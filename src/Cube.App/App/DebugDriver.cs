using Godot;

namespace Cube.App;

/// <summary>
/// 개발용 자동 조작. 커맨드라인 <c>--drive=&lt;script&gt;</c>로 입력 이벤트를 주입해 조작을 재현하고 스크린샷으로 검증한다.
/// 스크립트 문법(세미콜론 구분):
///   wait N            N프레임 대기
///   move X Y          뷰포트 로컬 좌표로 마우스 이동(픽셀)
///   press L|M|R [alt|shift|ctrl ...]    버튼 누름(현재 위치)
///   release L|M|R
///   drag X Y [alt...]  현재 위치에서 (X,Y)까지 8단계로 모션 이벤트
///   wheel N           휠 N틱(음수 = 아래)
///   key NAME [ctrl|shift|alt]   키 입력(누름+뗌), NAME은 Godot Key 이름(F, A, W, Home, F9 ...)
///   shot PATH         스크린샷 저장
/// 예: --drive="wait 5; move 800 450; press L alt; drag 950 400 alt; release L; shot C:/tmp/a.png"
/// </summary>
public partial class DebugDriver : Node
{
    private readonly Queue<string> _steps = new();
    private int _wait;
    private Vector2 _pos = new(800, 450);
    private readonly HashSet<MouseButton> _held = new();

    public DebugDriver(string script)
    {
        foreach (var s in script.Split(';')) { var t = s.Trim(); if (t.Length > 0) _steps.Enqueue(t); }
    }

    private Control? Viewport => UI.Shell.Instance?.Viewport;

    public override void _Process(double delta)
    {
        if (_wait > 0) { _wait--; return; }
        while (_steps.Count > 0 && _wait == 0)
        {
            var step = _steps.Dequeue();
            var parts = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try { Exec(parts); }
            catch (Exception ex) { GD.PrintErr($"[Drive] '{step}': {ex.Message}"); }
            // 주입된 입력 이벤트는 다음 입력 플러시에서 처리되므로 입력 스텝 뒤에는 한 프레임 양보한다
            if (parts[0] is "move" or "press" or "dblclick" or "release" or "drag" or "wheel" or "key" or "axisdrag" or "ringdrag" or "centerdrag" or "keydown" or "keyup") _wait = Math.Max(_wait, 1);
        }
        if (_steps.Count == 0 && _wait == 0) { GD.Print("[Drive] done"); QueueFree(); }
    }

    private Vector2 ToGlobal(Vector2 local) => Viewport != null ? Viewport.GlobalPosition + local : local;

    /// <summary>"c+10" / "c-50" 처럼 뷰포트 중심 기준 좌표도 허용한다.</summary>
    private Vector2 ParseXY(string xs, string ys)
    {
        var size = Viewport?.Size ?? new Vector2(1000, 700);
        float Parse(string t, float center) => t.StartsWith("c") ? center + (t.Length > 1 ? float.Parse(t[1..]) : 0) : float.Parse(t);
        return new Vector2(Parse(xs, size.X / 2), Parse(ys, size.Y / 2));
    }

    private void Exec(string[] p)
    {
        switch (p[0])
        {
            case "wait": _wait = int.Parse(p[1]); break;
            case "move":
                _pos = ParseXY(p[1], p[2]);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), Relative = Vector2.Zero, ButtonMask = Mask() });
                break;
            case "press":
                {
                    var b = Button(p[1]);
                    _held.Add(b);
                    var ev = new InputEventMouseButton { ButtonIndex = b, Pressed = true, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() };
                    Mods(ev, p, 2);
                    Input.ParseInputEvent(ev);
                    break;
                }
            case "dblclick":   // dblclick L [mods]: 더블클릭 프레스(DoubleClick=true)만 보낸다. 앞서 press/release로 첫 클릭을 보내 둘 것.
                {
                    var b = Button(p[1]);
                    _held.Add(b);
                    var ev = new InputEventMouseButton { ButtonIndex = b, Pressed = true, DoubleClick = true, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() };
                    Mods(ev, p, 2);
                    Input.ParseInputEvent(ev);
                    break;
                }
            case "release":
                {
                    var b = Button(p[1]);
                    _held.Remove(b);
                    var ev = new InputEventMouseButton { ButtonIndex = b, Pressed = false, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() };
                    Mods(ev, p, 2);
                    Input.ParseInputEvent(ev);
                    break;
                }
            case "drag":
                {
                    var target = ParseXY(p[1], p[2]);
                    const int steps = 8;
                    for (int i = 1; i <= steps; i++)
                    {
                        var next = _pos.Lerp(target, (float)i / steps);
                        var ev = new InputEventMouseMotion { Position = ToGlobal(next), GlobalPosition = ToGlobal(next), Relative = next - _pos, ButtonMask = Mask() };
                        Mods(ev, p, 3);
                        Input.ParseInputEvent(ev);
                        _pos = next;
                    }
                    break;
                }
            case "wheel":
                {
                    int n = int.Parse(p[1]);
                    var b = n > 0 ? MouseButton.WheelUp : MouseButton.WheelDown;
                    for (int i = 0; i < Math.Abs(n); i++)
                    {
                        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = b, Pressed = true, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos) });
                        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = b, Pressed = false, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos) });
                    }
                    break;
                }
            case "key":
                {
                    var key = Enum.Parse<Key>(p[1], ignoreCase: true);
                    var down = new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true };
                    Mods(down, p, 2);
                    var up = new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false };
                    Mods(up, p, 2);
                    Input.ParseInputEvent(down);
                    Input.ParseInputEvent(up);
                    break;
                }
            case "shot":
                {
                    var img = GetViewport().GetTexture().GetImage();
                    GD.Print($"[Drive] shot {p[1]}: {img.SavePng(p[1])}");
                    break;
                }
            case "popup":
                {
                    // popup LABEL VALUE [AXIS]: Action Popup 필드 변경
                    bool ok = UI.Shell.Instance.ActionPopup.DebugSet(p[1].Replace('_', ' '), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), p.Length > 3 ? int.Parse(p[3]) : 0);
                    GD.Print($"[Drive] popup {p[1]}={p[2]} ok={ok}");
                    _wait = Math.Max(_wait, 2);
                    break;
                }
            case "histedit":  // histedit INDEX PARAM VALUE[,Y,Z]: 활성 노드 히스토리 항목의 파라미터를 바꿔 재평가(EditHistoryCommand)
                {
                    var doc = CubeApp.Instance.Document;
                    var node = doc.Find(doc.Selection.ActiveObject) ?? doc.MeshNodes().FirstOrDefault();
                    if (node?.MeshShape == null) { GD.Print("[Drive] histedit: no mesh"); break; }
                    int idx = int.Parse(p[1]);
                    var np = node.MeshShape.History[idx].Params.Clone();
                    var parts2 = p[3].Split(',');
                    var val = np[p[2]].Value;
                    val.X = float.Parse(parts2[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (parts2.Length > 1) val.Y = float.Parse(parts2[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (parts2.Length > 2) val.Z = float.Parse(parts2[2], System.Globalization.CultureInfo.InvariantCulture);
                    np[p[2]].Value = val;
                    doc.Undo.Push(new Core.Commands.EditHistoryCommand(node.Id, idx, np));
                    GD.Print($"[Drive] histedit {idx} {p[2]}={p[3]}");
                    break;
                }
            case "matnew":   // matnew TYPE: 머티리얼을 만들어 선택 오브젝트에 할당
                {
                    var doc = CubeApp.Instance.Document;
                    var type = Enum.TryParse<Core.Scene.MaterialType>(p[1], true, out var mt) ? mt : Core.Scene.MaterialType.Lambert;
                    var mat = new Core.Scene.MaterialDef { Name = doc.UniqueMaterialName(type.ToString().ToLowerInvariant()), Type = type, Color = new System.Numerics.Vector3(0.9f, 0.3f, 0.2f) };
                    var cmd = new Core.Commands.AddMaterialCommand(mat); doc.Undo.Push(cmd);
                    var ids = doc.Selection.Objects.Where(id => doc.Find(id)?.Mesh != null).ToArray();
                    if (ids.Length > 0) doc.Undo.Push(new Core.Commands.AssignMaterialCommand(ids, cmd.Material.Id));
                    GD.Print($"[Drive] matnew {type} id={cmd.Material.Id} assigned={ids.Length}");
                    break;
                }
            case "action":
                GD.Print($"[Drive] action {p[1]}: {UI.Shell.Instance.Actions.Invoke(p[1])}");
                break;
            case "export":
                GD.Print($"[Drive] export: {UI.Shell.Instance.Files.Export(p[1], selectionOnly: p.Length > 2 && p[2] == "selection")}");
                break;
            case "import":
                GD.Print($"[Drive] import: {UI.Shell.Instance.Files.Import(p[1])}");
                break;
            case "skincheck":  // skincheck PATH CLIP TIME: Godot 자체 스킨/애니메이션 결과와 비교용 수치 출력
                {
                    var fd = p[1].EndsWith(".fbx") ? new FbxDocument() : new GltfDocument();
                    var st = p[1].EndsWith(".fbx") ? new FbxState() : new GltfState();
                    fd.AppendFromFile(p[1], st);
                    var scene = fd.GenerateScene(st);
                    var stack = new Stack<Node>(); stack.Push(scene);
                    Skeleton3D? sk = null; MeshInstance3D? mi = null; ImporterMeshInstance3D? imi = null; AnimationPlayer? ap = null;
                    while (stack.Count > 0) { var n = stack.Pop(); if (n is Skeleton3D s3) sk = s3; if (n is MeshInstance3D m3 && m3.Skin != null) mi = m3; if (n is ImporterMeshInstance3D im && im.Skin != null) imi = im; if (n is AnimationPlayer a) ap = a; foreach (var c in n.GetChildren()) stack.Push(c); }
                    var skin = mi?.Skin ?? imi?.Skin;
                    GD.Print($"[Drive] skincheck skel={sk?.Name} mesh={(mi?.Name ?? imi?.Name)} binds={skin?.GetBindCount()} meshXform={(mi as Node3D ?? imi)?.Transform}");
                    if (sk != null && skin != null)
                    {
                        float worst = 0; string worstName = "";
                        for (int b = 0; b < skin.GetBindCount(); b++)
                        {
                            int bone = skin.GetBindBone(b); if (bone < 0) bone = sk.FindBone(skin.GetBindName(b));
                            var restG = sk.GetBoneGlobalRest(bone);
                            var prod = restG * skin.GetBindPose(b);
                            float d = (prod.Origin).Length() + (prod.Basis.X - Vector3.Right).Length() + (prod.Basis.Y - Vector3.Up).Length() + (prod.Basis.Z - Vector3.Back).Length();
                            if (d > worst) { worst = d; worstName = sk.GetBoneName(bone); }
                        }
                        GD.Print($"[Drive] skincheck rest*bind deviation worst={worst:F4} at {worstName}");
                    }
                    if (sk != null && ap != null && p.Length > 3)
                    {
                        var lib = ap.GetAnimationLibrary(""); var names = lib.GetAnimationList();
                        int gi = Array.FindIndex(names.ToArray(), n => n.ToString() == p[2]);
                        if (gi < 0) { GD.Print($"[Drive] skincheck: clip {p[2]} not in file ({string.Join(",", names)})"); scene.Free(); break; }
                        var an = lib.GetAnimation(names[gi]);
                        double t = double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
                        var docA = CubeApp.Instance.Document.Animations;
                        int di = docA.FindIndex(a => a.Name == p[2]);
                        if (di < 0) { GD.Print($"[Drive] skincheck: clip {p[2]} not in document"); scene.Free(); break; }
                        UI.Shell.Instance.Playback.SelectClip(di); UI.Shell.Instance.Playback.SetTime((float)t);
                        GD.Print($"[Drive] skincheck anim length={an.Length} tracks={an.GetTrackCount()}");
                        var clipDoc = docA[di];
                        for (int tr = 0; tr < Math.Min(6, an.GetTrackCount()); tr++)
                        {
                            int kc = an.TrackGetKeyCount(tr);
                            GD.Print($"[Drive]  track {an.TrackGetPath(tr)} {an.TrackGetType(tr)} keys={kc} t0={(kc > 0 ? an.TrackGetKeyTime(tr, 0) : -1)} tN={(kc > 0 ? an.TrackGetKeyTime(tr, kc - 1) : -1)} v0={(kc > 0 ? an.TrackGetKeyValue(tr, 0) : default)} interp@t={(an.TrackGetType(tr) == Animation.TrackType.Rotation3D ? an.RotationTrackInterpolate(tr, t).ToString() : an.TrackGetType(tr) == Animation.TrackType.Position3D ? an.PositionTrackInterpolate(tr, t).ToString() : "")}");
                        }
                        foreach (var ctr in clipDoc.Tracks.Take(4))
                            GD.Print($"[Drive]  ours {ctr.NodeName} P={ctr.Position.Count} R={ctr.Rotation.Count} S={ctr.Scale.Count} rt0={(ctr.Rotation.Count > 0 ? ctr.Rotation[0].Time : -1)} r0={(ctr.Rotation.Count > 0 ? ctr.Rotation[0].Value.ToString() : "")} rAt={(ctr.Rotation.Count > 0 ? Core.Scene.NodeTrack.Sample(ctr.Rotation, (float)t, System.Numerics.Quaternion.Identity).ToString() : "")}");
                        for (int tr = 0; tr < an.GetTrackCount(); tr++)
                        {
                            var path = an.TrackGetPath(tr); if (path.GetSubNameCount() == 0) continue;
                            int bi = sk.FindBone(path.GetSubName(0)); if (bi < 0) continue;
                            switch (an.TrackGetType(tr))
                            {
                                case Animation.TrackType.Position3D: sk.SetBonePosePosition(bi, an.PositionTrackInterpolate(tr, t)); break;
                                case Animation.TrackType.Rotation3D: sk.SetBonePoseRotation(bi, an.RotationTrackInterpolate(tr, t)); break;
                                case Animation.TrackType.Scale3D: sk.SetBonePoseScale(bi, an.ScaleTrackInterpolate(tr, t)); break;
                            }
                        }
                        var doc = CubeApp.Instance.Document;
                        float worst = 0; string wn = "";
                        var glob = new Transform3D[sk.GetBoneCount()];
                        for (int b = 0; b < sk.GetBoneCount(); b++) { int pb2 = sk.GetBoneParent(b); var lp = sk.GetBonePose(b); glob[b] = pb2 >= 0 ? glob[pb2] * lp : lp; }
                        for (int b = 0; b < sk.GetBoneCount(); b++)
                        {
                            var g = glob[b];
                            var jn = doc.Nodes.Values.FirstOrDefault(n => n.IsJoint && n.Name.EndsWith(sk.GetBoneName(b)));
                            if (jn == null) continue;
                            var parentSk = jn; while (parentSk.Parent != null && parentSk.Parent.IsJoint) parentSk = parentSk.Parent;
                            var skelWorld = parentSk.Parent?.WorldMatrix ?? System.Numerics.Matrix4x4.Identity;
                            var ours = (jn.WorldMatrix * (System.Numerics.Matrix4x4.Invert(skelWorld, out var inv) ? inv : System.Numerics.Matrix4x4.Identity)).Translation;
                            float d = (new System.Numerics.Vector3(g.Origin.X, g.Origin.Y, g.Origin.Z) - ours).Length();
                            if (d > worst) { worst = d; wn = sk.GetBoneName(b); }
                        }
                        GD.Print($"[Drive] skincheck pose t={t} clip={p[2]} worst bone position diff={worst:F4} at {wn}");
                        // 메시 변형: Godot 규칙(BONES = 바인드 번호, 정점(스켈레톤 공간) = Σ w · global[bindBone] · bindPose · v)으로 직접 계산한 AABB와 Cube 표시 AABB 비교
                        Mesh? gm = mi?.Mesh ?? imi?.Mesh?.GetMesh();
                        if (gm != null && skin != null)
                        {
                            var gmin = new Vector3(1e9f, 1e9f, 1e9f); var gmax = -gmin;
                            for (int sfi = 0; sfi < gm.GetSurfaceCount(); sfi++)
                            {
                                var arr = gm.SurfaceGetArrays(sfi);
                                var pos = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                                var bns = arr[(int)Mesh.ArrayType.Bones].AsInt32Array(); var wts = arr[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                                int stride = bns.Length / Math.Max(1, pos.Length);
                                for (int v = 0; v < pos.Length; v++)
                                {
                                    var acc = Vector3.Zero;
                                    for (int k = 0; k < stride; k++)
                                    {
                                        float w = wts[v * stride + k]; if (w <= 0) continue;
                                        int bind = bns[v * stride + k];
                                        int bone = skin.GetBindBone(bind); if (bone < 0) bone = sk.FindBone(skin.GetBindName(bind));
                                        acc += w * (glob[bone] * skin.GetBindPose(bind) * pos[v]);
                                    }
                                    gmin = gmin.Min(acc); gmax = gmax.Max(acc);
                                }
                            }
                            var sn = CubeApp.Instance.Document.SkinnedNodes().FirstOrDefault();
                            var mv = sn != null ? UI.Shell.Instance.Viewport.Scene.GetMeshView(sn.Id) : null;
                            var dmin = new System.Numerics.Vector3(1e9f); var dmax = -dmin;
                            if (mv?.Deformed != null) foreach (var dp in mv.Deformed) { dmin = System.Numerics.Vector3.Min(dmin, dp); dmax = System.Numerics.Vector3.Max(dmax, dp); }
                            GD.Print($"[Drive] skincheck mesh godot=<{gmin.X:F3},{gmin.Y:F3},{gmin.Z:F3}>..<{gmax.X:F3},{gmax.Y:F3},{gmax.Z:F3}> cube=<{dmin.X:F3},{dmin.Y:F3},{dmin.Z:F3}>..<{dmax.X:F3},{dmax.Y:F3},{dmax.Z:F3}>");
                        }
                    }
                    scene.Free();
                    break;
                }
            case "confirm":   // 열린 확인 다이얼로그의 OK(Discard)를 누른다
                {
                    var dl = UI.Shell.Instance.FindChildren("*", "ConfirmationDialog", true, false).OfType<ConfirmationDialog>().FirstOrDefault(d => d.Visible);
                    if (dl != null) { dl.Hide(); dl.EmitSignal(AcceptDialog.SignalName.Confirmed); }
                    GD.Print($"[Drive] confirm {(dl != null)}");
                    break;
                }
            case "anim":   // anim play|pause|rest|frame N|clip N|key +1/-1: 애니메이션 재생기 조작
                {
                    var pb = UI.Shell.Instance.Playback;
                    switch (p[1])
                    {
                        case "play": pb.Play(); break;
                        case "pause": pb.Pause(); break;
                        case "rest": pb.Rest(); break;
                        case "frame": pb.SetFrame(int.Parse(p[2])); break;
                        case "clip": pb.SelectClip(int.Parse(p[2])); break;
                        case "key": pb.StepKey(int.Parse(p[2])); break;
                    }
                    GD.Print($"[Drive] anim {p[1]} clip={pb.ClipIndex} frame={pb.Frame} playing={pb.Playing} posed={pb.Posed}");
                    break;
                }
            case "save":
                GD.Print($"[Drive] save: {UI.Shell.Instance.SceneFiles.Save(p[1])} title='{UI.Shell.Instance.SceneFiles.Title}'");
                break;
            case "open":
                GD.Print($"[Drive] open: {UI.Shell.Instance.SceneFiles.Open(p[1])} title='{UI.Shell.Instance.SceneFiles.Title}'");
                break;
            case "axisdrag":   // axisdrag X|Y|Z dist [mods]: 기즈모 축 위(피벗에서 55px)에서 누르고 축 방향으로 dist px 드래그
            case "ringdrag":   // ringdrag X|Y|Z|S dist: 회전 링 위에서 누르고 접선 방향으로 dist px 드래그 (S = 화면 링)
            case "centerdrag": // centerdrag dx dy: 중앙 핸들(피벗 옆 6px)에서 누르고 (dx,dy)만큼 드래그
                {
                    if (UI.Shell.Instance.Tools.Current is not Tools.TransformToolBase t || t.GizmoPublic == null || !t.GizmoPublic.Visible) { GD.PrintErr("[Drive] no visible gizmo"); break; }
                    var g = t.GizmoPublic;
                    var proj = UI.Shell.Instance.Viewport.Picker.Projection();
                    var c0 = proj.Project(g.Pivot, out _) ?? new System.Numerics.Vector2(0, 0);
                    Vector2 pressAt, dragTo;
                    if (p[0] == "centerdrag")
                    {
                        pressAt = new Vector2(c0.X + 6, c0.Y + 6);
                        dragTo = pressAt + new Vector2(float.Parse(p[1]), float.Parse(p[2]));
                    }
                    else if (p[0] == "axisdrag")
                    {
                        var axis = p[1].ToUpperInvariant() switch { "X" => g.AxisX, "Y" => g.AxisY, _ => g.AxisZ };
                        var c1 = proj.Project(g.Pivot + axis * g.WorldUnit, out _) ?? c0;
                        var dir = new Vector2(c1.X - c0.X, c1.Y - c0.Y);
                        dir = dir.Length() > 1e-3f ? dir.Normalized() : new Vector2(1, 0);
                        float dist = float.Parse(p[2]);
                        pressAt = new Vector2(c0.X, c0.Y) + dir * 55f * CubeApp.Instance.UiScale;
                        dragTo = pressAt + dir * dist;
                    }
                    else
                    {
                        float r = Cube.App.Viewport.Gizmos.GizmoBase.ScreenSizePx * CubeApp.Instance.UiScale;
                        float dist = float.Parse(p[2]);
                        if (p[1].ToUpperInvariant() == "S")
                        {
                            r *= Cube.App.Viewport.Gizmos.RotateGizmo.OuterRadius;
                            pressAt = new Vector2(c0.X + r, c0.Y);
                            dragTo = new Vector2(c0.X + r, c0.Y - dist);
                        }
                        else
                        {
                            var axis = p[1].ToUpperInvariant() switch { "X" => g.AxisX, "Y" => g.AxisY, _ => g.AxisZ };
                            // 링 위에서 화면상 중심에서 가장 먼 점을 고른다
                            var helper = MathF.Abs(axis.Y) < 0.9f ? System.Numerics.Vector3.UnitY : System.Numerics.Vector3.UnitX;
                            var u = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(helper, axis)); var v = System.Numerics.Vector3.Cross(axis, u);
                            Vector2 best = new(c0.X + r, c0.Y); float bestD = -1;
                            for (int i = 0; i < 32; i++)
                            {
                                float a = MathF.Tau * i / 32;
                                var w = g.Pivot + (u * MathF.Cos(a) + v * MathF.Sin(a)) * g.WorldUnit;
                                var sp = proj.Project(w, out _); if (sp == null) continue;
                                float d = new Vector2(sp.Value.X, sp.Value.Y).DistanceTo(new Vector2(c0.X, c0.Y));
                                if (d > bestD) { bestD = d; best = new Vector2(sp.Value.X, sp.Value.Y); }
                            }
                            pressAt = best;
                            var radial = (best - new Vector2(c0.X, c0.Y)).Normalized();
                            var tangent = new Vector2(-radial.Y, radial.X);
                            dragTo = best + tangent * dist;
                        }
                    }
                    GD.Print($"[Drive] {p[0]} press={pressAt} to={dragTo}");
                    _pos = pressAt;
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() });
                    _held.Add(MouseButton.Left);
                    var down = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() };
                    Mods(down, p, 3); Input.ParseInputEvent(down);
                    for (int i = 1; i <= 8; i++)
                    {
                        var next = _pos.Lerp(dragTo, (float)i / 8);
                        var ev = new InputEventMouseMotion { Position = ToGlobal(next), GlobalPosition = ToGlobal(next), Relative = next - _pos, ButtonMask = Mask() };
                        Mods(ev, p, 3); Input.ParseInputEvent(ev);
                        _pos = next;
                    }
                    _held.Remove(MouseButton.Left);
                    Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() });
                    break;
                }
            case "keydown":   // keydown NAME [mods]: 누르기만(홀드 테스트)
            case "keyup":
                {
                    var key = Enum.Parse<Key>(p[1], ignoreCase: true);
                    var ev = new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = p[0] == "keydown" };
                    Mods(ev, p, 2);
                    Input.ParseInputEvent(ev);
                    break;
                }
            case "hit":   // hit X Y: 현재 변형 툴 기즈모의 히트 테스트 결과
                {
                    var px = ParseXY(p[1], p[2]);
                    if (UI.Shell.Instance.Tools.Current is Tools.TransformToolBase t && t.GizmoPublic != null)
                        GD.Print($"[Drive] hit {px} -> {t.GizmoPublic.HitTest(new System.Numerics.Vector2(px.X, px.Y), UI.Shell.Instance.Viewport.Picker.Projection())} visible={t.GizmoPublic.Visible} unit={t.GizmoPublic.WorldUnit}");
                    break;
                }
            case "panel":
                {
                    var layout = UI.Shell.Instance.Layout;
                    int idx = int.Parse(p[1]);
                    layout.SetActive(layout.Panels[idx]);
                    GD.Print($"[Drive] panel {idx} ({layout.Panels[idx].CameraController.Label}) size={layout.Panels[idx].Size} quad={layout.IsQuad}");
                    break;
                }
            case "gizmo":
                {
                    if (UI.Shell.Instance.Tools.Current is Tools.TransformToolBase t && t.GizmoPublic != null)
                        GD.Print($"[Drive] gizmo visible={t.GizmoPublic.Visible} pivot={t.GizmoPublic.Pivot} x={t.GizmoPublic.AxisX} y={t.GizmoPublic.AxisY} z={t.GizmoPublic.AxisZ} axis={UI.Shell.Instance.ToolContext.AxisOrientation} view={UI.Shell.Instance.Viewport.CameraController.Label}");
                    else GD.Print("[Drive] gizmo: (no transform tool)");
                    break;
                }
            case "mattex":
                {
                    // mattex ID PATH  : 머티리얼 ID에 컬러 텍스처 경로를 설정한다(빈 PATH면 해제)
                    var doc = CubeApp.Instance.Document;
                    int id = int.Parse(p[1]);
                    var m = doc.FindMaterial(id);
                    if (m == null) { GD.PrintErr($"[Drive] no material {id}"); break; }
                    var after = m.Clone(); after.TexturePath = p.Length > 2 ? string.Join(" ", p.Skip(2)) : null;
                    doc.Undo.Push(new Core.Commands.SetMaterialCommand(id, after));
                    GD.Print($"[Drive] mattex {id} -> {after.TexturePath ?? "(none)"}");
                    break;
                }
            case "shelf":
                {
                    // shelf N : 셸프 탭 전환(0 Polygons, 1 UV, 2 Rigging)
                    UI.Shell.Instance.Shelf.CurrentTab = int.Parse(p[1]);
                    break;
                }
            case "project":
                {
                    // project v|e ID : 활성 메시의 정점/엣지(중점) 뷰포트 로컬 좌표를 찍는다
                    var doc = CubeApp.Instance.Document;
                    var active = doc.Find(doc.Selection.ActiveObject) ?? doc.MeshNodes().FirstOrDefault();
                    if (active?.Mesh == null) { GD.PrintErr("[Drive] no active mesh"); break; }
                    var proj = UI.Shell.Instance.Viewport.Picker.Projection();
                    int id = int.Parse(p[2]);
                    System.Numerics.Vector3 pos;
                    if (p[1] == "e") { var (a, b) = active.Mesh.EdgeVertices(id); pos = (active.Mesh.Verts[a].Position + active.Mesh.Verts[b].Position) * 0.5f; }
                    else pos = active.Mesh.Verts[id].Position;
                    var sp = proj.Project(System.Numerics.Vector3.Transform(pos, active.WorldMatrix), out _);
                    GD.Print($"[Drive] project {p[1]}{id} -> {(sp == null ? "offscreen" : $"{sp.Value.X:F0} {sp.Value.Y:F0}")}");
                    break;
                }
            case "matassign":
                {
                    // matassign ID : 선택 오브젝트에 머티리얼 할당(0 = lambert1)
                    UI.Shell.Instance.AssignMaterialToSelection(int.Parse(p[1]));
                    break;
                }
            case "print":
                {
                    var doc = CubeApp.Instance.Document;
                    GD.Print($"[Drive] nodes={doc.Nodes.Count} sel={doc.Selection.Mode} objs={doc.Selection.Objects.Count} undo={doc.Undo.UndoCount} tool={UI.Shell.Instance.Tools.Current?.Id} shading={UI.Shell.Instance.Viewport.Display.Mode} view={UI.Shell.Instance.Viewport.CameraController.Label} quad={UI.Shell.Instance.Layout.IsQuad} pie={UI.Shell.Instance.Viewport.Pie.IsOpen} cursor={DisplayServer.CursorGetShape()}");
                    GD.Print($"[Drive] {UI.Shell.Instance.ActionPopup.DebugSummary()} dialogs={UI.Shell.Instance.FindChildren("*", "ConfirmationDialog", true, false).Count(n => n is Window w && w.Visible)}");
                    if (doc.Animations.Count > 0)
                    {
                        var pb = UI.Shell.Instance.Playback;
                        GD.Print($"[Drive] anims={doc.Animations.Count} {string.Join(",", doc.Animations.Select(a => $"{a.Name}:{a.Length:F2}s@{a.FrameRate:0.#}/{a.Tracks.Count}tr/{a.KeyCount}k"))} current={pb.ClipIndex} frame={pb.Frame}/{pb.EndFrame} playing={pb.Playing} posed={pb.Posed} posedNodes={doc.Nodes.Values.Count(n => n.Pose != null)}");
                    }
                    var lights = doc.LightNodes().ToList();
                    if (lights.Count > 0) GD.Print($"[Drive] lights={lights.Count} {string.Join(",", lights.Select(l => $"{l.Name}:{l.Light!.Type}/{l.Light.Intensity:F1}"))} materials={doc.Materials.Count} {string.Join(",", doc.Materials.Select(m => m.Name + ":" + m.Type))}");
                    else if (doc.Materials.Count > 0) GD.Print($"[Drive] materials={doc.Materials.Count} {string.Join(",", doc.Materials.Select(m => m.Name + ":" + m.Type))}");
                    var joints = doc.JointNodes().ToList();
                    if (joints.Count > 0) GD.Print($"[Drive] joints={joints.Count} {string.Join(",", joints.Select(j => $"{j.Name}@<{j.WorldMatrix.Translation.X:F2},{j.WorldMatrix.Translation.Y:F2},{j.WorldMatrix.Translation.Z:F2}>R<{j.Local.RotationDegrees.X:F0},{j.Local.RotationDegrees.Y:F0},{j.Local.RotationDegrees.Z:F0}>{(j.Parent is { IsRoot: false } p ? "<" + p.Name : "")}"))}");
                    foreach (var sn in doc.SkinnedNodes())
                    {
                        var sk = sn.Skin!; int weighted = sk.Weights.Count(w => w != null && w.Count > 0);
                        var mvw = UI.Shell.Instance.Viewport.Scene.GetMeshView(sn.Id);
                        var dmin = new System.Numerics.Vector3(float.MaxValue); var dmax = new System.Numerics.Vector3(float.MinValue);
                        if (mvw?.Deformed != null) foreach (var dp in mvw.Deformed) { dmin = System.Numerics.Vector3.Min(dmin, dp); dmax = System.Numerics.Vector3.Max(dmax, dp); }
                        GD.Print($"[Drive] skin {sn.Name}: joints={sk.Joints.Count} weighted={weighted} w(v0)={(sk.Weights.Length > 0 && sk.Weights[0] != null ? string.Join("|", sk.Weights[0]!.Select(w => $"{w.joint}:{w.weight:F2}")) : "-")} deformedMax=<{dmax.X:F3},{dmax.Y:F3},{dmax.Z:F3}>");
                    }
                    var active = doc.Find(doc.Selection.ActiveObject) ?? doc.MeshNodes().FirstOrDefault();
                    if (active?.MeshShape is { } ms0)
                    {
                        var hist = string.Join(" | ", ms0.History.Select((h, i) => $"{i}:{h.Name}" + (h.Editable ? "(" + string.Join(",", h.Params.Items.Select(pp => pp.Name + "=" + (pp.Kind == Core.Commands.HistoryParamKind.Vector3 ? $"{pp.Value.X:F2},{pp.Value.Y:F2},{pp.Value.Z:F2}" : pp.Value.X.ToString("F3")))) + ")" : "")));
                        var uv0 = ms0.Mesh.Hes.Count > 0 ? ms0.Mesh.Hes[0].Uv0 : default;
                        GD.Print($"[Drive] mesh {active.Name}: faces={ms0.Mesh.AliveFaceCount} verts={ms0.Mesh.AliveVertexCount} smoothPreview={ms0.SmoothPreview} uv0=<{uv0.X:F3},{uv0.Y:F3}> history=[{hist}]");
                    }
                    if (active?.Mesh != null)
                    {
                        var mn = new System.Numerics.Vector3(float.MaxValue); var mx = new System.Numerics.Vector3(float.MinValue);
                        foreach (var v in active.Mesh.Verts) if (v.Alive) { mn = System.Numerics.Vector3.Min(mn, v.Position); mx = System.Numerics.Vector3.Max(mx, v.Position); }
                        GD.Print($"[Drive] active={active.Name} material={active.MaterialId} local={active.Local} meshMin=<{mn.X:F3},{mn.Y:F3},{mn.Z:F3}> meshMax=<{mx.X:F3},{mx.Y:F3},{mx.Z:F3}> comps={string.Join("|", doc.Selection.Components.Select(kv => $"{kv.Key}:v{kv.Value.Verts.Count}/e{kv.Value.Edges.Count}/f{kv.Value.Faces.Count}/u{kv.Value.Uvs.Count}"))}");
                    }
                    break;
                }
            default: GD.PrintErr($"[Drive] unknown step {p[0]}"); break;
        }
    }

    private MouseButtonMask Mask()
    {
        MouseButtonMask m = 0;
        foreach (var b in _held) m |= b switch { MouseButton.Left => MouseButtonMask.Left, MouseButton.Middle => MouseButtonMask.Middle, MouseButton.Right => MouseButtonMask.Right, _ => 0 };
        return m;
    }

    private static MouseButton Button(string s) => s.ToUpperInvariant() switch { "L" => MouseButton.Left, "M" => MouseButton.Middle, "R" => MouseButton.Right, _ => throw new ArgumentException(s) };

    private static void Mods(InputEventWithModifiers ev, string[] p, int from)
    {
        for (int i = from; i < p.Length; i++)
            switch (p[i].ToLowerInvariant())
            {
                case "alt": ev.AltPressed = true; break;
                case "shift": ev.ShiftPressed = true; break;
                case "ctrl": ev.CtrlPressed = true; break;
            }
    }
}
