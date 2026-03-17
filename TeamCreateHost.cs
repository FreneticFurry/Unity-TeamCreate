#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace TeamCreate.Editor
{
    public class TeamCreateHost
    {
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running = false;
        private readonly ConcurrentDictionary<string, PeerConnection> _peers = new ConcurrentDictionary<string, PeerConnection>();

        private const long HeartbeatTimeoutTicks = 2L * 60L * TimeSpan.TicksPerSecond;

        public void StartListening(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "TC-Accept" };
            _acceptThread.Start();
            Debug.Log($"[TeamCreate] Host listening on port {port}.");
        }

        public void Stop(bool isReloading = false)
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            var leftPayload = Protocol.Serialize(new PeerEventPayload
            {
                PeerId = TeamCreateSession.LocalPeerId,
                Username = TeamCreateSession.LocalUsername,
                IsReloading = isReloading
            });
            var sessionEndMsg = Protocol.CreateMessage(MessageType.PEER_LEFT, TeamCreateSession.LocalPeerId, leftPayload);
            foreach (var peer in _peers.Values)
            {
                try { Protocol.WriteMessage(peer.Stream, sessionEndMsg); } catch { }
                try { peer.Close(); } catch { }
            }
            _peers.Clear();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var tcpClient = _listener.AcceptTcpClient();
                    tcpClient.NoDelay = true;
                    var conn = new PeerConnection(tcpClient, null, null);
                    var t = new Thread(() => PerformHandshake(conn)) { IsBackground = true, Name = "TC-Handshake" };
                    t.Start();
                }
                catch (SocketException) when (!_running) { break; }
                catch (Exception e) { if (_running) Debug.LogError($"[TeamCreate] Accept error: {e.Message}"); }
            }
        }

        private void PerformHandshake(PeerConnection conn)
        {
            try
            {
                string remoteIp = conn.RemoteIp;

                var msg = Protocol.ReadMessage(conn.Stream);
                if (msg.Type != MessageType.HANDSHAKE_REQUEST) { conn.Close(); return; }

                var req = Protocol.Deserialize<HandshakeRequestPayload>(msg.Payload);
                if (req == null) { conn.Close(); return; }

                if (req.ProtocolVersion != Protocol.ProtocolVersion)
                {
                    SendReject(conn, "Incompatible protocol version.");
                    Debug.LogWarning($"[TeamCreate] Rejected peer with protocol {req.ProtocolVersion}");
                    return;
                }

                if (TeamCreateSession.IsBanned(req.PeerId, remoteIp))
                {
                    SendReject(conn, "You are banned from this session.");
                    TeamCreateLogger.Log($"Rejected banned peer {req.Username} ({remoteIp})");
                    return;
                }


                if (_peers.ContainsKey(req.PeerId))
                {
                    SendReject(conn, "A peer with this ID is already connected. :thinking: thats odd");
                    TeamCreateLogger.Log($"Rejected duplicate PeerId {req.PeerId} from {remoteIp}");
                    return;
                }

                conn.PeerId = req.PeerId;
                conn.Username = req.Username;

                if (TeamCreateSession.IsPrivateMode && !TeamCreateSession.IsKnownSessionPeer(req.PeerId))
                {
                    using var mre = new ManualResetEventSlim(false);
                    bool approved = false;
                    var hostPkgs = TeamCreateSession.GetInstalledPackages();
                    var (missingFromPeer, extraInPeer, versionMismatches) = TeamCreateSession.ComputePackageDiff(hostPkgs, req.Packages);
                    var pending = new PendingJoinRequest
                    {
                        PeerId = req.PeerId,
                        Username = req.Username,
                        IpAddress = remoteIp,
                        Respond = result => { approved = result; mre.Set(); },
                        MissingPackages = missingFromPeer,
                        ExtraPackages = extraInPeer,
                        VersionMismatches = versionMismatches
                    };

                    TeamCreateSession.EnqueueMainThread(() => TeamCreateSession.RaiseJoinRequest(pending));

                    bool timedOut = !mre.Wait(TimeSpan.FromSeconds(60));
                    if (timedOut || !approved)
                    {
                        SendReject(conn, timedOut ? "Request timed out." : "Denied by host.");
                        return;
                    }
                }

                var existingPeers = new List<PeerInfo>();
                foreach (var p in _peers.Values)
                    existingPeers.Add(new PeerInfo { PeerId = p.PeerId, Username = p.Username, IpAddress = p.RemoteIp });
                existingPeers.Add(new PeerInfo
                {
                    PeerId = TeamCreateSession.LocalPeerId,
                    Username = TeamCreateSession.LocalUsername,
                    IpAddress = "localhost"
                });

                var accept = new HandshakeAcceptPayload
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    HostId = TeamCreateSession.LocalPeerId,
                    HostUsername = TeamCreateSession.LocalUsername,
                    ConnectedPeers = existingPeers,
                    Packages = TeamCreateSession.GetInstalledPackages()
                };
                Protocol.WriteMessage(conn.Stream, Protocol.CreateMessage(MessageType.HANDSHAKE_ACCEPT, TeamCreateSession.LocalPeerId, Protocol.Serialize(accept)));

                _peers[conn.PeerId] = conn;

                var joinPayload = Protocol.Serialize(new PeerEventPayload { PeerId = conn.PeerId, Username = conn.Username });
                BroadcastMessage(Protocol.CreateMessage(MessageType.PEER_JOINED, TeamCreateSession.LocalPeerId, joinPayload), conn.PeerId);

                TeamCreateLogger.Log($"Peer joined: {conn.Username} ({conn.PeerId}) from {remoteIp}");

                TeamCreateSession.EnqueueMainThread(() =>
                    TeamCreateSession.AddPeer(new PeerInfo { PeerId = conn.PeerId, Username = conn.Username, IpAddress = remoteIp }));

                conn.StartReceiving(OnMessageReceived, OnPeerDisconnected);
            }
            catch (Exception e)
            {
                Debug.LogError($"[TeamCreate] Handshake failed: {e.Message}");
                conn.Close();
            }
        }

        private static void SendReject(PeerConnection conn, string reason)
        {
            try
            {
                var payload = Protocol.Serialize(new HandshakeRejectedPayload { Reason = reason });
                Protocol.WriteMessage(conn.Stream, Protocol.CreateMessage(MessageType.HANDSHAKE_REJECTED, TeamCreateSession.LocalPeerId, payload));
            }
            catch { }
            finally { conn.Close(); }
        }

        private void OnMessageReceived(PeerConnection peer, NetworkMessage message)
        {

            message.SenderId = peer.PeerId;

            TeamCreateLogger.LogRecv(message.Type.ToString(), peer.PeerId);

            if (message.Type == MessageType.FULL_STATE_REQUEST)
            {
                TeamCreateSession.EnqueueMainThread(() => TeamCreateSnapshotBuilder.SendFullState(peer));
                return;
            }
            if (message.Type == MessageType.FULL_STATE_ASSET_REQUEST)
            {
                TeamCreateSession.EnqueueMainThread(() => TeamCreateSnapshotBuilder.SendRequestedAssets(peer, message));
                return;
            }
            if (message.Type == MessageType.ASSET_CHUNK_ACK)
            {
                TeamCreateSession.EnqueueMainThread(() => TeamCreateAssetSync.HandleDeliveryAck(message));
                return;
            }
            BroadcastMessage(message, peer.PeerId);
            TeamCreateSession.EnqueueMainThread(() => TeamCreateMessageDispatcher.Dispatch(message));
        }

        private void OnPeerDisconnected(PeerConnection peer)
        {
            _peers.TryRemove(peer.PeerId, out _);
            var leftPayload = Protocol.Serialize(new PeerEventPayload { PeerId = peer.PeerId, Username = peer.Username });
            BroadcastMessage(Protocol.CreateMessage(MessageType.PEER_LEFT, TeamCreateSession.LocalPeerId, leftPayload), peer.PeerId);
            TeamCreateSession.EnqueueMainThread(() => TeamCreateSession.RemovePeer(peer.PeerId));
            Debug.Log($"[TeamCreate] Peer disconnected: {peer.Username} ({peer.PeerId})");
        }

        public void CheckHeartbeats()
        {
            long now = DateTime.UtcNow.Ticks;
            var timedOut = new List<string>();
            foreach (var kvp in _peers)
            {
                if (now - kvp.Value.LastMessageTime > HeartbeatTimeoutTicks)
                    timedOut.Add(kvp.Key);
            }
            foreach (var id in timedOut)
            {
                TeamCreateLogger.Log($"Peer {id} timed out (no message for 2 minutes), kicking.");
                TeamCreateSession.EnqueueMainThread(() => TeamCreateSession.KickPeer(id, "Connection timed out."));
            }
        }

        public void BroadcastMessage(NetworkMessage message, string excludePeerId)
        {
            var stale = new List<string>();
            foreach (var kvp in _peers)
            {
                if (kvp.Key == excludePeerId) continue;
                try
                {
                    kvp.Value.SendMessage(message);
                }
                catch (ObjectDisposedException)
                {
                    stale.Add(kvp.Key);
                }
                catch (Exception e)
                {
                    TeamCreateLogger.Log($"Broadcast error to {kvp.Key}: {e.Message}, will remove.");
                    stale.Add(kvp.Key);
                }
            }
            foreach (var id in stale)
            {
                if (_peers.TryRemove(id, out var dead))
                {
                    try { dead.Close(); } catch { }
                    var leftPayload = Protocol.Serialize(new PeerEventPayload { PeerId = id, Username = dead.Username });
                    BroadcastMessage(Protocol.CreateMessage(MessageType.PEER_LEFT, TeamCreateSession.LocalPeerId, leftPayload), id);
                    TeamCreateSession.EnqueueMainThread(() => TeamCreateSession.RemovePeer(id));
                    Debug.Log($"[TeamCreate] Removed stale peer {dead.Username} ({id}) during broadcast.");
                }
            }
        }

        public void SendToPeer(string peerId, NetworkMessage message)
        {
            if (_peers.TryGetValue(peerId, out var conn))
            {
                try
                {
                    conn.SendMessage(message);
                }
                catch (ObjectDisposedException)
                {
                    if (_peers.TryRemove(peerId, out var dead))
                    {
                        try { dead.Close(); } catch { }
                        TeamCreateSession.EnqueueMainThread(() => TeamCreateSession.RemovePeer(peerId));
                        Debug.Log($"[TeamCreate] Removed stale peer {dead.Username} ({peerId}) during direct send.");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[TeamCreate] Send error to {peerId}: {e.Message}");
                }
            }
        }

        public void DisconnectPeer(string peerId)
        {
            if (_peers.TryRemove(peerId, out var conn))
                try { conn.Close(); } catch { }
        }
    }

    public class PeerConnection
    {
        public string PeerId;
        public string Username;
        public long LastMessageTime = DateTime.UtcNow.Ticks;

        private readonly TcpClient _client;
        public NetworkStream Stream => _client.GetStream();
        public string RemoteIp => (_client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
        private Thread _receiveThread;
        private Thread _sendThread;
        private Action<PeerConnection, NetworkMessage> _onMessage;
        private Action<PeerConnection> _onDisconnect;

        private readonly ConcurrentQueue<NetworkMessage> _sendQueue = new ConcurrentQueue<NetworkMessage>();
        private readonly ManualResetEventSlim _sendSignal = new ManualResetEventSlim(false);
        private volatile bool _running;

        public PeerConnection(TcpClient client, string peerId, string username)
        {
            _client = client;
            PeerId = peerId;
            Username = username;
        }

        public void StartReceiving(Action<PeerConnection, NetworkMessage> onMessage, Action<PeerConnection> onDisconnect)
        {
            _onMessage = onMessage;
            _onDisconnect = onDisconnect;
            _running = true;

            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"TC-Recv-{PeerId}" };
            _receiveThread.Start();

            _sendThread = new Thread(SendLoop) { IsBackground = true, Name = $"TC-Send-{PeerId}" };
            _sendThread.Start();
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_running)
                {
                    var msg = Protocol.ReadMessage(Stream);
                    LastMessageTime = DateTime.UtcNow.Ticks;
                    _onMessage?.Invoke(this, msg);
                }
            }
            catch
            {
                _running = false;
                _sendSignal.Set();
                _onDisconnect?.Invoke(this);
            }
        }

        private void SendLoop()
        {
            try
            {
                while (_running)
                {
                    _sendSignal.Wait(100);
                    _sendSignal.Reset();

                    while (_sendQueue.TryDequeue(out var message))
                    {
                        if (!_running || !_client.Connected) break;
                        try
                        {
                            Protocol.WriteMessage(Stream, message);
                        }
                        catch (Exception e)
                        {
                            TeamCreateLogger.Log($"Send error in queue for {PeerId}: {e.Message}");
                            _running = false;
                            _onDisconnect?.Invoke(this);
                            return;
                        }
                    }
                }
            }
            catch
            {
                _running = false;
            }
        }

        public void SendMessage(NetworkMessage message)
        {
            if (!_running) throw new ObjectDisposedException(nameof(PeerConnection));

            if (_sendThread == null || !_sendThread.IsAlive)
            {
                if (!_client.Connected) throw new ObjectDisposedException(nameof(TcpClient));
                Protocol.WriteMessage(Stream, message);
                return;
            }

            _sendQueue.Enqueue(message);
            _sendSignal.Set();
        }

        public void Close()
        {
            _running = false;
            _sendSignal.Set();
            try { _client.Close(); } catch { }
        }
    }
}
#endif
