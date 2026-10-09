; ─────────────────────────────────────────────────────────────────────────────
;  Instalador de MOVE · DICOM Migrator (Inno Setup 6.3+)
;
;  Se compila con build-installer.ps1, que publica la app y pasa:
;    /DAppVersion=1.7.1   /DPublishDir=<carpeta de dotnet publish>
;
;  Qué hace (ver DEPLOYMENT.md, secciones 1, 2, 5, 6 y 8):
;    · Copia el ejecutable autónomo (win-x64) a C:\DicomMigrator (por defecto).
;    · Primera instalación: pide la conexión PostgreSQL, puerto web y contraseña inicial
;      de admin, y escribe appsettings.Production.json (solo Administradores/SYSTEM).
;    · Protege la carpeta (en cada instalación y actualización): sin herencia de C:\;
;      Usuarios solo lectura y ejecución; logs solo Administradores/SYSTEM (SEC-1).
;    · Opcional: crea el rol y la base del Modelo A con un superusuario (--setup-db).
;    · Opcional: copia un fichero de licencia .dmlic.
;    · Registra el Servicio de Windows "DicomMigrator" con reinicio automático.
;    · Actualización: detiene el servicio, sustituye binarios y conserva la configuración.
; ─────────────────────────────────────────────────────────────────────────────

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #error Falta /DPublishDir=<carpeta de publicación>. Usa build-installer.ps1.
#endif

#define AppName     "MOVE · DICOM Migrator"
#define AppExe      "DicomMigrator.Web.exe"
#define ServiceName "DicomMigrator"
#define FirewallRule "DICOM Migrator"

[Setup]
AppId={{8E3B6A52-4D1F-4C7B-9A0E-6F2D5C1B7A93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=MOVE
VersionInfoVersion={#AppVersion}
DefaultDirName=C:\DicomMigrator
DisableDirPage=auto
DefaultGroupName=DICOM Migrator
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=out
OutputBaseFilename=DicomMigrator-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "service";      Description: "Registrar como Servicio de Windows (arranque automático)"
Name: "service\start"; Description: "Arrancar el servicio al terminar"
Name: "firewall";     Description: "Abrir el Firewall de Windows para la aplicación (Storage SCP y web)"

[Files]
; appsettings.Production.json nunca viaja en el paquete: lo genera el instalador.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "appsettings.Production.json,appsettings.Development.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{app}\logs"

[INI]
Filename: "{group}\Abrir DICOM Migrator.url"; Section: "InternetShortcut"; Key: "URL"; String: "{code:GetLocalUrl}"

[Icons]
Name: "{group}\Obtener fingerprint de licencia"; Filename: "{cmd}"; Parameters: "/k ""{app}\{#AppExe}"" --fingerprint"; WorkingDir: "{app}"
Name: "{group}\Carpeta de logs"; Filename: "{app}\logs"
Name: "{group}\Desinstalar DICOM Migrator"; Filename: "{uninstallexe}"

[Run]
Filename: "{code:GetLocalUrl}"; Description: "Abrir DICOM Migrator en el navegador"; Flags: postinstall shellexec nowait skipifsilent; Check: ServiceRunning

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "DeleteFirewall"

[UninstallDelete]
Type: files; Name: "{group}\Abrir DICOM Migrator.url"

[Code]
function SetEnvironmentVariable(lpName, lpValue: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

var
  DbPage: TInputQueryWizardPage;
  ProvisionPage: TInputQueryWizardPage;
  ProvisionCheck: TNewCheckBox;
  WebPage: TInputQueryWizardPage;
  NetworkCheck: TNewCheckBox;
  LicensePage: TInputFileWizardPage;
  ReconfigPage: TInputOptionWizardPage;
  KeptConfig: Boolean;   { KeepConfig fijado al empezar a instalar (luego el fichero ya existe). }
  DbSetupOk: Boolean;
  ServiceStarted: Boolean;

{ ── Utilidades ─────────────────────────────────────────────────────────────── }

// WizardDirValue y no la constante app: ShouldSkipPage se evalúa también en las páginas
// previas a la selección de carpeta, cuando esa constante aún no está inicializada.
function ConfigPath: String;
begin
  Result := AddBackslash(WizardDirValue) + 'appsettings.Production.json';
end;

function JsonEsc(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

{ Valor para cadena de conexión (DbConnectionStringBuilder): entre comillas dobles
  si contiene caracteres especiales, duplicando las comillas internas. }
function ConnVal(const S: String): String;
begin
  Result := S;
  if (Pos(';', S) > 0) or (Pos('=', S) > 0) or (Pos('"', S) > 0) or (Pos('''', S) > 0)
     or ((Length(S) > 0) and ((S[1] = ' ') or (S[Length(S)] = ' '))) then
  begin
    StringChangeEx(Result, '"', '""', True);
    Result := '"' + Result + '"';
  end;
end;

function IsDigits(const S: String): Boolean;
var I: Integer;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
    if (S[I] < '0') or (S[I] > '9') then Result := False;
end;

function ServiceExists: Boolean;
var Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code)
            and (Code = 0);
end;

function RunHidden(const Exe, Params: String): Integer;
begin
  if not Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

function GetPort: String;
begin
  if Assigned(WebPage) and (Trim(WebPage.Values[0]) <> '') then
    Result := Trim(WebPage.Values[0])
  else
    Result := GetPreviousData('WebPort', '5200');
end;

function GetLocalUrl(Param: String): String;
begin
  Result := 'http://localhost:' + GetPort;
end;

{ True si hay un appsettings.Production.json y se ha elegido conservarlo (actualización). }
function KeepConfig: Boolean;
begin
  Result := FileExists(ConfigPath) and
            (not Assigned(ReconfigPage) or (ReconfigPage.SelectedValueIndex = 0));
end;

function ServiceRunning: Boolean;
begin
  Result := ServiceStarted;
end;

{ ── Páginas del asistente ──────────────────────────────────────────────────── }

procedure InitializeWizard;
begin
  ReconfigPage := CreateInputOptionPage(wpSelectTasks,
    'Configuración existente',
    'Ya hay un appsettings.Production.json en la carpeta de instalación.',
    '¿Qué quieres hacer con él?', True, False);
  ReconfigPage.Add('Conservarlo (actualizar solo los binarios)');
  ReconfigPage.Add('Volver a configurar (se sobrescribe con los datos que indiques)');
  ReconfigPage.SelectedValueIndex := 0;

  DbPage := CreateInputQueryPage(ReconfigPage.ID,
    'Base de datos PostgreSQL',
    'Conexión que usará la aplicación (usuario de aplicación, dueño del esquema).',
    'La aplicación crea las tablas sola al arrancar. Se recomienda un usuario propio ' +
    '(Modelo A), no postgres.');
  DbPage.Add('Servidor:', False);
  DbPage.Add('Puerto:', False);
  DbPage.Add('Base de datos:', False);
  DbPage.Add('Usuario de aplicación:', False);
  DbPage.Add('Contraseña:', True);
  DbPage.Values[0] := GetPreviousData('DbHost', 'localhost');
  DbPage.Values[1] := GetPreviousData('DbPort', '5432');
  DbPage.Values[2] := GetPreviousData('DbName', 'dicommigrator');
  DbPage.Values[3] := GetPreviousData('DbUser', 'dicom_app_migrator');

  ProvisionPage := CreateInputQueryPage(DbPage.ID,
    'Crear rol y base de datos',
    '¿Existen ya el usuario y la base en PostgreSQL?',
    'Si no existen, el instalador puede crearlos con un superusuario (solo se usa ahora; ' +
    'no se guarda). Nunca modifica un rol o una base existentes.');
  ProvisionCheck := TNewCheckBox.Create(ProvisionPage);
  ProvisionCheck.Parent := ProvisionPage.Surface;
  ProvisionCheck.Caption := 'Crear el rol y la base si no existen';
  ProvisionCheck.Width := ProvisionPage.SurfaceWidth;
  ProvisionCheck.Checked := True;
  ProvisionPage.Add('Superusuario:', False);
  ProvisionPage.Add('Contraseña del superusuario:', True);
  ProvisionPage.Values[0] := 'postgres';
  { Desplaza los campos bajo la casilla. }
  ProvisionPage.PromptLabels[0].Top := ProvisionPage.PromptLabels[0].Top + ScaleY(24);
  ProvisionPage.Edits[0].Top := ProvisionPage.Edits[0].Top + ScaleY(24);
  ProvisionPage.PromptLabels[1].Top := ProvisionPage.PromptLabels[1].Top + ScaleY(24);
  ProvisionPage.Edits[1].Top := ProvisionPage.Edits[1].Top + ScaleY(24);
  ProvisionCheck.Top := ProvisionPage.PromptLabels[0].Top - ScaleY(28);

  WebPage := CreateInputQueryPage(ProvisionPage.ID,
    'Acceso web y administrador',
    'Puerto de la interfaz y contraseña inicial del usuario admin.',
    'En el primer acceso la aplicación obligará a cambiar la contraseña de admin.');
  WebPage.Add('Puerto HTTP:', False);
  WebPage.Add('Contraseña inicial de admin (mín. 8 caracteres):', True);
  WebPage.Add('Repetir contraseña:', True);
  WebPage.Values[0] := GetPreviousData('WebPort', '5200');
  NetworkCheck := TNewCheckBox.Create(WebPage);
  NetworkCheck.Parent := WebPage.Surface;
  NetworkCheck.Caption := 'Permitir acceso desde otros equipos (HTTP sin cifrar; ver HTTPS en DEPLOYMENT.md)';
  NetworkCheck.Width := WebPage.SurfaceWidth;
  NetworkCheck.Top := WebPage.Edits[2].Top + WebPage.Edits[2].Height + ScaleY(12);
  NetworkCheck.Checked := GetPreviousData('WebNetwork', '0') = '1';

  LicensePage := CreateInputFilePage(WebPage.ID,
    'Licencia',
    'Fichero de licencia (opcional).',
    'Sin licencia la aplicación arranca, pero no ejecuta migraciones. Puedes instalarla ' +
    'más tarde desde la pantalla Licencia. El fingerprint de esta máquina se obtiene con el ' +
    'acceso directo "Obtener fingerprint de licencia".');
  LicensePage.Add('Fichero .dmlic:', 'Licencia DICOM Migrator (*.dmlic)|*.dmlic|Todos los ficheros|*.*', '.dmlic');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = ReconfigPage.ID then
    Result := not FileExists(ConfigPath)
  else if (PageID = DbPage.ID) or (PageID = ProvisionPage.ID) or
          (PageID = WebPage.ID) or (PageID = LicensePage.ID) then
    Result := KeepConfig;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = DbPage.ID then
  begin
    if (Trim(DbPage.Values[0]) = '') or (Trim(DbPage.Values[2]) = '') or (Trim(DbPage.Values[3]) = '') then
    begin
      MsgBox('Servidor, base de datos y usuario son obligatorios.', mbError, MB_OK);
      Result := False;
    end
    else if not IsDigits(Trim(DbPage.Values[1])) then
    begin
      MsgBox('El puerto de PostgreSQL debe ser numérico.', mbError, MB_OK);
      Result := False;
    end
    else if DbPage.Values[4] = '' then
    begin
      MsgBox('Indica la contraseña del usuario de aplicación.', mbError, MB_OK);
      Result := False;
    end
    else if CompareText(Trim(DbPage.Values[3]), 'postgres') = 0 then
      Result := MsgBox('Usar postgres como usuario de aplicación no es recomendable ' +
        '(ver DEPLOYMENT.md, Modelo A). ¿Continuar igualmente?', mbConfirmation, MB_YESNO) = IDYES;
  end
  else if CurPageID = ProvisionPage.ID then
  begin
    if ProvisionCheck.Checked and (Trim(ProvisionPage.Values[0]) = '') then
    begin
      MsgBox('Indica el superusuario o desmarca la casilla.', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = WebPage.ID then
  begin
    if not IsDigits(Trim(WebPage.Values[0])) or (StrToIntDef(Trim(WebPage.Values[0]), 0) < 1)
       or (StrToIntDef(Trim(WebPage.Values[0]), 0) > 65535) then
    begin
      MsgBox('El puerto HTTP debe ser un número entre 1 y 65535.', mbError, MB_OK);
      Result := False;
    end
    else if Length(WebPage.Values[1]) < 8 then
    begin
      MsgBox('La contraseña inicial de admin debe tener al menos 8 caracteres.', mbError, MB_OK);
      Result := False;
    end
    else if WebPage.Values[1] <> WebPage.Values[2] then
    begin
      MsgBox('Las contraseñas de admin no coinciden.', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = LicensePage.ID then
  begin
    if (LicensePage.Values[0] <> '') and not FileExists(LicensePage.Values[0]) then
    begin
      MsgBox('El fichero de licencia no existe.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  { Solo datos no secretos: sirven de valores por defecto al reinstalar. }
  if not KeptConfig then
  begin
    SetPreviousData(PreviousDataKey, 'DbHost', Trim(DbPage.Values[0]));
    SetPreviousData(PreviousDataKey, 'DbPort', Trim(DbPage.Values[1]));
    SetPreviousData(PreviousDataKey, 'DbName', Trim(DbPage.Values[2]));
    SetPreviousData(PreviousDataKey, 'DbUser', Trim(DbPage.Values[3]));
    SetPreviousData(PreviousDataKey, 'WebPort', Trim(WebPage.Values[0]));
    if NetworkCheck.Checked then
      SetPreviousData(PreviousDataKey, 'WebNetwork', '1')
    else
      SetPreviousData(PreviousDataKey, 'WebNetwork', '0');
  end
  else
  begin
    SetPreviousData(PreviousDataKey, 'DbHost', GetPreviousData('DbHost', 'localhost'));
    SetPreviousData(PreviousDataKey, 'DbPort', GetPreviousData('DbPort', '5432'));
    SetPreviousData(PreviousDataKey, 'DbName', GetPreviousData('DbName', 'dicommigrator'));
    SetPreviousData(PreviousDataKey, 'DbUser', GetPreviousData('DbUser', 'dicom_app_migrator'));
    SetPreviousData(PreviousDataKey, 'WebPort', GetPreviousData('WebPort', '5200'));
    SetPreviousData(PreviousDataKey, 'WebNetwork', GetPreviousData('WebNetwork', '0'));
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  if KeepConfig then
    Result := Result + 'Configuración:' + NewLine + Space +
      'Se conserva el appsettings.Production.json existente (actualización).'
  else
  begin
    Result := Result + 'PostgreSQL:' + NewLine + Space + DbPage.Values[3] + '@' + DbPage.Values[0] + ':' +
      DbPage.Values[1] + '/' + DbPage.Values[2] + NewLine;
    if ProvisionCheck.Checked then
      Result := Result + Space + 'Crear rol y base si no existen (como ' + ProvisionPage.Values[0] + ')' + NewLine;
    Result := Result + NewLine + 'Interfaz web:' + NewLine + Space + GetLocalUrl('');
    if NetworkCheck.Checked then
      Result := Result + '  (accesible desde la red)';
    if LicensePage.Values[0] <> '' then
      Result := Result + NewLine + NewLine + 'Licencia:' + NewLine + Space + LicensePage.Values[0];
  end;
end;

{ ── Instalación ────────────────────────────────────────────────────────────── }

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  KeptConfig := KeepConfig;
  { Actualización: liberar los binarios en uso. net stop espera a que se detenga. }
  if ServiceExists then
    RunHidden(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
end;

procedure WriteConfig;
var
  Lines: TArrayOfString;
  ConnStr, BindHost, LicenseTarget: String;
  N: Integer;
begin
  ConnStr := 'Host=' + ConnVal(Trim(DbPage.Values[0])) +
             ';Port=' + Trim(DbPage.Values[1]) +
             ';Database=' + ConnVal(Trim(DbPage.Values[2])) +
             ';Username=' + ConnVal(Trim(DbPage.Values[3])) +
             ';Password=' + ConnVal(DbPage.Values[4]);
  if NetworkCheck.Checked then BindHost := '0.0.0.0' else BindHost := 'localhost';

  N := 0;
  SetArrayLength(Lines, 20);
  Lines[N] := '{'; N := N + 1;
  Lines[N] := '  "ConnectionStrings": {'; N := N + 1;
  Lines[N] := '    "Default": "' + JsonEsc(ConnStr) + '"'; N := N + 1;
  Lines[N] := '  },'; N := N + 1;
  Lines[N] := '  "Kestrel": {'; N := N + 1;
  Lines[N] := '    "Endpoints": {'; N := N + 1;
  Lines[N] := '      "Http": { "Url": "http://' + BindHost + ':' + Trim(WebPage.Values[0]) + '" }'; N := N + 1;
  Lines[N] := '    }'; N := N + 1;
  Lines[N] := '  },'; N := N + 1;
  Lines[N] := '  "Auth": {'; N := N + 1;
  Lines[N] := '    "InitialAdminPassword": "' + JsonEsc(WebPage.Values[1]) + '"'; N := N + 1;
  if LicensePage.Values[0] <> '' then
  begin
    LicenseTarget := ExpandConstant('{app}\license.dmlic');
    CopyFile(LicensePage.Values[0], LicenseTarget, False);
    Lines[N] := '  },'; N := N + 1;
    Lines[N] := '  "License": {'; N := N + 1;
    Lines[N] := '    "Path": "' + JsonEsc(LicenseTarget) + '"'; N := N + 1;
  end;
  Lines[N] := '  }'; N := N + 1;
  Lines[N] := '}'; N := N + 1;
  SetArrayLength(Lines, N);

  SaveStringsToUTF8File(ConfigPath, Lines, False);
  { Sus permisos (solo Administradores y SYSTEM) los fija SecureInstallDir. }
end;

{ ── Permisos de la carpeta de instalación (SEC-1) ─────────────────────────────
  Antes la carpeta heredaba los permisos de C:\, que en Windows de escritorio dan
  "Modificar" a Usuarios autentificados: cualquier usuario sin privilegios podía
  sustituir el .exe o una DLL que el servicio ejecuta como LocalSystem (escalada a
  SYSTEM), editar appsettings.json o leer los logs, que contienen datos de pacientes.
  Ahora:
    · carpeta y contenido: Administradores y SYSTEM control total; Usuarios, solo
      lectura y ejecución;
    · logs: solo Administradores y SYSTEM;
    · appsettings.Production.json (contraseñas): solo Administradores y SYSTEM.
  Se aplica en cada instalación y actualización, así que corrige también las
  instalaciones existentes. Se usan SID, no nombres, para que valga en cualquier
  idioma de Windows: S-1-5-32-544 Administradores, S-1-5-18 SYSTEM,
  S-1-5-32-545 Usuarios. }
function Icacls(const Params: String; var Failed: String): Boolean;
var Code: Integer;
begin
  Code := RunHidden(ExpandConstant('{sys}\icacls.exe'), Params);
  Result := Code = 0;
  Log('icacls ' + Params + ' -> ' + IntToStr(Code));
  if not Result then
    Failed := Failed + '  icacls ' + Params + '  (código ' + IntToStr(Code) + ')' + #13#10;
end;

procedure SecureInstallDir;
var
  App, Logs, Failed: String;
begin
  WizardForm.StatusLabel.Caption := 'Protegiendo la carpeta de instalación...';
  App := ExpandConstant('{app}');
  Logs := App + '\logs';
  Failed := '';

  { 1. La carpeta deja de heredar de C:\ y recibe permisos explícitos heredables.
       /grant:r solo sustituye los grupos que nombra: un permiso EXPLÍCITO previo para
       Usuarios autentificados (S-1-5-11), Todos (S-1-1-0), Interactivo (S-1-5-4) o
       CREATOR OWNER (S-1-3-0), p. ej. añadido a mano, se quedaría. Se quita antes. }
  Icacls('"' + App + '" /inheritance:r', Failed);
  Icacls('"' + App + '" /remove:g *S-1-5-11 *S-1-1-0 *S-1-5-4 *S-1-3-0', Failed);
  Icacls('"' + App + '" /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F ' +
         '*S-1-5-32-545:(OI)(CI)RX', Failed);
  { 2. Todo su contenido pasa a heredar de ella. Quita los permisos heredados de C:\
       que quedaran en ficheros de una instalación anterior. }
  Icacls('"' + App + '\*" /reset /T /C /Q', Failed);
  { 3. Configuración con contraseñas: el paso 2 la restablece; se vuelve a proteger. }
  if FileExists(ConfigPath) then
    Icacls('"' + ConfigPath + '" /inheritance:r /grant:r *S-1-5-32-544:F *S-1-5-18:F', Failed);
  { 4. Logs: pueden contener datos de pacientes. Sin acceso para Usuarios. }
  if DirExists(Logs) then
  begin
    Icacls('"' + Logs + '" /inheritance:r', Failed);
    Icacls('"' + Logs + '" /remove:g *S-1-5-11 *S-1-1-0 *S-1-5-4 *S-1-3-0 *S-1-5-32-545', Failed);
    Icacls('"' + Logs + '" /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F', Failed);
    Icacls('"' + Logs + '\*" /reset /T /C /Q', Failed);
  end;

  if Failed <> '' then
    SuppressibleMsgBox('No se pudieron ajustar todos los permisos de la carpeta de instalación:' + #13#10#13#10 +
      Failed + #13#10 + 'La aplicación funcionará, pero la carpeta puede quedar modificable por ' +
      'usuarios sin privilegios. Revisa los permisos con: icacls "' + App + '"',
      mbError, MB_OK, IDOK);
end;

procedure SetupDatabase;
var
  Output: TExecOutput;
  Code, I: Integer;
  Msg: String;
begin
  if (not KeptConfig) and ProvisionCheck.Checked then
    SetEnvironmentVariable('DICOMMIGRATOR_SETUP_ADMIN_CONNSTR',
      'Host=' + ConnVal(Trim(DbPage.Values[0])) + ';Port=' + Trim(DbPage.Values[1]) +
      ';Database=postgres;Username=' + ConnVal(Trim(ProvisionPage.Values[0])) +
      ';Password=' + ConnVal(ProvisionPage.Values[1]));
  try
    WizardForm.StatusLabel.Caption := 'Comprobando la base de datos PostgreSQL...';
    if not ExecAndCaptureOutput(ExpandConstant('{app}\{#AppExe}'), '--setup-db', ExpandConstant('{app}'),
                                SW_HIDE, ewWaitUntilTerminated, Code, Output) then
      Code := -1;
  finally
    SetEnvironmentVariable('DICOMMIGRATOR_SETUP_ADMIN_CONNSTR', '');
  end;

  DbSetupOk := Code = 0;
  Msg := '';
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
    { Todo salvo las líneas del logger de arranque ("[hh:mm:ss INF] ..."). }
    if Copy(Output.StdOut[I], 1, 1) <> '[' then
      Msg := Msg + Output.StdOut[I] + #13#10;
  Log('--setup-db (código ' + IntToStr(Code) + '):' + #13#10 + Msg);

  if not DbSetupOk then
    MsgBox('No se pudo preparar o conectar con la base de datos:' + #13#10#13#10 + Msg + #13#10 +
      'La instalación continúa, pero el servicio no se arrancará. Corrige ' +
      ConfigPath + ' (o la base de datos) y arráncalo con: sc start {#ServiceName}',
      mbError, MB_OK);
end;

{ Host de PostgreSQL configurado: el introducido ahora o, si se conserva la configuración,
  el de la instalación anterior. }
function DbHost: String;
begin
  if KeptConfig then
    Result := GetPreviousData('DbHost', 'localhost')
  else
    Result := Trim(DbPage.Values[0]);
end;

function IsLocalHost(const H: String): Boolean;
begin
  Result := (CompareText(H, 'localhost') = 0) or (H = '127.0.0.1') or (H = '::1') or (H = '.')
            or (CompareText(H, GetComputerNameString) = 0);
end;

{ Si PostgreSQL está en esta máquina, el servicio depende del suyo: Windows arranca antes
  PostgreSQL y no detiene PostgreSQL sin detener antes este servicio (OPS-2). Se busca el
  servicio postgresql* (p. ej. postgresql-x64-18). Si no hay, no se toca la dependencia. }
procedure SetPostgresDependency(const Sc: String);
var
  Output: TExecOutput;
  Code: Integer;
  Name: String;
begin
  if not IsLocalHost(DbHost) then Exit;
  if not ExecAndCaptureOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -Command "(Get-Service -Name ''postgresql*'' | Select-Object -First 1).Name"',
       '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then Exit;
  if (Code <> 0) or (GetArrayLength(Output.StdOut) = 0) then Exit;
  Name := Trim(Output.StdOut[0]);
  if Name = '' then Exit;
  Log('Dependencia del servicio de PostgreSQL: ' + Name);
  RunHidden(Sc, 'config {#ServiceName} depend= ' + Name);
end;

procedure InstallService;
var Sc: String;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  if not ServiceExists then
  begin
    WizardForm.StatusLabel.Caption := 'Registrando el servicio de Windows...';
    RunHidden(Sc, 'create {#ServiceName} binPath= "' + ExpandConstant('\"{app}\{#AppExe}\"') +
      '" start= auto DisplayName= "DICOM Migrator"');
    RunHidden(Sc, 'description {#ServiceName} "Migración de estudios DICOM entre sistemas PACS."');
  end;
  { Recuperación (OPS-2): reiniciar al minuto en TODOS los fallos. Antes solo en los dos
    primeros ("restart, restart, nada"): tras un corte de luz con PostgreSQL recuperándose
    más de dos minutos, el servicio se quedaba parado hasta que alguien lo notara. La
    tercera acción se repite para los fallos siguientes; el contador se reinicia al día. }
  RunHidden(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000');
  { Que la recuperación se aplique también si el proceso termina con código de error
    (p. ej. PostgreSQL no disponible al arrancar), no solo si se cuelga. }
  RunHidden(Sc, 'failureflag {#ServiceName} 1');
  { Arranque automático retrasado: tras reiniciar el equipo, da margen a que arranquen
    antes la red y PostgreSQL. }
  RunHidden(Sc, 'config {#ServiceName} start= delayed-auto');
  SetPostgresDependency(Sc);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then Exit;

  if not KeptConfig then
    WriteConfig;

  { Antes de ejecutar nada de la carpeta (--setup-db, servicio): permisos seguros. }
  SecureInstallDir;

  SetupDatabase;

  if WizardIsTaskSelected('firewall') then
  begin
    RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#FirewallRule}"');
    RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#FirewallRule}" ' +
      'dir=in action=allow program="' + ExpandConstant('{app}\{#AppExe}') + '" enable=yes profile=any');
  end;

  if WizardIsTaskSelected('service') then
  begin
    InstallService;
    if WizardIsTaskSelected('service\start') and DbSetupOk then
    begin
      WizardForm.StatusLabel.Caption := 'Arrancando el servicio...';
      ServiceStarted := RunHidden(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}') = 0;
      if not ServiceStarted then
        MsgBox('El servicio no arrancó. Revisa los logs en ' + ExpandConstant('{app}\logs') + '.',
          mbError, MB_OK);
    end;
  end
  else if ServiceExists then
  begin
    { Actualización sin la tarea de servicio: volver a dejarlo como estaba. }
    ServiceStarted := RunHidden(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}') = 0;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if DirExists(ExpandConstant('{app}')) then
      MsgBox('Se conservan en ' + ExpandConstant('{app}') + ' la configuración ' +
        '(appsettings.Production.json), la licencia y los logs. La base de datos PostgreSQL ' +
        'no se modifica. Bórralos a mano si ya no los necesitas.', mbInformation, MB_OK);
end;
