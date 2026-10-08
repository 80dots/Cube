using Godot;

namespace Cube.App.UI;

/// <summary>옵션 창 필드 하나. Maya의 □ 옵션 창처럼 마지막 값이 유지된다(Values에 저장).</summary>
public sealed class OptionField
{
    /// <summary>필드 값의 종류. 어떤 컨트롤로 편집하고 OptionValues에 어떻게 저장할지를 정한다.</summary>
    public enum FieldKind { Float, Int, Bool, Enum, Vector3 }
    /// <summary>FieldKind 값: Float = 실수 SpinBox, Int = 정수 SpinBox, Bool = 체크박스, Enum = 드롭다운(선택 인덱스 저장), Vector3 = SpinBox 세 개.</summary>
    public string Key = ""; public string Label = ""; public FieldKind Kind;
    // Key = OptionValues의 저장 키(액션 코드가 읽는 이름), Label = 왼쪽 라벨 텍스트, Kind = 편집 종류.
    public double Min = double.MinValue, Max = double.MaxValue, Step = 0.001;
    // 숫자 필드의 범위와 증분. 범위가 ±1e9를 넘으면 사실상 무제한(AllowGreater/Lesser)으로 취급한다.
    public string[] Choices = Array.Empty<string>();
    /// <summary>Enum 필드의 선택지 라벨(저장 값은 인덱스).</summary>
    public string? Tooltip;
/// <summary>라벨과 컨트롤에 표시할 툴팁(없으면 빈 문자열).</summary>

    public static OptionField F(string key, string label, double min = double.MinValue, double max = double.MaxValue, double step = 0.001, string? tip = null) => new() { Key = key, Label = label, Kind = FieldKind.Float, Min = min, Max = max, Step = step, Tooltip = tip };
    /// <summary>실수 필드를 만든다(기본 증분 0.001, 범위 미지정이면 무제한).</summary>
    public static OptionField I(string key, string label, int min, int max, string? tip = null) => new() { Key = key, Label = label, Kind = FieldKind.Int, Min = min, Max = max, Step = 1, Tooltip = tip };
    /// <summary>정수 필드를 만든다(증분 1).</summary>
    public static OptionField B(string key, string label, string? tip = null) => new() { Key = key, Label = label, Kind = FieldKind.Bool, Tooltip = tip };
    /// <summary>불(체크박스) 필드를 만든다.</summary>
    public static OptionField E(string key, string label, params string[] choices) => new() { Key = key, Label = label, Kind = FieldKind.Enum, Choices = choices };
    /// <summary>열거(드롭다운) 필드를 만든다. 저장 값은 선택지 인덱스.</summary>
    public static OptionField V(string key, string label, double step = 0.01) => new() { Key = key, Label = label, Kind = FieldKind.Vector3, Step = step };
/// <summary>3성분 벡터 필드를 만든다(각 성분 무제한 SpinBox).</summary>
}

/// <summary>옵션 값 저장소(액션별). 숫자/불/열거/벡터를 Vector3에 담는다.</summary>
/// <remarks>
/// 스칼라는 X 성분에 넣고 Y/Z는 0이다. Bool은 1/0, Enum/Int는 실수로 저장한 뒤 읽을 때 반올림한다.
/// 키가 없으면 각 Get 메서드가 기본값을 돌려주므로 처음 실행할 때는 호출자가 준 기본값이 쓰인다.
/// </remarks>
public sealed class OptionValues
{
    private readonly Dictionary<string, Vector3> _v = new();
    /// <summary>키 → 값(스칼라는 X에만).</summary>
    public float Float(string k, float def = 0) => _v.TryGetValue(k, out var v) ? v.X : def;
    /// <summary>실수 값을 읽는다(없으면 def).</summary>
    public int Int(string k, int def = 0) => _v.TryGetValue(k, out var v) ? (int)MathF.Round(v.X) : def;
    /// <summary>정수 값을 읽는다(X를 반올림, 없으면 def).</summary>
    public bool Bool(string k, bool def = false) => _v.TryGetValue(k, out var v) ? v.X > 0.5f : def;
    /// <summary>불 값을 읽는다(X &gt; 0.5면 true, 없으면 def).</summary>
    public Vector3 Vec(string k, Vector3 def = default) => _v.TryGetValue(k, out var v) ? v : def;
    /// <summary>벡터 값을 읽는다(없으면 def).</summary>
    public void Set(string k, float x) => _v[k] = new Vector3(x, 0, 0);
    /// <summary>스칼라 값을 저장한다(X에 넣고 Y/Z = 0).</summary>
    public void Set(string k, Vector3 v) => _v[k] = v;
    /// <summary>벡터 값을 저장한다.</summary>
    public bool Has(string k) => _v.ContainsKey(k);
/// <summary>키가 한 번이라도 저장되었는지(기본값 초기화 여부 판단용).</summary>
}

/// <summary>
/// 범용 옵션 다이얼로그(Maya 옵션 창 대용): 필드 목록을 그리드로 만들고 OK(Apply)에서 값을 OptionValues에 저장한 뒤 콜백을 부른다.
/// </summary>
/// <remarks>
/// 다이얼로그는 생성 시 values의 현재 값으로 컨트롤을 채우고, 확인(Confirmed) 때만 <see cref="Store"/>로 값을 되돌려 쓴다.
/// 취소하면 values는 바뀌지 않는다. 같은 OptionValues 인스턴스가 액션별로 유지되어 Maya처럼 마지막 값이 기억된다.
/// </remarks>
public partial class OptionsDialog : ConfirmationDialog
{
    private readonly List<(OptionField field, Control control)> _controls = new();
    /// <summary>필드와 그 편집 컨트롤 쌍(Store가 순서대로 읽는다).</summary>
    private readonly OptionValues _values;
    /// <summary>읽고 쓸 값 저장소(액션별 인스턴스).</summary>
    private readonly Action _onApply;
/// <summary>OK를 누르고 값을 저장한 뒤 부를 콜백(보통 해당 액션의 Apply 실행).</summary>

    public OptionsDialog(string title, string okText, IReadOnlyList<OptionField> fields, OptionValues values, Action onApply)
    /// <summary>필드마다 (라벨, 편집 컨트롤) 두 칸 그리드를 만든다.</summary>
    /// <param name="title">창 제목.</param>
    /// <param name="okText">확인 버튼 텍스트(예: "Apply", "Bevel").</param>
    /// <param name="fields">표시할 필드 목록(순서대로 행이 됨).</param>
    /// <param name="values">초기값을 읽고 확인 시 값을 쓸 저장소.</param>
    /// <param name="onApply">확인 후 호출할 동작.</param>
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
                    // 실수: 범위가 ±1e9 밖이면 무제한 허용, SpinBox 자체 범위는 ±1e9로 자른다.
                    c = new SpinBox { MinValue = Math.Max(f.Min, -1e9), MaxValue = Math.Min(f.Max, 1e9), Step = f.Step, AllowGreater = f.Max >= 1e9, AllowLesser = f.Min <= -1e9, Value = values.Float(f.Key), CustomMinimumSize = new Vector2(140 * s, 0) };
                    break;
                case OptionField.FieldKind.Int:
                    // 정수: 지정 범위 안에서 1씩.
                    c = new SpinBox { MinValue = f.Min, MaxValue = f.Max, Step = 1, Value = values.Int(f.Key), CustomMinimumSize = new Vector2(140 * s, 0) };
                    break;
                case OptionField.FieldKind.Bool:
                    // 불: 체크박스.
                    c = new CheckBox { ButtonPressed = values.Bool(f.Key) };
                    break;
                case OptionField.FieldKind.Enum:
                    // 열거: 드롭다운. 저장된 인덱스를 선택지 범위로 자른다.
                    {
                        var ob = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(140 * s, 0) };
                        foreach (var ch in f.Choices) ob.AddItem(ch);
                        ob.Selected = Math.Clamp(values.Int(f.Key), 0, Math.Max(0, f.Choices.Length - 1));
                        c = ob;
                        break;
                    }
                default:
                    // 벡터: 가로로 SpinBox 세 개(X, Y, Z).
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
        // 확인을 누르면 값 저장 → 콜백 순서로 실행된다.
        Confirmed += () => { Store(); _onApply(); };
    }

    /// <summary>창에 크기 조절 그립과 SpinBox MMB 드래그(SpinDrag)를 붙인다. 다이얼로그는 별도 Window라 셸의 SpinDrag가 닿지 않기 때문.</summary>
    public override void _Ready() { ResizeGrip.AttachToWindow(this); AddChild(new SpinDrag()); }

    /// <summary>각 컨트롤의 현재 값을 종류에 맞게 OptionValues에 써 넣는다(Enum은 선택 인덱스, Vector3는 행의 SpinBox 세 개).</summary>
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
