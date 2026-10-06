using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// Maya 내비게이션 입력 해석: Alt+LMB 텀블, Alt+MMB 트랙, Alt+RMB 돌리, 휠 돌리.
/// Alt+버튼으로 시작된 드래그는 Alt를 떼도 버튼을 놓을 때까지 계속되며, 그동안 툴에는 이벤트를 주지 않는다.
/// </summary>
public sealed class NavigationHandler
{
    private readonly ViewportPanel _panel;
    private MouseButton _dragButton = MouseButton.None;
    private Vector2 _last;

    public bool IsDragging => _dragButton != MouseButton.None;

    public NavigationHandler(ViewportPanel panel) { _panel = panel; }

    /// <summary>이벤트를 소비했으면 true.</summary>
    public bool Handle(InputEvent e)
    {
        var cam = _panel.CameraController;
        switch (e)
        {
            case InputEventMouseButton mb:
                if (mb.Pressed)
                {
                    if (mb.ButtonIndex == MouseButton.WheelUp) { cam.Wheel(1); return true; }
                    if (mb.ButtonIndex == MouseButton.WheelDown) { cam.Wheel(-1); return true; }
                    if (mb.AltPressed && !IsDragging && mb.ButtonIndex is MouseButton.Left or MouseButton.Middle or MouseButton.Right)
                    {
                        _dragButton = mb.ButtonIndex;
                        _last = mb.Position;
                        return true;
                    }
                    if (IsDragging) return true; // 드래그 중 다른 버튼은 무시
                }
                else if (IsDragging)
                {
                    if (mb.ButtonIndex == _dragButton) _dragButton = MouseButton.None;
                    return true;
                }
                return false;

            case InputEventMouseMotion mm when IsDragging:
                {
                    var d = mm.Position - _last;
                    _last = mm.Position;
                    float s = CubeApp.Instance.UiScale;
                    switch (_dragButton)
                    {
                        case MouseButton.Left: cam.Tumble(d.X / s, d.Y / s); break;
                        case MouseButton.Middle: cam.Track(d.X, d.Y, _panel.Size.Y); break;
                        case MouseButton.Right: cam.Dolly(d.X / s, d.Y / s); break;
                    }
                    return true;
                }
        }
        return false;
    }

    public void Cancel() => _dragButton = MouseButton.None;
}
