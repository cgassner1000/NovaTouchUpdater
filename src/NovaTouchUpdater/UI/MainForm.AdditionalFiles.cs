namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly List<string> additionalFiles = [];
    private readonly TableLayoutPanel additionalFilesTable = new()
    {
        Name = "additionalFilesTable", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0), Visible = false
    };
    private TransferFileRequest[] activeAdditionalFiles = [];
    private string activeAdditionalDestination = "";
    private Func<string, bool>? confirmUpdate = null;

    private void AddAdditionalFiles(IEnumerable<string> paths)
    {
        bool changed = false;
        foreach (string path in paths.Select(Path.GetFullPath))
            if (!additionalFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) { additionalFiles.Add(path); changed = true; }
        if (!changed) return;
        ResetAdditionalFileSelection();
    }

    private void RemoveAdditionalFile(string path)
    {
        if (!CanDropPackages || !additionalFiles.Remove(path)) return;
        ResetAdditionalFileSelection();
    }

    private void ResetAdditionalFileSelection()
    {
        preparedPackageHashes = null;
        foreach (HostState host in hosts) host.Updates?.ResetAdditionalFiles();
        RefreshSelectedPackages();
        UpdateButtons();
    }

    private void RefreshAdditionalFiles()
    {
        additionalFilesTable.SuspendLayout();
        try
        {
            foreach (Control control in additionalFilesTable.Controls.Cast<Control>().ToArray()) control.Dispose();
            additionalFilesTable.ColumnStyles.Clear(); additionalFilesTable.RowStyles.Clear();
            additionalFilesTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            additionalFilesTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            additionalFilesTable.RowCount = 0;
            if (additionalFiles.Count > 0)
            {
                var title = new Label { Text = "Zusätzliche Dateien", AutoSize = true, Dock = DockStyle.Fill,
                    Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 2, 0, 6) };
                additionalFilesTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                additionalFilesTable.Controls.Add(title, 0, additionalFilesTable.RowCount++);
                additionalFilesTable.SetColumnSpan(title, 2);
                foreach (string path in additionalFiles)
                {
                    int row = additionalFilesTable.RowCount++;
                    additionalFilesTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    var name = new Label { Text = Path.GetFileName(path), AutoEllipsis = true, Dock = DockStyle.Fill,
                        Height = Font.Height + 4, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 0, 2), AccessibleDescription = path };
                    updateToolTip.SetToolTip(name, path + "\nWird nur in den Zielpfad kopiert, nicht ausgeführt.");
                    additionalFilesTable.Controls.Add(name, 0, row);
                    var remove = CreateSelectionRemoveButton("Zusätzliche Datei " + Path.GetFileName(path) + " aus der Auswahl entfernen", () => RemoveAdditionalFile(path));
                    remove.Name = "removeAdditionalFile"; remove.Tag = path;
                    remove.Compact = true; remove.AutoSize = false; remove.Padding = Padding.Empty;
                    remove.Size = Size.Round(new SizeF(22 * DeviceDpi / 96F, 12 * DeviceDpi / 96F));
                    remove.Margin = new Padding(6, 2, 0, 2);
                    additionalFilesTable.Controls.Add(remove, 1, row);
                }
            }
            additionalFilesTable.Visible = additionalFiles.Count > 0;
        }
        finally { additionalFilesTable.ResumeLayout(true); }
    }

    private bool NeedsAdditionalFiles(HostState host, bool onlyFailed) => additionalFiles.Count > 0 &&
        (!onlyFailed || host.Updates?.AdditionalFilesCompleted != true);

    private async Task PrepareAdditionalFilesAsync(UpdateComponents components, CancellationToken ct)
    {
        activeAdditionalFiles = await PrepareTransferFilesAsync(activeAdditionalFiles, ct);
        // Detect conflicts with package/CAB filenames before any host is changed.
        if (activeAdditionalFiles.Length > 0)
            await PrepareTransferFilesAsync(PackageFileCandidates(components).Concat(activeAdditionalFiles), ct);
    }

    private void CaptureAdditionalFiles(bool needed) => activeAdditionalFiles = needed
        ? additionalFiles.Select(path => new TransferFileRequest { LocalPath = path, RelativePath = Path.GetFileName(path) }).ToArray() : [];

    private async Task UploadAdditionalFilesAsync(HostState host, CancellationToken ct)
    {
        if (activeAdditionalFiles.Length == 0 || host.Updates?.AdditionalFilesCompleted == true) return;
        HostUpdateState state = host.Updates ?? throw new InvalidOperationException("Update wurde nicht vorbereitet.");
        try
        {
            ct.ThrowIfCancellationRequested();
            Mark(host, h => { h.Phase = "Zusätzliche Dateien werden übertragen"; state.AdditionalFilesError = ""; });
            var request = new WorkerRequest
            {
                Action = host.IsLocal ? "stage-packages" : "transfer", Host = host.Host, LocalExecution = host.IsLocal,
                Username = host.IsLocal ? "" : activeUser, Password = host.IsLocal ? "" : activePassword,
                UseSsl = !host.IsLocal && activeSsl, HttpsPort = activeHttpsPort,
                RemoteInstallPath = activeAdditionalDestination, TransferFiles = activeAdditionalFiles
            };
            WorkerResult result = await runner.RunAsync(request, ev =>
            {
                Log(host.Host, "Zusätzliche Dateien · " + ev.Phase + ": " + ev.Message);
                Mark(host, h => h.ApplyProgress(ev));
            }, ct);
            if (!result.Success) throw new IOException(FriendlyError(result));
            state.CompleteAdditionalFiles(result.Changed, $"{activeAdditionalFiles.Length} zusätzliche Datei(en) im Zielpfad geprüft");
            Log(host.Host, "ZUSÄTZLICHE DATEIEN => OK " + result.Message);
        }
        catch (OperationCanceledException)
        {
            state.AdditionalFilesError = "Zusätzliche Dateien: Übertragung abgebrochen.";
            Mark(host, h => { h.Result = "Abgebrochen"; h.UpdateFailed = true; h.Phase = state.AdditionalFilesError; });
            throw;
        }
        catch (Exception ex)
        {
            state.AdditionalFilesError = "Zusätzliche Dateien: " + ex.Message;
            Log(host.Host, "ZUSÄTZLICHE DATEIEN => FEHLER " + ex.Message);
        }
    }
}
