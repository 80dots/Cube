using Godot;

namespace Cube.App.UI;

/// <remarks>
/// 아이콘 이름(예: "shelf_extrude")과 픽셀 크기 쌍마다 한 번만 래스터화해 <see cref="Cache"/>에 보관한다.
/// SVG 원본은 32px 기준으로 그려져 있으므로 요청 크기 / 32를 배율로 넘긴다.
/// 내보낸 빌드에는 .svg 파일이 포함되지 않으므로 <c>IconData.Svg</c>(tools/gen-icons.py로 생성한 내장 문자열)가 실제 출처다.
/// </remarks>
/// <summary>내장 SVG(IconData)를 UI 배율에 맞춰 래스터화한다. 개발 중에는 assets/icons/*.svg 파일이 있으면 그것을 우선 쓴다.</summary>
public static class Icons
{
    /// <summary>(아이콘 이름, 픽셀 크기) → 래스터화된 텍스처 캐시. 같은 아이콘을 여러 버튼이 써도 이미지 디코딩은 한 번뿐이다.</summary>
    private static readonly Dictionary<(string, int), Texture2D> Cache = new();

    /// <summary>
    /// 아이콘 텍스처를 얻는다. 순서: ① 캐시 ② (에디터 실행일 때만) res://assets/icons/{name}.svg 파일 ③ 내장 IconData.Svg.
    /// SVG를 sizePx/32 배율로 래스터화해 캐시에 넣고 돌려준다.
    /// </summary>
    /// <param name="name">확장자 없는 아이콘 이름.</param>
    /// <param name="sizePx">결과 텍스처의 한 변 픽셀(이미 UI 배율이 곱해진 값).</param>
    /// <returns>텍스처, 아이콘이 없거나 SVG 파싱에 실패하면 null(경고 출력).</returns>
    public static Texture2D? Get(string name, int sizePx)
    {
        if (Cache.TryGetValue((name, sizePx), out var t)) return t;
        // 개발 중(에디터 기능 플래그)에는 디스크의 SVG를 우선 읽어 아이콘 수정을 재생성 없이 바로 확인할 수 있게 한다.
        string? svg = null;
        string path = $"res://assets/icons/{name}.svg";
        if (OS.HasFeature("editor") && Godot.FileAccess.FileExists(path)) svg = Godot.FileAccess.GetFileAsString(path);
        // 파일이 없으면 내장 문자열 사전에서 찾고, 그래도 없으면 경고 후 null.
        if (string.IsNullOrEmpty(svg) && !IconData.Svg.TryGetValue(name, out svg)) { GD.PushWarning($"[Icons] unknown icon '{name}'"); return null; }
        // 원본 SVG 좌표계가 32px이라 sizePx/32 배율로 래스터화한다.
        var img = new Image();
        float scale = sizePx / 32f;
        if (img.LoadSvgFromString(svg, scale) != Error.Ok) { GD.PushWarning($"[Icons] bad svg '{name}'"); return null; }
        // 캐시에 넣어 다음 호출부터는 바로 돌려준다.
        var tex = ImageTexture.CreateFromImage(img);
        Cache[(name, sizePx)] = tex;
        return tex;
    }

    /// <param name="resPath">res:// 경로의 PNG 바이트 파일(예: assets/textures/uv_grid.bin).</param>
    /// <returns>밉맵이 생성된 ImageTexture, 파일이 없거나 디코딩 실패 시 null.</returns>
    /// <summary>임포트 없이 PNG 바이트 파일을 읽는다(.bin 확장자로 두어 Godot 임포터를 피한다).</summary>
    public static Texture2D? LoadPng(string resPath)
    {
        if (!Godot.FileAccess.FileExists(resPath)) { GD.PushWarning($"[Icons] missing {resPath}"); return null; }
        var img = new Image();
        if (img.LoadPngFromBuffer(Godot.FileAccess.GetFileAsBytes(resPath)) != Error.Ok) return null;
        // 뷰포트에서 축소되어 보일 때 깜빡임이 없도록 밉맵을 만든다.
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// 아이콘만 있는 버튼을 만든다(툴바·HUD용). 포커스를 받지 않아 키 입력을 뺏지 않으며, 아이콘 크기 + 10px 여백의 최소 크기를 갖는다.
    /// 아이콘을 찾지 못하면 툴팁 앞 세 글자를 텍스트로 대신 표시한다.
    /// </summary>
    /// <param name="icon">아이콘 이름.</param>
    /// <param name="tooltip">마우스를 올리면 보이는 설명(아이콘이 없을 때의 대체 텍스트로도 쓰임).</param>
    /// <param name="sizePx">아이콘 픽셀 크기.</param>
    /// <param name="toggle">true면 눌림 상태를 유지하는 토글 버튼.</param>
    public static Button IconButton(string icon, string tooltip, int sizePx, bool toggle = false)
    {
        var b = new Button { ToggleMode = toggle, FocusMode = Control.FocusModeEnum.None, TooltipText = tooltip, CustomMinimumSize = new Vector2(sizePx + 10, sizePx + 10), IconAlignment = HorizontalAlignment.Center, ExpandIcon = false };
        var tex = Get(icon, sizePx);
        if (tex != null) b.Icon = tex; else b.Text = tooltip[..Math.Min(3, tooltip.Length)];
        return b;
    }
}
