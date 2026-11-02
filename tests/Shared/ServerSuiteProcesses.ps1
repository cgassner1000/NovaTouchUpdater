# Load the production helper with only its native executable replaced. All
# process enumeration and taskkill calls below use in-memory fixtures.
$processSource = Get-Content -LiteralPath (Join-Path $TestScriptsRoot 'UpdateSteps.ps1') -Raw -Encoding UTF8
$processAst = [Management.Automation.Language.Parser]::ParseInput($processSource, [ref]$null, [ref]$null)
$processHelper = $processAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'StopServerSuiteProcesses' }, $true)
$processHelperSource = $processHelper.Extent.Text.Replace(
    '$taskkill = Join-Path $env:SystemRoot ''System32\taskkill.exe''', '$taskkill = ''InvokeSuiteTaskkillFixture''')
. ([scriptblock]::Create($processHelperSource))

function ResetSuiteProcessFixture($trace, $services) {
    $script:suiteProcessTrace = $trace
    $script:suiteProcessServices = $services
    $script:suiteProcessFault = ''
    $script:suiteProcesses = @([pscustomobject]@{ ProcessName='python'; Id=7000 })
}
function Get-Process {
    [CmdletBinding()]param()
    Assert (@($script:suiteProcessServices.Values | Where-Object { $_.Name -like 'nt3_server_suite*' -and $_.Status -ne 'Stopped' }).Count -eq 0) 'All suite services must be stopped before searching for leftover processes'
    $script:suiteProcessTrace.Add('process-scan')
    return $script:suiteProcesses
}
function InvokeSuiteTaskkillFixture {
    Assert (($args -join '|') -ceq '/F|/FI|IMAGENAME eq nt3_server_suite*|/IM|*') 'taskkill must target only suite image names with the requested prefix'
    Assert (@($script:suiteProcessServices.Values | Where-Object { $_.Name -like 'nt3_server_suite*' -and $_.Status -ne 'Stopped' }).Count -eq 0) 'All suite services must remain stopped during taskkill'
    $script:suiteProcessTrace.Add('taskkill:suite')
    if ($script:suiteProcessFault -in @('denied','survives')) {
        Set-Variable -Name LASTEXITCODE -Scope 1 -Value $(if ($script:suiteProcessFault -eq 'denied') { 5 } else { 0 })
        return 'simulated taskkill failure'
    }
    $script:suiteProcesses = @($script:suiteProcesses | Where-Object { $_.ProcessName -notlike 'nt3_server_suite*' })
    Set-Variable -Name LASTEXITCODE -Scope 1 -Value $(if ($script:suiteProcessFault -eq 'disappeared') { 128 } else { 0 })
    return 'simulated taskkill output'
}
function AddSuiteProcessFixtures {
    $script:suiteProcesses += @(
        [pscustomobject]@{ ProcessName='nt3_server_suite'; Id=7101 },
        [pscustomobject]@{ ProcessName='nt3_server_suite_123'; Id=7102 },
        [pscustomobject]@{ ProcessName='NT3_SERVER_SUITERetail'; Id=7103 })
}
