using Godot;

namespace Cube.App;

public enum LogLevel { Info, Warning, Error, Status }

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Text)
{
    public string Format() => $"{Time:HH:mm:ss.fff} [{LevelTag(Level)}] {Text}";
    public static string LevelTag(LogLevel l) => l switch { LogLevel.Warning => "WARN", LogLevel.Error => "ERROR", LogLevel.Status => "STATUS", _ => "INFO" };
}

/// <summary>
/// 앱 실행 중 로그(GD.Print/PrintErr, 엔진 경고·오류, C# 예외, 헬프 라인 메시지)를 모은다. Logger 콜백은 어느 스레드에서나
/// 불릴 수 있으므로 잠금 아래 링 버퍼에 넣고, UI는 <see cref="Version"/>을 보고 새 항목을 가져간다.
/// 콜백 안에서 GD.Print를 부르면 재귀하므로 절대 출력하지 않는다.
/// </summary>
public partial class LogCapture : Logger
{
    public const int MaxEntries = 10000;
    public static LogCapture? Instance { get; private set; }

    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _entries = new();
    private long _version, _total;
    private int _errors, _warnings;

    /// <summary>항목이 추가·삭제될 때마다 증가(UI 폴링용).</summary>
    public long Version { get { lock (_lock) return _version; } }
    /// <summary>지금까지 추가된 항목 수(지운 뒤에도 증가만 함; UI의 증분 추가용).</summary>
    public long Total { get { lock (_lock) return _total; } }
    public (int errors, int warnings) Counts { get { lock (_lock) return (_errors, _warnings); } }

    public static void Install()
    {
        if (Instance != null) return;
        Instance = new LogCapture();
        OS.AddLogger(Instance);
    }

    public override void _LogMessage(string message, bool error)
    {
        foreach (var line in Lines(message)) Add(error ? LogLevel.Error : LogLevel.Info, line);
    }

    public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        // errorType: 0 = ERROR, 1 = WARNING, 2 = SCRIPT, 3 = SHADER
        var level = errorType == 1 ? LogLevel.Warning : LogLevel.Error;
        string msg = string.IsNullOrEmpty(rationale) ? code : rationale;
        string where = string.IsNullOrEmpty(file) ? "" : $"  ({System.IO.Path.GetFileName(file)}:{line}{(string.IsNullOrEmpty(function) ? "" : " " + function)})";
        Add(level, msg + where);
    }

    /// <summary>앱 메시지(헬프 라인 등)를 직접 넣는다.</summary>
    public static void Write(LogLevel level, string text) { if (Instance != null) foreach (var l in Lines(text)) Instance.Add(level, l); }

    private static IEnumerable<string> Lines(string s)
    {
        foreach (var l in s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) yield return l;
    }

    private void Add(LogLevel level, string text)
    {
        lock (_lock)
        {
            _entries.AddLast(new LogEntry(DateTime.Now, level, text));
            if (level == LogLevel.Error) _errors++; else if (level == LogLevel.Warning) _warnings++;
            while (_entries.Count > MaxEntries)
            {
                var first = _entries.First!.Value;
                if (first.Level == LogLevel.Error) _errors--; else if (first.Level == LogLevel.Warning) _warnings--;
                _entries.RemoveFirst();
            }
            _version++; _total++;
        }
    }

    /// <summary>현재 항목 전체 사본.</summary>
    public List<LogEntry> Snapshot() { lock (_lock) return _entries.ToList(); }

    /// <summary>total 이후에 추가된 항목(버퍼에서 밀려난 것은 빠짐).</summary>
    public List<LogEntry> Since(long total)
    {
        lock (_lock)
        {
            long n = Math.Min(_total - total, _entries.Count);
            var list = new List<LogEntry>((int)Math.Max(0, n));
            if (n <= 0) return list;
            var node = _entries.Last;
            for (long i = 1; i < n && node != null; i++) node = node.Previous;
            for (; node != null; node = node.Next) list.Add(node.Value);
            return list;
        }
    }

    public void Clear() { lock (_lock) { _entries.Clear(); _errors = _warnings = 0; _version++; } }
}
