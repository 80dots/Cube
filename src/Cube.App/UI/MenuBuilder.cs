using Cube.App.Hotkeys;
using Godot;

namespace Cube.App.UI;

/// <summary>메뉴 구조를 정의하고 PopupMenu를 만든다. 항목은 ActionId로만 연결되며 단축키 표시는 HotkeyMap에서 가져온다.</summary>
public sealed class MenuBuilder
{
    private readonly ActionRegistry _actions;
    private readonly HotkeyMap _hotkeys;
    private readonly List<(PopupMenu menu, int index, string action)> _items = new();

    public MenuBuilder(ActionRegistry actions, HotkeyMap hotkeys) { _actions = actions; _hotkeys = hotkeys; }

    public sealed class Menu
    {
        public readonly PopupMenu Popup;
        private readonly MenuBuilder _b;
        internal Menu(MenuBuilder b, PopupMenu p) { _b = b; Popup = p; }

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

        public Menu Separator(string? label = null) { Popup.AddSeparator(label ?? ""); return this; }

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

    public Menu Build(PopupMenu popup)
    {
        popup.IdPressed += id => OnPressed(popup, id);
        popup.AboutToPopup += () => RefreshStates(popup);
        return new Menu(this, popup);
    }

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
        foreach (var (menu, idx, action) in _items)
        {
            if (menu != popup && !IsDescendant(menu, popup)) continue;
            var a = _actions.Get(action);
            if (a == null) continue;
            menu.SetItemDisabled(idx, !a.Enabled);
            if (a.IsChecked != null) menu.SetItemChecked(idx, a.IsChecked());
        }
    }

    private static bool IsDescendant(Node n, Node ancestor)
    {
        for (var p = n.GetParent(); p != null; p = p.GetParent()) if (p == ancestor) return true;
        return false;
    }
}
