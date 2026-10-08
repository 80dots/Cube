using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Maya Create Polygon Tool: 뷰포트에 점을 찍어 n각형 하나를 만든다. 원근 뷰는 지면 평면(y=0), 직교 뷰는 원점을 지나는 화면 평면.
/// Enter = 완성(3점 이상), Esc/Q = 취소. 완성된 면은 카메라를 향하는 쪽(직교)/위(원근)가 앞면이다.
/// </summary>
/// <remarks>
/// 점은 월드 좌표로 모아 두고 Enter 때 <see cref="MeshBuilder.Polygon"/>(법선 힌트로 CCW 보정)으로 메시를 만들어
/// AddNodeCommand로 새 노드("polySurface1" 기반 고유 이름)를 추가한다. 찍는 동안 오버레이 폴리라인으로 표시한다.
/// </remarks>
public sealed class CreatePolygonTool : ToolBase
{
    /// <summary>툴 ID("createPolygon").</summary>
    public override string Id => "createPolygon";
    /// <summary>표시 이름.</summary>
    public override string Label => "Create Polygon Tool";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Create Polygon Tool: click to place vertices, Enter to finish (3+ points), Esc to cancel.";

    /// <summary>찍은 점들(월드).</summary>
    private readonly List<NVec3> _points = new();
    /// <summary>면 앞면 방향 힌트(원근 = +Y, 직교 = 카메라 쪽). 마지막 점을 찍을 때의 뷰 기준.</summary>
    private NVec3 _normal = NVec3.UnitY;

    /// <summary>활성화: 점 목록을 비우고 오버레이 갱신.</summary>
    public override void Activate(ToolContext ctx) { base.Activate(ctx); _points.Clear(); UpdateOverlay(); }
    /// <summary>비활성화: 점과 오버레이 정리.</summary>
    public override void Deactivate() { base.Deactivate(); _points.Clear(); ClearOverlay(); }
    /// <summary>취소: 점과 오버레이 정리.</summary>
    public override void Cancel() { _points.Clear(); ClearOverlay(); }

    /// <summary>LMB 누름 = 점 추가(뗌도 소비), 이동 = 오버레이 갱신, Enter = 완성, Esc = 취소.</summary>
    public override bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                AddPoint(new NVec2(mb.Position.X, mb.Position.Y));
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                return true;
            case InputEventMouseMotion:
                UpdateOverlay();
                return false;
            case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Enter or Key.KpEnter:
                Finish();
                return true;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                Cancel();
                Ctx.SetHelp?.Invoke(HelpText);
                return true;
        }
        return false;
    }

    /// <summary>점을 놓을 평면: 직교 뷰 = 원점을 지나는 화면 평면, 원근 뷰 = 지면(y = 0).</summary>
    private bool Plane(out NVec3 point, out NVec3 normal)
    {
        var proj = Ctx.Viewport.Picker.Projection();
        if (proj.IsOrtho) { normal = -proj.Forward; point = NVec3.Zero; }
        else { normal = NVec3.UnitY; point = NVec3.Zero; }
        return true;
    }

    /// <summary>화면 점을 평면에 투영해 점을 추가한다. 레이가 평면과 거의 평행하면 화면 평면을 쓴다.</summary>
    private void AddPoint(NVec2 px)
    {
        var proj = Ctx.Viewport.Picker.Projection();
        Plane(out var pp, out var n);
        var ray = proj.Unproject(px);
        if (MathF.Abs(NVec3.Dot(ray.Direction, n)) < 1e-3f) n = -proj.Forward;
        if (!Core.Geometry.DragMath.RayPlane(ray, pp, n, out var hit)) return;
        _points.Add(hit);
        _normal = proj.IsOrtho ? -proj.Forward : NVec3.UnitY;
        UpdateOverlay();
        Ctx.SetHelp?.Invoke($"Create Polygon Tool: {_points.Count} point(s). Enter to finish, Esc to cancel.");
    }

    /// <summary>점이 3개 이상이면 폴리곤 메시 노드를 만들어 추가한다(퇴화면 점을 버림). 툴은 계속 켜져 있어 다음 면을 이어서 만들 수 있다.</summary>
    private void Finish()
    {
        if (_points.Count < 3) { Ctx.SetHelp?.Invoke("Create Polygon Tool: need at least 3 points."); return; }
        var mesh = MeshBuilder.Polygon(_points.ToList(), _normal);
        if (mesh.AliveFaceCount == 0) { Ctx.SetHelp?.Invoke("Create Polygon Tool: points are degenerate."); _points.Clear(); ClearOverlay(); return; }
        var node = new SceneNode { Name = Ctx.Doc.UniqueName("polySurface1"), Shape = new MeshShape(mesh) };
        Ctx.Undo.Push(new AddNodeCommand("Create Polygon", node));
        _points.Clear();
        ClearOverlay();
        Ctx.SetHelp?.Invoke($"Create Polygon Tool: {node.Name} created. Click to start another, Q to return to Select.");
    }

    /// <summary>찍은 점들을 화면에 투영해 활성 패널 오버레이 폴리라인으로 그린다.</summary>
    private void UpdateOverlay()
    {
        var overlay = Ctx.Viewport.Overlay;
        var proj = Ctx.Viewport.Picker.Projection();
        var pts = new List<Godot.Vector2>();
        foreach (var p in _points) { var sp = proj.Project(p, out _); if (sp != null) pts.Add(new Godot.Vector2(sp.Value.X, sp.Value.Y)); }
        overlay.Polyline = pts.Count > 0 ? pts : null;
    }

    /// <summary>모든 패널의 오버레이 폴리라인을 지운다(활성 패널이 바뀌었을 수 있으므로 전부).</summary>
    private void ClearOverlay() { foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Polyline = null; }
}
