using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>UV Editor Select 메뉴/Cut-Sew 메뉴 도우미: Overlapping, Back/Front-facing, Texture Borders, Unmapped, Shortest Edge Path, Grow/Shrink Along Loop, Contained/Connected Faces, Split/Merge UVs, Create UV Shell, Delete UVs, 통계.</summary>
public static partial class UvOps
{
    /// <summary>UV 면적이 음수인 면(뒤집힘 = Back-facing).</summary>
    public static List<int> BackFacingFaces(PolyMesh m, bool back = true)
    {
        var list = new List<int>();
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive && (FaceUvSignedArea(m, f) < 0) == back) list.Add(f);
        return list;
    }

    /// <summary>다른 면과 UV 공간에서 겹치는 면(같은 정점을 공유하는 이웃은 제외). O(n²)에 경계 상자 가지치기.</summary>
    public static List<int> OverlappingFaces(PolyMesh m)
    {
        var faces = new List<(int f, Vector2[] poly, Vector2 min, Vector2 max)>();
        var hes = new List<int>();
        double sumW = 0, sumH = 0;
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
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
    public static bool PolygonsOverlap(Vector2[] a, Vector2[] b)
    {
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                if (SegmentsCrossStrict(a[i], a[(i + 1) % a.Length], b[j], b[(j + 1) % b.Length])) return true;
        var ca = Centroid(a); var cb = Centroid(b);
        return PointInPolygonStrict(ca, b) || PointInPolygonStrict(cb, a);
    }

    private static Vector2 Centroid(Vector2[] p) { var s = Vector2.Zero; foreach (var q in p) s += q; return s / p.Length; }

    private static bool SegmentsCrossStrict(Vector2 p, Vector2 p2, Vector2 q, Vector2 q2)
    {
        float d1 = Cross(p2 - p, q - p), d2 = Cross(p2 - p, q2 - p), d3 = Cross(q2 - q, p - q), d4 = Cross(q2 - q, p2 - q);
        const float eps = 1e-9f;
        return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
    }

    private static bool PointInPolygonStrict(Vector2 pt, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if (MathF.Abs(Cross(b - a, pt - a)) < 1e-9f && Vector2.Dot(pt - a, pt - b) <= 0) return false; // 변 위
            if ((a.Y > pt.Y) != (b.Y > pt.Y) && pt.X < (b.X - a.X) * (pt.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Texture Borders: 셸 경계(심/메시 경계) 위의 UV 점.</summary>
    public static List<int> TextureBorderPoints(PolyMesh m, UvTopology topo)
    {
        var set = new HashSet<int>();
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h]; if (!he.Alive) continue;
            bool border = he.Twin < 0 || m.Edges[he.Edge].Seam || topo.HeToPoint[he.Twin] != topo.HeToPoint[he.Next] || topo.HeToPoint[m.Hes[he.Twin].Next] != topo.HeToPoint[h];
            if (border) { set.Add(topo.HeToPoint[h]); set.Add(topo.HeToPoint[he.Next]); }
        }
        return set.ToList();
    }

    /// <summary>Unmapped faces: 모든 코너 UV가 (0,0)인 면.</summary>
    public static List<int> UnmappedFaces(PolyMesh m)
    {
        var list = new List<int>();
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive && FaceHalfEdges(m, f).All(h => m.Hes[h].Uv0.LengthSquared() < 1e-16f)) list.Add(f);
        return list;
    }

    /// <summary>Shortest Edge Path: 두 정점 사이 3D 최단 엣지 경로(Dijkstra). 반환값은 엣지 ID들(없으면 빈 목록).</summary>
    public static List<int> ShortestEdgePath(PolyMesh m, int va, int vb)
    {
        var result = new List<int>();
        if (va < 0 || vb < 0 || va >= m.VertexCount || vb >= m.VertexCount || va == vb) return result;
        var dist = new Dictionary<int, float> { [va] = 0f };
        var prevEdge = new Dictionary<int, int>();
        var queue = new PriorityQueue<int, float>(); queue.Enqueue(va, 0f);
        var done = new HashSet<int>();
        var edges = new List<int>();
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
        if (!prevEdge.ContainsKey(vb)) return result;
        int cur = vb;
        while (cur != va) { int e = prevEdge[cur]; result.Add(e); var (x, y) = m.EdgeVertices(e); cur = x == cur ? y : x; }
        result.Reverse();
        return result;
    }

    /// <summary>Grow Along Loop: 선택 엣지 집합의 양 끝에서 루프 방향으로 한 엣지씩 늘린다.</summary>
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
    public static List<int> FacesOfPoints(PolyMesh m, UvTopology topo, IEnumerable<int> points, bool all)
    {
        var set = new HashSet<int>(points);
        var list = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            int hit = 0, n = 0;
            foreach (int h in FaceHalfEdges(m, f)) { n++; if (set.Contains(topo.HeToPoint[h])) hit++; }
            if (all ? hit == n : hit > 0) list.Add(f);
        }
        return list;
    }

    /// <summary>Split UVs: 선택 UV 점의 정점에 닿은 모든 엣지를 심으로 만든다(점을 셸에서 떼어 낸다).</summary>
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
    public static void DeleteUvs(PolyMesh m, IEnumerable<int> faces)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        foreach (int f in list) foreach (int h in FaceHalfEdges(m, f)) SetUv(m, h, Vector2.Zero);
        MarkSeamsAroundSelection(m, list);
    }

    /// <summary>UV 통계: 셸 수, 겹치는 면 수, 뒤집힌 면 수, 0..1 타일 사용률(면적 합).</summary>
    public static (int shells, int overlapping, int reversed, float usage) Statistics(PolyMesh m, UvTopology topo)
    {
        float area = 0; int reversed = 0;
        for (int f = 0; f < m.FaceCount; f++) { if (!m.Faces[f].Alive) continue; float a = FaceUvSignedArea(m, f); if (a < 0) reversed++; area += MathF.Abs(a); }
        return (topo.ShellCount, OverlappingFaces(m).Count, reversed, area);
    }

    /// <summary>UV 왜곡 비율(면별): sqrt(UV 면적 / 3D 면적)을 전체 평균으로 나눈 값. 1보다 작으면 늘어남(stretched), 크면 눌림.</summary>
    public static float[] DistortionPerFace(PolyMesh m)
    {
        var ratio = new float[m.FaceCount];
        double sumUv = 0, sum3 = 0;
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            float uv = MathF.Abs(FaceUvSignedArea(m, f)); float a3 = MeshNormals.FaceNormalUnnormalized(m, f).Length() * 0.5f;
            sumUv += uv; sum3 += a3;
            ratio[f] = a3 > 1e-12f ? MathF.Sqrt(uv / a3) : 1f;
        }
        float global = sum3 > 1e-12 && sumUv > 1e-12 ? MathF.Sqrt((float)(sumUv / sum3)) : 1f;
        for (int f = 0; f < ratio.Length; f++) if (m.Faces[f].Alive) ratio[f] /= global;
        return ratio;
    }
}
