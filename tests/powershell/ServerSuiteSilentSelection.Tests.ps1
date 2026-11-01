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
# Execute only the production pure selection functions in an Inno test harness.
# No payload, registry access, service operations or installation entry points.
$start = $source.IndexOf('function NormalizePathNoTrailingBackslash(')
$end = $source.IndexOf('// NovaTouchUpdater protocol v1.')
if ($start -lt 0 -or $end -le $start) { throw 'Selection function boundaries missing.' }
$selection = $source.Substring($start, $end - $start)
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
$harness = $harness.Replace('__SELECTION_CODE__', $selection).Replace('__REPORT_PATH__', $report.Replace("'", "''"))
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
if ($results.Contains('FAIL') -or -not $results.Contains('PASS all 21 selection cases')) { throw 'Inno silent instance selection regression.' }
Write-Host 'Selection tested using production Pascal functions; no installation or service/registry changes.'
