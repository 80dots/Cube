using System.Diagnostics;
using Godot;

namespace Cube.App;

/// <summary>
/// `-- --uiperf`: UI 드래그(패널 이동/크기 조절/도크 경계) 성능 측정. DebugDriver `perf start LABEL` … `perf stop` 사이의
/// 프레임 시간(평균/최대/p95)과 <see cref="Begin"/>/<see cref="End"/>로 감싼 단계별 누적 시간, <see cref="Count"/> 횟수를 `[UiPerf]` 줄로 찍는다(vsync 끔).
/// 꺼져 있으면 비용이 거의 없다.
/// </summary>
public static class UiPerf
{
    /// <summary>커맨드라인 사용자 인자에 --uiperf가 있으면 true.</summary>
    public static readonly bool Enabled = OS.GetCmdlineUserArgs().Contains("--uiperf");
    /// <summary>계측 키 → 측정 구간 누적 ms.</summary>
    private static readonly Dictionary<string, double> _acc = new();
    /// <summary>계측 키 → 호출 횟수(End 또는 Count).</summary>
    private static readonly Dictionary<string, int> _cnt = new();
    /// <summary>측정 구간의 프레임 간격(ms) 목록(평균·최대·p95 계산용).</summary>
    private static readonly List<double> _frames = new();
    /// <summary>직전 Tick 타임스탬프(0 = 없음).</summary>
    private static long _lastFrame;
    /// <summary>측정 중인 구간 이름(null = 측정 중 아님).</summary>
    private static string? _label;
    /// <summary>vsync를 이미 껐는지.</summary>
    private static bool _vsyncOff;

    /// <summary>측정 구간이 진행 중인지.</summary>
    public static bool Active => _label != null;

    /// <summary>계측 시작 타임스탬프(꺼져 있으면 0).</summary>
    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>측정 중이면 <paramref name="t0"/>부터의 경과 ms를 <paramref name="key"/>에 누적하고 횟수를 센다.</summary>
    public static void End(string key, long t0)
    {
        if (!Enabled || _label == null) return;
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        _acc[key] = _acc.TryGetValue(key, out var a) ? a + ms : ms;
        _cnt[key] = _cnt.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    /// <summary>측정 중이면 <paramref name="key"/>의 횟수만 하나 늘린다(시간 없이 발생 빈도만 볼 때).</summary>
    public static void Count(string key)
    {
        if (!Enabled || _label == null) return;
        _cnt[key] = _cnt.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    /// <summary>측정 구간을 시작한다: vsync를 끄고(처음 한 번) 누적을 비운다.</summary>
    public static void Start(string label)
    {
        if (!Enabled) return;
        if (!_vsyncOff) { DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); _vsyncOff = true; }
        _label = label; _acc.Clear(); _cnt.Clear(); _frames.Clear(); _lastFrame = 0;
    }

    /// <summary>프레임마다 한 번(DebugDriver._Process).</summary>
    public static void Tick()
    {
        if (!Enabled || _label == null) return;
        long now = Stopwatch.GetTimestamp();
        if (_lastFrame != 0) _frames.Add((now - _lastFrame) * 1000.0 / Stopwatch.Frequency);
        _lastFrame = now;
    }

    /// <summary>측정을 끝내고 프레임 통계(평균/최대/p95)와 키별 누적 시간·횟수를 한 줄로 출력한다.</summary>
    public static void Stop()
    {
        if (!Enabled || _label == null) return;
        int n = _frames.Count;
        var sorted = _frames.OrderBy(x => x).ToList();
        // p95 = 정렬된 프레임 시간의 95번째 백분위 값
        double avg = n > 0 ? _frames.Sum() / n : 0, max = n > 0 ? sorted[^1] : 0, p95 = n > 0 ? sorted[(int)Math.Min(n - 1, Math.Floor(n * 0.95))] : 0;
        var parts = new List<string>();
        // 시간 계측 키는 "키=총ms/횟수", Count 전용 키는 "키xN"
        foreach (var (k, v) in _acc.OrderByDescending(kv => kv.Value)) parts.Add($"{k}={v:F2}ms/{_cnt[k]}");
        foreach (var (k, c) in _cnt) if (!_acc.ContainsKey(k)) parts.Add($"{k}x{c}");
        GD.Print($"[UiPerf] {_label}: frames={n} avg={avg:F2}ms max={max:F2}ms p95={p95:F2}ms | {string.Join(" ", parts)}");
        _label = null;
    }
}
