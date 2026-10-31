using NovaTouchUpdater;

internal static partial class Program
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoRetryDelay = (_, ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

    private static void SetField(MainForm form, string field, object value) => typeof(MainForm).GetField(field, Private)!.SetValue(form, value);

    private static MainForm UpdateForm(UpdateRunner worker, bool force = false)
    {
        var form = new MainForm(preview: true, worker, new FakeRunner());
        _ = form.Handle;
        foreach (string kind in new[] { "payment", "fiscal", "serverSuite" })
        {
            SetField(form, kind + "Zip", kind + ".zip");
            SetField(form, kind + "Version", "2.0.0");
            SetField(form, kind + "Hash", kind + "-hash");
        }
        SetField(form, "activeForceUpdate", force);
        return form;
    }

    private static void Update(MainForm form, HostState host, CancellationToken ct = default) =>
        Complete((Task)Call(form, "UpdateHostAsync", host, ct, NoRetryDelay)!);

    private static void UpdateRetryTests()
    {
        foreach (UpdateComponents failing in HostUpdateState.Each(UpdateComponents.ZipServices))
        {
            var worker = new UpdateRunner();
            using var form = UpdateForm(worker, force: true);
            HostState host = Populate(form)[0];
            host.Updates = new(UpdateComponents.ZipServices);
            worker.Outcome = request => worker.Kind(request) == failing && worker.Count(host.Host, failing) == 1
                ? new WorkerResult { Success = false, Error = "Simulated first install failure" } : worker.Success(request);
            Update(form, host);
            Assert(HostUpdateState.Each(UpdateComponents.ZipServices).All(c => worker.Count(host.Host, c) == (c == failing ? 2 : 1)),
                $"automatic retries repeat only {HostUpdateState.Label(failing)}, including Force mode");
            WorkerRequest[] failedRequests = worker.Requests.Where(r => r.Action == "update" && worker.Kind(r) == failing).ToArray();
            Assert(failedRequests[0].RetryUpdate == false && failedRequests[1].RetryUpdate && worker.Requests.Where(r => r.Action == "update").All(r => r.ForceUpdate),
                "repair requests bypass a misleading already-copied version only for the failed component");
            Assert(host.Updates.Pending == UpdateComponents.None && !host.CanRetryUpdate && host.Result == "Erfolgreich" && host.Attempt == 2 && host.ProgressPercent == 100,
                "all verified components complete the host and clear retry eligibility");
        }

        var instanceWorker = new UpdateRunner();
        using (var form = UpdateForm(instanceWorker, force:true))
        {
            HostState host = Populate(form)[0];
            host.Updates = new(UpdateComponents.ServerSuite);
            instanceWorker.Outcome = request => instanceWorker.Count(host.Host, UpdateComponents.ServerSuite) == 1
                ? new WorkerResult { Success=false, Error="Second service instance failed", CompletedServiceNames=["nt3_server_suite_A"] }
                : instanceWorker.Success(request);
            Update(form, host);
            WorkerRequest[] requests = instanceWorker.Requests.Where(request => request.Action == "update").ToArray();
            Assert(requests.Length == 2 && requests[0].CompletedServiceNames.Length == 0 && requests[1].CompletedServiceNames.SequenceEqual(["nt3_server_suite_A"]),
                "a retry passes verified service names so the worker only installs failed service instances, including Force mode");
        }

        var permanent = new UpdateRunner();
        using (var form = UpdateForm(permanent))
        {
            HostState host = Populate(form)[0];
            host.Updates = new(UpdateComponents.ZipServices);
            bool repair = false;
            permanent.Progress = (request, report) =>
            {
                Assert(host.Error.Length == 0 && host.ProgressPercent is null, "each component or retry begins with a fresh displayed status");
                report(new WorkerEvent { Phase="Installieren", Message=HostUpdateState.Label(permanent.Kind(request)), ProgressPercent=35 });
                Call(form, "PaintRow", host);
                string information = Convert.ToString(host.Row!.Cells["information"].Value)!;
                Assert(information.Contains("35 %") && information.Contains("Installieren") && !information.Contains("Simulated fiscal"),
                    "the running information cell shows current progress without earlier component errors");
            };
            permanent.Outcome = request => permanent.Kind(request) == UpdateComponents.Fiscal && !repair
                ? throw new IOException("Simulated fiscal installation failure") : permanent.Success(request);
            Update(form, host);
            Assert(permanent.Count(host.Host, UpdateComponents.Payment) == 1 && permanent.Count(host.Host, UpdateComponents.Fiscal) == 3 && permanent.Count(host.Host, UpdateComponents.ServerSuite) == 1,
                "a persistent component error does not prevent the other components from finishing once");
            Assert(host.UpdateFailed && host.CanRetryUpdate && host.Updates.Pending == UpdateComponents.Fiscal && host.Error.Contains("nt.fiscal") && host.Payment == "2.0.0" && host.ServerSuite == "2.0.0",
                "partial inventory and the exact failed component remain visible after three attempts");
            Call(form, "PaintRow", host);
            Assert(Convert.ToString(host.Row!.Cells["information"].Value)!.Contains("Simulated fiscal installation failure") && host.Row.Cells["information"].Tag is null,
                "the final information cell restores unresolved failures and removes the green progress bar");
            Assert((UpdateComponents)Call(form, "ComponentsForUpdate", host, true)! == UpdateComponents.Fiscal &&
                (UpdateComponents)Call(form, "ComponentsForUpdate", host, false)! == UpdateComponents.ZipServices,
                "manual retry retains pending components while an explicit new update selects a fresh run");
            Task preflight = (Task)Call(form, "PrepareUploadDirectoriesAsync", new[] { host }, true,
                (Action<HostState, string>)((_, error) => throw new InvalidOperationException(error)), CancellationToken.None)!;
            Complete(preflight);
            WorkerRequest check = permanent.Requests.Last(r => r.Action == "check-upload-directory");
            Assert(permanent.Kind(check) == UpdateComponents.Fiscal && check.CheckUpdateUpload && check.RetryUpdate,
                "manual retry upload preparation excludes completed ZIPs and recognizes partial installs");
            host.BeginCommand(true);
            host.CompleteCommand(true, DateTimeOffset.Now);
            Assert(host.Updates.Pending == UpdateComponents.Fiscal, "a terminal reboot cannot forget successful update components");
            repair = true;
            Update(form, host);
            Assert(permanent.Count(host.Host, UpdateComponents.Payment) == 1 && permanent.Count(host.Host, UpdateComponents.ServerSuite) == 1 && permanent.Count(host.Host, UpdateComponents.Fiscal) == 4,
                "manual retry after a reboot reinstalls only fiscal, after rechecking the host");
            Assert(permanent.Requests.Any(r => r.Action == "probe") && permanent.Requests.Any(r => r.Action == "scan") &&
                host.Updates.Pending == UpdateComponents.None && !host.UpdateFailed && host.Result == "Erfolgreich",
                "connectivity and version rechecks preserve successful component results");
        }

        using var cancel = new CancellationTokenSource();
        var cancelling = new UpdateRunner();
        using (var form = UpdateForm(cancelling))
        {
            HostState host = Populate(form)[0];
            host.Updates = new(UpdateComponents.ZipServices);
            cancelling.Outcome = request => { var result = cancelling.Success(request); cancel.Cancel(); return result; };
            bool cancelled = false;
            try { Update(form, host, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && host.Result == "Abgebrochen" && host.CanRetryUpdate && host.Updates.Pending == (UpdateComponents.Fiscal | UpdateComponents.ServerSuite),
                "cancellation keeps the verified payment update and remembers unfinished components");
            cancelling.Outcome = cancelling.Success;
            Update(form, host);
            Assert(cancelling.Count(host.Host, UpdateComponents.Payment) == 1 && cancelling.Count(host.Host, UpdateComponents.Fiscal) == 1 && cancelling.Count(host.Host, UpdateComponents.ServerSuite) == 1,
                "resuming unfinished work after cancellation never repeats payment");
        }

        var independent = new UpdateRunner();
        using (var form = UpdateForm(independent))
        {
            HostState[] hosts = Populate(form);
            hosts[0].Updates = new(UpdateComponents.Payment);
            hosts[1].Updates = new(UpdateComponents.ServerSuite);
            Update(form, hosts[0]);
            Update(form, hosts[1]);
            Assert(independent.Requests.Count(r => r.Action == "update") == 2 && independent.Count(hosts[0].Host, UpdateComponents.Payment) == 1 && independent.Count(hosts[1].Host, UpdateComponents.ServerSuite) == 1,
                "different hosts retain independent component selections");
        }

        foreach (string outcome in new[] { "absent", "current", "mixed" })
        {
            var worker = new UpdateRunner();
            using var form = UpdateForm(worker);
            HostState host = Populate(form)[0];
            host.Updates = new(UpdateComponents.ZipServices);
            worker.Outcome = request =>
            {
                WorkerResult result = worker.Success(request);
                result.Changed = outcome == "mixed" && worker.Kind(request) == UpdateComponents.Payment;
                result.Skipped = outcome == "absent" || (outcome == "mixed" && worker.Kind(request) == UpdateComponents.Fiscal);
                return result;
            };
            Update(form, host);
            string expected = outcome == "absent" ? "Übersprungen" : outcome == "current" ? "Bereits aktuell / übersprungen" : "Erfolgreich";
            Assert(host.Result == expected && !host.CanRetryUpdate && worker.Requests.Count(r => r.Action == "update") == 3,
                $"host result combines component outcomes correctly: {outcome}");
        }
    }

    private sealed class UpdateRunner : IWorkerRunner
    {
        internal List<WorkerRequest> Requests { get; } = [];
        internal Func<WorkerRequest, WorkerResult>? Outcome { get; set; }
        internal Action<WorkerRequest, Action<WorkerEvent>>? Progress { get; set; }
        private readonly Dictionary<(string Host, UpdateComponents Component), int> counts = [];
        private readonly Dictionary<(string Host, UpdateComponents Component), string> versions = [];
        internal int Count(string host, UpdateComponents kind) => counts.GetValueOrDefault((host, kind));
        internal UpdateComponents Kind(WorkerRequest request)
        {
            UpdateComponents[] kinds = new[] {
                string.IsNullOrEmpty(request.PaymentZip) ? UpdateComponents.None : UpdateComponents.Payment,
                string.IsNullOrEmpty(request.FiscalZip) ? UpdateComponents.None : UpdateComponents.Fiscal,
                string.IsNullOrEmpty(request.ServerSuiteZip) ? UpdateComponents.None : UpdateComponents.ServerSuite
            }.Where(c => c != UpdateComponents.None).ToArray();
            if (kinds.Length != 1) throw new InvalidOperationException("An update request must contain exactly one component ZIP.");
            return kinds[0];
        }
        private ServiceInfo[] Inventory(string host) => HostUpdateState.Each(UpdateComponents.ZipServices).Select(c => new ServiceInfo
        {
            Kind = c == UpdateComponents.Payment ? "payment" : c == UpdateComponents.Fiscal ? "fiscal" : "serversuite",
            Name = HostUpdateState.Label(c), ProductVersion = versions.GetValueOrDefault((host, c), "1.0.0"), State = "Running"
        }).ToArray();
        internal WorkerResult Success(WorkerRequest request)
        {
            versions[(request.Host, Kind(request))] = "2.0.0";
            return new WorkerResult { Success = true, Changed = true, Services = Inventory(request.Host) };
        }
        public Task<WorkerResult> RunAsync(WorkerRequest request, Action<WorkerEvent> onProgress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.Action == "check-upload-directory") return Task.FromResult(new WorkerResult { Success = true, UploadRequired = false });
            if (request.Action is "probe" or "scan") return Task.FromResult(new WorkerResult { Success = true, Services = Inventory(request.Host) });
            if (request.Action != "update") throw new InvalidOperationException("Unexpected action: " + request.Action);
            UpdateComponents kind = Kind(request);
            counts[(request.Host, kind)] = Count(request.Host, kind) + 1;
            Progress?.Invoke(request, onProgress);
            return Task.FromResult((Outcome ?? Success)(request));
        }
        public void Dispose() { }
    }
}
