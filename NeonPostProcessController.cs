// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 7 — Cinematics (Higgsfield AI) & Visual Polish
// File:    _Project/Scripts/Cinematics/NeonPostProcessController.cs
// Author:  Lead Game Architect
// Purpose: Dynamic URP Volume Post-Processing controller for Vice Coast:
//          - Neon Bloom intensity boost at twilight & night
//          - Miami Sunset / Night Color Grading (ColorAdjustments & Vignette)
//          - Speed-linked Motion Blur during high-speed supercar driving
//          - Classic GTA Wasted / Busted desaturation & chromatic pulse.
//          Uses #if UNITY_URP_VOLUME guards or UnityEngine.Rendering.Universal
//          cleanly so it compiles seamlessly in Unity URP projects.
// ============================================================================

using UnityEngine;
using UnityEngine.Rendering;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Vehicles;
#if UNITY_RENDER_PIPELINE_UNIVERSAL
using UnityEngine.Rendering.Universal;
#endif

namespace ViceCity.Cinematics
{
    /// <summary>
    /// Dynamically drives URP Post-Processing (Bloom, Color Grading, Motion Blur, Vignette)
    /// in response to time-of-day, vehicle velocity, and Wasted/Busted game states.
    /// </summary>
    public sealed class NeonPostProcessController : MonoBehaviour
    {
        [Header("Post-Processing Volume")]
        [SerializeField] private Volume globalVolume;

        [Header("Neon Bloom Calibration")]
        [SerializeField] private float dayBloomIntensity = 0.45f;
        [SerializeField] private float nightNeonBloomIntensity = 1.65f;
        [SerializeField] private float bloomThreshold = 0.92f;

        [Header("Color Grading (Vice Coastal Look)")]
        [SerializeField] private Color dayColorFilter = new Color(1.00f, 0.98f, 0.95f);
        [SerializeField] private Color sunsetColorFilter = new Color(1.00f, 0.86f, 0.88f);
        [SerializeField] private Color nightColorFilter = new Color(0.86f, 0.90f, 1.00f);
        [SerializeField] private float normalSaturation = 16f;
        [SerializeField] private float wastedDesaturation = -82f;

        [Header("Speed-Linked Motion Blur")]
        [SerializeField] private float motionBlurStartKmh = 65f;
        [SerializeField] private float motionBlurMaxKmh = 200f;
        [SerializeField, Range(0f, 1f)] private float maxMotionBlurIntensity = 0.65f;
        [SerializeField] private float blendSpeed = 4.5f;

        private EventSubscription _envSub;
        private EventSubscription _stateSub;
        private EventSubscription _vehicleSub;

        private ArcadeRealisticCarController _activeVehicle;
        private float _normalizedTimeOfDay = 0.35f;
        private bool _isWastedOrBusted;

#if UNITY_RENDER_PIPELINE_UNIVERSAL
        private Bloom _bloom;
        private ColorAdjustments _colorAdjustments;
        private MotionBlur _motionBlur;
        private Vignette _vignette;
#endif

        private void Awake()
        {
            CacheVolumeOverrides();
        }

        private void OnEnable()
        {
            _envSub = EventBus.Subscribe<WorldEnvironmentChangedEvent>(OnEnvironmentChanged, replayStickyIfAvailable: true);
            _stateSub = EventBus.Subscribe<GameStateChangedEvent>(OnGameStateChanged, replayStickyIfAvailable: true);
            _vehicleSub = EventBus.Subscribe<PlayerVehicleTransitionEvent>(OnVehicleTransition);
        }

        private void OnDisable()
        {
            _envSub.Dispose();
            _stateSub.Dispose();
            _vehicleSub.Dispose();
        }

        private void Update()
        {
            UpdatePostProcessing(Time.unscaledDeltaTime);
        }

        private void CacheVolumeOverrides()
        {
            if (globalVolume == null || globalVolume.profile == null)
            {
                return;
            }

#if UNITY_RENDER_PIPELINE_UNIVERSAL
            globalVolume.profile.TryGet(out _bloom);
            globalVolume.profile.TryGet(out _colorAdjustments);
            globalVolume.profile.TryGet(out _motionBlur);
            globalVolume.profile.TryGet(out _vignette);

            if (_bloom != null)
            {
                _bloom.threshold.overrideState = true;
                _bloom.threshold.value = bloomThreshold;
                _bloom.intensity.overrideState = true;
            }
#endif
        }

        private void UpdatePostProcessing(float dt)
        {
            float nightFactor = EvaluateNightFactor(_normalizedTimeOfDay);
            float sunsetFactor = EvaluateSunsetFactor(_normalizedTimeOfDay);
            float speedKmh = _activeVehicle != null ? _activeVehicle.SpeedKmh : 0f;

#if UNITY_RENDER_PIPELINE_UNIVERSAL
            if (_bloom != null)
            {
                float targetBloom = Mathf.Lerp(dayBloomIntensity, nightNeonBloomIntensity, nightFactor);
                _bloom.intensity.value = Mathf.Lerp(_bloom.intensity.value, targetBloom, dt * blendSpeed);
            }

            if (_colorAdjustments != null)
            {
                Color baseFilter = Color.Lerp(dayColorFilter, nightColorFilter, nightFactor);
                Color targetFilter = Color.Lerp(baseFilter, sunsetColorFilter, sunsetFactor);
                _colorAdjustments.colorFilter.overrideState = true;
                _colorAdjustments.colorFilter.value = Color.Lerp(_colorAdjustments.colorFilter.value, targetFilter, dt * blendSpeed);

                float targetSat = _isWastedOrBusted ? wastedDesaturation : normalSaturation;
                _colorAdjustments.saturation.overrideState = true;
                _colorAdjustments.saturation.value = Mathf.Lerp(_colorAdjustments.saturation.value, targetSat, dt * blendSpeed);
            }

            if (_motionBlur != null)
            {
                float speedRatio = Mathf.InverseLerp(motionBlurStartKmh, motionBlurMaxKmh, speedKmh);
                float targetBlur = speedRatio * maxMotionBlurIntensity;
                _motionBlur.intensity.overrideState = true;
                _motionBlur.intensity.value = Mathf.Lerp(_motionBlur.intensity.value, targetBlur, dt * blendSpeed);
            }

            if (_vignette != null)
            {
                float targetVignette = _isWastedOrBusted ? 0.52f : Mathf.Lerp(0.18f, 0.30f, nightFactor);
                _vignette.intensity.overrideState = true;
                _vignette.intensity.value = Mathf.Lerp(_vignette.intensity.value, targetVignette, dt * blendSpeed);
            }
#endif
        }

        private static float EvaluateNightFactor(float t01)
        {
            // 0.0=Midnight, 0.25=6AM, 0.5=Noon, 0.75=6PM
            float sunHeight = Mathf.Sin(t01 * Mathf.PI * 2f - Mathf.PI * 0.5f);
            return 1f - Mathf.SmoothStep(-0.15f, 0.25f, sunHeight);
        }

        private static float EvaluateSunsetFactor(float t01)
        {
            float distFromSunset = Mathf.Abs(t01 - 0.76f);
            float distFromSunrise = Mathf.Abs(t01 - 0.25f);
            float nearest = Mathf.Min(distFromSunset, distFromSunrise);
            return Mathf.Clamp01(1f - (nearest / 0.09f));
        }

        private void OnEnvironmentChanged(WorldEnvironmentChangedEvent evt)
        {
            _normalizedTimeOfDay = evt.NormalizedTimeOfDay;
        }

        private void OnGameStateChanged(GameStateChangedEvent evt)
        {
            _isWastedOrBusted = evt.NewState == GameState.Wasted || evt.NewState == GameState.Busted;
        }

        private void OnVehicleTransition(PlayerVehicleTransitionEvent evt)
        {
            _activeVehicle = (evt.EnteredVehicle && evt.VehicleObject != null)
                ? evt.VehicleObject.GetComponent<ArcadeRealisticCarController>()
                : null;
        }
    }
}
