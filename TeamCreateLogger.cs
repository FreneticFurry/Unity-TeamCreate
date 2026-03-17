#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public static class TeamCreateLogger
    {
        private static volatile bool _enabled;

        static TeamCreateLogger()
        {
            _enabled = EditorPrefs.GetBool("TeamCreate_LoggingEnabled", false);
        }

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                EditorPrefs.SetBool("TeamCreate_LoggingEnabled", value);
            }
        }

        public static void Log(string message)
        {
            if (_enabled) Debug.Log($"[TeamCreate] {message}");
        }

        public static void LogSend(string messageType, string target = "broadcast")
        {
            if (_enabled) Debug.Log($"[TeamCreate][SEND] {messageType} → {target}");
        }

        public static void LogRecv(string messageType, string senderId)
        {
            if (_enabled) Debug.Log($"[TeamCreate][RECV] {messageType} ← {senderId}");
        }

        public static void LogWarning(string message)
        {
            if (_enabled) Debug.LogWarning($"[TeamCreate] {message}");
        }
    }
}
#endif
