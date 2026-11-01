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
            string msi = Path.Combine(root, "POS.msi");
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
            Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages").Add(UpdateComponents.Pos,
                new() { Kind="pos", ProductName="NovaTouch POS", ProductVersion="2.0.0", PayloadPath=msi });
            SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ => Task.FromResult(new[] {
                new IrisUploadTarget { Instance="TEST", Directory=root, Port=1972, Namespaces=["APP"] }
            })));
            Call(form, "CaptureCredentials");
            HostState[] hosts = [new() { Host="LOCALHOST", Scanned=true }, new() { Host="REMOTE-A", Scanned=true }, new() { Host="REMOTE-B", Scanned=true }];
            foreach (HostState host in hosts) { host.PosVersion="1.0.0"; host.Updates = new(UpdateComponents.None); }
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
            Assert(worker.Requests.Count == 0 && hosts.All(host => !host.CanRetryUpdate) &&
                hosts.All(host => host.Phase.Contains("Upload ausgeschaltet")) && hosts.Skip(1).All(host => host.Phase.Contains("POS-Neustart übersprungen")),
                "disabled database upload reads no MSI sources, explains its skip and never restarts POS");

            void NewRun()
            {
                Guid runId = Guid.NewGuid();
                foreach (HostState host in hosts) host.Updates = new(UpdateComponents.None) { RunId=runId };
                worker.Requests.Clear(); worker.UnchangedUploadHosts.Clear(); worker.UploadMessage=""; worker.FailedPosHost=""; worker.UploadedKindsOverride=null;
            }
            Field<CheckBox>(form, "chkUploadToZzzSoftware").Checked = true;
            Call(form, "CaptureCredentials");
            var packages = Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages");
            packages.Clear(); NewRun();
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Count == 0 && hosts.All(host => host.Phase.Contains("keine MSI-Pakete")) && hosts.Skip(1).All(host => !host.Updates!.PosRestartCompleted),
                "enabled upload without selected MSIs visibly explains its skip and never restarts POS");

            File.WriteAllText(msi, "fixture payload, never installed");
            packages.Add(UpdateComponents.Pos, new() { Kind="pos", ProductName="NovaTouch POS", ProductVersion="2.0.0", PayloadPath=msi });
            SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ => Task.FromResult(Array.Empty<IrisUploadTarget>())));
            NewRun();
            foreach (HostState host in hosts) worker.UnchangedUploadHosts.Add(host.Host);
            worker.UploadMessage = "Keine IRIS-Namespaces auf dem Zielrechner; kein Datenbank-Upload erforderlich.";
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Count == 2 && worker.Requests.All(request => request.Action == "publish-msi-packages") &&
                hosts.All(host => host.Phase.Contains("IRIS")) && hosts.Skip(1).All(host => host.Phase.Contains("POS-Neustart übersprungen")),
                "no IRIS namespaces locally or remotely cannot authorize a POS restart and remain visible in final information");

            SetField(form, "prepareDatabaseTargets", new Func<CancellationToken, Task<IrisUploadTarget[]>>(_ => Task.FromResult(new[] {
                new IrisUploadTarget { Instance="TEST", Directory=root, Port=1972, Namespaces=["APP"] }
            })));
            NewRun();
            foreach (HostState host in hosts) worker.UnchangedUploadHosts.Add(host.Host);
            worker.UploadMessage = "Vorhandene Pakete sind gleich/neuer; kein MSI hochgeladen.";
            hosts[1].Updates!.Complete(UpdateComponents.Payment, new WorkerResult { Success=true, Changed=true });
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Count == 3 && worker.Requests.All(request => request.Action == "publish-msi-packages") &&
                hosts.All(host => host.Phase.Contains("gleich/neuer")),
                "successful version checks without actual MSI upload never restart POS, even after changed service updates");

            NewRun();
            worker.UnchangedUploadHosts.UnionWith(["REMOTE-A", "REMOTE-B"]);
            worker.FailedPosHost="REMOTE-A";
            FinishUpdates(form, hosts);
            Assert(worker.Requests.Take(3).All(request => request.Action == "publish-msi-packages") && worker.Requests.Count == 5 &&
                hosts[1].CanRetryUpdate && !hosts[2].CanRetryUpdate && hosts.Skip(1).All(host => host.Updates!.SuccessfulPosUploadInRun),
                "actual server upload authorizes POS devices without their own database only after all uploads finish");
            worker.Requests.Clear(); worker.FailedPosHost="";
            FinishUpdates(form, hosts[1]);
            Assert(worker.Requests.ToArray() is [{ Action:"restart-pos", Host:"REMOTE-A" }] && hosts.All(host => !host.CanRetryUpdate),
                "POS-only retry retains proof of the earlier server upload without uploading again");

            NewRun();
            hosts[0].Updates!.CompleteDatabaseUpload(true, "Earlier successful upload");
            hosts[0].Updates!.RecordDatabaseUploads(["pos"]);
            hosts[1].Updates = new(UpdateComponents.None); // Different update run.
            worker.UnchangedUploadHosts.Add("REMOTE-A");
            FinishUpdates(form, hosts[0], hosts[1]);
            Assert(worker.Requests.ToArray() is [{ Action:"publish-msi-packages", Host:"REMOTE-A" }] && !hosts[1].Updates!.SuccessfulPosUploadInRun,
                "upload proof from an unrelated update run cannot authorize POS restart");

            foreach (string installed in new[] { "2.0.0", "3.0.0", "2.0.0.123", "Nicht installiert", "Version unbekannt", "1.0.0, 3.0.0" })
            {
                NewRun(); hosts[1].PosVersion=installed;
                FinishUpdates(form, hosts);
                Assert(worker.Requests.Where(request => request.Action == "restart-pos").All(request => request.Host != "REMOTE-A") &&
                    hosts[1].Phase.Contains("POS-Neustart übersprungen"), "POS with before-version '" + installed + "' is not automatically restarted");
            }
            hosts[1].PosVersion="1.0.0";
            NewRun();
            packages.Add(UpdateComponents.Ntc, new() { Kind="ntc", ProductName="NovaTouch Control", ProductVersion="2.0.0", PayloadPath=msi });
            worker.UploadedKindsOverride=["ntc"];
            FinishUpdates(form, hosts);
            Assert(worker.Requests.All(request => request.Action == "publish-msi-packages") && hosts.Skip(1).All(host => host.Phase.Contains("kein POS-MSI neu")),
                "NTC upload cannot authorize POS restart when selected POS MSI was already present in zzzSoftware");
            packages.Remove(UpdateComponents.Pos);
            NewRun();
            FinishUpdates(form, hosts);
            Assert(worker.Requests.All(request => request.Action == "publish-msi-packages") && hosts.Skip(1).All(host => host.Phase.Contains("kein POS-Paket")),
                "no POS package means no POS restart, including service/NTC-only updates");

            Field<CheckBox>(form, "chkUploadToZzzSoftware").Checked = false;

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
        internal string UploadMessage = "";
        internal string[]? UploadedKindsOverride;
        internal HashSet<string> UnchangedUploadHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> progress, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (request.Action == "restart-pos") Assert(!ct.CanBeCanceled, "accepted POS restarts finish their cleanup even when the batch is cancelled");
            bool failure = request.Action == "publish-msi-packages" ? request.Host == FailedUploadHost : request.Host == FailedPosHost;
            bool unchanged = request.Action == "publish-msi-packages" && UnchangedUploadHosts.Contains(request.Host);
            return Task.FromResult(new WorkerResult { Success=!failure, Error=failure ? "Synthetic final step failure" : "", Changed=!failure && !unchanged,
                Skipped=unchanged, Message=request.Action == "publish-msi-packages" ? UploadMessage : "",
                DatabaseUploadedKinds=request.Action == "publish-msi-packages" && !failure && !unchanged ? UploadedKindsOverride ?? request.MsiPackages.Select(package => package.Kind).ToArray() : [] });
        }
        public void Dispose() { }
    }
}
