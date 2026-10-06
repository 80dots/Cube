using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Cube.App.Hotkeys;

public enum HotkeyContext { Global, Viewport }

/// <summary>키 + 수식어 조합. 문자열 형식: "Ctrl+Shift+Z", "F9", "Shift+Period".</summary>
public readonly record struct KeyChord(Key Key, bool Ctrl, bool Shift, bool Alt)
{
    public static bool TryParse(string text, out KeyChord chord)
    {
        chord = default;
        bool ctrl = false, shift = false, alt = false;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; continue;
                case "shift": shift = true; continue;
                case "alt": alt = true; continue;
            }
            if (raw.Length == 1 && char.IsDigit(raw[0])) { key = (Key)((int)Key.Key0 + (raw[0] - '0')); continue; }
            if (Enum.TryParse(raw, ignoreCase: true, out Key k)) { key = k; continue; }
            return false;
        }
        if (key == Key.None) return false;
        chord = new KeyChord(key, ctrl, shift, alt);
        return true;
    }

    public static KeyChord FromEvent(InputEventKey e)
    {
        var key = e.Keycode;
        // 한글 자판 등 키코드가 라틴 문자가 아니면 물리 키로 대체
        if ((long)key > 0xFF && e.PhysicalKeycode != Key.None && (long)e.PhysicalKeycode <= 0xFF) key = e.PhysicalKeycode;
        if (key == Key.None) key = e.PhysicalKeycode;
        return new KeyChord(key, e.CtrlPressed, e.ShiftPressed, e.AltPressed);
    }

    /// <summary>Godot PopupMenu accelerator 표시용 값.</summary>
    public Key ToAccelerator()
    {
        var k = Key;
        if (Ctrl) k |= (Key)KeyModifierMask.MaskCtrl;
        if (Shift) k |= (Key)KeyModifierMask.MaskShift;
        if (Alt) k |= (Key)KeyModifierMask.MaskAlt;
        return k;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl"); if (Shift) parts.Add("Shift"); if (Alt) parts.Add("Alt");
        parts.Add(Key >= Key.Key0 && Key <= Key.Key9 ? ((int)Key - (int)Key.Key0).ToString() : Key.ToString());
        return string.Join("+", parts);
    }
}

/// <summary>JSON 핫키 바인딩(기본값 res://config/hotkeys.default.json, 사용자 덮어쓰기 user://hotkeys.json).</summary>
public sealed class HotkeyMap
{
    private sealed class BindingDto
    {
        [JsonPropertyName("action")] public string Action { get; set; } = "";
        [JsonPropertyName("chord")] public string Chord { get; set; } = "";
        [JsonPropertyName("context")] public string? Context { get; set; }
    }
    private sealed class FileDto
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("bindings")] public List<BindingDto> Bindings { get; set; } = new();
    }

    public readonly record struct Binding(string Action, KeyChord Chord, HotkeyContext Context);

    private readonly List<Binding> _bindings = new();
    private readonly Dictionary<(KeyChord, HotkeyContext), string> _lookup = new();

    public IReadOnlyList<Binding> Bindings => _bindings;

    public const string DefaultPath = "res://config/hotkeys.default.json";
    public const string UserPath = "user://hotkeys.json";

    public static HotkeyMap Load()
    {
        var map = new HotkeyMap();
        string? json = null;
        if (Godot.FileAccess.FileExists(UserPath)) json = Godot.FileAccess.GetFileAsString(UserPath);
        if (string.IsNullOrWhiteSpace(json) && Godot.FileAccess.FileExists(DefaultPath)) json = Godot.FileAccess.GetFileAsString(DefaultPath);
        if (!string.IsNullOrWhiteSpace(json)) map.LoadJson(json);
        return map;
    }

    public void LoadJson(string json)
    {
        _bindings.Clear(); _lookup.Clear();
        var dto = JsonSerializer.Deserialize<FileDto>(json);
        if (dto == null) return;
        foreach (var b in dto.Bindings)
        {
            if (!KeyChord.TryParse(b.Chord, out var chord)) { GD.PushWarning($"[Hotkeys] bad chord '{b.Chord}' for {b.Action}"); continue; }
            var ctx = string.Equals(b.Context, "viewport", StringComparison.OrdinalIgnoreCase) ? HotkeyContext.Viewport : HotkeyContext.Global;
            Add(b.Action, chord, ctx);
        }
    }

    public void Add(string action, KeyChord chord, HotkeyContext ctx = HotkeyContext.Global)
    {
        _bindings.Add(new Binding(action, chord, ctx));
        _lookup[(chord, ctx)] = action;
    }

    /// <summary>뷰포트 컨텍스트가 활성(마우스가 뷰포트 위 또는 포커스)이면 뷰포트 바인딩을 먼저 본다.</summary>
    public bool TryResolve(KeyChord chord, bool viewportContext, out string action)
    {
        if (viewportContext && _lookup.TryGetValue((chord, HotkeyContext.Viewport), out action!)) return true;
        return _lookup.TryGetValue((chord, HotkeyContext.Global), out action!);
    }

    /// <summary>액션의 첫 바인딩(메뉴 표시용).</summary>
    public KeyChord? FirstChord(string action)
    {
        foreach (var b in _bindings) if (b.Action == action) return b.Chord;
        return null;
    }

    public void SaveUser()
    {
        var dto = new FileDto();
        foreach (var b in _bindings) dto.Bindings.Add(new BindingDto { Action = b.Action, Chord = b.Chord.ToString(), Context = b.Context == HotkeyContext.Viewport ? "viewport" : null });
        using var f = Godot.FileAccess.Open(UserPath, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }
}
