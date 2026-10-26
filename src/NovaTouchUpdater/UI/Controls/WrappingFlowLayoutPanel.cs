namespace NovaTouchUpdater;

// Nested AutoSize tables can ask for an unconstrained preferred size. Measure wrapping
// against the assigned width so a button bar does not reserve one row per button.
internal sealed class WrappingFlowLayoutPanel : FlowLayoutPanel
{
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (WrapContents) proposedSize = new Size(Math.Max(1, Width), 0);
        return base.GetPreferredSize(proposedSize);
    }
}
