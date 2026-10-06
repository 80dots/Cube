using Godot;

namespace Cube.App.UI;

/// <summary>res://assets/icons/*.svg 를 런타임에 UI 배율에 맞춰 래스터화한다(임포트 불필요, Hi-DPI 선명).</summary>
public static class Icons
{
    private static readonly Dictionary<(string, int), Texture2D> Cache = new();

    public static Texture2D? Get(string name, int sizePx)
    {
        if (Cache.TryGetValue((name, sizePx), out var t)) return t;
        string path = $"res://assets/icons/{name}.svg";
        if (!Godot.FileAccess.FileExists(path)) { GD.PushWarning($"[Icons] missing {path}"); return null; }
        var svg = Godot.FileAccess.GetFileAsString(path);
        var img = new Image();
        float scale = sizePx / 32f;
        if (img.LoadSvgFromString(svg, scale) != Error.Ok) { GD.PushWarning($"[Icons] bad svg {path}"); return null; }
        var tex = ImageTexture.CreateFromImage(img);
        Cache[(name, sizePx)] = tex;
        return tex;
    }

    /// <summary>임포트 없이 PNG를 읽는다(UV 그리드 등).</summary>
    public static Texture2D? LoadPng(string resPath)
    {
        if (!Godot.FileAccess.FileExists(resPath)) return null;
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
