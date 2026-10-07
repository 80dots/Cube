using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Picking;
using Cube.Core.Rig;
using Cube.Core.Scene;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Maya Paint Skin Weights Tool. 선택된 스킨 메시 위에서 브러시로 현재 영향(조인트)의 가중치를 칠한다.
/// 표면은 현재 영향의 가중치를 흑백 램프로 보여 준다. 스트로크 하나가 Undo 한 스텝.
/// </summary>
public sealed class PaintWeightsTool : ToolBase
{
    public override string Id => "paintWeights";
    public override string Label => "Paint Skin Weights";
    public override string HelpText => "Paint Skin Weights: drag to paint the current influence. Choose influence/mode/value/radius in the tool window. Q returns to Select.";

    public SceneNode? Mesh { get; private set; }
    public int Joint { get; private set; }
    public PaintMode Mode = PaintMode.Replace;
    public float Value = 1f;
    public float Radius = 0.25f;

    public event Action? TargetChanged;

    private bool _stroking;
    private readonly Dictionary<int, List<(int joint, float weight)>> _before = new();

    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        RetargetFromSelection();
        UI.Shell.Instance.ShowPaintWeightsWindow(this);
        ApplyDisplay();
    }

    public override void Deactivate()
    {
        base.Deactivate();
        UI.Shell.Instance.WeightDisplay = null;
        UI.Shell.Instance.RefreshAllDisplays();
        UI.Shell.Instance.HidePaintWeightsWindow();
        foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Brush = null;
    }

    public override void Cancel()
    {
        if (!_stroking) return;
        _stroking = false;
        if (Mesh?.Skin is { } skin)
        {
            foreach (var (v, w) in _before) skin.SetWeights(v, w);
            Ctx.Doc.Notify(new DocChange(ChangeKind.SkinChanged, Mesh.Id));
        }
        _before.Clear();
    }

    /// <summary>선택에서 대상 메시(스킨 있음)와 영향 조인트를 고른다.</summary>
    public void RetargetFromSelection()
    {
        var doc = Ctx.Doc; var sel = doc.Sel();
        SceneNode? mesh = null; SceneNode? joint = null;
        foreach (var id in sel.Objects)
        {
            var n = doc.Find(id); if (n == null) continue;
            if (n.Skin != null && mesh == null) mesh = n;
            if (n.IsJoint && joint == null) joint = n;
        }
        mesh ??= doc.SkinnedNodes().FirstOrDefault();
        Mesh = mesh;
        Joint = 0;
        if (mesh?.Skin != null && joint != null) { int j = mesh.Skin.JointIndexOf(joint.Id); if (j >= 0) Joint = j; }
        TargetChanged?.Invoke();
        if (Mesh == null) Ctx.SetHelp?.Invoke("Paint Skin Weights: no skinned mesh. Bind Skin first (Skin > Bind Skin).");
    }

    public void SetJoint(int index)
    {
        Joint = index;
        ApplyDisplay();
    }

    private void ApplyDisplay()
    {
        var shell = UI.Shell.Instance;
        if (Mesh?.Skin is { } skin)
        {
            int j = Joint; var s = skin;
            shell.WeightDisplay = (Mesh.Id, v => s.GetWeight(v, j));
        }
        else shell.WeightDisplay = null;
        shell.RefreshAllDisplays();
    }

    /// <summary>모든 정점에 현재 값 적용(Flood).</summary>
    public void Flood()
    {
        if (Mesh?.Mesh is not { } m || Mesh.Skin is not { } skin) return;
        var verts = Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive).ToArray();
        var before = verts.Select(v => skin.CopyWeights(v)).ToArray();
        foreach (int v in verts)
        {
            float avg = Mode == PaintMode.Smooth ? SkinOps.NeighborAverage(m, skin, v, Joint) : 0f;
            SkinOps.PaintVertex(skin, v, Joint, Mode, Value, 1f, avg);
        }
        var after = verts.Select(v => skin.CopyWeights(v)).ToArray();
        var cmd = new WeightPaintCommand(Mesh.Id, verts, before, after);
        if (!cmd.IsNoop) Ctx.Undo.Push(cmd, alreadyApplied: true);
        Ctx.Doc.Notify(new DocChange(ChangeKind.SkinChanged, Mesh.Id));
    }

    public override bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed)
                {
                    if (Mesh?.Skin == null) { RetargetFromSelection(); ApplyDisplay(); if (Mesh?.Skin == null) return false; }
                    _stroking = true; _before.Clear();
                    PaintAt(new NVec2(mb.Position.X, mb.Position.Y));
                    return true;
                }
                if (_stroking) { EndStroke(); return true; }
                return false;
            case InputEventMouseMotion mm:
                {
                    var px = new NVec2(mm.Position.X, mm.Position.Y);
                    UpdateBrush(px);
                    if (_stroking) { PaintAt(px); return true; }
                    return false;
                }
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } when _stroking:
                Cancel();
                return true;
        }
        return false;
    }

    private bool Hit(NVec2 px, out NVec3 worldHit, out PickTarget? target)
    {
        worldHit = default; target = null;
        if (Mesh == null) return false;
        var picker = Ctx.Viewport.Picker;
        target = picker.Targets().FirstOrDefault(t => t.Id == Mesh.Id);
        if (target == null) return false;
        var ray = picker.Projection().Unproject(px);
        return RayPicker.RaycastTarget(target, ray, out _, out _, out worldHit);
    }

    private void UpdateBrush(NVec2 px)
    {
        var overlay = Ctx.Viewport.Overlay;
        if (!Hit(px, out var hit, out _)) { overlay.Brush = null; return; }
        var proj = Ctx.Viewport.Picker.Projection();
        var c = proj.Project(hit, out _);
        var r = proj.Project(hit + proj.Right * Radius, out _);
        if (c == null || r == null) { overlay.Brush = null; return; }
        overlay.Brush = (new Godot.Vector2(c.Value.X, c.Value.Y), NVec2.Distance(c.Value, r.Value));
    }

    private void PaintAt(NVec2 px)
    {
        if (Mesh?.Mesh is not { } m || Mesh.Skin is not { } skin) return;
        if (!Hit(px, out var hitWorld, out var target) || target == null) return;
        var hitLocal = NVec3.Transform(hitWorld, target.WorldInverse);
        // 브러시 반지름은 월드 단위 → 메시 로컬 스케일 보정(균등 스케일 가정)
        float scale = NVec3.TransformNormal(NVec3.UnitX, target.World).Length();
        float rLocal = Radius / MathF.Max(scale, 1e-6f);
        bool any = false;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            float d = NVec3.Distance(m.Verts[v].Position, hitLocal);
            if (d > rLocal) continue;
            float amount = 1f - d / rLocal;
            if (!_before.ContainsKey(v)) _before[v] = skin.CopyWeights(v);
            float avg = Mode == PaintMode.Smooth ? SkinOps.NeighborAverage(m, skin, v, Joint) : 0f;
            SkinOps.PaintVertex(skin, v, Joint, Mode, Value, amount, avg);
            any = true;
        }
        if (any) Ctx.Doc.Notify(new DocChange(ChangeKind.SkinChanged, Mesh.Id));
    }

    private void EndStroke()
    {
        _stroking = false;
        if (Mesh?.Skin is not { } skin || _before.Count == 0) { _before.Clear(); return; }
        var verts = _before.Keys.ToArray();
        var before = verts.Select(v => _before[v]).ToArray();
        var after = verts.Select(v => skin.CopyWeights(v)).ToArray();
        _before.Clear();
        var cmd = new WeightPaintCommand(Mesh.Id, verts, before, after);
        if (!cmd.IsNoop) Ctx.Undo.Push(cmd, alreadyApplied: true);
    }
}

internal static class DocExt
{
    public static Core.Selection.SelectionState Sel(this Document d) => d.Selection;
}
