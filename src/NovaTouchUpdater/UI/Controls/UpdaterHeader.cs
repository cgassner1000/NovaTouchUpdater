namespace NovaTouchUpdater;

internal sealed class UpdaterHeader : TableLayoutPanel
{
    private readonly Label title;
    private readonly LinkLabel notice;
    private readonly FlowLayoutPanel actions;
    private bool arranging;

    internal UpdaterHeader(LinkLabel notice, Button feedback, Button administrator)
    {
        this.notice = notice;
        Name = "updaterHeader";
        feedback.Name = "reportFeedback";
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
        Margin = new Padding(0, 0, 0, 8);
        var mark = new AppMark { Margin = new Padding(0, 4, 14, 0) };
        Controls.Add(mark, 0, 0);
        SetRowSpan(mark, 2);
        title = new Label
        {
            Text = "NovaTouch Updater", Font = new Font("Segoe UI", 22F, FontStyle.Bold),
            AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty
        };
        Controls.Add(title, 1, 0);
        actions = new FlowLayoutPanel
        {
            Name = "headerActions", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right, WrapContents = false, Margin = new Padding(16, 0, 0, 0)
        };
        administrator.Anchor = AnchorStyles.None;
        administrator.Margin = Padding.Empty;
        feedback.Anchor = AnchorStyles.None;
        feedback.Margin = new Padding(16, 0, 0, 0);
        actions.Controls.Add(administrator);
        actions.Controls.Add(feedback);
        Controls.Add(actions, 2, 0);
        notice.Anchor = AnchorStyles.Right;
        notice.AutoSize = true;
        notice.TextAlign = ContentAlignment.MiddleRight;
        notice.Padding = new Padding(12, 6, 12, 6);
        notice.BackColor = UiTheme.AccentSoft;
        notice.Margin = new Padding(16, 0, 0, 0);
        Controls.Add(notice, 1, 1);
        SetColumnSpan(notice, 2);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (!arranging && notice?.Parent == this)
        {
            arranging = true;
            try
            {
                int markWidth = Controls[0].Width + Controls[0].Margin.Horizontal;
                int feedbackWidth = actions.Controls.Cast<Control>().Where(control => control.Visible)
                    .Sum(control => control.GetPreferredSize(Size.Empty).Width + control.Margin.Horizontal) + actions.Margin.Horizontal;
                bool separateFeedback = ClientSize.Width - markWidth < TextRenderer.MeasureText(title.Text, title.Font).Width + feedbackWidth;
                actions.MaximumSize = new Size(Math.Max(1, ClientSize.Width - markWidth - actions.Margin.Horizontal), 0);
                SetCellPosition(actions, separateFeedback ? new TableLayoutPanelCellPosition(1, 2) : new TableLayoutPanelCellPosition(2, 0));
                SetColumnSpan(actions, separateFeedback ? 2 : 1);
                SetColumnSpan(title, separateFeedback ? 2 : 1);
                title.MaximumSize = new Size(Math.Max(1, ClientSize.Width - markWidth - (separateFeedback ? 0 : feedbackWidth)), 0);
                notice.MaximumSize = new Size(Math.Max(1, ClientSize.Width - markWidth - notice.Margin.Horizontal), 0);
            }
            finally { arranging = false; }
        }
        base.OnLayout(levent);
    }
}
