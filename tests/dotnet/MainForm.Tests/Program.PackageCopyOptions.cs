using NovaTouchUpdater;

internal static partial class Program
{
    private static void PackageCopyOptionsTests()
    {
        var worker = new FakeRunner();
        using var form = new MainForm(preview: true, worker, new FakeRunner());
        Populate(form);
        Field<ComboBox>(form, "executionMode").SelectedIndex = 1;
        HostState local = Targets(form).Single(h => h.IsLocal), remote = Targets(form).First(h => !h.IsLocal);
        var localOption = Field<CheckBox>(form, "chkSkipLocalPackageCopy");
        var remoteOption = Field<CheckBox>(form, "chkSkipZipTransfer");
        var remoteDirectOption = Field<CheckBox>(form, "chkSkipRemotePackageCopy");
        var destination = Field<TextBox>(form, "txtRemotePath");
        var settings = (TableLayoutPanel)localOption.Parent!;
        Assert(localOption.Text == "Installationspakete nicht Lokal ablegen" &&
            remoteOption.Text == "Kein upload, vorhandene Update-Dateien auf dem Zielrechner verwenden" &&
            remoteDirectOption.Text == "Paketdateien direkt verwenden - keine zusätzliche Kopie" &&
            settings.GetRow(localOption) < settings.GetRow(remoteDirectOption) && settings.GetRow(remoteDirectOption) < settings.GetRow(remoteOption),
            "the new remote direct-package switch appears above reuse of existing remote packages");
        destination.Text = "";
        Call(form, "CaptureCredentials");
        var localRequest = (WorkerRequest)Call(form, "Request", "update", local, UpdateComponents.Payment)!;
        var remoteRequest = (WorkerRequest)Call(form, "Request", "update", remote, UpdateComponents.Payment)!;
        Assert((bool)Call(form, "IsUpdatePathValid", (object)new[] { local })! && (bool)Call(form, "IsUpdatePathValid", (object)new[] { local, remote })! &&
            localRequest.RemoteInstallPath == "" && localRequest.SkipLocalPackageCopy && remoteRequest.RemoteInstallPath == @"C:\install\Novacom" && !remoteRequest.SkipLocalPackageCopy,
            "an empty destination uses local sources directly and the default destination for remote updates");
        destination.Text = "relative-invalid-path";
        Assert(!(bool)Call(form, "IsUpdatePathValid", (object)new[] { local })!, "local copying requires a valid nonempty destination");
        localOption.Checked = true;
        Assert((bool)Call(form, "IsUpdatePathValid", (object)new[] { local })! && !(bool)Call(form, "IsUpdatePathValid", (object)new[] { local, remote })!,
            "direct local use ignores an unused destination while remote updates still validate theirs");
        destination.Text = @"D:\update destination";
        foreach (bool skipLocal in new[] { false, true })
        foreach (bool skipRemote in new[] { false, true })
        foreach (bool directRemote in new[] { false, true })
        {
            localOption.Checked = skipLocal; remoteOption.Checked = skipRemote; remoteDirectOption.Checked = directRemote;
            Call(form, "CaptureCredentials");
            localRequest = (WorkerRequest)Call(form, "Request", "update", local, UpdateComponents.Payment)!;
            remoteRequest = (WorkerRequest)Call(form, "Request", "update", remote, UpdateComponents.Payment)!;
            Assert(localRequest.SkipLocalPackageCopy == skipLocal && !localRequest.SkipZipTransfer &&
                remoteRequest.SkipZipTransfer == skipRemote && !remoteRequest.SkipLocalPackageCopy &&
                remoteRequest.SkipRemotePackageCopy == directRemote && !localRequest.SkipRemotePackageCopy &&
                localRequest.RemoteInstallPath == destination.Text && remoteRequest.RemoteInstallPath == destination.Text,
                $"copy options remain scoped to their hosts (local={skipLocal}, existingRemote={skipRemote}, directRemote={directRemote})");
        }
        localOption.Checked = false; remoteOption.Checked = false; remoteDirectOption.Checked = true;
        destination.Text = "unused-invalid-destination";
        Call(form, "CaptureCredentials");
        Assert((bool)Call(form, "IsUpdatePathValid", (object)new[] { remote })! && !(bool)Call(form, "IsUpdatePathValid", (object)new[] { local, remote })!,
            "temporary remote packages ignore the unused target while local copying still requires a valid target");
        worker.Requests.Clear();
        var preparation = (Task<HostState[]?>)Call(form, "PrepareUploadDirectoriesAsync", new[] { remote }, true,
            (Action<HostState, string>)((_, error) => throw new InvalidOperationException(error)), CancellationToken.None)!;
        Complete(preparation);
        Assert(preparation.Result!.SequenceEqual(new[] { remote }) && worker.Requests.IsEmpty,
            "direct remote updates skip target-folder checks and cleanup dialogs entirely");
        Assert(Field<bool>(form, "activeStreamDatabasePackages"), "direct remote packages also stream database MSIs without a target copy");
        string path = Path.Combine(TestWorkspace.Artifacts, "copy-options-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            foreach (bool enabled in new[] { true, false })
            {
                destination.Text = ""; localOption.Checked = remoteOption.Checked = remoteDirectOption.Checked = enabled;
                AppConfigStore.Save(path, (UpdaterConfig)Call(form, "CaptureConfig")!, "");
                using var restored = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
                Call(restored, "ApplyLoadedConfig", AppConfigStore.Load(path));
                Assert(Field<CheckBox>(restored, "chkSkipLocalPackageCopy").Checked == enabled &&
                    Field<CheckBox>(restored, "chkSkipZipTransfer").Checked == enabled && Field<TextBox>(restored, "txtRemotePath").Text == "",
                    $"both switches and the empty local destination survive actual form capture, save and startup apply ({enabled})");
                Assert(Field<CheckBox>(restored, "chkSkipRemotePackageCopy").Checked == enabled,
                    $"the new remote direct-package option survives config save and load ({enabled})");
            }
        }
        finally { File.Delete(path); }
    }
}
