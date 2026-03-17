#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace TeamCreate.Editor
{
    public enum MessageType : byte
    {
        HANDSHAKE_REQUEST = 1,
        HANDSHAKE_ACCEPT = 2,
        PEER_JOINED = 3,
        PEER_LEFT = 4,
        FULL_STATE_REQUEST = 5,
        FULL_STATE_RESPONSE = 6,
        ASSET_CREATED = 7,
        ASSET_MODIFIED = 8,
        ASSET_DELETED = 9,
        ASSET_MOVED = 10,
        GAMEOBJECT_CREATED = 11,
        GAMEOBJECT_DELETED = 12,
        GAMEOBJECT_REPARENTED = 13,
        GAMEOBJECT_RENAMED = 14,
        GAMEOBJECT_ACTIVATED = 15,
        COMPONENT_ADDED = 16,
        COMPONENT_REMOVED = 17,
        COMPONENT_PROPERTY_CHANGED = 18,
        SCENE_OPENED = 19,
        SCENE_CLOSED = 20,
        SCENE_ACTIVE_CHANGED = 21,
        SCENE_RENAMED = 22,
        ASSET_CHUNK = 23,
        ASSET_CHUNK_ACK = 24,
        FULL_STATE_ASSET_REQUEST = 25,
        KICK_PEER = 26,
        HANDSHAKE_REJECTED = 27,
        PEER_TRANSFORM = 28,
        PING = 29,
        PONG = 30,
        PEER_SELECTION = 31,
        TAG_ADDED = 32,
        TAG_REMOVED = 33,
        ASSET_MANIFEST_SYNC = 34,
        MATERIAL_PROPERTY_CHANGED = 35,
        SCENE_VISIBILITY_CHANGED = 36,
        HOST_RELOADING = 37,
        ANIM_TIME_SYNC = 38,
        LIGHTMAP_SYNC = 39,
        LIGHTMAP_SYNC_REQUEST = 40,
    }

    public class NetworkMessage
    {
        public MessageType Type { get; set; }
        public string SenderId { get; set; }
        public long Timestamp { get; set; }
        public byte[] Payload { get; set; }
    }

    public class PackageEntry
    {
        public string Name { get; set; }
        public string Version { get; set; }
    }

    public class HandshakeRequestPayload
    {
        public string PeerId { get; set; }
        public string Username { get; set; }
        public string ProtocolVersion { get; set; }
        public List<PackageEntry> Packages { get; set; }
    }

    public class HandshakeAcceptPayload
    {
        public string SessionId { get; set; }
        public string HostId { get; set; }
        public string HostUsername { get; set; }
        public List<PeerInfo> ConnectedPeers { get; set; }
        public List<PackageEntry> Packages { get; set; }
    }

    public class HandshakeRejectedPayload
    {
        public string Reason { get; set; }
    }

    public class PeerInfo
    {
        public string PeerId { get; set; }
        public string Username { get; set; }
        public string IpAddress { get; set; }
    }

    public class PeerEventPayload
    {
        public string PeerId { get; set; }
        public string Username { get; set; }
        public bool IsReloading { get; set; }
    }

    public class KickPeerPayload
    {
        public string TargetPeerId { get; set; }
        public string Reason { get; set; }
    }

    public class PeerTransformPayload
    {
        public string PeerId { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float RotW { get; set; }
    }

    public class PeerSelectionPayload
    {
        public string PeerId { get; set; }
        public List<string> SelectedGuids { get; set; }
        public List<string> SelectedAssetGuids { get; set; }
    }

    public class PongPayload
    {
        public long OriginalTimestamp { get; set; }
    }

    public class AssetCreatedPayload
    {
        public string RelativePath { get; set; }
        public string FileGuid { get; set; }
        public byte[] Content { get; set; }
        public string TransferId { get; set; }
        public string ContentHash { get; set; }
        public string BundleId { get; set; }
        public bool IsCompressed { get; set; }
    }

    public class AssetModifiedPayload
    {
        public string RelativePath { get; set; }
        public string FileGuid { get; set; }
        public byte[] Content { get; set; }
        public string TransferId { get; set; }
        public string ContentHash { get; set; }
        public string BundleId { get; set; }
        public bool IsCompressed { get; set; }
    }

    public class AssetDeletedPayload
    {
        public string RelativePath { get; set; }
    }

    public class AssetMovedPayload
    {
        public string OldRelativePath { get; set; }
        public string NewRelativePath { get; set; }
    }

    public class AssetChunkPayload
    {
        public string TransferId { get; set; }
        public string RelativePath { get; set; }
        public string FileGuid { get; set; }
        public MessageType OriginalMessageType { get; set; }
        public int ChunkIndex { get; set; }
        public int TotalChunks { get; set; }
        public byte[] ChunkData { get; set; }
        public string OldRelativePath { get; set; }
        public string ContentHash { get; set; }
        public string BundleId { get; set; }
        public bool IsCompressed { get; set; }
    }

    public class AssetChunkAckPayload
    {
        public string TransferId { get; set; }
        public int ChunkIndex { get; set; }
        public bool Success { get; set; }
        public string ContentHash { get; set; }
    }

    public class AssetManifestSyncPayload
    {
        public List<AssetManifestEntry> Manifest { get; set; }
        public string SceneHash { get; set; }
    }

    public class GameObjectCreatedPayload
    {
        public string Guid { get; set; }
        public string Name { get; set; }
        public string ParentGuid { get; set; }
        public int SiblingIndex { get; set; }
        public int Layer { get; set; }
        public string Tag { get; set; }
        public bool IsActive { get; set; }
        public string ScenePath { get; set; }
        public string SceneNetId { get; set; }
        public string PrefabAssetGuid { get; set; }
        public List<ComponentData> Components { get; set; }
    }

    public class GameObjectDeletedPayload
    {
        public string Guid { get; set; }
    }

    public class GameObjectReparentedPayload
    {
        public string Guid { get; set; }
        public string NewParentGuid { get; set; }
        public int NewSiblingIndex { get; set; }
        public float LocalPosX { get; set; }
        public float LocalPosY { get; set; }
        public float LocalPosZ { get; set; }
        public float LocalRotX { get; set; }
        public float LocalRotY { get; set; }
        public float LocalRotZ { get; set; }
        public float LocalRotW { get; set; }
        public float LocalScaleX { get; set; }
        public float LocalScaleY { get; set; }
        public float LocalScaleZ { get; set; }
    }

    public class GameObjectRenamedPayload
    {
        public string Guid { get; set; }
        public string NewName { get; set; }
        public bool HasGoProperties { get; set; }
        public int Layer { get; set; }
        public string Tag { get; set; }
        public int StaticFlags { get; set; }
    }

    public class GameObjectActivatedPayload
    {
        public string Guid { get; set; }
        public bool IsActive { get; set; }
    }

    public class ComponentAddedPayload
    {
        public string GameObjectGuid { get; set; }
        public string ComponentTypeName { get; set; }
        public string ComponentGuid { get; set; }
        public Dictionary<string, string> Properties { get; set; }
    }

    public class ComponentRemovedPayload
    {
        public string GameObjectGuid { get; set; }
        public string ComponentTypeName { get; set; }
        public string ComponentGuid { get; set; }
    }

    public class ComponentPropertyChangedPayload
    {
        public string GameObjectGuid { get; set; }
        public string ComponentTypeName { get; set; }
        public string ComponentGuid { get; set; }
        public Dictionary<string, string> Properties { get; set; }
    }

    public class SceneOpenedPayload
    {
        public string SceneAssetGuid { get; set; }
        public string SceneName { get; set; }
        public string ScenePath { get; set; }
        public bool SetActive { get; set; }
        public bool IsSingleMode { get; set; }
        public SceneData SceneData { get; set; }
        public string SceneNetId { get; set; }
    }

    public class SceneClosedPayload
    {
        public string ScenePath { get; set; }
        public string SceneName { get; set; }
        public string SceneNetId { get; set; }
    }

    public class SceneActiveChangedPayload
    {
        public string ScenePath { get; set; }
        public string SceneName { get; set; }
    }

    public class SceneRenamedPayload
    {
        public string OldScenePath { get; set; }
        public string NewScenePath { get; set; }
        public string NewSceneName { get; set; }
    }

    public class TagAddedPayload
    {
        public string TagName { get; set; }
    }

    public class TagRemovedPayload
    {
        public string TagName { get; set; }
    }

    public class ComponentData
    {
        public string TypeName { get; set; }
        public string ComponentGuid { get; set; }
        public Dictionary<string, string> Properties { get; set; }
    }

    public class GameObjectData
    {
        public string Guid { get; set; }
        public string Name { get; set; }
        public string ParentGuid { get; set; }
        public int SiblingIndex { get; set; }
        public int Layer { get; set; }
        public string Tag { get; set; }
        public bool IsActive { get; set; }
        public string ScenePath { get; set; }
        public List<ComponentData> Components { get; set; }
    }

    public class SceneData
    {
        public string ScenePath { get; set; }
        public string SceneName { get; set; }
        public string SceneAssetGuid { get; set; }
        public bool IsActive { get; set; }
        public List<GameObjectData> GameObjects { get; set; }
    }

    public class AssetManifestEntry
    {
        public string RelativePath { get; set; }
        public string Md5Hash { get; set; }
        public string AssetGuid { get; set; }
    }

    public class FullStateResponsePayload
    {
        public List<SceneData> Scenes { get; set; }
        public List<AssetManifestEntry> AssetManifest { get; set; }
    }

    public class FullStateAssetRequestPayload
    {
        public List<string> RelativePaths { get; set; }
    }

    public class MaterialPropertyEntry
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
        public float TexOffsetX { get; set; }
        public float TexOffsetY { get; set; }
        public float TexScaleX { get; set; }
        public float TexScaleY { get; set; }
    }

    public class SceneVisibilityPayload
    {
        public string GoGuid { get; set; }
        public bool IsHidden { get; set; }
        public bool IsPickingDisabled { get; set; }
    }

    public class MaterialPropertyChangedPayload
    {
        public string RelativePath { get; set; }
        public string ShaderName { get; set; }
        public string ShaderGuid { get; set; }
        public int RenderQueue { get; set; }
        public List<MaterialPropertyEntry> Properties { get; set; }
        public List<string> EnabledKeywords { get; set; }
        public bool EnableInstancing { get; set; }
        public bool DoubleSidedGI { get; set; }
    }

    public class LightmapSlotEntry
    {
        public string LightmapColorGuid { get; set; }
        public string LightmapDirGuid { get; set; }
        public string ShadowMaskGuid { get; set; }
    }

    public class LightmapRendererEntry
    {
        public string GoGuid { get; set; }
        public string ComponentGuid { get; set; }
        public int LightmapIndex { get; set; }
        public float ScaleOffsetX { get; set; }
        public float ScaleOffsetY { get; set; }
        public float ScaleOffsetZ { get; set; }
        public float ScaleOffsetW { get; set; }
        public int ReceiveGI { get; set; }
        public float ScaleInLightmap { get; set; }
    }

    public class LightmapGameObjectEntry
    {
        public string GoGuid { get; set; }
        public int StaticFlags { get; set; }
    }

    public class LightmapSyncPayload
    {
        public List<LightmapSlotEntry> Slots { get; set; }
        public List<LightmapRendererEntry> Renderers { get; set; }
        public List<LightmapGameObjectEntry> GameObjects { get; set; }
    }

    public static class Protocol
    {
        public const string ProtocolVersion = "1.0";
        public const int ChunkSize = 524288;

        public static NetworkMessage CreateMessage(MessageType type, string senderId, byte[] payload)
        {
            return new NetworkMessage
            {
                Type = type,
                SenderId = senderId,
                Timestamp = DateTime.UtcNow.Ticks,
                Payload = payload
            };
        }

        public static void WriteMessage(NetworkStream stream, NetworkMessage message)
        {
            string json = TeamCreateJson.Serialize(message);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] lenBytes = BitConverter.GetBytes(jsonBytes.Length);
            stream.Write(lenBytes, 0, 4);
            stream.Write(jsonBytes, 0, jsonBytes.Length);
        }

        public static NetworkMessage ReadMessage(NetworkStream stream)
        {
            byte[] lenBytes = ReadExact(stream, 4);
            int length = BitConverter.ToInt32(lenBytes, 0);
            if (length <= 0 || length > 256 * 1024 * 1024)
                throw new Exception($"[TeamCreate] Invalid message length: {length}");
            byte[] data = ReadExact(stream, length);
            string json = Encoding.UTF8.GetString(data);
            return TeamCreateJson.Deserialize<NetworkMessage>(json);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) throw new Exception("[TeamCreate] Connection closed unexpectedly.");
                offset += read;
            }
            return buffer;
        }

        public static byte[] Serialize<T>(T payload)
        {
            if (payload == null) return null;
            string json = TeamCreateJson.Serialize(payload);
            return Encoding.UTF8.GetBytes(json);
        }

        public static T Deserialize<T>(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return default;
            string json = Encoding.UTF8.GetString(payload);
            return TeamCreateJson.Deserialize<T>(json);
        }
    }
}
#endif
