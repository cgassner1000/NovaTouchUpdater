using NovaTouchUpdater;

internal static partial class Program
{
    private static void ConnectionBadgeTests()
    {
        var worker = new FakeRunner(); var monitor = new FakeRunner();
        using var form = new MainForm(preview: true, worker, monitor);
        HostState[] hosts = Populate(form);
        var table = Field<DataGridView>(form, "table");
        var https = Field<CheckBox>(form, "chkSsl");
        HostState host = hosts[0];
        var cell = (ConnectionBadgeCell)host.Row!.Cells["connection"];
        DateTimeOffset now = DateTimeOffset.Now.AddMinutes(1);
        int generation = Field<int>(form, "connectionGeneration");
        Call(form, "ApplyConnectionResult", host, host.ConnectionState.Revision, generation, true, "WinRM HTTP/5985: synthetic success", now, false);
        Assert(cell.Value is "WinRM - OK" && cell.Tag is false && cell.IconSpace == 0 && host.Connection == "Verbunden",
            "the host table shows WinRM - OK while existing state, summary and retry semantics remain intact");
        https.Checked = true;
        Assert(cell.Tag is false, "enabling the HTTPS setting alone cannot claim an encrypted connection");
        Call(form, "ApplyConnectionResult", host, host.ConnectionState.Revision, generation, true, "stale HTTPS", now.AddSeconds(1), true);
        Assert(cell.Tag is false, "a late result from an invalidated monitoring round cannot add a lock");
        generation = Field<int>(form, "connectionGeneration");
        Call(form, "ApplyConnectionResult", host, host.ConnectionState.Revision, generation, true, "WinRM HTTPS/5986: synthetic success", now.AddSeconds(2), true);
        Assert(cell.Value is "WinRM - OK" && cell.Tag is true && cell.IconSpace > 0 && cell.ToolTipText.Contains("TLS-verschlüsselt"),
            "a successful HTTPS session shows the lock and an explicit TLS tooltip");
        Call(form, "ApplyConnectionResult", hosts[1], hosts[1].ConnectionState.Revision, generation, true, "WinRM HTTP/5985: synthetic success", now, false);
        hosts[2].Connection = "Nicht erreichbar";
        Call(form, "PaintRow", hosts[2]);
        table.Dock = DockStyle.None;
        table.Size = new Size(1500, 340);
        foreach (Control control in All(form)) _ = control.Handle;
        foreach (string mode in new[] { "Light", "Dark" })
        {
            UiTheme.SetMode(mode);
            for (int pass = 0; pass < 3; pass++) Layout(table);
            Call(form, "ApplyColumnWidths");
            using var full = new Bitmap(table.Width, table.Height);
            table.DrawToBitmap(full, table.ClientRectangle);
            int width = Math.Min(full.Width, table.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible && column.DisplayIndex <= table.Columns["connection"].DisplayIndex).Sum(column => column.Width) + 2);
            int height = Math.Min(full.Height, table.ColumnHeadersHeight + table.Rows.Cast<DataGridViewRow>().Sum(row => row.Height) + 3);
            using var preview = full.Clone(new Rectangle(0, 0, width, height), full.PixelFormat);
            preview.Save(Path.Combine(TestWorkspace.Artifacts, "winrm-connection-badge-" + mode.ToLowerInvariant() + ".png"));
            using var graphics = table.CreateGraphics();
            int content = TextRenderer.MeasureText(graphics, "WinRM - OK", cell.InheritedStyle.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
            Assert(table.Columns["connection"].Width >= content + cell.IconSpace + cell.InheritedStyle.Padding.Horizontal,
                "dynamic connection width includes the lock in " + mode + " mode");
        }
        Call(form, "ApplyConnectionResult", host, host.ConnectionState.Revision, generation, false, "synthetic HTTPS failure", now.AddSeconds(3), true);
        Assert(cell.Value is "Nicht erreichbar" && cell.Tag is false && cell.IconSpace == 0, "an unreachable client loses the success badge and lock");
        Assert(worker.Requests.IsEmpty && monitor.Requests.IsEmpty, "badge verification uses injected results and starts no real or simulated connection requests");
        UiTheme.SetMode("System");
    }
}
