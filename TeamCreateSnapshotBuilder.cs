#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public static class TeamCreateSnapshotBuilder
    {
        private static FullStateResponsePayload _deferredState;
        private static HashSet<string> _pendingLocalPaths;
        private static double _deferredStartTime;
        private const double DeferTimeout = 30.0;
        private static int _applyFullStateVersion = 0;

        static TeamCreateSnapshotBuilder()
        {
            EditorApplication.update += OnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            _deferredState = null;
            _pendingLocalPaths = null;
        }

        private static void OnUpdate()
        {
            if (_deferredState == null || _pendingLocalPaths == null) return;
            if (EditorApplication.timeSinceStartup - _deferredStartTime >= DeferTimeout)
            {
                Debug.LogWarning("[TeamCreate] Asset wait timed out — building scene with available assets.");
                ApplyDeferredSceneData();
            }
        }

        public static void NotifyAssetArrived(string localPath)
        {
            if (_pendingLocalPaths == null) return;
            _pendingLocalPaths.Remove(localPath);
            if (_pendingLocalPaths.Count == 0)
                ApplyDeferredSceneData();
        }

        private static void ApplyDeferredSceneData()
        {
            var state = _deferredState;
            _deferredState = null;
            _pendingLocalPaths = null;
            if (state == null) return;

            if (!string.IsNullOrEmpty(TeamCreateSession.SessionFolderName))
            {
                TeamCreateSession.IsApplyingRemoteChange = true;
                try
                {
                    string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                    string sessionAbsPath = Path.Combine(projectRoot, "Assets", TeamCreateSession.SessionFolderName);
                    if (Directory.Exists(sessionAbsPath))
                    {
                        string[] matFiles = Directory.GetFiles(sessionAbsPath, "*.mat", SearchOption.AllDirectories);
                        foreach (string absPath in matFiles)
                        {
                            string relPath = absPath.Substring(projectRoot.Length + 1).Replace('\\', '/');
                            AssetDatabase.ImportAsset(relPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                        }
                    }
                }
                catch { }
                finally { TeamCreateSession.IsApplyingRemoteChange = false; }
            }

            ApplySceneData(state);
        }

        public static void SendFullState(PeerConnection peer)
        {
            var scenes = new List<SceneData>();
            int sceneCount = EditorSceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid()) continue;
                scenes.Add(SerializeScene(scene));
            }

            TeamCreateHierarchySync.FlushPendingMaterialsToDisk();
            var response = new FullStateResponsePayload { Scenes = scenes, AssetManifest = BuildAssetManifest() };
            TeamCreateLogger.LogSend("FULL_STATE_RESPONSE", peer.PeerId);
            peer.SendMessage(Protocol.CreateMessage(MessageType.FULL_STATE_RESPONSE, TeamCreateSession.LocalPeerId, Protocol.Serialize(response)));
        }

        public static SceneData SerializeScene(Scene scene)
        {
            var data = new SceneData
            {
                ScenePath = scene.path,
                SceneName = scene.name,
                SceneAssetGuid = string.IsNullOrEmpty(scene.path) ? "" : AssetDatabase.AssetPathToGUID(scene.path),
                IsActive = scene == SceneManager.GetActiveScene(),
                GameObjects = new List<GameObjectData>()
            };
            foreach (var root in scene.GetRootGameObjects())
                CollectGameObjectData(root, null, data.GameObjects);
            return data;
        }

        private static void CollectGameObjectData(GameObject go, string parentGuid, List<GameObjectData> list)
        {
            string guid = TeamCreateIdentity.GetOrAssignGuid(go);
            var goData = new GameObjectData
            {
                Guid = guid,
                Name = go.name,
                ParentGuid = parentGuid,
                SiblingIndex = go.transform.GetSiblingIndex(),
                Layer = go.layer,
                Tag = go.tag,
                IsActive = go.activeSelf,
                ScenePath = go.scene.path,
                Components = new List<ComponentData>()
            };
            var components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                var comp = components[i];
                if (comp == null) continue;
                string compGuid = TeamCreateIdentity.BuildComponentGuidFromSnapshot(guid, comp.GetType().FullName, i);
                TeamCreateIdentity.AssignComponentGuid(comp, compGuid);
                goData.Components.Add(new ComponentData
                {
                    TypeName = comp.GetType().AssemblyQualifiedName,
                    ComponentGuid = compGuid,
                    Properties = SerializeAllProperties(comp)
                });
            }
            list.Add(goData);
            foreach (Transform child in go.transform)
                CollectGameObjectData(child.gameObject, guid, list);
        }

        public static Dictionary<string, string> SerializeAllProperties(Component comp)
        {
            var result = new Dictionary<string, string>();
            var so = new SerializedObject(comp);
            var prop = so.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = prop.propertyType == SerializedPropertyType.Generic;
                if (prop.propertyPath == "m_Script") continue;
                try
                {
                    var val = SerializeProperty(prop);
                    if (val != null) result[prop.propertyPath] = val;
                }
                catch { }
            }
            return result;
        }

        public static string SerializeProperty(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return $"int:{prop.intValue}";
                case SerializedPropertyType.Boolean: return $"bool:{prop.boolValue}";
                case SerializedPropertyType.Float: return $"float:{prop.floatValue.ToString("R")}";
                case SerializedPropertyType.String: return $"string:{prop.stringValue}";
                case SerializedPropertyType.Enum: return $"enum:{prop.enumValueIndex}";
                case SerializedPropertyType.LayerMask: return $"mask:{prop.intValue}";
                case SerializedPropertyType.Color:
                    {
                        var c = prop.colorValue;
                        return $"color:{c.r.ToString("R")},{c.g.ToString("R")},{c.b.ToString("R")},{c.a.ToString("R")}";
                    }
                case SerializedPropertyType.Vector2:
                    {
                        var v = prop.vector2Value;
                        return $"v2:{v.x.ToString("R")},{v.y.ToString("R")}";
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var v = prop.vector3Value;
                        return $"v3:{v.x.ToString("R")},{v.y.ToString("R")},{v.z.ToString("R")}";
                    }
                case SerializedPropertyType.Vector4:
                    {
                        var v = prop.vector4Value;
                        return $"v4:{v.x.ToString("R")},{v.y.ToString("R")},{v.z.ToString("R")},{v.w.ToString("R")}";
                    }
                case SerializedPropertyType.Quaternion:
                    {
                        var q = prop.quaternionValue;
                        return $"quat:{q.x.ToString("R")},{q.y.ToString("R")},{q.z.ToString("R")},{q.w.ToString("R")}";
                    }
                case SerializedPropertyType.Rect:
                    {
                        var r = prop.rectValue;
                        return $"rect:{r.x.ToString("R")},{r.y.ToString("R")},{r.width.ToString("R")},{r.height.ToString("R")}";
                    }
                case SerializedPropertyType.AnimationCurve:
                    {
                        var curve = prop.animationCurveValue;
                        if (curve == null) return null;
                        var keys = new List<string>();
                        foreach (var k in curve.keys)
                            keys.Add($"{k.time.ToString("R")}:{k.value.ToString("R")}:{k.inTangent.ToString("R")}:{k.outTangent.ToString("R")}");
                        return $"curve:{string.Join(";", keys)}";
                    }
                case SerializedPropertyType.ObjectReference:
                    {
                        var obj = prop.objectReferenceValue;
                        if (obj == null) return "ref:null";

                        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string assetGuid, out long localId))
                        {
                            if (!string.IsNullOrEmpty(assetGuid) && assetGuid != "00000000000000000000000000000000"
                                && !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(assetGuid)))
                            {
                                return $"ref:guidlocal:{assetGuid}:{localId}";
                            }
                        }

                        if (obj is Shader shaderRef)
                            return $"ref:shader:{shaderRef.name}";

                        if (obj is GameObject goRef)
                        {
                            string netGuid = TeamCreateIdentity.GetGuid(goRef);
                            if (!string.IsNullOrEmpty(netGuid)) return $"ref:scene:{netGuid}";
                        }
                        if (obj is Component compRef)
                        {
                            string netGuid = TeamCreateIdentity.GetComponentGuid(compRef);
                            if (!string.IsNullOrEmpty(netGuid)) return $"ref:comp:{netGuid}";
                        }
                        return "ref:null";
                    }
                default: return null;
            }
        }

        public static void DeserializeProperty(SerializedProperty prop, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            int colon = value.IndexOf(':');
            if (colon < 0) return;
            string type = value.Substring(0, colon);
            string data = value.Substring(colon + 1);

            switch (type)
            {
                case "int":
                    if (int.TryParse(data, out int ival)) prop.intValue = ival;
                    break;
                case "bool":
                    if (bool.TryParse(data, out bool bval)) prop.boolValue = bval;
                    break;
                case "float":
                    if (TryF(data, out float fval)) prop.floatValue = fval;
                    break;
                case "string":
                    prop.stringValue = data;
                    break;
                case "enum":
                    if (int.TryParse(data, out int eIdx)) prop.enumValueIndex = eIdx;
                    break;
                case "mask":
                    if (int.TryParse(data, out int mval)) prop.intValue = mval;
                    break;
                case "color":
                    {
                        var p = data.Split(',');
                        if (p.Length == 4 && TryF(p[0], out float r) && TryF(p[1], out float g) &&
                            TryF(p[2], out float b) && TryF(p[3], out float a))
                            prop.colorValue = new Color(r, g, b, a);
                        break;
                    }
                case "v2":
                    {
                        var p = data.Split(',');
                        if (p.Length == 2 && TryF(p[0], out float x) && TryF(p[1], out float y))
                            prop.vector2Value = new Vector2(x, y);
                        break;
                    }
                case "v3":
                    {
                        var p = data.Split(',');
                        if (p.Length == 3 && TryF(p[0], out float x) && TryF(p[1], out float y) && TryF(p[2], out float z))
                            prop.vector3Value = new Vector3(x, y, z);
                        break;
                    }
                case "v4":
                    {
                        var p = data.Split(',');
                        if (p.Length == 4 && TryF(p[0], out float x) && TryF(p[1], out float y) &&
                            TryF(p[2], out float z) && TryF(p[3], out float w))
                            prop.vector4Value = new Vector4(x, y, z, w);
                        break;
                    }
                case "quat":
                    {
                        var p = data.Split(',');
                        if (p.Length == 4 && TryF(p[0], out float x) && TryF(p[1], out float y) &&
                            TryF(p[2], out float z) && TryF(p[3], out float w))
                            prop.quaternionValue = new Quaternion(x, y, z, w);
                        break;
                    }
                case "rect":
                    {
                        var p = data.Split(',');
                        if (p.Length == 4 && TryF(p[0], out float x) && TryF(p[1], out float y) &&
                            TryF(p[2], out float wi) && TryF(p[3], out float h))
                            prop.rectValue = new Rect(x, y, wi, h);
                        break;
                    }
                case "curve":
                    {
                        if (data == "") break;
                        var keyStrs = data.Split(';');
                        var keys = new List<Keyframe>();
                        foreach (var ks in keyStrs)
                        {
                            var kp = ks.Split(':');
                            if (kp.Length == 4 && TryF(kp[0], out float t) && TryF(kp[1], out float v) &&
                                TryF(kp[2], out float inT) && TryF(kp[3], out float outT))
                                keys.Add(new Keyframe(t, v, inT, outT));
                        }
                        prop.animationCurveValue = new AnimationCurve(keys.ToArray());
                        break;
                    }
                case "ref":
                    {
                        if (data == "null") { prop.objectReferenceValue = null; break; }

                        if (data.StartsWith("guidlocal:"))
                        {
                            string rest = data.Substring(10);
                            int sep = rest.IndexOf(':');
                            if (sep > 0)
                            {
                                string refGuid = rest.Substring(0, sep);
                                if (long.TryParse(rest.Substring(sep + 1), out long localId))
                                {
                                    string assetPath = AssetDatabase.GUIDToAssetPath(refGuid);
                                    if (!string.IsNullOrEmpty(assetPath))
                                    {
                                        var allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                                        foreach (var a in allAssets)
                                        {
                                            if (a == null) continue;
                                            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out string g, out long l)
                                                && g == refGuid && l == localId)
                                            {
                                                prop.objectReferenceValue = a;
                                                break;
                                            }
                                        }
                                        if (prop.objectReferenceValue == null)
                                            prop.objectReferenceValue = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                                    }
                                    else
                                    {
                                        var comp = prop.serializedObject.targetObject as Component;
                                        if (comp != null)
                                            _pendingRefs.Add(new PendingRefEntry { Comp = comp, PropertyPath = prop.propertyPath, AssetGuid = refGuid, LocalFileId = localId });
                                    }
                                }
                            }
                            break;
                        }
                        if (data.StartsWith("shader:"))
                        {
                            string shaderName = data.Substring(7);
                            Shader shader = Shader.Find(shaderName);
                            if (shader != null) prop.objectReferenceValue = shader;
                            break;
                        }
                        if (data.StartsWith("scene:")) { prop.objectReferenceValue = TeamCreateIdentity.FindByGuid(data.Substring(6)); break; }
                        if (data.StartsWith("comp:")) { prop.objectReferenceValue = TeamCreateIdentity.FindComponentByGuid(data.Substring(5)); break; }
                        break;
                    }
            }
        }

        private static bool TryF(string s, out float result) =>
            float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result);

        private class PendingRefEntry
        {
            public Component Comp;
            public string PropertyPath;
            public string AssetGuid;
            public long LocalFileId;
        }

        private static readonly List<PendingRefEntry> _pendingRefs = new List<PendingRefEntry>();
        private static readonly HashSet<string> _requestedAssetGuids = new HashSet<string>();
        private static double _lastAssetRequestTime;
        private const double AssetRequestCooldownSeconds = 2.0;

        public static void ClearPendingRefs()
        {
            _pendingRefs.Clear();
            _requestedAssetGuids.Clear();
            _deferredState = null;
            _pendingLocalPaths = null;
        }

        public static void RetryPendingRefs()
        {
            if (_pendingRefs.Count == 0) return;
            var resolved = new List<int>();
            var unresolvedGuids = new List<string>();
            for (int i = 0; i < _pendingRefs.Count; i++)
            {
                var entry = _pendingRefs[i];
                if (entry.Comp == null) { resolved.Add(i); continue; }
                string assetPath = AssetDatabase.GUIDToAssetPath(entry.AssetGuid);
                if (string.IsNullOrEmpty(assetPath))
                {
                    if (!_requestedAssetGuids.Contains(entry.AssetGuid))
                        unresolvedGuids.Add(entry.AssetGuid);
                    continue;
                }

                TeamCreateSession.IsApplyingRemoteChange = true;
                try
                {
                    var so = new SerializedObject(entry.Comp);
                    var prop = so.FindProperty(entry.PropertyPath);
                    if (prop != null)
                    {
                        UnityEngine.Object found = null;
                        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                        {
                            if (a == null) continue;
                            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out string g, out long l)
                                && g == entry.AssetGuid && l == entry.LocalFileId)
                            { found = a; break; }
                        }
                        if (found == null)
                            found = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                        if (found != null)
                        {
                            prop.objectReferenceValue = found;
                            so.ApplyModifiedPropertiesWithoutUndo();
                            if (entry.Comp != null && entry.Comp.gameObject != null)
                                EditorSceneManager.MarkSceneDirty(entry.Comp.gameObject.scene);
                        }
                    }
                }
                finally { TeamCreateSession.IsApplyingRemoteChange = false; }
                resolved.Add(i);
            }
            for (int i = resolved.Count - 1; i >= 0; i--)
                _pendingRefs.RemoveAt(resolved[i]);

            RequestMissingAssets(unresolvedGuids);
        }

        private static void RequestMissingAssets(List<string> unresolvedGuids)
        {
            if (unresolvedGuids.Count == 0) return;
            if (!TeamCreateSession.IsConnected || TeamCreateSession.IsHosting) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastAssetRequestTime < AssetRequestCooldownSeconds) return;
            _lastAssetRequestTime = now;

            foreach (var guid in unresolvedGuids)
                _requestedAssetGuids.Add(guid);

            TeamCreateLogger.Log($"[TeamCreate] Requesting {unresolvedGuids.Count} missing assets from host for unresolved component references.");
        }

        private static List<AssetManifestEntry> BuildAssetManifest()
        {
            var manifest = new List<AssetManifestEntry>();
            string assetsRoot = Application.dataPath;
            var files = Directory.GetFiles(assetsRoot, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                string relative = "Assets" + file.Substring(assetsRoot.Length).Replace('\\', '/');
                if (TeamCreateAssetSync.IsExcluded(relative)) continue;
                try
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    manifest.Add(new AssetManifestEntry
                    {
                        RelativePath = relative,
                        Md5Hash = TeamCreateAssetSync.ComputeMd5(bytes),
                        AssetGuid = AssetDatabase.AssetPathToGUID(relative)
                    });
                }
                catch { }
            }
            return manifest;
        }

        public static void SendRequestedAssets(PeerConnection peer, NetworkMessage request)
        {
            var req = Protocol.Deserialize<FullStateAssetRequestPayload>(request.Payload);
            if (req == null) return;
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;


            var guidMap = new Dictionary<string, string>(req.RelativePaths.Count);
            foreach (var rp in req.RelativePaths)
            {
                if (!TeamCreateAssetSync.IsExcluded(rp))
                    guidMap[rp] = AssetDatabase.AssetPathToGUID(rp);
            }

            var paths = new List<string>(req.RelativePaths);
            string localPeerId = TeamCreateSession.LocalPeerId;


            Task.Run(() =>
            {
                foreach (var relativePath in paths)
                {
                    if (TeamCreateAssetSync.IsExcluded(relativePath)) continue;

                    string bundleId = Guid.NewGuid().ToString("N");


                    string metaPath = relativePath + ".meta";
                    string absMetaPath = Path.Combine(projectRoot, metaPath);
                    if (File.Exists(absMetaPath))
                    {
                        try
                        {
                            byte[] metaContent = File.ReadAllBytes(absMetaPath);
                            byte[] metaSend = TeamCreateAssetSync.TryCompress(metaContent, out bool metaComp);
                            var metaPayload = new AssetModifiedPayload
                            {
                                RelativePath = metaPath,
                                FileGuid = "",
                                Content = metaSend,
                                BundleId = bundleId,
                                IsCompressed = metaComp
                            };
                            peer.SendMessage(Protocol.CreateMessage(MessageType.ASSET_MODIFIED, localPeerId, Protocol.Serialize(metaPayload)));
                        }
                        catch { }
                    }


                    string absPath = Path.Combine(projectRoot, relativePath);
                    if (!File.Exists(absPath)) continue;
                    try
                    {
                        byte[] content = File.ReadAllBytes(absPath);
                        string fileGuid = guidMap.TryGetValue(relativePath, out var g) ? g : "";
                        TeamCreateLogger.LogSend("ASSET (requested)", relativePath);
                        TeamCreateAssetSync.SendFileToPeerDirect(peer, relativePath, fileGuid, content, MessageType.ASSET_CREATED, localPeerId, bundleId);
                    }
                    catch (Exception e) { Debug.LogError($"[TeamCreate] Failed to send asset '{relativePath}': {e.Message}"); }
                }
            });
        }

        public static void ApplyFullState(NetworkMessage message)
        {
            var state = Protocol.Deserialize<FullStateResponsePayload>(message.Payload);
            if (state == null) { Debug.LogError("[TeamCreate] Received null full state payload."); return; }

            _deferredState = null;
            _pendingLocalPaths = null;
            TeamCreateAssetSync.ClearReceiveBundles();
            TeamCreateConflictResolver.Clear();
            TeamCreateIdentity.ClearSession();

            int myVersion = ++_applyFullStateVersion;
            var manifest = state.AssetManifest ?? new List<AssetManifestEntry>();
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            if (manifest.Count == 0)
            {
                ApplySceneData(state);
                TeamCreateSession.SetState(Editor.SessionState.Connected);
                return;
            }


            Task.Run(() =>
            {
                var pathsToRequest = new List<string>();
                var pendingLocalPaths = new HashSet<string>();

                foreach (var entry in manifest)
                {
                    string localPath = TeamCreateSession.NetworkToLocalPath(entry.RelativePath);
                    if (TeamCreateAssetSync.IsExcluded(localPath)) continue;
                    string absPath = Path.Combine(projectRoot, localPath);
                    bool needsUpdate = true;
                    if (File.Exists(absPath))
                    {
                        try { needsUpdate = TeamCreateAssetSync.ComputeMd5(File.ReadAllBytes(absPath)) != entry.Md5Hash; }
                        catch { }
                    }
                    if (needsUpdate)
                    {
                        pathsToRequest.Add(entry.RelativePath);
                        pendingLocalPaths.Add(localPath);
                    }
                }

                TeamCreateSession.EnqueueMainThread(() =>
                {
                    if (myVersion != _applyFullStateVersion) return;

                    if (pathsToRequest.Count > 0)
                    {
                        var assetReq = new FullStateAssetRequestPayload { RelativePaths = pathsToRequest };
                        TeamCreateLogger.LogSend("FULL_STATE_ASSET_REQUEST", $"{pathsToRequest.Count} assets");
                        TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.FULL_STATE_ASSET_REQUEST,
                            TeamCreateSession.LocalPeerId, Protocol.Serialize(assetReq)));

                        _deferredState = state;
                        _pendingLocalPaths = pendingLocalPaths;
                        _deferredStartTime = EditorApplication.timeSinceStartup;
                        Debug.Log($"[TeamCreate] Waiting for {pendingLocalPaths.Count} assets before building scene.");
                    }
                    else
                    {
                        ApplySceneData(state);
                    }

                    TeamCreateSession.SetState(Editor.SessionState.Connected);
                });
            });
        }

        private static void ApplySceneData(FullStateResponsePayload state)
        {
            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                bool firstScene = true;
                Scene activeScene = default;
                foreach (var sceneData in state.Scenes ?? new List<SceneData>())
                {
                    Scene scene = firstScene
                        ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single)
                        : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    firstScene = false;
                    if (!string.IsNullOrEmpty(sceneData.SceneName)) scene.name = sceneData.SceneName;
                    if (sceneData.IsActive) activeScene = scene;
                    ReconstructHierarchy(scene, sceneData.GameObjects ?? new List<GameObjectData>());
                }
                if (activeScene.IsValid()) SceneManager.SetActiveScene(activeScene);
                RetryPendingRefs();
            }
            catch (Exception e) { Debug.LogError($"[TeamCreate] Failed to apply full state: {e.Message}"); }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }

            Debug.Log("[TeamCreate] Full state applied. Session is live.");

            if (!TeamCreateSession.IsHosting)
                TeamCreateHierarchySync.ScheduleLightmapSyncRequest();
        }

        public static void ReconstructHierarchy(Scene scene, List<GameObjectData> objects)
        {
            var guidToGo = new Dictionary<string, GameObject>();
            var sorted = new List<GameObjectData>(objects);
            sorted.Sort((a, b) => CountDepth(a, objects) - CountDepth(b, objects));

            foreach (var goData in sorted)
            {
                var go = new GameObject(goData.Name);
                go.layer = goData.Layer;
                try { go.tag = goData.Tag; } catch { }

                if (!string.IsNullOrEmpty(goData.ParentGuid) && guidToGo.TryGetValue(goData.ParentGuid, out var parent))
                    go.transform.SetParent(parent.transform, false);
                else
                    EditorSceneManager.MoveGameObjectToScene(go, scene);

                go.transform.SetSiblingIndex(goData.SiblingIndex);
                go.SetActive(goData.IsActive);
                TeamCreateIdentity.AssignGuid(go, goData.Guid);
                guidToGo[goData.Guid] = go;

                ApplyComponentsToGameObject(go, goData.Components);
            }
            EditorSceneManager.MarkSceneDirty(scene);
        }

        public static void ApplyComponentsToGameObject(GameObject go, List<ComponentData> components)
        {
            if (components == null) return;
            foreach (var compData in components)
            {
                Type compType = TeamCreateHierarchySync.ResolveType(compData.TypeName);
                if (compType == null)
                {
                    Debug.LogWarning($"[TeamCreate] Cannot resolve type: {compData.TypeName}");
                    continue;
                }

                Component comp;
                if (compType == typeof(Transform))
                    comp = go.transform;
                else
                {
                    comp = go.GetComponent(compType);
                    if (comp == null)
                    {
                        try { comp = ObjectFactory.AddComponent(go, compType); }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[TeamCreate] ObjectFactory.AddComponent failed for '{compType.FullName}' on '{go.name}': {e.Message}");
                            try { comp = go.AddComponent(compType); } catch { }
                        }
                    }
                }

                if (comp == null) { Debug.LogWarning($"[TeamCreate] Could not add '{compType.FullName}' on '{go.name}'"); continue; }

                TeamCreateIdentity.AssignComponentGuid(comp, compData.ComponentGuid);
                if (compData.Properties == null) continue;
                var so = new SerializedObject(comp);
                var sortedProps = new List<KeyValuePair<string, string>>(compData.Properties);
                sortedProps.Sort((a, b) =>
                {
                    bool aSize = a.Key.EndsWith(".Array.size", StringComparison.Ordinal);
                    bool bSize = b.Key.EndsWith(".Array.size", StringComparison.Ordinal);
                    if (aSize != bSize) return aSize ? -1 : 1;
                    return string.Compare(a.Key, b.Key, StringComparison.Ordinal);
                });
                foreach (var kvp in sortedProps)
                {
                    var prop = so.FindProperty(kvp.Key);
                    if (prop != null) try { DeserializeProperty(prop, kvp.Value); } catch { }
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static int CountDepth(GameObjectData go, List<GameObjectData> all)
        {
            int depth = 0;
            string current = go.ParentGuid;
            while (!string.IsNullOrEmpty(current))
            {
                depth++;
                var parent = all.Find(g => g.Guid == current);
                if (parent == null) break;
                current = parent.ParentGuid;
                if (depth > 1000) break;
            }
            return depth;
        }
    }
}
#endif
