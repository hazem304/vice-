// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 1 — Core Architecture & Project Setup
// File:    _Project/Scripts/Core/SaveSystem/SaveLoadSystem.cs
// Author:  Lead Game Architect
// Purpose: Production-grade JSON Save/Load system with multi-slot management,
//          atomic file writes, automatic .bak recovery, SHA-256 integrity
//          checksums, and ISaveable registry for all open-world modules.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using ViceCity.Core.Events;

namespace ViceCity.Core.SaveSystem
{
    /// <summary>
    /// Implemented by any gameplay system (Player, Economy, World, Missions, Vehicles)
    /// that persists state inside the master <see cref="GameSaveData"/> snapshot.
    /// </summary>
    public interface ISaveable
    {
        /// <summary>Unique identifier for diagnostics and deterministic save ordering.</summary>
        string SaveKey { get; }

        /// <summary>Writes the system's current runtime state into <paramref name="saveData"/>.</summary>
        void CaptureState(GameSaveData saveData);

        /// <summary>Restores the system's runtime state from <paramref name="saveData"/>.</summary>
        void RestoreState(GameSaveData saveData);
    }

    // ========================================================================
    // SERIALIZABLE SAVE DATA MODELS (Unity JsonUtility Compatible)
    // ========================================================================

    [Serializable]
    public sealed class SaveEnvelope
    {
        public int FormatVersion = SaveLoadSystem.CurrentFormatVersion;
        public string ChecksumSha256;
        public string PayloadJson;
    }

    [Serializable]
    public sealed class SaveSlotMetadata
    {
        public string SlotId;
        public string DisplayName;
        public string SavedUtcIso8601;
        public float TotalPlaytimeSeconds;
        public string CurrentDistrictName;
        public long PlayerCash;
        public int CompletedMissionCount;
        public bool IsCorrupted;
    }

    [Serializable]
    public sealed class GameSaveData
    {
        public int FormatVersion = SaveLoadSystem.CurrentFormatVersion;
        public string SlotId = "autosave";
        public string DisplayName = "Vice Coast Autosave";
        public string SavedUtcIso8601;
        public float TotalPlaytimeSeconds;

        public PlayerSaveData Player = new PlayerSaveData();
        public WorldSaveData World = new WorldSaveData();
        public EconomySaveData Economy = new EconomySaveData();
        public MissionSaveData Missions = new MissionSaveData();
        public List<SavedVehicleData> OwnedVehicles = new List<SavedVehicleData>();
    }

    [Serializable]
    public sealed class PlayerSaveData
    {
        public Vector3 Position = new Vector3(0f, 1f, 0f);
        public Quaternion Rotation = Quaternion.identity;
        public float Health = 100f;
        public float Armor = 0f;
        public float Stamina = 100f;
        public int WantedStars = 0;
        public string EquippedWeaponId = "Unarmed";
        public List<WeaponAmmoRecord> WeaponInventory = new List<WeaponAmmoRecord>();
    }

    [Serializable]
    public sealed class WeaponAmmoRecord
    {
        public string WeaponId;
        public int ClipAmmo;
        public int ReserveAmmo;
    }

    [Serializable]
    public sealed class WorldSaveData
    {
        public float NormalizedTimeOfDay = 0.35f; // Morning sunlight by default
        public int DayCount = 1;
        public string WeatherId = "ClearSunny";
        public float RainIntensity01 = 0f;
        public string CurrentDistrictName = "Ocean Beach";
    }

    [Serializable]
    public sealed class EconomySaveData
    {
        public long CashBalance = 2500;
        public List<string> UnlockedClothingIds = new List<string>();
        public List<string> OwnedPropertyIds = new List<string>();
    }

    [Serializable]
    public sealed class MissionSaveData
    {
        public string ActiveMissionId = string.Empty;
        public int ActiveObjectiveIndex = 0;
        public List<string> CompletedMissionIds = new List<string>();
    }

    [Serializable]
    public sealed class SavedVehicleData
    {
        public string VehicleId;
        public string GarageId;
        public Color PrimaryColor = Color.magenta;
        public Vector3 LastParkedPosition;
        public Quaternion LastParkedRotation = Quaternion.identity;
    }

    // ========================================================================
    // SAVE / LOAD SYSTEM SERVICE
    // ========================================================================

    /// <summary>
    /// Handles JSON serialization, SHA-256 integrity checks, atomic file writes,
    /// automatic backup fallback, and multi-slot management for Vice Coast.
    /// Registers itself as an <see cref="IGameService"/> on <see cref="GameManager"/>.
    /// </summary>
    public sealed class SaveLoadSystem : MonoBehaviour, IGameService
    {
        public const int CurrentFormatVersion = 1;
        public const string AutoSaveSlotId = "autosave";
        public const string QuickSaveSlotId = "quicksave";

        [Header("Storage Settings")]
        [SerializeField] private string saveFolderName = "ViceCoastSaves";
        [SerializeField] private bool prettyPrintJson = true;
        [SerializeField] private bool enableAutoSaveTimer = true;
        [SerializeField, Min(30f)] private float autoSaveIntervalSeconds = 300f;

        private readonly List<ISaveable> _saveables = new List<ISaveable>(16);
        private string _saveDirectoryPath;
        private float _autoSaveTimer;
        private GameSaveData _lastLoadedSnapshot;

        /// <summary>Absolute directory path where JSON save slots are stored.</summary>
        public string SaveDirectoryPath => _saveDirectoryPath;

        /// <summary>Most recently saved or loaded snapshot in memory.</summary>
        public GameSaveData LastLoadedSnapshot => _lastLoadedSnapshot;

        private void Awake()
        {
            EnsureSaveDirectoryInitialized();

            if (GameManager.HasInstance)
            {
                GameManager.Instance.RegisterService(this);
            }
        }

        private void Update()
        {
            if (!enableAutoSaveTimer || !GameManager.HasInstance)
            {
                return;
            }

            if (GameManager.Instance.CurrentState != GameState.Gameplay)
            {
                return;
            }

            _autoSaveTimer += Time.unscaledDeltaTime;
            if (_autoSaveTimer >= autoSaveIntervalSeconds)
            {
                _autoSaveTimer = 0f;
                SaveGame(AutoSaveSlotId, "Auto Save");
            }
        }

        private void OnDestroy()
        {
            if (GameManager.HasInstance)
            {
                GameManager.Instance.UnregisterService<SaveLoadSystem>();
            }
        }

        public void Initialize()
        {
            EnsureSaveDirectoryInitialized();
            Debug.Log($"[SaveLoadSystem] Initialized. Save directory: {_saveDirectoryPath}");
        }

        public void Shutdown()
        {
            _saveables.Clear();
        }

        // ====================================================================
        // ISAVEABLE REGISTRATION
        // ====================================================================

        /// <summary>Registers a module to participate in capture/restore snapshots.</summary>
        public void RegisterSaveable(ISaveable saveable)
        {
            if (saveable == null)
            {
                return;
            }

            if (!_saveables.Contains(saveable))
            {
                _saveables.Add(saveable);
            }
        }

        /// <summary>Unregisters a module from save/load snapshots.</summary>
        public void UnregisterSaveable(ISaveable saveable)
        {
            if (saveable == null)
            {
                return;
            }

            _saveables.Remove(saveable);
        }

        // ====================================================================
        // SAVE / LOAD OPERATIONS
        // ====================================================================

        /// <summary>
        /// Captures state from all registered <see cref="ISaveable"/> systems and writes
        /// an integrity-verified JSON file atomically with automatic backup rotation.
        /// </summary>
        public bool SaveGame(string slotId, string displayName = null)
        {
            string safeSlotId = SanitizeSlotId(slotId);
            EnsureSaveDirectoryInitialized();

            try
            {
                GameSaveData snapshot = new GameSaveData
                {
                    FormatVersion = CurrentFormatVersion,
                    SlotId = safeSlotId,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? safeSlotId : displayName.Trim(),
                    SavedUtcIso8601 = DateTime.UtcNow.ToString("O"),
                    TotalPlaytimeSeconds = GameManager.HasInstance ? GameManager.Instance.SessionPlaytimeSeconds : 0f
                };

                for (int i = 0; i < _saveables.Count; i++)
                {
                    ISaveable saveable = _saveables[i];
                    if (saveable == null)
                    {
                        continue;
                    }

                    try
                    {
                        saveable.CaptureState(snapshot);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[SaveLoadSystem] Error capturing state from '{saveable.SaveKey}': {ex}");
                    }
                }

                string payloadJson = JsonUtility.ToJson(snapshot, prettyPrintJson);
                SaveEnvelope envelope = new SaveEnvelope
                {
                    FormatVersion = CurrentFormatVersion,
                    ChecksumSha256 = ComputeSha256(payloadJson),
                    PayloadJson = payloadJson
                };

                string envelopeJson = JsonUtility.ToJson(envelope, prettyPrintJson);
                WriteAtomicWithBackup(safeSlotId, envelopeJson);

                _lastLoadedSnapshot = snapshot;
                EventBus.Publish(new SaveSystemEvent(safeSlotId, isSaveOperation: true, succeeded: true));
                Debug.Log($"[SaveLoadSystem] Successfully saved slot '{safeSlotId}'.");
                return true;
            }
            catch (Exception ex)
            {
                string error = $"Failed to save slot '{safeSlotId}': {ex.Message}";
                Debug.LogError($"[SaveLoadSystem] {error}\n{ex}");
                EventBus.Publish(new SaveSystemEvent(safeSlotId, isSaveOperation: true, succeeded: false, error));
                return false;
            }
        }

        /// <summary>
        /// Loads a save slot from disk, validates its SHA-256 checksum (automatically falling
        /// back to its .bak file if primary data is corrupted), and restores all registered systems.
        /// </summary>
        public bool LoadGame(string slotId)
        {
            string safeSlotId = SanitizeSlotId(slotId);
            EnsureSaveDirectoryInitialized();

            if (!TryReadValidatedSaveData(safeSlotId, out GameSaveData snapshot, out string errorMessage))
            {
                Debug.LogError($"[SaveLoadSystem] Load failed for slot '{safeSlotId}': {errorMessage}");
                EventBus.Publish(new SaveSystemEvent(safeSlotId, isSaveOperation: false, succeeded: false, errorMessage));
                return false;
            }

            for (int i = 0; i < _saveables.Count; i++)
            {
                ISaveable saveable = _saveables[i];
                if (saveable == null)
                {
                    continue;
                }

                try
                {
                    saveable.RestoreState(snapshot);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SaveLoadSystem] Error restoring state to '{saveable.SaveKey}': {ex}");
                }
            }

            _lastLoadedSnapshot = snapshot;
            EventBus.Publish(new SaveSystemEvent(safeSlotId, isSaveOperation: false, succeeded: true));
            Debug.Log($"[SaveLoadSystem] Successfully loaded slot '{safeSlotId}'.");
            return true;
        }

        /// <summary>Deletes both the primary save file and its backup for a given slot.</summary>
        public bool DeleteSlot(string slotId)
        {
            string safeSlotId = SanitizeSlotId(slotId);
            EnsureSaveDirectoryInitialized();

            bool deletedAny = false;
            string primaryPath = GetPrimaryFilePath(safeSlotId);
            string backupPath = GetBackupFilePath(safeSlotId);

            try
            {
                if (File.Exists(primaryPath))
                {
                    File.Delete(primaryPath);
                    deletedAny = true;
                }

                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                    deletedAny = true;
                }

                return deletedAny;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveLoadSystem] Failed to delete slot '{safeSlotId}': {ex}");
                return false;
            }
        }

        /// <summary>Returns true if a primary or backup file exists for the requested slot.</summary>
        public bool SlotExists(string slotId)
        {
            string safeSlotId = SanitizeSlotId(slotId);
            EnsureSaveDirectoryInitialized();
            return File.Exists(GetPrimaryFilePath(safeSlotId)) || File.Exists(GetBackupFilePath(safeSlotId));
        }

        /// <summary>
        /// Scans the save directory and returns metadata summaries for all available save slots,
        /// sorted newest first, for rendering in the Load Game / Smartphone UI.
        /// </summary>
        public List<SaveSlotMetadata> ListAllSlots()
        {
            EnsureSaveDirectoryInitialized();
            List<SaveSlotMetadata> results = new List<SaveSlotMetadata>();

            if (!Directory.Exists(_saveDirectoryPath))
            {
                return results;
            }

            string[] files = Directory.GetFiles(_saveDirectoryPath, "*.json", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string slotId = Path.GetFileNameWithoutExtension(files[i]);
                if (TryReadValidatedSaveData(slotId, out GameSaveData data, out _))
                {
                    results.Add(new SaveSlotMetadata
                    {
                        SlotId = data.SlotId,
                        DisplayName = data.DisplayName,
                        SavedUtcIso8601 = data.SavedUtcIso8601,
                        TotalPlaytimeSeconds = data.TotalPlaytimeSeconds,
                        CurrentDistrictName = data.World != null ? data.World.CurrentDistrictName : "Unknown",
                        PlayerCash = data.Economy != null ? data.Economy.CashBalance : 0,
                        CompletedMissionCount = data.Missions?.CompletedMissionIds?.Count ?? 0,
                        IsCorrupted = false
                    });
                }
                else
                {
                    results.Add(new SaveSlotMetadata
                    {
                        SlotId = slotId,
                        DisplayName = $"{slotId} (Corrupted)",
                        IsCorrupted = true
                    });
                }
            }

            results.Sort((a, b) => string.Compare(b.SavedUtcIso8601, a.SavedUtcIso8601, StringComparison.Ordinal));
            return results;
        }

        // ====================================================================
        // INTERNAL ATOMIC I/O & INTEGRITY HELPERS
        // ====================================================================

        private void EnsureSaveDirectoryInitialized()
        {
            if (!string.IsNullOrEmpty(_saveDirectoryPath))
            {
                if (!Directory.Exists(_saveDirectoryPath))
                {
                    Directory.CreateDirectory(_saveDirectoryPath);
                }
                return;
            }

            string folder = string.IsNullOrWhiteSpace(saveFolderName) ? "ViceCoastSaves" : saveFolderName.Trim();
            _saveDirectoryPath = Path.Combine(Application.persistentDataPath, folder);

            if (!Directory.Exists(_saveDirectoryPath))
            {
                Directory.CreateDirectory(_saveDirectoryPath);
            }
        }

        private void WriteAtomicWithBackup(string safeSlotId, string content)
        {
            string primaryPath = GetPrimaryFilePath(safeSlotId);
            string tempPath = primaryPath + ".tmp";
            string backupPath = GetBackupFilePath(safeSlotId);

            // 1. Write full payload to temporary file first
            File.WriteAllText(tempPath, content, Encoding.UTF8);

            // 2. Rotate existing primary save into .bak before replacing
            if (File.Exists(primaryPath))
            {
                File.Copy(primaryPath, backupPath, overwrite: true);
                File.Delete(primaryPath);
            }

            // 3. Atomically move temp file into primary slot
            File.Move(tempPath, primaryPath);
        }

        private bool TryReadValidatedSaveData(string safeSlotId, out GameSaveData saveData, out string error)
        {
            string primaryPath = GetPrimaryFilePath(safeSlotId);
            string backupPath = GetBackupFilePath(safeSlotId);

            if (TryParseAndValidateFile(primaryPath, out saveData, out error))
            {
                return true;
            }

            if (File.Exists(backupPath))
            {
                Debug.LogWarning($"[SaveLoadSystem] Primary save '{safeSlotId}' invalid ({error}). Attempting backup recovery...");
                if (TryParseAndValidateFile(backupPath, out saveData, out string backupError))
                {
                    Debug.Log($"[SaveLoadSystem] Recovered slot '{safeSlotId}' from backup file.");
                    error = null;
                    return true;
                }

                error = $"Primary failed ({error}); Backup failed ({backupError}).";
            }

            saveData = null;
            return false;
        }

        private static bool TryParseAndValidateFile(string path, out GameSaveData saveData, out string error)
        {
            saveData = null;

            if (!File.Exists(path))
            {
                error = "File does not exist.";
                return false;
            }

            try
            {
                string rawText = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    error = "File is empty.";
                    return false;
                }

                SaveEnvelope envelope = JsonUtility.FromJson<SaveEnvelope>(rawText);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.PayloadJson))
                {
                    error = "Malformed save envelope.";
                    return false;
                }

                string computedHash = ComputeSha256(envelope.PayloadJson);
                if (!string.Equals(computedHash, envelope.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    error = "SHA-256 checksum mismatch (possible tampering or disk corruption).";
                    return false;
                }

                saveData = JsonUtility.FromJson<GameSaveData>(envelope.PayloadJson);
                if (saveData == null)
                {
                    error = "Failed to deserialize GameSaveData payload.";
                    return false;
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private string GetPrimaryFilePath(string safeSlotId) => Path.Combine(_saveDirectoryPath, safeSlotId + ".json");
        private string GetBackupFilePath(string safeSlotId) => Path.Combine(_saveDirectoryPath, safeSlotId + ".json.bak");

        private static string SanitizeSlotId(string slotId)
        {
            if (string.IsNullOrWhiteSpace(slotId))
            {
                return AutoSaveSlotId;
            }

            StringBuilder sb = new StringBuilder(slotId.Length);
            for (int i = 0; i < slotId.Length; i++)
            {
                char c = slotId[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.Length > 0 ? sb.ToString() : AutoSaveSlotId;
        }

        private static string ComputeSha256(string input)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
