namespace NovaTouchUpdater;

internal sealed class DatabaseCheckForm : Form
{
    private readonly Func<CancellationToken, Task<IrisInventoryResult>> readInventory;
    private readonly TreeView instances = new() { Name = "irisInstances", Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.FixedSingle, MinimumSize = new Size(0, 180) };
    private readonly Label status = new() { Name = "databaseStatus", AutoSize = true, Dock = DockStyle.Fill, Text = "Lokale IRIS-Instanzen werden eingelesen …" };
    private readonly TextBox details = new() { Name = "databaseDetails", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, MinimumSize = new Size(0, 95) };
    private readonly Button refresh = new ModernButton { Name = "refreshDatabase", Text = "Aktualisieren", AutoSize = true, Padding = new Padding(12, 7, 12, 7) };
    private IrisInventoryResult? lastInventory;
    private CancellationTokenSource? pending;

    internal DatabaseCheckForm(Func<CancellationToken, Task<IrisInventoryResult>>? readInventory = null)
    {
        this.readInventory = readInventory ?? new IrisDatabaseInventory().ReadAsync;
        SuspendLayout();
        Text = "Database Check – LOKAL";
        Font = UiTheme.BodyFont();
        ClientSize = new Size(900, 650);
        MinimumSize = new Size(620, 440);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Installierte InterSystems-IRIS-Instanzen auf diesem Rechner und ihre konfigurierten Namespaces. %SYS wird ausgeblendet.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 10) }, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(instances, 0, 2);
        layout.Controls.Add(details, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var close = new ModernButton { Text = "Schließen", AutoSize = true, Padding = new Padding(12, 7, 12, 7), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(close);
        buttons.Controls.Add(refresh);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(WindowLayout.CreateScrollArea(layout, instances, details));
        CancelButton = close;
        instances.AfterSelect += (_, _) => ShowDetails();
        ShowDetails();
        refresh.Click += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => await RefreshAsync();
        FormClosing += (_, _) => pending?.Cancel();
        Disposed += (_, _) => pending?.Cancel();
        WindowLayout.CompleteInitialization(this);
    }

    internal async Task RefreshAsync()
    {
        if (pending is not null || IsDisposed) return;
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        refresh.Enabled = false;
        lastInventory = null;
        instances.Nodes.Clear();
        details.Clear();
        ShowDetails();
        status.Text = "Lokale IRIS-Instanzen werden eingelesen …";
        status.ForeColor = UiTheme.Muted;
        try
        {
            IrisInventoryResult result = await readInventory(cancellation.Token);
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            lastInventory = result;
            int namespaceCount = 0;
            instances.BeginUpdate();
            try
            {
                foreach (IrisInstanceInfo instance in result.Instances)
                {
                    var root = new TreeNode(instance.Name + (instance.Version.Length > 0 ? " · " + instance.Version : "")) { Tag = instance };
                    instances.Nodes.Add(root);
                    string[] names = instance.Namespaces.Where(name => !name.Equals("%SYS", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (string name in names) root.Nodes.Add(new TreeNode(name) { Tag = instance });
                    namespaceCount += names.Length;
                    if (instance.Error.Length > 0) root.Nodes.Add(new TreeNode("Namespaces konnten nicht gelesen werden") { ForeColor = UiTheme.Danger, Tag = instance });
                    else if (names.Length == 0) root.Nodes.Add(new TreeNode("Keine Namespaces außer %SYS konfiguriert") { ForeColor = UiTheme.Muted, Tag = instance });
                    root.Expand();
                }
                if (result.Warnings.Length > 0)
                {
                    var warnings = instances.Nodes.Add("Hinweise zur Erkennung");
                    foreach (string warning in result.Warnings) warnings.Nodes.Add(warning);
                    warnings.Expand();
                }
            }
            finally { instances.EndUpdate(); }
            int errors = result.Instances.Count(instance => instance.Error.Length > 0) + result.Warnings.Length;
            status.Text = result.Instances.Length == 0 ? errors > 0 ? "IRIS-Erkennung fehlgeschlagen – siehe Hinweise." : "Keine lokal installierten InterSystems-IRIS-Instanzen gefunden."
                : $"{result.Instances.Length} Instanz(en), {namespaceCount} Namespace(s) ohne %SYS." + (errors > 0 ? $" {errors} Fehler/Hinweis(e)." : "");
            status.ForeColor = errors > 0 ? UiTheme.Warning : UiTheme.Text;
            if (instances.Nodes.Count > 0) instances.SelectedNode = instances.Nodes[0];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            status.Text = "Database Check fehlgeschlagen.";
            status.ForeColor = UiTheme.Danger;
            details.Text = ex.Message;
        }
        finally
        {
            pending = null;
            if (!IsDisposed)
            {
                refresh.Enabled = true;
                if (lastInventory is not null) ShowDetails();
            }
        }
    }

    private void ShowDetails()
    {
        if (instances.SelectedNode?.Tag is IrisInstanceInfo instance)
            details.Text = $"Instanz: {instance.Name}\r\nVersion: {instance.Version}\r\nInstallation: {instance.Directory}\r\nKonfiguration: {instance.ConfigurationFile}\r\nSuperServer-Port: {(instance.SuperServerPort > 0 ? instance.SuperServerPort.ToString() : instance.ConnectionError)}" +
                (instance.Error.Length > 0 ? "\r\nFehler: " + instance.Error : "");
        else details.Text = instances.SelectedNode?.Text ?? "";
    }
}
