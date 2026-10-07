using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>
/// 범용 위상 편집 명령: op가 메시를 제자리에서 수정하고 새로 선택할 컴포넌트 ID(없으면 null)를 돌려준다.
/// 변경이 없으면(false) 스택에 영향이 없다.
/// </summary>
public sealed class MeshOpCommand : MeshEditCommand
{
    private readonly Func<PolyMesh, HistoryParams, (bool changed, SelectMode? selectMode, IEnumerable<int>? select)> _op;
    private readonly HistoryParams _params;
    public override string Name { get; }
    public List<int> NewComponents { get; private set; } = new();

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

    protected override HistoryEntry? MakeHistoryEntry()
    {
        var op = _op;
        return new HistoryEntry { Params = _params.Clone(), Replay = (m, p) => op(m, p).changed };
    }

    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        var (changed, mode, select) = _op(mesh, _params);
        if (!changed) return false;
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
