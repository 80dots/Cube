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
    public static readonly bool Enabled = OS.GetCmdlineUserArgs().Contains("--uiperf");
    private static readonly Dictionary<string, double> _acc = new();
    private static readonly Dictionary<string, int> _cnt = new();
    private static readonly List<double> _frames = new();
    private static long _lastFrame;
    private static string? _label;
    private static bool _vsyncOff;

    public static bool Active => _label != null;

    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void End(string key, long t0)
    {
        if (!Enabled || _label == null) return;
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        _acc[key] = _acc.TryGetValue(key, out var a) ? a + ms : ms;
        _cnt[key] = _cnt.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    public static void Count(string key)
    {
        if (!Enabled || _label == null) return;
        _cnt[key] = _cnt.TryGetValue(key, out var c) ? c + 1 : 1;
    }

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

    public static void Stop()
    {
        if (!Enabled || _label == null) return;
        int n = _frames.Count;
        var sorted = _frames.OrderBy(x => x).ToList();
        double avg = n > 0 ? _frames.Sum() / n : 0, max = n > 0 ? sorted[^1] : 0, p95 = n > 0 ? sorted[(int)Math.Min(n - 1, Math.Floor(n * 0.95))] : 0;
        var parts = new List<string>();
        foreach (var (k, v) in _acc.OrderByDescending(kv => kv.Value)) parts.Add($"{k}={v:F2}ms/{_cnt[k]}");
        foreach (var (k, c) in _cnt) if (!_acc.ContainsKey(k)) parts.Add($"{k}x{c}");
        GD.Print($"[UiPerf] {_label}: frames={n} avg={avg:F2}ms max={max:F2}ms p95={p95:F2}ms | {string.Join(" ", parts)}");
        _label = null;
    }
}
