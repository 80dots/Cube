using Godot;

namespace Cube.App.Hotkeys;

/// <summary>
/// 전역 키 라우터. _Input에서 키를 받아 컨텍스트(텍스트 필드 포커스, 뷰포트 호버/포커스)를 판정하고 액션을 호출한다.
/// 홀드 키(J/X/V 스냅 등)는 <see cref="HeldKeys"/>로 추적한다.
/// </summary>
/// <remarks>
/// Godot InputMap은 쓰지 않는다. 바인딩은 <see cref="HotkeyMap"/>(config/hotkeys.default.json + user://hotkeys.json)이고
/// 결과는 항상 <see cref="ActionRegistry"/>의 ActionId 호출이다. 텍스트 입력 칸에 포커스가 있으면 Esc 외에는 무시한다.
/// </remarks>
public partial class ShellInput : Node
{
    /// <summary>현재 핫키 바인딩 표. <see cref="_Ready"/>에서 파일로부터 읽는다.</summary>
    public HotkeyMap Map { get; private set; } = new();
    /// <summary>키가 해석된 액션을 실행할 레지스트리(Shell이 설정).</summary>
    public ActionRegistry Actions { get; set; } = null!;
    /// <summary>뷰포트 컨텍스트 판정 콜백(마우스가 뷰포트 위이거나 포커스). true면 "viewport" 바인딩을 먼저 본다.</summary>
    public Func<bool>? IsViewportContext;
    /// <summary>
    /// 지금 눌려 있는 키 집합(Keycode와 PhysicalKeycode 둘 다 넣는다). 툴이 X/V/J 홀드 스냅 판정에 쓴다.
    /// 텍스트 필드 포커스 중에 눌린 키도 들어가며, 뗄 때 제거된다.
    /// </summary>
    public readonly HashSet<Key> HeldKeys = new();
    /// <summary>홀드 키 집합이 바뀜(상태 라인 스냅 버튼 표시 등).</summary>
    public event Action? HeldKeysChanged;

    /// <summary>노드 준비: 게임 일시정지와 무관하게 항상 입력을 받도록 하고 핫키 파일을 읽는다.</summary>
    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Map = HotkeyMap.Load();
    }

    // [참고: 아래 SpaceDown/SpaceUp 이벤트 설명] Space 누름/뗌(텍스트 필드 포커스가 아닐 때). Shell이 탭=레이아웃 토글, 홀드=뷰 파이로 해석한다.
    /// <summary>마지막으로 본 버튼 없는 마우스 이동의 위치(루트 뷰포트 좌표). <see cref="FixCursor"/>가 사용.</summary>
    private Vector2 _cursorPos;
    /// <summary>FixCursor 지연 호출이 이미 예약되었는지(프레임당 한 번만 예약).</summary>
    private bool _cursorFixQueued;

    /// <summary>
    /// 커서 복구: 크기 조절 그립·스플리터·텍스트 필드 위에서 바뀐 커서가 SubViewportContainer(뷰포트·UV 캔버스) 위로 오면
    /// Godot이 갱신하지 않아 그대로 남는 경우가 있다. 버튼을 누르지 않은 마우스 이동마다(GUI 처리 뒤) 호버 컨트롤의 커서 모양을 다시 적용한다.
    /// 임베디드 다이얼로그 위(루트에서 호버 컨트롤 없음)에서는 건드리지 않는다.
    /// </summary>
    private void FixCursor()
    {
        _cursorFixQueued = false;
        UiPerf.Count("fixCursor");
        var hovered = GetViewport().GuiGetHoveredControl();
        if (hovered == null || !hovered.IsVisibleInTree()) return;
        var local = hovered.GetGlobalTransformWithCanvas().AffineInverse() * _cursorPos;
        var shape = hovered.GetCursorShape(local);
        DisplayServer.CursorSetShape((DisplayServer.CursorShape)(int)shape);
    }

    /// <summary>Space가 뷰포트 판단 없이 눌림(Shell: 탭이면 4분할 토글, 홀드면 뷰 전환 파이).</summary>
    public event Action? SpaceDown;
    /// <summary>Space 뗌. <see cref="SpaceDown"/>과 짝을 이룬다.</summary>
    public event Action? SpaceUp;
    /// <summary>Space 누름을 이쪽에서 가로챘는지. 뗄 때 같은 경우에만 <see cref="SpaceUp"/>을 보낸다.</summary>
    private bool _spaceHeld;

    /// <summary>모달 툴 키 처리기(true = 처리함, 단축키로 보내지 않음). 모달 툴이 켜질 때 설정하고 끝날 때 비운다.</summary>
    public Func<InputEventKey, bool>? Modal;

    /// <summary>
    /// 전역 입력 처리. 순서:
    /// 1) 버튼 없는 마우스 이동 → 커서 복구 예약 후 종료,
    /// 2) 키를 뗌 → 홀드 키 제거, Space 뗌 통지,
    /// 3) 키 반복(Echo) 무시, 홀드 키 추가,
    /// 4) 텍스트 필드 포커스면 Esc만 처리(포커스 해제 + app.escape),
    /// 5) 모달 툴에 먼저 전달,
    /// 6) 수식어 없는 Space → SpaceDown,
    /// 7) KeyChord로 바꿔 HotkeyMap에서 액션을 찾아 실행(성공 시 입력 소비).
    /// </summary>
    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseMotion mm && mm.ButtonMask == 0) { _cursorPos = mm.Position; if (!_cursorFixQueued) { _cursorFixQueued = true; CallDeferred(nameof(FixCursor)); } return; }
        if (e is not InputEventKey k) return;
        // 포커스 주인이 텍스트 입력류(LineEdit/TextEdit/DataGrid, SpinBox 내부 LineEdit)인지 판정
        var focus = GetViewport().GuiGetFocusOwner();
        bool textFocused = focus is LineEdit or TextEdit or UI.ComponentEditor.DataGrid || (focus != null && focus.GetParent() is SpinBox);
        // 키를 뗀 경우: 홀드 키에서 빼고(논리/물리 키코드 모두), 이쪽이 잡은 Space면 SpaceUp 통지
        if (!k.Pressed)
        {
            if (HeldKeys.Remove(k.Keycode) | HeldKeys.Remove(k.PhysicalKeycode)) HeldKeysChanged?.Invoke();
            if (k.Keycode == Key.Space && _spaceHeld) { _spaceHeld = false; SpaceUp?.Invoke(); GetViewport().SetInputAsHandled(); }
            return;
        }
        // 키 반복은 무시. 새로 눌린 키는 홀드 집합에 추가
        if (k.Echo) return;
        // 논리·물리 키코드를 모두 넣는다(한글 IME 등에서 Keycode가 라틴 키가 아니어도 X/V/J 홀드 스냅이 동작하도록; 뗄 때도 둘 다 뺀다)
        bool addedKey = HeldKeys.Add(k.Keycode);
        if (k.PhysicalKeycode != Key.None && k.PhysicalKeycode != k.Keycode) addedKey |= HeldKeys.Add(k.PhysicalKeycode);
        if (addedKey) HeldKeysChanged?.Invoke();
        // 텍스트 입력 중에는 단축키를 보내지 않는다. Esc만 포커스를 풀고 전역 Escape 액션을 실행
        if (textFocused)
        {
            if (k.Keycode == Key.Escape) { focus!.ReleaseFocus(); Actions.Invoke("app.escape"); GetViewport().SetInputAsHandled(); }
            return;
        }
        // 모달 툴(대화형 Bevel 등)이 켜져 있으면 키를 먼저 넘긴다
        if (Modal != null && Modal(k)) { GetViewport().SetInputAsHandled(); return; }
        // 수식어 없는 Space는 액션 대신 탭/홀드 판정용 이벤트로 넘긴다
        if (k.Keycode == Key.Space && !k.CtrlPressed && !k.AltPressed && !k.ShiftPressed)
        {
            _spaceHeld = true; SpaceDown?.Invoke(); GetViewport().SetInputAsHandled();
            return;
        }

        // 일반 키: 컨텍스트(뷰포트/전역)에 맞는 바인딩을 찾아 액션 실행. 실행 가능할 때만 입력을 소비
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

    /// <summary>true면 해석 결과를 [Hotkey] 로그로 찍는다(--drive 실행 시 켜짐).</summary>
    public static bool Verbose;
}
