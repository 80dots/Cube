using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>
/// 위상 편집 연산. 모두 메시를 제자리에서 수정하며 끝에 <see cref="PolyMesh.BumpTopology"/>를 호출한다.
/// 노멀 재계산은 호출자(MeshEditCommand)가 한다.
/// <para>
/// 이 클래스는 partial로 여러 파일에 나뉜다: 이 파일은 공통 헬퍼(코너 캡처/재생성, 하드 엣지 플래그)와
/// Extrude/Delete/Merge/Reverse/Append/연결 요소 같은 기본 연산을 담고, Bevel·Split·Loops·Subdivide 등은
/// MeshOps.*.cs에 있다. 대부분의 연산은 "영향 받는 면의 코너(정점·UV·노멀)와 엣지 하드 플래그를 캡처 →
/// 면 제거 → 정점 매핑을 바꿔 다시 AddFace → 하드 플래그 복원" 패턴으로 위상을 바꾼다.
/// 정점/면 ID는 슬롯 인덱스이고 삭제는 Alive=false라서 연산 도중 기존 ID가 재사용되지 않는다.
/// </para>
/// </summary>
public static partial class MeshOps
{
    /// <summary>
    /// 면 루프의 한 코너 스냅샷. 면을 지웠다가 다시 만들 때 코너 속성을 보존하기 위해 쓴다.
    /// </summary>
    /// <param name="Vertex">코너가 가리키는 정점 ID(하프에지의 시작 정점).</param>
    /// <param name="Uv">코너 UV(Uv0, 하단 원점).</param>
    /// <param name="Normal">코너 노멀(재계산 전 값; 새 면에서는 Zero여도 호출자가 Recompute한다).</param>
    internal readonly record struct Corner(int Vertex, Vector2 Uv, Vector3 Normal);

    /// <summary>
    /// 면 f의 하프에지 루프를 처음부터 한 바퀴 돌며 코너(정점, UV, 노멀)를 순서대로 수집한다.
    /// 순서는 면의 감김 방향(CCW = 앞면)과 같아서 그대로 <see cref="AddFaceWithCorners"/>에 넘기면 같은 면이 재생성된다.
    /// </summary>
    /// <param name="m">대상 메시.</param>
    /// <param name="f">살아 있는 면 ID.</param>
    /// <returns>면 루프 순서의 코너 목록.</returns>
    internal static List<Corner> CaptureCorners(PolyMesh m, int f)
    {
        var list = new List<Corner>();
        int start = m.Faces[f].HalfEdge, he = start;
        do { var h = m.Hes[he]; list.Add(new Corner(h.Vertex, h.Uv0, h.Normal)); he = h.Next; } while (he != start);
        return list;
    }

    /// <summary>
    /// 코너 목록의 정점 순서로 새 면을 추가하고, 생성된 하프에지마다 코너의 UV/노멀을 써 넣는다.
    /// <see cref="PolyMesh.AddFace"/>가 비매니폴드(같은 방향 중복 엣지 등)로 거부하면 -1을 돌려준다.
    /// </summary>
    /// <param name="m">대상 메시.</param>
    /// <param name="corners">면 루프 순서의 코너(CCW).</param>
    /// <param name="material">새 면의 머티리얼 인덱스.</param>
    /// <returns>새 면 ID, 실패 시 -1.</returns>
    internal static int AddFaceWithCorners(PolyMesh m, IReadOnlyList<Corner> corners, int material = 0)
    {
        // 정점 ID 배열만 뽑아 위상을 먼저 만든다
        var ids = new int[corners.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = corners[i].Vertex;
        int f = m.AddFace(ids, material);
        if (f < 0) return -1;
        // AddFace가 만든 하프에지 루프는 corners와 같은 순서로 시작하므로 i2번째 코너 속성을 차례로 복사한다
        int start = m.Faces[f].HalfEdge, he = start, i2 = 0;
        do { var h = m.Hes[he]; h.Uv0 = corners[i2].Uv; h.Normal = corners[i2].Normal; m.Hes[he] = h; he = h.Next; i2++; } while (he != start);
        return f;
    }

    /// <summary>
    /// 정점 a-b 사이 엣지가 있으면 Hard 플래그(하드 엣지 = 노멀 분리)를 설정한다. 엣지가 없으면 아무것도 하지 않는다.
    /// 면을 재생성한 뒤 원래 하드 플래그를 복원할 때 쓴다(엣지 레코드는 새로 만들어지므로 플래그가 사라진다).
    /// </summary>
    internal static void SetHard(PolyMesh m, int a, int b, bool hard)
    {
        int e = m.FindEdge(a, b);
        if (e < 0) return;
        var ed = m.Edges[e]; ed.Hard = hard; m.Edges[e] = ed;
    }

    /// <summary>정점 a-b 사이 엣지가 존재하고 Hard로 표시되어 있으면 true.</summary>
    internal static bool IsHard(PolyMesh m, int a, int b)
    {
        int e = m.FindEdge(a, b);
        return e >= 0 && m.Edges[e].Hard;
    }

    // ------------------------------------------------------------ Extrude

    /// <summary>
    /// 면 집합을 압출(Maya "Keep Faces Together" on). 이동 거리 0으로 위상만 만든다.
    /// 영역 경계 엣지마다 측면 쿼드가 생기고, 경계 정점은 복제된다. 반환값은 새 캡 면 ID들.
    /// </summary>
    public static List<int> ExtrudeFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        // 살아 있는 면만 영역으로 삼는다(잘못된/삭제된 ID는 무시)
        var region = new HashSet<int>(faceIds.Where(f => f >= 0 && f < m.Faces.Count && m.Faces[f].Alive));
        var result = new List<int>();
        if (region.Count == 0) return result;

        // 코너(= 그 정점에서 출발하는 영역 면의 하프에지)를 영역 내부 엣지로 이어지는 묶음(팬)으로 나눈다.
        // 경계 정점은 묶음마다 따로 복제한다: 선택 면들이 한 정점에서만 맞닿는 경우(나비넥타이, 삼각형 팬 중심 등)
        // 하나로 복제하면 옆면들이 같은 방향 엣지를 중복해 추가가 거부되고 구멍/뒤집힌 면이 생긴다.
        // parent: 하프에지(코너) 단위 Union-Find. Find는 경로 압축, Union은 단순 연결.
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int x, int y) { int rx = Find(x), ry = Find(y); if (rx != ry) parent[rx] = ry; }
        // 영역 면마다 하프에지 목록을 캐시하고 각 코너를 자기 자신의 집합으로 초기화
        var faceHes = new Dictionary<int, List<int>>();
        foreach (int f in region)
        {
            var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
            faceHes[f] = hes;
            foreach (int he in hes) parent[he] = he;
        }
        // 영역 내부 엣지(양쪽 면이 모두 영역)를 건너는 코너끼리 같은 묶음으로 합친다
        foreach (int f in region)
            foreach (int he in faceHes[f])
            {
                int tw = m.Hes[he].Twin;
                if (tw < 0 || !region.Contains(m.Hes[tw].Face)) continue;
                // he: vi -> vj, tw: vj -> vi. vi의 코너 = he와 tw.Next, vj의 코너 = he.Next와 tw
                Union(he, m.Hes[tw].Next);
                Union(m.Hes[he].Next, tw);
            }

        // 경계 하프에지(영역 안, 트윈이 영역 밖)와 경계 코너 묶음
        // boundaryHes: 경계 하프에지의 (시작 정점, 끝 정점, 시작 코너 묶음, 끝 코너 묶음, 하드 여부) — 측면 쿼드 생성용
        var boundaryHes = new List<(int a, int b, int groupA, int groupB, bool hard)>();
        var boundaryGroups = new Dictionary<int, int>(); // 묶음 대표 -> 원래 정점
        foreach (int f in region)
            foreach (int he in faceHes[f])
            {
                var h = m.Hes[he];
                bool boundary = h.Twin < 0 || !region.Contains(m.Hes[h.Twin].Face);
                if (!boundary) continue;
                int a = h.Vertex, b = m.Hes[h.Next].Vertex;
                int ga = Find(he), gb = Find(h.Next);
                boundaryHes.Add((a, b, ga, gb, m.Edges[h.Edge].Hard));
                boundaryGroups[ga] = a; boundaryGroups[gb] = b;
            }

        // 원본 면 코너 캡처 + 내부 엣지 하드 플래그
        var faceCorners = new Dictionary<int, (List<Corner> corners, List<int> hes, int material, List<bool> hard)>();
        foreach (int f in region)
        {
            var corners = CaptureCorners(m, f);
            var hard = new List<bool>();
            for (int i = 0; i < corners.Count; i++) hard.Add(IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
            faceCorners[f] = (corners, faceHes[f], m.Faces[f].Material, hard);
        }

        // dup: 코너 묶음 대표 -> 복제된 새 정점(원래 위치에 생성; 실제 이동은 이후 툴/옵션이 한다)
        // 경계 코너 묶음마다 정점 복제(묶음 대표는 면 제거 전에 계산)
        var dup = new Dictionary<int, int>();
        foreach (var (g, v) in boundaryGroups) dup[g] = m.AddVertex(m.Verts[v].Position);
        // cornerGroup: 각 코너(하프에지) -> 묶음 대표. 면을 지우면 하프에지가 사라지므로 미리 계산해 둔다.
        var cornerGroup = new Dictionary<int, int>();
        foreach (var hes in faceHes.Values) foreach (int he in hes) cornerGroup[he] = Find(he);

        // 영역 면 제거(정점은 유지)
        foreach (int f in region) m.RemoveFace(f, removeIsolated: false);

        // 캡 면 재생성(경계 정점 -> 그 코너 묶음의 복제본)
        foreach (var (f, (corners, hes, material, hard)) in faceCorners)
        {
            // 경계 코너는 복제 정점으로, 내부 정점은 그대로 둔 코너 목록
            var mapped = new List<Corner>(corners.Count);
            for (int i = 0; i < corners.Count; i++)
            {
                var c = corners[i];
                mapped.Add(dup.TryGetValue(cornerGroup[hes[i]], out int d) ? c with { Vertex = d } : c);
            }
            int nf = AddFaceWithCorners(m, mapped, material);
            if (nf >= 0)
            {
                result.Add(nf);
                for (int i = 0; i < mapped.Count; i++) SetHard(m, mapped[i].Vertex, mapped[(i + 1) % mapped.Count].Vertex, hard[i]);
            }
        }

        // 측면 쿼드 (a, b, b', a')
        foreach (var (a, b, ga, gb, hard) in boundaryHes)
        // 원래 경계 엣지 a->b와 복제 엣지 a'->b'를 잇는 측면. UV는 단위 사각형으로 임시 배정한다.
        {
            int a2 = dup[ga], b2 = dup[gb];
            var quad = new List<Corner>
            {
                new(a, new Vector2(0, 0), Vector3.Zero), new(b, new Vector2(1, 0), Vector3.Zero),
                new(b2, new Vector2(1, 1), Vector3.Zero), new(a2, new Vector2(0, 1), Vector3.Zero),
            };
            int q = AddFaceWithCorners(m, quad);
            if (q >= 0)
            {
                // 측면 쿼드의 네 엣지는 원래 경계 엣지의 하드 여부를 따른다(하드 경계면 돌출 모서리도 하드)
                SetHard(m, a, b, hard);
                SetHard(m, a2, b2, hard);
                SetHard(m, a, a2, hard);
                SetHard(m, b, b2, hard);
            }
        }
        m.BumpTopology();
        return result;
    }

    // ------------------------------------------------------------ Delete

    /// <summary>
    /// 면 삭제(Maya Delete Face). 지정한 면만 제거하고 더 이상 어떤 면에도 속하지 않는 정점은 함께 지운다
    /// (<see cref="PolyMesh.RemoveFace"/>의 기본 removeIsolated). 순회 중 컬렉션 변경을 피하려고 ToArray로 복사한다.
    /// </summary>
    public static void DeleteFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        foreach (int f in faceIds.ToArray()) m.RemoveFace(f);
        m.BumpTopology();
    }

    /// <summary>엣지 삭제(Maya Delete Edge): 양쪽 면을 하나로 합친다. 경계 엣지는 인접 면을 지운다. 결과로 생긴 2가 정점은 녹인다.</summary>
    public static void DeleteEdges(PolyMesh m, IEnumerable<int> edgeIds)
    {
        // touched: 삭제된 엣지의 양끝 정점 — 마지막에 2가 정점 정리/고립 정점 제거 대상
        var touched = new HashSet<int>();
        foreach (int e in edgeIds.ToArray())
        {
            if (e < 0 || e >= m.Edges.Count || !m.Edges[e].Alive) continue;
            var (a, b) = m.EdgeVertices(e);
            touched.Add(a); touched.Add(b);
            // 경계 엣지(한쪽 면만)는 합칠 상대가 없으니 그 면을 지운다. 내부 엣지는 두 면을 하나로 병합.
            var (f0, f1) = m.EdgeFaces(e);
            if (f1 < 0) { m.RemoveFace(f0, removeIsolated: false); continue; }
            MergeFacesAcrossEdgeReturning(m, e);
        }
        // 엣지를 지우면 직선 위에 엣지 2개만 남은 정점이 생기므로 녹이고, 남은 고립 정점을 지운다
        foreach (int v in touched) DissolveIfValence2(m, v);
        foreach (int v in touched) m.RemoveVertexIfIsolated(v);
        m.BumpTopology();
    }

    /// <summary>엣지가 정확히 2개인 정점을 인접 면 루프에서 제거한다(직선 위 불필요 정점 정리).</summary>
    /// <remarks>
    /// 정점 v를 포함한 각 면을 v를 뺀 코너 목록으로 다시 만든다. v에 닿던 두 엣지 중 하나라도 하드였으면
    /// 두 이웃을 직접 잇는 새 엣지를 하드로 둔다. 3각 미만이 되는 면은 버린다.
    /// </remarks>
    /// <returns>v를 녹였으면 true, 2가 정점이 아니거나 유효하지 않으면 false.</returns>
    public static bool DissolveIfValence2(PolyMesh m, int v)
    {
        if (v < 0 || v >= m.Verts.Count || !m.Verts[v].Alive) return false;
        // 정확히 2개의 엣지에 연결된 정점만 대상
        var edges = new List<int>(); m.GetVertexEdges(v, edges);
        if (edges.Count != 2) return false;
        var faces = new List<int>(); m.GetVertexFaces(v, faces);
        foreach (int f in faces.Distinct().ToArray())
        {
            // v를 뺀 코너 목록과 새 엣지(c[i]-c[i+1])의 하드 여부: 원래 엣지 또는 v를 거치던 두 엣지 중 하나가 하드면 하드
            var corners = CaptureCorners(m, f).Where(c => c.Vertex != v).ToList();
            var hard = new List<bool>();
            for (int i = 0; i < corners.Count; i++) hard.Add(IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex) || IsHard(m, corners[i].Vertex, v) || IsHard(m, v, corners[(i + 1) % corners.Count].Vertex));
            int material = m.Faces[f].Material;
            m.RemoveFace(f, removeIsolated: false);
            // 삼각형에서 정점을 빼면 면이 사라진다(선분) — 다시 만들지 않는다
            if (corners.Count >= 3)
            {
                int nf = AddFaceWithCorners(m, corners, material);
                if (nf >= 0) for (int i = 0; i < corners.Count; i++) SetHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex, hard[i]);
            }
        }
        m.RemoveVertexIfIsolated(v);
        return true;
    }

    /// <summary>정점 삭제(Maya Delete Vertex): 내부 정점은 주변 면을 하나로 합치고, 경계 정점은 주변 면을 지운다.</summary>
    /// <remarks>
    /// 2가 정점은 <see cref="DissolveIfValence2"/>로 녹인다. 내부 정점은 주변 면 부채꼴을 돌며 v를 제외한
    /// 바깥 루프를 모아 하나의 n각형으로 다시 만든다. 경계 정점(열린 부채꼴)은 루프가 닫히지 않으므로 주변 면을 지운다.
    /// </remarks>
    public static void DeleteVertices(PolyMesh m, IEnumerable<int> vertIds)
    {
        foreach (int v in vertIds.ToArray())
        {
            if (v < 0 || v >= m.Verts.Count || !m.Verts[v].Alive) continue;
            if (DissolveIfValence2(m, v)) continue;
            // v에서 나가는 하프에지들. 하나라도 트윈이 없거나(나가는 쪽) 이전 하프에지 트윈이 없으면(들어오는 쪽) 경계 정점
            var outgoing = m.VertexOutgoing(v).ToArray();
            bool boundary = outgoing.Any(he => m.Hes[he].Twin < 0 || m.Hes[m.Hes[he].Prev].Twin < 0);
            if (boundary || outgoing.Length == 0)
            {
                var faces = new List<int>(); m.GetVertexFaces(v, faces);
                foreach (int f in faces.Distinct()) m.RemoveFace(f, removeIsolated: false);
                m.RemoveVertexIfIsolated(v);
                continue;
            }
            // 부채꼴을 돌며 외곽 루프 수집: 각 면에서 v 다음 정점부터 v 이전 정점까지
            // loop: 새 n각형의 코너들, visitedFaces: 부채꼴을 이루는 면들, guard: 비정상 위상에서 무한 루프 방지
            var loop = new List<Corner>();
            int startHe = outgoing[0], cur = startHe;
            var visitedFaces = new List<int>();
            int guard = 0;
            do
            {
                var h = m.Hes[cur];
                visitedFaces.Add(h.Face);
                int walk = h.Next;
                while (m.Hes[walk].Next != cur) { var w = m.Hes[walk]; loop.Add(new Corner(w.Vertex, w.Uv0, w.Normal)); walk = w.Next; }
                // 마지막 정점(v 직전)은 다음 면의 첫 정점과 같으므로 건너뜀
                cur = m.Hes[m.Hes[cur].Prev].Twin; // 다음 면에서 v에서 나가는 하프에지
                if (cur < 0 || guard++ > 10000) break;
            } while (cur != startHe);
            var hard = new List<bool>();
            for (int i = 0; i < loop.Count; i++) hard.Add(IsHard(m, loop[i].Vertex, loop[(i + 1) % loop.Count].Vertex));
            // 새 면의 머티리얼은 부채꼴 첫 면을 따른다
            int material = m.Faces[visitedFaces[0]].Material;
            foreach (int f in visitedFaces.Distinct()) m.RemoveFace(f, removeIsolated: false);
            m.RemoveVertexIfIsolated(v);
            if (loop.Count >= 3)
            {
                int nf = AddFaceWithCorners(m, loop, material);
                if (nf >= 0) for (int i = 0; i < loop.Count; i++) SetHard(m, loop[i].Vertex, loop[(i + 1) % loop.Count].Vertex, hard[i]);
            }
        }
        m.BumpTopology();
    }

    // ------------------------------------------------------------ Merge

    /// <summary>임계 거리 안의 선택 정점들을 하나로 합친다. 영향을 받는 면은 다시 만들고 퇴화 면은 버린다. 반환값은 합쳐진 쌍 수.</summary>
    /// <remarks>
    /// O(n²) 탐욕 클러스터링: 아직 대표가 없는 정점 i를 대표로 삼고 거리 ≤ threshold인 뒤쪽 정점 j를 i에 묶는다
    /// (j끼리의 거리는 보지 않으므로 체인처럼 이어진 정점은 대표 기준으로만 판정). 대표 위치는 클러스터 평균으로 옮기고
    /// <see cref="RebuildFacesWithVertexMap"/>이 면을 다시 만들어 연속 중복 정점·2각 이하 면을 정리한다.
    /// </remarks>
    public static int MergeVertices(PolyMesh m, IEnumerable<int> vertIds, float threshold)
    {
        var verts = vertIds.Where(v => v >= 0 && v < m.Verts.Count && m.Verts[v].Alive).Distinct().ToList();
        // rep: 합쳐질 정점 -> 대표 정점. t2: 제곱 거리 비교로 sqrt를 피한다.
        var rep = new Dictionary<int, int>();
        float t2 = threshold * threshold;
        int merged = 0;
        // 대표가 정해지지 않은 정점 i를 기준으로 가까운 뒤쪽 정점들을 묶는다
        for (int i = 0; i < verts.Count; i++)
        {
            if (rep.ContainsKey(verts[i])) continue;
            for (int j = i + 1; j < verts.Count; j++)
            {
                if (rep.ContainsKey(verts[j])) continue;
                if (Vector3.DistanceSquared(m.Verts[verts[i]].Position, m.Verts[verts[j]].Position) <= t2) { rep[verts[j]] = verts[i]; merged++; }
            }
        }
        if (merged == 0) return 0;
        // 대표 정점 위치 = 클러스터 평균
        // 그룹 = 같은 대표에 묶인 정점들
        foreach (var group in rep.GroupBy(kv => kv.Value))
        {
            var sum = m.Verts[group.Key].Position; int n = 1;
            foreach (var kv in group) { sum += m.Verts[kv.Key].Position; n++; }
            var vv = m.Verts[group.Key]; vv.Position = sum / n; m.Verts[group.Key] = vv;
        }
        RebuildFacesWithVertexMap(m, rep);
        m.BumpTopology();
        return merged;
    }

    /// <summary>
    /// 정점 치환 맵(map: 옛 정점 → 새 정점)을 적용해 영향 받는 면을 모두 재생성한다.
    /// 치환 후 연속으로 같은 정점이 오면 하나로 합치고(사이 엣지의 하드는 OR), 루프 처음과 끝이 같으면 끝을 버린다.
    /// 3각 미만이 된 퇴화 면은 버리고, 치환된/대표 정점 중 고립된 것은 지운다.
    /// </summary>
    private static void RebuildFacesWithVertexMap(PolyMesh m, Dictionary<int, int> map)
    {
        // map의 키 정점(사라질 정점)에 닿은 면만 영향 받는다
        var affected = new HashSet<int>();
        var tmp = new List<int>();
        foreach (int v in map.Keys) { m.GetVertexFaces(v, tmp); affected.UnionWith(tmp); }
        var rebuilt = new List<(List<Corner> corners, int material, List<bool> hard)>();
        foreach (int f in affected)
        {
            var corners = CaptureCorners(m, f);
            var hard = new List<bool>();
            for (int i = 0; i < corners.Count; i++) hard.Add(IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
            rebuilt.Add((corners, m.Faces[f].Material, hard));
        }
        foreach (int f in affected) m.RemoveFace(f, removeIsolated: false);
        // 면을 모두 지운 뒤에 다시 만들어야 일시적인 같은 방향 엣지 충돌이 없다
        foreach (var (corners, material, hard) in rebuilt)
        {
            var mapped = new List<Corner>(); var mappedHard = new List<bool>();
            for (int i = 0; i < corners.Count; i++)
            {
                var c = corners[i] with { Vertex = map.TryGetValue(corners[i].Vertex, out int r) ? r : corners[i].Vertex };
                if (mapped.Count > 0 && mapped[^1].Vertex == c.Vertex) { mappedHard[^1] |= hard[i]; continue; }
                mapped.Add(c); mappedHard.Add(hard[i]);
            }
            while (mapped.Count > 1 && mapped[0].Vertex == mapped[^1].Vertex) { mapped.RemoveAt(mapped.Count - 1); mappedHard.RemoveAt(mappedHard.Count - 1); }
            if (mapped.Count < 3) continue;
            int nf = AddFaceWithCorners(m, mapped, material);
            if (nf >= 0) for (int i = 0; i < mapped.Count; i++) SetHard(m, mapped[i].Vertex, mapped[(i + 1) % mapped.Count].Vertex, mappedHard[i]);
        }
        foreach (int v in map.Keys) m.RemoveVertexIfIsolated(v);
        foreach (int v in map.Values.Distinct()) m.RemoveVertexIfIsolated(v);
    }

    // ------------------------------------------------------------ 기타

    /// <summary>
    /// 면의 방향(노멀)을 뒤집는다. 하프에지 구조의 일관성을 위해 선택 면이 속한 연결 요소 전체를 뒤집는다
    /// (Maya의 Reverse + Conform에 해당; 안팎이 뒤집힌 메시를 고치는 용도).
    /// </summary>
    public static void ReverseFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        // faces: 선택 면이 하나라도 들어 있는 연결 요소의 모든 면
        var selected = new HashSet<int>(faceIds);
        var faces = new HashSet<int>();
        foreach (var comp in ConnectedComponents(m)) if (comp.Any(selected.Contains)) faces.UnionWith(comp);
        // 1) 모든 면을 캡처하고 제거한 뒤 2) 뒤집어 재생성 (동시에 해야 같은 방향 충돌이 없다)
        var captured = new List<(List<Corner> corners, int material, List<bool> hard)>();
        foreach (int f in faces)
        {
            if (f < 0 || f >= m.Faces.Count || !m.Faces[f].Alive) continue;
            var corners = CaptureCorners(m, f);
            var hard = new List<bool>();
            for (int i = 0; i < corners.Count; i++) hard.Add(IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
            captured.Add((corners, m.Faces[f].Material, hard));
        }
        foreach (int f in faces) if (f >= 0 && f < m.Faces.Count && m.Faces[f].Alive) m.RemoveFace(f, removeIsolated: false);
        foreach (var (corners, material, hard) in captured)
        {
            // 코너 순서를 뒤집으면 면 법선 방향이 반대가 된다
            var rev = corners.AsEnumerable().Reverse().ToList();
            int nf = AddFaceWithCorners(m, rev, material);
            if (nf < 0) continue;
            int n = corners.Count;
            // 원래 엣지 i는 (c[i], c[i+1]); 뒤집힌 루프에서 (rev[j], rev[j+1]) = (c[n-1-j], c[n-2-j]) → 원래 엣지 n-2-j
            for (int j = 0; j < n; j++) SetHard(m, rev[j].Vertex, rev[(j + 1) % n].Vertex, hard[((n - 2 - j) % n + n) % n]);
        }
        m.BumpTopology();
    }

    /// <summary>
    /// 지정한 엣지들의 Hard 플래그를 일괄 설정한다(Maya Harden/Soften Edge). 위상은 바꾸지 않으므로
    /// BumpTopology를 부르지 않는다. 노멀 재계산은 호출자 몫이다.
    /// </summary>
    public static void SetEdgesHard(PolyMesh m, IEnumerable<int> edgeIds, bool hard)
    {
        foreach (int e in edgeIds)
        {
            if (e < 0 || e >= m.Edges.Count || !m.Edges[e].Alive) continue;
            var ed = m.Edges[e]; ed.Hard = hard; m.Edges[e] = ed;
        }
    }

    /// <summary>다른 메시를 변환 행렬을 적용해 이 메시에 덧붙인다. 반환값은 정점 ID 오프셋 매핑.</summary>
    /// <remarks>
    /// 정점은 transform으로 옮기고 코너 노멀은 TransformNormal 후 정규화한다. 행렬의 3×3 행렬식이 음수(거울 반사)면
    /// 면의 감김이 뒤집히므로 코너 순서를 뒤집어 앞면 방향(CCW)을 유지한다. 하드 플래그는 원본 엣지에서 복사한다.
    /// 실제 반환값은 없다(source 정점 → target 정점 매핑은 내부 vmap으로만 쓰인다).
    /// </remarks>
    public static void Append(PolyMesh target, PolyMesh source, Matrix4x4 transform)
    {
        // vmap: source 정점 ID -> target 새 정점 ID(죽은 정점은 -1)
        var vmap = new int[source.Verts.Count];
        for (int v = 0; v < source.Verts.Count; v++)
            vmap[v] = source.Verts[v].Alive ? target.AddVertex(Vector3.Transform(source.Verts[v].Position, transform)) : -1;
        bool flip = Matrix4x4.Invert(transform, out _) && Det3(transform) < 0;
        for (int f = 0; f < source.Faces.Count; f++)
        {
            if (!source.Faces[f].Alive) continue;
            var corners = CaptureCorners(source, f).Select(c => c with { Vertex = vmap[c.Vertex], Normal = Vector3.Normalize(Vector3.TransformNormal(c.Normal, transform)) }).ToList();
            if (flip) corners.Reverse();
            // 하드 플래그 조회용 원본 코너(뒤집기 전 순서) — 엣지는 무향이라 순서와 무관하게 짝을 찾는다
            var srcCorners = CaptureCorners(source, f);
            int nf = AddFaceWithCorners(target, corners, source.Faces[f].Material);
            if (nf < 0) continue;
            for (int i = 0; i < srcCorners.Count; i++)
                SetHard(target, vmap[srcCorners[i].Vertex], vmap[srcCorners[(i + 1) % srcCorners.Count].Vertex], IsHard(source, srcCorners[i].Vertex, srcCorners[(i + 1) % srcCorners.Count].Vertex));
        }
        target.BumpTopology();
    }

    /// <summary>행렬 좌상단 3×3(회전/스케일 부분)의 행렬식. 음수면 반사 변환이다.</summary>
    private static float Det3(Matrix4x4 m)
        => m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    /// <summary>연결 요소(면 인접 기준)별 면 ID 목록.</summary>
    /// <remarks>
    /// 정점을 공유하면 같은 요소로 본다(엣지가 아니라 정점 공유 기준이므로 한 정점에서만 맞닿은 면도 같은 요소).
    /// 스택 기반 DFS로 comp[] 배열에 요소 번호를 매긴다. Combine/Separate, ReverseFaces가 사용한다.
    /// </remarks>
    public static List<List<int>> ConnectedComponents(PolyMesh m)
    {
        // comp[f] = 면 f가 속한 요소 번호(-1 = 미방문)
        var comp = new int[m.Faces.Count];
        Array.Fill(comp, -1);
        var result = new List<List<int>>();
        var stack = new Stack<int>();
        var tmp = new List<int>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive || comp[f] >= 0) continue;
            int id = result.Count; var list = new List<int>();
            stack.Push(f); comp[f] = id;
            while (stack.Count > 0)
            {
                int cur = stack.Pop(); list.Add(cur);
                // 현재 면의 모든 정점에서 나가는 하프에지의 면을 이웃으로 방문
                m.GetFaceVertices(cur, tmp);
                foreach (int v in tmp)
                    foreach (int he in m.VertexOutgoing(v)) { int nf = m.Hes[he].Face; if (comp[nf] < 0) { comp[nf] = id; stack.Push(nf); } }
            }
            result.Add(list);
        }
        return result;
    }

    /// <summary>면 ID 집합만으로 새 메시를 만든다(정점은 필요한 것만 복사).</summary>
    /// <remarks>Separate/Extract에서 쓰인다. 코너 UV/노멀, 머티리얼, 하드 플래그를 보존하며 원본 메시는 변경하지 않는다.</remarks>
    public static PolyMesh ExtractFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        var target = new PolyMesh();
        // vmap: 원본 정점 ID -> 새 메시 정점 ID(처음 등장할 때 생성)
        var vmap = new Dictionary<int, int>();
        foreach (int f in faceIds)
        {
            var corners = CaptureCorners(m, f);
            var mapped = corners.Select(c =>
            {
                if (!vmap.TryGetValue(c.Vertex, out int nv)) { nv = target.AddVertex(m.Verts[c.Vertex].Position); vmap[c.Vertex] = nv; }
                return c with { Vertex = nv };
            }).ToList();
            int nf = AddFaceWithCorners(target, mapped, m.Faces[f].Material);
            if (nf < 0) continue;
            for (int i = 0; i < corners.Count; i++)
                SetHard(target, mapped[i].Vertex, mapped[(i + 1) % mapped.Count].Vertex, IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
        }
        target.BumpTopology();
        return target;
    }
}
