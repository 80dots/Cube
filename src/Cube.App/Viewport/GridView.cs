using Cube.App.Bridge;
using Godot;

namespace Cube.App.Viewport;

/// <summary>Maya 스타일 그리드. 기본 ±12 유닛, 1유닛 보조선, 5유닛 주선, 중앙 축선은 더 어둡게.</summary>
public partial class GridView : MeshInstance3D
{
    public float Size = 12f;
    public float Spacing = 1f;
    public int MajorEvery = 5;
    public Color MinorColor = MathConvert.Rgb(0x4a4a4a);
    public Color MajorColor = MathConvert.Rgb(0x6b6b6b);
    public Color AxisColor = MathConvert.Rgb(0x1e1e1e);

    private readonly ImmediateMesh _mesh = new();

    public override void _Ready()
    {
        Mesh = _mesh;
        MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        Build();
    }

    public void Build()
    {
        _mesh.ClearSurfaces();
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        int n = (int)MathF.Round(Size / Spacing);
        for (int i = -n; i <= n; i++)
        {
            float t = i * Spacing;
            Color c = i == 0 ? AxisColor : (i % MajorEvery == 0 ? MajorColor : MinorColor);
            _mesh.SurfaceSetColor(c);
            _mesh.SurfaceAddVertex(new Vector3(t, 0, -Size)); _mesh.SurfaceAddVertex(new Vector3(t, 0, Size));
            _mesh.SurfaceSetColor(c);
            _mesh.SurfaceAddVertex(new Vector3(-Size, 0, t)); _mesh.SurfaceAddVertex(new Vector3(Size, 0, t));
        }
        _mesh.SurfaceEnd();
    }
}
