using Godot;

namespace Cube.App;

/// <summary>
/// 로그 항목 수준. Info = 일반 출력(GD.Print), Warning = 엔진/스크립트 경고, Error = 오류·PrintErr·예외,
/// Status = 앱이 헬프 라인 등에 띄운 상태 메시지(<see cref="LogCapture.Write"/>로 넣음).
/// </summary>
public enum LogLevel { Info, Warning, Error, Status }

/// <summary>로그 한 줄: 기록 시각, 수준, 텍스트(여러 줄 메시지는 줄마다 별도 항목).</summary>
public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Text)
{
    /// <summary>"HH:mm:ss.fff [수준] 텍스트" 형식 문자열(로그 패널 표시·복사용).</summary>
    public string Format() => $"{Time:HH:mm:ss.fff} [{LevelTag(Level)}] {Text}";
    /// <summary>수준 → 표시 태그(INFO/WARN/ERROR/STATUS).</summary>
    public static string LevelTag(LogLevel l) => l switch { LogLevel.Warning => "WARN", LogLevel.Error => "ERROR", LogLevel.Status => "STATUS", _ => "INFO" };
}

/// <summary>
/// 앱 실행 중 로그(GD.Print/PrintErr, 엔진 경고·오류, C# 예외, 헬프 라인 메시지)를 모은다. Logger 콜백은 어느 스레드에서나
/// 불릴 수 있으므로 잠금 아래 링 버퍼에 넣고, UI는 <see cref="Version"/>을 보고 새 항목을 가져간다.
/// 콜백 안에서 GD.Print를 부르면 재귀하므로 절대 출력하지 않는다.
/// </summary>
/// <remarks>
/// Godot 4.5+ <c>Logger</c> 확장: <see cref="Install"/>이 OS.AddLogger로 등록하면 엔진이 모든 메시지를 _LogMessage/_LogError로 넘긴다.
/// 버퍼는 최대 <see cref="MaxEntries"/>개이며 넘치면 가장 오래된 항목부터 버린다(오류/경고 카운트도 함께 보정).
/// </remarks>
public partial class LogCapture : Logger
{
    /// <summary>링 버퍼 최대 항목 수.</summary>
    public const int MaxEntries = 10000;
    /// <summary>설치된 단일 인스턴스(설치 전 null).</summary>
    public static LogCapture? Instance { get; private set; }

    /// <summary>모든 상태 접근을 직렬화하는 잠금 객체(로거 콜백은 다른 스레드에서 올 수 있음).</summary>
    private readonly object _lock = new();
    /// <summary>항목 버퍼(오래된 것이 앞). 끝 추가·앞 제거가 O(1)인 연결 리스트.</summary>
    private readonly LinkedList<LogEntry> _entries = new();
    /// <summary>_version = 변경 카운터, _total = 누적 추가 수.</summary>
    private long _version, _total;
    /// <summary>현재 버퍼에 있는 오류·경고 항목 수(상태 표시 배지용).</summary>
    private int _errors, _warnings;

    /// <summary>항목이 추가·삭제될 때마다 증가(UI 폴링용).</summary>
    public long Version { get { lock (_lock) return _version; } }
    /// <summary>지금까지 추가된 항목 수(지운 뒤에도 증가만 함; UI의 증분 추가용).</summary>
    public long Total { get { lock (_lock) return _total; } }
    /// <summary>현재 버퍼의 (오류 수, 경고 수).</summary>
    public (int errors, int warnings) Counts { get { lock (_lock) return (_errors, _warnings); } }

    /// <summary>로거를 한 번만 만들어 엔진에 등록한다(CubeApp._EnterTree에서 호출).</summary>
    public static void Install()
    {
        if (Instance != null) return;
        Instance = new LogCapture();
        OS.AddLogger(Instance);
    }

    /// <summary>엔진 출력 메시지(GD.Print/PrintErr 등). error면 Error, 아니면 Info로 줄마다 넣는다.</summary>
    public override void _LogMessage(string message, bool error)
    {
        foreach (var line in Lines(message)) Add(error ? LogLevel.Error : LogLevel.Info, line);
    }

    /// <summary>엔진 오류/경고 보고(push_error/push_warning, C# 예외, 셰이더 오류 등). 설명과 "(파일:줄 함수)" 위치를 붙여 넣는다.</summary>
    public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        // errorType: 0 = ERROR, 1 = WARNING, 2 = SCRIPT, 3 = SHADER
        var level = errorType == 1 ? LogLevel.Warning : LogLevel.Error;
        // 설명(rationale)이 있으면 그것을, 없으면 오류 코드 문자열을 메시지로 쓴다
        string msg = string.IsNullOrEmpty(rationale) ? code : rationale;
        string where = string.IsNullOrEmpty(file) ? "" : $"  ({System.IO.Path.GetFileName(file)}:{line}{(string.IsNullOrEmpty(function) ? "" : " " + function)})";
        Add(level, msg + where);
    }

    /// <summary>앱 메시지(헬프 라인 등)를 직접 넣는다.</summary>
    public static void Write(LogLevel level, string text) { if (Instance != null) foreach (var l in Lines(text)) Instance.Add(level, l); }

    /// <summary>CRLF를 정규화하고 끝 개행을 뗀 뒤 줄 단위로 나눈다.</summary>
    private static IEnumerable<string> Lines(string s)
    {
        foreach (var l in s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) yield return l;
    }

    /// <summary>항목 하나를 잠금 아래 추가하고, 용량을 넘으면 앞에서부터 버리며 카운트를 보정한다.</summary>
    private void Add(LogLevel level, string text)
    {
        lock (_lock)
        {
            _entries.AddLast(new LogEntry(DateTime.Now, level, text));
            if (level == LogLevel.Error) _errors++; else if (level == LogLevel.Warning) _warnings++;
            // 최대 개수를 넘으면 가장 오래된 항목을 버리고 그 수준의 카운트를 줄인다
            while (_entries.Count > MaxEntries)
            {
                var first = _entries.First!.Value;
                if (first.Level == LogLevel.Error) _errors--; else if (first.Level == LogLevel.Warning) _warnings--;
                _entries.RemoveFirst();
            }
            // UI 폴링이 변화를 알아채도록 버전과 누적 수를 올린다
            _version++; _total++;
        }
    }

    /// <summary>현재 항목 전체 사본.</summary>
    public List<LogEntry> Snapshot() { lock (_lock) return _entries.ToList(); }

    /// <summary>total 이후에 추가된 항목(버퍼에서 밀려난 것은 빠짐).</summary>
    /// <param name="total">UI가 마지막으로 본 <see cref="Total"/> 값.</param>
    public List<LogEntry> Since(long total)
    {
        lock (_lock)
        {
            // 새 항목 수(버퍼에 남은 수로 제한) 만큼 끝에서 거슬러 올라간 뒤 앞으로 순서대로 모은다
            long n = Math.Min(_total - total, _entries.Count);
            var list = new List<LogEntry>((int)Math.Max(0, n));
            if (n <= 0) return list;
            var node = _entries.Last;
            for (long i = 1; i < n && node != null; i++) node = node.Previous;
            for (; node != null; node = node.Next) list.Add(node.Value);
            return list;
        }
    }

    /// <summary>버퍼와 오류/경고 카운트를 비운다(Total은 유지 — 증분 UI가 Since로 계속 동작하도록).</summary>
    public void Clear() { lock (_lock) { _entries.Clear(); _errors = _warnings = 0; _version++; } }
}
