; TastyEdit installer for 1.0.0.
;
; Compile it from the repository root, with the version passed in — the same number drives the
; artifact name, the publish folder it reads and the Start-Menu entry, so it is never guessed:
;
;   dotnet publish src/TastyEditor.App -c Release -r win-x64 --self-contained true -t:Rebuild `
;       -o publish/TastyEdit-1.0.0
;   & "C:\Program Files\Inno Setup 7\ISCC.exe" "/DMyAppVersion=1.0.0" installer\TastyEdit.iss
;
; It packages the publish folder and writes nothing of its own into it.

#ifndef MyAppVersion
  #error MyAppVersion must be passed as /DMyAppVersion=<version>; it names the publish folder below.
#endif

#define MyAppName "TastyEdit"
#define MyAppPublisher "Remag"
#define MyAppURL "https://github.com/RemagOfficial/TastyEdit"
#define PublishDir "..\publish\TastyEdit-" + MyAppVersion

[Setup]
AppId={{B7A52F04-6C31-4A18-9E2D-51C4B7F0D9A3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Installed per user, without elevation. The editor writes to the current user's registry for the
; .bin association and reads games out of the user's own Steam library, so machine-wide would buy
; nothing and cost an admin prompt. {localappdata}\Programs is where the other elevation-free
; per-user apps live; Windows does not police that path, so the apphost can rewrite itself.
; Leave the default uninstall directory name alone: it is derived from AppId and matches what the
; editor's own "find my install" logic expects.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no

OutputDir=Output
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
SetupIconFile=..\src\TastyEditor.App\app.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\TastyEdit.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Opt-in and unchecked: .bin is a shared extension that other tools use, and the editor's own rule
; is that the association is the user's preference, never something it takes without asking.
Name: "assocbin"; Description: "Open .bin and .xml level files with {#MyAppName}"; Flags: unchecked

[Files]
; Source="." with RecurseInSubdirs includes the folder's own top-level files, which is what the
; publish layout needs: the exe, its dlls and the icons folder all sit one level down.
; The .pdb is a build artefact — a player never gets a stack symbol out of it.
Source: "{#PublishDir}\*"; RecurseInSubdirs: "."; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The plain entry: the editor with its start screen. No IconFilename, so Windows reads the icon out
; of the apphost, which is the embedded app.ico.
Name: "{group}\{#MyAppName}"; Filename: "{app}\TastyEdit.exe"; WorkingDir: "{app}"

; One entry per game, each opening that game's own level folder. A shortcut's icon has to be a file
; Windows can see, so these point at the shipped tiles rather than at the exe — the only place a
; per-game icon can live, since a shared .bin extension carries exactly one association icon.
; The names match the games, not the internal keys; --game <key> is what the editor reads.
Name: "{group}\Tasty Planet 1";      Filename: "{app}\TastyEdit.exe"; Parameters: "--game tp1"; IconFilename: "{app}\icons\tp1.ico"; WorkingDir: "{app}"
Name: "{group}\Tasty Planet: Back for Seconds"; Filename: "{app}\TastyEdit.exe"; Parameters: "--game tp2"; IconFilename: "{app}\icons\tp2.ico"; WorkingDir: "{app}"
Name: "{group}\Tasty Blue";           Filename: "{app}\TastyEdit.exe"; Parameters: "--game tb";  IconFilename: "{app}\icons\tb.ico";  WorkingDir: "{app}"
Name: "{group}\Tasty Planet Forever"; Filename: "{app}\TastyEdit.exe"; Parameters: "--game f4";  IconFilename: "{app}\icons\f4.ico";  WorkingDir: "{app}"
Name: "{group}\Tasty Planet Final Bite"; Filename: "{app}\TastyEdit.exe"; Parameters: "--game f5"; IconFilename: "{app}\icons\f5.ico"; WorkingDir: "{app}"

[Run]
; The registry layout lives in the editor (Editing/FileAssociation.cs), not here: the File menu, this
; installer and the uninstaller all call the same code, so there is one definition of the ProgId and
; one set of keys to keep honest. runascurrentuser matters if this setup is ever elevated — the
; association must still land in HKCU, where the editor looks for it.
Filename: "{app}\TastyEdit.exe"; Parameters: "register-association ""{app}\TastyEdit.exe"""; \
    Check: TaskSelected("assocbin"); StatusMsg: "Associating level files..."; \
    Flags: runhidden runascurrentuser waituntilterminated

Filename: "{app}\TastyEdit.exe"; Description: "Launch {#MyAppName}"; \
    Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
; Runs before the files are deleted, so the executable is still there to do the removing. Only
; touches what Register wrote in HKCU; nothing machine-wide is ever claimed or cleaned.
Filename: "{app}\TastyEdit.exe"; Parameters: "unregister-association"; \
    Flags: runhidden waituntilterminated; RunOnceId: "unassoc"

[Code]
function TaskSelected(const ATaskName: string): Boolean;
begin
  Result := WizardIsTaskSelected(ATaskName);
end;
