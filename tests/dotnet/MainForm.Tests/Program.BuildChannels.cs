using NovaTouchUpdater;

internal static partial class Program
{
    private static string buildChannelFixture = "";

    private static async Task BuildChannelCatalogTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "build-channels-" + Guid.NewGuid().ToString("N"));
        string builds = buildChannelFixture = Path.Combine(root, "BuIlDs");
        foreach (var (channel, version) in new[] { ("alpha", "9.0.0"), ("BETA", "5.0.0"), ("stable", "2.0.0") })
        {
            foreach (string product in new[] { "Payment", "Fiscal", "Suite", "Pos", "Ntc", "Sol", "Ntv" })
            {
                string folder = Path.Combine(builds, product, channel, "releases", "current");
                Directory.CreateDirectory(folder);
                string? zip = product switch
                {
                    "Payment" => "nt.payment_" + version + ".zip",
                    "Fiscal" => "nt.fiscal_" + version + ".zip",
                    "Suite" => "nt.server.suite-" + version + ".zip",
                    _ => null
                };
                if (zip is not null) File.WriteAllText(Path.Combine(folder, zip), "fixture, never installed");
                else MsiFixture.Create(Path.Combine(folder, product + ".msi"), product switch
                {
                    "Pos" => "NovaTouch POS", "Ntc" => "NovaTouch Control", "Sol" => "NovaTouch Orderman SOL", _ => "NovaTouch Voucher"
                }, version, null);
            }
        }
        // Packages outside a channel and broken files in other channels must
        // never win over the requested release or produce unrelated warnings.
        File.WriteAllText(Path.Combine(builds, "nt.payment_99.0.0.zip"), "outside any channel");
        File.WriteAllText(Path.Combine(builds, "Fiscal", "nt.fiscal_99.0.0.zip"), "outside any channel");
        File.WriteAllText(Path.Combine(builds, "Pos", "alpha", "broken.msi"), "invalid MSI");
        string nestedOtherChannel = Path.Combine(builds, "Payment", "stable", "beta");
        Directory.CreateDirectory(nestedOtherChannel);
        File.WriteAllText(Path.Combine(nestedOtherChannel, "nt.payment_98.0.0.zip"), "other channel nested within stable");
        using var catalog = new SoftwarePackageCatalog();
        foreach (var (channel, version) in new[] { ("ALPHA", "9.0.0"), ("BETA", "5.0.0"), ("STABLE", "2.0.0") })
        {
            SoftwareCatalogResult result = await catalog.ReadAsync(builds + Path.DirectorySeparatorChar, null, CancellationToken.None, channel);
            Assert(result.Packages.Length == 7 && result.Packages.All(package => package.Version.StartsWith(version, StringComparison.Ordinal)) &&
                result.Packages.All(package => Path.GetRelativePath(builds, package.Path).Split(Path.DirectorySeparatorChar).Contains(channel, StringComparer.OrdinalIgnoreCase)),
                channel + " selects all seven products exclusively from their matching channel folders, including nested packages");
            Assert(result.Warnings.Length == (channel == "ALPHA" ? 1 : 0), channel + " only reports unreadable packages within the selected channel");
        }
        var stable = await catalog.ReadAsync(builds, null, CancellationToken.None);
        Assert(stable.Packages.Length == 7 && stable.Packages.All(package => package.Version.StartsWith("2.0.0", StringComparison.Ordinal)),
            "builds folders default to STABLE when no preference has been supplied");
        var regular = await catalog.ReadAsync(root, null, CancellationToken.None, "BETA");
        Assert(regular.Packages.Single(package => package.Component == UpdateComponents.Payment).Version == "99.0.0" &&
            regular.Packages.Single(package => package.Component == UpdateComponents.Pos).Version == "9.0.0.0",
            "a normal folder keeps unrestricted recursive discovery even if it contains a builds subfolder");
        Assert(SoftwarePackageCatalog.IsBuildsDirectory(@"\\fixture.invalid\software\builds\") &&
            SoftwarePackageCatalog.IsBuildsDirectory(@"C:\Software\BUILDS/") &&
            !SoftwarePackageCatalog.IsBuildsDirectory(@"C:\Software\builds-archive") &&
            !SoftwarePackageCatalog.IsBuildsDirectory(@"C:\Software\builds\Payment"),
            "only a directory actually named builds enables channel filtering, including UNC paths and trailing separators");
        string channelFirst = Path.Combine(root, "channel-first", "builds", "beta", "Fiscal");
        Directory.CreateDirectory(channelFirst);
        File.WriteAllText(Path.Combine(channelFirst, "nt.fiscal_4.0.0.zip"), "fixture");
        var alternate = await catalog.ReadAsync(Path.GetFullPath(Path.Combine(channelFirst, "..", "..")), null, CancellationToken.None, "BETA");
        Assert(alternate.Packages is [{ Component: UpdateComponents.Fiscal, Version: "4.0.0" }], "channel-first builds/beta/product folders are supported as well");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { await catalog.ReadAsync(builds, null, cancellation.Token, "BETA"); } catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "channel discovery honours cancellation without executing installers");
    }

    private static void BuildChannelSelectionTests()
    {
        var previousContext = SynchronizationContext.Current;
        using var context = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            _ = form.Handle;
            var combo = Field<ComboBox>(form, "useBuild");
            var gate = Field<BetaFeatureGate>(form, "betaFeatures");
            var row = combo.Parent!;
            using var canvas = new Panel { Size = new Size(550, 100) };
            var parent = (TableLayoutPanel)row.Parent!;
            var cell = parent.GetCellPosition(row);
            canvas.Controls.Add(row);
            canvas.CreateControl();
            Assert(!row.Visible && combo.DropDownStyle == ComboBoxStyle.DropDownList && combo.Items.Cast<string>().SequenceEqual(["ALPHA", "BETA", "STABLE"]) &&
                (string)combo.SelectedItem! == "STABLE", "use Build is a hidden beta setting with three choices and STABLE as the initial default");
            SetField(form, "showBetaActivated", new Action(() => { }));
            var image = All(form).OfType<AppMark>().Single();
            for (int click = 0; click < 5; click++) gate.RegisterClick(image, true);
            Assert(gate.Enabled && row.Visible && combo.Visible && row.Name == "buildPreference", "five logo clicks reveal the complete use Build row");
            parent.Controls.Add(row, cell.Column, cell.Row);

            Field<TextBox>(form, "softwarePath").Text = buildChannelFixture;
            Complete((Task)Call(form, "LoadSoftwarePackagesAsync")!);
            AssertBuild("STABLE", "2.0.0");
            string individual = Path.Combine(Path.GetDirectoryName(buildChannelFixture)!, "nt.payment_77.0.0.zip");
            File.WriteAllText(individual, "individual fixture");
            Call(form, "SelectIndividualPackage", new SoftwarePackage(UpdateComponents.Payment, individual, "77.0.0"));
            Complete((Task)Call(form, "LoadSoftwarePackagesAsync")!);
            Assert(Field<string>(form, "paymentZip") == individual, "rereading the same build source preserves explicit individual selections");
            combo.SelectedItem = "BETA";
            Complete(Field<Task>(form, "buildPreferenceRefresh"));
            AssertBuild("BETA", "5.0.0");
            combo.SelectedItem = "ALPHA";
            Complete(Field<Task>(form, "buildPreferenceRefresh"));
            AssertBuild("ALPHA", "9.0.0");

            string config = Path.Combine(Path.GetDirectoryName(buildChannelFixture)!, "build-config.json");
            AppConfigStore.Save(config, (UpdaterConfig)Call(form, "CaptureConfig")!, "");
            using var fresh = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            _ = fresh.Handle;
            Call(fresh, "ApplyLoadedConfig", AppConfigStore.Load(config));
            Assert((string)Field<ComboBox>(fresh, "useBuild").SelectedItem! == "ALPHA" && !Field<BetaFeatureGate>(fresh, "betaFeatures").Enabled,
                "the saved channel is restored without enabling beta functions or starting a read while config loads");
            Complete((Task)Call(fresh, "LoadSoftwarePackagesAsync")!);
            Assert(Field<string>(fresh, "paymentVersion") == "9.0.0" && Field<Label>(fresh, "packageStatus").Text.Contains("use ALPHA"),
                "future package reads honour the saved preference even before beta controls are shown again");

            SetField(form, "busy", true); Call(form, "UpdateButtons");
            Assert(!combo.Enabled, "build choice cannot change during an active operation");
            SetField(form, "busy", false); Call(form, "UpdateButtons");
            string stableFolder = Path.Combine(buildChannelFixture, "Payment", "stable");
            foreach (string package in Directory.EnumerateFiles(stableFolder, "*.zip", SearchOption.AllDirectories)) File.Delete(package);
            combo.SelectedItem = "STABLE";
            Complete(Field<Task>(form, "buildPreferenceRefresh"));
            Assert(Field<string>(form, "paymentZip") == "" && Field<string>(form, "fiscalVersion") == "2.0.0" &&
                !Field<Label>(form, "selectedPackagesList").Text.Contains("9.0.0"), "a product missing from the new channel does not retain its old-channel selection");

            void AssertBuild(string channel, string version)
            {
                var packages = Field<SoftwarePackage[]>(form, "folderPackages");
                Assert(packages.Length == 7 && Field<string>(form, "paymentVersion") == version &&
                    Field<Label>(form, "packageStatus").Text.Contains("use " + channel) &&
                    Field<Label>(form, "selectedPackagesList").Text.Split(Environment.NewLine).Length == 7 &&
                    Field<Label>(form, "selectedPackagesList").Text.Contains(version), channel + " refreshes the real selected package paths, versions and displayed summary");
                var request = (WorkerRequest)Call(form, "Request", "update", new HostState { Host = "LOCALHOST" }, UpdateComponents.All)!;
                Assert(request.PaymentZip == Field<string>(form, "paymentZip") && request.MsiPackages.Length == 4 &&
                    request.MsiPackages.All(package => package.PayloadPath.Split(Path.DirectorySeparatorChar).Contains(channel, StringComparer.OrdinalIgnoreCase)),
                    channel + " is also used in actual installation requests for every selected MSI");
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
}
