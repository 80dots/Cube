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

public partial class Shell
{
    private void RegisterTools()
    {
        Tools.Register(new SelectTool());
        Tools.Register(new MoveTool());
        Tools.Register(new RotateTool());
        Tools.Register(new ScaleTool());
        Tools.Register(new InsertEdgeLoopTool());
        Tools.Register(new JointTool());
        Tools.Register(new PaintWeightsTool());
        Tools.Register(new CreatePolygonTool());
        Tools.Register(new InsertJointTool());
    }

    private void RegisterActions()
    {
        var doc = Document;
        var sel = doc.Selection;

        // --- 툴
        foreach (var (id, label) in new[] { ("select", "Select Tool"), ("move", "Move Tool"), ("rotate", "Rotate Tool"), ("scale", "Scale Tool") })
        {
            string t = id;
            Actions.Register("tool." + id, label, () => Tools.SetTool(t), isChecked: () => Tools.Current?.Id == t);
        }
        Actions.Register("tool.last", "Last Tool", () => Tools.SwapToPrevious());
        Actions.Register("axis.world", "Axis: World", () => ToolContext.AxisOrientation = AxisOrientation.World, isChecked: () => ToolContext.AxisOrientation == AxisOrientation.World);
        Actions.Register("axis.local", "Axis: Local", () => ToolContext.AxisOrientation = AxisOrientation.Object, isChecked: () => ToolContext.AxisOrientation == AxisOrientation.Object);
        Actions.Register("axis.normal", "Axis: Normal", () => ToolContext.AxisOrientation = AxisOrientation.Normal, isChecked: () => ToolContext.AxisOrientation == AxisOrientation.Normal);

        // --- 선택 모드
        Actions.Register("mode.object", "Object Mode", () => sel.Mode = SelectMode.Object, isChecked: () => sel.Mode == SelectMode.Object);
        Actions.Register("mode.toggle", "Toggle Object/Component", () => sel.Mode = sel.Mode == SelectMode.Object ? _lastComponentMode : SelectMode.Object);
        Actions.Register("mode.vertex", "Vertex", () => SetComponentMode(SelectMode.Vertex), isChecked: () => sel.Mode == SelectMode.Vertex);
        Actions.Register("mode.edge", "Edge", () => SetComponentMode(SelectMode.Edge), isChecked: () => sel.Mode == SelectMode.Edge);
        Actions.Register("mode.face", "Face", () => SetComponentMode(SelectMode.Face), isChecked: () => sel.Mode == SelectMode.Face);
        Actions.Register("mode.uv", "UV", () => { SetComponentMode(SelectMode.Uv); UvEditorWindow?.Canvas.SetIslandMode(false); }, isChecked: () => sel.Mode == SelectMode.Uv && !(UvEditorWindow?.Canvas.IslandMode ?? false));
        Actions.Register("mode.uvIsland", "UV Island", () => { SetComponentMode(SelectMode.Uv); UvEditorWindow?.Canvas.SetIslandMode(true); }, isChecked: () => sel.Mode == SelectMode.Uv && (UvEditorWindow?.Canvas.IslandMode ?? false));

        Actions.Register("select.all", "Select All", () => RecordSelection(s => { s.Mode = SelectMode.Object; s.SelectObjects(doc.Nodes.Values.Where(n => !n.IsRoot).Select(n => n.Id)); }));
        Actions.Register("select.none", "Deselect All", () => RecordSelection(s => s.ClearAll()), canExecute: () => !sel.IsEmpty);
        Actions.Register("select.grow", "Grow Selection", () => GrowShrink(true), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.shrink", "Shrink Selection", () => GrowShrink(false), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toVertices", "To Vertices", () => ConvertSelection(SelectMode.Vertex), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toEdges", "To Edges", () => ConvertSelection(SelectMode.Edge), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toFaces", "To Faces", () => ConvertSelection(SelectMode.Face), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toBoundaryEdges", "To Boundary Edges", ConvertToBoundaryEdges, canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toUv", "To UV", () => ConvertToUv(island: false), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toUvIsland", "To UV Island", () => ConvertToUv(island: true), canExecute: () => sel.IsComponentMode);

        // --- 편집
        Actions.Register("edit.undo", "Undo", () => doc.Undo.Undo(), canExecute: () => doc.Undo.CanUndo);
        Actions.Register("edit.redo", "Redo", () => doc.Undo.Redo(), canExecute: () => doc.Undo.CanRedo);
        Actions.Register("edit.repeatLast", "Repeat Last", () => Actions.RepeatLast(), canExecute: () => Actions.LastRepeatable != null);
        Actions.Register("edit.delete", "Delete", DeleteSelection, canExecute: () => !sel.IsEmpty, repeatable: true);
        Actions.Register("edit.duplicate", "Duplicate", DuplicateSelection, canExecute: () => sel.Objects.Count > 0, repeatable: true);
        Actions.Register("edit.preferences", "Preferences...", ShowPreferences);
        Actions.Register("edit.deleteHistory", "Delete History", () => { var ids = sel.Objects.Where(id => doc.Find(id)?.MeshShape?.History.Count > 0).ToArray(); if (ids.Length > 0) doc.Undo.Push(new DeleteHistoryCommand(ids)); },
            canExecute: () => sel.Objects.Any(id => doc.Find(id)?.MeshShape?.History.Count > 0));
        Actions.Register("mesh.smooth", "Smooth...", ShowSmoothDialog, canExecute: () => sel.Mode == SelectMode.Object && sel.Objects.Any(id => doc.Find(id)?.Mesh != null));
        Actions.Register("mesh.smoothApply", "Smooth", SmoothSelection, canExecute: () => sel.Mode == SelectMode.Object && sel.Objects.Any(id => doc.Find(id)?.Mesh != null), repeatable: true);
        foreach (var (level, id, label) in new[] { (0, "display.smoothPreviewOff", "Smooth Mesh Preview: Cage (1)"), (1, "display.smoothPreviewBoth", "Smooth Mesh Preview: Cage + Smooth (2)"), (2, "display.smoothPreviewOn", "Smooth Mesh Preview: Smooth (3)") })
        {
            int lv = level;
            Actions.Register(id, label, () => SetSmoothPreview(lv), canExecute: () => sel.Objects.Any(x => doc.Find(x)?.Mesh != null) || sel.NodesWithComponents(sel.Mode).Any());
        }

        // --- 생성
        Actions.Register("create.cube", "Polygon Cube", () => doc.Undo.Push(CreatePrimitiveCommand.Cube(doc)), repeatable: true);
        Actions.Register("create.sphere", "Polygon Sphere", () => doc.Undo.Push(CreatePrimitiveCommand.Sphere(doc)), repeatable: true);
        Actions.Register("create.cylinder", "Polygon Cylinder", () => doc.Undo.Push(CreatePrimitiveCommand.Cylinder(doc)), repeatable: true);
        Actions.Register("create.cone", "Polygon Cone", () => doc.Undo.Push(CreatePrimitiveCommand.Cone(doc)), repeatable: true);
        Actions.Register("create.plane", "Polygon Plane", () => doc.Undo.Push(CreatePrimitiveCommand.Plane(doc)), repeatable: true);
        Actions.Register("create.torus", "Polygon Torus", () => doc.Undo.Push(CreatePrimitiveCommand.Torus(doc)), repeatable: true);

        // --- 메시 편집
        Actions.Register("mesh.extrude", "Extrude", ExtrudeSelection, canExecute: () => sel.Mode == SelectMode.Face && sel.NodesWithComponents(SelectMode.Face).Any(), repeatable: true);
        Actions.Register("mesh.deleteComponents", "Delete Edge/Vertex", DeleteComponents, canExecute: () => sel.IsComponentMode && sel.NodesWithComponents(sel.Mode).Any(), repeatable: true);
        Actions.Register("mesh.merge", "Merge Vertices...", ShowMergeDialog, canExecute: () => sel.IsComponentMode && sel.NodesWithComponents(sel.Mode).Any());
        Actions.Register("mesh.mergeApply", "Merge Vertices", MergeSelectedVertices, canExecute: () => sel.IsComponentMode && sel.NodesWithComponents(sel.Mode).Any(), repeatable: true);
        Actions.Register("mesh.combine", "Combine", CombineSelection, canExecute: () => sel.Mode == SelectMode.Object && sel.Objects.Count(id => doc.Find(id)?.Mesh != null) >= 2);
        Actions.Register("mesh.separate", "Separate", SeparateSelection, canExecute: () => sel.Mode == SelectMode.Object && sel.Objects.Count == 1);
        Actions.Register("mesh.soften", "Soften Edge", () => SetEdgesHard(false), canExecute: () => HasEdgeTargets(), repeatable: true);
        Actions.Register("mesh.harden", "Harden Edge", () => SetEdgesHard(true), canExecute: () => HasEdgeTargets(), repeatable: true);
        Actions.Register("mesh.reverse", "Reverse", ReverseSelection, canExecute: () => sel.Mode == SelectMode.Object ? sel.Objects.Count > 0 : sel.Mode == SelectMode.Face && sel.NodesWithComponents(SelectMode.Face).Any(), repeatable: true);
        bool HasBevelTargets() => sel.Mode is SelectMode.Edge or SelectMode.Face && sel.NodesWithComponents(sel.Mode).Any();
        Actions.Register("mesh.bevel", "Bevel...", ShowBevelDialog, canExecute: HasBevelTargets);
        Actions.Register("mesh.bevelApply", "Bevel", BevelSelection, canExecute: HasBevelTargets, repeatable: true);
        Actions.Register("mesh.bridge", "Bridge", BridgeSelection, canExecute: () => sel.Mode == SelectMode.Edge && sel.NodesWithComponents(SelectMode.Edge).Any(), repeatable: true);
        Actions.Register("mesh.insertLoop", "Insert Edge Loop Tool", () => Tools.SetTool("insertLoop"), isChecked: () => Tools.Current?.Id == "insertLoop");
        Actions.Register("mesh.multiCut", "Multi-Cut", () => { }, canExecute: () => false);

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
        Actions.Register("file.exit", "Exit", () => GetTree().Quit());

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

        // --- 표시
        Actions.Register("display.wireframe", "Wireframe", () => Viewport.Display.SetMode(ShadingMode.Wireframe), isChecked: () => Viewport.Display.Mode == ShadingMode.Wireframe);
        Actions.Register("display.shaded", "Smooth Shade All", () => Viewport.Display.SetMode(ShadingMode.Shaded), isChecked: () => Viewport.Display.Mode == ShadingMode.Shaded);
        Actions.Register("display.textured", "Smooth Shade + Textured", () => Viewport.Display.SetMode(ShadingMode.Textured), isChecked: () => Viewport.Display.Mode == ShadingMode.Textured);
        Actions.Register("display.lit", "Use All Lights", () => Viewport.Display.SetMode(ShadingMode.Lit), isChecked: () => Viewport.Display.Mode == ShadingMode.Lit);
        Actions.Register("display.uvGrid", "UV Grid", () => Viewport.Display.SetMode(ShadingMode.UvGrid), isChecked: () => Viewport.Display.Mode == ShadingMode.UvGrid);
        Actions.Register("display.wireOnShaded", "Wireframe on Shaded", () => { bool on = !Viewport.Display.WireOnShaded; Settings.WireOnShaded = on; foreach (var p in Layout.Panels) { p.Display.WireOnShaded = on; p.Display.RefreshAll(); } }, isChecked: () => Viewport.Display.WireOnShaded);
        Actions.Register("display.grid", "Grid", () => { bool on = !Viewport.Display.ShowGrid; Settings.ShowGrid = on; foreach (var p in Layout.Panels) p.Display.ShowGrid = on; }, isChecked: () => Viewport.Display.ShowGrid);
        Actions.Register("display.background", "Background Color", () => { Viewport.CycleBackground(); });

        // --- 창
        Actions.Register("windows.outliner", "Outliner", () => OutlinerDock.Visible = !OutlinerDock.Visible, isChecked: () => OutlinerDock.Visible);
        Actions.Register("windows.properties", "Properties", () => PropertiesDock.Visible = !PropertiesDock.Visible, isChecked: () => PropertiesDock.Visible);
        Actions.Register("help.about", "About Cube", () => HelpLine.Text = $"Cube {ProjectSettings.GetSetting("application/config/version")} — Godot {Engine.GetVersionInfo()["string"]}");

        Actions.Register("app.escape", "Escape", () => { Tools.CancelCurrent(); Viewport.GrabFocus(); });
        RegisterUvActions();
        RegisterRigActions();
        RegisterSceneActions();
    }

    private SelectMode _lastComponentMode = SelectMode.Vertex;

    private void SetComponentMode(SelectMode mode)
    {
        _lastComponentMode = mode;
        var sel = Document.Selection;
        // Maya: 오브젝트가 선택된 상태에서 컴포넌트 모드로 가면 그 오브젝트의 컴포넌트를 편집한다(선택은 비움)
        sel.Mode = mode;
    }

    /// <summary>선택 변경을 Undo 가능하게 기록한다.</summary>
    public void RecordSelection(Action<SelectionState> change)
    {
        var cmd = SelectionCommand.Record(Document, change);
        if (!cmd.IsNoop) Document.Undo.Push(cmd, alreadyApplied: true);
    }

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
                if (grow) SelectionOps.Grow(mesh, mode, set); else SelectionOps.Shrink(mesh, mode, set);
            }
        });
        Viewport.Display.RefreshAll();
    }

    /// <summary>UV 모드 선택은 정점 집합으로 바꾼 ComponentSet을 돌려준다(그 외 모드는 그대로).</summary>
    private static (ComponentSet comps, SelectMode from) NormalizeForConvert(PolyMesh mesh, ComponentSet comps, SelectMode from)
    {
        if (from != SelectMode.Uv) return (comps, from);
        var topo = UvTopology.Build(mesh);
        var c = new ComponentSet();
        foreach (int p in comps.Uvs) if (p < topo.Points.Count) c.Verts.Add(topo.Points[p].Vertex);
        return (c, SelectMode.Vertex);
    }

    private void ConvertSelection(SelectMode to)
    {
        var sel = Document.Selection;
        var from = sel.Mode;
        if (from == to || from == SelectMode.Object) { sel.Mode = to; return; }
        RecordSelection(s =>
        {
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
                var topo = UvTopology.Build(mesh);
                var comps = s.GetComponents(id);
                var points = new HashSet<int>();
                if (from == SelectMode.Uv) points.UnionWith(comps.Uvs.Where(p => p < topo.Points.Count));
                else
                {
                    var verts = from == SelectMode.Vertex ? comps.Verts : SelectionOps.Convert(mesh, comps, from, SelectMode.Vertex);
                    for (int p = 0; p < topo.Points.Count; p++) if (verts.Contains(topo.Points[p].Vertex)) points.Add(p);
                }
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

    private void DeleteSelection()
    {
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Object)
        {
            var cmd = new DeleteNodesCommand(Document, sel.Objects);
            if (!cmd.IsEmpty) Document.Undo.Push(cmd);
        }
        else Actions.Invoke("mesh.deleteComponents");
    }

    // ---------------------------------------------------------------- 폴리 편집

    /// <summary>현재 모드의 컴포넌트가 있는 노드마다 명령을 만들어 한 Undo 스텝으로 실행한다.</summary>
    private void ForEachComponentNode(string groupName, SelectMode mode, Func<NodeId, ComponentSet, ICommand?> make)
    {
        var doc = Document;
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

    private void ExtrudeSelection()
    {
        var doc = Document;
        var newSel = new Dictionary<NodeId, List<int>>();
        ForEachComponentNode("Extrude", SelectMode.Face, (id, comps) =>
        {
            var cmd = new ExtrudeFacesCommand(id, comps.Faces);
            return cmd;
        });
        // 그룹 안의 각 명령이 자기 노드의 새 면을 선택했으므로, 마지막 명령만 남은 선택을 합친다
        var merged = new Dictionary<NodeId, HashSet<int>>();
        if (doc.Undo.LastCommand is CompoundCommand cc)
            foreach (var c in cc.Items.OfType<ExtrudeFacesCommand>()) merged[c.NodeIdPublic] = new HashSet<int>(c.NewFaces);
        else if (doc.Undo.LastCommand is ExtrudeFacesCommand single) merged[single.NodeIdPublic] = new HashSet<int>(single.NewFaces);
        if (merged.Count > 0)
        {
            bool first = true;
            foreach (var (id, faces) in merged) { doc.Selection.SelectComponents(id, SelectMode.Face, faces, replace: first); first = false; }
        }
        // Maya 압출 조작기 간이판: 법선 방향 Move 툴로 전환
        ToolContext.AxisOrientation = AxisOrientation.Normal;
        Tools.SetTool("move");
        HelpLine.Text = "Extrude: drag the manipulator to offset the new faces.";
    }

    private void DeleteComponents()
    {
        var mode = Document.Selection.Mode;
        ForEachComponentNode("Delete", mode, (id, comps) => new DeleteComponentsCommand(id, mode, comps.Get(mode)));
    }

    private float _mergeThreshold = 0.001f;
    private ConfirmationDialog? _mergeDialog;
    private SpinBox? _mergeSpin;

    private void ShowMergeDialog()
    {
        if (_mergeDialog == null)
        {
            _mergeDialog = new ConfirmationDialog { Title = "Merge Vertices", OkButtonText = "Merge" };
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = "Threshold" });
            _mergeSpin = new SpinBox { MinValue = 0, MaxValue = 1000, Step = 0.0001, Value = _mergeThreshold, CustomMinimumSize = new Vector2(120, 0) };
            row.AddChild(_mergeSpin);
            _mergeDialog.AddChild(row);
            _mergeDialog.Confirmed += () => { _mergeThreshold = (float)_mergeSpin.Value; Actions.Invoke("mesh.mergeApply"); };
            AddChild(_mergeDialog);
        }
        _mergeSpin!.Value = _mergeThreshold;
        _mergeDialog.PopupCentered();
    }

    private ConfirmationDialog? _smoothDialog;
    private SpinBox? _smoothSpin;
    private int _smoothLevels = 1;

    private void ShowSmoothDialog()
    {
        if (_smoothDialog == null)
        {
            _smoothDialog = new ConfirmationDialog { Title = "Smooth", OkButtonText = "Smooth" };
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = "Division levels" });
            _smoothSpin = new SpinBox { MinValue = 1, MaxValue = 4, Step = 1, Value = _smoothLevels, CustomMinimumSize = new Vector2(100, 0) };
            row.AddChild(_smoothSpin);
            _smoothDialog.AddChild(row);
            _smoothDialog.Confirmed += () => { _smoothLevels = (int)_smoothSpin.Value; Actions.Invoke("mesh.smoothApply"); };
            AddChild(_smoothDialog);
        }
        _smoothSpin!.Value = _smoothLevels;
        _smoothDialog.PopupCentered();
    }

    /// <summary>Mesh → Smooth: Catmull-Clark 서브디비전(히스토리에서 단계 수 편집 가능).</summary>
    private void SmoothSelection()
    {
        int levels = _smoothLevels;
        var targets = Document.Selection.Objects.Where(id => Document.Find(id)?.Mesh != null).ToArray();
        using (Document.Undo.BeginGroup("Smooth"))
            foreach (var id in targets)
                Document.Undo.Push(new MeshOpCommand("Smooth", id, new HistoryParams(HistoryParam.I("Levels", levels, 0, 4)),
                    (m, p) => { MeshOps.Smooth(m, p.Int("Levels")); return (true, null, null); }));
        HelpLine.Text = $"Smooth: {levels} level(s).";
    }

    /// <summary>1/2/3 키: 선택 오브젝트의 Smooth Mesh Preview(표시 전용, 케이지는 그대로 편집).</summary>
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

    private ConfirmationDialog? _bevelDialog;
    private SpinBox? _bevelSpin;
    private float _bevelDistance = 0.1f;

    private void ShowBevelDialog()
    {
        if (_bevelDialog == null)
        {
            _bevelDialog = new ConfirmationDialog { Title = "Bevel", OkButtonText = "Bevel" };
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = "Distance" });
            _bevelSpin = new SpinBox { MinValue = 0.0001, MaxValue = 1000, Step = 0.001, Value = _bevelDistance, CustomMinimumSize = new Vector2(120, 0) };
            row.AddChild(_bevelSpin);
            _bevelDialog.AddChild(row);
            _bevelDialog.Confirmed += () => { _bevelDistance = (float)_bevelSpin.Value; Actions.Invoke("mesh.bevelApply"); };
            AddChild(_bevelDialog);
        }
        _bevelSpin!.Value = _bevelDistance;
        _bevelDialog.PopupCentered();
    }

    private void BevelSelection()
    {
        var mode = Document.Selection.Mode;
        float dist = _bevelDistance;
        ForEachComponentNode("Bevel", mode, (id, comps) =>
        {
            var mesh = Document.Find(id)?.Mesh; if (mesh == null) return null;
            var edges = mode == SelectMode.Edge ? comps.Edges.ToArray() : SelectionOps.Convert(mesh, comps, mode, SelectMode.Edge).ToArray();
            return new MeshOpCommand("Bevel", id, new HistoryParams(HistoryParam.F("Distance", dist, 0.0001f, 1000f, 0.001f)),
                (m, p) => { var faces = MeshOps.BevelEdges(m, edges, p.Float("Distance")); return (faces.Count > 0, SelectMode.Face, faces); });
        });
        HelpLine.Text = $"Bevel: distance {dist:0.###}.";
    }

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

    private void MergeSelectedVertices()
    {
        var sel = Document.Selection;
        var mode = sel.Mode;
        int total = 0;
        ForEachComponentNode("Merge Vertices", mode, (id, comps) =>
        {
            var mesh = Document.Find(id)?.Mesh; if (mesh == null) return null;
            var verts = mode == SelectMode.Vertex ? comps.Verts : SelectionOps.Convert(mesh, comps, mode, SelectMode.Vertex);
            var cmd = new MergeVerticesCommand(id, verts, _mergeThreshold);
            return cmd;
        });
        if (Document.Undo.LastCommand is CompoundCommand cc) total = cc.Items.OfType<MergeVerticesCommand>().Sum(c => c.MergedCount);
        else if (Document.Undo.LastCommand is MergeVerticesCommand m) total = m.MergedCount;
        HelpLine.Text = total > 0 ? $"Merged {total} vertex pair(s)." : "No vertices within threshold.";
    }

    private void CombineSelection()
    {
        var ids = Document.Selection.Objects.Where(id => Document.Find(id)?.Mesh != null).ToArray();
        if (ids.Length < 2) return;
        Document.Undo.Push(new CombineCommand(ids));
    }

    private void SeparateSelection()
    {
        var id = Document.Selection.ActiveObject;
        var cmd = new SeparateCommand(id);
        if (!cmd.Prepare(Document)) { HelpLine.Text = "Separate: the mesh has only one piece."; return; }
        Document.Undo.Push(cmd);
    }

    private bool HasEdgeTargets()
    {
        var sel = Document.Selection;
        return sel.Mode == SelectMode.Object ? sel.Objects.Any(id => Document.Find(id)?.Mesh != null) : sel.NodesWithComponents(sel.Mode).Any();
    }

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

    private void DuplicateSelection()
    {
        var doc = Document;
        var ids = doc.Selection.Objects.ToArray();
        if (ids.Length == 0) return;
        using (doc.Undo.BeginGroup("Duplicate"))
        {
            var created = new List<NodeId>();
            foreach (var id in ids)
            {
                var src = doc.Get(id);
                var copy = new SceneNode { Name = doc.UniqueName(src.Name), Local = src.Local, Visible = src.Visible };
                if (src.Mesh != null) copy.Shape = new MeshShape(src.Mesh.Clone());
                var cmd = new AddNodeCommand("Duplicate", copy, src.Parent != null && !src.Parent.IsRoot ? src.Parent.Id : NodeId.None);
                doc.Undo.Push(cmd);
                created.Add(copy.Id);
            }
            RecordSelection(s => { s.Mode = SelectMode.Object; s.SelectObjects(created); });
        }
    }

    public void UpdateTitle() => DisplayServer.WindowSetTitle(SceneFiles.Title);

    private PreferencesDialog? _prefs;

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
    }

    private PopupMenu? _recentMenu;

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

    private void BuildMenus()
    {
        PopupMenu Add(string title)
        {
            var pm = new PopupMenu { Name = title.Replace(" ", "") };
            MenuBar.AddChild(pm);
            MenuBar.SetMenuTitle(MenuBar.GetChildCount() - 1, title);
            return pm;
        }

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

        Menus.Build(Add("Edit"))
            .Item("edit.undo").Item("edit.redo").Item("edit.repeatLast").Separator()
            .Item("edit.delete").Item("edit.duplicate").Separator().Item("edit.deleteHistory").Separator()
            .Item("select.all").Item("select.none").Separator()
            .Item("edit.preferences");

        Menus.Build(Add("Create"))
            .Submenu("Polygon Primitives", m => m.Item("create.cube", "Cube").Item("create.sphere", "Sphere").Item("create.cylinder", "Cylinder").Item("create.cone", "Cone").Item("create.plane", "Plane").Item("create.torus", "Torus"))
            .Item("create.polygonTool").Separator()
            .Submenu("Lights", m => m.Item("create.lightDirectional", "Directional Light").Item("create.lightPoint", "Point Light").Item("create.lightSpot", "Spot Light"));

        Menus.Build(Add("Select"))
            .Item("mode.object").Item("mode.vertex").Item("mode.edge").Item("mode.face").Item("mode.uv").Separator()
            .Item("select.grow").Item("select.shrink").Separator()
            .Submenu("Convert Selection", m => m.Item("select.toVertices").Item("select.toEdges").Item("select.toFaces"));

        Menus.Build(Add("Mesh"))
            .Item("mesh.combine").Item("mesh.separate").Separator().Item("mesh.smooth").Separator().Item("mesh.soften").Item("mesh.harden").Item("mesh.reverse");

        Menus.Build(Add("Edit Mesh"))
            .Item("mesh.extrude").Item("mesh.merge").Item("mesh.bevel").Item("mesh.bridge").Separator().Item("mesh.deleteComponents");

        Menus.Build(Add("Mesh Tools"))
            .Item("mesh.insertLoop").Item("mesh.multiCut");

        Menus.Build(Add("UV"))
            .Item("windows.uvEditor").Separator()
            .Item("uv.planarBest").Item("uv.planarX").Item("uv.planarY").Item("uv.planarZ").Item("uv.cylindrical").Item("uv.spherical").Separator()
            .Item("uv.unfold").Item("uv.layout").Separator().Item("uv.cut").Item("uv.sew").Separator().Item("uv.flipU").Item("uv.flipV");
        Menus.Build(Add("Skeleton")).Item("skeleton.jointTool").Item("skeleton.insertJointTool").Separator().Item("skeleton.mirror").Item("skeleton.orient").Item("skeleton.orientApply");
        Menus.Build(Add("Skin")).Item("skin.bind").Item("skin.detach").Separator().Item("skin.paintTool").Item("skin.normalize").Item("skin.rebind");

        Menus.Build(Add("Display"))
            .Item("display.wireframe").Item("display.shaded").Item("display.textured").Item("display.lit").Item("display.uvGrid").Item("display.wireOnShaded").Separator()
            .Item("display.smoothPreviewOff").Item("display.smoothPreviewBoth").Item("display.smoothPreviewOn").Separator()
            .Item("display.jointAxes").Separator()
            .Item("display.grid").Item("display.background").Separator()
            .Submenu("View", m => m.Item("view.persp").Item("view.front").Item("view.side").Item("view.top").Item("view.back").Item("view.left").Item("view.bottom").Separator().Item("view.toggleProjection").Item("view.toggleLayout").Separator().Item("view.home").Item("view.frameSelected").Item("view.frameAll").Item("view.maximize"));

        Menus.Build(Add("Material")).Item("windows.materialEditor");
        Menus.Build(Add("Windows")).Item("windows.outliner").Item("windows.properties").Item("windows.uvEditor").Item("windows.materialEditor");
        Menus.Build(Add("Help")).Item("help.about");
    }
}
