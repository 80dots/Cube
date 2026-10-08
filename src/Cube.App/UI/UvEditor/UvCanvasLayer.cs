using Godot;

namespace Cube.App.UI.UvEditor;

/// <summary>
/// UvCanvas 뒤에 겹쳐 그리는 레이어(ShowBehindParent → 부모의 덧그림보다 먼저, 자식 순서대로 그려진다).
/// 배경/메시 배치/굵은 선/UV 점을 서로 다른 레이어에 두어 셰이더 머티리얼(줌과 무관한 픽셀 굵기)을 레이어별로 쓴다. 입력은 받지 않는다.
/// 동작: 부모 <c>UvCanvas</c>가 레이어마다 <see cref="Paint"/> 콜백을 넣어 두고 QueueRedraw를 부르면,
/// Godot이 이 레이어의 _Draw에서 콜백을 실행해 해당 레이어의 CanvasItem에 그리기 명령을 쌓는다.
/// 레이어 자체는 상태가 없고 "어디에 그릴지"만 제공하는 얇은 그리기 대상이다.
/// </summary>
public partial class UvCanvasLayer : Control
{
    /// <summary>
    /// 이 레이어를 다시 그릴 때 호출되는 그리기 콜백. 인자로 자기 자신(그리기 대상 CanvasItem)을 넘긴다.
    /// null이면 아무것도 그리지 않는다. UvCanvas가 레이어를 만들 때 배경/면/선/점 그리기 함수를 연결한다.
    /// </summary>
    public Action<UvCanvasLayer>? Paint;

    /// <summary>
    /// 생성자: 입력을 통과시키고(MouseFilter Ignore — 마우스 이벤트는 부모 UvCanvas가 받는다),
    /// 부모보다 먼저 그려지도록 ShowBehindParent를 켜며, 포커스를 받지 않게 한다.
    /// </summary>
    public UvCanvasLayer()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ShowBehindParent = true;
        FocusMode = FocusModeEnum.None;
    }

    /// <summary>Godot 그리기 콜백. 등록된 <see cref="Paint"/>에 위임한다.</summary>
    public override void _Draw() => Paint?.Invoke(this);
}
