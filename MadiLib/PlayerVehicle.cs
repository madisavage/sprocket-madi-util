extern alias UnityCoreModule;

using Sprocket.Gameplay.VehicleControl;
using Component = UnityCoreModule::UnityEngine.Component;
using Object = UnityCoreModule::UnityEngine.Object;
using Time = UnityCoreModule::UnityEngine.Time;
using Transform = UnityCoreModule::UnityEngine.Transform;

namespace MadiLib
{
    /// <summary>The vehicle the player is controlling.</summary>
    public static class PlayerVehicle
    {
        private static Transform? root;
        private static float nextLookup;

        /// <summary>
        /// The player's vehicle, or null outside vehicle control. Looked up at
        /// most once a second, so it can lag a vehicle switch by that long.
        /// </summary>
        public static Transform? Root
        {
            get
            {
                if (Time.unscaledTime >= nextLookup)
                {
                    nextLookup = Time.unscaledTime + 1f;
                    root = Object.FindObjectOfType<VehicleController>()?.ControlledVehicleGameObject?.transform;
                }
                return root;
            }
        }

        /// <summary>Whether <paramref name="part"/> belongs to the player's vehicle.</summary>
        public static bool Contains(Component? part)
        {
            Transform? player = Root;
            return part != null && player != null && part.transform.IsChildOf(player);
        }
    }
}
