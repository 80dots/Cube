using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>미리 만든 노드를 문서에 넣고 선택한다. Undo 시 노드 ID는 유지된다.</summary>
public sealed class AddNodeCommand : ICommand
{
    private readonly SceneNode _node;
    private readonly NodeId _parent;
    private SelectionSnapshot? _selBefore;
    private int _index = -1;

    public string Name { get; }
    public SceneNode Node => _node;

    public AddNodeCommand(string name, SceneNode node, NodeId parent = default)
    {
        Name = name; _node = node; _parent = parent;
    }

    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        var parent = _parent.IsNone ? doc.Root : doc.Get(_parent);
        doc.AddNode(_node, parent, _index);
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(new[] { _node.Id });
    }

    public void Undo(Document doc)
    {
        _index = doc.RemoveNode(_node);
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}

/// <summary>프리미티브 생성. Maya처럼 원점에 만들고 pCube1 식 이름을 붙인다.</summary>
public static class CreatePrimitiveCommand
{
    public static AddNodeCommand Cube(Document doc, float w = 1, float h = 1, float d = 1) => Make(doc, "pCube", MeshBuilder.Cube(w, h, d));
    public static AddNodeCommand Plane(Document doc, float w = 1, float d = 1, int sx = 1, int sz = 1) => Make(doc, "pPlane", MeshBuilder.Plane(w, d, sx, sz));
    public static AddNodeCommand Cylinder(Document doc, float r = 0.5f, float h = 1, int seg = 20) => Make(doc, "pCylinder", MeshBuilder.Cylinder(r, h, seg));
    public static AddNodeCommand Cone(Document doc, float r = 0.5f, float h = 1, int seg = 20) => Make(doc, "pCone", MeshBuilder.Cone(r, h, seg));
    public static AddNodeCommand Sphere(Document doc, float r = 0.5f, int seg = 20, int rings = 10) => Make(doc, "pSphere", MeshBuilder.Sphere(r, seg, rings));
    public static AddNodeCommand Torus(Document doc, float r = 0.5f, float sr = 0.2f, int seg = 20, int sec = 12) => Make(doc, "pTorus", MeshBuilder.Torus(r, sr, seg, sec));

    private static AddNodeCommand Make(Document doc, string baseName, PolyMesh mesh)
    {
        var node = new SceneNode { Name = doc.UniqueName(baseName + "1"), Shape = new MeshShape(mesh) };
        return new AddNodeCommand("Create " + baseName.TrimStart('p'), node);
    }
}

/// <summary>노드 삭제(하위 포함). Undo 시 같은 부모·인덱스로 복원.</summary>
public sealed class DeleteNodesCommand : ICommand
{
    private readonly List<(SceneNode node, NodeId parent, int index)> _items = new();
    private SelectionSnapshot? _selBefore;
    public string Name => "Delete";

    public DeleteNodesCommand(Document doc, IEnumerable<NodeId> ids)
    {
        // 조상이 포함된 노드는 제외(조상 삭제에 포함됨)
        var set = new HashSet<NodeId>(ids);
        foreach (var id in set)
        {
            var n = doc.Find(id); if (n == null) continue;
            bool ancestorSelected = false;
            for (var p = n.Parent; p != null && !p.IsRoot; p = p.Parent) if (set.Contains(p.Id)) { ancestorSelected = true; break; }
            if (!ancestorSelected) _items.Add((n, n.Parent!.IsRoot ? NodeId.None : n.Parent.Id, n.Parent.Children.IndexOf(n)));
        }
        _items.Sort((a, b) => b.index.CompareTo(a.index)); // 뒤에서부터 지워 인덱스 보존
    }

    public bool IsEmpty => _items.Count == 0;

    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        foreach (var (n, _, _) in _items) doc.RemoveNode(n);
        doc.Selection.ClearAll();
    }

    public void Undo(Document doc)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var (n, p, idx) = _items[i];
            doc.AddNode(n, p.IsNone ? doc.Root : doc.Get(p), idx);
        }
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}

public sealed class RenameNodeCommand : ICommand
{
    private readonly NodeId _id; private readonly string _before, _after;
    public string Name => "Rename";
    public RenameNodeCommand(Document doc, NodeId id, string newName) { _id = id; _before = doc.Get(id).Name; _after = newName; }
    public void Do(Document doc) { doc.Get(_id).Name = _after; doc.Notify(new DocChange(ChangeKind.NodeRenamed, _id)); }
    public void Undo(Document doc) { doc.Get(_id).Name = _before; doc.Notify(new DocChange(ChangeKind.NodeRenamed, _id)); }
}

/// <summary>노드들의 로컬 트랜스폼 변경. 드래그는 alreadyApplied로 넣는다.</summary>
public sealed class TransformNodesCommand : ICommand
{
    private readonly NodeId[] _ids; private readonly Transform3[] _before, _after;
    public string Name { get; }
    public IReadOnlyList<NodeId> Ids => _ids;
    public IReadOnlyList<Transform3> Before => _before;
    public IReadOnlyList<Transform3> After => _after;

    public TransformNodesCommand(string name, NodeId[] ids, Transform3[] before, Transform3[] after)
    {
        Name = name; _ids = ids; _before = before; _after = after;
    }

    public bool IsNoop { get { for (int i = 0; i < _ids.Length; i++) if (_before[i] != _after[i]) return false; return true; } }

    public void Do(Document doc) => Apply(doc, _after);
    public void Undo(Document doc) => Apply(doc, _before);

    private void Apply(Document doc, Transform3[] values)
    {
        for (int i = 0; i < _ids.Length; i++)
        {
            var n = doc.Find(_ids[i]); if (n == null) continue;
            n.Local = values[i];
            doc.Notify(new DocChange(ChangeKind.TransformChanged, _ids[i]));
        }
    }
}
