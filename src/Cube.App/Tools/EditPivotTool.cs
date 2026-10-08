using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Maya Edit Pivot(Insert): Move 조작기로 선택 오브젝트의 회전/스케일 피벗만 옮긴다(오브젝트는 그대로).
/// X/V 홀드(또는 상태 라인 스냅 토글)로 그리드/점 스냅이 되며, 점 스냅은 자기 정점에도 붙는다. Insert/Esc 또는 다른 툴로 종료.
/// </summary>
/// <remarks>MoveTool의 드래그·스냅 수학을 그대로 쓰고 적용 단계만 피벗 이동(<see cref="TransformToolBase.ApplyPivotMove"/>/<see cref="TransformToolBase.ApplyPivotTo"/>)으로 바꾼다.</remarks>
public sealed class EditPivotTool : MoveTool
{
    /// <summary>툴 ID("editPivot", edit.editPivot 액션이 전환).</summary>
    public override string Id => "editPivot";
    /// <summary>표시 이름(커밋 명령 이름).</summary>
    public override string Label => "Edit Pivot";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Edit Pivot: drag the manipulator to move the pivot (the object stays). Hold X to snap to grid, V to snap to points. Press Insert or Esc to finish.";

    /// <summary>피벗은 자기 메시 정점에도 스냅해야 하므로 움직이는 대상을 후보에서 빼지 않는다.</summary>
    protected override bool ExcludeMovingFromPointSnap => false;
    /// <summary>이동 델타를 피벗에만 적용(월드 행렬 유지).</summary>
    protected override void ApplyMove(NVec3 worldDelta) => ApplyPivotMove(worldDelta);
    /// <summary>점 스냅 collapse: 모든 선택 오브젝트의 피벗을 목표점으로.</summary>
    protected override void ApplyCollapse(NVec3 worldTarget) => ApplyPivotTo(worldTarget);
}
