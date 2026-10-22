namespace NovaTouchUpdater;

internal sealed class CommandDialog : Form
{
    private readonly CheckBox payment = new() { Name = "payment" };
    private readonly CheckBox fiscal = new() { Name = "fiscal" };
    private readonly CheckBox serverSuite = new() { Name = "serverSuite" };
    private readonly CheckBox terminal = new() { Name = "terminal" };
    private readonly CheckBox pos = new() { Name = "pos" };
    private readonly Label selection = new() { Name = "selection", AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button execute = new()
    {
        Name = "execute", Text = "Auswahl ausführen", AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 7, 12, 7),
        DialogResult = DialogResult.OK, Enabled = false
    };

    public bool RestartTerminals => terminal.Checked;
    public bool RestartPos => pos.Checked;
    public bool RestartPayment => !RestartTerminals && !RestartPos && payment.Checked;
    public bool RestartFiscal => !RestartTerminals && !RestartPos && fiscal.Checked;
    public bool StartServerSuite => !RestartTerminals && !RestartPos && serverSuite.Checked;

    public CommandDialog(int hostCount)
    {
        SuspendLayout();
        Text = "Befehl senden";
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.White;
        ClientSize = new Size(780, 690);
        MinimumSize = new Size(620, 480);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        string hosts = hostCount == 1 ? "1 Host" : $"{hostCount} Hosts";
        var header = Stack(
            new Label
            {
                Text = "Was möchten Sie ausführen?", AutoSize = true, Dock = DockStyle.Fill,
                Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8)
            },
            Paragraph($"Die Auswahl gilt für die gesamte Hostliste ({hosts})."));
        header.Margin = new Padding(0, 0, 0, 14);
        layout.Controls.Add(header, 0, 0);

        var content = Stack(
            Heading("Dienste", 0),
            Paragraph("Eine oder mehrere Dienstaktionen auswählen."),
            ActionCard(payment, "nt.payment – Zahlungsdienst neu starten",
                "Beendet die nt.payment-Dienste und startet sie neu. Der Zahlungsdienst ist dabei kurzzeitig unterbrochen."),
            ActionCard(fiscal, "nt.fiscal – Fiskaldienst neu starten",
                "Beendet die nt.fiscal-Dienste und startet sie neu. Der Fiskaldienst ist dabei kurzzeitig unterbrochen."),
            ActionCard(serverSuite, "nt3 Server Suite – starten / neu starten",
                "Startet die nt3_server_suite-Dienste. Bereits laufende Dienste werden dafür zuerst beendet."),
            Heading("Kassenanwendung", 14),
            ActionCard(pos, "POS neustart – KassaPreh neu starten",
                "Beendet die laufende KassaPreh.exe und startet sie in derselben angemeldeten Benutzersitzung neu. Fehlt die Anwendung oder läuft sie nicht, wird der Host übersprungen. Windows bleibt geöffnet.", true),
            Heading("Gesamtes Terminal / Windows", 14),
            ActionCard(terminal, "Terminal NEUSTART – Windows neu starten",
                "Startet Windows auf den Hosts nach 30 Sekunden neu. Geöffnete Programme werden geschlossen; nicht gespeicherte Daten können verloren gehen.", true));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        scroll.Controls.Add(content);
        layout.Controls.Add(scroll, 0, 1);

        var cancel = new Button
        {
            Text = "Abbrechen", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 7, 12, 7), DialogResult = DialogResult.Cancel
        };
        var buttons = new WrappingFlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Margin = Padding.Empty
        };
        buttons.Controls.Add(execute);
        buttons.Controls.Add(cancel);
        selection.Margin = new Padding(0, 10, 0, 8);
        var footer = Stack(selection, buttons);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        AcceptButton = execute;
        CancelButton = cancel;

        foreach (var service in new[] { payment, fiscal, serverSuite })
            service.CheckedChanged += (_, _) => UpdateSelection();
        void SelectExclusive(CheckBox selected, CheckBox other)
        {
            if (selected.Checked) other.Checked = false;
            foreach (var service in new[] { payment, fiscal, serverSuite })
            {
                if (selected.Checked) service.Checked = false;
                service.Enabled = !RestartTerminals && !RestartPos;
            }
            UpdateSelection();
        }
        terminal.CheckedChanged += (_, _) => SelectExclusive(terminal, pos);
        pos.CheckedChanged += (_, _) => SelectExclusive(pos, terminal);
        UpdateSelection();
        WindowLayout.CompleteInitialization(this);
    }

    private void UpdateSelection()
    {
        var services = new List<string>();
        if (RestartPayment) services.Add("nt.payment");
        if (RestartFiscal) services.Add("nt.fiscal");
        if (StartServerSuite) services.Add("nt3 Server Suite");
        execute.Enabled = RestartTerminals || RestartPos || services.Count > 0;
        execute.Text = RestartTerminals ? "Windows-Neustart bestätigen …" : RestartPos ? "POS-Neustart bestätigen …" : "Auswahl ausführen";
        selection.Text = RestartTerminals
            ? "Ausgewählt: vollständiger Windows-Neustart. Eine Bestätigung folgt."
            : RestartPos ? "Ausgewählt: KassaPreh im angemeldeten Benutzerkonto neu starten. Eine Bestätigung folgt."
            : services.Count > 0 ? "Ausgewählte Dienste: " + string.Join(", ", services) : "Noch keine Aktion ausgewählt.";
        selection.ForeColor = RestartTerminals || RestartPos ? Color.FromArgb(153, 44, 32) : Color.FromArgb(75, 89, 107);
    }

    private static Label Paragraph(string text) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(75, 89, 107), Margin = new Padding(0, 0, 0, 8)
    };

    private Label Heading(string text, int topMargin) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill,
        Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, topMargin, 0, 6)
    };

    private TableLayoutPanel ActionCard(CheckBox choice, string title, string description, bool windowsRestart = false)
    {
        choice.Text = title;
        choice.AutoSize = true;
        choice.Dock = DockStyle.Fill;
        choice.Font = new Font(Font, FontStyle.Bold);
        choice.Margin = new Padding(0, 0, 0, 5);
        choice.AccessibleDescription = description;
        if (windowsRestart) choice.ForeColor = Color.FromArgb(153, 44, 32);
        var detail = Paragraph(description);
        detail.Margin = new Padding(24, 0, 0, 0);
        var card = Stack(choice, detail);
        card.Padding = new Padding(12, 10, 12, 10);
        card.Margin = new Padding(0, 0, 0, 8);
        card.BackColor = windowsRestart ? Color.FromArgb(255, 242, 235) : Color.FromArgb(243, 246, 250);
        card.BorderStyle = BorderStyle.FixedSingle;
        card.SizeChanged += (_, _) =>
        {
            int width = Math.Max(1, card.ClientSize.Width - card.Padding.Horizontal);
            var titleLimit = new Size(width, 0);
            var detailLimit = new Size(Math.Max(1, width - detail.Margin.Horizontal), 0);
            if (choice.MaximumSize != titleLimit) choice.MaximumSize = titleLimit;
            if (detail.MaximumSize != detailLimit) detail.MaximumSize = detailLimit;
        };
        detail.Click += (_, _) => { if (choice.Enabled) choice.Checked = !choice.Checked; };
        card.Click += (_, _) => { if (choice.Enabled) choice.Checked = !choice.Checked; };
        return card;
    }

    private static TableLayoutPanel Stack(params Control[] controls)
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = controls.Length, Margin = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < controls.Length; i++)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(controls[i], 0, i);
        }
        return stack;
    }
}
