using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.AI;

public sealed class TacticalMapGenerationRequest
{
    public string Description { get; set; } = "";
    public string MapType { get; set; } = "dungeon";
    public string Theme { get; set; } = "stone_dungeon";
    public string AssetSetId { get; set; } = "core.fantasy.crypt";
    public int WidthSquares { get; set; } = 30;
    public int HeightSquares { get; set; } = 20;
    public int FeetPerSquare { get; set; } = 5;
    public int Seed { get; set; }
    public bool FogOfWarEnabled { get; set; } = true;
    public List<string> AllowedAssetKeys { get; set; } = [];
}

public sealed record TacticalMapAiGenerationResult(
    TacticalMap Map,
    string RawJson,
    int Attempts,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Uses the existing local OpenAI-compatible llama.cpp endpoint to author structured TacticalMap JSON.
/// The language model proposes map structure only. Application-owned dimensions, scale, asset pack,
/// seed, source kind, validation, and campaign mutation remain deterministic application concerns.
/// </summary>
public sealed class TacticalMapAiGeneratorService(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<string> DefaultAssetKeys { get; } =
    [
        "floor.stone.flagstone",
        "floor.stone.crypt_flagstone",
        "wall.stone.block",
        "wall.stone.crypt_block",
        "door.wood.ironbound",
        "door.wood.broken",
        "door.stone.secret",
        "terrain.water.crypt_shallow",
        "terrain.rubble.stone",
        "prop.pillar.stone_round",
        "prop.rubble.pillar",
        "prop.altar.stone_crypt",
        "prop.sarcophagus.stone",
        "light.torch.wall",
        "light.brazier"
    ];

    public async Task<TacticalMapAiGenerationResult> GenerateAsync(
        TacticalMapGenerationRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        ValidateRequest(request);

        var seed = request.Seed == 0 ? Random.Shared.Next(1, int.MaxValue) : request.Seed;
        var allowedKeys = (request.AllowedAssetKeys.Count == 0 ? DefaultAssetKeys : request.AllowedAssetKeys)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowedKeys.Length == 0) throw new ArgumentException("At least one allowed map asset key is required.", nameof(request));

        string? previousJson = null;
        IReadOnlyList<string> previousErrors = [];
        var warnings = new List<string>();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompt = attempt == 1
                ? BuildGenerationPrompt(request, seed, allowedKeys)
                : BuildRepairPrompt(request, seed, allowedKeys, previousJson!, previousErrors);
            var responseText = await CompleteJsonAsync(prompt, settings, cancellationToken);
            var jsonText = ExtractJsonObject(responseText);
            previousJson = jsonText;

            TacticalMap candidate;
            try
            {
                candidate = DeserializeCandidate(jsonText);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                previousErrors = [$"The response could not be parsed as TacticalMap JSON: {ex.Message}"];
                if (attempt == 2) throw new InvalidDataException($"Local AI map generation failed after repair: {previousErrors[0]}", ex);
                continue;
            }

            NormalizeCandidate(candidate, request, seed);
            var validation = ValidateGeneratedMap(candidate, allowedKeys);
            warnings = validation.Warnings.ToList();
            if (validation.Errors.Count == 0)
            {
                var normalizedJson = JsonSerializer.Serialize(candidate, new JsonSerializerOptions(_json) { WriteIndented = true });
                return new TacticalMapAiGenerationResult(candidate, normalizedJson, attempt, warnings);
            }

            previousErrors = validation.Errors;
            if (attempt == 2)
                throw new InvalidDataException("Local AI returned an invalid tactical map after one repair attempt: " + string.Join(" | ", previousErrors));
        }

        throw new InvalidOperationException("Tactical map generation exited unexpectedly.");
    }

    private async Task<string> CompleteJsonAsync(string userPrompt, AppSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(NormalizeBase(settings.LlamaServerUrl)), "v1/chat/completions");
        var payload = new
        {
            model = settings.ModelName,
            temperature = 0.2,
            max_tokens = Math.Max(settings.MaxTokens, 6000),
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt() },
                new { role = "user", content = userPrompt }
            }
        };

        using var response = await _http.PostAsJsonAsync(endpoint, payload, _json, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Local AI returned HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").TryGetProperty("content", out var contentNode)
            ? contentNode.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("The local map generator returned an empty response.");
        return content;
    }

    private TacticalMap DeserializeCandidate(string jsonText)
    {
        using var doc = JsonDocument.Parse(jsonText);
        var mapElement = doc.RootElement;
        if (mapElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Map response root must be a JSON object.");
        if (mapElement.TryGetProperty("map", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object) mapElement = wrapped;
        return JsonSerializer.Deserialize<TacticalMap>(mapElement.GetRawText(), _json)
            ?? throw new InvalidDataException("Map response deserialized to null.");
    }

    private static void NormalizeCandidate(TacticalMap map, TacticalMapGenerationRequest request, int seed)
    {
        map.SchemaVersion = TacticalMapSchema.CurrentMapSchemaVersion;
        if (string.IsNullOrWhiteSpace(map.Id)) map.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(map.Name)) map.Name = "Generated Tactical Map";
        if (string.IsNullOrWhiteSpace(map.Key)) map.Key = Slug(map.Name);
        map.MapType = request.MapType.Trim();
        map.Theme = request.Theme.Trim();
        map.AssetSetId = request.AssetSetId.Trim();
        map.WidthSquares = request.WidthSquares;
        map.HeightSquares = request.HeightSquares;
        map.FeetPerSquare = request.FeetPerSquare;
        map.Seed = seed;
        // Record the geometry seed at generation time. Art variants are selected from Seed and may
        // be rerolled in the editor, so the reproducible geometry seed needs its own durable field
        // rather than being recovered from Seed later.
        map.GenerationSeed = seed;
        map.FogOfWarEnabled = request.FogOfWarEnabled;
        // A generated map is AI-invented content exactly like a generated NPC or quest, so it uses
        // the shared ai_expanded provenance value and shows up in generated-content filters.
        map.SourceKind = CampaignProvenance.AiExpanded;
        map.Visibility ??= new TacticalMapVisibility();
        map.Visibility.RevealAll = !request.FogOfWarEnabled;

        EnsureIds(map.Rooms, room => room.Id, (room, id) => room.Id = id);
        EnsureIds(map.Walls, wall => wall.Id, (wall, id) => wall.Id = id);
        EnsureIds(map.Doors, door => door.Id, (door, id) => door.Id = id);
        EnsureIds(map.Terrain, terrain => terrain.Id, (terrain, id) => terrain.Id = id);
        EnsureIds(map.Props, prop => prop.Id, (prop, id) => prop.Id = id);
        EnsureIds(map.Lights, light => light.Id, (light, id) => light.Id = id);
        EnsureIds(map.SpawnPoints, spawn => spawn.Id, (spawn, id) => spawn.Id = id);
        EnsureIds(map.Zones, zone => zone.Id, (zone, id) => zone.Id = id);
    }

    private static void EnsureIds<T>(IEnumerable<T> items, Func<T, string> getId, Action<T, string> setId)
    {
        foreach (var item in items)
            if (string.IsNullOrWhiteSpace(getId(item))) setId(item, Guid.NewGuid().ToString("N"));
    }

    private static GeneratedMapValidation ValidateGeneratedMap(TacticalMap map, IReadOnlyCollection<string> allowedKeys)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var geometry = TacticalMapGeometry.Validate(map);
        foreach (var issue in geometry.Issues)
        {
            var text = $"{issue.Path}: {issue.Message}";
            if (issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)) errors.Add(text);
            else warnings.Add(text);
        }

        if (map.Rooms.Count == 0) errors.Add("rooms: Generated map must contain at least one room, corridor, cave, or exterior region.");
        if (!map.SpawnPoints.Any(spawn => spawn.Side.Equals("player", StringComparison.OrdinalIgnoreCase)))
            errors.Add("spawnPoints: Generated map must contain at least one player-side spawn point.");

        var allowed = allowedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, key) in EnumerateAssetKeys(map))
            if (string.IsNullOrWhiteSpace(key) || !allowed.Contains(key)) errors.Add($"{path}: Asset key '{key}' is not in the allowed asset-key list.");

        return new GeneratedMapValidation(errors, warnings);
    }

    private static IEnumerable<(string Path, string Key)> EnumerateAssetKeys(TacticalMap map)
    {
        for (var i = 0; i < map.Rooms.Count; i++)
        {
            yield return ($"rooms[{i}].floorAssetKey", map.Rooms[i].FloorAssetKey);
            yield return ($"rooms[{i}].wallAssetKey", map.Rooms[i].WallAssetKey);
        }
        for (var i = 0; i < map.Walls.Count; i++) yield return ($"walls[{i}].assetKey", map.Walls[i].AssetKey);
        for (var i = 0; i < map.Doors.Count; i++) yield return ($"doors[{i}].assetKey", map.Doors[i].AssetKey);
        for (var i = 0; i < map.Terrain.Count; i++) yield return ($"terrain[{i}].assetKey", map.Terrain[i].AssetKey);
        for (var i = 0; i < map.Props.Count; i++) yield return ($"props[{i}].assetKey", map.Props[i].AssetKey);
        for (var i = 0; i < map.Lights.Count; i++) yield return ($"lights[{i}].assetKey", map.Lights[i].AssetKey);
    }

    private static void ValidateRequest(TacticalMapGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Description)) throw new ArgumentException("Map description is required.", nameof(request));
        if (request.WidthSquares is < 4 or > 200) throw new ArgumentOutOfRangeException(nameof(request), "Generated map width must be between 4 and 200 squares.");
        if (request.HeightSquares is < 4 or > 200) throw new ArgumentOutOfRangeException(nameof(request), "Generated map height must be between 4 and 200 squares.");
        if (request.FeetPerSquare is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(request), "Feet per square must be between 1 and 30.");
        if (string.IsNullOrWhiteSpace(request.MapType)) throw new ArgumentException("Map type is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Theme)) throw new ArgumentException("Map theme is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.AssetSetId)) throw new ArgumentException("Asset set ID is required.", nameof(request));
    }

    private static string BuildGenerationPrompt(TacticalMapGenerationRequest request, int seed, IReadOnlyCollection<string> allowedKeys)
        => $"""
Create one playable tactical map from this request:
{request.Description.Trim()}

APPLICATION-OWNED CONSTRAINTS. Follow these exactly:
- mapType: {request.MapType}
- theme: {request.Theme}
- widthSquares: {request.WidthSquares}
- heightSquares: {request.HeightSquares}
- feetPerSquare: {request.FeetPerSquare}
- assetSetId: {request.AssetSetId}
- seed: {seed}
- fogOfWarEnabled: {request.FogOfWarEnabled.ToString().ToLowerInvariant()}

ALLOWED ASSET KEYS. Do not output any other visual asset key:
{string.Join("\n", allowedKeys.Select(key => "- " + key))}

Return one TacticalMap JSON object using camelCase property names. Include rooms, walls, doors where useful, terrain, props, lights, spawnPoints, zones, and visibility. IDs and key may be omitted because the application can supply missing identifiers.

Coordinate contract:
- (0,0) is the upper-left cell.
- Rooms, terrain, props, zones, and spawn points use zero-based cell coordinates and must remain inside map bounds.
- Walls use grid-line coordinates and may use map boundary coordinates. Walls must be horizontal or vertical, never diagonal.
- Prefer long wall segments rather than one wall per square.
- A vertical door at (x,y) occupies edge (x,y) to (x,y+1).
- A horizontal door at (x,y) occupies edge (x,y) to (x+1,y).
- Every door should be embedded in a matching explicit wall segment.
- Include at least one spawnPoint with side "player" in a walkable entrance area.
- Mark secret doors with secret=true and discovered=false.
- Gameplay flags must agree with the described object: solid pillars/walls block movement as appropriate, shallow water/rubble may be difficult terrain, and opaque solid objects may block line of sight.
- Do not output rendered pixels, image prompts, filenames, markdown, comments, or prose outside the JSON object.
""";

    private static string BuildRepairPrompt(
        TacticalMapGenerationRequest request,
        int seed,
        IReadOnlyCollection<string> allowedKeys,
        string previousJson,
        IReadOnlyList<string> errors)
        => $"""
Repair the tactical map JSON below. Return a complete replacement TacticalMap JSON object and nothing else.

The application rejected the previous candidate for these reasons:
{string.Join("\n", errors.Select(error => "- " + error))}

Preserve the requested concept while correcting every listed problem. Application-owned values must remain:
mapType={request.MapType}; theme={request.Theme}; widthSquares={request.WidthSquares}; heightSquares={request.HeightSquares}; feetPerSquare={request.FeetPerSquare}; assetSetId={request.AssetSetId}; seed={seed}; fogOfWarEnabled={request.FogOfWarEnabled.ToString().ToLowerInvariant()}.
Only these asset keys are permitted:
{string.Join("\n", allowedKeys.Select(key => "- " + key))}

PREVIOUS CANDIDATE:
{previousJson}
""";

    private static string SystemPrompt() => """
You are the structured tactical-map authoring component of a local tabletop campaign builder.
You create game-readable geometry, not images and not narration.
Return exactly one JSON object that conforms to the TacticalMap schema requested by the application.
The application is authoritative for dimensions, grid scale, seed, asset pack, validation, and whether the candidate is accepted into a campaign.
Never invent filenames. Use only asset keys explicitly allowed by the user prompt.
Keep all rectangles and spawn points within bounds, all walls axis-aligned, and all doors on explicit wall edges.
Favor coherent connected play spaces, sensible entrances, tactical choices, readable movement lanes, and useful cover without making the map impassable.
Do not return markdown fences, explanatory prose, or a second alternative.
""";

    private static string ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline) trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
        }
        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        if (first < 0 || last <= first) throw new InvalidDataException("No JSON object was found in the local map-generator response.");
        return trimmed[first..(last + 1)];
    }

    private static string NormalizeBase(string url) => url.TrimEnd('/') + "/";

    private static string Slug(string text)
    {
        var chars = text.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        var slug = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "generated_map" : slug;
    }

    private sealed record GeneratedMapValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
}
