using System.Diagnostics;
using Matrix4x4 = System.Numerics.Matrix4x4;
using NVec3 = System.Numerics.Vector3;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI;

public enum BridgeApp { Blender, RizomUv, Marmoset, Cascadeur }

/// <summary>외부 앱으로 보낸 파일 하나에 대한 세션. 파일이 바뀌면(외부 앱 저장) 다시 읽는다.</summary>
public sealed class BridgeSession
{
    public BridgeApp App;
    /// <summary>Cube가 외부 앱으로 보낸 파일.</summary>
    public string Path = "";
    /// <summary>외부 앱이 되돌려 주는 파일(감시 대상). Blender는 OBJ(n각형·공유 정점 유지), 나머지는 보낸 파일과 같다.</summary>
    public string ReturnPath = "";
    /// <summary>보낸 노드(ID, 보낸 시점의 이름). UV 전송은 이름으로, 교체 가져오기는 ID로 찾는다.</summary>
    public List<(NodeId id, string name)> Nodes = new();
    public DateTime Stamp;
    public bool Notified;
    /// <summary>true = OBJ UV만 받아 원본 메시에 덮어쓴다(RizomUV). false = 파일을 다시 가져와 보낸 노드를 교체한다.</summary>
    public bool UvOnly => App == BridgeApp.RizomUv;
    public bool CanReload => App != BridgeApp.Marmoset;
}

/// <summary>
/// Bridge 메뉴: Blender(glTF + 파이썬 패널 "Send to Cube"), RizomUV(OBJ, UV만 왕복), Marmoset Toolbag(FBX, 보내기만),
/// Cascadeur(FBX 왕복), Tripo3D(API로 생성 → 가져오기). 파일은 user://bridge/&lt;app&gt;/cube_bridge.* 에 쓰고 1초마다 변경을 감시한다.
/// </summary>
public partial class Shell
{
    public BridgeSession? Bridge { get; private set; }
    public BridgeSettingsWindow? BridgeSettingsWindow { get; private set; }
    public TripoWindow? TripoWindow { get; private set; }

    private void RegisterBridgeActions()
    {
        Actions.Register("bridge.rizom", "Send to RizomUV (OBJ, UVs round-trip)", () => SendToBridge(BridgeApp.RizomUv), canExecute: HasBridgeNodes, repeatable: true);
        Actions.Register("bridge.marmoset", "Send to Marmoset Toolbag (FBX)", () => SendToBridge(BridgeApp.Marmoset), canExecute: HasBridgeNodes, repeatable: true);
        Actions.Register("bridge.cascadeur", "Send to Cascadeur (FBX)", () => SendToBridge(BridgeApp.Cascadeur), canExecute: HasBridgeNodes, repeatable: true);
        Actions.Register("bridge.tripo", "Tripo Editor...", ToggleTripo, isChecked: () => TripoWindow?.Visible ?? false);
        Actions.Register("bridge.tripoImport", "Import Tripo File...", () => Files.ShowImportDialog());
        Actions.Register("bridge.tripoFolder", "Open Tripo Folder", () => OS.ShellOpen(System.IO.Path.Combine(BridgeDir(null), "tripo")));
        Actions.Register("bridge.reload", "Reload from Bridge File", () => ReloadBridge(), canExecute: () => Bridge is { CanReload: true } && System.IO.File.Exists(Bridge.ReturnPath), repeatable: true);
        Actions.Register("bridge.autoReload", "Auto Reload When File Changes", () => { Settings.Bridge.AutoReload = !Settings.Bridge.AutoReload; Settings.Save(); }, isChecked: () => Settings.Bridge.AutoReload);
        Actions.Register("bridge.openFolder", "Open Bridge Folder", () => { var d = BridgeDir(null); OS.ShellOpen(d); });
        Actions.Register("bridge.settings", "Bridge Settings...", ToggleBridgeSettings, isChecked: () => BridgeSettingsWindow?.Visible ?? false);
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

    private bool HasBridgeNodes() => Document.Root.Children.Any(n => n.Mesh != null || n.IsJoint);

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

    private void SendToBridge(BridgeApp app) => SendToBridge(app, launch: true, selection: null);

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
        var args = new[] { path };
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            HelpLine.Text = app switch
            {
                BridgeApp.RizomUv => "Sent to RizomUV. Edit UVs and save (Ctrl+S) — Cube copies the UVs back onto the original meshes.",
                BridgeApp.Marmoset => "Sent to Marmoset Toolbag (FBX with materials and textures).",
                _ => "Sent to Cascadeur. Save/export back to the same FBX — Cube reloads it.",
            };
        }
        catch (Exception ex) { HelpLine.Text = $"Bridge: could not start {AppLabel(app)} — {ex.Message}"; OS.ShellOpen(dir); }
    }

    public static string AppLabel(BridgeApp app) => app switch { BridgeApp.Blender => "Blender", BridgeApp.RizomUv => "RizomUV", BridgeApp.Marmoset => "Marmoset Toolbag", _ => "Cascadeur" };

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

    private void ReloadBridge()
    {
        var b = Bridge; if (b == null) return;
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
        var res = new ObjImporter().Import(objPath, Document, ImportOptions.Default);
        if (!res.Ok) { HelpLine.Text = res.Message; return res; }
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

    private const string BlenderAddonRes = "res://assets/addons/blender/cube_bridge.py";

    /// <summary>앱 시작 시 Blender 브리지 파일이 이미 있으면(애드온의 'Send to Cube') 수동 세션 없이도 변경을 감시한다(가져오기만, 교체 없음).</summary>
    private void WatchBlenderBridgeFile()
    {
        if (Bridge != null) return;
        string dir = BridgeDir(BridgeApp.Blender);
        string ret = System.IO.Path.Combine(dir, "cube_bridge.obj");
        Bridge = new BridgeSession { App = BridgeApp.Blender, Path = System.IO.Path.Combine(dir, "cube_bridge.fbx"), ReturnPath = ret, Stamp = System.IO.File.Exists(ret) ? System.IO.File.GetLastWriteTimeUtc(ret) : DateTime.UtcNow };
    }

    private static string BlenderAddonSource() => Godot.FileAccess.GetFileAsString(BlenderAddonRes);

    /// <summary>%APPDATA%\Blender Foundation\Blender\&lt;버전&gt;\scripts\addons\cube_bridge.py 로 복사한다(모든 설치 버전). Blender에서 Preferences → Add-ons → "Cube Bridge" 활성화 필요.</summary>
    private void InstallBlenderAddon()
    {
        string src = BlenderAddonSource();
        if (string.IsNullOrEmpty(src)) { HelpLine.Text = "Bridge: add-on source not found (assets/addons/blender/cube_bridge.py)."; return; }
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

    private static string Sanitize(string name) => string.Concat(name.Select(c => char.IsWhiteSpace(c) ? '_' : c));

    // ---------------------------------------------------------------- 창

    private void ToggleBridgeSettings() => ToggleBridgeSettings(open: null);
    private void ToggleBridgeSettings(bool? open)
    {
        if (BridgeSettingsWindow == null)
        {
            BridgeSettingsWindow = new BridgeSettingsWindow { Name = "BridgeSettings", Visible = false };
            AddChild(BridgeSettingsWindow);
            BridgeSettingsWindow.Setup(this);
        }
        bool show = open ?? !BridgeSettingsWindow.Visible;
        if (show) BridgeSettingsWindow.Open(); else BridgeSettingsWindow.Close();
    }

    private void ToggleTripo()
    {
        if (TripoWindow == null)
        {
            TripoWindow = new TripoWindow { Name = "Tripo", Visible = false };
            AddChild(TripoWindow);
            TripoWindow.Setup(this);
        }
        if (TripoWindow.Visible) TripoWindow.Close(); else TripoWindow.Open();
    }
}
