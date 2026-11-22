// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 7 — Cinematics & Visual Polish (Master HUD Integration)
// File:    _Project/Scripts/UI/ViceHudController.cs
// Author:  Lead Game Architect
// Purpose: Zero-coupling EventBus-driven Master HUD binding all 7 modules:
//          - Cash balance, Wanted Stars (1–5) with evasion blink
//          - Equipped weapon name, clip/reserve ammo & reload indicator
//          - Active mission objective banner, GPS distance & countdown timer
//          - Health / Armor / Stamina bars & Vehicle Speedometer / Nitro gauge
//          - Wasted / Busted full-screen cinematic overlay banner.
// ============================================================================

using UnityEngine;
using UnityEngine.UI;
using ViceCity.AI;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Missions;
using ViceCity.Player;
using ViceCity.Vehicles;

namespace ViceCity.UI
{
    /// <summary>
    /// Subscribes to all core <see cref="EventBus"/> channels and updates Unity UI widgets cleanly.
    /// </summary>
    public sealed class ViceHudController : MonoBehaviour
    {
        [Header("Player & Vehicle Live References")]
        [SerializeField] private ThirdPersonController playerController;
        [SerializeField] private WantedSystemManager wantedManager;
        [SerializeField] private MissionManager missionManager;
        [SerializeField] private MinimapGPSController gpsController;

        [Header("Vitals & Vehicle Gauges")]
        [SerializeField] private Slider healthSlider;
        [SerializeField] private Slider armorSlider;
        [SerializeField] private Slider staminaSlider;
        [SerializeField] private GameObject vehicleHudPanel;
        [SerializeField] private Text speedometerText;
        [SerializeField] private Slider nitroSlider;

        [Header("Top-Right GTA Cluster (Cash, Weapon, Wanted Stars)")]
        [SerializeField] private Text cashBalanceText;
        [SerializeField] private Text weaponAmmoText;
        [SerializeField] private Text clockWeatherText;
        [SerializeField] private Image[] wantedStarIcons;
        [SerializeField] private Color activeStarColor = new Color(1.0f, 0.82f, 0.14f, 1f);
        [SerializeField] private Color inactiveStarColor = new Color(0.25f, 0.25f, 0.25f, 0.35f);

        [Header("Mission & Status Overlays")]
        [SerializeField] private Text missionObjectiveText;
        [SerializeField] private Text gpsDistanceText;
        [SerializeField] private GameObject wastedBannerPanel;
        [SerializeField] private GameObject bustedBannerPanel;

        private EventSubscription _currencySub;
        private EventSubscription _weaponSub;
        private EventSubscription _wantedSub;
        private EventSubscription _missionSub;
        private EventSubscription _envSub;
        private EventSubscription _stateSub;
        private EventSubscription _vehicleSub;

        private ArcadeRealisticCarController _activeVehicle;
        private int _currentStars;

        private void OnEnable()
        {
            _currencySub = EventBus.Subscribe<PlayerCurrencyChangedEvent>(OnCurrencyChanged, replayStickyIfAvailable: true);
            _weaponSub = EventBus.Subscribe<WeaponStateChangedEvent>(OnWeaponStateChanged, replayStickyIfAvailable: true);
            _wantedSub = EventBus.Subscribe<WantedLevelChangedEvent>(OnWantedLevelChanged, replayStickyIfAvailable: true);
            _missionSub = EventBus.Subscribe<MissionStatusChangedEvent>(OnMissionStatusChanged, replayStickyIfAvailable: true);
            _envSub = EventBus.Subscribe<WorldEnvironmentChangedEvent>(OnEnvironmentChanged, replayStickyIfAvailable: true);
            _stateSub = EventBus.Subscribe<GameStateChangedEvent>(OnGameStateChanged, replayStickyIfAvailable: true);
            _vehicleSub = EventBus.Subscribe<PlayerVehicleTransitionEvent>(OnVehicleTransition);
        }

        private void OnDisable()
        {
            _currencySub.Dispose();
            _weaponSub.Dispose();
            _wantedSub.Dispose();
            _missionSub.Dispose();
            _envSub.Dispose();
            _stateSub.Dispose();
            _vehicleSub.Dispose();
        }

        private void Start()
        {
            if (vehicleHudPanel != null) vehicleHudPanel.SetActive(false);
            if (wastedBannerPanel != null) wastedBannerPanel.SetActive(false);
            if (bustedBannerPanel != null) bustedBannerPanel.SetActive(false);
        }

        private void Update()
        {
            UpdateVitalsSliders();
            UpdateVehicleHud();
            UpdateWantedStarBlink();
            UpdateGpsReadout();
        }

        private void UpdateVitalsSliders()
        {
            if (playerController == null) return;
            if (healthSlider != null) healthSlider.value = playerController.Health / 100f;
            if (armorSlider != null) armorSlider.value = playerController.Armor / 100f;
            if (staminaSlider != null) staminaSlider.value = playerController.Stamina / 100f;
        }

        private void UpdateVehicleHud()
        {
            if (_activeVehicle == null) return;

            if (speedometerText != null)
            {
                speedometerText.text = $"{Mathf.RoundToInt(_activeVehicle.SpeedKmh):000} KM/H";
            }

            if (nitroSlider != null)
            {
                nitroSlider.value = _activeVehicle.NitroNormalized;
            }
        }

        private void UpdateWantedStarBlink()
        {
            if (wantedStarIcons == null || _currentStars <= 0) return;

            bool isEvading = wantedManager != null && !wantedManager.IsActivelySpotted;
            float alpha = isEvading ? (0.35f + 0.65f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 5f))) : 1f;

            for (int i = 0; i < wantedStarIcons.Length; i++)
            {
                if (wantedStarIcons[i] == null) continue;
                if (i < _currentStars)
                {
                    Color c = activeStarColor;
                    c.a = alpha;
                    wantedStarIcons[i].color = c;
                }
                else
                {
                    wantedStarIcons[i].color = inactiveStarColor;
                }
            }
        }

        private void UpdateGpsReadout()
        {
            if (gpsDistanceText == null) return;

            if (gpsController != null && gpsController.HasActiveWaypoint)
            {
                float dist = gpsController.RemainingDistanceMeters;
                string timerSuffix = (missionManager != null && missionManager.HasActiveMission && missionManager.ActiveMission.GlobalTimeLimitSeconds > 0f)
                    ? $"  |  {Mathf.CeilToInt(missionManager.MissionTimeRemainingSeconds)}s"
                    : string.Empty;

                gpsDistanceText.text = dist >= 1000f
                    ? $"GPS: {(dist / 1000f):0.00} km{timerSuffix}"
                    : $"GPS: {Mathf.RoundToInt(dist)} m{timerSuffix}";
            }
            else
            {
                gpsDistanceText.text = string.Empty;
            }
        }

        // ====================================================================
        // EVENTBUS HANDLERS
        // ====================================================================

        private void OnCurrencyChanged(PlayerCurrencyChangedEvent evt)
        {
            if (cashBalanceText != null)
            {
                cashBalanceText.text = $"${evt.CurrentBalance:N0}";
            }
        }

        private void OnWeaponStateChanged(WeaponStateChangedEvent evt)
        {
            if (weaponAmmoText != null)
            {
                weaponAmmoText.text = evt.IsReloading
                    ? $"{evt.DisplayName} [RELOADING]"
                    : $"{evt.DisplayName}  {evt.ClipAmmo}/{evt.ReserveAmmo}";
            }
        }

        private void OnWantedLevelChanged(WantedLevelChangedEvent evt)
        {
            _currentStars = evt.CurrentStars;
            if (wantedStarIcons == null) return;

            for (int i = 0; i < wantedStarIcons.Length; i++)
            {
                if (wantedStarIcons[i] != null)
                {
                    wantedStarIcons[i].color = i < _currentStars ? activeStarColor : inactiveStarColor;
                }
            }
        }

        private void OnMissionStatusChanged(MissionStatusChangedEvent evt)
        {
            if (missionObjectiveText == null) return;

            missionObjectiveText.text = evt.Status switch
            {
                MissionLifecycleStatus.Completed => $"MISSION PASSED: {evt.ObjectiveDescription}",
                MissionLifecycleStatus.Failed => $"MISSION FAILED: {evt.ObjectiveDescription}",
                _ => evt.ObjectiveDescription
            };
        }

        private void OnEnvironmentChanged(WorldEnvironmentChangedEvent evt)
        {
            if (clockWeatherText == null) return;
            int totalMinutes = Mathf.FloorToInt(evt.NormalizedTimeOfDay * 1440f);
            int h = (totalMinutes / 60) % 24;
            int m = totalMinutes % 60;
            clockWeatherText.text = $"Day {evt.DayCount}  {h:00}:{m:00}  |  {evt.WeatherId}";
        }

        private void OnGameStateChanged(GameStateChangedEvent evt)
        {
            if (wastedBannerPanel != null) wastedBannerPanel.SetActive(evt.NewState == GameState.Wasted);
            if (bustedBannerPanel != null) bustedBannerPanel.SetActive(evt.NewState == GameState.Busted);
        }

        private void OnVehicleTransition(PlayerVehicleTransitionEvent evt)
        {
            _activeVehicle = (evt.EnteredVehicle && evt.VehicleObject != null)
                ? evt.VehicleObject.GetComponent<ArcadeRealisticCarController>()
                : null;

            if (vehicleHudPanel != null)
            {
                vehicleHudPanel.SetActive(_activeVehicle != null);
            }
        }
    }
}
