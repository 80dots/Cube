using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>메시에 skinCluster를 붙이거나(Bind Skin) 뗀다(Detach Skin).</summary>
public sealed class SetSkinCommand : ICommand
{
    private readonly NodeId _mesh;
    private readonly SkinCluster? _after;
    private SkinCluster? _before;
    public string Name { get; }

    public SetSkinCommand(string name, NodeId mesh, SkinCluster? skin) { Name = name; _mesh = mesh; _after = skin; }

    public void Do(Document doc)
    {
        var shape = doc.Get(_mesh).MeshShape ?? throw new InvalidOperationException("node has no mesh");
        _before ??= shape.Skin;
        shape.Skin = _after;
        doc.Notify(new DocChange(ChangeKind.SkinChanged, _mesh));
    }

    public void Undo(Document doc)
    {
        var shape = doc.Get(_mesh).MeshShape!;
        shape.Skin = _before;
        doc.Notify(new DocChange(ChangeKind.SkinChanged, _mesh));
    }
}

/// <summary>가중치 페인트 스트로크 하나. 바뀐 정점의 before/after 가중치 목록을 가진다(드래그 후 alreadyApplied로 넣는다).</summary>
public sealed class WeightPaintCommand : ICommand
{
    private readonly NodeId _mesh;
    private readonly int[] _verts;
    private readonly List<(int joint, float weight)>[] _before, _after;
    public string Name => "Paint Skin Weights";

    public WeightPaintCommand(NodeId mesh, int[] verts, List<(int joint, float weight)>[] before, List<(int joint, float weight)>[] after)
    {
        _mesh = mesh; _verts = verts; _before = before; _after = after;
    }

    public bool IsNoop
    {
        get
        {
            for (int i = 0; i < _verts.Length; i++)
            {
                if (_before[i].Count != _after[i].Count) return false;
                for (int k = 0; k < _before[i].Count; k++) if (_before[i][k] != _after[i][k]) return false;
            }
            return true;
        }
    }

    public void Do(Document doc) => Apply(doc, _after);
    public void Undo(Document doc) => Apply(doc, _before);

    private void Apply(Document doc, List<(int joint, float weight)>[] weights)
    {
        var skin = doc.Get(_mesh).MeshShape?.Skin;
        if (skin == null) return;
        for (int i = 0; i < _verts.Length; i++) skin.SetWeights(_verts[i], weights[i]);
        doc.Notify(new DocChange(ChangeKind.SkinChanged, _mesh));
    }
}
