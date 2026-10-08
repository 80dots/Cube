using Cube.App.Bridge;
using Cube.Core.Scene;
using Godot;
using GArray = Godot.Collections.Array;

namespace Cube.App.Viewport;

/// <summary>라이트 노드의 표시: 실제 Godot 라이트 + 선으로 그린 아이콘(X-ray). 방향은 노드 -Z.</summary>
public partial class LightView : Node3D
{
    public static readonly Color IconNormal = MathConvert.Rgb(0xffd060);
    public static readonly Color IconSelected = MathConvert.Rgb(0xffffff);
    public static readonly Color IconActive = MathConvert.Rgb(0x3fff3f);

    public SceneNode Node { get; }
    private Light3D? _light;
    private MeshInstance3D _icon = null!;
    private readonly ArrayMesh _iconMesh = new();
    private StandardMaterial3D _mat = null!;
    private LightType? _builtType;

    public LightView(SceneNode node) { Node = node; Name = node.Name; }

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
        var col = new Color(l.Color.X, l.Color.Y, l.Color.Z);
        _light!.LightColor = col;
        _light.LightEnergy = l.Intensity;
        _light.ShadowEnabled = CubeApp.Instance.Settings.Render.Shadows;
        // 씬 라이트는 그 패널이 Use All Lights(7) 모드일 때만 비춘다(SceneView.SceneLightsOn)
        _light.Visible = FindScene(this)?.SceneLightsOn ?? true;
        if (_light is OmniLight3D o) o.OmniRange = l.Range;
        if (_light is SpotLight3D s) { s.SpotRange = l.Range; s.SpotAngle = l.SpotAngle * 0.5f; }
        BuildIcon(l);
    }

    private static SceneView? FindScene(Node n)
    {
        for (Node? c = n.GetParent(); c != null; c = c.GetParent()) if (c is SceneView sv) return sv;
        return null;
    }

    private void BuildIcon(LightShape l)
    {
        _iconMesh.ClearSurfaces();
        var v = new List<Vector3>();
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
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        _iconMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
    }

    public void SetColor(Color c) { if (_mat != null) _mat.AlbedoColor = c; }
}
