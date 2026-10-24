using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using NovaTouchUpdater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--helper-handshake" })) HelperHandshake().GetAwaiter().GetResult();
            else { ReleaseTests().GetAwaiter().GetResult(); LayoutTests(); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task Fails(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException or IOException)
        { Console.WriteLine("PASS " + message); return; }
        throw new InvalidOperationException(message);
    }

    private static async Task ReleaseTests()
    {
        byte[] payload = new byte[400000];
        new Random(42).NextBytes(payload);
        string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string tag = "v1.0.23";
        string url = $"https://github.com/{AppReleaseClient.Repository}/releases/download/{tag}/NovaTouchUpdater-{tag}.exe";
        string Json(string version = "v1.0.23", bool draft = false, bool prerelease = false, string? download = null, string? sha = null, string? name = null) =>
            JsonSerializer.Serialize(new { tag_name = version, draft, prerelease, assets = new[] {
                new { name = name ?? $"NovaTouchUpdater-{version}.exe", size = payload.Length, digest = sha ?? digest, browser_download_url = download ?? url }
            }});
        AppRelease? Parse(string json, string current = "v1.0.22")
        {
            using var doc = JsonDocument.Parse(json);
            return AppReleaseClient.ReadRelease(doc.RootElement, AppReleaseClient.ParseVersion(current)!);
        }
        Assert(Parse(Json())?.Version == new Version(1, 0, 23, 0), "new stable release uses its matching single-file EXE");
        Assert(Parse(Json(), "v1.0.23") is null && Parse(Json(), "v1.0.23.0") is null && Parse(Json(), "v2.0.0") is null,
            "same version, zero revision and older releases never offer a downgrade");
        Assert(Parse(Json(draft: true)) is null && Parse(Json(prerelease: true)) is null && Parse(Json("v1.0.24-beta")) is null,
            "drafts, prereleases and invalid version tags are ignored");
        Assert(Parse(Json(name: "OtherApplication.exe")) is null, "unrelated release assets are ignored");
        await Fails(() => { Parse(Json(download: "https://example.invalid/other.exe")); return Task.CompletedTask; }, "foreign download URL is rejected");
        await Fails(() => { Parse(Json(sha: "sha256:wrong")); return Task.CompletedTask; }, "invalid release checksum is rejected");
        using var handler = new FakeHandler(request =>
        {
            Assert(request.RequestUri == AppReleaseClient.LatestReleaseUrl && request.Headers.UserAgent.Count > 0, "release request uses the known repository and a User-Agent");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json()) };
        });
        using var http = new HttpClient(handler);
        var release = await new AppReleaseClient(http).CheckAsync(new Version(1, 0, 22), CancellationToken.None);
        Assert(release is not null, "background check reads GitHub release metadata without credentials");
        using var missing = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert(await new AppReleaseClient(missing).CheckAsync(new Version(1, 0, 22), CancellationToken.None) is null, "no published release leaves the notice hidden");

        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string root = Path.Combine(workspace, "obj", "app-release-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string destination = Path.Combine(root, "new.exe");
            using var downloads = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
            var client = new AppReleaseClient(downloads);
            var progress = new ImmediateProgress();
            await client.DownloadAsync(release!, destination, progress, CancellationToken.None);
            Assert(File.ReadAllBytes(destination).SequenceEqual(payload) && progress.Values.Last() == 100 && progress.Values.Any(x => x > 0 && x < 100),
                "download streams bytes with progress and confirms size plus SHA256 before 100 percent");
            await Fails(() => client.DownloadAsync(release!, destination, null, CancellationToken.None), "an existing file is never overwritten by the downloader");
            Assert(File.ReadAllBytes(destination).SequenceEqual(payload), "existing file survives a failed CreateNew");
            File.Delete(destination);
            await Fails(() => client.DownloadAsync(release! with { Sha256 = new string('0', 64) }, destination, null, CancellationToken.None), "corrupt download fails checksum verification");
            Assert(!File.Exists(destination), "failed download removes only its partial file");
            await Fails(() => client.DownloadAsync(release! with { Size = payload.Length + 1 }, destination, null, CancellationToken.None), "incorrect declared length fails before staging");
            using var shortDownload = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(payload[..100])) }));
            await Fails(() => new AppReleaseClient(shortDownload).DownloadAsync(release!, destination, null, CancellationToken.None), "truncated body without Content-Length is rejected");
            using var overflow = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(payload.Concat(new byte[100]).ToArray())) }));
            await Fails(() => new AppReleaseClient(overflow).DownloadAsync(release!, destination, null, CancellationToken.None), "oversized streaming body is rejected");
            using var cancel = new CancellationTokenSource();
            var cancelProgress = new ImmediateProgress { Callback = _ => cancel.Cancel() };
            await Fails(() => client.DownloadAsync(release!, destination, cancelProgress, cancel.Token), "cancel stops a running download");
            Assert(!File.Exists(destination), "cancel leaves no partial EXE");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.Combine(workspace, "obj") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid fixture directory");
            Directory.Delete(root, true);
        }
    }

    private static async Task HelperHandshake()
    {
        // Run only from the published single-file TEST executable. Never hand
        // over to the helper: Dispose cancels it while this process stays alive.
        string target = Environment.ProcessPath!;
        byte[] originalHash;
        using (var input = File.OpenRead(target)) originalHash = SHA256.HashData(input);
        string download;
        using (var update = new AppSelfUpdate())
        {
            download = update.DownloadPath;
            byte[] payload = "test fixture - not executable and never started"u8.ToArray();
            await File.WriteAllBytesAsync(download, payload);
            var release = new AppRelease(new Version(9, 9, 9), new Uri("https://example.invalid/unused"), payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
            await update.StartHelperAsync(release, CancellationToken.None);
            Assert(File.Exists(Path.Combine(Path.GetDirectoryName(download)!, "ready")), "published single-file test starts its embedded PowerShell helper and receives readiness");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(download)!, "update.json")));
            Assert(manifest.RootElement.GetProperty("OriginalExecutablePath").GetString() == target &&
                manifest.RootElement.GetProperty("ExecutablePath").GetString() == Path.Combine(Path.GetDirectoryName(target)!, "NovaTouchUpdater.exe"),
                "helper receives the original path and the fixed NovaTouchUpdater.exe destination in the same folder");
            using var input = File.OpenRead(target);
            Assert(SHA256.HashData(input).SequenceEqual(originalHash), "real helper waits for the test parent and does not replace a running EXE");
        }
        Assert(!Directory.Exists(Path.GetDirectoryName(download)), "cancelling before hand-off stops the helper and deletes its staging files");
        using var final = File.OpenRead(target);
        Assert(SHA256.HashData(final).SequenceEqual(originalHash), "the real handshake test leaves its own executable unchanged");
    }

    private static void LayoutTests()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        foreach (float scale in new[] { 1F, 1.25F, 1.5F, 2F })
        {
            using var link = new LinkLabel { Text = "Neue Version verfügbar", Visible = true };
            using var header = new UpdaterHeader(link) { Font = new Font("Segoe UI", 9F * scale) };
            header.Controls.Cast<Control>().Single(c => c != link).Controls.OfType<Label>().Single(l => l.Text == "NovaTouch 2.5 Updater").Font =
                new Font("Segoe UI", 19F * scale, FontStyle.Bold);
            header.Scale(new SizeF(scale, scale));
            _ = header.Handle; // no Form.Show, connections or real application startup
            foreach (int width in new[] { 1700, 1190, 610, 1190, 1700 })
            {
                header.Width = (int)(width * scale);
                for (int pass = 0; pass < 8; pass++)
                {
                    header.PerformLayout();
                    foreach (Control control in header.Controls) control.PerformLayout();
                }
                Control caption = header.Controls.Cast<Control>().Single(c => c != link);
                Assert(link.Right <= header.ClientSize.Width && Math.Abs(link.Right + link.Margin.Right - header.ClientSize.Width) <= 2,
                    $"update notice stays aligned to the right at {width}px / {scale * 100}%");
                Assert(!link.Bounds.IntersectsWith(caption.Bounds), $"notice never overlaps caption at {width}px / {scale * 100}%");
                Assert(caption.Controls.Cast<Control>().All(c => c.Right <= caption.Width), $"header texts fit at {width}px / {scale * 100}%");
                if (width == 1190) Assert(header.GetRow(link) == 0, $"normal window keeps the notice in the same row at {scale * 100}%");
                if (width == 610) Assert(header.GetRow(link) == 1, $"narrow window gives the notice its own row at {scale * 100}%");
                if (scale == 1F && width is 1190 or 610)
                {
                    header.Height = header.GetPreferredSize(new Size(header.Width, 0)).Height;
                    using var bitmap = new Bitmap(header.Width, header.Height);
                    header.DrawToBitmap(bitmap, header.ClientRectangle);
                    string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
                    bitmap.Save(Path.Combine(workspace, "obj", $"updater-header-{width}.png"));
                }
            }
        }
    }

    private sealed class ImmediateProgress : IProgress<int>
    {
        internal List<int> Values { get; } = [];
        internal Action<int>? Callback { get; init; }
        public void Report(int value) { Values.Add(value); Callback?.Invoke(value); }
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream source = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => source.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }
}
