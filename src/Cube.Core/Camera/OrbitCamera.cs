using System.Numerics;

namespace Cube.Core.Camera;

/// <summary>
/// 뷰포트 카메라 프리셋 종류. Persp는 자유 원근 카메라, 나머지는 축 정렬 직교 뷰(Maya의 front/side/top 등)다.
/// </summary>
public enum ViewKind { Persp, Front, Side, Top, Back, Left, Bottom }

/// <summary>
/// Maya 식 피벗(center of interest) 기반 카메라 상태. 순수 수학이며 Godot Camera3D에 적용하는 쪽은 App이다.
/// 좌표계: 오른손, Y-up. 카메라는 로컬 -Z를 본다.
/// </summary>
/// <remarks>
/// 상태는 피벗(Pivot) + 거리(Distance) + 방위각(Yaw) + 앙각(Pitch)의 구면 좌표로 표현한다.
/// 눈 위치 = Pivot + Rotation·(0,0,1)·Distance. 텀블(Alt+LMB)은 Yaw/Pitch를, 트랙(Alt+MMB)은 Pivot을,
/// 돌리(Alt+RMB/휠)는 Distance(직교면 OrthoSize)를 바꾼다. NavigationHandler(App)가 픽셀 델타를 넘겨 호출한다.
/// </remarks>
public sealed class OrbitCamera
{
    /// <summary>회전 중심(Maya center of interest). 텀블은 이 점을 중심으로 돌고 트랙은 이 점을 옮긴다(월드, m).</summary>
    public Vector3 Pivot;
    /// <summary>피벗에서 눈까지의 거리(m). 원근 투영의 줌에 해당한다. MinDistance~MaxDistance로 제한.</summary>
    public float Distance = 44.9f;
    /// <summary>방위각. 0이면 +Z 쪽에서 −Z를 바라본다(front).</summary>
    public float Yaw;      // 라디안, Y축 회전
    /// <summary>앙각. ±MaxPitch로 클램프되어 짐벌 뒤집힘을 막는다.</summary>
    public float Pitch;    // 라디안, X축 회전(음수 = 아래를 봄)
    /// <summary>직교 투영 여부. 고정 뷰(front/top...)는 항상 true.</summary>
    public bool IsOrtho;
    /// <summary>직교 투영일 때 화면 세로가 담는 월드 길이(m). 직교 줌은 이 값을 바꾼다.</summary>
    public float OrthoSize = 30f;   // 세로 폭(월드 단위)
    /// <summary>원근 투영의 세로 시야각(도).</summary>
    public float FovDegrees = 45f;  // 세로 FOV

    /// <summary>텀블 감도: 픽셀당 회전 라디안.</summary>
    public const float TumbleSpeed = 0.0087f;   // ≈0.5°/px
    /// <summary>돌리 감도: 픽셀 이동량에 곱해 지수 배율 exp(−s)로 쓴다.</summary>
    public const float DollySpeed = 0.004f;
    /// <summary>최소 거리(m). 0이 되면 피벗과 눈이 겹쳐 방향이 정의되지 않는다.</summary>
    public const float MinDistance = 0.01f;
    /// <summary>최대 앙각(89.9°를 라디안으로). 정확히 90°면 Up 벡터가 시선과 평행해져 쓰지 않는다.</summary>
    public const float MaxPitch = 89.9f * MathF.PI / 180f;

    /// <summary>새 원근 뷰의 기본 카메라. (2.8, 2.1, 2.8)에서 원점을 바라본다.</summary>
    public static OrbitCamera MayaDefault()
    {
        // Maya 기본 persp 방향(28, 21, 28)을 유지하되 1m 큐브가 크게 보이도록 가까이(거리 ≈ 4.5m)
        var c = new OrbitCamera();
        c.LookFrom(new Vector3(2.8f, 2.1f, 2.8f), Vector3.Zero);
        return c;
    }

    /// <summary>
    /// 뷰 종류별 기본 카메라를 만든다. 직교 뷰는 거리 1000m에서 30m 폭으로 보며 텀블할 수 없다.
    /// Persp는 <see cref="MayaDefault"/>이고 직교로 바꿔도 텀블이 허용된다(<see cref="AllowOrthoTumble"/>).
    /// </summary>
    public static OrbitCamera Preset(ViewKind kind)
    {
        switch (kind)
        {
            // front: −Z를 바라봄(+Z 쪽에서)
            case ViewKind.Front: return new OrbitCamera { Yaw = 0, Pitch = 0, Distance = 1000, IsOrtho = true, OrthoSize = 30 };
            // side: Yaw 90° → +X 쪽에서 −X를 바라봄
            case ViewKind.Side: return new OrbitCamera { Yaw = MathF.PI / 2, Pitch = 0, Distance = 1000, IsOrtho = true, OrthoSize = 30 };
            // top: Pitch −90° → 위에서 아래(−Y)를 내려다봄
            case ViewKind.Top: return new OrbitCamera { Yaw = 0, Pitch = -MathF.PI / 2, Distance = 1000, IsOrtho = true, OrthoSize = 30 };
            case ViewKind.Back: return new OrbitCamera { Yaw = MathF.PI, Pitch = 0, Distance = 1000, IsOrtho = true, OrthoSize = 30 };
            case ViewKind.Left: return new OrbitCamera { Yaw = -MathF.PI / 2, Pitch = 0, Distance = 1000, IsOrtho = true, OrthoSize = 30 };
            case ViewKind.Bottom: return new OrbitCamera { Yaw = 0, Pitch = MathF.PI / 2, Distance = 1000, IsOrtho = true, OrthoSize = 30 };
            default: { var p = MayaDefault(); p.AllowOrthoTumble = true; return p; }
        }
    }

    /// <summary>위치와 피벗으로 yaw/pitch/distance를 역산한다.</summary>
    /// <remarks>
    /// 방향 n = (eye − pivot)/|eye − pivot| 에서 Rotation 정의를 역으로 풀어 Pitch = −asin(n.y), Yaw = atan2(n.x, n.z)를 얻는다.
    /// </remarks>
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
    /// <remarks>
    /// System.Numerics의 Concatenate(a, b)는 "a 다음 b" 적용이므로 먼저 X축(Pitch), 그다음 Y축(Yaw)으로 돌린다.
    /// </remarks>
    public Quaternion Rotation
        => Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, Pitch), Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw));

    /// <summary>카메라가 바라보는 방향(로컬 −Z의 월드 방향, 눈 → 피벗).</summary>
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Rotation);
    /// <summary>화면 오른쪽 방향(로컬 +X의 월드 방향). 트랙 이동에 쓴다.</summary>
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);
    /// <summary>화면 위 방향(로컬 +Y의 월드 방향).</summary>
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);
    /// <summary>눈(카메라) 위치 = 피벗 + 로컬 +Z 방향 × 거리.</summary>
    public Vector3 Eye => Pivot + Vector3.Transform(Vector3.UnitZ, Rotation) * Distance;

    /// <summary>persp 카메라는 직교 투영으로 바꿔도 텀블할 수 있다(Maya의 Orthographic 체크). 고정 뷰(front/top...)는 불가.</summary>
    public bool AllowOrthoTumble;

    /// <summary>원근 ↔ 직교 전환. 현재 프레이밍이 유지되도록 거리/직교 폭을 맞춘다.</summary>
    /// <remarks>
    /// 원근 → 직교: 피벗 깊이에서 화면 세로 폭 2·D·tan(fov/2)를 OrthoSize로.
    /// 직교 → 원근: 그 역으로 D = OrthoSize / (2·tan(fov/2)). 그래서 전환해도 피벗 주변 크기가 그대로 보인다.
    /// </remarks>
    public void ToggleOrtho()
    {
        // t = tan(fov/2). FovDegrees·π/360 = (fov/2)를 라디안으로
        float t = MathF.Tan(FovDegrees * MathF.PI / 360f);
        if (IsOrtho) { Distance = MathF.Max(OrthoSize / (2f * t), MinDistance); IsOrtho = false; }
        else { OrthoSize = 2f * Distance * t; IsOrtho = true; }
    }

    /// <summary>
    /// Alt+LMB 텀블. 가로 픽셀은 Yaw, 세로 픽셀은 Pitch를 바꾸며 피벗은 그대로 둔다.
    /// 고정 직교 뷰(AllowOrthoTumble = false)에서는 아무것도 하지 않는다.
    /// </summary>
    public void Tumble(float dxPixels, float dyPixels)
    // 고정 직교 뷰는 회전 불가(Maya와 같음)
    {
        if (IsOrtho && !AllowOrthoTumble) return;
        // 오른쪽으로 끌면 카메라가 왼쪽으로 돌아 장면이 오른쪽으로 도는 것처럼 보이도록 부호를 뺀다
        Yaw -= dxPixels * TumbleSpeed;
        Pitch -= dyPixels * TumbleSpeed;
        // 수직 위/아래를 넘어 뒤집히지 않게 제한
        Pitch = Math.Clamp(Pitch, -MaxPitch, MaxPitch);
    }

    /// <summary>화면 픽셀 델타만큼 피벗(과 카메라)을 뷰 평면에서 이동.</summary>
    /// <remarks>
    /// Alt+MMB. 피벗 깊이의 픽셀당 월드 길이 k를 곱해 커서 아래 점이 마우스를 정확히 따라오게 한다.
    /// 화면 y는 아래로 커지므로 Up 방향은 +dy, Right 방향은 −dx(장면이 마우스를 따라가도록 카메라는 반대로 이동).
    /// </remarks>
    public void Track(float dxPixels, float dyPixels, float viewportHeightPx)
    {
        float k = WorldPerPixel(viewportHeightPx);
        Pivot += (-dxPixels * Right + dyPixels * Up) * k;
    }

    /// <summary>Alt+RMB: 오른쪽/위로 끌면 가까워진다.</summary>
    /// <remarks>
    /// (위 summary는 아래 Dolly의 동작 설명이다.) MaxDistance 자체는 거리/직교 폭의 상한(m)으로,
    /// 너무 멀리 빠져 부동소수 정밀도가 무너지는 것을 막는다.
    /// </remarks>
    public const float MaxDistance = 1e6f;

    /// <summary>
    /// Alt+RMB 돌리. (dx − dy)·DollySpeed 를 지수 배율 exp(−s)로 바꿔 거리(직교면 OrthoSize)에 곱한다.
    /// 오른쪽/위로 끌면 s &gt; 0 → 배율 &lt; 1 → 가까워진다. 한 번에 너무 큰 변화가 없도록 s를 ±40으로 제한.
    /// </summary>
    public void Dolly(float dxPixels, float dyPixels)
    {
        float s = Math.Clamp((dxPixels - dyPixels) * DollySpeed, -40f, 40f);
        Scale(MathF.Exp(-s));
    }

    /// <summary>마우스 휠 줌. 한 틱마다 0.9배(앞으로 굴리면 가까워짐). 틱 수는 ±100으로 제한.</summary>
    public void Wheel(int ticks) => Scale(MathF.Pow(0.9f, Math.Clamp(ticks, -100, 100)));

    /// <summary>줌 배율 f를 적용한다. 직교면 OrthoSize, 원근이면 Distance에 곱하고 범위를 클램프한다.</summary>
    private void Scale(float f)
    {
        if (IsOrtho) OrthoSize = Math.Clamp(OrthoSize * f, 0.01f, MaxDistance);
        else Distance = Math.Clamp(Distance * f, MinDistance, MaxDistance);
    }

    /// <summary>피벗 깊이에서 1픽셀이 차지하는 월드 길이.</summary>
    /// <remarks>
    /// 직교 = OrthoSize / 높이, 원근 = 2·D·tan(fov/2) / 높이(피벗까지의 깊이 기준). 트랙 이동량과 조작기 화면 고정 크기에 쓴다.
    /// </remarks>
    /// <param name="viewportHeightPx">뷰포트 높이(픽셀). 0 이하이면 0을 돌려준다.</param>
    public float WorldPerPixel(float viewportHeightPx)
    {
        if (viewportHeightPx <= 0) return 0;
        if (IsOrtho) return OrthoSize / viewportHeightPx;
        return 2f * Distance * MathF.Tan(FovDegrees * MathF.PI / 360f) / viewportHeightPx;
    }

    /// <summary>AABB가 화면에 들어오도록 피벗/거리 설정(F, A).</summary>
    /// <remarks>
    /// AABB를 감싸는 구(반지름 r = 대각선/2)를 기준으로 맞춘다. 피벗은 AABB 중심.
    /// 직교: 세로 폭 = 지름 × 1.1(세로가 더 긴 창이면 aspect로 나눠 가로도 들어오게).
    /// 원근: 세로/가로 중 좁은 유효 FOV를 구해 구가 그 원뿔에 내접하는 거리 r / sin(fov/2) × 1.05 여유.
    /// </remarks>
    /// <param name="aspect">뷰포트 가로/세로 비.</param>
    public void Frame(Vector3 aabbMin, Vector3 aabbMax, float aspect)
    {
        var center = (aabbMin + aabbMax) * 0.5f;
        float r = (aabbMax - aabbMin).Length() * 0.5f;
        // 점 하나·빈 선택처럼 크기가 거의 0이면 1m 구로 프레임
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
            // 가로가 좁은 창이면 가로 FOV(= 2·atan(tan(fov/2)·aspect))가 더 작으므로 그쪽을 쓴다
            float fovEff = MathF.Min(fov, 2f * MathF.Atan(MathF.Tan(fov / 2f) * aspect));
            Distance = r / MathF.Sin(fovEff / 2f) * 1.05f;
        }
    }

    /// <summary>다른 카메라의 모든 상태를 복사한다(4분할 뷰 전환·뷰 저장 복원 등).</summary>
    public void CopyFrom(OrbitCamera o)
    {
        Pivot = o.Pivot; Distance = o.Distance; Yaw = o.Yaw; Pitch = o.Pitch; IsOrtho = o.IsOrtho; OrthoSize = o.OrthoSize; FovDegrees = o.FovDegrees; AllowOrthoTumble = o.AllowOrthoTumble;
    }
}
