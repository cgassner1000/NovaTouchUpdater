namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private readonly Dictionary<DataGridViewColumn, int> columnContentWidths = [];
    private readonly Dictionary<(Font Font, string Text, int Dpi), int> columnTextWidths = [];
    private bool columnSizingQueued;
    private float columnMeasurementScale;

    private void InvalidateColumnContent(DataGridViewColumn? column = null, bool clearTextMeasurements = false)
    {
        if (column is null) columnContentWidths.Clear();
        else columnContentWidths.Remove(column);
        if (clearTextMeasurements) columnTextWidths.Clear();
        QueueColumnSizing();
    }

    private void QueueColumnSizing()
    {
        if (columnSizingQueued || !table.IsHandleCreated || table.IsDisposed) return;
        columnSizingQueued = true;
        try
        {
            table.BeginInvoke((Action)(() =>
            {
                columnSizingQueued = false;
                if (!table.IsDisposed) ApplyColumnWidths();
            }));
        }
        catch (InvalidOperationException) { columnSizingQueued = false; }
    }

    private int ColumnContentWidth(DataGridViewColumn column, Graphics graphics)
    {
        if (columnContentWidths.TryGetValue(column, out int width)) return width;
        foreach (DataGridViewRow row in table.Rows)
        {
            if (!row.Visible || row.IsNewRow) continue;
            DataGridViewCell cell = row.Cells[column.Index];
            string text = Convert.ToString(cell.Value) ?? "";
            if (text.Length == 0) continue;
            DataGridViewCellStyle style = cell.InheritedStyle;
            Font font = style.Font ?? table.Font;
            foreach (string line in text.Split('\n'))
            {
                var key = (font, line.TrimEnd('\r'), table.DeviceDpi);
                if (!columnTextWidths.TryGetValue(key, out int textWidth))
                {
                    textWidth = TextRenderer.MeasureText(graphics, key.Item2, font, Size.Empty,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
                    columnTextWidths.Add(key, textWidth);
                }
                width = Math.Max(width, textWidth + style.Padding.Horizontal + 2);
            }
        }
        columnContentWidths[column] = width;
        return width;
    }

    private void ApplyColumnWidths()
    {
        if (fittingColumns || table.Columns.Count == 0 || table.ClientSize.Width <= 0 || table.IsDisposed) return;
        fittingColumns = true;
        table.SuspendLayout();
        try
        {
            float scale = table.DeviceDpi / 96F * table.Font.SizeInPoints / 9.5F;
            if (columnMeasurementScale != scale)
            {
                columnMeasurementScale = scale;
                columnContentWidths.Clear(); columnTextWidths.Clear();
            }
            int padding = Math.Max(1, (int)Math.Ceiling(5 * scale));
            int gap = Math.Max(2, (int)Math.Ceiling(6 * scale));
            table.ColumnHeadersDefaultCellStyle.Padding = new Padding(padding, (int)(11 * scale), padding, (int)(11 * scale));
            table.DefaultCellStyle.Padding = new Padding(padding, (int)(9 * scale), padding, (int)(9 * scale));
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            int scrollbar = table.Controls.OfType<VScrollBar>().Where(s => s.Visible).Sum(s => s.Width);
            int border = table.BorderStyle == BorderStyle.None ? 0 : SystemInformation.BorderSize.Width * 2;
            int available = Math.Max(1, table.ClientSize.Width - scrollbar - border);
            int checkbox = (int)Math.Ceiling(26 * table.DeviceDpi / 96F);
            var columns = table.Columns.Cast<DataGridViewColumn>().ToArray();
            var minimums = new int[columns.Length];
            var desired = new int[columns.Length];
            int info = table.Columns["information"].Index;
            using var graphics = table.CreateGraphics();
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i].Name == "include") { minimums[i] = desired[i] = checkbox; continue; }
                minimums[i] = ((SizingColumnHeaderCell)columns[i].HeaderCell).SingleLineWidth(graphics, table.ColumnHeadersDefaultCellStyle.Padding) + gap;
                desired[i] = i == info ? minimums[i] : Math.Max(minimums[i], ColumnContentWidth(columns[i], graphics) + gap);
            }
            bool narrow = minimums.Sum() > available;
            table.ColumnHeadersDefaultCellStyle.WrapMode = narrow ? DataGridViewTriState.True : DataGridViewTriState.False;
            int[] widths;
            if (narrow)
            {
                // A narrow window cannot fit every one-line heading. Wrap them
                // while preserving the checkbox and keeping Status in view.
                double fit = Math.Max(0, available - checkbox) / (double)Math.Max(1, minimums.Sum() - checkbox);
                widths = minimums.Select((width, i) => i == 0 ? checkbox : Math.Max(2, (int)(width * fit))).ToArray();
            }
            else
            {
                int budget = available - minimums.Sum();
                int[] growth = desired.Select((width, i) => width - minimums[i]).ToArray();
                int low = 0, high = growth.Max();
                // Short values keep their natural width; unusually long values
                // share the remaining space and wrap instead of hiding Status.
                while (low < high)
                {
                    int limit = low + (high - low + 1) / 2;
                    if (growth.Sum(value => (long)Math.Min(value, limit)) <= budget) low = limit;
                    else high = limit - 1;
                }
                widths = minimums.Select((width, i) => width + Math.Min(growth[i], low)).ToArray();
            }
            // Information alone receives all surplus width, including rounding.
            widths[info] += available - widths.Sum();
            for (int i = 0; i < columns.Length; i++)
            {
                DataGridViewColumn column = columns[i];
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.NotSet;
                column.MinimumWidth = narrow ? Math.Max(2, widths[i]) : minimums[i];
                if (column.Width != widths[i]) column.Width = widths[i];
            }
            table.HorizontalScrollingOffset = 0;
        }
        finally { table.ResumeLayout(true); fittingColumns = false; }
    }
}
