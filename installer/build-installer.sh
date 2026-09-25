#!/usr/bin/env bash
#
# 构建 ChatTranslate 的 MSI 安装包。
#
#   用法：./installer/build-installer.sh [版本号]
#         ./installer/build-installer.sh 0.1.0
#
# 不传版本号就从 csproj 里读 <Version>，避免两处各写一遍。
#
# 前置：dotnet SDK 8、WiX v5（dotnet tool install --global wix --version "5.*"）
# 产物：installer/out/ChatTranslate-Setup-<版本>-x64.msi
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# ★ dotnet / wix 是原生 Windows 程序，不认 MSYS 路径：
#   传 /c/Users/... 给它，会被当成相对路径，拼出 C:\c\Users\... 这种影子路径然后失败。
#   cygpath -m 给出 C:/Users/... 形式，bash 与原生程序都能用。
if command -v cygpath >/dev/null 2>&1; then
  ROOT="$(cygpath -m "$ROOT")"
fi

CSPROJ="$ROOT/src/ChatTranslate/ChatTranslate.csproj"
PUBLISH_DIR="$ROOT/publish"
OUT_DIR="$ROOT/installer/out"

# dotnet 与 wix 可能不在当前 PATH 里（尤其是从 MSYS / git-bash 里调用时）
export PATH="/c/Program Files/dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# ---------- 版本号 ----------
if [[ $# -ge 1 ]]; then
  VERSION="$1"
else
  # 取第一个 <Version> 的值；csproj 里只有一处。
  # 用 sed 而不是 grep -P：git-bash 的 grep 不保证带 PCRE 支持。
  VERSION="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$CSPROJ" | head -1)"
fi
if [[ -z "${VERSION:-}" ]]; then
  echo "错误：拿不到版本号，请显式传入，例如 ./installer/build-installer.sh 0.1.0" >&2
  exit 1
fi
echo "版本：$VERSION"

# ---------- 1) Release 单文件发布 ----------
echo
echo "==> 1/3 发布 Release 单文件版"
rm -rf "$PUBLISH_DIR"
dotnet publish "$CSPROJ" -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -o "$PUBLISH_DIR"

if [[ ! -f "$PUBLISH_DIR/ChatTranslate.exe" ]]; then
  echo "错误：发布产物里没有 ChatTranslate.exe" >&2
  exit 1
fi
echo "    ChatTranslate.exe = $(stat -c%s "$PUBLISH_DIR/ChatTranslate.exe") 字节"

# 单文件发布应当只产出这一个文件；多了就说明打包配置变了，需要同步改 .wxs
EXTRA="$(find "$PUBLISH_DIR" -maxdepth 1 -type f ! -name 'ChatTranslate.exe' | wc -l)"
if [[ "$EXTRA" -ne 0 ]]; then
  echo "警告：publish 目录里有 $EXTRA 个额外文件，.wxs 只登记了 ChatTranslate.exe，它们不会被装进 MSI：" >&2
  find "$PUBLISH_DIR" -maxdepth 1 -type f ! -name 'ChatTranslate.exe' >&2
fi

# ---------- 2) 编译 MSI ----------
echo
echo "==> 2/4 编译 MSI"
mkdir -p "$OUT_DIR"
MSI_NAME="ChatTranslate-Setup-${VERSION}-x64.msi"

# 许可协议页要 RTF，而 RTF 是从 LICENSE 生成的；在这里重跑一遍，
# 免得改了 LICENSE 忘了同步 RTF（许可页显示的就会是旧文本）
if command -v python3 >/dev/null 2>&1; then
  python3 "$ROOT/tools/make-license-rtf.py" | sed 's/^/    /'
else
  echo "    跳过 RTF 生成（没有 python3），沿用现有的 installer/LICENSE.rtf"
fi

# -arch x64：程序是 x64，装到 Program Files 而不是 Program Files (x86)
# -ext WixToolset.UI.wixext：安装向导的对话框集（不加的话 MSI 没有界面，双击就直接装完）
# -culture zh-CN：向导界面用中文
# -d Version / -d SourceRoot：把版本号与仓库根注入 .wxs（避免写死路径，见 .wxs 里的说明）
# -d Scope=perMachine：正式包。装进 Program Files 并出现在「应用和功能」里，
#                      需要管理员；perUser 那一档只给测试用，见 installer/test-installer.sh
ROOT_WIN="$(cygpath -w "$ROOT")"
wix build "$ROOT/installer/ChatTranslate.wxs" \
  -arch x64 \
  -ext WixToolset.UI.wixext \
  -culture zh-CN \
  -d "Version=$VERSION" \
  -d "SourceRoot=$ROOT_WIN" \
  -d "Scope=perMachine" \
  -o "$OUT_DIR/$MSI_NAME"

# ---------- 3) 校验 ----------
echo
echo "==> 3/3 校验 MSI"
ls -la "$OUT_DIR/$MSI_NAME"
echo
echo "完成：$OUT_DIR/$MSI_NAME"
echo
echo "可以在「设置 → 应用 → 已安装的应用」里卸载。"
echo "静默安装：msiexec /i \"$MSI_NAME\" /qn"
