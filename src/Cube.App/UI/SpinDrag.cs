using Godot;

namespace Cube.App.UI;

/// <summary>
/// 숫자 입력칸 가상 슬라이더: SpinBox(또는 그 안의 LineEdit) 위에서 **가운데 버튼을 누른 채 좌우로 끌면** 값을 줄이고 늘린다.
/// Shift를 누르고 있으면 1/10 속도로 세밀하게 바뀐다. 한 픽셀당 변화량은 정수 칸이면 0.2(5픽셀에 1), 실수 칸이면 Step(최소 0.005).
/// 이 노드를 넣은 뷰포트(셸 루트, 다이얼로그 Window)의 컨트롤에 적용된다.
/// </summary>
public partial class SpinDrag : Node
{
    private SpinBox? _spin;
    /// <summary>진행 중인 드래그 번호(드래그가 아니면 0). 값 변경을 받는 쪽이 한 드래그의 변경을 Undo 한 단계로 합칠 때 쓴다.</summary>
    public static int ActiveDrag { get; private set; }
    private static int _dragCounter;
    private float _lastX;
    private double _accum;

    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                if (mb.Pressed)
                {
                    var spin = FindSpin(GetViewport().GuiGetHoveredControl());
                    if (spin == null || !spin.IsVisibleInTree() || !spin.Editable) return;
                    _spin = spin; _lastX = mb.Position.X; _accum = spin.Value; ActiveDrag = ++_dragCounter;
                    spin.GetLineEdit().ReleaseFocus();
                    DisplayServer.CursorSetShape(DisplayServer.CursorShape.Hsize);
                    GetViewport().SetInputAsHandled();
                }
                else if (_spin != null)
                {
                    _spin = null; ActiveDrag = 0;
                    DisplayServer.CursorSetShape(DisplayServer.CursorShape.Arrow);
                    GetViewport().SetInputAsHandled();
                }
                break;
            case InputEventMouseMotion mm when _spin != null:
                {
                    float dx = mm.Position.X - _lastX; _lastX = mm.Position.X;
                    if (!IsInstanceValid(_spin)) { _spin = null; return; }
                    double step = _spin.Step;
                    double unit = step >= 1 ? 0.2 : Math.Max(step, 0.005);
                    if (mm.ShiftPressed) unit *= 0.1;
                    _accum += dx * unit;
                    double v = _accum;
                    if (step > 0) v = Math.Round(v / step) * step;
                    if (!_spin.AllowGreater) v = Math.Min(v, _spin.MaxValue);
                    if (!_spin.AllowLesser) v = Math.Max(v, _spin.MinValue);
                    if (Math.Abs(v - _spin.Value) > 1e-12) _spin.Value = v;
                    DisplayServer.CursorSetShape(DisplayServer.CursorShape.Hsize);
                    GetViewport().SetInputAsHandled();
                    break;
                }
        }
    }

    private static SpinBox? FindSpin(Control? c)
    {
        for (var n = c as Node; n != null; n = n.GetParent())
        {
            if (n is SpinBox sb) return sb;
            if (n is Window) break;
        }
        return null;
    }
}
