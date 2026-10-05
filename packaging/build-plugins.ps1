param([Parameter(Mandatory)][string]$OutputDirectory, [string[]]$Plugins = @('all'))
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'build-common.ps1')
$catalog = @(Get-PluginBuildCatalog $repo)
$selected = @(Resolve-BuildSelection $Plugins $catalog.Id '插件')
foreach ($plugin in $catalog | Where-Object { $_.Id -in $selected }) {
    $destination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('plugins/' + $plugin.Id)
    Initialize-BuildDirectory $destination
    if ($plugin.Id -eq 'bilibili') {
        $runtime = Join-Path $repo 'runtime/bilibili'
        foreach ($required in @('ffmpeg/ffmpeg.exe','ffmpeg/ffprobe.exe','ffmpeg/LICENSE.txt','vendor/yt_dlp/__init__.py','vendor/qrcode/__init__.py','dependency-manifest.json')) {
            if (!(Test-Path -LiteralPath (Join-Path $runtime $required))) { throw "插件依赖缺失：$required，请使用根目录 build.cmd 自动准备。" }
        }
    }
    dotnet publish $plugin.Project -c Release -r win-x64 --self-contained false -o $destination --nologo
    if ($LASTEXITCODE -ne 0) { throw "插件编译失败：$($plugin.Id)" }
    if ($plugin.Id -eq 'bilibili') {
        foreach ($name in @('ffmpeg','vendor')) { Copy-BuildTree (Join-Path $runtime $name) (Join-Path $destination $name) }
        Copy-Item -LiteralPath (Join-Path $runtime 'dependency-manifest.json') -Destination $destination -Force
    }
    Copy-Item -LiteralPath (Join-Path $plugin.Directory 'README.md') -Destination $destination -Force
    Assert-PluginBuild $destination $plugin
    Write-Host "插件已生成（默认关闭）：$($plugin.Id) -> $destination"
}
