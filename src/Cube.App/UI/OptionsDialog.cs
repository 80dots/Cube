using Godot;

namespace Cube.App.UI;

/// <summary>옵션 창 필드 하나. Maya의 □ 옵션 창처럼 마지막 값이 유지된다(Values에 저장).</summary>
public sealed class OptionField
{
    public enum FieldKind { Float, Int, Bool, Enum, Vector3 }
    public string Key = ""; public string Label = ""; public FieldKind Kind;
    public double Min = double.MinValue, Max = double.MaxValue, Step = 0.001;
    public string[] Choices = Array.Empty<string>();
    public string? Tooltip;

    public static OptionField F(string key, string label, double min = double.MinValue, double max = double.MaxValue, double step = 0.001, string? tip = null) => new() { Key = key, Label = label, Kind = FieldKind.Float, Min = min, Max = max, Step = step, Tooltip = tip };
    public static OptionField I(string key, string label, int min, int max, string? tip = null) => new() { Key = key, Label = label, Kind = FieldKind.Int, Min = min, Max = max, Step = 1, Tooltip = tip };
    public static OptionField B(string key, string label, string? tip = null) => new() { Key = key, Label = label, Kind = FieldKind.Bool, Tooltip = tip };
    public static OptionField E(string key, string label, params string[] choices) => new() { Key = key, Label = label, Kind = FieldKind.Enum, Choices = choices };
    public static OptionField V(string key, string label, double step = 0.01) => new() { Key = key, Label = label, Kind = FieldKind.Vector3, Step = step };
}

/// <summary>옵션 값 저장소(액션별). 숫자/불/열거/벡터를 Vector3에 담는다.</summary>
public sealed class OptionValues
{
    private readonly Dictionary<string, Vector3> _v = new();
    public float Float(string k, float def = 0) => _v.TryGetValue(k, out var v) ? v.X : def;
    public int Int(string k, int def = 0) => _v.TryGetValue(k, out var v) ? (int)MathF.Round(v.X) : def;
    public bool Bool(string k, bool def = false) => _v.TryGetValue(k, out var v) ? v.X > 0.5f : def;
    public Vector3 Vec(string k, Vector3 def = default) => _v.TryGetValue(k, out var v) ? v : def;
    public void Set(string k, float x) => _v[k] = new Vector3(x, 0, 0);
    public void Set(string k, Vector3 v) => _v[k] = v;
    public bool Has(string k) => _v.ContainsKey(k);
}

/// <summary>
/// 범용 옵션 다이얼로그(Maya 옵션 창 대용): 필드 목록을 그리드로 만들고 OK(Apply)에서 값을 OptionValues에 저장한 뒤 콜백을 부른다.
/// </summary>
public partial class OptionsDialog : ConfirmationDialog
{
    private readonly List<(OptionField field, Control control)> _controls = new();
    private readonly OptionValues _values;
    private readonly Action _onApply;

    public OptionsDialog(string title, string okText, IReadOnlyList<OptionField> fields, OptionValues values, Action onApply)
    {
        _values = values; _onApply = onApply;
        Title = title; OkButtonText = okText;
        float s = CubeApp.Instance.UiScale;
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", (int)(12 * s));
        grid.AddThemeConstantOverride("v_separation", (int)(6 * s));
        foreach (var f in fields)
        {
            grid.AddChild(new Label { Text = f.Label, TooltipText = f.Tooltip ?? "" });
            Control c;
            switch (f.Kind)
            {
                case OptionField.FieldKind.Float:
                    c = new SpinBox { MinValue = Math.Max(f.Min, -1e9), MaxValue = Math.Min(f.Max, 1e9), Step = f.Step, AllowGreater = f.Max >= 1e9, AllowLesser = f.Min <= -1e9, Value = values.Float(f.Key), CustomMinimumSize = new Vector2(140 * s, 0) };
                    break;
                case OptionField.FieldKind.Int:
                    c = new SpinBox { MinValue = f.Min, MaxValue = f.Max, Step = 1, Value = values.Int(f.Key), CustomMinimumSize = new Vector2(140 * s, 0) };
                    break;
                case OptionField.FieldKind.Bool:
                    c = new CheckBox { ButtonPressed = values.Bool(f.Key) };
                    break;
                case OptionField.FieldKind.Enum:
                    {
                        var ob = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(140 * s, 0) };
                        foreach (var ch in f.Choices) ob.AddItem(ch);
                        ob.Selected = Math.Clamp(values.Int(f.Key), 0, Math.Max(0, f.Choices.Length - 1));
                        c = ob;
                        break;
                    }
                default:
                    {
                        var row = new HBoxContainer();
                        var v = values.Vec(f.Key);
                        foreach (var comp in new[] { v.X, v.Y, v.Z })
                            row.AddChild(new SpinBox { MinValue = -1e9, MaxValue = 1e9, Step = f.Step, AllowGreater = true, AllowLesser = true, Value = comp, CustomMinimumSize = new Vector2(80 * s, 0) });
                        c = row;
                        break;
                    }
            }
            c.TooltipText = f.Tooltip ?? "";
            grid.AddChild(c);
            _controls.Add((f, c));
        }
        AddChild(grid);
        Confirmed += () => { Store(); _onApply(); };
    }

    public override void _Ready() => ResizeGrip.AttachToWindow(this);

    private void Store()
    {
        foreach (var (f, c) in _controls)
        {
            switch (f.Kind)
            {
                case OptionField.FieldKind.Float: case OptionField.FieldKind.Int: _values.Set(f.Key, (float)((SpinBox)c).Value); break;
                case OptionField.FieldKind.Bool: _values.Set(f.Key, ((CheckBox)c).ButtonPressed ? 1f : 0f); break;
                case OptionField.FieldKind.Enum: _values.Set(f.Key, ((OptionButton)c).Selected); break;
                default:
                    {
                        var row = (HBoxContainer)c;
                        var sb = row.GetChildren().OfType<SpinBox>().ToArray();
                        _values.Set(f.Key, new Vector3((float)sb[0].Value, (float)sb[1].Value, (float)sb[2].Value));
                        break;
                    }
            }
        }
    }
}
