using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;
using GColor = Godot.Color;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <remarks>
/// 구조: Name 행 + Type 행 + <c>_body</c>(그룹 머리 버튼과 그룹 상자들). 본문 UI는 (머티리얼 ID, 타입)이 바뀔 때만 다시 만들고,
/// 값 갱신은 파라미터 키마다 등록한 "updater"(머티리얼 → 컨트롤 반영 람다)를 모두 호출하는 방식이라 Undo/Redo 후에도 가볍다.
/// <c>_updating</c> 플래그로 컨트롤 갱신 중 발생하는 변경 신호가 다시 명령을 만들지 않게 막는다.
/// </remarks>
/// <summary>
/// 머티리얼 하나의 속성 편집기. <see cref="MaterialParams"/> 표에서 현재 타입에 해당하는 파라미터를 그룹별로 보여 주고,
/// 텍스처 가능한 파라미터마다 텍스처 버튼(Load / ✕ Clear)을 둔다. glTF 확장 그룹(Clearcoat, Transmission/Volume, Specular, Sheen, Iridescence,
/// Anisotropy)과 Alpha/Texture Transform은 접혀 있다가 제목을 눌러 펼친다(접힘 상태는 세션 동안 기억).
/// Material Editor와 Properties 패널이 공유한다. 모든 변경은 SetMaterialCommand로 Undo 가능하며, 숫자 칸 가운데 버튼 드래그·색 고르기 중에는 미리보기만 한다.
/// </summary>
public partial class MaterialPropsEditor : VBoxContainer
{
    /// <summary>문서·Undo·파일 다이얼로그 부모로 쓰는 셸.</summary>
    private Shell _shell = null!;
    /// <summary>머티리얼 이름 입력(Enter로 확정).</summary>
    private LineEdit _name = null!;
    /// <summary>머티리얼 타입 드롭다운(MaterialType 열거 순서).</summary>
    private OptionButton _type = null!;
    /// <summary>Name 행 전체(Properties 패널처럼 이름을 따로 보여 주는 곳에서는 숨김, <see cref="ShowName"/>).</summary>
    private Control _nameRow = null!;
    /// <summary>타입별 파라미터 그룹을 담는 본문.</summary>
    private VBoxContainer _body = null!;
    /// <summary>true인 동안은 컨트롤 → 머티리얼 방향의 변경을 무시한다(Refresh가 컨트롤 값을 채우는 중).</summary>
    private bool _updating;
    /// <summary>현재 본문을 만든 기준(머티리얼 ID, 타입). 같으면 본문을 다시 만들지 않는다.</summary>
    private (int id, MaterialType type)? _built;
    /// <summary>파라미터 키 → 머티리얼 값을 컨트롤에 반영하는 람다(텍스처 가능 항목은 버튼 표시까지 포함).</summary>
    private readonly Dictionary<string, Action<MaterialDef>> _updaters = new();
    /// <summary>펼쳐진 그룹 이름 집합(정적이라 편집기 인스턴스·세션 동안 공유). 기본은 Base/Surface/Emission만 펼침.</summary>
    private static readonly HashSet<string> Expanded = new() { MaterialParams.GBase, MaterialParams.GSurface, MaterialParams.GEmission };

    /// <summary>편집 대상 머티리얼 ID(0 = 없음/기본 lambert1).</summary>
    public int MaterialId { get; private set; }
    /// <summary>Name 행 표시 여부.</summary>
    public bool ShowName { get => _nameRow.Visible; set => _nameRow.Visible = value; }

    /// <summary>고정 행(Name, Type)과 본문 컨테이너를 만들고 현재 머티리얼로 채운다.</summary>
    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", (int)(4 * s));

        // 이름: Enter로 확정(빈 문자열이면 유지), 확정 후 키보드 포커스를 뷰포트로 돌려 단축키가 다시 먹게 한다.
        _name = new LineEdit { PlaceholderText = "name" };
        _name.TextSubmitted += t => { Commit(m => m.Name = string.IsNullOrWhiteSpace(t) ? m.Name : t.Trim()); CallDeferred(nameof(ReturnFocus)); };
        _nameRow = Row("Name", _name, null, s); AddChild(_nameRow);
        // 타입: 바꾸면 명령으로 기록되고 Refresh가 새 타입의 파라미터로 본문을 다시 만든다.
        _type = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var t in Enum.GetNames<MaterialType>()) _type.AddItem(t);
        _type.ItemSelected += i => Commit(m => m.Type = (MaterialType)(int)i);
        AddChild(Row("Type", _type, null, s));
        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", (int)(3 * s));
        AddChild(_body);
        Refresh();
    }

    /// <summary>이름 입력 후 포커스를 활성 뷰포트로 되돌린다(텍스트 필드에 포커스가 남으면 단축키가 무시됨).</summary>
    private static void ReturnFocus() => Shell.Instance?.Viewport.GrabFocus();
    /// <summary>Godot 색(RGB) → System.Numerics 벡터(알파 버림).</summary>
    private static NVec3 ToVec(GColor c) => new(c.R, c.G, c.B);
    /// <summary>System.Numerics 벡터 → Godot 색(알파 1).</summary>
    private static GColor ToColor(NVec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// "라벨(고정 폭 110px, 잘림) + 컨트롤(가로 채움) + 선택적 꼬리(텍스처 버튼 등)" 한 행을 만든다.
    /// </summary>
    /// <param name="tip">라벨 툴팁(없으면 라벨 자체).</param>
    private static Control Row(string label, Control c, Control? tail, float s, string? tip = null)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Godot.Vector2(110 * s, 0), ClipText = true, TooltipText = tip ?? label, MouseFilter = MouseFilterEnum.Pass });
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(c);
        if (tail != null) row.AddChild(tail);
        return row;
    }

    /// <summary>이 머티리얼을 쓰는 모든 노드에 MaterialChanged를 통지해 뷰포트 표시를 갱신시킨다(미리보기·변경 없는 확정 때).</summary>
    private void NotifyUsers(int id)
    {
        foreach (var n in _shell.Document.Nodes.Values) if (n.MaterialId == id) _shell.Document.Notify(new DocChange(ChangeKind.MaterialChanged, n.Id));
    }

    /// <remarks>색 고르기 중(ColorChanged)과 숫자 칸 MMB 드래그 중에 쓴다. Undo 기록이 없으므로 끝날 때 반드시 <see cref="Commit"/>이 뒤따른다.</remarks>
    /// <summary>미리보기: 문서 머티리얼에 직접 쓰고 사용자에게 통지(명령 없음). 확정은 Commit.</summary>
    private void Preview(Action<MaterialDef> change)
    {
        if (_updating) return;
        var m = _shell.Document.FindMaterial(MaterialId); if (m == null) return;
        change(m);
        NotifyUsers(MaterialId);
    }

    /// <remarks>
    /// 순서: 현재 머티리얼을 복제해 change를 적용한 "after"를 만든다 → 미리보기로 바뀌었을 수 있는 문서 머티리얼을
    /// Refresh 때 저장해 둔 원본(<c>_original</c>)으로 복원 → after가 원본과 같으면 통지만 하고 끝 → 다르면 SetMaterialCommand를 푸시.
    /// 이렇게 해야 Undo가 미리보기 이전 값으로 돌아간다.
    /// </remarks>
    /// <summary>변경을 명령으로 기록한다. 미리보기로 이미 바뀐 값은 원래대로 되돌린 뒤 명령이 적용한다.</summary>
    private void Commit(Action<MaterialDef> change)
    {
        if (_updating) return;
        var m = _shell.Document.FindMaterial(MaterialId); if (m == null) return;
        var after = m.Clone(); change(after);
        if (_original != null && _original.Id == m.Id) m.CopyFrom(_original);
        if (after.ValuesEqual(m)) { NotifyUsers(MaterialId); return; }
        _shell.Document.Undo.Push(new SetMaterialCommand(MaterialId, after));
    }

    /// <summary>마지막 Refresh 시점의 머티리얼 복제본(미리보기 전 원본). Commit이 명령 전에 이 값으로 되돌린다.</summary>
    private MaterialDef? _original;
    /// <summary>MMB 드래그가 끝나면 실행할 확정 동작(드래그 중 마지막 값으로 Commit).</summary>
    private Action? _pendingCommit;

    /// <summary>매 프레임 드래그 종료를 감시해 보류된 확정을 한 번 실행한다.</summary>
    public override void _Process(double delta)
    {
        // 숫자 칸 가운데 버튼 드래그가 끝나면 미리보기 값을 한 번에 확정
        if (_pendingCommit != null && SpinDrag.ActiveDrag == 0) { var c = _pendingCommit; _pendingCommit = null; c(); }
    }

    /// <summary>네이티브 파일 다이얼로그로 이미지 파일을 고르게 하고, 고르면 onPicked(경로)를 부른다. 다이얼로그는 닫히면 해제된다.</summary>
    private void PickImage(string title, Action<string> onPicked)
    {
        var dlg = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = title };
        dlg.Filters = new[] { "*.png, *.jpg, *.jpeg, *.bmp, *.tga, *.webp, *.exr, *.hdr ; Images" };
        dlg.FileSelected += path => { onPicked(path); dlg.QueueFree(); };
        dlg.Canceled += () => dlg.QueueFree();
        _shell.AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>편집 대상 머티리얼을 바꾸고 UI를 갱신한다(0이면 본문을 숨김).</summary>
    public void SetMaterial(int id) { MaterialId = id; Refresh(); }

    /// <summary>텍스처를 할당할 때 glTF처럼 값(계수)이 텍스처를 곱하므로, 0이거나 검정이면 1/흰색으로 바꿔 텍스처가 그대로 보이게 한다.</summary>
    // 텍스처 경로를 먼저 설정하고(null = 제거), 할당일 때만 계수를 보정한다. 기본 색("color")은 항상 흰색으로 바꾼다.
    public static void AssignTexture(MaterialDef m, MatParamInfo p, string? path)
    {
        m.SetTex(p.Key, path);
        if (path == null) return;
        var v = m.Get(p.Key);
        if (p.Kind == MatParamKind.Color && (p.Key == "color" || v == NVec3.Zero)) m.Set(p.Key, NVec3.One);
        else if (p.Kind == MatParamKind.Float && v.X == 0f) m.Set(p.Key, MathF.Min(1f, p.Max));
    }

    /// <summary>
    /// 본문을 다시 만든다: 기존 자식 제거 → MaterialParams.For(타입)를 그룹별로 묶어 그룹마다 접기 머리 버튼 + 상자 + 파라미터 행.
    /// Matcap 타입이면 matcap 이미지 행을 덧붙인다. updater 사전도 새로 채워진다.
    /// </summary>
    private void Build(MaterialDef m)
    {
        float s = CubeApp.Instance.UiScale;
        foreach (var c in _body.GetChildren()) { _body.RemoveChild(c); c.QueueFree(); }
        _updaters.Clear();
        string? group = null; VBoxContainer? groupBox = null;
        var groups = MaterialParams.For(m.Type).GroupBy(p => p.Group).ToList();
        // 그룹 머리: ▾(펼침)/▸(접힘) + 그룹 이름. 누르면 Expanded 집합을 토글하고 상자 표시와 화살표를 갱신한다.
        foreach (var g in groups)
        {
            group = g.Key;
            var header = new Button { Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, Text = (Expanded.Contains(group) ? "▾ " : "▸ ") + group, ClipText = true, TooltipText = group };
            header.AddThemeColorOverride("font_color", MayaTheme.TextDim);
            groupBox = new VBoxContainer { Visible = Expanded.Contains(group) };
            // 람다 캡처용 지역 복사본.
            string gname = group; var box = groupBox;
            header.Pressed += () => { if (!Expanded.Remove(gname)) Expanded.Add(gname); box.Visible = Expanded.Contains(gname); header.Text = (box.Visible ? "▾ " : "▸ ") + gname; };
            _body.AddChild(header);
            _body.AddChild(groupBox);
            foreach (var p in g) groupBox.AddChild(ParamRow(p, s));
        }
        if (m.Type == MaterialType.Matcap) _body.AddChild(MatcapRow(s));
    }

    /// <summary>
    /// 파라미터 하나의 행을 만든다. 종류별 컨트롤: Color = 색 버튼(고르는 중 미리보기, 팝업 닫힐 때 확정), Bool = 체크박스,
    /// Enum = 드롭다운(인덱스를 실수로 저장), 그 외(Float) = SpinBox(범위에 따라 증분 0.01/0.05/1, MMB 드래그 중 미리보기 후 끝나면 확정).
    /// 텍스처 가능하면 꼬리에 Map…/파일명 버튼과 ✕ 버튼을 붙이고, 툴팁에 glTF 확장 이름·설명·텍스처 슬롯 유무를 넣는다.
    /// </summary>
    private Control ParamRow(MatParamInfo p, float s)
    {
        Control ctl;
        switch (p.Kind)
        {
            case MatParamKind.Color:
                {
                    var cp = new ColorPickerButton { EditAlpha = false, CustomMinimumSize = new Godot.Vector2(0, 22 * s) };
                    cp.ColorChanged += c => Preview(m => m.Set(p.Key, ToVec(c)));
                    cp.PopupClosed += () => Commit(m => m.Set(p.Key, ToVec(cp.Color)));
                    _updaters[p.Key] = m => cp.Color = ToColor(m.Get(p.Key));
                    ctl = cp; break;
                }
            case MatParamKind.Bool:
                {
                    var cb = new CheckBox { FocusMode = FocusModeEnum.None };
                    cb.Toggled += on => Commit(m => m.Set(p.Key, on ? 1f : 0f));
                    _updaters[p.Key] = m => cb.SetPressedNoSignal(m.GetF(p.Key) > 0.5f);
                    ctl = cb; break;
                }
            case MatParamKind.Enum:
                {
                    var ob = new OptionButton { FocusMode = FocusModeEnum.None };
                    foreach (var c in p.Choices ?? Array.Empty<string>()) ob.AddItem(c);
                    ob.ItemSelected += i => Commit(m => m.Set(p.Key, (float)i));
                    _updaters[p.Key] = m => ob.Selected = Math.Clamp((int)m.GetF(p.Key), 0, ob.ItemCount - 1);
                    ctl = ob; break;
                }
            default:
                {
                    // 범위 폭에 따라 증분을 정한다: 0~1 계열 0.01, 20 이하 0.05, 그 이상 1.
                    double range = p.Max - p.Min;
                    var sb = new SpinBox { MinValue = p.Min, MaxValue = p.Max, Step = range <= 1.01 ? 0.01 : range <= 20 ? 0.05 : 1, CustomMinimumSize = new Godot.Vector2(80 * s, 0), SelectAllOnFocus = true };
                    sb.ValueChanged += v =>
                    {
                        if (_updating) return;
                        // 드래그 중이면 미리보기만 하고 확정은 드래그가 끝난 뒤(_Process)로 미룬다 → 한 드래그 = Undo 한 단계.
                        if (SpinDrag.ActiveDrag != 0) { Preview(m => m.Set(p.Key, (float)v)); _pendingCommit = () => Commit(m => m.Set(p.Key, (float)sb.Value)); }
                        else Commit(m => m.Set(p.Key, (float)v));
                    };
                    _updaters[p.Key] = m => sb.SetValueNoSignal(m.GetF(p.Key));
                    ctl = sb; break;
                }
        }
        // 텍스처 꼬리: 불러오기 버튼(파일명 표시) + 제거 버튼. 기존 값 updater를 감싸 버튼 상태까지 갱신한다.
        Control? tail = null;
        if (p.Texturable)
        {
            var box = new HBoxContainer();
            var btn = new Button { FocusMode = FocusModeEnum.None, ClipText = true, CustomMinimumSize = new Godot.Vector2(64 * s, 0), TooltipText = "Load a texture for " + p.Label };
            btn.Pressed += () => PickImage("Texture: " + p.Label, path => Commit(m => AssignTexture(m, p, path)));
            var clear = new Button { Text = "✕", FocusMode = FocusModeEnum.None, TooltipText = "Remove the texture" };
            clear.Pressed += () => Commit(m => m.SetTex(p.Key, null));
            box.AddChild(btn); box.AddChild(clear);
            var prev = _updaters[p.Key];
            _updaters[p.Key] = m =>
            {
                prev(m);
                var t = m.Tex(p.Key);
                btn.Text = t == null ? "Map…" : System.IO.Path.GetFileName(t);
                btn.TooltipText = t ?? "Load a texture for " + p.Label;
                btn.Modulate = t == null ? new GColor(1, 1, 1, 0.75f) : new GColor(0.75f, 0.9f, 1f);
                clear.Visible = t != null;
            };
            tail = box;
        }
        // 툴팁: 라벨 + [glTF 확장] + 설명 + (텍스처 슬롯이 없는 스칼라/색이면 그 안내).
        string tip = p.Label + (p.Extension != null ? $"  [{p.Extension}]" : "") + (p.Tip != null ? "\n" + p.Tip : "") + (!p.Texturable && p.Kind is MatParamKind.Float or MatParamKind.Color ? "\n(glTF has no texture slot for this value)" : "");
        return Row(p.Label, ctl, tail, s, tip);
    }

    /// <summary>Matcap 이미지 행: 현재 파일명(없으면 내장), Load...(이미지 고르기), Built-in(내장 matcap으로 되돌리기).</summary>
    private Control MatcapRow(float s)
    {
        var box = new HBoxContainer();
        var lbl = new Label { Text = "(built-in)", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        box.AddChild(lbl);
        var load = new Button { Text = "Load...", FocusMode = FocusModeEnum.None };
        load.Pressed += () => PickImage("Load Matcap Image", path => Commit(m => m.MatcapPath = path));
        box.AddChild(load);
        var builtIn = new Button { Text = "Built-in", FocusMode = FocusModeEnum.None };
        builtIn.Pressed += () => Commit(m => m.MatcapPath = null);
        box.AddChild(builtIn);
        _updaters["__matcap"] = m => lbl.Text = string.IsNullOrEmpty(m.MatcapPath) ? "(built-in)" : System.IO.Path.GetFileName(m.MatcapPath);
        return Row("Matcap", box, null, s);
    }

    /// <summary>
    /// 현재 머티리얼로 UI를 맞춘다(<c>_updating</c> 동안 신호 무시). 원본 복제본을 저장하고, (ID, 타입)이 바뀌었으면 본문을 다시 만든 뒤
    /// 모든 updater를 실행한다. 머티리얼이 없으면 이름을 비우고 본문을 숨긴다. 문서 변경·SetMaterial 후 호출된다.
    /// </summary>
    public void Refresh()
    {
        var m = _shell.Document.FindMaterial(MaterialId);
        _updating = true;
        bool has = m != null;
        _name.Editable = has; _type.Disabled = !has;
        if (m != null)
        {
            _original = m.Clone();
            _name.Text = m.Name; _type.Selected = (int)m.Type;
            if (_built != (m.Id, m.Type)) { Build(m); _built = (m.Id, m.Type); }
            foreach (var u in _updaters.Values) u(m);
            _body.Visible = true;
        }
        else { _original = null; _name.Text = ""; _body.Visible = false; _built = null; }
        _updating = false;
    }
}
