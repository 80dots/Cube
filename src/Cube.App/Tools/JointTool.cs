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
/// <remarks>
/// 새 조인트는 AddNodeCommand로 _current(직전 조인트)의 자식으로 추가되며, Local은 월드 위치를 부모 월드 역행렬로 바꾼 이동만 갖는다(회전 0, 스케일 1).
/// 조인트 방향(Orient)은 별도 Orient Joint 명령으로 맞춘다.
/// </remarks>
public sealed class JointTool : ToolBase
{
    /// <summary>툴 ID("joint", skeleton.jointTool).</summary>
    public override string Id => "joint";
    /// <summary>표시 이름.</summary>
    public override string Label => "Joint Tool";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Joint Tool: click to place joints in a chain. Enter finishes, Esc starts a new chain, Q returns to Select.";

    /// <summary>체인의 마지막 조인트(다음 조인트의 부모). null이면 다음 클릭이 루트 조인트가 된다.</summary>
    private SceneNode? _current;

    /// <summary>활성화: 선택된 조인트가 있으면 그 조인트에서 체인을 이어 가고, 오브젝트 모드로 전환한다.</summary>
    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        var sel = ctx.Sel;
        _current = sel.Mode == SelectMode.Object ? ctx.Doc.Find(sel.ActiveObject) : null;
        if (_current != null && !_current.IsJoint) _current = null;
        if (ctx.Sel.Mode != SelectMode.Object) ctx.Sel.Mode = SelectMode.Object;
    }

    /// <summary>취소: 체인을 끊는다(다음 클릭은 새 루트).</summary>
    public override void Cancel() { _current = null; }

    /// <summary>LMB 누름 = 조인트 배치(뗌도 소비), Enter = 체인 완료 후 Select 툴, Esc = 새 체인 시작.</summary>
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

    /// <summary>
    /// 화면 점을 배치 평면(원근 = 마지막 조인트 높이의 수평면, 직교 = 마지막 조인트를 지나는 화면 평면)에 투영해 조인트를 만든다.
    /// 레이가 평면과 거의 평행하면 화면 평면을 쓴다.
    /// </summary>
    private void PlaceJoint(NVec2 px)
    {
        // Undo 등으로 이어 붙일 조인트가 문서에서 빠졌으면 선택된 조인트(없으면 새 체인)에서 이어간다
        // (전에는 사라진 부모 ID로 AddNodeCommand를 만들어 KeyNotFoundException).
        if (_current != null && Ctx.Doc.Find(_current.Id) != _current)
        {
            var active = Ctx.Doc.Find(Ctx.Sel.ActiveObject);
            _current = active is { IsJoint: true } ? active : null;
        }
        var proj = Ctx.Viewport.Picker.Projection();
        var ray = proj.Unproject(px);
        var anchor = _current?.WorldMatrix.Translation ?? NVec3.Zero;
        NVec3 normal = proj.IsOrtho ? -proj.Forward : NVec3.UnitY;
        if (MathF.Abs(NVec3.Dot(ray.Direction, normal)) < 1e-3f) normal = -proj.Forward;
        if (!Core.Geometry.DragMath.RayPlane(ray, anchor, normal, out var world)) return;

        // 부모가 있으면 월드 위치를 부모 로컬로 변환해 이동만 설정
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
