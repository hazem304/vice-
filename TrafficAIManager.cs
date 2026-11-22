// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 4 — Advanced Vehicle Physics & Traffic AI
// File:    _Project/Scripts/Vehicles/TrafficAIManager.cs
// Author:  Lead Game Architect
// Purpose: Autonomous urban Traffic AI system featuring:
//          - TrafficSignalIntersection (automated Red/Yellow/Green phases)
//          - TrafficWaypointNode lane graph with branching intersections
//          - TrafficVehicleAgent driving ArcadeRealisticCarController with
//            lane following, red-light stopping, and forward collision avoidance
//          - TrafficAIManager dynamic density spawning & despawning around player.
// ============================================================================

using System.Collections.Generic;
using UnityEngine;
using ViceCity.Core;

namespace ViceCity.Vehicles
{
    public enum TrafficSignalColor
    {
        Green = 0,
        Yellow = 1,
        Red = 2
    }

    /// <summary>
    /// Controls alternating North-South and East-West traffic signals at a city intersection.
    /// </summary>
    public sealed class TrafficSignalIntersection : MonoBehaviour
    {
        [SerializeField] private float greenDuration = 14f;
        [SerializeField] private float yellowDuration = 3f;

        private float _timer;
        private bool _northSouthActive = true;
        private TrafficSignalColor _activeAxisColor = TrafficSignalColor.Green;

        public TrafficSignalColor GetSignalForAxis(bool isNorthSouthAxis)
        {
            if (isNorthSouthAxis == _northSouthActive)
            {
                return _activeAxisColor;
            }
            return TrafficSignalColor.Red;
        }

        private void Update()
        {
            if (GameManager.HasInstance && GameManager.Instance.IsPaused) return;

            _timer += Time.deltaTime;
            if (_activeAxisColor == TrafficSignalColor.Green && _timer >= greenDuration)
            {
                _timer = 0f;
                _activeAxisColor = TrafficSignalColor.Yellow;
            }
            else if (_activeAxisColor == TrafficSignalColor.Yellow && _timer >= yellowDuration)
            {
                _timer = 0f;
                _northSouthActive = !_northSouthActive;
                _activeAxisColor = TrafficSignalColor.Green;
            }
        }
    }

    /// <summary>
    /// Single directional lane node placed along Vice Coast streets.
    /// </summary>
    public sealed class TrafficWaypointNode : MonoBehaviour
    {
        public List<TrafficWaypointNode> NextWaypoints = new List<TrafficWaypointNode>();
        public float SpeedLimitKmh = 52f;
        public TrafficSignalIntersection ControlledByIntersection;
        public bool IsNorthSouthAxis = true;

        public TrafficWaypointNode PickNextWaypoint()
        {
            if (NextWaypoints == null || NextWaypoints.Count == 0) return null;
            return NextWaypoints[Random.Range(0, NextWaypoints.Count)];
        }

        public bool MustStopForSignal()
        {
            if (ControlledByIntersection == null) return false;
            TrafficSignalColor color = ControlledByIntersection.GetSignalForAxis(IsNorthSouthAxis);
            return color == TrafficSignalColor.Red || color == TrafficSignalColor.Yellow;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(transform.position, 0.5f);
            if (NextWaypoints == null) return;
            for (int i = 0; i < NextWaypoints.Count; i++)
            {
                if (NextWaypoints[i] != null)
                {
                    Gizmos.DrawLine(transform.position, NextWaypoints[i].transform.position);
                }
            }
        }
    }

    /// <summary>
    /// AI driver component that pilots an <see cref="ArcadeRealisticCarController"/> along waypoints,
    /// obeys traffic lights, and avoids collisions with vehicles or pedestrians ahead.
    /// </summary>
    [RequireComponent(typeof(ArcadeRealisticCarController))]
    public sealed class TrafficVehicleAgent : MonoBehaviour
    {
        [Header("Navigation & Sensors")]
        [SerializeField] private float waypointReachRadius = 4.2f;
        [SerializeField] private float sensorLengthMeters = 12f;
        [SerializeField] private float emergencyBrakeDistance = 5.5f;
        [SerializeField] private float sensorRadius = 0.9f;
        [SerializeField] private LayerMask obstacleLayers = ~0;

        private ArcadeRealisticCarController _car;
        private TrafficWaypointNode _currentTarget;

        public bool IsActiveAI => _car != null && !_car.IsPlayerControlled && !_car.IsDestroyed;

        private void Awake()
        {
            _car = GetComponent<ArcadeRealisticCarController>();
        }

        public void AssignInitialWaypoint(TrafficWaypointNode startNode)
        {
            _currentTarget = startNode;
        }

        private void FixedUpdate()
        {
            if (!IsActiveAI || _currentTarget == null)
            {
                return;
            }

            Vector3 toTarget = _currentTarget.transform.position - transform.position;
            toTarget.y = 0f;
            float distToNode = toTarget.magnitude;

            // Advance waypoint if reached (unless held by a red light right before the intersection)
            if (distToNode <= waypointReachRadius)
            {
                if (_currentTarget.MustStopForSignal())
                {
                    _car.SetAIInputs(throttle: 0f, steer: 0f, brake: 1f, handbrake: true);
                    return;
                }

                TrafficWaypointNode next = _currentTarget.PickNextWaypoint();
                if (next != null)
                {
                    _currentTarget = next;
                    toTarget = _currentTarget.transform.position - transform.position;
                    toTarget.y = 0f;
                }
            }

            // Compute proportional steering toward target lane node
            Vector3 localTarget = transform.InverseTransformPoint(_currentTarget.transform.position);
            float steer = Mathf.Clamp(localTarget.x / Mathf.Max(2f, localTarget.magnitude), -1f, 1f);

            // Check forward traffic light stop line & dynamic obstacle sensor
            bool redLightAhead = _currentTarget.MustStopForSignal() && distToNode < 14f;
            float obstacleDist = ProbeForwardObstacleDistance();

            float targetSpeedKmh = _currentTarget.SpeedLimitKmh;
            float throttle = 0.65f;
            float brake = 0f;
            bool handbrake = false;

            if (redLightAhead || obstacleDist < emergencyBrakeDistance)
            {
                throttle = 0f;
                brake = 1f;
                handbrake = _car.SpeedKmh < 8f;
            }
            else if (obstacleDist < sensorLengthMeters)
            {
                float slowFactor = Mathf.InverseLerp(emergencyBrakeDistance, sensorLengthMeters, obstacleDist);
                targetSpeedKmh *= slowFactor;
                throttle = _car.SpeedKmh > targetSpeedKmh ? 0f : 0.4f * slowFactor;
                brake = _car.SpeedKmh > targetSpeedKmh ? 0.65f : 0f;
            }
            else if (_car.SpeedKmh > targetSpeedKmh)
            {
                throttle = 0f;
                brake = 0.25f;
            }

            // Reduce speed slightly during tight intersection turns
            if (Mathf.Abs(steer) > 0.45f && _car.SpeedKmh > 28f)
            {
                throttle = 0.2f;
                brake = 0.35f;
            }

            _car.SetAIInputs(throttle, steer, brake, handbrake);
        }

        private float ProbeForwardObstacleDistance()
        {
            Vector3 origin = transform.position + Vector3.up * 0.85f + transform.forward * 2.3f;
            if (Physics.SphereCast(origin, sensorRadius, transform.forward, out RaycastHit hit, sensorLengthMeters, obstacleLayers, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.root != transform.root)
                {
                    return hit.distance;
                }
            }
            return sensorLengthMeters + 1f;
        }
    }

    /// <summary>
    /// Dynamically maintains ambient coastal traffic density around the player while
    /// despawning distant vehicles to preserve 60 FPS performance.
    /// </summary>
    public sealed class TrafficAIManager : MonoBehaviour
    {
        [Header("Traffic Spawning")]
        [SerializeField] private Transform playerTransform;
        [SerializeField] private GameObject[] trafficCarPrefabs;
        [SerializeField] private TrafficWaypointNode[] roadWaypoints;
        [SerializeField, Range(0, 35)] private int maxActiveTrafficCars = 14;
        [SerializeField] private float minSpawnDistance = 42f;
        [SerializeField] private float maxSpawnDistance = 125f;
        [SerializeField] private float despawnDistance = 155f;
        [SerializeField] private float spawnCheckInterval = 1.5f;
        [SerializeField] private LayerMask spawnClearanceLayers = ~0;

        private readonly List<TrafficVehicleAgent> _spawnedAgents = new List<TrafficVehicleAgent>(24);
        private float _nextSpawnCheckTime;

        private void Start()
        {
            if (playerTransform == null)
            {
                GameObject p = GameObject.FindGameObjectWithTag("Player");
                if (p != null) playerTransform = p.transform;
            }

            if (roadWaypoints == null || roadWaypoints.Length == 0)
            {
                roadWaypoints = FindObjectsByType<TrafficWaypointNode>(FindObjectsSortMode.None);
            }
        }

        private void Update()
        {
            if (playerTransform == null || trafficCarPrefabs == null || trafficCarPrefabs.Length == 0)
            {
                return;
            }

            if (Time.time < _nextSpawnCheckTime)
            {
                return;
            }
            _nextSpawnCheckTime = Time.time + spawnCheckInterval;

            CleanupDistantVehicles();
            if (_spawnedAgents.Count < maxActiveTrafficCars)
            {
                TrySpawnTrafficVehicle();
            }
        }

        private void CleanupDistantVehicles()
        {
            float despawnSqr = despawnDistance * despawnDistance;
            for (int i = _spawnedAgents.Count - 1; i >= 0; i--)
            {
                TrafficVehicleAgent agent = _spawnedAgents[i];
                if (agent == null)
                {
                    _spawnedAgents.RemoveAt(i);
                    continue;
                }

                // Never despawn a car the player hijacked and is currently driving
                if (!agent.IsActiveAI)
                {
                    _spawnedAgents.RemoveAt(i);
                    continue;
                }

                if ((agent.transform.position - playerTransform.position).sqrMagnitude > despawnSqr)
                {
                    Destroy(agent.gameObject);
                    _spawnedAgents.RemoveAt(i);
                }
            }
        }

        private void TrySpawnTrafficVehicle()
        {
            if (roadWaypoints == null || roadWaypoints.Length == 0) return;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                TrafficWaypointNode node = roadWaypoints[Random.Range(0, roadWaypoints.Length)];
                if (node == null) continue;

                float dist = Vector3.Distance(node.transform.position, playerTransform.position);
                if (dist < minSpawnDistance || dist > maxSpawnDistance) continue;

                Vector3 spawnPos = node.transform.position + Vector3.up * 0.45f;
                if (Physics.CheckSphere(spawnPos, 3.2f, spawnClearanceLayers, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                TrafficWaypointNode next = node.PickNextWaypoint();
                Quaternion spawnRot = next != null
                    ? Quaternion.LookRotation((next.transform.position - node.transform.position).normalized, Vector3.up)
                    : node.transform.rotation;

                GameObject prefab = trafficCarPrefabs[Random.Range(0, trafficCarPrefabs.Length)];
                GameObject carObj = Instantiate(prefab, spawnPos, spawnRot);

                TrafficVehicleAgent agent = carObj.GetComponent<TrafficVehicleAgent>();
                if (agent == null) agent = carObj.AddComponent<TrafficVehicleAgent>();

                agent.AssignInitialWaypoint(next != null ? next : node);
                _spawnedAgents.Add(agent);
                return;
            }
        }
    }
}
