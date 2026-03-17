#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public static class TeamCreateIdentity
    {
        private const string KeyGuidMap = "TeamCreate_GuidMap";
        private const string KeyReverseMap = "TeamCreate_ReverseGuidMap";
        private const string KeyCompMap = "TeamCreate_ComponentGuidMap";
        private const string KeyCompReverse = "TeamCreate_ReverseCompGuidMap";

        private static Dictionary<int, string> _instanceToGuid = new Dictionary<int, string>();
        private static Dictionary<string, int> _guidToInstance = new Dictionary<string, int>();
        private static Dictionary<int, string> _compInstanceToGuid = new Dictionary<int, string>();
        private static Dictionary<string, int> _compGuidToInstance = new Dictionary<string, int>();

        static TeamCreateIdentity()
        {
            LoadFromSessionState();
            AssemblyReloadEvents.beforeAssemblyReload += SaveToSessionState;
        }

        private static void LoadFromSessionState()
        {
            try
            {
                _instanceToGuid = TeamCreateJson.Deserialize<Dictionary<int, string>>(UnityEditor.SessionState.GetString(KeyGuidMap, "{}")) ?? new Dictionary<int, string>();
                _guidToInstance = TeamCreateJson.Deserialize<Dictionary<string, int>>(UnityEditor.SessionState.GetString(KeyReverseMap, "{}")) ?? new Dictionary<string, int>();
                _compInstanceToGuid = TeamCreateJson.Deserialize<Dictionary<int, string>>(UnityEditor.SessionState.GetString(KeyCompMap, "{}")) ?? new Dictionary<int, string>();
                _compGuidToInstance = TeamCreateJson.Deserialize<Dictionary<string, int>>(UnityEditor.SessionState.GetString(KeyCompReverse, "{}")) ?? new Dictionary<string, int>();
            }
            catch
            {
                _instanceToGuid = new Dictionary<int, string>();
                _guidToInstance = new Dictionary<string, int>();
                _compInstanceToGuid = new Dictionary<int, string>();
                _compGuidToInstance = new Dictionary<string, int>();
            }
        }

        private static void SaveToSessionState()
        {
            try
            {
                UnityEditor.SessionState.SetString(KeyGuidMap, TeamCreateJson.Serialize(_instanceToGuid));
                UnityEditor.SessionState.SetString(KeyReverseMap, TeamCreateJson.Serialize(_guidToInstance));
                UnityEditor.SessionState.SetString(KeyCompMap, TeamCreateJson.Serialize(_compInstanceToGuid));
                UnityEditor.SessionState.SetString(KeyCompReverse, TeamCreateJson.Serialize(_compGuidToInstance));
            }
            catch (Exception e) { Debug.LogError($"[TeamCreate] Failed to save identity map: {e.Message}"); }
        }

        public static string GetOrAssignGuid(GameObject go)
        {
            if (go == null) return null;
            int id = go.GetInstanceID();
            if (_instanceToGuid.TryGetValue(id, out var existing)) return existing;
            var guid = Guid.NewGuid().ToString("N");
            _instanceToGuid[id] = guid;
            _guidToInstance[guid] = id;
            return guid;
        }

        public static void AssignGuid(GameObject go, string guid)
        {
            if (go == null || string.IsNullOrEmpty(guid)) return;
            int id = go.GetInstanceID();
            if (_instanceToGuid.TryGetValue(id, out var old) && old != guid) _guidToInstance.Remove(old);
            _instanceToGuid[id] = guid;
            _guidToInstance[guid] = id;
        }

        public static string GetGuid(GameObject go)
        {
            if (go == null) return null;
            _instanceToGuid.TryGetValue(go.GetInstanceID(), out var guid);
            return guid;
        }

        public static string GetGuidByInstanceId(int instanceId)
        {
            _instanceToGuid.TryGetValue(instanceId, out var guid);
            return guid;
        }

        public static GameObject FindByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (!_guidToInstance.TryGetValue(guid, out int id)) return null;
            return EditorUtility.InstanceIDToObject(id) as GameObject;
        }

        public static string GetOrAssignComponentGuid(Component comp)
        {
            if (comp == null) return null;
            int id = comp.GetInstanceID();
            if (_compInstanceToGuid.TryGetValue(id, out var existing)) return existing;
            var goGuid = GetOrAssignGuid(comp.gameObject);
            var components = comp.gameObject.GetComponents(comp.GetType());
            int index = Array.IndexOf(components, comp);
            var guid = $"{goGuid}_{comp.GetType().FullName}_{index}";
            _compInstanceToGuid[id] = guid;
            _compGuidToInstance[guid] = id;
            return guid;
        }

        public static void AssignComponentGuid(Component comp, string guid)
        {
            if (comp == null || string.IsNullOrEmpty(guid)) return;
            int id = comp.GetInstanceID();
            if (_compInstanceToGuid.TryGetValue(id, out var old) && old != guid) _compGuidToInstance.Remove(old);
            _compInstanceToGuid[id] = guid;
            _compGuidToInstance[guid] = id;
        }

        public static string GetComponentGuid(Component comp)
        {
            if (comp == null) return null;
            _compInstanceToGuid.TryGetValue(comp.GetInstanceID(), out var guid);
            return guid;
        }

        public static string GetComponentGuidByInstanceId(int instanceId)
        {
            _compInstanceToGuid.TryGetValue(instanceId, out var guid);
            return guid;
        }

        public static Component FindComponentByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (!_compGuidToInstance.TryGetValue(guid, out int id)) return null;
            return EditorUtility.InstanceIDToObject(id) as Component;
        }

        public static void RemoveGuid(GameObject go)
        {
            if (go == null) return;
            int id = go.GetInstanceID();
            if (_instanceToGuid.TryGetValue(id, out var guid)) { _guidToInstance.Remove(guid); _instanceToGuid.Remove(id); }
        }

        public static void ClearSession()
        {
            _instanceToGuid.Clear();
            _guidToInstance.Clear();
            _compInstanceToGuid.Clear();
            _compGuidToInstance.Clear();
            SaveToSessionState();
        }

        public static string BuildComponentGuidFromSnapshot(string gameObjectGuid, string typeName, int index)
            => $"{gameObjectGuid}_{typeName}_{index}";
    }
}
#endif
