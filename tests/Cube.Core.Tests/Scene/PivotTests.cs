using System.Numerics;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Scene;

/// <summary>
/// <c>Transform3</c>의 피벗(오브젝트 공간 회전/스케일 피벗, 행렬 T(-P)·S·R·T(P+T)) 수학을 검증한다.
/// 행렬 ↔ TRS 왕복, 월드를 유지하며 피벗만 옮기기(Center/Edit Pivot), 피벗 기준 회전을 다룬다.
/// </summary>
public class PivotTests
{
    /// <summary>
    /// 이동·회전·비균등 스케일·피벗이 모두 있는 트랜스폼을 행렬로 만든 뒤 <c>FromMatrix(m, pivot)</c>로 되돌렸을 때
    /// 이동/스케일이 원래 값과 같고 다시 만든 행렬의 16개 성분이 모두 일치하는지 확인한다.
    /// 기존 노드의 Local을 행렬에서 다시 만들 때 피벗이 깨지지 않아야 하기 때문이다.
    /// </summary>
    [Fact]
    public void Transform_WithPivot_RoundTripsThroughMatrix()
    {
        var t = new Transform3(new Vector3(1, 2, 3), new Vector3(30, -40, 10), new Vector3(1, 2, 0.5f), new Vector3(0.5f, 0.25f, -1));
        var m = t.ToMatrix();
        var back = Transform3.FromMatrix(m, t.Pivot);
        Assert.True(Vector3.Distance(back.Translation, t.Translation) < 1e-4f, $"{back.Translation} vs {t.Translation}");
        Assert.True(Vector3.Distance(back.Scale, t.Scale) < 1e-4f);
        var m2 = back.ToMatrix();
        for (int i = 0; i < 16; i++) Assert.True(MathF.Abs(Get(m, i) - Get(m2, i)) < 1e-4f, $"m[{i}] {Get(m, i)} vs {Get(m2, i)}");
    }

    /// <summary>
    /// <c>WithPivotKeepingMatrix</c>로 피벗만 (0.5,0.5,0.5)로 옮겨도 전체 행렬(월드 배치)은 변하지 않아야 하고,
    /// 새 피벗의 월드 위치는 Pivot + Translation이며 원래 행렬로 옮긴 같은 점과 일치해야 한다.
    /// </summary>
    [Fact]
    public void WithPivotKeepingMatrix_KeepsWorld_AndMovesPivotPoint()
    {
        var t = new Transform3(new Vector3(1, 0, 0), new Vector3(0, 90, 0), Vector3.One);
        var m = t.ToMatrix();
        var t2 = t.WithPivotKeepingMatrix(new Vector3(0.5f, 0.5f, 0.5f));
        var m2 = t2.ToMatrix();
        for (int i = 0; i < 16; i++) Assert.True(MathF.Abs(Get(m, i) - Get(m2, i)) < 1e-4f, $"m[{i}]");
        // 피벗의 월드 위치 = 오브젝트 공간 피벗을 행렬로 옮긴 점 = Pivot + Translation
        var pivotWorld = Vector3.Transform(t2.Pivot, m2);
        Assert.True(Vector3.Distance(pivotWorld, t2.Pivot + t2.Translation) < 1e-4f);
        Assert.True(Vector3.Distance(pivotWorld, Vector3.Transform(new Vector3(0.5f, 0.5f, 0.5f), m)) < 1e-4f);
    }

    /// <summary>
    /// 피벗이 (1,0,0)인 트랜스폼을 Z축 90° 회전시키면 피벗 점의 월드 위치는 그대로이고,
    /// 오브젝트 원점은 피벗을 중심으로 돌아 (1,-1,0)으로 가야 한다. 피벗이 회전 중심으로 동작하는지 확인한다.
    /// </summary>
    [Fact]
    public void RotationAboutPivot_KeepsPivotPointFixed()
    {
        var t = new Transform3(Vector3.Zero, Vector3.Zero, Vector3.One, new Vector3(1, 0, 0));
        var pivotBefore = Vector3.Transform(t.Pivot, t.ToMatrix());
        t.RotationDegrees = new Vector3(0, 0, 90);
        var pivotAfter = Vector3.Transform(t.Pivot, t.ToMatrix());
        Assert.True(Vector3.Distance(pivotBefore, pivotAfter) < 1e-5f);
        // 원점(오브젝트 공간 0)은 피벗을 중심으로 회전한다: (0,0,0) → (1,-1,0)
        var origin = Vector3.Transform(Vector3.Zero, t.ToMatrix());
        Assert.True(Vector3.Distance(origin, new Vector3(1, -1, 0)) < 1e-4f, $"{origin}");
    }

    /// <summary>행렬 성분을 행 우선 순서의 0~15 인덱스로 읽는 도우미(M11=0 … M44=15). 성분별 비교 루프에서 쓴다.</summary>
    private static float Get(Matrix4x4 m, int i) => i switch
    {
        0 => m.M11, 1 => m.M12, 2 => m.M13, 3 => m.M14, 4 => m.M21, 5 => m.M22, 6 => m.M23, 7 => m.M24,
        8 => m.M31, 9 => m.M32, 10 => m.M33, 11 => m.M34, 12 => m.M41, 13 => m.M42, 14 => m.M43, _ => m.M44,
    };
}
