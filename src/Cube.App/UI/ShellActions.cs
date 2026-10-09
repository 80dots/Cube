using Cube.App.Tools;
using Cube.App.Viewport;
using Cube.Core.Camera;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Cube.Core.Uv;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// Shell의 액션 등록부(공통). 툴 등록, 기본 액션(툴/선택 모드/선택/피벗·스냅/편집/생성/메시/파일/뷰/표시/창),
/// 선택 변환(Grow/Shrink/Convert/To Boundary Edge/To UV), 기본 편집 명령(삭제·복제·결합·분리·하드/소프트 엣지·뒤집기·Bridge),
/// 그리고 상단 메뉴바 구성(BuildMenus)을 담당한다. 영역별 액션은 ShellMeshActions/ShellUvActions 등 다른 partial 파일이 등록한다.
/// </summary>
public partial class Shell
{
    /// <summary>
    /// ToolManager에 모든 툴 인스턴스를 등록한다. 툴은 Id로 구분되며 액션(tool.*, mesh.insertLoop 등)이
    /// Tools.SetTool(id)로 전환한다. 새 툴을 만들면 여기에 추가해야 SetTool로 찾을 수 있다.
    /// </summary>
    private void RegisterTools()
    {
        Tools.Register(new SelectTool());
        Tools.Register(new LassoTool());
        Tools.Register(new MoveTool());
        Tools.Register(new RotateTool());
        Tools.Register(new ScaleTool());
        Tools.Register(new InsertEdgeLoopTool());
        Tools.Register(new JointTool());
        Tools.Register(new PaintWeightsTool());
        Tools.Register(new CreatePolygonTool());
        Tools.Register(new InsertJointTool());
        Tools.Register(new EditPivotTool());
        Tools.Register(new MultiCutTool());
        Tools.Register(new CreaseTool());
        Tools.Register(new TargetWeldTool());
        Tools.Register(new AppendPolygonTool());
        Tools.Register(new CutSewUvTool());
        Tools.Register(new BevelTool());
    }

    /// <summary>
    /// 공통 액션을 ActionRegistry에 등록한 뒤 영역별 등록 함수(UV/Rig/Scene/Anim/Log/Component Editor/Mesh/Render/Bridge)를 호출한다.
    /// Register(id, label, run, canExecute, isChecked, repeatable): canExecute는 메뉴·셸프 비활성 판정,
    /// isChecked는 체크/토글 표시, repeatable이면 Repeat Last(G)의 대상이 된다.
    /// 이 함수는 메뉴·셸프 생성보다 먼저 호출되어야 한다(_Ready 참고).
    /// </summary>
    private void RegisterActions()
    {
        var doc = Document;
        var sel = doc.Selection;

        // tool.* = 기본 변형 툴 전환(체크 = 현재 툴), tool.last = 직전 툴로 되돌아가기, axis.* = 조작기 축 방향
        // --- 툴
        foreach (var (id, label) in new[] { ("select", "Select Tool"), ("lasso", "Lasso Tool"), ("move", "Move Tool"), ("rotate", "Rotate Tool"), ("scale", "Scale Tool") })
        {
            string t = id;
            Actions.Register("tool." + id, label, () => Tools.SetTool(t), isChecked: () => Tools.Current?.Id == t);
        }
        Actions.Register("tool.last", "Last Tool", () => Tools.SwapToPrevious());
        Actions.Register("axis.world", "Axis: World", () => ToolContext.AxisOrientation = AxisOrientation.World, isChecked: () => ToolContext.AxisOrientation == AxisOrientation.World);
        Actions.Register("axis.local", "Axis: Local", () => ToolContext.AxisOrientation = AxisOrientation.Object, isChecked: () => ToolContext.AxisOrientation == AxisOrientation.Object);
        Actions.Register("axis.normal", "Axis: Normal", () => ToolContext.AxisOrientation = AxisOrientation.Normal, isChecked: () => ToolContext.AxisOrientation == AxisOrientation.Normal);

        // mode.* = 선택 모드 전환. 컴포넌트 모드는 SetComponentMode로 마지막 모드를 기억해 mode.toggle(F8)이 그 모드로 돌아간다.
        // UV/UV Island는 같은 SelectMode.Uv이며 UV 편집기 캔버스의 IslandMode 플래그로 구분한다.
        // --- 선택 모드
        Actions.Register("mode.object", "Object Mode", () => sel.Mode = SelectMode.Object, isChecked: () => sel.Mode == SelectMode.Object);
        Actions.Register("mode.toggle", "Toggle Object/Component", () => sel.Mode = sel.Mode == SelectMode.Object ? _lastComponentMode : SelectMode.Object);
        Actions.Register("mode.vertex", "Vertex", () => SetComponentMode(SelectMode.Vertex), isChecked: () => sel.Mode == SelectMode.Vertex);
        Actions.Register("mode.edge", "Edge", () => SetComponentMode(SelectMode.Edge), isChecked: () => sel.Mode == SelectMode.Edge);
        Actions.Register("mode.face", "Face", () => SetComponentMode(SelectMode.Face), isChecked: () => sel.Mode == SelectMode.Face);
        Actions.Register("mode.uv", "UV", () => { SetComponentMode(SelectMode.Uv); UvEditorWindow?.Canvas.SetIslandMode(false); }, isChecked: () => sel.Mode == SelectMode.Uv && !(UvEditorWindow?.Canvas.IslandMode ?? false));
        Actions.Register("mode.uvIsland", "UV Island", () => { SetComponentMode(SelectMode.Uv); UvEditorWindow?.Canvas.SetIslandMode(true); }, isChecked: () => sel.Mode == SelectMode.Uv && (UvEditorWindow?.Canvas.IslandMode ?? false));

        // select.* = 전체 선택/해제, Grow/Shrink, 컴포넌트 변환(To Vertices/Edges/Faces/Boundary/UV/UV Island), 계층 선택.
        // 모두 RecordSelection으로 감싸 선택 변경이 Undo 가능(Maya와 동일).
        Actions.Register("select.all", "Select All", SelectAll);
        Actions.Register("select.none", "Deselect All", () => RecordSelection(s => s.ClearAll()), canExecute: () => !sel.IsEmpty);
        Actions.Register("select.grow", "Grow Selection", () => GrowShrink(true), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.shrink", "Shrink Selection", () => GrowShrink(false), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toVertices", "To Vertices", () => ConvertSelection(SelectMode.Vertex), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toEdges", "To Edges", () => ConvertSelection(SelectMode.Edge), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toFaces", "To Faces", () => ConvertSelection(SelectMode.Face), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toBoundaryEdges", "To Boundary Edges", ConvertToBoundaryEdges, canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toUv", "To UV", () => ConvertToUv(island: false), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toUvIsland", "To UV Island", () => ConvertToUv(island: true), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.hierarchy", "Select Hierarchy", SelectHierarchy, canExecute: () => sel.Objects.Count > 0);

        // Center Pivot = 메시 AABB 중심으로 피벗 이동, Edit Pivot = 피벗 편집 툴 토글(Insert),
        // snap.grid/point = 상태 라인 스냅 토글(설정 저장 후 상태 라인 버튼 동기화)
        // --- 피벗 / 스냅
        Actions.Register("edit.centerPivot", "Center Pivot", CenterPivot, canExecute: () => sel.Objects.Any(id => doc.Find(id)?.Mesh != null), repeatable: true);
        Actions.Register("edit.editPivot", "Edit Pivot", ToggleEditPivot, isChecked: () => Tools.Current?.Id == "editPivot");
        Actions.Register("snap.grid", "Snap to Grid", () => { Settings.SnapToGrid = !Settings.SnapToGrid; Settings.Save(); SyncStatusLine(); }, isChecked: () => Settings.SnapToGrid);
        Actions.Register("snap.point", "Snap to Points", () => { Settings.SnapToPoints = !Settings.SnapToPoints; Settings.Save(); SyncStatusLine(); }, isChecked: () => Settings.SnapToPoints);

        // Undo/Redo/Repeat Last, 삭제(모드에 따라 노드 또는 컴포넌트), 복제, 환경설정, 구성 이력 삭제,
        // Smooth Mesh Preview 1/2/3(표시 전용 서브디비전 미리보기 단계)
        // --- 편집
        Actions.Register("edit.undo", "Undo", () => doc.Undo.Undo(), canExecute: () => doc.Undo.CanUndo);
        Actions.Register("edit.redo", "Redo", () => doc.Undo.Redo(), canExecute: () => doc.Undo.CanRedo);
        Actions.Register("edit.repeatLast", "Repeat Last", () => Actions.RepeatLast(), canExecute: () => Actions.LastRepeatable != null);
        Actions.Register("edit.delete", "Delete", DeleteSelection, canExecute: () => sel.Mode == SelectMode.Object ? sel.Objects.Count > 0 : sel.NodesWithComponents(sel.Mode).Any(), repeatable: true); // UV 모드 Delete = uv.deleteUvs(선택 UV가 덮는 면의 UV 삭제; 예전에는 정점 삭제가 UV 점 ID를 받아 빈 Undo 단계만 남겼다)
        Actions.Register("edit.duplicate", "Duplicate", DuplicateSelection, canExecute: () => sel.Objects.Count > 0, repeatable: true);
        Actions.Register("edit.preferences", "Preferences...", ShowPreferences);
        Actions.Register("edit.deleteHistory", "Delete History", () => { var ids = sel.Objects.Where(id => doc.Find(id)?.MeshShape?.History.Count > 0).ToArray(); if (ids.Length > 0) doc.Undo.Push(new DeleteHistoryCommand(ids)); },
            canExecute: () => sel.Objects.Any(id => doc.Find(id)?.MeshShape?.History.Count > 0));
        // 레벨 0 = 케이지, 1 = 케이지 + 스무스, 2 = 스무스. lv를 지역 변수로 복사해 람다가 루프 변수를 공유하지 않게 한다.
        foreach (var (level, id, label) in new[] { (0, "display.smoothPreviewOff", "Smooth Mesh Preview: Cage (1)"), (1, "display.smoothPreviewBoth", "Smooth Mesh Preview: Cage + Smooth (2)"), (2, "display.smoothPreviewOn", "Smooth Mesh Preview: Smooth (3)") })
        {
            int lv = level;
            Actions.Register(id, label, () => SetSmoothPreview(lv), canExecute: () => sel.Objects.Any(x => doc.Find(x)?.Mesh != null) || sel.NodesWithComponents(sel.Mode).Any());
        }

        // 기본 폴리곤 도형 생성(CreatePrimitiveCommand; repeatable이라 G로 반복 생성 가능)
        // --- 생성
        Actions.Register("create.cube", "Polygon Cube", () => doc.Undo.Push(CreatePrimitiveCommand.Cube(doc)), repeatable: true);
        Actions.Register("create.sphere", "Polygon Sphere", () => doc.Undo.Push(CreatePrimitiveCommand.Sphere(doc)), repeatable: true);
        Actions.Register("create.cylinder", "Polygon Cylinder", () => doc.Undo.Push(CreatePrimitiveCommand.Cylinder(doc)), repeatable: true);
        Actions.Register("create.cone", "Polygon Cone", () => doc.Undo.Push(CreatePrimitiveCommand.Cone(doc)), repeatable: true);
        Actions.Register("create.plane", "Polygon Plane", () => doc.Undo.Push(CreatePrimitiveCommand.Plane(doc)), repeatable: true);
        Actions.Register("create.torus", "Polygon Torus", () => doc.Undo.Push(CreatePrimitiveCommand.Torus(doc)), repeatable: true);

        // Extrude(옵션 쌍), 컴포넌트 삭제(Maya Delete Edge/Vertex), Combine/Separate, 하드/소프트 엣지, 면 뒤집기, Bridge, 엣지 루프/크리즈 툴
        // --- 메시 편집
        RegisterBooleanActions(); // Maya Booleans(ShellBoolean.cs): mesh.booleanUnion/Difference/Intersection 옵션 쌍
        RegisterArrayActions(); // Blender식 Array(ShellArray.cs): mesh.array = 옵션 창, mesh.arrayApply = 실행
        RegisterExtrudeActions(); // Blender식 Extrude 옵션(ShellExtrude.cs): mesh.extrude = 옵션 창, mesh.extrudeApply = 실행
        Actions.Register("mesh.deleteComponents", "Delete Edge/Vertex", DeleteComponents, canExecute: () => sel.IsComponentMode && sel.Mode != SelectMode.Uv && sel.NodesWithComponents(sel.Mode).Any(), repeatable: true);
        Actions.Register("mesh.combine", "Combine", CombineSelection, canExecute: () => sel.Mode == SelectMode.Object && sel.Objects.Count(id => doc.Find(id)?.Mesh != null) >= 2);
        Actions.Register("mesh.separate", "Separate", SeparateSelection, canExecute: () => sel.Mode == SelectMode.Object && sel.Objects.Count == 1);
        Actions.Register("mesh.soften", "Soften Edge", () => SetEdgesHard(false), canExecute: () => HasEdgeTargets(), repeatable: true);
        Actions.Register("mesh.harden", "Harden Edge", () => SetEdgesHard(true), canExecute: () => HasEdgeTargets(), repeatable: true);
        Actions.Register("mesh.reverse", "Reverse", ReverseSelection, canExecute: () => sel.Mode == SelectMode.Object ? sel.Objects.Count > 0 : sel.Mode == SelectMode.Face && sel.NodesWithComponents(SelectMode.Face).Any(), repeatable: true);
        Actions.Register("mesh.bridge", "Bridge", BridgeSelection, canExecute: () => sel.Mode == SelectMode.Edge && sel.NodesWithComponents(SelectMode.Edge).Any(), repeatable: true);
        Actions.Register("mesh.insertLoop", "Insert Edge Loop Tool", () => Tools.SetTool("insertLoop"), isChecked: () => Tools.Current?.Id == "insertLoop");
        Actions.Register("mesh.creaseTool", "Crease Tool", () => Tools.SetTool("creaseTool"), isChecked: () => Tools.Current?.Id == "creaseTool");

        // 파일 액션 객체를 만들고(헬프 라인으로 메시지 출력) 씬 파일이 바뀌면 카메라 Home·창 제목 갱신
        // --- 파일
        Files = new IO.FileActions(doc, Settings, this, msg => HelpLine.Text = msg);
        SceneFiles = new IO.SceneFileActions(doc, Settings, this, msg => HelpLine.Text = msg);
        SceneFiles.FileChanged += () => { Viewport.CameraController.Home(); UpdateTitle(); };
        Actions.Register("file.new", "New Scene", () => SceneFiles.NewWithConfirm());
        Actions.Register("file.open", "Open Scene...", () => SceneFiles.ShowOpenDialog());
        Actions.Register("file.save", "Save Scene", () => SceneFiles.SaveOrSaveAs());
        Actions.Register("file.saveAs", "Save Scene As...", () => SceneFiles.ShowSaveAsDialog());
        Actions.Register("file.import", "Import...", () => Files.ShowImportDialog());
        Actions.Register("file.exportSelection", "Export Selection...", () => Files.ShowExportDialog(true), canExecute: () => sel.Objects.Count > 0);
        Actions.Register("file.exportAll", "Export All...", () => Files.ShowExportDialog(false), canExecute: () => doc.Root.Children.Count > 0);
        Actions.Register("file.exit", "Exit", RequestQuit);

        // 활성 뷰포트 카메라: 프레임, Home, 정사영/원근 뷰 전환, 투영 토글, 1/4분할, 최대화
        // --- 뷰
        Actions.Register("view.frameSelected", "Frame Selected", () => Viewport.FrameSelected());
        Actions.Register("view.frameAll", "Frame All", () => Viewport.FrameAll());
        Actions.Register("view.home", "Home", () => Viewport.CameraController.Home());
        Actions.Register("view.persp", "Perspective", () => Viewport.SetView(ViewKind.Persp), isChecked: () => Viewport.CameraController.Kind == ViewKind.Persp);
        Actions.Register("view.front", "Front", () => Viewport.SetView(ViewKind.Front), isChecked: () => Viewport.CameraController.Kind == ViewKind.Front);
        Actions.Register("view.side", "Side", () => Viewport.SetView(ViewKind.Side), isChecked: () => Viewport.CameraController.Kind == ViewKind.Side);
        Actions.Register("view.top", "Top", () => Viewport.SetView(ViewKind.Top), isChecked: () => Viewport.CameraController.Kind == ViewKind.Top);
        Actions.Register("view.back", "Back", () => Viewport.SetView(ViewKind.Back), isChecked: () => Viewport.CameraController.Kind == ViewKind.Back);
        Actions.Register("view.left", "Left", () => Viewport.SetView(ViewKind.Left), isChecked: () => Viewport.CameraController.Kind == ViewKind.Left);
        Actions.Register("view.bottom", "Bottom", () => Viewport.SetView(ViewKind.Bottom), isChecked: () => Viewport.CameraController.Kind == ViewKind.Bottom);
        Actions.Register("view.toggleProjection", "Perspective / Orthographic", () => { Viewport.CameraController.ToggleOrtho(); Viewport.Hud.Refresh(); }, isChecked: () => Viewport.CameraController.IsOrtho);
        Actions.Register("view.toggleLayout", "Single / Four Panes", () => { Layout.Toggle(); Settings.QuadView = Layout.IsQuad; }, isChecked: () => Layout.IsQuad);
        Actions.Register("view.maximize", "Maximize Viewport", ToggleMaximizeViewport, isChecked: () => _maximized);

        // 활성 뷰포트 셰이딩 모드. Wireframe on Shaded/Grid는 모든 패널에 같이 적용하고 설정에 저장한다.
        // --- 표시
        Actions.Register("display.wireframe", "Wireframe", () => Viewport.Display.SetMode(ShadingMode.Wireframe), isChecked: () => Viewport.Display.Mode == ShadingMode.Wireframe);
        Actions.Register("display.shaded", "Smooth Shade All", () => Viewport.Display.SetMode(ShadingMode.Shaded), isChecked: () => Viewport.Display.Mode == ShadingMode.Shaded);
        Actions.Register("display.textured", "Smooth Shade + Textured", () => Viewport.Display.SetMode(ShadingMode.Textured), isChecked: () => Viewport.Display.Mode == ShadingMode.Textured);
        Actions.Register("display.lit", "Use All Lights", () => Viewport.Display.SetMode(ShadingMode.Lit), isChecked: () => Viewport.Display.Mode == ShadingMode.Lit);
        Actions.Register("display.uvGrid", "UV Grid", () => Viewport.Display.SetMode(ShadingMode.UvGrid), isChecked: () => Viewport.Display.Mode == ShadingMode.UvGrid);
        Actions.Register("display.wireOnShaded", "Wireframe on Shaded", () => { bool on = !Viewport.Display.WireOnShaded; Settings.WireOnShaded = on; foreach (var p in Layout.Panels) { p.Display.WireOnShaded = on; p.Display.RefreshAll(); } }, isChecked: () => Viewport.Display.WireOnShaded);
        Actions.Register("display.grid", "Grid", () => { bool on = !Viewport.Display.ShowGrid; Settings.ShowGrid = on; foreach (var p in Layout.Panels) p.Display.ShowGrid = on; }, isChecked: () => Viewport.Display.ShowGrid);
        Actions.Register("display.background", "Background Color", () => { Viewport.CycleBackground(); });
        // Hide/Show(Maya Ctrl+H / Shift+H / Show All): 숨긴 노드는 자손과 함께 보이지 않고 피킹·프레임에서 빠진다(Undo 가능)
        Actions.Register("display.hideSelection", "Hide Selection", () => SetVisibility(sel.Objects, false), canExecute: () => sel.Mode == SelectMode.Object && sel.Objects.Any(id => doc.Find(id)?.Visible == true), repeatable: true); // 오브젝트 모드만(컴포넌트 숨기기는 미지원)
        Actions.Register("display.showSelection", "Show Selection", () => SetVisibility(sel.Objects, true), canExecute: () => sel.Objects.Any(id => doc.Find(id)?.Visible == false));
        Actions.Register("display.showAll", "Show All", () => SetVisibility(doc.Nodes.Values.Where(n => !n.IsRoot).Select(n => n.Id), true), canExecute: () => doc.Nodes.Values.Any(n => !n.IsRoot && !n.Visible));
        Actions.Register("display.polyCount", "Poly Count (HUD)", () => { Settings.ShowPolyCount = !Settings.ShowPolyCount; Settings.Save(); }, isChecked: () => Settings.ShowPolyCount);

        // Outliner/Properties 패널 열기/닫기 토글, About(버전 정보를 헬프 라인에)
        // --- 창
        Actions.Register("windows.outliner", "Outliner", () => TogglePanel(OutlinerWindow), isChecked: () => OutlinerWindow.IsOpen);
        Actions.Register("windows.properties", "Properties", () => TogglePanel(PropertiesWindow), isChecked: () => PropertiesWindow.IsOpen);
        Actions.Register("help.about", "About Cube", () => HelpLine.Text = $"Cube {ProjectSettings.GetSetting("application/config/version")} — Godot {Engine.GetVersionInfo()["string"]}");

        // Esc: 열린 파이 메뉴를 모두 닫고, Edit Pivot 중이면 이전 툴(없으면 Select)로 돌아가며,
        // 현재 툴의 진행 중 동작을 취소하고 뷰포트에 포커스를 돌려준다.
        Actions.Register("app.escape", "Escape", () =>
        {
            foreach (var p in Layout.Panels) p.Pie.Close();
            if (Tools.Current?.Id == "editPivot") Tools.SetTool(Tools.Previous != null && Tools.Previous.Id != "editPivot" ? Tools.Previous.Id : "select");
            Tools.CancelCurrent(); Viewport.GrabFocus();
        });
        // 영역별 액션 등록(각 partial 파일). 순서는 메뉴/셸프가 참조하기 전이기만 하면 된다.
        RegisterUvActions();
        RegisterRigActions();
        RegisterSceneActions();
        RegisterAnimActions();
        RegisterLogActions();
        RegisterComponentEditorActions();
        RegisterMeshActions();
        RegisterRenderActions();
        RegisterBridgeActions();
    }

    /// <summary>마지막으로 쓴 컴포넌트 모드(기본 Vertex). mode.toggle이 오브젝트 → 컴포넌트로 갈 때 이 모드로 간다.</summary>
    private SelectMode _lastComponentMode = SelectMode.Vertex;

    /// <summary>컴포넌트 선택 모드로 전환하고 그 모드를 _lastComponentMode로 기억한다.</summary>
    private void SetComponentMode(SelectMode mode)
    {
        _lastComponentMode = mode;
        var sel = Document.Selection;
        // Maya: 오브젝트가 선택된 상태에서 컴포넌트 모드로 가면 그 오브젝트의 컴포넌트를 편집한다(선택은 비움)
        sel.Mode = mode;
    }

    /// <summary>선택 변경을 Undo 가능하게 기록한다.</summary>
    /// <remarks>
    /// SelectionCommand.Record가 변경 전 스냅샷을 찍고 change를 적용한 뒤 변경 후 스냅샷을 비교한다.
    /// 실제로 바뀐 것이 없으면(IsNoop) Undo 스택에 넣지 않는다. 이미 적용된 상태라 alreadyApplied: true로 넣는다.
    /// </remarks>
    public void RecordSelection(Action<SelectionState> change)
    {
        var cmd = SelectionCommand.Record(Document, change);
        if (!cmd.IsNoop) Document.Undo.Push(cmd, alreadyApplied: true);
    }

    /// <summary>
    /// Grow/Shrink Selection: 현재 모드의 컴포넌트가 있는 노드마다 SelectionOps.Grow/Shrink로
    /// 선택 집합을 제자리에서 한 고리 넓히거나 좁힌다(Undo 가능). 끝나면 뷰포트 표시를 갱신한다.
    /// </summary>
    private void GrowShrink(bool grow)
    {
        var sel = Document.Selection;
        var mode = sel.Mode;
        RecordSelection(s =>
        {
            foreach (var id in s.NodesWithComponents(mode).ToArray())
            {
                var mesh = Document.Find(id)?.Mesh; if (mesh == null) continue;
                var set = s.GetComponents(id).Get(mode);
                // UV 모드는 UV 점 이웃으로(예전에는 SelectionOps.Grow가 UV를 다루지 않아 아무 일도 없었다)
                if (mode == SelectMode.Uv) { var topo = UvTopology.Build(mesh); if (grow) SelectionOps.GrowUv(mesh, topo, set); else SelectionOps.ShrinkUv(mesh, topo, set); }
                else if (grow) SelectionOps.Grow(mesh, mode, set); else SelectionOps.Shrink(mesh, mode, set);
            }
        });
        Viewport.Display.RefreshAll();
        UvEditorWindow?.Canvas.QueueRedraw();
    }

    /// <summary>UV 모드 선택은 정점 집합으로 바꾼 ComponentSet을 돌려준다(그 외 모드는 그대로).</summary>
    /// <remarks>
    /// SelectionOps.Convert는 UV 점을 모르므로 UV 점 ID를 UvTopology로 원래 정점 ID로 바꿔 Vertex 모드로 넘긴다.
    /// 범위를 벗어난 UV 점 ID(토폴로지가 바뀐 뒤 남은 옛 ID)는 건너뛴다.
    /// </remarks>
    private static (ComponentSet comps, SelectMode from) NormalizeForConvert(PolyMesh mesh, ComponentSet comps, SelectMode from)
    {
        if (from != SelectMode.Uv) return (comps, from);
        var topo = UvTopology.Build(mesh);
        var c = new ComponentSet();
        foreach (int p in comps.Uvs) if (p < topo.Points.Count) c.Verts.Add(topo.Points[p].Vertex);
        return (c, SelectMode.Vertex);
    }

    /// <summary>
    /// Convert Selection(To Vertices/Edges/Faces): 현재 모드의 선택을 노드별로 대상 모드 컴포넌트로 변환하고 모드를 바꾼다.
    /// 같은 모드이거나 오브젝트 모드에서는 모드만 바꾼다. 첫 노드는 replace, 이후 노드는 추가로 선택한다.
    /// </summary>
    private void ConvertSelection(SelectMode to)
    {
        var sel = Document.Selection;
        var from = sel.Mode;
        if (from == to || from == SelectMode.Object) { sel.Mode = to; return; }
        RecordSelection(s =>
        {
            // 모드를 바꾸기 전에 모든 노드의 변환 결과를 먼저 계산한다(모드 변경이 컴포넌트 선택을 비울 수 있으므로)
            var converted = new Dictionary<NodeId, HashSet<int>>();
            foreach (var id in s.NodesWithComponents(from).ToArray())
            {
                var mesh = Document.Find(id)?.Mesh; if (mesh == null) continue;
                var (comps, f) = NormalizeForConvert(mesh, s.GetComponents(id), from);
                converted[id] = SelectionOps.Convert(mesh, comps, f, to);
            }
            s.Mode = to;
            bool first = true;
            foreach (var (id, set) in converted) { s.SelectComponents(id, to, set, replace: first); first = false; }
        });
    }

    /// <summary>To Boundary Edge: 정점/엣지는 포함 면으로, 면은 그대로 → 면 집합의 바깥 경계 엣지. 엣지 모드로 전환.</summary>
    private void ConvertToBoundaryEdges()
    {
        var sel = Document.Selection;
        var from = sel.Mode;
        if (from == SelectMode.Object) { sel.Mode = SelectMode.Edge; return; }
        // 면 집합을 구한 뒤 그 바깥 경계 엣지만 남긴다
        RecordSelection(s =>
        {
            var converted = new Dictionary<NodeId, HashSet<int>>();
            foreach (var id in s.NodesWithComponents(from).ToArray())
            {
                var mesh = Document.Find(id)?.Mesh; if (mesh == null) continue;
                var (comps, f) = NormalizeForConvert(mesh, s.GetComponents(id), from);
                var faces = f == SelectMode.Face ? comps.Faces : SelectionOps.Convert(mesh, comps, f, SelectMode.Face);
                converted[id] = SelectionOps.BoundaryEdgesOfFaces(mesh, faces);
            }
            s.Mode = SelectMode.Edge;
            bool first = true;
            foreach (var (id, set) in converted) { s.SelectComponents(id, SelectMode.Edge, set, replace: first); first = false; }
        });
    }

    /// <summary>To UV / To UV Island: 선택을 정점으로 바꾼 뒤 그 정점의 UV 점(섬이면 그 점이 속한 셸 전체)을 선택. UV 모드로 전환.</summary>
    private void ConvertToUv(bool island)
    {
        var sel = Document.Selection;
        var from = sel.Mode;
        if (from == SelectMode.Object) { sel.Mode = SelectMode.Uv; return; }
        RecordSelection(s =>
        {
            var converted = new Dictionary<NodeId, HashSet<int>>();
            foreach (var id in s.NodesWithComponents(from).ToArray())
            {
                var mesh = Document.Find(id)?.Mesh; if (mesh == null) continue;
                // 노드마다 UV 토폴로지를 만들어 선택 → UV 점 집합으로 바꾼다(UV 모드면 유효한 점만 그대로)
                var topo = UvTopology.Build(mesh);
                var comps = s.GetComponents(id);
                var points = new HashSet<int>();
                if (from == SelectMode.Uv) points.UnionWith(comps.Uvs.Where(p => p < topo.Points.Count));
                else
                {
                    var verts = from == SelectMode.Vertex ? comps.Verts : SelectionOps.Convert(mesh, comps, from, SelectMode.Vertex);
                    for (int p = 0; p < topo.Points.Count; p++) if (verts.Contains(topo.Points[p].Vertex)) points.Add(p);
                }
                // 섬(셸) 확장: 고른 UV 점이 속한 셸의 모든 점을 더한다
                if (island)
                {
                    var shells = new HashSet<int>(points.Select(p => topo.Points[p].Shell));
                    foreach (int sh in shells) points.UnionWith(topo.PointsInShell(sh));
                }
                converted[id] = points;
            }
            s.Mode = SelectMode.Uv;
            bool first = true;
            foreach (var (id, set) in converted) { s.SelectComponents(id, SelectMode.Uv, set, replace: first); first = false; }
        });
        UvEditorWindow?.Canvas.QueueRedraw();
    }

    /// <summary>
    /// Select All. 오브젝트 모드 = 모든 노드. 컴포넌트 모드(Maya와 같이) = 편집 대상 개체의 현재 모드 컴포넌트 전부(UV 모드 = 모든 UV 점).
    /// 컴포넌트 모드인데 편집 대상이 없으면 오브젝트 모드로 바꿔 모든 노드를 선택한다.
    /// </summary>
    /// <remarks>예전에는 컴포넌트 모드에서도 오브젝트 모드로 빠져 모든 노드를 선택했다(UV 편집기 파이의 Select All도 같은 액션).</remarks>
    private void SelectAll()
    {
        var doc = Document; var sel = doc.Selection;
        var target = doc.Find(sel.ComponentTarget);
        if (sel.IsComponentMode && target?.Mesh != null)
        {
            var mesh = target.Mesh; var mode = sel.Mode;
            IEnumerable<int> ids = mode switch
            {
                SelectMode.Vertex => Enumerable.Range(0, mesh.VertexCount).Where(v => mesh.Verts[v].Alive),
                SelectMode.Edge => Enumerable.Range(0, mesh.EdgeCount).Where(e => mesh.Edges[e].Alive),
                SelectMode.Face => Enumerable.Range(0, mesh.FaceCount).Where(f => mesh.Faces[f].Alive),
                _ => Enumerable.Range(0, UvTopology.Build(mesh).Points.Count),
            };
            var list = ids.ToList();
            RecordSelection(s => s.SelectComponents(target.Id, mode, list));
            UvEditorWindow?.Canvas.QueueRedraw();
            return;
        }
        RecordSelection(s => { s.Mode = SelectMode.Object; s.SelectObjects(doc.Nodes.Values.Where(n => !n.IsRoot).Select(n => n.Id)); });
    }

    /// <summary>노드들의 가시성을 바꾸는 SetVisibilityCommand를 넣는다(바뀌는 노드가 없으면 아무것도 안 함).</summary>
    private void SetVisibility(IEnumerable<NodeId> ids, bool visible)
    {
        var cmd = new SetVisibilityCommand(Document, ids.ToArray(), visible);
        if (cmd.IsEmpty) return;
        Document.Undo.Push(cmd);
        HelpLine.Text = visible ? "Show: objects shown." : "Hide: objects hidden (Shift+H or Display > Show All to show again).";
    }

    /// <summary>Select Hierarchy: 선택 오브젝트의 모든 자손을 선택에 더한다.</summary>
    private void SelectHierarchy()
    {
        RecordSelection(s =>
        {
            var ids = new List<NodeId>();
            foreach (var id in s.Objects)
            {
                var n = Document.Find(id); if (n == null) continue;
                ids.Add(id);
                foreach (var d in n.Descendants()) ids.Add(d.Id);
            }
            s.Mode = SelectMode.Object;
            s.SelectObjects(ids.Distinct());
        });
    }

    /// <summary>Center Pivot: 메시 바운딩 박스 중심(오브젝트 공간)으로 피벗을 옮긴다(월드는 그대로).</summary>
    /// <remarks>
    /// 선택 오브젝트마다 살아 있는 정점의 AABB를 구해 그 중심을 새 피벗으로 하고,
    /// Transform3.WithPivotKeepingMatrix로 월드 행렬이 바뀌지 않게 Translate를 보정한다.
    /// 바뀐 노드만 모아 TransformNodesCommand 하나로 Undo에 넣는다.
    /// </remarks>
    private void CenterPivot()
    {
        var ids = new List<NodeId>(); var before = new List<Transform3>(); var after = new List<Transform3>();
        foreach (var id in Document.Selection.Objects)
        {
            var n = Document.Find(id); if (n?.Mesh == null) continue;
            var mn = new System.Numerics.Vector3(float.MaxValue); var mx = new System.Numerics.Vector3(float.MinValue);
            int cnt = 0;
            foreach (var v in n.Mesh.Verts) if (v.Alive) { mn = System.Numerics.Vector3.Min(mn, v.Position); mx = System.Numerics.Vector3.Max(mx, v.Position); cnt++; }
            if (cnt == 0) continue;
            var t = n.Local.WithPivotKeepingMatrix((mn + mx) * 0.5f);
            if (t == n.Local) continue;
            ids.Add(id); before.Add(n.Local); after.Add(t);
        }
        if (ids.Count == 0) return;
        Document.Undo.Push(new TransformNodesCommand("Center Pivot", ids.ToArray(), before.ToArray(), after.ToArray()));
        HelpLine.Text = $"Center Pivot: {ids.Count} object(s).";
    }

    /// <summary>Insert: Edit Pivot 모드 토글. 오브젝트 모드로 바꾸고 Move 조작기로 피벗만 옮긴다. 다시 누르면 이전 툴로.</summary>
    private void ToggleEditPivot()
    {
        if (Tools.Current?.Id == "editPivot") { Tools.SetTool(Tools.Previous != null && Tools.Previous.Id != "editPivot" ? Tools.Previous.Id : "move"); return; }
        if (Document.Selection.Mode != SelectMode.Object) Document.Selection.Mode = SelectMode.Object;
        Tools.SetTool("editPivot");
    }

    /// <summary>선택 오브젝트에 머티리얼 할당(0 = lambert1). Material Editor와 Assign Material 파이가 쓴다.</summary>
    public void AssignMaterialToSelection(int id)
    {
        var ids = Document.Selection.Objects.Where(x => Document.Find(x)?.Mesh != null).ToArray();
        if (ids.Length == 0) { HelpLine.Text = "Assign Material: select an object first."; return; }
        Document.Undo.Push(new AssignMaterialCommand(ids, id));
        HelpLine.Text = $"Assigned {(id == 0 ? "lambert1" : Document.FindMaterial(id)?.Name)} to {ids.Length} object(s).";
    }

    /// <summary>
    /// Delete: 오브젝트 모드면 선택 노드를 삭제(DeleteNodesCommand), 컴포넌트 모드면 mesh.deleteComponents에 위임한다.
    /// </summary>
    private void DeleteSelection()
    {
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Object)
        {
            var cmd = new DeleteNodesCommand(Document, sel.Objects);
            if (!cmd.IsEmpty) Document.Undo.Push(cmd);
        }
        // UV 모드의 Delete는 메시 컴포넌트가 아니라 선택 UV가 덮는 면의 UV를 지운다(Maya UV Editor Edit > Delete).
        // 전에는 정점 삭제 명령이 UV 점 ID를 받아 아무것도 지우지 않은 채 선택만 비우는 빈 Undo 단계를 남겼다.
        else if (sel.Mode == SelectMode.Uv) Actions.Invoke("uv.deleteUvs");
        else Actions.Invoke("mesh.deleteComponents");
    }

    // ---------------------------------------------------------------- 폴리 편집

    /// <summary>현재 모드의 컴포넌트가 있는 노드마다 명령을 만들어 한 Undo 스텝으로 실행한다.</summary>
    private void ForEachComponentNode(string groupName, SelectMode mode, Func<NodeId, ComponentSet, ICommand?> make)
    {
        var doc = Document;
        // 대상 노드 목록을 먼저 고정한다(명령 실행 중 선택이 바뀌어도 순회가 깨지지 않도록).
        // 컴포넌트 집합은 Clone해서 넘긴다 — 명령이 위상을 바꾸며 선택을 비웠다 복원하기 때문.
        var targets = doc.Selection.NodesWithComponents(mode).ToArray();
        if (targets.Length == 0) return;
        using (doc.Undo.BeginGroup(groupName))
        {
            foreach (var id in targets)
            {
                var cmd = make(id, doc.Selection.GetComponents(id).Clone());
                if (cmd != null) doc.Undo.Push(cmd);
            }
        }
    }

    /// <summary>현재 모드의 선택 컴포넌트(정점/엣지/면)를 노드별 DeleteComponentsCommand로 삭제한다(한 Undo 그룹).</summary>
    private void DeleteComponents()
    {
        var mode = Document.Selection.Mode;
        ForEachComponentNode("Delete", mode, (id, comps) => new DeleteComponentsCommand(id, mode, comps.Get(mode)));
    }

    /// <summary>1/2/3 키: 선택 오브젝트의 Smooth Mesh Preview(표시 전용, 케이지는 그대로 편집).</summary>
    /// <remarks>
    /// 대상 = 선택 오브젝트 + 현재 모드 컴포넌트가 있는 노드. MeshShape.SmoothPreview만 바꾸고
    /// DisplayChanged를 알려 MeshView가 표면을 다시 만들게 한다. 문서 데이터 변경이 아니므로 Undo에 넣지 않는다.
    /// </remarks>
    private void SetSmoothPreview(int level)
    {
        var sel = Document.Selection;
        var ids = new HashSet<NodeId>(sel.Objects);
        foreach (var id in sel.NodesWithComponents(sel.Mode)) ids.Add(id);
        foreach (var id in ids)
        {
            var shape = Document.Find(id)?.MeshShape; if (shape == null) continue;
            shape.SmoothPreview = level;
            Document.Notify(new DocChange(ChangeKind.DisplayChanged, id));
        }
        HelpLine.Text = level switch { 0 => "Smooth Mesh Preview off (cage).", 1 => "Smooth Mesh Preview: cage + smooth.", _ => "Smooth Mesh Preview: smooth." };
    }

    /// <summary>
    /// Bridge: 선택한 경계 엣지 체인 두 개를 쿼드로 잇는다(MeshOps.BridgeEdges). 결과 면을 면 모드로 선택하고,
    /// 만든 면 수(made, 람다가 누적)를 헬프 라인에 알린다. 실패하면 사용법 안내를 띄운다.
    /// </summary>
    private void BridgeSelection()
    {
        int made = 0;
        ForEachComponentNode("Bridge", SelectMode.Edge, (id, comps) =>
        {
            var edges = comps.Edges.ToArray();
            return new MeshOpCommand("Bridge", id, m => { var faces = MeshOps.BridgeEdges(m, edges); made += faces.Count; return (faces.Count > 0, SelectMode.Face, faces); });
        });
        HelpLine.Text = made > 0 ? $"Bridge: {made} faces created." : "Bridge: select two border edge chains with the same number of edges.";
    }

    /// <summary>Combine: 선택한 메시 오브젝트 2개 이상을 하나의 메시로 합친다(CombineCommand).</summary>
    private void CombineSelection()
    {
        var ids = Document.Selection.Objects.Where(id => Document.Find(id)?.Mesh != null).ToArray();
        if (ids.Length < 2) return;
        Document.Undo.Push(new CombineCommand(ids));
    }

    /// <summary>
    /// Separate: 활성 오브젝트 메시를 연결 요소별 노드로 나눈다. Prepare가 false면(조각이 하나) 안내만 한다.
    /// </summary>
    private void SeparateSelection()
    {
        var id = Document.Selection.ActiveObject;
        var cmd = new SeparateCommand(id);
        if (!cmd.Prepare(Document)) { HelpLine.Text = "Separate: the mesh has only one piece."; return; }
        Document.Undo.Push(cmd);
    }

    /// <summary>Harden/Soften Edge 실행 가능 여부: 오브젝트 모드면 메시 오브젝트 선택, 컴포넌트 모드면 해당 모드 선택이 있어야 한다.</summary>
    private bool HasEdgeTargets()
    {
        var sel = Document.Selection;
        return sel.Mode == SelectMode.Object ? sel.Objects.Any(id => Document.Find(id)?.Mesh != null) : sel.NodesWithComponents(sel.Mode).Any();
    }

    /// <summary>
    /// Harden/Soften Edge: 오브젝트 모드면 각 메시의 살아 있는 모든 엣지, 컴포넌트 모드면 선택을 엣지로 변환한 집합에
    /// SetEdgesHardCommand를 적용한다(노드별 명령, 한 Undo 그룹).
    /// </summary>
    private void SetEdgesHard(bool hard)
    {
        var doc = Document; var sel = doc.Selection;
        string name = hard ? "Harden Edge" : "Soften Edge";
        if (sel.Mode == SelectMode.Object)
        {
            using (doc.Undo.BeginGroup(name))
                foreach (var id in sel.Objects.ToArray())
                {
                    var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
                    doc.Undo.Push(new SetEdgesHardCommand(id, Enumerable.Range(0, mesh.EdgeCount).Where(e => mesh.Edges[e].Alive), hard));
                }
            return;
        }
        var mode = sel.Mode;
        ForEachComponentNode(name, mode, (id, comps) =>
        {
            var mesh = doc.Find(id)?.Mesh; if (mesh == null) return null;
            var edges = mode == SelectMode.Edge ? comps.Edges : SelectionOps.Convert(mesh, comps, mode, SelectMode.Edge);
            return new SetEdgesHardCommand(id, edges, hard);
        });
    }

    /// <summary>
    /// Reverse(면 방향 뒤집기): 오브젝트 모드면 각 메시의 모든 면, 면 모드면 선택 면에 ReverseFacesCommand를 적용한다.
    /// </summary>
    private void ReverseSelection()
    {
        var doc = Document; var sel = doc.Selection;
        if (sel.Mode == SelectMode.Object)
        {
            using (doc.Undo.BeginGroup("Reverse"))
                foreach (var id in sel.Objects.ToArray())
                {
                    var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
                    doc.Undo.Push(new ReverseFacesCommand(id, Enumerable.Range(0, mesh.FaceCount).Where(f => mesh.Faces[f].Alive)));
                }
            return;
        }
        ForEachComponentNode("Reverse", SelectMode.Face, (id, comps) => new ReverseFacesCommand(id, comps.Faces));
    }

    /// <summary>
    /// Duplicate(Maya 기본): 선택 오브젝트마다 자식 계층·셰이프(메시/조인트/라이트)·머티리얼·피벗·가시성을 복제한 트리를
    /// 같은 부모 아래에 추가하고 만든 최상위 노드들을 선택한다(NodeDuplicate.CloneTree). 선택된 조상이 있는 노드는 조상과 함께 복제되므로 건너뛴다.
    /// 메시는 구성 이력·스킨 없이 복제된다. 한 Undo 그룹.
    /// </summary>
    /// <remarks>예전에는 메시만 복제해(자식·머티리얼·조인트/라이트 셰이프 누락) 조인트나 라이트를 복제하면 빈 노드가 생겼다.</remarks>
    private void DuplicateSelection()
    {
        var doc = Document;
        var ids = doc.Selection.Objects.ToArray();
        if (ids.Length == 0) return;
        var set = new HashSet<NodeId>(ids);
        var names = new HashSet<string>();
        using (doc.Undo.BeginGroup("Duplicate"))
        {
            var created = new List<NodeId>();
            foreach (var id in ids)
            {
                var src = doc.Find(id); if (src == null) continue;
                bool ancestorSel = false;
                for (var p = src.Parent; p != null && !p.IsRoot; p = p.Parent) if (set.Contains(p.Id)) { ancestorSel = true; break; }
                if (ancestorSel) continue;
                var copy = NodeDuplicate.CloneTree(doc, src, names);
                doc.Undo.Push(new AddNodeCommand("Duplicate", copy, src.Parent != null && !src.Parent.IsRoot ? src.Parent.Id : NodeId.None));
                created.Add(copy.Id);
            }
            RecordSelection(s => { s.Mode = SelectMode.Object; s.SelectObjects(created); });
        }
    }

    /// <summary>창 제목을 현재 씬 파일 이름(+ 변경 표시)으로 갱신한다.</summary>
    public void UpdateTitle() => DisplayServer.WindowSetTitle(SceneFiles.Title);

    /// <summary>Preferences 창(한 번 만들어 재사용; 셸 재생성 등으로 해제되었으면 다시 만든다).</summary>
    private PreferencesDialog? _prefs;

    /// <summary>Edit → Preferences 창을 화면 가운데에 띄운다.</summary>
    private void ShowPreferences()
    {
        if (_prefs == null || !GodotObject.IsInstanceValid(_prefs))
        {
            _prefs = new PreferencesDialog();
            AddChild(_prefs);
        }
        _prefs.PopupCentered();
    }

    /// <summary>환경설정 변경을 상태 라인 위젯에 반영.</summary>
    public void SyncStatusLine()
    {
        _cameraBased.SetPressedNoSignal(Settings.CameraBasedSelection);
        // 스냅 버튼은 토글 설정 또는 홀드 키(X = 그리드, V = 점)가 눌려 있으면 눌림 표시
        _snapGrid.SetPressedNoSignal(Settings.SnapToGrid || Hotkeys.HeldKeys.Contains(Key.X));
        _snapPoint.SetPressedNoSignal(Settings.SnapToPoints || Hotkeys.HeldKeys.Contains(Key.V));
    }

    /// <summary>File → Open Recent 하위 메뉴. 파일 메뉴가 열릴 때마다 RefreshRecentMenu로 다시 채운다.</summary>
    private PopupMenu? _recentMenu;

    /// <summary>최근 파일 목록 중 아직 존재하는 파일만 하위 메뉴에 넣는다(없으면 비활성 "(empty)").</summary>
    private void RefreshRecentMenu()
    {
        if (_recentMenu == null) return;
        _recentMenu.Clear();
        int i = 0;
        foreach (var p in Settings.RecentFiles.Where(System.IO.File.Exists))
        {
            _recentMenu.AddItem(p, i++);
        }
        if (_recentMenu.ItemCount == 0) { _recentMenu.AddItem("(empty)", 0); _recentMenu.SetItemDisabled(0, true); }
    }

    // ---------------------------------------------------------------- 메뉴

    /// <summary>
    /// 상단 메뉴바를 Maya 2027 구성에 맞춰 만든다: File / Edit / Create / Select / Mesh / Edit Mesh / Mesh Tools / Mesh Display /
    /// UV / Skeleton / Skin / Display / Animation / Render / Bridge / Windows / Help.
    /// 항목은 액션 ID로만 넣고(라벨·단축키·체크는 MenuBuilder가 액션에서 가져옴), 옵션 명령은 Op(id)로 실행 + Options... 두 항목을 넣는다.
    /// 같은 기능을 여러 메뉴에 중복해 넣지 않는다(CLAUDE.md 메뉴 중복 금지 규칙).
    /// </summary>
    private void BuildMenus()
    {
        // 메뉴바에 PopupMenu를 하나 추가하고 제목을 붙여 돌려준다(MenuBar는 자식 순서로 메뉴 인덱스를 매김)
        PopupMenu Add(string title)
        {
            var pm = new PopupMenu { Name = title.Replace(" ", "") };
            MenuBar.AddChild(pm);
            MenuBar.SetMenuTitle(MenuBar.GetChildCount() - 1, title);
            return pm;
        }

        // File 메뉴: New/Open → Open Recent 하위 메뉴(직접 만든 PopupMenu) → 저장/가져오기/내보내기/종료.
        // MenuBuilder.Build를 같은 팝업에 두 번 불러 항목을 이어 붙인다(클릭 핸들러는 팝업당 한 번만 연결됨).
        var fileMenu = Add("File");
        Menus.Build(fileMenu)
            .Item("file.new").Item("file.open");
        _recentMenu = new PopupMenu { Name = "OpenRecent" };
        fileMenu.AddChild(_recentMenu);
        fileMenu.AddSubmenuNodeItem("Open Recent", _recentMenu);
        _recentMenu.IdPressed += id => { var p = _recentMenu.GetItemText((int)id); if (System.IO.File.Exists(p)) SceneFiles.OpenRecentWithConfirm(p); };
        fileMenu.AboutToPopup += RefreshRecentMenu;
        Menus.Build(fileMenu)
            .Separator().Item("file.save").Item("file.saveAs").Separator()
            .Item("file.import").Item("file.exportSelection").Item("file.exportAll").Separator().Item("file.exit");

        // Maya 규약: "X"는 마지막 옵션으로 바로 실행(<id>Apply), "X Options..."는 옵션 창(<id>). Menu.Op가 둘을 함께 넣는다.
        Menus.Build(Add("Edit"))
            .Item("edit.undo").Item("edit.redo").Item("edit.repeatLast").Separator()
            .Item("edit.delete").Item("edit.duplicate").Separator().Item("edit.deleteHistory").Separator()
            .Item("edit.centerPivot").Item("edit.editPivot").Separator()
            .Item("edit.componentEditor").Separator()
            .Item("select.all").Item("select.none").Item("select.hierarchy").Separator()
            .Item("snap.grid").Item("snap.point").Separator()
            .Item("edit.preferences");

        Menus.Build(Add("Create"))
            .Submenu("Polygon Primitives", m => m.Item("create.cube", "Cube").Item("create.sphere", "Sphere").Item("create.cylinder", "Cylinder").Item("create.cone", "Cone").Item("create.plane", "Plane").Item("create.torus", "Torus"))
            .Submenu("Lights", m => m.Item("create.lightDirectional", "Directional Light").Item("create.lightPoint", "Point Light").Item("create.lightSpot", "Spot Light"));

        Menus.Build(Add("Select"))
            .Item("tool.select").Item("tool.lasso").Separator()
            .Item("mode.object").Item("mode.vertex").Item("mode.edge").Item("mode.face").Item("mode.uv").Separator()
            .Item("select.grow").Item("select.shrink").Separator()
            .Item("select.lights").Separator()
            .Submenu("Convert Selection", m => m.Item("select.toVertices").Item("select.toEdges").Item("select.toFaces").Item("select.toBoundaryEdges").Separator().Item("select.toUv").Item("select.toUvIsland"));

        Menus.Build(Add("Mesh"))
            .Item("mesh.combine").Item("mesh.separate").Submenu("Booleans", m => m.Op("mesh.booleanUnion").Op("mesh.booleanDifference").Op("mesh.booleanIntersection")).Separator()
            .Item("mesh.conform").Item("mesh.fillHole").Op("mesh.smooth").Item("mesh.triangulate").Op("mesh.quadrangulate").Separator()
            .Op("mesh.mirror").Item("mesh.symmetrizeMesh").Op("mesh.array").Separator()
            .Item("mesh.cleanup");

        Menus.Build(Add("Edit Mesh"))
            .Op("mesh.addDivisions").Op("mesh.bevel").Item("mesh.bevelTool").Item("mesh.bevelVerticesTool").Item("mesh.bridge").Op("mesh.circularize").Item("mesh.collapse").Item("mesh.connect").Item("mesh.detach")
            .Op("mesh.extrude").Op("mesh.merge").Item("mesh.mergeToCenter").Op("mesh.flipComponents").Op("mesh.symmetrizeComponents").Separator()
            .Op("mesh.averageVertices").Op("mesh.chamferVertices").Separator()
            .Item("mesh.deleteComponents").Item("mesh.flipTriangleEdge").Item("mesh.spinEdgeBackward").Item("mesh.spinEdgeForward").Separator()
            .Item("mesh.duplicateFaces").Item("mesh.extractFaces").Op("mesh.poke").Op("mesh.wedge");

        Menus.Build(Add("Mesh Tools"))
            .Item("mesh.appendPolygon").Op("mesh.crease").Item("mesh.creaseTool").Item("mesh.uncrease").Item("create.polygonTool").Item("mesh.insertLoop").Item("mesh.multiCut")
            .Op("mesh.offsetEdgeLoop").Op("mesh.slideEdge").Item("mesh.targetWeld");

        Menus.Build(Add("Mesh Display"))
            .Item("normals.average").Item("normals.conform").Item("mesh.reverse", "Reverse").Item("normals.setToFace").Op("normals.setVertexNormal").Separator()
            .Item("mesh.harden", "Harden Edge").Item("mesh.soften", "Soften Edge").Op("normals.softenHardenAngle").Separator()
            .Item("normals.lock").Item("normals.unlock");

        Menus.Build(Add("UV"))
            .Item("windows.uvEditor").Item("uv.setEditor").Separator()
            .Op("uv.automatic").Item("uv.bestPlane").Item("uv.cameraBased").Item("uv.contourStretch").Item("uv.planarBest").Item("uv.planarX").Item("uv.planarY").Item("uv.planarZ").Item("uv.cylindrical").Item("uv.spherical").Separator()
            .Item("uv.autoSeams").Item("uv.autoWrap").Item("uv.createShell").Item("uv.createShellGrid").Separator()
            .Item("uv.unfold").Item("uv.optimize").Op("uv.layout").Op("uv.normalize").Op("uv.straighten").Separator().Item("uv.cut").Item("uv.sew").Item("uv.moveAndSew").Item("uv.cutSewTool").Separator().Item("uv.flipU").Item("uv.flipV");
        Menus.Build(Add("Skeleton")).Item("skeleton.jointTool").Item("skeleton.insertJointTool").Separator().Item("skeleton.mirror").Op("skeleton.orient");
        Menus.Build(Add("Skin")).Item("skin.bind").Item("skin.detach").Separator().Item("skin.paintTool").Item("skin.normalize").Item("skin.rebind");

        Menus.Build(Add("Display"))
            .Item("display.wireframe").Item("display.shaded").Item("display.textured").Item("display.lit").Item("display.uvGrid").Item("display.wireOnShaded").Separator()
            .Item("display.smoothPreviewOff").Item("display.smoothPreviewBoth").Item("display.smoothPreviewOn").Separator()
            .Item("display.joints").Item("display.jointSize").Item("display.jointAxes").Item("display.timeSlider").Separator()
            .Item("display.hideSelection").Item("display.showSelection").Item("display.showAll").Separator()
            .Item("display.grid").Item("display.polyCount").Item("display.background").Separator()
            .Submenu("View", m => m.Item("view.persp").Item("view.front").Item("view.side").Item("view.top").Item("view.back").Item("view.left").Item("view.bottom").Separator().Item("view.toggleProjection").Item("view.toggleLayout").Separator().Item("view.home").Item("view.frameSelected").Item("view.frameAll").Item("view.maximize"));

        Menus.Build(Add("Animation"))
            .Item("anim.playToggle").Item("anim.rest").Separator()
            .Item("anim.start").Item("anim.end").Item("anim.prevFrame").Item("anim.nextFrame").Item("anim.prevKey").Item("anim.nextKey").Separator()
            .Item("anim.loop").Item("anim.nextClip").Separator()
            .Item("windows.animationData");

        Menus.Build(Add("Render"))
            .Item("windows.renderSettings").Separator()
            .Item("render.ibl").Item("render.background").Item("render.nextHdri").Separator()
            .Item("render.headlight").Item("render.shadows").Separator()
            .Submenu("Post Effects", m => m.Item("render.ssao").Item("render.glow").Item("render.ssr").Item("render.ssil").Item("render.sdfgi").Separator()
                .Item("render.fog").Item("render.volumetricFog").Separator().Item("render.adjust").Item("render.dof").Item("render.autoExposure").Separator().Item("render.postReset"))
            .Submenu("Anti-aliasing", m => m.Item("render.fxaa").Item("render.smaa").Item("render.taa").Separator().Item("render.debanding"));

        Menus.Build(Add("Bridge"))
            .Item("bridge.blenderAll").Item("bridge.blenderSelected").Separator().Item("bridge.rizom").Item("bridge.marmoset").Item("bridge.cascadeur").Separator()
            .Submenu("Add-ons", m => m.Item("bridge.installBlenderAddon").Item("bridge.saveBlenderAddon").Separator().Item("bridge.openAddonsFolder"))
            .Submenu("Tripo3D", m => m.Item("bridge.tripo").Separator().Item("bridge.tripoImport").Item("bridge.tripoFolder")).Separator()
            .Item("bridge.reload").Item("bridge.autoReload").Item("bridge.openFolder").Separator()
            .Item("bridge.settings");

        Menus.Build(Add("Windows")).Item("windows.outliner").Item("windows.properties").Item("windows.uvEditor").Item("windows.materialEditor").Item("windows.renderSettings").Item("windows.animationData").Separator().Item("windows.log").Item("log.copy").Item("log.save").Item("log.clear");
        Menus.Build(Add("Help")).Item("help.about");
    }
}
