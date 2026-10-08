namespace Cube.App;

/// <summary>메뉴·셸프·툴박스·핫키가 공유하는 액션. 모두 ActionId로만 호출한다.</summary>
/// <remarks>
/// 액션 하나 = 고유 ID(예: "mesh.extrudeApply") + 표시 라벨 + 실행 델리게이트 + (선택) 활성/체크 상태 함수.
/// UI는 ShellAction을 직접 실행하지 않고 <see cref="ActionRegistry.Invoke"/>를 거쳐 Repeat Last·Action Popup 이벤트가 일관되게 동작한다.
/// </remarks>
public sealed class ShellAction
{
    /// <summary>고유 액션 ID("영역.이름" 형식). 핫키 바인딩·메뉴·셸프가 이 문자열로 참조한다.</summary>
    public string Id { get; }
    /// <summary>메뉴·툴팁에 보이는 이름.</summary>
    public string Label { get; }
    /// <summary>실행 본문.</summary>
    public Action Execute { get; }
    /// <summary>실행 가능 여부(null이면 항상 가능). 메뉴/셸프 비활성 표시와 Invoke의 사전 검사에 쓴다.</summary>
    public Func<bool>? CanExecute { get; init; }
    /// <summary>토글 액션의 현재 상태(null이면 토글 아님). 메뉴 체크 표시·셸프 토글 버튼에 쓴다.</summary>
    public Func<bool>? IsChecked { get; init; }
    /// <summary>G(Repeat Last)로 반복 가능한 편집 액션인지.</summary>
    public bool Repeatable { get; init; }

    /// <summary>ID·라벨·실행 본문으로 만든다. 나머지 속성은 init으로 지정한다.</summary>
    public ShellAction(string id, string label, Action execute) { Id = id; Label = label; Execute = execute; }
    /// <summary>현재 실행 가능한지(<see cref="CanExecute"/>가 없으면 true).</summary>
    public bool Enabled => CanExecute?.Invoke() ?? true;
}

/// <summary>
/// 모든 <see cref="ShellAction"/>의 등록부. ID로 찾아 실행하고, 실행 전후 이벤트와 마지막 반복 가능 액션(G 키 Repeat Last)을 관리한다.
/// </summary>
public sealed class ActionRegistry
{
    /// <summary>ID → 액션. 같은 ID로 다시 등록하면 덮어쓴다.</summary>
    private readonly Dictionary<string, ShellAction> _actions = new();
    /// <summary>마지막으로 실행된 반복 가능 액션의 ID(Repeat Last 대상).</summary>
    public string? LastRepeatable { get; private set; }
    /// <summary>실행 직후(Action Popup이 새 Undo 명령·히스토리 항목을 감지하는 데 쓴다).</summary>
    public event Action<string>? Invoked;
    /// <summary>실행 직전(Action Popup이 실행 전 Undo 상태를 기억하는 데 쓴다).</summary>
    public event Action<string>? Invoking;

    /// <summary>등록된 모든 액션(핫키 편집기·메뉴 검색 등 열거용).</summary>
    public IEnumerable<ShellAction> All => _actions.Values;

    /// <summary>액션을 등록(같은 ID면 교체)하고 만든 액션을 돌려준다.</summary>
    /// <param name="repeatable">true면 실행 후 Repeat Last 대상으로 기억한다.</param>
    public ShellAction Register(string id, string label, Action execute, Func<bool>? canExecute = null, bool repeatable = false, Func<bool>? isChecked = null)
    {
        var a = new ShellAction(id, label, execute) { CanExecute = canExecute, Repeatable = repeatable, IsChecked = isChecked };
        _actions[id] = a;
        return a;
    }

    /// <summary>ID로 액션을 찾는다(없으면 null).</summary>
    public ShellAction? Get(string id) => _actions.TryGetValue(id, out var a) ? a : null;

    /// <summary>
    /// 액션을 실행한다. 순서: 존재 확인(없으면 경고) → 활성 검사 → Invoking 이벤트 → 실행 → 반복 가능이면 기억 → Invoked 이벤트.
    /// </summary>
    /// <returns>실제로 실행했으면 true.</returns>
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

    /// <summary>마지막 반복 가능 액션을 다시 실행한다(G 키). 없으면 false.</summary>
    public bool RepeatLast() => LastRepeatable != null && Invoke(LastRepeatable);
}
