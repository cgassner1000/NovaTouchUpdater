namespace NovaTouchUpdater;

internal static class AppMessageBox
{
    internal static DialogResult Show(string text, string caption = "", MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None) => Show(null, text, caption, buttons, icon);

    internal static DialogResult Show(IWin32Window? owner, string text, string caption = "", MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        if (!UiTheme.IsDark || SystemInformation.HighContrast) return MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
        using var dialog = CreateDialog(text, caption, buttons, icon, defaultButton);
        return dialog.ShowDialog(owner);
    }

    internal static Form CreateDialog(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        var form = new Form { Text = caption, Font = UiTheme.BodyFont(), FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false };
        int textWidth = icon == MessageBoxIcon.None ? 512 : 474;
        int contentHeight = Math.Clamp(TextRenderer.MeasureText(text, form.Font, new Size(textWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 12, 55, 440);
        form.ClientSize = new Size(560, contentHeight + 108);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 1 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, icon == MessageBoxIcon.None ? 0 : 38));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (icon != MessageBoxIcon.None)
            content.Controls.Add(new Label { Text = icon == MessageBoxIcon.Error ? "!" : icon == MessageBoxIcon.Warning ? "!" : icon == MessageBoxIcon.Question ? "?" : "i",
                Font = new Font("Segoe UI", 20, FontStyle.Bold), ForeColor = icon == MessageBoxIcon.Error ? UiTheme.Danger :
                    icon == MessageBoxIcon.Warning ? UiTheme.Warning : UiTheme.Accent, AutoSize = true, Margin = Padding.Empty }, 0, 0);
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var body = new Label { Text = text, AutoSize = true, MaximumSize = new Size(textWidth, 0), Margin = Padding.Empty,
            UseMnemonic = false, AccessibleRole = AccessibleRole.StaticText };
        viewport.Controls.Add(body);
        content.Controls.Add(viewport, 1, 0);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(16, 10, 16, 12),
            FlowDirection = FlowDirection.RightToLeft, BackColor = UiTheme.Surface };
        (string, DialogResult)[] choices = buttons switch
        {
            MessageBoxButtons.YesNo => [("Ja", DialogResult.Yes), ("Nein", DialogResult.No)],
            MessageBoxButtons.YesNoCancel => [("Ja", DialogResult.Yes), ("Nein", DialogResult.No), ("Abbrechen", DialogResult.Cancel)],
            MessageBoxButtons.OKCancel => [("OK", DialogResult.OK), ("Abbrechen", DialogResult.Cancel)],
            MessageBoxButtons.RetryCancel => [("Wiederholen", DialogResult.Retry), ("Abbrechen", DialogResult.Cancel)],
            _ => [("OK", DialogResult.OK)]
        };
        foreach (var (label, result) in choices.Reverse())
        {
            var button = new AppButton { Text = label, DialogResult = result, MinimumSize = new Size(94, 34),
                Role = result == choices[0].Item2 ? ButtonRole.Primary : ButtonRole.Normal, Margin = new Padding(8, 0, 0, 0) };
            footer.Controls.Add(button);
            int defaultIndex = Math.Clamp((int)defaultButton / 256, 0, choices.Length - 1);
            if (result == choices[defaultIndex].Item2) form.AcceptButton = button;
            if (result == DialogResult.Cancel || choices.Length == 1 || buttons == MessageBoxButtons.YesNo && result == DialogResult.No)
                form.CancelButton = button;
        }
        form.Controls.Add(content);
        form.Controls.Add(footer);
        form.Shown += (_, _) => ((Control)form.AcceptButton!).Focus();
        form.KeyPreview = true;
        form.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.C) return;
            try { Clipboard.SetText(caption + Environment.NewLine + Environment.NewLine + text); }
            catch (System.Runtime.InteropServices.ExternalException) { /* Match native dialogs when the clipboard is busy. */ }
            e.Handled = e.SuppressKeyPress = true;
        };
        WindowLayout.CompleteInitialization(form);
        return form;
    }
}
