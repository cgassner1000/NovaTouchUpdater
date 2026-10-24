namespace NovaTouchUpdater;

internal sealed class UpdaterHeader : TableLayoutPanel
{
    private readonly WrappingFlowLayoutPanel caption;
    private readonly Label title;
    private readonly Label subtitle;
    private readonly LinkLabel notice;
    private bool arranging;

    internal UpdaterHeader(LinkLabel notice)
    {
        this.notice = notice;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 2;
        RowCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Margin = new Padding(3, 0, 3, 12);
        caption = new WrappingFlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        title = new Label { Text = "NovaTouch 2.5 Updater", Font = new Font("Segoe UI", 19F, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Left };
        subtitle = new Label
        {
            Text = "nt.payment / nt.fiscal / nt3 Server Suite    ·    parallele Remote-Updates",
            AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(95, 110, 130)
        };
        caption.Controls.AddRange([title, subtitle]);
        notice.Anchor = AnchorStyles.Right;
        notice.AutoSize = true;
        notice.TextAlign = ContentAlignment.MiddleRight;
        notice.Margin = new Padding(12, 3, 3, 3);
        Controls.Add(caption, 0, 0);
        Controls.Add(notice, 1, 0);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (!arranging && caption is not null)
        {
            arranging = true;
            try
            {
                int captionWidth = TextRenderer.MeasureText(title.Text, title.Font).Width + TextRenderer.MeasureText(subtitle.Text, subtitle.Font).Width + 12;
                bool separateRow = notice.Visible && ClientSize.Width < captionWidth + notice.GetPreferredSize(Size.Empty).Width + notice.Margin.Horizontal;
                SetColumnSpan(caption, separateRow ? 2 : 1);
                SetCellPosition(notice, separateRow ? new TableLayoutPanelCellPosition(0, 1) : new TableLayoutPanelCellPosition(1, 0));
                SetColumnSpan(notice, separateRow ? 2 : 1);
                int available = Math.Max(1, ClientSize.Width - (separateRow || !notice.Visible ? 0 : notice.GetPreferredSize(Size.Empty).Width + notice.Margin.Horizontal) - 12);
                title.MaximumSize = new Size(available, 0);
                subtitle.MaximumSize = new Size(available, 0);
            }
            finally { arranging = false; }
        }
        base.OnLayout(levent);
    }
}
