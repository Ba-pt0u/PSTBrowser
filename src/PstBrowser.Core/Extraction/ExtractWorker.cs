using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PstBrowser.Core.Extraction
{
    /// <summary>
    /// Server side of the extraction process. Text extraction parses untrusted files with complex formats: it runs in a
    /// separate process (<c>PstBrowser.exe --extract-worker</c>, <c>pstbrowser-cli extract-worker</c>) so that a crash, a memory
    /// explosion or an endless loop can only cost that process, which the indexer kills and restarts.
    ///
    /// Binary protocol on stdin/stdout (little endian, BinaryWriter strings):
    ///   request  : byte op (1 = extract, 2 = pause for the given milliseconds — diagnostics —, 0 = quit)
    ///              op 1: string name, int32 maxChars, int32 length, bytes
    ///              op 2: int32 milliseconds
    ///   response : int32 magic, byte status, string error, int32 length, UTF-8 text bytes
    /// </summary>
    public static class ExtractWorker
    {
        public const string Argument = "--extract-worker";
        internal const int Magic = 0x31584250; // "PBX1"
        internal const byte OpQuit = 0, OpExtract = 1, OpSleep = 2;

        public static bool IsWorkerInvocation(string[] args)
            => args != null && args.Length > 0 && (args[0] == Argument || args[0] == "extract-worker");

        /// <summary>Runs the request loop until stdin is closed. Returns the process exit code.</summary>
        public static int Run()
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            Console.SetOut(TextWriter.Null); // nothing may pollute the protocol stream
            using var reader = new BinaryReader(stdin, Encoding.UTF8);
            using var writer = new BinaryWriter(stdout, Encoding.UTF8);
            Text.Charsets.EnsureRegistered();
            while (true)
            {
                int op = stdin.ReadByte();
                if (op <= OpQuit) return 0;
                ExtractResult res;
                if (op == OpExtract)
                {
                    string name = reader.ReadString();
                    int maxChars = reader.ReadInt32();
                    int length = reader.ReadInt32();
                    if (length < 0 || length > 512 * 1024 * 1024) return 2;
                    var data = reader.ReadBytes(length);
                    if (data.Length != length) return 3;
                    res = TextExtractor.Extract(name, data, new ExtractLimits { MaxChars = maxChars > 0 ? maxChars : new ExtractLimits().MaxChars });
                }
                else if (op == OpSleep)
                {
                    Thread.Sleep(Math.Max(0, reader.ReadInt32()));
                    res = new ExtractResult();
                }
                else return 4;
                var text = Encoding.UTF8.GetBytes(res.Text ?? "");
                writer.Write(Magic);
                writer.Write(AttachmentStatus.ToByte(res.Status));
                writer.Write(res.Error ?? "");
                writer.Write(text.Length);
                writer.Write(text);
                writer.Flush();
            }
        }
    }

    /// <summary>How the extraction worker process is started.</summary>
    public sealed class WorkerLaunch
    {
        public string FileName { get; set; }
        public List<string> Arguments { get; } = new List<string>();

        /// <summary>The running executable itself, started with <c>--extract-worker</c> (through <c>dotnet app.dll</c> when launched that way).</summary>
        public static WorkerLaunch Default()
        {
            var w = new WorkerLaunch { FileName = Environment.ProcessPath };
            if (string.Equals(Path.GetFileNameWithoutExtension(w.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entry = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(entry)) w.Arguments.Add(entry);
            }
            w.Arguments.Add(ExtractWorker.Argument);
            return w;
        }
    }

    /// <summary>Client side: owns one worker process, restarted after a time-out or a crash. Not thread-safe.</summary>
    public sealed class ExtractorClient : IDisposable
    {
        private readonly WorkerLaunch _launch;
        private Process _proc;
        private BinaryWriter _writer;
        private BinaryReader _reader;

        /// <summary>Number of times the worker had to be killed (time-out) or died.</summary>
        public int Restarts { get; private set; }

        public ExtractorClient(WorkerLaunch launch = null) { _launch = launch ?? WorkerLaunch.Default(); }

        private void EnsureStarted()
        {
            if (_proc != null && !_proc.HasExited) return;
            Reset();
            var psi = new ProcessStartInfo(_launch.FileName)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in _launch.Arguments) psi.ArgumentList.Add(a);
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de démarrer l'extracteur de pièces jointes");
            try { _proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _proc.ErrorDataReceived += (_, __) => { };
            _proc.BeginErrorReadLine();
            _writer = new BinaryWriter(_proc.StandardInput.BaseStream, Encoding.UTF8);
            _reader = new BinaryReader(_proc.StandardOutput.BaseStream, Encoding.UTF8);
        }

        private void Reset()
        {
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null; _writer = null; _reader = null;
        }

        /// <summary>Extracts the text of a file. Time-outs and crashes are reported as an 'error' result, after which the worker restarts.</summary>
        public ExtractResult Extract(string name, byte[] data, int maxChars, TimeSpan timeout, CancellationToken ct = default)
            => Call(w =>
            {
                w.Write(ExtractWorker.OpExtract);
                w.Write(name ?? "");
                w.Write(maxChars);
                w.Write(data.Length);
                w.Write(data);
            }, timeout, ct);

        /// <summary>Asks the worker to pause (diagnostics: lets tests exercise the time-out).</summary>
        public ExtractResult Sleep(int milliseconds, TimeSpan timeout, CancellationToken ct = default)
            => Call(w => { w.Write(ExtractWorker.OpSleep); w.Write(milliseconds); }, timeout, ct);

        private ExtractResult Call(Action<BinaryWriter> request, TimeSpan timeout, CancellationToken ct)
        {
            EnsureStarted();
            var writer = _writer; var reader = _reader;
            var task = Task.Run(() =>
            {
                request(writer);
                writer.Flush();
                if (reader.ReadInt32() != ExtractWorker.Magic) throw new InvalidDataException("Réponse invalide de l'extracteur");
                var res = new ExtractResult { Status = AttachmentStatus.FromByte(reader.ReadByte()) };
                var err = reader.ReadString();
                res.Error = err.Length == 0 ? null : err;
                int n = reader.ReadInt32();
                var bytes = reader.ReadBytes(n);
                if (bytes.Length != n) throw new EndOfStreamException();
                res.Text = Encoding.UTF8.GetString(bytes);
                return res;
            });
            try
            {
                if (task.Wait(timeout, ct)) return task.Result;
            }
            catch (OperationCanceledException)
            {
                Reset();
                throw;
            }
            catch (AggregateException ex)
            {
                Restarts++;
                Reset();
                var inner = ex.GetBaseException();
                return new ExtractResult { Status = AttachmentStatus.Error, Error = "L'extracteur s'est arrêté (" + inner.Message + ")" };
            }
            Restarts++;
            Reset();
            return new ExtractResult { Status = AttachmentStatus.Error, Error = $"Délai dépassé ({timeout.TotalSeconds:0.#} s) : extraction abandonnée" };
        }

        public void Dispose()
        {
            try { if (_proc != null && !_proc.HasExited) { _writer?.Write(ExtractWorker.OpQuit); _writer?.Flush(); _proc.WaitForExit(500); } } catch { }
            Reset();
        }
    }

    /// <summary>Write-only sink that hashes everything written and keeps at most <c>limit</c> bytes.</summary>
    internal sealed class CaptureStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private MemoryStream _ms = new MemoryStream();
        private readonly long _limit;

        public CaptureStream(long limit) { _limit = limit; }
        public long Total { get; private set; }
        public bool Overflow { get; private set; }
        public byte[] Bytes => Overflow ? null : _ms.ToArray();
        public string Sha256Hex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            Total += count;
            if (Overflow) return;
            if (Total > _limit) { Overflow = true; _ms = null; return; }
            _ms.Write(buffer, offset, count);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Total;
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
