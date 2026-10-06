using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>
/// 자체 명령 스택. 드래그처럼 이미 적용된 변경은 <c>alreadyApplied: true</c>로 밀어 넣는다.
/// <see cref="BeginGroup"/> 안에서 밀어 넣은 명령은 하나의 <see cref="CompoundCommand"/>로 합쳐진다.
/// </summary>
public sealed class UndoStack
{
    private readonly Document _doc;
    private readonly List<ICommand> _undo = new();
    private readonly List<ICommand> _redo = new();
    private CompoundCommand? _group;
    private int _groupDepth;

    public int MaxSteps { get; set; } = 100;
    public event Action? Changed;

    public UndoStack(Document doc) { _doc = doc; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public string? UndoName => CanUndo ? _undo[^1].Name : null;
    public string? RedoName => CanRedo ? _redo[^1].Name : null;
    public ICommand? LastCommand => CanUndo ? _undo[^1] : null;
    public bool IsGrouping => _groupDepth > 0;

    /// <summary>명령을 실행(또는 이미 적용됐으면 건너뜀)하고 스택에 넣는다.</summary>
    public void Push(ICommand cmd, bool alreadyApplied = false)
    {
        if (!alreadyApplied) cmd.Do(_doc);
        if (_group != null) { _group.Add(cmd); return; }
        _undo.Add(cmd);
        _redo.Clear();
        while (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_groupDepth > 0 || _undo.Count == 0) return false;
        var c = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        c.Undo(_doc);
        _redo.Add(c);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_groupDepth > 0 || _redo.Count == 0) return false;
        var c = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        c.Do(_doc);
        _undo.Add(c);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        _undo.Clear(); _redo.Clear(); _group = null; _groupDepth = 0;
        Changed?.Invoke();
    }

    /// <summary>그룹을 시작한다. Dispose 시 그룹 안의 명령이 하나로 묶여 스택에 들어간다(비어 있으면 무시).</summary>
    public IDisposable BeginGroup(string name)
    {
        if (_groupDepth++ == 0) _group = new CompoundCommand(name);
        return new GroupScope(this);
    }

    private void EndGroup()
    {
        if (--_groupDepth > 0) return;
        var g = _group!; _group = null;
        if (g.Count == 0) return;
        _undo.Add(g.Count == 1 ? g.Single : g);
        _redo.Clear();
        while (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        Changed?.Invoke();
    }

    private sealed class GroupScope : IDisposable
    {
        private UndoStack? _s;
        public GroupScope(UndoStack s) { _s = s; }
        public void Dispose() { _s?.EndGroup(); _s = null; }
    }
}

/// <summary>여러 명령을 하나의 Undo 스텝으로 묶는다. Undo는 역순.</summary>
public sealed class CompoundCommand : ICommand
{
    private readonly List<ICommand> _items = new();
    public string Name { get; }
    public int Count => _items.Count;
    public ICommand Single => _items[0];
    public IReadOnlyList<ICommand> Items => _items;

    public CompoundCommand(string name) { Name = name; }
    public void Add(ICommand c) => _items.Add(c);

    public void Do(Document doc) { foreach (var c in _items) c.Do(doc); }
    public void Undo(Document doc) { for (int i = _items.Count - 1; i >= 0; i--) _items[i].Undo(doc); }
}
