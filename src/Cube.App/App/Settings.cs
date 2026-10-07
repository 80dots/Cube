using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Cube.App;

/// <summary>user://settings.json 에 저장되는 사용자 설정.</summary>
public sealed class Settings
{
    public const string Path = "user://settings.json";

    [JsonPropertyName("recentFiles")] public List<string> RecentFiles { get; set; } = new();
    /// <summary>UI 배율(%). 화면 DPI 배율에 곱해진다. 기본 130.</summary>
    [JsonPropertyName("uiScalePercent")] public int UiScalePercent { get; set; } = 130;
    [JsonPropertyName("backgroundIndex")] public int BackgroundIndex { get; set; }
    [JsonPropertyName("cameraBasedSelection")] public bool CameraBasedSelection { get; set; } = true;
    /// <summary>박스(마키) 선택 시 가려진 요소도 선택. 기본 on.</summary>
    [JsonPropertyName("marqueeSelectThrough")] public bool MarqueeSelectThrough { get; set; } = true;
    /// <summary>마우스 내비게이션 감도(%). 기본 80.</summary>
    [JsonPropertyName("mouseSensitivityPercent")] public int MouseSensitivityPercent { get; set; } = 80;
    [JsonPropertyName("axisOrientation")] public string AxisOrientation { get; set; } = "World";
    [JsonPropertyName("wireOnShaded")] public bool WireOnShaded { get; set; } = true;
    [JsonPropertyName("showGrid")] public bool ShowGrid { get; set; } = true;
    [JsonPropertyName("quadView")] public bool QuadView { get; set; }
    /// <summary>Display → Joint Local Rotation Axes.</summary>
    [JsonPropertyName("showJointAxes")] public bool ShowJointAxes { get; set; }
    /// <summary>그리드 간격(cm). 기본 100. 그리드 스냅 단위도 이 값이다.</summary>
    [JsonPropertyName("gridSpacingCm")] public float GridSpacingCm { get; set; } = 100f;
    /// <summary>상태 라인 Snap to Grid 토글(Maya 자석 버튼). X 홀드와 같다.</summary>
    [JsonPropertyName("snapToGrid")] public bool SnapToGrid { get; set; }
    /// <summary>상태 라인 Snap to Points 토글. V 홀드와 같다.</summary>
    [JsonPropertyName("snapToPoints")] public bool SnapToPoints { get; set; }
    /// <summary>Maya Move Tool "Retain component spacing": 점 스냅 시 선택을 통째로 옮긴다(off면 모두 스냅 점으로 모은다).</summary>
    [JsonPropertyName("retainComponentSpacing")] public bool RetainComponentSpacing { get; set; } = true;
    /// <summary>J 홀드 회전 증분(도). 기본 15.</summary>
    [JsonPropertyName("rotateSnapDegrees")] public float RotateSnapDegrees { get; set; } = 15f;
    /// <summary>J 홀드 스케일 증분. 기본 0.25.</summary>
    [JsonPropertyName("scaleSnapStep")] public float ScaleSnapStep { get; set; } = 0.25f;
    /// <summary>Material Editor 목록을 썸네일로 표시.</summary>
    [JsonPropertyName("materialThumbnails")] public bool MaterialThumbnails { get; set; }
    [JsonPropertyName("lastExportDir")] public string? LastExportDir { get; set; }
    [JsonPropertyName("lastSceneDir")] public string? LastSceneDir { get; set; }

    public static Settings Load()
    {
        try
        {
            if (Godot.FileAccess.FileExists(Path))
            {
                var json = Godot.FileAccess.GetFileAsString(Path);
                var s = JsonSerializer.Deserialize<Settings>(json);
                if (s != null) return s;
            }
        }
        catch (Exception ex) { GD.PushWarning($"[Settings] load failed: {ex.Message}"); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
            f?.StoreString(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { GD.PushWarning($"[Settings] save failed: {ex.Message}"); }
    }

    public void AddRecent(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        while (RecentFiles.Count > 10) RecentFiles.RemoveAt(RecentFiles.Count - 1);
    }
}
