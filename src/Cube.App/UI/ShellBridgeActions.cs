using System.Diagnostics;
using Matrix4x4 = System.Numerics.Matrix4x4;
using NVec3 = System.Numerics.Vector3;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI;

/// <summary>브리지로 연동하는 외부 앱 종류.</summary>
/// <remarks>
/// 각 값: Blender = FBX로 보내고 애드온이 쓴 OBJ(+ 원점 JSON)로 받음, RizomUv = OBJ 왕복(UV만 원본에 적용),
/// Marmoset = FBX 보내기만(Toolbag 가져오기 스크립트), Cascadeur = FBX 왕복(교체 가져오기 + 애니메이션 클립 교체).
/// </remarks>
public enum BridgeApp { Blender, RizomUv, Marmoset, Cascadeur }

/// <summary>외부 앱으로 보낸 파일 하나에 대한 세션. 파일이 바뀌면(외부 앱 저장) 다시 읽는다.</summary>
public sealed class BridgeSession
{
    /// <summary>이 세션의 대상 앱.</summary>
    public BridgeApp App;
    /// <summary>Cube가 외부 앱으로 보낸 파일.</summary>
    public string Path = "";
    /// <summary>외부 앱이 되돌려 주는 파일(감시 대상). Blender는 OBJ(n각형·공유 정점 유지), 나머지는 보낸 파일과 같다.</summary>
    public string ReturnPath = "";
    /// <summary>보낸 노드(ID, 보낸 시점의 이름). UV 전송은 이름으로, 교체 가져오기는 ID로 찾는다.</summary>
    public List<(NodeId id, string name)> Nodes = new();
    /// <summary>마지막으로 확인/반영한 ReturnPath의 수정 시각(UTC). 이보다 새 파일이면 외부 앱이 저장한 것으로 본다.</summary>
    public DateTime Stamp;
    /// <summary>자동 다시 읽기가 꺼져 있을 때 "파일이 바뀌었다" 안내를 이미 띄웠는지(한 번만 안내).</summary>
    public bool Notified;
    /// <summary>true = OBJ UV만 받아 원본 메시에 덮어쓴다(RizomUV). false = 파일을 다시 가져와 보낸 노드를 교체한다.</summary>
    public bool UvOnly => App == BridgeApp.RizomUv;
    /// <summary>되돌려 받을 수 있는 앱인지. Marmoset은 보내기만 하므로 감시·다시 읽기를 하지 않는다.</summary>
    public bool CanReload => App != BridgeApp.Marmoset;
}

/// <summary>
/// Bridge 메뉴: Blender(glTF + 파이썬 패널 "Send to Cube"), RizomUV(OBJ, UV만 왕복), Marmoset Toolbag(FBX, 보내기만),
/// Cascadeur(FBX 왕복), Tripo3D(API로 생성 → 가져오기). 파일은 user://bridge/&lt;app&gt;/cube_bridge.* 에 쓰고 1초마다 변경을 감시한다.
/// </summary>
/// <remarks>
/// 현재 동작(이후 버전에서 바뀐 점 포함): Blender는 자체 FBX writer로 보내고, 애드온의 'Send to Cube'가 쓴 OBJ + cube_bridge.json(원점)을
/// 받아 보낸 노드를 교체한다(v0.0.14~). Marmoset은 Toolbag 파이썬 가져오기 스크립트를 생성해 실행 인자로 넘긴다(v0.0.49).
/// 감시는 1초 Timer(CheckBridgeFile), 다시 읽기는 한 Undo 그룹으로 묶어 되돌릴 수 있다.
/// </remarks>
public partial class Shell
{
    /// <summary>현재(마지막) 브리지 세션. 시작 시 Blender 반환 파일 감시용 세션이 기본으로 만들어진다. 한 번에 하나만 유지.</summary>
    public BridgeSession? Bridge { get; private set; }
    /// <summary>Bridge Settings 패널(앱 실행 파일 경로, Tripo API 키 등). 처음 열 때 만든다.</summary>
    public BridgeSettingsWindow? BridgeSettingsWindow { get; private set; }
    /// <summary>Tripo Editor 패널(Tripo3D OpenAPI로 생성·가공·리깅 후 가져오기). 처음 열 때 만든다.</summary>
    public TripoWindow? TripoWindow { get; private set; }

    /// <summary>
    /// bridge.* 액션 등록(RizomUV/Marmoset/Cascadeur 보내기, Tripo 창·가져오기·폴더, 다시 읽기·자동 다시 읽기·폴더 열기, 설정,
    /// Blender 전체/선택 보내기, 애드온 설치·저장·폴더 열기). 이어서 Blender 반환 파일 감시 세션을 만들고
    /// 1초 주기 Timer(BridgeWatch)로 CheckBridgeFile을 돌린다.
    /// </summary>
    private void RegisterBridgeActions()
    {
        Actions.Register("bridge.rizom", "Send to RizomUV (OBJ, UVs round-trip)", () => SendToBridge(BridgeApp.RizomUv), canExecute: HasBridgeNodes, repeatable: true);
        Actions.Register("bridge.marmoset", "Send to Marmoset Toolbag (FBX)", () => SendToBridge(BridgeApp.Marmoset), canExecute: HasBridgeNodes, repeatable: true);
        Actions.Register("bridge.cascadeur", "Send to Cascadeur (FBX)", () => SendToBridge(BridgeApp.Cascadeur), canExecute: HasBridgeNodes, repeatable: true);
        Actions.Register("bridge.tripo", "Tripo Editor...", ToggleTripo, isChecked: () => TripoWindow?.IsOpen ?? false);
        Actions.Register("bridge.tripoImport", "Import Tripo File...", () => Files.ShowImportDialog());
        Actions.Register("bridge.tripoFolder", "Open Tripo Folder", () => OS.ShellOpen(System.IO.Path.Combine(BridgeDir(null), "tripo")));
        Actions.Register("bridge.reload", "Reload from Bridge File", () => ReloadBridge(), canExecute: () => Bridge is { CanReload: true } && System.IO.File.Exists(Bridge.ReturnPath), repeatable: true);
        Actions.Register("bridge.autoReload", "Auto Reload When File Changes", () => { Settings.Bridge.AutoReload = !Settings.Bridge.AutoReload; Settings.Save(); }, isChecked: () => Settings.Bridge.AutoReload);
        Actions.Register("bridge.openFolder", "Open Bridge Folder", () => { var d = BridgeDir(null); OS.ShellOpen(d); });
        Actions.Register("bridge.settings", "Bridge Settings...", ToggleBridgeSettings, isChecked: () => BridgeSettingsWindow?.IsOpen ?? false);
        Actions.Register("bridge.blenderAll", "Send All to Blender", () => SendToBridge(BridgeApp.Blender, launch: false, selection: false), canExecute: HasBridgeNodes, repeatable: true);
        Actions.Register("bridge.blenderSelected", "Send Selected to Blender", () => SendToBridge(BridgeApp.Blender, launch: false, selection: true), canExecute: () => Document.Selection.Objects.Count > 0, repeatable: true);
        Actions.Register("bridge.installBlenderAddon", "Install Blender Add-on", InstallBlenderAddon);
        Actions.Register("bridge.saveBlenderAddon", "Save Blender Add-on As...", SaveBlenderAddon);
        Actions.Register("bridge.openAddonsFolder", "Open Add-ons Folder (shipped add-ons)", () =>
        {
            // 배포본: Cube.exe 옆 addons\, 개발 실행: 저장소 assets/addons
            string exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
            string shipped = System.IO.Path.Combine(exeDir, "addons");
            string dev = ProjectSettings.GlobalizePath("res://assets/addons");
            string dir = System.IO.Directory.Exists(shipped) ? shipped : dev;
            if (System.IO.Directory.Exists(dir)) OS.ShellOpen(dir); else HelpLine.Text = "Add-ons folder not found: " + shipped;
        });
        WatchBlenderBridgeFile();

        var timer = new Godot.Timer { Name = "BridgeWatch", WaitTime = 1.0, Autostart = true, OneShot = false };
        timer.Timeout += CheckBridgeFile;
        AddChild(timer);
    }

    /// <summary>보낼 것이 있는지: 루트 바로 아래에 메시 또는 조인트 노드가 하나라도 있어야 한다.</summary>
    private bool HasBridgeNodes() => Document.Root.Children.Any(n => n.Mesh != null || n.IsJoint);

    /// <summary>
    /// 앱별 브리지 폴더(user://bridge/&lt;app 소문자&gt;; app이 null이면 user://bridge)의 절대 경로. 없으면 만든다.
    /// </summary>
    public static string BridgeDir(BridgeApp? app)
    {
        string dir = ProjectSettings.GlobalizePath("user://bridge/" + (app?.ToString().ToLowerInvariant() ?? ""));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>보낼 노드: 선택 오브젝트(없으면 루트 자식 전부). 선택이 있으면 선택만 내보낸다.</summary>
    /// <summary>보낼 노드. selection = null이면 선택이 있을 때 선택, 없으면 전체; true/false는 강제.</summary>
    private List<SceneNode> BridgeNodes(bool? selection, out bool selectionOnly)
    {
        var sel = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n != null).Cast<SceneNode>().ToList();
        selectionOnly = selection ?? sel.Count > 0;
        return selectionOnly ? sel : Document.Root.Children.ToList();
    }

    /// <summary>메뉴/셸프용 보내기: 앱을 실행하며, 선택이 있으면 선택만 아니면 전체를 보낸다.</summary>
    private void SendToBridge(BridgeApp app) => SendToBridge(app, launch: true, selection: null);

    /// <summary>
    /// 외부 앱으로 보내기. 순서: ① 보낼 노드 결정 ② 앱별 파일 쓰기(Blender/Cascadeur/Marmoset = FBX, RizomUV = 월드 좌표 OBJ)
    /// ③ 새 BridgeSession 생성(반환 파일 = Blender는 cube_bridge.obj, 나머지는 보낸 파일; Stamp = 현재 반환 파일 시각이라 기존 파일은 무시)
    /// ④ launch가 false면(Blender) 안내만 하고 끝 ⑤ 실행 파일을 찾아(없으면 폴더 열기 + 설정 창) 프로세스 시작.
    /// Marmoset은 이미 실행 중이고 이 세션에서 띄운 적이 있으면 FBX 갱신만으로 Toolbag이 자동 다시 읽는다.
    /// </summary>
    /// <param name="app">대상 앱.</param>
    /// <param name="launch">true면 외부 앱 실행 파일을 띄운다.</param>
    /// <param name="selection">null = 선택이 있으면 선택만, true/false = 강제.</param>
    private void SendToBridge(BridgeApp app, bool launch, bool? selection)
    {
        var nodes = BridgeNodes(selection, out bool selOnly);
        if (nodes.Count == 0) { HelpLine.Text = selOnly ? "Bridge: nothing selected." : "Bridge: nothing to send."; return; }
        string dir = BridgeDir(app);
        string path;
        try
        {
            switch (app)
            {
                case BridgeApp.Blender:
                    // FBX(자체 writer): n각형·공유 정점·코너 노멀/UV·머티리얼/텍스처·조인트/스킨이 그대로 간다(glTF는 삼각형 + 코너 분리)
                    path = System.IO.Path.Combine(dir, "cube_bridge.fbx");
                    if (!Files.Export(path, selOnly).Ok) return;
                    break;
                case BridgeApp.RizomUv:
                    path = System.IO.Path.Combine(dir, "cube_bridge.obj");
                    ObjFormat.Write(path, nodes.Where(n => n.Mesh != null), worldSpace: true);
                    break;
                default:
                    path = System.IO.Path.Combine(dir, "cube_bridge.fbx");
                    if (!Files.Export(path, selOnly).Ok) return;
                    break;
            }
        }
        catch (Exception ex) { HelpLine.Text = $"Bridge: export failed — {ex.Message}"; return; }

        // 세션 기록: 보낸 노드(ID, 이름) — UV 전송은 이름으로, 교체 가져오기는 ID로 찾는다
        string ret = app == BridgeApp.Blender ? System.IO.Path.Combine(dir, "cube_bridge.obj") : path;
        Bridge = new BridgeSession { App = app, Path = path, ReturnPath = ret, Nodes = nodes.Select(n => (n.Id, n.Name)).ToList(), Stamp = System.IO.File.Exists(ret) ? System.IO.File.GetLastWriteTimeUtc(ret) : DateTime.UtcNow };
        if (!launch) { HelpLine.Text = $"Bridge: sent {nodes.Count} node(s) ({(selOnly ? "selected" : "all")}) — the Blender add-on (View3D sidebar → Cube tab, Auto receive) imports {System.IO.Path.GetFileName(path)}; 'Send All/Selected to Cube' there sends back."; return; }
        string? exe = ResolveExe(app);
        if (exe == null)
        {
            HelpLine.Text = $"Bridge: wrote {path}. {AppLabel(app)} executable not found — set it in Bridge → Bridge Settings, or open the file manually.";
            OS.ShellOpen(dir);
            ToggleBridgeSettings(open: true);
            return;
        }
        // Marmoset Toolbag은 명령줄 FBX를 가져오지 않는다(.tbscene/.py만). 가져오기 스크립트를 넘긴다:
        // mset.importModel(FBX) → 자동 다시 읽기 외부 모델 + 머티리얼, 화면 맞춤. 이미 Toolbag이 떠 있으면 FBX만 갱신(자동 다시 읽기)
        var args = new[] { path };
        if (app == BridgeApp.Marmoset)
        {
            if (Process.GetProcessesByName("toolbag").Length > 0 && Bridge?.App == BridgeApp.Marmoset && _marmosetLaunched)
            {
                HelpLine.Text = $"Sent to Marmoset Toolbag: {System.IO.Path.GetFileName(path)} updated — Toolbag reloads it automatically. (Closed the scene? File → Run Script… → {MarmosetScriptName} in the bridge folder.)";
                return;
            }
            try { args = new[] { WriteMarmosetScript(dir, path) }; }
            catch (Exception ex) { HelpLine.Text = $"Bridge: could not write the Toolbag import script — {ex.Message}"; return; }
            _marmosetLaunched = true;
        }
        try
        {
            // 셸 실행 없이 직접 실행(인자 목록 사용으로 공백/한글 경로 안전), 작업 폴더 = 브리지 폴더
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            HelpLine.Text = app switch
            {
                BridgeApp.RizomUv => "Sent to RizomUV. Edit UVs and save (Ctrl+S) — Cube copies the UVs back onto the original meshes.",
                BridgeApp.Marmoset => "Sent to Marmoset Toolbag: it opens and imports cube_bridge.fbx with materials (linked; sending again reloads it).",
                _ => "Sent to Cascadeur. Save/export back to the same FBX — Cube reloads it.",
            };
        }
        catch (Exception ex) { HelpLine.Text = $"Bridge: could not start {AppLabel(app)} — {ex.Message}"; OS.ShellOpen(dir); }
    }

    /// <summary>이 세션에서 Toolbag을 가져오기 스크립트와 함께 띄운 적이 있는지(다시 보낼 때 새로 띄우지 않고 파일만 갱신).</summary>
    private bool _marmosetLaunched;
    /// <summary>브리지 폴더에 생성되는 Toolbag 가져오기 스크립트 파일 이름.</summary>
    private const string MarmosetScriptName = "cube_bridge_toolbag.py";

    /// <summary>
    /// Toolbag 가져오기 스크립트를 브리지 폴더에 쓴다(배포 애드온 assets/addons/marmoset/cube_bridge_toolbag.py와 같은 내용에 FBX 경로를 박아 넣음).
    /// Toolbag에 인자로 넘기면 실행되어 FBX를 mset.importModel로 가져온다(loadMaterials, autoReload).
    /// </summary>
    private static string WriteMarmosetScript(string dir, string fbx)
    {
        string src;
        // 스크립트 원본: 개발 실행은 res:// 리소스, 배포본은 실행 파일 옆 addons/marmoset 폴더에서 읽는다
        var res = "res://assets/addons/marmoset/cube_bridge_toolbag.py";
        var bytes = Godot.FileAccess.FileExists(res) ? Godot.FileAccess.GetFileAsBytes(res) : null;
        if (bytes is { Length: > 0 }) src = System.Text.Encoding.UTF8.GetString(bytes);
        else
        {
            string shipped = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "", "addons", "marmoset", MarmosetScriptName);
            src = System.IO.File.ReadAllText(shipped);
        }
        // 기본 경로 대신 이번에 쓴 FBX를 가리키게(사용자 폴더 이름에 한글이 있어도 되도록 UTF-8 + raw 문자열)
        string fbxLit = fbx.Replace('\\', '/');
        src = src.Replace("FBX = os.environ.get(\"CUBE_BRIDGE_FBX\") or", $"FBX = r\"{fbxLit}\" or os.environ.get(\"CUBE_BRIDGE_FBX\") or");
        string path = System.IO.Path.Combine(dir, MarmosetScriptName);
        System.IO.File.WriteAllText(path, src, new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>사용자에게 보여 줄 앱 이름.</summary>
    public static string AppLabel(BridgeApp app) => app switch { BridgeApp.Blender => "Blender", BridgeApp.RizomUv => "RizomUV", BridgeApp.Marmoset => "Marmoset Toolbag", _ => "Cascadeur" };

    /// <summary>
    /// 앱 실행 파일 경로: 설정에 저장된 경로가 존재하면 그것, 아니면 DetectExe로 자동 감지해 설정에 저장한다. 못 찾으면 null.
    /// </summary>
    private string? ResolveExe(BridgeApp app)
    {
        var b = Settings.Bridge;
        string? p = app switch { BridgeApp.Blender => b.BlenderPath, BridgeApp.RizomUv => b.RizomUvPath, BridgeApp.Marmoset => b.MarmosetPath, _ => b.CascadeurPath };
        if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p)) return p;
        p = DetectExe(app);
        if (p != null)
        {
            switch (app) { case BridgeApp.Blender: b.BlenderPath = p; break; case BridgeApp.RizomUv: b.RizomUvPath = p; break; case BridgeApp.Marmoset: b.MarmosetPath = p; break; default: b.CascadeurPath = p; break; }
            Settings.Save();
        }
        return p;
    }

    /// <summary>흔한 설치 경로에서 실행 파일을 찾는다(가장 최신 버전 폴더 우선).</summary>
    public static string? DetectExe(BridgeApp app)
    {
        // 후보 = (검색 루트, 하위 폴더 패턴, 실행 파일 패턴). 폴더는 이름 내림차순(대개 최신 버전 우선)으로 보고,
        // 바로 아래에 없으면 하위 전체에서 첫 번째 일치를 쓴다. 권한 오류 등은 무시하고 다음 후보로.
        string pf = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
        string pf86 = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
        string local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        IEnumerable<(string root, string dirPattern, string exe)> candidates = app switch
        {
            BridgeApp.Blender => new[] { (System.IO.Path.Combine(pf, "Blender Foundation"), "Blender*", "blender.exe"), (System.IO.Path.Combine(pf86, "Steam", "steamapps", "common"), "Blender", "blender.exe"), (System.IO.Path.Combine(pf, "Blender Foundation"), "*", "blender.exe") },
            BridgeApp.RizomUv => new[] { (System.IO.Path.Combine(pf, "Rizom Lab"), "RizomUV*", "rizomuv.exe"), (System.IO.Path.Combine(pf, "Rizom Lab"), "*", "rizomuv*.exe") },
            BridgeApp.Marmoset => new[] { (System.IO.Path.Combine(pf, "Marmoset"), "Toolbag*", "toolbag.exe"), (System.IO.Path.Combine(pf86, "Steam", "steamapps", "common"), "Toolbag*", "toolbag.exe") },
            _ => new[] { (pf, "Cascadeur*", "cascadeur.exe"), (System.IO.Path.Combine(local, "Programs"), "Cascadeur*", "cascadeur.exe"), (System.IO.Path.Combine(pf86, "Steam", "steamapps", "common"), "Cascadeur*", "cascadeur.exe") },
        };
        foreach (var (root, dirPattern, exe) in candidates)
        {
            try
            {
                if (!System.IO.Directory.Exists(root)) continue;
                foreach (var dir in System.IO.Directory.GetDirectories(root, dirPattern).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var hits = System.IO.Directory.GetFiles(dir, exe, System.IO.SearchOption.TopDirectoryOnly);
                    if (hits.Length == 0) hits = System.IO.Directory.GetFiles(dir, exe, System.IO.SearchOption.AllDirectories).Take(1).ToArray();
                    if (hits.Length > 0) return hits[0];
                }
            }
            catch { /* 권한 등 */ }
        }
        return null;
    }

    // ---------------------------------------------------------------- 다시 읽기

    /// <summary>
    /// 1초마다 호출: 세션의 반환 파일이 Stamp보다 새로 저장되었고 저장 후 1초 이상 지났으면
    /// 자동 다시 읽기(설정)면 ReloadBridge, 아니면 헬프 라인에 한 번 안내한다. 파일 잠금 등 예외는 무시(다음 틱에 재시도).
    /// </summary>
    private void CheckBridgeFile()
    {
        var b = Bridge;
        if (b == null || !b.CanReload) return;
        try
        {
            if (!System.IO.File.Exists(b.ReturnPath)) return;
            var t = System.IO.File.GetLastWriteTimeUtc(b.ReturnPath);
            if (t <= b.Stamp) return;
            // 쓰는 중일 수 있으니 1초 이상 지난 뒤에 읽는다
            if ((DateTime.UtcNow - t).TotalSeconds < 1.0) return;
            if (Settings.Bridge.AutoReload) ReloadBridge();
            else if (!b.Notified) { b.Notified = true; HelpLine.Text = $"Bridge: {System.IO.Path.GetFileName(b.ReturnPath)} changed in {AppLabel(b.App)} — Bridge → Reload from Bridge File."; }
        }
        catch { /* 잠금 등 */ }
    }

    /// <summary>
    /// 반환 파일 다시 읽기. UV 전용 세션(RizomUV)은 ReloadUvsFromObj로 UV만 적용한다.
    /// 그 외에는 한 Undo 그룹에서: 보낸 노드의 머티리얼 할당을 이름별로 기억 → (Blender) OBJ + 원점을 먼저 읽고 성공하면 보낸 노드 삭제 후 추가,
    /// (Cascadeur 등) 보낸 노드만 가리키던 애니메이션 클립 제거 + 노드 삭제 후 Files.Import → 세션 노드 목록을 새 노드로 갱신하고
    /// 같은 이름 메시에 이전 머티리얼을 다시 할당한다.
    /// </summary>
    private void ReloadBridge()
    {
        var b = Bridge; if (b == null) return;
        // 지금 파일 시각을 기록해 같은 저장을 두 번 읽지 않게 하고 안내 상태를 초기화
        b.Stamp = System.IO.File.GetLastWriteTimeUtc(b.ReturnPath); b.Notified = false;
        if (b.UvOnly) { ReloadUvsFromObj(b); return; }
        var doc = Document;
        using (doc.Undo.BeginGroup($"Bridge Reload ({AppLabel(b.App)})"))
        {
            // 보낸 노드를 지우고 다시 가져온다. 이름이 같은 노드는 머티리얼 할당을 이어받는다(OBJ에는 머티리얼이 없음)
            var oldMaterials = new Dictionary<string, int>();
            foreach (var (id, _) in b.Nodes) { var n = doc.Find(id); if (n != null && n.MaterialId > 0) oldMaterials[n.Name] = n.MaterialId; }
            // 먼저 파일을 읽고(실패하면 아무것도 지우지 않음) 보낸 노드를 지운 뒤 새 노드를 넣는다
            ImportResult res;
            if (b.App == BridgeApp.Blender)
            {
                res = ImportObjWithOrigins(b.ReturnPath);
                if (!res.Ok) { HelpLine.Text = "Bridge: " + res.Message; return; }
                var existing = b.Nodes.Select(n => n.id).Where(id => doc.Find(id) != null).ToList();
                if (existing.Count > 0) { var del = new DeleteNodesCommand(doc, existing); if (!del.IsEmpty) doc.Undo.Push(del); }
                doc.Undo.Push(new ImportNodesCommand(res.Nodes));
            }
            else
            {
                var existing = b.Nodes.Select(n => n.id).Where(id => doc.Find(id) != null).ToList();
                // 보낸 노드(하위 포함)만 가리키는 기존 애니메이션 클립은 다시 가져온 클립으로 교체한다(Cascadeur 왕복)
                var sent = new HashSet<Core.Scene.NodeId>();
                void Collect(Core.Scene.SceneNode n) { sent.Add(n.Id); foreach (var c in n.Children) Collect(c); }
                foreach (var id in existing) Collect(doc.Get(id));
                var stale = doc.Animations.Where(c => c.Tracks.Count > 0 && c.Tracks.All(t => sent.Contains(t.Node))).ToList();
                if (stale.Count > 0) doc.Undo.Push(new Core.Scene.SetAnimationsCommand("Remove Old Animations", Array.Empty<Core.Scene.AnimationClip>(), stale));
                if (existing.Count > 0) { var del = new DeleteNodesCommand(doc, existing); if (!del.IsEmpty) doc.Undo.Push(del); }
                res = Files.Import(b.ReturnPath);
            }
            // 성공: 다음 다시 읽기에서 교체할 노드를 새로 가져온 노드로 바꾸고, 이름이 같은 메시에 머티리얼을 이어 준다(같은 머티리얼끼리 한 명령)
            if (res.Ok)
            {
                b.Nodes = res.Nodes.Select(n => (n.Id, n.Name)).ToList();
                foreach (var grp in res.Nodes.Where(n => n.Mesh != null && oldMaterials.ContainsKey(n.Name)).GroupBy(n => oldMaterials[n.Name]))
                    doc.Undo.Push(new AssignMaterialCommand(grp.Select(n => n.Id), grp.Key));
            }
        }
        HelpLine.Text = $"Bridge: reloaded {System.IO.Path.GetFileName(b.ReturnPath)} from {AppLabel(b.App)} (replaced {b.Nodes.Count} node(s); Undo to revert).";
    }

    /// <summary>
    /// Blender → Cube: OBJ(월드 좌표)와 함께 온 cube_bridge.json(오브젝트별 월드 행렬, Y-up 내보내기 공간)으로
    /// 정점을 로컬 좌표로 되돌리고 노드 트랜스폼(T = Blender origin, R, S, Pivot 0)을 복원한다. JSON이 없으면 그냥 가져온다.
    /// </summary>
    private ImportResult ImportObjWithOrigins(string objPath)
    {
        // ① OBJ를 월드 좌표 그대로 노드로 가져온다(아직 문서에 넣지 않음)
        var res = new ObjImporter().Import(objPath, Document, ImportOptions.Default);
        if (!res.Ok) { HelpLine.Text = res.Message; return res; }
        // ② 같은 이름의 .json(OBJ와 30초 이내에 쓰인 것만 = 같은 내보내기)에서 오브젝트 이름 → 4x4 월드 행렬을 읽는다
        string jsonPath = System.IO.Path.ChangeExtension(objPath, ".json");
        var matrices = new Dictionary<string, Matrix4x4>();
        try
        {
            if (System.IO.File.Exists(jsonPath) && Math.Abs((System.IO.File.GetLastWriteTimeUtc(jsonPath) - System.IO.File.GetLastWriteTimeUtc(objPath)).TotalSeconds) < 30)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(jsonPath));
                if (doc.RootElement.TryGetProperty("objects", out var objs))
                    foreach (var o in objs.EnumerateObject())
                    {
                        var rows = o.Value.EnumerateArray().Select(r => r.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray()).ToArray();
                        if (rows.Length != 4 || rows.Any(r => r.Length != 4)) continue;
                        // Blender(열벡터, M·v) → System.Numerics(행벡터, v·M) = 전치
                        matrices[o.Name] = new Matrix4x4(
                            rows[0][0], rows[1][0], rows[2][0], rows[3][0],
                            rows[0][1], rows[1][1], rows[2][1], rows[3][1],
                            rows[0][2], rows[1][2], rows[2][2], rows[3][2],
                            rows[0][3], rows[1][3], rows[2][3], rows[3][3]);
                    }
            }
        }
        catch (Exception ex) { GD.PushWarning($"[Bridge] origins json: {ex.Message}"); }
        if (matrices.Count == 0) return res;
        // ③ 노드마다 행렬을 찾아(정확한 이름, 없으면 "이름_" 접두어나 공백→_ 치환 이름) 정점을 역행렬로 로컬화하고 T/R/S를 복원
        int restored = 0;
        foreach (var n in res.Nodes)
        {
            if (n.Mesh == null) continue;
            if (!matrices.TryGetValue(n.Name, out var m))
            {
                var key = matrices.Keys.FirstOrDefault(k => n.Name.StartsWith(k + "_", StringComparison.Ordinal) || Sanitize(k) == n.Name);
                if (key == null) continue;
                m = matrices[key];
            }
            if (!Matrix4x4.Invert(m, out var inv)) continue;
            var mesh = n.Mesh;
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                if (!mesh.Verts[v].Alive) continue;
                var vert = mesh.Verts[v]; vert.Position = NVec3.Transform(vert.Position, inv); mesh.Verts[v] = vert;
            }
            Core.Mesh.MeshNormals.Recompute(mesh);
            mesh.BumpGeometry();
            n.Local = Transform3.FromMatrix(m);
            restored++;
        }
        return new ImportResult(true, res.Message + $" (origins restored for {restored})", res.Nodes);
    }

    /// <summary>RizomUV 왕복: OBJ의 오브젝트를 이름(없으면 순서)으로 원본 노드와 짝지어 면 순서대로 UV만 복사한다.</summary>
    /// <remarks>
    /// OBJ 오브젝트 이름은 공백이 _로 바뀌어 있으므로 Sanitize로 비교한다. 면 수/순서가 다르면(위상 변경) 해당 면은 건너뛰고
    /// 건너뛴 수와 찾지 못한 오브젝트 수를 헬프 라인에 알린다. 노드마다 UvEditCommand(한 Undo 그룹).
    /// </remarks>
    private void ReloadUvsFromObj(BridgeSession b)
    {
        List<ObjObject> objs;
        try { objs = ObjFormat.Read(b.ReturnPath); }
        catch (Exception ex) { HelpLine.Text = $"Bridge: could not read OBJ — {ex.Message}"; return; }
        var doc = Document;
        int done = 0, skipped = 0, missing = 0;
        using (doc.Undo.BeginGroup("Bridge UVs (RizomUV)"))
        {
            for (int i = 0; i < b.Nodes.Count; i++)
            {
                var (id, name) = b.Nodes[i];
                var node = doc.Find(id); if (node?.Mesh == null) continue;
                string key = Sanitize(name);
                var obj = objs.FirstOrDefault(o => o.Name == key) ?? (i < objs.Count ? objs[i] : null);
                if (obj == null) { missing++; continue; }
                var src = obj.Mesh; int sk = 0; int n = 0;
                doc.Undo.Push(new UvEditCommand("Bridge UVs", id, m => n = UvTransfer.ApplyByFaceOrder(m, src, out sk)));
                done += n; skipped += sk;
            }
        }
        UvEditorWindow?.Canvas.Invalidate();
        HelpLine.Text = $"Bridge: UVs from RizomUV applied to {done} face(s)" + (skipped > 0 ? $", {skipped} skipped (topology changed?)" : "") + (missing > 0 ? $", {missing} object(s) not found" : "") + ".";
    }

    // ---------------------------------------------------------------- Blender 애드온

    /// <summary>내장 Blender 애드온 원본 경로(배포 빌드에는 include_filter로 포함된다).</summary>
    private const string BlenderAddonRes = "res://assets/addons/blender/cube_bridge.py";

    /// <summary>앱 시작 시 Blender 브리지 파일이 이미 있으면(애드온의 'Send to Cube') 수동 세션 없이도 변경을 감시한다(가져오기만, 교체 없음).</summary>
    private void WatchBlenderBridgeFile()
    {
        if (Bridge != null) return;
        string dir = BridgeDir(BridgeApp.Blender);
        string ret = System.IO.Path.Combine(dir, "cube_bridge.obj");
        Bridge = new BridgeSession { App = BridgeApp.Blender, Path = System.IO.Path.Combine(dir, "cube_bridge.fbx"), ReturnPath = ret, Stamp = System.IO.File.Exists(ret) ? System.IO.File.GetLastWriteTimeUtc(ret) : DateTime.UtcNow };
    }

    /// <summary>Blender 애드온 파이썬 소스 텍스트(없으면 빈 문자열).</summary>
    private static string BlenderAddonSource() => Godot.FileAccess.GetFileAsString(BlenderAddonRes);

    /// <summary>%APPDATA%\Blender Foundation\Blender\&lt;버전&gt;\scripts\addons\cube_bridge.py 로 복사한다(모든 설치 버전). Blender에서 Preferences → Add-ons → "Cube Bridge" 활성화 필요.</summary>
    private void InstallBlenderAddon()
    {
        string src = BlenderAddonSource();
        if (string.IsNullOrEmpty(src)) { HelpLine.Text = "Bridge: add-on source not found (assets/addons/blender/cube_bridge.py)."; return; }
        // Blender 사용자 폴더 아래의 버전 폴더(config/userpref.blend 또는 scripts가 있는 것)마다 scripts/addons에 복사
        string root = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender");
        var installed = new List<string>();
        try
        {
            if (System.IO.Directory.Exists(root))
                foreach (var ver in System.IO.Directory.GetDirectories(root))
                {
                    if (!System.IO.File.Exists(System.IO.Path.Combine(ver, "config", "userpref.blend")) && !System.IO.Directory.Exists(System.IO.Path.Combine(ver, "scripts"))) continue;
                    string dir = System.IO.Path.Combine(ver, "scripts", "addons");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "cube_bridge.py"), src);
                    installed.Add(System.IO.Path.GetFileName(ver));
                }
        }
        catch (Exception ex) { HelpLine.Text = $"Bridge: install failed — {ex.Message}"; return; }
        if (installed.Count == 0)
        {
            HelpLine.Text = "Bridge: no Blender user folder found under %APPDATA%\\Blender Foundation\\Blender. Use 'Save Blender Add-on As...' and install it from Blender's Preferences → Add-ons → Install.";
            SaveBlenderAddon();
            return;
        }
        HelpLine.Text = $"Bridge: cube_bridge.py installed for Blender {string.Join(", ", installed)}. In Blender: Edit → Preferences → Add-ons → enable \"Cube Bridge\" (View3D sidebar → Cube tab).";
    }

    /// <summary>애드온 파일을 사용자가 고른 위치에 저장한다(Blender Preferences → Add-ons → Install로 설치하도록 안내).</summary>
    private void SaveBlenderAddon()
    {
        var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "Save Blender Add-on", CurrentFile = "cube_bridge.py" };
        fd.AddFilter("*.py", "Blender add-on");
        AddChild(fd);
        fd.FileSelected += p =>
        {
            try { System.IO.File.WriteAllText(p, BlenderAddonSource()); HelpLine.Text = $"Bridge: saved {p}. In Blender: Preferences → Add-ons → Install... → choose it → enable \"Cube Bridge\"."; }
            catch (Exception ex) { HelpLine.Text = $"Bridge: save failed — {ex.Message}"; }
            fd.QueueFree();
        };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    }

    /// <summary>OBJ 내보내기와 같은 규칙으로 이름의 공백 문자를 '_'로 바꾼다(이름 짝짓기용).</summary>
    private static string Sanitize(string name) => string.Concat(name.Select(c => char.IsWhiteSpace(c) ? '_' : c));

    // ---------------------------------------------------------------- 창

    /// <summary>Bridge Settings 패널 열기/닫기 토글. open이 지정되면 그 상태로 강제(실행 파일을 못 찾았을 때 열기 등).</summary>
    private void ToggleBridgeSettings() => ToggleBridgeSettings(open: null);
    private void ToggleBridgeSettings(bool? open)
    {
        EnsureBridgeSettings();
        bool show = open ?? !BridgeSettingsWindow.Visible;
        if (show) BridgeSettingsWindow.Open(); else BridgeSettingsWindow.Close();
    }

    /// <summary>Bridge Settings 패널을 지연 생성하고 DockManager에 등록한다(레이아웃 복원 "bridgeSettings"에서도 호출).</summary>
    private BridgeSettingsWindow EnsureBridgeSettings()
    {
        if (BridgeSettingsWindow == null)
        {
            BridgeSettingsWindow = new BridgeSettingsWindow { Name = "BridgeSettings", Visible = false, PanelId = "bridgeSettings" };
            AddChild(BridgeSettingsWindow);
            BridgeSettingsWindow.Setup(this);
            Dock.Register(BridgeSettingsWindow);
        }
        return BridgeSettingsWindow;
    }

    /// <summary>Tripo Editor 패널을 지연 생성하고 DockManager에 등록한다(레이아웃 복원 "tripo"에서도 호출).</summary>
    private TripoWindow EnsureTripo()
    {
        if (TripoWindow == null)
        {
            TripoWindow = new TripoWindow { Name = "Tripo", Visible = false, PanelId = "tripo" };
            AddChild(TripoWindow);
            TripoWindow.Setup(this);
            Dock.Register(TripoWindow);
        }
        return TripoWindow;
    }

    /// <summary>Tripo Editor 열기/닫기 토글.</summary>
    private void ToggleTripo()
    {
        var w = EnsureTripo();
        if (w.Visible) w.Close(); else w.Open();
    }
}
