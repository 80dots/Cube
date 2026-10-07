using System.Text;
using System.Text.Json;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// Bridge → Tripo3D: 텍스트 프롬프트로 Tripo API(v2 openapi) text_to_model 작업을 만들고 폴링해 결과 GLB를 받아 가져온다.
/// Godot HttpRequest 노드를 써서 메인 스레드 콜백으로 처리한다. API 키는 Bridge Settings(Settings.Bridge.TripoApiKey).
/// </summary>
public partial class TripoWindow : FloatingPanel
{
    private const string ApiBase = "https://api.tripo3d.ai/v2/openapi";
    private Shell _shell = null!;
    private TextEdit _prompt = null!;
    private Label _status = null!;
    private ProgressBar _progress = null!;
    private Button _generate = null!;
    private HttpRequest _http = null!;
    private Godot.Timer _poll = null!;
    private string? _taskId;
    private enum Stage { Idle, Creating, Polling, Downloading }
    private Stage _stage;
    private string _downloadPath = "";

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "Tripo3D — Generate Model";
        Size = new Vector2(520 * s, 320 * s);
        MinPanelSize = new Vector2(400 * s, 240 * s);

        var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", (int)(6 * s));
        Content.AddChild(box);
        box.AddChild(new Label { Text = "Prompt (text to model)" });
        _prompt = new TextEdit { SizeFlagsVertical = Control.SizeFlags.ExpandFill, WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "e.g. a low-poly wooden treasure chest with iron bands", CustomMinimumSize = new Vector2(0, 80 * s) };
        box.AddChild(_prompt);
        var row = new HBoxContainer();
        _generate = new Button { Text = "Generate", FocusMode = Control.FocusModeEnum.None };
        _generate.Pressed += Generate;
        row.AddChild(_generate);
        var cancel = new Button { Text = "Cancel", FocusMode = Control.FocusModeEnum.None };
        cancel.Pressed += () => { Reset(); _status.Text = "Cancelled."; };
        row.AddChild(cancel);
        var key = new Button { Text = "API Key...", FocusMode = Control.FocusModeEnum.None };
        key.Pressed += () => _shell.Actions.Invoke("bridge.settings");
        row.AddChild(key);
        var import = new Button { Text = "Import Tripo File...", FocusMode = Control.FocusModeEnum.None, TooltipText = "Import a GLB/FBX downloaded from tripo3d.ai" };
        import.Pressed += () => _shell.Actions.Invoke("file.import");
        row.AddChild(import);
        box.AddChild(row);
        _progress = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 0, ShowPercentage = true };
        box.AddChild(_progress);
        _status = new Label { Text = "Enter a prompt and press Generate. Results are saved to user://bridge/tripo/ and imported.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_status);

        _http = new HttpRequest { Name = "Http", Timeout = 120 };
        _http.RequestCompleted += OnCompleted;
        AddChild(_http);
        _poll = new Godot.Timer { Name = "Poll", WaitTime = 3.0, OneShot = true };
        _poll.Timeout += PollTask;
        AddChild(_poll);
    }

    private string[] Headers()
    {
        string key = CubeApp.Instance.Settings.Bridge.TripoApiKey ?? "";
        return new[] { "Content-Type: application/json", "Authorization: Bearer " + key };
    }

    private void Generate()
    {
        if (_stage != Stage.Idle) return;
        string prompt = _prompt.Text.Trim();
        if (prompt.Length == 0) { _status.Text = "Enter a prompt first."; return; }
        if (string.IsNullOrEmpty(CubeApp.Instance.Settings.Bridge.TripoApiKey)) { _status.Text = "Set your Tripo3D API key in Bridge Settings first."; _shell.Actions.Invoke("bridge.settings"); return; }
        var body = JsonSerializer.Serialize(new { type = "text_to_model", prompt });
        _stage = Stage.Creating; _generate.Disabled = true; _progress.Value = 0;
        _status.Text = "Creating task...";
        var err = _http.Request(ApiBase + "/task", Headers(), Godot.HttpClient.Method.Post, body);
        if (err != Error.Ok) { _status.Text = $"Request failed: {err}"; Reset(); }
    }

    private void PollTask()
    {
        if (_stage != Stage.Polling || _taskId == null) return;
        var err = _http.Request(ApiBase + "/task/" + _taskId, Headers(), Godot.HttpClient.Method.Get);
        if (err != Error.Ok) { _status.Text = $"Poll failed: {err}"; Reset(); }
    }

    private void OnCompleted(long result, long code, string[] headers, byte[] body)
    {
        try
        {
            if (_stage == Stage.Downloading)
            {
                if (result != (long)HttpRequest.Result.Success || code >= 400) { _status.Text = $"Download failed (HTTP {code})."; Reset(); return; }
                _status.Text = "Importing...";
                var res = _shell.Files.Import(_downloadPath);
                _status.Text = res.Ok ? $"Imported {res.Nodes.Count} node(s) from Tripo3D ({System.IO.Path.GetFileName(_downloadPath)})." : "Import failed: " + res.Message;
                _progress.Value = 100;
                Reset(keepStatus: true);
                return;
            }
            if (result != (long)HttpRequest.Result.Success) { _status.Text = $"Network error ({(HttpRequest.Result)result})."; Reset(); return; }
            string text = Encoding.UTF8.GetString(body);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            int apiCode = root.TryGetProperty("code", out var c) ? c.GetInt32() : (code >= 400 ? (int)code : 0);
            if (code >= 400 || apiCode != 0)
            {
                string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : root.TryGetProperty("suggestion", out var sg) ? sg.GetString() ?? "" : text;
                _status.Text = $"Tripo API error (HTTP {code}, code {apiCode}): {msg}";
                Reset();
                return;
            }
            var data = root.GetProperty("data");
            if (_stage == Stage.Creating)
            {
                _taskId = data.GetProperty("task_id").GetString();
                _stage = Stage.Polling;
                _status.Text = $"Task {_taskId} queued...";
                _poll.Start();
                return;
            }
            if (_stage == Stage.Polling)
            {
                string status = data.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                int progress = data.TryGetProperty("progress", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : 0;
                _progress.Value = progress;
                switch (status)
                {
                    case "success":
                        {
                            string? url = null;
                            if (data.TryGetProperty("output", out var o))
                            {
                                if (o.TryGetProperty("pbr_model", out var pbr) && pbr.ValueKind == JsonValueKind.String) url = pbr.GetString();
                                if (string.IsNullOrEmpty(url) && o.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String) url = mo.GetString();
                                if (string.IsNullOrEmpty(url) && o.TryGetProperty("base_model", out var bm) && bm.ValueKind == JsonValueKind.String) url = bm.GetString();
                            }
                            if (string.IsNullOrEmpty(url)) { _status.Text = "Task finished but no model URL was returned."; Reset(); return; }
                            string dir = ProjectSettings.GlobalizePath("user://bridge/tripo/");
                            System.IO.Directory.CreateDirectory(dir);
                            string ext = url.Contains(".fbx", StringComparison.OrdinalIgnoreCase) ? ".fbx" : ".glb";
                            _downloadPath = System.IO.Path.Combine(dir, $"tripo_{_taskId}{ext}");
                            _stage = Stage.Downloading;
                            _status.Text = "Downloading model...";
                            _http.DownloadFile = _downloadPath;
                            var err = _http.Request(url);
                            if (err != Error.Ok) { _status.Text = $"Download request failed: {err}"; Reset(); }
                            return;
                        }
                    case "failed": case "cancelled": case "banned": case "expired": case "unknown":
                        _status.Text = $"Task {status}."; Reset(); return;
                    default:
                        _status.Text = $"Task {status}... {progress}%";
                        _poll.Start();
                        return;
                }
            }
        }
        catch (Exception ex) { _status.Text = "Error: " + ex.Message; Reset(); }
    }

    private void Reset(bool keepStatus = false)
    {
        _stage = Stage.Idle; _taskId = null; _generate.Disabled = false; _poll.Stop();
        _http.DownloadFile = "";
        if (!keepStatus) _progress.Value = 0;
    }
}
