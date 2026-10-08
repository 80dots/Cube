using Godot;

namespace Cube.App.UI.UvEditor;

/// <summary>
/// UvCanvas 뒤에 겹쳐 그리는 레이어(ShowBehindParent → 부모의 덧그림보다 먼저, 자식 순서대로 그려진다).
/// 배경/메시 배치/굵은 선/UV 점을 서로 다른 레이어에 두어 셰이더 머티리얼(줌과 무관한 픽셀 굵기)을 레이어별로 쓴다. 입력은 받지 않는다.
/// </summary>
public partial class UvCanvasLayer : Control
{
    public Action<UvCanvasLayer>? Paint;

    public UvCanvasLayer()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ShowBehindParent = true;
        FocusMode = FocusModeEnum.None;
    }

    public override void _Draw() => Paint?.Invoke(this);
}
