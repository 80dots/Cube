using Cube.App.Viewport;
using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.Tools;

public enum AxisOrientation { World, Object, Normal }

/// <summary>툴이 접근하는 공용 컨텍스트.</summary>
public sealed class ToolContext
{
    public required Document Doc { get; init; }
    public required ViewportPanel Viewport { get; init; }
    public required Settings Settings { get; init; }
    public SelectionState Sel => Doc.Selection;
    public UndoStack Undo => Doc.Undo;
    public AxisOrientation AxisOrientation { get; set; } = AxisOrientation.World;
    public bool CameraBasedSelection => Settings.CameraBasedSelection;
    public Action<string>? SetHelp;
}

/// <summary>뷰포트 입력을 받는 툴. 내비게이션이 소비하지 않은 이벤트만 온다.</summary>
public interface ITool
{
    string Id { get; }
    string Label { get; }
    string HelpText { get; }
    void Activate(ToolContext ctx);
    void Deactivate();
    /// <summary>true면 이벤트를 소비.</summary>
    bool HandleInput(InputEvent e);
    /// <summary>드래그 등 진행 중 작업을 취소(Esc, 포커스 잃음).</summary>
    void Cancel();
}

public abstract class ToolBase : ITool
{
    protected ToolContext Ctx { get; private set; } = null!;
    public abstract string Id { get; }
    public abstract string Label { get; }
    public virtual string HelpText => "";
    public virtual void Activate(ToolContext ctx) { Ctx = ctx; ctx.SetHelp?.Invoke(HelpText); }
    public virtual void Deactivate() { }
    public virtual bool HandleInput(InputEvent e) => false;
    public virtual void Cancel() { }
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
