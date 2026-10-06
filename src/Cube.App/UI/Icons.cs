using Godot;

namespace Cube.App.UI;

/// <summary>내장 SVG(IconData)를 UI 배율에 맞춰 래스터화한다. 개발 중에는 assets/icons/*.svg 파일이 있으면 그것을 우선 쓴다.</summary>
public static class Icons
{
    private static readonly Dictionary<(string, int), Texture2D> Cache = new();

    public static Texture2D? Get(string name, int sizePx)
    {
        if (Cache.TryGetValue((name, sizePx), out var t)) return t;
        string? svg = null;
        string path = $"res://assets/icons/{name}.svg";
        if (OS.HasFeature("editor") && Godot.FileAccess.FileExists(path)) svg = Godot.FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(svg) && !IconData.Svg.TryGetValue(name, out svg)) { GD.PushWarning($"[Icons] unknown icon '{name}'"); return null; }
        var img = new Image();
        float scale = sizePx / 32f;
        if (img.LoadSvgFromString(svg, scale) != Error.Ok) { GD.PushWarning($"[Icons] bad svg '{name}'"); return null; }
        var tex = ImageTexture.CreateFromImage(img);
        Cache[(name, sizePx)] = tex;
        return tex;
    }

    /// <summary>임포트 없이 PNG 바이트 파일을 읽는다(.bin 확장자로 두어 Godot 임포터를 피한다).</summary>
    public static Texture2D? LoadPng(string resPath)
    {
        if (!Godot.FileAccess.FileExists(resPath)) { GD.PushWarning($"[Icons] missing {resPath}"); return null; }
        var img = new Image();
        if (img.LoadPngFromBuffer(Godot.FileAccess.GetFileAsBytes(resPath)) != Error.Ok) return null;
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    public static Button IconButton(string icon, string tooltip, int sizePx, bool toggle = false)
    {
        var b = new Button { ToggleMode = toggle, FocusMode = Control.FocusModeEnum.None, TooltipText = tooltip, CustomMinimumSize = new Vector2(sizePx + 10, sizePx + 10), IconAlignment = HorizontalAlignment.Center, ExpandIcon = false };
        var tex = Get(icon, sizePx);
        if (tex != null) b.Icon = tex; else b.Text = tooltip[..Math.Min(3, tooltip.Length)];
        return b;
    }
}
