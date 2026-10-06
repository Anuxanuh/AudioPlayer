param([string]$BootstrapPython = '', [string]$Destination = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$BootstrapPython) { $BootstrapPython = Join-Path $repo '.venv/Scripts/python.exe' }
if (!$Destination) { $Destination = Join-Path $repo 'runtime/python' }
$Destination = [IO.Path]::GetFullPath($Destination)
$cache = Join-Path $repo 'artifacts/downloads'
New-Item -ItemType Directory -Force -Path $cache,$Destination | Out-Null
$archive = Join-Path $cache 'python-3.13.16-embed-amd64.zip'
$url = 'https://www.python.org/ftp/python/3.13.16/python-3.13.16-embed-amd64.zip'
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '97DAE5274CC54867065E8D5A3226E48C35017ED332A0FDB0E27D5B5821961297') { throw "Python 下载包校验失败：$archive" }
Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
@'
python313.zip
.
Lib/site-packages
../recognition
import site
'@ | Set-Content -LiteralPath (Join-Path $Destination 'python313._pth') -Encoding ascii
$packages = Join-Path $Destination 'Lib/site-packages'
& $BootstrapPython -m pip install --only-binary=:all: --platform win_amd64 --python-version 3.13 --implementation cp --abi cp313 --target $packages -r (Join-Path $repo 'recognition/requirements-lock.txt') --upgrade --no-compile
if ($LASTEXITCODE -ne 0) { throw '安装便携 Python 依赖失败。' }
& (Join-Path $Destination 'python.exe') -I -X utf8 -c 'import sys, faster_whisper, av, ctranslate2, sentencepiece, py3langid; assert py3langid.classify("This is an English sentence.")[0] == "en"; from opencc import OpenCC; assert OpenCC("t2s").convert("繁體中文") == "繁体中文"; print(sys.version); print("Portable Python, translation and OpenCC imports OK")'
if ($LASTEXITCODE -ne 0) { throw '便携 Python 自检失败。' }
@{ url = $url; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash; architecture = 'win-x64'; version = '3.13.16' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'runtime-manifest.json') -Encoding utf8
