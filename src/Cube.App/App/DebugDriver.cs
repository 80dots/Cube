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
            if (parts[0] is "move" or "press" or "release" or "drag" or "wheel" or "key") _wait = Math.Max(_wait, 1);
        }
        if (_steps.Count == 0 && _wait == 0) { GD.Print("[Drive] done"); QueueFree(); }
    }

    private Vector2 ToGlobal(Vector2 local) => Viewport != null ? Viewport.GlobalPosition + local : local;

    private void Exec(string[] p)
    {
        switch (p[0])
        {
            case "wait": _wait = int.Parse(p[1]); break;
            case "move":
                _pos = new Vector2(float.Parse(p[1]), float.Parse(p[2]));
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
                    var target = new Vector2(float.Parse(p[1]), float.Parse(p[2]));
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
            case "print":
                {
                    var doc = CubeApp.Instance.Document;
                    GD.Print($"[Drive] nodes={doc.Nodes.Count} sel={doc.Selection.Mode} objs={doc.Selection.Objects.Count} undo={doc.Undo.UndoCount} tool={UI.Shell.Instance.Tools.Current?.Id} shading={UI.Shell.Instance.Viewport.Display.Mode}");
                    var active = doc.Find(doc.Selection.ActiveObject) ?? doc.MeshNodes().FirstOrDefault();
                    if (active?.Mesh != null)
                    {
                        float ymax = float.MinValue, ymin = float.MaxValue;
                        foreach (var v in active.Mesh.Verts) if (v.Alive) { ymax = MathF.Max(ymax, v.Position.Y); ymin = MathF.Min(ymin, v.Position.Y); }
                        GD.Print($"[Drive] active={active.Name} local={active.Local} meshY=[{ymin:F3},{ymax:F3}] comps={string.Join("|", doc.Selection.Components.Select(kv => $"{kv.Key}:v{kv.Value.Verts.Count}/e{kv.Value.Edges.Count}/f{kv.Value.Faces.Count}"))}");
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
