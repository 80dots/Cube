using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Cube.App;

/// <summary>도킹 레이아웃: 좌/우 도크마다 그룹(위→아래) 목록, 그룹은 탭 패널 ID 목록. 떠 있는 패널 위치도 저장.</summary>
public sealed class DockLayoutSettings
{
    [JsonPropertyName("left")] public List<List<string>> Left { get; set; } = new() { new() { "outliner" } };
    [JsonPropertyName("right")] public List<List<string>> Right { get; set; } = new() { new() { "properties" } };
    /// <summary>도크 폭(px, 0 = 기본값).</summary>
    [JsonPropertyName("leftWidth")] public float LeftWidth { get; set; }
    [JsonPropertyName("rightWidth")] public float RightWidth { get; set; }
    [JsonPropertyName("floating")] public List<FloatingPanelState> Floating { get; set; } = new();
    /// <summary>그룹 사이 경계 오프셋(px, UI 배율 1 기준).</summary>
    [JsonPropertyName("leftSplits")] public List<float> LeftSplits { get; set; } = new();
    [JsonPropertyName("rightSplits")] public List<float> RightSplits { get; set; } = new();
    /// <summary>v0.0.34: 도크 항목(위→아래)마다 나란히 놓인 그룹들(왼→오), 그룹 = 탭 패널 ID. 있으면 Left/Right 대신 쓴다.</summary>
    [JsonPropertyName("leftRows")] public List<List<List<string>>>? LeftRows { get; set; }
    [JsonPropertyName("rightRows")] public List<List<List<string>>>? RightRows { get; set; }
    /// <summary>항목마다 가로 경계 오프셋(UI 배율 1 기준).</summary>
    [JsonPropertyName("leftRowSplits")] public List<List<float>>? LeftRowSplits { get; set; }
    [JsonPropertyName("rightRowSplits")] public List<List<float>>? RightRowSplits { get; set; }
    /// <summary>그룹마다 앞에 보이던 탭의 패널 ID.</summary>
    [JsonPropertyName("active")] public List<string> Active { get; set; } = new();
}

public sealed class FloatingPanelState
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("w")] public float W { get; set; }
    [JsonPropertyName("h")] public float H { get; set; }
}

/// <summary>뷰포트 렌더 설정(Render → Render Settings). 모든 패널의 Environment에 적용된다.</summary>
public sealed class RenderSettings
{
    [JsonPropertyName("iblEnabled")] public bool IblEnabled { get; set; } = true;
    /// <summary>내장 HDRI id(HdriLibrary.BuiltIn) 또는 "custom".</summary>
    [JsonPropertyName("hdri")] public string Hdri { get; set; } = "studio_small_09";
    [JsonPropertyName("hdriPath")] public string? HdriPath { get; set; }
    [JsonPropertyName("iblIntensity")] public float IblIntensity { get; set; } = 0.6f;
    [JsonPropertyName("iblRotation")] public float IblRotation { get; set; }
    [JsonPropertyName("showBackground")] public bool ShowBackground { get; set; }
    /// <summary>0 Linear, 1 Reinhard, 2 Filmic, 3 ACES, 4 AgX.</summary>
    [JsonPropertyName("tonemap")] public int Tonemap { get; set; }
    [JsonPropertyName("exposure")] public float Exposure { get; set; } = 1f;
    [JsonPropertyName("headlight")] public bool Headlight { get; set; } = true;
    [JsonPropertyName("shadows")] public bool Shadows { get; set; }
    [JsonPropertyName("ssao")] public bool Ssao { get; set; }
    /// <summary>0 off, 1 2x, 2 4x, 3 8x.</summary>
    [JsonPropertyName("msaa")] public int Msaa { get; set; } = 2;
    [JsonPropertyName("fxaa")] public bool Fxaa { get; set; }
}

/// <summary>Bridge(외부 앱 연동) 설정: 실행 파일 경로, Tripo3D API 키, 자동 다시 읽기.</summary>
public sealed class BridgeSettings
{
    [JsonPropertyName("blenderPath")] public string? BlenderPath { get; set; }
    [JsonPropertyName("rizomUvPath")] public string? RizomUvPath { get; set; }
    [JsonPropertyName("marmosetPath")] public string? MarmosetPath { get; set; }
    [JsonPropertyName("cascadeurPath")] public string? CascadeurPath { get; set; }
    [JsonPropertyName("tripoApiKey")] public string? TripoApiKey { get; set; }
    [JsonPropertyName("autoReload")] public bool AutoReload { get; set; } = true;
}

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
    [JsonPropertyName("wireOnShaded")] public bool WireOnShaded { get; set; }
    /// <summary>v0.0.22: Wireframe on Shaded 기본값을 OFF로 바꾼 1회 마이그레이션 적용 여부.</summary>
    [JsonPropertyName("wireOnShadedDefaultOff")] public bool WireOnShadedDefaultOff { get; set; }
    [JsonPropertyName("showGrid")] public bool ShowGrid { get; set; } = true;
    [JsonPropertyName("showTimeSlider")] public bool ShowTimeSlider { get; set; } = true;
    /// <summary>뷰포트 좌상단 Poly Count HUD(Verts/Edges/Faces/Tris/Objects).</summary>
    [JsonPropertyName("showPolyCount")] public bool ShowPolyCount { get; set; } = true;
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
    /// <summary>Render → Render Settings(IBL/톤 매핑/AA).</summary>
    [JsonPropertyName("render")] public RenderSettings Render { get; set; } = new();
    [JsonPropertyName("dock")] public DockLayoutSettings Dock { get; set; } = new();
    /// <summary>Bridge 메뉴(외부 앱 연동) 설정.</summary>
    [JsonPropertyName("bridge")] public BridgeSettings Bridge { get; set; } = new();

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
        return new Settings { WireOnShadedDefaultOff = true };
    }

    /// <summary>예전 설정 파일을 읽은 뒤 1회 적용하는 기본값 변경.</summary>
    public void Migrate()
    {
        if (!WireOnShadedDefaultOff) { WireOnShaded = false; WireOnShadedDefaultOff = true; Save(); }
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
