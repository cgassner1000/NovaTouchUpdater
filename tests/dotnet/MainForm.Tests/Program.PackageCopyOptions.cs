using NovaTouchUpdater;

internal static partial class Program
{
    private static void PackageCopyOptionsTests()
    {
        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        Populate(form);
        Field<ComboBox>(form, "executionMode").SelectedIndex = 1;
        HostState local = Targets(form).Single(h => h.IsLocal), remote = Targets(form).First(h => !h.IsLocal);
        var localOption = Field<CheckBox>(form, "chkSkipLocalPackageCopy");
        var remoteOption = Field<CheckBox>(form, "chkSkipZipTransfer");
        var destination = Field<TextBox>(form, "txtRemotePath");
        var settings = (TableLayoutPanel)localOption.Parent!;
        Assert(localOption.Text == "LOKAL - Keine Kopie in Update-Datein Zielpfad" &&
            remoteOption.Text == "REMOTE - Lokale Datei am Client verwenden ohne upload" &&
            settings.GetRow(localOption) < settings.GetRow(remoteOption), "local and remote copy switches have their requested captions and order");
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
        {
            localOption.Checked = skipLocal; remoteOption.Checked = skipRemote;
            Call(form, "CaptureCredentials");
            localRequest = (WorkerRequest)Call(form, "Request", "update", local, UpdateComponents.Payment)!;
            remoteRequest = (WorkerRequest)Call(form, "Request", "update", remote, UpdateComponents.Payment)!;
            Assert(localRequest.SkipLocalPackageCopy == skipLocal && !localRequest.SkipZipTransfer &&
                remoteRequest.SkipZipTransfer == skipRemote && !remoteRequest.SkipLocalPackageCopy &&
                localRequest.RemoteInstallPath == destination.Text && remoteRequest.RemoteInstallPath == destination.Text,
                $"copy options remain independent in combined mode (local={skipLocal}, remote={skipRemote})");
        }
        string path = Path.Combine(TestWorkspace.Artifacts, "copy-options-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            foreach (bool enabled in new[] { true, false })
            {
                destination.Text = ""; localOption.Checked = remoteOption.Checked = enabled;
                AppConfigStore.Save(path, (UpdaterConfig)Call(form, "CaptureConfig")!, "");
                using var restored = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
                Call(restored, "ApplyLoadedConfig", AppConfigStore.Load(path));
                Assert(Field<CheckBox>(restored, "chkSkipLocalPackageCopy").Checked == enabled &&
                    Field<CheckBox>(restored, "chkSkipZipTransfer").Checked == enabled && Field<TextBox>(restored, "txtRemotePath").Text == "",
                    $"both switches and the empty local destination survive actual form capture, save and startup apply ({enabled})");
            }
        }
        finally { File.Delete(path); }
    }
}
