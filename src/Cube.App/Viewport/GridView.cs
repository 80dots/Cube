using Cube.App.Bridge;
using Godot;

namespace Cube.App.Viewport;

/// <summary>Maya 스타일 그리드. 기본 ±12 유닛, 1유닛 보조선, 5유닛 주선, 중앙 축선은 더 어둡게.</summary>
/// <remarks>
/// XZ 평면(y = 0)에 X/Z 방향 선을 <see cref="Spacing"/> 간격으로 −Size..+Size 범위에 그린다. 선은 ImmediateMesh 하나에 정점 색으로 담고
/// 무광 머티리얼로 그린다. 설정(Preferences Grid Spacing) 이 바뀌면 Shell이 필드를 바꾼 뒤 <see cref="Build"/>를 다시 부른다.
/// </remarks>
public partial class GridView : MeshInstance3D
{
    /// <summary>그리드 반폭(월드 m). 선은 −Size..+Size 사이에 놓인다.</summary>
    public float Size = 12f;
    /// <summary>선 간격(월드 m). Settings.GridSpacingCm / 100이 들어온다.</summary>
    public float Spacing = 1f;
    /// <summary>몇 번째 선마다 주선(밝은 색)으로 그릴지.</summary>
    public int MajorEvery = 5;
    /// <summary>보조선 색.</summary>
    public Color MinorColor = MathConvert.Rgb(0x4a4a4a);
    /// <summary>주선 색.</summary>
    public Color MajorColor = MathConvert.Rgb(0x6b6b6b);
    /// <summary>원점을 지나는 축선(i = 0) 색. Maya처럼 더 어둡다.</summary>
    public Color AxisColor = MathConvert.Rgb(0x1e1e1e);

    /// <summary>그리드 선 지오메트리(Lines 서피스 하나).</summary>
    private readonly ImmediateMesh _mesh = new();

    /// <summary>메시와 무광·정점 색·양면 머티리얼을 연결하고(그림자 없음) 처음 한 번 그리드를 만든다.</summary>
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

    /// <summary>
    /// 현재 Size/Spacing/MajorEvery로 선을 다시 만든다. i = −n..n(n = round(Size/Spacing))마다 Z 방향 선(x = t)과 X 방향 선(z = t)을 하나씩 넣고,
    /// 색은 i = 0이면 축색, i가 MajorEvery의 배수면 주선색, 나머지는 보조선색이다.
    /// </summary>
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
