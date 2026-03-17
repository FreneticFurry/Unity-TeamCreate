#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace TeamCreate.Editor
{
    public static class TeamCreateConflictResolver
    {
        private const long ConflictWindowTicks = 100 * TimeSpan.TicksPerMillisecond;

        private static readonly Dictionary<string, long> LastSeenTimestamps = new Dictionary<string, long>();

        public static bool ShouldApply(string conflictKey, long incomingTimestamp)
        {
            if (!LastSeenTimestamps.TryGetValue(conflictKey, out long lastTimestamp))
            {
                LastSeenTimestamps[conflictKey] = incomingTimestamp;
                return true;
            }

            long delta = Math.Abs(incomingTimestamp - lastTimestamp);
            if (delta <= ConflictWindowTicks)
            {
                if (incomingTimestamp >= lastTimestamp)
                {
                    TeamCreateLogger.Log($"Conflict on '{conflictKey}': applying later timestamp {incomingTimestamp}.");
                    LastSeenTimestamps[conflictKey] = incomingTimestamp;
                    return true;
                }
                else
                {
                    TeamCreateLogger.Log($"Conflict on '{conflictKey}': discarding earlier timestamp {incomingTimestamp}, keeping {lastTimestamp}.");
                    return false;
                }
            }

            if (incomingTimestamp > lastTimestamp)
            {
                LastSeenTimestamps[conflictKey] = incomingTimestamp;
                return true;
            }

            TeamCreateLogger.Log($"Conflict on '{conflictKey}': discarding stale timestamp {incomingTimestamp}.");
            return false;
        }

        public static string MakePropertyKey(string componentGuid, string propertyPath)
        {
            return $"{componentGuid}::{propertyPath}";
        }

        public static string MakeAssetKey(string relativePath)
        {
            return $"asset::{relativePath}";
        }

        public static string MakeGameObjectKey(string gameObjectGuid, string aspect)
        {
            return $"go::{gameObjectGuid}::{aspect}";
        }

        public static void Clear()
        {
            LastSeenTimestamps.Clear();
        }
    }
}
#endif
