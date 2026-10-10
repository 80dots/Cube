using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Selection;

namespace Cube.Core.Uv;

/// <summary>
/// UV 편집기 Symmetry(v0.0.65, Maya UV Toolkit → Transform → Symmetry): UV 공간의 축선(u = Center 또는 v = Center)을 기준으로
/// 선택과 변형을 거울처럼 맞춘다. 3D Symmetry(<see cref="Selection.SymmetryPlane"/>)와 독립이며 UV 좌표만 본다.
/// </summary>
public sealed class UvSymmetryPlane
{
    /// <summary>0 = U(세로선 u = Center 기준으로 좌우 거울), 1 = V(가로선 v = Center 기준으로 위아래 거울).</summary>
    public int Axis;
    /// <summary>축선 위치(기본 0.5 = 0..1 타일 중앙).</summary>
    public float Center;
    /// <summary>거울 짝을 찾을 때 허용하는 UV 거리(기본 0.001).</summary>
    public float Tolerance;

    public UvSymmetryPlane(int axis, float center, float tolerance = 0.001f) { Axis = axis; Center = center; Tolerance = MathF.Max(tolerance, 1e-7f); }

    /// <summary>축선으로부터의 부호 있는 거리(+ = u/v가 Center보다 큰 쪽).</summary>
    public float Signed(Vector2 uv) => (Axis == 0 ? uv.X : uv.Y) - Center;
    /// <summary>허용 오차 안에서 축선 위에 있는지.</summary>
    public bool OnLine(Vector2 uv) => MathF.Abs(Signed(uv)) <= Tolerance;
    /// <summary>축선 반대편의 거울 위치.</summary>
    public Vector2 Reflect(Vector2 uv) => Axis == 0 ? new Vector2(2 * Center - uv.X, uv.Y) : new Vector2(uv.X, 2 * Center - uv.Y);
    /// <summary>축선 위로 투영.</summary>
    public Vector2 Project(Vector2 uv) => Axis == 0 ? new Vector2(Center, uv.Y) : new Vector2(uv.X, Center);
    /// <summary>거울 변환 행렬(행벡터, uv' = uv·R).</summary>
    public Matrix3x2 ReflectMatrix => Axis == 0 ? new Matrix3x2(-1, 0, 0, 1, 2 * Center, 0) : new Matrix3x2(1, 0, 0, -1, 0, 2 * Center);
    /// <summary>같은 평면인지(캐시 비교용).</summary>
    public bool SameAs(UvSymmetryPlane? o) => o != null && o.Axis == Axis && o.Center == Center && o.Tolerance == Tolerance;
}

/// <summary>
/// UV 점의 거울 짝 테이블. <see cref="UvTopology"/> 스냅샷 기준이며 UV가 바뀌면 다시 만든다(선택 한 번·드래그 한 번마다 Build해도 O(n)).
/// 엣지/면의 짝은 양끝/둘레 UV 점의 짝으로 정한다(UV 편집기의 엣지·면은 메시 엣지·면 ID지만 셸마다 따로 보이므로 UV 점 기준이 맞다).
/// </summary>
public sealed class UvSymmetryMap
{
    /// <summary>UV 점 → 거울 UV 점(축선 위 = 자기 자신, 없음 = −1).</summary>
    public readonly int[] Mirror;
    public readonly UvSymmetryPlane Plane;
    private readonly UvTopology _topo;
    private Dictionary<(int, int), int>? _edgeByPoints;
    private Dictionary<long, List<int>>? _faceByPoints;

    private UvSymmetryMap(UvTopology topo, UvSymmetryPlane plane, int[] mirror) { _topo = topo; Plane = plane; Mirror = mirror; }

    /// <summary>UV 점마다 거울 위치에 있는 점을 공간 해시로 찾는다. 같은 정점을 공유하는 짝(심 양쪽의 점)을 우선한다.</summary>
    public static UvSymmetryMap Build(UvTopology topo, UvSymmetryPlane plane)
    {
        int n = topo.Points.Count;
        var mirror = new int[n];
        float cell = MathF.Max(plane.Tolerance * 2f, 1e-5f);
        var grid = new Dictionary<(long, long), List<int>>();
        (long, long) Key(Vector2 uv) => ((long)MathF.Floor(uv.X / cell), (long)MathF.Floor(uv.Y / cell));
        for (int i = 0; i < n; i++)
        {
            var k = Key(topo.Points[i].Uv);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
            list.Add(i);
        }
        float tol2 = plane.Tolerance * plane.Tolerance;
        for (int i = 0; i < n; i++)
        {
            var uv = topo.Points[i].Uv;
            if (plane.OnLine(uv)) { mirror[i] = i; continue; }
            var r = plane.Reflect(uv);
            var (kx, ky) = Key(r);
            int best = -1; float bestD = float.MaxValue; bool bestSameVertex = false;
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((kx + dx, ky + dy), out var list)) continue;
                    foreach (int j in list)
                    {
                        if (j == i) continue;
                        float d = Vector2.DistanceSquared(topo.Points[j].Uv, r);
                        if (d > tol2) continue;
                        bool same = topo.Points[j].Vertex == topo.Points[i].Vertex;
                        if (same && !bestSameVertex || (same == bestSameVertex && d < bestD)) { best = j; bestD = d; bestSameVertex = same; }
                    }
                }
            mirror[i] = best;
        }
        return new UvSymmetryMap(topo, plane, mirror);
    }

    /// <summary>UV 점의 거울 짝(없으면 −1).</summary>
    public int MirrorPoint(int p) => p >= 0 && p < Mirror.Length ? Mirror[p] : -1;

    /// <summary>메시 정점의 거울 정점: 그 정점의 UV 점 중 하나라도 짝이 있으면 짝 점의 정점.</summary>
    public int MirrorVertex(PolyMesh m, int v)
    {
        if (v < 0 || v >= m.VertexCount || !m.Verts[v].Alive) return -1;
        foreach (int he in m.VertexOutgoing(v))
        {
            int p = he < _topo.HeToPoint.Length ? _topo.HeToPoint[he] : -1;
            if (p < 0) continue;
            int mp = Mirror[p];
            if (mp >= 0) return _topo.Points[mp].Vertex;
        }
        return -1;
    }

    /// <summary>메시 엣지의 거울 엣지: 어느 한 하프에지의 양끝 UV 점 짝을 양끝으로 갖는 엣지.</summary>
    public int MirrorEdge(PolyMesh m, int e)
    {
        if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive) return -1;
        _edgeByPoints ??= BuildEdgeIndex(m);
        var ed = m.Edges[e];
        for (int k = 0; k < 2; k++)
        {
            int he = k == 0 ? ed.He0 : ed.He1; if (he < 0) continue;
            int pa = _topo.HeToPoint[he], pb = _topo.HeToPoint[m.Hes[he].Next];
            if (pa < 0 || pb < 0) continue;
            int ma = Mirror[pa], mb = Mirror[pb];
            if (ma < 0 || mb < 0) continue;
            if (_edgeByPoints.TryGetValue((Math.Min(ma, mb), Math.Max(ma, mb)), out int me)) return me;
        }
        return -1;
    }

    /// <summary>메시 면의 거울 면: 둘레 UV 점 집합의 짝 집합을 둘레로 갖는 면.</summary>
    public int MirrorFace(PolyMesh m, int f)
    {
        if (f < 0 || f >= m.FaceCount || !m.Faces[f].Alive) return -1;
        _faceByPoints ??= BuildFaceIndex(m);
        var pts = new List<int>();
        foreach (int he in UvOps.FaceHalfEdges(m, f))
        {
            int p = _topo.HeToPoint[he]; if (p < 0) return -1;
            int mp = Mirror[p]; if (mp < 0) return -1;
            pts.Add(mp);
        }
        pts.Sort();
        if (!_faceByPoints.TryGetValue(Hash(pts), out var cands)) return -1;
        foreach (int g in cands)
        {
            var other = SortedPoints(m, g);
            if (other.Count == pts.Count && other.SequenceEqual(pts)) return g;
        }
        return -1;
    }

    /// <summary>모드별 거울 컴포넌트(Vertex/Edge/Face/Uv). 없으면 −1.</summary>
    public int MirrorComponent(PolyMesh m, SelectMode mode, int id) => mode switch
    {
        SelectMode.Uv => MirrorPoint(id),
        SelectMode.Vertex => MirrorVertex(m, id),
        SelectMode.Edge => MirrorEdge(m, id),
        SelectMode.Face => MirrorFace(m, id),
        _ => -1,
    };

    /// <summary>집합에 각 요소의 거울 짝을 더한다. 짝이 없는 요소 수를 돌려준다.</summary>
    public int Expand(PolyMesh m, SelectMode mode, HashSet<int> set)
    {
        int missing = 0;
        foreach (int c in set.ToArray()) { int mc = MirrorComponent(m, mode, c); if (mc >= 0) set.Add(mc); else missing++; }
        return missing;
    }

    private Dictionary<(int, int), int> BuildEdgeIndex(PolyMesh m)
    {
        var d = new Dictionary<(int, int), int>();
        for (int he = 0; he < m.HalfEdgeCount; he++)
        {
            if (!m.Hes[he].Alive) continue;
            int pa = _topo.HeToPoint[he], pb = _topo.HeToPoint[m.Hes[he].Next];
            if (pa < 0 || pb < 0) continue;
            d[(Math.Min(pa, pb), Math.Max(pa, pb))] = m.Hes[he].Edge;
        }
        return d;
    }

    private Dictionary<long, List<int>> BuildFaceIndex(PolyMesh m)
    {
        var d = new Dictionary<long, List<int>>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            long h = Hash(SortedPoints(m, f));
            if (!d.TryGetValue(h, out var list)) d[h] = list = new List<int>();
            list.Add(f);
        }
        return d;
    }

    private List<int> SortedPoints(PolyMesh m, int f)
    {
        var pts = new List<int>();
        foreach (int he in UvOps.FaceHalfEdges(m, f)) pts.Add(_topo.HeToPoint[he]);
        pts.Sort();
        return pts;
    }

    private static long Hash(List<int> sorted)
    {
        long h = 1469598103934665603L;
        foreach (int p in sorted) { h ^= p; h *= 1099511628211L; }
        return h;
    }
}

/// <summary>UV Symmetry의 변형 규칙.</summary>
public static class UvSymmetryOps
{
    /// <summary>
    /// 변형 전 위치 p로 쪽을 정해 변환한다: 주(primary) 쪽은 xf 그대로, 반대쪽은 R·xf·R(거울 변형), 축선 위 점은 변환 뒤 축선에 투영.
    /// </summary>
    /// <param name="positivePrimary">true면 + 쪽(u/v &gt; Center)이 주 쪽(드래그를 그대로 따름). 드래그 기준점(피벗·집은 점)이 − 쪽이면 false로 넘겨 그쪽이 따르게 한다.</param>
    public static Vector2 Transform(Vector2 p, Matrix3x2 xf, UvSymmetryPlane plane, bool positivePrimary = true)
    {
        float d = plane.Signed(p);
        if (MathF.Abs(d) <= plane.Tolerance) return plane.Project(Vector2.Transform(p, xf));
        bool primary = positivePrimary ? d > 0 : d < 0;
        if (primary) return Vector2.Transform(p, xf);
        var r = plane.ReflectMatrix;
        return Vector2.Transform(p, r * xf * r);
    }
}
