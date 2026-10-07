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
    private bool[] _pinBefore = Array.Empty<bool>(), _pinAfter = Array.Empty<bool>();
    private readonly Action<PolyMesh>? _apply;
    public string Name { get; }
    public bool Changed { get; private set; }

    /// <summary>즉시 실행형: apply가 메시 UV를 바꾼다.</summary>
    public UvEditCommand(string name, NodeId node, Action<PolyMesh> apply) { Name = name; _node = node; _apply = apply; }

    /// <summary>드래그형: Capture 후 바깥에서 바꾸고 Commit.</summary>
    public UvEditCommand(string name, NodeId node) { Name = name; _node = node; }

    private static Vector2[] Uvs(PolyMesh m) { var a = new Vector2[m.HalfEdgeCount]; for (int i = 0; i < a.Length; i++) a[i] = m.Hes[i].Uv0; return a; }
    private static bool[] Seams(PolyMesh m) { var a = new bool[m.EdgeCount]; for (int i = 0; i < a.Length; i++) a[i] = m.Edges[i].Seam; return a; }
    private static bool[] Pins(PolyMesh m) { var a = new bool[m.HalfEdgeCount]; for (int i = 0; i < a.Length; i++) a[i] = m.Hes[i].PinUv; return a; }

    public void Capture(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        _uvBefore = Uvs(mesh); _seamBefore = Seams(mesh); _pinBefore = Pins(mesh);
    }

    public void Commit(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        _uvAfter = Uvs(mesh); _seamAfter = Seams(mesh); _pinAfter = Pins(mesh);
        Changed = !_uvBefore.AsSpan().SequenceEqual(_uvAfter) || !_seamBefore.AsSpan().SequenceEqual(_seamAfter) || !_pinBefore.AsSpan().SequenceEqual(_pinAfter);
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
        else Restore(mesh, _uvAfter, _seamAfter, _pinAfter);
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, _node));
    }

    public void Undo(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        Restore(mesh, _uvBefore, _seamBefore, _pinBefore);
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, _node));
    }

    private static void Restore(PolyMesh m, Vector2[] uvs, bool[] seams, bool[] pins)
    {
        for (int i = 0; i < uvs.Length && i < m.HalfEdgeCount; i++) { var h = m.Hes[i]; h.Uv0 = uvs[i]; if (i < pins.Length) h.PinUv = pins[i]; m.Hes[i] = h; }
        for (int i = 0; i < seams.Length && i < m.EdgeCount; i++) { var e = m.Edges[i]; e.Seam = seams[i]; m.Edges[i] = e; }
    }
}

/// <summary>UV 세트 구조 변경(생성/삭제/이름/전환/복사)의 전/후 스냅샷 명령.</summary>
public sealed class UvSetsCommand : ICommand
{
    private readonly NodeId _node; private readonly Action<PolyMesh> _apply;
    private (List<UvSet> sets, int current, Vector2[] uvs)? _before, _after;
    public string Name { get; }
    public UvSetsCommand(string name, NodeId node, Action<PolyMesh> apply) { Name = name; _node = node; _apply = apply; }

    private static (List<UvSet>, int, Vector2[]) Snap(PolyMesh m) => (m.UvSets.Select(s => s.Clone()).ToList(), m.CurrentUvSet, m.SnapshotUvs());
    private static void Restore(PolyMesh m, (List<UvSet> sets, int current, Vector2[] uvs) s)
    {
        m.UvSets.Clear(); foreach (var x in s.sets) m.UvSets.Add(x.Clone());
        m.CurrentUvSet = s.current;
        for (int i = 0; i < s.uvs.Length && i < m.HalfEdgeCount; i++) { var h = m.Hes[i]; h.Uv0 = s.uvs[i]; m.Hes[i] = h; }
    }

    public void Do(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        if (_after == null) { _before = Snap(mesh); _apply(mesh); _after = Snap(mesh); }
        else Restore(mesh, _after.Value);
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, _node));
    }

    public void Undo(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        if (_before != null) Restore(mesh, _before.Value);
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, _node));
    }
}
