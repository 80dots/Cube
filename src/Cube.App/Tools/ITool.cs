using Cube.App.Viewport;
using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.Tools;

public enum AxisOrientation { World, Object, Normal }

/// <summary>툴이 접근하는 공용 컨텍스트. 활성 뷰포트와 축 방향이 바뀌면 이벤트로 알린다.</summary>
public sealed class ToolContext
{
    public required Document Doc { get; init; }
    public required Settings Settings { get; init; }
    public SelectionState Sel => Doc.Selection;
    public UndoStack Undo => Doc.Undo;
    public bool CameraBasedSelection => Settings.CameraBasedSelection;
    public Action<string>? SetHelp;

    private ViewportPanel _viewport = null!;
    public required ViewportPanel Viewport
    {
        get => _viewport;
        set { if (_viewport == value) return; _viewport = value; ViewportChanged?.Invoke(value); }
    }
    public event Action<ViewportPanel>? ViewportChanged;

    private AxisOrientation _axis = AxisOrientation.World;
    public AxisOrientation AxisOrientation
    {
        get => _axis;
        set { if (_axis == value) return; _axis = value; AxisOrientationChanged?.Invoke(value); }
    }
    public event Action<AxisOrientation>? AxisOrientationChanged;
}

/// <summary>뷰포트 입력을 받는 툴. 내비게이션이 소비하지 않은 이벤트만 온다.</summary>
public interface ITool
{
    string Id { get; }
    string Label { get; }
    string HelpText { get; }
    void Activate(ToolContext ctx);
    void Deactivate();
    bool HandleInput(InputEvent e);
    void Cancel();
}

/// <summary>
/// 모달 툴(Blender식 대화형 연산: Bevel 등). 켜져 있는 동안 키 입력은 단축키보다 먼저 <see cref="HandleModalKey"/>로,
/// 뷰포트의 RMB/휠은 파이 메뉴·줌보다 먼저 툴로 간다(Alt 내비게이션은 그대로).
/// </summary>
public interface IModalTool : ITool
{
    bool HandleModalKey(InputEventKey k);
}

public abstract class ToolBase : ITool
{
    protected ToolContext Ctx { get; private set; } = null!;
    public abstract string Id { get; }
    public abstract string Label { get; }
    public virtual string HelpText => "";
    public virtual void Activate(ToolContext ctx)
    {
        Ctx = ctx;
        ctx.SetHelp?.Invoke(HelpText);
        ctx.ViewportChanged += OnViewportChanged;
    }
    public virtual void Deactivate() { Ctx.ViewportChanged -= OnViewportChanged; }
    public virtual bool HandleInput(InputEvent e) => false;
    public virtual void Cancel() { }
    /// <summary>활성 뷰포트가 바뀌었을 때(4분할 뷰). 피커/기즈모를 새 패널로 옮긴다.</summary>
    protected virtual void OnViewportChanged(ViewportPanel panel) { }
}

public sealed class ToolManager
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly ToolContext _ctx;
    public ITool? Current { get; private set; }
    public ITool? Previous { get; private set; }
    public event Action<ITool?>? ToolChanged;

    public ToolManager(ToolContext ctx) { _ctx = ctx; }

    public void Register(ITool tool) => _tools[tool.Id] = tool;
    public ITool? Get(string id) => _tools.TryGetValue(id, out var t) ? t : null;

    public void SetTool(string id)
    {
        if (!_tools.TryGetValue(id, out var tool) || tool == Current) return;
        Current?.Cancel();
        Current?.Deactivate();
        Previous = Current;
        Current = tool;
        tool.Activate(_ctx);
        ToolChanged?.Invoke(tool);
    }

    public void SwapToPrevious() { if (Previous != null) SetTool(Previous.Id); }

    public bool HandleInput(InputEvent e) => Current?.HandleInput(e) ?? false;
    public void CancelCurrent() => Current?.Cancel();
}
