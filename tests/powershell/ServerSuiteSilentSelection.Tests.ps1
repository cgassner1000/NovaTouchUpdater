param([string]$SuiteRepository = 'C:\development\novacom-software-gmbh\nt3-server-suite')
. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$fixture = Join-Path $TestArtifactsRoot ('suite-selection-' + [guid]::NewGuid().ToString('N').Substring(0,8))
[void][IO.Directory]::CreateDirectory($fixture)
$env:TEMP = Join-Path $fixture 'temp'
$env:TMP = $env:TEMP
[void][IO.Directory]::CreateDirectory($env:TEMP)
$source = Get-Content -LiteralPath (Join-Path $SuiteRepository 'installer\nt3-server-suite-setup.iss') -Raw -Encoding UTF8
if ($source -match '(?im)^\s*#include\s') { throw 'Production installer must remain self-contained.' }
# Execute production selection and install-step dispatch in an Inno test harness.
# Installation actions are replaced with trace-only stubs; no payload or services.
$start = $source.IndexOf('function NormalizePathNoTrailingBackslash(')
$end = $source.IndexOf('// NovaTouchUpdater protocol v1.')
if ($start -lt 0 -or $end -le $start) { throw 'Selection function boundaries missing.' }
$selection = $source.Substring($start, $end - $start)
$mode = [regex]::Match($source, '(?ms)^function IsUpdaterInstall: Boolean;\r?\n.*?^end;').Value
$steps = [regex]::Match($source, '(?ms)^procedure CurStepChanged\(CurStep: TSetupStep\);\r?\n.*?^end;').Value
if (-not $mode -or -not $steps) { throw 'Production setup mode/install-step dispatch missing.' }
$legacySource = (& git -C $SuiteRepository show 'b9113012^:installer/nt3-server-suite-setup.iss') -join "`r`n"
if ($LASTEXITCODE -ne 0) { throw 'Historical installer source missing.' }
$legacyGetter = [regex]::Match($legacySource, '(?ms)^function GetInstanceName\(\): String;\r?\n.*?^end;').Value
if (-not $legacyGetter) { throw 'Historical directory-based instance selection missing.' }
$legacyGetter = $legacyGetter.Replace('GetInstanceName()', 'LegacyGetInstanceName()').Replace('RegGetSubkeyNames(', 'FixtureRegGetSubkeyNames(').Replace('RegQueryStringValue(', 'FixtureRegQueryStringValue(')
$report = Join-Path $fixture 'selection-results.txt'
$harness = @'
[Setup]
AppName=NovaTouch Selection Tests
AppVersion=1.0
DefaultDirName={tmp}\UnusedSelectionTest
Uninstallable=no
CreateAppDir=no
PrivilegesRequired=lowest
DisableWelcomePage=yes
OutputBaseFilename=selection-tests
[Code]
const
  REG_BASE_KEY = 'SOFTWARE\Novacom\novatouch3\server.suite';
  DEFAULT_INSTANCE_NAME = 'nt3_server_suite';
  DEFAULT_SERVICE_NAME = 'nt3_server_suite';
  SERVICE_NAME_PREFIX = 'nt3_server_suite_';
__SELECTION_CODE__
var
  Names, Dirs: TArrayOfString;
  Report: String;
  Count: Integer;
  IsNewInstance, IsUninstallSelected: Boolean;
  FlowTrace, FlowService, FlowRoot: String;
  LegacyCurrentDir: String;

function GetAppPath: String; begin Result := LegacyCurrentDir; end;

function FixtureRegGetSubkeyNames(RootKey: Integer; const Key: String; var Keys: TArrayOfString): Boolean;
begin
  Keys := Names;
  Result := True;
end;

function FixtureRegQueryStringValue(RootKey: Integer; const Key, ValueName: String; var Value: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Names) - 1 do
    if (Key = RegistryInstanceKey(Names[I])) and (ValueName = 'InstallDir') then
    begin Value := Dirs[I]; Result := True; Exit; end;
end;

__LEGACY_GETTER__

procedure CheckLegacyDirectory(const LabelText, CurrentDir, ExpectedName: String);
var
  Selected: String;
begin
  LegacyCurrentDir := CurrentDir;
  Selected := LegacyGetInstanceName();
  if Selected <> ExpectedName then RaiseException(LabelText + ': selected ' + Selected);
  Count := Count + 1;
  Report := Report + 'PASS ' + LabelText + #13#10;
end;

__MODE_CODE__

procedure RecordStep(const Step: String);
begin
  FlowTrace := FlowTrace + Step + ':' + FlowService + '@' + FlowRoot + ';';
end;

procedure StopService; begin RecordStep('stop/remove'); end;
procedure CleanupForUpdateInstall; begin RecordStep('cleanup'); end;
procedure RegisterInstance; begin RecordStep('register-instance'); end;
procedure ManageConfigFile; begin RecordStep('config'); end;
procedure CreateBatchFile; begin RecordStep('batch-files'); end;
procedure AddFirewallRule; begin RecordStep('firewall'); end;
procedure RunServiceScriptsPostInstall; begin RecordStep('install/start-service'); end;
procedure CleanupUpdaterInstall; begin RecordStep('WRONG-UPDATER-PATH'); end;

__STEP_CODE__

procedure CheckNativeUpdateFlow(const ExpectedName, ExpectedDir: String);
var
  Error, SelectedName, SelectedDir, ManualTrace: String;
  NewInstance: Boolean;
begin
  if IsUpdaterInstall then RaiseException('Native update must not enable /UPDATER.');
  IsNewInstance := False;
  IsUninstallSelected := False;
  FlowService := GetServiceNameForInstance(ExpectedName);
  FlowRoot := ExpectedDir;
  FlowTrace := '';
  CurStepChanged(ssInstall);
  CurStepChanged(ssPostInstall);
  ManualTrace := FlowTrace;
  if (Pos('stop/remove:', ManualTrace) <> 1) or
     (Pos('batch-files:', ManualTrace) = 0) or
     (Pos('install/start-service:', ManualTrace) = 0) or
     (Pos('WRONG-UPDATER-PATH', ManualTrace) <> 0) then
    RaiseException('Normal update did not execute service removal/registration.');
  Error := ResolveSilentInstance(GetServiceNameForInstance(ExpectedName), '',
    ExpectedDir, 'C:\Wrong Default', Names, Dirs, SelectedName, SelectedDir, NewInstance);
  if (Error <> '') or NewInstance or (SelectedName <> ExpectedName) or
     (SelectedDir <> ExpectedDir) then RaiseException('Silent target differs: ' + Error);
  IsNewInstance := NewInstance;
  IsUninstallSelected := False;
  FlowService := GetServiceNameForInstance(SelectedName);
  FlowRoot := SelectedDir;
  FlowTrace := '';
  CurStepChanged(ssInstall);
  CurStepChanged(ssPostInstall);
  if FlowTrace <> ManualTrace then RaiseException('Silent and manual update paths differ.');
  Count := Count + 1;
  Report := Report + 'PASS native silent and manual update use identical service/install steps: ' + ExpectedName + #13#10;
end;

procedure Check(const LabelText, Svc, Inst, Dir, CurrentDir, ExpectedName,
  ExpectedDir: String; ExpectedNew, ExpectedError: Boolean);
var
  Error, SelectedName, SelectedDir: String;
  NewInstance: Boolean;
begin
  Error := ResolveSilentInstance(Svc, Inst, Dir, CurrentDir, Names, Dirs,
    SelectedName, SelectedDir, NewInstance);
  if (ExpectedError and (Error = '')) or ((not ExpectedError) and (Error <> '')) then
    RaiseException(LabelText + ': unexpected error status: ' + Error);
  if (not ExpectedError) and ((SelectedName <> ExpectedName) or
      (SelectedDir <> ExpectedDir) or (NewInstance <> ExpectedNew)) then
    RaiseException(LabelText + ': unexpected target: ' + SelectedName + ' at ' + SelectedDir);
  Count := Count + 1;
  Report := Report + 'PASS ' + LabelText + #13#10;
end;

function InitializeSetup: Boolean;
begin
  Result := False; // Exit before wizard, file installation, registry or services.
  try
    SetArrayLength(Names, 4); SetArrayLength(Dirs, 4);
    Names[0] := 'ABC'; Dirs[0] := 'C:\Suite ABC';
    Names[1] := 'nt3_server_suite'; Dirs[1] := 'C:\DefaultSuite';
    Names[2] := 'XYZ'; Dirs[2] := 'D:\Suite XYZ';
    Names[3] := 'Shared'; Dirs[3] := 'C:\Suite ABC';
    CheckLegacyDirectory('markerless setup selects the unique target by DIR, including case/trailing slash', 'd:\suite xyz\', 'XYZ');
    CheckLegacyDirectory('markerless setup selects the standard instance by DIR', 'C:\DefaultSuite', 'nt3_server_suite');
    CheckLegacyDirectory('historical shared directory chooses the first instance, requiring ambiguity protection', 'C:\Suite ABC', 'ABC');
    CheckLegacyDirectory('historical unmatched directory falls back to default, requiring registration protection', 'C:\Missing', 'nt3_server_suite');
    CheckNativeUpdateFlow('ABC', 'C:\Suite ABC');
    CheckNativeUpdateFlow('XYZ', 'D:\Suite XYZ');
    CheckNativeUpdateFlow('nt3_server_suite', 'C:\DefaultSuite');
    Check('service selects named instance', 'nt3_server_suite_ABC', '', '', 'C:\DefaultSuite', 'ABC', 'C:\Suite ABC', False, False);
    Check('instance alias selects named instance', '', 'XYZ', '', 'C:\DefaultSuite', 'XYZ', 'D:\Suite XYZ', False, False);
    Check('default instance alias', '', 'nt3_server_suite', '', 'C:\Unmatched', 'nt3_server_suite', 'C:\DefaultSuite', False, False);
    Check('consistent selectors and trailing slash', 'nt3_server_suite_ABC', 'ABC', 'c:\suite abc\', 'C:\DefaultSuite', 'ABC', 'C:\Suite ABC', False, False);
    Check('service comparison ignores case', 'NT3_SERVER_SUITE_XYZ', '', '', 'C:\DefaultSuite', 'XYZ', 'D:\Suite XYZ', False, False);
    Check('unknown service never falls back', 'nt3_server_suite_MISSING', '', '', 'C:\DefaultSuite', '', '', False, True);
    Check('unknown instance never falls back', '', 'MISSING', '', 'C:\DefaultSuite', '', '', False, True);
    Check('conflicting selectors fail', 'nt3_server_suite_ABC', 'XYZ', '', 'C:\DefaultSuite', '', '', False, True);
    Check('conflicting directory fails', 'nt3_server_suite_ABC', '', 'D:\Suite XYZ', 'C:\DefaultSuite', '', '', False, True);
    Check('directory alone selects unique instance', '', '', 'd:\suite xyz\', 'C:\DefaultSuite', 'XYZ', 'D:\Suite XYZ', False, False);
    Check('shared directory needs instance name', '', '', 'C:\Suite ABC', 'C:\DefaultSuite', '', '', False, True);
    Check('unmatched explicit directory never updates default', '', '', 'C:\Missing', 'C:\DefaultSuite', '', '', False, True);
    Check('legacy matching current path', '', '', '', 'D:\Suite XYZ', 'XYZ', 'D:\Suite XYZ', False, False);
    Check('legacy fallback prefers standard instance', '', '', '', 'C:\Unmatched', 'nt3_server_suite', 'C:\DefaultSuite', False, False);
    Check('legacy shared path keeps first match', '', '', '', 'C:\Suite ABC', 'ABC', 'C:\Suite ABC', False, False);
    SetArrayLength(Names, 1); SetArrayLength(Dirs, 1);
    Check('legacy fallback without standard uses first', '', '', '', 'C:\Unmatched', 'ABC', 'C:\Suite ABC', False, False);
    Dirs[0] := '';
    Check('explicit target needs registered directory', 'nt3_server_suite_ABC', '', '', 'C:\Unmatched', '', '', False, True);
    Check('legacy missing directory keeps current path', '', '', '', 'C:\Unmatched', 'ABC', 'C:\Unmatched', False, False);
    SetArrayLength(Names, 0); SetArrayLength(Dirs, 0);
    Check('first silent installation remains possible', '', '', '', 'C:\FreshSuite', 'nt3_server_suite', 'C:\FreshSuite', True, False);
    Check('first silent installation honors directory', '', '', 'D:\FreshSuite', 'C:\DefaultSuite', 'nt3_server_suite', 'D:\FreshSuite', True, False);
    Check('explicit missing instance cannot become first installation', '', 'MISSING', '', 'C:\FreshSuite', '', '', False, True);
    Report := Report + 'PASS all ' + IntToStr(Count) + ' selection cases' + #13#10;
  except
    Report := Report + 'FAIL ' + GetExceptionMessage + #13#10;
  end;
  SaveStringToFile('__REPORT_PATH__', Report, False);
end;
'@
$harness = $harness.Replace('__SELECTION_CODE__', $selection).Replace('__MODE_CODE__', $mode).Replace('__STEP_CODE__', $steps).Replace('__LEGACY_GETTER__', $legacyGetter).Replace('__REPORT_PATH__', $report.Replace("'", "''"))
$scriptFile = Join-Path $fixture 'selection-tests.iss'
[IO.File]::WriteAllText($scriptFile, $harness, [Text.UTF8Encoding]::new($true))
$compiler = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
$ErrorActionPreference = 'Continue'
& $compiler '/Qp' ('/O' + $fixture) $scriptFile > (Join-Path $fixture 'compile.log') 2>&1
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -ne 0) { throw (Get-Content (Join-Path $fixture 'compile.log') -Raw) }
$arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG="' + (Join-Path $fixture 'harness.log') + '"'
$process = Start-Process -FilePath (Join-Path $fixture 'selection-tests.exe') -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if (-not (Test-Path -LiteralPath $report -PathType Leaf)) { throw "Selection test harness failed before report (exit $($process.ExitCode))." }
$results = Get-Content -LiteralPath $report -Raw
Write-Host $results
if ($results.Contains('FAIL') -or -not $results.Contains('PASS all 28 selection cases')) { throw 'Inno silent instance selection/update flow regression.' }
Write-Host 'Selection and update-step dispatch tested using production Pascal functions; installation actions are trace-only stubs.'
