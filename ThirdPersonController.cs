// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 3 — Character Controller & Combat System
// File:    _Project/Scripts/Player/ThirdPersonController.cs
// Author:  Lead Game Architect
// Purpose: Smooth GTA-style Third-Person Character Controller with:
//          - Unity New Input System (with legacy fallback)
//          - Cinemachine camera pivot orbit (yaw/pitch + ADS shoulder offset)
//          - Walk, Run, Sprint (with Stamina), Jump/Gravity, & Coastal Swimming
//          - Health/Armor damage pipeline triggering GameManager Wasted state
//          - SaveLoadSystem (ISaveable) integration & external lock API for
//            Parkour, Cover, Cutscenes, and Vehicle Enter/Exit.
// ============================================================================

using System;
using UnityEngine;
using ViceCity.Core;
using ViceCity.Core.SaveSystem;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ViceCity.Player
{
    public enum PlayerLocomotionState
    {
        Grounded = 0,
        Airborne = 1,
        Swimming = 2,
        Parkour = 3,
        InCover = 4,
        InVehicle = 5,
        Dead = 6
    }

    /// <summary>
    /// Primary third-person character controller driving locomotion, swimming,
    /// stamina, health/armor, and Cinemachine camera follow target rotation.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class ThirdPersonController : MonoBehaviour, ISaveable
    {
        private static readonly int AnimSpeedHash = Animator.StringToHash("Speed");
        private static readonly int AnimGroundedHash = Animator.StringToHash("IsGrounded");
        private static readonly int AnimSwimmingHash = Animator.StringToHash("IsSwimming");
        private static readonly int AnimJumpTriggerHash = Animator.StringToHash("Jump");
        private static readonly int AnimAimingHash = Animator.StringToHash("IsAiming");

        [Header("Core References")]
        [SerializeField] private CharacterController characterController;
        [SerializeField] private Animator animator;
        [SerializeField] private Transform cameraFollowTarget;
        [SerializeField] private Camera mainCamera;

        [Header("Locomotion Speeds (m/s)")]
        [SerializeField] private float walkSpeed = 2.2f;
        [SerializeField] private float runSpeed = 5.4f;
        [SerializeField] private float sprintSpeed = 8.4f;
        [SerializeField] private float aimWalkSpeed = 2.8f;
        [SerializeField] private float accelerationRate = 12f;
        [SerializeField] private float rotationSmoothTime = 0.11f;

        [Header("Jump & Gravity")]
        [SerializeField] private float jumpHeight = 1.45f;
        [SerializeField] private float gravity = -22f;
        [SerializeField] private float groundedStickForce = -2.5f;
        [SerializeField] private float groundCheckRadius = 0.28f;
        [SerializeField] private float groundCheckOffset = -0.08f;
        [SerializeField] private LayerMask groundLayers = ~0;

        [Header("Swimming (Coastal Ocean & Pools)")]
        [SerializeField] private float swimSpeed = 3.6f;
        [SerializeField] private float swimSprintSpeed = 5.2f;
        [SerializeField] private float swimSurfaceOffset = 1.25f;
        [SerializeField] private float waterBuoyancyLerp = 6f;

        [Header("Cinemachine Camera Orbit")]
        [SerializeField] private float lookSensitivity = 0.18f;
        [SerializeField] private float minPitchDegrees = -42f;
        [SerializeField] private float maxPitchDegrees = 68f;
        [SerializeField] private bool invertLookY = false;

        [Header("Vitals & Stamina")]
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private float maxArmor = 100f;
        [SerializeField] private float maxStamina = 100f;
        [SerializeField] private float sprintStaminaDrainPerSec = 18f;
        [SerializeField] private float staminaRegenPerSec = 14f;
        [SerializeField] private float staminaRegenDelay = 1.2f;

        private SaveLoadSystem _saveLoadSystem;
        private PlayerLocomotionState _state = PlayerLocomotionState.Grounded;

        private Vector2 _moveInput;
        private Vector2 _lookInput;
        private bool _sprintHeld;
        private bool _walkToggle;
        private bool _jumpPressedThisFrame;
        private bool _isAiming;

        private float _currentSpeed;
        private float _verticalVelocity;
        private float _turnSmoothVelocity;
        private float _cameraYaw;
        private float _cameraPitch = 12f;
        private bool _isGrounded;

        private bool _isInWaterVolume;
        private float _waterSurfaceY;

        private float _health;
        private float _armor;
        private float _stamina;
        private float _staminaExhaustedCooldown;
        private bool _externalControlLocked;

        public string SaveKey => "Player.Controller";
        public PlayerLocomotionState State => _state;
        public CharacterController Controller => characterController;
        public Transform CameraFollowTarget => cameraFollowTarget;
        public bool IsGrounded => _isGrounded;
        public bool IsSwimming => _state == PlayerLocomotionState.Swimming;
        public bool IsAiming => _isAiming;
        public float Health => _health;
        public float Armor => _armor;
        public float Stamina => _stamina;
        public Vector2 MoveInput => _moveInput;

        private void Awake()
        {
            if (characterController == null)
            {
                characterController = GetComponent<CharacterController>();
            }

            if (mainCamera == null)
            {
                mainCamera = Camera.main;
            }

            _health = maxHealth;
            _armor = 0f;
            _stamina = maxStamina;

            if (cameraFollowTarget != null)
            {
                _cameraYaw = cameraFollowTarget.eulerAngles.y;
            }
            else
            {
                _cameraYaw = transform.eulerAngles.y;
            }
        }

        private void Start()
        {
            if (GameManager.HasInstance && GameManager.Instance.TryGetService(out _saveLoadSystem))
            {
                _saveLoadSystem.RegisterSaveable(this);
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
            if (_state == PlayerLocomotionState.Dead)
            {
                return;
            }

            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay)
            {
                _moveInput = Vector2.zero;
                _lookInput = Vector2.zero;
                return;
            }

            PollFallbackInputIfNoPlayerInputComponent();

            if (_externalControlLocked || _state == PlayerLocomotionState.InVehicle || _state == PlayerLocomotionState.Parkour)
            {
                return;
            }

            UpdateStamina(Time.deltaTime);

            if (_state == PlayerLocomotionState.InCover)
            {
                return;
            }

            EvaluateWaterTransition();

            if (_state == PlayerLocomotionState.Swimming)
            {
                HandleSwimmingMovement(Time.deltaTime);
            }
            else
            {
                CheckGrounded();
                HandleGroundAndAirMovement(Time.deltaTime);
            }

            UpdateAnimatorParameters();
            _jumpPressedThisFrame = false;
        }

        private void LateUpdate()
        {
            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay)
            {
                return;
            }

            UpdateCameraOrbitTarget();
        }

        // ====================================================================
        // NEW INPUT SYSTEM CALLBACKS (Compatible with Unity PlayerInput)
        // ====================================================================

#if ENABLE_INPUT_SYSTEM
        public void OnMove(InputValue value) => _moveInput = value.Get<Vector2>();
        public void OnLook(InputValue value) => _lookInput = value.Get<Vector2>();
        public void OnSprint(InputValue value) => _sprintHeld = value.isPressed;
        public void OnJump(InputValue value)
        {
            if (value.isPressed)
            {
                _jumpPressedThisFrame = true;
            }
        }
#endif

        /// <summary>Allows external modules (Combat, Parkour, Vehicles) to override locomotion cleanly.</summary>
        public void SetExternalControlLock(bool locked, PlayerLocomotionState targetState = PlayerLocomotionState.Grounded)
        {
            _externalControlLocked = locked;
            _state = targetState;
            if (locked)
            {
                _verticalVelocity = 0f;
                _currentSpeed = 0f;
            }
        }

        public void SetAimingState(bool aiming)
        {
            _isAiming = aiming;
            if (animator != null)
            {
                animator.SetBool(AnimAimingHash, aiming);
            }
        }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            characterController.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            characterController.enabled = true;
            _verticalVelocity = 0f;
        }

        // ====================================================================
        // HEALTH, ARMOR & WASTED PIPELINE
        // ====================================================================

        public void ApplyDamage(float amount)
        {
            if (_state == PlayerLocomotionState.Dead || amount <= 0f)
            {
                return;
            }

            float remainingDamage = amount;
            if (_armor > 0f)
            {
                float absorbed = Mathf.Min(_armor, remainingDamage);
                _armor -= absorbed;
                remainingDamage -= absorbed;
            }

            _health = Mathf.Max(0f, _health - remainingDamage);
            if (_health <= 0f)
            {
                HandleDeath();
            }
        }

        public void Heal(float healthAmount, float armorAmount = 0f)
        {
            if (_state == PlayerLocomotionState.Dead)
            {
                return;
            }

            _health = Mathf.Min(maxHealth, _health + Mathf.Max(0f, healthAmount));
            _armor = Mathf.Min(maxArmor, _armor + Mathf.Max(0f, armorAmount));
        }

        private void HandleDeath()
        {
            _state = PlayerLocomotionState.Dead;
            _moveInput = Vector2.zero;
            if (GameManager.HasInstance)
            {
                GameManager.Instance.TriggerPlayerFailureState(wasBusted: false, onRespawnReady: RespawnAtHospital);
            }
        }

        private void RespawnAtHospital()
        {
            _health = maxHealth;
            _stamina = maxStamina;
            _state = PlayerLocomotionState.Grounded;
            _externalControlLocked = false;
        }

        // ====================================================================
        // GROUND, AIR & SWIMMING MOVEMENT
        // ====================================================================

        private void CheckGrounded()
        {
            Vector3 spherePos = transform.position + Vector3.up * groundCheckOffset;
            _isGrounded = Physics.CheckSphere(spherePos, groundCheckRadius, groundLayers, QueryTriggerInteraction.Ignore)
                          || characterController.isGrounded;

            if (_isGrounded && _verticalVelocity < 0f)
            {
                _verticalVelocity = groundedStickForce;
            }

            _state = _isGrounded ? PlayerLocomotionState.Grounded : PlayerLocomotionState.Airborne;
        }

        private void HandleGroundAndAirMovement(float dt)
        {
            bool hasInput = _moveInput.sqrMagnitude > 0.01f;
            bool canSprint = _sprintHeld && !_isAiming && _stamina > 2f && hasInput;

            float targetSpeed = 0f;
            if (hasInput)
            {
                if (_isAiming) targetSpeed = aimWalkSpeed;
                else if (canSprint) targetSpeed = sprintSpeed;
                else if (_walkToggle) targetSpeed = walkSpeed;
                else targetSpeed = runSpeed;
            }

            _currentSpeed = Mathf.Lerp(_currentSpeed, targetSpeed, dt * accelerationRate);

            Vector3 moveDirection = Vector3.zero;
            if (hasInput)
            {
                float inputAngle = Mathf.Atan2(_moveInput.x, _moveInput.y) * Mathf.Rad2Deg;
                float camYaw = mainCamera != null ? mainCamera.transform.eulerAngles.y : _cameraYaw;
                float targetYaw = inputAngle + camYaw;

                if (_isAiming)
                {
                    // Strafe facing camera forward when ADS aiming
                    float smoothYaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, camYaw, ref _turnSmoothVelocity, rotationSmoothTime * 0.6f);
                    transform.rotation = Quaternion.Euler(0f, smoothYaw, 0f);
                }
                else
                {
                    float smoothYaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetYaw, ref _turnSmoothVelocity, rotationSmoothTime);
                    transform.rotation = Quaternion.Euler(0f, smoothYaw, 0f);
                }

                moveDirection = Quaternion.Euler(0f, targetYaw, 0f) * Vector3.forward;
            }
            else if (_isAiming && mainCamera != null)
            {
                float smoothYaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, mainCamera.transform.eulerAngles.y, ref _turnSmoothVelocity, rotationSmoothTime * 0.6f);
                transform.rotation = Quaternion.Euler(0f, smoothYaw, 0f);
            }

            if (_isGrounded && _jumpPressedThisFrame)
            {
                _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                _isGrounded = false;
                _state = PlayerLocomotionState.Airborne;
                if (animator != null)
                {
                    animator.SetTrigger(AnimJumpTriggerHash);
                }
            }

            _verticalVelocity += gravity * dt;
            Vector3 finalVelocity = (moveDirection.normalized * _currentSpeed) + (Vector3.up * _verticalVelocity);
            characterController.Move(finalVelocity * dt);
        }

        private void HandleSwimmingMovement(float dt)
        {
            bool hasInput = _moveInput.sqrMagnitude > 0.01f;
            float speed = (_sprintHeld && _stamina > 2f) ? swimSprintSpeed : swimSpeed;
            _currentSpeed = Mathf.Lerp(_currentSpeed, hasInput ? speed : 0f, dt * 6f);

            Vector3 planarMove = Vector3.zero;
            if (hasInput)
            {
                float camYaw = mainCamera != null ? mainCamera.transform.eulerAngles.y : _cameraYaw;
                float targetYaw = Mathf.Atan2(_moveInput.x, _moveInput.y) * Mathf.Rad2Deg + camYaw;
                float smoothYaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetYaw, ref _turnSmoothVelocity, rotationSmoothTime * 1.4f);
                transform.rotation = Quaternion.Euler(0f, smoothYaw, 0f);
                planarMove = (Quaternion.Euler(0f, targetYaw, 0f) * Vector3.forward) * _currentSpeed;
            }

            // Buoyancy keeps shoulders right at the coastal water surface
            float targetY = _waterSurfaceY - swimSurfaceOffset;
            float verticalCorrection = (targetY - transform.position.y) * waterBuoyancyLerp;
            characterController.Move((planarMove + Vector3.up * verticalCorrection) * dt);
        }

        private void EvaluateWaterTransition()
        {
            if (_isInWaterVolume && transform.position.y <= _waterSurfaceY - (swimSurfaceOffset * 0.85f))
            {
                _state = PlayerLocomotionState.Swimming;
                _verticalVelocity = 0f;
            }
            else if (_state == PlayerLocomotionState.Swimming && (!_isInWaterVolume || transform.position.y > _waterSurfaceY - 0.5f))
            {
                _state = PlayerLocomotionState.Grounded;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("WaterVolume"))
            {
                _isInWaterVolume = true;
                _waterSurfaceY = other.bounds.max.y;
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.CompareTag("WaterVolume"))
            {
                _isInWaterVolume = false;
            }
        }

        private void UpdateStamina(float dt)
        {
            bool isSprinting = _sprintHeld && _moveInput.sqrMagnitude > 0.01f && !_isAiming && _currentSpeed > runSpeed + 0.5f;
            if (isSprinting)
            {
                _stamina = Mathf.Max(0f, _stamina - sprintStaminaDrainPerSec * dt);
                _staminaExhaustedCooldown = staminaRegenDelay;
            }
            else
            {
                if (_staminaExhaustedCooldown > 0f)
                {
                    _staminaExhaustedCooldown -= dt;
                }
                else
                {
                    _stamina = Mathf.Min(maxStamina, _stamina + staminaRegenPerSec * dt);
                }
            }
        }

        private void UpdateCameraOrbitTarget()
        {
            if (cameraFollowTarget == null)
            {
                return;
            }

            float ySign = invertLookY ? 1f : -1f;
            _cameraYaw += _lookInput.x * lookSensitivity;
            _cameraPitch = Mathf.Clamp(_cameraPitch + (_lookInput.y * lookSensitivity * ySign), minPitchDegrees, maxPitchDegrees);
            cameraFollowTarget.rotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
        }

        private void UpdateAnimatorParameters()
        {
            if (animator == null)
            {
                return;
            }

            animator.SetFloat(AnimSpeedHash, _currentSpeed, 0.1f, Time.deltaTime);
            animator.SetBool(AnimGroundedHash, _isGrounded);
            animator.SetBool(AnimSwimmingHash, _state == PlayerLocomotionState.Swimming);
        }

        private void PollFallbackInputIfNoPlayerInputComponent()
        {
#if ENABLE_INPUT_SYSTEM
            if (GetComponent<PlayerInput>() != null)
            {
                return;
            }

            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (kb != null)
            {
                float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
                float y = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
                _moveInput = Vector2.ClampMagnitude(new Vector2(x, y), 1f);
                _sprintHeld = kb.leftShiftKey.isPressed;
                if (kb.spaceKey.wasPressedThisFrame) _jumpPressedThisFrame = true;
                if (kb.leftAltKey.wasPressedThisFrame) _walkToggle = !_walkToggle;
            }
            if (mouse != null)
            {
                _lookInput = mouse.delta.ReadValue();
            }
#else
            _moveInput = Vector2.ClampMagnitude(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1f);
            _lookInput = new Vector2(Input.GetAxis("Mouse X") * 12f, Input.GetAxis("Mouse Y") * 12f);
            _sprintHeld = Input.GetKey(KeyCode.LeftShift);
            if (Input.GetKeyDown(KeyCode.Space)) _jumpPressedThisFrame = true;
#endif
        }

        // ====================================================================
        // ISAVEABLE IMPLEMENTATION
        // ====================================================================

        public void CaptureState(GameSaveData saveData)
        {
            if (saveData?.Player == null) return;
            saveData.Player.Position = transform.position;
            saveData.Player.Rotation = transform.rotation;
            saveData.Player.Health = _health;
            saveData.Player.Armor = _armor;
            saveData.Player.Stamina = _stamina;
        }

        public void RestoreState(GameSaveData saveData)
        {
            if (saveData?.Player == null) return;
            Teleport(saveData.Player.Position, saveData.Player.Rotation);
            _health = Mathf.Clamp(saveData.Player.Health, 1f, maxHealth);
            _armor = Mathf.Clamp(saveData.Player.Armor, 0f, maxArmor);
            _stamina = Mathf.Clamp(saveData.Player.Stamina, 0f, maxStamina);
        }
    }
}
