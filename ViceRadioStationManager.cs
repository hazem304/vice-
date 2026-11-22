// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Expansion — In-Vehicle Vice Radio Station System
// File:    _Project/Scripts/Vehicles/ViceRadioStationManager.cs
// Author:  Lead Game Architect
// Purpose: GTA-style continuous in-car radio station system:
//          - Simulates live broadcast timelines across multiple coastal stations
//            (Vice Synthwave FM, Ocean Beach Lounge, Midnight Hip-Hop, Talk FM)
//          - Automatically starts when player enters a vehicle and stops on exit
//          - Allows seamless station cycling (Q / Mouse Wheel / Radio Wheel)
//            with subtle radio static transition burst & EventBus HUD broadcast.
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using ViceCity.Core;
using ViceCity.Core.Events;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ViceCity.Vehicles
{
    [Serializable]
    public sealed class RadioStationProfile
    {
        public string StationId = "vice_synth_fm";
        public string DisplayName = "VICE SYNTHWAVE 86.5 FM";
        public string GenreTag = "80s Neon Synth";
        public AudioClip[] Tracks;
    }

    public readonly struct RadioStationChangedEvent : IGameEvent
    {
        public readonly string StationName;
        public readonly string GenreTag;
        public readonly bool IsRadioOff;

        public RadioStationChangedEvent(string stationName, string genreTag, bool isRadioOff)
        {
            StationName = stationName;
            GenreTag = genreTag;
            IsRadioOff = isRadioOff;
        }
    }

    /// <summary>
    /// Plays continuous Vice Coast radio stations while driving and broadcasts station changes to HUD.
    /// </summary>
    public sealed class ViceRadioStationManager : MonoBehaviour
    {
        [Header("Audio Sources")]
        [SerializeField] private AudioSource musicAudioSource;
        [SerializeField] private AudioSource staticBurstSource;
        [SerializeField] private AudioClip tuningStaticClip;
        [SerializeField, Range(0f, 1f)] private float radioVolume = 0.65f;

        [Header("Stations")]
        [SerializeField] private List<RadioStationProfile> stations = new List<RadioStationProfile>();

        private EventSubscription _vehicleSub;
        private int _currentStationIndex = 0; // -1 means Radio Off
        private bool _isInVehicle;
        private float _broadcastClockSeconds;

        private void Awake()
        {
            if (musicAudioSource == null)
            {
                musicAudioSource = gameObject.AddComponent<AudioSource>();
            }
            musicAudioSource.loop = false;
            musicAudioSource.volume = radioVolume;
            musicAudioSource.playOnAwake = false;
        }

        private void OnEnable()
        {
            _vehicleSub = EventBus.Subscribe<PlayerVehicleTransitionEvent>(OnVehicleTransition);
        }

        private void OnDisable()
        {
            _vehicleSub.Dispose();
        }

        private void Update()
        {
            _broadcastClockSeconds += Time.unscaledDeltaTime;

            if (!_isInVehicle) return;
            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay) return;

            if (WasNextStationPressed())
            {
                CycleStation(1);
            }
            else if (WasPreviousStationPressed())
            {
                CycleStation(-1);
            }

            if (_currentStationIndex >= 0 && musicAudioSource != null && !musicAudioSource.isPlaying)
            {
                PlayCurrentStationAtLiveTimestamp();
            }
        }

        public void CycleStation(int direction)
        {
            int totalSlots = stations.Count + 1; // +1 for Radio Off (-1)
            int shifted = _currentStationIndex + 1;
            shifted = (shifted + direction + totalSlots) % totalSlots;
            _currentStationIndex = shifted - 1;

            PlayTuningStatic();

            if (_currentStationIndex < 0)
            {
                musicAudioSource.Stop();
                EventBus.Publish(new RadioStationChangedEvent("RADIO OFF", string.Empty, isRadioOff: true), sticky: true);
            }
            else
            {
                PlayCurrentStationAtLiveTimestamp();
                RadioStationProfile profile = stations[_currentStationIndex];
                EventBus.Publish(new RadioStationChangedEvent(profile.DisplayName, profile.GenreTag, isRadioOff: false), sticky: true);
            }
        }

        private void PlayCurrentStationAtLiveTimestamp()
        {
            if (_currentStationIndex < 0 || _currentStationIndex >= stations.Count) return;
            RadioStationProfile station = stations[_currentStationIndex];
            if (station.Tracks == null || station.Tracks.Length == 0) return;

            // Calculate live continuous broadcast offset so stations feel alive when switching
            float totalLength = 0f;
            for (int i = 0; i < station.Tracks.Length; i++)
            {
                if (station.Tracks[i] != null) totalLength += station.Tracks[i].length;
            }
            if (totalLength <= 0.1f) return;

            float liveOffset = _broadcastClockSeconds % totalLength;
            float cursor = 0f;

            for (int i = 0; i < station.Tracks.Length; i++)
            {
                AudioClip clip = station.Tracks[i];
                if (clip == null) continue;
                if (liveOffset < cursor + clip.length)
                {
                    musicAudioSource.clip = clip;
                    musicAudioSource.time = Mathf.Clamp(liveOffset - cursor, 0f, Mathf.Max(0f, clip.length - 0.1f));
                    musicAudioSource.volume = radioVolume;
                    musicAudioSource.Play();
                    return;
                }
                cursor += clip.length;
            }
        }

        private void PlayTuningStatic()
        {
            if (staticBurstSource != null && tuningStaticClip != null)
            {
                staticBurstSource.PlayOneShot(tuningStaticClip, 0.45f);
            }
        }

        private void OnVehicleTransition(PlayerVehicleTransitionEvent evt)
        {
            _isInVehicle = evt.EnteredVehicle;
            if (_isInVehicle)
            {
                if (_currentStationIndex >= 0 && _currentStationIndex < stations.Count)
                {
                    PlayCurrentStationAtLiveTimestamp();
                    RadioStationProfile p = stations[_currentStationIndex];
                    EventBus.Publish(new RadioStationChangedEvent(p.DisplayName, p.GenreTag, isRadioOff: false), sticky: true);
                }
            }
            else
            {
                musicAudioSource?.Stop();
            }
        }

        private static bool WasNextStationPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.xKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.X);
#endif
        }

        private static bool WasPreviousStationPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.zKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Z);
#endif
        }
    }
}
