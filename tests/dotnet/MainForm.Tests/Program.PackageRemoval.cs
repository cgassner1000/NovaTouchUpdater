using NovaTouchUpdater;

internal static partial class Program
{
    private static void PackageRemovalTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "package-removal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SoftwarePackage[] packages = HostUpdateState.Each(UpdateComponents.All).Select(component =>
        {
            string path = Path.Combine(root, component is UpdateComponents.Ntc or UpdateComponents.Ntv ? "shared-msi.zip" : component + ".zip");
            File.WriteAllText(path, "synthetic package bytes");
            return new SoftwarePackage(component, path, "2026.4.9800", (component & UpdateComponents.Msi) == 0 ? null :
                new MsiPackageRequest { Kind = component.ToString().ToLowerInvariant(), PayloadPath = path, ProductVersion = "2026.4.9800" });
        }).ToArray();
        var originalFiles = packages.Select(package => package.Path).Distinct().ToDictionary(path => path, File.ReadAllBytes);
        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        HostState[] hosts = Populate(form);
        SetField(form, "folderPackages", packages);
        var buttons = Field<Dictionary<UpdateComponents, Button>>(form, "removePackageButtons");
        foreach (SoftwarePackage package in packages)
        {
            Call(form, "UseFolderPackages");
            Assert(buttons.Count == 7 && buttons.Values.All(button => button.Enabled),
                "each loaded component offers removal even while the individual-files section is collapsed");
            SetField(form, "paymentHash", "payment-hash");
            SetField(form, "fiscalHash", "fiscal-hash");
            SetField(form, "serverSuiteHash", "suite-hash");
            var priorUpdate = new HostUpdateState(UpdateComponents.All) { DatabaseUploadCompleted = true, SuccessfulPosUploadInRun = true };
            hosts[0].Updates = priorUpdate;
            typeof(Button).GetMethod("OnClick", Private)!.Invoke(buttons[package.Component], [EventArgs.Empty]);
            UpdateComponents expected = UpdateComponents.All & ~package.Component;
            Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == expected &&
                Field<TableLayoutPanel>(form, "selectedPackagesTable").RowCount == 6 && !buttons[package.Component].Enabled,
                package.Component + " removal changes only its selection and immediately updates the package summary");
            var request = (WorkerRequest)Call(form, "Request", "update", new HostState { Host = "LOCALHOST" }, expected)!;
            Assert(request.PaymentZip == (package.Component == UpdateComponents.Payment ? "" : packages.Single(p => p.Component == UpdateComponents.Payment).Path) &&
                request.FiscalZip == (package.Component == UpdateComponents.Fiscal ? "" : packages.Single(p => p.Component == UpdateComponents.Fiscal).Path) &&
                request.ServerSuiteZip == (package.Component == UpdateComponents.ServerSuite ? "" : packages.Single(p => p.Component == UpdateComponents.ServerSuite).Path) &&
                request.MsiPackages.Select(msi => msi.Kind).ToHashSet().SetEquals(packages.Where(p => p.Msi is not null && p.Component != package.Component).Select(p => p.Msi!.Kind)),
                package.Component + " is omitted from the update request while all other packages remain");
            if ((package.Component & UpdateComponents.Msi) != 0)
                Assert(!priorUpdate.DatabaseUploadCompleted && !priorUpdate.SuccessfulPosUploadInRun,
                    "removing an MSI invalidates upload results for the previous selection");
            else
                Assert((package.Component switch
                {
                    UpdateComponents.Payment => Field<string>(form, "paymentHash"),
                    UpdateComponents.Fiscal => Field<string>(form, "fiscalHash"),
                    _ => Field<string>(form, "serverSuiteHash")
                }).Length == 0, "removing a service package clears its cached hash");
            Assert(originalFiles.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)),
                "all source files, including a ZIP shared by multiple products, remain byte-for-byte unchanged");
            Call(form, "SelectIndividualPackage", package);
            Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == UpdateComponents.All && buttons.Values.All(button => button.Enabled),
                "an individually reselected package is added back without changing the others");
        }
        Call(form, "UseFolderPackages");
        foreach (string state in new[] { "busy", "selfUpdating", "loadingPackages", "preparingDatabaseUpload", "restartingAsAdministrator" })
        {
            SetField(form, state, true);
            Call(form, "UpdateButtons");
            Assert(buttons.Values.All(button => !button.Enabled), "package removal is disabled during " + state);
            typeof(Button).GetMethod("OnClick", Private)!.Invoke(buttons[UpdateComponents.Payment], [EventArgs.Empty]);
            Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == UpdateComponents.All,
                "active work keeps the captured package selection unchanged");
            SetField(form, state, false);
        }
        Call(form, "UpdateButtons");
        foreach (Button button in buttons.Values)
            typeof(Button).GetMethod("OnClick", Private)!.Invoke(button, [EventArgs.Empty]);
        Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == UpdateComponents.None &&
            Field<Label>(form, "selectedPackagesList").Text == "Keine Pakete ausgewählt." &&
            buttons.Values.All(button => !button.Enabled), "removing the last package restores the empty selection");
        Call(form, "UseFolderPackages");
        Assert((UpdateComponents)Call(form, "SelectedUpdateComponents")! == UpdateComponents.All,
            "the complete folder selection can be explicitly restored after all packages were removed");
    }
}
