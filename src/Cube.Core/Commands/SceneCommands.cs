using System.Numerics;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>라이트 속성 변경.</summary>
public sealed class SetLightCommand : ICommand
{
    private readonly NodeId _node; private readonly LightShape _after; private LightShape? _before;
    public string Name => "Set Light";
    public SetLightCommand(NodeId node, LightShape after) { _node = node; _after = after.Clone(); }
    public void Do(Document doc)
    {
        var l = doc.Get(_node).Shape as LightShape ?? throw new InvalidOperationException("not a light");
        _before ??= l.Clone();
        Apply(doc, l, _after);
    }
    public void Undo(Document doc) { if (_before != null && doc.Get(_node).Shape is LightShape l) Apply(doc, l, _before); }
    private void Apply(Document doc, LightShape target, LightShape src)
    {
        target.Type = src.Type; target.Color = src.Color; target.Intensity = src.Intensity; target.Range = src.Range; target.SpotAngle = src.SpotAngle;
        doc.Notify(new DocChange(ChangeKind.LightChanged, _node));
    }
}

/// <summary>머티리얼 추가.</summary>
public sealed class AddMaterialCommand : ICommand
{
    private readonly MaterialDef _mat;
    public MaterialDef Material => _mat;
    public string Name => "New Material";
    public AddMaterialCommand(MaterialDef mat) { _mat = mat; }
    public void Do(Document doc)
    {
        if (_mat.Id == 0) _mat.Id = doc.NextMaterialId();
        doc.Materials.Add(_mat);
        doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None));
    }
    public void Undo(Document doc) { doc.Materials.Remove(_mat); doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None)); }
}

/// <summary>머티리얼 삭제(할당된 오브젝트는 기본 머티리얼로).</summary>
public sealed class DeleteMaterialCommand : ICommand
{
    private readonly int _id; private MaterialDef? _mat; private int _index;
    private readonly List<NodeId> _assigned = new();
    public string Name => "Delete Material";
    public DeleteMaterialCommand(int id) { _id = id; }
    public void Do(Document doc)
    {
        _mat ??= doc.Materials.First(m => m.Id == _id);
        _index = doc.Materials.IndexOf(_mat);
        doc.Materials.Remove(_mat);
        _assigned.Clear();
        foreach (var n in doc.Nodes.Values) if (n.MaterialId == _id) { _assigned.Add(n.Id); n.MaterialId = 0; doc.Notify(new DocChange(ChangeKind.MaterialChanged, n.Id)); }
        doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None));
    }
    public void Undo(Document doc)
    {
        doc.Materials.Insert(Math.Clamp(_index, 0, doc.Materials.Count), _mat!);
        foreach (var id in _assigned) { var n = doc.Find(id); if (n != null) { n.MaterialId = _id; doc.Notify(new DocChange(ChangeKind.MaterialChanged, id)); } }
        doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None));
    }
}

/// <summary>머티리얼 속성 변경.</summary>
public sealed class SetMaterialCommand : ICommand
{
    private readonly int _id; private readonly MaterialDef _after; private MaterialDef? _before;
    public string Name => "Edit Material";
    public SetMaterialCommand(int id, MaterialDef after) { _id = id; _after = after.Clone(); }
    public void Do(Document doc)
    {
        var m = doc.Materials.First(x => x.Id == _id);
        _before ??= m.Clone();
        m.CopyFrom(_after);
        NotifyUsers(doc);
    }
    public void Undo(Document doc) { var m = doc.Materials.First(x => x.Id == _id); if (_before != null) m.CopyFrom(_before); NotifyUsers(doc); }
    private void NotifyUsers(Document doc)
    {
        doc.Notify(new DocChange(ChangeKind.MaterialChanged, NodeId.None));
        foreach (var n in doc.Nodes.Values) if (n.MaterialId == _id) doc.Notify(new DocChange(ChangeKind.MaterialChanged, n.Id));
    }
}

/// <summary>오브젝트에 머티리얼 할당(0 = 기본).</summary>
public sealed class AssignMaterialCommand : ICommand
{
    private readonly NodeId[] _nodes; private readonly int _mat; private int[]? _before;
    public string Name => "Assign Material";
    public AssignMaterialCommand(IEnumerable<NodeId> nodes, int material) { _nodes = nodes.ToArray(); _mat = material; }
    public void Do(Document doc)
    {
        _before ??= _nodes.Select(id => doc.Find(id)?.MaterialId ?? 0).ToArray();
        foreach (var id in _nodes) { var n = doc.Find(id); if (n == null) continue; n.MaterialId = _mat; doc.Notify(new DocChange(ChangeKind.MaterialChanged, id)); }
    }
    public void Undo(Document doc)
    {
        for (int i = 0; i < _nodes.Length; i++) { var n = doc.Find(_nodes[i]); if (n == null) continue; n.MaterialId = _before![i]; doc.Notify(new DocChange(ChangeKind.MaterialChanged, _nodes[i])); }
    }
}

/// <summary>Insert Joint: 조인트 parent와 그 자식 child 사이(비율 t)에 새 조인트를 끼운다. 자식의 월드 위치는 유지된다.</summary>
public sealed class InsertJointCommand : ICommand
{
    private readonly NodeId _parent, _child; private readonly float _t;
    private SceneNode? _joint; private Transform3 _childBefore; private int _childIndex;
    private SelectionSnapshot? _selBefore;
    public string Name => "Insert Joint";
    public SceneNode? Joint => _joint;
    public InsertJointCommand(NodeId parent, NodeId child, float t) { _parent = parent; _child = child; _t = Math.Clamp(t, 0.01f, 0.99f); }

    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        var parent = doc.Get(_parent); var child = doc.Get(_child);
        var pw = parent.WorldMatrix; var cw = child.WorldMatrix;
        if (_joint == null)
        {
            var pos = Vector3.Lerp(pw.Translation, cw.Translation, _t);
            Matrix4x4.Invert(pw, out var pinv);
            _joint = new SceneNode { Name = doc.UniqueName("joint1"), Shape = new JointShape { Radius = parent.Joint?.Radius ?? 0.08f }, Local = new Transform3(Vector3.Transform(pos, pinv), Vector3.Zero, Vector3.One) };
            _childBefore = child.Local;
        }
        _childIndex = parent.Children.IndexOf(child);
        doc.AddNode(_joint, parent, _childIndex);
        Matrix4x4.Invert(_joint.WorldMatrix, out var jinv);
        doc.Reparent(child, _joint);
        child.Local = Transform3.FromMatrix(cw * jinv, child.Local.Pivot);
        doc.Notify(new DocChange(ChangeKind.TransformChanged, child.Id));
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(new[] { _joint.Id });
    }

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
