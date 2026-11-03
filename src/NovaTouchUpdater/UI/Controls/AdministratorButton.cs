namespace NovaTouchUpdater;

internal sealed class AdministratorButton : Button
{
    internal AdministratorButton()
    {
        Name = "restartAsAdministrator";
        Text = "NovaTouchUpdater als\nAdministrator starten";
        AccessibleName = "NovaTouchUpdater als Administrator starten";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = UiTheme.AccentSoft;
        FlatAppearance.MouseDownBackColor = UiTheme.AccentSoft;
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Accent;
        Font = new Font("Segoe UI", 8.5F);
        Cursor = Cursors.Hand;
        Padding = new Padding(6, 2, 6, 2);
        TextImageRelation = TextImageRelation.ImageBeforeText;
        TextAlign = ContentAlignment.MiddleLeft;
        UpdateShield();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        UpdateShield();
        base.OnDpiChangedAfterParent(e);
    }

    private void UpdateShield()
    {
        int size = (int)Math.Round(24 * DeviceDpi / 96F);
        using var icon = new Icon(SystemIcons.Shield, new Size(size, size));
        Image? previous = Image;
        Image = icon.ToBitmap();
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Image?.Dispose(); Image = null; }
        base.Dispose(disposing);
    }
}
