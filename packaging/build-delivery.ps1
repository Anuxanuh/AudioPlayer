param([Parameter(Mandatory)][string]$PackageDirectory, [string]$OutputZip = '')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'build-common.ps1')
$artifacts = Join-Path $repo 'artifacts'
$source = [IO.Path]::GetFullPath($PackageDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $source 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.kind -notin @('app','plugin') -or $null -eq $manifest.plugins -or $null -eq $manifest.models) { throw '请先使用根目录 build.cmd 构建发布目录。' }
if (!$OutputZip) { $OutputZip = Join-Path $artifacts ('ShengYu-' + $manifest.kind + '-' + [Guid]::NewGuid().ToString('N') + '.zip') }
$OutputZip = [IO.Path]::GetFullPath($OutputZip)
if (Test-Path -LiteralPath $OutputZip) { throw "输出已经存在，不覆盖：$OutputZip" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputZip) | Out-Null
$stageRoot = Join-Path $artifacts ('delivery-stage-' + [Guid]::NewGuid().ToString('N'))
$stage = if ($manifest.kind -eq 'app') { Join-Path $stageRoot 'ShengYu' } else { $stageRoot }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
if ($manifest.kind -eq 'app') {
    # Select plugins/models explicitly; never copy personal root data or download caches.
    $excluded = @('data','models','plugins','logs','Downloads') | ForEach-Object { Join-Path $source $_ }
    & robocopy $source $stage /E /XD $excluded __pycache__ .cache .downloads .backups /XF '*.pyc' /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) { throw '复制交付文件失败。' }
    $readme = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'README-delivery.md') -Raw
    $modelText = if ($manifest.models.Count) { $manifest.models -join '、' } else { '无（可在程序内下载或选择本地模型）' }
    $pluginText = if ($manifest.plugins.Count) { $manifest.plugins -join '、' } else { '无' }
    $dotnetText = if ($manifest.selfContainedDotNet) { '已附带 .NET 桌面运行时，无须预装 .NET。' } else { '未附带 .NET 运行时，使用本机已安装的 .NET 10 Desktop Runtime x64。' }
    $pythonText = if ($manifest.bundledPython -ne $false) { '已附带独立 Python、faster-whisper、翻译引擎和 OpenCC 字典，无须预装 Python。' } else { '未附带 Python 或识别／翻译依赖。程序自动查找本机 Python；没有时会提示自行准备，普通播放不受影响。准备方法见下方“本机 Python 环境”。' }
    $readme.Replace('{{MODELS}}', $modelText).Replace('{{PLUGINS}}', $pluginText).Replace('{{DOTNET}}', $dotnetText).Replace('{{PYTHON}}', $pythonText) | Set-Content -LiteralPath (Join-Path $stage 'README.md') -Encoding utf8
}
$catalog = @(Get-PluginBuildCatalog $repo)
foreach ($id in $manifest.plugins) {
    $plugin = $catalog | Where-Object Id -eq $id
    if (!$plugin) { throw "清单包含未知插件：$id" }
    Copy-BuildTree (Join-Path $source "plugins/$id") (Join-Path $stage "plugins/$id")
}
foreach ($id in $manifest.models) {
    if ($manifest.kind -ne 'app') { throw '独立插件包不能包含模型。' }
    if (@((Get-Content -LiteralPath (Join-Path $repo 'recognition/model_catalog.json') -Raw | ConvertFrom-Json).id) -notcontains $id) { throw "清单包含未知模型：$id" }
    $modelSource = Join-Path $source "models/faster-whisper-$id"
    $files = @(Get-ModelBuildFiles $modelSource $id)
    $destination = Join-Path $stage "models/faster-whisper-$id"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $modelSource $file) -Destination $destination }
}
$manifest.createdUtc = [DateTime]::UtcNow.ToString('o')
$flavor = if ($manifest.kind -eq 'plugin') { 'standalone-plugin' } elseif ($manifest.models.Count) { 'selected-models-no-user-data' } else { 'no-models-no-user-data' }
$manifest | Add-Member -NotePropertyName flavor -NotePropertyValue $flavor -Force
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Encoding utf8
$null = Assert-PackageLayout $stage $repo
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $OutputZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($OutputZip)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $prefix = if ($manifest.kind -eq 'app') { 'ShengYu/' } else { '' }
    if ($names -notcontains ($prefix + 'package-manifest.json') -or ($names | Where-Object { $_ -match '(?i)/session\.bin$|/(\.cache|\.downloads|__pycache__)/|\.pyc$|WebView2' })) { throw '交付 ZIP 含有错误或未排除的文件。' }
    $entries = $archive.Entries.Count
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $OutputZip -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  ' + [IO.Path]::GetFileName($OutputZip)) | Set-Content -LiteralPath ($OutputZip + '.sha256') -Encoding ascii
[pscustomobject]@{ Zip = $OutputZip; MiB = [math]::Round((Get-Item -LiteralPath $OutputZip).Length / 1MB, 2); Entries = $entries; SHA256 = $hash; StagingDirectory = $stage; Plugins = @($manifest.plugins); Models = @($manifest.models) } | ConvertTo-Json
