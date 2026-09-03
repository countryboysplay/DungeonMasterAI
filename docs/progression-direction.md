# Progression Direction — XP, Levelling, and the Reward Loop

**Status:** Settled direction. The product owner has decided that XP, levelling and visible
progression become app systems. This document specifies them; it does not re-argue them.

**Scope of this document:** the economy. What awards XP, how much, how it divides across the
player characters, what the curve is, what a level grants, what the player sees at each moment, and
what breaks if the numbers are wrong. It is written to be implemented from without guessing.

**Scope of the r62 implementation it specifies:** domain, engine, persistence. The presentation
layer is a separate track; §7 is the contract this layer publishes to it.

**Constraints this design honours, from `docs/game-feel-direction.md`:**

- The party is four real PCs, not a protagonist. Every rule below is written for N eligible PCs and
  verified at N = 4.
- The engine adjudicates; the LLM narrates. There is no XP-granting tool and there will not be one.
- The r59 visual language stands. Nothing here asks for a restyle.
- No monetisation, streaks, or daily rewards. This is a local, offline, single-player application.
  Retention machinery would be both inappropriate and off-identity.

---

## 1. What this economy is for

XP is one currency, and it is an unusual one: **it is never spent by the player.** It is a
monotonically rising counter whose only consumer is a threshold. So the ordinary faucet/drain frame
translates as:

- **Faucets** — the events that pay XP, and their rates.
- **The drain** — the level threshold curve, which is what converts accumulated XP into permanent
  power and resets the "distance to the next thing" that drives the loop.
- **Inflation** — levelling faster than the content and the fiction can absorb: level-up spam, a
  party outrunning its own campaign, thresholds crossed while the player is still reading the last
  award.
- **Deflation** — the more likely failure here: an award cadence so sparse that a session of play
  moves no visible number, which reads as *nothing happening* and is exactly the "tool, not a game"
  problem this product is trying to leave.

The decisions this economy has to create for the player are, in order of importance:

1. **"Is this fight worth having?"** — engaging opposition must visibly pay, so combat is a choice
   with an upside rather than a tax on hit points.
2. **"Is there another way through this?"** — talking, sneaking and bribing must pay the *same*, or
   the economy quietly teaches murderhobo play and every `alternate_resolutions` entry in the
   campaign manifests becomes a strictly dominated strategy.
3. **"Is there anything over there?"** — exploration must pay a trickle, so the quiet stretches
   between fights still move a number.
4. **"When do I cash this in?"** — a crossed threshold is banked, not instant, which creates a
   small, real anticipation beat and a reason to call a rest.

Everything below serves those four and nothing else.

---

## 2. Currency specification

### Currency: Experience Points (XP)

| Field | Value |
| --- | --- |
| **Purpose** | Convert adjudicated outcomes into permanent, visible character growth. Creates decisions 1–4 above. |
| **Type** | Soft, non-spendable, monotonic, per-character. |
| **Sources** | F1 defeated opposition, F2 quest completion, F3 first discovery, F4 non-combat resolution. §3. |
| **Sink** | Level thresholds only. §4. There is no other consumer and none will be added. |
| **Faucet/drain target** | Expressed as **sessions per level**: 1.0 at levels 1–2 (tutorial slope), then **1.0–2.5 across every legitimate playstyle**, levels 3–20, with the spread between the fastest and slowest under 2.5×. §5. |
| **Cap** | Level 20 threshold (355,000). XP continues to accrue past it and is stored, but grants nothing. Not clamped — clamping would make a level-20 save lossy if a future round adds epic boons. |
| **Conversion paths** | XP → level, one-way, at fixed thresholds. XP is never converted to gold, items, or anything else, and gold is never converted to XP. |
| **Exploit surface** | Re-killing, quest-status churn, re-revealing locations, encounter-end farming, mid-combat level-up healing, save-scum re-kill. All addressed in §6. |

**One currency, deliberately.** A second currency (renown, milestone tokens, ability points) would
have to earn its place by creating a decision XP does not. None of the candidates do, at this
product's scope. Do not add one without a decision it enables.

**Per-character storage, party-wide awards.** XP lives on `CharacterSheet`, not on `CampaignState`.
Characters own their experience: one can die, be replaced, or be added later, and a single party
counter cannot express any of that. But every award is divided across the party (§3.5), so in
ordinary play all the totals stay equal and the UI can honestly show one party-level number.

**Party size is N, not 4.** Four is the design target, the number every table in §5 is modelled at,
and the number the product ships with. It is not an invariant: every rule below is a function of
the eligible party membership at the moment of the award, and the implementation reads that
membership rather than a constant.

---

## 3. The faucets

### 3.0 Award identity

Every award is a `(source, subjectId)` pair that pays **exactly once, ever**, enforced by a
persisted flag on the subject — not by a transaction log, not by a timestamp. A flag on the thing
that paid survives save/load, campaign clone, and reload-and-retry, which a log does not.

| Faucet | Subject | Idempotency flag |
| --- | --- | --- |
| F1 defeat | The defeated creature | `CharacterSheet.ExperienceAwarded` |
| F4 resolution | The undefeated creature | `CharacterSheet.ExperienceAwarded` (same flag — a creature pays once, by either route) |
| F2 quest | The quest | `Quest.RewardsGranted` |
| F3 discovery | The location | `WorldLocation.DiscoveryExperienceAwarded` |

---

### F1 — Defeated opposition

**Trigger.** The instant a non-PC creature transitions to dead. In the engine this is the single
choke point `GameEngine.MarkDead`, which every death path already funnels through: damage at 0 HP,
massive-damage overkill, monster drop-to-zero, and the third death-save failure.

**Why at the kill and not at the end of the encounter.** The whole thesis of
`docs/game-feel-direction.md` is that this product computes drama and then discards it before the
player sees it. Batching four kills into one end-of-fight total would repeat that mistake at the
level of the reward loop. The award rides the killing blow.

**Who it pays.** The whole eligible party (§3.5), not the killer. There is no kill-steal economy
here and there must not be one — four PCs are controlled by one player, so a kill-credit rule would
create a tactically optimal but narratively absurd play pattern (funnel every finishing blow through
one character).

**Value.** Resolution order, first match wins:

1. `CharacterSheet.ExperienceValue` — an explicit authored override, if > 0.
2. `CharacterSheet.ChallengeRating` — an authored SRD CR string, mapped through the CR→XP table.
3. **Derived CR** — computed from stats already present in the domain. §3.1.

Floor: **10 XP.** A defeated hostile never pays zero. A zero award is worse than a small one; it
teaches the player that the number is decorative.

**Eligibility.** `CharacterType` is not `pc`. PCs never award XP, whoever kills them. NPCs and
monsters both do — a hostile innkeeper is opposition when you kill them, and the domain has no
"hostile" flag to distinguish it. This is a deliberate accepted looseness: killing a friendly NPC
pays XP. The alternative (a hostility model) is a larger design than this track, and the fiction
punishes murder through the DM, not through the ledger.

---

### 3.1 Derived Challenge Rating — the load-bearing detail

**The problem this solves.** There is no CR anywhere in the domain today, and there is none in the
campaign manifests either. The sample manifest's Ashen Watcher carries `armor_class: 12`,
`max_hp: 9`, `attack_bonus: 3`, `damage: "1d6+1"` and nothing else. A design that reads CR off the
sheet would award the 10 XP floor for every creature in every imported campaign, forever, and the
economy would be dead on arrival while passing every unit test. **This is the single most likely way
this feature ships broken.**

So the engine derives an effective CR from stats it actually has, by the standard DMG method:

**Defensive CR** from `MaxHp`, adjusted by `ArmorClass`:

| HP | Base CR | Expected AC |
| --- | --- | --- |
| 1–6 | 0 | 13 |
| 7–35 | 1/8 | 13 |
| 36–49 | 1/4 | 13 |
| 50–70 | 1/2 | 13 |
| 71–85 | 1 | 13 |
| 86–100 | 2 | 13 |
| 101–115 | 3 | 13 |
| 116–130 | 4 | 14 |
| 131–145 | 5 | 15 |
| 146–160 | 6 | 15 |
| 161–175 | 7 | 15 |
| 176–190 | 8 | 16 |
| 191–205 | 9 | 16 |
| 206–220 | 10 | 17 |
| 221–235 | 11 | 17 |
| 236–250 | 12 | 17 |
| 251–265 | 13 | 18 |
| 266–280 | 14 | 18 |
| 281–295 | 15 | 18 |
| 296–310 | 16 | 18 |
| 311–325 | 17 | 19 |
| 326–340 | 18 | 19 |
| 341–355 | 19 | 19 |
| 356–400 | 20 | 19 |
| 401–445 | 21 | 19 |
| 446–490 | 22 | 19 |
| 491–535 | 23 | 19 |
| 536–580 | 24 | 19 |
| 581–625 | 25 | 19 |
| 626–670 | 26 | 19 |
| 671–715 | 27 | 19 |
| 716–760 | 28 | 19 |
| 761–805 | 29 | 19 |
| 806+ | 30 | 19 |

**Offensive CR** from expected damage per round, adjusted by attack bonus:

| DPR | Base CR | Expected attack bonus |
| --- | --- | --- |
| 0–1 | 0 | +3 |
| 2–3 | 1/8 | +3 |
| 4–5 | 1/4 | +3 |
| 6–8 | 1/2 | +3 |
| 9–14 | 1 | +3 |
| 15–20 | 2 | +3 |
| 21–26 | 3 | +4 |
| 27–32 | 4 | +5 |
| 33–38 | 5 | +6 |
| 39–44 | 6 | +6 |
| 45–50 | 7 | +6 |
| 51–56 | 8 | +7 |
| 57–62 | 9 | +7 |
| 63–68 | 10 | +7 |
| 69–74 | 11 | +8 |
| 75–80 | 12 | +8 |
| 81–86 | 13 | +8 |
| 87–92 | 14 | +8 |
| 93–98 | 15 | +8 |
| 99–104 | 16 | +9 |
| 105–110 | 17 | +10 |
| 111–116 | 18 | +10 |
| 117–122 | 19 | +10 |
| 123–140 | 20 | +10 |
| 141–158 | 21 | +11 |
| 159–176 | 22 | +11 |
| 177–194 | 23 | +11 |
| 195–212 | 24 | +12 |
| 213–230 | 25 | +12 |
| 231–248 | 26 | +12 |
| 249–266 | 27 | +13 |
| 267–284 | 28 | +13 |
| 285–302 | 29 | +13 |
| 303+ | 30 | +14 |

**DPR** = (average of the best attack profile's `DamageExpression`) × `AttacksPerAction`, floored at
0, computed from the *average* of the expression (`count × (sides + 1) / 2 + modifier`), never
rolled. A creature with no attack profiles has DPR 0.

**Adjustment.** For every full 2 points that actual AC exceeds expected AC, step defensive CR up
one rung; for every full 2 points below, step down one. Same rule for attack bonus against expected,
on the offensive side.

**Combination.** Average the two rungs on the CR ladder
`[0, 1/8, 1/4, 1/2, 1, 2, 3, … 30]` by **index**, rounding *down* on a tie. Rounding down is the
anti-inflation default: over a whole campaign, systematically rounding up compounds.

**Validation — this method is checkable against known SRD monsters, and the implementation must
prove it does:**

| Creature | AC | HP | Atk | Damage | Def CR | Off CR | Derived | SRD actual |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Ashen Watcher (sample manifest) | 12 | 9 | +3 | 1d6+1 (4.5) | 1/8 | 1/4 | **1/8 → 25 XP** | Guard is CR 1/8, 25 XP |
| Orc | 13 | 15 | +5 | 1d12+3 (9.5) | 1/8 | 1 → +1 rung = 2 | **1/2 → 100 XP** | CR 1/2, 100 XP |

Both land exactly. Ship the derivation with these two as tests; if a future change moves either,
the economy has moved and someone needs to know.

**CR → XP** is the SRD table, verbatim:

```
0→10   1/8→25   1/4→50   1/2→100   1→200   2→450   3→700   4→1100   5→1800
6→2300   7→2900   8→3900   9→5000   10→5900   11→7200   12→8400   13→10000
14→11500   15→13000   16→15000   17→18000   18→20000   19→22000   20→25000
21→33000   22→41000   23→50000   24→62000   25→75000   26→90000   27→105000
28→120000   29→135000   30→155000
```

**No encounter multiplier.** The SRD's ×1.5/×2 multipliers for creature count are a tool for
*budgeting* an encounter's difficulty when you build it. They are not part of the XP paid out.
Applying them here would inflate every large fight by up to 100%.

---

### F2 — Quest completion

**Trigger.** A quest's status transitions into a completing status —
`completed`, `complete`, `done`, `finished`, `resolved` (case-insensitive) — from any other status.

**Value.** `Quest.RewardExperience` if authored (> 0), treated as a **party total**. Otherwise a
derived default:

```
default party total = round(0.15 × (Threshold[L+1] − Threshold[L])) × N
```

where `L` is the party level (§3.5), `N` the eligible PC count, and at L = 20 the L19→L20 band is
used. So an unauthored quest is worth **15% of a level to each PC**, at every level, forever, with
no per-level hand-tuning and no possibility of drifting out of band as the curve steepens. This is
the construction that keeps imported campaigns — which author no XP at all — economically alive.

**Also, finally, pay the gold.** `Quest.RewardGp` is imported, is shown to the LLM, and is granted
to nobody by any code path in the product today (`docs/game-feel-direction.md` item 10). Quest
completion pays gold to the party at the same moment, divided by the same rule. The product's only
existing stake stops being dead.

**Idempotent** via `Quest.RewardsGranted`. A quest that is completed, reopened, and completed again
pays once. That is the correct call: status is LLM-driven, and status churn must not be a faucet.

---

### F3 — First discovery

**Trigger.** `GameEngine.RevealLocation` transitions a location to discovered for the first time.

**Value.** `5 × partyLevel` XP **to each PC** — not a party total to divide. Exploration is not an
encounter and does not scale with party size.

**Intended contribution: 3–8% of session income.** This faucet exists for *cadence*, not for
magnitude. At level 3 it is 15 XP against a 240 XP kill. It will never be the reason anyone levels,
and that is the point: it is the number that moves during the twenty minutes of a session in which
nothing is being killed, so that stretch stops reading as dead air.

The presentation track should treat it accordingly — a quiet, distinct, non-interrupting treatment,
not the same beat a kill gets. See §7.

**Idempotent** via `WorldLocation.DiscoveryExperienceAwarded`. Re-revealing pays nothing; the engine
already returns `false` for an already-discovered location, and the flag is belt-and-braces for
import, clone, and migration paths that set `Discovered` directly.

---

### F4 — Overcoming without combat

**Trigger.** An encounter whose status is `active` is ended.

**Value.** Every opposition creature still in that encounter and not already paid out awards its
**full F1 value**, once, to the party.

**Why full value and not a fraction.** This is the single most important economic decision in the
document. The campaign manifests carry explicit `alternate_resolutions` — "the watchers can be
bribed", "a successful stealth approach can avoid combat". If those pay less than killing, the
economy has stated, in the only language it has, that the intended-and-authored non-violent solution
is the inferior play. Players read incentives faster than they read prose. Talking pays the same.

**Why it cannot be farmed.** Three independent guards:

1. The encounter must have status `active` at the moment it is ended. A *planned* encounter the
   party has never met is not active, so ending it pays nothing.
2. Each creature carries the same `ExperienceAwarded` flag F1 uses. Kill two of four watchers and
   talk down the rest, and the party is paid for four creatures total, not six.
3. Ending an already-completed encounter finds status `completed`, not `active`, and pays nothing.

---

### 3.5 Party division

**Eligible PC** = `CharacterType == "pc"` and not `Dead`.

A PC at 0 HP, unconscious, stable, or dying **is eligible and takes a full share.** Docking XP from
the character who got dropped is punishing the player for the thing the fight was about. Sink
rejection in this genre starts exactly here.

**Division.** For a party total `T` across `N` eligible PCs:

- Each PC receives `floor(T / N)`.
- The remainder `r = T mod N` is distributed one point at a time to eligible PCs ordered by
  **ascending current XP, then by their order in `campaign.Characters`**. Deterministic, no RNG, and
  it actively pulls the party back together instead of letting rounding drift accumulate over
  hundreds of awards.

**N = 0** (every PC dead): the award is computed, logged as unawarded, and nothing is granted. It
does not queue.

**Party level** = the **lowest** level among eligible PCs, or 1 if there are none. Lowest, not
average or highest: every level-scaled default in this document (F2, F3) is a payout, and scaling a
payout off the strongest party member would over-reward a party carrying a low-level PC.

**N is read at award time**, from the campaign's actual membership. Nothing in the division, the
quest default, or the discovery award refers to a fixed party size.

**What this deliberately does not decide:** what XP a character should start with if they are added
to a campaign already in progress. Storing XP per character means nothing in the schema forbids it,
and a character added mid-campaign starts at the threshold for whatever level they were authored at
— but "a new character should enter at the party's level so their first session is playable" is a
policy question, not a schema one, and it belongs to whoever builds that feature.

### 3.6 Faucets deliberately rejected

Each of these was considered and is excluded. Recording *why* matters more than recording *that* —
these are the ones that will be proposed again.

| Rejected faucet | Why |
| --- | --- |
| XP per point of damage dealt | Turns every fight into a farming target and rewards prolonging combat over winning it. |
| XP per die rolled / per action taken | Rewards clicking. Directly manufactures the grind this design is built to avoid. |
| XP for time passing or for resting | Rewards not playing. `advance_time` is LLM-callable, so this would additionally be an LLM-driven faucet. |
| XP for a successful skill check | Superficially attractive; in practice pays for *attempting* things, and the DC is DM-chosen, which makes the LLM the rate-setter. |
| XP for damage taken / for surviving | Rewards bad play and taking avoidable hits. |
| A `grant_xp` DM tool | Hands the rate of the only faucet-bearing currency to a 4B local model. Non-negotiable: it does not exist. |

---

## 4. The curve and what a level grants

### 4.1 Thresholds — SRD, verbatim

```
L1 0        L2 300      L3 900      L4 2700     L5 6500
L6 14000    L7 23000    L8 34000    L9 48000    L10 64000
L11 85000   L12 100000  L13 120000  L14 140000  L15 165000
L16 195000  L17 225000  L18 265000  L19 305000  L20 355000
```

**Not invented, and not tuned.** This is the one place where fidelity beats design: players of this
genre know these numbers, the product is SRD 5.2.1 throughout, and every published adventure's
pacing assumes them. A custom curve would buy a little pacing control and cost the product's whole
claim to being D&D. Every tuning lever this design needs lives on the faucet side.

### 4.2 Banked level-ups — the key mechanical decision

**Crossing a threshold does not level the character.** It increments
`CharacterSheet.PendingLevelUps` and announces it. The mechanical grant is applied later, by an
explicit engine call that is **illegal while an encounter is active**.

Three reasons, in order of weight:

1. **Mid-combat levelling is an exploit, not a feature.** A level grants max HP, current HP, and a
   Hit Die. If it applied the instant the last enemy in a fight died, the optimal play would be to
   arrange for the threshold to be crossed while hurt, and the level-up becomes a heal. The economy
   should never make "be damaged at the right moment" a strategy.
2. **It creates the anticipation beat.** "You have earned a level. Rest and take it." is a better
   thirty seconds of game than a number silently changing. It also gives the player a reason to
   choose a rest, which the product currently offers and nothing motivates.
3. It matches how the tabletop game is actually run.

`PendingLevelUps` may exceed 1 — one large quest award can cross two thresholds at low level. They
are applied **one at a time**, each producing its own result and its own beat, so a double level-up
is two moments and not one confusing jump.

**Application points:**

- `GameEngine.LongRest` applies every pending level-up for the resting character, automatically.
  This is the default path and it means the loop closes end-to-end even if the presentation track
  ships nothing at all.
- An explicit `ApplyLevelUp(campaign, characterId)` for the UI to call, which throws if the
  character is dead, is in an active encounter, or has nothing pending.

### 4.3 What a level actually grants

The domain has **no class field.** Class-driven progression cannot be computed and must not be
faked. What the engine grants is exactly what it can derive:

| Granted | Rule |
| --- | --- |
| `Level` | +1, capped at 20 |
| `ProficiencyBonus` | Recomputed by `CharacterMechanics.ProficiencyBonusForLevel` — the write-once `Level` at `GameEngine.cs:31` stops being inert |
| `MaxHp` | `+ max(1, HitDieSides/2 + 1 + CON modifier)` — the SRD fixed-average option, **never rolled**. A level-up must not depend on a die the player has to be prompted for. |
| `CurrentHp` | `+` the same amount. Levelling never costs proportional health. |
| `HitDiceMaximum` / `HitDiceRemaining` | `+1` each |

**Explicitly not granted, and why:**

- **Spell slots.** There is no class, so there is no slot progression to apply. Guessing would
  silently hand a fighter ninth-level slots. Left to authored data.
- **Attacks per action, ability score increases, features, subclasses.** Same reason.

This is an honest, deterministic, class-agnostic level-up, and it is a **known and deliberate gap**,
not an oversight. Closing it needs a class model, which is its own track.

---

## 5. Pacing simulation

Modelled before any value ships, per this document's own standard. A **session** is one sitting,
roughly one adventuring day of content for a combat-forward player.

### 5.1 The natural cadence of the SRD curve

Using the SRD adventuring-day XP budget per character against the thresholds:

| Level | Band to next | Day budget/PC | Days per level |
| --- | --- | --- | --- |
| 1 | 300 | 300 | 1.0 |
| 2 | 600 | 600 | 1.0 |
| 3 | 1,800 | 1,200 | 1.5 |
| 4 | 3,800 | 1,700 | 2.2 |
| 5 | 7,500 | 3,500 | 2.1 |
| 10 | 21,000 | 9,000 | 2.3 |
| 15 | 30,000 | 18,000 | 1.7 |

**Result:** levels 1–2 are a single-session tutorial slope by design, then the curve settles at
~1.5–2.3 sessions per level and stays there. That is the target and it comes free — which is the
whole argument for not inventing a curve.

### 5.2 Archetypes

Four player profiles, modelled at level 3 (band to level 4 = 1,800 XP/PC).

Units, derived from the L3 adventuring-day budget of 1,200 XP/PC ≈ 4,800 party XP over ~6
encounters: **one encounter = 800 party XP = 200/PC** (four CR 1 creatures). Unauthored quest at
L3 = 270/PC (§F2). Discovery = 15/PC (§F3).

"Encounters resolved" counts encounters *finished*, by any means — the Talker's are mostly talked
down, and F4 pays those the same. That equivalence is the thing this table exists to check.

| Archetype | Encounters resolved | Enc XP/PC | Quests | Quest XP/PC | Discoveries | Disc XP/PC | Total/session | Sessions to level |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Skirmisher** — fights everything | 5 | 1,000 | 0.3 | 81 | 1 | 15 | 1,096 | 1.64 |
| **Talker** — talks past most of it | 3 | 600 | 1.5 | 405 | 4 | 60 | 1,065 | 1.69 |
| **Explorer** — maps everything | 3 | 600 | 0.5 | 135 | 10 | 150 | 885 | 2.03 |
| **Completionist** — does all of it | 6 | 1,200 | 2 | 540 | 8 | 120 | 1,860 | 0.97 |

**Spread between the slowest and fastest legitimate playstyle: 2.09×.**

**The balance threshold this economy is held to: that spread stays under 2.5×.** If a future change
pushes it past 2.5×, one playstyle has become the correct one and the economy needs a pass. This is
the checkable number; it is this design's faucet/drain ratio.

Three things to read off that table:

- **The Talker is not punished.** 1,065 against the Skirmisher's 1,096 — a 3% gap. That is F4 doing
  its job, and it is the first number to re-check if F4 is ever weakened or made fractional.
- **The Completionist sits at the floor**, just under one session per level. Acceptable: doing
  everything available *should* be the fastest route, and 0.97 is a floor, not a spiral.
- **The Explorer is carried by discovery.** 150 of 885 is 17% — above the 3–8% band F3 targets,
  because ten first-discoveries in one session is a deliberately extreme profile. Typical profiles
  land at 1–6%, in band. It is worth watching: if real play looks like the Explorer, `5 × level` is
  too generous and should drop to `3 × level` rather than fights being made to pay more.

### 5.3 The adversarial profile

| Profile | Behaviour | Outcome |
| --- | --- | --- |
| **Farmer** | Re-kills, re-completes quests, re-reveals, ends encounters repeatedly | **Zero.** Every faucet pays once per subject, per §3.0. |
| **Timeskipper** | `advance_time` in a loop | **Zero.** Time is not a faucet. |
| **Reloader** | Save-scums to re-kill the same creature | **Zero.** The flag is persisted on the sheet, not held in memory. |
| **Threshold-camper** | Holds a level-up until damaged, then rests to heal | Rest already restores all HP. No gain. |

---

## 6. Failure modes and mitigations

| Failure mode | The design smell that predicts it | Mitigation in this design |
| --- | --- | --- |
| **Silent zero economy** | A value source that reads a field nothing populates | Derived CR (§3.1), validated against two known SRD monsters as tests. The 10 XP floor. The self-scaling quest default. |
| **Grinding** | Any faucet keyed to a repeatable action | No per-action faucet exists (§3.6). Every faucet is one-time-per-subject. |
| **Level-up spam** | Instant application at the threshold | Banked level-ups; applied one at a time, out of combat (§4.2). |
| **Sparse cadence / "nothing happened"** | Only one faucet, tied to one activity | Four faucets; F3 exists purely for cadence. |
| **Mid-combat level-up as a heal** | Any HP grant that can fire during a fight | `ApplyLevelUp` throws inside an active encounter. |
| **Murderhobo incentive** | Combat pays and alternatives do not | F4 pays full value (§F4). |
| **Kill-steal / party drift** | Per-killer credit, or integer division without remainder handling | Party-wide awards; remainder to the lowest total (§3.5). |
| **LLM as rate-setter** | Any XP-granting tool | None exists, and adding one is out of contract. The LLM reads XP; it never writes it. |
| **Retroactive XP flood on upgrade** | A migration that backfills by recomputation | The v4→v5 migration grants **nothing** retroactively and marks all pre-existing subjects as settled (§8.2). |
| **Migration eats a save** | Version bump without a round-trip test | An old-version state file must load. This is the test that matters most on this branch. |

---

## 7. What the player sees — the contract with the presentation track

Not implemented in r62. This is what the domain and engine expose, and what the presentation track
is expected to do with it.

### 7.1 The award moment

Every award returns, per character: the amount, the source kind (`defeat` / `quest` / `discovery` /
`resolution`), the source's display name, the new total, the current level, and the XP remaining to
the next threshold.

Enough to render **"+50 XP — Ashen Watcher — 640 / 900"** and a bar that visibly advances. The bar
advancing is the entire point; the number alone is a status line, which is what this product is
trying to stop being.

**It rides the Resolution Beat** (`docs/game-feel-direction.md` item 1). It is not a second,
competing notification channel. The kill and the XP are one beat, because they are one moment.

**Coalescing.** Multiple awards inside one resolution — an area spell that kills three creatures —
must present as **one** beat with a combined total and a source list, not three. Three consecutive
identical cards is the level-up-spam failure mode wearing a different hat.

**Discovery is quieter.** F3 gets a distinct, smaller, non-interrupting treatment. It fires often
and it is small; giving it the kill's treatment would devalue the kill's.

### 7.2 The threshold crossing

Loud, distinct, one-time, and **clearly not the same thing as being levelled.** The player has
earned a level; they do not have it yet. The wording has to carry that or the delay reads as a bug.

This is the moment the design is built around. It should be the loudest thing the application does.

### 7.3 The application moment

Deliberate and player-initiated, or automatic at a Long Rest. It reports exactly what changed —
level, proficiency bonus, max HP delta, hit dice — and it should be honest that spell slots and
class features are not among them (§4.3), rather than leaving the player to discover the omission.

### 7.4 What is exposed for the UI to bind

- `CharacterSheet.ExperiencePoints`, `.PendingLevelUps`, `.Level`
- Static queries: threshold for a level, level for an XP total, XP remaining to the next level,
  fractional progress through the current band
- `ExperienceAward` records returned from every awarding engine call
- `LevelUpResult` from the application call
- Typed `CampaignEvent`s — `experience_awarded`, `level_up_available`, `level_up` — so the whole
  economy is reconstructible from a real save file

### 7.5 Instrumentation

There is no telemetry and there will not be any: this is a local, offline, private application, and
shipping analytics would break a promise the product makes on its own status bar.

The local equivalent is the campaign event log. Typed XP events make a real save file a readable
economy trace: what paid, how much, when, and whether the modelled cadence in §5.2 matches what
actually happened. **That is the artefact a future balance pass reads.** The simulation above is a
model; a played save is the evidence. Prefer adding a faucet to nerfing one — players punish
takebacks harder than they reward gifts, and in a single-player game with no live-ops there is no
recovering a nerf's reputation.

---

## 8. Implementation contract

### 8.1 Domain additions

```
CharacterSheet:
  int  ExperiencePoints          = 0
  int  PendingLevelUps           = 0
  string? ChallengeRating        = null   // authored SRD CR text, e.g. "1/4", "5"
  int? ExperienceValue           = null   // explicit per-creature override, wins over CR
  bool ExperienceAwarded         = false  // F1/F4 idempotency

Quest:
  int  RewardExperience          = 0      // party total; 0 means "use the derived default"
  bool RewardsGranted            = false  // F2 idempotency, covers XP and the gold

WorldLocation:
  bool DiscoveryExperienceAwarded = false // F3 idempotency

records:
  ExperienceAward(CharacterId, CharacterName, Amount, NewTotal, Level, XpToNextLevel,
                  bool CrossedThreshold, string SourceKind, string SourceName)
  LevelUpResult(CharacterId, CharacterName, NewLevel, HitPointsGained, NewMaxHp,
                int ProficiencyBonus, int PendingLevelUpsRemaining, string Summary)
```

### 8.2 Persistence

`AppDataStore.CurrentSchemaVersion` 4 → 5. Migration case 4 → 5:

1. **Seed XP from existing level.** Every character gets
   `ExperiencePoints = Threshold[clamp(Level, 1, 20)]`. Without this, an imported level-5 PC sits at
   0 XP and the next 300-XP award banks a level-up as though they were levelling 1→2. This is the
   whole substance of the migration.
2. **Settle every pre-existing subject.** Dead non-PCs → `ExperienceAwarded = true`. Quests already
   in a completing status → `RewardsGranted = true`. Discovered locations →
   `DiscoveryExperienceAwarded = true`.
3. **Grant nothing.** No XP is paid, no level-up is banked, no gold moves. A migration that pays out
   would hand a long-running campaign a windfall on upgrade — and a migration is not the place to
   overrule the state of a game in progress.

`AddCharacter` seeds `ExperiencePoints` the same way for a character added at a level above 1, so
the invariant "XP is consistent with level at creation" holds on both paths.

**The test that matters most:** a state file written at schema version 4 still loads, arrives at
version 5, and its characters' XP matches their levels. Migration bugs eat player saves.

### 8.3 Engine surface

```
GameEngine:
  IReadOnlyList<ExperienceAward> AwardExperience(campaign, partyTotal, sourceKind, sourceName)
  LevelUpResult ApplyLevelUp(campaign, characterId)      // throws in an active encounter
  int  ExperienceValueOf(CharacterSheet)                 // override → CR → derived, floor 10

Progression (static):
  int ExperienceThresholdForLevel(int level)
  int LevelForExperience(int xp)
  int ExperienceToNextLevel(int xp)
  string DeriveChallengeRating(CharacterSheet)
  int ExperienceForChallengeRating(string cr)
```

Hooks: `MarkDead` (F1), `SetQuestStatus` (F2), `RevealLocation` (F3), `EndEncounter` (F4),
`LongRest` (level-up application).

**Determinism.** No XP path takes a `DiceService`, consults `Random`, or reads a clock. Derived CR
uses *average* damage, never a roll. Given the same save file and the same action, the same XP is
awarded — which is the same contract the rest of this engine already keeps.

### 8.4 The LLM boundary

The DM model gains **no new tools.** Level, XP, and XP-to-next are added to the read-only character
view it already receives, so it can narrate progression truthfully. It cannot grant, adjust, or
withhold XP, and the only faucets it can influence at all — quest status and encounter end — are
one-time-per-subject and gated on state it does not control.
