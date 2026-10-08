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
    /// <summary>문서·Undo·선택 할당에 쓰는 셸.</summary>
    private Shell _shell = null!;
    /// <summary>머티리얼 목록(항목 메타데이터 = 머티리얼 ID).</summary>
    private ItemList _list = null!;
    /// <summary>New 버튼으로 만들 머티리얼 타입 선택(MaterialType 열거 순서).</summary>
    private OptionButton _newType = null!;
    /// <summary>선택 머티리얼의 속성 편집기(오른쪽).</summary>
    private MaterialPropsEditor _props = null!;
    /// <summary>목록 아이콘으로 쓰는 구 썸네일 렌더러(이 패널의 자식 노드).</summary>
    private MaterialThumbnails _thumbs = null!;
    /// <summary>List / Thumbnails 보기 전환 토글 버튼.</summary>
    private Button _viewList = null!, _viewThumbs = null!;
    /// <summary>선택 머티리얼이 할당된 노드 이름 목록을 보여 주는 라벨.</summary>
    private Label _assigned = null!;
    /// <summary>현재 선택된 머티리얼 ID(0 = 없음 또는 기본 lambert1).</summary>
    private int _selectedId;

    /// <summary>현재 선택된 머티리얼 ID(파이 메뉴 등 외부에서 읽음).</summary>
    public int SelectedId => _selectedId;

    /// <summary>
    /// 패널을 만든다: 왼쪽(목록, 보기 전환, New/Delete/Assign 버튼)과 오른쪽(스크롤되는 속성 편집기, 할당 대상 라벨).
    /// 문서의 머티리얼/노드 변경과 선택 변경을 구독해 목록·라벨을 갱신하고, 저장된 보기 모드를 적용한다.
    /// </summary>
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
        // 목록에서 고르면 그 머티리얼을 속성 편집기에 띄우고 할당 대상 라벨을 갱신한다.
        _list.ItemSelected += i => { _selectedId = (int)_list.GetItemMetadata((int)i); _props.SetMaterial(_selectedId); RefreshAssignedLabel(); };
        left.AddChild(_list);
        // 새 머티리얼: 타입 드롭다운 + New.
        var newRow = new HBoxContainer();
        _newType = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var t in Enum.GetNames<MaterialType>()) _newType.AddItem(t);
        newRow.AddChild(_newType);
        var btnNew = new Button { Text = "New", FocusMode = Control.FocusModeEnum.None };
        btnNew.Pressed += NewMaterial;
        newRow.AddChild(btnNew);
        left.AddChild(newRow);
        // 삭제(DeleteMaterialCommand, Undo 가능)와 선택 오브젝트에 할당.
        var row2 = new HFlowContainer(); // 좁아지면 줄바꿈
        var btnDel = new Button { Text = "Delete", FocusMode = Control.FocusModeEnum.None };
        btnDel.Pressed += () => { if (_selectedId > 0) _shell.Document.Undo.Push(new DeleteMaterialCommand(_selectedId)); };
        row2.AddChild(btnDel);
        var btnAssign = new Button { Text = "Assign to Selected", FocusMode = Control.FocusModeEnum.None, TooltipText = "Assign this material to the selected objects" };
        btnAssign.Pressed += () => { if (_selectedId > 0) _shell.AssignMaterialToSelection(_selectedId); };
        row2.AddChild(btnAssign);
        left.AddChild(row2);
        // 기본 머티리얼(ID 0 = lambert1)로 되돌리기.
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
        // 파라미터가 많으므로(glTF 확장 그룹) 스크롤
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_props);
        right.AddChild(scroll);
        _assigned = new Label { Text = "", Modulate = new GColor(1, 1, 1, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        right.AddChild(_assigned);
        root.AddChild(right);

        // 머티리얼 추가/삭제/변경, 문서 리셋, 노드 추가/삭제(할당 라벨에 영향) 때 목록을 다시 만든다.
        shell.Document.Changed += c => { if (c.Kind is ChangeKind.MaterialChanged or ChangeKind.Reset or ChangeKind.NodeRemoved or ChangeKind.NodeAdded) RefreshList(); };
        shell.Document.Selection.Changed += () => RefreshAssignedLabel();
        SetThumbnails(CubeApp.Instance.Settings.MaterialThumbnails, save: false);
    }

    /// <remarks>
    /// Thumbnails: 아이콘 위·글자 아래, 여러 열(MaxColumns 0 = 폭에 맞춰 자동), 아이콘 = 썸네일 크기.
    /// List: 아이콘 왼쪽 20px, 한 열. save가 true면 설정에 보기 모드를 저장한다. 마지막에 목록을 다시 채운다.
    /// </remarks>
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

    /// <summary>현재 썸네일 보기인지(토글 버튼 상태).</summary>
    public bool ThumbnailsShown => _viewThumbs.ButtonPressed;

    /// <summary>
    /// 선택한 타입으로 고유 이름의 새 머티리얼을 만들어 AddMaterialCommand로 추가(Undo 가능)하고 선택한다.
    /// PBR은 금속성 0·거칠기 0.5, Matcap은 흰색을 기본값으로 둔다.
    /// </summary>
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

    /// <summary>
    /// 문서 머티리얼로 목록을 다시 채운다(각 항목에 썸네일 아이콘, 메타데이터 = ID). 사라진 머티리얼의 썸네일은 정리한다.
    /// 이전 선택 ID가 없으면 첫 머티리얼을 고르고, 속성 편집기와 할당 라벨도 그 선택에 맞춘다.
    /// </summary>
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

    /// <summary>선택 머티리얼을 MaterialId로 가진 노드 이름들을 "Assigned to:" 라벨에 표시한다.</summary>
    private void RefreshAssignedLabel()
    {
        var doc = _shell.Document;
        var m = doc.FindMaterial(_selectedId);
        if (m == null) { _assigned.Text = ""; return; }
        var users = doc.Nodes.Values.Where(n => n.MaterialId == m.Id).Select(n => n.Name).ToList();
        _assigned.Text = users.Count == 0 ? "Assigned to: (none)" : "Assigned to: " + string.Join(", ", users);
    }

    /// <summary>보이면 닫고, 숨겨져 있으면 열면서 목록을 새로 채운다(Windows → Material Editor).</summary>
    public void Toggle()
    {
        if (Visible) { Close(); return; }
        Open();
        RefreshList();
    }
}
