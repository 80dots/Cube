using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>히스토리 파라미터의 값 종류. UI(Properties History 그룹, Action Popup)가 어떤 편집 위젯을 쓸지 결정한다.</summary>
/// <remarks>Float = 실수 하나(Value.X), Int = 정수(Value.X 반올림), Vector3 = 3성분 벡터(Value 전체), Bool = 불리언(Value.X &gt; 0.5).</remarks>
public enum HistoryParamKind { Float, Int, Vector3, Bool }

/// <summary>히스토리 항목의 편집 가능한 파라미터 하나. 값은 Vector3에 담는다(Float/Int/Bool은 X).</summary>
/// <remarks>모든 종류를 Vector3 하나에 담아 비교·복사·Undo 스냅샷을 단순하게 한다. Min/Max/Step은 스핀박스 범위와 증분이다.</remarks>
public sealed class HistoryParam
{
    /// <summary>파라미터 이름(조회 키이자 UI 라벨).</summary>
    public string Name = "";
    /// <summary>값 종류.</summary>
    public HistoryParamKind Kind = HistoryParamKind.Float;
    /// <summary>실제 값. 스칼라 종류는 X만 쓰고 Y/Z는 0.</summary>
    public Vector3 Value;
    /// <summary>스핀박스 최소/최대값과 증분.</summary>
    public float Min = float.MinValue, Max = float.MaxValue, Step = 0.001f;

    /// <summary>실수 보기(쓰기 시 Y/Z는 0으로).</summary>
    public float Float { get => Value.X; set => Value = new Vector3(value, 0, 0); }
    /// <summary>정수 보기(X를 반올림).</summary>
    public int Int { get => (int)MathF.Round(Value.X); set => Value = new Vector3(value, 0, 0); }
    /// <summary>불리언 보기(X &gt; 0.5면 true, 쓰기는 1/0).</summary>
    public bool Bool { get => Value.X > 0.5f; set => Value = new Vector3(value ? 1 : 0, 0, 0); }

    /// <summary>모든 필드를 복사한 새 인스턴스.</summary>
    public HistoryParam Clone() => new() { Name = Name, Kind = Kind, Value = Value, Min = Min, Max = Max, Step = Step };

    /// <summary>실수 파라미터 생성 도우미.</summary>
    public static HistoryParam F(string name, float v, float min = float.MinValue, float max = float.MaxValue, float step = 0.001f) => new() { Name = name, Kind = HistoryParamKind.Float, Value = new Vector3(v, 0, 0), Min = min, Max = max, Step = step };
    /// <summary>정수 파라미터 생성 도우미(Step 1).</summary>
    public static HistoryParam I(string name, int v, int min = int.MinValue, int max = int.MaxValue) => new() { Name = name, Kind = HistoryParamKind.Int, Value = new Vector3(v, 0, 0), Min = min, Max = max, Step = 1 };
    /// <summary>벡터 파라미터 생성 도우미.</summary>
    public static HistoryParam V(string name, Vector3 v, float step = 0.001f) => new() { Name = name, Kind = HistoryParamKind.Vector3, Value = v, Step = step };
}

/// <summary>히스토리 항목 하나의 파라미터 묶음(순서 유지). 이름으로 조회한다.</summary>
public sealed class HistoryParams
{
    /// <summary>파라미터 목록. UI는 이 순서대로 행을 만든다.</summary>
    public readonly List<HistoryParam> Items = new();
    /// <summary>빈 묶음(파라미터 없는 항목 = 편집 불가).</summary>
    public HistoryParams() { }
    /// <summary>주어진 파라미터들로 묶음을 만든다.</summary>
    public HistoryParams(params HistoryParam[] items) { Items.AddRange(items); }
    /// <summary>이름으로 파라미터를 찾는다(없으면 예외).</summary>
    public HistoryParam this[string name] => Items.First(p => p.Name == name);
    /// <summary>이름으로 실수 값을 읽는다.</summary>
    public float Float(string name) => this[name].Float;
    /// <summary>이름으로 정수 값을 읽는다.</summary>
    public int Int(string name) => this[name].Int;
    /// <summary>이름으로 벡터 값을 읽는다.</summary>
    public Vector3 Vec(string name) => this[name].Value;
    /// <summary>각 파라미터를 복제한 깊은 복사본.</summary>
    public HistoryParams Clone() { var c = new HistoryParams(); foreach (var p in Items) c.Items.Add(p.Clone()); return c; }
    /// <summary>같은 순서·개수에서 값만 비교한다(이름/범위는 무시).</summary>
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
    /// <summary>항목 이름(Properties History 목록 표시, 예: "Bevel").</summary>
    public string Name = "";
    /// <summary>편집 가능한 파라미터. 비어 있으면 이름만 보이는 항목.</summary>
    public HistoryParams Params = new();
    /// <summary>이 항목 적용 직전의 메시. 재평가 시 교체된다.</summary>
    public PolyMesh Before = new();
    /// <summary>메시를 제자리에서 수정한다. false면 변경 없음.</summary>
    public Func<PolyMesh, HistoryParams, bool> Replay = (_, _) => false;
    /// <summary>파라미터가 하나라도 있으면 편집 가능.</summary>
    public bool Editable => Params.Items.Count > 0;
}

/// <summary>구성 이력 재평가 도우미.</summary>
public static class HistoryReplay
{
    /// <summary>entries[from]부터 끝까지 다시 실행한다. 메시는 entries[from].Before로 되돌린 뒤 시작한다.</summary>
    /// <param name="mesh">제자리에서 다시 만들 메시(셰이프의 현재 메시).</param>
    /// <param name="entries">셰이프의 히스토리 목록(오래된 것이 앞).</param>
    /// <param name="from">다시 시작할 항목 인덱스.</param>
    public static void Rebuild(PolyMesh mesh, List<HistoryEntry> entries, int from)
    {
        if (from < 0 || from >= entries.Count) return;
        // 시작 항목의 적용 직전 상태로 되돌린다.
        mesh.CopyFrom(entries[from].Before);
        for (int k = from; k < entries.Count; k++)
        {
            // 뒤 항목들의 Before는 앞 항목 결과가 바뀌었으므로 새로 찍어 둔다.
            if (k > from) entries[k].Before = mesh.Clone();
            entries[k].Replay(mesh, entries[k].Params);
        }
        // 노멀 재계산과 위상 버전 증가(뷰/캐시 무효화)는 한 번만.
        MeshNormals.Recompute(mesh);
        mesh.BumpTopology();
    }
}

/// <summary>히스토리 항목의 파라미터를 바꾸고 그 항목부터 다시 실행한다.</summary>
/// <remarks>Undo를 위해 이전 파라미터, 전체 메시 스냅샷, 뒤 항목들의 Before를 보관한다. 재실행 중 컴포넌트 ID가 바뀔 수 있으므로 해당 노드의 컴포넌트 선택은 비운다.</remarks>
public sealed class EditHistoryCommand : ICommand
{
    /// <summary>대상 노드와 편집할 히스토리 항목 인덱스.</summary>
    private readonly NodeId _node; private readonly int _index;
    /// <summary>새 파라미터(복사본)와 Undo용 이전 파라미터.</summary>
    private readonly HistoryParams _newParams; private HistoryParams? _oldParams;
    /// <summary>편집 전/후 메시 전체 스냅샷. _meshAfter가 null이면 첫 실행.</summary>
    private PolyMesh? _meshBefore, _meshAfter;
    private List<PolyMesh>? _beforeSnapshots; // 뒤 항목들의 Before(재평가로 바뀌므로 보관)
    /// <summary>Undo 메뉴 이름(기본 "Edit History").</summary>
    public string Name { get; }

    /// <summary>편집 명령을 만든다. newParams는 복사해 보관하므로 호출자가 이후 바꿔도 안전하다.</summary>
    public EditHistoryCommand(NodeId node, int index, HistoryParams newParams, string? name = null)
    {
        _node = node; _index = index; _newParams = newParams.Clone(); Name = name ?? "Edit History";
    }

    /// <summary>파라미터를 교체하고 그 항목부터 히스토리를 다시 실행한 뒤 MeshTopology를 통지한다.</summary>
    public void Do(Document doc)
    {
        var n = doc.Get(_node); var shape = n.MeshShape ?? throw new InvalidOperationException("no mesh");
        var entries = shape.History;
        if (_index < 0 || _index >= entries.Count) return;
        if (_meshAfter == null)
        {
            // 첫 실행: Undo에 필요한 상태를 모두 저장한 뒤 재평가.
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
            // Redo: 새 파라미터를 다시 넣고 재평가(뒤 항목 Before도 함께 다시 만들어진다).
            entries[_index].Params = _newParams.Clone();
            doc.Selection.GetComponents(_node).ClearAll();
            shape.Mesh.CopyFrom(_meshAfter);
            // 뒤 항목 Before 재계산(결정적이므로 다시 실행)
            HistoryReplay.Rebuild(shape.Mesh, entries, _index);
        }
        doc.Notify(new DocChange(ChangeKind.MeshTopology, _node));
    }

    /// <summary>이전 파라미터·뒤 항목 Before·메시 스냅샷을 복원한다.</summary>
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
    /// <summary>이력을 지울 노드들.</summary>
    private readonly NodeId[] _nodes;
    /// <summary>Undo용으로 보관한 노드별 원래 이력 목록.</summary>
    private readonly Dictionary<NodeId, List<HistoryEntry>> _saved = new();
    /// <summary>명령 이름.</summary>
    public string Name => "Delete History";
    /// <summary>대상 노드 목록을 복사해 보관한다.</summary>
    public DeleteHistoryCommand(IEnumerable<NodeId> nodes) { _nodes = nodes.ToArray(); }

    /// <summary>메시 셰이프가 있는 노드마다 이력을 보관 후 비우고 HistoryChanged를 통지한다.</summary>
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

    /// <summary>보관한 이력 목록을 그대로 되돌려 넣는다.</summary>
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
/// <remarks>
/// 변형 툴 드래그가 끝나면 <c>MoveVerticesCommand</c>에 이 객체를 붙여 히스토리에 기록한다. 파라미터(Translate/Angle/Scale)를 바꾸면
/// <see cref="LocalMatrix"/>로 델타 행렬을 다시 만들어 같은 정점들에 적용한다. 행렬은 System.Numerics 행벡터 규약(v * M)이다.
/// </remarks>
public sealed class ComponentTransformOp
{
    /// <summary>변형 종류.</summary>
    /// <remarks>Move = 이동(Translate 파라미터), Rotate = 축 회전(Angle 파라미터, 도), Scale = 기저 축 기준 스케일(Scale 파라미터).</remarks>
    public enum Kind { Move, Rotate, Scale }
    /// <summary>이 연산의 종류.</summary>
    public Kind Type;
    public Vector3 Pivot;              // 월드
    public Vector3 Axis = Vector3.UnitY; // 회전축(월드)
    public Vector3 BasisX = Vector3.UnitX, BasisY = Vector3.UnitY, BasisZ = Vector3.UnitZ; // 스케일 기저(월드)
    /// <summary>드래그 당시 메시 노드의 월드 행렬. 월드 델타를 메시 로컬로 옮길 때 쓴다.</summary>
    public Matrix4x4 MeshWorld = Matrix4x4.Identity;

    /// <summary>종류에 맞는 파라미터 하나짜리 묶음을 만든다(드래그 결과 값으로 초기화).</summary>
    public HistoryParams DefaultParams(Vector3 translate, float angleDeg, Vector3 scale) => Type switch
    {
        Kind.Move => new HistoryParams(HistoryParam.V("Translate", translate)),
        Kind.Rotate => new HistoryParams(HistoryParam.F("Angle", angleDeg, step: 0.1f)),
        _ => new HistoryParams(HistoryParam.V("Scale", scale)),
    };

    /// <summary>파라미터로 월드 델타 행렬을 만든다.</summary>
    /// <remarks>회전/스케일은 피벗을 원점으로 옮긴 뒤(T(-P)) 변형하고 다시 되돌린다(T(P)). 스케일은 기저 행렬 b로 기저 공간에 들어가 축별 스케일 후 돌아온다.</remarks>
    public Matrix4x4 WorldMatrix(HistoryParams p)
    {
        switch (Type)
        {
            case Kind.Move: return Matrix4x4.CreateTranslation(p.Vec("Translate"));
            case Kind.Rotate:
                {
                    // 정규화한 축과 도 → 라디안 각도로 쿼터니언 회전.
                    var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Axis), p.Float("Angle") * MathF.PI / 180f);
                    return Matrix4x4.CreateTranslation(-Pivot) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(Pivot);
                }
            default:
                {
                    var s = p.Vec("Scale");
                    // b의 행 = 기저 축. v * Transpose(b) = 기저 좌표, 스케일 후 * b로 월드 복귀.
                    var b = new Matrix4x4(BasisX.X, BasisX.Y, BasisX.Z, 0, BasisY.X, BasisY.Y, BasisY.Z, 0, BasisZ.X, BasisZ.Y, BasisZ.Z, 0, 0, 0, 0, 1);
                    return Matrix4x4.CreateTranslation(-Pivot) * Matrix4x4.Transpose(b) * Matrix4x4.CreateScale(s) * b * Matrix4x4.CreateTranslation(Pivot);
                }
        }
    }

    /// <summary>메시 로컬 델타 행렬.</summary>
    /// <remarks>로컬 → 월드(MeshWorld) → 월드 델타 → 다시 로컬(inv) 순으로 합성한다.</remarks>
    public Matrix4x4 LocalMatrix(HistoryParams p)
    {
        Matrix4x4.Invert(MeshWorld, out var inv);
        return MeshWorld * WorldMatrix(p) * inv;
    }
}
