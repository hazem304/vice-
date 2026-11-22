// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Module 3 — Character Controller & Combat System
// File:    _Project/Scripts/Player/WeaponDataSO.cs
// Author:  Lead Game Architect
// Purpose: ScriptableObject weapon blueprint, IDamageable combat interface,
//          and WeaponFiredEvent so Module 5 (Pedestrian flee & Police Wanted)
//          reacts immediately to gunshots without tight coupling.
// ============================================================================

using UnityEngine;
using ViceCity.Core.Events;

namespace ViceCity.Player
{
    public enum WeaponCategory
    {
        Unarmed = 0,
        Pistol = 1,
        SMG = 2,
        AssaultRifle = 3,
        Shotgun = 4,
        Sniper = 5
    }

    /// <summary>
    /// Universal damage receiver implemented by NPCs, Police, Vehicles, and Props.
    /// </summary>
    public interface IDamageable
    {
        void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitNormal, GameObject instigator);
    }

    /// <summary>
    /// Broadcast through <see cref="EventBus"/> whenever a firearm is discharged.
    /// Module 5 (Pedestrians &amp; Police) listens to this event for panic and Wanted triggers.
    /// </summary>
    public readonly struct WeaponFiredEvent : IGameEvent
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;
        public readonly float AudibleRadius;
        public readonly string WeaponId;
        public readonly GameObject Shooter;

        public WeaponFiredEvent(Vector3 origin, Vector3 direction, float audibleRadius, string weaponId, GameObject shooter)
        {
            Origin = origin;
            Direction = direction;
            AudibleRadius = audibleRadius;
            WeaponId = weaponId;
            Shooter = shooter;
        }
    }

    /// <summary>
    /// Broadcast whenever the player equips a different weapon or ammo counts change,
    /// allowing HUD and Weapon Wheel UI to stay decoupled from combat logic.
    /// </summary>
    public readonly struct WeaponStateChangedEvent : IGameEvent
    {
        public readonly string WeaponId;
        public readonly string DisplayName;
        public readonly int ClipAmmo;
        public readonly int ClipCapacity;
        public readonly int ReserveAmmo;
        public readonly bool IsReloading;

        public WeaponStateChangedEvent(
            string weaponId,
            string displayName,
            int clipAmmo,
            int clipCapacity,
            int reserveAmmo,
            bool isReloading)
        {
            WeaponId = weaponId;
            DisplayName = displayName;
            ClipAmmo = clipAmmo;
            ClipCapacity = clipCapacity;
            ReserveAmmo = reserveAmmo;
            IsReloading = isReloading;
        }
    }

    /// <summary>
    /// Data-driven weapon specification asset created via:
    /// <c>Assets > Create > Vice Coast > Weapon Data</c>.
    /// </summary>
    [CreateAssetMenu(fileName = "Weapon_New", menuName = "Vice Coast/Weapon Data", order = 1)]
    public sealed class WeaponDataSO : ScriptableObject
    {
        [Header("Identity")]
        public string WeaponId = "pistol_9mm";
        public string DisplayName = "Vice 9mm";
        public WeaponCategory Category = WeaponCategory.Pistol;
        public Sprite WheelIcon;
        public GameObject WeaponModelPrefab;

        [Header("Ballistics & Damage")]
        [Min(1f)] public float BaseDamage = 24f;
        [Min(0.05f)] public float FireIntervalSeconds = 0.22f;
        public bool IsAutomatic = false;
        [Range(1, 12)] public int PelletsPerShot = 1;
        [Min(5f)] public float MaxRangeMeters = 90f;
        [Min(1f)] public float AudibleRadiusMeters = 55f;
        public bool IsSuppressed = false;

        [Header("Ammunition")]
        [Min(1)] public int ClipCapacity = 15;
        [Min(0)] public int DefaultReserveAmmo = 90;
        [Min(1)] public int MaxReserveAmmo = 240;
        [Min(0.2f)] public float ReloadDurationSeconds = 1.6f;

        [Header("ADS, Spread & Recoil")]
        [Range(20f, 65f)] public float AdsFieldOfView = 42f;
        [Min(0f)] public float HipFireSpreadDegrees = 2.4f;
        [Min(0f)] public float AdsSpreadDegrees = 0.55f;
        [Min(0f)] public float RecoilPitchKick = 1.8f;
        [Min(0f)] public float RecoilYawRandom = 0.65f;

        [Header("Economy & Audio")]
        [Min(0)] public int ShopPrice = 450;
        public AudioClip FireClip;
        public AudioClip ReloadClip;
        public AudioClip EmptyClickClip;
        public GameObject MuzzleFlashPrefab;
        public GameObject ImpactEffectPrefab;
    }
}
