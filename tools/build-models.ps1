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

# Geliştirme makinesinde de hemen kullanılsın diye yerel model klasörüne aç
$models = Join-Path $env:LOCALAPPDATA "KickTranslator\models"
foreach ($d in "en-tr", "tr-en") {
    $zip = "dist\models\opus-mt-$d.zip"
    $dest = Join-Path $models "opus-mt-$d"
    if (Test-Path $zip) {
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Expand-Archive $zip -DestinationPath $dest
    }
}

Write-Host "`nTamam. dist\models klasöründeki 3 dosyayı internete yükleyin (docs\DAGITIM.md, Adım 2)." -ForegroundColor Green
