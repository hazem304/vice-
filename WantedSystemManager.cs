// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 5 — Living City & NPC AI (Pedestrians & Police)
// File:    _Project/Scripts/AI/WantedSystemManager.cs
// Author:  Lead Game Architect
// Purpose: GTA-style 1-to-5 Star Wanted System:
//          - Accumulates crime heat from gunshots, assaults, homicides & thefts
//          - Escalates Stars 1..5 cleanly and broadcasts WantedLevelChangedEvent
//          - Tracks police Line-of-Sight & evasion cooldown timer (blinking stars)
//          - Integrates with GameManager (IGameService) & SaveLoadSystem (ISaveable).
// ============================================================================

using UnityEngine;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Core.SaveSystem;
using ViceCity.Player;

namespace ViceCity.AI
{
    /// <summary>
    /// Central Wanted Level authority managing Star ratings (0–5), last known player
    /// coordinates, line-of-sight cooldown evasion, and crime escalation thresholds.
    /// </summary>
    public sealed class WantedSystemManager : MonoBehaviour, IGameService, ISaveable
    {
        [Header("Star Heat Thresholds (1 to 5 Stars)")]
        [SerializeField] private float[] starThresholds = { 25f, 80f, 170f, 300f, 480f };
        [SerializeField] private float[] evasionCooldownByStar = { 0f, 22f, 32f, 45f, 60f, 75f };
        [SerializeField] private float gunshotHeatAmount = 12f;

        [Header("Player Reference")]
        [SerializeField] private Transform playerTransform;

        private SaveLoadSystem _saveLoadSystem;
        private EventSubscription _crimeSub;
        private EventSubscription _gunshotSub;
        private EventSubscription _stateSub;

        private int _currentStars;
        private float _currentHeat;
        private float _timeSinceLastSpotted;
        private bool _isActivelySpotted;
        private Vector3 _lastKnownPlayerPosition;

        public string SaveKey => "AI.WantedSystem";
        public int CurrentStars => _currentStars;
        public bool IsWanted => _currentStars > 0;
        public bool IsActivelySpotted => _isActivelySpotted;
        public Vector3 LastKnownPlayerPosition => _lastKnownPlayerPosition;

        /// <summary>Normalized 0..1 evasion progress while hidden from police sight.</summary>
        public float EvasionProgress01
        {
            get
            {
                if (_currentStars <= 0 || _isActivelySpotted) return 0f;
                float required = evasionCooldownByStar[Mathf.Clamp(_currentStars, 0, evasionCooldownByStar.Length - 1)];
                return required > 0f ? Mathf.Clamp01(_timeSinceLastSpotted / required) : 1f;
            }
        }

        private void Awake()
        {
            if (GameManager.HasInstance)
            {
                GameManager.Instance.RegisterService(this);
            }
        }

        private void OnEnable()
        {
            _crimeSub = EventBus.Subscribe<CrimeCommittedEvent>(OnCrimeCommitted);
            _gunshotSub = EventBus.Subscribe<WeaponFiredEvent>(OnWeaponFired);
            _stateSub = EventBus.Subscribe<GameStateChangedEvent>(OnGameStateChanged);
        }

        private void OnDisable()
        {
            _crimeSub.Dispose();
            _gunshotSub.Dispose();
            _stateSub.Dispose();
        }

        private void Start()
        {
            if (playerTransform == null)
            {
                GameObject p = GameObject.FindGameObjectWithTag("Player");
                if (p != null) playerTransform = p.transform;
            }

            if (GameManager.HasInstance && GameManager.Instance.TryGetService(out _saveLoadSystem))
            {
                _saveLoadSystem.RegisterSaveable(this);
            }
        }

        private void OnDestroy()
        {
            if (_saveLoadSystem != null) _saveLoadSystem.UnregisterSaveable(this);
            if (GameManager.HasInstance) GameManager.Instance.UnregisterService<WantedSystemManager>();
        }

        public void Initialize() { }
        public void Shutdown() { }

        private void Update()
        {
            if (_currentStars <= 0) return;
            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay) return;

            if (_isActivelySpotted)
            {
                _timeSinceLastSpotted = 0f;
                if (playerTransform != null)
                {
                    _lastKnownPlayerPosition = playerTransform.position;
                }
                // Reset spotted flag each frame; Police units re-confirm LOS during their update tick
                _isActivelySpotted = false;
            }
            else
            {
                _timeSinceLastSpotted += Time.deltaTime;
                float requiredCooldown = evasionCooldownByStar[Mathf.Clamp(_currentStars, 0, evasionCooldownByStar.Length - 1)];
                if (_timeSinceLastSpotted >= requiredCooldown)
                {
                    ClearWantedLevel();
                }
            }
        }

        // ====================================================================
        // PUBLIC API FOR POLICE UNITS, MISSIONS & SHOPS
        // ====================================================================

        /// <summary>Called by patrolling police or helicopters when they have direct Line-of-Sight on the player.</summary>
        public void ReportPlayerSpotted(Vector3 confirmedPosition)
        {
            _isActivelySpotted = true;
            _timeSinceLastSpotted = 0f;
            _lastKnownPlayerPosition = confirmedPosition;
        }

        /// <summary>Adds crime heat and recalculates the 1-to-5 Star rating.</summary>
        public void AddCrimeHeat(float amount, Vector3 crimeLocation)
        {
            if (amount <= 0f) return;

            _currentHeat = Mathf.Clamp(_currentHeat + amount, 0f, starThresholds[starThresholds.Length - 1] + 50f);
            _lastKnownPlayerPosition = crimeLocation;
            _timeSinceLastSpotted = 0f;
            _isActivelySpotted = true;

            int evaluatedStars = EvaluateStarsFromHeat(_currentHeat);
            if (evaluatedStars > _currentStars)
            {
                SetWantedStars(evaluatedStars);
            }
        }

        /// <summary>Directly sets a specific Star level (used by scripted missions or Pay'n'Spray).</summary>
        public void SetWantedStars(int targetStars)
        {
            int clamped = Mathf.Clamp(targetStars, 0, 5);
            if (clamped == _currentStars) return;

            int previous = _currentStars;
            _currentStars = clamped;

            if (clamped == 0)
            {
                _currentHeat = 0f;
                _timeSinceLastSpotted = 0f;
                _isActivelySpotted = false;
            }
            else
            {
                float minHeat = starThresholds[clamped - 1];
                if (_currentHeat < minHeat) _currentHeat = minHeat;
                _timeSinceLastSpotted = 0f;
                if (playerTransform != null) _lastKnownPlayerPosition = playerTransform.position;
            }

            EventBus.Publish(new WantedLevelChangedEvent(previous, _currentStars, _lastKnownPlayerPosition), sticky: true);
            Debug.Log($"[WantedSystemManager] Wanted Level changed: {previous} -> {_currentStars} Star(s).");
        }

        public void ClearWantedLevel()
        {
            SetWantedStars(0);
        }

        private int EvaluateStarsFromHeat(float heat)
        {
            int stars = 0;
            for (int i = 0; i < starThresholds.Length; i++)
            {
                if (heat >= starThresholds[i])
                {
                    stars = i + 1;
                }
            }
            return Mathf.Clamp(stars, 0, 5);
        }

        private void OnCrimeCommitted(CrimeCommittedEvent evt)
        {
            AddCrimeHeat(evt.HeatValue, evt.Location);
        }

        private void OnWeaponFired(WeaponFiredEvent evt)
        {
            if (evt.Shooter != null && evt.Shooter.CompareTag("Player"))
            {
                AddCrimeHeat(gunshotHeatAmount, evt.Origin);
            }
        }

        private void OnGameStateChanged(GameStateChangedEvent evt)
        {
            if (evt.NewState == GameState.Wasted || evt.NewState == GameState.Busted)
            {
                ClearWantedLevel();
            }
        }

        // ====================================================================
        // ISAVEABLE IMPLEMENTATION
        // ====================================================================

        public void CaptureState(GameSaveData saveData)
        {
            if (saveData?.Player != null)
            {
                saveData.Player.WantedStars = _currentStars;
            }
        }

        public void RestoreState(GameSaveData saveData)
        {
            if (saveData?.Player != null)
            {
                SetWantedStars(saveData.Player.WantedStars);
            }
        }
    }
}
