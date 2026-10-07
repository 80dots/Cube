using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>엣지 베벨(세그먼트 1 = 챔퍼, 2 이상 = 둥근 프로파일).</summary>
public static partial class MeshOps
{
    private sealed record BevelEntry(int Vertex, Vector2 Uv, Vector3 Normal, int OriginEdge, int OriginVertex);

    /// <summary>
    /// 선택 엣지를 베벨한다. 각 끝 정점에서 선택되지 않은 인접 엣지를 따라 distance만큼 물러난 새 정점을 만들고,
    /// 선택 엣지마다 쿼드 띠(segments개), 선택 엣지가 모이는 정점에는 캡 면을 만든다. 반환값은 새로 생긴 베벨 면 ID들.
    /// segments가 2 이상이면 한 끝의 두 오프셋 점(면 A의 p0, 면 B의 p1) 사이에 원호를 따라 segments-1개의 중간 점을 넣는다.
    /// 원호 중심은 p0에서 면 A 안쪽(-nA)으로, p1에서 면 B 안쪽(-nB)으로 뻗은 두 직선의 최근접점(두 면에 접하는 원; 거의 공면이면 선형 보간).
    /// </summary>
    public static List<int> BevelEdges(PolyMesh m, IEnumerable<int> edgeIds, float distance, int segments = 1)
    {
        var result = BevelEdgesCore(m, edgeIds, distance, segments);
        if (segments >= 2 && result.Count > 0) PostProcessRoundBevel(m, result);
        return result;
    }

    /// <summary>
    /// 둥근 Bevel(세그먼트 2+) 마무리(Maya와 같게):
    /// ① 끝 정점의 캡이 이웃한 평평한 면과 같은 평면이면(엣지 하나만 Bevel된 모서리) 그 면에 합쳐 원호 정점을 면 테두리로 흡수한다
    ///    — 별도의 얇은 D자 캡 면이 생겨 면이 잘게 쪼개져 보이지 않게.
    /// ② 스트립 사이/스트립과 이웃 면 사이 엣지를 30° 스무딩 각(60°)으로 소프트/하드 처리해 둥근 면이 부드럽게 음영된다.
    /// </summary>
    private static void PostProcessRoundBevel(PolyMesh m, List<int> result)
    {
        var newSet = new HashSet<int>(result);
        var hes = new List<int>();
        bool merged = true;
        while (merged)
        {
            merged = false;
            foreach (int f in result.ToArray())
            {
                if (f < 0 || f >= m.FaceCount || !m.Faces[f].Alive) { result.Remove(f); continue; }
                var nf = MeshNormals.FaceNormalUnnormalized(m, f);
                if (nf.LengthSquared() < 1e-20f) continue;
                nf = Vector3.Normalize(nf);
                m.GetFaceHalfEdges(f, hes);
                // 둥근 영역의 면(이웃한 새 면과 꺾여 있음)만 대상: 전부 평평한 경우(평면 위 Bevel)는 그대로 둔다
                bool curved = false;
                foreach (int he in hes)
                {
                    int tw = m.Hes[he].Twin; if (tw < 0) continue;
                    int g = m.Hes[tw].Face;
                    if (g < 0 || !newSet.Contains(g) || !m.Faces[g].Alive) continue;
                    var ng = MeshNormals.FaceNormalUnnormalized(m, g);
                    if (ng.LengthSquared() > 1e-20f && Vector3.Dot(nf, Vector3.Normalize(ng)) < 0.9999f) { curved = true; break; }
                }
                if (!curved) continue;
                foreach (int he in hes)
                {
                    int tw = m.Hes[he].Twin; if (tw < 0) continue;
                    int g = m.Hes[tw].Face;
                    if (g < 0 || newSet.Contains(g) || !m.Faces[g].Alive) continue;
                    var ng = MeshNormals.FaceNormalUnnormalized(m, g);
                    if (ng.LengthSquared() < 1e-20f || Vector3.Dot(nf, Vector3.Normalize(ng)) < 0.9999f) continue;
                    var (ok, _) = MergeFacesAcrossEdgeReturning(m, m.Hes[he].Edge);
                    if (ok) { result.Remove(f); merged = true; }
                    break;
                }
                if (merged) break;
            }
        }
        var edges = new HashSet<int>();
        foreach (int f in result)
        {
            if (!m.Faces[f].Alive) continue;
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes) edges.Add(m.Hes[he].Edge);
        }
        SoftenHardenByAngle(m, edges, 60f); // 세그먼트 사이(90°/s)와 끝 면 경계는 부드럽게, 원래의 직각 모서리는 하드
        m.BumpTopology();
    }

    private static List<int> BevelEdgesCore(PolyMesh m, IEnumerable<int> edgeIds, float distance, int segments)
    {
        var result = new List<int>();
        var selected = new HashSet<int>(edgeIds.Where(e => e >= 0 && e < m.EdgeCount && m.Edges[e].Alive && !m.IsBoundaryEdge(e)));
        if (selected.Count == 0) return result;
        distance = MathF.Max(distance, 1e-5f);
        segments = Math.Max(1, segments);

        var V = new HashSet<int>();
        foreach (int e in selected) { var (a, b) = m.EdgeVertices(e); V.Add(a); V.Add(b); }

        // 영향 면과 원래 하드 플래그, 면 법선(프로파일 원호용; 제거 전에 기록)
        var affected = new List<int>();
        var tmp = new List<int>();
        foreach (int v in V) { m.GetVertexFaces(v, tmp); foreach (int f in tmp) if (!affected.Contains(f)) affected.Add(f); }
        var hardOf = new Dictionary<int, bool>();
        var edgeVerts = new Dictionary<int, (int a, int b)>();
        var faceNormal = new Dictionary<int, Vector3>();
        foreach (int f in affected)
        {
            var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes) { int e = m.Hes[he].Edge; hardOf[e] = m.Edges[e].Hard; edgeVerts[e] = m.EdgeVertices(e); }
            var fn = MeshNormals.FaceNormalUnnormalized(m, f);
            faceNormal[f] = fn.LengthSquared() > 1e-18f ? Vector3.Normalize(fn) : Vector3.Zero;
        }
        // 선택 엣지의 면/방향(제거 전에 기록)
        var selInfo = new List<(int e, int f0, int f1, int a, int b)>();
        foreach (int e in selected)
        {
            var ed = m.Edges[e];
            int he0 = ed.He0, he1 = ed.He1;
            selInfo.Add((e, m.Hes[he0].Face, m.Hes[he1].Face, m.Hes[he0].Vertex, m.Hes[m.Hes[he0].Next].Vertex));
        }

        var pOnEdge = new Dictionary<(int v, int e), int>();
        var qOnFace = new Dictionary<(int v, int f), int>();
        var uvOf = new Dictionary<int, Vector2>();
        var side = new Dictionary<(int f, int e, int v), (int vert, Vector2 uv)>();
        var capEdges = new Dictionary<int, List<(int from, int to)>>();
        foreach (int v in V) capEdges[v] = new List<(int, int)>();

        int P(int v, int e, int other)
        {
            if (pOnEdge.TryGetValue((v, e), out int id)) return id;
            var pv = m.Verts[v].Position; var po = m.Verts[other].Position;
            float len = Vector3.Distance(pv, po);
            float d = MathF.Min(distance, len * 0.45f);
            id = m.AddVertex(len > 1e-9f ? pv + (po - pv) * (d / len) : pv);
            pOnEdge[(v, e)] = id;
            return id;
        }
        float Frac(int v, int other)
        {
            float len = Vector3.Distance(m.Verts[v].Position, m.Verts[other].Position);
            return len > 1e-9f ? MathF.Min(distance, len * 0.45f) / len : 0f;
        }

        var rebuilt = new List<(int face, List<BevelEntry> loop, int material)>();
        foreach (int f in affected)
        {
            var corners = CaptureCorners(m, f);
            int n = corners.Count;
            var loop = new List<BevelEntry>();
            for (int i = 0; i < n; i++)
            {
                var c = corners[i]; var prev = corners[(i + n - 1) % n]; var next = corners[(i + 1) % n];
                if (!V.Contains(c.Vertex)) { loop.Add(new BevelEntry(c.Vertex, c.Uv, c.Normal, -1, c.Vertex)); continue; }
                int ePrev = m.FindEdge(prev.Vertex, c.Vertex), eNext = m.FindEdge(c.Vertex, next.Vertex);
                bool sp = selected.Contains(ePrev), sn = selected.Contains(eNext);
                int first = loop.Count;
                if (!sp && !sn)
                {
                    int p1 = P(c.Vertex, ePrev, prev.Vertex), p2 = P(c.Vertex, eNext, next.Vertex);
                    var uv1 = Vector2.Lerp(c.Uv, prev.Uv, Frac(c.Vertex, prev.Vertex)); var uv2 = Vector2.Lerp(c.Uv, next.Uv, Frac(c.Vertex, next.Vertex));
                    loop.Add(new BevelEntry(p1, uv1, c.Normal, ePrev, c.Vertex)); loop.Add(new BevelEntry(p2, uv2, c.Normal, eNext, c.Vertex));
                    uvOf.TryAdd(p1, uv1); uvOf.TryAdd(p2, uv2);
                    capEdges[c.Vertex].Add((p2, p1));
                }
                else if (sp && !sn)
                {
                    int p2 = P(c.Vertex, eNext, next.Vertex);
                    var uv2 = Vector2.Lerp(c.Uv, next.Uv, Frac(c.Vertex, next.Vertex));
                    loop.Add(new BevelEntry(p2, uv2, c.Normal, eNext, c.Vertex)); uvOf.TryAdd(p2, uv2);
                }
                else if (!sp && sn)
                {
                    int p1 = P(c.Vertex, ePrev, prev.Vertex);
                    var uv1 = Vector2.Lerp(c.Uv, prev.Uv, Frac(c.Vertex, prev.Vertex));
                    loop.Add(new BevelEntry(p1, uv1, c.Normal, ePrev, c.Vertex)); uvOf.TryAdd(p1, uv1);
                }
                else
                {
                    if (!qOnFace.TryGetValue((c.Vertex, f), out int q))
                    {
                        var pc = m.Verts[c.Vertex].Position;
                        var dp = m.Verts[prev.Vertex].Position - pc; var dn = m.Verts[next.Vertex].Position - pc;
                        var pos = pc + dp * Frac(c.Vertex, prev.Vertex) + dn * Frac(c.Vertex, next.Vertex);
                        q = m.AddVertex(pos); qOnFace[(c.Vertex, f)] = q;
                    }
                    var uvq = c.Uv + (prev.Uv - c.Uv) * Frac(c.Vertex, prev.Vertex) + (next.Uv - c.Uv) * Frac(c.Vertex, next.Vertex);
                    loop.Add(new BevelEntry(q, uvq, c.Normal, -2, c.Vertex)); uvOf.TryAdd(q, uvq);
                }
                if (sp) side[(f, ePrev, c.Vertex)] = (loop[first].Vertex, loop[first].Uv);
                if (sn) side[(f, eNext, c.Vertex)] = (loop[^1].Vertex, loop[^1].Uv);
            }
            rebuilt.Add((f, loop, m.Faces[f].Material));
        }

        foreach (var (f, _, _) in rebuilt) m.RemoveFace(f, removeIsolated: false);

        bool HardBetween(BevelEntry x, BevelEntry y)
        {
            if (x.OriginEdge >= 0 && (y.OriginEdge == x.OriginEdge || (y.OriginEdge == -1 && edgeVerts.TryGetValue(x.OriginEdge, out var ev) && (ev.a == y.Vertex || ev.b == y.Vertex)))) return hardOf[x.OriginEdge];
            if (y.OriginEdge >= 0 && x.OriginEdge == -1 && edgeVerts.TryGetValue(y.OriginEdge, out var ev2) && (ev2.a == x.Vertex || ev2.b == x.Vertex)) return hardOf[y.OriginEdge];
            if (x.OriginEdge == -1 && y.OriginEdge == -1)
            {
                foreach (var (e, (a, b)) in edgeVerts) if ((a == x.Vertex && b == y.Vertex) || (a == y.Vertex && b == x.Vertex)) return hardOf[e];
            }
            return false;
        }

        foreach (var (_, loop, material) in rebuilt)
        {
            // 연속 중복 제거
            var clean = new List<BevelEntry>();
            foreach (var en in loop) if (clean.Count == 0 || clean[^1].Vertex != en.Vertex) clean.Add(en);
            if (clean.Count > 1 && clean[0].Vertex == clean[^1].Vertex) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 3) continue;
            int nf = AddFaceWithCorners(m, clean.Select(e => new Corner(e.Vertex, e.Uv, e.Normal)).ToList(), material);
            if (nf < 0) continue;
            for (int i = 0; i < clean.Count; i++) SetHard(m, clean[i].Vertex, clean[(i + 1) % clean.Count].Vertex, HardBetween(clean[i], clean[(i + 1) % clean.Count]));
        }

        // 한 끝의 프로파일: 면 f0의 오프셋 점 → (중간 점 segments-1개) → 면 f1의 오프셋 점. 정점 ID와 UV(프로파일을 따라 보간).
        (int[] verts, Vector2[] uvs) Profile((int vert, Vector2 uv) s0, int f0, (int vert, Vector2 uv) s1, int f1)
        {
            var verts = new int[segments + 1]; var uvs = new Vector2[segments + 1];
            verts[0] = s0.vert; uvs[0] = s0.uv; verts[segments] = s1.vert; uvs[segments] = s1.uv;
            if (segments == 1) return (verts, uvs);
            if (s0.vert == s1.vert)
            {
                for (int k = 1; k < segments; k++) { verts[k] = s0.vert; uvs[k] = s0.uv; }
                return (verts, uvs);
            }
            var pts = ArcPoints(m.Verts[s0.vert].Position, faceNormal[f0], m.Verts[s1.vert].Position, faceNormal[f1], segments);
            for (int k = 1; k < segments; k++)
            {
                var uv = Vector2.Lerp(s0.uv, s1.uv, (float)k / segments);
                verts[k] = m.AddVertex(pts[k - 1]); uvs[k] = uv; uvOf[verts[k]] = uv;
            }
            return (verts, uvs);
        }

        // 베벨 쿼드 띠: 엣지마다 segments개의 쿼드가 양끝 프로파일의 k번째 점끼리 잇는다
        foreach (var (e, f0, f1, a, b) in selInfo)
        {
            if (!side.TryGetValue((f0, e, a), out var a0) || !side.TryGetValue((f0, e, b), out var b0) ||
                !side.TryGetValue((f1, e, a), out var a1) || !side.TryGetValue((f1, e, b), out var b1)) continue;
            if (a0.vert == a1.vert && b0.vert == b1.vert) continue;
            var (pa, uva) = Profile(a0, f0, a1, f1);
            var (pb, uvb) = Profile(b0, f0, b1, f1);
            for (int k = 0; k < segments; k++)
            {
                var quad = new List<Corner> { new(pb[k], uvb[k], Vector3.Zero), new(pa[k], uva[k], Vector3.Zero), new(pa[k + 1], uva[k + 1], Vector3.Zero), new(pb[k + 1], uvb[k + 1], Vector3.Zero) };
                if (quad.Select(c => c.Vertex).Distinct().Count() < 3) continue;
                int q = AddFaceWithCorners(m, quad);
                if (q >= 0) result.Add(q);
            }
            for (int k = segments; k >= 1; k--) capEdges[a].Add((pa[k], pa[k - 1]));
            for (int k = 0; k < segments; k++) capEdges[b].Add((pb[k], pb[k + 1]));
        }

        // 정점 캡: 그 정점에 모이는 모든 프로파일 점을 순환 순서로 잇는 n각형
        foreach (int v in V)
        {
            var edges = capEdges[v];
            if (edges.Count < 3) continue;
            var next = new Dictionary<int, int>();
            bool bad = false;
            foreach (var (from, to) in edges) { if (from == to || !next.TryAdd(from, to)) { bad = true; break; } }
            if (bad) continue;
            var loop = new List<int>();
            int start = edges[0].from, cur = start;
            while (loop.Count <= edges.Count)
            {
                loop.Add(cur);
                if (!next.TryGetValue(cur, out cur)) { bad = true; break; }
                if (cur == start) break;
            }
            if (bad || cur != start || loop.Count < 3 || loop.Count != edges.Count) continue;
            var corners = loop.Select(id => new Corner(id, uvOf.TryGetValue(id, out var uv) ? uv : Vector2.Zero, Vector3.Zero)).ToList();
            int cf = AddFaceWithCorners(m, corners);
            if (cf >= 0) result.Add(cf);
        }

        foreach (int v in V) m.RemoveVertexIfIsolated(v);
        m.BumpTopology();
        return result;
    }

    /// <summary>
    /// p0(법선 n0인 면 위)과 p1(법선 n1인 면 위) 사이의 둥근 프로파일 중간 점 segments-1개.
    /// 중심은 p0 - s·n0, p1 - t·n1 두 직선의 최근접점(대칭이면 두 면에 접하는 원의 중심), 반지름은 두 끝 반지름을 선형 보간.
    /// 두 면이 거의 공면이거나 중심이 불안정하면 선형 보간.
    /// </summary>
    internal static List<Vector3> ArcPoints(Vector3 p0, Vector3 n0, Vector3 p1, Vector3 n1, int segments)
    {
        var pts = new List<Vector3>(Math.Max(0, segments - 1));
        if (segments < 2) return pts;
        bool linear = true;
        Vector3 c = default, u0n = default, u1n = default;
        float r0 = 0, r1 = 0, omega = 0;
        if (n0.LengthSquared() > 0.5f && n1.LengthSquared() > 0.5f)
        {
            var d0 = -n0; var d1 = -n1;
            float bb = Vector3.Dot(d0, d1);
            float denom = 1f - bb * bb;
            if (denom > 1e-4f)
            {
                var w = p0 - p1;
                float d = Vector3.Dot(d0, w), e = Vector3.Dot(d1, w);
                float s = (bb * e - d) / denom, t = (e - bb * d) / denom;
                c = ((p0 + d0 * s) + (p1 + d1 * t)) * 0.5f;
                var u0 = p0 - c; var u1 = p1 - c;
                r0 = u0.Length(); r1 = u1.Length();
                float chord = Vector3.Distance(p0, p1);
                if (r0 > 1e-7f && r1 > 1e-7f && r0 < chord * 50f && r1 < chord * 50f)
                {
                    u0n = u0 / r0; u1n = u1 / r1;
                    omega = MathF.Acos(System.Math.Clamp(Vector3.Dot(u0n, u1n), -1f, 1f));
                    if (omega > 1e-3f && omega < MathF.PI - 1e-3f) linear = false;
                }
            }
        }
        for (int k = 1; k < segments; k++)
        {
            float f = (float)k / segments;
            if (linear) { pts.Add(Vector3.Lerp(p0, p1, f)); continue; }
            float sinO = MathF.Sin(omega);
            var dir = (u0n * MathF.Sin((1f - f) * omega) + u1n * MathF.Sin(f * omega)) / sinO;
            pts.Add(c + dir * (r0 + (r1 - r0) * f));
        }
        return pts;
    }
}
