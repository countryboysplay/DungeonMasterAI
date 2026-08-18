using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DungeonMasterAI.AI;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.AiMapGenerationTests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var failures = new List<string>();
        try
        {
            var invalidCandidate = """
            {
              "name": "Broken Crypt",
              "rooms": [
                { "name": "Entry", "x": 1, "y": 1, "widthSquares": 8, "heightSquares": 6, "floorAssetKey": "floor.stone.crypt_flagstone", "wallAssetKey": "wall.stone.crypt_block" }
              ],
              "walls": [
                { "fromX": 1, "fromY": 1, "toX": 8, "toY": 5, "assetKey": "wall.stone.crypt_block" }
              ],
              "props": [
                { "name": "Invented Statue", "x": 3, "y": 3, "assetKey": "prop.not.allowed" }
              ],
              "spawnPoints": []
            }
            """;

            var repairedCandidate = """
            ```json
            {
              "name": "The Flooded Reliquary",
              "key": "flooded_reliquary",
              "rooms": [
                { "name": "Reliquary Hall", "kind": "room", "x": 2, "y": 2, "widthSquares": 20, "heightSquares": 12, "floorAssetKey": "floor.stone.crypt_flagstone", "wallAssetKey": "wall.stone.crypt_block" }
              ],
              "walls": [
                { "fromX": 2, "fromY": 2, "toX": 22, "toY": 2, "assetKey": "wall.stone.crypt_block" },
                { "fromX": 22, "fromY": 2, "toX": 22, "toY": 14, "assetKey": "wall.stone.crypt_block" },
                { "fromX": 22, "fromY": 14, "toX": 2, "toY": 14, "assetKey": "wall.stone.crypt_block" },
                { "fromX": 2, "fromY": 14, "toX": 2, "toY": 2, "assetKey": "wall.stone.crypt_block" },
                { "fromX": 12, "fromY": 2, "toX": 12, "toY": 14, "assetKey": "wall.stone.crypt_block" }
              ],
              "doors": [
                { "name": "Reliquary Gate", "x": 12, "y": 7, "orientation": "vertical", "state": "closed", "assetKey": "door.wood.ironbound" }
              ],
              "terrain": [
                { "name": "Shallow Flood", "terrainType": "water", "x": 14, "y": 4, "widthSquares": 5, "heightSquares": 4, "assetKey": "terrain.water.crypt_shallow", "difficultTerrain": true }
              ],
              "props": [
                { "name": "Round Pillar", "x": 8, "y": 7, "assetKey": "prop.pillar.stone_round", "blocksMovement": true, "blocksLineOfSight": true, "cover": "three_quarters" },
                { "name": "Stone Altar", "x": 17, "y": 10, "widthSquares": 2, "heightSquares": 1, "assetKey": "prop.altar.stone_crypt", "blocksMovement": true, "cover": "half" }
              ],
              "lights": [
                { "name": "Wall Torch", "assetKey": "light.torch.wall", "x": 5.5, "y": 3, "brightRadiusFeet": 20, "dimRadiusFeet": 20 }
              ],
              "spawnPoints": [
                { "name": "Party Entrance", "side": "player", "x": 4, "y": 5, "dmOnly": true },
                { "name": "Warden Spawn", "side": "enemy", "x": 18, "y": 9, "characterKey": "crypt_warden", "dmOnly": true }
              ],
              "zones": [
                { "name": "Warden Trigger", "zoneType": "encounter", "x": 16, "y": 8, "widthSquares": 4, "heightSquares": 4, "referenceId": "warden_encounter", "dmOnly": true }
              ],
              "visibility": { "revealAll": false, "revealedRoomIds": [], "revealedCells": [] }
            }
            ```
            """;

            var handler = new QueuedCompletionHandler([invalidCandidate, repairedCandidate]);
            using var http = new HttpClient(handler);
            var generator = new TacticalMapAiGeneratorService(http);
            var request = new TacticalMapGenerationRequest
            {
                Description = "Create a flooded ruined crypt with a central divider, one guarded reliquary chamber, useful cover, and a clear party entrance.",
                MapType = "dungeon",
                Theme = "abandoned_crypt",
                AssetSetId = "core.fantasy.crypt",
                WidthSquares = 26,
                HeightSquares = 18,
                FeetPerSquare = 5,
                Seed = 771944,
                FogOfWarEnabled = true,
                AllowedAssetKeys = TacticalMapAiGeneratorService.DefaultAssetKeys.ToList()
            };
            var settings = new AppSettings
            {
                LlamaServerUrl = "http://127.0.0.1:8080",
                ModelName = "test-local-model",
                MaxTokens = 700
            };

            var result = await generator.GenerateAsync(request, settings);
            Check(result.Attempts == 2, "Invalid first candidate triggers exactly one repair attempt.", failures);
            Check(handler.RequestBodies.Count == 2, "Generator made exactly two local chat-completion requests.", failures);
            Check(result.Map.SourceKind == "ai_generated", "Accepted map is marked ai_generated rather than source canon.", failures);
            Check(result.Map.WidthSquares == request.WidthSquares && result.Map.HeightSquares == request.HeightSquares,
                "Application-owned map dimensions override model output.", failures);
            Check(result.Map.FeetPerSquare == request.FeetPerSquare && result.Map.Seed == request.Seed,
                "Application-owned grid scale and deterministic seed are preserved.", failures);
            Check(result.Map.AssetSetId == request.AssetSetId && result.Map.Theme == request.Theme && result.Map.MapType == request.MapType,
                "Application-owned asset set, theme, and map type are preserved.", failures);
            Check(result.Map.FogOfWarEnabled && !result.Map.Visibility.RevealAll,
                "Fog setting remains application-owned and accepted map begins unrevealed.", failures);
            Check(result.Map.SpawnPoints.Any(spawn => spawn.Side.Equals("player", StringComparison.OrdinalIgnoreCase)),
                "Accepted map contains a player-side entrance spawn.", failures);
            Check(result.Map.Rooms.All(room => !string.IsNullOrWhiteSpace(room.Id))
                  && result.Map.Walls.All(wall => !string.IsNullOrWhiteSpace(wall.Id))
                  && result.Map.Doors.All(door => !string.IsNullOrWhiteSpace(door.Id)),
                "Application supplies missing element IDs before validation.", failures);

            var geometry = TacticalMapGeometry.Validate(result.Map);
            Check(geometry.IsValid, "Accepted AI map passes authoritative deterministic geometry validation.", failures);

            var allowed = request.AllowedAssetKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var used = result.Map.Rooms.SelectMany(room => new[] { room.FloorAssetKey, room.WallAssetKey })
                .Concat(result.Map.Walls.Select(wall => wall.AssetKey))
                .Concat(result.Map.Doors.Select(door => door.AssetKey))
                .Concat(result.Map.Terrain.Select(terrain => terrain.AssetKey))
                .Concat(result.Map.Props.Select(prop => prop.AssetKey))
                .Concat(result.Map.Lights.Select(light => light.AssetKey));
            Check(used.All(allowed.Contains), "Accepted map uses only asset keys supplied by the application.", failures);

            if (handler.RequestBodies.Count == 2)
            {
                var first = handler.RequestBodies[0];
                var second = handler.RequestBodies[1];
                Check(first.Contains(request.Description, StringComparison.Ordinal), "Initial prompt contains the user's map description.", failures);
                Check(first.Contains("prop.pillar.stone_round", StringComparison.Ordinal), "Initial prompt contains the allowed semantic asset-key catalog.", failures);
                Check(second.Contains("not in the allowed asset-key list", StringComparison.OrdinalIgnoreCase),
                    "Repair prompt includes deterministic asset validation failure.", failures);
                Check(second.Contains("supports axis-aligned walls only", StringComparison.OrdinalIgnoreCase),
                    "Repair prompt includes deterministic geometry failure.", failures);
                Check(second.Contains("Generated map must contain at least one player-side spawn point", StringComparison.OrdinalIgnoreCase),
                    "Repair prompt includes missing player entrance failure.", failures);
                Check(second.Contains("Broken Crypt", StringComparison.Ordinal), "Repair prompt includes the rejected candidate for correction.", failures);
            }

            Check(result.RawJson.Contains("\"sourceKind\": \"ai_generated\"", StringComparison.Ordinal),
                "Result exposes normalized JSON suitable for preview/export.", failures);

            if (failures.Count == 0)
            {
                Console.WriteLine("AI MAP GENERATION PASS");
                Console.WriteLine("Local completion contract, validation, one-pass repair, asset whitelist, application-owned fields, and authoritative geometry verified.");
                return 0;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Unhandled AI map generation test exception: {ex}");
        }

        Console.Error.WriteLine($"AI MAP GENERATION FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
        return 1;
    }

    private static void Check(bool condition, string message, ICollection<string> failures)
    {
        if (!condition) failures.Add(message);
    }

    private sealed class QueuedCompletionHandler(IEnumerable<string> completions) : HttpMessageHandler
    {
        private readonly Queue<string> _completions = new(completions);
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_completions.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("No queued completion remains.")
                };

            var content = _completions.Dequeue();
            var body = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { role = "assistant", content } }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
