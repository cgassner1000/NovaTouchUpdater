namespace NovaTouchUpdater;

internal sealed class UpdaterHeader : TableLayoutPanel
{
    private readonly Label subtitle;
    private readonly LinkLabel notice;
    private bool arranging;

    internal UpdaterHeader(LinkLabel notice)
    {
        this.notice = notice;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 3;
        RowCount = 3;
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Margin = new Padding(0, 0, 0, 22);
        var mark = new AppMark { Margin = new Padding(0, 4, 14, 0) };
        Controls.Add(mark, 0, 0);
        SetRowSpan(mark, 2);
        var title = new Label
        {
            Text = "NovaTouch Updater", Font = new Font("Segoe UI", 22F, FontStyle.Bold),
            AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty
        };
        Controls.Add(title, 1, 0);
        SetColumnSpan(title, 2);
        subtitle = new Label
        {
            Text = "ZIP- und MSI-Updates    ·    lokal und remote",
            AutoSize = true, Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, Margin = new Padding(0, 5, 0, 0)
        };
        Controls.Add(subtitle, 1, 1);
        notice.Anchor = AnchorStyles.Right;
        notice.AutoSize = true;
        notice.TextAlign = ContentAlignment.MiddleRight;
        notice.Padding = new Padding(12, 6, 12, 6);
        notice.BackColor = UiTheme.AccentSoft;
        notice.Margin = new Padding(16, 0, 0, 0);
        Controls.Add(notice, 2, 1);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (!arranging && subtitle is not null)
        {
            arranging = true;
            try
            {
                int markWidth = Controls[0].Width + Controls[0].Margin.Horizontal;
                int noticeWidth = notice.GetPreferredSize(Size.Empty).Width + notice.Margin.Horizontal;
                bool separateRow = notice.Visible && ClientSize.Width - markWidth < TextRenderer.MeasureText(subtitle.Text, subtitle.Font).Width + noticeWidth;
                SetCellPosition(notice, separateRow ? new TableLayoutPanelCellPosition(1, 2) : new TableLayoutPanelCellPosition(2, 1));
                SetColumnSpan(notice, separateRow ? 2 : 1);
                SetColumnSpan(subtitle, separateRow || !notice.Visible ? 2 : 1);
                subtitle.MaximumSize = new Size(Math.Max(1, ClientSize.Width - markWidth - (!separateRow && notice.Visible ? noticeWidth : 0)), 0);
            }
            finally { arranging = false; }
        }
        base.OnLayout(levent);
    }
}
