# 贡献指南

欢迎改进声屿的播放器、识别流程、插件和文档。提交问题或 Pull Request 时，请描述使用场景、预期行为和可复现的验证方法。

## 开发环境

主程序使用 WPF，开发和桌面集成测试需要 Windows 与 .NET 10 SDK。构建脚本使用 PowerShell 7。

在仓库根目录执行：

```powershell
dotnet build AudioPlayer.slnx -c Release
dotnet run --project AudioPlayer/AudioPlayer.csproj -c Release
```

普通 .NET 构建用于开发运行；包含独立 Python、插件运行依赖和可选模型的便携包，请使用根目录的 `build.cmd` / `build.ps1`。参数见 [README](README.md#编译与打包)。

### Python 与模型

涉及语音识别或 Python 测试时，需要安装相应依赖。以下示例使用开发 Python 创建 `.venv`；请将路径替换为自己的 Python 安装路径：

```powershell
./recognition/setup.ps1 -PythonExe 'C:/Python312/python.exe'

# 下载一个模型，供本地识别或带模型构建使用
.venv/Scripts/python.exe recognition/download_model.py --size tiny --output models/faster-whisper-tiny

# 下载模型目录中的全部模型
.venv/Scripts/python.exe recognition/download_model.py --all --output models

# 下载独立的歌词翻译模型（约 930 MiB）
.venv/Scripts/python.exe recognition/model_manager.py --translation --root models/translation --download m2m100-418M
```

依赖安装和模型下载需要联网。模型列表定义在 [model_catalog.json](recognition/model_catalog.json)，下载脚本保存来源、固定提交、文件大小和哈希清单。模型权重、虚拟环境及下载缓存受 Git 忽略，不应提交到仓库。

开发运行时，可在播放器设置中将 Python 路径指定为 `.venv/Scripts/python.exe`，并选择本地模型目录。便携发行包使用 `packaging/prepare-python.ps1` 和 [requirements-lock.txt](recognition/requirements-lock.txt) 准备独立环境。

## 代码结构

| 目录 | 职责 |
|---|---|
| `AudioPlayer/` | WPF 界面、播放器、歌词、状态存储与识别进程管理 |
| `AudioPlayer.Plugin.Abstractions/` | 插件生命周期、上下文与播放接口 |
| `AudioPlayer.Tests/` | C# 核心测试及 WPF／播放集成测试 |
| `Plugins/Bilibili/` | Bilibili 页面、会话管理与 Python 下载进程 |
| `Plugins/Novel/` | 小说解析、文本对齐、章节映射与缓存 |
| `recognition/` | 识别、模型管理、GPU 检测和 Python 测试 |
| `packaging/` | 根构建入口共用的依赖准备、发布与验证步骤 |
| `licenses/` | 第三方许可文本与声明 |

## 插件开发

实现 [IPlayerPlugin](AudioPlayer.Plugin.Abstractions/IPlayerPlugin.cs) 的 `CreatePage(PluginContext)`、`StopAsync()` 和 `Dispose()`。页面在 WPF UI 线程创建，耗时操作应异步处理，退出时响应 `ShutdownToken` 并停止自己的任务。

`PluginContext` 提供程序目录、插件目录、独立数据目录、Python 路径和日志回调。API 2 的 `Playback` 接口还支持读取当前播放列表、播放进度，以及切换音频、跳转并播放；这些接口应在宿主 UI 线程调用。

插件目录中的 `plugin.json` 声明 `id`、`name`、`version`、`description`、`assembly`、`entryType` 和 `apiVersion`。宿主接受 API 1 和 API 2，插件默认关闭，程序集修改后需要重启加载。清单可参考 [Bilibili 插件](Plugins/Bilibili/plugin.json)与[小说插件](Plugins/Novel/plugin.json)。

独立构建示例：

```powershell
./build.cmd -Mode Build -Plugin novel
./build.cmd -Mode Package -Plugin bilibili
```

开发调试时，将构建结果中的 `plugins/插件ID` 目录复制到宿主输出目录，再在设置中启用。独立插件包依赖兼容其 API 版本的宿主。

新增插件的构建清单来自 `Plugins/*/plugin.json`，每个插件目录应有一个 `.csproj` 和使用说明。需要额外运行依赖的插件，应在共用构建流程中增加依赖准备、复制及验证逻辑；保持根目录为统一构建入口。

## 测试

根据修改内容选择相关检查。以下命令均从仓库根目录运行：

```powershell
# 核心逻辑、小说映射、插件加载和退出流程
dotnet run --project AudioPlayer.Tests -c Release -- --novel --plugins --exit

# WPF 界面、歌词交互和真实播放接口（需要可用的桌面会话）
dotnet run --project AudioPlayer.Tests -c Release -- --render --media --novel-ui --novel-host

# 播放诊断：底层停滞、恢复、暂停排除、告警限频和实例切换
dotnet run --project AudioPlayer.Tests -c Release -- --playback-diagnostics --novel-host

# Bilibili 插件的编码和列表交互
dotnet run --project AudioPlayer.Tests -c Release -- --bili-encoding --bili-selection

# Python 识别与下载逻辑
.venv/Scripts/python.exe -m unittest discover -s recognition -p 'test_*.py' -v
.venv/Scripts/python.exe -m unittest discover -s Plugins/Bilibili -p 'test_*.py' -v

# 构建选项、模型完整性、包内容和依赖资源检查
./build.cmd -Plugin novel -Mode Build
./packaging/test-build-options.ps1
```

识别集成测试需要可用的 Python、模型与测试音频；音频路径应替换为准备好的样例：

```powershell
dotnet run --project AudioPlayer.Tests -c Release -- --python runtime/python/python.exe --offline-model models/faster-whisper-tiny --speech 'C:/audio/sample.wav'

# 歌词翻译的时间轴、WPF、进程取消与真实本地模型集成测试
dotnet run --project AudioPlayer.Tests -c Release -- --translation --translation-python runtime/python/python.exe --translation-model models/translation/m2m100-418M
```

`--native` 会创建托盘和桌面歌词窗口，用于检查原生窗口行为；`--online-models` 会联网查询、下载和校验模型，需与 `--python` 一起使用；`--bili-qr` 会请求 Bilibili 二维码。测试输出保存在 `artifacts/tests/`。

歌词翻译使用 `recognition/translate_lyrics.py` 的 UTF-8 JSON 标准输入／输出协议，由 C# 端保留时间轴并在完整成功后写入 LRC。py3langid 使用随库数据自动判断原文语言，翻译使用本地 CTranslate2 模型和 SentencePiece，不依赖 Transformers、PyTorch 或远程模型代码。测试包括显示模式触发翻译、切歌取消、识别完成后启动翻译、已有译文复用及独立批量队列。省略 `--translation-model` 可仅检查进程与界面，不加载真实翻译权重。

### 发行包自检

`-Mode Package` 自动解压生成的 ZIP，在中文路径下检查包清单和依赖；主程序包还会隔离开发机 .NET／Python 路径执行自检。也可手动重验：

```powershell
./packaging/test-delivery.ps1 -ZipPath 'artifacts/your-package.zip'
```

在解压后的主程序目录中，可执行：

```powershell
./AudioPlayer.exe --portable-check
# 执行本地模型识别，需包内有模型，音频路径替换为测试样例
./AudioPlayer.exe --portable-check 'C:/audio/sample.wav'
```

报告写入 `data/portable-check.json`，识别样例的结果写入 `data/portable-check.lrc`。需要验证整个目录移动时，可使用 `packaging/test-portable.ps1` 并显式指定 `-PackageDirectory` 和 `-SampleAudio`；待测目录须位于 `artifacts` 下、包含模型，且程序处于退出状态。脚本完成后会恢复原目录。

## 提交修改

- 将修改聚焦于一个问题或功能，说明问题触发条件、修改后的行为和相关验证步骤。
- 保留便携路径处理、UTF-8 进程通信和已有文件保护；修改任务生命周期时关注取消、暂停、切换音频与退出场景。
- 修改构建选择或依赖时，检查发布清单与实际文件一致，并保留第三方许可文件。
- 文档面向使用者与贡献者，描述当前功能、使用步骤和限制。个人工作日志、阶段性验收结果及机器专属信息可放在受 Git 忽略的 `docs/local-history/` 或 `artifacts/`，不要混进入门文档。
- 提交前检查差异，避免包含用户配置、音频、字幕、模型、登录凭据、运行日志或生成的发布文件。

报告问题时，请提供系统与程序版本、可复现步骤及必要的脱敏日志。账号权限、媒体格式、显示器／DPI 配置或 GPU 环境与问题有关时，请一并说明；避免上传凭据及私人媒体文件。
