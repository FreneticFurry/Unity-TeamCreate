#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public static class TeamCreatePeerManager
    {
        private class PeerState
        {
            public string Username;
            public Color DisplayColor;
            public bool HasTransform;
            public Vector3 CurrentPos;
            public Quaternion CurrentRot;
            public Vector3 TargetPos;
            public Quaternion TargetRot;
            public GameObject Instance;
        }

        private static readonly Dictionary<string, PeerState> Peers = new Dictionary<string, PeerState>();
        private static readonly Dictionary<string, GameObject> PeerInstances = new Dictionary<string, GameObject>();

        private static readonly HashSet<float> UsedHues = new HashSet<float>();
        private static readonly Dictionary<string, float> PeerHues = new Dictionary<string, float>();
        private static readonly System.Random Rng = new System.Random();

        private static double _prevTime;
        private static double _lastSendTime;
        private static Vector3 _lastSentPos;
        private static Quaternion _lastSentRot;
        private static bool _hasSentOnce;

        private const double SendIntervalSeconds = 1.0 / 30.0;
        private const float SendPosThresholdSqr = 0.0001f;
        private const float SendRotThresholdDot = 1.9999f;

        private const float PosConvergenceThresholdSqr = 0.000001f;
        private const float RotConvergenceDot = 1.99999f;

        private static float SmoothSpeed => EditorPrefs.GetFloat("TeamCreate_InterpSpeed", 20f);

        private static GameObject _peerPrefab;
        private static bool _prefabLoadAttempted;

        private static bool _hasActiveInterpolation;

        static TeamCreatePeerManager()
        {
            _prevTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += OnUpdate;
            SceneView.duringSceneGui += OnSceneGUI;
            Selection.selectionChanged += OnSelectionChanged;
            TeamCreateSession.OnPeersChanged += OnPeersChanged;
            TeamCreateSession.OnSessionEnded += ClearAll;
            AssemblyReloadEvents.beforeAssemblyReload += Unregister;
        }

        private static void Unregister()
        {
            EditorApplication.update -= OnUpdate;
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= OnSelectionChanged;
            TeamCreateSession.OnPeersChanged -= OnPeersChanged;
            TeamCreateSession.OnSessionEnded -= ClearAll;
            AssemblyReloadEvents.beforeAssemblyReload -= Unregister;
            ClearAll();
        }

        private static GameObject LoadPrefab()
        {
            if (_prefabLoadAttempted) return _peerPrefab;
            _prefabLoadAttempted = true;
            _peerPrefab = Resources.Load<GameObject>("PeerPrefab");
            if (_peerPrefab == null)
                Debug.LogWarning("[TeamCreate] PeerPrefab not found at Assets/TeamCreate/Resources/PeerPrefab.prefab.");
            return _peerPrefab;
        }

        private static GameObject SpawnPeerInstance(string peerId)
        {
            var prefab = LoadPrefab();
            if (prefab == null) return null;
            var instance = UnityEngine.Object.Instantiate(prefab);
            instance.name = $"[TC_Peer_{peerId}]";
            instance.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave | HideFlags.NotEditable;
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave | HideFlags.NotEditable;
            return instance;
        }

        private static void DestroyPeerInstance(string peerId)
        {
            if (PeerInstances.TryGetValue(peerId, out var go))
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
                PeerInstances.Remove(peerId);
            }
        }

        private static void OnSelectionChanged()
        {
            if (PeerInstances.Count == 0) return;
            var sel = new List<UnityEngine.Object>(Selection.objects);
            bool changed = false;
            for (int i = sel.Count - 1; i >= 0; i--)
            {
                if (sel[i] is GameObject go && go.name.StartsWith("[TC_Peer_", StringComparison.Ordinal))
                { sel.RemoveAt(i); changed = true; }
            }
            if (changed) Selection.objects = sel.ToArray();
        }

        private static Color AssignPeerColor(string peerId)
        {
            if (PeerHues.TryGetValue(peerId, out float existing))
                return Color.HSVToRGB(existing, 0.85f, 1.0f);

            float hue = (float)Rng.NextDouble();
            for (int attempt = 0; attempt < 30; attempt++)
            {
                bool tooClose = false;
                foreach (float used in UsedHues)
                {
                    float diff = Mathf.Abs(hue - used);
                    if (diff > 0.5f) diff = 1f - diff;
                    if (diff < 0.15f) { tooClose = true; break; }
                }
                if (!tooClose) break;
                hue = (float)Rng.NextDouble();
            }
            UsedHues.Add(hue);
            PeerHues[peerId] = hue;
            return Color.HSVToRGB(hue, 0.85f, 1.0f);
        }

        private static void ReleasePeerColor(string peerId)
        {
            if (PeerHues.TryGetValue(peerId, out float hue))
            { UsedHues.Remove(hue); PeerHues.Remove(peerId); }
        }

        public static Color GetPeerColor(string peerId)
        {
            if (Peers.TryGetValue(peerId, out PeerState state)) return state.DisplayColor;
            if (PeerHues.TryGetValue(peerId, out float hue)) return Color.HSVToRGB(hue, 0.85f, 1.0f);
            return AssignPeerColor(peerId);
        }

        private static void SendCurrentCameraTransform()
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null) return;
            Vector3 pos = sv.camera.transform.position;
            Quaternion rot = sv.camera.transform.rotation;
            _lastSentPos = pos;
            _lastSentRot = rot;
            _hasSentOnce = true;
            var payload = new PeerTransformPayload
            {
                PeerId = TeamCreateSession.LocalPeerId,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotX = rot.x,
                RotY = rot.y,
                RotZ = rot.z,
                RotW = rot.w
            };
            TeamCreateSession.SendMessage(Protocol.CreateMessage(
                MessageType.PEER_TRANSFORM, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
        }

        private static void OnPeersChanged(List<PeerInfo> peers)
        {
            var currentIds = new HashSet<string>();
            bool anyAdded = false;

            foreach (var p in peers)
            {
                if (p.PeerId == TeamCreateSession.LocalPeerId) continue;
                currentIds.Add(p.PeerId);
                if (!Peers.ContainsKey(p.PeerId))
                {
                    var instance = SpawnPeerInstance(p.PeerId);
                    if (instance != null) PeerInstances[p.PeerId] = instance;
                    Peers[p.PeerId] = new PeerState
                    {
                        Username = p.Username,
                        DisplayColor = AssignPeerColor(p.PeerId),
                        CurrentRot = Quaternion.identity,
                        TargetRot = Quaternion.identity,
                        HasTransform = false,
                        Instance = instance
                    };
                    anyAdded = true;
                }
                else
                {
                    Peers[p.PeerId].Username = p.Username;
                }
            }

            var toRemove = new List<string>();
            foreach (string id in Peers.Keys)
                if (!currentIds.Contains(id)) toRemove.Add(id);
            foreach (string id in toRemove)
            { DestroyPeerInstance(id); ReleasePeerColor(id); Peers.Remove(id); }

            if (anyAdded && TeamCreateSession.IsConnected)
                SendCurrentCameraTransform();

            SceneView.RepaintAll();
        }

        private static void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - _prevTime), 0.0001f, 0.1f);
            _prevTime = now;

            if (TeamCreateSession.IsConnected && now - _lastSendTime >= SendIntervalSeconds)
            {
                _lastSendTime = now;
                SceneView sv = SceneView.lastActiveSceneView;
                if (sv != null && sv.camera != null)
                {
                    Vector3 pos = sv.camera.transform.position;
                    Quaternion rot = sv.camera.transform.rotation;

                    bool moved = !_hasSentOnce
                        || (pos - _lastSentPos).sqrMagnitude > SendPosThresholdSqr
                        || Mathf.Abs(Quaternion.Dot(rot, _lastSentRot)) < SendRotThresholdDot;

                    if (moved)
                    {
                        _lastSentPos = pos;
                        _lastSentRot = rot;
                        _hasSentOnce = true;
                        var payload = new PeerTransformPayload
                        {
                            PeerId = TeamCreateSession.LocalPeerId,
                            PosX = pos.x,
                            PosY = pos.y,
                            PosZ = pos.z,
                            RotX = rot.x,
                            RotY = rot.y,
                            RotZ = rot.z,
                            RotW = rot.w
                        };
                        TeamCreateSession.SendMessage(Protocol.CreateMessage(
                            MessageType.PEER_TRANSFORM, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload)));
                    }
                }
            }

            if (Peers.Count == 0) return;

            float smoothT = 1f - Mathf.Exp(-SmoothSpeed * dt);
            bool anyActiveTarget = false;

            TeamCreateSession.IsApplyingRemoteChange = true;
            try
            {
                foreach (var kvp in Peers)
                {
                    PeerState s = kvp.Value;
                    if (!s.HasTransform) continue;

                    float posDeltaSqr = (s.CurrentPos - s.TargetPos).sqrMagnitude;
                    float rotDot = Mathf.Abs(Quaternion.Dot(s.CurrentRot, s.TargetRot));

                    bool posConverged = posDeltaSqr < PosConvergenceThresholdSqr;
                    bool rotConverged = rotDot > RotConvergenceDot;

                    if (!posConverged || !rotConverged)
                    {
                        s.CurrentPos = Vector3.Lerp(s.CurrentPos, s.TargetPos, smoothT);
                        s.CurrentRot = Quaternion.Slerp(s.CurrentRot, s.TargetRot, smoothT);
                        anyActiveTarget = true;
                    }
                    else
                    {
                        s.CurrentPos = s.TargetPos;
                        s.CurrentRot = s.TargetRot;
                    }

                    if (s.Instance != null)
                    {
                        s.Instance.transform.position = s.CurrentPos;
                        s.Instance.transform.rotation = s.CurrentRot;
                        s.Instance.transform.localScale = Vector3.one;
                    }
                }
            }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }

            _hasActiveInterpolation = anyActiveTarget;

            bool anyHasTransform = false;
            foreach (var kvp in Peers)
                if (kvp.Value.HasTransform) { anyHasTransform = true; break; }

            if (anyHasTransform)
                SceneView.RepaintAll();
        }

        public static void ApplyPeerTransform(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<PeerTransformPayload>(message.Payload);
            if (payload == null) return;
            if (payload.PeerId == TeamCreateSession.LocalPeerId) return;

            if (!Peers.TryGetValue(payload.PeerId, out PeerState state))
            {
                if (!TeamCreateSession.IsConnected) return;
                var instance = SpawnPeerInstance(payload.PeerId);
                if (instance != null) PeerInstances[payload.PeerId] = instance;
                state = new PeerState
                {
                    Username = payload.PeerId.Substring(0, Math.Min(8, payload.PeerId.Length)),
                    DisplayColor = AssignPeerColor(payload.PeerId),
                    CurrentRot = Quaternion.identity,
                    TargetRot = Quaternion.identity,
                    HasTransform = false,
                    Instance = instance
                };
                Peers[payload.PeerId] = state;
            }

            var newPos = new Vector3(payload.PosX, payload.PosY, payload.PosZ);
            var newRot = new Quaternion(payload.RotX, payload.RotY, payload.RotZ, payload.RotW);

            state.TargetPos = newPos;
            state.TargetRot = newRot;

            if (!state.HasTransform)
            {
                state.CurrentPos = newPos;
                state.CurrentRot = newRot;
                state.HasTransform = true;

                if (state.Instance != null)
                {
                    state.Instance.transform.position = newPos;
                    state.Instance.transform.rotation = newRot;
                }
            }

        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!TeamCreateSession.IsConnected) return;
            if (Peers.Count == 0) return;

            Handles.matrix = Matrix4x4.identity;

            foreach (var kvp in Peers)
            {
                PeerState state = kvp.Value;
                if (!state.HasTransform) continue;

                Color col = state.DisplayColor;

                var labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 12,
                    normal = { textColor = col }
                };

                Handles.Label(state.CurrentPos + Vector3.up * 0.5f, state.Username, labelStyle);
            }

            Handles.color = Color.white;
        }

        public static void HandlePing(NetworkMessage message)
        {
            var pong = new PongPayload { OriginalTimestamp = message.Timestamp };
            TeamCreateSession.SendMessage(Protocol.CreateMessage(
                MessageType.PONG, TeamCreateSession.LocalPeerId, Protocol.Serialize(pong)));
        }

        public static void HandlePong(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<PongPayload>(message.Payload);
            if (payload == null) return;
            long rttTicks = DateTime.UtcNow.Ticks - payload.OriginalTimestamp;
            TeamCreateSession.UpdatePeerPing(message.SenderId, rttTicks / TimeSpan.TicksPerMillisecond / 2);
        }

        private static void ClearAll()
        {
            foreach (string id in new List<string>(PeerInstances.Keys))
                DestroyPeerInstance(id);
            Peers.Clear();
            PeerInstances.Clear();
            UsedHues.Clear();
            PeerHues.Clear();
            _hasSentOnce = false;
            _hasActiveInterpolation = false;
            _prefabLoadAttempted = false;
            _peerPrefab = null;
            SceneView.RepaintAll();
        }
    }
}
#endif
