#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#define AppVersion "0.2.0"
[Setup]
AppId={{9601ED02-95A7-49C8-BE5F-6E1390981464}
AppName=eslee MeshDrive
AppVersion={#AppVersion}
AppPublisher=eslee
DefaultDirName={autopf}\eslee\MeshDrive
DefaultGroupName=eslee MeshDrive
OutputDir=..\artifacts\installer
OutputBaseFilename=MeshDriveSetup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=MeshDrive.Windows.exe,MeshDrive.Agent.exe
RestartApplications=no
UninstallDisplayIcon={app}\MeshDrive.Windows.exe
[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "startup"; Description: "Windows 로그인 시 MeshDrive Agent 시작"
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\MeshDrive"; Filename: "{app}\MeshDrive.Windows.exe"
Name: "{group}\MeshDrive 제거"; Filename: "{uninstallexe}"
[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""eslee MeshDrive Private"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""eslee MeshDrive Private"" dir=in action=allow program=""{app}\MeshDrive.Agent.exe"" profile=private remoteip=localsubnet"; Flags: runhidden waituntilterminated
Filename: "{app}\MeshDrive.Agent.exe"; Parameters: "--enable-startup"; Tasks: startup; Flags: runasoriginaluser waituntilterminated runhidden
Filename: "{app}\MeshDrive.Windows.exe"; Description: "MeshDrive 실행"; Flags: postinstall nowait skipifsilent runasoriginaluser
[UninstallRun]
Filename: "{app}\MeshDrive.Agent.exe"; Parameters: "--shutdown"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{app}\MeshDrive.Agent.exe"; Parameters: "--disable-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStartup"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""eslee MeshDrive Private"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewall"
[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if MsgBox('현재 사용자 MeshDrive 설정, 페어링 정보, 캐시 및 동기화 이전 버전 백업을 모두 삭제할까요? 백업 삭제는 복구할 수 없습니다. 원본 공유 폴더는 삭제하지 않습니다.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\eslee\MeshDrive'), True, True, True);
end;
