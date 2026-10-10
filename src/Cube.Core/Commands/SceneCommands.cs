using System.Numerics;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>이미지 플레인 속성 변경(Properties의 Image Plane 그룹). 복사본 전체를 받아 덮어쓴다.</summary>
public sealed class SetImagePlaneCommand : ICommand
{
    private readonly NodeId _node; private readonly ImagePlaneShape _after; private ImagePlaneShape? _before;
    public string Name => "Set Image Plane";
    public SetImagePlaneCommand(NodeId node, ImagePlaneShape after) { _node = node; _after = after.Clone(); }
    public void Do(Document doc)
    {
        var ip = doc.Get(_node).Shape as ImagePlaneShape ?? throw new InvalidOperationException("not an image plane");
        _before ??= ip.Clone();
        Apply(doc, ip, _after);
    }
    public void Undo(Document doc) { if (_before != null && doc.Get(_node).Shape is ImagePlaneShape ip) Apply(doc, ip, _before); }
    private void Apply(Document doc, ImagePlaneShape target, ImagePlaneShape src)
    {
        target.ImagePath = src.ImagePath; target.Width = src.Width; target.Height = src.Height; target.Opacity = src.Opacity; target.OnlyView = src.OnlyView; target.Locked = src.Locked;
        doc.Notify(new DocChange(ChangeKind.ImagePlaneChanged, _node));
    }
}

/// <summary>라이트 속성 변경.</summary>
/// <remarks>Properties의 Light 그룹이 편집한 결과 전체(Type/Color/Intensity/Range/SpotAngle)를 복사본으로 받아 덮어쓴다.</remarks>
public sealed class SetLightCommand : ICommand
{
    /// <summary>라이트 노드, 적용할 값(복사본), Undo용 원래 값.</summary>
    private readonly NodeId _node; private readonly LightShape _after; private LightShape? _before;
    /// <summary>명령 이름.</summary>
    public string Name => "Set Light";
    /// <summary>적용할 라이트 값을 복제해 보관한다.</summary>
    public SetLightCommand(NodeId node, LightShape after) { _node = node; _after = after.Clone(); }
    /// <summary>처음 실행 때 원래 값을 복제해 두고 새 값을 적용한다.</summary>
    public void Do(Document doc)
    {
        var l = doc.Get(_node).Shape as LightShape ?? throw new InvalidOperationException("not a light");
        _before ??= l.Clone();
        Apply(doc, l, _after);
    }
    /// <summary>원래 값으로 되돌린다.</summary>
    public void Undo(Document doc) { if (_before != null && doc.Get(_node).Shape is LightShape l) Apply(doc, l, _before); }
    /// <summary>필드를 복사하고 LightChanged를 통지한다(셰이프 객체는 교체하지 않음).</summary>
    private void Apply(Document doc, LightShape target, LightShape src)
    {
        target.Type = src.Type; target.Color = src.Color; target.Intensity = src.Intensity; target.Range = src.Range; target.SpotAngle = src.SpotAngle;
        doc.Notify(new DocChange(ChangeKind.LightChanged, _node));
    }
}

/// <summary>머티리얼 추가.</summary>
public sealed class AddMaterialCommand : ICommand
{
    /// <summary>추가할 머티리얼(Redo에서도 같은 객체·ID).</summary>
    private readonly MaterialDef _mat;
    /// <summary>추가되는 머티리얼.</summary>
    public MaterialDef Material => _mat;
    /// <summary>명령 이름.</summary>
    public string Name => "New Material";
    /// <summary>머티리얼을 받는다(ID 0이면 실행 때 배정).</summary>
    public AddMaterialCommand(MaterialDef mat) { _mat = mat; }
    /// <summary>ID가 없으면 새로 배정하고 목록에 추가한 뒤 MaterialChanged(노드 없음)를 통지한다.</summary>
    public void Do(Document doc)
    {
        if (_mat.Id == 0) _mat.Id = doc.NextMaterialId();
        doc.Materials.Add(_mat);
        doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None));
    }
    /// <summary>목록에서 제거한다.</summary>
    public void Undo(Document doc) { doc.Materials.Remove(_mat); doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None)); }
}

/// <summary>머티리얼 삭제(할당된 오브젝트는 기본 머티리얼로).</summary>
public sealed class DeleteMaterialCommand : ICommand
{
    /// <summary>삭제할 머티리얼 ID, 제거한 객체, 원래 목록 위치.</summary>
    private readonly int _id; private MaterialDef? _mat; private int _index;
    /// <summary>삭제 때 기본 머티리얼(0)로 바뀐 노드들(Undo에서 다시 할당).</summary>
    private readonly List<NodeId> _assigned = new();
    /// <summary>명령 이름.</summary>
    public string Name => "Delete Material";
    /// <summary>삭제할 머티리얼 ID를 받는다.</summary>
    public DeleteMaterialCommand(int id) { _id = id; }
    /// <summary>머티리얼을 목록에서 빼고, 이를 쓰던 노드를 lambert1(0)로 돌린다.</summary>
    public void Do(Document doc)
    {
        _mat ??= doc.Materials.First(m => m.Id == _id);
        _index = doc.Materials.IndexOf(_mat);
        doc.Materials.Remove(_mat);
        // 할당된 노드 기록 후 기본으로 교체(노드마다 통지).
        _assigned.Clear();
        foreach (var n in doc.Nodes.Values) if (n.MaterialId == _id) { _assigned.Add(n.Id); n.MaterialId = 0; doc.Notify(new DocChange(ChangeKind.MaterialChanged, n.Id)); }
        doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None));
    }
    /// <summary>원래 위치에 머티리얼을 되돌리고 할당도 복원한다.</summary>
    public void Undo(Document doc)
    {
        doc.Materials.Insert(Math.Clamp(_index, 0, doc.Materials.Count), _mat!);
        foreach (var id in _assigned) { var n = doc.Find(id); if (n != null) { n.MaterialId = _id; doc.Notify(new DocChange(ChangeKind.MaterialChanged, id)); } }
        doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None));
    }
}

/// <summary>머티리얼 속성 변경.</summary>
/// <remarks>편집된 머티리얼 전체를 복사본으로 받아 <c>CopyFrom</c>으로 덮어쓴다(객체 동일성 유지 → 캐시/참조가 깨지지 않음).</remarks>
public sealed class SetMaterialCommand : ICommand
{
    /// <summary>대상 머티리얼 ID, 새 값, Undo용 원래 값.</summary>
    private readonly int _id; private readonly MaterialDef _after; private MaterialDef? _before;
    /// <summary>명령 이름.</summary>
    public string Name => "Edit Material";
    /// <summary>새 값을 복제해 보관한다.</summary>
    public SetMaterialCommand(int id, MaterialDef after) { _id = id; _after = after.Clone(); }
    /// <summary>원래 값을 한 번 복제해 두고 새 값을 복사해 넣는다.</summary>
    public void Do(Document doc)
    {
        var m = doc.Materials.First(x => x.Id == _id);
        _before ??= m.Clone();
        m.CopyFrom(_after);
        NotifyUsers(doc);
    }
    /// <summary>원래 값으로 복원한다.</summary>
    public void Undo(Document doc) { var m = doc.Materials.First(x => x.Id == _id); if (_before != null) m.CopyFrom(_before); NotifyUsers(doc); }
    /// <summary>머티리얼 목록 변경과, 이 머티리얼을 쓰는 각 노드의 변경을 통지한다(뷰포트 셰이더 갱신).</summary>
    private void NotifyUsers(Document doc)
    {
        doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None));
        foreach (var n in doc.Nodes.Values) if (n.MaterialId == _id) doc.Notify(new DocChange(ChangeKind.MaterialChanged, n.Id));
    }
}

/// <summary>오브젝트에 머티리얼 할당(0 = 기본).</summary>
public sealed class AssignMaterialCommand : ICommand
{
    /// <summary>대상 노드들, 할당할 머티리얼 ID, 노드별 이전 ID.</summary>
    private readonly NodeId[] _nodes; private readonly int _mat; private int[]? _before;
    /// <summary>명령 이름.</summary>
    public string Name => "Assign Material";
    /// <summary>대상 노드와 머티리얼 ID를 받는다.</summary>
    public AssignMaterialCommand(IEnumerable<NodeId> nodes, int material) { _nodes = nodes.ToArray(); _mat = material; }
    /// <summary>처음 실행 때 이전 ID를 기록하고 모든 대상에 새 ID를 넣는다.</summary>
    public void Do(Document doc)
    {
        _before ??= _nodes.Select(id => doc.Find(id)?.MaterialId ?? 0).ToArray();
        foreach (var id in _nodes) { var n = doc.Find(id); if (n == null) continue; n.MaterialId = _mat; doc.Notify(new DocChange(ChangeKind.MaterialChanged, id)); }
    }
    /// <summary>노드별 이전 ID로 되돌린다.</summary>
    public void Undo(Document doc)
    {
        for (int i = 0; i < _nodes.Length; i++) { var n = doc.Find(_nodes[i]); if (n == null) continue; n.MaterialId = _before![i]; doc.Notify(new DocChange(ChangeKind.MaterialChanged, _nodes[i])); }
    }
}

/// <summary>Insert Joint: 조인트 parent와 그 자식 child 사이(비율 t)에 새 조인트를 끼운다. 자식의 월드 위치는 유지된다.</summary>
/// <remarks>새 조인트는 회전 0·스케일 1이고 부모 공간 위치만 가진다. 자식은 새 조인트 아래로 옮긴 뒤 로컬을 <c>FromMatrix(childWorld · inv(jointWorld), pivot)</c>로 다시 계산한다.</remarks>
public sealed class InsertJointCommand : ICommand
{
    /// <summary>부모 조인트, 자식 조인트, 사이 비율(0.01~0.99로 클램프).</summary>
    private readonly NodeId _parent, _child; private readonly float _t;
    /// <summary>새 조인트(첫 실행에 생성), 자식의 원래 로컬, 부모 아래에서 자식이 있던 인덱스.</summary>
    private SceneNode? _joint; private Transform3 _childBefore; private int _childIndex;
    /// <summary>실행 전 선택.</summary>
    private SelectionSnapshot? _selBefore;
    /// <summary>명령 이름.</summary>
    public string Name => "Insert Joint";
    /// <summary>삽입된 조인트.</summary>
    public SceneNode? Joint => _joint;
    /// <summary>t가 양 끝에 붙지 않도록 0.01~0.99로 제한한다.</summary>
    public InsertJointCommand(NodeId parent, NodeId child, float t) { _parent = parent; _child = child; _t = Math.Clamp(t, 0.01f, 0.99f); }

    /// <summary>새 조인트를 자식 자리에 끼우고 자식을 그 아래로 옮긴다(자식 월드 유지).</summary>
    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        var parent = doc.Get(_parent); var child = doc.Get(_child);
        var pw = parent.WorldMatrix; var cw = child.WorldMatrix;
        if (_joint == null)
        {
            // 두 조인트 월드 위치 사이를 보간한 점을 부모 로컬로 바꿔 새 조인트 위치로 쓴다(반지름은 부모 것을 따름).
            var pos = Vector3.Lerp(pw.Translation, cw.Translation, _t);
            Matrix4x4.Invert(pw, out var pinv);
            _joint = new SceneNode { Name = doc.UniqueName("joint1"), Shape = new JointShape { Radius = parent.Joint?.Radius ?? 0.08f }, Local = new Transform3(Vector3.Transform(pos, pinv), Vector3.Zero, Vector3.One) };
            _childBefore = child.Local;
        }
        // 자식이 있던 인덱스에 새 조인트를 넣어 형제 순서를 유지한다.
        _childIndex = parent.Children.IndexOf(child);
        doc.AddNode(_joint, parent, _childIndex);
        // 자식을 새 조인트 밑으로 옮기고 월드가 그대로가 되도록 로컬을 다시 계산(피벗 유지).
        Matrix4x4.Invert(_joint.WorldMatrix, out var jinv);
        doc.Reparent(child, _joint);
        child.Local = Transform3.FromMatrix(cw * jinv, child.Local.Pivot);
        doc.Notify(new DocChange(ChangeKind.TransformChanged, child.Id));
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(new[] { _joint.Id });
    }

    /// <summary>자식을 원래 부모·인덱스·로컬로 되돌리고 새 조인트를 제거한다.</summary>
    public void Undo(Document doc)
    {
        var parent = doc.Get(_parent); var child = doc.Get(_child);
        doc.Reparent(child, parent, _childIndex);
        child.Local = _childBefore;
        doc.Notify(new DocChange(ChangeKind.TransformChanged, child.Id));
        doc.RemoveNode(_joint!);
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}
