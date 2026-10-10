using System.Numerics;
using Cube.Core.Uv;

namespace Cube.Core.Mesh;

/// <summary>Smart Soften/Harden 임계값 결정 방식.</summary>
public enum SmartThresholdMode
{
    /// <summary>크리즈 강도 히스토그램을 Otsu로 두 무리로 나눠 메시마다 자동으로 정한다(Bias로 치우침 조절).</summary>
    Auto,
    /// <summary>High/Low 각도를 직접 준다.</summary>
    Manual,
}

/// <summary>볼록/오목 크리즈 중 어느 쪽을 하드로 삼을지.</summary>
public enum SmartConcavity { Both, ConvexOnly, ConcaveOnly }

/// <summary>UV 셸 경계 규칙.</summary>
public enum SmartUvRule
{
    /// <summary>UV를 보지 않는다.</summary>
    Ignore,
    /// <summary>UV 셸 경계(심 또는 UV가 갈라진 엣지)는 무조건 하드.</summary>
    HardenBorders,
    /// <summary>셸 경계는 하드, 셸 안쪽은 무조건 소프트(노멀맵 베이크용: 정점 분할이 UV 분할과 일치).</summary>
    HardenBordersSoftenInside,
    /// <summary>셸 안쪽만 소프트로 강제하고 경계는 형태 분석 결과를 따른다.</summary>
    SoftenInside,
}

/// <summary>결과에 적용할 가중 노멀.</summary>
public enum SmartWeightedNormals
{
    None,
    /// <summary>면적 가중(큰 면이 노멀을 지배; Blender Weighted Normal Face Area).</summary>
    FaceArea,
    /// <summary>면적 × 코너각 가중.</summary>
    FaceAreaAndAngle,
}

/// <summary>
/// Smart Soften/Harden 옵션(v0.0.60). 엣지 하나의 각도가 아니라 링 문맥(이웃 엣지와의 각도 차), 메시 전체의 각도 분포,
/// 루프 연속성, UV 구조를 함께 보고 하드/소프트를 정한다.
/// </summary>
public sealed class SmartEdgeOptions
{
    public SmartThresholdMode ThresholdMode = SmartThresholdMode.Auto;
    /// <summary>Auto 임계값 치우침(−1 = 많이 소프트 … +1 = 많이 하드). Otsu 임계값을 분포 폭의 이 비율만큼 옮긴다.</summary>
    public float AutoBias = 0f;
    /// <summary>Manual: 이 강도(도) 이상이면 확정 하드.</summary>
    public float HighAngle = 40f;
    /// <summary>Manual: 이 강도(도) 이상이면 후보(히스테리시스로 확정 하드와 이어질 때만 하드). High 이상이면 히스테리시스 없음.</summary>
    public float LowAngle = 20f;
    /// <summary>Auto일 때 Low = High × 이 비율(0..1). 1이면 히스테리시스 없음.</summary>
    public float HysteresisRatio = 0.5f;
    /// <summary>링 문맥 가중(0 = 순수 각도, 1 = 링 이웃 각도를 모두 뺀 '각도 변화량'). 곡면 띠(원기둥·구)를 소프트로 만드는 핵심.</summary>
    public float ContextWeight = 1f;
    /// <summary>이 각도(도) 이상이면 문맥과 무관하게 무조건 하드(4각 원기둥은 상자다). 180이면 끔.</summary>
    public float MaxSmoothAngle = 80f;
    /// <summary>크리즈 강도(문맥을 뺀 값)가 이 각도(도) 미만이면 무조건 소프트. 베벨 프로파일의 작은 불균일이 하드로 잡히지 않게 한다.</summary>
    public float MinHardAngle = 15f;
    public SmartConcavity Concavity = SmartConcavity.Both;
    /// <summary>루프 전파: 후보 엣지가 확정 하드 엣지와 엣지 루프로 이어지면 하드.</summary>
    public bool LoopPropagation = true;
    /// <summary>이 길이(엣지 수) 미만의 고립된 하드 체인은 지운다(0 = 끔).</summary>
    public int MinChainLength = 2;
    /// <summary>하드 루프의 한 칸 틈을 메운다(양쪽이 하드인 루프 엣지).</summary>
    public bool FillGaps = true;
    public SmartUvRule UvRule = SmartUvRule.Ignore;
    /// <summary>UV 심 엣지는 하드.</summary>
    public bool SeamsHard = false;
    /// <summary>크리즈(서브디비전) 엣지는 하드.</summary>
    public bool CreasesHard = true;
    /// <summary>true면 현재 하드인 엣지를 소프트로 바꾸지 않는다(추가만).</summary>
    public bool KeepExistingHard = false;
    /// <summary>true면 현재 소프트인 엣지를 하드로 바꾸지 않는다(제거만).</summary>
    public bool KeepExistingSoft = false;
    public SmartWeightedNormals WeightedNormals = SmartWeightedNormals.None;
}

/// <summary>Smart Soften/Harden 실행 결과 요약.</summary>
public sealed class SmartEdgeReport
{
    public int Hard, Soft, Candidates, Changed;
    /// <summary>실제로 쓴 임계값(도).</summary>
    public float HighUsed, LowUsed;
    /// <summary>Auto에서 분포가 한 무리뿐이라 기본값으로 폴백했는지.</summary>
    public bool AutoFallback;
}

public static partial class MeshOps
{
    /// <summary>
    /// Smart Soften/Harden: 대상 엣지(null = 전체)의 Hard 플래그를 형태 분석으로 다시 정한다. 노멀 재계산은 호출자 몫.
    /// 순서 ① 엣지별 부호 있는 이면각 ② 크리즈 강도 = |θ| − w·(링 이웃 |θ| 평균) (+ Max/Min 절대 규칙, 오목/볼록 필터)
    /// ③ 임계값(Otsu 자동 또는 수동) ④ 히스테리시스: High 이상 확정, Low~High는 확정과 루프로 이어질 때만
    /// ⑤ 정리: 짧은 고립 체인 제거, 루프 한 칸 틈 메우기 ⑥ UV·심·크리즈 규칙 ⑦ 기존 플래그 보호 ⑧ 가중 노멀 고정.
    /// </summary>
    public static SmartEdgeReport SmartSoftenHarden(PolyMesh m, IEnumerable<int>? edgeIds, SmartEdgeOptions o)
    {
        var rep = new SmartEdgeReport();
        var domain = edgeIds == null ? AllInteriorEdges(m) : new HashSet<int>(AliveEdges(m, edgeIds).Where(e => m.Edges[e].He1 >= 0));
        if (domain.Count == 0) return rep;

        // ① 부호 있는 이면각(라디안): 볼록 +, 오목 −
        var theta = new Dictionary<int, float>();
        for (int e = 0; e < m.EdgeCount; e++)
        {
            var ed = m.Edges[e];
            if (!ed.Alive || ed.He1 < 0) continue;
            theta[e] = SignedDihedral(m, e);
        }

        // ② 크리즈 강도(도)
        float w = Math.Clamp(o.ContextWeight, 0f, 1f);
        var strength = new Dictionary<int, float>();
        foreach (int e in domain)
        {
            float t = theta[e];
            float a = MathF.Abs(t) * 180f / MathF.PI;
            if (o.Concavity == SmartConcavity.ConvexOnly && t < 0) { strength[e] = 0; continue; }
            if (o.Concavity == SmartConcavity.ConcaveOnly && t > 0) { strength[e] = 0; continue; }
            if (a < 2f) { strength[e] = 0; continue; } // 평면 노이즈
            if (a >= o.MaxSmoothAngle) { strength[e] = 180f; continue; }
            // 링 문맥: 양쪽 면에서 e와 가장 비슷한 각도로 이어지는 링 엣지를 하나씩 고르고(그 면 안의 '맞은편' 후보 중 각도 편차 최소),
            // 두 편차의 평균 d로 균일도 u = 1 − d/θ → 강도 = θ·(1 − w·u). 곡면 띠는 양쪽 모두 비슷해 0에 가깝고,
            // 단일 챔퍼 [45 | 45, 90]은 한쪽만 이어져 절반(22.5), 베벨 코너 패치의 삼각형은 어느 한 변으로든 이어지면 소프트.
            var (f0, f1) = m.EdgeFaces(e);
            float dev = 0f; int sides = 0;
            foreach (int f in new[] { f0, f1 })
            {
                float best = float.MaxValue;
                foreach (int r in RingNeighbors(m, e, f))
                {
                    float ar = MathF.Abs(theta.TryGetValue(r, out var tr) ? tr : 0f) * 180f / MathF.PI;
                    best = MathF.Min(best, MathF.Abs(ar - a));
                }
                if (best < float.MaxValue) { dev += best; sides++; }
            }
            float u = sides > 0 ? MathF.Max(0f, 1f - dev / sides / a) : 0f;
            float st = a * (1f - w * u);
            strength[e] = st < o.MinHardAngle ? 0f : st;
        }

        // ③ 임계값
        float high, low;
        if (o.ThresholdMode == SmartThresholdMode.Manual) { high = o.HighAngle; low = MathF.Min(o.LowAngle, high); }
        else
        {
            var vals = strength.Values.Where(s => s > 0f && s < 180f).ToList();
            if (vals.Count >= 4 && Otsu(vals, out float t0, out float spread))
            {
                high = t0 + o.AutoBias * spread * 0.5f;
                high = Math.Clamp(high, o.MinHardAngle, o.MaxSmoothAngle);
            }
            else { high = 20f; rep.AutoFallback = true; } // 분포가 한 무리뿐(값이 적거나 고름): 문맥을 뺀 강도 기준 기본 20°
            low = high * Math.Clamp(o.HysteresisRatio, 0f, 1f);
        }
        rep.HighUsed = high; rep.LowUsed = low;

        // ④ 히스테리시스 + 루프 전파
        var hard = new HashSet<int>();
        var cand = new HashSet<int>();
        foreach (var (e, s) in strength) { if (s >= high) hard.Add(e); else if (s >= low) cand.Add(e); }
        rep.Candidates = cand.Count;
        if (o.LoopPropagation && cand.Count > 0 && hard.Count > 0)
        {
            // 확정 하드에서 루프를 따라 후보로 번진다(후보 사이로도 계속)
            var stack = new Stack<int>(hard);
            while (stack.Count > 0)
            {
                int e = stack.Pop();
                var (a, b) = m.EdgeVertices(e);
                foreach (int v in new[] { a, b })
                {
                    int nx = OppositeEdgeAtVertex(m, v, e);
                    if (nx >= 0 && cand.Remove(nx)) { hard.Add(nx); stack.Push(nx); }
                }
            }
        }
        else if (!o.LoopPropagation && low < high)
        {
            // 전파 없이 히스테리시스: 후보 중 확정 하드와 정점을 공유하는 것만
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (int e in cand.ToArray())
                {
                    var (a, b) = m.EdgeVertices(e);
                    if (TouchesHard(m, a, hard, e) || TouchesHard(m, b, hard, e)) { cand.Remove(e); hard.Add(e); grew = true; }
                }
            }
        }

        // ⑤ 정리
        if (o.FillGaps) FillLoopGaps(m, hard, domain);
        if (o.MinChainLength > 1) RemoveShortChains(m, hard, o.MinChainLength);

        // ⑥ UV·심·크리즈 규칙
        UvTopology? topo = null;
        bool needUv = o.UvRule != SmartUvRule.Ignore;
        if (needUv) topo = UvTopology.Build(m);
        foreach (int e in domain)
        {
            var ed = m.Edges[e];
            bool? force = null;
            if (needUv)
            {
                bool border = ed.Seam || !UvOps.IsEdgeSewn(m, e);
                switch (o.UvRule)
                {
                    case SmartUvRule.HardenBorders: if (border) force = true; break;
                    case SmartUvRule.HardenBordersSoftenInside: force = border; break;
                    case SmartUvRule.SoftenInside: if (!border) force = false; break;
                }
            }
            if (o.SeamsHard && ed.Seam) force = true;
            if (o.CreasesHard && ed.Crease > 0f) force = true;
            if (force == true) hard.Add(e); else if (force == false) hard.Remove(e);
        }

        // ⑦ 적용(기존 플래그 보호)
        foreach (int e in domain)
        {
            var ed = m.Edges[e];
            bool target = hard.Contains(e);
            if (o.KeepExistingHard && ed.Hard) target = true;
            if (o.KeepExistingSoft && !ed.Hard) target = false;
            if (ed.Hard != target) { ed.Hard = target; m.Edges[e] = ed; rep.Changed++; }
            if (target) rep.Hard++; else rep.Soft++;
        }

        // ⑧ 가중 노멀: 소프트 부채꼴마다 면적(×각) 가중 합을 코너 노멀로 고정
        if (o.WeightedNormals != SmartWeightedNormals.None) ApplyWeightedNormals(m, domain, o.WeightedNormals == SmartWeightedNormals.FaceAreaAndAngle);
        else UnlockCornerNormalsOf(m, domain);
        return rep;
    }

    /// <summary>살아 있는 내부(양면) 엣지 전체.</summary>
    private static HashSet<int> AllInteriorEdges(PolyMesh m)
    {
        var set = new HashSet<int>();
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive && m.Edges[e].He1 >= 0) set.Add(e);
        return set;
    }

    /// <summary>부호 있는 이면각(라디안). 볼록(바깥으로 접힘) +, 오목 −. 퇴화 면은 0.</summary>
    public static float SignedDihedral(PolyMesh m, int e)
    {
        var (f0, f1) = m.EdgeFaces(e);
        var n0 = MeshNormals.FaceNormalUnnormalized(m, f0); var n1 = MeshNormals.FaceNormalUnnormalized(m, f1);
        if (n0.LengthSquared() < 1e-18f || n1.LengthSquared() < 1e-18f) return 0f;
        n0 = Vector3.Normalize(n0); n1 = Vector3.Normalize(n1);
        float ang = MathF.Acos(Math.Clamp(Vector3.Dot(n0, n1), -1f, 1f));
        // 볼록 판정: f1 중심이 f0 평면의 뒤쪽(법선 반대)이면 볼록
        var c0 = m.FaceCentroid(f0); var c1 = m.FaceCentroid(f1);
        return Vector3.Dot(c1 - c0, n0) <= 0f ? ang : -ang;
    }

    /// <summary>
    /// 링 이웃: 면 f에서 e의 '맞은편' 엣지(쿼드 = 두 칸 건너, 그 외 = e와 정점을 공유하지 않는 엣지들; 삼각형은 나머지 두 엣지).
    /// 곡면 띠에서는 이 엣지들의 이면각이 e와 비슷하고, 모서리에서는 훨씬 작다.
    /// </summary>
    private static IEnumerable<int> RingNeighbors(PolyMesh m, int e, int f)
    {
        var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
        int n = hes.Count;
        int idx = hes.FindIndex(h => m.Hes[h].Edge == e);
        if (idx < 0) yield break;
        if (n == 4) { yield return m.Hes[hes[(idx + 2) % 4]].Edge; yield break; }
        if (n == 3) { yield return m.Hes[hes[(idx + 1) % 3]].Edge; yield return m.Hes[hes[(idx + 2) % 3]].Edge; yield break; }
        var (a, b) = m.EdgeVertices(e);
        for (int i = 0; i < n; i++)
        {
            if (i == idx) continue;
            int oe = m.Hes[hes[i]].Edge; var (x, y) = m.EdgeVertices(oe);
            if (x == a || x == b || y == a || y == b) continue;
            yield return oe;
        }
    }

    /// <summary>Otsu: 값들을 두 무리로 가장 잘 나누는 임계값. 분산이 거의 없으면 false.</summary>
    private static bool Otsu(List<float> vals, out float threshold, out float spread)
    {
        threshold = 0; spread = 0;
        float min = vals.Min(), max = vals.Max();
        spread = max - min;
        if (spread < 2f) return false;
        const int bins = 64;
        var hist = new int[bins];
        foreach (var v in vals) hist[Math.Clamp((int)((v - min) / spread * (bins - 1)), 0, bins - 1)]++;
        int total = vals.Count; float sumAll = 0; for (int i = 0; i < bins; i++) sumAll += i * hist[i];
        float sumB = 0, best = -1; int wB = 0, bestBin = 0;
        for (int i = 0; i < bins; i++)
        {
            wB += hist[i]; if (wB == 0) continue;
            int wF = total - wB; if (wF == 0) break;
            sumB += i * hist[i];
            float mB = sumB / wB, mF = (sumAll - sumB) / wF;
            float between = wB * (float)wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; bestBin = i; }
        }
        threshold = min + (bestBin + 1f) / bins * spread;
        // 두 무리의 평균 차가 작으면(한 무리) 실패
        return best > 0 && threshold > min + 1f && threshold < max - 1f;
    }

    private static bool TouchesHard(PolyMesh m, int v, HashSet<int> hard, int except)
    {
        var es = new List<int>(); m.GetVertexEdges(v, es);
        foreach (int c in es) if (c != except && hard.Contains(c)) return true;
        return false;
    }

    /// <summary>하드 루프의 한 칸 틈: 양쪽 맞은편 엣지가 모두 하드인 비하드 엣지를 하드로.</summary>
    private static void FillLoopGaps(PolyMesh m, HashSet<int> hard, HashSet<int> domain)
    {
        var add = new List<int>();
        foreach (int e in domain)
        {
            if (hard.Contains(e)) continue;
            var (a, b) = m.EdgeVertices(e);
            int na = OppositeEdgeAtVertex(m, a, e), nb = OppositeEdgeAtVertex(m, b, e);
            if (na >= 0 && nb >= 0 && hard.Contains(na) && hard.Contains(nb)) add.Add(e);
        }
        foreach (int e in add) hard.Add(e);
    }

    /// <summary>하드 엣지를 정점 공유로 묶은 연결 요소 중 엣지 수가 minLen 미만인 것을 지운다.</summary>
    private static void RemoveShortChains(PolyMesh m, HashSet<int> hard, int minLen)
    {
        var seen = new HashSet<int>();
        var es = new List<int>();
        foreach (int start in hard.ToArray())
        {
            if (seen.Contains(start) || !hard.Contains(start)) continue;
            var comp = new List<int>(); var stack = new Stack<int>(); stack.Push(start); seen.Add(start);
            while (stack.Count > 0)
            {
                int e = stack.Pop(); comp.Add(e);
                var (a, b) = m.EdgeVertices(e);
                foreach (int v in new[] { a, b }) { m.GetVertexEdges(v, es); foreach (int c in es) if (hard.Contains(c) && seen.Add(c)) stack.Push(c); }
            }
            if (comp.Count < minLen) foreach (int e in comp) hard.Remove(e);
        }
    }

    /// <summary>대상 엣지에 닿은 정점의 코너 노멀 고정을 푼다(이전 Weighted Normal 결과가 남지 않도록).</summary>
    private static void UnlockCornerNormalsOf(PolyMesh m, HashSet<int> domain)
    {
        var verts = new HashSet<int>();
        foreach (int e in domain) { var (a, b) = m.EdgeVertices(e); verts.Add(a); verts.Add(b); }
        foreach (int v in verts) foreach (int h in m.VertexOutgoing(v).ToArray()) { var he = m.Hes[h]; if (he.NormalLocked) { he.NormalLocked = false; m.Hes[h] = he; } }
    }

    /// <summary>Weighted Normals: 대상 엣지에 닿은 정점의 각 코너에서 소프트 부채꼴 면들의 면적(×코너각) 가중 법선 합을 코너 노멀로 고정한다.</summary>
    private static void ApplyWeightedNormals(PolyMesh m, HashSet<int> domain, bool withAngle)
    {
        var verts = new HashSet<int>();
        foreach (int e in domain) { var (a, b) = m.EdgeVertices(e); verts.Add(a); verts.Add(b); }
        foreach (int v in verts)
        {
            foreach (int h in m.VertexOutgoing(v).ToArray())
            {
                var sum = Vector3.Zero;
                foreach (int f in CornerFan(m, h))
                {
                    var n = MeshNormals.FaceNormalUnnormalized(m, f); // 길이 = 2 × 면적
                    if (withAngle) n *= CornerAngle(m, f, v);
                    sum += n;
                }
                if (sum.LengthSquared() < 1e-20f) continue;
                var he = m.Hes[h]; he.Normal = Vector3.Normalize(sum); he.NormalLocked = true; m.Hes[h] = he;
            }
        }
    }

    /// <summary>면 f에서 정점 v가 이루는 코너 각(라디안).</summary>
    private static float CornerAngle(PolyMesh m, int f, int v)
    {
        int start = m.Faces[f].HalfEdge, he = start;
        do
        {
            var h = m.Hes[he];
            if (h.Vertex == v)
            {
                var p = m.Verts[v].Position; var a = m.Verts[m.Hes[h.Next].Vertex].Position - p; var b = m.Verts[m.Hes[h.Prev].Vertex].Position - p;
                if (a.LengthSquared() < 1e-20f || b.LengthSquared() < 1e-20f) return 0f;
                return MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f));
            }
            he = h.Next;
        } while (he != start);
        return 0f;
    }
}
