; MediaVerifier installer (subplan 14, task U-06; started from FastTaskMgr's script).
; Per-user by default, framework-dependent: the .NET 10 Desktop Runtime (x64) must be installed first.
; The AppId must never change after the first public release.

#define AppName "MediaVerifier"
#ifndef AppVersion
#define AppVersion "0.0.0.0"
#endif
#ifndef SourceDir
#define SourceDir "..\artifacts\MediaVerifier"
#endif
#ifndef OutputDir
#define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{AE2FDF62-8004-4807-BFF0-83A2C996BB18}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=rockenrooster
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=MediaVerifier-Setup
SetupIconFile=..\src\MediaVerifier\Assets\MediaVerifier.ico
UninstallDisplayIcon={app}\MediaVerifier.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter=MediaVerifier.exe
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany=rockenrooster
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Messages]
FinishedLabel=Setup has finished installing [name] on your computer. Your library database and settings are kept in your user folder; uninstalling leaves them in place.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: checkedonce

[InstallDelete]
; A new FFmpeg bundle may carry DLLs with other version suffixes: never keep old ones.
Type: filesandordirs; Name: "{app}\ffmpeg\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\MediaVerifier.exe"; AppUserModelID: "rockenrooster.MediaVerifier"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\MediaVerifier.exe"; Tasks: desktopicon; AppUserModelID: "rockenrooster.MediaVerifier"

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\MediaVerifier.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\MediaVerifier.exe"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\MediaVerifier.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\MediaVerifier.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\MediaVerifier.exe"; Flags: nowait skipifnotsilent

[UninstallRun]
; Removes scheduled tasks and per-user registrations (subplan 13); the data folder stays.
Filename: "{app}\MediaVerifier.exe"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "MediaVerifierCleanup"

[Code]
function HasDesktopRuntime10(): Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  Root := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\');
  if FindFirst(Root + '10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  // A silent update skips the check: the runtime was there when the app last ran.
  if WizardSilent() then
    Exit;
  if not HasDesktopRuntime10() then
  begin
    MsgBox('MediaVerifier needs the .NET 10 Desktop Runtime (x64). Install it from https://dotnet.microsoft.com/download/dotnet/10.0, then run this setup again.', mbInformation, MB_OK);
    Result := False;
  end;
end;
