param([string]$BootstrapPython = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$cache = Join-Path $repo 'artifacts/downloads'
$runtime = Join-Path $repo 'runtime/bilibili'
New-Item -ItemType Directory -Force -Path $cache,$runtime | Out-Null
if (!$BootstrapPython) { $BootstrapPython = Join-Path $repo '.venv/Scripts/python.exe' }
$dependencies = @(
    @{file='ffmpeg-n8.1.3-14-g330caae0c1-win64-lgpl-shared-8.1.zip'; url='https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-01-13-06/ffmpeg-n8.1.3-14-g330caae0c1-win64-lgpl-shared-8.1.zip'; sha256='BF545D8FEE9BB6957C1F3DEA0F384BF64EDEAD407D763326DBBD2DE1B04768A4'},
    @{file='yt_dlp-2026.8.19-py3-none-any.whl'; url='https://pypi.org/project/yt-dlp/2026.8.19/'; package='yt-dlp==2026.8.19'; sha256='1D57897E94C6665A0A6F9BC54B34E584284E32C034FFAB3A7DF25D8F7B24EEDF'},
    @{file='qrcode-8.2-py3-none-any.whl'; url='https://pypi.org/project/qrcode/8.2/'; package='qrcode==8.2'; sha256='16E64E0716C14960108E85D853062C9E8BBA5CA8252C0B4D0231B9DF4060FF4F'}
)
foreach ($dependency in $dependencies) {
    $archive = Join-Path $cache $dependency.file
    if (!(Test-Path -LiteralPath $archive)) {
        if ($dependency.file -like '*.whl') {
            & $BootstrapPython -m pip download --no-deps --dest $cache $dependency.package
            if ($LASTEXITCODE -ne 0) { throw '插件 Python 依赖下载失败。' }
        } else { Invoke-WebRequest $dependency.url -OutFile $archive }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $dependency.sha256) { throw "依赖校验失败：$archive" }
}
Expand-Archive -LiteralPath (Join-Path $cache $dependencies[0].file) -DestinationPath (Join-Path $runtime 'ffmpeg-extract') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $runtime 'vendor') | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $cache $dependencies[1].file), (Join-Path $runtime 'vendor'), $true)
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $cache $dependencies[2].file), (Join-Path $runtime 'vendor'), $true)
$ffmpegExtracted = Join-Path $runtime ('ffmpeg-extract/' + [IO.Path]::GetFileNameWithoutExtension($dependencies[0].file))
& robocopy (Join-Path $ffmpegExtracted 'bin') (Join-Path $runtime 'ffmpeg') /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -gt 7) { throw '运行时复制失败。' }
Copy-Item -LiteralPath (Join-Path $ffmpegExtracted 'LICENSE.txt') -Destination (Join-Path $runtime 'ffmpeg/LICENSE.txt') -Force
$dependencies | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtime 'dependency-manifest.json') -Encoding utf8
Write-Host 'Bilibili 插件依赖准备完成。'
