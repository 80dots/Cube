namespace Cube.App.UI;

/// <summary>
/// 액션 ID → 아이콘 이름(assets/icons/*.svg, IconData에 내장). 파이 메뉴가 항목마다 아이콘+텍스트를 그릴 때 쓴다(v0.0.61, 사용자 지시).
/// 셸프·툴박스에 아이콘이 있는 액션은 <see cref="Shell"/>이 버튼을 만들 때 <see cref="Register"/>로 자동 등록되고,
/// 셸프에 없는 액션(선택·편집·뷰 등)은 아래 표에 직접 적는다. 접두어 규칙(폴백): mesh.* → shelf_connect, uv.* → uv_tweak, select.* → select, view.* → pie_view.
/// </summary>
public static class ActionIcons
{
    private static readonly Dictionary<string, string> Map = new()
    {
        // 모드·선택
        ["mode.object"] = "mode_object", ["mode.vertex"] = "mode_vertex", ["mode.edge"] = "mode_edge", ["mode.face"] = "mode_face", ["mode.uv"] = "mode_uv", ["mode.uvIsland"] = "mode_island",
        ["select.all"] = "pie_select_all", ["uv.selectAll"] = "pie_select_all", ["select.none"] = "pie_deselect", ["select.hierarchy"] = "pie_hierarchy",
        ["select.grow"] = "pie_grow", ["select.shrink"] = "pie_shrink", ["uv.growLoop"] = "pie_grow", ["uv.shrinkLoop"] = "pie_shrink",
        ["select.toVertices"] = "mode_vertex", ["select.toEdges"] = "mode_edge", ["select.toFaces"] = "mode_face", ["select.toUv"] = "mode_uv", ["select.toUvIsland"] = "pie_island",
        ["select.toBoundaryEdges"] = "pie_boundary", ["select.nonManifoldApply"] = "pie_nonmanifold", ["select.lights"] = "light_select",
        ["uv.selectInverse"] = "pie_deselect", ["uv.selectBackFacing"] = "mode_face", ["uv.selectFrontFacing"] = "mode_face", ["uv.selectOverlapping"] = "pie_island", ["uv.selectNonOverlapping"] = "pie_island",
        ["uv.selectTextureBorders"] = "pie_boundary", ["uv.selectUnmapped"] = "mode_uv", ["uv.shortestPath"] = "mode_edge", ["uv.containedFaces"] = "mode_face", ["uv.connectedFaces"] = "mode_face",
        // 편집·오브젝트
        ["edit.delete"] = "pie_delete", ["mesh.deleteComponents"] = "pie_delete", ["uv.deleteUvs"] = "pie_delete", ["edit.duplicate"] = "pie_duplicate", ["edit.deleteHistory"] = "pie_history",
        ["edit.centerPivot"] = "pie_pivot", ["edit.editPivot"] = "pie_pivot", ["material.assign"] = "pie_material", ["windows.materialEditor"] = "pie_material",
        ["file.exportSelection"] = "pie_export", ["view.frameSelected"] = "pie_frame", ["view.frameAll"] = "pie_frame", ["uv.frameAll"] = "uv_frame", ["uv.frameSelected"] = "uv_frame",
        ["symmetry.toggle"] = "shelf_mirror", ["symmetry.off"] = "shelf_mirror", ["symmetry.objectX"] = "shelf_mirror", ["symmetry.objectY"] = "shelf_mirror", ["symmetry.objectZ"] = "shelf_mirror", ["symmetry.worldX"] = "shelf_mirror", ["symmetry.worldY"] = "shelf_mirror", ["symmetry.worldZ"] = "shelf_mirror",
        ["uv.symmetryToggle"] = "shelf_mirror", ["uv.symmetryOff"] = "shelf_mirror", ["uv.symmetryU"] = "uv_flip_u", ["uv.symmetryV"] = "uv_flip_v", ["uv.symmetryCenterSelection"] = "shelf_mirror",
        ["tool.select"] = "select", ["tool.lasso"] = "lasso", ["tool.move"] = "move", ["tool.rotate"] = "rotate", ["tool.scale"] = "scale",
        // 메시
        ["mesh.mergeApply"] = "pie_merge", ["mesh.mergeToCenter"] = "pie_merge_center", ["mesh.collapse"] = "pie_collapse", ["mesh.fillHole"] = "shelf_fillhole",
        ["mesh.flipTriangleEdge"] = "pie_flip", ["mesh.spinEdgeForward"] = "pie_spin", ["mesh.spinEdgeBackward"] = "pie_spin", ["mesh.slideEdgeApply"] = "pie_slide",
        ["mesh.chamferVerticesApply"] = "pie_chamfer", ["mesh.bevelVerticesTool"] = "shelf_bevel", ["mesh.bevelTool"] = "shelf_bevel", ["mesh.pokeApply"] = "pie_poke", ["mesh.wedgeApply"] = "pie_wedge",
        ["mesh.extractFaces"] = "pie_extract", ["mesh.duplicateFaces"] = "pie_duplicate", ["mesh.detach"] = "pie_detach", ["mesh.reverse"] = "pie_reverse", ["mesh.averageVerticesApply"] = "pie_average",
        ["mesh.appendPolygon"] = "pie_append", ["mesh.triangulate"] = "pie_triangulate", ["mesh.quadrangulateApply"] = "pie_quadrangulate", ["mesh.cleanup"] = "pie_cleanup",
        ["normals.conform"] = "pie_conform", ["mesh.circularizeApply"] = "pie_circularize", ["mesh.offsetEdgeLoopApply"] = "pie_offset_loop", ["mesh.insertLoop"] = "pie_insert_loop",
        ["mesh.harden"] = "pie_harden", ["mesh.soften"] = "pie_soften", ["normals.softenHardenAngleApply"] = "pie_normals", ["normals.lock"] = "pie_lock", ["normals.unlock"] = "pie_unlock",
        ["mesh.creaseApply"] = "shelf_crease", ["mesh.creaseTool"] = "shelf_crease", ["mesh.removeCrease"] = "pie_crease_remove", ["mesh.extrudeVertexApply"] = "shelf_extrude",
        ["mesh.smoothApply"] = "smooth", ["mesh.divideEdgesApply"] = "pie_divisions_edge", ["mesh.separate"] = "shelf_separate", ["mesh.combine"] = "shelf_combine", ["mesh.bridge"] = "shelf_bridge",
        ["mesh.booleans"] = "pie_boolean", ["skin.detach"] = "skin_detach", ["skin.paintTool"] = "skin_paint", ["mesh.conform"] = "pie_conform",
        // 뷰
        ["view.top"] = "pie_view", ["view.bottom"] = "pie_view", ["view.front"] = "pie_view", ["view.back"] = "pie_view", ["view.left"] = "pie_view", ["view.side"] = "pie_view", ["view.persp"] = "persp",
        ["view.toggleProjection"] = "ortho", ["view.toggleLayout"] = "pie_panes",
        // UV
        ["uv.planarBest"] = "uv_planar", ["uv.cut"] = "uv_cut", ["uv.sew"] = "uv_sew", ["uv.moveAndSew"] = "uv_sew", ["uv.split"] = "uv_cut", ["uv.mergeApply"] = "uv_sew", ["uv.createShell"] = "pie_island", ["uv.createShellGrid"] = "pie_island",
        ["uv.automaticApply"] = "uv_automatic", ["uv.automatic"] = "uv_automatic", ["uv.cameraBased"] = "uv_planar", ["uv.bestPlane"] = "uv_planar", ["uv.contourStretch"] = "uv_unfold", ["display.uvGrid"] = "view_uv",
        ["uv.autoSeams"] = "uv_autoseam", ["uv.autoWrap"] = "uv_autowrap", ["uv.cutSewTool"] = "uv_cutsew", ["uv.layout"] = "uv_layout", ["uv.normalizeApply"] = "uv_layout", ["uv.unitize"] = "uv_layout",
        ["uv.straightenApply"] = "uv_straighten", ["uv.straightenBorder"] = "uv_straighten", ["uv.straightenShell"] = "uv_straighten", ["uv.pin"] = "uv_pin", ["uv.unpin"] = "uv_pin", ["uv.invertPins"] = "uv_pin", ["uv.unpinAll"] = "uv_pin",
        ["uv.toolNone"] = "select", ["uv.toolTweak"] = "uv_tweak", ["uv.toolMoveShell"] = "move", ["uv.toolGrab"] = "uv_brush", ["uv.toolSmooth"] = "uv_brush", ["uv.toolPinch"] = "uv_brush", ["uv.toolSmear"] = "uv_brush", ["uv.toolPinBrush"] = "uv_pin", ["uv.toolCutSew"] = "uv_cutsew", ["uv.brushOptions"] = "uv_brush",
        ["uv.setEditor"] = "uv_sets", ["uv.setCreate"] = "uv_sets", ["uv.setCopy"] = "uv_sets", ["uv.setDelete"] = "uv_sets", ["uv.setNext"] = "uv_sets",
        ["uv.rotateCw"] = "rotate", ["uv.rotateCcw"] = "rotate", ["uv.rotateApply"] = "rotate", ["uv.symmetrizeApply"] = "shelf_mirror", ["uv.cycle"] = "rotate",
        ["uv.orientShells"] = "rotate", ["uv.orientToEdge"] = "rotate", ["uv.randomizeShellsApply"] = "pie_island", ["uv.stackShells"] = "pie_island", ["uv.stackSimilar"] = "pie_island", ["uv.unstackShells"] = "pie_island",
        ["uv.snapAndStack"] = "pie_island", ["uv.distributeShellsU"] = "pie_island", ["uv.distributeShellsV"] = "pie_island", ["uv.gatherShells"] = "pie_island", ["uv.flipReversed"] = "uv_flip_u",
        ["uv.mapBorderSquare"] = "pie_boundary", ["uv.mapBorderCircle"] = "pie_boundary", ["uv.copy"] = "pie_duplicate", ["uv.paste"] = "pie_duplicate",
        ["uv.cycleBackground"] = "view_tex", ["uv.checkerMap"] = "view_uv", ["uv.checkerSizeUp"] = "view_uv", ["uv.checkerSizeDown"] = "view_uv", ["uv.viewShaded"] = "view_shade", ["uv.viewDistortion"] = "view_shade",
        ["uv.viewTextureBorders"] = "pie_boundary", ["uv.viewIsolate"] = "pie_island", ["uv.viewStats"] = "view_wire", ["uv.viewGrid"] = "view_grid", ["uv.viewTiles"] = "view_grid", ["uv.imageDim"] = "view_tex", ["uv.imageUnfiltered"] = "view_tex", ["uv.snapshot"] = "pie_export", ["uv.pixelSnap"] = "snap_grid",
        ["uv.alignMinU"] = "uv_straighten", ["uv.alignMaxU"] = "uv_straighten", ["uv.alignMinV"] = "uv_straighten", ["uv.alignMaxV"] = "uv_straighten", ["uv.alignCenterU"] = "uv_straighten", ["uv.alignCenterV"] = "uv_straighten", ["uv.linearAlign"] = "uv_straighten",
        ["uv.distributeU"] = "uv_layout", ["uv.distributeV"] = "uv_layout", ["uv.matchGridApply"] = "snap_grid", ["uv.matchUvs"] = "snap_point", ["uv.snapTogether"] = "snap_point",
    };

    /// <summary>셸프/툴박스 버튼을 만들 때 액션의 아이콘을 등록한다(표에 없을 때만; 표가 우선).</summary>
    public static void Register(string actionId, string icon) { if (!Map.ContainsKey(actionId)) Map[actionId] = icon; }

    /// <summary>액션의 아이콘 이름. 표 → 셸프 등록 → 접두어 폴백 순. 서브 파이 항목(ActionId "…sub")은 pie_sub.</summary>
    public static string For(string actionId)
    {
        if (Map.TryGetValue(actionId, out var n)) return n;
        if (actionId.EndsWith(".sub", StringComparison.Ordinal)) return "pie_sub";
        if (actionId.StartsWith("uv.", StringComparison.Ordinal)) return "uv_tweak";
        if (actionId.StartsWith("select.", StringComparison.Ordinal)) return "select";
        if (actionId.StartsWith("view.", StringComparison.Ordinal)) return "pie_view";
        if (actionId.StartsWith("normals.", StringComparison.Ordinal)) return "pie_normals";
        if (actionId.StartsWith("mesh.", StringComparison.Ordinal)) return "shelf_connect";
        return "pie_tool";
    }
}
