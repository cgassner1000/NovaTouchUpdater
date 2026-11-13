using NovaTouchUpdater;

internal static partial class Program
{
    private static void ProgramUpdateTests()
    {
        var worker = new UpdateRunner();
        using (var form = UpdateForm(worker))
        {
            HostState[] hosts = Populate(form);
            HostState first = hosts[0], second = hosts[1];
            first.Updates = new(UpdateComponents.ZipServices);
            second.Updates = new(UpdateComponents.Payment);
            var table = Field<DataGridView>(form, "table");
            table.Dock = DockStyle.None; table.Size = new Size(1600, 340);
            worker.Progress = (request, report) =>
            {
                HostState host = hosts.Single(host => host.Host == request.Host);
                string column = worker.Kind(request) switch { UpdateComponents.Payment => "payment", UpdateComponents.Fiscal => "fiscal", _ => "serversuite" };
                Call(form, "PaintRow", host);
                Assert(host.Row!.Cells[column].Value is "Update …" && host.Row.Cells[column].Tag is ComponentUpdateProgress { Status: ComponentUpdateStatus.Updating, Percent: null },
                    "an active component replaces only its own version and clears any earlier failure");
                report(new() { Phase = "Dateien kopieren", Message = "Synthetic files", ProgressPercent = 45 });
                Call(form, "PaintRow", host);
                Assert(host.Row.Cells[column].Tag is ComponentUpdateProgress { Percent: 45 } &&
                    host.Row.Cells["information"].Value!.ToString()!.StartsWith("45 % · Dateien kopieren") && host.Row.Cells[column].ToolTipText.Contains("45 %"),
                    "real worker percentages drive the component bar while the information column keeps its existing detail");
                if (request.Host == first.Host && column == "fiscal")
                {
                    Assert(first.Row!.Cells["payment"].Value is "Fehlgeschlagen" && second.Row!.Cells["payment"].Tag is null,
                        "a later program cannot erase another program's failure or color another host");
                    DrawProgramTable(form, "running");
                }
                report(new() { Phase = "Dienst starten", Message = "Synthetic service" });
                Call(form, "PaintRow", host);
                Assert(host.Row.Cells[column].Tag is ComponentUpdateProgress { Percent: null }, "steps without measured progress never retain a misleading completed bar");
            };
            worker.Outcome = request => worker.Kind(request) == UpdateComponents.Payment
                ? new() { Success = false, Error = "Synthetic payment failure", AutomaticRetriesExhausted = true } : worker.Success(request);
            Update(form, first);
            Assert(first.Row!.Cells["payment"].Value is "Fehlgeschlagen" && first.Row.Cells["payment"].Tag is ComponentUpdateProgress { Status: ComponentUpdateStatus.Failed } &&
                first.Row.Cells["fiscal"].Value is "2.0.0" && first.Row.Cells["fiscal"].Tag is ComponentUpdateProgress { Status: ComponentUpdateStatus.Succeeded, Percent: 100 },
                "failed and verified programs retain separate final states and verified versions");
            worker.Outcome = worker.Success;
            Update(form, second);
            Assert(first.Row.Cells["payment"].Value is "Fehlgeschlagen" && second.Row!.Cells["payment"].Value is "2.0.0",
                "the same program can fail on one host and succeed on another");
            DrawProgramTable(form, "finished");
            Update(form, first);
            Assert(first.Row.Cells["payment"].Value is "2.0.0" && first.Row.Cells["payment"].Tag is ComponentUpdateProgress { Status: ComponentUpdateStatus.Succeeded } && !first.CanRetryUpdate,
                "a successful retry replaces the red error with the verified version and green success");
        }
        var msi = new ProgramMsiRunner();
        using (var form = new MainForm(preview: true, msi, new FakeRunner()))
        {
            _ = form.Handle;
            Field<ComboBox>(form, "executionMode").SelectedIndex = (int)UpdateExecutionMode.Local;
            Call(form, "CaptureCredentials");
            HostState host = Targets(form).Single(); host.Scanned = true;
            foreach (var (component, kind, column) in new[] { (UpdateComponents.Pos, "pos", "pos"), (UpdateComponents.Ntc, "ntc", "ntc"), (UpdateComponents.Ntv, "ntv", "ntv") })
            {
                Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages")[component] = new() { Kind = kind, ProductVersion = "2.0.0", PayloadPath = "synthetic.msi" };
                host.Updates = new(component);
                msi.Check = () =>
                {
                    Call(form, "PaintRow", host);
                    Assert(host.Row!.Cells[column].Value is "Update …" && host.Row.Cells[column].Tag is ComponentUpdateProgress { Percent: 60 },
                        kind + " routes its installation progress into its own program column");
                };
                Update(form, host);
                Assert(host.Row!.Cells[column].Tag is ComponentUpdateProgress { Status: ComponentUpdateStatus.Succeeded }, kind + " shows verified success");
            }
            host.Updates = new(UpdateComponents.Ntc);
            host.Updates.BeginComponent(UpdateComponents.Ntc);
            host.FinishRun(cancelled: true);
            Call(form, "PaintRow", host);
            Assert(host.Row!.Cells["ntc"].Value is "Abgebrochen" && host.Row.Cells["ntc"].Tag is ComponentUpdateProgress { Status: ComponentUpdateStatus.Cancelled },
                "cancellation cannot leave a program stuck in Update status");
        }
    }

    private static void DrawProgramTable(MainForm form, string phase)
    {
        var table = Field<DataGridView>(form, "table");
        foreach (string mode in new[] { "Light", "Dark" })
        {
            UiTheme.SetMode(mode);
            for (int pass = 0; pass < 3; pass++) Layout(table);
            Call(form, "ApplyColumnWidths");
            using var bitmap = new Bitmap(table.Width, table.Height);
            table.DrawToBitmap(bitmap, table.ClientRectangle);
            bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "program-updates-" + phase + "-" + mode.ToLowerInvariant() + ".png"));
        }
        UiTheme.SetMode("System");
    }

    private sealed class ProgramMsiRunner : IWorkerRunner
    {
        internal Action? Check;
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            progress(new() { Phase = "MSI installieren", Message = "Synthetic installation", ProgressPercent = 60 });
            Check?.Invoke();
            return Task.FromResult(new WorkerResult { Success = true, Changed = true, PosVersion = "2.0.0", NtcVersion = "2.0.0",
                InstalledPrograms = [new() { Kind = "ntv", Version = "2.0.0", DisplayName = "NovaTouch Voucher" }] });
        }
        public void Dispose() { }
    }
}
