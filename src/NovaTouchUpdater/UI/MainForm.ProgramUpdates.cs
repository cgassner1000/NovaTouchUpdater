namespace NovaTouchUpdater;

public sealed partial class MainForm
{
    private static readonly (string Column, UpdateComponents Component)[] programColumns =
    [
        ("payment", UpdateComponents.Payment), ("fiscal", UpdateComponents.Fiscal), ("serversuite", UpdateComponents.ServerSuite),
        ("ntc", UpdateComponents.Ntc), ("pos", UpdateComponents.Pos), ("ntv", UpdateComponents.Ntv), ("oman", UpdateComponents.Oman)
    ];

    private void PaintProgramUpdates(HostState host)
    {
        foreach (var (column, component) in programColumns)
        {
            ComponentUpdateProgress? progress;
            string version;
            lock (host)
            {
                progress = host.Updates?.ProgressFor(component);
                version = component switch
                {
                    UpdateComponents.Payment => host.Payment, UpdateComponents.Fiscal => host.Fiscal,
                    UpdateComponents.ServerSuite => host.ServerSuite, UpdateComponents.Pos => host.PosVersion,
                    UpdateComponents.Ntc => host.NtcVersion, UpdateComponents.Ntv => host.NtvVersion, UpdateComponents.Oman => host.OmanVersion, _ => "–"
                };
            }
            var cell = host.Row!.Cells[column];
            if (!Equals(cell.Tag, progress))
            {
                cell.Tag = progress;
                table.InvalidateCell(cell);
            }
            string status = progress?.Status switch
            {
                ComponentUpdateStatus.Updating => "Update …",
                ComponentUpdateStatus.Failed => "Fehlgeschlagen",
                ComponentUpdateStatus.Cancelled => "Abgebrochen",
                _ => "Update verifiziert"
            };
            cell.Value = progress is { Status: not ComponentUpdateStatus.Succeeded } ? status : version;
            if (progress is not { } current) continue;
            string percent = current.Status == ComponentUpdateStatus.Updating && current.Percent is int value ? " · " + value + " % (Gesamtfortschritt)" : "";
            string step = current.Status == ComponentUpdateStatus.Updating && current.StepPercent is int stepValue && stepValue != current.Percent
                ? "Aktueller Schritt: " + stepValue + " %" : "";
            cell.ToolTipText = string.Join(Environment.NewLine, new[]
            {
                cell.ToolTipText.Length > 0 ? cell.ToolTipText : version,
                DisplayText.WithAttempt(status + percent, current.Attempt), current.Phase, step, DisplayText.Clean(current.Message)
            }.Where(line => line.Length > 0));
        }
    }
}
