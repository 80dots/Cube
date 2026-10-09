using System.Numerics;
using System.Runtime.CompilerServices;

namespace Cube.Core.Mesh;

/// <summary>정점. <see cref="HalfEdge"/>는 이 정점에서 출발하는 하프에지 하나(없으면 -1).</summary>
/// <remarks>구조체이므로 List에서 꺼내 고친 뒤 다시 넣어야 반영된다(var v = Verts[i]; ...; Verts[i] = v).</remarks>
public struct Vertex
{
    /// <summary>메시 로컬 공간 위치(m, Y-up).</summary>
    public Vector3 Position;
    /// <summary>이 정점에서 출발하는 하프에지 하나(대표). 고립 정점이면 -1. 정점 주위 순회의 시작점이며 면 삭제 시 RemoveFace가 보정한다.</summary>
    public int HalfEdge;
    /// <summary>살아 있는지. 삭제는 false로 표시만 하고 슬롯은 재사용하지 않는다(ID 불변).</summary>
    public bool Alive;
}

/// <summary>
/// 하프에지. 면의 한 코너(face-vertex)에 해당하며 코너 속성(노멀, UV)을 가진다.
/// <see cref="Twin"/>이 -1이면 경계(인접 면 없음).
/// </summary>
/// <remarks>
/// Next/Prev는 같은 면 루프 안에서 반시계 순서의 다음/이전 하프에지, Edge는 소속 무방향 엣지.
/// 코어의 하프에지 ID = 슬롯 인덱스이며 UV 세트 배열도 이 ID로 인덱싱한다.
/// </remarks>
public struct HalfEdge
{
    /// <summary>이 하프에지의 출발 정점 ID(코너가 놓인 정점).</summary>
    public int Vertex;   // 출발 정점
    /// <summary>같은 면 루프에서 다음 하프에지(끝 정점 = Next의 출발 정점).</summary>
    public int Next;
    /// <summary>같은 면 루프에서 이전 하프에지(이 코너로 들어오는 변).</summary>
    public int Prev;
    /// <summary>같은 엣지의 반대 방향 하프에지(이웃 면 쪽). -1이면 경계.</summary>
    public int Twin;     // -1 = 경계
    /// <summary>소속 면 ID.</summary>
    public int Face;
    /// <summary>소속 무방향 엣지 ID(<see cref="Edge"/>).</summary>
    public int Edge;
    /// <summary>코너 노멀(MeshNormals.Recompute가 하드 엣지를 고려해 채움). 표시·내보내기에 그대로 쓰인다.</summary>
    public Vector3 Normal;
    /// <summary>현재 UV 세트의 코너 UV(하단 원점, Maya 규약). Godot/glTF로 넘길 때 브리지에서 v = 1 - v.</summary>
    public Vector2 Uv0;
    /// <summary>UV 편집기 Pin: 이 코너의 UV 점은 Unfold/Optimize/브러시 등에서 움직이지 않는다.</summary>
    public bool PinUv;
    /// <summary>코너 노멀 고정(사용자 지정 분할 노멀, Bevel Harden Normals/Face Strength): MeshNormals.Recompute가 Normal을 덮어쓰지 않는다.</summary>
    public bool NormalLocked;
    /// <summary>살아 있는지(삭제 시 false, 슬롯 재사용 없음).</summary>
    public bool Alive;
}

/// <summary>UV 세트(Maya UV Sets): 하프에지 슬롯별 UV 배열. 현재 세트는 HalfEdge.Uv0에 올라와 있고 전환 시 저장/복원된다.</summary>
/// <remarks>배열 인덱스 = 하프에지 ID. 하프에지가 늘어난 뒤에는 배열이 짧을 수 있으므로 읽는 쪽이 길이를 확인한다.</remarks>
public sealed class UvSet
{
    /// <summary>세트 이름(Maya 기본 "map1").</summary>
    public string Name = "map1";
    /// <summary>하프에지 슬롯별 UV. 현재 세트의 값은 전환/저장 시점의 스냅샷이고 최신 값은 HalfEdge.Uv0에 있다.</summary>
    public Vector2[] Uvs = Array.Empty<Vector2>();
    /// <summary>엣지 슬롯별 UV 심(Edge.Seam). 현재 세트의 값은 전환/저장 시점의 스냅샷. null이면 기록이 없어 전환 때 메시 심을 그대로 둔다(예전 파일).</summary>
    /// <remarks>심은 UV 세트마다 다르다(Maya도 UV 불연속은 세트별) — 메시 하나에 공유하면 다른 세트에서 자르거나 투영한 심이 이 세트의 심을 덮었다.</remarks>
    public bool[]? Seams;
    /// <summary>하프에지 슬롯별 UV 핀(HalfEdge.PinUv). null이면 전환 때 메시 핀을 그대로 둔다.</summary>
    public bool[]? Pins;
    /// <summary>배열까지 복사한 깊은 복제(메시 Clone/스냅샷용).</summary>
    public UvSet Clone() => new() { Name = Name, Uvs = (Vector2[])Uvs.Clone(), Seams = (bool[]?)Seams?.Clone(), Pins = (bool[]?)Pins?.Clone() };
}

/// <summary>무방향 엣지. 최대 2개의 하프에지(He1은 경계면 -1)를 가진다. 비매니폴드(3면 이상)는 허용하지 않는다.</summary>
/// <remarks>He0은 항상 살아 있는 하프에지이고, 한쪽 면이 삭제되면 남은 쪽이 He0으로 당겨진다(RemoveFace).</remarks>
public struct Edge
{
    /// <summary>첫 번째 하프에지(엣지 방향의 기준; EdgeVertices는 He0의 시작→끝 정점).</summary>
    public int He0;
    /// <summary>반대쪽 하프에지(He0의 트윈). 경계 엣지면 -1.</summary>
    public int He1;      // -1 = 경계
    /// <summary>하드 엣지: 노멀 평활을 끊는다(Maya Harden Edge). 양쪽 코너 노멀이 각자 면 노멀을 따른다.</summary>
    public bool Hard;
    /// <summary>UV 심(Cut UV Edges). 양쪽 코너 UV가 같아도 별개의 UV 점으로 취급한다.</summary>
    public bool Seam;
    /// <summary>서브디비전 크리즈 단계(Maya Crease, 0 = 없음). 1 이상이면 그 단계만큼 Catmull-Clark에서 날카롭게 유지된다.</summary>
    public float Crease;
    /// <summary>살아 있는지(삭제 시 false).</summary>
    public bool Alive;
}

/// <summary>면(n각형). 하프에지 루프 하나로 정의되며 루프 순서가 반시계면 앞면(코어 규약).</summary>
public struct Face
{
    /// <summary>면 루프에 속한 아무 하프에지(순회 시작점). AddFace는 첫 정점의 하프에지로 둔다.</summary>
    public int HalfEdge; // 루프의 아무 하프에지
    /// <summary>면별 머티리얼 슬롯 번호(0 = 기본). 내보내기에서 서피스 분할에 쓴다.</summary>
    public int Material;
    /// <summary>살아 있는지(삭제 시 false).</summary>
    public bool Alive;
    /// <summary>정규화된 면 노멀 캐시. 위치를 바꾼 뒤에는 Recompute 전까지 낡은 값일 수 있다.</summary>
    public Vector3 Normal; // MeshNormals.Recompute가 채우는 캐시
}

/// <summary>
/// Maya 식 폴리곤 메시(n-gon, 코너별 UV/노멀, 하드/소프트 엣지)를 하프에지 구조로 보관한다.
/// 모든 ID는 슬롯 인덱스이며 삭제 시 <c>Alive=false</c>만 표시하고 재사용하지 않는다(세션 중 ID 불변).
/// <see cref="Compact"/>는 저장/내보내기 직전에만 호출한다.
/// </summary>
public sealed class PolyMesh
{
    /// <summary>정점 슬롯 목록(ID = 인덱스, 죽은 슬롯 포함).</summary>
    public readonly List<Vertex> Verts = new();
    /// <summary>하프에지(코너) 슬롯 목록(ID = 인덱스).</summary>
    public readonly List<HalfEdge> Hes = new();
    /// <summary>무방향 엣지 슬롯 목록(ID = 인덱스).</summary>
    public readonly List<Edge> Edges = new();
    /// <summary>면 슬롯 목록(ID = 인덱스).</summary>
    public readonly List<Face> Faces = new();

    /// <summary>위상(정점/엣지/면 추가·삭제·연결)이 바뀔 때마다 증가.</summary>
    public int TopologyVersion { get; private set; }
    /// <summary>정점 위치만 바뀔 때 증가(드래그 프리뷰 등).</summary>
    public int GeometryVersion { get; private set; }

    /// <summary>잠긴 정점 노멀(Mesh Display → Lock Normals / Set Vertex Normal / Set to Face / Average). 재계산 시 이 정점의 코너 노멀은 저장된 값으로 고정된다.</summary>
    public readonly Dictionary<int, Vector3> LockedNormals = new();

    /// <summary>UV 세트 목록. 비어 있으면 단일 기본 세트("map1")만 있는 것으로 본다. <see cref="CurrentUvSet"/>의 UV가 HalfEdge.Uv0에 올라와 있다.</summary>
    public readonly List<UvSet> UvSets = new();
    /// <summary>현재 UV 세트 인덱스(UvSets 안의 위치). 이 세트의 UV가 HalfEdge.Uv0에 올라와 있다.</summary>
    public int CurrentUvSet;

    /// <summary>세트 목록이 비어 있으면 현재 UV로 "map1"을 만든다.</summary>
    public void EnsureUvSets()
    {
        if (UvSets.Count > 0) return;
        UvSets.Add(new UvSet { Name = "map1", Uvs = SnapshotUvs(), Seams = SnapshotSeams(), Pins = SnapshotPins() });
        CurrentUvSet = 0;
    }

    /// <summary>모든 엣지 슬롯의 Seam을 새 배열로 복사한다(인덱스 = 엣지 ID).</summary>
    public bool[] SnapshotSeams() { var a = new bool[Edges.Count]; for (int i = 0; i < a.Length; i++) a[i] = Edges[i].Seam; return a; }
    /// <summary>모든 하프에지 슬롯의 PinUv를 새 배열로 복사한다(인덱스 = 하프에지 ID).</summary>
    public bool[] SnapshotPins() { var a = new bool[Hes.Count]; for (int i = 0; i < a.Length; i++) a[i] = Hes[i].PinUv; return a; }

    /// <summary>모든 하프에지 슬롯의 Uv0을 새 배열로 복사한다(인덱스 = 하프에지 ID).</summary>
    public Vector2[] SnapshotUvs() { var a = new Vector2[Hes.Count]; for (int i = 0; i < a.Length; i++) a[i] = Hes[i].Uv0; return a; }

    /// <summary>현재 코너 UV를 현재 세트 배열에 저장한다.</summary>
    public void StoreCurrentUvs()
    {
        EnsureUvSets();
        var cur = UvSets[Math.Clamp(CurrentUvSet, 0, UvSets.Count - 1)];
        cur.Uvs = SnapshotUvs(); cur.Seams = SnapshotSeams(); cur.Pins = SnapshotPins();
    }

    /// <summary>세트를 전환한다: 현재 UV를 저장하고 대상 세트를 코너에 올린다(배열이 짧으면 0).</summary>
    public void SwitchUvSet(int index)
    {
        EnsureUvSets();
        if (index < 0 || index >= UvSets.Count) return;
        StoreCurrentUvs();
        var set = UvSets[index];
        var src = set.Uvs;
        for (int h = 0; h < Hes.Count; h++)
        {
            var he = Hes[h]; he.Uv0 = h < src.Length ? src[h] : Vector2.Zero;
            if (set.Pins != null) he.PinUv = h < set.Pins.Length && set.Pins[h];
            Hes[h] = he;
        }
        // 세트의 심을 올린다(배열보다 늘어난 엣지는 심 없음). 기록이 없는 세트(예전 파일)는 메시 심을 그대로 둔다.
        if (set.Seams != null)
            for (int e = 0; e < Edges.Count; e++) { var ed = Edges[e]; ed.Seam = e < set.Seams.Length && set.Seams[e]; Edges[e] = ed; }
        CurrentUvSet = index;
        GeometryVersion++;
    }

    /// <summary>
    /// 다른 세트와 겹치지 않는 세트 이름: 이미 쓰는 이름이면 끝의 숫자를 늘리거나(uvSet1 → uvSet2) 숫자를 붙인다(map1_copy → map1_copy1).
    /// </summary>
    /// <param name="name">원하는 이름.</param>
    /// <param name="exceptIndex">비교에서 뺄 세트(이름 바꾸기 대상 자신), 없으면 −1.</param>
    public string UniqueUvSetName(string name, int exceptIndex = -1)
    {
        bool Taken(string n) { for (int i = 0; i < UvSets.Count; i++) if (i != exceptIndex && UvSets[i].Name == n) return true; return false; }
        if (!Taken(name)) return name;
        // 끝의 숫자 부분과 앞부분으로 나눠 숫자를 올린다
        int k = name.Length; while (k > 0 && char.IsDigit(name[k - 1])) k--;
        string stem = name[..k]; int n = k < name.Length ? int.Parse(name[k..]) : 0;
        string cand;
        do cand = stem + (++n); while (Taken(cand));
        return cand;
    }

    /// <summary>새 UV 세트를 만든다(copyCurrent면 현재 UV 복사, 아니면 0). 이름이 이미 있으면 숫자를 붙여 겹치지 않게 한다. 반환값은 인덱스.</summary>
    public int AddUvSet(string name, bool copyCurrent)
    {
        EnsureUvSets();
        StoreCurrentUvs();
        name = UniqueUvSetName(name);
        // 복사 세트는 심·핀도 그대로, 빈 세트는 심·핀 없음
        UvSets.Add(new UvSet { Name = name, Uvs = copyCurrent ? SnapshotUvs() : new Vector2[Hes.Count], Seams = copyCurrent ? SnapshotSeams() : new bool[Edges.Count], Pins = copyCurrent ? SnapshotPins() : new bool[Hes.Count] });
        return UvSets.Count - 1;
    }

    /// <summary>UV 세트를 지운다. 마지막 하나는 지울 수 없고, 현재 세트를 지우면 먼저 다른 세트(0번 또는 1번)로 전환한 뒤 인덱스를 보정한다.</summary>
    public void RemoveUvSet(int index)
    {
        EnsureUvSets();
        if (UvSets.Count <= 1 || index < 0 || index >= UvSets.Count) return;
        if (index == CurrentUvSet) SwitchUvSet(index == 0 ? 1 : 0);
        UvSets.RemoveAt(index);
        if (CurrentUvSet > index) CurrentUvSet--;
    }

    // 위상 캐시: 둘 다 생성 시점의 TopologyVersion을 기억해 버전이 다르면 무효로 보고 다시 만든다.
    // AddFace/RemoveFace처럼 캐시를 직접 갱신하는 연산은 버전을 올린 뒤 캐시 버전도 같이 올려 유효 상태를 유지한다.
    private Dictionary<long, int>? _edgeMap;      // (min,max) 정점쌍 → edge id
    private int _edgeMapVersion = -1;
    private int[][]? _vertexOutgoing;             // vertex → 출발 하프에지 목록
    private int _vertexOutgoingVersion = -1;

    /// <summary>정점 슬롯 수(죽은 슬롯 포함). ID 배열 크기를 정할 때 쓴다.</summary>
    public int VertexCount => Verts.Count;
    /// <summary>하프에지 슬롯 수(죽은 슬롯 포함).</summary>
    public int HalfEdgeCount => Hes.Count;
    /// <summary>엣지 슬롯 수(죽은 슬롯 포함).</summary>
    public int EdgeCount => Edges.Count;
    /// <summary>면 슬롯 수(죽은 슬롯 포함).</summary>
    public int FaceCount => Faces.Count;

    /// <summary>살아 있는 정점 수(매번 전체를 센다, O(n)).</summary>
    public int AliveVertexCount { get { int n = 0; foreach (var v in Verts) if (v.Alive) n++; return n; } }
    /// <summary>살아 있는 엣지 수(O(n)).</summary>
    public int AliveEdgeCount { get { int n = 0; foreach (var e in Edges) if (e.Alive) n++; return n; } }
    /// <summary>살아 있는 면 수(O(n)).</summary>
    public int AliveFaceCount { get { int n = 0; foreach (var f in Faces) if (f.Alive) n++; return n; } }

    /// <summary>구조를 직접 고친 연산이 위상 변경을 알린다: 두 버전을 올려 캐시와 표시(MeshView 재구성)를 무효화한다.</summary>
    public void BumpTopology() { TopologyVersion++; GeometryVersion++; }
    /// <summary>정점 위치만 바꾼 뒤 호출: 위상 캐시는 유지하고 표시 위치만 갱신하게 한다.</summary>
    public void BumpGeometry() { GeometryVersion++; }

    // ---------------------------------------------------------------- 생성

    /// <summary>
    /// 고립 정점 하나를 추가한다(HalfEdge = -1). 면은 AddFace로 따로 만든다.
    /// </summary>
    /// <returns>새 정점 ID(= 이전 정점 수).</returns>
    public int AddVertex(Vector3 position)
    {
        Verts.Add(new Vertex { Position = position, HalfEdge = -1, Alive = true });
        // 새 정점은 엣지·하프에지를 바꾸지 않으므로 유효했던 캐시는 그대로 유효하다.
        // (예전에는 여기서 엣지 맵이 무효가 되어, 정점과 면을 번갈아 추가하는 가져오기/연산에서 면마다 엣지 맵 전체를 다시 만들었다 → O(면²))
        bool edgeMapValid = _edgeMap != null && _edgeMapVersion == TopologyVersion;
        bool outgoingValid = _vertexOutgoing != null && _vertexOutgoingVersion == TopologyVersion;
        TopologyVersion++;
        if (edgeMapValid) _edgeMapVersion = TopologyVersion;
        if (outgoingValid) _vertexOutgoingVersion = TopologyVersion; // VertexOutgoing은 배열 밖 정점에 빈 목록을 돌려준다
        return Verts.Count - 1;
    }

    /// <summary>
    /// 정점 ID 목록(반시계 = 앞면)으로 면을 추가한다. 하프에지와 엣지를 만들고 기존 엣지와 트윈을 연결한다.
    /// 3개 미만, 중복 정점, 이미 2면이 붙은 엣지(비매니폴드)면 -1을 반환하고 아무것도 바꾸지 않는다.
    /// </summary>
    /// <param name="verts">면 루프 정점 ID(바깥에서 볼 때 반시계 순서). 첫 정점의 하프에지가 Faces[f].HalfEdge가 된다.</param>
    /// <param name="material">면 머티리얼 슬롯 번호.</param>
    /// <returns>새 면 ID, 거부되면 -1.</returns>
    public int AddFace(ReadOnlySpan<int> verts, int material = 0)
    {
        // 입력 검증: 존재하는 살아 있는 정점이어야 하고, 같은 정점이 두 번 나오면 안 된다
        int n = verts.Length;
        if (n < 3) return -1;
        for (int i = 0; i < n; i++)
        {
            if ((uint)verts[i] >= (uint)Verts.Count || !Verts[verts[i]].Alive) return -1;
            for (int j = i + 1; j < n; j++) if (verts[i] == verts[j]) return -1;
        }
        EnsureEdgeMap();
        // 사전 검사: 모든 엣지가 수용 가능한지
        for (int i = 0; i < n; i++)
        {
            int a = verts[i], b = verts[(i + 1) % n];
            if (_edgeMap!.TryGetValue(Key(a, b), out int e))
            {
                var ed = Edges[e];
                if (ed.He1 != -1) return -1; // 이미 2면
                // 같은 방향 하프에지가 이미 있으면(면이 뒤집힘) 비매니폴드 → 거부
                if (Hes[ed.He0].Vertex == a) return -1;
            }
        }

        // 새 면과 하프에지 n개를 연속 슬롯에 만든다. Next/Prev는 같은 블록 안의 순환 인덱스
        int faceId = Faces.Count;
        int heBase = Hes.Count;
        Faces.Add(new Face { HalfEdge = heBase, Material = material, Alive = true });
        for (int i = 0; i < n; i++)
        {
            Hes.Add(new HalfEdge
            {
                Vertex = verts[i],
                Next = heBase + (i + 1) % n,
                Prev = heBase + (i + n - 1) % n,
                Twin = -1,
                Face = faceId,
                Edge = -1,
                Alive = true,
            });
        }
        // 각 변을 엣지에 연결: 기존 엣지(반대 방향 면이 있음)면 He1과 트윈으로 묶고, 없으면 새 경계 엣지를 만든다
        for (int i = 0; i < n; i++)
        {
            int a = verts[i], b = verts[(i + 1) % n];
            int he = heBase + i;
            long key = Key(a, b);
            if (_edgeMap!.TryGetValue(key, out int e))
            {
                var ed = Edges[e];
                ed.He1 = he;
                Edges[e] = ed;
                var h0 = Hes[ed.He0]; h0.Twin = he; Hes[ed.He0] = h0;
                var h1 = Hes[he]; h1.Twin = ed.He0; h1.Edge = e; Hes[he] = h1;
            }
            else
            {
                e = Edges.Count;
                Edges.Add(new Edge { He0 = he, He1 = -1, Hard = false, Alive = true });
                _edgeMap[key] = e;
                var h = Hes[he]; h.Edge = e; Hes[he] = h;
            }
            // 정점의 대표 하프에지가 없거나 죽었으면 이 하프에지로 지정
            var v = Verts[a];
            if (v.HalfEdge == -1 || !Hes[v.HalfEdge].Alive) { v.HalfEdge = he; Verts[a] = v; }
        }
        TopologyVersion++;
        _edgeMapVersion = TopologyVersion; // 직접 갱신했으므로 유효
        return faceId;
    }

    // ---------------------------------------------------------------- 삭제

    /// <summary>면을 삭제한다. 고립되는 엣지/정점은 함께 삭제한다.</summary>
    /// <param name="faceId">삭제할 면 ID(없거나 이미 죽었으면 무시).</param>
    /// <param name="removeIsolated">true면 더 이상 하프에지가 없는 정점도 죽인다. false면 정점은 고립 상태로 남는다(대표 하프에지 -1).</param>
    public void RemoveFace(int faceId, bool removeIsolated = true)
    {
        if ((uint)faceId >= (uint)Faces.Count || !Faces[faceId].Alive) return;
        // 면 루프의 하프에지를 먼저 모은다(순회 중 구조를 바꾸므로)
        var f = Faces[faceId];
        // 엣지 맵을 먼저 유효하게 만든다: 아래에서 엣지가 하나도 죽지 않으면 맵을 건드리지 않은 채 '유효'로 표시하므로,
        // 이전 버전의 오래된 맵이 유효로 둔갑하지 않게 한다(v0.0.55; 슬롯을 직접 이어 붙인 뒤 RemoveFace를 부르는 Array에서 드러남)
        EnsureEdgeMap();
        int start = f.HalfEdge, he = start;
        var loopList = new List<int>();
        do { loopList.Add(he); he = Hes[he].Next; } while (he != start);

        // 하프에지마다: 엣지에서 떼고(He0이 빠지면 He1을 당김), 트윈의 Twin을 끊고, 엣지에 남은 하프에지가 없으면 엣지도 죽이고 엣지 맵에서 제거
        foreach (int h in loopList)
        {
            var hh = Hes[h];
            int e = hh.Edge;
            var ed = Edges[e];
            if (ed.He0 == h) { ed.He0 = ed.He1; ed.He1 = -1; }
            else if (ed.He1 == h) { ed.He1 = -1; }
            if (hh.Twin != -1)
            {
                var t = Hes[hh.Twin]; t.Twin = -1; Hes[hh.Twin] = t;
            }
            if (ed.He0 == -1)
            {
                ed.Alive = false;
                EnsureEdgeMap();
                _edgeMap!.Remove(Key(hh.Vertex, Hes[hh.Next].Vertex));
            }
            Edges[e] = ed;
            hh.Alive = false; hh.Twin = -1;
            Hes[h] = hh;
        }
        f.Alive = false;
        Faces[faceId] = f;
        // 위상 버전 증가. 엣지 맵은 위에서 직접 갱신했으므로 유효로 표시(정점 출발 캐시는 무효가 됨)
        TopologyVersion++;
        _edgeMapVersion = TopologyVersion;

        // 정점의 대표 하프에지 보정 및 고립 정점 삭제
        foreach (int h in loopList)
        {
            int v = Hes[h].Vertex;
            var vert = Verts[v];
            // 대표 하프에지가 방금 죽은 하프에지면 살아 있는 다른 출발 하프에지로 교체, 없으면 고립 정점
            if (vert.HalfEdge == h || !Hes[vert.HalfEdge].Alive)
            {
                vert.HalfEdge = FindOutgoing(v);
                if (vert.HalfEdge == -1 && removeIsolated) vert.Alive = false;
                Verts[v] = vert;
            }
        }
    }

    /// <summary>정점에서 출발하는 살아 있는 하프에지가 없으면(고립) 정점을 죽인다.</summary>
    public void RemoveVertexIfIsolated(int v)
    {
        var vert = Verts[v];
        if (!vert.Alive) return;
        if (FindOutgoing(v) == -1) { vert.Alive = false; vert.HalfEdge = -1; Verts[v] = vert; TopologyVersion++; }
    }

    /// <summary>정점에서 출발하는 살아 있는 하프에지 하나를 전체 선형 탐색으로 찾는다(없으면 -1). 대표 하프에지 보정용.</summary>
    private int FindOutgoing(int v)
    {
        for (int i = 0; i < Hes.Count; i++) if (Hes[i].Alive && Hes[i].Vertex == v) return i;
        return -1;
    }

    // ---------------------------------------------------------------- 조회

    /// <summary>무방향 정점 쌍 키: 작은 ID를 상위 32비트, 큰 ID를 하위 32비트에 넣어 (a,b)와 (b,a)가 같은 키가 된다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// 디버그/테스트: 유효하다고 판단된 캐시를 처음부터 다시 만든 결과와 비교해 불일치면 예외를 던진다(위상 버전마다 한 번).
    /// 테스트 프로젝트가 모듈 초기화에서 켠다(CacheVerifyInit) — 직접 구조를 고친 뒤 캐시를 무효화하지 않는 연산을 바로 잡아낸다.
    /// 검증은 O(엣지)라 하프에지 2천 개 미만인 메시에서만 한다(대형 가져오기 성능 테스트가 O(n²)이 되지 않게).
    /// </summary>
    public static bool VerifyCaches;
    // 캐시 검증을 마지막으로 수행한 위상 버전(같은 버전에서 반복 검증하지 않도록)
    private int _edgeMapVerified = -1, _outgoingVerified = -1;

    /// <summary>엣지 맵((min,max) 정점쌍 → 엣지 ID)이 현재 위상 버전에 유효하도록 보장한다. 무효면 살아 있는 엣지로 다시 만든다(O(엣지)).</summary>
    private void EnsureEdgeMap()
    {
        // 캐시가 현재 버전이면 그대로 사용(테스트에서는 버전마다 한 번 무결성 검증)
        if (_edgeMap != null && _edgeMapVersion == TopologyVersion)
        {
            if (VerifyCaches && Hes.Count < 2000 && _edgeMapVerified != TopologyVersion) { VerifyEdgeMap(); _edgeMapVerified = TopologyVersion; }
            return;
        }
        // 재구성: 사전 객체는 재사용하고 살아 있는 엣지만 다시 넣는다. 정점 쌍 키 해시가 몰리지 않게 PairKeyComparer 사용
        _edgeMap ??= new Dictionary<long, int>(PairKeyComparer.Instance);
        _edgeMap.Clear();
        for (int e = 0; e < Edges.Count; e++)
        {
            if (!Edges[e].Alive) continue;
            var (a, b) = EdgeVertices(e);
            _edgeMap[Key(a, b)] = e;
        }
        _edgeMapVersion = TopologyVersion;
    }

    /// <summary>디버그: 엣지 맵을 새로 만든 결과와 비교해 개수나 항목이 다르면 InvalidOperationException.</summary>
    private void VerifyEdgeMap()
    {
        var fresh = new Dictionary<long, int>(PairKeyComparer.Instance);
        for (int e = 0; e < Edges.Count; e++)
        {
            if (!Edges[e].Alive) continue;
            var (a, b) = EdgeVertices(e);
            fresh[Key(a, b)] = e;
        }
        if (fresh.Count != _edgeMap!.Count) throw new InvalidOperationException($"edge map stale: count {_edgeMap.Count} vs {fresh.Count}");
        foreach (var kv in fresh)
            if (!_edgeMap.TryGetValue(kv.Key, out int e) || e != kv.Value) throw new InvalidOperationException($"edge map stale: key ({kv.Key >> 32},{(int)kv.Key}) → {(_edgeMap.TryGetValue(kv.Key, out int ee) ? ee : -1)} vs {kv.Value}");
    }

    /// <summary>디버그: 정점별 출발 하프에지 캐시를 실제 하프에지와 비교해 개수나 소속이 다르면 InvalidOperationException.</summary>
    private void VerifyOutgoing()
    {
        var counts = new int[Verts.Count];
        for (int i = 0; i < Hes.Count; i++) if (Hes[i].Alive) counts[Hes[i].Vertex]++;
        for (int v = 0; v < Verts.Count; v++)
        {
            int cached = v < _vertexOutgoing!.Length ? _vertexOutgoing[v].Length : 0;
            if (cached != counts[v]) throw new InvalidOperationException($"vertex outgoing stale: v{v} cached {cached} vs {counts[v]}");
        }
        for (int i = 0; i < Hes.Count; i++)
        {
            if (!Hes[i].Alive) continue;
            int v = Hes[i].Vertex;
            if (v >= _vertexOutgoing!.Length || Array.IndexOf(_vertexOutgoing[v], i) < 0) throw new InvalidOperationException($"vertex outgoing stale: he{i} missing at v{v}");
        }
    }

    /// <summary>두 정점을 잇는 살아있는 엣지 ID, 없으면 -1.</summary>
    public int FindEdge(int a, int b)
    {
        EnsureEdgeMap();
        return _edgeMap!.TryGetValue(Key(a, b), out int e) ? e : -1;
    }

    /// <summary>엣지의 두 끝 정점(He0의 시작 정점, He0의 끝 정점 순서).</summary>
    public (int a, int b) EdgeVertices(int e)
    {
        int h = Edges[e].He0;
        return (Hes[h].Vertex, Hes[Hes[h].Next].Vertex);
    }

    /// <summary>엣지 양쪽 면 ID(He0 쪽, He1 쪽). 경계면 f1 = -1.</summary>
    public (int f0, int f1) EdgeFaces(int e)
    {
        var ed = Edges[e];
        return (ed.He0 == -1 ? -1 : Hes[ed.He0].Face, ed.He1 == -1 ? -1 : Hes[ed.He1].Face);
    }

    /// <summary>경계 엣지(한쪽 면만 있음)인지.</summary>
    public bool IsBoundaryEdge(int e) => Edges[e].He1 == -1;

    /// <summary>면의 하프에지를 루프 순서대로 채운다. 반환값은 개수.</summary>
    public int GetFaceHalfEdges(int f, List<int> result)
    {
        result.Clear();
        int start = Faces[f].HalfEdge, he = start;
        do { result.Add(he); he = Hes[he].Next; } while (he != start);
        return result.Count;
    }

    /// <summary>면의 정점 ID를 루프 순서(반시계)대로 채운다. 반환값은 개수(= 면 차수).</summary>
    public int GetFaceVertices(int f, List<int> result)
    {
        result.Clear();
        int start = Faces[f].HalfEdge, he = start;
        do { result.Add(Hes[he].Vertex); he = Hes[he].Next; } while (he != start);
        return result.Count;
    }

    /// <summary>면의 차수(꼭짓점 수). 루프를 한 바퀴 돌며 센다.</summary>
    public int FaceDegree(int f)
    {
        int start = Faces[f].HalfEdge, he = start, n = 0;
        do { n++; he = Hes[he].Next; } while (he != start);
        return n;
    }

    /// <summary>정점에서 출발하는 살아있는 하프에지 목록(비매니폴드 정점도 포함). 위상 버전별로 캐시된다.</summary>
    public ReadOnlySpan<int> VertexOutgoing(int v)
    {
        if (VerifyCaches && Hes.Count < 2000 && _vertexOutgoing != null && _vertexOutgoingVersion == TopologyVersion && _outgoingVerified != TopologyVersion) { VerifyOutgoing(); _outgoingVerified = TopologyVersion; }
        // 캐시 재구성: 정점별 개수를 센 뒤 정확한 크기의 배열을 만들고 두 번째 패스에서 채운다(계수 정렬 방식)
        if (_vertexOutgoing == null || _vertexOutgoingVersion != TopologyVersion)
        {
            var counts = new int[Verts.Count];
            for (int i = 0; i < Hes.Count; i++) if (Hes[i].Alive) counts[Hes[i].Vertex]++;
            _vertexOutgoing = new int[Verts.Count][];
            for (int i = 0; i < Verts.Count; i++) _vertexOutgoing[i] = new int[counts[i]];
            Array.Clear(counts);
            for (int i = 0; i < Hes.Count; i++)
                if (Hes[i].Alive) { int vv = Hes[i].Vertex; _vertexOutgoing[vv][counts[vv]++] = i; }
            _vertexOutgoingVersion = TopologyVersion;
        }
        return v < _vertexOutgoing.Length ? _vertexOutgoing[v] : ReadOnlySpan<int>.Empty;
    }

    /// <summary>정점에 붙은 면 목록(출발 하프에지마다 그 면, 정점 주위 순서는 보장하지 않음).</summary>
    public void GetVertexFaces(int v, List<int> result)
    {
        result.Clear();
        foreach (int he in VertexOutgoing(v)) result.Add(Hes[he].Face);
    }

    /// <summary>
    /// 정점에 붙은 엣지 목록(중복 없음). 출발 하프에지의 엣지와 들어오는 하프에지(Prev)의 엣지를 모두 넣어
    /// 경계 정점에서 트윈이 없는 쪽 엣지도 빠지지 않게 한다.
    /// </summary>
    public void GetVertexEdges(int v, List<int> result)
    {
        result.Clear();
        foreach (int he in VertexOutgoing(v))
        {
            int e = Hes[he].Edge;
            if (!result.Contains(e)) result.Add(e);
            int prev = Hes[he].Prev; // 들어오는 하프에지의 엣지(경계일 때 트윈이 없어도 잡힘)
            int e2 = Hes[prev].Edge;
            if (!result.Contains(e2)) result.Add(e2);
        }
    }

    /// <summary>면 중심 = 꼭짓점 위치 산술 평균(면적 가중 아님).</summary>
    public Vector3 FaceCentroid(int f)
    {
        var sum = Vector3.Zero; int n = 0;
        int start = Faces[f].HalfEdge, he = start;
        do { sum += Verts[Hes[he].Vertex].Position; n++; he = Hes[he].Next; } while (he != start);
        return sum / n;
    }

    // ---------------------------------------------------------------- 복제/압축

    /// <summary>깊은 복제(위상·속성·잠긴 노멀·UV 세트 포함, 캐시는 새로 만든다). Undo 스냅샷과 히스토리 Before에 쓴다.</summary>
    public PolyMesh Clone()
    {
        var m = new PolyMesh();
        m.CopyFrom(this);
        return m;
    }

    /// <summary>다른 메시의 내용으로 통째로 덮어쓴다(구조체 리스트 복사 + UV 세트 깊은 복사). 버전을 올리고 캐시를 버린다.</summary>
    public void CopyFrom(PolyMesh src)
    {
        Verts.Clear(); Verts.AddRange(src.Verts);
        Hes.Clear(); Hes.AddRange(src.Hes);
        Edges.Clear(); Edges.AddRange(src.Edges);
        Faces.Clear(); Faces.AddRange(src.Faces);
        LockedNormals.Clear(); foreach (var kv in src.LockedNormals) LockedNormals[kv.Key] = kv.Value;
        UvSets.Clear(); foreach (var s in src.UvSets) UvSets.Add(s.Clone());
        CurrentUvSet = src.CurrentUvSet;
        TopologyVersion++;
        GeometryVersion++;
        _edgeMap = null; _vertexOutgoing = null;
    }

    /// <summary>모든 요소와 UV 세트·잠긴 노멀을 지워 빈 메시로 만든다(버전 증가, 캐시 폐기).</summary>
    public void Clear()
    {
        Verts.Clear(); Hes.Clear(); Edges.Clear(); Faces.Clear(); LockedNormals.Clear(); UvSets.Clear(); CurrentUvSet = 0;
        TopologyVersion++; GeometryVersion++;
        _edgeMap = null; _vertexOutgoing = null;
    }

    /// <summary>죽은 슬롯을 제거하고 ID를 다시 매긴다. 반환된 리맵은 old→new (삭제된 것은 -1).</summary>
    /// <remarks>
    /// ID가 바뀌므로 선택·Undo·히스토리 같은 ID 참조가 깨진다. 그래서 편집 중에는 부르지 않고 저장/내보내기 직전에만 호출한다.
    /// 순서: ① 종류별 old→new 맵(살아 있는 것만 순서대로 번호) ② 참조 필드를 맵으로 바꿔 새 리스트 구성
    /// ③ 잠긴 노멀 키와 UV 세트 배열도 새 ID로 재배치 ④ 버전 증가·캐시 폐기.
    /// </remarks>
    public CompactRemap Compact()
    {
        // ① 종류별 old→new 리맵(삭제된 슬롯은 -1)
        var vMap = new int[Verts.Count]; var hMap = new int[Hes.Count];
        var eMap = new int[Edges.Count]; var fMap = new int[Faces.Count];
        int nv = 0, nh = 0, ne = 0, nf = 0;
        for (int i = 0; i < Verts.Count; i++) vMap[i] = Verts[i].Alive ? nv++ : -1;
        for (int i = 0; i < Hes.Count; i++) hMap[i] = Hes[i].Alive ? nh++ : -1;
        for (int i = 0; i < Edges.Count; i++) eMap[i] = Edges[i].Alive ? ne++ : -1;
        for (int i = 0; i < Faces.Count; i++) fMap[i] = Faces[i].Alive ? nf++ : -1;

        // ② 살아 있는 요소만 옮기며 모든 ID 참조를 새 번호로 바꾼다
        var newV = new List<Vertex>(nv); var newH = new List<HalfEdge>(nh);
        var newE = new List<Edge>(ne); var newF = new List<Face>(nf);
        for (int i = 0; i < Verts.Count; i++)
        {
            if (vMap[i] < 0) continue;
            var v = Verts[i]; v.HalfEdge = v.HalfEdge >= 0 ? hMap[v.HalfEdge] : -1; newV.Add(v);
        }
        for (int i = 0; i < Hes.Count; i++)
        {
            if (hMap[i] < 0) continue;
            var h = Hes[i];
            h.Vertex = vMap[h.Vertex]; h.Next = hMap[h.Next]; h.Prev = hMap[h.Prev];
            h.Twin = h.Twin >= 0 ? hMap[h.Twin] : -1; h.Face = fMap[h.Face]; h.Edge = eMap[h.Edge];
            newH.Add(h);
        }
        for (int i = 0; i < Edges.Count; i++)
        {
            if (eMap[i] < 0) continue;
            var e = Edges[i]; e.He0 = hMap[e.He0]; e.He1 = e.He1 >= 0 ? hMap[e.He1] : -1; newE.Add(e);
        }
        for (int i = 0; i < Faces.Count; i++)
        {
            if (fMap[i] < 0) continue;
            var f = Faces[i]; f.HalfEdge = hMap[f.HalfEdge]; newF.Add(f);
        }
        // 새 리스트로 교체
        Verts.Clear(); Verts.AddRange(newV);
        Hes.Clear(); Hes.AddRange(newH);
        Edges.Clear(); Edges.AddRange(newE);
        Faces.Clear(); Faces.AddRange(newF);
        // ③ 정점 키 잠긴 노멀과 하프에지 인덱스 UV 세트 배열을 새 ID로 다시 채운다
        var locked = LockedNormals.Where(kv => kv.Key < vMap.Length && vMap[kv.Key] >= 0).Select(kv => (vMap[kv.Key], kv.Value)).ToList();
        LockedNormals.Clear(); foreach (var (v, n) in locked) LockedNormals[v] = n;
        foreach (var set in UvSets)
        {
            var packed = new Vector2[nh];
            for (int i = 0; i < hMap.Length && i < set.Uvs.Length; i++) if (hMap[i] >= 0) packed[hMap[i]] = set.Uvs[i];
            set.Uvs = packed;
            if (set.Pins != null) { var pp = new bool[nh]; for (int i = 0; i < hMap.Length && i < set.Pins.Length; i++) if (hMap[i] >= 0) pp[hMap[i]] = set.Pins[i]; set.Pins = pp; }
            if (set.Seams != null) { var sp = new bool[ne]; for (int i = 0; i < eMap.Length && i < set.Seams.Length; i++) if (eMap[i] >= 0) sp[eMap[i]] = set.Seams[i]; set.Seams = sp; }
        }
        TopologyVersion++; GeometryVersion++;
        _edgeMap = null; _vertexOutgoing = null;
        return new CompactRemap(vMap, hMap, eMap, fMap);
    }
}

/// <summary><see cref="PolyMesh.Compact"/>의 리맵 결과. 각 배열은 old ID → new ID(삭제된 것은 -1).</summary>
public sealed record CompactRemap(int[] Vertices, int[] HalfEdges, int[] Edges, int[] Faces);
