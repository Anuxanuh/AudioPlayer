# Explicit migration requested by the user: only the nine known English-only model folders.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ids = @('tiny.en','base.en','small.en','medium.en','distil-small.en','distil-medium.en','distil-large-v2','distil-large-v3','distil-large-v3.5')
$roots = @((Join-Path $repo 'models'), (Join-Path $repo 'artifacts/ShengYu-Portable-win-x64/models'))
$targets = @()
foreach ($root in $roots) {
    $root = [IO.Path]::GetFullPath($root)
    if (!(Test-Path -LiteralPath $root)) { continue }
    if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "不清理目录链接：$root" }
    foreach ($id in $ids) {
        $target = [IO.Path]::GetFullPath((Join-Path $root "faster-whisper-$id"))
        if (!$target.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetDirectoryName($target) -ne $root) { throw "清理路径越界：$target" }
        if (!(Test-Path -LiteralPath $target)) { continue }
        $resolved = (Resolve-Path -LiteralPath $target).ProviderPath
        if ($resolved -ne $target -or ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "目标不是预期实际目录：$target" }
        if (Get-ChildItem -LiteralPath $target -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw "模型目录包含链接，停止：$target" }
        $targets += $target
    }
}
foreach ($target in $targets) {
    Write-Host "移除仅英语模型：$target"
    Remove-Item -LiteralPath $target -Recurse -Force
}
Write-Host "已移除 $($targets.Count) 个源/发布目录中的仅英语模型文件夹。"
