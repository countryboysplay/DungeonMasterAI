using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App.Controls;

public sealed record ResolvedTacticalMapAsset(
    TacticalMapAssetDefinition Definition,
    TacticalMapAssetVariant Variant,
    ImageSource Image,
    string PackId,
    string PackName,
    string License,
    string Author,
    string SourcePath);

/// <summary>
/// Loads renderer-neutral map-asset manifests and resolves stable asset keys to deterministic image
/// variants. User-local packs are searched before application fallbacks so upgraded first-party or
/// user-installed artwork can replace bundled placeholders without changing campaign geometry.
/// </summary>
public sealed class TacticalMapAssetCatalog
{
    private sealed record LoadedPack(TacticalMapAssetPackManifest Manifest, string Directory);

    private readonly IReadOnlyList<string> _roots;
    private readonly Dictionary<string, LoadedPack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BitmapSource?> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loadWarnings = [];
    private bool _loaded;

    public TacticalMapAssetCatalog(IEnumerable<string>? roots = null)
    {
        _roots = (roots ?? DefaultRoots()).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyCollection<TacticalMapAssetPackManifest> Packs
    {
        get
        {
            EnsureLoaded();
            return _packs.Values.Select(pack => pack.Manifest).ToArray();
        }
    }

    public IReadOnlyList<string> LoadWarnings
    {
        get
        {
            EnsureLoaded();
            return _loadWarnings.ToArray();
        }
    }

    public void Refresh()
    {
        _packs.Clear();
        _imageCache.Clear();
        _loadWarnings.Clear();
        _loaded = false;
        EnsureLoaded();
    }

    public bool TryResolve(string packId, string assetKey, int seed, int x, int y, out ResolvedTacticalMapAsset? resolved)
    {
        resolved = null;
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(assetKey)) return false;
        if (!_packs.TryGetValue(packId, out var pack)) return false;
        var definition = pack.Manifest.Assets.FirstOrDefault(asset => asset.Key.Equals(assetKey, StringComparison.OrdinalIgnoreCase));
        if (definition is null || definition.Variants.Count == 0) return false;

        var variant = ChooseVariant(definition.Variants, seed, x, y, assetKey);
        if (variant is null) return false;
        var fullPath = Path.GetFullPath(Path.Combine(pack.Directory, variant.File));
        var packRoot = Path.GetFullPath(pack.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(packRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(fullPath)) return false;

        var image = _imageCache.GetOrAdd(fullPath, LoadBitmap);
        if (image is null) return false;
        resolved = new ResolvedTacticalMapAsset(
            definition,
            variant,
            image,
            pack.Manifest.PackId,
            pack.Manifest.Name,
            pack.Manifest.License,
            pack.Manifest.Author,
            fullPath);
        return true;
    }

    public bool HasDefinition(string packId, string assetKey)
    {
        EnsureLoaded();
        return _packs.TryGetValue(packId, out var pack)
            && pack.Manifest.Assets.Any(asset => asset.Key.Equals(assetKey, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var manifest = JsonSerializer.Deserialize<TacticalMapAssetPackManifest>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (manifest is null)
                    {
                        _loadWarnings.Add($"Map asset manifest '{manifestPath}' could not be parsed.");
                        continue;
                    }

                    var validation = TacticalMapAssetPackValidator.Validate(manifest);
                    if (!validation.IsValid)
                    {
                        _loadWarnings.Add($"Map asset pack '{manifestPath}' rejected: {string.Join(" ", validation.Issues.Where(i => i.Severity == "error").Select(i => i.Message))}");
                        continue;
                    }

                    // Root order is priority order. A user-local production pack intentionally
                    // shadows the same semantic pack id shipped as a procedural fallback.
                    if (_packs.ContainsKey(manifest.PackId)) continue;
                    _packs[manifest.PackId] = new LoadedPack(manifest, Path.GetDirectoryName(manifestPath)!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _loadWarnings.Add($"Map asset manifest '{manifestPath}' was skipped: {ex.Message}");
                }
            }
        }
    }

    private static TacticalMapAssetVariant? ChooseVariant(IReadOnlyList<TacticalMapAssetVariant> variants, int seed, int x, int y, string key)
    {
        var valid = variants.Where(variant => variant.Weight > 0).ToArray();
        if (valid.Length == 0) return null;
        var totalWeight = valid.Sum(variant => variant.Weight);
        var hash = StableHash(seed, x, y, key);
        var pick = (int)((uint)hash % (uint)totalWeight);
        foreach (var variant in valid)
        {
            if (pick < variant.Weight) return variant;
            pick -= variant.Weight;
        }
        return valid[^1];
    }

    private static BitmapSource? LoadBitmap(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null) return null;
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    private static int StableHash(int seed, int x, int y, string key)
    {
        unchecked
        {
            var hash = seed == 0 ? 17 : seed;
            hash = hash * 31 + x;
            hash = hash * 31 + y;
            foreach (var ch in key ?? "") hash = hash * 31 + ch;
            return hash;
        }
    }

    private static IEnumerable<string> DefaultRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local)) yield return Path.Combine(local, "DungeonMasterAI", "MapPacks");
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "MapPacks");
    }
}
