#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $repo)
$id = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stage = Join-Path $repo ('artifacts/novel-plugin-' + $id)
$target = Join-Path $stage 'plugins/novel'
$zip = Join-Path $OutputDirectory ('ShengYu-Novel-Plugin-' + $id + '.zip')
$log = Join-Path $repo ('artifacts/build-logs/novel-' + $id + '.log')
$recording = $false
$code = 1
try {
    if (!$IsWindows) { throw '需要 Windows x64 构建环境。' }
    New-Item -ItemType Directory -Force -Path $target,$OutputDirectory,(Split-Path -Parent $log) | Out-Null
    Start-Transcript -LiteralPath $log | Out-Null; $recording = $true
    Push-Location -LiteralPath $repo
    try {
        Write-Host '编译独立小说章节插件…'
        dotnet publish (Join-Path $PSScriptRoot 'AudioPlayer.Plugin.Novel.csproj') -c Release -r win-x64 --self-contained false -o $target --nologo
        if ($LASTEXITCODE -ne 0) { throw '插件编译失败。' }
        # A standalone plugin shares the host contract; never replace the root host/runtime.
        foreach ($required in @('AudioPlayer.Plugin.Novel.dll','TagLibSharp.dll','plugin.json','README.md','licenses/TagLibSharp-LGPL-2.1.txt','licenses/TagLibSharp-README.md')) {
            if (!(Test-Path -LiteralPath (Join-Path $target $required))) { throw "插件缺少 $required" }
        }
        $manifest = Get-Content -LiteralPath (Join-Path $target 'plugin.json') -Raw | ConvertFrom-Json
        if ($manifest.id -ne 'novel' -or $manifest.apiVersion -ne 2) { throw '插件清单不正确。' }
        Write-Host '执行章节解析、映射、缓存与性能测试…'
        dotnet build (Join-Path $repo 'AudioPlayer.Tests/AudioPlayer.Tests.csproj') -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw '测试构建失败。' }
        dotnet (Join-Path $repo 'AudioPlayer.Tests/bin/Release/net10.0-windows/AudioPlayer.Tests.dll') --novel
        if ($LASTEXITCODE -ne 0) { throw '小说插件回归失败，未生成交付 ZIP。' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
            if ($names -notcontains 'plugins/novel/AudioPlayer.Plugin.Novel.dll' -or ($names | Where-Object { $_ -notlike 'plugins/novel/*' -or $_ -match '/(data|cache|logs)/' })) { throw '插件 ZIP 内容不正确。' }
        } finally { $archive.Dispose() }
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        ($hash + '  ' + [IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
        [ordered]@{ zip = $zip; sha256 = $hash; buildLog = $log; version = $manifest.version; requiredHostApi = 2 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'latest-novel-plugin.json') -Encoding utf8
        Write-Host "插件编译、测试、打包成功：$zip" -ForegroundColor Green
        Write-Host '解压到支持插件 API 2 的新版声屿目录；设置中启用后重启。'
        $code = 0
    } finally { Pop-Location }
} catch { Write-Host "失败：$($_.Exception.Message)" -ForegroundColor Red; Write-Host $_.ScriptStackTrace }
finally { if ($recording) { Stop-Transcript | Out-Null } }
exit $code
