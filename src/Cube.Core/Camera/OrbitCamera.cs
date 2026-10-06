using System.Numerics;

namespace Cube.Core.Camera;

public enum ViewKind { Persp, Front, Side, Top }

/// <summary>
/// Maya 식 피벗(center of interest) 기반 카메라 상태. 순수 수학이며 Godot Camera3D에 적용하는 쪽은 App이다.
/// 좌표계: 오른손, Y-up. 카메라는 로컬 -Z를 본다.
/// </summary>
public sealed class OrbitCamera
{
    public Vector3 Pivot;
    public float Distance = 44.9f;
    public float Yaw;      // 라디안, Y축 회전
    public float Pitch;    // 라디안, X축 회전(음수 = 아래를 봄)
    public bool IsOrtho;
    public float OrthoSize = 30f;   // 세로 폭(월드 단위)
    public float FovDegrees = 45f;  // 세로 FOV

    public const float TumbleSpeed = 0.0087f;   // ≈0.5°/px
    public const float DollySpeed = 0.004f;
    public const float MinDistance = 0.01f;
    public const float MaxPitch = 89.9f * MathF.PI / 180f;

    public static OrbitCamera MayaDefault()
    {
        // Maya 기본 persp: (28, 21, 28)에서 원점을 봄
        var c = new OrbitCamera();
        c.LookFrom(new Vector3(28, 21, 28), Vector3.Zero);
        return c;
    }

    public static OrbitCamera Preset(ViewKind kind)
    {
        switch (kind)
        {
            case ViewKind.Front: return new OrbitCamera { Yaw = 0, Pitch = 0, Distance = 1000, IsOrtho = true, OrthoSize = 30 };
            case ViewKind.Side: return new OrbitCamera { Yaw = MathF.PI / 2, Pitch = 0, Distance = 1000, IsOrtho = true, OrthoSize = 30 };
            case ViewKind.Top: return new OrbitCamera { Yaw = 0, Pitch = -MathF.PI / 2, Distance = 1000, IsOrtho = true, OrthoSize = 30 };
            default: return MayaDefault();
        }
    }

    /// <summary>위치와 피벗으로 yaw/pitch/distance를 역산한다.</summary>
    public void LookFrom(Vector3 eye, Vector3 pivot)
    {
        Pivot = pivot;
        var d = eye - pivot;
        Distance = MathF.Max(d.Length(), MinDistance);
        var n = d / Distance;
        // Rotation = Ry(Yaw)·Rx(Pitch) 가 (0,0,1)을 n = (cos p·sin y, -sin p, cos p·cos y) 로 보낸다
        Pitch = -MathF.Asin(Math.Clamp(n.Y, -1f, 1f)); // 위에서 내려다보면(n.Y>0) 음수
        Yaw = MathF.Atan2(n.X, n.Z);
    }

    /// <summary>카메라 회전(월드 기준). 카메라 로컬 +Z가 피벗에서 눈으로 향하는 방향.</summary>
    public Quaternion Rotation
        => Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, Pitch), Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw));

    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Rotation);
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);
    public Vector3 Eye => Pivot + Vector3.Transform(Vector3.UnitZ, Rotation) * Distance;

    public void Tumble(float dxPixels, float dyPixels)
    {
        if (IsOrtho) return;
        Yaw -= dxPixels * TumbleSpeed;
        Pitch -= dyPixels * TumbleSpeed;
        Pitch = Math.Clamp(Pitch, -MaxPitch, MaxPitch);
    }

    /// <summary>화면 픽셀 델타만큼 피벗(과 카메라)을 뷰 평면에서 이동.</summary>
    public void Track(float dxPixels, float dyPixels, float viewportHeightPx)
    {
        float k = WorldPerPixel(viewportHeightPx);
        Pivot += (-dxPixels * Right + dyPixels * Up) * k;
    }

    /// <summary>Alt+RMB: 오른쪽/위로 끌면 가까워진다.</summary>
    public const float MaxDistance = 1e6f;

    public void Dolly(float dxPixels, float dyPixels)
    {
        float s = Math.Clamp((dxPixels - dyPixels) * DollySpeed, -40f, 40f);
        Scale(MathF.Exp(-s));
    }

    public void Wheel(int ticks) => Scale(MathF.Pow(0.9f, Math.Clamp(ticks, -100, 100)));

    private void Scale(float f)
    {
        if (IsOrtho) OrthoSize = Math.Clamp(OrthoSize * f, 0.01f, MaxDistance);
        else Distance = Math.Clamp(Distance * f, MinDistance, MaxDistance);
    }

    /// <summary>피벗 깊이에서 1픽셀이 차지하는 월드 길이.</summary>
    public float WorldPerPixel(float viewportHeightPx)
    {
        if (viewportHeightPx <= 0) return 0;
        if (IsOrtho) return OrthoSize / viewportHeightPx;
        return 2f * Distance * MathF.Tan(FovDegrees * MathF.PI / 360f) / viewportHeightPx;
    }

    /// <summary>AABB가 화면에 들어오도록 피벗/거리 설정(F, A).</summary>
    public void Frame(Vector3 aabbMin, Vector3 aabbMax, float aspect)
    {
        var center = (aabbMin + aabbMax) * 0.5f;
        float r = (aabbMax - aabbMin).Length() * 0.5f;
        if (r < 0.01f) r = 1f;
        Pivot = center;
        if (IsOrtho)
        {
            OrthoSize = 2f * r * 1.1f;
            if (aspect < 1f) OrthoSize /= aspect;
        }
        else
        {
            float fov = FovDegrees * MathF.PI / 180f;
            float fovEff = MathF.Min(fov, 2f * MathF.Atan(MathF.Tan(fov / 2f) * aspect));
            Distance = r / MathF.Sin(fovEff / 2f) * 1.05f;
        }
    }

    public void CopyFrom(OrbitCamera o)
    {
        Pivot = o.Pivot; Distance = o.Distance; Yaw = o.Yaw; Pitch = o.Pitch; IsOrtho = o.IsOrtho; OrthoSize = o.OrthoSize; FovDegrees = o.FovDegrees;
    }
}
