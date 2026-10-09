using Cube.Core.Scene;

namespace Cube.Core.Selection;

/// <summary>선택 모드(Maya 선택 마스크). Object = 오브젝트(트랜스폼 노드), Vertex/Edge/Face = 폴리곤 컴포넌트(ID = 메시 슬롯 인덱스), Uv = UV 점(ID = UvTopology의 UV 점 번호).</summary>
public enum SelectMode { Object, Vertex, Edge, Face, Uv }

/// <summary>클릭/마키 선택 수식어(Maya 규칙). 각 멤버 오른쪽 주석이 대응 키.</summary>
public enum SelectModifier
{
    /// <summary>기존 선택을 비우고 새 항목으로 교체.</summary>
    Replace,   // 클릭
    /// <summary>선택돼 있으면 빼고 아니면 더함.</summary>
    Toggle,    // Shift
    /// <summary>선택에서 뺌.</summary>
    Remove,    // Ctrl
    /// <summary>선택에 더함.</summary>
    Add,       // Ctrl+Shift
}

/// <summary>한 노드의 컴포넌트 선택. 모드별 ID 집합을 따로 보관한다(Maya처럼 vtx/e/f 선택이 공존).</summary>
/// <remarks>모드를 바꿔도 다른 모드의 선택은 지워지지 않고 남아 있다가 그 모드로 돌아오면 다시 보인다.</remarks>
public sealed class ComponentSet
{
    /// <summary>선택된 정점 ID.</summary>
    public readonly HashSet<int> Verts = new();
    /// <summary>선택된 엣지 ID.</summary>
    public readonly HashSet<int> Edges = new();
    /// <summary>선택된 면 ID.</summary>
    public readonly HashSet<int> Faces = new();
    /// <summary>선택된 UV 점 ID(UvTopology 번호).</summary>
    public readonly HashSet<int> Uvs = new();

    /// <summary>모드에 해당하는 ID 집합(Object 모드는 예외).</summary>
    public HashSet<int> Get(SelectMode mode) => mode switch
    {
        SelectMode.Vertex => Verts,
        SelectMode.Edge => Edges,
        SelectMode.Face => Faces,
        SelectMode.Uv => Uvs,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>네 집합이 모두 비었는지.</summary>
    public bool IsEmpty => Verts.Count == 0 && Edges.Count == 0 && Faces.Count == 0 && Uvs.Count == 0;

    /// <summary>네 집합을 복사한 새 인스턴스(스냅샷용).</summary>
    public ComponentSet Clone()
    {
        var c = new ComponentSet();
        c.Verts.UnionWith(Verts); c.Edges.UnionWith(Edges); c.Faces.UnionWith(Faces); c.Uvs.UnionWith(Uvs);
        return c;
    }

    /// <summary>모든 모드의 선택을 비운다.</summary>
    public void ClearAll() { Verts.Clear(); Edges.Clear(); Faces.Clear(); Uvs.Clear(); }
}

/// <summary>선택 항목 하나. 오브젝트 모드면 Component=-1.</summary>
/// <remarks>Picker(클릭/마키)가 만들어 <see cref="SelectionState.Apply"/>에 넘긴다.</remarks>
public readonly record struct SelItem(NodeId Node, int Component);

/// <summary>선택 상태의 불변 스냅샷(Undo용).</summary>
/// <remarks>Capture가 컴포넌트 집합을 복제해 만들므로 이후 선택 변경에 영향받지 않는다.</remarks>
public sealed class SelectionSnapshot
{
    /// <summary>캡처 당시 선택 모드.</summary>
    public SelectMode Mode;
    /// <summary>캡처 당시 활성 오브젝트(정보용; 복원은 Objects 순서로 결정됨).</summary>
    public NodeId ActiveObject;
    /// <summary>캡처 당시 컴포넌트 편집 대상(hilite).</summary>
    public NodeId ComponentTarget;
    /// <summary>선택된 오브젝트(선택 순서, 마지막 = 활성).</summary>
    public readonly List<NodeId> Objects = new();
    /// <summary>노드별 컴포넌트 선택 복사본.</summary>
    public readonly Dictionary<NodeId, ComponentSet> Components = new();
}

/// <summary>
/// 선택 모드와 선택된 오브젝트/컴포넌트. 변경 시 <see cref="Changed"/>를 발행한다.
/// Undo는 <see cref="Capture"/>/<see cref="Restore"/>로 처리한다.
/// </summary>
/// <remarks>
/// 오브젝트 선택은 순서 있는 목록(마지막 = 활성), 컴포넌트 선택은 노드별 ComponentSet이다.
/// v0.0.22부터 컴포넌트 모드는 개체 하나(<see cref="ComponentTarget"/>)만 다룬다. 비어 버린 ComponentSet은 PruneEmpty로 정리한다.
/// </remarks>
public sealed class SelectionState
{
    /// <summary>소유 문서(Restore 때 사라진 노드를 거르는 데 쓴다).</summary>
    private readonly Document _doc;
    /// <summary>선택된 오브젝트 목록.</summary>
    private readonly List<NodeId> _objects = new();        // 선택 순서 유지(마지막 = 활성)
    /// <summary>노드 → 컴포넌트 선택.</summary>
    private readonly Dictionary<NodeId, ComponentSet> _components = new();
    /// <summary>현재 선택 모드.</summary>
    private SelectMode _mode = SelectMode.Object;
    /// <summary>
    /// 컴포넌트 모드에서 편집 중인 개체(Maya hilite, 하나만). 오브젝트 모드에서 컴포넌트 모드로 갈 때 활성 오브젝트가 되고
    /// 다른 선택 오브젝트와 다른 노드의 컴포넌트 선택은 해제된다. 선택된 오브젝트가 없었으면 None이고, 처음 집은 컴포넌트의 노드가 된다.
    /// 클릭/마키는 이 개체의 컴포넌트만 집는다.
    /// </summary>
    private NodeId _target = NodeId.None;
    /// <summary>컴포넌트 편집 대상(오브젝트 모드에서는 항상 None).</summary>
    public NodeId ComponentTarget => _mode == SelectMode.Object ? NodeId.None : _target;

    /// <summary>컴포넌트 모드에서 이 노드를 컴포넌트로 그리고 집을 수 있는지.</summary>
    public bool IsComponentEditable(NodeId id) => _mode != SelectMode.Object && (_target == id || (_components.TryGetValue(id, out var c) && !c.IsEmpty));

    /// <summary>선택 내용이나 모드가 바뀔 때 발생(Document가 ChangeKind.Selection으로 중계).</summary>
    public event Action? Changed;
    /// <summary>선택 모드가 바뀔 때만 발생.</summary>
    public event Action? ModeChanged;
    /// <summary>모든 구독자를 뗀다(<see cref="Document.ClearEventSubscribers"/>에서만).</summary>
    internal void ClearSubscribers() { Changed = null; ModeChanged = null; }

    /// <summary>문서에 연결된 빈 선택(오브젝트 모드).</summary>
    public SelectionState(Document doc) { _doc = doc; }


    /// <summary>선택 모드. 오브젝트 → 컴포넌트로 바뀌는 순간 활성 오브젝트가 편집 대상이 된다(EnterComponentMode).</summary>
    public SelectMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            if (_mode == SelectMode.Object && value != SelectMode.Object) EnterComponentMode();
            _mode = value;
            ModeChanged?.Invoke();
            Changed?.Invoke();
        }
    }

    /// <summary>오브젝트 모드가 아닌지.</summary>
    public bool IsComponentMode => _mode != SelectMode.Object;

    /// <summary>오브젝트 → 컴포넌트 전환 처리: 활성 오브젝트를 대상으로 삼고 다른 오브젝트 선택과 다른 노드의 컴포넌트 선택을 지운다.</summary>
    private void EnterComponentMode()
    {
        _target = ActiveObject;
        if (_target != NodeId.None) { _objects.Clear(); _objects.Add(_target); }
        foreach (var k in _components.Keys.Where(k => k != _target).ToArray()) _components.Remove(k);
    }

    /// <summary>컴포넌트 모드 대상 개체를 바꾼다(다른 노드의 컴포넌트 선택은 해제).</summary>
    public void SetComponentTarget(NodeId id)
    {
        _target = id;
        if (id != NodeId.None) { _objects.Clear(); _objects.Add(id); }
        foreach (var k in _components.Keys.Where(k => k != id).ToArray()) _components.Remove(k);
        Changed?.Invoke();
    }
    /// <summary>선택된 오브젝트(선택 순서).</summary>
    public IReadOnlyList<NodeId> Objects => _objects;
    /// <summary>노드별 컴포넌트 선택(읽기 전용 보기).</summary>
    public IReadOnlyDictionary<NodeId, ComponentSet> Components => _components;
    /// <summary>활성(마지막으로 선택한) 오브젝트. 조작기·Properties가 따른다.</summary>
    public NodeId ActiveObject => _objects.Count > 0 ? _objects[^1] : NodeId.None;
    /// <summary>오브젝트도 컴포넌트도 선택되지 않았는지.</summary>
    public bool IsEmpty => _objects.Count == 0 && !_components.Values.Any(c => !c.IsEmpty);

    /// <summary>오브젝트가 선택 목록에 있는지.</summary>
    public bool IsObjectSelected(NodeId id) => _objects.Contains(id);

    /// <summary>노드의 특정 모드 컴포넌트가 선택돼 있는지.</summary>
    public bool IsComponentSelected(NodeId node, SelectMode mode, int id)
        => _components.TryGetValue(node, out var c) && c.Get(mode).Contains(id);

    /// <summary>노드의 ComponentSet(없으면 만들어 등록). 반환된 집합을 직접 고치면 통지가 없으므로 호출자가 필요 시 통지한다.</summary>
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
    /// <remarks>
    /// 1) 컴포넌트 모드면 대상 개체 이외의 항목을 버린다(대상이 없으면 첫 항목의 노드가 대상).
    /// 2) Replace면 현재 모드 선택을 비운다. 3) 항목마다 수식어 규칙 적용(오브젝트 Replace/Add는 이미 있으면 끝으로 옮겨 활성으로).
    /// </remarks>
    public void Apply(IEnumerable<SelItem> items, SelectModifier modifier)
    {
        if (_mode != SelectMode.Object)
        {
            // 한 개체만: 대상이 없으면 처음 집은 노드가 대상이 되고, 다른 노드의 항목은 버린다
            var list = items.ToList();
            if (_target == NodeId.None && list.Count > 0)
            {
                _target = list[0].Node;
                _objects.Clear(); _objects.Add(_target);
                foreach (var k in _components.Keys.Where(k => k != _target).ToArray()) _components.Remove(k);
            }
            var t = _target;
            items = list.Where(it => it.Node == t).ToList();
        }
        // Replace: 현재 모드의 기존 선택만 비운다(다른 모드의 컴포넌트 선택은 유지).
        if (modifier == SelectModifier.Replace)
        {
            if (_mode == SelectMode.Object) _objects.Clear();
            else foreach (var c in _components.Values) c.Get(_mode).Clear();
        }
        // 항목별 수식어 적용.
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

    /// <summary>오브젝트를 선택한다(목록 끝 = 활성). 컴포넌트 모드에서는 마지막 노드를 컴포넌트 편집 대상으로 바꾸기만 한다.</summary>
    public void SelectObjects(IEnumerable<NodeId> ids, bool replace = true)
    {
        if (_mode != SelectMode.Object)
        {
            // 컴포넌트 모드에서 오브젝트를 고르면(Outliner 등) 그 개체가 컴포넌트 편집 대상이 된다
            var last = ids.LastOrDefault(NodeId.None);
            if (last != NodeId.None) SetComponentTarget(last);
            return;
        }
        // 이미 있던 항목은 빼고 다시 넣어 순서상 끝(활성)으로 보낸다.
        if (replace) _objects.Clear();
        foreach (var id in ids) { _objects.Remove(id); _objects.Add(id); }
        Changed?.Invoke();
    }

    /// <summary>노드의 컴포넌트를 코드로 선택한다(명령 결과 선택 등). replace면 모든 노드의 해당 모드 선택을 먼저 비운다.</summary>
    public void SelectComponents(NodeId node, SelectMode mode, IEnumerable<int> ids, bool replace = true)
    {
        // 대상이 없으면 이 노드를 컴포넌트 편집 대상(및 유일한 선택 오브젝트)으로 삼는다.
        if (_target == NodeId.None && mode != SelectMode.Object) { _target = node; if (!_objects.Contains(node)) { _objects.Clear(); _objects.Add(node); } }
        if (replace) foreach (var c in _components.Values) c.Get(mode).Clear();
        var set = GetComponents(node).Get(mode);
        foreach (var id in ids) set.Add(id);
        PruneEmpty();
        Changed?.Invoke();
    }

    /// <summary>현재 모드의 선택만 비운다(오브젝트 모드면 오브젝트 선택).</summary>
    public void ClearCurrentMode()
    {
        if (_mode == SelectMode.Object) _objects.Clear();
        else foreach (var c in _components.Values) c.Get(_mode).Clear();
        PruneEmpty();
        Changed?.Invoke();
    }

    /// <summary>오브젝트·컴포넌트 선택을 모두 비운다. silent면 통지하지 않는다(문서 초기화 등).</summary>
    public void ClearAll(bool silent = false)
    {
        _objects.Clear(); _components.Clear();
        if (!silent) Changed?.Invoke();
    }

    /// <summary>노드가 문서에서 빠질 때 선택에서 지운다(Document.RemoveNode가 호출). 대상이었으면 대상도 해제.</summary>
    internal void RemoveObject(NodeId id, bool silent)
    {
        if (_target == id) _target = NodeId.None;
        bool changed = _objects.Remove(id) | _components.Remove(id);
        if (changed && !silent) Changed?.Invoke();
    }

    /// <summary>비어 있는 ComponentSet 항목을 사전에서 제거한다(IsEmpty/NodesWithComponents가 정확하도록).</summary>
    private void PruneEmpty()
    {
        foreach (var k in _components.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToArray()) _components.Remove(k);
    }

    // ------------------------------------------------------------ 스냅샷

    /// <summary>현재 선택의 깊은 복사 스냅샷.</summary>
    public SelectionSnapshot Capture()
    {
        var s = new SelectionSnapshot { Mode = _mode, ActiveObject = ActiveObject, ComponentTarget = _target };
        s.Objects.AddRange(_objects);
        foreach (var (k, v) in _components) s.Components[k] = v.Clone();
        return s;
    }

    /// <summary>스냅샷으로 선택을 되돌린다. 그 사이 문서에서 사라진 노드는 건너뛴다. 모드가 바뀌었으면 ModeChanged도 발생.</summary>
    public void Restore(SelectionSnapshot s)
    {
        bool modeChanged = _mode != s.Mode;
        _mode = s.Mode;
        _target = _doc.Find(s.ComponentTarget) != null ? s.ComponentTarget : NodeId.None;
        _objects.Clear(); _objects.AddRange(s.Objects.Where(id => _doc.Find(id) != null));
        _components.Clear();
        foreach (var (k, v) in s.Components) if (_doc.Find(k) != null) _components[k] = v.Clone();
        if (modeChanged) ModeChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>두 스냅샷의 모드·대상·오브젝트 순서·컴포넌트 집합이 모두 같은지(SelectionCommand.IsNoop).</summary>
    public static bool AreEqual(SelectionSnapshot a, SelectionSnapshot b)
    {
        if (a.Mode != b.Mode || a.ComponentTarget != b.ComponentTarget || !a.Objects.SequenceEqual(b.Objects) || a.Components.Count != b.Components.Count) return false;
        foreach (var (k, v) in a.Components)
        {
            if (!b.Components.TryGetValue(k, out var w)) return false;
            if (!v.Verts.SetEquals(w.Verts) || !v.Edges.SetEquals(w.Edges) || !v.Faces.SetEquals(w.Faces) || !v.Uvs.SetEquals(w.Uvs)) return false;
        }
        return true;
    }
}
