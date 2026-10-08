using System.Numerics;
using Cube.Core.Picking;

namespace Cube.Core.Geometry;

/// <summary>조작기 드래그에 쓰는 기하 유틸.</summary>
/// <remarks>
/// W/E/R 조작기(Move/Rotate/Scale)와 UV 2D 조작기가 마우스 위치를 3D 축·평면 위의 값으로 바꿀 때 쓰는 순수 수학 함수 모음이다.
/// Godot에 의존하지 않으며(System.Numerics), 화면 좌표는 좌상단 원점 픽셀, 월드는 오른손 Y-up 규약을 따른다.
/// </remarks>
public static class DragMath
{
    /// <summary>
    /// 직선 L(t)=p+t·a (a 단위벡터)와 광선의 최근접점 파라미터 t. 축이 시선과 거의 평행하면 false.
    /// </summary>
    /// <remarks>
    /// 축 이동 핸들 드래그에 쓴다: 마우스 광선과 축 직선 사이의 최근접점을 구하면 축 위 이동량 t가 된다.
    /// 두 직선 L(t)=p+t·a, R(s)=o+s·d 의 최근접 조건 (L−R)·a = 0, (L−R)·d = 0 을 풀면
    /// t = (b·(d·w) − a·w) / (1 − b²) (w = p − o, b = a·d)이다. 1 − b² 가 0.01 미만(약 84° 이상 평행)이면 해가 불안정해 거부한다.
    /// </remarks>
    /// <param name="p">축 위의 기준점(보통 조작기 피벗, 월드).</param>
    /// <param name="a">축 방향 단위벡터.</param>
    /// <param name="r">마우스 위치에서 역투영한 광선.</param>
    /// <param name="t">축 위 파라미터(p에서 a 방향 거리). 실패 시 0.</param>
    /// <returns>계산에 성공하면 true.</returns>
    public static bool ClosestParamOnAxis(Vector3 p, Vector3 a, in Ray r, out float t)
    {
        // 광선 방향을 정규화해 d·d = 1을 가정한 공식으로 푼다
        var d = Vector3.Normalize(r.Direction);
        var w = p - r.Origin;
        float b = Vector3.Dot(a, d);
        float denom = 1f - b * b;
        // 축과 시선이 거의 평행하면 분모가 0에 가까워 t가 폭주하므로 실패 처리
        if (denom < 0.01f) { t = 0; return false; }
        t = (b * Vector3.Dot(d, w) - Vector3.Dot(a, w)) / denom;
        return true;
    }

    /// <summary>
    /// 광선과 평면(점 planePoint, 법선 normal)의 교점을 구한다. 평면 핸들 드래그·지면 클릭(Joint Tool, Create Polygon) 등에 쓴다.
    /// </summary>
    /// <remarks>
    /// s = ((planePoint − origin)·n) / (dir·n). dir·n 이 1e-6 미만이면 광선이 평면과 평행하므로 실패.
    /// 광선 뒤쪽(s &lt; 0) 교점도 허용한다(직교 카메라 광선의 원점이 평면 앞에 있을 수 있기 때문). 무한대만 거부한다.
    /// </remarks>
    /// <param name="hit">교점(월드). 실패 시 default.</param>
    /// <returns>교점이 있으면 true.</returns>
    public static bool RayPlane(in Ray r, Vector3 planePoint, Vector3 normal, out Vector3 hit)
    {
        float denom = Vector3.Dot(r.Direction, normal);
        hit = default;
        // 평행(분모 ≈ 0)이면 교점 없음
        if (MathF.Abs(denom) < 1e-6f) return false;
        float s = Vector3.Dot(planePoint - r.Origin, normal) / denom;
        // s &lt; 0이면서 유한하지 않은 경우만 거부(뒤쪽 교점 자체는 허용)
        if (s < 0 && !float.IsFinite(s)) return false;
        hit = r.At(s);
        return true;
    }

    /// <summary>화면에서 center 기준 from→to 각도(라디안, 반시계 양수; 화면 y가 아래로 커지므로 부호는 호출자가 조정).</summary>
    /// <remarks>
    /// 회전 링 드래그에서 이전 마우스 위치와 현재 위치가 피벗을 중심으로 이루는 각도 변화량을 구한다.
    /// atan2(외적, 내적)로 −π~π 범위의 부호 있는 각을 돌려주므로 누적해도 360° 경계에서 튀지 않는다.
    /// </remarks>
    public static float ScreenAngle(Vector2 center, Vector2 from, Vector2 to)
    {
        var v0 = from - center; var v1 = to - center;
        return MathF.Atan2(v0.X * v1.Y - v0.Y * v1.X, Vector2.Dot(v0, v1));
    }

    /// <summary>2D 점과 선분 거리.</summary>
    /// <remarks>
    /// 조작기 핸들(화살표 선분) 히트 테스트에 쓴다. 선분 위 최근접 파라미터 u(0~1로 클램프)를
    /// <see cref="RayPicker.ClosestParam"/>로 구해 그 점까지의 거리를 돌려준다(단위는 입력과 같은 픽셀).
    /// </remarks>
    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        float u = RayPicker.ClosestParam(a, b, p);
        return Vector2.Distance(Vector2.Lerp(a, b, u), p);
    }

    /// <summary>볼록 다각형 내부 판정(2D). 퇴화(면적 0) 다각형은 항상 false.</summary>
    /// <remarks>
    /// 모든 변에 대해 (b−a)×(p−a) 의 부호가 같으면 내부다(정점 순서 CW/CCW 모두 허용).
    /// 변 위에 정확히 놓인 경우(외적 ≈ 0)는 판정에서 건너뛴다. 조작기 평면 핸들(사각형)·스케일 상자 히트 테스트에 쓴다.
    /// </remarks>
    public static bool PointInConvexPolygon(Vector2 p, ReadOnlySpan<Vector2> poly)
    {
        // 면적이 0인 다각형(시선과 평행하게 눌린 핸들)은 화면상 선이므로 집을 수 없게 한다
        if (MathF.Abs(PolygonArea(poly)) < 1e-6f) return false;
        // sign: 처음 만난 변의 외적 부호(0 = 아직 없음). 이후 변의 부호가 다르면 외부
        int sign = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Length];
            float c = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (MathF.Abs(c) < 1e-9f) continue;
            int s = c > 0 ? 1 : -1;
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return sign != 0;
    }

    /// <summary>부호 있는 다각형 면적(2D).</summary>
    /// <remarks>신발끈 공식(Shoelace): Σ(x_i·y_{i+1} − x_{i+1}·y_i)/2. 반시계면 양수(수학 좌표계 기준).</remarks>
    public static float PolygonArea(ReadOnlySpan<Vector2> poly)
    {
        float a = 0;
        for (int i = 0; i < poly.Length; i++) { var p = poly[i]; var q = poly[(i + 1) % poly.Length]; a += p.X * q.Y - q.X * p.Y; }
        return a * 0.5f;
    }

    /// <summary>축 a를 Z로 하고 월드 Y를 기준으로 그람-슈미트한 정규직교 기저(행 = 축).</summary>
    /// <remarks>
    /// 면 법선(Normal 축 방향) 조작기 등에서 법선 하나로 좌표축 세 개를 만들 때 쓴다.
    /// 보조 벡터는 기본 월드 Y이고, 법선이 Y와 거의 평행(|z.Y| ≥ 0.95)하면 X를 써서 외적이 퇴화하지 않게 한다.
    /// x = helper × z, y = z × x 로 오른손 정규직교 기저가 된다.
    /// </remarks>
    /// <param name="normal">Z축으로 쓸 방향(정규화하지 않아도 됨).</param>
    /// <returns>(x, y, z) 단위 축.</returns>
    public static (Vector3 x, Vector3 y, Vector3 z) BasisFromNormal(Vector3 normal)
    {
        var z = Vector3.Normalize(normal);
        var helper = MathF.Abs(z.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX;
        var x = Vector3.Normalize(Vector3.Cross(helper, z));
        var y = Vector3.Cross(z, x);
        return (x, y, z);
    }

    /// <summary>행렬의 회전 부분을 정규직교화한 축 세 개.</summary>
    /// <remarks>
    /// 행벡터 규약 행렬이므로 1·2행(M11~M13, M21~M23)이 각각 로컬 X·Y 축이다. 스케일·전단이 섞여 있어도
    /// X를 정규화하고 Y에서 X 성분을 빼(그람-슈미트) 정규화한 뒤 Z = X × Y로 만든다. Local(Object) 축 방향 조작기에 쓴다.
    /// </remarks>
    public static (Vector3 x, Vector3 y, Vector3 z) OrthonormalAxes(Matrix4x4 m)
    {
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var y = new Vector3(m.M21, m.M22, m.M23);
        y = Vector3.Normalize(y - x * Vector3.Dot(y, x));
        var z = Vector3.Cross(x, y);
        return (x, y, z);
    }
}
