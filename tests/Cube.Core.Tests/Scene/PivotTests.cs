using System.Numerics;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Scene;

public class PivotTests
{
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

    private static float Get(Matrix4x4 m, int i) => i switch
    {
        0 => m.M11, 1 => m.M12, 2 => m.M13, 3 => m.M14, 4 => m.M21, 5 => m.M22, 6 => m.M23, 7 => m.M24,
        8 => m.M31, 9 => m.M32, 10 => m.M33, 11 => m.M34, 12 => m.M41, 13 => m.M42, 14 => m.M43, _ => m.M44,
    };
}
