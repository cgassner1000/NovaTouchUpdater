function InitializeSoftwareDatabaseStreaming {
    if ('NovaTouch.DatabaseStreaming.Source' -as [type]) { return }
    Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
namespace NovaTouch.DatabaseStreaming {
    public sealed class Source {
        readonly Dictionary<string, InputStream> readers = new Dictionary<string, InputStream>(StringComparer.OrdinalIgnoreCase);
        public Source(long length, string[] destinations) {
            foreach (string key in destinations) readers.Add(key, new InputStream(length));
        }
        public Stream Open(string key) { return readers[key]; }
        public void Skip(string key) { InputStream stream; if (readers.TryGetValue(key, out stream)) stream.Dispose(); }
        internal void Cancel() { foreach (InputStream stream in readers.Values) stream.Dispose(); }
        internal void Push(byte[] bytes) { foreach (InputStream stream in readers.Values) stream.Push(bytes); }
        internal void Finish(Exception error) { foreach (InputStream stream in readers.Values) stream.Finish(error); }
    }
    sealed class InputStream : Stream {
        readonly BlockingCollection<byte[]> blocks = new BlockingCollection<byte[]>(4);
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        readonly long length;
        byte[] current; int offset; long position; Exception error; int finished;
        internal InputStream(long size) { length = size; }
        internal void Push(byte[] bytes) {
            try { blocks.Add(bytes, cancel.Token); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { if (!cancel.IsCancellationRequested) throw; }
        }
        internal void Finish(Exception failure) {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            error = failure;
            if (failure != null) cancel.Cancel();
            blocks.CompleteAdding();
        }
        public override int Read(byte[] buffer, int start, int count) {
            if (position == length || count == 0) return 0;
            if (current == null || offset == current.Length) {
                try { current = blocks.Take(cancel.Token); offset = 0; }
                catch (InvalidOperationException) { throw error ?? new EndOfStreamException("MSI stream ended early"); }
            }
            int take = Math.Min(count, current.Length - offset);
            Buffer.BlockCopy(current, offset, buffer, start, take); offset += take; position += take;
            return take;
        }
        protected override void Dispose(bool disposing) { cancel.Cancel(); base.Dispose(disposing); }
        public override long Length { get { return length; } }
        public override long Position { get { return position; } set { throw new NotSupportedException(); } }
        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override void Flush() { }
        public override long Seek(long value, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
    public sealed class Pump : IDisposable {
        readonly Source[] sources; readonly long[] lengths; readonly TextReader input;
        readonly Thread thread; readonly Timer watchdog; long activity = DateTime.UtcNow.Ticks; volatile Exception error;
        public Pump(Source[] values, long[] sizes, TextReader reader) {
            sources = values; lengths = sizes; input = reader;
            thread = new Thread(Read) { IsBackground = true };
            watchdog = new Timer(delegate { if (DateTime.UtcNow.Ticks - Interlocked.Read(ref activity) > TimeSpan.FromMinutes(5).Ticks) Fail(new TimeoutException("WinRM MSI stream timed out")); }, null, 1000, 1000);
            thread.Start();
        }
        void Fail(Exception failure) { error = failure; foreach (Source source in sources) source.Finish(failure); }
        void Read() {
            try {
                for (int i=0; i<sources.Length; i++) {
                    long received = 0;
                    while (received < lengths[i]) {
                        string line = input.ReadLine();
                        if (line == null) throw new EndOfStreamException("WinRM MSI stream interrupted");
                        byte[] bytes = Convert.FromBase64String(line);
                        if (bytes.Length == 0 || bytes.Length > lengths[i] - received) throw new InvalidDataException("Invalid MSI stream length");
                        Interlocked.Exchange(ref activity, DateTime.UtcNow.Ticks);
                        sources[i].Push(bytes); received += bytes.Length;
                    }
                    sources[i].Finish(null);
                }
            } catch (Exception failure) { Fail(failure); }
        }
        public void Complete() { while (thread.IsAlive && error == null) thread.Join(50); if (error != null) throw new IOException("WinRM MSI stream failed", error); }
        public void Dispose() { watchdog.Dispose(); foreach (Source source in sources) source.Cancel(); }
    }
    public sealed class ProcessRelay : IDisposable {
        readonly Process process; readonly Thread output, errors;
        readonly StreamWriter input;
        readonly ConcurrentQueue<string> lines = new ConcurrentQueue<string>();
        string error = "";
        public ProcessRelay(string executable, string script, string request) {
            process = new Process { StartInfo = new ProcessStartInfo(executable, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"") {
                UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true,
                StandardOutputEncoding=new System.Text.UTF8Encoding(false), StandardErrorEncoding=new System.Text.UTF8Encoding(false) } };
            process.Start();
            input = new StreamWriter(process.StandardInput.BaseStream, new System.Text.UTF8Encoding(false)) { AutoFlush=true };
            output = new Thread(delegate() { string line; while ((line=process.StandardOutput.ReadLine()) != null) lines.Enqueue(line); }) { IsBackground=true };
            errors = new Thread(delegate() { error=process.StandardError.ReadToEnd(); }) { IsBackground=true };
            output.Start(); errors.Start(); input.WriteLine(request);
        }
        public void Write(string line) { input.WriteLine(line); }
        public void CloseInput() { input.Close(); }
        public bool TryRead(out string line) { return lines.TryDequeue(out line); }
        public bool HasExited { get { return process.HasExited; } }
        public int ExitCode { get { return process.ExitCode; } }
        public string Error { get { return error; } }
        public void Wait() { process.WaitForExit(); output.Join(); errors.Join(); }
        public void Dispose() { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } output.Join(); errors.Join(); } finally { input.Dispose(); process.Dispose(); } }
    }
}
'@
}
