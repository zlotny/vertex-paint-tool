using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Stores vertex color overrides as a "patch" for a mesh.
/// Only stores differences from white (neutral).
/// This is the ONLY new asset created on disk when vertex painting.
/// </summary>
[CreateAssetMenu(menuName = "VertexPaintData", fileName = "New VertexPaintData")]
public class VertexPaintData : ScriptableObject, ISerializationCallbackReceiver
{
    [SerializeField] private List<int> _savedIndices = new List<int>();
    [SerializeField] private List<Color> _savedColors = new List<Color>();

    private Dictionary<int, Color> _vertexOverrides = new Dictionary<int, Color>();

    public void SetVertexColor(int index, Color color)
    {
        _vertexOverrides[index] = color;
    }

    public bool TryGetVertexColor(int index, out Color color)
    {
        return _vertexOverrides.TryGetValue(index, out color);
    }

    public void RemoveVertexColor(int index)
    {
        _vertexOverrides.Remove(index);
    }

    public void Clear()
    {
        _vertexOverrides.Clear();
    }

    public int OverrideCount => _vertexOverrides.Count;

    public Dictionary<int, Color> GetAllOverrides()
    {
        return _vertexOverrides;
    }

    public void OnBeforeSerialize()
    {
        _savedIndices.Clear();
        _savedColors.Clear();
        foreach (var kvp in _vertexOverrides)
        {
            _savedIndices.Add(kvp.Key);
            _savedColors.Add(kvp.Value);
        }
    }

    public void OnAfterDeserialize()
    {
        _vertexOverrides.Clear();
        int count = Mathf.Min(_savedIndices.Count, _savedColors.Count);
        for (int i = 0; i < count; i++)
            _vertexOverrides[_savedIndices[i]] = _savedColors[i];
    }
}
