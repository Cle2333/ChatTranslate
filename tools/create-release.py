"""创建 GitHub Release 并上传 MSI 安装包。

用法：
    GH_TOKEN=xxx python tools/create-release.py
    # 网络需要代理时（直连 GitHub 的 git/上传通道在某些网络下会被重置）：
    GH_TOKEN=xxx HTTPS_PROXY=http://127.0.0.1:PORT python tools/create-release.py
    # 只想先看会做什么、不实际发请求：
    GH_TOKEN=xxx python tools/create-release.py --dry-run

代理从 HTTPS_PROXY / HTTP_PROXY 环境变量读取；不设就直连。
"""
import json
import os
import sys
import urllib.request
import urllib.error

OWNER = "Cle2333"
REPO = "ChatTranslate"
TAG = "v0.1.1"
MSI = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "installer", "out", "ChatTranslate-Setup-0.1.1-x64.msi")

# 代理取自环境变量，不写死——写死会把本机的代理端口带进公开仓库
PROXY = (os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy")
         or os.environ.get("HTTP_PROXY") or os.environ.get("http_proxy") or "")

BODY = """面向本地 Ollama 模型设计的 Windows 划词翻译工具。翻译全部在本机完成，**全程离线**，不需要账号、不消耗额度。

## 本版新增

- **对话历史可归档** —— 侧边栏右键即可归档或删除；归档的对话进「已归档的对话」窗口，可随时恢复。
  归档不删数据，只从主列表里收起来。
- **划词长度上限 2000 → 20000**（设置里可改，200~500000）—— 原来超过 2000 字符直接拒绝翻译，
  复制一段长文就废了。现在超长文本自动按语义边界分段、逐段翻译，再按原文结构拼回去，
  衔接处一字不差；分段期间浮窗不会因误点外部而关闭。
- **确认框底部多出一截空白** 修复（FluentWindow 自带 320 DIP 最小高度）。
- 一轮代码评审（含静态分析与模型复核）报出的 18 个问题全部修复，其中影响使用的有：
  翻译进行中无法归档/删除**其它**对话、右键菜单作用到错误的那一行、
  长文分段后状态栏显示「上下文 14600 / 8192」、超长文本的预算没扣历史导致上下文被挤掉。

## 功能

- **划词翻译** —— 开启监听后选中文字即弹出译文，无需按快捷键
- **截图 OCR** —— 截图冻结 → 框选 → 系统 OCR 识别 → 译文替换原文
- **输入翻译** —— 对话式输入，支持多轮上下文
- **对话归档** —— 右键归档 / 删除，归档窗口里恢复
- **主题** —— 跟随系统 / 亮色 / 深色
- **本地模型切换** —— 主界面与设置页都能切换 Ollama 里已装的模型（7B / 1.8B 等）

## 安装

下载下面的 `ChatTranslate-Setup-0.1.1-x64.msi` 双击即可，会走标准安装向导
（欢迎 → 许可协议 → 选择安装位置 → 确认安装 → 完成）。

- 默认装到 `C:\\Program Files\\ChatTranslate\\`，安装位置可改
- 自动创建开始菜单与桌面快捷方式
- **在「设置 → 应用 → 已安装的应用」里可以正常卸载**，不留残留
- 安装包自带 .NET 运行时，目标机器不需要预装任何东西

## 使用前需要准备

1. **Ollama** —— https://ollama.com/download
2. **一个翻译模型**（二选一）
   ```bash
   # A. 直接拉社区包（最省事）
   ollama pull kaelri/hy-mt2:1.8b-q4_K_M
   ```
   ```bash
   # B. 用官方权重（推荐，模板可控）
   #    下载 https://modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-7B-GGUF 里的 Q4_K_M
   #    把仓库 tools/Modelfile 里的 FROM 改成该文件的绝对路径，然后：
   ollama create hy-mt2:7b-q4km -f tools/Modelfile
   ```
3. **系统 OCR 语言包**（仅截图 OCR 需要）
   设置 → 时间和语言 → 语言和区域 → 语言 → 添加语言 → 可选功能 → 勾选「光学字符识别」

## 7B 还是 1.8B？

本机（RTX 4060 Laptop 8GB）实测：

| | 1.8B (Q4_K_M) | 7B (Q4_K_M) |
|---|---|---|
| 速度 | **135.8 tok/s** | 47.8 tok/s |
| 显存 | **1.66 GB** | 5.41 GB |
| 质量 | 直译倾向明显 | 自然、会重排语序 |

差距只在"需要理解而非直译"的句子，例如成语 `道高一尺，魔高一丈。`：
7B 给 `The more the good rises, the higher the evil climbs.`，
1.8B 给 `When the path is one foot high, the demons are a hundred feet tall.`（把「道」当成了"路径"）。

**显存够就用 7B；要快或显存紧就 1.8B。** 界面里可随时切换，不用改配置。

## 环境要求

- Windows 10 1809+ / Windows 11
- Ollama + 上述模型
- 截图 OCR 需要系统装了对应语言的 OCR 包

## 欢迎提交 PR

这个项目的技术选择都是**为"本地模型"这个前提做的**，未必适合在线 API。
`Services/OllamaClient.cs` 是唯一与模型服务耦合的文件，换成 OpenAI 兼容端点需要改的地方
（鉴权、参数映射、SSE 流式、并发限流、API Key 存储）README 里有完整清单。

**欢迎提 Issue 讨论设计，或直接提 PR。** 用中文提完全没问题。

---

源码：[github.com/Cle2333/ChatTranslate](https://github.com/Cle2333/ChatTranslate) · 许可证：MIT
"""


def request(url, data=None, method="GET", content_type="application/json", token=""):
    """发 API 请求。代理未设时直连。"""
    handlers = [urllib.request.ProxyHandler(
        {"http": PROXY, "https": PROXY} if PROXY else {})]
    opener = urllib.request.build_opener(*handlers)
    headers = {
        "Authorization": f"token {token}",
        "Accept": "application/vnd.github+json",
        "User-Agent": "ChatTranslate-release-script",
    }
    if data is not None:
        headers["Content-Type"] = content_type
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    with opener.open(req, timeout=300) as r:
        body = r.read()
    return json.loads(body) if body else {}


def main() -> int:
    dry_run = "--dry-run" in sys.argv
    token = os.environ.get("GH_TOKEN", "").strip()
    if not token:
        print("缺少 GH_TOKEN 环境变量", file=sys.stderr)
        return 1
    if not os.path.exists(MSI):
        print(f"找不到安装包：{MSI}", file=sys.stderr)
        return 1

    print(f"仓库   : {OWNER}/{REPO}")
    print(f"标签   : {TAG}")
    print(f"安装包 : {MSI}")
    print(f"大小   : {os.path.getsize(MSI):,} 字节")
    print(f"代理   : {PROXY or '(直连)'}")
    if dry_run:
        print("\n--dry-run：以上为将要执行的内容，未发任何请求")
        return 0

    api = f"https://api.github.com/repos/{OWNER}/{REPO}"

    # ---------- 1) 创建 Release（tag 不存在时会自动创建）----------
    print("==> 1/3 创建 Release", TAG)
    payload = json.dumps({
        "tag_name": TAG,
        "target_commitish": "main",
        "name": f"{TAG} — 划词上限提升与对话归档",
        "body": BODY,
        "draft": False,
        "prerelease": False,
    }, ensure_ascii=False).encode("utf-8")

    try:
        rel = request(f"{api}/releases", data=payload, method="POST", token=token)
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        if e.code == 422 and "already_exists" in detail:
            print("    （该 tag 的 Release 已存在，改为读取现有 Release）")
            rel = request(f"{api}/releases/tags/{TAG}", token=token)
        else:
            print(f"    ✗ HTTP {e.code}: {detail[:400]}", file=sys.stderr)
            return 1

    print(f"    id       = {rel['id']}")
    print(f"    tag      = {rel['tag_name']}")
    print(f"    网页     = {rel['html_url']}")

    # ---------- 2) 上传 MSI ----------
    name = os.path.basename(MSI)
    size = os.path.getsize(MSI)
    print(f"==> 2/3 上传 {name}（{size:,} 字节）")

    # 已存在同名资产就先删掉，避免脚本不可重复执行
    for a in rel.get("assets", []):
        if a["name"] == name:
            print(f"    删除已存在的同名资产 id={a['id']}")
            request(f"{api}/releases/assets/{a['id']}", method="DELETE", token=token)

    with open(MSI, "rb") as f:
        blob = f.read()

    uploader = urllib.request.build_opener(
        urllib.request.ProxyHandler({"http": PROXY, "https": PROXY} if PROXY else {})
    )
    req = urllib.request.Request(
        f"https://uploads.github.com/repos/{OWNER}/{REPO}/releases/{rel['id']}/assets?name={name}",
        data=blob,
        method="POST",
        headers={
            "Authorization": f"token {token}",
            "Accept": "application/vnd.github+json",
            "Content-Type": "application/octet-stream",
            "User-Agent": "ChatTranslate-release-script",
        },
    )
    with uploader.open(req, timeout=1800) as r:
        asset = json.loads(r.read())
    print(f"    ✓ 已上传，远端大小 = {asset['size']:,} 字节")
    if asset["size"] != size:
        print(f"    ✗ 大小不符！本地 {size:,} / 远端 {asset['size']:,}", file=sys.stderr)
        return 1
    print(f"    下载地址 = {asset['browser_download_url']}")

    # ---------- 3) 回读核对 ----------
    print("==> 3/3 回读核对")
    check = request(f"{api}/releases/tags/{TAG}", token=token)
    print(f"    tag_name  = {check['tag_name']}")
    print(f"    资产数    = {len(check['assets'])}")
    for a in check["assets"]:
        print(f"      · {a['name']}  {a['size']:,} 字节  下载数 {a['download_count']}")
    print(f"    Release 网页 = {check['html_url']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
