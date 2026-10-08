using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>
/// 범용 위상 편집 명령: op가 메시를 제자리에서 수정하고 새로 선택할 컴포넌트 ID(없으면 null)를 돌려준다.
/// 변경이 없으면(false) 스택에 영향이 없다.
/// </summary>
/// <remarks>대부분의 모델링 메뉴 연산(Bevel, Extrude, Insert Edge Loop, Smooth 등)이 이 클래스를 람다로 만들어 쓴다. 항상 구성 이력 항목을 남긴다.</remarks>
public sealed class MeshOpCommand : MeshEditCommand
{
    /// <summary>실제 연산: (메시, 파라미터) → (변경 여부, 새 선택 모드, 새 선택 ID).</summary>
    private readonly Func<PolyMesh, HistoryParams, (bool changed, SelectMode? selectMode, IEnumerable<int>? select)> _op;
    /// <summary>히스토리에 기록할 파라미터(없으면 빈 묶음 = 이름만 보이는 항목).</summary>
    private readonly HistoryParams _params;
    /// <summary>명령 이름.</summary>
    public override string Name { get; }
    /// <summary>첫 실행에서 op가 돌려준 새 컴포넌트 ID(새로 만든 면/엣지 등). 호출자가 후속 툴 설정에 쓴다.</summary>
    public List<int> NewComponents { get; private set; } = new();

    /// <summary>파라미터 없이 선택 결과까지 돌려주는 형태.</summary>
    public MeshOpCommand(string name, NodeId node, Func<PolyMesh, (bool changed, SelectMode? selectMode, IEnumerable<int>? select)> op) : base(node)
    {
        Name = name; _op = (m, _) => op(m); _params = new HistoryParams();
    }

    /// <summary>선택 변경 없이 메시만 바꾸는 간단한 형태.</summary>
    public MeshOpCommand(string name, NodeId node, Func<PolyMesh, bool> op) : base(node)
    {
        Name = name; _op = (m, _) => (op(m), null, null); _params = new HistoryParams();
    }

    /// <summary>편집 가능한 파라미터가 있는 형태(Bevel 거리, Insert Edge Loop 위치, Smooth 단계 등). 히스토리에서 파라미터를 바꾸면 다시 실행된다.</summary>
    public MeshOpCommand(string name, NodeId node, HistoryParams parameters, Func<PolyMesh, HistoryParams, (bool changed, SelectMode? selectMode, IEnumerable<int>? select)> op) : base(node)
    {
        Name = name; _op = op; _params = parameters;
    }

    /// <summary>같은 op를 Replay로 쓰는 히스토리 항목을 만든다(선택 결과는 재실행 때 무시).</summary>
    protected override HistoryEntry? MakeHistoryEntry()
    {
        var op = _op;
        return new HistoryEntry { Params = _params.Clone(), Replay = (m, p) => op(m, p).changed };
    }

    /// <summary>op를 실행하고, 새 선택이 주어지면 해당 모드로 바꿔 그 컴포넌트만 선택한다.</summary>
    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        var (changed, mode, select) = _op(mesh, _params);
        if (!changed) return false;
        // 결과 선택 적용(모드 전환 포함).
        if (mode != null && select != null)
        {
            NewComponents = select.ToList();
            doc.Selection.GetComponents(NodeId).ClearAll(); // 옛 ID(죽은 컴포넌트)가 남지 않도록
            doc.Selection.Mode = mode.Value;
            doc.Selection.SelectComponents(NodeId, mode.Value, NewComponents, replace: true);
        }
        return true;
    }
}
