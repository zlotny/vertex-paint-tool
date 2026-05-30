using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Component that enables vertex painting on MeshRenderer or SkinnedMeshRenderer objects.
///
/// Design:
/// - originalMesh (public): the mesh from the renderer, stolen on Awake.
/// - The renderer always shows an in-memory patched copy (asset on disk untouched).
/// - paintData (ScriptableObject): the ONLY new file on disk. Stores vertex color overrides.
/// - Painting updates both the patched mesh (realtime) AND paintData (auto-save).
/// - No save/load buttons. Drag in paintData = auto-rebuilds from it. Paint = auto-saves to it.
/// </summary>
[DisallowMultipleComponent]
[ExecuteAlways]
public class VertexPaintTool : MonoBehaviour
{
    public VertexPaintData paintData;

    public Mesh originalMesh;

    public Color paintColor = Color.red;
    [Range(0.001f, 5f)]
    public float brushSize = 0.5f;
    [Range(0f, 1f)]
    public float brushOpacity = 1f;

    public enum BrushType { Hard, Linear, Soft, Gaussian }
    public BrushType brushType = BrushType.Hard;

    [Header("Preview")]
    public bool showVertexColors = false;

    // Internal: which renderer type
    private MeshRenderer _meshRenderer;
    private SkinnedMeshRenderer _skinnedRenderer;
    private MeshFilter _meshFilter;
    private Mesh _patchedMesh;

    // Cached materials for preview restore (supports multi-material renderers).
    // SerializeField so it survives domain reloads when preview is active.
    [SerializeField] private Material[] _previewRestoreMaterials;

    // Debug material (never serialized)
    [System.NonSerialized] public Material debugMaterial;

    // Public accessor for which renderer is being used.
    // Always re-fetches to avoid Unity fake-null issues.
    public bool HasPatchedMesh
    {
        get
        {
            if (_patchedMesh == null) return false;
            var mf  = GetComponent<MeshFilter>();
            if (mf  != null) return mf.sharedMesh == _patchedMesh;
            var smr = GetComponent<SkinnedMeshRenderer>();
            if (smr != null) return smr.sharedMesh == _patchedMesh;
            return false;
        }
    }

    public Renderer Renderer
    {
        get
        {
            var mr = GetComponent<MeshRenderer>();
            if (mr != null) return (_meshRenderer = mr);
            var smr = GetComponent<SkinnedMeshRenderer>();
            if (smr != null) return (_skinnedRenderer = smr);
            _meshRenderer = null;
            _skinnedRenderer = null;
            return null;
        }
    }

    private void Awake()
    {
        _meshRenderer = GetComponent<MeshRenderer>();
        _skinnedRenderer = GetComponent<SkinnedMeshRenderer>();
        _meshFilter = GetComponent<MeshFilter>();

        if (_meshRenderer == null && _skinnedRenderer == null)
        {
            Debug.LogError("[VertexPaint] No MeshRenderer or SkinnedMeshRenderer found on " + gameObject.name);
            return;
        }

        if (originalMesh == null)
        {
            if (_meshFilter != null)
                originalMesh = _meshFilter.sharedMesh;
            else if (_skinnedRenderer != null)
                originalMesh = _skinnedRenderer.sharedMesh;
        }
    }

    private void OnEnable()
    {
        _meshRenderer = GetComponent<MeshRenderer>();
        _skinnedRenderer = GetComponent<SkinnedMeshRenderer>();
        _meshFilter = GetComponent<MeshFilter>();
        RebuildPatchedMesh();
        ApplyPreviewState();

#if UNITY_EDITOR
        UnityEditor.Undo.undoRedoPerformed += OnUndoRedo;
#endif
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.undoRedoPerformed -= OnUndoRedo;
#endif
    }

    private void OnUndoRedo()
    {
        if (paintData == null) return;

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(paintData);

        // Force paintData to rebuild its runtime dictionary from serialized lists
        // (undo/redo only restores the serialized fields)
        paintData.OnAfterDeserialize();

        // Fresh fetch of everything
        var mf = GetComponent<MeshFilter>();
        var smr = GetComponent<SkinnedMeshRenderer>();

        if (originalMesh == null) return;

        // Create fresh patched mesh
        var freshMesh = Instantiate(originalMesh);
        freshMesh.name = originalMesh.name + " (Patched)";

        // Use the original mesh's colors as baseline, then layer paint overrides on top.
        var colors = freshMesh.colors;
        if (colors == null || colors.Length != freshMesh.vertexCount)
        {
            colors = new Color[freshMesh.vertexCount];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        }
        if (paintData != null)
        {
            foreach (var kvp in paintData.GetAllOverrides())
                if (kvp.Key >= 0 && kvp.Key < colors.Length)
                    colors[kvp.Key] = kvp.Value;
        }
        freshMesh.colors = colors;

        // Assign to renderer
        if (mf != null) mf.sharedMesh = freshMesh;
        if (smr != null) smr.sharedMesh = freshMesh;

        // Destroy old patched mesh
        if (_patchedMesh != null)
        {
            if (!Application.isPlaying)
                DestroyImmediate(_patchedMesh);
            else
                Destroy(_patchedMesh);
        }
        _patchedMesh = freshMesh;

        // Re-apply preview state
        _meshRenderer = mf != null ? mf.GetComponent<MeshRenderer>() : null;
        _skinnedRenderer = smr;
        _meshFilter = mf;
        ApplyPreviewState();

        if (UnityEditor.SceneView.lastActiveSceneView != null)
            UnityEditor.SceneView.lastActiveSceneView.Repaint();
#endif
    }

    private void OnDestroy()
    {
        if (_patchedMesh != null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(_patchedMesh);
            else
                Destroy(_patchedMesh);
#else
            Destroy(_patchedMesh);
#endif
            _patchedMesh = null;
        }
    }

    /// <summary>
    /// Raycast against this object's patched mesh directly (no collider needed).
    /// </summary>
    public bool RaycastPatchedMesh(Ray ray, out Vector3 hitPoint, out Vector3 hitNormal, float maxDistance = 200f)
    {
        hitPoint = Vector3.zero;
        hitNormal = Vector3.zero;

        Mesh mesh = _patchedMesh;
        if (mesh == null) mesh = originalMesh;
        if (mesh == null) return false;

        Matrix4x4 localToWorld = transform.localToWorldMatrix;
        Matrix4x4 worldToLocal = transform.worldToLocalMatrix;
        Ray localRay = new Ray(
            worldToLocal.MultiplyPoint3x4(ray.origin),
            worldToLocal.MultiplyVector(ray.direction).normalized
        );

        Vector3[] verts = mesh.vertices;
        int[] tris = mesh.triangles;

        float bestDist = float.MaxValue;
        bool found = false;

        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector3 a = verts[tris[i]];
            Vector3 b = verts[tris[i + 1]];
            Vector3 c = verts[tris[i + 2]];

            float t;
            Vector3 bary;
            if (!RayTriangleIntersect(localRay, a, b, c, out t, out bary))
                continue;

            if (t < 0 || t > maxDistance) continue;
            if (t >= bestDist) continue;

            bestDist = t;
            found = true;

            Vector3 n0 = mesh.normals[tris[i]];
            Vector3 n1 = mesh.normals[tris[i + 1]];
            Vector3 n2 = mesh.normals[tris[i + 2]];
            Vector3 localNormal = (n0 * bary.x + n1 * bary.y + n2 * bary.z).normalized;

            hitPoint = localToWorld.MultiplyPoint3x4(localRay.origin + localRay.direction * t);
            hitNormal = transform.TransformDirection(localNormal).normalized;
        }

        return found;
    }

    private static bool RayTriangleIntersect(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float t, out Vector3 bary)
    {
        t = 0;
        bary = Vector3.zero;

        Vector3 e1 = b - a;
        Vector3 e2 = c - a;
        Vector3 p = Vector3.Cross(ray.direction, e2);
        float det = Vector3.Dot(e1, p);
        if (Mathf.Abs(det) < 1e-10f) return false;
        float invDet = 1f / det;

        Vector3 s = ray.origin - a;
        float u = Vector3.Dot(s, p) * invDet;
        if (u < 0f || u > 1f) return false;

        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(ray.direction, q) * invDet;
        if (v < 0f || u + v > 1f) return false;

        t = Vector3.Dot(e2, q) * invDet;
        bary = new Vector3(1f - u - v, u, v);
        return true;
    }

    /// <summary>
    /// Build an in-memory patched copy of the original mesh and assign to the renderer.
    /// </summary>
    public void RebuildPatchedMesh()
    {
        if (originalMesh == null) return;

        if (_patchedMesh != null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(_patchedMesh);
            else
                Destroy(_patchedMesh);
#else
            Destroy(_patchedMesh);
#endif
        }

        _patchedMesh = Instantiate(originalMesh);
        _patchedMesh.name = originalMesh.name + " (Patched)";

        ApplyPaintDataToMesh(_patchedMesh);

        // Always re-fetch to survive domain reload / fake-null
        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
        _skinnedRenderer = GetComponent<SkinnedMeshRenderer>();

        if (_meshFilter != null)
            _meshFilter.sharedMesh = _patchedMesh;
        if (_skinnedRenderer != null)
            _skinnedRenderer.sharedMesh = _patchedMesh;
    }

    private void ApplyPaintDataToMesh(Mesh mesh)
    {
        if (mesh == null) return;

        int vertCount = mesh.vertexCount;

        // Use the mesh's existing vertex colors as the baseline (preserves colors baked in
        // Blender or other DCC tools). Fall back to white only when there are no colors at all.
        Color[] colors = mesh.colors;
        if (colors == null || colors.Length != vertCount)
        {
            colors = new Color[vertCount];
            for (int i = 0; i < vertCount; i++)
                colors[i] = Color.white;
        }

        if (paintData != null)
        {
            foreach (var kvp in paintData.GetAllOverrides())
            {
                if (kvp.Key >= 0 && kvp.Key < vertCount)
                    colors[kvp.Key] = kvp.Value;
            }
        }

        mesh.colors = colors;
    }

    /// <summary>
    /// Paint every vertex on the mesh with the current paintColor.
    /// </summary>
    public void FillMesh()
    {
        if (_patchedMesh == null) return;

        int vertCount = _patchedMesh.vertexCount;
        Color[] colors = new Color[vertCount];
        for (int i = 0; i < vertCount; i++)
            colors[i] = paintColor;
        _patchedMesh.colors = colors;

        if (paintData != null)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCompleteObjectUndo(paintData, "Fill Mesh");
#endif
            paintData.Clear();

            Color[] originalColors = originalMesh != null ? originalMesh.colors : null;
            bool hasOriginal = originalColors != null && originalColors.Length == vertCount;

            for (int i = 0; i < vertCount; i++)
            {
                Color baseline = hasOriginal ? originalColors[i] : Color.white;
                if (paintColor != baseline)
                    paintData.SetVertexColor(i, paintColor);
            }
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(paintData);
#endif
        }
    }

    /// <summary>
    /// Erase at a world-space position by blending each vertex back toward its original mesh color.
    /// </summary>
    public void EraseAt(Vector3 worldPosition)
    {
        if (_patchedMesh == null) return;

        Color[] originalColors = originalMesh != null ? originalMesh.colors : null;
        bool hasOriginal = originalColors != null && originalColors.Length == _patchedMesh.vertexCount;

        Color[] colors = _patchedMesh.colors;
        if (colors == null || colors.Length != _patchedMesh.vertexCount)
        {
            colors = new Color[_patchedMesh.vertexCount];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = hasOriginal ? originalColors[i] : Color.white;
        }

        Vector3[] verts = _patchedMesh.vertices;
        float brushRadius = brushSize * 0.5f;
        var changed = new List<int>();

        for (int i = 0; i < verts.Length; i++)
        {
            float dist = Vector3.Distance(transform.TransformPoint(verts[i]), worldPosition);
            if (dist > brushRadius) continue;

            float t = Mathf.Clamp01(dist / brushRadius);
            float influence;
            if      (brushType == BrushType.Hard)     influence = t < 1f ? 1f : 0f;
            else if (brushType == BrushType.Linear)   influence = 1f - t;
            else if (brushType == BrushType.Soft)     influence = (1f - t) * (1f - t);
            else /* Gaussian */                       influence = Mathf.Exp(-4f * t * t);

            if (influence <= 0f) continue;

            Color target = hasOriginal ? originalColors[i] : Color.white;
            colors[i] = Color.Lerp(colors[i], target, brushOpacity * influence);
            changed.Add(i);
        }

        if (changed.Count == 0) return;

        _patchedMesh.colors = colors;

        if (paintData != null)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCompleteObjectUndo(paintData, "Erase Stroke");
#endif
            foreach (int idx in changed)
            {
                Color target = hasOriginal ? originalColors[idx] : Color.white;
                if (colors[idx] == target)
                    paintData.RemoveVertexColor(idx);
                else
                    paintData.SetVertexColor(idx, colors[idx]);
            }
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(paintData);
#endif
        }
    }

    /// <summary>
    /// Paint at a world-space position. Updates the patched mesh AND paintData immediately.
    /// </summary>
    public void PaintAt(Vector3 worldPosition)
    {
        if (_patchedMesh == null) return;

        Color[] colors = _patchedMesh.colors;
        if (colors == null || colors.Length != _patchedMesh.vertexCount)
        {
            colors = new Color[_patchedMesh.vertexCount];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = Color.white;
        }

        Vector3[] verts = _patchedMesh.vertices;
        float brushRadius = brushSize * 0.5f;
        var changed = new List<int>();

        for (int i = 0; i < verts.Length; i++)
        {
            float dist = Vector3.Distance(transform.TransformPoint(verts[i]), worldPosition);
            if (dist > brushRadius) continue;

            float t = Mathf.Clamp01(dist / brushRadius);
            float influence;
            if      (brushType == BrushType.Hard)     influence = t < 1f ? 1f : 0f;
            else if (brushType == BrushType.Linear)   influence = 1f - t;
            else if (brushType == BrushType.Soft)     influence = (1f - t) * (1f - t);
            else /* Gaussian */                       influence = Mathf.Exp(-4f * t * t);

            if (influence <= 0f) continue;

            colors[i] = Color.Lerp(colors[i], paintColor, brushOpacity * influence);
            changed.Add(i);
        }

        if (changed.Count == 0) return;

        _patchedMesh.colors = colors;

        if (paintData != null)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCompleteObjectUndo(paintData, "Paint Stroke");
#endif
            Color[] originalColors = originalMesh != null ? originalMesh.colors : null;
            bool hasOriginal = originalColors != null && originalColors.Length == _patchedMesh.vertexCount;

            foreach (int idx in changed)
            {
                Color baseline = hasOriginal ? originalColors[idx] : Color.white;
                if (colors[idx] == baseline)
                    paintData.RemoveVertexColor(idx);
                else
                    paintData.SetVertexColor(idx, colors[idx]);
            }
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(paintData);
#endif
        }
    }

    /// <summary>
    /// Toggle between vertex-color debug view and original material(s).
    /// Saves and restores the full materials array (supports multi-material renderers).
    /// </summary>
    public void ApplyPreviewState()
    {
        // Always re-fetch to avoid Unity's fake-null
        Renderer r = Renderer;
        if (r == null) return;

        if (showVertexColors)
        {
            Material debug = GetDebugMaterial();
            if (debug == null) return;

            // Save originals only once. _previewRestoreMaterials is [SerializeField] so it
            // survives domain reloads — if it's already populated, we already saved them.
            if (_previewRestoreMaterials == null)
                _previewRestoreMaterials = r.sharedMaterials;

            var mats = new Material[_previewRestoreMaterials.Length];
            for (int i = 0; i < mats.Length; i++)
                mats[i] = debug;

            r.sharedMaterials = mats;
        }
        else
        {
            if (_previewRestoreMaterials != null && _previewRestoreMaterials.Length > 0)
                r.sharedMaterials = _previewRestoreMaterials;

            _previewRestoreMaterials = null;
        }
    }

    public Material GetDebugMaterial()
    {
        if (debugMaterial == null)
        {
            Shader shader = Shader.Find("Samhain/VertexColorDebug");
            if (shader != null)
            {
                debugMaterial = new Material(shader);
                debugMaterial.name = "VertexColorDebug (Auto)";
            }
        }
        return debugMaterial;
    }

    private void Reset()
    {
        _meshRenderer = GetComponent<MeshRenderer>();
        _skinnedRenderer = GetComponent<SkinnedMeshRenderer>();
        _meshFilter = GetComponent<MeshFilter>();

        if (originalMesh == null)
        {
            if (_meshFilter != null)
                originalMesh = _meshFilter.sharedMesh;
            else if (_skinnedRenderer != null)
                originalMesh = _skinnedRenderer.sharedMesh;
        }

#if UNITY_EDITOR
        AutoCreatePaintData();
#endif
    }

#if UNITY_EDITOR
    private void AutoCreatePaintData()
    {
        if (paintData != null) return;

        string folder = "Assets/VertexPaintData";
        if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
            UnityEditor.AssetDatabase.CreateFolder("Assets", "VertexPaintData");

        string path = $"{folder}/{gameObject.name}_VertexPaintData.asset";
        path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(path);

        var data = ScriptableObject.CreateInstance<VertexPaintData>();
        UnityEditor.AssetDatabase.CreateAsset(data, path);
        UnityEditor.AssetDatabase.SaveAssets();

        paintData = UnityEditor.AssetDatabase.LoadAssetAtPath<VertexPaintData>(path);
        UnityEditor.EditorUtility.SetDirty(this);
    }
#endif

    private void OnValidate()
    {
#if UNITY_EDITOR
        if (Application.isPlaying) return;
        if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
        if (_skinnedRenderer == null) _skinnedRenderer = GetComponent<SkinnedMeshRenderer>();
        if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();

        if (originalMesh == null)
        {
            if (_meshFilter != null)
                originalMesh = _meshFilter.sharedMesh;
            else if (_skinnedRenderer != null)
                originalMesh = _skinnedRenderer.sharedMesh;
        }

        Mesh src = _meshFilter != null ? _meshFilter.sharedMesh : (_skinnedRenderer != null ? _skinnedRenderer.sharedMesh : null);
        if (src != null && originalMesh != null)
        {
            UnityEditor.EditorApplication.delayCall -= DelayedRebuild;
            UnityEditor.EditorApplication.delayCall += DelayedRebuild;
        }
#endif
    }

#if UNITY_EDITOR
    private void DelayedRebuild()
    {
        if (this == null) return;
        RebuildPatchedMesh();
        ApplyPreviewState();
    }
#endif
}
