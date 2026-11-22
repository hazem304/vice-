// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 5 — Living City & NPC AI (Pedestrians & Police)
// File:    _Project/Scripts/AI/PoliceDispatchAndSwatAI.cs
// Author:  Lead Game Architect
// Purpose: Escalating Police, Roadblock, Helicopter & SWAT response system:
//          - Star 1–2: Patrol officers pursue on NavMesh, shoot or BUST player
//          - Star 3–4: Spawns Roadblock barricades ahead of player + Police
//                      Helicopter with tracking searchlight & aerial fire
//          - Star 5:   Deploys armored SWAT Tactical Units for full lockdown.
// ============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Player;

namespace ViceCity.AI
{
    public enum PoliceUnitTier
    {
        PatrolOfficer = 0,
        SwatOperator = 1
    }

    /// <summary>
    /// Individual ground police or SWAT unit pursuing the player via NavMesh,
    /// confirming Line-of-Sight, engaging in gunfights, and triggering Busted arrests.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class PoliceOfficerAgent : MonoBehaviour, IDamageable
    {
        [Header("Unit Profile")]
        [SerializeField] private PoliceUnitTier unitTier = PoliceUnitTier.PatrolOfficer;
        [SerializeField] private float maxHealth = 90f;
        [SerializeField] private float chaseSpeed = 5.6f;
        [SerializeField] private float sightRange = 48f;
        [SerializeField] private float engagementRange = 24f;
        [SerializeField] private float arrestDistance = 2.4f;
        [SerializeField] private float fireCooldown = 0.75f;
        [SerializeField] private float bulletDamage = 9f;
        [SerializeField] private LayerMask visionObstacleMask = ~0;

        private NavMeshAgent _agent;
        private Transform _playerTransform;
        private ThirdPersonController _playerController;
        private WantedSystemManager _wantedManager;

        private float _health;
        private float _nextFireTime;
        private float _arrestProgress;
        private bool _isDead;

        public void InitializeUnit(Transform player, WantedSystemManager wantedManager, PoliceUnitTier tier)
        {
            _playerTransform = player;
            _playerController = player != null ? player.GetComponent<ThirdPersonController>() : null;
            _wantedManager = wantedManager;
            unitTier = tier;

            if (tier == PoliceUnitTier.SwatOperator)
            {
                maxHealth = 180f;
                chaseSpeed = 6.2f;
                bulletDamage = 14f;
                fireCooldown = 0.35f;
            }

            _health = maxHealth;
        }

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _health = maxHealth;
        }

        private void Update()
        {
            if (_isDead || _playerTransform == null || _wantedManager == null || !_wantedManager.IsWanted)
            {
                return;
            }

            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay)
            {
                return;
            }

            float distToPlayer = Vector3.Distance(transform.position, _playerTransform.position);
            bool hasLos = distToPlayer <= sightRange && CheckLineOfSight();

            if (hasLos)
            {
                _wantedManager.ReportPlayerSpotted(_playerTransform.position);
            }

            // Star 1–2 close-range Busted arrest if player is on foot and cornered
            if (hasLos && distToPlayer <= arrestDistance && _wantedManager.CurrentStars <= 2 &&
                _playerController != null && _playerController.State != PlayerLocomotionState.InVehicle)
            {
                _arrestProgress += Time.deltaTime;
                if (_arrestProgress >= 1.6f && GameManager.HasInstance)
                {
                    GameManager.Instance.TriggerPlayerFailureState(wasBusted: true);
                    _arrestProgress = 0f;
                    return;
                }
            }
            else
            {
                _arrestProgress = Mathf.Max(0f, _arrestProgress - Time.deltaTime);
            }

            // Navigate toward player's live or last known position
            Vector3 targetPos = hasLos ? _playerTransform.position : _wantedManager.LastKnownPlayerPosition;
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.speed = chaseSpeed;
                bool stopToShoot = hasLos && distToPlayer <= engagementRange * 0.75f;
                _agent.isStopped = stopToShoot;
                if (!stopToShoot)
                {
                    _agent.SetDestination(targetPos);
                }
            }

            if (hasLos && distToPlayer <= engagementRange)
            {
                FaceTarget(_playerTransform.position);
                TryFireAtPlayer();
            }
        }

        private bool CheckLineOfSight()
        {
            Vector3 eyePos = transform.position + Vector3.up * 1.55f;
            Vector3 targetPos = _playerTransform.position + Vector3.up * 1.1f;
            Vector3 dir = targetPos - eyePos;

            if (Physics.Raycast(eyePos, dir.normalized, out RaycastHit hit, dir.magnitude, visionObstacleMask, QueryTriggerInteraction.Ignore))
            {
                return hit.collider.CompareTag("Player") || hit.collider.transform.root == _playerTransform.root;
            }
            return true;
        }

        private void TryFireAtPlayer()
        {
            if (Time.time < _nextFireTime) return;
            _nextFireTime = Time.time + fireCooldown;

            // Slight accuracy spread so player can dodge or use CoverSystem
            if (_playerController != null && Random.value <= 0.65f)
            {
                _playerController.ApplyDamage(bulletDamage);
            }
        }

        private void FaceTarget(Vector3 target)
        {
            Vector3 dir = target - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir.normalized), Time.deltaTime * 10f);
            }
        }

        public void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitNormal, GameObject instigator)
        {
            if (_isDead || amount <= 0f) return;

            _health -= amount;
            bool isPlayer = instigator != null && instigator.CompareTag("Player");

            if (_health <= 0f)
            {
                _isDead = true;
                if (_agent != null) _agent.enabled = false;
                if (isPlayer)
                {
                    EventBus.Publish(new CrimeCommittedEvent(CrimeType.PoliceHomicide, transform.position, 65f));
                }
                Destroy(gameObject, 10f);
            }
            else if (isPlayer)
            {
                EventBus.Publish(new CrimeCommittedEvent(CrimeType.PoliceAssault, transform.position, 30f));
            }
        }
    }

    /// <summary>
    /// Star 3+ Police Helicopter that orbits above the player, illuminates them with a
    /// tracking spotlight, and reports continuous aerial Line-of-Sight to WantedSystemManager.
    /// </summary>
    public sealed class PoliceHelicopterAI : MonoBehaviour
    {
        [SerializeField] private float hoverAltitude = 28f;
        [SerializeField] private float orbitRadius = 22f;
        [SerializeField] private float moveSpeed = 18f;
        [SerializeField] private Light searchlight;
        [SerializeField] private Transform mainRotor;

        private Transform _target;
        private WantedSystemManager _wantedManager;
        private float _orbitAngle;

        public void Initialize(Transform playerTarget, WantedSystemManager wantedManager)
        {
            _target = playerTarget;
            _wantedManager = wantedManager;
        }

        private void Update()
        {
            if (mainRotor != null)
            {
                mainRotor.Rotate(Vector3.up, 1100f * Time.deltaTime, Space.Self);
            }

            if (_target == null || _wantedManager == null || !_wantedManager.IsWanted) return;

            _orbitAngle += Time.deltaTime * 0.65f;
            Vector3 anchor = _wantedManager.LastKnownPlayerPosition;
            Vector3 desiredPos = anchor + new Vector3(
                Mathf.Cos(_orbitAngle) * orbitRadius,
                hoverAltitude,
                Mathf.Sin(_orbitAngle) * orbitRadius);

            transform.position = Vector3.Lerp(transform.position, desiredPos, Time.deltaTime * (moveSpeed * 0.12f));

            Vector3 lookTarget = _target.position;
            Vector3 flatLook = lookTarget - transform.position;
            flatLook.y = 0f;
            if (flatLook.sqrMagnitude > 0.1f)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(flatLook.normalized), Time.deltaTime * 3.5f);
            }

            if (searchlight != null)
            {
                searchlight.transform.LookAt(lookTarget + Vector3.up * 0.8f);
            }

            if (Vector3.Distance(transform.position, _target.position) <= 65f)
            {
                _wantedManager.ReportPlayerSpotted(_target.position);
            }
        }
    }

    /// <summary>
    /// Master Police Dispatch Coordinator listening to <see cref="WantedLevelChangedEvent"/>
    /// to deploy Patrols (1–2★), Roadblocks &amp; Helicopter (3–4★), and SWAT Squads (5★).
    /// </summary>
    public sealed class PoliceDispatchAndSwatAI : MonoBehaviour
    {
        [Header("References & Prefabs")]
        [SerializeField] private Transform playerTransform;
        [SerializeField] private WantedSystemManager wantedManager;
        [SerializeField] private GameObject patrolOfficerPrefab;
        [SerializeField] private GameObject swatOperatorPrefab;
        [SerializeField] private GameObject roadblockBarricadePrefab;
        [SerializeField] private GameObject policeHelicopterPrefab;

        [Header("Dispatch Limits")]
        [SerializeField] private float reinforcementIntervalSeconds = 7.5f;
        [SerializeField] private float spawnMinDistance = 26f;
        [SerializeField] private float spawnMaxDistance = 48f;
        [SerializeField] private float roadblockAheadDistance = 55f;

        private readonly List<GameObject> _activeUnits = new List<GameObject>(16);
        private readonly List<GameObject> _activeRoadblocks = new List<GameObject>(4);
        private GameObject _activeHelicopter;
        private EventSubscription _wantedSub;
        private float _nextReinforcementTime;
        private int _currentStars;

        private void OnEnable()
        {
            _wantedSub = EventBus.Subscribe<WantedLevelChangedEvent>(OnWantedLevelChanged, replayStickyIfAvailable: true);
        }

        private void OnDisable()
        {
            _wantedSub.Dispose();
        }

        private void Start()
        {
            if (playerTransform == null)
            {
                GameObject p = GameObject.FindGameObjectWithTag("Player");
                if (p != null) playerTransform = p.transform;
            }

            if (wantedManager == null && GameManager.HasInstance)
            {
                GameManager.Instance.TryGetService(out wantedManager);
            }
        }

        private void Update()
        {
            if (_currentStars <= 0 || playerTransform == null) return;
            if (GameManager.HasInstance && GameManager.Instance.CurrentState != GameState.Gameplay) return;

            PruneDestroyedUnits();

            if (Time.time >= _nextReinforcementTime)
            {
                _nextReinforcementTime = Time.time + reinforcementIntervalSeconds;
                EvaluateReinforcements();
            }
        }

        private void OnWantedLevelChanged(WantedLevelChangedEvent evt)
        {
            _currentStars = evt.CurrentStars;

            if (_currentStars == 0)
            {
                DespawnAllPoliceAssets();
                return;
            }

            EvaluateReinforcements();
        }

        private void EvaluateReinforcements()
        {
            int desiredGroundUnits = _currentStars switch
            {
                1 => 2,
                2 => 4,
                3 => 6,
                4 => 8,
                5 => 12,
                _ => 0
            };

            while (_activeUnits.Count < desiredGroundUnits)
            {
                bool spawnSwat = _currentStars >= 5 || (_currentStars == 4 && Random.value < 0.4f);
                if (!TrySpawnGroundUnit(spawnSwat ? PoliceUnitTier.SwatOperator : PoliceUnitTier.PatrolOfficer))
                {
                    break;
                }
            }

            // Star 3+: Deploy Roadblock ahead of player + Police Helicopter
            if (_currentStars >= 3)
            {
                if (_activeRoadblocks.Count < (_currentStars >= 5 ? 3 : 1))
                {
                    TrySpawnRoadblockAhead();
                }

                if (_activeHelicopter == null && policeHelicopterPrefab != null)
                {
                    Vector3 heliSpawn = playerTransform.position + new Vector3(0f, 38f, -40f);
                    _activeHelicopter = Instantiate(policeHelicopterPrefab, heliSpawn, Quaternion.identity);
                    PoliceHelicopterAI heliAi = _activeHelicopter.GetComponent<PoliceHelicopterAI>();
                    if (heliAi == null) heliAi = _activeHelicopter.AddComponent<PoliceHelicopterAI>();
                    heliAi.Initialize(playerTransform, wantedManager);
                }
            }
        }

        private bool TrySpawnGroundUnit(PoliceUnitTier tier)
        {
            GameObject prefab = (tier == PoliceUnitTier.SwatOperator && swatOperatorPrefab != null)
                ? swatOperatorPrefab
                : patrolOfficerPrefab;

            if (prefab == null) return false;

            Vector2 ring = Random.insideUnitCircle.normalized * Random.Range(spawnMinDistance, spawnMaxDistance);
            Vector3 probe = playerTransform.position + new Vector3(ring.x, 0f, ring.y);

            if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, 12f, NavMesh.AllAreas))
            {
                return false;
            }

            GameObject unitObj = Instantiate(prefab, hit.position, Quaternion.identity);
            PoliceOfficerAgent agent = unitObj.GetComponent<PoliceOfficerAgent>();
            if (agent == null) agent = unitObj.AddComponent<PoliceOfficerAgent>();
            agent.InitializeUnit(playerTransform, wantedManager, tier);

            _activeUnits.Add(unitObj);
            return true;
        }

        private void TrySpawnRoadblockAhead()
        {
            if (roadblockBarricadePrefab == null) return;

            Vector3 aheadPos = playerTransform.position + playerTransform.forward * roadblockAheadDistance;
            if (NavMesh.SamplePosition(aheadPos, out NavMeshHit hit, 15f, NavMesh.AllAreas))
            {
                Quaternion rot = Quaternion.LookRotation(-playerTransform.forward, Vector3.up);
                GameObject rb = Instantiate(roadblockBarricadePrefab, hit.position, rot);
                _activeRoadblocks.Add(rb);
            }
        }

        private void PruneDestroyedUnits()
        {
            for (int i = _activeUnits.Count - 1; i >= 0; i--)
            {
                if (_activeUnits[i] == null) _activeUnits.RemoveAt(i);
            }
            for (int i = _activeRoadblocks.Count - 1; i >= 0; i--)
            {
                if (_activeRoadblocks[i] == null) _activeRoadblocks.RemoveAt(i);
            }
        }

        private void DespawnAllPoliceAssets()
        {
            for (int i = 0; i < _activeUnits.Count; i++)
            {
                if (_activeUnits[i] != null) Destroy(_activeUnits[i]);
            }
            _activeUnits.Clear();

            for (int i = 0; i < _activeRoadblocks.Count; i++)
            {
                if (_activeRoadblocks[i] != null) Destroy(_activeRoadblocks[i]);
            }
            _activeRoadblocks.Clear();

            if (_activeHelicopter != null)
            {
                Destroy(_activeHelicopter);
                _activeHelicopter = null;
            }
        }
    }
}
