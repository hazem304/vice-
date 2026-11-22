// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 1 — Core Architecture & Project Setup
// File:    _Project/Scripts/Core/Events/EventBus.cs
// Author:  Lead Game Architect
// Purpose: Decoupled, type-safe, exception-isolated EventBus with built-in
//          core event definitions shared across all 7 project modules.
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ViceCity.Core.Events
{
    /// <summary>
    /// Marker interface for all events dispatched through the <see cref="EventBus"/>.
    /// Implement as <c>readonly struct</c> whenever possible to avoid heap allocations.
    /// </summary>
    public interface IGameEvent
    {
    }

    /// <summary>
    /// Execution priority for event listeners. Higher priority handlers execute first.
    /// </summary>
    public enum EventPriority
    {
        Low = 0,
        Normal = 50,
        High = 100,
        Critical = 200
    }

    /// <summary>
    /// Disposable subscription handle returned when registering a listener.
    /// Calling <see cref="Dispose"/> safely unsubscribes the listener.
    /// </summary>
    public readonly struct EventSubscription : IDisposable
    {
        private readonly Action _unsubscribeAction;

        internal EventSubscription(Action unsubscribeAction)
        {
            _unsubscribeAction = unsubscribeAction;
        }

        /// <summary>
        /// Unsubscribes the associated listener from the <see cref="EventBus"/>.
        /// Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            _unsubscribeAction?.Invoke();
        }
    }

    /// <summary>
    /// Central, decoupled Publish/Subscribe EventBus for inter-module communication.
    /// Supports priority ordering, safe mid-dispatch subscribe/unsubscribe, exception
    /// isolation per listener, sticky (retained) events, and Unity Domain Reload cleanup.
    /// </summary>
    public static class EventBus
    {
        private sealed class ListenerEntry
        {
            public Delegate Callback;
            public EventPriority Priority;
            public int RegistrationOrder;
            public bool IsMarkedForRemoval;
        }

        private sealed class Channel
        {
            public readonly List<ListenerEntry> Listeners = new List<ListenerEntry>(8);
            public readonly List<ListenerEntry> PendingAdditions = new List<ListenerEntry>(4);
            public int ActiveDispatchDepth;
            public bool NeedsSort;
            public bool HasPendingRemovals;
        }

        private static readonly Dictionary<Type, Channel> Channels = new Dictionary<Type, Channel>(64);
        private static readonly Dictionary<Type, IGameEvent> StickyEvents = new Dictionary<Type, IGameEvent>(16);
        private static readonly object SyncRoot = new object();
        private static int _nextOrderId;

        /// <summary>
        /// Enables verbose diagnostic logging for publish/subscribe calls in development builds.
        /// </summary>
        public static bool EnableDebugLogging { get; set; } = false;

        /// <summary>
        /// Automatically resets all channels when Unity enters Play Mode with Domain Reload disabled.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnDomainReload()
        {
            ClearAll();
        }

        /// <summary>
        /// Subscribes a typed callback to receive events of type <typeparamref name="TEvent"/>.
        /// </summary>
        /// <typeparam name="TEvent">Event struct or class implementing <see cref="IGameEvent"/>.</typeparam>
        /// <param name="callback">Handler invoked when the event is published.</param>
        /// <param name="priority">Execution priority relative to other listeners.</param>
        /// <param name="replayStickyIfAvailable">
        /// If true and a sticky event of this type was previously published, invokes <paramref name="callback"/> immediately.
        /// </param>
        /// <returns>An <see cref="EventSubscription"/> handle that unsubscribes on <see cref="IDisposable.Dispose"/>.</returns>
        public static EventSubscription Subscribe<TEvent>(
            Action<TEvent> callback,
            EventPriority priority = EventPriority.Normal,
            bool replayStickyIfAvailable = false) where TEvent : IGameEvent
        {
            if (callback == null)
            {
                Debug.LogWarning($"[EventBus] Ignored null subscription attempt for event type {typeof(TEvent).Name}.");
                return default;
            }

            Type eventType = typeof(TEvent);
            ListenerEntry entry;
            IGameEvent stickyToReplay = null;

            lock (SyncRoot)
            {
                Channel channel = GetOrCreateChannel(eventType);

                // Prevent duplicate registration of the exact same delegate instance.
                if (ContainsCallback(channel, callback))
                {
                    Debug.LogWarning($"[EventBus] Duplicate subscription ignored for {eventType.Name} ({callback.Method.Name}).");
                    return new EventSubscription(() => Unsubscribe(callback));
                }

                entry = new ListenerEntry
                {
                    Callback = callback,
                    Priority = priority,
                    RegistrationOrder = ++_nextOrderId,
                    IsMarkedForRemoval = false
                };

                if (channel.ActiveDispatchDepth > 0)
                {
                    channel.PendingAdditions.Add(entry);
                }
                else
                {
                    channel.Listeners.Add(entry);
                    channel.NeedsSort = true;
                }

                if (replayStickyIfAvailable && StickyEvents.TryGetValue(eventType, out IGameEvent sticky))
                {
                    stickyToReplay = sticky;
                }
            }

            if (EnableDebugLogging)
            {
                Debug.Log($"[EventBus] Subscribed {callback.Method.DeclaringType?.Name}.{callback.Method.Name} to {eventType.Name} (Priority: {priority}).");
            }

            if (stickyToReplay != null)
            {
                InvokeSingleSafe(callback, (TEvent)stickyToReplay, eventType);
            }

            return new EventSubscription(() => Unsubscribe(callback));
        }

        /// <summary>
        /// Unsubscribes a previously registered callback from <typeparamref name="TEvent"/>.
        /// Safe to call while an event of the same type is currently being dispatched.
        /// </summary>
        public static void Unsubscribe<TEvent>(Action<TEvent> callback) where TEvent : IGameEvent
        {
            if (callback == null)
            {
                return;
            }

            Type eventType = typeof(TEvent);

            lock (SyncRoot)
            {
                if (!Channels.TryGetValue(eventType, out Channel channel))
                {
                    return;
                }

                // Check active listener list
                for (int i = channel.Listeners.Count - 1; i >= 0; i--)
                {
                    if (Equals(channel.Listeners[i].Callback, callback))
                    {
                        if (channel.ActiveDispatchDepth > 0)
                        {
                            channel.Listeners[i].IsMarkedForRemoval = true;
                            channel.HasPendingRemovals = true;
                        }
                        else
                        {
                            channel.Listeners.RemoveAt(i);
                        }
                    }
                }

                // Check pending additions queue
                for (int i = channel.PendingAdditions.Count - 1; i >= 0; i--)
                {
                    if (Equals(channel.PendingAdditions[i].Callback, callback))
                    {
                        channel.PendingAdditions.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// Publishes an event to all subscribed listeners in descending <see cref="EventPriority"/> order.
        /// Exceptions thrown by individual listeners are caught and logged without interrupting other listeners.
        /// </summary>
        /// <typeparam name="TEvent">Event type implementing <see cref="IGameEvent"/>.</typeparam>
        /// <param name="gameEvent">The event payload to broadcast.</param>
        /// <param name="sticky">If true, stores this event so late subscribers can replay it.</param>
        public static void Publish<TEvent>(TEvent gameEvent, bool sticky = false) where TEvent : IGameEvent
        {
            Type eventType = typeof(TEvent);
            Channel channel;
            int listenerCount;

            lock (SyncRoot)
            {
                if (sticky)
                {
                    StickyEvents[eventType] = gameEvent;
                }

                if (!Channels.TryGetValue(eventType, out channel) || channel.Listeners.Count == 0)
                {
                    if (EnableDebugLogging)
                    {
                        Debug.Log($"[EventBus] Published {eventType.Name} (0 listeners, sticky={sticky}).");
                    }
                    return;
                }

                if (channel.NeedsSort && channel.ActiveDispatchDepth == 0)
                {
                    SortListeners(channel);
                    channel.NeedsSort = false;
                }

                channel.ActiveDispatchDepth++;
                listenerCount = channel.Listeners.Count;
            }

            if (EnableDebugLogging)
            {
                Debug.Log($"[EventBus] Dispatching {eventType.Name} to {listenerCount} listener(s).");
            }

            try
            {
                for (int i = 0; i < listenerCount; i++)
                {
                    ListenerEntry entry;
                    lock (SyncRoot)
                    {
                        if (i >= channel.Listeners.Count)
                        {
                            break;
                        }
                        entry = channel.Listeners[i];
                    }

                    if (entry.IsMarkedForRemoval)
                    {
                        continue;
                    }

                    if (entry.Callback is Action<TEvent> typedCallback)
                    {
                        InvokeSingleSafe(typedCallback, gameEvent, eventType);
                    }
                }
            }
            finally
            {
                lock (SyncRoot)
                {
                    channel.ActiveDispatchDepth = Mathf.Max(0, channel.ActiveDispatchDepth - 1);

                    if (channel.ActiveDispatchDepth == 0)
                    {
                        FlushPendingChanges(channel);
                    }
                }
            }
        }

        /// <summary>
        /// Attempts to retrieve the last sticky event published for <typeparamref name="TEvent"/>.
        /// </summary>
        public static bool TryGetSticky<TEvent>(out TEvent gameEvent) where TEvent : IGameEvent
        {
            lock (SyncRoot)
            {
                if (StickyEvents.TryGetValue(typeof(TEvent), out IGameEvent stored) && stored is TEvent typed)
                {
                    gameEvent = typed;
                    return true;
                }
            }

            gameEvent = default;
            return false;
        }

        /// <summary>
        /// Clears any retained sticky event of type <typeparamref name="TEvent"/>.
        /// </summary>
        public static void ClearSticky<TEvent>() where TEvent : IGameEvent
        {
            lock (SyncRoot)
            {
                StickyEvents.Remove(typeof(TEvent));
            }
        }

        /// <summary>
        /// Returns the current number of active listeners for <typeparamref name="TEvent"/>.
        /// Useful for unit tests and profiling diagnostics.
        /// </summary>
        public static int GetListenerCount<TEvent>() where TEvent : IGameEvent
        {
            lock (SyncRoot)
            {
                if (!Channels.TryGetValue(typeof(TEvent), out Channel channel))
                {
                    return 0;
                }

                int count = 0;
                for (int i = 0; i < channel.Listeners.Count; i++)
                {
                    if (!channel.Listeners[i].IsMarkedForRemoval)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>
        /// Removes all subscribers and sticky events across all channels.
        /// Typically invoked on full game shutdown or automated test teardown.
        /// </summary>
        public static void ClearAll()
        {
            lock (SyncRoot)
            {
                Channels.Clear();
                StickyEvents.Clear();
                _nextOrderId = 0;
            }
        }

        private static Channel GetOrCreateChannel(Type eventType)
        {
            if (!Channels.TryGetValue(eventType, out Channel channel))
            {
                channel = new Channel();
                Channels.Add(eventType, channel);
            }
            return channel;
        }

        private static bool ContainsCallback(Channel channel, Delegate callback)
        {
            for (int i = 0; i < channel.Listeners.Count; i++)
            {
                if (!channel.Listeners[i].IsMarkedForRemoval && Equals(channel.Listeners[i].Callback, callback))
                {
                    return true;
                }
            }

            for (int i = 0; i < channel.PendingAdditions.Count; i++)
            {
                if (Equals(channel.PendingAdditions[i].Callback, callback))
                {
                    return true;
                }
            }

            return false;
        }

        private static void FlushPendingChanges(Channel channel)
        {
            if (channel.HasPendingRemovals)
            {
                for (int i = channel.Listeners.Count - 1; i >= 0; i--)
                {
                    if (channel.Listeners[i].IsMarkedForRemoval)
                    {
                        channel.Listeners.RemoveAt(i);
                    }
                }
                channel.HasPendingRemovals = false;
            }

            if (channel.PendingAdditions.Count > 0)
            {
                channel.Listeners.AddRange(channel.PendingAdditions);
                channel.PendingAdditions.Clear();
                channel.NeedsSort = true;
            }

            if (channel.NeedsSort)
            {
                SortListeners(channel);
                channel.NeedsSort = false;
            }
        }

        private static void SortListeners(Channel channel)
        {
            channel.Listeners.Sort((a, b) =>
            {
                int priorityComparison = b.Priority.CompareTo(a.Priority);
                if (priorityComparison != 0)
                {
                    return priorityComparison;
                }
                return a.RegistrationOrder.CompareTo(b.RegistrationOrder);
            });
        }

        private static void InvokeSingleSafe<TEvent>(Action<TEvent> callback, TEvent payload, Type eventType)
            where TEvent : IGameEvent
        {
            try
            {
                callback.Invoke(payload);
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[EventBus] Unhandled exception in listener '{callback.Method.DeclaringType?.Name}.{callback.Method.Name}' " +
                    $"while handling event '{eventType.Name}': {ex}");
            }
        }
    }

    // ========================================================================
    // CORE CROSS-MODULE EVENT DEFINITIONS
    // ========================================================================

    /// <summary>Published whenever the global game state changes.</summary>
    public readonly struct GameStateChangedEvent : IGameEvent
    {
        public readonly GameState PreviousState;
        public readonly GameState NewState;

        public GameStateChangedEvent(GameState previousState, GameState newState)
        {
            PreviousState = previousState;
            NewState = newState;
        }
    }

    /// <summary>Published when the game is paused or resumed.</summary>
    public readonly struct GamePauseToggledEvent : IGameEvent
    {
        public readonly bool IsPaused;

        public GamePauseToggledEvent(bool isPaused)
        {
            IsPaused = isPaused;
        }
    }

    /// <summary>Published during asynchronous scene transitions.</summary>
    public readonly struct SceneLoadProgressEvent : IGameEvent
    {
        public readonly string SceneName;
        public readonly float Progress01;
        public readonly bool IsCompleted;

        public SceneLoadProgressEvent(string sceneName, float progress01, bool isCompleted)
        {
            SceneName = sceneName;
            Progress01 = Mathf.Clamp01(progress01);
            IsCompleted = isCompleted;
        }
    }

    /// <summary>Published before and after save/load operations.</summary>
    public readonly struct SaveSystemEvent : IGameEvent
    {
        public readonly string SlotId;
        public readonly bool IsSaveOperation;
        public readonly bool Succeeded;
        public readonly string ErrorMessage;

        public SaveSystemEvent(string slotId, bool isSaveOperation, bool succeeded, string errorMessage = null)
        {
            SlotId = slotId;
            IsSaveOperation = isSaveOperation;
            Succeeded = succeeded;
            ErrorMessage = errorMessage;
        }
    }

    /// <summary>Published when the player's Wanted star rating changes (Module 5).</summary>
    public readonly struct WantedLevelChangedEvent : IGameEvent
    {
        public readonly int PreviousStars;
        public readonly int CurrentStars;
        public readonly Vector3 LastKnownPosition;

        public WantedLevelChangedEvent(int previousStars, int currentStars, Vector3 lastKnownPosition)
        {
            PreviousStars = previousStars;
            CurrentStars = Mathf.Clamp(currentStars, 0, 5);
            LastKnownPosition = lastKnownPosition;
        }
    }

    /// <summary>Published when the player enters or exits a vehicle (Module 4).</summary>
    public readonly struct PlayerVehicleTransitionEvent : IGameEvent
    {
        public readonly GameObject VehicleObject;
        public readonly bool EnteredVehicle;

        public PlayerVehicleTransitionEvent(GameObject vehicleObject, bool enteredVehicle)
        {
            VehicleObject = vehicleObject;
            EnteredVehicle = enteredVehicle;
        }
    }

    /// <summary>Published when world time-of-day or weather state changes (Module 2).</summary>
    public readonly struct WorldEnvironmentChangedEvent : IGameEvent
    {
        public readonly float NormalizedTimeOfDay;
        public readonly int DayCount;
        public readonly string WeatherId;
        public readonly float RainIntensity01;

        public WorldEnvironmentChangedEvent(float normalizedTimeOfDay, int dayCount, string weatherId, float rainIntensity01)
        {
            NormalizedTimeOfDay = Mathf.Repeat(normalizedTimeOfDay, 1f);
            DayCount = dayCount;
            WeatherId = weatherId;
            RainIntensity01 = Mathf.Clamp01(rainIntensity01);
        }
    }

    /// <summary>Published when the player's cash balance changes (Module 6).</summary>
    public readonly struct PlayerCurrencyChangedEvent : IGameEvent
    {
        public readonly long PreviousBalance;
        public readonly long CurrentBalance;
        public readonly long Delta;
        public readonly string Reason;

        public PlayerCurrencyChangedEvent(long previousBalance, long currentBalance, string reason)
        {
            PreviousBalance = previousBalance;
            CurrentBalance = currentBalance;
            Delta = currentBalance - previousBalance;
            Reason = reason;
        }
    }

    /// <summary>Published when a mission starts, updates an objective, completes, or fails (Module 6).</summary>
    public readonly struct MissionStatusChangedEvent : IGameEvent
    {
        public readonly string MissionId;
        public readonly string ObjectiveDescription;
        public readonly MissionLifecycleStatus Status;

        public MissionStatusChangedEvent(string missionId, string objectiveDescription, MissionLifecycleStatus status)
        {
            MissionId = missionId;
            ObjectiveDescription = objectiveDescription;
            Status = status;
        }
    }

    /// <summary>Shared mission lifecycle states referenced by Module 1 and Module 6.</summary>
    public enum MissionLifecycleStatus
    {
        Available,
        Started,
        ObjectiveUpdated,
        Completed,
        Failed
    }
}
