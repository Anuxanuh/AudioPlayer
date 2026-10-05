param([string]$PackageDirectory = '', [string]$SampleAudio = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
if (!$PackageDirectory) { $PackageDirectory = Join-Path $artifacts 'ShengYu-Portable-win-x64' }
if (!$SampleAudio) { $SampleAudio = Join-Path $artifacts 'offline-speech.wav' }
$original = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$relocated = [IO.Path]::GetFullPath((Join-Path $artifacts ('便携验证 声屿 ' + [Guid]::NewGuid().ToString('N'))))
function Assert-ArtifactPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (!$resolved.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "移动目标必须位于本项目 artifacts 内：$resolved"
    }
    if ((Test-Path -LiteralPath $resolved) -and ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "不移动目录链接：$resolved"
    }
}
Assert-ArtifactPath $original
Assert-ArtifactPath $relocated
if (!(Test-Path -LiteralPath (Join-Path $original 'portable.flag'))) { throw '目标不是完整便携包。' }
if (!(Test-Path -LiteralPath $SampleAudio)) { throw '缺少用于真实识别验证的音频。' }
if (Test-Path -LiteralPath $relocated) { throw '临时移动目录已经存在。' }
$process = $null
$moved = $false
$expectedCount = @( (Get-Content -LiteralPath (Join-Path $original 'package-manifest.json') -Raw | ConvertFrom-Json).models ).Count
if ($expectedCount -eq 0) { throw '真实识别移动验证需要包含至少一个模型的发布目录；无模型 ZIP 请用 test-delivery.ps1。' }
try {
    Move-Item -LiteralPath $original -Destination $relocated
    $moved = $true
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Join-Path $relocated 'AudioPlayer.exe'
    $info.WorkingDirectory = $artifacts
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.ArgumentList.Add('--portable-check')
    $info.ArgumentList.Add([IO.Path]::GetFullPath($SampleAudio))
    # The child must find the shipped runtime and packages without developer PATH entries.
    $info.Environment['PATH'] = Join-Path $env:WINDIR 'System32'
    $info.Environment['DOTNET_ROOT'] = Join-Path $relocated 'absent-dotnet'
    $info.Environment['DOTNET_ROOT_X64'] = Join-Path $relocated 'absent-dotnet'
    $info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $info.Environment['PYTHONHOME'] = Join-Path $relocated 'absent-python'
    $info.Environment['PYTHONPATH'] = Join-Path $relocated 'absent-python-packages'
    $info.Environment['HF_HUB_OFFLINE'] = '1'
    $process = [Diagnostics.Process]::Start($info)
    if (!$process.WaitForExit(60000)) { $process.Kill($true); $process.WaitForExit(); throw '便携自检超时。' }
    $reportPath = Join-Path $relocated 'data/portable-check.json'
    if (!(Test-Path -LiteralPath $reportPath)) { throw "自检未生成报告，退出码 $($process.ExitCode)。" }
    Copy-Item -LiteralPath $reportPath -Destination (Join-Path $artifacts 'portable-relocation-check.json') -Force
    Copy-Item -LiteralPath (Join-Path $relocated 'data/portable-check.lrc') -Destination (Join-Path $artifacts 'portable-relocation-check.lrc') -Force
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or !$report.localPython -or !$report.localModels -or $report.modelCount -ne $expectedCount -or !$report.transcription) {
        throw ('便携自检失败：' + ($report | ConvertTo-Json -Depth 8))
    }
    Write-Host "PASS: 中文与空格目录，隔离开发机 PATH / Python / .NET，$expectedCount 个模型，真实 CPU 离线识别。"
}
finally {
    if ($process) { if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
    if ($moved) {
        # Revalidate both resolved absolute paths immediately before the restore move.
        Assert-ArtifactPath $relocated
        Assert-ArtifactPath $original
        if (Test-Path -LiteralPath $original) { throw "原目录意外出现，请手动检查；完整包保留在 $relocated" }
        Move-Item -LiteralPath $relocated -Destination $original
    }
}
