#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TeamCreate.Editor
{
    public class TeamCreateWindow : EditorWindow
    {
        string hostPort = "7777";
        string joinIp = "127.0.0.1";
        string joinPort = "7777";
        string username = System.Environment.MachineName;

        Vector2 peerScroll;
        Vector2 pendingScroll;
        readonly HashSet<string> _expandedPkgDiff = new HashSet<string>();

        List<PeerInfo> peers = new List<PeerInfo>();
        List<PendingJoinRequest> pending = new List<PendingJoinRequest>();

        string statusText = "Idle";

        GUIStyle toptitle;
        GUIStyle subtitle;
        GUIStyle statusStyle;
        GUIStyle sectionTitle;
        GUIStyle card;

        [MenuItem("Tools/TeamCreate")]
        static void Open()
        {
            var w = GetWindow<TeamCreateWindow>("TeamCreate");
            w.minSize = new Vector2(320, 600);
        }

        void OnEnable()
        {
            TeamCreateSession.OnStatusChanged -= OnStatusChanged;
            TeamCreateSession.OnPeersChanged -= OnPeersChanged;
            TeamCreateSession.OnJoinRequest -= OnJoinRequest;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update -= OnEditorUpdate;

            TeamCreateSession.OnStatusChanged += OnStatusChanged;
            TeamCreateSession.OnPeersChanged += OnPeersChanged;
            TeamCreateSession.OnJoinRequest += OnJoinRequest;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += OnEditorUpdate;

            peers = TeamCreateSession.GetPeers();
            statusText = TeamCreateSession.CurrentState.ToString();
        }

        void OnDisable()
        {
            TeamCreateSession.OnStatusChanged -= OnStatusChanged;
            TeamCreateSession.OnPeersChanged -= OnPeersChanged;
            TeamCreateSession.OnJoinRequest -= OnJoinRequest;

            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update -= OnEditorUpdate;
        }

        void OnEditorUpdate()
        {
            if (TeamCreateSession.CurrentState == SessionState.Reconnecting)
                Repaint();
        }

        void OnPlayModeChanged(PlayModeStateChange s)
        {
            Repaint();
        }

        void OnStatusChanged(string s)
        {
            statusText = s;
            Repaint();
        }

        void OnPeersChanged(List<PeerInfo> p)
        {
            peers = new List<PeerInfo>(p);
            Repaint();
        }

        void OnJoinRequest(PendingJoinRequest r)
        {
            if (pending.Exists(p => p.PeerId == r.PeerId)) return;
            pending.Add(r);
            Repaint();
        }

        void InitStyles()
        {
            if (toptitle != null) return;

            toptitle = new GUIStyle(EditorStyles.boldLabel);
            toptitle.alignment = TextAnchor.MiddleCenter;
            toptitle.fontSize = 16;

            subtitle = new GUIStyle(EditorStyles.centeredGreyMiniLabel);

            statusStyle = new GUIStyle(EditorStyles.boldLabel);
            statusStyle.alignment = TextAnchor.MiddleCenter;
            statusStyle.fontSize = 13;

            sectionTitle = new GUIStyle(EditorStyles.miniBoldLabel);
            sectionTitle.fontSize = 10;

            card = new GUIStyle(EditorStyles.helpBox);
            card.padding = new RectOffset(10, 10, 8, 8);
        }

        void OnGUI()
        {
            InitStyles();

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                GUILayout.Space(20);

                GUILayout.Label("TeamCreate", EditorStyles.centeredGreyMiniLabel);

                GUILayout.Space(6);

                GUI.color = Color.yellow;
                GUILayout.Label("⚠ Not available in Play Mode", statusStyle);
                GUI.color = Color.white;

                GUILayout.Space(4);

                GUILayout.Label("Exit Play Mode to use TeamCreate", EditorStyles.centeredGreyMiniLabel);

                return;
            }

            var state = TeamCreateSession.CurrentState;

            DrawHeader(state);

            GUILayout.Space(6);
            DrawSeparator();

            if (IsActive(state))
                DrawActiveSession(state);
            else
                DrawIdleSections();

            if (!TeamCreateSession.IsHosting)
                DrawJoinPackageDiff();

            GUILayout.Space(6);
            DrawSeparator();

            DrawSettings();

            GUILayout.Space(6);
            DrawSeparator();

            DrawPending();

            GUILayout.Space(6);
            DrawSeparator();

            DrawPeers();
        }

        bool IsActive(SessionState s)
        {
            return s == SessionState.Hosting ||
                   s == SessionState.Connected ||
                   s == SessionState.Syncing ||
                   s == SessionState.Connecting ||
                   s == SessionState.Reconnecting;
        }

        void DrawHeader(SessionState state)
        {
            GUILayout.Label("TeamCreate - Made by Frenetic!", toptitle);

            GUILayout.Label("Testers - Trudolph, Mustaro, Ryuu\n\nThank you for using this asset!\nif you can support that would help me be able to continue\nto make free assets like this!\n", subtitle);

            if (GUILayout.Button("Click to support ♥\n", subtitle))
            {
                Application.OpenURL("https://patreon.com/FreneticFurry");
            }

            Color c =
                state == SessionState.Hosting || state == SessionState.Connected ? Color.green :
                state == SessionState.Disconnected ? Color.red :
                Color.yellow;

            var prev = GUI.color;
            GUI.color = c;
            GUILayout.Label("● " + statusText, statusStyle);
            GUI.color = prev;
        }

        void DrawIdleSections()
        {
            GUILayout.Label("Connect", sectionTitle);

            GUILayout.BeginVertical(card);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(50));
            username = EditorGUILayout.TextField(username);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            GUILayout.Space(4);

            GUILayout.Label("Host", sectionTitle);

            GUILayout.BeginVertical(card);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(50));
            hostPort = EditorGUILayout.TextField(hostPort, GUILayout.Width(80));
            GUILayout.Label("Private", GUILayout.Width(50));
            TeamCreateSession.IsPrivateMode = EditorGUILayout.Toggle(TeamCreateSession.IsPrivateMode);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            GUI.backgroundColor = new Color(0.35f, 0.6f, 1f);

            if (GUILayout.Button("Start Hosting", GUILayout.Height(28)))
                if (int.TryParse(hostPort, out int p))
                {
                    TeamCreateSession.LocalUsername = username;
                    TeamCreateSession.StartHosting(p);
                }

            GUI.backgroundColor = Color.white;

            GUILayout.EndVertical();

            GUILayout.Space(4);

            GUILayout.Label("Join", sectionTitle);

            GUILayout.BeginVertical(card);

            GUILayout.BeginHorizontal();
            GUILayout.Label("IP", GUILayout.Width(50));
            joinIp = EditorGUILayout.TextField(joinIp);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(50));
            joinPort = EditorGUILayout.TextField(joinPort, GUILayout.Width(80));
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            if (GUILayout.Button("Connect", GUILayout.Height(28)))
                if (int.TryParse(joinPort, out int p))
                {
                    TeamCreateSession.LocalUsername = username;
                    TeamCreateSession.StartConnecting(joinIp, p);
                }

            GUILayout.EndVertical();
        }

        void DrawActiveSession(SessionState state)
        {
            GUILayout.Label("Session", sectionTitle);

            GUILayout.BeginVertical(card);

            if (state == SessionState.Hosting)
                GUILayout.Label("Hosting on port " + hostPort);
            else if (state == SessionState.Connected)
                GUILayout.Label("Connected to " + joinIp + ":" + joinPort);
            else if (state == SessionState.Syncing)
                GUILayout.Label("Syncing with host...");
            else if (state == SessionState.Connecting)
                GUILayout.Label("Connecting to " + joinIp + ":" + joinPort);
            else if (state == SessionState.Reconnecting)
            {
                double remaining = TeamCreateSession.NextReconnectTime - EditorApplication.timeSinceStartup;
                string attemptLine = $"Reconnecting ({TeamCreateSession.ReconnectAttempt}/{TeamCreateSession.TotalReconnectAttempts})";
                string timeLine = remaining > 0 ? $"Next attempt in {Mathf.CeilToInt((float)remaining)}s" : "Attempting...";
                GUILayout.Label(attemptLine);
                GUILayout.Label(timeLine, EditorStyles.miniLabel);

                GUILayout.Space(4);

                GUI.backgroundColor = new Color(1f, 0.6f, 0.2f);
                if (GUILayout.Button("Stop Reconnecting", GUILayout.Height(28)))
                    TeamCreateSession.CancelReconnect();
                GUI.backgroundColor = Color.white;

                GUILayout.EndVertical();
                return;
            }

            if (state == SessionState.Hosting)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Private Mode");
                TeamCreateSession.IsPrivateMode = EditorGUILayout.Toggle(TeamCreateSession.IsPrivateMode);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);

            GUI.backgroundColor = new Color(1f, 0.35f, 0.35f);

            if (GUILayout.Button("Disconnect", GUILayout.Height(28)))
                if (EditorUtility.DisplayDialog("TeamCreate", "Disconnect from session?", "Disconnect", "Cancel"))
                    TeamCreateSession.Disconnect();

            GUI.backgroundColor = Color.white;

            GUILayout.EndVertical();
        }

        void DrawJoinPackageDiff()
        {
            if (!TeamCreateSession.HasJoinPackageDiff) return;

            GUILayout.Space(6);
            DrawSeparator();
            GUILayout.Space(4);

            var warnStyle = new GUIStyle(EditorStyles.helpBox);
            GUILayout.BeginVertical(warnStyle);

            GUILayout.BeginHorizontal();
            GUI.color = new Color(1f, 0.85f, 0.3f);
            GUILayout.Label("⚠ Package Mismatch with Host", EditorStyles.boldLabel);
            GUI.color = Color.white;
            if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
                TeamCreateSession.ClearJoinPackageDiff();
            GUILayout.EndHorizontal();

            GUILayout.Space(2);

            if (TeamCreateSession.JoinMissingPackages.Count > 0)
            {
                GUILayout.Label("Host has — you're missing:", EditorStyles.miniLabel);
                foreach (var pkg in TeamCreateSession.JoinMissingPackages)
                    GUILayout.Label("  • " + pkg, EditorStyles.miniLabel);
            }
            if (TeamCreateSession.JoinExtraPackages.Count > 0)
            {
                GUILayout.Label("You have — host is missing:", EditorStyles.miniLabel);
                foreach (var pkg in TeamCreateSession.JoinExtraPackages)
                    GUILayout.Label("  • " + pkg, EditorStyles.miniLabel);
            }
            if (TeamCreateSession.JoinVersionMismatches.Count > 0)
            {
                GUILayout.Label("Version mismatches:", EditorStyles.miniLabel);
                foreach (var pkg in TeamCreateSession.JoinVersionMismatches)
                    GUILayout.Label("  • " + pkg, EditorStyles.miniLabel);
            }

            GUILayout.EndVertical();
        }

        void DrawPending()
        {
            if (pending.Count == 0) return;

            GUILayout.Label("Pending Join Requests (" + pending.Count + ")", sectionTitle);

            pendingScroll = GUILayout.BeginScrollView(pendingScroll, GUILayout.MaxHeight(130));

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var r = pending[i];

                GUILayout.BeginVertical(card);

                GUILayout.Label(r.Username, EditorStyles.boldLabel);
                GUILayout.Label(r.IpAddress, EditorStyles.miniLabel);

                if (r.HasPackageDiff)
                {
                    GUILayout.Space(2);
                    bool expanded = _expandedPkgDiff.Contains(r.PeerId);
                    GUI.backgroundColor = new Color(1f, 0.35f, 0.35f);
                    if (GUILayout.Button(expanded ? "▲ Package Mismatch" : "▼ Package Mismatch", GUILayout.Height(20)))
                    {
                        if (expanded) _expandedPkgDiff.Remove(r.PeerId);
                        else _expandedPkgDiff.Add(r.PeerId);
                    }
                    GUI.backgroundColor = Color.white;

                    if (expanded)
                    {
                        GUILayout.Space(3);
                        if (r.MissingPackages != null && r.MissingPackages.Count > 0)
                        {
                            GUILayout.Label("Peer is missing:", EditorStyles.boldLabel);
                            foreach (var pkg in r.MissingPackages)
                                GUILayout.Label("  " + pkg, EditorStyles.miniLabel);
                        }
                        if (r.ExtraPackages != null && r.ExtraPackages.Count > 0)
                        {
                            GUILayout.Label("Peer has extra:", EditorStyles.boldLabel);
                            foreach (var pkg in r.ExtraPackages)
                                GUILayout.Label("  " + pkg, EditorStyles.miniLabel);
                        }
                        if (r.VersionMismatches != null && r.VersionMismatches.Count > 0)
                        {
                            GUILayout.Label("Version mismatches:", EditorStyles.boldLabel);
                            foreach (var pkg in r.VersionMismatches)
                                GUILayout.Label("  " + pkg, EditorStyles.miniLabel);
                        }
                        GUILayout.Space(3);
                    }
                }

                GUILayout.BeginHorizontal();

                GUI.backgroundColor = new Color(0.4f, 1f, 0.5f);

                if (GUILayout.Button("Allow"))
                {
                    _expandedPkgDiff.Remove(r.PeerId);
                    r.Respond(true);
                    pending.RemoveAt(i);
                }

                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);

                if (GUILayout.Button("Deny"))
                {
                    _expandedPkgDiff.Remove(r.PeerId);
                    r.Respond(false);
                    pending.RemoveAt(i);
                }

                GUI.backgroundColor = Color.white;

                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
            }

            GUILayout.EndScrollView();
        }

        void DrawPeers()
        {
            GUILayout.Label("Peers (" + peers.Count + ")", sectionTitle);

            peerScroll = GUILayout.BeginScrollView(peerScroll, GUILayout.MaxHeight(220));

            bool host = TeamCreateSession.IsHosting;

            foreach (var p in peers)
            {
                GUILayout.BeginVertical(card);

                GUILayout.BeginHorizontal();

                Color dot = TeamCreatePeerManager.GetPeerColor(p.PeerId);
                Color tintedDot = Color.Lerp(dot, Color.white, 0.35f);

                var prev = GUI.color;
                GUI.color = tintedDot;
                GUILayout.Label("■", GUILayout.Width(14));
                GUI.color = prev;

                GUILayout.Label(p.Username, EditorStyles.boldLabel);

                GUILayout.FlexibleSpace();

                long ping = TeamCreateSession.GetPeerPing(p.PeerId);

                Color pingColor =
                    ping == 0 ? Color.gray :
                    ping < 80 ? Color.green :
                    ping < 200 ? Color.yellow :
                    Color.red;

                GUI.color = pingColor;

                GUILayout.Label(ping == 0 ? "--- ms" : ping + " ms", EditorStyles.miniLabel);

                GUI.color = Color.white;

                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(p.IpAddress))
                    GUILayout.Label(p.IpAddress, EditorStyles.miniLabel);

                if (host && p.PeerId != TeamCreateSession.LocalPeerId)
                {
                    GUILayout.BeginHorizontal();

                    GUI.backgroundColor = new Color(1f, 0.75f, 0.3f);

                    if (GUILayout.Button("Kick"))
                        TeamCreateSession.KickPeer(p.PeerId);

                    GUI.backgroundColor = new Color(1f, 0.3f, 0.3f);

                    if (GUILayout.Button("Ban"))
                        TeamCreateSession.BanPeer(p.PeerId, p.IpAddress);

                    GUI.backgroundColor = Color.white;

                    GUILayout.EndHorizontal();
                }

                GUILayout.EndVertical();
            }

            GUILayout.EndScrollView();
        }

        void DrawSettings()
        {
            GUILayout.Label("Settings", sectionTitle);

            GUILayout.BeginVertical(card);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Peer Highlights", GUILayout.Width(110));
            TeamCreateSelectionSync.HighlightsEnabled = EditorGUILayout.Toggle(TeamCreateSelectionSync.HighlightsEnabled);
            GUILayout.EndHorizontal();

            if (TeamCreateSelectionSync.HighlightsEnabled)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Opacity", GUILayout.Width(110));
                TeamCreateSelectionSync.HighlightOpacity =
                    EditorGUILayout.Slider(TeamCreateSelectionSync.HighlightOpacity, 0f, 1f);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Interpolation", GUILayout.Width(110));
            TeamCreateInterpolator.InterpolationEnabled =
                EditorGUILayout.Toggle(TeamCreateInterpolator.InterpolationEnabled);
            GUILayout.EndHorizontal();

            if (TeamCreateInterpolator.InterpolationEnabled)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Speed", GUILayout.Width(110));
                TeamCreateInterpolator.SmoothSpeed =
                    EditorGUILayout.Slider(TeamCreateInterpolator.SmoothSpeed, 1f, 50f);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Verbose Logging", GUILayout.Width(110));
            TeamCreateLogger.Enabled = EditorGUILayout.Toggle(TeamCreateLogger.Enabled);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        static void DrawSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f, 0.5f));
        }
    }
}
#endif