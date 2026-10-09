using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>
/// UV 편집(코너 UV + 엣지 심)의 전/후 스냅샷 명령. 위상은 바뀌지 않으므로 하프에지/엣지 ID로 배열을 저장한다.
/// 드래그는 <see cref="Capture"/> → 변경 → <see cref="Commit"/> 후 alreadyApplied로 밀어 넣는다.
/// </summary>
/// <remarks>
/// 저장 대상: 코너 UV(Uv0, 하단 원점), 엣지 심(<c>Edge.Seam</c>), 코너 UV 핀(<c>HalfEdge.PinUv</c>). 다른 UV 세트는 건드리지 않는다.
/// 실제로 바뀐 편집은 구성 이력(<see cref="MeshShape.History"/>)에도 항목을 남긴다 — 앞 항목(Bevel 폭 등)을 고쳐 이력을 다시 실행할 때
/// 위상(하프에지·엣지 수)이 같으면 이 UV 결과를 슬롯 그대로 다시 써서, 예전처럼 그 뒤에 한 UV 작업이 통째로 사라지지 않게 한다.
/// </remarks>
public sealed class UvEditCommand : ICommand, IAppliedHook
{
    /// <summary>구성 이력 항목(실제로 바뀌었을 때 처음 등록하며 만든다).</summary>
    private HistoryEntry? _entry;
    /// <summary>대상 노드.</summary>
    private readonly NodeId _node;
    /// <summary>하프에지 슬롯별 변경 전/후 UV.</summary>
    private Vector2[] _uvBefore = Array.Empty<Vector2>(), _uvAfter = Array.Empty<Vector2>();
    /// <summary>엣지 슬롯별 변경 전/후 심 플래그.</summary>
    private bool[] _seamBefore = Array.Empty<bool>(), _seamAfter = Array.Empty<bool>();
    /// <summary>하프에지 슬롯별 변경 전/후 핀 플래그.</summary>
    private bool[] _pinBefore = Array.Empty<bool>(), _pinAfter = Array.Empty<bool>();
    /// <summary>즉시형 변경 동작(드래그형이면 null).</summary>
    private readonly Action<PolyMesh>? _apply;
    /// <summary>명령 이름.</summary>
    public string Name { get; }
    /// <summary>Commit 결과 실제로 바뀐 것이 있는지(없으면 호출자가 스택에 넣지 않는다).</summary>
    public bool Changed { get; private set; }

    /// <summary>즉시 실행형: apply가 메시 UV를 바꾼다.</summary>
    public UvEditCommand(string name, NodeId node, Action<PolyMesh> apply) { Name = name; _node = node; _apply = apply; }

    /// <summary>드래그형: Capture 후 바깥에서 바꾸고 Commit.</summary>
    public UvEditCommand(string name, NodeId node) { Name = name; _node = node; }

    /// <summary>모든 하프에지의 Uv0 복사.</summary>
    private static Vector2[] Uvs(PolyMesh m) { var a = new Vector2[m.HalfEdgeCount]; for (int i = 0; i < a.Length; i++) a[i] = m.Hes[i].Uv0; return a; }
    /// <summary>모든 엣지의 Seam 복사.</summary>
    private static bool[] Seams(PolyMesh m) { var a = new bool[m.EdgeCount]; for (int i = 0; i < a.Length; i++) a[i] = m.Edges[i].Seam; return a; }
    /// <summary>모든 하프에지의 PinUv 복사.</summary>
    private static bool[] Pins(PolyMesh m) { var a = new bool[m.HalfEdgeCount]; for (int i = 0; i < a.Length; i++) a[i] = m.Hes[i].PinUv; return a; }

    /// <summary>현재 UV·심·핀을 변경 전으로 저장한다.</summary>
    public void Capture(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        _uvBefore = Uvs(mesh); _seamBefore = Seams(mesh); _pinBefore = Pins(mesh);
    }

    /// <summary>현재 상태를 변경 후로 저장하고 <see cref="Changed"/>를 계산한다.</summary>
    public void Commit(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        _uvAfter = Uvs(mesh); _seamAfter = Seams(mesh); _pinAfter = Pins(mesh);
        Changed = !_uvBefore.AsSpan().SequenceEqual(_uvAfter) || !_seamBefore.AsSpan().SequenceEqual(_seamAfter) || !_pinBefore.AsSpan().SequenceEqual(_pinAfter);
    }

    /// <summary>첫 실행(후 스냅샷 없음)이면 Capture → apply → Commit, 아니면 후 상태 복원. MeshAttributes를 통지한다.</summary>
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
        RegisterHistory(doc);
    }

    /// <summary>드래그형(Capture/Commit 후 alreadyApplied로 들어옴): 이력 항목만 등록한다.</summary>
    public void OnPushedApplied(Document doc) => RegisterHistory(doc);

    /// <summary>
    /// 바뀐 것이 있으면 구성 이력에 항목을 더한다. Before = 지금 메시에 변경 전 UV·심·핀을 되돌린 복사본,
    /// Replay = 하프에지·엣지 수가 기록 때와 같으면 변경 후 배열을 슬롯 그대로 다시 쓴다(위상이 달라졌으면 건너뜀).
    /// </summary>
    private void RegisterHistory(Document doc)
    {
        if (!Changed || doc.Get(_node).MeshShape is not { } shape) return;
        if (_entry == null)
        {
            var snapshot = shape.Mesh.Clone();
            Restore(snapshot, _uvBefore, _seamBefore, _pinBefore);
            var uv = _uvAfter; var seam = _seamAfter; var pin = _pinAfter;
            _entry = new HistoryEntry
            {
                Name = Name, Before = snapshot,
                Replay = (m, _) => { if (m.HalfEdgeCount != uv.Length || m.EdgeCount != seam.Length) return false; Restore(m, uv, seam, pin); return true; },
            };
        }
        if (!shape.History.Contains(_entry)) { shape.History.Add(_entry); doc.Notify(new DocChange(ChangeKind.HistoryChanged, _node)); }
    }

    /// <summary>변경 전 UV·심·핀을 복원하고 이력 항목을 뺀다.</summary>
    public void Undo(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        Restore(mesh, _uvBefore, _seamBefore, _pinBefore);
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, _node));
        if (_entry != null && doc.Get(_node).MeshShape?.History.Remove(_entry) == true) doc.Notify(new DocChange(ChangeKind.HistoryChanged, _node));
    }

    /// <summary>배열을 메시에 다시 써 넣는다(구조체는 꺼내서 고친 뒤 되돌려 넣음, 길이가 다르면 짧은 쪽까지).</summary>
    private static void Restore(PolyMesh m, Vector2[] uvs, bool[] seams, bool[] pins)
    {
        for (int i = 0; i < uvs.Length && i < m.HalfEdgeCount; i++) { var h = m.Hes[i]; h.Uv0 = uvs[i]; if (i < pins.Length) h.PinUv = pins[i]; m.Hes[i] = h; }
        for (int i = 0; i < seams.Length && i < m.EdgeCount; i++) { var e = m.Edges[i]; e.Seam = seams[i]; m.Edges[i] = e; }
    }
}

/// <summary>UV 세트 구조 변경(생성/삭제/이름/전환/복사)의 전/후 스냅샷 명령.</summary>
/// <remarks>스냅샷 = (UV 세트 목록 깊은 복사, 현재 세트 인덱스, 현재 코너 UV). 현재 세트는 Uv0에 올라와 있으므로 함께 저장해야 전환을 되돌릴 수 있다.</remarks>
public sealed class UvSetsCommand : ICommand
{
    /// <summary>대상 노드와 구조 변경 동작.</summary>
    private readonly NodeId _node; private readonly Action<PolyMesh> _apply;
    /// <summary>변경 전/후 스냅샷. _after가 null이면 첫 실행.</summary>
    private (List<UvSet> sets, int current, Vector2[] uvs, bool[] seams, bool[] pins)? _before, _after;
    /// <summary>명령 이름.</summary>
    public string Name { get; }
    /// <summary>이름, 노드, 변경 동작을 받는다.</summary>
    public UvSetsCommand(string name, NodeId node, Action<PolyMesh> apply) { Name = name; _node = node; _apply = apply; }

    /// <summary>세트 목록(복제), 현재 세트 인덱스, 현재 코너 UV를 묶어 스냅샷을 만든다.</summary>
    private static (List<UvSet>, int, Vector2[], bool[], bool[]) Snap(PolyMesh m) => (m.UvSets.Select(s => s.Clone()).ToList(), m.CurrentUvSet, m.SnapshotUvs(), m.SnapshotSeams(), m.SnapshotPins());
    /// <summary>스냅샷으로 세트 목록·현재 인덱스·코너 UV·심·핀을 되돌린다(세트는 다시 복제해 공유 방지). 심/핀도 세트마다 다르므로 함께 되돌린다.</summary>
    private static void Restore(PolyMesh m, (List<UvSet> sets, int current, Vector2[] uvs, bool[] seams, bool[] pins) s)
    {
        m.UvSets.Clear(); foreach (var x in s.sets) m.UvSets.Add(x.Clone());
        m.CurrentUvSet = s.current;
        for (int i = 0; i < s.uvs.Length && i < m.HalfEdgeCount; i++) { var h = m.Hes[i]; h.Uv0 = s.uvs[i]; h.PinUv = i < s.pins.Length && s.pins[i]; m.Hes[i] = h; }
        for (int e = 0; e < s.seams.Length && e < m.EdgeCount; e++) { var ed = m.Edges[e]; ed.Seam = s.seams[e]; m.Edges[e] = ed; }
    }

    /// <summary>첫 실행이면 전 스냅샷 → apply → 후 스냅샷, Redo면 후 스냅샷 복원.</summary>
    public void Do(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        if (_after == null) { _before = Snap(mesh); _apply(mesh); _after = Snap(mesh); }
        else Restore(mesh, _after.Value);
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, _node));
    }

    /// <summary>전 스냅샷으로 복원한다.</summary>
    public void Undo(Document doc)
    {
        var mesh = doc.Get(_node).Mesh!;
        if (_before != null) Restore(mesh, _before.Value);
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, _node));
    }
}
