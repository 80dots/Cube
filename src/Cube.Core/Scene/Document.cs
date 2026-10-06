using Cube.Core.Commands;
using Cube.Core.Selection;

namespace Cube.Core.Scene;

public enum ChangeKind
{
    Reset,            // 문서 전체 교체(새 문서/열기)
    NodeAdded,
    NodeRemoved,
    NodeRenamed,
    NodeReparented,
    TransformChanged,
    VisibilityChanged,
    MeshTopology,     // 위상 변경 → 전체 재빌드
    MeshGeometry,     // 위치만 변경 → 포지션 갱신
    MeshAttributes,   // 노멀/UV/하드엣지 등 → 전체 재빌드(위상 동일)
    Selection,
}

public readonly record struct DocChange(ChangeKind Kind, NodeId Node);

/// <summary>
/// 편집 문서. 씬 DAG, 선택 상태, Undo 스택을 소유한다. Godot을 모르며 변경은 <see cref="Changed"/>로만 알린다.
/// </summary>
public sealed class Document
{
    private int _nextId = 1;
    private readonly Dictionary<NodeId, SceneNode> _nodes = new();

    public SceneNode Root { get; }
    public IReadOnlyDictionary<NodeId, SceneNode> Nodes => _nodes;
    public SelectionState Selection { get; }
    public UndoStack Undo { get; }
    public string? FilePath { get; set; }
    public bool IsDirty { get; set; }

    public event Action<DocChange>? Changed;

    public Document()
    {
        Root = new SceneNode { Name = "root", IsRoot = true, Id = NodeId.None };
        Selection = new SelectionState(this);
        Undo = new UndoStack(this);
        Selection.Changed += () => Notify(new DocChange(ChangeKind.Selection, NodeId.None));
    }

    public NodeId AllocateId() => new(_nextId++);

    public SceneNode? Find(NodeId id) => _nodes.TryGetValue(id, out var n) ? n : null;

    public SceneNode Get(NodeId id) => _nodes.TryGetValue(id, out var n) ? n : throw new KeyNotFoundException($"node {id}");

    /// <summary>노드를 트리에 넣는다. Id가 None이면 새로 할당한다(Undo 재삽입은 기존 Id 유지).</summary>
    public void AddNode(SceneNode node, SceneNode? parent = null, int index = -1)
    {
        if (node.Id.IsNone) node.Id = AllocateId();
        if (_nodes.ContainsKey(node.Id)) throw new InvalidOperationException($"node {node.Id} already in document");
        parent ??= Root;
        node.Parent = parent;
        if (index < 0 || index > parent.Children.Count) parent.Children.Add(node); else parent.Children.Insert(index, node);
        _nodes[node.Id] = node;
        foreach (var d in node.Descendants()) _nodes[d.Id] = d;
        IsDirty = true;
        Notify(new DocChange(ChangeKind.NodeAdded, node.Id));
    }

    /// <summary>노드(와 하위 트리)를 트리에서 뗀다. 객체는 보존되어 다시 AddNode 할 수 있다. 반환값은 부모 내 인덱스.</summary>
    public int RemoveNode(SceneNode node)
    {
        if (node.Parent == null) return -1;
        int index = node.Parent.Children.IndexOf(node);
        node.Parent.Children.Remove(node);
        _nodes.Remove(node.Id);
        foreach (var d in node.Descendants()) _nodes.Remove(d.Id);
        Selection.RemoveObject(node.Id, silent: true);
        foreach (var d in node.Descendants()) Selection.RemoveObject(d.Id, silent: true);
        node.Parent = null;
        IsDirty = true;
        Notify(new DocChange(ChangeKind.NodeRemoved, node.Id));
        return index;
    }

    public void Reparent(SceneNode node, SceneNode newParent, int index = -1)
    {
        if (node.Parent != null) node.Parent.Children.Remove(node);
        node.Parent = newParent;
        if (index < 0 || index > newParent.Children.Count) newParent.Children.Add(node); else newParent.Children.Insert(index, node);
        IsDirty = true;
        Notify(new DocChange(ChangeKind.NodeReparented, node.Id));
    }

    public void Notify(DocChange change)
    {
        if (change.Kind != ChangeKind.Selection) IsDirty = true;
        Changed?.Invoke(change);
    }

    /// <summary>문서를 비운다(새 문서). Undo 이력도 지운다.</summary>
    public void Clear()
    {
        foreach (var c in Root.Children.ToArray()) { c.Parent = null; }
        Root.Children.Clear();
        _nodes.Clear();
        _nextId = 1;
        Selection.ClearAll(silent: true);
        Undo.Clear();
        FilePath = null;
        IsDirty = false;
        Notify(new DocChange(ChangeKind.Reset, NodeId.None));
        IsDirty = false;
    }

    /// <summary>표시 가능한 모든 메시 노드.</summary>
    public IEnumerable<SceneNode> MeshNodes()
    {
        foreach (var n in _nodes.Values) if (n.MeshShape != null) yield return n;
    }

    /// <summary>이름이 겹치지 않도록 접미 번호를 붙인다(Maya: pCube1, pCube2 ...).</summary>
    public string UniqueName(string baseName)
    {
        var used = new HashSet<string>();
        foreach (var n in _nodes.Values) used.Add(n.Name);
        if (!used.Contains(baseName) && !char.IsDigit(baseName[^1])) return baseName;
        string stem = baseName.TrimEnd("0123456789".ToCharArray());
        if (stem.Length == 0) stem = baseName;
        for (int i = 1; ; i++)
        {
            string cand = stem + i;
            if (!used.Contains(cand)) return cand;
        }
    }
}
