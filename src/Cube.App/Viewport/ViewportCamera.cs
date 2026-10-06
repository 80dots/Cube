using Cube.App.Bridge;
using Cube.Core.Camera;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 패널 카메라 하나의 상태(OrbitCamera)를 Godot Camera3D에 적용한다.
/// 뷰 프리셋(Front/Top/...)은 카메라를 그 방향으로 회전(애니메이션)시키고 직교 투영으로 바꾼다.
/// 직교 상태에서 텀블하면 원근으로 돌아오며 자연스럽게 이어진다. Persp/Ortho 토글은 투영만 바꾼다.
/// </summary>
public sealed class ViewportCamera
{
    private readonly Camera3D _cam;
    private float _targetYaw, _targetPitch;
    private bool _animating;
    private const float AnimSeconds = 0.25f;
    private float _animT;
    private float _fromYaw, _fromPitch;

    public OrbitCamera State { get; } = OrbitCamera.MayaDefault();
    /// <summary>마지막으로 적용한 프리셋. 텀블하면 Persp가 된다. 라벨 표시용.</summary>
    public ViewKind Kind { get; private set; } = ViewKind.Persp;
    public event Action? Changed;

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

    public bool IsOrtho => State.IsOrtho;
    public bool IsPreset => Kind != ViewKind.Persp;

    /// <summary>프리셋 방향으로 회전(애니메이션)하고 직교 투영으로 전환. 피벗/거리는 유지해 프레이밍이 바뀌지 않는다.</summary>
    public void SetView(ViewKind kind)
    {
        if (kind == ViewKind.Persp) { SetOrtho(false); Kind = ViewKind.Persp; Apply(); return; }
        var p = OrbitCamera.Preset(kind);
        Kind = kind;
        SetOrtho(true);
        StartAnimation(p.Yaw, p.Pitch);
    }

    private void StartAnimation(float yaw, float pitch)
    {
        // 최단 경로로 yaw를 돈다
        float dy = NormalizeAngle(yaw - State.Yaw);
        _fromYaw = State.Yaw; _fromPitch = State.Pitch;
        _targetYaw = State.Yaw + dy; _targetPitch = pitch;
        _animT = 0; _animating = true;
    }

    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    /// <summary>패널 _Process에서 매 프레임 호출.</summary>
    public void Update(float delta)
    {
        if (!_animating) return;
        _animT = MathF.Min(_animT + delta / AnimSeconds, 1f);
        float t = 1f - (1f - _animT) * (1f - _animT); // ease-out
        State.Yaw = _fromYaw + (_targetYaw - _fromYaw) * t;
        State.Pitch = _fromPitch + (_targetPitch - _fromPitch) * t;
        if (_animT >= 1f) { _animating = false; State.Yaw = NormalizeAngle(_targetYaw); }
        Apply();
    }

    private void SetOrtho(bool ortho)
    {
        if (State.IsOrtho == ortho) return;
        State.ToggleOrtho();
    }

    /// <summary>Persp/Ortho 버튼: 투영만 바꾼다(방향 유지).</summary>
    public void ToggleOrtho() { State.ToggleOrtho(); Apply(); }

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
        _animating = false;
        if (State.IsOrtho) SetOrtho(false);
        Kind = ViewKind.Persp;
        State.AllowOrthoTumble = true;
        State.Tumble(dx, dy);
        Apply();
    }

    public void Track(float dx, float dy, float vpHeight) { _animating = false; State.Track(dx, dy, vpHeight); Apply(); }
    public void Dolly(float dx, float dy) { State.Dolly(dx, dy); Apply(); }
    public void Wheel(int ticks) { State.Wheel(ticks); Apply(); }

    public void Frame(Aabb aabb, float aspect)
    {
        State.Frame(aabb.Position.ToNumerics(), aabb.End.ToNumerics(), aspect);
        Apply();
    }

    /// <summary>현재 시선이 향하는 축(프리셋 방향이면 그 축의 법선 평면에 그리드를 세울 때 사용).</summary>
    public Vector3 Forward => -_cam.GlobalTransform.Basis.Z;

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
