using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public ConcentrationCheckResult ResolvePendingConcentrationCheckRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo,
        DiceService dice)
    {
        Guard.NotNull(campaign, nameof(campaign));
        Guard.NotNull(dice, nameof(dice));
        if (rollOne is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollOne));
        if (rollTwo.HasValue && rollTwo.Value is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollTwo));

        var pending = campaign.PendingPlayerRoll
  ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
  throw new InvalidOperationException("The supplied roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase))
  throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not a Concentration check.");

        var character = RequireCharacter(campaign, pending.ActorCharacterId);
        if (!character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
  throw new InvalidOperationException("The pending Concentration check no longer belongs to a player character.");
        if (!pending.Context.TryGetValue("effect", out var effect) || string.IsNullOrWhiteSpace(effect))
  throw new InvalidOperationException("The pending Concentration check is missing its effect context.");
        if (!string.Equals(character.ConcentrationEffect, effect, StringComparison.OrdinalIgnoreCase))
  throw new InvalidOperationException($"{character.Name} is no longer concentrating on {effect}.");

        var dc = pending.TargetNumber ?? throw new InvalidOperationException("The pending Concentration check is missing its DC.");
        var effectiveDamage = pending.Context.TryGetValue("effective_damage", out var damageText) && int.TryParse(damageText, out var parsedDamage)
  ? parsedDamage
  : 0;
        var mode = ParsePendingRollMode(pending.RollMode);
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
  throw new InvalidOperationException($"This Concentration check requires two d20 results because it has {mode}.");

        var proficient = character.SavingThrowProficiencies.Any(x => CharacterMechanics.NormalizeAbility(x) == "constitution");
        var activeEffectBonus = RollActiveSavingThrowBonus(campaign, character.Id, dice);
        var savingThrow = CharacterMechanics.ResolveD20Test(
  character,
  "constitution",
  dc,
  rollOne,
  rollTwo,
  mode,
  proficient,
  activeEffectBonus);
        var maintained = savingThrow.Success;

        campaign.PendingPlayerRoll = null;
        if (!maintained)
  EndConcentrationInternal(campaign, character, $"failing a DC {dc} Constitution saving throw after taking damage");
        else
        {
  Touch(campaign);
  Log(campaign, "concentration_check", $"{character.Name} maintained Concentration on {effect} ({savingThrow.Total} vs DC {dc}).");
        }

        var summary = maintained
  ? $"{character.Name} maintained Concentration on {effect} ({savingThrow.Total} vs DC {dc})."
  : $"{character.Name} lost Concentration on {effect} ({savingThrow.Total} vs DC {dc}).";
        var continuationSummary = ResumePendingRollContinuation(campaign, pending.Context, dice);
if (!string.IsNullOrWhiteSpace(continuationSummary)) summary += $" {continuationSummary}";
return new ConcentrationCheckResult(effect, effectiveDamage, dc, savingThrow, maintained, summary);
    }
}
