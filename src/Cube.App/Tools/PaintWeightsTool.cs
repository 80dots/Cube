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
/// <remarks>
/// 대상 = 선택에서 스킨이 있는 첫 메시(없으면 문서의 첫 스킨 메시), 영향 = 선택된 조인트의 스킨 내 인덱스(없으면 0).
/// 브러시는 화면 레이와 메시 표면의 교점을 중심으로 월드 반지름 <see cref="Radius"/> 안의 정점에 선형 감쇠(1 - d/r) 세기로
/// <see cref="SkinOps.PaintVertex"/>를 적용한다(다른 조인트는 비율 유지 정규화). 스트로크 동안 바뀐 정점의 원래 가중치를 모아
/// 놓을 때 WeightPaintCommand(alreadyApplied)로 푸시한다. 툴 창(PaintWeightsWindow)이 Mode/Value/Radius/Joint를 바꾼다.
/// </remarks>
public sealed class PaintWeightsTool : ToolBase
{
    /// <summary>툴 ID("paintWeights", skin.paintTool).</summary>
    public override string Id => "paintWeights";
    /// <summary>표시 이름.</summary>
    public override string Label => "Paint Skin Weights";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Paint Skin Weights: drag to paint the current influence. Choose influence/mode/value/radius in the tool window. Q returns to Select.";

    /// <summary>칠할 스킨 메시 노드(없으면 null).</summary>
    public SceneNode? Mesh { get; private set; }
    /// <summary>현재 영향의 스킨 조인트 인덱스(SkinCluster 조인트 목록 기준, 노드 ID 아님).</summary>
    public int Joint { get; private set; }
    /// <summary>칠하기 방식(Replace/Add/Smooth).</summary>
    public PaintMode Mode = PaintMode.Replace;
    /// <summary>칠할 값(0~1). Smooth 모드에서는 이웃 평균 쪽으로 섞는 데 쓰인다.</summary>
    public float Value = 1f;
    /// <summary>브러시 반지름(월드 m).</summary>
    public float Radius = 0.25f;

    /// <summary>대상 메시/영향이 다시 정해짐(툴 창의 영향 목록 갱신용).</summary>
    public event Action? TargetChanged;

    /// <summary>스트로크(LMB 드래그) 중인지.</summary>
    private bool _stroking;
    /// <summary>이번 스트로크에서 처음 건드린 정점별 원래 가중치(Undo before·취소 복원용).</summary>
    private readonly Dictionary<int, List<(int joint, float weight)>> _before = new();

    /// <summary>활성화: 선택에서 대상을 정하고 툴 창을 띄운 뒤 가중치 흑백 표시를 켠다.</summary>
    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        RetargetFromSelection();
        UI.Shell.Instance.ShowPaintWeightsWindow(this);
        ApplyDisplay();
    }

    /// <summary>비활성화: 가중치 표시를 끄고 모든 뷰 갱신, 툴 창 숨김, 브러시 원 제거.</summary>
    public override void Deactivate()
    {
        base.Deactivate();
        UI.Shell.Instance.WeightDisplay = null;
        UI.Shell.Instance.RefreshAllDisplays();
        UI.Shell.Instance.HidePaintWeightsWindow();
        foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Brush = null;
    }

    /// <summary>스트로크 중이면 바뀐 정점의 가중치를 원래대로 되돌린다(Esc).</summary>
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
    /// <remarks>선택 조인트가 그 메시 스킨에 없으면 영향 0을 쓴다. 대상이 없으면 헬프 라인에 바인드 안내.</remarks>
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

    /// <summary>현재 영향을 바꾸고 표시를 갱신한다(툴 창 목록 선택).</summary>
    public void SetJoint(int index)
    {
        Joint = index;
        ApplyDisplay();
    }

    /// <summary>가중치 표시 설정: 대상 메시의 정점 → 현재 영향 가중치 함수를 Shell.WeightDisplay로 넘겨 표면을 흑백 램프로 그리게 한다.</summary>
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
    /// <remarks>살아 있는 모든 정점에 세기 1로 PaintVertex를 적용하고 하나의 WeightPaintCommand로 기록한다.</remarks>
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

    /// <summary>
    /// LMB 누름 = 스트로크 시작(대상이 없으면 선택에서 다시 찾음, 그래도 없으면 소비 안 함), 뗌 = 스트로크 끝,
    /// 이동 = 브러시 원 갱신(+ 스트로크 중이면 칠하기), Esc = 스트로크 취소.
    /// </summary>
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

    /// <summary>화면 점에서 레이를 쏴 대상 메시 표면과의 교점(월드)을 구한다.</summary>
    /// <returns>대상 메시를 맞혔으면 true.</returns>
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

    /// <summary>브러시 원 오버레이: 교점의 화면 위치와, 교점에서 카메라 오른쪽으로 Radius만큼 떨어진 점까지의 화면 거리(반지름).</summary>
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

    /// <summary>커서 아래 교점 주변 정점에 가중치를 칠한다(처음 건드린 정점은 원래 값 저장).</summary>
    private void PaintAt(NVec2 px)
    {
        if (Mesh?.Mesh is not { } m || Mesh.Skin is not { } skin) return;
        if (!Hit(px, out var hitWorld, out var target) || target == null) return;
        var hitLocal = NVec3.Transform(hitWorld, target.WorldInverse);
        // 브러시 반지름은 월드 단위 → 메시 로컬 스케일 보정(균등 스케일 가정)
        float scale = NVec3.TransformNormal(NVec3.UnitX, target.World).Length();
        float rLocal = Radius / MathF.Max(scale, 1e-6f);
        bool any = false;
        // 반지름 안의 살아 있는 정점마다 거리 감쇠 세기로 칠하기
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

    /// <summary>스트로크 끝: 건드린 정점의 before/after 가중치로 WeightPaintCommand를 alreadyApplied 푸시한다(변화 없으면 생략).</summary>
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

/// <summary>이 파일 전용 확장: Document의 선택 상태 바로가기.</summary>
internal static class DocExt
{
    /// <summary>문서의 선택 상태를 돌려준다.</summary>
    public static Core.Selection.SelectionState Sel(this Document d) => d.Selection;
}
