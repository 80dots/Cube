using Cube.App.Tools;
using Cube.Core.Rig;
using Godot;

namespace Cube.App.UI;

/// <remarks>
/// UI 값은 바뀌는 즉시 <see cref="PaintWeightsTool"/>의 속성(Joint/Mode/Value/Radius)에 써 넣는다. 실제 칠하기는 툴이 뷰포트에서 처리한다.
/// 툴의 대상 메시가 바뀌면(TargetChanged) 영향 목록을 다시 만든다. 패널을 닫으면 페인트 툴에서 Select 툴로 돌아간다.
/// </remarks>
/// <summary>Paint Skin Weights Tool 설정 창: 영향(조인트) 목록, 모드, 값, 반지름, Flood.</summary>
public partial class PaintWeightsWindow : FloatingPanel
{
    /// <summary>툴 전환·문서 조회·액션 호출에 쓰는 셸.</summary>
    private Shell _shell = null!;
    /// <summary>설정을 전달할 가중치 페인트 툴.</summary>
    private PaintWeightsTool _tool = null!;
    /// <summary>스킨 클러스터의 영향(조인트) 목록. 항목 인덱스 = 스킨 조인트 인덱스.</summary>
    private ItemList _influences = null!;
    /// <summary>칠하기 모드 선택(Replace/Add/Smooth = PaintMode 정수값과 같은 순서).</summary>
    private OptionButton _mode = null!;
    /// <summary>칠할 가중치 값(0~1)과 브러시 반지름(m) 슬라이더.</summary>
    private HSlider _value = null!, _radius = null!;
    /// <summary>슬라이더 현재 값 표시 라벨들과 대상 메시 이름 라벨.</summary>
    private Label _valueLabel = null!, _radiusLabel = null!, _target = null!;

    /// <summary>패널 내용을 만들고 툴과 연결한다.</summary>
    public void Setup(Shell shell, PaintWeightsTool tool)
    {
        _shell = shell; _tool = tool;
        float s = CubeApp.Instance.UiScale;
        Title = "Paint Skin Weights Tool";
        Size = new Vector2(300 * s, 420 * s);
        MinPanelSize = new Vector2(240 * s, 300 * s);
        // 패널을 닫을 때 페인트 툴이 아직 활성이면 Select 툴로 되돌린다.
        Closed += () => { if (shell.Tools.Current?.Id == "paintWeights") shell.Tools.SetTool("select"); };

        var root = new VBoxContainer();
        root.SizeFlagsVertical = Control.SizeFlags.ExpandFill; root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        root.AddThemeConstantOverride("separation", (int)(6 * s));
        Content.AddChild(root);

        // 대상 메시 이름과 영향 목록(선택하면 그 조인트를 칠할 대상으로 지정).
        _target = new Label { Text = "Mesh: -" };
        root.AddChild(_target);
        root.AddChild(new Label { Text = "Influences" });
        _influences = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 120 * s) };
        _influences.ItemSelected += i => _tool.SetJoint((int)i);
        root.AddChild(_influences);

        // 모드 드롭다운.
        var modeRow = new HBoxContainer();
        modeRow.AddChild(new Label { Text = "Mode", CustomMinimumSize = new Vector2(60 * s, 0) });
        _mode = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var m in new[] { "Replace", "Add", "Smooth" }) _mode.AddItem(m);
        _mode.ItemSelected += i => _tool.Mode = (PaintMode)(int)i;
        modeRow.AddChild(_mode);
        root.AddChild(modeRow);

        // 값(0~1)과 반지름(0.01~5m) 슬라이더.
        root.AddChild(Slider("Value", 0, 1, 0.01, tool.Value, out _value, out _valueLabel, v => _tool.Value = v, s));
        root.AddChild(Slider("Radius", 0.01, 5, 0.01, tool.Radius, out _radius, out _radiusLabel, v => _tool.Radius = v, s));

        var buttons = new HFlowContainer(); // 좁아지면 줄바꿈
        // Flood = 모든 정점에 현재 값을 적용, Normalize = skin.normalize 액션(정점별 가중치 합 1로).
        var flood = new Button { Text = "Flood", FocusMode = Control.FocusModeEnum.None, TooltipText = "Apply the value to every vertex" };
        flood.Pressed += () => _tool.Flood();
        buttons.AddChild(flood);
        var normalize = new Button { Text = "Normalize", FocusMode = Control.FocusModeEnum.None };
        normalize.Pressed += () => shell.Actions.Invoke("skin.normalize");
        buttons.AddChild(normalize);
        root.AddChild(buttons);

        // 툴 대상이 바뀔 때마다 목록 갱신, 그리고 지금 한 번 채운다.
        tool.TargetChanged += RefreshTarget;
        RefreshTarget();
    }

    /// <summary>
    /// "라벨 + 가로 슬라이더 + 값 라벨(소수 둘째 자리)" 한 줄을 만든다. 슬라이더를 움직이면 set 콜백과 값 라벨을 함께 갱신한다.
    /// </summary>
    /// <param name="slider">만든 슬라이더(호출자가 필드에 보관).</param>
    /// <param name="valueLabel">만든 값 라벨.</param>
    /// <param name="set">값이 바뀔 때 툴에 써 넣는 콜백.</param>
    /// <param name="s">UI 배율.</param>
    private static Control Slider(string label, double min, double max, double step, float initial, out HSlider slider, out Label valueLabel, Action<float> set, float s)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(60 * s, 0) });
        var sl = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        var lbl = new Label { Text = initial.ToString("0.00"), CustomMinimumSize = new Vector2(40 * s, 0) };
        sl.ValueChanged += v => { set((float)v); lbl.Text = v.ToString("0.00"); };
        row.AddChild(sl); row.AddChild(lbl);
        slider = sl; valueLabel = lbl;
        return row;
    }

    /// <summary>
    /// 툴의 대상 메시를 반영한다: 메시 이름 라벨, 스킨이 있으면 조인트 이름으로 영향 목록을 다시 채우고 현재 조인트를 선택,
    /// 모드 드롭다운도 툴 상태와 맞춘다. 스킨이 없으면 "Bind Skin first" 안내.
    /// </summary>
    public void RefreshTarget()
    {
        _influences.Clear();
        var mesh = _tool.Mesh;
        _target.Text = mesh != null ? $"Mesh: {mesh.Name}" : "Mesh: (none — Bind Skin first)";
        if (mesh?.Skin is { } skin)
        {
            foreach (var id in skin.Joints) _influences.AddItem(_shell.Document.Find(id)?.Name ?? id.ToString());
            if (_tool.Joint < _influences.ItemCount) _influences.Select(_tool.Joint);
        }
        _mode.Selected = (int)_tool.Mode;
    }

    /// <summary>
    /// 패널을 보여 준다. 도크에 붙어 있으면 탭을 앞으로, 숨겨져 있으면 열되 이전 도크 자리가 없을 때만 화면 오른쪽(위에서 25%)에 둔다.
    /// </summary>
    public void Show(Shell shell)
    {
        if (Docked) { Open(); return; }
        if (!Visible)
        {
            var host = shell.GetViewport().GetVisibleRect().Size;
            bool redock = LastDock != null;
            Open();
            if (!redock) Position = new Vector2(host.X - Size.X - 24, host.Y * 0.25f);
        }
    }
}
