#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public static class TeamCreateInterpolator
    {
        private const string EditorPrefKey_Speed = "TeamCreate_InterpSpeed_v2";
        private const string EditorPrefKey_Enabled = "TeamCreate_InterpEnabled";
        private const double ExpirySeconds = 3.0;

        public static float SmoothSpeed
        {
            get => EditorPrefs.GetFloat(EditorPrefKey_Speed, 50f);
            set => EditorPrefs.SetFloat(EditorPrefKey_Speed, Mathf.Clamp(value, 1f, 50f));
        }

        public static bool InterpolationEnabled
        {
            get => EditorPrefs.GetBool(EditorPrefKey_Enabled, true);
            set => EditorPrefs.SetBool(EditorPrefKey_Enabled, value);
        }

        private class TransformState
        {
            public Vector3 CurrentPos;
            public Quaternion CurrentRot;
            public Vector3 CurrentScale;
            public Vector3 TargetPos;
            public Quaternion TargetRot;
            public Vector3 TargetScale;
            public bool HasTarget;
            public double LastUpdateTime;
        }

        private class PropState
        {
            public Component Comp;
            public string TypePrefix;
            public float[] Current;
            public float[] Target;
            public bool HasTarget;
            public double LastUpdateTime;
        }

        private static readonly Dictionary<string, TransformState> TransformStates = new Dictionary<string, TransformState>();
        private static readonly Dictionary<string, Dictionary<string, PropState>> PropStates = new Dictionary<string, Dictionary<string, PropState>>();
        private static double _prevTime;

        static TeamCreateInterpolator()
        {
            _prevTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += OnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        private static void OnBeforeReload()
        {
            EditorApplication.update -= OnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            Clear();
        }

        public static bool GetLastTransformTarget(string goGuid, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            scale = Vector3.one;
            if (!TransformStates.TryGetValue(goGuid, out var s) || !s.HasTarget) return false;
            pos = s.TargetPos;
            rot = s.TargetRot;
            scale = s.TargetScale;
            return true;
        }

        public static void SetTarget(string goGuid, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            if (!InterpolationEnabled)
            {
                var goImm = TeamCreateIdentity.FindByGuid(goGuid);
                if (goImm == null) return;
                TeamCreateSession.IsApplyingRemoteChange = true;
                try
                {
                    goImm.transform.localPosition = pos;
                    goImm.transform.localRotation = rot;
                    goImm.transform.localScale = scale;
                    EditorSceneManager.MarkSceneDirty(goImm.scene);
                }
                finally { TeamCreateSession.IsApplyingRemoteChange = false; }
                return;
            }

            if (!TransformStates.TryGetValue(goGuid, out var s))
            {
                s = new TransformState();
                TransformStates[goGuid] = s;
            }

            if (!s.HasTarget)
            {
                var go = TeamCreateIdentity.FindByGuid(goGuid);
                s.CurrentPos = go != null ? go.transform.localPosition : pos;
                s.CurrentRot = go != null ? go.transform.localRotation : rot;
                s.CurrentScale = go != null ? go.transform.localScale : scale;
                s.HasTarget = true;
            }

            s.TargetPos = pos;
            s.TargetRot = rot;
            s.TargetScale = scale;
            s.LastUpdateTime = EditorApplication.timeSinceStartup;
        }

        public static void SetPropertyTarget(Component comp, string componentGuid, string propertyPath, string serializedValue)
        {
            if (comp == null || string.IsNullOrEmpty(serializedValue)) return;

            int colon = serializedValue.IndexOf(':');
            if (colon < 0) return;
            string prefix = serializedValue.Substring(0, colon);
            string data = serializedValue.Substring(colon + 1);
            float[] values = ParseToFloats(prefix, data);
            if (values == null) return;

            if (!InterpolationEnabled)
            {
                TeamCreateSession.IsApplyingRemoteChange = true;
                try
                {
                    var soImm = new SerializedObject(comp);
                    var propImm = soImm.FindProperty(propertyPath);
                    if (propImm != null)
                    {
                        ApplyFloatsToProperty(propImm, prefix, values);
                        soImm.ApplyModifiedPropertiesWithoutUndo();
                        EditorSceneManager.MarkSceneDirty(comp.gameObject.scene);
                    }
                }
                finally { TeamCreateSession.IsApplyingRemoteChange = false; }
                return;
            }

            if (!PropStates.TryGetValue(componentGuid, out var dict))
            {
                dict = new Dictionary<string, PropState>();
                PropStates[componentGuid] = dict;
            }

            if (!dict.TryGetValue(propertyPath, out var ps))
            {
                ps = new PropState { Comp = comp, TypePrefix = prefix };
                dict[propertyPath] = ps;
            }

            if (!ps.HasTarget || ps.Current == null || ps.Current.Length != values.Length)
            {
                float[] initValues = null;
                try
                {
                    var soInit = new SerializedObject(comp);
                    var propInit = soInit.FindProperty(propertyPath);
                    if (propInit != null)
                    {
                        string curStr = TeamCreateSnapshotBuilder.SerializeProperty(propInit);
                        if (curStr != null)
                        {
                            int c2 = curStr.IndexOf(':');
                            if (c2 >= 0)
                                initValues = ParseToFloats(prefix, curStr.Substring(c2 + 1));
                        }
                    }
                }
                catch { }
                ps.Current = initValues ?? (float[])values.Clone();
                ps.HasTarget = true;
            }

            ps.Target = values;
            ps.LastUpdateTime = EditorApplication.timeSinceStartup;
        }

        public static bool IsInterpolatableValue(string serializedValue)
        {
            if (string.IsNullOrEmpty(serializedValue)) return false;
            return serializedValue.StartsWith("float:") ||
                   serializedValue.StartsWith("color:") ||
                   serializedValue.StartsWith("v2:") ||
                   serializedValue.StartsWith("v3:") ||
                   serializedValue.StartsWith("v4:") ||
                   serializedValue.StartsWith("quat:");
        }

        private static void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - _prevTime), 0.0001f, 0.1f);
            _prevTime = now;

            float t = 1f - Mathf.Exp(-SmoothSpeed * dt);
            bool repaint = false;

            if (TransformStates.Count > 0)
            {
                var dead = new List<string>();
                TeamCreateSession.IsApplyingRemoteChange = true;
                try
                {
                    foreach (var kvp in TransformStates)
                    {
                        var s = kvp.Value;
                        if (!s.HasTarget) continue;
                        if (now - s.LastUpdateTime > ExpirySeconds) { dead.Add(kvp.Key); continue; }

                        s.CurrentPos = Vector3.Lerp(s.CurrentPos, s.TargetPos, t);
                        s.CurrentRot = Quaternion.Slerp(s.CurrentRot, s.TargetRot, t);
                        s.CurrentScale = Vector3.Lerp(s.CurrentScale, s.TargetScale, t);

                        var go = TeamCreateIdentity.FindByGuid(kvp.Key);
                        if (go == null) { dead.Add(kvp.Key); continue; }

                        go.transform.localPosition = s.CurrentPos;
                        go.transform.localRotation = s.CurrentRot;
                        go.transform.localScale = s.CurrentScale;
                        repaint = true;
                    }
                }
                finally { TeamCreateSession.IsApplyingRemoteChange = false; }
                foreach (var k in dead) TransformStates.Remove(k);
            }

            if (PropStates.Count > 0)
            {
                var deadComps = new List<string>();
                TeamCreateSession.IsApplyingRemoteChange = true;
                try
                {
                    foreach (var compKvp in PropStates)
                    {
                        var deadProps = new List<string>();
                        Component comp = null;
                        SerializedObject so = null;
                        bool changed = false;

                        foreach (var propKvp in compKvp.Value)
                        {
                            var ps = propKvp.Value;
                            if (!ps.HasTarget || ps.Target == null) continue;
                            if (now - ps.LastUpdateTime > ExpirySeconds) { deadProps.Add(propKvp.Key); continue; }

                            SmoothFloats(ps.Current, ps.Target, t, ps.TypePrefix);

                            if (comp == null)
                            {
                                comp = ps.Comp;
                                if (comp == null) { deadProps.Add(propKvp.Key); continue; }
                                so = new SerializedObject(comp);
                            }

                            var prop = so.FindProperty(propKvp.Key);
                            if (prop != null) { ApplyFloatsToProperty(prop, ps.TypePrefix, ps.Current); changed = true; }
                        }

                        foreach (var k in deadProps) compKvp.Value.Remove(k);

                        if (changed && so != null)
                        {
                            so.ApplyModifiedPropertiesWithoutUndo();
                            if (comp != null) EditorSceneManager.MarkSceneDirty(comp.gameObject.scene);
                            repaint = true;
                        }

                        if (compKvp.Value.Count == 0) deadComps.Add(compKvp.Key);
                    }
                }
                finally { TeamCreateSession.IsApplyingRemoteChange = false; }
                foreach (var k in deadComps) PropStates.Remove(k);
            }

            if (repaint) SceneView.RepaintAll();
        }

        private static void SmoothFloats(float[] current, float[] target, float t, string prefix)
        {
            if (current == null || target == null || current.Length != target.Length) return;
            if (prefix == "quat" && current.Length == 4)
            {
                var qr = Quaternion.Slerp(
                    new Quaternion(current[0], current[1], current[2], current[3]),
                    new Quaternion(target[0], target[1], target[2], target[3]), t);
                current[0] = qr.x; current[1] = qr.y; current[2] = qr.z; current[3] = qr.w;
                return;
            }
            for (int i = 0; i < current.Length; i++)
                current[i] = Mathf.Lerp(current[i], target[i], t);
        }

        private static float[] ParseToFloats(string prefix, string data)
        {
            switch (prefix)
            {
                case "float":
                    {
                        if (TryF(data, out float f)) return new[] { f };
                        break;
                    }
                case "color":
                    {
                        var p = data.Split(',');
                        if (p.Length == 4 && TryF(p[0], out float r) && TryF(p[1], out float g) && TryF(p[2], out float b) && TryF(p[3], out float a))
                            return new[] { r, g, b, a };
                        break;
                    }
                case "v2":
                    {
                        var p = data.Split(',');
                        if (p.Length == 2 && TryF(p[0], out float x) && TryF(p[1], out float y))
                            return new[] { x, y };
                        break;
                    }
                case "v3":
                    {
                        var p = data.Split(',');
                        if (p.Length == 3 && TryF(p[0], out float x) && TryF(p[1], out float y) && TryF(p[2], out float z))
                            return new[] { x, y, z };
                        break;
                    }
                case "v4":
                    {
                        var p = data.Split(',');
                        if (p.Length == 4 && TryF(p[0], out float x) && TryF(p[1], out float y) && TryF(p[2], out float z) && TryF(p[3], out float w))
                            return new[] { x, y, z, w };
                        break;
                    }
                case "quat":
                    {
                        var p = data.Split(',');
                        if (p.Length == 4 && TryF(p[0], out float x) && TryF(p[1], out float y) && TryF(p[2], out float z) && TryF(p[3], out float w))
                            return new[] { x, y, z, w };
                        break;
                    }
            }
            return null;
        }

        private static void ApplyFloatsToProperty(SerializedProperty prop, string prefix, float[] values)
        {
            switch (prefix)
            {
                case "float":
                    if (values.Length >= 1) prop.floatValue = values[0];
                    break;
                case "color":
                    if (values.Length >= 4) prop.colorValue = new Color(values[0], values[1], values[2], values[3]);
                    break;
                case "v2":
                    if (values.Length >= 2) prop.vector2Value = new Vector2(values[0], values[1]);
                    break;
                case "v3":
                    if (values.Length >= 3) prop.vector3Value = new Vector3(values[0], values[1], values[2]);
                    break;
                case "v4":
                    if (values.Length >= 4) prop.vector4Value = new Vector4(values[0], values[1], values[2], values[3]);
                    break;
                case "quat":
                    if (values.Length >= 4) prop.quaternionValue = new Quaternion(values[0], values[1], values[2], values[3]);
                    break;
            }
        }

        private static bool TryF(string s, out float result) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        public static void Clear()
        {
            TransformStates.Clear();
            PropStates.Clear();
        }
    }
}
#endif
