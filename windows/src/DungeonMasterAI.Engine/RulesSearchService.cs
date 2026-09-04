using System.Text.Json;
using System.Text.RegularExpressions;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed class RulesSearchService
{
    private readonly List<RuleChunk> _chunks = [];
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","and","are","as","at","be","by","for","from","how","in","is","it","of","on","or","that","the","this","to","what","when","where","which","with","you","your"
    };

    public int Count => _chunks.Count;

    public async Task LoadAsync(string jsonlPath, CancellationToken cancellationToken = default)
    {
        _chunks.Clear();
        if (!File.Exists(jsonlPath)) return;
        using var stream = File.OpenRead(jsonlPath);
        using var reader = new StreamReader(stream);
#if NETSTANDARD2_1
        // netstandard2.1 has only the parameterless ReadLineAsync(); the cancellable overload is
        // .NET 7+. The token is still honoured by every other await in this method's callers -- the
        // only thing lost is a cooperative cancellation check between lines of a small bundled
        // srd_chunks.jsonl, which is read from local disk in a single pass.
        while (await reader.ReadLineAsync() is { } line)
#else
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
#endif
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            _chunks.Add(new RuleChunk(
                GetString(r, "chunk_key") ?? GetString(r, "chunk_id") ?? Guid.NewGuid().ToString("N"),
                GetInt(r, "page"),
                GetString(r, "section") ?? "",
                GetString(r, "heading") ?? "",
                GetString(r, "text") ?? ""));
        }
    }

    public IReadOnlyList<RuleSearchResult> Search(string query, int limit = 6)
    {
        if (string.IsNullOrWhiteSpace(query) || _chunks.Count == 0) return [];
        var tokens = Tokenize(query).Where(t => !StopWords.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tokens.Length == 0) return [];

        return _chunks
            .Select(c => new { Chunk = c, Score = Score(c, tokens) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.Page)
            .Take(Math.Clamp(limit, 1, 20))
            .Select(x => new RuleSearchResult(x.Chunk.ChunkKey, x.Chunk.Page, x.Chunk.Section, x.Chunk.Heading, x.Chunk.Text, x.Score))
            .ToArray();
    }

    private static int Score(RuleChunk chunk, IReadOnlyList<string> tokens)
    {
        var heading = chunk.Heading.ToLowerInvariant();
        var section = chunk.Section.ToLowerInvariant();
        var text = chunk.Text.ToLowerInvariant();
        var score = 0;
        foreach (var token in tokens)
        {
            if (heading.Contains(token)) score += 8;
            if (section.Contains(token)) score += 4;
            score += Regex.Matches(text, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase).Count;
        }
        return score;
    }

    private static IEnumerable<string> Tokenize(string value) =>
        Regex.Matches(value.ToLowerInvariant(), "[a-z0-9']+").Select(m => m.Value);

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) ? value : 0;

    private sealed record RuleChunk(string ChunkKey, int Page, string Section, string Heading, string Text);
}
