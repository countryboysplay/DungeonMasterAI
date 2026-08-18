namespace DungeonMasterAI.Domain;

/// <summary>
/// UI-neutral tactical geometry shared by the deterministic spell engine and battlefield preview.
/// Coordinates are 5-foot grid squares. This helper intentionally contains no mutable campaign state.
/// </summary>
public static class SpellAreaGeometry
{
    public static (int Dx, int Dy) NormalizeDirection(string? direction)
    {
        var d = (direction ?? "north").Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        return d switch
        {
            "n" or "north" => (0, -1),
            "ne" or "north-east" or "northeast" => (1, -1),
            "e" or "east" => (1, 0),
            "se" or "south-east" or "southeast" => (1, 1),
            "s" or "south" => (0, 1),
            "sw" or "south-west" or "southwest" => (-1, 1),
            "w" or "west" => (-1, 0),
            "nw" or "north-west" or "northwest" => (-1, -1),
            _ => throw new ArgumentException("Area direction must be north, northeast, east, southeast, south, southwest, west, or northwest.", nameof(direction))
        };
    }

    /// <summary>
    /// Default width, in feet, of a line-shaped area when a spell does not declare one.
    /// SRD line spells are 5 feet wide unless stated otherwise.
    /// </summary>
    public const int DefaultLineWidthFeet = 5;

    public static bool ContainsCell(
        string? shape,
        int sizeFeet,
        int originX,
        int originY,
        int targetX,
        int targetY,
        string? direction = "north",
        int widthFeet = DefaultLineWidthFeet)
    {
        if (sizeFeet <= 0) return false;
        var normalizedShape = (shape ?? "").Trim().ToLowerInvariant();
        var rx = targetX - originX;
        var ry = targetY - originY;
        if (normalizedShape == "sphere")
            return Math.Sqrt(rx * rx + ry * ry) * 5.0 <= sizeFeet + 0.001;

        var (dx, dy) = NormalizeDirection(direction);
        var norm = Math.Sqrt(dx * dx + dy * dy);
        var projectionFeet = ((rx * dx + ry * dy) / norm) * 5.0;
        var sideFeet = Math.Abs(rx * dy - ry * dx) / norm * 5.0;
        return normalizedShape switch
        {
            // A small half-square allowance keeps the alpha grid representation usable at cell centers.
            "cone" => projectionFeet > 0 && projectionFeet <= sizeFeet + 0.001 && sideFeet <= projectionFeet / 2.0 + 2.5,
            "cube" => projectionFeet > 0 && projectionFeet <= sizeFeet + 0.001 && sideFeet <= sizeFeet / 2.0 + 0.001,
            // A line runs sizeFeet along the chosen direction with a fixed width, so unlike a cone
            // its lateral extent does not grow with distance from the origin.
            "line" => projectionFeet > 0
                && projectionFeet <= sizeFeet + 0.001
                && sideFeet <= (widthFeet <= 0 ? DefaultLineWidthFeet : widthFeet) / 2.0 + 0.001,
            _ => throw new InvalidOperationException($"Unsupported area shape '{shape}'.")
        };
    }

    public static IReadOnlyList<(int X, int Y)> EnumerateCells(
        string? shape,
        int sizeFeet,
        int originX,
        int originY,
        string? direction = "north",
        int widthFeet = DefaultLineWidthFeet)
    {
        if (sizeFeet <= 0) return [];
        var radiusSquares = Math.Max(1, (int)Math.Ceiling(sizeFeet / 5.0) + 1);
        var cells = new List<(int X, int Y)>();
        for (var x = originX - radiusSquares; x <= originX + radiusSquares; x++)
        for (var y = originY - radiusSquares; y <= originY + radiusSquares; y++)
            if (ContainsCell(shape, sizeFeet, originX, originY, x, y, direction, widthFeet))
                cells.Add((x, y));
        return cells;
    }

    /// <summary>
    /// Returns a deterministic center-to-center grid trace excluding the origin and including the destination.
    /// Used for alpha line-of-effect checks and battlefield preview.
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> TraceGridLine(int fromX, int fromY, int toX, int toY)
    {
        var points = new List<(int X, int Y)>();
        var x = fromX;
        var y = fromY;
        var dx = Math.Abs(toX - fromX);
        var sx = fromX < toX ? 1 : -1;
        var dy = -Math.Abs(toY - fromY);
        var sy = fromY < toY ? 1 : -1;
        var error = dx + dy;

        while (x != toX || y != toY)
        {
            var e2 = 2 * error;
            if (e2 >= dy) { error += dy; x += sx; }
            if (e2 <= dx) { error += dx; y += sy; }
            points.Add((x, y));
        }
        return points;
    }
}
