using Cube.Core.Scene;
using Cube.Core.Selection;
using Cube.Core.Uv;
using Cube.Core.Commands;

namespace Cube.App.UI;

/// <summary>
/// UV 편집기 Symmetry(v0.0.65, Maya UV Toolkit → Transform → Symmetry: Off / U / V + 축 위치). 켜져 있으면
/// ① UV 편집기에서의 선택(클릭·마키)이 UV 공간 거울 짝까지 확장되고(제거하면 짝도 제거, <see cref="ApplyUvSymmetryToSelection"/>)
/// ② 캔버스의 W/E/R 조작기·Tweak·Move Shell 드래그가 반대쪽 점에 거울 변형을, 축선 위 점에 축선 투영을 적용하며(<see cref="UvSymmetryOps.Transform"/>)
/// ③ 브러시(Grab/Smooth/Pinch/Smear/Pin)와 Cut/Sew 칠하기는 거울 위치에도 같은 스탬프를 찍는다.
/// 3D Symmetry(<c>Settings.Symmetry</c>)와는 독립이며 UV 좌표만 본다. 설정은 <c>Settings.UvSymmetry</c>(0 Off, 1 U, 2 V)·<c>UvSymmetryCenter</c>.
/// </summary>
public partial class Shell
{
    private static readonly string[] UvSymmetryNames = { "UV Symmetry: Off", "Symmetry U", "Symmetry V" };
    private static readonly string[] UvSymmetryActionIds = { "uv.symmetryOff", "uv.symmetryU", "uv.symmetryV" };

    /// <summary>UV Symmetry가 켜져 있는지.</summary>
    public bool UvSymmetryOn => Settings.UvSymmetry > 0;

    /// <summary>UV Symmetry 설정이 바뀌었을 때(툴바 동기화용).</summary>
    public event Action? UvSymmetryChanged;

    /// <summary>현재 UV 대칭 축선(꺼져 있으면 null).</summary>
    public UvSymmetryPlane? UvSymmetryPlane => Settings.UvSymmetry is 1 or 2
        ? new UvSymmetryPlane(Settings.UvSymmetry - 1, Settings.UvSymmetryCenter, MathF.Max(Settings.UvSymmetryTolerance, 1e-6f)) : null;

    /// <summary>
    /// UV 편집기에서 온 선택 변경을 기록한다: 3D Symmetry 훅 대신 UV Symmetry 훅(더해진 컴포넌트의 UV 짝을 더하고 빠진 것의 짝을 뺌)을 적용한다.
    /// </summary>
    public void RecordUvSelection(Action<SelectionState> change)
    {
        var cmd = SelectionCommand.Record(Document, s => { var before = SnapshotComponents(s); change(s); ApplyUvSymmetryToSelection(s, before); });
        if (!cmd.IsNoop) Document.Undo.Push(cmd, alreadyApplied: true);
    }

    /// <summary>선택 변경 전후를 비교해 UV 거울 짝을 더하고/뺀다(Vertex/Edge/Face/UV 모드 공통, 캔버스의 UV 위상 기준).</summary>
    private void ApplyUvSymmetryToSelection(SelectionState s, Dictionary<(NodeId, SelectMode), HashSet<int>> before)
    {
        var plane = UvSymmetryPlane;
        var canvas = UvEditorWindow?.Canvas;
        if (plane == null || canvas == null || !s.IsComponentMode) return;
        var mode = s.Mode;
        int missing = 0;
        foreach (var id in s.NodesWithComponents(mode).Concat(before.Keys.Where(k => k.Item2 == mode).Select(k => k.Item1)).Distinct().ToArray())
        {
            var node = Document.Find(id); var mesh = node?.Mesh; if (node == null || mesh == null) continue;
            var map = UvSymmetryMap.Build(canvas.Topo(node), plane);
            var set = s.GetComponents(id).Get(mode);
            var old = before.TryGetValue((id, mode), out var o) ? o : new HashSet<int>();
            var added = set.Where(c => !old.Contains(c)).ToList();
            var removed = old.Where(c => !set.Contains(c)).ToList();
            foreach (int c in added) { int mc = map.MirrorComponent(mesh, mode, c); if (mc >= 0) set.Add(mc); else missing++; }
            foreach (int c in removed) { int mc = map.MirrorComponent(mesh, mode, c); if (mc >= 0 && !added.Contains(mc)) set.Remove(mc); }
            if (set.Count > 0) s.SelectComponents(id, mode, Array.Empty<int>(), replace: false); // Changed 통지(집합은 제자리 수정됨)
        }
        if (missing > 0) HelpLine.Text = $"UV Symmetry: {missing} component(s) have no mirror counterpart across {(plane.Axis == 0 ? "U" : "V")} = {plane.Center:0.###}.";
    }

    /// <summary>UV Symmetry 모드를 바꾼다. 켤 때는 현재 선택도 짝으로 확장한다.</summary>
    private void SetUvSymmetry(int mode)
    {
        mode = Math.Clamp(mode, 0, 2);
        if (mode > 0) Settings.UvSymmetryLast = mode;
        Settings.UvSymmetry = mode; Settings.Save();
        if (mode > 0 && Document.Selection.IsComponentMode && UvEditorWindow != null) RecordUvSelection(s => { });
        HelpLine.Text = mode > 0 ? $"UV {UvSymmetryNames[mode]} at {(mode == 1 ? "u" : "v")} = {Settings.UvSymmetryCenter:0.###}: UV selections and edits mirror across the line." : "UV Symmetry off.";
        UvSymmetryChanged?.Invoke();
        UvEditorWindow?.Canvas.QueueRedraw();
    }

    /// <summary>축선 위치를 바꾼다(툴바 스핀박스·Set Center to Selection).</summary>
    public void SetUvSymmetryCenter(float center)
    {
        if (MathF.Abs(Settings.UvSymmetryCenter - center) < 1e-7f) return;
        Settings.UvSymmetryCenter = center; Settings.Save();
        UvSymmetryChanged?.Invoke();
        UvEditorWindow?.Canvas.QueueRedraw();
    }

    /// <summary>uv.symmetry* 액션: Off/U/V, toggle(Off ↔ 마지막 축), centerSelection(축선을 선택 UV 중심으로).</summary>
    private void RegisterUvSymmetryActions()
    {
        for (int i = 0; i < UvSymmetryActionIds.Length; i++)
        {
            int mode = i;
            Actions.Register(UvSymmetryActionIds[i], i == 0 ? "UV Symmetry Off" : "UV " + UvSymmetryNames[i], () => SetUvSymmetry(mode), isChecked: () => Settings.UvSymmetry == mode);
        }
        Actions.Register("uv.symmetryToggle", "UV Symmetry", () => SetUvSymmetry(UvSymmetryOn ? 0 : Math.Clamp(Settings.UvSymmetryLast, 1, 2)), isChecked: () => UvSymmetryOn);
        Actions.Register("uv.symmetryCenterSelection", "Set Symmetry Center to Selection", () =>
        {
            var canvas = UvEditorWindow?.Canvas; if (canvas == null) return;
            var sum = System.Numerics.Vector2.Zero; int n = 0;
            foreach (var node in canvas.TargetNodes()) { var topo = canvas.Topo(node); foreach (int p in canvas.SelectedPoints(node)) { sum += topo.Points[p].Uv; n++; } }
            if (n == 0) { HelpLine.Text = "Set Symmetry Center: select UVs first."; return; }
            var c = sum / n;
            SetUvSymmetryCenter(Settings.UvSymmetry == 2 ? c.Y : c.X);
            HelpLine.Text = $"UV Symmetry center = {Settings.UvSymmetryCenter:0.####}.";
        }, canExecute: () => UvEditorWindow != null);
    }
}
