using Cube.App.Bridge;
using Cube.Core.Scene;
using Godot;
using GArray = Godot.Collections.Array;

namespace Cube.App.Viewport;

/// <summary>라이트 노드의 표시: 실제 Godot 라이트 + 선으로 그린 아이콘(X-ray). 방향은 노드 -Z.</summary>
/// <remarks>
/// SceneView가 라이트 SceneNode마다 하나 만든다. 라이트 종류가 바뀌면 Godot 라이트 노드를 새로 만들고(Directional/Spot/Omni),
/// 색·세기·범위·원뿔 각·그림자는 <see cref="Refresh"/>마다 반영한다. 아이콘은 깊이 테스트 없는 선 메시이며 오브젝트 모드 피킹 대상이다.
/// </remarks>
public partial class LightView : Node3D
{
    /// <summary>선택되지 않은 라이트 아이콘 색(노랑).</summary>
    public static readonly Color IconNormal = MathConvert.Rgb(0xffd060);
    /// <summary>선택된 라이트 아이콘 색.</summary>
    public static readonly Color IconSelected = MathConvert.Rgb(0xffffff);
    /// <summary>활성 라이트 아이콘 색(초록).</summary>
    public static readonly Color IconActive = MathConvert.Rgb(0x3fff3f);

    /// <summary>표시하는 라이트 SceneNode(Light 셰이프 보유).</summary>
    public SceneNode Node { get; }
    /// <summary>실제 조명을 내는 Godot 라이트(종류에 맞는 파생 클래스). 종류가 바뀌면 교체된다.</summary>
    private Light3D? _light;
    /// <summary>아이콘 표시 노드.</summary>
    private MeshInstance3D _icon = null!;
    /// <summary>아이콘 선 메시(로컬 공간, -Z가 빛 방향).</summary>
    private readonly ArrayMesh _iconMesh = new();
    /// <summary>아이콘 머티리얼(무광, X-ray, 색 = 선택 상태).</summary>
    private StandardMaterial3D _mat = null!;
    /// <summary>현재 Godot 라이트를 만든 종류(null = 아직 없음). 다르면 라이트를 다시 만든다.</summary>
    private LightType? _builtType;

    /// <summary>라이트 노드를 받아 뷰를 만든다.</summary>
    public LightView(SceneNode node) { Node = node; Name = node.Name; }

    /// <summary>아이콘 머티리얼·노드를 만들고 <see cref="Refresh"/>로 라이트를 만든다.</summary>
    public override void _Ready()
    {
        _mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true, AlbedoColor = IconNormal, RenderPriority = 40, VertexColorUseAsAlbedo = false };
        _icon = new MeshInstance3D { Name = "Icon", Mesh = _iconMesh, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_icon);
        Refresh();
    }

    /// <summary>LightShape 속성을 Godot 라이트에 반영하고 아이콘을 다시 그린다.</summary>
    public void Refresh()
    {
        var l = Node.Light; if (l == null || _icon == null) return;
        // 종류가 바뀌었으면 이전 Godot 라이트를 버리고 새 종류로 만든다
        if (_builtType != l.Type)
        {
            _light?.QueueFree();
            _light = l.Type switch
            {
                LightType.Directional => new DirectionalLight3D(),
                LightType.Spot => new SpotLight3D(),
                _ => new OmniLight3D(),
            };
            _light.Name = "Light";
            AddChild(_light);
            _builtType = l.Type;
        }
        // 공통 속성: 색(선형 RGB 그대로), 세기, 그림자(Render Settings)
        var col = new Color(l.Color.X, l.Color.Y, l.Color.Z);
        _light!.LightColor = col;
        _light.LightEnergy = l.Intensity;
        _light.ShadowEnabled = CubeApp.Instance.Settings.Render.Shadows;
        // 씬 라이트는 그 패널이 Use All Lights(7) 모드일 때만 비춘다(SceneView.SceneLightsOn)
        _light.Visible = FindScene(this)?.SceneLightsOn ?? true;
        // 종류별 속성: 점광 범위, 스포트 범위·반각(Godot SpotAngle은 반각이라 원뿔 전체 각의 절반)
        if (_light is OmniLight3D o) o.OmniRange = l.Range;
        if (_light is SpotLight3D s) { s.SpotRange = l.Range; s.SpotAngle = l.SpotAngle * 0.5f; }
        BuildIcon(l);
    }

    /// <summary>부모를 따라 올라가 이 뷰를 담고 있는 SceneView를 찾는다(없으면 null).</summary>
    private static SceneView? FindScene(Node n)
    {
        for (Node? c = n.GetParent(); c != null; c = c.GetParent()) if (c is SceneView sv) return sv;
        return null;
    }

    /// <summary>
    /// 종류별 아이콘 선분을 만든다(로컬 공간, 반지름 0.12 기준): 방향광 = 원 + 방사선 + -Z 화살표,
    /// 스포트 = 원뿔 각에 맞는 -Z 방향 원 + 꼭짓점에서 원으로 4선, 점광 = 3축 십자 + 세 평면의 작은 원.
    /// </summary>
    private void BuildIcon(LightShape l)
    {
        _iconMesh.ClearSurfaces();
        var v = new List<Vector3>();
        // 선분 하나(두 점)를 목록에 추가하는 로컬 함수
        void Seg(Vector3 a, Vector3 b) { v.Add(a); v.Add(b); }
        const float r = 0.12f;
        switch (l.Type)
        {
            case LightType.Directional:
                // 태양: 원 + 방사선, 방향 화살표(-Z)
                for (int i = 0; i < 12; i++) { float a0 = i * Mathf.Tau / 12, a1 = (i + 1) * Mathf.Tau / 12; Seg(new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0) * r, new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0) * r); }
                for (int i = 0; i < 8; i++) { float a = i * Mathf.Tau / 8; var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0); Seg(d * r * 1.3f, d * r * 1.8f); }
                Seg(Vector3.Zero, new Vector3(0, 0, -0.8f)); Seg(new Vector3(0, 0, -0.8f), new Vector3(0.08f, 0, -0.65f)); Seg(new Vector3(0, 0, -0.8f), new Vector3(-0.08f, 0, -0.65f));
                break;
            case LightType.Spot:
                {
                    float len = 0.6f, rad = len * Mathf.Tan(Mathf.DegToRad(l.SpotAngle * 0.5f));
                    for (int i = 0; i < 16; i++)
                    {
                        float a0 = i * Mathf.Tau / 16, a1 = (i + 1) * Mathf.Tau / 16;
                        Seg(new Vector3(Mathf.Cos(a0) * rad, Mathf.Sin(a0) * rad, -len), new Vector3(Mathf.Cos(a1) * rad, Mathf.Sin(a1) * rad, -len));
                    }
                    for (int i = 0; i < 4; i++) { float a = i * Mathf.Tau / 4; Seg(Vector3.Zero, new Vector3(Mathf.Cos(a) * rad, Mathf.Sin(a) * rad, -len)); }
                    break;
                }
            default:
                // 점광: 3축 십자 + 작은 원 3개
                Seg(new Vector3(-r, 0, 0), new Vector3(r, 0, 0)); Seg(new Vector3(0, -r, 0), new Vector3(0, r, 0)); Seg(new Vector3(0, 0, -r), new Vector3(0, 0, r));
                for (int i = 0; i < 12; i++)
                {
                    float a0 = i * Mathf.Tau / 12, a1 = (i + 1) * Mathf.Tau / 12; float rr = r * 0.7f;
                    Seg(new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0) * rr, new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0) * rr);
                    Seg(new Vector3(0, Mathf.Cos(a0), Mathf.Sin(a0)) * rr, new Vector3(0, Mathf.Cos(a1), Mathf.Sin(a1)) * rr);
                    Seg(new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * rr, new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * rr);
                }
                break;
        }
        // 모은 선분을 Lines 서피스 하나로 올린다
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        _iconMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
    }

    /// <summary>아이콘 색을 바꾼다(선택/활성 상태 표시). 아직 _Ready 전이면 무시.</summary>
    public void SetColor(Color c) { if (_mat != null) _mat.AlbedoColor = c; }
}
