using NovaTouchUpdater;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            FilterTests();
            ProtocolTests().GetAwaiter().GetResult();
            DialogTests();
            LayoutTests();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static void Rejects(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { Console.WriteLine("PASS " + message); return; }
        throw new InvalidOperationException(message);
    }

    private static void FilterTests()
    {
        foreach (string pattern in new[] { "PC*-RST-POS-*", "SERVER-*", "*-KASSE-ABC", "*" })
            Assert(LdapComputerQuery.ForPattern(pattern) == $"(&(objectCategory=computer)(name={pattern}))", "arbitrary pattern is sent as an LDAP substring query: " + pattern);
        Assert(LdapComputerQuery.ForPattern(@"A*)(objectClass=*)/\") == @"(&(objectCategory=computer)(name=A*\29\28objectClass=*\29\2f\5c))",
            "LDAP operators and backslashes in a name pattern are literal and cannot inject a filter");
        string custom = @"(&(|(name=ALT-*)(name=OTHER-*))(description=POS\28EG\29)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";
        Assert(LdapComputerQuery.ForCustomFilter(custom) == $"(&(objectCategory=computer){custom})", "custom AND/OR/NOT filters and escaped literals remain available within the computer restriction");
        foreach (string invalid in new[] { "", "name=*", "()", "(name=*) (objectClass=*)", "(&(name=*)", @"(name=bad\zz)", "(name=bad\0)" })
            Rejects(() => LdapComputerQuery.ForCustomFilter(invalid), "malformed or multiple-root filter is rejected");
        Rejects(() => LdapComputerQuery.ForPattern(" "), "blank pattern does not implicitly search every computer");
        foreach (string domain in new[] { "LDAP://example.test", "example.test/RootDSE", "dc.example.test:389", "invalid domain" })
            Rejects(() => LdapComputerQuery.Create(domain, "", "*", false), "domain field accepts a host name instead of an injected binding path");
        var request = LdapComputerQuery.Create(" example.test ", " OU=Kassen,DC=example,DC=test ", "SERVER-*", false);
        Assert(request.Domain == "example.test" && request.SearchBase == "OU=Kassen,DC=example,DC=test", "domain and optional OU search base remain independently configurable");
    }

    private static async Task ProtocolTests()
    {
        // This invalid domain is rejected by the real embedded worker BEFORE
        // creating any DirectoryEntry. No real domain or host is contacted.
        bool rejected = false;
        try { await LdapComputerSearch.SearchAsync(new("invalid/domain", "", LdapComputerQuery.ForPattern("*")), CancellationToken.None); }
        catch (InvalidOperationException ex) { rejected = ex.Message.Contains("Domänenname"); }
        Assert(rejected, "embedded PowerShell worker returns a UTF-8 error through the real JSON protocol without an AD query");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        bool cancelled = false;
        try { await LdapComputerSearch.SearchAsync(new("invalid/domain", "", LdapComputerQuery.ForPattern("*")), cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "already-cancelled search does not start a worker or contact a domain");
    }

    private static IEnumerable<Control> All(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control item in All(child)) yield return item;
    }

    private static void Complete(Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(5); }
        Assert(task.IsCompleted, "asynchronous dialog operation completes");
        task.GetAwaiter().GetResult();
    }

    private static void DialogTests()
    {
        LdapSearchRequest? observed = null;
        int searches = 0;
        using var dialog = new LdapComputerSearchDialog((request, _) =>
        {
            searches++; observed = request;
            return Task.FromResult(new LdapSearchResult(true, [
                new("A", "a.example.test", "Windows", "Test"), new("B", "b.example.test", "Windows", "Test")
            ], "DC=example,DC=test", false, ""));
        });
        _ = dialog.Handle; // no Form.Show and no network-capable default search
        var controls = All(dialog).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
        var pattern = (TextBox)controls["pattern"];
        var filter = (TextBox)controls["customFilter"];
        var modes = (TabControl)controls["modes"];
        var import = (Button)controls["import"];
        var status = controls["searchStatus"];
        Assert(searches == 0 && pattern.TextLength == 0 && !import.Enabled && !controls["stop"].Enabled,
            "opening the dialog starts no search, hardcodes no pattern and selects no computers");
        pattern.Text = "WHATEVER-*";
        Complete(dialog.SearchAsync());
        Assert(observed is not null, "pattern search starts: " + status.Text);
        Assert(observed!.Filter.Contains("(name=WHATEVER-*)") && !import.Enabled, "freely entered pattern reaches the search and results require explicit selection");
        dialog.SetAllSelected(true);
        Assert(import.Enabled && dialog.GetSelectedHosts().SequenceEqual(new[] { "a.example.test", "b.example.test" }), "multiple checked computers are imported by DNS name");
        dialog.SetAllSelected(false);
        Assert(!import.Enabled && dialog.GetSelectedHosts().Length == 0, "clearing selection disables import");
        dialog.SetAllSelected(true);
        pattern.Text = "OTHER-*";
        Assert(!import.Enabled && status.Text.Contains("erneut suchen"), "changed query cannot import stale results");
        modes.SelectedIndex = 1;
        filter.Text = "(|(name=ALT-*)(description=*POS*))";
        Complete(dialog.SearchAsync());
        Assert(observed!.Filter == "(&(objectCategory=computer)(|(name=ALT-*)(description=*POS*)))", "advanced mode sends the custom LDAP expression and still selects computers only");
        filter.Text = "broken";
        int before = searches;
        Complete(dialog.SearchAsync());
        Assert(searches == before && !import.Enabled && status.ForeColor == Color.Firebrick, "invalid custom filter is reported before a search starts");

        const string directoryError = "Computer suchen [LDAP://dc.example.test/DC=example,DC=test]: simulated LDAP bind error (0x80072020)";
        using var failed = new LdapComputerSearchDialog((_, _) => Task.FromException<LdapSearchResult>(new InvalidOperationException(directoryError)));
        _ = failed.Handle;
        var failedControls = All(failed).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
        ((TextBox)failedControls["pattern"]).Text = "TEST-*";
        Complete(failed.SearchAsync());
        Assert(failedControls["searchStatus"].Text == "Suche fehlgeschlagen: " + directoryError
            && failedControls["find"].Enabled && !failedControls["stop"].Enabled && !failedControls["import"].Enabled,
            "original LDAP diagnostics reach the dialog and a failed search remains retryable");

        using var waiting = new LdapComputerSearchDialog((_, token) =>
        {
            var completion = new TaskCompletionSource<LdapSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            return completion.Task;
        });
        _ = waiting.Handle;
        var waitingControls = All(waiting).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
        ((TextBox)waitingControls["pattern"]).Text = "TEST-*";
        Task running = waiting.SearchAsync();
        Assert(waitingControls["stop"].Enabled && !waitingControls["find"].Enabled && !waitingControls["import"].Enabled, "running search can be cancelled and cannot import partial results");
        waiting.CancelSearch();
        Complete(running);
        Assert(waitingControls["find"].Enabled && !waitingControls["stop"].Enabled && waitingControls["searchStatus"].Text.Contains("abgebrochen"), "cancel returns the dialog to a usable state");
        Task closing = waiting.SearchAsync();
        waiting.Close();
        Complete(closing);
        Assert(waiting.IsDisposed, "closing the dialog cancels and disposes an active search");
    }

    private static void LayoutTests()
    {
        foreach (float scale in new[] { 1F, 1.25F, 1.5F, 2F })
        {
            using var dialog = new LdapComputerSearchDialog((_, _) => throw new InvalidOperationException("Layout test must not query a domain"));
            dialog.Scale(new SizeF(scale, scale));
            dialog.Font = new Font("Segoe UI", 9F * scale);
            var controls = All(dialog).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
            controls["customFilter"].Font = new Font("Consolas", 9F * scale);
            _ = dialog.Handle;
            foreach (int width in new[] { 960, 700 })
            {
                dialog.ClientSize = new Size((int)(width * scale), (int)(650 * scale));
                for (int pass = 0; pass < 8; pass++) PerformLayout(dialog);
                var buttons = All(dialog).OfType<Button>().ToArray();
                Assert(buttons.All(b => b.Width >= b.GetPreferredSize(Size.Empty).Width && b.Height >= b.GetPreferredSize(Size.Empty).Height),
                    $"all search and selection buttons fit their text at {width}px / {scale * 100}%");
                Assert(buttons.Select(b => b.Height).Distinct().Count() == 1, $"dialog buttons have equal height at {scale * 100}%");
                Assert(new[] { "domain", "searchBase", "pattern" }.All(name => controls[name].Width > 100 * scale && controls[name].Height >= controls[name].GetPreferredSize(Size.Empty).Height),
                    $"query text fields have usable width and height at {width}px / {scale * 100}%");
                Assert(!controls["results"].Bounds.IntersectsWith(controls["modes"].Bounds), "query controls and results do not overlap");
                if (scale == 1F && width == 960)
                {
                    // Render the content in an off-screen parent with no hidden
                    // Form ancestor, so DrawToBitmap includes child controls.
                    Control content = dialog.Controls[0];
                    using var canvas = new Panel { Size = dialog.ClientSize, Font = dialog.Font };
                    canvas.Controls.Add(content);
                    canvas.CreateControl();
                    foreach (Control control in All(canvas)) _ = control.Handle;
                    for (int pass = 0; pass < 8; pass++) PerformLayout(canvas);
                    using var bitmap = new Bitmap(canvas.Width, canvas.Height);
                    canvas.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    string directory = Path.Combine(Directory.GetCurrentDirectory(), "obj");
                    Directory.CreateDirectory(directory);
                    bitmap.Save(Path.Combine(directory, "ldap-search-dialog.png"));
                    canvas.Controls.Remove(content);
                    dialog.Controls.Add(content);
                }
            }
        }
    }

    private static void PerformLayout(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls) PerformLayout(child);
    }
}
