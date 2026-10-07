using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Commands;

public enum HistoryParamKind { Float, Int, Vector3, Bool }

/// <summary>히스토리 항목의 편집 가능한 파라미터 하나. 값은 Vector3에 담는다(Float/Int/Bool은 X).</summary>
public sealed class HistoryParam
{
    public string Name = "";
    public HistoryParamKind Kind = HistoryParamKind.Float;
    public Vector3 Value;
    public float Min = float.MinValue, Max = float.MaxValue, Step = 0.001f;

    public float Float { get => Value.X; set => Value = new Vector3(value, 0, 0); }
    public int Int { get => (int)MathF.Round(Value.X); set => Value = new Vector3(value, 0, 0); }
    public bool Bool { get => Value.X > 0.5f; set => Value = new Vector3(value ? 1 : 0, 0, 0); }

    public HistoryParam Clone() => new() { Name = Name, Kind = Kind, Value = Value, Min = Min, Max = Max, Step = Step };

    public static HistoryParam F(string name, float v, float min = float.MinValue, float max = float.MaxValue, float step = 0.001f) => new() { Name = name, Kind = HistoryParamKind.Float, Value = new Vector3(v, 0, 0), Min = min, Max = max, Step = step };
    public static HistoryParam I(string name, int v, int min = int.MinValue, int max = int.MaxValue) => new() { Name = name, Kind = HistoryParamKind.Int, Value = new Vector3(v, 0, 0), Min = min, Max = max, Step = 1 };
    public static HistoryParam V(string name, Vector3 v, float step = 0.001f) => new() { Name = name, Kind = HistoryParamKind.Vector3, Value = v, Step = step };
}

public sealed class HistoryParams
{
    public readonly List<HistoryParam> Items = new();
    public HistoryParams() { }
    public HistoryParams(params HistoryParam[] items) { Items.AddRange(items); }
    public HistoryParam this[string name] => Items.First(p => p.Name == name);
    public float Float(string name) => this[name].Float;
    public int Int(string name) => this[name].Int;
    public Vector3 Vec(string name) => this[name].Value;
    public HistoryParams Clone() { var c = new HistoryParams(); foreach (var p in Items) c.Items.Add(p.Clone()); return c; }
    public bool ValuesEqual(HistoryParams o)
    {
        if (o.Items.Count != Items.Count) return false;
        for (int i = 0; i < Items.Count; i++) if (Items[i].Value != o.Items[i].Value) return false;
        return true;
    }
}

/// <summary>
/// 구성 이력(Maya construction history) 항목. 적용 직전 메시 스냅샷과, 파라미터로 다시 실행할 수 있는 연산을 가진다.
/// 파라미터를 바꾸면 그 항목부터 끝까지 다시 실행한다(ID는 슬롯 인덱스로 결정적이므로 뒤 항목의 컴포넌트 참조가 유지된다).
/// </summary>
public sealed class HistoryEntry
{
    public string Name = "";
    public HistoryParams Params = new();
    /// <summary>이 항목 적용 직전의 메시. 재평가 시 교체된다.</summary>
    public PolyMesh Before = new();
    /// <summary>메시를 제자리에서 수정한다. false면 변경 없음.</summary>
    public Func<PolyMesh, HistoryParams, bool> Replay = (_, _) => false;
    public bool Editable => Params.Items.Count > 0;
}

public static class HistoryReplay
{
    /// <summary>entries[from]부터 끝까지 다시 실행한다. 메시는 entries[from].Before로 되돌린 뒤 시작한다.</summary>
    public static void Rebuild(PolyMesh mesh, List<HistoryEntry> entries, int from)
    {
        if (from < 0 || from >= entries.Count) return;
        mesh.CopyFrom(entries[from].Before);
        for (int k = from; k < entries.Count; k++)
        {
            if (k > from) entries[k].Before = mesh.Clone();
            entries[k].Replay(mesh, entries[k].Params);
        }
        MeshNormals.Recompute(mesh);
        mesh.BumpTopology();
    }
}

/// <summary>히스토리 항목의 파라미터를 바꾸고 그 항목부터 다시 실행한다.</summary>
public sealed class EditHistoryCommand : ICommand
{
    private readonly NodeId _node; private readonly int _index;
    private readonly HistoryParams _newParams; private HistoryParams? _oldParams;
    private PolyMesh? _meshBefore, _meshAfter;
    private List<PolyMesh>? _beforeSnapshots; // 뒤 항목들의 Before(재평가로 바뀌므로 보관)
    public string Name { get; }

    public EditHistoryCommand(NodeId node, int index, HistoryParams newParams, string? name = null)
    {
        _node = node; _index = index; _newParams = newParams.Clone(); Name = name ?? "Edit History";
    }

    public void Do(Document doc)
    {
        var n = doc.Get(_node); var shape = n.MeshShape ?? throw new InvalidOperationException("no mesh");
        var entries = shape.History;
        if (_index < 0 || _index >= entries.Count) return;
        if (_meshAfter == null)
        {
            _oldParams = entries[_index].Params.Clone();
            _meshBefore = shape.Mesh.Clone();
            _beforeSnapshots = entries.Skip(_index + 1).Select(e => e.Before).ToList();
            entries[_index].Params = _newParams.Clone();
            doc.Selection.GetComponents(_node).ClearAll();
            HistoryReplay.Rebuild(shape.Mesh, entries, _index);
            _meshAfter = shape.Mesh.Clone();
        }
        else
        {
            entries[_index].Params = _newParams.Clone();
            doc.Selection.GetComponents(_node).ClearAll();
            shape.Mesh.CopyFrom(_meshAfter);
            // 뒤 항목 Before 재계산(결정적이므로 다시 실행)
            HistoryReplay.Rebuild(shape.Mesh, entries, _index);
        }
        doc.Notify(new DocChange(ChangeKind.MeshTopology, _node));
    }

    public void Undo(Document doc)
    {
        var shape = doc.Get(_node).MeshShape!;
        var entries = shape.History;
        if (_oldParams != null && _index < entries.Count) entries[_index].Params = _oldParams.Clone();
        if (_beforeSnapshots != null) for (int i = 0; i < _beforeSnapshots.Count && _index + 1 + i < entries.Count; i++) entries[_index + 1 + i].Before = _beforeSnapshots[i];
        doc.Selection.GetComponents(_node).ClearAll();
        if (_meshBefore != null) shape.Mesh.CopyFrom(_meshBefore);
        doc.Notify(new DocChange(ChangeKind.MeshTopology, _node));
    }
}

/// <summary>Edit → Delete History: 구성 이력을 비운다(메시는 그대로).</summary>
public sealed class DeleteHistoryCommand : ICommand
{
    private readonly NodeId[] _nodes;
    private readonly Dictionary<NodeId, List<HistoryEntry>> _saved = new();
    public string Name => "Delete History";
    public DeleteHistoryCommand(IEnumerable<NodeId> nodes) { _nodes = nodes.ToArray(); }

    public void Do(Document doc)
    {
        foreach (var id in _nodes)
        {
            var shape = doc.Find(id)?.MeshShape; if (shape == null) continue;
            _saved[id] = new List<HistoryEntry>(shape.History);
            shape.History.Clear();
            doc.Notify(new DocChange(ChangeKind.HistoryChanged, id));
        }
    }

    public void Undo(Document doc)
    {
        foreach (var (id, list) in _saved)
        {
            var shape = doc.Find(id)?.MeshShape; if (shape == null) continue;
            shape.History.Clear(); shape.History.AddRange(list);
            doc.Notify(new DocChange(ChangeKind.HistoryChanged, id));
        }
    }
}

/// <summary>컴포넌트 변형(이동/회전/스케일)의 히스토리 표현: 고정 데이터(피벗, 축, 기저, 메시 월드)와 편집 가능한 파라미터.</summary>
public sealed class ComponentTransformOp
{
    public enum Kind { Move, Rotate, Scale }
    public Kind Type;
    public Vector3 Pivot;              // 월드
    public Vector3 Axis = Vector3.UnitY; // 회전축(월드)
    public Vector3 BasisX = Vector3.UnitX, BasisY = Vector3.UnitY, BasisZ = Vector3.UnitZ; // 스케일 기저(월드)
    public Matrix4x4 MeshWorld = Matrix4x4.Identity;

    public HistoryParams DefaultParams(Vector3 translate, float angleDeg, Vector3 scale) => Type switch
    {
        Kind.Move => new HistoryParams(HistoryParam.V("Translate", translate)),
        Kind.Rotate => new HistoryParams(HistoryParam.F("Angle", angleDeg, step: 0.1f)),
        _ => new HistoryParams(HistoryParam.V("Scale", scale)),
    };

    /// <summary>파라미터로 월드 델타 행렬을 만든다.</summary>
    public Matrix4x4 WorldMatrix(HistoryParams p)
    {
        switch (Type)
        {
            case Kind.Move: return Matrix4x4.CreateTranslation(p.Vec("Translate"));
            case Kind.Rotate:
                {
                    var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Axis), p.Float("Angle") * MathF.PI / 180f);
                    return Matrix4x4.CreateTranslation(-Pivot) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(Pivot);
                }
            default:
                {
                    var s = p.Vec("Scale");
                    var b = new Matrix4x4(BasisX.X, BasisX.Y, BasisX.Z, 0, BasisY.X, BasisY.Y, BasisY.Z, 0, BasisZ.X, BasisZ.Y, BasisZ.Z, 0, 0, 0, 0, 1);
                    return Matrix4x4.CreateTranslation(-Pivot) * Matrix4x4.Transpose(b) * Matrix4x4.CreateScale(s) * b * Matrix4x4.CreateTranslation(Pivot);
                }
        }
    }

    /// <summary>메시 로컬 델타 행렬.</summary>
    public Matrix4x4 LocalMatrix(HistoryParams p)
    {
        Matrix4x4.Invert(MeshWorld, out var inv);
        return MeshWorld * WorldMatrix(p) * inv;
    }
}
