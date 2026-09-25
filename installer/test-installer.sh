#!/usr/bin/env bash
#
# 安装包的安装 / 卸载往返测试。
#
#   用法：./installer/test-installer.sh
#
# 做的事：用同一份 .wxs 构建一个 perUser 变体（装到 LocalAppData、写 HKCU，
#         不需要管理员），然后**真的装一遍、验一遍、卸一遍、再验一遍**。
#
# 为什么用 perUser 而不是正式包的 perMachine：
#   本机跑脚本时往往没有管理员权限，perMachine 会以 1925 失败。
#   MSI 的安装/卸载机制（组件解析、文件落地、快捷方式、ARP 登记、卸载清理）
#   两种 scope 完全相同，只是根目录与注册表根不同，所以 perUser 足以验证流程。
#
# 正式包请用 ./installer/build-installer.sh（perMachine）。
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if command -v cygpath >/dev/null 2>&1; then
  ROOT="$(cygpath -m "$ROOT")"
fi

export PATH="/c/Program Files/dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

VERSION="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$ROOT/src/ChatTranslate/ChatTranslate.csproj" | head -1)"
[[ -n "$VERSION" ]] || { echo "拿不到版本号" >&2; exit 1; }

TEST_DIR="$ROOT/installer/out/test"
MSI="$TEST_DIR/ChatTranslate-test-perUser-x64.msi"
LOG_INSTALL="$TEST_DIR/install.log"
LOG_UNINSTALL="$TEST_DIR/uninstall.log"
ROOT_WIN="$(cygpath -w "$ROOT")"

echo "== 安装/卸载往返测试（perUser，不需要管理员）=="
echo "版本：$VERSION"

# 前置：Release 产物必须在
if [[ ! -f "$ROOT/publish/ChatTranslate.exe" ]]; then
  echo "错误：找不到 $ROOT/publish/ChatTranslate.exe，请先跑 installer/build-installer.sh" >&2
  exit 1
fi

# 清掉上一轮的产物。删不掉就沿用（可能被某个 shell 的工作目录占着，
# Windows 不允许删除被占用的目录）；wix 与 msiexec 都会覆盖同名文件，
# 所以沿用不会污染结果。
mkdir -p "$TEST_DIR"
rm -rf "$TEST_DIR"/* 2>/dev/null || true

# ---------- 1) 构建 perUser 测试包 ----------
echo
echo "==> 1/6 构建 perUser 测试包"
wix build "$ROOT/installer/ChatTranslate.wxs" \
  -arch x64 \
  -ext WixToolset.UI.wixext \
  -culture zh-CN \
  -d "Version=$VERSION" \
  -d "SourceRoot=$ROOT_WIN" \
  -d "Scope=perUser" \
  -o "$MSI"
echo "    $(stat -c%s "$MSI") 字节"

# ProductCode 每次构建都不同，卸载时要按 MSI 文件走，不要按产品码硬编码
echo
echo "==> 2/6 读 ProductCode"
PRODUCT_CODE="$(powershell -NoProfile -Command "
  \$db=(New-Object -ComObject WindowsInstaller.Installer).GetType().InvokeMember('OpenDatabase','InvokeMethod',\$null,(New-Object -ComObject WindowsInstaller.Installer),@('$(cygpath -w "$MSI")',0))
  \$v=\$db.GetType().InvokeMember('OpenView','InvokeMethod',\$null,\$db,@(\"SELECT \`\`Value\`\` FROM \`\`Property\`\` WHERE \`\`Property\`\`='ProductCode'\"))
  \$v.GetType().InvokeMember('Execute','InvokeMethod',\$null,\$v,\$null)
  \$r=\$v.GetType().InvokeMember('Fetch','InvokeMethod',\$null,\$v,\$null)
  \$r.GetType().InvokeMember('StringData','GetProperty',\$null,\$r,@(1))
" 2>/dev/null | tr -d '\r\n')"
echo "    $PRODUCT_CODE"

# ---------- 3) 安装 ----------
echo
echo "==> 3/6 安装"
msiexec.exe /i "$(cygpath -w "$MSI")" /qn /l*v "$(cygpath -w "$LOG_INSTALL")" || {
  echo "安装失败（退出码 $?），日志尾部：" >&2
  tail -20 "$LOG_INSTALL" >&2
  exit 1
}
echo "    msiexec 成功返回"

INSTALL_DIR="$LOCALAPPDATA/ChatTranslate"
LNK_START="$APPDATA/Microsoft/Windows/Start Menu/Programs/ChatTranslate"
LNK_DESKTOP="$USERPROFILE/Desktop"

echo
echo "==> 4/6 验证安装结果"
fail=0
chk() { # 描述 路径
  if [[ -e "$2" ]]; then echo "    ✓ $1"; else echo "    ✗ $1  ← 不存在：$2"; fail=1; fi
}
chk "程序文件已落地"      "$INSTALL_DIR/ChatTranslate.exe"
chk "开始菜单文件夹"      "$LNK_START"
chk "开始菜单快捷方式"    "$LNK_START/ChatTranslate.lnk"
chk "桌面快捷方式"        "$LNK_DESKTOP/ChatTranslate.lnk"

# 载荷大小应与 publish 一致
if [[ -f "$INSTALL_DIR/ChatTranslate.exe" ]]; then
  A=$(stat -c%s "$INSTALL_DIR/ChatTranslate.exe")
  B=$(stat -c%s "$ROOT/publish/ChatTranslate.exe")
  if [[ "$A" == "$B" ]]; then echo "    ✓ 载荷大小一致（$A 字节）"; else echo "    ✗ 载荷大小不符：装出来 $A / 源 $B"; fail=1; fi
fi

# 查 ARP 登记（「设置 → 应用」里看到的卸载入口）。
# ★ 必须把四个位置都查一遍：登记落在 HKLM 还是 HKCU，
#   取决于安装是否提升以及机器策略——只查 HKCU 会得出"没登记"的错误结论。
ARP_PROBE='
$roots = @(
  "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
  "HKCU:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
  "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
foreach ($r in $roots) {
  if (-not (Test-Path $r)) { continue }
  foreach ($k in (Get-ChildItem $r -ErrorAction SilentlyContinue)) {
    $p = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
    if ($p.DisplayName -like "*ChatTranslate*") {
      Write-Output ("FOUND|" + $p.DisplayName + "|" + $p.DisplayVersion + "|" + $p.Publisher + "|" + $r)
      exit
    }
  }
}
Write-Output "NONE"
'

echo "    检查 ARP 登记…"
ARP="$(powershell -NoProfile -Command "$ARP_PROBE" 2>/dev/null | tr -d '\r\n')"
if [[ "$ARP" == FOUND* ]]; then
  echo "    ✓ 已出现在应用列表，卸载入口可用:"
  echo "        $ARP"
else
  echo "    ✗ 未在应用列表里登记 → 用户将无法从「设置」卸载"; fail=1
fi

# ---------- 5) 卸载 ----------
echo
echo "==> 5/6 卸载"
msiexec.exe /x "$(cygpath -w "$MSI")" /qn /l*v "$(cygpath -w "$LOG_UNINSTALL")" || {
  echo "卸载失败（退出码 $?），日志尾部：" >&2
  tail -20 "$LOG_UNINSTALL" >&2
  exit 1
}
echo "    msiexec 成功返回"

# ---------- 6) 验证卸载干净 ----------
echo
echo "==> 6/6 验证卸载是否干净"
gchk() { # 描述 路径（应当不存在）
  if [[ ! -e "$2" ]]; then echo "    ✓ $1 已清除"; else echo "    ✗ $1 仍残留：$2"; fail=1; fi
}
gchk "程序文件"        "$INSTALL_DIR/ChatTranslate.exe"
gchk "安装目录"        "$INSTALL_DIR"
gchk "开始菜单文件夹"  "$LNK_START"
gchk "桌面快捷方式"    "$LNK_DESKTOP/ChatTranslate.lnk"

ARP2="$(powershell -NoProfile -Command "$ARP_PROBE" 2>/dev/null | tr -d '\r\n')"
if [[ "$ARP2" == NONE ]]; then echo "    ✓ 应用列表里的登记已清除"; else echo "    ✗ 应用列表里仍有残留：$ARP2"; fail=1; fi

echo
if [[ "$fail" == 0 ]]; then
  echo "✅ 全部通过：安装 → 验证 → 卸载 → 验证，全部符合预期"
  rm -rf "$TEST_DIR"/* 2>/dev/null || true
  exit 0
else
  echo "❌ 有检查项未通过（日志留在 $TEST_DIR）"
  exit 1
fi
