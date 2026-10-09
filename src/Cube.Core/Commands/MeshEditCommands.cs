using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>면 Extrude(Keep Faces Together). 결과로 새 캡 면을 면 모드로 선택한다.</summary>
/// <remarks>초기 M1 명령. 옵션이 있는 Blender식 Extrude는 <c>MeshOpCommand</c> + <c>MeshOps.Extrude(ExtrudeOptions)</c>를 쓴다.</remarks>
public sealed class ExtrudeFacesCommand : MeshEditCommand
{
    /// <summary>돌출할 면 ID들.</summary>
    private readonly int[] _faces;
    /// <summary>실행 결과 새로 생긴 캡 면 ID(호출자가 조작기 설정에 사용).</summary>
    public List<int> NewFaces { get; private set; } = new();
    /// <summary>명령 이름.</summary>
    public override string Name => "Extrude";
    /// <summary>대상 노드와 면 목록을 받는다.</summary>
    public ExtrudeFacesCommand(NodeId node, IEnumerable<int> faces) : base(node) { _faces = faces.ToArray(); }

    /// <summary>ExtrudeFaces를 실행하고 새 면을 선택한다. 새 면이 없으면 변경 없음.</summary>
    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        NewFaces = MeshOps.ExtrudeFaces(mesh, _faces);
        if (NewFaces.Count == 0) return false;
        doc.Selection.Mode = SelectMode.Face;
        doc.Selection.SelectComponents(NodeId, SelectMode.Face, NewFaces, replace: true);
        return true;
    }

    /// <summary>파라미터 없는 히스토리 항목(같은 면 ID로 다시 Extrude).</summary>
    protected override HistoryEntry? MakeHistoryEntry() { var faces = _faces; return new HistoryEntry { Replay = (m, _) => MeshOps.ExtrudeFaces(m, faces).Count > 0 }; }
}

/// <summary>현재 모드의 컴포넌트 삭제(면/엣지/정점).</summary>
/// <remarks>엣지/정점 삭제는 Maya Delete Edge/Vertex처럼 면을 병합하고 2가 정점을 정리한다(<c>MeshOps.DeleteEdges/DeleteVertices</c>).</remarks>
public sealed class DeleteComponentsCommand : MeshEditCommand
{
    /// <summary>삭제할 컴포넌트 종류와 ID들.</summary>
    private readonly SelectMode _mode; private readonly int[] _ids;
    /// <summary>모드별 이름(Delete Faces/Edges/Vertices).</summary>
    public override string Name => _mode switch { SelectMode.Face => "Delete Faces", SelectMode.Edge => "Delete Edges", _ => "Delete Vertices" };
    /// <summary>대상 노드, 모드, ID 목록을 받는다.</summary>
    public DeleteComponentsCommand(NodeId node, SelectMode mode, IEnumerable<int> ids) : base(node) { _mode = mode; _ids = ids.ToArray(); }

    /// <summary>모드에 맞는 삭제 연산을 실행하고 해당 모드 선택을 비운다.</summary>
    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        if (_ids.Length == 0) return false;
        switch (_mode)
        {
            case SelectMode.Face: MeshOps.DeleteFaces(mesh, _ids); break;
            case SelectMode.Edge: MeshOps.DeleteEdges(mesh, _ids); break;
            case SelectMode.Vertex: MeshOps.DeleteVertices(mesh, _ids); break;
            default: return false;
        }
        // 삭제된(죽은) ID가 선택에 남지 않도록 비운다.
        doc.Selection.GetComponents(NodeId).Get(_mode).Clear();
        doc.Selection.ClearCurrentMode();
        return true;
    }

    /// <summary>같은 모드·ID로 다시 삭제하는 히스토리 항목.</summary>
    protected override HistoryEntry? MakeHistoryEntry()
    {
        var mode = _mode; var ids = _ids;
        return new HistoryEntry { Replay = (m, _) => { switch (mode) { case SelectMode.Face: MeshOps.DeleteFaces(m, ids); break; case SelectMode.Edge: MeshOps.DeleteEdges(m, ids); break; case SelectMode.Vertex: MeshOps.DeleteVertices(m, ids); break; } return true; } };
    }
}

/// <summary>Merge Vertices: 거리 임계값 안의 선택 정점들을 하나로 병합한다. Threshold는 히스토리에서 편집 가능.</summary>
public sealed class MergeVerticesCommand : MeshEditCommand
{
    /// <summary>병합 후보 정점 ID와 거리 임계값(메시 로컬 단위, m).</summary>
    private readonly int[] _verts; private readonly float _threshold;
    /// <summary>첫 실행에서 병합(제거)된 정점 수.</summary>
    public int MergedCount { get; private set; }
    /// <summary>명령 이름.</summary>
    public override string Name => "Merge Vertices";
    /// <summary>대상 노드, 정점 목록, 임계값을 받는다.</summary>
    public MergeVerticesCommand(NodeId node, IEnumerable<int> verts, float threshold) : base(node) { _verts = verts.ToArray(); _threshold = threshold; }

    /// <summary>병합을 실행한다. 병합된 정점이 없으면 변경 없음.</summary>
    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        MergedCount = MeshOps.MergeVertices(mesh, _verts, _threshold);
        if (MergedCount == 0) return false;
        doc.Selection.ClearCurrentMode();
        return true;
    }

    /// <summary>Threshold 파라미터를 가진 히스토리 항목(값을 바꾸면 같은 정점으로 다시 병합).</summary>
    protected override HistoryEntry? MakeHistoryEntry()
    {
        var verts = _verts;
        return new HistoryEntry { Params = new HistoryParams(HistoryParam.F("Threshold", _threshold, 0, 1000, 0.0001f)), Replay = (m, p) => MeshOps.MergeVertices(m, verts, p.Float("Threshold")) >= 0 };
    }
}

/// <summary>Harden/Soften Edge: 엣지의 하드 플래그를 설정해 노멀 분리 여부를 바꾼다.</summary>
public sealed class SetEdgesHardCommand : MeshEditCommand
{
    /// <summary>대상 엣지 ID와 설정할 값(true = 하드).</summary>
    private readonly int[] _edges; private readonly bool _hard;
    /// <summary>값에 따라 "Harden Edge" 또는 "Soften Edge".</summary>
    public override string Name => _hard ? "Harden Edge" : "Soften Edge";
    /// <summary>대상 노드, 엣지 목록, 하드 여부를 받는다.</summary>
    public SetEdgesHardCommand(NodeId node, IEnumerable<int> edges, bool hard) : base(node) { _edges = edges.ToArray(); _hard = hard; }
    /// <summary>플래그를 설정한다(노멀 재계산은 기반 클래스가 한다).</summary>
    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        if (_edges.Length == 0) return false;
        MeshOps.SetEdgesHard(mesh, _edges, _hard);
        return true;
    }

    /// <summary>같은 엣지에 같은 값을 다시 설정하는 히스토리 항목.</summary>
    protected override HistoryEntry? MakeHistoryEntry() { var edges = _edges; bool hard = _hard; return new HistoryEntry { Replay = (m, _) => { MeshOps.SetEdgesHard(m, edges, hard); return true; } }; }
}

/// <summary>Reverse: 면 방향(감기 순서)을 뒤집는다. 하프에지 일관성을 위해 연결 요소 전체가 뒤집힌다.</summary>
public sealed class ReverseFacesCommand : MeshEditCommand
{
    /// <summary>기준 면 ID들.</summary>
    private readonly int[] _faces;
    /// <summary>명령 이름.</summary>
    public override string Name => "Reverse";
    /// <summary>대상 노드와 면 목록을 받는다.</summary>
    public ReverseFacesCommand(NodeId node, IEnumerable<int> faces) : base(node) { _faces = faces.ToArray(); }
    /// <summary>면을 뒤집는다.</summary>
    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        if (_faces.Length == 0) return false;
        MeshOps.ReverseFaces(mesh, _faces);
        return true;
    }

    /// <summary>같은 면으로 다시 뒤집는 히스토리 항목.</summary>
    protected override HistoryEntry? MakeHistoryEntry() { var faces = _faces; return new HistoryEntry { Replay = (m, _) => { MeshOps.ReverseFaces(m, faces); return true; } }; }
}

/// <summary>Mesh → Combine: 선택 오브젝트들을 월드 공간 정점을 가진 새 메시 하나로 합친다(Maya와 동일하게 원점 트랜스폼).</summary>
/// <remarks>원본 노드 삭제는 내부 <see cref="DeleteNodesCommand"/>에 맡긴다. 결과 노드는 첫 실행에서 한 번만 만들고 Redo 때 재사용한다(같은 ID 유지).</remarks>
public sealed class CombineCommand : ICommand
{
    /// <summary>합칠 원본 노드들.</summary>
    private readonly NodeId[] _sources;
    /// <summary>합쳐진 결과 노드(첫 Do에서 생성).</summary>
    private SceneNode? _combined;
    /// <summary>원본 삭제를 담당하는 하위 명령.</summary>
    private DeleteNodesCommand? _delete;
    /// <summary>실행 전 선택(Undo 복원용, 한 번만 캡처).</summary>
    private SelectionSnapshot? _selBefore;
    /// <summary>명령 이름.</summary>
    public string Name => "Combine";
    /// <summary>결과 노드(실행 전이면 null).</summary>
    public SceneNode? Result => _combined;

    /// <summary>원본 노드 목록을 받는다.</summary>
    public CombineCommand(IEnumerable<NodeId> sources) { _sources = sources.ToArray(); }

    /// <summary>원본을 삭제하고 합친 노드를 추가·선택한다.</summary>
    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        if (_combined == null)
        {
            // 첫 실행: 각 원본 메시를 압축 복사해 월드 행렬로 베이크하며 하나에 이어 붙인다.
            var mesh = new PolyMesh();
            string name = "polySurface";
            foreach (var id in _sources)
            {
                var n = doc.Find(id); if (n?.Mesh == null) continue;
                var src = n.Mesh.Clone(); src.Compact();
                MeshOps.Append(mesh, src, n.WorldMatrix);
            }
            MeshNormals.Recompute(mesh);
            // 결과는 단위 트랜스폼(원점)의 새 노드. 삭제 명령은 이 시점의 문서 상태로 만든다.
            // 머티리얼은 오브젝트 단위이므로 첫 원본의 머티리얼을 이어받는다(전에는 lambert1로 초기화됐다, v0.0.57)
            var firstMat = _sources.Select(doc.Find).FirstOrDefault(n => n?.Mesh != null)?.MaterialId ?? default;
            _combined = new SceneNode { Name = doc.UniqueName(name + "1"), Shape = new MeshShape(mesh), MaterialId = firstMat };
            _delete = new DeleteNodesCommand(doc, _sources);
        }
        _delete!.Do(doc);
        doc.AddNode(_combined);
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(new[] { _combined.Id });
    }

    /// <summary>결과 노드를 빼고 원본을 되살린 뒤 선택을 복원한다.</summary>
    public void Undo(Document doc)
    {
        doc.RemoveNode(_combined!);
        _delete!.Undo(doc);
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}

/// <summary>Mesh → Separate: 연결 요소마다 새 오브젝트로 분리한다(같은 트랜스폼 유지).</summary>
/// <remarks>사용 전에 <see cref="Prepare"/>를 호출해 조각을 미리 만든다(false면 분리할 것이 없음). 조각은 원본과 같은 부모 아래에 들어간다.</remarks>
public sealed class SeparateCommand : ICommand
{
    /// <summary>분리할 원본 노드.</summary>
    private readonly NodeId _source;
    /// <summary>연결 요소별로 만든 새 노드들.</summary>
    private readonly List<SceneNode> _parts = new();
    /// <summary>원본 삭제 하위 명령.</summary>
    private DeleteNodesCommand? _delete;
    /// <summary>실행 전 선택.</summary>
    private SelectionSnapshot? _selBefore;
    /// <summary>명령 이름.</summary>
    public string Name => "Separate";
    /// <summary>분리된 조각 노드들.</summary>
    public IReadOnlyList<SceneNode> Parts => _parts;

    /// <summary>원본 노드를 받는다.</summary>
    public SeparateCommand(NodeId source) { _source = source; }

    /// <summary>연결 요소를 구해 조각 노드를 만든다. 요소가 2개 미만이면 false.</summary>
    public bool Prepare(Document doc)
    {
        var n = doc.Find(_source); if (n?.Mesh == null) return false;
        var comps = MeshOps.ConnectedComponents(n.Mesh);
        if (comps.Count < 2) return false;
        for (int i = 0; i < comps.Count; i++)
        {
            // 요소의 면만 뽑아 독립 메시로 만들고 원본 로컬 트랜스폼을 그대로 복사한다.
            var mesh = MeshOps.ExtractFaces(n.Mesh, comps[i]);
            MeshNormals.Recompute(mesh);
            _parts.Add(new SceneNode { Name = doc.UniqueName("polySurface1"), Local = n.Local, Shape = new MeshShape(mesh) });
            // UniqueName은 문서에 추가되기 전이라 중복될 수 있어 번호를 덧붙인다
            _parts[^1].Name = $"polySurface{NextIndex(doc) + i}";
        }
        _delete = new DeleteNodesCommand(doc, new[] { _source });
        return true;
    }

    /// <summary>문서에서 "polySurfaceN" 이름의 최대 N + 1을 구한다.</summary>
    private static int NextIndex(Document doc)
    {
        int max = 0;
        foreach (var n in doc.Nodes.Values)
            if (n.Name.StartsWith("polySurface") && int.TryParse(n.Name["polySurface".Length..], out int k)) max = System.Math.Max(max, k);
        return max + 1;
    }

    /// <summary>원본을 지우고 조각들을 원본의 부모 아래에 추가해 선택한다.</summary>
    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        // 삭제 전에 부모를 기억(루트면 None).
        var parentId = doc.Find(_source)?.Parent is { IsRoot: false } p ? p.Id : NodeId.None;
        _delete!.Do(doc);
        foreach (var part in _parts) doc.AddNode(part, parentId.IsNone ? doc.Root : doc.Get(parentId));
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(_parts.Select(p => p.Id));
    }

    /// <summary>조각을 빼고 원본을 되살린다.</summary>
    public void Undo(Document doc)
    {
        foreach (var part in _parts) doc.RemoveNode(part);
        _delete!.Undo(doc);
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}
