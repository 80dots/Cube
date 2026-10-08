using Cube.App.Viewport;
using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.Tools;

/// <summary>
/// 조작기(기즈모) 축의 방향 기준. 툴박스 하단 아이콘 버튼(axis.world/local/normal)으로 바꾸며
/// <see cref="ToolContext.AxisOrientation"/>에 저장된다.
/// </summary>
/// <remarks>
/// World = 월드 축(X/Y/Z) 그대로, Object = 대상 노드의 로컬 회전 축(Maya "Object"), Normal = 선택 컴포넌트의 평균 법선을 Z로 하는 축.
/// </remarks>
public enum AxisOrientation { World, Object, Normal }

/// <summary>툴이 접근하는 공용 컨텍스트. 활성 뷰포트와 축 방향이 바뀌면 이벤트로 알린다.</summary>
public sealed class ToolContext
/// <remarks>
/// 툴 매니저가 모든 툴에 같은 인스턴스를 넘긴다. 툴은 이 객체를 통해서만 문서·선택·Undo·설정·활성 뷰포트에 접근하므로
/// 툴 코드가 Shell에 직접 의존하지 않는다. 활성 패널이 바뀌면(4분할 뷰) <see cref="ViewportChanged"/>로 기즈모/피커를 옮기게 한다.
/// </remarks>
{
    /// <summary>편집 대상 문서(Maya DAG). 툴은 이 문서를 미리보기로 직접 갱신하거나 Undo 명령을 푸시한다.</summary>
    public required Document Doc { get; init; }
    /// <summary>앱 설정(스냅 단위, 선택 옵션 등). 툴이 스냅/감도/선택 규칙을 읽는다.</summary>
    public required Settings Settings { get; init; }
    /// <summary>문서의 현재 선택 상태(모드 + 노드별 컴포넌트 집합)에 대한 바로가기.</summary>
    public SelectionState Sel => Doc.Selection;
    /// <summary>문서의 Undo 스택 바로가기. 드래그 툴은 놓을 때 alreadyApplied: true로 명령을 넣는다.</summary>
    public UndoStack Undo => Doc.Undo;
    /// <summary>클릭 피킹 시 가려진 요소를 제외할지(보이는 요소 우선). <see cref="Settings.CameraBasedSelection"/>을 그대로 읽는다.</summary>
    public bool CameraBasedSelection => Settings.CameraBasedSelection;
    /// <summary>헬프 라인(하단 상태 표시줄)에 문자열을 표시하는 콜백. 툴이 켜질 때 사용법 안내를 띄우는 데 쓴다.</summary>
    public Action<string>? SetHelp;

    /// <summary><see cref="Viewport"/>의 저장 필드. 초기화 시 required 속성으로 반드시 채워진다.</summary>
    private ViewportPanel _viewport = null!;
    /// <summary>
    /// 현재 활성 뷰포트 패널(마우스가 들어가거나 눌린 패널). 값이 실제로 바뀔 때만 <see cref="ViewportChanged"/>를 발생시킨다.
    /// </summary>
    public required ViewportPanel Viewport
    {
        get => _viewport;
        set { if (_viewport == value) return; _viewport = value; ViewportChanged?.Invoke(value); }
    }
    /// <summary>활성 뷰포트가 바뀐 뒤 호출된다. 툴은 기즈모/오버레이를 새 패널로 옮긴다.</summary>
    public event Action<ViewportPanel>? ViewportChanged;

    /// <summary><see cref="AxisOrientation"/>의 저장 필드(기본 World).</summary>
    private AxisOrientation _axis = AxisOrientation.World;
    /// <summary>조작기 축 방향 기준. 바뀌면 <see cref="AxisOrientationChanged"/>로 기즈모를 즉시 다시 맞추게 한다.</summary>
    public AxisOrientation AxisOrientation
    {
        get => _axis;
        set { if (_axis == value) return; _axis = value; AxisOrientationChanged?.Invoke(value); }
    }
    /// <summary>축 방향 기준이 바뀐 뒤 호출된다(툴박스 버튼 클릭 등).</summary>
    public event Action<AxisOrientation>? AxisOrientationChanged;
}

/// <summary>뷰포트 입력을 받는 툴. 내비게이션이 소비하지 않은 이벤트만 온다.</summary>
/// <remarks>
/// 뷰포트 입력 순서는 NavigationHandler(Alt+버튼/휠) → 파이 메뉴 → <see cref="ToolManager.Current"/>의 <see cref="HandleInput"/>이다.
/// 툴은 <see cref="ToolManager.SetTool"/>로 하나만 활성화되며 바뀔 때 Cancel → Deactivate → (새 툴) Activate 순으로 불린다.
/// </remarks>
public interface ITool
{
    /// <summary>액션/등록용 고유 ID(예: "tool.move"). <see cref="ToolManager.Register"/>의 키로 쓰인다.</summary>
    string Id { get; }
    /// <summary>UI(툴박스 툴팁, 헬프 라인)에 보이는 이름.</summary>
    string Label { get; }
    /// <summary>툴이 켜질 때 헬프 라인에 띄울 사용법 안내 문자열.</summary>
    string HelpText { get; }
    /// <summary>툴이 활성화될 때 호출. 컨텍스트를 저장하고 기즈모/이벤트 구독을 준비한다.</summary>
    /// <param name="ctx">공용 툴 컨텍스트.</param>
    void Activate(ToolContext ctx);
    /// <summary>다른 툴로 바뀔 때 호출. 이벤트 구독 해제와 기즈모 숨김 등 정리를 한다.</summary>
    void Deactivate();
    /// <summary>뷰포트 입력 이벤트 하나를 처리한다.</summary>
    /// <param name="e">뷰포트 로컬 좌표의 입력 이벤트(내비게이션·파이가 소비하지 않은 것).</param>
    /// <returns>이벤트를 소비했으면 true(뒤 처리기로 전달하지 않음).</returns>
    bool HandleInput(InputEvent e);
    /// <summary>진행 중인 드래그/미리보기를 취소하고 원래 상태로 되돌린다(Esc, 툴 전환, Undo 직전 등).</summary>
    void Cancel();
}

/// <summary>
/// 모달 툴(Blender식 대화형 연산: Bevel 등). 켜져 있는 동안 키 입력은 단축키보다 먼저 <see cref="HandleModalKey"/>로,
/// 뷰포트의 RMB/휠은 파이 메뉴·줌보다 먼저 툴로 간다(Alt 내비게이션은 그대로).
/// </summary>
public interface IModalTool : ITool
{
    /// <summary>모달 툴이 켜져 있는 동안 키 이벤트를 단축키보다 먼저 받는다.</summary>
    /// <param name="k">눌린 키 이벤트.</param>
    /// <returns>true면 처리했으므로 단축키 라우팅을 하지 않는다.</returns>
    bool HandleModalKey(InputEventKey k);
}

/// <summary>
/// 툴 공통 기반 클래스. <see cref="Ctx"/> 저장, 헬프 라인 표시, 활성 뷰포트 변경 이벤트 구독/해제를 기본 구현한다.
/// </summary>
/// <remarks>파생 툴은 Activate/Deactivate를 재정의할 때 반드시 base를 호출해야 이벤트 구독이 짝을 이룬다.</remarks>
public abstract class ToolBase : ITool
{
    /// <summary>Activate에서 받은 툴 컨텍스트. Activate 전에는 null이므로 접근하지 않는다.</summary>
    protected ToolContext Ctx { get; private set; } = null!;
    /// <summary>툴 고유 ID(파생 클래스가 지정).</summary>
    public abstract string Id { get; }
    /// <summary>툴 표시 이름(파생 클래스가 지정).</summary>
    public abstract string Label { get; }
    /// <summary>헬프 라인 안내 문구. 기본은 빈 문자열.</summary>
    public virtual string HelpText => "";
    /// <summary>컨텍스트를 저장하고 헬프 문구를 띄운 뒤 활성 뷰포트 변경 이벤트를 구독한다.</summary>
    public virtual void Activate(ToolContext ctx)
    {
        Ctx = ctx;
        ctx.SetHelp?.Invoke(HelpText);
        ctx.ViewportChanged += OnViewportChanged;
    }
    /// <summary>활성 뷰포트 변경 이벤트 구독을 해제한다.</summary>
    public virtual void Deactivate() { Ctx.ViewportChanged -= OnViewportChanged; }
    /// <summary>기본 구현은 아무 입력도 소비하지 않는다.</summary>
    public virtual bool HandleInput(InputEvent e) => false;
    /// <summary>기본 구현은 취소할 상태가 없다.</summary>
    public virtual void Cancel() { }
    /// <summary>활성 뷰포트가 바뀌었을 때(4분할 뷰). 피커/기즈모를 새 패널로 옮긴다.</summary>
    protected virtual void OnViewportChanged(ViewportPanel panel) { }
}

/// <summary>
/// 툴 레지스트리 겸 현재 툴 관리자. ID로 등록된 툴 중 하나를 활성 툴로 두고 뷰포트 입력을 전달한다.
/// </summary>
/// <remarks>직전 툴(<see cref="Previous"/>)을 기억해 모달성 툴(Edit Pivot 등)이 끝나면 되돌아갈 수 있다.</remarks>
public sealed class ToolManager
{
    /// <summary>ID → 툴 인스턴스 표.</summary>
    private readonly Dictionary<string, ITool> _tools = new();
    /// <summary>모든 툴에 넘기는 공용 컨텍스트.</summary>
    private readonly ToolContext _ctx;
    /// <summary>현재 활성 툴(없으면 null).</summary>
    public ITool? Current { get; private set; }
    /// <summary>직전에 활성이던 툴. <see cref="SwapToPrevious"/>에서 사용.</summary>
    public ITool? Previous { get; private set; }
    /// <summary>활성 툴이 바뀐 뒤 호출(툴박스 버튼 체크 표시 갱신 등).</summary>
    public event Action<ITool?>? ToolChanged;

    /// <summary>공용 컨텍스트로 매니저를 만든다.</summary>
    public ToolManager(ToolContext ctx) { _ctx = ctx; }

    /// <summary>툴을 등록한다. 같은 ID가 있으면 덮어쓴다.</summary>
    public void Register(ITool tool) => _tools[tool.Id] = tool;
    /// <summary>ID로 등록된 툴을 찾는다(없으면 null).</summary>
    public ITool? Get(string id) => _tools.TryGetValue(id, out var t) ? t : null;

    /// <summary>
    /// 활성 툴을 바꾼다. 없는 ID이거나 이미 활성이면 아무것도 하지 않는다.
    /// 순서: 현재 툴 Cancel(진행 중 드래그 되돌림) → Deactivate → Previous 기록 → 새 툴 Activate → <see cref="ToolChanged"/>.
    /// </summary>
    public void SetTool(string id)
    {
        if (!_tools.TryGetValue(id, out var tool) || tool == Current) return;
        Current?.Cancel();
        Current?.Deactivate();
        Previous = Current;
        Current = tool;
        tool.Activate(_ctx);
        ToolChanged?.Invoke(tool);
    }

    /// <summary>직전 툴로 되돌아간다(Previous가 없으면 무시).</summary>
    public void SwapToPrevious() { if (Previous != null) SetTool(Previous.Id); }

    /// <summary>현재 툴에 입력을 전달한다. 툴이 없으면 false.</summary>
    public bool HandleInput(InputEvent e) => Current?.HandleInput(e) ?? false;
    /// <summary>현재 툴의 진행 중 작업을 취소한다.</summary>
    public void CancelCurrent() => Current?.Cancel();
}
