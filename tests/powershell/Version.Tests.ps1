. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
$projectRoot = $RepositoryRoot
$fixtureRoot = Join-Path $projectRoot ('artifacts\test-results\version-tests\' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$fixtureProject = Join-Path $fixtureRoot 'NovaTouchUpdater.csproj'
$fixtureVersion = Join-Path $fixtureRoot 'version.txt'
Copy-Item -LiteralPath (Join-Path $TestSourceRoot 'NovaTouchUpdater.csproj') -Destination $fixtureProject
Copy-Item -LiteralPath (Join-Path $projectRoot 'version.txt') -Destination $fixtureVersion

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    Write-Host "PASS $message"
}

function Invoke-VersionBuild([string[]]$BuildArguments) {
    $output = @(& dotnet msbuild $fixtureProject -nologo "-p:RepositoryRoot=$fixtureRoot\" '-p:GITHUB_ACTIONS=false' @BuildArguments 2>&1)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output -join "`n").Trim() }
}

$versionSource = [IO.File]::ReadAllText($fixtureVersion)
$baseVersion = [regex]::Replace($versionSource, '(?m)^\s*#.*$', '').Trim()
Assert ($baseVersion -match '^\d+\.\d+$' -and $versionSource.Contains('#')) 'the central version file includes instructions and one base version'
$result = Invoke-VersionBuild @('-p:UPDATER_BUILD_NUMBER=', '-getProperty:Version')
Assert ($result.ExitCode -eq 0 -and $result.Text -eq "$baseVersion.0") 'local builds read the documented version file and append zero'
$result = Invoke-VersionBuild @('-p:UPDATER_BUILD_NUMBER=1', '-p:GITHUB_RUN_NUMBER=123', '-getProperty:Version')
Assert ($result.ExitCode -eq 0 -and $result.Text -eq "$baseVersion.1") 'GitHub builds use the per-series build number independently of the global run number'

# Edit only the isolated version file, leaving all build and workflow files unchanged.
[IO.File]::WriteAllText($fixtureVersion, "# Anleitung: z. B. 1.1 oder 2.0 verwenden.`r`n`r`n  7.8  `r`n")
$result = Invoke-VersionBuild @('-p:UPDATER_BUILD_NUMBER=', '-p:GITHUB_RUN_NUMBER=999', '-getProperty:Version')
Assert ($result.ExitCode -eq 0 -and $result.Text -eq '7.8.0') 'changing only version.txt updates local builds, ignoring comments and whitespace'
$result = Invoke-VersionBuild @('-p:UPDATER_BUILD_NUMBER=123', '-getProperty:Version')
Assert ($result.ExitCode -eq 0 -and $result.Text -eq '7.8.123') 'changing only version.txt updates GitHub builds'
$result = Invoke-VersionBuild @('-p:UPDATER_BUILD_NUMBER=123', '-target:ValidateUpdaterVersion')
Assert ($result.ExitCode -eq 0) 'valid documented version settings pass build validation'

foreach ($invalidVersion in @('1.1.x', '1.1.2', '', "1.1`n1.2")) {
    [IO.File]::WriteAllText($fixtureVersion, "# Beschreibung`n$invalidVersion`n")
    $result = Invoke-VersionBuild @('-p:UPDATER_BUILD_NUMBER=123', '-target:ValidateUpdaterVersion')
    Assert ($result.ExitCode -ne 0 -and $result.Text.Contains('version.txt muss genau eine Basisversion')) 'invalid or ambiguous version settings stop the build with an explanation'
}
[IO.File]::WriteAllText($fixtureVersion, "# Beschreibung`n7.8`n")
$result = Invoke-VersionBuild @('-p:UPDATER_BUILD_NUMBER=invalid', '-target:ValidateUpdaterVersion')
Assert ($result.ExitCode -ne 0 -and $result.Text.Contains('UPDATER_BUILD_NUMBER muss')) 'invalid per-series build numbers stop the build'
$result = Invoke-VersionBuild @('-p:UPDATER_BUILD_NUMBER=', '-p:GITHUB_ACTIONS=true', '-target:ValidateUpdaterVersion')
Assert ($result.ExitCode -ne 0 -and $result.Text.Contains('UPDATER_BUILD_NUMBER fehlt')) 'GitHub builds require an explicitly prepared per-series build number'

# Run only the real EXE naming step in an isolated directory. This fixture EXE
# is compiled for its version metadata and never executed; no GitHub calls occur.
$publishDir = Join-Path $fixtureRoot 'artifacts\publish\win-x64'
[void][IO.Directory]::CreateDirectory($publishDir)
$fixtureExe = Join-Path $publishDir 'NovaTouchUpdater.exe'
Add-Type -TypeDefinition @'
using System.Reflection;
[assembly: AssemblyVersion("7.8.123.0")]
[assembly: AssemblyFileVersion("7.8.123.0")]
[assembly: AssemblyInformationalVersion("7.8.123+fixture")]
public static class VersionFixture { public static void Main() {} }
'@ -OutputType ConsoleApplication -OutputAssembly $fixtureExe
$exeHash = (Get-FileHash -LiteralPath $fixtureExe -Algorithm SHA256).Hash
$workflow = [IO.File]::ReadAllText((Join-Path $projectRoot '.github\workflows\release.yml'))
$namingStep = [regex]::Match($workflow, '(?ms)^      - name: EXE pruefen und mit Versionsnummer benennen\r?\n        shell: pwsh\r?\n        run: \|\r?\n(?<Script>.*?)(?=^      - name:|\z)')
Assert ($namingStep.Success) 'the actual release workflow naming step is exercised'
$namingScript = [regex]::Replace($namingStep.Groups['Script'].Value, '(?m)^ {10}', '')
$previousGithubEnv = $env:GITHUB_ENV
$previousRunNumber = $env:GITHUB_RUN_NUMBER
$previousReleaseVersion = $env:UPDATER_RELEASE_VERSION
$env:GITHUB_ENV = Join-Path $fixtureRoot 'github-env.txt'
$env:GITHUB_RUN_NUMBER = '999'
$env:UPDATER_RELEASE_VERSION = '7.8.123'
Push-Location $fixtureRoot
try {
    $env:UPDATER_RELEASE_VERSION = '7.8.124'
    $mismatchFailed = $false
    try { & ([scriptblock]::Create($namingScript)) } catch { $mismatchFailed = $true }
    Assert ($mismatchFailed -and (Test-Path -LiteralPath $fixtureExe)) 'a mismatch between prepared and embedded version stops publishing before renaming'
    $env:UPDATER_RELEASE_VERSION = '7.8.123'
    & ([scriptblock]::Create($namingScript))
    $releaseExe = Join-Path $publishDir 'NovaTouchUpdater-v7.8.123.exe'
    Assert ((Test-Path -LiteralPath $releaseExe) -and -not (Test-Path -LiteralPath $fixtureExe)) 'the release filename follows embedded EXE metadata rather than a separate version constant'
    Assert ((Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash -eq $exeHash) 'naming preserves the built EXE contents'
    Assert ([IO.File]::ReadAllText($env:GITHUB_ENV).Trim() -eq 'RELEASE_TAG=v7.8.123') 'the GitHub release tag uses the same version as the EXE'
} finally {
    Pop-Location
    $env:GITHUB_ENV = $previousGithubEnv
    $env:GITHUB_RUN_NUMBER = $previousRunNumber
    $env:UPDATER_RELEASE_VERSION = $previousReleaseVersion
}
