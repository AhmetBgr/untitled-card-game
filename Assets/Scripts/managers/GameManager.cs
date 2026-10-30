using System; 
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

public enum GameState { Setup, StartGame, PlayerTurn, OpponentTurn, EndGame }

public class GameManager : Singleton<GameManager>
{
    public Player player;
    public Agent opponent;
    public SwitchController switchController;   
    public GameObject minionprefab;
    public GameObject rangedMinionprefab;
    public Transform discardPile;
    public bool isPlayingCard = false;
    public IEnumerator curaction;

    private bool cancelPlayingCardRequested = false;
    private CardController playingCard = null;

    /// <summary>
    /// The card currently being played, or null when no card play is in progress. Read-only; the general
    /// targeting arrow reads this so it can originate from the card while the player picks a cell/minion.
    /// </summary>
    public CardController PlayingCard => playingCard;
    private Agent playingAgent = null;
    private int playingAgentManaBeforePlay = 0;
    private int playingCardHandLayoutIndex = -1;

    public TextMeshProUGUI[] playerCornerDamageTexts;
    public TextMeshProUGUI[] opponentCornerDamageTexts;

    public List<SelectableEntity> selectables = new List<SelectableEntity>();
    private Queue<IEnumerator> actionQueue = new Queue<IEnumerator>();
    private Queue<IEnumerator> onTurnStartActions = new Queue<IEnumerator>();
    private Queue<IEnumerator> onTurnEndActions = new Queue<IEnumerator>();
    private Queue<IEnumerator> onCardDrawActions = new Queue<IEnumerator>();
    private Queue<IEnumerator> onMinionDeathActions = new Queue<IEnumerator>();
    private Queue<IEnumerator> onMinionCollidedActions = new Queue<IEnumerator>();
    private Queue<IEnumerator> onMinionTookDamageActions = new Queue<IEnumerator>();
    private Queue<IEnumerator> onHeroAttackedActions = new Queue<IEnumerator>();
    private Queue<IEnumerator> onAnyMinionSummonedActions = new Queue<IEnumerator>();
    private Queue<IEnumerator> onThisMinionSummonedActions = new Queue<IEnumerator>();
    private readonly List<HeroPassiveSO> _heroPassiveMatchBuffer = new List<HeroPassiveSO>();

    // Triggered-action execution uses shared ActionHolder globals. To prevent different triggers
    // (death, took damage, collision, etc.) from stomping each other's state mid-execution,
    // we serialize all triggered-action coroutines through this scheduler.
    private bool _executingTriggeredActions = false;
    private readonly Queue<Action> _pendingTriggeredCallbacks = new Queue<Action>();

    // True while any triggered action is running or still queued to run. Callers that mutate the shared
    // ActionHolder selection globals (the AI turn loop, card plays, turn-end processing) must wait for
    // this to clear before proceeding, or they will clobber an in-flight trigger's state mid-execution
    // (e.g. a minion that kills itself attacking, whose OnDeath resolves a frame later).
    public bool HasInFlightTriggeredActions => _executingTriggeredActions || _pendingTriggeredCallbacks.Count > 0;

    [SerializeField] private Queue<IEnumerator> defaultActionQueue;

    public GameState currentState;

    [Header("Mana")]
    [Tooltip("Max mana the player gets on their FIRST turn. Every turn of theirs after that adds one, up to the cap.")]
    [SerializeField] private int playerStartingMana = 1;
    [Tooltip("What the player starts on in the tutorial match instead, so the opening turn has enough mana to actually play something and follow along.")]
    [SerializeField] private int tutorialPlayerStartingMana = 2;
    [Tooltip("The opponent's equivalent. Separate from the player's so the AI can be handed an easier or harder opening.")]
    [SerializeField] private int opponentStartingMana = 1;
    [Tooltip("Neither side's max mana grows past this.")]
    [SerializeField] private int manaCap = 10;

    // The tutorial only changes where the player's ramp STARTS — from there it climbs by one a turn
    // like any other match, so the head start narrows rather than compounding.
    private int PlayerStartingMana => GauntletRun.StartingManaFor(SelectionSide.Player,
        IsTutorialMatch ? tutorialPlayerStartingMana : playerStartingMana);

    // Forged in Battle's Head Start blessing raises it; otherwise the configured value.
    private int OpponentStartingMana => GauntletRun.StartingManaFor(SelectionSide.Opponent, opponentStartingMana);

    // "This side hasn't taken a turn yet", so their next turn opens on their starting mana rather
    // than one more than last turn's.
    private const int NoTurnTakenYet = -1;

    /// <summary>
    /// Max mana the player refills to at the start of their turn, and the denominator the mana bar
    /// shows. Grows on the player's turns only.
    /// </summary>
    public int PlayerMaxMana { get; private set; } = NoTurnTakenYet;

    /// <summary>
    /// The opponent's own max mana, grown on the opponent's turns only. Tracked separately from
    /// <see cref="PlayerMaxMana"/> rather than shared, so the two sides can ramp from different
    /// starting points; with equal starting values the two stay in lockstep, as they always did.
    /// </summary>
    public int OpponentMaxMana { get; private set; } = NoTurnTakenYet;

    public bool isPlayerTurn;

    /// <summary>
    /// True only while the player actually owns the turn. isPlayerTurn alone is not enough: it flips back
    /// to true at the end of OpponentTurn while the AI's minion movement phase is still running, and
    /// currentState alone is not enough either (it stays OpponentTurn through that same window). Every
    /// player-initiated card play gates on this so a hand card can't be dropped during the AI's turn.
    /// </summary>
    public bool CanPlayerPlayCards => isPlayerTurn && currentState == GameState.PlayerTurn;

    public bool isTesting = false;
    public bool isTestingFailed = false;
    public static event Action<GameState> OnTurnEnd;
    public static event Action<GameState> OnTurnStarted;
    public static event Action<MinionController> OnMinionSummoned;
    // Fired once when a card play successfully commits (past the cancel checks). Purely additive:
    // consumed by the stats system; no core logic depends on it.
    public static event Action<Agent, CardSO> OnCardPlayed;
    // Fired once per play, the instant the card stops ASKING and starts DOING: immediately before the
    // first queued action that is not part of the card's targeting (and before the very first action
    // for a card that asks for nothing). OnCardPlayed is the wrong hook for anything that has to land
    // with the effect rather than with its outcome -- it fires only once the whole queue has drained,
    // which for the opponent is seconds later, since ExecuteActions waits 0.35s before every action.
    // The start of the play is equally wrong: that is before the player has even picked a target.
    // Purely additive (audio only). A play cancelled during targeting never raises it; one cancelled
    // after its effects began does, and OnCardPlayCancelled is the counterpart for undoing that.
    public static event Action<Agent, CardSO> OnCardEffectsStarting;
    // The other half of that pair: fired when a play lands in the play area and has to WAIT, because
    // the card opens with a prompt for the player to answer. Raised only for a card that actually asks
    // for something -- its queue is fully built by OnPlay before a single action runs, so whether it
    // will ask is known here. Every raise of this is followed by exactly one of OnCardEffectsStarting
    // (the wait was answered) or OnCardPlayCancelled (it was backed out of), which is what lets a
    // listener hold something for the duration. Purely additive (audio only).
    public static event Action<Agent, CardSO> OnCardTargetingStarted;
    // The mirror of OnCardPlayed: the play was backed out of and the card has just been put back in
    // hand. Also purely additive (audio only). Fires for the AI's aborted plays too, so listeners that
    // only care about the visible hand must check the agent.
    public static event Action<Agent, CardSO> OnCardPlayCancelled;

    private readonly HeroPassiveSystem heroPassives = new HeroPassiveSystem();

    /// <summary>
    /// True while the match being played is the one-off tutorial match — what the tutorial-only UI
    /// (the cards' stat-naming hints) keys off.
    ///
    /// Latched once at scene load rather than read live from the save: <see cref="CheckWinCondition"/>
    /// flips the save flag the instant the match ends, and the hints shouldn't blink off mid-match
    /// because the final blow landed while a card was hovered.
    ///
    /// Resolves itself on first read as well as in Awake, because <see cref="Agent.ApplySavedSelection"/>
    /// asks during ITS Awake to pick the tutorial deck, and execution order between the two components
    /// isn't defined. Both paths read the same save flag, which nothing touches before the match ends,
    /// so whichever wins the race gets the same answer.
    /// </summary>
    public static bool IsTutorialMatch
    {
        get
        {
            if (isTutorialMatch == null) isTutorialMatch = ResolveIsTutorialMatch();
            return isTutorialMatch.Value;
        }
    }

    private static bool? isTutorialMatch;

    // A Forged in Battle match is never the tutorial, even before the tutorial has been played.
    private static bool ResolveIsTutorialMatch() => !SaveManager.Instance.IsTutorial && !GauntletRun.IsActive;

    // Statics survive between play sessions when domain reload is off, so clear the cache before each
    // run. Without this, a stale answer from the previous session could be handed to Agent.Awake, which
    // reads this and may run before GameManager.Awake gets to re-resolve it.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetTutorialMatchCache() => isTutorialMatch = null;

    /// <summary>How many of the player's turns the tutorial's stat hints keep appearing for.</summary>
    public const int TutorialHintTurns = 2;

    /// <summary>Player turns begun so far this match; 0 until the first one starts.</summary>
    public int PlayerTurnsStarted { get; private set; }

    /// <summary>
    /// Should a hovered hand card show the tutorial's stat hints? True for the player's first
    /// <see cref="TutorialHintTurns"/> turns of the tutorial match, then never again. Counted in
    /// player turns rather than hovers so the hints stay available for as long as the player is
    /// finding their feet, however much or little they mouse around in that time.
    /// </summary>
    public bool ShouldShowTutorialHints => IsTutorialMatch && PlayerTurnsStarted <= TutorialHintTurns;

    protected override void Awake()
    {
        base.Awake();

        // Every static that outlives the scene is cleared here, before anything in this match can touch
        // it. A Replay/Restart is a plain scene load, so without this the previous match's selection
        // state, its event subscribers and its open selection request all carry over — see
        // ActionHolder.ResetForNewMatch for what that did to the player's cell picks.
        ActionHolder.ResetForNewMatch();
        SelectionManager.ResetForNewMatch();

        // Static, so it outlives the scene: re-resolve per match rather than letting a Replay inherit
        // the previous one's answer.
        isTutorialMatch = ResolveIsTutorialMatch();
    }

    void Start()
    {
        // A Forged in Battle run left on its end-of-match screen comes back to that screen, not to a new
        // match: nothing is set up or dealt, and the panel is rebuilt from the saved battle.
        if (GauntletRun.RestoreResultsPending)
        {
            currentState = GameState.EndGame;
            PopupManager.Instance.OpenRestoredResults();
        }
        // Between battles -- a new run, or one resumed there -- the run's draft opens on the end-of-match
        // panel instead of a match: the hero, the deck, then the way into the next fight.
        else if (GauntletRun.IsActive && GauntletRun.Location == GauntletRun.RunLocation.Draft)
        {
            currentState = GameState.EndGame;

            // The board stays empty through the draft: the heroes come on once the fight starts.
            if (player != null && player.hero != null) player.hero.gameObject.SetActive(false);
            if (opponent != null && opponent.hero != null) opponent.hero.gameObject.SetActive(false);

            PopupManager.Instance.OpenDraft();
        }
        else
        {
            StartCoroutine(GameLoop());
        }

        MinionController.OnDied += OnMinionDied;
        MinionController.OnCollided += OnMinionCollided;
        MinionController.OnTookDamage += OnMinionTookDamage;
        OnMinionSummoned += OnMinionSummonedForLog;
    }
    private void OnDestroy()
    {
        MinionController.OnDied -= OnMinionDied;
        MinionController.OnCollided -= OnMinionCollided;
        MinionController.OnTookDamage -= OnMinionTookDamage;
        OnMinionSummoned -= OnMinionSummonedForLog;
        heroPassives.Clear();

        // The Game scene is going away: back to the menu, a replay, or the game shutting down.
        MarkTutorialPlayed();
    }

    // Quitting tears the scene down too, but this runs BEFORE any OnDestroy, while SaveManager is
    // certainly still alive — teardown order between the two isn't defined, and the tutorial must not
    // survive being quit out of.
    private void OnApplicationQuit()
    {
        MarkTutorialPlayed();
    }



    private void Update()
    {
        if (currentState == GameState.EndGame) return;

        // Paused. Time.timeScale stops the coroutines but not this poll, so a right-click behind the pause
        // menu would otherwise still back out of the card being played.
        if (EscMenuController.IsOpen) return;

        if (isPlayerTurn && Input.GetMouseButtonDown(1))
        {
            // Right-click backs out: cancel the card being played, otherwise cancel an active
            // attack/minion selection.
            if (isPlayingCard) CancelPlayingCard();
            else SelectionManager.Instance.Cancel();
        }

        CheckWinCondition();
    }
    IEnumerator GameLoop()
    {
        yield return StartCoroutine(SetupGame());

        while (currentState != GameState.EndGame)
        {
            if (isPlayerTurn)
            {
                /*for (int i = player.minions.Count - 1; i >= 0; i--)
                {
                    if (player.minions[i] == null)
                    {
                        player.minions.RemoveAt(i);
                    }
                }
                for (int i = opponent.minions.Count - 1; i >= 0; i--)
                {
                    if (opponent.minions[i] == null)
                    {
                        opponent.minions.RemoveAt(i);
                    }
                }*/
                yield return StartCoroutine(PlayerTurn());


            }
            else
            {
                /*for (int i = opponent.minions.Count - 1; i >= 0; i--)
                {
                    if (opponent.minions[i] == null)
                    {
                        opponent.minions.RemoveAt(i);
                    }
                }
                for (int i = player.minions.Count - 1; i >= 0; i--)
                {
                    if (player.minions[i] == null)
                    {
                        player.minions.RemoveAt(i);
                    }
                }*/
                yield return new WaitForSeconds(0.5f);

                GridManager.Instance.InvokeGridChanged();

                yield return StartCoroutine(OpponentTurn());


            }
        }
    }

    /// <summary>
    /// The passives added for <paramref name="side"/> on top of the hero's own: a Forged in Battle
    /// enemy's Extra Passive blessings, else the setup screen's picks, or null unless the
    /// multiple-passives modifier is on for this match.
    /// </summary>
    static IReadOnlyList<HeroPassiveSO> ExtraPassivesFor(SelectionSide side)
    {
        if (GauntletRun.IsActive) return GauntletRun.ExtraPassivesFor(side);
        if (!MatchModifiers.ExtraPassivesEnabled || SaveManager.Instance == null || HeroDatabase.Instance == null) return null;
        return HeroDatabase.Instance.ResolvePassives(SaveManager.Instance.GetExtraPassives(side));
    }

    IEnumerator SetupGame()
    {
        // Seed the hero statlines BEFORE registering passives. Register stamps the standing self
        // modifiers of a hero's passives onto hero.modal (the Summoner's -2 Attack), and
        // HeroController.Initialize re-seeds that same modal from the CardSO. This code runs inline
        // from GameManager.Start() — GameLoop's first segment executes during StartCoroutine, it does
        // not wait a frame — so HeroController.Start() may not have run yet, and the stamp would then
        // be overwritten by it. EnsureInitialized is idempotent, so ordering stops mattering.
        // hero.card/owner are safe to read here either way: Agent sets them in Awake().
        if (player != null && player.hero is HeroController playerHero) playerHero.EnsureInitialized();
        if (opponent != null && opponent.hero is HeroController opponentHero) opponentHero.EnsureInitialized();

        // Match modifiers scale the seeded statline (hero health / attack multipliers) here, between
        // the seed and the passives, so a passive's own stat stamp lands on top of the scaled base.
        // No-ops when no mode is active.
        MatchModifiers.ApplyHeroSetup(player != null ? player.hero : null, SelectionSide.Player);
        MatchModifiers.ApplyHeroSetup(opponent != null ? opponent.hero : null, SelectionSide.Opponent);

        // Forged in Battle scales the enemy's health per encounter and arms its blessings. No-op outside a run.
        GauntletRun.ApplyEnemySetup(opponent != null ? opponent.hero : null);

        heroPassives.Register(player != null ? player.hero : null, ExtraPassivesFor(SelectionSide.Player),
            GauntletRun.OwnPassivesFor(SelectionSide.Player));
        heroPassives.Register(opponent != null ? opponent.hero : null, ExtraPassivesFor(SelectionSide.Opponent),
            GauntletRun.OwnPassivesFor(SelectionSide.Opponent));

        // ...and carries the player's hero over from the last won battle: health, attack, passive state.
        // After Register, so the carried attack replaces the passives' self-stat stamp instead of
        // stacking it again. No-op outside a run and before the first win.
        GauntletRun.RestorePlayerHero(player != null ? player.hero : null);

        // The match-up, over the board: it hides the heroes and their passives, waits for a click, then
        // lands them on the board. Nothing is dealt until it has.
        if (VersusIntro.Instance != null) yield return VersusIntro.Instance.Play(player, opponent);

        switchController.PlaySwitchAnim(true);
        currentState = GameState.Setup;
        //Debug.Log("Setting up game...");
        yield return new WaitForSeconds(0.5f);

        // A blessed enemy may start with a minion on the board. After Register, so hero auras stamp it,
        // and after the wait above: the board's cells register with the grid in their own Start, which
        // has not run yet when this coroutine begins.
        GauntletRun.SummonStartingMinion(opponent);

        player.DrawCard();
        yield return new WaitForSeconds(0.5f);

        opponent.DrawCard();
        yield return new WaitForSeconds(0.5f);

        player.DrawCard();
        yield return new WaitForSeconds(0.5f);

        opponent.DrawCard();
        yield return new WaitForSeconds(0.5f);

        player.DrawCard();
        yield return new WaitForSeconds(0.5f);

        opponent.DrawCard();
        yield return new WaitForSeconds(0.5f);

        // Forged in Battle's Bigger Hand blessing. 0 outside a run.
        for (int i = GauntletRun.ExtraStartingCardsFor(SelectionSide.Player); i > 0; i--)
        {
            player.DrawCard();
            yield return new WaitForSeconds(0.5f);
        }
        for (int i = GauntletRun.ExtraStartingCardsFor(SelectionSide.Opponent); i > 0; i--)
        {
            opponent.DrawCard();
            yield return new WaitForSeconds(0.5f);
        }

        /*player.DrawCard(true);
        yield return new WaitForSeconds(0.25f);

        opponent.DrawCard(false);
        yield return new WaitForSeconds(0.25f);

        */
        isPlayerTurn = true;

        currentState = GameState.StartGame;
        //Debug.Log("Game Started!");
        //OnTurnSwitch?.Invoke(currentState);

        yield return null;
    }

    IEnumerator PlayerTurn()
    {
        currentState = GameState.PlayerTurn;
        PlayerTurnsStarted++;
        // Grown here rather than in GameLoop, so each side's ramp lives with the turn it belongs to.
        PlayerMaxMana = GrowMaxMana(PlayerMaxMana, PlayerStartingMana);
        player.availibleMana = PlayerMaxMana;
        player.curState = Player.State.Waiting;
        //Debug.Log("Player's Turn");
        yield return StartCoroutine(DrawTurnStartCards(player));
        // Queue turn-start through the same serializer as the draw above so it runs AFTER the on-draw
        // pass completes, instead of racing it and clobbering shared ActionHolder state.
        EnqueueTriggeredAction(() => StartCoroutine(InvokeOnTurnStarted()));
        // Let the on-draw + turn-start passes fully resolve before play begins, so card plays don't
        // run concurrently with (and clobber) an in-flight trigger's shared ActionHolder selection state.
        yield return new WaitUntil(() => !HasInFlightTriggeredActions);
        yield return StartCoroutine(player.PlayTurn());

        yield return new WaitForSeconds(0.5f);
    }

    /// <summary>
    /// A side's max mana for the turn about to start: their configured starting mana on their first
    /// turn, one more than last turn on every turn after, never past <see cref="manaCap"/>. Shared by
    /// both sides so their ramps can't drift apart in anything but their starting value.
    /// </summary>
    private int GrowMaxMana(int current, int startingMana) =>
        Mathf.Clamp(current == NoTurnTakenYet ? startingMana : current + 1, 0, manaCap);

    public IEnumerator InvokeOnTurnEnd()
    {
        // A turn boundary aborts any in-progress selection so it can't leak into the next turn.
        SelectionManager.Instance.Cancel();
        ActionLogPanel.Instance?.AddTurnSpacer();
        OnTurnEnd?.Invoke(currentState);

        yield break;
    }
    /// <summary>
    /// The agent's turn-start draw, plus any extra cards a delayed effect owes it (Do Nothing). The
    /// debt is consumed up front so it is paid exactly once even if a draw fizzles on the hand cap.
    /// Runs before InvokeOnTurnStarted is queued, so the extra card is in hand before any OnTurnStart
    /// trigger reads it, and each draw's on-draw pass (turrets) still serializes behind the last.
    /// </summary>
    private IEnumerator DrawTurnStartCards(Agent agent)
    {
        // Yielded on, not fired off: on an empty deck the draw opens a card-selection panel and waits for
        // a pick, so the turn must not proceed underneath it.
        yield return StartCoroutine(agent.DrawCardRoutine());

        int extra = agent.ConsumePendingExtraDraws();
        for (int i = 0; i < extra; i++)
        {
            yield return new WaitForSeconds(0.35f);
            yield return StartCoroutine(agent.DrawCardRoutine());
        }
    }

    public IEnumerator InvokeOnTurnStarted()
    {
        OnTurnStarted?.Invoke(currentState);

        Agent currentAgent = currentState == GameState.PlayerTurn ? (Agent)player : opponent;

        bool iterateForward = currentState == GameState.PlayerTurn;

        int xStart = iterateForward ? 0 : GridManager.Instance.GridWidth - 1;
        int xEnd = iterateForward ? GridManager.Instance.GridWidth : -1;
        int xStep = iterateForward ? 1 : -1;

        int yStart = iterateForward ? 0 : GridManager.Instance.GridHeight - 1;
        int yEnd = iterateForward ? GridManager.Instance.GridHeight : -1;
        int yStep = iterateForward ? 1 : -1;

        // Hold the triggered-action lock for the WHOLE pass (not per grid cell). Triggered passes share
        // ActionHolder's static selection state (thisMinion / selectedTargetMinions / ...); releasing the
        // lock between minions would let a queued sibling pass start and clobber an in-flight one mid-way.
        _executingTriggeredActions = true;
        try
        {
            for (int x = xStart; x != xEnd; x += xStep)
            {
                for (int y = yStart; y != yEnd; y += yStep)
                {
                    Cell cell = GridManager.Instance.GetCell(new Vector2Int(x, y));

                    MinionController minion;

                    // Skip the hero here: it is handled by the dedicated hero block below. The hero's
                    // GridEntity is type Obj so it occupies a grid cell, and without this guard its
                    // OnTurnStart would fire twice (once here, once in the dedicated block).
                    if (cell.obj != null && cell.obj.TryGetComponent(out minion) && minion.owner == currentAgent && minion != currentAgent.hero)
                    {
                        using (ActionHolder.PushScope())
                        {
                            onTurnStartActions.Clear();
                            ActionHolder.ResetSelections();
                            ActionHolder.thisMinion = minion;
                            ActionHolder.thisCardSO = minion.card;
                            ActionHolder.thisCard = null;
                            ActionHolder.selectedMinions.Add(minion);
                            ActionHolder.selectedAgent = currentAgent;
                            ActionHolder.curActionsList = onTurnStartActions;

                            this.isTesting = false;
                            minion.modal.OnTurnStart.Invoke();

                            yield return StartCoroutine(ExecuteActions(onTurnStartActions));
                        }
                    }
                }
            }

            // Hero turn start
            MinionController hero = currentAgent.hero;
            if (hero != null)
            {
                using (ActionHolder.PushScope())
                {
                    onTurnStartActions.Clear();
                    ActionHolder.ResetSelections();
                    ActionHolder.thisMinion = hero;
                    ActionHolder.thisCardSO = hero.card;
                    ActionHolder.thisCard = null;
                    ActionHolder.selectedMinions.Add(hero);
                    ActionHolder.selectedAgent = currentAgent;
                    ActionHolder.curActionsList = onTurnStartActions;

                    this.isTesting = false;
                    hero.modal.OnTurnStart.Invoke();

                    yield return StartCoroutine(ExecuteActions(onTurnStartActions));
                }
            }
        }
        finally
        {
            FinishTriggeredAction();
        }
    }

    public void TriggerCardDrawActions(Agent drawingAgent)
    {
        EnqueueTriggeredAction(() => StartCoroutine(InvokeOnCardDrawActions(drawingAgent)));
    }

    private IEnumerator InvokeOnCardDrawActions(Agent drawingAgent)
    {
        // Lock acquired up front (before any yield) so a turn-start/turn-end pass enqueued right after
        // the draw waits for this whole pass instead of clobbering a turret's in-flight select->attack.
        _executingTriggeredActions = true;
        try
        {
            for (int x = 0; x < GridManager.Instance.GridWidth; x++)
            {
                for (int y = 0; y < GridManager.Instance.GridHeight; y++)
                {
                    Cell cell = GridManager.Instance.GetCell(new Vector2Int(x, y));

                    MinionController minion;

                    // "Whenever you draw a card": a turret triggers when its own owner drew, regardless of
                    // whose turn it is (covers off-turn / card-effect draws, not just the start-of-turn draw).
                    if (cell.obj != null && cell.obj.TryGetComponent(out minion) && minion.owner == drawingAgent)
                    {
                        using (ActionHolder.PushScope())
                        {
                            onCardDrawActions.Clear();
                            ActionHolder.ResetSelections();
                            ActionHolder.thisMinion = minion;
                            ActionHolder.thisCardSO = minion.card;
                            ActionHolder.thisCard = null;
                            ActionHolder.selectedMinions.Add(minion);
                            ActionHolder.selectedAgent = minion.owner;
                            ActionHolder.curActionsList = onCardDrawActions;

                            this.isTesting = false;
                            minion.modal.OnOwnerDrawedCard.Invoke();

                            yield return StartCoroutine(ExecuteActions(onCardDrawActions));
                        }
                    }
                }
            }
        }
        finally
        {
            FinishTriggeredAction();
        }

        SetPlayerMinionsReadyToAttack();
    }

    public IEnumerator EndPlayerTurn()
    {
        if (currentState != GameState.PlayerTurn) yield break;

        switchController.PlaySwitchAnim(false);

        // Lock held across the whole OnOwnerTurnEnd loop so the per-minion passes can't interleave with
        // each other or a queued sibling pass (see InvokeOnCardDrawActions for the rationale).
        _executingTriggeredActions = true;
        try
        {
            for (int x = 0; x < GridManager.Instance.GridWidth; x++)
            {
                for (int y = 0; y < GridManager.Instance.GridHeight; y++)
                {
                    Cell cell = GridManager.Instance.GetCell(new Vector2Int(x, y));

                    MinionController minion;

                    if (cell.obj != null && cell.obj.TryGetComponent(out minion) && minion.owner == player)
                    {
                        using (ActionHolder.PushScope())
                        {
                            onTurnEndActions.Clear();
                            ActionHolder.ResetSelections();
                            ActionHolder.thisMinion = minion;
                            ActionHolder.thisCardSO = minion.card;
                            ActionHolder.thisCard = null;
                            ActionHolder.selectedMinions.Add(minion);
                            ActionHolder.selectedAgent = player;
                            ActionHolder.curActionsList = onTurnEndActions;

                            this.isTesting = false;
                            minion.modal.OnOwnerTurnEnd.Invoke();

                            yield return StartCoroutine(ExecuteActions(onTurnEndActions));
                        }
                    }
                }
            }
        }
        finally
        {
            FinishTriggeredAction();
        }

        isPlayerTurn = false;


        yield return new WaitForSeconds(0.5f);

        // movement
        for (int x = 0; x < GridManager.Instance.GridWidth; x++)
        {
            for (int y = 0; y < GridManager.Instance.GridHeight; y++)
            {
                Cell cell = GridManager.Instance.GetCell(new Vector2Int(x, y));

                MinionController minion;


                if (cell.obj != null && cell.obj.TryGetComponent(out minion))
                {
                    if (!minion.modal.isPlayerMinion) continue;


                    Vector3Int pos = Vector3Int.RoundToInt(minion.transform.position) + Vector3Int.up;
                    var moveInfo = minion.CanMove(pos);
                    if (moveInfo.CanMove)
                    {
                        minion.Move(pos);
                        yield return new WaitForSeconds(0.26f);


                    }
                    else if (moveInfo.Blocked)
                    {
                        // Wanted to advance but the cell ahead stayed occupied (minions are resolved
                        // front-first, so anything still in the way here is a genuine block) — bump.
                        minion.FailedMove(Vector3.up, moveInfo.CollidedEntity);
                        yield return new WaitForSeconds(0.26f);
                    }
                }
            }
        }

        StartCoroutine(InvokeOnTurnEnd());

        //Debug.Log("Player Ends Turn");

        //StartCoroutine(OpponentTurn());
    }

    IEnumerator OpponentTurn()
    {

        currentState = GameState.OpponentTurn;
        //Debug.Log("Opponent's Turn");
        OpponentMaxMana = GrowMaxMana(OpponentMaxMana, OpponentStartingMana);
        opponent.availibleMana = OpponentMaxMana;

        yield return StartCoroutine(DrawTurnStartCards(opponent));

        // Queue turn-start behind the on-draw pass (same serializer) instead of racing it.
        EnqueueTriggeredAction(() => StartCoroutine(InvokeOnTurnStarted()));

        // Critical: the AI's PlayTurn evaluates moves by running ExecuteActions/ResetSelections on the
        // shared ActionHolder globals. Wait for the on-draw + turn-start passes to drain first, or the
        // AI eval clobbers an opponent turret's in-flight select->attack (its target list gets reset).
        yield return new WaitUntil(() => !HasInFlightTriggeredActions);

        yield return StartCoroutine(opponent.PlayTurn());


        // Lock held across the whole OnOwnerTurnEnd loop (see InvokeOnCardDrawActions for rationale).
        _executingTriggeredActions = true;
        try
        {
            for (int x = GridManager.Instance.GridWidth-1; x >= 0; x--)
            {
                for (int y = GridManager.Instance.GridHeight-1; y >= 0 ; y--)
                {
                    Cell cell = GridManager.Instance.GetCell(new Vector2Int(x, y));

                    MinionController minion;

                    if (cell.obj != null && cell.obj.TryGetComponent(out minion) && minion.owner == opponent)
                    {
                        using (ActionHolder.PushScope())
                        {
                            onTurnEndActions.Clear();
                            ActionHolder.ResetSelections();
                            ActionHolder.thisMinion = minion;
                            ActionHolder.thisCardSO = minion.card;
                            ActionHolder.thisCard = null;
                            ActionHolder.selectedMinions.Add(minion);
                            ActionHolder.selectedAgent = opponent;
                            ActionHolder.curActionsList = onTurnEndActions;

                            this.isTesting = false;
                            minion.modal.OnOwnerTurnEnd.Invoke();

                            yield return StartCoroutine(ExecuteActions(onTurnEndActions));
                        }
                    }
                }
            }
        }
        finally
        {
            FinishTriggeredAction();
        }

        isPlayerTurn = true;

        switchController.PlaySwitchAnim(true);

        yield return new WaitForSeconds(0.5f);


        // movement
        for (int x = GridManager.Instance.GridWidth - 1; x >= 0; x--)
        {
            for (int y = GridManager.Instance.GridHeight - 1; y >= 0; y--)
            {
                Cell cell = GridManager.Instance.GetCell(new Vector2Int(x, y));

                MinionController minion;

                if (cell.obj != null && cell.obj.TryGetComponent(out minion))
                {
                    if (minion.modal.isPlayerMinion) continue;

                    Vector3Int pos = Vector3Int.RoundToInt(minion.transform.position) + Vector3Int.down;
                    var moveInfo = minion.CanMove(pos);
                    if (moveInfo.CanMove)
                    {
                        minion.Move(pos);
                        yield return new WaitForSeconds(0.26f);

                        GridManager.Instance.InvokeGridChanged();
                    }
                    else if (moveInfo.Blocked)
                    {
                        // Wanted to advance but the cell ahead stayed occupied (minions are resolved
                        // front-first, so anything still in the way here is a genuine block) — bump.
                        minion.FailedMove(Vector3.down, moveInfo.CollidedEntity);
                        yield return new WaitForSeconds(0.26f);
                    }
                }
            }
        }

        yield return new WaitForSeconds(0.5f);

        //Debug.Log("Opponent has played.");
        StartCoroutine(InvokeOnTurnEnd());

        SetPlayerMinionsReadyToAttack();

        yield return null;
    }

    // Turn-switch hover preview: light up each player minion's move arrow to show what will happen when
    // the turn ends (white = will advance, yellow = wants to advance but will collide).
    public void ShowMovePreview()
    {
        foreach (var minion in player.minions)
            if (minion != null) minion.ShowMoveArrow();
    }

    public void HideMovePreview()
    {
        foreach (var minion in player.minions)
            if (minion != null) minion.HideMoveArrow();
    }

    public void SetPlayerMinionsReadyToAttack()
    {

        if (!isPlayerTurn) return;

        Debug.Log("should set palyer minions ready to attack");

        player.curState = Player.State.Waiting;
        // Set player minions Ready to attack. SetReadyToAttack owns the visual + clickable state
        // (attack highlight for attack-ready minions, no normal highlight), so we don't re-toggle
        // SetSelectable here — doing so would light the normal highlight on attack-ready minions.
        foreach (var item in player.minions)
        {
            item.SetReadyToAttack();
        }

        if (player.hero != null)
        {
            player.hero.SetReadyToAttack();
        }
    }

    public void CheckWinCondition()
    {
        if (player.hero.modal.health <= 0)
        {
            Debug.Log("Player Loses!");
            currentState = GameState.EndGame;
            MarkTutorialPlayed();
            GauntletRun.OnBattleLost();
            PopupManager.Instance.OpenGameOverPopup(false);
        }
        else if (opponent.hero.modal.health <= 0)
        {
            Debug.Log("Player Wins!");
            currentState = GameState.EndGame;
            MarkTutorialPlayed();
            GauntletRun.OnBattleWon(player.hero);
            PopupManager.Instance.OpenGameOverPopup(true);

        }
    }

    /// <summary>
    /// Retires the tutorial so the title screen stops auto-launching it. Called from every way out of
    /// the tutorial match — winning, losing, walking back to the menu, quitting — because the player
    /// only gets shown it once, whether or not they saw it through. A no-op in any other match.
    /// </summary>
    private void MarkTutorialPlayed()
    {
        if (!IsTutorialMatch) return;

        var saveManager = SaveManager.Instance;
        // Null once the app is tearing down: PermanentSingleton refuses to hand out an instance after
        // one has been destroyed. OnApplicationQuit above is what actually covers the quit case.
        if (saveManager == null) return;

        saveManager.SetTutorial(true);
    }

    // Editor-only debug triggers (see GameManagerEditor) to preview the end-game panels without
    // having to actually win/lose a match.
    public void TriggerVictory()
    {
        currentState = GameState.EndGame;
        GauntletRun.OnBattleWon(player.hero);
        PopupManager.Instance.OpenGameOverPopup(true, 0f);
    }

    public void TriggerDefeat()
    {
        currentState = GameState.EndGame;
        GauntletRun.OnBattleLost();
        PopupManager.Instance.OpenGameOverPopup(false, 0f);
    }
    public void Addtoactions(IEnumerator action)
    {
        actionQueue.Enqueue(action);
    }
    public void RemoveFromActions(IEnumerator action)
    {
        actionQueue.Enqueue(action);
    }

    public IEnumerator PlayCard(CardController card, Agent agent)
    {
        //Debug.Log("card.modal.cost: " + card.modal.cost);
        //Debug.Log("agent.availibleMana: " + agent.availibleMana);

        bool isPlayTurn = currentState == GameState.PlayerTurn || currentState == GameState.OpponentTurn;

        // An agent may only play on its own turn. isPlayingCard used to cover this by accident for the
        // player: the AI's play routine held it true across a trailing 3s animation wait, so there was no
        // window to drop into. That wait is gone (the reveal now runs in parallel with the card's effects),
        // which leaves real gaps between AI plays — hence an explicit turn check rather than a timing one.
        bool isAgentsTurn = agent == player ? CanPlayerPlayCards : !isPlayerTurn;
        if ((!isPlayTurn || !isAgentsTurn || isPlayingCard || card.modal.cost > agent.availibleMana) && !isTesting) yield break;

        // Playing a card preempts any in-progress attack/minion selection (e.g. mid-attack), tearing it
        // down so the played card's own selection steps start from a clean slate.
        SelectionManager.Instance.Cancel();

        ClearSelectables();

        isTesting = false;
        isTestingFailed = false;
        cancelPlayingCardRequested = false;
        ActionHolder.cancelRequested = false;
        playingCard = card;
        playingAgent = agent;
        playingAgentManaBeforePlay = agent != null ? agent.availibleMana : 0;
        playingCardHandLayoutIndex = player != null && player.cardHandLayout != null
            ? player.cardHandLayout.cards.IndexOf(card.transform)
            : -1;
        //agent.availibleMana -= card.modal.cost;
        isPlayingCard = true;
        actionQueue.Clear();
        ActionHolder.ResetSelections();
        //ActionHolder.thisMinion = null;
        ActionHolder.thisCardSO = card.card;
        ActionHolder.thisCard = card;
        ActionHolder.curActionsList = actionQueue;
        card.modal.OnPlay.Invoke();

        Debug.LogWarning("playing card: " + card.card.cardName);

        // OnPlay has filled the queue but nothing has run yet, so this is the one place that knows the
        // card is about to stop and ask -- before the first prompt goes up, not after it is answered.
        if (QueueHasTargeting(actionQueue)) OnCardTargetingStarted?.Invoke(agent, card.card);

        // Spell (non-minion) cards log their "played" entry here, before their effects resolve, so it
        // sits above the deaths/summons the spell triggers. It stays pending until the play commits so
        // a cancelled play leaves no orphaned entry. Minion cards instead log when they're summoned.
        if (card.modal.health <= 0)
        {
            ActionLogPanel.Instance?.SetPending(ActionLogMessageFactory.CardPlayed(playingAgent, card.card));
        }

        yield return StartCoroutine(ExecuteActions(card));

        Debug.LogWarning("played card");
    }
    public void PayCardCost(Agent agent, int cost)
    {

        
        
    }
    public IEnumerator _PayCardCost(Agent agent, int cost)
    {
        agent.availibleMana -= cost;

        yield return null;
    }
    public void RefundCardCost(Agent agent, int cost)
    {
        agent.availibleMana += cost;
    }
    // checks if card can be played? Runs the card's OnPlay queue with isTesting=true and reports
    // failures via isTestingFailed. Must be a coroutine so actions run sequentially — otherwise
    // selection-step coroutines yield-return null and the caller sees an empty queue before they
    // ever set isTestingFailed.
    public IEnumerator TestCard(CardController card)
    {
        isTestingFailed = false;

        actionQueue.Clear();
        ActionHolder.ResetSelections();
        ActionHolder.thisCardSO = card.card;
        ActionHolder.thisCard = card;
        //ActionHolder.thisMinion = null;
        ActionHolder.curActionsList = actionQueue;

        bool prevIsTesting = isTesting;
        isTesting = true;
        try
        {
            card.modal.OnPlay.Invoke();

            while (actionQueue.Count > 0)
            {
                IEnumerator action = actionQueue.Dequeue();
                yield return StartCoroutine(action);
            }
        }
        finally
        {
            isTesting = prevIsTesting;
        }
    }

    /// <summary>Whether any action still queued is a targeting step waiting to be run.</summary>
    private static bool QueueHasTargeting(Queue<IEnumerator> queue)
    {
        foreach (IEnumerator queued in queue)
        {
            if (ActionHolder.IsTargetingAction(queued)) return true;
        }

        return false;
    }

    public IEnumerator ExecuteActions(CardController card)
    {
        isTesting = false;
        _executingTriggeredActions = true;

        if (!isPlayerTurn)
        {
            //Debug.Log("execution complete4");

            //opponent.handManager.RemoveFromHand(card);
            if (card != null)
            {
                opponent.RemoveCardFromHand(card);

                // Same container the player's played card goes to (Canvas/CardParent). Previously this
                // parented to the Canvas itself and forced the card to be the LAST sibling, which drew it
                // over CardSelectionPanel and GameOverPanel; CardParent sits below both.
                card.transform.SetParent(PlayArea.Instance != null
                    ? PlayArea.Instance.PlayedCardParent
                    : opponent.cardHandLayout.transform.parent);
                card.transform.SetAsLastSibling();
                card.transform.localRotation = Quaternion.identity;

                // Scale / timings live on PlayArea next to the player's equivalents, so the deliberate
                // asymmetry between the two (the AI's card is bigger and lingers, because it also flips
                // face-up here and has to be readable) can be tuned against them rather than guessed at.
                card.transform.DOScale(Vector3.one * PlayArea.OpponentPlayedCardScale, PlayArea.OpponentPlayedCardTweenDuration);
                card.transform.DORotate(Vector3.up * 90, 0.15f).OnComplete(() =>
                {
                    card.modal.isPlayerMinion = true;
                    card.view.UpdateView(card.modal);
                    card.transform.DORotate(Vector3.up * 0, 0.15f);
                });
                card.transform.DOMove(PlayArea.Instance.opponentCardPos.position, PlayArea.OpponentPlayedCardTweenDuration).OnComplete(() =>
                {
                    card.transform.DOScale(0f, 0.25f).SetDelay(PlayArea.OpponentPlayedCardHoldDuration).OnComplete(() =>
                    {
                        if (card.modal.upgradedVerdion != null)
                        {
                            opponent.SpawnCardToDeck(card.modal.upgradedVerdion, true);
                        }
                        card.transform.SetParent(discardPile);
                        card.gameObject.SetActive(false);
                    });
                });

                //yield return new WaitForSeconds(3f);
            }

            //opponent.UpdateHand();
        }

        Debug.Log("executeing card actions");

        // Raised once, on the first action that is neither targeting itself nor still has targeting
        // ahead of it -- i.e. the moment the card's picks are all in and it starts doing something.
        // The look-ahead is what makes a two-pick card (Upheaval: pick, stash, pick, swap) announce on
        // the swap instead of on the stash between its two prompts.
        bool effectsAnnounced = false;

        while (actionQueue.Count > 0)
        {
            if (cancelPlayingCardRequested || ActionHolder.cancelRequested)
            {
                actionQueue.Clear();
                break;
            }
            Debug.Log("executeing action");
            IEnumerator action = actionQueue.Dequeue();

            if (!isPlayerTurn) yield return new WaitForSeconds(0.35f);

            if (!effectsAnnounced && !ActionHolder.IsTargetingAction(action) && !QueueHasTargeting(actionQueue))
            {
                effectsAnnounced = true;
                OnCardEffectsStarting?.Invoke(playingAgent, card != null ? card.card : null);
            }

            yield return StartCoroutine(action);
        }
        Debug.Log("execution complete");
        isTesting = false;

        if (cancelPlayingCardRequested || ActionHolder.cancelRequested)
        {
            FinishCancelPlayingCard();
            yield break;
        }

        if (isTesting)
        {
            Debug.Log("is testing true");

            yield break;
        }
        //Destroy(card.gameObject);
        //Debug.Log("execution complete2");

        // Show the spell's pending "played" entry if none of its effects already did (e.g. a spell with
        // no minion deaths/summons). No-op for minion cards, which never set a pending entry.
        ActionLogPanel.Instance?.FlushPending();

        // Confirmed-commit point: this is past the cancel checks above, so cancelled/failed plays never
        // reach here. Broadcast for the stats system (cards played / mana spent / upgraded count).
        OnCardPlayed?.Invoke(playingAgent, card != null ? card.card : null);

        if (isPlayerTurn)
        {
            //Debug.Log("execution complete3");

            //player.handManager.RemoveFromHand(card);
            if (card != null) {
                player.RemoveCardFromHand(card);

                CardSO upgraded = card.modal.upgradedVerdion;

                // A card that forges gets to BECOME its upgrade rather than being swapped for a copy of
                // it -- so it is neither shrunk away nor parked under the discard pile, and the object
                // the player has been looking at is the one that flies off to the deck. Everything
                // else, including every card that forges nothing, keeps the original path.
                if (upgraded != null && !GameSettings.ReduceCardAnimations)
                {
                    player.ForgeCardInPlace(card, upgraded);
                }
                else
                {
                    card.transform.DOScale(0f, 0.25f).OnComplete(() =>
                    {
                        if (upgraded != null)
                        {
                            player.SpawnCardToDeck(upgraded, true);
                        }
                        card.transform.SetParent(discardPile);
                        card.gameObject.SetActive(false);
                    });
                }
            }
        }
        else
        {

        }

        isPlayingCard = false;
        ClearSelectables();
        playingCard = null;
        playingAgent = null;
        cancelPlayingCardRequested = false;
        ActionHolder.cancelRequested = false;

        if (isPlayerTurn)
        {
            //Debug.LogWarning("setting player minions ready to attack");
            player.UpdateHand();
            SetPlayerMinionsReadyToAttack();

        }
        else
        {
            opponent.UpdateHand();
        }
        FinishTriggeredAction();
        
        Debug.Log("All actions completed.");

    }

    public void CancelPlayingCard()
    {
        if (!isPlayingCard) return;
        if (!isPlayerTurn) return;
        if (currentState != GameState.PlayerTurn) return;
        if (playingAgent != player) return;

        // A card choice on screen has already shown the player what they rolled. Backing out now would
        // refund the card and hand them a fresh set of options — a free reroll. Once revealed, committed.
        if (CardChoice.Instance.HasActiveRequest) return;

        cancelPlayingCardRequested = true;
        ActionHolder.cancelRequested = true;

        ActionHolder.selectedcell = null;
        ActionHolder.selectedMinion = null;
        ActionHolder.selectedAgent = null;
        ActionHolder.selectedMinions.Clear();
        ActionHolder.selectedCells.Clear();

        SelectionManager.Instance.Cancel();
        player.curState = Player.State.Waiting;
        ClearSelectables();
    }

    /// <summary>
    /// Drops a cancel that arrived after the play was already committed (see ActionHolder's Discover /
    /// transform verbs). The card and its mana stay spent and only the unresolved effect is lost, so the
    /// outer ExecuteActions finishes the play normally instead of refunding it back into the hand.
    /// </summary>
    public void ClearCancelledPlay()
    {
        cancelPlayingCardRequested = false;
        ActionHolder.cancelRequested = false;
    }

    private void FinishCancelPlayingCard()
    {
        // The cancelled card never resolved, so drop its unshown "played" entry.
        ActionLogPanel.Instance?.DiscardPending();

        if (playingAgent != null)
        {
            playingAgent.availibleMana = playingAgentManaBeforePlay;
        }

        if (playingCard != null && isPlayerTurn && player != null && player.cardHandLayout != null)
        {
            var layout = player.cardHandLayout;

            layout.RemoveCard(playingCard.transform);
            int insertIndex = playingCardHandLayoutIndex >= 0 ? playingCardHandLayoutIndex : 0;
            layout.InsertCardAt(playingCard.transform, insertIndex);

            playingCard.transform.localRotation = Quaternion.identity;
            playingCard.transform.localScale = Vector3.one;
            playingCard.draggableItem.ParentAfterDrag = layout.transform;
            playingCard.EnablePeek();
        }

        isPlayingCard = false;
        cancelPlayingCardRequested = false;
        ActionHolder.cancelRequested = false;

        ClearSelectables();

        if (isPlayerTurn)
        {
            player.UpdateHand();
            SetPlayerMinionsReadyToAttack();
        }
        else
        {
            opponent.UpdateHand();
        }

        // Announced from here rather than from CancelPlayingCard, which only *requests* the cancel: the
        // card is not actually back in hand until this method has run, and a request can still be
        // dropped on the way (see ClearCancelledPlay).
        OnCardPlayCancelled?.Invoke(playingAgent, playingCard != null ? playingCard.card : null);

        playingCard = null;
        playingAgent = null;
        playingCardHandLayoutIndex = -1;
    }

    public IEnumerator ExecuteActions(Queue<IEnumerator> actionsList)
    {
        //Debug.Log("executeing card actions");
        while (actionsList.Count > 0)
        {
            //Debug.Log("executeing action");
            IEnumerator action = actionsList.Dequeue();
            yield return StartCoroutine(action);
        }
        //Debug.Log("All actions completed.");
    }

    public void SummonMinion(CardSO card, Vector3 pos)
    {
        // Turn-relative summon: the new minion belongs to whoever's turn it is.
        SummonMinion(card, pos, isPlayerTurn ? player : opponent);
    }

    /// <summary>
    /// Owner-explicit summon. Unlike the turn-relative overload above, the summoned minion belongs to
    /// `owner` regardless of whose turn it is — needed by hero passives that summon on the *defender's*
    /// side during the attacker's turn (summon-on-attacked). Push direction and side assignment are all
    /// resolved from `owner`, not from isPlayerTurn.
    /// </summary>
    public void SummonMinion(CardSO card, Vector3 pos, Agent owner)
    {
        bool ownerIsPlayer = owner == player;

        // If the target cell is already occupied, push the occupant forward one cell first so the new
        // minion spawns in the vacated cell. Callers only pass cells whose occupant is pushable.
        Vector2Int destIndex = GridManager.Instance.PosToGridIndex(pos);
        GameObject occupantObj = GridManager.Instance.GetCell(destIndex).obj;
        if (occupantObj != null && occupantObj.TryGetComponent(out MinionController occupant))
        {
            Vector3Int pushDir = ownerIsPlayer ? Vector3Int.up : Vector3Int.down;
            if (occupant.CanBePushedForward(pushDir))
            {
                occupant.PushForward(pushDir);
            }
            else
            {
                return;
            }
        }

        GameObject prefabToSpawn = (card.range >= 2 && rangedMinionprefab != null) ? rangedMinionprefab : minionprefab;
        MinionController minion = Instantiate(prefabToSpawn, pos, Quaternion.identity).GetComponent<MinionController>();

        // Stamp the grid position NOW instead of leaving it to GridEntity.Start(). Start doesn't run
        // until after this frame's Update, so anything that reads the fresh minion's grid position in
        // the same frame as the summon — e.g. the Totem's own OnPlay chain, whose adjacency step
        // resumes later in this very frame — used to read a default (0,0,0) and pick the wrong cells.
        if (minion.gridEntity != null) minion.gridEntity.WorldPos = pos;

        minion.card = card;
        //minion.modal = new MinionModal(card, minion);
        minion.modal.UpdateModal(minion.card, owner, ownerIsPlayer);

        // Everything but events comes from the played hand card's modal (which may include in-hand
        // buffs), not the CardSO. The card's modal was itself populated from the CardSO when the card
        // was drawn. Falls back to the CardSO data (already set by UpdateModal) for summons with no
        // hand card (e.g. tokens / random / passive summons, where thisCard is null).
        CardController sourceCard = ActionHolder.thisCard;
        if (sourceCard != null && sourceCard.modal != null && sourceCard.card == card)
            minion.modal.CopyFrom(sourceCard.modal);

        // Re-assert ownership AFTER CopyFrom and BEFORE UpdateView. CopyFrom drags owner/isPlayerMinion
        // over from the hand card, and an opponent card's modal is flipped to isPlayerMinion=true by the
        // play-area reveal animation (that flag doubles as "render this card face-up"). MinionView caches
        // the side on UpdateView and never re-reads it, so summoning off a copied modal used to bake the
        // player's frame tint into an enemy minion.
        minion.modal.isPlayerMinion = ownerIsPlayer;
        minion.modal.owner = owner;

        minion.view.UpdateView(minion.modal);
        minion.view.PlayAppearAnimation();
        ActionHolder.thisMinion = minion;
        ActionHolder.thisCardSO = minion.card;

        owner.minions.Add(minion);
        minion.owner = owner;

        // Let hero aura passives stamp per-minion stats (e.g. collision damage on friendly minions).
        // Pure side effect — safe here mid-summon; must not enqueue triggered actions.
        heroPassives.ApplyAurasOnSummon(minion);

        // Aura indicators read the board (e.g. "dormant until a friendly minion exists"), so the row
        // has to re-evaluate now that owner.minions has changed. Read-only, no triggered action.
        heroPassives.RefreshIndicators();

        OnMinionSummoned?.Invoke(minion);

        // Broadcast the summon: first the new minion's own OnThisMinionSummoned trigger, then every other
        // minion's OnAnyMinionSummoned trigger (e.g. crossbows that snap-fire at enemies entering play).
        // Routed through the triggered-action scheduler so it can't race the play/trigger currently
        // draining, exactly like death/collision/took-damage.
        EnqueueTriggeredAction(() => StartCoroutine(InvokeMinionSummonedActions(minion)));
    }

    private IEnumerator InvokeMinionSummonedActions(MinionController summoned)
    {
        // FinishTriggeredAction is OUTSIDE the using (see InvokeOnMinionDeathActions) so a scope's Restore()
        // can't clobber the selection state of whatever pending trigger it drains next.
        try
        {
            _executingTriggeredActions = true;

            // Snapshot the reactors up front: a reaction shot can kill minions mid-pass, which mutates the
            // live owner.minions lists we'd otherwise be iterating.
            List<MinionController> reactors = new List<MinionController>();
            foreach (var m in player.minions) if (m != null && m != summoned) reactors.Add(m);
            foreach (var m in opponent.minions) if (m != null && m != summoned) reactors.Add(m);

            // The new arrival reacts to its own summon first, before the rest of the board hears about it.
            // This is the token-safe counterpart to OnPlay: it fires for every way a minion can enter play
            // (its own card, a token/random/copy summon, a hero passive), not just for a card leaving hand.
            // thisMinion and summonedMinion both point at the newcomer, so verbs written against either
            // context resolve to it.
            if (summoned != null && summoned.modal != null && summoned.modal.OnThisMinionSummoned != null)
            {
                using (ActionHolder.PushScope())
                {
                    onThisMinionSummonedActions.Clear();
                    ActionHolder.ResetSelections();
                    ActionHolder.summonedMinion = summoned;
                    ActionHolder.thisMinion = summoned;
                    ActionHolder.thisCardSO = summoned.card;
                    ActionHolder.thisCard = null;
                    ActionHolder.selectedAgent = summoned.owner;
                    ActionHolder.curActionsList = onThisMinionSummonedActions;

                    this.isTesting = false;
                    summoned.modal.OnThisMinionSummoned.Invoke();

                    yield return StartCoroutine(ExecuteActions(onThisMinionSummonedActions));
                }
            }

            foreach (var reactor in reactors)
            {
                if (reactor == null) continue;
                // Skip reactors that died earlier in this same broadcast.
                if (!player.minions.Contains(reactor) && !opponent.minions.Contains(reactor)) continue;

                using (ActionHolder.PushScope())
                {
                    onAnyMinionSummonedActions.Clear();
                    ActionHolder.ResetSelections();
                    ActionHolder.summonedMinion = summoned;
                    ActionHolder.thisMinion = reactor;
                    ActionHolder.thisCardSO = reactor.card;
                    ActionHolder.thisCard = null;
                    ActionHolder.selectedAgent = reactor.owner;
                    ActionHolder.curActionsList = onAnyMinionSummonedActions;

                    this.isTesting = false;
                    reactor.modal.OnAnyMinionSummoned.Invoke();

                    yield return StartCoroutine(ExecuteActions(onAnyMinionSummonedActions));
                }
            }

            // A minion entering play mid-turn is a fresh attack target for the acting player's minions,
            // but the cached canAttack/clickable state was computed before it existed (e.g. the attacker's
            // Attack() epilogue ran before this async summon resolved). Without this the player can't click
            // a melee minion to strike a just-summoned enemy — notably one the enemy Summoner passive spawns
            // in response to being attacked. Recompute readiness now that the board (and any summon-reaction
            // deaths above) has settled. No-ops off the player's turn via the guard inside the callee.
            SetPlayerMinionsReadyToAttack();
        }
        finally
        {
            FinishTriggeredAction();
        }
    }
    private void ClearSelectables()
    {
        foreach (var selectable in selectables)
        {
            selectable.SetSelectable(false);
        }
    }
    private IEnumerator InvokeOnMinionDeathActions(MinionController minion)
    {
        // PushScope() snapshots selection state + isTesting; Dispose restores both.
        // FinishTriggeredAction runs after Dispose (finally OUTSIDE the using, not inside it) so the outer
        // trigger queue resumes against restored state. Order matters: FinishTriggeredAction drains the
        // next pending trigger and starts it SYNCHRONOUSLY up to its first yield. If it ran before this
        // scope's Restore(), that Restore would fire while the drained trigger is suspended mid-selection
        // and wipe the selection state it just set up — e.g. a deathrattle that damages a hero re-entrantly
        // drains that hero's took-damage passive here, and this Restore would clobber the passive's
        // selectedCells, making it silently select nothing.
        try
        {
            using (ActionHolder.PushScope())
            {
                _executingTriggeredActions = true;

                onMinionDeathActions.Clear();
                ActionHolder.ResetSelections();
                ActionHolder.thisMinion = minion;
                ActionHolder.thisCardSO = minion.card;
                ActionHolder.thisCard = null;
                ActionHolder.selectedAgent = minion.owner;
                ActionHolder.curActionsList = onMinionDeathActions;

                this.isTesting = false;
                minion.modal.OnDeath.Invoke();

                yield return StartCoroutine(ExecuteActions(onMinionDeathActions));
            }
        }
        finally
        {
            // Release the destroy-hold taken in OnMinionDied now that the deathrattle has read everything
            // it needs off this minion. In a finally so a fault mid-deathrattle can't strand the object
            // alive forever. Before FinishTriggeredAction only because it is this trigger's own teardown —
            // the minion is already off the grid and off its owner's roster, so nothing the next trigger
            // does can see it either way.
            if (minion != null) minion.OnDeathTriggerResolved();
            FinishTriggeredAction();
        }
    }

    private void OnMinionDied(MinionController minion)
    {
        ActionLogPanel.Instance?.AddEntry(ActionLogMessageFactory.MinionDied(minion));
        // Claim the minion before the trigger is queued: EnqueueTriggeredAction usually defers it behind
        // the play that did the killing, and the death animation would otherwise destroy the object in the
        // gap — see MinionController.DestroySelf. Paired with OnDeathTriggerResolved below, which always
        // runs because the coroutine clears it in a finally.
        if (minion != null) minion.deathTriggerPending = true;
        EnqueueTriggeredAction(() => StartCoroutine(InvokeOnMinionDeathActions(minion)));
    }

    private void OnMinionSummonedForLog(MinionController minion)
    {
        ActionLogPanel.Instance?.AddEntry(ActionLogMessageFactory.MinionSummoned(minion));
    }

    private void OnMinionCollided(MinionController minion, MinionController collidedMinion)
    {
        EnqueueTriggeredAction(() => StartCoroutine(InvokeOnMinionCollidedActions(minion, collidedMinion)));
    }

    private void OnMinionTookDamage(MinionController minion, int damage)
    {
        EnqueueTriggeredAction(() => StartCoroutine(InvokeOnMinionTookDamageActions(minion, damage)));
    }

    private void EnqueueTriggeredAction(Action startCoroutine)
    {
        if (_executingTriggeredActions)
        {
            Debug.Log("added top pending actions");
            _pendingTriggeredCallbacks.Enqueue(startCoroutine);
            return;
        }

        startCoroutine.Invoke();
    }

    private void FinishTriggeredAction()
    {
        _executingTriggeredActions = false;
        if (_pendingTriggeredCallbacks.Count > 0)
        {
            _pendingTriggeredCallbacks.Dequeue().Invoke();
        }
    }
    private IEnumerator InvokeOnMinionTookDamageActions(MinionController minion, int damage)
    {
        // FinishTriggeredAction is OUTSIDE the using (see InvokeOnMinionDeathActions) so this scope's
        // Restore() can't clobber the selection state of whatever pending trigger it drains next.
        try
        {
            using (ActionHolder.PushScope())
            {
                _executingTriggeredActions = true;

                onMinionTookDamageActions.Clear();
                ActionHolder.ResetSelections();
                ActionHolder.thisMinion = minion;
                ActionHolder.thisCardSO = minion.card;
                ActionHolder.thisCard = null;
                ActionHolder.selectedAgent = minion.owner;
                ActionHolder.curActionsList = onMinionTookDamageActions;

                this.isTesting = false;
                minion.modal.OnTookDamage.Invoke();

                // Hero "took damage" passives ride the same queue and scope as the self-trigger above,
                // so they can't race it. Registers are already set to the damaged minion (the hero).
                DispatchHeroTookDamagePassives(minion, damage);

                yield return StartCoroutine(ExecuteActions(onMinionTookDamageActions));

                // AFTER the drain, not at dispatch: passive.Run only enqueues verbs, so state the
                // badges read (e.g. appliedAttackBonus) is not written until ExecuteActions runs.
                heroPassives.RefreshIndicators();
            }
        }
        finally
        {
            FinishTriggeredAction();
        }
    }

    /// <summary>
    /// Enqueues the verbs of every hero passive that fires for a "hero took damage" self-trigger onto
    /// the queue the caller is already draining.
    ///
    /// This is called INLINE from InvokeOnMinionTookDamageActions, inside the triggered-action scope
    /// GameManager already opened, with thisMinion/selectedAgent already set to the damaged hero. It
    /// deliberately does NOT open its own scope or start its own triggered action: doing that races the
    /// enclosing scope and gets its registers clobbered by the outer Restore (see HeroPassiveSystem for
    /// the full explanation). The passive verbs simply ride the caller's single ExecuteActions pass.
    /// </summary>
    private void DispatchHeroTookDamagePassives(MinionController hero, int damage)
    {
        HeroRuntime runtime = heroPassives.GetRuntime(hero);
        if (runtime == null) return;

        var ctx = new HeroPassiveContext(hero, subject: hero, amount: damage,
            ownerTurnNumber: runtime.ownerTurnNumber);

        _heroPassiveMatchBuffer.Clear();
        heroPassives.CollectMatching(runtime, HeroPassiveTrigger.HeroTookDamage, ctx, _heroPassiveMatchBuffer);

        HeroPassiveIndicatorView indicators = HeroPassiveIndicatorView.For(hero);

        // Registers are already set by the caller (thisMinion = hero, selectedAgent = hero.owner), which
        // is exactly what a self-trigger needs; the owner-relative selectors then resolve the correct
        // board half even though the hero was damaged on the opponent's turn.
        foreach (var passive in _heroPassiveMatchBuffer)
        {
            // Flash here (the passive matched), but refresh the badge only after the caller's
            // ExecuteActions drain — Run enqueues, it does not apply.
            indicators?.PlayProcFlash(passive);
            passive.Run(ctx);
        }
    }

    /// <summary>
    /// A minion has struck the given hero. This is an "attack" — distinct from took-damage, which also
    /// fires for spells — so it drives the HeroAttacked passive trigger only. Routed through the same
    /// triggered-action scheduler as took-damage/collision so its ActionHolder scope can't race the
    /// strike's own took-damage trigger. Heroes with no passives are a no-op (behave exactly as before).
    /// </summary>
    public void OnHeroAttacked(MinionController hero, MinionController attacker)
    {
        if (hero == null) return;
        if (heroPassives.GetRuntime(hero) == null) return; // not a passive hero — nothing to dispatch

        EnqueueTriggeredAction(() => StartCoroutine(InvokeOnHeroAttackedActions(hero, attacker)));
    }

    private IEnumerator InvokeOnHeroAttackedActions(MinionController hero, MinionController attacker)
    {
        // FinishTriggeredAction is OUTSIDE the using (see InvokeOnMinionTookDamageActions) so this scope's
        // Restore() can't clobber the selection state of whatever pending trigger it drains next.
        try
        {
            using (ActionHolder.PushScope())
            {
                _executingTriggeredActions = true;

                onHeroAttackedActions.Clear();
                ActionHolder.ResetSelections();
                ActionHolder.thisMinion = hero;
                ActionHolder.thisCardSO = hero.card;
                ActionHolder.thisCard = null;
                ActionHolder.selectedAgent = hero.owner;
                ActionHolder.curActionsList = onHeroAttackedActions;

                this.isTesting = false;

                // Registers are set to the hero (thisMinion = hero, selectedAgent = hero.owner) so the
                // owner-relative summon verb resolves the defender's board half even though the attack
                // happened on the attacker's turn.
                DispatchHeroAttackedPassives(hero, attacker);

                yield return StartCoroutine(ExecuteActions(onHeroAttackedActions));

                // Same ordering rule as InvokeOnMinionTookDamageActions: refresh after the drain.
                heroPassives.RefreshIndicators();
            }
        }
        finally
        {
            FinishTriggeredAction();
        }
    }

    /// <summary>Enqueues the verbs of every HeroAttacked passive on this hero onto the drain the caller owns.</summary>
    private void DispatchHeroAttackedPassives(MinionController hero, MinionController attacker)
    {
        HeroRuntime runtime = heroPassives.GetRuntime(hero);
        if (runtime == null) return;

        var ctx = new HeroPassiveContext(hero, subject: attacker,
            ownerTurnNumber: runtime.ownerTurnNumber);

        _heroPassiveMatchBuffer.Clear();
        heroPassives.CollectMatching(runtime, HeroPassiveTrigger.HeroAttacked, ctx, _heroPassiveMatchBuffer);

        HeroPassiveIndicatorView indicators = HeroPassiveIndicatorView.For(hero);

        foreach (var passive in _heroPassiveMatchBuffer)
        {
            indicators?.PlayProcFlash(passive);
            passive.Run(ctx);
        }
    }

    /// <summary>
    /// True if `hero` is a registered passive hero carrying a passive that cancels the counter-attack an
    /// attacker would otherwise take. Non-heroes and heroes without such a passive return false, so the
    /// normal retaliation path in MinionController.Attack is untouched for everything else.
    /// </summary>
    public bool HeroSuppressesCounterAttack(MinionController hero)
    {
        HeroRuntime runtime = heroPassives.GetRuntime(hero);
        if (runtime == null || runtime.heroSO == null) return false;

        var passives = runtime.passives;
        for (int i = 0; i < passives.Count; i++)
            if (passives[i] != null && passives[i].SuppressesCounterAttack) return true;

        return false;
    }

    /// <summary>
    /// Two minions collided (mover ran into target). Only the minion that MOVED deals damage: if the two
    /// are on opposite sides, the mover deals its collisionDamage to the target. Being rammed deals
    /// nothing back — a minion only hurts an enemy when it is the one that collides into it, never when an
    /// enemy collides into it. A plain minion (collisionDamage 0) deals nothing. Same-side or
    /// missing-owner collisions are ignored.
    /// </summary>
    private void ApplyCollisionDamage(MinionController mover, MinionController target)
    {
        if (mover == null || target == null) return;
        if (mover.modal == null) return;
        if (mover.owner == null || target.owner == null) return;
        if (mover.owner == target.owner) return; // friendly bump — no damage

        int moverDamage = mover.modal.collisionDamage;
        if (moverDamage > 0) target.TakeDamage(moverDamage);
    }

    private IEnumerator InvokeOnMinionCollidedActions(MinionController minion, MinionController collidedMinion)
    {
        // FinishTriggeredAction is OUTSIDE the using (see InvokeOnMinionDeathActions) so this scope's
        // Restore() can't clobber the selection state of whatever pending trigger it drains next.
        try
        {
            using (ActionHolder.PushScope())
            {
                _executingTriggeredActions = true;

                onMinionCollidedActions.Clear();
                ActionHolder.ResetSelections();
                ActionHolder.selectedTargetMinions.Clear();
                ActionHolder.selectedTargetMinions.Add(collidedMinion);
                ActionHolder.thisMinion = minion;
                ActionHolder.thisCardSO = minion.card;
                ActionHolder.thisCard = null;
                ActionHolder.selectedAgent = minion.owner;
                ActionHolder.curActionsList = onMinionCollidedActions;

                this.isTesting = false;

                // Intrinsic collision damage: the minion that moved into another deals its collisionDamage
                // to that enemy (the rammed minion deals nothing back). Default collisionDamage is 0, so
                // this is inert unless a passive (the Collision Damage Aura) raised it. Applied before the
                // scripted OnMinionCollided so card effects observe any deaths it caused, and inside this
                // scope so the TakeDamage-driven took-damage/death triggers serialize on the same scheduler.
                ApplyCollisionDamage(minion, collidedMinion);

                minion.modal.OnMinionCollided.Invoke();

                yield return StartCoroutine(ExecuteActions(onMinionCollidedActions));
            }
        }
        finally
        {
            FinishTriggeredAction();
        }
    }
}

