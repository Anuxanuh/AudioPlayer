param([string]$OutputDirectory = '', [string]$VcCrtDirectory = '', [string[]]$Plugins = @('all'), [string[]]$Models = @('none'), [switch]$NoModels)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'build-common.ps1')
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/portable-' + [Guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$pythonRoot = Join-Path $repo 'runtime/python'
$modelsRoot = Join-Path $repo 'models'
if (!(Test-Path -LiteralPath (Join-Path $pythonRoot 'python.exe'))) { throw '请先运行 packaging/prepare-python.ps1。' }
$pluginCatalog = @(Get-PluginBuildCatalog $repo)
$selectedPlugins = @(Resolve-BuildSelection $Plugins $pluginCatalog.Id '插件')
$catalog = @(Get-Content -LiteralPath (Join-Path $repo 'recognition/model_catalog.json') -Raw | ConvertFrom-Json)
if ($NoModels) { if ($PSBoundParameters.ContainsKey('Models')) { throw '不要同时指定 Models 与 NoModels。' }; $Models = @('none') }
$selectedModels = @(Resolve-BuildSelection $Models $catalog.id '模型')
$modelFiles = @{}
foreach ($id in $selectedModels) { $modelFiles[$id] = @(Get-ModelBuildFiles (Join-Path $modelsRoot "faster-whisper-$id") $id -VerifyHash) }
if (!$VcCrtDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installation = & $vswhere -latest -property installationPath
        $crt = Get-ChildItem -Path (Join-Path $installation 'VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT') -Directory | Sort-Object FullName -Descending | Select-Object -First 1
        if ($crt) { $VcCrtDirectory = $crt.FullName }
    }
}
if (!$VcCrtDirectory -or !(Test-Path -LiteralPath (Join-Path $VcCrtDirectory 'msvcp140.dll'))) { throw '请使用 -VcCrtDirectory 指定 Visual C++ 可再发行 CRT 目录，以保证无须安装运行库。' }
Initialize-BuildDirectory $OutputDirectory
dotnet publish (Join-Path $repo 'AudioPlayer/AudioPlayer.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw '发布 .NET 自包含程序失败。' }
& (Join-Path $PSScriptRoot 'build-plugins.ps1') -OutputDirectory $OutputDirectory -Plugins $Plugins
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDirectory 'python'),(Join-Path $OutputDirectory 'licenses') | Out-Null
Copy-BuildTree $pythonRoot (Join-Path $OutputDirectory 'python')
foreach ($id in $selectedModels) {
    $name = "faster-whisper-$id"
    $destination = Join-Path $OutputDirectory "models/$name"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in $modelFiles[$id]) { Copy-Item -LiteralPath (Join-Path $modelsRoot "$name/$file") -Destination $destination }
}
Get-ChildItem -LiteralPath $VcCrtDirectory -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $OutputDirectory 'python') -Force }
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $OutputDirectory 'README.md') -Force
if (Test-Path -LiteralPath (Join-Path $repo 'licenses')) { Copy-BuildTree (Join-Path $repo 'licenses') (Join-Path $OutputDirectory 'licenses') }
'Portable: keep this directory together. Settings are stored in data; Python and .NET are bundled.' | Set-Content -LiteralPath (Join-Path $OutputDirectory 'portable.flag') -Encoding utf8
@{ kind = 'app'; createdUtc = [DateTime]::UtcNow.ToString('o'); architecture = 'win-x64'; selfContainedDotNet = $true; python = '3.13.16'; plugins = @($selectedPlugins); models = @($selectedModels); vcCrt = (Split-Path -Leaf $VcCrtDirectory) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'package-manifest.json') -Encoding utf8
& (Join-Path $OutputDirectory 'python/python.exe') -I -X utf8 -c 'from faster_whisper import WhisperModel; import onnxruntime,ctranslate2,av,sentencepiece,py3langid; assert py3langid.classify("This is an English sentence.")[0] == "en"; from opencc import OpenCC; assert OpenCC("t2s").convert("繁體中文") == "繁体中文"; print("Portable package Python, translation and OpenCC ready")'
if ($LASTEXITCODE -ne 0) { throw '发布目录中的 Python 检查失败。' }
$null = Assert-PackageLayout $OutputDirectory $repo
Write-Host "便携目录已生成：$OutputDirectory"
