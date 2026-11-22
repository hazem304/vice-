// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 4 — Advanced Vehicle Physics & Traffic AI
// File:    _Project/Scripts/Vehicles/ArcadeRealisticCarController.cs
// Author:  Lead Game Architect
// Purpose: GTA-style coastal sports car physics built on Unity WheelColliders:
//          - 4-wheel suspension sync with Blender Wheel_FL/FR/RL/RR meshes
//          - Anti-roll bar stabilization & speed-sensitive steering
//          - Handbrake Drift mode (dynamic rear friction) & Nitro Boost system
//          - Dynamic engine RPM audio pitch & wet-road grip from DynamicWeather
//          - Unified Player / AI input interface + IDamageable vehicle health.
// ============================================================================

using UnityEngine;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Player;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ViceCity.Vehicles
{
    public enum VehicleDriveType
    {
        RearWheelDrive = 0,
        FrontWheelDrive = 1,
        AllWheelDrive = 2
    }

    /// <summary>
    /// Full-featured vehicle controller usable by both the Player and AI traffic/police drivers.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class ArcadeRealisticCarController : MonoBehaviour, IDamageable
    {
        [Header("Wheel Colliders & Visual Meshes")]
        [SerializeField] private WheelCollider wheelFL;
        [SerializeField] private WheelCollider wheelFR;
        [SerializeField] private WheelCollider wheelRL;
        [SerializeField] private WheelCollider wheelRR;
        [SerializeField] private Transform meshFL;
        [SerializeField] private Transform meshFR;
        [SerializeField] private Transform meshRL;
        [SerializeField] private Transform meshRR;

        [Header("Chassis & Suspension")]
        [SerializeField] private Vector3 centerOfMassOffset = new Vector3(0f, -0.45f, 0.08f);
        [SerializeField] private float antiRollForce = 6500f;
        [SerializeField] private float downforceCoefficient = 45f;

        [Header("Engine & Steering")]
        [SerializeField] private VehicleDriveType driveType = VehicleDriveType.RearWheelDrive;
        [SerializeField] private float maxMotorTorque = 2400f;
        [SerializeField] private float maxBrakeTorque = 4200f;
        [SerializeField] private float handbrakeTorque = 5500f;
        [SerializeField] private float topSpeedKmh = 215f;
        [SerializeField] private float maxSteerAngleLowSpeed = 34f;
        [SerializeField] private float maxSteerAngleHighSpeed = 11f;
        [SerializeField] private float steerResponseSpeed = 9f;

        [Header("Drift & Wet Asphalt Grip")]
        [SerializeField] private float normalRearSidewaysStiffness = 1.65f;
        [SerializeField] private float driftRearSidewaysStiffness = 0.58f;
        [SerializeField] private float rainGripPenaltyMultiplier = 0.72f;
        [SerializeField] private ParticleSystem[] driftSmokeParticles;

        [Header("Nitro Boost")]
        [SerializeField] private float nitroForceNewtons = 12500f;
        [SerializeField] private float maxNitroCharge = 100f;
        [SerializeField] private float nitroDrainPerSecond = 34f;
        [SerializeField] private float nitroRegenPerSecond = 8f;
        [SerializeField] private ParticleSystem[] nitroExhaustFlames;

        [Header("Audio & Lighting")]
        [SerializeField] private AudioSource engineAudioSource;
        [SerializeField] private float minEnginePitch = 0.75f;
        [SerializeField] private float maxEnginePitch = 2.35f;
        [SerializeField] private Light[] brakeLights;

        [Header("Vehicle Durability")]
        [SerializeField] private float maxVehicleHealth = 1000f;
        [SerializeField] private ParticleSystem engineSmokeVfx;

        private Rigidbody _rb;
        private EventSubscription _weatherSub;

        private bool _isPlayerControlled;
        private float _throttleInput;
        private float _steerInput;
        private float _brakeInput;
        private bool _handbrakeHeld;
        private bool _nitroHeld;

        private float _currentSteerAngle;
        private float _currentNitro;
        private float _currentHealth;
        private float _weatherRainIntensity;
        private bool _isDestroyed;

        public Rigidbody VehicleRigidbody => _rb;
        public bool IsPlayerControlled => _isPlayerControlled;
        public float SpeedKmh => _rb != null ? _rb.linearVelocity.magnitude * 3.6f : 0f;
        public float NitroNormalized => maxNitroCharge > 0f ? _currentNitro / maxNitroCharge : 0f;
        public float HealthNormalized => maxVehicleHealth > 0f ? _currentHealth / maxVehicleHealth : 0f;
        public bool IsDestroyed => _isDestroyed;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.centerOfMass = centerOfMassOffset;
            _currentNitro = maxNitroCharge;
            _currentHealth = maxVehicleHealth;
        }

        private void OnEnable()
        {
            _weatherSub = EventBus.Subscribe<WorldEnvironmentChangedEvent>(
                OnWorldEnvironmentChanged,
                replayStickyIfAvailable: true);
        }

        private void OnDisable()
        {
            _weatherSub.Dispose();
        }

        private void Update()
        {
            if (_isDestroyed)
            {
                _throttleInput = 0f;
                _nitroHeld = false;
                return;
            }

            if (_isPlayerControlled)
            {
                if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay)
                {
                    _throttleInput = 0f;
                    _steerInput = 0f;
                    _brakeInput = 1f;
                    _handbrakeHeld = true;
                    _nitroHeld = false;
                }
                else
                {
                    ReadPlayerDrivingInput();
                }
            }

            UpdateEngineAudio();
            UpdateBrakeLights();
            UpdateVisualEffects();
        }

        private void FixedUpdate()
        {
            if (wheelFL == null || wheelFR == null || wheelRL == null || wheelRR == null)
            {
                return;
            }

            ApplySteering(Time.fixedDeltaTime);
            ApplyDriveAndBrakes();
            ApplyDriftAndWeatherFriction();
            ApplyNitroBoost(Time.fixedDeltaTime);
            ApplyAntiRollBar(wheelFL, wheelFR);
            ApplyAntiRollBar(wheelRL, wheelRR);
            ApplyAerodynamicDownforce();

            SyncWheelMesh(wheelFL, meshFL);
            SyncWheelMesh(wheelFR, meshFR);
            SyncWheelMesh(wheelRL, meshRL);
            SyncWheelMesh(wheelRR, meshRR);
        }

        // ====================================================================
        // EXTERNAL CONTROL API (PLAYER & AI TRAFFIC / POLICE)
        // ====================================================================

        public void SetPlayerControlled(bool controlled)
        {
            _isPlayerControlled = controlled;
            if (!controlled)
            {
                _throttleInput = 0f;
                _steerInput = 0f;
                _brakeInput = 0.35f;
                _handbrakeHeld = false;
                _nitroHeld = false;
            }
        }

        /// <summary>Feeds steering, throttle, and braking inputs from TrafficAI or PoliceAI.</summary>
        public void SetAIInputs(float throttle, float steer, float brake, bool handbrake = false)
        {
            if (_isPlayerControlled || _isDestroyed)
            {
                return;
            }

            _throttleInput = Mathf.Clamp(throttle, -1f, 1f);
            _steerInput = Mathf.Clamp(steer, -1f, 1f);
            _brakeInput = Mathf.Clamp01(brake);
            _handbrakeHeld = handbrake;
            _nitroHeld = false;
        }

        public void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitNormal, GameObject instigator)
        {
            if (_isDestroyed || amount <= 0f) return;

            _currentHealth = Mathf.Max(0f, _currentHealth - amount);
            if (_currentHealth <= maxVehicleHealth * 0.35f && engineSmokeVfx != null && !engineSmokeVfx.isPlaying)
            {
                engineSmokeVfx.Play();
            }

            if (_currentHealth <= 0f)
            {
                _isDestroyed = true;
                _throttleInput = 0f;
                _brakeInput = 1f;
                _handbrakeHeld = true;
                _rb.AddForce(Vector3.up * 5500f, ForceMode.Impulse);
            }
        }

        // ====================================================================
        // PHYSICS PIPELINE
        // ====================================================================

        private void ApplySteering(float dt)
        {
            float speedFactor = Mathf.Clamp01(SpeedKmh / topSpeedKmh);
            float maxAngle = Mathf.Lerp(maxSteerAngleLowSpeed, maxSteerAngleHighSpeed, speedFactor);
            float targetAngle = _steerInput * maxAngle;
            _currentSteerAngle = Mathf.Lerp(_currentSteerAngle, targetAngle, dt * steerResponseSpeed);

            wheelFL.steerAngle = _currentSteerAngle;
            wheelFR.steerAngle = _currentSteerAngle;
        }

        private void ApplyDriveAndBrakes()
        {
            float speedKmh = SpeedKmh;
            float forwardSpeed = Vector3.Dot(_rb.linearVelocity, transform.forward);

            bool isReversingAgainstMotion = (_throttleInput > 0.1f && forwardSpeed < -1.5f) ||
                                            (_throttleInput < -0.1f && forwardSpeed > 1.5f);

            float motorTorque = 0f;
            float brakeTorque = _brakeInput * maxBrakeTorque;

            if (isReversingAgainstMotion)
            {
                brakeTorque = maxBrakeTorque;
            }
            else if (speedKmh < topSpeedKmh || _throttleInput < 0f)
            {
                motorTorque = _throttleInput * maxMotorTorque;
            }

            switch (driveType)
            {
                case VehicleDriveType.FrontWheelDrive:
                    wheelFL.motorTorque = motorTorque;
                    wheelFR.motorTorque = motorTorque;
                    wheelRL.motorTorque = 0f;
                    wheelRR.motorTorque = 0f;
                    break;

                case VehicleDriveType.AllWheelDrive:
                    float splitTorque = motorTorque * 0.6f;
                    wheelFL.motorTorque = splitTorque;
                    wheelFR.motorTorque = splitTorque;
                    wheelRL.motorTorque = splitTorque;
                    wheelRR.motorTorque = splitTorque;
                    break;

                case VehicleDriveType.RearWheelDrive:
                default:
                    wheelFL.motorTorque = 0f;
                    wheelFR.motorTorque = 0f;
                    wheelRL.motorTorque = motorTorque;
                    wheelRR.motorTorque = motorTorque;
                    break;
            }

            wheelFL.brakeTorque = brakeTorque;
            wheelFR.brakeTorque = brakeTorque;
            wheelRL.brakeTorque = _handbrakeHeld ? handbrakeTorque : brakeTorque;
            wheelRR.brakeTorque = _handbrakeHeld ? handbrakeTorque : brakeTorque;
        }

        private void ApplyDriftAndWeatherFriction()
        {
            float weatherMult = Mathf.Lerp(1f, rainGripPenaltyMultiplier, _weatherRainIntensity);
            float targetRearStiffness = (_handbrakeHeld ? driftRearSidewaysStiffness : normalRearSidewaysStiffness) * weatherMult;
            float targetFrontStiffness = normalRearSidewaysStiffness * weatherMult;

            SetSidewaysStiffness(wheelFL, targetFrontStiffness);
            SetSidewaysStiffness(wheelFR, targetFrontStiffness);
            SetSidewaysStiffness(wheelRL, targetRearStiffness);
            SetSidewaysStiffness(wheelRR, targetRearStiffness);
        }

        private void ApplyNitroBoost(float dt)
        {
            bool canUseNitro = _nitroHeld && _currentNitro > 1f && _throttleInput > 0.1f && SpeedKmh < topSpeedKmh * 1.25f;
            if (canUseNitro)
            {
                _currentNitro = Mathf.Max(0f, _currentNitro - nitroDrainPerSecond * dt);
                _rb.AddForce(transform.forward * nitroForceNewtons, ForceMode.Force);
            }
            else
            {
                _currentNitro = Mathf.Min(maxNitroCharge, _currentNitro + nitroRegenPerSecond * dt);
            }
        }

        private void ApplyAntiRollBar(WheelCollider leftWheel, WheelCollider rightWheel)
        {
            float travelL = 1f;
            float travelR = 1f;

            bool groundedL = leftWheel.GetGroundHit(out WheelHit hitL);
            if (groundedL)
            {
                travelL = (-leftWheel.transform.InverseTransformPoint(hitL.point).y - leftWheel.radius) / leftWheel.suspensionDistance;
            }

            bool groundedR = rightWheel.GetGroundHit(out WheelHit hitR);
            if (groundedR)
            {
                travelR = (-rightWheel.transform.InverseTransformPoint(hitR.point).y - rightWheel.radius) / rightWheel.suspensionDistance;
            }

            float antiRoll = (travelL - travelR) * antiRollForce;
            if (groundedL)
            {
                _rb.AddForceAtPosition(leftWheel.transform.up * -antiRoll, leftWheel.transform.position);
            }
            if (groundedR)
            {
                _rb.AddForceAtPosition(rightWheel.transform.up * antiRoll, rightWheel.transform.position);
            }
        }

        private void ApplyAerodynamicDownforce()
        {
            _rb.AddForce(-transform.up * (downforceCoefficient * _rb.linearVelocity.magnitude));
        }

        private static void SyncWheelMesh(WheelCollider col, Transform meshTransform)
        {
            if (col == null || meshTransform == null) return;
            col.GetWorldPose(out Vector3 pos, out Quaternion rot);
            meshTransform.SetPositionAndRotation(pos, rot);
        }

        private static void SetSidewaysStiffness(WheelCollider wheel, float stiffness)
        {
            WheelFrictionCurve curve = wheel.sidewaysFriction;
            curve.stiffness = stiffness;
            wheel.sidewaysFriction = curve;
        }

        // ====================================================================
        // AUDIO, VFX & INPUT
        // ====================================================================

        private void UpdateEngineAudio()
        {
            if (engineAudioSource == null) return;

            float speedRatio = Mathf.Clamp01(SpeedKmh / topSpeedKmh);
            float gearMod = (speedRatio * 5f) % 1f;
            float targetPitch = Mathf.Lerp(minEnginePitch, maxEnginePitch, speedRatio * 0.65f + gearMod * 0.35f);
            if (_nitroHeld && _currentNitro > 1f) targetPitch += 0.25f;

            engineAudioSource.pitch = Mathf.Lerp(engineAudioSource.pitch, targetPitch, Time.deltaTime * 8f);
        }

        private void UpdateBrakeLights()
        {
            if (brakeLights == null) return;
            bool braking = _brakeInput > 0.1f || _handbrakeHeld || _throttleInput < -0.1f;
            for (int i = 0; i < brakeLights.Length; i++)
            {
                if (brakeLights[i] != null) brakeLights[i].enabled = braking;
            }
        }

        private void UpdateVisualEffects()
        {
            bool drifting = _handbrakeHeld && SpeedKmh > 25f;
            ToggleParticleArray(driftSmokeParticles, drifting);

            bool boosting = _nitroHeld && _currentNitro > 1f && _throttleInput > 0.1f;
            ToggleParticleArray(nitroExhaustFlames, boosting);
        }

        private static void ToggleParticleArray(ParticleSystem[] systems, bool active)
        {
            if (systems == null) return;
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null) continue;
                if (active && !ps.isPlaying) ps.Play();
                else if (!active && ps.isPlaying) ps.Stop();
            }
        }

        private void OnWorldEnvironmentChanged(WorldEnvironmentChangedEvent evt)
        {
            _weatherRainIntensity = evt.RainIntensity01;
        }

        private void ReadPlayerDrivingInput()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            _throttleInput = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
            _steerInput = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            _handbrakeHeld = kb.spaceKey.isPressed;
            _nitroHeld = kb.leftShiftKey.isPressed;
            _brakeInput = _handbrakeHeld ? 0.5f : 0f;
#else
            _throttleInput = Input.GetAxis("Vertical");
            _steerInput = Input.GetAxis("Horizontal");
            _handbrakeHeld = Input.GetKey(KeyCode.Space);
            _nitroHeld = Input.GetKey(KeyCode.LeftShift);
            _brakeInput = _handbrakeHeld ? 0.5f : 0f;
#endif
        }
    }
}
