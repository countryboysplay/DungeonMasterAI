namespace DungeonMasterAI.Domain;

/// <summary>
/// Renderer-neutral metadata for a tactical-map visual asset pack. Campaign maps store stable asset
/// keys and a pack id; they never store renderer-specific file paths.
/// </summary>
public sealed class TacticalMapAssetPackManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string PackId { get; set; } = "";
    public string Name { get; set; } = "Map Asset Pack";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string License { get; set; } = "";
    public string LicenseUrl { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Credits { get; set; } = "";
    public List<TacticalMapAssetDefinition> Assets { get; set; } = [];
}

public sealed class TacticalMapAssetDefinition
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "prop"; // floor, wall, door, terrain, prop, light, decal
    public string RenderMode { get; set; } = "sprite"; // tile, stretch, sprite, segment
    public double Opacity { get; set; } = 1;
    public double Scale { get; set; } = 1;
    public bool AllowProceduralFallback { get; set; } = true;
    public List<TacticalMapAssetVariant> Variants { get; set; } = [];
}

public sealed class TacticalMapAssetVariant
{
    public string File { get; set; } = "";
    public int Weight { get; set; } = 1;
    public int RotationDegrees { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    public List<string> Tags { get; set; } = [];
}

public sealed record TacticalMapAssetPackValidationIssue(string Severity, string Message);

public sealed class TacticalMapAssetPackValidationReport
{
    public List<TacticalMapAssetPackValidationIssue> Issues { get; } = [];
    public int Errors => Issues.Count(i => i.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
    public bool IsValid => Errors == 0;
}

public static class TacticalMapAssetPackValidator
{
    public static TacticalMapAssetPackValidationReport Validate(TacticalMapAssetPackManifest manifest)
    {
        var report = new TacticalMapAssetPackValidationReport();
        void Error(string message) => report.Issues.Add(new TacticalMapAssetPackValidationIssue("error", message));
        void Warning(string message) => report.Issues.Add(new TacticalMapAssetPackValidationIssue("warning", message));

        if (manifest.SchemaVersion != 1) Error($"Unsupported map asset manifest schema version {manifest.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.PackId)) Error("PackId is required.");
        if (string.IsNullOrWhiteSpace(manifest.Name)) Error("Pack name is required.");
        if (string.IsNullOrWhiteSpace(manifest.Author)) Error("Asset pack author is required.");
        if (string.IsNullOrWhiteSpace(manifest.License)) Error("Asset pack license is required.");
        if (manifest.Assets.Count == 0) Warning("Asset pack contains no asset definitions.");

        var duplicateKeys = manifest.Assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Key))
            .GroupBy(asset => asset.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicate in duplicateKeys) Error($"Asset key '{duplicate}' is duplicated.");

        foreach (var asset in manifest.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Key)) Error("Every asset requires a stable Key.");
            if (asset.Opacity is < 0 or > 1) Error($"Asset '{asset.Key}' opacity must be between 0 and 1.");
            if (asset.Scale <= 0) Error($"Asset '{asset.Key}' scale must be greater than zero.");
            if (asset.Variants.Count == 0 && !asset.AllowProceduralFallback)
                Error($"Asset '{asset.Key}' has no file variants and disallows procedural fallback.");

            foreach (var variant in asset.Variants)
            {
                if (string.IsNullOrWhiteSpace(variant.File))
                {
                    Error($"Asset '{asset.Key}' contains a variant with no file path.");
                    continue;
                }

                var normalized = variant.File.Replace('\\', '/');
                if (Path.IsPathRooted(variant.File) || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains(".."))
                    Error($"Asset '{asset.Key}' variant '{variant.File}' must be a relative path inside its pack directory.");
                if (variant.Weight <= 0) Error($"Asset '{asset.Key}' variant '{variant.File}' must have a positive weight.");
                if (variant.RotationDegrees % 90 != 0) Warning($"Asset '{asset.Key}' variant '{variant.File}' rotation is not a 90-degree increment.");
            }
        }

        return report;
    }
}
