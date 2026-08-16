using System.Diagnostics;
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
            catch (InvalidOperationException) { return false; }
            catch (ObjectDisposedException) { return false; }
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
        if (!File.Exists(exe))
        {
            LastError = "Bundled llama-server.exe is not installed yet.";
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

            start.ArgumentList.Add("--host"); start.ArgumentList.Add("127.0.0.1");
            start.ArgumentList.Add("--port"); start.ArgumentList.Add(port.ToString());
            start.ArgumentList.Add("--jinja");
            start.ArgumentList.Add("--ctx-size"); start.ArgumentList.Add(Math.Clamp(settings.ContextSize, 4096, 131072).ToString());
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
                catch (InvalidOperationException) { }
                catch (ObjectDisposedException) { }
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
