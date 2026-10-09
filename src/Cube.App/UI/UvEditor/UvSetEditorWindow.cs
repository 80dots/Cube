using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI.UvEditor;

/// <summary>UV Set Editor(플로팅 패널): 활성 메시의 UV 세트 목록. New / Copy / Rename / Delete / Set Current.</summary>
/// <remarks>
/// 동작: 목록에서 항목을 고르면 그 세트를 현재 세트(Uv0에 올라오는 세트)로 바꾸고, 이름 칸으로 현재 세트 이름을 바꾼다.
/// New/Copy/Delete 버튼은 직접 메시를 고치지 않고 ActionRegistry의 <c>uv.setCreate/setCopy/setDelete</c> 액션을 호출한다.
/// 모든 변경은 <see cref="UvSetsCommand"/>로 Undo 스택에 들어가며, 문서/선택 변경 이벤트마다 목록을 다시 채운다.
/// </remarks>
public partial class UvSetEditorWindow : FloatingPanel
{
    /// <summary>소유 셸. 문서·액션 레지스트리·UV 편집기 창에 접근하는 데 쓴다.</summary>
    private Shell _shell = null!;
    /// <summary>UV 세트 이름 목록. 인덱스 = <c>PolyMesh.UvSets</c> 인덱스, 현재 세트에는 " (current)"가 붙는다.</summary>
    private ItemList _list = null!;
    /// <summary>현재 세트의 새 이름을 입력하는 칸(Enter 또는 Rename 버튼으로 적용).</summary>
    private LineEdit _name = null!;
    /// <summary>상단 정보 라벨: 대상 노드 이름과 세트 개수, 메시가 없으면 안내 문구.</summary>
    private Label _info = null!;

    /// <summary>
    /// 패널 UI를 구성하고 이벤트를 연결한다(셸이 패널을 만들 때 한 번 호출).
    /// 크기는 UiScale을 곱한 픽셀 값이며, 문서의 메시 속성/리셋/노드 삭제와 선택 변경에 맞춰 <see cref="Refresh"/>한다.
    /// </summary>
    /// <param name="shell">소유 셸.</param>
    public void Setup(Shell shell)
    {
        _shell = shell;
        // UI 배율(Hi-DPI × 사용자 배율)로 패널 크기를 정한다.
        float s = CubeApp.Instance.UiScale;
        Title = "UV Set Editor";
        Size = new Vector2(320 * s, 300 * s);
        MinPanelSize = new Vector2(260 * s, 220 * s);
        // 세로 상자: 정보 라벨 → 세트 목록 → 버튼 줄 → 이름 바꾸기 줄 순서.
        var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _info = new Label { Text = "" };
        box.AddChild(_info);
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.Click };
        // 목록 선택 = 현재 세트 전환. 이미 현재 세트이거나 범위 밖이면 명령을 만들지 않는다(Refresh가 Select를 다시 불러도 무한 반복 없음).
        _list.ItemSelected += i => { var n = Node(); if (n?.Mesh != null) { int idx = (int)i; if (n.Mesh.UvSets.Count > idx && n.Mesh.CurrentUvSet != idx) Op("Switch UV Set", n, m => m.SwitchUvSet(idx)); } };
        box.AddChild(_list);
        var row = new HBoxContainer();
        // 생성/복사/삭제 버튼은 액션 ID만 호출한다(메뉴·파이와 같은 경로를 써서 동작을 일치시킴).
        foreach (var (label, act) in new[] { ("New", "uv.setCreate"), ("Copy", "uv.setCopy"), ("Delete", "uv.setDelete") })
        {
            var b = new Button { Text = label, FocusMode = Control.FocusModeEnum.None }; string a = act;
            b.Pressed += () => shell.Actions.Invoke(a); row.AddChild(b);
        }
        box.AddChild(row);
        // 이름 바꾸기 줄: 입력 칸 + Rename 버튼.
        var row2 = new HBoxContainer();
        _name = new LineEdit { PlaceholderText = "rename current set", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _name.TextSubmitted += t => Rename(t);
        row2.AddChild(_name);
        var rb = new Button { Text = "Rename", FocusMode = Control.FocusModeEnum.None };
        rb.Pressed += () => Rename(_name.Text);
        row2.AddChild(rb);
        box.AddChild(row2);
        Content.AddChild(box);
        // 메시 속성(UV 세트 포함)·문서 리셋·노드 삭제 때와 선택이 바뀔 때 목록을 갱신한다.
        shell.Document.Changed += c => { if (c.Kind is ChangeKind.MeshAttributes or ChangeKind.Reset or ChangeKind.NodeRemoved) Refresh(); };
        shell.Document.Selection.Changed += Refresh;
        Refresh();
    }

    /// <summary>
    /// 편집 대상 노드를 찾는다: 활성 오브젝트가 우선이고, 없으면 컴포넌트가 선택된 노드 중 메시를 가진 첫 노드.
    /// </summary>
    /// <returns>대상 SceneNode, 없으면 null.</returns>
    private SceneNode? Node() { var doc = _shell.Document; return doc.Find(doc.Selection.ActiveObject) ?? doc.Selection.Components.Keys.Select(id => doc.Find(id)).FirstOrDefault(n => n?.Mesh != null); }

    /// <summary>
    /// UV 세트 구조를 바꾸는 연산을 <see cref="UvSetsCommand"/>로 실행해 Undo 스택에 넣고,
    /// UV 편집기 캔버스를 무효화(다시 그림)한 뒤 목록을 갱신한다.
    /// </summary>
    /// <param name="name">Undo 항목 이름.</param>
    /// <param name="n">대상 노드.</param>
    /// <param name="op">메시에 적용할 연산(명령이 앞뒤 스냅샷을 잡는다).</param>
    private void Op(string name, SceneNode n, Action<Core.Mesh.PolyMesh> op)
    {
        _shell.Document.Undo.Push(new UvSetsCommand(name, n.Id, op));
        _shell.UvEditorWindow?.Canvas.Invalidate();
        Refresh();
    }

    /// <summary>현재 UV 세트 이름을 바꾼다. 빈 문자열은 무시하고 앞뒤 공백은 잘라낸다. 세트가 아직 없으면 EnsureUvSets로 기본 세트를 만든다.</summary>
    private void Rename(string text)
    {
        var n = Node(); if (n?.Mesh == null || string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        // 다른 세트와 같은 이름이면 숫자를 붙인다(같은 이름 세트가 둘이면 구분할 수 없다)
        Op("Rename UV Set", n, m => { m.EnsureUvSets(); m.UvSets[m.CurrentUvSet].Name = m.UniqueUvSetName(t, m.CurrentUvSet); });
    }

    /// <summary>
    /// 대상 노드의 UV 세트 목록을 다시 채운다. UV 세트가 하나도 없는 메시(암묵적 기본 세트)는 "map1 (current)"만 보여 준다.
    /// 현재 세트 항목을 선택 상태로 둔다.
    /// </summary>
    public void Refresh()
    {
        _list.Clear();
        var n = Node();
        if (n?.Mesh == null) { _info.Text = "(select a mesh)"; return; }
        var m = n.Mesh;
        // UvSets가 비어 있으면 Uv0 자체가 유일한 세트(map1)다.
        if (m.UvSets.Count == 0) { _list.AddItem("map1 (current)"); _list.Select(0); _info.Text = n.Name; return; }
        for (int i = 0; i < m.UvSets.Count; i++) _list.AddItem(m.UvSets[i].Name + (i == m.CurrentUvSet ? " (current)" : ""));
        _list.Select(Math.Clamp(m.CurrentUvSet, 0, m.UvSets.Count - 1));
        _info.Text = $"{n.Name}: {m.UvSets.Count} UV set(s)";
    }

    /// <summary>패널 열기/닫기 토글(메뉴 액션에서 호출). 열 때 목록을 새로 채운다.</summary>
    public void Toggle() { if (Visible) Close(); else { Open(); Refresh(); } }
}
