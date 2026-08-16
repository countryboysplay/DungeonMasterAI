using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DungeonMasterAI.AI;

public sealed record RuntimeProvisionProgress(string Stage, double? Fraction, string Message);
public sealed record RuntimeProvisionResult(bool Success, string Message, string? Version = null, string? ExecutablePath = null);

public sealed class RuntimeBootstrapService(HttpClient? httpClient = null)
{
    private const string LatestReleaseApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

    public async Task<RuntimeProvisionResult> EnsureRuntimeAsync(
        string runtimeDirectory,
        IProgress<RuntimeProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(runtimeDirectory);
        var executable = Path.Combine(runtimeDirectory, "llama-server.exe");
        if (File.Exists(executable))
            return new RuntimeProvisionResult(true, "Local AI runtime is already installed.", ReadInstalledVersion(runtimeDirectory), executable);

        progress?.Report(new RuntimeProvisionProgress("metadata", null, "Finding the latest llama.cpp Windows runtime..."));
        using var releaseRequest = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        releaseRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("DungeonMasterAI", "0.1"));
        releaseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var releaseResponse = await _http.SendAsync(releaseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();

        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "unknown" : "unknown";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return new RuntimeProvisionResult(false, "The latest llama.cpp release did not include downloadable assets.");

        var candidates = assets.EnumerateArray()
            .Select(a => new
            {
                Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
                Size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0L
            })
            .Where(a => !string.IsNullOrWhiteSpace(a.Url))
            .ToArray();

        var asset = candidates.FirstOrDefault(a => a.Name.Contains("win-vulkan-x64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(a => a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return new RuntimeProvisionResult(false, "No compatible x64 Windows llama.cpp runtime was found in the latest release.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "DungeonMasterAI", "llama-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var archive = Path.Combine(tempRoot, asset.Name);
        var extract = Path.Combine(tempRoot, "extract");
        try
        {
            progress?.Report(new RuntimeProvisionProgress("download", 0, $"Downloading {asset.Name}..."));
            await DownloadAsync(asset.Url, archive, asset.Size, progress, cancellationToken);
            progress?.Report(new RuntimeProvisionProgress("extract", null, "Installing local AI runtime..."));
            ZipFile.ExtractToDirectory(archive, extract, overwriteFiles: true);

            var server = Directory.EnumerateFiles(extract, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (server is null)
                return new RuntimeProvisionResult(false, "The downloaded runtime did not contain llama-server.exe.");

            var sourceDir = Path.GetDirectoryName(server)!;
            CopyDirectory(sourceDir, runtimeDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeDirectory, "runtime-version.txt"), tag, cancellationToken);
            executable = Path.Combine(runtimeDirectory, "llama-server.exe");
            if (!File.Exists(executable))
                return new RuntimeProvisionResult(false, "Runtime extraction completed but llama-server.exe was not installed.");

            progress?.Report(new RuntimeProvisionProgress("complete", 1, $"llama.cpp {tag} installed."));
            return new RuntimeProvisionResult(true, "Local AI runtime installed.", tag, executable);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new RuntimeProvisionResult(false, $"Runtime installation failed: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        long expectedSize,
        IProgress<RuntimeProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DungeonMasterAI", "0.1"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? (expectedSize > 0 ? expectedSize : null);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        var buffer = new byte[1024 * 128];
        long received = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            var fraction = total is > 0 ? Math.Clamp((double)received / total.Value, 0, 1) : (double?)null;
            progress?.Report(new RuntimeProvisionProgress("download", fraction, total is > 0
                ? $"Downloading local AI runtime... {received / 1024d / 1024d:0.0} / {total.Value / 1024d / 1024d:0.0} MB"
                : $"Downloading local AI runtime... {received / 1024d / 1024d:0.0} MB"));
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string? ReadInstalledVersion(string runtimeDirectory)
    {
        var path = Path.Combine(runtimeDirectory, "runtime-version.txt");
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }
}
