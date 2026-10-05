# 声屿 · Windows 便携音频播放器

WPF / .NET 10 桌面程序，使用 .NET 9 起内置的官方 Fluent 资源与控件样式。这是 **Windows x64 便携应用**，不是手机应用。

## 直接使用

最终便携目录：`artifacts/ShengYu-Portable-win-x64`。

**复制整个目录，双击其中的 AudioPlayer.exe 即可使用。** 无须在目标电脑预装 .NET、Python 或 Python 包，也不需要激活虚拟环境。不要只复制 exe。

便携包内包含：

- .NET 10 Windows Desktop 运行时与程序。
- Python 官方 3.13.16 x64 可嵌入运行时及所有识别依赖。
- 应用本地的 Microsoft Visual C++ CRT DLL。
- 8 个多语言 CTranslate2 / faster-whisper 模型，模型总计约 **12.23 GiB**；仅英语模型已移除。
- `data` 配置目录、识别脚本、图标与第三方许可证。

完整发布目录约 **12.66 GiB**，模型占其中约 12.23 GiB。

CPU 识别不依赖 CUDA。GPU 识别需要目标电脑安装兼容 NVIDIA 驱动、cuBLAS 12 / CUDA 12 与 cuDNN 9；设置中的“检测本机环境”可检查这些组件。程序不会替用户安装驱动。

## 本次改动

1. **完整便携发布**：使用自包含 .NET；Python 是独立运行时，不是指向开发机的 venv。配置写入程序目录的 `data/state.json`，不依赖 AppData。位于程序目录内的音频、歌词、Python 和模型路径按相对路径保存，移动后重新解析。
2. **图标播放按钮**：播放三角与暂停双竖线自动切换，保留提示与无障碍名称。
3. **应用图标**：青绿渐变、音符与水波的原创矢量图标；提供 SVG、PNG 和 16–256 像素多尺寸 ICO，用于 exe、窗口、托盘和默认封面。
4. **音频封面**：读取 MP3、FLAC、M4A、WAV 等格式的内嵌封面，优先正面封面；无内嵌图时查找同名 jpg/png、cover.jpg/png、folder.jpg/png、front.jpg。异步加载，坏图片不影响播放，切歌后丢弃旧的异步结果。也显示歌手与专辑标签。
5. **本地环境检测**：设置中新增检测按钮，结果弹窗显示 Python/依赖、NVIDIA GPU/驱动、CUDA 驱动支持版本、nvcc 工具包版本、cuBLAS 12、cuDNN 9、CTranslate2 GPU 能力及 CPU 可用性，并提供官网链接。另有“检查选中模型”执行实际模型加载。
6. **模型 ComboBox**：扫描完整本地模型目录，列出名称、语言范围和体积；刷新或选择仓库目录即可更新。模型损坏/缺文件不会自动下载补齐。
7. **多线程批量识别**：多选音频或加入整个播放列表，单次加载模型后用 1–4 个线程并发识别（默认 2），直接写入各音频同目录同名 LRC；支持暂停／继续、进度、取消、失败继续和可选覆盖。
8. **在线模型管理**：设置 → 在线模型与下载，可查看支持的多语言模型在线版本、大小、来源及本地状态，下载、取消和续传；完成校验后自动刷新模型下拉框。
9. **竖排桌面歌词**：设置 → 桌面歌词 → 竖排桌面歌词；从上到下排字，长句从右向左换列，保留字体、颜色、拖动和穿透设置。
10. **单例与音量**：同一 Windows 用户会话仅保留一个播放器，重复启动唤回已有窗口；音量轨道按点击位置调节，键盘小步长 1%、大步长 5%。
11. **左侧导航与歌词抽屉**：左侧切换我的音乐、本地识别和设置，主页只显示播放列表。点击底部小封面向上展开大封面与完整歌词，点击收起按钮、再次点击小封面或按 Esc 返回；播放栏始终保留。
12. **边听边识别**：开启后，播放缺少 LRC 的音频会自动在本机识别，逐段显示时间同步字幕，完成后保存同目录同名 LRC；切歌会停止上一首的自动识别。
13. **倍速播放**：底部提供 0.5×–3× 倍速选择，切歌保留倍速，歌词按音频实际时间同步。
14. **模型刷新修复**：整体更新模型列表并保留选中项，识别多层模型目录与 Hugging Face snapshots 缓存，跳过未完成和下载临时目录；个别模型正在替换不会让其他模型消失。

如果音乐也需要随程序移动，请将它们放到便携目录的 `Music` 子目录后再添加；程序不会擅自复制原有音频。外部磁盘的音频路径仍保留为外部路径。请把便携目录放在当前用户可写的位置。

旧版配置位于 `%LOCALAPPDATA%/ShengYuAudioPlayer/state.json`。如需迁移旧播放列表，可以在程序关闭时复制该文件到便携目录 `data/state.json`；之后在设置中确认本包 Python 和模型目录，保存后内部路径会自动转换为相对路径。

## 模型目录

已保留当前目录中的 **8 个多语言模型**：

tiny、base、small、medium、large-v1、large-v2、large-v3、large-v3-turbo。

`large` 是 `large-v3` 的别名，`turbo` 是 `large-v3-turbo` 的别名，因此不重复占空间。`.en` 与仅英语蒸馏模型已从源模型目录和便携包移除，也不会在在线目录或本地选择器中出现。这里的模型目录不包含 Hugging Face 上任意第三方微调模型。

每个模型保留 `download-manifest.json`，记录来源、固定提交、文件大小及大文件 SHA-256 校验值。下载过程验证大小和 LFS 哈希。

这些模型均可识别中文。较大模型需要更多内存和时间。唱词、伴奏、和声仍会影响准确性，歌词可能需要校对。

在设置点击“在线模型与下载…”会联网读取各仓库当前版本和文件大小。选中缺失的模型后点击下载，可更改保存目录；已有完整模型不会重复下载。取消后保留 `.downloads` 中的临时文件以便续传，临时模型不会进入识别下拉框。下载按固定提交完成并验证文件大小与大文件 SHA-256，再移入正式目录；若正式目录已有不完整文件，保留到 `.backups`。下载使用 Hugging Face 的公开模型仓库，不需要 API Key，不上传音频。

## 播放与歌词

支持多个播放列表、文件/文件夹多选、递归导入、拖放、去重、重命名、移除与调整顺序。双击或 Enter 播放，Delete 从列表移除但不删除文件。

支持顺序、逆序、随机、单曲循环和单曲播放，可独立开启列表循环；随机上一首遵循实际播放历史。音量和进度可以调节。

自动加载同名 LRC，支持手动关联、多时间标签、毫秒、offset、UTF-8 与 GBK。桌面歌词支持横排/竖排切换、字体、字号、颜色、不透明度、时间偏移、拖动、锁定和鼠标穿透；竖排只影响桌面悬浮歌词，长句自动换列，Unicode 字素保持完整。

主页面采用左侧导航与宽播放列表，底部为持续可用的播放控制。点击底部封面，以抽屉动画向上展开大封面和完整歌词；点击右上角向下箭头、再次点击底部封面或按 Esc 收起。当前歌词高亮并平滑居中跟随；可使用鼠标滚轮、滚动条或方向键上下翻阅。翻阅时继续更新高亮但不抢回滚动位置；停止操作 **5 秒** 后自动回到当前句，也可点击“回到当前歌词”立即恢复。新识别片段追加时保留手动翻阅位置，切换歌曲时重置，无歌词时显示提示。

**点击任意一行歌词即可跳转到该句的音频时间，并恢复自动跟随。** 跳转会同时更新进度条和桌面歌词，考虑 LRC 自带偏移与设置中的歌词偏移；播放时继续播放，暂停时保持暂停。边听边识别已经生成的歌词也可以点击。

在歌词抽屉或设置里开启“边听边识别”，当前音频没有同名或已关联的 LRC 时自动开始本地识别。识别出的片段按原音频时间显示在歌词抽屉和桌面歌词中，完整结果写入同目录同名 LRC。首段字幕需要等待模型加载和计算，模型速度不足时字幕可能落后于播放；倍速不会改变识别输出的时间戳。已有 LRC 不重复识别，识别期间手动放入的 LRC 也不会被覆盖。切歌或关闭该选项会取消当前自动识别；批量队列正在处理同一音频时，播放界面使用该任务的字幕。该开关默认关闭。

默认关闭窗口时隐藏到托盘继续播放；双击恢复，右键可控制播放或退出。设置中可以改为直接退出。

直接退出会先结束当前 `Closing` 事件，再取消并等待后台任务、保存设置和释放资源，避免空闲时同步完成清理导致重复 `Close()` 异常。退出期间重复关闭或点击托盘恢复不会重复执行退出流程。可用测试参数 `--exit` 验证直接关闭、连续关闭、等待识别结束和托盘退出。

桌面歌词每 2 秒检查原生窗口的隐藏、最小化、置顶和屏幕位置，异常时恢复；显示器、分辨率、DPI 变化，以及休眠恢复、解锁和远程桌面重连时重新布局。位置按实际显示器工作区约束，避免落入多屏间隙或已断开的屏幕；透明歌词窗口单独使用软件渲染，减少显卡驱动相关的重绘问题。恢复保留锁定穿透且不抢键盘焦点，主动关闭桌面歌词后不会自动打开。已模拟验证这些恢复路径，对其他电脑上未知触发原因的消失问题仍需结合日志确认。

再次双击 exe 时，第二个进程会唤回已有窗口并退出，支持从托盘隐藏或最小化状态恢复。带音频路径启动时，把文件传给原窗口；单例覆盖同一用户会话中的不同程序副本。

播放解码使用 WPF MediaPlayer，具体格式/编码支持取决于 Windows 媒体组件。便携包不附带第三方播放解码器；识别解码由附带的 PyAV 提供。Windows N 版本可能需要 Media Feature Pack。

## 环境检查与离线识别

在“设置 → 离线识别引擎”选择模型，点击“检测本机环境”查看结果。GPU 检查区分：

- 显卡驱动支持的 CUDA 版本。
- nvcc 对应的 CUDA Toolkit 版本。
- 实际可加载的 cuBLAS 12 与 cuDNN 9 DLL。
- CTranslate2 能看到的设备和计算类型。

环境检测不联网，不安装任何组件；只有点击结果窗口的官网链接才会打开浏览器。弹窗明确提供“cuBLAS 12 下载（含于 CUDA 12.9）”与 cuBLAS 官方说明链接：[CUDA 12.9 下载](https://developer.nvidia.com/cuda-12-9-1-download-archive)、[cuBLAS 官方说明](https://developer.nvidia.com/cublas)。仅发现较新的 CUDA Toolkit 不代表兼容运行库齐全；基础依赖通过后，还可用“检查选中模型”验证模型加载。

在“本地识别”批量添加音频，或加入当前整个播放列表，再点击“开始批量识别”。每个文件默认生成 `原文件夹/原音频名.lrc`，不再弹出保存路径窗口；默认跳过已有 LRC，需要替换时勾选“覆盖已有 LRC”。同批同名但扩展名不同的音频若指向同一 LRC，后项跳过并说明冲突。单项失败不阻止后续文件；取消保留已完成结果，未完成项不会替换旧歌词。

一个批次只启动一个 Python 进程、只加载一次模型，多线程共享模型进行识别；“并发任务”可选 1–4，默认 2。并发会增加内存／显存占用，大模型或显存较小时可降低到 1。点击“暂停识别”，正在计算的片段结束后暂停，保留模型、音频迭代器和已完成的片段；点击“继续识别”从原进度接着处理，不重新加载或从头识别。暂停期间仍占用模型内存。暂停状态只保留在当前程序进程中；“取消识别”或退出程序会终止未完成任务，再次开始需重新识别未完成文件，已生成的 LRC 默认跳过。

设置 → 离线识别引擎中的 **“识别结果自动转为简体中文”默认开启**，使用随包附带的 OpenCC 本地字典，不联网。转换同时应用于实时字幕、单文件和多线程批量识别的新 LRC；关闭后保留识别原文。修改从下一次识别任务生效，不修改现有 LRC、音频名称或时间戳。环境检测也会检查 OpenCC 字典是否可用。

识别强制只读本地模型，无云 API；只有主动打开在线模型管理窗口或运行下载脚本时才联网。本机 CPU 并发识别已通过真实音频测试；GPU 可用性以运行电脑当时的环境检测与模型检查结果为准。

## 运行日志

使用 Serilog，将 UTF-8 日志写入 **exe 同目录的 `logs` 文件夹**，例如 `logs/AudioPlayer-2026100221.log`。按本机时间每小时一个文件，同一小时再次启动继续追加；自动删除超过 7 天的应用日志，不设默认 31 个文件的数量上限。启动写入和每小时轮转时执行清理，空闲运行期间也有每小时一条状态记录；程序未运行时不会执行删除。

日志记录版本、系统、启动退出、播放与歌词跳转、识别任务和耗时、暂停取消、模型下载、环境检测、桌面歌词恢复，以及异常堆栈。包含本地文件路径和模型配置，不逐句记录识别文字或每帧播放进度。若再次遇到桌面歌词消失，可提供发生时间及对应小时的日志，便于定位。

## 插件与 Bilibili 下载

启动时扫描 `plugins/*/plugin.json`，只加载设置中启用的插件。新插件默认关闭；在“设置 → 插件”更改开关后，从托盘右键退出并重新启动生效。清单不合法、API 不兼容或程序集加载失败会显示原因并写日志，不阻止其他插件加载。已启用插件的页面追加到左侧导航。

新增默认关闭的**小说章节插件**（API 2）：按自定义规则解析小说，卷标题原文折叠／每 N 章分组、编辑校对与拆分合并；利用同名 LRC 的标题与正文锚点建立双向映射，跳转播放并跟踪章节。缓存为本地可读 JSON，保留历史音频和匹配依据，标记缺失及推断范围，支持片段时间校对和上一版回滚。按播放列表绑定小说／缓存，重新进入时加载预览，切换无绑定列表时不会误用另一小说。详细行为、匹配限制与文件位置见 `Plugins/Novel/README.md`。

双击 `Plugins/Novel/build.cmd` 可独立编译、测试并打包小说插件，输出到 `artifacts/ShengYu-Novel-Plugin-*.zip`（含 `plugins/novel`，无需模型和 Python）。根目录 `build.cmd` 会把两个插件一起打入完整便携交付包。新版宿主保留 API 1，并为 API 2 提供 `PluginContext.Playback`：UI 线程只读播放列表／播放快照，以及等待音频打开后再定位播放的 `PlayAsync`。

附带的 Bilibili 下载插件使用官方二维码生成／轮询接口，打开二维码窗口后用 Bilibili App 扫码并确认。支持二维码刷新、过期提示和关闭取消，无内嵌浏览器。Cookie 使用标准 CookieJar 解析并由 Windows 当前用户加密存入 `data/plugins/bilibili/session.bin`，通过子进程标准输入传递；不写入命令行、日志或交付包。移动到其他电脑／用户后需重新扫码，退出登录删除保存的凭据。

输入 BV 号或完整视频链接，读取多 P 列表（默认全选），选择分 P、清晰度、保存目录及是否带字幕。默认只下载 M4A 音频；勾选视频后下载音视频流，用 FFmpeg 合并 MP4。清晰度按账号可访问的流列出，其他分 P 不支持所选清晰度时明确失败，不静默降级。字幕保存为 SRT，并优先用中文字幕生成同名 LRC；没有可获取字幕时提示。支持取消、保留下载片段续传、同名已完成文件跳过和单项失败继续。默认输出 `Downloads/Bilibili`。

插件依赖独立保存在 `plugins/bilibili`，包括 yt-dlp、qrcode 和 FFmpeg；不会改变离线识别依赖。重建时先运行 `packaging/prepare-bilibili.ps1`，再运行 `packaging/build-portable.ps1`（会自动构建插件）。开发运行可用 `packaging/build-plugins.ps1 -OutputDirectory AudioPlayer/bin/Release/net10.0-windows` 部署到开发输出目录。

插件开发使用 `AudioPlayer.Plugin.Abstractions` 的 API 1：实现 `IPlayerPlugin.CreatePage(PluginContext)`、`StopAsync()` 和 `Dispose()`。页面在 WPF UI 线程创建；耗时操作需异步执行，退出时停止自己的工作并响应 `ShutdownToken`。`PluginContext` 提供独立数据目录、Python 路径和日志回调。清单包含 `id`、`name`、`version`、`description`、`assembly`（同目录 DLL 文件名）、`entryType` 和 `apiVersion: 1`，可参考 `Plugins/Bilibili/plugin.json`。插件与播放器拥有相同的本机权限，应仅启用可信来源。修改程序集后重启加载。

本次插件验证：22 组 C# 集成测试通过，包含插件开关持久化、默认关闭、清单错误隔离、动态加载、加密凭据、真实二维码获取／刷新／取消和现有播放器回归；7 项插件 Python 测试通过，覆盖 Cookie 多响应头与 Expires、手机确认、过期、精确清晰度、分 P 选择、字幕转换和已有文件保护。真实公开 23 P 视频解析、音频下载和视频下载合并成功，ffprobe 确认 MP4 包含 H.264 视频与 AAC 音轨。账号扫码确认后的会员清晰度和受登录限制的字幕仍需使用用户账号验证。

## 开发与重建

开发需要 .NET 10 SDK。普通 Debug/Release 构建用于开发，不等于完整便携包。

### 一键生成交付 ZIP

**双击项目根目录的 `build.cmd`**。也可以在 PowerShell 7 中运行 `./build.ps1`；从其他工作目录调用同样有效。

脚本自动检查依赖，编译 Release 主程序和 Bilibili 插件，附带 .NET／Python／FFmpeg 运行环境，生成**无模型、无用户数据**的 ZIP 和 SHA-256 文件，再解压到中文路径执行便携自检。默认不下载、不校验、不复制 Whisper 模型；即使本机没有 `models` 目录也可以构建。

- 构建机需要 **Windows x64、PowerShell 7、.NET 10 SDK**，以及 Visual Studio／Build Tools 提供的 **x64 Visual C++ 可再发行 CRT**。运行包的用户无需安装这些开发工具。
- `runtime` 不完整时，自动调用依赖准备脚本联网下载。首次准备需要带 pip 的开发 Python，依次查找项目 `.venv`、PATH 上的 Python、`py` 启动器；完整运行环境可直接复用，重复构建不会重复下载。
- ZIP 和校验文件默认位于 `artifacts/ShengYu-win-x64-NoModels-日期时间-随机后缀.zip` 及 `.zip.sha256`。每次使用独立发布目录，不覆盖正在运行的旧播放器、模型或用户数据。
- 完整日志位于 `artifacts/build-logs/`。只有编译、打包及自检全部成功，才更新 `artifacts/latest-build.json`，其中包含本次 ZIP 路径、哈希和日志位置。失败时窗口保留错误信息并返回非零退出码。

可选参数（相对路径以项目根目录为基准）：

```powershell
./build.ps1 -OutputDirectory 'artifacts/交付包'
./build.ps1 -BootstrapPython 'C:/Python313/python.exe' -VcCrtDirectory 'C:/VS/VC/Redist/MSVC/14.xx/x64/Microsoft.VC143.CRT'
# 自动化中调用双击入口，不等待按键：
./build.cmd --no-pause
```

以下保留单步开发和含模型便携目录的重建方式。

```powershell
dotnet build AudioPlayer.slnx -c Release
dotnet run --project AudioPlayer/AudioPlayer.csproj -c Release
```

从零准备环境（以下下载步骤需要联网）：

```powershell
# 准备开发用 Python；路径替换为自己的 Python 3.12。
.\recognition\setup.ps1 -PythonExe 'C:\Python312\python.exe'

# 当前 8 个多语言模型；会下载约 12.23 GiB。
.venv\Scripts\python.exe recognition/download_model.py --all --output models

# 下载官方 Python 可嵌入包，并按锁定版本安装 Windows x64 依赖。
.\packaging\prepare-python.ps1

# 生成完整自包含便携目录（包括模型的实际副本，不使用目录链接）。
.\packaging\build-portable.ps1
```

`prepare-python.ps1` 可通过 `-BootstrapPython` 指定用于 pip 的开发 Python。`build-portable.ps1` 会自动寻找 Visual Studio 的可再发行 CRT 目录，也可用 `-VcCrtDirectory` 指定。打包脚本仅复制目录清单中的模型，不复制下载缓存、不清空既有数据。完整重建建议留出约 30 GiB 空间用于源模型和发布副本。旧开发目录可运行 `packaging/remove-english-models.ps1` 清理指定的仅英语模型文件夹。

需要无模型、无用户数据的交付压缩包时，在完整发布后运行 `packaging/build-delivery.ps1`。它排除根目录的 `models`、`data`、`logs` 及缓存，保留 Python 依赖内部所需的资源和字典，生成 ZIP 与 SHA-256 校验文件；不会修改原便携目录。交付版附带独立使用说明，首次识别前在程序内下载或选择模型。可用 `packaging/test-delivery.ps1 -ZipPath <ZIP路径>` 解压到中文路径，在隔离开发机环境后验证运行时、字典和日志。

## 测试与便携自检

Bilibili 插件 1.1.0 支持 Ctrl 切换勾选、Shift 按起点状态连续勾选／取消，以及全列表反选。Python 使用 `-I -X utf8 -u` 和显式 UTF-8 标准流，避免隔离启动忽略编码环境变量后在 GBK Windows 上出现乱码。独立插件回归可执行 `dotnet AudioPlayer.Tests/bin/Release/net10.0-windows/AudioPlayer.Tests.dll --plugins --bili-encoding --bili-selection` 和 `.venv/Scripts/python.exe -m unittest discover -s Plugins/Bilibili -p 'test_*.py' -v`，覆盖实际 WPF 勾选事件、虚拟化长列表范围与反选、跨进程 Unicode 文本，以及模拟 GBK／西文系统的请求和响应。

```powershell
dotnet run --project AudioPlayer.Tests -c Release -- --render --media --python runtime/python/python.exe --offline-model models/faster-whisper-tiny --speech artifacts/offline-speech.wav --native
.venv\Scripts\python.exe -m unittest discover -s recognition -p 'test_*.py' -v
```

覆盖播放模式、LRC、中文编码、配置备份、移动后相对路径、内嵌/目录封面、完整模型发现、Fluent 界面渲染、音频播放与重播、识别取消和文件保护、真实 CPU 识别、环境报告、托盘与歌词穿透。

测试参数追加 `--online-models` 可执行真实在线目录查询、tiny 模型下载与校验、下载取消；测试模型保存到本次 `artifacts/tests` 私有目录中。

`--native` 会短暂创建并清理托盘与歌词窗。语音样例是本地合成的英文测试音频，不是用户私人文件。

发布程序提供无界面自检：

```powershell
.\AudioPlayer.exe --portable-check
# 可选：真实识别一段音频，结果保存在 data/portable-check.lrc。
.\AudioPlayer.exe --portable-check 'C:\audio\sample.wav'
```

报告保存在 `data/portable-check.json`，包含当前根目录、运行时、Python、模型数量与环境检测结果。

可运行 `.\packaging\test-portable.ps1` 执行整包移动验证：将发布目录暂时移到带中文和空格的新路径，清除子进程对开发机 PATH、Python 和 .NET 路径的依赖后，执行真实离线识别；最后恢复目录，报告保存在 `artifacts/portable-relocation-check.json`。运行此检查前请退出发布版播放器。

2026-10-02 本次验证：Release 构建 0 警告、0 错误；20 组集成测试通过，包括左侧导航、歌词抽屉、最小窗口布局、运行中模型刷新、实际倍速播放、识别暂停／继续、实时字幕与切歌保护、真实双文件 CPU 并发识别、单例和托盘，以及歌词点击跳转与偏移、简体开关、日志七天保留和共享写入、原生桌面歌词隐藏／置顶／屏外／最小化恢复与无焦点抢占。Python 12 项单元测试通过，包含共享模型多线程、暂停保留进度、真实 OpenCC 字典转换及单文件／批量参数传递。在线目录查询与真实 tiny 下载校验在前次功能交付时通过。尚未在另一台全新 Windows 电脑上验证。

完整便携包已通过中文／空格路径移动和真实 CPU 识别验证。无模型交付 ZIP 已重新解压到中文路径，在清除开发机 .NET／Python 路径后通过运行时与 OpenCC 自检，并确认无模型、无预置用户数据和日志，运行时可正常生成 Serilog 启动／退出日志。

## 主要文件

- `AudioPlayer/App.xaml`：官方 Fluent 资源。
- `AudioPlayer/MainWindow.xaml`：主界面、封面、图标按钮和模型下拉框。
- `AudioPlayer/Views/EnvironmentWindow.*`：检测结果与官网链接。
- `AudioPlayer/Views/SynchronizedLyricsView.*`：完整歌词、点击跳转、高亮、平滑跟随与闲置恢复。
- `AudioPlayer/Views/LyricsWindow.xaml.cs`、`Services/DesktopLyricsPlacement.cs`：桌面歌词布局、屏幕约束与窗口恢复。
- `AudioPlayer/Services/AppLogging.cs`：Serilog 小时日志、七天保留与退出刷新。
- `AudioPlayer/Services/CoverArtService.cs`：音频标签及封面。
- `AudioPlayer/Services/PortablePaths.cs`、`StateStore.cs`：便携路径与配置。
- `AudioPlayer/Services/BatchTranscriptionService.cs`：批量识别进程、进度与输出文件保护。
- `AudioPlayer/Services/SingleInstanceService.cs`：单例互斥与进程间窗口唤回。
- `AudioPlayer/Views/ModelManagerWindow.*`、`Services/OnlineModelService.cs`：在线模型管理界面和下载进程。
- `recognition/inspect_environment.py`、`gpu_runtime.py`：环境探测与 GPU 库定位。
- `recognition/model_catalog.json`、`download_model.py`：模型目录与完整下载。
- `recognition/model_manager.py`：在线版本查询、续传、校验及安装。
- `packaging/`：独立 Python 与便携发布构建。
- `AudioPlayer/Assets/player.svg`：可编辑图标源文件。

参考：[WPF 官方 Fluent](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90)、[Python Windows 可嵌入发行版](https://docs.python.org/3/using/windows.html#the-embeddable-package)、[faster-whisper](https://github.com/SYSTRAN/faster-whisper)、[TagLibSharp](https://github.com/mono/taglib-sharp)。

