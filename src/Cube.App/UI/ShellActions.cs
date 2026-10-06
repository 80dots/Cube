using Cube.App.Tools;
using Cube.App.Viewport;
using Cube.Core.Camera;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
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

        // --- 선택 모드
        Actions.Register("mode.object", "Object Mode", () => sel.Mode = SelectMode.Object, isChecked: () => sel.Mode == SelectMode.Object);
        Actions.Register("mode.toggle", "Toggle Object/Component", () => sel.Mode = sel.Mode == SelectMode.Object ? _lastComponentMode : SelectMode.Object);
        Actions.Register("mode.vertex", "Vertex", () => SetComponentMode(SelectMode.Vertex), isChecked: () => sel.Mode == SelectMode.Vertex);
        Actions.Register("mode.edge", "Edge", () => SetComponentMode(SelectMode.Edge), isChecked: () => sel.Mode == SelectMode.Edge);
        Actions.Register("mode.face", "Face", () => SetComponentMode(SelectMode.Face), isChecked: () => sel.Mode == SelectMode.Face);
        Actions.Register("mode.uv", "UV", () => SetComponentMode(SelectMode.Uv), isChecked: () => sel.Mode == SelectMode.Uv);

        Actions.Register("select.all", "Select All", () => RecordSelection(s => { s.Mode = SelectMode.Object; s.SelectObjects(doc.MeshNodes().Select(n => n.Id)); }));
        Actions.Register("select.none", "Deselect All", () => RecordSelection(s => s.ClearAll()), canExecute: () => !sel.IsEmpty);
        Actions.Register("select.grow", "Grow Selection", () => GrowShrink(true), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.shrink", "Shrink Selection", () => GrowShrink(false), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toVertices", "To Vertices", () => ConvertSelection(SelectMode.Vertex), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toEdges", "To Edges", () => ConvertSelection(SelectMode.Edge), canExecute: () => sel.IsComponentMode);
        Actions.Register("select.toFaces", "To Faces", () => ConvertSelection(SelectMode.Face), canExecute: () => sel.IsComponentMode);

        // --- 편집
        Actions.Register("edit.undo", "Undo", () => doc.Undo.Undo(), canExecute: () => doc.Undo.CanUndo);
        Actions.Register("edit.redo", "Redo", () => doc.Undo.Redo(), canExecute: () => doc.Undo.CanRedo);
        Actions.Register("edit.repeatLast", "Repeat Last", () => Actions.RepeatLast(), canExecute: () => Actions.LastRepeatable != null);
        Actions.Register("edit.delete", "Delete", DeleteSelection, canExecute: () => !sel.IsEmpty, repeatable: true);
        Actions.Register("edit.duplicate", "Duplicate", DuplicateSelection, canExecute: () => sel.Objects.Count > 0, repeatable: true);

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
        foreach (var (id, label) in new[] { ("mesh.bevel", "Bevel"), ("mesh.bridge", "Bridge"), ("mesh.insertLoop", "Insert Edge Loop"), ("mesh.multiCut", "Multi-Cut") })
            Actions.Register(id, label, () => { }, canExecute: () => false);

        // --- 파일 (이후 단계)
        Actions.Register("file.new", "New Scene", NewScene);
        foreach (var (id, label) in new[] { ("file.open", "Open Scene..."), ("file.save", "Save Scene"), ("file.saveAs", "Save Scene As..."), ("file.import", "Import..."), ("file.exportSelection", "Export Selection..."), ("file.exportAll", "Export All...") })
            Actions.Register(id, label, () => { }, canExecute: () => false);
        Actions.Register("file.exit", "Exit", () => GetTree().Quit());

        // --- 뷰
        Actions.Register("view.frameSelected", "Frame Selected", Viewport.FrameSelected);
        Actions.Register("view.frameAll", "Frame All", Viewport.FrameAll);
        Actions.Register("view.home", "Home", () => Viewport.CameraController.Home());
        Actions.Register("view.persp", "Perspective", () => Viewport.SetView(ViewKind.Persp), isChecked: () => Viewport.CameraController.Kind == ViewKind.Persp);
        Actions.Register("view.front", "Front", () => Viewport.SetView(ViewKind.Front), isChecked: () => Viewport.CameraController.Kind == ViewKind.Front);
        Actions.Register("view.side", "Side", () => Viewport.SetView(ViewKind.Side), isChecked: () => Viewport.CameraController.Kind == ViewKind.Side);
        Actions.Register("view.top", "Top", () => Viewport.SetView(ViewKind.Top), isChecked: () => Viewport.CameraController.Kind == ViewKind.Top);
        Actions.Register("view.maximize", "Maximize Viewport", ToggleMaximizeViewport, isChecked: () => _maximized);

        // --- 표시
        var disp = Viewport.Display;
        Actions.Register("display.wireframe", "Wireframe", () => disp.SetMode(ShadingMode.Wireframe), isChecked: () => disp.Mode == ShadingMode.Wireframe);
        Actions.Register("display.shaded", "Smooth Shade All", () => disp.SetMode(ShadingMode.Shaded), isChecked: () => disp.Mode == ShadingMode.Shaded);
        Actions.Register("display.textured", "Smooth Shade + Textured", () => disp.SetMode(ShadingMode.Textured), isChecked: () => disp.Mode == ShadingMode.Textured);
        Actions.Register("display.lit", "Use All Lights", () => disp.SetMode(ShadingMode.Lit), isChecked: () => disp.Mode == ShadingMode.Lit);
        Actions.Register("display.wireOnShaded", "Wireframe on Shaded", () => { disp.WireOnShaded = !disp.WireOnShaded; Settings.WireOnShaded = disp.WireOnShaded; disp.RefreshAll(); }, isChecked: () => disp.WireOnShaded);
        Actions.Register("display.grid", "Grid", () => { disp.ShowGrid = !disp.ShowGrid; Settings.ShowGrid = disp.ShowGrid; }, isChecked: () => disp.ShowGrid);
        Actions.Register("display.background", "Background Color", () => { Viewport.CycleBackground(); });

        // --- 창
        Actions.Register("windows.outliner", "Outliner", () => OutlinerDock.Visible = !OutlinerDock.Visible, isChecked: () => OutlinerDock.Visible);
        Actions.Register("windows.channelBox", "Channel Box", () => ChannelBoxDock.Visible = !ChannelBoxDock.Visible, isChecked: () => ChannelBoxDock.Visible);
        Actions.Register("windows.uvEditor", "UV Editor", () => { }, canExecute: () => false);
        Actions.Register("help.about", "About Cube", () => HelpLine.Text = $"Cube {ProjectSettings.GetSetting("application/config/version")} — Godot {Engine.GetVersionInfo()["string"]}");

        Actions.Register("app.escape", "Escape", () => { Tools.CancelCurrent(); Viewport.GrabFocus(); });
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
                converted[id] = SelectionOps.Convert(mesh, s.GetComponents(id), from, to);
            }
            s.Mode = to;
            bool first = true;
            foreach (var (id, set) in converted) { s.SelectComponents(id, to, set, replace: first); first = false; }
        });
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
        _axisOrientation.Selected = (int)AxisOrientation.Normal;
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

    private void NewScene()
    {
        Document.Clear();
        Viewport.CameraController.Home();
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

        Menus.Build(Add("File"))
            .Item("file.new").Item("file.open").Separator().Item("file.save").Item("file.saveAs").Separator()
            .Item("file.import").Item("file.exportSelection").Item("file.exportAll").Separator().Item("file.exit");

        Menus.Build(Add("Edit"))
            .Item("edit.undo").Item("edit.redo").Item("edit.repeatLast").Separator()
            .Item("edit.delete").Item("edit.duplicate").Separator()
            .Item("select.all").Item("select.none");

        Menus.Build(Add("Create"))
            .Submenu("Polygon Primitives", m => m.Item("create.cube", "Cube").Item("create.sphere", "Sphere").Item("create.cylinder", "Cylinder").Item("create.cone", "Cone").Item("create.plane", "Plane").Item("create.torus", "Torus"));

        Menus.Build(Add("Select"))
            .Item("mode.object").Item("mode.vertex").Item("mode.edge").Item("mode.face").Item("mode.uv").Separator()
            .Item("select.grow").Item("select.shrink").Separator()
            .Submenu("Convert Selection", m => m.Item("select.toVertices").Item("select.toEdges").Item("select.toFaces"));

        Menus.Build(Add("Mesh"))
            .Item("mesh.combine").Item("mesh.separate").Separator().Item("mesh.soften").Item("mesh.harden").Item("mesh.reverse");

        Menus.Build(Add("Edit Mesh"))
            .Item("mesh.extrude").Item("mesh.merge").Item("mesh.bevel").Item("mesh.bridge").Separator().Item("mesh.deleteComponents");

        Menus.Build(Add("Mesh Tools"))
            .Item("mesh.insertLoop").Item("mesh.multiCut");

        Menus.Build(Add("UV")).Item("windows.uvEditor");
        Menus.Build(Add("Skeleton")).Item("skeleton.createJoints", "Create Joints", disabled: true);
        Menus.Build(Add("Skin")).Item("skin.bind", "Bind Skin", disabled: true);

        Menus.Build(Add("Display"))
            .Item("display.wireframe").Item("display.shaded").Item("display.textured").Item("display.lit").Item("display.wireOnShaded").Separator()
            .Item("display.grid").Item("display.background").Separator()
            .Submenu("View", m => m.Item("view.persp").Item("view.front").Item("view.side").Item("view.top").Separator().Item("view.home").Item("view.frameSelected").Item("view.frameAll").Item("view.maximize"));

        Menus.Build(Add("Windows")).Item("windows.outliner").Item("windows.channelBox").Item("windows.uvEditor");
        Menus.Build(Add("Help")).Item("help.about");
    }
}
