using System.Numerics;
using System.Runtime.CompilerServices;

namespace Cube.Core.Mesh;

/// <summary>정점. <see cref="HalfEdge"/>는 이 정점에서 출발하는 하프에지 하나(없으면 -1).</summary>
public struct Vertex
{
    public Vector3 Position;
    public int HalfEdge;
    public bool Alive;
}

/// <summary>
/// 하프에지. 면의 한 코너(face-vertex)에 해당하며 코너 속성(노멀, UV)을 가진다.
/// <see cref="Twin"/>이 -1이면 경계(인접 면 없음).
/// </summary>
public struct HalfEdge
{
    public int Vertex;   // 출발 정점
    public int Next;
    public int Prev;
    public int Twin;     // -1 = 경계
    public int Face;
    public int Edge;
    public Vector3 Normal;
    public Vector2 Uv0;
    /// <summary>UV 편집기 Pin: 이 코너의 UV 점은 Unfold/Optimize/브러시 등에서 움직이지 않는다.</summary>
    public bool PinUv;
    /// <summary>코너 노멀 고정(사용자 지정 분할 노멀, Bevel Harden Normals/Face Strength): MeshNormals.Recompute가 Normal을 덮어쓰지 않는다.</summary>
    public bool NormalLocked;
    public bool Alive;
}

/// <summary>UV 세트(Maya UV Sets): 하프에지 슬롯별 UV 배열. 현재 세트는 HalfEdge.Uv0에 올라와 있고 전환 시 저장/복원된다.</summary>
public sealed class UvSet
{
    public string Name = "map1";
    public Vector2[] Uvs = Array.Empty<Vector2>();
    public UvSet Clone() => new() { Name = Name, Uvs = (Vector2[])Uvs.Clone() };
}

/// <summary>무방향 엣지. 최대 2개의 하프에지(He1은 경계면 -1)를 가진다. 비매니폴드(3면 이상)는 허용하지 않는다.</summary>
public struct Edge
{
    public int He0;
    public int He1;      // -1 = 경계
    public bool Hard;
    /// <summary>UV 심(Cut UV Edges). 양쪽 코너 UV가 같아도 별개의 UV 점으로 취급한다.</summary>
    public bool Seam;
    /// <summary>서브디비전 크리즈 단계(Maya Crease, 0 = 없음). 1 이상이면 그 단계만큼 Catmull-Clark에서 날카롭게 유지된다.</summary>
    public float Crease;
    public bool Alive;
}

public struct Face
{
    public int HalfEdge; // 루프의 아무 하프에지
    public int Material;
    public bool Alive;
    public Vector3 Normal; // MeshNormals.Recompute가 채우는 캐시
}

/// <summary>
/// Maya 식 폴리곤 메시(n-gon, 코너별 UV/노멀, 하드/소프트 엣지)를 하프에지 구조로 보관한다.
/// 모든 ID는 슬롯 인덱스이며 삭제 시 <c>Alive=false</c>만 표시하고 재사용하지 않는다(세션 중 ID 불변).
/// <see cref="Compact"/>는 저장/내보내기 직전에만 호출한다.
/// </summary>
public sealed class PolyMesh
{
    public readonly List<Vertex> Verts = new();
    public readonly List<HalfEdge> Hes = new();
    public readonly List<Edge> Edges = new();
    public readonly List<Face> Faces = new();

    /// <summary>위상(정점/엣지/면 추가·삭제·연결)이 바뀔 때마다 증가.</summary>
    public int TopologyVersion { get; private set; }
    /// <summary>정점 위치만 바뀔 때 증가(드래그 프리뷰 등).</summary>
    public int GeometryVersion { get; private set; }

    /// <summary>잠긴 정점 노멀(Mesh Display → Lock Normals / Set Vertex Normal / Set to Face / Average). 재계산 시 이 정점의 코너 노멀은 저장된 값으로 고정된다.</summary>
    public readonly Dictionary<int, Vector3> LockedNormals = new();

    /// <summary>UV 세트 목록. 비어 있으면 단일 기본 세트("map1")만 있는 것으로 본다. <see cref="CurrentUvSet"/>의 UV가 HalfEdge.Uv0에 올라와 있다.</summary>
    public readonly List<UvSet> UvSets = new();
    public int CurrentUvSet;

    /// <summary>세트 목록이 비어 있으면 현재 UV로 "map1"을 만든다.</summary>
    public void EnsureUvSets()
    {
        if (UvSets.Count > 0) return;
        UvSets.Add(new UvSet { Name = "map1", Uvs = SnapshotUvs() });
        CurrentUvSet = 0;
    }

    public Vector2[] SnapshotUvs() { var a = new Vector2[Hes.Count]; for (int i = 0; i < a.Length; i++) a[i] = Hes[i].Uv0; return a; }

    /// <summary>현재 코너 UV를 현재 세트 배열에 저장한다.</summary>
    public void StoreCurrentUvs()
    {
        EnsureUvSets();
        UvSets[Math.Clamp(CurrentUvSet, 0, UvSets.Count - 1)].Uvs = SnapshotUvs();
    }

    /// <summary>세트를 전환한다: 현재 UV를 저장하고 대상 세트를 코너에 올린다(배열이 짧으면 0).</summary>
    public void SwitchUvSet(int index)
    {
        EnsureUvSets();
        if (index < 0 || index >= UvSets.Count) return;
        StoreCurrentUvs();
        var src = UvSets[index].Uvs;
        for (int h = 0; h < Hes.Count; h++) { var he = Hes[h]; he.Uv0 = h < src.Length ? src[h] : Vector2.Zero; Hes[h] = he; }
        CurrentUvSet = index;
        GeometryVersion++;
    }

    /// <summary>새 UV 세트를 만든다(copyCurrent면 현재 UV 복사, 아니면 0). 반환값은 인덱스.</summary>
    public int AddUvSet(string name, bool copyCurrent)
    {
        EnsureUvSets();
        StoreCurrentUvs();
        UvSets.Add(new UvSet { Name = name, Uvs = copyCurrent ? SnapshotUvs() : new Vector2[Hes.Count] });
        return UvSets.Count - 1;
    }

    public void RemoveUvSet(int index)
    {
        EnsureUvSets();
        if (UvSets.Count <= 1 || index < 0 || index >= UvSets.Count) return;
        if (index == CurrentUvSet) SwitchUvSet(index == 0 ? 1 : 0);
        UvSets.RemoveAt(index);
        if (CurrentUvSet > index) CurrentUvSet--;
    }

    private Dictionary<long, int>? _edgeMap;      // (min,max) 정점쌍 → edge id
    private int _edgeMapVersion = -1;
    private int[][]? _vertexOutgoing;             // vertex → 출발 하프에지 목록
    private int _vertexOutgoingVersion = -1;

    public int VertexCount => Verts.Count;
    public int HalfEdgeCount => Hes.Count;
    public int EdgeCount => Edges.Count;
    public int FaceCount => Faces.Count;

    public int AliveVertexCount { get { int n = 0; foreach (var v in Verts) if (v.Alive) n++; return n; } }
    public int AliveEdgeCount { get { int n = 0; foreach (var e in Edges) if (e.Alive) n++; return n; } }
    public int AliveFaceCount { get { int n = 0; foreach (var f in Faces) if (f.Alive) n++; return n; } }

    public void BumpTopology() { TopologyVersion++; GeometryVersion++; }
    public void BumpGeometry() { GeometryVersion++; }

    // ---------------------------------------------------------------- 생성

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
    public int AddFace(ReadOnlySpan<int> verts, int material = 0)
    {
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
            var v = Verts[a];
            if (v.HalfEdge == -1 || !Hes[v.HalfEdge].Alive) { v.HalfEdge = he; Verts[a] = v; }
        }
        TopologyVersion++;
        _edgeMapVersion = TopologyVersion; // 직접 갱신했으므로 유효
        return faceId;
    }

    // ---------------------------------------------------------------- 삭제

    /// <summary>면을 삭제한다. 고립되는 엣지/정점은 함께 삭제한다.</summary>
    public void RemoveFace(int faceId, bool removeIsolated = true)
    {
        if ((uint)faceId >= (uint)Faces.Count || !Faces[faceId].Alive) return;
        var f = Faces[faceId];
        int start = f.HalfEdge, he = start;
        var loopList = new List<int>();
        do { loopList.Add(he); he = Hes[he].Next; } while (he != start);

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
        TopologyVersion++;
        _edgeMapVersion = TopologyVersion;

        // 정점의 대표 하프에지 보정 및 고립 정점 삭제
        foreach (int h in loopList)
        {
            int v = Hes[h].Vertex;
            var vert = Verts[v];
            if (vert.HalfEdge == h || !Hes[vert.HalfEdge].Alive)
            {
                vert.HalfEdge = FindOutgoing(v);
                if (vert.HalfEdge == -1 && removeIsolated) vert.Alive = false;
                Verts[v] = vert;
            }
        }
    }

    public void RemoveVertexIfIsolated(int v)
    {
        var vert = Verts[v];
        if (!vert.Alive) return;
        if (FindOutgoing(v) == -1) { vert.Alive = false; vert.HalfEdge = -1; Verts[v] = vert; TopologyVersion++; }
    }

    private int FindOutgoing(int v)
    {
        for (int i = 0; i < Hes.Count; i++) if (Hes[i].Alive && Hes[i].Vertex == v) return i;
        return -1;
    }

    // ---------------------------------------------------------------- 조회

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// 디버그/테스트: 유효하다고 판단된 캐시를 처음부터 다시 만든 결과와 비교해 불일치면 예외를 던진다(위상 버전마다 한 번).
    /// 테스트 프로젝트가 모듈 초기화에서 켠다(CacheVerifyInit) — 직접 구조를 고친 뒤 캐시를 무효화하지 않는 연산을 바로 잡아낸다.
    /// 검증은 O(엣지)라 하프에지 2천 개 미만인 메시에서만 한다(대형 가져오기 성능 테스트가 O(n²)이 되지 않게).
    /// </summary>
    public static bool VerifyCaches;
    private int _edgeMapVerified = -1, _outgoingVerified = -1;

    private void EnsureEdgeMap()
    {
        if (_edgeMap != null && _edgeMapVersion == TopologyVersion)
        {
            if (VerifyCaches && Hes.Count < 2000 && _edgeMapVerified != TopologyVersion) { VerifyEdgeMap(); _edgeMapVerified = TopologyVersion; }
            return;
        }
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

    public (int a, int b) EdgeVertices(int e)
    {
        int h = Edges[e].He0;
        return (Hes[h].Vertex, Hes[Hes[h].Next].Vertex);
    }

    public (int f0, int f1) EdgeFaces(int e)
    {
        var ed = Edges[e];
        return (ed.He0 == -1 ? -1 : Hes[ed.He0].Face, ed.He1 == -1 ? -1 : Hes[ed.He1].Face);
    }

    public bool IsBoundaryEdge(int e) => Edges[e].He1 == -1;

    /// <summary>면의 하프에지를 루프 순서대로 채운다. 반환값은 개수.</summary>
    public int GetFaceHalfEdges(int f, List<int> result)
    {
        result.Clear();
        int start = Faces[f].HalfEdge, he = start;
        do { result.Add(he); he = Hes[he].Next; } while (he != start);
        return result.Count;
    }

    public int GetFaceVertices(int f, List<int> result)
    {
        result.Clear();
        int start = Faces[f].HalfEdge, he = start;
        do { result.Add(Hes[he].Vertex); he = Hes[he].Next; } while (he != start);
        return result.Count;
    }

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

    public void GetVertexFaces(int v, List<int> result)
    {
        result.Clear();
        foreach (int he in VertexOutgoing(v)) result.Add(Hes[he].Face);
    }

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

    public Vector3 FaceCentroid(int f)
    {
        var sum = Vector3.Zero; int n = 0;
        int start = Faces[f].HalfEdge, he = start;
        do { sum += Verts[Hes[he].Vertex].Position; n++; he = Hes[he].Next; } while (he != start);
        return sum / n;
    }

    // ---------------------------------------------------------------- 복제/압축

    public PolyMesh Clone()
    {
        var m = new PolyMesh();
        m.CopyFrom(this);
        return m;
    }

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

    public void Clear()
    {
        Verts.Clear(); Hes.Clear(); Edges.Clear(); Faces.Clear(); LockedNormals.Clear(); UvSets.Clear(); CurrentUvSet = 0;
        TopologyVersion++; GeometryVersion++;
        _edgeMap = null; _vertexOutgoing = null;
    }

    /// <summary>죽은 슬롯을 제거하고 ID를 다시 매긴다. 반환된 리맵은 old→new (삭제된 것은 -1).</summary>
    public CompactRemap Compact()
    {
        var vMap = new int[Verts.Count]; var hMap = new int[Hes.Count];
        var eMap = new int[Edges.Count]; var fMap = new int[Faces.Count];
        int nv = 0, nh = 0, ne = 0, nf = 0;
        for (int i = 0; i < Verts.Count; i++) vMap[i] = Verts[i].Alive ? nv++ : -1;
        for (int i = 0; i < Hes.Count; i++) hMap[i] = Hes[i].Alive ? nh++ : -1;
        for (int i = 0; i < Edges.Count; i++) eMap[i] = Edges[i].Alive ? ne++ : -1;
        for (int i = 0; i < Faces.Count; i++) fMap[i] = Faces[i].Alive ? nf++ : -1;

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
        Verts.Clear(); Verts.AddRange(newV);
        Hes.Clear(); Hes.AddRange(newH);
        Edges.Clear(); Edges.AddRange(newE);
        Faces.Clear(); Faces.AddRange(newF);
        var locked = LockedNormals.Where(kv => kv.Key < vMap.Length && vMap[kv.Key] >= 0).Select(kv => (vMap[kv.Key], kv.Value)).ToList();
        LockedNormals.Clear(); foreach (var (v, n) in locked) LockedNormals[v] = n;
        foreach (var set in UvSets)
        {
            var packed = new Vector2[nh];
            for (int i = 0; i < hMap.Length && i < set.Uvs.Length; i++) if (hMap[i] >= 0) packed[hMap[i]] = set.Uvs[i];
            set.Uvs = packed;
        }
        TopologyVersion++; GeometryVersion++;
        _edgeMap = null; _vertexOutgoing = null;
        return new CompactRemap(vMap, hMap, eMap, fMap);
    }
}

public sealed record CompactRemap(int[] Vertices, int[] HalfEdges, int[] Edges, int[] Faces);
