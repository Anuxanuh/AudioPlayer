param([Parameter(Mandatory)][string]$ZipPath)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$testRoot = Join-Path $repo ('artifacts/交付验证 无模型 ' + [Guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($ZipPath), $testRoot)
$package = Join-Path $testRoot 'ShengYu'
foreach ($name in @('models', 'data', 'logs', 'Downloads')) {
    if (Test-Path -LiteralPath (Join-Path $package $name)) { throw "交付包含有 $name 目录。" }
}
$manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.models.Count -ne 0 -or $manifest.flavor -ne 'no-models-no-user-data') { throw '交付清单不正确。' }
$plugin = Join-Path $package 'plugins/bilibili'
if (!(Test-Path -LiteralPath (Join-Path $plugin 'plugin.json'))) { throw '交付缺少插件。' }
& (Join-Path $package 'python/python.exe') -I -c 'import sys; sys.path.insert(0,sys.argv[1]); import yt_dlp, qrcode; from yt_dlp.version import __version__; print("Plugin Python dependencies OK", __version__)' (Join-Path $plugin 'vendor')
if ($LASTEXITCODE -ne 0) { throw '插件 Python 依赖不完整。' }
& (Join-Path $plugin 'ffmpeg/ffmpeg.exe') -version | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { throw '插件 FFmpeg 不可用。' }
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = Join-Path $package 'AudioPlayer.exe'
$info.WorkingDirectory = $testRoot
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$info.ArgumentList.Add('--portable-check')
$info.Environment['PATH'] = Join-Path $env:WINDIR 'System32'
$info.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'absent-dotnet'
$info.Environment['DOTNET_ROOT_X64'] = Join-Path $testRoot 'absent-dotnet'
$info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$info.Environment['PYTHONHOME'] = Join-Path $testRoot 'absent-python'
$info.Environment['PYTHONPATH'] = Join-Path $testRoot 'absent-python-packages'
$info.Environment['HF_HUB_OFFLINE'] = '1'
$process = [Diagnostics.Process]::Start($info)
try {
    if (!$process.WaitForExit(60000)) { $process.Kill($true); $process.WaitForExit(); throw '无模型包自检超时。' }
    $reportPath = Join-Path $package 'data/portable-check.json'
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or !$report.localPython -or !$report.report.CpuReady -or $report.modelCount -ne 0) { throw ('无模型包自检失败：' + ($report | ConvertTo-Json -Depth 8)) }
    if (!($report.report.Items | Where-Object { $_.Name -like 'OpenCC*' -and $_.Status -eq 'ok' })) { throw 'OpenCC 字典自检失败。' }
    $logs = @(Get-ChildItem -LiteralPath (Join-Path $package 'logs') -Filter 'AudioPlayer-*.log')
    $text = ($logs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    if ($text -notmatch 'Application starting' -or $text -notmatch 'Application exiting; exitCode=0') { throw '启动退出日志缺失。' }
    Copy-Item -LiteralPath $reportPath -Destination (Join-Path $repo 'artifacts/delivery-check.json') -Force
    Write-Host 'PASS: ZIP 无模型、无用户数据及日志；中文路径解压后，隔离开发机环境，自包含 .NET / Python / OpenCC 自检成功，Serilog 正常生成日志。'
}
finally { if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
