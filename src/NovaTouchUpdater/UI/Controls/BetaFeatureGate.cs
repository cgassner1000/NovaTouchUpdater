namespace NovaTouchUpdater;

// Session-only opt-in. Register future beta controls here instead of scattering
// independent visibility switches throughout the form.
internal sealed class BetaFeatureGate : IMessageFilter, IDisposable
{
    private readonly Form owner;
    private readonly Control image;
    private readonly List<Control> controls = [];
    private int clicks;
    internal bool Enabled { get; private set; }
    internal event Action? Activated;

    internal BetaFeatureGate(Form owner, Control image)
    {
        this.owner = owner;
        this.image = image;
        owner.Deactivate += ResetClicks;
        Application.AddMessageFilter(this);
    }

    internal void Register(Control control) { controls.Add(control); ApplyVisibility(); }
    internal void ApplyVisibility() { foreach (Control control in controls) control.Visible = Enabled; }

    internal void RegisterClick(Control? target, bool leftButton)
    {
        if (Enabled) return;
        clicks = leftButton && target == image ? clicks + 1 : 0;
        if (clicks != 5) return;
        Enabled = true;
        ApplyVisibility();
        Activated?.Invoke();
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg is 0x0201 or 0x0203 or 0x0204 or 0x0207)
            RegisterClick(Control.FromChildHandle(message.HWnd), message.Msg is 0x0201 or 0x0203);
        return false;
    }

    private void ResetClicks(object? sender, EventArgs e) => clicks = 0;
    public void Dispose() { Application.RemoveMessageFilter(this); owner.Deactivate -= ResetClicks; }
}
