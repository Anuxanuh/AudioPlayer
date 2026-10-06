#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'build-common.ps1')
$root = Join-Path $repo ('artifacts/tests/build-options-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$passed = 0
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; $script:passed++; Write-Host "PASS $Message" }
function Must-Fail([scriptblock]$Action, [string]$Pattern) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true; Check ($_.Exception.Message -match $Pattern) "Reject: $Pattern" }
    if (!$caught) { throw "Expected rejection: $Pattern" }
}
function Invoke-Root([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = 'Hidden'
    $info.RedirectStandardOutput = $info.RedirectStandardError = $true
    $info.WorkingDirectory = $root
    foreach ($argument in @('-NoProfile','-File',(Join-Path $repo 'build.ps1')) + $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(15000)) { $process.Kill($true); throw 'Parameter validation timed out.' }
        return [pscustomobject]@{ Code = $process.ExitCode; Text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}

$catalog = @(Get-PluginBuildCatalog $repo)
Check (($catalog.Id -join ',') -eq 'bilibili,novel') 'Discover plugin IDs from manifests'
Assert-BuildItems @(Resolve-BuildSelection @('all') $catalog.Id '插件') $catalog.Id 'all'
Check (@(Resolve-BuildSelection @('none') $catalog.Id '插件').Count -eq 0) 'none selects no plugins'
Assert-BuildItems @(Resolve-BuildSelection @('NOVEL, bilibili','novel') $catalog.Id '插件') @('novel','bilibili') 'comma and array lists'
Check $true 'Comma / array syntax, case normalization and duplicate removal'
Must-Fail { Resolve-BuildSelection @('none,novel') $catalog.Id '插件' } '不能.*混用'
Must-Fail { Resolve-BuildSelection @('all','novel') $catalog.Id '插件' } '不能.*混用'
Must-Fail { Resolve-BuildSelection @('novel,') $catalog.Id '插件' } '不能为空'
Must-Fail { Resolve-BuildSelection @('missing') $catalog.Id '插件' } '未知插件'
$models = @((Get-Content -LiteralPath (Join-Path $repo 'recognition/model_catalog.json') -Raw | ConvertFrom-Json).id)
Check (@(Resolve-BuildSelection @('all') $models '模型').Count -eq 8) 'all models expands the multilingual catalog'
Must-Fail { Resolve-BuildSelection @('tiny.en') $models '模型' } '未知模型'

foreach ($case in @(
    @{ Args = @('-Plugin','novel','-Plugins','none'); Pattern = '不能与' },
    @{ Args = @('-Plugin','novel','-Models','tiny'); Pattern = '不能与' },
    @{ Args = @('-Plugin','novel','-NoDotNetRuntime'); Pattern = '不能与运行环境' },
    @{ Args = @('-Plugin','novel','-NoPythonRuntime'); Pattern = '不能与运行环境' },
    @{ Args = @('-Plugin','all'); Pattern = '未知独立插件' },
    @{ Args = @('-Plugins','bad'); Pattern = '未知插件' },
    @{ Args = @('-Models','bad'); Pattern = '未知模型' },
    @{ Args = @('-Mode','bad'); Pattern = 'Build,Package' }
)) {
    $result = Invoke-Root $case.Args
    Check ($result.Code -ne 0 -and $result.Text -match $case.Pattern) "Root argument rejection: $($case.Args -join ' ')"
}
$result = Invoke-Root @('-Help','--no-pause')
Check ($result.Code -eq 0 -and $result.Text -match 'large-v3-turbo' -and $result.Text -match 'novel') 'Help works from another working directory and keeps legacy --no-pause'
$result = Invoke-Root @('-Help','-NoDotNet','-NoPython')
Check ($result.Code -eq 0 -and $result.Text -match 'NoDotNetRuntime' -and $result.Text -match 'NoPythonRuntime') 'Runtime switches and their aliases bind and appear in help'

foreach ($dotnet in @($false,$true)) {
    foreach ($python in @($false,$true)) {
        $app = Join-Path $root "runtime-$dotnet-$python"
        $required = @('AudioPlayer.exe','AudioPlayer.dll','AudioPlayer.Plugin.Abstractions.dll','Serilog.dll','Serilog.Sinks.File.dll','recognition/transcribe.py','recognition/translate_lyrics.py','recognition/model_manager.py','recognition/requirements-lock.txt','README.md','portable.flag')
        if ($python) { $required += @('python/python.exe','python/Lib/site-packages/opencc/opencc.py') }
        if ($dotnet) { $required += @('coreclr.dll','hostfxr.dll','PresentationFramework.dll') }
        foreach ($file in $required) {
            $path = Join-Path $app $file; New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [IO.File]::WriteAllText($path, 'fixture')
        }
        $runtime = @{}; $property = if ($dotnet) { 'includedFrameworks' } else { 'frameworks' }
        $runtime[$property] = @(@{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.0' })
        @{ runtimeOptions = $runtime } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $app 'AudioPlayer.runtimeconfig.json')
        @{ kind = 'app'; plugins = @(); models = @(); selfContainedDotNet = $dotnet; bundledPython = $python } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $app 'package-manifest.json')
        $null = Assert-PackageLayout $app $repo
        Check $true "App runtime layout: .NET=$dotnet Python=$python"
        if (!$dotnet) {
            [IO.File]::WriteAllText((Join-Path $app 'coreclr.dll'), 'leftover')
            Must-Fail { Assert-PackageLayout $app $repo } '残留 .NET'
            Remove-Item -LiteralPath (Join-Path $app 'coreclr.dll')
        }
        if (!$python) {
            New-Item -ItemType Directory -Path (Join-Path $app 'python') | Out-Null
            Must-Fail { Assert-PackageLayout $app $repo } '残留 python'
        }
    }
}

$model = Join-Path $root 'model-fixture'; New-Item -ItemType Directory -Path $model | Out-Null
$files = @()
foreach ($name in @('model.bin','config.json','tokenizer.json','vocabulary.txt')) {
    $path = Join-Path $model $name; [IO.File]::WriteAllText($path, 'fixture-' + $name)
    $files += @{ name = $name; bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
@{ id = 'tiny'; files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $model 'download-manifest.json') -Encoding utf8
Check (@(Get-ModelBuildFiles $model 'tiny' -VerifyHash).Count -eq 5) 'Only manifest resources are selected for model copying'
[IO.File]::WriteAllText((Join-Path $model 'personal.txt'), 'not a model resource')
Check (@(Get-ModelBuildFiles $model 'tiny') -notcontains 'personal.txt') 'Unlisted model-directory files are excluded'
[IO.File]::WriteAllText((Join-Path $model 'model.bin'), 'truncated')
Must-Fail { Get-ModelBuildFiles $model 'tiny' } '大小不符'
[IO.File]::WriteAllText((Join-Path $model 'model.bin'), 'FIXTURE-model.bin')
Must-Fail { Get-ModelBuildFiles $model 'tiny' -VerifyHash } '校验失败'
Must-Fail { Get-ModelBuildFiles (Join-Path $root 'missing') 'tiny' } '模型未完整下载'
Must-Fail { Initialize-BuildDirectory $model } '必须为空'

# Use a real published plugin; this test never modifies it or the user's runtime data.
$plugin = $catalog | Where-Object Id -eq 'novel'
$published = Join-Path $plugin.Directory 'bin/Release/net10.0-windows/win-x64'
if (!(Test-Path -LiteralPath (Join-Path $published $plugin.Manifest.assembly))) { throw '请先运行根目录 build.cmd -Plugin novel -Mode Build。' }
$package = Join-Path $root 'package'; $destination = Join-Path $package 'plugins/novel'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($file in Get-PluginRequiredFiles $plugin) {
    $source = if ($file -eq 'README.md') { Join-Path $plugin.Directory $file } else { Join-Path $published $file }
    $target = Join-Path $destination $file; New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
@{ kind = 'plugin'; plugins = @('novel'); models = @() } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
$null = Assert-PackageLayout $package $repo
Check $true 'Standalone package has exactly one real plugin and no host'
New-Item -ItemType Directory -Path (Join-Path $destination 'vendor/example.data/data') -Force | Out-Null
$null = Assert-PackageLayout $package $repo
Check $true 'Required wheel data resources are allowed inside vendor'
New-Item -ItemType Directory -Path (Join-Path $package 'plugins/stale') | Out-Null
Must-Fail { Assert-PackageLayout $package $repo } '插件目录.*不一致'
Remove-Item -LiteralPath (Join-Path $package 'plugins/stale')
New-Item -ItemType Directory -Path (Join-Path $destination 'data') | Out-Null
[IO.File]::WriteAllText((Join-Path $destination 'data/session.bin'), 'private fixture')
Must-Fail { Assert-PackageLayout $package $repo } '运行数据或缓存'
Check (!(Test-Path -LiteralPath (Join-Path $repo 'Plugins/Novel/build.cmd')) -and !(Test-Path -LiteralPath (Join-Path $repo 'Plugins/Novel/build.ps1'))) 'No build entry points remain in plugin subprojects'
Write-Host "PASS: $passed build-option checks. Artifacts: $root"
