using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;
using GColor = Godot.Color;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// Material Editor(플로팅 패널): 머티리얼 목록(List / Thumbnails 보기, New/Delete/Assign to Selected)과 선택 머티리얼의 속성 편집(MaterialPropsEditor).
/// 타입: Lambert / BlinnPhong / PBR / Unlit / Matcap. 썸네일은 구에 재질을 입혀 렌더한다(MaterialThumbnails). 모든 변경은 Undo 가능.
/// </summary>
public partial class MaterialEditorWindow : FloatingPanel
{
    private Shell _shell = null!;
    private ItemList _list = null!;
    private OptionButton _newType = null!;
    private MaterialPropsEditor _props = null!;
    private MaterialThumbnails _thumbs = null!;
    private Button _viewList = null!, _viewThumbs = null!;
    private Label _assigned = null!;
    private int _selectedId;

    public int SelectedId => _selectedId;

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "Material Editor";
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Godot.Vector2(MathF.Min(640 * s, host.X * 0.7f), MathF.Min(440 * s, host.Y * 0.7f));
        MinPanelSize = new Godot.Vector2(440 * s, 320 * s);

        _thumbs = new MaterialThumbnails { Name = "Thumbnails", SizePx = (int)(72 * s) };
        AddChild(_thumbs);

        var root = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", (int)(8 * s));
        Content.AddChild(root);

        // 왼쪽: 목록 + 보기 전환 + 버튼
        var left = new VBoxContainer { CustomMinimumSize = new Godot.Vector2(220 * s, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var head = new HBoxContainer();
        head.AddChild(new Label { Text = "Materials", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _viewList = new Button { Text = "List", ToggleMode = true, FocusMode = Control.FocusModeEnum.None, TooltipText = "Show materials as a list" };
        _viewThumbs = new Button { Text = "Thumbnails", ToggleMode = true, FocusMode = Control.FocusModeEnum.None, TooltipText = "Show materials as sphere thumbnails" };
        _viewList.Pressed += () => SetThumbnails(false);
        _viewThumbs.Pressed += () => SetThumbnails(true);
        head.AddChild(_viewList); head.AddChild(_viewThumbs);
        left.AddChild(head);
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.Click };
        _list.ItemSelected += i => { _selectedId = (int)_list.GetItemMetadata((int)i); _props.SetMaterial(_selectedId); RefreshAssignedLabel(); };
        left.AddChild(_list);
        var newRow = new HBoxContainer();
        _newType = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var t in Enum.GetNames<MaterialType>()) _newType.AddItem(t);
        newRow.AddChild(_newType);
        var btnNew = new Button { Text = "New", FocusMode = Control.FocusModeEnum.None };
        btnNew.Pressed += NewMaterial;
        newRow.AddChild(btnNew);
        left.AddChild(newRow);
        var row2 = new HFlowContainer(); // 좁아지면 줄바꿈
        var btnDel = new Button { Text = "Delete", FocusMode = Control.FocusModeEnum.None };
        btnDel.Pressed += () => { if (_selectedId > 0) _shell.Document.Undo.Push(new DeleteMaterialCommand(_selectedId)); };
        row2.AddChild(btnDel);
        var btnAssign = new Button { Text = "Assign to Selected", FocusMode = Control.FocusModeEnum.None, TooltipText = "Assign this material to the selected objects" };
        btnAssign.Pressed += () => { if (_selectedId > 0) _shell.AssignMaterialToSelection(_selectedId); };
        row2.AddChild(btnAssign);
        left.AddChild(row2);
        var btnDefault = new Button { Text = "Assign Default (lambert1)", FocusMode = Control.FocusModeEnum.None };
        btnDefault.Pressed += () => _shell.AssignMaterialToSelection(0);
        left.AddChild(btnDefault);
        root.AddChild(left);

        // 오른쪽: 속성
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", (int)(6 * s));
        right.AddChild(new Label { Text = "Properties" });
        _props = new MaterialPropsEditor { Name = "Props" };
        _props.Setup(shell);
        right.AddChild(_props);
        _assigned = new Label { Text = "", Modulate = new GColor(1, 1, 1, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        right.AddChild(_assigned);
        root.AddChild(right);

        shell.Document.Changed += c => { if (c.Kind is ChangeKind.MaterialChanged or ChangeKind.Reset or ChangeKind.NodeRemoved or ChangeKind.NodeAdded) RefreshList(); };
        shell.Document.Selection.Changed += () => RefreshAssignedLabel();
        SetThumbnails(CubeApp.Instance.Settings.MaterialThumbnails, save: false);
    }

    /// <summary>List ↔ Thumbnails 보기.</summary>
    public void SetThumbnails(bool thumbs, bool save = true)
    {
        float s = CubeApp.Instance.UiScale;
        _viewList.SetPressedNoSignal(!thumbs); _viewThumbs.SetPressedNoSignal(thumbs);
        if (thumbs)
        {
            _list.IconMode = ItemList.IconModeEnum.Top; _list.MaxColumns = 0; _list.SameColumnWidth = true;
            _list.FixedIconSize = new Vector2I(_thumbs.SizePx, _thumbs.SizePx); _list.FixedColumnWidth = (int)(_thumbs.SizePx + 16 * s);
        }
        else
        {
            _list.IconMode = ItemList.IconModeEnum.Left; _list.MaxColumns = 1; _list.SameColumnWidth = false;
            _list.FixedIconSize = new Vector2I((int)(20 * s), (int)(20 * s)); _list.FixedColumnWidth = 0;
        }
        if (save) { CubeApp.Instance.Settings.MaterialThumbnails = thumbs; CubeApp.Instance.Settings.Save(); }
        RefreshList();
    }

    public bool ThumbnailsShown => _viewThumbs.ButtonPressed;

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

    public void RefreshList()
    {
        var doc = _shell.Document;
        _list.Clear();
        int sel = -1;
        bool thumbs = ThumbnailsShown;
        foreach (var m in doc.Materials)
        {
            var icon = _thumbs.Get(m);
            int idx = _list.AddItem(thumbs ? m.Name : $"{m.Name}  [{m.Type}]", icon);
            _list.SetItemMetadata(idx, m.Id);
            _list.SetItemTooltip(idx, $"{m.Name} [{m.Type}]");
            if (m.Id == _selectedId) sel = idx;
        }
        _thumbs.Prune(doc.Materials.Select(m => m.Id));
        if (sel < 0 && doc.Materials.Count > 0) { sel = 0; _selectedId = doc.Materials[0].Id; }
        if (sel >= 0) _list.Select(sel); else _selectedId = 0;
        _props.SetMaterial(_selectedId);
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
