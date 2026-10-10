using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Cube.App;

/// <summary>도킹 레이아웃: 좌/우 도크마다 그룹(위→아래) 목록, 그룹은 탭 패널 ID 목록. 떠 있는 패널 위치도 저장.</summary>
/// <remarks>
/// <c>Shell.Dock</c>(DockManager)가 SaveLayout/RestoreLayout으로 읽고 쓴다. 픽셀 값은 UI 배율 1 기준으로 저장해
/// 배율을 바꿔도 같은 비율로 복원된다. v0.0.34 이후는 *Rows/*RowSplits를 쓰고 Left/Right/*Splits는 예전 파일 호환용이다.
/// </remarks>
public sealed class DockLayoutSettings
{
    /// <summary>(예전 형식) 왼쪽 도크의 그룹 목록, 그룹 = 탭 패널 ID 목록. 기본 = Outliner 하나.</summary>
    [JsonPropertyName("left")] public List<List<string>> Left { get; set; } = new() { new() { "outliner" } };
    /// <summary>(예전 형식) 오른쪽 도크의 그룹 목록. 기본 = Properties 하나.</summary>
    [JsonPropertyName("right")] public List<List<string>> Right { get; set; } = new() { new() { "properties" } };
    /// <summary>도크 폭(px, 0 = 기본값).</summary>
    [JsonPropertyName("leftWidth")] public float LeftWidth { get; set; }
    /// <summary>오른쪽 도크 폭(px, 0 = 기본값).</summary>
    [JsonPropertyName("rightWidth")] public float RightWidth { get; set; }
    /// <summary>떠 있는 패널들의 위치·크기.</summary>
    [JsonPropertyName("floating")] public List<FloatingPanelState> Floating { get; set; } = new();
    /// <summary>그룹 사이 경계 오프셋(px, UI 배율 1 기준).</summary>
    [JsonPropertyName("leftSplits")] public List<float> LeftSplits { get; set; } = new();
    /// <summary>오른쪽 도크의 그룹 사이 경계 오프셋.</summary>
    [JsonPropertyName("rightSplits")] public List<float> RightSplits { get; set; } = new();
    /// <summary>v0.0.34: 도크 항목(위→아래)마다 나란히 놓인 그룹들(왼→오), 그룹 = 탭 패널 ID. 있으면 Left/Right 대신 쓴다.</summary>
    [JsonPropertyName("leftRows")] public List<List<List<string>>>? LeftRows { get; set; }
    /// <summary>오른쪽 도크의 항목 → 나란한 그룹 → 탭 패널 ID.</summary>
    [JsonPropertyName("rightRows")] public List<List<List<string>>>? RightRows { get; set; }
    /// <summary>항목마다 가로 경계 오프셋(UI 배율 1 기준).</summary>
    [JsonPropertyName("leftRowSplits")] public List<List<float>>? LeftRowSplits { get; set; }
    /// <summary>오른쪽 도크 항목마다 가로 경계 오프셋.</summary>
    [JsonPropertyName("rightRowSplits")] public List<List<float>>? RightRowSplits { get; set; }
    /// <summary>그룹마다 앞에 보이던 탭의 패널 ID.</summary>
    [JsonPropertyName("active")] public List<string> Active { get; set; } = new();
}

/// <summary>떠 있는 패널 하나의 위치·크기(셸 좌표, px).</summary>
public sealed class FloatingPanelState
{
    /// <summary>패널 ID(PanelId, 예: "uvEditor").</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>왼쪽 위 X.</summary>
    [JsonPropertyName("x")] public float X { get; set; }
    /// <summary>왼쪽 위 Y.</summary>
    [JsonPropertyName("y")] public float Y { get; set; }
    /// <summary>폭.</summary>
    [JsonPropertyName("w")] public float W { get; set; }
    /// <summary>높이.</summary>
    [JsonPropertyName("h")] public float H { get; set; }
}

/// <summary>뷰포트 렌더 설정(Render → Render Settings). 모든 패널의 Environment에 적용된다.</summary>
/// <remarks>
/// RenderSettingsWindow에서 값을 바꾸면 즉시 Shell.ApplyRenderSettings가 저장하고 모든 ViewportPanel.ApplyRenderSettings를 부른다.
/// 열거형 성격의 값은 JSON 호환을 위해 정수로 저장한다.
/// </remarks>
public sealed class RenderSettings
{
    /// <summary>HDRI 이미지 기반 조명(IBL) 사용. 켜면 Environment의 앰비언트·반사 광원이 Sky가 된다.</summary>
    [JsonPropertyName("iblEnabled")] public bool IblEnabled { get; set; } = true;
    /// <summary>내장 HDRI id(HdriLibrary.BuiltIn) 또는 "custom".</summary>
    [JsonPropertyName("hdri")] public string Hdri { get; set; } = "studio_small_09";
    /// <summary>Hdri가 "custom"일 때 읽을 사용자 HDRI 파일 경로(.hdr 또는 Godot이 읽는 이미지).</summary>
    [JsonPropertyName("hdriPath")] public string? HdriPath { get; set; }
    /// <summary>IBL 세기 배율.</summary>
    [JsonPropertyName("iblIntensity")] public float IblIntensity { get; set; } = 0.6f;
    /// <summary>HDRI 회전(도, Y축).</summary>
    [JsonPropertyName("iblRotation")] public float IblRotation { get; set; }
    /// <summary>HDRI를 배경으로 그릴지(끄면 캔버스 그라디언트 배경).</summary>
    [JsonPropertyName("showBackground")] public bool ShowBackground { get; set; }
    /// <summary>HDRI 배경 흐림 단계 0(끔)~9. 배경으로 그릴 때만 적용되고 조명(앰비언트·반사)은 원본 HDRI를 쓴다.</summary>
    [JsonPropertyName("backgroundBlur")] public int BackgroundBlur { get; set; }
    /// <summary>0 Linear, 1 Reinhard, 2 Filmic, 3 ACES, 4 AgX.</summary>
    [JsonPropertyName("tonemap")] public int Tonemap { get; set; }
    /// <summary>톤 매핑 노출 배율.</summary>
    [JsonPropertyName("exposure")] public float Exposure { get; set; } = 1f;
    /// <summary>카메라를 따라가는 헤드라이트(Lit 모드가 아닐 때만).</summary>
    [JsonPropertyName("headlight")] public bool Headlight { get; set; } = true;
    /// <summary>씬 라이트 그림자.</summary>
    [JsonPropertyName("shadows")] public bool Shadows { get; set; }
    /// <summary>화면 공간 앰비언트 오클루전(SSAO).</summary>
    [JsonPropertyName("ssao")] public bool Ssao { get; set; }
    /// <summary>0 off, 1 2x, 2 4x, 3 8x.</summary>
    [JsonPropertyName("msaa")] public int Msaa { get; set; } = 2;
    /// <summary>FXAA 후처리 안티에일리어싱.</summary>
    [JsonPropertyName("fxaa")] public bool Fxaa { get; set; }

    // ---- Post Effects(Godot Environment / CameraAttributes / Viewport, v0.0.48)
    /// <summary>글로(블룸) 사용.</summary>
    [JsonPropertyName("glow")] public bool Glow { get; set; }
    /// <summary>글로 세기.</summary>
    [JsonPropertyName("glowIntensity")] public float GlowIntensity { get; set; } = 0.8f;
    /// <summary>글로 블룸 양(임계값 이하 영역에도 퍼지는 정도).</summary>
    [JsonPropertyName("glowBloom")] public float GlowBloom { get; set; } = 0.1f;
    /// <summary>글로가 시작되는 HDR 밝기 임계값.</summary>
    [JsonPropertyName("glowThreshold")] public float GlowThreshold { get; set; } = 1f;
    /// <summary>0 Additive, 1 Screen, 2 Softlight, 3 Replace, 4 Mix.</summary>
    [JsonPropertyName("glowBlend")] public int GlowBlend { get; set; } = 2;
    /// <summary>화면 공간 반사(SSR).</summary>
    [JsonPropertyName("ssr")] public bool Ssr { get; set; }
    /// <summary>SSR 레이 마칭 최대 단계 수.</summary>
    [JsonPropertyName("ssrMaxSteps")] public int SsrMaxSteps { get; set; } = 64;
    /// <summary>화면 공간 간접광(SSIL).</summary>
    [JsonPropertyName("ssil")] public bool Ssil { get; set; }
    /// <summary>SSIL 세기.</summary>
    [JsonPropertyName("ssilIntensity")] public float SsilIntensity { get; set; } = 1f;
    /// <summary>SDFGI(부호 거리장 전역 조명).</summary>
    [JsonPropertyName("sdfgi")] public bool Sdfgi { get; set; }
    /// <summary>거리 안개.</summary>
    [JsonPropertyName("fog")] public bool Fog { get; set; }
    /// <summary>안개 밀도.</summary>
    [JsonPropertyName("fogDensity")] public float FogDensity { get; set; } = 0.01f;
    /// <summary>안개 색(sRGB RGB 0~1 배열).</summary>
    [JsonPropertyName("fogColor")] public float[] FogColor { get; set; } = { 0.55f, 0.6f, 0.68f };
    /// <summary>볼류메트릭 안개.</summary>
    [JsonPropertyName("volumetricFog")] public bool VolumetricFog { get; set; }
    /// <summary>볼류메트릭 안개 밀도.</summary>
    [JsonPropertyName("volumetricFogDensity")] public float VolumetricFogDensity { get; set; } = 0.03f;
    /// <summary>색 보정(밝기·대비·채도) 사용.</summary>
    [JsonPropertyName("adjust")] public bool Adjust { get; set; }
    /// <summary>밝기 배율(1 = 그대로).</summary>
    [JsonPropertyName("brightness")] public float Brightness { get; set; } = 1f;
    /// <summary>대비 배율.</summary>
    [JsonPropertyName("contrast")] public float Contrast { get; set; } = 1f;
    /// <summary>채도 배율.</summary>
    [JsonPropertyName("saturation")] public float Saturation { get; set; } = 1f;
    /// <summary>원거리 피사계 심도 블러.</summary>
    [JsonPropertyName("dofFar")] public bool DofFar { get; set; }
    /// <summary>원거리 블러 시작 거리(m).</summary>
    [JsonPropertyName("dofFarDistance")] public float DofFarDistance { get; set; } = 8f;
    /// <summary>원거리 블러 전환 구간 길이(m).</summary>
    [JsonPropertyName("dofFarTransition")] public float DofFarTransition { get; set; } = 4f;
    /// <summary>근거리 피사계 심도 블러.</summary>
    [JsonPropertyName("dofNear")] public bool DofNear { get; set; }
    /// <summary>근거리 블러 거리(m).</summary>
    [JsonPropertyName("dofNearDistance")] public float DofNearDistance { get; set; } = 1.5f;
    /// <summary>근거리 블러 전환 구간 길이(m).</summary>
    [JsonPropertyName("dofNearTransition")] public float DofNearTransition { get; set; } = 1f;
    /// <summary>피사계 심도 블러 양.</summary>
    [JsonPropertyName("dofAmount")] public float DofAmount { get; set; } = 0.1f;
    /// <summary>자동 노출(카메라 속성).</summary>
    [JsonPropertyName("autoExposure")] public bool AutoExposure { get; set; }
    /// <summary>TAA(시간적 안티에일리어싱).</summary>
    [JsonPropertyName("taa")] public bool Taa { get; set; }
    /// <summary>SMAA 후처리 안티에일리어싱.</summary>
    [JsonPropertyName("smaa")] public bool Smaa { get; set; }
    /// <summary>디밴딩(그라디언트 계단 완화).</summary>
    [JsonPropertyName("debanding")] public bool Debanding { get; set; }
}

/// <summary>Bridge(외부 앱 연동) 설정: 실행 파일 경로, Tripo3D API 키, 자동 다시 읽기.</summary>
public sealed class BridgeSettings
{
    /// <summary>Blender 실행 파일 경로(null이면 자동 감지).</summary>
    [JsonPropertyName("blenderPath")] public string? BlenderPath { get; set; }
    /// <summary>RizomUV 실행 파일 경로.</summary>
    [JsonPropertyName("rizomUvPath")] public string? RizomUvPath { get; set; }
    /// <summary>Marmoset Toolbag 실행 파일 경로.</summary>
    [JsonPropertyName("marmosetPath")] public string? MarmosetPath { get; set; }
    /// <summary>Cascadeur 실행 파일 경로.</summary>
    [JsonPropertyName("cascadeurPath")] public string? CascadeurPath { get; set; }
    /// <summary>Tripo3D OpenAPI 키(Bearer 토큰). 평문으로 settings.json에 저장된다.</summary>
    [JsonPropertyName("tripoApiKey")] public string? TripoApiKey { get; set; }
    /// <summary>외부 앱이 브리지 파일을 다시 쓰면 자동으로 다시 읽을지(끄면 헬프 라인 안내 + 수동 Reload).</summary>
    [JsonPropertyName("autoReload")] public bool AutoReload { get; set; } = true;
}

/// <summary>user://settings.json 에 저장되는 사용자 설정.</summary>
/// <remarks>
/// System.Text.Json으로 직렬화하며 속성 이름은 JsonPropertyName(camelCase)으로 고정한다. 새 속성은 기본값을 주면
/// 예전 파일을 읽을 때 그 값으로 채워진다. 값을 바꾼 쪽이 <see cref="Save"/>를 불러 즉시 저장한다.
/// </remarks>
public sealed class Settings
{
    /// <summary>설정 파일 경로(Godot user:// = %APPDATA%\Godot\app_userdata\Cube).</summary>
    public const string Path = "user://settings.json";

    /// <summary>최근 연 .cube 파일(최신이 앞, 최대 10개).</summary>
    [JsonPropertyName("recentFiles")] public List<string> RecentFiles { get; set; } = new();
    /// <summary>UI 배율(%). 화면 DPI 배율에 곱해진다. 기본 130.</summary>
    [JsonPropertyName("uiScalePercent")] public int UiScalePercent { get; set; } = 130;
    /// <summary>뷰포트 배경 프리셋 번호.</summary>
    [JsonPropertyName("backgroundIndex")] public int BackgroundIndex { get; set; }
    /// <summary>클릭 선택 시 보이는 요소만 집는다(Maya Camera-based selection). 기본 on.</summary>
    [JsonPropertyName("cameraBasedSelection")] public bool CameraBasedSelection { get; set; } = true;
    /// <summary>박스(마키) 선택 시 가려진 요소도 선택. 기본 on.</summary>
    [JsonPropertyName("marqueeSelectThrough")] public bool MarqueeSelectThrough { get; set; } = true;
    /// <summary>마우스 내비게이션 감도(%). 기본 80.</summary>
    [JsonPropertyName("mouseSensitivityPercent")] public int MouseSensitivityPercent { get; set; } = 80;
    /// <summary>조작기 축 방향 "World"/"Local"/"Normal".</summary>
    [JsonPropertyName("axisOrientation")] public string AxisOrientation { get; set; } = "World";
    /// <summary>셰이딩 위에 와이어프레임 겹쳐 그리기(기본 OFF).</summary>
    [JsonPropertyName("wireOnShaded")] public bool WireOnShaded { get; set; }
    /// <summary>v0.0.22: Wireframe on Shaded 기본값을 OFF로 바꾼 1회 마이그레이션 적용 여부.</summary>
    [JsonPropertyName("wireOnShadedDefaultOff")] public bool WireOnShadedDefaultOff { get; set; }
    /// <summary>바닥 그리드 표시.</summary>
    [JsonPropertyName("showGrid")] public bool ShowGrid { get; set; } = true;
    /// <summary>애니메이션 타임 슬라이더 표시(클립이 있을 때만 보임).</summary>
    [JsonPropertyName("showTimeSlider")] public bool ShowTimeSlider { get; set; } = true;
    /// <summary>뷰포트 좌상단 Poly Count HUD(Verts/Edges/Faces/Tris/Objects).</summary>
    [JsonPropertyName("showPolyCount")] public bool ShowPolyCount { get; set; } = true;
    /// <summary>4분할 뷰로 시작(Space 탭 토글 상태 기억).</summary>
    [JsonPropertyName("quadView")] public bool QuadView { get; set; }
    /// <summary>Display → Joint Local Rotation Axes.</summary>
    [JsonPropertyName("showJointAxes")] public bool ShowJointAxes { get; set; }
    /// <summary>Display → Joints: 조인트(구·본·축) 표시. 끄면 뷰포트에서 조인트를 집지 않는다(Outliner에서는 선택 가능).</summary>
    [JsonPropertyName("showJoints")] public bool ShowJoints { get; set; } = true;
    /// <summary>Display → Joint Size: 모든 조인트 표시 크기 배율(Maya Joint Size; 조인트별 반지름에 곱함, 표시 전용).</summary>
    [JsonPropertyName("jointDisplayScale")] public float JointDisplayScale { get; set; } = 1f;
    /// <summary>그리드 간격(cm). 기본 100. 그리드 스냅 단위도 이 값이다.</summary>
    [JsonPropertyName("gridSpacingCm")] public float GridSpacingCm { get; set; } = 100f;
    /// <summary>상태 라인 Snap to Grid 토글(Maya 자석 버튼). X 홀드와 같다.</summary>
    [JsonPropertyName("snapToGrid")] public bool SnapToGrid { get; set; }
    /// <summary>상태 라인 Snap to Points 토글. V 홀드와 같다.</summary>
    [JsonPropertyName("snapToPoints")] public bool SnapToPoints { get; set; }
    /// <summary>Maya Move Tool "Retain component spacing": 점 스냅 시 선택을 통째로 옮긴다(off면 모두 스냅 점으로 모은다).</summary>
    [JsonPropertyName("retainComponentSpacing")] public bool RetainComponentSpacing { get; set; } = true;
    /// <summary>Symmetry 모드(Maya Tool Settings): 0 Off, 1~3 Object X/Y/Z, 4~6 World X/Y/Z.</summary>
    [JsonPropertyName("symmetry")] public int Symmetry { get; set; }
    /// <summary>symmetry.toggle이 다시 켤 때 쓸 마지막 축(1~6).</summary>
    [JsonPropertyName("symmetryLast")] public int SymmetryLast { get; set; } = 1;
    /// <summary>거울 짝을 찾는 허용 오차(로컬 단위).</summary>
    [JsonPropertyName("symmetryTolerance")] public float SymmetryTolerance { get; set; } = 0.001f;
    /// <summary>UV 편집기 Symmetry(Maya UV Toolkit): 0 Off, 1 U(u = Center 축선), 2 V.</summary>
    [JsonPropertyName("uvSymmetry")] public int UvSymmetry { get; set; }
    /// <summary>uv.symmetryToggle이 다시 켤 때 쓸 마지막 축(1~2).</summary>
    [JsonPropertyName("uvSymmetryLast")] public int UvSymmetryLast { get; set; } = 1;
    /// <summary>UV 대칭 축선 위치(기본 0.5).</summary>
    [JsonPropertyName("uvSymmetryCenter")] public float UvSymmetryCenter { get; set; } = 0.5f;
    /// <summary>UV 거울 짝을 찾는 허용 오차(UV 단위).</summary>
    [JsonPropertyName("uvSymmetryTolerance")] public float UvSymmetryTolerance { get; set; } = 0.001f;
    /// <summary>J 홀드 회전 증분(도). 기본 15.</summary>
    [JsonPropertyName("rotateSnapDegrees")] public float RotateSnapDegrees { get; set; } = 15f;
    /// <summary>J 홀드 스케일 증분. 기본 0.25.</summary>
    [JsonPropertyName("scaleSnapStep")] public float ScaleSnapStep { get; set; } = 0.25f;
    /// <summary>Material Editor 목록을 썸네일로 표시.</summary>
    [JsonPropertyName("materialThumbnails")] public bool MaterialThumbnails { get; set; }
    /// <summary>마지막 가져오기/내보내기 폴더.</summary>
    [JsonPropertyName("lastExportDir")] public string? LastExportDir { get; set; }
    /// <summary>마지막 .cube 열기/저장 폴더.</summary>
    [JsonPropertyName("lastSceneDir")] public string? LastSceneDir { get; set; }
    /// <summary>Render → Render Settings(IBL/톤 매핑/AA).</summary>
    [JsonPropertyName("render")] public RenderSettings Render { get; set; } = new();
    /// <summary>도킹 레이아웃(그룹·탭·폭·떠 있는 패널).</summary>
    [JsonPropertyName("dock")] public DockLayoutSettings Dock { get; set; } = new();
    /// <summary>Bridge 메뉴(외부 앱 연동) 설정.</summary>
    [JsonPropertyName("bridge")] public BridgeSettings Bridge { get; set; } = new();

    /// <summary>설정 파일을 읽는다. 없거나 읽기에 실패하면 기본값(새 설치이므로 마이그레이션 완료 상태)으로 만든다.</summary>
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
        // 새 설치는 이미 새 기본값이므로 1회 마이그레이션을 적용한 것으로 표시한다
        return new Settings { WireOnShadedDefaultOff = true };
    }

    /// <summary>예전 설정 파일을 읽은 뒤 1회 적용하는 기본값 변경.</summary>
    public void Migrate()
    {
        if (!WireOnShadedDefaultOff) { WireOnShaded = false; WireOnShadedDefaultOff = true; Save(); }
    }

    /// <summary>들여쓰기된 JSON으로 설정 파일에 쓴다. 실패하면 경고만 남긴다.</summary>
    public void Save()
    {
        try
        {
            using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
            f?.StoreString(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { GD.PushWarning($"[Settings] save failed: {ex.Message}"); }
    }

    /// <summary>최근 파일 목록 맨 앞에 추가한다(중복 제거, 최대 10개 유지). 저장은 호출자가 한다.</summary>
    public void AddRecent(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        while (RecentFiles.Count > 10) RecentFiles.RemoveAt(RecentFiles.Count - 1);
    }
}
