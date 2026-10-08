using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>
/// Component Editor 편집 한 번(셀 입력 또는 가운데 버튼 드래그)의 전/후 스냅샷 명령. 위상은 바뀌지 않으므로 정점 위치, 모든 UV 세트(현재 세트 = Uv0),
/// UV 핀, 코너 노멀·고정, 정점 노멀 잠금, 스킨 가중치를 통째로 저장한다. 즉시형(apply) 또는 Capture → 변경 → Commit 후 alreadyApplied로 넣는다.
/// </summary>
/// <remarks>
/// 스냅샷 배열의 인덱스 = 정점/하프에지 슬롯 ID(PolyMesh는 ID를 재사용하지 않으므로 위상이 같으면 그대로 대응한다).
/// 위상이 바뀌는 편집에는 쓰면 안 된다(그건 <c>MeshEditCommand</c>의 몫).
/// </remarks>
public sealed class ComponentEditCommand : ICommand
{
    /// <summary>한 시점의 속성 스냅샷(위상 제외).</summary>
    private sealed class State
    {
        /// <summary>정점 슬롯별 위치(메시 로컬).</summary>
        public Vector3[] Pos = Array.Empty<Vector3>();
        /// <summary>하프에지(코너)별 현재 UV(Uv0, 하단 원점).</summary>
        public Vector2[] Uv0 = Array.Empty<Vector2>();
        /// <summary>코너별 UV 핀 여부.</summary>
        public bool[] Pin = Array.Empty<bool>();
        /// <summary>코너별 노멀.</summary>
        public Vector3[] Nrm = Array.Empty<Vector3>();
        /// <summary>코너 노멀 고정 플래그(<c>HalfEdge.NormalLocked</c>).</summary>
        public bool[] NrmLock = Array.Empty<bool>();
        /// <summary>모든 UV 세트의 코너 UV 복사본(세트 순서 그대로).</summary>
        public Vector2[][] Sets = Array.Empty<Vector2[]>();
        /// <summary>정점 → 잠긴 노멀(<c>PolyMesh.LockedNormals</c>) 복사본.</summary>
        public Dictionary<int, Vector3> Locked = new();
        /// <summary>정점별 (조인트 인덱스, 가중치) 목록. 스킨이 없으면 null.</summary>
        public List<(int, float)>?[]? Weights;
    }

    /// <summary>편집 대상 노드.</summary>
    private readonly NodeId _node;
    /// <summary>즉시형 생성자에서 받은 변경 동작(메시, 스킨). Capture/Commit 방식이면 null.</summary>
    private readonly Action<PolyMesh, SkinCluster?>? _apply;
    /// <summary>변경 전/후 스냅샷. _after가 null이면 아직 한 번도 실행되지 않은 즉시형 명령이다.</summary>
    private State? _before, _after;
    /// <summary>Undo 메뉴에 표시할 이름.</summary>
    public string Name { get; }

    /// <summary>즉시형: Push 시 Do가 Capture → apply → Commit을 수행한다.</summary>
    public ComponentEditCommand(string name, NodeId node, Action<PolyMesh, SkinCluster?> apply) { Name = name; _node = node; _apply = apply; }
    /// <summary>드래그형: 호출자가 <see cref="Capture"/> → 직접 변경 → <see cref="Commit"/> 후 alreadyApplied로 넣는다.</summary>
    public ComponentEditCommand(string name, NodeId node) { Name = name; _node = node; }

    /// <summary>전후 스냅샷이 모두 있고 내용이 같으면 true(스택에 넣지 않아도 됨).</summary>
    public bool IsNoop => _before != null && _after != null && Same(_before, _after);

    /// <summary>현재 상태를 변경 전 스냅샷으로 저장한다.</summary>
    public void Capture(Document doc) => _before = Snap(doc.Get(_node));
    /// <summary>현재 상태를 변경 후 스냅샷으로 저장한다.</summary>
    public void Commit(Document doc) => _after = Snap(doc.Get(_node));

    /// <summary>처음이면 apply를 실행해 전후를 캡처하고, Redo면 후 스냅샷을 복원한다. 이어서 변경을 통지한다.</summary>
    public void Do(Document doc)
    {
        var n = doc.Get(_node);
        // 첫 실행(즉시형): 전 캡처 → 변경 → 후 캡처. Redo: 저장된 후 상태로 복원.
        if (_after == null) { Capture(doc); _apply?.Invoke(n.Mesh!, n.MeshShape?.Skin); Commit(doc); }
        else Restore(n, _after);
        NotifyAll(doc, _node, n.MeshShape?.Skin != null);
    }

    /// <summary>변경 전 스냅샷을 복원하고 통지한다.</summary>
    public void Undo(Document doc)
    {
        var n = doc.Get(_node);
        if (_before != null) Restore(n, _before);
        NotifyAll(doc, _node, n.MeshShape?.Skin != null);
    }

    /// <summary>미리보기(드래그 중)에서도 같은 통지를 쓴다.</summary>
    /// <remarks>지오메트리 버전을 올려 캐시(테셀레이션·UV 토폴로지)를 무효화하고, MeshAttributes(위상 불변 속성 변경)와 스킨이 있으면 SkinChanged를 보낸다.</remarks>
    public static void NotifyAll(Document doc, NodeId node, bool skinned)
    {
        var mesh = doc.Get(node).Mesh!;
        mesh.BumpGeometry();
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, node));
        if (skinned) doc.Notify(new DocChange(ChangeKind.SkinChanged, node));
    }

    /// <summary>노드의 메시·스킨 속성을 깊은 복사해 스냅샷을 만든다.</summary>
    private static State Snap(SceneNode n)
    {
        var m = n.Mesh!;
        // 배열 크기는 슬롯 수(죽은 슬롯 포함)라서 ID로 바로 인덱싱할 수 있다.
        var s = new State
        {
            Pos = new Vector3[m.VertexCount], Uv0 = new Vector2[m.HalfEdgeCount], Pin = new bool[m.HalfEdgeCount],
            Nrm = new Vector3[m.HalfEdgeCount], NrmLock = new bool[m.HalfEdgeCount],
            Sets = m.UvSets.Select(x => (Vector2[])x.Uvs.Clone()).ToArray(),
            Locked = new Dictionary<int, Vector3>(m.LockedNormals),
        };
        // 정점 위치와 코너 속성 복사.
        for (int i = 0; i < s.Pos.Length; i++) s.Pos[i] = m.Verts[i].Position;
        for (int i = 0; i < s.Uv0.Length; i++) { var h = m.Hes[i]; s.Uv0[i] = h.Uv0; s.Pin[i] = h.PinUv; s.Nrm[i] = h.Normal; s.NrmLock[i] = h.NormalLocked; }
        // 스킨 가중치는 정점별 리스트를 새로 만들어 복사(원본 리스트가 이후 변경돼도 영향 없음).
        if (n.MeshShape?.Skin is { } skin) s.Weights = skin.Weights.Select(w => w == null ? null : new List<(int, float)>(w)).ToArray();
        return s;
    }

    /// <summary>스냅샷을 메시·스킨에 다시 써 넣는다. 구조체(Vertex/HalfEdge)는 꺼내서 고친 뒤 되돌려 넣는다.</summary>
    private static void Restore(SceneNode n, State s)
    {
        var m = n.Mesh!;
        // 슬롯 수가 달라졌을 가능성에 대비해 짧은 쪽까지만 복원.
        for (int i = 0; i < s.Pos.Length && i < m.VertexCount; i++) { var v = m.Verts[i]; v.Position = s.Pos[i]; m.Verts[i] = v; }
        for (int i = 0; i < s.Uv0.Length && i < m.HalfEdgeCount; i++) { var h = m.Hes[i]; h.Uv0 = s.Uv0[i]; h.PinUv = s.Pin[i]; h.Normal = s.Nrm[i]; h.NormalLocked = s.NrmLock[i]; m.Hes[i] = h; }
        // UV 세트(현재 세트 외 보관본 포함)와 잠긴 노멀.
        for (int k = 0; k < s.Sets.Length && k < m.UvSets.Count; k++) m.UvSets[k].Uvs = (Vector2[])s.Sets[k].Clone();
        m.LockedNormals.Clear(); foreach (var (k, v) in s.Locked) m.LockedNormals[k] = v;
        // 가중치는 다시 복사해 넣어 스냅샷이 공유되지 않게 한다(Redo/Undo 반복 안전).
        if (s.Weights != null && n.MeshShape?.Skin is { } skin) skin.Weights = s.Weights.Select(w => w == null ? null : new List<(int, float)>(w)).ToArray();
    }

    /// <summary>두 스냅샷의 모든 항목이 같은지 비교한다(IsNoop 판정).</summary>
    private static bool Same(State a, State b)
    {
        // 배열 항목은 요소 단위 비교, UV 세트 개수까지 확인.
        if (!a.Pos.AsSpan().SequenceEqual(b.Pos) || !a.Uv0.AsSpan().SequenceEqual(b.Uv0) || !a.Pin.AsSpan().SequenceEqual(b.Pin)
            || !a.Nrm.AsSpan().SequenceEqual(b.Nrm) || !a.NrmLock.AsSpan().SequenceEqual(b.NrmLock) || a.Sets.Length != b.Sets.Length) return false;
        for (int k = 0; k < a.Sets.Length; k++) if (!a.Sets[k].AsSpan().SequenceEqual(b.Sets[k])) return false;
        // 잠긴 노멀 사전 비교.
        if (a.Locked.Count != b.Locked.Count || a.Locked.Any(kv => !b.Locked.TryGetValue(kv.Key, out var v) || v != kv.Value)) return false;
        // 가중치: 한쪽만 스킨이 있으면 다름, 둘 다 있으면 정점별 목록 비교(null과 빈 목록은 같다고 본다).
        if ((a.Weights == null) != (b.Weights == null)) return false;
        if (a.Weights != null)
            for (int i = 0; i < a.Weights.Length; i++)
            {
                var x = a.Weights[i]; var y = i < b.Weights!.Length ? b.Weights[i] : null;
                if ((x?.Count ?? 0) != (y?.Count ?? 0)) return false;
                if (x != null && y != null && !x.SequenceEqual(y)) return false;
            }
        return true;
    }
}
