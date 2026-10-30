using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

public class SceneTransitionManager : MonoBehaviour
{
    public static SceneTransitionManager Instance { get; private set; }

    [Header("Transition Settings")]
    [SerializeField] private CanvasGroup fadeCanvasGroup;
    [SerializeField] private float fadeDuration = 1f;
    [SerializeField] private bool fadeOnStart = true;

    private bool isTransitioning = false;

    /// <summary>A fade is under way: the screen is still going to black or coming back from it.</summary>
    public bool IsTransitioning => isTransitioning;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        // Not while a transition is already under way: SkipToScene can be called from another Start,
        // and script execution order decides which of the two runs first. Without this guard that
        // ordering would sometimes fade the outgoing scene in over a hand-off meant to stay black.
        if (fadeOnStart && !isTransitioning)
            StartCoroutine(Fade(0f));
    }

    public void TransitionToScene(string sceneName)
    {
        if (!isTransitioning)
            StartCoroutine(Transition(sceneName));
    }

    /// <summary>
    /// Loads a scene without fading out first, for a hand-off that happens before the outgoing scene has
    /// ever been shown -- the screen is still black there, so a fade out would only reveal what is under
    /// it on the way down. The fade back in still plays, once the new scene is up.
    /// </summary>
    public void SkipToScene(string sceneName)
    {
        if (isTransitioning) return;

        // Claimed synchronously so a Start() that has not run yet skips its own fade in, and any fade
        // that did already start is dropped in favour of holding black until the new scene is loaded.
        isTransitioning = true;
        StopAllCoroutines();

        fadeCanvasGroup.alpha = 1f;
        fadeCanvasGroup.blocksRaycasts = true;

        StartCoroutine(LoadThenFadeIn(sceneName));
    }

    private IEnumerator LoadThenFadeIn(string sceneName)
    {
        AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(sceneName);
        while (!asyncLoad.isDone)
        {
            yield return null;
        }

        yield return StartCoroutine(Fade(0f));

        isTransitioning = false;
    }

    private IEnumerator Transition(string sceneName)
    {
        isTransitioning = true;

        // Fade to black
        yield return StartCoroutine(Fade(1f));

        // Load the new scene
        AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(sceneName);
        while (!asyncLoad.isDone)
        {
            yield return null;
        }

        // Fade back in
        yield return StartCoroutine(Fade(0f));

        isTransitioning = false;
    }

    private IEnumerator Fade(float targetAlpha)
    {
        fadeCanvasGroup.blocksRaycasts = true;

        float startAlpha = fadeCanvasGroup.alpha;
        float timer = 0f;

        while (timer < fadeDuration)
        {
            fadeCanvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, timer / fadeDuration);
            timer += Time.deltaTime;
            yield return null;
        }

        fadeCanvasGroup.alpha = targetAlpha;
        fadeCanvasGroup.blocksRaycasts = targetAlpha != 0;
    }
}
