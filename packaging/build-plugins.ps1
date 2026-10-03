param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'plugins/bilibili'
$runtime = Join-Path $repo 'runtime/bilibili'
foreach ($required in @('ffmpeg/ffmpeg.exe','ffmpeg/ffprobe.exe','vendor/yt_dlp/__init__.py','vendor/qrcode/__init__.py')) {
    if (!(Test-Path -LiteralPath (Join-Path $runtime $required))) { throw "插件依赖缺失：$required，请运行 packaging/prepare-bilibili.ps1。" }
}
dotnet publish (Join-Path $repo 'Plugins/Bilibili/AudioPlayer.Plugin.Bilibili.csproj') -c Release -r win-x64 --self-contained false -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw '插件构建失败。' }
foreach ($name in @('ffmpeg','vendor')) {
    & robocopy (Join-Path $runtime $name) (Join-Path $destination $name) /E /XD __pycache__ /XF '*.pyc' /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "插件依赖复制失败：$name" }
}
Copy-Item -LiteralPath (Join-Path $repo 'Plugins/Bilibili/README.md') -Destination (Join-Path $destination 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $runtime 'dependency-manifest.json') -Destination (Join-Path $destination 'dependency-manifest.json') -Force
Write-Host "Bilibili 插件已生成（默认关闭）：$destination"
