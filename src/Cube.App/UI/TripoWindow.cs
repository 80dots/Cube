using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Cube.App.UI;

/// <summary>Tripo 작업 기록 항목(user://bridge/tripo/tasks.json에 저장).</summary>
public sealed class TripoTaskRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "queued";
    [JsonPropertyName("progress")] public int Progress { get; set; }
    [JsonPropertyName("credits")] public double Credits { get; set; }
    [JsonPropertyName("created")] public string Created { get; set; } = "";
    [JsonPropertyName("modelUrl")] public string? ModelUrl { get; set; }
    [JsonPropertyName("previewUrl")] public string? PreviewUrl { get; set; }
    [JsonPropertyName("imageUrl")] public string? ImageUrl { get; set; }
    [JsonPropertyName("localModel")] public string? LocalModel { get; set; }
    [JsonPropertyName("localPreview")] public string? LocalPreview { get; set; }
    [JsonPropertyName("riggable")] public bool? Riggable { get; set; }
    [JsonPropertyName("rigType")] public string? RigType { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("autoImport")] public bool AutoImport { get; set; }
    [JsonPropertyName("imported")] public bool Imported { get; set; }
    [JsonIgnore] public bool IsDone => Status is "success" or "failed" or "cancelled" or "banned" or "expired" or "unknown";
    [JsonIgnore] public bool HasModel => !string.IsNullOrEmpty(LocalModel) && System.IO.File.Exists(LocalModel);
    [JsonIgnore] public bool IsImageTask => Type is "text_to_image" or "image_to_image";
}

/// <summary>
/// Bridge → Tripo3D → Tripo Editor: Tripo OpenAPI v3를 Cube 안에서 전부 쓰는 플로팅 패널.
/// 탭: Generate(Text/Image/Multiview → Model, Text → Image), Process(Texture/Retopology/Segment/Complete/Convert), Animate(Rig Check/Auto Rig/Retarget), Tasks(기록·미리보기·가져오기).
/// 입력은 작업 ID(기록에서 선택), 업로드한 파일(file_token; Cube 선택 오브젝트를 GLB로 내보내 올릴 수도 있음), URL 중 하나.
/// 작업은 2초마다 폴링하고 성공하면 모델/미리보기를 user://bridge/tripo/에 바로 내려받는다(URL은 5분 뒤 만료).
/// </summary>
public partial class TripoWindow : FloatingPanel
{
    private Shell _shell = null!;
    private TripoClient _client = null!;
    private readonly List<TripoTaskRecord> _tasks = new();
    private readonly Dictionary<string, Texture2D> _thumbs = new();
    private bool _polling;

    // 공통
    private Label _status = null!, _balance = null!;
    private TabContainer _tabs = null!;
    // Generate
    private OptionButton _genMode = null!, _genModel = null!, _texQuality = null!, _geoQuality = null!, _orientation = null!, _imgModel = null!, _imgSize = null!;
    private TextEdit _prompt = null!; private LineEdit _negPrompt = null!;
    private SpinBox _faceLimit = null!, _modelSeed = null!, _texSeed = null!;
    private CheckBox _texture = null!, _pbr = null!, _quad = null!, _smartLowPoly = null!, _autoSize = null!, _faceLimitOn = null!, _seedOn = null!, _autofix = null!, _autoImport = null!, _imgTemplateOn = null!;
    private OptionButton _imgTemplate = null!;
    private Control _promptBox = null!, _imageBox = null!, _multiBox = null!, _modelOptions = null!, _imageOptions = null!;
    private readonly Dictionary<string, (Button pick, Label name, string? token)> _views = new();
    private string? _imageToken; private Label _imageName = null!;
    private LineEdit _imageUrl = null!;
    // Process / Animate 공통 입력
    private OptionButton _inputKind = null!; private OptionButton _inputTask = null!; private LineEdit _inputText = null!; private Button _inputUpload = null!; private Label _inputInfo = null!;
    private string? _uploadedCubeToken; private string _uploadedCubeDesc = "";
    private OptionButton _aInputKind = null!, _aInputTask = null!; private LineEdit _aInputText = null!; private Label _aInputInfo = null!;
    // Process
    private OptionButton _procOp = null!, _texModel = null!, _procTexQuality = null!, _decModel = null!, _segModel = null!, _segGran = null!, _convFormat = null!, _convTexFormat = null!, _fbxPreset = null!, _completeMode = null!;
    private LineEdit _texPrompt = null!, _partNames = null!; private SpinBox _procFaceLimit = null!, _convTexSize = null!;
    private CheckBox _procPbr = null!, _procQuad = null!, _procBake = null!, _procFaceLimitOn = null!, _convPivotBottom = null!, _convPackUv = null!, _convAnim = null!, _segConn = null!;
    private Control _texBox = null!, _decBox = null!, _segBox = null!, _completeBox = null!, _convBox = null!;
    // Animate
    private OptionButton _rigModel = null!, _rigType = null!, _rigSpec = null!, _rigFormat = null!, _retFormat = null!;
    private LineEdit _animations = null!; private CheckBox _animInPlace = null!, _retBake = null!;
    private Label _rigCheckResult = null!;
    // Tasks
    private ItemList _taskList = null!; private TextureRect _preview = null!; private Label _taskInfo = null!;
    private Button _importBtn = null!, _useInputBtn = null!, _openBtn = null!, _deleteBtn = null!, _redownloadBtn = null!;

    private static string TripoDir() { string d = ProjectSettings.GlobalizePath("user://bridge/tripo/"); System.IO.Directory.CreateDirectory(d); return d; }
    private static string TasksFile() => System.IO.Path.Combine(TripoDir(), "tasks.json");
    private static readonly string[] HModels = { "v3.1-20260211", "v3.0-20250812", "v2.5-20250123" };
    private static readonly string[] PModels = { "P1-20260311", "P2-20260801" };

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "Tripo Editor";
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Vector2(MathF.Min(820 * s, host.X * 0.85f), MathF.Min(640 * s, host.Y * 0.85f));
        MinPanelSize = new Vector2(560 * s, 420 * s);
        _client = new TripoClient { Name = "TripoClient" };
        AddChild(_client);

        var root = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", (int)(4 * s));
        Content.AddChild(root);

        _tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        root.AddChild(_tabs);
        BuildGenerateTab(s);
        BuildProcessTab(s);
        BuildAnimateTab(s);
        BuildTasksTab(s);

        var bar = new HBoxContainer();
        _status = new Label { Text = "Ready.", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        bar.AddChild(_status);
        _balance = new Label { Text = "credits: –" };
        _balance.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        bar.AddChild(_balance);
        var refresh = new Button { Text = "⟳", FocusMode = Control.FocusModeEnum.None, TooltipText = "Refresh balance" };
        refresh.Pressed += () => _ = RefreshBalance();
        bar.AddChild(refresh);
        var key = new Button { Text = "API Key...", FocusMode = Control.FocusModeEnum.None };
        key.Pressed += () => _shell.Actions.Invoke("bridge.settings");
        bar.AddChild(key);
        var folder = new Button { Text = "Folder", FocusMode = Control.FocusModeEnum.None, TooltipText = "Open user://bridge/tripo" };
        folder.Pressed += () => OS.ShellOpen(TripoDir());
        bar.AddChild(folder);
        root.AddChild(bar);

        LoadTasks();
        RefreshTaskList();
        RefreshInputTaskLists();
        var poll = new Godot.Timer { Name = "Poll", WaitTime = 2.0, Autostart = true, OneShot = false };
        poll.Timeout += () => _ = PollActive();
        AddChild(poll);
        VisibilityChanged += () => { if (Visible) { SyncKey(); _ = RefreshBalance(); } };
    }

    private void SyncKey() => _client.ApiKey = CubeApp.Instance.Settings.Bridge.TripoApiKey ?? "";

    private void SetStatus(string text) { _status.Text = text; _shell.HelpLine.Text = "Tripo: " + text; }

    // ---------------------------------------------------------------- UI 헬퍼

    private static GridContainer Grid(Control parent, float s, int columns = 2)
    {
        var g = new GridContainer { Columns = columns, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        g.AddThemeConstantOverride("h_separation", (int)(10 * s));
        g.AddThemeConstantOverride("v_separation", (int)(4 * s));
        parent.AddChild(g);
        return g;
    }
    private static OptionButton Option(GridContainer g, string label, string[] items, int selected = 0, float s = 1f)
    {
        g.AddChild(new Label { Text = label });
        var ob = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(180 * s, 0) };
        foreach (var it in items) ob.AddItem(it);
        ob.Selected = Math.Clamp(selected, 0, Math.Max(0, items.Length - 1));
        g.AddChild(ob);
        return ob;
    }
    private static CheckBox Check(GridContainer g, string label, bool value, string? tip = null)
    {
        g.AddChild(new Label { Text = label, TooltipText = tip ?? "" });
        var c = new CheckBox { ButtonPressed = value, FocusMode = Control.FocusModeEnum.None, TooltipText = tip ?? "" };
        g.AddChild(c);
        return c;
    }
    private static (CheckBox on, SpinBox spin) OptionalSpin(GridContainer g, string label, double min, double max, double step, double value, bool on, float s)
    {
        g.AddChild(new Label { Text = label });
        var row = new HBoxContainer();
        var c = new CheckBox { ButtonPressed = on, FocusMode = Control.FocusModeEnum.None, TooltipText = "Send this parameter (otherwise adaptive/random)" };
        var sb = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(130 * s, 0) };
        row.AddChild(c); row.AddChild(sb);
        g.AddChild(row);
        return (c, sb);
    }
    private static LineEdit Line(GridContainer g, string label, string placeholder, float s)
    {
        g.AddChild(new Label { Text = label });
        var le = new LineEdit { PlaceholderText = placeholder, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(220 * s, 0) };
        g.AddChild(le);
        return le;
    }
    private static Label Section(Control parent, string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        parent.AddChild(l);
        parent.AddChild(new HSeparator());
        return l;
    }
    private static ScrollContainer Tab(TabContainer tabs, string title, out VBoxContainer box, float s)
    {
        var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", (int)(6 * s));
        scroll.AddChild(box);
        tabs.AddChild(scroll);
        return scroll;
    }
    private Button Action(Control parent, string text, Func<Task> run)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        b.Pressed += () => _ = Guard(run);
        parent.AddChild(b);
        return b;
    }
    private async Task Guard(Func<Task> run)
    {
        try { SyncKey(); await run(); }
        catch (TripoClient.TripoException ex) { SetStatus(ex.Message); }
        catch (Exception ex) { SetStatus("Error: " + ex.Message); GD.PushWarning("[Tripo] " + ex); }
    }

    // ---------------------------------------------------------------- Generate 탭

    private void BuildGenerateTab(float s)
    {
        Tab(_tabs, "Generate", out var box, s);
        var top = Grid(box, s);
        _genMode = Option(top, "Mode", new[] { "Text → 3D Model", "Image → 3D Model", "Multiview → 3D Model", "Text → Image" }, 0, s);
        _genMode.ItemSelected += _ => UpdateGenerateVisibility();
        _genModel = Option(top, "Model", HModels.Concat(PModels).ToArray(), 0, s);
        _genModel.ItemSelected += _ => UpdateGenerateVisibility();

        _promptBox = new VBoxContainer();
        box.AddChild(_promptBox);
        _promptBox.AddChild(new Label { Text = "Prompt (shape, material, style, scale; up to 1024 chars)" });
        _prompt = new TextEdit { CustomMinimumSize = new Vector2(0, 70 * s), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "A low-poly medieval wooden treasure chest with iron hinges and a rusty lock" };
        _promptBox.AddChild(_prompt);
        var pg = Grid(_promptBox, s);
        _negPrompt = Line(pg, "Negative prompt", "blurry, low quality, broken mesh", s);

        _imageBox = new VBoxContainer();
        box.AddChild(_imageBox);
        var ig = Grid(_imageBox, s);
        ig.AddChild(new Label { Text = "Image" });
        var irow = new HBoxContainer();
        var pick = new Button { Text = "Choose & Upload...", FocusMode = Control.FocusModeEnum.None };
        pick.Pressed += () => PickImage(token => { _imageToken = token; }, name => _imageName.Text = name);
        _imageName = new Label { Text = "(none)", ClipText = true, CustomMinimumSize = new Vector2(160 * s, 0) };
        irow.AddChild(pick); irow.AddChild(_imageName);
        ig.AddChild(irow);
        _imageUrl = Line(ig, "…or image URL / task_id", "https://example.com/photo.png or task_… (text→image result)", s);
        _autofix = Check(ig, "Image autofix", false, "enable_image_autofix: enhance low-quality input images");

        _multiBox = new VBoxContainer();
        box.AddChild(_multiBox);
        var mg = Grid(_multiBox, s);
        foreach (var view in new[] { "front", "left", "back", "right" })
        {
            mg.AddChild(new Label { Text = view + (view == "front" ? " (required)" : "") });
            var row = new HBoxContainer();
            var b = new Button { Text = "Choose & Upload...", FocusMode = Control.FocusModeEnum.None };
            var nm = new Label { Text = "(none)", ClipText = true, CustomMinimumSize = new Vector2(160 * s, 0) };
            string v = view;
            b.Pressed += () => PickImage(token => { var e = _views[v]; _views[v] = (e.pick, e.name, token); }, name => _views[v].name.Text = name);
            row.AddChild(b); row.AddChild(nm);
            mg.AddChild(row);
            _views[view] = (b, nm, null);
        }

        _modelOptions = new VBoxContainer();
        box.AddChild(_modelOptions);
        Section(_modelOptions, "Model options");
        var og = Grid(_modelOptions, s, 4);
        (_faceLimitOn, _faceLimit) = OptionalSpin(og, "Face limit", 48, 2000000, 1, 20000, false, s);
        _texQuality = Option(og, "Texture quality", new[] { "standard", "fast", "detailed", "extreme" }, 0, s);
        _texture = Check(og, "Texture", true);
        _pbr = Check(og, "PBR", true);
        _geoQuality = Option(og, "Geometry quality", new[] { "standard", "detailed" }, 0, s);
        _orientation = Option(og, "Orientation", new[] { "default", "align_image" }, 0, s);
        _quad = Check(og, "Quad mesh", false, "quad: four-sided polygons (forces FBX output)");
        _smartLowPoly = Check(og, "Smart low poly", false, "smart_low_poly: hand-crafted clean topology (v3.0+)");
        _autoSize = Check(og, "Auto size (meters)", false, "auto_size: scale to real-world dimensions");
        (_seedOn, _modelSeed) = OptionalSpin(og, "Model seed", 0, 2147483647, 1, 0, false, s);
        og.AddChild(new Label { Text = "Texture seed" });
        _texSeed = new SpinBox { MinValue = 0, MaxValue = 2147483647, Step = 1, CustomMinimumSize = new Vector2(130 * s, 0) };
        og.AddChild(_texSeed);
        _autoImport = Check(og, "Import when done", true, "Download and add the model to the scene automatically");

        _imageOptions = new VBoxContainer();
        box.AddChild(_imageOptions);
        Section(_imageOptions, "Image options");
        var xg = Grid(_imageOptions, s, 4);
        _imgModel = Option(xg, "Image model", new[] { "seedream_v4", "seedream_v5", "banana", "banana_pro", "banana2", "chat_image_2" }, 0, s);
        _imgSize = Option(xg, "Size", new[] { "2048x2048", "2K", "3K", "1248x832", "832x1248" }, 0, s);
        xg.AddChild(new Label { Text = "Template" });
        var trow = new HBoxContainer();
        _imgTemplateOn = new CheckBox { FocusMode = Control.FocusModeEnum.None };
        _imgTemplate = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var t in new[] { "t_pose", "asset_extraction", "character_completion", "variants", "figure" }) _imgTemplate.AddItem(t);
        trow.AddChild(_imgTemplateOn); trow.AddChild(_imgTemplate);
        xg.AddChild(trow);

        var run = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        Action(run, "Generate", Generate);
        box.AddChild(run);
        UpdateGenerateVisibility();
    }

    private void UpdateGenerateVisibility()
    {
        int mode = _genMode.Selected;
        _promptBox.Visible = mode is 0 or 3;
        _imageBox.Visible = mode == 1;
        _multiBox.Visible = mode == 2;
        _modelOptions.Visible = mode != 3;
        _imageOptions.Visible = mode == 3;
        _genModel.Disabled = mode == 3;
        _autofix.GetParent<Control>().Visible = mode == 1;
    }

    private void PickImage(Action<string> onToken, Action<string> onName)
    {
        var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "Choose image (PNG/JPEG/WebP, ≤20 MB)" };
        fd.AddFilter("*.png,*.jpg,*.jpeg,*.webp", "Images");
        _shell.AddChild(fd);
        fd.FileSelected += p =>
        {
            fd.QueueFree();
            _ = Guard(async () =>
            {
                onName("uploading…");
                SetStatus($"Uploading {System.IO.Path.GetFileName(p)}…");
                string token = await _client.UploadFile(p);
                onToken(token);
                onName(System.IO.Path.GetFileName(p));
                SetStatus($"Uploaded {System.IO.Path.GetFileName(p)} → {token}");
            });
        };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    }

    private string SelectedModel => _genModel.GetItemText(_genModel.Selected);
    private bool IsPSeries => SelectedModel.StartsWith("P", StringComparison.Ordinal);

    private Dictionary<string, object?> ModelOptionsBody()
    {
        var b = new Dictionary<string, object?> { ["model"] = SelectedModel, ["texture"] = _texture.ButtonPressed, ["pbr"] = _pbr.ButtonPressed };
        if (_faceLimitOn.ButtonPressed) b["face_limit"] = (int)_faceLimit.Value;
        if (_texQuality.Selected != 0) b["texture_quality"] = _texQuality.GetItemText(_texQuality.Selected);
        if (_texQuality.GetItemText(_texQuality.Selected) == "fast") b["texture_version"] = "v3.5-20260815";
        if (_seedOn.ButtonPressed) b["model_seed"] = (int)_modelSeed.Value;
        if (_texSeed.Value > 0) b["texture_seed"] = (int)_texSeed.Value;
        if (_autoSize.ButtonPressed) b["auto_size"] = true;
        bool v3 = !IsPSeries && !SelectedModel.StartsWith("v2.5", StringComparison.Ordinal);
        if (v3)
        {
            if (_geoQuality.Selected == 1) b["geometry_quality"] = "detailed";
            if (_quad.ButtonPressed) b["quad"] = true;
            if (_smartLowPoly.ButtonPressed) b["smart_low_poly"] = true;
        }
        else if (IsPSeries && _quad.ButtonPressed && SelectedModel.StartsWith("P2", StringComparison.Ordinal)) b["quad"] = true;
        return b;
    }

    private async Task Generate()
    {
        int mode = _genMode.Selected;
        string prompt = _prompt.Text.Trim();
        Dictionary<string, object?> body; string path; string label; string type;
        switch (mode)
        {
            case 0:
                if (prompt.Length == 0) { SetStatus("Enter a prompt."); return; }
                body = ModelOptionsBody(); body["prompt"] = prompt;
                if (_negPrompt.Text.Trim().Length > 0) body["negative_prompt"] = _negPrompt.Text.Trim();
                path = "/generation/text-to-model"; label = prompt; type = "text_to_model";
                break;
            case 1:
                {
                    string input = _imageToken ?? _imageUrl.Text.Trim();
                    if (string.IsNullOrEmpty(input)) { SetStatus("Choose an image (upload) or enter an image URL / task_id."); return; }
                    body = ModelOptionsBody(); body["input"] = input;
                    if (_autofix.ButtonPressed) body["enable_image_autofix"] = true;
                    if (_orientation.Selected == 1) body["orientation"] = "align_image";
                    path = "/generation/image-to-model"; label = "image: " + (_imageToken != null ? _imageName.Text : input); type = "image_to_model";
                    break;
                }
            case 2:
                {
                    var inputs = new List<Dictionary<string, string>>();
                    foreach (var (view, e) in _views) if (e.token != null) inputs.Add(new Dictionary<string, string> { [view] = e.token });
                    if (_views["front"].token == null || inputs.Count < 2) { SetStatus("Multiview needs the front view plus at least one more view."); return; }
                    body = ModelOptionsBody(); body["inputs"] = inputs;
                    if (_orientation.Selected == 1) body["orientation"] = "align_image";
                    path = "/generation/multiview-to-model"; label = $"multiview ({inputs.Count} views)"; type = "multiview_to_model";
                    break;
                }
            default:
                {
                    if (prompt.Length == 0) { SetStatus("Enter a prompt."); return; }
                    body = new Dictionary<string, object?> { ["prompt"] = prompt, ["model"] = _imgModel.GetItemText(_imgModel.Selected), ["output_format"] = "png" };
                    string size = _imgSize.GetItemText(_imgSize.Selected);
                    if (!_imgModel.GetItemText(_imgModel.Selected).StartsWith("banana")) body["size"] = size; else body["aspect_ratio"] = "1:1";
                    if (_imgTemplateOn.ButtonPressed) body["template"] = _imgTemplate.GetItemText(_imgTemplate.Selected);
                    path = "/generation/text-to-image"; label = "image: " + prompt; type = "text_to_image";
                    break;
                }
        }
        await Submit(path, body, type, label, autoImport: mode != 3 && _autoImport.ButtonPressed);
    }

    private async Task Submit(string path, Dictionary<string, object?> body, string type, string label, bool autoImport)
    {
        SetStatus($"Submitting {type}…");
        var data = await _client.PostJsonRaw(path, JsonSerializer.Serialize(body));
        string id = data.TryGetProperty("task_id", out var t) ? t.GetString() ?? "" : "";
        if (id.Length == 0) { SetStatus("No task_id in response."); return; }
        _tasks.Insert(0, new TripoTaskRecord { Id = id, Type = type, Label = label, Status = "queued", Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), AutoImport = autoImport });
        SaveTasks(); RefreshTaskList(); RefreshInputTaskLists();
        SetStatus($"Task {id} queued ({type}).");
        _tabs.CurrentTab = 3;
    }

    // ---------------------------------------------------------------- 입력 선택(Process/Animate 공용)

    private void BuildInputRow(VBoxContainer box, float s, out OptionButton kind, out OptionButton taskSel, out LineEdit text, out Label info, bool uploadFromCube)
    {
        Section(box, "Input model");
        var g = Grid(box, s);
        kind = Option(g, "Source", uploadFromCube ? new[] { "Task from history", "Selected Cube object (upload GLB)", "URL or file_token" } : new[] { "Task from history", "URL or file_token" }, 0, s);
        taskSel = Option(g, "Task", Array.Empty<string>(), 0, s);
        text = Line(g, "URL / file_token", "https://… or file_…", s);
        g.AddChild(new Label { Text = "" });
        info = new Label { Text = "", ClipText = true };
        info.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        g.AddChild(info);
    }

    private readonly List<TripoTaskRecord> _inputTaskItems = new();

    private void RefreshInputTaskLists()
    {
        _inputTaskItems.Clear();
        foreach (var t in _tasks) if (t.Status == "success" && !t.IsImageTask) _inputTaskItems.Add(t);
        foreach (var ob in new[] { _inputTask, _aInputTask })
        {
            if (ob == null) continue;
            int sel = ob.Selected;
            ob.Clear();
            foreach (var t in _inputTaskItems) ob.AddItem($"{t.Id}  {t.Type}  {Short(t.Label)}");
            if (ob.ItemCount > 0) ob.Selected = Math.Clamp(sel, 0, ob.ItemCount - 1);
        }
    }

    private static string Short(string s) => s.Length > 28 ? s[..28] + "…" : s;

    /// <summary>입력 문자열(task_id / file_token / URL)을 정한다. Cube 오브젝트 업로드는 GLB로 내보내 /files에 올린다.</summary>
    private async Task<string?> ResolveInput(OptionButton kind, OptionButton taskSel, LineEdit text, bool uploadFromCube)
    {
        int k = kind.Selected;
        if (k == 0)
        {
            if (taskSel.Selected < 0 || taskSel.Selected >= _inputTaskItems.Count) { SetStatus("No finished model task in history."); return null; }
            return _inputTaskItems[taskSel.Selected].Id;
        }
        if (uploadFromCube && k == 1)
        {
            var sel = _shell.Document.Selection.Objects;
            if (sel.Count == 0) { SetStatus("Select an object in Cube first."); return null; }
            string desc = string.Join(",", sel.Select(id => id.ToString()));
            if (_uploadedCubeToken != null && _uploadedCubeDesc == desc + "|" + _shell.Document.Undo.LastCommand?.GetHashCode()) return _uploadedCubeToken;
            string path = System.IO.Path.Combine(TripoDir(), "cube_upload.glb");
            if (!_shell.Files.Export(path, selectionOnly: true).Ok) { SetStatus("Export failed."); return null; }
            SetStatus("Uploading selection as GLB…");
            _uploadedCubeToken = await _client.UploadFile(path);
            _uploadedCubeDesc = desc + "|" + _shell.Document.Undo.LastCommand?.GetHashCode();
            SetStatus($"Uploaded selection → {_uploadedCubeToken}");
            return _uploadedCubeToken;
        }
        string v = text.Text.Trim();
        if (v.Length == 0) { SetStatus("Enter a URL or file_token."); return null; }
        return v;
    }

    // ---------------------------------------------------------------- Process 탭

    private void BuildProcessTab(float s)
    {
        Tab(_tabs, "Process", out var box, s);
        BuildInputRow(box, s, out _inputKind, out _inputTask, out _inputText, out _inputInfo, uploadFromCube: true);
        Section(box, "Operation");
        var g = Grid(box, s);
        _procOp = Option(g, "Operation", new[] { "Texture (regenerate textures)", "Retopology (decimate)", "Segmentation (split into parts)", "Part completion (after segmentation)", "Convert format" }, 0, s);
        _procOp.ItemSelected += _ => UpdateProcessVisibility();

        _texBox = new VBoxContainer(); box.AddChild(_texBox);
        var tg = Grid(_texBox, s, 4);
        _texModel = Option(tg, "Texture model", new[] { "v3.0-20250812", "v3.5-20260815", "v2.5-20250123" }, 0, s);
        _procTexQuality = Option(tg, "Quality", new[] { "standard", "fast", "detailed", "extreme" }, 0, s);
        _texPrompt = Line(tg, "Texture prompt", "worn leather with scratches (optional)", s);
        _procPbr = Check(tg, "PBR", true);

        _decBox = new VBoxContainer(); box.AddChild(_decBox);
        var dg = Grid(_decBox, s, 4);
        _decModel = Option(dg, "Algorithm", new[] { "v2.0 (smart retopology)", "v1.0 (basic)" }, 0, s);
        (_procFaceLimitOn, _procFaceLimit) = OptionalSpin(dg, "Face limit", 500, 20000, 1, 5000, true, s);
        _procQuad = Check(dg, "Quad", false);
        _procBake = Check(dg, "Bake textures", true);

        _segBox = new VBoxContainer(); box.AddChild(_segBox);
        var sg = Grid(_segBox, s, 4);
        _segModel = Option(sg, "Segment model", new[] { "v1.0-20250506 (geometry)", "v2.0-20260430 (semantic, beta)" }, 0, s);
        _segGran = Option(sg, "Granularity (v2)", new[] { "balanced", "simple", "detailed" }, 0, s);
        _segConn = Check(sg, "Split by connectivity (v2)", true);

        _completeBox = new VBoxContainer(); box.AddChild(_completeBox);
        var cg = Grid(_completeBox, s, 4);
        _completeMode = Option(cg, "Mode", new[] { "ai_completion", "quick_cap" }, 0, s);
        _partNames = Line(cg, "Part names", "head, body (optional, comma separated)", s);

        _convBox = new VBoxContainer(); box.AddChild(_convBox);
        var vg = Grid(_convBox, s, 4);
        _convFormat = Option(vg, "Format", new[] { "FBX", "GLTF", "OBJ", "USDZ", "STL", "3MF" }, 0, s);
        _convTexFormat = Option(vg, "Texture format", new[] { "PNG", "JPEG", "WEBP" }, 0, s);
        vg.AddChild(new Label { Text = "Texture size" });
        _convTexSize = new SpinBox { MinValue = 256, MaxValue = 8192, Step = 256, Value = 2048, CustomMinimumSize = new Vector2(130 * s, 0) };
        vg.AddChild(_convTexSize);
        _fbxPreset = Option(vg, "FBX preset", new[] { "blender", "3dsmax", "mixamo", "bake_scale" }, 0, s);
        _convPivotBottom = Check(vg, "Pivot to center bottom", false);
        _convPackUv = Check(vg, "Pack UVs", false);
        _convAnim = Check(vg, "With animation", true);

        var run = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        Action(run, "Run", RunProcess);
        box.AddChild(run);
        UpdateProcessVisibility();
    }

    private void UpdateProcessVisibility()
    {
        int op = _procOp.Selected;
        _texBox.Visible = op == 0; _decBox.Visible = op == 1; _segBox.Visible = op == 2; _completeBox.Visible = op == 3; _convBox.Visible = op == 4;
    }

    private async Task RunProcess()
    {
        string? input = await ResolveInput(_inputKind, _inputTask, _inputText, uploadFromCube: true);
        if (input == null) return;
        int op = _procOp.Selected;
        var body = new Dictionary<string, object?> { ["input"] = input };
        string path, type;
        switch (op)
        {
            case 0:
                path = "/models/texture"; type = "texture_model";
                body["model"] = _texModel.GetItemText(_texModel.Selected);
                body["pbr"] = _procPbr.ButtonPressed;
                if (_procTexQuality.Selected != 0) body["texture_quality"] = _procTexQuality.GetItemText(_procTexQuality.Selected);
                if (_texPrompt.Text.Trim().Length > 0) body["texture_prompt"] = new Dictionary<string, string> { ["text"] = _texPrompt.Text.Trim() };
                break;
            case 1:
                path = "/mesh/decimate"; type = "decimate";
                body["model"] = _decModel.Selected == 0 ? "v2.0" : "v1.0";
                if (_procFaceLimitOn.ButtonPressed) body["face_limit"] = (int)_procFaceLimit.Value;
                body["quad"] = _procQuad.ButtonPressed;
                if (_decModel.Selected == 0) body["bake"] = _procBake.ButtonPressed;
                break;
            case 2:
                path = "/mesh/segment"; type = "mesh_segmentation";
                body["model"] = _segModel.Selected == 0 ? "v1.0-20250506" : "v2.0-20260430";
                if (_segModel.Selected == 1) { body["segmentation_granularity"] = _segGran.GetItemText(_segGran.Selected); body["split_by_connectivity"] = _segConn.ButtonPressed; }
                break;
            case 3:
                path = "/mesh/complete"; type = "mesh_completion";
                body["completion_mode"] = _completeMode.GetItemText(_completeMode.Selected);
                if (_partNames.Text.Trim().Length > 0) body["part_names"] = _partNames.Text.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                break;
            default:
                path = "/models/convert"; type = "convert";
                body["format"] = _convFormat.GetItemText(_convFormat.Selected);
                body["texture_format"] = _convTexFormat.GetItemText(_convTexFormat.Selected);
                body["texture_size"] = (int)_convTexSize.Value;
                if (_convFormat.Selected == 0) body["fbx_preset"] = _fbxPreset.GetItemText(_fbxPreset.Selected);
                if (_convPivotBottom.ButtonPressed) body["pivot_to_center_bottom"] = true;
                if (_convPackUv.ButtonPressed) body["pack_uv"] = true;
                body["with_animation"] = _convAnim.ButtonPressed;
                break;
        }
        await Submit(path, body, type, $"{type} ← {Short(input)}", autoImport: op != 2);
    }

    // ---------------------------------------------------------------- Animate 탭

    private void BuildAnimateTab(float s)
    {
        Tab(_tabs, "Animate", out var box, s);
        BuildInputRow(box, s, out _aInputKind, out _aInputTask, out _aInputText, out _aInputInfo, uploadFromCube: true);
        Section(box, "1. Rig check");
        var rc = new HBoxContainer();
        Action(rc, "Rig Check", RigCheck);
        _rigCheckResult = new Label { Text = "(not checked)" };
        rc.AddChild(_rigCheckResult);
        box.AddChild(rc);
        Section(box, "2. Auto rig");
        var rg = Grid(box, s, 4);
        _rigModel = Option(rg, "Rig model", new[] { "v1.0-20240301 (biped, 90+ presets)", "v2.5-20260210 (all creatures)" }, 0, s);
        _rigType = Option(rg, "Rig type", new[] { "biped", "quadruped", "hexapod", "octopod", "avian", "serpentine", "aquatic" }, 0, s);
        _rigSpec = Option(rg, "Bone spec", new[] { "tripo", "mixamo" }, 1, s);
        _rigFormat = Option(rg, "Output", new[] { "glb", "fbx" }, 0, s);
        var rr = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        Action(rr, "Auto Rig", Rig);
        box.AddChild(rr);
        Section(box, "3. Retarget animations (input must be a rigged task)");
        var tg = Grid(box, s);
        _animations = Line(tg, "Animations", "preset:idle, preset:walk, preset:run (comma separated)", s);
        _animations.Text = "preset:idle, preset:walk, preset:run";
        _retFormat = Option(tg, "Output", new[] { "glb", "fbx" }, 0, s);
        _retBake = Check(tg, "Bake animation (glb)", true);
        _animInPlace = Check(tg, "Animate in place", false);
        var presets = new Label { Text = "Presets: idle, walk, run, dive, climb, jump, slash, shoot, hurt, fall, turn; quadruped:walk, hexapod:walk, octopod:walk, serpentine:march, aquatic:march", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        presets.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        box.AddChild(presets);
        var ra = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        Action(ra, "Retarget", Retarget);
        box.AddChild(ra);
    }

    private async Task RigCheck()
    {
        string? input = await ResolveInput(_aInputKind, _aInputTask, _aInputText, uploadFromCube: true);
        if (input == null) return;
        _rigCheckResult.Text = "checking…";
        await Submit("/animations/rig-check", new Dictionary<string, object?> { ["input"] = input }, "rig_check", "rig check ← " + Short(input), autoImport: false);
        _tabs.CurrentTab = 2;
    }

    private async Task Rig()
    {
        string? input = await ResolveInput(_aInputKind, _aInputTask, _aInputText, uploadFromCube: true);
        if (input == null) return;
        var body = new Dictionary<string, object?>
        {
            ["input"] = input, ["model"] = _rigModel.Selected == 0 ? "v1.0-20240301" : "v2.5-20260210",
            ["rig_type"] = _rigType.GetItemText(_rigType.Selected), ["spec"] = _rigSpec.GetItemText(_rigSpec.Selected), ["out_format"] = _rigFormat.GetItemText(_rigFormat.Selected),
        };
        await Submit("/animations/rig", body, "rig", "rig ← " + Short(input), autoImport: true);
    }

    private async Task Retarget()
    {
        string? input = await ResolveInput(_aInputKind, _aInputTask, _aInputText, uploadFromCube: false);
        if (input == null) return;
        var anims = _animations.Text.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToArray();
        if (anims.Length == 0) { SetStatus("Enter at least one animation preset."); return; }
        var body = new Dictionary<string, object?> { ["input"] = input, ["out_format"] = _retFormat.GetItemText(_retFormat.Selected), ["bake_animation"] = _retBake.ButtonPressed, ["animate_in_place"] = _animInPlace.ButtonPressed };
        if (anims.Length == 1) body["animation"] = anims[0]; else body["animations"] = anims;
        await Submit("/animations/retarget", body, "retarget", $"retarget {anims.Length} anim(s) ← {Short(input)}", autoImport: true);
    }

    // ---------------------------------------------------------------- Tasks 탭

    private void BuildTasksTab(float s)
    {
        var scroll = Tab(_tabs, "Tasks", out var box, s);
        scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddChild(split);
        _taskList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(300 * s, 200 * s), FixedIconSize = new Vector2I((int)(40 * s), (int)(40 * s)) };
        _taskList.ItemSelected += _ => RefreshTaskDetail();
        split.AddChild(_taskList);
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(220 * s, 0) };
        split.AddChild(right);
        _preview = new TextureRect { CustomMinimumSize = new Vector2(200 * s, 200 * s), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        right.AddChild(_preview);
        _taskInfo = new Label { Text = "Select a task.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        right.AddChild(_taskInfo);
        var buttons = new GridContainer { Columns = 2 };
        _importBtn = Action(buttons, "Import into Cube", ImportSelected);
        _redownloadBtn = Action(buttons, "Download again", RedownloadSelected);
        _useInputBtn = new Button { Text = "Use as input", FocusMode = Control.FocusModeEnum.None };
        _useInputBtn.Pressed += UseSelectedAsInput;
        buttons.AddChild(_useInputBtn);
        _openBtn = new Button { Text = "Open preview", FocusMode = Control.FocusModeEnum.None };
        _openBtn.Pressed += () => { var t = Selected(); if (t?.LocalPreview != null && System.IO.File.Exists(t.LocalPreview)) OS.ShellOpen(t.LocalPreview); else if (t?.PreviewUrl != null) OS.ShellOpen(t.PreviewUrl); };
        buttons.AddChild(_openBtn);
        var refresh = Action(buttons, "Refresh status", async () => { var t = Selected(); if (t != null) await UpdateTask(t, force: true); });
        _deleteBtn = new Button { Text = "Remove from list", FocusMode = Control.FocusModeEnum.None };
        _deleteBtn.Pressed += () => { var t = Selected(); if (t != null) { _tasks.Remove(t); SaveTasks(); RefreshTaskList(); RefreshInputTaskLists(); } };
        buttons.AddChild(_deleteBtn);
        right.AddChild(buttons);
    }

    private TripoTaskRecord? Selected()
    {
        var sel = _taskList.GetSelectedItems();
        return sel.Length > 0 && sel[0] < _tasks.Count ? _tasks[sel[0]] : null;
    }

    private void RefreshTaskList()
    {
        var selected = Selected();
        _taskList.Clear();
        foreach (var t in _tasks)
        {
            string line = $"{t.Type}  {t.Status}{(t.IsDone ? "" : $" {t.Progress}%")}  {(t.Credits > 0 ? t.Credits.ToString("0.#") + "cr  " : "")}{Short(t.Label)}";
            int idx = _taskList.AddItem(line, Thumb(t));
            _taskList.SetItemTooltip(idx, $"{t.Id}\n{t.Created}\n{t.Label}" + (t.Error != null ? "\n" + t.Error : ""));
            if (t.Status == "failed") _taskList.SetItemCustomFgColor(idx, new Color(1f, 0.5f, 0.5f));
            else if (t.Status == "success" && !t.Imported && t.HasModel) _taskList.SetItemCustomFgColor(idx, new Color(0.7f, 1f, 0.7f));
        }
        if (selected != null) { int i = _tasks.IndexOf(selected); if (i >= 0) _taskList.Select(i); }
        RefreshTaskDetail();
    }

    private void RefreshTaskDetail()
    {
        var t = Selected();
        bool has = t != null;
        _importBtn.Disabled = !(t is { Status: "success" } && (t.HasModel || t.ModelUrl != null) && !t.IsImageTask);
        _redownloadBtn.Disabled = !(t is { Status: "success" } && (t.ModelUrl != null || t.PreviewUrl != null || t.ImageUrl != null));
        _useInputBtn.Disabled = !(t is { Status: "success" });
        _openBtn.Disabled = !(t != null && (t.LocalPreview != null || t.PreviewUrl != null));
        _deleteBtn.Disabled = !has;
        if (t == null) { _taskInfo.Text = "Select a task."; _preview.Texture = null; return; }
        _preview.Texture = Thumb(t, large: true);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{t.Id}");
        sb.AppendLine($"{t.Type} · {t.Status} · {t.Progress}%" + (t.Credits > 0 ? $" · {t.Credits:0.##} credits" : ""));
        sb.AppendLine(t.Created);
        if (!string.IsNullOrEmpty(t.Label)) sb.AppendLine(t.Label);
        if (t.Riggable != null) sb.AppendLine($"riggable: {t.Riggable}, rig_type: {t.RigType}");
        if (t.LocalModel != null) sb.AppendLine(System.IO.Path.GetFileName(t.LocalModel) + (t.Imported ? " (imported)" : ""));
        if (t.Error != null) sb.AppendLine("error: " + t.Error);
        _taskInfo.Text = sb.ToString().TrimEnd();
    }

    private Texture2D? Thumb(TripoTaskRecord t, bool large = false)
    {
        if (t.LocalPreview == null || !System.IO.File.Exists(t.LocalPreview)) return null;
        if (_thumbs.TryGetValue(t.LocalPreview, out var tex)) return tex;
        try
        {
            var bytes = System.IO.File.ReadAllBytes(t.LocalPreview);
            var img = new Image();
            Error err = t.LocalPreview.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? img.LoadWebpFromBuffer(bytes)
                : t.LocalPreview.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || t.LocalPreview.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? img.LoadJpgFromBuffer(bytes) : img.LoadPngFromBuffer(bytes);
            if (err != Error.Ok) { err = img.LoadPngFromBuffer(bytes); if (err != Error.Ok) err = img.LoadJpgFromBuffer(bytes); if (err != Error.Ok) err = img.LoadWebpFromBuffer(bytes); }
            if (err != Error.Ok) return null;
            tex = ImageTexture.CreateFromImage(img);
            _thumbs[t.LocalPreview] = tex;
            return tex;
        }
        catch { return null; }
    }

    private void UseSelectedAsInput()
    {
        var t = Selected(); if (t == null) return;
        int idx = _inputTaskItems.IndexOf(t);
        if (t.IsImageTask) { _imageUrl.Text = t.Id; _imageToken = null; _imageName.Text = "(task " + t.Id + ")"; _genMode.Selected = 1; UpdateGenerateVisibility(); _tabs.CurrentTab = 0; SetStatus("Image task set as Image → 3D input."); return; }
        if (idx < 0) { SetStatus("Only finished model tasks can be inputs."); return; }
        _inputKind.Selected = 0; _inputTask.Selected = idx; _aInputKind.Selected = 0; _aInputTask.Selected = idx;
        _tabs.CurrentTab = 1;
        SetStatus($"{t.Id} set as input for Process / Animate.");
    }

    private async Task ImportSelected()
    {
        var t = Selected(); if (t == null) return;
        await ImportTask(t);
    }

    private async Task RedownloadSelected()
    {
        var t = Selected(); if (t == null) return;
        await UpdateTask(t, force: true);
        await DownloadOutputs(t, force: true);
        SaveTasks(); RefreshTaskList();
    }

    private async Task ImportTask(TripoTaskRecord t)
    {
        if (!t.HasModel) await DownloadOutputs(t, force: true);
        if (!t.HasModel) { SetStatus("No model file for this task (URL expired? use Download again right after success)."); return; }
        var res = _shell.Files.Import(t.LocalModel!);
        if (res.Ok) { t.Imported = true; SaveTasks(); RefreshTaskList(); }
        SetStatus(res.Message);
    }

    // ---------------------------------------------------------------- 폴링 / 다운로드

    private async Task PollActive()
    {
        if (_polling || !Visible && !_tasks.Any(t => !t.IsDone)) return;
        _polling = true;
        try
        {
            SyncKey();
            if (string.IsNullOrEmpty(_client.ApiKey)) return;
            foreach (var t in _tasks.Where(t => !t.IsDone).ToList())
            {
                await UpdateTask(t, force: false);
            }
        }
        catch (TripoClient.TripoException ex) { SetStatus(ex.Message); }
        catch (Exception ex) { GD.PushWarning("[Tripo] poll: " + ex.Message); }
        finally { _polling = false; }
    }

    private async Task UpdateTask(TripoTaskRecord t, bool force)
    {
        if (t.IsDone && !force) return;
        var d = await _client.GetTask(t.Id);
        string status = d.TryGetProperty("status", out var st) ? st.GetString() ?? t.Status : t.Status;
        bool wasDone = t.IsDone;
        t.Status = status;
        t.Progress = d.TryGetProperty("progress", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : t.Progress;
        if (d.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ty.GetString())) t.Type = ty.GetString()!;
        if (d.TryGetProperty("credits_consumed", out var cc) && cc.ValueKind == JsonValueKind.Number) t.Credits = cc.GetDouble();
        if (status == "failed")
        {
            t.Error = (d.TryGetProperty("error_message", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() : null) ?? (d.TryGetProperty("error_code", out var ec) ? "error " + ec.ToString() : "failed");
        }
        if (status == "success" && d.TryGetProperty("output", out var o))
        {
            string? Str(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            t.ModelUrl = Str("model_url") ?? Str("pbr_model") ?? Str("model") ?? Str("base_model");
            if (t.ModelUrl == null && o.TryGetProperty("model_urls", out var mu) && mu.ValueKind == JsonValueKind.Array) { var first = mu.EnumerateArray().FirstOrDefault(); if (first.ValueKind == JsonValueKind.String) t.ModelUrl = first.GetString(); }
            t.PreviewUrl = Str("rendered_image_url") ?? Str("rendered_image");
            t.ImageUrl = Str("generated_image_url") ?? Str("generated_image");
            if (o.TryGetProperty("riggable", out var rg) && rg.ValueKind is JsonValueKind.True or JsonValueKind.False) t.Riggable = rg.GetBoolean();
            if (o.TryGetProperty("rig_type", out var rt) && rt.ValueKind == JsonValueKind.String) t.RigType = rt.GetString();
            if (t.Type == "rig_check") _rigCheckResult.Text = t.Riggable == true ? $"riggable ✓  rig_type: {t.RigType}" : "not riggable";
            if (t.Riggable == true && t.RigType != null) { int ri = Array.IndexOf(new[] { "biped", "quadruped", "hexapod", "octopod", "avian", "serpentine", "aquatic" }, t.RigType); if (ri >= 0) _rigType.Selected = ri; }
            if (!wasDone || force) await DownloadOutputs(t, force: force);
            if (!wasDone && t.AutoImport && t.HasModel && !t.IsImageTask) await ImportTask(t);
        }
        if (!wasDone && t.IsDone) { SetStatus($"Task {t.Id} {status}" + (t.Error != null ? ": " + t.Error : "") + "."); _ = RefreshBalance(); RefreshInputTaskLists(); }
        SaveTasks(); RefreshTaskList();
    }

    private async Task DownloadOutputs(TripoTaskRecord t, bool force)
    {
        string dir = TripoDir();
        if (t.ModelUrl != null && (force || !t.HasModel))
        {
            string ext = UrlExt(t.ModelUrl, ".glb");
            string path = System.IO.Path.Combine(dir, $"{t.Id}{ext}");
            SetStatus($"Downloading model for {t.Id}…");
            await _client.DownloadTo(t.ModelUrl, path);
            t.LocalModel = path;
        }
        string? img = t.PreviewUrl ?? t.ImageUrl;
        if (img != null && (force || t.LocalPreview == null || !System.IO.File.Exists(t.LocalPreview)))
        {
            string ext = UrlExt(img, ".png");
            string path = System.IO.Path.Combine(dir, $"{t.Id}_preview{ext}");
            await _client.DownloadTo(img, path);
            t.LocalPreview = path;
            _thumbs.Remove(path);
        }
        SetStatus($"Downloaded outputs of {t.Id}.");
    }

    private static string UrlExt(string url, string fallback)
    {
        try
        {
            string p = new Uri(url).AbsolutePath;
            string e = System.IO.Path.GetExtension(p).ToLowerInvariant();
            return e is ".glb" or ".gltf" or ".fbx" or ".obj" or ".stl" or ".usdz" or ".3mf" or ".zip" or ".png" or ".webp" or ".jpg" or ".jpeg" ? e : fallback;
        }
        catch { return fallback; }
    }

    private async Task RefreshBalance()
    {
        try
        {
            SyncKey();
            if (string.IsNullOrEmpty(_client.ApiKey)) { _balance.Text = "credits: (no API key)"; return; }
            var d = await _client.GetBalance();
            double bal = d.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 0;
            double frozen = d.TryGetProperty("frozen", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
            _balance.Text = $"credits: {bal:0.##}" + (frozen > 0 ? $" (frozen {frozen:0.##})" : "");
        }
        catch (TripoClient.TripoException ex) { _balance.Text = "credits: ?"; SetStatus(ex.Message); }
        catch (Exception ex) { _balance.Text = "credits: ?"; GD.PushWarning("[Tripo] balance: " + ex.Message); }
    }

    // ---------------------------------------------------------------- 저장

    private void LoadTasks()
    {
        try
        {
            if (!System.IO.File.Exists(TasksFile())) return;
            var list = JsonSerializer.Deserialize<List<TripoTaskRecord>>(System.IO.File.ReadAllText(TasksFile()));
            if (list != null) { _tasks.Clear(); _tasks.AddRange(list); }
        }
        catch (Exception ex) { GD.PushWarning("[Tripo] tasks.json: " + ex.Message); }
    }

    private void SaveTasks()
    {
        try { System.IO.File.WriteAllText(TasksFile(), JsonSerializer.Serialize(_tasks, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { GD.PushWarning("[Tripo] save: " + ex.Message); }
    }
}
