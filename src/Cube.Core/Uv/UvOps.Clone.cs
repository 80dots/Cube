using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>클론 대상 선택 범위.</summary>
public enum CloneShellTarget
{
    /// <summary>원본 셸과 경계 상자가 겹치는(스택된) 같은 위상의 셸.</summary>
    Stacked,
    /// <summary>메시 안의 같은 위상의 모든 셸.</summary>
    AllSimilar,
}

/// <summary>클론 결과 요약.</summary>
public sealed class CloneShellReport
{
    /// <summary>UV를 복사한 셸 수.</summary>
    public int Cloned;
    /// <summary>후보였지만 위상이 일치하지 않아 건너뛴 셸 수.</summary>
    public int Mismatched;
    /// <summary>검사한 후보 셸 수.</summary>
    public int Candidates;
}

/// <summary>
/// Clone UV Shell(v0.0.68): 원본 셸의 UV를 같은 위상(같은 면·점·연결)을 가진 다른 셸에 그대로 복사한다 — 스택된 복제 파츠(좌우 대칭 부품 등)가
/// 텍스처 공간을 정확히 공유하도록. 대응은 하프에지 그래프 동형(next/셸 안 twin 전파)으로 찾고, 대칭(회전 등)으로 여러 대응이 가능하면
/// 현재 UV와 가장 가까운 것을 고른다(스택해 둔 상태를 존중).
/// </summary>
public static partial class UvOps
{
    /// <summary>셸에 속한 면 목록.</summary>
    public static List<int> ShellFaces(PolyMesh m, UvTopology topo, int shell)
    {
        var list = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            int p = topo.HeToPoint[m.Faces[f].HalfEdge];
            if (p >= 0 && topo.Points[p].Shell == shell) list.Add(f);
        }
        return list;
    }

    /// <summary>셸 위상 서명(면 수, 점 수, 하프에지 수, 경계 하프에지 수, 면 차수 합) — 후보를 빠르게 거른다.</summary>
    private static (int faces, int points, int hes, int boundary, long degreeHash) ShellSignature(PolyMesh m, UvTopology topo, int shell, List<int> faces)
    {
        int hes = 0, boundary = 0; long dh = 1469598103934665603L;
        var degrees = new List<int>();
        foreach (int f in faces)
        {
            int d = 0;
            foreach (int he in FaceHalfEdges(m, f)) { hes++; d++; if (InnerTwin(m, topo, he) < 0) boundary++; }
            degrees.Add(d);
        }
        degrees.Sort();
        foreach (int d in degrees) { dh ^= d; dh *= 1099511628211L; }
        return (faces.Count, topo.PointsInShell(shell).Count(), hes, boundary, dh);
    }

    /// <summary>셸 안에서 UV로 이어진 반대 하프에지(심이거나 UV가 갈라졌거나 경계면 −1).</summary>
    private static int InnerTwin(PolyMesh m, UvTopology topo, int he)
    {
        int tw = m.Hes[he].Twin; if (tw < 0) return -1;
        int a = topo.HeToPoint[he], b = topo.HeToPoint[m.Hes[he].Next];
        int ta = topo.HeToPoint[tw], tb = topo.HeToPoint[m.Hes[tw].Next];
        return a == tb && b == ta ? tw : -1;
    }

    /// <summary>
    /// 두 셸의 하프에지 대응(src → dst)을 찾는다. 대응이 여럿이면(대칭 셸) dst의 현재 UV(경계 상자 중심을 맞춘 뒤)와 가장 가까운 것.
    /// </summary>
    /// <returns>대응을 찾으면 src 하프에지 → dst 하프에지 사전, 아니면 null.</returns>
    public static Dictionary<int, int>? MatchShellTopology(PolyMesh m, UvTopology topo, int srcShell, int dstShell)
    {
        Dictionary<int, int>? best = null; float bestScore = float.MaxValue;
        var (smn, smx) = ShellBounds(topo, srcShell); var (dmn, dmx) = ShellBounds(topo, dstShell);
        var offset = (dmn + dmx) * 0.5f - (smn + smx) * 0.5f;
        foreach (var map in AllShellMappings(m, topo, srcShell, dstShell))
        {
            // 점수: 대응 점끼리 (원본 UV + 중심 오프셋)과 대상 현재 UV의 거리 제곱 합
            float score = 0;
            foreach (var (s, t) in map) score += Vector2.DistanceSquared(topo.Points[topo.HeToPoint[s]].Uv + offset, topo.Points[topo.HeToPoint[t]].Uv);
            if (score < bestScore) { bestScore = score; best = map; }
        }
        return best;
    }

    /// <summary>두 셸 사이의 모든 하프에지 대응(위상 동형)을 열거한다. 대칭 셸은 대칭 차수만큼 나온다. 위상이 다르면 비어 있다.</summary>
    public static IEnumerable<Dictionary<int, int>> AllShellMappings(PolyMesh m, UvTopology topo, int srcShell, int dstShell)
    {
        var srcFaces = ShellFaces(m, topo, srcShell); var dstFaces = ShellFaces(m, topo, dstShell);
        if (srcFaces.Count == 0 || srcFaces.Count != dstFaces.Count) yield break;
        if (ShellSignature(m, topo, srcShell, srcFaces) != ShellSignature(m, topo, dstShell, dstFaces)) yield break;
        var srcHes = new HashSet<int>(); foreach (int f in srcFaces) foreach (int he in FaceHalfEdges(m, f)) srcHes.Add(he);
        var dstHes = new HashSet<int>(); foreach (int f in dstFaces) foreach (int he in FaceHalfEdges(m, f)) dstHes.Add(he);
        // 시작 하프에지: 경계(셸 안 twin 없음)가 있으면 그중 하나(후보가 적다), 없으면 아무거나
        int s0 = srcHes.FirstOrDefault(h => InnerTwin(m, topo, h) < 0, -1); if (s0 < 0) s0 = srcHes.First();
        bool s0Boundary = InnerTwin(m, topo, s0) < 0; int s0Deg = FaceDegree(m, m.Hes[s0].Face);
        foreach (int t0 in dstHes)
        {
            if ((InnerTwin(m, topo, t0) < 0) != s0Boundary || FaceDegree(m, m.Hes[t0].Face) != s0Deg) continue;
            var map = TryMap(m, topo, srcHes, dstHes, s0, t0);
            if (map != null) yield return map;
        }
    }

    private static int FaceDegree(PolyMesh m, int f) { int d = 0; foreach (var _ in FaceHalfEdges(m, f)) d++; return d; }

    /// <summary>s0 → t0에서 시작해 next/셸 안 twin으로 대응을 전파한다. 모순(차수·경계·점 대응 불일치)이 나오면 null.</summary>
    private static Dictionary<int, int>? TryMap(PolyMesh m, UvTopology topo, HashSet<int> srcHes, HashSet<int> dstHes, int s0, int t0)
    {
        var map = new Dictionary<int, int>(srcHes.Count);
        var pointMap = new Dictionary<int, int>();
        var used = new HashSet<int>();
        var queue = new Queue<(int s, int t)>();
        bool Assign(int s, int t)
        {
            if (map.TryGetValue(s, out int prev)) return prev == t;
            if (!dstHes.Contains(t) || !used.Add(t)) return false;
            if (FaceDegree(m, m.Hes[s].Face) != FaceDegree(m, m.Hes[t].Face)) return false;
            int ps = topo.HeToPoint[s], pt = topo.HeToPoint[t];
            if (pointMap.TryGetValue(ps, out int prevP)) { if (prevP != pt) return false; } else pointMap[ps] = pt;
            map[s] = t; queue.Enqueue((s, t));
            return true;
        }
        if (!Assign(s0, t0)) return null;
        while (queue.Count > 0)
        {
            var (s, t) = queue.Dequeue();
            if (!Assign(m.Hes[s].Next, m.Hes[t].Next)) return null;
            int st = InnerTwin(m, topo, s), tt = InnerTwin(m, topo, t);
            if ((st < 0) != (tt < 0)) return null;
            if (st >= 0 && !Assign(st, tt)) return null;
        }
        return map.Count == srcHes.Count ? map : null;
    }

    /// <summary>원본 셸의 UV를 대상 셸에 복사한다(대응은 <see cref="MatchShellTopology"/>). keepPosition이면 대상의 경계 상자 중심을 유지한다.</summary>
    /// <returns>위상이 일치해 복사했으면 true.</returns>
    public static bool CloneShellUvs(PolyMesh m, UvTopology topo, int srcShell, int dstShell, bool keepPosition)
    {
        var map = MatchShellTopology(m, topo, srcShell, dstShell); if (map == null) return false;
        var offset = Vector2.Zero;
        if (keepPosition)
        {
            var (smn, smx) = ShellBounds(topo, srcShell); var (dmn, dmx) = ShellBounds(topo, dstShell);
            offset = (dmn + dmx) * 0.5f - (smn + smx) * 0.5f;
        }
        var target = new Dictionary<int, Vector2>();
        foreach (var (s, t) in map) target[topo.HeToPoint[t]] = topo.Points[topo.HeToPoint[s]].Uv + offset;
        foreach (var (p, uv) in target) SetPointUv(m, topo, p, uv);
        return true;
    }

    /// <summary>원본 셸과 같은 위상의 셸(스택된 것만 또는 전부)에 UV를 복사한다.</summary>
    public static CloneShellReport CloneToSimilarShells(PolyMesh m, UvTopology topo, int srcShell, CloneShellTarget target, bool keepPosition)
    {
        var rep = new CloneShellReport();
        var (smn, smx) = ShellBounds(topo, srcShell);
        for (int s = 0; s < topo.ShellCount; s++)
        {
            if (s == srcShell) continue;
            if (target == CloneShellTarget.Stacked)
            {
                var (mn, mx) = ShellBounds(topo, s);
                // 겹침 = 경계 상자 교집합이 실제 넓이를 가질 때(옆에 붙어 있기만 한 셸은 제외)
                const float eps = 1e-6f;
                bool overlap = mn.X < smx.X - eps && mx.X > smn.X + eps && mn.Y < smx.Y - eps && mx.Y > smn.Y + eps;
                if (!overlap) continue;
            }
            rep.Candidates++;
            if (CloneShellUvs(m, topo, srcShell, s, keepPosition)) rep.Cloned++; else rep.Mismatched++;
        }
        return rep;
    }
}
