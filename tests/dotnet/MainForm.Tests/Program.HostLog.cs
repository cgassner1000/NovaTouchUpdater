using System.Diagnostics;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void HostLogTests()
    {
        var source = new List<string>();
        var offsets = new List<int>();
        var sync = new object();
        for (int index = 0; index < 150; index++)
            source.Add($"2026-11-11 10:20:{index % 60:00}.123 [SRV-TEST] Setup installieren: nt3_server_suite_{index} · Version 2026.4.9999 installieren");
        source[148] = "2026-11-11 10:22:28.456 [SRV-TEST] Setup fehlgeschlagen: nt3_server_suite_C4U1 · Startdatei fehlt: C:\\Novacom\\novatouch3\\C4U1\\portable_python\\python.exe";
        source[149] = "2026-11-11 10:22:29.456 [SRV-TEST] Setup wiederholen: nt3_server_suite_C4U1 · Installation wird erneut versucht (Versuch 2 von 3)";
        string[] Read(int offset)
        {
            lock (sync) { offsets.Add(offset); return source.Skip(offset).ToArray(); }
        }
        using var dialog = new HostLogDialog("SRV-TEST", Read) { Opacity = 0 };
        dialog.Show(); Application.DoEvents();
        var grid = All(dialog).OfType<DataGridView>().Single();
        var toggle = All(dialog).OfType<CheckBox>().Single(control => control.Name == "autoRefresh");
        var details = All(dialog).OfType<TextBox>().Single(control => control.Name == "logDetails");
        Assert(toggle.Checked && grid.VirtualMode && grid.RowCount == 150 && grid.CurrentCell!.RowIndex == 149 && grid.Rows[149].Displayed,
            "host log opens with auto-refresh enabled and the newest entry selected and visible");
        Assert(grid[0, 0].Value?.ToString() == "10:20:00.123" && grid[1, 0].Value?.ToString() == "Setup installieren" &&
            grid[2, 0].Value?.ToString() == "nt3_server_suite_0 · Version 2026.4.9999 installieren" && details.Text == source[149],
            "log columns separate time, phase and message while details retain the complete timestamp, host and original text");

        Task.Run(() => { lock (sync) source.Add("2026-11-11 10:22:30.456 [SRV-TEST] Setup abgeschlossen: C4U1 erfolgreich"); }).GetAwaiter().GetResult();
        PumpLogUntil(() => grid.RowCount == 151);
        Assert(offsets.Contains(150) && grid.CurrentCell!.RowIndex == 150 && grid.Rows[150].Displayed && details.Text == source[150],
            "real timer refresh picks up background log appends incrementally and follows the newest entry");

        toggle.Checked = false;
        grid.CurrentCell = grid[0, 20]; grid.FirstDisplayedScrollingRowIndex = 15;
        details.Select(5, 12);
        int first = grid.FirstDisplayedScrollingRowIndex, reads = offsets.Count;
        source.Add("2026-11-11 10:22:31.456 [SRV-TEST] Softwarepaket kompilieren: IRIS:1972/USER · Klasse 120 von 300 · Paket.xml");
        PumpLogFor(650);
        Assert(grid.RowCount == 151 && offsets.Count == reads && grid.CurrentCell.RowIndex == 20 && grid.FirstDisplayedScrollingRowIndex == first && details.SelectionLength == 12,
            "paused auto-refresh stops reading and leaves both scroll position and selected detail text undisturbed");
        All(dialog).OfType<Button>().Single(button => button.Name == "refreshLog").PerformClick();
        Assert(grid.RowCount == 152 && grid.CurrentCell.RowIndex == 20 && grid.FirstDisplayedScrollingRowIndex == first && details.SelectionLength == 12,
            "manual refresh appends new entries without moving a paused reader or resetting text selection");
        toggle.Checked = true;
        Assert(grid.CurrentCell.RowIndex == 151 && grid.Rows[151].Displayed && grid[2, 151].Value!.ToString()!.Contains("IRIS:1972/USER"),
            "resuming auto-refresh immediately returns to the end without splitting database ports into the phase column");

        grid.CurrentCell = grid[0, 148]; Application.DoEvents();
        using (var bitmap = new Bitmap(dialog.ClientSize.Width, dialog.ClientSize.Height))
        {
            dialog.Controls[0].DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "host-log.png"));
        }
        dialog.ClientSize = new Size(720, 480); Application.DoEvents();
        Assert(details.Height >= 60 && grid.Height >= 100 && All(dialog).OfType<Button>().All(button => button.Right <= button.Parent!.ClientSize.Width),
            "host log retains readable entries, details and toolbar actions at its minimum window size");
        dialog.Close(); reads = offsets.Count; PumpLogFor(650);
        Assert(offsets.Count == reads, "closing the log disposes the refresh timer and stops all source reads");

        var empty = new List<string>();
        using var emptyDialog = new HostLogDialog("EMPTY", offset => empty.Skip(offset).ToArray()) { Opacity = 0 };
        emptyDialog.Show(); Application.DoEvents();
        var emptyGrid = All(emptyDialog).OfType<DataGridView>().Single();
        Assert(emptyGrid.RowCount == 0 && All(emptyDialog).OfType<TextBox>().Single().Text.Contains("Noch keine"), "an empty live log has a clear placeholder");
        empty.Add("Unformatierter älterer Eintrag mit Umlauten: Grüße");
        PumpLogUntil(() => emptyGrid.RowCount == 1);
        Assert(emptyGrid[2, 0].Value!.ToString()!.Contains("Grüße") && All(emptyDialog).OfType<TextBox>().Single().Text == empty[0],
            "the first arriving entry and unformatted older log lines remain complete and readable");
        emptyDialog.Close();
    }

    private static void PumpLogUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 4000) { Application.DoEvents(); Thread.Sleep(5); }
        Assert(condition(), "live log change arrives through the WinForms timer");
    }

    private static void PumpLogFor(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
}
