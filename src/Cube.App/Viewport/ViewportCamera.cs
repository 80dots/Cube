using Cube.App.Bridge;
using Cube.Core.Camera;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 패널 카메라 하나의 상태(OrbitCamera)를 Godot Camera3D에 적용한다.
/// 뷰 프리셋(Front/Top/...)은 카메라를 그 방향으로 회전(애니메이션)시키고 직교 투영으로 바꾼다.
/// 직교 상태에서 텀블하면 원근으로 돌아오며 자연스럽게 이어진다. Persp/Ortho 토글은 투영만 바꾼다.
/// </summary>
/// <remarks>
/// 카메라 수학(텀블/트랙/돌리/프레임, 눈 위치·회전 계산)은 코어 <c>OrbitCamera</c>(<see cref="State"/>)가 하고,
/// 이 클래스는 프리셋 전환 애니메이션(yaw/pitch 보간, 0.25초 ease-out)과 Godot Camera3D 속성 적용(<see cref="Apply"/>)만 맡는다.
/// 카메라가 바뀔 때마다 <see cref="Changed"/>를 알려 HUD 뷰 큐브·조작기 크기 등이 따라가게 한다.
/// </remarks>
public sealed class ViewportCamera
{
    /// <summary>상태를 적용할 Godot 카메라(패널의 SubViewport 안).</summary>
    private readonly Camera3D _cam;
    /// <summary>프리셋 애니메이션의 목표 yaw/pitch(라디안).</summary>
    private float _targetYaw, _targetPitch;
    /// <summary>프리셋 회전 애니메이션 진행 중인지.</summary>
    private bool _animating;
    /// <summary>프리셋 회전 애니메이션 길이(초).</summary>
    private const float AnimSeconds = 0.25f;
    /// <summary>애니메이션 진행도(0..1).</summary>
    private float _animT;
    /// <summary>애니메이션 시작 시의 yaw/pitch(라디안).</summary>
    private float _fromYaw, _fromPitch;

    /// <summary>궤도 카메라 상태(피벗, 거리, yaw/pitch, 직교 여부·크기, FOV). 시작값은 Maya 기본 원근 뷰.</summary>
    public OrbitCamera State { get; } = OrbitCamera.MayaDefault();
    /// <summary>마지막으로 적용한 프리셋. 텀블하면 Persp가 된다. 라벨 표시용.</summary>
    public ViewKind Kind { get; private set; } = ViewKind.Persp;
    /// <summary><see cref="Apply"/>로 Godot 카메라가 갱신될 때마다 발생.</summary>
    public event Action? Changed;

    /// <summary>
    /// 카메라를 받아 초기 뷰를 설정한다. 프리셋 뷰(4분할의 top/front/side)면 그 방향 + 직교 투영으로 시작하며,
    /// 직교 크기는 같은 거리의 원근 뷰와 화면에 보이는 높이가 같도록 2·d·tan(FOV/2)로 정한다.
    /// </summary>
    public ViewportCamera(Camera3D cam, ViewKind initial = ViewKind.Persp)
    {
        _cam = cam;
        if (initial != ViewKind.Persp)
        {
            var p = OrbitCamera.Preset(initial);
            State.Yaw = p.Yaw; State.Pitch = p.Pitch;
            State.IsOrtho = true; State.OrthoSize = 2f * State.Distance * MathF.Tan(State.FovDegrees * MathF.PI / 360f);
            Kind = initial;
        }
        _targetYaw = State.Yaw; _targetPitch = State.Pitch;
        Apply();
    }

    /// <summary>패널 좌상단 등에 표시할 뷰 이름("persp", "front", 기본 투영과 다르면 "(ortho)"/"(persp)" 덧붙임).</summary>
    public string Label
    {
        get
        {
            string name = Kind switch
            {
                ViewKind.Front => "front", ViewKind.Side => "side", ViewKind.Top => "top",
                ViewKind.Back => "back", ViewKind.Left => "left", ViewKind.Bottom => "bottom",
                _ => "persp",
            };
            // 프리셋 뷰는 직교가 기본, persp는 원근이 기본이므로 기본과 다를 때만 투영을 덧붙인다
            if (Kind == ViewKind.Persp && State.IsOrtho) return "persp (ortho)";
            if (Kind != ViewKind.Persp && !State.IsOrtho) return name + " (persp)";
            return name;
        }
    }

    /// <summary>현재 직교 투영인지.</summary>
    public bool IsOrtho => State.IsOrtho;
    /// <summary>프리셋 뷰(Persp가 아닌 방향) 상태인지.</summary>
    public bool IsPreset => Kind != ViewKind.Persp;

    /// <summary>프리셋 방향으로 회전(애니메이션)하고 직교 투영으로 전환. 피벗/거리는 유지해 프레이밍이 바뀌지 않는다.</summary>
    public void SetView(ViewKind kind)
    {
        // Persp 요청: 원근으로 돌리고 방향은 그대로
        if (kind == ViewKind.Persp) { SetOrtho(false); Kind = ViewKind.Persp; Apply(); return; }
        var p = OrbitCamera.Preset(kind);
        Kind = kind;
        SetOrtho(true);
        StartAnimation(p.Yaw, p.Pitch);
    }

    /// <summary>현재 yaw/pitch에서 목표로 보간 애니메이션을 시작한다(yaw는 ±π 안의 최단 방향으로 돈다).</summary>
    private void StartAnimation(float yaw, float pitch)
    {
        // 최단 경로로 yaw를 돈다
        float dy = NormalizeAngle(yaw - State.Yaw);
        _fromYaw = State.Yaw; _fromPitch = State.Pitch;
        _targetYaw = State.Yaw + dy; _targetPitch = pitch;
        _animT = 0; _animating = true;
    }

    /// <summary>각도를 (−π, π] 범위로 감는다.</summary>
    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    /// <summary>패널 _Process에서 매 프레임 호출.</summary>
    public void Update(float delta)
    {
        // 진행도를 올리고 ease-out 곡선으로 yaw/pitch를 보간, 끝나면 yaw를 정규화
        if (!_animating) return;
        _animT = MathF.Min(_animT + delta / AnimSeconds, 1f);
        float t = 1f - (1f - _animT) * (1f - _animT); // ease-out
        State.Yaw = _fromYaw + (_targetYaw - _fromYaw) * t;
        State.Pitch = _fromPitch + (_targetPitch - _fromPitch) * t;
        if (_animT >= 1f) { _animating = false; State.Yaw = NormalizeAngle(_targetYaw); }
        Apply();
    }

    /// <summary>현재 투영이 원하는 값과 다를 때만 OrbitCamera의 투영을 토글한다(토글은 화면 크기를 유지하도록 거리/직교 크기를 맞춘다).</summary>
    private void SetOrtho(bool ortho)
    {
        if (State.IsOrtho == ortho) return;
        State.ToggleOrtho();
    }

    /// <summary>Persp/Ortho 버튼: 투영만 바꾼다(방향 유지).</summary>
    public void ToggleOrtho() { State.ToggleOrtho(); Apply(); }

    /// <summary>Maya 기본 뷰로 돌아간다: 피벗·거리를 기본값으로, 원근으로 바꾸고 기본 방향으로 애니메이션.</summary>
    public void Home()
    {
        var p = OrbitCamera.MayaDefault();
        State.Pivot = p.Pivot; State.Distance = p.Distance;
        SetOrtho(false);
        Kind = ViewKind.Persp;
        StartAnimation(p.Yaw, p.Pitch);
    }

    /// <summary>텀블: 직교 프리셋 상태면 원근으로 돌아오며 그대로 이어서 돈다.</summary>
    public void Tumble(float dx, float dy)
    {
        // 진행 중인 프리셋 애니메이션을 끊고, 직교였으면 원근으로 바꾼 뒤 이어서 돈다
        _animating = false;
        if (State.IsOrtho) SetOrtho(false);
        Kind = ViewKind.Persp;
        State.AllowOrthoTumble = true;
        State.Tumble(dx, dy);
        Apply();
    }

    /// <summary>트랙(팬): 화면 픽셀 이동량을 피벗 평면 이동으로(vpHeight = 패널 높이, 픽셀→월드 환산용).</summary>
    public void Track(float dx, float dy, float vpHeight) { _animating = false; State.Track(dx, dy, vpHeight); Apply(); }
    /// <summary>돌리(Alt+RMB 드래그): 피벗까지 거리(직교면 크기)를 바꾼다.</summary>
    public void Dolly(float dx, float dy) { State.Dolly(dx, dy); Apply(); }
    /// <summary>휠 돌리: 틱 수만큼 줌(양수 = 가까이).</summary>
    public void Wheel(int ticks) { State.Wheel(ticks); Apply(); }

    /// <summary>월드 AABB가 화면에 꽉 차도록 피벗·거리를 맞춘다(F 키 프레임). aspect = 패널 가로/세로.</summary>
    public void Frame(Aabb aabb, float aspect)
    {
        State.Frame(aabb.Position.ToNumerics(), aabb.End.ToNumerics(), aspect);
        Apply();
    }

    /// <summary>현재 시선이 향하는 축(프리셋 방향이면 그 축의 법선 평면에 그리드를 세울 때 사용).</summary>
    public Vector3 Forward => -_cam.GlobalTransform.Basis.Z;

    /// <summary>
    /// OrbitCamera 상태를 Godot 카메라에 적용한다: 회전 쿼터니언 + 눈 위치로 Transform, 투영 종류에 따라 Size/Fov와 near/far를 설정한다.
    /// 직교는 near를 음수(−5000)로 두어 카메라 뒤쪽 물체도 잘리지 않게 한다. 두 경우 모두 높이 기준 종횡비 유지.
    /// </summary>
    public void Apply()
    {
        var q = State.Rotation;
        var basis = new Basis(new Quaternion(q.X, q.Y, q.Z, q.W));
        _cam.Transform = new Transform3D(basis, State.Eye.ToGodot());
        if (State.IsOrtho)
        {
            _cam.Projection = Camera3D.ProjectionType.Orthogonal;
            _cam.Size = State.OrthoSize;
            _cam.KeepAspect = Camera3D.KeepAspectEnum.Height;
            _cam.Near = -5000f; _cam.Far = 10000f;
        }
        else
        {
            _cam.Projection = Camera3D.ProjectionType.Perspective;
            _cam.Fov = State.FovDegrees;
            _cam.KeepAspect = Camera3D.KeepAspectEnum.Height;
            _cam.Near = 0.05f; _cam.Far = 10000f;
        }
        Changed?.Invoke();
    }
}
