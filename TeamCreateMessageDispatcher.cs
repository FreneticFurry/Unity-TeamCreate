#if UNITY_EDITOR
using UnityEngine;

namespace TeamCreate.Editor
{
    public static class TeamCreateMessageDispatcher
    {
        public static void Dispatch(NetworkMessage message)
        {
            TeamCreateLogger.LogRecv(message.Type.ToString(), message.SenderId);

            switch (message.Type)
            {
                case MessageType.GAMEOBJECT_CREATED:
                    TeamCreateHierarchySync.ApplyGameObjectCreated(message);
                    break;
                case MessageType.GAMEOBJECT_DELETED:
                    TeamCreateHierarchySync.ApplyGameObjectDeleted(message);
                    break;
                case MessageType.GAMEOBJECT_REPARENTED:
                    TeamCreateHierarchySync.ApplyGameObjectReparented(message);
                    break;
                case MessageType.GAMEOBJECT_RENAMED:
                    TeamCreateHierarchySync.ApplyGameObjectRenamed(message);
                    break;
                case MessageType.GAMEOBJECT_ACTIVATED:
                    TeamCreateHierarchySync.ApplyGameObjectActivated(message);
                    break;
                case MessageType.COMPONENT_ADDED:
                    TeamCreateLogger.Log($"COMPONENT_ADDED from {message.SenderId}, payload={message.Payload?.Length ?? -1} bytes");
                    TeamCreateHierarchySync.ApplyComponentAdded(message);
                    break;
                case MessageType.COMPONENT_REMOVED:
                    TeamCreateHierarchySync.ApplyComponentRemoved(message);
                    break;
                case MessageType.COMPONENT_PROPERTY_CHANGED:
                    TeamCreateHierarchySync.ApplyComponentPropertyChanged(message);
                    break;
                case MessageType.SCENE_OPENED:
                    TeamCreateSceneSync.ApplySceneOpened(message);
                    break;
                case MessageType.SCENE_CLOSED:
                    TeamCreateSceneSync.ApplySceneClosed(message);
                    break;
                case MessageType.SCENE_ACTIVE_CHANGED:
                    TeamCreateSceneSync.ApplySceneActiveChanged(message);
                    break;
                case MessageType.SCENE_RENAMED:
                    TeamCreateSceneSync.ApplySceneRenamed(message);
                    break;
                case MessageType.ASSET_CREATED:
                    TeamCreateAssetSync.ApplyAssetCreated(message);
                    break;
                case MessageType.ASSET_MODIFIED:
                    TeamCreateAssetSync.ApplyAssetModified(message);
                    break;
                case MessageType.ASSET_DELETED:
                    TeamCreateAssetSync.ApplyAssetDeleted(message);
                    break;
                case MessageType.ASSET_MOVED:
                    TeamCreateAssetSync.ApplyAssetMoved(message);
                    break;
                case MessageType.ASSET_CHUNK:
                    TeamCreateAssetSync.HandleChunkReceived(message);
                    break;
                case MessageType.ASSET_CHUNK_ACK:
                    TeamCreateAssetSync.HandleDeliveryAck(message);
                    break;
                case MessageType.PEER_TRANSFORM:
                    TeamCreatePeerManager.ApplyPeerTransform(message);
                    break;
                case MessageType.PEER_SELECTION:
                    TeamCreateSelectionSync.ApplyPeerSelection(message);
                    break;
                case MessageType.TAG_ADDED:
                    TeamCreateHierarchySync.ApplyTagAdded(message);
                    break;
                case MessageType.TAG_REMOVED:
                    TeamCreateHierarchySync.ApplyTagRemoved(message);
                    break;
                case MessageType.PING:
                    TeamCreatePeerManager.HandlePing(message);
                    break;
                case MessageType.PONG:
                    TeamCreatePeerManager.HandlePong(message);
                    break;
                case MessageType.ASSET_MANIFEST_SYNC:
                    TeamCreateAssetSync.HandleManifestSync(message);
                    break;
                case MessageType.MATERIAL_PROPERTY_CHANGED:
                    TeamCreateHierarchySync.ApplyMaterialPropertyChanged(message);
                    break;
                case MessageType.SCENE_VISIBILITY_CHANGED:
                    TeamCreateHierarchySync.ApplySceneVisibilityChanged(message);
                    break;
                case MessageType.HOST_RELOADING:
                    break;
                case MessageType.ANIM_TIME_SYNC:
                    break;
                case MessageType.LIGHTMAP_SYNC:
                    TeamCreateHierarchySync.ApplyLightmapSync(message);
                    break;
                case MessageType.LIGHTMAP_SYNC_REQUEST:
                    TeamCreateHierarchySync.HandleLightmapSyncRequest(message);
                    break;
                default:
                    Debug.LogWarning($"[TeamCreate] Unhandled message type: {message.Type}");
                    break;
            }
        }
    }
}
#endif
