using NovaTouchUpdater;

internal static class InstallationProgressTests
{
    internal static void Run(Action<bool, string> assert)
    {
        foreach (UpdateComponents component in HostUpdateState.Each(UpdateComponents.All))
        {
            var state = new HostUpdateState(component);
            state.BeginComponent(component);
            int previous = 0;
            WorkerEvent[] steps =
            [
                new() { Phase="Übertragung", ProgressPercent=0 },
                new() { Phase="Übertragung", ProgressPercent=100 },
                new() { Phase="Archive entpacken", ProgressPercent=0 },
                new() { Phase="Archive entpacken", ProgressPercent=100 },
                new() { Phase="Dienste stoppen", ProgressPercent=50 },
                new() { Phase="Update installieren", ProgressPercent=0 },
                new() { Phase="Update installieren", ProgressPercent=100 },
                new() { Phase="Dienste starten", ProgressPercent=50 },
                new() { Phase="Dienste starten", ProgressPercent=100 },
                new() { Phase="Verifikation" },
                new() { Phase="Instanz geprüft", ProgressPercent=100 }
            ];
            foreach (WorkerEvent step in steps)
            {
                int? raw = step.ProgressPercent;
                state.ReportProgress(component, step);
                int current = state.ProgressFor(component)!.Value.Percent!.Value;
                assert(current >= previous && current < 100 && step.ProgressPercent == raw,
                    component + ": total progress stays monotonic and below 100 while raw step progress is preserved (" + step.Phase + ")");
                previous = current;
            }
            state.Complete(component, new() { Success=true, Changed=true });
            assert(state.ProgressFor(component) is { Status: ComponentUpdateStatus.Succeeded, Percent: 100 }, component + ": only confirmed worker success fills the whole bar");
        }

        var msi = new HostUpdateState(UpdateComponents.Ntc);
        msi.BeginComponent(UpdateComponents.Ntc);
        msi.ReportProgress(UpdateComponents.Ntc, new() { Phase="MSI installieren", ProgressPercent=0 });
        assert(msi.ProgressFor(UpdateComponents.Ntc)!.Value.Percent is > 0 and < 100, "an MSI without inner installer percentages still displays its reached installation milestone");
        int reached = msi.ProgressFor(UpdateComponents.Ntc)!.Value.Percent!.Value;
        msi.ReportProgress(UpdateComponents.Ntc, new() { Phase="Unbekannter Schritt" });
        assert(msi.ProgressFor(UpdateComponents.Ntc)!.Value.Percent == reached, "a message without a percentage cannot clear the reached progress");
        msi.ReportProgress(UpdateComponents.Ntc, new() { Phase="Übertragung", ProgressPercent=0 });
        assert(msi.ProgressFor(UpdateComponents.Ntc)!.Value.Percent == reached, "a repeated stage cannot move the overall bar backwards");
        msi.ReportProgress(UpdateComponents.Ntc, new() { Phase="MSI verifiziert", ProgressPercent=100 });
        assert(msi.ProgressFor(UpdateComponents.Ntc)!.Value.Percent == 99, "an installer progress event cannot substitute for the final worker result");
        msi.Fail(UpdateComponents.Ntc, "verification failed");
        assert(msi.ProgressFor(UpdateComponents.Ntc) is { Status: ComponentUpdateStatus.Failed, Percent: null } && msi.Pending == UpdateComponents.Ntc,
            "a late verification failure stays failed and pending instead of showing a full green bar");
        msi.BeginComponent(UpdateComponents.Ntc, 2);
        assert(msi.ProgressFor(UpdateComponents.Ntc) is { Percent: 0, Attempt: 2 }, "a new installation attempt starts its own progress at zero");

        var suite = new HostUpdateState(UpdateComponents.ServerSuite | UpdateComponents.Payment);
        suite.BeginComponent(UpdateComponents.ServerSuite | UpdateComponents.Payment);
        foreach (int percent in new[] { 25, 39, 57, 76, 95 })
        {
            var ev = System.Text.Json.JsonSerializer.Deserialize<WorkerEvent>("{\"Phase\":\"Setup installieren\",\"ComponentProgressPercent\":" + percent + "}")!;
            suite.ReportProgress(UpdateComponents.ServerSuite, ev);
            assert(suite.ProgressFor(UpdateComponents.ServerSuite)!.Value.Percent == percent && suite.ProgressFor(UpdateComponents.Payment)!.Value.Percent == 0,
                "aggregated Suite instance progress survives the JSON protocol without changing another component");
        }
        suite.ReportProgress(UpdateComponents.ServerSuite, new() { ComponentProgressPercent=100 });
        assert(suite.ProgressFor(UpdateComponents.ServerSuite)!.Value.Percent == 99, "even an explicit total percentage remains below 100 until successful completion");
        suite.StopActiveComponents();
        assert(suite.ProgressFor(UpdateComponents.ServerSuite) is { Status: ComponentUpdateStatus.Cancelled, Percent: null }, "cancellation removes the active progress fill and retains the cancelled state");
    }
}
