param([string]$OutputDirectory = '', [string]$VcCrtDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/ShengYu-Portable-win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$pythonRoot = Join-Path $repo 'runtime/python'
$modelsRoot = Join-Path $repo 'models'
if (!(Test-Path -LiteralPath (Join-Path $pythonRoot 'python.exe'))) { throw '请先运行 packaging/prepare-python.ps1。' }
$catalog = Get-Content -LiteralPath (Join-Path $repo 'recognition/model_catalog.json') -Raw | ConvertFrom-Json
foreach ($model in $catalog) {
    $modelDirectory = Join-Path $modelsRoot "faster-whisper-$($model.id)"
    foreach ($file in @('model.bin','config.json','tokenizer.json','download-manifest.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $modelDirectory $file))) { throw "模型尚未完整下载：$($model.id) / $file" }
    }
}
if (!$VcCrtDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installation = & $vswhere -latest -property installationPath
        $crt = Get-ChildItem -Path (Join-Path $installation 'VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT') -Directory | Sort-Object FullName -Descending | Select-Object -First 1
        if ($crt) { $VcCrtDirectory = $crt.FullName }
    }
}
if (!$VcCrtDirectory -or !(Test-Path -LiteralPath (Join-Path $VcCrtDirectory 'msvcp140.dll'))) { throw '请使用 -VcCrtDirectory 指定 Visual C++ 可再发行 CRT 目录，以保证无须安装运行库。' }
dotnet publish (Join-Path $repo 'AudioPlayer/AudioPlayer.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw '发布 .NET 自包含程序失败。' }
& (Join-Path $PSScriptRoot 'build-plugins.ps1') -OutputDirectory $OutputDirectory
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDirectory 'python'),(Join-Path $OutputDirectory 'models'),(Join-Path $OutputDirectory 'data'),(Join-Path $OutputDirectory 'licenses') | Out-Null
function Copy-Tree([string]$Source, [string]$Target) {
    # /E copies without purging existing user data; models remain real files, not junctions.
    & robocopy $Source $Target /E /XD __pycache__ .cache /XF '*.pyc' /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "复制失败：$Source" }
}
Copy-Tree $pythonRoot (Join-Path $OutputDirectory 'python')
foreach ($model in $catalog) {
    $name = "faster-whisper-$($model.id)"
    Copy-Tree (Join-Path $modelsRoot $name) (Join-Path $OutputDirectory "models/$name")
}
Get-ChildItem -LiteralPath $VcCrtDirectory -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $OutputDirectory 'python') -Force }
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $OutputDirectory 'README.md') -Force
if (Test-Path -LiteralPath (Join-Path $repo 'licenses')) { Copy-Tree (Join-Path $repo 'licenses') (Join-Path $OutputDirectory 'licenses') }
'Portable: keep this directory together. Settings are stored in data; Python, .NET and models are bundled.' | Set-Content -LiteralPath (Join-Path $OutputDirectory 'portable.flag') -Encoding utf8
@{ createdUtc = [DateTime]::UtcNow.ToString('o'); architecture = 'win-x64'; selfContainedDotNet = $true; python = '3.13.16'; models = @($catalog.id); vcCrt = (Split-Path -Leaf $VcCrtDirectory) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'package-manifest.json') -Encoding utf8
& (Join-Path $OutputDirectory 'python/python.exe') -I -c 'from faster_whisper import WhisperModel; import onnxruntime,ctranslate2,av; from opencc import OpenCC; assert OpenCC("t2s").convert("繁體中文") == "繁体中文"; print("Portable package Python and OpenCC ready")'
if ($LASTEXITCODE -ne 0) { throw '发布目录中的 Python 检查失败。' }
Write-Host "便携目录已生成：$OutputDirectory"
