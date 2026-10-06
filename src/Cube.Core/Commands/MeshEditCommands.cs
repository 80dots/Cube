using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

public sealed class ExtrudeFacesCommand : MeshEditCommand
{
    private readonly int[] _faces;
    public List<int> NewFaces { get; private set; } = new();
    public override string Name => "Extrude";
    public ExtrudeFacesCommand(NodeId node, IEnumerable<int> faces) : base(node) { _faces = faces.ToArray(); }

    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        NewFaces = MeshOps.ExtrudeFaces(mesh, _faces);
        if (NewFaces.Count == 0) return false;
        doc.Selection.Mode = SelectMode.Face;
        doc.Selection.SelectComponents(NodeId, SelectMode.Face, NewFaces, replace: true);
        return true;
    }
}

/// <summary>현재 모드의 컴포넌트 삭제(면/엣지/정점).</summary>
public sealed class DeleteComponentsCommand : MeshEditCommand
{
    private readonly SelectMode _mode; private readonly int[] _ids;
    public override string Name => _mode switch { SelectMode.Face => "Delete Faces", SelectMode.Edge => "Delete Edges", _ => "Delete Vertices" };
    public DeleteComponentsCommand(NodeId node, SelectMode mode, IEnumerable<int> ids) : base(node) { _mode = mode; _ids = ids.ToArray(); }

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
        doc.Selection.GetComponents(NodeId).Get(_mode).Clear();
        doc.Selection.ClearCurrentMode();
        return true;
    }
}

public sealed class MergeVerticesCommand : MeshEditCommand
{
    private readonly int[] _verts; private readonly float _threshold;
    public int MergedCount { get; private set; }
    public override string Name => "Merge Vertices";
    public MergeVerticesCommand(NodeId node, IEnumerable<int> verts, float threshold) : base(node) { _verts = verts.ToArray(); _threshold = threshold; }

    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        MergedCount = MeshOps.MergeVertices(mesh, _verts, _threshold);
        if (MergedCount == 0) return false;
        doc.Selection.ClearCurrentMode();
        return true;
    }
}

public sealed class SetEdgesHardCommand : MeshEditCommand
{
    private readonly int[] _edges; private readonly bool _hard;
    public override string Name => _hard ? "Harden Edge" : "Soften Edge";
    public SetEdgesHardCommand(NodeId node, IEnumerable<int> edges, bool hard) : base(node) { _edges = edges.ToArray(); _hard = hard; }
    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        if (_edges.Length == 0) return false;
        MeshOps.SetEdgesHard(mesh, _edges, _hard);
        return true;
    }
}

public sealed class ReverseFacesCommand : MeshEditCommand
{
    private readonly int[] _faces;
    public override string Name => "Reverse";
    public ReverseFacesCommand(NodeId node, IEnumerable<int> faces) : base(node) { _faces = faces.ToArray(); }
    protected override bool Execute(Document doc, SceneNode node, PolyMesh mesh)
    {
        if (_faces.Length == 0) return false;
        MeshOps.ReverseFaces(mesh, _faces);
        return true;
    }
}

/// <summary>Mesh → Combine: 선택 오브젝트들을 월드 공간 정점을 가진 새 메시 하나로 합친다(Maya와 동일하게 원점 트랜스폼).</summary>
public sealed class CombineCommand : ICommand
{
    private readonly NodeId[] _sources;
    private SceneNode? _combined;
    private DeleteNodesCommand? _delete;
    private SelectionSnapshot? _selBefore;
    public string Name => "Combine";
    public SceneNode? Result => _combined;

    public CombineCommand(IEnumerable<NodeId> sources) { _sources = sources.ToArray(); }

    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        if (_combined == null)
        {
            var mesh = new PolyMesh();
            string name = "polySurface";
            foreach (var id in _sources)
            {
                var n = doc.Find(id); if (n?.Mesh == null) continue;
                var src = n.Mesh.Clone(); src.Compact();
                MeshOps.Append(mesh, src, n.WorldMatrix);
            }
            MeshNormals.Recompute(mesh);
            _combined = new SceneNode { Name = doc.UniqueName(name + "1"), Shape = new MeshShape(mesh) };
            _delete = new DeleteNodesCommand(doc, _sources);
        }
        _delete!.Do(doc);
        doc.AddNode(_combined);
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(new[] { _combined.Id });
    }

    public void Undo(Document doc)
    {
        doc.RemoveNode(_combined!);
        _delete!.Undo(doc);
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}

/// <summary>Mesh → Separate: 연결 요소마다 새 오브젝트로 분리한다(같은 트랜스폼 유지).</summary>
public sealed class SeparateCommand : ICommand
{
    private readonly NodeId _source;
    private readonly List<SceneNode> _parts = new();
    private DeleteNodesCommand? _delete;
    private SelectionSnapshot? _selBefore;
    public string Name => "Separate";
    public IReadOnlyList<SceneNode> Parts => _parts;

    public SeparateCommand(NodeId source) { _source = source; }

    public bool Prepare(Document doc)
    {
        var n = doc.Find(_source); if (n?.Mesh == null) return false;
        var comps = MeshOps.ConnectedComponents(n.Mesh);
        if (comps.Count < 2) return false;
        for (int i = 0; i < comps.Count; i++)
        {
            var mesh = MeshOps.ExtractFaces(n.Mesh, comps[i]);
            MeshNormals.Recompute(mesh);
            _parts.Add(new SceneNode { Name = doc.UniqueName("polySurface1"), Local = n.Local, Shape = new MeshShape(mesh) });
            // UniqueName은 문서에 추가되기 전이라 중복될 수 있어 번호를 덧붙인다
            _parts[^1].Name = $"polySurface{NextIndex(doc) + i}";
        }
        _delete = new DeleteNodesCommand(doc, new[] { _source });
        return true;
    }

    private static int NextIndex(Document doc)
    {
        int max = 0;
        foreach (var n in doc.Nodes.Values)
            if (n.Name.StartsWith("polySurface") && int.TryParse(n.Name["polySurface".Length..], out int k)) max = System.Math.Max(max, k);
        return max + 1;
    }

    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        var parentId = doc.Find(_source)?.Parent is { IsRoot: false } p ? p.Id : NodeId.None;
        _delete!.Do(doc);
        foreach (var part in _parts) doc.AddNode(part, parentId.IsNone ? doc.Root : doc.Get(parentId));
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(_parts.Select(p => p.Id));
    }

    public void Undo(Document doc)
    {
        foreach (var part in _parts) doc.RemoveNode(part);
        _delete!.Undo(doc);
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}
