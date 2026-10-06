using Godot;

namespace Cube.App;

/// <summary>
/// 앱 전역 autoload. Hi-DPI 배율 적용과 코어 어셈블리 로드 확인을 담당한다.
/// 문서(Document)·툴·설정 같은 전역 서비스는 이후 단계에서 여기에 모인다.
/// </summary>
public partial class CubeApp : Node
{
    public static CubeApp Instance { get; private set; } = null!;

    /// <summary>화면 배율. 픽셀 단위 상수(점 크기, 피킹 임계값)에 곱해 쓴다.</summary>
    public float UiScale { get; private set; } = 1f;

    public override void _Ready()
    {
        Instance = this;
        UiScale = (float)DisplayServer.ScreenGetScale();
        GetWindow().ContentScaleFactor = UiScale;
        GD.Print($"[Cube] core={Core.CoreInfo.Name} uiScale={UiScale}");
    }
}
