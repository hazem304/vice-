// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 2 — Procedural City & World Streaming
// File:    _Project/Scripts/World/ChunkStreamingManager.cs
// Author:  Lead Game Architect
// Purpose: High-performance dynamic city chunk streaming around the player
//          (80m x 80m grid matching Blender 01_generate_vice_city_blocks.py).
//          Features frame-budgeted async loading (Addressables or Prefab/Resources
//          fallback), distance-based Occlusion/LOD activation, and 60 FPS throttling.
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using ViceCity.Core;
#if UNITY_ADDRESSABLES
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
#endif

namespace ViceCity.World
{
    /// <summary>
    /// Maps a grid coordinate to an optional pre-assigned prefab or Addressable key.
    /// </summary>
    [Serializable]
    public sealed class ChunkAssetEntry
    {
        public Vector2Int Coordinates;
        public string AddressableKey;
        public GameObject FallbackPrefab;
        public string DistrictName = "Ocean Beach";
    }

    /// <summary>
    /// Streams open-world city chunks dynamically around the player's transform while
    /// keeping frame times under 16.6ms (60 FPS) via strict per-frame load/unload budgets.
    /// </summary>
    public sealed class ChunkStreamingManager : MonoBehaviour, IGameService
    {
        private enum ChunkLoadState
        {
            Unloaded,
            Loading,
            Loaded
        }

        private sealed class RuntimeChunk
        {
            public Vector2Int Coordinates;
            public ChunkLoadState State;
            public GameObject Instance;
            public Renderer[] CachedRenderers;
            public float LastActiveTime;
            public bool IsVisible;
#if UNITY_ADDRESSABLES
            public AsyncOperationHandle<GameObject> AddressableHandle;
            public bool UsesAddressables;
#endif
        }

        [Header("Streaming Target & Grid")]
        [SerializeField] private Transform streamingTarget;
        [SerializeField, Min(10f)] private float chunkSizeMeters = 80f;
        [SerializeField, Range(1, 6)] private int loadRadiusChunks = 2;
        [SerializeField, Range(2, 8)] private int unloadRadiusChunks = 3;
        [SerializeField] private string addressableKeyPrefix = "ViceChunk_";
        [SerializeField] private string resourcesFallbackFolder = "CityChunks/";

        [Header("60 FPS Frame-Budget Throttling")]
        [SerializeField, Range(1, 4)] private int maxLoadsPerFrame = 1;
        [SerializeField, Range(1, 4)] private int maxUnloadsPerFrame = 2;
        [SerializeField, Min(0.05f)] private float streamingTickInterval = 0.25f;
        [SerializeField] private float unloadGracePeriodSeconds = 4.0f;

        [Header("Occlusion & Frustum Distance Culling")]
        [SerializeField] private bool enableDistanceOcclusion = true;
        [SerializeField] private float maxVisibleDistanceMeters = 240f;
        [SerializeField] private Camera cullingCamera;

        [Header("Chunk Registry & Default Fallback")]
        [SerializeField] private List<ChunkAssetEntry> registeredChunks = new List<ChunkAssetEntry>();
        [SerializeField] private GameObject genericFallbackChunkPrefab;
        [SerializeField] private Transform chunksContainerParent;

        private readonly Dictionary<Vector2Int, ChunkAssetEntry> _registryLookup = new Dictionary<Vector2Int, ChunkAssetEntry>();
        private readonly Dictionary<Vector2Int, RuntimeChunk> _activeChunks = new Dictionary<Vector2Int, RuntimeChunk>();
        private readonly Queue<Vector2Int> _pendingLoadQueue = new Queue<Vector2Int>();
        private readonly HashSet<Vector2Int> _queuedCoordinates = new HashSet<Vector2Int>();
        private readonly List<Vector2Int> _unloadScratchList = new List<Vector2Int>(16);

        private Vector2Int _currentPlayerChunk = new Vector2Int(int.MinValue, int.MinValue);
        private float _nextTickTime;
        private Plane[] _frustumPlanes = new Plane[6];

        /// <summary>Current 2D grid coordinates occupied by the streaming target.</summary>
        public Vector2Int CurrentPlayerChunk => _currentPlayerChunk;

        /// <summary>Number of currently instantiated chunks in memory.</summary>
        public int LoadedChunkCount => _activeChunks.Count;

        private void Awake()
        {
            BuildRegistryLookup();

            if (chunksContainerParent == null)
            {
                GameObject container = new GameObject("[StreamedCityChunks]");
                chunksContainerParent = container.transform;
            }

            if (cullingCamera == null)
            {
                cullingCamera = Camera.main;
            }

            if (GameManager.HasInstance)
            {
                GameManager.Instance.RegisterService(this);
            }
        }

        private void OnDestroy()
        {
            if (GameManager.HasInstance)
            {
                GameManager.Instance.UnregisterService<ChunkStreamingManager>();
            }
            UnloadAllChunksImmediate();
        }

        public void Initialize()
        {
            if (streamingTarget == null)
            {
                GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
                if (playerObj != null)
                {
                    streamingTarget = playerObj.transform;
                }
            }

            if (streamingTarget != null)
            {
                ForceEvaluateStreaming(streamingTarget.position);
            }
        }

        public void Shutdown()
        {
            UnloadAllChunksImmediate();
        }

        private void Update()
        {
            if (streamingTarget == null)
            {
                return;
            }

            if (Time.unscaledTime >= _nextTickTime)
            {
                _nextTickTime = Time.unscaledTime + streamingTickInterval;
                EvaluateStreamingGrid(streamingTarget.position);
            }

            ProcessLoadBudget();
            ProcessUnloadBudget();

            if (enableDistanceOcclusion)
            {
                UpdateChunkVisibility();
            }
        }

        /// <summary>Assigns or switches the transform tracked by the streaming grid (e.g., Player or Vehicle).</summary>
        public void SetStreamingTarget(Transform newTarget)
        {
            streamingTarget = newTarget;
            if (streamingTarget != null)
            {
                ForceEvaluateStreaming(streamingTarget.position);
            }
        }

        /// <summary>Converts any Unity world position to its corresponding 2D city chunk coordinate.</summary>
        public Vector2Int WorldToChunkCoords(Vector3 worldPosition)
        {
            int cx = Mathf.RoundToInt(worldPosition.x / chunkSizeMeters);
            int cz = Mathf.RoundToInt(worldPosition.z / chunkSizeMeters);
            return new Vector2Int(cx, cz);
        }

        /// <summary>Returns the center world position of a chunk coordinate.</summary>
        public Vector3 ChunkCoordsToWorldCenter(Vector2Int coords)
        {
            return new Vector3(coords.x * chunkSizeMeters, 0f, coords.y * chunkSizeMeters);
        }

        /// <summary>Returns the district name for the given world position.</summary>
        public string GetDistrictAtPosition(Vector3 worldPosition)
        {
            Vector2Int coords = WorldToChunkCoords(worldPosition);
            if (_registryLookup.TryGetValue(coords, out ChunkAssetEntry entry) && !string.IsNullOrWhiteSpace(entry.DistrictName))
            {
                return entry.DistrictName;
            }

            return coords.x <= 0 ? "Ocean Beach" : "Downtown Vice";
        }

        public void ForceEvaluateStreaming(Vector3 worldPosition)
        {
            _currentPlayerChunk = new Vector2Int(int.MinValue, int.MinValue);
            EvaluateStreamingGrid(worldPosition);
        }

        // ====================================================================
        // STREAMING GRID EVALUATION
        // ====================================================================

        private void EvaluateStreamingGrid(Vector3 targetPosition)
        {
            Vector2Int center = WorldToChunkCoords(targetPosition);
            if (center == _currentPlayerChunk)
            {
                return;
            }

            _currentPlayerChunk = center;

            // Queue chunks within load radius ordered by closest distance first
            List<Vector2Int> desired = new List<Vector2Int>((loadRadiusChunks * 2 + 1) * (loadRadiusChunks * 2 + 1));
            for (int dx = -loadRadiusChunks; dx <= loadRadiusChunks; dx++)
            {
                for (int dz = -loadRadiusChunks; dz <= loadRadiusChunks; dz++)
                {
                    desired.Add(new Vector2Int(center.x + dx, center.y + dz));
                }
            }

            desired.Sort((a, b) =>
            {
                int distA = (a - center).sqrMagnitude;
                int distB = (b - center).sqrMagnitude;
                return distA.CompareTo(distB);
            });

            for (int i = 0; i < desired.Count; i++)
            {
                Vector2Int coord = desired[i];
                if (_activeChunks.TryGetValue(coord, out RuntimeChunk existing))
                {
                    existing.LastActiveTime = Time.unscaledTime;
                    continue;
                }

                if (_queuedCoordinates.Add(coord))
                {
                    _pendingLoadQueue.Enqueue(coord);
                }
            }
        }

        private void ProcessLoadBudget()
        {
            int loadedThisFrame = 0;
            while (loadedThisFrame < maxLoadsPerFrame && _pendingLoadQueue.Count > 0)
            {
                Vector2Int coord = _pendingLoadQueue.Dequeue();
                _queuedCoordinates.Remove(coord);

                if (_activeChunks.ContainsKey(coord))
                {
                    continue;
                }

                // Skip if player already moved far away before this chunk reached front of queue
                if (ChebyshevDistance(coord, _currentPlayerChunk) > unloadRadiusChunks)
                {
                    continue;
                }

                BeginChunkLoad(coord);
                loadedThisFrame++;
            }
        }

        private void ProcessUnloadBudget()
        {
            _unloadScratchList.Clear();
            float now = Time.unscaledTime;

            foreach (KeyValuePair<Vector2Int, RuntimeChunk> kvp in _activeChunks)
            {
                int dist = ChebyshevDistance(kvp.Key, _currentPlayerChunk);
                if (dist > unloadRadiusChunks && (now - kvp.Value.LastActiveTime) >= unloadGracePeriodSeconds)
                {
                    _unloadScratchList.Add(kvp.Key);
                }
            }

            int unloadedThisFrame = 0;
            for (int i = 0; i < _unloadScratchList.Count && unloadedThisFrame < maxUnloadsPerFrame; i++)
            {
                UnloadChunk(_unloadScratchList[i]);
                unloadedThisFrame++;
            }
        }

        private void BeginChunkLoad(Vector2Int coord)
        {
            RuntimeChunk chunk = new RuntimeChunk
            {
                Coordinates = coord,
                State = ChunkLoadState.Loading,
                LastActiveTime = Time.unscaledTime,
                IsVisible = true
            };
            _activeChunks[coord] = chunk;

            _registryLookup.TryGetValue(coord, out ChunkAssetEntry entry);
            string key = (entry != null && !string.IsNullOrWhiteSpace(entry.AddressableKey))
                ? entry.AddressableKey
                : $"{addressableKeyPrefix}{coord.x}_{coord.y}";

#if UNITY_ADDRESSABLES
            try
            {
                AsyncOperationHandle<GameObject> handle = Addressables.InstantiateAsync(
                    key,
                    ChunkCoordsToWorldCenter(coord),
                    Quaternion.identity,
                    chunksContainerParent);

                chunk.AddressableHandle = handle;
                chunk.UsesAddressables = true;
                handle.Completed += op => OnAddressableChunkLoaded(coord, op);
                return;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChunkStreamingManager] Addressables load failed for '{key}', falling back to Prefab/Resources: {ex.Message}");
            }
#endif

            // Synchronous fallback via registered prefab, Resources folder, or generic placeholder block
            GameObject prefab = entry != null ? entry.FallbackPrefab : null;
            if (prefab == null)
            {
                prefab = Resources.Load<GameObject>(resourcesFallbackFolder + key);
            }
            if (prefab == null)
            {
                prefab = genericFallbackChunkPrefab;
            }

            if (prefab != null)
            {
                GameObject instance = Instantiate(prefab, ChunkCoordsToWorldCenter(coord), Quaternion.identity, chunksContainerParent);
                instance.name = $"Chunk_{coord.x}_{coord.y}";
                FinalizeLoadedChunk(chunk, instance);
            }
            else
            {
                // Chunk coordinates outside authored bounds stay cleanly unloaded without errors
                _activeChunks.Remove(coord);
            }
        }

#if UNITY_ADDRESSABLES
        private void OnAddressableChunkLoaded(Vector2Int coord, AsyncOperationHandle<GameObject> handle)
        {
            if (!_activeChunks.TryGetValue(coord, out RuntimeChunk chunk))
            {
                if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
                {
                    Addressables.ReleaseInstance(handle);
                }
                return;
            }

            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
            {
                FinalizeLoadedChunk(chunk, handle.Result);
            }
            else
            {
                Debug.LogWarning($"[ChunkStreamingManager] Addressable chunk '{coord}' not found in catalog.");
                _activeChunks.Remove(coord);
            }
        }
#endif

        private static void FinalizeLoadedChunk(RuntimeChunk chunk, GameObject instance)
        {
            chunk.Instance = instance;
            chunk.CachedRenderers = instance.GetComponentsInChildren<Renderer>(includeInactive: true);
            chunk.State = ChunkLoadState.Loaded;
            chunk.IsVisible = true;
        }

        private void UnloadChunk(Vector2Int coord)
        {
            if (!_activeChunks.TryGetValue(coord, out RuntimeChunk chunk))
            {
                return;
            }

            _activeChunks.Remove(coord);

#if UNITY_ADDRESSABLES
            if (chunk.UsesAddressables)
            {
                if (chunk.Instance != null)
                {
                    Addressables.ReleaseInstance(chunk.Instance);
                }
                else if (chunk.AddressableHandle.IsValid())
                {
                    Addressables.Release(chunk.AddressableHandle);
                }
                return;
            }
#endif

            if (chunk.Instance != null)
            {
                Destroy(chunk.Instance);
            }
        }

        private void UnloadAllChunksImmediate()
        {
            List<Vector2Int> keys = new List<Vector2Int>(_activeChunks.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                UnloadChunk(keys[i]);
            }

            _pendingLoadQueue.Clear();
            _queuedCoordinates.Clear();
        }

        // ====================================================================
        // DISTANCE & FRUSTUM OCCLUSION CULLING
        // ====================================================================

        private void UpdateChunkVisibility()
        {
            if (cullingCamera == null)
            {
                cullingCamera = Camera.main;
                if (cullingCamera == null)
                {
                    return;
                }
            }

            GeometryUtility.CalculateFrustumPlanes(cullingCamera, _frustumPlanes);
            Vector3 camPos = cullingCamera.transform.position;
            float maxDistSqr = maxVisibleDistanceMeters * maxVisibleDistanceMeters;
            Vector3 chunkBoundsSize = new Vector3(chunkSizeMeters, 120f, chunkSizeMeters);

            foreach (KeyValuePair<Vector2Int, RuntimeChunk> kvp in _activeChunks)
            {
                RuntimeChunk chunk = kvp.Value;
                if (chunk.State != ChunkLoadState.Loaded || chunk.Instance == null)
                {
                    continue;
                }

                Vector3 center = ChunkCoordsToWorldCenter(chunk.Coordinates) + new Vector3(0f, 35f, 0f);
                bool withinDistance = (center - camPos).sqrMagnitude <= maxDistSqr;
                bool inFrustum = withinDistance && GeometryUtility.TestPlanesAABB(_frustumPlanes, new Bounds(center, chunkBoundsSize));

                if (chunk.IsVisible != inFrustum)
                {
                    chunk.IsVisible = inFrustum;
                    SetChunkRenderersEnabled(chunk, inFrustum);
                }
            }
        }

        private static void SetChunkRenderersEnabled(RuntimeChunk chunk, bool enabled)
        {
            if (chunk.CachedRenderers == null)
            {
                return;
            }

            for (int i = 0; i < chunk.CachedRenderers.Length; i++)
            {
                if (chunk.CachedRenderers[i] != null)
                {
                    chunk.CachedRenderers[i].enabled = enabled;
                }
            }
        }

        private void BuildRegistryLookup()
        {
            _registryLookup.Clear();
            for (int i = 0; i < registeredChunks.Count; i++)
            {
                ChunkAssetEntry entry = registeredChunks[i];
                if (entry != null)
                {
                    _registryLookup[entry.Coordinates] = entry;
                }
            }
        }

        private static int ChebyshevDistance(Vector2Int a, Vector2Int b)
        {
            return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }
    }
}
