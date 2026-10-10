using System.Numerics;
using System.Runtime.CompilerServices;
using Cube.Core.Mesh;

namespace Cube.Core.Selection;

/// <summary>
/// 대칭 평면(메시 오브젝트 공간). Maya Tool Settings → Symmetry(Object X/Y/Z, World X/Y/Z)에 해당(v0.0.64).
/// 점 <see cref="Point"/>를 지나고 법선 <see cref="Normal"/>(단위)인 평면. <see cref="Tolerance"/> 안의 정점은 '평면 위'로 본다.
/// </summary>
public sealed class SymmetryPlane
{
    public Vector3 Point;
    public Vector3 Normal;
    public float Tolerance = 0.001f;

    /// <summary>부호 있는 거리(법선 쪽 +).</summary>
    public float Signed(Vector3 p) => Vector3.Dot(p - Point, Normal);
    /// <summary>평면 위인지(|거리| ≤ 허용 오차).</summary>
    public bool OnPlane(Vector3 p) => MathF.Abs(Signed(p)) <= Tolerance;
    /// <summary>평면에 대한 거울 위치.</summary>
    public Vector3 Reflect(Vector3 p) => p - Normal * (2f * Signed(p));
    /// <summary>평면 위로 투영.</summary>
    public Vector3 Project(Vector3 p) => p - Normal * Signed(p);

    /// <summary>반사 행렬(행벡터 규약: p' = p·R). R·R = I.</summary>
    public Matrix4x4 ReflectMatrix
    {
        get
        {
            var n = Normal; float d = Vector3.Dot(Point, n);
            // p' = p − 2 n (p·n − d) = p (I − 2 n nᵀ) + 2 d n
            var m = Matrix4x4.Identity;
            m.M11 -= 2 * n.X * n.X; m.M12 -= 2 * n.X * n.Y; m.M13 -= 2 * n.X * n.Z;
            m.M21 -= 2 * n.Y * n.X; m.M22 -= 2 * n.Y * n.Y; m.M23 -= 2 * n.Y * n.Z;
            m.M31 -= 2 * n.Z * n.X; m.M32 -= 2 * n.Z * n.Y; m.M33 -= 2 * n.Z * n.Z;
            m.M41 = 2 * d * n.X; m.M42 = 2 * d * n.Y; m.M43 = 2 * d * n.Z;
            return m;
        }
    }

    /// <summary>오브젝트 공간 축 평면(원점 통과).</summary>
    public static SymmetryPlane Object(int axis, float tol = 0.001f) => new() { Point = Vector3.Zero, Normal = AxisVector(axis), Tolerance = tol };

    /// <summary>월드 축 평면(월드 원점 통과)을 노드 월드 행렬로 오브젝트 공간에 옮긴 평면.</summary>
    /// <remarks>행벡터 규약: x_w = x_o·W → 법선 n_o = n_w·Wᵀ(정규화), 점 = 원점·inv(W).</remarks>
    public static SymmetryPlane World(int axis, Matrix4x4 nodeWorld, float tol = 0.001f)
    {
        Matrix4x4.Invert(nodeWorld, out var inv);
        var nw = AxisVector(axis);
        var no = Vector3.TransformNormal(nw, Matrix4x4.Transpose(nodeWorld));
        if (no.LengthSquared() < 1e-20f) no = nw;
        return new SymmetryPlane { Point = Vector3.Transform(Vector3.Zero, inv), Normal = Vector3.Normalize(no), Tolerance = tol };
    }

    private static Vector3 AxisVector(int axis) => axis switch { 1 => Vector3.UnitY, 2 => Vector3.UnitZ, _ => Vector3.UnitX };

    /// <summary>같은 평면인지(캐시 키 비교).</summary>
    public bool SameAs(SymmetryPlane o) => Vector3.Distance(Point, o.Point) < 1e-7f && Vector3.Distance(Normal, o.Normal) < 1e-7f && MathF.Abs(Tolerance - o.Tolerance) < 1e-9f;
}

/// <summary>
/// 메시의 거울 짝 테이블: <see cref="Mirror"/>[v] = 거울 위치(허용 오차 안)에 있는 정점, 평면 위 정점은 자기 자신, 짝이 없으면 −1.
/// 메시의 위상·형상 버전과 평면이 같으면 캐시(<see cref="Get"/>)를 재사용한다.
/// </summary>
public sealed class SymmetryMap
{
    public readonly int[] Mirror;
    public readonly SymmetryPlane Plane;
    private readonly int _topo, _geom;
    private static readonly ConditionalWeakTable<PolyMesh, SymmetryMap> Cache = new();

    private SymmetryMap(PolyMesh m, SymmetryPlane plane)
    {
        Plane = plane; _topo = m.TopologyVersion; _geom = m.GeometryVersion;
        Mirror = Build(m, plane);
    }

    /// <summary>캐시된 테이블(없거나 메시·평면이 바뀌었으면 새로 만든다).</summary>
    public static SymmetryMap Get(PolyMesh m, SymmetryPlane plane)
    {
        if (Cache.TryGetValue(m, out var c) && c._topo == m.TopologyVersion && c._geom == m.GeometryVersion && c.Plane.SameAs(plane)) return c;
        var map = new SymmetryMap(m, plane);
        Cache.AddOrUpdate(m, map);
        return map;
    }

    /// <summary>공간 해시로 거울 위치의 정점을 찾는다(셀 = 허용 오차 × 4; 이웃 27칸).</summary>
    private static int[] Build(PolyMesh m, SymmetryPlane plane)
    {
        var map = new int[m.VertexCount];
        Array.Fill(map, -1);
        float tol = MathF.Max(plane.Tolerance, 1e-7f), cell = tol * 4f;
        var grid = new Dictionary<(long, long, long), List<int>>();
        (long, long, long) Cell(Vector3 p) => ((long)MathF.Floor(p.X / cell), (long)MathF.Floor(p.Y / cell), (long)MathF.Floor(p.Z / cell));
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            var c = Cell(m.Verts[v].Position);
            if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
            l.Add(v);
        }
        float tol2 = tol * tol;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            var p = m.Verts[v].Position;
            if (plane.OnPlane(p)) { map[v] = v; continue; }
            var r = plane.Reflect(p);
            var (cx, cy, cz) = Cell(r);
            int best = -1; float bd = tol2;
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) continue;
                foreach (int u in l) { if (u == v) continue; float d = Vector3.DistanceSquared(m.Verts[u].Position, r); if (d <= bd) { bd = d; best = u; } }
            }
            map[v] = best;
        }
        return map;
    }

    /// <summary>정점의 거울 짝(없으면 −1, 평면 위면 자기 자신).</summary>
    public int MirrorVertex(int v) => v >= 0 && v < Mirror.Length ? Mirror[v] : -1;

    /// <summary>엣지의 거울 짝: 양끝 짝 사이 엣지(없으면 −1).</summary>
    public int MirrorEdge(PolyMesh m, int e)
    {
        if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive) return -1;
        var (a, b) = m.EdgeVertices(e);
        int ma = MirrorVertex(a), mb = MirrorVertex(b);
        if (ma < 0 || mb < 0 || ma == mb) return -1;
        return m.FindEdge(ma, mb);
    }

    /// <summary>면의 거울 짝: 모든 꼭짓점의 짝으로 이루어진 면(없으면 −1).</summary>
    public int MirrorFace(PolyMesh m, int f)
    {
        if (f < 0 || f >= m.FaceCount || !m.Faces[f].Alive) return -1;
        var vs = new List<int>(); m.GetFaceVertices(f, vs);
        var mirrored = new HashSet<int>();
        foreach (int v in vs) { int mv = MirrorVertex(v); if (mv < 0) return -1; mirrored.Add(mv); }
        if (mirrored.Count != vs.Count) return -1;
        // 첫 짝 정점에 닿은 면 중 꼭짓점 집합이 같은 것
        var fs = new List<int>(); m.GetVertexFaces(mirrored.First(), fs);
        var tmp = new List<int>();
        foreach (int g in fs)
        {
            m.GetFaceVertices(g, tmp);
            if (tmp.Count == vs.Count && tmp.All(mirrored.Contains)) return g;
        }
        return -1;
    }

    /// <summary>컴포넌트 ID의 거울 짝(모드별).</summary>
    public int MirrorComponent(PolyMesh m, SelectMode mode, int id) => mode switch
    {
        SelectMode.Vertex => MirrorVertex(id),
        SelectMode.Edge => MirrorEdge(m, id),
        SelectMode.Face => MirrorFace(m, id),
        _ => -1,
    };

    /// <summary>집합의 모든 컴포넌트에 거울 짝을 더한다(짝이 없는 것은 그대로). 반환값은 짝이 없던 개수.</summary>
    public int Expand(PolyMesh m, SelectMode mode, HashSet<int> set)
    {
        int missing = 0;
        foreach (int id in set.ToArray())
        {
            int mi = MirrorComponent(m, mode, id);
            if (mi >= 0) set.Add(mi); else missing++;
        }
        return missing;
    }
}

/// <summary>대칭 변형 규칙.</summary>
public static class SymmetryOps
{
    /// <summary>
    /// 정점 하나에 로컬 변형 행렬을 대칭 규칙으로 적용한다: 평면 + 쪽(법선 쪽)은 그대로, − 쪽은 거울 변형(R·M·R), 평면 위는 변형 뒤 평면으로 투영(평면을 벗어나지 않음).
    /// 어느 쪽인지는 변형 전 위치로 정하므로 드래그와 이력 재실행이 같은 결과를 낸다.
    /// </summary>
    public static Vector3 Transform(Vector3 p, Matrix4x4 local, SymmetryPlane? plane)
    {
        if (plane == null) return Vector3.Transform(p, local);
        float d = plane.Signed(p);
        if (MathF.Abs(d) <= plane.Tolerance) return plane.Project(Vector3.Transform(p, local));
        if (d > 0) return Vector3.Transform(p, local);
        var r = plane.ReflectMatrix;
        return Vector3.Transform(p, r * local * r);
    }

    /// <summary>엣지 위 비율 t(a→b)의 점을 거울 엣지(ma→mb) 위 비율로 옮긴다(거울 위치를 그 엣지에 투영).</summary>
    public static float MirrorParam(PolyMesh m, SymmetryPlane plane, int e, float t, int me)
    {
        var (a, b) = m.EdgeVertices(e); var (ma, mb) = m.EdgeVertices(me);
        var p = plane.Reflect(Vector3.Lerp(m.Verts[a].Position, m.Verts[b].Position, t));
        var pa = m.Verts[ma].Position; var d = m.Verts[mb].Position - pa;
        float l2 = d.LengthSquared();
        return l2 < 1e-18f ? t : Math.Clamp(Vector3.Dot(p - pa, d) / l2, 0.01f, 0.99f);
    }
}
