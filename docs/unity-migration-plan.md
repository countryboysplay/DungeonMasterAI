# Unity Migration Mechanics Plan

**Status:** proposal, r63. Companion to `docs/unity-target-architecture.md` (branch
`docs/r63-unity-target-architecture`, PR #20). That document is the architecture — Unity version,
render pipeline, scene/prefab structure, the assembly-definition boundary, ScriptableObject policy,
map and combat presentation, narration pacing, audio, the WPF rebuild/drop/reshape inventory, the
risk register (§11), owner decisions (§12) and phased sequencing (§13). **This document does not
re-derive any of that** — it is referenced by section number throughout. This document is the
mechanical migration detail the architecture doc deliberately left open: exactly what blocks a
`netstandard2.1` build of `Domain`/`Engine`/`Data`, exactly what `PdfPig` and `System.Text.Json`
mean for that build, where the Unity project lives, the `.csproj`/CI mechanics of multi-targeting,
how to prove the engine behaves identically on both targets, and what happens to the 33 test
projects.

**This is planning only.** No Unity code was written, no migration was performed on this codebase,
and nothing in `windows/` changed as part of producing this document. Section 1's inventory was
verified by actually compiling `Domain`, `Engine` and `Data` for `netstandard2.1` — including the
full proposed remediation — in an isolated scratch copy outside this repository, then discarding it.
That copy is not part of this change; every fact below that says "verified" or "compiler-confirmed"
was checked that way, not inferred from reading source. Base: `main` at `8f21a7f`.

**Scope update from the owner, overriding the architecture doc on one point:** WPF is being dropped
entirely and immediately — `DungeonMasterAI.App` is deleted outright, not kept buildable until the
Unity vertical slice reaches combat. This reverses `docs/unity-target-architecture.md` §12 decision
10, which recommended keeping WPF alive during the port; that recommendation no longer holds and is
not repeated or argued for anywhere below. Two direct consequences, addressed in detail in §7 and
the new §8: test-project handling is now the most time-sensitive part of this plan rather than a
side concern, since deleting `App` deletes six test projects' worth of coverage unless the genuinely
engine-level portions are extracted first; and, stated as a fact of the plan rather than an
objection, **once `App` is deleted, nothing in this repository is playable until the Unity vertical
slice reaches combat** (architecture doc §13, roughly Phase 2).

---

## 1. The `netstandard2.1` incompatibility inventory

### 1.1 Method

Two passes, cross-checked against each other:

1. **Static audit** — `grep`/`ripgrep` across every `.cs` file in `windows/src/DungeonMasterAI.Domain`
   (10 files, 1,749 lines), `windows/src/DungeonMasterAI.Engine` (43 files, 13,115 lines) and
   `windows/src/DungeonMasterAI.Data` (7 files, 2,490 lines) — 17,354 lines total, matching
   `docs/unity-target-architecture.md` §0's counts exactly. Checked for every API and language
   feature named in the task brief plus a broader sweep (generic math, `ref` fields/`ref struct`,
   `Span<T>`/`Memory<T>`/`stackalloc`, `DateOnly`/`TimeOnly`, static abstract members, `unsafe`,
   list patterns, `IAsyncEnumerable`, `PriorityQueue`, `SearchValues`, `Half`, `nint`/`nuint`,
   `TimeProvider`, `System.Threading.Lock`, `Channels`, `FrozenDictionary`/`FrozenSet`,
   `ImmutableArray`, `CollectionsMarshal`, `CallerArgumentExpression`, `StringSyntaxAttribute`,
   the `NotNullWhen`/`MaybeNullWhen`/`DoesNotReturn` family, reflection/`dynamic`/`Activator`).
2. **Compiler verification** — copied the three projects into a scratch directory, retargeted them
   to `netstandard2.1` (the .NET 10 SDK, `10.0.400`, is installed in this environment), and
   iteratively fixed every real compiler error until all three built clean — first individually,
   then as a true `net10.0;netstandard2.1` multi-target producing both DLLs from one build. The
   grep pass and the compiler pass agree exactly on every blocker below; the compiler pass also
   surfaced two blockers grep would not have caught (§1.4 items E and F).

This matters because the grep pass alone is not trustworthy here — see §1.2.

### 1.2 Corrections to `docs/unity-target-architecture.md` §3.1

Two of the architecture doc's specific claims do not hold up under verification. Both are reported
straight, because the task asked for exhaustiveness over comfort:

**"~90 `ArgumentNullException.ThrowIfNull` call sites" is an undercount.** The real count in
`Domain`+`Engine` is **139**, and `Data` adds **6** more that the doc's §3.1 paragraph does not
mention at all (it only discusses `Data`'s PdfPig dependency). Total: **145**. See §1.4.A.

**"~60 `required` member uses, heaviest in `DmToolRouter.cs` (19)" is not real.** Grepping the bare
word `required` does find ~70 hits in these three projects, and the majority are in `DmToolRouter.cs`
— but every one of them is inside a DM-tool description string literal ("a required player d20
roll"), an XML doc comment, or one JSON-schema-builder property literally named `required`
(`DmToolRouter.cs:632`, `required = fields.Where(f => f.Required)...`). **There are zero actual uses
of the C# `required` member modifier anywhere in `Domain`, `Engine` or `Data`.** Confirmed two ways:
a regex restricted to real `required TypeName Name {` declaration syntax matches nothing, and — more
conclusively — the netstandard2.1 compiler build never once raised `CS0246`/`CS0656` for
`RequiredMemberAttribute`, `CompilerFeatureRequiredAttribute` or `SetsRequiredMembersAttribute`, even
before those types were shimmed. **Nothing needs to be done about `required` members**, because there
are none. What genuinely needs a shim is `IsExternalInit` — but that is because of `record` types,
not `required` (see §1.4.B). This appears to be the source of the architecture doc's error: it is
easy to conflate "codebase full of `record`s needs `IsExternalInit`" with "codebase uses `required`
members," and the two verification paths (compile-time proof vs. word-frequency grep) diverge exactly
here.

### 1.3 Inventory summary

| # | Category | Real count | Files affected | Fix shape | Verified |
|---|---|---|---|---|---|
| A | `ArgumentNullException.ThrowIfNull` | **145** (139 Domain+Engine, 6 Data) | 36 files | Mechanical rewrite, **one implementation serves both TFMs** | Compiled clean |
| B | `record`/`record struct` → `IsExternalInit` | 37 records + 2 record structs, **0** `required` members | 3 assemblies (one shim type each) | `#if NETSTANDARD2_1` internal marker type | Compiled clean |
| C | `StringSplitOptions.TrimEntries` | **3** (not 2) | `Spellcasting.cs` ×2, `CampaignRehearsalService.cs` ×1 | Mechanical rewrite, one implementation serves both TFMs | Compiled clean |
| D | `Random.Shared` | 1 | `DiceService.cs:10` | `#if`-forked implementation | Compiled clean |
| E | `[GeneratedRegex]` source generator | 2 | `DiceService.cs:19,22` | `#if`-forked implementation (**shim attribute does not work — see below**) | Compiled clean |
| F | `StreamReader.ReadLineAsync(CancellationToken)` | 1 | `RulesSearchService.cs:23` | `#if`-forked (drop the token under ns2.1) | Compiled clean |
| G | `System.Text.Json` package presence | n/a — 21 files use it | Needs an explicit `PackageReference` on the netstandard2.1 leg only | See §3 | Compiled clean |
| H | `PdfPig` (Data only) | 3 sites | `CampaignImportService.cs` | Excluded from the netstandard2.1 leg | See §2, compiled clean |

Everything else checked (§1.1's list) is a **verified non-blocker** — §1.5.

### 1.4 Blocker detail

**A. `ArgumentNullException.ThrowIfNull` — 145 sites.**

Per-file breakdown (Domain + Engine, all confirmed by both grep and a clean-then-broken-then-fixed
compiler cycle):

| File | Sites | File | Sites |
|---|---|---|---|
| `Engine/Spellcasting.cs` | 18 | `Engine/GameEngine.SpellPlayerRolls.cs` | 4 |
| `Engine/GameEngine.cs` | 15 | `Engine/GameEngine.ProjectilePlayerRolls.cs` | 4 |
| `Engine/GameEngine.PlayerRolls.cs` | 10 | `Engine/GameEngine.InitiativeRolls.cs` | 4 |
| `Engine/GameEngine.AreaSpellPlayerRolls.cs` | 10 | `Engine/GameEngine.DeathSaves.cs` | 4 |
| `Engine/GameEngine.UnarmedPlayerRolls.cs` | 8 | `Engine/StealthReady.cs` | 3 |
| `Engine/GameEngine.Progression.cs` | 8 | `Engine/GameEngine.PlayerDecisions.cs` | 3 |
| `Engine/GameEngine.StealthAidPlayerRolls.cs` | 6 | `Engine/BattlefieldEffects.cs` | 3 |
| `Engine/CharacterMechanics.cs` | 6 | `Engine/TacticalMapGeometry.cs` | 2 |
| `Engine/GameEngine.ReadiedAttackPlayerRolls.cs` | 5 | `Engine/PersistentAreaSpellcasting.cs` | 2 |
| `Engine/GameEngine.OpportunityAttackPlayerRolls.cs` | 5 | `Engine/GameEngine.SpellSavePlayerRolls.cs` | 2 |
| `Engine/GameEngine.SpellSaveDamageRolls.cs` | 2 | `Engine/GameEngine.ConcentrationRolls.cs` | 2 |
| `Engine/GameEngine.CombatSkillPlayerRolls.cs` | 2 | `Engine/GameEngine.AutoProjectilePlayerRolls.cs` | 2 |
| `Engine/GameEngine.TacticalMapCombat.cs` | 1 | `Engine/GameEngine.ReadiedSpellDecisions.cs` | 1 |
| `Engine/GameEngine.ReadiedMoveDecisions.cs` | 1 | `Engine/GameEngine.ReadiedAttackDecisions.cs` | 1 |
| `Domain/TacticalMapSchema.cs` | 2 | `Domain/Progression.cs` | 2 |
| `Domain/CombatSide.cs` | 1 | | |

Plus `Data`, one site each in `AppDataStore.cs`, `CampaignCloneService.cs`,
`CampaignExpansionApplyService.cs`, `CampaignReadinessValidator.cs`, `CampaignRehearsalService.cs`,
`SrdSpellCatalogService.cs` (6 total).

**You cannot polyfill a static method onto an existing sealed BCL type** — `ArgumentNullException`
already exists in netstandard2.1 (just without `ThrowIfNull`), and C# extension methods cannot add
static members to a type you don't own. So this is a real, mechanical source rewrite, not a shim.

**The rewrite is safer than it looks.** Every one of the 145 call sites was checked for its argument
shape: **100% are the plain single-identifier form** `ArgumentNullException.ThrowIfNull(x);` — zero
use a member-access expression (`ThrowIfNull(foo.Bar)`), and zero pass an explicit second
`paramName` argument. This matters because `ThrowIfNull`'s real signature captures the *source
expression text* via `[CallerArgumentExpression]`, so `nameof(x)` is only a faithful substitute when
the argument is a bare identifier — which, verified, it always is here. The rewrite is:

```csharp
// One file per assembly, e.g. windows/src/DungeonMasterAI.Engine/Netstandard21Compat.cs
// NOT TFM-gated — this replaces ThrowIfNull uniformly on both net10.0 and netstandard2.1, so
// there is no behavioral fork for this blocker and nothing to keep in sync between targets.
namespace DungeonMasterAI.Engine
{
    internal static class Guard
    {
        public static T NotNull<T>(T? value, string paramName) where T : class
            => value ?? throw new ArgumentNullException(paramName);
    }
}
```

and a mechanical find/replace at every call site:

```
ArgumentNullException.ThrowIfNull(x);   →   Guard.NotNull(x, nameof(x));
```

This can be done with a single-line `sed`/regex pass per project (verified: `sed -E
's/ArgumentNullException\.ThrowIfNull\(([a-zA-Z0-9_]+)\);/Guard.NotNull(\1, nameof(\1));/g'` handles
every real call site in this codebase because the argument shape is uniform), but it should still be
reviewed file-by-file rather than trusted blind, and the full `net10.0` test suite (§11.3 of the
architecture doc makes the same point) must stay green throughout — a `Guard.NotNull` call that
silently changed a parameter name would still throw `ArgumentNullException`, just with a different
`ParamName`, which is exactly the kind of "compiles clean, behaves differently" defect this whole
document is trying to design against. **Because `Guard` replaces `ThrowIfNull` on both targets
identically, this blocker cannot itself cause net10.0/netstandard2.1 behavioral drift** — it is a
one-time rewrite, not a permanently-forked implementation.

**B. `record`/`record struct` → `IsExternalInit`, and the corrected `required`-member story.**

37 `record` declarations (25 in `Domain/Models.cs` alone) and 2 `record struct` declarations exist
across the three projects; zero explicit `init` accessor syntax was found anywhere (all init-only
surface comes from record positional-parameter auto-properties, which the compiler still lowers
through `init` and therefore still needs `System.Runtime.CompilerServices.IsExternalInit` to exist).
Confirmed by compiler: a raw netstandard2.1 build without any shim produces exactly 198 `CS0518`
errors ("Predefined type 'System.Runtime.CompilerServices.IsExternalInit' is not defined or
imported"), all in `Domain` alone, before any other fix is applied. The fix, one internal type per
assembly (three total, since `internal` types don't cross assembly boundaries):

```csharp
#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
```

This is `#if`-gated (unlike `Guard` above) because `net10.0` already defines the real
`IsExternalInit` in its runtime, and a second definition of the same fully-qualified name would
collide. As §1.2 established, `RequiredMemberAttribute`/`CompilerFeatureRequiredAttribute`/
`SetsRequiredMembersAttribute` do **not** need shims — there is no `required` member anywhere to
support.

**C. `StringSplitOptions.TrimEntries` — 3 sites, not 2.**

`Engine/Spellcasting.cs:1250` and `:1487` (matching the architecture doc), plus
`Data/CampaignRehearsalService.cs:374` (`Normalize`, which the doc's §3.1 paragraph does not
mention). All three follow the shape `x.Split(sep, StringSplitOptions.RemoveEmptyEntries |
StringSplitOptions.TrimEntries)`. `TrimEntries` is .NET 5+; netstandard2.1 lacks it. Fix — one
implementation for both targets, no `#if`:

```csharp
// before
text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
// after — identical output on both targets: empty-after-trim entries are still dropped
text.Split(' ').Select(s => s.Trim()).Where(s => s.Length > 0)
```

Verified equivalent and compiled clean at all three sites (the third, `Split((char[]?)null, ...)`
in `CampaignRehearsalService.cs`, needs the same treatment against `StringSplitOptions
.RemoveEmptyEntries` alone, then `.Select(...).Where(...)`).

**D. `Random.Shared` — one site, and it is on a live (if narrow) engine code path, not just in
tests.** `Engine/DiceService.cs:10`:

```csharp
public DiceService() : this((minimumInclusive, maximumExclusive) =>
    Random.Shared.Next(minimumInclusive, maximumExclusive))
```

`Random.Shared` is .NET 6+; absent from netstandard2.1 (confirmed: `CS0117 'Random' does not contain
a definition for 'Shared'` in an isolated repro). Not caught by the architecture doc — worth flagging
because it sits in the exact type (`DiceService`) that §1.1 of the architecture doc calls "the only
injection seam in the engine." Fix, `#if`-forked because there is no netstandard2.1 equivalent of
`Random.Shared`'s thread-safe singleton semantics:

```csharp
#if NETSTANDARD2_1
[ThreadStatic] private static Random? _threadRandom;
private static Random ThreadRandom => _threadRandom ??= new Random();
public DiceService() : this((min, max) => ThreadRandom.Next(min, max)) { }
#else
public DiceService() : this((min, max) => Random.Shared.Next(min, max)) { }
#endif
```

**Worth noting for §6 (verification):** the parameterless `DiceService()` constructor is not purely
test-only. `GameEngine` itself constructs `new DiceService()` directly at five call sites —
`GameEngine.cs:771` (battlefield effects at turn start), `:886` (`dice ??= new DiceService();`, a
fallback when a caller passes no dice service), `:1484` (inside `CommitCombatMove`),
`GameEngine.PlayerDecisions.cs:150` (readied spell reaction fallback), and `StealthReady.cs:279`.
These are genuinely unseeded on both targets today (neither `Random.Shared` nor `new Random()` is
reproducible), so the netstandard2.1 rewrite does not make anything *more* nondeterministic than it
already is — but any golden-file or differential test (§6) that exercises turn-advance, a combat
move, a readied spell reaction, or stealth/ready mechanics must either avoid triggering these five
paths or tolerate them as a known, pre-existing source of non-determinism unrelated to the port.

**E. `[GeneratedRegex]` — 2 sites, and the obvious shim does not work.** `Engine/DiceService.cs:19`
and `:22`:

```csharp
[GeneratedRegex(@"^\s*(?<count>\d*)d(?<sides>\d+)(?<modifier>[+-]\d+)?\s*$", RegexOptions.IgnoreCase)]
private static partial Regex DiceExpressionRegex();
```

`GeneratedRegexAttribute` (.NET 7+) does not exist in netstandard2.1's reference surface, and — this
is the part worth writing down because it is not obvious — **a hand-rolled polyfill attribute of the
same name does not make the in-box regex source generator fire under netstandard2.1.** Tested
directly: declaring an `internal sealed class GeneratedRegexAttribute` shim resolves the symbol
(the `CS0246` "type not found" error goes away) but the partial method is still left without a body,
producing `CS8795` ("Partial method must have an implementation part because it has accessibility
modifiers"). The generator's activation is evidently gated on more than symbol resolution. The safe
fix, verified to compile, is two real implementations rather than a shimmed attribute:

```csharp
#if NETSTANDARD2_1
    private static readonly Regex DiceExpressionRegexInstance =
        new(@"^\s*(?<count>\d*)d(?<sides>\d+)(?<modifier>[+-]\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex DiceExpressionRegex() => DiceExpressionRegexInstance;
    // ...FixedExpressionRegex follows the same shape
#else
    [GeneratedRegex(@"^\s*(?<count>\d*)d(?<sides>\d+)(?<modifier>[+-]\d+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiceExpressionRegex();
#endif
```

`RegexOptions.Compiled` under netstandard2.1 approximates the source generator's compile-time
codegen at runtime instead; behaviorally equivalent regex matching either way (same pattern, same
options minus the generator mechanism itself). This is the one blocker in this inventory with a
real, if small, engineering cost: two hand-maintained implementations of the same two patterns,
which is a place a future edit to the regex could update one branch and not the other. Worth a
comment pointing each implementation at the other.

**F. `StreamReader.ReadLineAsync(CancellationToken)` — one site, found only by compiling.**
`Engine/RulesSearchService.cs:23`:

```csharp
while (await reader.ReadLineAsync(cancellationToken) is { } line)
```

The single-argument `ReadLineAsync(CancellationToken)` overload is .NET 7+; netstandard2.1 has only
the parameterless `ReadLineAsync()`. This was not visible to the grep pass (nothing in the method
name signals a version gate) and only surfaced as `CS1501` during the compiler pass — direct evidence
for why §6 recommends actually compiling both targets rather than trusting a checklist. Fix,
`#if`-forked (netstandard2.1 loses the cooperative-cancellation check on this one read call, which is
loading a small bundled `srd_chunks.jsonl` file, not a concern in practice):

```csharp
#if NETSTANDARD2_1
while (await reader.ReadLineAsync() is { } line)
#else
while (await reader.ReadLineAsync(cancellationToken) is { } line)
#endif
```

**G. `System.Text.Json` presence.** Used across **21 files**, not "at least seven" as the
architecture doc's §2.2 says (that count was scoped to `GameEngine.*` files specifically and is
correct for that scope): `Domain/CaseInsensitiveMap.cs` (a doc-comment mention only, not real usage),
`Domain/TacticalMaps.cs`, six `DmToolRouter.*.cs` files, five `GameEngine.*PlayerRolls.cs`/
`ReadiedSpellDecisions.cs` files, `Engine/RulesSearchService.cs`, and five `Data/*.cs` files
including `AppDataStore.cs` (the entire save-file format). All reflection-based — no
`JsonSerializerContext` source-generated contract exists anywhere, confirmed by grep. This is not a
code blocker by itself (the types exist once the package is referenced) but it is the reason
`netstandard2.1` needs an explicit `PackageReference` the `net10.0` leg does not (§5), and it is the
subject of §3.

**H. `PdfPig`.** See §2 — the fix is exclusion, not remediation, and it only touches `Data`.

### 1.5 Verified non-blockers

Confirmed present in the codebase and confirmed **not** a problem for netstandard2.1, either because
the feature is compile-time-only (works under any target once `LangVersion` allows the syntax) or
because the API already exists in netstandard2.1's surface:

- **Collection expressions** (`= []`, `[.. spread]`) — 111 + 15 = **126 sites**. C# 12 syntax sugar
  lowering to `List<T>`/arrays; no BCL surface requirement beyond what already exists.
- **Primary constructors** — 1 site (`DmToolRouter.cs:9`). Compile-time sugar.
- **`record struct`/`readonly record struct`** — 2 sites. Same `IsExternalInit` story as `record`,
  no separate issue.
- **File-scoped namespaces** — used throughout. Compile-time only.
- **Nullable reference types** (`Nullable enable` from `Directory.Build.props`) — fine on both
  targets. Roslyn auto-embeds `NullableAttribute`/`NullableContextAttribute` into the compiling
  assembly whenever the target framework's own BCL doesn't already provide them (netstandard2.1
  qualifies), so no manual polyfill is needed — and confirmed there is no use of the
  `NotNullWhen`/`MaybeNullWhen`/`DoesNotReturn`/`MemberNotNull`/`NotNullIfNotNull` attribute family,
  which *would* have needed one.
- **`Math.Clamp`** — native to netstandard2.1 (added alongside `Span<T>` in the 2.1 spec).
- **Switch expressions and pattern matching** used throughout — no list patterns, no generic math,
  no static abstract interface members, no `ref struct`/`ref` fields, no `unsafe`/function pointers.
- **No `Span<T>`/`ReadOnlySpan<T>`/`Memory<T>`/`stackalloc`**, no `DateOnly`/`TimeOnly`, no
  `PriorityQueue<,>`/`SearchValues`/`Half`/`nint`/`nuint`/`TimeProvider`/
  `System.Threading.Lock`/`System.Threading.Channels`, no `FrozenDictionary`/`FrozenSet`/
  `ImmutableArray`/`CollectionsMarshal`, no `CallerArgumentExpression`/`StringSyntaxAttribute`.
- **No reflection, no `dynamic`, no `Activator`, no `HttpClient`/`System.Net`** anywhere in `Domain`,
  `Engine` or `Data` — the architecture doc's §1.1/§3.1 claim for `Domain`/`Engine` verified and
  extended to cover `Data` as well.
- **No `System.Windows`/`System.Drawing`/`PresentationCore`/`WindowsBase`** references in any of the
  three projects — confirmed directly, not just inferred from the architecture doc.

### 1.6 What was actually compiled to verify this

All three projects — with every fix in §1.4 applied — were retargeted to `netstandard2.1` in an
isolated scratch copy and built with the .NET 10 SDK (`10.0.400`), individually and then as a true
`<TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>` multi-target producing both DLLs from
one `dotnet build`. Final result: **`Build succeeded. 0 Warning(s). 0 Error(s).`** for all three
projects on both targets. A `net10.0` console harness was then built against the *netstandard2.1*
output specifically (via `ProjectReference`'s `SetTargetFramework` metadata — see §6) and, at
runtime, printed the loaded assembly's `TargetFrameworkAttribute` as
`.NETStandard,Version=v2.1` and produced a correct `DiceService.Roll("2d6+3")` result — i.e., this is
not just "the compiler accepted it," it is "a .NET host loaded and executed the netstandard2.1
build and got a right answer." None of this scratch work is part of this change; it existed only to
produce the numbers above and was discarded.

---

## 2. `PdfPig`

**Confirmed via its own `.nuspec`** (fetched from the local NuGet cache, version 0.1.15, matching
`windows/src/DungeonMasterAI.Data/DungeonMasterAI.Data.csproj`): `PdfPig` ships `lib/` folders for
`net462`, `net471`, `net6.0`, `net8.0` and **`netstandard2.0`** — there is no netstandard2.1-specific
asset group, but none is needed: netstandard2.1 is a strict superset of netstandard2.0, so any
netstandard2.0 assembly is directly consumable from a netstandard2.1 project. **This resolves the
architecture doc's §3.1 hedge ("PdfPig targets netstandard2.0 and would probably load") to a
confirmed yes — it would load.**

That is not, however, a reason to bring it into Unity. Two reasons not to:

1. **It has its own dependency chain that adds to exactly the packaging-collision risk §3 is about.**
   Under its netstandard2.0 dependency group, PdfPig pulls `Microsoft.Bcl.HashCode 6.0.0` and
   `System.Memory 4.6.0` — two more third-party DLLs that would need to land in
   `Assets/Plugins/DungeonMasterAI/Third-Party/` (or be folded into the same ILRepack merge as
   `System.Text.Json`, compounding that merge's surface area) for a capability — parsing PDF
   rulebooks — the running game never needs.
2. **`CampaignImportService.cs`'s PdfPig dependency is small, precise, and easy to fence off.** Of
   the file's 724 lines, PdfPig touches exactly: two `using` statements (`UglyToad.PdfPig`,
   `UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor`, lines 6–7), one 5-line private method
   (`ExtractPdf`, lines 604–608, `PdfDocument.Open(path)` → `ContentOrderTextExtractor.GetText`),
   and one `.pdf` arm each in the `ExtractSourceAsync` (line 24) and `ImportAsync` (line 40) switch
   expressions. **`ImportManifestJson`, `CompileText`, and the `.docx` path (`ExtractDocx`, using
   only `System.IO.Compression` and `System.Xml.Linq`, both native to netstandard2.1) are pure and
   portable already.**

**Recommendation, confirming and sharpening `docs/unity-target-architecture.md` §9.7: `Data` *does*
need to go to Unity** — `AppDataStore` (save/load, schema v5), `CampaignCloneService` (the
transaction-clone mechanism §3.4.1 of the architecture doc builds `EngineSession` commands on),
`CampaignReadinessValidator`, `CampaignRehearsalService` and `SrdSpellCatalogService` are all runtime
concerns, not import-time ones. Only `CampaignImportService`'s PDF path is excluded from the
netstandard2.1 leg:

```csharp
using DungeonMasterAI.Domain;
#if !NETSTANDARD2_1
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
#endif
...
#if !NETSTANDARD2_1
            ".pdf" => ExtractPdf(path),
#else
            ".pdf" => throw new NotSupportedException(
                "PDF campaign import is not available in this build. Convert the source with the campaign-pdf-import CLI tool first."),
#endif
...
#if !NETSTANDARD2_1
    private static string ExtractPdf(string path) { /* unchanged */ }
#endif
```

paired with the `.csproj` scoping the package reference the same way (§5). Verified: `Data` builds
clean under netstandard2.1 with `PdfPig` entirely absent from that leg's dependency graph, and
unchanged under `net10.0` with `PdfPig` present exactly as today.

**Where PDF import lives after this:** as `docs/unity-target-architecture.md` §9.7 recommends, a
small `net10.0`-only CLI tool (e.g. `windows/tools/CampaignPdfImport`, referencing `Data`'s
`net10.0` leg normally) that calls `ExtractSourceAsync`/`ImportAsync` exactly as today and writes out
a campaign manifest. PDF import is a once-per-campaign authoring action; it does not need to run
inside the game, and keeping it out of the netstandard2.1 leg means Unity's plugin folder never has
to reason about PdfPig's dependency chain at all. This CLI tool is new build surface, not existing
code moved — flag it as a small, separate implementation task, not something the multi-target change
itself produces for free.

---

## 3. `System.Text.Json` in Unity — the dependency chain, options, and what the spike must prove

`docs/unity-target-architecture.md` §11.1 names this the highest risk of the whole port. This section
gives the concrete dependency graph and a precise spike checklist.

### 3.1 The actual dependency chain

Resolved from `System.Text.Json`'s own `.nuspec` (version 8.0.5, fetched from the local NuGet cache
as a representative recent version — **the exact version and transitive graph must be re-resolved
and pinned at implementation time**, this is illustrative, not a promise of what `dotnet restore`
will pick then). Under its `.NETStandard2.0` dependency group (netstandard2.1 falls back to this
group; there is no netstandard2.1-specific group):

```
System.Text.Json (8.0.5)
├── Microsoft.Bcl.AsyncInterfaces (8.0.0)
├── System.Text.Encodings.Web (8.0.0)
├── System.Buffers (4.5.1)
├── System.Memory (4.5.5)
├── System.Runtime.CompilerServices.Unsafe (6.0.0)   ← the one the architecture doc names as highest-risk
└── System.Threading.Tasks.Extensions (4.5.4)
```

**Seven assemblies total** (including `System.Text.Json.dll` itself) would need to reach
`Assets/Plugins/DungeonMasterAI/` if referenced as separate DLLs. This is a fuller picture than the
architecture doc's "several — `System.Runtime.CompilerServices.Unsafe` above all" — worth having the
exact list before the spike so the ILRepack invocation (or the NuGetForUnity dependency list) has a
concrete checklist rather than an open-ended "and whatever else comes along."

**I could not verify** whether Unity 6.3's Mono scripting backend ships its own copy of any of these
— specifically `System.Runtime.CompilerServices.Unsafe`, which Unity's own Burst/Collections
packages are known to depend on in some versions. Unity is not installed in this environment (nor,
per the architecture doc, in the development environment this plan targets). This is exactly the
fact the day-one spike (§3.3) must establish before anything else.

### 3.2 Remediation options, in order of preference

1. **ILRepack/ILMerge the netstandard2.1 build of `System.Text.Json` and its six dependencies into
   `DungeonMasterAI.Engine.dll`, with the dependency types internalized.** One assembly crosses into
   Unity; no separate `System.Text.Json.dll`/`System.Runtime.CompilerServices.Unsafe.dll`/etc.
   identity exists for Unity's own copies (if any) to collide with. Cost: a merge step in
   `tools/build-engine-for-unity.ps1` (§3.2 of the architecture doc already names this script) using
   ILRepack (actively maintained, .NET-Core-build-compatible; ILMerge is the older, less
   .NET-Core-friendly alternative — prefer ILRepack). **Recommended**, matching the architecture
   doc's own preference, but explicitly **unproven against this specific dependency graph** — that
   is what the spike is for.
2. **NuGetForUnity**, resolving the same seven-package graph into `Assets/Packages`. Faster to wire
   up, but leaves seven separate assembly identities in the Unity project, each a candidate for a
   future Unity upgrade to collide with. Acceptable as a day-one fallback if option 1's ILRepack step
   proves harder to get working than the time-box below allows, with an explicit note to revisit.
3. **Replace `System.Text.Json` in the engine.** Rejected, per the architecture doc: it would touch
   21 files (§1.4.G) and the entire save-file format (`AppDataStore`, schema v5) to solve a Unity
   packaging problem, inverting the rule in §3 of the architecture doc that the view never dictates
   engine implementation.

### 3.3 What the day-one spike must prove, concretely

The architecture doc says to spike this before any scene work; here is the specific checklist,
because "spike it" alone is not verifiable:

1. Create an empty Unity 6.3 LTS project, Windows Build Support (Mono), no other modules.
2. Run `tools/build-engine-for-unity.ps1` (or its equivalent by hand) to produce the netstandard2.1
   `Domain.dll`/`Engine.dll`/`Data.dll`, then ILRepack `Engine.dll` with the seven-assembly
   `System.Text.Json` graph from §3.1, internalizing every type except `DungeonMasterAI.Engine`'s own
   public surface. Drop the merged output plus `Domain.dll`/`Data.dll` into
   `Assets/Plugins/DungeonMasterAI/`.
3. **Before writing a single MonoBehaviour**, check the Unity Console for duplicate-assembly or
   version-mismatch warnings on project open. This answers §3.1's open question about what Unity 6.3
   already ships.
4. Write one `MonoBehaviour` in `DMAI.Session` (or a throwaway test scene) that: constructs a
   `GameEngine` + `DiceService`, round-trips a small `CampaignState` through `AppDataStore`'s actual
   save/load path (exercising `JsonSerializer.SerializeAsync`/`DeserializeAsync` with the real
   `JsonSerializerOptions(JsonSerializerDefaults.Web)` the codebase uses — not a toy JSON call), and
   calls `DmToolRouter.ToOpenAiToolSchema()` (a nontrivial reflection-driven serialization of a real
   object graph, since no `JsonSerializerContext` source-generated path exists anywhere in this
   codebase per §1.4.G — the reflection path is the only path, so the spike must exercise it, not a
   simplified stand-in).
5. Run step 4 in the **Editor Play Mode** and separately in an actual **built (non-Editor) Windows
   Mono player**. The risk named in the architecture doc's §11.1 is specifically an
   editor-works/build-fails split (stripping and reflection interact differently between the two,
   even under Mono, which is less aggressive than IL2CPP but not exempt), so both must be checked,
   not just the faster one.
6. Confirm zero `MissingMethodException`/`TypeLoadException`/`FileLoadException` in either mode.
7. Confirm the asmdef fence still holds with the merged DLL present — `DMAI.Presentation` still
   cannot resolve `GameEngine` (architecture doc §3.3) after the merge changes what's on disk.
8. **Time-box it.** If a clean ILRepack merge is not working within roughly one to two days, fall
   back to NuGetForUnity (option 2) for day one and file the ILRepack path as a follow-up rather than
   letting this block all subsequent phases — the architecture doc's own sequencing (§13, Phase 0)
   treats this spike as the thing everything else is conditioned on, so it should fail fast and
   loud, not become an open-ended rabbit hole.

---

## 4. Repository structure

### 4.1 Current layout, verified

The repository has **no `.sln` file anywhere** — confirmed by search. CI (`.github/workflows/
windows-ci.yml`) builds and runs individual `.csproj` files directly via `dotnet build`/`dotnet run`,
not a solution. The whole .NET codebase lives under one directory, `windows/`, itself containing
`src/` (five projects: `Domain`, `Engine`, `Data`, `AI`, `App`), `tests/` (33 projects), `tools/`,
`installer/`, and its own `Directory.Build.props`. The repo root otherwise holds `docs/`,
`reference-python/`, `Sample Campaigns/`, and top-level README-style files.

### 4.2 Recommendation: the Unity project lives in this repo, as a sibling of `windows/`

```
DungeonMasterAI-main/
  docs/
  reference-python/
  windows/                        # unchanged — the .NET solution
  unity/                          # new — Unity 6.3 LTS project
    Assets/
      Plugins/DungeonMasterAI/    # built netstandard2.1 DLLs + .xml + .pdb — generated, gitignored
      Scripts/
        Session/                  # DMAI.Session.asmdef
        Presentation/             # DMAI.Presentation.asmdef
        Editor/                   # DMAI.Editor.asmdef
    Packages/
    ProjectSettings/
    .gitignore                    # Unity-specific: Library/, Temp/, obj/, Logs/, UserSettings/, Build/
  .github/workflows/
    windows-ci.yml                 # existing — gets a netstandard2.1 build job (§5.3)
```

**Not a separate repo, and not nested inside `windows/`.** Reasoning:

- **The engine is the single source of truth and the whole thesis of the port** (architecture doc
  §0: "the engine is already the game"). A separate repo means every `Domain`/`Engine`/`Data` change
  that Unity code needs to react to becomes a two-repo, two-PR, package-publish-and-consume
  workflow — real friction for a project the owner has explicitly framed as solo/small-scale craft
  work, not something built for cross-team API stability.
- **`tools/build-engine-for-unity.ps1`** (architecture doc §3.2) needs to copy build output from
  `windows/src/*/bin/...` into `Assets/Plugins/DungeonMasterAI/`. Within one repo this is a relative
  path copy; across repos it becomes an artifact-download step with its own versioning and staleness
  questions — precisely the "hand-copied DLL will go stale" failure mode the architecture doc already
  warns against, just moved one layer up.
- **CI already lives at `.github/workflows/windows-ci.yml`** in this repo. Adding a netstandard2.1
  build job (§5.3) to prove the multi-target stays green is a same-repo, same-PR change; a
  cross-repo setup would need its own triggering and secret-sharing story for no benefit given the
  project has explicitly deferred distribution and multiplayer.
- **Docs already live at repo-root `docs/`** — this very document and its companion sit there,
  alongside `docs/game-feel-direction.md`, `docs/audio-direction.md`, etc. Splitting the code that
  implements those docs across two repos while the docs describing both halves stay in one is an
  avoidable seam.
- **Git history stays atomic.** An engine bug fix and the Unity-side code that reacts to it (a new
  field on a result record consumed immediately by a beat builder, per architecture doc §1.2/§10)
  land in one commit, bisectable together.

**Costs, named honestly:**
- Repo size grows over time with Unity `Assets/` art content. Standard mitigation: a Unity-specific
  `.gitignore` (`Library/`, `Temp/`, `Obj/`, `Logs/`, `UserSettings/`, `*.csproj`/`*.sln` that Unity
  auto-generates for script editing) keeps generated cruft out; if imported art assets get large,
  Git LFS is the standard next step — not needed on day one, worth revisiting once the map/portrait
  asset packs (architecture doc §5.3, §9.6) are actually being imported at volume.
- Opening `unity/` in the Unity Editor auto-generates its own `.csproj`/`.sln` for C# script editing,
  fully separate from and not to be confused with the hand-written `windows/*.csproj` files — worth a
  one-line note in `unity/README.md` so a future contributor doesn't wonder why there appear to be two
  unrelated sets of project files.
- **Built DLLs in `Assets/Plugins/` should be gitignored, not committed**, even though this means the
  Unity project will show missing-reference errors on first open after a fresh clone until
  `tools/build-engine-for-unity.ps1` runs. The alternative — committing built binaries — reintroduces
  exactly the staleness risk the architecture doc's §3.2 already flags ("a hand-copied DLL will go
  stale and produce an afternoon of debugging a bug that was fixed a week ago"), just via git instead
  of via a human forgetting to copy. Document the required first step prominently in `unity/README.md`
  and, if a CI job ever touches `unity/`, have it run the build script first every time rather than
  trusting a checked-in artifact.

---

## 5. Multi-targeting mechanics

### 5.1 `.csproj` changes, verified by compiling

`Domain`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>
  </PropertyGroup>
</Project>
```

`Engine`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../DungeonMasterAI.Domain/DungeonMasterAI.Domain.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'netstandard2.1'">
    <PackageReference Include="System.Text.Json" Version="8.0.5" />
  </ItemGroup>
</Project>
```

(`net10.0` needs no explicit `System.Text.Json` reference — it's part of the shared framework
already, which is why this `PackageReference` is conditioned to the netstandard2.1 leg only. Pin the
version deliberately at implementation time rather than floating it — see §3.1's caveat.)

`Data`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../DungeonMasterAI.Domain/DungeonMasterAI.Domain.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'netstandard2.1'">
    <PackageReference Include="System.Text.Json" Version="8.0.5" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' != 'netstandard2.1'">
    <PackageReference Include="PdfPig" Version="0.1.15" />
  </ItemGroup>
</Project>
```

**Verified: this exact shape, with all of §1.4's fixes applied, produces `Build succeeded, 0
Warning(s), 0 Error(s)` for all three projects, each emitting both `bin/Release/net10.0/*.dll` and
`bin/Release/netstandard2.1/*.dll` from a single `dotnet build`.** `windows/tools/build-engine-for-
unity.ps1` (architecture doc §3.2) then targets the `netstandard2.1` output folder specifically:
`dotnet build -f netstandard2.1 -c Release`.

`AI` is unaffected by multi-targeting — it stays `net10.0` only; nothing in this plan multi-targets
it (the AI sidecar's process-spawn half ports to Unity as its own thing per architecture doc §3.6,
not by multi-targeting `DungeonMasterAI.AI` itself). `App` is not multi-targeted either, for a
different reason: per the scope update in the preamble, it is deleted outright rather than kept
alive — see §8.

### 5.2 `Directory.Build.props` interaction

`windows/Directory.Build.props` sets `LangVersion latest`, `Nullable enable`, `ImplicitUsings
enable` with no TFM condition, and applies unchanged to the netstandard2.1 leg — verified, not
assumed:

- **`LangVersion latest`** lets the compiler accept C# 12/13 syntax (collection expressions, primary
  constructors, etc.) regardless of target framework — Roslyn's language version and a project's
  target framework are independent knobs. This is *why* §1.5's non-blockers compile fine under
  netstandard2.1 despite the BCL being years older: the compiler happily emits the lowered IL for
  `[]`/`[.. ]`/primary constructors against any target whose BCL has the underlying types
  (`List<T>`, arrays) those lowerings need. It only fails when code references a BCL *member* that
  target's reference assembly doesn't have — which is exactly §1.4's list, and why those failures
  show up as ordinary "member not found" compiler errors (`CS0117`/`CS0246`) rather than language
  version errors.
- **`Nullable enable`** works identically on both targets with no manual action — confirmed no
  `CS0656`/missing-attribute errors for `NullableAttribute`/`NullableContextAttribute` in the whole
  verification pass; Roslyn embeds these itself into the compiling assembly when the target
  framework doesn't already provide them.
- **`ImplicitUsings enable`** works identically on both targets (it is a project-level
  `global using` injection, unrelated to TFM).

**The `#if`-gated preprocessor symbols used throughout §1.4 need no manual `DefineConstants`
configuration.** The SDK automatically defines `NETSTANDARD2_1`, `NETSTANDARD`, `NET10_0`,
`NET10_0_OR_GREATER`, `NET`, etc. per target framework the moment `<TargetFrameworks>` includes it —
confirmed by every `#if NETSTANDARD2_1` block in the verification build resolving correctly with zero
`Directory.Build.props` changes.

### 5.3 CI: proving both legs stay green

Current CI (`.github/workflows/windows-ci.yml`) has two jobs relevant here: `source-validation`
(Ubuntu, a Python structural linter) and `build-test-package` (`windows-latest`, `dotnet build` on
`App` specifically, then `dotnet run` for each of the 27 non-WPF-coupled test projects individually,
then a self-contained publish + Inno Setup installer build). Neither currently builds `Domain`/
`Engine`/`Data` in isolation — they're pulled in transitively through `App`'s single `net10.0-windows`
build.

Add one job (this is architecture doc §10.9's "netstandard2.1 CI job" and §11.3's requirement that the
migration happen "while the full net10.0 test suite is green," made concrete):

```yaml
  netstandard21-build:
    runs-on: windows-latest      # or ubuntu-latest — the netstandard2.1 leg needs no Windows-only API
    timeout-minutes: 15
    defaults:
      run:
        shell: pwsh
        working-directory: windows
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - name: Build Domain/Engine/Data for netstandard2.1
        run: |
          dotnet build src/DungeonMasterAI.Domain/DungeonMasterAI.Domain.csproj -f netstandard2.1 -c Release
          dotnet build src/DungeonMasterAI.Engine/DungeonMasterAI.Engine.csproj -f netstandard2.1 -c Release
          dotnet build src/DungeonMasterAI.Data/DungeonMasterAI.Data.csproj -f netstandard2.1 -c Release
```

This alone proves "a Domain/Engine change that breaks Unity is discovered on the PR" — the
compile-fails-loudly half of correctness. It does **not** prove behavioral equivalence; that is a
separate, harder problem, which is §6. Because `ubuntu-latest` can build netstandard2.1 class
libraries with no Windows-specific dependency (verified: nothing in §1.5's non-blocker list or §1.4's
fixes touches a Windows-only API), this job can run on the cheaper/faster Linux runner in parallel
with the existing Windows jobs, rather than adding to `windows-latest` runner contention.

The existing `build-test-package` and `map-pipeline` jobs are otherwise unaffected — `App` keeps
building `net10.0-windows` exactly as today, since multi-targeting `Domain`/`Engine`/`Data` does not
change what `net10.0-windows` resolves to when it references them (MSBuild picks the closest
compatible framework automatically).

---

## 6. Verification strategy: proving identical behavior, not just a green build

This repo's own recent history is the argument for taking this seriously: two r61 branches compiled
cleanly while badly broken (a method with no caller; 13 silent WPF binding failures), and the fix
that worked was extracting the base commit to a scratch tree and proving the defects reproduced
*before* fixing them. The migration risk named throughout this document is the same shape: **it
compiles and quietly behaves differently.** Three concrete, complementary techniques, ordered by how
much of the existing test investment each one reuses.

### 6.1 Differential testing across both targets, using the *existing* test suite unchanged

This is the centerpiece, and it is a proven mechanism, not a proposal — verified directly. MSBuild's
`ProjectReference` supports a `SetTargetFramework` metadata attribute that forces a reference to
resolve against a *specific* framework output of a multi-targeted project, independent of the
referencing project's own target framework. Tested exactly as it would be used here: a `net10.0`
console app referencing `Engine`/`Domain` with `SetTargetFramework="TargetFramework=netstandard2.1"`
compiled clean, ran, and — read back via reflection — reported its loaded `Engine` assembly's
`TargetFrameworkAttribute` as `.NETStandard,Version=v2.1`, while producing a correct `DiceService`
roll result. **A `net10.0` host can load and exercise the netstandard2.1 build directly, with no
Unity involved.**

That means every one of the 27 non-WPF-coupled test projects under `windows/tests` — all plain
`net10.0` console apps that already assert engine behavior (§7 covers their structure) — can run
**twice**, with zero duplication of test code, by adding one conditional `ItemGroup` to each:

```xml
<ItemGroup>
  <ProjectReference Include="../../src/DungeonMasterAI.Engine/DungeonMasterAI.Engine.csproj"
                     Condition="'$(UseNetStandardEngine)' != 'true'" />
  <ProjectReference Include="../../src/DungeonMasterAI.Engine/DungeonMasterAI.Engine.csproj"
                     SetTargetFramework="TargetFramework=netstandard2.1"
                     Condition="'$(UseNetStandardEngine)' == 'true'" />
  <!-- same pattern for Domain and, where referenced, Data -->
</ItemGroup>
```

CI then runs the existing test matrix twice: once as today, once with `-p:UseNetStandardEngine=true`.
Same assertions, same expected values, two different compiled engines underneath. A divergence
between the two runs is exactly the "compiles clean, behaves differently" defect class this document
exists to catch — and because the `Guard`/`IsExternalInit`/regex/etc. remediations in §1.4 are known
in advance, a failure here after that remediation lands points at something the remediation missed,
not at an unknown unknown.

This is the highest-leverage single piece of verification infrastructure available here: it costs one
conditional `ItemGroup` per test project (mechanical, scriptable) and reuses roughly 11,000 lines of
already-written, already-trusted assertions with no new test-writing.

### 6.2 Golden-file / seeded-replay testing across the beat pipeline

Differential testing (§6.1) reruns *existing* assertions, which mostly check individual mechanics in
isolation (a death save, a spell save, an opportunity attack). It does not exercise a longer scripted
sequence the way a real session would. For that, exploit the fact the task brief names directly:
`Domain`/`Engine` contain no ambient randomness in the deterministic paths — `DiceService`'s
`Func<int,int,int>` constructor overload is, per the architecture doc §1.1, "the only injection seam
in the engine" for everything *except* the five `new DiceService()` fallback call sites identified in
§1.4.D, which any golden scenario should design around (or explicitly accept as excluded from the
diff).

Concretely: a new small test project (or an extension of `DungeonMasterAI.Smoke`, which already
takes a sample campaign manifest and exercises a real turn sequence) that:

1. Supplies a fixed, seeded `Func<int,int,int>` (e.g., wrapping `new Random(12345)`, or a literal
   fixed sequence for full reproducibility) to every `DiceService` the scenario touches.
2. Runs a scripted sequence of `GameEngine`/`DmToolRouter` calls — ideally the same sequence
   `DungeonMasterAI.Smoke` already replays against `sample_campaign_manifest.json` — against the
   `net10.0` build.
3. Serializes the resulting `CampaignState` (or the specific result records that matter: XP awards,
   HP deltas, position, initiative order) to a golden JSON file, using the project's own
   `System.Text.Json` path so the serialization itself is exercised, not just the engine logic.
4. Runs the identical scripted sequence, with the identical seed, against the netstandard2.1 build
   (via the `SetTargetFramework` mechanism from §6.1) and diffs the result against the golden file.

Because the XP/progression path is explicitly stated to have no randomness at all (per the task
brief, confirmed by this audit finding no `Random`/dice usage in `Domain/Progression.cs` beyond the
one `ThrowIfNull` call already covered in §1.4.A), a full end-to-end progression scenario — several
kills, a level-up threshold crossing, a coalesced multi-kill award — is a strong first golden
scenario: it should produce byte-identical output on both targets with **zero** seeding required,
making any divergence unambiguous.

### 6.3 What differential and golden-file testing do *not* cover, and the honest gap

Neither technique reaches into Unity itself — they prove `Engine.dll`'s netstandard2.1 build behaves
identically to its net10.0 build, which is the port's actual risk surface per this document's thesis,
but they do not prove the *Unity-hosted* build behaves the same way once ILRepack (§3) has merged and
internalized `System.Text.Json` and friends into it. Recommend the §3.3 spike's step 4 (the
`AppDataStore` round-trip + `DmToolRouter.ToOpenAiToolSchema()` call inside an actual Unity Editor
Play Mode session and an actual built player) double as a lightweight version of this: run it against
the same seeded scenario as §6.2 and diff its output against the same golden file. That closes the
loop from "netstandard2.1 DLL, verified on a .NET host" to "the same DLL, merged and loaded inside
Unity" without needing full Unity CI (which architecture doc §11.6 correctly declines to build for a
one-developer, no-distribution project).

---

## 7. Test-project handling

### 7.1 What the 33 projects actually are

Confirmed by inspection, not assumed: **none of the 33 projects under `windows/tests` use
xUnit/NUnit/MSTest.** Each is a plain `OutputType=Exe` console application with `TargetFramework=
net10.0`, run via `dotnet run --project ... -- [args]` in CI (matching `windows-ci.yml` exactly, not
`dotnet test`). Internally each is a sequence of top-level `Run("description", () => { ... assertions
... });` calls accumulating a `failures` list and a `passed` counter, exiting non-zero on any failure
— a hand-rolled harness, not a framework. This matters directly for §6.1's mechanism: because there is
no test framework runner to reconfigure, "run the same test project against a different engine build"
is *only* a `ProjectReference` question (exactly what `SetTargetFramework` answers), not a test-runner
configuration question.

**33 projects confirmed** (not just taken from the architecture doc's count): 27 are plain
`net10.0` console apps referencing `Domain`/`Engine`(/`Data`) only. **6 also reference
`DungeonMasterAI.App.csproj` and target `net10.0-windows`** — `GuiSmokeTests` (485 lines),
`MapAssetTests` (222), `MapRendererTests` (176), `R56MapBuilderTests` (217), `R57MapEditingTests`
(187), and `R62MapCombatTests` (563) — matching `docs/unity-target-architecture.md` §1.11's table
exactly; independently reconfirmed here rather than taken on faith.

### 7.2 The six WPF-coupled projects, read individually — the split is not uniform

Given WPF is now deleted immediately rather than kept alive (preamble), this is the most
time-sensitive section of this document: whatever coverage is not extracted before `App` is deleted
is gone. Each of the six was read in full, not sampled, because the architecture doc's own
recommendation — "split engine assertions out" — turns out to be the right instruction for only some
of them. **Three of the six have substantial, cleanly extractable engine assertions. Three have
essentially none, because what they test is `App`'s own ViewModel/catalog/provisioner classes, not
`Domain`/`Engine` — extracting *those* checks verbatim would just move WPF-coupled code into a
"portable" project that still can't compile.** Getting this distinction right is exactly the point:
naively assuming a uniform split either strands WPF-only code in a project that claims to be
portable, or discards real, currently-passing engine coverage that has nowhere else to go.

**`R62MapCombatTests` (563 lines) — the large majority extracts cleanly.** WPF appears at exactly
four places: `using System.Windows.Media.Imaging;` (line 7), and `RenderToPixels(CombatGridControl
control, ...)` (defined line 366, called lines 349 and 351). Everything else in the file is ordinary
`GameEngine`/`TacticalMapGeometry` assertions — this is the file the owner flagged as proving
combatants cannot walk through closed doors or be placed off-map, and that proof is pure engine
logic with no WPF dependency. **Action: extract everything except the `RenderToPixels`-dependent
`Run(...)` blocks (roughly 15–20 of the file's 563 lines) into a new plain-`net10.0` project; delete
the WPF-only remainder with `App`.**

**`MapRendererTests` (176 lines) — the cleanest case of the six.** Lines 22–58 are ~10 `Check(...)`
calls against `TacticalMapGeometry.Validate`, `CanMoveBetween`, `IsCellWalkable`,
`IsDifficultTerrain`, `MovementCostFeet`, `HasLineOfSight`, plus a `CampaignState`/JSON
serialization round-trip — 100% `Domain`/`Engine`, zero WPF, and they don't even reference
`TacticalMapControl` until line 60. Only the last ~20 lines (renderer construction, `RenderTargetBitmap`,
PNG write, one `Check`) are WPF. **Action: extract lines 22–58 plus the shared `BuildRuinedCrypt()`
map-fixture builder verbatim; delete the renderer-snapshot remainder with `App`.**

**`MapAssetTests` (222 lines) — a small extractable slice, and a larger non-extractable one that is
genuinely lost, not just relocated.** Two `Check(...)` calls (lines 70–71, 85–86) test
`TacticalMapAssetPackValidator.Validate` — a `Domain` type — and extract verbatim. Everything else
(catalog discovery, deterministic seeded variant resolution, image caching, `TacticalMapControl`
rendering) tests `TacticalMapAssetCatalog`, which — confirmed by locating its definition — lives in
`windows/src/DungeonMasterAI.App/Controls/TacticalMapAssetCatalog.cs`, **not** `Domain` or `Engine`.
**This is not a WPF-presentation detail on top of portable logic — the deterministic-by-seed asset
resolution behavior itself is implemented in the WPF layer today, so there is no engine-side code to
extract it to.** Deleting `App` deletes this behavior's only implementation, not just its test.
**Action:** extract the two validator checks; for the rest, carry the *requirements* forward as
acceptance criteria for Unity's `ScriptedImporter`/`MapAssetPackSO` replacement (architecture doc
§5.3) — specifically: variant selection must be deterministic for a given `(pack, key, seed, cell)`,
a missing key must return failure rather than throw, and pack author/license metadata must survive
into the catalog. The test itself does not survive; the requirements it encoded must not be
silently dropped.

**`R56MapBuilderTests` (217 lines) and `R57MapEditingTests` (187 lines) — essentially zero
extractable engine assertions.** Read both in full. Every meaningful check in both files exercises
`MainViewModel` (`InitializeMapWorkspace`, `BeginMapEditCommand`, `RerollMapVisualsCommand`,
`ApplyMapEditCommand`, `MapEditDraft`) or `CoreFantasyMapAssetPackProvisioner` — all `App`-only
types with no `Domain`/`Engine` counterpart. This is real, currently-tested business behavior, not
incidental UI plumbing — `R57MapEditingTests` in particular is the one place in the whole test suite
that already exercises the exact `Seed`-vs-`GenerationSeed` distinction the architecture doc's §11.9
risk register warns is "one careless line apart," proving today that a visual reroll changes `Seed`
but preserves `GenerationSeed` and all authored geometry. **None of this has anywhere portable to go
— it is not a split, it is a loss**, unless and until Unity's own Map Builder (architecture doc §9.6,
owner decision #8) reimplements the same guarantees. **Action:** do not attempt to extract test code
from these two projects — there is nothing in them that compiles without `App`. Instead, write down
the specific guarantees they currently prove as acceptance criteria for the Unity Map Builder
rebuild, so that work is scoped against real, currently-enforced behavior rather than the
architecture doc's design intent alone:
- a map-edit working copy is isolated from the saved map until Apply (R57, lines 37–38);
- a visual-only reroll changes the render seed, leaves `GenerationSeed` and all room/door geometry
  untouched (R57, lines 44–47) — this is the concrete, already-tested form of the §11.9 risk;
- an invalid draft (e.g. negative room coordinates) is rejected and not persisted (R57, lines 50–53);
- editing a review candidate (an AI-generated map not yet saved) never mutates the campaign's saved
  maps (R57, lines 68–73);
- a review/preview map clone reveals fog for DM inspection without mutating the saved map's own fog
  state (R56, lines 76–80);
- the first-party map pack provisions completely and its manifest references every one of its
  raster files, with local packs taking precedence over the packaged fallback (R56, lines 30–44).

**`GuiSmokeTests` (485 lines) — zero extractable engine content, and it is the one whose loss is
worth naming precisely.** Confirmed: every check in this project is presentation-layer — shell window
reference dimensions, packaged-resource presence, and, via `System.Diagnostics
.PresentationTraceSources.DataBindingSource` (`BindingFailureListener`, line 361), detection of
silent WPF data-binding failures. This is a real gate: per the repository's own history, this
mechanism is what catches a class of defect (a XAML binding that silently fails at runtime with no
exception and no compiler error) that a green build does not catch. **There is nothing to extract —
this entire project's subject matter (WPF shell construction, WPF data binding) ceases to exist the
moment `App` is deleted.** See §8.3 for what, if anything, stands in for this gate afterward.

### 7.3 What the suite looks like once both targets must stay green, and once `App` is gone

- The **27 already-portable projects** get the `SetTargetFramework` conditional `ItemGroup` from
  §6.1 and run twice in CI (once per target). No other change.
- **Two new portable projects** carry forward the extractable majority of `R62MapCombatTests` and
  `MapRendererTests` (§7.2), plus a **third, smaller new project** (or an addition to an existing
  portable one) for `MapAssetTests`'s two validator checks. These three join the 27 above — **30
  portable projects total**, all running the §6.1 differential pattern.
- **`GuiSmokeTests`, `R56MapBuilderTests`, `R57MapEditingTests`, and the WPF-only remainders of
  `R62MapCombatTests`/`MapRendererTests`/`MapAssetTests` are deleted, not split** — there is no
  portable half to keep. Their requirements are preserved as the acceptance-criteria list in §7.2 and
  §8.3, not as code.
- **`DungeonMasterAI.Smoke`** (1,416 lines, references `Domain`+`Engine`+`Data`, takes a sample
  campaign manifest as a CLI argument) is unaffected by the `App` deletion (it never referenced
  `App`) and is the natural host for §6.2's golden-scenario extension.
- **New:** the `netstandard21-build` CI job (§5.3), a second full pass of the 30 portable test
  projects with `-p:UseNetStandardEngine=true`, and the CI restructuring in §8.2.

Net effect: the total project count changes from 33 to roughly 30 (27 existing portable + 3 newly
extracted, minus the 6 WPF-coupled projects that are deleted rather than split), CI gets one new fast
Linux job (§5.3) plus a second run of the portable test matrix under the netstandard2.1 engine, and —
the point the owner's scope update makes unavoidable — **the map/combat rendering system, the Map
Builder's editing guarantees, and the binding-failure gate lose their automated coverage the moment
`App` is deleted**, with only the fraction described above actually carried forward as running code.
The rest is a deliberate, accepted gap until Unity's own equivalents exist (§8.3).

---

## 8. Deleting `DungeonMasterAI.App`: the ordered sequence

`App` is 42 `.cs` files + 16 `.xaml` files (58 code files, 12,029 lines, confirmed by direct count)
under `windows/src/DungeonMasterAI.App`, plus its `.csproj`. This section is the mechanical sequence
for removing it, what breaks in `.github/workflows/windows-ci.yml` as a result, and what the
pipeline looks like afterward — written so the deletion is a single well-understood commit rather
than a source of surprise CI failures.

### 8.1 Ordered sequence

1. **Extract first.** Land the three new portable test projects from §7.2 (the `R62MapCombatTests`
   and `MapRendererTests` majorities, the `MapAssetTests` validator slice) and confirm they pass
   against the current `net10.0` `Engine`/`Domain` build, referencing nothing under `App`. Write down
   the §7.2 acceptance-criteria list for `R56MapBuilderTests`/`R57MapEditingTests`/`GuiSmokeTests`
   somewhere durable (an issue, a section of the Unity Map Builder's own design notes) so it survives
   the deletion even though the code does not.
2. **Remove the six WPF-coupled test projects** (all of `GuiSmokeTests`, `R56MapBuilderTests`,
   `R57MapEditingTests`, and the WPF-only remainders of `MapAssetTests`/`MapRendererTests`/
   `R62MapCombatTests`) from `windows/tests/`.
3. **Delete `windows/src/DungeonMasterAI.App`** entirely.
4. **Update `.github/workflows/windows-ci.yml`** — see §8.2 for the exact steps affected.
5. **Confirm the remaining 30 portable test projects and the new `netstandard21-build` job are green**
   before merging the deletion — this is the same discipline §1.4.A already asks for (don't combine
   a structural change with an unrelated one in a window where a regression could hide), applied to
   the deletion itself.

Do steps 1–2 in one PR (extraction, provably not yet dependent on `App` being gone) and steps 3–4 in
a second (the deletion itself), so a problem with the extraction is caught before the safety net of
"just don't delete `App` yet" is removed.

### 8.2 What breaks in `windows-ci.yml`, concretely

Read directly from the current workflow (`build-test-package` and `map-pipeline` jobs):

| Current step | References `App`? | Disposition |
|---|---|---|
| `Restore app` / `Build app` (`DungeonMasterAI.App.csproj`) | Yes — is `App` | **Removed** |
| `Run AAA GUI construction smoke tests` (`GuiSmokeTests`) | Yes | **Removed** — see §8.3 |
| `Upload real WPF GUI snapshots` | Yes (artifact from the above) | **Removed** |
| `Publish self-contained Windows build` (`dotnet publish ... App.csproj`) | Yes | **Removed** — there is no player to publish until Unity produces one |
| `Install Inno Setup` / `Build installer` | Yes (packages the `App` publish output) | **Removed** |
| `Upload runnable build` / `Upload installer` | Yes | **Removed** |
| The other ~24 `dotnet run` steps in `build-test-package` (`RollTests`, `InitiativeTests`, etc.) | No — `Domain`/`Engine`(/`Data`) only | **Unchanged** |
| `Run map schema and renderer prototype tests (r53)` (`MapRendererTests`) | Currently yes | **Replaced** by the new portable `MapRendererTests` project from §7.2 |
| `Run high-quality map asset pack tests (r54)` (`MapAssetTests`) | Currently yes | **Replaced** by the new portable validator-only project from §7.2 |
| `Run local AI tactical map generation contract tests (r55)` (`AiMapGenerationTests`) | No — already portable | **Unchanged** |
| `Run production asset and Map Builder tests (r56)` (`R56MapBuilderTests`) | Yes | **Removed**, no replacement — §7.2 |
| `Run non-destructive map editing tests (r57)` (`R57MapEditingTests`) | Yes | **Removed**, no replacement — §7.2 |
| `Run map-combat wiring and spawn-vocabulary tests (r62)` (`R62MapCombatTests`) | Currently yes | **Replaced** by the new portable engine-assertion project from §7.2 |
| `Upload rendered map references` (PNG artifacts from r53/r54/r56/r57/r62) | Yes (all five renderer snapshots) | **Removed entirely** — see below |

**The `map-pipeline` job's entire purpose — producing rendered PNG proof that the map/combat system
looks right — goes away.** Once the WPF-only halves of r53/r54/r62 and all of r56/r57 are gone,
nothing in CI renders anything, so there is nothing left to upload as a visual artifact. This is a
real, visible regression in what CI proves, not a refactor — say so plainly rather than letting the
artifact-upload step quietly disappear from the YAML with no note of what stopped being checked.

**New job**, replacing the intent (not the mechanism) of the removed GUI/renderer coverage:
`netstandard21-build` from §5.3, plus the second differential pass over the 30 portable projects
from §7.3.

### 8.3 What replaces the GUI smoke test's binding-failure gate — an honest answer

The owner's message asks directly: does anything replace it, or does nothing? **The honest answer is
that nothing replaces it in kind, though the specific failure mode it exists to catch cannot recur
in Unity for a structural reason worth stating precisely, not just asserted.**

`GuiSmokeTests`'s value is catching a *silent* class of defect: a WPF `Binding` that fails at
runtime with no exception and no build error, verified through `PresentationTraceSources
.DataBindingSource`. Unity's two UI systems (architecture doc §2.6 — UI Toolkit for documents, uGUI
in world space) do not have this exact failure mode: UI Toolkit's data-binding path and typical uGUI
wiring both fail loudly (a null reference, a missing binding path throws or logs a visible Console
error) rather than silently, so the specific defect class `GuiSmokeTests` was built to catch is less
likely to reappear in the same shape. **That is a claim about the failure mode, not a claim that
equivalent test coverage exists.** Until Unity code exists, there is nothing to test, and this plan
does not invent a placeholder. Once it does exist, the closest analogues — worth building
deliberately rather than assuming they fall out for free — are:
- the Cecil-based EditMode test over `DMAI.Presentation`'s IL that the architecture doc's §3.3
  already calls for (catches an *architectural* class of defect — Presentation mutating a Domain
  object or reaching `Engine` — not a UI-wiring defect, but it is the nearest thing on the roadmap
  that plays the same "catches something a green build wouldn't" role);
- EditMode tests over `DMAI.Session`'s command/query surface (architecture doc §11.6), which can
  assert that a `Beat`/`Rejection` actually reaches a UI-facing property, the nearest functional
  equivalent to "did the value actually make it to the screen" — but this checks `Session`'s output,
  not that a specific UI element is correctly bound to it, so it is a narrower guarantee than
  `GuiSmokeTests` provided.

Until either of those exists and is deliberately scoped to cover this, **the gap is real and
unclosed** — recorded here rather than glossed over, per the instruction to say so honestly.

---

## 9. What I could not verify, and what remains genuinely open

Flagged plainly rather than guessed at:

- **Whether Unity 6.3's Mono scripting backend ships any part of the `System.Text.Json` dependency
  chain already** (§3.1). Unity is not installed in this environment. This is the single fact the
  day-one spike (§3.3) exists to establish, and nothing in this document should be read as having
  settled it.
- **Whether ILRepack actually produces a clean, loadable merge of `Engine.dll` plus the seven-
  assembly `System.Text.Json` graph inside an actual Unity player**, as opposed to a bare .NET host.
  Verified here: the netstandard2.1 `Engine.dll` builds clean and loads correctly on a .NET 10 host.
  Not verified: the ILRepack merge step itself, or its behavior once Unity's own assembly loading and
  (even under Mono) any stripping settings are involved. §3.3 states the exact spike to close this
  gap; it has not been run.
- **The exact `System.Text.Json` version and its exact transitive graph at implementation time.**
  §3.1's chain (8.0.5 → six dependencies) reflects a version resolved in an offline NuGet cache during
  this audit, used because it gives concrete, checkable numbers rather than a hand-wave — but the
  version actually restored when this work happens should be deliberately chosen and pinned, not
  assumed to match.
- **All six WPF-coupled test projects were read in full** for §7.2 (not sampled), so that section's
  per-project split-or-discard determination is verified, not estimated. What was **not** individually
  read line-by-line is the other 27 already-portable projects (`MapSchemaProvenanceTests`,
  `AiMapGenerationTests`, and the rest) — the `SetTargetFramework` mechanism they all need for §6.1's
  differential pattern is proven and framework-level regardless of what any individual project
  contains, so this is a risk about confirming each project's `ProjectReference` block accepts the
  conditional `ItemGroup` cleanly, not about the mechanism's correctness.
- **Everything the companion architecture document already flags as open** (§11, §12) is unchanged by
  this document and is not re-litigated here — in particular the model-size discrepancy (§1.10),
  temperature/latency questions (§7.7, §11.8), and the scope risk named in §11.5.

---

## Appendix: verification artifacts (not part of this change)

Everything reported as "verified" or "compiled clean" in this document came from an isolated scratch
copy of `Domain`/`Engine`/`Data`, built and discarded outside version control specifically to produce
this document. No file under `windows/` was modified to produce this plan, and the remediation shown
in §1.4 is a proposal for the actual migration to apply — described precisely enough (with the guard
class, the `#if` shapes, and the exact rewrite patterns shown inline above) that the migration itself
should not need to rediscover any of it.
