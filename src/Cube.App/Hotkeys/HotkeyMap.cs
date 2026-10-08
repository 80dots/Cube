using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Cube.App.Hotkeys;

/// <summary>바인딩이 유효한 범위. JSON의 "context" 값("viewport" 또는 생략)에 대응한다.</summary>
/// <remarks>같은 키 조합이라도 뷰포트 컨텍스트에서는 Viewport 바인딩이 Global보다 우선한다.</remarks>
public enum HotkeyContext { Global, Viewport }

/// <summary>키 + 수식어 조합. 문자열 형식: "Ctrl+Shift+Z", "F9", "Shift+Period".</summary>
/// <remarks>
/// 레코드 구조체라 값 동등성이 있으므로 <see cref="HotkeyMap"/>의 사전 키로 그대로 쓴다.
/// Key는 Godot <see cref="Godot.Key"/> 열거형(논리 키코드), 수식어는 Ctrl/Shift/Alt 세 개만 다룬다(Meta 없음).
/// </remarks>
public readonly record struct KeyChord(Key Key, bool Ctrl, bool Shift, bool Alt)
{
    /// <summary>
    /// "Ctrl+Shift+Z" 같은 문자열을 파싱한다. '+'로 나눈 토큰 중 ctrl/control/shift/alt는 수식어,
    /// 한 글자 숫자는 Key0~Key9, 그 외는 Godot Key 열거형 이름(대소문자 무시)으로 해석한다.
    /// </summary>
    /// <param name="text">키 조합 문자열.</param>
    /// <param name="chord">파싱 결과(실패 시 default).</param>
    /// <returns>알 수 없는 토큰이 있거나 주 키가 없으면 false.</returns>
    public static bool TryParse(string text, out KeyChord chord)
    {
        chord = default;
        bool ctrl = false, shift = false, alt = false;
        Key key = Key.None;
        // 토큰마다: 수식어면 플래그만 켜고 다음 토큰으로
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; continue;
                case "shift": shift = true; continue;
                case "alt": alt = true; continue;
            }
            // "1" 같은 숫자 한 글자는 Key.Key1로(열거형 이름이 Key1이라 Enum.TryParse로는 안 됨), 그 외는 열거형 이름
            if (raw.Length == 1 && char.IsDigit(raw[0])) { key = (Key)((int)Key.Key0 + (raw[0] - '0')); continue; }
            if (Enum.TryParse(raw, ignoreCase: true, out Key k)) { key = k; continue; }
            return false;
        }
        // 주 키 없이 수식어만 있으면 무효
        if (key == Key.None) return false;
        chord = new KeyChord(key, ctrl, shift, alt);
        return true;
    }

    /// <summary>
    /// Godot 키 이벤트를 조합으로 바꾼다. 한글 IME 등으로 Keycode가 0xFF를 넘는 비라틴 값이면 물리 키코드를 쓰고,
    /// Keycode가 None이어도 물리 키코드로 대체해 자판 배열과 무관하게 단축키가 동작하게 한다.
    /// </summary>
    public static KeyChord FromEvent(InputEventKey e)
    {
        var key = e.Keycode;
        // 한글 자판 등 키코드가 라틴 문자가 아니면 물리 키로 대체
        if ((long)key > 0xFF && e.PhysicalKeycode != Key.None && (long)e.PhysicalKeycode <= 0xFF) key = e.PhysicalKeycode;
        if (key == Key.None) key = e.PhysicalKeycode;
        return new KeyChord(key, e.CtrlPressed, e.ShiftPressed, e.AltPressed);
    }

    /// <summary>Godot PopupMenu accelerator 표시용 값.</summary>
    /// <remarks>주 키에 KeyModifierMask 비트를 OR해 PopupMenu.SetItemAccelerator에 넘길 값을 만든다.</remarks>
    public Key ToAccelerator()
    {
        var k = Key;
        if (Ctrl) k |= (Key)KeyModifierMask.MaskCtrl;
        if (Shift) k |= (Key)KeyModifierMask.MaskShift;
        if (Alt) k |= (Key)KeyModifierMask.MaskAlt;
        return k;
    }

    /// <summary>"Ctrl+Shift+Alt+키" 형식 문자열(<see cref="TryParse"/>와 왕복 가능). 숫자 키는 "0"~"9"로 표기.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl"); if (Shift) parts.Add("Shift"); if (Alt) parts.Add("Alt");
        parts.Add(Key >= Key.Key0 && Key <= Key.Key9 ? ((int)Key - (int)Key.Key0).ToString() : Key.ToString());
        return string.Join("+", parts);
    }
}

/// <summary>JSON 핫키 바인딩(기본값 res://config/hotkeys.default.json, 사용자 덮어쓰기 user://hotkeys.json).</summary>
/// <remarks>
/// 바인딩 목록(<see cref="Bindings"/>, 메뉴 표시·저장용)과 (조합, 컨텍스트) → 액션 조회 사전(<see cref="_lookup"/>)을 함께 유지한다.
/// 같은 (조합, 컨텍스트)가 여러 번 정의되면 조회는 마지막 것이 이긴다.
/// </remarks>
public sealed class HotkeyMap
{
    /// <summary>JSON 직렬화용 바인딩 항목 하나.</summary>
    private sealed class BindingDto
    {
        /// <summary>실행할 ActionId(예: "tool.move").</summary>
        [JsonPropertyName("action")] public string Action { get; set; } = "";
        /// <summary>키 조합 문자열(<see cref="KeyChord.TryParse"/> 형식).</summary>
        [JsonPropertyName("chord")] public string Chord { get; set; } = "";
        /// <summary>"viewport"면 뷰포트 컨텍스트 전용, null/그 외면 전역.</summary>
        [JsonPropertyName("context")] public string? Context { get; set; }
    }
    /// <summary>JSON 파일 최상위 구조.</summary>
    private sealed class FileDto
    {
        /// <summary>파일 형식 버전(현재 1).</summary>
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        /// <summary>바인딩 목록.</summary>
        [JsonPropertyName("bindings")] public List<BindingDto> Bindings { get; set; } = new();
    }

    /// <summary>파싱된 바인딩(액션, 키 조합, 컨텍스트).</summary>
    public readonly record struct Binding(string Action, KeyChord Chord, HotkeyContext Context);

    /// <summary>정의 순서대로의 바인딩 목록.</summary>
    private readonly List<Binding> _bindings = new();
    /// <summary>(키 조합, 컨텍스트) → ActionId 조회 표.</summary>
    private readonly Dictionary<(KeyChord, HotkeyContext), string> _lookup = new();

    /// <summary>읽기 전용 바인딩 목록(메뉴 단축키 표시·저장 등).</summary>
    public IReadOnlyList<Binding> Bindings => _bindings;

    /// <summary>앱에 포함된 기본 바인딩 파일 경로.</summary>
    public const string DefaultPath = "res://config/hotkeys.default.json";
    /// <summary>사용자 바인딩 파일 경로. 있으면 기본 파일 대신 이 파일 전체를 쓴다(병합 아님).</summary>
    public const string UserPath = "user://hotkeys.json";

    /// <summary>
    /// 바인딩을 읽는다. 사용자 파일이 있고 비어 있지 않으면 그것을, 아니면 기본 파일을 쓴다. 둘 다 없으면 빈 맵.
    /// </summary>
    public static HotkeyMap Load()
    {
        var map = new HotkeyMap();
        string? json = null;
        // 우선순위: user://hotkeys.json → res://config/hotkeys.default.json
        if (Godot.FileAccess.FileExists(UserPath)) json = Godot.FileAccess.GetFileAsString(UserPath);
        if (string.IsNullOrWhiteSpace(json) && Godot.FileAccess.FileExists(DefaultPath)) json = Godot.FileAccess.GetFileAsString(DefaultPath);
        if (!string.IsNullOrWhiteSpace(json)) map.LoadJson(json);
        return map;
    }

    /// <summary>
    /// JSON 문자열에서 바인딩을 모두 다시 만든다(기존 내용은 지움). 파싱할 수 없는 조합은 경고만 찍고 건너뛴다.
    /// </summary>
    public void LoadJson(string json)
    {
        _bindings.Clear(); _lookup.Clear();
        var dto = JsonSerializer.Deserialize<FileDto>(json);
        if (dto == null) return;
        // 항목마다 조합 파싱 → 컨텍스트 결정("viewport"만 뷰포트, 나머지는 전역) → 등록
        foreach (var b in dto.Bindings)
        {
            if (!KeyChord.TryParse(b.Chord, out var chord)) { GD.PushWarning($"[Hotkeys] bad chord '{b.Chord}' for {b.Action}"); continue; }
            var ctx = string.Equals(b.Context, "viewport", StringComparison.OrdinalIgnoreCase) ? HotkeyContext.Viewport : HotkeyContext.Global;
            Add(b.Action, chord, ctx);
        }
    }

    /// <summary>바인딩 하나를 추가한다. 조회 표에서는 같은 (조합, 컨텍스트)의 이전 값을 덮어쓴다.</summary>
    public void Add(string action, KeyChord chord, HotkeyContext ctx = HotkeyContext.Global)
    {
        _bindings.Add(new Binding(action, chord, ctx));
        _lookup[(chord, ctx)] = action;
    }

    /// <summary>뷰포트 컨텍스트가 활성(마우스가 뷰포트 위 또는 포커스)이면 뷰포트 바인딩을 먼저 본다.</summary>
    /// <remarks>뷰포트 컨텍스트가 아니면 전역 바인딩만 본다.</remarks>
    /// <param name="chord">눌린 키 조합.</param>
    /// <param name="viewportContext">마우스가 뷰포트 위이거나 뷰포트에 포커스가 있는지.</param>
    /// <param name="action">찾은 ActionId.</param>
    /// <returns>바인딩이 있으면 true.</returns>
    public bool TryResolve(KeyChord chord, bool viewportContext, out string action)
    {
        if (viewportContext && _lookup.TryGetValue((chord, HotkeyContext.Viewport), out action!)) return true;
        return _lookup.TryGetValue((chord, HotkeyContext.Global), out action!);
    }

    /// <summary>액션의 첫 바인딩(메뉴 표시용).</summary>
    /// <remarks>정의 순서상 처음 나오는 바인딩을 돌려준다. 없으면 null.</remarks>
    public KeyChord? FirstChord(string action)
    {
        foreach (var b in _bindings) if (b.Action == action) return b.Chord;
        return null;
    }

    /// <summary>
    /// 현재 바인딩을 user://hotkeys.json에 들여쓰기 JSON으로 저장한다. 전역 바인딩은 context를 생략(null)한다.
    /// </summary>
    public void SaveUser()
    {
        var dto = new FileDto();
        foreach (var b in _bindings) dto.Bindings.Add(new BindingDto { Action = b.Action, Chord = b.Chord.ToString(), Context = b.Context == HotkeyContext.Viewport ? "viewport" : null });
        using var f = Godot.FileAccess.Open(UserPath, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }
}
