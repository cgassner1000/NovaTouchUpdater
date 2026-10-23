# Self-contained: serialized into the authenticated remote session.
function ManageUploadDirectory {
    param([string]$path, [bool]$clean, [string]$expectedSnapshot)
    $ErrorActionPreference='Stop'
    Set-StrictMode -Version Latest
    function ContainsPath([string]$parent, [string]$child) {
        $child.Equals($parent,[StringComparison]::OrdinalIgnoreCase) -or
            $child.StartsWith($parent.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)
    }
    function CheckAncestors([string]$target) {
        $cursor=$target
        while ($cursor) {
            if (Test-Path -LiteralPath $cursor) {
                $item=Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Verknüpfung/Junction im Uploadziel: $cursor" }
            }
            $cursor=[IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        }
    }
    function ProtectionReason([string]$target) {
        if ($target.TrimEnd('\').Length -le [IO.Path]::GetPathRoot($target).TrimEnd('\').Length) { return 'Laufwerksstamm' }
        foreach ($system in @($env:SystemRoot,$env:ProgramFiles,${env:ProgramFiles(x86)})) {
            if ($system -and ((ContainsPath $target $system) -or (ContainsPath $system $target))) { return "System-/Programmverzeichnis: $system" }
        }
        foreach ($special in @($env:USERPROFILE,$env:ProgramData,[Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('MyDocuments'))) {
            if ($special -and (ContainsPath $target $special)) { return "Geschütztes Stammverzeichnis: $special" }
        }
        try {
            foreach ($service in @(Get-Service -ErrorAction Stop | Where-Object { $_.Name -match '^(nt\.(payment|fiscal)|nt3_server_suite)' })) {
                $image=[Environment]::ExpandEnvironmentVariables([string](Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $service.Name) -Name ImagePath -ErrorAction Stop).ImagePath)
                if ($image -match '^\s*"([^"]+?\.exe)"') { $exe=$Matches[1] }
                elseif ($image -match '^\s*(.+?\.exe)(?:\s|$)') { $exe=$Matches[1] }
                else { return "Dienstpfad nicht sicher lesbar: $($service.Name)" }
                if ($exe -notmatch '^[A-Za-z]:\\') { return "Kein absoluter Dienstpfad: $($service.Name)" }
                $protected=@([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($exe)))
                if ($service.Name -match '^nt3_server_suite') {
                    $cursor=$protected[0]
                    while ($cursor -and [IO.Path]::GetFileName($cursor.TrimEnd('\')) -ine 'portable_python') { $cursor=[IO.Path]::GetDirectoryName($cursor.TrimEnd('\')) }
                    if (-not $cursor) { return "Server-Suite-Pfad nicht sicher zuordenbar: $($service.Name)" }
                    $protected=@($cursor,(Join-Path ([IO.Path]::GetDirectoryName($cursor)) 'apps\nt3-server-suite'))
                }
                foreach ($installed in $protected) {
                    if ((ContainsPath $target $installed) -or (ContainsPath $installed $target)) { return "Installationsverzeichnis von $($service.Name): $installed" }
                }
            }
        }
        catch { return ('Installationsschutz konnte nicht geprüft werden: ' + $_.Exception.Message) }
        return ''
    }
    function Snapshot([string]$target) {
        $top=@(Get-ChildItem -LiteralPath $target -Force -ErrorAction Stop)
        $records=[Collections.Generic.List[string]]::new()
        $pending=[Collections.Generic.Stack[object]]::new()
        foreach ($item in $top) { $pending.Push($item) }
        while ($pending.Count -gt 0) {
            $item=$pending.Pop()
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Verknüpfung/Junction im Uploadordner: $($item.FullName)" }
            $relative=$item.FullName.Substring($target.TrimEnd('\').Length).TrimStart('\')
            $length=if ($item.PSIsContainer) { -1 } else { $item.Length }
            $records.Add("$relative|$length|$($item.LastWriteTimeUtc.Ticks)")
            if ($item.PSIsContainer) { foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop) { $pending.Push($child) } }
        }
        $identity=[Environment]::MachineName + '|' + $target.ToUpperInvariant() + "`n" + (($records | Sort-Object) -join "`n")
        $hash=[Security.Cryptography.SHA256]::Create()
        try { $token=([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity)))).Replace('-','') }
        finally { $hash.Dispose() }
        [pscustomobject]@{ Token=$token; Entries=$top }
    }
    if ($path -notmatch '^[A-Za-z]:\\') { throw "Kein absoluter Windows-Uploadpfad: $path" }
    if ($clean -and [string]::IsNullOrWhiteSpace($expectedSnapshot)) { throw 'Keine bestätigte Verzeichnisprüfung für die Bereinigung vorhanden.' }
    $target=[IO.Path]::GetFullPath($path)
    if ($target.Length -gt 3) { $target=$target.TrimEnd('\') }
    CheckAncestors $target
    $reason=ProtectionReason $target
    $info=[pscustomobject]@{ Path=$target; EntryCount=0; Snapshot=''; CanClean=($reason -eq ''); ProtectionReason=$reason; Cleaned=$false }
    if (-not (Test-Path -LiteralPath $target)) { return $info }
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw "Uploadziel ist kein Ordner: $target" }
    $info.EntryCount=@(Get-ChildItem -LiteralPath $target -Force -ErrorAction Stop).Count
    if (-not $info.CanClean) {
        if ($clean) { throw "Uploadordner darf nicht bereinigt werden: $reason" }
        return $info
    }
    $snapshot=Snapshot $target
    $info.EntryCount=$snapshot.Entries.Count
    $info.Snapshot=$snapshot.Token
    if (-not $clean -or $info.EntryCount -eq 0) { return $info }
    if ($snapshot.Token -cne $expectedSnapshot) { throw 'Der Inhalt des Uploadordners hat sich seit der Abfrage geändert. Bitte den Upload erneut starten.' }
    foreach ($entry in $snapshot.Entries) {
        # Only checked, direct children of the confirmed absolute upload path.
        $child=[IO.Path]::GetFullPath($entry.FullName)
        if (-not $child.StartsWith($target.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetDirectoryName($child).Equals($target.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { throw "Ungültiger Bereinigungspfad: $child" }
        CheckAncestors $child
        Remove-Item -LiteralPath $child -Recurse -Force -ErrorAction Stop
    }
    $info.Cleaned=$true
    $info.EntryCount=@(Get-ChildItem -LiteralPath $target -Force -ErrorAction Stop).Count
    if ($info.EntryCount -ne 0) { throw 'Uploadordner ist nach der Bereinigung nicht leer; Upload abgebrochen.' }
    return $info
}
