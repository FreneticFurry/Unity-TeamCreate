#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public static class TeamCreateSceneSync
    {
        private static readonly Dictionary<int, string> _handleToNetId = new Dictionary<int, string>();
        private static readonly Dictionary<string, int> _netIdToHandle = new Dictionary<string, int>();

        static TeamCreateSceneSync()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            AssemblyReloadEvents.beforeAssemblyReload += Unregister;
        }

        private static void Unregister()
        {
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            AssemblyReloadEvents.beforeAssemblyReload -= Unregister;
            _handleToNetId.Clear();
            _netIdToHandle.Clear();
        }

        public static string GetOrCreateNetIdForScene(Scene scene)
        {
            if (!scene.IsValid()) return string.Empty;
            if (_handleToNetId.TryGetValue(scene.handle, out string existing)) return existing;
            string newId = Guid.NewGuid().ToString("N");
            _handleToNetId[scene.handle] = newId;
            _netIdToHandle[newId] = scene.handle;
            return newId;
        }

        public static Scene GetSceneByNetId(string netId)
        {
            if (string.IsNullOrEmpty(netId)) return default;
            if (!_netIdToHandle.TryGetValue(netId, out int handle)) return default;
            int count = EditorSceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (s.handle == handle) return s;
            }
            _netIdToHandle.Remove(netId);
            return default;
        }

        private static void StoreNetIdMapping(string netId, Scene scene)
        {
            if (string.IsNullOrEmpty(netId) || !scene.IsValid()) return;
            _netIdToHandle[netId] = scene.handle;
            _handleToNetId[scene.handle] = netId;
        }

        private static void RemoveNetIdMapping(Scene scene)
        {
            if (!_handleToNetId.TryGetValue(scene.handle, out string netId)) return;
            _handleToNetId.Remove(scene.handle);
            _netIdToHandle.Remove(netId);
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (!TeamCreateSession.IsConnected) return;
            if (TeamCreateSession.IsApplyingRemoteChange) return;

            string networkPath = string.IsNullOrEmpty(scene.path) ? string.Empty : TeamCreateSession.LocalToNetworkPath(scene.path);
            if (!string.IsNullOrEmpty(scene.path) && networkPath == null) return;

            string sceneAssetGuid = string.IsNullOrEmpty(scene.path) ? string.Empty : AssetDatabase.AssetPathToGUID(scene.path);
            var payload = new SceneOpenedPayload
            {
                SceneAssetGuid = sceneAssetGuid,
                SceneName = scene.name,
                ScenePath = networkPath ?? string.Empty,
                SetActive = scene == SceneManager.GetActiveScene(),
                IsSingleMode = mode == OpenSceneMode.Single,
                SceneData = TeamCreateSnapshotBuilder.SerializeScene(scene),
                SceneNetId = GetOrCreateNetIdForScene(scene)
            };
            TeamCreateLogger.LogSend("SCENE_OPENED", scene.name);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.SCENE_OPENED, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
        }

        private static void OnSceneClosed(Scene scene)
        {
            if (!TeamCreateSession.IsConnected) return;
            if (TeamCreateSession.IsApplyingRemoteChange) return;
            if (!string.IsNullOrEmpty(scene.path) && !TeamCreateSession.IsInSessionScope(scene.path)) return;

            string networkPath = string.IsNullOrEmpty(scene.path) ? string.Empty : TeamCreateSession.LocalToNetworkPath(scene.path);
            if (!string.IsNullOrEmpty(scene.path) && networkPath == null) return;

            string netId = GetOrCreateNetIdForScene(scene);
            RemoveNetIdMapping(scene);
            var payload = new SceneClosedPayload { ScenePath = networkPath ?? string.Empty, SceneName = scene.name, SceneNetId = netId };
            TeamCreateLogger.LogSend("SCENE_CLOSED", scene.name);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.SCENE_CLOSED, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
        }

        private static void OnActiveSceneChanged(Scene previous, Scene next)
        {
            if (!TeamCreateSession.IsConnected) return;
            if (TeamCreateSession.IsApplyingRemoteChange) return;
            if (!string.IsNullOrEmpty(next.path) && !TeamCreateSession.IsInSessionScope(next.path)) return;

            string networkPath = string.IsNullOrEmpty(next.path) ? string.Empty : TeamCreateSession.LocalToNetworkPath(next.path);
            if (!string.IsNullOrEmpty(next.path) && networkPath == null) return;

            var payload = new SceneActiveChangedPayload { ScenePath = networkPath ?? string.Empty, SceneName = next.name };
            TeamCreateLogger.LogSend("SCENE_ACTIVE_CHANGED", next.name);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.SCENE_ACTIVE_CHANGED, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
        }

        private static void OnSceneSaved(Scene scene)
        {
            if (!TeamCreateSession.IsConnected) return;
            if (TeamCreateSession.IsApplyingRemoteChange) return;
            if (string.IsNullOrEmpty(scene.path)) return;
            if (!TeamCreateSession.IsInSessionScope(scene.path)) return;

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string absPath = Path.Combine(projectRoot, scene.path);
            if (!File.Exists(absPath)) return;

            try
            {
                byte[] content = File.ReadAllBytes(absPath);
                TeamCreateLogger.LogSend("ASSET_MODIFIED (scene save)", scene.path);
                TeamCreateAssetSync.SendFileDirectly(scene.path, content, MessageType.ASSET_MODIFIED);
            }
            catch (Exception e)
            {
                Debug.LogError($"[TeamCreate] Failed to broadcast saved scene '{scene.path}': {e.Message}");
            }
        }

        public static void ApplySceneOpened(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<SceneOpenedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("SCENE_OPENED", message.SenderId);

            string localPath = string.IsNullOrEmpty(payload.ScenePath)
                ? string.Empty
                : TeamCreateSession.NetworkToLocalPath(payload.ScenePath);

            if (!string.IsNullOrEmpty(payload.SceneNetId) && GetSceneByNetId(payload.SceneNetId).IsValid()) return;
            if (!string.IsNullOrEmpty(localPath))
            {
                int preCount = EditorSceneManager.sceneCount;
                for (int i = 0; i < preCount; i++)
                {
                    var s = EditorSceneManager.GetSceneAt(i);
                    if (s.path == localPath) return;
                }
            }

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                Scene newScene;
                bool openedFromFile = false;
                if (!string.IsNullOrEmpty(localPath))
                {
                    string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                    string absPath = Path.Combine(projectRoot, localPath);
                    if (File.Exists(absPath))
                    {
                        var fileMode = payload.IsSingleMode ? OpenSceneMode.Single : (EditorSceneManager.sceneCount == 0 ? OpenSceneMode.Single : OpenSceneMode.Additive);
                        newScene = EditorSceneManager.OpenScene(localPath, fileMode);
                        openedFromFile = true;
                    }
                    else
                    {
                        var newMode = payload.IsSingleMode ? NewSceneMode.Single : (EditorSceneManager.sceneCount == 0 ? NewSceneMode.Single : NewSceneMode.Additive);
                        newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, newMode);
                        newScene.name = payload.SceneName;
                    }
                }
                else
                {
                    var newMode = payload.IsSingleMode ? NewSceneMode.Single : (EditorSceneManager.sceneCount == 0 ? NewSceneMode.Single : NewSceneMode.Additive);
                    newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, newMode);
                    newScene.name = payload.SceneName;
                }

                if (!string.IsNullOrEmpty(payload.SceneNetId) && newScene.IsValid())
                    StoreNetIdMapping(payload.SceneNetId, newScene);

                if (openedFromFile && newScene.IsValid() && payload.SceneData?.GameObjects != null && payload.SceneData.GameObjects.Count > 0)
                {
                    RegisterSceneObjectGuids(newScene, payload.SceneData.GameObjects);
                }

                if (!openedFromFile && newScene.IsValid() && payload.SceneData?.GameObjects != null && payload.SceneData.GameObjects.Count > 0)
                    TeamCreateSnapshotBuilder.ReconstructHierarchy(newScene, payload.SceneData.GameObjects);

                if (payload.SetActive && newScene.IsValid())
                    SceneManager.SetActiveScene(newScene);
            }
            catch (Exception e)
            {
                Debug.LogError($"[TeamCreate] Failed to apply scene opened '{payload.SceneName}': {e.Message}");
            }
            finally
            {
                TeamCreateSession.IsApplyingRemoteChange = false;
            }
        }

        public static void ApplySceneClosed(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<SceneClosedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("SCENE_CLOSED", message.SenderId);

            string localPath = string.IsNullOrEmpty(payload.ScenePath)
                ? string.Empty
                : TeamCreateSession.NetworkToLocalPath(payload.ScenePath);

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                Scene targetScene = GetSceneByNetId(payload.SceneNetId);
                if (!targetScene.IsValid())
                {
                    int count = EditorSceneManager.sceneCount;
                    for (int i = 0; i < count; i++)
                    {
                        var s = EditorSceneManager.GetSceneAt(i);
                        if ((!string.IsNullOrEmpty(localPath) && s.path == localPath) || s.name == payload.SceneName) { targetScene = s; break; }
                    }
                }
                if (targetScene.IsValid()) RemoveNetIdMapping(targetScene);
                if (targetScene.IsValid() && EditorSceneManager.sceneCount > 1)
                    EditorSceneManager.CloseScene(targetScene, true);
            }
            catch (Exception e)
            {
                Debug.LogError($"[TeamCreate] Failed to apply scene closed '{payload.SceneName}': {e.Message}");
            }
            finally
            {
                TeamCreateSession.IsApplyingRemoteChange = false;
            }
        }

        public static void ApplySceneActiveChanged(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<SceneActiveChangedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("SCENE_ACTIVE_CHANGED", message.SenderId);

            string localPath = string.IsNullOrEmpty(payload.ScenePath)
                ? string.Empty
                : TeamCreateSession.NetworkToLocalPath(payload.ScenePath);

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                int count = EditorSceneManager.sceneCount;
                for (int i = 0; i < count; i++)
                {
                    var s = EditorSceneManager.GetSceneAt(i);
                    if (s.path == localPath || s.name == payload.SceneName) { SceneManager.SetActiveScene(s); break; }
                }
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        private static void RegisterSceneObjectGuids(Scene scene, List<GameObjectData> dataList)
        {
            if (dataList == null || dataList.Count == 0) return;

            var rootData = new List<GameObjectData>();
            var childrenByParent = new Dictionary<string, List<GameObjectData>>();
            foreach (var d in dataList)
            {
                if (string.IsNullOrEmpty(d.ParentGuid))
                    rootData.Add(d);
                else
                {
                    if (!childrenByParent.ContainsKey(d.ParentGuid))
                        childrenByParent[d.ParentGuid] = new List<GameObjectData>();
                    childrenByParent[d.ParentGuid].Add(d);
                }
            }
            rootData.Sort((a, b) => a.SiblingIndex.CompareTo(b.SiblingIndex));

            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length && i < rootData.Count; i++)
                AssignGuidsRecursive(roots[i], rootData[i], childrenByParent);
        }

        private static void AssignGuidsRecursive(GameObject go, GameObjectData data, Dictionary<string, List<GameObjectData>> childrenByParent)
        {
            if (!string.IsNullOrEmpty(data.Guid) && string.IsNullOrEmpty(TeamCreateIdentity.GetGuid(go)))
                TeamCreateIdentity.AssignGuid(go, data.Guid);

            if (string.IsNullOrEmpty(data.Guid) || !childrenByParent.TryGetValue(data.Guid, out var children)) return;
            children.Sort((a, b) => a.SiblingIndex.CompareTo(b.SiblingIndex));
            for (int i = 0; i < go.transform.childCount && i < children.Count; i++)
                AssignGuidsRecursive(go.transform.GetChild(i).gameObject, children[i], childrenByParent);
        }

        public static void ApplySceneRenamed(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<SceneRenamedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("SCENE_RENAMED", message.SenderId);

            string localOld = string.IsNullOrEmpty(payload.OldScenePath) ? string.Empty : TeamCreateSession.NetworkToLocalPath(payload.OldScenePath);
            string localNew = string.IsNullOrEmpty(payload.NewScenePath) ? string.Empty : TeamCreateSession.NetworkToLocalPath(payload.NewScenePath);

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                if (!string.IsNullOrEmpty(localOld) && !string.IsNullOrEmpty(localNew))
                    AssetDatabase.MoveAsset(localOld, localNew);
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }
    }
}
#endif
