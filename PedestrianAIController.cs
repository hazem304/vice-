// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 5 — Living City & NPC AI (Pedestrians & Police)
// File:    _Project/Scripts/AI/PedestrianAIController.cs
// Author:  Lead Game Architect
// Purpose: Autonomous NavMesh pedestrian AI for a living Vice Coast sidewalk:
//          - Strolling along urban sidewalks & stopping to chat with nearby NPCs
//          - Dynamic umbrella deployment during tropical rain (EventBus)
//          - Panic & sprint fleeing when gunshots occur nearby (WeaponFiredEvent)
//          - IDamageable combat reactions + CrimeCommittedEvent reporting.
// ============================================================================

using UnityEngine;
using UnityEngine.AI;
using ViceCity.Core;
using ViceCity.Core.Events;
using ViceCity.Player;

namespace ViceCity.AI
{
    public enum PedestrianBehaviorState
    {
        Strolling = 0,
        Chatting = 1,
        Fleeing = 2,
        Dead = 3
    }

    /// <summary>
    /// Broadcast whenever the player commits an observable crime so <see cref="WantedSystemManager"/>
    /// escalates the Wanted star level without tight coupling.
    /// </summary>
    public readonly struct CrimeCommittedEvent : IGameEvent
    {
        public readonly CrimeType Type;
        public readonly Vector3 Location;
        public readonly float HeatValue;

        public CrimeCommittedEvent(CrimeType type, Vector3 location, float heatValue)
        {
            Type = type;
            Location = location;
            HeatValue = heatValue;
        }
    }

    public enum CrimeType
    {
        PublicGunfire = 0,
        VehicleTheft = 1,
        CivilianAssault = 2,
        CivilianHomicide = 3,
        PoliceAssault = 4,
        PoliceHomicide = 5
    }

    /// <summary>
    /// Controls an individual NavMesh pedestrian with stroll/chat/flee FSM and rain umbrella reactions.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class PedestrianAIController : MonoBehaviour, IDamageable
    {
        private static readonly int AnimSpeedHash = Animator.StringToHash("Speed");
        private static readonly int AnimPanicHash = Animator.StringToHash("IsPanicking");
        private static readonly int AnimChattingHash = Animator.StringToHash("IsChatting");
        private static readonly int AnimUmbrellaHash = Animator.StringToHash("HoldingUmbrella");

        [Header("References")]
        [SerializeField] private NavMeshAgent navAgent;
        [SerializeField] private Animator animator;
        [SerializeField] private GameObject umbrellaPropObject;

        [Header("Locomotion & AI Tuning")]
        [SerializeField] private float strollSpeed = 1.65f;
        [SerializeField] private float fleeSprintSpeed = 5.4f;
        [SerializeField] private float wanderRadius = 26f;
        [SerializeField] private float fleeDistance = 38f;
        [SerializeField] private float panicDurationSeconds = 16f;
        [SerializeField] private float chatCheckRadius = 2.8f;
        [SerializeField] private float chatDurationSeconds = 6.5f;
        [SerializeField] private float rainUmbrellaThreshold = 0.25f;

        [Header("Vitals")]
        [SerializeField] private float maxHealth = 60f;

        private EventSubscription _gunshotSub;
        private EventSubscription _weatherSub;

        private PedestrianBehaviorState _state = PedestrianBehaviorState.Strolling;
        private float _health;
        private float _stateTimer;
        private float _chatCooldown;
        private Vector3 _threatOrigin;
        private bool _isHoldingUmbrella;

        public PedestrianBehaviorState State => _state;

        private void Awake()
        {
            if (navAgent == null) navAgent = GetComponent<NavMeshAgent>();
            _health = maxHealth;
            if (umbrellaPropObject != null) umbrellaPropObject.SetActive(false);
        }

        private void OnEnable()
        {
            _gunshotSub = EventBus.Subscribe<WeaponFiredEvent>(OnWeaponFired);
            _weatherSub = EventBus.Subscribe<WorldEnvironmentChangedEvent>(
                OnWeatherChanged,
                replayStickyIfAvailable: true);
        }

        private void OnDisable()
        {
            _gunshotSub.Dispose();
            _weatherSub.Dispose();
        }

        private void Start()
        {
            EnterStrollingState();
        }

        private void Update()
        {
            if (_state == PedestrianBehaviorState.Dead) return;
            if (GameManager.HasInstance && GameManager.Instance.IsPaused) return;

            if (_chatCooldown > 0f) _chatCooldown -= Time.deltaTime;

            switch (_state)
            {
                case PedestrianBehaviorState.Strolling:
                    UpdateStrolling();
                    break;
                case PedestrianBehaviorState.Chatting:
                    UpdateChatting();
                    break;
                case PedestrianBehaviorState.Fleeing:
                    UpdateFleeing();
                    break;
            }

            if (animator != null && navAgent != null)
            {
                animator.SetFloat(AnimSpeedHash, navAgent.velocity.magnitude, 0.1f, Time.deltaTime);
            }
        }

        // ====================================================================
        // BEHAVIOR STATES
        // ====================================================================

        private void EnterStrollingState()
        {
            _state = PedestrianBehaviorState.Strolling;
            if (navAgent != null && navAgent.isOnNavMesh)
            {
                navAgent.isStopped = false;
                navAgent.speed = strollSpeed;
                PickNewWanderDestination();
            }

            if (animator != null)
            {
                animator.SetBool(AnimPanicHash, false);
                animator.SetBool(AnimChattingHash, false);
            }
        }

        private void UpdateStrolling()
        {
            if (navAgent == null || !navAgent.isOnNavMesh) return;

            if (!navAgent.pathPending && navAgent.remainingDistance <= 1.2f)
            {
                if (_chatCooldown <= 0f && TryStartConversationWithNearbyPedestrian())
                {
                    return;
                }
                PickNewWanderDestination();
            }
        }

        private bool TryStartConversationWithNearbyPedestrian()
        {
            if (_isHoldingUmbrella) return false; // Less likely to stop and chat in heavy rain

            Collider[] nearby = Physics.OverlapSphere(transform.position, chatCheckRadius);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i].gameObject == gameObject) continue;
                PedestrianAIController other = nearby[i].GetComponentInParent<PedestrianAIController>();
                if (other != null && other._state == PedestrianBehaviorState.Strolling)
                {
                    StartChatting(other.transform.position);
                    other.StartChatting(transform.position);
                    return true;
                }
            }
            return false;
        }

        public void StartChatting(Vector3 partnerPosition)
        {
            if (_state == PedestrianBehaviorState.Fleeing || _state == PedestrianBehaviorState.Dead) return;

            _state = PedestrianBehaviorState.Chatting;
            _stateTimer = chatDurationSeconds;
            _chatCooldown = 20f;

            if (navAgent != null && navAgent.isOnNavMesh)
            {
                navAgent.isStopped = true;
            }

            Vector3 lookDir = partnerPosition - transform.position;
            lookDir.y = 0f;
            if (lookDir.sqrMagnitude > 0.01f)
            {
                transform.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
            }

            if (animator != null) animator.SetBool(AnimChattingHash, true);
        }

        private void UpdateChatting()
        {
            _stateTimer -= Time.deltaTime;
            if (_stateTimer <= 0f)
            {
                EnterStrollingState();
            }
        }

        public void TriggerPanicFlee(Vector3 dangerOrigin)
        {
            if (_state == PedestrianBehaviorState.Dead) return;

            _state = PedestrianBehaviorState.Fleeing;
            _threatOrigin = dangerOrigin;
            _stateTimer = panicDurationSeconds;

            SetUmbrellaActive(false);

            if (navAgent != null && navAgent.isOnNavMesh)
            {
                navAgent.isStopped = false;
                navAgent.speed = fleeSprintSpeed;
                SetFleeDestination(dangerOrigin);
            }

            if (animator != null)
            {
                animator.SetBool(AnimChattingHash, false);
                animator.SetBool(AnimPanicHash, true);
            }
        }

        private void UpdateFleeing()
        {
            _stateTimer -= Time.deltaTime;
            if (_stateTimer <= 0f)
            {
                EnterStrollingState();
                return;
            }

            if (navAgent != null && navAgent.isOnNavMesh && !navAgent.pathPending && navAgent.remainingDistance < 3f)
            {
                SetFleeDestination(_threatOrigin);
            }
        }

        private void PickNewWanderDestination()
        {
            Vector3 randomOffset = Random.insideUnitSphere * wanderRadius;
            randomOffset.y = 0f;
            if (NavMesh.SamplePosition(transform.position + randomOffset, out NavMeshHit hit, wanderRadius, NavMesh.AllAreas))
            {
                navAgent.SetDestination(hit.position);
            }
        }

        private void SetFleeDestination(Vector3 dangerOrigin)
        {
            Vector3 awayDir = (transform.position - dangerOrigin);
            awayDir.y = 0f;
            if (awayDir.sqrMagnitude < 0.01f) awayDir = transform.forward;

            Vector3 candidate = transform.position + awayDir.normalized * fleeDistance;
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 14f, NavMesh.AllAreas))
            {
                navAgent.SetDestination(hit.position);
            }
        }

        // ====================================================================
        // EVENTBUS REACTIONS & DAMAGE
        // ====================================================================

        private void OnWeaponFired(WeaponFiredEvent evt)
        {
            if (_state == PedestrianBehaviorState.Dead) return;

            float distSqr = (transform.position - evt.Origin).sqrMagnitude;
            if (distSqr <= evt.AudibleRadius * evt.AudibleRadius)
            {
                TriggerPanicFlee(evt.Origin);
            }
        }

        private void OnWeatherChanged(WorldEnvironmentChangedEvent evt)
        {
            if (_state == PedestrianBehaviorState.Dead || _state == PedestrianBehaviorState.Fleeing) return;
            SetUmbrellaActive(evt.RainIntensity01 >= rainUmbrellaThreshold);
        }

        private void SetUmbrellaActive(bool active)
        {
            _isHoldingUmbrella = active;
            if (umbrellaPropObject != null) umbrellaPropObject.SetActive(active);
            if (animator != null) animator.SetBool(AnimUmbrellaHash, active);
        }

        public void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitNormal, GameObject instigator)
        {
            if (_state == PedestrianBehaviorState.Dead || amount <= 0f) return;

            _health -= amount;
            bool isPlayerInstigator = instigator != null && instigator.CompareTag("Player");

            if (_health <= 0f)
            {
                _state = PedestrianBehaviorState.Dead;
                SetUmbrellaActive(false);
                if (navAgent != null) navAgent.enabled = false;
                if (isPlayerInstigator)
                {
                    EventBus.Publish(new CrimeCommittedEvent(CrimeType.CivilianHomicide, transform.position, 45f));
                }
                Destroy(gameObject, 15f);
            }
            else
            {
                TriggerPanicFlee(instigator != null ? instigator.transform.position : hitPoint);
                if (isPlayerInstigator)
                {
                    EventBus.Publish(new CrimeCommittedEvent(CrimeType.CivilianAssault, transform.position, 22f));
                }
            }
        }
    }
}
