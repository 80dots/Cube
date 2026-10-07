using Godot;

namespace Cube.App.UI;

/// <summary>Bridge → Bridge Settings: 외부 앱 실행 파일 경로(찾아보기/자동 감지), Tripo3D API 키, 자동 다시 읽기.</summary>
public partial class BridgeSettingsWindow : FloatingPanel
{
    private Shell _shell = null!;
    private readonly Dictionary<BridgeApp, LineEdit> _paths = new();
    private LineEdit _apiKey = null!;
    private CheckBox _auto = null!;

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "Bridge Settings";
        Size = new Vector2(620 * s, 360 * s);
        MinPanelSize = new Vector2(480 * s, 280 * s);
        var b = CubeApp.Instance.Settings.Bridge;

        var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", (int)(6 * s));
        Content.AddChild(box);
        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", (int)(8 * s));
        grid.AddThemeConstantOverride("v_separation", (int)(6 * s));
        box.AddChild(grid);

        foreach (var app in new[] { BridgeApp.RizomUv, BridgeApp.Marmoset, BridgeApp.Cascadeur }) // Blender는 실행하지 않고 애드온이 받으므로 경로 불필요
        {
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
            detect.Pressed += () =>
            {
                var p = Shell.DetectExe(a);
                if (p != null) { le.Text = p; Set(CubeApp.Instance.Settings.Bridge, a, p); CubeApp.Instance.Settings.Save(); _shell.HelpLine.Text = $"{Shell.AppLabel(a)}: {p}"; }
                else _shell.HelpLine.Text = $"{Shell.AppLabel(a)} was not found in the usual install folders.";
            };
            grid.AddChild(detect);
        }

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

        _auto = new CheckBox { Text = "Reload automatically when the bridge file changes (otherwise Bridge → Reload)", ButtonPressed = b.AutoReload, FocusMode = Control.FocusModeEnum.None };
        _auto.Toggled += v => { CubeApp.Instance.Settings.Bridge.AutoReload = v; CubeApp.Instance.Settings.Save(); };
        box.AddChild(_auto);

        var note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text =
            "Blender: install the Cube Bridge add-on (Bridge → Add-ons); 'Send to Blender' writes cube_bridge.fbx which the add-on auto-receives, and its 'Send to Cube' returns OBJ + origins.  RizomUV: OBJ, only UVs come back (same topology).  " +
            "Marmoset Toolbag: FBX with materials/textures (send only).  Cascadeur: FBX with skeleton/skin; export back to the same file.  " +
            "Tripo3D: text-to-model via the Tripo API, the result is imported as glTF." };
        note.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        box.AddChild(note);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var folder = new Button { Text = "Open Bridge Folder", FocusMode = Control.FocusModeEnum.None };
        folder.Pressed += () => OS.ShellOpen(Shell.BridgeDir(null));
        row.AddChild(folder);
        box.AddChild(row);
    }

    private void Browse(BridgeApp app)
    {
        var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = $"{Shell.AppLabel(app)} executable" };
        fd.AddFilter("*.exe", "Executable");
        _shell.AddChild(fd);
        fd.FileSelected += p => { _paths[app].Text = p; Set(CubeApp.Instance.Settings.Bridge, app, p); CubeApp.Instance.Settings.Save(); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    }

    private static string? Get(BridgeSettings b, BridgeApp app) => app switch { BridgeApp.Blender => b.BlenderPath, BridgeApp.RizomUv => b.RizomUvPath, BridgeApp.Marmoset => b.MarmosetPath, _ => b.CascadeurPath };
    private static void Set(BridgeSettings b, BridgeApp app, string? p)
    {
        switch (app) { case BridgeApp.Blender: b.BlenderPath = p; break; case BridgeApp.RizomUv: b.RizomUvPath = p; break; case BridgeApp.Marmoset: b.MarmosetPath = p; break; default: b.CascadeurPath = p; break; }
    }
}
