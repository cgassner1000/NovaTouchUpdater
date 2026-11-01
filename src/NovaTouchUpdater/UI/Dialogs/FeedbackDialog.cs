using NovaTouchUpdater.Feedback;

namespace NovaTouchUpdater;

internal sealed class FeedbackDialog : Form
{
    private readonly IFeedbackClient client;
    private readonly FlowLayoutPanel category = new() { Name = "feedbackCategory", AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly RadioButton bug = new() { Name = "feedbackBug", Text = "Fehler melden", Checked = true, AutoSize = true, Margin = new Padding(0, 3, 18, 3) };
    private readonly RadioButton suggestion = new() { Name = "feedbackSuggestion", Text = "Verbesserungsvorschlag", AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
    private readonly TextBox reporter = new() { Name = "feedbackName", MaxLength = 80, Dock = DockStyle.Fill, PlaceholderText = "Freiwillig – leer lassen für eine anonyme Meldung" };
    private readonly TextBox title = new() { Name = "feedbackTitle", MaxLength = 160, Dock = DockStyle.Fill };
    private readonly TextBox description = new() { Name = "feedbackDescription", MaxLength = 12000, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MinimumSize = new Size(0, 200) };
    private readonly Label status = new() { Name = "feedbackStatus", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
    private readonly Button send = new ModernButton { Name = "sendFeedback", Text = "Meldung senden", Role = ButtonRole.Primary, AutoSize = true, Padding = new Padding(12, 7, 12, 7) };
    private readonly Button close = new ModernButton { Text = "Schließen", AutoSize = true, Padding = new Padding(12, 7, 12, 7), DialogResult = DialogResult.Cancel };
    private CancellationTokenSource? pending;
    private Guid submissionId = Guid.NewGuid();
    private bool sent;

    internal FeedbackDialog(IFeedbackClient client)
    {
        this.client = client;
        SuspendLayout();
        Text = "Fehler oder Verbesserung melden";
        Font = UiTheme.BodyFont();
        ClientSize = new Size(740, 630);
        MinimumSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        category.Controls.Add(bug); category.Controls.Add(suggestion);
        description.PlaceholderText = "Was ist passiert? Was wurde erwartet? Bei einem Fehler helfen die Schritte zum Nachstellen.";
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Padding = new Padding(20) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Ihre Rückmeldung", Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        layout.Controls.Add(new Label { Text = "Die Meldung wird als öffentliches GitHub-Issue veröffentlicht. Gesendet werden Ihre Eingaben und die Updater-Version. Name freiwillig, keine GitHub-Anmeldung nötig. Bitte keine Passwörter oder vertraulichen Daten eintragen.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 16) }, 0, 1);
        var fields = new TableLayoutPanel { ColumnCount = 2, RowCount = 4, Dock = DockStyle.Fill, Margin = Padding.Empty, MinimumSize = new Size(0, 340) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 3; row++) fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        int index = 0;
        foreach (var field in new (string Label, Control Input)[] { ("Art", category), ("Name (freiwillig)", reporter), ("Titel", title), ("Beschreibung", description) })
        {
            fields.Controls.Add(new Label { Text = field.Label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 7, 14, 10) }, 0, index);
            field.Input.Margin = new Padding(0, 3, 0, 10);
            fields.Controls.Add(field.Input, 1, index++);
        }
        layout.Controls.Add(fields, 0, 2);
        var buttons = new WrappingFlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
        buttons.Controls.Add(send); buttons.Controls.Add(close);
        var footer = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(WindowLayout.CreateScrollArea(layout, fields));
        CancelButton = close;
        send.Click += async (_, _) => await SubmitAsync();
        foreach (Control input in new Control[] { reporter, title, description }) input.TextChanged += (_, _) => Edited();
        bug.CheckedChanged += (_, _) => Edited();
        suggestion.CheckedChanged += (_, _) => Edited();
        FormClosing += (_, _) => pending?.Cancel();
        Disposed += (_, _) => pending?.Cancel();
        UpdateAvailability();
        WindowLayout.CompleteInitialization(this);
    }

    private FeedbackSubmission Submission() => new(submissionId, suggestion.Checked ? "enhancement" : "bug", title.Text, description.Text, reporter.Text, AppReleaseClient.DisplayVersion(AppReleaseClient.CurrentVersion));

    private void Edited()
    {
        submissionId = Guid.NewGuid();
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        bool valid;
        try { Submission().Validate(); valid = true; } catch (ArgumentException) { valid = false; }
        send.Enabled = client.IsConfigured && valid && pending is null && !sent;
        if (!client.IsConfigured) status.Text = "Das Senden von Meldungen ist noch nicht eingerichtet.";
    }

    internal async Task SubmitAsync()
    {
        if (pending is not null || sent) return;
        FeedbackSubmission submission;
        try { submission = Submission().Validate(); }
        catch (ArgumentException ex) { status.Text = ex.Message; return; }
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        foreach (Control control in new Control[] { category, reporter, title, description }) control.Enabled = false;
        UpdateAvailability();
        status.ForeColor = UiTheme.Muted;
        status.Text = "Meldung wird gesendet …";
        try
        {
            FeedbackReceipt receipt = await client.SendAsync(submission, cancellation.Token);
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            sent = true;
            status.ForeColor = UiTheme.Success;
            status.Text = $"Vielen Dank! Ihre Meldung wurde als Issue #{receipt.IssueNumber} erstellt.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            status.ForeColor = UiTheme.Danger;
            status.Text = ex is OperationCanceledException or HttpRequestException ? "Keine Bestätigung erhalten. Möglicherweise wurde die Meldung bereits erstellt. Bitte später prüfen." : ex.Message;
        }
        finally
        {
            pending = null;
            if (!IsDisposed)
            {
                foreach (Control control in new Control[] { category, reporter, title, description }) control.Enabled = !sent;
                UpdateAvailability();
            }
        }
    }
}
