using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.AI;

public sealed class LlamaRuntimeManager : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _log = new();
    public bool IsRunning
    {
        get
        {
            var process = _process;
            try { return process is { HasExited: false }; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public string? LastError { get; private set; }
    public string RecentLog
    {
        get
        {
            lock (_log) return _log.ToString();
        }
    }

    public string RuntimeDirectory { get; }
    public string ModelDirectory { get; }

    public LlamaRuntimeManager(string baseDirectory)
    {
        RuntimeDirectory = Path.Combine(baseDirectory, "Runtime");
        ModelDirectory = Path.Combine(baseDirectory, "Models");
    }

    public bool TryStart(AppSettings settings)
    {
        if (IsRunning) return true;
        LastError = null;
        lock (_log) _log.Clear();

        var exe = Path.Combine(RuntimeDirectory, "llama-server.exe");
        // llama-server.exe alone proves nothing: it is a 9 KB stub launcher for
        // llama-server-impl.dll. Require the whole readiness manifest before claiming the runtime
        // is usable, so a truncated install fails here with a clear message instead of at launch.
        if (!RuntimeBootstrapService.IsRuntimeInstalled(RuntimeDirectory))
        {
            var missing = RuntimeBootstrapService.MissingRuntimeFiles(RuntimeDirectory);
            LastError = missing.Count == 0
                ? "The bundled llama.cpp runtime is not installed yet."
                : $"The bundled llama.cpp runtime is incomplete. Missing: {string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? $" (+{missing.Count - 6} more)" : "")}.";
            return false;
        }

        var localModel = ResolveModelPath(settings);
        var huggingFaceModel = string.IsNullOrWhiteSpace(settings.HuggingFaceModel) ? null : settings.HuggingFaceModel.Trim();
        if (localModel is null && huggingFaceModel is null)
        {
            LastError = "No local GGUF model or Hugging Face model is configured.";
            return false;
        }

        try
        {
            var uri = new Uri(settings.LlamaServerUrl);
            var port = uri.Port;
            var start = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = RuntimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (localModel is not null)
            {
                start.ArgumentList.Add("-m");
                start.ArgumentList.Add(localModel);
            }
            else
            {
                start.ArgumentList.Add("-hf");
                start.ArgumentList.Add(huggingFaceModel!);
            }

            // SECURITY BOUNDARY -- read before adding a flag here.
            //
            // Never pass --tools or --agent to llama-server. Those switches enable llama.cpp's own
            // server-side tool layer, which exposes shell and filesystem tools directly to the
            // model. The entire determinism and safety model of this application rests on the model
            // being able to change game reality only through DmToolRouter's allow-list, and those
            // flags would hand it a way around that allow-list completely. The only tools the model
            // may ever see are the ones this app sends in the request body.
            start.ArgumentList.Add("--host"); start.ArgumentList.Add("127.0.0.1");
            start.ArgumentList.Add("--port"); start.ArgumentList.Add(port.ToString());
            start.ArgumentList.Add("--jinja");
            start.ArgumentList.Add("--ctx-size"); start.ArgumentList.Add(Math.Clamp(settings.ContextSize, 4096, 131072).ToString());
            // The Qwen3.5 GGUF repository also publishes mmproj-*.gguf vision projectors. Refuse the
            // multimodal path explicitly: this app sends text and tool calls only.
            start.ArgumentList.Add("--no-mmproj");
            // No browser UI. The server is an internal implementation detail bound to loopback, and
            // its web console is another surface with no purpose here.
            start.ArgumentList.Add("--no-webui");
            // Physical cores, not logical. Oversubscribing SMT siblings slows llama.cpp prefill.
            start.ArgumentList.Add("-t"); start.ArgumentList.Add(PhysicalCoreCount().ToString());
            if (settings.GpuLayers >= 0)
            {
                start.ArgumentList.Add("--gpu-layers");
                start.ArgumentList.Add(settings.GpuLayers.ToString());
            }

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => AppendLog(e.Data);
            process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
            process.Exited += (sender, _) =>
            {
                // Read the exit code from the sender, not the _process field: Stop() can
                // dispose and null the field concurrently, and an exception thrown here
                // would surface on a thread-pool callback with no catch frame above it.
                try
                {
                    if (sender is Process exited && exited.ExitCode != 0)
                        LastError = $"Local AI runtime exited with code {exited.ExitCode}.";
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            };
            _process = process;
            if (!process.Start())
            {
                process.Dispose();
                _process = null;
                LastError = "Windows could not start the local AI runtime.";
                return false;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _process?.Dispose();
            _process = null;
            return false;
        }
    }

    public async Task<bool> WaitUntilReadyAsync(LocalDmClient client, AppSettings settings, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is { HasExited: true })
            {
                LastError ??= $"Local AI runtime exited with code {_process.ExitCode}.";
                return false;
            }
            var status = await client.CheckAsync(settings, cancellationToken);
            if (status.Online) return true;
            await Task.Delay(750, cancellationToken);
        }
        LastError = "Local AI runtime did not become ready before the startup timeout.";
        return false;
    }

    public void Stop()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(3000);
        }
        catch { }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private string? ResolveModelPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ModelPath))
        {
            var configured = Path.IsPathRooted(settings.ModelPath) ? settings.ModelPath : Path.Combine(ModelDirectory, settings.ModelPath);
            if (File.Exists(configured)) return configured;
        }
        return Directory.Exists(ModelDirectory) ? Directory.EnumerateFiles(ModelDirectory, "*.gguf").FirstOrDefault() : null;
    }

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    /// <summary>
    /// Physical core count for llama.cpp's -t argument.
    ///
    /// Environment.ProcessorCount reports logical processors, which is double the useful thread
    /// count on an SMT part; oversubscribing the siblings makes prefill slower, not faster. Walk the
    /// RelationProcessorCore records instead. Each record begins with DWORD Relationship followed by
    /// DWORD Size, so the buffer can be traversed by Size alone without marshalling the rest.
    /// </summary>
    private static int PhysicalCoreCount()
    {
        var logical = Math.Max(1, Environment.ProcessorCount);
        if (!OperatingSystem.IsWindows()) return logical;
        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
            if (length == 0) return logical;

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) return logical;
                var cores = 0;
                var offset = 0;
                while (offset + 8 <= (int)length)
                {
                    var size = Marshal.ReadInt32(buffer, offset + 4);
                    if (size <= 0) break;
                    cores++;
                    offset += size;
                }
                return cores > 0 ? Math.Min(cores, logical) : logical;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            return logical;
        }
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_log)
        {
            _log.AppendLine(line);
            const int maxChars = 24000;
            if (_log.Length > maxChars) _log.Remove(0, _log.Length - maxChars);
        }
    }

    public void Dispose() => Stop();
}
