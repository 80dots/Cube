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
                                         "mesh.smooth", "skeleton.jointTool", "skin.bind", "skin.detach", "skin.paintTool", "edit.deleteHistory", "tool.move", "tool.rotate", "tool.scale", "file.exportSelection",
                                         "mesh.mirror", "mesh.triangulate", "mesh.quadrangulate", "mesh.cleanup", "normals.conform", "mesh.multiCut" },
            SelectMode.Face => new[] { "mesh.extrude", "select.toEdges", "mesh.harden", "select.grow", "edit.delete", "select.shrink", "mesh.soften", "select.toVertices",
                                       "mesh.reverse", "mesh.merge", "mesh.bevel", "mesh.bridge", "mesh.addDivisions", "mesh.poke", "mesh.duplicateFaces", "mesh.extractFaces", "mesh.detach", "mesh.collapse", "mesh.triangulate", "mesh.quadrangulate", "mesh.circularize", "mesh.wedge" },
            SelectMode.Edge => new[] { "mesh.harden", "select.toFaces", "mesh.bridge", "select.grow", "edit.delete", "select.shrink", "mesh.soften", "select.toVertices",
                                       "mesh.merge", "mesh.bevel", "mesh.insertLoop", "mesh.extrude", "mesh.connect", "mesh.collapse", "mesh.flipTriangleEdge", "mesh.spinEdgeForward", "mesh.offsetEdgeLoop", "mesh.slideEdge", "mesh.fillHole", "mesh.crease", "mesh.multiCut" },
            SelectMode.Vertex => new[] { "mesh.merge", "select.toFaces", "mesh.harden", "select.grow", "edit.delete", "select.shrink", "mesh.soften", "select.toEdges",
                                         "mesh.connect", "mesh.chamferVertices", "mesh.mergeToCenter", "mesh.averageVertices", "mesh.detach", "mesh.targetWeld", "mesh.multiCut" },
            _ => new[] { "mode.object", "mode.vertex", "mode.edge", "mode.face" },
        };
        var items = ids.Select(id => Item(shell, id)).ToList();
        if (sel.Mode == SelectMode.Object)
        {
            bool hasMesh = sel.Objects.Any(id => shell.Document.Find(id)?.Mesh != null);
            items.Insert(8, new PieItem("Assign Material ▸", "material.assign", hasMesh) { Sub = () => MaterialItems(shell) });
            items.Insert(9, Item(shell, "edit.centerPivot", "Center Pivot"));
            items.Insert(10, Item(shell, "edit.editPivot", "Edit Pivot"));
        }
        return items;
    }

    /// <summary>Assign Material 서브 파이: lambert1 + 문서 머티리얼(현재 할당은 •) + Material Editor 열기.</summary>
    public static List<PieItem> MaterialItems(Shell shell)
    {
        var doc = shell.Document;
        var active = doc.Find(doc.Selection.ActiveObject);
        int current = active?.MaterialId ?? -1;
        var list = new List<PieItem>
        {
            new("lambert1" + (current == 0 ? " •" : ""), "material.assign.0") { Run = () => shell.AssignMaterialToSelection(0) },
        };
        foreach (var m in doc.Materials)
        {
            int id = m.Id;
            list.Add(new PieItem(m.Name + (current == id ? " •" : ""), "material.assign." + id) { Run = () => shell.AssignMaterialToSelection(id) });
        }
        list.Add(Item(shell, "windows.materialEditor", "Material Editor..."));
        return list;
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
        Item(shell, "select.hierarchy", "Select Hierarchy"),
        Item(shell, "select.all", "Select All"),
        Item(shell, "select.none", "Deselect All"),
    };

    /// <summary>UV 편집기 RMB(기본): 모드 전환. N UV, NE Edge, E Face, SE Island, S Object, SW Deselect All, W Select All, NW Frame.</summary>
    public static List<PieItem> UvModeMenu(Shell shell, bool island)
    {
        var sel = shell.Document.Selection;
        PieItem Mode(string label, string action, bool on) => new(label + (on ? " •" : ""), action);
        return new List<PieItem>
        {
            Mode("UV", "mode.uv", sel.Mode == SelectMode.Uv && !island),
            Mode("Edge", "mode.edge", sel.Mode == SelectMode.Edge),
            Mode("Face", "mode.face", sel.Mode == SelectMode.Face),
            Mode("Island", "mode.uvIsland", sel.Mode == SelectMode.Uv && island),
            Mode("Object Mode", "mode.object", sel.Mode == SelectMode.Object),
            Item(shell, "select.none", "Deselect All"),
            Item(shell, "select.all", "Select All"),
            Item(shell, "uv.frameSelected", "Frame Selected"),
        };
    }

    /// <summary>UV 편집기 Ctrl+RMB: 선택 변환. N To Edge, NE To Face, E To UV, SE To Island, S To Object, SW To Boundary Edge, W Grow, NW Shrink.</summary>
    public static List<PieItem> UvSelectMenu(Shell shell) => new()
    {
        Item(shell, "select.toEdges", "To Edge"),
        Item(shell, "select.toFaces", "To Face"),
        Item(shell, "select.toUv", "To UV"),
        Item(shell, "select.toUvIsland", "To Island"),
        Item(shell, "mode.object", "To Object"),
        Item(shell, "select.toBoundaryEdges", "To Boundary Edge"),
        Item(shell, "select.grow", "Grow"),
        Item(shell, "select.shrink", "Shrink"),
    };

    /// <summary>UV 편집기 Shift+RMB(Edit): UV 편집기가 지원하는 모든 기능. UV 편집기에 기능을 추가하면 여기에도 넣는다.</summary>
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
            Item(shell, "uv.frameAll", "Frame All"),
            Item(shell, "uv.frameSelected", "Frame Selected"),
            Item(shell, "uv.planarX", "Planar X"),
            Item(shell, "uv.planarY", "Planar Y"),
            Item(shell, "uv.planarZ", "Planar Z"),
            Item(shell, "uv.spherical", "Spherical"),
            Item(shell, "uv.flipU", "Flip U"),
            Item(shell, "uv.flipV", "Flip V"),
            Item(shell, "uv.cycleBackground", "Background"),
            Item(shell, "uv.autoSeams", "Auto Seam Select"),
            Item(shell, "uv.autoWrap", "Auto Wrap"),
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
