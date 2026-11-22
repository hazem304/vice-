// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Expansion — One-Click ScriptableObject Starter Content Generator
// File:    _Project/Scripts/Editor/ViceStarterContentEditor.cs
// Author:  Lead Game Architect
// Purpose: Unity Editor utility that automatically populates ready-to-play
//          WeaponDataSO assets (Pistol, SMG, Assault Rifle, Shotgun) and
//          MissionDataSO contracts (Neon Vault Heist, Ocean Chase, Delivery)
//          under Assets/_Project/ScriptableObjects/ so the Vertical Slice
//          runs immediately without manual asset entry.
// ============================================================================

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ViceCity.Missions;
using ViceCity.Player;

namespace ViceCity.EditorTools
{
    public static class ViceStarterContentEditor
    {
        private const string WeaponsFolder = "Assets/_Project/ScriptableObjects/Weapons";
        private const string MissionsFolder = "Assets/_Project/ScriptableObjects/Missions";

        [MenuItem("Vice Coast/Setup/3. Generate Starter Weapons & Missions", priority = 12)]
        public static void GenerateAllStarterAssets()
        {
            EnsureFolder(WeaponsFolder);
            EnsureFolder(MissionsFolder);

            CreateStarterWeapons();
            CreateStarterMissions();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Vice Coast Starter Content Created",
                "Generated 4 WeaponDataSO assets and 3 MissionDataSO contracts (Heist, Chase, Timed Delivery).",
                "OK");
        }

        private static void CreateStarterWeapons()
        {
            CreateOrUpdateWeapon("Weapon_Vice9mm", w =>
            {
                w.WeaponId = "pistol_vice9mm";
                w.DisplayName = "Vice 9mm";
                w.Category = WeaponCategory.Pistol;
                w.BaseDamage = 26f;
                w.FireIntervalSeconds = 0.22f;
                w.IsAutomatic = false;
                w.PelletsPerShot = 1;
                w.MaxRangeMeters = 85f;
                w.ClipCapacity = 15;
                w.DefaultReserveAmmo = 90;
                w.MaxReserveAmmo = 240;
                w.ReloadDurationSeconds = 1.4f;
                w.AdsFieldOfView = 44f;
                w.ShopPrice = 450;
            });

            CreateOrUpdateWeapon("Weapon_OceanSMG", w =>
            {
                w.WeaponId = "smg_ocean";
                w.DisplayName = "Ocean Drive SMG";
                w.Category = WeaponCategory.SMG;
                w.BaseDamage = 18f;
                w.FireIntervalSeconds = 0.09f;
                w.IsAutomatic = true;
                w.PelletsPerShot = 1;
                w.MaxRangeMeters = 70f;
                w.ClipCapacity = 32;
                w.DefaultReserveAmmo = 160;
                w.MaxReserveAmmo = 420;
                w.ReloadDurationSeconds = 1.8f;
                w.AdsFieldOfView = 42f;
                w.ShopPrice = 1600;
            });

            CreateOrUpdateWeapon("Weapon_MiamiCarbine", w =>
            {
                w.WeaponId = "rifle_carbine";
                w.DisplayName = "Miami Tactical Carbine";
                w.Category = WeaponCategory.AssaultRifle;
                w.BaseDamage = 31f;
                w.FireIntervalSeconds = 0.11f;
                w.IsAutomatic = true;
                w.PelletsPerShot = 1;
                w.MaxRangeMeters = 135f;
                w.ClipCapacity = 30;
                w.DefaultReserveAmmo = 150;
                w.MaxReserveAmmo = 360;
                w.ReloadDurationSeconds = 2.1f;
                w.AdsFieldOfView = 36f;
                w.ShopPrice = 3400;
            });

            CreateOrUpdateWeapon("Weapon_CoastalShotgun", w =>
            {
                w.WeaponId = "shotgun_coastal";
                w.DisplayName = "Coastal Pump Shotgun";
                w.Category = WeaponCategory.Shotgun;
                w.BaseDamage = 16f;
                w.FireIntervalSeconds = 0.85f;
                w.IsAutomatic = false;
                w.PelletsPerShot = 8;
                w.MaxRangeMeters = 32f;
                w.ClipCapacity = 6;
                w.DefaultReserveAmmo = 36;
                w.MaxReserveAmmo = 96;
                w.ReloadDurationSeconds = 2.4f;
                w.HipFireSpreadDegrees = 5.5f;
                w.AdsSpreadDegrees = 3.2f;
                w.AdsFieldOfView = 48f;
                w.ShopPrice = 2200;
            });
        }

        private static void CreateStarterMissions()
        {
            // 1. Heist / Robbery Mission
            CreateOrUpdateMission("Mission_01_NeonVaultHeist", m =>
            {
                m.MissionId = "mission_01_neon_vault";
                m.MissionTitle = "Neon Vault Heist";
                m.Category = MissionCategory.HeistRobbery;
                m.ContactGiverName = "Vance";
                m.BriefingText = "Hit the Art Deco Hotel safe on Ocean Drive, grab the cash, and lose the Vice Coast PD.";
                m.GlobalTimeLimitSeconds = 0f;
                m.CashReward = 12500;
                m.UnlockWeaponIdReward = "smg_ocean";
                m.Objectives = new List<MissionObjectiveData>
                {
                    new MissionObjectiveData
                    {
                        ObjectiveTitle = "Drive to the Ocean Drive Neon Hotel",
                        ActionType = ObjectiveActionType.ReachLocation,
                        TargetWorldPosition = new Vector3(80f, 0.5f, 0f),
                        CompletionRadiusMeters = 8f,
                        GpsRouteColor = new Color(1.0f, 0.16f, 0.62f, 1f)
                    },
                    new MissionObjectiveData
                    {
                        ObjectiveTitle = "Crack the Hotel Vault (Hold Position)",
                        ActionType = ObjectiveActionType.StealOrSecureItem,
                        TargetWorldPosition = new Vector3(80f, 0.5f, 14f),
                        CompletionRadiusMeters = 4f,
                        HoldOrInteractDurationSeconds = 5f,
                        TriggerWantedStarsOnStart = 3,
                        GpsRouteColor = new Color(1.0f, 0.72f, 0.12f, 1f)
                    },
                    new MissionObjectiveData
                    {
                        ObjectiveTitle = "Escape the 3-Star Police Pursuit",
                        ActionType = ObjectiveActionType.EscapePoliceWanted
                    },
                    new MissionObjectiveData
                    {
                        ObjectiveTitle = "Return to the Beachfront Safehouse",
                        ActionType = ObjectiveActionType.ReachLocation,
                        TargetWorldPosition = new Vector3(0f, 0.5f, 0f),
                        CompletionRadiusMeters = 7f,
                        GpsRouteColor = new Color(0.08f, 0.92f, 1.0f, 1f)
                    }
                };
            });

            // 2. Timed Coastal Supercar Delivery
            CreateOrUpdateMission("Mission_02_CoastalExpress", m =>
            {
                m.MissionId = "mission_02_coastal_express";
                m.MissionTitle = "Midnight Supercar Express";
                m.Category = MissionCategory.TimedDelivery;
                m.ContactGiverName = "Rico";
                m.BriefingText = "Deliver the Cheetah GT across Downtown Vice before the timer hits zero.";
                m.GlobalTimeLimitSeconds = 120f;
                m.CashReward = 6500;
                m.Objectives = new List<MissionObjectiveData>
                {
                    new MissionObjectiveData
                    {
                        ObjectiveTitle = "Reach Downtown Checkpoint (In Vehicle)",
                        ActionType = ObjectiveActionType.DeliverInVehicle,
                        TargetWorldPosition = new Vector3(160f, 0.5f, 80f),
                        CompletionRadiusMeters = 9f,
                        RequiresPlayerInVehicle = true,
                        GpsRouteColor = new Color(0.08f, 0.92f, 1.0f, 1f)
                    },
                    new MissionObjectiveData
                    {
                        ObjectiveTitle = "Deliver Supercar to Marina Pier",
                        ActionType = ObjectiveActionType.DeliverInVehicle,
                        TargetWorldPosition = new Vector3(160f, 0.5f, 160f),
                        CompletionRadiusMeters = 10f,
                        RequiresPlayerInVehicle = true,
                        GpsRouteColor = new Color(0.20f, 1.0f, 0.45f, 1f)
                    }
                };
            });

            // 3. High-Speed Chase Contract
            CreateOrUpdateMission("Mission_03_SyndicateChase", m =>
            {
                m.MissionId = "mission_03_syndicate_chase";
                m.MissionTitle = "Vice Boulevard Intercept";
                m.Category = MissionCategory.HighSpeedChase;
                m.ContactGiverName = "Elena";
                m.BriefingText = "Intercept the rival courier along Collins Avenue and neutralize the target.";
                m.GlobalTimeLimitSeconds = 180f;
                m.CashReward = 9000;
                m.UnlockWeaponIdReward = "rifle_carbine";
                m.Objectives = new List<MissionObjectiveData>
                {
                    new MissionObjectiveData
                    {
                        ObjectiveTitle = "Intercept Courier Convoy",
                        ActionType = ObjectiveActionType.ReachLocation,
                        TargetWorldPosition = new Vector3(80f, 0.5f, 160f),
                        CompletionRadiusMeters = 12f,
                        TriggerWantedStarsOnStart = 2,
                        GpsRouteColor = new Color(1.0f, 0.25f, 0.25f, 1f)
                    },
                    new MissionObjectiveData
                    {
                        ObjectiveTitle = "Lose the Police Patrols",
                        ActionType = ObjectiveActionType.EscapePoliceWanted
                    }
                };
            });
        }

        private static void CreateOrUpdateWeapon(string assetName, System.Action<WeaponDataSO> configure)
        {
            string path = $"{WeaponsFolder}/{assetName}.asset";
            WeaponDataSO asset = AssetDatabase.LoadAssetAtPath<WeaponDataSO>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<WeaponDataSO>();
                AssetDatabase.CreateAsset(asset, path);
            }

            configure(asset);
            EditorUtility.SetDirty(asset);
        }

        private static void CreateOrUpdateMission(string assetName, System.Action<MissionDataSO> configure)
        {
            string path = $"{MissionsFolder}/{assetName}.asset";
            MissionDataSO asset = AssetDatabase.LoadAssetAtPath<MissionDataSO>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<MissionDataSO>();
                AssetDatabase.CreateAsset(asset, path);
            }

            configure(asset);
            EditorUtility.SetDirty(asset);
        }

        private static void EnsureFolder(string relativePath)
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePath));
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
            }
        }
    }
}
#endif
