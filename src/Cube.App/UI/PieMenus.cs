using Cube.Core.Selection;

namespace Cube.App.UI;

/// <summary>뷰포트 파이 메뉴 구성. RMB = 선택 모드 전환, Shift+RMB = 현재 모드에서 수행 가능한 편집 액션, Space 홀드 = 뷰 전환.</summary>
public static class PieMenus
{
    /// <summary>N, NE, E, SE, S, SW, W, NW 순.</summary>
    public static List<PieItem> ModeMenu(Shell shell)
    {
        var sel = shell.Document.Selection;
        PieItem Mode(string label, string action, SelectMode mode) => new(label + (sel.Mode == mode ? " •" : ""), action);
        return new List<PieItem>
        {
            Mode("Vertex", "mode.vertex", SelectMode.Vertex),
            Mode("UV", "mode.uv", SelectMode.Uv),
            Mode("Edge", "mode.edge", SelectMode.Edge),
            Item(shell, "select.none", "Deselect All"),
            Mode("Face", "mode.face", SelectMode.Face),
            Item(shell, "select.all", "Select All"),
            Mode("Object Mode", "mode.object", SelectMode.Object),
            Item(shell, "view.frameSelected", "Frame Selected"),
        };
    }

    /// <summary>현재 선택 모드에서 의미 있는 액션들. 실행 불가한 것은 비활성으로 표시한다.</summary>
    public static List<PieItem> ContextMenu(Shell shell)
    {
        var sel = shell.Document.Selection;
        string[] ids = sel.Mode switch
        {
            SelectMode.Object => new[] { "mesh.combine", "edit.duplicate", "mesh.separate", "mesh.harden", "edit.delete", "mesh.soften", "mesh.reverse", "view.frameSelected",
                                         "skeleton.jointTool", "skin.bind", "skin.detach", "skin.paintTool", "tool.move", "tool.rotate", "tool.scale", "file.exportSelection" },
            SelectMode.Face => new[] { "mesh.extrude", "select.toEdges", "mesh.harden", "select.grow", "edit.delete", "select.shrink", "mesh.soften", "select.toVertices",
                                       "mesh.reverse", "mesh.merge", "mesh.bevel", "mesh.bridge" },
            SelectMode.Edge => new[] { "mesh.harden", "select.toFaces", "mesh.bridge", "select.grow", "edit.delete", "select.shrink", "mesh.soften", "select.toVertices",
                                       "mesh.merge", "mesh.bevel", "mesh.insertLoop" },
            SelectMode.Vertex => new[] { "mesh.merge", "select.toFaces", "mesh.harden", "select.grow", "edit.delete", "select.shrink", "mesh.soften", "select.toEdges" },
            _ => new[] { "mode.object", "mode.vertex", "mode.edge", "mode.face" },
        };
        return ids.Select(id => Item(shell, id)).ToList();
    }

    /// <summary>Ctrl+RMB: 선택 변환. N To Edge, NE To Boundary Edge, E To Vertex, SE To Face, S To UV, SW To UV Island, W Grow, NW Shrink.</summary>
    public static List<PieItem> SelectMenu(Shell shell) => new()
    {
        Item(shell, "select.toEdges", "To Edge"),
        Item(shell, "select.toBoundaryEdges", "To Boundary Edge"),
        Item(shell, "select.toVertices", "To Vertex"),
        Item(shell, "select.toFaces", "To Face"),
        Item(shell, "select.toUv", "To UV"),
        Item(shell, "select.toUvIsland", "To UV Island"),
        Item(shell, "select.grow", "Grow"),
        Item(shell, "select.shrink", "Shrink"),
        Item(shell, "select.all", "Select All"),
        Item(shell, "select.none", "Deselect All"),
    };

    /// <summary>UV 편집기 RMB: UV 편집기가 지원하는 모든 기능. UV 편집기에 기능을 추가하면 여기에도 넣는다.</summary>
    public static List<PieItem> UvMenu(Shell shell)
    {
        var sel = shell.Document.Selection;
        PieItem Mode(string label, string action, SelectMode mode) => new(label + (sel.Mode == mode ? " •" : ""), action);
        return new List<PieItem>
        {
            Item(shell, "uv.planarBest", "Planar"),
            Item(shell, "uv.cylindrical", "Cylindrical"),
            Item(shell, "uv.unfold", "Unfold"),
            Item(shell, "uv.layout", "Layout"),
            Item(shell, "uv.cut", "Cut UV"),
            Item(shell, "uv.sew", "Sew UV"),
            Mode("UV Mode", "mode.uv", SelectMode.Uv),
            Item(shell, "uv.frameSelected", "Frame"),
            Item(shell, "uv.planarX", "Planar X"),
            Item(shell, "uv.planarY", "Planar Y"),
            Item(shell, "uv.planarZ", "Planar Z"),
            Item(shell, "uv.spherical", "Spherical"),
            Item(shell, "uv.flipU", "Flip U"),
            Item(shell, "uv.flipV", "Flip V"),
            Item(shell, "uv.frameAll", "Frame All"),
            Item(shell, "uv.cycleBackground", "Background"),
            Mode("Edge Mode", "mode.edge", SelectMode.Edge),
            Mode("Face Mode", "mode.face", SelectMode.Face),
            Mode("Object Mode", "mode.object", SelectMode.Object),
            Item(shell, "select.toUv", "To UV"),
            Item(shell, "select.toUvIsland", "To UV Island"),
        };
    }

    /// <summary>Space 홀드: 뷰 전환. N Top, NE Front, E Right, SE Persp, S Bottom, SW Back, W Left, NW Frame All.</summary>
    public static List<PieItem> ViewMenu(Shell shell) => new()
    {
        Item(shell, "view.top", "Top"),
        Item(shell, "view.front", "Front"),
        Item(shell, "view.side", "Right"),
        Item(shell, "view.persp", "Perspective"),
        Item(shell, "view.bottom", "Bottom"),
        Item(shell, "view.back", "Back"),
        Item(shell, "view.left", "Left"),
        Item(shell, "view.frameAll", "Frame All"),
        Item(shell, "view.toggleProjection", "Persp / Ortho"),
        Item(shell, "view.toggleLayout", "Single / Four Panes"),
    };

    private static PieItem Item(Shell shell, string actionId, string? label = null)
    {
        var a = shell.Actions.Get(actionId);
        return new PieItem(label ?? a?.Label ?? actionId, actionId, a != null && a.Enabled);
    }
}
