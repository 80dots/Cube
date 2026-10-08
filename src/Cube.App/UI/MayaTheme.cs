using Cube.App.Bridge;
using Godot;

namespace Cube.App.UI;

/// <remarks>
/// 색 상수는 sRGB 16진수(MathConvert.Rgb)로 정의되며 테마뿐 아니라 각 패널이 직접 덮어쓸 때(AddThemeColorOverride 등)도 공용으로 쓴다.
/// 모든 픽셀 값(글꼴 크기, 여백, 둥근 모서리, 테두리 두께)은 <see cref="Build"/>의 scale(UI 배율)을 곱한다.
/// </remarks>
/// <summary>Maya 다크 UI 느낌의 테마를 코드로 만든다(.tres 수작업 대신).</summary>
public static class MayaTheme
{
    /// <summary>기본 패널 배경(중간 회색).</summary>
    public static readonly Color Panel = MathConvert.Rgb(0x444444);
    /// <summary>어두운 패널 배경(팝업 메뉴·제목 바·비활성 버튼).</summary>
    public static readonly Color PanelDark = MathConvert.Rgb(0x373737);
    /// <summary>구분선·스플리터·테두리 색(가장 어두운 회색).</summary>
    public static readonly Color Separator = MathConvert.Rgb(0x2b2b2b);
    /// <summary>버튼 기본 배경.</summary>
    public static readonly Color ButtonNormal = MathConvert.Rgb(0x4a4a4a);
    /// <summary>버튼 호버 배경.</summary>
    public static readonly Color ButtonHover = MathConvert.Rgb(0x5a5a5a);
    /// <summary>버튼 눌림 배경(강조색 테두리와 함께 쓰임).</summary>
    public static readonly Color ButtonPressed = MathConvert.Rgb(0x2d2d2d);
    /// <summary>기본 글자색(밝은 회색).</summary>
    public static readonly Color Text = MathConvert.Rgb(0xdcdcdc);
    /// <summary>흐린 글자색(보조 설명·비활성·단축키 표시).</summary>
    public static readonly Color TextDim = MathConvert.Rgb(0x9a9a9a);
    /// <summary>강조색(Maya 선택 파랑): 선택 행, 포커스 테두리, 메뉴 호버.</summary>
    public static readonly Color Accent = MathConvert.Rgb(0x5285a6);
    /// <summary>입력 필드·트리·로그 본문 배경.</summary>
    public static readonly Color Field = MathConvert.Rgb(0x2b2b2b);

    /// <summary>
    /// 전체 앱 테마를 만든다. 글꼴(Segoe UI → 맑은 고딕 대체) 12pt × 배율, 그리고 컨트롤 종류별 StyleBox/색/상수를 설정한다.
    /// 셸을 다시 만들 때(Preferences에서 UI 배율 변경) 새 배율로 다시 호출된다.
    /// </summary>
    /// <param name="scale">UI 배율(화면 DPI 배율 × 사용자 배율).</param>
    /// <returns>셸 루트에 지정할 Theme.</returns>
    public static Theme Build(float scale)
    {
        var theme = new Theme();
        var font = new SystemFont { FontNames = new[] { "Segoe UI", "Malgun Gothic", "Noto Sans" }, Antialiasing = TextServer.FontAntialiasing.Gray, Hinting = TextServer.Hinting.Light };
        theme.DefaultFont = font;
        theme.DefaultFontSize = (int)(12 * scale);

        // 단색 StyleBoxFlat 헬퍼: 배경색, 모서리 반지름, 선택적 테두리(색·두께), 내용 여백. 모든 px 값에 배율을 곱한다.
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
        // 버튼은 포커스 테두리를 그리지 않는다(키보드 포커스가 단축키 입력과 섞이지 않도록 대부분 FocusMode None).
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
        // 메뉴 제목은 normal/hover/pressed 모두 같은 여백을 써서 호버 시 글자가 움직이지 않게 한다
        var menuNormal = new StyleBoxEmpty();
        menuNormal.SetContentMarginAll(6 * scale);
        theme.SetStylebox("normal", "MenuBar", menuNormal);
        theme.SetStylebox("hover", "MenuBar", Flat(ButtonHover, 2, pad: 6));
        theme.SetStylebox("pressed", "MenuBar", Flat(ButtonPressed, 2, pad: 6));
        theme.SetStylebox("disabled", "MenuBar", menuNormal);
        theme.SetConstant("h_separation", "MenuBar", (int)(10 * scale));
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
