using Godot;

namespace Cube.App.UI;

/// <remarks>
/// 값은 입력이 바뀔 때마다 즉시 <c>Settings.Bridge</c>에 써 넣고 설정 파일을 저장한다(별도 OK 버튼 없음).
/// 레이아웃: 4열 그리드(라벨 / 경로 입력 / Browse / Detect) + API 키 줄 + 자동 다시 읽기 체크 + 앱별 연동 방식 설명 + 브리지 폴더 열기.
/// </remarks>
/// <summary>Bridge → Bridge Settings: 외부 앱 실행 파일 경로(찾아보기/자동 감지), Tripo3D API 키, 자동 다시 읽기.</summary>
public partial class BridgeSettingsWindow : FloatingPanel
{
    /// <summary>도움말 줄 표시와 파일 다이얼로그의 부모로 쓰는 셸.</summary>
    private Shell _shell = null!;
    /// <summary>앱별 실행 파일 경로 입력칸(Browse/Detect 결과를 반영하기 위해 보관).</summary>
    private readonly Dictionary<BridgeApp, LineEdit> _paths = new();
    /// <summary>Tripo3D API 키 입력칸(기본은 가려진 Secret 모드).</summary>
    private LineEdit _apiKey = null!;
    /// <summary>브리지 파일 변경 시 자동 다시 읽기 체크박스.</summary>
    private CheckBox _auto = null!;

    /// <summary>패널 내용을 만든다. 셸이 패널을 처음 만들 때 한 번 호출한다.</summary>
    /// <param name="shell">소유 셸.</param>
    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "Bridge Settings";
        Size = new Vector2(620 * s, 360 * s);
        MinPanelSize = new Vector2(480 * s, 280 * s);
        var b = CubeApp.Instance.Settings.Bridge;

        // 세로 스택: 그리드 → 자동 다시 읽기 → 설명 → 버튼 줄.
        var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", (int)(6 * s));
        Content.AddChild(box);
        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", (int)(8 * s));
        grid.AddThemeConstantOverride("v_separation", (int)(6 * s));
        box.AddChild(grid);

        // 실행 파일이 필요한 앱마다 한 줄: 라벨, 경로 입력(바뀌면 즉시 저장), 파일 찾아보기, 설치 폴더 자동 감지.
        foreach (var app in new[] { BridgeApp.RizomUv, BridgeApp.Marmoset, BridgeApp.Cascadeur }) // Blender는 실행하지 않고 애드온이 받으므로 경로 불필요
        {
            // 람다가 반복 변수를 캡처하지 않도록 지역 복사본을 둔다.
            var a = app;
            grid.AddChild(new Label { Text = Shell.AppLabel(app) });
            var le = new LineEdit { Text = Get(b, app) ?? "", PlaceholderText = "executable path", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260 * s, 0) };
            le.TextChanged += t => { Set(CubeApp.Instance.Settings.Bridge, a, t); CubeApp.Instance.Settings.Save(); };
            grid.AddChild(le);
            _paths[app] = le;
            var browse = new Button { Text = "Browse...", FocusMode = Control.FocusModeEnum.None };
            browse.Pressed += () => Browse(a);
            grid.AddChild(browse);
            var detect = new Button { Text = "Detect", FocusMode = Control.FocusModeEnum.None, TooltipText = "Search common install folders" };
            // Program Files 등 흔한 설치 경로에서 실행 파일을 찾아 성공하면 입력칸과 설정을 갱신하고 결과를 도움말 줄에 알린다.
            detect.Pressed += () =>
            {
                var p = Shell.DetectExe(a);
                if (p != null) { le.Text = p; Set(CubeApp.Instance.Settings.Bridge, a, p); CubeApp.Instance.Settings.Save(); _shell.HelpLine.Text = $"{Shell.AppLabel(a)}: {p}"; }
                else _shell.HelpLine.Text = $"{Shell.AppLabel(a)} was not found in the usual install folders.";
            };
            grid.AddChild(detect);
        }

        // Tripo3D API 키: 가려진 입력 + 보이기 토글 + 키 발급 페이지 열기.
        grid.AddChild(new Label { Text = "Tripo3D API key" });
        _apiKey = new LineEdit { Text = b.TripoApiKey ?? "", Secret = true, PlaceholderText = "tsk_...", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _apiKey.TextChanged += t => { CubeApp.Instance.Settings.Bridge.TripoApiKey = t; CubeApp.Instance.Settings.Save(); };
        grid.AddChild(_apiKey);
        var show = new Button { Text = "Show", ToggleMode = true, FocusMode = Control.FocusModeEnum.None };
        show.Toggled += v => _apiKey.Secret = !v;
        grid.AddChild(show);
        var site = new Button { Text = "Get key", FocusMode = Control.FocusModeEnum.None, TooltipText = "platform.tripo3d.ai" };
        site.Pressed += () => OS.ShellOpen("https://developers.tripo3d.ai/ko/keys");
        grid.AddChild(site);

        // 자동 다시 읽기: 끄면 Bridge → Reload로 수동으로 가져와야 한다.
        _auto = new CheckBox { Text = "Reload automatically when the bridge file changes (otherwise Bridge → Reload)", ButtonPressed = b.AutoReload, FocusMode = Control.FocusModeEnum.None };
        _auto.Toggled += v => { CubeApp.Instance.Settings.Bridge.AutoReload = v; CubeApp.Instance.Settings.Save(); };
        box.AddChild(_auto);

        // 각 앱과 어떤 형식으로 주고받는지 안내하는 설명문.
        var note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text =
            "Blender: install the Cube Bridge add-on (Bridge → Add-ons); 'Send to Blender' writes cube_bridge.fbx which the add-on auto-receives, and its 'Send to Cube' returns OBJ + origins.  RizomUV: OBJ, only UVs come back (same topology).  " +
            "Marmoset Toolbag: FBX with materials/textures (send only).  Cascadeur: FBX with skeleton/skin; export back to the same file.  " +
            "Tripo3D: text-to-model via the Tripo API, the result is imported as glTF." };
        note.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        box.AddChild(note);

        // 오른쪽 정렬 버튼 줄: 브리지 파일 폴더(user://bridge)를 탐색기로 연다.
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var folder = new Button { Text = "Open Bridge Folder", FocusMode = Control.FocusModeEnum.None };
        folder.Pressed += () => OS.ShellOpen(Shell.BridgeDir(null));
        row.AddChild(folder);
        box.AddChild(row);
    }

    /// <summary>네이티브 파일 다이얼로그로 .exe를 골라 경로 입력칸과 설정에 저장한다. 다이얼로그는 닫히면 해제된다.</summary>
    private void Browse(BridgeApp app)
    {
        var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = $"{Shell.AppLabel(app)} executable" };
        fd.AddFilter("*.exe", "Executable");
        _shell.AddChild(fd);
        fd.FileSelected += p => { _paths[app].Text = p; Set(CubeApp.Instance.Settings.Bridge, app, p); CubeApp.Instance.Settings.Save(); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    }

    /// <summary>설정 객체에서 앱에 해당하는 실행 파일 경로를 읽는다(그 외 앱은 Cascadeur로 취급).</summary>
    private static string? Get(BridgeSettings b, BridgeApp app) => app switch { BridgeApp.Blender => b.BlenderPath, BridgeApp.RizomUv => b.RizomUvPath, BridgeApp.Marmoset => b.MarmosetPath, _ => b.CascadeurPath };
    /// <summary>설정 객체의 앱별 실행 파일 경로 필드에 값을 쓴다(저장은 호출자가 한다).</summary>
    private static void Set(BridgeSettings b, BridgeApp app, string? p)
    {
        switch (app) { case BridgeApp.Blender: b.BlenderPath = p; break; case BridgeApp.RizomUv: b.RizomUvPath = p; break; case BridgeApp.Marmoset: b.MarmosetPath = p; break; default: b.CascadeurPath = p; break; }
    }
}
