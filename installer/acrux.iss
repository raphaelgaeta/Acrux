; Windows installer for the desktop app (Inno Setup 6.7). Built by the release
; workflow (.github/workflows/release.yml), which passes:
;   /DAppVersion=0.1.0  /DNumericVersion=0.1.0  /DLabel=v0.1.0
;   /DPublishDir=<a self-contained, NOT single-file, publish of Acrux.WinForms>
; A folder install starts faster than the single-file exe and leaves nothing
; extracted under %TEMP%\.net.
;
; Polars' native library imports VCRUNTIME140.dll, part of the Microsoft Visual
; C++ Redistributable and missing from a clean Windows (the app then fails with
; "Dll was not found."). When it's missing, setup downloads the redistributable
; from Microsoft and runs it, which asks for admin rights; everything else
; installs per user, without admin.

#ifndef AppVersion
  #error Pass /DAppVersion=..., /DNumericVersion=..., /DLabel=... and /DPublishDir=...
#endif

#define VCRedistUrl "https://aka.ms/vs/17/release/vc_redist.x64.exe"

[Setup]
; never change: Windows knows the installed app by it
AppId={{52898CEE-7068-4C81-9DE4-5CA4BAF9DD0B}
AppName=Acrux
AppVersion={#AppVersion}
AppVerName=Acrux {#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=Raphael Henrique
AppPublisherURL=https://github.com/raphaelgaeta/Acrux
AppSupportURL=https://github.com/raphaelgaeta/Acrux/issues
AppUpdatesURL=https://github.com/raphaelgaeta/Acrux/releases
PrivilegesRequired=lowest
DefaultDirName={autopf}\Acrux
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 and later carry the Universal C runtime (api-ms-win-crt-*)
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\src\Acrux.WinForms\acrux.ico
UninstallDisplayIcon={app}\Acrux.exe
Compression=lzma2
SolidCompression=yes
OutputBaseFilename=Acrux-{#Label}-setup
; Explorer rereads file associations at the end of install and uninstall
ChangesAssociations=yes

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "..\assets\brand\NOTICE.md"; DestDir: "{app}"; DestName: "NOTICE-logo.md"

[Icons]
Name: "{autoprograms}\Acrux"; Filename: "{app}\Acrux.exe"
Name: "{autodesktop}\Acrux"; Filename: "{app}\Acrux.exe"; Tasks: desktopicon

[Registry]
; "Open with Acrux" for the formats the app reads (keep in step with
; DataFrameProvider.OpenDialogFilter). Per user, like the install: HKA is
; HKCU here. Windows lets no app make itself the default (only the user, in
; its own UI), so this only puts Acrux on the list. Parquet is the exception:
; nothing else usually claims it, so Acrux becomes its handler and a double
; click opens it. That default value stays on uninstall, as Microsoft advises:
; Windows ignores it once the ProgID below is gone.
Root: HKA; Subkey: "Software\Classes\Acrux.DataFile"; ValueType: string; ValueData: "Acrux data file"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Acrux.DataFile\DefaultIcon"; ValueType: string; ValueData: """{app}\Acrux.exe"",0"
Root: HKA; Subkey: "Software\Classes\Acrux.DataFile\shell\open\command"; ValueType: string; ValueData: """{app}\Acrux.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.parquet"; ValueType: string; ValueData: "Acrux.DataFile"
Root: HKA; Subkey: "Software\Classes\.parquet\OpenWithProgids"; ValueType: string; ValueName: "Acrux.DataFile"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.csv\OpenWithProgids"; ValueType: string; ValueName: "Acrux.DataFile"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.xlsx\OpenWithProgids"; ValueType: string; ValueName: "Acrux.DataFile"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.db\OpenWithProgids"; ValueType: string; ValueName: "Acrux.DataFile"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.sqlite\OpenWithProgids"; ValueType: string; ValueName: "Acrux.DataFile"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.sqlite3\OpenWithProgids"; ValueType: string; ValueName: "Acrux.DataFile"; ValueData: ""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Acrux.exe"; Description: "{cm:LaunchProgram,Acrux}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; the app's own temporary parquet files (DataFrameProvider)
Type: filesandordirs; Name: "{%TEMP}\Acrux"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  RedistDownloaded: Boolean;

function VCRuntimePresent: Boolean;
var
  Installed: Cardinal;
begin
  Result := FileExists(ExpandConstant('{sys}\vcruntime140.dll')) or
    (RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
      'Installed', Installed) and (Installed = 1));
end;

procedure WarnNoRuntime(const Reason: String);
begin
  SuppressibleMsgBox(Reason + #13#10#13#10 +
    'Acrux will be installed, but it can''t open data files until the Microsoft ' +
    'Visual C++ Redistributable (x64) is installed:' + #13#10 + '{#VCRedistUrl}',
    mbError, MB_OK, IDOK);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
    'Downloading the Microsoft Visual C++ runtime that Acrux needs...', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or VCRuntimePresent then
    Exit;
  DownloadPage.Clear;
  DownloadPage.Add('{#VCRedistUrl}', 'vc_redist.x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      RedistDownloaded := True;
    except
      if not DownloadPage.AbortedByUser then
        WarnNoRuntime('The Microsoft Visual C++ runtime couldn''t be downloaded: ' +
          GetExceptionMessage);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep <> ssPostInstall) or not RedistDownloaded then
    Exit;
  WizardForm.StatusLabel.Caption := 'Installing the Microsoft Visual C++ runtime...';
  // "runas": the redistributable installs for the whole machine, so Windows asks for admin
  if not ShellExec('runas', ExpandConstant('{tmp}\vc_redist.x64.exe'),
      '/install /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    WarnNoRuntime('The Microsoft Visual C++ runtime wasn''t installed (' +
      SysErrorMessage(ResultCode) + ').')
  // 3010: installed, restart pending; 1638: a newer version is already there
  else if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1638) then
    WarnNoRuntime(Format('The Microsoft Visual C++ runtime installer failed (exit code %d).', [ResultCode]))
  else if not VCRuntimePresent then
    WarnNoRuntime('The Microsoft Visual C++ runtime installer finished, but vcruntime140.dll isn''t there.');
end;
