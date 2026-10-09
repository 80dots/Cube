using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;

namespace Cube.App;

/// <summary>
/// 앱 전역 autoload. Hi-DPI 배율, 현재 문서, 설정 등 전역 서비스를 소유한다.
/// </summary>
/// <remarks>
/// project.godot의 autoload로 등록되어 Shell 씬보다 먼저 만들어지며 셸을 다시 만들어도(ReloadShell) 살아남는다.
/// 그래서 문서·설정처럼 셸 수명보다 길어야 하는 상태를 여기에 둔다. 개발용 커맨드라인 인자(--with-cube, --screenshot,
/// --quit-after, --drive)도 여기서 처리한다.
/// </remarks>
public partial class CubeApp : Node
{
    /// <summary>싱글턴 인스턴스(_Ready에서 설정). 어디서나 CubeApp.Instance.Document 등으로 접근한다.</summary>
    public static CubeApp Instance { get; private set; } = null!;

    /// <summary>화면 배율. 픽셀 단위 상수(점 크기, 피킹 임계값)에 곱해 쓴다.</summary>
    public float UiScale { get; private set; } = 1f;

    /// <summary>현재 편집 중인 장면 문서(Undo·선택 포함). 앱 수명 동안 같은 객체이며 New/Open은 내용만 바꾼다.</summary>
    public Document Document { get; private set; } = null!;
    /// <summary>사용자 설정(user://settings.json).</summary>
    public Settings Settings { get; private set; } = null!;

    /// <summary>트리에 들어올 때 로그 수집기를 가장 먼저 설치한다.</summary>
    public override void _EnterTree()
    {
        // 로그 패널용: 가능한 한 먼저 로거를 등록해 시작 이후 모든 출력·경고·오류를 모은다
        LogCapture.Install();
    }

    /// <summary>설정 로드·마이그레이션 → UI 배율 계산 → 빈 문서 생성 → 디버그 인자 처리.</summary>
    public override void _Ready()
    {
        Instance = this;
        // 설정을 읽고 예전 버전 설정 값을 한 번 보정한다
        Settings = Settings.Load();
        Settings.Migrate();
        ApplyUiScale();
        // 빈 문서로 시작(--with-cube면 아래에서 큐브를 넣는다)
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
        // 옛 셸의 패널·뷰·툴이 문서 이벤트에 남긴 구독을 지운다(문서는 유지되므로 그대로 두면 해제된 컨트롤을 건드려 Undo 중 예외가 났음)
        Document.ClearEventSubscribers();
        ApplyUiScale();
        GetTree().ReloadCurrentScene();
    }

    // 종료 확인과 설정 저장은 Shell이 처리한다(AutoAcceptQuit=false).

    // ---------------------------------------------------------------- 개발용 커맨드라인 인자
    // godot --path . -- --with-cube --screenshot=C:/tmp/shot.png --quit-after=10 --drive="..."
    /// <summary>--screenshot=PATH: 종료 직전에 저장할 스크린샷 경로.</summary>
    private string? _screenshotPath;
    /// <summary>--quit-after=N: N프레임 뒤 종료(-1 = 사용 안 함).</summary>
    private int _quitAfterFrames = -1;
    /// <summary>_Process에서 센 프레임 수.</summary>
    private int _frame;

    /// <summary>
    /// 커맨드라인 사용자 인자(-- 뒤)를 해석한다. --drive는 DebugDriver를 지연 생성(셸이 준비된 뒤)하고 핫키 로그를 켠다.
    /// --with-cube는 기본 큐브를 넣은 뒤 Undo 기록과 dirty 플래그를 지워 깨끗한 시작 상태로 만든다.
    /// </summary>
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
        // 스크린샷만 지정하고 종료 프레임이 없으면 8프레임 뒤 찍고 종료
        if (_screenshotPath != null && _quitAfterFrames < 0) _quitAfterFrames = 8;
    }

    /// <summary>드라이브 스크립트를 실행하는 <see cref="DebugDriver"/> 노드를 자식으로 붙인다.</summary>
    private void StartDriver(string script) => AddChild(new DebugDriver(script));

    /// <summary>--quit-after가 지정됐을 때만 프레임을 세어, 마지막 직전 프레임에 스크린샷을 저장하고 마지막 프레임에 설정 저장 후 종료한다.</summary>
    public override void _Process(double delta)
    {
        if (_quitAfterFrames < 0) return;
        _frame++;
        // 종료 한 프레임 전에 현재 화면을 PNG로 저장
        if (_frame == _quitAfterFrames - 1 && _screenshotPath != null)
        {
            var img = GetViewport().GetTexture().GetImage();
            var err = img.SavePng(_screenshotPath);
            GD.Print($"[Cube] screenshot {_screenshotPath}: {err}");
        }
        if (_frame >= _quitAfterFrames) { Settings.Save(); GetTree().Quit(0); } // 디버그 종료는 확인 없이
    }
}
