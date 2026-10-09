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
/// <remarks>
/// 위 목록 외에도 많은 스텝이 있다(CLAUDE.md "자동 조작으로 검증하기" 참고). 각 스텝은 <see cref="Exec"/>의 case 하나다.
/// 동작 방식: 스크립트를 ';'로 나눠 큐에 넣고 <see cref="_Process"/>가 프레임마다 대기 카운터가 0인 동안 스텝을 연속 실행한다.
/// 입력 이벤트는 Input.ParseInputEvent로 주입하며 다음 입력 플러시에 처리되므로 입력 스텝 뒤에는 자동으로 한 프레임 쉰다.
/// 마우스 위치는 활성 뷰포트 로컬 좌표(<c>_pos</c>)로 추적하고 이벤트에는 전역 좌표로 바꿔 넣는다(OS 커서는 움직이지 않음).
/// 스크립트가 끝나면 "[Drive] done"을 찍고 자신을 해제한다.
/// </remarks>
public partial class DebugDriver : Node
{
    /// <summary>실행 대기 중인 스텝(공백으로 구분된 한 줄 명령).</summary>
    private readonly Queue<string> _steps = new();
    /// <summary>남은 대기 프레임 수(wait N, 입력 스텝 뒤 1프레임 등).</summary>
    private int _wait;
    /// <summary>현재 가상 마우스 위치(활성 뷰포트 로컬 픽셀). 기본은 1600×900 창 가운데쯤.</summary>
    private Vector2 _pos = new(800, 450);
    /// <summary>현재 눌려 있는 마우스 버튼(모션 이벤트의 ButtonMask 계산용).</summary>
    private readonly HashSet<MouseButton> _held = new();

    /// <summary>드라이브 스크립트를 세미콜론으로 나눠 빈 스텝을 빼고 큐에 넣는다.</summary>
    public DebugDriver(string script)
    {
        foreach (var s in script.Split(';')) { var t = s.Trim(); if (t.Length > 0) _steps.Enqueue(t); }
    }

    /// <summary>현재 활성 뷰포트 패널(로컬 ↔ 전역 좌표 변환 기준). 셸이 없으면 null.</summary>
    private Control? Viewport => UI.Shell.Instance?.Viewport;

    /// <summary>프레임마다: UI 성능 측정 틱 → 대기 중이면 하나 줄이고 끝 → 아니면 대기가 생길 때까지 스텝을 연속 실행. 오류는 출력만 하고 계속 진행.</summary>
    public override void _Process(double delta)
    {
        UiPerf.Tick();
        if (_wait > 0) { _wait--; return; }
        while (_steps.Count > 0 && _wait == 0)
        {
            var step = _steps.Dequeue();
            var parts = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try { Exec(parts); }
            catch (Exception ex) { GD.PrintErr($"[Drive] '{step}': {ex.Message}"); }
            // 주입된 입력 이벤트는 다음 입력 플러시에서 처리되므로 입력 스텝 뒤에는 한 프레임 양보한다
            if (parts[0] is "move" or "gmove" or "gdrag" or "grab" or "grip" or "dragger" or "press" or "dblclick" or "release" or "drag" or "wheel" or "key" or "axisdrag" or "ringdrag" or "centerdrag" or "keydown" or "keyup") _wait = Math.Max(_wait, 1);
        }
        if (_steps.Count == 0 && _wait == 0) { GD.Print("[Drive] done"); QueueFree(); }
    }

    /// <summary>뷰포트 로컬 좌표 → 창 전역 좌표.</summary>
    private Vector2 ToGlobal(Vector2 local) => Viewport != null ? Viewport.GlobalPosition + local : local;

    /// <summary>"c+10" / "c-50" 처럼 뷰포트 중심 기준 좌표도 허용한다.</summary>
    /// <remarks>"c"만 쓰면 중심, 그 밖에는 일반 실수 픽셀 값.</remarks>
    private Vector2 ParseXY(string xs, string ys)
    {
        var size = Viewport?.Size ?? new Vector2(1000, 700);
        float Parse(string t, float center) => t.StartsWith("c") ? center + (t.Length > 1 ? float.Parse(t[1..]) : 0) : float.Parse(t);
        return new Vector2(Parse(xs, size.X / 2), Parse(ys, size.Y / 2));
    }

    /// <summary>스텝 하나를 실행한다. p[0] = 스텝 이름, 나머지 = 인자. 모르는 스텝은 오류를 출력한다.</summary>
    private void Exec(string[] p)
    {
        switch (p[0])
        {
            // wait N: N프레임 동안 다음 스텝을 미룬다
            case "wait": _wait = int.Parse(p[1]); break;
            case "gmove":   // gmove X Y: 창 전역 좌표로 이동(뷰포트 밖 UI용)
                _pos = new Vector2(float.Parse(p[1]), float.Parse(p[2])) - (Viewport?.GlobalPosition ?? Vector2.Zero);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), Relative = Vector2.Zero, ButtonMask = Mask() });
                break;
            case "gdragn":  // gdragn X Y N: 전역 좌표까지 N프레임에 걸쳐 한 프레임에 한 번씩 이동(실제 드래그처럼; 성능 측정용)
                {
                    var target = new Vector2(float.Parse(p[1]), float.Parse(p[2])) - (Viewport?.GlobalPosition ?? Vector2.Zero);
                    int n = Math.Max(1, int.Parse(p[3]));
                    var start = _pos;
                    var rest = _steps.ToList(); _steps.Clear();
                    for (int i = 1; i <= n; i++) { var g = ToGlobal(start.Lerp(target, i / (float)n)); _steps.Enqueue($"gmove {g.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} {g.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}"); }
                    foreach (var s in rest) _steps.Enqueue(s);
                    break;
                }
            case "perf":    // perf start LABEL | perf stop: --uiperf 측정 구간
                if (p[1] == "start") UiPerf.Start(p.Length > 2 ? p[2] : "drag"); else UiPerf.Stop();
                break;
            case "grip":    // grip PANELID: 떠 있는 패널의 우하단 크기 조절 그립 위로 커서를 옮긴다
                {
                    var panel = UI.Shell.Instance.FindChildren("*", "", true, false).OfType<UI.FloatingPanel>().FirstOrDefault(f => f.PanelId == p[1]);
                    if (panel == null) { GD.Print($"[Drive] grip: no panel {p[1]}"); break; }
                    var g = panel.GlobalPosition + panel.Size - new Vector2(7, 7) * CubeApp.Instance.UiScale;
                    _pos = g - (Viewport?.GlobalPosition ?? Vector2.Zero);
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), Relative = Vector2.Zero, ButtonMask = Mask() });
                    GD.Print($"[Drive] grip {p[1]} at {g}");
                    break;
                }
            case "dragger": // dragger NAME [I]: 스플리터(MainSplit/RightSplit/LeftDock/RightDock/DockRow)의 I번째 경계 위로 커서를 옮긴다
                {
                    var sc = UI.Shell.Instance.FindChildren(p[1], "SplitContainer", true, false).OfType<SplitContainer>().FirstOrDefault(c => c.IsVisibleInTree());
                    if (sc == null) { GD.Print($"[Drive] dragger: no split {p[1]}"); break; }
                    int i = p.Length > 2 ? int.Parse(p[2]) : 0;
                    var kids = sc.GetChildren().OfType<Control>().Where(c => c.Visible && !c.TopLevel).ToList();
                    if (i + 1 >= kids.Count) { GD.Print($"[Drive] dragger: {p[1]} has {kids.Count} children"); break; }
                    var a = kids[i].GetGlobalRect(); var b = kids[i + 1].GetGlobalRect();
                    var g = sc.Vertical ? new Vector2(a.GetCenter().X, (a.End.Y + b.Position.Y) / 2) : new Vector2((a.End.X + b.Position.X) / 2, a.GetCenter().Y);
                    _pos = g - (Viewport?.GlobalPosition ?? Vector2.Zero);
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), Relative = Vector2.Zero, ButtonMask = Mask() });
                    GD.Print($"[Drive] dragger {p[1]}[{i}] at {g} (gap {(sc.Vertical ? b.Position.Y - a.End.Y : b.Position.X - a.End.X):0})");
                    break;
                }
            case "gdrag":   // gdrag X Y: 버튼을 누른 채 전역 좌표까지 8단계로 이동
                {
                    var target = new Vector2(float.Parse(p[1]), float.Parse(p[2])) - (Viewport?.GlobalPosition ?? Vector2.Zero);
                    for (int i = 1; i <= 8; i++)
                    {
                        var next = _pos.Lerp(target, i / 8f);
                        Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(next), GlobalPosition = ToGlobal(next), Relative = next - _pos, ButtonMask = Mask() });
                        _pos = next;
                    }
                    break;
                }
            case "grab":    // grab PANELID: 떠 있으면 제목 바, 도크에 붙어 있으면 탭 위로 커서를 옮긴다
                {
                    var panel = UI.Shell.Instance.FindChildren("*", "", true, false).OfType<UI.FloatingPanel>().FirstOrDefault(f => f.PanelId == p[1]);
                    if (panel == null) { GD.Print($"[Drive] grab: no panel {p[1]}"); break; }
                    Vector2 g;
                    if (panel.Docked && panel.GetParent() is UI.Docking.DockGroup grp)
                    {
                        var bar = grp.GetTabBar();
                        g = bar.GlobalPosition + bar.GetTabRect(grp.GetTabIdxFromControl(panel)).GetCenter();
                    }
                    else g = panel.GlobalPosition + new Vector2(panel.Size.X * 0.4f, 12 * CubeApp.Instance.UiScale);
                    _pos = g - (Viewport?.GlobalPosition ?? Vector2.Zero);
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), Relative = Vector2.Zero, ButtonMask = Mask() });
                    GD.Print($"[Drive] grab {p[1]} at {g} docked={panel.Docked}");
                    break;
                }
            // dockinfo: 도킹 레이아웃 요약(DockManager.Summary)을 찍는다
            case "dockinfo":
                GD.Print($"[Drive] dock {UI.Shell.Instance.Dock.Summary()}");
                break;
            // move X Y: 뷰포트 로컬 좌표로 커서 이동(버튼이 눌려 있으면 그 마스크 포함)
            case "move":
                _pos = ParseXY(p[1], p[2]);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), Relative = Vector2.Zero, ButtonMask = Mask() });
                break;
            // press L|M|R [mods]: 현재 위치에서 버튼 누름
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
            // release L|M|R [mods]: 현재 위치에서 버튼 뗌
            case "release":
                {
                    var b = Button(p[1]);
                    _held.Remove(b);
                    var ev = new InputEventMouseButton { ButtonIndex = b, Pressed = false, Position = ToGlobal(_pos), GlobalPosition = ToGlobal(_pos), ButtonMask = Mask() };
                    Mods(ev, p, 2);
                    Input.ParseInputEvent(ev);
                    break;
                }
            // drag X Y [mods]: 현재 위치에서 (X,Y)까지 8단계 모션(눌린 버튼 유지)
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
            // wheel N: 휠 N틱(양수 = 위/줌인, 음수 = 아래). 틱마다 누름+뗌 쌍
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
            // key NAME [mods]: 키 누름+뗌(Godot Key 열거형 이름, 대소문자 무시)
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
            // shot PATH: 메인 창 화면을 PNG로 저장
            case "shot":
                {
                    var img = GetViewport().GetTexture().GetImage();
                    GD.Print($"[Drive] shot {p[1]}: {img.SavePng(p[1])}");
                    break;
                }
            // popup LABEL VALUE [AXIS]: Action Popup 필드 값을 바꿔 재적용(적용에 2프레임 대기)
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
            // action ID: ActionRegistry로 액션을 실행하고 성공 여부를 찍는다
            case "action":
                GD.Print($"[Drive] action {p[1]}: {UI.Shell.Instance.Actions.Invoke(p[1])}");
                break;
            // export PATH [selection]: FileActions로 내보내기(selection이면 선택만)
            case "export":
                GD.Print($"[Drive] export: {UI.Shell.Instance.Files.Export(p[1], selectionOnly: p.Length > 2 && p[2] == "selection")}");
                break;
            // import PATH: FileActions로 가져오기
            case "import":
                GD.Print($"[Drive] import: {UI.Shell.Instance.Files.Import(p[1])}");
                break;
            case "skincheck":  // skincheck PATH CLIP TIME: Godot 자체 스킨/애니메이션 결과와 비교용 수치 출력
                {
                    // 파일을 Godot으로 직접 읽어 스켈레톤·스킨 메시·AnimationPlayer를 찾는다
                    var fd = p[1].EndsWith(".fbx") ? new FbxDocument() : new GltfDocument();
                    var st = p[1].EndsWith(".fbx") ? new FbxState() : new GltfState();
                    fd.AppendFromFile(p[1], st);
                    var scene = fd.GenerateScene(st);
                    var stack = new Stack<Node>(); stack.Push(scene);
                    Skeleton3D? sk = null; MeshInstance3D? mi = null; ImporterMeshInstance3D? imi = null; AnimationPlayer? ap = null;
                    while (stack.Count > 0) { var n = stack.Pop(); if (n is Skeleton3D s3) sk = s3; if (n is MeshInstance3D m3 && m3.Skin != null) mi = m3; if (n is ImporterMeshInstance3D im && im.Skin != null) imi = im; if (n is AnimationPlayer a) ap = a; foreach (var c in n.GetChildren()) stack.Push(c); }
                    var skin = mi?.Skin ?? imi?.Skin;
                    GD.Print($"[Drive] skincheck skel={sk?.Name} mesh={(mi?.Name ?? imi?.Name)} binds={skin?.GetBindCount()} meshXform={(mi as Node3D ?? imi)?.Transform}");
                    // ① rest 포즈 검사: 본 전역 rest × 바인드 포즈가 단위 행렬에서 얼마나 벗어나는지(최대 편차 본)
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
                    // ② 포즈 검사: 클립을 TIME에 평가해 Godot 스켈레톤 본 위치와 Cube 조인트 위치를 비교
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
                        // Godot 애니메이션 트랙을 시간 t로 보간해 스켈레톤 본 포즈에 적용
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
                        // 본 전역 포즈 = 부모 전역 × 로컬 포즈(본 인덱스는 부모가 먼저라고 가정)
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
                            // 가져오기가 머티리얼별로 메시를 나누므로 모든 스킨 메시의 변형 위치를 합친다
                            var dmin = new System.Numerics.Vector3(1e9f); var dmax = -dmin;
                            foreach (var sn in CubeApp.Instance.Document.SkinnedNodes())
                            {
                                var mv = UI.Shell.Instance.Viewport.Scene.GetMeshView(sn.Id);
                                if (mv?.Deformed != null) foreach (var dp in mv.Deformed) { dmin = System.Numerics.Vector3.Min(dmin, dp); dmax = System.Numerics.Vector3.Max(dmax, dp); }
                            }
                            GD.Print($"[Drive] skincheck mesh godot=<{gmin.X:F3},{gmin.Y:F3},{gmin.Z:F3}>..<{gmax.X:F3},{gmax.Y:F3},{gmax.Z:F3}> cube=<{dmin.X:F3},{dmin.Y:F3},{dmin.Z:F3}>..<{dmax.X:F3},{dmax.Y:F3},{dmax.Z:F3}>");
                        }
                    }
                    scene.Free();
                    break;
                }
            case "comped":    // comped tab|sel|world|edit|select|rows ... (Component Editor; 열려 있지 않으면 연다)
                {
                    var sh = UI.Shell.Instance;
                    if (sh.ComponentEditor?.IsOpen != true) sh.Actions.Invoke("edit.componentEditor");
                    GD.Print($"[Drive] comped {string.Join(' ', p.Skip(1))}: {sh.ComponentEditor!.Drive(p.Skip(1).ToArray())}");
                    break;
                }
            case "logsave":   // logsave PATH: 로그 패널의 현재 필터로 파일 저장
                GD.Print($"[Drive] logsave {UI.Shell.Instance.SaveLog(p[1])}");
                break;
            case "logcopy":   // 로그를 클립보드에 복사하고 줄 수를 찍는다
                UI.Shell.Instance.Actions.Invoke("log.copy");
                GD.Print($"[Drive] logcopy lines={DisplayServer.ClipboardGet().Count(c => c == '\n')} counts={LogCapture.Instance?.Counts}");
                break;
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
            // save PATH: .cube 저장 후 창 제목 확인
            case "save":
                GD.Print($"[Drive] save: {UI.Shell.Instance.SceneFiles.Save(p[1])} title='{UI.Shell.Instance.SceneFiles.Title}'");
                break;
            // open PATH: .cube 열기 후 창 제목 확인
            case "open":
                GD.Print($"[Drive] open: {UI.Shell.Instance.SceneFiles.Open(p[1])} title='{UI.Shell.Instance.SceneFiles.Title}'");
                break;
            case "axisdrag":   // axisdrag X|Y|Z dist [mods]: 기즈모 축 위(피벗에서 55px)에서 누르고 축 방향으로 dist px 드래그
            case "ringdrag":   // ringdrag X|Y|Z|S dist: 회전 링 위에서 누르고 접선 방향으로 dist px 드래그 (S = 화면 링)
            case "centerdrag": // centerdrag dx dy: 중앙 핸들(피벗 옆 6px)에서 누르고 (dx,dy)만큼 드래그
                {
                    // 현재 툴이 보이는 기즈모를 가진 변형 툴이어야 한다. 피벗을 화면에 투영해 기준점 c0로 삼는다
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
                    // 계산한 위치로 이동 → 왼쪽 버튼 누름 → 8단계 드래그 → 뗌
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
            // keyup NAME [mods]: 홀드한 키 떼기
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
            // panel N: 활성 뷰포트 패널 선택(0 top, 1 persp, 2 front, 3 side)
            case "panel":
                {
                    var layout = UI.Shell.Instance.Layout;
                    int idx = int.Parse(p[1]);
                    layout.SetActive(layout.Panels[idx]);
                    GD.Print($"[Drive] panel {idx} ({layout.Panels[idx].CameraController.Label}) size={layout.Panels[idx].Size} quad={layout.IsQuad}");
                    break;
                }
            // gizmo: 현재 변형 툴 조작기의 피벗·축·축 방향 모드를 찍는다
            case "gizmo":
                {
                    if (UI.Shell.Instance.Tools.Current is Tools.TransformToolBase t && t.GizmoPublic != null)
                        GD.Print($"[Drive] gizmo visible={t.GizmoPublic.Visible} screen={UI.Shell.Instance.Viewport.Picker.Projection().Project(t.GizmoPublic.Pivot, out _)} pivot={t.GizmoPublic.Pivot} x={t.GizmoPublic.AxisX} y={t.GizmoPublic.AxisY} z={t.GizmoPublic.AxisZ} axis={UI.Shell.Instance.ToolContext.AxisOrientation} view={UI.Shell.Instance.Viewport.CameraController.Label}");
                    else GD.Print("[Drive] gizmo: (no transform tool)");
                    break;
                }
            // mattex ID PATH: 머티리얼 컬러 텍스처 지정(빈 PATH면 해제)
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
            // matset / matmap: 머티리얼 파라미터 값 / 텍스처 지정
            case "matset":
            case "matmap":
                {
                    // matset ID KEY VALUE[,Y,Z] : 머티리얼 파라미터 값 / matmap ID KEY [PATH] : 파라미터 텍스처(빈 PATH면 해제)
                    var doc = CubeApp.Instance.Document;
                    int id = int.Parse(p[1]);
                    var m = doc.FindMaterial(id);
                    if (m == null) { GD.PrintErr($"[Drive] no material {id}"); break; }
                    var after = m.Clone();
                    if (p[0] == "matset")
                    {
                        var f = p[3].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        after.Set(p[2], new System.Numerics.Vector3(f[0], f.Length > 1 ? f[1] : 0, f.Length > 2 ? f[2] : 0));
                    }
                    else after.SetTex(p[2], p.Length > 3 ? string.Join(" ", p.Skip(3)) : null);
                    doc.Undo.Push(new Core.Commands.SetMaterialCommand(id, after));
                    GD.Print($"[Drive] {p[0]} {id} {p[2]} = {(p[0] == "matset" ? after.Get(p[2]).ToString() : after.Tex(p[2]) ?? "(none)")}");
                    break;
                }
            case "matpie":   // matpie: Assign Material 서브 파이(썸네일 포함)를 활성 뷰포트 가운데에 연다
                {
                    var vp = UI.Shell.Instance.Viewport;
                    vp.Pie.Open(UI.PieMenus.MaterialItems(UI.Shell.Instance), vp.Size / 2, sticky: true, title: "Assign Material ▸");
                    GD.Print($"[Drive] matpie open={vp.Pie.IsOpen}");
                    break;
                }
            // mkpng PATH R G B [SIZE]: 테스트용 그라디언트 PNG 생성
            case "mkpng":
                {
                    // mkpng PATH R G B [SIZE] : 테스트용 그라디언트 PNG(R/G/B 0..255 기준색, 가로로 밝기 0→1)
                    int size = p.Length > 5 ? int.Parse(p[5]) : 16;
                    var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            float t = (x + 0.5f) / size;
                            img.SetPixel(x, y, new Color(int.Parse(p[2]) / 255f * t, int.Parse(p[3]) / 255f * t, int.Parse(p[4]) / 255f * t, 1f));
                        }
                    GD.Print($"[Drive] mkpng {p[1]}: {img.SavePng(p[1])}");
                    break;
                }
            // shelf N: 셸프 탭 전환
            case "shelf":
                {
                    // shelf N : 셸프 탭 전환(0 Polygons, 1 UV, 2 Rigging)
                    UI.Shell.Instance.Shelf.CurrentTab = int.Parse(p[1]);
                    break;
                }
            // project v|e ID: 정점/엣지 중점의 뷰포트 로컬 좌표(툴 드라이브 좌표 구하기)
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
            // matassign ID: 선택에 머티리얼 할당
            case "matassign":
                {
                    // matassign ID : 선택 오브젝트에 머티리얼 할당(0 = lambert1)
                    UI.Shell.Instance.AssignMaterialToSelection(int.Parse(p[1]));
                    break;
                }
            // print: 문서·선택·Undo·툴·뷰·Action Popup·애니메이션·라이트·머티리얼·조인트·스킨·활성 메시(히스토리, AABB, 컴포넌트 수) 상태를 여러 줄로 찍는다
            case "print":
                {
                    var doc = CubeApp.Instance.Document;
                    GD.Print($"[Drive] nodes={doc.Nodes.Count} sel={doc.Selection.Mode} objs={doc.Selection.Objects.Count} undo={doc.Undo.UndoCount} tool={UI.Shell.Instance.Tools.Current?.Id} shading={UI.Shell.Instance.Viewport.Display.Mode} view={UI.Shell.Instance.Viewport.CameraController.Label} quad={UI.Shell.Instance.Layout.IsQuad} pie={UI.Shell.Instance.Viewport.Pie.IsOpen} cursor={DisplayServer.CursorGetShape()} vpSubs={UI.Shell.Instance.ToolContext.ViewportChangedSubscribers}");
                    GD.Print($"[Drive] {UI.Shell.Instance.ActionPopup.DebugSummary()} dialogs={UI.Shell.Instance.FindChildren("*", "ConfirmationDialog", true, false).Count(n => n is Window w && w.Visible)}");
                    if (doc.Animations.Count > 0)
                    {
                        var pb = UI.Shell.Instance.Playback;
                        GD.Print($"[Drive] anims={doc.Animations.Count} {string.Join(",", doc.Animations.Select(a => $"{a.Name}:{a.Length:F2}s@{a.FrameRate:0.#}/{a.Tracks.Count}tr/{a.KeyCount}k"))} current={pb.ClipIndex} frame={pb.Frame}/{pb.EndFrame} playing={pb.Playing} posed={pb.Posed} posedNodes={doc.Nodes.Values.Count(n => n.Pose != null)} fastpath=[{Bridge.GodotMeshBridge.FastPathState}]{(Bridge.GodotMeshBridge.FastPathDisabledReason != null ? " disabled: " + Bridge.GodotMeshBridge.FastPathDisabledReason : "")}");
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
            // piesweep: 현재 선택 모드의 Edit 파이(Shift+RMB) 항목 중 활성인 것을 하나씩 실행하고 Undo로 되돌린다(파일 대화상자 항목 제외).
            // 예외와 실행 결과(만든 Undo 단계 수)를 찍는다. 툴 전환 항목은 Select 툴로 되돌린다.
            case "piesweep":
                {
                    var shell = UI.Shell.Instance; var doc = CubeApp.Instance.Document;
                    var items = UI.PieMenus.ContextMenu(shell);
                    var report = new List<string>();
                    foreach (var it in items)
                    {
                        if (it.Sub != null || it.Run != null || it.ActionId.StartsWith("file.")) continue;
                        if (!it.Enabled) { report.Add($"{it.ActionId}:disabled"); continue; }
                        var selBefore = doc.Selection.Capture();
                        int undoBefore = doc.Undo.UndoCount;
                        string res;
                        try
                        {
                            bool ok = shell.Actions.Invoke(it.ActionId);
                            int made = doc.Undo.UndoCount - undoBefore;
                            res = $"{it.ActionId}:{(ok ? "ok" : "fail")}+{made}";
                            while (doc.Undo.UndoCount > undoBefore && doc.Undo.CanUndo) doc.Undo.Undo();
                        }
                        catch (Exception ex) { res = $"{it.ActionId}:EXCEPTION {ex.GetType().Name}: {ex.Message}"; }
                        if (shell.Tools.Current?.Id != "select") shell.Tools.SetTool("select");
                        doc.Selection.Restore(selBefore);
                        report.Add(res);
                    }
                    GD.Print($"[Drive] piesweep {doc.Selection.Mode}: {string.Join(" ", report)}");
                    break;
                }
            // cam: 활성 패널 카메라 상태(피벗·거리·yaw/pitch·직교 크기)
            case "cam":
                {
                    var c = UI.Shell.Instance.Viewport.CameraController;
                    var st = c.State;
                    GD.Print($"[Drive] cam {c.Label} pivot=<{st.Pivot.X:F2},{st.Pivot.Y:F2},{st.Pivot.Z:F2}> dist={st.Distance:F2} yaw={st.Yaw * 180 / MathF.PI:F1} pitch={st.Pitch * 180 / MathF.PI:F1} ortho={st.IsOrtho} size={st.OrthoSize:F2}");
                    break;
                }
            // quad 0|1: 단일/4분할 레이아웃을 명시적으로 맞춘다(시작 상태가 공유 settings.json에 따라 달라지므로 토글 대신)
            case "quad":
                {
                    var shell = UI.Shell.Instance;
                    if (shell.Layout.IsQuad != (p[1] != "0")) shell.Actions.Invoke("view.toggleLayout");
                    GD.Print($"[Drive] quad={shell.Layout.IsQuad}");
                    break;
                }
            // hidefloat: 떠 있는(도킹 안 된) 플로팅 패널을 모두 숨긴다(뷰포트 위를 덮어 주입한 클릭을 가로채지 않게; 설정은 저장하지 않음)
            case "hidefloat":
                {
                    int n = 0;
                    foreach (var fp in UI.Shell.Instance.FindChildren("*", "", true, false).OfType<UI.FloatingPanel>())
                        if (fp.Visible && !fp.Docked) { fp.Visible = false; n++; }
                    GD.Print($"[Drive] hidefloat {n}");
                    break;
                }
            // focusedit: Properties 패널의 첫 숫자 칸(LineEdit)에 키보드 포커스를 준다(텍스트 입력 중 단축키 무시 확인용)
            case "focusedit":
                {
                    var le = UI.Shell.Instance.PropertiesWindow.FindChildren("*", "", true, false).OfType<LineEdit>().FirstOrDefault(l => l.IsVisibleInTree());
                    le?.GrabFocus();
                    GD.Print($"[Drive] focusedit {(le == null ? "(none)" : le.GetPath().ToString())} focus={GetViewport().GuiGetFocusOwner()?.GetType().Name}");
                    break;
                }
            // hover: 현재 마우스 아래 GUI 컨트롤 경로(입력을 가로채는 위젯 확인용)
            case "hover":
                {
                    var c = GetViewport().GuiGetHoveredControl();
                    GD.Print($"[Drive] hover {_pos} -> {(c == null ? "(none)" : c.GetPath().ToString())}");
                    break;
                }
            // retain 0|1: Retain Component Spacing(점 스냅 collapse 여부)를 메모리에서만 바꾼다(저장하지 않음; 드라이브 끝에 되돌릴 것)
            case "retain":
                CubeApp.Instance.Settings.RetainComponentSpacing = p[1] != "0";
                GD.Print($"[Drive] retain={CubeApp.Instance.Settings.RetainComponentSpacing}");
                break;
            // actioncheck: 메뉴·셸프·핫키·파이(모든 선택 모드, 서브 파이 포함)가 가리키는 ActionId 중 등록되지 않은 것과
            // 같은 (키 조합, 컨텍스트)에 서로 다른 액션이 묶인 핫키 충돌을 찍는다
            case "actioncheck":
                {
                    var shell = UI.Shell.Instance; var reg = shell.Actions; var sel = CubeApp.Instance.Document.Selection;
                    var missing = new SortedSet<string>();
                    void Check(string src, string id) { if (reg.Get(id) == null) missing.Add($"{src}:{id}"); }
                    foreach (var id in shell.Menus.ReferencedActions) Check("menu", id);
                    foreach (var id in shell.ShelfActions) Check("shelf", id);
                    foreach (var b in shell.Hotkeys.Map.Bindings) Check("hotkey", b.Action);
                    void Pie(string src, List<UI.PieItem> items, int depth)
                    {
                        foreach (var it in items)
                        {
                            if (it.Sub != null) { if (depth < 3) Pie(src + ">" + it.Label, it.Sub(), depth + 1); continue; }
                            if (it.Run == null) Check(src, it.ActionId);
                        }
                    }
                    var oldMode = sel.Mode;
                    foreach (var m in Enum.GetValues<Core.Selection.SelectMode>())
                    {
                        sel.Mode = m;
                        Pie($"pieMode[{m}]", UI.PieMenus.ModeMenu(shell), 0);
                        Pie($"pieEdit[{m}]", UI.PieMenus.ContextMenu(shell), 0);
                    }
                    sel.Mode = oldMode;
                    Pie("pieSelect", UI.PieMenus.SelectMenu(shell), 0);
                    Pie("pieView", UI.PieMenus.ViewMenu(shell), 0);
                    Pie("pieUvMode", UI.PieMenus.UvModeMenu(shell, false), 0);
                    Pie("pieUvSelect", UI.PieMenus.UvSelectMenu(shell), 0);
                    Pie("pieUv", UI.PieMenus.UvMenu(shell), 0);
                    var conflicts = shell.Hotkeys.Map.Bindings.GroupBy(b => (b.Chord, b.Context)).Where(g => g.Select(b => b.Action).Distinct().Count() > 1)
                        .Select(g => $"{g.Key.Chord}/{g.Key.Context}: {string.Join(",", g.Select(b => b.Action))}");
                    GD.Print($"[Drive] actioncheck registered={reg.All.Count()} missing={missing.Count} {string.Join(" ", missing)}");
                    GD.Print($"[Drive] actioncheck conflicts: {string.Join(" | ", conflicts)}");
                    GD.Print($"[Drive] actioncheck notInMenu: {string.Join(" ", reg.All.Select(a => a.Id).Where(id => !shell.Menus.ReferencedActions.Contains(id)).OrderBy(x => x))}");
                    break;
                }
            default: GD.PrintErr($"[Drive] unknown step {p[0]}"); break;
        }
    }

    /// <summary>현재 눌린 버튼 집합(<c>_held</c>)을 Godot 모션 이벤트용 버튼 마스크로 바꾼다.</summary>
    private MouseButtonMask Mask()
    {
        MouseButtonMask m = 0;
        foreach (var b in _held) m |= b switch { MouseButton.Left => MouseButtonMask.Left, MouseButton.Middle => MouseButtonMask.Middle, MouseButton.Right => MouseButtonMask.Right, _ => 0 };
        return m;
    }

    /// <summary>"L"/"M"/"R" → 마우스 버튼. 그 밖이면 예외(스텝 오류로 출력됨).</summary>
    private static MouseButton Button(string s) => s.ToUpperInvariant() switch { "L" => MouseButton.Left, "M" => MouseButton.Middle, "R" => MouseButton.Right, _ => throw new ArgumentException(s) };

    /// <summary>인자 배열의 <paramref name="from"/> 이후에 있는 alt/shift/ctrl 수식어를 이벤트에 설정한다.</summary>
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
