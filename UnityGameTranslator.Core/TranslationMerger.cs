using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Handles 3-way merging of translation dictionaries.
    /// Supports merging local changes with remote updates from the website.
    /// </summary>
    public static class TranslationMerger
    {
        // Helper for .NET Standard 2.0 compatibility (GetValueOrDefault not available)
        private static TValue GetOrDefault<TKey, TValue>(Dictionary<TKey, TValue> dict, TKey key)
        {
            if (dict != null && dict.TryGetValue(key, out var value))
                return value;
            return default;
        }

        /// <summary>
        /// Perform a 3-way merge between local, remote, and ancestor translations.
        /// This overload supports TranslationEntry with tags and uses priority-based conflict resolution.
        /// Priority hierarchy: H empty (0) < A (1) < V (2) < H with value (3)
        /// </summary>
        public static MergeResultWithTags MergeWithTags(
            Dictionary<string, TranslationEntry> local,
            Dictionary<string, TranslationEntry> remote,
            Dictionary<string, TranslationEntry> ancestor)
        {
            var result = new MergeResultWithTags
            {
                Merged = new Dictionary<string, TranslationEntry>(),
                Conflicts = new List<MergeConflictWithTags>(),
                Statistics = new MergeStatistics()
            };

            // Get all keys from all three sources
            var allKeys = new HashSet<string>();
            if (local != null) allKeys.UnionWith(local.Keys.Where(k => !k.StartsWith("_")));
            if (remote != null) allKeys.UnionWith(remote.Keys.Where(k => !k.StartsWith("_")));
            if (ancestor != null) allKeys.UnionWith(ancestor.Keys.Where(k => !k.StartsWith("_")));

            foreach (var key in allKeys)
            {
                var localEntry = GetOrDefault(local, key);
                var remoteEntry = GetOrDefault(remote, key);
                var ancestorEntry = GetOrDefault(ancestor, key);

                var decision = ResolveKeyWithTags(key, localEntry, remoteEntry, ancestorEntry, result.Statistics);

                if (decision.HasConflict)
                {
                    result.Conflicts.Add(new MergeConflictWithTags
                    {
                        Key = key,
                        Local = localEntry,
                        Remote = remoteEntry,
                        Ancestor = ancestorEntry,
                        Type = decision.ConflictType
                    });
                    // Default to higher priority value, or remote if same priority
                    result.Merged[key] = decision.FinalEntry ?? remoteEntry ?? localEntry ?? new TranslationEntry();
                }
                else if (decision.FinalEntry != null)
                {
                    result.Merged[key] = decision.FinalEntry;
                }
            }

            RenumberMergedIndices(result.Merged, local);

            return result;
        }

        /// <summary>
        /// Resolve capture-order index collisions after a merge: both sides grew
        /// their counters independently since the fork, so different texts can
        /// carry the same index. Local entries keep their numbers; entries taken
        /// from the remote side whose index collides with a local one are
        /// renumbered after the highest index in the merged set, preserving
        /// their relative order — a conversation captured on the other device
        /// stays contiguous. Entries without an index are left for LoadCache's
        /// deterministic backfill. Safe by design: "i" is excluded from the
        /// content hash and from merge comparisons.
        /// </summary>
        private static void RenumberMergedIndices(
            Dictionary<string, TranslationEntry> merged,
            Dictionary<string, TranslationEntry> local)
        {
            if (merged == null || merged.Count == 0) return;

            // Entries kept from the local side, by reference (Merged reuses the
            // input TranslationEntry instances)
            var localEntries = new HashSet<TranslationEntry>();
            if (local != null)
            {
                foreach (var entry in local.Values)
                    localEntries.Add(entry);
            }

            long maxIndex = 0;
            var localIndices = new HashSet<long>();
            foreach (var kvp in merged)
            {
                if (!kvp.Value.Index.HasValue) continue;
                if (kvp.Value.Index.Value > maxIndex)
                    maxIndex = kvp.Value.Index.Value;
                if (localEntries.Contains(kvp.Value))
                    localIndices.Add(kvp.Value.Index.Value);
            }

            // Remote-sourced entries colliding with a local index
            var colliding = new List<KeyValuePair<string, TranslationEntry>>();
            foreach (var kvp in merged)
            {
                if (!kvp.Value.Index.HasValue) continue;
                if (localEntries.Contains(kvp.Value)) continue;
                if (localIndices.Contains(kvp.Value.Index.Value))
                    colliding.Add(kvp);
            }
            if (colliding.Count == 0) return;

            // Preserve the remote side's own ordering (index, then key for ties)
            colliding.Sort((a, b) =>
            {
                int cmp = a.Value.Index.Value.CompareTo(b.Value.Index.Value);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Key, b.Key);
            });

            long next = maxIndex + 1;
            foreach (var kvp in colliding)
            {
                kvp.Value.Index = next++;
            }
        }

        private static KeyDecisionWithTags ResolveKeyWithTags(
            string key,
            TranslationEntry localEntry,
            TranslationEntry remoteEntry,
            TranslationEntry ancestorEntry,
            MergeStatistics stats)
        {
            bool inLocal = localEntry != null;
            bool inRemote = remoteEntry != null;
            bool inAncestor = ancestorEntry != null;

            // Case 1: Key only in local (locally added)
            if (inLocal && !inRemote && !inAncestor)
            {
                stats.LocalOnlyCount++;
                return new KeyDecisionWithTags { FinalEntry = localEntry };
            }

            // Case 2: Key only in remote (remotely added)
            if (!inLocal && inRemote && !inAncestor)
            {
                stats.RemoteAddedCount++;
                return new KeyDecisionWithTags { FinalEntry = remoteEntry };
            }

            // Case 3: Key only in ancestor (deleted in both)
            if (!inLocal && !inRemote && inAncestor)
            {
                stats.DeletedCount++;
                return new KeyDecisionWithTags { FinalEntry = null };
            }

            // Case 4: Key in both local and remote
            if (inLocal && inRemote)
            {
                // Same value AND tag = no conflict
                if (localEntry.Value == remoteEntry.Value && localEntry.Tag == remoteEntry.Tag)
                {
                    stats.UnchangedCount++;
                    return new KeyDecisionWithTags { FinalEntry = localEntry };
                }

                // Check if one can replace the other based on tag priority
                // Higher priority wins without conflict
                if (remoteEntry.CanReplace(localEntry))
                {
                    // Remote has higher priority - take remote (no conflict)
                    stats.RemoteUpdatedCount++;
                    return new KeyDecisionWithTags { FinalEntry = remoteEntry };
                }
                if (localEntry.CanReplace(remoteEntry))
                {
                    // Local has higher priority - keep local (no conflict)
                    stats.LocalModifiedCount++;
                    return new KeyDecisionWithTags { FinalEntry = localEntry };
                }

                // Same priority - check ancestor for traditional 3-way merge
                if (inAncestor)
                {
                    // Local unchanged, remote changed = take remote
                    if (localEntry.Value == ancestorEntry.Value && localEntry.Tag == ancestorEntry.Tag)
                    {
                        stats.RemoteUpdatedCount++;
                        return new KeyDecisionWithTags { FinalEntry = remoteEntry };
                    }

                    // Remote unchanged, local changed = keep local
                    if (remoteEntry.Value == ancestorEntry.Value && remoteEntry.Tag == ancestorEntry.Tag)
                    {
                        stats.LocalModifiedCount++;
                        return new KeyDecisionWithTags { FinalEntry = localEntry };
                    }

                    // Both changed with same priority = conflict
                    stats.ConflictCount++;
                    return new KeyDecisionWithTags
                    {
                        HasConflict = true,
                        ConflictType = ConflictType.BothModified,
                        FinalEntry = remoteEntry  // Default to remote for display
                    };
                }
                else
                {
                    // No ancestor, same priority = conflict
                    stats.ConflictCount++;
                    return new KeyDecisionWithTags
                    {
                        HasConflict = true,
                        ConflictType = ConflictType.NoAncestor,
                        FinalEntry = remoteEntry
                    };
                }
            }

            // Case 5: Key in local and ancestor but not remote (remote deleted)
            if (inLocal && !inRemote && inAncestor)
            {
                if (localEntry.Value == ancestorEntry.Value && localEntry.Tag == ancestorEntry.Tag)
                {
                    stats.DeletedCount++;
                    return new KeyDecisionWithTags { FinalEntry = null };
                }
                else
                {
                    stats.ConflictCount++;
                    return new KeyDecisionWithTags
                    {
                        HasConflict = true,
                        ConflictType = ConflictType.LocalModifiedRemoteDeleted,
                        FinalEntry = localEntry
                    };
                }
            }

            // Case 6: Key in remote and ancestor but not local (locally deleted)
            if (!inLocal && inRemote && inAncestor)
            {
                if (remoteEntry.Value == ancestorEntry.Value && remoteEntry.Tag == ancestorEntry.Tag)
                {
                    stats.DeletedCount++;
                    return new KeyDecisionWithTags { FinalEntry = null };
                }
                else
                {
                    stats.ConflictCount++;
                    return new KeyDecisionWithTags
                    {
                        HasConflict = true,
                        ConflictType = ConflictType.RemoteModifiedLocalDeleted,
                        FinalEntry = remoteEntry
                    };
                }
            }

            // Default: take whatever is available
            stats.UnchangedCount++;
            return new KeyDecisionWithTags { FinalEntry = remoteEntry ?? localEntry };
        }

        private class KeyDecisionWithTags
        {
            public TranslationEntry FinalEntry { get; set; }
            public bool HasConflict { get; set; }
            public ConflictType ConflictType { get; set; }
        }
    }

    /// <summary>
    /// Result of a 3-way merge with tag support
    /// </summary>
    public class MergeResultWithTags
    {
        /// <summary>
        /// The merged translation dictionary with tags
        /// </summary>
        public Dictionary<string, TranslationEntry> Merged { get; set; }

        /// <summary>
        /// List of conflicts that need resolution
        /// </summary>
        public List<MergeConflictWithTags> Conflicts { get; set; }

        /// <summary>
        /// Statistics about the merge operation
        /// </summary>
        public MergeStatistics Statistics { get; set; }

        /// <summary>
        /// Whether merge completed without conflicts
        /// </summary>
        public bool Success => Conflicts == null || Conflicts.Count == 0;

        /// <summary>
        /// Number of unresolved conflicts
        /// </summary>
        public int ConflictCount => Conflicts?.Count ?? 0;
    }

    /// <summary>
    /// A merge conflict with TranslationEntry values (includes tags)
    /// </summary>
    public class MergeConflictWithTags
    {
        public string Key { get; set; }
        public TranslationEntry Local { get; set; }
        public TranslationEntry Remote { get; set; }
        public TranslationEntry Ancestor { get; set; }
        public ConflictType Type { get; set; }
    }

    public class MergeStatistics
    {
        /// <summary>Keys unchanged in both versions</summary>
        public int UnchangedCount { get; set; }

        /// <summary>Keys only in local (user additions)</summary>
        public int LocalOnlyCount { get; set; }

        /// <summary>Keys modified locally</summary>
        public int LocalModifiedCount { get; set; }

        /// <summary>Keys added in remote</summary>
        public int RemoteAddedCount { get; set; }

        /// <summary>Keys updated in remote (local unchanged)</summary>
        public int RemoteUpdatedCount { get; set; }

        /// <summary>Keys deleted (in sync)</summary>
        public int DeletedCount { get; set; }

        /// <summary>Keys with conflicts</summary>
        public int ConflictCount { get; set; }

        /// <summary>Conflicts that were resolved</summary>
        public int ResolvedCount { get; set; }

        /// <summary>Total keys in merged result</summary>
        public int TotalMergedCount =>
            UnchangedCount + LocalOnlyCount + LocalModifiedCount +
            RemoteAddedCount + RemoteUpdatedCount + ConflictCount - ResolvedCount;

        /// <summary>Get a summary string</summary>
        public string GetSummary()
        {
            var parts = new List<string>();
            if (RemoteAddedCount > 0) parts.Add($"+{RemoteAddedCount} new");
            if (RemoteUpdatedCount > 0) parts.Add($"~{RemoteUpdatedCount} updated");
            if (LocalModifiedCount > 0) parts.Add($"{LocalModifiedCount} local kept");
            if (LocalOnlyCount > 0) parts.Add($"{LocalOnlyCount} local only");
            if (DeletedCount > 0) parts.Add($"-{DeletedCount} deleted");
            if (ConflictCount > 0) parts.Add($"!{ConflictCount} conflicts");
            return parts.Count > 0 ? string.Join(", ", parts) : "No changes";
        }
    }

    public enum ConflictType
    {
        /// <summary>Both local and remote modified the same key differently</summary>
        BothModified,

        /// <summary>No ancestor available to determine who changed what</summary>
        NoAncestor,

        /// <summary>Local modified but remote deleted the key</summary>
        LocalModifiedRemoteDeleted,

        /// <summary>Remote modified but local deleted the key</summary>
        RemoteModifiedLocalDeleted
    }

    public enum ConflictResolution
    {
        /// <summary>Keep the local version</summary>
        KeepLocal,

        /// <summary>Take the remote version</summary>
        TakeRemote,

        /// <summary>Keep both (mark for manual review)</summary>
        KeepBoth
    }
}
