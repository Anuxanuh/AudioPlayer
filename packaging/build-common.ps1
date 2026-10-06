# Shared implementation for the repository-root build entry point.
function Get-PluginBuildCatalog([string]$Repository) {
    foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $Repository 'Plugins') -Directory | Sort-Object Name) {
        $manifestPath = Join-Path $directory.FullName 'plugin.json'
        if (!(Test-Path -LiteralPath $manifestPath)) { continue }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $projects = @(Get-ChildItem -LiteralPath $directory.FullName -Filter '*.csproj' -File)
        if ($manifest.id -notmatch '^[a-z0-9][a-z0-9._-]{0,63}$' -or $projects.Count -ne 1 -or
            [IO.Path]::GetFileName($manifest.assembly) -ne $manifest.assembly) { throw "插件构建清单无效：$manifestPath" }
        [pscustomobject]@{ Id = $manifest.id; Name = $manifest.name; Directory = $directory.FullName; Project = $projects[0].FullName; Manifest = $manifest }
    }
}

function Resolve-BuildSelection([AllowEmptyCollection()][string[]]$Values, [string[]]$Available, [string]$Label) {
    $names = @($Values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Select-Object -Unique)
    if (!$names.Count -or $names -contains '') { throw "$Label 参数不能为空；全部使用 all，不包含任何项使用 none。" }
    if ($names -contains 'all' -or $names -contains 'none') {
        if ($names.Count -ne 1) { throw "$Label 的 all / none 不能与其他项混用。" }
        if ($names[0] -eq 'all') { $Available }
        return
    }
    foreach ($name in $names) {
        if ($Available -notcontains $name) { throw "未知${Label}：$name。可选：$($Available -join ', ')、all、none。" }
        $name
    }
}

function Assert-BuildItems([string[]]$Actual, [string[]]$Expected, [string]$Label) {
    if ((($Actual | Sort-Object) -join '|') -cne (($Expected | Sort-Object) -join '|')) { throw "$Label 与选择不一致：实际 [$($Actual -join ',')]，预期 [$($Expected -join ',')]。" }
}

function Get-ModelBuildFiles([string]$Directory, [string]$Id, [switch]$VerifyHash) {
    $manifestPath = Join-Path $Directory 'download-manifest.json'
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "模型未完整下载：$Id；缺少 $manifestPath。请先下载到 models/faster-whisper-$Id。" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.id -ne $Id -or !$manifest.files) { throw "模型下载清单无效：$Id" }
    $files = @($manifest.files)
    foreach ($required in @('model.bin', 'config.json', 'tokenizer.json')) {
        if ($files.name -notcontains $required) { throw "模型 $Id 清单缺少 $required。" }
    }
    if ($files.name -notcontains 'vocabulary.txt' -and $files.name -notcontains 'vocabulary.json') { throw "模型 $Id 缺少词表。" }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        # Download manifests list only model resources, never arbitrary directories or user data.
        if ($file.name -notmatch '^(model\.bin|config\.json|tokenizer\.json|vocabulary\.(txt|json)|preprocessor_config\.json|README\.md|LICENSE(\.txt|\.md)?)$' -or !$seen.Add($file.name)) { throw "模型 $Id 清单含不支持或重复的文件名：$($file.name)" }
        $path = Join-Path $Directory $file.name
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or $file.bytes -le 0) { throw "模型 $Id 文件缺失或大小不符：$($file.name)" }
        if ($VerifyHash -and $file.sha256 -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "模型 $Id 校验失败：$($file.name)" }
        $file.name
    }
    'download-manifest.json'
}

function Get-PluginRequiredFiles($Plugin) {
    $Plugin.Manifest.assembly; 'plugin.json'; 'README.md'
    switch ($Plugin.Id) {
        'bilibili' { 'worker.py'; 'dependency-manifest.json'; 'ffmpeg/ffmpeg.exe'; 'ffmpeg/ffprobe.exe'; 'ffmpeg/LICENSE.txt'; 'vendor/yt_dlp/__init__.py'; 'vendor/qrcode/__init__.py' }
        'novel' { 'TagLibSharp.dll'; 'licenses/TagLibSharp-LGPL-2.1.txt'; 'licenses/TagLibSharp-README.md' }
    }
}

function Assert-PluginBuild([string]$Directory, $Plugin) {
    foreach ($required in Get-PluginRequiredFiles $Plugin) {
        if (!(Test-Path -LiteralPath (Join-Path $Directory $required) -PathType Leaf)) { throw "插件 $($Plugin.Id) 缺少 $required。" }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'plugin.json') -Raw | ConvertFrom-Json
    if ($manifest.id -cne $Plugin.Id -or $manifest.apiVersion -ne $Plugin.Manifest.apiVersion -or $manifest.assembly -cne $Plugin.Manifest.assembly) { throw "插件清单与构建选择不符：$($Plugin.Id)" }
    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Directory $manifest.assembly))
    if ($assembly.Name -ne [IO.Path]::GetFileNameWithoutExtension($manifest.assembly)) { throw "插件程序集名称不符：$($Plugin.Id)" }
    foreach ($name in @('data','logs','cache','Downloads')) {
        if (Test-Path -LiteralPath (Join-Path $Directory $name)) { throw "插件包含运行数据或缓存：$name" }
    }
    # Wheels can contain .data/data for licenses, man pages and other required resources.
    $unwanted = Get-ChildItem -LiteralPath $Directory -Recurse -Force | Where-Object {
        ($_.PSIsContainer -and $_.Name -in @('.cache','.downloads','__pycache__')) -or $_.Name -eq 'session.bin' -or $_.Extension -eq '.pyc'
    } | Select-Object -First 1
    if ($unwanted) { throw "插件包含运行数据或缓存：$($unwanted.FullName)" }
}

function Copy-BuildTree([string]$Source, [string]$Target) {
    & robocopy $Source $Target /E /XD __pycache__ .cache .downloads .backups /XF '*.pyc' /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "复制失败：$Source" }
}

function Initialize-BuildDirectory([string]$Directory) {
    if ((Test-Path -LiteralPath $Directory) -and (Get-ChildItem -LiteralPath $Directory -Force | Select-Object -First 1)) { throw "构建目标目录必须为空，避免残留未选择的插件或模型：$Directory" }
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
}

function Assert-PackageLayout([string]$Directory, [string]$Repository) {
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.kind -notin @('app','plugin') -or $null -eq $manifest.plugins -or $null -eq $manifest.models) { throw '缺少有效构建清单，请使用根目录 build.cmd 重新构建。' }
    $catalog = @(Get-PluginBuildCatalog $Repository)
    $modelIds = @((Get-Content -LiteralPath (Join-Path $Repository 'recognition/model_catalog.json') -Raw | ConvertFrom-Json).id)
    foreach ($id in $manifest.plugins) { if ($catalog.Id -notcontains $id) { throw "构建清单包含未知插件：$id" } }
    foreach ($id in $manifest.models) { if ($modelIds -notcontains $id) { throw "构建清单包含未知模型：$id" } }
    $pluginRoot = Join-Path $Directory 'plugins'
    $pluginNames = @()
    if (Test-Path -LiteralPath $pluginRoot) { $pluginNames = @(Get-ChildItem -LiteralPath $pluginRoot -Directory | ForEach-Object Name) }
    Assert-BuildItems $pluginNames @($manifest.plugins) '插件目录'
    foreach ($plugin in $catalog | Where-Object { $_.Id -in $manifest.plugins }) { Assert-PluginBuild (Join-Path $pluginRoot $plugin.Id) $plugin }
    foreach ($name in @('data','logs','Downloads')) { if (Test-Path -LiteralPath (Join-Path $Directory $name)) { throw "交付目录包含用户数据：$name" } }
    $modelRoot = Join-Path $Directory 'models'
    $modelNames = @()
    if (Test-Path -LiteralPath $modelRoot) { $modelNames = @(Get-ChildItem -LiteralPath $modelRoot -Force | ForEach-Object Name) }
    Assert-BuildItems $modelNames @($manifest.models | ForEach-Object { "faster-whisper-$_" }) '模型目录'
    foreach ($id in $manifest.models) {
        $path = Join-Path $modelRoot "faster-whisper-$id"
        $files = @(Get-ModelBuildFiles $path $id)
        Assert-BuildItems @(Get-ChildItem -LiteralPath $path -Force | ForEach-Object Name) $files "模型 $id 文件"
    }
    if ($manifest.kind -eq 'plugin') {
        if ($manifest.plugins.Count -ne 1 -or $manifest.models.Count -ne 0) { throw '独立插件包必须只含一个插件，不能包含模型。' }
        Assert-BuildItems @(Get-ChildItem -LiteralPath $Directory -Force | ForEach-Object Name) @('plugins','package-manifest.json') '独立插件包根目录'
    } else {
        $requiredFiles = @('AudioPlayer.exe','AudioPlayer.dll','AudioPlayer.Plugin.Abstractions.dll','Serilog.dll','Serilog.Sinks.File.dll','AudioPlayer.runtimeconfig.json','recognition/transcribe.py','recognition/translate_lyrics.py','recognition/model_manager.py','README.md','portable.flag')
        # Older manifests predate the switch and always included Python.
        if ($manifest.bundledPython -ne $false) {
            $requiredFiles += @('python/python.exe','python/Lib/site-packages/opencc/opencc.py')
        } else {
            if (Test-Path -LiteralPath (Join-Path $Directory 'python')) { throw '不带 Python 的包残留 python 目录。' }
            $requiredFiles += 'recognition/requirements-lock.txt'
        }
        if ($manifest.selfContainedDotNet -isnot [bool]) { throw '构建清单缺少 .NET 运行环境选择。' }
        if ($manifest.selfContainedDotNet) { $requiredFiles += @('coreclr.dll','hostfxr.dll','PresentationFramework.dll') }
        elseif ((Test-Path -LiteralPath (Join-Path $Directory 'coreclr.dll')) -or (Test-Path -LiteralPath (Join-Path $Directory 'hostfxr.dll'))) { throw '不带 .NET 的包残留 .NET 运行时。' }
        foreach ($required in $requiredFiles) {
            if (!(Test-Path -LiteralPath (Join-Path $Directory $required))) { throw "主程序包缺少 $required" }
        }
        $runtime = (Get-Content -LiteralPath (Join-Path $Directory 'AudioPlayer.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
        $frameworks = if ($manifest.selfContainedDotNet) { @($runtime.includedFrameworks) } else { @($runtime.frameworks) }
        if ($frameworks.name -notcontains 'Microsoft.WindowsDesktop.App') { throw '.NET 发布方式与构建清单不符。' }
    }
    return $manifest
}
