using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>미리 만든 노드를 문서에 넣고 선택한다. Undo 시 노드 ID는 유지된다.</summary>
/// <remarks>Undo 때 제거된 위치(형제 인덱스)를 기억해 Redo 때 같은 자리에 다시 넣는다.</remarks>
public sealed class AddNodeCommand : ICommand
{
    /// <summary>추가할 노드(Redo에서도 같은 객체).</summary>
    private readonly SceneNode _node;
    /// <summary>부모 노드 ID(None이면 루트).</summary>
    private readonly NodeId _parent;
    /// <summary>실행 전 선택.</summary>
    private SelectionSnapshot? _selBefore;
    /// <summary>삽입 위치(-1 = 끝에 추가). Undo가 RemoveNode의 반환값으로 채운다.</summary>
    private int _index = -1;

    /// <summary>명령 이름(예: "Create Cube").</summary>
    public string Name { get; }
    /// <summary>추가되는 노드.</summary>
    public SceneNode Node => _node;

    /// <summary>이름, 노드, 부모(기본 루트)를 받는다.</summary>
    public AddNodeCommand(string name, SceneNode node, NodeId parent = default)
    {
        Name = name; _node = node; _parent = parent;
    }

    /// <summary>노드를 부모 아래 지정 위치에 추가하고 오브젝트 모드로 그 노드만 선택한다.</summary>
    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        var parent = _parent.IsNone ? doc.Root : doc.Get(_parent);
        doc.AddNode(_node, parent, _index);
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(new[] { _node.Id });
    }

    /// <summary>노드를 제거하고(위치 기억) 선택을 복원한다.</summary>
    public void Undo(Document doc)
    {
        _index = doc.RemoveNode(_node);
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}

/// <summary>프리미티브 생성. Maya처럼 원점에 만들고 pCube1 식 이름을 붙인다.</summary>
/// <remarks>크기 단위는 m(1 = 1m). 각 메서드는 메시를 만들고 <see cref="AddNodeCommand"/>를 돌려줄 뿐 실행하지 않는다(호출자가 Push).</remarks>
public static class CreatePrimitiveCommand
{
    /// <summary>폭·높이·깊이 상자.</summary>
    public static AddNodeCommand Cube(Document doc, float w = 1, float h = 1, float d = 1) => Make(doc, "pCube", MeshBuilder.Cube(w, h, d));
    /// <summary>XZ 평면(sx×sz 분할).</summary>
    public static AddNodeCommand Plane(Document doc, float w = 1, float d = 1, int sx = 1, int sz = 1) => Make(doc, "pPlane", MeshBuilder.Plane(w, d, sx, sz));
    /// <summary>원기둥(반지름, 높이, 둘레 분할).</summary>
    public static AddNodeCommand Cylinder(Document doc, float r = 0.5f, float h = 1, int seg = 20) => Make(doc, "pCylinder", MeshBuilder.Cylinder(r, h, seg));
    /// <summary>원뿔(반지름, 높이, 둘레 분할).</summary>
    public static AddNodeCommand Cone(Document doc, float r = 0.5f, float h = 1, int seg = 20) => Make(doc, "pCone", MeshBuilder.Cone(r, h, seg));
    /// <summary>UV 구(둘레 분할, 위도 고리 수).</summary>
    public static AddNodeCommand Sphere(Document doc, float r = 0.5f, int seg = 20, int rings = 10) => Make(doc, "pSphere", MeshBuilder.Sphere(r, seg, rings));
    /// <summary>토러스(큰 반지름, 단면 반지름, 둘레 분할, 단면 분할).</summary>
    public static AddNodeCommand Torus(Document doc, float r = 0.5f, float sr = 0.2f, int seg = 20, int sec = 12) => Make(doc, "pTorus", MeshBuilder.Torus(r, sr, seg, sec));

    /// <summary>유일한 이름("pCube1" 등)의 메시 노드를 만들어 추가 명령으로 감싼다. 명령 이름은 접두사 p를 뗀 "Create Cube" 식.</summary>
    private static AddNodeCommand Make(Document doc, string baseName, PolyMesh mesh)
    {
        var node = new SceneNode { Name = doc.UniqueName(baseName + "1"), Shape = new MeshShape(mesh) };
        return new AddNodeCommand("Create " + baseName.TrimStart('p'), node);
    }
}

/// <summary>노드 삭제(하위 포함). Undo 시 같은 부모·인덱스로 복원.</summary>
/// <remarks>생성 시점에 문서를 보고 삭제 목록(노드, 부모, 형제 인덱스)을 확정한다. 노드 객체를 그대로 들고 있어 Undo 때 ID와 하위 트리가 그대로 돌아온다.</remarks>
public sealed class DeleteNodesCommand : ICommand
{
    /// <summary>삭제할 노드와 원래 부모(None = 루트)·형제 인덱스. 인덱스 내림차순 정렬.</summary>
    private readonly List<(SceneNode node, NodeId parent, int index)> _items = new();
    /// <summary>실행 전 선택.</summary>
    private SelectionSnapshot? _selBefore;
    /// <summary>명령 이름.</summary>
    public string Name => "Delete";

    /// <summary>삭제 대상을 정리한다(조상이 함께 지워지는 노드는 중복이라 뺀다).</summary>
    public DeleteNodesCommand(Document doc, IEnumerable<NodeId> ids)
    {
        // 조상이 포함된 노드는 제외(조상 삭제에 포함됨)
        var set = new HashSet<NodeId>(ids);
        foreach (var id in set)
        {
            var n = doc.Find(id); if (n == null) continue;
            // 부모 체인을 따라 올라가며 선택된 조상이 있는지 확인.
            bool ancestorSelected = false;
            for (var p = n.Parent; p != null && !p.IsRoot; p = p.Parent) if (set.Contains(p.Id)) { ancestorSelected = true; break; }
            if (!ancestorSelected) _items.Add((n, n.Parent!.IsRoot ? NodeId.None : n.Parent.Id, n.Parent.Children.IndexOf(n)));
        }
        _items.Sort((a, b) => b.index.CompareTo(a.index)); // 뒤에서부터 지워 인덱스 보존
    }

    /// <summary>지울 노드가 없으면 true.</summary>
    public bool IsEmpty => _items.Count == 0;

    /// <summary>노드를 제거하고 선택을 모두 해제한다.</summary>
    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        foreach (var (n, _, _) in _items) doc.RemoveNode(n);
        doc.Selection.ClearAll();
    }

    /// <summary>삭제의 역순(인덱스 오름차순)으로 원래 부모·위치에 되돌려 넣는다.</summary>
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

/// <summary>노드 이름 변경(Outliner/Properties).</summary>
public sealed class RenameNodeCommand : ICommand
{
    /// <summary>대상 노드와 변경 전/후 이름.</summary>
    private readonly NodeId _id; private readonly string _before, _after;
    /// <summary>명령 이름.</summary>
    public string Name => "Rename";
    /// <summary>현재 이름을 before로 기억한다(중복 이름 처리는 호출자 책임).</summary>
    public RenameNodeCommand(Document doc, NodeId id, string newName) { _id = id; _before = doc.Get(id).Name; _after = newName; }
    /// <summary>새 이름을 넣고 NodeRenamed를 통지한다.</summary>
    public void Do(Document doc) { doc.Get(_id).Name = _after; doc.Notify(new DocChange(ChangeKind.NodeRenamed, _id)); }
    /// <summary>이전 이름으로 되돌린다.</summary>
    public void Undo(Document doc) { doc.Get(_id).Name = _before; doc.Notify(new DocChange(ChangeKind.NodeRenamed, _id)); }
}

/// <summary>노드들의 로컬 트랜스폼 변경. 드래그는 alreadyApplied로 넣는다.</summary>
/// <remarks>노드별 전/후 <see cref="Transform3"/>(TRS + Pivot)를 통째로 교체한다. Action Popup이 Ids/Before/After로 델타를 계산한다.</remarks>
public sealed class TransformNodesCommand : ICommand
{
    /// <summary>대상 노드와 같은 순서의 전/후 로컬 트랜스폼.</summary>
    private readonly NodeId[] _ids; private readonly Transform3[] _before, _after;
    /// <summary>명령 이름(Move/Rotate/Scale 등).</summary>
    public string Name { get; }
    /// <summary>대상 노드 ID 목록.</summary>
    public IReadOnlyList<NodeId> Ids => _ids;
    /// <summary>변경 전 로컬 트랜스폼.</summary>
    public IReadOnlyList<Transform3> Before => _before;
    /// <summary>변경 후 로컬 트랜스폼.</summary>
    public IReadOnlyList<Transform3> After => _after;

    /// <summary>배열들은 같은 길이·순서여야 한다.</summary>
    public TransformNodesCommand(string name, NodeId[] ids, Transform3[] before, Transform3[] after)
    {
        Name = name; _ids = ids; _before = before; _after = after;
    }

    /// <summary>모든 노드의 전/후가 같으면 true.</summary>
    public bool IsNoop { get { for (int i = 0; i < _ids.Length; i++) if (_before[i] != _after[i]) return false; return true; } }

    /// <summary>변경 후 값을 적용한다.</summary>
    public void Do(Document doc) => Apply(doc, _after);
    /// <summary>변경 전 값을 적용한다.</summary>
    public void Undo(Document doc) => Apply(doc, _before);

    /// <summary>각 노드에 로컬 트랜스폼을 넣고 TransformChanged를 통지한다(없어진 노드는 건너뜀).</summary>
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
