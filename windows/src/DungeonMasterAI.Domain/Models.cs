namespace DungeonMasterAI.Domain;

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public string? SelectedCampaignId { get; set; }
    public List<CampaignState> Campaigns { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}

public sealed class AppSettings
{
    public string LlamaServerUrl { get; set; } = "http://127.0.0.1:8080";
    public string ModelName { get; set; } = "local-model";
    public string ModelPath { get; set; } = "";
    public string HuggingFaceModel { get; set; } = "unsloth/Qwen3.5-9B-GGUF:UD-Q4_K_XL";
    public int ContextSize { get; set; } = 16384;
    public int GpuLayers { get; set; } = 99;
    public bool AutoProvisionRuntime { get; set; } = true;
    public double Temperature { get; set; } = 0.75;
    public int MaxTokens { get; set; } = 700;
    public bool PlayerSafeMode { get; set; } = true;
}

public sealed partial class CampaignState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled Campaign";
    public string System { get; set; } = "D&D 5E compatible / SRD 5.2.1";
    public string Summary { get; set; } = "";
    public string Tone { get; set; } = "";
    public string PartyName { get; set; } = "Adventuring Party";
    public int Day { get; set; } = 1;
    public int MinuteOfDay { get; set; } = 480;
    public string? PartyLocationId { get; set; }
    public List<WorldLocation> Locations { get; set; } = [];
    public List<LocationConnection> Connections { get; set; } = [];
    public List<CharacterSheet> Characters { get; set; } = [];
    public List<ItemDefinition> Items { get; set; } = [];
    public List<SpellDefinition> Spells { get; set; } = [];
    public List<Merchant> Merchants { get; set; } = [];
    public List<Quest> Quests { get; set; } = [];
    public List<Faction> Factions { get; set; } = [];
    public List<EntityRelationship> Relationships { get; set; } = [];
    public List<CampaignSecret> Secrets { get; set; } = [];
    public List<TimelineEvent> Timeline { get; set; } = [];
    public List<CampaignSupplement> Supplements { get; set; } = [];
    public List<EncounterState> Encounters { get; set; } = [];
    public List<ActiveEffectState> ActiveEffects { get; set; } = [];
    public List<CampaignEvent> Events { get; set; } = [];
    public List<ChatMessage> Chat { get; set; } = [];
    public PendingRollRequest? PendingPlayerRoll { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorldLocation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "Location";
    public string Type { get; set; } = "area";
    public string Description { get; set; } = "";
    public string? ParentId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool Discovered { get; set; }
    public bool DmOnly { get; set; }
    public string SourceKind { get; set; } = "source_canon";
}

public sealed class LocationConnection
{
    public string FromLocationId { get; set; } = "";
    public string ToLocationId { get; set; } = "";
    public string Label { get; set; } = "Road";
    public int TravelMinutes { get; set; } = 5;
    public bool Hidden { get; set; }
    public string SourceKind { get; set; } = "source_canon";
}

public sealed class CharacterSheet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "Adventurer";
    public string CharacterType { get; set; } = "pc";
    public string CreatureType { get; set; } = "";
    public int Level { get; set; } = 1;
    public int ArmorClass { get; set; } = 10;
    public int MaxHp { get; set; } = 10;
    public int CurrentHp { get; set; } = 10;
    public int TempHp { get; set; }
    public int Gold { get; set; }
    public string? LocationId { get; set; }
    public string PublicKnowledge { get; set; } = "";
    public string SecretKnowledge { get; set; } = "";
    public Dictionary<string, int> Abilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Speed { get; set; } = 30;
    public string Size { get; set; } = "Medium";
    public int FreeHands { get; set; } = 1;
    public int ProficiencyBonus { get; set; } = 2;
    public List<string> SavingThrowProficiencies { get; set; } = [];
    public List<string> SkillProficiencies { get; set; } = [];
    public List<string> ToolProficiencies { get; set; } = [];
    public List<string> Conditions { get; set; } = [];
    public List<string> DamageResistances { get; set; } = [];
    public List<string> DamageVulnerabilities { get; set; } = [];
    public List<string> DamageImmunities { get; set; } = [];
    public int ExhaustionLevel { get; set; }
    public int DeathSaveSuccesses { get; set; }
    public int DeathSaveFailures { get; set; }
    public bool Stable { get; set; }
    public bool Dead { get; set; }
    public int HitDieSides { get; set; } = 8;
    public int HitDiceMaximum { get; set; } = 1;
    public int HitDiceRemaining { get; set; } = 1;
    public Dictionary<int, SpellSlotPool> SpellSlots { get; set; } = [];
    public string SpellcastingAbility { get; set; } = "intelligence";
    public List<string> PreparedSpellIds { get; set; } = [];
    public bool CanProvideVerbalComponents { get; set; } = true;
    public bool CanProvideSomaticComponents { get; set; } = true;
    public bool CanProvideMaterialComponents { get; set; } = true;
    public List<ResourcePool> Resources { get; set; } = [];
    public int AttacksPerAction { get; set; } = 1;
    public List<AttackProfile> Attacks { get; set; } = [];
    public string? ConcentrationEffect { get; set; }
    public List<InventoryEntry> Inventory { get; set; } = [];
    public string SourceKind { get; set; } = "source_canon";
}


public sealed class SpellDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "Spell";
    public int Level { get; set; }
    public string School { get; set; } = "";
    public string CastingTime { get; set; } = "Action";
    public string RangeKind { get; set; } = "distance";
    public int RangeFeet { get; set; }
    public bool RequiresVerbal { get; set; }
    public bool RequiresSomatic { get; set; }
    public bool RequiresMaterial { get; set; }
    public string MaterialDescription { get; set; } = "";
    public string Duration { get; set; } = "Instantaneous";
    public bool RequiresConcentration { get; set; }
    public bool Ritual { get; set; }
    public bool RequiresTarget { get; set; }
    public string Resolution { get; set; } = "utility"; // utility, attack, save, healing
    public string SaveAbility { get; set; } = "";
    public string DamageExpression { get; set; } = "";
    public string DamageType { get; set; } = "";
    public bool HalfDamageOnSuccessfulSave { get; set; }
    public string HealingExpression { get; set; } = "";
    public string ExtraDamagePerSlotExpression { get; set; } = "";
    public string ExtraHealingPerSlotExpression { get; set; } = "";
    public bool AddSpellcastingAbilityModifierToHealing { get; set; }
    public bool CantripDamageScaling { get; set; }
    public bool CantripRangeDoubling { get; set; }
    public bool IgnoreHalfAndThreeQuartersCoverOnSave { get; set; }
    public string RequiredTargetCreatureType { get; set; } = "";
    public string ConditionOnFailedSave { get; set; } = "";
    public bool RepeatSaveAtEndOfTurn { get; set; }
    public bool NextAttackAgainstTargetHasAdvantage { get; set; }
    public bool EffectExpiresAtEndOfCasterNextTurn { get; set; }
    public bool EffectExpiresAtStartOfCasterNextTurn { get; set; }
    public int SpeedModifierFeet { get; set; }
    public int ArmorClassBonus { get; set; }
    public string SaveDisadvantageCreatureType { get; set; } = "";
    public int BaseProjectiles { get; set; }
    public int ExtraProjectilesPerSlot { get; set; }
    public int BaseTargets { get; set; }
    public int ExtraTargetsPerSlot { get; set; }
    public string AttackRollBonusExpression { get; set; } = "";
    public string SavingThrowBonusExpression { get; set; } = "";
    public string AreaShape { get; set; } = ""; // sphere, cone, cube
    public int AreaSizeFeet { get; set; }
    public int ExtraAreaSizePerSlotFeet { get; set; }
    public string AreaOrigin { get; set; } = ""; // point, self
    public int PushFeetOnFailedSave { get; set; }
    public string EnvironmentalEffect { get; set; } = "";
    public string BattlefieldTrigger { get; set; } = "none";
    public bool BattlefieldDifficultTerrain { get; set; }
    public bool BattlefieldHeavilyObscured { get; set; }
    public bool BattlefieldBlocksLineOfSight { get; set; }
    public int BattlefieldDurationRounds { get; set; }
    public bool RequiresVisibleTarget { get; set; }
    public string SourceKind { get; set; } = "source_canon";
    public int SourcePage { get; set; }
    public string SourceReference { get; set; } = "";
}

public sealed class SpellSlotPool
{
    public int Maximum { get; set; }
    public int Remaining { get; set; }
}

public sealed class ResourcePool
{
    public string Name { get; set; } = "Resource";
    public int Maximum { get; set; }
    public int Remaining { get; set; }
    public bool RechargeOnShortRest { get; set; }
    public bool RechargeOnLongRest { get; set; } = true;
}


public sealed class AttackProfile
{
    public string Name { get; set; } = "Attack";
    public int AttackBonus { get; set; }
    public string DamageExpression { get; set; } = "1d4";
    public string DamageType { get; set; } = "";
    public int ReachFeet { get; set; } = 5;
    public int RangeFeet { get; set; }
}

public sealed class ActiveEffectState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Effect";
    public string SourceCharacterId { get; set; } = "";
    public string TargetCharacterId { get; set; } = "";
    public string SourceSpellId { get; set; } = "";
    public string ConcentrationName { get; set; } = "";
    public bool RequiresSourceConcentration { get; set; }
    public string Condition { get; set; } = "";
    public bool OwnsCondition { get; set; }
    public string RepeatSaveAbility { get; set; } = "";
    public int SaveDc { get; set; }
    public bool RepeatSaveAtEndOfTurn { get; set; }
    public bool NextAttackAgainstTargetHasAdvantage { get; set; }
    public bool ConsumeOnNextAttackAgainst { get; set; }
    public bool ExpireAtEndOfSourceNextTurn { get; set; }
    public string AttackRollBonusExpression { get; set; } = "";
    public string SavingThrowBonusExpression { get; set; } = "";
    public int SpeedModifierFeet { get; set; }
    public int ArmorClassBonus { get; set; }
    public bool ExpireAtStartOfSourceNextTurn { get; set; }
    public int AppliedRound { get; set; }
    public int AppliedTurnIndex { get; set; }
}

public sealed class EncounterState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "Encounter";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "active";
    public bool DmOnly { get; set; }
    public string? LocationId { get; set; }
    public int Round { get; set; } = 1;
    public int TurnIndex { get; set; }
    public List<CombatantState> Combatants { get; set; } = [];
    public List<TerrainFeature> Terrain { get; set; } = [];
    public List<BattlefieldEffectState> BattlefieldEffects { get; set; } = [];
    public List<GrappleState> Grapples { get; set; } = [];
    public PendingCombatMove? PendingMove { get; set; }
    public List<string> SpellSlotCasterIdsThisTurn { get; set; } = [];
    public string SourceKind { get; set; } = "source_canon";
}

public sealed class CombatantState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CharacterId { get; set; } = "";
    public int? Initiative { get; set; }
    public bool Surprised { get; set; }
    public int TieBreaker { get; set; }
    public bool Positioned { get; set; }
    public int GridX { get; set; }
    public int GridY { get; set; }
    public int MovementRemainingFeet { get; set; }
    public string Side { get; set; } = "";
    public bool ActionAvailable { get; set; }
    public bool BonusActionAvailable { get; set; }
    public bool ReactionAvailable { get; set; } = true;
    public bool AttackActionInProgress { get; set; }
    public int AttacksRemainingInAction { get; set; }
    public bool Disengaging { get; set; }
    public bool Dodging { get; set; }
    public bool DeathSaveRequiredThisTurn { get; set; }
    public bool DeathSaveResolvedThisTurn { get; set; }
    public string? HelpAttackTargetCombatantId { get; set; }
    public string? HelpAbilityTargetCharacterId { get; set; }
    public string? HelpAbilityProficiency { get; set; }
    public bool IsHidden { get; set; }
    public int HideCheckTotal { get; set; }
    public ReadiedActionState? ReadiedAction { get; set; }
}

public sealed class ReadiedActionState
{
    public string Trigger { get; set; } = "";
    public string Kind { get; set; } = "attack"; // attack, move, spell
    public string? TargetCombatantId { get; set; }
    public string? AttackName { get; set; }
    public string? SpellId { get; set; }
    public int CastAtLevel { get; set; }
    public bool UsedSpellSlot { get; set; }
    public int PreparedRound { get; set; }
    public int PreparedTurnIndex { get; set; }
}

public sealed class GrappleState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GrapplerCombatantId { get; set; } = "";
    public string TargetCombatantId { get; set; } = "";
    public int EscapeDc { get; set; } = 10;
    public int ReachFeet { get; set; } = 5;
}

public sealed class TerrainFeature
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Terrain";
    public int GridX { get; set; }
    public int GridY { get; set; }
    public int WidthSquares { get; set; } = 1;
    public int HeightSquares { get; set; } = 1;
    public bool DifficultTerrain { get; set; }
    public string Cover { get; set; } = "none"; // none, half, three-quarters, total
    public bool BlocksMovement { get; set; }
    public bool BlocksLineOfSight { get; set; }
    public bool HeavilyObscured { get; set; }
    public bool DmOnly { get; set; }
    public string SourceKind { get; set; } = "runtime_generated";
}

public sealed class BattlefieldEffectState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Battlefield Effect";
    public string SourceCharacterId { get; set; } = "";
    public string SourceSpellId { get; set; } = "";
    public string Shape { get; set; } = "sphere"; // sphere, cone, cube
    public int SizeFeet { get; set; } = 5;
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public string Direction { get; set; } = "north";
    public string Trigger { get; set; } = "none"; // none, start_turn, enter, start_or_enter, move_within
    public string DamageExpression { get; set; } = "";
    public string DamageType { get; set; } = "";
    public string SaveAbility { get; set; } = "";
    public int SaveDc { get; set; }
    public bool HalfDamageOnSuccessfulSave { get; set; }
    public bool OncePerTurn { get; set; } = true;
    public bool DifficultTerrain { get; set; }
    public bool HeavilyObscured { get; set; }
    public bool BlocksLineOfSight { get; set; }
    public bool RequiresSourceConcentration { get; set; }
    public string ConcentrationName { get; set; } = "";
    public int DurationRounds { get; set; }
    public int AppliedRound { get; set; }
    public int AppliedTurnIndex { get; set; }
    public int ExpiresAfterRound { get; set; }
    public Dictionary<string, string> LastTriggeredTurnByCharacter { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DmOnly { get; set; }
    public string SourceKind { get; set; } = "runtime_generated";
}

public sealed class PendingRollRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ActorCharacterId { get; set; } = "";
    public string? EncounterId { get; set; }
    public string? CombatantId { get; set; }
    public string Formula { get; set; } = "1d20";
    public string RollType { get; set; } = "d20";
    public string Purpose { get; set; } = "";
    public string ResolutionKey { get; set; } = "";
    public string RollMode { get; set; } = "normal";
    public int Modifier { get; set; }
    public int? TargetNumber { get; set; }
    public string TargetLabel { get; set; } = "";
    public Dictionary<string, string> Context { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Required { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PendingCombatMove
{
    public string CombatantId { get; set; } = "";
    public int FromX { get; set; }
    public int FromY { get; set; }
    public int ToX { get; set; }
    public int ToY { get; set; }
    public int DistanceFeet { get; set; }
    public int MovementCostFeet { get; set; }
    public bool ReadiedReactionMove { get; set; }
    public List<OpportunityAttackWindow> OpportunityAttacks { get; set; } = [];
}

public sealed class OpportunityAttackWindow
{
    public string ReactorCombatantId { get; set; } = "";
    public string ReactorCharacterId { get; set; } = "";
    public string ReactorName { get; set; } = "";
    public int ReachFeet { get; set; } = 5;
    public int TriggerX { get; set; }
    public int TriggerY { get; set; }
    public bool Resolved { get; set; }
    public bool Declined { get; set; }
    public string ResolutionSummary { get; set; } = "";
}

public sealed class InventoryEntry
{
    public string ItemId { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public bool Equipped { get; set; }
}

public sealed class ItemDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "Item";
    public string Category { get; set; } = "gear";
    public string Description { get; set; } = "";
    public int PriceGp { get; set; }
    public bool Consumable { get; set; }
    public bool Equippable { get; set; }
    public string EquipmentSlot { get; set; } = "";
    public string SourceKind { get; set; } = "source_canon";
}

public sealed class Merchant
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "Merchant";
    public string? LocationId { get; set; }
    public string? NpcId { get; set; }
    public int Gold { get; set; }
    public List<MerchantStockEntry> Stock { get; set; } = [];
    public string SourceKind { get; set; } = "source_canon";
}

public sealed class MerchantStockEntry
{
    public string ItemId { get; set; } = "";
    public int Quantity { get; set; }
    public int? PriceGp { get; set; }
    public string SourceKind { get; set; } = "source_canon";
}

public sealed class Quest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "Quest";
    public string Status { get; set; } = "available";
    public string Summary { get; set; } = "";
    public string DmNotes { get; set; } = "";
    public int RewardGp { get; set; }
    public bool DmOnly { get; set; }
    public List<string> Objectives { get; set; } = [];
    public string SourceKind { get; set; } = "source_canon";
}


public sealed class Faction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "Faction";
    public string Summary { get; set; } = "";
    public string PublicKnowledge { get; set; } = "";
    public string SecretKnowledge { get; set; } = "";
    public string SourceKind { get; set; } = "source_canon";
}

public sealed class EntityRelationship
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceKey { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public string Relation { get; set; } = "related_to";
    public double Strength { get; set; } = 1.0;
    public bool Public { get; set; }
    public string SourceKind { get; set; } = "source_canon";
}

public sealed class CampaignSecret
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Title { get; set; } = "Secret";
    public string Truth { get; set; } = "";
    public List<string> KnownByKeys { get; set; } = [];
    public List<string> RevealConditions { get; set; } = [];
    public bool Revealed { get; set; }
    public string SourceKind { get; set; } = "source_canon";
}

public sealed class TimelineEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "World Event";
    public string TriggerType { get; set; } = "time";
    public int CampaignDay { get; set; } = 1;
    public int MinuteOfDay { get; set; }
    public string EffectQuestKey { get; set; } = "";
    public string Consequence { get; set; } = "";
    public string DmNotes { get; set; } = "";
    public bool Resolved { get; set; }
    public string SourceKind { get; set; } = "source_canon";
}


public sealed class CampaignSupplement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TargetKey { get; set; } = "";
    public string Category { get; set; } = "detail";
    public string Content { get; set; } = "";
    public bool DmOnly { get; set; } = true;
    public string SourceKind { get; set; } = "ai_expanded";
}

public sealed class CampaignEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Type { get; set; } = "event";
    public string Summary { get; set; } = "";
    public bool DmOnly { get; set; }
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "system";
    public string Content { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record DiceRoll(string Expression, IReadOnlyList<int> Rolls, int Modifier, int Total);
public sealed record AttackResult(int D20, int Modifier, int Total, bool Hit, bool Critical, int Damage, string Summary);
public sealed record CombatMoveResult(string EncounterId, string CombatantId, string CharacterId, int FromX, int FromY, int ToX, int ToY, int DistanceFeet, int MovementCostFeet, int MovementRemainingFeet, bool Committed, IReadOnlyList<OpportunityAttackWindow> OpportunityAttacks, string Summary);
public sealed record PurchaseResult(bool Success, string Message, int BuyerGold, int RemainingStock);
public sealed record D20TestResult(int RollOne, int? RollTwo, int ChosenRoll, int AbilityModifier, int ProficiencyModifier, int ExhaustionPenalty, int Total, int DifficultyClass, bool Success, string Summary);
public sealed record DeathSaveResult(int Roll, int Successes, int Failures, bool Stable, bool Dead, int CurrentHp, string Summary);
public sealed record DamageResult(int RequestedDamage, int EffectiveDamage, string? DamageType, int TempHpLost, int HpLost, int CurrentHp, bool DroppedToZero, bool Dead, int DeathSaveFailures, string Summary);
public sealed record ConcentrationCheckResult(string Effect, int DamageTaken, int DifficultyClass, D20TestResult SavingThrow, bool Maintained, string Summary);
public sealed record DamageResolutionResult(DamageResult Damage, ConcentrationCheckResult? Concentration);
public sealed record RestResult(string RestType, int Minutes, IReadOnlyList<string> Effects, string Summary);
public sealed record InitiativeEntry(string CombatantId, string CharacterId, string Name, int Initiative, bool Surprised);
public sealed record EncounterAttackResult(string EncounterId, string AttackerName, string TargetName, string AttackName, AttackResult Attack, DamageResult? Damage, string Summary, ConcentrationCheckResult? Concentration = null, bool UsedReaction = false, int CoverBonus = 0);
public sealed record GrappleResult(string EncounterId, string GrapplerCombatantId, string TargetCombatantId, int SaveDc, string SaveAbility, D20TestResult SavingThrow, bool Grappled, string Summary);
public sealed record ShoveResult(string EncounterId, string AttackerCombatantId, string TargetCombatantId, int SaveDc, string SaveAbility, D20TestResult SavingThrow, bool Succeeded, string Effect, string Summary);
public sealed record EscapeGrappleResult(string EncounterId, string GrapplerCombatantId, string TargetCombatantId, int EscapeDc, string Skill, D20TestResult AbilityCheck, bool Escaped, string Summary);
public sealed record CombatSkillActionResult(string EncounterId, string CombatantId, string CharacterId, string ActionName, string Ability, string Skill, D20TestResult Check, string Summary);
public sealed record FirstAidResult(string EncounterId, string HelperCombatantId, string TargetCharacterId, D20TestResult MedicineCheck, bool Stabilized, bool Awakened, string Summary);
public sealed record HideResult(string EncounterId, string CombatantId, string CharacterId, D20TestResult StealthCheck, bool Hidden, int PerceptionDc, string Summary);
public sealed record HiddenSearchResult(string EncounterId, string SearcherCombatantId, string TargetCombatantId, D20TestResult PerceptionCheck, bool Found, string Summary);
public sealed record ReadyActionResult(string EncounterId, string CombatantId, string CharacterId, string Kind, string Trigger, string Summary);
public sealed record SpellTargetResolution(
    string TargetId,
    string TargetName,
    int Sequence,
    AttackResult? SpellAttack,
    D20TestResult? TargetSavingThrow,
    DamageResolutionResult? Damage,
    int Healing,
    string Summary);
public sealed record SpellCastResult(
    string SpellId,
    string SpellName,
    string CasterId,
    string? TargetId,
    int CastAtLevel,
    bool UsedSpellSlot,
    bool Ritual,
    AttackResult? SpellAttack,
    D20TestResult? TargetSavingThrow,
    DamageResolutionResult? Damage,
    int Healing,
    bool ConcentrationStarted,
    string Summary,
    IReadOnlyList<SpellTargetResolution>? TargetResults = null);
public sealed record RuleSearchResult(string ChunkKey, int Page, string Section, string Heading, string Text, int Score);
