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

在歌词抽屉或设置里开启“边听边识别”，当前音频没有同名或已关联的 LRC 时自动开始本地识别。识别出的片段按原音频时间显示在歌词抽屉和桌面歌词中，完整结果写入同目录同名 LRC。首段字幕需要等待模型加载和计算，模型速度不足时字幕可能落后于播放；倍速不会改变识别输出的时间戳。已有 LRC 不重复识别，识别期间手动放入的 LRC 也不会被覆盖。切歌或关闭该选项会取消当前自动识别；批量队列正在处理同一音频时，播放界面使用该任务的字幕。该开关默认关闭。

默认关闭窗口时隐藏到托盘继续播放；双击恢复，右键可控制播放或退出。设置中可以改为直接退出。

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

识别强制只读本地模型，无云 API；只有主动打开在线模型管理窗口或运行下载脚本时才联网。本机 CPU 并发识别已通过真实音频测试；GPU 可用性以运行电脑当时的环境检测与模型检查结果为准。

## 开发与重建

开发需要 .NET 10 SDK。普通 Debug/Release 构建用于开发，不等于完整便携包。

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

## 测试与便携自检

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

2026-10-02 本次验证：Release 构建 0 警告、0 错误；18 组集成测试通过，包括左侧导航、歌词抽屉动画与 Esc、最小窗口布局、运行中模型刷新、实际倍速播放、暂停／继续与暂停时取消、实时字幕与切歌保护、真实双文件 CPU 并发识别、单例和托盘。Python 10 项单元测试通过，包含共享模型的多线程并发及暂停保留迭代器进度。在线目录查询与真实 tiny 下载校验在前次功能交付时通过。本次便携包还使用 `test-portable.ps1` 验证中文/空格路径移动及真实识别；尚未在另一台全新 Windows 电脑上验证。

## 主要文件

- `AudioPlayer/App.xaml`：官方 Fluent 资源。
- `AudioPlayer/MainWindow.xaml`：主界面、封面、图标按钮和模型下拉框。
- `AudioPlayer/Views/EnvironmentWindow.*`：检测结果与官网链接。
- `AudioPlayer/Views/SynchronizedLyricsView.*`：完整歌词、高亮、平滑跟随与闲置恢复。
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

