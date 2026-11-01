using NovaTouchUpdater;
using System.Collections.Concurrent;

internal static partial class Program
{
    private static void UpdateFinalizationTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "final-phases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string msi = Path.Combine(root, "Control.msi");
            File.WriteAllText(msi, "fixture payload, never installed");
            var worker = new FinalizationRunner();
            using var form = new MainForm(preview:true, worker, new FakeRunner());
            _ = form.Handle;
            Assert(Field<CheckBox>(form, "chkUploadToZzzSoftware").Checked && !Field<CheckBox>(form, "chkRestartPosAfterUpdate").Checked,
                "database upload defaults on and automatic POS restart defaults off");
            Field<CheckBox>(form, "chkRestartPosAfterUpdate").Checked = true;
            Field<CheckBox>(form, "chkSkipLocalPackageCopy").Checked = true;
            Field<TextBox>(form, "txtUser").Text = "fixture-user";
            Field<TextBox>(form, "txtPassword").Text = "fixture-password";
            Field<CheckBox>(form, "chkSsl").Checked = true;
            Field<NumericUpDown>(form, "nHttpsPort").Value = 6001;
            Field<NumericUpDown>(form, "nUpdate").Value = 2;
            SetField(form, "createPosUpdateRunner", new Func<IWorkerRunner>(() => worker));
            Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages").Add(UpdateComponents.Ntc,
                new() { Kind="ntc", ProductName="NovaTouch Control", ProductVersion="2.0.0", PayloadPath=msi });
            SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ => Task.FromResult(new[] {
                new IrisUploadTarget { Instance="TEST", Directory=root, Port=1972, Namespaces=["APP"] }
            })));
            Call(form, "CaptureCredentials");
            HostState[] hosts = [new() { Host="LOCALHOST", Scanned=true }, new() { Host="REMOTE-A", Scanned=true }, new() { Host="REMOTE-B", Scanned=true }];
            foreach (HostState host in hosts) host.Updates = new(UpdateComponents.None);
            worker.FailedUploadHost = "LOCALHOST";
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Count(request => request.Action == "publish-msi-packages") == 3 && worker.Requests.All(request => request.Action != "restart-pos"),
                "all database uploads finish before any POS restart, and an upload failure blocks remote POS restarts");
            Assert(hosts.All(host => host.CanRetryUpdate) && hosts.All(host => host.Updates!.Pending == UpdateComponents.None),
                "upload errors and waiting POS restarts remain retryable without reopening installed components");
            worker.Requests.Clear(); worker.FailedUploadHost = "";
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Select(request => request.Action).SequenceEqual(["publish-msi-packages", "restart-pos", "restart-pos"]) &&
                worker.Requests.First().LocalExecution && worker.Requests.Where(request => request.Action == "restart-pos").All(request => request.Host != "LOCALHOST" && request.Username == "fixture-user" && request.UseSsl && request.HttpsPort == 6001) &&
                hosts.All(host => !host.CanRetryUpdate),
                "retry only completes the failed upload then restarts remote POS with configured WinRM credentials, never the local POS");
            Assert(hosts.Skip(1).All(host => host.Result == "Erfolgreich" && host.Phase.Contains("POS-Neustart")),
                "successful automatic POS restarts remain visible in the final host status");
            worker.Requests.Clear();
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Count == 0, "completed uploads and POS restarts are never repeated by a finalization retry");

            HostUpdateState localFailure = hosts[0].Updates = new(UpdateComponents.Payment);
            localFailure.Fail(UpdateComponents.Payment, "Payment service failed");
            hosts[0].UpdateFailed = true;
            HostUpdateState remoteFailure = hosts[1].Updates = new(UpdateComponents.Fiscal);
            remoteFailure.Fail(UpdateComponents.Fiscal, "Fiscal service failed");
            hosts[1].UpdateFailed = true;
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Count(request => request.Action == "publish-msi-packages") == 2 &&
                worker.Requests.Where(request => request.Action == "publish-msi-packages").Select(request => request.Host).ToHashSet().SetEquals(["LOCALHOST", "REMOTE-A"]) &&
                worker.Requests.Last() is { Action:"restart-pos", Host:"REMOTE-A" },
                "failed local and remote service updates still upload packages and allow remote POS restart after successful uploads");
            Assert(localFailure.DatabaseUploadCompleted && remoteFailure.DatabaseUploadCompleted &&
                hosts[0].Result == "Fehler" && hosts[1].Result == "Fehler" && hosts[0].Error.Contains("Payment service failed") && hosts[1].Error.Contains("Fiscal service failed"),
                "successful database upload and POS restart preserve each failed service and its final error");
            localFailure.Complete(UpdateComponents.Payment, new WorkerResult { Success=true });
            remoteFailure.Complete(UpdateComponents.Fiscal, new WorkerResult { Success=true });
            hosts[0].UpdateFailed = false;
            worker.Requests.Clear();
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Count == 0 && hosts.All(host => !host.CanRetryUpdate),
                "retrying a failed service does not repeat a database upload or POS restart that already succeeded");

            localFailure = hosts[0].Updates = new(UpdateComponents.Payment);
            localFailure.Fail(UpdateComponents.Payment, "Payment service failed");
            hosts[0].UpdateFailed = true;
            worker.FailedUploadHost = "LOCALHOST";
            FinishUpdates(form, hosts[0]);
            Assert(!localFailure.DatabaseUploadCompleted && hosts[0].Error.Contains("Payment service failed") && hosts[0].Error.Contains("Synthetic final step failure"),
                "simultaneous service and database errors are both retained at the end");
            worker.Requests.Clear(); worker.FailedUploadHost = "";
            FinishUpdates(form, hosts[0]);
            Assert(worker.Requests.ToArray() is [{ Action:"publish-msi-packages" }] && localFailure.DatabaseUploadCompleted &&
                localFailure.Pending == UpdateComponents.Payment && hosts[0].Error.Contains("Payment service failed") && !hosts[0].Error.Contains("Synthetic final step failure"),
                "a failed database upload can succeed on retry while the service error remains independently pending");

            Field<CheckBox>(form, "chkUploadToZzzSoftware").Checked = false;
            Call(form, "CaptureCredentials");
            worker.Requests.Clear();
            worker.FailedPosHost = "REMOTE-A";
            foreach (HostState host in hosts) host.Updates = new(UpdateComponents.None);
            File.Delete(msi);
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Count == 2 && worker.Requests.All(request => request.Action == "restart-pos") && hosts[1].CanRetryUpdate && !hosts[2].CanRetryUpdate,
                "disabled database upload reads no MSI sources while POS restarts stay independent and failures stay per host");
            worker.Requests.Clear(); worker.FailedPosHost = "";
            FinishUpdates(form, hosts);
            Assert(worker.Requests.ToArray() is [{ Action:"restart-pos", Host:"REMOTE-A" }] && hosts.All(host => !host.CanRetryUpdate),
                "retry after a POS error only restarts the failed remote POS");

            string config = Path.Combine(root, "config.json");
            AppConfigStore.Save(config, (UpdaterConfig)Call(form, "CaptureConfig")!, "");
            using var restored = new MainForm(preview:true, new FakeRunner(), new FakeRunner());
            Call(restored, "ApplyLoadedConfig", AppConfigStore.Load(config));
            Assert(!Field<CheckBox>(restored, "chkUploadToZzzSoftware").Checked && Field<CheckBox>(restored, "chkRestartPosAfterUpdate").Checked,
                "the form captures and restores both final-step toggles through config.json");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FinalizationRunner : IWorkerRunner
    {
        internal ConcurrentQueue<WorkerRequest> Requests { get; } = new();
        internal string FailedUploadHost = "", FailedPosHost = "";
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (request.Action == "restart-pos") Assert(!ct.CanBeCanceled, "accepted POS restarts finish their cleanup even when the batch is cancelled");
            bool failure = request.Action == "publish-msi-packages" ? request.Host == FailedUploadHost : request.Host == FailedPosHost;
            return Task.FromResult(new WorkerResult { Success=!failure, Error=failure ? "Synthetic final step failure" : "", Changed=!failure });
        }
        public void Dispose() { }
    }
}
