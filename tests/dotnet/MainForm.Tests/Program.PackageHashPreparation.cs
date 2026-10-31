using System.Collections.Concurrent;
using NovaTouchUpdater;

internal static partial class Program
{
    private static void PackageHashPreparationTests()
    {
        string root = Path.GetFullPath(Path.Combine(TestWorkspace.Artifacts, "package-hashes-" + Guid.NewGuid().ToString("N")));
        Assert(root.StartsWith(TestWorkspace.Artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "hash fixtures remain inside the test workspace");
        Directory.CreateDirectory(root);
        try
        {
            string payment = Path.Combine(root, "nt.payment_1.0.0.zip"), bundle = Path.Combine(root, "ControlVoucher.zip"), cab = Path.Combine(root, "data.cab");
            foreach (string file in new[] { payment, bundle, cab }) File.WriteAllText(file, "Test data, never installed: " + Path.GetFileName(file));
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            SetField(form, "paymentZip", payment);
            var packages = Field<Dictionary<UpdateComponents, MsiPackageRequest>>(form, "msiPackages");
            packages.Add(UpdateComponents.Ntc, new() { Kind = "ntc", PayloadPath = bundle,
                CompanionFiles = [new() { LocalPath = cab, RelativePath = "data.cab" }] });
            packages.Add(UpdateComponents.Ntv, new() { Kind = "ntv", PayloadPath = bundle });
            var components = UpdateComponents.Payment | UpdateComponents.Ntc | UpdateComponents.Ntv;
            var reads = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int active = 0, maxActive = 0;
            Func<string, CancellationToken, Task<string>> hash = async (path, ct) =>
            {
                reads.AddOrUpdate(path, 1, (_, count) => count + 1);
                int concurrent = Interlocked.Increment(ref active);
                if (concurrent == 2) { Interlocked.Exchange(ref maxActive, 2); bothStarted.TrySetResult(); }
                try
                {
                    if (concurrent > 2) throw new InvalidOperationException("Too many concurrent disk reads");
                    await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    return await WorkerRunner.Sha256Async(path, ct);
                }
                finally { Interlocked.Decrement(ref active); }
            };
            Complete((Task)Call(form, "PrepareUpdatePackageHashesAsync", components, CancellationToken.None, hash)!);
            Assert(reads.Count == 3 && reads.Values.All(count => count == 1) && maxActive == 2,
                "preparation hashes each unique source once, with at most two concurrent reads even for a shared MSI ZIP");
            string bundleHash = packages[UpdateComponents.Ntc].Sha256;
            Assert(bundleHash.Length == 64 && bundleHash == packages[UpdateComponents.Ntv].Sha256 &&
                packages[UpdateComponents.Ntc].CompanionFiles.Single().Sha256.Length == 64 && Field<string>(form, "paymentHash").Length == 64,
                "service ZIPs, shared MSI payloads and cabinets receive prepared SHA256 values");

            // Manifest creation must use the prepared snapshot. The actual worker still checks source bytes before installation.
            string bundleContent = File.ReadAllText(bundle);
            File.Delete(bundle);
            var manifestTask = (Task<TransferFileRequest[]>)Call(form, "PackageFilesAsync", components, CancellationToken.None)!;
            Complete(manifestTask);
            Assert(manifestTask.Result.Length == 3 && manifestTask.Result.Single(file => file.LocalPath == bundle).Sha256 == bundleHash,
                "local staging reuses prepared hashes without reopening package sources");
            File.WriteAllText(bundle, bundleContent);

            Complete((Task)Call(form, "InBatchAsync", Array.Empty<HostState>(), (Func<CancellationToken, Task>)(_ => Task.CompletedTask))!);
            Assert(Field<IReadOnlyDictionary<string, string>?>(form, "preparedPackageHashes") is null,
                "a finished batch clears its hash snapshot so a later update cannot reuse stale checksums");
            File.AppendAllText(bundle, " changed before next update");
            Complete((Task)Call(form, "PrepareUpdatePackageHashesAsync", components, CancellationToken.None, hash)!);
            Assert(packages[UpdateComponents.Ntc].Sha256 != bundleHash && reads.Values.All(count => count == 2),
                "each new update recalculates hashes and detects changed package sources");

            File.Delete(payment);
            reads.Clear();
            Complete((Task)Call(form, "PrepareUpdatePackageHashesAsync", UpdateComponents.Ntc, CancellationToken.None, hash)!);
            Assert(reads.Count == 2 && !reads.ContainsKey(payment) && Field<string>(form, "paymentHash") == "",
                "retry preparation reads only pending components and does not require completed package sources");
            SetField(form, "activeInstallHashComponents", UpdateComponents.None);
            reads.Clear();
            Complete((Task)Call(form, "PrepareUpdatePackageHashesAsync", UpdateComponents.Ntc, CancellationToken.None, hash)!);
            Assert(reads.Count == 1 && reads.ContainsKey(bundle), "a database-only retry hashes the MSI payload without requiring cabinet files from completed installations");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            reads.Clear();
            bool cancelled = false;
            try { Complete((Task)Call(form, "PrepareUpdatePackageHashesAsync", UpdateComponents.Ntc, cancel.Token, hash)!); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && reads.Count == 0 && Field<IReadOnlyDictionary<string, string>?>(form, "preparedPackageHashes") is null,
                "cancelled preparation publishes no partial or stale hash snapshot");
        }
        finally
        {
            Assert(root.StartsWith(TestWorkspace.Artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "hash fixture cleanup stays scoped to test artifacts");
            Directory.Delete(root, true);
        }
    }
}
