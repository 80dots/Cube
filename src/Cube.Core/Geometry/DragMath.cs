using System.Numerics;
using Cube.Core.Picking;

namespace Cube.Core.Geometry;

/// <summary>조작기 드래그에 쓰는 기하 유틸.</summary>
public static class DragMath
{
    /// <summary>
    /// 직선 L(t)=p+t·a (a 단위벡터)와 광선의 최근접점 파라미터 t. 축이 시선과 거의 평행하면 false.
    /// </summary>
    public static bool ClosestParamOnAxis(Vector3 p, Vector3 a, in Ray r, out float t)
    {
        var d = Vector3.Normalize(r.Direction);
        var w = p - r.Origin;
        float b = Vector3.Dot(a, d);
        float denom = 1f - b * b;
        if (denom < 0.01f) { t = 0; return false; }
        t = (b * Vector3.Dot(d, w) - Vector3.Dot(a, w)) / denom;
        return true;
    }

    public static bool RayPlane(in Ray r, Vector3 planePoint, Vector3 normal, out Vector3 hit)
    {
        float denom = Vector3.Dot(r.Direction, normal);
        hit = default;
        if (MathF.Abs(denom) < 1e-6f) return false;
        float s = Vector3.Dot(planePoint - r.Origin, normal) / denom;
        if (s < 0 && !float.IsFinite(s)) return false;
        hit = r.At(s);
        return true;
    }

    /// <summary>화면에서 center 기준 from→to 각도(라디안, 반시계 양수; 화면 y가 아래로 커지므로 부호는 호출자가 조정).</summary>
    public static float ScreenAngle(Vector2 center, Vector2 from, Vector2 to)
    {
        var v0 = from - center; var v1 = to - center;
        return MathF.Atan2(v0.X * v1.Y - v0.Y * v1.X, Vector2.Dot(v0, v1));
    }

    /// <summary>2D 점과 선분 거리.</summary>
    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        float u = RayPicker.ClosestParam(a, b, p);
        return Vector2.Distance(Vector2.Lerp(a, b, u), p);
    }

    /// <summary>볼록 다각형 내부 판정(2D).</summary>
    public static bool PointInConvexPolygon(Vector2 p, ReadOnlySpan<Vector2> poly)
    {
        int sign = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Length];
            float c = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (MathF.Abs(c) < 1e-9f) continue;
            int s = c > 0 ? 1 : -1;
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return true;
    }

    /// <summary>축 a를 Z로 하고 월드 Y를 기준으로 그람-슈미트한 정규직교 기저(행 = 축).</summary>
    public static (Vector3 x, Vector3 y, Vector3 z) BasisFromNormal(Vector3 normal)
    {
        var z = Vector3.Normalize(normal);
        var helper = MathF.Abs(z.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX;
        var x = Vector3.Normalize(Vector3.Cross(helper, z));
        var y = Vector3.Cross(z, x);
        return (x, y, z);
    }

    /// <summary>행렬의 회전 부분을 정규직교화한 축 세 개.</summary>
    public static (Vector3 x, Vector3 y, Vector3 z) OrthonormalAxes(Matrix4x4 m)
    {
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var y = new Vector3(m.M21, m.M22, m.M23);
        y = Vector3.Normalize(y - x * Vector3.Dot(y, x));
        var z = Vector3.Cross(x, y);
        return (x, y, z);
    }
}
