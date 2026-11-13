using System.Text.Json;
using System.Text.Json.Nodes;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void ColumnSelectionTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "column-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(root);
        paths.InitializeAndMigrate(Path.Combine(root, "exe"));
        try
        {
            AppConfigStore.Save(paths.ConfigFile, new UpdaterConfig { Hosts = ["SAVED.invalid"], Username = "saved-user", ScanOnStartup = false }, "Synthetic saved password");
            JsonObject stored = JsonNode.Parse(File.ReadAllText(paths.ConfigFile))!.AsObject();
            string credential = stored["EncryptedPassword"]!.GetValue<string>();
            stored["FutureSetting"] = "preserve";
            File.WriteAllText(paths.ConfigFile, stored.ToJsonString());
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner(), dataPaths: paths);
            _ = form.Handle;
            Call(form, "LoadStartupConfig");
            HostState host = Populate(form)[0];
            Field<TextBox>(form, "txtUser").Text = "auto-saved-user";
            Field<TextBox>(form, "txtPassword").Text = "auto-saved-password";
            Assert((bool)Call(form, "FlushConfigChanges")!, "edited credentials are automatically persisted before column changes");
            credential = JsonNode.Parse(File.ReadAllText(paths.ConfigFile))!["EncryptedPassword"]!.GetValue<string>();
            var table = Field<DataGridView>(form, "table");
            Control parent = table.Parent!;
            using var canvas = new Panel { Size = new Size(2200, 650), Font = form.Font };
            canvas.Controls.Add(table); canvas.CreateControl(); _ = table.Handle;
            try
            {
                string[] Visible() => table.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).OrderBy(column => column.DisplayIndex).Select(column => column.Name).ToArray();
                void Settle() { canvas.PerformLayout(); Application.DoEvents(); Call(form, "ApplyColumnWidths"); Application.DoEvents(); }
                ContextMenuStrip menu = table.ContextMenuStrip!;
                ToolStripMenuItem Choice(string name) => (ToolStripMenuItem)menu.Items[name]!;
                Settle();
                Assert(Visible().SequenceEqual(HostColumnSettings.Defaults) && !table.Columns["ksm"].Visible && !table.Columns["ntv"].Visible &&
                    menu.Items["include"] is null && menu.Items.OfType<ToolStripMenuItem>().Count(item => item.Tag is string) == table.Columns.Count - 1,
                    "defaults retain the original view and the fixed selection column has no context-menu choice");
                Choice("dotnet48").PerformClick();
                Choice("ksm").PerformClick();
                Choice("ntv").PerformClick();
                Settle();
                Assert(!table.Columns["dotnet48"].Visible && table.Columns["ksm"].Visible && table.Columns["ntv"].Visible &&
                    AppConfigStore.Load(paths.ConfigFile).Settings.VisibleHostColumns!.SequenceEqual(Visible()),
                    "right-click choices immediately save hidden .NET and visible optional columns without the configuration button");
                using (var document = JsonDocument.Parse(File.ReadAllText(paths.ConfigFile)))
                    Assert(document.RootElement.GetProperty("EncryptedPassword").GetString() == credential &&
                        document.RootElement.GetProperty("Username").GetString() == "auto-saved-user" && document.RootElement.GetProperty("Hosts")[0].GetString() == "SAVED.invalid" &&
                        document.RootElement.GetProperty("FutureSetting").GetString() == "preserve",
                        "column clicks retain automatically saved credentials, hosts and unknown settings");
                var inventory = new WorkerResult { Success = true, PosVersion = "1.2.3", NtcVersion = "1.2.3",
                    Services = [new ServiceInfo { Kind = "payment", ProductVersion = "2026.4.9785.14868" },
                        new ServiceInfo { Kind = "serversuite", Name = "nt3_server_suite_USER", ProductVersion = "2026.4.9809.25670",
                            KsmVersion = "2026.4.9785.14868", KsmInstallDir = @"C:\Suite\apps\nt3-kitchen-display" }],
                    InstalledPrograms = [new InstalledProgramInfo { Kind = "ntv", DisplayName = "NovaTouch Voucher", Version = "2026.4.9785.14868" }] };
                Call(form, "ReadScanInventory", host, JsonSerializer.Deserialize<WorkerResult>(JsonSerializer.Serialize(inventory))!);
                Call(form, "PaintRow", host); Settle();
                Assert(host.NtvVersion == "2026.4.9785.14868" && host.KsmVersion == "2026.4.9785.14868" &&
                    host.Row!.Cells["ntv"].Value?.ToString() == host.NtvVersion && host.Row.Cells["ksm"].Value?.ToString() == host.KsmVersion &&
                    host.Row.Cells["ksm"].ToolTipText.Contains("nt3_server_suite_USER") && host.Row.Cells["ntv"].ToolTipText.Contains("NovaTouch Voucher"),
                    "optional columns show complete KSM metadata and Windows Voucher versions with instance/product details");
                Assert(table.Columns["ksm"].Width >= table.Columns["payment"].Width &&
                    table.Columns["ksm"].Width >= table.Columns["ksm"].GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, true) &&
                    table.Columns["ntv"].Width >= table.Columns["ntv"].GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, true),
                    "ServiceDisplay uses the same content sizing as Payment while both optional columns fit their complete headings");
                int shortKsm = table.Columns["ksm"].Width;
                host.Row.Cells["ksm"].Value = "2026.4.9785.14868, 2026.4.9800.12345"; Settle();
                Assert(table.Columns["ksm"].Width > shortKsm && table.HorizontalScrollingOffset == 0, "multiple KSM versions grow dynamically without horizontal scrolling");

                Call(form, "ReadScanInventory", host, new WorkerResult { Success = true, PosVersion = "Nicht installiert", NtcVersion = "Nicht installiert" });
                Assert(host.NtvVersion == "Nicht installiert", "missing Voucher entries are shown as not installed after a completed software inventory");
                Call(form, "ReadScanInventory", host, new WorkerResult());
                Assert(host.NtvVersion == "–", "an unavailable software inventory cannot claim that Voucher is uninstalled");
                host.Services = [new ServiceInfo { Kind = "serversuite", KsmVersion = "Nicht installiert" },
                    new ServiceInfo { Kind = "serversuite", Name = "nt3_server_suite_A", KsmVersion = "2026.4.9785.14868" },
                    new ServiceInfo { Kind = "serversuite", Name = "nt3_server_suite_B", KsmVersion = "2026.4.9800.12345" }];
                Call(form, "ReadVersions", host);
                Assert(host.KsmVersion == "2026.4.9785.14868, 2026.4.9800.12345", "different KSM installation versions remain visible without mixing absent instances into the version list");
                Call(form, "ReadScanInventory", host, inventory);
                Call(form, "PaintRow", host);

                foreach (string name in new[] { "hostname", "information", "result" }) Choice(name).PerformClick();
                Settle();
                Assert(table.Columns["include"].Visible && new[] { "hostname", "information", "result" }.All(name => !table.Columns[name].Visible) &&
                    table.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).All(column => column.Width >= 2),
                    "data columns can be hidden while the selection column remains visible and dynamic sizing stays valid");
                foreach (string name in Visible().Where(name => name != "include")) Choice(name).PerformClick();
                Settle();
                Assert(Visible().SequenceEqual(["include"]) && AppConfigStore.Load(paths.ConfigFile).Settings.VisibleHostColumns!.SequenceEqual(["include"]) &&
                    table.Columns["include"].Width == (int)Math.Ceiling(26 * table.DeviceDpi / 96F),
                    "hiding every data column persists while the fixed checkbox column keeps its narrow width");
                Point contextPoint = table.PointToScreen(new Point(8, 8));
                var contextMessage = Message.Create(table.Handle, 0x007B, table.Handle,
                    new IntPtr((contextPoint.X & 0xffff) | ((contextPoint.Y & 0xffff) << 16)));
                typeof(DataGridView).GetMethod("WndProc", Private)!.Invoke(table, [contextMessage]);
                Application.DoEvents();
                Assert(menu.Visible && Choice("restoreDefaultHostColumns").Enabled,
                    "the context menu still exposes recovery when every data column is hidden");
                menu.Close();
                // Old configurations may have hidden the checkbox column too.
                AppConfigStore.SaveVisibleHostColumns(paths.ConfigFile, []);
                using (var restored = new MainForm(preview: true, new FakeRunner(), new FakeRunner(), dataPaths: paths))
                {
                    Call(restored, "LoadStartupConfig");
                    Assert(Field<DataGridView>(restored, "table").Columns.Cast<DataGridViewColumn>().All(column => column.Visible == (column.Name == "include")),
                        "loading old preferences restores the fixed selection column without exposing hidden data columns");
                }
                Choice("restoreDefaultHostColumns").PerformClick(); Settle();
                Assert(Visible().SequenceEqual(HostColumnSettings.Defaults), "the context-menu reset restores the original view including .NET and persists it");
                string? error = null;
                SetField(form, "showHostColumnSaveError", new Action<string>(message => error = message));
                byte[] original = File.ReadAllBytes(paths.ConfigFile);
                using (File.Open(paths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read)) Choice("dotnet48").PerformClick();
                Assert(error is not null && !table.Columns["dotnet48"].Visible && File.ReadAllBytes(paths.ConfigFile).SequenceEqual(original),
                    "an unavailable config retains the current-session choice, preserves stored settings and reports persistence failure");
                Choice("restoreDefaultHostColumns").PerformClick();
                Choice("ksm").PerformClick(); Choice("ntv").PerformClick();
                Call(form, "PaintRow", host); Settle();
                using var bitmap = new Bitmap(canvas.Width, canvas.Height);
                canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
                bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "host-optional-columns.png"));
                menu.Show(canvas, new Point(20, 20)); Application.DoEvents();
                using var menuBitmap = new Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(menuBitmap, menu.ClientRectangle);
                menuBitmap.Save(Path.Combine(TestWorkspace.Artifacts, "host-column-menu.png"));
                menu.Close();
            }
            finally { canvas.Controls.Remove(table); parent.Controls.Add(table); }
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Column fixtures outside test artifacts.");
            Directory.Delete(root, recursive: true);
        }
    }
}
