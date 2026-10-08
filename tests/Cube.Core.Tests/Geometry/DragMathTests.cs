using System.Numerics;
using Cube.Core.Geometry;
using Cube.Core.Picking;

namespace Cube.Core.Tests.Geometry;

/// <summary>
/// 조작기(기즈모) 드래그에 쓰는 <c>DragMath</c> 수학 함수들을 검증한다: 축 위 최근접 파라미터, 레이-평면 교차,
/// 화면 각도, 볼록 다각형 내부 판정, 스케일이 섞인 행렬의 정규직교 축 추출.
/// </summary>
public class DragMathTests
{
    /// <summary>
    /// X축 위 점(0.7,0,0)을 화면에 투영하고 그 픽셀로 레이를 다시 쏘았을 때, 축 위 최근접 파라미터 t가 0.7로 복원되는지 확인한다.
    /// 이동 조작기 축 드래그가 커서 아래 점을 정확히 따라가는 근거다.
    /// </summary>
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

    /// <summary>축이 시선 방향과 평행하면 해가 불안정하므로 false를 돌려 드래그를 거부해야 한다.</summary>
    [Fact]
    public void ClosestParamOnAxis_RejectsAxisParallelToView()
    {
        var ray = new Ray(new Vector3(0, 0, 5), -Vector3.UnitZ);
        Assert.False(DragMath.ClosestParamOnAxis(Vector3.Zero, Vector3.UnitZ, ray, out _));
    }

    /// <summary>아래를 향하는 레이가 XZ 평면과 (1,0,1)에서 만나고, 평면과 평행한 레이는 교차 없음(false)으로 처리되는지 확인한다.</summary>
    [Fact]
    public void RayPlane_Hits()
    {
        var ray = new Ray(new Vector3(1, 5, 1), Vector3.Normalize(new Vector3(0, -1, 0)));
        Assert.True(DragMath.RayPlane(ray, Vector3.Zero, Vector3.UnitY, out var hit));
        Assert.True(Vector3.Distance(hit, new Vector3(1, 0, 1)) < 1e-5f);
        Assert.False(DragMath.RayPlane(new Ray(Vector3.Zero, Vector3.UnitX), Vector3.Zero, Vector3.UnitY, out _));
    }

    /// <summary>중심 (100,100)에서 오른쪽 점 → 아래쪽 점으로 가는 화면 각도가 π/2인지 확인한다(회전 조작기 링 드래그 각도).</summary>
    [Fact]
    public void ScreenAngle_QuarterTurn()
    {
        float a = DragMath.ScreenAngle(new Vector2(100, 100), new Vector2(200, 100), new Vector2(100, 200));
        Assert.True(MathF.Abs(a - MathF.PI / 2) < 1e-5f);
    }

    /// <summary>
    /// 정사각형 내부/외부 판정이 맞는지, 그리고 넓이가 0인 퇴화 다각형(일직선)은 어떤 점도 포함하지 않는지 확인한다
    /// (화면에서 납작해진 조작기 핸들이 오클릭되지 않도록).
    /// </summary>
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

    /// <summary>
    /// 비균등 스케일 × Y 회전 행렬에서 뽑은 세 축이 단위 길이이고 서로 직교하며 오른손계(x×y=z)인지 확인한다(Local 축 조작기 방향).
    /// </summary>
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
