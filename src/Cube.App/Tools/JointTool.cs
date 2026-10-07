using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Maya Joint Tool: 클릭할 때마다 조인트를 만든다. 이전에 만든(또는 선택된) 조인트의 자식으로 이어져 체인이 된다.
/// 원근 뷰는 마지막 조인트 높이의 지면 평면, 직교 뷰는 마지막 조인트를 지나는 화면 평면에 놓는다.
/// Enter = 체인 완료(선택 툴로), Esc = 새 체인 시작, Q = 선택 툴.
/// </summary>
public sealed class JointTool : ToolBase
{
    public override string Id => "joint";
    public override string Label => "Joint Tool";
    public override string HelpText => "Joint Tool: click to place joints in a chain. Enter finishes, Esc starts a new chain, Q returns to Select.";

    private SceneNode? _current;

    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        var sel = ctx.Sel;
        _current = sel.Mode == SelectMode.Object ? ctx.Doc.Find(sel.ActiveObject) : null;
        if (_current != null && !_current.IsJoint) _current = null;
        if (ctx.Sel.Mode != SelectMode.Object) ctx.Sel.Mode = SelectMode.Object;
    }

    public override void Cancel() { _current = null; }

    public override bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                PlaceJoint(new NVec2(mb.Position.X, mb.Position.Y));
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                return true;
            case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Enter or Key.KpEnter:
                _current = null;
                UI.Shell.Instance.Tools.SetTool("select");
                return true;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                _current = null;
                Ctx.SetHelp?.Invoke("Joint Tool: new chain. Click to place the root joint.");
                return true;
        }
        return false;
    }

    private void PlaceJoint(NVec2 px)
    {
        var proj = Ctx.Viewport.Picker.Projection();
        var ray = proj.Unproject(px);
        var anchor = _current?.WorldMatrix.Translation ?? NVec3.Zero;
        NVec3 normal = proj.IsOrtho ? -proj.Forward : NVec3.UnitY;
        if (MathF.Abs(NVec3.Dot(ray.Direction, normal)) < 1e-3f) normal = -proj.Forward;
        if (!Core.Geometry.DragMath.RayPlane(ray, anchor, normal, out var world)) return;

        var node = new SceneNode { Name = Ctx.Doc.UniqueName("joint1"), Shape = new JointShape() };
        if (_current != null)
        {
            Matrix4x4.Invert(_current.WorldMatrix, out var inv);
            node.Local = new Transform3(NVec3.Transform(world, inv), NVec3.Zero, NVec3.One);
        }
        else node.Local = new Transform3(world, NVec3.Zero, NVec3.One);
        var cmd = new AddNodeCommand("Create Joint", node, _current?.Id ?? default);
        Ctx.Undo.Push(cmd);
        _current = node;
        Ctx.SetHelp?.Invoke($"Joint Tool: {node.Name} placed. Click to add a child joint, Enter to finish.");
    }
}
