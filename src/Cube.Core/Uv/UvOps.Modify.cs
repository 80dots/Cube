using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>Maya UV Editor Modify 메뉴: Align / Linear Align / Distribute / Rotate / Normalize / Unitize / Cycle / Match Grid / Match UVs / Symmetrize / Straighten / Map Border / Optimize / Pin.</summary>
public static partial class UvOps
{
    public enum AlignMode { MinU, MaxU, MinV, MaxV, CenterU, CenterV }

    /// <summary>Align: 선택 UV 점을 한 축의 최소/최대/중심에 맞춘다.</summary>
    public static void Align(PolyMesh m, UvTopology topo, IEnumerable<int> points, AlignMode mode)
    {
        var list = points.ToList(); if (list.Count == 0) return;
        var (min, max) = Bounds(list.Select(p => topo.Points[p].Uv));
        foreach (int p in list)
        {
            var uv = topo.Points[p].Uv;
            uv = mode switch
            {
                AlignMode.MinU => new Vector2(min.X, uv.Y), AlignMode.MaxU => new Vector2(max.X, uv.Y), AlignMode.CenterU => new Vector2((min.X + max.X) * 0.5f, uv.Y),
                AlignMode.MinV => new Vector2(uv.X, min.Y), AlignMode.MaxV => new Vector2(uv.X, max.Y), _ => new Vector2(uv.X, (min.Y + max.Y) * 0.5f),
            };
            SetPointUv(m, topo, p, uv);
        }
    }

    /// <summary>Linear Align: 선택 UV 점을 그 점들의 주축(최소제곱 직선)에 투영한다.</summary>
    public static void LinearAlign(PolyMesh m, UvTopology topo, IEnumerable<int> points)
    {
        var list = points.ToList(); if (list.Count < 2) return;
        var c = list.Aggregate(Vector2.Zero, (s, p) => s + topo.Points[p].Uv) / list.Count;
        float sxx = 0, sxy = 0, syy = 0;
        foreach (int p in list) { var d = topo.Points[p].Uv - c; sxx += d.X * d.X; sxy += d.X * d.Y; syy += d.Y * d.Y; }
        float ang = 0.5f * MathF.Atan2(2 * sxy, sxx - syy);
        var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
        foreach (int p in list) { var d = topo.Points[p].Uv - c; SetPointUv(m, topo, p, c + dir * Vector2.Dot(d, dir)); }
    }

    /// <summary>Distribute UVs: 선택 UV 점을 축 방향으로 균등 간격으로 놓는다(양 끝은 고정).</summary>
    public static void Distribute(PolyMesh m, UvTopology topo, IEnumerable<int> points, bool alongU)
    {
        var list = points.OrderBy(p => alongU ? topo.Points[p].Uv.X : topo.Points[p].Uv.Y).ToList();
        if (list.Count < 3) return;
        float a = alongU ? topo.Points[list[0]].Uv.X : topo.Points[list[0]].Uv.Y;
        float b = alongU ? topo.Points[list[^1]].Uv.X : topo.Points[list[^1]].Uv.Y;
        for (int i = 1; i < list.Count - 1; i++)
        {
            float t = a + (b - a) * i / (list.Count - 1);
            var uv = topo.Points[list[i]].Uv;
            SetPointUv(m, topo, list[i], alongU ? new Vector2(t, uv.Y) : new Vector2(uv.X, t));
        }
    }

    /// <summary>Rotate: 선택 UV 점을 중심(없으면 경계 상자 중심) 기준으로 angleDeg 회전.</summary>
    public static void Rotate(PolyMesh m, UvTopology topo, IEnumerable<int> points, float angleDeg, Vector2? pivot = null)
    {
        var list = points.ToList(); if (list.Count == 0) return;
        var (min, max) = Bounds(list.Select(p => topo.Points[p].Uv));
        var c = pivot ?? (min + max) * 0.5f;
        TransformPoints(m, topo, list, Matrix3x2.CreateRotation(angleDeg * MathF.PI / 180f, c));
    }

    /// <summary>Normalize: 선택 UV 점(면 단위로 모아서)의 경계 상자를 0..1에 맞춘다. preserveAspect면 큰 변 기준, collectively면 전체를 한 번에.</summary>
    public static void Normalize(PolyMesh m, UvTopology topo, IEnumerable<int> points, bool preserveAspect, bool collectively)
    {
        var list = points.ToList(); if (list.Count == 0) return;
        if (collectively) NormalizeGroup(m, topo, list, preserveAspect);
        else foreach (var g in list.GroupBy(p => topo.Points[p].Shell)) NormalizeGroup(m, topo, g.ToList(), preserveAspect);
    }

    private static void NormalizeGroup(PolyMesh m, UvTopology topo, List<int> list, bool preserveAspect)
    {
        var (min, max) = Bounds(list.Select(p => topo.Points[p].Uv));
        var size = Vector2.Max(max - min, new Vector2(1e-9f));
        float sx = 1f / size.X, sy = 1f / size.Y;
        if (preserveAspect) sx = sy = 1f / MathF.Max(size.X, size.Y);
        foreach (int p in list) { var uv = topo.Points[p].Uv - min; SetPointUv(m, topo, p, new Vector2(uv.X * sx, uv.Y * sy)); }
    }

    /// <summary>Unitize: 각 면의 UV를 0..1 사각형 둘레에 놓는다(쿼드는 네 모서리). 면을 심으로 분리한다.</summary>
    public static void Unitize(PolyMesh m, IEnumerable<int> faces)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        foreach (int f in list)
        {
            var hes = FaceHalfEdges(m, f).ToList();
            int n = hes.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 uv;
                if (n == 4) uv = i switch { 0 => new Vector2(0, 0), 1 => new Vector2(1, 0), 2 => new Vector2(1, 1), _ => new Vector2(0, 1) };
                else
                {
                    // 둘레를 따라 균등 분배
                    float t = 4f * i / n;
                    uv = t < 1 ? new Vector2(t, 0) : t < 2 ? new Vector2(1, t - 1) : t < 3 ? new Vector2(3 - t, 1) : new Vector2(0, 4 - t);
                }
                SetUv(m, hes[i], uv);
            }
        }
        // 모든 면이 독립된 섬이 되도록 선택 면의 엣지를 심으로
        var set = new HashSet<int>(list);
        foreach (int f in list) foreach (int he in FaceHalfEdges(m, f)) { var ed = m.Edges[m.Hes[he].Edge]; if (m.Hes[he].Twin >= 0) { ed.Seam = true; m.Edges[m.Hes[he].Edge] = ed; } }
    }

    /// <summary>Cycle: 면의 코너 UV를 한 칸 돌린다(쿼드의 텍스처 회전).</summary>
    public static void Cycle(PolyMesh m, IEnumerable<int> faces)
    {
        foreach (int f in faces)
        {
            if (f < 0 || f >= m.FaceCount || !m.Faces[f].Alive) continue;
            var hes = FaceHalfEdges(m, f).ToList();
            var uvs = hes.Select(h => m.Hes[h].Uv0).ToList();
            for (int i = 0; i < hes.Count; i++) SetUv(m, hes[i], uvs[(i + 1) % hes.Count]);
            foreach (int he in hes) if (m.Hes[he].Twin >= 0) { var ed = m.Edges[m.Hes[he].Edge]; ed.Seam = true; m.Edges[m.Hes[he].Edge] = ed; }
        }
    }

    /// <summary>Match Grid: 선택 UV 점을 gridSize 격자에 스냅.</summary>
    public static void MatchGrid(PolyMesh m, UvTopology topo, IEnumerable<int> points, float gridSize)
    {
        gridSize = MathF.Max(gridSize, 1e-6f);
        foreach (int p in points) { var uv = topo.Points[p].Uv; SetPointUv(m, topo, p, new Vector2(MathF.Round(uv.X / gridSize) * gridSize, MathF.Round(uv.Y / gridSize) * gridSize)); }
    }

    /// <summary>Match UVs: 선택 UV 점을 같은 정점의 다른 UV 점(다른 셸) 위치로 옮긴다(겹치게).</summary>
    public static int MatchUvs(PolyMesh m, UvTopology topo, IEnumerable<int> points)
    {
        var sel = new HashSet<int>(points);
        var byVertex = new Dictionary<int, List<int>>();
        for (int p = 0; p < topo.Points.Count; p++) { if (!byVertex.TryGetValue(topo.Points[p].Vertex, out var l)) byVertex[topo.Points[p].Vertex] = l = new List<int>(); l.Add(p); }
        int n = 0;
        foreach (int p in sel)
        {
            var others = byVertex[topo.Points[p].Vertex].Where(q => q != p && !sel.Contains(q)).ToList();
            if (others.Count == 0) continue;
            var target = others.Aggregate(Vector2.Zero, (s, q) => s + topo.Points[q].Uv) / others.Count;
            SetPointUv(m, topo, p, target); n++;
        }
        return n;
    }

    /// <summary>Symmetrize: 선택 UV 점을 축(alongU = U축 거울, 즉 u' = 2·pos − u)으로 반사한 위치에 가장 가까운 UV 점(허용 오차 안)을 그 반사 위치로 옮긴다.</summary>
    public static int SymmetrizeUvs(PolyMesh m, UvTopology topo, IEnumerable<int> points, bool mirrorU, float position, float tolerance)
    {
        var sel = new HashSet<int>(points);
        int n = 0;
        foreach (int p in sel)
        {
            var uv = topo.Points[p].Uv;
            var mirrored = mirrorU ? new Vector2(2 * position - uv.X, uv.Y) : new Vector2(uv.X, 2 * position - uv.Y);
            int best = -1; float bestD = tolerance * tolerance;
            for (int q = 0; q < topo.Points.Count; q++)
            {
                if (q == p || sel.Contains(q)) continue;
                float d = Vector2.DistanceSquared(topo.Points[q].Uv, mirrored);
                if (d < bestD) { bestD = d; best = q; }
            }
            if (best >= 0) { SetPointUv(m, topo, best, mirrored); n++; }
        }
        return n;
    }

    /// <summary>Straighten UVs: 선택 UV 점 사이의 엣지 중 U/V 축과 angleDeg 이내로 기운 것을 축에 맞춘다(끝점 좌표 평균).</summary>
    public static void StraightenUvs(PolyMesh m, UvTopology topo, IEnumerable<int> points, float angleDeg, bool alongU = true, bool alongV = true, int iterations = 3)
    {
        var sel = new HashSet<int>(points);
        var edges = new List<(int a, int b)>();
        var seen = new HashSet<long>();
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            if (!m.Hes[h].Alive) continue;
            int a = topo.HeToPoint[h], b = topo.HeToPoint[m.Hes[h].Next];
            if (!sel.Contains(a) || !sel.Contains(b) || a == b) continue;
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (seen.Add(key)) edges.Add((a, b));
        }
        float tan = MathF.Tan(angleDeg * MathF.PI / 180f);
        for (int it = 0; it < iterations; it++)
            foreach (var (a, b) in edges)
            {
                var pa = topo.Points[a].Uv; var pb = topo.Points[b].Uv;
                var d = pb - pa; float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y);
                if (alongU && ay <= ax * tan && ax > 1e-9f) { float y = (pa.Y + pb.Y) * 0.5f; SetPointUv(m, topo, a, new Vector2(pa.X, y)); SetPointUv(m, topo, b, new Vector2(pb.X, y)); }
                else if (alongV && ax <= ay * tan && ay > 1e-9f) { float x = (pa.X + pb.X) * 0.5f; SetPointUv(m, topo, a, new Vector2(x, pa.Y)); SetPointUv(m, topo, b, new Vector2(x, pb.Y)); }
            }
    }

    /// <summary>셸의 UV 경계 루프(심/경계 하프에지를 따라, 면 방향 순서)의 UV 점 목록들. 구멍이 있으면 여러 루프.</summary>
    public static List<List<int>> ShellBorderLoops(PolyMesh m, UvTopology topo, int shell)
    {
        var loops = new List<List<int>>();
        // 경계 하프에지: 트윈이 없거나, 트윈 쪽 코너의 UV 점이 다른(심) 하프에지
        bool IsBorder(int h) { var he = m.Hes[h]; if (he.Twin < 0) return true; return topo.HeToPoint[he.Twin] != topo.HeToPoint[he.Next] || topo.HeToPoint[m.Hes[he.Twin].Next] != topo.HeToPoint[h]; }
        var border = new Dictionary<int, int>(); // 시작 UV 점 → 하프에지
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            if (!m.Hes[h].Alive || topo.Points[topo.HeToPoint[h]].Shell != shell || !IsBorder(h)) continue;
            border.TryAdd(topo.HeToPoint[h], h);
        }
        var used = new HashSet<int>();
        foreach (var start in border.Keys.ToList())
        {
            if (used.Contains(start)) continue;
            var loop = new List<int>(); int cur = start; int guard = 0;
            while (guard++ < 100000)
            {
                if (!border.TryGetValue(cur, out int h) || used.Contains(cur)) break;
                used.Add(cur); loop.Add(cur);
                cur = topo.HeToPoint[m.Hes[h].Next];
                if (cur == start) break;
            }
            if (loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    /// <summary>
    /// Map Border: 셸의 바깥 경계(가장 긴 루프)를 0..1 정사각형(또는 내접원) 둘레에 호 길이 비례로 놓는다. startPoint가 있으면 그 점이 (0,0).
    /// 안쪽 점은 그대로이므로 뒤에 Optimize/Unfold를 돌린다.
    /// </summary>
    public static bool MapBorder(PolyMesh m, UvTopology topo, int shell, bool square, int startPoint = -1, bool fillGapsEvenly = false)
    {
        var loops = ShellBorderLoops(m, topo, shell);
        if (loops.Count == 0) return false;
        var loop = loops.OrderByDescending(l => l.Count).First();
        if (startPoint >= 0 && loop.Contains(startPoint)) { int k = loop.IndexOf(startPoint); loop = loop.Skip(k).Concat(loop.Take(k)).ToList(); }
        int n = loop.Count;
        var len = new float[n]; float total = 0;
        for (int i = 0; i < n; i++) { len[i] = fillGapsEvenly ? 1f : MathF.Max(Vector2.Distance(topo.Points[loop[i]].Uv, topo.Points[loop[(i + 1) % n]].Uv), 1e-6f); total += len[i]; }
        float acc = 0;
        for (int i = 0; i < n; i++)
        {
            float t = acc / total; acc += len[i];
            Vector2 uv;
            if (square)
            {
                float s = t * 4f;
                uv = s < 1 ? new Vector2(s, 0) : s < 2 ? new Vector2(1, s - 1) : s < 3 ? new Vector2(3 - s, 1) : new Vector2(0, 4 - s);
            }
            else uv = new Vector2(0.5f + 0.5f * MathF.Cos(t * MathF.Tau), 0.5f + 0.5f * MathF.Sin(t * MathF.Tau));
            SetPointUv(m, topo, loop[i], uv);
        }
        return true;
    }

    /// <summary>Straighten Border: 가장 긴 경계 루프의 네 모서리(회전각이 가장 큰 점 4개)를 찾아 경계 상자 모서리에 놓고 각 변을 호 길이 비례로 펴준다.</summary>
    public static bool StraightenBorder(PolyMesh m, UvTopology topo, int shell)
    {
        var loops = ShellBorderLoops(m, topo, shell);
        if (loops.Count == 0) return false;
        var loop = loops.OrderByDescending(l => l.Count).First();
        int n = loop.Count; if (n < 4) return false;
        var turn = new float[n];
        for (int i = 0; i < n; i++)
        {
            var p = topo.Points[loop[i]].Uv; var a = topo.Points[loop[(i + n - 1) % n]].Uv; var b = topo.Points[loop[(i + 1) % n]].Uv;
            var d0 = Vector2.Normalize(p - a + new Vector2(1e-12f)); var d1 = Vector2.Normalize(b - p + new Vector2(1e-12f));
            turn[i] = MathF.Acos(Math.Clamp(Vector2.Dot(d0, d1), -1f, 1f));
        }
        var corners = Enumerable.Range(0, n).OrderByDescending(i => turn[i]).Take(4).OrderBy(i => i).ToList();
        var (min, max) = Bounds(loop.Select(p => topo.Points[p].Uv));
        var targets = new[] { min, new Vector2(max.X, min.Y), max, new Vector2(min.X, max.Y) };
        // 모서리 0이 min에 오도록 가장 가까운 매칭 회전
        int bestRot = 0; float bestD = float.MaxValue;
        for (int r = 0; r < 4; r++)
        {
            float d = 0; for (int k = 0; k < 4; k++) d += Vector2.DistanceSquared(topo.Points[loop[corners[k]]].Uv, targets[(k + r) % 4]);
            if (d < bestD) { bestD = d; bestRot = r; }
        }
        for (int k = 0; k < 4; k++)
        {
            int i0 = corners[k], i1 = corners[(k + 1) % 4];
            var t0 = targets[(k + bestRot) % 4]; var t1 = targets[(k + 1 + bestRot) % 4];
            int count = ((i1 - i0 + n) % n);
            float total = 0; var seg = new List<float>();
            for (int s = 0; s < count; s++) { float l = MathF.Max(Vector2.Distance(topo.Points[loop[(i0 + s) % n]].Uv, topo.Points[loop[(i0 + s + 1) % n]].Uv), 1e-6f); seg.Add(l); total += l; }
            float acc = 0;
            for (int s = 0; s <= count; s++)
            {
                float t = total > 0 ? acc / total : 0;
                SetPointUv(m, topo, loop[(i0 + s) % n], Vector2.Lerp(t0, t1, t));
                if (s < count) acc += seg[s];
            }
        }
        return true;
    }

    /// <summary>Straighten Shell: 선택 엣지 체인을 직선(주축)으로 펴서 고정하고 나머지를 Unfold한다.</summary>
    public static void StraightenShell(PolyMesh m, UvTopology topo, IEnumerable<int> edges, int iterations = 80)
    {
        var pts = new HashSet<int>();
        foreach (int e in edges)
        {
            if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive) continue;
            var ed = m.Edges[e];
            foreach (int he in new[] { ed.He0, ed.He1 }) { if (he < 0) continue; pts.Add(topo.HeToPoint[he]); pts.Add(topo.HeToPoint[m.Hes[he].Next]); }
        }
        if (pts.Count < 2) return;
        LinearAlign(m, topo, pts);
        var shells = pts.Select(p => topo.Points[p].Shell).Distinct().ToList();
        UnfoldRelax(m, topo, shells, iterations, pts);
    }

    /// <summary>Optimize: 셸의 경계와 Pin을 고정한 채 안쪽을 이완한다.</summary>
    public static void Optimize(PolyMesh m, UvTopology topo, IEnumerable<int> shells, int iterations = 60)
    {
        var shellList = shells.Distinct().ToList();
        var pinned = new HashSet<int>();
        foreach (int s in shellList) foreach (var loop in ShellBorderLoops(m, topo, s)) pinned.UnionWith(loop);
        UnfoldRelax(m, topo, shellList, iterations, pinned);
    }

    /// <summary>Pin / Unpin: 선택 UV 점의 모든 코너에 PinUv를 설정.</summary>
    public static void SetPins(PolyMesh m, UvTopology topo, IEnumerable<int> points, bool pin)
    {
        foreach (int p in points) foreach (int he in topo.Points[p].HalfEdges) { var h = m.Hes[he]; h.PinUv = pin; m.Hes[he] = h; }
    }

    public static void InvertPins(PolyMesh m) { for (int h = 0; h < m.HalfEdgeCount; h++) { var he = m.Hes[h]; if (he.Alive) { he.PinUv = !he.PinUv; m.Hes[h] = he; } } }
    public static void UnpinAll(PolyMesh m) { for (int h = 0; h < m.HalfEdgeCount; h++) { var he = m.Hes[h]; if (he.PinUv) { he.PinUv = false; m.Hes[h] = he; } } }

    /// <summary>Copy UVs: 면의 코너 UV 목록. Paste: 코너 수가 같은 면에 순서대로 붙인다(시작 코너 회전 offset).</summary>
    public static List<Vector2> CopyFaceUvs(PolyMesh m, int f) => FaceHalfEdges(m, f).Select(h => m.Hes[h].Uv0).ToList();

    public static bool PasteFaceUvs(PolyMesh m, int f, IReadOnlyList<Vector2> uvs)
    {
        var hes = FaceHalfEdges(m, f).ToList();
        if (hes.Count != uvs.Count) return false;
        for (int i = 0; i < hes.Count; i++) SetUv(m, hes[i], uvs[i]);
        foreach (int he in hes) if (m.Hes[he].Twin >= 0) { var ed = m.Edges[m.Hes[he].Edge]; ed.Seam = true; m.Edges[m.Hes[he].Edge] = ed; }
        return true;
    }
}
