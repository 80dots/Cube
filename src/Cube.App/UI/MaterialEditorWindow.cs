using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;
using GColor = Godot.Color;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// Material Editor(임베디드 창): 머티리얼 목록(New/Delete/Assign to Selected)과 선택 머티리얼의 속성 편집.
/// 타입: Lambert / BlinnPhong / PBR / Unlit / Matcap. 모든 변경은 Undo 가능.
/// </summary>
public partial class MaterialEditorWindow : FloatingPanel
{
    private Shell _shell = null!;
    private ItemList _list = null!;
    private OptionButton _newType = null!;
    private LineEdit _name = null!;
    private OptionButton _type = null!;
    private ColorPickerButton _color = null!, _specular = null!;
    private HSlider _shininess = null!, _metallic = null!, _roughness = null!;
    private Label _matcapLabel = null!;
    private Control _blinnRow = null!, _pbrRow = null!, _matcapRow = null!, _specRow = null!;
    private Label _assigned = null!;
    private int _selectedId;
    private bool _updating;

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "Material Editor";
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Godot.Vector2(MathF.Min(620 * s, host.X * 0.7f), MathF.Min(420 * s, host.Y * 0.7f));
        MinPanelSize = new Godot.Vector2(420 * s, 300 * s);

        var root = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", (int)(8 * s));
        Content.AddChild(root);

        // 왼쪽: 목록 + 버튼
        var left = new VBoxContainer { CustomMinimumSize = new Godot.Vector2(200 * s, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        left.AddChild(new Label { Text = "Materials" });
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.Click };
        _list.ItemSelected += i => { _selectedId = (int)_list.GetItemMetadata((int)i); RefreshProps(); };
        left.AddChild(_list);
        var newRow = new HBoxContainer();
        _newType = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var t in Enum.GetNames<MaterialType>()) _newType.AddItem(t);
        newRow.AddChild(_newType);
        var btnNew = new Button { Text = "New", FocusMode = Control.FocusModeEnum.None };
        btnNew.Pressed += NewMaterial;
        newRow.AddChild(btnNew);
        left.AddChild(newRow);
        var row2 = new HBoxContainer();
        var btnDel = new Button { Text = "Delete", FocusMode = Control.FocusModeEnum.None };
        btnDel.Pressed += () => { if (_selectedId > 0) _shell.Document.Undo.Push(new DeleteMaterialCommand(_selectedId)); };
        row2.AddChild(btnDel);
        var btnAssign = new Button { Text = "Assign to Selected", FocusMode = Control.FocusModeEnum.None, TooltipText = "Assign this material to the selected objects" };
        btnAssign.Pressed += AssignToSelected;
        row2.AddChild(btnAssign);
        left.AddChild(row2);
        var btnDefault = new Button { Text = "Assign Default (lambert1)", FocusMode = Control.FocusModeEnum.None };
        btnDefault.Pressed += () => Assign(0);
        left.AddChild(btnDefault);
        root.AddChild(left);

        // 오른쪽: 속성
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", (int)(6 * s));
        right.AddChild(new Label { Text = "Properties" });
        _name = new LineEdit { PlaceholderText = "name" };
        _name.TextSubmitted += t => Commit(m => m.Name = string.IsNullOrWhiteSpace(t) ? m.Name : t.Trim());
        right.AddChild(Row("Name", _name, s));
        _type = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var t in Enum.GetNames<MaterialType>()) _type.AddItem(t);
        _type.ItemSelected += i => Commit(m => m.Type = (MaterialType)(int)i);
        right.AddChild(Row("Type", _type, s));
        _color = new ColorPickerButton { EditAlpha = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(0, 24 * s) };
        _color.PopupClosed += () => Commit(m => m.Color = ToVec(_color.Color));
        right.AddChild(Row("Color", _color, s));
        _specular = new ColorPickerButton { EditAlpha = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(0, 24 * s) };
        _specular.PopupClosed += () => Commit(m => m.Specular = ToVec(_specular.Color));
        _specRow = Row("Specular", _specular, s); right.AddChild(_specRow);
        _shininess = Slider(1, 128, 1, v => Commit(m => m.Shininess = v));
        _blinnRow = Row("Shininess", _shininess, s); right.AddChild(_blinnRow);
        _metallic = Slider(0, 1, 0.01, v => Commit(m => m.Metallic = v));
        _pbrRow = new VBoxContainer();
        _pbrRow.AddChild(Row("Metallic", _metallic, s));
        _roughness = Slider(0, 1, 0.01, v => Commit(m => m.Roughness = v));
        _pbrRow.AddChild(Row("Roughness", _roughness, s));
        right.AddChild(_pbrRow);
        var matcapBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _matcapLabel = new Label { Text = "(built-in)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        matcapBox.AddChild(_matcapLabel);
        var btnMatcap = new Button { Text = "Load...", FocusMode = Control.FocusModeEnum.None };
        btnMatcap.Pressed += LoadMatcap;
        matcapBox.AddChild(btnMatcap);
        var btnMatcapClear = new Button { Text = "Built-in", FocusMode = Control.FocusModeEnum.None };
        btnMatcapClear.Pressed += () => Commit(m => m.MatcapPath = null);
        matcapBox.AddChild(btnMatcapClear);
        _matcapRow = Row("Matcap", matcapBox, s); right.AddChild(_matcapRow);
        _assigned = new Label { Text = "", Modulate = new GColor(1, 1, 1, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        right.AddChild(_assigned);
        root.AddChild(right);

        shell.Document.Changed += c => { if (c.Kind is ChangeKind.MaterialChanged or ChangeKind.Reset or ChangeKind.NodeRemoved or ChangeKind.NodeAdded) RefreshList(); };
        shell.Document.Selection.Changed += () => RefreshAssignedLabel();
        RefreshList();
    }

    private static NVec3 ToVec(GColor c) => new(c.R, c.G, c.B);
    private static GColor ToColor(NVec3 v) => new(v.X, v.Y, v.Z);

    private static Control Row(string label, Control c, float s)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Godot.Vector2(70 * s, 0) });
        c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(c);
        return row;
    }

    private HSlider Slider(double min, double max, double step, Action<float> set)
    {
        var sl = new HSlider { MinValue = min, MaxValue = max, Step = step, FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        sl.DragEnded += changed => { if (changed) set((float)sl.Value); };
        sl.ValueChanged += v => { if (!_updating && !sl.HasFocus()) { /* 드래그 중 미리보기 */ Preview(sl, (float)v); } };
        return sl;
    }

    private void Preview(HSlider sl, float v)
    {
        var m = _shell.Document.FindMaterial(_selectedId); if (m == null) return;
        if (sl == _shininess) m.Shininess = v; else if (sl == _metallic) m.Metallic = v; else if (sl == _roughness) m.Roughness = v;
        foreach (var n in _shell.Document.Nodes.Values) if (n.MaterialId == _selectedId) _shell.Document.Notify(new DocChange(ChangeKind.MaterialChanged, n.Id));
    }

    private void Commit(Action<MaterialDef> change)
    {
        if (_updating) return;
        var m = _shell.Document.FindMaterial(_selectedId); if (m == null) return;
        var after = m.Clone(); change(after);
        var before = m.Clone(); change(m); bool same = m.Name == before.Name && m.Type == before.Type && m.Color == before.Color && m.Specular == before.Specular && m.Shininess == before.Shininess && m.Metallic == before.Metallic && m.Roughness == before.Roughness && m.MatcapPath == before.MatcapPath;
        m.CopyFrom(before);
        if (same) return;
        _shell.Document.Undo.Push(new SetMaterialCommand(_selectedId, after));
    }

    private void NewMaterial()
    {
        var type = (MaterialType)_newType.Selected;
        var mat = new MaterialDef { Name = _shell.Document.UniqueMaterialName(type.ToString().ToLowerInvariant()), Type = type };
        if (type == MaterialType.Pbr) { mat.Metallic = 0f; mat.Roughness = 0.5f; }
        if (type == MaterialType.Matcap) mat.Color = new NVec3(1, 1, 1); // matcap은 색을 곱하므로 기본 흰색
        var cmd = new AddMaterialCommand(mat);
        _shell.Document.Undo.Push(cmd);
        _selectedId = cmd.Material.Id;
        RefreshList();
    }

    private void AssignToSelected() { if (_selectedId > 0) Assign(_selectedId); }

    private void Assign(int id)
    {
        var ids = _shell.Document.Selection.Objects.Where(x => _shell.Document.Find(x)?.Mesh != null).ToArray();
        if (ids.Length == 0) { _shell.HelpLine.Text = "Assign Material: select an object first."; return; }
        _shell.Document.Undo.Push(new AssignMaterialCommand(ids, id));
        _shell.HelpLine.Text = $"Assigned {(id == 0 ? "lambert1" : _shell.Document.FindMaterial(id)?.Name)} to {ids.Length} object(s).";
    }

    private void LoadMatcap()
    {
        var dlg = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "Load Matcap Image" };
        dlg.Filters = new[] { "*.png, *.jpg, *.jpeg ; Images" };
        dlg.FileSelected += path => { Commit(m => m.MatcapPath = path); dlg.QueueFree(); };
        dlg.Canceled += () => dlg.QueueFree();
        _shell.AddChild(dlg);
        dlg.PopupCentered();
    }

    public void RefreshList()
    {
        var doc = _shell.Document;
        _list.Clear();
        int sel = -1;
        foreach (var m in doc.Materials)
        {
            int idx = _list.AddItem($"{m.Name}  [{m.Type}]");
            _list.SetItemMetadata(idx, m.Id);
            if (m.Id == _selectedId) sel = idx;
        }
        if (sel < 0 && doc.Materials.Count > 0) { sel = 0; _selectedId = doc.Materials[0].Id; }
        if (sel >= 0) _list.Select(sel); else _selectedId = 0;
        RefreshProps();
    }

    private void RefreshProps()
    {
        var m = _shell.Document.FindMaterial(_selectedId);
        _updating = true;
        bool has = m != null;
        _name.Editable = has; _type.Disabled = !has; _color.Disabled = !has;
        if (m != null)
        {
            _name.Text = m.Name; _type.Selected = (int)m.Type; _color.Color = ToColor(m.Color); _specular.Color = ToColor(m.Specular);
            _shininess.Value = m.Shininess; _metallic.Value = m.Metallic; _roughness.Value = m.Roughness;
            _matcapLabel.Text = string.IsNullOrEmpty(m.MatcapPath) ? "(built-in)" : System.IO.Path.GetFileName(m.MatcapPath);
            _specRow.Visible = _blinnRow.Visible = m.Type == MaterialType.BlinnPhong;
            _pbrRow.Visible = m.Type == MaterialType.Pbr;
            _matcapRow.Visible = m.Type == MaterialType.Matcap;
        }
        else { _name.Text = ""; _specRow.Visible = _blinnRow.Visible = _pbrRow.Visible = _matcapRow.Visible = false; }
        _updating = false;
        RefreshAssignedLabel();
    }

    private void RefreshAssignedLabel()
    {
        var doc = _shell.Document;
        var m = doc.FindMaterial(_selectedId);
        if (m == null) { _assigned.Text = ""; return; }
        var users = doc.Nodes.Values.Where(n => n.MaterialId == m.Id).Select(n => n.Name).ToList();
        _assigned.Text = users.Count == 0 ? "Assigned to: (none)" : "Assigned to: " + string.Join(", ", users);
    }

    public void Toggle()
    {
        if (Visible) { Close(); return; }
        Open();
        RefreshList();
    }
}
