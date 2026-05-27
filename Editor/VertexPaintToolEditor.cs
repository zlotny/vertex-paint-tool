using UnityEngine;
using UnityEditor;
using UnityEditor.EditorTools;

[EditorTool("Vertex Paint Tool", typeof(VertexPaintTool))]
public class VertexPaintToolEditor : EditorTool
{
    private VertexPaintTool _cachedTarget;
    private bool _cachedHover;
    private Vector3 _cachedPoint, _cachedNormal;
    private Vector2 _prevMouse;
    private Vector2 _lastRepaintMouse;

    // Overlay window state
    private static Rect     _overlayRect;
    private static bool     _overlayPosInit;
    private static GUIStyle _overlayStyle;
    private SceneView        _currentSV;
    private VertexPaintTool  _currentVPT;

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        EditorApplication.update += ForceRepaint;
        AssemblyReloadEvents.beforeAssemblyReload += ClearTextureCache;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        EditorApplication.update -= ForceRepaint;
        AssemblyReloadEvents.beforeAssemblyReload -= ClearTextureCache;
        _cachedTarget = null;
    }

    private static void ClearTextureCache()
    {
        if (_brushIcons != null)    { foreach (var t in _brushIcons)    if (t) DestroyImmediate(t); _brushIcons    = null; }
        if (_presetSwatches != null){ foreach (var t in _presetSwatches) if (t) DestroyImmediate(t); _presetSwatches = null; }
        _swatchStyles = null;
        _hintStyle    = null;
        if (_overlayStyle?.normal.background != null) DestroyImmediate(_overlayStyle.normal.background);
        _overlayStyle = null;
    }

    /// <summary>
    /// Force SceneView to repaint while tool + target are active,
    /// so idle mouse hover fires events and the brush stays visible.
    /// </summary>
    private void ForceRepaint()
    {
        if (ToolManager.activeToolType != typeof(VertexPaintToolEditor)) return;
        if (_cachedTarget == null || !_cachedTarget.isActiveAndEnabled) return;
        // Only repaint when the mouse has actually moved — no need to hammer every frame.
        if (_prevMouse == _lastRepaintMouse) return;
        _lastRepaintMouse = _prevMouse;
        SceneView.RepaintAll();
    }

    public override void OnToolGUI(EditorWindow window) { }

    private VertexPaintTool GetTarget()
    {
        if (ToolManager.activeToolType != typeof(VertexPaintToolEditor)) return null;
        if (_cachedTarget == null || _cachedTarget.gameObject != Selection.activeGameObject)
            _cachedTarget = Selection.activeGameObject?.GetComponent<VertexPaintTool>();
        return _cachedTarget;
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        VertexPaintTool vpt = GetTarget();
        if (vpt == null || !vpt.isActiveAndEnabled) return;

        // Claim the default control so Unity's built-in object picking never fires
        // while this tool is active. Camera navigation (Alt+drag) is unaffected.
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        Event e = Event.current;

        // Cache mouse on non-Repaint so we can use it during Repaint
        if (e.type != EventType.Repaint && e.type != EventType.Layout)
            _prevMouse = e.mousePosition;

        // Use cached mouse during Repaint (e.mousePosition is (0,0) on Repaint)
        Vector2 mouse = (e.type == EventType.Repaint) ? _prevMouse : e.mousePosition;

        Ray ray = HandleUtility.GUIPointToWorldRay(mouse);
        Vector3 hp, hn;
        _cachedHover = vpt.RaycastPatchedMesh(ray, out hp, out hn);
        if (_cachedHover) { _cachedPoint = hp; _cachedNormal = hn; }

        if (e.type == EventType.Repaint && _cachedHover)
            DrawBrush(vpt, _cachedPoint, _cachedNormal);

        DrawOverlay(sceneView, vpt);

        bool altHeld       = (e.modifiers & EventModifiers.Alt)   != 0;
        bool shiftHeld     = (e.modifiers & EventModifiers.Shift) != 0;
        bool mouseOverPanel = _overlayRect.Contains(e.mousePosition);

        if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0 && _cachedHover && !altHeld && !mouseOverPanel)
        {
            if (shiftHeld)
                vpt.EraseAt(_cachedPoint);
            else
                vpt.PaintAt(_cachedPoint);
            e.Use();
        }

        // Shift+scroll wheel — resize brush (multiplicative so it matches the log slider feel)
        if (e.type == EventType.ScrollWheel && shiftHeld)
        {
            vpt.brushSize = Mathf.Clamp(vpt.brushSize * Mathf.Pow(1.1f, -e.delta.y), 0.001f, 5f);
            e.Use();
        }

        if (e.type == EventType.KeyDown)
        {
            // [ / ] — resize brush
            if (e.keyCode == KeyCode.LeftBracket)
            {
                vpt.brushSize = Mathf.Clamp(vpt.brushSize * 0.9f, 0.001f, 5f);
                e.Use();
            }
            else if (e.keyCode == KeyCode.RightBracket)
            {
                vpt.brushSize = Mathf.Clamp(vpt.brushSize * 1.1f, 0.001f, 5f);
                e.Use();
            }
            else if (shiftHeld)
            {
                if (e.keyCode == KeyCode.C)
                {
                    if (vpt.paintData != null) UnityEditor.Undo.RegisterCompleteObjectUndo(vpt.paintData, "Clear All Paint");
                    vpt.paintData?.Clear();
                    vpt.RebuildPatchedMesh();
                    e.Use();
                }
                else if (e.keyCode == KeyCode.P)
                {
                    vpt.showVertexColors = !vpt.showVertexColors;
                    vpt.ApplyPreviewState();
                    SceneView.RepaintAll();
                    e.Use();
                }
            }
        }
    }

    private void DrawBrush(VertexPaintTool vpt, Vector3 point, Vector3 normal)
    {
        float r = vpt.brushSize * 0.5f;
        Handles.color = new Color(1f, 1f, 1f, 0.85f);
        Handles.DrawWireDisc(point, normal, r);
        Color f = vpt.paintColor; f.a = vpt.brushType == VertexPaintTool.BrushType.Hard ? 0.22f : 0.12f;
        Handles.color = f;
        Handles.DrawSolidDisc(point, normal, r);
        Handles.color = Color.white;
        float cs = r * 0.1f;
        Handles.DrawLine(point - Vector3.right * cs, point + Vector3.right * cs);
        Handles.DrawLine(point - Vector3.up * cs, point + Vector3.up * cs);
    }

    // Maps a linear slider [0,1] to [min,max] on a log scale.
    private static float LogSlider(float value, float min, float max)
    {
        float t = Mathf.Log(Mathf.Clamp(value, min, max) / min) / Mathf.Log(max / min);
        float newT = GUILayout.HorizontalSlider(t, 0f, 1f);
        return min * Mathf.Pow(max / min, newT);
    }

    private static GUIStyle _hintStyle;
    private static GUIStyle HintStyle
    {
        get
        {
            _hintStyle ??= new GUIStyle(EditorStyles.miniLabel) { richText = true };
            return _hintStyle;
        }
    }

    // --- Swatch textures for quick-color buttons ---
    private static readonly Color[]  _presetColors = { Color.red, Color.green, Color.blue, Color.white, Color.black };
    private static readonly string[] _presetTips  = { "Red", "Green", "Blue", "White", "Black" };
    private static Texture2D[] _presetSwatches;
    private static GUIStyle[]  _swatchStyles;

    private static Texture2D[] PresetSwatches
    {
        get
        {
            if (_presetSwatches != null && _presetSwatches[0] != null) return _presetSwatches;
            _presetSwatches = new Texture2D[_presetColors.Length];
            for (int i = 0; i < _presetColors.Length; i++)
            {
                var t = new Texture2D(1, 1) { filterMode = FilterMode.Point };
                t.SetPixel(0, 0, _presetColors[i]);
                t.Apply();
                _presetSwatches[i] = t;
            }
            return _presetSwatches;
        }
    }

    // Button style with the swatch as the background so the color fills the whole button.
    private static GUIStyle GetSwatchStyle(int i)
    {
        _swatchStyles ??= new GUIStyle[_presetColors.Length];
        if (_swatchStyles[i] != null) return _swatchStyles[i];
        var tex = PresetSwatches[i];
        var s = new GUIStyle(GUI.skin.button) { border = new RectOffset(1, 1, 1, 1) };
        s.normal.background  = tex;
        s.hover.background   = tex;
        s.active.background  = tex;
        s.focused.background = tex;
        return _swatchStyles[i] = s;
    }

    // --- Brush type icons — one per BrushType enum value ---
    private static Texture2D[] _brushIcons;

    private static Texture2D GetBrushIcon(VertexPaintTool.BrushType type)
    {
        _brushIcons ??= new Texture2D[4];
        int idx = (int)type;
        if (_brushIcons[idx] != null) return _brushIcons[idx];
        const int size = 24;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        float center = size * 0.5f, radius = size * 0.42f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x + 0.5f - center, dy = y + 0.5f - center;
            float t = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / radius);
            float a;
            if      (type == VertexPaintTool.BrushType.Hard)     a = t < 1f ? 1f : 0f;
            else if (type == VertexPaintTool.BrushType.Linear)   a = 1f - t;
            else if (type == VertexPaintTool.BrushType.Soft)     a = (1f - t) * (1f - t);
            else /* Gaussian */                                   a = Mathf.Exp(-4f * t * t);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        return _brushIcons[idx] = tex;
    }

    private static bool CompareRGB(Color a, Color b) =>
        Mathf.Approximately(a.r, b.r) && Mathf.Approximately(a.g, b.g) && Mathf.Approximately(a.b, b.b);

    private static void DrawQuickColorButton(VertexPaintTool vpt, int i)
    {
        if (GUILayout.Button(new GUIContent("", _presetTips[i]), GetSwatchStyle(i), GUILayout.Height(20)))
        {
            Color c = _presetColors[i]; c.a = vpt.paintColor.a; vpt.paintColor = c;
        }
        if (CompareRGB(vpt.paintColor, _presetColors[i]) && Event.current.type == EventType.Repaint)
        {
            Rect r = GUILayoutUtility.GetLastRect();
            EditorGUI.DrawRect(new Rect(r.x,          r.y,          r.width, 2), Color.white);
            EditorGUI.DrawRect(new Rect(r.x,          r.yMax - 2,   r.width, 2), Color.white);
            EditorGUI.DrawRect(new Rect(r.x,          r.y,          2, r.height), Color.white);
            EditorGUI.DrawRect(new Rect(r.xMax - 2,   r.y,          2, r.height), Color.white);
        }
    }

    private static GUIStyle GetOverlayStyle()
    {
        if (_overlayStyle != null) return _overlayStyle;
        var bg = new Texture2D(1, 1) { filterMode = FilterMode.Point };
        bg.SetPixel(0, 0, new Color(0.13f, 0.13f, 0.13f, 0.97f));
        bg.Apply();
        _overlayStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(8, 8, 8, 8)
        };
        _overlayStyle.normal.background = bg;
        return _overlayStyle;
    }

    private void SnapToCorner(int corner)
    {
        float svW    = _currentSV != null ? _currentSV.position.width  : 800f;
        float svH    = _currentSV != null ? _currentSV.position.height : 600f;
        float w      = _overlayRect.width  > 0 ? _overlayRect.width  : 340f;
        float h      = _overlayRect.height > 0 ? _overlayRect.height : 300f;
        const float m = 10f;
        float x = (corner == 1 || corner == 3) ? svW - w - m : m;
        float y = (corner == 2 || corner == 3) ? svH - h - m : m;
        _overlayRect = new Rect(x, y, w, h);
    }

    private void DrawWindowContents(int id)
    {
        VertexPaintTool vpt = _currentVPT;
        if (vpt == null) return;

        // Title row + corner snap buttons
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Vertex Paint Tool", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("↖", EditorStyles.miniButton, GUILayout.Width(22))) SnapToCorner(0);
        if (GUILayout.Button("↗", EditorStyles.miniButton, GUILayout.Width(22))) SnapToCorner(1);
        if (GUILayout.Button("↙", EditorStyles.miniButton, GUILayout.Width(22))) SnapToCorner(2);
        if (GUILayout.Button("↘", EditorStyles.miniButton, GUILayout.Width(22))) SnapToCorner(3);
        EditorGUILayout.EndHorizontal();

        // Coverage
        int overrideCount = vpt.paintData != null ? vpt.paintData.OverrideCount : 0;
        int totalVerts    = vpt.originalMesh != null ? vpt.originalMesh.vertexCount : 0;
        string pct = totalVerts > 0 ? $" ({100f * overrideCount / totalVerts:F0}%)" : "";
        GUILayout.Label($"Painted: {overrideCount} / {totalVerts} verts{pct}");
        GUILayout.Space(6);

        // Color picker
        EditorGUI.BeginChangeCheck();
        Color newColor = EditorGUILayout.ColorField("Paint Color", vpt.paintColor);
        if (EditorGUI.EndChangeCheck()) vpt.paintColor = newColor;

        // Quick-color presets
        EditorGUILayout.BeginHorizontal();
        for (int i = 0; i < _presetColors.Length; i++)
            DrawQuickColorButton(vpt, i);
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(4);

        // Alpha shortcuts
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("No Alpha",   GUILayout.Height(20))) { Color c = vpt.paintColor; c.a = 0f; vpt.paintColor = c; }
        if (GUILayout.Button("Full Alpha", GUILayout.Height(20))) { Color c = vpt.paintColor; c.a = 1f; vpt.paintColor = c; }
        EditorGUILayout.EndHorizontal();

        // Brush Size
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Brush Size", GUILayout.Width(70));
        EditorGUI.BeginChangeCheck();
        float newSize = LogSlider(vpt.brushSize, 0.001f, 5f);
        if (EditorGUI.EndChangeCheck()) vpt.brushSize = (float)System.Math.Round(newSize, 3);
        EditorGUI.BeginChangeCheck();
        float sizeField = EditorGUILayout.DelayedFloatField(vpt.brushSize, GUILayout.Width(40));
        if (EditorGUI.EndChangeCheck()) vpt.brushSize = Mathf.Clamp(sizeField, 0.001f, 5f);
        EditorGUILayout.EndHorizontal();

        // Opacity
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Opacity", GUILayout.Width(70));
        EditorGUI.BeginChangeCheck();
        float newOpacity = GUILayout.HorizontalSlider(vpt.brushOpacity, 0f, 1f);
        if (EditorGUI.EndChangeCheck()) vpt.brushOpacity = newOpacity;
        EditorGUI.BeginChangeCheck();
        float opacityField = EditorGUILayout.DelayedFloatField(vpt.brushOpacity, GUILayout.Width(40));
        if (EditorGUI.EndChangeCheck()) vpt.brushOpacity = Mathf.Clamp01(opacityField);
        EditorGUILayout.EndHorizontal();

        // Brush Type — exclusive icon toolbar
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Brush Type", GUILayout.Width(70));
        GUILayout.FlexibleSpace();
        int brushIdx = (int)vpt.brushType;
        int newIdx = GUILayout.Toolbar(brushIdx, new GUIContent[]
        {
            new GUIContent(GetBrushIcon(VertexPaintTool.BrushType.Hard),     "Hard"),
            new GUIContent(GetBrushIcon(VertexPaintTool.BrushType.Linear),   "Linear"),
            new GUIContent(GetBrushIcon(VertexPaintTool.BrushType.Soft),     "Soft"),
            new GUIContent(GetBrushIcon(VertexPaintTool.BrushType.Gaussian), "Gaussian"),
        }, GUILayout.Height(20), GUILayout.ExpandWidth(false));
        if (newIdx != brushIdx) vpt.brushType = (VertexPaintTool.BrushType)newIdx;
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(4);

        // Preview toggle
        bool prev = vpt.showVertexColors;
        bool show = GUILayout.Toggle(prev, " Show Vertex Colors", GUI.skin.button, GUILayout.Height(24));
        if (show != prev) { vpt.showVertexColors = show; vpt.ApplyPreviewState(); SceneView.RepaintAll(); }

        // Fill / Clear
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Fill Mesh", GUILayout.Height(24))) { vpt.FillMesh(); SceneView.RepaintAll(); }
        if (GUILayout.Button("Clear All", GUILayout.Height(24)))
        {
            if (vpt.paintData != null) Undo.RegisterCompleteObjectUndo(vpt.paintData, "Clear All Paint");
            vpt.paintData?.Clear();
            vpt.RebuildPatchedMesh();
            SceneView.RepaintAll();
        }
        EditorGUILayout.EndHorizontal();

        // Shortcuts
        GUILayout.Space(2);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("<b>Shift+Click</b> Erase",                    HintStyle);
        GUILayout.FlexibleSpace();
        GUILayout.Label("<b>Shift+Wheel</b> / <b>[ / ]</b> Brush Size", HintStyle);
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("<b>Shift+P</b> Preview", HintStyle);
        GUILayout.FlexibleSpace();
        GUILayout.Label("<b>Shift+C</b> Clear",   HintStyle);
        EditorGUILayout.EndHorizontal();

        GUI.DragWindow();
    }

    private void DrawOverlay(SceneView sv, VertexPaintTool vpt)
    {
        _currentSV  = sv;
        _currentVPT = vpt;

        if (!_overlayPosInit)
        {
            _overlayRect    = new Rect(sv.position.width - 350f, 10f, 340f, 0f);
            _overlayPosInit = true;
        }

        Handles.BeginGUI();

        // Keep panel inside the scene view
        _overlayRect.x = Mathf.Clamp(_overlayRect.x, 0f, Mathf.Max(0f, sv.position.width  - _overlayRect.width));
        _overlayRect.y = Mathf.Clamp(_overlayRect.y, 0f, Mathf.Max(0f, sv.position.height - _overlayRect.height));

        _overlayRect = GUILayout.Window(
            "VPTOverlay".GetHashCode(),
            _overlayRect,
            DrawWindowContents,
            GUIContent.none,
            GetOverlayStyle(),
            GUILayout.Width(340f)
        );

        Handles.EndGUI();
    }
}

[CustomEditor(typeof(VertexPaintTool))]
public class VertexPaintToolInspector : Editor
{
    private SerializedProperty _paintData, _originalMesh;
    private void OnEnable()
    {
        _paintData    = serializedObject.FindProperty("paintData");
        _originalMesh = serializedObject.FindProperty("originalMesh");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        VertexPaintTool vpt = (VertexPaintTool)target;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Assets", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(_paintData);
        EditorGUILayout.PropertyField(_originalMesh);

        if (vpt.paintData != null)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Vertex overrides saved: {vpt.paintData.OverrideCount}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Mesh: {vpt.originalMesh?.name ?? "(none)"} ({vpt.originalMesh?.vertexCount ?? 0} verts)", EditorStyles.miniLabel);
        }

        EditorGUILayout.Space(4);

        if (!vpt.HasPatchedMesh)
            EditorGUILayout.HelpBox("Patched mesh is missing from the renderer. Click Rebuild Mesh to restore it.", MessageType.Error);

        if (GUILayout.Button("Rebuild Mesh"))
        {
            vpt.RebuildPatchedMesh();
            vpt.ApplyPreviewState();
        }

        EditorGUILayout.Space(2);
        EditorGUILayout.HelpBox("Paint controls are in the Scene View overlay\nwhen the Vertex Paint Tool is active.", MessageType.Info);

        serializedObject.ApplyModifiedProperties();
    }
}
