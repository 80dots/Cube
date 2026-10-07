using Cube.App.Anim;
using Cube.App.UI.Anim;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI;

/// <summary>애니메이션 보기/재생(가져온 클립 전용; 키 생성·편집 없음): Time Slider, Animation Data, 재생 액션.</summary>
public partial class Shell
{
    public AnimationPlayback Playback { get; private set; } = null!;
    public TimeSlider TimeSlider { get; private set; } = null!;
    public AnimationDataWindow? AnimationData { get; private set; }
    private Control _timeSliderPanel = null!;

    /// <summary>_Ready에서 레이아웃(HelpLine 앞)에 Time Slider를 넣는다.</summary>
    private void BuildTimeSlider(VBoxContainer root, int index, float s)
    {
        Playback = new AnimationPlayback { Name = "AnimationPlayback" };
        AddChild(Playback);
        Playback.Bind(Document);
        TimeSlider = new TimeSlider { Name = "TimeSlider" };
        var pc = new PanelContainer { Name = "TimeSliderPanel", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var sb = new StyleBoxFlat { BgColor = MayaTheme.PanelDark };
        sb.SetContentMarginAll(3 * s);
        pc.AddThemeStyleboxOverride("panel", sb);
        pc.AddChild(TimeSlider);
        root.AddChild(pc);
        root.MoveChild(pc, index);
        _timeSliderPanel = pc;
        TimeSlider.Setup(Playback, Document);
        TimeSlider.OpenData = ToggleAnimationData;
        Document.Changed += c => { if (c.Kind is ChangeKind.AnimationsChanged or ChangeKind.Reset) UpdateTimeSliderVisibility(); };
        UpdateTimeSliderVisibility();
    }

    /// <summary>클립이 있고 Display → Time Slider가 켜져 있을 때만 보인다.</summary>
    public void UpdateTimeSliderVisibility()
        => _timeSliderPanel.Visible = !_maximized && Settings.ShowTimeSlider && Document.Animations.Count > 0;

    private void RegisterAnimActions()
    {
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
        Actions.Register("windows.animationData", "Animation Data", ToggleAnimationData, isChecked: () => AnimationData?.Visible ?? false);
        Actions.Register("display.timeSlider", "Time Slider", () => { Settings.ShowTimeSlider = !Settings.ShowTimeSlider; Settings.Save(); UpdateTimeSliderVisibility(); }, isChecked: () => Settings.ShowTimeSlider);
    }

    private void ToggleAnimationData()
    {
        if (AnimationData == null)
        {
            AnimationData = new AnimationDataWindow { Name = "AnimationData", Visible = false };
            AddChild(AnimationData);
            AnimationData.Setup(this, Playback);
            AnimationData.Closed += RefreshShelf;
        }
        AnimationData.Toggle();
    }
}
