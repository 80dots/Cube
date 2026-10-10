using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// Symmetry 모드(v0.0.64, Maya Tool Settings → Symmetry: Off / Object X·Y·Z / World X·Y·Z). 켜져 있으면
/// ① 컴포넌트 선택이 거울 짝까지 확장되고(<see cref="ApplySymmetryToSelection"/>; 제거하면 짝도 제거)
/// ② Move/Rotate/Scale이 − 쪽 정점에 거울 변형을, 평면 위 정점에 평면 투영을 적용하며(TransformToolBase → <see cref="SymmetryOps.Transform"/>)
/// ③ 선택 기반 연산(Extrude/Bevel/Delete…)은 확장된 선택으로 자동 대칭, 클릭형 툴(Insert Edge Loop/Multi-Cut/Target Weld)은 짝에 같은 연산을 더 실행한다.
/// 뷰포트에는 활성 메시의 대칭 평면을 반투명 사각형으로 그린다. 설정은 <c>Settings.Symmetry</c>(0 Off, 1~3 Object X/Y/Z, 4~6 World X/Y/Z)에 저장.
/// </summary>
public partial class Shell
{
    /// <summary>상태 라인 Symmetry 드롭다운.</summary>
    private OptionButton _symmetryButton = null!;
    private static readonly string[] SymmetryNames = { "Symmetry: Off", "Object X", "Object Y", "Object Z", "World X", "World Y", "World Z" };
    private static readonly string[] SymmetryActionIds = { "symmetry.off", "symmetry.objectX", "symmetry.objectY", "symmetry.objectZ", "symmetry.worldX", "symmetry.worldY", "symmetry.worldZ" };

    /// <summary>Symmetry가 켜져 있는지.</summary>
    public bool SymmetryOn => Settings.Symmetry > 0;

    /// <summary>노드(메시) 기준 대칭 평면(오브젝트 공간). 꺼져 있거나 메시가 없으면 null.</summary>
    public SymmetryPlane? SymmetryPlaneFor(SceneNode? node)
    {
        int mode = Settings.Symmetry;
        if (mode <= 0 || node?.Mesh == null) return null;
        float tol = MathF.Max(Settings.SymmetryTolerance, 1e-6f);
        return mode <= 3 ? SymmetryPlane.Object(mode - 1, tol) : SymmetryPlane.World(mode - 4, node.WorldMatrix, tol);
    }

    /// <summary>노드 ID로 대칭 평면.</summary>
    public SymmetryPlane? SymmetryPlaneFor(NodeId id) => SymmetryPlaneFor(Document.Find(id));

    /// <summary>
    /// 선택 변경에 대칭을 적용한다(RecordSelection 안): 변경 전후를 비교해 새로 더해진 컴포넌트의 짝을 더하고, 빠진 컴포넌트의 짝을 뺀다.
    /// 이렇게 하면 클릭·마키·Lasso·Grow·Convert뿐 아니라 Ctrl 제거·Shift 토글도 양쪽에 같이 적용된다.
    /// </summary>
    private void ApplySymmetryToSelection(SelectionState s, Dictionary<(NodeId, SelectMode), HashSet<int>> before)
    {
        if (!SymmetryOn || !s.IsComponentMode || s.Mode == SelectMode.Uv) return;
        var mode = s.Mode;
        int missing = 0;
        foreach (var id in s.NodesWithComponents(mode).Concat(before.Keys.Where(k => k.Item2 == mode).Select(k => k.Item1)).Distinct().ToArray())
        {
            var mesh = Document.Find(id)?.Mesh; if (mesh == null) continue;
            var plane = SymmetryPlaneFor(id); if (plane == null) continue;
            var map = SymmetryMap.Get(mesh, plane);
            var set = s.GetComponents(id).Get(mode);
            var old = before.TryGetValue((id, mode), out var o) ? o : new HashSet<int>();
            var added = set.Where(c => !old.Contains(c)).ToList();
            var removed = old.Where(c => !set.Contains(c)).ToList();
            foreach (int c in added) { int mc = map.MirrorComponent(mesh, mode, c); if (mc >= 0) set.Add(mc); else missing++; }
            foreach (int c in removed) { int mc = map.MirrorComponent(mesh, mode, c); if (mc >= 0 && !added.Contains(mc)) set.Remove(mc); }
            if (set.Count > 0) s.SelectComponents(id, mode, Array.Empty<int>(), replace: false); // Changed 통지(집합은 제자리 수정됨)
        }
        if (missing > 0) HelpLine.Text = $"Symmetry: {missing} component(s) have no mirror counterpart (mesh is not symmetric there).";
    }

    /// <summary>현재 컴포넌트 선택의 노드별 집합 복사본(대칭 전후 비교용).</summary>
    private Dictionary<(NodeId, SelectMode), HashSet<int>> SnapshotComponents(SelectionState s)
    {
        var d = new Dictionary<(NodeId, SelectMode), HashSet<int>>();
        if (!s.IsComponentMode) return d;
        foreach (var id in s.NodesWithComponents(s.Mode)) d[(id, s.Mode)] = new HashSet<int>(s.GetComponents(id).Get(s.Mode));
        return d;
    }

    /// <summary>Symmetry 설정을 바꾸고 상태 라인·셸프·뷰포트를 갱신한다. 켤 때는 현재 선택도 짝으로 확장한다.</summary>
    private void SetSymmetry(int mode)
    {
        if (mode > 0) Settings.SymmetryLast = mode;
        Settings.Symmetry = mode; Settings.Save();
        SyncStatusLine(); RefreshShelf();
        if (mode > 0 && Document.Selection.IsComponentMode && Document.Selection.Mode != SelectMode.Uv)
            RecordSelection(s => { }); // 빈 변경 → 대칭 적용 훅이 현재 선택 전체를 짝으로 확장
        HelpLine.Text = mode > 0 ? $"Symmetry {SymmetryNames[mode]}: selections and edits mirror across the plane." : "Symmetry off.";
        foreach (var p in Layout.Panels) p.Overlay.QueueRedraw();
    }

    /// <summary>symmetry.* 액션: 모드 7개 + toggle(Off ↔ 마지막 축).</summary>
    private void RegisterSymmetryActions()
    {
        for (int i = 0; i < SymmetryActionIds.Length; i++)
        {
            int mode = i;
            Actions.Register(SymmetryActionIds[i], i == 0 ? "Symmetry Off" : "Symmetry " + SymmetryNames[i], () => SetSymmetry(mode), isChecked: () => Settings.Symmetry == mode);
        }
        Actions.Register("symmetry.toggle", "Symmetry", () => SetSymmetry(SymmetryOn ? 0 : Math.Clamp(Settings.SymmetryLast, 1, 6)), isChecked: () => SymmetryOn);
    }

    /// <summary>상태 라인 드롭다운(모드 버튼 뒤).</summary>
    private void BuildSymmetryStatus(float s)
    {
        _symmetryButton = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Symmetry (Maya Tool Settings): mirror selections and edits across an axis plane" };
        foreach (var n in SymmetryNames) _symmetryButton.AddItem(n);
        _symmetryButton.Selected = Math.Clamp(Settings.Symmetry, 0, 6);
        _symmetryButton.ItemSelected += i => Actions.Invoke(SymmetryActionIds[(int)i]);
        StatusLine.AddChild(new VSeparator());
        StatusLine.AddChild(_symmetryButton);
    }

    /// <summary>상태 라인 드롭다운을 설정과 맞춘다(색: 켜져 있으면 강조).</summary>
    private void SyncSymmetryStatus()
    {
        if (_symmetryButton == null) return;
        int m = Math.Clamp(Settings.Symmetry, 0, 6);
        if (_symmetryButton.Selected != m) _symmetryButton.Selected = m;
        if (m > 0) _symmetryButton.AddThemeColorOverride("font_color", MayaTheme.Accent); else _symmetryButton.RemoveThemeColorOverride("font_color");
    }

    /// <summary>
    /// 뷰포트에 그릴 대칭 평면 사각형(화면 px): 활성 메시(컴포넌트 대상 → 활성 오브젝트)의 AABB를 평면 기저로 덮는 사각형을 투영한다. 꺼져 있으면 null.
    /// </summary>
    private (List<Vector2> quad, Color color)? SymmetryQuad(Viewport.ViewportPanel panel)
    {
        if (!SymmetryOn) return null;
        var sel = Document.Selection;
        var node = Document.Find(sel.IsComponentMode ? sel.ComponentTarget : sel.ActiveObject);
        if (node?.Mesh == null) return null;
        var plane = SymmetryPlaneFor(node); if (plane == null) return null;
        var (min, max) = Core.Mesh.MeshOps.Bounds(node.Mesh);
        var c = plane.Project((min + max) * 0.5f);
        float r = MathF.Max((max - min).Length() * 0.6f, 0.05f);
        // 평면 기저 u, v
        var n = plane.Normal;
        var u = NVec3.Cross(n, MathF.Abs(n.Y) < 0.9f ? NVec3.UnitY : NVec3.UnitX); u = NVec3.Normalize(u);
        var v = NVec3.Cross(n, u);
        var proj = panel.Picker.Projection();
        var corners = new[] { c + u * r + v * r, c - u * r + v * r, c - u * r - v * r, c + u * r - v * r };
        var pts = new List<Vector2>();
        foreach (var p in corners)
        {
            var sp = proj.Project(NVec3.Transform(p, node.WorldMatrix), out float depth);
            if (sp == null || depth <= 0) return null;
            pts.Add(new Vector2(sp.Value.X, sp.Value.Y));
        }
        int axis = (Settings.Symmetry - 1) % 3;
        var color = axis == 0 ? new Color(1f, 0.25f, 0.25f) : axis == 1 ? new Color(0.35f, 1f, 0.25f) : new Color(0.3f, 0.5f, 1f);
        return (pts, color);
    }
}
