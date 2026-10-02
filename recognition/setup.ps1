param(
    [Parameter(Mandatory = $true)][string]$PythonExe,
    [ValidateSet('tiny', 'base', 'small', 'medium', 'large-v3')][string]$ModelSize = 'small',
    [switch]$DownloadModel
)
$ErrorActionPreference = 'Stop'
$repoDirectory = Split-Path -Parent $PSScriptRoot
$venvDirectory = Join-Path $repoDirectory '.venv'
& $PythonExe -m venv $venvDirectory
if ($LASTEXITCODE -ne 0) { throw '创建 Python 环境失败。请使用 Python 3.10 至 3.12。' }
$venvPython = Join-Path $venvDirectory 'Scripts/python.exe'
& $venvPython -m pip install -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw '依赖安装失败。' }
Write-Host "播放器的 Python 路径：$venvPython"
if ($DownloadModel) {
    $modelDirectory = Join-Path $repoDirectory "models/faster-whisper-$ModelSize"
    & $venvPython (Join-Path $PSScriptRoot 'download_model.py') --size $ModelSize --output $modelDirectory
    if ($LASTEXITCODE -ne 0) { throw '模型下载失败。' }
    Write-Host "播放器的模型目录：$modelDirectory"
}
