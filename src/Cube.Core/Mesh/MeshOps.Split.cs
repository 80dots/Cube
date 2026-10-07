using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>엣지/면 분할과 공통 면 재구성 도우미. Connect, Multi-Cut, Add Divisions, Poke 등의 기반.</summary>
public static partial class MeshOps
{
    /// <summary>엣지 플래그(하드/심/크리즈) 묶음.</summary>
    internal readonly record struct EdgeFlags(bool Hard, bool Seam, float Crease)
    {
        public static readonly EdgeFlags None = new(false, false, 0f);
    }

    internal static EdgeFlags GetFlags(PolyMesh m, int a, int b)
    {
        int e = m.FindEdge(a, b);
        if (e < 0) return EdgeFlags.None;
        var ed = m.Edges[e];
        return new EdgeFlags(ed.Hard, ed.Seam, ed.Crease);
    }

    internal static void SetFlags(PolyMesh m, int a, int b, EdgeFlags f)
    {
        int e = m.FindEdge(a, b);
        if (e < 0) return;
        var ed = m.Edges[e]; ed.Hard = f.Hard; ed.Seam = f.Seam; ed.Crease = f.Crease; m.Edges[e] = ed;
    }

    /// <summary>
    /// 면 집합을 캡처해 제거한 뒤 새 루프로 다시 만들고 엣지 플래그를 복원한다.
    /// 플래그는 정점 쌍으로 찾고, 분할로 생긴 정점(parent[v] = (a,b))의 엣지는 부모 엣지의 플래그를 물려받는다.
    /// </summary>
    internal sealed class FaceRebuilder
    {
        private readonly PolyMesh _m;
        private readonly Dictionary<long, EdgeFlags> _flags = new();
        private readonly Dictionary<int, (int a, int b)> _parent = new();
        public readonly List<(int face, List<Corner> corners, int material)> Captured = new();

        public FaceRebuilder(PolyMesh m) { _m = m; }

        private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        public void Capture(int f)
        {
            if (f < 0 || f >= _m.FaceCount || !_m.Faces[f].Alive) return;
            if (Captured.Any(c => c.face == f)) return;
            var corners = CaptureCorners(_m, f);
            for (int i = 0; i < corners.Count; i++)
            {
                int a = corners[i].Vertex, b = corners[(i + 1) % corners.Count].Vertex;
                _flags[Key(a, b)] = GetFlags(_m, a, b);
            }
            Captured.Add((f, corners, _m.Faces[f].Material));
        }

        /// <summary>v가 (a,b) 엣지 위에서 만들어졌음을 기록한다(플래그 상속).</summary>
        public void SetParent(int v, int a, int b) => _parent[v] = (a, b);

        public EdgeFlags FlagsFor(int x, int y)
        {
            if (_flags.TryGetValue(Key(x, y), out var f)) return f;
            if (_parent.TryGetValue(x, out var px) && (px.a == y || px.b == y || (_parent.TryGetValue(y, out var py) && py == px))) return _flags.TryGetValue(Key(px.a, px.b), out var pf) ? pf : EdgeFlags.None;
            if (_parent.TryGetValue(y, out var py2) && (py2.a == x || py2.b == x)) return _flags.TryGetValue(Key(py2.a, py2.b), out var pf2) ? pf2 : EdgeFlags.None;
            return EdgeFlags.None;
        }

        public void RemoveCaptured()
        {
            foreach (var (f, _, _) in Captured) _m.RemoveFace(f, removeIsolated: false);
        }

        /// <summary>새 면을 추가하고 모든 엣지의 플래그를 복원한다. 실패하면 -1.</summary>
        public int AddFace(IReadOnlyList<Corner> loop, int material)
        {
            var clean = new List<Corner>();
            foreach (var c in loop) if (clean.Count == 0 || clean[^1].Vertex != c.Vertex) clean.Add(c);
            while (clean.Count > 1 && clean[0].Vertex == clean[^1].Vertex) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 3) return -1;
            int nf = AddFaceWithCorners(_m, clean, material);
            if (nf < 0) return -1;
            for (int i = 0; i < clean.Count; i++) SetFlags(_m, clean[i].Vertex, clean[(i + 1) % clean.Count].Vertex, FlagsFor(clean[i].Vertex, clean[(i + 1) % clean.Count].Vertex));
            return nf;
        }
    }

    // ------------------------------------------------------------ Split edge / face

    /// <summary>엣지를 t(0..1, EdgeVertices 순서 a→b)에서 나눈다. 양쪽 면에 새 정점이 끼워지고 코너 UV/노멀은 보간된다. 반환값은 새 정점.</summary>
    public static int SplitEdge(PolyMesh m, int e, float t)
    {
        if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive) return -1;
        t = Math.Clamp(t, 0.001f, 0.999f);
        var (a, b) = m.EdgeVertices(e);
        int nv = m.AddVertex(Vector3.Lerp(m.Verts[a].Position, m.Verts[b].Position, t));
        var rb = new FaceRebuilder(m);
        var (f0, f1) = m.EdgeFaces(e);
        rb.Capture(f0); if (f1 >= 0) rb.Capture(f1);
        rb.SetParent(nv, a, b);
        rb.RemoveCaptured();
        foreach (var (_, corners, material) in rb.Captured)
        {
            var loop = new List<Corner>();
            int n = corners.Count;
            for (int i = 0; i < n; i++)
            {
                var c = corners[i]; var d = corners[(i + 1) % n];
                loop.Add(c);
                if ((c.Vertex == a && d.Vertex == b) || (c.Vertex == b && d.Vertex == a))
                {
                    float s = c.Vertex == a ? t : 1f - t;
                    loop.Add(new Corner(nv, Vector2.Lerp(c.Uv, d.Uv, s), Vector3.Normalize(Vector3.Lerp(c.Normal, d.Normal, s) + new Vector3(1e-12f))));
                }
            }
            rb.AddFace(loop, material);
        }
        m.BumpTopology();
        return nv;
    }

    /// <summary>면을 두 코너 정점 va–vb 사이에서 둘로 나눈다(인접 코너면 실패). 반환값은 새 엣지 ID(-1 = 실패).</summary>
    public static int SplitFace(PolyMesh m, int f, int va, int vb)
    {
        if (f < 0 || f >= m.FaceCount || !m.Faces[f].Alive || va == vb) return -1;
        var corners = CaptureCorners(m, f);
        int n = corners.Count;
        int i = corners.FindIndex(c => c.Vertex == va), j = corners.FindIndex(c => c.Vertex == vb);
        if (i < 0 || j < 0) return -1;
        if ((i + 1) % n == j || (j + 1) % n == i) return -1; // 이미 엣지
        var rb = new FaceRebuilder(m);
        rb.Capture(f);
        rb.RemoveCaptured();
        var loop1 = new List<Corner>(); var loop2 = new List<Corner>();
        for (int k = i; ; k = (k + 1) % n) { loop1.Add(corners[k]); if (k == j) break; }
        for (int k = j; ; k = (k + 1) % n) { loop2.Add(corners[k]); if (k == i) break; }
        int material = rb.Captured[0].material;
        int f1 = rb.AddFace(loop1, material), f2 = rb.AddFace(loop2, material);
        m.BumpTopology();
        if (f1 < 0 && f2 < 0) return -1;
        return m.FindEdge(va, vb);
    }

    /// <summary>두 정점을 코너로 가지는 공통 면을 찾아 나눈다(이미 엣지로 이어져 있으면 -1).</summary>
    public static int SplitFaceBetween(PolyMesh m, int va, int vb)
    {
        if (m.FindEdge(va, vb) >= 0) return -1;
        var fa = new List<int>(); m.GetVertexFaces(va, fa);
        var fb = new List<int>(); m.GetVertexFaces(vb, fb);
        foreach (int f in fa) if (fb.Contains(f)) { int e = SplitFace(m, f, va, vb); if (e >= 0) return e; }
        return -1;
    }

    // ------------------------------------------------------------ Add Divisions

    public enum DivisionMode { Quads, Triangles }

    /// <summary>
    /// Add Divisions(면, Exponentially): 각 면을 엣지 중점 + 면 중심으로 나눈다. Quads = 코너마다 쿼드(n개), Triangles = 코너·중점마다 삼각형(2n개).
    /// levels만큼 반복. 반환값은 결과 면 ID들.
    /// </summary>
    public static List<int> AddDivisions(PolyMesh m, IEnumerable<int> faceIds, int levels, DivisionMode mode)
    {
        var current = new List<int>(faceIds.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive));
        levels = Math.Clamp(levels, 1, 4);
        for (int l = 0; l < levels; l++) current = SubdivideOnce(m, current, mode);
        return current;
    }

    private static List<int> SubdivideOnce(PolyMesh m, List<int> faces, DivisionMode mode)
    {
        var result = new List<int>();
        if (faces.Count == 0) return result;
        var set = new HashSet<int>(faces);
        var rb = new FaceRebuilder(m);
        foreach (int f in faces) rb.Capture(f);
        // 영역 밖 이웃 면도 엣지 중점을 끼워야 하므로 캡처
        var neighbors = new List<int>();
        foreach (int f in faces)
        {
            var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes) { int tw = m.Hes[he].Twin; if (tw >= 0 && !set.Contains(m.Hes[tw].Face) && !neighbors.Contains(m.Hes[tw].Face)) neighbors.Add(m.Hes[tw].Face); }
        }
        foreach (int f in neighbors) rb.Capture(f);
        // 엣지 중점(영역 면의 엣지만)
        var mid = new Dictionary<long, int>();
        long K(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        foreach (int f in faces)
        {
            var corners = CaptureCorners(m, f);
            for (int i = 0; i < corners.Count; i++)
            {
                int a = corners[i].Vertex, b = corners[(i + 1) % corners.Count].Vertex;
                if (mid.ContainsKey(K(a, b))) continue;
                int v = m.AddVertex((m.Verts[a].Position + m.Verts[b].Position) * 0.5f);
                mid[K(a, b)] = v; rb.SetParent(v, a, b);
            }
        }
        rb.RemoveCaptured();
        foreach (var (f, corners, material) in rb.Captured)
        {
            int n = corners.Count;
            if (!set.Contains(f))
            {
                // 이웃 면: 중점만 끼운다
                var loop = new List<Corner>();
                for (int i = 0; i < n; i++)
                {
                    var c = corners[i]; var d = corners[(i + 1) % n];
                    loop.Add(c);
                    if (mid.TryGetValue(K(c.Vertex, d.Vertex), out int mv)) loop.Add(new Corner(mv, (c.Uv + d.Uv) * 0.5f, c.Normal));
                }
                rb.AddFace(loop, material);
                continue;
            }
            var center = Vector3.Zero; var uvC = Vector2.Zero;
            foreach (var c in corners) { center += m.Verts[c.Vertex].Position; uvC += c.Uv; }
            center /= n; uvC /= n;
            int cv = m.AddVertex(center);
            for (int i = 0; i < n; i++)
            {
                var c = corners[i]; var next = corners[(i + 1) % n]; var prev = corners[(i + n - 1) % n];
                int mOut = mid[K(c.Vertex, next.Vertex)], mIn = mid[K(prev.Vertex, c.Vertex)];
                var uvOut = (c.Uv + next.Uv) * 0.5f; var uvIn = (prev.Uv + c.Uv) * 0.5f;
                if (mode == DivisionMode.Quads)
                {
                    int nf = rb.AddFace(new[] { c, new Corner(mOut, uvOut, c.Normal), new Corner(cv, uvC, c.Normal), new Corner(mIn, uvIn, c.Normal) }, material);
                    if (nf >= 0) result.Add(nf);
                }
                else
                {
                    int t1 = rb.AddFace(new[] { c, new Corner(mOut, uvOut, c.Normal), new Corner(cv, uvC, c.Normal) }, material);
                    int t2 = rb.AddFace(new[] { new Corner(mIn, uvIn, c.Normal), c, new Corner(cv, uvC, c.Normal) }, material);
                    if (t1 >= 0) result.Add(t1); if (t2 >= 0) result.Add(t2);
                }
            }
        }
        m.BumpTopology();
        return result;
    }

    /// <summary>Add Divisions(면, Linearly): 쿼드 면을 u×v 격자로 나눈다. 쿼드가 아니면 Exponential 1단계로 대신한다.</summary>
    public static List<int> AddDivisionsLinear(PolyMesh m, IEnumerable<int> faceIds, int divU, int divV)
    {
        divU = Math.Clamp(divU, 1, 32); divV = Math.Clamp(divV, 1, 32);
        var result = new List<int>();
        var faces = faceIds.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        var nonQuad = faces.Where(f => m.FaceDegree(f) != 4).ToList();
        var quads = faces.Where(f => m.FaceDegree(f) == 4).ToList();
        if (nonQuad.Count > 0) result.AddRange(AddDivisions(m, nonQuad, 1, DivisionMode.Quads));
        if (quads.Count == 0) return result;
        var set = new HashSet<int>(quads);
        var rb = new FaceRebuilder(m);
        foreach (int f in quads) rb.Capture(f);
        var neighbors = new List<int>();
        foreach (int f in quads)
        {
            var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes) { int tw = m.Hes[he].Twin; if (tw >= 0 && !set.Contains(m.Hes[tw].Face) && !neighbors.Contains(m.Hes[tw].Face)) neighbors.Add(m.Hes[tw].Face); }
        }
        foreach (int f in neighbors) rb.Capture(f);
        // 엣지별 분할 정점(방향: 정점 쌍 키, 작은 ID → 큰 ID 순서로 저장)
        var edgePts = new Dictionary<long, int[]>();
        long K(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        int[] PtsFromTo(int a, int b, int divisions)
        {
            long k = K(a, b);
            if (!edgePts.TryGetValue(k, out var pts))
            {
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                pts = new int[divisions - 1];
                for (int i = 1; i < divisions; i++) { pts[i - 1] = m.AddVertex(Vector3.Lerp(m.Verts[lo].Position, m.Verts[hi].Position, (float)i / divisions)); rb.SetParent(pts[i - 1], a, b); }
                edgePts[k] = pts;
            }
            if (pts.Length != divisions - 1) return Array.Empty<int>(); // 분할 수 충돌(이웃 쿼드의 U/V가 다름): 사용 안 함
            return a < b ? pts : pts.Reverse().ToArray();
        }
        // 1차: 모든 쿼드의 엣지 분할점 생성(코너 0→1, 2→3 = U, 1→2, 3→0 = V)
        foreach (var (f, corners, _) in rb.Captured.Where(c => set.Contains(c.face)))
        {
            PtsFromTo(corners[0].Vertex, corners[1].Vertex, divU); PtsFromTo(corners[2].Vertex, corners[3].Vertex, divU);
            PtsFromTo(corners[1].Vertex, corners[2].Vertex, divV); PtsFromTo(corners[3].Vertex, corners[0].Vertex, divV);
        }
        rb.RemoveCaptured();
        foreach (var (f, corners, material) in rb.Captured)
        {
            int n = corners.Count;
            if (!set.Contains(f))
            {
                var loop = new List<Corner>();
                for (int i = 0; i < n; i++)
                {
                    var c = corners[i]; var d = corners[(i + 1) % n];
                    loop.Add(c);
                    if (edgePts.TryGetValue(K(c.Vertex, d.Vertex), out var pts))
                    {
                        var ordered = c.Vertex < d.Vertex ? pts : pts.Reverse().ToArray();
                        for (int k = 0; k < ordered.Length; k++) loop.Add(new Corner(ordered[k], Vector2.Lerp(c.Uv, d.Uv, (k + 1f) / (ordered.Length + 1)), c.Normal));
                    }
                }
                rb.AddFace(loop, material);
                continue;
            }
            // 격자 정점 (i: 0..divU, j: 0..divV); 코너 0=(0,0) 1=(U,0) 2=(U,V) 3=(0,V)
            var grid = new int[divU + 1, divV + 1];
            var uv = new Vector2[divU + 1, divV + 1];
            var p0 = m.Verts[corners[0].Vertex].Position; var p1 = m.Verts[corners[1].Vertex].Position; var p2 = m.Verts[corners[2].Vertex].Position; var p3 = m.Verts[corners[3].Vertex].Position;
            var bottom = PtsFromTo(corners[0].Vertex, corners[1].Vertex, divU); var top = PtsFromTo(corners[3].Vertex, corners[2].Vertex, divU);
            var left = PtsFromTo(corners[0].Vertex, corners[3].Vertex, divV); var right = PtsFromTo(corners[1].Vertex, corners[2].Vertex, divV);
            for (int i = 0; i <= divU; i++)
                for (int j = 0; j <= divV; j++)
                {
                    float s = (float)i / divU, t = (float)j / divV;
                    uv[i, j] = Vector2.Lerp(Vector2.Lerp(corners[0].Uv, corners[1].Uv, s), Vector2.Lerp(corners[3].Uv, corners[2].Uv, s), t);
                    if (i == 0 && j == 0) grid[i, j] = corners[0].Vertex;
                    else if (i == divU && j == 0) grid[i, j] = corners[1].Vertex;
                    else if (i == divU && j == divV) grid[i, j] = corners[2].Vertex;
                    else if (i == 0 && j == divV) grid[i, j] = corners[3].Vertex;
                    else if (j == 0 && bottom.Length == divU - 1) grid[i, j] = bottom[i - 1];
                    else if (j == divV && top.Length == divU - 1) grid[i, j] = top[i - 1];
                    else if (i == 0 && left.Length == divV - 1) grid[i, j] = left[j - 1];
                    else if (i == divU && right.Length == divV - 1) grid[i, j] = right[j - 1];
                    else grid[i, j] = m.AddVertex(Vector3.Lerp(Vector3.Lerp(p0, p1, s), Vector3.Lerp(p3, p2, s), t));
                }
            for (int i = 0; i < divU; i++)
                for (int j = 0; j < divV; j++)
                {
                    int nf = rb.AddFace(new[]
                    {
                        new Corner(grid[i, j], uv[i, j], corners[0].Normal), new Corner(grid[i + 1, j], uv[i + 1, j], corners[0].Normal),
                        new Corner(grid[i + 1, j + 1], uv[i + 1, j + 1], corners[0].Normal), new Corner(grid[i, j + 1], uv[i, j + 1], corners[0].Normal),
                    }, material);
                    if (nf >= 0) result.Add(nf);
                }
        }
        m.BumpTopology();
        return result;
    }

    /// <summary>Add Divisions(엣지): 각 엣지에 정점 levels개를 균등하게 끼운다. 반환값은 새 정점들.</summary>
    public static List<int> DivideEdges(PolyMesh m, IEnumerable<int> edgeIds, int levels)
    {
        levels = Math.Clamp(levels, 1, 32);
        var result = new List<int>();
        foreach (int e in edgeIds.ToArray())
        {
            if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive) continue;
            var (a, b) = m.EdgeVertices(e);
            // 뒤에서부터 나눠야 앞쪽 t가 유지된다: b쪽부터
            int cur = e; int curA = a;
            for (int i = 1; i <= levels; i++)
            {
                float t = 1f / (levels - i + 2);
                // cur 엣지는 (curA, b): curA 쪽에서 t
                var (x, y) = m.EdgeVertices(cur);
                float tt = x == curA ? t : 1f - t;
                int nv = SplitEdge(m, cur, tt);
                if (nv < 0) break;
                result.Add(nv);
                curA = nv;
                cur = m.FindEdge(nv, b);
                if (cur < 0) break;
            }
        }
        return result;
    }
}
