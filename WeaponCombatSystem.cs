// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 3 — Character Controller & Combat System
// File:    _Project/Scripts/Player/WeaponCombatSystem.cs
// Author:  Lead Game Architect
// Purpose: Complete third-person combat manager featuring:
//          - GTA-style Weapon Wheel with 0.25x slow-motion time dilation
//          - Smooth ADS (Aim Down Sights) FOV zoom & shoulder stance
//          - Hitscan ballistics (including multi-pellet shotguns) & procedural recoil
//          - Clip/Reserve ammo management, reloading, shop weapon unlocking
//          - EventBus broadcasting & SaveLoadSystem (ISaveable) inventory persistence.
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Core.SaveSystem;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ViceCity.Player
{
    /// <summary>
    /// Manages the player's equipped weapons, Weapon Wheel slow-motion selector,
    /// ADS aiming, firing, procedural recoil, reloading, and ammo persistence.
    /// </summary>
    [RequireComponent(typeof(ThirdPersonController))]
    public sealed class WeaponCombatSystem : MonoBehaviour, ISaveable
    {
        private sealed class RuntimeWeaponSlot
        {
            public WeaponDataSO Data;
            public int ClipAmmo;
            public int ReserveAmmo;
            public bool IsUnlocked;
        }

        [Header("Core References")]
        [SerializeField] private ThirdPersonController playerController;
        [SerializeField] private Camera mainCamera;
        [SerializeField] private Transform weaponHoldSocket;
        [SerializeField] private AudioSource weaponAudioSource;
        [SerializeField] private LayerMask hitLayers = ~0;

        [Header("Loadout Catalog")]
        [SerializeField] private List<WeaponDataSO> weaponCatalog = new List<WeaponDataSO>();
        [SerializeField] private int startingUnlockedIndex = 0;

        [Header("Weapon Wheel & ADS Tuning")]
        [SerializeField] private GameObject weaponWheelPanel;
        [SerializeField, Range(0.1f, 0.5f)] private float weaponWheelTimeScale = 0.25f;
        [SerializeField] private float defaultCameraFov = 60f;
        [SerializeField] private float fovTransitionSpeed = 12f;
        [SerializeField] private float recoilRecoverySpeed = 9f;

        private readonly List<RuntimeWeaponSlot> _slots = new List<RuntimeWeaponSlot>(8);
        private SaveLoadSystem _saveLoadSystem;
        private GameObject _activeWeaponModel;
        private Coroutine _reloadCoroutine;

        private int _activeSlotIndex = -1;
        private bool _isWheelOpen;
        private bool _isAiming;
        private bool _isReloading;
        private float _nextFireTime;
        private Vector2 _currentRecoilOffset;

        public string SaveKey => "Player.Weapons";
        public bool IsWeaponWheelOpen => _isWheelOpen;
        public bool IsReloading => _isReloading;
        public WeaponDataSO EquippedWeapon => IsValidSlot(_activeSlotIndex) ? _slots[_activeSlotIndex].Data : null;

        private void Awake()
        {
            if (playerController == null) playerController = GetComponent<ThirdPersonController>();
            if (mainCamera == null) mainCamera = Camera.main;

            BuildRuntimeSlots();

            if (weaponWheelPanel != null)
            {
                weaponWheelPanel.SetActive(false);
            }
        }

        private void Start()
        {
            if (GameManager.HasInstance && GameManager.Instance.TryGetService(out _saveLoadSystem))
            {
                _saveLoadSystem.RegisterSaveable(this);
            }

            if (IsValidSlot(startingUnlockedIndex))
            {
                _slots[startingUnlockedIndex].IsUnlocked = true;
                EquipSlot(startingUnlockedIndex);
            }
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
            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay)
            {
                CloseWeaponWheelIfOpen();
                return;
            }

            if (playerController.State == PlayerLocomotionState.InVehicle ||
                playerController.State == PlayerLocomotionState.Swimming ||
                playerController.State == PlayerLocomotionState.Dead)
            {
                SetAiming(false);
                CloseWeaponWheelIfOpen();
                return;
            }

            HandleWeaponWheelInput();
            if (_isWheelOpen)
            {
                return;
            }

            HandleCombatInput();
            UpdateCameraFovAndRecoil(Time.deltaTime);
        }

        // ====================================================================
        // WEAPON WHEEL & LOADOUT MANAGEMENT
        // ====================================================================

        public void EquipSlot(int slotIndex)
        {
            if (!IsValidSlot(slotIndex) || !_slots[slotIndex].IsUnlocked)
            {
                return;
            }

            if (_activeSlotIndex == slotIndex)
            {
                return;
            }

            CancelActiveReload();
            _activeSlotIndex = slotIndex;
            SpawnActiveWeaponModel(_slots[slotIndex].Data);
            BroadcastWeaponState();
        }

        /// <summary>Called by Module 6 Weapon Shops to unlock a weapon or purchase extra ammunition.</summary>
        public bool UnlockOrAddAmmo(string weaponId, int extraReserveAmmo)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                RuntimeWeaponSlot slot = _slots[i];
                if (slot.Data != null && string.Equals(slot.Data.WeaponId, weaponId, StringComparison.OrdinalIgnoreCase))
                {
                    slot.IsUnlocked = true;
                    slot.ReserveAmmo = Mathf.Min(slot.Data.MaxReserveAmmo, slot.ReserveAmmo + Mathf.Max(0, extraReserveAmmo));
                    if (_activeSlotIndex == i)
                    {
                        BroadcastWeaponState();
                    }
                    return true;
                }
            }
            return false;
        }

        public void CycleWeapon(int direction)
        {
            if (_slots.Count == 0) return;

            int start = _activeSlotIndex < 0 ? 0 : _activeSlotIndex;
            for (int step = 1; step <= _slots.Count; step++)
            {
                int candidate = (start + (step * direction) + _slots.Count) % _slots.Count;
                if (_slots[candidate].IsUnlocked)
                {
                    EquipSlot(candidate);
                    return;
                }
            }
        }

        private void HandleWeaponWheelInput()
        {
            bool wheelHeld = IsWeaponWheelKeyHeld();
            if (wheelHeld && !_isWheelOpen)
            {
                OpenWeaponWheel();
            }
            else if (!wheelHeld && _isWheelOpen)
            {
                CloseWeaponWheelIfOpen();
            }

            // Number keys 1..6 for instant GTA weapon slot switching
            int directKey = GetPressedWeaponNumberKey();
            if (directKey >= 0 && directKey < _slots.Count && _slots[directKey].IsUnlocked)
            {
                EquipSlot(directKey);
            }
        }

        private void OpenWeaponWheel()
        {
            _isWheelOpen = true;
            SetAiming(false);
            if (weaponWheelPanel != null) weaponWheelPanel.SetActive(true);
            if (GameManager.HasInstance) GameManager.Instance.SetGameplayTimeScale(weaponWheelTimeScale);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void CloseWeaponWheelIfOpen()
        {
            if (!_isWheelOpen) return;

            _isWheelOpen = false;
            if (weaponWheelPanel != null) weaponWheelPanel.SetActive(false);
            if (GameManager.HasInstance) GameManager.Instance.SetGameplayTimeScale(1f);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // ====================================================================
        // ADS AIMING, FIRING, RECOIL & RELOADING
        // ====================================================================

        private void HandleCombatInput()
        {
            if (!IsValidSlot(_activeSlotIndex)) return;
            RuntimeWeaponSlot slot = _slots[_activeSlotIndex];
            WeaponDataSO data = slot.Data;

            SetAiming(IsAimInputHeld() && data.Category != WeaponCategory.Unarmed);

            if (WasReloadKeyPressed() && !_isReloading && slot.ClipAmmo < data.ClipCapacity && slot.ReserveAmmo > 0)
            {
                _reloadCoroutine = StartCoroutine(ReloadRoutine(slot));
                return;
            }

            float scroll = GetMouseScrollDelta();
            if (Mathf.Abs(scroll) > 0.1f)
            {
                CycleWeapon(scroll > 0f ? 1 : -1);
                return;
            }

            if (_isReloading || data.Category == WeaponCategory.Unarmed)
            {
                return;
            }

            bool wantsFire = data.IsAutomatic ? IsFireInputHeld() : WasFireInputPressed();
            if (wantsFire && Time.time >= _nextFireTime)
            {
                TryFireWeapon(slot);
            }
        }

        private void TryFireWeapon(RuntimeWeaponSlot slot)
        {
            WeaponDataSO data = slot.Data;
            _nextFireTime = Time.time + data.FireIntervalSeconds;

            if (slot.ClipAmmo <= 0)
            {
                PlayClip(data.EmptyClickClip, 0.7f);
                if (slot.ReserveAmmo > 0 && !_isReloading)
                {
                    _reloadCoroutine = StartCoroutine(ReloadRoutine(slot));
                }
                return;
            }

            slot.ClipAmmo--;
            PlayClip(data.FireClip, 1f);
            SpawnMuzzleFlash(data);

            Transform aimOriginTransform = mainCamera != null ? mainCamera.transform : transform;
            Vector3 rayOrigin = aimOriginTransform.position;
            float spread = _isAiming ? data.AdsSpreadDegrees : data.HipFireSpreadDegrees;

            for (int p = 0; p < data.PelletsPerShot; p++)
            {
                Vector3 spreadDir = ApplySpread(aimOriginTransform.forward, spread);
                if (Physics.Raycast(rayOrigin, spreadDir, out RaycastHit hit, data.MaxRangeMeters, hitLayers, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.gameObject != gameObject)
                    {
                        IDamageable damageable = hit.collider.GetComponentInParent<IDamageable>();
                        damageable?.TakeDamage(data.BaseDamage, hit.point, hit.normal, gameObject);

                        if (data.ImpactEffectPrefab != null)
                        {
                            GameObject fx = Instantiate(data.ImpactEffectPrefab, hit.point + hit.normal * 0.02f, Quaternion.LookRotation(hit.normal));
                            Destroy(fx, 2.5f);
                        }
                    }
                }
            }

            // Apply procedural recoil impulse
            _currentRecoilOffset.x += data.RecoilPitchKick;
            _currentRecoilOffset.y += UnityEngine.Random.Range(-data.RecoilYawRandom, data.RecoilYawRandom);

            float audibleRadius = data.IsSuppressed ? data.AudibleRadiusMeters * 0.25f : data.AudibleRadiusMeters;
            EventBus.Publish(new WeaponFiredEvent(transform.position + Vector3.up * 1.4f, transform.forward, audibleRadius, data.WeaponId, gameObject));
            BroadcastWeaponState();
        }

        private IEnumerator ReloadRoutine(RuntimeWeaponSlot slot)
        {
            _isReloading = true;
            PlayClip(slot.Data.ReloadClip, 0.9f);
            BroadcastWeaponState();

            yield return new WaitForSeconds(slot.Data.ReloadDurationSeconds);

            int needed = slot.Data.ClipCapacity - slot.ClipAmmo;
            int loaded = Mathf.Min(needed, slot.ReserveAmmo);
            slot.ClipAmmo += loaded;
            slot.ReserveAmmo -= loaded;

            _isReloading = false;
            _reloadCoroutine = null;
            BroadcastWeaponState();
        }

        private void CancelActiveReload()
        {
            if (_reloadCoroutine != null)
            {
                StopCoroutine(_reloadCoroutine);
                _reloadCoroutine = null;
            }
            _isReloading = false;
        }

        private void SetAiming(bool aiming)
        {
            if (_isAiming == aiming) return;
            _isAiming = aiming;
            playerController.SetAimingState(aiming);
        }

        private void UpdateCameraFovAndRecoil(float dt)
        {
            if (mainCamera != null)
            {
                float targetFov = (_isAiming && EquippedWeapon != null) ? EquippedWeapon.AdsFieldOfView : defaultCameraFov;
                mainCamera.fieldOfView = Mathf.Lerp(mainCamera.fieldOfView, targetFov, dt * fovTransitionSpeed);
            }

            _currentRecoilOffset = Vector2.Lerp(_currentRecoilOffset, Vector2.zero, dt * recoilRecoverySpeed);
        }

        private void SpawnActiveWeaponModel(WeaponDataSO data)
        {
            if (_activeWeaponModel != null)
            {
                Destroy(_activeWeaponModel);
                _activeWeaponModel = null;
            }

            if (data != null && data.WeaponModelPrefab != null && weaponHoldSocket != null)
            {
                _activeWeaponModel = Instantiate(data.WeaponModelPrefab, weaponHoldSocket);
                _activeWeaponModel.transform.localPosition = Vector3.zero;
                _activeWeaponModel.transform.localRotation = Quaternion.identity;
            }
        }

        private void SpawnMuzzleFlash(WeaponDataSO data)
        {
            if (data.MuzzleFlashPrefab == null || weaponHoldSocket == null) return;
            GameObject flash = Instantiate(data.MuzzleFlashPrefab, weaponHoldSocket.position + weaponHoldSocket.forward * 0.45f, weaponHoldSocket.rotation);
            Destroy(flash, 0.12f);
        }

        private void PlayClip(AudioClip clip, float volume)
        {
            if (weaponAudioSource != null && clip != null)
            {
                weaponAudioSource.PlayOneShot(clip, volume);
            }
        }

        private static Vector3 ApplySpread(Vector3 forward, float spreadDegrees)
        {
            if (spreadDegrees <= 0.001f) return forward;
            float rx = UnityEngine.Random.Range(-spreadDegrees, spreadDegrees);
            float ry = UnityEngine.Random.Range(-spreadDegrees, spreadDegrees);
            return (Quaternion.Euler(rx, ry, 0f) * forward).normalized;
        }

        private void BuildRuntimeSlots()
        {
            _slots.Clear();
            for (int i = 0; i < weaponCatalog.Count; i++)
            {
                WeaponDataSO data = weaponCatalog[i];
                if (data == null) continue;
                _slots.Add(new RuntimeWeaponSlot
                {
                    Data = data,
                    ClipAmmo = data.ClipCapacity,
                    ReserveAmmo = data.DefaultReserveAmmo,
                    IsUnlocked = (i == startingUnlockedIndex || data.Category == WeaponCategory.Unarmed)
                });
            }
        }

        private void BroadcastWeaponState()
        {
            if (!IsValidSlot(_activeSlotIndex)) return;
            RuntimeWeaponSlot slot = _slots[_activeSlotIndex];
            EventBus.Publish(
                new WeaponStateChangedEvent(
                    slot.Data.WeaponId,
                    slot.Data.DisplayName,
                    slot.ClipAmmo,
                    slot.Data.ClipCapacity,
                    slot.ReserveAmmo,
                    _isReloading),
                sticky: true);
        }

        private bool IsValidSlot(int index) => index >= 0 && index < _slots.Count && _slots[index].Data != null;

        // ====================================================================
        // INPUT HELPERS (New Input System + Legacy Fallback)
        // ====================================================================

        private static bool IsWeaponWheelKeyHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.tabKey.isPressed;
#else
            return Input.GetKey(KeyCode.Tab);
#endif
        }

        private static bool IsAimInputHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
            return Input.GetMouseButton(1);
#endif
        }

        private static bool IsFireInputHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.leftButton.isPressed;
#else
            return Input.GetMouseButton(0);
#endif
        }

        private static bool WasFireInputPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }

        private static bool WasReloadKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.R);
#endif
        }

        private static float GetMouseScrollDelta()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
#else
            return Input.mouseScrollDelta.y;
#endif
        }

        private static int GetPressedWeaponNumberKey()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard kb = Keyboard.current;
            if (kb == null) return -1;
            if (kb.digit1Key.wasPressedThisFrame) return 0;
            if (kb.digit2Key.wasPressedThisFrame) return 1;
            if (kb.digit3Key.wasPressedThisFrame) return 2;
            if (kb.digit4Key.wasPressedThisFrame) return 3;
            if (kb.digit5Key.wasPressedThisFrame) return 4;
            if (kb.digit6Key.wasPressedThisFrame) return 5;
            return -1;
#else
            for (int i = 0; i < 6; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) return i;
            }
            return -1;
#endif
        }

        // ====================================================================
        // ISAVEABLE IMPLEMENTATION
        // ====================================================================

        public void CaptureState(GameSaveData saveData)
        {
            if (saveData?.Player == null) return;
            saveData.Player.EquippedWeaponId = EquippedWeapon != null ? EquippedWeapon.WeaponId : "Unarmed";
            saveData.Player.WeaponInventory.Clear();

            for (int i = 0; i < _slots.Count; i++)
            {
                if (!_slots[i].IsUnlocked || _slots[i].Data == null) continue;
                saveData.Player.WeaponInventory.Add(new WeaponAmmoRecord
                {
                    WeaponId = _slots[i].Data.WeaponId,
                    ClipAmmo = _slots[i].ClipAmmo,
                    ReserveAmmo = _slots[i].ReserveAmmo
                });
            }
        }

        public void RestoreState(GameSaveData saveData)
        {
            if (saveData?.Player?.WeaponInventory == null) return;

            for (int i = 0; i < saveData.Player.WeaponInventory.Count; i++)
            {
                WeaponAmmoRecord rec = saveData.Player.WeaponInventory[i];
                for (int s = 0; s < _slots.Count; s++)
                {
                    if (string.Equals(_slots[s].Data.WeaponId, rec.WeaponId, StringComparison.OrdinalIgnoreCase))
                    {
                        _slots[s].IsUnlocked = true;
                        _slots[s].ClipAmmo = Mathf.Clamp(rec.ClipAmmo, 0, _slots[s].Data.ClipCapacity);
                        _slots[s].ReserveAmmo = Mathf.Clamp(rec.ReserveAmmo, 0, _slots[s].Data.MaxReserveAmmo);
                    }
                }
            }

            for (int s = 0; s < _slots.Count; s++)
            {
                if (string.Equals(_slots[s].Data.WeaponId, saveData.Player.EquippedWeaponId, StringComparison.OrdinalIgnoreCase))
                {
                    EquipSlot(s);
                    break;
                }
            }
        }
    }
}
