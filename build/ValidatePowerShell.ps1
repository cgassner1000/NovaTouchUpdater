param([Parameter(Mandatory=$true)][string]$ScriptsDirectory)

$ErrorActionPreference = 'Stop'
$directory = (Get-Item -LiteralPath $ScriptsDirectory -ErrorAction Stop).FullName
$files = @([IO.Directory]::EnumerateFiles($directory, '*.ps1', [IO.SearchOption]::AllDirectories) | Sort-Object)
if ($files.Count -eq 0) { throw "No PowerShell scripts found in $directory" }
$failed = $false
foreach ($file in $files) {
    $source = [IO.File]::ReadAllText($file)
    $marker = [regex]::Match($source, '(?m)^[\t ]*(?:<{7}(?:\s|$)|={7}[\t ]*\r?$|>{7}(?:\s|$)|\|{7}(?:\s|$))')
    if ($marker.Success) {
        $line = 1 + [regex]::Matches($source.Substring(0, $marker.Index), '\n').Count
        [Console]::Error.WriteLine("${file}($line): unresolved Git conflict marker in PowerShell source.")
        $failed = $true
        continue
    }
    $tokens = $null; $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseInput($source, $file, [ref]$tokens, [ref]$parseErrors)
    foreach ($problem in @($parseErrors | Select-Object -First 5)) {
        [Console]::Error.WriteLine("${file}($($problem.Extent.StartLineNumber),$($problem.Extent.StartColumnNumber)): $($problem.Message)")
        $failed = $true
    }
}
if ($failed) { exit 1 }
Write-Output "PowerShell validation succeeded ($($files.Count) scripts, syntax only)."
