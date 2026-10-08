using System.Numerics;

namespace Cube.Core.Picking;

/// <summary>
/// 3D 광선(원점 + 방향). 피킹·조작기 드래그에서 화면 픽셀을 역투영한 결과로 쓴다. Direction은 정규화되어 있지 않을 수도 있다.
/// </summary>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    /// <summary>광선 위의 점 Origin + Direction·t.</summary>
    public Vector3 At(float t) => Origin + Direction * t;
}

/// <summary>
/// 카메라의 투영/역투영을 순수 수학으로 제공한다(Godot Camera3D에서 파라미터만 가져와 만든다).
/// 화면 좌표는 좌상단 원점 픽셀.
/// </summary>
/// <remarks>
/// App의 ViewportPanel이 매 피킹/드래그마다 현재 Camera3D의 월드 행렬·FOV·직교 크기·뷰포트 크기로 만들어 Core(RayPicker, 툴 수학)에 넘긴다.
/// 카메라 공간은 Godot과 같이 −Z가 전방, +Y가 위다. NDC는 x·y 모두 −1~1이며 화면 y는 아래로 커지므로 변환 시 뒤집는다.
/// </remarks>
public sealed class CameraProjection
{
    /// <summary>카메라 로컬 → 월드 행렬(행벡터 규약, Translation = 눈 위치).</summary>
    public Matrix4x4 CameraToWorld { get; }
    /// <summary>월드 → 카메라 로컬 행렬(CameraToWorld의 역행렬, 생성자에서 한 번 계산).</summary>
    public Matrix4x4 WorldToCamera { get; }
    /// <summary>직교 투영이면 true.</summary>
    public bool IsOrtho { get; }
    /// <summary>원근 투영의 세로 시야각(라디안).</summary>
    public float FovYRadians { get; }
    /// <summary>직교 투영에서 화면 세로가 담는 월드 길이(m).</summary>
    public float OrthoHeight { get; }
    /// <summary>근평면 거리. 직교 광선 원점을 뷰 평면 뒤로 물리는 데 쓴다.</summary>
    public float Near { get; }
    /// <summary>원평면 거리(현재 계산에는 쓰지 않고 보관만).</summary>
    public float Far { get; }
    /// <summary>뷰포트 크기(픽셀).</summary>
    public Vector2 ViewportSize { get; }
    /// <summary>가로/세로 비. 높이가 0이면 1.</summary>
    public float Aspect => ViewportSize.Y > 0 ? ViewportSize.X / ViewportSize.Y : 1f;

    /// <summary>카메라(눈) 월드 위치.</summary>
    public Vector3 Eye => CameraToWorld.Translation;
    /// <summary>카메라 전방(로컬 −Z) 월드 단위벡터.</summary>
    public Vector3 Forward => Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, CameraToWorld));
    /// <summary>화면 오른쪽(로컬 +X) 월드 단위벡터.</summary>
    public Vector3 Right => Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, CameraToWorld));
    /// <summary>화면 위(로컬 +Y) 월드 단위벡터.</summary>
    public Vector3 Up => Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, CameraToWorld));

    /// <summary>카메라 파라미터로 투영 객체를 만든다. 역행렬(WorldToCamera)을 미리 계산해 둔다.</summary>
    public CameraProjection(Matrix4x4 cameraToWorld, bool isOrtho, float fovYRadians, float orthoHeight, float near, float far, Vector2 viewportSize)
    {
        CameraToWorld = cameraToWorld;
        Matrix4x4.Invert(cameraToWorld, out var inv);
        WorldToCamera = inv;
        IsOrtho = isOrtho; FovYRadians = fovYRadians; OrthoHeight = orthoHeight; Near = near; Far = far; ViewportSize = viewportSize;
    }

    /// <summary>
    /// 눈·목표·업 벡터로 원근 카메라를 만드는 편의 팩토리(주로 테스트용).
    /// CreateLookAt은 월드→뷰 행렬이므로 역행렬로 카메라→월드를 얻는다.
    /// </summary>
    public static CameraProjection Perspective(Vector3 eye, Vector3 target, Vector3 up, float fovYDegrees, Vector2 viewportSize, float near = 0.05f, float far = 10000f)
    {
        var view = Matrix4x4.CreateLookAt(eye, target, up);
        Matrix4x4.Invert(view, out var camToWorld);
        return new CameraProjection(camToWorld, false, fovYDegrees * MathF.PI / 180f, 0, near, far, viewportSize);
    }

    /// <summary>월드 점을 화면 픽셀로. 카메라 뒤면 null. depth는 카메라 전방 거리.</summary>
    /// <remarks>
    /// 순서: 월드 → 카메라 공간 → NDC(원근은 깊이로 나눔, 직교는 반폭으로 나눔) → 픽셀(y 반전).
    /// 직교는 카메라 뒤 점도 투영한다(깊이와 무관하게 평행 투영이므로).
    /// </remarks>
    public Vector2? Project(Vector3 world, out float depth)
    {
        var v = Vector3.Transform(world, WorldToCamera);
        // 카메라는 −Z를 보므로 전방 거리는 −v.Z
        depth = -v.Z;
        // 원근에서 눈 뒤(또는 눈 위치)의 점은 투영 불가
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
        // NDC(−1~1) → 픽셀. y는 NDC 위가 +1, 픽셀은 위가 0이므로 (1 − ndcY)
        return new Vector2((ndcX + 1f) * 0.5f * ViewportSize.X, (1f - ndcY) * 0.5f * ViewportSize.Y);
    }

    /// <summary>화면 픽셀에서 월드 광선. 원근은 눈에서, 직교는 뷰 평면 위의 점에서 출발한다.</summary>
    /// <remarks>
    /// 직교 광선 원점은 눈 평면에서 Near+1만큼 뒤로 물러난 점이다. 카메라 바로 앞에 있는 물체도 광선의 양의 구간에 들어오게 하기 위함.
    /// 원근 광선 방향은 카메라 공간의 (ndcX·tan·aspect, ndcY·tan, −1)을 월드로 돌린 뒤 정규화한다.
    /// </remarks>
    public Ray Unproject(Vector2 px)
    {
        // 픽셀 → NDC(y 반전)
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
    /// <remarks>원근은 깊이에 비례(2·depth·tan(fov/2)/높이), 직교는 깊이와 무관. 픽셀 임계값을 월드 거리로 바꿀 때 쓴다.</remarks>
    public float WorldPerPixel(float depth)
    {
        if (IsOrtho) return OrthoHeight / ViewportSize.Y;
        return 2f * depth * MathF.Tan(FovYRadians / 2f) / ViewportSize.Y;
    }
}
