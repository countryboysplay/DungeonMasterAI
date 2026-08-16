using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DungeonMasterAI.AI;
using DungeonMasterAI.Data;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;
using Microsoft.Win32;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppDataStore _store = new();
    private readonly CampaignImportService _importer = new();
    private readonly CampaignReadinessValidator _readinessValidator = new();
    private readonly SrdSpellCatalogService _spellCatalog = new();
    private readonly CampaignRehearsalService _rehearsal = new();
    private readonly CampaignCloneService _campaignCloner = new();
    private readonly GameEngine _engine = new();
    private readonly DiceService _dice = new();
    private readonly RulesSearchService _rules = new();
    private readonly LocalDmClient _dm = new();
    private readonly CampaignAiCompilerService _campaignCompiler = new();
    private readonly CampaignAiExpansionService _campaignExpander = new();
    private readonly CampaignExpansionApplyService _expansionApplier = new();
    private readonly LlamaRuntimeManager _runtime = new(AppContext.BaseDirectory);
    private readonly RuntimeBootstrapService _runtimeBootstrap = new();
    private readonly DmToolRouter _tools;

    private AppState _state = new();
    private CampaignState? _selectedCampaign;
    private CharacterSheet? _selectedCharacter;
    private WorldLocation? _selectedLocation;
    private Merchant? _selectedMerchant;
    private MerchantStockEntry? _selectedStock;
    private EncounterState? _selectedEncounter;
    private CombatantDisplay? _selectedAttacker;
    private CombatantDisplay? _selectedTarget;
    private OpportunityAttackWindow? _selectedOpportunityAttack;
    private RuleSearchResult? _selectedRuleResult;
    private string _newCampaignName = "New Adventure";
    private string _playerInput = "";
    private string _ruleQuery = "";
    private string _statusMessage = "Ready";
    private string _localAiStatus = "Not checked";
    private string _localAiSetupProgress = "Local AI has not been set up yet.";
    private string _localAiRuntimeLog = "No local AI runtime output yet.";
    private string _campaignCompilerStatus = "Use AI Compile File after Local AI setup for full source extraction.";
    private bool _isAiSetupBusy;
    private bool _isDmBusy;
    private string _lastDiceResult = "";
    private string _concentrationEffectInput = "";
    private SpellDisplay? _selectedPreparedSpell;
    private SpellDisplay? _selectedCombatPreparedSpell;
    private CharacterSheet? _selectedSpellTarget;
    private string _spellSlotLevelInput = "1";
    private string _spellTargetAllocationInput = "";
    private string _spellAreaCenterXInput = "0";
    private string _spellAreaCenterYInput = "0";
    private string _spellAreaDirectionInput = "north";
    private bool _castAsRitual;
    private string _spellLibraryQuery = "";
    private SpellLibraryDisplay? _selectedLibrarySpell;
    private string _combatMoveXInput = "0";
    private string _combatMoveYInput = "0";
    private string _combatSkillInput = "perception";
    private string _combatDcInput = "15";
    private string _readyTriggerInput = "If the chosen trigger occurs";
    private bool _showDmMap;
    private int _mapRevision;
    private CampaignRehearsalReport? _rehearsalReport;

    public MainViewModel()
    {
        _tools = new DmToolRouter(_engine, _dice, _rules);
        CreateCampaignCommand = new AsyncRelayCommand(CreateCampaignAsync);
        ImportCampaignCommand = new AsyncRelayCommand(ImportCampaignAsync);
        CompileCampaignWithAiCommand = new AsyncRelayCommand(CompileCampaignWithAiAsync);
        ExpandCampaignWithAiCommand = new AsyncRelayCommand(ExpandCampaignWithAiAsync);
        LoadSampleCommand = new AsyncRelayCommand(LoadSampleAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        DeleteCampaignCommand = new AsyncRelayCommand(DeleteCampaignAsync);
        RunRehearsalCommand = new RelayCommand(RunRehearsal);
        AddQuickCharacterCommand = new AsyncRelayCommand(AddQuickCharacterAsync);
        DamageCharacterCommand = new AsyncRelayCommand(() => DamageSelectedAsync(1));
        HealCharacterCommand = new AsyncRelayCommand(() => HealSelectedAsync(1));
        RollD20Command = new AsyncRelayCommand(RollD20Async);
        AdvanceTenMinutesCommand = new AsyncRelayCommand(() => AdvanceTimeAsync(10));
        RevealLocationCommand = new AsyncRelayCommand(RevealSelectedLocationAsync);
        MovePartyCommand = new AsyncRelayCommand(MoveToSelectedLocationAsync);
        SearchRulesCommand = new RelayCommand(SearchRules);
        TestAiCommand = new AsyncRelayCommand(TestAiAsync);
        SetupLocalAiCommand = new AsyncRelayCommand(SetupLocalAiAsync);
        StopLocalAiCommand = new RelayCommand(StopLocalAi);
        SendPlayerInputCommand = new AsyncRelayCommand(SendPlayerInputAsync);
        LookAroundCommand = new AsyncRelayCommand(() => SendQuickPlayerInputAsync("I look around carefully. Describe what I can immediately see, hear, and interact with."));
        ContinueSceneCommand = new AsyncRelayCommand(() => SendQuickPlayerInputAsync("Continue the scene from the current verified state until I need to make a meaningful player decision."));
        TalkNearbyCommand = new AsyncRelayCommand(() => SendQuickPlayerInputAsync("I address the nearest relevant NPC and ask what is happening here."));
        QuickAttackCommand = new AsyncRelayCommand(QuickAttackAsync);
        QuickEndTurnCommand = new AsyncRelayCommand(() => SendQuickPlayerInputAsync("End my turn. Resolve any NPC turns automatically and stop when the next player character needs to act."));
        RollActiveDeathSaveCommand = new AsyncRelayCommand(RollActiveDeathSaveAsync);
        BuySelectedItemCommand = new AsyncRelayCommand(BuySelectedItemAsync);
        ShortRestCommand = new AsyncRelayCommand(ShortRestAsync);
        SpendHitDieCommand = new AsyncRelayCommand(SpendHitDieAsync);
        LongRestCommand = new AsyncRelayCommand(LongRestAsync);
        DeathSaveCommand = new AsyncRelayCommand(DeathSaveAsync);
        GrantTempHpCommand = new AsyncRelayCommand(GrantTempHpAsync);
        BeginConcentrationCommand = new AsyncRelayCommand(BeginConcentrationAsync);
        EndConcentrationCommand = new AsyncRelayCommand(EndConcentrationAsync);
        CastSelectedSpellCommand = new AsyncRelayCommand(CastSelectedSpellAsync);
        ActivateEncounterCommand = new AsyncRelayCommand(ActivateEncounterAsync);
        RollInitiativeCommand = new AsyncRelayCommand(RollInitiativeAsync);
        CombatAttackCommand = new AsyncRelayCommand(CombatAttackAsync);
        MoveCombatantCommand = new AsyncRelayCommand(MoveCombatantAsync);
        TakeDisengageCommand = new AsyncRelayCommand(TakeDisengageAsync);
        TakeDashCommand = new AsyncRelayCommand(TakeDashAsync);
        TakeDodgeCommand = new AsyncRelayCommand(TakeDodgeAsync);
        TakeHideCommand = new AsyncRelayCommand(TakeHideAsync);
        SearchHiddenCommand = new AsyncRelayCommand(SearchHiddenAsync);
        ReadyAttackCommand = new AsyncRelayCommand(ReadyAttackAsync);
        ReadyMoveCommand = new AsyncRelayCommand(ReadyMoveAsync);
        ReadySpellCommand = new AsyncRelayCommand(ReadySpellAsync);
        TriggerReadiedActionCommand = new AsyncRelayCommand(TriggerReadiedActionAsync);
        HelpAttackCommand = new AsyncRelayCommand(HelpAttackAsync);
        HelpAbilityCheckCommand = new AsyncRelayCommand(HelpAbilityCheckAsync);
        FirstAidCommand = new AsyncRelayCommand(FirstAidAsync);
        SearchActionCommand = new AsyncRelayCommand(() => CombatSkillActionAsync("search"));
        StudyActionCommand = new AsyncRelayCommand(() => CombatSkillActionAsync("study"));
        InfluenceActionCommand = new AsyncRelayCommand(() => CombatSkillActionAsync("influence"));
        GrappleCommand = new AsyncRelayCommand(GrappleAsync);
        ShoveProneCommand = new AsyncRelayCommand(() => ShoveAsync("prone"));
        ShovePushCommand = new AsyncRelayCommand(() => ShoveAsync("push"));
        EscapeGrappleCommand = new AsyncRelayCommand(EscapeGrappleAsync);
        ReleaseGrappleCommand = new AsyncRelayCommand(ReleaseGrappleAsync);
        StandFromProneCommand = new AsyncRelayCommand(StandFromProneAsync);
        ResolveOpportunityAttackCommand = new AsyncRelayCommand(ResolveOpportunityAttackAsync);
        DeclineOpportunityAttackCommand = new AsyncRelayCommand(DeclineOpportunityAttackAsync);
        NextCombatTurnCommand = new AsyncRelayCommand(NextCombatTurnAsync);
        EndEncounterCommand = new AsyncRelayCommand(EndEncounterAsync);
    }

    public AppState State { get => _state; private set { _state = value; OnPropertyChanged(); OnPropertyChanged(nameof(Campaigns)); OnPropertyChanged(nameof(Settings)); } }
    public AppSettings Settings => State.Settings;
    public IReadOnlyList<CampaignState> Campaigns => State.Campaigns;
    public ObservableCollection<RuleSearchResult> RuleResults { get; } = [];

    public CampaignState? SelectedCampaign
    {
        get => _selectedCampaign;
        set
        {
            if (ReferenceEquals(_selectedCampaign, value)) return;
            _selectedCampaign = value;
            _rehearsalReport = null;
            if (value is not null) State.SelectedCampaignId = value.Id;
            SelectedCharacter = value?.Characters.FirstOrDefault(c => c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)) ?? value?.Characters.FirstOrDefault();
            SelectedLocation = value?.Locations.FirstOrDefault(l => l.Id == value.PartyLocationId) ?? value?.Locations.FirstOrDefault();
            SelectedMerchant = value?.Merchants.FirstOrDefault();
            SelectedEncounter = value?.Encounters.LastOrDefault(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase)) ?? value?.Encounters.FirstOrDefault();
            OnPropertyChanged();
            RaiseCampaignProperties();
        }
    }

    public CharacterSheet? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            _selectedCharacter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCharacterEffectiveSpeed));
            OnPropertyChanged(nameof(SelectedCharacterConditions));
            OnPropertyChanged(nameof(SelectedCharacterDeathSaves));
            OnPropertyChanged(nameof(SelectedCharacterConcentration));
            OnPropertyChanged(nameof(PreparedSpells));
            OnPropertyChanged(nameof(SpellTargets));
            OnPropertyChanged(nameof(SpellcastingSummary));
            OnPropertyChanged(nameof(SpellSlotsSummary));
            OnPropertyChanged(nameof(SpellLibrary));
            SelectedLibrarySpell = SpellLibrary.FirstOrDefault();
            SelectedPreparedSpell = PreparedSpells.FirstOrDefault();
            SelectedSpellTarget = value;
        }
    }
    public WorldLocation? SelectedLocation { get => _selectedLocation; set { _selectedLocation = value; OnPropertyChanged(); } }
    public Merchant? SelectedMerchant { get => _selectedMerchant; set { _selectedMerchant = value; _selectedStock = value?.Stock.FirstOrDefault(); OnPropertyChanged(); OnPropertyChanged(nameof(SelectedStock)); OnPropertyChanged(nameof(SelectedMerchantStock)); } }
    public MerchantStockEntry? SelectedStock { get => _selectedStock; set { _selectedStock = value; OnPropertyChanged(); } }
    public EncounterState? SelectedEncounter
    {
        get => _selectedEncounter;
        set
        {
            _selectedEncounter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Combatants));
            OnPropertyChanged(nameof(CombatStatus));
            OnPropertyChanged(nameof(HasActiveCombat));
            OnPropertyChanged(nameof(PlaySceneModeTitle));
            OnPropertyChanged(nameof(ActiveTurnName));
            OnPropertyChanged(nameof(ActiveTurnSummary));
            RefreshCombatSelections();
        }
    }
    public CombatantDisplay? SelectedAttacker
    {
        get => _selectedAttacker;
        set
        {
            _selectedAttacker = value;
            if (value is not null)
            {
                CombatMoveXInput = value.GridX.ToString(System.Globalization.CultureInfo.InvariantCulture);
                CombatMoveYInput = value.GridY.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(CombatPreparedSpells));
            SelectedCombatPreparedSpell = CombatPreparedSpells.FirstOrDefault();
        }
    }
    public CombatantDisplay? SelectedTarget { get => _selectedTarget; set { _selectedTarget = value; OnPropertyChanged(); } }
    public OpportunityAttackWindow? SelectedOpportunityAttack { get => _selectedOpportunityAttack; set { _selectedOpportunityAttack = value; OnPropertyChanged(); } }
    public RuleSearchResult? SelectedRuleResult { get => _selectedRuleResult; set { _selectedRuleResult = value; OnPropertyChanged(); } }

    public string NewCampaignName { get => _newCampaignName; set { _newCampaignName = value; OnPropertyChanged(); } }
    public string PlayerInput { get => _playerInput; set { _playerInput = value; OnPropertyChanged(); } }
    public string RuleQuery { get => _ruleQuery; set { _ruleQuery = value; OnPropertyChanged(); } }
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }
    public string LocalAiStatus { get => _localAiStatus; private set { _localAiStatus = value; OnPropertyChanged(); } }
    public string LocalAiSetupProgress { get => _localAiSetupProgress; private set { _localAiSetupProgress = value; OnPropertyChanged(); } }
    public string LocalAiRuntimeLog { get => _localAiRuntimeLog; private set { _localAiRuntimeLog = value; OnPropertyChanged(); } }
    public string CampaignCompilerStatus { get => _campaignCompilerStatus; private set { _campaignCompilerStatus = value; OnPropertyChanged(); } }
    public bool IsAiSetupBusy { get => _isAiSetupBusy; private set { _isAiSetupBusy = value; OnPropertyChanged(); } }
    public bool IsDmBusy { get => _isDmBusy; private set { _isDmBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(DmActivityText)); } }
    public string DmActivityText => IsDmBusy ? "Dungeon Master is resolving the scene…" : "Ready for your action";
    public string LastDiceResult { get => _lastDiceResult; private set { _lastDiceResult = value; OnPropertyChanged(); } }
    public string ConcentrationEffectInput { get => _concentrationEffectInput; set { _concentrationEffectInput = value; OnPropertyChanged(); } }
    public SpellDisplay? SelectedPreparedSpell
    {
        get => _selectedPreparedSpell;
        set
        {
            _selectedPreparedSpell = value;
            SpellTargetAllocationInput = "";
            if (value is not null && value.Level > 0) SpellSlotLevelInput = Math.Max(1, value.Level).ToString(System.Globalization.CultureInfo.InvariantCulture);
            OnPropertyChanged();
        }
    }
    public SpellDisplay? SelectedCombatPreparedSpell
    {
        get => _selectedCombatPreparedSpell;
        set
        {
            _selectedCombatPreparedSpell = value;
            if (value is not null && value.Level > 0) SpellSlotLevelInput = Math.Max(1, value.Level).ToString(System.Globalization.CultureInfo.InvariantCulture);
            OnPropertyChanged();
        }
    }
    public CharacterSheet? SelectedSpellTarget { get => _selectedSpellTarget; set { _selectedSpellTarget = value; OnPropertyChanged(); } }
    public string SpellSlotLevelInput { get => _spellSlotLevelInput; set { _spellSlotLevelInput = value; OnPropertyChanged(); } }
    public string SpellTargetAllocationInput { get => _spellTargetAllocationInput; set { _spellTargetAllocationInput = value; OnPropertyChanged(); } }
    public string SpellAreaCenterXInput { get => _spellAreaCenterXInput; set { _spellAreaCenterXInput = value; OnPropertyChanged(); } }
    public string SpellAreaCenterYInput { get => _spellAreaCenterYInput; set { _spellAreaCenterYInput = value; OnPropertyChanged(); } }
    public string SpellAreaDirectionInput { get => _spellAreaDirectionInput; set { _spellAreaDirectionInput = value; OnPropertyChanged(); } }
    public bool CastAsRitual { get => _castAsRitual; set { _castAsRitual = value; OnPropertyChanged(); } }
    public string CombatMoveXInput { get => _combatMoveXInput; set { _combatMoveXInput = value; OnPropertyChanged(); } }
    public string CombatMoveYInput { get => _combatMoveYInput; set { _combatMoveYInput = value; OnPropertyChanged(); } }
    public string CombatSkillInput { get => _combatSkillInput; set { _combatSkillInput = value; OnPropertyChanged(); } }
    public string CombatDcInput { get => _combatDcInput; set { _combatDcInput = value; OnPropertyChanged(); } }
    public string ReadyTriggerInput { get => _readyTriggerInput; set { _readyTriggerInput = value; OnPropertyChanged(); } }
    public bool ShowDmMap
    {
        get => _showDmMap;
        set
        {
            if (_showDmMap == value) return;
            _showDmMap = value;
            MapRevision++;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Locations));
            OnPropertyChanged(nameof(Quests));
            OnPropertyChanged(nameof(RecentEvents));
            OnPropertyChanged(nameof(Encounters));
            OnPropertyChanged(nameof(FactionDisplays));
            OnPropertyChanged(nameof(SecretDisplays));
            OnPropertyChanged(nameof(RelationshipDisplays));
            OnPropertyChanged(nameof(TimelineDisplays));
            OnPropertyChanged(nameof(GeneratedDetails));
        }
    }
    public int MapRevision { get => _mapRevision; private set { _mapRevision = value; OnPropertyChanged(); } }

    public string CurrentLocationName => SelectedCampaign?.Locations.FirstOrDefault(l => l.Id == SelectedCampaign.PartyLocationId)?.Name ?? "No campaign selected";
    public string CampaignTime => SelectedCampaign is null ? "-" : GameEngine.FormatCampaignTime(SelectedCampaign);
    public string CampaignSummary => SelectedCampaign?.Summary ?? "Create or import a campaign to begin.";
    public IReadOnlyList<CampaignReadinessIssue> ReadinessIssues => SelectedCampaign is null ? [] : _readinessValidator.Validate(SelectedCampaign);
    public IReadOnlyList<CampaignRehearsalFinding> RehearsalFindings => CurrentRehearsal?.Findings ?? [];
    public string RehearsalSummary
    {
        get
        {
            var report = CurrentRehearsal;
            if (report is null) return "No campaign selected.";
            return report.Passed
                ? $"Static rehearsal passed with {report.Warnings} warning(s) and {report.Info} informational finding(s)."
                : $"Static rehearsal found {report.Errors} error(s), {report.Warnings} warning(s), and {report.Info} informational finding(s).";
        }
    }
    private CampaignRehearsalReport? CurrentRehearsal => SelectedCampaign is null ? null : _rehearsalReport ??= _rehearsal.Run(SelectedCampaign);

    public string ExpansionSummary
    {
        get
        {
            if (SelectedCampaign is null) return "No campaign selected.";
            var campaign = SelectedCampaign;
            var count = campaign.Locations.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Connections.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Characters.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Items.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Merchants.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Merchants.Sum(m => m.Stock.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase)))
                + campaign.Quests.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Factions.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Relationships.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Secrets.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Timeline.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Encounters.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase))
                + campaign.Supplements.Count(x => x.SourceKind.Equals("ai_expanded", StringComparison.OrdinalIgnoreCase));
            return count == 0 ? "No AI-expanded world details yet." : $"{count} generated detail(s) are marked ai_expanded and kept separate from source canon.";
        }
    }

    public string ReadinessSummary
    {
        get
        {
            var issues = ReadinessIssues;
            if (SelectedCampaign is null) return "No campaign selected.";
            var errors = issues.Count(i => i.Severity == ReadinessSeverity.Error);
            var warnings = issues.Count(i => i.Severity == ReadinessSeverity.Warning);
            var info = issues.Count(i => i.Severity == ReadinessSeverity.Info);
            return errors == 0 && warnings == 0
                ? $"Ready for core play checks ({info} informational item(s))."
                : $"{errors} error(s), {warnings} warning(s), {info} informational item(s).";
        }
    }
    public IEnumerable<CharacterSheet> Characters => SelectedCampaign?.Characters ?? [];
    public IEnumerable<WorldLocation> Locations => SelectedCampaign?.Locations.Where(l => ShowDmMap || (l.Discovered && !l.DmOnly)) ?? [];
    public IEnumerable<Quest> Quests => SelectedCampaign?.Quests.Where(q => ShowDmMap || !q.DmOnly) ?? [];
    public IEnumerable<CampaignEvent> RecentEvents => SelectedCampaign?.Events.Where(e => ShowDmMap || !e.DmOnly).TakeLast(12).Reverse() ?? [];
    public IEnumerable<ChatMessage> Chat => SelectedCampaign?.Chat ?? [];
    public IEnumerable<SessionChatMessageDisplay> SessionChat => (SelectedCampaign?.Chat ?? [])
        .Where(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) || message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
        .Select(message => new SessionChatMessageDisplay(
            message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "You" : "Dungeon Master",
            CleanSessionNarration(message.Content),
            message.Role.Equals("user", StringComparison.OrdinalIgnoreCase),
            message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)));
    public IEnumerable<CharacterSheet> PartyCharacters => SelectedCampaign?.Characters.Where(c => c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)) ?? [];
    public string CurrentLocationDescription => SelectedCampaign?.Locations.FirstOrDefault(l => l.Id == SelectedCampaign.PartyLocationId)?.Description ?? "No current location details are available.";
    public bool HasActiveCombat => SelectedEncounter?.Status.Equals("active", StringComparison.OrdinalIgnoreCase) == true;
    public string PlaySceneModeTitle => HasActiveCombat ? "TACTICAL COMBAT" : "EXPLORATION";
    public string ActiveTurnName
    {
        get
        {
            if (!HasActiveCombat || SelectedCampaign is null || SelectedEncounter is null || SelectedEncounter.Combatants.Count == 0 || SelectedEncounter.TurnIndex < 0 || SelectedEncounter.TurnIndex >= SelectedEncounter.Combatants.Count)
                return "No active turn";
            var combatant = SelectedEncounter.Combatants[SelectedEncounter.TurnIndex];
            return SelectedCampaign.Characters.FirstOrDefault(c => c.Id == combatant.CharacterId)?.Name ?? "Unknown combatant";
        }
    }
    public CharacterSheet? ActiveTurnCharacter
    {
        get
        {
            if (!HasActiveCombat || SelectedCampaign is null || SelectedEncounter is null || SelectedEncounter.Combatants.Count == 0
                || SelectedEncounter.TurnIndex < 0 || SelectedEncounter.TurnIndex >= SelectedEncounter.Combatants.Count)
                return null;
            var combatant = SelectedEncounter.Combatants[SelectedEncounter.TurnIndex];
            return SelectedCampaign.Characters.FirstOrDefault(c => c.Id == combatant.CharacterId);
        }
    }

    public CombatantState? ActiveTurnCombatant
    {
        get
        {
            if (!HasActiveCombat || SelectedEncounter is null || SelectedEncounter.Combatants.Count == 0
                || SelectedEncounter.TurnIndex < 0 || SelectedEncounter.TurnIndex >= SelectedEncounter.Combatants.Count)
                return null;
            return SelectedEncounter.Combatants[SelectedEncounter.TurnIndex];
        }
    }

    public PendingRollRequest? PendingPlayerRoll => SelectedCampaign?.PendingPlayerRoll;
    public bool PlayerRollRequired => PendingPlayerRoll?.Required == true;
    public string RollD20ButtonText => PlayerRollRequired && PendingPlayerRoll?.Formula.Equals("1d20", StringComparison.OrdinalIgnoreCase) == true
        ? "Roll d20 • Required"
        : "Roll d20";
    public string PendingPlayerRollPrompt => PendingPlayerRoll?.Purpose ?? "No required player roll.";

    public bool PlayerDeathSaveRequired
    {
        get
        {
            var pending = PendingPlayerRoll;
            if (pending is null
                || !pending.Required
                || !pending.ResolutionKey.Equals("combat_death_save", StringComparison.OrdinalIgnoreCase)
                || SelectedCampaign is null)
                return false;

            var character = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase));
            return character is not null
                && character.CurrentHp == 0
                && !character.Stable
                && !character.Dead;
        }
    }

    public bool ActivePlayerUnableToActAtZero
    {
        get
        {
            var character = ActiveTurnCharacter;
            return character is not null
                && character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
                && character.CurrentHp == 0
                && !PlayerDeathSaveRequired;
        }
    }

    public string ActiveDeathSaveStatus
    {
        get
        {
            var character = ActiveTurnCharacter;
            return character is null
                ? "0/3 successes • 0/3 failures"
                : $"{character.DeathSaveSuccesses}/3 successes • {character.DeathSaveFailures}/3 failures";
        }
    }

    public string ActiveDeathSavePrompt => PlayerDeathSaveRequired && PendingPlayerRoll is not null
        ? PendingPlayerRoll.Purpose
        : ActiveTurnCharacter is null
            ? "Death Saving Throw"
            : $"{ActiveTurnCharacter.Name} starts the turn at 0 HP.";

    public string ActiveTurnSummary => HasActiveCombat ? $"{CombatStatus} • {ActiveTurnName}" : $"{CurrentLocationName} • {CampaignTime}";
    public IEnumerable<Merchant> Merchants => SelectedCampaign?.Merchants ?? [];
    public IEnumerable<MerchantStockEntry> SelectedMerchantStock => SelectedMerchant?.Stock ?? [];
    public IEnumerable<EncounterState> Encounters => SelectedCampaign?.Encounters.Where(e => ShowDmMap || !e.DmOnly) ?? [];
    public IEnumerable<FactionDisplay> FactionDisplays => SelectedCampaign is null ? [] : SelectedCampaign.Factions
        .Where(f => ShowDmMap || (!string.IsNullOrWhiteSpace(f.PublicKnowledge) && !f.PublicKnowledge.TrimStart().StartsWith("None", StringComparison.OrdinalIgnoreCase)))
        .Select(f => new FactionDisplay(
            f.Name,
            f.Summary,
            ShowDmMap && !string.IsNullOrWhiteSpace(f.SecretKnowledge)
                ? string.Join(Environment.NewLine, new[] { f.PublicKnowledge, $"DM: {f.SecretKnowledge}" }.Where(x => !string.IsNullOrWhiteSpace(x)))
                : f.PublicKnowledge));
    public IEnumerable<SecretDisplay> SecretDisplays => SelectedCampaign is null ? [] : SelectedCampaign.Secrets
        .Where(secret => ShowDmMap || secret.Revealed)
        .Select(secret => new SecretDisplay(
            secret.Title,
            secret.Truth,
            ShowDmMap ? string.Join("; ", secret.RevealConditions) : "",
            secret.Revealed));
    public IEnumerable<RelationshipDisplay> RelationshipDisplays => SelectedCampaign is null ? [] : SelectedCampaign.Relationships
        .Where(r => ShowDmMap || r.Public)
        .Select(r => new RelationshipDisplay(r.SourceKey, r.TargetKey, r.Relation, r.Strength, r.Public));
    public IEnumerable<CampaignSupplement> GeneratedDetails => SelectedCampaign?.Supplements.Where(s => ShowDmMap || !s.DmOnly) ?? [];
    public IEnumerable<TimelineDisplay> TimelineDisplays => SelectedCampaign is null || !ShowDmMap ? [] : SelectedCampaign.Timeline
        .OrderBy(evt => evt.CampaignDay)
        .ThenBy(evt => evt.MinuteOfDay)
        .Select(evt => new TimelineDisplay(
            evt.Name,
            $"Day {evt.CampaignDay}, {evt.MinuteOfDay / 60:00}:{evt.MinuteOfDay % 60:00}",
            evt.Consequence,
            evt.DmNotes,
            evt.Resolved));
    public IEnumerable<CombatantDisplay> Combatants
    {
        get
        {
            if (SelectedCampaign is null || SelectedEncounter is null) return [];
            return SelectedEncounter.Combatants.Select(c =>
            {
                var character = SelectedCampaign.Characters.FirstOrDefault(x => x.Id == c.CharacterId);
                return new CombatantDisplay(
                    c.Id,
                    c.CharacterId,
                    character?.Name ?? "Unknown",
                    character?.CharacterType ?? "unknown",
                    c.Initiative,
                    character?.ArmorClass ?? 0,
                    character?.CurrentHp ?? 0,
                    character?.MaxHp ?? 0,
                    character?.TempHp ?? 0,
                    c.Surprised,
                    character?.Dead ?? false,
                    character?.ConcentrationEffect ?? "",
                    character is null || character.Conditions.Count == 0 ? "None" : string.Join(", ", character.Conditions),
                    character is null ? "" : GrappleStatus(SelectedCampaign, SelectedEncounter, c),
                    character is null ? "-" : HelpStatus(SelectedCampaign, SelectedEncounter, c),
                    c.IsHidden,
                    ReadyStatus(SelectedCampaign, SelectedEncounter, c),
                    character is null ? "" : character.Attacks.Count == 0 ? "Unarmed Strike" : string.Join(", ", character.Attacks.Select(a => a.Name)),
                    c.Positioned,
                    c.GridX,
                    c.GridY,
                    c.MovementRemainingFeet,
                    character is null ? 0 : CharacterMechanics.EffectiveSpeed(character, SelectedCampaign.ActiveEffects),
                    c.Side,
                    c.ActionAvailable,
                    c.BonusActionAvailable,
                    c.AttacksRemainingInAction,
                    c.ReactionAvailable,
                    c.Disengaging,
                    c.Dodging,
                    SelectedEncounter.Combatants.Count > 0 && SelectedEncounter.TurnIndex >= 0 && SelectedEncounter.TurnIndex < SelectedEncounter.Combatants.Count && SelectedEncounter.Combatants[SelectedEncounter.TurnIndex].Id == c.Id);
            }).ToArray();
        }
    }
    public string CombatStatus => SelectedEncounter is null
        ? "No encounter selected"
        : $"{SelectedEncounter.Status} • Round {SelectedEncounter.Round} • Turn {Math.Min(SelectedEncounter.TurnIndex + 1, Math.Max(1, SelectedEncounter.Combatants.Count))}/{Math.Max(1, SelectedEncounter.Combatants.Count)}";
    public IEnumerable<OpportunityAttackWindow> PendingOpportunityAttacks => SelectedEncounter?.PendingMove?.OpportunityAttacks.Where(x => !x.Resolved) ?? [];
    public string PendingMoveSummary => SelectedEncounter?.PendingMove is null
        ? "No pending reaction window."
        : $"Pending move: ({SelectedEncounter.PendingMove.FromX},{SelectedEncounter.PendingMove.FromY}) → ({SelectedEncounter.PendingMove.ToX},{SelectedEncounter.PendingMove.ToY}), cost {SelectedEncounter.PendingMove.MovementCostFeet} ft. Resolve or decline all Opportunity Attacks.";
    public int RuleChunkCount => _rules.Count;
    public int SpellCatalogCount => _spellCatalog.Count;
    public int SpellImplementedCount => _spellCatalog.Spells.Count(s => !s.Resolution.Equals("unsupported", StringComparison.OrdinalIgnoreCase));
    public string SpellLibraryQuery
    {
        get => _spellLibraryQuery;
        set
        {
            _spellLibraryQuery = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpellLibrary));
            SelectedLibrarySpell = SpellLibrary.FirstOrDefault();
        }
    }
    public SpellLibraryDisplay? SelectedLibrarySpell { get => _selectedLibrarySpell; set { _selectedLibrarySpell = value; OnPropertyChanged(); } }
    public IEnumerable<SpellLibraryDisplay> SpellLibrary
    {
        get
        {
            IEnumerable<SpellDefinition> spells = SelectedCampaign?.Spells.Count > 0 ? SelectedCampaign.Spells : _spellCatalog.Spells;
            var query = (SpellLibraryQuery ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(query))
                spells = spells.Where(spell => spell.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || spell.School.Contains(query, StringComparison.OrdinalIgnoreCase) || spell.Level.ToString(System.Globalization.CultureInfo.InvariantCulture) == query);
            return spells
                .GroupBy(spell => string.IsNullOrWhiteSpace(spell.Key) ? spell.Id : spell.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(spell => spell.Level)
                .ThenBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase)
                .Select(spell => new SpellLibraryDisplay(
                    spell.Id,
                    spell.Name,
                    spell.Level,
                    spell.School,
                    spell.CastingTime,
                    spell.RangeKind.Equals("distance", StringComparison.OrdinalIgnoreCase) && spell.RangeFeet > 0 ? $"{spell.RangeFeet} feet" : spell.RangeKind,
                    string.Concat(spell.RequiresVerbal ? "V" : "", spell.RequiresSomatic ? (spell.RequiresVerbal ? ", S" : "S") : "", spell.RequiresMaterial ? ((spell.RequiresVerbal || spell.RequiresSomatic) ? ", M" : "M") : ""),
                    spell.Duration,
                    spell.Ritual,
                    spell.RequiresConcentration,
                    !spell.Resolution.Equals("unsupported", StringComparison.OrdinalIgnoreCase),
                    spell.Resolution,
                    spell.SourcePage,
                    spell.SourceReference,
                    spell.Resolution.Equals("unsupported", StringComparison.OrdinalIgnoreCase) ? "Rules metadata only. Deterministic effect not implemented." : $"Deterministic {spell.Resolution} resolution available."));
        }
    }
    public string DataPath => _store.DataDirectory;
    public int SelectedCharacterEffectiveSpeed => SelectedCharacter is null ? 0 : CharacterMechanics.EffectiveSpeed(SelectedCharacter, SelectedCampaign?.ActiveEffects);
    public string SelectedCharacterConditions => SelectedCharacter is null || SelectedCharacter.Conditions.Count == 0 ? "None" : string.Join(", ", SelectedCharacter.Conditions);
    public string SelectedCharacterDeathSaves => SelectedCharacter is null ? "-" : $"{SelectedCharacter.DeathSaveSuccesses} successes / {SelectedCharacter.DeathSaveFailures} failures";
    public string SelectedCharacterConcentration => SelectedCharacter is null || string.IsNullOrWhiteSpace(SelectedCharacter.ConcentrationEffect) ? "None" : SelectedCharacter.ConcentrationEffect;
    public string SelectedCharacterOngoingEffects => SelectedCampaign is null || SelectedCharacter is null
        ? "None"
        : string.Join(" | ", SelectedCampaign.ActiveEffects
            .Where(e => e.TargetCharacterId.Equals(SelectedCharacter.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => string.IsNullOrWhiteSpace(e.Condition) ? e.Name : $"{e.Name}: {e.Condition}")) is var text && !string.IsNullOrWhiteSpace(text) ? text : "None";

    public IEnumerable<SpellDisplay> PreparedSpells
    {
        get
        {
            if (SelectedCampaign is null || SelectedCharacter is null) return [];
            var ids = SelectedCharacter.PreparedSpellIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return SelectedCampaign.Spells
                .Where(spell => ids.Contains(spell.Id) || (!string.IsNullOrWhiteSpace(spell.Key) && ids.Contains(spell.Key)))
                .OrderBy(spell => spell.Level)
                .ThenBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase)
                .Select(spell => new SpellDisplay(
                    spell.Id,
                    spell.Name,
                    spell.Level,
                    spell.School,
                    spell.CastingTime,
                    spell.RangeKind.Equals("distance", StringComparison.OrdinalIgnoreCase) ? $"{spell.RangeFeet} ft" : spell.RangeKind,
                    string.Join(", ", new[] { spell.RequiresVerbal ? "V" : null, spell.RequiresSomatic ? "S" : null, spell.RequiresMaterial ? "M" : null }.Where(x => x is not null)),
                    spell.Duration,
                    spell.Resolution,
                    SpellEffectSummary(spell),
                    spell.Ritual,
                    spell.RequiresConcentration));
        }
    }
    public IEnumerable<SpellDisplay> CombatPreparedSpells
    {
        get
        {
            if (SelectedCampaign is null || SelectedAttacker is null) return [];
            var character = SelectedCampaign.Characters.FirstOrDefault(c => c.Id == SelectedAttacker.CharacterId);
            if (character is null) return [];
            var ids = character.PreparedSpellIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return SelectedCampaign.Spells
                .Where(spell => ids.Contains(spell.Id) || (!string.IsNullOrWhiteSpace(spell.Key) && ids.Contains(spell.Key)))
                .Where(spell => (spell.CastingTime ?? "Action").Equals("Action", StringComparison.OrdinalIgnoreCase))
                .OrderBy(spell => spell.Level)
                .ThenBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase)
                .Select(spell => new SpellDisplay(
                    spell.Id, spell.Name, spell.Level, spell.School, spell.CastingTime,
                    spell.RangeKind.Equals("distance", StringComparison.OrdinalIgnoreCase) ? $"{spell.RangeFeet} ft" : spell.RangeKind,
                    string.Join(", ", new[] { spell.RequiresVerbal ? "V" : null, spell.RequiresSomatic ? "S" : null, spell.RequiresMaterial ? "M" : null }.Where(x => x is not null)),
                    spell.Duration, spell.Resolution, SpellEffectSummary(spell), spell.Ritual, spell.RequiresConcentration));
        }
    }

    public IEnumerable<CharacterSheet> SpellTargets => SelectedCampaign?.Characters.Where(c => !c.Dead) ?? [];
    public string SpellcastingSummary => SelectedCharacter is null
        ? "Select a character."
        : $"{SelectedCharacter.SpellcastingAbility} • Save DC {_engine.SpellSaveDc(SelectedCharacter)} • Attack {Signed(_engine.SpellAttackModifier(SelectedCharacter))}";
    public string SpellSlotsSummary => SelectedCharacter is null || SelectedCharacter.SpellSlots.Count == 0
        ? "No leveled spell slots configured."
        : string.Join("  |  ", SelectedCharacter.SpellSlots.OrderBy(x => x.Key).Select(x => $"L{x.Key}: {x.Value.Remaining}/{x.Value.Maximum}"));

    public ICommand CreateCampaignCommand { get; }
    public ICommand ImportCampaignCommand { get; }
    public ICommand CompileCampaignWithAiCommand { get; }
    public ICommand ExpandCampaignWithAiCommand { get; }
    public ICommand LoadSampleCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCampaignCommand { get; }
    public ICommand RunRehearsalCommand { get; }
    public ICommand AddQuickCharacterCommand { get; }
    public ICommand DamageCharacterCommand { get; }
    public ICommand HealCharacterCommand { get; }
    public ICommand RollD20Command { get; }
    public ICommand AdvanceTenMinutesCommand { get; }
    public ICommand RevealLocationCommand { get; }
    public ICommand MovePartyCommand { get; }
    public ICommand SearchRulesCommand { get; }
    public ICommand TestAiCommand { get; }
    public ICommand SetupLocalAiCommand { get; }
    public ICommand StopLocalAiCommand { get; }
    public ICommand SendPlayerInputCommand { get; }
    public ICommand LookAroundCommand { get; }
    public ICommand ContinueSceneCommand { get; }
    public ICommand TalkNearbyCommand { get; }
    public ICommand QuickAttackCommand { get; }
    public ICommand QuickEndTurnCommand { get; }
    public ICommand RollActiveDeathSaveCommand { get; }
    public ICommand BuySelectedItemCommand { get; }
    public ICommand ShortRestCommand { get; }
    public ICommand SpendHitDieCommand { get; }
    public ICommand LongRestCommand { get; }
    public ICommand DeathSaveCommand { get; }
    public ICommand GrantTempHpCommand { get; }
    public ICommand BeginConcentrationCommand { get; }
    public ICommand EndConcentrationCommand { get; }
    public ICommand CastSelectedSpellCommand { get; }
    public ICommand ActivateEncounterCommand { get; }
    public ICommand RollInitiativeCommand { get; }
    public ICommand CombatAttackCommand { get; }
    public ICommand MoveCombatantCommand { get; }
    public ICommand TakeDisengageCommand { get; }
    public ICommand TakeDashCommand { get; }
    public ICommand TakeDodgeCommand { get; }
    public ICommand TakeHideCommand { get; }
    public ICommand SearchHiddenCommand { get; }
    public ICommand ReadyAttackCommand { get; }
    public ICommand ReadyMoveCommand { get; }
    public ICommand ReadySpellCommand { get; }
    public ICommand TriggerReadiedActionCommand { get; }
    public ICommand HelpAttackCommand { get; }
    public ICommand HelpAbilityCheckCommand { get; }
    public ICommand FirstAidCommand { get; }
    public ICommand SearchActionCommand { get; }
    public ICommand StudyActionCommand { get; }
    public ICommand InfluenceActionCommand { get; }
    public ICommand GrappleCommand { get; }
    public ICommand ShoveProneCommand { get; }
    public ICommand ShovePushCommand { get; }
    public ICommand EscapeGrappleCommand { get; }
    public ICommand ReleaseGrappleCommand { get; }
    public ICommand StandFromProneCommand { get; }
    public ICommand ResolveOpportunityAttackCommand { get; }
    public ICommand DeclineOpportunityAttackCommand { get; }
    public ICommand NextCombatTurnCommand { get; }
    public ICommand EndEncounterCommand { get; }

    public async Task InitializeAsync()
    {
        State = await _store.LoadAsync();
        var rulesPath = Path.Combine(AppContext.BaseDirectory, "Knowledge", "srd_chunks.jsonl");
        await _rules.LoadAsync(rulesPath);
        var spellCatalogPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Rules", "srd_spells.json");
        await _spellCatalog.LoadAsync(spellCatalogPath);
        foreach (var campaign in State.Campaigns) _spellCatalog.MergeInto(campaign);
        SelectedCampaign = State.Campaigns.FirstOrDefault(c => c.Id == State.SelectedCampaignId) ?? State.Campaigns.FirstOrDefault();
        OnPropertyChanged(nameof(RuleChunkCount));
        OnPropertyChanged(nameof(SpellCatalogCount));
        OnPropertyChanged(nameof(SpellImplementedCount));
        OnPropertyChanged(nameof(SpellLibrary));
        SelectedLibrarySpell = SpellLibrary.FirstOrDefault();

        var runtimeExe = Path.Combine(_runtime.RuntimeDirectory, "llama-server.exe");
        if (File.Exists(runtimeExe))
        {
            LocalAiSetupProgress = "Local AI runtime is installed. Use Test Local AI to start or verify the configured model.";
            LocalAiStatus = "Runtime installed";
        }
        else
        {
            LocalAiStatus = "Not installed";
            LocalAiSetupProgress = "Use Set Up Local AI to install the runtime and download the model on first start.";
        }
        StatusMessage = _store.LastRecoveryMessage ?? (State.Campaigns.Count == 0 ? "Load the included sample or create a campaign." : "Campaign state loaded.");
    }


    private void RunRehearsal()
    {
        if (SelectedCampaign is null)
        {
            StatusMessage = "Select a campaign before running rehearsal.";
            return;
        }
        _rehearsalReport = _rehearsal.Run(SelectedCampaign);
        OnPropertyChanged(nameof(RehearsalFindings));
        OnPropertyChanged(nameof(RehearsalSummary));
        StatusMessage = RehearsalSummary;
    }

    private async Task CreateCampaignAsync()
    {
        var campaign = _engine.CreateCampaign(NewCampaignName);
        _spellCatalog.MergeInto(campaign);
        State.Campaigns.Add(campaign);
        OnPropertyChanged(nameof(Campaigns));
        SelectedCampaign = campaign;
        await SaveAsync();
        StatusMessage = $"Created {campaign.Name}.";
    }

    private async Task ImportCampaignAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Campaign",
            Filter = "Campaign sources|*.json;*.txt;*.md;*.markdown;*.pdf;*.docx|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var result = await _importer.ImportAsync(dialog.FileName);
            _spellCatalog.MergeInto(result.Campaign);
            State.Campaigns.Add(result.Campaign);
            OnPropertyChanged(nameof(Campaigns));
            SelectedCampaign = result.Campaign;
            await SaveAsync();
            StatusMessage = result.Warnings.Count == 0
                ? $"Imported {result.Campaign.Name}."
                : $"Imported with {result.Warnings.Count} review warning(s): {string.Join(" ", result.Warnings.Take(2))}";
        }
        catch (Exception ex) { StatusMessage = $"Import failed: {ex.Message}"; }
    }

    private async Task CompileCampaignWithAiAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Compile Campaign with Local AI",
            Filter = "Campaign documents|*.txt;*.md;*.markdown;*.pdf;*.docx|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            CampaignCompilerStatus = "Preparing campaign source...";
            var source = await _importer.ExtractSourceAsync(dialog.FileName);
            if (!await EnsureLocalAiReadyAsync(TimeSpan.FromMinutes(45)))
            {
                CampaignCompilerStatus = "Local AI is not ready. Use Settings > Set Up Local AI, then retry AI Compile File.";
                StatusMessage = CampaignCompilerStatus;
                return;
            }

            var progress = new Progress<CampaignCompileProgress>(p =>
            {
                CampaignCompilerStatus = p.Message;
                StatusMessage = p.Message;
            });
            var compiled = await _campaignCompiler.CompileAsync(source.SourceFile, source.Text, Settings, progress);
            var imported = _importer.ImportManifestJson(compiled.ManifestJson, source.SourceFile);
            _spellCatalog.MergeInto(imported.Campaign);
            imported.Campaign.Events.Add(new CampaignEvent
            {
                Type = "ai_campaign_compiled",
                Summary = $"Local AI extracted campaign canon from {source.SourceFile} in {compiled.ChunkCount} source chunks.",
                DmOnly = true
            });

            var expansionWarnings = new List<string>();
            var expansionAdded = 0;
            try
            {
                var readiness = _readinessValidator.Validate(imported.Campaign).Select(i => $"{i.Severity}: {i.Category}: {i.Message}").ToArray();
                var expansionProgress = new Progress<CampaignExpansionProgress>(p =>
                {
                    CampaignCompilerStatus = p.Message;
                    StatusMessage = p.Message;
                });
                var expansion = await _campaignExpander.ExpandAsync(imported.Campaign, readiness, Settings, expansionProgress);
                var applied = _expansionApplier.Apply(imported.Campaign, expansion.PatchJson);
                expansionAdded = applied.AddedObjects;
                expansionWarnings.AddRange(expansion.Warnings);
                expansionWarnings.AddRange(applied.Warnings);
            }
            catch (Exception ex)
            {
                expansionWarnings.Add($"Canon extraction succeeded, but automatic playability expansion was skipped: {ex.Message}");
            }

            State.Campaigns.Add(imported.Campaign);
            OnPropertyChanged(nameof(Campaigns));
            SelectedCampaign = imported.Campaign;
            await SaveAsync();

            var warningCount = compiled.Warnings.Count + imported.Warnings.Count + expansionWarnings.Count;
            CampaignCompilerStatus = warningCount == 0
                ? $"Compiled {imported.Campaign.Name} from {compiled.ChunkCount} source chunks and added {expansionAdded} AI-expanded playable detail(s)."
                : $"Compiled {imported.Campaign.Name}, added {expansionAdded} AI-expanded detail(s), and found {warningCount} review warning(s).";
            StatusMessage = CampaignCompilerStatus;
        }
        catch (Exception ex)
        {
            CampaignCompilerStatus = $"AI campaign compilation failed: {ex.Message}";
            StatusMessage = CampaignCompilerStatus;
        }
    }


    private async Task ExpandCampaignWithAiAsync()
    {
        if (SelectedCampaign is null)
        {
            StatusMessage = "Select a campaign before expanding missing content.";
            return;
        }
        try
        {
            CampaignCompilerStatus = "Preparing campaign playability expansion...";
            if (!await EnsureLocalAiReadyAsync(TimeSpan.FromMinutes(45)))
            {
                CampaignCompilerStatus = "Local AI is not ready. Use Settings > Set Up Local AI, then retry expansion.";
                StatusMessage = CampaignCompilerStatus;
                return;
            }

            var original = SelectedCampaign;
            var working = _campaignCloner.Clone(original);
            var readiness = _readinessValidator.Validate(working).Select(i => $"{i.Severity}: {i.Category}: {i.Message}").ToArray();
            var progress = new Progress<CampaignExpansionProgress>(p =>
            {
                CampaignCompilerStatus = p.Message;
                StatusMessage = p.Message;
            });
            var expansion = await _campaignExpander.ExpandAsync(working, readiness, Settings, progress);
            var applied = _expansionApplier.Apply(working, expansion.PatchJson);
            CommitCampaign(original, working);
            await SaveAsync();
            var warnings = expansion.Warnings.Count + applied.Warnings.Count;
            CampaignCompilerStatus = warnings == 0
                ? $"Added {applied.AddedObjects} AI-expanded playable detail(s). Source canon was left unchanged."
                : $"Added {applied.AddedObjects} AI-expanded detail(s) with {warnings} review warning(s).";
            StatusMessage = CampaignCompilerStatus;
        }
        catch (Exception ex)
        {
            CampaignCompilerStatus = $"Campaign expansion failed without changing the saved campaign: {ex.Message}";
            StatusMessage = CampaignCompilerStatus;
        }
    }

    private async Task<bool> EnsureLocalAiReadyAsync(TimeSpan timeout)
    {
        var status = await _dm.CheckAsync(Settings);
        if (status.Online)
        {
            LocalAiStatus = $"Online: {status.Model ?? Settings.ModelName}";
            return true;
        }

        var runtimeExe = Path.Combine(_runtime.RuntimeDirectory, "llama-server.exe");
        if (!File.Exists(runtimeExe))
        {
            LocalAiStatus = "Not installed";
            return false;
        }
        if (!_runtime.IsRunning && !_runtime.TryStart(Settings))
        {
            LocalAiStatus = $"Offline: {_runtime.LastError}";
            return false;
        }
        LocalAiStatus = "Starting local model...";
        var ready = await _runtime.WaitUntilReadyAsync(_dm, Settings, timeout);
        if (!ready)
        {
            LocalAiStatus = $"Offline: {_runtime.LastError}";
            return false;
        }
        status = await _dm.CheckAsync(Settings);
        LocalAiStatus = status.Online ? $"Online: {status.Model ?? Settings.ModelName}" : $"Offline: {status.Message}";
        return status.Online;
    }

    private async Task LoadSampleAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sample", "sample_campaign_manifest.json");
        if (!File.Exists(path)) { StatusMessage = "Included sample campaign is missing from this build."; return; }
        var result = await _importer.ImportManifestAsync(path);
        _spellCatalog.MergeInto(result.Campaign);
        result.Campaign.Name += " (Sample)";
        State.Campaigns.Add(result.Campaign);
        OnPropertyChanged(nameof(Campaigns));
        SelectedCampaign = result.Campaign;
        await SaveAsync();
        StatusMessage = "Loaded the Greenhaven sample campaign.";
    }

    private async Task SaveAsync()
    {
        if (SelectedCampaign is not null) SelectedCampaign.UpdatedAt = DateTimeOffset.UtcNow;
        await _store.SaveAsync(State);
        StatusMessage = "Saved.";
    }

    private async Task DeleteCampaignAsync()
    {
        if (SelectedCampaign is null) return;
        var name = SelectedCampaign.Name;
        State.Campaigns.Remove(SelectedCampaign);
        OnPropertyChanged(nameof(Campaigns));
        SelectedCampaign = State.Campaigns.FirstOrDefault();
        State.SelectedCampaignId = SelectedCampaign?.Id;
        await SaveAsync();
        StatusMessage = $"Deleted {name}.";
    }

    private async Task AddQuickCharacterAsync()
    {
        if (SelectedCampaign is null) return;
        var n = SelectedCampaign.Characters.Count(c => c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)) + 1;
        var character = new CharacterSheet
        {
            Key = $"pc.{n}", Name = $"Adventurer {n}", CharacterType = "pc", CreatureType = "Humanoid", Level = 1, ProficiencyBonus = 2,
            ArmorClass = 14, MaxHp = 12, CurrentHp = 12, Gold = 20, HitDieSides = 8, HitDiceMaximum = 1, HitDiceRemaining = 1,
            Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["strength"] = 14, ["dexterity"] = 14, ["constitution"] = 14, ["intelligence"] = 10, ["wisdom"] = 12, ["charisma"] = 10
            }
        };
        var practiceSpark = SelectedCampaign.Spells.FirstOrDefault(x => x.Key.Equals("test.practice_spark", StringComparison.OrdinalIgnoreCase));
        if (practiceSpark is null)
        {
            practiceSpark = new SpellDefinition
            {
                Key = "test.practice_spark", Name = "Practice Spark", Level = 0, School = "Evocation", CastingTime = "Action",
                RangeKind = "distance", RangeFeet = 60, RequiresVerbal = true, RequiresSomatic = true, RequiresTarget = true,
                Resolution = "attack", DamageExpression = "1d8", DamageType = "Force", SourceKind = "test_fixture"
            };
            SelectedCampaign.Spells.Add(practiceSpark);
        }
        var practiceMend = SelectedCampaign.Spells.FirstOrDefault(x => x.Key.Equals("test.practice_mend", StringComparison.OrdinalIgnoreCase));
        if (practiceMend is null)
        {
            practiceMend = new SpellDefinition
            {
                Key = "test.practice_mend", Name = "Practice Mend", Level = 1, School = "Evocation", CastingTime = "Action",
                RangeKind = "touch", RequiresVerbal = true, RequiresSomatic = true, RequiresTarget = true,
                Resolution = "healing", HealingExpression = "1d8+2", ExtraHealingPerSlotExpression = "1d8", SourceKind = "test_fixture"
            };
            SelectedCampaign.Spells.Add(practiceMend);
        }
        character.SpellcastingAbility = "wisdom";
        character.PreparedSpellIds.Add(practiceSpark.Id);
        character.PreparedSpellIds.Add(practiceMend.Id);
        character.SpellSlots[1] = new SpellSlotPool { Maximum = 2, Remaining = 2 };
        _engine.AddCharacter(SelectedCampaign, character);
        SelectedCharacter = character;
        RaiseCampaignProperties();
        await SaveAsync();
    }

    private async Task DamageSelectedAsync(int amount)
    {
        if (SelectedCampaign is null || SelectedCharacter is null) return;
        try
        {
            var resolution = _engine.ApplyDamageWithConcentration(SelectedCampaign, SelectedCharacter.Id, amount, _dice);
            StatusMessage = resolution.Concentration is null
                ? resolution.Damage.Summary
                : $"{resolution.Damage.Summary} {resolution.Concentration.Summary}";
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task HealSelectedAsync(int amount)
    {
        if (SelectedCampaign is null || SelectedCharacter is null) return;
        _engine.Heal(SelectedCampaign, SelectedCharacter.Id, amount);
        OnPropertyChanged(nameof(SelectedCharacter)); RaiseCampaignProperties(); await SaveAsync();
    }

    private async Task AdvanceTimeAsync(int minutes)
    {
        if (SelectedCampaign is null) return;
        _engine.AdvanceTime(SelectedCampaign, minutes); RaiseCampaignProperties(); await SaveAsync();
    }

    private async Task RevealSelectedLocationAsync()
    {
        if (SelectedCampaign is null || SelectedLocation is null) return;
        try { _engine.RevealLocation(SelectedCampaign, SelectedLocation.Id); MapRevision++; RaiseCampaignProperties(); await SaveAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task MoveToSelectedLocationAsync()
    {
        if (SelectedCampaign is null || SelectedLocation is null) return;
        try { _engine.MoveParty(SelectedCampaign, SelectedLocation.Id); MapRevision++; RaiseCampaignProperties(); await SaveAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void SearchRules()
    {
        RuleResults.Clear();
        foreach (var result in _rules.Search(RuleQuery, 10)) RuleResults.Add(result);
        SelectedRuleResult = RuleResults.FirstOrDefault();
        StatusMessage = RuleResults.Count == 0 ? "No matching SRD rule chunks found." : $"Found {RuleResults.Count} rule result(s).";
    }

    private async Task TestAiAsync()
    {
        if (IsAiSetupBusy) return;
        IsAiSetupBusy = true;
        try
        {
            var status = await _dm.CheckAsync(Settings);
            if (!status.Online)
            {
                if (!File.Exists(Path.Combine(_runtime.RuntimeDirectory, "llama-server.exe")))
                {
                    LocalAiStatus = "Not installed";
                    LocalAiSetupProgress = "The local runtime is not installed yet. Use Start Local AI to install it.";
                    return;
                }

                LocalAiStatus = "Starting model...";
                LocalAiSetupProgress = "Starting llama.cpp. On the first launch the configured GGUF model must download before it can load into GPU memory.";
                StatusMessage = LocalAiSetupProgress;
                if (!_runtime.TryStart(Settings))
                {
                    LocalAiStatus = "Offline";
                    LocalAiSetupProgress = _runtime.LastError ?? "The local AI runtime did not start.";
                    RefreshLocalAiRuntimeLog();
                    return;
                }

                if (!await WaitForLocalAiWithProgressAsync(TimeSpan.FromMinutes(45))) return;
                status = await _dm.CheckAsync(Settings);
                if (!status.Online)
                {
                    LocalAiStatus = $"Offline: {status.Message}";
                    LocalAiSetupProgress = status.Message;
                    return;
                }
            }

            LocalAiStatus = $"Online: {status.Model ?? Settings.ModelName}";
            LocalAiSetupProgress = "Server is online. Running a real chat inference test...";
            StatusMessage = LocalAiSetupProgress;
            var inference = await _dm.TestInferenceAsync(Settings);
            LocalAiStatus = inference.Online ? $"Inference ready: {status.Model ?? Settings.ModelName}" : "Inference failed";
            LocalAiSetupProgress = inference.Message;
            StatusMessage = inference.Message;
            RefreshLocalAiRuntimeLog();
        }
        catch (Exception ex)
        {
            LocalAiStatus = "Offline";
            LocalAiSetupProgress = ex.Message;
            StatusMessage = LocalAiSetupProgress;
            RefreshLocalAiRuntimeLog();
        }
        finally { IsAiSetupBusy = false; }
    }

    private async Task SetupLocalAiAsync()
    {
        if (IsAiSetupBusy) return;
        IsAiSetupBusy = true;
        try
        {
            LocalAiStatus = "Preparing local AI...";
            LocalAiSetupProgress = "Checking the bundled local AI runtime...";
            var progress = new Progress<RuntimeProvisionProgress>(p =>
            {
                LocalAiSetupProgress = p.Message;
                StatusMessage = p.Message;
            });
            var installed = await _runtimeBootstrap.EnsureRuntimeAsync(_runtime.RuntimeDirectory, progress);
            if (!installed.Success)
            {
                LocalAiStatus = "Setup failed";
                LocalAiSetupProgress = installed.Message;
                StatusMessage = installed.Message;
                return;
            }

            LocalAiStatus = "Starting model...";
            LocalAiSetupProgress = "Runtime ready. Starting the configured GGUF model. First launch may spend several minutes downloading before GPU loading begins.";
            StatusMessage = LocalAiSetupProgress;
            if (!_runtime.TryStart(Settings))
            {
                LocalAiStatus = "Setup failed";
                LocalAiSetupProgress = _runtime.LastError ?? "The local AI runtime did not start.";
                RefreshLocalAiRuntimeLog();
                return;
            }

            if (!await WaitForLocalAiWithProgressAsync(TimeSpan.FromMinutes(45))) return;
            var status = await _dm.CheckAsync(Settings);
            LocalAiStatus = status.Online ? $"Online: {status.Model ?? Settings.ModelName}" : $"Offline: {status.Message}";
            LocalAiSetupProgress = status.Online ? "Local AI setup is complete and the model is ready." : status.Message;
            StatusMessage = LocalAiSetupProgress;
            await SaveAsync();
        }
        catch (Exception ex)
        {
            LocalAiStatus = "Setup failed";
            LocalAiSetupProgress = ex.Message;
            StatusMessage = ex.Message;
            RefreshLocalAiRuntimeLog();
        }
        finally { IsAiSetupBusy = false; }
    }

    private async Task<bool> WaitForLocalAiWithProgressAsync(TimeSpan timeout)
    {
        var started = DateTimeOffset.UtcNow;
        var deadline = started + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!_runtime.IsRunning)
            {
                RefreshLocalAiRuntimeLog();
                LocalAiStatus = "Offline";
                LocalAiSetupProgress = _runtime.LastError ?? "The local AI process exited before becoming ready.";
                StatusMessage = LocalAiSetupProgress;
                return false;
            }

            var status = await _dm.CheckAsync(Settings);
            RefreshLocalAiRuntimeLog();
            if (status.Online) return true;

            var elapsed = DateTimeOffset.UtcNow - started;
            LocalAiSetupProgress = BuildLocalAiProgressMessage(_runtime.RecentLog, elapsed);
            StatusMessage = LocalAiSetupProgress;
            await Task.Delay(1000);
        }

        RefreshLocalAiRuntimeLog();
        LocalAiStatus = "Offline";
        LocalAiSetupProgress = "The model did not become ready within 45 minutes. The runtime log below contains the startup details.";
        StatusMessage = LocalAiSetupProgress;
        return false;
    }

    private void RefreshLocalAiRuntimeLog()
    {
        var log = _runtime.RecentLog;
        if (string.IsNullOrWhiteSpace(log))
        {
            LocalAiRuntimeLog = "Waiting for llama.cpp output...";
            return;
        }

        var lines = log.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        LocalAiRuntimeLog = string.Join(Environment.NewLine, lines.TakeLast(18));
    }

    private static string BuildLocalAiProgressMessage(string log, TimeSpan elapsed)
    {
        var elapsedText = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{elapsed.Seconds}s";
        if (string.IsNullOrWhiteSpace(log))
            return $"Starting local model ({elapsedText}). The first launch can remain here while the GGUF downloads.";

        if (log.Contains("download", StringComparison.OrdinalIgnoreCase) || log.Contains("huggingface", StringComparison.OrdinalIgnoreCase) || log.Contains("hf_", StringComparison.OrdinalIgnoreCase))
            return $"Downloading or resolving the local model ({elapsedText}). See Runtime Output in Settings for details.";
        if (log.Contains("load_tensors", StringComparison.OrdinalIgnoreCase) || log.Contains("loading model", StringComparison.OrdinalIgnoreCase) || log.Contains("llama_model_load", StringComparison.OrdinalIgnoreCase))
            return $"Loading the model into memory/GPU ({elapsedText}). See Runtime Output in Settings for details.";
        if (log.Contains("server is listening", StringComparison.OrdinalIgnoreCase) || log.Contains("listening on", StringComparison.OrdinalIgnoreCase))
            return $"Model loaded. Waiting for the local API health check ({elapsedText}).";
        return $"Local AI is starting ({elapsedText}). See Runtime Output in Settings for the latest llama.cpp messages.";
    }

    private void StopLocalAi()
    {
        _runtime.Stop();
        LocalAiStatus = "Stopped";
        LocalAiSetupProgress = "Local AI was stopped. The deterministic game engine remains available.";
        StatusMessage = LocalAiSetupProgress;
        RefreshLocalAiRuntimeLog();
    }

    private async Task SendPlayerInputAsync()
    {
        if (string.IsNullOrWhiteSpace(PlayerInput)) return;
        var input = PlayerInput.Trim();
        PlayerInput = "";
        await SendPlayerInputCoreAsync(input);
    }

    private async Task SendQuickPlayerInputAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        await SendPlayerInputCoreAsync(input.Trim());
    }

    private async Task QuickAttackAsync()
    {
        var target = SelectedTarget?.Name;
        var instruction = string.IsNullOrWhiteSpace(target)
            ? "I attack the most immediate hostile creature with my appropriate currently available attack."
            : $"I attack {target} with my appropriate currently available attack.";
        await SendQuickPlayerInputAsync(instruction);
    }

    private async Task SendPlayerInputCoreAsync(string input)
    {
        if (SelectedCampaign is null || string.IsNullOrWhiteSpace(input) || IsDmBusy) return;
        _engine.EnsurePendingPlayerRollForActiveCombat(SelectedCampaign);
        if (SelectedCampaign.PendingPlayerRoll?.Required == true)
        {
            StatusMessage = $"Required roll pending: {SelectedCampaign.PendingPlayerRoll.Purpose}";
            RaiseCampaignProperties();
            return;
        }
        IsDmBusy = true;
        StatusMessage = "Dungeon Master is resolving the scene…";

        // Resolve AI tool calls against an isolated campaign copy. A failed model call,
        // malformed response, or exhausted tool loop cannot leave half-applied game state.
        var originalCampaign = SelectedCampaign;
        var workingCampaign = _campaignCloner.Clone(originalCampaign);

        try
        {
            var result = await _dm.RunTurnAsync(workingCampaign, input, Settings, _tools);
            workingCampaign.Chat.Add(new ChatMessage { Role = "user", Content = input });
            workingCampaign.Chat.Add(new ChatMessage { Role = "assistant", Content = result.Narration });
            foreach (var audit in result.Audit)
            {
                workingCampaign.Events.Add(new CampaignEvent
                {
                    Type = "dm_tool_audit",
                    Summary = audit,
                    DmOnly = true
                });
            }

            CommitCampaign(originalCampaign, workingCampaign);
            StatusMessage = result.ToolCalls == 0 ? "DM turn complete." : $"DM turn complete with {result.ToolCalls} verified tool call(s).";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or InvalidOperationException)
        {
            originalCampaign.Chat.Add(new ChatMessage { Role = "user", Content = input });
            originalCampaign.Chat.Add(new ChatMessage
            {
                Role = "system",
                Content = "[Local AI offline] Your action was recorded, but no AI-requested state changes were committed. The deterministic campaign state remains at the last verified point."
            });
            originalCampaign.Events.Add(new CampaignEvent
            {
                Type = "dm_turn_rolled_back",
                Summary = "An AI turn failed and all uncommitted tool effects were discarded.",
                DmOnly = true
            });
            LocalAiStatus = $"Offline: {ex.Message}";
            StatusMessage = "Player action recorded; unverified AI state changes were rolled back.";
        }
        finally
        {
            IsDmBusy = false;
        }

        RaiseCampaignProperties();
        await SaveAsync();
    }

    private static string CleanSessionNarration(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var text = content.Replace("**", "", StringComparison.Ordinal)
            .Replace("__", "", StringComparison.Ordinal);
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(line => line.TrimStart().TrimStart('#').TrimStart())
            .ToArray();
        return string.Join(Environment.NewLine, lines).Trim();
    }

    private void CommitCampaign(CampaignState original, CampaignState committed)
    {
        var index = State.Campaigns.FindIndex(c => c.Id == original.Id);
        if (index < 0) throw new InvalidOperationException("The active campaign is no longer present in application state.");
        committed.UpdatedAt = DateTimeOffset.UtcNow;
        State.Campaigns[index] = committed;
        OnPropertyChanged(nameof(Campaigns));
        SelectedCampaign = committed;
    }


    private async Task ShortRestAsync()
    {
        if (SelectedCampaign is null || SelectedCharacter is null) return;
        try
        {
            var result = _engine.ShortRest(SelectedCampaign, SelectedCharacter.Id);
            StatusMessage = result.Summary;
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task SpendHitDieAsync()
    {
        if (SelectedCampaign is null || SelectedCharacter is null) return;
        try
        {
            var roll = _dice.Roll($"1d{Math.Max(2, SelectedCharacter.HitDieSides)}").Total;
            var regained = _engine.SpendHitDie(SelectedCampaign, SelectedCharacter.Id, roll);
            StatusMessage = $"Hit Die rolled {roll}; regained {regained} HP.";
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task LongRestAsync()
    {
        if (SelectedCampaign is null || SelectedCharacter is null) return;
        try
        {
            var result = _engine.LongRest(SelectedCampaign, SelectedCharacter.Id);
            StatusMessage = result.Summary;
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task DeathSaveAsync()
    {
        if (SelectedCampaign is null || SelectedCharacter is null) return;
        try
        {
            DeathSaveResult result;
            if (HasActiveCombat && SelectedEncounter is not null)
            {
                var combatant = SelectedEncounter.Combatants.FirstOrDefault(c => c.CharacterId == SelectedCharacter.Id);
                if (combatant is not null)
                    result = _engine.ResolveCombatDeathSavingThrow(SelectedCampaign, SelectedEncounter.Id, combatant.Id, _dice);
                else
                    result = _engine.ResolveDeathSavingThrowWithDice(SelectedCampaign, SelectedCharacter.Id, _dice);
            }
            else
            {
                result = _engine.ResolveDeathSavingThrowWithDice(SelectedCampaign, SelectedCharacter.Id, _dice);
            }

            StatusMessage = result.Summary;
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task GrantTempHpAsync()
    {
        if (SelectedCampaign is null || SelectedCharacter is null) return;
        try
        {
            _engine.GrantTemporaryHitPoints(SelectedCampaign, SelectedCharacter.Id, 5);
            StatusMessage = $"{SelectedCharacter.Name} now has {SelectedCharacter.TempHp} Temporary HP.";
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task BeginConcentrationAsync()
    {
        if (SelectedCampaign is null || SelectedCharacter is null) return;
        try
        {
            var effect = _engine.BeginConcentration(SelectedCampaign, SelectedCharacter.Id, ConcentrationEffectInput);
            ConcentrationEffectInput = "";
            StatusMessage = $"{SelectedCharacter.Name} is concentrating on {effect}.";
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task EndConcentrationAsync()
    {
        if (SelectedCampaign is null || SelectedCharacter is null) return;
        try
        {
            var changed = _engine.EndConcentration(SelectedCampaign, SelectedCharacter.Id, "ended manually from the character sheet");
            StatusMessage = changed ? $"{SelectedCharacter.Name} ended Concentration." : $"{SelectedCharacter.Name} is not concentrating.";
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void RaiseCharacterProperties()
    {
        OnPropertyChanged(nameof(SelectedCharacter));
        OnPropertyChanged(nameof(SelectedCharacterEffectiveSpeed));
        OnPropertyChanged(nameof(SelectedCharacterConditions));
        OnPropertyChanged(nameof(SelectedCharacterDeathSaves));
        OnPropertyChanged(nameof(SelectedCharacterConcentration));
        OnPropertyChanged(nameof(SelectedCharacterOngoingEffects));
        OnPropertyChanged(nameof(PreparedSpells));
        OnPropertyChanged(nameof(SpellTargets));
        OnPropertyChanged(nameof(SpellcastingSummary));
        OnPropertyChanged(nameof(SpellSlotsSummary));
    }

    private async Task CastSelectedSpellAsync()
    {
        if (SelectedCampaign is null || SelectedCharacter is null || SelectedPreparedSpell is null) return;
        try
        {
            int? slotLevel = null;
            if (SelectedPreparedSpell.Level > 0 && !CastAsRitual)
            {
                if (!int.TryParse(SpellSlotLevelInput, out var parsed) || parsed is < 1 or > 9)
                    throw new InvalidOperationException("Enter a spell slot level from 1 to 9.");
                slotLevel = parsed;
            }
            var activeEncounter = SelectedCampaign.Encounters.LastOrDefault(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase));
            SpellCastResult result;
            string[] ResolveAllocationInput(bool requireAtLeastOne)
            {
                var tokens = SpellTargetAllocationInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length == 0 && SelectedSpellTarget is not null) tokens = [SelectedSpellTarget.Id];
                if (requireAtLeastOne && tokens.Length == 0) throw new InvalidOperationException("Select a target or enter one or more exact target names, keys, or IDs.");
                return tokens.Select(token => SelectedCampaign.Characters.FirstOrDefault(c =>
                    c.Id.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(c.Key) && c.Key.Equals(token, StringComparison.OrdinalIgnoreCase)) ||
                    c.Name.Equals(token, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Spell target '{token}' was not found. Use exact character names, keys, or IDs separated by commas."))
                    .Select(c => c.Id).ToArray();
            }

            if (SelectedPreparedSpell.Resolution is "projectile_auto" or "projectile_attack")
            {
                result = _engine.CastProjectileSpell(SelectedCampaign, SelectedCharacter.Id, SelectedPreparedSpell.Id, _dice, ResolveAllocationInput(true), slotLevel, activeEncounter?.Id);
            }
            else if (SelectedPreparedSpell.Resolution == "multi_buff")
            {
                result = _engine.CastMultiTargetSpell(SelectedCampaign, SelectedCharacter.Id, SelectedPreparedSpell.Id, _dice, ResolveAllocationInput(true), slotLevel, activeEncounter?.Id);
            }
            else if (SelectedPreparedSpell.Resolution == "area_save")
            {
                int? centerX = int.TryParse(SpellAreaCenterXInput, out var x) ? x : null;
                int? centerY = int.TryParse(SpellAreaCenterYInput, out var y) ? y : null;
                result = _engine.CastAreaSpell(SelectedCampaign, SelectedCharacter.Id, SelectedPreparedSpell.Id, _dice, centerX, centerY, SpellAreaDirectionInput, slotLevel, activeEncounter?.Id);
            }
            else if (SelectedPreparedSpell.Resolution == "persistent_area")
            {
                int? centerX = int.TryParse(SpellAreaCenterXInput, out var x) ? x : null;
                int? centerY = int.TryParse(SpellAreaCenterYInput, out var y) ? y : null;
                result = _engine.CastPersistentAreaSpell(SelectedCampaign, SelectedCharacter.Id, SelectedPreparedSpell.Id, _dice, centerX, centerY, SpellAreaDirectionInput, slotLevel, activeEncounter?.Id);
            }
            else
            {
                result = _engine.CastSpell(
                    SelectedCampaign,
                    SelectedCharacter.Id,
                    SelectedPreparedSpell.Id,
                    _dice,
                    SelectedSpellTarget?.Id,
                    slotLevel,
                    CastAsRitual,
                    activeEncounter?.Id);
            }
            StatusMessage = result.Summary;
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private static string SpellEffectSummary(SpellDefinition spell)
    {
        if (spell.Resolution.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
            return "Rules metadata loaded; deterministic effect not implemented yet";
        if (spell.Resolution.Equals("attack", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(spell.DamageExpression) ? "Spell attack" : $"Spell attack • {spell.DamageExpression} {spell.DamageType}";
        if (spell.Resolution.Equals("save", StringComparison.OrdinalIgnoreCase))
            return $"{spell.SaveAbility} save • {spell.DamageExpression} {spell.DamageType}".Trim();
        if (spell.Resolution.Equals("healing", StringComparison.OrdinalIgnoreCase))
            return $"Healing • {spell.HealingExpression}{(spell.AddSpellcastingAbilityModifierToHealing ? " + spellcasting modifier" : "")}";
        if (spell.Resolution.Equals("stabilize", StringComparison.OrdinalIgnoreCase))
            return "Stabilizes a living creature at 0 HP";
        if (spell.Resolution.Equals("projectile_auto", StringComparison.OrdinalIgnoreCase))
            return $"{spell.BaseProjectiles} auto-hit projectiles • {spell.DamageExpression} {spell.DamageType} each";
        if (spell.Resolution.Equals("projectile_attack", StringComparison.OrdinalIgnoreCase))
            return $"{spell.BaseProjectiles} spell-attack projectiles • {spell.DamageExpression} {spell.DamageType} each";
        if (spell.Resolution.Equals("area_save", StringComparison.OrdinalIgnoreCase))
            return $"{spell.AreaSizeFeet}-ft {spell.AreaShape} • {spell.SaveAbility} save • {spell.DamageExpression} {spell.DamageType}".Trim();
        if (spell.Resolution.Equals("persistent_area", StringComparison.OrdinalIgnoreCase))
        {
            var upcast = spell.ExtraAreaSizePerSlotFeet > 0 ? $" • +{spell.ExtraAreaSizePerSlotFeet} ft/slot" : "";
            var tags = new List<string>();
            if (spell.BattlefieldHeavilyObscured) tags.Add("Heavily Obscured");
            if (spell.BattlefieldDifficultTerrain) tags.Add("Difficult Terrain");
            if (spell.BattlefieldBlocksLineOfSight) tags.Add("blocks sight");
            var environment = tags.Count == 0 ? "" : $" • {string.Join(", ", tags)}";
            return $"Persistent {spell.AreaSizeFeet}-ft {spell.AreaShape}{upcast}{environment}";
        }
        if (spell.Resolution.Equals("multi_buff", StringComparison.OrdinalIgnoreCase))
        {
            var benefits = new List<string>();
            if (!string.IsNullOrWhiteSpace(spell.AttackRollBonusExpression)) benefits.Add($"attack rolls +{spell.AttackRollBonusExpression}");
            if (!string.IsNullOrWhiteSpace(spell.SavingThrowBonusExpression)) benefits.Add($"saves +{spell.SavingThrowBonusExpression}");
            if (spell.ArmorClassBonus != 0) benefits.Add($"AC {(spell.ArmorClassBonus > 0 ? "+" : "")}{spell.ArmorClassBonus}");
            if (spell.SpeedModifierFeet != 0) benefits.Add($"Speed {(spell.SpeedModifierFeet > 0 ? "+" : "")}{spell.SpeedModifierFeet} ft");
            return $"Up to {spell.BaseTargets} targets • {string.Join(" • ", benefits)}";
        }
        return "Utility / narrative effect";
    }

    private static string GrappleStatus(CampaignState campaign, EncounterState encounter, CombatantState combatant)
    {
        var grappling = encounter.Grapples.Where(g => g.GrapplerCombatantId == combatant.Id).Select(g => encounter.Combatants.FirstOrDefault(c => c.Id == g.TargetCombatantId)).Where(c => c is not null).Select(c => campaign.Characters.FirstOrDefault(ch => ch.Id == c!.CharacterId)?.Name ?? "Unknown").ToArray();
        var grappledBy = encounter.Grapples.Where(g => g.TargetCombatantId == combatant.Id).Select(g => encounter.Combatants.FirstOrDefault(c => c.Id == g.GrapplerCombatantId)).Where(c => c is not null).Select(c => campaign.Characters.FirstOrDefault(ch => ch.Id == c!.CharacterId)?.Name ?? "Unknown").ToArray();
        var parts = new List<string>();
        if (grappling.Length > 0) parts.Add("Grappling: " + string.Join(", ", grappling));
        if (grappledBy.Length > 0) parts.Add("Grappled by: " + string.Join(", ", grappledBy));
        return parts.Count == 0 ? "-" : string.Join(" | ", parts);
    }

    private static string HelpStatus(CampaignState campaign, EncounterState encounter, CombatantState combatant)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(combatant.HelpAttackTargetCombatantId))
        {
            var targetCombatant = encounter.Combatants.FirstOrDefault(c => c.Id == combatant.HelpAttackTargetCombatantId);
            var targetName = campaign.Characters.FirstOrDefault(ch => ch.Id == targetCombatant?.CharacterId)?.Name ?? "Unknown";
            parts.Add("Attack vs " + targetName);
        }
        if (!string.IsNullOrWhiteSpace(combatant.HelpAbilityTargetCharacterId))
        {
            var allyName = campaign.Characters.FirstOrDefault(ch => ch.Id == combatant.HelpAbilityTargetCharacterId)?.Name ?? "Unknown";
            parts.Add($"{combatant.HelpAbilityProficiency} for {allyName}");
        }
        return parts.Count == 0 ? "-" : string.Join(" | ", parts);
    }

    private static string ReadyStatus(CampaignState campaign, EncounterState encounter, CombatantState combatant)
    {
        var ready = combatant.ReadiedAction;
        if (ready is null) return "-";
        if (ready.Kind.Equals("attack", StringComparison.OrdinalIgnoreCase))
        {
            var target = encounter.Combatants.FirstOrDefault(c => c.Id == ready.TargetCombatantId);
            var targetName = campaign.Characters.FirstOrDefault(c => c.Id == target?.CharacterId)?.Name ?? "Unknown";
            var attack = string.IsNullOrWhiteSpace(ready.AttackName) ? "default attack" : ready.AttackName;
            return $"Attack {targetName} with {attack} • {ready.Trigger}";
        }
        if (ready.Kind.Equals("spell", StringComparison.OrdinalIgnoreCase))
        {
            var spell = campaign.Spells.FirstOrDefault(s => s.Id == ready.SpellId);
            var slot = ready.UsedSpellSlot ? $" L{ready.CastAtLevel}" : " cantrip";
            return $"Spell {spell?.Name ?? "Unknown"}{slot} • {ready.Trigger}";
        }
        return $"Move • {ready.Trigger}";
    }

    private static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task ActivateEncounterAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null) return;
        try
        {
            _engine.ActivateEncounter(SelectedCampaign, SelectedEncounter.Id, includeParty: true);
            StatusMessage = $"Encounter '{SelectedEncounter.Name}' is active.";
            RaiseCampaignProperties();
            RefreshCombatSelections();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task RollInitiativeAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null) return;
        try
        {
            foreach (var combatant in SelectedEncounter.Combatants)
            {
                var character = SelectedCampaign.Characters.First(c => c.Id == combatant.CharacterId);
                var mode = combatant.Surprised ? D20RollMode.Disadvantage : D20RollMode.Normal;
                var rolls = _dice.RollD20(mode);
                var dexterity = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(character, "dexterity"));
                var exhaustionPenalty = 2 * Math.Clamp(character.ExhaustionLevel, 0, 6);
                _engine.SetInitiative(SelectedCampaign, SelectedEncounter.Id, combatant.Id, rolls.ChosenRoll + dexterity - exhaustionPenalty);
            }
            _engine.FinalizeInitiative(SelectedCampaign, SelectedEncounter.Id);
            StatusMessage = "Initiative rolled and turn order established.";
            RaiseCampaignProperties();
            RefreshCombatSelections();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task MoveCombatantAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            if (!int.TryParse(CombatMoveXInput, out var x) || !int.TryParse(CombatMoveYInput, out var y))
                throw new InvalidOperationException("Grid X and Y must be whole-number square coordinates.");
            var result = _engine.MoveCombatant(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, x, y);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            RefreshOpportunityAttackSelection();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task TakeDisengageAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            _engine.TakeDisengage(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId);
            StatusMessage = $"{SelectedAttacker.Name} took the Disengage action.";
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task TakeDashAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            var combatant = _engine.TakeDash(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId);
            StatusMessage = $"{SelectedAttacker.Name} took the Dash action and now has {combatant.MovementRemainingFeet} feet of movement remaining.";
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task TakeDodgeAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            _engine.TakeDodge(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId);
            StatusMessage = $"{SelectedAttacker.Name} took the Dodge action.";
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task TakeHideAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            var result = _engine.TakeHide(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, _dice);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task SearchHiddenAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var result = _engine.SearchForHiddenCombatant(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedTarget.CombatantId, _dice);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task ReadyAttackAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var result = _engine.TakeReadyAttack(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedTarget.CombatantId, ReadyTriggerInput);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task ReadyMoveAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            var result = _engine.TakeReadyMove(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, ReadyTriggerInput);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task ReadySpellAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedCombatPreparedSpell is null) return;
        try
        {
            int? slotLevel = null;
            if (SelectedCombatPreparedSpell.Level > 0)
            {
                if (!int.TryParse(SpellSlotLevelInput, out var parsed) || parsed is < 1 or > 9)
                    throw new InvalidOperationException("Enter a spell slot level from 1 to 9.");
                slotLevel = parsed;
            }
            var result = _engine.TakeReadySpell(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedCombatPreparedSpell.Id, ReadyTriggerInput, slotLevel);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task TriggerReadiedActionAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            var combatant = SelectedEncounter.Combatants.FirstOrDefault(c => c.Id == SelectedAttacker.CombatantId)
                ?? throw new InvalidOperationException("Selected combatant is no longer in the encounter.");
            var ready = combatant.ReadiedAction ?? throw new InvalidOperationException($"{SelectedAttacker.Name} has no readied action waiting for a trigger.");
            if (ready.Kind.Equals("attack", StringComparison.OrdinalIgnoreCase))
            {
                var result = _engine.TriggerReadiedAttack(SelectedCampaign, SelectedEncounter.Id, combatant.Id, _dice);
                StatusMessage = result.Summary;
            }
            else if (ready.Kind.Equals("move", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(CombatMoveXInput, out var x) || !int.TryParse(CombatMoveYInput, out var y))
                    throw new InvalidOperationException("Grid X and Y must be whole-number square coordinates for readied movement.");
                var result = _engine.TriggerReadiedMove(SelectedCampaign, SelectedEncounter.Id, combatant.Id, x, y);
                StatusMessage = result.Summary;
                RefreshOpportunityAttackSelection();
            }
            else if (ready.Kind.Equals("spell", StringComparison.OrdinalIgnoreCase))
            {
                var result = _engine.TriggerReadiedSpell(SelectedCampaign, SelectedEncounter.Id, combatant.Id, _dice, SelectedTarget?.CombatantId);
                StatusMessage = result.Summary;
            }
            else throw new InvalidOperationException($"Unsupported readied action kind '{ready.Kind}'.");

            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task HelpAttackAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            _engine.TakeHelpAttack(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedTarget.CombatantId);
            StatusMessage = $"{SelectedAttacker.Name} used Help to distract {SelectedTarget.Name}.";
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task HelpAbilityCheckAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            _engine.TakeHelpAbilityCheck(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedTarget.CombatantId, CombatSkillInput);
            StatusMessage = $"{SelectedAttacker.Name} used Help for {SelectedTarget.Name}'s next {CombatSkillInput} check.";
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task FirstAidAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var result = _engine.TakeFirstAid(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedTarget.CombatantId, _dice);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task CombatSkillActionAsync(string action)
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            if (!int.TryParse(CombatDcInput, out var dc) || dc < 1)
                throw new InvalidOperationException("The action DC must be a positive whole number.");
            var result = action switch
            {
                "search" => _engine.TakeSearchAction(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, CombatSkillInput, dc, _dice),
                "study" => _engine.TakeStudyAction(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, CombatSkillInput, dc, _dice),
                "influence" => _engine.TakeInfluenceAction(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, CombatSkillInput, dc, _dice),
                _ => throw new InvalidOperationException("Unknown combat skill action.")
            };
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task GrappleAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var result = _engine.ResolveUnarmedGrapple(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedTarget.CombatantId, _dice);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task ShoveAsync(string effect)
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var result = _engine.ResolveUnarmedShove(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedTarget.CombatantId, effect, _dice);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task EscapeGrappleAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var result = _engine.EscapeGrapple(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedTarget.CombatantId, "athletics", _dice);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task ReleaseGrappleAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            StatusMessage = _engine.ReleaseGrapple(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, SelectedTarget.CombatantId);
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task StandFromProneAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            var combatant = _engine.StandFromProne(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId);
            StatusMessage = $"{SelectedAttacker.Name} stood from Prone and has {combatant.MovementRemainingFeet} feet of movement remaining.";
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task ResolveOpportunityAttackAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedOpportunityAttack is null) return;
        try
        {
            var result = _engine.ResolveOpportunityAttack(SelectedCampaign, SelectedEncounter.Id, SelectedOpportunityAttack.ReactorCombatantId, null, _dice);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            RefreshOpportunityAttackSelection();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task DeclineOpportunityAttackAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedOpportunityAttack is null) return;
        try
        {
            StatusMessage = _engine.DeclineOpportunityAttack(SelectedCampaign, SelectedEncounter.Id, SelectedOpportunityAttack.ReactorCombatantId);
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            RefreshOpportunityAttackSelection();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task CombatAttackAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var result = _engine.ResolveEncounterAttack(
                SelectedCampaign,
                SelectedEncounter.Id,
                SelectedAttacker.CombatantId,
                SelectedTarget.CombatantId,
                attackName: null,
                _dice);
            StatusMessage = result.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task NextCombatTurnAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null) return;
        try
        {
            var current = _engine.NextTurn(SelectedCampaign, SelectedEncounter.Id, _dice);
            var character = SelectedCampaign.Characters.First(c => c.Id == current.CharacterId);
            StatusMessage = $"Round {SelectedEncounter.Round}: {character.Name}'s turn.";
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: false);
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task EndEncounterAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null) return;
        try
        {
            _engine.EndEncounter(SelectedCampaign, SelectedEncounter.Id);
            StatusMessage = $"Encounter '{SelectedEncounter.Name}' ended.";
            RaiseCampaignProperties();
            await SaveAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void RefreshCombatSelections(bool keepSelection = false)
    {
        var combatants = Combatants.ToArray();
        if (!keepSelection || SelectedAttacker is null)
            SelectedAttacker = combatants.FirstOrDefault(c => !c.Dead);
        else
            SelectedAttacker = combatants.FirstOrDefault(c => c.CombatantId == SelectedAttacker.CombatantId) ?? combatants.FirstOrDefault(c => !c.Dead);

        if (!keepSelection || SelectedTarget is null)
            SelectedTarget = combatants.FirstOrDefault(c => !c.Dead && c.CombatantId != SelectedAttacker?.CombatantId);
        else
            SelectedTarget = combatants.FirstOrDefault(c => c.CombatantId == SelectedTarget.CombatantId) ?? combatants.FirstOrDefault(c => !c.Dead && c.CombatantId != SelectedAttacker?.CombatantId);

        OnPropertyChanged(nameof(Combatants));
        OnPropertyChanged(nameof(CombatStatus));
        RefreshOpportunityAttackSelection();
    }

    private void RefreshOpportunityAttackSelection()
    {
        var pending = PendingOpportunityAttacks.ToArray();
        SelectedOpportunityAttack = pending.FirstOrDefault(x => SelectedOpportunityAttack is not null && x.ReactorCombatantId == SelectedOpportunityAttack.ReactorCombatantId)
            ?? pending.FirstOrDefault();
        OnPropertyChanged(nameof(PendingOpportunityAttacks));
        OnPropertyChanged(nameof(PendingMoveSummary));
    }

    private async Task BuySelectedItemAsync()
    {
        if (SelectedCampaign is null || SelectedCharacter is null || SelectedMerchant is null || SelectedStock is null) return;
        var result = _engine.Purchase(SelectedCampaign, SelectedCharacter.Id, SelectedMerchant.Id, SelectedStock.ItemId, 1);
        StatusMessage = result.Message;
        OnPropertyChanged(nameof(SelectedCharacter)); OnPropertyChanged(nameof(SelectedMerchantStock)); RaiseCampaignProperties(); await SaveAsync();
    }

    private void RaiseCampaignProperties()
    {
        if (SelectedCampaign is not null) _engine.EnsurePendingPlayerRollForActiveCombat(SelectedCampaign);
        _rehearsalReport = null;
        OnPropertyChanged(nameof(CurrentLocationName));
        OnPropertyChanged(nameof(CampaignTime));
        OnPropertyChanged(nameof(CampaignSummary));
        OnPropertyChanged(nameof(ReadinessIssues));
        OnPropertyChanged(nameof(ReadinessSummary));
        OnPropertyChanged(nameof(RehearsalFindings));
        OnPropertyChanged(nameof(RehearsalSummary));
        OnPropertyChanged(nameof(ExpansionSummary));
        OnPropertyChanged(nameof(Characters));
        OnPropertyChanged(nameof(Locations));
        OnPropertyChanged(nameof(Quests));
        OnPropertyChanged(nameof(RecentEvents));
        OnPropertyChanged(nameof(Chat));
        OnPropertyChanged(nameof(SessionChat));
        OnPropertyChanged(nameof(PartyCharacters));
        OnPropertyChanged(nameof(CurrentLocationDescription));
        OnPropertyChanged(nameof(HasActiveCombat));
        OnPropertyChanged(nameof(PlaySceneModeTitle));
        OnPropertyChanged(nameof(ActiveTurnName));
        OnPropertyChanged(nameof(ActiveTurnCharacter));
        OnPropertyChanged(nameof(ActiveTurnCombatant));
        OnPropertyChanged(nameof(PendingPlayerRoll));
        OnPropertyChanged(nameof(PlayerRollRequired));
        OnPropertyChanged(nameof(RollD20ButtonText));
        OnPropertyChanged(nameof(PendingPlayerRollPrompt));
        OnPropertyChanged(nameof(PlayerDeathSaveRequired));
        OnPropertyChanged(nameof(ActivePlayerUnableToActAtZero));
        OnPropertyChanged(nameof(ActiveDeathSaveStatus));
        OnPropertyChanged(nameof(ActiveDeathSavePrompt));
        OnPropertyChanged(nameof(ActiveTurnSummary));
        OnPropertyChanged(nameof(Merchants));
        OnPropertyChanged(nameof(Encounters));
        OnPropertyChanged(nameof(FactionDisplays));
        OnPropertyChanged(nameof(SecretDisplays));
        OnPropertyChanged(nameof(RelationshipDisplays));
        OnPropertyChanged(nameof(TimelineDisplays));
        OnPropertyChanged(nameof(GeneratedDetails));
        OnPropertyChanged(nameof(Combatants));
        OnPropertyChanged(nameof(CombatStatus));
        OnPropertyChanged(nameof(PendingOpportunityAttacks));
        OnPropertyChanged(nameof(PendingMoveSummary));
        OnPropertyChanged(nameof(PreparedSpells));
        OnPropertyChanged(nameof(SpellTargets));
        OnPropertyChanged(nameof(SpellcastingSummary));
        OnPropertyChanged(nameof(SpellSlotsSummary));
        OnPropertyChanged(nameof(SelectedCharacterOngoingEffects));
        OnPropertyChanged(nameof(SpellLibrary));
        MapRevision++;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() => _runtime.Dispose();
}
