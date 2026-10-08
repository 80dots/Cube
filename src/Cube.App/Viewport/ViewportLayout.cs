using Cube.Core.Camera;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 단일 뷰 ↔ 4분할 뷰(persp / top / front / side) 레이아웃. 패널 4개를 모두 만들어 두고 보이기만 바꾼다.
/// 마우스가 들어가거나 눌린 패널이 활성 패널이 되어 툴/핫키의 대상이 된다.
/// </summary>
/// <remarks>
/// 2열 GridContainer에 패널을 top, persp, front, side 순으로 넣어 4분할이면 좌상 top/우상 persp/좌하 front/우하 side가 된다.
/// 단일 뷰는 활성 패널만 보이고 열 수를 1로 바꾼다. 숨은 패널도 계속 존재하며 SceneView는 숨은 동안 포즈/스킨 갱신을 미룬다.
/// </remarks>
public partial class ViewportLayout : Control
{
    /// <summary>패널 4개(top, persp, front, side 순).</summary>
    public readonly List<ViewportPanel> Panels = new();
    /// <summary>활성 패널(툴·핫키·표시 액션 대상). 기본은 persp.</summary>
    public ViewportPanel Active { get; private set; } = null!;
    /// <summary>4분할 레이아웃인지.</summary>
    public bool IsQuad { get; private set; }
    /// <summary>활성 패널이 바뀌었을 때(Shell이 ToolContext.Viewport를 갱신해 조작기를 옮긴다).</summary>
    public event Action<ViewportPanel>? ActiveChanged;

    /// <summary>패널을 담는 그리드(열 2 = 4분할, 1 = 단일).</summary>
    private GridContainer _grid = null!;

    /// <summary>그리드와 패널 4개를 만들고 각 패널의 Activated를 <see cref="SetActive"/>에 연결한 뒤 단일 뷰(persp)로 시작한다.</summary>
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

    /// <summary>모든 패널을 문서에 연결한다.</summary>
    public void Bind(Document doc) { foreach (var p in Panels) p.Bind(doc); }

    /// <summary>활성 패널을 바꾸고(같으면 무시) <see cref="ActiveChanged"/>를 알린다.</summary>
    public void SetActive(ViewportPanel p)
    {
        if (Active == p) return;
        Active = p;
        ActiveChanged?.Invoke(p);
    }

    /// <summary>4분할/단일 레이아웃을 설정한다. 단일이면 활성 패널만 보인다.</summary>
    public void SetQuad(bool quad)
    {
        IsQuad = quad;
        foreach (var p in Panels) p.Visible = quad || p == Active;
        _grid.Columns = quad ? 2 : 1;
    }

    /// <summary>단일 ↔ 4분할 토글(Space 탭, view.toggleLayout).</summary>
    public void Toggle() => SetQuad(!IsQuad);

    /// <summary>마우스가 어느 패널 위에 있거나 보이는 패널이 키보드 포커스를 가졌는지(핫키 'viewport' 컨텍스트 판정).</summary>
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
