namespace Cube.App.Tools;

/// <summary>구현 전 자리표시 툴. 각 단계에서 실제 툴로 교체한다.</summary>
public sealed class PlaceholderTool : ToolBase
{
    public override string Id { get; }
    public override string Label { get; }
    public override string HelpText { get; }
    public PlaceholderTool(string id, string label, string help) { Id = id; Label = label; HelpText = help; }
}
