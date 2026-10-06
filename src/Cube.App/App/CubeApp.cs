using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;

namespace Cube.App;

/// <summary>
/// 앱 전역 autoload. Hi-DPI 배율, 현재 문서 등 전역 서비스를 소유한다.
/// </summary>
public partial class CubeApp : Node
{
    public static CubeApp Instance { get; private set; } = null!;

    /// <summary>화면 배율. 픽셀 단위 상수(점 크기, 피킹 임계값)에 곱해 쓴다.</summary>
    public float UiScale { get; private set; } = 1f;

    public Document Document { get; private set; } = null!;

    public override void _Ready()
    {
        Instance = this;
        UiScale = (float)DisplayServer.ScreenGetScale();
        GetWindow().ContentScaleFactor = UiScale;

        Document = new Document();
        // 개발 중 확인용 기본 큐브. 셸프/메뉴로 프리미티브를 만들 수 있게 되면 제거한다.
        Document.Undo.Push(CreatePrimitiveCommand.Cube(Document));
        Document.Undo.Clear();
        Document.IsDirty = false;

        GD.Print($"[Cube] core={Core.CoreInfo.Name} uiScale={UiScale} nodes={Document.Nodes.Count}");

        ParseDebugArgs();
    }

    // ---------------------------------------------------------------- 개발용 커맨드라인 인자
    // godot --path . -- --screenshot=C:/tmp/shot.png --quit-after=10
    private string? _screenshotPath;
    private int _quitAfterFrames = -1;
    private int _frame;

    private void ParseDebugArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--screenshot=")) _screenshotPath = a["--screenshot=".Length..];
            else if (a.StartsWith("--quit-after=") && int.TryParse(a["--quit-after=".Length..], out int n)) _quitAfterFrames = n;
        }
        if (_screenshotPath != null && _quitAfterFrames < 0) _quitAfterFrames = 8;
    }

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
        if (_frame >= _quitAfterFrames) GetTree().Quit(0);
    }
}
