using Cube.Core.Commands;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;

namespace Cube.App.Tools;

/// <summary>Maya Insert Joint Tool: 본(조인트와 자식 조인트 사이)을 클릭한 위치에 조인트를 끼운다. 자식의 월드 위치는 유지된다.</summary>
/// <remarks>
/// <see cref="Picker.PickBone"/>이 화면에서 가장 가까운 본(부모 조인트 → 자식 조인트 선분)과 그 위의 비율 t를 찾고,
/// <see cref="InsertJointCommand"/>가 부모와 자식 사이에 새 조인트를 넣는다. 본이 아니면 일반 선택으로 동작한다.
/// </remarks>
public sealed class InsertJointTool : SelectTool
{
    /// <summary>툴 ID("insertJoint").</summary>
    public override string Id => "insertJoint";
    /// <summary>표시 이름.</summary>
    public override string Label => "Insert Joint";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Insert Joint Tool: click on a bone to insert a joint there. Q returns to Select.";

    /// <summary>활성화: 조인트는 오브젝트로 다루므로 오브젝트 모드로 전환.</summary>
    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode != SelectMode.Object) ctx.Sel.Mode = SelectMode.Object;
    }

    /// <summary>왼쪽 누름: 본을 맞히면 그 위치(비율 t)에 조인트를 삽입하는 명령을 푸시하고 true, 아니면 false(선택으로).</summary>
    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        var px = new NVec2(mb.Position.X, mb.Position.Y);
        if (!Picker.PickBone(px, out var parent, out var child, out float t)) return false;
        Ctx.Undo.Push(new InsertJointCommand(parent, child, t));
        SetHover(null);
        return true;
    }
}
