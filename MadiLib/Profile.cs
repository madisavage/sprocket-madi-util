extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MonoBehaviour = UnityCoreModule::UnityEngine.MonoBehaviour;
using Time = UnityCoreModule::UnityEngine.Time;

namespace MadiLib
{
    /// <summary>
    /// Times plugin code while the MadiLib <c>Profile</c> setting is on, and
    /// every 5 seconds logs each timed section's milliseconds and calls per
    /// second, slowest first. Times include anything a section calls,
    /// timed sections and the game's own code alike.
    /// </summary>
    /// <example><code>using var _ = Profile.Measure("stagedAmmo.Update");</code></example>
    public static class Profile
    {
        private const float Interval = 5f;

        // Section name, its ticks and calls since the last report
        private static readonly Dictionary<string, (long Ticks, int Calls)> sections = new();
        private static float since;
        private static int frames;

        /// <summary>Times from here until the returned value is disposed, under <paramref name="name"/>; nothing while profiling is off.</summary>
        public static Section Measure(string name) => new(Plugin.Profiling.Value ? name : null);

        /// <summary>A timed section, ended by disposing it.</summary>
        public readonly struct Section : IDisposable
        {
            private readonly string? name;
            private readonly long start;

            internal Section(string? name)
            {
                this.name = name;
                start = name == null ? 0 : Stopwatch.GetTimestamp();
            }

            /// <summary>Adds the time since this section started to its total.</summary>
            public void Dispose()
            {
                if (name == null)
                    return;
                long ticks = Stopwatch.GetTimestamp() - start;
                sections.TryGetValue(name, out var total);
                sections[name] = (total.Ticks + ticks, total.Calls + 1);
            }
        }

        /// <summary>Called every frame by <see cref="ProfileReporter"/>; logs and clears the totals every <see cref="Interval"/> seconds.</summary>
        internal static void Tick()
        {
            if (!Plugin.Profiling.Value)
            {
                sections.Clear();
                since = 0f;
                frames = 0;
                return;
            }
            since += Time.unscaledDeltaTime;
            frames++;
            if (since < Interval)
                return;
            var report = new System.Text.StringBuilder($"Profile over {since:0.0} s, {frames / since:0} fps ({since * 1000f / frames:0.00} ms a frame):");
            foreach (var (name, (ticks, calls)) in sections.OrderByDescending(s => s.Value.Ticks))
                report.Append($"\n  {ticks * 1000.0 / Stopwatch.Frequency / since,8:0.000} ms/s  {ticks * 1000.0 / Stopwatch.Frequency / frames,7:0.000} ms/frame  {calls / since,8:0} calls/s  {name}");
            Plugin.Logger.LogInfo(report.ToString());
            sections.Clear();
            since = 0f;
            frames = 0;
        }
    }

    /// <summary>Component added to the plugin's object to call <see cref="Profile.Tick"/> every frame.</summary>
    internal sealed class ProfileReporter : MonoBehaviour
    {
        /// <summary>Wraps an existing native instance; used by Il2CppInterop.</summary>
        public ProfileReporter(IntPtr ptr) : base(ptr) { }

        private void Update() => Profile.Tick();
    }
}
