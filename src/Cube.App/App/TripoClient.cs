using System.Text;
using System.Text.Json;
using Godot;

namespace Cube.App;

/// <summary>
/// Tripo3D OpenAPI v3 클라이언트(https://openapi.tripo3d.ai/v3, Bearer API 키). Godot HttpRequest 노드를 호출마다 하나씩 만들어
/// 메인 스레드 콜백으로 끝나는 async 메서드로 감싼다. 속도 제한(초당 1요청)을 지키려고 요청 사이에 최소 1초를 둔다.
/// 응답은 {"code":0,"data":{...}} 규약이며 code≠0 또는 HTTP≥400이면 <see cref="TripoException"/>.
/// </summary>
/// <remarks>
/// TripoWindow(Bridge → Tripo Editor)가 자식 노드로 만들어 쓴다. HttpRequest는 노드라 트리에 붙어 있어야 동작하므로
/// 이 클래스도 Node다. 모든 공개 메서드는 메인 스레드에서 await 해야 한다(완료 신호가 메인 스레드에서 온다).
/// </remarks>
public sealed partial class TripoClient : Node
{
    /// <summary>API 기준 URL(v3). 경로 인자는 이 뒤에 붙는다(예: "/tasks/{id}").</summary>
    public const string BaseUrl = "https://openapi.tripo3d.ai/v3";
    /// <summary>Bearer 인증에 쓰는 API 키(Settings.Bridge.TripoApiKey에서 설정).</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>마지막 요청 시각(엔진 시작 후 ms, 0 = 아직 없음). 스로틀 계산용.</summary>
    private ulong _lastRequestMs;

    /// <summary>Tripo API/네트워크 오류. API 오류 코드와 HTTP 상태를 함께 담는다.</summary>
    public sealed class TripoException : Exception
    {
        /// <summary>Code = 응답 JSON의 code(-1 = 해당 없음), Http = HTTP 상태 코드(0 = 없음).</summary>
        public int Code; public long Http;
        /// <summary>메시지와 (선택) API 코드·HTTP 상태로 만든다.</summary>
        public TripoException(string message, int code = -1, long http = 0) : base(message) { Code = code; Http = http; }
    }

    /// <summary>요청 헤더 배열: 인증 헤더 + (있으면) Content-Type.</summary>
    private string[] Headers(string? contentType) =>
        contentType == null ? new[] { "Authorization: Bearer " + ApiKey } : new[] { "Content-Type: " + contentType, "Authorization: Bearer " + ApiKey };

    /// <summary>직전 요청과 1초 이상 간격이 되도록 SceneTreeTimer로 기다린 뒤 요청 시각을 기록한다(초당 1요청 제한).</summary>
    private async Task Throttle()
    {
        ulong now = Time.GetTicksMsec();
        ulong elapsed = now - _lastRequestMs;
        if (_lastRequestMs != 0 && elapsed < 1000) await ToSignal(GetTree().CreateTimer((1000 - elapsed) / 1000.0), SceneTreeTimer.SignalName.Timeout);
        _lastRequestMs = Time.GetTicksMsec();
    }

    /// <summary>요청을 보내고 (result, code, body)를 돌려준다. 다운로드면 파일로 저장한다.</summary>
    /// <param name="url">전체 URL.</param>
    /// <param name="raw">본문 바이트(null이면 본문 없는 요청).</param>
    /// <param name="downloadTo">지정하면 응답 본문을 이 파일에 직접 저장한다(이 경우 API 키 없이도 허용 — 서명된 다운로드 URL).</param>
    /// <param name="timeout">초 단위 타임아웃.</param>
    private async Task<(long result, long code, byte[] body)> Send(string url, string[] headers, Godot.HttpClient.Method method, byte[]? raw, string? downloadTo = null, int timeout = 120)
    {
        if (string.IsNullOrWhiteSpace(ApiKey) && downloadTo == null) throw new TripoException("Tripo API key is not set (Bridge → Settings).");
        await Throttle();
        // 요청마다 새 HttpRequest 노드(스레드 사용)를 만들어 자식으로 붙인다
        var req = new HttpRequest { Timeout = timeout, UseThreads = true };
        if (downloadTo != null) req.DownloadFile = downloadTo;
        AddChild(req);
        // RequestCompleted 신호를 TaskCompletionSource로 바꿔 await 가능하게 한다
        var tcs = new TaskCompletionSource<(long, long, byte[])>();
        req.RequestCompleted += (result, code, _, body) => tcs.TrySetResult((result, code, body));
        var err = raw != null ? req.RequestRaw(url, headers, method, raw) : req.Request(url, headers, method);
        if (err != Error.Ok) { req.QueueFree(); throw new TripoException($"Request failed to start: {err}"); }
        // 완료 후 노드를 해제하고, 네트워크 수준 실패(연결·타임아웃 등)는 예외로 바꾼다
        var r = await tcs.Task;
        req.QueueFree();
        if (r.Item1 != (long)HttpRequest.Result.Success) throw new TripoException($"Network error: {(HttpRequest.Result)r.Item1}", -1, r.Item2);
        return r;
    }

    /// <summary>
    /// 응답 본문을 JSON으로 읽어 "data" 요소를 돌려준다(없으면 루트). JSON이 아니거나 HTTP ≥ 400 또는 code ≠ 0이면
    /// message(+ suggestion)를 담은 <see cref="TripoException"/>을 던진다.
    /// </summary>
    private static JsonElement ParseData(long code, byte[] body)
    {
        string text = Encoding.UTF8.GetString(body);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch { throw new TripoException($"HTTP {code}: {Truncate(text)}", -1, code); }
        // JsonDocument 수명과 무관하게 쓰도록 루트를 복제한다
        var root = doc.RootElement.Clone();
        int apiCode = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        // 오류 응답: message와 suggestion을 이어 사람이 읽을 메시지를 만든다
        if (code >= 400 || apiCode != 0)
        {
            string msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            if (root.TryGetProperty("suggestion", out var sg) && sg.ValueKind == JsonValueKind.String) msg += " " + sg.GetString();
            if (string.IsNullOrWhiteSpace(msg)) msg = Truncate(text);
            throw new TripoException($"Tripo API error (HTTP {code}, code {apiCode}): {msg}", apiCode, code);
        }
        return root.TryGetProperty("data", out var d) ? d : root;
    }

    /// <summary>오류 메시지용으로 300자에서 자른다.</summary>
    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;

    /// <summary>객체를 JSON(null 속성 생략)으로 직렬화해 POST하고 응답 data를 돌려준다.</summary>
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

    /// <summary>GET 요청 후 응답 data를 돌려준다.</summary>
    public async Task<JsonElement> Get(string path)
    {
        var (_, code, bytes) = await Send(BaseUrl + path, Headers(null), Godot.HttpClient.Method.Get, null);
        return ParseData(code, bytes);
    }

    /// <summary>POST /files (multipart/form-data, 필드 "file") → file_token.</summary>
    /// <remarks>본문은 직접 조립한 multipart(경계 문자열 + 헤더 + 파일 바이트 + 끝 경계)이며 큰 파일을 고려해 타임아웃 300초.</remarks>
    public async Task<string> UploadFile(string localPath)
    {
        var data = System.IO.File.ReadAllBytes(localPath);
        string name = System.IO.Path.GetFileName(localPath);
        // 확장자로 MIME 타입을 정한다(모르는 형식은 octet-stream)
        string mime = System.IO.Path.GetExtension(localPath).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
            ".glb" => "model/gltf-binary", ".gltf" => "model/gltf+json", ".fbx" or ".obj" or ".stl" => "application/octet-stream", _ => "application/octet-stream",
        };
        // multipart 본문 = 머리(경계·Content-Disposition·Content-Type) + 파일 + 꼬리(닫는 경계)
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

    /// <summary>GET /tasks/{id}: 작업 상태·진행률·결과 URL.</summary>
    public Task<JsonElement> GetTask(string taskId) => Get("/tasks/" + taskId);
    /// <summary>GET /account/balance: 계정 크레딧 잔액.</summary>
    public Task<JsonElement> GetBalance() => Get("/account/balance");
}
