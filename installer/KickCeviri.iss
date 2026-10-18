; Kick Çeviri kurulum betiği (Inno Setup 6)
; Derleme: build.ps1 (veya ISCC.exe installer\KickCeviri.iss)
; Yönetici izni istemez; kullanıcının kendi klasörüne kurulur.

#define AppName "Kick Çeviri"
#define AppExe "KickCeviri.exe"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6E3C1B7A-2F4D-4B8E-9A61-5D2C7F0B4E19}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\Programs\KickCeviri
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=KickCeviri-Setup-{#AppVersion}
SetupIconFile=..\src\KickTranslator.Service\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
; Kod imzalama sertifikanız olduğunda (SmartScreen uyarısını kaldırır):
; SignTool=signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f "sertifika.pfx" /p "SIFRE" $f

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Windows açılışında başlat (önerilir)"; GroupDescription: "Ek seçenekler:"
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; GroupDescription: "Ek seçenekler:"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\dist\ffmpeg\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\ffmpeg\LICENSE-ffmpeg.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\LISANSLAR.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{#AppName} - Kurulum ve eklenti"; Filename: "{app}\{#AppExe}"; Parameters: "--setup"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "KickCeviri"; \
  ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Kurulum bitince uygulamayı başlat ve "ilk adımlar" sayfasını aç
Filename: "{app}\{#AppExe}"; Parameters: "--setup"; Description: "Kick Çeviri'yi başlat"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
; İndirilen modeller büyük olduğu için kaldırırken silinir (hesap bilgisi de)
Type: filesandordirs; Name: "{localappdata}\KickTranslator"

[Code]
// Güncelleme kurulurken çalışan uygulamayı kapat
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
