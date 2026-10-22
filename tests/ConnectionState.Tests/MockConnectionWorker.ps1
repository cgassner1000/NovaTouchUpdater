# Protocol fixture only: no networking or remote commands.
$request = [Console]::In.ReadLine() | ConvertFrom-Json
foreach ($computer in $request.Hosts) {
    $connected = if ($computer -eq 'unknown.invalid') { $null } else { $computer -ne 'offline.invalid' }
    @{ Event='progress'; Host=$computer; Connected=$connected; Message='simulated' } | ConvertTo-Json -Compress | ForEach-Object { [Console]::Out.WriteLine($_) }
}
[Console]::Out.WriteLine('{"Event":"result","Success":true,"Services":[]}')
