using NovaTouchUpdater;

internal static partial class Program
{
    private static void HostManagementTests()
    {
        var worker = new FakeRunner();
        var monitor = new FakeRunner();
        using var form = new MainForm(preview: true, worker, monitor);
        _ = form.Handle;
        var table = Field<DataGridView>(form, "table");
        Assert(Targets(form) is [{ IsLocal: true }] && table.Rows.Count == 1 &&
            table.Rows[0].Cells["hostname"].Value is "LOKAL" && Field<Button>(form, "btnScan").Enabled,
            "an empty initial host list shows LOKAL and enables local checking in Remote mode");
        Assert(table.Rows[0].Cells["include"].ReadOnly && table.Rows[0].Cells["include"].Value is true,
            "the local fallback stays checked while no remote host is selected");
        Assert(All(form).OfType<Button>().Any(button => button.Text == "Host bearbeiten") &&
            !All(form).OfType<Button>().Any(button => button.Text is "Entfernen" or "Host hinzufügen"),
            "remote-host management offers Host bearbeiten without an Entfernen button");
        HostState[] remote = Populate(form);
        var (dialog, input) = ((Form, TextBox))Call(form, "CreateHostEditor")!;
        using (dialog)
        {
            Assert(input.Lines.SequenceEqual(remote.Select(h => h.Host)), "the editor includes every remote host and omits the local placeholder");
            input.Clear();
            typeof(Button).GetMethod("OnClick", Private)!.Invoke(dialog.AcceptButton, [EventArgs.Empty]);
            Assert(dialog.DialogResult == DialogResult.OK, "the real editor accepts a completely empty list");
        }
        Field<TextBox>(form, "txtUser").Text = "unused-remote-user";
        Field<TextBox>(form, "txtPassword").Clear();
        Call(form, "ApplyRemoteHosts", Array.Empty<string>(), "Testliste");
        Assert(Targets(form) is [{ IsLocal: true }] && table.Rows.Count == 1 &&
            table.Rows[0].Cells["hostname"].Value is "LOKAL" &&
            ((UpdaterConfig)Call(form, "CaptureConfig")!).Hosts is { Length: 0 },
            "deleting every remote host keeps only the local target and captures an empty remote config");
        Complete((Task)Call(form, "RunScansAsync")!);
        Assert(worker.Requests.Count == 2 && worker.Requests.All(r => r.LocalExecution && r.Host == "LOCALHOST" &&
            r.Action is "probe" or "scan" && r.Username.Length == 0 && r.Password.Length == 0) && monitor.Requests.IsEmpty,
            "the empty-list fallback checks connection and versions directly without remote credentials or WinRM");
        Assert(Targets(form) is [{ Scanned: true }] && Field<Button>(form, "btnUpdate").Enabled &&
            !Field<Button>(form, "btnWinRm").Enabled && !Field<Button>(form, "btnTransfer").Enabled && !Field<Button>(form, "btnSendCommand").Enabled,
            "a successful local check prepares local updates while remote-only actions remain disabled");
        string confirmation = (string)Call(form, "UpdateConfirmation", Targets(form), UpdateComponents.None, false)!;
        Assert(confirmation.StartsWith("Ausführung: Lokal") && confirmation.Contains("Ziele: LOKAL"),
            "the update confirmation identifies the local fallback clearly");
        string path = Path.Combine(TestWorkspace.Artifacts, "empty-hosts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AppConfigStore.Save(path, (UpdaterConfig)Call(form, "CaptureConfig")!, "");
            using var restored = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            Populate(restored); // Applying an empty config must replace an existing list too.
            Call(restored, "ApplyLoadedConfig", AppConfigStore.Load(path));
            Assert(Field<List<HostState>>(restored, "hosts") is [{ IsLocal: true }] &&
                Targets(restored) is [{ IsLocal: true }], "an empty saved config reloads as LOKAL and removes stale remote hosts");
        }
        finally { File.Delete(path); }
        HostState local = Targets(form).Single();
        Call(form, "ApplyRemoteHosts", new[] { "REPLACEMENT.invalid" }, "Testliste");
        Assert(Targets(form) is [{ IsLocal: false, Host: "REPLACEMENT.invalid" }] && table.Rows.Count == 1,
            "adding a remote host restores remote execution and hides the local fallback");
        Field<ComboBox>(form, "executionMode").SelectedIndex = 1;
        Assert(!local.Row!.Cells["include"].ReadOnly, "combined mode permits changing the local checkbox when a remote host is selected");
        EditSelection(form, local, false);
        Assert(Targets(form) is [{ IsLocal: false }], "combined mode can run only the remote selection when LOKAL is unchecked");
        EditSelection(form, Targets(form).Single(), false);
        Assert(Targets(form) is [{ IsLocal: true }] && local.Row.Cells["include"].ReadOnly,
            "unchecking the final remote in combined mode restores and checks LOKAL");
        Call(form, "ApplyRemoteHosts", Array.Empty<string>(), "Testliste");
        Assert(ReferenceEquals(local, Targets(form).Single()) && local.Scanned,
            "returning to the local fallback preserves its previously read versions");
    }
}
