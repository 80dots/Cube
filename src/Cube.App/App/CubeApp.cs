using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;

namespace Cube.App;

/// <summary>
/// 앱 전역 autoload. Hi-DPI 배율, 현재 문서, 설정 등 전역 서비스를 소유한다.
/// </summary>
public partial class CubeApp : Node
{
    public static CubeApp Instance { get; private set; } = null!;

    /// <summary>화면 배율. 픽셀 단위 상수(점 크기, 피킹 임계값)에 곱해 쓴다.</summary>
    public float UiScale { get; private set; } = 1f;

    public Document Document { get; private set; } = null!;
    public Settings Settings { get; private set; } = null!;

    public override void _EnterTree()
    {
        // 로그 패널용: 가능한 한 먼저 로거를 등록해 시작 이후 모든 출력·경고·오류를 모은다
        LogCapture.Install();
    }

    public override void _Ready()
    {
        Instance = this;
        Settings = Settings.Load();
        Settings.Migrate();
        ApplyUiScale();
        Document = new Document();

        ParseDebugArgs();
        GD.Print($"[Cube] core={Core.CoreInfo.Name} uiScale={UiScale} nodes={Document.Nodes.Count}");
    }

    /// <summary>
    /// UI 배율 = 화면 DPI 배율 × 환경설정 퍼센트. 창의 ContentScaleFactor는 쓰지 않는다(3D 뷰포트가 업스케일되어 흐려짐).
    /// 테마/위젯 크기와 픽셀 상수가 이 값을 곱해 쓴다.
    /// </summary>
    public void ApplyUiScale()
    {
        float dpi = (float)DisplayServer.ScreenGetScale();
        UiScale = dpi * Math.Clamp(Settings.UiScalePercent, 50, 300) / 100f;
    }

    /// <summary>환경설정 변경 후 셸을 다시 만든다(문서는 유지).</summary>
    public void ReloadShell()
    {
        UI.Shell.Instance?.Dock?.SaveLayout(); // 도킹 레이아웃(폭·탭) 유지
        ApplyUiScale();
        GetTree().ReloadCurrentScene();
    }

    // 종료 확인과 설정 저장은 Shell이 처리한다(AutoAcceptQuit=false).

    // ---------------------------------------------------------------- 개발용 커맨드라인 인자
    // godot --path . -- --with-cube --screenshot=C:/tmp/shot.png --quit-after=10 --drive="..."
    private string? _screenshotPath;
    private int _quitAfterFrames = -1;
    private int _frame;

    private void ParseDebugArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--screenshot=")) _screenshotPath = a["--screenshot=".Length..];
            else if (a.StartsWith("--quit-after=") && int.TryParse(a["--quit-after=".Length..], out int n)) _quitAfterFrames = n;
            else if (a.StartsWith("--drive=")) { Hotkeys.ShellInput.Verbose = true; CallDeferred(nameof(StartDriver), a["--drive=".Length..]); }
            else if (a == "--with-cube")
            {
                Document.Undo.Push(CreatePrimitiveCommand.Cube(Document));
                Document.Undo.Clear();
                Document.IsDirty = false;
            }
        }
        if (_screenshotPath != null && _quitAfterFrames < 0) _quitAfterFrames = 8;
    }

    private void StartDriver(string script) => AddChild(new DebugDriver(script));

    public override void _Process(double delta)
    {
        if (_quitAfterFrames < 0) return;
        _frame++;
        if (_frame == _quitAfterFrames - 1 && _screenshotPath != null)
        {
            var img = GetViewport().GetTexture().GetImage();
            var err = img.SavePng(_screenshotPath);
            GD.Print($"[Cube] screenshot {_screenshotPath}: {err}");
        }
        if (_frame >= _quitAfterFrames) { Settings.Save(); GetTree().Quit(0); } // 디버그 종료는 확인 없이
    }
}
