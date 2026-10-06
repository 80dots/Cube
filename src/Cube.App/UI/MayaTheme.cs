using Cube.App.Bridge;
using Godot;

namespace Cube.App.UI;

/// <summary>Maya 다크 UI 느낌의 테마를 코드로 만든다(.tres 수작업 대신).</summary>
public static class MayaTheme
{
    public static readonly Color Panel = MathConvert.Rgb(0x444444);
    public static readonly Color PanelDark = MathConvert.Rgb(0x373737);
    public static readonly Color Separator = MathConvert.Rgb(0x2b2b2b);
    public static readonly Color ButtonNormal = MathConvert.Rgb(0x4a4a4a);
    public static readonly Color ButtonHover = MathConvert.Rgb(0x5a5a5a);
    public static readonly Color ButtonPressed = MathConvert.Rgb(0x2d2d2d);
    public static readonly Color Text = MathConvert.Rgb(0xdcdcdc);
    public static readonly Color TextDim = MathConvert.Rgb(0x9a9a9a);
    public static readonly Color Accent = MathConvert.Rgb(0x5285a6);
    public static readonly Color Field = MathConvert.Rgb(0x2b2b2b);

    public static Theme Build(float scale)
    {
        var theme = new Theme();
        var font = new SystemFont { FontNames = new[] { "Segoe UI", "Malgun Gothic", "Noto Sans" }, Antialiasing = TextServer.FontAntialiasing.Gray, Hinting = TextServer.Hinting.Light };
        theme.DefaultFont = font;
        theme.DefaultFontSize = (int)(12 * scale);

        StyleBoxFlat Flat(Color bg, float radius = 2, Color? border = null, int bw = 0, int pad = 4)
        {
            var s = new StyleBoxFlat { BgColor = bg };
            s.SetCornerRadiusAll((int)(radius * scale));
            s.SetContentMarginAll(pad * scale);
            if (border is { } b) { s.BorderColor = b; s.SetBorderWidthAll((int)(bw * scale)); }
            return s;
        }

        // 패널
        theme.SetStylebox("panel", "Panel", Flat(Panel, 0, pad: 0));
        theme.SetStylebox("panel", "PanelContainer", Flat(Panel, 0, pad: 2));
        theme.SetStylebox("panel", "PopupMenu", Flat(PanelDark, 2, Separator, 1, 4));
        theme.SetStylebox("panel", "PopupPanel", Flat(PanelDark, 2, Separator, 1, 6));
        theme.SetStylebox("panel", "TabContainer", Flat(Panel, 0, pad: 2));
        theme.SetStylebox("panel", "Tree", Flat(Field, 0, pad: 2));
        theme.SetStylebox("panel", "ScrollContainer", Flat(Panel, 0, pad: 0));

        // 버튼
        var btnNormal = Flat(ButtonNormal, 3, pad: 4);
        var btnHover = Flat(ButtonHover, 3, pad: 4);
        var btnPressed = Flat(ButtonPressed, 3, Accent, 1, 4);
        var btnFocus = new StyleBoxEmpty();
        foreach (var type in new[] { "Button", "MenuButton", "OptionButton", "CheckButton", "CheckBox" })
        {
            theme.SetStylebox("normal", type, btnNormal);
            theme.SetStylebox("hover", type, btnHover);
            theme.SetStylebox("pressed", type, btnPressed);
            theme.SetStylebox("hover_pressed", type, btnPressed);
            theme.SetStylebox("focus", type, btnFocus);
            theme.SetStylebox("disabled", type, Flat(PanelDark, 3, pad: 4));
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_hover_color", type, Color.Color8(255, 255, 255));
            theme.SetColor("font_pressed_color", type, Color.Color8(255, 255, 255));
            theme.SetColor("font_disabled_color", type, TextDim);
        }
        // 메뉴바
        theme.SetStylebox("normal", "MenuBar", new StyleBoxEmpty());
        theme.SetStylebox("hover", "MenuBar", Flat(ButtonHover, 2, pad: 4));
        theme.SetStylebox("pressed", "MenuBar", Flat(ButtonPressed, 2, pad: 4));
        theme.SetColor("font_color", "MenuBar", Text);
        theme.SetColor("font_hover_color", "MenuBar", Color.Color8(255, 255, 255));
        theme.SetColor("font_pressed_color", "MenuBar", Color.Color8(255, 255, 255));
        theme.SetColor("font_color", "PopupMenu", Text);
        theme.SetColor("font_hover_color", "PopupMenu", Color.Color8(255, 255, 255));
        theme.SetColor("font_disabled_color", "PopupMenu", TextDim);
        theme.SetColor("font_accelerator_color", "PopupMenu", TextDim);
        theme.SetStylebox("hover", "PopupMenu", Flat(Accent, 2, pad: 2));
        theme.SetStylebox("separator", "PopupMenu", new StyleBoxLine { Color = Separator, Thickness = 1 });

        // 라벨
        theme.SetColor("font_color", "Label", Text);

        // 트리(Outliner)
        theme.SetColor("font_color", "Tree", Text);
        theme.SetColor("font_selected_color", "Tree", Color.Color8(255, 255, 255));
        theme.SetStylebox("selected", "Tree", Flat(Accent, 0, pad: 0));
        theme.SetStylebox("selected_focus", "Tree", Flat(Accent, 0, pad: 0));
        theme.SetStylebox("focus", "Tree", new StyleBoxEmpty());
        theme.SetColor("guide_color", "Tree", Separator);
        theme.SetColor("relationship_line_color", "Tree", TextDim);

        // 입력 필드
        foreach (var type in new[] { "LineEdit", "SpinBox" })
        {
            theme.SetStylebox("normal", type, Flat(Field, 2, Separator, 1, 3));
            theme.SetStylebox("focus", type, Flat(Field, 2, Accent, 1, 3));
            theme.SetStylebox("read_only", type, Flat(PanelDark, 2, Separator, 1, 3));
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_uneditable_color", type, TextDim);
            theme.SetColor("caret_color", type, Text);
            theme.SetColor("selection_color", type, Accent);
        }

        // 탭
        theme.SetStylebox("tab_selected", "TabContainer", Flat(Panel, 2, pad: 4));
        theme.SetStylebox("tab_unselected", "TabContainer", Flat(PanelDark, 2, pad: 4));
        theme.SetStylebox("tab_hovered", "TabContainer", Flat(ButtonHover, 2, pad: 4));
        theme.SetColor("font_selected_color", "TabContainer", Color.Color8(255, 255, 255));
        theme.SetColor("font_unselected_color", "TabContainer", TextDim);
        theme.SetColor("font_hovered_color", "TabContainer", Text);

        // 스플리터
        foreach (var type in new[] { "HSplitContainer", "VSplitContainer" })
        {
            theme.SetStylebox("split_bar_background", type, Flat(Separator, 0, pad: 0));
            theme.SetConstant("separation", type, (int)(4 * scale));
            theme.SetConstant("minimum_grab_thickness", type, (int)(6 * scale));
        }
        theme.SetConstant("separation", "HBoxContainer", (int)(2 * scale));
        theme.SetConstant("separation", "VBoxContainer", (int)(2 * scale));
        theme.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Separator, Thickness = 1 });
        theme.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Separator, Thickness = 1, Vertical = true });

        // 툴팁
        theme.SetStylebox("panel", "TooltipPanel", Flat(PanelDark, 2, Separator, 1, 4));
        theme.SetColor("font_color", "TooltipLabel", Text);
        return theme;
    }
}
