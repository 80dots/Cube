using System.Numerics;
using Cube.Core.Camera;

namespace Cube.Core.Tests.Camera;

/// <summary>
/// Maya식 피벗 기반 카메라 <c>OrbitCamera</c>를 검증한다: 기본 시점, 텀블(Alt+LMB)/트랙(Alt+MMB)/돌리(Alt+RMB·휠),
/// 프레임(F), 직교 프리셋 방향, 원근 ↔ 직교 전환 시 화면 크기 유지.
/// </summary>
public class OrbitCameraTests
{
    /// <summary>두 벡터 사이 거리가 <paramref name="eps"/>보다 작은지 판정하는 근사 비교 도우미.</summary>
    private static bool Near(Vector3 a, Vector3 b, float eps = 1e-3f) => Vector3.Distance(a, b) < eps;

    /// <summary>
    /// 기본 카메라가 (2.8, 2.1, 2.8)에서 원점(피벗)을 바라보고, 위쪽 벡터가 대체로 +Y이며, 거리가 약 4.49인지 확인한다.
    /// </summary>
    [Fact]
    public void MayaDefault_LooksAtOriginFrom28_21_28()
    {
        var c = OrbitCamera.MayaDefault();
        Assert.True(Near(c.Eye, new Vector3(2.8f, 2.1f, 2.8f), 1e-2f), $"eye {c.Eye}");
        Assert.True(Near(c.Pivot, Vector3.Zero));
        Assert.True(Vector3.Dot(c.Forward, Vector3.Normalize(-c.Eye)) > 0.9999f);
        Assert.True(c.Up.Y > 0.5f);
        Assert.True(MathF.Abs(c.Distance - 4.49f) < 0.02f);
    }

    /// <summary>텀블은 피벗을 중심으로 궤도 회전만 하므로 거리·피벗이 그대로이고 여전히 피벗을 바라봐야 한다.</summary>
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

    /// <summary>아주 큰 세로 드래그에도 피치가 ±MaxPitch로 제한되어 극점에서 뒤집히지 않는지 확인한다.</summary>
    [Fact]
    public void Tumble_ClampsPitch()
    {
        var c = OrbitCamera.MayaDefault();
        c.Tumble(0, 100000);
        Assert.True(c.Pitch >= -OrbitCamera.MaxPitch - 1e-6f && c.Pitch <= OrbitCamera.MaxPitch + 1e-6f);
    }

    /// <summary>
    /// 트랙(팬)은 피벗을 시선에 수직인 평면 안에서만 옮겨야 하고, 오른쪽 드래그는 장면을 오른쪽으로 끌므로 피벗은 왼쪽(-Right)으로 가야 한다.
    /// </summary>
    [Fact]
    public void Track_MovesPivotInViewPlane()
    {
        var c = OrbitCamera.MayaDefault();
        var fwd = c.Forward;
        c.Track(100, 0, 900);
        Assert.True(MathF.Abs(Vector3.Dot(c.Pivot, fwd)) < 1e-3f); // 시선 방향 성분 없음
        Assert.True(Vector3.Dot(c.Pivot, c.Right) < 0);           // 오른쪽 드래그 → 장면이 오른쪽으로 → 피벗은 왼쪽
    }

    /// <summary>
    /// 돌리는 지수적(같은 드래그량이면 같은 비율로 거리 변화)이어야 하고, 반대 방향이면 멀어지며,
    /// 아무리 당겨도 MinDistance 아래로 내려가지 않는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// (-1..1) 바운드를 프레임하면 피벗이 중심으로 오고, 거리 = 외접구 반지름 / sin(FOV/2) × 여유 1.05가 되는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// 직교 프리셋(Front/Top/Side/Back/Left/Bottom)의 시선 방향이 Maya 규약과 같은지, 직교 프리셋에서는 텀블이 무시되는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// 원근 → 직교 전환 시 OrthoSize = 2·거리·tan(FOV/2)로 같은 화면 크기를 유지하고, AllowOrthoTumble이면 직교에서도 텀블이 되며,
    /// 다시 원근으로 돌아오면 원래 거리가 복원되는지 확인한다.
    /// </summary>
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
