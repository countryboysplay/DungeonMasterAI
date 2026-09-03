using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("DungeonMasterAI.RuntimeProvisioningTests")]

namespace DungeonMasterAI.AI;

public sealed record RuntimeProvisionProgress(string Stage, double? Fraction, string Message);
public sealed record RuntimeProvisionResult(bool Success, string Message, string? Version = null, string? ExecutablePath = null);
public sealed record ModelProvisionResult(bool Success, string Message, string? ModelPath = null);

/// <summary>
/// Provisions the two large binary dependencies the local DM needs: the llama.cpp server runtime
/// and the narration GGUF.
///
/// The runtime is normally already present, because the installer ships the pinned CPU build that
/// tools/fetch-llama-runtime.ps1 vendors in CI. The download path here exists for developer builds
/// and for repairing a damaged install; it is not the expected first-run experience.
///
/// The model is the opposite: it is never bundled. At 2.55 GiB the GGUF would push the setup
/// executable past the 2 GiB per-file limit on GitHub Releases, so it is fetched once on first run
/// over this resumable, size-and-SHA-256-verified path.
/// </summary>
/// <remarks>
/// The type is partial because <see cref="BuildTagPattern"/> is a [GeneratedRegex] method: the
/// regex source generator emits its implementation into a second, partial declaration of this
/// class, so this declaration has to be partial too.
/// </remarks>
public sealed partial class RuntimeBootstrapService : IDisposable
{
    private readonly bool _ownsHttp;

    public RuntimeBootstrapService(HttpClient? httpClient = null)
    {
        _ownsHttp = httpClient is null;
        // Timeout.InfiniteTimeSpan, not a finite value. HttpClient.Timeout spans the whole
        // operation including draining the response body, so any finite timeout is really a
        // minimum-throughput requirement on a 2.55 GiB download -- and it surfaces as a
        // TaskCanceledException that is indistinguishable from the user pressing Stop. The
        // caller's CancellationToken is the only cancellation signal.
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private const string UserAgentProduct = "DungeonMasterAI";
    private const string UserAgentVersion = "0.1";
    private const string VersionFileName = "runtime-version.txt";
    private const int CopyBufferBytes = 1024 * 128;

    private static readonly Lazy<ProvisionLock> RuntimeLock = new(() => LoadLock("runtime.lock.json"));
    private static readonly Lazy<ProvisionLock> ModelLock = new(() => LoadLock("model.lock.json"));

    private readonly HttpClient _http;

    /// <summary>The GGUF filename the app expects in its Models directory.</summary>
    public static string ModelFileName => ModelLock.Value.File;

    /// <summary>Human-readable name of the pinned model, for UI copy.</summary>
    public static string ModelDisplayName => ModelLock.Value.DisplayName;

    /// <summary>Download size of the pinned model, for UI copy.</summary>
    public static long ModelSizeBytes => ModelLock.Value.SizeBytes;

    /// <summary>The pinned llama.cpp build tag that the installer ships.</summary>
    public static string PinnedRuntimeTag => RuntimeLock.Value.Tag;

    /// <summary>
    /// Readiness contract for the runtime directory.
    ///
    /// llama-server.exe is a 9,216-byte stub launcher; the actual server is llama-server-impl.dll,
    /// and inference needs at least one of the runtime-dispatched ggml-cpu-*.dll backends. Checking
    /// only for the exe (which is what every call site used to do) reports "Runtime installed" for a
    /// half-extracted or half-copied directory that fails on every single launch.
    /// </summary>
    public static bool IsRuntimeInstalled(string runtimeDirectory)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory)) return false;
        if (!File.Exists(Path.Combine(runtimeDirectory, "llama-server.exe"))) return false;
        if (!File.Exists(Path.Combine(runtimeDirectory, "llama-server-impl.dll"))) return false;
        return Directory.EnumerateFiles(runtimeDirectory, "ggml-cpu-*.dll").Any();
    }

    /// <summary>Lists the manifest files that are missing from an incomplete runtime directory.</summary>
    public static IReadOnlyList<string> MissingRuntimeFiles(string runtimeDirectory)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
            return RuntimeLock.Value.Files;
        return RuntimeLock.Value.Files.Where(f => !File.Exists(Path.Combine(runtimeDirectory, f))).ToArray();
    }

    /// <summary>True when the pinned GGUF is present at its full expected size.</summary>
    public static bool IsModelInstalled(string modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory)) return false;
        var path = Path.Combine(modelDirectory, ModelLock.Value.File);
        return File.Exists(path) && new FileInfo(path).Length == ModelLock.Value.SizeBytes;
    }

    public static string ModelDownloadNotice =>
        $"{ModelLock.Value.DisplayName} is not installed yet. It is a one-time {Humanize(ModelLock.Value.SizeBytes)} download "
        + $"from Hugging Face ({ModelLock.Value.Repository}, {ModelLock.Value.License}). It is not included in the installer because it "
        + "is far too large to ship there. The download resumes if it is interrupted and is verified against a pinned SHA-256 "
        + "before it is used. Nothing else about the app needs the internet.";

    // Every public entry point hops to the thread pool before touching the filesystem. These
    // methods are invoked from an AsyncRelayCommand on the WPF dispatcher, and the zip extraction,
    // manifest copy, temp-tree delete and SHA-256 pass are all synchronous multi-second work that
    // would otherwise freeze the window. ConfigureAwait(false) throughout keeps continuations off
    // the dispatcher too.

    public Task<RuntimeProvisionResult> EnsureRuntimeAsync(
        string runtimeDirectory,
        IProgress<RuntimeProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => EnsureRuntimeCoreAsync(runtimeDirectory, progress, cancellationToken), cancellationToken);

    public Task<ModelProvisionResult> EnsureModelAsync(
        string modelDirectory,
        IProgress<RuntimeProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => EnsureModelCoreAsync(modelDirectory, progress, cancellationToken), cancellationToken);

    private async Task<RuntimeProvisionResult> EnsureRuntimeCoreAsync(
        string runtimeDirectory,
        IProgress<RuntimeProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var lockData = RuntimeLock.Value;
        var throttle = new ProgressThrottle(progress);
        Directory.CreateDirectory(runtimeDirectory);
        var executable = Path.Combine(runtimeDirectory, "llama-server.exe");

        if (IsRuntimeInstalled(runtimeDirectory))
            return new RuntimeProvisionResult(true, "Local AI runtime is installed.", ReadInstalledVersion(runtimeDirectory) ?? lockData.Tag, executable);

        var missing = MissingRuntimeFiles(runtimeDirectory);
        throttle.Report(new RuntimeProvisionProgress("metadata", null,
            $"The bundled llama.cpp runtime is incomplete ({missing.Count} of {lockData.Files.Count} files missing). Repairing it..."), force: true);

        var tempRoot = Path.Combine(Path.GetTempPath(), "DungeonMasterAI", "llama-runtime", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var extract = Path.Combine(tempRoot, "extract");

            // The pinned asset is the primary source and needs no API call at all, which keeps this
            // path clear of the 60-requests-per-hour unauthenticated GitHub rate limit. The API is
            // only consulted if the pinned asset has gone missing, and that fetch lives inside the
            // try so a network failure returns a message instead of escaping as an exception.
            var tag = lockData.Tag;
            var assetName = lockData.Asset;
            var url = lockData.Url;
            var size = lockData.SizeBytes;
            var sha256 = lockData.Sha256;

            if (!await AssetExistsAsync(url, cancellationToken).ConfigureAwait(false))
            {
                throttle.Report(new RuntimeProvisionProgress("metadata", null,
                    $"Pinned llama.cpp build {lockData.Tag} is no longer downloadable. Looking for the newest published build..."), force: true);
                var resolved = await ResolveNewestBuildAssetAsync(lockData.Repository, cancellationToken).ConfigureAwait(false);
                if (resolved.Error is not null) return new RuntimeProvisionResult(false, resolved.Error);
                tag = resolved.Tag!;
                assetName = resolved.Asset!;
                url = resolved.Url!;
                size = resolved.SizeBytes;
                sha256 = resolved.Sha256!;
            }

            var archive = Path.Combine(tempRoot, assetName);
            throttle.Report(new RuntimeProvisionProgress("download", 0, $"Downloading the llama.cpp runtime ({Humanize(size)})..."), force: true);
            var failure = await DownloadVerifiedAsync(url, archive, size, sha256, "local AI runtime", throttle, cancellationToken).ConfigureAwait(false);
            if (failure is not null) return new RuntimeProvisionResult(false, failure);

            throttle.Report(new RuntimeProvisionProgress("extract", null, "Installing the local AI runtime..."), force: true);
            ZipFile.ExtractToDirectory(archive, extract, overwriteFiles: true);

            // Validate the extracted tree against the manifest and copy only the manifest files.
            // The old code located llama-server.exe, then copied that whole directory verbatim --
            // an unverified tree of 51 files including an RPC server and several general-purpose
            // inference CLIs -- into the install directory.
            var resolvedFiles = new List<(string Name, string Source)>(lockData.Files.Count);
            foreach (var name in lockData.Files)
            {
                var source = Directory.EnumerateFiles(extract, name, SearchOption.AllDirectories).FirstOrDefault();
                if (source is null)
                    return new RuntimeProvisionResult(false, $"The downloaded llama.cpp runtime is missing the required file '{name}'. Nothing was installed.");
                resolvedFiles.Add((name, source));
            }

            foreach (var (name, source) in resolvedFiles)
                File.Copy(source, Path.Combine(runtimeDirectory, name), overwrite: true);

            await File.WriteAllTextAsync(Path.Combine(runtimeDirectory, VersionFileName), tag, cancellationToken).ConfigureAwait(false);
            if (!IsRuntimeInstalled(runtimeDirectory))
                return new RuntimeProvisionResult(false, "The runtime files were copied but the installed runtime still fails its readiness check.");

            throttle.Report(new RuntimeProvisionProgress("complete", 1, $"llama.cpp {tag} installed."), force: true);
            return new RuntimeProvisionResult(true, $"Local AI runtime {tag} installed.", tag, executable);
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

    private async Task<ModelProvisionResult> EnsureModelCoreAsync(
        string modelDirectory,
        IProgress<RuntimeProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var lockData = ModelLock.Value;
        var throttle = new ProgressThrottle(progress);
        Directory.CreateDirectory(modelDirectory);
        var destination = Path.Combine(modelDirectory, lockData.File);

        if (IsModelInstalled(modelDirectory))
            return new ModelProvisionResult(true, $"{lockData.DisplayName} is installed.", destination);

        try
        {
            // Say plainly what is about to happen and why it is not in the installer. A silent
            // multi-gigabyte download on first launch is not an acceptable first-run experience.
            throttle.Report(new RuntimeProvisionProgress("download", 0, ModelDownloadNotice), force: true);
            var failure = await DownloadVerifiedAsync(
                lockData.Url, destination, lockData.SizeBytes, lockData.Sha256, lockData.DisplayName, throttle, cancellationToken).ConfigureAwait(false);
            if (failure is not null) return new ModelProvisionResult(false, failure);

            throttle.Report(new RuntimeProvisionProgress("complete", 1, $"{lockData.DisplayName} is downloaded and verified."), force: true);
            return new ModelProvisionResult(true, $"{lockData.DisplayName} is downloaded and verified.", destination);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ModelProvisionResult(false, $"Model download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads to "<paramref name="destination"/>.partial", resuming from whatever is already on
    /// disk, then verifies size and SHA-256 before promoting the file into place. Returns null on
    /// success or a user-facing message on failure.
    ///
    /// The partial file is deliberately left behind on a network failure so a cancelled or dropped
    /// 2.55 GiB download does not have to start over. It is deleted only when the bytes are proven
    /// wrong, because a hash mismatch means the file cannot be trusted at any offset.
    /// </summary>
    private async Task<string?> DownloadVerifiedAsync(
        string url,
        string destination,
        long expectedSize,
        string expectedSha256,
        string label,
        ProgressThrottle progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return $"No pinned SHA-256 is available for the {label}. Refusing to install an unverified download.";

        var partial = destination + ".partial";
        const int maxAttempts = 4;
        string? lastFailure = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0L;
            // A .partial longer than the lock says the finished file is cannot be resumed into
            // anything that will pass the hash, so it is discarded rather than appended to.
            if (existing > expectedSize)
            {
                TryDelete(partial);
                existing = 0;
            }
            if (existing == expectedSize) break;

            try
            {
                await DownloadFromOffsetAsync(url, partial, existing, expectedSize, label, progress, cancellationToken).ConfigureAwait(false);
                // Deliberately no break. A response can end short without throwing -- a chunked
                // transfer that is simply cut off reads as a clean end of stream -- so completion
                // is decided by re-measuring the file at the top of the next iteration, not by the
                // call having returned. Breaking here spent the entire retry budget on the first
                // silent truncation and reported "incomplete" on a download that would have
                // resumed fine.
                lastFailure = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // An OperationCanceledException the caller's token did not raise is a client-side
                // timeout, not the user pressing Stop, and it is as retryable as a dropped socket.
                // Only the guarded catch above -- the one that checks the token -- aborts.
                lastFailure = ex.Message;
                if (attempt == maxAttempts) break;
                progress.Report(new RuntimeProvisionProgress("download", null,
                    $"The {label} download was interrupted ({ex.Message}). Resuming in {2 * attempt}s..."), force: true);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var length = File.Exists(partial) ? new FileInfo(partial).Length : 0L;
        if (length != expectedSize)
            return lastFailure is null
                ? $"The {label} download stopped at {Humanize(length)} of {Humanize(expectedSize)} after {maxAttempts} attempts. Partial progress was kept; run setup again to resume."
                : $"The {label} download failed after {maxAttempts} attempts: {lastFailure}. Partial progress was kept, so retrying resumes where it stopped.";

        progress.Report(new RuntimeProvisionProgress("verify", null, $"Verifying the {label} against its pinned SHA-256..."), force: true);
        var actual = await ComputeSha256Async(partial, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partial);
            return $"The downloaded {label} failed SHA-256 verification (expected {expectedSha256}, got {actual}). The file was discarded and was not installed.";
        }

        File.Move(partial, destination, overwrite: true);
        return null;
    }

    private async Task DownloadFromOffsetAsync(
        string url,
        string partialPath,
        long offset,
        long expectedSize,
        string label,
        ProgressThrottle progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProduct, UserAgentVersion));
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // If a range was requested and the server answered 200 rather than 206 it ignored the range
        // and is sending the whole file again, so restart the local file from zero instead of
        // appending a second copy onto the first.
        var resuming = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        var received = resuming ? offset : 0L;
        if (offset > 0 && !resuming)
            progress.Report(new RuntimeProvisionProgress("download", 0, $"The server did not honour resume. Restarting the {label} download..."), force: true);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            resuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            useAsync: true);

        var buffer = new byte[CopyBufferBytes];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            var fraction = expectedSize > 0 ? Math.Clamp((double)received / expectedSize, 0, 1) : (double?)null;
            progress.Report(new RuntimeProvisionProgress("download", fraction,
                expectedSize > 0
                    ? $"Downloading {label}... {Humanize(received)} of {Humanize(expectedSize)}"
                    : $"Downloading {label}... {Humanize(received)}"));
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam for <see cref="DownloadVerifiedAsync"/>.
    ///
    /// The resume, retry, hash-gate and partial-retention rules are the riskiest code in this file
    /// and they are unreachable from the public surface without a 2.55 GiB network round trip,
    /// because the URL, size and hash all come from the embedded lock. This lets the suite drive
    /// exactly that method against a local socket. It adds nothing to the public API.
    /// </summary>
    internal Task<string?> DownloadVerifiedForTestsAsync(
        string url,
        string destination,
        long expectedSize,
        string expectedSha256,
        IProgress<RuntimeProvisionProgress>? progress,
        CancellationToken cancellationToken) =>
        DownloadVerifiedAsync(url, destination, expectedSize, expectedSha256, "test payload", new ProgressThrottle(progress), cancellationToken);

    /// <summary>Cheap existence probe for the pinned asset, so the API is only used as a fallback.</summary>
    private async Task<bool> AssetExistsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProduct, UserAgentVersion));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
    }

    private sealed record ResolvedAsset(string? Tag, string? Asset, string? Url, long SizeBytes, string? Sha256, string? Error);

    /// <summary>
    /// Enumerates recent releases and picks the newest real build.
    ///
    /// /releases/latest cannot be used: every llama.cpp build release (b10786, b10785, ... roughly
    /// 48 a day) is published as a prerelease, so /releases/latest skips all of them and resolves to
    /// the v0.3.0 marker release, whose only asset is nightly-tag.txt.
    /// </summary>
    private async Task<ResolvedAsset> ResolveNewestBuildAssetAsync(string repository, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page=30");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProduct, UserAgentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        // Unauthenticated GitHub API calls are limited to 60 per hour per address and answer 403
        // (or 429) when that is exhausted. This only ever runs when the pinned runtime asset has
        // vanished, so say clearly that the bundled runtime is unaffected and that the internet is
        // only needed for the model.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var wait = DescribeRetryAfter(response);
            return new ResolvedAsset(null, null, null, 0, null,
                $"GitHub is rate limiting this machine{wait}. The bundled llama.cpp runtime already works and does not need this; "
                + "only the one-time model download requires the internet. Try again later, or install the runtime manually.");
        }
        if (!response.IsSuccessStatusCode)
            return new ResolvedAsset(null, null, null, 0, null, $"GitHub returned HTTP {(int)response.StatusCode} when listing llama.cpp releases.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new ResolvedAsset(null, null, null, 0, null, "GitHub returned an unexpected response when listing llama.cpp releases.");

        var builds = new List<(long Number, JsonElement Release)>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tag = release.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "" : "";
            var match = BuildTagPattern().Match(tag);
            if (match.Success && long.TryParse(match.Groups[1].Value, out var number))
                builds.Add((number, release));
        }

        foreach (var (_, release) in builds.OrderByDescending(b => b.Number))
        {
            var tag = release.GetProperty("tag_name").GetString()!;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                // Match the CPU asset by its exact suffix. The old filter looked for the substring
                // "win-x64", which matches nothing: the asset is named "...-bin-win-cpu-x64.zip".
                if (!name.EndsWith("-bin-win-cpu-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;

                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                // A missing digest is a hard skip, not a downgrade to a size-only check. This asset
                // is an executable the app is about to launch.
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) continue;

                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var parsed) ? parsed : 0L;
                if (string.IsNullOrWhiteSpace(url) || size <= 0) continue;

                return new ResolvedAsset(tag, name, url, size, digest["sha256:".Length..].Trim(), null);
            }
        }

        return new ResolvedAsset(null, null, null, 0, null,
            "No recent llama.cpp release published a Windows CPU runtime with a verifiable SHA-256 digest.");
    }

    private static string DescribeRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return $" (retry after {Math.Ceiling(delta.TotalMinutes)} minute(s))";
        if (response.Headers.RetryAfter?.Date is { } date)
            return $" (retry after {date.ToLocalTime():t})";
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var epoch))
            return $" (limit resets at {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():t})";
        return "";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ReadInstalledVersion(string runtimeDirectory)
    {
        var path = Path.Combine(runtimeDirectory, VersionFileName);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Humanize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024d / 1024d / 1024d:0.00} GB"
        : $"{bytes / 1024d / 1024d:0.0} MB";

    [GeneratedRegex(@"^b(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildTagPattern();

    private static ProvisionLock LoadLock(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded provisioning lock '{resourceName}' is missing from the build.");
        return JsonSerializer.Deserialize<ProvisionLock>(stream, LockJsonOptions)
            ?? throw new InvalidOperationException($"Embedded provisioning lock '{resourceName}' could not be parsed.");
    }

    private static readonly JsonSerializerOptions LockJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Shape of runtime.lock.json and model.lock.json. Both are embedded into this assembly so the
    /// PowerShell vendoring script and the runtime provisioner read the same pinned values.
    /// </summary>
    private sealed class ProvisionLock
    {
        public string Repository { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Asset { get; init; } = "";
        public string Revision { get; init; } = "";
        public string File { get; init; } = "";
        public string Url { get; init; } = "";
        public long SizeBytes { get; init; }
        public string Sha256 { get; init; } = "";
        public string License { get; init; } = "";
        public string DisplayName { get; init; } = "";

        [JsonPropertyName("files")]
        public string[] FileList { get; init; } = [];

        [JsonIgnore]
        public IReadOnlyList<string> Files => FileList;
    }

    /// <summary>
    /// Rate limiter for <see cref="IProgress{T}"/> reports.
    ///
    /// Progress&lt;T&gt; captures the dispatcher's synchronization context, so every report is a post
    /// to the UI thread. A 2.55 GiB download reporting once per 128 KB buffer is roughly 21,000 of
    /// them; at four per second it is a few hundred.
    /// </summary>
    private sealed class ProgressThrottle(IProgress<RuntimeProvisionProgress>? inner)
    {
        private const int MinimumIntervalMs = 250;
        private long _lastReport = long.MinValue / 2;

        public void Report(RuntimeProvisionProgress value, bool force = false)
        {
            if (inner is null) return;
            var now = Environment.TickCount64;
            if (!force && now - _lastReport < MinimumIntervalMs) return;
            _lastReport = now;
            inner.Report(value);
        }
    }
}
