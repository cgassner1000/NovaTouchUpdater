namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly LoadingSpinner packageLoadingSpinner = new() { Name = "packageLoadingSpinner" };

    private void ConfigurePackageLoadingSpinner()
    {
        Controls.Add(packageLoadingSpinner);
        packageLoadingSpinner.SendToBack();
        ClientSizeChanged += (_, _) => CenterPackageLoadingSpinner();
        packageLoadingSpinner.SizeChanged += (_, _) => CenterPackageLoadingSpinner();
        CenterPackageLoadingSpinner();
    }

    private void CenterPackageLoadingSpinner()
    {
        packageLoadingSpinner.Location = new Point((ClientSize.Width - packageLoadingSpinner.Width) / 2,
            (ClientSize.Height - packageLoadingSpinner.Height) / 2);
    }

    private void UpdatePackageLoadingSpinner()
    {
        if (packageLoadingSpinner.Active == loadingPackages) return;
        packageLoadingSpinner.Active = loadingPackages;
        if (loadingPackages) { CenterPackageLoadingSpinner(); packageLoadingSpinner.BringToFront(); }
        else packageLoadingSpinner.SendToBack();
    }
}
