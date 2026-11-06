using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Rounds
{
    /// <summary>
    /// Which rounds one rack's shells are. The game only knows a rack's count,
    /// so this tracks a count per round and keeps it summing to that count.
    /// </summary>
    /// <remarks>
    /// Designed in the rack blueprint's description as
    /// <c>{"split": {id: count}}</c>: the slots assigned to each round. The rest
    /// are unassigned, and hold an even balance of all rounds. Without a split,
    /// <c>{"round": id}</c> assigns the whole rack to that round, as older
    /// versions read it. No game types, so tests can run it.
    /// </remarks>
    internal sealed class RackMix
    {
        /// <summary>The slots assigned to each of the gun's rounds, in the cannon panel's order.</summary>
        public readonly Dictionary<string, int> Targets;

        /// <summary>The slots assigned to no round.</summary>
        public readonly int Unassigned;

        /// <summary>The shells per round in the rack now. Live: it changes with <see cref="Sync"/>.</summary>
        public readonly Dictionary<string, int> Counts;

        private RackMix(Dictionary<string, int> targets, int unassigned)
        {
            Targets = targets;
            Unassigned = unassigned;
            Counts = new Dictionary<string, int>(targets);
            Sync(targets.Values.Sum() + unassigned);
        }

        /// <summary>The round <paramref name="rack"/> was given among <paramref name="ids"/>: its pick, or the first; null if there are none.</summary>
        public static string? FillOf(JsonObject rack, IReadOnlyList<string> ids)
        {
            string? pick = (string?)(rack["round"] as JsonValue);
            return pick != null && ids.Contains(pick) ? pick : ids.FirstOrDefault();
        }

        /// <summary>
        /// The mix designed in <paramref name="rack"/>, for a rack of
        /// <paramref name="capacity"/> full; null without rounds. Split ids not
        /// among <paramref name="ids"/> drop out, and counts past the capacity
        /// are cut, earlier ones first served.
        /// </summary>
        public static RackMix? Read(JsonObject rack, IReadOnlyList<string> ids, int capacity)
        {
            if (FillOf(rack, ids) is not { } fill)
                return null;
            var targets = ids.ToDictionary(id => id, _ => 0);
            int left = Math.Max(capacity, 0);
            if (rack["split"] is JsonObject split)
            {
                foreach (var (id, value) in split)
                    if (targets.ContainsKey(id) && value is JsonValue number && number.TryGetValue(out int count) && count > 0)
                    {
                        targets[id] = Math.Min(count, left);
                        left -= targets[id];
                    }
            }
            else
            {
                targets[fill] = left;
                left = 0;
            }
            return new RackMix(targets, left);
        }

        /// <summary>The most slots <paramref name="id"/> can be assigned: its own and the unassigned.</summary>
        public int Limit(string id) => Targets.GetValueOrDefault(id) + Unassigned;

        /// <summary>Shells of <paramref name="id"/> in the rack.</summary>
        public int Count(string id) => Counts.GetValueOrDefault(id);

        // Shells of the round beyond its assigned slots; negative when it's short of them
        private int Excess(string id) => Count(id) - Targets.GetValueOrDefault(id);

        // Unassigned slots no shell is in
        private int Free => Unassigned - Counts.Keys.Sum(id => Math.Max(Excess(id), 0));

        /// <summary>
        /// The round a shell leaving the rack is: <paramref name="prefer"/> if
        /// it has one, else the one most beyond its assigned slots.
        /// </summary>
        public string Pick(string? prefer)
        {
            if (prefer != null && Count(prefer) > 0)
                return prefer;
            return Counts.Keys.Where(id => Count(id) > 0).OrderByDescending(Excess).FirstOrDefault() ?? Targets.Keys.First();
        }

        /// <summary>
        /// Brings the counts to <paramref name="stored"/> shells. The change
        /// is <paramref name="prefer"/>'s if it's known. An unexplained drop
        /// (damage, cook-off) comes off unassigned slots first; unexplained
        /// shells (a resupply) go where a re-rack would bring them.
        /// </summary>
        public void Sync(int stored, string? prefer = null)
        {
            int change = stored - Counts.Values.Sum();
            for (; change < 0; change++)
            {
                string id = Pick(prefer);
                if (Count(id) > 0)
                    Counts[id]--;
            }
            for (; change > 0; change--)
            {
                string id = prefer ?? Wanted(null).FirstOrDefault() ?? Targets.Keys.OrderBy(Excess).First();
                Counts[id] = Count(id) + 1;
            }
        }

        /// <summary>
        /// The rounds to re-rack into this rack, best first: the gun's
        /// <paramref name="next"/> round if it's short of its assigned slots,
        /// the rounds furthest short of theirs, then with unassigned slots free,
        /// the rounds least beyond theirs, which balances those slots evenly.
        /// Then, for when none of those can be had, any other round, the
        /// next round first: an assigned slot whose round has run out takes
        /// another rather than stay empty.
        /// </summary>
        public List<string> Wanted(string? next)
        {
            var wanted = new List<string>();
            if (next != null && Excess(next) < 0)
                wanted.Add(next);
            foreach (string id in Targets.Keys.Where(id => Excess(id) < 0 && id != next).OrderBy(Excess))
                wanted.Add(id);
            if (Free > 0)
                foreach (string id in Targets.Keys.OrderBy(Excess))
                    if (!wanted.Contains(id))
                        wanted.Add(id);
            if (Counts.Values.Sum() < Targets.Values.Sum() + Unassigned)
            {
                if (next != null && Targets.ContainsKey(next) && !wanted.Contains(next))
                    wanted.Add(next);
                foreach (string id in Targets.Keys.OrderBy(Excess))
                    if (!wanted.Contains(id))
                        wanted.Add(id);
            }
            return wanted;
        }
    }
}
