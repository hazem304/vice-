// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 4 — Advanced Vehicle Physics & Traffic AI
// File:    _Project/Scripts/Vehicles/VehicleEnterExitStateMachine.cs
// Author:  Lead Game Architect
// Purpose: GTA-style Enter/Exit Vehicle State Machine supporting any vehicle
//          in the world:
//          - Detects closest drivable vehicle within interaction radius
//          - Animates hinged Door_L / Door_R opening & closing cleanly
//          - Transitions player into seat, switches ChunkStreamingManager target
//          - Checks obstacle clearance on exit (auto-switches to passenger side
//            if driver door is blocked) and publishes PlayerVehicleTransitionEvent.
// ============================================================================

using System.Collections;
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
    public enum VehicleOccupancyState
    {
        OnFoot = 0,
        EnteringVehicle = 1,
        DrivingVehicle = 2,
        ExitingVehicle = 3
    }

    /// <summary>
    /// Attached to any drivable vehicle prefab to expose its seat, entry points, and hinged doors.
    /// Matches Blender 03_generate_sports_car_modular.py naming (Door_L / Door_R).
    /// </summary>
    public sealed class DrivableVehicleMount : MonoBehaviour
    {
        public ArcadeRealisticCarController CarController;
        public Transform DriverSeatPoint;
        public Transform LeftEntryPoint;
        public Transform RightEntryPoint;
        public Transform LeftDoorHinge;
        public Transform RightDoorHinge;
        public float DoorOpenAngleDegrees = 62f;

        private void Reset()
        {
            CarController = GetComponent<ArcadeRealisticCarController>();
            LeftDoorHinge = transform.Find("Door_L");
            RightDoorHinge = transform.Find("Door_R");
        }
    }

    /// <summary>
    /// Placed on the Player GameObject to handle seamless entering and exiting of any nearby vehicle.
    /// </summary>
    [RequireComponent(typeof(ThirdPersonController))]
    public sealed class VehicleEnterExitStateMachine : MonoBehaviour
    {
        private static readonly int AnimDrivingHash = Animator.StringToHash("IsDriving");

        [Header("Player References")]
        [SerializeField] private ThirdPersonController playerController;
        [SerializeField] private Animator playerAnimator;
        [SerializeField] private GameObject playerVisualRoot;

        [Header("Interaction Settings")]
        [SerializeField] private float vehicleDetectRadius = 3.6f;
        [SerializeField] private LayerMask vehicleLayerMask = ~0;
        [SerializeField] private LayerMask exitObstacleMask = ~0;
        [SerializeField] private float doorSwingDuration = 0.32f;
        [SerializeField] private float seatTransitionDuration = 0.35f;
        [SerializeField] private float maxSafeExitSpeedKmh = 35f;

        private VehicleOccupancyState _occupancyState = VehicleOccupancyState.OnFoot;
        private DrivableVehicleMount _currentVehicle;
        private Transform _originalParent;

        public VehicleOccupancyState OccupancyState => _occupancyState;
        public DrivableVehicleMount CurrentVehicle => _currentVehicle;

        private void Awake()
        {
            if (playerController == null) playerController = GetComponent<ThirdPersonController>();
            if (playerVisualRoot == null) playerVisualRoot = gameObject;
            _originalParent = transform.parent;
        }

        private void Update()
        {
            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay)
            {
                return;
            }

            if (_occupancyState == VehicleOccupancyState.EnteringVehicle ||
                _occupancyState == VehicleOccupancyState.ExitingVehicle)
            {
                return;
            }

            if (WasInteractPressed())
            {
                if (_occupancyState == VehicleOccupancyState.OnFoot)
                {
                    DrivableVehicleMount candidate = FindNearestDrivableVehicle();
                    if (candidate != null)
                    {
                        StartCoroutine(EnterVehicleRoutine(candidate));
                    }
                }
                else if (_occupancyState == VehicleOccupancyState.DrivingVehicle && _currentVehicle != null)
                {
                    StartCoroutine(ExitVehicleRoutine(_currentVehicle));
                }
            }
        }

        private DrivableVehicleMount FindNearestDrivableVehicle()
        {
            Collider[] hits = Physics.OverlapSphere(transform.position, vehicleDetectRadius, vehicleLayerMask, QueryTriggerInteraction.Ignore);
            DrivableVehicleMount best = null;
            float bestDistSqr = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                DrivableVehicleMount mount = hits[i].GetComponentInParent<DrivableVehicleMount>();
                if (mount == null || mount.CarController == null || mount.CarController.IsDestroyed)
                {
                    continue;
                }

                float d2 = (mount.transform.position - transform.position).sqrMagnitude;
                if (d2 < bestDistSqr)
                {
                    bestDistSqr = d2;
                    best = mount;
                }
            }

            return best;
        }

        private IEnumerator EnterVehicleRoutine(DrivableVehicleMount mount)
        {
            _occupancyState = VehicleOccupancyState.EnteringVehicle;
            _currentVehicle = mount;

            playerController.SetExternalControlLock(true, PlayerLocomotionState.InVehicle);
            playerController.Controller.enabled = false;

            // Choose closer door (Driver Left vs Passenger Right)
            bool useLeftDoor = IsLeftDoorCloser(mount);
            Transform doorHinge = useLeftDoor ? mount.LeftDoorHinge : mount.RightDoorHinge;
            Transform entryPoint = useLeftDoor ? mount.LeftEntryPoint : mount.RightEntryPoint;
            float openAngle = useLeftDoor ? -mount.DoorOpenAngleDegrees : mount.DoorOpenAngleDegrees;

            // 1. Align player with door entry point
            if (entryPoint != null)
            {
                yield return LerpTransformRoutine(transform, entryPoint.position, entryPoint.rotation, 0.22f);
            }

            // 2. Swing door open smoothly
            if (doorHinge != null)
            {
                yield return RotateDoorRoutine(doorHinge, Quaternion.Euler(0f, openAngle, 0f), doorSwingDuration);
            }

            // 3. Move player into driver seat and parent to vehicle
            Transform seat = mount.DriverSeatPoint != null ? mount.DriverSeatPoint : mount.transform;
            yield return LerpTransformRoutine(transform, seat.position, seat.rotation, seatTransitionDuration);

            transform.SetParent(seat, worldPositionStays: false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

            if (playerAnimator != null)
            {
                playerAnimator.SetBool(AnimDrivingHash, true);
            }

            // 4. Close door behind player
            if (doorHinge != null)
            {
                yield return RotateDoorRoutine(doorHinge, Quaternion.identity, doorSwingDuration * 0.85f);
            }

            // 5. Hand off vehicle control and world streaming target
            mount.CarController.SetPlayerControlled(true);
            UpdateChunkStreamingTarget(mount.transform);

            _occupancyState = VehicleOccupancyState.DrivingVehicle;
            EventBus.Publish(new PlayerVehicleTransitionEvent(mount.gameObject, enteredVehicle: true));
        }

        private IEnumerator ExitVehicleRoutine(DrivableVehicleMount mount)
        {
            _occupancyState = VehicleOccupancyState.ExitingVehicle;
            bool bailingAtHighSpeed = mount.CarController != null && mount.CarController.SpeedKmh > maxSafeExitSpeedKmh;

            mount.CarController.SetPlayerControlled(false);

            // Pick unobstructed exit side (prefer left driver side, fallback to right passenger side)
            bool useLeft = IsExitSideClear(mount, leftSide: true) || !IsExitSideClear(mount, leftSide: false);
            Transform doorHinge = useLeft ? mount.LeftDoorHinge : mount.RightDoorHinge;
            Transform exitPoint = useLeft ? mount.LeftEntryPoint : mount.RightEntryPoint;
            float openAngle = useLeft ? -mount.DoorOpenAngleDegrees : mount.DoorOpenAngleDegrees;

            // 1. Open door
            if (doorHinge != null)
            {
                yield return RotateDoorRoutine(doorHinge, Quaternion.Euler(0f, openAngle, 0f), doorSwingDuration * 0.8f);
            }

            // 2. Unparent player and place safely outside vehicle
            transform.SetParent(_originalParent, worldPositionStays: true);

            Vector3 fallbackOffset = mount.transform.right * (useLeft ? -1.85f : 1.85f);
            Vector3 exitPos = exitPoint != null ? exitPoint.position : (mount.transform.position + fallbackOffset + Vector3.up * 0.25f);
            Quaternion exitRot = Quaternion.Euler(0f, mount.transform.eulerAngles.y, 0f);

            if (playerAnimator != null)
            {
                playerAnimator.SetBool(AnimDrivingHash, false);
            }

            playerController.Teleport(exitPos, exitRot);

            // Small bail-out damage if jumping out of a fast-moving sports car
            if (bailingAtHighSpeed)
            {
                playerController.ApplyDamage(12f);
            }

            // 3. Close door smoothly
            if (doorHinge != null)
            {
                yield return RotateDoorRoutine(doorHinge, Quaternion.identity, doorSwingDuration);
            }

            UpdateChunkStreamingTarget(transform);
            playerController.SetExternalControlLock(false, PlayerLocomotionState.Grounded);

            GameObject exitedVehicle = mount.gameObject;
            _currentVehicle = null;
            _occupancyState = VehicleOccupancyState.OnFoot;
            EventBus.Publish(new PlayerVehicleTransitionEvent(exitedVehicle, enteredVehicle: false));
        }

        private bool IsLeftDoorCloser(DrivableVehicleMount mount)
        {
            if (mount.LeftEntryPoint == null || mount.RightEntryPoint == null) return true;
            float leftDist = (transform.position - mount.LeftEntryPoint.position).sqrMagnitude;
            float rightDist = (transform.position - mount.RightEntryPoint.position).sqrMagnitude;
            return leftDist <= rightDist;
        }

        private bool IsExitSideClear(DrivableVehicleMount mount, bool leftSide)
        {
            Vector3 sideDir = mount.transform.right * (leftSide ? -1f : 1f);
            Vector3 origin = mount.transform.position + Vector3.up * 0.8f;
            return !Physics.SphereCast(origin, 0.35f, sideDir, out _, 1.75f, exitObstacleMask, QueryTriggerInteraction.Ignore);
        }

        private static void UpdateChunkStreamingTarget(Transform newTarget)
        {
            if (GameManager.HasInstance && GameManager.Instance.TryGetService(out ChunkStreamingManager streamer))
            {
                streamer.SetStreamingTarget(newTarget);
            }
        }

        private static IEnumerator RotateDoorRoutine(Transform hinge, Quaternion targetLocalRot, float duration)
        {
            Quaternion startRot = hinge.localRotation;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                hinge.localRotation = Quaternion.Slerp(startRot, targetLocalRot, t);
                yield return null;
            }
            hinge.localRotation = targetLocalRot;
        }

        private static IEnumerator LerpTransformRoutine(Transform target, Vector3 endPos, Quaternion endRot, float duration)
        {
            Vector3 startPos = target.position;
            Quaternion startRot = target.rotation;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                target.SetPositionAndRotation(Vector3.Lerp(startPos, endPos, t), Quaternion.Slerp(startRot, endRot, t));
                yield return null;
            }
            target.SetPositionAndRotation(endPos, endRot);
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
