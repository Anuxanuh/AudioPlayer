param([Parameter(Mandatory)][string]$ZipPath, [string]$PythonPath = '')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'build-common.ps1')
$testRoot = Join-Path $repo ('artifacts/交付验证 ' + [Guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($ZipPath), $testRoot)
$package = if (Test-Path -LiteralPath (Join-Path $testRoot 'ShengYu/package-manifest.json')) { Join-Path $testRoot 'ShengYu' } else { $testRoot }
$manifest = Assert-PackageLayout $package $repo
foreach ($id in $manifest.models) { $null = @(Get-ModelBuildFiles (Join-Path $package "models/faster-whisper-$id") $id -VerifyHash) }
if ($manifest.kind -eq 'app') { $PythonPath = Join-Path $package 'python/python.exe' }
if ($manifest.plugins -contains 'bilibili') {
    if (!$PythonPath) { $PythonPath = Join-Path $repo 'runtime/python/python.exe' }
    $plugin = Join-Path $package 'plugins/bilibili'
    & $PythonPath -I -X utf8 -c 'import sys; sys.path.insert(0,sys.argv[1]); import yt_dlp, qrcode; from importlib.metadata import version; assert version("yt-dlp") == "2026.8.19"; assert version("qrcode") == "8.2"; print("Bilibili Python dependencies OK")' (Join-Path $plugin 'vendor')
    if ($LASTEXITCODE -ne 0) { throw '插件 Python 依赖不完整。' }
    & (Join-Path $plugin 'ffmpeg/ffmpeg.exe') -version | Select-Object -First 1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '插件 FFmpeg 不可用。' }
}
if ($manifest.kind -eq 'plugin') {
    Write-Host "PASS: 独立插件 $($manifest.plugins -join ',') 中文路径解压、清单、程序集与依赖验证；不含主程序、模型或用户数据。"
    return
}
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
    if (!$process.WaitForExit(60000)) { $process.Kill($true); $process.WaitForExit(); throw '交付包自检超时。' }
    $reportPath = Join-Path $package 'data/portable-check.json'
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or !$report.localPython -or !$report.localModels -or !$report.report.CpuReady -or $report.modelCount -ne $manifest.models.Count) { throw ('交付包自检失败：' + ($report | ConvertTo-Json -Depth 8)) }
    if (!($report.report.Items | Where-Object { $_.Name -like 'OpenCC*' -and $_.Status -eq 'ok' })) { throw 'OpenCC 字典自检失败。' }
    $logs = @(Get-ChildItem -LiteralPath (Join-Path $package 'logs') -Filter 'AudioPlayer-*.log')
    $text = ($logs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    if ($text -notmatch 'Application starting' -or $text -notmatch 'Application exiting; exitCode=0') { throw '启动退出日志缺失。' }
    Copy-Item -LiteralPath $reportPath -Destination (Join-Path $repo 'artifacts/delivery-check.json') -Force
    Write-Host "PASS: 所选插件 [$($manifest.plugins -join ',')]、模型 [$($manifest.models -join ',')]，无用户数据；中文路径解压、自包含 .NET / Python / OpenCC 与日志自检成功。"
} finally { if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
