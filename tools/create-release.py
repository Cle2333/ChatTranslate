"""创建 GitHub Release 并上传免安装 zip 包。

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
TAG = "v0.1.3"
ASSET = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                     "release", "ChatTranslate-0.1.3-win-x64.zip")

# 代理取自环境变量，不写死——写死会把本机的代理端口带进公开仓库
PROXY = (os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy")
         or os.environ.get("HTTP_PROXY") or os.environ.get("http_proxy") or "")

BODY = """本地 Ollama + DeepSeek 云端 API 的 Windows 划词翻译工具。默认使用本机 Ollama，零配置、离线可用；也可以在设置里切到 DeepSeek，切到云端后待翻译文本会发送到 DeepSeek。

## 本版新增

- **DeepSeek 云端 API** —— 设置里切换服务商，填 API Key 与 `deepseek-chat` / `deepseek-reasoner` 等模型名即可。Key 用当前 Windows 用户的 DPAPI 加密保存，界面不回显；请求走 SSE 流式，并带 `max_tokens` 输出上限。
- **Ollama 卡死自动恢复** —— 本机 Ollama 进程还在但连续 15 秒无响应时，自动结束并重新启动；设置 → 服务里也新增了「重启 Ollama」按钮。启动未满 8 分钟且从未响应过时视为 GPU 探测初始化，不会误杀。
- **发布包更整洁** —— 程序收进 `ChatTranslate\\` 子文件夹，整个文件夹解压即用。

## 下载

下载 `ChatTranslate-0.1.3-win-x64.zip`，解压后运行 `ChatTranslate\\ChatTranslate.exe`。

- 自带 .NET 运行时，目标机器不需要预装任何东西
- 配置与会话数据写在 `%APPDATA%\\ChatTranslate\\`，不写程序目录
- 整个 `ChatTranslate` 文件夹一起移动，不要只拷 exe——框架 DLL 必须与它同目录

## 使用前需要准备

**本地模式**（默认）：

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

**云端模式**：DeepSeek API Key，不需要安装 Ollama。

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

## 欢迎提交 PR

`Services/OllamaClient.cs` 与 `Services/DeepSeekClient.cs` 是两个模型服务入口，都实现同一套 `ITranslationClient` 契约；新增其他 OpenAI 兼容端点时可以照着 `DeepSeekClient` 切。鉴权、参数映射、SSE 流式、API Key 存储的边界在 README 与 `AGENTS.md` 里有说明。

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
    if not os.path.exists(ASSET):
        print(f"找不到发布包：{ASSET}", file=sys.stderr)
        return 1

    print(f"仓库   : {OWNER}/{REPO}")
    print(f"标签   : {TAG}")
    print(f"发布包 : {ASSET}")
    print(f"大小   : {os.path.getsize(ASSET):,} 字节")
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
        "name": f"{TAG} — 本地 Ollama 与 DeepSeek 云端",
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

    # ---------- 2) 上传 zip ----------
    name = os.path.basename(ASSET)
    size = os.path.getsize(ASSET)
    print(f"==> 2/3 上传 {name}（{size:,} 字节）")

    # 已存在同名资产就先删掉，避免脚本不可重复执行
    for a in rel.get("assets", []):
        if a["name"] == name:
            print(f"    删除已存在的同名资产 id={a['id']}")
            request(f"{api}/releases/assets/{a['id']}", method="DELETE", token=token)

    with open(ASSET, "rb") as f:
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