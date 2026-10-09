using Cube.Core.Selection;

namespace Cube.App.UI;

/// <remarks>
/// 각 메서드는 열릴 때마다 새 항목 목록을 만들어 돌려준다(현재 선택 모드·액션 활성 상태를 반영하기 위해).
/// 목록의 앞 8개는 PieMenu에서 방사형 방향(N, NE, E, SE, S, SW, W, NW 순)이 되고 9번째부터는 아래 오버플로 목록이 된다.
/// 항목 활성 여부는 <see cref="Item"/>이 ActionRegistry의 Enabled로 정한다.
/// </remarks>
/// <summary>뷰포트 파이 메뉴 구성. RMB = 선택 모드 전환, Shift+RMB = 현재 모드에서 수행 가능한 편집 액션, Space 홀드 = 뷰 전환.</summary>
public static class PieMenus
{
    /// <summary>RMB 기본 파이: 선택 모드 전환(현재 모드에는 • 표시) + 전체 선택/해제 + Frame Selected.</summary>
    /// <remarks>방향 배치: N Vertex, NE UV, E Edge, SE Deselect All, S Face, SW Select All, W Object Mode, NW Frame Selected.</remarks>
    /// <summary>N, NE, E, SE, S, SW, W, NW 순.</summary>
    public static List<PieItem> ModeMenu(Shell shell)
    {
        var sel = shell.Document.Selection;
        // 모드 항목 헬퍼: 현재 선택 모드와 같으면 라벨 뒤에 " •"를 붙인다.
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

    /// <remarks>
    /// Shift+RMB Edit 파이. 모드별 액션 ID 배열에서 앞 8개가 방사형, 나머지는 오버플로 목록이다.
    /// 오브젝트 모드에서는 9~11번째 자리에 Assign Material 서브 파이(메시가 선택돼 있을 때만 활성), Center Pivot, Edit Pivot을 끼워 넣는다.
    /// UV 모드 등 그 밖의 모드에서는 모드 전환 항목만 보여 준다.
    /// </remarks>
    /// <summary>현재 선택 모드에서 의미 있는 액션들. 실행 불가한 것은 비활성으로 표시한다.</summary>
    public static List<PieItem> ContextMenu(Shell shell)
    {
        var sel = shell.Document.Selection;
        // 선택 모드 → 표시할 액션 ID 목록(대부분 *Apply = 마지막 옵션으로 즉시 실행).
        string[] ids = sel.Mode switch
        {
            SelectMode.Object => new[] { "mesh.combine", "edit.duplicate", "mesh.separate", "mesh.harden", "edit.delete", "mesh.soften", "mesh.reverse", "view.frameSelected",
                                         "mesh.smoothApply", "skin.detach", "skin.paintTool", "edit.deleteHistory", "tool.move", "tool.rotate", "tool.scale", "file.exportSelection",
                                         "mesh.mirrorApply", "mesh.arrayApply", "mesh.booleanUnionApply", "mesh.booleanDifferenceApply", "mesh.booleanIntersectionApply", "mesh.triangulate", "mesh.quadrangulateApply", "mesh.cleanup", "normals.conform", "mesh.multiCut" },
            SelectMode.Face => new[] { "mesh.extrudeApply", "select.toEdges", "mesh.harden", "select.grow", "edit.delete", "select.shrink", "mesh.soften", "select.toVertices",
                                       "mesh.reverse", "mesh.mergeApply", "mesh.bevelApply", "mesh.addDivisionsApply", "mesh.pokeApply", "mesh.duplicateFaces", "mesh.extractFaces", "mesh.detach", "mesh.collapse", "mesh.triangulate", "mesh.quadrangulateApply", "mesh.circularizeApply", "mesh.wedgeApply" },
            SelectMode.Edge => new[] { "mesh.harden", "select.toFaces", "mesh.bridge", "select.grow", "edit.delete", "select.shrink", "mesh.soften", "select.toVertices",
                                       "mesh.mergeApply", "mesh.bevelApply", "mesh.bevelTool", "mesh.insertLoop", "mesh.extrudeApply", "mesh.connect", "mesh.collapse", "mesh.flipTriangleEdge", "mesh.spinEdgeForward", "mesh.offsetEdgeLoopApply", "mesh.slideEdgeApply", "mesh.fillHole", "mesh.creaseApply", "mesh.creaseTool", "mesh.multiCut" },
            SelectMode.Vertex => new[] { "mesh.mergeApply", "select.toFaces", "mesh.harden", "select.grow", "edit.delete", "select.shrink", "mesh.soften", "select.toEdges",
                                         "mesh.connect", "mesh.extrudeApply", "mesh.extrudeVertexApply", "mesh.chamferVerticesApply", "mesh.bevelVerticesTool", "mesh.mergeToCenter", "mesh.averageVerticesApply", "mesh.detach", "mesh.targetWeld", "mesh.multiCut" },
            _ => new[] { "mode.object", "mode.vertex", "mode.edge", "mode.face" },
        };
        // 각 ID를 PieItem으로(라벨 = 액션 라벨, 실행 불가면 비활성).
        var items = ids.Select(id => Item(shell, id)).ToList();
        if (sel.Mode == SelectMode.Object)
        {
            // 메시가 하나라도 선택돼 있어야 머티리얼 할당이 의미가 있다.
            bool hasMesh = sel.Objects.Any(id => shell.Document.Find(id)?.Mesh != null);
            items.Insert(8, new PieItem("Assign Material ▸", "material.assign", hasMesh) { Sub = () => MaterialItems(shell) });
            items.Insert(9, Item(shell, "edit.centerPivot", "Center Pivot"));
            items.Insert(10, Item(shell, "edit.editPivot", "Edit Pivot"));
        }
        return items;
    }

    /// <summary>Assign Material 서브 파이: lambert1 + 문서 머티리얼(현재 할당은 •) + Material Editor 열기.</summary>
    /// <remarks>
    /// sticky 서브 파이로 열린다. 각 항목은 ActionId 대신 <c>Run</c>으로 선택 오브젝트에 머티리얼을 직접 할당하며,
    /// 현재 활성 오브젝트의 머티리얼에는 •를 붙인다. 마지막 항목은 Material Editor 창 열기.
    /// </remarks>
    public static List<PieItem> MaterialItems(Shell shell)
    {
        var doc = shell.Document;
        var active = doc.Find(doc.Selection.ActiveObject);
        // 활성 오브젝트의 현재 머티리얼 ID(없으면 -1 → 어떤 항목에도 • 없음).
        int current = active?.MaterialId ?? -1;
        // 항목마다 구 썸네일(Material Editor와 같은 방식, Shell.PieThumbnail)
        var list = new List<PieItem>
        {
            new("lambert1" + (current == 0 ? " •" : ""), "material.assign.0") { Run = () => shell.AssignMaterialToSelection(0), Icon = shell.PieThumbnail(null) },
        };
        // 문서 머티리얼마다 항목 하나(람다가 반복 변수를 캡처하지 않도록 id 지역 복사).
        foreach (var m in doc.Materials)
        {
            int id = m.Id;
            list.Add(new PieItem(m.Name + (current == id ? " •" : ""), "material.assign." + id) { Run = () => shell.AssignMaterialToSelection(id), Icon = shell.PieThumbnail(m) });
        }
        list.Add(Item(shell, "windows.materialEditor", "Material Editor..."));
        return list;
    }

    /// <summary>Ctrl+RMB: 선택 변환. N To Edge, NE To Boundary Edge, E To Vertex, SE To Face, S To UV, SW To UV Island, W Grow, NW Shrink.</summary>
    /// <remarks>앞 8개 방사형 이후 Select Hierarchy / Select All / Deselect All이 오버플로 목록으로 붙는다.</remarks>
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
        Item(shell, "select.nonManifoldApply", "Non-Manifold"),
        Item(shell, "select.all", "Select All"),
        Item(shell, "select.none", "Deselect All"),
    };

    /// <summary>UV 편집기 RMB(기본): 모드 전환. N UV, NE Edge, E Face, SE Island, S Object, SW Deselect All, W Select All, NW Frame.</summary>
    /// <param name="island">UV 편집기가 Island 모드인지(UV 모드의 변형이라 UV/Island 중 어느 쪽에 •를 붙일지 결정).</param>
    public static List<PieItem> UvModeMenu(Shell shell, bool island)
    {
        var sel = shell.Document.Selection;
        // 모드 항목 헬퍼: on이면 라벨 뒤에 " •".
        PieItem Mode(string label, string action, bool on) => new(label + (on ? " •" : ""), action);
        return new List<PieItem>
        {
            Mode("UV", "mode.uv", sel.Mode == SelectMode.Uv && !island),
            Mode("Edge", "mode.edge", sel.Mode == SelectMode.Edge),
            Mode("Face", "mode.face", sel.Mode == SelectMode.Face),
            Mode("Island", "mode.uvIsland", sel.Mode == SelectMode.Uv && island),
            Mode("Object Mode", "mode.object", sel.Mode == SelectMode.Object),
            Item(shell, "select.none", "Deselect All"),
            Item(shell, "uv.selectAll", "Select All"),
            Item(shell, "uv.frameSelected", "Frame Selected"),
        };
    }

    /// <summary>UV 편집기 Ctrl+RMB: 선택 변환. N To Edge, NE To Face, E To UV, SE To Island, S To Object, SW To Boundary Edge, W Grow, NW Shrink + Select By Type 등.</summary>
    /// <remarks>앞 8개 이후는 UV 전용 선택(루프 따라 늘리기/줄이기, 반전, 앞/뒷면, 겹침, 텍스처 경계, 매핑 없음, 최단 경로 등) 오버플로 목록.</remarks>
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
        Item(shell, "uv.growLoop", "Grow Along Loop"),
        Item(shell, "uv.shrinkLoop", "Shrink Along Loop"),
        Item(shell, "uv.selectInverse", "Inverse"),
        Item(shell, "uv.selectBackFacing", "Back-Facing"),
        Item(shell, "uv.selectFrontFacing", "Front-Facing"),
        Item(shell, "uv.selectOverlapping", "Overlapping"),
        Item(shell, "uv.selectNonOverlapping", "Non-Overlapping"),
        Item(shell, "uv.selectTextureBorders", "Texture Borders"),
        Item(shell, "uv.selectUnmapped", "Unmapped"),
        Item(shell, "uv.shortestPath", "Shortest Edge Path"),
        Item(shell, "uv.containedFaces", "Contained Faces"),
        Item(shell, "uv.connectedFaces", "Connected Faces"),
    };

    /// <summary>
    /// UV 편집기 Shift+RMB(Edit): UV 편집기가 지원하는 모든 기능. 방사형 8개는 자주 쓰는 것, 나머지는 그룹별 서브 파이(▸, 버튼을 뗀 뒤 LMB로 선택).
    /// UV 편집기에 기능을 추가하면 여기(해당 그룹)에도 넣는다.
    /// </summary>
    public static List<PieItem> UvMenu(Shell shell)
    {
        // Group: (액션, 라벨) 목록을 PieItem 목록으로. Sub: 라벨에 ▸를 붙이고 고르면 하위 파이를 여는 항목.
        List<PieItem> Group(params (string action, string label)[] items) => items.Select(i => Item(shell, i.action, i.label)).ToList();
        PieItem Sub(string label, Func<List<PieItem>> sub) => new(label + " ▸", "uv.sub") { Sub = sub };
        return new List<PieItem>
        // 방사형 8개: 자주 쓰는 투영/펼치기/배치/자르기·꿰매기/프레임.
        {
            Item(shell, "uv.planarBest", "Planar"),
            Item(shell, "uv.cylindrical", "Cylindrical"),
            Item(shell, "uv.unfold", "Unfold"),
            Item(shell, "uv.layoutApply", "Layout"),
            Item(shell, "uv.cut", "Cut UV"),
            Item(shell, "uv.sew", "Sew UV"),
            Item(shell, "uv.frameAll", "Frame All"),
            Item(shell, "uv.frameSelected", "Frame Selected"),
            // 오버플로: 그룹별 서브 파이(열 때 생성기를 호출하므로 최신 상태가 반영됨).
            Sub("Create", () => Group(("uv.automaticApply", "Automatic"), ("uv.automatic", "Automatic..."), ("uv.cameraBased", "Camera-Based"), ("uv.planarX", "Planar X"), ("uv.planarY", "Planar Y"), ("uv.planarZ", "Planar Z"), ("uv.spherical", "Spherical"), ("uv.bestPlane", "Best Plane"), ("uv.contourStretch", "Contour Stretch"), ("display.uvGrid", "Checker Shader"))),
            Sub("Cut / Sew", () => Group(("uv.autoSeams", "Auto Seam Select"), ("uv.autoWrap", "Auto Wrap"), ("uv.createShell", "Create UV Shell"), ("uv.createShellGrid", "Create Shell Grid"), ("uv.split", "Split UVs"), ("uv.mergeApply", "Merge UVs"), ("uv.moveAndSew", "Move and Sew"), ("uv.deleteUvs", "Delete UVs"), ("uv.cutSewTool", "3D Cut/Sew Tool"))),
            Sub("Align / Snap", () => Group(("uv.alignMinU", "Align Min U"), ("uv.alignMaxU", "Align Max U"), ("uv.alignMinV", "Align Min V"), ("uv.alignMaxV", "Align Max V"), ("uv.alignCenterU", "Center U"), ("uv.alignCenterV", "Center V"), ("uv.linearAlign", "Linear Align"), ("uv.distributeU", "Distribute U"), ("uv.distributeV", "Distribute V"), ("uv.matchGridApply", "Match Grid"), ("uv.matchUvs", "Match UVs"), ("uv.snapTogether", "Snap Together"), ("uv.pixelSnap", "Pixel Snap"))),
            Sub("Modify", () => Group(("uv.normalizeApply", "Normalize"), ("uv.unitize", "Unitize"), ("uv.cycle", "Cycle"), ("uv.flipU", "Flip U"), ("uv.flipV", "Flip V"), ("uv.rotateCw", "Rotate 90 CW"), ("uv.rotateCcw", "Rotate 90 CCW"), ("uv.rotateApply", "Rotate"), ("uv.symmetrizeApply", "Symmetrize"), ("uv.straightenApply", "Straighten UVs"), ("uv.straightenBorder", "Straighten Border"), ("uv.straightenShell", "Straighten Shell"), ("uv.mapBorderSquare", "Map Border Square"), ("uv.mapBorderCircle", "Map Border Circle"), ("uv.optimize", "Optimize"))),
            Sub("Shells", () => Group(("uv.layout", "Layout..."), ("uv.orientShells", "Orient Shells"), ("uv.orientToEdge", "Orient to Edges"), ("uv.randomizeShellsApply", "Randomize"), ("uv.stackShells", "Stack"), ("uv.stackSimilar", "Stack Similar"), ("uv.unstackShells", "Unstack"), ("uv.snapAndStack", "Snap and Stack"), ("uv.distributeShellsU", "Distribute U"), ("uv.distributeShellsV", "Distribute V"), ("uv.gatherShells", "Gather"), ("uv.flipReversed", "Flip Reversed"))),
            Sub("Pin / Edit", () => Group(("uv.pin", "Pin"), ("uv.unpin", "Unpin"), ("uv.invertPins", "Invert Pins"), ("uv.unpinAll", "Unpin All"), ("uv.copy", "Copy UVs"), ("uv.paste", "Paste UVs"))),
            Sub("Tools", () => Group(("uv.toolNone", "Select/Transform"), ("uv.toolTweak", "Tweak"), ("uv.toolMoveShell", "Move Shell"), ("uv.toolGrab", "Grab"), ("uv.toolSmooth", "Smooth"), ("uv.toolPinch", "Pinch"), ("uv.toolSmear", "Smear"), ("uv.toolPinBrush", "Pin Brush"), ("uv.toolCutSew", "Cut/Sew"), ("uv.brushOptions", "Brush Options..."))),
            Sub("Display", () => Group(("uv.cycleBackground", "Background"), ("uv.checkerMap", "Checker Map"), ("uv.checkerSizeUp", "Checker +"), ("uv.checkerSizeDown", "Checker -"), ("uv.viewShaded", "Shaded"), ("uv.viewDistortion", "Distortion"), ("uv.viewTextureBorders", "Texture Borders"), ("uv.viewIsolate", "Isolate Select"), ("uv.viewStats", "Statistics"), ("uv.viewGrid", "Grid"), ("uv.viewTiles", "UV Tiles"), ("uv.imageDim", "Dim Image"), ("uv.imageUnfiltered", "Unfiltered"), ("uv.snapshot", "UV Snapshot..."))),
            Sub("UV Sets", () => Group(("uv.setEditor", "UV Set Editor"), ("uv.setCreate", "Create Empty Set..."), ("uv.setCopy", "Copy to New Set"), ("uv.setDelete", "Delete Current"), ("uv.setNext", "Next Set"))),
        };
    }

    /// <summary>Space 홀드: 뷰 전환. N Top, NE Front, E Right, SE Persp, S Bottom, SW Back, W Left, NW Frame All.</summary>
    /// <remarks>앞 8개 이후 원근/직교 전환과 단일/4분할 레이아웃 전환이 오버플로 목록으로 붙는다.</remarks>
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

    /// <summary>
    /// 액션 ID로 파이 항목을 만든다. 라벨 = label → 액션 라벨 → ID 순으로 정하고, 액션이 등록되어 있고 현재 실행 가능(Enabled)할 때만 활성이다.
    /// </summary>
    private static PieItem Item(Shell shell, string actionId, string? label = null)
    {
        var a = shell.Actions.Get(actionId);
        return new PieItem(label ?? a?.Label ?? actionId, actionId, a != null && a.Enabled);
    }
}
