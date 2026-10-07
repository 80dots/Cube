namespace Cube.App;

/// <summary>메뉴·셸프·툴박스·핫키가 공유하는 액션. 모두 ActionId로만 호출한다.</summary>
public sealed class ShellAction
{
    public string Id { get; }
    public string Label { get; }
    public Action Execute { get; }
    public Func<bool>? CanExecute { get; init; }
    public Func<bool>? IsChecked { get; init; }
    /// <summary>G(Repeat Last)로 반복 가능한 편집 액션인지.</summary>
    public bool Repeatable { get; init; }

    public ShellAction(string id, string label, Action execute) { Id = id; Label = label; Execute = execute; }
    public bool Enabled => CanExecute?.Invoke() ?? true;
}

public sealed class ActionRegistry
{
    private readonly Dictionary<string, ShellAction> _actions = new();
    public string? LastRepeatable { get; private set; }
    public event Action<string>? Invoked;
    /// <summary>실행 직전(Action Popup이 실행 전 Undo 상태를 기억하는 데 쓴다).</summary>
    public event Action<string>? Invoking;

    public IEnumerable<ShellAction> All => _actions.Values;

    public ShellAction Register(string id, string label, Action execute, Func<bool>? canExecute = null, bool repeatable = false, Func<bool>? isChecked = null)
    {
        var a = new ShellAction(id, label, execute) { CanExecute = canExecute, Repeatable = repeatable, IsChecked = isChecked };
        _actions[id] = a;
        return a;
    }

    public ShellAction? Get(string id) => _actions.TryGetValue(id, out var a) ? a : null;

    public bool Invoke(string id)
    {
        if (!_actions.TryGetValue(id, out var a)) { Godot.GD.PushWarning($"[Actions] unknown action '{id}'"); return false; }
        if (!a.Enabled) return false;
        Invoking?.Invoke(id);
        a.Execute();
        if (a.Repeatable) LastRepeatable = id;
        Invoked?.Invoke(id);
        return true;
    }

    public bool RepeatLast() => LastRepeatable != null && Invoke(LastRepeatable);
}
