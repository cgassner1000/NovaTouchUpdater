using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NovaTouchUpdater;

internal sealed record LdapComputer(string Name, string DnsName, string OperatingSystem, string Description)
{
    public string Host => string.IsNullOrWhiteSpace(DnsName) ? Name : DnsName;
}
internal sealed record LdapSearchRequest(string Domain, string SearchBase, string Filter);
internal sealed record LdapSearchResult(bool Success, LdapComputer[] Computers, string SearchBase, bool Truncated, string Error, bool DomainUnavailable = false);
internal sealed class LdapDomainUnavailableException(string message) : InvalidOperationException(message);

internal static class LdapComputerQuery
{
    internal static string ForPattern(string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) pattern = "*";
        var escaped = new StringBuilder();
        foreach (char value in pattern)
            escaped.Append(value switch { '\\' => @"\5c", '(' => @"\28", ')' => @"\29", '/' => @"\2f", '\0' => @"\00", _ => value.ToString() });
        // Only * is a wildcard. All LDAP control characters in a name pattern
        // stay literal, so a pattern cannot turn into another LDAP expression.
        return $"(&(objectCategory=computer)(name={escaped}))";
    }

    internal static string ForCustomFilter(string filter) => $"(&(objectCategory=computer){ValidateFilter(filter)})";

    internal static string ValidateFilter(string filter)
    {
        filter = filter.Trim();
        if (filter.Length < 3 || filter.Length > 65536 || filter[0] != '(' || filter[^1] != ')')
            throw new ArgumentException("Bitte einen vollständigen LDAP-Filter in Klammern eingeben, z. B. (name=SERVER-*).");
        int depth = 0;
        for (int i = 0; i < filter.Length; i++)
        {
            char value = filter[i];
            if (value == '\0') throw new ArgumentException("Der LDAP-Filter enthält ein ungültiges Nullzeichen.");
            if (value == '\\')
            {
                if (i + 2 >= filter.Length || !Uri.IsHexDigit(filter[i + 1]) || !Uri.IsHexDigit(filter[i + 2]))
                    throw new ArgumentException(@"LDAP-Escapezeichen müssen zwei Hexziffern enthalten, z. B. \28.");
                i += 2;
                continue;
            }
            if (value == '(') depth++;
            if (value == ')' && --depth < 0) throw new ArgumentException("Die Klammern des LDAP-Filters sind nicht ausgeglichen.");
            if (depth == 0 && i != filter.Length - 1) throw new ArgumentException("Bitte genau einen LDAP-Filter eingeben; mehrere Bedingungen mit & oder | verbinden.");
        }
        if (depth != 0) throw new ArgumentException("Die Klammern des LDAP-Filters sind nicht ausgeglichen.");
        return filter;
    }

    internal static LdapSearchRequest Create(string domain, string searchBase, string text, bool customFilter)
    {
        domain = domain.Trim();
        searchBase = searchBase.Trim();
        if (domain.Length > 0 && (Uri.CheckHostName(domain) == UriHostNameType.Unknown || domain.Any(c => c is '/' or '\\' or ':' || char.IsWhiteSpace(c))))
            throw new ArgumentException("Bitte einen Domänen- oder Domaincontroller-Namen ohne LDAP://, Port oder Pfad eingeben.");
        if (searchBase.Contains("://", StringComparison.Ordinal) || searchBase.Any(c => c is '\0' or '\r' or '\n'))
            throw new ArgumentException("Die Suchbasis muss ein Distinguished Name sein, z. B. OU=Kassen,DC=firma,DC=local.");
        return new(domain, searchBase, customFilter ? ForCustomFilter(text) : ForPattern(text));
    }
}

internal static class LdapComputerSearch
{
    internal static async Task<LdapSearchResult> SearchAsync(LdapSearchRequest request, CancellationToken cancellationToken)
    {
        LdapComputerQuery.ValidateFilter(request.Filter);
        string directory = Path.Combine(Path.GetTempPath(), "NovaTouchUpdater");
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "Directory-" + Guid.NewGuid().ToString("N") + ".ps1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = new Process();
        bool started = false;
        try
        {
            using var resource = typeof(LdapComputerSearch).Assembly.GetManifestResourceStream("NovaTouchUpdater.DirectorySearch.ps1")
                ?? throw new InvalidOperationException("Die LDAP-Suche fehlt in dieser EXE.");
            using var reader = new StreamReader(resource);
            await File.WriteAllTextAsync(script, await reader.ReadToEndAsync(timeout.Token), new UTF8Encoding(true), timeout.Token);
            process.StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            process.StartInfo.Environment.Remove("PSModulePath");
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                process.StartInfo.ArgumentList.Add(argument);
            started = process.Start();
            if (!started) throw new InvalidOperationException("Die Domänensuche konnte nicht gestartet werden.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, errors);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Die Domänensuche hat das Zeitlimit überschritten. Bitte Verbindung, Domäne und Suchfilter prüfen.");
            }
            string json = await output;
            string error = await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("Die Domänensuche wurde nicht abgeschlossen. " + error.Trim());
            LdapSearchResult result;
            try { result = JsonSerializer.Deserialize<LdapSearchResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidOperationException("Die Domänensuche hat keine gültige Antwort geliefert. " + error.Trim()); }
            if (!result.Success)
            {
                if (result.DomainUnavailable) throw new LdapDomainUnavailableException(result.Error);
                throw new InvalidOperationException(result.Error);
            }
            return result;
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            try { File.Delete(script); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
