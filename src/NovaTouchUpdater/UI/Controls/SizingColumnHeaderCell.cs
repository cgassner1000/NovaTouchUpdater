namespace NovaTouchUpdater;

internal sealed class SizingColumnHeaderCell : DataGridViewColumnHeaderCell
{
    // Measure an unwrapped heading without changing the live grid's styles.
    // Temporarily switching wrapping can trigger another layout while resizing.
    internal int SingleLineWidth(Graphics graphics, Padding padding)
    {
        DataGridViewCellStyle style = InheritedStyle.Clone();
        style.WrapMode = DataGridViewTriState.False;
        style.Padding = padding;
        return GetPreferredSize(graphics, style, -1, Size.Empty).Width;
    }
}
