extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using HarmonyLib;
using MadiLib;
using Sprocket.Vehicles.Cannons;
using Sprocket.WeaponFramework;
using Quaternion = UnityCoreModule::UnityEngine.Quaternion;

namespace MadiUtil
{
    /// <summary>
    /// A cannon's breech settings, in its panel: the breech's width, height and length as
    /// shares of the game's (its lost mass comes off the gun), a recoil multiplier on the
    /// muzzle's recoil coefficient (0: firing doesn't push the vehicle, as a recoilless gun),
    /// and a backblast flash behind the gun. Kept in the cannon's description as
    /// <c>"breech"</c> (see <see cref="Parse"/>). The recoil coefficient is the muzzle part's,
    /// which the game sets again on its own, so <see cref="Apply"/> runs wherever it might have.
    /// </summary>
    internal static class Breech
    {
        /// <summary>Breech width, height and length as shares of the game's, recoil multiplier, backblast on.</summary>
        internal readonly record struct Settings(float X, float Y, float Z, float Recoil, bool Backblast);

        /// <summary>The game's own breech: full size, full recoil, no backblast.</summary>
        internal static readonly Settings Default = new(1f, 1f, 1f, 1f, false);

        /// <summary>Smallest breech scale a slider allows.</summary>
        internal const float MinScale = 0.01f;

        // Cannon blueprint, the muzzle part's own recoil coefficient, and the value last written over it
        private static readonly Dictionary<IntPtr, (float Recoil, float Written)> muzzles = new();

        /// <summary><paramref name="gun"/>'s breech settings.</summary>
        internal static Settings Of(CannonBlueprint gun) => Parse(Descriptions.Parsed(gun));

        /// <summary>The settings in description <paramref name="text"/>; see <see cref="Parse(JsonObject?)"/>.</summary>
        internal static Settings Parse(string text) => Parse(Descriptions.Parse(text));

        /// <summary>
        /// The settings in a description's <c>"breech"</c>: <c>{"scale": [x, y, z], "recoil": r, "backblast": bool}</c>,
        /// missing parts at the game's. Reads the <c>"recoilless"</c> of an earlier build (no recoil, its scale, backblast).
        /// </summary>
        private static Settings Parse(JsonObject? json)
        {
            if (json == null)
                return Default;
            if (json["breech"] is JsonObject breech)
                return Read(breech["scale"], Number(breech["recoil"], Default.Recoil, 0f, 1f), Flag(breech["backblast"], Default.Backblast));
            return json["recoilless"] switch
            {
                JsonValue on when on.TryGetValue(out bool yes) && yes => new Settings(0.4f, 0.4f, 1f, 0f, true),
                JsonObject old => Read(old["breech"], 0f, Flag(old["backblast"], true)),
                _ => Default,
            };
        }

        private static Settings Read(JsonNode? scale, float recoil, bool backblast)
        {
            float At(int i) => scale is JsonArray array && array.Count > i ? Number(array[i], 1f, MinScale, 1f) : 1f;
            return new Settings(At(0), At(1), At(2), recoil, backblast);
        }

        private static float Number(JsonNode? node, float fallback, float min, float max) =>
            node is JsonValue value && value.TryGetValue(out float f) ? Math.Clamp(f, min, max) : fallback;

        private static bool Flag(JsonNode? node, bool fallback) => node is JsonValue value && value.TryGetValue(out bool b) ? b : fallback;

        /// <summary>The <c>"breech"</c> value for <paramref name="settings"/>; null for the game's own.</summary>
        internal static JsonNode? ToJson(Settings settings) => settings == Default ? null
            : new JsonObject { ["scale"] = new JsonArray(settings.X, settings.Y, settings.Z), ["recoil"] = settings.Recoil, ["backblast"] = settings.Backblast };

        /// <summary>Sets <paramref name="gun"/>'s breech settings, kept in its description, and applies them.</summary>
        internal static void Set(CannonBlueprint gun, Settings settings)
        {
            string? text = Descriptions.With(gun.Description, "breech", ToJson(settings));
            if (text != null)
                text = Descriptions.With(text, "recoilless", null);
            if (text != null)
                gun.Description = text;
            else
                Plugin.Logger.LogWarning($"Cannon {gun.Name}'s description isn't JSON, so its breech settings can't be kept.");
            Apply(gun);
        }

        /// <summary>
        /// Writes the muzzle part's recoil coefficient times the recoil multiplier over it, if the
        /// game set it again or the multiplier changed; true if it changed.
        /// </summary>
        internal static bool Apply(CannonBlueprint gun)
        {
            float multiplier = Of(gun).Recoil, recoil = gun.MuzzleRecoilCoefficient;
            if (!muzzles.TryGetValue(gun.Pointer, out var muzzle))
            {
                if (multiplier == 1f)
                    return false;
                muzzle = (recoil, float.NaN);
            }
            // A value other than ours was set by the game: the muzzle part's own
            if (recoil != muzzle.Written)
                muzzle.Recoil = recoil;
            float target = muzzle.Recoil * multiplier;
            muzzles[gun.Pointer] = (muzzle.Recoil, target);
            if (target == recoil)
                return false;
            gun.MuzzleRecoilCoefficient = target;
            gun.physicalPropertiesDirty = true;
            return true;
        }

        private static bool loggedBreech;

        /// <summary>
        /// Scales the breech by its settings and takes the breech mass it loses off the gun. Run
        /// after the game works out the breech from the blueprint, so the model is built from the
        /// scaled one. Found in game: a breech a few mm thick, or a short one (the barrel starts at
        /// its front, so it's pulled back into the mantlet), stops the gun elevating.
        /// </summary>
        internal static void Scale(CannonBlueprint gun)
        {
            Settings settings = Of(gun);
            if (settings.X == 1f && settings.Y == 1f && settings.Z == 1f)
                return;
            var size = gun.breechSize;
            float before = gun.BreechMass;
            var scaled = new UnityCoreModule::UnityEngine.Vector3(size.x * settings.X, size.y * settings.Y, size.z * settings.Z);
            gun.breechSize = scaled;
            float after = gun.BreechMass;
            // BreechMass may follow the size or be worked out once; either way the gun loses what the breech lost, or the volume it lost
            float lost = after < before ? before - after : before * (1f - settings.X * settings.Y * settings.Z);
            gun.mass = MathF.Max(gun.mass - lost, 0f);
            if (!loggedBreech)
                Plugin.Logger.LogInfo($"Breech: size {size.x:0.###} x {size.y:0.###} x {size.z:0.###} to {scaled.x:0.###} x {scaled.y:0.###} x {scaled.z:0.###}, breech mass {before:0.#} to {after:0.#} kg, gun loses {lost:0.#} kg");
            loggedBreech = true;
        }
    }

    /// <summary>The last muzzle flash the game set up, whose info a backblast copies, so it's sized as the game sizes the gun's.</summary>
    [HarmonyPatch(typeof(Sprocket.Vehicles.Fires.MuzzleFlashEffect), nameof(Sprocket.Vehicles.Fires.MuzzleFlashEffect.Setup))]
    internal static class MuzzleFlashSetupPatch
    {
        internal static CannonMuzzleFlashEffectInfo? Last;
        internal static int Frame = -1;

        private static void Prefix(CannonMuzzleFlashEffectInfo __0)
        {
            Last = __0;
            Frame = UnityCoreModule::UnityEngine.Time.frameCount;
        }
    }

    /// <summary>A gun's backblast, when its breech has one: its muzzle flash again at the breech, facing backward.</summary>
    [HarmonyPatch(typeof(Cannon), nameof(Cannon.OnFire))]
    internal static class BackblastFirePatch
    {
        private static bool failed, logged;

        private static void Postfix(Cannon __instance)
        {
            try
            {
                if (__instance.Blueprint is not { } gun || !Breech.Of(gun).Backblast || __instance.behaviour is not { } behaviour
                    || behaviour.muzzleFlashEffectFactory is not { } flash || behaviour.transform is not { } barrel)
                    return;
                // This shot's own flash if the game set it up this frame; else built here, units unchecked
                bool copied = MuzzleFlashSetupPatch.Last is { } last && MuzzleFlashSetupPatch.Frame == UnityCoreModule::UnityEngine.Time.frameCount;
                var info = copied ? MuzzleFlashSetupPatch.Last!.Value
                    : new CannonMuzzleFlashEffectInfo { Calibre = gun.Caliber, BoreLength = gun.BoreLength, PropellantVolume = behaviour.PropellantVolume };
                if (!logged)
                    Plugin.Logger.LogInfo($"Backblast: {(copied ? "copies the muzzle flash" : "flash built here")}: calibre {info.Calibre}, bore {info.BoreLength}, propellant {info.PropellantVolume}");
                logged = true;
                var at = __instance.breechModel != null ? __instance.breechModel.LoadPosition : barrel.position;
                flash.PlayEffect(info, barrel, at, Quaternion.LookRotation(-barrel.forward, barrel.up));
            }
            catch (Exception e)
            {
                // The shot still fires; only the backblast is lost
                if (!failed)
                    Plugin.Logger.LogError($"Backblast failed: {e}");
                failed = true;
            }
        }
    }
}
