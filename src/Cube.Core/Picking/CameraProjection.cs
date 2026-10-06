using System.Numerics;

namespace Cube.Core.Picking;

public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    public Vector3 At(float t) => Origin + Direction * t;
}

/// <summary>
/// 카메라의 투영/역투영을 순수 수학으로 제공한다(Godot Camera3D에서 파라미터만 가져와 만든다).
/// 화면 좌표는 좌상단 원점 픽셀.
/// </summary>
public sealed class CameraProjection
{
    public Matrix4x4 CameraToWorld { get; }
    public Matrix4x4 WorldToCamera { get; }
    public bool IsOrtho { get; }
    public float FovYRadians { get; }
    public float OrthoHeight { get; }
    public float Near { get; }
    public float Far { get; }
    public Vector2 ViewportSize { get; }
    public float Aspect => ViewportSize.Y > 0 ? ViewportSize.X / ViewportSize.Y : 1f;

    public Vector3 Eye => CameraToWorld.Translation;
    public Vector3 Forward => Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, CameraToWorld));
    public Vector3 Right => Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, CameraToWorld));
    public Vector3 Up => Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, CameraToWorld));

    public CameraProjection(Matrix4x4 cameraToWorld, bool isOrtho, float fovYRadians, float orthoHeight, float near, float far, Vector2 viewportSize)
    {
        CameraToWorld = cameraToWorld;
        Matrix4x4.Invert(cameraToWorld, out var inv);
        WorldToCamera = inv;
        IsOrtho = isOrtho; FovYRadians = fovYRadians; OrthoHeight = orthoHeight; Near = near; Far = far; ViewportSize = viewportSize;
    }

    public static CameraProjection Perspective(Vector3 eye, Vector3 target, Vector3 up, float fovYDegrees, Vector2 viewportSize, float near = 0.05f, float far = 10000f)
    {
        var view = Matrix4x4.CreateLookAt(eye, target, up);
        Matrix4x4.Invert(view, out var camToWorld);
        return new CameraProjection(camToWorld, false, fovYDegrees * MathF.PI / 180f, 0, near, far, viewportSize);
    }

    /// <summary>월드 점을 화면 픽셀로. 카메라 뒤면 null. depth는 카메라 전방 거리.</summary>
    public Vector2? Project(Vector3 world, out float depth)
    {
        var v = Vector3.Transform(world, WorldToCamera);
        depth = -v.Z;
        if (!IsOrtho && depth <= 1e-6f) return null;
        float ndcX, ndcY;
        if (IsOrtho)
        {
            float halfH = OrthoHeight / 2f, halfW = halfH * Aspect;
            ndcX = v.X / halfW; ndcY = v.Y / halfH;
        }
        else
        {
            float t = MathF.Tan(FovYRadians / 2f);
            ndcX = v.X / (depth * t * Aspect); ndcY = v.Y / (depth * t);
        }
        return new Vector2((ndcX + 1f) * 0.5f * ViewportSize.X, (1f - ndcY) * 0.5f * ViewportSize.Y);
    }

    /// <summary>화면 픽셀에서 월드 광선. 원근은 눈에서, 직교는 뷰 평면 위의 점에서 출발한다.</summary>
    public Ray Unproject(Vector2 px)
    {
        float ndcX = px.X / ViewportSize.X * 2f - 1f;
        float ndcY = 1f - px.Y / ViewportSize.Y * 2f;
        if (IsOrtho)
        {
            float halfH = OrthoHeight / 2f, halfW = halfH * Aspect;
            var origin = Eye + Right * (ndcX * halfW) + Up * (ndcY * halfH) + Forward * (-Math.Abs(Near) - 1f);
            return new Ray(origin, Forward);
        }
        float t = MathF.Tan(FovYRadians / 2f);
        var dirCam = new Vector3(ndcX * t * Aspect, ndcY * t, -1f);
        var dirWorld = Vector3.Normalize(Vector3.TransformNormal(dirCam, CameraToWorld));
        return new Ray(Eye, dirWorld);
    }

    /// <summary>이 깊이에서 1픽셀의 월드 길이.</summary>
    public float WorldPerPixel(float depth)
    {
        if (IsOrtho) return OrthoHeight / ViewportSize.Y;
        return 2f * depth * MathF.Tan(FovYRadians / 2f) / ViewportSize.Y;
    }
}
