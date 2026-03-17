#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public class TeamCreateAssetSync : AssetPostprocessor
    {
        private static readonly Dictionary<string, ChunkBuffer> PendingChunks = new Dictionary<string, ChunkBuffer>();
        private static readonly Dictionary<string, long> RecentBroadcasts = new Dictionary<string, long>();
        private const long DeduplicateWindowTicks = 3000 * TimeSpan.TicksPerMillisecond;

        private class ChunkBuffer
        {
            public string RelativePath;
            public string FileGuid;
            public MessageType OriginalType;
            public string OldRelativePath;
            public int TotalChunks;
            public byte[][] Chunks;
            public int ReceivedCount;
            public string TransferId;
            public string ContentHash;
            public string BundleId;
            public bool IsCompressed;
        }

        private class PendingDelivery
        {
            public string TransferId;
            public string RelativePath;
            public byte[] Content;
            public MessageType MsgType;
            public string ContentHash;
            public string BundleId;
            public HashSet<string> PendingPeerIds;
            public int RetryCount;
            public long NextRetryTicks;
        }

        private class ReceiverBundle
        {
            public string BundleId;
            public string AssetPath;
            public byte[] AssetContent;
            public string AssetTransferId;
            public string AssetContentHash;
            public MessageType AssetMsgType;
            public bool MetaWritten;
            public double FirstReceivedTime;
        }

        private static readonly Dictionary<string, PendingDelivery> PendingDeliveries = new Dictionary<string, PendingDelivery>();
        private static readonly Dictionary<string, ReceiverBundle> ReceiverBundles = new Dictionary<string, ReceiverBundle>();
        private const int MaxRetries = 5;
        private const long BaseRetryTicks = 8L * TimeSpan.TicksPerSecond;
        private const double BundleTimeoutSeconds = 3.0;
        private const double ReconcileIntervalSeconds = 60.0;

        private static double _lastReconcileTime;
        private static volatile bool _manifestBuildInProgress = false;
        private static readonly Dictionary<string, (long ModifiedTicks, string Hash)> _assetHashCache
            = new Dictionary<string, (long, string)>();

        static TeamCreateAssetSync()
        {
            EditorApplication.update += OnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        private static void OnBeforeReload()
        {
            EditorApplication.update -= OnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            _assetHashCache.Clear();
            _manifestBuildInProgress = false;
        }

        public static void ClearReceiveBundles()
        {
            ReceiverBundles.Clear();
        }

        private static void OnUpdate()
        {
            double editorNow = EditorApplication.timeSinceStartup;
            long tickNow = DateTime.UtcNow.Ticks;

            if (TeamCreateSession.IsHosting && PendingDeliveries.Count > 0)
            {
                var expired = new List<string>();
                foreach (var kvp in PendingDeliveries)
                {
                    var d = kvp.Value;
                    if (tickNow < d.NextRetryTicks) continue;
                    if (d.RetryCount >= MaxRetries || d.PendingPeerIds.Count == 0)
                    {
                        expired.Add(kvp.Key);
                        continue;
                    }
                    d.RetryCount++;
                    long backoff = BaseRetryTicks * (1L << Math.Min(d.RetryCount - 1, 4));
                    d.NextRetryTicks = tickNow + backoff;
                    TeamCreateLogger.Log($"[AssetDelivery] Retry {d.RetryCount}/{MaxRetries} for '{d.RelativePath}' to {d.PendingPeerIds.Count} peer(s)");
                    foreach (string peerId in new List<string>(d.PendingPeerIds))
                    {
                        string fileGuid = AssetDatabase.AssetPathToGUID(d.RelativePath);
                        if (d.Content.Length <= Protocol.ChunkSize)
                        {
                            var retryMsg = BuildFileMessage(d.RelativePath, fileGuid, d.Content, d.MsgType, d.TransferId, d.ContentHash, d.BundleId);
                            TeamCreateSession.Host?.SendToPeer(peerId, retryMsg);
                        }
                        else
                        {
                            SendChunkedToPeer(peerId, d.RelativePath, fileGuid, d.Content, d.MsgType, d.TransferId, d.ContentHash, d.BundleId);
                        }
                    }
                }
                foreach (string k in expired) PendingDeliveries.Remove(k);
            }

            if (ReceiverBundles.Count > 0)
            {
                var timedOut = new List<string>();
                foreach (var kvp in ReceiverBundles)
                {
                    var b = kvp.Value;
                    if (editorNow - b.FirstReceivedTime > BundleTimeoutSeconds && b.AssetContent != null)
                        timedOut.Add(kvp.Key);
                }
                foreach (string k in timedOut)
                {
                    var b = ReceiverBundles[k];
                    ReceiverBundles.Remove(k);
                    TeamCreateLogger.Log($"[AssetBundle] Timeout waiting for meta, applying asset anyway: {b.AssetPath}");
                    ImportAssetFile(b.AssetPath, b.AssetContent, b.AssetTransferId, b.AssetContentHash);
                }
            }

            if (TeamCreateSession.IsHosting && editorNow - _lastReconcileTime >= ReconcileIntervalSeconds && _lastReconcileTime > 0)
            {
                _lastReconcileTime = editorNow;
                BroadcastManifestSync();
            }

            if (TeamCreateSession.IsHosting && _lastReconcileTime == 0)
                _lastReconcileTime = editorNow;
        }

        static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (!TeamCreateSession.IsConnected) return;
            if (TeamCreateSession.IsApplyingRemoteChange) return;

            long now = DateTime.UtcNow.Ticks;

            var movedToPaths = new HashSet<string>();
            for (int i = 0; i < movedAssets.Length; i++)
            {
                if (!IsExcluded(movedAssets[i]) && TeamCreateSession.IsInSessionScope(movedAssets[i]))
                    movedToPaths.Add(movedAssets[i]);
            }

            foreach (var path in importedAssets)
            {
                if (IsExcluded(path)) continue;
                if (!TeamCreateSession.IsInSessionScope(path)) continue;
                if (movedToPaths.Contains(path)) continue;
                if (WasRecentlyBroadcast(path, now)) continue;
                string networkPath = TeamCreateSession.LocalToNetworkPath(path);
                if (networkPath == null) continue;
                BroadcastAssetCreatedOrModified(path, networkPath);
            }
            foreach (var path in deletedAssets)
            {
                if (IsExcluded(path)) continue;
                if (!TeamCreateSession.IsInSessionScope(path)) continue;
                string networkPath = TeamCreateSession.LocalToNetworkPath(path);
                if (networkPath == null) continue;
                BroadcastAssetDeleted(networkPath);
            }
            for (int i = 0; i < movedAssets.Length; i++)
            {
                var newPath = movedAssets[i];
                var oldPath = movedFromAssetPaths[i];
                bool newInScope = !IsExcluded(newPath) && TeamCreateSession.IsInSessionScope(newPath);
                bool oldInScope = !IsExcluded(oldPath) && TeamCreateSession.IsInSessionScope(oldPath);
                if (!newInScope && !oldInScope) continue;

                string netNew = newInScope ? TeamCreateSession.LocalToNetworkPath(newPath) : null;
                string netOld = oldInScope ? TeamCreateSession.LocalToNetworkPath(oldPath) : null;

                if (netNew != null && netOld != null)
                    BroadcastAssetMoved(netOld, netNew);
                else if (netNew != null)
                    BroadcastAssetCreatedOrModified(newPath, netNew);
                else if (netOld != null)
                    BroadcastAssetDeleted(netOld);
            }
        }

        private static bool WasRecentlyBroadcast(string path, long now)
        {
            if (!RecentBroadcasts.TryGetValue(path, out long ts)) return false;
            if (now - ts < DeduplicateWindowTicks) return true;
            RecentBroadcasts.Remove(path);
            return false;
        }

        public static void MarkRecentlyBroadcast(string path)
        {
            RecentBroadcasts[path] = DateTime.UtcNow.Ticks;
            _assetHashCache.Remove(path);
        }

        public static bool IsExcluded(string assetRelativePath)
        {
            if (string.IsNullOrEmpty(assetRelativePath)) return true;
            string normalised = assetRelativePath.Replace('\\', '/');
            if (!normalised.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return true;
            string[] parts = normalised.Split('/');
            foreach (var part in parts)
            {
                if (string.Equals(part, "TeamCreate", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(part, "Excluded", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(part, "PreviousScene", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void BroadcastAssetCreatedOrModified(string localPath, string networkPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string absPath = Path.Combine(projectRoot, localPath);
            if (!File.Exists(absPath)) return;

            byte[] content;
            try { content = File.ReadAllBytes(absPath); } catch { return; }

            string bundleId = Guid.NewGuid().ToString("N");
            string metaLocalPath = localPath + ".meta";
            string metaNetworkPath = networkPath + ".meta";
            string absMetaPath = Path.Combine(projectRoot, metaLocalPath);

            if (File.Exists(absMetaPath))
            {
                try
                {
                    byte[] metaContent = File.ReadAllBytes(absMetaPath);
                    string metaHash = ComputeMd5(metaContent);
                    string metaTransferId = Guid.NewGuid().ToString("N");
                    byte[] metaSend = TryCompress(metaContent, out bool metaComp);
                    var metaPayload = new AssetModifiedPayload
                    {
                        RelativePath = metaNetworkPath,
                        FileGuid = "",
                        Content = metaSend,
                        TransferId = metaTransferId,
                        ContentHash = metaHash,
                        BundleId = bundleId,
                        IsCompressed = metaComp
                    };
                    TeamCreateLogger.LogSend("ASSET_MODIFIED (meta)", metaNetworkPath);
                    TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.ASSET_MODIFIED,
                        TeamCreateSession.LocalPeerId, Protocol.Serialize(metaPayload)));
                    RegisterDeliveryForAllPeers(metaTransferId, metaNetworkPath, metaContent, MessageType.ASSET_MODIFIED, metaHash, bundleId);
                }
                catch { }
            }

            string fileGuid = AssetDatabase.AssetPathToGUID(localPath);
            bool isImported = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(localPath) != null;
            var msgType = isImported ? MessageType.ASSET_MODIFIED : MessageType.ASSET_CREATED;
            string assetHash = ComputeMd5(content);
            string assetTransferId = Guid.NewGuid().ToString("N");

            TeamCreateLogger.LogSend(msgType.ToString(), networkPath);

            if (content.Length <= Protocol.ChunkSize)
            {
                var msg = BuildFileMessage(networkPath, fileGuid, content, msgType, assetTransferId, assetHash, bundleId);
                TeamCreateSession.SendMessage(msg);
            }
            else
            {
                TeamCreateLogger.Log($"Sending {networkPath} as {(int)Math.Ceiling((double)content.Length / Protocol.ChunkSize)} chunks ({content.Length} bytes)");
                SendChunked(networkPath, fileGuid, content, msgType, assetTransferId, assetHash, bundleId, null);
            }
            RegisterDeliveryForAllPeers(assetTransferId, networkPath, content, msgType, assetHash, bundleId);
        }

        private static void BroadcastAssetDeleted(string relativePath)
        {
            TeamCreateLogger.LogSend("ASSET_DELETED", relativePath);
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.ASSET_DELETED,
                TeamCreateSession.LocalPeerId,
                Protocol.Serialize(new AssetDeletedPayload { RelativePath = relativePath })));
        }

        private static void BroadcastAssetMoved(string oldPath, string newPath)
        {
            TeamCreateLogger.LogSend("ASSET_MOVED", $"{oldPath} → {newPath}");
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.ASSET_MOVED,
                TeamCreateSession.LocalPeerId,
                Protocol.Serialize(new AssetMovedPayload { OldRelativePath = oldPath, NewRelativePath = newPath })));
        }

        private static NetworkMessage BuildFileMessage(string relativePath, string fileGuid, byte[] content, MessageType msgType, string transferId, string contentHash, string bundleId)
        {
            byte[] sendData = TryCompress(content, out bool isCompressed);
            var payload = new AssetModifiedPayload
            {
                RelativePath = relativePath,
                FileGuid = fileGuid,
                Content = sendData,
                TransferId = transferId,
                ContentHash = contentHash,
                BundleId = bundleId,
                IsCompressed = isCompressed
            };
            return Protocol.CreateMessage(msgType, TeamCreateSession.LocalPeerId, Protocol.Serialize(payload));
        }

        private static void RegisterDeliveryForAllPeers(string transferId, string relativePath, byte[] content, MessageType msgType, string contentHash, string bundleId)
        {
            if (!TeamCreateSession.IsHosting) return;
            var peers = TeamCreateSession.GetPeers();
            if (peers.Count == 0) return;
            var pendingIds = new HashSet<string>();
            foreach (var p in peers) pendingIds.Add(p.PeerId);
            PendingDeliveries[transferId] = new PendingDelivery
            {
                TransferId = transferId,
                RelativePath = relativePath,
                Content = content,
                MsgType = msgType,
                ContentHash = contentHash,
                BundleId = bundleId,
                PendingPeerIds = pendingIds,
                RetryCount = 0,
                NextRetryTicks = DateTime.UtcNow.Ticks + BaseRetryTicks
            };
        }

        private static void SendChunked(string relativePath, string fileGuid, byte[] content, MessageType originalType, string transferId, string contentHash, string bundleId, string oldRelativePath)
        {
            byte[] dataToSend = TryCompress(content, out bool isCompressed);
            int totalChunks = (int)Math.Ceiling((double)dataToSend.Length / Protocol.ChunkSize);
            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * Protocol.ChunkSize;
                int size = Math.Min(Protocol.ChunkSize, dataToSend.Length - offset);
                byte[] chunk = new byte[size];
                Buffer.BlockCopy(dataToSend, offset, chunk, 0, size);
                var chunkPayload = new AssetChunkPayload
                {
                    TransferId = transferId,
                    RelativePath = relativePath,
                    FileGuid = fileGuid,
                    OriginalMessageType = originalType,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    ChunkData = chunk,
                    OldRelativePath = oldRelativePath,
                    ContentHash = i == totalChunks - 1 ? contentHash : null,
                    BundleId = bundleId,
                    IsCompressed = isCompressed
                };
                TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.ASSET_CHUNK, TeamCreateSession.LocalPeerId, Protocol.Serialize(chunkPayload)));
            }
        }

        private static void SendChunkedToPeer(string peerId, string relativePath, string fileGuid, byte[] content, MessageType originalType, string transferId, string contentHash, string bundleId)
        {
            byte[] dataToSend = TryCompress(content, out bool isCompressed);
            int totalChunks = (int)Math.Ceiling((double)dataToSend.Length / Protocol.ChunkSize);
            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * Protocol.ChunkSize;
                int size = Math.Min(Protocol.ChunkSize, dataToSend.Length - offset);
                byte[] chunk = new byte[size];
                Buffer.BlockCopy(dataToSend, offset, chunk, 0, size);
                var chunkPayload = new AssetChunkPayload
                {
                    TransferId = transferId,
                    RelativePath = relativePath,
                    FileGuid = fileGuid,
                    OriginalMessageType = originalType,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    ChunkData = chunk,
                    ContentHash = i == totalChunks - 1 ? contentHash : null,
                    BundleId = bundleId,
                    IsCompressed = isCompressed
                };
                TeamCreateSession.Host?.SendToPeer(peerId, Protocol.CreateMessage(MessageType.ASSET_CHUNK, TeamCreateSession.LocalPeerId, Protocol.Serialize(chunkPayload)));
            }
        }

        public static void HandleChunkReceived(NetworkMessage message)
        {
            var chunk = Protocol.Deserialize<AssetChunkPayload>(message.Payload);
            if (chunk == null) return;
            if (!PendingChunks.TryGetValue(chunk.TransferId, out var buffer))
            {
                buffer = new ChunkBuffer
                {
                    RelativePath = chunk.RelativePath,
                    FileGuid = chunk.FileGuid,
                    OriginalType = chunk.OriginalMessageType,
                    OldRelativePath = chunk.OldRelativePath,
                    TotalChunks = chunk.TotalChunks,
                    Chunks = new byte[chunk.TotalChunks][],
                    ReceivedCount = 0,
                    TransferId = chunk.TransferId,
                    BundleId = chunk.BundleId,
                    IsCompressed = chunk.IsCompressed
                };
                PendingChunks[chunk.TransferId] = buffer;
                TeamCreateLogger.LogRecv($"ASSET_CHUNK (new transfer {chunk.TransferId}, {chunk.TotalChunks} chunks) for {chunk.RelativePath}", message.SenderId);
            }

            if (!string.IsNullOrEmpty(chunk.ContentHash))
                buffer.ContentHash = chunk.ContentHash;

            if (buffer.Chunks[chunk.ChunkIndex] == null)
            {
                buffer.Chunks[chunk.ChunkIndex] = chunk.ChunkData;
                buffer.ReceivedCount++;
            }

            if (buffer.ReceivedCount == buffer.TotalChunks)
            {
                PendingChunks.Remove(chunk.TransferId);
                int totalSize = 0;
                foreach (var c in buffer.Chunks) totalSize += c.Length;
                byte[] assembled = new byte[totalSize];
                int offset = 0;
                foreach (var c in buffer.Chunks) { Buffer.BlockCopy(c, 0, assembled, offset, c.Length); offset += c.Length; }
                if (buffer.IsCompressed)
                {
                    try { assembled = Decompress(assembled); }
                    catch (Exception e) { Debug.LogError($"[TeamCreate] Decompress failed for {buffer.RelativePath}: {e.Message}"); return; }
                }
                TeamCreateLogger.Log($"Chunk transfer complete for {buffer.RelativePath} ({assembled.Length} bytes)");
                string chunkLocalPath = TeamCreateSession.NetworkToLocalPath(buffer.RelativePath);
                ReceiveAssetFile(chunkLocalPath, assembled, buffer.TransferId, buffer.ContentHash, buffer.BundleId, buffer.OriginalType);
                if (TeamCreateSession.IsHosting)
                    RegisterDeliveryForAllPeers(buffer.TransferId, buffer.RelativePath, assembled, buffer.OriginalType, buffer.ContentHash, buffer.BundleId);
            }
        }

        public static void ApplyAssetCreated(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<AssetCreatedPayload>(message.Payload);
            if (payload == null) return;
            string localPath = TeamCreateSession.NetworkToLocalPath(payload.RelativePath);
            if (IsExcluded(localPath)) return;
            bool isMeta = localPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
            if (!isMeta && !TeamCreateConflictResolver.ShouldApply(TeamCreateConflictResolver.MakeAssetKey(payload.RelativePath), message.Timestamp)) return;
            TeamCreateLogger.LogRecv("ASSET_CREATED", message.SenderId);
            byte[] content = payload.IsCompressed ? Decompress(payload.Content) : payload.Content;
            ReceiveAssetFile(localPath, content, payload.TransferId, payload.ContentHash, payload.BundleId, MessageType.ASSET_CREATED);
        }

        public static void ApplyAssetModified(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<AssetModifiedPayload>(message.Payload);
            if (payload == null) return;
            string localPath = TeamCreateSession.NetworkToLocalPath(payload.RelativePath);
            if (IsExcluded(localPath)) return;
            bool isMeta = localPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
            if (!isMeta && !TeamCreateConflictResolver.ShouldApply(TeamCreateConflictResolver.MakeAssetKey(payload.RelativePath), message.Timestamp)) return;
            TeamCreateLogger.LogRecv("ASSET_MODIFIED", message.SenderId);
            byte[] content = payload.IsCompressed ? Decompress(payload.Content) : payload.Content;
            ReceiveAssetFile(localPath, content, payload.TransferId, payload.ContentHash, payload.BundleId, MessageType.ASSET_MODIFIED);
        }

        public static void ApplyAssetDeleted(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<AssetDeletedPayload>(message.Payload);
            if (payload == null) return;
            string localPath = TeamCreateSession.NetworkToLocalPath(payload.RelativePath);
            if (IsExcluded(localPath)) return;
            TeamCreateLogger.LogRecv("ASSET_DELETED", message.SenderId);
            TeamCreateSession.IsApplyingRemoteChange = true;
            try { AssetDatabase.DeleteAsset(localPath); }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        public static void ApplyAssetMoved(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<AssetMovedPayload>(message.Payload);
            if (payload == null) return;
            string localOld = TeamCreateSession.NetworkToLocalPath(payload.OldRelativePath);
            string localNew = TeamCreateSession.NetworkToLocalPath(payload.NewRelativePath);
            if (IsExcluded(localNew) && IsExcluded(localOld)) return;
            TeamCreateLogger.LogRecv("ASSET_MOVED", message.SenderId);
            TeamCreateSession.IsApplyingRemoteChange = true;
            try { AssetDatabase.MoveAsset(localOld, localNew); }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }
        }

        private static void ReceiveAssetFile(string relativePath, byte[] content, string transferId, string contentHash, string bundleId, MessageType msgType)
        {
            if (content == null) return;
            bool isMeta = relativePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

            if (isMeta)
            {
                string assetPath = relativePath.Substring(0, relativePath.Length - 5);
                WriteFileRaw(relativePath, content, transferId, contentHash, false);
                MarkRecentlyBroadcast(assetPath);

                if (!string.IsNullOrEmpty(bundleId))
                {
                    if (!ReceiverBundles.TryGetValue(bundleId, out var bundle))
                    {
                        bundle = new ReceiverBundle
                        {
                            BundleId = bundleId,
                            AssetPath = assetPath,
                            FirstReceivedTime = EditorApplication.timeSinceStartup,
                            MetaWritten = true
                        };
                        ReceiverBundles[bundleId] = bundle;
                    }
                    else
                    {
                        bundle.MetaWritten = true;
                        if (bundle.AssetContent != null)
                        {
                            ReceiverBundles.Remove(bundleId);
                            ImportAssetFile(bundle.AssetPath, bundle.AssetContent, bundle.AssetTransferId, bundle.AssetContentHash);
                            return;
                        }
                    }
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(bundleId))
                {
                    if (ReceiverBundles.TryGetValue(bundleId, out var bundle) && bundle.MetaWritten)
                    {
                        ReceiverBundles.Remove(bundleId);
                        ImportAssetFile(relativePath, content, transferId, contentHash);
                        return;
                    }
                    else
                    {
                        if (!ReceiverBundles.TryGetValue(bundleId, out bundle))
                        {
                            bundle = new ReceiverBundle
                            {
                                BundleId = bundleId,
                                AssetPath = relativePath,
                                FirstReceivedTime = EditorApplication.timeSinceStartup,
                                MetaWritten = false
                            };
                            ReceiverBundles[bundleId] = bundle;
                        }
                        bundle.AssetContent = content;
                        bundle.AssetTransferId = transferId;
                        bundle.AssetContentHash = contentHash;
                        bundle.AssetMsgType = msgType;
                        return;
                    }
                }

                ImportAssetFile(relativePath, content, transferId, contentHash);
            }
        }

        private static void WriteFileRaw(string relativePath, byte[] content, string transferId, string contentHash, bool sendAck)
        {
            if (IsExcluded(relativePath) || content == null) return;

            bool isMeta = relativePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
            if (isMeta && !string.IsNullOrEmpty(TeamCreateSession.SessionFolderName))
            {
                string assetPathForMeta = relativePath.Substring(0, relativePath.Length - 5);
                if (assetPathForMeta.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    return;
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string absPath = Path.Combine(projectRoot, relativePath);
            string dir = Path.GetDirectoryName(absPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            TeamCreateSession.IsApplyingRemoteChange = true;
            bool success = false;
            try
            {
                File.WriteAllBytes(absPath, content);
                success = true;
            }
            catch (Exception e) { Debug.LogError($"[TeamCreate] Failed to write '{relativePath}': {e.Message}"); }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }

            if (success && sendAck)
                SendAck(transferId, contentHash, content, true);
        }

        private static void ImportAssetFile(string relativePath, byte[] content, string transferId, string contentHash)
        {
            if (IsExcluded(relativePath) || content == null) return;

            bool isMat = relativePath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string absPath = Path.Combine(projectRoot, relativePath);
            string dir = Path.GetDirectoryName(absPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            TeamCreateSession.IsApplyingRemoteChange = true;
            bool success = false;
            try
            {
                File.WriteAllBytes(absPath, content);

                if (!string.IsNullOrEmpty(contentHash))
                {
                    string actualHash = ComputeMd5(File.ReadAllBytes(absPath));
                    if (actualHash != contentHash)
                    {
                        Debug.LogWarning($"[TeamCreate] Content hash mismatch for '{relativePath}' — requesting resend.");
                        SendAck(transferId, contentHash, content, false);
                        return;
                    }
                }

                AssetDatabase.ImportAsset(relativePath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                success = true;

                MarkRecentlyBroadcast(relativePath);
                if (isMat)
                    TeamCreateHierarchySync.NotifyRemoteMaterialImported(relativePath);
                TeamCreateSnapshotBuilder.NotifyAssetArrived(relativePath);
                TeamCreateSnapshotBuilder.RetryPendingRefs();
            }
            catch (Exception e) { Debug.LogError($"[TeamCreate] Failed to import '{relativePath}': {e.Message}"); }
            finally { TeamCreateSession.IsApplyingRemoteChange = false; }

            if (success)
                SendAck(transferId, contentHash, content, true);
        }

        private static void SendAck(string transferId, string contentHash, byte[] content, bool success)
        {
            if (string.IsNullOrEmpty(transferId) || !TeamCreateSession.IsConnected) return;
            if (TeamCreateSession.IsHosting) return;
            var ack = new AssetChunkAckPayload
            {
                TransferId = transferId,
                ChunkIndex = -1,
                Success = success,
                ContentHash = contentHash
            };
            TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.ASSET_CHUNK_ACK,
                TeamCreateSession.LocalPeerId, Protocol.Serialize(ack)));
        }

        public static void HandleDeliveryAck(NetworkMessage message)
        {
            var ack = Protocol.Deserialize<AssetChunkAckPayload>(message.Payload);
            if (ack == null || string.IsNullOrEmpty(ack.TransferId)) return;

            if (!PendingDeliveries.TryGetValue(ack.TransferId, out var delivery)) return;

            if (!ack.Success)
            {
                TeamCreateLogger.Log($"[AssetDelivery] Peer {message.SenderId} reported hash mismatch for '{delivery.RelativePath}', resending immediately.");
                delivery.NextRetryTicks = DateTime.UtcNow.Ticks;
                return;
            }

            delivery.PendingPeerIds.Remove(message.SenderId);
            if (delivery.PendingPeerIds.Count == 0)
            {
                PendingDeliveries.Remove(ack.TransferId);
                TeamCreateLogger.Log($"[AssetDelivery] All peers confirmed '{delivery.RelativePath}'");
            }
        }

        private static void BroadcastManifestSync()
        {
            if (!TeamCreateSession.IsHosting) return;
            if (_manifestBuildInProgress) return;
            var peers = TeamCreateSession.GetPeers();
            if (peers.Count == 0) return;

            TeamCreateHierarchySync.FlushPendingMaterialsToDisk();

            string assetsRoot = Application.dataPath;
            string sceneHash = ComputeSceneHash();
            var cacheCopy = new Dictionary<string, (long ModifiedTicks, string Hash)>(_assetHashCache);

            _manifestBuildInProgress = true;

            Task.Run(() =>
            {
                var manifest = new List<AssetManifestEntry>();
                var cacheUpdates = new Dictionary<string, (long ModifiedTicks, string Hash)>();

                try
                {
                    string sessionFolder = TeamCreateSession.SessionFolderName;
                    string scanRoot = (!string.IsNullOrEmpty(sessionFolder))
                        ? Path.Combine(assetsRoot, sessionFolder)
                        : assetsRoot;
                    if (!Directory.Exists(scanRoot)) scanRoot = assetsRoot;

                    string[] files = Directory.GetFiles(scanRoot, "*", SearchOption.AllDirectories);
                    foreach (string file in files)
                    {
                        if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                        string relative = "Assets" + file.Substring(assetsRoot.Length).Replace('\\', '/');
                        if (IsExcluded(relative)) continue;
                        try
                        {
                            long modTicks = File.GetLastWriteTimeUtc(file).Ticks;
                            string hash;
                            if (cacheCopy.TryGetValue(relative, out var cached) && cached.ModifiedTicks == modTicks)
                            {
                                hash = cached.Hash;
                            }
                            else
                            {
                                hash = ComputeMd5(File.ReadAllBytes(file));
                                cacheUpdates[relative] = (modTicks, hash);
                            }
                            manifest.Add(new AssetManifestEntry { RelativePath = relative, Md5Hash = hash });
                        }
                        catch { }
                    }
                }
                catch { }

                TeamCreateSession.EnqueueMainThread(() =>
                {
                    _manifestBuildInProgress = false;
                    foreach (var kvp in cacheUpdates)
                        _assetHashCache[kvp.Key] = kvp.Value;

                    if (!TeamCreateSession.IsHosting || TeamCreateSession.GetPeers().Count == 0) return;

                    var syncPayload = new AssetManifestSyncPayload { Manifest = manifest, SceneHash = sceneHash };
                    TeamCreateLogger.Log($"[AssetReconcile] Broadcasting manifest ({manifest.Count} assets) to {peers.Count} peer(s)");
                    TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.ASSET_MANIFEST_SYNC,
                        TeamCreateSession.LocalPeerId, Protocol.Serialize(syncPayload)));
                });
            });
        }

        private static string ComputeSceneHash()
        {
            var guids = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    CollectGuidsDfs(root.transform, guids);
            }
            guids.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string g in guids) { sb.Append(g); sb.Append(','); }
            return ComputeMd5(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static void CollectGuidsDfs(Transform t, List<string> guids)
        {
            string guid = TeamCreateIdentity.GetGuid(t.gameObject);
            if (!string.IsNullOrEmpty(guid)) guids.Add(guid);
            for (int i = 0; i < t.childCount; i++)
                CollectGuidsDfs(t.GetChild(i), guids);
        }

        public static void HandleManifestSync(NetworkMessage message)
        {
            var payload = Protocol.Deserialize<AssetManifestSyncPayload>(message.Payload);
            if (payload == null) return;

            if (!string.IsNullOrEmpty(payload.SceneHash))
            {
                string localSceneHash = ComputeSceneHash();
                if (localSceneHash != payload.SceneHash)
                {
                    TeamCreateLogger.Log("[AssetReconcile] Scene hash mismatch — requesting full state resync.");
                    TeamCreateSession.Client?.RequestFullState();
                }
            }

            if (payload.Manifest == null || payload.Manifest.Count == 0) return;

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var cacheCopy = new Dictionary<string, (long ModifiedTicks, string Hash)>(_assetHashCache);

            Task.Run(() =>
            {
                var toRequest = new List<string>();
                var cacheUpdates = new Dictionary<string, (long ModifiedTicks, string Hash)>();

                foreach (var entry in payload.Manifest)
                {
                    string localPath = TeamCreateSession.NetworkToLocalPath(entry.RelativePath);
                    if (IsExcluded(localPath)) continue;
                    if (localPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) &&
                        TeamCreateHierarchySync.IsRecentlyReceivedMaterial(localPath)) continue;

                    string absPath = Path.Combine(projectRoot, localPath);
                    if (!File.Exists(absPath))
                    {
                        toRequest.Add(entry.RelativePath);
                        continue;
                    }

                    try
                    {
                        long modTicks = File.GetLastWriteTimeUtc(absPath).Ticks;
                        string localHash;
                        if (cacheCopy.TryGetValue(localPath, out var cached) && cached.ModifiedTicks == modTicks)
                        {
                            localHash = cached.Hash;
                        }
                        else
                        {
                            localHash = ComputeMd5(File.ReadAllBytes(absPath));
                            cacheUpdates[localPath] = (modTicks, localHash);
                        }

                        if (localHash != entry.Md5Hash)
                            toRequest.Add(entry.RelativePath);
                    }
                    catch { toRequest.Add(entry.RelativePath); }
                }

                TeamCreateSession.EnqueueMainThread(() =>
                {
                    foreach (var kvp in cacheUpdates)
                        _assetHashCache[kvp.Key] = kvp.Value;

                    if (toRequest.Count == 0) return;

                    TeamCreateLogger.Log($"[AssetReconcile] {toRequest.Count} asset(s) out of sync — requesting resend.");
                    var req = new FullStateAssetRequestPayload { RelativePaths = toRequest };
                    TeamCreateSession.SendMessage(Protocol.CreateMessage(MessageType.FULL_STATE_ASSET_REQUEST,
                        TeamCreateSession.LocalPeerId, Protocol.Serialize(req)));
                });
            });
        }

        public static void SendFileDirectly(string localPath, byte[] content, MessageType msgType, string oldLocalPath = null)
        {
            string networkPath = TeamCreateSession.LocalToNetworkPath(localPath);
            if (networkPath == null) return;
            string oldNetworkPath = oldLocalPath != null ? TeamCreateSession.LocalToNetworkPath(oldLocalPath) : null;
            string fileGuid = AssetDatabase.AssetPathToGUID(localPath);
            MarkRecentlyBroadcast(localPath);
            string transferId = Guid.NewGuid().ToString("N");
            string contentHash = ComputeMd5(content);
            string bundleId = Guid.NewGuid().ToString("N");

            if (content.Length <= Protocol.ChunkSize)
            {
                var msg = BuildFileMessage(networkPath, fileGuid, content, msgType, transferId, contentHash, bundleId);
                TeamCreateSession.SendMessage(msg);
            }
            else
            {
                SendChunked(networkPath, fileGuid, content, msgType, transferId, contentHash, bundleId, oldNetworkPath);
            }
            RegisterDeliveryForAllPeers(transferId, networkPath, content, msgType, contentHash, bundleId);
        }

        public static string ComputeMd5(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(data);
                var sb = new StringBuilder();
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static byte[] TryCompress(byte[] data, out bool compressed)
        {
            compressed = false;
            if (data == null || data.Length < 512) return data;
            try
            {
                using var ms = new MemoryStream();
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    gz.Write(data, 0, data.Length);
                var result = ms.ToArray();
                if (result.Length < data.Length)
                {
                    compressed = true;
                    return result;
                }
            }
            catch { }
            return data;
        }

        private static byte[] Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }

        public static void SendFileToPeer(PeerConnection peer, string relativePath, byte[] content, MessageType msgType)
        {
            string fileGuid = AssetDatabase.AssetPathToGUID(relativePath);
            SendFileToPeerWithGuid(peer, relativePath, fileGuid, content, msgType, TeamCreateSession.LocalPeerId, registerDelivery: true);
        }


        public static void SendFileToPeerDirect(PeerConnection peer, string relativePath, string fileGuid, byte[] content, MessageType msgType, string localPeerId, string bundleId = null)
        {
            string transferId = Guid.NewGuid().ToString("N");
            string contentHash = ComputeMd5(content);
            bundleId = bundleId ?? Guid.NewGuid().ToString("N");

            byte[] dataToSend = TryCompress(content, out bool isCompressed);

            if (dataToSend.Length <= Protocol.ChunkSize)
            {
                var payload = new AssetModifiedPayload
                {
                    RelativePath = relativePath,
                    FileGuid = fileGuid,
                    Content = dataToSend,
                    TransferId = transferId,
                    ContentHash = contentHash,
                    BundleId = bundleId,
                    IsCompressed = isCompressed
                };
                peer.SendMessage(Protocol.CreateMessage(msgType, localPeerId, Protocol.Serialize(payload)));
            }
            else
            {
                int totalChunks = (int)Math.Ceiling((double)dataToSend.Length / Protocol.ChunkSize);
                for (int i = 0; i < totalChunks; i++)
                {
                    int offset = i * Protocol.ChunkSize;
                    int size = Math.Min(Protocol.ChunkSize, dataToSend.Length - offset);
                    byte[] chunk = new byte[size];
                    Buffer.BlockCopy(dataToSend, offset, chunk, 0, size);
                    var chunkPayload = new AssetChunkPayload
                    {
                        TransferId = transferId,
                        RelativePath = relativePath,
                        FileGuid = fileGuid,
                        OriginalMessageType = msgType,
                        ChunkIndex = i,
                        TotalChunks = totalChunks,
                        ChunkData = chunk,
                        ContentHash = i == totalChunks - 1 ? contentHash : null,
                        BundleId = bundleId,
                        IsCompressed = isCompressed
                    };
                    peer.SendMessage(Protocol.CreateMessage(MessageType.ASSET_CHUNK, localPeerId, Protocol.Serialize(chunkPayload)));
                }
            }
        }

        private static void SendFileToPeerWithGuid(PeerConnection peer, string relativePath, string fileGuid, byte[] content, MessageType msgType, string localPeerId, bool registerDelivery)
        {
            string transferId = Guid.NewGuid().ToString("N");
            string contentHash = ComputeMd5(content);
            string bundleId = Guid.NewGuid().ToString("N");

            byte[] dataToSend = TryCompress(content, out bool isCompressed);

            if (dataToSend.Length <= Protocol.ChunkSize)
            {
                var payload = new AssetModifiedPayload
                {
                    RelativePath = relativePath,
                    FileGuid = fileGuid,
                    Content = dataToSend,
                    TransferId = transferId,
                    ContentHash = contentHash,
                    BundleId = bundleId,
                    IsCompressed = isCompressed
                };
                peer.SendMessage(Protocol.CreateMessage(msgType, localPeerId, Protocol.Serialize(payload)));
            }
            else
            {
                int totalChunks = (int)Math.Ceiling((double)dataToSend.Length / Protocol.ChunkSize);
                for (int i = 0; i < totalChunks; i++)
                {
                    int offset = i * Protocol.ChunkSize;
                    int size = Math.Min(Protocol.ChunkSize, dataToSend.Length - offset);
                    byte[] chunk = new byte[size];
                    Buffer.BlockCopy(dataToSend, offset, chunk, 0, size);
                    var chunkPayload = new AssetChunkPayload
                    {
                        TransferId = transferId,
                        RelativePath = relativePath,
                        FileGuid = fileGuid,
                        OriginalMessageType = msgType,
                        ChunkIndex = i,
                        TotalChunks = totalChunks,
                        ChunkData = chunk,
                        ContentHash = i == totalChunks - 1 ? contentHash : null,
                        BundleId = bundleId,
                        IsCompressed = isCompressed
                    };
                    peer.SendMessage(Protocol.CreateMessage(MessageType.ASSET_CHUNK, localPeerId, Protocol.Serialize(chunkPayload)));
                }
            }

            if (registerDelivery)
            {
                PendingDeliveries[transferId] = new PendingDelivery
                {
                    TransferId = transferId,
                    RelativePath = relativePath,
                    Content = content,
                    MsgType = msgType,
                    ContentHash = contentHash,
                    BundleId = bundleId,
                    PendingPeerIds = new HashSet<string> { peer.PeerId },
                    RetryCount = 0,
                    NextRetryTicks = DateTime.UtcNow.Ticks + BaseRetryTicks
                };
            }
        }
    }
}
#endif
