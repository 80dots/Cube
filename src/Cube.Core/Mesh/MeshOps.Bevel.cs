using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>엣지 베벨(1 세그먼트 챔퍼).</summary>
public static partial class MeshOps
{
    private sealed record BevelEntry(int Vertex, Vector2 Uv, Vector3 Normal, int OriginEdge, int OriginVertex);

    /// <summary>
    /// 선택 엣지를 베벨한다. 각 끝 정점에서 선택되지 않은 인접 엣지를 따라 distance만큼 물러난 새 정점을 만들고,
    /// 선택 엣지마다 쿼드, 선택 엣지가 모이는 정점에는 캡 면을 만든다. 반환값은 새로 생긴 베벨 면 ID들.
    /// </summary>
    public static List<int> BevelEdges(PolyMesh m, IEnumerable<int> edgeIds, float distance)
    {
        var result = new List<int>();
        var selected = new HashSet<int>(edgeIds.Where(e => e >= 0 && e < m.EdgeCount && m.Edges[e].Alive && !m.IsBoundaryEdge(e)));
        if (selected.Count == 0) return result;
        distance = MathF.Max(distance, 1e-5f);

        var V = new HashSet<int>();
        foreach (int e in selected) { var (a, b) = m.EdgeVertices(e); V.Add(a); V.Add(b); }

        // 영향 면과 원래 하드 플래그
        var affected = new List<int>();
        var tmp = new List<int>();
        foreach (int v in V) { m.GetVertexFaces(v, tmp); foreach (int f in tmp) if (!affected.Contains(f)) affected.Add(f); }
        var hardOf = new Dictionary<int, bool>();
        var edgeVerts = new Dictionary<int, (int a, int b)>();
        foreach (int f in affected)
        {
            var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes) { int e = m.Hes[he].Edge; hardOf[e] = m.Edges[e].Hard; edgeVerts[e] = m.EdgeVertices(e); }
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

        // 베벨 쿼드
        foreach (var (e, f0, f1, a, b) in selInfo)
        {
            if (!side.TryGetValue((f0, e, a), out var a0) || !side.TryGetValue((f0, e, b), out var b0) ||
                !side.TryGetValue((f1, e, a), out var a1) || !side.TryGetValue((f1, e, b), out var b1)) continue;
            var quad = new List<Corner> { new(b0.vert, b0.uv, Vector3.Zero), new(a0.vert, a0.uv, Vector3.Zero), new(a1.vert, a1.uv, Vector3.Zero), new(b1.vert, b1.uv, Vector3.Zero) };
            if (quad.Select(c => c.Vertex).Distinct().Count() < 3) continue;
            int q = AddFaceWithCorners(m, quad);
            if (q >= 0) result.Add(q);
            capEdges[a].Add((a1.vert, a0.vert));
            capEdges[b].Add((b0.vert, b1.vert));
        }

        // 정점 캡
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
}
