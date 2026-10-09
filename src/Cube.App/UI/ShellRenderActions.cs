using Cube.App.Viewport;

namespace Cube.App.UI;

/// <summary>Render 메뉴: Render Settings 창, IBL/배경 토글, HDRI 순환. 적용은 모든 뷰포트 패널의 Environment에.</summary>
public partial class Shell
{
    /// <summary>Render Settings 플로팅/도킹 패널(처음 열 때 생성). 값을 바꾸면 즉시 ApplyRenderSettings를 부른다.</summary>
    public RenderSettingsWindow? RenderSettingsWindow { get; private set; }

    /// <summary>
    /// 렌더 관련 액션 등록: Render Settings 창, IBL·HDRI 배경·다음 내장 HDRI, 헤드라이트·그림자,
    /// 포스트 이펙트/안티앨리어싱 토글(Toggle 헬퍼), 모든 포스트 이펙트 끄기.
    /// 모든 액션은 Settings.Render를 직접 바꾼 뒤 ApplyRenderSettings로 저장·적용한다.
    /// </summary>
    private void RegisterRenderActions()
    {
        // r() = 현재 렌더 설정(셸 재생성·설정 재로드 후에도 최신 객체를 가리키도록 람다로 매번 조회)
        var r = () => Settings.Render;
        Actions.Register("windows.renderSettings", "Render Settings...", ToggleRenderSettings, isChecked: () => RenderSettingsWindow?.IsOpen ?? false);
        Actions.Register("render.ibl", "Image Based Lighting (IBL)", () => { r().IblEnabled = !r().IblEnabled; ApplyRenderSettings(); }, isChecked: () => r().IblEnabled);
        Actions.Register("render.background", "Show HDRI Background", () => { r().ShowBackground = !r().ShowBackground; ApplyRenderSettings(); }, canExecute: () => r().IblEnabled, isChecked: () => r().ShowBackground);
        Actions.Register("render.nextHdri", "Next Built-in HDRI", () =>
        {
            // 현재 HDRI의 내장 목록 인덱스를 찾아 다음 것으로 순환(못 찾으면 -1 → 0번), IBL은 켠다
            int i = Array.FindIndex(HdriLibrary.BuiltIn, b => b.id == r().Hdri);
            r().Hdri = HdriLibrary.BuiltIn[(i + 1 + HdriLibrary.BuiltIn.Length) % HdriLibrary.BuiltIn.Length].id;
            r().IblEnabled = true;
            ApplyRenderSettings();
            HelpLine.Text = $"IBL: {HdriLibrary.BuiltIn.First(b => b.id == r().Hdri).label}";
        }, repeatable: true);
        Actions.Register("render.headlight", "Headlight", () => { r().Headlight = !r().Headlight; ApplyRenderSettings(); }, isChecked: () => r().Headlight);
        Actions.Register("render.shadows", "Shadows", () => { r().Shadows = !r().Shadows; ApplyRenderSettings(); }, isChecked: () => r().Shadows);

        // Post Effects 토글(값은 Render Settings 창에서). Render 메뉴 → Post Effects / Anti-aliasing, Render 셸프
        // Toggle: bool 설정 하나를 뒤집는 액션을 등록하는 헬퍼(get/set 람다로 대상 필드 지정, 체크 = 현재 값)
        void Toggle(string id, string label, Func<RenderSettings, bool> get, Action<RenderSettings, bool> set)
            => Actions.Register(id, label, () => { set(r(), !get(r())); ApplyRenderSettings(); HelpLine.Text = $"{label}: {(get(r()) ? "on" : "off")}"; }, isChecked: () => get(r()));
        Toggle("render.ssao", "Ambient Occlusion (SSAO)", s => s.Ssao, (s, v) => s.Ssao = v);
        Toggle("render.glow", "Glow (Bloom)", s => s.Glow, (s, v) => s.Glow = v);
        Toggle("render.ssr", "Screen-Space Reflections", s => s.Ssr, (s, v) => s.Ssr = v);
        Toggle("render.ssil", "Screen-Space Indirect Light", s => s.Ssil, (s, v) => s.Ssil = v);
        Toggle("render.sdfgi", "SDFGI Global Illumination", s => s.Sdfgi, (s, v) => s.Sdfgi = v);
        Toggle("render.fog", "Fog", s => s.Fog, (s, v) => s.Fog = v);
        Toggle("render.volumetricFog", "Volumetric Fog", s => s.VolumetricFog, (s, v) => s.VolumetricFog = v);
        Toggle("render.adjust", "Color Adjustment", s => s.Adjust, (s, v) => s.Adjust = v);
        // DOF는 원거리/근거리 블러 둘 중 하나라도 켜져 있으면 켜짐. 켜면 원거리만, 끄면 둘 다 끈다.
        Toggle("render.dof", "Depth of Field", s => s.DofFar || s.DofNear, (s, v) => { s.DofFar = v; if (!v) s.DofNear = false; });
        Toggle("render.autoExposure", "Auto Exposure", s => s.AutoExposure, (s, v) => s.AutoExposure = v);
        Toggle("render.fxaa", "FXAA", s => s.Fxaa, (s, v) => s.Fxaa = v);
        Toggle("render.smaa", "SMAA", s => s.Smaa, (s, v) => s.Smaa = v);
        Toggle("render.taa", "TAA (Temporal AA)", s => s.Taa, (s, v) => s.Taa = v);
        Toggle("render.debanding", "Debanding", s => s.Debanding, (s, v) => s.Debanding = v);
        // 모든 포스트 이펙트를 끈다(안티앨리어싱 FXAA/SMAA/TAA와 디밴딩은 유지)
        Actions.Register("render.postReset", "Turn Off All Post Effects", () =>
        {
            var s = r();
            s.Glow = s.Ssr = s.Ssil = s.Sdfgi = s.Fog = s.VolumetricFog = s.Adjust = s.DofFar = s.DofNear = s.AutoExposure = s.Ssao = false;
            ApplyRenderSettings();
            HelpLine.Text = "Post effects off.";
        });
    }

    /// <summary>Settings.Render를 저장하고 모든 패널의 환경/안티앨리어싱/헤드라이트에 적용한다.</summary>
    public void ApplyRenderSettings()
    {
        Settings.Save();
        foreach (var p in Layout.Panels) p.ApplyRenderSettings();
        RenderSettingsWindow?.Rebuild();
        // Render Settings 창에서 바꾼 값도 셸프 토글(IBL/Glow 등) 표시에 반영
        RefreshShelf();
    }

    /// <summary>Render Settings 패널을 지연 생성하고 DockManager에 등록한다(레이아웃 복원에서도 호출).</summary>
    private RenderSettingsWindow EnsureRenderSettings()
    {
        if (RenderSettingsWindow == null)
        {
            RenderSettingsWindow = new RenderSettingsWindow { Name = "RenderSettings", Visible = false, PanelId = "renderSettings" };
            AddChild(RenderSettingsWindow);
            RenderSettingsWindow.Setup(this);
            Dock.Register(RenderSettingsWindow);
        }
        return RenderSettingsWindow;
    }

    /// <summary>Render Settings 패널 열기/닫기 토글.</summary>
    private void ToggleRenderSettings()
    {
        var w = EnsureRenderSettings();
        if (w.Visible) w.Close(); else w.Open();
    }
}
