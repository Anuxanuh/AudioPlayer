#Requires -Version 7.0
<#
.SYNOPSIS
编译声屿及插件，生成不含模型和用户数据的便携 ZIP，并验证交付包。
.DESCRIPTION
依赖可复用时不重复下载；依赖缺失时使用带 pip 的开发 Python 自动准备。
需 Windows x64、.NET 10 SDK、PowerShell 7，以及 Visual C++ 可再发行 CRT 目录。
.PARAMETER OutputDirectory
ZIP、校验文件和成功记录的保存目录；默认项目 artifacts。相对路径以项目根目录为基准。
.PARAMETER BootstrapPython
依赖缺失时用于 pip 的 python.exe；默认自动查找 .venv、python 或 py 启动器。
.PARAMETER VcCrtDirectory
Visual C++ x64 可再发行 CRT 目录；默认由 Visual Studio Installer 定位。
#>
[CmdletBinding()]
param([string]$OutputDirectory = '', [string]$BootstrapPython = '', [string]$VcCrtDirectory = '')

$ErrorActionPreference = 'Stop'
# robocopy uses nonzero success codes; each native command is checked at its call site.
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repo = $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $repo)
$buildId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$work = Join-Path $repo ('artifacts/build-' + $buildId)
$package = Join-Path $work 'ShengYu'
$zip = Join-Path $OutputDirectory ('ShengYu-win-x64-NoModels-' + $buildId + '.zip')
$log = Join-Path $repo ('artifacts/build-logs/' + $buildId + '.log')
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
    throw '首次准备依赖需要带 pip 的开发 Python。请安装 Python，或运行 ./build.ps1 -BootstrapPython "C:/Python/python.exe"。已有完整 runtime 时不需要开发 Python。'
}

try {
    if (!$IsWindows -or ![Environment]::Is64BitProcess) { throw '请使用 Windows x64 的 PowerShell 7 构建。' }
    New-Item -ItemType Directory -Force -Path $OutputDirectory,$work,(Split-Path -Parent $log) | Out-Null
    Start-Transcript -LiteralPath $log | Out-Null
    $transcribing = $true
    Push-Location -LiteralPath $repo
    try {
        Write-Host '[1/5] 检查构建工具' -ForegroundColor Cyan
        if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '缺少 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0' }
        $sdk = & dotnet --version
        if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw "本项目需要 .NET 10 SDK，当前解析版本：$sdk。https://dotnet.microsoft.com/download/dotnet/10.0" }
        Write-Host "SDK：$sdk；构建日志：$log"
        if ($VcCrtDirectory) { $VcCrtDirectory = [IO.Path]::GetFullPath($VcCrtDirectory, $repo) }

        Write-Host '[2/5] 检查并准备 Python / FFmpeg / 插件依赖' -ForegroundColor Cyan
        $python = Join-Path $repo 'runtime/python/python.exe'
        $pythonProbe = @'
import sys, struct
from pathlib import Path
from importlib.metadata import version
assert sys.version_info[:3] == (3, 13, 16) and struct.calcsize('P') == 8, 'Expected Python 3.13.16 x64'
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
        if (Test-Path -LiteralPath $python) {
            & $python -I -X utf8 -c $pythonProbe (Join-Path $repo 'recognition/requirements-lock.txt') 2>&1 | Out-Host
            $pythonReady = $LASTEXITCODE -eq 0
        }
        if (!$pythonReady) {
            $bootstrap = Find-BootstrapPython
            & (Join-Path $repo 'packaging/prepare-python.ps1') -BootstrapPython $bootstrap
        }
        $pluginRoot = Join-Path $repo 'runtime/bilibili'
        $pluginReady = $true
        foreach ($required in @('ffmpeg/ffmpeg.exe', 'ffmpeg/ffprobe.exe', 'ffmpeg/LICENSE.txt', 'vendor/yt_dlp/__init__.py', 'vendor/qrcode/__init__.py', 'dependency-manifest.json')) {
            if (!(Test-Path -LiteralPath (Join-Path $pluginRoot $required))) { $pluginReady = $false }
        }
        if ($pluginReady) {
            & $python -I -X utf8 -c 'import sys; sys.path.insert(0,sys.argv[1]); import yt_dlp, qrcode; from importlib.metadata import version; assert version("yt-dlp") == "2026.8.19"; assert version("qrcode") == "8.2"; print("Plugin Python dependencies OK")' (Join-Path $pluginRoot 'vendor') 2>&1 | Out-Host
            $pluginReady = $LASTEXITCODE -eq 0
            & (Join-Path $pluginRoot 'ffmpeg/ffmpeg.exe') -version 2>&1 | Select-Object -First 1 | Out-Host
            $pluginReady = $pluginReady -and $LASTEXITCODE -eq 0
        }
        if (!$pluginReady) {
            if (!$bootstrap) { $bootstrap = Find-BootstrapPython }
            & (Join-Path $repo 'packaging/prepare-bilibili.ps1') -BootstrapPython $bootstrap
        }

        Write-Host '[3/5] 编译主程序和插件，生成无模型便携目录' -ForegroundColor Cyan
        & (Join-Path $repo 'packaging/build-portable.ps1') -OutputDirectory $package -VcCrtDirectory $VcCrtDirectory -NoModels
        Write-Host '[4/5] 创建交付 ZIP 和 SHA-256 校验文件' -ForegroundColor Cyan
        $result = & (Join-Path $repo 'packaging/build-delivery.ps1') -PackageDirectory $package -OutputZip $zip
        $result | Out-Host
        Write-Host '[5/5] 解压到中文路径并执行便携自检' -ForegroundColor Cyan
        & (Join-Path $repo 'packaging/test-delivery.ps1') -ZipPath $zip

        $details = ($result -join "`n") | ConvertFrom-Json
        $record = [ordered]@{ zip = $zip; sha256 = $details.SHA256; sizeMiB = $details.MiB; verified = $true; createdUtc = [DateTime]::UtcNow.ToString('o'); buildLog = $log; buildDirectory = $work }
        $recordPath = Join-Path $OutputDirectory 'latest-build.json'
        $temporaryRecord = Join-Path $work 'latest-build.json'
        $record | ConvertTo-Json | Set-Content -LiteralPath $temporaryRecord -Encoding utf8
        Move-Item -LiteralPath $temporaryRecord -Destination $recordPath -Force
        Write-Host "构建及交付验证成功，用时 $([math]::Round($watch.Elapsed.TotalSeconds)) 秒。" -ForegroundColor Green
        Write-Host "交付压缩包：$zip"
        Write-Host "SHA-256：$($details.SHA256)"
        Write-Host "构建日志：$log"
        $exitCode = 0
    }
    finally { Pop-Location }
}
catch {
    Write-Host "构建失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    if ($transcribing) { Write-Host "详细日志：$log" }
}
finally { if ($transcribing) { Stop-Transcript | Out-Null } }
exit $exitCode
