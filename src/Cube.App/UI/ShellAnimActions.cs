using Cube.App.Anim;
using Cube.App.UI.Anim;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI;

/// <summary>애니메이션 보기/재생(가져온 클립 전용; 키 생성·편집 없음): Time Slider, Animation Data, 재생 액션.</summary>
public partial class Shell
{
    /// <summary>
    /// 가져온 애니메이션 클립의 재생 상태(클립·시간·재생 중·루프·속도)를 관리하는 노드.
    /// 시간이 바뀌면 AnimationPose.Apply로 SceneNode.Pose(표시 전용)를 갱신하고, 선택 이외의 Undo 명령이나 변형 드래그가 시작되면 Rest()로 포즈를 끈다.
    /// </summary>
    public AnimationPlayback Playback { get; private set; } = null!;
    /// <summary>HelpLine 위의 타임 슬라이더(눈금·키 표시·스크럽·재생 버튼). 클립이 있을 때만 보인다.</summary>
    public TimeSlider TimeSlider { get; private set; } = null!;
    /// <summary>Windows → Animation Data 패널(클립/트랙/키 표). 처음 열 때 EnsureAnimationData가 만든다.</summary>
    public AnimationDataWindow? AnimationData { get; private set; }
    /// <summary>타임 슬라이더를 감싼 PanelContainer. 표시/숨김은 이 패널 단위로 한다.</summary>
    private Control _timeSliderPanel = null!;

    /// <summary>_Ready에서 레이아웃(HelpLine 앞)에 Time Slider를 넣는다.</summary>
    private void BuildTimeSlider(VBoxContainer root, int index, float s)
    {
        // 재생기 노드를 만들고 문서에 연결(문서의 Animations를 읽고 포즈를 적용)
        Playback = new AnimationPlayback { Name = "AnimationPlayback" };
        AddChild(Playback);
        Playback.Bind(Document);
        // 슬라이더를 어두운 패널로 감싸 루트의 index 위치(HelpLine 패널 바로 앞)로 옮긴다
        TimeSlider = new TimeSlider { Name = "TimeSlider" };
        var pc = new PanelContainer { Name = "TimeSliderPanel", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var sb = new StyleBoxFlat { BgColor = MayaTheme.PanelDark };
        sb.SetContentMarginAll(3 * s);
        pc.AddThemeStyleboxOverride("panel", sb);
        pc.AddChild(TimeSlider);
        root.AddChild(pc);
        root.MoveChild(pc, index);
        _timeSliderPanel = pc;
        // 슬라이더 설정, 데이터 창 열기 버튼 연결, 클립 목록이 바뀌거나 문서가 리셋되면 표시 여부 재계산
        TimeSlider.Setup(Playback, Document);
        TimeSlider.OpenData = ToggleAnimationData;
        Document.Changed += c => { if (c.Kind is ChangeKind.AnimationsChanged or ChangeKind.Reset) UpdateTimeSliderVisibility(); };
        UpdateTimeSliderVisibility();
    }

    /// <summary>클립이 있고 Display → Time Slider가 켜져 있을 때만 보인다.</summary>
    /// <remarks>뷰포트 최대화 중에는 숨긴다. ToggleMaximizeViewport와 display.timeSlider가 다시 부른다.</remarks>
    public void UpdateTimeSliderVisibility()
        => _timeSliderPanel.Visible = !_maximized && Settings.ShowTimeSlider && Document.Animations.Count > 0;

    /// <summary>
    /// anim.* 액션 등록: 재생/일시정지, 정지(rest 포즈), 처음/끝, 이전/다음 프레임, 이전/다음 키, 루프, 다음 클립,
    /// Animation Data 창, Time Slider 표시 토글. 대부분 클립이 있을 때만 실행 가능(HasClip).
    /// </summary>
    private void RegisterAnimActions()
    {
        // 현재 클립이 선택되어 있는지(재생 관련 액션의 canExecute)
        bool HasClip() => Playback?.Clip != null;
        Actions.Register("anim.playToggle", "Play / Pause", () => Playback.TogglePlay(), HasClip, isChecked: () => Playback?.Playing ?? false);
        Actions.Register("anim.rest", "Stop (Rest Pose)", () => Playback.Rest(), () => Playback != null && (Playback.Posed || Playback.Playing));
        Actions.Register("anim.start", "Go to Start", () => Playback.GoStart(), HasClip);
        Actions.Register("anim.end", "Go to End", () => Playback.GoEnd(), HasClip);
        Actions.Register("anim.nextFrame", "Next Frame", () => Playback.StepFrame(1), HasClip);
        Actions.Register("anim.prevFrame", "Previous Frame", () => Playback.StepFrame(-1), HasClip);
        Actions.Register("anim.nextKey", "Next Key", () => Playback.StepKey(1), HasClip);
        Actions.Register("anim.prevKey", "Previous Key", () => Playback.StepKey(-1), HasClip);
        Actions.Register("anim.loop", "Loop Playback", () => { Playback.Loop = !Playback.Loop; TimeSlider.QueueRedraw(); }, isChecked: () => Playback?.Loop ?? true);
        Actions.Register("anim.nextClip", "Next Clip", () => Playback.SelectClip((Playback.ClipIndex + 1) % Document.Animations.Count), () => Document.Animations.Count > 1);
        Actions.Register("windows.animationData", "Animation Data", ToggleAnimationData, isChecked: () => AnimationData?.IsOpen ?? false);
        Actions.Register("display.timeSlider", "Time Slider", () => { Settings.ShowTimeSlider = !Settings.ShowTimeSlider; Settings.Save(); UpdateTimeSliderVisibility(); }, isChecked: () => Settings.ShowTimeSlider);
    }

    /// <summary>
    /// Animation Data 패널을 처음 필요할 때 만든다(숨긴 상태로 셸에 추가 → Setup → 닫힐 때 셸프 갱신 → DockManager 등록).
    /// 레이아웃 복원(EnsurePanel "animationData")도 이 함수를 쓴다.
    /// </summary>
    private AnimationDataWindow EnsureAnimationData()
    {
        if (AnimationData == null)
        {
            AnimationData = new AnimationDataWindow { Name = "AnimationData", Visible = false, PanelId = "animationData" };
            AddChild(AnimationData);
            AnimationData.Setup(this, Playback);
            AnimationData.Closed += RefreshShelf;
            Dock.Register(AnimationData);
        }
        return AnimationData;
    }

    /// <summary>Animation Data 패널 열기/닫기 토글(없으면 먼저 만든다).</summary>
    private void ToggleAnimationData() => EnsureAnimationData().Toggle();
}
