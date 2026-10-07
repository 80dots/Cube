using System.Text;
using System.Text.Json;
using Godot;

namespace Cube.App;

/// <summary>
/// Tripo3D OpenAPI v3 클라이언트(https://openapi.tripo3d.ai/v3, Bearer API 키). Godot HttpRequest 노드를 호출마다 하나씩 만들어
/// 메인 스레드 콜백으로 끝나는 async 메서드로 감싼다. 속도 제한(초당 1요청)을 지키려고 요청 사이에 최소 1초를 둔다.
/// 응답은 {"code":0,"data":{...}} 규약이며 code≠0 또는 HTTP≥400이면 <see cref="TripoException"/>.
/// </summary>
public sealed partial class TripoClient : Node
{
    public const string BaseUrl = "https://openapi.tripo3d.ai/v3";
    public string ApiKey { get; set; } = "";
    private ulong _lastRequestMs;

    public sealed class TripoException : Exception
    {
        public int Code; public long Http;
        public TripoException(string message, int code = -1, long http = 0) : base(message) { Code = code; Http = http; }
    }

    private string[] Headers(string? contentType) =>
        contentType == null ? new[] { "Authorization: Bearer " + ApiKey } : new[] { "Content-Type: " + contentType, "Authorization: Bearer " + ApiKey };

    private async Task Throttle()
    {
        ulong now = Time.GetTicksMsec();
        ulong elapsed = now - _lastRequestMs;
        if (_lastRequestMs != 0 && elapsed < 1000) await ToSignal(GetTree().CreateTimer((1000 - elapsed) / 1000.0), SceneTreeTimer.SignalName.Timeout);
        _lastRequestMs = Time.GetTicksMsec();
    }

    /// <summary>요청을 보내고 (result, code, body)를 돌려준다. 다운로드면 파일로 저장한다.</summary>
    private async Task<(long result, long code, byte[] body)> Send(string url, string[] headers, Godot.HttpClient.Method method, byte[]? raw, string? downloadTo = null, int timeout = 120)
    {
        if (string.IsNullOrWhiteSpace(ApiKey) && downloadTo == null) throw new TripoException("Tripo API key is not set (Bridge → Settings).");
        await Throttle();
        var req = new HttpRequest { Timeout = timeout, UseThreads = true };
        if (downloadTo != null) req.DownloadFile = downloadTo;
        AddChild(req);
        var tcs = new TaskCompletionSource<(long, long, byte[])>();
        req.RequestCompleted += (result, code, _, body) => tcs.TrySetResult((result, code, body));
        var err = raw != null ? req.RequestRaw(url, headers, method, raw) : req.Request(url, headers, method);
        if (err != Error.Ok) { req.QueueFree(); throw new TripoException($"Request failed to start: {err}"); }
        var r = await tcs.Task;
        req.QueueFree();
        if (r.Item1 != (long)HttpRequest.Result.Success) throw new TripoException($"Network error: {(HttpRequest.Result)r.Item1}", -1, r.Item2);
        return r;
    }

    private static JsonElement ParseData(long code, byte[] body)
    {
        string text = Encoding.UTF8.GetString(body);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch { throw new TripoException($"HTTP {code}: {Truncate(text)}", -1, code); }
        var root = doc.RootElement.Clone();
        int apiCode = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        if (code >= 400 || apiCode != 0)
        {
            string msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            if (root.TryGetProperty("suggestion", out var sg) && sg.ValueKind == JsonValueKind.String) msg += " " + sg.GetString();
            if (string.IsNullOrWhiteSpace(msg)) msg = Truncate(text);
            throw new TripoException($"Tripo API error (HTTP {code}, code {apiCode}): {msg}", apiCode, code);
        }
        return root.TryGetProperty("data", out var d) ? d : root;
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public async Task<JsonElement> PostJson(string path, object body)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var (_, code, bytes) = await Send(BaseUrl + path, Headers("application/json"), Godot.HttpClient.Method.Post, Encoding.UTF8.GetBytes(json));
        return ParseData(code, bytes);
    }

    /// <summary>미리 만든 JSON 문자열로 POST(딕셔너리 본문용).</summary>
    public async Task<JsonElement> PostJsonRaw(string path, string json)
    {
        var (_, code, bytes) = await Send(BaseUrl + path, Headers("application/json"), Godot.HttpClient.Method.Post, Encoding.UTF8.GetBytes(json));
        return ParseData(code, bytes);
    }

    public async Task<JsonElement> Get(string path)
    {
        var (_, code, bytes) = await Send(BaseUrl + path, Headers(null), Godot.HttpClient.Method.Get, null);
        return ParseData(code, bytes);
    }

    /// <summary>POST /files (multipart/form-data, 필드 "file") → file_token.</summary>
    public async Task<string> UploadFile(string localPath)
    {
        var data = System.IO.File.ReadAllBytes(localPath);
        string name = System.IO.Path.GetFileName(localPath);
        string mime = System.IO.Path.GetExtension(localPath).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
            ".glb" => "model/gltf-binary", ".gltf" => "model/gltf+json", ".fbx" or ".obj" or ".stl" => "application/octet-stream", _ => "application/octet-stream",
        };
        string boundary = "----CubeTripo" + Guid.NewGuid().ToString("N");
        var head = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{name}\"\r\nContent-Type: {mime}\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        var body = new byte[head.Length + data.Length + tail.Length];
        Buffer.BlockCopy(head, 0, body, 0, head.Length); Buffer.BlockCopy(data, 0, body, head.Length, data.Length); Buffer.BlockCopy(tail, 0, body, head.Length + data.Length, tail.Length);
        var (_, code, bytes) = await Send(BaseUrl + "/files", Headers("multipart/form-data; boundary=" + boundary), Godot.HttpClient.Method.Post, body, timeout: 300);
        var d = ParseData(code, bytes);
        return d.TryGetProperty("file_token", out var t) ? t.GetString() ?? "" : throw new TripoException("Upload returned no file_token.");
    }

    /// <summary>URL을 파일로 내려받는다(모델 URL은 5분 뒤 만료되므로 성공 즉시 호출).</summary>
    public async Task DownloadTo(string url, string localPath)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(localPath)!);
        var (_, code, _) = await Send(url, Array.Empty<string>(), Godot.HttpClient.Method.Get, null, downloadTo: localPath, timeout: 600);
        if (code >= 400) throw new TripoException($"Download failed (HTTP {code}).", -1, code);
    }

    public Task<JsonElement> GetTask(string taskId) => Get("/tasks/" + taskId);
    public Task<JsonElement> GetBalance() => Get("/account/balance");
}
