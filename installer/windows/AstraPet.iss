; Astra Pet 的 Windows 安装程序（Inno Setup 6）。
;
; 编译：ISCC.exe /DAppVersion=0.1.2 /DNumericVersion=0.1.2.0 /DSourceDir=publish /DOutputDir=. /DOutputName=AstraPet-0.1.2-win32-x64 installer\windows\AstraPet.iss
;   AppVersion      显示的版本，可以带 -preview.N
;   NumericVersion  文件属性里的版本，必须是 x.y.z.w 四段数字
;   SourceDir       AOT 发布目录（里面有 AstraPet.exe、ffmpeg.exe、Assets、licenses）
;
; 装到当前用户的目录（%LOCALAPPDATA%\Programs\Astra Pet），不需要管理员权限，所以更新时也能直接覆盖。
; 卸载只删程序，不删用户数据（设置、记忆在 %APPDATA%\AstraPet）。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef OutputName
  #define OutputName "AstraPet-setup"
#endif

[Setup]
; 这个 GUID 标识「这个应用」，升级时靠它认出旧版本，不能改。
AppId={{8E0F5C7A-3B1D-4C9E-A2F4-6D7B1C5E9A30}
AppName=Astra Pet
AppVersion={#AppVersion}
AppPublisher=RoutinAI
AppPublisherURL=https://routin.ai
AppSupportURL=https://astra-gate.si/pet
AppUpdatesURL=https://astra-gate.si/pet
AppCopyright=Copyright (c) 2026 RoutinAI
VersionInfoVersion={#NumericVersion}
VersionInfoCompany=RoutinAI
VersionInfoDescription=Astra Pet Setup
DefaultDirName={autopf}\Astra Pet
DefaultGroupName=Astra Pet
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\..\src\Assets\icons\app.ico
UninstallDisplayIcon={app}\AstraPet.ico
UninstallDisplayName=Astra Pet
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
; 许可页显示 MIT 许可和「宠物素材版权归 RoutinAI」的说明
LicenseFile={#SourceDir}\licenses\LICENSE

#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\..\src\Assets\icons\app.ico"; DestDir: "{app}"; DestName: "AstraPet.ico"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Astra Pet"; Filename: "{app}\AstraPet.exe"; IconFilename: "{app}\AstraPet.ico"
Name: "{autodesktop}\Astra Pet"; Filename: "{app}\AstraPet.exe"; IconFilename: "{app}\AstraPet.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\AstraPet.exe"; Description: "{cm:LaunchProgram,Astra Pet}"; Flags: nowait postinstall skipifsilent

[Code]
// 更新或卸载时宠物可能正在运行，文件被占用。先把它关掉（它没有任务栏窗口，Windows 的「重启管理器」关不掉）。
// 只杀 AstraPet.exe；它起的 ffmpeg 子进程在父进程退出后会因为管道断开自己退出。
procedure StopPet();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM AstraPet.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopPet();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopPet();
  Result := True;
end;
