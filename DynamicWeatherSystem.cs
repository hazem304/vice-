// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 2 — Procedural City & World Streaming
// File:    _Project/Scripts/World/DynamicWeatherSystem.cs
// Author:  Lead Game Architect
// Purpose: Tropical coastal dynamic weather controller supporting ClearSunny,
//          PartlyCloudy, CoastalFog, TropicalRain, and Thunderstorm states.
//          Drives rain VFX, fog density, wind zones, lightning flashes, global
//          wet-asphalt reflection shader parameters, EventBus notifications
//          (for Pedestrian umbrellas & Vehicle tire grip), and SaveLoadSystem.
// ============================================================================

using System;
using UnityEngine;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Core.SaveSystem;

namespace ViceCity.World
{
    public enum WeatherPresetType
    {
        ClearSunny = 0,
        PartlyCloudy = 1,
        CoastalFog = 2,
        TropicalRain = 3,
        Thunderstorm = 4
    }

    /// <summary>
    /// Simulates Miami's sudden tropical storms, coastal mist, and wet asphalt neon reflections.
    /// Broadcasts weather state through <see cref="EventBus"/> so vehicles reduce tire friction
    /// and pedestrians open umbrellas automatically.
    /// </summary>
    public sealed class DynamicWeatherSystem : MonoBehaviour, ISaveable
    {
        private static readonly int GlobalWetnessId = Shader.PropertyToID("_GlobalWetness");
        private static readonly int GlobalPuddleLevelId = Shader.PropertyToID("_GlobalPuddleLevel");
        private static readonly int RainRippleSpeedId = Shader.PropertyToID("_RainRippleSpeed");

        [Header("References")]
        [SerializeField] private DayNightCycleController dayNightController;
        [SerializeField] private Transform followTarget;
        [SerializeField] private ParticleSystem rainParticleSystem;
        [SerializeField] private WindZone palmWindZone;
        [SerializeField] private Light lightningFlashLight;
        [SerializeField] private AudioSource rainLoopAudio;
        [SerializeField] private AudioSource thunderOneShotAudio;
        [SerializeField] private AudioClip[] thunderClips;

        [Header("Road Wetness & Asphalt Reflections")]
        [SerializeField] private Material[] wettableAsphaltMaterials;
        [SerializeField, Range(0f, 1f)] private float dryAsphaltSmoothness = 0.22f;
        [SerializeField, Range(0f, 1f)] private float wetAsphaltSmoothness = 0.88f;

        [Header("Weather Transitions")]
        [SerializeField] private WeatherPresetType currentWeather = WeatherPresetType.ClearSunny;
        [SerializeField] private bool enableAutomaticWeatherCycle = true;
        [SerializeField, Min(30f)] private float minStateDurationSeconds = 150f;
        [SerializeField, Min(60f)] private float maxStateDurationSeconds = 360f;
        [SerializeField, Min(1f)] private float transitionSpeed = 0.12f;
        [SerializeField] private float rainHeightOffset = 22f;

        [Header("Thunderstorm Tuning")]
        [SerializeField] private float minLightningInterval = 7f;
        [SerializeField] private float maxLightningInterval = 18f;
        [SerializeField] private float maxLightningIntensity = 4.5f;

        private SaveLoadSystem _saveLoadSystem;
        private WeatherPresetType _targetWeather;
        private float _stateTimer;
        private float _lightningTimer;
        private float _lightningDecay;

        private float _currentRainIntensity;
        private float _currentFogDensity;
        private float _currentWetness;
        private float _currentWindMain;

        public string SaveKey => "World.DynamicWeather";
        public WeatherPresetType CurrentWeather => _targetWeather;
        public float CurrentRainIntensity01 => _currentRainIntensity;
        public float CurrentRoadWetness01 => _currentWetness;

        private void Awake()
        {
            _targetWeather = currentWeather;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;

            if (lightningFlashLight != null)
            {
                lightningFlashLight.intensity = 0f;
                lightningFlashLight.enabled = false;
            }

            ScheduleNextWeatherChange();
            _lightningTimer = UnityEngine.Random.Range(minLightningInterval, maxLightningInterval);
        }

        private void Start()
        {
            if (followTarget == null)
            {
                GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
                if (playerObj != null)
                {
                    followTarget = playerObj.transform;
                }
            }

            if (GameManager.HasInstance && GameManager.Instance.TryGetService(out _saveLoadSystem))
            {
                _saveLoadSystem.RegisterSaveable(this);
            }

            ApplyWeatherTargetsImmediate(_targetWeather);
            PublishWeatherEvent();
        }

        private void OnDestroy()
        {
            if (_saveLoadSystem != null)
            {
                _saveLoadSystem.UnregisterSaveable(this);
            }
        }

        private void Update()
        {
            if (GameManager.HasInstance && GameManager.Instance.IsPaused)
            {
                return;
            }

            if (enableAutomaticWeatherCycle)
            {
                _stateTimer -= Time.deltaTime;
                if (_stateTimer <= 0f)
                {
                    SetWeather(PickNextTropicalWeather(_targetWeather));
                }
            }

            UpdateWeatherInterpolation(Time.deltaTime);
            UpdateRainEmitterPosition();
            UpdateThunderstormLightning(Time.deltaTime);
        }

        /// <summary>Smoothly transitions to a new tropical weather preset and notifies all modules.</summary>
        public void SetWeather(WeatherPresetType newWeather, bool immediate = false)
        {
            _targetWeather = newWeather;
            currentWeather = newWeather;
            ScheduleNextWeatherChange();

            if (immediate)
            {
                ApplyWeatherTargetsImmediate(newWeather);
            }

            PublishWeatherEvent();
        }

        // ====================================================================
        // INTERPOLATION & SHADER WETNESS
        // ====================================================================

        private void UpdateWeatherInterpolation(float dt)
        {
            GetPresetTargets(_targetWeather, out float targetRain, out float targetFog, out float targetWind, out float targetLightMult);

            float step = transitionSpeed * dt;
            _currentRainIntensity = Mathf.MoveTowards(_currentRainIntensity, targetRain, step);
            _currentFogDensity = Mathf.MoveTowards(_currentFogDensity, targetFog, step * 0.05f);
            _currentWindMain = Mathf.MoveTowards(_currentWindMain, targetWind, step * 2.5f);

            // Asphalt gets wet quickly during rain and dries slowly afterward
            float wetnessRate = targetRain > 0.05f ? step * 1.4f : step * 0.35f;
            float targetWetness = targetRain > 0.05f ? Mathf.Clamp01(targetRain + 0.15f) : 0f;
            _currentWetness = Mathf.MoveTowards(_currentWetness, targetWetness, wetnessRate);

            ApplyRuntimeEffects(targetLightMult);
        }

        private void ApplyWeatherTargetsImmediate(WeatherPresetType preset)
        {
            GetPresetTargets(preset, out float targetRain, out float targetFog, out float targetWind, out float targetLightMult);
            _currentRainIntensity = targetRain;
            _currentFogDensity = targetFog;
            _currentWindMain = targetWind;
            _currentWetness = targetRain > 0.05f ? Mathf.Clamp01(targetRain + 0.15f) : 0f;

            ApplyRuntimeEffects(targetLightMult);
        }

        private void ApplyRuntimeEffects(float targetSunDarkening)
        {
            RenderSettings.fogDensity = _currentFogDensity;

            if (dayNightController != null)
            {
                float blendedDarkening = Mathf.Lerp(1f, targetSunDarkening, Mathf.Max(_currentRainIntensity, _currentFogDensity * 40f));
                dayNightController.SetWeatherDarkeningMultiplier(blendedDarkening);
            }

            // Rain particles
            if (rainParticleSystem != null)
            {
                var emission = rainParticleSystem.emission;
                emission.rateOverTime = _currentRainIntensity * 1400f;

                if (_currentRainIntensity > 0.02f && !rainParticleSystem.isPlaying)
                {
                    rainParticleSystem.Play();
                }
                else if (_currentRainIntensity <= 0.02f && rainParticleSystem.isPlaying)
                {
                    rainParticleSystem.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            // Rain ambient audio
            if (rainLoopAudio != null)
            {
                rainLoopAudio.volume = _currentRainIntensity * 0.65f;
                if (_currentRainIntensity > 0.02f && !rainLoopAudio.isPlaying)
                {
                    rainLoopAudio.Play();
                }
                else if (_currentRainIntensity <= 0.02f && rainLoopAudio.isPlaying)
                {
                    rainLoopAudio.Stop();
                }
            }

            // Palm tree wind force
            if (palmWindZone != null)
            {
                palmWindZone.windMain = _currentWindMain;
                palmWindZone.windTurbulence = _currentWindMain * 0.45f;
            }

            // Global shader parameters for wet asphalt & neon puddle reflections
            Shader.SetGlobalFloat(GlobalWetnessId, _currentWetness);
            Shader.SetGlobalFloat(GlobalPuddleLevelId, Mathf.Clamp01(_currentWetness * 1.15f - 0.1f));
            Shader.SetGlobalFloat(RainRippleSpeedId, _currentRainIntensity * 2.2f);

            // Standard URP Lit fallback: raise smoothness on asphalt materials for glossy neon reflections
            if (wettableAsphaltMaterials != null)
            {
                float smoothness = Mathf.Lerp(dryAsphaltSmoothness, wetAsphaltSmoothness, _currentWetness);
                for (int i = 0; i < wettableAsphaltMaterials.Length; i++)
                {
                    Material mat = wettableAsphaltMaterials[i];
                    if (mat != null && mat.HasProperty("_Smoothness"))
                    {
                        mat.SetFloat("_Smoothness", smoothness);
                    }
                }
            }
        }

        private void UpdateRainEmitterPosition()
        {
            if (rainParticleSystem == null || followTarget == null)
            {
                return;
            }

            Vector3 pos = followTarget.position;
            rainParticleSystem.transform.position = new Vector3(pos.x, pos.y + rainHeightOffset, pos.z);
        }

        private void UpdateThunderstormLightning(float dt)
        {
            if (lightningFlashLight == null)
            {
                return;
            }

            if (_lightningDecay > 0f)
            {
                _lightningDecay = Mathf.Max(0f, _lightningDecay - dt * 5.5f);
                lightningFlashLight.intensity = _lightningDecay * maxLightningIntensity;
                lightningFlashLight.enabled = _lightningDecay > 0.01f;
            }

            if (_targetWeather != WeatherPresetType.Thunderstorm || _currentRainIntensity < 0.65f)
            {
                return;
            }

            _lightningTimer -= dt;
            if (_lightningTimer <= 0f)
            {
                _lightningTimer = UnityEngine.Random.Range(minLightningInterval, maxLightningInterval);
                _lightningDecay = 1f;
                lightningFlashLight.enabled = true;
                lightningFlashLight.intensity = maxLightningIntensity;

                if (thunderOneShotAudio != null && thunderClips != null && thunderClips.Length > 0)
                {
                    AudioClip clip = thunderClips[UnityEngine.Random.Range(0, thunderClips.Length)];
                    if (clip != null)
                    {
                        thunderOneShotAudio.PlayOneShot(clip, UnityEngine.Random.Range(0.65f, 1.0f));
                    }
                }
            }
        }

        private static void GetPresetTargets(
            WeatherPresetType preset,
            out float rainIntensity,
            out float fogDensity,
            out float windMain,
            out float lightMultiplier)
        {
            switch (preset)
            {
                case WeatherPresetType.PartlyCloudy:
                    rainIntensity = 0f;
                    fogDensity = 0.0022f;
                    windMain = 0.55f;
                    lightMultiplier = 0.85f;
                    break;

                case WeatherPresetType.CoastalFog:
                    rainIntensity = 0f;
                    fogDensity = 0.014f;
                    windMain = 0.35f;
                    lightMultiplier = 0.72f;
                    break;

                case WeatherPresetType.TropicalRain:
                    rainIntensity = 0.72f;
                    fogDensity = 0.0085f;
                    windMain = 1.45f;
                    lightMultiplier = 0.55f;
                    break;

                case WeatherPresetType.Thunderstorm:
                    rainIntensity = 1.0f;
                    fogDensity = 0.012f;
                    windMain = 2.6f;
                    lightMultiplier = 0.38f;
                    break;

                case WeatherPresetType.ClearSunny:
                default:
                    rainIntensity = 0f;
                    fogDensity = 0.0012f;
                    windMain = 0.3f;
                    lightMultiplier = 1.0f;
                    break;
            }
        }

        private static WeatherPresetType PickNextTropicalWeather(WeatherPresetType current)
        {
            float roll = UnityEngine.Random.value;
            if (current == WeatherPresetType.Thunderstorm)
            {
                return roll < 0.6f ? WeatherPresetType.TropicalRain : WeatherPresetType.PartlyCloudy;
            }

            if (roll < 0.42f) return WeatherPresetType.ClearSunny;
            if (roll < 0.65f) return WeatherPresetType.PartlyCloudy;
            if (roll < 0.78f) return WeatherPresetType.CoastalFog;
            if (roll < 0.92f) return WeatherPresetType.TropicalRain;
            return WeatherPresetType.Thunderstorm;
        }

        private void ScheduleNextWeatherChange()
        {
            _stateTimer = UnityEngine.Random.Range(minStateDurationSeconds, maxStateDurationSeconds);
        }

        private void PublishWeatherEvent()
        {
            float timeOfDay = dayNightController != null ? dayNightController.NormalizedTimeOfDay : 0.35f;
            int dayCount = dayNightController != null ? dayNightController.CurrentDayCount : 1;

            EventBus.Publish(
                new WorldEnvironmentChangedEvent(timeOfDay, dayCount, _targetWeather.ToString(), _currentRainIntensity),
                sticky: true);
        }

        // ====================================================================
        // ISAVEABLE IMPLEMENTATION
        // ====================================================================

        public void CaptureState(GameSaveData saveData)
        {
            if (saveData?.World == null)
            {
                return;
            }

            saveData.World.WeatherId = _targetWeather.ToString();
            saveData.World.RainIntensity01 = _currentRainIntensity;
        }

        public void RestoreState(GameSaveData saveData)
        {
            if (saveData?.World == null)
            {
                return;
            }

            if (Enum.TryParse(saveData.World.WeatherId, ignoreCase: true, out WeatherPresetType parsed))
            {
                SetWeather(parsed, immediate: true);
            }
        }
    }
}
