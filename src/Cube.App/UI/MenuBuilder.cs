using Cube.App.Hotkeys;
using Godot;

namespace Cube.App.UI;

/// <remarks>
/// 사용 흐름: <c>Build(popup)</c>로 <see cref="Menu"/>를 얻어 <c>.Item(...)/.Separator()/.Submenu(...)/.Op(...)</c>를 체인으로 호출한다.
/// 각 항목의 메타데이터에 ActionId 문자열을 저장해 두고, 클릭 시 그 ID로 ActionRegistry.Invoke를 호출한다.
/// 메뉴가 열리기 직전(AboutToPopup) 해당 팝업과 서브메뉴 항목의 활성/체크 상태를 액션의 Enabled/IsChecked로 갱신한다.
/// </remarks>
/// <summary>메뉴 구조를 정의하고 PopupMenu를 만든다. 항목은 ActionId로만 연결되며 단축키 표시는 HotkeyMap에서 가져온다.</summary>
public sealed class MenuBuilder
{
    /// <summary>액션 조회·실행에 쓰는 레지스트리.</summary>
    private readonly ActionRegistry _actions;
    /// <summary>항목 옆 단축키 표시(첫 번째 코드)를 얻는 단축키 맵.</summary>
    private readonly HotkeyMap _hotkeys;
    /// <summary>만든 모든 항목(팝업, 항목 인덱스, ActionId). 상태 갱신 때 순회한다.</summary>
    private readonly List<(PopupMenu menu, int index, string action)> _items = new();

    /// <summary>액션 레지스트리와 단축키 맵을 받아 빌더를 만든다.</summary>
    public MenuBuilder(ActionRegistry actions, HotkeyMap hotkeys) { _actions = actions; _hotkeys = hotkeys; }

    /// <summary>팝업 하나에 항목을 덧붙이는 플루언트 래퍼. 모든 메서드가 자기 자신을 돌려줘 체인 호출이 가능하다.</summary>
    public sealed class Menu
    {
        /// <summary>항목을 추가할 대상 PopupMenu.</summary>
        public readonly PopupMenu Popup;
        /// <summary>항목 목록과 액션 레지스트리를 가진 소유 빌더.</summary>
        private readonly MenuBuilder _b;
        /// <summary>빌더 내부에서만 만든다(<see cref="MenuBuilder.Build"/>, <see cref="Submenu"/>).</summary>
        internal Menu(MenuBuilder b, PopupMenu p) { _b = b; Popup = p; }

        /// <summary>
        /// 액션 항목을 추가한다. 라벨 = labelOverride → 액션 Label → ID 순. 액션에 IsChecked가 있으면 체크 항목으로 만든다.
        /// 단축키가 있으면 가속기 표시를 붙이고, 액션이 등록되지 않았거나 disabled면 비활성으로 만든다.
        /// 항목 ID는 추가 시점의 ItemCount(팝업 안에서 고유)이며 메타데이터에 ActionId를 넣는다.
        /// </summary>
        public Menu Item(string action, string? labelOverride = null, bool disabled = false)
        {
            var a = _b._actions.Get(action);
            string label = labelOverride ?? a?.Label ?? action;
            int id = Popup.ItemCount;
            if (a?.IsChecked != null) Popup.AddCheckItem(label, id); else Popup.AddItem(label, id);
            int idx = Popup.GetItemIndex(id);
            var chord = _b._hotkeys.FirstChord(action);
            if (chord != null) Popup.SetItemAccelerator(idx, chord.Value.ToAccelerator());
            if (disabled || a == null) Popup.SetItemDisabled(idx, true);
            Popup.SetItemMetadata(idx, action);
            _b._items.Add((Popup, idx, action));
            return this;
        }

        /// <summary>구분선(선택적으로 라벨 포함)을 추가한다.</summary>
        public Menu Separator(string? label = null) { Popup.AddSeparator(label ?? ""); return this; }

        // 예: Op("mesh.bevel") → "mesh.bevelApply"(Bevel) 항목 + "mesh.bevel"(Bevel Options...) 항목.
        /// <summary>옵션 쌍(Maya 규약): "<id>Apply"(라벨 "X", 마지막 옵션으로 실행) 다음에 "<id>"(라벨 "X Options...").</summary>
        public Menu Op(string dialogId) => Item(dialogId + "Apply").Item(dialogId);

        /// <summary>
        /// 서브메뉴를 추가한다. 새 PopupMenu를 현재 팝업의 자식으로 붙이고(Name은 공백 제거한 제목), build 콜백으로 내용을 채운 뒤
        /// 서브메뉴 클릭도 같은 OnPressed로 연결한다. 상태 갱신은 부모 팝업이 열릴 때 IsDescendant로 함께 처리된다.
        /// </summary>
        public Menu Submenu(string title, Action<Menu> build)
        {
            var sub = new PopupMenu { Name = title.Replace(" ", "") };
            Popup.AddChild(sub);
            Popup.AddSubmenuNodeItem(title, sub);
            build(new Menu(_b, sub));
            sub.IdPressed += id => _b.OnPressed(sub, id);
            return this;
        }
    }

    /// <summary>이미 클릭/열림 핸들러를 연결한 팝업 집합(중복 연결 방지).</summary>
    private readonly HashSet<PopupMenu> _wired = new();

    /// <summary>같은 팝업에 여러 번 호출해 항목을 이어 붙일 수 있다(File 메뉴). 클릭 핸들러는 팝업마다 한 번만 연결한다(두 번 연결하면 액션이 두 번 실행됨).</summary>
    // 처음 보는 팝업일 때만 클릭 → 액션 실행, 열림 → 상태 갱신 핸들러를 연결한다.
    public Menu Build(PopupMenu popup)
    {
        if (_wired.Add(popup))
        {
            popup.IdPressed += id => OnPressed(popup, id);
            popup.AboutToPopup += () => RefreshStates(popup);
        }
        return new Menu(this, popup);
    }

    /// <summary>항목 클릭 처리: 항목 ID → 인덱스 → 메타데이터의 ActionId를 읽어 실행한다. --drive 모드에서는 [Menu] 로그를 찍는다.</summary>
    private void OnPressed(PopupMenu popup, long id)
    {
        int idx = popup.GetItemIndex((int)id);
        var action = popup.GetItemMetadata(idx).AsString();
        if (Hotkeys.ShellInput.Verbose) GD.Print($"[Menu] {popup.Name} id={id} -> {action}");
        if (!string.IsNullOrEmpty(action)) _actions.Invoke(action);
    }

    /// <summary>메뉴가 열릴 때 활성/체크 상태 갱신(서브메뉴 포함).</summary>
    public void RefreshStates(PopupMenu popup)
    {
        // 이 팝업 또는 그 하위 서브메뉴의 항목만 갱신한다.
        foreach (var (menu, idx, action) in _items)
        {
            if (menu != popup && !IsDescendant(menu, popup)) continue;
            var a = _actions.Get(action);
            if (a == null) continue;
            menu.SetItemDisabled(idx, !a.Enabled);
            if (a.IsChecked != null) menu.SetItemChecked(idx, a.IsChecked());
        }
    }

    /// <summary>n이 ancestor의 (직계가 아닌 것까지 포함한) 자손 노드인지.</summary>
    private static bool IsDescendant(Node n, Node ancestor)
    {
        for (var p = n.GetParent(); p != null; p = p.GetParent()) if (p == ancestor) return true;
        return false;
    }
}
