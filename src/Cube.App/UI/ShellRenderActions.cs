using Cube.App.Viewport;

namespace Cube.App.UI;

/// <summary>Render 메뉴: Render Settings 창, IBL/배경 토글, HDRI 순환. 적용은 모든 뷰포트 패널의 Environment에.</summary>
public partial class Shell
{
    public RenderSettingsWindow? RenderSettingsWindow { get; private set; }

    private void RegisterRenderActions()
    {
        var r = () => Settings.Render;
        Actions.Register("windows.renderSettings", "Render Settings...", ToggleRenderSettings, isChecked: () => RenderSettingsWindow?.IsOpen ?? false);
        Actions.Register("render.ibl", "Image Based Lighting (IBL)", () => { r().IblEnabled = !r().IblEnabled; ApplyRenderSettings(); }, isChecked: () => r().IblEnabled);
        Actions.Register("render.background", "Show HDRI Background", () => { r().ShowBackground = !r().ShowBackground; ApplyRenderSettings(); }, canExecute: () => r().IblEnabled, isChecked: () => r().ShowBackground);
        Actions.Register("render.nextHdri", "Next Built-in HDRI", () =>
        {
            int i = Array.FindIndex(HdriLibrary.BuiltIn, b => b.id == r().Hdri);
            r().Hdri = HdriLibrary.BuiltIn[(i + 1 + HdriLibrary.BuiltIn.Length) % HdriLibrary.BuiltIn.Length].id;
            r().IblEnabled = true;
            ApplyRenderSettings();
            HelpLine.Text = $"IBL: {HdriLibrary.BuiltIn.First(b => b.id == r().Hdri).label}";
        }, repeatable: true);
        Actions.Register("render.headlight", "Headlight", () => { r().Headlight = !r().Headlight; ApplyRenderSettings(); }, isChecked: () => r().Headlight);
        Actions.Register("render.shadows", "Shadows", () => { r().Shadows = !r().Shadows; ApplyRenderSettings(); }, isChecked: () => r().Shadows);

        // Post Effects 토글(값은 Render Settings 창에서). Render 메뉴 → Post Effects / Anti-aliasing, Render 셸프
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
        Toggle("render.dof", "Depth of Field", s => s.DofFar || s.DofNear, (s, v) => { s.DofFar = v; if (!v) s.DofNear = false; });
        Toggle("render.autoExposure", "Auto Exposure", s => s.AutoExposure, (s, v) => s.AutoExposure = v);
        Toggle("render.fxaa", "FXAA", s => s.Fxaa, (s, v) => s.Fxaa = v);
        Toggle("render.smaa", "SMAA", s => s.Smaa, (s, v) => s.Smaa = v);
        Toggle("render.taa", "TAA (Temporal AA)", s => s.Taa, (s, v) => s.Taa = v);
        Toggle("render.debanding", "Debanding", s => s.Debanding, (s, v) => s.Debanding = v);
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
    }

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

    private void ToggleRenderSettings()
    {
        var w = EnsureRenderSettings();
        if (w.Visible) w.Close(); else w.Open();
    }
}
