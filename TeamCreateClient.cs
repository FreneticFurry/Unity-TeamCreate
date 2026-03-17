#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace TeamCreate.Editor
{
    public class TeamCreateClient
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _receiveThread;
        private Thread _sendThread;
        private volatile bool _running = false;
        private volatile bool _suppressReconnect = false;

        private readonly ConcurrentQueue<NetworkMessage> _sendQueue = new ConcurrentQueue<NetworkMessage>();
        private readonly ManualResetEventSlim _sendSignal = new ManualResetEventSlim(false);

        public void Connect(string ip, int port)
        {
            _suppressReconnect = false;
            try
            {
                _client = new TcpClient();
                _client.NoDelay = true;
                _client.Connect(ip, port);
                _stream = _client.GetStream();
                _running = true;

                TeamCreateLogger.Log($"Connected to host at {ip}:{port}, sending handshake.");

                var req = new HandshakeRequestPayload
                {
                    PeerId = TeamCreateSession.LocalPeerId,
                    Username = TeamCreateSession.LocalUsername,
                    ProtocolVersion = Protocol.ProtocolVersion,
                    Packages = TeamCreateSession.GetInstalledPackages()
                };
                Protocol.WriteMessage(_stream, Protocol.CreateMessage(MessageType.HANDSHAKE_REQUEST,
                    TeamCreateSession.LocalPeerId, Protocol.Serialize(req)));

                TeamCreateLogger.LogSend("HANDSHAKE_REQUEST", $"{ip}:{port}");

                var response = Protocol.ReadMessage(_stream);

                if (response.Type == MessageType.HANDSHAKE_REJECTED)
                {
                    var reject = Protocol.Deserialize<HandshakeRejectedPayload>(response.Payload);
                    string reason = reject?.Reason ?? "Unknown reason";
                    _running = false;
                    TeamCreateSession.EnqueueMainThread(() =>
                    {
                        Debug.LogError($"[TeamCreate] Connection rejected: {reason}");
                        TeamCreateSession.SetState(Editor.SessionState.Disconnected);
                    });
                    return;
                }

                if (response.Type != MessageType.HANDSHAKE_ACCEPT)
                {
                    Disconnect();
                    TeamCreateSession.EnqueueMainThread(() =>
                    {
                        Debug.LogError("[TeamCreate] Unexpected response during handshake.");
                        TeamCreateSession.SetState(Editor.SessionState.Disconnected);
                    });
                    return;
                }

                TeamCreateLogger.LogRecv("HANDSHAKE_ACCEPT", "host");

                var accept = Protocol.Deserialize<HandshakeAcceptPayload>(response.Payload);
                var localPkgs = TeamCreateSession.GetInstalledPackages();
                var (missing, extra, versionMismatches) = TeamCreateSession.ComputePackageDiff(accept?.Packages, localPkgs);
                TeamCreateSession.EnqueueMainThread(() =>
                {
                    TeamCreateSession.SetJoinPackageDiff(missing, extra, versionMismatches);
                    TeamCreateSession.SessionHostId = accept?.HostId ?? string.Empty;
                    TeamCreateSession.SetupSessionFolder(accept?.HostUsername ?? "Host");
                    if (accept?.ConnectedPeers != null)
                        foreach (var peer in accept.ConnectedPeers)
                            TeamCreateSession.AddPeer(peer);
                    TeamCreateSession.SetState(Editor.SessionState.Syncing);
                    RequestFullState();
                });

                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "TC-ClientRecv" };
                _receiveThread.Start();

                _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "TC-ClientSend" };
                _sendThread.Start();
            }
            catch (Exception e)
            {
                TeamCreateSession.EnqueueMainThread(() =>
                {
                    if (TeamCreateSession.CurrentState == Editor.SessionState.Reconnecting)
                        TeamCreateSession.ScheduleNextReconnectAttempt(e.Message);
                    else
                    {
                        Debug.LogError($"[TeamCreate] Connection failed: {e.Message}");
                        TeamCreateSession.SetState(Editor.SessionState.Disconnected);
                    }
                });
            }
        }

        public void RequestFullState()
        {
            TeamCreateLogger.LogSend("FULL_STATE_REQUEST", "host");
            SendMessage(Protocol.CreateMessage(MessageType.FULL_STATE_REQUEST, TeamCreateSession.LocalPeerId, null));
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_running)
                {
                    var msg = Protocol.ReadMessage(_stream);
                    TeamCreateSession.EnqueueMainThread(() => HandleMessage(msg));
                }
            }
            catch (Exception e)
            {
                if (_running && !_suppressReconnect)
                {
                    TeamCreateSession.EnqueueMainThread(() =>
                    {
                        if (TeamCreateSession.CurrentState == Editor.SessionState.Reconnecting)
                            TeamCreateSession.ScheduleNextReconnectAttempt(e.Message);
                        else
                        {
                            Debug.LogError($"[TeamCreate] Connection to host lost: {e.Message}");
                            TeamCreateSession.BeginReconnect();
                        }
                    });
                }
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
                        if (!_running || _stream == null) break;
                        try
                        {
                            TeamCreateLogger.LogSend(message.Type.ToString(), "host");
                            Protocol.WriteMessage(_stream, message);
                        }
                        catch (Exception e)
                        {
                            if (_running)
                            {
                                TeamCreateSession.EnqueueMainThread(() =>
                                {
                                    Debug.LogError($"[TeamCreate] Send failed: {e.Message}");
                                    TeamCreateSession.Disconnect();
                                });
                            }
                            return;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void HandleMessage(NetworkMessage msg)
        {
            TeamCreateLogger.LogRecv(msg.Type.ToString(), msg.SenderId);

            switch (msg.Type)
            {
                case MessageType.PEER_JOINED:
                    {
                        var p = Protocol.Deserialize<PeerEventPayload>(msg.Payload);
                        if (p != null)
                        {
                            TeamCreateLogger.Log($"Peer joined: {p.Username} ({p.PeerId})");
                            TeamCreateSession.AddPeer(new PeerInfo { PeerId = p.PeerId, Username = p.Username });
                        }
                        break;
                    }
                case MessageType.HOST_RELOADING:
                    TeamCreateLogger.Log("Host is reloading scripts. Will attempt to reconnect.");
                    TeamCreateSession.BeginReconnect();
                    break;
                case MessageType.KICK_PEER:
                    {
                        var p = Protocol.Deserialize<KickPeerPayload>(msg.Payload);
                        if (p != null && p.TargetPeerId == TeamCreateSession.LocalPeerId)
                        {
                            Debug.LogWarning($"[TeamCreate] Kicked from session: {p.Reason}");
                            _suppressReconnect = true;
                            TeamCreateSession.Disconnect();
                        }
                        break;
                    }
                case MessageType.PEER_LEFT:
                    {
                        var p = Protocol.Deserialize<PeerEventPayload>(msg.Payload);
                        if (p != null)
                        {
                            if (p.PeerId == TeamCreateSession.SessionHostId)
                            {
                                if (p.IsReloading)
                                {
                                    TeamCreateLogger.Log("Host is reloading. Reconnecting...");
                                    TeamCreateSession.BeginReconnect();
                                }
                                else
                                {
                                    Debug.Log("[TeamCreate] Host ended the session.");
                                    _suppressReconnect = true;
                                    TeamCreateSession.Disconnect();
                                }
                            }
                            else
                            {
                                TeamCreateLogger.Log($"Peer left: {p.Username} ({p.PeerId})");
                                TeamCreateSession.RemovePeer(p.PeerId);
                            }
                        }
                        break;
                    }
                case MessageType.FULL_STATE_RESPONSE:
                    TeamCreateLogger.Log("Applying full state response from host.");
                    TeamCreateSnapshotBuilder.ApplyFullState(msg);
                    break;
                case MessageType.ASSET_CHUNK:
                    TeamCreateAssetSync.HandleChunkReceived(msg);
                    break;
                default:
                    TeamCreateMessageDispatcher.Dispatch(msg);
                    break;
            }
        }

        public void SendMessage(NetworkMessage message)
        {
            if (_stream == null || !_running) return;

            if (_sendThread == null || !_sendThread.IsAlive)
            {
                try
                {
                    TeamCreateLogger.LogSend(message.Type.ToString(), "host");
                    Protocol.WriteMessage(_stream, message);
                }
                catch (Exception e) { Debug.LogError($"[TeamCreate] Send failed: {e.Message}"); }
                return;
            }

            _sendQueue.Enqueue(message);
            _sendSignal.Set();
        }

        public void Disconnect()
        {
            _running = false;
            _sendSignal.Set();
            try { _client?.Close(); } catch { }
        }
    }
}
#endif
