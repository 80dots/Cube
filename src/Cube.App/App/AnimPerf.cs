using System.Diagnostics;
using Godot;

namespace Cube.App;

/// <summary>
/// `-- --animperf`: 애니메이션 재생 중 단계별 소요 시간(ms/프레임)과 fps를 60프레임마다 `[AnimPerf]` 줄로 찍는다(vsync 끔).
/// 계측 지점은 <see cref="Begin"/>/<see cref="End"/>로 감싼다. 꺼져 있으면 비용이 거의 없다(타임스탬프 호출 없음).
/// </summary>
public static class AnimPerf
{
    public static readonly bool Enabled = OS.GetCmdlineUserArgs().Contains("--animperf");
    private static readonly Dictionary<string, double> _acc = new();
    private static readonly Dictionary<string, int> _cnt = new();
    private static int _frames;
    private static long _lastFrame;
    private static double _frameMs;
    private static bool _vsyncOff;

    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void End(string key, long t0)
    {
        if (!Enabled) return;
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        _acc[key] = _acc.TryGetValue(key, out var a) ? a + ms : ms;
        _cnt[key] = _cnt.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    /// <summary>재생 프레임마다 한 번(AnimationPlayback._Process). 60프레임마다 평균을 찍고 누적을 비운다.</summary>
    public static void Frame(string context)
    {
        if (!Enabled) return;
        if (!_vsyncOff) { DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); _vsyncOff = true; }
        long now = Stopwatch.GetTimestamp();
        if (_lastFrame != 0) _frameMs += (now - _lastFrame) * 1000.0 / Stopwatch.Frequency;
        _lastFrame = now;
        _frames++;
        if (_frames < 60) return;
        var parts = new List<string>();
        foreach (var (k, v) in _acc.OrderByDescending(kv => kv.Value))
            parts.Add($"{k}={v / _frames:F2}ms" + (_cnt[k] != _frames ? $"(x{_cnt[k] / (float)_frames:0.#})" : ""));
        GD.Print($"[AnimPerf] {context} frame={_frameMs / _frames:F2}ms fps={Engine.GetFramesPerSecond():F0} | {string.Join(" ", parts)}");
        _acc.Clear(); _cnt.Clear(); _frames = 0; _frameMs = 0;
    }
}
