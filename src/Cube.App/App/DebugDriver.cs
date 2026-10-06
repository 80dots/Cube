using Godot;

namespace Cube.App;

/// <summary>
/// 개발용 자동 조작. 커맨드라인 <c>--drive=&lt;script&gt;</c>로 입력 이벤트를 주입해 조작을 재현하고 스크린샷으로 검증한다.
/// 스크립트 문법(세미콜론 구분):
///   wait N            N프레임 대기
///   move X Y          뷰포트 로컬 좌표로 마우스 이동(픽셀)
///   press L|M|R [alt|shift|ctrl ...]    버튼 누름(현재 위치)
///   release L|M|R
///   drag X Y [alt...]  현재 위치에서 (X,Y)까지 8단계로 모션 이벤트
///   wheel N           휠 N틱(음수 = 아래)
///   key NAME [ctrl|shift|alt]   키 입력(누름+뗌), NAME은 Godot Key 이름(F, A, W, Home, F9 ...)
///   shot PATH         스크린샷 저장
/// 예: --drive="wait 5; move 800 450; press L alt; drag 950 400 alt; release L; shot C:/tmp/a.png"
/// </summary>
public partial class DebugDriver : Node
{
    private readonly Queue<string> _steps = new();
    private int _wait;
    private Vector2 _pos = new(800, 450);
    private readonly HashSet<MouseButton> _held = new();

    public DebugDriver(string script)
    {
        foreach (var s in script.Split(';')) { var t = s.Trim(); if (t.Length > 0) _steps.Enqueue(t); }
    }

    private Control? Viewport => UI.Shell.Instance?.Viewport;

    public override void _Process(double delta)
    {
        if (_wait > 0) { _wait--; return; }
        while (_steps.Count > 0 && _wait == 0)
        {
            var step = _steps.Dequeue();
            var parts = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try { Exec(parts); }
            catch (Exception ex) { GD.PrintErr($"[Drive] '{step}': {ex.Message}"); }
            // 주입된 입력 이벤트는 다음 입력 플러시에서 처리되므로 입력 스텝 뒤에는 한 프레임 양보한다
            if (parts[0] is "move" or "press" or "release" or "drag" or "wheel" or "key" or "axisdrag" or "ringdrag" or "centerdrag" or "keydown" or "keyup") _wait = Math.Max(_wait, 1);
        }
        if (_steps.Count == 0 && _wait == 0) { GD.Print("[Drive] done"); QueueFree(); }
    }

    private Vector2 ToGlobal(Vector2 local) => Viewport != null ? Viewport.GlobalPosition + local : local;

    /// <summary>"c+10" / "c-50" 처럼 뷰포트 중심 기준 좌표도 허용한다.</summary>
    private Vector2 ParseXY(string xs, string ys)
    {
        var size = Viewport?.Size ?? new Vector2(1000, 700);
        float Parse(string t, float center) => t.StartsWith("c") ? center + (t.Length > 1 ? float.Parse(t[1..]) : 0) : float.Parse(t);
        return new Vector2(Parse(xs, size.X / 2), Parse(ys, size.Y / 2));
    }

    private void Exec(string[] p)
    {
        switch (p[0])
        {
            case "wait": _wait = int.Parse(p[1]); break;
            case "move":
                _pos = ParseXY(p[1], p[2]);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), Relative = Vector2.Zero, ButtonMask = Mask() });
                break;
            case "press":
                {
                    var b = Button(p[1]);
                    _held.Add(b);
                    var ev = new InputEventMouseButton { ButtonIndex = b, Pressed = true, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() };
                    Mods(ev, p, 2);
                    Input.ParseInputEvent(ev);
                    break;
                }
            case "release":
                {
                    var b = Button(p[1]);
                    _held.Remove(b);
                    var ev = new InputEventMouseButton { ButtonIndex = b, Pressed = false, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() };
                    Mods(ev, p, 2);
                    Input.ParseInputEvent(ev);
                    break;
                }
            case "drag":
                {
                    var target = ParseXY(p[1], p[2]);
                    const int steps = 8;
                    for (int i = 1; i <= steps; i++)
                    {
                        var next = _pos.Lerp(target, (float)i / steps);
                        var ev = new InputEventMouseMotion { Position = ToGlobal(next), GlobalPosition = ToGlobal(next), Relative = next - _pos, ButtonMask = Mask() };
                        Mods(ev, p, 3);
                        Input.ParseInputEvent(ev);
                        _pos = next;
                    }
                    break;
                }
            case "wheel":
                {
                    int n = int.Parse(p[1]);
                    var b = n > 0 ? MouseButton.WheelUp : MouseButton.WheelDown;
                    for (int i = 0; i < Math.Abs(n); i++)
                    {
                        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = b, Pressed = true, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos) });
                        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = b, Pressed = false, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos) });
                    }
                    break;
                }
            case "key":
                {
                    var key = Enum.Parse<Key>(p[1], ignoreCase: true);
                    var down = new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true };
                    Mods(down, p, 2);
                    var up = new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false };
                    Mods(up, p, 2);
                    Input.ParseInputEvent(down);
                    Input.ParseInputEvent(up);
                    break;
                }
            case "shot":
                {
                    var img = GetViewport().GetTexture().GetImage();
                    GD.Print($"[Drive] shot {p[1]}: {img.SavePng(p[1])}");
                    break;
                }
            case "action":
                GD.Print($"[Drive] action {p[1]}: {UI.Shell.Instance.Actions.Invoke(p[1])}");
                break;
            case "export":
                GD.Print($"[Drive] export: {UI.Shell.Instance.Files.Export(p[1], selectionOnly: p.Length > 2 && p[2] == "selection")}");
                break;
            case "import":
                GD.Print($"[Drive] import: {UI.Shell.Instance.Files.Import(p[1])}");
                break;
            case "save":
                GD.Print($"[Drive] save: {UI.Shell.Instance.SceneFiles.Save(p[1])} title='{UI.Shell.Instance.SceneFiles.Title}'");
                break;
            case "open":
                GD.Print($"[Drive] open: {UI.Shell.Instance.SceneFiles.Open(p[1])} title='{UI.Shell.Instance.SceneFiles.Title}'");
                break;
            case "axisdrag":   // axisdrag X|Y|Z dist [mods]: 기즈모 축 위(피벗에서 55px)에서 누르고 축 방향으로 dist px 드래그
            case "ringdrag":   // ringdrag X|Y|Z|S dist: 회전 링 위에서 누르고 접선 방향으로 dist px 드래그 (S = 화면 링)
            case "centerdrag": // centerdrag dx dy: 중앙 핸들(피벗 옆 6px)에서 누르고 (dx,dy)만큼 드래그
                {
                    if (UI.Shell.Instance.Tools.Current is not Tools.TransformToolBase t || t.GizmoPublic == null || !t.GizmoPublic.Visible) { GD.PrintErr("[Drive] no visible gizmo"); break; }
                    var g = t.GizmoPublic;
                    var proj = UI.Shell.Instance.Viewport.Picker.Projection();
                    var c0 = proj.Project(g.Pivot, out _) ?? new System.Numerics.Vector2(0, 0);
                    Vector2 pressAt, dragTo;
                    if (p[0] == "centerdrag")
                    {
                        pressAt = new Vector2(c0.X + 6, c0.Y + 6);
                        dragTo = pressAt + new Vector2(float.Parse(p[1]), float.Parse(p[2]));
                    }
                    else if (p[0] == "axisdrag")
                    {
                        var axis = p[1].ToUpperInvariant() switch { "X" => g.AxisX, "Y" => g.AxisY, _ => g.AxisZ };
                        var c1 = proj.Project(g.Pivot + axis * g.WorldUnit, out _) ?? c0;
                        var dir = new Vector2(c1.X - c0.X, c1.Y - c0.Y);
                        dir = dir.Length() > 1e-3f ? dir.Normalized() : new Vector2(1, 0);
                        float dist = float.Parse(p[2]);
                        pressAt = new Vector2(c0.X, c0.Y) + dir * 55f * CubeApp.Instance.UiScale;
                        dragTo = pressAt + dir * dist;
                    }
                    else
                    {
                        float r = Cube.App.Viewport.Gizmos.GizmoBase.ScreenSizePx * CubeApp.Instance.UiScale;
                        float dist = float.Parse(p[2]);
                        if (p[1].ToUpperInvariant() == "S")
                        {
                            r *= Cube.App.Viewport.Gizmos.RotateGizmo.OuterRadius;
                            pressAt = new Vector2(c0.X + r, c0.Y);
                            dragTo = new Vector2(c0.X + r, c0.Y - dist);
                        }
                        else
                        {
                            var axis = p[1].ToUpperInvariant() switch { "X" => g.AxisX, "Y" => g.AxisY, _ => g.AxisZ };
                            // 링 위에서 화면상 중심에서 가장 먼 점을 고른다
                            var helper = MathF.Abs(axis.Y) < 0.9f ? System.Numerics.Vector3.UnitY : System.Numerics.Vector3.UnitX;
                            var u = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(helper, axis)); var v = System.Numerics.Vector3.Cross(axis, u);
                            Vector2 best = new(c0.X + r, c0.Y); float bestD = -1;
                            for (int i = 0; i < 32; i++)
                            {
                                float a = MathF.Tau * i / 32;
                                var w = g.Pivot + (u * MathF.Cos(a) + v * MathF.Sin(a)) * g.WorldUnit;
                                var sp = proj.Project(w, out _); if (sp == null) continue;
                                float d = new Vector2(sp.Value.X, sp.Value.Y).DistanceTo(new Vector2(c0.X, c0.Y));
                                if (d > bestD) { bestD = d; best = new Vector2(sp.Value.X, sp.Value.Y); }
                            }
                            pressAt = best;
                            var radial = (best - new Vector2(c0.X, c0.Y)).Normalized();
                            var tangent = new Vector2(-radial.Y, radial.X);
                            dragTo = best + tangent * dist;
                        }
                    }
                    GD.Print($"[Drive] {p[0]} press={pressAt} to={dragTo}");
                    _pos = pressAt;
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() });
                    _held.Add(MouseButton.Left);
                    var down = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() };
                    Mods(down, p, 3); Input.ParseInputEvent(down);
                    for (int i = 1; i <= 8; i++)
                    {
                        var next = _pos.Lerp(dragTo, (float)i / 8);
                        var ev = new InputEventMouseMotion { Position = ToGlobal(next), GlobalPosition = ToGlobal(next), Relative = next - _pos, ButtonMask = Mask() };
                        Mods(ev, p, 3); Input.ParseInputEvent(ev);
                        _pos = next;
                    }
                    _held.Remove(MouseButton.Left);
                    Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() });
                    break;
                }
            case "keydown":   // keydown NAME [mods]: 누르기만(홀드 테스트)
            case "keyup":
                {
                    var key = Enum.Parse<Key>(p[1], ignoreCase: true);
                    var ev = new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = p[0] == "keydown" };
                    Mods(ev, p, 2);
                    Input.ParseInputEvent(ev);
                    break;
                }
            case "hit":   // hit X Y: 현재 변형 툴 기즈모의 히트 테스트 결과
                {
                    var px = ParseXY(p[1], p[2]);
                    if (UI.Shell.Instance.Tools.Current is Tools.TransformToolBase t && t.GizmoPublic != null)
                        GD.Print($"[Drive] hit {px} -> {t.GizmoPublic.HitTest(new System.Numerics.Vector2(px.X, px.Y), UI.Shell.Instance.Viewport.Picker.Projection())} visible={t.GizmoPublic.Visible} unit={t.GizmoPublic.WorldUnit}");
                    break;
                }
            case "panel":
                {
                    var layout = UI.Shell.Instance.Layout;
                    int idx = int.Parse(p[1]);
                    layout.SetActive(layout.Panels[idx]);
                    GD.Print($"[Drive] panel {idx} ({layout.Panels[idx].CameraController.Label}) size={layout.Panels[idx].Size} quad={layout.IsQuad}");
                    break;
                }
            case "gizmo":
                {
                    if (UI.Shell.Instance.Tools.Current is Tools.TransformToolBase t && t.GizmoPublic != null)
                        GD.Print($"[Drive] gizmo visible={t.GizmoPublic.Visible} pivot={t.GizmoPublic.Pivot} x={t.GizmoPublic.AxisX} y={t.GizmoPublic.AxisY} z={t.GizmoPublic.AxisZ} axis={UI.Shell.Instance.ToolContext.AxisOrientation} view={UI.Shell.Instance.Viewport.CameraController.Label}");
                    else GD.Print("[Drive] gizmo: (no transform tool)");
                    break;
                }
            case "print":
                {
                    var doc = CubeApp.Instance.Document;
                    GD.Print($"[Drive] nodes={doc.Nodes.Count} sel={doc.Selection.Mode} objs={doc.Selection.Objects.Count} undo={doc.Undo.UndoCount} tool={UI.Shell.Instance.Tools.Current?.Id} shading={UI.Shell.Instance.Viewport.Display.Mode} view={UI.Shell.Instance.Viewport.CameraController.Label} quad={UI.Shell.Instance.Layout.IsQuad} pie={UI.Shell.Instance.Viewport.Pie.IsOpen}");
                    var active = doc.Find(doc.Selection.ActiveObject) ?? doc.MeshNodes().FirstOrDefault();
                    if (active?.Mesh != null)
                    {
                        var mn = new System.Numerics.Vector3(float.MaxValue); var mx = new System.Numerics.Vector3(float.MinValue);
                        foreach (var v in active.Mesh.Verts) if (v.Alive) { mn = System.Numerics.Vector3.Min(mn, v.Position); mx = System.Numerics.Vector3.Max(mx, v.Position); }
                        GD.Print($"[Drive] active={active.Name} local={active.Local} meshMin=<{mn.X:F3},{mn.Y:F3},{mn.Z:F3}> meshMax=<{mx.X:F3},{mx.Y:F3},{mx.Z:F3}> comps={string.Join("|", doc.Selection.Components.Select(kv => $"{kv.Key}:v{kv.Value.Verts.Count}/e{kv.Value.Edges.Count}/f{kv.Value.Faces.Count}"))}");
                    }
                    break;
                }
            default: GD.PrintErr($"[Drive] unknown step {p[0]}"); break;
        }
    }

    private MouseButtonMask Mask()
    {
        MouseButtonMask m = 0;
        foreach (var b in _held) m |= b switch { MouseButton.Left => MouseButtonMask.Left, MouseButton.Middle => MouseButtonMask.Middle, MouseButton.Right => MouseButtonMask.Right, _ => 0 };
        return m;
    }

    private static MouseButton Button(string s) => s.ToUpperInvariant() switch { "L" => MouseButton.Left, "M" => MouseButton.Middle, "R" => MouseButton.Right, _ => throw new ArgumentException(s) };

    private static void Mods(InputEventWithModifiers ev, string[] p, int from)
    {
        for (int i = from; i < p.Length; i++)
            switch (p[i].ToLowerInvariant())
            {
                case "alt": ev.AltPressed = true; break;
                case "shift": ev.ShiftPressed = true; break;
                case "ctrl": ev.CtrlPressed = true; break;
            }
    }
}
