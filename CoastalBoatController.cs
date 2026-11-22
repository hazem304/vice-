// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Expansion — Coastal Watercraft & Ocean Physics
// File:    _Project/Scripts/Vehicles/CoastalBoatController.cs
// Author:  Lead Game Architect
// Purpose: Miami / Vice City speedboat physics with:
//          - Multi-point Archimedes buoyancy across dynamic ocean waves
//          - Planing hull bow-lift, rudder steering, and lateral water drag
//          - Engine wake foam VFX & dynamic throttle pitch audio
//          - Seamless Player boarding/exiting (F key) + EventBus transitions.
// ============================================================================

using UnityEngine;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Player;
using ViceCity.World;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ViceCity.Vehicles
{
    /// <summary>
    /// High-speed coastal powerboat controller with multi-point buoyancy and wake spray VFX.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class CoastalBoatController : MonoBehaviour, IDamageable
    {
        [Header("Buoyancy & Ocean Waves")]
        [SerializeField] private Transform[] buoyancyFloatPoints;
        [SerializeField] private float baseWaterLevelY = 0f;
        [SerializeField] private float waveAmplitude = 0.28f;
        [SerializeField] private float waveFrequency = 0.14f;
        [SerializeField] private float waveSpeed = 1.4f;
        [SerializeField] private float buoyancyForcePerPoint = 4800f;
        [SerializeField] private float waterLinearDrag = 0.85f;
        [SerializeField] private float waterAngularDrag = 2.2f;

        [Header("Propulsion & Rudder")]
        [SerializeField] private Transform propellerPoint;
        [SerializeField] private float forwardThrustNewtons = 16500f;
        [SerializeField] private float reverseThrustNewtons = 6500f;
        [SerializeField] private float rudderTurnTorque = 6200f;
        [SerializeField] private float maxSpeedKnots = 52f;
        [SerializeField] private float lateralKeelGrip = 3.8f;

        [Header("Boarding & Effects")]
        [SerializeField] private Transform helmSeatPoint;
        [SerializeField] private Transform dismountPoint;
        [SerializeField] private float interactRadius = 4.5f;
        [SerializeField] private ParticleSystem wakeSprayVfx;
        [SerializeField] private AudioSource boatEngineAudio;
        [SerializeField] private float maxHullHealth = 800f;

        private Rigidbody _rb;
        private ThirdPersonController _pilotPlayer;
        private Transform _originalPlayerParent;

        private float _throttle;
        private float _steer;
        private float _hullHealth;
        private bool _isDestroyed;

        public bool IsPlayerPiloting => _pilotPlayer != null;
        public float SpeedKnots => _rb != null ? _rb.linearVelocity.magnitude * 1.94384f : 0f;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _hullHealth = maxHullHealth;
        }

        private void Update()
        {
            if (_isDestroyed) return;
            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay)
            {
                _throttle = 0f;
                _steer = 0f;
                return;
            }

            if (WasInteractPressed())
            {
                if (IsPlayerPiloting)
                {
                    ExitBoat();
                }
                else
                {
                    TryBoardNearbyPlayer();
                }
            }

            if (IsPlayerPiloting)
            {
                ReadBoatInput();
            }
            else
            {
                _throttle = 0f;
                _steer = 0f;
            }

            UpdateAudioAndWake();
        }

        private void FixedUpdate()
        {
            ApplyMultiPointBuoyancy();
            ApplyWaterDragAndKeelStabilization();

            if (!_isDestroyed && IsPlayerPiloting)
            {
                ApplyPropulsionAndSteering();
            }
        }

        // ====================================================================
        // OCEAN BUOYANCY & PROPULSION
        // ====================================================================

        public float SampleOceanHeight(Vector3 worldPos)
        {
            float t = Time.time * waveSpeed;
            float w1 = Mathf.Sin(worldPos.x * waveFrequency + t);
            float w2 = Mathf.Cos(worldPos.z * (waveFrequency * 0.85f) + t * 0.8f);
            return baseWaterLevelY + (w1 + w2) * 0.5f * waveAmplitude;
        }

        private void ApplyMultiPointBuoyancy()
        {
            if (buoyancyFloatPoints == null || buoyancyFloatPoints.Length == 0)
            {
                ApplySinglePointBuoyancy(transform.position, buoyancyForcePerPoint * 4f);
                return;
            }

            for (int i = 0; i < buoyancyFloatPoints.Length; i++)
            {
                if (buoyancyFloatPoints[i] != null)
                {
                    ApplySinglePointBuoyancy(buoyancyFloatPoints[i].position, buoyancyForcePerPoint);
                }
            }
        }

        private void ApplySinglePointBuoyancy(Vector3 pointPos, float forceStrength)
        {
            float waterY = SampleOceanHeight(pointPos);
            float submersionDepth = waterY - pointPos.y;
            if (submersionDepth > 0f)
            {
                float factor = Mathf.Clamp01(submersionDepth / 1.15f);
                _rb.AddForceAtPosition(Vector3.up * (factor * forceStrength), pointPos, ForceMode.Force);
            }
        }

        private void ApplyWaterDragAndKeelStabilization()
        {
            bool inWater = transform.position.y < SampleOceanHeight(transform.position) + 0.6f;
            _rb.linearDamping = inWater ? waterLinearDrag : 0.08f;
            _rb.angularDamping = inWater ? waterAngularDrag : 0.2f;

            if (!inWater) return;

            // Cancel excessive sideways sliding (keel hydrodynamic resistance)
            Vector3 localVel = transform.InverseTransformDirection(_rb.linearVelocity);
            Vector3 lateralCounter = -transform.right * (localVel.x * lateralKeelGrip);
            _rb.AddForce(lateralCounter, ForceMode.Acceleration);
        }

        private void ApplyPropulsionAndSteering()
        {
            Vector3 propPos = propellerPoint != null ? propellerPoint.position : (transform.position - transform.forward * 2.2f);
            if (propPos.y > SampleOceanHeight(propPos) + 0.45f)
            {
                return; // Propeller out of water during wave jump
            }

            if (Mathf.Abs(_throttle) > 0.05f && SpeedKnots < maxSpeedKnots)
            {
                float thrust = _throttle > 0f ? forwardThrustNewtons : reverseThrustNewtons;
                _rb.AddForceAtPosition(transform.forward * (_throttle * thrust), propPos, ForceMode.Force);
            }

            if (Mathf.Abs(_steer) > 0.05f)
            {
                float speedSteerAuthority = Mathf.Clamp01(0.35f + (SpeedKnots / 18f));
                _rb.AddTorque(Vector3.up * (_steer * rudderTurnTorque * speedSteerAuthority), ForceMode.Force);
                // Natural inward banking tilt when carving ocean turns
                _rb.AddTorque(transform.forward * (-_steer * rudderTurnTorque * 0.25f * speedSteerAuthority), ForceMode.Force);
            }
        }

        // ====================================================================
        // BOARDING / EXITING & DAMAGE
        // ====================================================================

        private void TryBoardNearbyPlayer()
        {
            GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
            if (playerObj == null) return;

            if (Vector3.Distance(playerObj.transform.position, transform.position) > interactRadius) return;

            ThirdPersonController tpc = playerObj.GetComponent<ThirdPersonController>();
            if (tpc == null || tpc.State == PlayerLocomotionState.InVehicle || tpc.State == PlayerLocomotionState.Dead) return;

            _pilotPlayer = tpc;
            _originalPlayerParent = tpc.transform.parent;
            tpc.SetExternalControlLock(true, PlayerLocomotionState.InVehicle);
            tpc.Controller.enabled = false;

            Transform seat = helmSeatPoint != null ? helmSeatPoint : transform;
            tpc.transform.SetParent(seat, worldPositionStays: false);
            tpc.transform.localPosition = Vector3.zero;
            tpc.transform.localRotation = Quaternion.identity;

            if (GameManager.HasInstance && GameManager.Instance.TryGetService(out ChunkStreamingManager streamer))
            {
                streamer.SetStreamingTarget(transform);
            }

            EventBus.Publish(new PlayerVehicleTransitionEvent(gameObject, enteredVehicle: true));
        }

        private void ExitBoat()
        {
            if (_pilotPlayer == null) return;

            ThirdPersonController tpc = _pilotPlayer;
            _pilotPlayer = null;
            _throttle = 0f;
            _steer = 0f;

            tpc.transform.SetParent(_originalPlayerParent, worldPositionStays: true);
            Vector3 exitPos = dismountPoint != null ? dismountPoint.position : (transform.position + transform.right * 2.4f + Vector3.up * 0.6f);
            tpc.Teleport(exitPos, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
            tpc.SetExternalControlLock(false, PlayerLocomotionState.Grounded);

            if (GameManager.HasInstance && GameManager.Instance.TryGetService(out ChunkStreamingManager streamer))
            {
                streamer.SetStreamingTarget(tpc.transform);
            }

            EventBus.Publish(new PlayerVehicleTransitionEvent(gameObject, enteredVehicle: false));
        }

        public void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitNormal, GameObject instigator)
        {
            if (_isDestroyed || amount <= 0f) return;
            _hullHealth = Mathf.Max(0f, _hullHealth - amount);
            if (_hullHealth <= 0f)
            {
                _isDestroyed = true;
                if (IsPlayerPiloting) ExitBoat();
            }
        }

        private void UpdateAudioAndWake()
        {
            if (boatEngineAudio != null)
            {
                float ratio = Mathf.Clamp01(SpeedKnots / maxSpeedKnots);
                boatEngineAudio.pitch = Mathf.Lerp(boatEngineAudio.pitch, Mathf.Lerp(0.75f, 2.0f, ratio), Time.deltaTime * 6f);
            }

            if (wakeSprayVfx != null)
            {
                bool activeWake = SpeedKnots > 6f;
                if (activeWake && !wakeSprayVfx.isPlaying) wakeSprayVfx.Play();
                else if (!activeWake && wakeSprayVfx.isPlaying) wakeSprayVfx.Stop();
            }
        }

        private void ReadBoatInput()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            _throttle = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
            _steer = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
#else
            _throttle = Input.GetAxis("Vertical");
            _steer = Input.GetAxis("Horizontal");
#endif
        }

        private static bool WasInteractPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.F);
#endif
        }
    }
}
