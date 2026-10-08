using System.Diagnostics;
using Godot;

namespace Cube.App;

/// <summary>
/// `-- --animperf`: 애니메이션 재생 중 단계별 소요 시간(ms/프레임)과 fps를 60프레임마다 `[AnimPerf]` 줄로 찍는다(vsync 끔).
/// 계측 지점은 <see cref="Begin"/>/<see cref="End"/>로 감싼다. 꺼져 있으면 비용이 거의 없다(타임스탬프 호출 없음).
/// </summary>
public static class AnimPerf
{
    /// <summary>커맨드라인 사용자 인자에 --animperf가 있으면 true(시작 시 한 번 결정).</summary>
    public static readonly bool Enabled = OS.GetCmdlineUserArgs().Contains("--animperf");
    /// <summary>계측 키 → 이번 60프레임 구간의 누적 ms.</summary>
    private static readonly Dictionary<string, double> _acc = new();
    /// <summary>계측 키 → 이번 구간의 호출 횟수(프레임당 여러 번 불리는 지점 표시용).</summary>
    private static readonly Dictionary<string, int> _cnt = new();
    /// <summary>이번 구간에 센 프레임 수.</summary>
    private static int _frames;
    /// <summary>직전 Frame 호출의 타임스탬프(0 = 아직 없음).</summary>
    private static long _lastFrame;
    /// <summary>이번 구간의 프레임 간격 누적 ms.</summary>
    private static double _frameMs;
    /// <summary>vsync를 이미 껐는지(측정 fps가 모니터 주사율에 묶이지 않게 처음 한 번 끈다).</summary>
    private static bool _vsyncOff;

    /// <summary>계측 시작 타임스탬프. 꺼져 있으면 0을 돌려주고 시간을 읽지 않는다.</summary>
    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>계측 끝: <paramref name="t0"/>부터 지금까지 걸린 ms를 <paramref name="key"/>에 누적한다.</summary>
    public static void End(string key, long t0)
    {
        if (!Enabled) return;
        // 타임스탬프 차이 → ms
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        _acc[key] = _acc.TryGetValue(key, out var a) ? a + ms : ms;
        _cnt[key] = _cnt.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    /// <summary>재생 프레임마다 한 번(AnimationPlayback._Process). 60프레임마다 평균을 찍고 누적을 비운다.</summary>
    public static void Frame(string context)
    {
        if (!Enabled) return;
        // 처음 한 번 vsync를 끈다
        if (!_vsyncOff) { DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); _vsyncOff = true; }
        long now = Stopwatch.GetTimestamp();
        // 직전 프레임과의 간격을 누적
        if (_lastFrame != 0) _frameMs += (now - _lastFrame) * 1000.0 / Stopwatch.Frequency;
        _lastFrame = now;
        _frames++;
        if (_frames < 60) return;
        // 누적 시간이 큰 순서로 키별 프레임당 평균을 만들고, 프레임당 호출 수가 1이 아니면 (xN)을 붙인다
        var parts = new List<string>();
        foreach (var (k, v) in _acc.OrderByDescending(kv => kv.Value))
            parts.Add($"{k}={v / _frames:F2}ms" + (_cnt[k] != _frames ? $"(x{_cnt[k] / (float)_frames:0.#})" : ""));
        GD.Print($"[AnimPerf] {context} frame={_frameMs / _frames:F2}ms fps={Engine.GetFramesPerSecond():F0} | {string.Join(" ", parts)}");
        // 다음 구간을 위해 누적을 비운다
        _acc.Clear(); _cnt.Clear(); _frames = 0; _frameMs = 0;
    }
}
