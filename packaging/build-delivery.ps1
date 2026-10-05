param([string]$PackageDirectory = '', [string]$OutputZip = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $repo 'artifacts'
if (!$PackageDirectory) { $PackageDirectory = Join-Path $artifacts 'ShengYu-Portable-win-x64' }
$source = [IO.Path]::GetFullPath($PackageDirectory)
if (!(Test-Path -LiteralPath (Join-Path $source 'portable.flag'))) { throw '请先构建完整便携目录。' }
if (!$OutputZip) { $OutputZip = Join-Path $artifacts ('ShengYu-win-x64-NoModels-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip') }
$OutputZip = [IO.Path]::GetFullPath($OutputZip)
if (Test-Path -LiteralPath $OutputZip) { throw "输出已经存在，不覆盖：$OutputZip" }
$stageRoot = Join-Path $artifacts ('delivery-stage-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $stageRoot 'ShengYu'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
# Exclude only the application's root data/models/logs; Python package data is required.
$excluded = @('data', 'models', 'logs', 'Downloads') | ForEach-Object { Join-Path $source $_ }
& robocopy $source $stage /E /XD $excluded __pycache__ .cache .downloads /XF '*.pyc' /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -gt 7) { throw '复制交付文件失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-delivery.md') -Destination (Join-Path $stage 'README.md')
$manifest = Get-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Raw | ConvertFrom-Json
$manifest.models = @()
$manifest.createdUtc = [DateTime]::UtcNow.ToString('o')
$manifest | Add-Member -NotePropertyName flavor -NotePropertyValue 'no-models-no-user-data' -Force
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Encoding utf8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $OutputZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($OutputZip)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    if ($names | Where-Object { $_ -match '^ShengYu/(models|data|logs|Downloads)/|/(\.cache|\.downloads|__pycache__)/|\.pyc$' }) { throw '交付 ZIP 含有应排除的文件。' }
    if ($names | Where-Object { $_ -match '(?i)WebView2|/session\.bin$' }) { throw '交付 ZIP 不应包含浏览器运行时或登录凭据。' }
    foreach ($required in @('AudioPlayer.exe', 'AudioPlayer.dll', 'AudioPlayer.Plugin.Abstractions.dll', 'Serilog.dll', 'Serilog.Sinks.File.dll', 'python/python.exe', 'python/Lib/site-packages/opencc/opencc.py', 'recognition/transcribe.py', 'recognition/model_manager.py', 'plugins/bilibili/plugin.json', 'plugins/bilibili/AudioPlayer.Plugin.Bilibili.dll', 'plugins/bilibili/ffmpeg/ffmpeg.exe', 'plugins/bilibili/vendor/qrcode/__init__.py', 'plugins/bilibili/vendor/yt_dlp/__init__.py', 'README.md', 'portable.flag')) {
        if ($names -notcontains ('ShengYu/' + $required)) { throw "交付 ZIP 缺少 $required" }
    }
    foreach ($required in @('plugins/novel/plugin.json','plugins/novel/AudioPlayer.Plugin.Novel.dll','plugins/novel/TagLibSharp.dll','plugins/novel/README.md')) {
        if ($names -notcontains ('ShengYu/' + $required)) { throw "交付 ZIP 缺少 $required" }
    }
    $entries = $archive.Entries.Count
}
finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $OutputZip -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  ' + [IO.Path]::GetFileName($OutputZip)) | Set-Content -LiteralPath ($OutputZip + '.sha256') -Encoding ascii
[pscustomobject]@{ Zip = $OutputZip; MiB = [math]::Round((Get-Item -LiteralPath $OutputZip).Length / 1MB, 2); Entries = $entries; SHA256 = $hash; StagingDirectory = $stage } | ConvertTo-Json
