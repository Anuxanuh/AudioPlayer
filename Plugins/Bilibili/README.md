# Bilibili 下载插件

在声屿“设置 → 插件”启用 Bilibili 下载，退出并重新启动后，左侧出现插件页面。首次安装默认关闭。

## 使用

1. 点击“扫码登录 Bilibili”，使用 Bilibili App 扫描二维码并在手机上确认。插件调用官方 passport 二维码生成／轮询接口，确认后自动保存会话 Cookie，无须复制 token。二维码支持刷新、过期提示和关闭取消；不使用内嵌浏览器，不收集账号密码。
2. 输入 BV 号或完整 Bilibili 视频链接，点击“读取视频”。多 P 默认全选，可以逐个取消；支持全选、全不选和反选。Ctrl + 单击列表行可切换该集勾选，Shift + 单击按起点的勾选状态批量勾选／取消连续范围，范围外的勾选保持不变；Ctrl + Shift 同样适用。先“全不选”、勾选起点，再 Shift 点击终点即可只勾选一段。空格可切换当前集。清晰度取自当前账号实际可获取的流；普通单击行内空白处选中某一分 P 后，可单独查询该 P 的清晰度。
3. 默认下载音频（M4A）。勾选“下载视频并合并音频”后下载对应清晰度视频和音频，使用随包 FFmpeg 合并为 MP4。如果该分 P 无所选清晰度，会显示失败原因，不静默降低清晰度。
4. 可同时下载可获取的字幕为 SRT，并为优先的中文字幕（否则首个语言）生成同名 LRC。弹幕不作为字幕；未登录、视频未提供字幕或账号无权限时会提示。
5. 选择保存目录并开始。支持取消、保留已完成文件和下载片段续传；同名已完成媒体默认跳过，失败分 P 不阻止其他分 P。

## 数据与编码

默认保存到程序目录 `Downloads/Bilibili`。登录凭据使用 Windows 当前用户加密，保存在 `data/plugins/bilibili/session.bin`。移动到其他电脑或 Windows 用户后需重新登录。退出登录会删除插件保存的凭据；过期后重新扫码。Cookie 不进入命令行、日志或发行包，只通过本地子进程管道传递。

插件源码、JSON 进程通信、SRT／LRC 字幕使用 UTF-8；Windows 文件名使用 Unicode。Python 通过 `-I -X utf8 -u` 启动，并显式设置标准输入输出为 UTF-8，不依赖 Windows 的区域编码设置。

## 使用限制

下载能力取决于视频、地区、Bilibili 接口和账号实际权限；登录不会提升账号原本没有的权限。插件不处理 DRM 解密。

## 编译与安装

在仓库根目录运行以下命令；需要 Windows、PowerShell 7 和 .NET 10 SDK：

```powershell
# 生成独立插件 ZIP
./build.cmd -Plugin bilibili -Mode Package
# 只生成插件发布目录
./build.cmd -Plugin bilibili -Mode Build
```

构建脚本准备插件所需的 FFmpeg 和 Python 库。将插件包解压到支持插件 API 1 的声屿目录，保留 `plugins/bilibili` 结构，再在设置中启用并重启。插件使用宿主提供的 Python 运行环境。

## 第三方依赖

随包包含 yt-dlp 2026.8.19（Unlicense，许可证见 vendor/yt_dlp-*.dist-info）、qrcode 8.2（BSD，见 vendor/qrcode-*.dist-info）和 FFmpeg n8.1.3-14-g330caae0c1（LGPL 3，见 ffmpeg/LICENSE.txt）。

FFmpeg 为单独可替换的进程与动态库，不链接到播放器；构建说明和依赖源代码见 [FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)，源码版本见 [FFmpeg 330caae0c1](https://github.com/FFmpeg/FFmpeg/tree/330caae0c1)。下载来源和 SHA-256 保存在 `dependency-manifest.json`。
