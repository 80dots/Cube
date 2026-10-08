using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>
/// 자체 명령 스택. 드래그처럼 이미 적용된 변경은 <c>alreadyApplied: true</c>로 밀어 넣는다.
/// <see cref="BeginGroup"/> 안에서 밀어 넣은 명령은 하나의 <see cref="CompoundCommand"/>로 합쳐진다.
/// </summary>
/// <remarks>
/// Undo 목록(_undo)의 끝이 가장 최근 명령이다. 새 명령이 들어오면 Redo 목록은 비워지고,
/// <see cref="MaxSteps"/>를 넘으면 가장 오래된 명령부터 버린다. 그룹이 열려 있는 동안에는 Undo/Redo를 막는다.
/// </remarks>
public sealed class UndoStack
{
    /// <summary>명령이 적용될 문서.</summary>
    private readonly Document _doc;
    /// <summary>되돌릴 수 있는 명령들(끝이 최신).</summary>
    private readonly List<ICommand> _undo = new();
    /// <summary>다시 실행할 수 있는 명령들(끝이 바로 직전에 Undo한 명령).</summary>
    private readonly List<ICommand> _redo = new();
    /// <summary>현재 열려 있는 그룹(없으면 null). 중첩 그룹은 바깥 그룹 하나로 합쳐진다.</summary>
    private CompoundCommand? _group;
    /// <summary>BeginGroup 중첩 깊이. 0이 되는 순간 그룹이 닫혀 스택에 들어간다.</summary>
    private int _groupDepth;

    /// <summary>보관할 최대 Undo 단계 수(기본 100).</summary>
    public int MaxSteps { get; set; } = 100;
    /// <summary>스택 내용이 바뀔 때(Push/Undo/Redo/Clear/그룹 종료) 발생. 메뉴 갱신·Action Popup 감지에 쓴다.</summary>
    public event Action? Changed;

    /// <summary>문서에 연결된 빈 스택을 만든다.</summary>
    public UndoStack(Document doc) { _doc = doc; }

    /// <summary>Undo할 명령이 있는지.</summary>
    public bool CanUndo => _undo.Count > 0;
    /// <summary>Redo할 명령이 있는지.</summary>
    public bool CanRedo => _redo.Count > 0;
    /// <summary>Undo 목록 길이.</summary>
    public int UndoCount => _undo.Count;
    /// <summary>Redo 목록 길이.</summary>
    public int RedoCount => _redo.Count;
    /// <summary>다음 Undo 대상 이름(메뉴 표시용).</summary>
    public string? UndoName => CanUndo ? _undo[^1].Name : null;
    /// <summary>다음 Redo 대상 이름(메뉴 표시용).</summary>
    public string? RedoName => CanRedo ? _redo[^1].Name : null;
    /// <summary>가장 최근에 들어간 명령. Action Popup이 실행 전후 비교로 새 명령을 감지할 때 쓴다.</summary>
    public ICommand? LastCommand => CanUndo ? _undo[^1] : null;
    /// <summary>그룹이 열려 있는지.</summary>
    public bool IsGrouping => _groupDepth > 0;

    /// <summary>명령을 실행(또는 이미 적용됐으면 건너뜀)하고 스택에 넣는다.</summary>
    /// <param name="cmd">넣을 명령.</param>
    /// <param name="alreadyApplied">true면 Do를 부르지 않고 <see cref="IAppliedHook"/>만 호출한다(드래그 커밋 등).</param>
    public void Push(ICommand cmd, bool alreadyApplied = false)
    {
        // 1) 실행: 아직 적용 안 됐으면 Do, 이미 적용됐으면 훅(히스토리 등록 등)만.
        if (!alreadyApplied) cmd.Do(_doc);
        else if (cmd is IAppliedHook hook) hook.OnPushedApplied(_doc);
        // 2) 그룹 중이면 그룹에 모으고 끝(그룹이 닫힐 때 한 번에 스택에 들어간다).
        if (_group != null) { _group.Add(cmd); return; }
        // 3) 스택에 넣고 Redo 무효화, 최대 단계 초과분은 앞에서부터 버림.
        _undo.Add(cmd);
        _redo.Clear();
        while (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        Changed?.Invoke();
    }

    /// <summary>가장 최근 명령을 되돌려 Redo 목록으로 옮긴다. 그룹 중이거나 비어 있으면 false.</summary>
    public bool Undo()
    {
        if (_groupDepth > 0 || _undo.Count == 0) return false;
        var c = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        c.Undo(_doc);
        _redo.Add(c);
        Changed?.Invoke();
        return true;
    }

    /// <summary>직전에 되돌린 명령을 다시 실행해 Undo 목록으로 옮긴다. 그룹 중이거나 비어 있으면 false.</summary>
    public bool Redo()
    {
        if (_groupDepth > 0 || _redo.Count == 0) return false;
        var c = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        c.Do(_doc);
        _undo.Add(c);
        Changed?.Invoke();
        return true;
    }

    /// <summary>모든 이력과 열린 그룹을 버린다(새 문서/열기 시).</summary>
    public void Clear()
    {
        _undo.Clear(); _redo.Clear(); _group = null; _groupDepth = 0;
        Changed?.Invoke();
    }

    /// <summary>그룹을 시작한다. Dispose 시 그룹 안의 명령이 하나로 묶여 스택에 들어간다(비어 있으면 무시).</summary>
    /// <remarks><c>using (undo.BeginGroup("X")) { ... }</c> 형태로 쓴다. 중첩되면 가장 바깥 그룹의 이름만 남는다.</remarks>
    public IDisposable BeginGroup(string name)
    {
        // 가장 바깥 BeginGroup에서만 새 CompoundCommand를 만든다.
        if (_groupDepth++ == 0) _group = new CompoundCommand(name);
        return new GroupScope(this);
    }

    /// <summary>그룹 깊이를 줄이고, 가장 바깥 그룹이 닫히면 모인 명령을 스택에 넣는다.</summary>
    private void EndGroup()
    {
        if (--_groupDepth > 0) return;
        var g = _group!; _group = null;
        // 아무것도 안 했으면 빈 스텝을 만들지 않는다.
        if (g.Count == 0) return;
        // 명령이 하나뿐이면 감싸지 않고 그 명령 자체를 넣는다(LastCommand 타입 검사가 동작하도록).
        _undo.Add(g.Count == 1 ? g.Single : g);
        _redo.Clear();
        while (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        Changed?.Invoke();
    }

    /// <summary>BeginGroup이 돌려주는 IDisposable. Dispose를 여러 번 불러도 EndGroup은 한 번만 호출된다.</summary>
    private sealed class GroupScope : IDisposable
    {
        /// <summary>대상 스택. Dispose 후 null로 만들어 중복 종료를 막는다.</summary>
        private UndoStack? _s;
        /// <summary>스택을 기억한다.</summary>
        public GroupScope(UndoStack s) { _s = s; }
        /// <summary>그룹을 닫는다(한 번만).</summary>
        public void Dispose() { _s?.EndGroup(); _s = null; }
    }
}

/// <summary>여러 명령을 하나의 Undo 스텝으로 묶는다. Undo는 역순.</summary>
public sealed class CompoundCommand : ICommand
{
    /// <summary>들어간 순서대로의 하위 명령.</summary>
    private readonly List<ICommand> _items = new();
    /// <summary>그룹 이름(Undo 메뉴 표시).</summary>
    public string Name { get; }
    /// <summary>하위 명령 수.</summary>
    public int Count => _items.Count;
    /// <summary>첫 번째 하위 명령(Count == 1일 때 그룹을 풀어 넣는 용도).</summary>
    public ICommand Single => _items[0];
    /// <summary>하위 명령 목록(읽기 전용). Action Popup 등이 안쪽 명령을 살펴볼 때 쓴다.</summary>
    public IReadOnlyList<ICommand> Items => _items;

    /// <summary>이름만 가진 빈 묶음을 만든다.</summary>
    public CompoundCommand(string name) { Name = name; }
    /// <summary>하위 명령을 끝에 추가한다(이미 적용된 상태로 들어온다).</summary>
    public void Add(ICommand c) => _items.Add(c);

    /// <summary>하위 명령을 들어간 순서대로 다시 실행한다.</summary>
    public void Do(Document doc) { foreach (var c in _items) c.Do(doc); }
    /// <summary>하위 명령을 역순으로 되돌린다(뒤 명령이 앞 명령 결과에 의존하므로).</summary>
    public void Undo(Document doc) { for (int i = _items.Count - 1; i >= 0; i--) _items[i].Undo(doc); }
}
