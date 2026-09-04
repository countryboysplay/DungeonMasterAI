using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed record TacticalMapValidationIssue(string Severity, string Path, string Message);

public sealed class TacticalMapValidationReport
{
    public List<TacticalMapValidationIssue> Issues { get; } = [];
    public bool IsValid => Issues.All(x => !x.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
    public int Errors => Issues.Count(x => x.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
    public int Warnings => Issues.Count(x => x.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Deterministic geometry queries for authored tactical maps. Rendering and AI generation both
/// consume this same schema so visible geometry and game-rule geometry cannot silently diverge.
/// </summary>
public static class TacticalMapGeometry
{
    public static TacticalMapValidationReport Validate(TacticalMap map)
    {
        Guard.NotNull(map, nameof(map));
        var report = new TacticalMapValidationReport();

        if (map.SchemaVersion < 1 || map.SchemaVersion > TacticalMapSchema.CurrentMapSchemaVersion)
            Error(report, "schemaVersion", $"Unsupported tactical map schema version {map.SchemaVersion}; expected 1 to {TacticalMapSchema.CurrentMapSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(map.Id)) Error(report, "id", "Map ID is required.");
        if (string.IsNullOrWhiteSpace(map.Name)) Error(report, "name", "Map name is required.");
        if (map.WidthSquares <= 0 || map.WidthSquares > 500) Error(report, "widthSquares", "Width must be between 1 and 500 squares.");
        if (map.HeightSquares <= 0 || map.HeightSquares > 500) Error(report, "heightSquares", "Height must be between 1 and 500 squares.");
        if (map.FeetPerSquare <= 0 || map.FeetPerSquare > 30) Error(report, "feetPerSquare", "Feet per square must be between 1 and 30.");

        ValidateUniqueIds(report, "rooms", map.Rooms.Select(x => x.Id));
        ValidateUniqueIds(report, "walls", map.Walls.Select(x => x.Id));
        ValidateUniqueIds(report, "doors", map.Doors.Select(x => x.Id));
        ValidateUniqueIds(report, "terrain", map.Terrain.Select(x => x.Id));
        ValidateUniqueIds(report, "props", map.Props.Select(x => x.Id));
        ValidateUniqueIds(report, "lights", map.Lights.Select(x => x.Id));
        ValidateUniqueIds(report, "spawnPoints", map.SpawnPoints.Select(x => x.Id));
        ValidateUniqueIds(report, "zones", map.Zones.Select(x => x.Id));

        for (var i = 0; i < map.Rooms.Count; i++)
        {
            var room = map.Rooms[i];
            ValidateRect(report, map, $"rooms[{i}]", room.X, room.Y, room.WidthSquares, room.HeightSquares);
            if (string.IsNullOrWhiteSpace(room.FloorAssetKey)) Warn(report, $"rooms[{i}].floorAssetKey", "No floor asset key is set; renderer fallback will be used.");
        }

        for (var i = 0; i < map.Walls.Count; i++)
        {
            var wall = map.Walls[i];
            var path = $"walls[{i}]";
            if (!GridPointInsideOrOnBoundary(map, wall.FromX, wall.FromY) || !GridPointInsideOrOnBoundary(map, wall.ToX, wall.ToY))
                Error(report, path, "Wall endpoints must lie on or inside the map boundary.");
            if (wall.FromX != wall.ToX && wall.FromY != wall.ToY)
                Error(report, path, "Prototype schema supports axis-aligned walls only.");
            if (wall.FromX == wall.ToX && wall.FromY == wall.ToY)
                Error(report, path, "Wall must have non-zero length.");
        }

        for (var i = 0; i < map.Doors.Count; i++)
        {
            var door = map.Doors[i];
            var path = $"doors[{i}]";
            if (!IsDoorOrientationValid(door.Orientation)) Error(report, path + ".orientation", "Door orientation must be horizontal or vertical.");
            if (!DoorEdgeInside(map, door)) Error(report, path, "Door edge must lie inside the map boundary.");
            if (!new[] { "open", "closed", "locked", "barred" }.Contains(door.State, StringComparer.OrdinalIgnoreCase))
                Warn(report, path + ".state", $"Unknown door state '{door.State}'. It will be treated as closed.");
        }

        for (var i = 0; i < map.Terrain.Count; i++)
            ValidateRect(report, map, $"terrain[{i}]", map.Terrain[i].X, map.Terrain[i].Y, map.Terrain[i].WidthSquares, map.Terrain[i].HeightSquares);
        for (var i = 0; i < map.Props.Count; i++)
            ValidateRect(report, map, $"props[{i}]", map.Props[i].X, map.Props[i].Y, map.Props[i].WidthSquares, map.Props[i].HeightSquares);
        for (var i = 0; i < map.Zones.Count; i++)
            ValidateRect(report, map, $"zones[{i}]", map.Zones[i].X, map.Zones[i].Y, map.Zones[i].WidthSquares, map.Zones[i].HeightSquares);
        for (var i = 0; i < map.SpawnPoints.Count; i++)
        {
            var spawn = map.SpawnPoints[i];
            if (!IsInside(map, spawn.X, spawn.Y)) Error(report, $"spawnPoints[{i}]", "Spawn point must be inside the map.");
            if (!CombatSide.IsRecognized(spawn.Side))
                Error(report, $"spawnPoints[{i}].side",
                    $"Unsupported spawn side '{spawn.Side}'. Use one of: {string.Join(", ", CombatSide.All)}.");
            else if (IsInside(map, spawn.X, spawn.Y) && !IsCellWalkable(map, spawn.X, spawn.Y))
                Warn(report, $"spawnPoints[{i}]", $"Spawn point '{spawn.Name}' sits on an unwalkable square; combat placement will fall back to a nearby square.");
        }
        for (var i = 0; i < map.Lights.Count; i++)
            if (map.Lights[i].X < 0 || map.Lights[i].Y < 0 || map.Lights[i].X > map.WidthSquares || map.Lights[i].Y > map.HeightSquares)
                Error(report, $"lights[{i}]", "Light origin must be inside the map.");

        foreach (var door in map.Doors)
        {
            var matchingWall = map.Walls.Any(w => WallContainsDoorEdge(w, door));
            if (!matchingWall) Warn(report, "doors", $"Door '{door.Name}' is not embedded in an explicit wall segment.");
        }

        return report;
    }

    public static bool IsInside(TacticalMap map, int x, int y) => x >= 0 && y >= 0 && x < map.WidthSquares && y < map.HeightSquares;

    public static bool IsCellWalkable(TacticalMap map, int x, int y)
    {
        if (!IsInside(map, x, y)) return false;
        if (map.Terrain.Any(t => t.BlocksMovement && Contains(t.X, t.Y, t.WidthSquares, t.HeightSquares, x, y))) return false;
        if (map.Props.Any(p => p.BlocksMovement && Contains(p.X, p.Y, p.WidthSquares, p.HeightSquares, x, y))) return false;
        return true;
    }

    public static bool IsDifficultTerrain(TacticalMap map, int x, int y)
    {
        if (!IsInside(map, x, y)) return false;
        return map.Terrain.Any(t => t.DifficultTerrain && Contains(t.X, t.Y, t.WidthSquares, t.HeightSquares, x, y))
            || map.Props.Any(p => p.DifficultTerrain && Contains(p.X, p.Y, p.WidthSquares, p.HeightSquares, x, y));
    }

    public static int MovementCostFeet(TacticalMap map, int x, int y)
        => map.FeetPerSquare * (IsDifficultTerrain(map, x, y) ? 2 : 1);

    public static bool CanMoveBetween(TacticalMap map, TacticalMapCell from, TacticalMapCell to)
    {
        if (!IsInside(map, from.X, from.Y) || !IsCellWalkable(map, to.X, to.Y)) return false;
        if (Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) != 1) return false;
        var edge = SharedEdge(from, to);
        return !EdgeBlocksMovement(map, edge);
    }

    /// <summary>
    /// True when a creature may take a single grid step from <paramref name="from"/> to
    /// <paramref name="to"/>, honouring blocking terrain and props in the destination square and
    /// walls and closed doors on the edge crossed.
    /// <para>
    /// <see cref="CanMoveBetween"/> answers this for orthogonal steps only. The combat engine
    /// traces movement with a Chebyshev path, so it also produces diagonal steps: a diagonal is
    /// allowed when at least one of its two orthogonal decompositions is legal, which is the same
    /// corner rule <see cref="HasLineOfSight"/> already applies to sight. This is a single-step
    /// legality test, not a pathfinder — it never searches for a route.
    /// </para>
    /// </summary>
    public static bool CanTraverseStep(TacticalMap map, TacticalMapCell from, TacticalMapCell to)
    {
        Guard.NotNull(map, nameof(map));
        if (from == to) return IsCellWalkable(map, to.X, to.Y);
        if (!IsInside(map, from.X, from.Y) || !IsCellWalkable(map, to.X, to.Y)) return false;

        var dx = Math.Abs(to.X - from.X);
        var dy = Math.Abs(to.Y - from.Y);
        if (dx > 1 || dy > 1) return false;
        if (dx + dy == 1) return CanMoveBetween(map, from, to);

        var horizontalFirst = new TacticalMapCell(to.X, from.Y);
        var verticalFirst = new TacticalMapCell(from.X, to.Y);
        return (CanMoveBetween(map, from, horizontalFirst) && CanMoveBetween(map, horizontalFirst, to))
            || (CanMoveBetween(map, from, verticalFirst) && CanMoveBetween(map, verticalFirst, to));
    }

    public static bool HasLineOfSight(TacticalMap map, TacticalMapCell from, TacticalMapCell to)
    {
        if (!IsInside(map, from.X, from.Y) || !IsInside(map, to.X, to.Y)) return false;
        if (from == to) return true;

        var cells = SupercoverLine(from, to).ToArray();
        for (var i = 1; i < cells.Length; i++)
        {
            var previous = cells[i - 1];
            var current = cells[i];
            if (CellBlocksLineOfSight(map, current.X, current.Y) && current != to) return false;

            var dx = current.X - previous.X;
            var dy = current.Y - previous.Y;
            if (Math.Abs(dx) + Math.Abs(dy) == 1)
            {
                if (EdgeBlocksLineOfSight(map, SharedEdge(previous, current))) return false;
            }
            else if (Math.Abs(dx) == 1 && Math.Abs(dy) == 1)
            {
                var horizontalStep = new TacticalMapCell(current.X, previous.Y);
                var verticalStep = new TacticalMapCell(previous.X, current.Y);
                var horizontalBlocked = EdgeBlocksLineOfSight(map, SharedEdge(previous, horizontalStep));
                var verticalBlocked = EdgeBlocksLineOfSight(map, SharedEdge(previous, verticalStep));
                if (horizontalBlocked && verticalBlocked) return false;
            }
        }
        return true;
    }

    public static IEnumerable<TacticalMapCell> EnumerateRoomCells(TacticalMapRoom room)
    {
        for (var y = room.Y; y < room.Y + Math.Max(0, room.HeightSquares); y++)
            for (var x = room.X; x < room.X + Math.Max(0, room.WidthSquares); x++)
                yield return new TacticalMapCell(x, y);
    }

    private static bool CellBlocksLineOfSight(TacticalMap map, int x, int y)
        => map.Terrain.Any(t => (t.BlocksLineOfSight || t.HeavilyObscured) && Contains(t.X, t.Y, t.WidthSquares, t.HeightSquares, x, y))
            || map.Props.Any(p => p.BlocksLineOfSight && Contains(p.X, p.Y, p.WidthSquares, p.HeightSquares, x, y));

    private static bool EdgeBlocksMovement(TacticalMap map, MapEdge edge)
    {
        var door = FindDoor(map, edge);
        if (door is not null)
            return !door.State.Equals("open", StringComparison.OrdinalIgnoreCase) && door.BlocksMovementWhenClosed;
        return map.Walls.Any(w => w.BlocksMovement && WallContainsEdge(w, edge));
    }

    private static bool EdgeBlocksLineOfSight(TacticalMap map, MapEdge edge)
    {
        var door = FindDoor(map, edge);
        if (door is not null)
            return !door.State.Equals("open", StringComparison.OrdinalIgnoreCase) && door.BlocksLineOfSightWhenClosed;
        return map.Walls.Any(w => w.BlocksLineOfSight && WallContainsEdge(w, edge));
    }

    private static TacticalMapDoor? FindDoor(TacticalMap map, MapEdge edge)
        => map.Doors.FirstOrDefault(d => DoorMatchesEdge(d, edge));

    private static bool DoorMatchesEdge(TacticalMapDoor door, MapEdge edge)
    {
        var orientation = door.Orientation.Trim().ToLowerInvariant();
        return orientation switch
        {
            "vertical" => edge.X1 == door.X && edge.X2 == door.X && edge.Y1 == door.Y && edge.Y2 == door.Y + 1,
            "horizontal" => edge.Y1 == door.Y && edge.Y2 == door.Y && edge.X1 == door.X && edge.X2 == door.X + 1,
            _ => false
        };
    }

    private static bool WallContainsDoorEdge(TacticalMapWall wall, TacticalMapDoor door)
    {
        var edge = door.Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase)
            ? NormalizeEdge(door.X, door.Y, door.X + 1, door.Y)
            : NormalizeEdge(door.X, door.Y, door.X, door.Y + 1);
        return WallContainsEdge(wall, edge);
    }

    private static bool WallContainsEdge(TacticalMapWall wall, MapEdge edge)
    {
        if (wall.FromX == wall.ToX && edge.X1 == edge.X2 && edge.X1 == wall.FromX)
        {
            var min = Math.Min(wall.FromY, wall.ToY);
            var max = Math.Max(wall.FromY, wall.ToY);
            return edge.Y1 >= min && edge.Y2 <= max;
        }
        if (wall.FromY == wall.ToY && edge.Y1 == edge.Y2 && edge.Y1 == wall.FromY)
        {
            var min = Math.Min(wall.FromX, wall.ToX);
            var max = Math.Max(wall.FromX, wall.ToX);
            return edge.X1 >= min && edge.X2 <= max;
        }
        return false;
    }

    private static MapEdge SharedEdge(TacticalMapCell from, TacticalMapCell to)
    {
        if (to.X == from.X + 1) return NormalizeEdge(from.X + 1, from.Y, from.X + 1, from.Y + 1);
        if (to.X == from.X - 1) return NormalizeEdge(from.X, from.Y, from.X, from.Y + 1);
        if (to.Y == from.Y + 1) return NormalizeEdge(from.X, from.Y + 1, from.X + 1, from.Y + 1);
        if (to.Y == from.Y - 1) return NormalizeEdge(from.X, from.Y, from.X + 1, from.Y);
        throw new ArgumentException("Cells do not share an orthogonal edge.");
    }

    private static MapEdge NormalizeEdge(int x1, int y1, int x2, int y2)
        => x1 < x2 || (x1 == x2 && y1 <= y2) ? new MapEdge(x1, y1, x2, y2) : new MapEdge(x2, y2, x1, y1);

    private static IEnumerable<TacticalMapCell> SupercoverLine(TacticalMapCell from, TacticalMapCell to)
    {
        var x = from.X;
        var y = from.Y;
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var nx = Math.Abs(dx);
        var ny = Math.Abs(dy);
        var signX = Math.Sign(dx);
        var signY = Math.Sign(dy);
        var ix = 0;
        var iy = 0;
        yield return from;

        while (ix < nx || iy < ny)
        {
            var decision = (1 + 2 * ix) * ny - (1 + 2 * iy) * nx;
            if (decision == 0)
            {
                x += signX;
                y += signY;
                ix++;
                iy++;
            }
            else if (decision < 0)
            {
                x += signX;
                ix++;
            }
            else
            {
                y += signY;
                iy++;
            }
            yield return new TacticalMapCell(x, y);
        }
    }

    private static bool Contains(int x, int y, int width, int height, int cellX, int cellY)
        => width > 0 && height > 0 && cellX >= x && cellY >= y && cellX < x + width && cellY < y + height;

    private static bool GridPointInsideOrOnBoundary(TacticalMap map, int x, int y)
        => x >= 0 && y >= 0 && x <= map.WidthSquares && y <= map.HeightSquares;

    private static bool DoorEdgeInside(TacticalMap map, TacticalMapDoor door)
        => door.Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase)
            ? door.X >= 0 && door.Y >= 0 && door.X < map.WidthSquares && door.Y <= map.HeightSquares
            : door.Orientation.Equals("vertical", StringComparison.OrdinalIgnoreCase)
                && door.X >= 0 && door.Y >= 0 && door.X <= map.WidthSquares && door.Y < map.HeightSquares;

    private static bool IsDoorOrientationValid(string value)
        => value.Equals("horizontal", StringComparison.OrdinalIgnoreCase) || value.Equals("vertical", StringComparison.OrdinalIgnoreCase);

    private static void ValidateRect(TacticalMapValidationReport report, TacticalMap map, string path, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) Error(report, path, "Width and height must be positive.");
        if (x < 0 || y < 0 || x + width > map.WidthSquares || y + height > map.HeightSquares)
            Error(report, path, "Rectangle extends outside the map bounds.");
    }

    private static void ValidateUniqueIds(TacticalMapValidationReport report, string path, IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) Error(report, $"{path}[{index}].id", "ID is required.");
            else if (!seen.Add(id)) Error(report, $"{path}[{index}].id", $"Duplicate ID '{id}'.");
            index++;
        }
    }

    private static void Error(TacticalMapValidationReport report, string path, string message) => report.Issues.Add(new("error", path, message));
    private static void Warn(TacticalMapValidationReport report, string path, string message) => report.Issues.Add(new("warning", path, message));

    private readonly record struct MapEdge(int X1, int Y1, int X2, int Y2);
}
