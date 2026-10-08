using Cube.App.Bridge;
using Cube.App.UI;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Blender식 대화형 Bevel(Edit Mesh → Bevel (Interactive), Edit Pie). 마우스를 선택 중심에서 멀리/가까이 움직이면 폭이 바뀌고, 휠 = 세그먼트,
/// 숫자 입력 = 폭(Backspace로 지움). Shift = 세밀, Ctrl = 눈금 스냅. Blender 단축키(글자 키 토글)는 쓰지 않는다(사용자 지시) — 나머지 옵션은 확정 후 Action Popup에서.
/// LMB/Enter = 확정(마지막 Bevel 옵션으로 저장되어 Action Popup에서 계속 조정), RMB/Esc = 취소. 미리보기는 원본을 복사해 매번 다시 계산한다.
/// </summary>
/// <remarks>
/// 동작 원리: 시작 시 대상 메시를 Clone해 원본(before)으로 보관하고, 값이 바뀔 때마다 원본을 CopyFrom으로 되돌린 뒤
/// <see cref="MeshOps.Bevel"/>을 다시 실행해 문서 메시에 직접 쓴다(Undo 미기록 프리뷰). 확정 시에는 원본으로 복구한 뒤
/// 옵션을 "mesh.bevel" 옵션 값으로 저장하고 "mesh.bevelApply" 액션을 실행해 정식 명령(Undo·구성 이력·Action Popup)으로 만든다.
/// 폭은 "현재 마우스와 선택 화면 중심 거리 / 기준 거리" 비율 × 기준 값으로 계산한다.
/// </remarks>
public sealed class BevelTool : ToolBase, IModalTool
{
    /// <summary>툴 ID("bevelTool"). mesh.bevelTool/mesh.bevelVerticesTool 액션이 VertexMode를 정해 전환한다.</summary>
    public override string Id => "bevelTool";
    /// <summary>표시 이름.</summary>
    public override string Label => "Bevel";
    /// <summary>헬프 라인 기본 안내(실행 중에는 <see cref="ShowStatus"/>가 현재 값으로 덮어씀).</summary>
    public override string HelpText => "Bevel: move the mouse to set the width, wheel = segments. LMB/Enter confirm, RMB/Esc cancel.";

    /// <summary>Shift+Ctrl+B로 시작하면 정점 Bevel.</summary>
    /// <remarks>true면 Affect = Vertices, false면 Edges로 시작한다. Activate 전에 설정해야 한다.</remarks>
    public bool VertexMode;

    /// <summary>
    /// 마우스 이동이 조정하는 값의 종류: Width = 폭, Segments = 세그먼트 수, Profile = 프로파일 Shape(0~1).
    /// 현재 UI는 Width 모드만 시작하며(글자 키 토글 없음) 세그먼트는 휠로 바꾼다.
    /// </summary>
    private enum Mode { Width, Segments, Profile }
    /// <summary>현재 조정 모드.</summary>
    private Mode _mode;
    /// <summary>현재 Bevel 옵션(불변 레코드, with로 갱신). 시작 값은 마지막 "mesh.bevel" 옵션.</summary>
    private BevelOptions _o = new();
    /// <summary>대상 목록: (노드 ID, 시작 시점 메시 복제본, Bevel할 엣지/정점 ID).</summary>
    private readonly List<(NodeId id, PolyMesh before, int[] ids)> _targets = new();
    /// <summary>선택 컴포넌트(엣지 중점/정점)들의 화면 투영 평균 위치(뷰포트 로컬 픽셀). 폭 계산의 기준점.</summary>
    private Vector2 _center;
    private float _refDist, _refValue;   // 모드 전환 시점의 마우스 거리와 값
    /// <summary>마지막 마우스 위치(뷰포트 로컬 픽셀).</summary>
    private Vector2 _mouse;
    private float _virtualDist;           // Shift 세밀 조정용 가상 거리
    /// <summary>키보드로 입력 중인 숫자 문자열(비어 있으면 마우스로 조정).</summary>
    private string _typed = "";
    /// <summary>모달 작업이 진행 중인지(시작 실패·확정·취소 후 false).</summary>
    private bool _active;

    /// <summary>셸 싱글턴 바로가기(옵션 저장, 액션 실행, 툴 전환, 핫키 모달 등록).</summary>
    private Shell Shell => Shell.Instance;

    /// <summary>
    /// 시작: 마지막 Bevel 옵션을 읽어 Affect를 정하고 대상을 모은다. 대상이 없으면 안내 후 Select 툴로 돌아간다(지연 호출).
    /// 대상이 있으면 기준 거리/값을 잡고 핫키 모달 처리기로 등록한 뒤 첫 프리뷰를 그린다.
    /// </summary>
    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        _o = Shell.BevelOptionsFrom(Shell.Options("mesh.bevel")) with { Affect = VertexMode ? BevelAffect.Vertices : BevelAffect.Edges };
        if (!Collect())
        {
            Ctx.SetHelp?.Invoke("Bevel: select edges, faces or vertices first.");
            Callable.From(() => Shell.Tools.SetTool("select")).CallDeferred();
            return;
        }
        _active = true;
        _mode = Mode.Width;
        _mouse = Ctx.Viewport.LastMouseLocal;
        _virtualDist = Dist(_mouse);
        StartMode(Mode.Width);
        Shell.Hotkeys.Modal = HandleModalKey;
        Preview();
    }

    /// <summary>다른 툴로 바뀌면 프리뷰를 원본으로 되돌리고(취소) 정리한다.</summary>
    public override void Deactivate()
    {
        if (_active) Restore(); // 다른 툴로 바뀌면 취소
        End();
        base.Deactivate();
    }

    /// <summary>진행 중이면 원본 복구 후 종료.</summary>
    public override void Cancel()
    {
        if (_active) { Restore(); End(); }
    }

    /// <summary>모달 상태를 끝낸다: 핫키 모달 처리기 해제(자기 것일 때만), 대상과 입력 문자열 비우기. 메시 복구는 하지 않는다.</summary>
    private void End()
    {
        _active = false;
        if (Shell.Hotkeys.Modal == HandleModalKey) Shell.Hotkeys.Modal = null;
        _targets.Clear();
        _typed = "";
    }

    /// <summary>대상 메시 원본과 컴포넌트를 모으고 화면 중심을 구한다.</summary>
    /// <returns>대상이 하나라도 있으면 true.</returns>
    private bool Collect()
    {
        _targets.Clear();
        var doc = Ctx.Doc;
        var proj = Ctx.Viewport.Picker.Projection();
        var sum = Vector2.Zero; int n = 0;
        // Affect에 맞게 변환된 선택(Shell.CollectBevelTargets)마다 원본 메시를 복제해 보관
        foreach (var (id, ids) in Shell.CollectBevelTargets(_o.Affect))
        {
            var node = doc.Find(id); var mesh = node?.Mesh; if (mesh == null) continue;
            _targets.Add((id, mesh.Clone(), ids));
            var world = node!.WorldMatrix;
            // 각 컴포넌트의 대표점(정점 또는 엣지 중점)을 월드→화면으로 투영해 합산
            foreach (int c in ids)
            {
                NVec3 p;
                if (_o.Affect == BevelAffect.Vertices) p = mesh.Verts[c].Position;
                else { var (a, b) = mesh.EdgeVertices(c); p = (mesh.Verts[a].Position + mesh.Verts[b].Position) * 0.5f; }
                var s = proj.Project(NVec3.Transform(p, world), out _);
                if (s is { } sp) { sum += new Vector2(sp.X, sp.Y); n++; }
            }
        }
        if (_targets.Count == 0) return false;
        // 투영된 점이 없으면 뷰포트 중앙을 기준으로
        _center = n > 0 ? sum / n : Ctx.Viewport.Size / 2;
        return true;
    }

    /// <summary>화면 기준점과의 거리(최소 1px, 0으로 나누기 방지).</summary>
    private float Dist(Vector2 p) => Math.Max((p - _center).Length(), 1f);

    /// <summary>모드에 해당하는 현재 옵션 값.</summary>
    private float ModeValue(Mode m) => m switch { Mode.Segments => _o.Segments, Mode.Profile => _o.Shape, _ => _o.Width };

    /// <summary>
    /// 조정 모드를 시작/전환한다: 숫자 입력을 비우고, 현재 가상 거리(최소 30px)와 현재 값을 기준으로 잡는다.
    /// 폭이 0이면 비율 곱이 항상 0이 되므로 기준 값을 0.1로 둔다.
    /// </summary>
    private void StartMode(Mode m)
    {
        _mode = m;
        _typed = "";
        _refDist = Math.Max(_virtualDist, 30f * CubeApp.Instance.UiScale);
        _refValue = ModeValue(m);
        if (m == Mode.Width && _refValue <= 0f) _refValue = 0.1f;
    }

    /// <summary>
    /// 마우스 입력: 이동 = 가상 거리 누적(Shift 1/10) 후 값 갱신(숫자 입력 중이면 무시), 휠 = 세그먼트 ±1, LMB = 확정,
    /// RMB = 취소 후 직전 툴로. 모달 동안 다른 마우스 버튼도 모두 소비한다.
    /// </summary>
    public override bool HandleInput(InputEvent e)
    {
        if (!_active) return false;
        switch (e)
        {
            case InputEventMouseMotion mm:
                {
                    float d = Dist(mm.Position);
                    float prev = Dist(_mouse);
                    _mouse = mm.Position;
                    _virtualDist += (d - prev) * (mm.ShiftPressed ? 0.1f : 1f);
                    if (_typed.Length > 0) return true; // 숫자 입력 중에는 마우스 무시
                    ApplyMouse(mm.CtrlPressed);
                    return true;
                }
            case InputEventMouseButton { Pressed: true } mb:
                switch (mb.ButtonIndex)
                {
                    case MouseButton.WheelUp: SetSegments(_o.Segments + 1); return true;
                    case MouseButton.WheelDown: SetSegments(_o.Segments - 1); return true;
                    case MouseButton.Left: Confirm(); return true;
                    case MouseButton.Right: Restore(); End(); Ctx.SetHelp?.Invoke("Bevel cancelled."); Callable.From(() => Shell.Tools.SwapToPrevious()).CallDeferred(); return true;
                }
                return true;
            case InputEventMouseButton:
                return true;
        }
        return false;
    }

    /// <summary>
    /// 가상 거리 비율로 현재 모드 값을 갱신하고 프리뷰한다. Width = 기준 값 × 비율(Ctrl이면 0.01 단위, Percent면 1 단위),
    /// Segments = 20px당 1, Profile = 300px당 1(0~1, Ctrl이면 0.05 단위).
    /// </summary>
    /// <param name="snap">Ctrl 눈금 스냅 여부.</param>
    private void ApplyMouse(bool snap)
    {
        float ratio = _virtualDist / _refDist;
        switch (_mode)
        {
            case Mode.Width:
                {
                    float w = Math.Max(0f, _refValue * ratio);
                    if (snap) w = _o.WidthType == BevelWidthType.Percent ? MathF.Round(w) : MathF.Round(w * 100f) / 100f;
                    _o = _o with { Width = w };
                    break;
                }
            case Mode.Segments:
                SetSegments((int)MathF.Round(_refValue + (_virtualDist - _refDist) / (20f * CubeApp.Instance.UiScale)), preview: false);
                break;
            case Mode.Profile:
                {
                    float s = Math.Clamp(_refValue + (_virtualDist - _refDist) / (300f * CubeApp.Instance.UiScale), 0f, 1f);
                    if (snap) s = MathF.Round(s * 20f) / 20f;
                    _o = _o with { Shape = s };
                    break;
                }
        }
        Preview();
    }

    /// <summary>세그먼트 수를 1~100으로 제한해 설정하고 필요하면 프리뷰한다.</summary>
    private void SetSegments(int s, bool preview = true)
    {
        _o = _o with { Segments = Math.Clamp(s, 1, 100) };
        if (preview) Preview();
    }

    /// <summary>모달 키(단축키보다 먼저). 처리한 키는 true.</summary>
    public bool HandleModalKey(InputEventKey k)
    /// <remarks>
    /// Esc = 취소, Enter = 입력 숫자 적용(없으면 확정), Backspace = 입력 한 글자 지움, 숫자/./- = 입력 추가.
    /// 그 외 키도 모두 true를 돌려 모달 동안 전역 단축키가 실행되지 않게 한다.
    /// </remarks>
    {
        if (!_active) return false;
        switch (k.Keycode)
        {
            case Key.Escape: Restore(); End(); Ctx.SetHelp?.Invoke("Bevel cancelled."); Callable.From(() => Shell.Tools.SwapToPrevious()).CallDeferred(); return true;
            case Key.Enter: case Key.KpEnter:
                if (_typed.Length > 0) { ApplyTyped(); return true; }
                Confirm(); return true;
            case Key.Backspace: if (_typed.Length > 0) { _typed = _typed[..^1]; ApplyTyped(keepText: true); } return true;
            default:
                {
                    // 숫자 입력: 0-9 . -
                    string ch = k.Keycode switch
                    {
                        >= Key.Key0 and <= Key.Key9 => ((int)(k.Keycode - Key.Key0)).ToString(),
                        >= Key.Kp0 and <= Key.Kp9 => ((int)(k.Keycode - Key.Kp0)).ToString(),
                        Key.Period or Key.KpPeriod => ".",
                        Key.Minus or Key.KpSubtract => "-",
                        _ => "",
                    };
                    if (ch.Length == 0) return true; // 모달 동안 다른 단축키는 막는다
                    _typed += ch;
                    ApplyTyped(keepText: true);
                    return true;
                }
        }
        Preview();
        return true;
    }

    /// <summary>
    /// 입력 문자열을 숫자로 해석해 현재 모드 값으로 적용(파싱 실패면 값 유지)하고 프리뷰한다.
    /// </summary>
    /// <param name="keepText">true면 입력 문자열을 유지(계속 타이핑 중), false면 확정하며 비운다.</param>
    private void ApplyTyped(bool keepText = false)
    {
        if (float.TryParse(_typed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
        {
            _o = _mode switch
            {
                Mode.Segments => _o with { Segments = Math.Clamp((int)MathF.Round(v), 1, 100) },
                Mode.Profile => _o with { Shape = Math.Clamp(v, 0f, 1f) },
                _ => _o with { Width = Math.Max(0f, v) },
            };
        }
        if (!keepText) _typed = "";
        Preview();
    }

    /// <summary>원본에서 다시 계산해 미리 보여 준다.</summary>
    private void Preview()
    /// <remarks>원본 복사 → Bevel → 노멀 재계산 → MeshTopology 통지. Undo에는 기록하지 않는다.</remarks>
    {
        var doc = Ctx.Doc;
        foreach (var (id, before, ids) in _targets)
        {
            var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
            mesh.CopyFrom(before);
            MeshOps.Bevel(mesh, ids, _o);
            MeshNormals.Recompute(mesh);
            doc.Notify(new DocChange(ChangeKind.MeshTopology, id));
        }
        ShowStatus();
    }

    /// <summary>모든 대상 메시를 시작 시점 원본으로 되돌린다(프리뷰 취소).</summary>
    private void Restore()
    {
        var doc = Ctx.Doc;
        foreach (var (id, before, _) in _targets)
        {
            var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
            mesh.CopyFrom(before);
            doc.Notify(new DocChange(ChangeKind.MeshTopology, id));
        }
    }

    /// <summary>헬프 라인에 현재 폭(폭 종류)·세그먼트·Shape와 조작법을 표시한다. 조정 중인 값은 [ ]로 감싼다.</summary>
    private void ShowStatus()
    {
        string Mark(Mode m, string s) => _mode == m ? $"[{s}{(_typed.Length > 0 ? " = " + _typed : "")}]" : s;
        string[] wt = { "Offset", "Width", "Depth", "Percent", "Absolute" };
        Ctx.SetHelp?.Invoke(
            $"Bevel {(_o.Affect == BevelAffect.Vertices ? "Vertices" : "Edges")}:  {Mark(Mode.Width, $"Width {_o.Width:0.###}{(_o.WidthType == BevelWidthType.Percent ? "%" : "")} ({wt[(int)_o.WidthType]})")}  " +
            $"Segments {_o.Segments}  Shape {_o.Shape:0.00}  |  move = width, wheel = segments, type a number, Shift fine, Ctrl snap, LMB/Enter OK, RMB/Esc cancel. Other options: Action Popup after confirming.");
    }

    /// <summary>확정: 원본으로 되돌린 뒤 옵션을 저장하고 Bevel 액션으로 실행(Undo·구성 이력·Action Popup).</summary>
    private void Confirm()
    /// <remarks>액션 실행과 직전 툴 복귀는 입력 처리 중 툴 전환을 피하려고 지연 호출한다.</remarks>
    {
        Restore();
        var opts = _o;
        End();
        Shell.WriteBevelOptions(Shell.Options("mesh.bevel"), opts);
        Callable.From(() =>
        {
            Shell.Actions.Invoke("mesh.bevelApply");
            Shell.Tools.SwapToPrevious();
        }).CallDeferred();
    }
}
