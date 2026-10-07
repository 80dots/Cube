using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Maya Edit Pivot(Insert): Move 조작기로 선택 오브젝트의 회전/스케일 피벗만 옮긴다(오브젝트는 그대로).
/// X/V 홀드(또는 상태 라인 스냅 토글)로 그리드/점 스냅이 되며, 점 스냅은 자기 정점에도 붙는다. Insert/Esc 또는 다른 툴로 종료.
/// </summary>
public sealed class EditPivotTool : MoveTool
{
    public override string Id => "editPivot";
    public override string Label => "Edit Pivot";
    public override string HelpText => "Edit Pivot: drag the manipulator to move the pivot (the object stays). Hold X to snap to grid, V to snap to points. Press Insert or Esc to finish.";

    protected override bool ExcludeMovingFromPointSnap => false;
    protected override void ApplyMove(NVec3 worldDelta) => ApplyPivotMove(worldDelta);
    protected override void ApplyCollapse(NVec3 worldTarget) => ApplyPivotTo(worldTarget);
}
