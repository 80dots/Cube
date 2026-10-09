using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Boolean 연산 종류(Maya Mesh → Booleans / Blender Boolean).</summary>
public enum BooleanOperation
{
    /// <summary>합집합: 두 메시를 하나로 합치고 안쪽에 묻힌 부분을 지운다.</summary>
    Union,
    /// <summary>차집합: A에서 B와 겹치는 부분을 파낸다.</summary>
    Difference,
    /// <summary>교집합: 둘이 겹치는 부분만 남긴다.</summary>
    Intersection,
}

/// <summary>Boolean 결과 요약(경고 표시용).</summary>
public sealed class BooleanReport
{
    /// <summary>A/B 입력이 닫힌 메시였는지(열린 메시는 안팎 판정이 정의되지 않아 결과가 이상할 수 있다).</summary>
    public bool ClosedA = true, ClosedB = true;
    /// <summary>결과 조각 중 하프에지 메시에 넣지 못한(비매니폴드) 면 수.</summary>
    public int DroppedFaces;
    /// <summary>결과의 구멍을 메운 수(BSP 허용 오차로 아주 가는 조각이 사라지거나 비매니폴드 조각을 버려 생긴 틈; 입력이 닫혀 있을 때만).</summary>
    public int FilledHoles;
    /// <summary>결과 면 수.</summary>
    public int Faces;
}

/// <summary>
/// 메시 Boolean(v0.0.56). BSP 트리 CSG(csg.js 알고리즘: 각 메시의 면 평면으로 BSP를 만들고 서로 잘라 안/밖 조각을 고른다)로 다각형 조각을 얻은 뒤
/// ① 정점 용접 ② T-접합 제거(다른 조각의 정점이 놓인 변에 그 정점을 끼움) ③ 하프에지 메시로 조립 ④ 같은 원본 면에서 나온 이웃 조각 병합 + 직선 위 2가 정점 정리
/// ⑤ 하드 엣지 복원(두 입력의 경계 = 하드, 같은 입력은 원래 엣지 플래그, 원래 이웃이 아니면 각도 30°)으로 정리한다. 코너 UV는 분할 시 선형 보간된다.
/// </summary>
public static partial class MeshOps
{
    /// <summary>CSG 정점: 위치 + 코너 UV.</summary>
    private struct BVert
    {
        public Vector3 P; public Vector2 Uv;
        public BVert(Vector3 p, Vector2 uv) { P = p; Uv = uv; }
        public static BVert Lerp(BVert a, BVert b, float t) => new(Vector3.Lerp(a.P, b.P, t), Vector2.Lerp(a.Uv, b.Uv, t));
    }

    /// <summary>CSG 다각형(볼록 가정): 정점, 평면(N·x = W), 출처(입력 0/1, 원본 면 ID, 뒤집힘), 머티리얼.</summary>
    private sealed class BPoly
    {
        public List<BVert> V = new();
        public Vector3 N; public float W;
        public int Op, Src, Mat; public bool Flip;
        public BPoly Clone() => new() { V = new List<BVert>(V), N = N, W = W, Op = Op, Src = Src, Mat = Mat, Flip = Flip };
        public void Invert() { V.Reverse(); N = -N; W = -W; Flip = !Flip; }
    }

    /// <summary>BSP 노드(csg.js Node). 자식/평면이 없으면 빈 노드.</summary>
    private sealed class BNode
    {
        public bool HasPlane; public Vector3 N; public float W;
        public BNode? Front, Back;
        public List<BPoly> Polys = new();
    }

    private const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

    /// <summary>
    /// 평면(n, w)으로 다각형을 분류·분할한다(csg.js splitPolygon). 같은 평면 = 방향에 따라 coFront/coBack, 걸치면 둘로 나눈다.
    /// </summary>
    private static void Split(Vector3 n, float w, float eps, BPoly p, List<BPoly> coFront, List<BPoly> coBack, List<BPoly> front, List<BPoly> back)
    {
        int type = 0;
        Span<int> types = p.V.Count <= 64 ? stackalloc int[p.V.Count] : new int[p.V.Count];
        for (int i = 0; i < p.V.Count; i++)
        {
            float t = Vector3.Dot(n, p.V[i].P) - w;
            int ty = t < -eps ? Back : t > eps ? Front : Coplanar;
            type |= ty; types[i] = ty;
        }
        switch (type)
        {
            case Coplanar: (Vector3.Dot(n, p.N) > 0 ? coFront : coBack).Add(p); break;
            case Front: front.Add(p); break;
            case Back: back.Add(p); break;
            default:
                {
                    var f = new List<BVert>(); var b = new List<BVert>();
                    for (int i = 0; i < p.V.Count; i++)
                    {
                        int j = (i + 1) % p.V.Count;
                        int ti = types[i], tj = types[j];
                        var vi = p.V[i]; var vj = p.V[j];
                        if (ti != Back) f.Add(vi);
                        if (ti != Front) b.Add(vi);
                        if ((ti | tj) == Spanning)
                        {
                            float t = (w - Vector3.Dot(n, vi.P)) / Vector3.Dot(n, vj.P - vi.P);
                            var v = BVert.Lerp(vi, vj, t);
                            f.Add(v); b.Add(v);
                        }
                    }
                    if (f.Count >= 3) front.Add(new BPoly { V = f, N = p.N, W = p.W, Op = p.Op, Src = p.Src, Mat = p.Mat, Flip = p.Flip });
                    if (b.Count >= 3) back.Add(new BPoly { V = b, N = p.N, W = p.W, Op = p.Op, Src = p.Src, Mat = p.Mat, Flip = p.Flip });
                    break;
                }
        }
    }

    /// <summary>BSP 구성(csg.js build; 깊은 트리에서도 스택이 넘치지 않도록 반복문).</summary>
    private static void Build(BNode root, List<BPoly> polys, float eps)
    {
        var stack = new Stack<(BNode, List<BPoly>)>();
        stack.Push((root, polys));
        while (stack.Count > 0)
        {
            var (node, ps) = stack.Pop();
            if (ps.Count == 0) continue;
            if (!node.HasPlane) { node.HasPlane = true; node.N = ps[0].N; node.W = ps[0].W; }
            var f = new List<BPoly>(); var b = new List<BPoly>();
            foreach (var p in ps) Split(node.N, node.W, eps, p, node.Polys, node.Polys, f, b);
            if (f.Count > 0) { node.Front ??= new BNode(); stack.Push((node.Front, f)); }
            if (b.Count > 0) { node.Back ??= new BNode(); stack.Push((node.Back, b)); }
        }
    }

    /// <summary>다각형들을 BSP로 잘라 트리 바깥(앞쪽 빈 공간)에 있는 조각만 남긴다(csg.js clipPolygons; 뒤쪽 빈 잎 = 안쪽 = 버림).</summary>
    private static List<BPoly> ClipPolygons(BNode root, List<BPoly> polys, float eps)
    {
        var result = new List<BPoly>();
        var stack = new Stack<(BNode, List<BPoly>)>();
        stack.Push((root, polys));
        while (stack.Count > 0)
        {
            var (node, ps) = stack.Pop();
            if (!node.HasPlane) { result.AddRange(ps); continue; }
            var f = new List<BPoly>(); var b = new List<BPoly>();
            foreach (var p in ps) Split(node.N, node.W, eps, p, f, b, f, b);
            if (node.Front != null) stack.Push((node.Front, f)); else result.AddRange(f);
            if (node.Back != null) stack.Push((node.Back, b));
        }
        return result;
    }

    /// <summary>트리의 모든 노드(전위 순회, 반복문).</summary>
    private static IEnumerable<BNode> Nodes(BNode root)
    {
        var stack = new Stack<BNode>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop(); yield return n;
            if (n.Front != null) stack.Push(n.Front);
            if (n.Back != null) stack.Push(n.Back);
        }
    }

    /// <summary>a의 모든 노드 다각형을 b로 잘라낸다(csg.js clipTo).</summary>
    private static void ClipTo(BNode a, BNode b, float eps) { foreach (var n in Nodes(a).ToList()) n.Polys = ClipPolygons(b, n.Polys, eps); }

    /// <summary>안팎 뒤집기(csg.js invert): 다각형·평면을 뒤집고 앞/뒤 자식을 바꾼다.</summary>
    private static void Invert(BNode root)
    {
        foreach (var n in Nodes(root).ToList())
        {
            foreach (var p in n.Polys) p.Invert();
            n.N = -n.N; n.W = -n.W;
            (n.Front, n.Back) = (n.Back, n.Front);
        }
    }

    /// <summary>트리의 모든 다각형.</summary>
    private static List<BPoly> AllPolygons(BNode root) => Nodes(root).SelectMany(n => n.Polys).ToList();

    /// <summary>메시 면 → CSG 다각형(변환 적용, 반사 행렬이면 감김 뒤집기). 볼록이 아닌 면은 삼각분할한다.</summary>
    private static List<BPoly> ToPolys(PolyMesh m, Matrix4x4 xf, int op)
    {
        var result = new List<BPoly>();
        bool flip = Det3(xf) < 0;
        var tmp = new List<int>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive) continue;
            var corners = CaptureCorners(m, f);
            var vs = corners.Select(c => new BVert(Vector3.Transform(m.Verts[c.Vertex].Position, xf), c.Uv)).ToList();
            if (flip) vs.Reverse();
            // 면 법선(뉴웰)과 볼록성 검사: 오목/비평면 면은 삼각형으로 나눠 넣는다(BSP 분할은 볼록 다각형을 가정)
            var nrm = Newell(vs);
            if (nrm.LengthSquared() < 1e-24f) continue;
            nrm = Vector3.Normalize(nrm);
            if (IsConvexPlanar(vs, nrm)) result.Add(MakePoly(vs, nrm, op, f, m.Faces[f].Material));
            else
            {
                EarClipping.PlaneBasis(nrm, out var bu, out var bw);
                var pts = vs.Select(v => new Vector2(Vector3.Dot(v.P, bu), Vector3.Dot(v.P, bw))).ToArray();
                var idx = new List<int>();
                EarClipping.Triangulate(pts, idx);
                for (int k = 0; k + 2 < idx.Count; k += 3)
                {
                    var tri = new List<BVert> { vs[idx[k]], vs[idx[k + 1]], vs[idx[k + 2]] };
                    if (Vector3.Dot(Vector3.Cross(tri[1].P - tri[0].P, tri[2].P - tri[0].P), nrm) < 0) tri.Reverse();
                    var tn = Vector3.Cross(tri[1].P - tri[0].P, tri[2].P - tri[0].P);
                    if (tn.LengthSquared() < 1e-24f) continue;
                    result.Add(MakePoly(tri, Vector3.Normalize(tn), op, f, m.Faces[f].Material));
                }
            }
        }
        return result;
    }

    private static BPoly MakePoly(List<BVert> vs, Vector3 n, int op, int src, int mat)
    {
        // 평면 거리는 정점 평균으로(비평면 오차를 고르게)
        float w = 0; foreach (var v in vs) w += Vector3.Dot(n, v.P); w /= vs.Count;
        return new BPoly { V = vs, N = n, W = w, Op = op, Src = src, Mat = mat };
    }

    private static Vector3 Newell(List<BVert> vs)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < vs.Count; i++) { var a = vs[i].P; var b = vs[(i + 1) % vs.Count].P; n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)); }
        return n;
    }

    private static bool IsConvexPlanar(List<BVert> vs, Vector3 n)
    {
        if (vs.Count == 3) return true;
        float w = Vector3.Dot(n, vs[0].P);
        float size = 0; foreach (var v in vs) size = MathF.Max(size, (v.P - vs[0].P).Length());
        foreach (var v in vs) if (MathF.Abs(Vector3.Dot(n, v.P) - w) > 1e-4f * MathF.Max(size, 1e-3f)) return false;
        for (int i = 0; i < vs.Count; i++)
        {
            var a = vs[i].P; var b = vs[(i + 1) % vs.Count].P; var c = vs[(i + 2) % vs.Count].P;
            if (Vector3.Dot(Vector3.Cross(b - a, c - b), n) < -1e-9f) return false;
        }
        return true;
    }

    /// <summary>모든 엣지에 면이 두 개인지(닫힌 메시).</summary>
    public static bool IsClosed(PolyMesh m)
    {
        bool any = false;
        foreach (var e in m.Edges) { if (!e.Alive) continue; any = true; if (e.He1 < 0) return false; }
        return any;
    }

    /// <summary>
    /// Boolean: A(결과 좌표계) op B(<paramref name="bToA"/>로 A 좌표계로 옮김) 결과 메시를 새로 만든다. 노멀 재계산은 호출자 몫.
    /// 머티리얼 인덱스는 각 면의 원래 값, UV는 원래 코너 UV(분할 지점은 보간).
    /// </summary>
    public static PolyMesh Boolean(PolyMesh a, PolyMesh b, Matrix4x4 bToA, BooleanOperation op, out BooleanReport report)
    {
        report = new BooleanReport { ClosedA = IsClosed(a), ClosedB = IsClosed(b) };
        var pa = ToPolys(a, Matrix4x4.Identity, 0);
        var pb = ToPolys(b, bToA, 1);
        // 허용 오차: 두 메시를 합친 크기에 비례
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var p in pa.Concat(pb)) foreach (var v in p.V) { min = Vector3.Min(min, v.P); max = Vector3.Max(max, v.P); }
        float extent = pa.Count + pb.Count == 0 ? 1f : MathF.Max((max - min).Length(), 1e-3f);
        float eps = 1e-5f * MathF.Max(extent, 1f);

        var na = new BNode(); Build(na, pa, eps);
        var nb = new BNode(); Build(nb, pb, eps);
        switch (op)
        {
            case BooleanOperation.Union:
                ClipTo(na, nb, eps); ClipTo(nb, na, eps); Invert(nb); ClipTo(nb, na, eps); Invert(nb);
                Build(na, AllPolygons(nb), eps);
                break;
            case BooleanOperation.Difference:
                Invert(na); ClipTo(na, nb, eps); ClipTo(nb, na, eps); Invert(nb); ClipTo(nb, na, eps); Invert(nb);
                Build(na, AllPolygons(nb), eps); Invert(na);
                break;
            default:
                Invert(na); ClipTo(nb, na, eps); Invert(nb); ClipTo(na, nb, eps); ClipTo(nb, na, eps);
                Build(na, AllPolygons(nb), eps); Invert(na);
                break;
        }
        var result = PolysToMesh(AllPolygons(na), a, b, eps * 10, report);
        report.Faces = result.AliveFaceCount;
        return result;
    }

    /// <summary>
    /// CSG 조각 → 하프에지 메시: 용접 → T-접합 제거 → 면 추가 → 같은 원본 면 조각 병합/2가 정점 정리(반복) → 하드 엣지 → 머티리얼 복원.
    /// 조립하는 동안 Face.Material에는 출처 표(infos) 인덱스를 넣어 병합·재생성을 거쳐도 출처를 잃지 않게 한다.
    /// </summary>
    private static PolyMesh PolysToMesh(List<BPoly> polys, PolyMesh a, PolyMesh b, float tol, BooleanReport report)
    {
        var m = new PolyMesh();
        // ① 용접: 셀(크기 tol) 해시 + 이웃 27칸 검색
        var pts = new List<Vector3>();
        var grid = new Dictionary<(long, long, long), List<int>>();
        (long, long, long) Cell(Vector3 p, float s) => ((long)MathF.Floor(p.X / s), (long)MathF.Floor(p.Y / s), (long)MathF.Floor(p.Z / s));
        int Weld(Vector3 p)
        {
            var (cx, cy, cz) = Cell(p, tol);
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) foreach (int i in l) if (Vector3.DistanceSquared(pts[i], p) <= tol * tol) return i;
            pts.Add(p);
            if (!grid.TryGetValue((cx, cy, cz), out var list)) grid[(cx, cy, cz)] = list = new List<int>();
            list.Add(pts.Count - 1);
            return pts.Count - 1;
        }
        var loops = new List<(List<(int v, Vector2 uv)> loop, BPoly src)>();
        foreach (var p in polys)
        {
            var loop = new List<(int, Vector2)>();
            foreach (var v in p.V)
            {
                int id = Weld(v.P);
                if (loop.Count > 0 && loop[^1].Item1 == id) continue;
                loop.Add((id, v.Uv));
            }
            while (loop.Count > 1 && loop[0].Item1 == loop[^1].Item1) loop.RemoveAt(loop.Count - 1);
            if (loop.Count >= 3) loops.Add((loop, p));
        }

        // ② T-접합: 변 위에 놓인 다른 정점을 그 변에 끼운다(공간 해시, 셀 = 크기/32)
        var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
        foreach (var q in pts) { bmin = Vector3.Min(bmin, q); bmax = Vector3.Max(bmax, q); }
        float cell = MathF.Max(MathF.Max(bmax.X - bmin.X, MathF.Max(bmax.Y - bmin.Y, bmax.Z - bmin.Z)) / 32f, tol * 4);
        var vgrid = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < pts.Count; i++) { var c = Cell(pts[i], cell); if (!vgrid.TryGetValue(c, out var l)) vgrid[c] = l = new List<int>(); l.Add(i); }
        for (int li = 0; li < loops.Count; li++)
        {
            var (loop, src) = loops[li];
            var outLoop = new List<(int, Vector2)>();
            for (int i = 0; i < loop.Count; i++)
            {
                var (va, uva) = loop[i]; var (vb, uvb) = loop[(i + 1) % loop.Count];
                outLoop.Add((va, uva));
                var pa = pts[va]; var d = pts[vb] - pa; float len2 = d.LengthSquared();
                if (len2 < tol * tol) continue;
                var c0 = Cell(Vector3.Min(pa, pts[vb]) - new Vector3(tol), cell); var c1 = Cell(Vector3.Max(pa, pts[vb]) + new Vector3(tol), cell);
                var on = new List<(float t, int v)>();
                for (long x = c0.Item1; x <= c1.Item1; x++) for (long y = c0.Item2; y <= c1.Item2; y++) for (long z = c0.Item3; z <= c1.Item3; z++)
                {
                    if (!vgrid.TryGetValue((x, y, z), out var l)) continue;
                    foreach (int c in l)
                    {
                        if (c == va || c == vb) continue;
                        float t = Vector3.Dot(pts[c] - pa, d) / len2;
                        if (t <= 1e-6f || t >= 1 - 1e-6f) continue;
                        if (Vector3.DistanceSquared(pa + d * t, pts[c]) > tol * tol) continue;
                        on.Add((t, c));
                    }
                }
                foreach (var (t, c) in on.OrderBy(x => x.t)) outLoop.Add((c, Vector2.Lerp(uva, uvb, t)));
            }
            loops[li] = (outLoop, src);
        }

        // ③ 면 추가: 출처 표 인덱스를 Material에
        foreach (var q in pts) m.AddVertex(q);
        var infos = new List<(int op, int src, bool flip, int mat)>();
        var infoIndex = new Dictionary<(int, int, bool, int), int>();
        foreach (var (rawLoop, src) in loops)
        {
            var key = (src.Op, src.Src, src.Flip, src.Mat);
            if (!infoIndex.TryGetValue(key, out int ii)) { ii = infos.Count; infos.Add(key); infoIndex[key] = ii; }
            // 용접·T-접합으로 루프가 자기 자신과 맞닿으면(가는 조각) 단순 루프들로 나눈다
            foreach (var loop in SimpleLoops(rawLoop))
            {
                if (LoopArea(loop, pts, src.N) <= tol * tol * 0.01f) continue; // 넓이 0 조각(일직선)
                var corners = loop.Select(x => new Corner(x.v, x.uv, src.N)).ToList();
                if (AddFaceWithCorners(m, corners, ii) < 0) report.DroppedFaces++;
            }
        }

        // ④ 같은 출처 조각 병합 + 직선 위 2가 정점 정리(변화가 없을 때까지)
        for (int round = 0; round < 8; round++)
        {
            bool changed = false;
            for (int e = 0; e < m.Edges.Count; e++)
            {
                var ed = m.Edges[e];
                if (!ed.Alive || ed.He1 < 0) continue;
                int f0 = m.Hes[ed.He0].Face, f1 = m.Hes[ed.He1].Face;
                if (f0 == f1 || m.Faces[f0].Material != m.Faces[f1].Material) continue;
                if (MergeFragments(m, e)) changed = true;
            }
            for (int v = 0; v < m.Verts.Count; v++)
            {
                if (!m.Verts[v].Alive) continue;
                var es = new List<int>(); m.GetVertexEdges(v, es);
                if (es.Count != 2) continue;
                // 일직선 위의 정점만 녹인다(꺾인 모서리에서 엣지 2개가 만나는 경우 — 맞닿은 두 L자 면 — 를 지우면 형태가 깎인다)
                var (a0, a1) = m.EdgeVertices(es[0]); var (b0, b1) = m.EdgeVertices(es[1]);
                var p = m.Verts[v].Position;
                var d0 = m.Verts[a0 == v ? a1 : a0].Position - p; var d1 = m.Verts[b0 == v ? b1 : b0].Position - p;
                if (d0.LengthSquared() < 1e-20f || d1.LengthSquared() < 1e-20f) continue;
                if (Vector3.Dot(Vector3.Normalize(d0), Vector3.Normalize(d1)) > -0.99999f) continue;
                if (DissolveIfValence2(m, v)) changed = true;
            }
            if (!changed) break;
        }

        // ④-2 두 입력이 닫혀 있으면 결과도 닫혀야 한다: 남은 틈(사라진 가는 조각·버린 조각)을 n각형으로 메우고 이웃 면의 출처를 물려준다
        if (report.ClosedA && report.ClosedB)
        {
            var open = new List<int>();
            for (int e = 0; e < m.Edges.Count; e++) if (m.Edges[e].Alive && m.Edges[e].He1 < 0) open.Add(e);
            if (open.Count > 0)
            {
                // 채우기 전 이웃 면 출처를 경계 정점 쌍으로 기억
                var newFaces = FillHoles(m, open);
                foreach (int nf in newFaces)
                {
                    int start = m.Faces[nf].HalfEdge, he = start, mat = -1;
                    do { int tw = m.Hes[he].Twin; if (tw >= 0 && m.Hes[tw].Face != nf) { mat = m.Faces[m.Hes[tw].Face].Material; break; } he = m.Hes[he].Next; } while (he != start);
                    var fc = m.Faces[nf]; fc.Material = mat >= 0 ? mat : 0; m.Faces[nf] = fc;
                }
                report.FilledHoles = newFaces.Count;
            }
        }

        // ⑤ 하드 엣지: 서로 다른 입력의 경계 = 하드, 같은 입력의 다른 원본 면 사이 = 원래 엣지 플래그(원래 이웃이 아니면 30° 이상이면 하드)
        MeshNormals.Recompute(m);
        for (int e = 0; e < m.Edges.Count; e++)
        {
            var ed = m.Edges[e];
            if (!ed.Alive || ed.He1 < 0) continue;
            var i0 = infos[m.Faces[m.Hes[ed.He0].Face].Material]; var i1 = infos[m.Faces[m.Hes[ed.He1].Face].Material];
            bool hard, seam = false;
            if (i0.op != i1.op) hard = true;
            else if (i0.src == i1.src) hard = false;
            else
            {
                var src = i0.op == 0 ? a : b;
                int oe = SharedEdge(src, i0.src, i1.src);
                if (oe >= 0) { hard = src.Edges[oe].Hard; seam = src.Edges[oe].Seam; }
                else
                {
                    var n0 = m.Faces[m.Hes[ed.He0].Face].Normal; var n1 = m.Faces[m.Hes[ed.He1].Face].Normal;
                    hard = Vector3.Dot(n0, n1) < MathF.Cos(30f * MathF.PI / 180f);
                }
            }
            ed.Hard = hard; ed.Seam = seam; m.Edges[e] = ed;
        }
        // 원래 머티리얼 인덱스로 복원
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive) continue;
            var fc = m.Faces[f]; fc.Material = infos[fc.Material].mat; m.Faces[f] = fc;
        }
        m.BumpTopology();
        return m;
    }

    /// <summary>
    /// 엣지 e 양쪽의 같은 출처 조각 두 개를 하나로 합친다. 두 조각이 이어진 엣지 여러 개(꺾인 모서리 포함)를 공유하면
    /// 합친 루프에 x, y, x 꼴의 '가시'가 생기므로 가시 끝 y와 중복 x를 걷어낸다(남는 고립 정점은 삭제).
    /// 결과 루프에 정점이 반복되거나(구멍이 생기는 경우) 면 추가에 실패하면 원래 두 면을 되살리고 false.
    /// </summary>
    private static bool MergeFragments(PolyMesh m, int e)
    {
        var ed = m.Edges[e];
        int he0 = ed.He0, he1 = ed.He1;
        int f0 = m.Hes[he0].Face, f1 = m.Hes[he1].Face;
        if (f0 == f1) return false;
        var loop = new List<Corner>();
        int cur = m.Hes[he0].Next;
        while (cur != he0) { var h = m.Hes[cur]; loop.Add(new Corner(h.Vertex, h.Uv0, h.Normal)); cur = h.Next; }
        cur = m.Hes[he1].Next;
        while (cur != he1) { var h = m.Hes[cur]; loop.Add(new Corner(h.Vertex, h.Uv0, h.Normal)); cur = h.Next; }
        // 가시 제거: 순환 루프에서 앞뒤가 같은 정점이면 가운데(가시 끝)와 뒤 중복을 지운다
        for (bool again = true; again && loop.Count >= 3;)
        {
            again = false;
            for (int i = 0; i < loop.Count; i++)
            {
                int n = loop.Count, ip = (i - 1 + n) % n, inx = (i + 1) % n;
                if (loop[ip].Vertex != loop[inx].Vertex) continue;
                int hi = Math.Max(i, inx), lo = Math.Min(i, inx);
                loop.RemoveAt(hi); loop.RemoveAt(lo);
                again = true; break;
            }
        }
        if (loop.Count < 3 || loop.Select(c => c.Vertex).Distinct().Count() != loop.Count) return false;
        int mat0 = m.Faces[f0].Material, mat1 = m.Faces[f1].Material;
        var old0 = CaptureCorners(m, f0); var old1 = CaptureCorners(m, f1);
        m.RemoveFace(f0, removeIsolated: false); m.RemoveFace(f1, removeIsolated: false);
        if (AddFaceWithCorners(m, loop, mat0) < 0)
        {
            AddFaceWithCorners(m, old0, mat0); AddFaceWithCorners(m, old1, mat1);
            return false;
        }
        var kept = new HashSet<int>(loop.Select(c => c.Vertex));
        foreach (var c in old0.Concat(old1)) if (!kept.Contains(c.Vertex)) m.RemoveVertexIfIsolated(c.Vertex);
        return true;
    }

    /// <summary>
    /// 정점이 반복되는 루프를 단순 루프들로 나눈다: x, y, x 꼴 가시를 걷어내고, 그래도 같은 정점이 두 번 나오면 그 지점에서 두 루프로 자른다(재귀).
    /// 정점 3개 미만인 조각은 버린다.
    /// </summary>
    private static List<List<(int v, Vector2 uv)>> SimpleLoops(List<(int v, Vector2 uv)> loop)
    {
        var result = new List<List<(int v, Vector2 uv)>>();
        var work = new Stack<List<(int v, Vector2 uv)>>(); work.Push(new List<(int, Vector2)>(loop));
        while (work.Count > 0)
        {
            var l = work.Pop();
            for (bool again = true; again && l.Count >= 3;)
            {
                again = false;
                for (int i = 0; i < l.Count; i++)
                {
                    int n = l.Count, ip = (i - 1 + n) % n, inx = (i + 1) % n;
                    if (l[ip].v == l[i].v) { l.RemoveAt(i); again = true; break; }
                    if (l[ip].v != l[inx].v) continue;
                    int hi = Math.Max(i, inx), lo = Math.Min(i, inx);
                    l.RemoveAt(hi); l.RemoveAt(lo); again = true; break;
                }
            }
            if (l.Count < 3) continue;
            int si = -1, sj = -1;
            var seen = new Dictionary<int, int>();
            for (int i = 0; i < l.Count && si < 0; i++) { if (seen.TryGetValue(l[i].v, out int j)) { si = j; sj = i; } else seen[l[i].v] = i; }
            if (si < 0) { result.Add(l); continue; }
            work.Push(l.GetRange(si, sj - si));
            var rest = l.GetRange(sj, l.Count - sj); rest.AddRange(l.GetRange(0, si));
            work.Push(rest);
        }
        return result;
    }

    /// <summary>루프의 넓이(법선 방향 성분, 부호 있음 — 반대로 감긴 조각은 음수).</summary>
    private static float LoopArea(List<(int v, Vector2 uv)> loop, List<Vector3> pts, Vector3 n)
    {
        var sum = Vector3.Zero; var p0 = pts[loop[0].v];
        for (int i = 1; i + 1 < loop.Count; i++) sum += Vector3.Cross(pts[loop[i].v] - p0, pts[loop[i + 1].v] - p0);
        return Vector3.Dot(sum, n) * 0.5f;
    }

    /// <summary>원본 메시에서 두 면이 공유하는 엣지(없으면 -1).</summary>
    private static int SharedEdge(PolyMesh m, int f0, int f1)
    {
        if (f0 < 0 || f1 < 0 || f0 >= m.Faces.Count || f1 >= m.Faces.Count || !m.Faces[f0].Alive) return -1;
        int start = m.Faces[f0].HalfEdge, he = start;
        do
        {
            var h = m.Hes[he];
            if (h.Twin >= 0 && m.Hes[h.Twin].Face == f1) return h.Edge;
            he = h.Next;
        } while (he != start);
        return -1;
    }
}
