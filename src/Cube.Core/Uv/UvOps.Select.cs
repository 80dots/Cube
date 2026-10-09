using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>UV Editor Select 메뉴/Cut-Sew 메뉴 도우미: Overlapping, Back/Front-facing, Texture Borders, Unmapped, Shortest Edge Path, Grow/Shrink Along Loop, Contained/Connected Faces, Split/Merge UVs, Create UV Shell, Delete UVs, 통계.</summary>
/// <remarks>UvOps partial의 일부. 선택 계열은 ID 목록을 돌려주기만 하고, Split/Merge/Create Shell/Delete는 UV·심을 바꾼다.</remarks>
public static partial class UvOps
{
    /// <summary>UV 면적이 음수인 면(뒤집힘 = Back-facing).</summary>
    /// <remarks>
    /// UV 면적 부호로 판정한다: 코어는 CCW가 앞면이므로 UV에서도 반시계면 양수(앞면), 시계면 음수(UV가 뒤집혀 텍스처가 거울상).
    /// </remarks>
    /// <param name="back">true면 뒤집힌 면, false면 정상(Front-facing) 면을 돌려준다.</param>
    public static List<int> BackFacingFaces(PolyMesh m, bool back = true)
    {
        var list = new List<int>();
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive && (FaceUvSignedArea(m, f) < 0) == back) list.Add(f);
        return list;
    }

    /// <summary>다른 면과 UV 공간에서 겹치는 면(같은 정점을 공유하는 이웃은 제외). O(n²)에 경계 상자 가지치기.</summary>
    /// <remarks>
    /// 1) 면마다 UV 다각형과 경계 상자를 만들고 평균 크기로 균일 격자 셀 크기를 정한다.
    /// 2) 각 면을 경계 상자가 덮는 모든 셀 버킷에 넣는다.
    /// 3) 버킷 안 면 쌍만 경계 상자 → 이미 검사한 쌍 → <see cref="PolygonsOverlap"/> 순으로 검사한다.
    /// 공유 변·점은 엄격 판정이 무시하므로 인접 면은 자연스럽게 겹침에서 빠진다.
    /// </remarks>
    public static List<int> OverlappingFaces(PolyMesh m) => OverlappingFaces(m, null);

    /// <summary>면 부분 집합(null = 전체) 안에서만 서로 겹치는 면을 찾는다(Auto Wrap의 셸 자기 겹침 검사).</summary>
    public static List<int> OverlappingFaces(PolyMesh m, HashSet<int>? subset)
    {
        // (면 ID, UV 다각형, 경계 min/max)
        var faces = new List<(int f, Vector2[] poly, Vector2 min, Vector2 max)>();
        var hes = new List<int>();
        // 격자 셀 크기 추정용 경계 상자 크기 합
        double sumW = 0, sumH = 0;
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive || (subset != null && !subset.Contains(f))) continue;
            m.GetFaceHalfEdges(f, hes);
            var poly = new Vector2[hes.Count];
            for (int i = 0; i < hes.Count; i++) poly[i] = m.Hes[hes[i]].Uv0;
            var (mn, mx) = Bounds(poly);
            faces.Add((f, poly, mn, mx));
            sumW += mx.X - mn.X; sumH += mx.Y - mn.Y;
        }
        var result = new HashSet<int>();
        if (faces.Count < 2) return result.ToList();
        // 균일 격자: 셀 크기 = 평균 바운딩 박스 크기의 2배(최소 1e-4). 같은 셀에 든 면끼리만 검사하고, 쌍은 먼저 만나는 셀에서 한 번만 검사한다.
        float cell = MathF.Max(1e-4f, (float)(Math.Max(sumW, sumH) / faces.Count) * 2f);
        var grid = new Dictionary<long, List<int>>(Cube.Core.Mesh.PairKeyComparer.Instance);
        // 셀 (x, y) → 64비트 키
        static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
        for (int i = 0; i < faces.Count; i++)
        {
            var (_, _, mn, mx) = faces[i];
            int x0 = (int)MathF.Floor(mn.X / cell), x1 = (int)MathF.Floor(mx.X / cell), y0 = (int)MathF.Floor(mn.Y / cell), y1 = (int)MathF.Floor(mx.Y / cell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    long k = Key(x, y);
                    if (!grid.TryGetValue(k, out var bucket)) { bucket = new List<int>(); grid[k] = bucket; }
                    bucket.Add(i);
                }
        }
        // 이미 검사한 면 쌍(작은 인덱스, 큰 인덱스) — 여러 셀에 걸친 쌍의 중복 검사 방지
        var tested = new HashSet<long>(Cube.Core.Mesh.PairKeyComparer.Instance);
        foreach (var bucket in grid.Values)
        {
            if (bucket.Count < 2) continue;
            for (int bi = 0; bi < bucket.Count; bi++)
                for (int bj = bi + 1; bj < bucket.Count; bj++)
                {
                    int i = bucket[bi], j = bucket[bj];
                    var a = faces[i]; var b = faces[j];
                    if (a.max.X < b.min.X || b.max.X < a.min.X || a.max.Y < b.min.Y || b.max.Y < a.min.Y) continue;
                    if (!tested.Add(Key(Math.Min(i, j), Math.Max(i, j)))) continue;
                    if (PolygonsOverlap(a.poly, b.poly)) { result.Add(a.f); result.Add(b.f); }
                }
        }
        return result.ToList();
    }

    /// <summary>두 폴리곤이 면적을 공유하는지(변 교차 또는 한 쪽 점이 다른 쪽 안). 공유 변/점은 겹침으로 보지 않는다.</summary>
    /// <remarks>
    /// 변끼리 엄격히 교차(양쪽 끝이 반대편에 확실히 있음)하면 겹침. 교차가 없으면 한 다각형이 다른 다각형을 포함하는 경우를
    /// 무게중심(정점 평균)의 엄격한 내부 판정으로 잡는다(볼록 가정 근사).
    /// </remarks>
    public static bool PolygonsOverlap(Vector2[] a, Vector2[] b)
    {
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                if (SegmentsCrossStrict(a[i], a[(i + 1) % a.Length], b[j], b[(j + 1) % b.Length])) return true;
        var ca = Centroid(a); var cb = Centroid(b);
        return PointInPolygonStrict(ca, b) || PointInPolygonStrict(cb, a);
    }

    /// <summary>다각형 정점 평균(넓이 중심이 아니라 단순 평균).</summary>
    private static Vector2 Centroid(Vector2[] p) { var s = Vector2.Zero; foreach (var q in p) s += q; return s / p.Length; }

    /// <summary>
    /// 두 선분 p→p2, q→q2가 내부에서 엄격히 교차하는지. 네 외적 부호가 모두 eps 이상으로 엇갈려야 true이므로
    /// 끝점이 닿거나 겹쳐 놓인(공선) 경우는 false.
    /// </summary>
    private static bool SegmentsCrossStrict(Vector2 p, Vector2 p2, Vector2 q, Vector2 q2)
    {
        float d1 = Cross(p2 - p, q - p), d2 = Cross(p2 - p, q2 - p), d3 = Cross(q2 - q, p - q), d4 = Cross(q2 - q, p2 - q);
        const float eps = 1e-9f;
        return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
    }

    /// <summary>
    /// 짝수-홀수 규칙(레이 캐스팅)으로 점이 다각형 내부인지 판정한다. 변 위에 놓인 점은 false(엄격).
    /// </summary>
    private static bool PointInPolygonStrict(Vector2 pt, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if (MathF.Abs(Cross(b - a, pt - a)) < 1e-9f && Vector2.Dot(pt - a, pt - b) <= 0) return false; // 변 위
            // 오른쪽으로 뻗은 수평 반직선이 변을 가로지를 때마다 내부/외부를 뒤집는다
            if ((a.Y > pt.Y) != (b.Y > pt.Y) && pt.X < (b.X - a.X) * (pt.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Texture Borders: 셸 경계(심/메시 경계) 위의 UV 점.</summary>
    /// <remarks>
    /// 하프에지마다 메시 경계, 심, 또는 엣지 건너편 코너가 다른 UV 점(UV가 갈라짐)이면 그 엣지의 두 끝 UV 점을 경계로 본다.
    /// UV 편집기 Texture Borders 표시와 Select → Texture Borders에 쓴다.
    /// </remarks>
    public static List<int> TextureBorderPoints(PolyMesh m, UvTopology topo)
    {
        var set = new HashSet<int>();
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h]; if (!he.Alive) continue;
            // twin 쪽 코너와 UV 점이 다르면(엣지를 건너 UV가 이어지지 않음) 경계
            bool border = he.Twin < 0 || m.Edges[he.Edge].Seam || topo.HeToPoint[he.Twin] != topo.HeToPoint[he.Next] || topo.HeToPoint[m.Hes[he.Twin].Next] != topo.HeToPoint[h];
            if (border) { set.Add(topo.HeToPoint[h]); set.Add(topo.HeToPoint[he.Next]); }
        }
        return set.ToList();
    }

    /// <summary>Unmapped faces: 모든 코너 UV가 (0,0)인 면.</summary>
    /// <remarks>Delete UVs 결과와 UV가 한 번도 만들어지지 않은 면을 찾는다(길이² &lt; 1e-16).</remarks>
    public static List<int> UnmappedFaces(PolyMesh m)
    {
        var list = new List<int>();
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive && FaceHalfEdges(m, f).All(h => m.Hes[h].Uv0.LengthSquared() < 1e-16f)) list.Add(f);
        return list;
    }

    /// <summary>Shortest Edge Path: 두 정점 사이 3D 최단 엣지 경로(Dijkstra). 반환값은 엣지 ID들(없으면 빈 목록).</summary>
    /// <remarks>
    /// 엣지 가중치 = 3D 길이. va에서 Dijkstra를 돌려 vb를 확정하면 멈추고 prevEdge로 거꾸로 따라가 va → vb 순서로 돌려준다.
    /// UV 편집기의 Select Shortest Edge Path 툴이 쓴다.
    /// </remarks>
    public static List<int> ShortestEdgePath(PolyMesh m, int va, int vb)
    {
        var result = new List<int>();
        if (va < 0 || vb < 0 || va >= m.VertexCount || vb >= m.VertexCount || va == vb) return result;
        var dist = new Dictionary<int, float> { [va] = 0f };
        var prevEdge = new Dictionary<int, int>();
        var queue = new PriorityQueue<int, float>(); queue.Enqueue(va, 0f);
        var done = new HashSet<int>();
        var edges = new List<int>();
        // done: 최단 거리가 확정된 정점(중복 큐 항목 건너뜀)
        while (queue.Count > 0)
        {
            int v = queue.Dequeue();
            if (!done.Add(v)) continue;
            if (v == vb) break;
            m.GetVertexEdges(v, edges);
            foreach (int e in edges)
            {
                var (x, y) = m.EdgeVertices(e); int w = x == v ? y : x;
                float nd = dist[v] + Vector3.Distance(m.Verts[x].Position, m.Verts[y].Position);
                if (!dist.TryGetValue(w, out float old) || nd < old) { dist[w] = nd; prevEdge[w] = e; queue.Enqueue(w, nd); }
            }
        }
        // 경로 복원(vb → va) 후 뒤집기
        if (!prevEdge.ContainsKey(vb)) return result;
        int cur = vb;
        while (cur != va) { int e = prevEdge[cur]; result.Add(e); var (x, y) = m.EdgeVertices(e); cur = x == cur ? y : x; }
        result.Reverse();
        return result;
    }

    /// <summary>Grow Along Loop: 선택 엣지 집합의 양 끝에서 루프 방향으로 한 엣지씩 늘린다.</summary>
    /// <remarks>
    /// 각 선택 엣지의 양끝 정점에서 <c>MeshOps.OppositeEdgeAtVertex</c>(4가 정점에서 맞은편 엣지 = 루프 방향 다음 엣지)를 더한다.
    /// 결과는 인자로 받은 집합을 직접 바꾼다.
    /// </remarks>
    public static void GrowAlongLoop(PolyMesh m, HashSet<int> edges)
    {
        var add = new List<int>();
        foreach (int e in edges)
        {
            var (a, b) = m.EdgeVertices(e);
            foreach (int v in new[] { a, b })
            {
                int next = MeshOps.OppositeEdgeAtVertex(m, v, e);
                if (next >= 0 && !edges.Contains(next)) add.Add(next);
            }
        }
        edges.UnionWith(add);
    }

    /// <summary>Shrink Along Loop: 루프 양 끝(한쪽만 선택 엣지와 이어진) 엣지를 뺀다.</summary>
    /// <remarks>
    /// 엣지의 두 끝 중 루프 방향 이웃이 선택에 없는 쪽이 하나라도 있으면(= 루프 끝) 제거한다. 닫힌 루프는 그대로 남는다.
    /// </remarks>
    public static void ShrinkAlongLoop(PolyMesh m, HashSet<int> edges)
    {
        var remove = new List<int>();
        foreach (int e in edges)
        {
            var (a, b) = m.EdgeVertices(e);
            int connected = 0;
            foreach (int v in new[] { a, b }) { int next = MeshOps.OppositeEdgeAtVertex(m, v, e); if (next >= 0 && edges.Contains(next)) connected++; }
            if (connected < 2) remove.Add(e);
        }
        edges.ExceptWith(remove);
    }

    /// <summary>Contained Faces(all) / Connected Faces(any): UV 점 집합과 면의 관계.</summary>
    /// <param name="all">true면 모든 코너가 집합에 든 면(Contained), false면 하나라도 든 면(Connected).</param>
    public static List<int> FacesOfPoints(PolyMesh m, UvTopology topo, IEnumerable<int> points, bool all)
    {
        var set = new HashSet<int>(points);
        var list = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            // hit: 집합에 든 코너 수, n: 면의 코너 수
            int hit = 0, n = 0;
            foreach (int h in FaceHalfEdges(m, f)) { n++; if (set.Contains(topo.HeToPoint[h])) hit++; }
            if (all ? hit == n : hit > 0) list.Add(f);
        }
        return list;
    }

    /// <summary>Split UVs: 선택 UV 점의 정점에 닿은 모든 엣지를 심으로 만든다(점을 셸에서 떼어 낸다).</summary>
    /// <remarks>점 하나의 정점만이 아니라 그 정점의 모든 엣지가 심이 되므로 같은 정점의 다른 UV 점도 서로 분리된다.</remarks>
    public static void SplitUvs(PolyMesh m, UvTopology topo, IEnumerable<int> points)
    {
        var edges = new List<int>();
        foreach (int p in points)
        {
            m.GetVertexEdges(topo.Points[p].Vertex, edges);
            CutEdges(m, edges);
        }
    }

    /// <summary>Merge UVs: 선택 UV 점들 사이에서 서로 threshold 안에 있고 같은 정점인 점을 합친다(엣지가 있으면 Sew). 반환값은 합친 수.</summary>
    /// <remarks>
    /// 같은 3D 정점에 속한 UV 점끼리만 비교한다(다른 정점은 합치지 않음 — UV 점은 정점별로만 의미가 있다).
    /// 거리 threshold 이내 쌍을 평균 위치로 옮기고, 두 점을 양 옆에 둔 심 엣지를 해제한다.
    /// 같은 실행 중에는 UvTopology를 다시 만들지 않으므로 이후 호출자가 Build를 다시 해야 한다.
    /// </remarks>
    public static int MergeUvs(PolyMesh m, UvTopology topo, IEnumerable<int> points, float threshold)
    {
        var list = points.ToList(); int n = 0;
        float t2 = threshold * threshold;
        var byVertex = list.GroupBy(p => topo.Points[p].Vertex);
        foreach (var g in byVertex)
        {
            var pts = g.ToList(); if (pts.Count < 2) continue;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    if (Vector2.DistanceSquared(topo.Points[pts[i]].Uv, topo.Points[pts[j]].Uv) > t2) continue;
                    var avg = (topo.Points[pts[i]].Uv + topo.Points[pts[j]].Uv) * 0.5f;
                    SetPointUv(m, topo, pts[i], avg); SetPointUv(m, topo, pts[j], avg);
                    // 두 점을 잇는 심 엣지가 있으면 해제
                    var edges = new List<int>(); m.GetVertexEdges(g.Key, edges);
                    foreach (int e in edges)
                    {
                        var ed = m.Edges[e]; if (!ed.Seam || ed.He1 < 0) continue;
                        var ends = new[] { topo.HeToPoint[ed.He0], topo.HeToPoint[m.Hes[ed.He0].Next], topo.HeToPoint[ed.He1], topo.HeToPoint[m.Hes[ed.He1].Next] };
                        if (ends.Contains(pts[i]) && ends.Contains(pts[j])) { ed.Seam = false; m.Edges[e] = ed; }
                    }
                    n++;
                }
        }
        return n;
    }

    /// <summary>Create UV Shell: 면 집합의 바깥 경계를 심으로, 안쪽 엣지는 심 해제.</summary>
    /// <remarks>
    /// 면 집합 둘레(상대 면이 집합 밖)는 심으로 자르고, 내부 엣지는 심을 해제하면서 Sew로 양쪽 UV를 평균 맞춘다.
    /// 메시 경계 엣지는 건드리지 않는다.
    /// </remarks>
    public static void CreateUvShell(PolyMesh m, IEnumerable<int> faces)
    {
        var set = new HashSet<int>(faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive));
        foreach (int f in set)
            foreach (int h in FaceHalfEdges(m, f))
            {
                var he = m.Hes[h]; if (he.Twin < 0) continue;
                bool boundary = !set.Contains(m.Hes[he.Twin].Face);
                var ed = m.Edges[he.Edge];
                if (boundary) ed.Seam = true;
                else { ed.Seam = false; SewEdges(m, new[] { he.Edge }); continue; }
                m.Edges[he.Edge] = ed;
            }
    }

    /// <summary>Delete UVs: 면의 코너 UV를 0으로 두고 둘레를 심으로 분리한다(Unmapped 취급).</summary>
    /// <remarks>Maya처럼 실제로 UV를 "없애는" 대신 (0, 0)으로 모아 Unmapped Faces 선택에 걸리게 한다.</remarks>
    public static void DeleteUvs(PolyMesh m, IEnumerable<int> faces)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        foreach (int f in list) foreach (int h in FaceHalfEdges(m, f)) SetUv(m, h, Vector2.Zero);
        MarkSeamsAroundSelection(m, list);
    }

    /// <summary>UV 통계: 셸 수, 겹치는 면 수, 뒤집힌 면 수, 0..1 타일 사용률(면적 합).</summary>
    /// <remarks>usage는 모든 면 UV 넓이 절대값의 합이다(0..1 타일 넓이 = 1이므로 그대로 사용률, 겹치면 1을 넘을 수 있음).</remarks>
    public static (int shells, int overlapping, int reversed, float usage) Statistics(PolyMesh m, UvTopology topo)
    {
        float area = 0; int reversed = 0;
        for (int f = 0; f < m.FaceCount; f++) { if (!m.Faces[f].Alive) continue; float a = FaceUvSignedArea(m, f); if (a < 0) reversed++; area += MathF.Abs(a); }
        return (topo.ShellCount, OverlappingFaces(m).Count, reversed, area);
    }

    /// <summary>UV 왜곡 비율(면별): sqrt(UV 면적 / 3D 면적)을 전체 평균으로 나눈 값. 1보다 작으면 늘어남(stretched), 크면 눌림.</summary>
    /// <remarks>
    /// 면마다 sqrt(UV 넓이 / 3D 넓이)를 구하고 전체 평균 비(sqrt(ΣUV / Σ3D))로 나눈다. 1이면 텍셀 밀도가 평균과 같다.
    /// UV 편집기 Distortion 표시(파랑 = 늘어남, 빨강 = 눌림)에 쓰며 결과 배열 인덱스는 면 ID(죽은 면은 0).
    /// </remarks>
    public static float[] DistortionPerFace(PolyMesh m)
    {
        var ratio = new float[m.FaceCount];
        double sumUv = 0, sum3 = 0;
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            // 3D 넓이 = 비정규화 면 법선 길이 / 2(뉴웰 법선 기준)
            float uv = MathF.Abs(FaceUvSignedArea(m, f)); float a3 = MeshNormals.FaceNormalUnnormalized(m, f).Length() * 0.5f;
            sumUv += uv; sum3 += a3;
            ratio[f] = a3 > 1e-12f ? MathF.Sqrt(uv / a3) : 1f;
        }
        // 전역 비율로 정규화
        float global = sum3 > 1e-12 && sumUv > 1e-12 ? MathF.Sqrt((float)(sumUv / sum3)) : 1f;
        for (int f = 0; f < ratio.Length; f++) if (m.Faces[f].Alive) ratio[f] /= global;
        return ratio;
    }
}
