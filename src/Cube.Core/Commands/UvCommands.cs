using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>
/// UV 편집(코너 UV + 엣지 심)의 전/후 스냅샷 명령. 위상은 바뀌지 않으므로 하프에지/엣지 ID로 배열을 저장한다.
/// 드래그는 <see cref="Capture"/> → 변경 → <see cref="Commit"/> 후 alreadyApplied로 밀어 넣는다.
/// </summary>
public sealed class UvEditCommand : ICommand
{
    private readonly NodeId _node;
    private Vector2[] _uvBefore = Array.Empty<Vector2>(), _uvAfter = Array.Empty<Vector2>();
    private bool[] _seamBefore = Array.Empty<bool>(), _seamAfter = Array.Empty<bool>();
    private readonly Action<PolyMesh>? _apply;
    public string Name { get; }
    public bool Changed { get; private set; }

    /// <summary>즉시 실행형: apply가 메시 UV를 바꾼다.</summary>
    public UvEditCommand(string name, NodeId node, Action<PolyMesh> apply) { Name = name; _node = node; _apply = apply; }

    /// <summary>드래그형: Capture 후 바깥에서 바꾸고 Commit.</summary>
    public UvEditCommand(string name, NodeId node) { Name = name; _node = node; }

    private static Vector2[] Uvs(PolyMesh m) { var a = new Vector2[m.HalfEdgeCount]; for (int i = 0; i < a.Length; i++) a[i] = m.Hes[i].Uv0; return a; }
    private static bool[] Seams(PolyMesh m) { var a = new bool[m.EdgeCount]; for (int i = 0; i < a.Length; i++) a[i] = m.Edges[i].Seam; return a; }

    public void Capture(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        _uvBefore = Uvs(mesh); _seamBefore = Seams(mesh);
    }

    public void Commit(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        _uvAfter = Uvs(mesh); _seamAfter = Seams(mesh);
        Changed = !_uvBefore.AsSpan().SequenceEqual(_uvAfter) || !_seamBefore.AsSpan().SequenceEqual(_seamAfter);
    }

    public void Do(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        if (_uvAfter.Length == 0)
        {
            Capture(doc);
            _apply?.Invoke(mesh);
            Commit(doc);
        }
        else Restore(mesh, _uvAfter, _seamAfter);
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, _node));
    }

    public void Undo(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        Restore(mesh, _uvBefore, _seamBefore);
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, _node));
    }

    private static void Restore(PolyMesh m, Vector2[] uvs, bool[] seams)
    {
        for (int i = 0; i < uvs.Length && i < m.HalfEdgeCount; i++) { var h = m.Hes[i]; h.Uv0 = uvs[i]; m.Hes[i] = h; }
        for (int i = 0; i < seams.Length && i < m.EdgeCount; i++) { var e = m.Edges[i]; e.Seam = seams[i]; m.Edges[i] = e; }
    }
}
