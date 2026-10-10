using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI;

/// <remarks>
/// 각 메서드는 열릴 때마다 새 항목 목록을 만들어 돌려준다(현재 선택 모드·액션 활성 상태를 반영하기 위해).
/// 목록의 앞 8개는 PieMenu에서 방사형 방향(N, NE, E, SE, S, SW, W, NW 순)이 되고 9번째부터는 아래 오버플로 목록이 된다.
/// 항목 활성 여부는 <see cref="Item"/>이 ActionRegistry의 Enabled로 정한다.
/// </remarks>
/// <summary>뷰포트 파이 메뉴 구성. RMB = 선택 모드 전환, Shift+RMB = 현재 모드에서 수행 가능한 편집 액션, Space 홀드 = 뷰 전환.</summary>
public static class PieMenus
{
    /// <summary>
    /// RMB 기본 파이 = Maya 폴리곤 RMB 마킹 메뉴 배치(v0.0.61): N Vertex, E Edge, S Face, W Object Mode, SE UV.
    /// Maya의 NE(Vertex Face)·NW(Multi)는 Cube에 없는 모드라 그 자리에 Select All(NE)·Frame Selected(NW)를, SW에 Deselect All을 둔다.
    /// 오버플로(Maya 목록부): Select Hierarchy, Hide/Show, Assign Material ▸.
    /// </summary>
    public static List<PieItem> ModeMenu(Shell shell)
    {
        var sel = shell.Document.Selection;
        PieItem Mode(string label, string action, SelectMode mode) => Item(shell, action, label + (sel.Mode == mode ? " •" : ""));
        bool hasMesh = sel.Objects.Any(id => shell.Document.Find(id)?.Mesh != null);
        return new List<PieItem>
        {
            Mode("Vertex", "mode.vertex", SelectMode.Vertex),          // N
            Item(shell, "select.all", "Select All"),                    // NE (Maya: Vertex Face)
            Mode("Edge", "mode.edge", SelectMode.Edge),                // E
            Mode("UV", "mode.uv", SelectMode.Uv),                      // SE
            Mode("Face", "mode.face", SelectMode.Face),                // S
            Item(shell, "select.none", "Deselect All"),                 // SW
            Mode("Object Mode", "mode.object", SelectMode.Object),     // W
            Item(shell, "view.frameSelected", "Frame Selected"),        // NW (Maya: Multi)
            Item(shell, "select.hierarchy", "Select Hierarchy"),
            Item(shell, "symmetry.toggle", "Symmetry"),
            Item(shell, "display.hideSelection", "Hide Selection"),
            Item(shell, "display.showAll", "Show All"),
            new PieItem("Assign Material ▸", "material.assign", hasMesh) { Sub = () => MaterialItems(shell), Icon = PieIcon("material.assign"), SmallIcon = true },
        };
    }

    /// <summary>
    /// Shift+RMB Edit 파이 = Maya 폴리곤 모델링 마킹 메뉴(contextPolyTools*MM) 배치를 따른다(v0.0.61).
    /// 공통 앵커: S = Extrude, SW = Delete, E = Multi-Cut(Maya 2015+ 공통 항목). 모드별 방사형:
    /// 오브젝트 N Combine·NE Mirror·SE Smooth·W Separate·NW Insert Edge Loop / 정점 N Merge·NE Chamfer·SE Connect·W Average·NW Target Weld /
    /// 엣지 N Bevel·NE Bridge·SE Connect·W Insert Edge Loop·NW Collapse / 면 N Bevel·NE Bridge·SE Duplicate Face·W Extract·NW Poke.
    /// 9번째부터는 Maya 목록부에 해당하는 나머지 기능(오버플로 목록).
    /// </summary>
    public static List<PieItem> ContextMenu(Shell shell)
    {
        var sel = shell.Document.Selection;
        (string id, string? label)[] ids = sel.Mode switch
        {
            SelectMode.Object => new (string, string?)[] {
                ("mesh.combine", null), ("mesh.mirrorApply", "Mirror"), ("mesh.multiCut", null), ("mesh.smoothApply", "Smooth"), ("mesh.extrudeApply", "Extrude"), ("edit.delete", null), ("mesh.separate", null), ("mesh.insertLoop", "Insert Edge Loop"),
                ("mesh.targetWeld", null), ("mesh.fillHole", "Fill Holes"), ("mesh.appendPolygon", null), ("normals.softenHardenAngleApply", "Soften/Harden Edge"), ("normals.smartSoftenHardenApply", "Smart Soften/Harden"),
                ("mesh.offsetEdgeLoopApply", "Offset Edge Loop"), ("mesh.triangulate", null), ("mesh.quadrangulateApply", "Quadrangulate"), ("mesh.booleans", "Booleans ▸"), ("mesh.cleanup", null), ("mesh.connect", null),
                ("mesh.arrayApply", "Array"), ("edit.duplicate", null), ("edit.centerPivot", null), ("edit.editPivot", null), ("edit.freezeApply", "Freeze Transformations"), ("material.assign", "Assign Material ▸") },
            SelectMode.Vertex => new (string, string?)[] {
                ("mesh.mergeApply", "Merge Vertices"), ("mesh.chamferVerticesApply", "Chamfer Vertex"), ("mesh.multiCut", null), ("mesh.connect", null), ("mesh.extrudeApply", "Extrude"), ("edit.delete", "Delete Vertex"), ("mesh.averageVerticesApply", "Average Vertices"), ("mesh.targetWeld", null),
                ("mesh.extrudeVertexApply", "Extrude Vertex"), ("mesh.bevelVerticesTool", null), ("mesh.mergeToCenter", null), ("mesh.detach", "Detach Components"),
                ("mesh.creaseTool", null), ("select.grow", "Grow Selection"), ("select.shrink", "Shrink Selection"), ("symmetry.toggle", "Symmetry") },
            SelectMode.Edge => new (string, string?)[] {
                ("mesh.bevelApply", "Bevel Edge"), ("mesh.bridge", null), ("mesh.multiCut", null), ("mesh.connect", null), ("mesh.extrudeApply", "Extrude"), ("edit.delete", "Delete Edge"), ("mesh.insertLoop", "Insert Edge Loop"), ("mesh.collapse", "Collapse Edge"),
                ("mesh.flipTriangleEdge", "Flip Edge"), ("mesh.spinEdgeForward", "Spin Edge"), ("normals.softenHardenAngleApply", "Soften/Harden Edge"), ("normals.smartSoftenHardenApply", "Smart Soften/Harden"),
                ("mesh.offsetEdgeLoopApply", "Offset Edge Loop"), ("mesh.slideEdgeApply", "Slide Edge"), ("mesh.addDivisionsApply", "Add Divisions"), ("mesh.fillHole", null), ("mesh.bevelTool", null), ("mesh.mergeApply", "Merge Edges"),
                ("mesh.creaseApply", "Crease"), ("mesh.creaseTool", null), ("mesh.detach", "Detach Components"), ("select.grow", "Grow Selection"), ("select.shrink", "Shrink Selection"), ("symmetry.toggle", "Symmetry") },
            SelectMode.Face => new (string, string?)[] {
                ("mesh.bevelApply", "Bevel Face"), ("mesh.bridge", "Bridge Faces"), ("mesh.multiCut", null), ("mesh.duplicateFaces", "Duplicate Face"), ("mesh.extrudeApply", "Extrude"), ("edit.delete", "Delete Face"), ("mesh.extractFaces", "Extract Faces"), ("mesh.pokeApply", "Poke Face"),
                ("mesh.wedgeApply", "Wedge Face"), ("mesh.smoothApply", "Smooth Faces"), ("mesh.addDivisionsApply", "Add Divisions"), ("mesh.triangulate", "Triangulate Faces"), ("mesh.quadrangulateApply", "Quadrangulate Faces"), ("mesh.circularizeApply", "Circularize"),
                ("mesh.collapse", null), ("mesh.detach", "Detach Components"), ("mesh.mergeApply", "Merge"), ("mesh.reverse", null), ("mesh.targetWeld", null),
                ("select.grow", "Grow Selection"), ("select.shrink", "Shrink Selection"), ("symmetry.toggle", "Symmetry") },
            _ => new (string, string?)[] { ("mode.object", null), ("mode.vertex", null), ("mode.edge", null), ("mode.face", null) },
        };
        var items = new List<PieItem>();
        bool hasMesh = sel.Objects.Any(id => shell.Document.Find(id)?.Mesh != null);
        foreach (var (id, label) in ids)
        {
            if (id == "mesh.booleans") items.Add(new PieItem("Booleans ▸", "mesh.booleans", hasMesh) { Sub = () => Group(shell, ("mesh.booleanUnionApply", "Union"), ("mesh.booleanDifferenceApply", "Difference"), ("mesh.booleanIntersectionApply", "Intersection")), Icon = PieIcon("mesh.booleans"), SmallIcon = true });
            else if (id == "material.assign") items.Add(new PieItem("Assign Material ▸", "material.assign", hasMesh) { Sub = () => MaterialItems(shell), Icon = PieIcon("material.assign"), SmallIcon = true });
            else items.Add(Item(shell, id, label));
        }
        return items;
    }

    /// <summary>(액션, 라벨) 목록 → 항목 목록(서브 파이용).</summary>
    private static List<PieItem> Group(Shell shell, params (string action, string label)[] items) => items.Select(i => Item(shell, i.action, i.label)).ToList();

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

    /// <summary>Ctrl+RMB 선택 변환 파이 = Maya Convert Selection 마킹 메뉴 배치(v0.0.61): N To Edges, E To Vertices, S To Faces, W To UVs; NE Edge Perimeter(경계), SE UV Shell, SW Grow, NW Shrink.</summary>
    /// <remarks>앞 8개 방사형 이후 Select Hierarchy / Non-Manifold / Select All / Deselect All이 오버플로 목록으로 붙는다.</remarks>
    public static List<PieItem> SelectMenu(Shell shell) => new()
    {
        Item(shell, "select.toEdges", "To Edges"),
        Item(shell, "select.toBoundaryEdges", "To Edge Perimeter"),
        Item(shell, "select.toVertices", "To Vertices"),
        Item(shell, "select.toUvIsland", "To UV Shell"),
        Item(shell, "select.toFaces", "To Faces"),
        Item(shell, "select.grow", "Grow Selection"),
        Item(shell, "select.toUv", "To UVs"),
        Item(shell, "select.shrink", "Shrink Selection"),
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
            Item(shell, "uv.symmetryToggle", "Symmetry"),
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
        Item(shell, "uv.selectIdenticalApply", "Identical Shells"),
        Item(shell, "uv.selectSimilarApply", "Similar Shells"),
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
        PieItem Sub(string label, Func<List<PieItem>> sub) => new(label + " ▸", "uv.sub") { Sub = sub, Icon = PieIcon("uv.sub"), SmallIcon = true };
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
            Sub("Modify", () => Group(("uv.normalizeApply", "Normalize"), ("uv.unitize", "Unitize"), ("uv.cycle", "Cycle"), ("uv.flipU", "Flip U"), ("uv.flipV", "Flip V"), ("uv.rotateCw", "Rotate 90 CW"), ("uv.rotateCcw", "Rotate 90 CCW"), ("uv.rotateApply", "Rotate"), ("uv.symmetrizeApply", "Symmetrize"), ("uv.symmetryToggle", "Symmetry (toggle)"), ("uv.symmetryU", "Symmetry U"), ("uv.symmetryV", "Symmetry V"), ("uv.symmetryCenterSelection", "Symmetry Center = Selection"), ("uv.straightenApply", "Straighten UVs"), ("uv.straightenBorder", "Straighten Border"), ("uv.straightenShell", "Straighten Shell"), ("uv.mapBorderSquare", "Map Border Square"), ("uv.mapBorderCircle", "Map Border Circle"), ("uv.optimize", "Optimize"))),
            Sub("Shells", () => Group(("uv.layout", "Layout..."), ("uv.orientShells", "Orient Shells"), ("uv.orientToEdge", "Orient to Edges"), ("uv.randomizeShellsApply", "Randomize"), ("uv.stackShells", "Stack"), ("uv.stackSimilar", "Stack Similar"), ("uv.unstackShells", "Unstack"), ("uv.cloneShellApply", "Clone UV Shell"), ("uv.snapAndStack", "Snap and Stack"), ("uv.distributeShellsU", "Distribute U"), ("uv.distributeShellsV", "Distribute V"), ("uv.gatherShells", "Gather"), ("uv.flipReversed", "Flip Reversed"))),
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
        return new PieItem(label ?? a?.Label ?? actionId, actionId, a != null && a.Enabled) { Icon = PieIcon(actionId), SmallIcon = true };
    }

    /// <summary>액션의 파이 아이콘 텍스처(18px × UI 배율; ActionIcons 표).</summary>
    public static Texture2D? PieIcon(string actionId) => Icons.Get(ActionIcons.For(actionId), (int)(18 * CubeApp.Instance.UiScale));
}
