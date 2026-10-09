namespace Cube.App.Tools;

/// <summary>
/// Maya Lasso Tool: 드래그로 자유 곡선을 그려 그 안의 오브젝트/컴포넌트를 선택한다(놓으면 끝점 → 시작점으로 닫힘).
/// 클릭(드래그 없음)은 Select Tool과 같은 클릭 선택이며 수식어(Shift 토글, Ctrl 제거, Ctrl+Shift 추가)와
/// 가려진 요소 포함 여부(Settings.MarqueeSelectThrough)도 마키와 같다. 구현은 <see cref="SelectTool"/>의 드래그 분기만 바꾼다.
/// </summary>
public class LassoTool : SelectTool
{
    /// <summary>툴 ID("lasso", 액션 tool.lasso).</summary>
    public override string Id => "lasso";
    /// <summary>표시 이름.</summary>
    public override string Label => "Lasso Tool";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Lasso Tool: drag a freeform outline to select inside it. Click selects. Shift toggles, Ctrl deselects, Ctrl+Shift adds.";
    /// <summary>드래그 선택을 자유 곡선으로.</summary>
    protected override bool UseLasso => true;
}
