#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public static class TeamCreateHierarchySync
    {
        private static readonly HashSet<int> KnownInstanceIds = new HashSet<int>();
        private static readonly Dictionary<string, int> PendingCreations = new Dictionary<string, int>();
        private static readonly HashSet<string> PendingGuids = new HashSet<string>();
        private static readonly Dictionary<int, HashSet<int>> KnownComponentIds = new Dictionary<int, HashSet<int>>();
        private static readonly Dictionary<int, string> CompIdToTypeName = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> CompIdToCompGuid = new Dictionary<int, string>();

        private static HashSet<string> _knownTags = new HashSet<string>();

        private static readonly Dictionary<string, int> _staticFlagsSnapshot = new Dictionary<string, int>();

        private static double _lightmapSyncRequestTime = -1;

        private static double _lastScanTime;
        private const double ScanIntervalSeconds = 0.15;

        private static double _lastTagScanTime;
        private const double TagScanIntervalSeconds = 2.0;

        private static double _lastPendingRefRetryTime;
        private const double PendingRefRetryIntervalSeconds = 0.25;

        private class PendingAssetBroadcast
        {
            public UnityEngine.Object DirtyObject;
            public double ScheduledTime;
        }

        private static readonly Dictionary<string, PendingAssetBroadcast> PendingAssetBroadcasts = new Dictionary<string, PendingAssetBroadcast>();
        private const double AssetBroadcastDebounceSeconds = 0.05;

        private static double _lastAnimatorParamScanTime;
        private const double AnimatorParamScanIntervalSeconds = 1.0 / 45.0;
        private static readonly Dictionary<string, string> AnimatorParamSnapshots = new Dictionary<string, string>();

        private static double _lastMaterialScanTime;
        private const double MaterialScanIntervalSeconds = 1.0 / 45.0;
        private static readonly Dictionary<string, string> MaterialPropertySnapshots = new Dictionary<string, string>();
        private static readonly Dictionary<string, double> RecentlyReceivedMaterials = new Dictionary<string, double>();
        private const double MaterialReceiveCooldownSeconds = 4.0;

        private class MatPropTarget
        {
            public string Type;
            public float[] Current;
            public float[] Target;
            public double LastUpdateTime;
        }

        private class MatInterp
        {
            public Material Mat;
            public Dictionary<string, MatPropTarget> Props = new Dictionary<string, MatPropTarget>();
            public bool PendingSave;
        }

        private static readonly Dictionary<string, MatInterp> MatInterpStates = new Dictionary<string, MatInterp>();
        private static double _matInterpPrevTime;
        private const double MatInterpExpirySeconds = 3.0;
        private const float MatInterpConvergenceThreshold = 0.0001f;

        private class PendingPropertyBroadcast
        {
            public Component Comp;
            public Dictionary<string, string> Properties;
            public long Timestamp;
            public double ScheduledTime;
        }
        private static readonly Dictionary<string, PendingPropertyBroadcast> PendingPropertyBroadcasts = new Dictionary<string, PendingPropertyBroadcast>();
        private const double PropertyBroadcastDebounceSeconds = 1.0 / 45.0;

        private static readonly Dictionary<string, bool[]> _visibilitySnapshot = new Dictionary<string, bool[]>();

        private class PendingReparentItem
        {
            public GameObjectReparentedPayload Payload;
            public double ReceivedTime;
        }
        private static readonly List<PendingReparentItem> _pendingReparents = new List<PendingReparentItem>();
        private const double PendingReparentTimeoutSeconds = 10.0;

        private static readonly Dictionary<string, (string ownerId, double expiryTime)> _transformOwners =
            new Dictionary<string, (string, double)>();
        private const double TransformOwnershipExpirySeconds = 2.0;

        static TeamCreateHierarchySync()
        {
            _knownTags = new HashSet<string>(InternalEditorUtility.tags);

            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.update += OnUpdate;
#if UNITY_2021_2_OR_NEWER
            ObjectChangeEvents.changesPublished += OnObjectChangesPublished;
#endif
            Undo.postprocessModifications += OnPostprocessModifications;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            AssemblyReloadEvents.beforeAssemblyReload += Unregister;
            SceneVisibilityManager.visibilityChanged += OnVisibilityOrPickingChanged;
#if UNITY_2020_1_OR_NEWER
            SceneVisibilityManager.pickingChanged += OnVisibilityOrPickingChanged;
#endif
            Selection.selectionChanged += OnEditorSelectionChanged;
#if UNITY_2018_4_OR_NEWER
            UnityEditor.Lightmapping.bakeCompleted += OnLightmapBakeCompleted;
#endif
            RebuildKnownObjects();
        }

        private static void Unregister()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.update -= OnUpdate;
#if UNITY_2021_2_OR_NEWER
            ObjectChangeEvents.changesPublished -= OnObjectChangesPublished;
#endif
            Undo.postprocessModifications -= OnPostprocessModifications;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            AssemblyReloadEvents.beforeAssemblyReload -= Unregister;
            SceneVisibilityManager.visibilityChanged -= OnVisibilityOrPickingChanged;
#if UNITY_2020_1_OR_NEWER
            SceneVisibilityManager.pickingChanged -= OnVisibilityOrPickingChanged;
#endif
            Selection.selectionChanged -= OnEditorSelectionChanged;
#if UNITY_2018_4_OR_NEWER
            UnityEditor.Lightmapping.bakeCompleted -= OnLightmapBakeCompleted;
#endif
            PendingAssetBroadcasts.Clear();
            PendingPropertyBroadcasts.Clear();
            AnimatorParamSnapshots.Clear();
            MaterialPropertySnapshots.Clear();
            _staticFlagsSnapshot.Clear();
            _lightmapSyncRequestTime = -1;
            RecentlyReceivedMaterials.Clear();
            MatInterpStates.Clear();
            _visibilitySnapshot.Clear();
            _transformOwners.Clear();
            _pendingReparents.Clear();
            _matInterpPrevTime = 0;
            _flushScheduled = false;
        }

        private static void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            float matDt = _matInterpPrevTime > 0 ? Mathf.Clamp((float)(now - _matInterpPrevTime), 0.0001f, 0.1f) : 0.016f;
            _matInterpPrevTime = now;

            if (TeamCreateSession.IsConnected && !TeamCreateSession.IsApplyingRemoteChange)
            {
                if (PendingAssetBroadcasts.Count > 0)
                {
                    var readyPaths = new List<string>();
                    foreach (var kvp in PendingAssetBroadcasts)
                        if (now >= kvp.Value.ScheduledTime) readyPaths.Add(kvp.Key);
                    foreach (string path in readyPaths)
                    {
                        var pending = PendingAssetBroadcasts[path];
                        PendingAssetBroadcasts.Remove(path);
                        FlushAssetBroadcast(pending.DirtyObject, path);
                    }
                }

                if (PendingPropertyBroadcasts.Count > 0)
                {
                    var readyKeys = new List<string>();
                    foreach (var kvp in PendingPropertyBroadcasts)
                        if (now >= kvp.Value.ScheduledTime) readyKeys.Add(kvp.Key);
                    foreach (string key in readyKeys)
                    {
                        var pp = PendingPropertyBroadcasts[key];
                        PendingPropertyBroadcasts.Remove(key);
                        if (pp.Comp != null && pp.Comp.gameObject != null)
                            FlushPropertyBroadcast(pp.Comp, pp.Properties, pp.Timestamp);
                    }
                }

                if (now - _lastScanTime >= ScanIntervalSeconds)
                {
                    _lastScanTime = now;
                    int sceneCount = EditorSceneManager.sceneCount;
                    for (int i = 0; i < sceneCount; i++)
                    {
                        var scene = EditorSceneManager.GetSceneAt(i);
                        if (!scene.IsValid() || !scene.isLoaded) continue;
                        foreach (var root in scene.GetRootGameObjects())
                            ScanComponentsRecursive(root);
                    }
                }

                if (_lightmapSyncRequestTime > 0 && now >= _lightmapSyncRequestTime)
                {
                    _lightmapSyncRequestTime = -1;
                    TeamCreateSession.SendMessage(Protocol.CreateMessage(
                        MessageType.LIGHTMAP_SYNC_REQUEST,
                        TeamCreateSession.LocalPeerId,
                        null));
                }

                if (now - _lastTagScanTime >= TagScanIntervalSeconds)
                {
                    _lastTagScanTime = now;
                    CheckTagChanges();
                }

                if (now - _lastPendingRefRetryTime >= PendingRefRetryIntervalSeconds)
                {
                    _lastPendingRefRetryTime = now;
                    TeamCreateSnapshotBuilder.RetryPendingRefs();
                    RetryPendingReparents();
                }

                if (now - _lastAnimatorParamScanTime >= AnimatorParamScanIntervalSeconds)
                {
                    _lastAnimatorParamScanTime = now;
                    ScanAnimatorControllerParameters();
                }

                if (now - _lastMaterialScanTime >= MaterialScanIntervalSeconds)
                {
                    _lastMaterialScanTime = now;
                    ScanMaterialProperties(now);
                }

                if (RecentlyReceivedMaterials.Count > 0)
                {
                    var expired = new List<string>();
                    foreach (var kvp in RecentlyReceivedMaterials)
                        if (now - kvp.Value > MaterialReceiveCooldownSeconds) expired.Add(kvp.Key);
                    foreach (var k in expired) RecentlyReceivedMaterials.Remove(k);
                }
            }

            if (MatInterpStates.Count > 0)
            {
                float smoothT = 1f - Mathf.Exp(-20f * matDt);
                var deadPaths = new List<string>();

                foreach (var kvp in MatInterpStates)
                {
                    var interp = kvp.Value;
                    if (interp.Mat == null) { deadPaths.Add(kvp.Key); continue; }

                    bool allConverged = true;
                    bool changed = false;

                    TeamCreateSession.IsApplyingRemoteChange = true;
                    try
                    {
                        foreach (var propKvp in interp.Props)
                        {
                            var pt = propKvp.Value;
                            if (pt.Target == null || pt.Current == null) continue;
                            if (now - pt.LastUpdateTime > MatInterpExpirySeconds) continue;

                            bool propConverged = true;
                            for (int i = 0; i < pt.Current.Length; i++)
                            {
                                float newVal = Mathf.Lerp(pt.Current[i], pt.Target[i], smoothT);
                                if (Mathf.Abs(newVal - pt.Target[i]) > MatInterpConvergenceThreshold)
                                    propConverged = false;
                                pt.Current[i] = newVal;
                            }

                            if (propConverged)
                                for (int i = 0; i < pt.Current.Length; i++)
                                    pt.Current[i] = pt.Target[i];
                            else
                                allConverged = false;

                            ApplyMatProp(interp.Mat, propKvp.Key, pt);
                            changed = true;
                        }

                        if (changed)
                        {
                            EditorUtility.SetDirty(interp.Mat);
                            interp.PendingSave = true;
                        }

                        if (allConverged && interp.PendingSave)
                        {
                            SaveAndSnapshotMaterial(kvp.Key, interp.Mat);
                            deadPaths.Add(kvp.Key);
                        }
                    }
                    finally { TeamCreateSession.IsApplyingRemoteChange = false; }
                }

                foreach (var k in deadPaths) MatInterpStates.Remove(k);
            }
        }

        private static void ScanComponentsRecursive(GameObject go)
        {
            string guid = TeamCreateIdentity.GetGuid(go);
            if (!string.IsNullOrEmpty(guid) && !PendingGuids.Contains(guid))
            {
                BroadcastStructureChanged(go);
                CheckAndBroadcastStaticFlags(go, guid);
            }
            foreach (Transform child in go.transform)
                ScanComponentsRecursive(child.gameObject);
        }

        private static void CheckAndBroadcastStaticFlags(GameObject go, string guid = null)
        {
            if (go == null) return;
            if (guid == null) guid = TeamCreateIdentity.GetGuid(go);
            if (string.IsNullOrEmpty(guid)) return;
            int flags = (int)GameObjectUtility.GetStaticEditorFlags(go);
            if (!_staticFlagsSnapshot.TryGetValue(guid, out int prev))
            {
                _staticFlagsSnapshot[guid] = flags;
                return;
            }
            if (prev == flags) return;
            _staticFlagsSnapshot[guid] = flags;
            BroadcastGameObjectPropertyChanged(go);
        }

        private static void RebuildKnownObjects()
        {
            KnownInstanceIds.Clear();
            KnownComponentIds.Clear();
            int sceneCount = EditorSceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    CollectInstanceIds(root);
            }
        }

        private static void CollectInstanceIds(GameObject go)
        {
            KnownInstanceIds.Add(go.GetInstanceID());
            SnapshotComponents(go);
            foreach (Transform child in go.transform)
                CollectInstanceIds(child.gameObject);
        }

        private static void SnapshotComponents(GameObject go)
        {
            int goId = go.GetInstanceID();
            var comps = go.GetComponents<Component>();
            var ids = new HashSet<int>();
            foreach (var comp in comps)
            {
                if (comp == null) continue;
                int cid = comp.GetInstanceID();
                ids.Add(cid);
                if (!CompIdToTypeName.ContainsKey(cid))
                {
                    CompIdToTypeName[cid] = comp.GetType().AssemblyQualifiedName;
                    CompIdToCompGuid[cid] = TeamCreateIdentity.GetOrAssignComponentGuid(comp);
                }
            }
            KnownComponentIds[goId] = ids;
        }

        private static void CheckTagChanges()
        {
            var currentTags = new HashSet<string>(InternalEditorUtility.tags);

            foreach (var tag in currentTags)
            {
                if (!_knownTags.Contains(tag))
                {
                    _knownTags.Add(tag);
                    BroadcastTagAdded(tag);
                }
            }

            var removed = new List<string>();
            foreach (var tag in _knownTags)
            {
                if (!currentTags.Contains(tag))
                    removed.Add(tag);
            }
            foreach (var tag in removed)
            {
                _knownTags.Remove(tag);
                BroadcastTagRemoved(tag);
            }
        }

        private static void BroadcastTagAdded(string tagName)
        {
            TeamCreateLogger.LogSend("TAG_ADDED", tagName);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.TAG_ADDED,
                TeamCreateSession.LocalPeerId,
                Protocol.Serialize(new TagAddedPayload { TagName = tagName })));
        }

        private static void BroadcastTagRemoved(string tagName)
        {
            TeamCreateLogger.LogSend("TAG_REMOVED", tagName);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.TAG_REMOVED,
                TeamCreateSession.LocalPeerId,
                Protocol.Serialize(new TagRemovedPayload { TagName = tagName })));
        }

        private static string SnapshotAnimatorControllerParams(UnityEditor.Animations.AnimatorController controller)
        {
            var so = new SerializedObject(controller);
            var paramsProp = so.FindProperty("m_AnimatorParameters");
            if (paramsProp == null) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < paramsProp.arraySize; i++)
            {
                var elem = paramsProp.GetArrayElementAtIndex(i);
                var nameProp = elem.FindPropertyRelative("m_Name");
                var typeProp = elem.FindPropertyRelative("m_Type");
                var floatProp = elem.FindPropertyRelative("m_DefaultFloat");
                var intProp = elem.FindPropertyRelative("m_DefaultInt");
                var boolProp = elem.FindPropertyRelative("m_DefaultBool");
                if (nameProp == null) continue;
                sb.Append(nameProp.stringValue).Append(':');
                if (typeProp != null) sb.Append(typeProp.intValue).Append(':');
                if (floatProp != null) sb.Append(floatProp.floatValue.ToString("R")).Append(':');
                if (intProp != null) sb.Append(intProp.intValue).Append(':');
                if (boolProp != null) sb.Append(boolProp.boolValue).Append(';');
            }
            return sb.ToString();
        }

        private static void ScanAnimatorControllerParameters()
        {
            var controllersToCheck = new HashSet<UnityEditor.Animations.AnimatorController>();

            foreach (var obj in Selection.objects)
            {
                if (obj is UnityEditor.Animations.AnimatorController ac) controllersToCheck.Add(ac);
            }
            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                var animator = go.GetComponent<Animator>();
                if (animator != null && animator.runtimeAnimatorController is UnityEditor.Animations.AnimatorController ctrl)
                    controllersToCheck.Add(ctrl);
            }

            try
            {
                var tracker = ActiveEditorTracker.sharedTracker;
                if (tracker != null)
                    foreach (var editor in tracker.activeEditors)
                        if (editor != null && editor.target is UnityEditor.Animations.AnimatorController tac)
                            controllersToCheck.Add(tac);
            }
            catch { }

            foreach (var controller in controllersToCheck)
            {
                string path = AssetDatabase.GetAssetPath(controller);
                if (string.IsNullOrEmpty(path) || TeamCreateAssetSync.IsExcluded(path)) continue;
                string snapshot = SnapshotAnimatorControllerParams(controller);
                if (AnimatorParamSnapshots.TryGetValue(path, out string last))
                {
                    if (snapshot != last)
                    {
                        AnimatorParamSnapshots[path] = snapshot;
                        BroadcastAssetModified(controller, path);
                    }
                }
                else
                {
                    AnimatorParamSnapshots[path] = snapshot;
                }
            }
        }

        private static void ScanMaterialProperties(double now)
        {
            var materialsToCheck = new HashSet<Material>();

            foreach (var obj in Selection.objects)
            {
                if (obj is Material mat) materialsToCheck.Add(mat);
            }
            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    if (r == null || r.sharedMaterials == null) continue;
                    foreach (var m in r.sharedMaterials)
                        if (m != null) materialsToCheck.Add(m);
                }
            }
            if (Selection.activeObject is Material activeMat)
                materialsToCheck.Add(activeMat);

            try
            {
                var tracker = ActiveEditorTracker.sharedTracker;
                if (tracker != null)
                    foreach (var editor in tracker.activeEditors)
                        if (editor != null && editor.target is Material tm)
                            materialsToCheck.Add(tm);
            }
            catch { }

            foreach (var material in materialsToCheck)
            {
                string assetPath = AssetDatabase.GetAssetPath(material);
                if (string.IsNullOrEmpty(assetPath) || TeamCreateAssetSync.IsExcluded(assetPath)) continue;
                if (!TeamCreateSession.IsInSessionScope(assetPath)) continue;

                if (RecentlyReceivedMaterials.ContainsKey(assetPath)) continue;

                string snapshot = SnapshotMaterialViaAPI(material);
                if (string.IsNullOrEmpty(snapshot)) continue;

                if (MaterialPropertySnapshots.TryGetValue(assetPath, out string last))
                {
                    if (snapshot != last)
                    {
                        MaterialPropertySnapshots[assetPath] = snapshot;
                        BroadcastMaterialPropertyChanged(material, assetPath);
                    }
                }
                else
                {
                    MaterialPropertySnapshots[assetPath] = snapshot;
                }
            }
        }

        private static string SnapshotMaterialViaAPI(Material mat)
        {
            if (mat == null || mat.shader == null) return null;
            var sb = new StringBuilder(256);

            sb.Append("sh:").Append(mat.shader.name).Append(';');
            sb.Append("rq:").Append(mat.renderQueue).Append(';');
            sb.Append("inst:").Append(mat.enableInstancing).Append(';');
            sb.Append("dsgi:").Append(mat.doubleSidedGI).Append(';');

            int propCount = UnityEditor.ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < propCount; i++)
            {
                string propName = UnityEditor.ShaderUtil.GetPropertyName(mat.shader, i);
                var propType = UnityEditor.ShaderUtil.GetPropertyType(mat.shader, i);

                switch (propType)
                {
                    case UnityEditor.ShaderUtil.ShaderPropertyType.TexEnv:
                        var tex = mat.GetTexture(propName);
                        sb.Append("t:").Append(propName).Append('=');
                        if (tex != null)
                        {
                            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tex, out string guid, out long lid))
                                sb.Append(guid).Append(':').Append(lid);
                            else
                                sb.Append(tex.GetInstanceID());
                            var off = mat.GetTextureOffset(propName);
                            var scl = mat.GetTextureScale(propName);
                            sb.Append(',').Append(off.x.ToString("R")).Append(',').Append(off.y.ToString("R"));
                            sb.Append(',').Append(scl.x.ToString("R")).Append(',').Append(scl.y.ToString("R"));
                        }
                        else sb.Append("null");
                        sb.Append(';');
                        break;
                    case UnityEditor.ShaderUtil.ShaderPropertyType.Float:
                    case UnityEditor.ShaderUtil.ShaderPropertyType.Range:
                        sb.Append("f:").Append(propName).Append('=')
                          .Append(mat.GetFloat(propName).ToString("R")).Append(';');
                        break;
                    case UnityEditor.ShaderUtil.ShaderPropertyType.Color:
                        var c = mat.GetColor(propName);
                        sb.Append("c:").Append(propName).Append('=')
                          .Append(c.r.ToString("R")).Append(',').Append(c.g.ToString("R")).Append(',')
                          .Append(c.b.ToString("R")).Append(',').Append(c.a.ToString("R")).Append(';');
                        break;
                    case UnityEditor.ShaderUtil.ShaderPropertyType.Vector:
                        var v = mat.GetVector(propName);
                        sb.Append("v:").Append(propName).Append('=')
                          .Append(v.x.ToString("R")).Append(',').Append(v.y.ToString("R")).Append(',')
                          .Append(v.z.ToString("R")).Append(',').Append(v.w.ToString("R")).Append(';');
                        break;
                }
            }

            {
                var allKws = new HashSet<string>(mat.shaderKeywords ?? new string[0]);
#if UNITY_2021_2_OR_NEWER
                foreach (var kw in mat.enabledKeywords)
                    allKws.Add(kw.name);
#endif
                if (allKws.Count > 0)
                {
                    var sorted = new List<string>(allKws);
                    sorted.Sort();
                    sb.Append("kw:");
                    foreach (var kw in sorted) sb.Append(kw).Append(',');
                    sb.Append(';');
                }
            }

            sb.Append("gi:").Append(mat.enableInstancing ? 1 : 0);
            return sb.ToString();
        }

        public static bool IsRecentlyReceivedMaterial(string localPath) =>
            RecentlyReceivedMaterials.ContainsKey(localPath);

        public static void FlushPendingMaterialsToDisk()
        {
            if (MatInterpStates.Count > 0)
            {
                var paths = new List<string>(MatInterpStates.Keys);
                foreach (var path in paths)
                {
                    var interp = MatInterpStates[path];
                    if (interp.Mat == null) continue;
                    TeamCreateSession.IsApplyingRemoteChange = true;
                    try
                    {
                        foreach (var pt in interp.Props.Values)
                        {
                            if (pt.Target == null) continue;
                            pt.Current = (float[])pt.Target.Clone();
                            ApplyMatProp(interp.Mat, interp.Props.Keys.GetEnumerator().Current, pt);
                        }
                        foreach (var kvp in interp.Props)
                            ApplyMatProp(interp.Mat, kvp.Key, kvp.Value);
                    }
                    finally { TeamCreateSession.IsApplyingRemoteChange = false; }
                    SaveAndSnapshotMaterial(path, interp.Mat);
                }
                MatInterpStates.Clear();
            }


            foreach (var path in new List<string>(MaterialPropertySnapshots.Keys))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || !EditorUtility.IsDirty(mat)) continue;
                TeamCreateAssetSync.MarkRecentlyBroadcast(path);
                TeamCreateSession.IsApplyingRemoteChange = true;
                try
                {
#if UNITY_2020_3_OR_NEWER
                    AssetDatabase.SaveAssetIfDirty(mat);
#else
                    AssetDatabase.SaveAssets();
#endif
                }
                finally { TeamCreateSession.IsApplyingRemoteChange = false; }
            }
        }

        public static void NotifyRemoteMaterialImported(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            RecentlyReceivedMaterials[assetPath] = EditorApplication.timeSinceStartup;


            EditorApplication.delayCall += () =>
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (mat != null)
                {
                    string snap = SnapshotMaterialViaAPI(mat);
                    if (!string.IsNullOrEmpty(snap))
                        MaterialPropertySnapshots[assetPath] = snap;
                }
            };
        }

        public static void ApplyTagAdded(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<TagAddedPayload>(message.Payload);
            if (payload == null || string.IsNullOrEmpty(payload.TagName)) return;
            TeamCreateLogger.LogRecv("TAG_ADDED", message.SenderId);
            AddProjectTag(payload.TagName);
        }

        public static void ApplyTagRemoved(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<TagRemovedPayload>(message.Payload);
            if (payload == null || string.IsNullOrEmpty(payload.TagName)) return;
            TeamCreateLogger.LogRecv("TAG_REMOVED", message.SenderId);
            RemoveProjectTag(payload.TagName);
        }

        private static void AddProjectTag(string tagName)
        {
            foreach (var t in InternalEditorUtility.tags)
                if (t == tagName) { _knownTags.Add(tagName); return; }

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
                if (tagManagerAssets == null || tagManagerAssets.Length == 0) return;
                var tagManager = new SerializedObject(tagManagerAssets[0]);
                var tagsProp = tagManager.FindProperty("tags");
                if (tagsProp == null) return;
                tagsProp.arraySize++;
                tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tagName;
                tagManager.ApplyModifiedProperties();
                _knownTags.Add(tagName);
            }
            catch (Exception e) { Debug.LogError($"[TeamCreate] Failed to add tag '{tagName}': {e.Message}"); }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        private static void RemoveProjectTag(string tagName)
        {
            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
                if (tagManagerAssets == null || tagManagerAssets.Length == 0) return;
                var tagManager = new SerializedObject(tagManagerAssets[0]);
                var tagsProp = tagManager.FindProperty("tags");
                if (tagsProp == null) return;
                for (int i = 0; i < tagsProp.arraySize; i++)
                {
                    if (tagsProp.GetArrayElementAtIndex(i).stringValue == tagName)
                    {
                        tagsProp.DeleteArrayElementAtIndex(i);
                        tagManager.ApplyModifiedProperties();
                        _knownTags.Remove(tagName);
                        break;
                    }
                }
            }
            catch (Exception e) { Debug.LogError($"[TeamCreate] Failed to remove tag '{tagName}': {e.Message}"); }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        private static void OnHierarchyChanged()
        {
            var currentIds = new HashSet<int>();
            int sceneCount = EditorSceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    CollectGoIds(root, currentIds);
            }

            if (TeamCreateSession.IsApplyingRemoteChange)
            {
                KnownInstanceIds.Clear();
                foreach (int id in currentIds) KnownInstanceIds.Add(id);
                foreach (int id in currentIds)
                {
                    var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                    if (go != null) SnapshotComponents(go);
                }
                return;
            }

            if (!TeamCreateSession.IsConnected)
            {
                KnownInstanceIds.Clear();
                foreach (int id in currentIds) KnownInstanceIds.Add(id);
                foreach (int id in currentIds)
                {
                    var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                    if (go != null) SnapshotComponents(go);
                }
                return;
            }

            foreach (int id in currentIds)
            {
                if (!KnownInstanceIds.Contains(id))
                {
                    var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                    if (go == null) continue;
                    string guid = TeamCreateIdentity.GetOrAssignGuid(go);
                    PendingCreations[guid] = id;
                    PendingGuids.Add(guid);
                }
            }

            foreach (int id in KnownInstanceIds)
            {
                if (!currentIds.Contains(id))
                    BroadcastGameObjectDeleted(id);
            }

            foreach (int id in currentIds)
            {
                if (!KnownInstanceIds.Contains(id)) continue;
                var existingGo = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (existingGo == null) continue;
                BroadcastStructureChanged(existingGo);
            }

            KnownInstanceIds.Clear();
            foreach (int id in currentIds) KnownInstanceIds.Add(id);

            if (PendingCreations.Count > 0)
                ScheduleFlush();
        }

        private static void CollectGoIds(GameObject go, HashSet<int> ids)
        {
            ids.Add(go.GetInstanceID());
            foreach (Transform child in go.transform)
                CollectGoIds(child.gameObject, ids);
        }

        private static bool _flushScheduled;

        private static void ScheduleFlush()
        {
            if (_flushScheduled) return;
            _flushScheduled = true;
            EditorApplication.delayCall += FlushPendingCreations;
        }

        private static void FlushPendingCreations()
        {
            _flushScheduled = false;
            if (PendingCreations.Count == 0) return;

            var toProcess = new Dictionary<string, int>(PendingCreations);
            PendingCreations.Clear();

            foreach (var kvp in toProcess)
            {
                string guid = kvp.Key;
                int instanceId = kvp.Value;
                PendingGuids.Remove(guid);

                if (TeamCreateSession.IsApplyingRemoteChange) continue;
                if (!TeamCreateSession.IsConnected) continue;

                var go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (go == null) continue;

                BroadcastGameObjectCreated(go);
                SnapshotComponents(go);
            }
        }

        private static void BroadcastGameObjectCreated(GameObject go)
        {
            string guid = TeamCreateIdentity.GetOrAssignGuid(go);
            string parentGuid = go.transform.parent != null
                ? TeamCreateIdentity.GetOrAssignGuid(go.transform.parent.gameObject)
                : null;

            var compList = new List<ComponentData>();
            var components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                var comp = components[i];
                if (comp == null) continue;
                string compGuid = TeamCreateIdentity.GetOrAssignComponentGuid(comp);
                int cid = comp.GetInstanceID();
                CompIdToTypeName[cid] = comp.GetType().AssemblyQualifiedName;
                CompIdToCompGuid[cid] = compGuid;

                var props = TeamCreateSnapshotBuilder.SerializeAllProperties(comp);
                TeamCreateLogger.Log($"SEND GAMEOBJECT_CREATED GO='{go.name}' Guid={guid} Comp={comp.GetType().FullName} Props={props.Count}");
                compList.Add(new ComponentData
                {
                    TypeName = comp.GetType().AssemblyQualifiedName,
                    ComponentGuid = compGuid,
                    Properties = props
                });
            }

            var payload = new GameObjectCreatedPayload
            {
                Guid = guid,
                Name = go.name,
                ParentGuid = parentGuid,
                SiblingIndex = go.transform.GetSiblingIndex(),
                Layer = go.layer,
                Tag = go.tag,
                IsActive = go.activeSelf,
                ScenePath = go.scene.path,
                SceneNetId = TeamCreateSceneSync.GetOrCreateNetIdForScene(go.scene),
                Components = compList
            };

            if (PrefabUtility.GetPrefabInstanceStatus(go) == PrefabInstanceStatus.Connected)
            {
                string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(prefabPath))
                    payload.PrefabAssetGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            }

            TeamCreateLogger.LogSend("GAMEOBJECT_CREATED", go.name);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.GAMEOBJECT_CREATED, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
        }

        private static void BroadcastGameObjectDeleted(int instanceId)
        {
            string guid = TeamCreateIdentity.GetGuidByInstanceId(instanceId);
            if (string.IsNullOrEmpty(guid)) return;
            KnownComponentIds.Remove(instanceId);
            TeamCreateLogger.LogSend("GAMEOBJECT_DELETED", guid);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.GAMEOBJECT_DELETED,
                TeamCreateSession.LocalPeerId,
                Protocol.Serialize(new GameObjectDeletedPayload { Guid = guid })));
        }

        private static void BroadcastStructureChanged(GameObject go)
        {
            if (go == null) return;
            int goId = go.GetInstanceID();

            var currentComps = go.GetComponents<Component>();
            var currentIds = new HashSet<int>();

            foreach (var comp in currentComps)
            {
                if (comp == null) continue;
                int cid = comp.GetInstanceID();
                currentIds.Add(cid);
                if (!CompIdToTypeName.ContainsKey(cid))
                {
                    CompIdToTypeName[cid] = comp.GetType().AssemblyQualifiedName;
                    CompIdToCompGuid[cid] = TeamCreateIdentity.GetOrAssignComponentGuid(comp);
                }
            }

            if (!KnownComponentIds.TryGetValue(goId, out var knownIds))
            {
                KnownComponentIds[goId] = new HashSet<int>(currentIds);
                return;
            }

            if (knownIds.SetEquals(currentIds))
                return;

            string goGuid = TeamCreateIdentity.GetOrAssignGuid(go);

            foreach (var comp in currentComps)
            {
                if (comp == null) continue;
                int cid = comp.GetInstanceID();
                if (!knownIds.Contains(cid))
                {
                    string compGuid = TeamCreateIdentity.GetOrAssignComponentGuid(comp);
                    var props = TeamCreateSnapshotBuilder.SerializeAllProperties(comp);
                    TeamCreateLogger.Log($"SEND COMPONENT_ADDED GO='{go.name}' Comp={comp.GetType().FullName} Props={props.Count}");
                    var payload = new ComponentAddedPayload
                    {
                        GameObjectGuid = goGuid,
                        ComponentTypeName = comp.GetType().AssemblyQualifiedName,
                        ComponentGuid = compGuid,
                        Properties = props
                    };
                    TeamCreateLogger.LogSend("COMPONENT_ADDED", go.name);
                    TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.COMPONENT_ADDED, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
                }
            }

            foreach (int oldId in knownIds)
            {
                if (!currentIds.Contains(oldId))
                {
                    CompIdToTypeName.TryGetValue(oldId, out string typeName);
                    CompIdToCompGuid.TryGetValue(oldId, out string compGuid);
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var payload = new ComponentRemovedPayload
                        {
                            GameObjectGuid = goGuid,
                            ComponentTypeName = typeName,
                            ComponentGuid = compGuid ?? ""
                        };
                        TeamCreateLogger.LogSend("COMPONENT_REMOVED", go.name);
                        TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.COMPONENT_REMOVED, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
                    }
                    CompIdToTypeName.Remove(oldId);
                    CompIdToCompGuid.Remove(oldId);
                }
            }

            KnownComponentIds[goId] = new HashSet<int>(currentIds);
        }

#if UNITY_2021_2_OR_NEWER
        private static void OnObjectChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (TeamCreateSession.IsApplyingRemoteChange) return;

            for (int i = 0; i < stream.length; i++)
            {
                var kind = stream.GetEventType(i);
                switch (kind)
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        {
                            if (!TeamCreateSession.IsConnected) break;
                            if (UnityEditor.AnimationMode.InAnimationMode()) break;
                            stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var ev);
                            var obj = EditorUtility.InstanceIDToObject(ev.instanceId);
                            if (obj is Transform) break;
                            if (obj is Component comp)
                                BroadcastComponentPropertyChanged(comp);
                            else if (obj is GameObject goObj)
                                BroadcastGameObjectPropertyChanged(goObj);
                            break;
                        }
                    case ObjectChangeKind.ChangeGameObjectParent:
                        {
                            if (!TeamCreateSession.IsConnected) break;
                            stream.GetChangeGameObjectParentEvent(i, out var ev);
                            var go = EditorUtility.InstanceIDToObject(ev.instanceId) as GameObject;
                            if (go != null) BroadcastGameObjectReparented(go);
                            break;
                        }
                    case ObjectChangeKind.ChangeGameObjectStructure:
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        {
                            if (!TeamCreateSession.IsConnected) break;
                            int instanceId;
                            if (kind == ObjectChangeKind.ChangeGameObjectStructure)
                            {
                                stream.GetChangeGameObjectStructureEvent(i, out var ev);
                                instanceId = ev.instanceId;
                            }
                            else
                            {
                                stream.GetChangeGameObjectStructureHierarchyEvent(i, out var ev);
                                instanceId = ev.instanceId;
                            }
                            var go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                            if (go == null) break;
                            BroadcastStructureChanged(go);
                            break;
                        }
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        {
                            if (!TeamCreateSession.IsConnected) break;
                            stream.GetChangeAssetObjectPropertiesEvent(i, out var ev);
                            var obj = EditorUtility.InstanceIDToObject(ev.instanceId);
                            if (obj == null) break;
                            if (obj is AssetImporter) break;
                            string assetPath = AssetDatabase.GetAssetPath(obj);
                            if (string.IsNullOrEmpty(assetPath) || TeamCreateAssetSync.IsExcluded(assetPath)) break;
                            if (assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!TeamCreateSession.IsInSessionScope(assetPath)) break;
                                if (RecentlyReceivedMaterials.ContainsKey(assetPath)) break;
                                var mat = obj as Material ?? AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                                if (mat != null)
                                {

                                    string snap = SnapshotMaterialViaAPI(mat);
                                    if (!string.IsNullOrEmpty(snap))
                                        MaterialPropertySnapshots[assetPath] = snap;
                                    BroadcastMaterialPropertyChanged(mat, assetPath);
                                }
                                break;
                            }
                            BroadcastAssetModified(obj, assetPath);
                            break;
                        }
                    case ObjectChangeKind.CreateAssetObject:
                        {
                            if (!TeamCreateSession.IsConnected) break;
                            stream.GetCreateAssetObjectEvent(i, out var ev);
                            var obj = EditorUtility.InstanceIDToObject(ev.instanceId);
                            if (obj == null) break;
                            if (obj is AssetImporter) break;
                            string assetPath = AssetDatabase.GetAssetPath(obj);
                            if (string.IsNullOrEmpty(assetPath) || TeamCreateAssetSync.IsExcluded(assetPath)) break;
                            if (assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) break;
                            BroadcastAssetModified(obj, assetPath);
                            break;
                        }
                    case ObjectChangeKind.DestroyAssetObject:
                        {
                            if (!TeamCreateSession.IsConnected) break;
                            stream.GetDestroyAssetObjectEvent(i, out var ev);
                            string assetPath = AssetDatabase.GUIDToAssetPath(ev.guid.ToString());
                            if (string.IsNullOrEmpty(assetPath) || TeamCreateAssetSync.IsExcluded(assetPath)) break;
                            var mainAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                            if (mainAsset == null) break;
                            BroadcastAssetModified(mainAsset, assetPath);
                            break;
                        }
                }
            }





            if (TeamCreateSession.IsConnected && !TeamCreateSession.IsApplyingRemoteChange)
                FlushAllPendingPropertyBroadcasts();
        }
#endif

        private static void FlushAllPendingPropertyBroadcasts()
        {
            if (PendingPropertyBroadcasts.Count == 0) return;
            var keys = new List<string>(PendingPropertyBroadcasts.Keys);
            foreach (var key in keys)
            {
                if (!PendingPropertyBroadcasts.TryGetValue(key, out var pp)) continue;
                PendingPropertyBroadcasts.Remove(key);
                if (pp.Comp != null && pp.Comp.gameObject != null)
                    FlushPropertyBroadcast(pp.Comp, pp.Properties, pp.Timestamp);
            }
        }

        private static void BroadcastAssetModified(UnityEngine.Object obj, string relativePath)
        {

            bool isMaterial = relativePath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
            double debounce = isMaterial ? 0.02 : AssetBroadcastDebounceSeconds;
            double fireTime = EditorApplication.timeSinceStartup + debounce;

            if (PendingAssetBroadcasts.TryGetValue(relativePath, out var existing))
            {
                existing.DirtyObject = obj;
                existing.ScheduledTime = fireTime;
            }
            else
            {
                PendingAssetBroadcasts[relativePath] = new PendingAssetBroadcast
                {
                    DirtyObject = obj,
                    ScheduledTime = fireTime
                };
            }
            EditorUtility.SetDirty(obj);
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(relativePath);
            if (mainAsset != null && mainAsset != obj) EditorUtility.SetDirty(mainAsset);
        }

        private static void FlushAssetBroadcast(UnityEngine.Object obj, string relativePath)
        {
            TeamCreateAssetSync.MarkRecentlyBroadcast(relativePath);

            try
            {
                if (obj != null)
                {
                    EditorUtility.SetDirty(obj);
#if UNITY_2020_3_OR_NEWER
                    AssetDatabase.SaveAssetIfDirty(obj);
#else
                    AssetDatabase.SaveAssets();
#endif
                }
                else
                {
                    AssetDatabase.SaveAssets();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TeamCreate] Save failed for '{relativePath}': {e.Message}");
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string absPath = Path.Combine(projectRoot, relativePath);
            if (!File.Exists(absPath))
            {
                Debug.LogWarning($"[TeamCreate] File not found after save: {absPath}");
                return;
            }
            try
            {
                string metaPath = relativePath + ".meta";
                string absMetaPath = Path.Combine(projectRoot, metaPath);
                if (File.Exists(absMetaPath))
                {
                    byte[] metaBytes = File.ReadAllBytes(absMetaPath);
                    TeamCreateAssetSync.MarkRecentlyBroadcast(metaPath);
                    TeamCreateAssetSync.SendFileDirectly(metaPath, metaBytes, MessageType.ASSET_MODIFIED);
                }
                byte[] content = File.ReadAllBytes(absPath);
                TeamCreateAssetSync.MarkRecentlyBroadcast(relativePath);
                TeamCreateAssetSync.SendFileDirectly(relativePath, content, MessageType.ASSET_MODIFIED);
            }
            catch (Exception e) { Debug.LogError($"[TeamCreate] Failed to broadcast asset '{relativePath}': {e.Message}"); }
        }

        private static void BroadcastMaterialPropertyChanged(Material mat, string assetPath)
        {
            if (mat == null || mat.shader == null) return;

            var entries = new List<MaterialPropertyEntry>();
            int propCount = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < propCount; i++)
            {
                string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                var propType = ShaderUtil.GetPropertyType(mat.shader, i);
                var entry = new MaterialPropertyEntry { Name = propName };

                switch (propType)
                {
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        entry.Type = "float";
                        entry.Value = mat.GetFloat(propName).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case ShaderUtil.ShaderPropertyType.Color:
                        entry.Type = "color";
                        var c = mat.GetColor(propName);
                        entry.Value = $"{c.r.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{c.g.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{c.b.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{c.a.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        entry.Type = "vector";
                        var v = mat.GetVector(propName);
                        entry.Value = $"{v.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{v.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{v.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{v.w.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
                        break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        entry.Type = "texture";
                        var tex = mat.GetTexture(propName);
                        if (tex != null)
                        {
                            string texAssetPath = AssetDatabase.GetAssetPath(tex);
                            string texNetworkPath = string.IsNullOrEmpty(texAssetPath) ? "" : (TeamCreateSession.LocalToNetworkPath(texAssetPath) ?? texAssetPath);
                            entry.Value = texNetworkPath;
                        }
                        else
                        {
                            entry.Value = "";
                        }
                        var off = mat.GetTextureOffset(propName);
                        var scl = mat.GetTextureScale(propName);
                        entry.TexOffsetX = off.x;
                        entry.TexOffsetY = off.y;
                        entry.TexScaleX = scl.x;
                        entry.TexScaleY = scl.y;
                        break;
                    default:
                        continue;
                }
                entries.Add(entry);
            }


            var keywordsSet = new HashSet<string>(mat.shaderKeywords ?? new string[0]);
#if UNITY_2021_2_OR_NEWER
            foreach (var kw in mat.enabledKeywords)
                keywordsSet.Add(kw.name);
#endif
            var keywords = new List<string>(keywordsSet);

            string shaderGuid = "";
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mat.shader, out string sg, out long _))
                shaderGuid = sg;

            string networkAssetPath = TeamCreateSession.LocalToNetworkPath(assetPath) ?? assetPath;
            var payload = new MaterialPropertyChangedPayload
            {
                RelativePath = networkAssetPath,
                ShaderName = mat.shader.name,
                ShaderGuid = shaderGuid,
                RenderQueue = mat.renderQueue,
                Properties = entries,
                EnabledKeywords = keywords,
                EnableInstancing = mat.enableInstancing,
                DoubleSidedGI = mat.doubleSidedGI
            };

            TeamCreateAssetSync.MarkRecentlyBroadcast(assetPath);
            TeamCreateLogger.LogSend("MATERIAL_PROPERTY_CHANGED", networkAssetPath);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(
                MessageType.MATERIAL_PROPERTY_CHANGED,
                TeamCreateSession.LocalPeerId,
                Protocol.Serialize(payload)));
        }

        public static void ApplyMaterialPropertyChanged(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<MaterialPropertyChangedPayload>(message.Payload);
            if (payload == null || string.IsNullOrEmpty(payload.RelativePath)) return;

            TeamCreateLogger.LogRecv("MATERIAL_PROPERTY_CHANGED", message.SenderId);

            string localMatPath = TeamCreateSession.NetworkToLocalPath(payload.RelativePath);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(localMatPath);
            if (mat == null) return;

            bool hasInterpolatable = false;
            bool hasImmediate = false;

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                Shader targetShader = null;
                if (!string.IsNullOrEmpty(payload.ShaderGuid))
                {
                    string shaderPath = AssetDatabase.GUIDToAssetPath(payload.ShaderGuid);
                    if (!string.IsNullOrEmpty(shaderPath))
                        targetShader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                }
                if (targetShader == null && !string.IsNullOrEmpty(payload.ShaderName))
                    targetShader = Shader.Find(payload.ShaderName);
                if (targetShader != null && mat.shader != targetShader)
                { mat.shader = targetShader; hasImmediate = true; }

                mat.renderQueue = payload.RenderQueue;

                if (payload.Properties != null)
                {
                    foreach (var entry in payload.Properties)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        try
                        {
                            switch (entry.Type)
                            {
                                case "float":
                                    if (float.TryParse(entry.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fVal))
                                    { QueueMatFloatInterp(localMatPath, mat, entry.Name, "float", new[] { fVal }); hasInterpolatable = true; }
                                    break;
                                case "color":
                                    var cparts = entry.Value.Split(',');
                                    if (cparts.Length == 4 &&
                                        float.TryParse(cparts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cr) &&
                                        float.TryParse(cparts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cg) &&
                                        float.TryParse(cparts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cb) &&
                                        float.TryParse(cparts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ca))
                                    { QueueMatFloatInterp(localMatPath, mat, entry.Name, "color", new[] { cr, cg, cb, ca }); hasInterpolatable = true; }
                                    break;
                                case "vector":
                                    var vparts = entry.Value.Split(',');
                                    if (vparts.Length == 4 &&
                                        float.TryParse(vparts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vx) &&
                                        float.TryParse(vparts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vy) &&
                                        float.TryParse(vparts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vz) &&
                                        float.TryParse(vparts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vw))
                                    { QueueMatFloatInterp(localMatPath, mat, entry.Name, "vector", new[] { vx, vy, vz, vw }); hasInterpolatable = true; }
                                    break;
                                case "texture":
                                    if (string.IsNullOrEmpty(entry.Value))
                                    {
                                        mat.SetTexture(entry.Name, null);
                                    }
                                    else
                                    {
                                        string texLocalPath = TeamCreateSession.NetworkToLocalPath(entry.Value);
                                        Texture loadedTex = AssetDatabase.LoadAssetAtPath<Texture>(texLocalPath);
                                        if (loadedTex == null)
                                        {
                                            foreach (var subAsset in AssetDatabase.LoadAllAssetsAtPath(texLocalPath))
                                                if (subAsset is Texture t) { loadedTex = t; break; }
                                        }
                                        if (loadedTex != null)
                                        {
                                            mat.SetTexture(entry.Name, loadedTex);
                                            mat.SetTextureOffset(entry.Name, new Vector2(entry.TexOffsetX, entry.TexOffsetY));
                                            mat.SetTextureScale(entry.Name, new Vector2(entry.TexScaleX, entry.TexScaleY));
                                        }
                                    }
                                    hasImmediate = true;
                                    break;
                            }
                        }
                        catch { }
                    }
                }

                if (payload.EnabledKeywords != null)
                {

                    foreach (var kw in mat.shaderKeywords ?? new string[0])
                        mat.DisableKeyword(kw);
#if UNITY_2021_2_OR_NEWER



                    if (mat.shader != null)
                    {
                        var enableSet = new HashSet<string>(payload.EnabledKeywords);
                        foreach (var shaderKw in mat.shader.keywordSpace.keywords)
                            mat.SetKeyword(shaderKw, enableSet.Contains(shaderKw.name));
                    }
                    else
                    {
                        foreach (var kw in payload.EnabledKeywords)
                            if (!string.IsNullOrEmpty(kw)) mat.EnableKeyword(kw);
                    }
#else
                    foreach (var kw in payload.EnabledKeywords)
                        if (!string.IsNullOrEmpty(kw)) mat.EnableKeyword(kw);
#endif
                    hasImmediate = true;
                }

                if (mat.enableInstancing != payload.EnableInstancing)
                { mat.enableInstancing = payload.EnableInstancing; hasImmediate = true; }
                if (mat.doubleSidedGI != payload.DoubleSidedGI)
                { mat.doubleSidedGI = payload.DoubleSidedGI; hasImmediate = true; }

                if (hasImmediate && !hasInterpolatable)
                {
                    TeamCreateAssetSync.MarkRecentlyBroadcast(localMatPath);
                    EditorUtility.SetDirty(mat);
#if UNITY_2020_3_OR_NEWER
                    AssetDatabase.SaveAssetIfDirty(mat);
#else
                    AssetDatabase.SaveAssets();
#endif



                    string _capturedPath = localMatPath;
                    Material _capturedMat = mat;
                    EditorApplication.delayCall += () =>
                    {
                        if (_capturedMat == null) return;
                        TeamCreateAssetSync.MarkRecentlyBroadcast(_capturedPath);

                        string snapImm = SnapshotMaterialViaAPI(_capturedMat);
                        if (!string.IsNullOrEmpty(snapImm))
                            MaterialPropertySnapshots[_capturedPath] = snapImm;
                        RecentlyReceivedMaterials[_capturedPath] = EditorApplication.timeSinceStartup;



                        AssetDatabase.ImportAsset(_capturedPath, ImportAssetOptions.ForceUpdate);
                    };
                }
                else if (hasImmediate)
                {
                    EditorUtility.SetDirty(mat);
                }


                if (!(hasImmediate && !hasInterpolatable))
                    RecentlyReceivedMaterials[localMatPath] = EditorApplication.timeSinceStartup;
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        private static void QueueMatFloatInterp(string assetPath, Material mat, string propName, string type, float[] target)
        {
            if (!MatInterpStates.TryGetValue(assetPath, out var interp))
            {
                interp = new MatInterp { Mat = mat };
                MatInterpStates[assetPath] = interp;
            }

            if (!interp.Props.TryGetValue(propName, out var pt))
            {
                float[] current = ReadMatProp(mat, propName, type);
                pt = new MatPropTarget
                {
                    Type = type,
                    Current = current ?? (float[])target.Clone(),
                    Target = (float[])target.Clone(),
                    LastUpdateTime = EditorApplication.timeSinceStartup
                };
                interp.Props[propName] = pt;
            }
            else
            {
                pt.Target = (float[])target.Clone();
                pt.LastUpdateTime = EditorApplication.timeSinceStartup;
            }
        }

        private static float[] ReadMatProp(Material mat, string propName, string type)
        {
            try
            {
                switch (type)
                {
                    case "float": return new[] { mat.GetFloat(propName) };
                    case "color": var c = mat.GetColor(propName); return new[] { c.r, c.g, c.b, c.a };
                    case "vector": var v = mat.GetVector(propName); return new[] { v.x, v.y, v.z, v.w };
                }
            }
            catch { }
            return null;
        }

        private static void ApplyMatProp(Material mat, string propName, MatPropTarget pt)
        {
            switch (pt.Type)
            {
                case "float":
                    if (pt.Current.Length >= 1) mat.SetFloat(propName, pt.Current[0]);
                    break;
                case "color":
                    if (pt.Current.Length >= 4) mat.SetColor(propName, new Color(pt.Current[0], pt.Current[1], pt.Current[2], pt.Current[3]));
                    break;
                case "vector":
                    if (pt.Current.Length >= 4) mat.SetVector(propName, new Vector4(pt.Current[0], pt.Current[1], pt.Current[2], pt.Current[3]));
                    break;
            }
        }

        private static void SaveAndSnapshotMaterial(string assetPath, Material mat)
        {
            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                TeamCreateAssetSync.MarkRecentlyBroadcast(assetPath);
#if UNITY_2020_3_OR_NEWER
                AssetDatabase.SaveAssetIfDirty(mat);
#else
                AssetDatabase.SaveAssets();
#endif


                string snap = SnapshotMaterialViaAPI(mat);
                if (!string.IsNullOrEmpty(snap))
                    MaterialPropertySnapshots[assetPath] = snap;
                RecentlyReceivedMaterials[assetPath] = EditorApplication.timeSinceStartup;
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        private static void OnUndoRedoPerformed()
        {
            if (!TeamCreateSession.IsConnected || TeamCreateSession.IsApplyingRemoteChange) return;
            TeamCreateLogger.Log("Undo/redo performed, broadcasting all changes.");


            _lastMaterialScanTime = 0;
            int sceneCount = EditorSceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    BroadcastAllChangesRecursive(root);
            }
        }

        private static void BroadcastAllChangesRecursive(GameObject go)
        {
            string guid = TeamCreateIdentity.GetGuid(go);
            if (string.IsNullOrEmpty(guid)) return;

            BroadcastStructureChanged(go);

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                BroadcastPropertyChanged(comp, TeamCreateSnapshotBuilder.SerializeAllProperties(comp), DateTime.UtcNow.Ticks);
            }
            foreach (Transform child in go.transform)
                BroadcastAllChangesRecursive(child.gameObject);
        }

        private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] mods)
        {
            if (!TeamCreateSession.IsConnected || TeamCreateSession.IsApplyingRemoteChange)
                return mods;

            var grouped = new Dictionary<Component, Dictionary<string, string>>();
            var structureCheckIds = new HashSet<int>();

            foreach (var mod in mods)
            {





                if (mod.previousValue != null &&
                    mod.previousValue.value == mod.currentValue.value &&
                    mod.previousValue.objectReference == mod.currentValue.objectReference) continue;

                var target = mod.currentValue.target;
                if (target is Component comp && comp != null && comp.gameObject != null)
                {
                    if (!grouped.ContainsKey(comp)) grouped[comp] = new Dictionary<string, string>();
                    grouped[comp][mod.currentValue.propertyPath] = null;

                    if (!CompIdToTypeName.ContainsKey(comp.GetInstanceID()))
                        structureCheckIds.Add(comp.gameObject.GetInstanceID());
                }
                else if (target is GameObject goTarget && goTarget != null)
                {
                    structureCheckIds.Add(goTarget.GetInstanceID());
                }
            }

            foreach (int goId in structureCheckIds)
            {
                var go = EditorUtility.InstanceIDToObject(goId) as GameObject;
                if (go == null) continue;
                string goGuid = TeamCreateIdentity.GetGuid(go);
                if (string.IsNullOrEmpty(goGuid) || PendingGuids.Contains(goGuid)) continue;
                BroadcastStructureChanged(go);
            }

            foreach (var kvp in grouped)
            {
                var comp = kvp.Key;
                if (comp == null || comp.gameObject == null) continue;
                var so = new SerializedObject(comp);
                var changed = new Dictionary<string, string>();
                foreach (var path in kvp.Value.Keys)
                {
                    var prop = so.FindProperty(path);
                    if (prop != null) try { changed[path] = TeamCreateSnapshotBuilder.SerializeProperty(prop); } catch { }
                }

                if (comp is Transform && HasAnyTransformSpatialKey(changed))
                {
                    if (AnimationMode.InAnimationMode()) continue;

                    ExpandToFullTransform(so, changed);

                    string tGoGuid = TeamCreateIdentity.GetGuid(comp.gameObject);
                    if (!string.IsNullOrEmpty(tGoGuid))
                    {
                        double tNow = EditorApplication.timeSinceStartup;
                        if (!_transformOwners.TryGetValue(tGoGuid, out var tOwn) ||
                            tOwn.ownerId == TeamCreateSession.LocalPeerId ||
                            tNow >= tOwn.expiryTime)
                        {
                            _transformOwners[tGoGuid] = (TeamCreateSession.LocalPeerId, tNow + TransformOwnershipExpirySeconds);
                        }
                    }
                }

                if (changed.Count > 0) BroadcastPropertyChanged(comp, changed, DateTime.UtcNow.Ticks);
            }


            if (TeamCreateSession.IsConnected && !TeamCreateSession.IsApplyingRemoteChange)
                FlushAllPendingPropertyBroadcasts();

            return mods;
        }

        private static readonly string[] TransformSpatialKeys =
        {
            "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
            "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w",
            "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z"
        };

        private static bool HasAnyTransformSpatialKey(Dictionary<string, string> props)
        {
            foreach (var key in TransformSpatialKeys)
                if (props.ContainsKey(key)) return true;
            return false;
        }

        private static void ExpandToFullTransform(SerializedObject so, Dictionary<string, string> props)
        {
            foreach (var key in TransformSpatialKeys)
            {
                if (props.ContainsKey(key)) continue;
                var prop = so.FindProperty(key);
                if (prop != null) try { props[key] = TeamCreateSnapshotBuilder.SerializeProperty(prop); } catch { }
            }
        }

        private static void BroadcastComponentPropertyChanged(Component comp)
        {
            if (comp == null || comp.gameObject == null) return;

            if (comp is Transform)
            {
                string goGuid = TeamCreateIdentity.GetGuid(comp.gameObject);
                if (!string.IsNullOrEmpty(goGuid))
                {
                    double now = EditorApplication.timeSinceStartup;
                    if (_transformOwners.TryGetValue(goGuid, out var existing) &&
                        now < existing.expiryTime &&
                        existing.ownerId != TeamCreateSession.LocalPeerId)
                    {

                        return;
                    }

                    _transformOwners[goGuid] = (TeamCreateSession.LocalPeerId, now + TransformOwnershipExpirySeconds);
                }
            }

            BroadcastPropertyChanged(comp, TeamCreateSnapshotBuilder.SerializeAllProperties(comp), DateTime.UtcNow.Ticks);
            CheckAndBroadcastStaticFlags(comp.gameObject);
        }

        private static void BroadcastGameObjectPropertyChanged(GameObject go)
        {
            string guid = TeamCreateIdentity.GetOrAssignGuid(go);
            _staticFlagsSnapshot[guid] = (int)GameObjectUtility.GetStaticEditorFlags(go);

            var renamedPayload = new GameObjectRenamedPayload
            {
                Guid = guid,
                NewName = go.name,
                HasGoProperties = true,
                Layer = go.layer,
                Tag = go.tag,
                StaticFlags = (int)GameObjectUtility.GetStaticEditorFlags(go)
            };
            TeamCreateLogger.LogSend("GAMEOBJECT_RENAMED", go.name);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.GAMEOBJECT_RENAMED,
                TeamCreateSession.LocalPeerId, Protocol.Serialize(renamedPayload)));

            TeamCreateLogger.LogSend("GAMEOBJECT_ACTIVATED", go.name);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.GAMEOBJECT_ACTIVATED,
                TeamCreateSession.LocalPeerId, Protocol.Serialize(new GameObjectActivatedPayload { Guid = guid, IsActive = go.activeSelf })));





        }

        private static void BroadcastGameObjectReparented(GameObject go)
        {
            string guid = TeamCreateIdentity.GetOrAssignGuid(go);
            string parentGuid = go.transform.parent != null
                ? TeamCreateIdentity.GetOrAssignGuid(go.transform.parent.gameObject)
                : null;
            var lp = go.transform.localPosition;
            var lr = go.transform.localRotation;
            var ls = go.transform.localScale;

            var payload = new GameObjectReparentedPayload
            {
                Guid = guid,
                NewParentGuid = parentGuid,
                NewSiblingIndex = go.transform.GetSiblingIndex(),
                LocalPosX = lp.x,
                LocalPosY = lp.y,
                LocalPosZ = lp.z,
                LocalRotX = lr.x,
                LocalRotY = lr.y,
                LocalRotZ = lr.z,
                LocalRotW = lr.w,
                LocalScaleX = ls.x,
                LocalScaleY = ls.y,
                LocalScaleZ = ls.z
            };

            TeamCreateLogger.LogSend("GAMEOBJECT_REPARENTED", go.name);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.GAMEOBJECT_REPARENTED, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
        }



        private static void BroadcastPropertyChanged(Component comp, Dictionary<string, string> props, long timestamp)
        {
            if (comp is Transform)
            {
                FlushPropertyBroadcast(comp, props, timestamp);
                return;
            }

            string compGuid = TeamCreateIdentity.GetOrAssignComponentGuid(comp);
            if (string.IsNullOrEmpty(compGuid))
            {
                FlushPropertyBroadcast(comp, props, timestamp);
                return;
            }

            double fireTime = EditorApplication.timeSinceStartup + PropertyBroadcastDebounceSeconds;
            if (PendingPropertyBroadcasts.TryGetValue(compGuid, out var existing))
            {
                foreach (var kvp in props) existing.Properties[kvp.Key] = kvp.Value;
                existing.Timestamp = timestamp;



            }
            else
            {
                PendingPropertyBroadcasts[compGuid] = new PendingPropertyBroadcast
                {
                    Comp = comp,
                    Properties = new Dictionary<string, string>(props),
                    Timestamp = timestamp,
                    ScheduledTime = fireTime
                };
            }
        }

        private static void FlushPropertyBroadcast(Component comp, Dictionary<string, string> props, long timestamp)
        {
            if (comp == null || comp.gameObject == null || props.Count == 0) return;
            string goGuid = TeamCreateIdentity.GetOrAssignGuid(comp.gameObject);
            string compGuid = TeamCreateIdentity.GetOrAssignComponentGuid(comp);
            var payload = new ComponentPropertyChangedPayload
            {
                GameObjectGuid = goGuid,
                ComponentTypeName = comp.GetType().AssemblyQualifiedName,
                ComponentGuid = compGuid,
                Properties = props
            };
            var msg = Protocol.CreateMessage(MessageType.COMPONENT_PROPERTY_CHANGED, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload));
            msg.Timestamp = timestamp;
            TeamCreateSession.SendMessage(msg);
        }

        public static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            var t = Type.GetType(typeName);
            if (t != null) return t;
            string fullOnly = typeName.Contains(',') ? typeName.Split(',')[0].Trim() : typeName;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullOnly);
                if (t != null) return t;
            }
            return null;
        }

        public static void ApplyGameObjectCreated(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<GameObjectCreatedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("GAMEOBJECT_CREATED", message.SenderId);

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                if (!string.IsNullOrEmpty(payload.Guid) && TeamCreateIdentity.FindByGuid(payload.Guid) != null)
                    return;

                if (!string.IsNullOrEmpty(payload.ParentGuid))
                {
                    var potentialParent = TeamCreateIdentity.FindByGuid(payload.ParentGuid);
                    if (potentialParent != null && payload.SiblingIndex >= 0 && payload.SiblingIndex < potentialParent.transform.childCount)
                    {
                        var existingChild = potentialParent.transform.GetChild(payload.SiblingIndex).gameObject;
                        if (existingChild.name == payload.Name && string.IsNullOrEmpty(TeamCreateIdentity.GetGuid(existingChild)))
                        {
                            TeamCreateIdentity.AssignGuid(existingChild, payload.Guid);
                            existingChild.layer = payload.Layer;
                            try { existingChild.tag = payload.Tag; } catch { }
                            TeamCreateSnapshotBuilder.ApplyComponentsToGameObject(existingChild, payload.Components);
                            existingChild.SetActive(payload.IsActive);
                            SnapshotComponents(existingChild);
                            EditorSceneManager.MarkSceneDirty(existingChild.scene);
                            RetryPendingReparents();
                            return;
                        }
                    }
                }

                GameObject go = null;

                if (!string.IsNullOrEmpty(payload.PrefabAssetGuid))
                {
                    string prefabPath = AssetDatabase.GUIDToAssetPath(payload.PrefabAssetGuid);
                    if (!string.IsNullOrEmpty(prefabPath))
                    {
                        var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                        if (prefabAsset != null)
                            go = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
                    }
                }

                if (go == null)
                    go = new GameObject(payload.Name);

                go.name = payload.Name;
                go.layer = payload.Layer;
                try { go.tag = payload.Tag; } catch { }

                if (!string.IsNullOrEmpty(payload.ParentGuid))
                {
                    var parent = TeamCreateIdentity.FindByGuid(payload.ParentGuid);
                    if (parent != null) go.transform.SetParent(parent.transform, false);
                }
                else
                {

                    Scene targetScene = TeamCreateSceneSync.GetSceneByNetId(payload.SceneNetId);
                    if (!targetScene.IsValid() && !string.IsNullOrEmpty(payload.ScenePath))
                    {
                        string localScenePath = TeamCreateSession.NetworkToLocalPath(payload.ScenePath) ?? payload.ScenePath;
                        targetScene = GetSceneByPath(localScenePath);
                    }
                    if (targetScene.IsValid()) EditorSceneManager.MoveGameObjectToScene(go, targetScene);
                }

                go.transform.SetSiblingIndex(payload.SiblingIndex);
                TeamCreateIdentity.AssignGuid(go, payload.Guid);
                TeamCreateSnapshotBuilder.ApplyComponentsToGameObject(go, payload.Components);
                go.SetActive(payload.IsActive);

                SnapshotComponents(go);
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }

            RetryPendingReparents();


            EditorApplication.delayCall += () => TeamCreateSnapshotBuilder.RetryPendingRefs();
        }

        public static void ApplyGameObjectDeleted(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<GameObjectDeletedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("GAMEOBJECT_DELETED", message.SenderId);
            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var go = TeamCreateIdentity.FindByGuid(payload.Guid);
                if (go != null)
                {
                    KnownComponentIds.Remove(go.GetInstanceID());
                    var scene = go.scene;
                    UnityEngine.Object.DestroyImmediate(go);
                    EditorSceneManager.MarkSceneDirty(scene);
                }
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        public static void ApplyGameObjectReparented(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<GameObjectReparentedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("GAMEOBJECT_REPARENTED", message.SenderId);




            if (!string.IsNullOrEmpty(payload.NewParentGuid) &&
                TeamCreateIdentity.FindByGuid(payload.NewParentGuid) == null)
            {
                _pendingReparents.Add(new PendingReparentItem
                {
                    Payload = payload,
                    ReceivedTime = EditorApplication.timeSinceStartup
                });
                return;
            }

            ApplyReparentPayload(payload);
        }

        private static void ApplyReparentPayload(GameObjectReparentedPayload payload)
        {
            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var go = TeamCreateIdentity.FindByGuid(payload.Guid);
                if (go == null) return;
                if (!string.IsNullOrEmpty(payload.NewParentGuid))
                {
                    var parent = TeamCreateIdentity.FindByGuid(payload.NewParentGuid);
                    if (parent != null) go.transform.SetParent(parent.transform, false);
                }
                else
                {
                    go.transform.SetParent(null);
                }
                go.transform.localPosition = new Vector3(payload.LocalPosX, payload.LocalPosY, payload.LocalPosZ);
                go.transform.localRotation = new Quaternion(payload.LocalRotX, payload.LocalRotY, payload.LocalRotZ, payload.LocalRotW);
                go.transform.localScale = new Vector3(payload.LocalScaleX, payload.LocalScaleY, payload.LocalScaleZ);
                go.transform.SetSiblingIndex(payload.NewSiblingIndex);
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        private static void RetryPendingReparents()
        {
            if (_pendingReparents.Count == 0) return;
            double now = EditorApplication.timeSinceStartup;
            var stillPending = new List<PendingReparentItem>();
            foreach (var item in _pendingReparents)
            {
                if (now - item.ReceivedTime > PendingReparentTimeoutSeconds) continue;
                var parent = TeamCreateIdentity.FindByGuid(item.Payload.NewParentGuid);
                if (parent == null) { stillPending.Add(item); continue; }
                ApplyReparentPayload(item.Payload);
            }
            _pendingReparents.Clear();
            _pendingReparents.AddRange(stillPending);
        }

        public static void ApplyGameObjectRenamed(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<GameObjectRenamedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("GAMEOBJECT_RENAMED", message.SenderId);
            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var go = TeamCreateIdentity.FindByGuid(payload.Guid);
                if (go == null) return;
                go.name = payload.NewName;
                if (payload.HasGoProperties)
                {
                    go.layer = payload.Layer;
                    if (!string.IsNullOrEmpty(payload.Tag))
                        try { go.tag = payload.Tag; } catch { }
                    GameObjectUtility.SetStaticEditorFlags(go, (StaticEditorFlags)payload.StaticFlags);
                    _staticFlagsSnapshot[payload.Guid] = payload.StaticFlags;
                }
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        public static void ApplyGameObjectActivated(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<GameObjectActivatedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("GAMEOBJECT_ACTIVATED", message.SenderId);
            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var go = TeamCreateIdentity.FindByGuid(payload.Guid);
                if (go == null) return;
                go.SetActive(payload.IsActive);
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        public static void ApplyComponentAdded(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<ComponentAddedPayload>(message.Payload);
            if (payload == null)
            {
                Debug.LogError($"[TeamCreate] COMPONENT_ADDED payload null. Raw length={message.Payload?.Length ?? -1}");
                return;
            }

            TeamCreateLogger.Log($"RECV COMPONENT_ADDED GoGuid={payload.GameObjectGuid} Comp={payload.ComponentTypeName} Props={payload.Properties?.Count ?? 0}");

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var go = TeamCreateIdentity.FindByGuid(payload.GameObjectGuid);
                if (go == null)
                {
                    Debug.LogError($"[TeamCreate] COMPONENT_ADDED failed — GO not found for GoGuid={payload.GameObjectGuid} Comp={payload.ComponentTypeName}");
                    return;
                }
                Type compType = ResolveType(payload.ComponentTypeName);
                if (compType == null)
                {
                    Debug.LogWarning($"[TeamCreate] COMPONENT_ADDED failed — Cannot resolve type '{payload.ComponentTypeName}' on GO='{go.name}'");
                    return;
                }
                if (compType == typeof(Transform))
                {
                    TeamCreateLogger.Log($"COMPONENT_ADDED skipped Transform on GO='{go.name}'");
                    return;
                }
                var existingComp = go.GetComponent(compType);
                bool wasAlreadyPresent = existingComp != null;
                Component comp = existingComp;
                if (comp == null)
                {
                    ObjectFactory.AddComponent(go, compType);
                    comp = go.GetComponent(compType);
                }
                if (comp == null)
                {
                    go.AddComponent(compType);
                    comp = go.GetComponent(compType);
                }
                if (comp == null)
                {
                    Debug.LogError($"[TeamCreate] COMPONENT_ADDED all add attempts failed for '{compType.FullName}' on GO='{go.name}'");
                    return;
                }

                TeamCreateLogger.Log($"COMPONENT_ADDED {(wasAlreadyPresent ? "found existing" : "added new")} '{compType.FullName}' on GO='{go.name}'");

                TeamCreateIdentity.AssignComponentGuid(comp, payload.ComponentGuid);
                if (payload.Properties != null)
                {
                    var so = new SerializedObject(comp);
                    int appliedCount = 0;
                    foreach (var kvp in payload.Properties)
                    {
                        var prop = so.FindProperty(kvp.Key);
                        if (prop != null)
                        {
                            try { TeamCreateSnapshotBuilder.DeserializeProperty(prop, kvp.Value); appliedCount++; }
                            catch (Exception propEx) { Debug.LogWarning($"[TeamCreate] COMPONENT_ADDED prop '{kvp.Key}' failed: {propEx.Message}"); }
                        }
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                    TeamCreateLogger.Log($"COMPONENT_ADDED applied {appliedCount}/{payload.Properties.Count} props to '{compType.FullName}'");
                }
                SnapshotComponents(go);
                EditorSceneManager.MarkSceneDirty(go.scene);



                EditorApplication.delayCall += () =>
                {
                    TeamCreateSnapshotBuilder.RetryPendingRefs();
                };
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        public static void ApplyComponentRemoved(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<ComponentRemovedPayload>(message.Payload);
            if (payload == null) return;
            TeamCreateLogger.LogRecv("COMPONENT_REMOVED", message.SenderId);
            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var go = TeamCreateIdentity.FindByGuid(payload.GameObjectGuid);
                if (go == null) return;
                Type compType = ResolveType(payload.ComponentTypeName);
                if (compType == null) return;
                Component comp = null;
                foreach (var c in go.GetComponents(compType))
                {
                    if (TeamCreateIdentity.GetComponentGuid(c) == payload.ComponentGuid) { comp = c; break; }
                }
                if (comp == null) comp = go.GetComponent(compType);
                if (comp != null)
                {
                    var scene = go.scene;
                    UnityEngine.Object.DestroyImmediate(comp);
                    SnapshotComponents(go);
                    EditorSceneManager.MarkSceneDirty(scene);
                }
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        public static void ApplyComponentPropertyChanged(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<ComponentPropertyChangedPayload>(message.Payload);
            if (payload == null) return;
            if (UnityEditor.AnimationMode.InAnimationMode()) return;
            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var go = TeamCreateIdentity.FindByGuid(payload.GameObjectGuid);
                if (go == null) return;
                Type compType = ResolveType(payload.ComponentTypeName);
                if (compType == null) { Debug.LogWarning($"[TeamCreate] Cannot resolve: {payload.ComponentTypeName}"); return; }

                Component comp = null;
                foreach (var c in go.GetComponents(compType))
                {
                    if (TeamCreateIdentity.GetComponentGuid(c) == payload.ComponentGuid) { comp = c; break; }
                }
                if (comp == null) { var all = go.GetComponents(compType); if (all.Length > 0) comp = all[0]; }
                if (comp == null) return;



                if (compType == typeof(Transform) && IsTransformPayload(payload.Properties))
                {
                    ApplyTransformWithOwnership(go, payload.Properties, message.SenderId);
                    return;
                }

                bool shouldApply = true;
                foreach (var kvp in payload.Properties)
                {
                    string key = TeamCreateConflictResolver.MakePropertyKey(payload.ComponentGuid, kvp.Key);
                    if (!TeamCreateConflictResolver.ShouldApply(key, message.Timestamp)) { shouldApply = false; break; }
                }
                if (!shouldApply) return;

                string actualCompGuid = TeamCreateIdentity.GetComponentGuid(comp);
                if (string.IsNullOrEmpty(actualCompGuid)) actualCompGuid = payload.ComponentGuid;

                bool interpEnabled = TeamCreateInterpolator.InterpolationEnabled;
                var so = new SerializedObject(comp);
                bool hasImmediateChanges = false;

                foreach (var kvp in payload.Properties)
                {
                    if (interpEnabled && TeamCreateInterpolator.IsInterpolatableValue(kvp.Value))
                    {
                        TeamCreateInterpolator.SetPropertyTarget(comp, actualCompGuid, kvp.Key, kvp.Value);
                    }
                    else
                    {
                        var prop = so.FindProperty(kvp.Key);
                        if (prop != null)
                        {
                            try { TeamCreateSnapshotBuilder.DeserializeProperty(prop, kvp.Value); }
                            catch { }
                            hasImmediateChanges = true;
                        }
                    }
                }

                if (hasImmediateChanges)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorSceneManager.MarkSceneDirty(go.scene);
                }
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        private static bool IsTransformPayload(Dictionary<string, string> props)
            => props.ContainsKey("m_LocalPosition.x") || props.ContainsKey("m_LocalPosition.y") || props.ContainsKey("m_LocalPosition.z") ||
               props.ContainsKey("m_LocalRotation.x") || props.ContainsKey("m_LocalRotation.y") || props.ContainsKey("m_LocalRotation.z") || props.ContainsKey("m_LocalRotation.w") ||
               props.ContainsKey("m_LocalScale.x") || props.ContainsKey("m_LocalScale.y") || props.ContainsKey("m_LocalScale.z");

        private static void ApplyTransformWithOwnership(GameObject go, Dictionary<string, string> props, string senderId)
        {
            string guid = TeamCreateIdentity.GetGuid(go);
            if (string.IsNullOrEmpty(guid)) return;

            double now = EditorApplication.timeSinceStartup;


            if (_transformOwners.TryGetValue(guid, out var owner) && now < owner.expiryTime)
            {
                if (owner.ownerId == TeamCreateSession.LocalPeerId)
                    return;
                if (owner.ownerId != senderId)
                    return;

                _transformOwners[guid] = (senderId, now + TransformOwnershipExpirySeconds);
            }
            else
            {

                _transformOwners[guid] = (senderId, now + TransformOwnershipExpirySeconds);
            }

            Vector3 pos;
            Quaternion rot;
            Vector3 scl;

            if (!TeamCreateInterpolator.GetLastTransformTarget(guid, out pos, out rot, out scl))
            {
                pos = go.transform.localPosition;
                rot = go.transform.localRotation;
                scl = go.transform.localScale;
            }

            if (props.TryGetValue("m_LocalPosition.x", out var px) && TryParseFloatProp(px, out float fpx)) pos.x = fpx;
            if (props.TryGetValue("m_LocalPosition.y", out var py) && TryParseFloatProp(py, out float fpy)) pos.y = fpy;
            if (props.TryGetValue("m_LocalPosition.z", out var pz) && TryParseFloatProp(pz, out float fpz)) pos.z = fpz;
            if (props.TryGetValue("m_LocalRotation.x", out var rx) && TryParseFloatProp(rx, out float frx)) rot.x = frx;
            if (props.TryGetValue("m_LocalRotation.y", out var ry) && TryParseFloatProp(ry, out float fry)) rot.y = fry;
            if (props.TryGetValue("m_LocalRotation.z", out var rz) && TryParseFloatProp(rz, out float frz)) rot.z = frz;
            if (props.TryGetValue("m_LocalRotation.w", out var rw) && TryParseFloatProp(rw, out float frw)) rot.w = frw;
            if (props.TryGetValue("m_LocalScale.x", out var sx) && TryParseFloatProp(sx, out float fsx)) scl.x = fsx;
            if (props.TryGetValue("m_LocalScale.y", out var sy) && TryParseFloatProp(sy, out float fsy)) scl.y = fsy;
            if (props.TryGetValue("m_LocalScale.z", out var sz) && TryParseFloatProp(sz, out float fsz)) scl.z = fsz;

            TeamCreateInterpolator.SetTarget(guid, pos, rot, scl);
        }

        private static bool TryParseFloatProp(string raw, out float result)
        {
            string s = raw.StartsWith("float:") ? raw.Substring(6) : raw;
            return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
        }





        private static readonly HashSet<Material> _prevSelectedMaterials = new HashSet<Material>();

        private static void OnEditorSelectionChanged()
        {
            if (!TeamCreateSession.IsConnected || TeamCreateSession.IsApplyingRemoteChange) return;

            var currentMats = new HashSet<Material>();
            foreach (var obj in Selection.objects)
                if (obj is Material m) currentMats.Add(m);
            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    if (r != null && r.sharedMaterials != null)
                        foreach (var m in r.sharedMaterials)
                            if (m != null) currentMats.Add(m);
            }
            try
            {
                var tracker = ActiveEditorTracker.sharedTracker;
                if (tracker != null)
                    foreach (var e in tracker.activeEditors)
                        if (e != null && e.target is Material tm) currentMats.Add(tm);
            }
            catch { }

            foreach (var mat in _prevSelectedMaterials)
            {
                if (currentMats.Contains(mat)) continue;
                string assetPath = AssetDatabase.GetAssetPath(mat);
                if (string.IsNullOrEmpty(assetPath) || RecentlyReceivedMaterials.ContainsKey(assetPath)) continue;
                if (!TeamCreateSession.IsInSessionScope(assetPath)) continue;
                string snap = SnapshotMaterialViaAPI(mat);
                if (string.IsNullOrEmpty(snap)) continue;
                if (!MaterialPropertySnapshots.TryGetValue(assetPath, out string last) || snap == last) continue;
                MaterialPropertySnapshots[assetPath] = snap;
                BroadcastMaterialPropertyChanged(mat, assetPath);
            }

            _prevSelectedMaterials.Clear();
            foreach (var m in currentMats) _prevSelectedMaterials.Add(m);
        }

        public static void ScheduleLightmapSyncRequest()
        {
            _lightmapSyncRequestTime = EditorApplication.timeSinceStartup + 0.15;
        }

        public static void HandleLightmapSyncRequest(NetworkMessage message)
        {
            if (!TeamCreateSession.IsHosting) return;
            TeamCreateLogger.LogRecv("LIGHTMAP_SYNC_REQUEST", message.SenderId);
#if UNITY_2018_4_OR_NEWER
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            LightmapData[] lightmaps = LightmapSettings.lightmaps;

            foreach (var lm in lightmaps)
            {
                SendLightmapTexture(lm.lightmapColor, projectRoot);
                SendLightmapTexture(lm.lightmapDir, projectRoot);
#if UNITY_2019_2_OR_NEWER
                SendLightmapTexture(lm.shadowMask, projectRoot);
#endif
            }

            var slots = new List<LightmapSlotEntry>();
            foreach (var lm in lightmaps)
            {
                slots.Add(new LightmapSlotEntry
                {
                    LightmapColorGuid = GetTexGuid(lm.lightmapColor),
                    LightmapDirGuid = GetTexGuid(lm.lightmapDir),
#if UNITY_2019_2_OR_NEWER
                    ShadowMaskGuid = GetTexGuid(lm.shadowMask),
#endif
                });
            }

            var rendererEntries = new List<LightmapRendererEntry>();
            var goEntries = new List<LightmapGameObjectEntry>();
            int sceneCount = EditorSceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    CollectRendererLightmapData(root, rendererEntries, goEntries);
            }

            var syncPayload = new LightmapSyncPayload { Slots = slots, Renderers = rendererEntries, GameObjects = goEntries };
            var msg = Protocol.CreateMessage(MessageType.LIGHTMAP_SYNC, TeamCreateSession.LocalPeerId, Protocol.Serialize(syncPayload));
            TeamCreateSession.Host.SendToPeer(message.SenderId, msg);
#endif
        }

#if UNITY_2018_4_OR_NEWER
        private static void OnLightmapBakeCompleted()
        {
            if (!TeamCreateSession.IsConnected) return;
            TeamCreateLogger.Log("[TeamCreate] Lightmap bake completed — scheduling sync.");
            EditorApplication.delayCall += () => EditorApplication.delayCall += BroadcastLightmapData;
        }

        private static void BroadcastLightmapData()
        {
            if (!TeamCreateSession.IsConnected) return;
            TeamCreateLogger.Log("[TeamCreate] Broadcasting lightmap textures and sync payload.");

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            LightmapData[] lightmaps = LightmapSettings.lightmaps;

            foreach (var lm in lightmaps)
            {
                SendLightmapTexture(lm.lightmapColor, projectRoot);
                SendLightmapTexture(lm.lightmapDir, projectRoot);
#if UNITY_2019_2_OR_NEWER
                SendLightmapTexture(lm.shadowMask, projectRoot);
#endif
            }

            var slots = new List<LightmapSlotEntry>();
            foreach (var lm in lightmaps)
            {
                slots.Add(new LightmapSlotEntry
                {
                    LightmapColorGuid = GetTexGuid(lm.lightmapColor),
                    LightmapDirGuid = GetTexGuid(lm.lightmapDir),
#if UNITY_2019_2_OR_NEWER
                    ShadowMaskGuid = GetTexGuid(lm.shadowMask),
#endif
                });
            }

            var rendererEntries = new List<LightmapRendererEntry>();
            var goEntries = new List<LightmapGameObjectEntry>();
            int sceneCount = EditorSceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    CollectRendererLightmapData(root, rendererEntries, goEntries);
                }
            }

            var syncPayload = new LightmapSyncPayload { Slots = slots, Renderers = rendererEntries, GameObjects = goEntries };
            EditorApplication.delayCall += () =>
            {
                if (!TeamCreateSession.IsConnected) return;
                TeamCreateSession.SendMessage(Protocol.CreateMessage(
                    MessageType.LIGHTMAP_SYNC,
                    TeamCreateSession.LocalPeerId,
                    Protocol.Serialize(syncPayload)));
            };
        }

        private static string GetTexGuid(Texture2D tex)
        {
            if (tex == null) return "";
            string path = AssetDatabase.GetAssetPath(tex);
            return string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path);
        }

        private static void SendLightmapTexture(Texture2D tex, string projectRoot)
        {
            if (tex == null) return;
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return;
            string absPath = Path.Combine(projectRoot, path);
            if (!File.Exists(absPath)) return;
            try
            {
                byte[] content = File.ReadAllBytes(absPath);
                TeamCreateAssetSync.SendFileDirectly(path, content, MessageType.ASSET_MODIFIED);
            }
            catch { }
        }

        private static void CollectRendererLightmapData(GameObject go, List<LightmapRendererEntry> entries, List<LightmapGameObjectEntry> goEntries)
        {
            string goGuid = TeamCreateIdentity.GetGuid(go);
            if (!string.IsNullOrEmpty(goGuid))
            {
                int flags = (int)GameObjectUtility.GetStaticEditorFlags(go);
                if (flags != 0)
                    goEntries.Add(new LightmapGameObjectEntry { GoGuid = goGuid, StaticFlags = flags });
            }

            foreach (var r in go.GetComponents<Renderer>())
            {
                if (r == null || r.lightmapIndex < 0) continue;
                if (string.IsNullOrEmpty(goGuid)) continue;
                string compGuid = TeamCreateIdentity.GetOrAssignComponentGuid(r);
                Vector4 so = r.lightmapScaleOffset;

                float scaleInLightmap = 1f;
                int receiveGI = 1;
                try
                {
                    var so2 = new SerializedObject(r);
                    so2.Update();
                    var scaleProp = so2.FindProperty("m_ScaleInLightmap");
                    if (scaleProp != null) scaleInLightmap = scaleProp.floatValue;
                    var receiveGIProp = so2.FindProperty("m_ReceiveGI");
                    if (receiveGIProp != null) receiveGI = receiveGIProp.intValue;
                }
                catch { }

                entries.Add(new LightmapRendererEntry
                {
                    GoGuid = goGuid,
                    ComponentGuid = compGuid,
                    LightmapIndex = r.lightmapIndex,
                    ScaleOffsetX = so.x,
                    ScaleOffsetY = so.y,
                    ScaleOffsetZ = so.z,
                    ScaleOffsetW = so.w,
                    ReceiveGI = receiveGI,
                    ScaleInLightmap = scaleInLightmap
                });
            }
            foreach (Transform child in go.transform)
                CollectRendererLightmapData(child.gameObject, entries, goEntries);
        }

        public static void ApplyLightmapSync(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<LightmapSyncPayload>(message.Payload);
            if (payload?.Slots == null) return;
            TeamCreateLogger.LogRecv("LIGHTMAP_SYNC", message.SenderId);

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var lmData = new LightmapData[payload.Slots.Count];
                for (int i = 0; i < payload.Slots.Count; i++)
                {
                    var slot = payload.Slots[i];
                    lmData[i] = new LightmapData
                    {
                        lightmapColor = LoadTexFromGuid(slot.LightmapColorGuid),
                        lightmapDir = LoadTexFromGuid(slot.LightmapDirGuid),
                    };
#if UNITY_2019_2_OR_NEWER
                    lmData[i].shadowMask = LoadTexFromGuid(slot.ShadowMaskGuid);
#endif
                }
                LightmapSettings.lightmaps = lmData;

                if (payload.GameObjects != null)
                {
                    foreach (var entry in payload.GameObjects)
                    {
                        var go = TeamCreateIdentity.FindByGuid(entry.GoGuid);
                        if (go == null) continue;
                        GameObjectUtility.SetStaticEditorFlags(go, (StaticEditorFlags)entry.StaticFlags);
                        _staticFlagsSnapshot[entry.GoGuid] = entry.StaticFlags;
                        EditorSceneManager.MarkSceneDirty(go.scene);
                    }
                }

                if (payload.Renderers != null)
                {
                    foreach (var entry in payload.Renderers)
                    {
                        var go = TeamCreateIdentity.FindByGuid(entry.GoGuid);
                        if (go == null) continue;
                        foreach (var r in go.GetComponents<Renderer>())
                        {
                            if (r == null) continue;
                            string compGuid = TeamCreateIdentity.GetOrAssignComponentGuid(r);
                            if (compGuid != entry.ComponentGuid) continue;
                            r.lightmapIndex = entry.LightmapIndex;
                            r.lightmapScaleOffset = new Vector4(entry.ScaleOffsetX, entry.ScaleOffsetY, entry.ScaleOffsetZ, entry.ScaleOffsetW);
                            try
                            {
                                var so2 = new SerializedObject(r);
                                so2.Update();
                                var scaleProp = so2.FindProperty("m_ScaleInLightmap");
                                if (scaleProp != null) scaleProp.floatValue = entry.ScaleInLightmap;
                                var receiveGIProp = so2.FindProperty("m_ReceiveGI");
                                if (receiveGIProp != null) receiveGIProp.intValue = entry.ReceiveGI;
                                so2.ApplyModifiedPropertiesWithoutUndo();
                            }
                            catch { }
                            break;
                        }
                    }
                }

                SceneView.RepaintAll();
            }
            finally
            {
                TeamCreateSession.IsApplyingRemoteChange = false;
            }
        }

        private static Texture2D LoadTexFromGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
#endif

        private static void OnVisibilityOrPickingChanged()
        {
            if (!TeamCreateSession.IsConnected) return;
            if (TeamCreateSession.IsApplyingRemoteChange) return;

            var svm = SceneVisibilityManager.instance;
            foreach (int instanceId in KnownInstanceIds)
            {
                var go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (go == null) continue;
                string guid = TeamCreateIdentity.GetGuid(go);
                if (string.IsNullOrEmpty(guid)) continue;

                bool isHidden = svm.IsHidden(go);
#if UNITY_2020_1_OR_NEWER
                bool isPickingDisabled = svm.IsPickingDisabled(go);
#else
                bool isPickingDisabled = false;
#endif

                bool prevHidden = false;
                bool prevPicking = false;
                if (_visibilitySnapshot.TryGetValue(guid, out var prev))
                {
                    prevHidden = prev[0];
                    prevPicking = prev[1];
                }

                if (isHidden != prevHidden || isPickingDisabled != prevPicking)
                {
                    _visibilitySnapshot[guid] = new bool[] { isHidden, isPickingDisabled };
                    var payload = new SceneVisibilityPayload
                    {
                        GoGuid = guid,
                        IsHidden = isHidden,
                        IsPickingDisabled = isPickingDisabled
                    };
                    TeamCreateSession.SendMessage(Protocol.CreateMessage(
                        MessageType.SCENE_VISIBILITY_CHANGED,
                        TeamCreateSession.LocalPeerId,
                        Protocol.Serialize(payload)));
                }
            }
        }

        public static void ApplySceneVisibilityChanged(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<SceneVisibilityPayload>(message.Payload);
            if (payload == null) return;

            var go = TeamCreateIdentity.FindByGuid(payload.GoGuid);
            if (go == null) return;

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                var svm = SceneVisibilityManager.instance;
                if (payload.IsHidden)
                    svm.Hide(go, false);
                else
                    svm.Show(go, false);

#if UNITY_2020_1_OR_NEWER
                if (payload.IsPickingDisabled)
                    svm.DisablePicking(go, false);
                else
                    svm.EnablePicking(go, false);
#endif

                _visibilitySnapshot[payload.GoGuid] = new bool[] { payload.IsHidden, payload.IsPickingDisabled };
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }





        public static void OnPeerDisconnected(string peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return;
            var toRemove = new List<string>();
            foreach (var kvp in _transformOwners)
                if (kvp.Value.ownerId == peerId) toRemove.Add(kvp.Key);
            foreach (var k in toRemove) _transformOwners.Remove(k);
        }

        private static Scene GetSceneByPath(string scenePath)
        {
            int count = EditorSceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (s.path == scenePath) return s;
            }
            return default;
        }
    }
}
#endif
