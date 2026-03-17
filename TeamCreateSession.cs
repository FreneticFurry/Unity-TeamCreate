#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TeamCreate.Editor
{
    public enum SessionState
    {
        Idle,
        Hosting,
        Connected,
        Connecting,
        Disconnected,
        Syncing,
        Reconnecting
    }

    public class PendingJoinRequest
    {
        public string PeerId;
        public string Username;
        public string IpAddress;
        internal Action<bool> Respond;
        public List<string> MissingPackages;
        public List<string> ExtraPackages;
        public List<string> VersionMismatches;
        public bool HasPackageDiff => (MissingPackages?.Count > 0) || (ExtraPackages?.Count > 0) || (VersionMismatches?.Count > 0);
    }

    [InitializeOnLoad]
    public static class TeamCreateSession
    {
        private const string SessKey_PeerId = "TeamCreate_PeerId";
        private const string SessKey_Role = "TeamCreate_ReconnRole";
        private const string SessKey_IP = "TeamCreate_ReconnIP";
        private const string SessKey_Port = "TeamCreate_ReconnPort";
        private const string SessKey_Username = "TeamCreate_ReconnUsername";
        private const string SessKey_KnownPeers = "TeamCreate_KnownPeers";
        private const string SessKey_BannedPeerIds = "TeamCreate_BannedPeerIds";
        private const string SessKey_BannedIps = "TeamCreate_BannedIps";
        private const string SessKey_SessionFolder = "TeamCreate_SessionFolder";
        private const string SessKey_PreviousScenes = "TeamCreate_PreviousScenes";

        public static string SessionFolderName { get; private set; }

        public static bool IsApplyingRemoteChange { get; set; } = false;
        public static bool IsPrivateMode { get; set; } = true;

        public static SessionState CurrentState { get; private set; } = SessionState.Idle;

        public static string LocalPeerId { get; private set; }
        public static string LocalUsername { get; set; }
        public static string SessionHostId { get; set; }

        public static string LastHostPort { get; private set; } = "7777";
        public static string LastClientIp { get; private set; } = "127.0.0.1";
        public static string LastClientPort { get; private set; } = "7777";

        public static TeamCreateHost Host { get; private set; }
        public static TeamCreateClient Client { get; private set; }

        public static event Action<string> OnStatusChanged;
        public static event Action<List<PeerInfo>> OnPeersChanged;
        public static event Action<PendingJoinRequest> OnJoinRequest;
        public static event Action OnSessionEnded;

        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();
        private static readonly List<PeerInfo> ConnectedPeers = new List<PeerInfo>();
        private static readonly Dictionary<string, long> PeerPingMs = new Dictionary<string, long>();
        private static readonly HashSet<string> BannedPeerIds = new HashSet<string>();
        private static readonly HashSet<string> BannedIps = new HashSet<string>();
        private static readonly HashSet<string> KnownSessionPeerIds = new HashSet<string>();

        private static double _lastPingSendTime;
        private const double PingIntervalSeconds = 2.0;

        private static double _lastHeartbeatCheckTime;
        private const double HeartbeatCheckIntervalSeconds = 15.0;

        private static readonly int[] ReconnectDelaysSeconds = { 3, 5, 7, 9, 10 };
        private static int _reconnectAttempt;
        private static double _nextReconnectTime;
        private static string _reconnectIp;
        private static int _reconnectPort;

        public static int ReconnectAttempt => _reconnectAttempt;
        public static int TotalReconnectAttempts => ReconnectDelaysSeconds.Length;
        public static double NextReconnectTime => _nextReconnectTime;

        static TeamCreateSession()
        {
            string savedId = UnityEditor.SessionState.GetString(SessKey_PeerId, "");
            if (string.IsNullOrEmpty(savedId))
            {
                savedId = Guid.NewGuid().ToString("N");
                UnityEditor.SessionState.SetString(SessKey_PeerId, savedId);
                Debug.Log($"[TeamCreate] Generated new per-instance peer ID: {savedId}");
            }
            else
            {
                Debug.Log($"[TeamCreate] Loaded existing per-instance peer ID: {savedId}");
            }
            LocalPeerId = savedId;

            LocalUsername = System.Environment.MachineName;

            SessionFolderName = UnityEditor.SessionState.GetString(SessKey_SessionFolder, "");

            LoadKnownPeers();
            LoadBans();

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.update += DrainMainThreadQueue;
            EditorApplication.update += PingLoop;
            EditorApplication.update += HeartbeatTimeoutCheck;
            EditorApplication.update += ReconnectUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            TryAutoReconnect();
        }

        private static void LoadKnownPeers()
        {
            KnownSessionPeerIds.Clear();
            string json = UnityEditor.SessionState.GetString(SessKey_KnownPeers, "");
            if (string.IsNullOrEmpty(json)) return;
            var list = TeamCreateJson.Deserialize<List<string>>(json);
            if (list == null) return;
            foreach (var id in list)
                KnownSessionPeerIds.Add(id);
        }

        private static void SaveKnownPeers()
        {
            var list = new List<string>(KnownSessionPeerIds);
            UnityEditor.SessionState.SetString(SessKey_KnownPeers, TeamCreateJson.Serialize(list));
        }

        private static void LoadBans()
        {
            BannedPeerIds.Clear();
            BannedIps.Clear();
            string idsJson = UnityEditor.SessionState.GetString(SessKey_BannedPeerIds, "");
            if (!string.IsNullOrEmpty(idsJson))
            {
                var list = TeamCreateJson.Deserialize<List<string>>(idsJson);
                if (list != null) foreach (var id in list) BannedPeerIds.Add(id);
            }
            string ipsJson = UnityEditor.SessionState.GetString(SessKey_BannedIps, "");
            if (!string.IsNullOrEmpty(ipsJson))
            {
                var list = TeamCreateJson.Deserialize<List<string>>(ipsJson);
                if (list != null) foreach (var ip in list) BannedIps.Add(ip);
            }
        }

        private static void SaveBans()
        {
            UnityEditor.SessionState.SetString(SessKey_BannedPeerIds, TeamCreateJson.Serialize(new List<string>(BannedPeerIds)));
            UnityEditor.SessionState.SetString(SessKey_BannedIps, TeamCreateJson.Serialize(new List<string>(BannedIps)));
        }

        private static void ClearBans()
        {
            BannedPeerIds.Clear();
            BannedIps.Clear();
            UnityEditor.SessionState.EraseString(SessKey_BannedPeerIds);
            UnityEditor.SessionState.EraseString(SessKey_BannedIps);
        }

        public static bool IsKnownSessionPeer(string peerId) => KnownSessionPeerIds.Contains(peerId);

        private static void TryAutoReconnect()
        {
            string role = UnityEditor.SessionState.GetString(SessKey_Role, "");
            if (string.IsNullOrEmpty(role)) return;

            string username = UnityEditor.SessionState.GetString(SessKey_Username, "");
            string ip = UnityEditor.SessionState.GetString(SessKey_IP, "");
            string port = UnityEditor.SessionState.GetString(SessKey_Port, "");

            UnityEditor.SessionState.EraseString(SessKey_Role);
            UnityEditor.SessionState.EraseString(SessKey_Username);
            UnityEditor.SessionState.EraseString(SessKey_IP);
            UnityEditor.SessionState.EraseString(SessKey_Port);

            if (!string.IsNullOrEmpty(username))
                LocalUsername = username;

            if (role == "host" && !string.IsNullOrEmpty(port) && int.TryParse(port, out int hostPort))
            {
                LastHostPort = port;
                EditorApplication.delayCall += () => StartHosting(hostPort);
            }
            else if (role == "client" && !string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(port) && int.TryParse(port, out int clientPort))
            {
                LastClientIp = ip;
                LastClientPort = port;
                EditorApplication.delayCall += () => StartConnecting(ip, clientPort);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode && IsConnected)
            {
                Debug.LogWarning("[TeamCreate] Disconnecting: entering Play Mode is not supported while connected.");
                Disconnect();
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (IsConnected || CurrentState == SessionState.Hosting || CurrentState == SessionState.Reconnecting)
            {
                UnityEditor.SessionState.SetString(SessKey_Username, LocalUsername);

                if (CurrentState == SessionState.Reconnecting)
                {
                    UnityEditor.SessionState.SetString(SessKey_Role, "client");
                    UnityEditor.SessionState.SetString(SessKey_IP, _reconnectIp);
                    UnityEditor.SessionState.SetString(SessKey_Port, _reconnectPort.ToString());
                }
                else if (IsHosting)
                {
                    UnityEditor.SessionState.SetString(SessKey_Role, "host");
                    UnityEditor.SessionState.SetString(SessKey_Port, LastHostPort);
                }
                else
                {
                    UnityEditor.SessionState.SetString(SessKey_Role, "client");
                    UnityEditor.SessionState.SetString(SessKey_IP, LastClientIp);
                    UnityEditor.SessionState.SetString(SessKey_Port, LastClientPort);
                }

                SaveKnownPeers();
                if (IsHosting) SaveBans();
                if (!string.IsNullOrEmpty(SessionFolderName))
                    UnityEditor.SessionState.SetString(SessKey_SessionFolder, SessionFolderName);
            }

            Cleanup();
        }

        private static void OnAfterAssemblyReload() { }

        private static void Cleanup()
        {
            try { Host?.Stop(isReloading: true); } catch { }
            try { Client?.Disconnect(); } catch { }
            Host = null;
            Client = null;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            EditorApplication.update -= DrainMainThreadQueue;
            EditorApplication.update -= PingLoop;
            EditorApplication.update -= HeartbeatTimeoutCheck;
            EditorApplication.update -= ReconnectUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private static void DrainMainThreadQueue()
        {
            int limit = 50;
            while (limit-- > 0 && MainThreadQueue.TryDequeue(out var action))
                try { action(); }
                catch (Exception e) { Debug.LogError($"[TeamCreate] Main-thread action threw: {e}"); }
        }

        private static void PingLoop()
        {
            if (!IsConnected) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastPingSendTime < PingIntervalSeconds) return;
            _lastPingSendTime = now;
            SendMessage(Protocol.CreateMessage(MessageType.PING, LocalPeerId, null));
        }

        private static void HeartbeatTimeoutCheck()
        {
            if (!IsHosting || Host == null) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastHeartbeatCheckTime < HeartbeatCheckIntervalSeconds) return;
            _lastHeartbeatCheckTime = now;
            Host.CheckHeartbeats();
        }

        public static void EnqueueMainThread(Action action) => MainThreadQueue.Enqueue(action);

        public static void StartHosting(int port)
        {
            if (CurrentState != SessionState.Idle && CurrentState != SessionState.Disconnected) return;
            LastHostPort = port.ToString();
            ConnectedPeers.Clear();
            PeerPingMs.Clear();
            Host = new TeamCreateHost();
            Host.StartListening(port);
            SetState(SessionState.Hosting);
        }

        public static void StartConnecting(string ip, int port)
        {
            if (CurrentState != SessionState.Idle && CurrentState != SessionState.Disconnected) return;
            LastClientIp = ip;
            LastClientPort = port.ToString();
            ConnectedPeers.Clear();
            PeerPingMs.Clear();
            SetState(SessionState.Connecting);
            Client = new TeamCreateClient();
            Client.Connect(ip, port);
        }

        public static void Disconnect()
        {
            SessionHostId = string.Empty;
            UnityEditor.SessionState.EraseString(SessKey_Role);
            KnownSessionPeerIds.Clear();
            UnityEditor.SessionState.EraseString(SessKey_KnownPeers);
            ClearBans();

            try { Host?.Stop(); } catch { }
            try { Client?.Disconnect(); } catch { }
            Host = null;
            Client = null;
            ConnectedPeers.Clear();
            PeerPingMs.Clear();
            OnPeersChanged?.Invoke(new List<PeerInfo>());
            TeamCreateIdentity.ClearSession();
            TeamCreateConflictResolver.Clear();
            TeamCreateSnapshotBuilder.ClearPendingRefs();
            ClearJoinPackageDiff();
            SetState(SessionState.Idle);
            OnSessionEnded?.Invoke();
            RestorePreviousScene();
            CleanupSessionFolder();
        }

        public static void SetState(SessionState state)
        {
            CurrentState = state;
            OnStatusChanged?.Invoke(state.ToString());
        }

        public static void EndSession() => OnSessionEnded?.Invoke();

        public static void AddPeer(PeerInfo peer)
        {
            ConnectedPeers.RemoveAll(p => p.PeerId == peer.PeerId);
            ConnectedPeers.Add(peer);
            KnownSessionPeerIds.Add(peer.PeerId);
            OnPeersChanged?.Invoke(new List<PeerInfo>(ConnectedPeers));
        }

        public static void RemovePeer(string peerId)
        {
            ConnectedPeers.RemoveAll(p => p.PeerId == peerId);
            PeerPingMs.Remove(peerId);
            TeamCreateHierarchySync.OnPeerDisconnected(peerId);
            OnPeersChanged?.Invoke(new List<PeerInfo>(ConnectedPeers));
        }

        public static List<PeerInfo> GetPeers() => new List<PeerInfo>(ConnectedPeers);

        public static void UpdatePeerPing(string peerId, long pingMs)
        {
            PeerPingMs[peerId] = pingMs;
        }

        public static long GetPeerPing(string peerId)
        {
            PeerPingMs.TryGetValue(peerId, out long ms);
            return ms;
        }

        public static void SendMessage(NetworkMessage message)
        {
            if (CurrentState == SessionState.Hosting && Host != null)
                Host.BroadcastMessage(message, excludePeerId: null);
            else if ((CurrentState == SessionState.Connected || CurrentState == SessionState.Syncing) && Client != null)
                Client.SendMessage(message);
        }

        public static void SendMessageExcluding(NetworkMessage message, string excludePeerId)
        {
            if (CurrentState == SessionState.Hosting && Host != null)
                Host.BroadcastMessage(message, excludePeerId);
        }

        public static async void KickPeer(string peerId, string reason = "Removed by host")
        {
            if (CurrentState != SessionState.Hosting || Host == null) return;
            var payload = Protocol.Serialize(new KickPeerPayload { TargetPeerId = peerId, Reason = reason });
            Host.SendToPeer(peerId, Protocol.CreateMessage(MessageType.KICK_PEER, LocalPeerId, payload));
            await Task.Delay(2000);
            Host.DisconnectPeer(peerId);
            RemovePeer(peerId);
        }

        public static void BanPeer(string peerId, string ipAddress)
        {
            BannedPeerIds.Add(peerId);
            if (!string.IsNullOrEmpty(ipAddress)) BannedIps.Add(ipAddress);
            SaveBans();
            KickPeer(peerId, "Banned by host");
        }

        public static bool IsBanned(string peerId, string ipAddress)
        {
            if (!string.IsNullOrEmpty(peerId) && BannedPeerIds.Contains(peerId)) return true;
            if (!string.IsNullOrEmpty(ipAddress) && BannedIps.Contains(ipAddress)) return true;
            return false;
        }

        public static void RaiseJoinRequest(PendingJoinRequest req) => OnJoinRequest?.Invoke(req);

        public static void NotifySessionEnded() => OnSessionEnded?.Invoke();

        public static bool IsHosting => CurrentState == SessionState.Hosting;
        public static bool IsConnected => CurrentState == SessionState.Connected || CurrentState == SessionState.Hosting || CurrentState == SessionState.Syncing;

        public static List<string> JoinMissingPackages { get; private set; } = new List<string>();
        public static List<string> JoinExtraPackages { get; private set; } = new List<string>();
        public static List<string> JoinVersionMismatches { get; private set; } = new List<string>();
        public static bool HasJoinPackageDiff => JoinMissingPackages.Count > 0 || JoinExtraPackages.Count > 0 || JoinVersionMismatches.Count > 0;

        public static void SetJoinPackageDiff(List<string> missing, List<string> extra, List<string> versionMismatches)
        {
            JoinMissingPackages = missing ?? new List<string>();
            JoinExtraPackages = extra ?? new List<string>();
            JoinVersionMismatches = versionMismatches ?? new List<string>();
        }

        public static void ClearJoinPackageDiff()
        {
            JoinMissingPackages = new List<string>();
            JoinExtraPackages = new List<string>();
            JoinVersionMismatches = new List<string>();
        }

        public static List<PackageEntry> GetInstalledPackages()
        {
            try
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
                if (!File.Exists(manifestPath)) return new List<PackageEntry>();
                string json = File.ReadAllText(manifestPath);
                var result = new List<PackageEntry>();
                int depsIdx = json.IndexOf("\"dependencies\"", StringComparison.Ordinal);
                if (depsIdx < 0) return result;
                int braceStart = json.IndexOf('{', depsIdx);
                if (braceStart < 0) return result;
                int depth = 0, braceEnd = -1;
                for (int k = braceStart; k < json.Length; k++)
                {
                    if (json[k] == '{') depth++;
                    else if (json[k] == '}') { depth--; if (depth == 0) { braceEnd = k; break; } }
                }
                if (braceEnd < 0) return result;
                string section = json.Substring(braceStart, braceEnd - braceStart + 1);
                var matches = System.Text.RegularExpressions.Regex.Matches(section, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\"");
                foreach (System.Text.RegularExpressions.Match m in matches)
                    result.Add(new PackageEntry { Name = m.Groups[1].Value, Version = m.Groups[2].Value });
                return result;
            }
            catch { return new List<PackageEntry>(); }
        }

        public static (List<string> missingFromPeer, List<string> extraInPeer, List<string> versionMismatches)
            ComputePackageDiff(List<PackageEntry> hostPkgs, List<PackageEntry> peerPkgs)
        {
            var missingFromPeer = new List<string>();
            var extraInPeer = new List<string>();
            var versionMismatches = new List<string>();

            var peerDict = new Dictionary<string, string>();
            if (peerPkgs != null) foreach (var p in peerPkgs) peerDict[p.Name] = p.Version;
            var hostDict = new Dictionary<string, string>();
            if (hostPkgs != null) foreach (var h in hostPkgs) hostDict[h.Name] = h.Version;

            foreach (var kvp in hostDict)
            {
                if (!peerDict.TryGetValue(kvp.Key, out string peerVer))
                    missingFromPeer.Add($"{kvp.Key} ({kvp.Value})");
                else if (peerVer != kvp.Value)
                    versionMismatches.Add($"{kvp.Key}  host: {kvp.Value}  peer: {peerVer}");
            }
            foreach (var kvp in peerDict)
                if (!hostDict.ContainsKey(kvp.Key))
                    extraInPeer.Add($"{kvp.Key} ({kvp.Value})");

            return (missingFromPeer, extraInPeer, versionMismatches);
        }

        public static string NetworkToLocalPath(string networkPath)
        {
            if (string.IsNullOrEmpty(SessionFolderName)) return networkPath;
            if (string.IsNullOrEmpty(networkPath)) return networkPath;
            return "Assets/" + SessionFolderName + "/" + networkPath.Substring("Assets/".Length);
        }

        public static string LocalToNetworkPath(string localPath)
        {
            if (string.IsNullOrEmpty(localPath)) return null;
            if (string.IsNullOrEmpty(SessionFolderName))
                return localPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? localPath : null;
            string prefix = "Assets/" + SessionFolderName + "/";
            if (!localPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets/" + localPath.Substring(prefix.Length);
        }

        public static bool IsInSessionScope(string localPath)
        {
            if (string.IsNullOrEmpty(localPath)) return false;
            if (string.IsNullOrEmpty(SessionFolderName))
                return localPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
            string prefix = "Assets/" + SessionFolderName + "/";
            return localPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        public static void SetupSessionFolder(string hostUsername)
        {
            bool isFirstSetup = string.IsNullOrEmpty(SessionFolderName);
            string sanitized = SanitizeFolderName(hostUsername);
            SessionFolderName = sanitized + "Session";
            UnityEditor.SessionState.SetString(SessKey_SessionFolder, SessionFolderName);

            string sessionAssetPath = "Assets/" + SessionFolderName;
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string absPath = Path.Combine(projectRoot, sessionAssetPath);
            if (!Directory.Exists(absPath))
                Directory.CreateDirectory(absPath);

            if (isFirstSetup)
                SaveScenesToPreviousScene();

            AssetDatabase.Refresh();
        }

        public static void CleanupSessionFolder()
        {
            if (string.IsNullOrEmpty(SessionFolderName)) return;
            string sessionAssetPath = "Assets/" + SessionFolderName;
            if (AssetDatabase.IsValidFolder(sessionAssetPath))
                AssetDatabase.DeleteAsset(sessionAssetPath);
            SessionFolderName = "";
            UnityEditor.SessionState.EraseString(SessKey_SessionFolder);
        }

        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Host";
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            return sb.Length > 0 ? sb.ToString() : "Host";
        }

        private static void SaveScenesToPreviousScene()
        {
            string prevAssetPath = "Assets/PreviousScene";
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string absPath = Path.Combine(projectRoot, prevAssetPath);
            if (!Directory.Exists(absPath))
                Directory.CreateDirectory(absPath);
            AssetDatabase.Refresh();

            var scenePaths = new List<string>();
            int count = EditorSceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) continue;
                scenePaths.Add(scene.path);
                string destPath = prevAssetPath + "/" + Path.GetFileName(scene.path);
                EditorSceneManager.SaveScene(scene, destPath, true);
            }
            UnityEditor.SessionState.SetString(SessKey_PreviousScenes, TeamCreateJson.Serialize(scenePaths));
            AssetDatabase.Refresh();
        }

        public static void BeginReconnect()
        {
            if (CurrentState == SessionState.Reconnecting || CurrentState == SessionState.Idle ||
                CurrentState == SessionState.Hosting || CurrentState == SessionState.Disconnected) return;

            _reconnectIp = LastClientIp;
            if (!int.TryParse(LastClientPort, out _reconnectPort)) _reconnectPort = 7777;
            _reconnectAttempt = 0;

            try { Client?.Disconnect(); } catch { }
            Client = null;
            ConnectedPeers.Clear();
            PeerPingMs.Clear();
            OnPeersChanged?.Invoke(new List<PeerInfo>());
            TeamCreateConflictResolver.Clear();
            TeamCreateInterpolator.Clear();

            _nextReconnectTime = EditorApplication.timeSinceStartup + ReconnectDelaysSeconds[0];
            SetState(SessionState.Reconnecting);
            Debug.Log($"[TeamCreate] Connection lost. Reconnecting in {ReconnectDelaysSeconds[0]}s (attempt 1/{ReconnectDelaysSeconds.Length})...");
        }

        public static void ScheduleNextReconnectAttempt(string reason = null)
        {
            if (CurrentState != SessionState.Reconnecting) return;

            if (_reconnectAttempt >= ReconnectDelaysSeconds.Length)
            {
                Debug.LogWarning("[TeamCreate] All reconnect attempts exhausted. Disconnecting.");
                CancelReconnect();
                return;
            }

            int delay = ReconnectDelaysSeconds[_reconnectAttempt];
            _nextReconnectTime = EditorApplication.timeSinceStartup + delay;
            string msg = string.IsNullOrEmpty(reason) ? "" : $" ({reason})";
            Debug.Log($"[TeamCreate] Reconnect attempt {_reconnectAttempt}/{ReconnectDelaysSeconds.Length} failed{msg}. Retrying in {delay}s...");
        }

        public static void CancelReconnect()
        {
            _reconnectAttempt = 0;
            _nextReconnectTime = 0;
            Disconnect();
        }

        private static void ReconnectUpdate()
        {
            if (CurrentState != SessionState.Reconnecting) return;
            if (EditorApplication.timeSinceStartup < _nextReconnectTime) return;

            _reconnectAttempt++;
            Debug.Log($"[TeamCreate] Reconnect attempt {_reconnectAttempt}/{ReconnectDelaysSeconds.Length} to {_reconnectIp}:{_reconnectPort}...");

            Client = new TeamCreateClient();
            Client.Connect(_reconnectIp, _reconnectPort);
        }

        private static void RestorePreviousScene()
        {
            if (string.IsNullOrEmpty(SessionFolderName)) return;

            string prevSceneJson = UnityEditor.SessionState.GetString(SessKey_PreviousScenes, "");
            UnityEditor.SessionState.EraseString(SessKey_PreviousScenes);

            IsApplyingRemoteChange = true;
            try
            {
                var scenePaths = string.IsNullOrEmpty(prevSceneJson) ? null : TeamCreateJson.Deserialize<List<string>>(prevSceneJson);
                bool opened = false;
                if (scenePaths != null && scenePaths.Count > 0)
                {
                    string projectRoot = Path.GetDirectoryName(Application.dataPath);
                    foreach (var path in scenePaths)
                    {
                        if (string.IsNullOrEmpty(path)) continue;
                        if (!File.Exists(Path.Combine(projectRoot, path))) continue;
                        var mode = !opened ? UnityEditor.SceneManagement.OpenSceneMode.Single : UnityEditor.SceneManagement.OpenSceneMode.Additive;
                        EditorSceneManager.OpenScene(path, mode);
                        opened = true;
                    }
                }
                if (!opened)
                    EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            }
            catch (Exception e) { Debug.LogError($"[TeamCreate] Failed to restore previous scene: {e.Message}"); }
            finally { IsApplyingRemoteChange = false; }

            string prevAssetPath = "Assets/PreviousScene";
            if (AssetDatabase.IsValidFolder(prevAssetPath))
                AssetDatabase.DeleteAsset(prevAssetPath);
        }
    }
}
#endif
