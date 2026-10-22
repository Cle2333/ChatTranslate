# AGENTS.md — ChatTranslate

本文件是这个仓库给 AI 编码 agent 的**唯一真相源**（项目是什么、边界在哪、改哪里、哪些行为不能碰、怎么验证）。

阅读顺序建议：先看 §1–§3 建立地图，动手前看 §6 不变量与 §8 已知坑。

优先级：**代码 > 本文件 > README**。README 面向最终用户（安装、模型准备、功能说明）；本文件面向改代码的人。发现两者矛盾时以实际代码为准，**并回头修本文件**——不要留一份过期地图。

---

## 1. 项目定位与边界

Windows 桌面翻译工具，三条入口合一：

| 入口 | 行为 |
|---|---|
| 划词翻译 | 打开监听开关后，选中文字即弹浮窗译文，不需要按快捷键 |
| 截图 OCR | 全局热键截图 → 冻结画面 → 框选 → 系统 OCR → 译文替换原文 |
| 输入翻译 | 对话式输入框，多轮上下文累积 |

翻译**全部由本机 Ollama 模型完成**（默认 `hy-mt2:7b-q4km`），全程离线，不经过任何第三方服务。这不是可选的实现细节，而是产品前提：

- 没有账号、没有额度、没有计费概念；
- 内容不出本机；
- 因此**没有鉴权层、没有并发控制、没有重试退避**——这些缺失是设计后果，不是遗漏。补它们属于 P5（见 §11）。

**当前非目标**：跨平台（OCR 绑死 `Windows.Media.Ocr`）、云同步、插件体系、多引擎并存（P5 待做）。

仓库已开源：`https://github.com/Cle2333/ChatTranslate`，MIT。主线分支 `main`。

---

## 2. 技术栈与硬约束

| 层 | 选型 | 出处 |
|---|---|---|
| 框架 | C# / WPF，`net8.0-windows10.0.19041.0` | `src/ChatTranslate/ChatTranslate.csproj` |
| UI 库 | WPF-UI 4.3.0（Fluent 2） | 同上 `PackageReference` |
| 存储 | Microsoft.Data.Sqlite 8.0.10（单文件库） | 同上 |
| OCR | `Windows.Media.Ocr`（系统内置，同进程） | `Core/OcrService.cs` |
| 翻译 | 本地 Ollama HTTP API（NDJSON 流） | `Services/OllamaClient.cs` |
| 取词 | UI Automation `TextPattern` 优先，回退模拟 `Ctrl+C` | `Core/SelectionGrabber.cs` |

### 硬约束（改动时必须保持）

- **目标框架不能"简化"成 `net8.0-windows`。** `10.0.19041.0` 后缀是让 WinRT API 直接可用的前提（csproj 里有注释说明），去掉后 OCR 会退化成起子进程再序列化。`SupportedOSPlatformVersion` 同步为 `10.0.19041.0`。
- **只支持 x64**：`<Platforms>x64</Platforms>`、`<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`、MSI 用 `-arch x64`（否则装进 `Program Files (x86)`）。
- **需要 .NET SDK 8**（`installer/build-installer.sh` 的前置注释）。
- `Nullable` / `ImplicitUsings` 均开启，`AllowUnsafeBlocks` 开启（P/Invoke 与钩子需要）。新代码跟着开着写，不要关。
- **没有测试工程**：`src/` 下只有 `ChatTranslate` 一个项目，`ChatTranslate.sln` 只映射这一个 csproj。验证靠「构建通过 + 手工运行 + `installer/test-installer.sh` 往返」。想加测试项目请先与维护者确认，不要顺手引入。
- **没有 DI 容器、没有 MVVM 框架**（无 CommunityToolkit.Mvvm / Microsoft.Extensions.DependencyInjection）。逻辑主要写在 code-behind，`Views/ViewModels.cs` 只是轻量数据结构。改功能时**沿用现有风格**，不要为了"更干净"引入框架——那会把一次修复变成一次重写。
- 界面文案是**中文硬编码在 XAML 里**，没有 `.resx` 本地化资源。改文案时留意 README 的「界面说明」一节是否需要同步。

---

## 3. 目录结构与改动落点

```
src/ChatTranslate/
├── Assets/          # 应用图标：app.ico（多尺寸 16~256）+ 各尺寸 PNG
├── Core/            # 底层能力，尽量不依赖业务
├── Data/            # 配置、SQLite 会话库、数据模型
├── Services/        # Ollama 客户端、语言表、翻译编排  ← 多引擎改造看这里
└── Views/           # 界面与交互
installer/           # WiX 安装包定义、构建脚本、往返测试脚本
tools/               # Modelfile（模型导入）、图标/许可 RTF 生成、Release 发布脚本
docs/                # README 用图
```

### 改动落点判定

| 要改什么 | 去哪 |
|---|---|
| 「能不能取到词」/ 钩子 / 热键 / 剪贴板 | `Core/`（`SelectionWatcher` 是划词主流程，`TranslationRenderer` 是 OCR 排版最密的地方） |
| 「翻得对不对 / 快不快」/ 模型服务 | `Services/`（`OllamaClient` 是**唯一**与模型服务耦合的文件，多引擎改造从这里切接口） |
| 界面显示 | `Views/`（`MainWindow.xaml.cs` 是主逻辑汇集处，改主界面基本都要动它） |
| 配置项 | `Data/AppConfig.cs` —— **三处都要动**：属性 + `Normalize()` + 设置窗界面 |
| 所有路径 | 只在 `Data/AppConfig.cs` 的 `AppPaths` 里定义，别处不许自己拼 |

**判定规则**：改「能不能取到词」→ `Core/`；改「翻得对不对/快不快」→ `Services/`；改「界面怎么显示」→ `Views/`；改「配置项」→ `Data/AppConfig.cs`（三处都要动：属性 + `Normalize()` + 设置窗界面）。

---

## 4. 常用命令

> 命令都是从 README / 脚本里抄出的**真实出处**，未在本仓库重新验证。不要凭空改写参数——`-p:IncludeNativeLibrariesForSelfExtract=true`、`-d Scope=` 这类参数缺一个就产出坏包。

### 构建

```bash
dotnet build src/ChatTranslate/ChatTranslate.csproj -c Debug -p:Platform=x64
```

- 出处：README「构建」。
- `-p:Platform=x64` 不是可省的：csproj 只声明 `<Platforms>x64</Platforms>`，而 `ChatTranslate.sln` 只映射 `Any CPU`（见 `.sln` 的 `ProjectConfigurationPlatforms`）。**直接 `dotnet build ChatTranslate.sln` 是否可行未实测**（见 §12）。

### 单文件发布（免安装版）

```bash
dotnet publish src/ChatTranslate/ChatTranslate.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

- 出处：README「发布 → 单文件」。
- 产物必须是**单个** `ChatTranslate.exe`。多出文件说明发布配置变了，`.wxs` 只登记了这一个 exe，装进 MSI 的东西会缺。

### MSI 安装包

```bash
dotnet tool install --global wix --version "5.*"
wix extension add -g "WixToolset.UI.wixext/5.0.2"
./installer/build-installer.sh            # 版本号从 csproj 读
./installer/build-installer.sh 0.1.3      # 或显式指定
```

- 出处：README「发布 → MSI 安装包」+ `installer/build-installer.sh` 头部注释。
- 脚本做的事：Release 单文件发布 → 重生成 `installer/LICENSE.rtf` → `wix build`（注入 `Version` / `SourceRoot` / `Scope=perMachine`）→ 校验产物。
- 产物：`installer/out/ChatTranslate-Setup-<版本>-x64.msi`。
- **不要直接调 `wix build`**——会跳过 publish，打包到旧的 exe（`.wxs` 注释里明确写了）。

### 安装/卸载往返测试（不需要管理员）

```bash
./installer/build-installer.sh      # 先有 publish/ChatTranslate.exe
./installer/test-installer.sh
```

- 出处：`installer/test-installer.sh` 头部注释。
- 用同一份 `.wxs` 建 `perUser` 变体，真的装 → 验 → 卸 → 验（程序文件、开始菜单、桌面快捷方式、ARP 登记、载荷字节数一致）。
- 改过 `.wxs`、组件、快捷方式、卸载登记，**必须跑这个**。

### 发 Release

```bash
GH_TOKEN=xxx python tools/create-release.py --dry-run   # 先看要做什么
GH_TOKEN=xxx python tools/create-release.py
```

- 出处：`tools/create-release.py` 文档字符串。
- 需要代理时用 `HTTPS_PROXY` 环境变量（脚本从环境变量读，不写死——写死会把本机代理端口带进公开仓库）。

### 路径纪律（本机 bash 环境）

`dotnet` / `wix` / `python` 是原生 Windows 程序，**不认 MSYS 路径**。传 `/c/Users/...` 会被当成相对路径，在当前目录下拼出 `C:\c\Users\...` 影子目录然后失败。给它们一律传 `C:/Users/...` 正斜杠形式；在 bash 脚本里用 `cygpath -m` 转换（两个 installer 脚本就是这么处理的）。

---

## 5. 三条运行链路

**输入翻译**：`MainWindow` 输入框 → `TranslationService`（组 prompt + 历史上下文裁剪）→ `OllamaClient.ChatAsync(stream: true)` → 流式回调刷新气泡 → 完成后写 `ChatStore` → 状态栏更新速度/上下文用量。

**划词翻译**：`MouseHook`（左键抬起）→ `SelectionWatcher`（防抖，默认 350 ms）→ `SelectionRuleEngine`（黑名单/形态过滤）→ `SelectionGrabber`（UIA 优先，回退 `Ctrl+C` + `ClipboardBackup` 保护）→ 长度上限判定 → `TranslatePopup` 流式显示 → 超长则 `TextChunker` 分段、逐段翻译后按原文结构拼回。

**截图 OCR**：全局热键 → `ScreenCapture` 冻结画面 → `CaptureOverlay` 框选 → `OcrService` 识别 → `TranslationService` 翻译 → `TranslationRenderer` 把译文按原位排版覆盖原文 → `ResultWindow`。

三条链路共用 `TranslationService` 和 `OllamaClient`。**任何一条链路的翻译路径都不要绕过 `TranslationService`**——上下文预算、分段、指标统计都在里面，绕过去就会出现"某条链路翻长文会截断"这类难查的不一致。

---

## 6. 必须保持的不变量

这些行为都有过真实故障支撑（注释里写了为什么）。改代码时**不要"优化"掉**：

| 不变量 | 原因 | 位置 |
|---|---|---|
| 请求体必带 `keep_alive`（默认 `30m`） | Ollama 默认 5 分钟卸载，下次翻译要重新载入模型（实测 7.05 s，期间界面无反馈） | `OllamaClient.BuildPayload` |
| 请求体必带 `num_predict`（`MaxOutputTokens = 4096`） | 不设上限时没有任何终止条件：llama.cpp 上下文写满后 context shift 继续生成，模型复读时实测 20 万字符仍未结束，界面永远「生成中」 | `OllamaClient.MaxOutputTokens` |
| 达到输出上限必须提示「可能不完整」 | Ollama 返回 `done_reason="length"`；不提示就等于给用户一段看起来完整、实际断尾的译文 | `OllamaClient.ChatReply` → 界面 |
| **不注入 system 消息** | Hy-MT2 官方明确没有默认 system_prompt；控制 token 由 GGUF 内嵌 Jinja 模板处理。prompt 只有一条 user 消息 | `OllamaClient.BuildTranslatePrompt`、`tools/Modelfile` 注释 |
| `keep_alive` 的形态：时长串发**字符串**，秒数与负数（含 `-1`）发**数字** | Ollama 用 Go `time.ParseDuration` 解析字符串，`"600"` / `"-1"` 都不是合法 duration，会 400。解析与发送必须共用 `AppConfig.TryParseKeepAlive`（单一真源） | `AppConfig.TryParseKeepAlive` / `OllamaClient` 构造器 |
| 单实例互斥体 `Local\ChatTranslate.SingleInstance` | 两个实例会抢全局热键与钩子 | `App.xaml.cs` `OnStartup` |
| 新增配置项**必须带默认值**，非法值走 `Normalize()` 收敛 | 配置是明文 JSON，用户会手改；缺字段/越界/`null` 会让启动路径崩 | `Data/AppConfig.cs` |
| 配置**读取失败**（IO/权限）不覆盖原文件；只有 **JSON 解析失败**才备份 `.bad` 并回落默认值 | 一次瞬时权限失败就把用户配好的地址/模型/快捷键换成默认值，不可逆 | `ConfigStore.Load` |
| 所有可写数据只落 `%APPDATA%\ChatTranslate\`，**绝不写程序目录** | 单文件 exe 可能放在只读目录（如 `Program Files` 下） | `AppPaths` |
| 剪贴板只备份/还原**已知格式**，不枚举 vendor 私有格式 | Office 等会把指向自身内部数据的指针放进剪贴板，替换再还原会让指针悬空，崩的是对方进程 | `ClipboardBackup` |
| 取词 UIA 优先、`Ctrl+C` 回退，且用剪贴板序号变化判断复制是否发生 | 避免把剪贴板里的旧内容当成选中文本 | `SelectionGrabber` |
| 数据目录不可写时退 `:memory:` 内存库，界面照常可用并说明原因 | 安全软件拦截/只读目录下，"起不来"比"本次不留历史"糟得多 | `ChatStore` |
| 模型清单失败**不清空**，配置的模型必然在首位 | 一次探测失败把用户配好的模型从下拉抹掉，是最难解释的"设置不见了"；`ComboBox` 绑不上会显示空白 | `ModelCatalog` |
| 日志写失败静默（`AppLog` 永不抛）；崩溃日志写不进数据目录就退 `%TEMP%\ChatTranslate-logs` | 日志不能反过来打断主流程；但崩溃时必须留下堆栈 | `Core/AppLog.cs`、`App.xaml.cs` |
| 致命异常（OOM / AccessViolation / StackOverflow）**不吞**，其余可恢复异常弹框后置 `Handled = true` | 吞掉致命异常会进入"弹窗→异常→再弹窗"循环，还可能留下半写的文件与不一致的数据库 | `App.xaml.cs` |
| `app.manifest` 必须 `PerMonitorV2` | 缺了它，多显示器/高缩放下截图与窗口定位会整体偏移 | `app.manifest` |
| 强调色固定品牌蓝 `#0A84F4`，不跟随系统强调色 | 否则用户气泡与选中项配色随机器变化 | `Core/AppTheme.cs` |
| 分段翻译只加段落边界，不改原文内容（换行、空行原样保留） | 长文拼回后要「一字不差」 | `TextChunker` / `TranslationService` |
| 上下文预算两边共用 `HistoryBudgetRatio = 0.5` | 单段 prompt = 历史 + 本次原文，两份各占一半才能保证最坏情况不溢出 `num_ctx - num_predict`。改一边必须同步另一边 | `TranslationService` |

---

## 7. 数据、配置与隐私

| 路径 | 内容 | 说明 |
|---|---|---|
| `%APPDATA%\ChatTranslate\config.json` | 全部设置（明文 JSON） | 手可编辑；损坏会备份为 `config.json.<时间戳>.bad` |
| `%APPDATA%\ChatTranslate\chat.db`（+ `-wal` / `-shm`） | 会话与消息 | WAL 模式；`schema_info` 记结构版本（当前 2） |
| `%APPDATA%\ChatTranslate\images\` | 截图 OCR 的位图 | 删除会话时连同截图一起清理 |
| `%APPDATA%\ChatTranslate\logs\app-YYYYMMDD.log` | 运行日志 | 保留 14 天，启动时清理过期文件；`DiagnosticLogging` 打开后才有高频 TRACE |
| `%APPDATA%\ChatTranslate\logs\crash-YYYYMMDD.log` | 崩溃堆栈 | 数据目录不可写时退 `%TEMP%\ChatTranslate-logs\` |
| `%TEMP%\ChatTranslate-logs\` | 崩溃日志兜底位置 | 用户报障时要问这一个路径 |

- **配置在 `%APPDATA%`，与安装范围无关**：`perMachine` 装到 `Program Files` 时数据仍在用户目录。卸载 MSI **不删** `%APPDATA%\ChatTranslate\`（用户数据不该被卸载程序带走）。
- **当前没有任何密钥/令牌**。P5 引入 `ApiKey` 时按下面四条硬规则做（README 也写明了）：
  1. **不写进 `config.json` 明文**——用 DPAPI（`ProtectedData.Protect`，`CurrentUser` 范围）或 Windows 凭据管理器；
  2. **不进仓库**（`.gitignore` 已挡 `*.local.json`、`appsettings.local.json`、`secrets.json`）；
  3. **不写日志**：`AppLog` 的调用点不得带 key、Authorization 头、完整请求体；
  4. **不回显明文**：设置界面只显示掩码或"已设置"，不回填输入框。
- 翻译内容是用户隐私：不要为了排查把它们落进仓库里的文件、不要贴进 issue/PR。

---

## 8. 已知坑与排查手册

### 8.1 「双击没反应」——先数进程

应用有**单实例保护**：`App.OnStartup` 先抢 `Local\ChatTranslate.SingleInstance` 互斥体，抢不到就弹「ChatTranslate 已经在运行了。」然后 `Shutdown()`，**直接退出**。

关键后果：这条路径在 `OnStartup` 里、`base.OnStartup` 之前返回，**主窗口根本不会创建，`OnLoaded` 不会触发，日志里通常也没有任何新内容**。所以：

1. 先 `tasklist | grep -i ChatTranslate`（或任务管理器）看是不是已经有一个实例在跑——安装版在跑时，刚构建的测试版双击就只会弹这一句；
2. 再确认弹框是不是被别的窗口遮住（它是 `MessageBox`，不是主窗口）；
3. 最后才怀疑启动失败，去看 `%APPDATA%\ChatTranslate\logs\` 与 `%TEMP%\ChatTranslate-logs\`。

`Local\` 前缀意味着互斥体按**登录会话**隔离：同一会话内第二个实例会退出，不同 Windows 用户/会话可以各跑一个。

### 8.2 GUI 运行时验证的边界

本项目的多数缺陷（浮窗不消失、框选偏移、DPI 错位、热键抢不到）**只能在有交互的桌面上复现**，静态阅读和编译通过都证明不了。

- 在**受限沙箱**里 spawn 出来的 GUI 进程，可能对用户目录只有读权限：`%APPDATA%` 写不进去 → 日志与配置都不落盘（`AppLog` 静默失败、`ConfigStore` 退内存值），界面可能起来但"什么都没留下"。此时**不能**据此判断"程序没启动"或"日志功能坏了"。
- 判定"启动了"至少要有两条证据：进程存在 + 日志文件出现（或界面可见）。
- **这是条件句，不是永久事实**：在**有桌面、有写权限的会话**里（例如人工双击，或以提升权限运行的 agent 沙箱，如 Codex 的 elevated 模式），上述限制不成立，GUI 可以正常起、日志会正常落盘。接手前先确认自己处在哪一档，别把上一档的结论当结论用。
- 需要真机验证的功能（热键、钩子、UIA 取词、框选、MSI 安装）请**明确写出"待人工验证"**，不要用"编译通过"冒充"已验证"。

### 8.3 构建 / 打包

- **别用 WiX v7**：要求接受 Open Source Maintenance Fee 协议（商用付费）。v5 仍是 MIT 免费（README 有警告）。
- **UI 扩展必需**：没有 `-ext WixToolset.UI.wixext`，MSI 没有任何界面，双击直接装完，看着就像坏了。
- **`.wxs` 里不用相对路径**：WiX 按**当前工作目录**解析 `Source` / `SourceFile`，不按 `.wxs` 所在目录。所以路径都来自 `-d SourceRoot=` 注入（`cygpath -w` 转成 `C:\...`）。
- **`perMachine` 需要管理员**，无提权时 msiexec 会以 1925 失败。验证流程用 `test-installer.sh` 的 `perUser` 变体。
- **不要给 MSI 加 `VersionNT` 判断**：包装没声明目标 OS 时 `VersionNT` 会报 603（Win 8.1），`VersionNT >= 1000` 恒为假，安装直接 1603 失败。系统要求由 TFM 与 README 保证（`.wxs` 里有长注释解释）。
- **组件 GUID 不要复用**：`MainExecutable` / `StartMenuShortcut` / `DesktopShortcut` 的 GUID 已固定；新增组件必须新 GUID，改路径不改 GUID 会升级残留。
- `build-installer.sh` 会在 publish 目录多出文件时**警告**：那些文件不会被装进 MSI。要么改发布配置，要么同步改 `.wxs`。
- 改 `LICENSE` 后必须重生成 `installer/LICENSE.rtf`（脚本会自动跑，手动打包时要记得；许可页只认 RTF）。

### 8.4 取词

- Electron / 自绘界面拿不到 UIA 文本 → 回退模拟 `Ctrl+C`，会**短暂占用剪贴板**。这是已知代价，不要试图"优化"成直接读剪贴板。
- 回退路径必须带剪贴板备份还原，且只处理已知格式（见 §6）。
- 排查"划词没反应"：设置里打开 `DiagnosticLogging`，日志里能看到钩子是否装上、取到没取到、被哪条规则拦下。默认的终端/密码管理器黑名单（`SelectionBlacklist`）会静默拦住那些进程里的选中——先确认目标进程不在名单里。
- 划词长度上限默认 20000 字符（`SelectionMaxChars`，范围 200 ~ 500000）。它只是"值不值得自动开工"的产品阈值，不是模型能力上限——超长文本会分段。

### 8.5 OCR

- 截图 OCR 需要系统装了对应语言的 OCR 语言包，**缺失时识别结果为空且不报错**。排查顺序：先看 `OcrService` 可用语言，再去系统设置加「光学字符识别」可选功能。
- 截图/框选偏移几乎都是 DPI 问题：先查 `app.manifest` 是否还是 `PerMonitorV2`。

### 8.6 版本号有四处，容易漏

| 位置 | 现状 |
|---|---|
| `src/ChatTranslate/ChatTranslate.csproj` `<Version>` | `0.1.2` —— **单一真源**，打包脚本从这里读 |
| `tools/create-release.py` `TAG` / `MSI` 路径 / `BODY` 正文 | 硬编码 `v0.1.2` 与 `...-0.1.2-x64.msi`，**必须手改** |
| `README.md` / Release 说明里的版本叙述 | 手改 |
| `src/ChatTranslate/app.manifest` `assemblyIdentity version` | 仍为 `0.1.1.0`（陈旧，不影响运行；顺手同步即可） |

改版本时四处一起看，否则会出现「包是 0.1.3、Release 标签是 v0.1.2」这种事。

---

## 9. 代码风格与提交约定

**注释**：中文，解释**为什么**而不是**做了什么**。最典型的模板是：一句话说结论，再用 `<para>` 说这个选择避免了什么故障（"否则…实测…"）。新代码请沿用——这个仓库里注释的价值主要在"防止下一个人把它改回去"。

**异步 / 线程**：UI 更新回主线程；`ConfigureAwait(false)` 用在库代码里，界面回调处不要加。跨线程访问 UI 是这里出现过的回归类型之一，改动涉及回调时格外小心。

**失败处理的分级**（重要，别搞反）：

- **可以静默兜底**：日志写失败、模型清单探测失败、主题应用失败、配置写盘失败（记日志即可）。
- **必须让用户看见**：翻译失败、输出撞上限被截断、数据目录不可写退内存库、崩溃。
- 判据是同一句话：**用户会不会因此拿到错误的结果，或不知道自己丢了一份数据。**

**不要留 `TODO` / `FIXME` 注释**（当前代码里是 0 个）。未完成的事写到本文档或 Issue 里。

**提交信息**：`feat:` / `fix:` / `chore:` / `docs:` + 中文描述，一行说清改了什么。历史上还有「修复代码评审 N 条」这类聚合提交——批量修复评审发现时沿用。**代码注释、README、提交信息、PR 都用中文**（README 明确说明"提 PR 用中文完全没问题"）。

**改动前的自检清单**：

1. `dotnet build ... -c Debug -p:Platform=x64` 通过；
2. 碰了 §6 里任何一条 → 逐条确认行为没变；
3. 碰了配置项 → 属性默认值 + `Normalize()` + 设置界面 三处齐全；
4. 碰了打包 → 跑 `test-installer.sh`；
5. 只做了编译验证的，在总结里**明说**没做运行时验证。

**不要提交**：`bin/`、`obj/`、`publish/`、`installer/out/`、`*.pdb`（均在 `.gitignore`）。本机 `publish/ChatTranslate.exe` 与 `installer/out/*.msi` 是构建产物，虽然文件系统里存在但**不入库**。

---

## 10. 发布流程（按顺序）

1. 改 `csproj` 的 `<Version>`；
2. 同步 `tools/create-release.py` 的 `TAG`、`MSI` 路径、`BODY` 正文；
3. `./installer/build-installer.sh`（读 csproj 版本，产出 `installer/out/ChatTranslate-Setup-<版本>-x64.msi`）；
4. `./installer/test-installer.sh`（往返验证；需先有 `publish/ChatTranslate.exe`）；
5. `python tools/create-release.py --dry-run` 看一眼要发的请求；
6. 真发：`GH_TOKEN=xxx python tools/create-release.py`。

Release 页的安装说明与「本版新增/修复」都在 `BODY` 里，按用户能看懂的话写，不写内部实现细节。

---

## 11. 待办：P5 多引擎（在线 API 与本地 Ollama 并存）

状态：**未开始**，README 明确欢迎 PR 认领。设计前提是"至少不破坏本机离线这条路径"。

改造点已经收敛，按依赖顺序：

1. **抽接口**：把 `OllamaClient` 的公开方法提成 `ITranslationClient`（`ChatAsync` / `ChatStreamAsync` / `ListModelsAsync` / 驻留状态），新增 `OpenAiCompatClient`（覆盖 OpenAI / DeepSeek / Moonshot / 智谱等兼容端点）。`TranslationService` 只依赖接口。
2. **配置加 provider**：`AppConfig` 增 `Provider` / `Endpoint` / `Model`（已有）/ `ApiKey`（加密存储，见 §7），并在 `Normalize()` 里收敛未知 provider。
3. **参数映射**：`num_predict` → `max_tokens`；`temperature` / `top_p` 同名；`keep_alive` / `num_ctx` 在线 API 无对应概念，**直接丢弃**（不要伪造）。
4. **流式差异**：Ollama 是 NDJSON 逐行 JSON；OpenAI 兼容端点是 SSE（`data: {...}\n\n`，`data: [DONE]` 结束）。`TranslationService` 的流式回调要能同时消化两种，或按 provider 分支。
5. **并发与限流**：本地模型串行就够；在线 API 需要重试退避 + 速率控制，否则很快被限流。
6. **模型清单**：`ModelCatalog` 目前硬绑 `/api/tags`。多 provider 下要么按 provider 分支，要么在线端点直接让用户手填模型名（更省事，也更诚实）。
7. **计费与配额**：README 列了但属于可选项；要做就做 token 统计展示，别假装能算钱。

**约束**：默认路径必须仍是本机 Ollama 且零配置可用；加了 provider 之后，"完全不联网也能用"要能被验证（断开网络、默认配置、翻译成功）。

---

## 12. 未核实的点（**不要凭猜测实现**）

这几条写文档时没能实跑验证，动到相关部分先去确认；确认后删掉对应行。

- `dotnet build ChatTranslate.sln`（不带 `-p:Platform=x64`）能否成功——`.sln` 只映射 `Any CPU`，csproj 只声明 `x64`。
- `installer/build-installer.sh` 的步骤编号是 `1/4 → 2/4 → 4/4`（缺 3/4），是文案遗留还是漏了一步校验。
- 仓库无 `.github/`，推断**没有 CI**。
- 长文分段与 `TextChunker` 的语义边界规则**没有回归清单**（无测试工程，切分回归风险最高）。
- P5 是否要保持 `OllamaClient` 的公开签名不变（仓库内只有 `TranslationService` / `ModelCatalog` 使用）。
