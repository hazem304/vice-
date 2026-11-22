// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 1 — Core Architecture & Project Setup
// File:    _Project/Scripts/Core/Managers/GameManager.cs
// Author:  Lead Game Architect
// Purpose: Persistent central GameManager managing global GameState FSM,
//          service locator registry, cursor/timeScale control, scene loading,
//          and clean lifecycle orchestration across all 7 modules.
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using ViceCity.Core.Events;

namespace ViceCity.Core
{
    /// <summary>
    /// High-level finite state machine states for the open-world runtime.
    /// </summary>
    public enum GameState
    {
        Boot = 0,
        MainMenu = 1,
        Loading = 2,
        Gameplay = 3,
        Paused = 4,
        Cutscene = 5,
        InPhoneOrShopUI = 6,
        Wasted = 7,
        Busted = 8
    }

    /// <summary>
    /// Interface for lightweight runtime services registered with <see cref="GameManager"/>.
    /// </summary>
    public interface IGameService
    {
        void Initialize();
        void Shutdown();
    }

    /// <summary>
    /// Persistent central orchestrator for the Vice Coast open-world project.
    /// Guarantees single-instance execution, validated state transitions, safe
    /// asynchronous scene loading, and decoupled service access.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameManager : MonoBehaviour
    {
        private static GameManager _instance;
        private static bool _isQuitting;

        /// <summary>
        /// Global accessor for the active <see cref="GameManager"/> instance.
        /// </summary>
        public static GameManager Instance
        {
            get
            {
                if (_isQuitting)
                {
                    return null;
                }

                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<GameManager>();
                    if (_instance == null)
                    {
                        GameObject host = new GameObject("[GameManager]");
                        _instance = host.AddComponent<GameManager>();
                    }
                }

                return _instance;
            }
        }

        /// <summary>Returns true if a live GameManager instance currently exists.</summary>
        public static bool HasInstance => _instance != null && !_isQuitting;

        [Header("Runtime Configuration")]
        [SerializeField] private GameState initialState = GameState.Gameplay;
        [SerializeField, Range(30, 240)] private int targetFrameRate = 60;
        [SerializeField] private bool enableVSync = false;
        [SerializeField] private bool lockCursorDuringGameplay = true;
        [SerializeField] private float wastedOrBustedRespawnDelay = 4.0f;

        [Header("Scene Names")]
        [SerializeField] private string mainMenuSceneName = "MainMenu";
        [SerializeField] private string openWorldSceneName = "ViceCoast_Main";

        private readonly Dictionary<Type, IGameService> _services = new Dictionary<Type, IGameService>(16);
        private readonly HashSet<string> _pauseSources = new HashSet<string>(StringComparer.Ordinal);

        private GameState _currentState = GameState.Boot;
        private GameState _stateBeforePause = GameState.Gameplay;
        private float _activeGameplayTimeScale = 1f;
        private float _sessionPlaytimeSeconds;
        private Coroutine _activeSceneLoadCoroutine;
        private Coroutine _respawnCoroutine;

        /// <summary>Current validated global state.</summary>
        public GameState CurrentState => _currentState;

        /// <summary>Returns true when one or more pause tokens are active.</summary>
        public bool IsPaused => _currentState == GameState.Paused || _pauseSources.Count > 0;

        /// <summary>Total unpaused gameplay time elapsed in the current session (seconds).</summary>
        public float SessionPlaytimeSeconds => _sessionPlaytimeSeconds;

        /// <summary>Configured name of the primary open-world scene.</summary>
        public string OpenWorldSceneName => openWorldSceneName;

        /// <summary>Configured name of the main menu scene.</summary>
        public string MainMenuSceneName => mainMenuSceneName;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _instance = null;
            _isQuitting = false;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning($"[GameManager] Duplicate instance detected on '{gameObject.name}'. Destroying duplicate.");
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            ApplyGraphicsSettings();
            TransitionToState(initialState, force: true);
        }

        private void Update()
        {
            if (_currentState == GameState.Gameplay)
            {
                _sessionPlaytimeSeconds += Time.unscaledDeltaTime;
            }
        }

        private void OnApplicationQuit()
        {
            _isQuitting = true;
            ShutdownAllServices();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                ShutdownAllServices();
                _instance = null;
            }
        }

        // ====================================================================
        // SERVICE REGISTRY (Lightweight Dependency Locator)
        // ====================================================================

        /// <summary>
        /// Registers and initializes a runtime service of type <typeparamref name="TService"/>.
        /// </summary>
        public void RegisterService<TService>(TService service) where TService : class, IGameService
        {
            if (service == null)
            {
                Debug.LogError($"[GameManager] Attempted to register null service for {typeof(TService).Name}.");
                return;
            }

            Type serviceType = typeof(TService);
            if (_services.TryGetValue(serviceType, out IGameService existing))
            {
                if (ReferenceEquals(existing, service))
                {
                    return;
                }

                Debug.LogWarning($"[GameManager] Replacing existing service {serviceType.Name}.");
                SafeShutdownService(existing);
            }

            _services[serviceType] = service;

            try
            {
                service.Initialize();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameManager] Failed to initialize service {serviceType.Name}: {ex}");
            }
        }

        /// <summary>
        /// Unregisters and shuts down a runtime service of type <typeparamref name="TService"/>.
        /// </summary>
        public void UnregisterService<TService>() where TService : class, IGameService
        {
            Type serviceType = typeof(TService);
            if (_services.TryGetValue(serviceType, out IGameService service))
            {
                SafeShutdownService(service);
                _services.Remove(serviceType);
            }
        }

        /// <summary>
        /// Attempts to retrieve a registered service without throwing if missing.
        /// </summary>
        public bool TryGetService<TService>(out TService service) where TService : class, IGameService
        {
            if (_services.TryGetValue(typeof(TService), out IGameService raw) && raw is TService typed)
            {
                service = typed;
                return true;
            }

            service = null;
            return false;
        }

        // ====================================================================
        // GAME STATE MACHINE
        // ====================================================================

        /// <summary>
        /// Transitions the game to a new <see cref="GameState"/>, updating cursor lock,
        /// timeScale, and broadcasting <see cref="GameStateChangedEvent"/>.
        /// </summary>
        public bool TransitionToState(GameState newState, bool force = false)
        {
            if (!force && _currentState == newState)
            {
                return false;
            }

            GameState previousState = _currentState;
            _currentState = newState;

            ApplyStateSideEffects(newState);

            EventBus.Publish(new GameStateChangedEvent(previousState, newState), sticky: true);
            return true;
        }

        /// <summary>
        /// Requests a pause using a named token (e.g., "PauseMenu", "WeaponWheel", "SystemDialog").
        /// Supports overlapping pause requests without prematurely unpausing.
        /// </summary>
        public void RequestPause(string sourceToken)
        {
            if (string.IsNullOrWhiteSpace(sourceToken))
            {
                sourceToken = "DefaultPause";
            }

            bool wasPaused = IsPaused;
            _pauseSources.Add(sourceToken);

            if (!wasPaused)
            {
                _stateBeforePause = _currentState == GameState.Paused ? GameState.Gameplay : _currentState;
                TransitionToState(GameState.Paused);
                EventBus.Publish(new GamePauseToggledEvent(true), sticky: true);
            }
        }

        /// <summary>
        /// Releases a previously acquired pause token. Resumes gameplay only when all tokens are cleared.
        /// </summary>
        public void ReleasePause(string sourceToken)
        {
            if (string.IsNullOrWhiteSpace(sourceToken))
            {
                sourceToken = "DefaultPause";
            }

            if (!_pauseSources.Remove(sourceToken))
            {
                return;
            }

            if (_pauseSources.Count == 0 && _currentState == GameState.Paused)
            {
                GameState restoreState = _stateBeforePause == GameState.Boot ? GameState.Gameplay : _stateBeforePause;
                TransitionToState(restoreState);
                EventBus.Publish(new GamePauseToggledEvent(false), sticky: true);
            }
        }

        /// <summary>
        /// Convenience method for toggling the main pause menu.
        /// </summary>
        public void TogglePauseMenu()
        {
            const string menuToken = "PauseMenu";
            if (_pauseSources.Contains(menuToken))
            {
                ReleasePause(menuToken);
            }
            else if (_currentState == GameState.Gameplay || _currentState == GameState.InPhoneOrShopUI)
            {
                RequestPause(menuToken);
            }
        }

        /// <summary>
        /// Adjusts gameplay time dilation (e.g., 0.25f during Weapon Wheel or cinematic slow-mo)
        /// without breaking the pause system.
        /// </summary>
        public void SetGameplayTimeScale(float scale)
        {
            _activeGameplayTimeScale = Mathf.Clamp(scale, 0.05f, 2.0f);
            if (_currentState == GameState.Gameplay || _currentState == GameState.Cutscene)
            {
                Time.timeScale = _activeGameplayTimeScale;
                Time.fixedDeltaTime = 0.02f * Time.timeScale;
            }
        }

        /// <summary>
        /// Triggers the GTA-style Wasted or Busted sequence and schedules a clean respawn callback.
        /// </summary>
        public void TriggerPlayerFailureState(bool wasBusted, Action onRespawnReady = null)
        {
            GameState targetState = wasBusted ? GameState.Busted : GameState.Wasted;
            if (_currentState == targetState)
            {
                return;
            }

            _pauseSources.Clear();
            TransitionToState(targetState);

            if (_respawnCoroutine != null)
            {
                StopCoroutine(_respawnCoroutine);
            }

            _respawnCoroutine = StartCoroutine(RespawnSequenceRoutine(onRespawnReady));
        }

        // ====================================================================
        // ASYNCHRONOUS SCENE LOADING
        // ====================================================================

        /// <summary>
        /// Loads a Unity scene asynchronously while broadcasting progress via <see cref="SceneLoadProgressEvent"/>.
        /// </summary>
        public void LoadSceneAsync(string sceneName, GameState postLoadState = GameState.Gameplay, Action onCompleted = null)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                Debug.LogError("[GameManager] Cannot load scene: sceneName is null or empty.");
                return;
            }

            if (_activeSceneLoadCoroutine != null)
            {
                Debug.LogWarning($"[GameManager] Scene load already in progress. Ignoring request for '{sceneName}'.");
                return;
            }

            _activeSceneLoadCoroutine = StartCoroutine(LoadSceneRoutine(sceneName, postLoadState, onCompleted));
        }

        private IEnumerator LoadSceneRoutine(string sceneName, GameState postLoadState, Action onCompleted)
        {
            _pauseSources.Clear();
            TransitionToState(GameState.Loading);
            EventBus.Publish(new SceneLoadProgressEvent(sceneName, 0f, false));

            AsyncOperation operation;
            try
            {
                operation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameManager] Exception starting async load for scene '{sceneName}': {ex}");
                _activeSceneLoadCoroutine = null;
                TransitionToState(GameState.MainMenu);
                yield break;
            }

            if (operation == null)
            {
                Debug.LogError($"[GameManager] Scene '{sceneName}' could not be loaded. Verify it is added to Build Settings.");
                _activeSceneLoadCoroutine = null;
                TransitionToState(GameState.MainMenu);
                yield break;
            }

            operation.allowSceneActivation = false;

            while (operation.progress < 0.9f)
            {
                float normalized = Mathf.Clamp01(operation.progress / 0.9f);
                EventBus.Publish(new SceneLoadProgressEvent(sceneName, normalized * 0.95f, false));
                yield return null;
            }

            operation.allowSceneActivation = true;

            while (!operation.isDone)
            {
                yield return null;
            }

            _activeSceneLoadCoroutine = null;
            EventBus.Publish(new SceneLoadProgressEvent(sceneName, 1f, true));
            TransitionToState(postLoadState);

            try
            {
                onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameManager] Exception in post-scene-load callback for '{sceneName}': {ex}");
            }
        }

        private IEnumerator RespawnSequenceRoutine(Action onRespawnReady)
        {
            // Brief dramatic slow-motion effect reminiscent of GTA Wasted/Busted screens
            Time.timeScale = 0.25f;
            Time.fixedDeltaTime = 0.02f * Time.timeScale;

            float elapsed = 0f;
            while (elapsed < wastedOrBustedRespawnDelay)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            _respawnCoroutine = null;
            TransitionToState(GameState.Gameplay);

            try
            {
                onRespawnReady?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameManager] Exception during respawn callback: {ex}");
            }
        }

        private void ApplyGraphicsSettings()
        {
            QualitySettings.vSyncCount = enableVSync ? 1 : 0;
            Application.targetFrameRate = enableVSync ? -1 : targetFrameRate;
        }

        private void ApplyStateSideEffects(GameState state)
        {
            switch (state)
            {
                case GameState.Gameplay:
                    Time.timeScale = _activeGameplayTimeScale;
                    Time.fixedDeltaTime = 0.02f * Time.timeScale;
                    AudioListener.pause = false;
                    SetCursorLocked(lockCursorDuringGameplay);
                    break;

                case GameState.Paused:
                    Time.timeScale = 0f;
                    Time.fixedDeltaTime = 0.02f;
                    AudioListener.pause = true;
                    SetCursorLocked(false);
                    break;

                case GameState.MainMenu:
                case GameState.InPhoneOrShopUI:
                    Time.timeScale = state == GameState.MainMenu ? 1f : _activeGameplayTimeScale;
                    Time.fixedDeltaTime = 0.02f * Time.timeScale;
                    AudioListener.pause = false;
                    SetCursorLocked(false);
                    break;

                case GameState.Cutscene:
                case GameState.Loading:
                    Time.timeScale = 1f;
                    Time.fixedDeltaTime = 0.02f;
                    AudioListener.pause = false;
                    SetCursorLocked(true);
                    break;

                case GameState.Wasted:
                case GameState.Busted:
                    AudioListener.pause = false;
                    SetCursorLocked(false);
                    break;
            }
        }

        private static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void ShutdownAllServices()
        {
            if (_services.Count == 0)
            {
                return;
            }

            List<IGameService> snapshot = new List<IGameService>(_services.Values);
            _services.Clear();

            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                SafeShutdownService(snapshot[i]);
            }
        }

        private static void SafeShutdownService(IGameService service)
        {
            try
            {
                service?.Shutdown();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameManager] Error while shutting down service {service?.GetType().Name}: {ex}");
            }
        }
    }
}
