namespace DungeonMasterAI.App;


public sealed record SessionChatMessageDisplay(
    string Speaker,
    string Content,
    bool IsUser,
    bool IsAssistant);

public sealed record CombatantDisplay(
    string CombatantId,
    string CharacterId,
    string Name,
    string CharacterType,
    int? Initiative,
    int ArmorClass,
    int CurrentHp,
    int MaxHp,
    int TempHp,
    bool Surprised,
    bool Dead,
    string Concentration,
    string Conditions,
    string GrappleStatus,
    string HelpStatus,
    bool Hidden,
    string ReadyStatus,
    string AttackNames,
    bool Positioned,
    int GridX,
    int GridY,
    int MovementRemainingFeet,
    int SpeedFeet,
    string Side,
    bool ActionAvailable,
    bool BonusActionAvailable,
    int AttacksRemainingInAction,
    bool ReactionAvailable,
    bool Disengaging,
    bool Dodging,
    bool IsCurrentTurn);

public sealed record FactionDisplay(
    string Name,
    string Summary,
    string Knowledge);

public sealed record SecretDisplay(
    string Title,
    string Truth,
    string RevealConditions,
    bool Revealed);

public sealed record RelationshipDisplay(
    string SourceKey,
    string TargetKey,
    string Relation,
    double Strength,
    bool Public);

public sealed record TimelineDisplay(
    string Name,
    string Schedule,
    string Consequence,
    string Notes,
    bool Resolved);


public sealed record SpellDisplay(
    string Id,
    string Name,
    int Level,
    string School,
    string CastingTime,
    string Range,
    string Components,
    string Duration,
    string Resolution,
    string Effect,
    bool Ritual,
    bool Concentration);

public sealed record SpellLibraryDisplay(
    string Id,
    string Name,
    int Level,
    string School,
    string CastingTime,
    string Range,
    string Components,
    string Duration,
    bool Ritual,
    bool Concentration,
    bool Deterministic,
    string Resolution,
    int SourcePage,
    string SourceReference,
    string Status);
