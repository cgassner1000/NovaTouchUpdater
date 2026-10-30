. (Join-Path $PSScriptRoot '..\Shared\TestEnvironment.ps1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
function ExpectFailure([scriptblock]$Action, [string]$Message) { $failed=$false; try { & $Action | Out-Null } catch { $failed=$true }; Assert $failed $Message }
foreach ($name in @('MsiUpdates','SoftwareDatabase','DatabaseStreaming','RemoteSoftwareDatabase')) {
    . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TestScriptsRoot ($name + '.ps1')))))
}
Add-Type -Path (Join-Path $PSScriptRoot '..\Shared\SoftwareDatabaseFake.cs') -ReferencedAssemblies System.Data, System.Core
function InitializeSoftwareDatabaseProvider { [pscustomobject]@{ ConnectionType=[SoftwareDatabaseFixture.Connection]; BuilderType=[SoftwareDatabaseFixture.Builder]; ObjectType=[SoftwareDatabaseFixture.DynamicObject]; ClientType=[SoftwareDatabaseFixture.ClientType]; BinaryStreamType=[SoftwareDatabaseFixture.BinaryStream] } }
function Progress { param($Phase,$Message,$Percent) }
function Emit($Value) { $script:result=$Value }
function Start-Process { throw 'No installers in this test' }
function Stop-Service { throw 'No service mutations in this test' }
function New-PSSession { throw 'No actual remote connections in this test' }
$fixture = Join-Path $TestArtifactsRoot ('remote-database-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$originalTemp=$env:TEMP; $originalTmp=$env:TMP; $previousInput=[Console]::In
$env:TEMP=$fixture; $env:TMP=$fixture
try {
    $cpf = Join-Path $fixture 'active configuration.cpf'
    [IO.File]::WriteAllText($cpf, "[Startup]`nDefaultPort=51774`n[Namespaces]`n%SYS=SYS`n%OTHER=OTHER`nUSER=USER`nAPP=APP`napp=APP`n")
    function ReadSoftwareIrisRegistrations {
        [pscustomobject]@{ Name='IRIS'; Directory=$fixture; ConfigurationFile=$cpf; Version='2026.1'; Error='' }
        [pscustomobject]@{ Name='iris'; Directory=''; ConfigurationFile=''; Version=''; Error='' }
        [pscustomobject]@{ Name='MISSING'; Directory=(Join-Path $fixture 'missing'); ConfigurationFile=''; Version=''; Error='' }
    }
    $inventory = @(ReadSoftwareIrisInventory)
    Assert ($inventory.Count -eq 2 -and $inventory[0].Namespaces.Count -eq 2 -and $inventory[0].SuperServerPort -eq 51774 -and $inventory[1].Error) 'target inventory reads the active CPF, filters all percent namespaces, deduplicates registry views and isolates missing files'
    $script:remoteInventory=@($inventory[0])
    $targets=@(ConvertSoftwareIrisTargets $script:remoteInventory $false)
    Assert ($targets.Count -eq 1 -and $targets[0].Namespaces -contains 'USER') 'remote upload targets come from the target inventory including USER'
    $index = Get-Content -LiteralPath (Join-Path $TestArtifactsRoot 'local-msi-fixtures.json') -Raw | ConvertFrom-Json
    $path=[string]$index.ntc
    $installer=New-Object -ComObject WindowsInstaller.Installer; $db=$installer.OpenDatabase($path,0); $values=@{}
    try {
        foreach ($name in @('ProductName','ProductVersion','ProductCode')) {
            $view=$db.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''' + $name + ''''); [void]$view.Execute(); $record=$view.Fetch()
            $values[$name]=[string]$record.StringData(1)
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
    $package=[pscustomobject]@{ Kind='ntc'; ProductName=$values.ProductName; ProductVersion=$values.ProductVersion; ProductCode=$values.ProductCode; PayloadPath=$path; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; RelativeMsiPath='' }
    $targets[0].Namespaces=@('USER','APP','APP2','APP3','APP4')
    $plan=@(GetSoftwareDatabaseRemotePlan @($package) $targets $fixture)
    Assert ($plan.Count -eq 5 -and @($plan | Where-Object { $_.Needed -contains 'ntc' }).Count -eq 5) 'remote preflight plans missing packages independently for each application namespace'
    $bytes=[IO.File]::ReadAllBytes($path)
    $package | Add-Member -NotePropertyName Length -NotePropertyValue $bytes.Length
    $lines=[Text.StringBuilder]::new()
    for ($wave=0; $wave -lt 3; $wave++) {
        for ($offset=0; $offset -lt $bytes.Length; $offset+=1024) { [void]$lines.AppendLine([Convert]::ToBase64String($bytes,$offset,[Math]::Min(1024,$bytes.Length-$offset))) }
    }
    InitializeSoftwareDatabaseStreaming
    [Console]::SetIn([IO.StringReader]::new($lines.ToString()))
    [SoftwareDatabaseFixture.Database]::WriteDelayMilliseconds=100
    try { PublishStreamedSoftwarePackages @($package) $plan $fixture 2 }
    finally { [SoftwareDatabaseFixture.Database]::WriteDelayMilliseconds=0; [Console]::SetIn($previousInput) }
    Assert ($script:result.Success -and $script:result.Changed -and [SoftwareDatabaseFixture.Database]::Entries.Count -eq 5 -and [SoftwareDatabaseFixture.Database]::PeakTransactions -eq 2) 'bounded WinRM streams feed independent namespace transactions in multiple waves using the configured concurrency'
    foreach ($entry in [SoftwareDatabaseFixture.Database]::Entries) {
        Assert ([Convert]::ToBase64String($entry.Bytes) -eq [Convert]::ToBase64String($bytes)) 'every namespace receives the complete original MSI bytes through the stream'
    }
    foreach ($entry in [SoftwareDatabaseFixture.Database]::Entries) { $entry.Version='9999.0.0' }
    $plan=@(GetSoftwareDatabaseRemotePlan @($package) $targets $fixture)
    Assert (@($plan | Where-Object { $_.Needed.Count -gt 0 }).Count -eq 0) 'remote preflight skips newer database versions before any payload transfer'
    foreach ($entry in [SoftwareDatabaseFixture.Database]::Entries) { $entry.Version=$package.ProductVersion }
    $plan=@(GetSoftwareDatabaseRemotePlan @($package) $targets $fixture)
    Assert (@($plan | Where-Object { $_.Needed.Count -gt 0 }).Count -eq 0) 'remote preflight skips equal database versions before any payload transfer'
    foreach ($entry in [SoftwareDatabaseFixture.Database]::Entries) { $entry.Version='0.0.0' }
    $plan=@(GetSoftwareDatabaseRemotePlan @($package) $targets $fixture)
    [Console]::SetIn([IO.StringReader]::new(''))
    try { ExpectFailure { PublishStreamedSoftwarePackages @($package) @($plan[0]) $fixture 1 } 'an interrupted WinRM stream fails and rolls the namespace back instead of saving partial MSI data' }
    finally { [Console]::SetIn($previousInput) }
    Assert ([SoftwareDatabaseFixture.Database]::ActiveTransactions -eq 0 -and [SoftwareDatabaseFixture.Database]::Entries.Count -eq 5) 'stream failures release namespace transactions and preserve existing setups'

    # Real child process and native stdout/stdin relay, without provider/DB access.
    $child=Join-Path $fixture 'relay.ps1'
    [IO.File]::WriteAllText($child, @'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
[Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
$request=[Console]::In.ReadLine() | ConvertFrom-Json
[Console]::Out.WriteLine((@{Event='progress';Message=$request.Name} | ConvertTo-Json -Compress))
$bytes=[Convert]::FromBase64String([Console]::In.ReadLine())
[Console]::Out.WriteLine((@{Event='result';Success=($bytes.Length -eq 3)} | ConvertTo-Json -Compress))
'@)
    StartSoftwareDatabaseChild $child '{"Name":"Prüfung"}' ${function:InitializeSoftwareDatabaseStreaming}.ToString()
    $events=@(ReadSoftwareDatabaseChild 'AQID' $true)
    Assert ($events.Count -eq 2 -and $events[0].Message -eq 'Prüfung' -and $events[1].Success) 'the remote native process relay preserves JSON, Unicode progress and streamed stdin without a PowerShell background runspace'

    [IO.File]::WriteAllText($child, @'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
[Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
$request=[Console]::In.ReadLine() | ConvertFrom-Json
$destinations=@($request.DatabasePlan | Where-Object { -not $_.Error -and $_.Needed.Count -gt 0 })
$received=0L
for ($offset=0; $offset -lt $destinations.Count; $offset+=$request.DatabaseUploadParallelism) {
    foreach ($package in $request.MsiPackages) {
        $count=0L
        while ($count -lt $package.Length) {
            $line=[Console]::In.ReadLine()
            if ($null -eq $line) { throw 'Incomplete simulated remote input' }
            $count += [Convert]::FromBase64String($line).Length
        }
        $received += $count
    }
}
[Console]::Out.WriteLine((@{Event='result';Success=($received -eq (3*$request.MsiPackages[0].Length));Changed=$true;Error=''} | ConvertTo-Json -Compress))
'@)
    function InvokeWorkerCommand {
        [CmdletBinding()]param($Session,[scriptblock]$ScriptBlock,[object[]]$ArgumentList=@())
        & $ScriptBlock @ArgumentList
    }
    $streamOutcome=InvokeSoftwareDatabaseChildOperation $null $child @{ Action='publish-msi-stream';MsiPackages=@($package);DatabasePlan=$plan;DatabaseUploadParallelism=2 } @([pscustomobject]@{Path=$path;Package=$package}) $plan
    Assert ($streamOutcome.Success) 'the production WinRM chunk orchestration streams all waves through the actual child relay and returns its result'

    # Orchestrate against an isolated fake target filesystem; no WinRM or DB calls.
    $script:copies=[Collections.Generic.List[string]]::new(); $script:children=[Collections.Generic.List[object]]::new()
    $script:plan=$plan; $script:remoteInventory=@($inventory[0])
    function InvokeWorkerCommand {
        [CmdletBinding()] param($Session,[scriptblock]$ScriptBlock,[object[]]$ArgumentList=@())
        if ($ScriptBlock.ToString().Contains('function ReadSoftwareIrisRegistrations')) { return $script:remoteInventory }
        & $ScriptBlock @ArgumentList
    }
    function CopyRemoteFileWithProgress($Session,$Source,$Target,$Label) { $script:copies.Add($Target); Copy-Item -LiteralPath $Source -Destination $Target -Force }
    function InvokeSoftwareDatabaseChildOperation($Session,$Worker,$Request,$Resolved=@(),$Plan=@()) {
        $script:children.Add([pscustomobject]@{Request=$Request;Resolved=$Resolved;Plan=$Plan})
        if ($Request.Action -eq 'database-upload-plan') { return [pscustomobject]@{Success=$true;DatabasePlan=$script:plan} }
        [pscustomobject]@{Success=$true;Changed=$true;Event='result'}
    }
    foreach ($name in @('InterSystems.Data.IRISClient.dll','InterSystems.Data.Bindings.dll','IRISClient.LICENSE.txt','Bindings.LICENSE.txt','Worker.ps1')) { [IO.File]::WriteAllText((Join-Path $fixture $name),'isolated fixture') }
    $destination=Join-Path $fixture 'target'
    $request=[pscustomobject]@{MsiPackages=@($package);IrisUseTls=$false;DatabaseProviderDirectory=$fixture;DatabaseUploadParallelism=2;SkipLocalPackageCopy=$true;SkipZipTransfer=$false;RemoteInstallPath=$destination}
    $outcome=PublishRemoteSoftwarePackages $request $null (Join-Path $fixture 'Worker.ps1')
    Assert ($outcome.Success -and $script:copies.Count -eq 5 -and -not (Test-Path -LiteralPath $destination) -and $script:children[1].Request.Action -eq 'publish-msi-stream' -and $script:children[1].Resolved.Count -eq 1) 'direct mode transfers the worker, provider and licenses but streams MSIs without creating a target payload folder'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'NovaTouchUpdater') -Directory).Count -eq 0) 'private remote staging and local extraction directories are removed after direct upload'
    $script:copies.Clear();$script:children.Clear();$request.SkipLocalPackageCopy=$false
    $outcome=PublishRemoteSoftwarePackages $request $null (Join-Path $fixture 'Worker.ps1')
    $copied=@($script:copies | Where-Object { [IO.Path]::GetDirectoryName($_) -eq $destination })
    Assert ($outcome.Success -and $copied.Count -eq 1 -and $copied[0].EndsWith('.msi') -and $script:children[1].Request.Action -eq 'publish-msi-packages' -and $script:children[1].Request.MsiPackages[0].PayloadPath -eq $copied[0]) 'copy mode first places the MSI in the configured target folder and passes that exact file to the database worker'
    $script:copies.Clear();$script:children.Clear();$request.SkipZipTransfer=$true
    $outcome=PublishRemoteSoftwarePackages $request $null (Join-Path $fixture 'Worker.ps1')
    Assert ($outcome.Success -and $script:copies.Count -eq 5 -and $script:children[1].Request.MsiPackages[0].PayloadPath -eq (Join-Path $destination ([IO.Path]::GetFileName($path)))) 'existing-client-file mode copies no package and resolves the selected filename in the target folder'
    $script:copies.Clear();$script:children.Clear();$script:plan=@($plan | ForEach-Object { $_.Needed=@(); $_ })
    $outcome=PublishRemoteSoftwarePackages $request $null (Join-Path $fixture 'Worker.ps1')
    Assert ($outcome.Skipped -and $script:copies.Count -eq 5 -and $script:children.Count -eq 1) 'a fully current remote database requires no MSI transfer or upload worker'
    $script:copies.Clear();$script:children.Clear();$script:remoteInventory=@()
    $outcome=PublishRemoteSoftwarePackages $request $null (Join-Path $fixture 'Worker.ps1')
    Assert ($outcome.Skipped -and $script:copies.Count -eq 0 -and $script:children.Count -eq 0) 'targets without IRIS instances skip database upload without staging provider or payload files'
    ExpectFailure { RemoveSoftwareDatabaseRemoteStage $destination $fixture } 'remote cleanup rejects user target folders outside the private GUID staging pattern'
    Assert (Test-Path -LiteralPath $copied[0]) 'remote cleanup preserves the configured package target folder'
    Write-Host 'PASS Remote database tests completed without real hosts, database connections, service actions or installers'
} finally {
    ReadSoftwareDatabaseChild -Stop $true
    [Console]::SetIn($previousInput); $env:TEMP=$originalTemp; $env:TMP=$originalTmp
    Assert ([IO.Path]::GetFullPath($fixture).StartsWith([IO.Path]::GetFullPath($TestArtifactsRoot).TrimEnd('\')+'\')) 'remote test cleanup stays within test artifacts'
    Remove-Item -LiteralPath $fixture -Force -Recurse
}
