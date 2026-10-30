using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Single owner of "is the board interactive right now?".
///
/// Every piece of board hover feedback — minion range, passive tooltips, the info card, push arrows,
/// death previews — is delivered by Unity's LEGACY mouse messages (OnMouseEnter/OnMouseExit on a
/// Collider2D). Those are dispatched against physics colliders and never consult the EventSystem, so
/// UI drawn on top cannot block them: opening the card selection panel over the board still lets the
/// minions underneath light their range squares. That was a deliberate trade (see the comment on
/// <see cref="HeroPassiveIndicator"/>: CardPlayArea is a screen-space OVERLAY raycast target covering
/// the board, so an EventSystem pointer would never reach world-space objects at all), and this class
/// is what pays for it.
///
/// Enforcement is <see cref="Camera.eventMask"/>, which gates Unity's delivery of ALL legacy OnMouse*
/// messages in one property. Deliberately a layer BELOW the hover code: every previous attempt at this
/// put a guard inside each OnMouseEnter, and they drifted apart immediately (MinionController checks
/// EndGame and AnyCardDragging, HeroPassiveIndicator checks nothing at all). A hover source cannot opt
/// out of this gate by forgetting a check, and hover code written later is covered without knowing the
/// gate exists.
///
/// Two things it deliberately does NOT touch:
///   - EventSystem/uGUI hover. The selection panel's own option cards (CardHoverOutline) keep swelling
///     and stay clickable while the board goes quiet, which is exactly the point.
///   - Right-click cancel. GameManager.Update reads Input.GetMouseButtonDown(1) directly, so backing
///     out of a card play still works while the board is gated.
///
/// There is deliberately NO cleanup pass for feedback that was already on screen when the gate closed,
/// and it should not be added back. Masking the camera makes Unity's mouse raycast miss, and Unity then
/// delivers OnMouseExit to whatever was hovered exactly as if the pointer had left — VERIFIED in play
/// mode: a minion's range squares go dark the instant the mask drops, with the mouse held still on the
/// minion. Every hover source therefore cleans up through its own existing exit handler. A central
/// "hide everything" method would have to name each source, which is the very drift this class exists
/// to remove.
///
/// Self-installing, so it needs no scene or prefab wiring (same reasoning as CardSelectionPanel's
/// static bootstrap).
/// </summary>
public class BoardInteractionGate : MonoBehaviour
{
    /// <summary>
    /// True while board interaction is suppressed. Public so a future hover source can also read it
    /// directly — and so the enforcement mechanism below can be swapped for per-site guards without
    /// changing how anything else asks the question.
    /// </summary>
    public static bool Blocked { get; private set; }

    // Resolved per scene rather than per frame; see ResolveSceneManagers.
    private GameManager _gm;
    private PopupManager _popup;

    private Camera _maskedCamera;
    private int _restoreMask;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        // Statics survive between play sessions when Enter Play Mode Options skips the domain reload,
        // so a session that ended while blocked would otherwise start the next one still blocked.
        Blocked = false;

        if (FindObjectOfType<BoardInteractionGate>() != null) return;

        var go = new GameObject(nameof(BoardInteractionGate));
        go.AddComponent<BoardInteractionGate>();
        DontDestroyOnLoad(go);
    }

    // The bootstrap runs AfterSceneLoad, i.e. after the first scene's sceneLoaded has already fired,
    // so the initial resolve has to happen here rather than only in the event handler.
    private void Start() => ResolveSceneManagers();

    private void OnEnable() => SceneManager.sceneLoaded += HandleSceneLoaded;

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;

        // Never leave a camera masked behind us; a stuck mask would kill board input for good.
        Release();
        Blocked = false;
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Restore before letting go: no-ops if that camera died with the outgoing scene, and correctly
        // un-masks it if it survived an additive load.
        Release();
        ResolveSceneManagers();
    }

    /// <summary>
    /// Both are <see cref="Singleton{T}"/>s whose Instance getter falls back to FindObjectOfType while
    /// the backing field is null — which is EVERY frame in a scene that has no GameManager, i.e. the
    /// menus. Resolved once per scene load instead, so <see cref="LateUpdate"/> stays search-free.
    /// </summary>
    private void ResolveSceneManagers()
    {
        _gm = FindObjectOfType<GameManager>();
        _popup = FindObjectOfType<PopupManager>();
    }

    private void LateUpdate()
    {
        Blocked = ShouldBlock();

        if (!Blocked)
        {
            Release();
            return;
        }

        // Re-checked every frame rather than applied once on the false->true edge: this object survives
        // scene loads, so the camera can be swapped out from under a still-blocked gate, and the
        // incoming scene's camera would otherwise never get masked.
        Camera cam = Camera.main;
        if (cam == _maskedCamera) return;

        Release();
        if (cam == null) return;

        _maskedCamera = cam;
        _restoreMask = cam.eventMask;
        cam.eventMask = 0;
    }

    private void Release()
    {
        // Unity's overloaded == reports a destroyed camera as null, which is what makes this safe to
        // call after the owning scene has gone away.
        if (_maskedCamera != null) _maskedCamera.eventMask = _restoreMask;
        _maskedCamera = null;
    }

    /// <summary>
    /// Recomputed from scratch each frame rather than tracked as a push/pop blocker stack. Card
    /// resolution runs through long coroutines with several `yield break` exits (GameManager.PlayCard),
    /// where a single skipped Pop would strand the board permanently inert. A predicate cannot leak.
    /// </summary>
    private bool ShouldBlock()
    {
        if (_gm == null) return false; // no match in progress (menu scenes): nothing to gate

        // The pause menu freezes the game with Time.timeScale, which does nothing to legacy OnMouse*
        // delivery — hover feedback would keep lighting up behind the menu without this.
        if (EscMenuController.IsOpen) return true;

        // The match-up opening the match covers the board; its heroes are only landing there.
        if (VersusIntro.IsUp) return true;

        if (_gm.currentState == GameState.EndGame) return true;
        if (CardChoice.Instance.HasActiveRequest) return true;
        if (_popup != null && _popup.HasOpenPopup) return true;

        // A card mid-resolution owns the board — EXCEPT while it is actively asking the player to pick
        // something, which is precisely when range / death-preview / push-arrow feedback is wanted.
        if (_gm.isPlayingCard && !PlayerIsBeingAskedToPick()) return true;

        return false;
    }

    /// <summary>
    /// Whether an interactive pick is waiting on the player. Reuses the two existing request objects
    /// rather than introducing a third piece of state to keep in sync: both already mean exactly this.
    /// </summary>
    private static bool PlayerIsBeingAskedToPick()
    {
        if (SelectionManager.Instance.HasActiveMinionRequest) return true;

        GridCellSelectionManager grid = GridCellSelectionManager.Instance;
        return grid != null && grid.HasActiveSession;
    }
}
