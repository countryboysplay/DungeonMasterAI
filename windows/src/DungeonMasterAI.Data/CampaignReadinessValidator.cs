using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Data;

public enum ReadinessSeverity
{
    Info,
    Warning,
    Error
}

public sealed record CampaignReadinessIssue(ReadinessSeverity Severity, string Category, string EntityKey, string Message);

public sealed class CampaignReadinessValidator
{
    public IReadOnlyList<CampaignReadinessIssue> Validate(CampaignState campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var issues = new List<CampaignReadinessIssue>();
        var locationIds = campaign.Locations.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var characterIds = campaign.Characters.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var itemIds = campaign.Items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var spellIds = campaign.Spells.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityKeys = BuildEntityKeys(campaign);

        if (campaign.Locations.Count == 0)
            Add(issues, ReadinessSeverity.Error, "world", campaign.Name, "Campaign has no playable locations.");
        if (campaign.Characters.All(c => !c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)))
            Add(issues, ReadinessSeverity.Warning, "party", campaign.Name, "Campaign has no player character yet. Add or import a PC before a play session.");

        if (campaign.PartyLocationId is null || !locationIds.Contains(campaign.PartyLocationId))
            Add(issues, ReadinessSeverity.Error, "world", campaign.Name, "Party starting location is missing or unresolved.");
        else
        {
            var start = campaign.Locations.First(l => l.Id == campaign.PartyLocationId);
            if (start.DmOnly) Add(issues, ReadinessSeverity.Error, "visibility", start.Key, "Party starting location is marked DM-only.");
            if (!start.Discovered) Add(issues, ReadinessSeverity.Warning, "visibility", start.Key, "Party starting location is not discovered in player view.");
        }

        foreach (var connection in campaign.Connections)
        {
            if (!locationIds.Contains(connection.FromLocationId) || !locationIds.Contains(connection.ToLocationId))
                Add(issues, ReadinessSeverity.Error, "world", "connection", "A travel connection references a missing location.");
            if (connection.TravelMinutes < 0)
                Add(issues, ReadinessSeverity.Error, "world", "connection", "A travel connection has negative travel time.");
        }

        foreach (var character in campaign.Characters)
        {
            if (character.LocationId is not null && !locationIds.Contains(character.LocationId))
                Add(issues, ReadinessSeverity.Warning, "character", character.Key, $"{character.Name} references a missing location.");
            if (character.MaxHp < 1 || character.ArmorClass < 1)
                Add(issues, ReadinessSeverity.Error, "character", character.Key, $"{character.Name} has invalid combat statistics.");
            if (character.CurrentHp > character.MaxHp)
                Add(issues, ReadinessSeverity.Warning, "character", character.Key, $"{character.Name} has current HP above maximum HP.");
            foreach (var inventory in character.Inventory.Where(i => !itemIds.Contains(i.ItemId)))
                Add(issues, ReadinessSeverity.Error, "inventory", character.Key, $"{character.Name} carries an unresolved item reference '{inventory.ItemId}'.");
            foreach (var spellId in character.PreparedSpellIds.Where(i => !spellIds.Contains(i) && !campaign.Spells.Any(s => s.Key.Equals(i, StringComparison.OrdinalIgnoreCase))))
                Add(issues, ReadinessSeverity.Error, "spellcasting", character.Key, $"{character.Name} has an unresolved prepared spell reference '{spellId}'.");
            if (character.PreparedSpellIds.Count > 0 && !character.Abilities.ContainsKey(character.SpellcastingAbility) && !character.Abilities.ContainsKey(character.SpellcastingAbility[..Math.Min(3, character.SpellcastingAbility.Length)]))
                Add(issues, ReadinessSeverity.Warning, "spellcasting", character.Key, $"{character.Name} has prepared spells but no explicit {character.SpellcastingAbility} ability score; the engine will use 10.");
        }

        foreach (var spell in campaign.Spells)
        {
            if (spell.Level is < 0 or > 9)
                Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has an invalid spell level.");

            var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();
            switch (resolution)
            {
                case "attack":
                    if (string.IsNullOrWhiteSpace(spell.DamageExpression))
                        Add(issues, ReadinessSeverity.Warning, "spellcasting", spell.Key, $"{spell.Name} is an attack spell without a configured damage expression.");
                    break;

                case "save":
                    if (string.IsNullOrWhiteSpace(spell.SaveAbility))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} is a saving-throw spell without a configured save ability.");
                    break;

                case "healing":
                    if (string.IsNullOrWhiteSpace(spell.HealingExpression))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} is a healing spell without a configured healing expression.");
                    break;

                case "projectile_auto":
                case "projectile_attack":
                    if (spell.BaseProjectiles < 1)
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} is a projectile spell without a positive base projectile count.");
                    if (string.IsNullOrWhiteSpace(spell.DamageExpression))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} is a projectile spell without a configured damage expression.");
                    if (spell.ExtraProjectilesPerSlot < 0)
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has a negative extra-projectiles-per-slot value.");
                    break;

                case "multi_buff":
                    if (spell.BaseTargets < 1)
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} is a multi-target buff without a positive base target count.");
                    if (string.IsNullOrWhiteSpace(spell.AttackRollBonusExpression) && string.IsNullOrWhiteSpace(spell.SavingThrowBonusExpression) && spell.ArmorClassBonus == 0 && spell.SpeedModifierFeet == 0)
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} is a multi-target buff without a deterministic attack, save, AC, or Speed modifier.");
                    if (spell.ExtraTargetsPerSlot < 0)
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has a negative extra-targets-per-slot value.");
                    break;

                case "area_save":
                    if (string.IsNullOrWhiteSpace(spell.SaveAbility))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} is an area saving-throw spell without a configured save ability.");
                    if (string.IsNullOrWhiteSpace(spell.DamageExpression))
                        Add(issues, ReadinessSeverity.Warning, "spellcasting", spell.Key, $"{spell.Name} is an area saving-throw spell without a configured damage expression.");
                    var shape = (spell.AreaShape ?? "").Trim().ToLowerInvariant();
                    if (shape is not ("sphere" or "cone" or "cube" or "line"))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has unsupported or missing area shape '{spell.AreaShape}'. Supported alpha shapes are sphere, cone, cube, and line.");
                    if (shape == "line" && (spell.AreaWidthFeet <= 0 || spell.AreaWidthFeet % 5 != 0))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} is a line-shaped area and must declare a positive width in 5-foot increments.");
                    if (spell.AreaSizeFeet <= 0)
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has no positive area size.");
                    var origin = (spell.AreaOrigin ?? "").Trim().ToLowerInvariant();
                    if (origin is not ("point" or "self"))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has unsupported or missing area origin '{spell.AreaOrigin}'.");
                    if (origin == "point" && spell.RangeFeet <= 0)
                        Add(issues, ReadinessSeverity.Warning, "spellcasting", spell.Key, $"{spell.Name} uses a point-origin area but has no positive range configured.");
                    if (spell.PushFeetOnFailedSave < 0 || spell.PushFeetOnFailedSave % 5 != 0)
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has an invalid forced-movement distance; tactical pushes must be a non-negative multiple of 5 feet.");
                    break;

                case "persistent_area":
                    var persistentShape = (spell.AreaShape ?? "").Trim().ToLowerInvariant();
                    if (persistentShape is not ("sphere" or "cone" or "cube" or "line"))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has unsupported or missing persistent-area shape '{spell.AreaShape}'.");
                    if (persistentShape == "line" && (spell.AreaWidthFeet <= 0 || spell.AreaWidthFeet % 5 != 0))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} is a line-shaped persistent area and must declare a positive width in 5-foot increments.");
                    if (spell.AreaSizeFeet <= 0 || spell.AreaSizeFeet % 5 != 0)
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} must have a positive persistent-area size in 5-foot increments.");
                    if (spell.ExtraAreaSizePerSlotFeet < 0 || spell.ExtraAreaSizePerSlotFeet % 5 != 0)
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has an invalid extra-area-size-per-slot value; tactical area growth must use non-negative 5-foot increments.");
                    var persistentOrigin = (spell.AreaOrigin ?? "").Trim().ToLowerInvariant();
                    if (persistentOrigin is not ("point" or "self"))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has unsupported or missing persistent-area origin '{spell.AreaOrigin}'.");
                    if (persistentOrigin == "point" && spell.RangeFeet <= 0)
                        Add(issues, ReadinessSeverity.Warning, "spellcasting", spell.Key, $"{spell.Name} uses a point-origin persistent area but has no positive range configured.");
                    if (spell.BattlefieldTrigger is not ("none" or "start_turn" or "enter" or "start_or_enter" or "move_within"))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has unsupported battlefield trigger '{spell.BattlefieldTrigger}'.");
                    if (!spell.BattlefieldTrigger.Equals("none", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(spell.DamageExpression))
                        Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has a triggered persistent area but no deterministic damage expression.");
                    break;
            }

            if (spell.SpeedModifierFeet % 5 != 0)
                Add(issues, ReadinessSeverity.Error, "spellcasting", spell.Key, $"{spell.Name} has a Speed modifier that is not a 5-foot increment.");
            if (Math.Abs(spell.ArmorClassBonus) > 20)
                Add(issues, ReadinessSeverity.Warning, "spellcasting", spell.Key, $"{spell.Name} has an unusually large AC modifier ({spell.ArmorClassBonus}).");

            if (spell.Level == 0 && spell.Ritual)
                Add(issues, ReadinessSeverity.Warning, "spellcasting", spell.Key, $"{spell.Name} is a cantrip marked as a Ritual; the engine will reject Ritual casting for cantrips.");
        }

        foreach (var merchant in campaign.Merchants)
        {
            if (merchant.LocationId is not null && !locationIds.Contains(merchant.LocationId))
                Add(issues, ReadinessSeverity.Warning, "merchant", merchant.Key, $"{merchant.Name} references a missing location.");
            if (merchant.NpcId is not null && !characterIds.Contains(merchant.NpcId))
                Add(issues, ReadinessSeverity.Warning, "merchant", merchant.Key, $"{merchant.Name} references a missing NPC.");
            if (merchant.Stock.Count == 0)
                Add(issues, ReadinessSeverity.Info, "merchant", merchant.Key, $"{merchant.Name} has no compiled stock yet.");
            foreach (var stock in merchant.Stock)
            {
                if (!itemIds.Contains(stock.ItemId)) Add(issues, ReadinessSeverity.Error, "merchant", merchant.Key, $"{merchant.Name} stocks an unresolved item '{stock.ItemId}'.");
                if (stock.Quantity < 0) Add(issues, ReadinessSeverity.Error, "merchant", merchant.Key, $"{merchant.Name} has negative stock quantity.");
            }
        }

        foreach (var quest in campaign.Quests)
        {
            var hasGeneratedObjective = campaign.Supplements.Any(s => s.TargetKey.Equals(quest.Key, StringComparison.OrdinalIgnoreCase)
                && s.Category.Equals("quest_objective", StringComparison.OrdinalIgnoreCase));
            if (quest.Objectives.Count == 0 && !hasGeneratedObjective)
                Add(issues, ReadinessSeverity.Info, "quest", quest.Key, $"{quest.Name} has no structured objectives yet.");
        }

        foreach (var encounter in campaign.Encounters)
        {
            if (encounter.LocationId is not null && !locationIds.Contains(encounter.LocationId))
                Add(issues, ReadinessSeverity.Error, "encounter", encounter.Key, $"{encounter.Name} references a missing location.");
            if (encounter.Combatants.Count == 0)
                Add(issues, ReadinessSeverity.Warning, "encounter", encounter.Key, $"{encounter.Name} has no compiled participants.");
            foreach (var combatant in encounter.Combatants.Where(c => !characterIds.Contains(c.CharacterId)))
                Add(issues, ReadinessSeverity.Error, "encounter", encounter.Key, $"{encounter.Name} references missing combatant character '{combatant.CharacterId}'.");
            foreach (var effect in encounter.BattlefieldEffects)
            {
                var effectKey = $"{encounter.Key}:{effect.Name}";
                if (effect.Shape is not ("sphere" or "cone" or "cube"))
                    Add(issues, ReadinessSeverity.Error, "battlefield_effect", effectKey, $"Battlefield effect '{effect.Name}' has unsupported shape '{effect.Shape}'.");
                if (effect.SizeFeet <= 0 || effect.SizeFeet % 5 != 0)
                    Add(issues, ReadinessSeverity.Error, "battlefield_effect", effectKey, $"Battlefield effect '{effect.Name}' must use a positive 5-foot area size.");
                if (effect.Trigger is not ("none" or "start_turn" or "enter" or "start_or_enter"))
                    Add(issues, ReadinessSeverity.Error, "battlefield_effect", effectKey, $"Battlefield effect '{effect.Name}' has unsupported trigger '{effect.Trigger}'.");
                if (!effect.Trigger.Equals("none", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(effect.DamageExpression))
                    Add(issues, ReadinessSeverity.Error, "battlefield_effect", effectKey, $"Triggered battlefield effect '{effect.Name}' has no deterministic damage expression.");
                if (!string.IsNullOrWhiteSpace(effect.SourceCharacterId) && !characterIds.Contains(effect.SourceCharacterId))
                    Add(issues, ReadinessSeverity.Error, "battlefield_effect", effectKey, $"Battlefield effect '{effect.Name}' references missing source character '{effect.SourceCharacterId}'.");
                if (!string.IsNullOrWhiteSpace(effect.SourceSpellId) && !spellIds.Contains(effect.SourceSpellId) && !campaign.Spells.Any(sp => sp.Key.Equals(effect.SourceSpellId, StringComparison.OrdinalIgnoreCase)))
                    Add(issues, ReadinessSeverity.Warning, "battlefield_effect", effectKey, $"Battlefield effect '{effect.Name}' references unresolved source spell '{effect.SourceSpellId}'.");
                if (effect.RequiresSourceConcentration && string.IsNullOrWhiteSpace(effect.SourceCharacterId))
                    Add(issues, ReadinessSeverity.Error, "battlefield_effect", effectKey, $"Concentration-bound battlefield effect '{effect.Name}' has no source character.");
            }
        }

        foreach (var relationship in campaign.Relationships)
        {
            if (!entityKeys.Contains(relationship.SourceKey))
                Add(issues, ReadinessSeverity.Warning, "relationship", relationship.SourceKey, $"Relationship source '{relationship.SourceKey}' does not resolve to a compiled entity.");
            if (!entityKeys.Contains(relationship.TargetKey))
                Add(issues, ReadinessSeverity.Warning, "relationship", relationship.TargetKey, $"Relationship target '{relationship.TargetKey}' does not resolve to a compiled entity.");
        }

        foreach (var secret in campaign.Secrets)
        {
            foreach (var knownBy in secret.KnownByKeys.Where(key => !entityKeys.Contains(key)))
                Add(issues, ReadinessSeverity.Warning, "secret", secret.Key, $"Secret '{secret.Title}' is known by unresolved entity '{knownBy}'.");
            var hasGeneratedReveal = campaign.Supplements.Any(s => s.TargetKey.Equals(secret.Key, StringComparison.OrdinalIgnoreCase)
                && s.Category.Equals("secret_reveal_condition", StringComparison.OrdinalIgnoreCase));
            if (!secret.Revealed && secret.RevealConditions.Count == 0 && !hasGeneratedReveal)
                Add(issues, ReadinessSeverity.Info, "secret", secret.Key, $"Secret '{secret.Title}' has no structured reveal condition.");
        }

        ValidateTacticalMaps(campaign, characterIds, issues);

        foreach (var evt in campaign.Timeline)
        {
            if (evt.CampaignDay < 1 || evt.MinuteOfDay is < 0 or > 1439)
                Add(issues, ReadinessSeverity.Error, "timeline", evt.Key, $"Timeline event '{evt.Name}' has an invalid scheduled time.");
            if (!string.IsNullOrWhiteSpace(evt.EffectQuestKey) && !campaign.Quests.Any(q => q.Key.Equals(evt.EffectQuestKey, StringComparison.OrdinalIgnoreCase)))
                Add(issues, ReadinessSeverity.Warning, "timeline", evt.Key, $"Timeline event '{evt.Name}' references missing quest '{evt.EffectQuestKey}'.");
        }

        return issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.EntityKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static readonly string[] DoorOrientations = ["vertical", "horizontal"];
    private static readonly string[] DoorStates = ["open", "closed", "locked", "barred"];
    private static readonly string[] RoomKinds = ["room", "corridor", "cave", "exterior"];
    private static readonly string[] ZoneTypes = ["encounter", "trap", "loot", "quest", "trigger"];
    private static readonly string[] CoverKinds = ["none", "half", "three_quarters", "total"];
    private static readonly string[] SpawnSides = ["party", "enemy", "ally", "neutral"];

    /// <summary>
    /// Validates authored tactical maps and their encounter bindings. Grid cells are zero-based, so
    /// a cell rectangle must fit inside [0, WidthSquares) x [0, HeightSquares). Walls sit on cell
    /// edges, so their endpoints are vertices and may legally touch the inclusive upper bound.
    /// </summary>
    private static void ValidateTacticalMaps(CampaignState campaign, HashSet<string> characterIds, ICollection<CampaignReadinessIssue> issues)
    {
        var mapIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characterKeys = campaign.Characters.Select(c => c.Key).Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var encounterIds = campaign.Encounters.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var questKeys = campaign.Quests.Select(q => q.Key).Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var map in campaign.TacticalMaps)
        {
            var key = string.IsNullOrWhiteSpace(map.Key) ? map.Id : map.Key;

            if (string.IsNullOrWhiteSpace(map.Id))
                Add(issues, ReadinessSeverity.Error, "map", key, $"Tactical map '{map.Name}' has no stable id.");
            else if (!mapIds.Add(map.Id))
                Add(issues, ReadinessSeverity.Error, "map", key, $"Tactical map '{map.Name}' reuses map id '{map.Id}'. Encounter bindings would resolve ambiguously.");

            if (string.IsNullOrWhiteSpace(map.Key))
                Add(issues, ReadinessSeverity.Warning, "map", key, $"Tactical map '{map.Name}' has no stable key. Tool and import round-trips will fall back to the generated id.");
            else if (!mapKeys.Add(map.Key))
                Add(issues, ReadinessSeverity.Error, "map", key, $"Tactical map '{map.Name}' reuses map key '{map.Key}'.");

            if (map.SchemaVersion > TacticalMapSchema.CurrentMapSchemaVersion)
                Add(issues, ReadinessSeverity.Error, "map", key, $"Tactical map '{map.Name}' declares schema version {map.SchemaVersion}, newer than the supported version {TacticalMapSchema.CurrentMapSchemaVersion}. This build cannot interpret it safely.");
            else if (map.SchemaVersion < 1)
                Add(issues, ReadinessSeverity.Error, "map", key, $"Tactical map '{map.Name}' has an invalid schema version {map.SchemaVersion}.");

            if (!CampaignProvenance.IsRecognized(map.SourceKind))
                Add(issues, ReadinessSeverity.Warning, "map", key, $"Tactical map '{map.Name}' has unrecognized provenance '{map.SourceKind}'.");

            if (map.WidthSquares < 1 || map.HeightSquares < 1)
            {
                Add(issues, ReadinessSeverity.Error, "map", key, $"Tactical map '{map.Name}' has a non-positive grid size ({map.WidthSquares}x{map.HeightSquares}).");
                continue;
            }

            if (map.FeetPerSquare < 1 || map.FeetPerSquare % 5 != 0)
                Add(issues, ReadinessSeverity.Error, "map", key, $"Tactical map '{map.Name}' must use a positive 5-foot grid scale; found {map.FeetPerSquare} feet per square.");

            var width = map.WidthSquares;
            var height = map.HeightSquares;

            void Rect(string category, string label, int x, int y, int w, int h)
            {
                if (w < 1 || h < 1)
                    Add(issues, ReadinessSeverity.Error, category, key, $"{label} on '{map.Name}' has a non-positive size ({w}x{h}).");
                else if (x < 0 || y < 0 || x + w > width || y + h > height)
                    Add(issues, ReadinessSeverity.Error, category, key, $"{label} on '{map.Name}' extends outside the {width}x{height} grid.");
            }

            foreach (var room in map.Rooms)
            {
                Rect("map_room", $"Room '{room.Name}'", room.X, room.Y, room.WidthSquares, room.HeightSquares);
                if (!RoomKinds.Contains(room.Kind, StringComparer.OrdinalIgnoreCase))
                    Add(issues, ReadinessSeverity.Warning, "map_room", key, $"Room '{room.Name}' on '{map.Name}' has unsupported kind '{room.Kind}'.");
            }

            if (map.Rooms.Count == 0 && map.Terrain.Count == 0)
                Add(issues, ReadinessSeverity.Warning, "map", key, $"Tactical map '{map.Name}' has no rooms or terrain and would render as an empty grid.");

            foreach (var wall in map.Walls)
            {
                if (wall.FromX < 0 || wall.FromY < 0 || wall.ToX < 0 || wall.ToY < 0
                    || wall.FromX > width || wall.ToX > width || wall.FromY > height || wall.ToY > height)
                    Add(issues, ReadinessSeverity.Error, "map_wall", key, $"A wall on '{map.Name}' has an endpoint outside the {width}x{height} grid.");
                if (wall.FromX == wall.ToX && wall.FromY == wall.ToY)
                    Add(issues, ReadinessSeverity.Error, "map_wall", key, $"A wall on '{map.Name}' is zero-length and cannot block movement or sight.");
                if (wall.HeightFeet < 0)
                    Add(issues, ReadinessSeverity.Error, "map_wall", key, $"A wall on '{map.Name}' has a negative height.");
            }

            foreach (var door in map.Doors)
            {
                if (door.X < 0 || door.Y < 0 || door.X >= width || door.Y >= height)
                    Add(issues, ReadinessSeverity.Error, "map_door", key, $"Door '{door.Name}' on '{map.Name}' sits outside the {width}x{height} grid.");
                if (!DoorOrientations.Contains(door.Orientation, StringComparer.OrdinalIgnoreCase))
                    Add(issues, ReadinessSeverity.Error, "map_door", key, $"Door '{door.Name}' on '{map.Name}' has unsupported orientation '{door.Orientation}'.");
                if (!DoorStates.Contains(door.State, StringComparer.OrdinalIgnoreCase))
                    Add(issues, ReadinessSeverity.Error, "map_door", key, $"Door '{door.Name}' on '{map.Name}' has unsupported state '{door.State}'.");
                if (door.Secret && door.Discovered)
                    Add(issues, ReadinessSeverity.Warning, "visibility", key, $"Secret door '{door.Name}' on '{map.Name}' is already marked discovered and will be visible to players at session start.");
            }

            foreach (var terrain in map.Terrain)
            {
                Rect("map_terrain", $"Terrain '{terrain.Name}'", terrain.X, terrain.Y, terrain.WidthSquares, terrain.HeightSquares);
                if (!CoverKinds.Contains(terrain.Cover, StringComparer.OrdinalIgnoreCase))
                    Add(issues, ReadinessSeverity.Error, "map_terrain", key, $"Terrain '{terrain.Name}' on '{map.Name}' has unsupported cover '{terrain.Cover}'.");
                if (terrain.ElevationFeet % 5 != 0)
                    Add(issues, ReadinessSeverity.Warning, "map_terrain", key, $"Terrain '{terrain.Name}' on '{map.Name}' has elevation {terrain.ElevationFeet} ft, which is not a 5-foot increment.");
            }

            foreach (var prop in map.Props)
            {
                Rect("map_prop", $"Prop '{prop.Name}'", prop.X, prop.Y, prop.WidthSquares, prop.HeightSquares);
                if (!CoverKinds.Contains(prop.Cover, StringComparer.OrdinalIgnoreCase))
                    Add(issues, ReadinessSeverity.Error, "map_prop", key, $"Prop '{prop.Name}' on '{map.Name}' has unsupported cover '{prop.Cover}'.");
                if (prop.RotationDegrees % 90 != 0)
                    Add(issues, ReadinessSeverity.Warning, "map_prop", key, $"Prop '{prop.Name}' on '{map.Name}' uses rotation {prop.RotationDegrees}°; the grid renderer only guarantees 90° steps.");
            }

            foreach (var light in map.Lights)
            {
                if (light.X < 0 || light.Y < 0 || light.X > width || light.Y > height)
                    Add(issues, ReadinessSeverity.Error, "map_light", key, $"Light '{light.Name}' on '{map.Name}' sits outside the {width}x{height} grid.");
                if (light.BrightRadiusFeet < 0 || light.DimRadiusFeet < 0)
                    Add(issues, ReadinessSeverity.Error, "map_light", key, $"Light '{light.Name}' on '{map.Name}' has a negative illumination radius.");
            }

            var sawPartySpawn = false;
            foreach (var spawn in map.SpawnPoints)
            {
                if (spawn.X < 0 || spawn.Y < 0 || spawn.X >= width || spawn.Y >= height)
                    Add(issues, ReadinessSeverity.Error, "map_spawn", key, $"Spawn point '{spawn.Name}' on '{map.Name}' sits outside the {width}x{height} grid.");
                if (!SpawnSides.Contains(spawn.Side, StringComparer.OrdinalIgnoreCase))
                    Add(issues, ReadinessSeverity.Error, "map_spawn", key, $"Spawn point '{spawn.Name}' on '{map.Name}' has unsupported side '{spawn.Side}'.");
                if (spawn.Side.Equals("party", StringComparison.OrdinalIgnoreCase)) sawPartySpawn = true;
                if (!string.IsNullOrWhiteSpace(spawn.CharacterKey)
                    && !characterKeys.Contains(spawn.CharacterKey) && !characterIds.Contains(spawn.CharacterKey))
                    Add(issues, ReadinessSeverity.Error, "map_spawn", key, $"Spawn point '{spawn.Name}' on '{map.Name}' reserves an unresolved character '{spawn.CharacterKey}'.");
            }

            if (map.SpawnPoints.Count > 0 && !sawPartySpawn)
                Add(issues, ReadinessSeverity.Warning, "map_spawn", key, $"Tactical map '{map.Name}' defines spawn points but none is marked for the party.");

            foreach (var zone in map.Zones)
            {
                Rect("map_zone", $"Zone '{zone.Name}'", zone.X, zone.Y, zone.WidthSquares, zone.HeightSquares);
                if (!ZoneTypes.Contains(zone.ZoneType, StringComparer.OrdinalIgnoreCase))
                    Add(issues, ReadinessSeverity.Error, "map_zone", key, $"Zone '{zone.Name}' on '{map.Name}' has unsupported type '{zone.ZoneType}'.");
                if (string.IsNullOrWhiteSpace(zone.ReferenceId)) continue;
                if (zone.ZoneType.Equals("encounter", StringComparison.OrdinalIgnoreCase)
                    && !encounterIds.Contains(zone.ReferenceId)
                    && !campaign.Encounters.Any(e => e.Key.Equals(zone.ReferenceId, StringComparison.OrdinalIgnoreCase)))
                    Add(issues, ReadinessSeverity.Error, "map_zone", key, $"Encounter zone '{zone.Name}' on '{map.Name}' references missing encounter '{zone.ReferenceId}'.");
                if (zone.ZoneType.Equals("quest", StringComparison.OrdinalIgnoreCase) && !questKeys.Contains(zone.ReferenceId))
                    Add(issues, ReadinessSeverity.Error, "map_zone", key, $"Quest zone '{zone.Name}' on '{map.Name}' references missing quest '{zone.ReferenceId}'.");
            }

            var roomIds = map.Rooms.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var revealed in map.Visibility.RevealedRoomIds.Where(id => !roomIds.Contains(id)))
                Add(issues, ReadinessSeverity.Warning, "visibility", key, $"Tactical map '{map.Name}' reveals unknown room '{revealed}'.");
            foreach (var cell in map.Visibility.RevealedCells.Where(c => c.X < 0 || c.Y < 0 || c.X >= width || c.Y >= height))
                Add(issues, ReadinessSeverity.Warning, "visibility", key, $"Tactical map '{map.Name}' reveals cell ({cell.X},{cell.Y}) outside the {width}x{height} grid.");
            if (map.FogOfWarEnabled && map.Visibility.RevealAll)
                Add(issues, ReadinessSeverity.Warning, "visibility", key, $"Tactical map '{map.Name}' enables fog of war but also reveals the whole map, so fog will have no effect.");
            if (map.Visibility.RevealAll && map.Rooms.Any(r => r.DmOnly))
                Add(issues, ReadinessSeverity.Warning, "visibility", key, $"Tactical map '{map.Name}' reveals everything while containing DM-only rooms, which would expose them to players.");
        }

        foreach (var binding in campaign.EncounterMapBindings)
        {
            var encounter = campaign.Encounters.FirstOrDefault(e => e.Id.Equals(binding.Key, StringComparison.OrdinalIgnoreCase));
            if (encounter is null)
                Add(issues, ReadinessSeverity.Error, "map_binding", binding.Key, $"A tactical map is bound to missing encounter '{binding.Key}'.");
            if (!mapIds.Contains(binding.Value))
            {
                Add(issues, ReadinessSeverity.Error, "map_binding", encounter?.Key ?? binding.Key, $"Encounter '{encounter?.Name ?? binding.Key}' is bound to missing tactical map '{binding.Value}'.");
                continue;
            }
            if (encounter is null) continue;

            var map = campaign.TacticalMaps.First(m => m.Id.Equals(binding.Value, StringComparison.OrdinalIgnoreCase));
            if (map.WidthSquares < 1 || map.HeightSquares < 1) continue;
            foreach (var combatant in encounter.Combatants.Where(c => c.Positioned))
            {
                if (combatant.GridX < 0 || combatant.GridY < 0 || combatant.GridX >= map.WidthSquares || combatant.GridY >= map.HeightSquares)
                    Add(issues, ReadinessSeverity.Error, "map_binding", encounter.Key, $"Encounter '{encounter.Name}' places a combatant at ({combatant.GridX},{combatant.GridY}), outside its bound map '{map.Name}' ({map.WidthSquares}x{map.HeightSquares}).");
            }
        }
    }

    private static HashSet<string> BuildEntityKeys(CampaignState campaign)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in campaign.Locations.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Characters.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Items.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Spells.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Merchants.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Quests.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Factions.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Encounters.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Secrets.Select(x => x.Key)) AddKey(keys, key);
        return keys;
    }

    private static void AddKey(ISet<string> keys, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
    }

    private static void Add(ICollection<CampaignReadinessIssue> issues, ReadinessSeverity severity, string category, string key, string message) =>
        issues.Add(new CampaignReadinessIssue(severity, category, string.IsNullOrWhiteSpace(key) ? "unknown" : key, message));
}
