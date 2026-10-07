using Cube.App.Viewport;

namespace Cube.App.UI;

/// <summary>Render 메뉴: Render Settings 창, IBL/배경 토글, HDRI 순환. 적용은 모든 뷰포트 패널의 Environment에.</summary>
public partial class Shell
{
    public RenderSettingsWindow? RenderSettingsWindow { get; private set; }

    private void RegisterRenderActions()
    {
        var r = () => Settings.Render;
        Actions.Register("windows.renderSettings", "Render Settings...", ToggleRenderSettings, isChecked: () => RenderSettingsWindow?.Visible ?? false);
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
    }

    /// <summary>Settings.Render를 저장하고 모든 패널의 환경/안티앨리어싱/헤드라이트에 적용한다.</summary>
    public void ApplyRenderSettings()
    {
        Settings.Save();
        foreach (var p in Layout.Panels) p.ApplyRenderSettings();
        RenderSettingsWindow?.Rebuild();
    }

    private void ToggleRenderSettings()
    {
        if (RenderSettingsWindow == null)
        {
            RenderSettingsWindow = new RenderSettingsWindow { Name = "RenderSettings", Visible = false };
            AddChild(RenderSettingsWindow);
            RenderSettingsWindow.Setup(this);
        }
        if (RenderSettingsWindow.Visible) RenderSettingsWindow.Close(); else RenderSettingsWindow.Open();
    }
}
