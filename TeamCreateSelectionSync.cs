#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public static class TeamCreateSelectionSync
    {
        private const string EditorPrefKey_Highlights = "TeamCreate_HighlightsEnabled";
        private const string EditorPrefKey_Opacity = "TeamCreate_HighlightOpacity";

        private static bool _highlightsEnabled;
        private static double _lastSendTime;
        private const double SendCooldownSeconds = 0.15;
        private static readonly Dictionary<string, List<string>> PeerSelections = new Dictionary<string, List<string>>();

        private static readonly Dictionary<string, List<string>> PeerAssetSelections = new Dictionary<string, List<string>>();

        private static readonly Dictionary<string, List<string>> PeerAssetPathCache = new Dictionary<string, List<string>>();

        private static readonly HashSet<string> _knownPeerIds = new HashSet<string>();

        private static readonly Dictionary<string, List<Color>> _hierarchyDirectHighlights = new Dictionary<string, List<Color>>();
        private static readonly Dictionary<string, List<Color>> _hierarchyAncestorHighlights = new Dictionary<string, List<Color>>();

        private static readonly Dictionary<int, MeshHighlightData> _meshHighlightCache = new Dictionary<int, MeshHighlightData>();
        private const int MaxTrisForMeshHighlight = 8000;

        private class MeshHighlightData
        {
            public Vector3[] Vertices;
            public int[] TriIndices;
            public int[] EdgeV0;
            public int[] EdgeV1;
            public Vector3[] EdgeN0;
            public Vector3[] EdgeN1;
        }

        private class HighlightData
        {
            public readonly List<Color> Colors = new List<Color>();
            public readonly List<string> Names = new List<string>();
        }

        static TeamCreateSelectionSync()
        {
            _highlightsEnabled = EditorPrefs.GetBool(EditorPrefKey_Highlights, true);
            Selection.selectionChanged += OnSelectionChanged;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyWindowItemGUI;
            TeamCreateSession.OnPeersChanged += OnPeersChanged;
            TeamCreateSession.OnSessionEnded += Clear;
            AssemblyReloadEvents.beforeAssemblyReload += Unregister;
        }

        public static bool HighlightsEnabled
        {
            get => _highlightsEnabled;
            set { _highlightsEnabled = value; EditorPrefs.SetBool(EditorPrefKey_Highlights, value); SceneView.RepaintAll(); }
        }

        public static float HighlightOpacity
        {
            get => EditorPrefs.GetFloat(EditorPrefKey_Opacity, 0.025f);
            set { EditorPrefs.SetFloat(EditorPrefKey_Opacity, Mathf.Clamp01(value)); SceneView.RepaintAll(); }
        }

        private static void Unregister()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.projectWindowItemOnGUI -= OnProjectWindowItemGUI;
            EditorApplication.hierarchyWindowItemOnGUI -= OnHierarchyWindowItemGUI;
            TeamCreateSession.OnPeersChanged -= OnPeersChanged;
            TeamCreateSession.OnSessionEnded -= Clear;
            AssemblyReloadEvents.beforeAssemblyReload -= Unregister;
            _knownPeerIds.Clear();
            _meshHighlightCache.Clear();
            _hierarchyDirectHighlights.Clear();
            _hierarchyAncestorHighlights.Clear();
        }

        private static void OnSelectionChanged()
        {
            if (!TeamCreateSession.IsConnected) return;
            if (TeamCreateSession.IsApplyingRemoteChange) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastSendTime < SendCooldownSeconds) return;
            _lastSendTime = now;

            var guids = new List<string>();
            foreach (GameObject obj in Selection.gameObjects)
            {
                string guid = TeamCreateIdentity.GetOrAssignGuid(obj);
                if (!string.IsNullOrEmpty(guid)) guids.Add(guid);
            }

            var assetGuids = new List<string>();
            foreach (var obj in Selection.objects)
            {
                if (obj == null || obj is GameObject) continue;
                string assetPath = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(assetPath)) continue;
                string ag = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(ag) && !assetGuids.Contains(ag)) assetGuids.Add(ag);
            }

            var activeAsset = Selection.activeObject;
            if (activeAsset != null && !(activeAsset is GameObject))
            {
                string ap = AssetDatabase.GetAssetPath(activeAsset);
                if (!string.IsNullOrEmpty(ap))
                {
                    string ag = AssetDatabase.AssetPathToGUID(ap);
                    if (!string.IsNullOrEmpty(ag) && !assetGuids.Contains(ag)) assetGuids.Add(ag);
                }
            }

            string[] projectAssets = Selection.assetGUIDs;
            if (projectAssets.Length > 0)
            {
                string folderGuid = projectAssets[0];
                if (!assetGuids.Contains(folderGuid)) assetGuids.Add(folderGuid);
            }

            var payload = new PeerSelectionPayload
            {
                PeerId = TeamCreateSession.LocalPeerId,
                SelectedGuids = guids,
                SelectedAssetGuids = assetGuids
            };
            TeamCreateLogger.LogSend("PEER_SELECTION", $"{guids.Count} GO(s), {assetGuids.Count} asset(s)");
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.PEER_SELECTION, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
        }

        private static void SendCurrentSelection()
        {
            if (!TeamCreateSession.IsConnected) return;

            var guids = new List<string>();
            foreach (GameObject obj in Selection.gameObjects)
            {
                string guid = TeamCreateIdentity.GetOrAssignGuid(obj);
                if (!string.IsNullOrEmpty(guid)) guids.Add(guid);
            }

            var assetGuids = new List<string>();
            foreach (var obj in Selection.objects)
            {
                if (obj == null || obj is GameObject) continue;
                string assetPath = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(assetPath)) continue;
                string ag = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(ag) && !assetGuids.Contains(ag)) assetGuids.Add(ag);
            }

            var activeAsset = Selection.activeObject;
            if (activeAsset != null && !(activeAsset is GameObject))
            {
                string ap = AssetDatabase.GetAssetPath(activeAsset);
                if (!string.IsNullOrEmpty(ap))
                {
                    string ag = AssetDatabase.AssetPathToGUID(ap);
                    if (!string.IsNullOrEmpty(ag) && !assetGuids.Contains(ag)) assetGuids.Add(ag);
                }
            }

            string[] projectAssets = Selection.assetGUIDs;
            if (projectAssets.Length > 0)
            {
                string folderGuid = projectAssets[0];
                if (!assetGuids.Contains(folderGuid)) assetGuids.Add(folderGuid);
            }

            var payload = new PeerSelectionPayload
            {
                PeerId = TeamCreateSession.LocalPeerId,
                SelectedGuids = guids,
                SelectedAssetGuids = assetGuids
            };
            _lastSendTime = EditorApplication.timeSinceStartup;
            TeamCreateLogger.LogSend("PEER_SELECTION (join)", $"{guids.Count} GO(s), {assetGuids.Count} asset(s)");
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.PEER_SELECTION, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
        }

        public static void ApplyPeerSelection(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<PeerSelectionPayload>(message.Payload);
            if (payload == null || payload.PeerId == TeamCreateSession.LocalPeerId) return;
            TeamCreateLogger.LogRecv("PEER_SELECTION", message.SenderId);
            PeerSelections[payload.PeerId] = payload.SelectedGuids ?? new List<string>();

            var toRemove = new List<string>();
            foreach (var kvp in PeerAssetSelections)
            {
                kvp.Value.Remove(payload.PeerId);
                if (kvp.Value.Count == 0) toRemove.Add(kvp.Key);
            }
            foreach (string k in toRemove) PeerAssetSelections.Remove(k);

            if (payload.SelectedAssetGuids != null)
            {
                foreach (string ag in payload.SelectedAssetGuids)
                {
                    if (string.IsNullOrEmpty(ag)) continue;
                    if (!PeerAssetSelections.TryGetValue(ag, out var list))
                        PeerAssetSelections[ag] = list = new List<string>();
                    if (!list.Contains(payload.PeerId)) list.Add(payload.PeerId);
                }
            }

            PeerAssetPathCache[payload.PeerId] = new List<string>();
            if (payload.SelectedAssetGuids != null)
            {
                foreach (string ag in payload.SelectedAssetGuids)
                {
                    string ap = AssetDatabase.GUIDToAssetPath(ag);
                    if (!string.IsNullOrEmpty(ap)) PeerAssetPathCache[payload.PeerId].Add(ap);
                }
            }

            RebuildHierarchyHighlights();
            SceneView.RepaintAll();
            EditorApplication.RepaintProjectWindow();
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void OnPeersChanged(List<PeerInfo> peers)
        {
            var currentIds = new HashSet<string>();
            foreach (var p in peers) currentIds.Add(p.PeerId);

            bool peerAdded = false;
            foreach (string id in currentIds)
                if (!_knownPeerIds.Contains(id)) { peerAdded = true; break; }

            _knownPeerIds.Clear();
            foreach (string id in currentIds) _knownPeerIds.Add(id);

            var toRemove = new List<string>();
            foreach (string id in PeerSelections.Keys) if (!currentIds.Contains(id)) toRemove.Add(id);
            foreach (string id in toRemove) PeerSelections.Remove(id);

            var deadAssetKeys = new List<string>();
            foreach (var kvp in PeerAssetSelections)
            {
                kvp.Value.RemoveAll(id => !currentIds.Contains(id));
                if (kvp.Value.Count == 0) deadAssetKeys.Add(kvp.Key);
            }
            foreach (string k in deadAssetKeys) PeerAssetSelections.Remove(k);

            var deadPathKeys = new List<string>();
            foreach (string id in PeerAssetPathCache.Keys) if (!currentIds.Contains(id)) deadPathKeys.Add(id);
            foreach (string id in deadPathKeys) PeerAssetPathCache.Remove(id);

            if (peerAdded && TeamCreateSession.IsConnected)
                SendCurrentSelection();

            RebuildHierarchyHighlights();
            SceneView.RepaintAll();
            EditorApplication.RepaintProjectWindow();
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void RebuildHierarchyHighlights()
        {
            _hierarchyDirectHighlights.Clear();
            _hierarchyAncestorHighlights.Clear();

            foreach (var kvp in PeerSelections)
            {
                if (kvp.Value == null || kvp.Value.Count == 0) continue;
                Color peerColor = TeamCreatePeerManager.GetPeerColor(kvp.Key);

                foreach (string guid in kvp.Value)
                {
                    if (!_hierarchyDirectHighlights.TryGetValue(guid, out var direct))
                        _hierarchyDirectHighlights[guid] = direct = new List<Color>();
                    direct.Add(peerColor);

                    var go = TeamCreateIdentity.FindByGuid(guid);
                    if (go == null) continue;
                    Transform parent = go.transform.parent;
                    while (parent != null)
                    {
                        string parentGuid = TeamCreateIdentity.GetGuid(parent.gameObject);
                        if (!string.IsNullOrEmpty(parentGuid))
                        {
                            if (!_hierarchyAncestorHighlights.TryGetValue(parentGuid, out var anc))
                                _hierarchyAncestorHighlights[parentGuid] = anc = new List<Color>();
                            if (!anc.Contains(peerColor)) anc.Add(peerColor);
                        }
                        parent = parent.parent;
                    }
                }
            }
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!TeamCreateSession.IsConnected) return;
            if (!_highlightsEnabled) return;
            if (PeerSelections.Count == 0) return;

            var perObject = new Dictionary<string, HighlightData>();
            foreach (var kvp in PeerSelections)
            {
                if (kvp.Value == null || kvp.Value.Count == 0) continue;
                Color peerColor = TeamCreatePeerManager.GetPeerColor(kvp.Key);
                string username = GetUsername(kvp.Key);
                foreach (string guid in kvp.Value)
                {
                    if (!perObject.TryGetValue(guid, out HighlightData data)) { data = new HighlightData(); perObject[guid] = data; }
                    data.Colors.Add(peerColor);
                    data.Names.Add(username);
                }
            }

            float opacity = HighlightOpacity;

            foreach (var kvp in perObject)
            {
                GameObject go = TeamCreateIdentity.FindByGuid(kvp.Key);
                if (go == null) continue;
                try { DrawHighlight(go, kvp.Value, opacity, sceneView.camera); } catch { }
            }

            Handles.color = Color.white;
        }

        private static string GetUsername(string peerId)
        {
            foreach (var p in TeamCreateSession.GetPeers()) if (p.PeerId == peerId) return p.Username;
            return peerId.Substring(0, Math.Min(8, peerId.Length));
        }

        private static Color BlendColors(List<Color> colors)
        {
            if (colors.Count == 1) return colors[0];
            float r = 0f, g = 0f, b = 0f;
            foreach (Color c in colors) { r += c.r; g += c.g; b += c.b; }
            return new Color(r / colors.Count, g / colors.Count, b / colors.Count, 1f);
        }

        private static void DrawHighlight(GameObject go, HighlightData data, float opacity, Camera cam)
        {
            Color blended = BlendColors(data.Colors);
            Color outlineCol = new Color(blended.r, blended.g, blended.b, 1f);
            Color fillCol = new Color(blended.r, blended.g, blended.b, Mathf.Max(opacity, 0f));

            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            Bounds worldBounds = new Bounds();
            bool hasWorldBounds = false;

            foreach (var r in renderers)
            {
                if (r == null) continue;

                Bounds wb = r.bounds;
                if (!hasWorldBounds) { worldBounds = wb; hasWorldBounds = true; }
                else worldBounds.Encapsulate(wb);

                Mesh mesh = GetRendererMesh(r);
                MeshHighlightData hd = mesh != null ? GetOrBuildHighlightData(mesh) : null;

                if (hd != null)
                {
                    DrawMeshHighlight(r, hd, outlineCol, fillCol, cam);
                }
                else
                {
                    Bounds lb = r.localBounds;
                    Vector3 c = lb.center;
                    Vector3 e = lb.extents;
                    if (e.sqrMagnitude < 0.0001f) e = Vector3.one * 0.05f;
                    Handles.matrix = r.transform.localToWorldMatrix;
                    Handles.color = outlineCol;
                    Handles.DrawWireCube(c, lb.size);
                }
            }

            if (!hasWorldBounds)
            {
                const float sz = 0.12f;
                Vector3 p = go.transform.position;
                Handles.matrix = Matrix4x4.identity;
                Handles.color = outlineCol;
                Handles.DrawLine(p - Vector3.right * sz, p + Vector3.right * sz);
                Handles.DrawLine(p - Vector3.up * sz, p + Vector3.up * sz);
                Handles.DrawLine(p - Vector3.forward * sz, p + Vector3.forward * sz);
                worldBounds = new Bounds(p, Vector3.zero);
            }

            Handles.matrix = Matrix4x4.identity;
            Handles.color = Color.white;
            Vector3 labelPos = worldBounds.center + Vector3.up * (worldBounds.extents.y + 0.15f);
            string label = string.Join(", ", data.Names.ToArray());
            var labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                normal = { textColor = outlineCol }
            };
            Handles.Label(labelPos, label, labelStyle);
        }

        private static Mesh GetRendererMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static MeshHighlightData GetOrBuildHighlightData(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (_meshHighlightCache.TryGetValue(id, out var cached)) return cached;


            int totalTris = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                totalTris += mesh.GetTriangles(s).Length / 3;

            if (totalTris > MaxTrisForMeshHighlight)
            {
                _meshHighlightCache[id] = null;
                return null;
            }

            Vector3[] verts = mesh.vertices;


            var allTrisL = new List<int>(totalTris * 3);
            for (int s = 0; s < mesh.subMeshCount; s++)
                allTrisL.AddRange(mesh.GetTriangles(s));
            int[] tris = allTrisL.ToArray();
            int fc = tris.Length / 3;


            var faceN = new Vector3[fc];
            for (int f = 0; f < fc; f++)
            {
                Vector3 e1 = verts[tris[f * 3 + 1]] - verts[tris[f * 3]];
                Vector3 e2 = verts[tris[f * 3 + 2]] - verts[tris[f * 3]];
                faceN[f] = Vector3.Cross(e1, e2);
            }


            var edgeMap = new Dictionary<long, (int v0, int v1, int f0, int f1)>(tris.Length);
            for (int f = 0; f < fc; f++)
            {
                RegisterEdge(edgeMap, tris[f * 3], tris[f * 3 + 1], f);
                RegisterEdge(edgeMap, tris[f * 3 + 1], tris[f * 3 + 2], f);
                RegisterEdge(edgeMap, tris[f * 3 + 2], tris[f * 3], f);
            }

            var ev0 = new List<int>(edgeMap.Count);
            var ev1 = new List<int>(edgeMap.Count);
            var en0 = new List<Vector3>(edgeMap.Count);
            var en1 = new List<Vector3>(edgeMap.Count);
            foreach (var e in edgeMap.Values)
            {
                ev0.Add(e.v0);
                ev1.Add(e.v1);
                en0.Add(faceN[e.f0]);
                en1.Add(e.f1 >= 0 ? faceN[e.f1] : faceN[e.f0]);
            }

            var result = new MeshHighlightData
            {
                Vertices = verts,
                TriIndices = tris,
                EdgeV0 = ev0.ToArray(),
                EdgeV1 = ev1.ToArray(),
                EdgeN0 = en0.ToArray(),
                EdgeN1 = en1.ToArray()
            };
            _meshHighlightCache[id] = result;
            return result;
        }

        private static void RegisterEdge(Dictionary<long, (int v0, int v1, int f0, int f1)> map, int a, int b, int faceIdx)
        {
            long key = MeshEdgeKey(a, b);
            if (map.TryGetValue(key, out var existing))
                map[key] = (existing.v0, existing.v1, existing.f0, faceIdx);
            else
                map[key] = (a, b, faceIdx, -1);
        }

        private static long MeshEdgeKey(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        private static void DrawMeshHighlight(Renderer r, MeshHighlightData hd, Color outlineCol, Color fillCol, Camera cam)
        {
            Matrix4x4 l2w = r.transform.localToWorldMatrix;
            Vector3 camPos = cam.transform.position;
            Vector3[] verts = hd.Vertices;

            Handles.matrix = Matrix4x4.identity;

            if (fillCol.a > 0.01f)
            {
                Handles.color = fillCol;
                int[] tris = hd.TriIndices;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 v0 = l2w.MultiplyPoint3x4(verts[tris[i]]);
                    Vector3 v1 = l2w.MultiplyPoint3x4(verts[tris[i + 1]]);
                    Vector3 v2 = l2w.MultiplyPoint3x4(verts[tris[i + 2]]);

                    if (Vector3.Dot(Vector3.Cross(v1 - v0, v2 - v0), v0 - camPos) > 0f) continue;
                    Handles.DrawAAConvexPolygon(v0, v1, v2);
                }
            }

            Vector3 camLocal = r.transform.InverseTransformPoint(camPos);
            var silLines = new List<Vector3>(hd.EdgeV0.Length);
            for (int i = 0; i < hd.EdgeV0.Length; i++)
            {
                Vector3 origin = verts[hd.EdgeV0[i]];

                float d0 = Vector3.Dot(hd.EdgeN0[i], camLocal - origin);
                float d1 = Vector3.Dot(hd.EdgeN1[i], camLocal - origin);
                if ((d0 > 0f) == (d1 > 0f)) continue;
                silLines.Add(l2w.MultiplyPoint3x4(origin));
                silLines.Add(l2w.MultiplyPoint3x4(verts[hd.EdgeV1[i]]));
            }
            if (silLines.Count >= 2)
            {
                Handles.color = outlineCol;
                Handles.DrawLines(silLines.ToArray());
            }
        }

        private static void OnHierarchyWindowItemGUI(int instanceID, Rect selectionRect)
        {
            if (!TeamCreateSession.IsConnected) return;
            if (!_highlightsEnabled) return;
            if (Event.current.type != EventType.Repaint) return;
            if (_hierarchyDirectHighlights.Count == 0 && _hierarchyAncestorHighlights.Count == 0) return;

            var go = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
            if (go == null) return;
            string guid = TeamCreateIdentity.GetGuid(go);
            if (string.IsNullOrEmpty(guid)) return;

            float barAlpha, bgAlpha;
            Color blended;

            if (_hierarchyDirectHighlights.TryGetValue(guid, out var directColors))
            {
                blended = BlendColors(directColors);
                barAlpha = 0.9f;
                bgAlpha = 0.15f;
            }
            else if (_hierarchyAncestorHighlights.TryGetValue(guid, out var ancColors))
            {
                blended = BlendColors(ancColors);
                barAlpha = 0.45f;
                bgAlpha = 0.07f;
            }
            else return;

            float barWidth = 3f;
            EditorGUI.DrawRect(new Rect(selectionRect.xMin, selectionRect.y, barWidth, selectionRect.height),
                new Color(blended.r, blended.g, blended.b, barAlpha));
            EditorGUI.DrawRect(new Rect(selectionRect.xMin + barWidth, selectionRect.y, selectionRect.width - barWidth, selectionRect.height),
                new Color(blended.r, blended.g, blended.b, bgAlpha));
        }

        private static void OnProjectWindowItemGUI(string itemGuid, Rect selectionRect)
        {
            if (!TeamCreateSession.IsConnected) return;
            if (!_highlightsEnabled) return;
            if (string.IsNullOrEmpty(itemGuid)) return;

            List<string> directPeers;
            PeerAssetSelections.TryGetValue(itemGuid, out directPeers);
            bool hasDirect = directPeers != null && directPeers.Count > 0;

            List<string> containedPeers = null;
            string itemPath = AssetDatabase.GUIDToAssetPath(itemGuid);

            if (!hasDirect && Event.current.type == EventType.Repaint)
            {
                if (!string.IsNullOrEmpty(itemPath) && AssetDatabase.IsValidFolder(itemPath) && !TeamCreateAssetSync.IsExcluded(itemPath))
                {
                    string prefix = itemPath + "/";
                    foreach (var kvp in PeerAssetPathCache)
                    {
                        foreach (string p in kvp.Value)
                        {
                            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                if (containedPeers == null) containedPeers = new List<string>();
                                if (!containedPeers.Contains(kvp.Key)) containedPeers.Add(kvp.Key);
                                break;
                            }
                        }
                    }
                }
            }

            var peerIds = hasDirect ? directPeers : containedPeers;
            if (peerIds == null || peerIds.Count == 0) return;

            float opacityScale = hasDirect ? 1f : 0.5f;

            var colors = new List<Color>();
            foreach (string pid in peerIds)
                colors.Add(TeamCreatePeerManager.GetPeerColor(pid));

            Color blended = BlendColors(colors);

            if (Event.current.type == EventType.Repaint && !TeamCreateAssetSync.IsExcluded(itemPath))
            {
                float barWidth = 3f;
                var barRect = new Rect(selectionRect.x, selectionRect.y, barWidth, selectionRect.height);
                EditorGUI.DrawRect(barRect, new Color(blended.r, blended.g, blended.b, 0.9f * opacityScale));

                var bgRect = new Rect(selectionRect.x + barWidth, selectionRect.y, selectionRect.width - barWidth, selectionRect.height);
                EditorGUI.DrawRect(bgRect, new Color(blended.r, blended.g, blended.b, 0.15f * opacityScale));

                if (hasDirect && selectionRect.height <= 20f && selectionRect.width > 100f)
                {
                    var names = new List<string>();
                    foreach (string pid in peerIds) names.Add(GetUsername(pid));
                    string label = string.Join(", ", names.ToArray());
                    var labelStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = new Color(blended.r, blended.g, blended.b, 0.85f) },
                        alignment = TextAnchor.MiddleRight
                    };
                    GUI.Label(selectionRect, label, labelStyle);
                }
            }
        }

        public static void Clear()
        {
            PeerSelections.Clear();
            PeerAssetSelections.Clear();
            PeerAssetPathCache.Clear();
            _hierarchyDirectHighlights.Clear();
            _hierarchyAncestorHighlights.Clear();
            SceneView.RepaintAll();
            EditorApplication.RepaintProjectWindow();
            EditorApplication.RepaintHierarchyWindow();
        }
    }
}
#endif
