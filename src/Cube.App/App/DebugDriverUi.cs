using Godot;

namespace Cube.App;

/// <summary>
/// UI 점검용 드라이브 스텝(메뉴/셸프/파이/핫키 액션 연결 감사, 렌더 설정 값 지정, 패널 상태 출력).
/// <see cref="DebugDriver.Exec"/>가 모르는 스텝을 이곳에 넘긴다.
/// </summary>
public partial class DebugDriver
{
    /// <summary>
    /// UI 스텝을 처리한다. 처리했으면 true.
    /// <c>menuaudit</c>: 모든 메인 메뉴·셸프·파이 항목의 액션 ID를 검사(미등록·같은 메뉴 안 중복·현재 비활성)한다.
    /// <c>rset KEY VALUE</c>: <see cref="RenderSettings"/> 속성을 리플렉션으로 바꾸고 렌더 설정을 적용한다.
    /// <c>rprint</c>: 렌더 설정 전체를 찍는다. <c>panels</c>: 등록된 플로팅 패널의 열림·도킹·위치·크기를 찍는다.
    /// </summary>
    private bool ExecUi(string[] p)
    {
        var shell = UI.Shell.Instance;
        switch (p[0])
        {
            case "menuaudit":
                {
                    var seen = new Dictionary<string, List<string>>();
                    int count = 0;
                    // 팝업 하나와 그 서브메뉴를 재귀로 돈다(경로 = 메뉴 제목/서브메뉴 제목)
                    void Walk(PopupMenu pm, string path)
                    {
                        for (int i = 0; i < pm.ItemCount; i++)
                        {
                            if (pm.IsItemSeparator(i)) continue;
                            var sub = pm.GetItemSubmenuNode(i);
                            if (sub != null) { Walk(sub, path + "/" + pm.GetItemText(i)); continue; }
                            var meta = pm.GetItemMetadata(i);
                            string id = meta.VariantType == Variant.Type.String ? meta.AsString() : "";
                            count++;
                            if (id.Length == 0) { if (path != "File/Open Recent") GD.Print($"[Audit] menu {path}: '{pm.GetItemText(i)}' has no action"); continue; }
                            var a = shell.Actions.Get(id);
                            if (a == null) GD.Print($"[Audit] menu {path}: '{pm.GetItemText(i)}' -> UNKNOWN action {id}");
                            else if (!a.Enabled) GD.Print($"[Audit] menu {path}: '{pm.GetItemText(i)}' ({id}) disabled now");
                            if (!seen.TryGetValue(id, out var l)) seen[id] = l = new List<string>();
                            l.Add(path);
                        }
                    }
                    for (int m = 0; m < shell.MenuBar.GetChildCount(); m++)
                        if (shell.MenuBar.GetChild(m) is PopupMenu pm) Walk(pm, shell.MenuBar.GetMenuTitle(m));
                    foreach (var (id, paths) in seen) if (paths.Count > 1) GD.Print($"[Audit] menu duplicate {id}: {string.Join(", ", paths)}");
                    GD.Print($"[Audit] menu items={count} actions={seen.Count}");
                    int sc = 0;
                    foreach (var (b, id) in shell.ShelfButtons)
                    {
                        sc++;
                        var a = shell.Actions.Get(id);
                        if (a == null) GD.Print($"[Audit] shelf '{b.Text}' -> UNKNOWN action {id}");
                        else if (b.Disabled != !a.Enabled) GD.Print($"[Audit] shelf '{b.Text}' ({id}) disabled={b.Disabled} but enabled={a.Enabled}");
                        if (a?.IsChecked != null && b.ButtonPressed != a.IsChecked()) GD.Print($"[Audit] shelf '{b.Text}' ({id}) pressed={b.ButtonPressed} but checked={a.IsChecked()}");
                    }
                    GD.Print($"[Audit] shelf buttons={sc}");
                    // 파이 메뉴(현재 모드 기준)
                    foreach (var (name, items) in new[] { ("mode", UI.PieMenus.ModeMenu(shell)), ("edit", UI.PieMenus.ContextMenu(shell)), ("select", UI.PieMenus.SelectMenu(shell)), ("view", UI.PieMenus.ViewMenu(shell)) })
                        foreach (var it in items)
                            if (it.Run == null && it.Sub == null && !string.IsNullOrEmpty(it.ActionId) && shell.Actions.Get(it.ActionId) == null)
                                GD.Print($"[Audit] pie {name}: '{it.Label}' -> UNKNOWN action {it.ActionId}");
                    // 핫키 맵
                    foreach (var id in shell.Hotkeys.Map.Bindings.Select(b => b.Action).Distinct())
                        if (shell.Actions.Get(id) == null) GD.Print($"[Audit] hotkey -> UNKNOWN action {id}");
                    return true;
                }
            case "rset":
                {
                    var r = CubeApp.Instance.Settings.Render;
                    var prop = r.GetType().GetProperty(p[1]) ?? throw new ArgumentException("no render setting " + p[1]);
                    object v = prop.PropertyType == typeof(bool) ? (p[2] is "1" or "true")
                        : prop.PropertyType == typeof(int) ? int.Parse(p[2])
                        : prop.PropertyType == typeof(float) ? float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)
                        : prop.PropertyType == typeof(double) ? double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)
                        : (object)string.Join(" ", p.Skip(2));
                    prop.SetValue(r, v);
                    shell.ApplyRenderSettings();
                    GD.Print($"[Drive] rset {p[1]}={prop.GetValue(r)}");
                    return true;
                }
            case "rprint":
                {
                    var r = CubeApp.Instance.Settings.Render;
                    GD.Print("[Drive] render " + string.Join(" ", r.GetType().GetProperties().Select(pp => $"{pp.Name}={pp.GetValue(r)}")));
                    return true;
                }
            case "panels":
                {
                    foreach (var fp in shell.Dock.RegisteredPanels)
                        GD.Print($"[Drive] panel {fp.PanelId}: open={fp.IsOpen} docked={fp.Docked} visible={fp.Visible} pos={fp.GlobalPosition} size={fp.Size}");
                    return true;
                }
            case "matprint":   // matprint ID: 머티리얼의 모든 값/텍스처를 찍는다
                {
                    var m = CubeApp.Instance.Document.FindMaterial(int.Parse(p[1]));
                    if (m == null) { GD.Print($"[Drive] matprint: no material {p[1]}"); return true; }
                    GD.Print($"[Drive] mat {m.Id} {m.Name} {m.Type} undo={CubeApp.Instance.Document.Undo.UndoCount} " + string.Join(" ", m.Values.Select(kv => $"{kv.Key}={kv.Value.X:0.###},{kv.Value.Y:0.###},{kv.Value.Z:0.###}")) + " tex=" + string.Join(",", m.Textures.Select(kv => kv.Key + ":" + System.IO.Path.GetFileName(kv.Value))));
                    return true;
                }
            case "imgdiff":   // imgdiff A B: 두 PNG의 활성 뷰포트 영역 평균 RGB 차이(0~255)와 다른 픽셀 비율
                {
                    var a = Image.LoadFromFile(p[1]); var b = Image.LoadFromFile(p[2]);
                    var r = shell.Viewport.GetGlobalRect();
                    int x0 = (int)r.Position.X, y0 = (int)r.Position.Y, x1 = Math.Min((int)r.End.X, Math.Min(a.GetWidth(), b.GetWidth())), y1 = Math.Min((int)r.End.Y, Math.Min(a.GetHeight(), b.GetHeight()));
                    double sum = 0; long n = 0, diff = 0;
                    for (int y = y0; y < y1; y += 2)
                        for (int x = x0; x < x1; x += 2)
                        {
                            var ca = a.GetPixel(x, y); var cb = b.GetPixel(x, y);
                            double d = (Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B)) / 3.0 * 255;
                            sum += d; n++; if (d > 4) diff++;
                        }
                    GD.Print($"[Drive] imgdiff {System.IO.Path.GetFileName(p[1])} {System.IO.Path.GetFileName(p[2])}: mean={sum / Math.Max(1, n):0.00} changed={100.0 * diff / Math.Max(1, n):0.0}%");
                    return true;
                }
            case "type":   // type TEXT: 글자마다 유니코드 키 이벤트(포커스된 텍스트 칸 입력용; '_'는 공백)
                {
                    foreach (char ch in string.Join(" ", p.Skip(1)).Replace('_', ' '))
                    {
                        Input.ParseInputEvent(new InputEventKey { Unicode = ch, Pressed = true });
                        Input.ParseInputEvent(new InputEventKey { Unicode = ch, Pressed = false });
                    }
                    _wait = Math.Max(_wait, 1);
                    return true;
                }
            case "find":   // find TEXT [N]: 보이는 컨트롤 중 텍스트(버튼·라벨) 또는 툴팁이 TEXT인 N번째의 전역 사각형을 찍고 커서를 그 가운데로 옮긴다('_'는 공백)
            case "gclick": // gclick TEXT [N]: find + 왼쪽 클릭
                {
                    string text = p[1].Replace('_', ' ');
                    int n = p.Length > 2 ? int.Parse(p[2]) : 0;
                    var hits = shell.GetTree().Root.FindChildren("*", "Control", true, false).OfType<Control>()
                        .Where(c => c.IsVisibleInTree() && ControlText(c) == text).ToList();
                    if (n >= hits.Count) { GD.Print($"[Drive] {p[0]} '{text}': {hits.Count} matches"); return true; }
                    var c = hits[n];
                    var g = c.GetGlobalRect().GetCenter();
                    _pos = g - (Viewport?.GlobalPosition ?? Vector2.Zero);
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = g, GlobalPosition = g, ButtonMask = Mask() });
                    if (p[0] == "gclick")
                    {
                        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = g, GlobalPosition = g, ButtonMask = MouseButtonMask.Left });
                        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = g, GlobalPosition = g });
                    }
                    GD.Print($"[Drive] {p[0]} '{text}' #{n}/{hits.Count} {c.GetType().Name} at {c.GetGlobalRect()}");
                    _wait = Math.Max(_wait, 1);
                    return true;
                }
        }
        return false;
    }

    /// <summary>점검 스텝이 컨트롤을 찾을 때 쓰는 텍스트(버튼/라벨 텍스트, 없으면 툴팁).</summary>
    private static string ControlText(Control c) => c switch
    {
        Button b when !string.IsNullOrEmpty(b.Text) => b.Text,
        Label l => l.Text,
        _ => c.TooltipText ?? "",
    };
}
