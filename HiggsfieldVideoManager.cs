// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 7 — Cinematics (Higgsfield AI) & Visual Polish
// File:    _Project/Scripts/Cinematics/HiggsfieldVideoManager.cs
// Author:  Lead Game Architect
// Purpose: Integrates Higgsfield AI generated MP4 cinematics and promotional
//          videos into Unity via VideoPlayer:
//          - Full-screen Intro & Mission Cutscenes with clean fade transitions,
//            skip input (Space/Esc), and GameManager Cutscene state sync
//          - Distance-culled looping City Billboards & Hotel TV screens using
//            dedicated RenderTextures to preserve 60 FPS open-world performance.
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using ViceCity.Core;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ViceCity.Cinematics
{
    [Serializable]
    public sealed class BillboardScreenBinding
    {
        public string ScreenId = "OceanDrive_Billboard_01";
        public Renderer TargetScreenRenderer;
        public VideoClip PromoClip;
        public string EmissionTextureProperty = "_EmissionMap";
        public string BaseTextureProperty = "_BaseMap";
        [Min(10f)] public float ActivePlaybackRadiusMeters = 85f;
        public Vector2Int RenderTextureResolution = new Vector2Int(640, 360);

        [NonSerialized] public VideoPlayer RuntimePlayer;
        [NonSerialized] public RenderTexture RuntimeTexture;
    }

    /// <summary>
    /// Orchestrates full-screen Higgsfield AI cutscenes and live in-world billboard/TV video screens.
    /// </summary>
    public sealed class HiggsfieldVideoManager : MonoBehaviour, IGameService
    {
        [Header("Full-Screen Cutscene Player")]
        [SerializeField] private VideoPlayer cutsceneVideoPlayer;
        [SerializeField] private RawImage cutsceneDisplayImage;
        [SerializeField] private CanvasGroup cutsceneCanvasGroup;
        [SerializeField] private VideoClip introCutsceneClip;
        [SerializeField] private bool playIntroOnStart = false;
        [SerializeField] private bool allowSkipCutscene = true;
        [SerializeField] private float fadeDurationSeconds = 0.6f;

        [Header("In-World City Billboards & Hotel TVs")]
        [SerializeField] private Transform playerTransform;
        [SerializeField] private List<BillboardScreenBinding> billboardScreens = new List<BillboardScreenBinding>();
        [SerializeField] private float billboardCullCheckInterval = 0.75f;

        private RenderTexture _cutsceneRenderTexture;
        private Action _onCutsceneFinishedCallback;
        private bool _isPlayingCutscene;
        private float _nextBillboardCheckTime;

        public bool IsPlayingCutscene => _isPlayingCutscene;

        private void Awake()
        {
            InitializeCutscenePlayer();
            InitializeBillboardScreens();

            if (GameManager.HasInstance)
            {
                GameManager.Instance.RegisterService(this);
            }
        }

        private void Start()
        {
            if (playerTransform == null)
            {
                GameObject p = GameObject.FindGameObjectWithTag("Player");
                if (p != null) playerTransform = p.transform;
            }

            if (playIntroOnStart && introCutsceneClip != null)
            {
                PlayCutscene(introCutsceneClip);
            }
        }

        private void OnDestroy()
        {
            if (GameManager.HasInstance)
            {
                GameManager.Instance.UnregisterService<HiggsfieldVideoManager>();
            }
            CleanupRenderTextures();
        }

        public void Initialize() { }
        public void Shutdown() { CleanupRenderTextures(); }

        private void Update()
        {
            if (_isPlayingCutscene)
            {
                if (allowSkipCutscene && WasSkipPressed())
                {
                    StopCurrentCutscene();
                }
                return;
            }

            if (playerTransform != null && Time.time >= _nextBillboardCheckTime)
            {
                _nextBillboardCheckTime = Time.time + billboardCullCheckInterval;
                UpdateBillboardProximityPlayback();
            }
        }

        // ====================================================================
        // FULL-SCREEN CUTSCENE PLAYBACK (HIGGSFIELD MP4)
        // ====================================================================

        /// <summary>Plays a full-screen Higgsfield cinematic clip and transitions GameManager cleanly.</summary>
        public void PlayCutscene(VideoClip clip, Action onCompleted = null)
        {
            if (clip == null || cutsceneVideoPlayer == null)
            {
                Debug.LogWarning("[HiggsfieldVideoManager] Cannot play cutscene: clip or VideoPlayer is missing.");
                onCompleted?.Invoke();
                return;
            }

            if (_isPlayingCutscene)
            {
                StopCurrentCutscene();
            }

            _onCutsceneFinishedCallback = onCompleted;
            StartCoroutine(PlayCutsceneRoutine(clip));
        }

        public void StopCurrentCutscene()
        {
            if (!_isPlayingCutscene) return;

            if (cutsceneVideoPlayer != null && cutsceneVideoPlayer.isPlaying)
            {
                cutsceneVideoPlayer.Stop();
            }

            StartCoroutine(FinalizeCutsceneRoutine());
        }

        private IEnumerator PlayCutsceneRoutine(VideoClip clip)
        {
            _isPlayingCutscene = true;

            if (GameManager.HasInstance)
            {
                GameManager.Instance.TransitionToState(GameState.Cutscene);
            }

            if (cutsceneCanvasGroup != null)
            {
                cutsceneCanvasGroup.gameObject.SetActive(true);
                cutsceneCanvasGroup.alpha = 0f;
            }

            cutsceneVideoPlayer.clip = clip;
            cutsceneVideoPlayer.Prepare();

            float timeout = 5f;
            while (!cutsceneVideoPlayer.isPrepared && timeout > 0f)
            {
                timeout -= Time.unscaledDeltaTime;
                yield return null;
            }

            if (!cutsceneVideoPlayer.isPrepared)
            {
                Debug.LogError($"[HiggsfieldVideoManager] Timed out preparing cutscene '{clip.name}'.");
                yield return FinalizeCutsceneRoutine();
                yield break;
            }

            cutsceneVideoPlayer.Play();
            yield return FadeCanvasGroup(cutsceneCanvasGroup, 0f, 1f, fadeDurationSeconds);
        }

        private void OnCutsceneLoopPointReached(VideoPlayer source)
        {
            if (_isPlayingCutscene)
            {
                StartCoroutine(FinalizeCutsceneRoutine());
            }
        }

        private IEnumerator FinalizeCutsceneRoutine()
        {
            if (!_isPlayingCutscene) yield break;
            _isPlayingCutscene = false;

            yield return FadeCanvasGroup(cutsceneCanvasGroup, 1f, 0f, fadeDurationSeconds * 0.8f);

            if (cutsceneCanvasGroup != null)
            {
                cutsceneCanvasGroup.gameObject.SetActive(false);
            }

            if (GameManager.HasInstance)
            {
                GameManager.Instance.TransitionToState(GameState.Gameplay);
            }

            Action callback = _onCutsceneFinishedCallback;
            _onCutsceneFinishedCallback = null;
            callback?.Invoke();
        }

        // ====================================================================
        // IN-WORLD CITY BILLBOARDS & HOTEL TV SCREENS (DISTANCE-CULLED)
        // ====================================================================

        private void InitializeCutscenePlayer()
        {
            if (cutsceneVideoPlayer == null)
            {
                cutsceneVideoPlayer = gameObject.AddComponent<VideoPlayer>();
            }

            _cutsceneRenderTexture = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
            cutsceneVideoPlayer.playOnAwake = false;
            cutsceneVideoPlayer.isLooping = false;
            cutsceneVideoPlayer.renderMode = VideoRenderMode.RenderTexture;
            cutsceneVideoPlayer.targetTexture = _cutsceneRenderTexture;
            cutsceneVideoPlayer.loopPointReached += OnCutsceneLoopPointReached;

            if (cutsceneDisplayImage != null)
            {
                cutsceneDisplayImage.texture = _cutsceneRenderTexture;
            }

            if (cutsceneCanvasGroup != null)
            {
                cutsceneCanvasGroup.alpha = 0f;
                cutsceneCanvasGroup.gameObject.SetActive(false);
            }
        }

        private void InitializeBillboardScreens()
        {
            for (int i = 0; i < billboardScreens.Count; i++)
            {
                BillboardScreenBinding screen = billboardScreens[i];
                if (screen == null || screen.TargetScreenRenderer == null || screen.PromoClip == null)
                {
                    continue;
                }

                RenderTexture rt = new RenderTexture(
                    Mathf.Max(128, screen.RenderTextureResolution.x),
                    Mathf.Max(128, screen.RenderTextureResolution.y),
                    0,
                    RenderTextureFormat.ARGB32);

                VideoPlayer vp = screen.TargetScreenRenderer.gameObject.AddComponent<VideoPlayer>();
                vp.playOnAwake = false;
                vp.isLooping = true;
                vp.audioOutputMode = VideoAudioOutputMode.None; // Silent ambient billboards for 60 FPS & clean mix
                vp.renderMode = VideoRenderMode.RenderTexture;
                vp.targetTexture = rt;
                vp.clip = screen.PromoClip;

                Material mat = screen.TargetScreenRenderer.material;
                if (mat != null)
                {
                    if (mat.HasProperty(screen.BaseTextureProperty)) mat.SetTexture(screen.BaseTextureProperty, rt);
                    if (mat.HasProperty(screen.EmissionTextureProperty))
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetTexture(screen.EmissionTextureProperty, rt);
                    }
                }

                screen.RuntimePlayer = vp;
                screen.RuntimeTexture = rt;
            }
        }

        private void UpdateBillboardProximityPlayback()
        {
            Vector3 playerPos = playerTransform.position;

            for (int i = 0; i < billboardScreens.Count; i++)
            {
                BillboardScreenBinding screen = billboardScreens[i];
                if (screen?.RuntimePlayer == null || screen.TargetScreenRenderer == null) continue;

                float distSqr = (screen.TargetScreenRenderer.transform.position - playerPos).sqrMagnitude;
                float maxDistSqr = screen.ActivePlaybackRadiusMeters * screen.ActivePlaybackRadiusMeters;

                if (distSqr <= maxDistSqr)
                {
                    if (!screen.RuntimePlayer.isPlaying) screen.RuntimePlayer.Play();
                }
                else
                {
                    if (screen.RuntimePlayer.isPlaying) screen.RuntimePlayer.Pause();
                }
            }
        }

        private void CleanupRenderTextures()
        {
            if (cutsceneVideoPlayer != null)
            {
                cutsceneVideoPlayer.loopPointReached -= OnCutsceneLoopPointReached;
            }

            if (_cutsceneRenderTexture != null)
            {
                _cutsceneRenderTexture.Release();
                Destroy(_cutsceneRenderTexture);
                _cutsceneRenderTexture = null;
            }

            for (int i = 0; i < billboardScreens.Count; i++)
            {
                if (billboardScreens[i]?.RuntimeTexture != null)
                {
                    billboardScreens[i].RuntimeTexture.Release();
                    Destroy(billboardScreens[i].RuntimeTexture);
                }
            }
        }

        private static IEnumerator FadeCanvasGroup(CanvasGroup group, float from, float to, float duration)
        {
            if (group == null || duration <= 0f) yield break;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                group.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
                yield return null;
            }
            group.alpha = to;
        }

        private static bool WasSkipPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null &&
                   (Keyboard.current.spaceKey.wasPressedThisFrame || Keyboard.current.escapeKey.wasPressedThisFrame);
#else
            return Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape);
#endif
        }
    }
}
