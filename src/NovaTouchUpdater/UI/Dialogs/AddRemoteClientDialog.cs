namespace NovaTouchUpdater;

internal sealed class AddRemoteClientDialog : Form
{
    private readonly TextBox host = new() { Name = "remoteHost", Dock = DockStyle.Fill, MaxLength = 253, PlaceholderText = "Hostname oder IP-Adresse" };
    private readonly Label error = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = UiTheme.Danger, Visible = false };
    internal string HostName { get; private set; } = "";

    internal AddRemoteClientDialog()
    {
        SuspendLayout();
        Text = "Remote Client hinzufügen";
        Font = UiTheme.BodyFont();
        ClientSize = new Size(440, 170);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Hostname oder IP-Adresse", AutoSize = true, Margin = new Padding(3, 0, 3, 7) }, 0, 0);
        layout.Controls.Add(host, 0, 1);
        layout.Controls.Add(error, 0, 2);
        var add = new AppButton { Name = "addRemoteClient", Text = "Hinzufügen", AutoSize = true, Role = ButtonRole.Accent };
        var cancel = new AppButton { Text = "Abbrechen", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, add]);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        add.Click += (_, _) =>
        {
            if (!TryNormalize(host.Text, out string name))
            {
                error.Text = "Bitte einen gültigen Remote-Hostnamen oder eine IP-Adresse eingeben.";
                error.Visible = true; host.Focus(); return;
            }
            HostName = name; DialogResult = DialogResult.OK; Close();
        };
        host.TextChanged += (_, _) => error.Visible = false;
        AcceptButton = add; CancelButton = cancel;
        WindowLayout.CompleteInitialization(this);
        Shown += (_, _) => host.Focus();
    }

    internal static bool TryNormalize(string value, out string host)
    {
        try
        {
            host = WinRmClientSetup.ValidateHost(value);
            return host.Length is > 0 and <= 253 && !host.Any(char.IsWhiteSpace) &&
                !host.Equals("LOCALHOST", StringComparison.OrdinalIgnoreCase) && host != ".";
        }
        catch (ArgumentException) { host = ""; return false; }
    }
}
