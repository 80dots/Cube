using Godot;

namespace Cube.App.Hotkeys;

/// <summary>
/// 전역 키 라우터. _Input에서 키를 받아 컨텍스트(텍스트 필드 포커스, 뷰포트 호버/포커스)를 판정하고 액션을 호출한다.
/// 홀드 키(J/X/V 스냅 등)는 <see cref="HeldKeys"/>로 추적한다.
/// </summary>
public partial class ShellInput : Node
{
    public HotkeyMap Map { get; private set; } = new();
    public ActionRegistry Actions { get; set; } = null!;
    public Func<bool>? IsViewportContext;
    public readonly HashSet<Key> HeldKeys = new();

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Map = HotkeyMap.Load();
    }

    /// <summary>Space 누름/뗌(텍스트 필드 포커스가 아닐 때). Shell이 탭=레이아웃 토글, 홀드=뷰 파이로 해석한다.</summary>
    public event Action? SpaceDown;
    public event Action? SpaceUp;
    private bool _spaceHeld;

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey k) return;
        var focus = GetViewport().GuiGetFocusOwner();
        bool textFocused = focus is LineEdit or TextEdit || (focus != null && focus.GetParent() is SpinBox);
        if (!k.Pressed)
        {
            HeldKeys.Remove(k.Keycode); HeldKeys.Remove(k.PhysicalKeycode);
            if (k.Keycode == Key.Space && _spaceHeld) { _spaceHeld = false; SpaceUp?.Invoke(); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (k.Echo) return;
        HeldKeys.Add(k.Keycode);
        if (textFocused)
        {
            if (k.Keycode == Key.Escape) { focus!.ReleaseFocus(); Actions.Invoke("app.escape"); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (k.Keycode == Key.Space && !k.CtrlPressed && !k.AltPressed && !k.ShiftPressed)
        {
            _spaceHeld = true; SpaceDown?.Invoke(); GetViewport().SetInputAsHandled();
            return;
        }

        var chord = KeyChord.FromEvent(k);
        bool vp = IsViewportContext?.Invoke() ?? false;
        if (Map.TryResolve(chord, vp, out var action))
        {
            bool ok = Actions.Invoke(action);
            if (Verbose) GD.Print($"[Hotkey] {chord} vp={vp} -> {action} ok={ok}");
            if (ok) GetViewport().SetInputAsHandled();
        }
        else if (Verbose) GD.Print($"[Hotkey] {chord} vp={vp} -> (none)");
    }

    public static bool Verbose;
}
