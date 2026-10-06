using Cube.Core.Scene;

namespace Cube.Core.Selection;

public enum SelectMode { Object, Vertex, Edge, Face, Uv }

public enum SelectModifier
{
    Replace,   // 클릭
    Toggle,    // Shift
    Remove,    // Ctrl
    Add,       // Ctrl+Shift
}

/// <summary>한 노드의 컴포넌트 선택. 모드별 ID 집합을 따로 보관한다(Maya처럼 vtx/e/f 선택이 공존).</summary>
public sealed class ComponentSet
{
    public readonly HashSet<int> Verts = new();
    public readonly HashSet<int> Edges = new();
    public readonly HashSet<int> Faces = new();
    public readonly HashSet<int> Uvs = new();

    public HashSet<int> Get(SelectMode mode) => mode switch
    {
        SelectMode.Vertex => Verts,
        SelectMode.Edge => Edges,
        SelectMode.Face => Faces,
        SelectMode.Uv => Uvs,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public bool IsEmpty => Verts.Count == 0 && Edges.Count == 0 && Faces.Count == 0 && Uvs.Count == 0;

    public ComponentSet Clone()
    {
        var c = new ComponentSet();
        c.Verts.UnionWith(Verts); c.Edges.UnionWith(Edges); c.Faces.UnionWith(Faces); c.Uvs.UnionWith(Uvs);
        return c;
    }

    public void ClearAll() { Verts.Clear(); Edges.Clear(); Faces.Clear(); Uvs.Clear(); }
}

/// <summary>선택 항목 하나. 오브젝트 모드면 Component=-1.</summary>
public readonly record struct SelItem(NodeId Node, int Component);

/// <summary>선택 상태의 불변 스냅샷(Undo용).</summary>
public sealed class SelectionSnapshot
{
    public SelectMode Mode;
    public NodeId ActiveObject;
    public readonly List<NodeId> Objects = new();
    public readonly Dictionary<NodeId, ComponentSet> Components = new();
}

/// <summary>
/// 선택 모드와 선택된 오브젝트/컴포넌트. 변경 시 <see cref="Changed"/>를 발행한다.
/// Undo는 <see cref="Capture"/>/<see cref="Restore"/>로 처리한다.
/// </summary>
public sealed class SelectionState
{
    private readonly Document _doc;
    private readonly List<NodeId> _objects = new();        // 선택 순서 유지(마지막 = 활성)
    private readonly Dictionary<NodeId, ComponentSet> _components = new();
    private SelectMode _mode = SelectMode.Object;

    public event Action? Changed;
    public event Action? ModeChanged;

    public SelectionState(Document doc) { _doc = doc; }

    public SelectMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ModeChanged?.Invoke();
            Changed?.Invoke();
        }
    }

    public bool IsComponentMode => _mode != SelectMode.Object;
    public IReadOnlyList<NodeId> Objects => _objects;
    public IReadOnlyDictionary<NodeId, ComponentSet> Components => _components;
    public NodeId ActiveObject => _objects.Count > 0 ? _objects[^1] : NodeId.None;
    public bool IsEmpty => _objects.Count == 0 && !_components.Values.Any(c => !c.IsEmpty);

    public bool IsObjectSelected(NodeId id) => _objects.Contains(id);

    public bool IsComponentSelected(NodeId node, SelectMode mode, int id)
        => _components.TryGetValue(node, out var c) && c.Get(mode).Contains(id);

    public ComponentSet GetComponents(NodeId node)
    {
        if (!_components.TryGetValue(node, out var c)) { c = new ComponentSet(); _components[node] = c; }
        return c;
    }

    /// <summary>컴포넌트가 선택된 노드들(현재 모드 기준, 비어 있지 않은 것만).</summary>
    public IEnumerable<NodeId> NodesWithComponents(SelectMode mode)
    {
        if (mode == SelectMode.Object) yield break;
        foreach (var (id, c) in _components) if (c.Get(mode).Count > 0) yield return id;
    }

    // ------------------------------------------------------------ 변경

    /// <summary>클릭/마키 공용 적용. 항목이 비어 있고 Replace면 전체 해제.</summary>
    public void Apply(IEnumerable<SelItem> items, SelectModifier modifier)
    {
        if (modifier == SelectModifier.Replace)
        {
            if (_mode == SelectMode.Object) _objects.Clear();
            else foreach (var c in _components.Values) c.Get(_mode).Clear();
        }
        foreach (var it in items)
        {
            if (_mode == SelectMode.Object)
            {
                bool has = _objects.Contains(it.Node);
                switch (modifier)
                {
                    case SelectModifier.Replace:
                    case SelectModifier.Add:
                        if (!has) _objects.Add(it.Node); else { _objects.Remove(it.Node); _objects.Add(it.Node); }
                        break;
                    case SelectModifier.Toggle:
                        if (has) _objects.Remove(it.Node); else _objects.Add(it.Node);
                        break;
                    case SelectModifier.Remove:
                        _objects.Remove(it.Node);
                        break;
                }
            }
            else
            {
                var set = GetComponents(it.Node).Get(_mode);
                switch (modifier)
                {
                    case SelectModifier.Replace:
                    case SelectModifier.Add: set.Add(it.Component); break;
                    case SelectModifier.Toggle: if (!set.Remove(it.Component)) set.Add(it.Component); break;
                    case SelectModifier.Remove: set.Remove(it.Component); break;
                }
            }
        }
        PruneEmpty();
        Changed?.Invoke();
    }

    public void SelectObjects(IEnumerable<NodeId> ids, bool replace = true)
    {
        if (replace) _objects.Clear();
        foreach (var id in ids) { _objects.Remove(id); _objects.Add(id); }
        Changed?.Invoke();
    }

    public void SelectComponents(NodeId node, SelectMode mode, IEnumerable<int> ids, bool replace = true)
    {
        if (replace) foreach (var c in _components.Values) c.Get(mode).Clear();
        var set = GetComponents(node).Get(mode);
        foreach (var id in ids) set.Add(id);
        PruneEmpty();
        Changed?.Invoke();
    }

    public void ClearCurrentMode()
    {
        if (_mode == SelectMode.Object) _objects.Clear();
        else foreach (var c in _components.Values) c.Get(_mode).Clear();
        PruneEmpty();
        Changed?.Invoke();
    }

    public void ClearAll(bool silent = false)
    {
        _objects.Clear(); _components.Clear();
        if (!silent) Changed?.Invoke();
    }

    internal void RemoveObject(NodeId id, bool silent)
    {
        bool changed = _objects.Remove(id) | _components.Remove(id);
        if (changed && !silent) Changed?.Invoke();
    }

    private void PruneEmpty()
    {
        foreach (var k in _components.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToArray()) _components.Remove(k);
    }

    // ------------------------------------------------------------ 스냅샷

    public SelectionSnapshot Capture()
    {
        var s = new SelectionSnapshot { Mode = _mode, ActiveObject = ActiveObject };
        s.Objects.AddRange(_objects);
        foreach (var (k, v) in _components) s.Components[k] = v.Clone();
        return s;
    }

    public void Restore(SelectionSnapshot s)
    {
        bool modeChanged = _mode != s.Mode;
        _mode = s.Mode;
        _objects.Clear(); _objects.AddRange(s.Objects.Where(id => _doc.Find(id) != null));
        _components.Clear();
        foreach (var (k, v) in s.Components) if (_doc.Find(k) != null) _components[k] = v.Clone();
        if (modeChanged) ModeChanged?.Invoke();
        Changed?.Invoke();
    }

    public static bool AreEqual(SelectionSnapshot a, SelectionSnapshot b)
    {
        if (a.Mode != b.Mode || !a.Objects.SequenceEqual(b.Objects) || a.Components.Count != b.Components.Count) return false;
        foreach (var (k, v) in a.Components)
        {
            if (!b.Components.TryGetValue(k, out var w)) return false;
            if (!v.Verts.SetEquals(w.Verts) || !v.Edges.SetEquals(w.Edges) || !v.Faces.SetEquals(w.Faces) || !v.Uvs.SetEquals(w.Uvs)) return false;
        }
        return true;
    }
}
