; Standalone offline installer. No MSI package or Windows Installer service is used.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#define ProductId "CA630B1F-9153-5830-A5EC-84A95D16534D"

[Setup]
AppId={{{#ProductId}}
AppName=PDF Meta Studio
AppVersion={#AppVersion}
AppVerName=PDF Meta Studio {#AppVersion}
AppPublisher=PDF Meta Studio
AppPublisherURL=https://github.com/qenuternis2/pdf-meta-studio
DefaultDirName={localappdata}\Programs\PdfMetaStudio
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=PdfMetaStudio-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\PdfMetaStudio.App\Assets\app.ico
UninstallDisplayIcon={app}\PdfMetaStudio.exe
WizardStyle=modern dynamic
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=no
DirExistsWarning=yes
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; A flat Start menu link does not collide with the legacy MSI's subfolder/link.
Name: "{autoprograms}\PDF Meta Studio"; Filename: "{app}\PdfMetaStudio.exe"; IconFilename: "{app}\PdfMetaStudio.exe"
Name: "{autodesktop}\PDF Meta Studio"; Filename: "{app}\PdfMetaStudio.exe"; IconFilename: "{app}\PdfMetaStudio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PdfMetaStudio.exe"; Description: "{cm:LaunchProgram,PDF Meta Studio}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
var
  PreviousVersion: String;
  PreviousPacked, CurrentPacked: Int64;
begin
  Result := True;
  if RegQueryStringValue(HKCU64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#ProductId}}_is1',
    'DisplayVersion', PreviousVersion) then
  begin
    Result := StrToVersion(PreviousVersion, PreviousPacked) and
      StrToVersion('{#AppVersion}', CurrentPacked);
    if Result then
      Result := ComparePackedVersion(PreviousPacked, CurrentPacked) <= 0;
    if not Result then
    begin
      Log('Refusing to replace a newer or unrecognized installed version: ' + PreviousVersion);
      if not WizardSilent then
        MsgBox('Установлена более новая или нераспознанная версия PDF Meta Studio. Установка отменена.', mbError, MB_OK);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Target, Legacy: String;
begin
  Result := '';
  Target := AddBackslash(ExpandFileName(ExpandConstant('{app}')));
  Legacy := AddBackslash(ExpandFileName(ExpandConstant('{localappdata}\PdfMetaStudio')));
  if CompareText(Copy(Target, 1, Length(Legacy)), Legacy) = 0 then
    Result := 'Выберите другую папку. Этот каталог зарезервирован для прежней MSI-версии. Рекомендуется папка Programs\PdfMetaStudio.';
end;
