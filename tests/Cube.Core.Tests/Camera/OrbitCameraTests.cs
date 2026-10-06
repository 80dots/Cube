using System.Numerics;
using Cube.Core.Camera;

namespace Cube.Core.Tests.Camera;

public class OrbitCameraTests
{
    private static bool Near(Vector3 a, Vector3 b, float eps = 1e-3f) => Vector3.Distance(a, b) < eps;

    [Fact]
    public void MayaDefault_LooksAtOriginFrom28_21_28()
    {
        var c = OrbitCamera.MayaDefault();
        Assert.True(Near(c.Eye, new Vector3(28, 21, 28), 1e-2f), $"eye {c.Eye}");
        Assert.True(Near(c.Pivot, Vector3.Zero));
        Assert.True(Vector3.Dot(c.Forward, Vector3.Normalize(-c.Eye)) > 0.9999f);
        Assert.True(c.Up.Y > 0.5f);
        Assert.True(MathF.Abs(c.Distance - 44.9f) < 0.1f);
    }

    [Fact]
    public void Tumble_KeepsDistanceAndPivot()
    {
        var c = OrbitCamera.MayaDefault();
        float d = c.Distance;
        c.Tumble(100, -40);
        Assert.Equal(d, c.Distance);
        Assert.True(Near(c.Pivot, Vector3.Zero));
        Assert.True(MathF.Abs(Vector3.Distance(c.Eye, c.Pivot) - d) < 1e-3f);
        Assert.True(Vector3.Dot(c.Forward, Vector3.Normalize(c.Pivot - c.Eye)) > 0.9999f);
    }

    [Fact]
    public void Tumble_ClampsPitch()
    {
        var c = OrbitCamera.MayaDefault();
        c.Tumble(0, 100000);
        Assert.True(c.Pitch >= -OrbitCamera.MaxPitch - 1e-6f && c.Pitch <= OrbitCamera.MaxPitch + 1e-6f);
    }

    [Fact]
    public void Track_MovesPivotInViewPlane()
    {
        var c = OrbitCamera.MayaDefault();
        var fwd = c.Forward;
        c.Track(100, 0, 900);
        Assert.True(MathF.Abs(Vector3.Dot(c.Pivot, fwd)) < 1e-3f); // 시선 방향 성분 없음
        Assert.True(Vector3.Dot(c.Pivot, c.Right) < 0);           // 오른쪽 드래그 → 장면이 오른쪽으로 → 피벗은 왼쪽
    }

    [Fact]
    public void Dolly_IsExponential_AndMovesTowardPivot()
    {
        var c = OrbitCamera.MayaDefault();
        float d0 = c.Distance;
        c.Dolly(100, 0);
        float d1 = c.Distance;
        Assert.True(d1 < d0);
        c.Dolly(100, 0);
        Assert.True(MathF.Abs(c.Distance / d1 - d1 / d0) < 1e-4f);
        c.Dolly(-100000, 0);
        Assert.True(c.Distance > d0);
        c.Dolly(100000000, 0);
        Assert.Equal(OrbitCamera.MinDistance, c.Distance);
    }

    [Fact]
    public void Frame_FitsBounds()
    {
        var c = OrbitCamera.MayaDefault();
        c.Frame(new Vector3(-1, -1, -1), new Vector3(1, 1, 1), 16f / 9f);
        Assert.True(Near(c.Pivot, Vector3.Zero));
        float r = MathF.Sqrt(3);
        float fov = 45 * MathF.PI / 180;
        Assert.True(MathF.Abs(c.Distance - r / MathF.Sin(fov / 2) * 1.05f) < 1e-3f);
    }

    [Fact]
    public void OrthoPresets_FaceTheRightWay()
    {
        var front = OrbitCamera.Preset(ViewKind.Front);
        Assert.True(Near(front.Forward, -Vector3.UnitZ));   // 앞에서(+Z) -Z를 봄
        var top = OrbitCamera.Preset(ViewKind.Top);
        Assert.True(Near(top.Forward, -Vector3.UnitY));     // 위에서 아래
        var side = OrbitCamera.Preset(ViewKind.Side);
        Assert.True(Near(side.Forward, -Vector3.UnitX));    // 오른쪽(+X)에서 -X를 봄
        front.Tumble(100, 100);
        Assert.Equal(0f, front.Yaw);                        // ortho에서는 텀블 무시
        Assert.True(Near(OrbitCamera.Preset(ViewKind.Back).Forward, Vector3.UnitZ));
        Assert.True(Near(OrbitCamera.Preset(ViewKind.Left).Forward, Vector3.UnitX));
        Assert.True(Near(OrbitCamera.Preset(ViewKind.Bottom).Forward, Vector3.UnitY));
    }

    [Fact]
    public void ToggleOrtho_KeepsFraming_AndPerspCanTumbleInOrtho()
    {
        var c = OrbitCamera.MayaDefault();
        c.AllowOrthoTumble = true;
        float d = c.Distance;
        c.ToggleOrtho();
        Assert.True(c.IsOrtho);
        Assert.True(MathF.Abs(c.OrthoSize - 2 * d * MathF.Tan(c.FovDegrees * MathF.PI / 360f)) < 1e-3f);
        float yaw = c.Yaw; c.Tumble(50, 0);
        Assert.NotEqual(yaw, c.Yaw);
        c.ToggleOrtho();
        Assert.False(c.IsOrtho);
        Assert.True(MathF.Abs(c.Distance - d) < 1e-3f);
    }
}
