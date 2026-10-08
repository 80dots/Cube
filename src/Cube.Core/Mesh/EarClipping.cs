using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>
/// 2D 단순 다각형(구멍 없음)의 귀 자르기 삼각분할.
/// n각형 면을 표시용 삼각형으로 나눌 때(<see cref="MeshTessellator"/>), Triangulate 연산 등에서 쓴다.
/// 3D 면은 <see cref="PlaneBasis"/>로 면 평면 2D 좌표로 투영한 뒤 넘긴다.
/// </summary>
public static class EarClipping
{
    /// <summary>
    /// 반시계 방향 2D 다각형을 삼각형 인덱스(로컬 0..n-1 기준)로 분할한다.
    /// 시계 방향이면 내부적으로 뒤집어 처리한다. 실패(퇴화) 시 팬 분할로 폴백.
    /// 알고리즘: 남은 꼭짓점 목록을 돌며 "귀"(볼록 꼭짓점이고 그 삼각형 안에 다른 꼭짓점이 없음)를 찾아
    /// 삼각형으로 떼어 내고 꼭짓점을 목록에서 지운다. 3개가 남을 때까지 반복(최악 O(n³)).
    /// 결과 삼각형의 감김 방향은 입력 다각형과 같다.
    /// </summary>
    /// <param name="poly">다각형 꼭짓점(순서대로 루프).</param>
    /// <param name="outIndices">삼각형 인덱스를 3개씩 덧붙일 목록(기존 내용은 유지).</param>
    public static void Triangulate(ReadOnlySpan<Vector2> poly, List<int> outIndices)
    {
        int n = poly.Length;
        if (n < 3) return;
        if (n == 3) { outIndices.Add(0); outIndices.Add(1); outIndices.Add(2); return; }

        // 신발끈 공식(부호 있는 넓이 ×2)으로 감김 방향 판정: 양수 = 반시계
        float area = 0;
        for (int i = 0; i < n; i++) { var a = poly[i]; var b = poly[(i + 1) % n]; area += a.X * b.Y - b.X * a.Y; }
        bool ccw = area > 0;

        // 작업 목록: 항상 반시계 순서로 꼭짓점 인덱스를 둔다(시계면 역순)
        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) idx.Add(ccw ? i : n - 1 - i);
        // 이번 호출이 추가한 삼각형의 시작 위치(마지막에 방향을 되돌릴 범위)
        int outStart = outIndices.Count;

        // guard: 퇴화 입력에서 무한 루프 방지(n² 회 상한)
        int guard = 0;
        while (idx.Count > 3 && guard++ < n * n)
        {
            bool found = false;
            for (int i = 0; i < idx.Count; i++)
            {
                // 후보 귀: 이전(ip) - 현재(ic) - 다음(inx) 꼭짓점
                int ip = idx[(i + idx.Count - 1) % idx.Count], ic = idx[i], inx = idx[(i + 1) % idx.Count];
                var a = poly[ip]; var b = poly[ic]; var c = poly[inx];
                if (Cross(b - a, c - a) <= 1e-12f) continue; // 오목 또는 퇴화
                // 남은 다른 꼭짓점이 이 삼각형 안에 있으면 귀가 아니다
                bool inside = false;
                for (int j = 0; j < idx.Count; j++)
                {
                    int k = idx[j];
                    if (k == ip || k == ic || k == inx) continue;
                    if (PointInTriangle(poly[k], a, b, c)) { inside = true; break; }
                }
                if (inside) continue;
                // 귀를 잘라 삼각형으로 내보내고 현재 꼭짓점을 제거
                outIndices.Add(ip); outIndices.Add(ic); outIndices.Add(inx);
                idx.RemoveAt(i);
                found = true;
                break;
            }
            // 한 바퀴 돌아도 귀가 없으면(자기 교차 등) 중단하고 폴백
            if (!found) break;
        }
        // 마지막 남은 삼각형
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

    /// <summary>
    /// 다각형이 볼록인지 검사한다. 연속한 두 변의 외적 부호가 모두 같으면 볼록(거의 0인 일직선 꼭짓점은 무시).
    /// 볼록이면 호출자가 귀 자르기 대신 값싼 팬 분할을 쓴다. 3각형 이하는 항상 볼록.
    /// </summary>
    public static bool IsConvex(ReadOnlySpan<Vector2> poly)
    {
        int n = poly.Length; if (n < 4) return true;
        // 처음 나온 0 아닌 회전 방향(+1 = 좌회전, -1 = 우회전)을 기준으로 삼는다
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

    /// <summary>2D 외적(z 성분). 양수면 b가 a의 왼쪽(반시계) 방향.</summary>
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>
    /// 점 p가 삼각형 abc 안(경계 포함)에 있는지. 세 변에 대한 외적 부호가 섞여 있지 않으면 내부다(감김 방향 무관).
    /// </summary>
    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>면 노멀에 수직인 2D 기저를 만든다.</summary>
    /// <param name="normal">면 노멀(정규화되지 않아도 됨, 0이면 안 됨).</param>
    /// <param name="u">평면 위 첫 번째 단위 축.</param>
    /// <param name="v">평면 위 두 번째 단위 축(n × u). (u, v, n)이 오른손 좌표계라 투영해도 감김 방향이 유지된다.</param>
    public static void PlaneBasis(Vector3 normal, out Vector3 u, out Vector3 v)
    {
        var n = Vector3.Normalize(normal);
        // 노멀과 거의 평행하지 않은 보조 축을 골라 외적이 퇴화하지 않게 한다
        var helper = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(helper, n));
        v = Vector3.Cross(n, u);
    }
}
