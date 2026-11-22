// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 3 — Character Controller & Combat System
// File:    _Project/Scripts/Player/ParkourAndCoverSystem.cs
// Author:  Lead Game Architect
// Purpose: GTA-style Parkour (Vaulting waist-high obstacles & Mantling ledges)
//          and Tactical Cover System (Snap to CoverSurface walls, slide along
//          cover normal, edge detection, and pop-out aiming integration).
// ============================================================================

using System.Collections;
using UnityEngine;
using ViceCity.Core;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ViceCity.Player
{
    /// <summary>
    /// Modular companion to <see cref="ThirdPersonController"/> adding fluid parkour
    /// vault/climb maneuvers and third-person snap-to-cover mechanics.
    /// </summary>
    [RequireComponent(typeof(ThirdPersonController))]
    public sealed class ParkourAndCoverSystem : MonoBehaviour
    {
        private static readonly int AnimInCoverHash = Animator.StringToHash("InCover");
        private static readonly int AnimCoverMoveHash = Animator.StringToHash("CoverMove");
        private static readonly int AnimVaultTriggerHash = Animator.StringToHash("Vault");
        private static readonly int AnimClimbTriggerHash = Animator.StringToHash("Climb");

        [Header("References")]
        [SerializeField] private ThirdPersonController playerController;
        [SerializeField] private Animator animator;

        [Header("Parkour (Vault & Ledge Mantle)")]
        [SerializeField] private LayerMask climbableLayers = ~0;
        [SerializeField] private float obstacleDetectDistance = 1.15f;
        [SerializeField] private float minVaultHeight = 0.55f;
        [SerializeField] private float maxVaultHeight = 1.35f;
        [SerializeField] private float maxClimbHeight = 2.35f;
        [SerializeField] private float vaultDurationSeconds = 0.42f;
        [SerializeField] private float climbDurationSeconds = 0.68f;
        [SerializeField] private float clearanceCheckHeight = 1.85f;

        [Header("Cover System")]
        [SerializeField] private LayerMask coverLayers = ~0;
        [SerializeField] private float coverDetectRange = 1.5f;
        [SerializeField] private float coverStandOffDistance = 0.42f;
        [SerializeField] private float coverSlideSpeed = 2.4f;
        [SerializeField] private float coverEdgeCheckOffset = 0.55f;

        private bool _isPerformingParkour;
        private bool _isInCover;
        private bool _isPeekingFromCover;
        private Vector3 _currentCoverNormal;
        private float _coverFacingSign = 1f;

        public bool IsPerformingParkour => _isPerformingParkour;
        public bool IsInCover => _isInCover;
        public bool IsPeekingFromCover => _isPeekingFromCover;

        private void Awake()
        {
            if (playerController == null)
            {
                playerController = GetComponent<ThirdPersonController>();
            }
        }

        private void Update()
        {
            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay)
            {
                return;
            }

            if (_isPerformingParkour || playerController.State == PlayerLocomotionState.InVehicle || playerController.State == PlayerLocomotionState.Dead)
            {
                return;
            }

            if (WasCoverTogglePressed())
            {
                if (_isInCover)
                {
                    ExitCover();
                }
                else
                {
                    TryEnterCover();
                }
            }

            if (_isInCover)
            {
                UpdateCoverMovement(Time.deltaTime);
                return;
            }

            if (WasParkourPressed() && playerController.IsGrounded)
            {
                TryTriggerParkour();
            }
        }

        // ====================================================================
        // PARKOUR: VAULTING & LEDGE CLIMBING
        // ====================================================================

        /// <summary>Probes in front of the player for a valid vault or ledge climb target.</summary>
        public bool TryTriggerParkour()
        {
            if (_isPerformingParkour || _isInCover)
            {
                return false;
            }

            Vector3 forwardOrigin = transform.position + Vector3.up * 0.5f;
            if (!Physics.Raycast(forwardOrigin, transform.forward, out RaycastHit wallHit, obstacleDetectDistance, climbableLayers, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            // Probe downward from above the obstacle to find the exact top surface
            Vector3 topProbeStart = wallHit.point + (transform.forward * 0.45f) + (Vector3.up * (maxClimbHeight + 0.4f));
            if (!Physics.Raycast(topProbeStart, Vector3.down, out RaycastHit topHit, maxClimbHeight + 0.5f, climbableLayers, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            float obstacleHeight = topHit.point.y - transform.position.y;
            if (obstacleHeight < minVaultHeight || obstacleHeight > maxClimbHeight)
            {
                return false;
            }

            // Check if this is a thin obstacle we can vault completely over
            bool isVault = obstacleHeight <= maxVaultHeight && CanVaultOverObstacle(topHit.point, out Vector3 vaultLandingPos);
            Vector3 finalLanding = isVault ? vaultLandingPos : topHit.point + (transform.forward * 0.25f);

            // Verify headroom at landing destination
            if (Physics.CheckCapsule(
                finalLanding + Vector3.up * 0.35f,
                finalLanding + Vector3.up * (clearanceCheckHeight - 0.3f),
                0.28f,
                climbableLayers,
                QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            StartCoroutine(ExecuteParkourRoutine(topHit.point, finalLanding, isVault));
            return true;
        }

        private bool CanVaultOverObstacle(Vector3 topPoint, out Vector3 landingPoint)
        {
            Vector3 overProbe = topPoint + (transform.forward * 1.15f) + (Vector3.up * 0.5f);
            if (Physics.Raycast(overProbe, Vector3.down, out RaycastHit groundHit, 3.0f, climbableLayers, QueryTriggerInteraction.Ignore))
            {
                // If ground on the far side is significantly lower than the top surface, treat as a Vault
                if ((topPoint.y - groundHit.point.y) >= 0.4f)
                {
                    landingPoint = groundHit.point;
                    return true;
                }
            }

            landingPoint = topPoint;
            return false;
        }

        private IEnumerator ExecuteParkourRoutine(Vector3 apexPoint, Vector3 targetLanding, bool isVault)
        {
            _isPerformingParkour = true;
            playerController.SetExternalControlLock(true, PlayerLocomotionState.Parkour);
            playerController.Controller.enabled = false;

            if (animator != null)
            {
                animator.SetTrigger(isVault ? AnimVaultTriggerHash : AnimClimbTriggerHash);
            }

            Vector3 startPos = transform.position;
            Vector3 midPos = new Vector3(apexPoint.x, apexPoint.y + (isVault ? 0.28f : 0.05f), apexPoint.z);
            float duration = isVault ? vaultDurationSeconds : climbDurationSeconds;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t);

                // Quadratic Bezier curve for natural arc over waist barriers or onto ledges
                Vector3 a = Vector3.Lerp(startPos, midPos, eased);
                Vector3 b = Vector3.Lerp(midPos, targetLanding, eased);
                transform.position = Vector3.Lerp(a, b, eased);
                yield return null;
            }

            transform.position = targetLanding;
            playerController.Controller.enabled = true;
            playerController.SetExternalControlLock(false, PlayerLocomotionState.Grounded);
            _isPerformingParkour = false;
        }

        // ====================================================================
        // TACTICAL COVER SYSTEM
        // ====================================================================

        public bool TryEnterCover()
        {
            Vector3 chestOrigin = transform.position + Vector3.up * 0.9f;
            if (!Physics.Raycast(chestOrigin, transform.forward, out RaycastHit hit, coverDetectRange, coverLayers, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            // Require either CoverSurface tag or near-vertical wall normal
            if (!hit.collider.CompareTag("CoverSurface") && Mathf.Abs(hit.normal.y) > 0.2f)
            {
                return false;
            }

            _isInCover = true;
            _currentCoverNormal = new Vector3(hit.normal.x, 0f, hit.normal.z).normalized;
            playerController.SetExternalControlLock(true, PlayerLocomotionState.InCover);

            // Snap player cleanly against the cover wall with back facing the wall
            Vector3 snapPos = hit.point + (_currentCoverNormal * coverStandOffDistance);
            snapPos.y = transform.position.y;
            Quaternion coverRot = Quaternion.LookRotation(_currentCoverNormal, Vector3.up);
            playerController.Teleport(snapPos, coverRot);

            if (animator != null)
            {
                animator.SetBool(AnimInCoverHash, true);
            }

            return true;
        }

        public void ExitCover()
        {
            if (!_isInCover)
            {
                return;
            }

            _isInCover = false;
            _isPeekingFromCover = false;
            playerController.SetExternalControlLock(false, PlayerLocomotionState.Grounded);

            if (animator != null)
            {
                animator.SetBool(AnimInCoverHash, false);
                animator.SetFloat(AnimCoverMoveHash, 0f);
            }
        }

        private void UpdateCoverMovement(float dt)
        {
            Vector2 move = playerController.MoveInput;

            // Pulling back away from the wall exits cover smoothly
            if (move.y < -0.65f)
            {
                ExitCover();
                return;
            }

            // Pop-out peeking when ADS aiming in cover
            _isPeekingFromCover = playerController.IsAiming;
            if (_isPeekingFromCover)
            {
                return;
            }

            float lateralInput = move.x;
            if (Mathf.Abs(lateralInput) < 0.08f)
            {
                if (animator != null) animator.SetFloat(AnimCoverMoveHash, 0f, 0.1f, dt);
                return;
            }

            _coverFacingSign = Mathf.Sign(lateralInput);
            Vector3 coverTangent = Vector3.Cross(Vector3.up, _currentCoverNormal).normalized;
            Vector3 slideDir = coverTangent * _coverFacingSign;

            // Edge check: ensure the wall still exists ahead before sliding past a corner
            Vector3 edgeOrigin = transform.position + Vector3.up * 0.9f + (slideDir * coverEdgeCheckOffset);
            if (!Physics.Raycast(edgeOrigin, -_currentCoverNormal, out RaycastHit wallCheck, coverDetectRange, coverLayers, QueryTriggerInteraction.Ignore))
            {
                if (animator != null) animator.SetFloat(AnimCoverMoveHash, 0f, 0.1f, dt);
                return;
            }

            // Follow curved walls smoothly by updating normal
            _currentCoverNormal = Vector3.Slerp(_currentCoverNormal, new Vector3(wallCheck.normal.x, 0f, wallCheck.normal.z).normalized, dt * 10f);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(_currentCoverNormal, Vector3.up), dt * 12f);

            playerController.Controller.Move(slideDir * (Mathf.Abs(lateralInput) * coverSlideSpeed * dt));

            if (animator != null)
            {
                animator.SetFloat(AnimCoverMoveHash, lateralInput, 0.1f, dt);
            }
        }

        private static bool WasParkourPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }

        private static bool WasCoverTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.qKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Q);
#endif
        }
    }
}
