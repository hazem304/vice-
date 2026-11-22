// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 2 — Procedural City & World Streaming
// File:    _Project/Scripts/World/DayNightCycleController.cs
// Author:  Lead Game Architect
// Purpose: Full 24-hour Miami / Vice City Day-Night lighting cycle featuring
//          sun & moon orbital lighting, tropical sunset pastel gradients,
//          automatic night-time neon & streetlamp activation, EventBus updates,
//          and seamless SaveLoadSystem persistence.
// ============================================================================

using System;
using UnityEngine;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Core.SaveSystem;

namespace ViceCity.World
{
    /// <summary>
    /// Controls the 24-hour solar/lunar lighting cycle, Vice City neon dusk triggers,
    /// and broadcasts time-of-day updates to all interested world and AI systems.
    /// </summary>
    public sealed class DayNightCycleController : MonoBehaviour, ISaveable
    {
        private static readonly int GlobalNightFactorId = Shader.PropertyToID("_GlobalNightFactor");

        [Header("Time Progression")]
        [SerializeField, Range(0f, 1f)] private float normalizedTimeOfDay = 0.35f; // 0.0=Midnight, 0.25=6AM, 0.5=Noon, 0.75=6PM
        [SerializeField, Min(1f)] private float fullDayDurationMinutes = 24f;     // 24 real minutes = 24 in-game hours (GTA standard)
        [SerializeField] private bool pauseTimeProgression = false;
        [SerializeField] private int currentDayCount = 1;

        [Header("Scene Lights")]
        [SerializeField] private Light sunLight;
        [SerializeField] private Light moonLight;
        [SerializeField] private float maxSunIntensity = 1.65f;
        [SerializeField] private float maxMoonIntensity = 0.35f;
        [SerializeField] private float sunAzimuthOffsetDegrees = 165f;

        [Header("Vice Coast Lighting Gradients")]
        [SerializeField] private Gradient sunColorGradient;
        [SerializeField] private Gradient ambientSkyGradient;
        [SerializeField] private Gradient ambientEquatorGradient;
        [SerializeField] private Gradient fogColorGradient;

        [Header("Night Neon & Street Lamps")]
        [SerializeField] private Light[] streetLampLights;
        [SerializeField] private Renderer[] neonEmissiveRenderers;
        [SerializeField, Range(0f, 1f)] private float duskStartNormalized = 0.74f;
        [SerializeField, Range(0f, 1f)] private float dawnEndNormalized = 0.26f;
        [SerializeField] private float nightNeonEmissionMultiplier = 1.0f;

        private SaveLoadSystem _saveLoadSystem;
        private float _weatherDarkeningFactor = 1f;
        private float _broadcastAccumulator;
        private bool _isNightActive;

        public string SaveKey => "World.DayNightCycle";

        /// <summary>Normalized time of day in [0..1) where 0.5 is 12:00 PM.</summary>
        public float NormalizedTimeOfDay => normalizedTimeOfDay;

        /// <summary>Current hour in [0..24).</summary>
        public float CurrentHour24 => normalizedTimeOfDay * 24f;

        /// <summary>Returns formatted 24-hour clock string (e.g., "19:45") for HUD/Smartphone UI.</summary>
        public string FormattedClockTime
        {
            get
            {
                int totalMinutes = Mathf.FloorToInt(normalizedTimeOfDay * 1440f);
                int hours = (totalMinutes / 60) % 24;
                int minutes = totalMinutes % 60;
                return $"{hours:00}:{minutes:00}";
            }
        }

        /// <summary>True during night hours when Vice Coast neon signage and street lamps illuminate.</summary>
        public bool IsNight => _isNightActive;

        public int CurrentDayCount => currentDayCount;

        private void Reset()
        {
            InitializeDefaultViceGradients();
        }

        private void Awake()
        {
            if (sunColorGradient == null || sunColorGradient.colorKeys.Length <= 2)
            {
                InitializeDefaultViceGradients();
            }

            if (sunLight == null)
            {
                sunLight = RenderSettings.sun;
            }
        }

        private void Start()
        {
            if (GameManager.HasInstance && GameManager.Instance.TryGetService(out _saveLoadSystem))
            {
                _saveLoadSystem.RegisterSaveable(this);
            }

            ApplyLightingImmediate();
            PublishEnvironmentUpdate();
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

            if (!pauseTimeProgression)
            {
                float dayDurationSeconds = Mathf.Max(60f, fullDayDurationMinutes * 60f);
                normalizedTimeOfDay += Time.deltaTime / dayDurationSeconds;

                if (normalizedTimeOfDay >= 1f)
                {
                    normalizedTimeOfDay -= 1f;
                    currentDayCount++;
                }
            }

            ApplyLightingImmediate();

            _broadcastAccumulator += Time.deltaTime;
            if (_broadcastAccumulator >= 2.0f)
            {
                _broadcastAccumulator = 0f;
                PublishEnvironmentUpdate();
            }
        }

        /// <summary>Sets the in-game clock to an exact hour (0..24) immediately.</summary>
        public void SetTimeOfDayHours(float hour24)
        {
            normalizedTimeOfDay = Mathf.Repeat(hour24 / 24f, 1f);
            ApplyLightingImmediate();
            PublishEnvironmentUpdate();
        }

        /// <summary>Called by <see cref="DynamicWeatherSystem"/> to dim sunlight during storms.</summary>
        public void SetWeatherDarkeningMultiplier(float multiplier01)
        {
            _weatherDarkeningFactor = Mathf.Clamp(multiplier01, 0.2f, 1.0f);
        }

        // ====================================================================
        // LIGHTING & NEON EVALUATION
        // ====================================================================

        private void ApplyLightingImmediate()
        {
            float sunAngle = (normalizedTimeOfDay * 360f) - 90f;
            float sunElevationFactor = Mathf.Clamp01(Mathf.Sin(normalizedTimeOfDay * Mathf.PI * 2f - Mathf.PI * 0.5f));
            float moonElevationFactor = Mathf.Clamp01(-Mathf.Sin(normalizedTimeOfDay * Mathf.PI * 2f - Mathf.PI * 0.5f));

            if (sunLight != null)
            {
                sunLight.transform.rotation = Quaternion.Euler(sunAngle, sunAzimuthOffsetDegrees, 0f);
                sunLight.color = sunColorGradient.Evaluate(normalizedTimeOfDay);
                sunLight.intensity = maxSunIntensity * Mathf.Pow(sunElevationFactor, 0.65f) * _weatherDarkeningFactor;
                sunLight.enabled = sunLight.intensity > 0.01f;
            }

            if (moonLight != null)
            {
                moonLight.transform.rotation = Quaternion.Euler(sunAngle + 180f, sunAzimuthOffsetDegrees, 0f);
                moonLight.intensity = maxMoonIntensity * Mathf.Pow(moonElevationFactor, 0.7f) * _weatherDarkeningFactor;
                moonLight.enabled = moonLight.intensity > 0.01f;
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ambientSkyGradient.Evaluate(normalizedTimeOfDay) * _weatherDarkeningFactor;
            RenderSettings.ambientEquatorColor = ambientEquatorGradient.Evaluate(normalizedTimeOfDay) * _weatherDarkeningFactor;
            RenderSettings.ambientGroundColor = Color.Lerp(new Color(0.06f, 0.05f, 0.09f), new Color(0.26f, 0.24f, 0.22f), sunElevationFactor);
            RenderSettings.fogColor = fogColorGradient.Evaluate(normalizedTimeOfDay);

            // Calculate smooth night factor for shaders (0 = full day, 1 = full night)
            float nightFactor = 1f - Mathf.SmoothStep(0.0f, 0.25f, sunElevationFactor);
            Shader.SetGlobalFloat(GlobalNightFactorId, nightFactor);

            bool shouldBeNight = normalizedTimeOfDay >= duskStartNormalized || normalizedTimeOfDay <= dawnEndNormalized;
            if (shouldBeNight != _isNightActive)
            {
                _isNightActive = shouldBeNight;
                ToggleNightCityLights(_isNightActive);
            }
        }

        private void ToggleNightCityLights(bool enableLights)
        {
            if (streetLampLights != null)
            {
                for (int i = 0; i < streetLampLights.Length; i++)
                {
                    if (streetLampLights[i] != null)
                    {
                        streetLampLights[i].enabled = enableLights;
                    }
                }
            }

            if (neonEmissiveRenderers != null)
            {
                for (int i = 0; i < neonEmissiveRenderers.Length; i++)
                {
                    Renderer r = neonEmissiveRenderers[i];
                    if (r == null)
                    {
                        continue;
                    }

                    foreach (Material mat in r.materials)
                    {
                        if (mat == null)
                        {
                            continue;
                        }

                        if (enableLights)
                        {
                            mat.EnableKeyword("_EMISSION");
                            if (mat.HasProperty("_EmissionColor"))
                            {
                                Color c = mat.GetColor("_EmissionColor");
                                mat.SetColor("_EmissionColor", c * nightNeonEmissionMultiplier);
                            }
                        }
                        else
                        {
                            mat.DisableKeyword("_EMISSION");
                        }
                    }
                }
            }
        }

        private void PublishEnvironmentUpdate()
        {
            string weatherId = "ClearSunny";
            float rainIntensity = 0f;

            if (EventBus.TryGetSticky(out WorldEnvironmentChangedEvent prev))
            {
                if (!string.IsNullOrEmpty(prev.WeatherId))
                {
                    weatherId = prev.WeatherId;
                }
                rainIntensity = prev.RainIntensity01;
            }

            EventBus.Publish(
                new WorldEnvironmentChangedEvent(normalizedTimeOfDay, currentDayCount, weatherId, rainIntensity),
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

            saveData.World.NormalizedTimeOfDay = normalizedTimeOfDay;
            saveData.World.DayCount = currentDayCount;
        }

        public void RestoreState(GameSaveData saveData)
        {
            if (saveData?.World == null)
            {
                return;
            }

            normalizedTimeOfDay = Mathf.Repeat(saveData.World.NormalizedTimeOfDay, 1f);
            currentDayCount = Mathf.Max(1, saveData.World.DayCount);
            ApplyLightingImmediate();
            PublishEnvironmentUpdate();
        }

        // ====================================================================
        // DEFAULT VICE CITY TROPICAL PALETTE
        // ====================================================================

        private void InitializeDefaultViceGradients()
        {
            sunColorGradient = new Gradient();
            sunColorGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.10f, 0.14f, 0.28f), 0.00f), // Midnight
                    new GradientColorKey(new Color(1.00f, 0.52f, 0.42f), 0.26f), // Coral Sunrise
                    new GradientColorKey(new Color(1.00f, 0.96f, 0.88f), 0.50f), // Bright Miami Noon
                    new GradientColorKey(new Color(1.00f, 0.38f, 0.52f), 0.76f), // Iconic Vice Sunset Pink/Orange
                    new GradientColorKey(new Color(0.10f, 0.14f, 0.28f), 1.00f)  // Midnight
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
            );

            ambientSkyGradient = new Gradient();
            ambientSkyGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.06f, 0.05f, 0.14f), 0.00f),
                    new GradientColorKey(new Color(0.88f, 0.54f, 0.62f), 0.26f),
                    new GradientColorKey(new Color(0.38f, 0.72f, 0.96f), 0.50f),
                    new GradientColorKey(new Color(0.76f, 0.30f, 0.56f), 0.76f),
                    new GradientColorKey(new Color(0.06f, 0.05f, 0.14f), 1.00f)
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
            );

            ambientEquatorGradient = new Gradient();
            ambientEquatorGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.14f, 0.08f, 0.22f), 0.00f),
                    new GradientColorKey(new Color(1.00f, 0.68f, 0.52f), 0.26f),
                    new GradientColorKey(new Color(0.72f, 0.86f, 0.94f), 0.50f),
                    new GradientColorKey(new Color(0.98f, 0.48f, 0.38f), 0.76f),
                    new GradientColorKey(new Color(0.14f, 0.08f, 0.22f), 1.00f)
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
            );

            fogColorGradient = new Gradient();
            fogColorGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.07f, 0.06f, 0.14f), 0.00f),
                    new GradientColorKey(new Color(0.85f, 0.60f, 0.58f), 0.26f),
                    new GradientColorKey(new Color(0.66f, 0.84f, 0.94f), 0.50f),
                    new GradientColorKey(new Color(0.72f, 0.34f, 0.48f), 0.76f),
                    new GradientColorKey(new Color(0.07f, 0.06f, 0.14f), 1.00f)
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
            );
        }
    }
}
