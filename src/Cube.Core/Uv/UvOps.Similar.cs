using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>Select Identical Shells의 모양 비교 방식.</summary>
public enum ShellShapeMatch
{
    /// <summary>위치만 달라도 됨(평행 이동 제외 완전 일치, 같은 방향).</summary>
    Exact,
    /// <summary>회전·뒤집기까지 허용(합동, 같은 크기).</summary>
    Congruent,
}

/// <summary>
/// 셸 찾기(v0.0.69): 원본 셸과 **구조(위상)가 같은** 셸(Similar)과 그중 **모양까지 같은** 셸(Identical)을 찾는다.
/// 위상 대응은 <see cref="UvOps.AllShellMappings"/>(하프에지 그래프 동형, 대칭 셸은 모든 자기동형), 모양 비교는 대응 점끼리 평행 이동(Exact) 또는
/// 2D 프로크루스테스 강체 맞춤(Congruent, 반사 허용·스케일 없음) 뒤 최대 거리가 허용 오차 이하인지로 판단한다.
/// </summary>
public static partial class UvOps
{
    /// <summary>원본 셸과 위상이 같은 다른 셸 목록(모양 무관).</summary>
    public static List<int> FindSimilarShells(PolyMesh m, UvTopology topo, int srcShell)
    {
        var list = new List<int>();
        for (int s = 0; s < topo.ShellCount; s++)
            if (s != srcShell && MatchShellTopology(m, topo, srcShell, s) != null) list.Add(s);
        return list;
    }

    /// <summary>원본 셸과 위상·모양이 모두 같은 다른 셸 목록.</summary>
    public static List<int> FindIdenticalShells(PolyMesh m, UvTopology topo, int srcShell, ShellShapeMatch match, float tolerance = 0.001f)
    {
        var list = new List<int>();
        for (int s = 0; s < topo.ShellCount; s++)
        {
            if (s == srcShell) continue;
            // 대칭 셸은 대응이 여럿이므로(회전 자기동형) 하나라도 모양이 맞으면 같은 셸
            if (AllShellMappings(m, topo, srcShell, s).Any(map => ShellShapeMatches(topo, map, match, tolerance))) list.Add(s);
        }
        return list;
    }

    /// <summary>위상 대응(src 하프에지 → dst 하프에지)으로 이어진 점들의 모양이 같은지.</summary>
    public static bool ShellShapeMatches(UvTopology topo, Dictionary<int, int> map, ShellShapeMatch match, float tolerance)
    {
        // 점 대응(중복 제거)
        var pairs = new Dictionary<int, int>();
        foreach (var (s, t) in map) pairs[topo.HeToPoint[s]] = topo.HeToPoint[t];
        if (pairs.Count == 0) return false;
        var a = pairs.Keys.Select(p => topo.Points[p].Uv).ToArray();
        var b = pairs.Values.Select(p => topo.Points[p].Uv).ToArray();
        var ca = Vector2.Zero; var cb = Vector2.Zero;
        foreach (var v in a) ca += v; foreach (var v in b) cb += v;
        ca /= a.Length; cb /= b.Length;
        float tol2 = tolerance * tolerance;
        if (match == ShellShapeMatch.Exact)
        {
            for (int i = 0; i < a.Length; i++) if (Vector2.DistanceSquared(a[i] - ca, b[i] - cb) > tol2) return false;
            return true;
        }
        // 합동: 반사 유무 각각 최적 회전을 구해(2D 프로크루스테스: 교차 공분산의 각도) 둘 중 하나라도 맞으면 같다
        foreach (bool flip in new[] { false, true })
        {
            float sxx = 0, sxy = 0, syx = 0, syy = 0;
            for (int i = 0; i < a.Length; i++)
            {
                var p = a[i] - ca; if (flip) p.X = -p.X;
                var q = b[i] - cb;
                sxx += p.X * q.X; sxy += p.X * q.Y; syx += p.Y * q.X; syy += p.Y * q.Y;
            }
            float angle = MathF.Atan2(sxy - syx, sxx + syy);
            float c = MathF.Cos(angle), sn = MathF.Sin(angle);
            bool ok = true;
            for (int i = 0; i < a.Length && ok; i++)
            {
                var p = a[i] - ca; if (flip) p.X = -p.X;
                var r = new Vector2(p.X * c - p.Y * sn, p.X * sn + p.Y * c);
                if (Vector2.DistanceSquared(r, b[i] - cb) > tol2) ok = false;
            }
            if (ok) return true;
        }
        return false;
    }
}
