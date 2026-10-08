using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>메시에 skinCluster를 붙이거나(Bind Skin) 뗀다(Detach Skin).</summary>
/// <remarks>skin이 null이면 Detach. 객체 참조만 교체하므로 Undo 시 이전 SkinCluster 객체(가중치 포함)가 그대로 돌아온다.</remarks>
public sealed class SetSkinCommand : ICommand
{
    /// <summary>대상 메시 노드.</summary>
    private readonly NodeId _mesh;
    /// <summary>설정할 스킨(null = 떼기).</summary>
    private readonly SkinCluster? _after;
    /// <summary>원래 스킨(첫 실행 때 기억).</summary>
    private SkinCluster? _before;
    /// <summary>명령 이름(Bind Skin/Detach Skin 등).</summary>
    public string Name { get; }

    /// <summary>이름, 메시 노드, 설정할 스킨을 받는다.</summary>
    public SetSkinCommand(string name, NodeId mesh, SkinCluster? skin) { Name = name; _mesh = mesh; _after = skin; }

    /// <summary>원래 스킨을 기억하고 새 스킨으로 교체한 뒤 SkinChanged를 통지한다.</summary>
    public void Do(Document doc)
    {
        var shape = doc.Get(_mesh).MeshShape ?? throw new InvalidOperationException("node has no mesh");
        _before ??= shape.Skin;
        shape.Skin = _after;
        doc.Notify(new DocChange(ChangeKind.SkinChanged, _mesh));
    }

    /// <summary>원래 스킨으로 되돌린다.</summary>
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
    /// <summary>스킨된 메시 노드.</summary>
    private readonly NodeId _mesh;
    /// <summary>스트로크가 건드린 정점 ID들.</summary>
    private readonly int[] _verts;
    /// <summary>정점별(_verts와 같은 순서) 변경 전/후 (조인트 인덱스, 가중치) 목록.</summary>
    private readonly List<(int joint, float weight)>[] _before, _after;
    /// <summary>명령 이름.</summary>
    public string Name => "Paint Skin Weights";

    /// <summary>배열들은 같은 길이·순서여야 한다.</summary>
    public WeightPaintCommand(NodeId mesh, int[] verts, List<(int joint, float weight)>[] before, List<(int joint, float weight)>[] after)
    {
        _mesh = mesh; _verts = verts; _before = before; _after = after;
    }

    /// <summary>모든 정점의 가중치 목록(순서·값)이 같으면 true.</summary>
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

    /// <summary>변경 후 가중치를 적용한다.</summary>
    public void Do(Document doc) => Apply(doc, _after);
    /// <summary>변경 전 가중치를 적용한다.</summary>
    public void Undo(Document doc) => Apply(doc, _before);

    /// <summary>정점마다 SetWeights로 목록을 넣고 SkinChanged를 통지한다(스킨이 사라졌으면 무시).</summary>
    private void Apply(Document doc, List<(int joint, float weight)>[] weights)
    {
        var skin = doc.Get(_mesh).MeshShape?.Skin;
        if (skin == null) return;
        for (int i = 0; i < _verts.Length; i++) skin.SetWeights(_verts[i], weights[i]);
        doc.Notify(new DocChange(ChangeKind.SkinChanged, _mesh));
    }
}
