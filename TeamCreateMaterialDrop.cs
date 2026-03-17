#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace TeamCreate.Editor
{
    [InitializeOnLoad]
    public static class TeamCreateMaterialDrop
    {
        private static Renderer   _hoverRenderer;
        private static int        _hoverSlot;
        private static Material   _hoverMaterial;
        private static Vector2    _hoverMousePos;

        private static GUIStyle _labelStyle;
        private static GUIStyle LabelStyle
        {
            get
            {
                if (_labelStyle == null)
                    _labelStyle = new GUIStyle(EditorStyles.helpBox) { fontSize = 11 };
                return _labelStyle;
            }
        }

        static TeamCreateMaterialDrop()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                SceneView.duringSceneGui -= OnSceneGUI;
                ClearHover();
            };
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!TeamCreateSession.IsConnected)
            {
                if (_hoverRenderer != null) { ClearHover(); SceneView.RepaintAll(); }
                return;
            }

            var evt = Event.current;
            if (evt == null) return;

            if (evt.type == EventType.Repaint)
            {
                if (_hoverRenderer != null && _hoverMaterial != null)
                    DrawHoverLabel(_hoverMousePos, _hoverRenderer, _hoverSlot, _hoverMaterial);
                return;
            }

            var type = evt.type;
            if (type != EventType.DragUpdated &&
                type != EventType.DragPerform &&
                type != EventType.DragExited)
                return;

            Material draggedMat = GetDraggedMaterial();
            if (draggedMat == null)
            {
                if (_hoverRenderer != null) { ClearHover(); SceneView.RepaintAll(); }
                return;
            }

            if (type == EventType.DragExited)
            {
                ClearHover();
                SceneView.RepaintAll();
                return;
            }

            GameObject picked = HandleUtility.PickGameObject(evt.mousePosition, false);
            Renderer rend = picked != null ? picked.GetComponentInParent<Renderer>() : null;

            if (rend == null)
            {
                ClearHover();
                DragAndDrop.visualMode = DragAndDropVisualMode.None;
                return;
            }

            Ray worldRay = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
            int slot = FindMaterialSlot(rend, worldRay);

            _hoverRenderer = rend;
            _hoverSlot     = slot;
            _hoverMaterial = draggedMat;
            _hoverMousePos = evt.mousePosition;

            if (type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                SceneView.RepaintAll();
                evt.Use();
            }
            else
            {
                DragAndDrop.AcceptDrag();
                ApplyMaterial(rend, slot, draggedMat);
                ClearHover();
                SceneView.RepaintAll();
                evt.Use();
            }
        }

        private static void DrawHoverLabel(Vector2 mousePos, Renderer rend, int slot, Material newMat)
        {
            var mats = rend.sharedMaterials;
            string current = (slot < mats.Length && mats[slot] != null) ? mats[slot].name : "None";
            string text    = $"Slot {slot}  ·  {current}  →  {newMat.name}";

            Handles.BeginGUI();
            var content = new GUIContent(text);
            Vector2 size = LabelStyle.CalcSize(content);
            GUI.Label(new Rect(mousePos.x + 20f, mousePos.y + 4f, size.x + 6f, size.y + 2f),
                      content, LabelStyle);
            Handles.EndGUI();
        }

        private static void ApplyMaterial(Renderer rend, int slot, Material mat)
        {
            Undo.RecordObject(rend, "Apply Material");

            var mats = rend.sharedMaterials;
            if (slot >= 0 && slot < mats.Length)
            {
                mats[slot] = mat;
                rend.sharedMaterials = mats;
            }
            EditorUtility.SetDirty(rend);
        }

        private static void ClearHover()
        {
            _hoverRenderer = null;
            _hoverMaterial = null;
        }

        private static Material GetDraggedMaterial()
        {
            if (DragAndDrop.objectReferences == null) return null;
            foreach (var obj in DragAndDrop.objectReferences)
                if (obj is Material mat) return mat;
            return null;
        }

        private static int FindMaterialSlot(Renderer rend, Ray worldRay)
        {
            int matCount = rend.sharedMaterials?.Length ?? 1;
            if (matCount <= 1) return 0;

            Mesh mesh = null;
            if (rend is SkinnedMeshRenderer smr)
                mesh = smr.sharedMesh;
            else
            {
                var mf = rend.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }

            if (mesh == null || mesh.subMeshCount <= 1) return 0;

            Matrix4x4 w2l  = rend.transform.worldToLocalMatrix;
            Vector3   lOrig = w2l.MultiplyPoint3x4(worldRay.origin);
            Vector3   lDir  = w2l.MultiplyVector(worldRay.direction);

            Vector3[] verts     = mesh.vertices;
            float     closestT  = float.MaxValue;
            int       hitSub    = 0;

            for (int s = 0; s < mesh.subMeshCount && s < matCount; s++)
            {
                int[] tris = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    if (MollerTrumbore(lOrig, lDir,
                            verts[tris[i]], verts[tris[i + 1]], verts[tris[i + 2]],
                            out float t) && t >= 0f && t < closestT)
                    {
                        closestT = t;
                        hitSub   = s;
                    }
                }
            }

            return hitSub;
        }

        private static bool MollerTrumbore(
            Vector3 orig, Vector3 dir,
            Vector3 v0, Vector3 v1, Vector3 v2,
            out float t)
        {
            t = 0f;
            const float EPS = 1e-7f;

            Vector3 e1 = v1 - v0;
            Vector3 e2 = v2 - v0;
            Vector3 h  = Vector3.Cross(dir, e2);
            float   a  = Vector3.Dot(e1, h);
            if (a > -EPS && a < EPS) return false;

            float   f  = 1f / a;
            Vector3 sv = orig - v0;
            float   u  = f * Vector3.Dot(sv, h);
            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(sv, e1);
            float   v = f * Vector3.Dot(dir, q);
            if (v < 0f || u + v > 1f) return false;

            t = f * Vector3.Dot(e2, q);
            return true;
        }
    }
}
#endif
