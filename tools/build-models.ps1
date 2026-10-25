# Çeviri modellerini hazırlar (en→tr, tr→en). Sadece geliştirici makinesinde, sürüm öncesi bir kez çalışır.
# Kullanıcıların Python kurmasına gerek yoktur; bu betiğin ürettiği zip'leri internete yüklersiniz (docs\DAGITIM.md).
#
# Kullanım:  powershell -ExecutionPolicy Bypass -File tools\build-models.ps1

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

Write-Host "Python paketleri kuruluyor..." -ForegroundColor Cyan
python -m pip install --upgrade pip
python -m pip install torch --index-url https://download.pytorch.org/whl/cpu
if ($LASTEXITCODE -ne 0) { throw "PyTorch kurulumu başarısız." }
python -m pip install --upgrade "optimum[onnxruntime]" sentencepiece sacremoses
if ($LASTEXITCODE -ne 0) { throw "pip kurulumu başarısız." }

python tools\build_models.py
if ($LASTEXITCODE -ne 0) { throw "Model hazırlama başarısız." }

# (build_models.py modelleri bu bilgisayara da kurar)

Write-Host "`nTamam. dist\models klasöründeki 3 dosyayı internete yükleyin (docs\DAGITIM.md)." -ForegroundColor Green
