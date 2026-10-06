using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>2D 단순 다각형(구멍 없음)의 귀 자르기 삼각분할.</summary>
public static class EarClipping
{
    /// <summary>
    /// 반시계 방향 2D 다각형을 삼각형 인덱스(로컬 0..n-1 기준)로 분할한다.
    /// 시계 방향이면 내부적으로 뒤집어 처리한다. 실패(퇴화) 시 팬 분할로 폴백.
    /// </summary>
    public static void Triangulate(ReadOnlySpan<Vector2> poly, List<int> outIndices)
    {
        int n = poly.Length;
        if (n < 3) return;
        if (n == 3) { outIndices.Add(0); outIndices.Add(1); outIndices.Add(2); return; }

        float area = 0;
        for (int i = 0; i < n; i++) { var a = poly[i]; var b = poly[(i + 1) % n]; area += a.X * b.Y - b.X * a.Y; }
        bool ccw = area > 0;

        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) idx.Add(ccw ? i : n - 1 - i);
        int outStart = outIndices.Count;

        int guard = 0;
        while (idx.Count > 3 && guard++ < n * n)
        {
            bool found = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int ip = idx[(i + idx.Count - 1) % idx.Count], ic = idx[i], inx = idx[(i + 1) % idx.Count];
                var a = poly[ip]; var b = poly[ic]; var c = poly[inx];
                if (Cross(b - a, c - a) <= 1e-12f) continue; // 오목 또는 퇴화
                bool inside = false;
                for (int j = 0; j < idx.Count; j++)
                {
                    int k = idx[j];
                    if (k == ip || k == ic || k == inx) continue;
                    if (PointInTriangle(poly[k], a, b, c)) { inside = true; break; }
                }
                if (inside) continue;
                outIndices.Add(ip); outIndices.Add(ic); outIndices.Add(inx);
                idx.RemoveAt(i);
                found = true;
                break;
            }
            if (!found) break;
        }
        if (idx.Count == 3) { outIndices.Add(idx[0]); outIndices.Add(idx[1]); outIndices.Add(idx[2]); }
        else if (idx.Count > 3)
        {
            // 폴백: 팬
            for (int i = 1; i + 1 < idx.Count; i++) { outIndices.Add(idx[0]); outIndices.Add(idx[i]); outIndices.Add(idx[i + 1]); }
        }
        // 입력이 시계 방향이었으면 삼각형 방향을 입력과 같게 되돌린다
        if (!ccw)
            for (int i = outStart; i + 2 < outIndices.Count; i += 3)
                (outIndices[i], outIndices[i + 2]) = (outIndices[i + 2], outIndices[i]);
    }

    public static bool IsConvex(ReadOnlySpan<Vector2> poly)
    {
        int n = poly.Length; if (n < 4) return true;
        int sign = 0;
        for (int i = 0; i < n; i++)
        {
            float c = Cross(poly[(i + 1) % n] - poly[i], poly[(i + 2) % n] - poly[(i + 1) % n]);
            if (MathF.Abs(c) < 1e-12f) continue;
            int s = c > 0 ? 1 : -1;
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return true;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>면 노멀에 수직인 2D 기저를 만든다.</summary>
    public static void PlaneBasis(Vector3 normal, out Vector3 u, out Vector3 v)
    {
        var n = Vector3.Normalize(normal);
        var helper = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(helper, n));
        v = Vector3.Cross(n, u);
    }
}
