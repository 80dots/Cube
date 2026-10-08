using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// Maya 내비게이션 입력 해석: Alt+LMB 텀블, Alt+MMB 트랙, Alt+RMB 돌리, 휠 돌리.
/// Alt+버튼으로 시작된 드래그는 Alt를 떼도 버튼을 놓을 때까지 계속되며, 그동안 툴에는 이벤트를 주지 않는다.
/// </summary>
/// <remarks>
/// ViewportPanel 입력 파이프라인의 첫 단계다(파이 메뉴·툴보다 먼저). 실제 카메라 수학은 <c>ViewportCamera</c>(코어 OrbitCamera 래퍼)가 하고,
/// 여기서는 버튼 상태 추적과 마우스 이동량(감도·UI 배율 보정)만 넘긴다.
/// </remarks>
public sealed class NavigationHandler
{
    /// <summary>이 핸들러가 속한 뷰포트 패널(카메라 컨트롤러·크기 조회).</summary>
    private readonly ViewportPanel _panel;
    /// <summary>진행 중인 내비게이션 드래그의 버튼(None = 드래그 아님).</summary>
    private MouseButton _dragButton = MouseButton.None;
    /// <summary>직전 마우스 위치(뷰포트 로컬 픽셀). 이동량 계산 기준.</summary>
    private Vector2 _last;

    /// <summary>Alt+버튼 내비게이션 드래그 중인지. 패널은 이때 다른 입력 처리를 건너뛴다.</summary>
    public bool IsDragging => _dragButton != MouseButton.None;

    /// <summary>패널을 받아 핸들러를 만든다.</summary>
    public NavigationHandler(ViewportPanel panel) { _panel = panel; }

    /// <summary>이벤트를 소비했으면 true.</summary>
    /// <remarks>
    /// 휠은 항상 돌리(줌). Alt+LMB/MMB/RMB 누름은 드래그 시작, 같은 버튼을 놓으면 끝. 드래그 중 마우스 이동은
    /// 감도(Settings.MouseSensitivityPercent, 10~300%)를 곱한 뒤 텀블/돌리는 UI 배율로 나눠(배율과 무관한 속도) 카메라에 전달한다.
    /// 트랙은 패널 높이 기준으로 커서 아래 점이 따라오도록 픽셀 그대로 넘긴다.
    /// </remarks>
    public bool Handle(InputEvent e)
    {
        // 버튼 이벤트: 휠 줌, Alt+버튼 드래그 시작/종료
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
                // 드래그 버튼을 놓으면 드래그 종료(다른 버튼 놓기도 소비)
                else if (IsDragging)
                {
                    if (mb.ButtonIndex == _dragButton) _dragButton = MouseButton.None;
                    return true;
                }
                return false;

            // 드래그 중 이동: 버튼에 따라 텀블/트랙/돌리
            case InputEventMouseMotion mm when IsDragging:
                {
                    float sens = Math.Clamp(CubeApp.Instance.Settings.MouseSensitivityPercent, 10, 300) / 100f;
                    var d = (mm.Position - _last) * sens;
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

    /// <summary>진행 중인 내비게이션 드래그를 강제로 끝낸다(포커스 상실, 패널 전환 등).</summary>
    public void Cancel() => _dragButton = MouseButton.None;
}
