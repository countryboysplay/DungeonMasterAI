using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DungeonMasterAI.AI;
using DungeonMasterAI.Data;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.RuntimeProvisioningTests;

/// <summary>
/// Coverage for the first-run provisioning path: the embedded pins, the runtime readiness
/// contract, and the resumable, SHA-256-gated downloader.
///
/// The downloader is exercised against a scripted loopback socket rather than the real pinned
/// URLs. That is the whole point: these assertions are about what happens when a 2.55 GiB transfer
/// is cut in half, resumed, restarted by a server that ignores Range, cancelled by the user, or
/// answered with the wrong bytes -- and none of those are reproducible against a healthy CDN.
/// </summary>
internal static class Program
{
    private const int PayloadSize = 512 * 1024;

    private static async Task<int> Main()
    {
        var failures = new List<string>();
        var passed = 0;

        async Task RunAsync(string name, Func<Task> test)
        {
            try
            {
                await test();
                passed++;
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Console.Error.WriteLine($"FAIL: {name}: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- embedded pins

        await RunAsync("the embedded runtime lock pins the build the installer vendors", () =>
        {
            Equal("b10786", RuntimeBootstrapService.PinnedRuntimeTag, "pinned llama.cpp tag");
            var manifest = RuntimeBootstrapService.MissingRuntimeFiles(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            Equal(24, manifest.Count, "runtime manifest file count for an absent directory");
            True(manifest.Contains("llama-server.exe"), "manifest must require the launcher");
            True(manifest.Contains("llama-server-impl.dll"), "manifest must require the real server DLL");
            True(manifest.Any(f => f.StartsWith("ggml-cpu-", StringComparison.Ordinal)), "manifest must require a CPU backend");
            return Task.CompletedTask;
        });

        await RunAsync("the embedded model lock and the AppSettings default name the same GGUF", () =>
        {
            // These live in two assemblies -- Domain cannot reference AI -- so nothing but this
            // assertion stops a re-pin from leaving AppSettings pointing at a file that is never
            // downloaded, which resolves to "no model configured" on a machine that has one.
            Equal(RuntimeBootstrapService.ModelFileName, new AppSettings().ModelPath, "AppSettings.ModelPath default");
            Equal(2740937888L, RuntimeBootstrapService.ModelSizeBytes, "pinned model size");
            True(RuntimeBootstrapService.ModelDownloadNotice.Contains("2.55 GB", StringComparison.Ordinal),
                "the first-run notice must state the real download size");
            return Task.CompletedTask;
        });

        await RunAsync("the default settings never send llama-server down its -hf branch", () =>
        {
            // A non-empty HuggingFaceModel makes LlamaRuntimeManager pass -hf, which auto-pulls the
            // repository's ~0.9 GB mmproj vision projector and bypasses the hash-verified local file.
            var settings = new AppSettings();
            Equal("", settings.HuggingFaceModel, "AppSettings.HuggingFaceModel default");
            Equal(0, settings.GpuLayers, "AppSettings.GpuLayers default for the bundled CPU build");
            return Task.CompletedTask;
        });

        // --------------------------------------------------------------- settings migration

        await RunAsync("an existing install is migrated off the settings that bypass the pinned model", async () =>
        {
            using var dir = new TempDir();
            // A v3 state file as an alpha tester's machine already holds it. Editing the AppSettings
            // defaults does nothing for these users: their values are on disk, and every one of them
            // routes llama-server around the verified local GGUF.
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "state.json"), """
            {
              "schemaVersion": 3,
              "settings": {
                "llamaServerUrl": "http://127.0.0.1:8080",
                "modelName": "local-model",
                "modelPath": "",
                "huggingFaceModel": "unsloth/Qwen3.5-9B-GGUF:UD-Q4_K_XL",
                "contextSize": 16384,
                "gpuLayers": 99,
                "autoProvisionRuntime": true,
                "temperature": 0.75,
                "maxTokens": 700,
                "playerSafeMode": true
              },
              "campaigns": []
            }
            """);

            var state = await new AppDataStore(dir.Path).LoadAsync();
            Equal(AppDataStore.CurrentSchemaVersion, state.SchemaVersion, "migrated schema version");
            Equal("", state.Settings.HuggingFaceModel, "the -hf branch must be closed, or the mmproj projector is pulled");
            Equal(RuntimeBootstrapService.ModelFileName, state.Settings.ModelPath, "ModelPath must name the file the provisioner writes");
            Equal(0, state.Settings.GpuLayers, "the bundled CPU build has nothing to offload to");
            Equal(8192, state.Settings.ContextSize, "context size");
            Equal(0.4, state.Settings.Temperature, "temperature");
        });

        await RunAsync("the migration leaves settings the user actually chose alone", async () =>
        {
            using var dir = new TempDir();
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "state.json"), """
            {
              "schemaVersion": 3,
              "settings": {
                "modelPath": "D:\\models\\my-own.gguf",
                "huggingFaceModel": "someone/else-GGUF:Q8_0",
                "contextSize": 32768,
                "gpuLayers": 24,
                "temperature": 0.9
              },
              "campaigns": []
            }
            """);

            var state = await new AppDataStore(dir.Path).LoadAsync();
            Equal("D:\\models\\my-own.gguf", state.Settings.ModelPath, "a chosen model path");
            Equal("someone/else-GGUF:Q8_0", state.Settings.HuggingFaceModel, "a chosen Hugging Face model");
            Equal(32768, state.Settings.ContextSize, "a chosen context size");
            Equal(24, state.Settings.GpuLayers, "a chosen offload count");
            Equal(0.9, state.Settings.Temperature, "a chosen temperature");
        });

        // ------------------------------------------------------- runtime readiness contract

        await RunAsync("a directory holding only the stub launcher is not a ready runtime", () =>
        {
            using var dir = new TempDir();
            False(RuntimeBootstrapService.IsRuntimeInstalled(dir.Path), "empty directory");

            Touch(dir.Path, "llama-server.exe");
            False(RuntimeBootstrapService.IsRuntimeInstalled(dir.Path), "llama-server.exe is a 9 KB stub and proves nothing on its own");

            Touch(dir.Path, "llama-server-impl.dll");
            False(RuntimeBootstrapService.IsRuntimeInstalled(dir.Path), "no ggml-cpu-*.dll backend means every launch fails");

            Touch(dir.Path, "ggml-cpu-x64.dll");
            True(RuntimeBootstrapService.IsRuntimeInstalled(dir.Path), "launcher + server DLL + a CPU backend is ready");
            return Task.CompletedTask;
        });

        await RunAsync("MissingRuntimeFiles names exactly the manifest files that are absent", () =>
        {
            using var dir = new TempDir();
            Equal(24, RuntimeBootstrapService.MissingRuntimeFiles(dir.Path).Count, "all files missing");
            Touch(dir.Path, "llama-server.exe");
            Touch(dir.Path, "llama.dll");
            Touch(dir.Path, "ggml.dll");
            var missing = RuntimeBootstrapService.MissingRuntimeFiles(dir.Path);
            Equal(21, missing.Count, "missing count after three files exist");
            False(missing.Contains("llama.dll"), "a present file must not be reported missing");
            return Task.CompletedTask;
        });

        await RunAsync("a complete runtime directory is reported ready without touching the network", async () =>
        {
            using var dir = new TempDir();
            foreach (var file in RuntimeBootstrapService.MissingRuntimeFiles(dir.Path)) Touch(dir.Path, file);

            using var http = new HttpClient(new ForbidNetworkHandler());
            using var service = new RuntimeBootstrapService(http);
            var result = await service.EnsureRuntimeAsync(dir.Path);
            True(result.Success, $"second run must be a no-op, got: {result.Message}");
        });

        await RunAsync("a model file of the wrong size is not treated as installed", () =>
        {
            using var dir = new TempDir();
            False(RuntimeBootstrapService.IsModelInstalled(dir.Path), "no model file at all");
            File.WriteAllBytes(Path.Combine(dir.Path, RuntimeBootstrapService.ModelFileName), new byte[4096]);
            False(RuntimeBootstrapService.IsModelInstalled(dir.Path), "a truncated GGUF must not count as installed");
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------ the downloader

        var payload = MakePayload(PayloadSize, seed: 61);
        var payloadSha = Sha256Hex(payload);

        await RunAsync("a clean response is verified, promoted, and leaves no .partial behind", async () =>
        {
            using var dir = new TempDir();
            var destination = Path.Combine(dir.Path, "payload.bin");
            await using var server = new ScriptedHttpServer((_, _, stream, ct) => Send200Async(stream, payload, ct));
            using var service = new RuntimeBootstrapService();

            var failure = await service.DownloadVerifiedForTestsAsync(server.Url, destination, payload.Length, payloadSha, null, default);
            Null(failure, "a matching payload must report no failure");
            Equal(payload.Length, (int)new FileInfo(destination).Length, "installed file length");
            Equal(payloadSha, Sha256Hex(File.ReadAllBytes(destination)), "installed file hash");
            False(File.Exists(destination + ".partial"), ".partial must be gone once the file is promoted");
        });

        await RunAsync("a response cut short mid-body is retried and resumed from where it stopped", async () =>
        {
            using var dir = new TempDir();
            var destination = Path.Combine(dir.Path, "payload.bin");
            const int cut = PayloadSize / 2;
            await using var server = new ScriptedHttpServer((attempt, from, stream, ct) => attempt == 0
                ? SendTruncatedAsync(stream, payload, cut, ct)
                : Send206Async(stream, payload, from, ct));
            using var service = new RuntimeBootstrapService();

            var failure = await service.DownloadVerifiedForTestsAsync(server.Url, destination, payload.Length, payloadSha, null, default);
            Null(failure, "the resumed download must succeed");
            Equal(payloadSha, Sha256Hex(File.ReadAllBytes(destination)), "resumed file hash");
            Equal(2, server.RangeStarts.Count, "attempt count");
            Equal(-1L, server.RangeStarts[0], "the first attempt must not ask for a range");
            Equal((long)cut, server.RangeStarts[1], "the retry must resume from the byte the first attempt reached");
        });

        await RunAsync("a body that simply ends early without an error is resumed, not reported incomplete", async () =>
        {
            // The regression this exists for: a response with no Content-Length that the server
            // closes early reads as a clean end of stream, so nothing throws. Treating "the call
            // returned" as success spent the whole retry budget on the first silent truncation and
            // failed a download that would have resumed on the next attempt.
            using var dir = new TempDir();
            var destination = Path.Combine(dir.Path, "payload.bin");
            const int cut = PayloadSize / 4;
            await using var server = new ScriptedHttpServer((attempt, from, stream, ct) => attempt == 0
                ? SendCleanShortAsync(stream, payload, cut, ct)
                : Send206Async(stream, payload, from, ct));
            using var service = new RuntimeBootstrapService();

            var failure = await service.DownloadVerifiedForTestsAsync(server.Url, destination, payload.Length, payloadSha, null, default);
            Null(failure, "a silently truncated body must be resumed, not surfaced as a failure");
            Equal(payloadSha, Sha256Hex(File.ReadAllBytes(destination)), "resumed file hash");
            Equal(2, server.RangeStarts.Count, "attempt count");
            Equal((long)cut, server.RangeStarts[1], "the retry must resume from the byte the first attempt reached");
        });

        await RunAsync("a server that ignores Range restarts the file instead of appending a second copy", async () =>
        {
            using var dir = new TempDir();
            var destination = Path.Combine(dir.Path, "payload.bin");
            File.WriteAllBytes(destination + ".partial", payload[..(PayloadSize / 2)]);
            await using var server = new ScriptedHttpServer((_, _, stream, ct) => Send200Async(stream, payload, ct));
            using var service = new RuntimeBootstrapService();

            var failure = await service.DownloadVerifiedForTestsAsync(server.Url, destination, payload.Length, payloadSha, null, default);
            Null(failure, "ignoring Range must not fail the download");
            Equal(payload.Length, (int)new FileInfo(destination).Length, "appending onto the resume point would produce 1.5x the bytes");
            Equal(payloadSha, Sha256Hex(File.ReadAllBytes(destination)), "restarted file hash");
            Equal(PayloadSize / 2L, server.RangeStarts[0], "the client must still have asked to resume");
        });

        await RunAsync("bytes that do not match the pinned hash are discarded and never installed", async () =>
        {
            using var dir = new TempDir();
            var destination = Path.Combine(dir.Path, "payload.bin");
            var wrong = MakePayload(PayloadSize, seed: 62);
            await using var server = new ScriptedHttpServer((_, _, stream, ct) => Send200Async(stream, wrong, ct));
            using var service = new RuntimeBootstrapService();

            var failure = await service.DownloadVerifiedForTestsAsync(server.Url, destination, payload.Length, payloadSha, null, default);
            NotNull(failure, "a hash mismatch must be reported");
            True(failure!.Contains("SHA-256", StringComparison.Ordinal), $"the message must say why: {failure}");
            False(File.Exists(destination), "an unverified payload must never be promoted");
            False(File.Exists(destination + ".partial"), "bytes proven wrong are untrustworthy at every offset and must be deleted");
        });

        await RunAsync("a .partial longer than the pinned size is discarded rather than resumed", async () =>
        {
            using var dir = new TempDir();
            var destination = Path.Combine(dir.Path, "payload.bin");
            File.WriteAllBytes(destination + ".partial", MakePayload(PayloadSize + 4096, seed: 63));
            await using var server = new ScriptedHttpServer((_, _, stream, ct) => Send200Async(stream, payload, ct));
            using var service = new RuntimeBootstrapService();

            var failure = await service.DownloadVerifiedForTestsAsync(server.Url, destination, payload.Length, payloadSha, null, default);
            Null(failure, "an over-long leftover must not break the download");
            Equal(payloadSha, Sha256Hex(File.ReadAllBytes(destination)), "restarted file hash");
            Equal(-1L, server.RangeStarts[0], "an over-long leftover must be dropped, so no range is requested");
        });

        await RunAsync("no pinned hash means the download is refused before any request is made", async () =>
        {
            using var dir = new TempDir();
            using var http = new HttpClient(new ForbidNetworkHandler());
            using var service = new RuntimeBootstrapService(http);

            var failure = await service.DownloadVerifiedForTestsAsync(
                "http://127.0.0.1:1/payload.bin", Path.Combine(dir.Path, "payload.bin"), payload.Length, "", null, default);
            NotNull(failure, "an unverifiable download must be refused");
            True(failure!.Contains("unverified", StringComparison.OrdinalIgnoreCase), $"the message must say why: {failure}");
        });

        await RunAsync("cancelling keeps the partial file so the next run resumes instead of restarting", async () =>
        {
            using var dir = new TempDir();
            var destination = Path.Combine(dir.Path, "payload.bin");
            await using var server = new ScriptedHttpServer((_, _, stream, ct) => Send200SlowAsync(stream, payload, 16 * 1024, 25, ct));
            using var service = new RuntimeBootstrapService();
            using var cts = new CancellationTokenSource();
            var progress = new InlineProgress<RuntimeProvisionProgress>(_ => cts.Cancel());

            var cancelled = false;
            try
            {
                await service.DownloadVerifiedForTestsAsync(server.Url, destination, payload.Length, payloadSha, progress, cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }

            True(cancelled, "cancellation must propagate, not be swallowed into a failure message");
            False(File.Exists(destination), "a cancelled download must not be promoted");
            True(File.Exists(destination + ".partial"), "the partial file is the whole point of resumable downloads");
            True(new FileInfo(destination + ".partial").Length > 0, "the partial file must retain the bytes already paid for");
        });

        await RunAsync("a token already cancelled stops before the first byte", async () =>
        {
            using var dir = new TempDir();
            var destination = Path.Combine(dir.Path, "payload.bin");
            using var http = new HttpClient(new ForbidNetworkHandler());
            using var service = new RuntimeBootstrapService(http);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var cancelled = false;
            try
            {
                await service.DownloadVerifiedForTestsAsync("http://127.0.0.1:1/payload.bin", destination, payload.Length, payloadSha, null, cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }

            True(cancelled, "a pre-cancelled token must abort immediately");
            False(File.Exists(destination), "nothing may be installed");
        });

        await RunAsync("a download that never completes stops after four attempts and keeps its progress", async () =>
        {
            using var dir = new TempDir();
            var destination = Path.Combine(dir.Path, "payload.bin");
            const int cut = 32 * 1024;
            // Always short, and always from zero: the server neither honours Range nor finishes.
            await using var server = new ScriptedHttpServer((_, _, stream, ct) => SendCleanShortAsync(stream, payload, cut, ct));
            using var service = new RuntimeBootstrapService();

            var failure = await service.DownloadVerifiedForTestsAsync(server.Url, destination, payload.Length, payloadSha, null, default);
            NotNull(failure, "an unfinishable download must be reported");
            True(failure!.Contains("resume", StringComparison.OrdinalIgnoreCase), $"the message must tell the user retrying resumes: {failure}");
            Equal(4, server.RangeStarts.Count, "the retry budget is four attempts");
            False(File.Exists(destination), "an incomplete payload must never be promoted");
            Equal((long)cut, new FileInfo(destination + ".partial").Length, "progress must be kept for the next run");
        });

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"Runtime provisioning tests failed: {failures.Count}");
            foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
            return 1;
        }

        Console.WriteLine($"Runtime provisioning tests passed: {passed}");
        return 0;
    }

    // ------------------------------------------------------------------------- assertions

    private static void True(bool value, string label)
    {
        if (!value) throw new Exception(label);
    }

    private static void False(bool value, string label)
    {
        if (value) throw new Exception(label);
    }

    private static void Null(object? value, string label)
    {
        if (value is not null) throw new Exception($"{label}: expected null, got {value}");
    }

    private static void NotNull(object? value, string label)
    {
        if (value is null) throw new Exception($"{label}: expected a value, got null");
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }

    // ----------------------------------------------------------------------------- helpers

    private static byte[] MakePayload(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Touch(string directory, string fileName) => File.WriteAllBytes(Path.Combine(directory, fileName), []);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dmai-provisioning-tests", Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        // Progress<T> posts to a synchronization context, which makes "cancel from the first
        // progress report" non-deterministic in a console host. This runs the handler inline.
        public void Report(T value) => handler(value);
    }

    private sealed class ForbidNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"No HTTP request was expected, but one was made to {request.RequestUri}.");
    }

    // ------------------------------------------------------------------ scripted responses

    private static async Task WriteHeadAsync(Stream stream, string head, CancellationToken ct) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);

    /// <summary>A complete, well-formed 200 carrying the whole body.</summary>
    private static async Task Send200Async(Stream stream, byte[] body, CancellationToken ct)
    {
        await WriteHeadAsync(stream,
            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n", ct);
        await stream.WriteAsync(body, ct);
    }

    /// <summary>A correct 206 answering a Range request, as GitHub and Hugging Face both do.</summary>
    private static async Task Send206Async(Stream stream, byte[] body, long from, CancellationToken ct)
    {
        var slice = body.AsMemory((int)from);
        await WriteHeadAsync(stream,
            $"HTTP/1.1 206 Partial Content\r\nContent-Length: {slice.Length}\r\n"
            + $"Content-Range: bytes {from}-{body.Length - 1}/{body.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n", ct);
        await stream.WriteAsync(slice, ct);
    }

    /// <summary>
    /// Promises the full length and then hangs up early. The client raises an IOException, which is
    /// the loud, obvious form of a dropped transfer.
    /// </summary>
    private static async Task SendTruncatedAsync(Stream stream, byte[] body, int count, CancellationToken ct)
    {
        await WriteHeadAsync(stream,
            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n", ct);
        await stream.WriteAsync(body.AsMemory(0, count), ct);
    }

    /// <summary>
    /// Declares no length at all and lets the close delimit the body, then closes early. This is the
    /// quiet form of a dropped transfer: the client sees a clean end of stream and nothing throws.
    /// </summary>
    private static async Task SendCleanShortAsync(Stream stream, byte[] body, int count, CancellationToken ct)
    {
        await WriteHeadAsync(stream, "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n", ct);
        await stream.WriteAsync(body.AsMemory(0, count), ct);
    }

    /// <summary>Trickles the body so a cancellation has somewhere to land.</summary>
    private static async Task Send200SlowAsync(Stream stream, byte[] body, int chunk, int delayMs, CancellationToken ct)
    {
        await WriteHeadAsync(stream,
            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n", ct);
        for (var offset = 0; offset < body.Length; offset += chunk)
        {
            var take = Math.Min(chunk, body.Length - offset);
            await stream.WriteAsync(body.AsMemory(offset, take), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(delayMs, ct);
        }
    }

    /// <summary>
    /// A loopback HTTP/1.1 server small enough to script byte-for-byte.
    ///
    /// HttpListener is not used deliberately: reserving a URL prefix needs an administrator on
    /// Windows, and none of the failure modes under test -- an early hang-up, a body with no
    /// declared length, a server that ignores Range -- can be expressed through it.
    /// </summary>
    private sealed class ScriptedHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<int, long, Stream, CancellationToken, Task> _respond;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        /// <summary>The Range start each request carried, in order; -1 when it carried none.</summary>
        public List<long> RangeStarts { get; } = [];

        public string Url { get; }

        public ScriptedHttpServer(Func<int, long, Stream, CancellationToken, Task> respond)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/payload.bin";
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var head = await ReadRequestHeadAsync(stream, _cts.Token);
                    if (head is null) return;

                    var rangeStart = ParseRangeStart(head);
                    int attempt;
                    lock (RangeStarts)
                    {
                        RangeStarts.Add(rangeStart);
                        attempt = RangeStarts.Count - 1;
                    }

                    await _respond(attempt, rangeStart < 0 ? 0 : rangeStart, stream, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
                catch
                {
                    // A client that hangs up mid-response (a cancelled download) is expected here.
                }
            }
        }

        private static async Task<string?> ReadRequestHeadAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (read == 0) return null;
                total += read;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
            }
            return null;
        }

        private static long ParseRangeStart(string head)
        {
            foreach (var line in head.Split("\r\n"))
            {
                if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var dash = line.IndexOf('-', eq);
                var start = dash < 0 ? line[(eq + 1)..] : line[(eq + 1)..dash];
                return long.TryParse(start.Trim(), out var value) ? value : -1;
            }
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch { }
            _cts.Dispose();
        }
    }
}
