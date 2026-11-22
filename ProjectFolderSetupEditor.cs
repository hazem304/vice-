// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 1 — Core Architecture & Project Setup
// File:    _Project/Scripts/Editor/ProjectFolderSetupEditor.cs
// Author:  Lead Game Architect
// Purpose: Unity Editor utility that creates the complete modular _Project
//          directory hierarchy, configures required Tags & Physics Layers,
//          and generates .gitkeep placeholders for empty asset folders.
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ViceCity.EditorTools
{
    /// <summary>
    /// One-click project scaffolding tool accessible via:
    /// <c>Vice Coast > Setup > 1. Create Project Folder Structure</c> and
    /// <c>Vice Coast > Setup > 2. Configure Tags &amp; Layers</c>.
    /// </summary>
    public static class ProjectFolderSetupEditor
    {
        private const string RootAssetFolder = "Assets/_Project";

        private static readonly string[] RequiredFolders =
        {
            // Art & Visuals
            "Assets/_Project/Art/Materials/City",
            "Assets/_Project/Art/Materials/Vehicles",
            "Assets/_Project/Art/Materials/Characters",
            "Assets/_Project/Art/Materials/Neon",
            "Assets/_Project/Art/Models/Buildings",
            "Assets/_Project/Art/Models/CityChunks",
            "Assets/_Project/Art/Models/Vehicles",
            "Assets/_Project/Art/Models/Characters",
            "Assets/_Project/Art/Models/Props",
            "Assets/_Project/Art/Shaders/Water",
            "Assets/_Project/Art/Shaders/Weather",
            "Assets/_Project/Art/Shaders/Neon",
            "Assets/_Project/Art/Textures/Asphalt",
            "Assets/_Project/Art/Textures/ArtDeco",
            "Assets/_Project/Art/Textures/UI",
            "Assets/_Project/Art/Videos/Higgsfield",

            // Audio
            "Assets/_Project/Audio/SFX/Weapons",
            "Assets/_Project/Audio/SFX/Impacts",
            "Assets/_Project/Audio/SFX/UI",
            "Assets/_Project/Audio/Vehicles/Engines",
            "Assets/_Project/Audio/Vehicles/Tires",
            "Assets/_Project/Audio/Ambience/Ocean",
            "Assets/_Project/Audio/Ambience/City",
            "Assets/_Project/Audio/Music/RadioStations",

            // Prefabs
            "Assets/_Project/Prefabs/Player",
            "Assets/_Project/Prefabs/Vehicles/Sports",
            "Assets/_Project/Prefabs/Vehicles/Traffic",
            "Assets/_Project/Prefabs/Vehicles/Police",
            "Assets/_Project/Prefabs/NPCs/Pedestrians",
            "Assets/_Project/Prefabs/NPCs/Police",
            "Assets/_Project/Prefabs/CityChunks",
            "Assets/_Project/Prefabs/Weapons",
            "Assets/_Project/Prefabs/UI",

            // Scenes
            "Assets/_Project/Scenes",

            // ScriptableObjects
            "Assets/_Project/ScriptableObjects/Weapons",
            "Assets/_Project/ScriptableObjects/Vehicles",
            "Assets/_Project/ScriptableObjects/Missions",
            "Assets/_Project/ScriptableObjects/Weather",
            "Assets/_Project/ScriptableObjects/Shops",

            // Scripts (Modular Architecture across all 7 Modules)
            "Assets/_Project/Scripts/Editor",
            "Assets/_Project/Scripts/Core/Events",
            "Assets/_Project/Scripts/Core/Managers",
            "Assets/_Project/Scripts/Core/SaveSystem",
            "Assets/_Project/Scripts/World",
            "Assets/_Project/Scripts/Player",
            "Assets/_Project/Scripts/Vehicles",
            "Assets/_Project/Scripts/AI",
            "Assets/_Project/Scripts/Missions",
            "Assets/_Project/Scripts/Cinematics",
            "Assets/_Project/Scripts/UI"
        };

        private static readonly string[] RequiredTags =
        {
            "Vehicle",
            "VehicleDoor",
            "Pedestrian",
            "Police",
            "CoverSurface",
            "ClimbableLedge",
            "WaterVolume",
            "RoadSurface",
            "MissionTrigger",
            "ShopInteractable"
        };

        private static readonly Dictionary<int, string> RequiredLayers = new Dictionary<int, string>
        {
            { 6, "Ground" },
            { 7, "Player" },
            { 8, "Vehicle" },
            { 9, "NPC" },
            { 10, "Police" },
            { 11, "Cover" },
            { 12, "Interactable" },
            { 13, "MinimapIcon" },
            { 14, "Projectile" },
            { 15, "TrafficWaypoint" }
        };

        [MenuItem("Vice Coast/Setup/Run Full Module 1 Setup", priority = 0)]
        public static void RunFullModule1Setup()
        {
            CreateProjectFolders();
            ConfigureTagsAndLayers();
            EditorUtility.DisplayDialog(
                "Vice Coast Setup Complete",
                "Project folder hierarchy, Tags, and Physics Layers have been created and saved successfully.",
                "OK");
        }

        [MenuItem("Vice Coast/Setup/1. Create Project Folder Structure", priority = 10)]
        public static void CreateProjectFolders()
        {
            int createdCount = 0;

            try
            {
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

                for (int i = 0; i < RequiredFolders.Length; i++)
                {
                    string relativePath = RequiredFolders[i];
                    string absolutePath = Path.Combine(projectRoot, relativePath);

                    if (!Directory.Exists(absolutePath))
                    {
                        Directory.CreateDirectory(absolutePath);
                        createdCount++;
                    }

                    string gitKeepPath = Path.Combine(absolutePath, ".gitkeep");
                    if (!File.Exists(gitKeepPath) && Directory.GetFiles(absolutePath).Length == 0)
                    {
                        File.WriteAllText(gitKeepPath, string.Empty);
                    }
                }

                AssetDatabase.Refresh();
                Debug.Log($"[ProjectFolderSetupEditor] Verified {RequiredFolders.Length} folders under '{RootAssetFolder}' ({createdCount} newly created).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProjectFolderSetupEditor] Failed creating project folders: {ex}");
            }
        }

        [MenuItem("Vice Coast/Setup/2. Configure Tags & Layers", priority = 11)]
        public static void ConfigureTagsAndLayers()
        {
            try
            {
                UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
                if (assets == null || assets.Length == 0)
                {
                    Debug.LogError("[ProjectFolderSetupEditor] Could not load ProjectSettings/TagManager.asset.");
                    return;
                }

                SerializedObject tagManager = new SerializedObject(assets[0]);
                SerializedProperty tagsProp = tagManager.FindProperty("tags");
                SerializedProperty layersProp = tagManager.FindProperty("layers");

                int addedTags = 0;
                for (int i = 0; i < RequiredTags.Length; i++)
                {
                    if (AddTagIfMissing(tagsProp, RequiredTags[i]))
                    {
                        addedTags++;
                    }
                }

                int configuredLayers = 0;
                foreach (KeyValuePair<int, string> kvp in RequiredLayers)
                {
                    if (SetLayerIfEmptyOrMatching(layersProp, kvp.Key, kvp.Value))
                    {
                        configuredLayers++;
                    }
                }

                tagManager.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();

                Debug.Log($"[ProjectFolderSetupEditor] TagManager updated: {addedTags} tag(s) added, {configuredLayers} layer(s) verified.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProjectFolderSetupEditor] Failed configuring Tags & Layers: {ex}");
            }
        }

        private static bool AddTagIfMissing(SerializedProperty tagsProp, string tagName)
        {
            if (tagsProp == null || string.IsNullOrWhiteSpace(tagName))
            {
                return false;
            }

            for (int i = 0; i < tagsProp.arraySize; i++)
            {
                SerializedProperty entry = tagsProp.GetArrayElementAtIndex(i);
                if (string.Equals(entry.stringValue, tagName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            int insertIndex = tagsProp.arraySize;
            tagsProp.InsertArrayElementAtIndex(insertIndex);
            tagsProp.GetArrayElementAtIndex(insertIndex).stringValue = tagName;
            return true;
        }

        private static bool SetLayerIfEmptyOrMatching(SerializedProperty layersProp, int layerIndex, string layerName)
        {
            if (layersProp == null || layerIndex < 6 || layerIndex >= layersProp.arraySize)
            {
                return false;
            }

            SerializedProperty slot = layersProp.GetArrayElementAtIndex(layerIndex);
            if (string.IsNullOrEmpty(slot.stringValue))
            {
                slot.stringValue = layerName;
                return true;
            }

            if (string.Equals(slot.stringValue, layerName, StringComparison.Ordinal))
            {
                return true;
            }

            Debug.LogWarning(
                $"[ProjectFolderSetupEditor] Layer slot {layerIndex} is already occupied by '{slot.stringValue}'. " +
                $"Skipped assigning '{layerName}' to avoid overwriting custom project settings.");
            return false;
        }
    }
}
#endif
