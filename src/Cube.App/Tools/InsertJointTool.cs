using Cube.Core.Commands;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;

namespace Cube.App.Tools;

/// <summary>Maya Insert Joint Tool: 본(조인트와 자식 조인트 사이)을 클릭한 위치에 조인트를 끼운다. 자식의 월드 위치는 유지된다.</summary>
public sealed class InsertJointTool : SelectTool
{
    public override string Id => "insertJoint";
    public override string Label => "Insert Joint";
    public override string HelpText => "Insert Joint Tool: click on a bone to insert a joint there. Q returns to Select.";

    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode != SelectMode.Object) ctx.Sel.Mode = SelectMode.Object;
    }

    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        var px = new NVec2(mb.Position.X, mb.Position.Y);
        if (!Picker.PickBone(px, out var parent, out var child, out float t)) return false;
        Ctx.Undo.Push(new InsertJointCommand(parent, child, t));
        SetHover(null);
        return true;
    }
}
