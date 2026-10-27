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
            NetworkSearchTests.Run().GetAwaiter().GetResult();
            ProtocolTests().GetAwaiter().GetResult();
            FormattingTests();
            DialogTests();
            NetworkDialogTests();
            SelectionInteractionTests();
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

    private static LdapComputerSearchDialog DomainDialog(Func<LdapSearchRequest, CancellationToken, Task<LdapSearchResult>> search) =>
        new(new ComputerDiscovery(directorySearch: search, domainAvailable: _ => Task.FromResult(true),
            networkSearch: (_, _, _) => throw new InvalidOperationException("Domain-only tests must not scan a real network")));

    private static void FormattingTests()
    {
        using var dialog = DomainDialog((_, _) => throw new InvalidOperationException("Formatting test must not query a domain"));
        var grid = All(dialog).OfType<DataGridView>().Single();
        var errors = new List<string>();
        grid.DataError += (_, e) => { errors.Add(e.Exception?.ToString() ?? e.Context.ToString()); e.ThrowException = false; };
        int row = grid.Rows.Add();
        var checkbox = grid.Rows[row].Cells["include"];
        Assert(checkbox.FormattedValue is false, "empty domain-selection cells format as unchecked checkboxes");
        foreach (bool value in new[] { true, false })
        {
            checkbox.Value = value;
            Assert(checkbox.FormattedValue is bool formatted && formatted == value, "domain-selection cells retain boolean display values");
        }
        Assert(errors.Count == 0, "domain-selection formatting raises no DataGridView errors: " + string.Join("\n", errors));
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
        foreach (string blank in new[] { "", " ", "\t\r\n" })
            Assert(LdapComputerQuery.ForPattern(blank) == "(&(objectCategory=computer)(name=*))",
                "empty or whitespace-only name patterns search for all computers");
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
        using var dialog = DomainDialog((request, _) =>
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
        Complete(dialog.SearchAsync());
        Assert(observed?.Filter == "(&(objectCategory=computer)(name=*))" && !import.Enabled,
            "searching with an untouched pattern field requests all computers without selecting any");
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
        using var failed = DomainDialog((_, _) => Task.FromException<LdapSearchResult>(new InvalidOperationException(directoryError)));
        _ = failed.Handle;
        var failedControls = All(failed).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
        ((TextBox)failedControls["pattern"]).Text = "TEST-*";
        Complete(failed.SearchAsync());
        Assert(failedControls["searchStatus"].Text == "Suche fehlgeschlagen: " + directoryError
            && failedControls["find"].Enabled && !failedControls["stop"].Enabled && !failedControls["import"].Enabled,
            "original LDAP diagnostics reach the dialog and a failed search remains retryable");

        using var waiting = DomainDialog((_, token) =>
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

    private static void ClickCell(DataGridView grid, int column, int row)
    {
        Rectangle bounds = grid.GetCellDisplayRectangle(column, row, false);
        Assert(bounds.Width > 0 && bounds.Height > 0, "the requested result cell is visible in the test control");
        grid.CurrentCell = grid[column, row];
        // Hidden controls cannot receive ordinary mouse clicks. Exercise native
        // checkbox editing/commit and dispatch the real row-click event instead.
        if (column == 0)
        {
            Assert(grid.BeginEdit(false), "individual checkboxes remain editable after applying the theme");
            var editing = (IDataGridViewEditingCell)grid.CurrentCell;
            editing.EditingCellFormattedValue = editing.EditingCellFormattedValue is not true;
            grid.NotifyCurrentCellDirty(true);
        }
        else
            typeof(DataGridView).GetMethod("OnCellClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(grid, [new DataGridViewCellEventArgs(column, row)]);
    }

    private static void NetworkDialogTests()
    {
        using var uiContext = new WindowsFormsSynchronizationContext();
        NetworkSearchRequest? observed = null;
        var discovery = new ComputerDiscovery(
            directorySearch: (_, _) => throw new InvalidOperationException("Workgroup dialog must not query LDAP"),
            domainAvailable: _ => Task.FromResult(false),
            networkSearch: (request, progress, _) =>
            {
                observed = request;
                progress?.Report("Lokales Netzwerk: 2 / 2 Adressen geprüft · 2 Treffer");
                return Task.FromResult(new LdapSearchResult(true, [
                    new("PC01-RST-POS-01", "", "", "IP-Adresse: 192.0.2.1"),
                    new("PC02-RST-POS-02", "", "", "IP-Adresse: 192.0.2.2")
                ], "Lokales Netzwerk: 192.0.2.0/30", false, ""));
            });
        using var dialog = new LdapComputerSearchDialog(discovery);
        _ = dialog.Handle;
        var controls = All(dialog).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
        var source = (ComboBox)controls["searchSource"];
        var modes = (TabControl)controls["modes"];
        _ = modes.Handle; // Native selection events require a created tab control.
        var grid = (DataGridView)controls["results"];
        var import = (Button)controls["import"];
        Control content = dialog.Controls[0];
        using var canvas = new Panel { Size = dialog.ClientSize, Font = dialog.Font };
        canvas.Controls.Add(content);
        canvas.CreateControl();
        foreach (Control control in All(canvas)) _ = control.Handle;
        for (int pass = 0; pass < 5; pass++) PerformLayout(canvas);
        SynchronizationContext.SetSynchronizationContext(uiContext);
        Assert(source.SelectedIndex == 0 && observed is null, "opening computer discovery defaults to automatic without scanning");
        ((TextBox)controls["networkRange"]).Text = "192.0.2.0/30";
        ((TextBox)controls["pattern"]).Text = "PC*-RST-POS-*";
        Complete(dialog.SearchAsync());
        Application.DoEvents();
        Assert(observed?.Pattern == "PC*-RST-POS-*" && observed.Range == "192.0.2.0/30" && grid.Rows.Count == 2 && !import.Enabled,
            "automatic workgroup discovery uses the same pattern and requires explicit result selection");
        Assert(controls["searchStatus"].Text.Contains("2 Computer / Geräte gefunden") && controls["searchStatus"].Text.Contains("Lokales Netzwerk"),
            "queued progress cannot replace the completed local search summary");
        dialog.SetAllSelected(true);
        source.SelectedIndex = 1;
        Assert(!import.Enabled && !controls["networkRange"].Enabled && controls["domain"].Enabled, "changing the source invalidates old results and shows domain controls");
        source.SelectedIndex = 2;
        modes.SelectedIndex = 1;
        Assert(modes.SelectedIndex == 0 && !controls["domain"].Enabled && !controls["searchBase"].Enabled && controls["networkRange"].Enabled,
            $"local-only mode keeps name patterns and disables LDAP-specific controls: tab={modes.SelectedIndex}, domain={controls["domain"].Enabled}, base={controls["searchBase"].Enabled}, range={controls["networkRange"].Enabled}");
        Complete(dialog.SearchAsync());
        dialog.SetAllSelected(true);
        grid.Rows[1].Cells["include"].Value = false;
        Assert(dialog.GetSelectedHosts().SequenceEqual(new[] { "PC01-RST-POS-01" }), "a single network computer can be selected independently");
        ((TextBox)controls["networkRange"]).Text = "192.0.2.0/29";
        Assert(!import.Enabled, "editing the IP range invalidates the previous host selection");
        Complete(dialog.SearchAsync());
        dialog.SetAllSelected(true);
        grid.Rows[1].Cells["include"].Value = false;
        using (var bitmap = new Bitmap(canvas.Width, canvas.Height))
        {
            canvas.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "network-search-dialog.png"));
        }
        using var waiting = new LdapComputerSearchDialog(new ComputerDiscovery(domainAvailable: _ => Task.FromResult(false),
            directorySearch: (_, _) => throw new InvalidOperationException("Cancellation test must not query LDAP"),
            networkSearch: async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }));
        _ = waiting.Handle;
        var waitingControls = All(waiting).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
        SynchronizationContext.SetSynchronizationContext(uiContext);
        Task running = waiting.SearchAsync();
        Assert(!waitingControls["searchSource"].Enabled && !waitingControls["networkRange"].Enabled && waitingControls["stop"].Enabled,
            "running network discovery locks its inputs and enables cancellation");
        waiting.CancelSearch();
        Complete(running);
        Assert(waitingControls["searchStatus"].Text.Contains("abgebrochen") && waitingControls["searchSource"].Enabled && !waitingControls["import"].Enabled,
            "cancelling local discovery restores the dialog without selectable partial results");
        // Closing the last dialog uninstalls the WinForms synchronization context;
        // finish asynchronous UI tests before exercising the actual import/close.
        import.PerformClick();
        Assert(dialog.SelectedHosts.SequenceEqual(new[] { "PC01-RST-POS-01" }), "importing network results transfers only the checked hosts");
    }

    private static void SelectionInteractionTests()
    {
        foreach (float scale in new[] { 1F, 1.25F, 1.5F, 2F })
        {
            using var dialog = DomainDialog((_, _) => Task.FromResult(new LdapSearchResult(true, [
                new("A", "a.example.test", "Windows", "Test"),
                new("B", "b.example.test", "Windows", "Test"),
                new("C", "c.example.test", "Windows", "Test")
            ], "DC=example,DC=test", false, "")));
            dialog.Scale(new SizeF(scale, scale));
            dialog.Font = new Font("Segoe UI", 9.5F * scale);
            _ = dialog.Handle;
            var controls = All(dialog).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
            var grid = (DataGridView)controls["results"];
            var import = (Button)controls["import"];
            ((TextBox)controls["pattern"]).Text = "*";
            Control content = dialog.Controls[0];
            using var canvas = new Panel { Size = dialog.ClientSize, Font = dialog.Font };
            canvas.Controls.Add(content);
            canvas.CreateControl();
            foreach (Control control in All(canvas)) _ = control.Handle;
            for (int pass = 0; pass < 8; pass++) PerformLayout(canvas);
            Complete(dialog.SearchAsync());
            for (int pass = 0; pass < 3; pass++) PerformLayout(canvas);

            Rectangle checkbox = grid[0, 0].ContentBounds;
            Assert(!checkbox.IsEmpty, $"checkboxes remain visible after applying the theme at {scale * 100}%");
            ClickCell(grid, 0, 0);
            Assert(import.Enabled && dialog.GetSelectedHosts().SequenceEqual(new[] { "a.example.test" }),
                $"one checkbox click selects exactly one computer at {scale * 100}%");
            ClickCell(grid, 1, 2);
            Assert(dialog.GetSelectedHosts().SequenceEqual(new[] { "a.example.test", "c.example.test" }) && controls["selectedCount"].Text == "2 ausgewählt",
                $"clicking a different row adds that computer to the explicit selection at {scale * 100}%");
            var key = new KeyEventArgs(Keys.Space);
            typeof(DataGridView).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(grid, [key]);
            Assert(key.Handled && key.SuppressKeyPress && dialog.GetSelectedHosts().SequenceEqual(new[] { "a.example.test" }),
                "Space toggles the current text row without editing its computer name");
            typeof(DataGridView).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(grid, [new KeyEventArgs(Keys.Space)]);
            ClickCell(grid, 0, 0);
            Assert(dialog.GetSelectedHosts().SequenceEqual(new[] { "c.example.test" }),
                "clicking a selected checkbox again removes only that computer");
            ClickCell(grid, 1, 2);
            Assert(!import.Enabled && dialog.GetSelectedHosts().Length == 0,
                "removing the last selection disables importing");
            dialog.SetAllSelected(true);
            grid.EndEdit();
            Assert(dialog.GetSelectedHosts().Length == 3, "selecting all remains committed after editing an individual checkbox");
            dialog.SetAllSelected(false);
            ClickCell(grid, 1, 1);
            if (scale > 1F) ClickCell(grid, 1, 2);
            string[] expected = scale == 1F ? ["b.example.test"] : ["b.example.test", "c.example.test"];
            if (scale == 1F)
            {
                using var bitmap = new Bitmap(canvas.Width, canvas.Height);
                canvas.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "ldap-selection.png"));
            }
            import.PerformClick();
            Assert(dialog.DialogResult == DialogResult.OK && dialog.SelectedHosts.SequenceEqual(expected),
                "importing transfers exactly the single computer or chosen subset, never all unselected results");

            canvas.Controls.Remove(content);
            if (!dialog.IsDisposed) dialog.Controls.Add(content);
        }
    }

    private static void LayoutTests()
    {
        foreach (float scale in new[] { 1F, 1.25F, 1.5F, 2F })
        {
            using var dialog = DomainDialog((_, _) => throw new InvalidOperationException("Layout test must not query a domain"));
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
                    string directory = TestWorkspace.Artifacts;
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
