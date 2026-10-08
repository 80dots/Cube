using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI.Docks;

/// <summary>Document DAG를 Tree로 보여주고 선택을 양방향 동기화한다. 더블클릭으로 이름을 바꾼다.</summary>
/// <remarks>
/// 동작 개요:
/// - 문서 구조가 바뀌면(리셋/추가/삭제/부모 변경) 트리 전체를 다시 만들고, 이름 변경은 해당 항목 텍스트만 고친다.
/// - 트리에서 고른 항목은 오브젝트 모드 선택으로 문서에 기록되고(<c>Shell.RecordSelection</c> → Undo 가능한 선택 명령),
///   문서 선택이 바뀌면 트리 선택을 그에 맞춘다.
/// - 두 방향 동기화가 서로를 다시 부르는 무한 반복은 <see cref="_syncing"/> 플래그로 막는다.
/// - 항목 메타데이터(열 0)에 NodeId 정수 값을 넣어 두고 역참조한다.
/// </remarks>
public partial class Outliner : Tree
{
    /// <summary>바인딩된 문서. <see cref="Bind"/> 전에는 null.</summary>
    private Document _doc = null!;
    /// <summary>NodeId → 트리 항목 매핑. 이름 변경 갱신과 선택 동기화에 쓴다. <see cref="Rebuild"/>마다 다시 채운다.</summary>
    private readonly Dictionary<NodeId, TreeItem> _items = new();
    /// <summary>동기화 중 표시. true인 동안 들어온 선택 이벤트는 무시해 트리↔문서 선택 사이의 재귀 갱신을 막는다.</summary>
    private bool _syncing;

    /// <summary>문서에 연결한다: 문서 변경/선택 변경 이벤트를 구독하고 트리를 처음 만든다.</summary>
    /// <param name="doc">보여 줄 문서.</param>
    public void Bind(Document doc)
    {
        _doc = doc;
        doc.Changed += OnDocChanged;
        doc.Selection.Changed += SyncFromSelection;
        Rebuild();
    }

    /// <summary>
    /// 트리 속성 설정과 입력 이벤트 연결. 루트는 숨기고(문서 루트의 자식들이 최상위로 보임) 다중 선택을 허용한다.
    /// MultiSelected는 한 번의 클릭에서 여러 번 오므로 SyncToSelection을 지연 호출해 마지막 상태로 한 번만 기록되게 한다.
    /// 더블클릭(ItemActivated)은 이름 편집을 시작하고, 빈 곳 클릭은 선택 전체 해제를 기록한다.
    /// </summary>
    public override void _Ready()
    {
        HideRoot = true;
        SelectMode = SelectModeEnum.Multi;
        AllowReselect = true;
        FocusMode = FocusModeEnum.Click;
        Columns = 1;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MultiSelected += (_, _, _) => CallDeferred(nameof(SyncToSelection));
        ItemEdited += OnItemEdited;
        ItemActivated += () => { var it = GetSelected(); if (it != null) EditSelected(); };
        NothingSelected += () => { if (!_syncing) UI.Shell.Instance.RecordSelection(s => s.ClearAll()); };
    }

    /// <summary>도킹/떼어 내기로 트리를 옮겨도 구독을 유지하고, 실제로 지워질 때만 해제한다.</summary>
    public override void _Notification(int what)
    {
        // 노드가 실제로 해제되기 직전(Predelete)에만 이벤트 구독을 푼다. _ExitTree는 도킹 이동 때도 불리므로 쓰지 않는다.
        if (what != (int)NotificationPredelete) return;
        if (_doc != null) { _doc.Changed -= OnDocChanged; _doc.Selection.Changed -= SyncFromSelection; }
    }

    /// <summary>
    /// 문서 변경 처리. 구조 변경(리셋/추가/삭제/부모 변경)은 전체 재구성, 이름 변경은 그 항목의 텍스트만 갱신한다.
    /// 그 외 변경(트랜스폼·메시 등)은 아웃라이너와 무관하므로 무시한다.
    /// </summary>
    private void OnDocChanged(DocChange c)
    {
        switch (c.Kind)
        {
            case ChangeKind.Reset: case ChangeKind.NodeAdded: case ChangeKind.NodeRemoved: case ChangeKind.NodeReparented:
                Rebuild(); break;
            case ChangeKind.NodeRenamed:
                if (_items.TryGetValue(c.Node, out var it)) it.SetText(0, _doc.Get(c.Node).Name);
                break;
        }
    }

    /// <summary>
    /// 트리를 처음부터 다시 만든다. 만드는 동안 _syncing을 켜서 Clear/CreateItem이 일으키는 선택 이벤트를 무시하고,
    /// 끝나면 문서 선택을 트리에 다시 반영한다.
    /// </summary>
    private void Rebuild()
    {
        _syncing = true;
        Clear(); _items.Clear();
        // 숨겨진 루트 항목을 만들고 문서 루트의 자식부터 재귀로 추가한다.
        var root = CreateItem();
        foreach (var n in _doc.Root.Children) AddItem(n, root);
        _syncing = false;
        SyncFromSelection();
    }

    /// <summary>노드 하나의 트리 항목을 만들고(이름, NodeId 메타데이터, 편집 가능) 자식들을 재귀로 추가한다.</summary>
    /// <param name="n">추가할 장면 노드.</param>
    /// <param name="parent">부모 트리 항목.</param>
    private void AddItem(SceneNode n, TreeItem parent)
    {
        var it = CreateItem(parent);
        it.SetText(0, n.Name);
        it.SetMetadata(0, n.Id.Value);
        it.SetEditable(0, true);
        _items[n.Id] = it;
        foreach (var c in n.Children) AddItem(c, it);
    }

    /// <summary>문서 선택 → 트리 선택. 모든 항목 선택을 지운 뒤 선택된 오브젝트의 항목만 선택한다(컴포넌트 선택은 반영하지 않음).</summary>
    private void SyncFromSelection()
    {
        if (_syncing) return;
        _syncing = true;
        DeselectAll();
        foreach (var id in _doc.Selection.Objects)
            if (_items.TryGetValue(id, out var it)) it.Select(0);
        _syncing = false;
    }

    /// <summary>
    /// 트리 선택 → 문서 선택. 선택된 항목들의 NodeId를 모아 오브젝트 모드로 전환하고 그 오브젝트들을 선택하는 명령을 기록한다.
    /// 기록 중에는 _syncing을 켜서 문서 선택 변경 이벤트가 트리를 다시 건드리지 않게 한다.
    /// </summary>
    private void SyncToSelection()
    {
        if (_syncing) return;
        // 트리의 선택 항목을 순회하며 메타데이터에서 NodeId를 복원한다.
        var ids = new List<NodeId>();
        for (var it = GetNextSelected(null); it != null; it = GetNextSelected(it))
            ids.Add(new NodeId(it.GetMetadata(0).AsInt32()));
        _syncing = true;
        UI.Shell.Instance.RecordSelection(s => { s.Mode = Core.Selection.SelectMode.Object; s.SelectObjects(ids); });
        _syncing = false;
    }

    /// <summary>
    /// 이름 편집 완료 처리. 앞뒤 공백을 자른 새 이름이 비었거나 기존과 같으면 원래 이름으로 되돌리고,
    /// 아니면 <see cref="RenameNodeCommand"/>를 Undo 스택에 넣는다(텍스트 갱신은 NodeRenamed 이벤트가 한다).
    /// </summary>
    private void OnItemEdited()
    {
        var it = GetEdited(); if (it == null) return;
        var id = new NodeId(it.GetMetadata(0).AsInt32());
        var name = it.GetText(0).Trim();
        var node = _doc.Find(id);
        if (node == null || name.Length == 0 || name == node.Name) { if (node != null) it.SetText(0, node.Name); return; }
        _doc.Undo.Push(new RenameNodeCommand(_doc, id, name));
    }
}
