# ChatTranslate

本地优先的划词翻译 / 截图 OCR / 输入翻译工具。翻译由本机 Ollama 运行的 **Hy-MT2** 模型完成，全程离线，不经过任何第三方服务。

## 功能

- **划词翻译**：开启监听开关后，选中文字即弹出译文（无需按快捷键）
- **截图 OCR**：截图冻结 → 框选 → 文字识别 → 译文替换原文
- **输入翻译**：对话式输入，支持多轮上下文

## 技术栈

| 层 | 选型 |
|---|---|
| 框架 | C# / WPF，目标框架 `net8.0-windows10.0.19041.0` |
| OCR | `Windows.Media.Ocr`（系统内置，同进程调用，零外部依赖） |
| 翻译 | 本地 Ollama + Hy-MT2（7B） |
| 取词 | UI Automation `TextPattern` 优先，回退模拟 `Ctrl+C` |

目标框架选 `net8.0-windows10.0.19041.0` 而不是普通的 `net8.0-windows`，是为了能直接调用 WinRT API——这让 OCR 变成一次普通的函数调用，而不是起一个子进程再序列化结果。

## 环境要求

- Windows 10 1809+ / Windows 11
- .NET 8 SDK（开发）；仅运行框架依赖版时需要 .NET 8 桌面运行时
- Ollama，以及 Hy-MT2 模型（见下）
- 系统 OCR 语言包：设置 → 时间和语言 → 语言和区域 → 语言 → 添加语言 → 可选功能 → 勾选「光学字符识别」

## 模型准备

有两条路，都可行。**推荐 B**——用官方权重，模板可控。

### A. 直接从 Ollama 拉社区包（最省事）

```bash
ollama pull kaelri/hy-mt2:1.8b-q4_K_M
```

### B. ModelScope 下载官方 GGUF 后本地导入（推荐）

```bash
# 1. 下载官方 GGUF（腾讯混元，约 4.6 GB）
curl -L -C - -o Hy-MT2-7B-Q4_K_M.gguf \
  "https://modelscope.cn/api/v1/models/Tencent-Hunyuan/Hy-MT2-7B-GGUF/repo?Revision=master&FilePath=Hy-MT2-7B-Q4_K_M.gguf"

# 2. 把 tools/Modelfile 里的 FROM 改成该 GGUF 的绝对路径，然后导入
ollama create hy-mt2:7b-q4km -f tools/Modelfile
```

可用档位：`Q4_K_M` 4.62 GB / `Q6_K` 6.16 GB / `Q8_0` 7.98 GB。

> **为什么选 B**：Ollama 上**没有腾讯官方账号**，现存的 hy-mt2 包都是社区转包；
> 走 ModelScope 拿到的是**官方账号发布的权重**，且能从 GGUF 里读出权威的 chat template。
>
> 控制 token（`<|startoftext|>` / `<|extra_0|>` / `<|eos|>`）由 GGUF 内嵌的官方 Jinja 模板处理，
> **无需手写 `TEMPLATE`**。若照抄社区 Ollama 包的模板反而会出错——它们用的是另一套 token。

## 构建

```bash
dotnet build src/ChatTranslate/ChatTranslate.csproj -c Debug -p:Platform=x64
```

## 发布（单文件）

```bash
dotnet publish src/ChatTranslate/ChatTranslate.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

产物为单个 `ChatTranslate.exe`，解压即用，目标机器无需安装 .NET 运行时。

## 目录结构

```
src/ChatTranslate/
├── Core/          # 底层能力：取词、截图、OCR、热键、剪贴板
├── Data/          # 配置、SQLite 会话存储、数据模型
├── Services/      # Ollama 客户端、语言表、翻译编排
└── Views/         # 界面：主窗、设置窗、全屏框选层
```

## 设计要点

- **剪贴板保护**：取词的回退路径会占用剪贴板，因此做已知格式的备份还原。刻意**不**枚举未知的 vendor 私有格式——Office 等应用会把指向自身内部数据的指针放进剪贴板，替换再还原会使指针悬空，崩溃的是对方进程。
- **取词双路**：优先 UI Automation（不产生副作用），失败才模拟 `Ctrl+C`。用剪贴板序号变化判断复制是否真的发生，避免把剪贴板里的旧内容当成选中文本。
- **DPI 感知**：`app.manifest` 声明 PerMonitorV2。缺少它的话，在多显示器或高缩放比下截图与窗口定位都会偏移。
- **配置与数据位置**：一律写 `%APPDATA%\ChatTranslate\`，**绝不写程序目录**——单文件 exe 可能被放在只读目录下。
- **模型常驻**：请求体带 `keep_alive`（默认 `30m`），否则按 Ollama 默认的 5 分钟计时，模型被卸载后下一次翻译要先等它载回显存（实测 7.05 s）。模型当前不在显存里时，界面会把「生成中」显示成「模型加载中…」。
- **强调色固定为品牌蓝** `#0A84F4`，不跟随 Windows 系统强调色——否则用户气泡与选中项的配色会随机器而变（见 `Core/AppTheme.cs`）。

## 开发阶段

| 阶段 | 内容 | 状态 |
|---|---|---|
| P0 | 底层能力验证（OCR / 取词 / 热键 / 剪贴板 / 翻译 / 单文件发布） | 已完成 |
| P1 | 会话式翻译核心（输入翻译 + 会话存储 + 主界面 + 语言选框 + 截图 OCR） | 已完成 |
| P2 | 划词翻译（被动监听 + 浮窗） | 已完成 |
| P3 | 截图 OCR 译文替换渲染 | 已完成 |
| P4 | 打磨（模型常驻 / 加载提示 / 主题配色） | 进行中 |

## 界面说明

- **侧边栏**：翻译历史（点击切换会话）· 划词翻译开关 · 设置
- **主区**：对话气泡（用户右侧 / 译文左侧）；截图 OCR 的用户气泡直接显示截图
- **输入区上方**：输入语言（含「自动检测」）· 目标语言 · 互换按钮
- **状态栏**（右对齐）：语言对 · 版本 · Ollama 状态 · 输出速度 · 上下文用量

### 语言与自动换向

输入语言为「自动检测」时：若检测出的原文语言与目标语言相同，会**自动译为另一种**（中↔英），
并在语言选框右侧给出提示。这是为了避免"把中文翻成中文"这种空转。

也可以显式指定输入语言——显式指定时只按所选执行，不做任何自动换向。

