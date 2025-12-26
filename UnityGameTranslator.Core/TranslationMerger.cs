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
        /// </summary>
        /// <param name="local">Current local translations (with user modifications)</param>
        /// <param name="remote">New translations from the website</param>
        /// <param name="ancestor">Original state when local was downloaded (before modifications)</param>
        /// <returns>Merge result with merged dictionary, conflicts, and statistics</returns>
        public static MergeResult Merge(
            Dictionary<string, string> local,
            Dictionary<string, string> remote,
            Dictionary<string, string> ancestor)
        {
            var result = new MergeResult
            {
                Merged = new Dictionary<string, string>(),
                Conflicts = new List<MergeConflict>(),
                Statistics = new MergeStatistics()
            };

            // Get all keys from all three sources
            var allKeys = new HashSet<string>();
            if (local != null) allKeys.UnionWith(local.Keys.Where(k => !k.StartsWith("_")));
            if (remote != null) allKeys.UnionWith(remote.Keys.Where(k => !k.StartsWith("_")));
            if (ancestor != null) allKeys.UnionWith(ancestor.Keys.Where(k => !k.StartsWith("_")));

            foreach (var key in allKeys)
            {
                string localValue = GetOrDefault(local, key);
                string remoteValue = GetOrDefault(remote, key);
                string ancestorValue = GetOrDefault(ancestor, key);

                var decision = ResolveKey(key, localValue, remoteValue, ancestorValue, result.Statistics);

                if (decision.HasConflict)
                {
                    result.Conflicts.Add(new MergeConflict
                    {
                        Key = key,
                        LocalValue = localValue,
                        RemoteValue = remoteValue,
                        AncestorValue = ancestorValue,
                        Type = decision.ConflictType
                    });
                    // Default to remote value for conflicts (can be overridden later)
                    result.Merged[key] = remoteValue ?? localValue ?? "";
                }
                else if (decision.FinalValue != null)
                {
                    result.Merged[key] = decision.FinalValue;
                }
                // else: key was deleted in both
            }

            return result;
        }

        /// <summary>
        /// Perform a simple 2-way merge (no ancestor).
        /// Prefers remote values for conflicts.
        /// </summary>
        public static MergeResult MergeSimple(
            Dictionary<string, string> local,
            Dictionary<string, string> remote)
        {
            return Merge(local, remote, null);
        }

        private static KeyDecision ResolveKey(
            string key,
            string localValue,
            string remoteValue,
            string ancestorValue,
            MergeStatistics stats)
        {
            bool inLocal = localValue != null;
            bool inRemote = remoteValue != null;
            bool inAncestor = ancestorValue != null;

            // Case 1: Key only in local (locally added or remote deleted)
            if (inLocal && !inRemote && !inAncestor)
            {
                stats.LocalOnlyCount++;
                return new KeyDecision { FinalValue = localValue };
            }

            // Case 2: Key only in remote (remotely added)
            if (!inLocal && inRemote && !inAncestor)
            {
                stats.RemoteAddedCount++;
                return new KeyDecision { FinalValue = remoteValue };
            }

            // Case 3: Key only in ancestor (deleted in both)
            if (!inLocal && !inRemote && inAncestor)
            {
                stats.DeletedCount++;
                return new KeyDecision { FinalValue = null };
            }

            // Case 4: Key in both local and remote
            if (inLocal && inRemote)
            {
                // Same value in both = no conflict
                if (localValue == remoteValue)
                {
                    stats.UnchangedCount++;
                    return new KeyDecision { FinalValue = localValue };
                }

                // Different values - check ancestor
                if (inAncestor)
                {
                    // Local unchanged, remote changed = take remote
                    if (localValue == ancestorValue && remoteValue != ancestorValue)
                    {
                        stats.RemoteUpdatedCount++;
                        return new KeyDecision { FinalValue = remoteValue };
                    }

                    // Remote unchanged, local changed = keep local
                    if (remoteValue == ancestorValue && localValue != ancestorValue)
                    {
                        stats.LocalModifiedCount++;
                        return new KeyDecision { FinalValue = localValue };
                    }

                    // Both changed differently = conflict
                    stats.ConflictCount++;
                    return new KeyDecision
                    {
                        HasConflict = true,
                        ConflictType = ConflictType.BothModified
                    };
                }
                else
                {
                    // No ancestor = conflict (2-way merge)
                    stats.ConflictCount++;
                    return new KeyDecision
                    {
                        HasConflict = true,
                        ConflictType = ConflictType.NoAncestor
                    };
                }
            }

            // Case 5: Key in local and ancestor but not remote (remote deleted)
            if (inLocal && !inRemote && inAncestor)
            {
                if (localValue == ancestorValue)
                {
                    // Local unchanged, remote deleted = accept deletion
                    stats.DeletedCount++;
                    return new KeyDecision { FinalValue = null };
                }
                else
                {
                    // Local modified, remote deleted = conflict
                    stats.ConflictCount++;
                    return new KeyDecision
                    {
                        HasConflict = true,
                        ConflictType = ConflictType.LocalModifiedRemoteDeleted
                    };
                }
            }

            // Case 6: Key in remote and ancestor but not local (locally deleted)
            if (!inLocal && inRemote && inAncestor)
            {
                if (remoteValue == ancestorValue)
                {
                    // Remote unchanged, local deleted = keep deletion
                    stats.DeletedCount++;
                    return new KeyDecision { FinalValue = null };
                }
                else
                {
                    // Remote modified, local deleted = conflict
                    stats.ConflictCount++;
                    return new KeyDecision
                    {
                        HasConflict = true,
                        ConflictType = ConflictType.RemoteModifiedLocalDeleted
                    };
                }
            }

            // Default: take whatever is available
            stats.UnchangedCount++;
            return new KeyDecision { FinalValue = remoteValue ?? localValue };
        }

        /// <summary>
        /// Apply conflict resolutions to merge result
        /// </summary>
        public static void ApplyResolutions(
            MergeResult result,
            Dictionary<string, ConflictResolution> resolutions)
        {
            foreach (var conflict in result.Conflicts.ToList())
            {
                if (resolutions.TryGetValue(conflict.Key, out var resolution))
                {
                    switch (resolution)
                    {
                        case ConflictResolution.KeepLocal:
                            if (conflict.LocalValue != null)
                                result.Merged[conflict.Key] = conflict.LocalValue;
                            else
                                result.Merged.Remove(conflict.Key);
                            break;

                        case ConflictResolution.TakeRemote:
                            if (conflict.RemoteValue != null)
                                result.Merged[conflict.Key] = conflict.RemoteValue;
                            else
                                result.Merged.Remove(conflict.Key);
                            break;

                        case ConflictResolution.KeepBoth:
                            // For "keep both", append remote value with marker
                            if (conflict.LocalValue != null && conflict.RemoteValue != null)
                            {
                                // Use local value but mark that remote differs
                                result.Merged[conflict.Key] = conflict.LocalValue;
                            }
                            break;
                    }

                    result.Conflicts.Remove(conflict);
                    result.Statistics.ResolvedCount++;
                }
            }
        }

        private class KeyDecision
        {
            public string FinalValue { get; set; }
            public bool HasConflict { get; set; }
            public ConflictType ConflictType { get; set; }
        }
    }

    #region Merge Types

    public class MergeResult
    {
        /// <summary>
        /// The merged translation dictionary
        /// </summary>
        public Dictionary<string, string> Merged { get; set; }

        /// <summary>
        /// List of conflicts that need resolution
        /// </summary>
        public List<MergeConflict> Conflicts { get; set; }

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

    public class MergeConflict
    {
        public string Key { get; set; }
        public string LocalValue { get; set; }
        public string RemoteValue { get; set; }
        public string AncestorValue { get; set; }
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

    #endregion
}
