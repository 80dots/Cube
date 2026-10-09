using Godot;

namespace Cube.App.UI;

/// <summary>
/// 숫자 입력칸 가상 슬라이더: SpinBox(또는 그 안의 LineEdit) 위에서 **가운데 버튼을 누른 채 좌우로 끌면** 값을 줄이고 늘린다.
/// Shift를 누르고 있으면 1/10 속도로 세밀하게 바뀐다. 한 픽셀당 변화량은 정수 칸이면 0.2(5픽셀에 1), 실수 칸이면 Step(최소 0.005).
/// 이 노드를 넣은 뷰포트(셸 루트, 다이얼로그 Window)의 컨트롤에 적용된다.
/// </summary>
/// <remarks>
/// 동작: MMB 누름 → 호버 컨트롤에서 위로 올라가며 SpinBox를 찾아 드래그 시작(값 누적기 = 현재 값, 커서 = 좌우 화살표) →
/// 마우스 이동 dx마다 누적기에 dx × 단위를 더하고 Step으로 반올림, 범위를 넘지 않게 자른 뒤 Value에 넣음 → MMB 놓음으로 종료.
/// 누적기를 따로 두는 이유: Step 반올림 때문에 작은 이동이 매번 버려지지 않도록 반올림 전 연속값을 유지한다.
/// </remarks>
public partial class SpinDrag : Node
{
    /// <summary>현재 드래그 중인 SpinBox(드래그가 아니면 null).</summary>
    private SpinBox? _spin;
    /// <summary>진행 중인 드래그 번호(드래그가 아니면 0). 값 변경을 받는 쪽이 한 드래그의 변경을 Undo 한 단계로 합칠 때 쓴다.</summary>
    public static int ActiveDrag { get; private set; }
    /// <summary>SpinBox 메타데이터 키: 있으면 MMB 드래그의 픽셀당 변화량(Shift 전)으로 쓴다.</summary>
    public const string DragUnitMeta = "drag_unit";
    /// <summary>드래그 번호를 만들기 위한 전역 카운터(드래그마다 1 증가, 모든 SpinDrag 인스턴스가 공유).</summary>
    private static int _dragCounter;
    /// <summary>직전 마우스 이벤트의 X 좌표(뷰포트 로컬 px). 다음 모션의 dx 계산용.</summary>
    private float _lastX;
    /// <summary>반올림 전 연속 값 누적기. 드래그 시작 시 SpinBox 값으로 초기화된다.</summary>
    private double _accum;

    /// <summary>
    /// 뷰포트 전체의 입력을 GUI보다 먼저 받아 MMB 드래그를 가로챈다.
    /// SpinBox 위가 아니면 아무것도 하지 않아 다른 MMB 동작(뷰포트 내비게이션 등)에 영향이 없다.
    /// </summary>
    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            // 가운데 버튼 누름/놓음: 드래그 시작과 종료.
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                if (mb.Pressed)
                {
                    // 마우스 아래 컨트롤(또는 그 조상)이 편집 가능한 SpinBox일 때만 시작.
                    var spin = FindSpin(GetViewport().GuiGetHoveredControl());
                    if (spin == null || !spin.IsVisibleInTree() || !spin.Editable) return;
                    _spin = spin; _lastX = mb.Position.X; _accum = spin.Value; ActiveDrag = ++_dragCounter;
                    // 텍스트 편집 중이던 값과 충돌하지 않도록 입력칸 포커스를 해제한다.
                    spin.GetLineEdit().ReleaseFocus();
                    DisplayServer.CursorSetShape(DisplayServer.CursorShape.Hsize);
                    GetViewport().SetInputAsHandled();
                }
                else if (_spin != null)
                {
                    // 드래그 종료: 상태와 드래그 번호를 지우고 커서를 되돌린다.
                    _spin = null; ActiveDrag = 0;
                    DisplayServer.CursorSetShape(DisplayServer.CursorShape.Arrow);
                    GetViewport().SetInputAsHandled();
                }
                break;
            // 드래그 중 이동: 가로 이동량만 값 변화로 바꾼다.
            case InputEventMouseMotion mm when _spin != null:
                {
                    float dx = mm.Position.X - _lastX; _lastX = mm.Position.X;
                    // 드래그 도중 SpinBox가 해제(패널 재생성 등)되었으면 조용히 끝낸다.
                    if (!IsInstanceValid(_spin)) { _spin = null; return; }
                    // 픽셀당 변화량: 정수 칸(Step ≥ 1)은 0.2, 실수 칸은 Step(너무 작으면 0.005). Shift는 1/10.
                    double step = _spin.Step;
                    double unit = step >= 1 ? 0.2 : Math.Max(step, 0.005);
                    // 칸이 픽셀당 변화량을 직접 지정했으면 그 값(예: Properties의 Rotate = 0.5°/px; 증분이 아주 작은 칸이 너무 느리지 않도록)
                    if (_spin.HasMeta(DragUnitMeta)) unit = _spin.GetMeta(DragUnitMeta).AsDouble();
                    if (mm.ShiftPressed) unit *= 0.1;
                    _accum += dx * unit;
                    double v = _accum;
                    // Step 격자로 반올림하고, 범위 밖 허용 플래그가 없으면 Min/Max로 자른다.
                    if (step > 0) v = Math.Round(v / step) * step;
                    if (!_spin.AllowGreater) v = Math.Min(v, _spin.MaxValue);
                    if (!_spin.AllowLesser) v = Math.Max(v, _spin.MinValue);
                    // 값이 실제로 바뀔 때만 대입해 ValueChanged 신호가 불필요하게 나가지 않게 한다.
                    if (Math.Abs(v - _spin.Value) > 1e-12) _spin.Value = v;
                    DisplayServer.CursorSetShape(DisplayServer.CursorShape.Hsize);
                    GetViewport().SetInputAsHandled();
                    break;
                }
        }
    }

    /// <summary>컨트롤에서 부모 방향으로 올라가며 처음 만나는 SpinBox를 찾는다(내부 LineEdit 위를 눌렀을 때 대비). Window 경계를 넘지 않는다.</summary>
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
