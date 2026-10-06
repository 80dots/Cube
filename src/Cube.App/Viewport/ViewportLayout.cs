using Cube.Core.Camera;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 단일 뷰 ↔ 4분할 뷰(persp / top / front / side) 레이아웃. 패널 4개를 모두 만들어 두고 보이기만 바꾼다.
/// 마우스가 들어가거나 눌린 패널이 활성 패널이 되어 툴/핫키의 대상이 된다.
/// </summary>
public partial class ViewportLayout : Control
{
    public readonly List<ViewportPanel> Panels = new();
    public ViewportPanel Active { get; private set; } = null!;
    public bool IsQuad { get; private set; }
    public event Action<ViewportPanel>? ActiveChanged;

    private GridContainer _grid = null!;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Ignore;
        _grid = new GridContainer { Name = "Grid", Columns = 2 };
        _grid.SetAnchorsPreset(LayoutPreset.FullRect);
        _grid.AddThemeConstantOverride("h_separation", 2);
        _grid.AddThemeConstantOverride("v_separation", 2);
        AddChild(_grid);
        foreach (var kind in new[] { ViewKind.Top, ViewKind.Persp, ViewKind.Front, ViewKind.Side })
        {
            var p = new ViewportPanel { Name = "Viewport_" + kind, InitialView = kind };
            p.Activated += () => SetActive(p);
            Panels.Add(p);
            _grid.AddChild(p);
        }
        Active = Panels[1]; // persp
        SetQuad(false);
    }

    public void Bind(Document doc) { foreach (var p in Panels) p.Bind(doc); }

    public void SetActive(ViewportPanel p)
    {
        if (Active == p) return;
        Active = p;
        ActiveChanged?.Invoke(p);
    }

    public void SetQuad(bool quad)
    {
        IsQuad = quad;
        foreach (var p in Panels) p.Visible = quad || p == Active;
        _grid.Columns = quad ? 2 : 1;
    }

    public void Toggle() => SetQuad(!IsQuad);

    public bool AnyHovered => Hovered != null || Panels.Any(p => p.Visible && p.HasFocus());

    /// <summary>마우스가 올라간 보이는 패널. 패널이 받은 마우스 이벤트 기준이며, 없으면 OS 커서 위치로 판정한다.</summary>
    public ViewportPanel? Hovered
    {
        get
        {
            var over = Panels.FirstOrDefault(p => p.Visible && p.IsMouseOver);
            if (over != null) return over;
            var mouse = GetViewport().GetMousePosition();
            return Panels.FirstOrDefault(p => p.Visible && p.GetGlobalRect().HasPoint(mouse));
        }
    }
}
