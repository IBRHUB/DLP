#define AppName "DLP"
#define AppVersion "1.0.0"
#define AppPublisher "IBRHUB"
#define AppExeName "DLP.exe"
#define NativeHostName "com.ibrhub.dlp"
#define NativeHostManifestName "com.ibrhub.dlp.json"
#define ExtensionId "aaljempbfmhnhkdghllojlkmibdnmoeh"

[Setup]
AppId={{B03F0E91-4027-499A-A4E8-65A8EC403D2A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableReadyMemo=yes
OutputDir=..\dist\installer
OutputBaseFilename=DLP_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
MinVersion=10.0
SetupLogging=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\browser-extension\*"; DestDir: "{app}\browser-extension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; No Start Menu or desktop shortcuts are created.

[Registry]
Root: HKCU; Subkey: "Software\IBRHUB\DLP"; ValueType: string; ValueName: "AppPath"; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\{#NativeHostManifestName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\{#NativeHostManifestName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\{#NativeHostManifestName}"; Flags: uninsdeletekey

[Run]
; No post-install application launch is needed.

[InstallDelete]
Type: files; Name: "{userprograms}\DLP.lnk"
Type: files; Name: "{userdesktop}\DLP.lnk"
Type: files; Name: "{localappdata}\DLP\logs\app.log"
Type: files; Name: "{localappdata}\DLP\logs\native-host.log"
Type: dirifempty; Name: "{localappdata}\DLP\logs"

[UninstallDelete]
Type: files; Name: "{userprograms}\DLP.lnk"
Type: files; Name: "{userdesktop}\DLP.lnk"
Type: files; Name: "{app}\logs\DLP.log"
Type: files; Name: "{app}\native-host\{#NativeHostManifestName}"
Type: dirifempty; Name: "{app}\logs"
Type: dirifempty; Name: "{app}\native-host"
Type: dirifempty; Name: "{app}\browser-extension"
Type: dirifempty; Name: "{app}\tools"
Type: dirifempty; Name: "{app}"

[Code]
const
  DotNetDesktopRuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

function JsonEscape(Value: string): string;
var
  I: Integer;
  Ch: string;
begin
  Result := '';

  for I := 1 to Length(Value) do
  begin
    Ch := Copy(Value, I, 1);

    if Ch = '\' then
      Result := Result + '\\'
    else if Ch = '"' then
      Result := Result + '\"'
    else
      Result := Result + Ch;
  end;
end;

function StartsWith(Value: string; Prefix: string): Boolean;
begin
  Result := Copy(Value, 1, Length(Prefix)) = Prefix;
end;

function HasDotNetDesktopRuntime8Directory(BasePath: string): Boolean;
var
  FindRec: TFindRec;
  RuntimePath: string;
begin
  Result := False;

  if FindFirst(AddBackslash(BasePath) + '8.*', FindRec) then
  begin
    try
      repeat
        RuntimePath := AddBackslash(BasePath) + FindRec.Name;

        if DirExists(RuntimePath) then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsDotNetDesktopRuntime8Installed(): Boolean;
var
  Subkeys: TArrayOfString;
  I: Integer;
begin
  Result := False;

  if HasDotNetDesktopRuntime8Directory(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App')) then
  begin
    Result := True;
    Exit;
  end;

  if HasDotNetDesktopRuntime8Directory(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App')) then
  begin
    Result := True;
    Exit;
  end;

  if RegGetSubkeyNames(HKLM64, DotNetDesktopRuntimeKey, Subkeys) then
  begin
    for I := 0 to GetArrayLength(Subkeys) - 1 do
    begin
      if StartsWith(Subkeys[I], '8.') then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;

  if not IsWin64 then
  begin
    MsgBox('DLP supports Windows 10/11 x64 only.', mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  if not IsDotNetDesktopRuntime8Installed() then
  begin
    MsgBox('DLP requires Microsoft .NET 8 Windows Desktop Runtime x64. Install the runtime, then run DLP_Setup again.', mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
end;

procedure WriteNativeHostManifest();
var
  ManifestDirectory: string;
  ManifestPath: string;
  HostPath: string;
  Manifest: string;
begin
  ManifestDirectory := ExpandConstant('{app}\native-host');
  ManifestPath := ManifestDirectory + '\{#NativeHostManifestName}';
  HostPath := ExpandConstant('{app}\{#AppExeName}');

  if not DirExists(ManifestDirectory) then
    ForceDirectories(ManifestDirectory);

  Manifest :=
    '{' + #13#10 +
    '  "name": "{#NativeHostName}",' + #13#10 +
    '  "description": "DLP Native Messaging Host",' + #13#10 +
    '  "path": "' + JsonEscape(HostPath) + '",' + #13#10 +
    '  "type": "stdio",' + #13#10 +
    '  "allowed_origins": [' + #13#10 +
    '    "chrome-extension://{#ExtensionId}/"' + #13#10 +
    '  ]' + #13#10 +
    '}' + #13#10;

  if not SaveStringToFile(ManifestPath, Manifest, False) then
    RaiseException('Failed to write Native Messaging host manifest: ' + ManifestPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteNativeHostManifest();
end;
