; Установщик опубликованной папки. Каталог задаётся при сборке: ISCC /DPublishDir=...
#ifndef PublishDir
  #error Укажите PublishDir: каталог с Bitrix24.Desktop.exe
#endif

#define AppName "Битрикс24"
#define AppRelease "1.0.4"
#define AppSignature "Неофициальная сборка Битрикс24 Мессенджера"

[Setup]
AppId={{8F4E2A91-6C3D-4B17-A5E0-9D2F7B1C4E83}
AppName={#AppName}
AppVersion={#AppRelease}
AppVerName={#AppName} {#AppRelease}
DefaultDirName={autopf}\Bitrix24
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=Bitrix24-Setup-{#AppRelease}
SetupIconFile=..\src\Bitrix24.Desktop\Assets\app.ico
UninstallDisplayIcon={app}\Bitrix24.Desktop.exe
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Bitrix24.Desktop.SingleInstance
CloseApplications=yes
RestartApplications=no
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppRelease}
VersionInfoVersion={#AppRelease}.0
VersionInfoDescription={#AppSignature}
VersionInfoCompany={#AppSignature}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.xml,*.pdb,*.log"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Bitrix24.Desktop.exe"; IconFilename: "{app}\Assets\app.ico"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Bitrix24.Desktop.exe"; IconFilename: "{app}\Assets\app.ico"; WorkingDir: "{app}"

[Run]
Filename: "{app}\Bitrix24.Desktop.exe"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent
