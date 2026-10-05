#Requires -Version 7.0
<#
.SYNOPSIS
统一编译／打包声屿或单独插件，按参数选择插件与本地语音模型。
.DESCRIPTION
默认 Package：主程序、全部插件、不带模型；ZIP 排除用户数据并自动解压验证。
Build 只发布到独立目录，不创建 ZIP。单独插件使用 -Plugin，不编译主程序。
.PARAMETER Plugins
主程序包含的插件 ID，支持 all、none、逗号列表或 PowerShell 数组，默认 all。
.PARAMETER Models
附带的本地模型 ID，支持 all、none、逗号列表或数组，默认 none；不自动下载模型。
.PARAMETER Plugin
仅编译／打包此插件，不能同时使用 Plugins 或 Models。
.PARAMETER Mode
Build：编译并准备可分发目录；Package：在 Build 基础上创建 ZIP、SHA-256 并验证。
.PARAMETER OutputDirectory
ZIP 与最新成功记录的目录，默认 artifacts；构建工作目录和日志始终位于 artifacts。
#>
[CmdletBinding()]
param(
    [ValidateSet('Build','Package')][string]$Mode = 'Package',
    [string[]]$Plugins = @('all'), [string[]]$Models = @('none'), [string]$Plugin = '',
    [string]$OutputDirectory = '', [string]$BootstrapPython = '', [string]$VcCrtDirectory = '',
    [switch]$Help, [switch]$ListOptions,
    [Alias('no-pause','-no-pause')][switch]$NoPause
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repo = $PSScriptRoot
. (Join-Path $repo 'packaging/build-common.ps1')
$transcribing = $false
$exitCode = 1
$watch = [Diagnostics.Stopwatch]::StartNew()
$bootstrap = ''

function Find-BootstrapPython {
    if ($BootstrapPython) {
        $candidate = [IO.Path]::GetFullPath($BootstrapPython, $repo)
        if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "开发 Python 不存在：$candidate" }
        & $candidate -m pip --version | Out-Host
        if ($LASTEXITCODE -ne 0) { throw '指定的开发 Python 缺少可用的 pip。' }
        return $candidate
    }
    $candidates = [Collections.Generic.List[string]]::new()
    $candidates.Add((Join-Path $repo '.venv/Scripts/python.exe'))
    $command = Get-Command python.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and $command.Source -notmatch '[\\/]WindowsApps[\\/]') { $candidates.Add($command.Source) }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            & $candidate -m pip --version 2>&1 | Out-Host
            if ($LASTEXITCODE -eq 0) { return $candidate }
        }
    }
    $launcher = Get-Command py.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($launcher) {
        $resolved = & $launcher.Source -3 -c 'import sys; print(sys.executable)' 2>$null
        if ($LASTEXITCODE -eq 0 -and $resolved -and (Test-Path -LiteralPath ([string]$resolved) -PathType Leaf)) {
            & ([string]$resolved) -m pip --version 2>&1 | Out-Host
            if ($LASTEXITCODE -eq 0) { return [string]$resolved }
        }
    }
    throw '首次准备依赖需要带 pip 的开发 Python。请安装 Python 或指定 -BootstrapPython。完整 runtime 可直接复用。'
}

try {
    $pluginCatalog = @(Get-PluginBuildCatalog $repo)
    $modelCatalog = @(Get-Content -LiteralPath (Join-Path $repo 'recognition/model_catalog.json') -Raw | ConvertFrom-Json)
    if (@($pluginCatalog.Id | Select-Object -Unique).Count -ne $pluginCatalog.Count) { throw '插件 ID 重复。' }
    if ($Help -or $ListOptions) {
        Write-Host @"
用法（仓库根目录；CMD / PowerShell 均可）：
  build.cmd -Mode Package -Plugins all -Models none
  build.cmd -Mode Package -Plugins bilibili,novel -Models tiny,base
  build.cmd -Mode Build -Plugins none -Models tiny
  build.cmd -Mode Package -Plugin novel
  build.cmd -Mode Build -Plugin bilibili

-Mode Build 只生成发布目录；Package 编译后创建 ZIP 并验证（默认）。
-Plugins / -Models 支持 all、none 或逗号列表；默认全部插件、不带模型。
-Plugin 只构建一个插件，不允许再指定 -Plugins / -Models。
-OutputDirectory 指定 ZIP / 最新成功记录目录（默认 artifacts）。
-BootstrapPython / -VcCrtDirectory 可指定依赖准备工具路径。
-ListOptions 列出可选项；-Help 显示帮助。带参数调用 build.cmd 不暂停；双击结束后暂停。
模型须已完整下载到 models/faster-whisper-ID；脚本只校验和复制选中模型，不自动下载。

可用插件：$($pluginCatalog.Id -join ', ')
可用模型：$($modelCatalog.id -join ', ')
"@
        exit 0
    }
    if ($PSBoundParameters.ContainsKey('Plugin')) {
        if ($PSBoundParameters.ContainsKey('Plugins') -or $PSBoundParameters.ContainsKey('Models')) { throw '-Plugin 为独立插件模式，不能与 -Plugins / -Models 同时使用。' }
        $Plugin = $Plugin.Trim().ToLowerInvariant()
        if ($pluginCatalog.Id -notcontains $Plugin) { throw "未知独立插件：$Plugin。可选：$($pluginCatalog.Id -join ', ')" }
        $kind = 'plugin'; $selectedPlugins = @($Plugin); $selectedModels = @()
    } else {
        $kind = 'app'
        $selectedPlugins = @(Resolve-BuildSelection $Plugins $pluginCatalog.Id '插件')
        $selectedModels = @(Resolve-BuildSelection $Models $modelCatalog.id '模型')
    }
    foreach ($id in $selectedModels) { $null = @(Get-ModelBuildFiles (Join-Path $repo "models/faster-whisper-$id") $id) }
    if (!$IsWindows -or ![Environment]::Is64BitProcess) { throw '请使用 Windows x64 的 PowerShell 7 构建。' }
    if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $repo)
    $buildId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $work = Join-Path $repo ('artifacts/build-' + $buildId)
    $package = Join-Path $work $(if ($kind -eq 'app') { 'ShengYu' } else { 'Plugin' })
    $flavor = if ($selectedModels.Count) { 'WithModels' } else { 'NoModels' }
    $zipName = if ($kind -eq 'plugin') { "ShengYu-$Plugin-Plugin-$buildId.zip" } else { "ShengYu-win-x64-$flavor-$buildId.zip" }
    $zip = Join-Path $OutputDirectory $zipName
    $log = Join-Path $repo ('artifacts/build-logs/' + $buildId + '.log')
    New-Item -ItemType Directory -Force -Path $OutputDirectory,$work,(Split-Path -Parent $log) | Out-Null
    Start-Transcript -LiteralPath $log | Out-Null; $transcribing = $true
    Push-Location -LiteralPath $repo
    try {
        Write-Host "目标：$kind；模式：$Mode；插件：[$($selectedPlugins -join ',')]；模型：[$($selectedModels -join ',')]" -ForegroundColor Cyan
        if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '缺少 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0' }
        $sdk = & dotnet --version
        if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw "本项目需要 .NET 10 SDK，当前：$sdk" }
        $python = Join-Path $repo 'runtime/python/python.exe'
        if ($kind -eq 'app') {
            $pythonProbe = @'
import sys, struct
from pathlib import Path
from importlib.metadata import version
assert sys.version_info[:3] == (3, 13, 16) and struct.calcsize('P') == 8
for line in Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    if line.strip() and not line.startswith('#'):
        name, expected = line.strip().split('==')
        assert version(name) == expected, f'{name}: expected {expected}, installed {version(name)}'
import faster_whisper, onnxruntime, ctranslate2, av
from opencc import OpenCC
assert OpenCC('t2s').convert('繁體中文') == '繁体中文'
print('Python pinned dependencies and OpenCC OK')
'@
            $pythonReady = $false
            if (Test-Path -LiteralPath $python) { & $python -I -X utf8 -c $pythonProbe (Join-Path $repo 'recognition/requirements-lock.txt') 2>&1 | Out-Host; $pythonReady = $LASTEXITCODE -eq 0 }
            if (!$pythonReady) { $bootstrap = Find-BootstrapPython; & (Join-Path $repo 'packaging/prepare-python.ps1') -BootstrapPython $bootstrap }
        }
        if ($selectedPlugins -contains 'bilibili') {
            if (!(Test-Path -LiteralPath $python)) { $bootstrap = Find-BootstrapPython; $python = $bootstrap }
            $pluginRoot = Join-Path $repo 'runtime/bilibili'
            $pluginReady = $true
            foreach ($required in @('ffmpeg/ffmpeg.exe','ffmpeg/ffprobe.exe','ffmpeg/LICENSE.txt','vendor/yt_dlp/__init__.py','vendor/qrcode/__init__.py','dependency-manifest.json')) {
                if (!(Test-Path -LiteralPath (Join-Path $pluginRoot $required))) { $pluginReady = $false }
            }
            if ($pluginReady) {
                & $python -I -X utf8 -c 'import sys; sys.path.insert(0,sys.argv[1]); import yt_dlp, qrcode; from importlib.metadata import version; assert version("yt-dlp") == "2026.8.19"; assert version("qrcode") == "8.2"; print("Bilibili Python dependencies OK")' (Join-Path $pluginRoot 'vendor') 2>&1 | Out-Host
                $pluginReady = $LASTEXITCODE -eq 0
                & (Join-Path $pluginRoot 'ffmpeg/ffmpeg.exe') -version 2>&1 | Select-Object -First 1 | Out-Host
                $pluginReady = $pluginReady -and $LASTEXITCODE -eq 0
            }
            if (!$pluginReady) { if (!$bootstrap) { $bootstrap = Find-BootstrapPython }; & (Join-Path $repo 'packaging/prepare-bilibili.ps1') -BootstrapPython $bootstrap }
        }
        Write-Host '编译并准备所选发布内容…' -ForegroundColor Cyan
        $pluginArgument = if ($selectedPlugins.Count) { $selectedPlugins -join ',' } else { 'none' }
        $modelArgument = if ($selectedModels.Count) { $selectedModels -join ',' } else { 'none' }
        if ($kind -eq 'app') {
            if ($VcCrtDirectory) { $VcCrtDirectory = [IO.Path]::GetFullPath($VcCrtDirectory, $repo) }
            & (Join-Path $repo 'packaging/build-portable.ps1') -OutputDirectory $package -VcCrtDirectory $VcCrtDirectory -Plugins $pluginArgument -Models $modelArgument
        } else {
            & (Join-Path $repo 'packaging/build-plugins.ps1') -OutputDirectory $package -Plugins $Plugin
            [ordered]@{ kind = 'plugin'; createdUtc = [DateTime]::UtcNow.ToString('o'); architecture = 'win-x64'; plugins = @($Plugin); models = @(); requiredHostApi = ($pluginCatalog | Where-Object Id -eq $Plugin).Manifest.apiVersion } |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
            $null = Assert-PackageLayout $package $repo
        }
        $details = $null
        if ($Mode -eq 'Package') {
            Write-Host '创建 ZIP 与 SHA-256，随后在中文路径解压验证…' -ForegroundColor Cyan
            $result = & (Join-Path $repo 'packaging/build-delivery.ps1') -PackageDirectory $package -OutputZip $zip
            $details = ($result -join "`n") | ConvertFrom-Json
            $result | Out-Host
            & (Join-Path $repo 'packaging/test-delivery.ps1') -ZipPath $zip -PythonPath $python
        }
        $record = [ordered]@{ kind = $kind; mode = $Mode; plugins = @($selectedPlugins); models = @($selectedModels); packageDirectory = $package; zip = $details.Zip; sha256 = $details.SHA256; sizeMiB = $details.MiB; verification = $(if ($Mode -eq 'Package') { 'extracted-package' } else { 'published-layout' }); createdUtc = [DateTime]::UtcNow.ToString('o'); buildLog = $log }
        $recordName = if ($kind -eq 'plugin') { "latest-$Plugin-plugin.json" } else { 'latest-build.json' }
        $temporaryRecord = Join-Path $work $recordName
        $record | ConvertTo-Json | Set-Content -LiteralPath $temporaryRecord -Encoding utf8
        Move-Item -LiteralPath $temporaryRecord -Destination (Join-Path $OutputDirectory $recordName) -Force
        Write-Host "成功，用时 $([math]::Round($watch.Elapsed.TotalSeconds)) 秒。发布目录：$package" -ForegroundColor Green
        if ($details) { Write-Host "交付压缩包：$zip"; Write-Host "SHA-256：$($details.SHA256)" }
        Write-Host "构建日志：$log"
        $exitCode = 0
    } finally { Pop-Location }
} catch { Write-Host "构建失败：$($_.Exception.Message)" -ForegroundColor Red; Write-Host $_.ScriptStackTrace }
finally { if ($transcribing) { Stop-Transcript | Out-Null } }
exit $exitCode
