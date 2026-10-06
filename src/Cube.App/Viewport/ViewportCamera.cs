using Cube.App.Bridge;
using Cube.Core.Camera;
using Godot;

namespace Cube.App.Viewport;

/// <summary>OrbitCamera 상태를 Godot Camera3D에 적용한다. 뷰 종류(persp/front/side/top/back/left/bottom)별 상태를 보관한다.</summary>
public sealed class ViewportCamera
{
    private readonly Camera3D _cam;
    private readonly Dictionary<ViewKind, OrbitCamera> _states = new();

    public ViewKind Kind { get; private set; } = ViewKind.Persp;
    public OrbitCamera State { get; private set; }
    public event Action? Changed;

    public ViewportCamera(Camera3D cam, ViewKind initial = ViewKind.Persp)
    {
        _cam = cam;
        foreach (ViewKind k in Enum.GetValues<ViewKind>()) _states[k] = OrbitCamera.Preset(k);
        Kind = initial;
        State = _states[initial];
        Apply();
    }

    public string Label => Kind switch
    {
        ViewKind.Front => "front", ViewKind.Side => "side", ViewKind.Top => "top",
        ViewKind.Back => "back", ViewKind.Left => "left", ViewKind.Bottom => "bottom",
        _ => State.IsOrtho ? "persp (ortho)" : "persp",
    };

    public bool IsOrtho => State.IsOrtho;

    public void SetView(ViewKind kind)
    {
        Kind = kind;
        State = _states[kind];
        Apply();
    }

    public void Home()
    {
        State.CopyFrom(OrbitCamera.Preset(Kind));
        Apply();
    }

    public void ToggleOrtho() { State.ToggleOrtho(); Apply(); }

    public void Tumble(float dx, float dy) { State.Tumble(dx, dy); Apply(); }
    public void Track(float dx, float dy, float vpHeight) { State.Track(dx, dy, vpHeight); Apply(); }
    public void Dolly(float dx, float dy) { State.Dolly(dx, dy); Apply(); }
    public void Wheel(int ticks) { State.Wheel(ticks); Apply(); }

    public void Frame(Aabb aabb, float aspect)
    {
        State.Frame(aabb.Position.ToNumerics(), aabb.End.ToNumerics(), aspect);
        Apply();
    }

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
