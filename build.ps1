# Kick Çeviri sürüm derlemesi: uygulamayı yayınlar, ffmpeg'i hazırlar, kurulum dosyasını üretir.
#
# Gerekenler (bir kez):
#   winget install Microsoft.DotNet.SDK.8
#   winget install JRSoftware.InnoSetup
#
# Kullanım:  powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 1.0.1]
# Çıktı:     dist\KickCeviri-Setup-<sürüm>.exe

param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 0) Ön kontrol: çeviri modelleri indirilebilir mi?
$settings = Get-Content "src\KickTranslator.Service\appsettings.json" -Raw | ConvertFrom-Json
if (-not $settings.Translator.TranslationModelsBaseUrl -or $settings.Translator.TranslationModelsBaseUrl -match "KULLANICI_ADINIZ") {
    $msg = "appsettings.json → TranslationModelsBaseUrl ayarlanmamış. Kullanıcıların bilgisayarında çeviri modeli indirilemez (docs\DAGITIM.md, Adım 3)."
    if ($env:CI -eq "true") { throw $msg }   # GitHub Actions'ta soru sorulamaz
    Write-Warning $msg
    $answer = Read-Host "Yine de devam edilsin mi? (e/h)"
    if ($answer -ne "e") { exit 1 }
}

# 1) Uygulama (.NET çalışma zamanı dahil, kullanıcı ayrıca kurmaz)
Write-Host "1/3 Uygulama derleniyor..." -ForegroundColor Cyan
if (Test-Path dist\app) { Remove-Item dist\app -Recurse -Force }
dotnet publish src\KickTranslator.Service\KickTranslator.Service.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o dist\app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish başarısız." }
# Geliştirme ayarları kuruluma girmesin
Remove-Item dist\app\appsettings.Development.json -ErrorAction SilentlyContinue

# 2) ffmpeg (LGPL derlemesi: ticari dağıtıma uygun)
if (-not (Test-Path dist\ffmpeg\ffmpeg.exe)) {
    Write-Host "2/3 ffmpeg (LGPL) indiriliyor..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force dist\ffmpeg | Out-Null
    $zip = "dist\ffmpeg\ffmpeg.zip"
    Invoke-WebRequest "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip" -OutFile $zip
    Expand-Archive $zip -DestinationPath dist\ffmpeg\x -Force
    $bin = Get-ChildItem dist\ffmpeg\x -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    Copy-Item $bin.FullName dist\ffmpeg\ffmpeg.exe
    $lic = Get-ChildItem dist\ffmpeg\x -Recurse -Filter LICENSE* | Select-Object -First 1
    if ($lic) { Copy-Item $lic.FullName dist\ffmpeg\LICENSE-ffmpeg.txt }
    Remove-Item dist\ffmpeg\x -Recurse -Force
    Remove-Item $zip
} else {
    Write-Host "2/3 ffmpeg hazır." -ForegroundColor Cyan
}

# 3) Kurulum dosyası
Write-Host "3/3 Kurulum dosyası oluşturuluyor..." -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup bulunamadı. 'winget install JRSoftware.InnoSetup' ile kurun." }

& $iscc "/DAppVersion=$Version" installer\KickCeviri.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup derlemesi başarısız." }

Write-Host "`nTamam: dist\KickCeviri-Setup-$Version.exe" -ForegroundColor Green
