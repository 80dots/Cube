using System.Numerics;
using Cube.Core.Geometry;
using Cube.Core.Picking;

namespace Cube.Core.Tests.Geometry;

public class DragMathTests
{
    [Fact]
    public void ClosestParamOnAxis_FindsPointUnderRay()
    {
        var cam = CameraProjection.Perspective(new Vector3(3, 2, 5), Vector3.Zero, Vector3.UnitY, 45, new Vector2(1000, 800));
        var target = new Vector3(0.7f, 0, 0); // X축 위의 점
        var px = cam.Project(target, out _)!.Value;
        var ray = cam.Unproject(px);
        Assert.True(DragMath.ClosestParamOnAxis(Vector3.Zero, Vector3.UnitX, ray, out float t));
        Assert.True(MathF.Abs(t - 0.7f) < 1e-3f, $"t={t}");
    }

    [Fact]
    public void ClosestParamOnAxis_RejectsAxisParallelToView()
    {
        var ray = new Ray(new Vector3(0, 0, 5), -Vector3.UnitZ);
        Assert.False(DragMath.ClosestParamOnAxis(Vector3.Zero, Vector3.UnitZ, ray, out _));
    }

    [Fact]
    public void RayPlane_Hits()
    {
        var ray = new Ray(new Vector3(1, 5, 1), Vector3.Normalize(new Vector3(0, -1, 0)));
        Assert.True(DragMath.RayPlane(ray, Vector3.Zero, Vector3.UnitY, out var hit));
        Assert.True(Vector3.Distance(hit, new Vector3(1, 0, 1)) < 1e-5f);
        Assert.False(DragMath.RayPlane(new Ray(Vector3.Zero, Vector3.UnitX), Vector3.Zero, Vector3.UnitY, out _));
    }

    [Fact]
    public void ScreenAngle_QuarterTurn()
    {
        float a = DragMath.ScreenAngle(new Vector2(100, 100), new Vector2(200, 100), new Vector2(100, 200));
        Assert.True(MathF.Abs(a - MathF.PI / 2) < 1e-5f);
    }

    [Fact]
    public void PointInConvexPolygon_Square()
    {
        var sq = new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) };
        Assert.True(DragMath.PointInConvexPolygon(new Vector2(5, 5), sq));
        Assert.False(DragMath.PointInConvexPolygon(new Vector2(15, 5), sq));
        // 퇴화(선분) 다각형은 어떤 점도 포함하지 않는다
        var line = new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(20, 0), new Vector2(30, 0) };
        Assert.False(DragMath.PointInConvexPolygon(new Vector2(5, 0), line));
        Assert.False(DragMath.PointInConvexPolygon(new Vector2(500, 500), line));
    }

    [Fact]
    public void OrthonormalAxes_FromScaledRotation()
    {
        var m = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateRotationY(0.7f);
        var (x, y, z) = DragMath.OrthonormalAxes(m);
        Assert.True(MathF.Abs(x.Length() - 1) < 1e-5f && MathF.Abs(y.Length() - 1) < 1e-5f && MathF.Abs(z.Length() - 1) < 1e-5f);
        Assert.True(MathF.Abs(Vector3.Dot(x, y)) < 1e-5f && MathF.Abs(Vector3.Dot(y, z)) < 1e-5f);
        Assert.True(Vector3.Distance(Vector3.Cross(x, y), z) < 1e-5f);
    }
}
