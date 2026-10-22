using NovaTouchUpdater;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
    private static IEnumerable<Control> All(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control item in All(child)) yield return item;
    }
    private static void Layout(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls) Layout(child);
    }
    private static void Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using (var dialog = new CommandDialog(24))
        {
            var controls = All(dialog).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name);
            var payment = (CheckBox)controls["payment"];
            var fiscal = (CheckBox)controls["fiscal"];
            var suite = (CheckBox)controls["serverSuite"];
            var terminal = (CheckBox)controls["terminal"];
            var pos = (CheckBox)controls["pos"];
            var execute = (Button)controls["execute"];
            Assert(!execute.Enabled && !dialog.RestartPos, "no action can run without a selection");
            payment.Checked = fiscal.Checked = true;
            Assert(dialog.RestartPayment && dialog.RestartFiscal && execute.Enabled, "service actions can still be combined");
            pos.Checked = true;
            Assert(dialog.RestartPos && !dialog.RestartTerminals && !dialog.RestartPayment && !dialog.RestartFiscal && !dialog.StartServerSuite &&
                !payment.Checked && !fiscal.Checked && !payment.Enabled && !fiscal.Enabled && !suite.Enabled,
                "POS restart clears service selection and runs as a standalone action");
            Assert(execute.Enabled && execute.Text == "POS-Neustart bestätigen …" && controls["selection"].Text.Contains("angemeldeten Benutzerkonto"),
                "POS selection explains the user context and the confirmation step");
            terminal.Checked = true;
            Assert(dialog.RestartTerminals && !dialog.RestartPos && !pos.Checked && !payment.Enabled,
                "choosing Windows restart replaces POS restart without combining actions");
            pos.Checked = true;
            Assert(dialog.RestartPos && !terminal.Checked && !dialog.RestartTerminals,
                "choosing POS restart replaces Windows restart");
            pos.Checked = false;
            Assert(payment.Enabled && fiscal.Enabled && suite.Enabled && !execute.Enabled,
                "leaving POS restart restores service controls without silently selecting an action");
            suite.Checked = true;
            Assert(dialog.StartServerSuite && !dialog.RestartPos && !dialog.RestartTerminals, "Server Suite retains its existing action");
            Assert(pos.Text.StartsWith("POS neustart") && pos.AccessibleDescription!.Contains("übersprungen"),
                "POS action explains its scope and how missing/inactive applications are handled");
        }

        // Invisible controls only: never show a form or invoke the command worker.
        foreach (var test in new[] { (scale: 1F, width: 780, height: 690), (scale: 1F, width: 620, height: 480),
            (scale: 1.25F, width: 1000, height: 800), (scale: 1.5F, width: 1180, height: 900),
            (scale: 2F, width: 1280, height: 700), (scale: 2F, width: 800, height: 500) })
        {
            using var dialog = new CommandDialog(24);
            var fonts = All(dialog).ToDictionary(c => c, c => (c.Font.FontFamily, c.Font.SizeInPoints, c.Font.Style));
            if (test.scale != 1) dialog.Scale(new SizeF(test.scale, test.scale));
            foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.SizeInPoints * test.scale, font.Style);
            dialog.MinimumSize = Size.Empty;
            dialog.ClientSize = new Size(test.width, test.height);
            _ = dialog.Handle;
            foreach (string mode in new[] { "none", "pos", "terminal" })
            {
                foreach (var choice in All(dialog).OfType<CheckBox>()) choice.Checked = choice.Name == mode;
                for (int pass = 0; pass < 5; pass++) Layout(dialog);
                var controls = All(dialog).ToArray();
                var execute = controls.Single(c => c.Name == "execute");
                Rectangle footerBounds = execute.Bounds;
                for (Control? parent = execute.Parent; parent != dialog && parent is not null; parent = parent.Parent) footerBounds.Offset(parent.Location);
                Assert(dialog.ClientRectangle.Contains(footerBounds),
                    $"command footer stays inside {test.width}x{test.height} at {test.scale:P0} in {mode} mode");
                var content = controls.OfType<Panel>().Single(p => p.AutoScroll);
                Assert(content.Height > 0 && content.Controls.Count == 1 && content.Controls[0].Controls.OfType<TableLayoutPanel>().Count() == 5,
                    "all five action cards remain in the scroll area");
                Assert(controls.OfType<CheckBox>().All(c => c.Height >= c.GetPreferredSize(new Size(c.Width, 0)).Height),
                    "action titles fit their allocated height");
                Assert(controls.OfType<Label>().All(c => c.Height >= c.GetPreferredSize(new Size(c.Width, 0)).Height),
                    "descriptions and selection summary fit their allocated height");
            }
        }
    }
}
