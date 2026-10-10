using Cube.App.Bridge;
using Cube.Core.Scene;
using Godot;
using GArray = Godot.Collections.Array;

namespace Cube.App.Viewport;

/// <summary>
/// 이미지 플레인 노드의 표시(v0.0.71): 텍스처를 입힌 양면 쿼드(무광, 알파 = Opacity) + 테두리 선(선택 색).
/// <see cref="ImagePlaneShape.OnlyView"/>가 있으면 그 라벨의 뷰 패널에서만 쿼드를 보인다(매 프레임 패널 라벨과 비교 — 뷰 전환 즉시 반영).
/// </summary>
public partial class ImagePlaneView : Node3D
{
    public static readonly Color BorderNormal = MathConvert.Rgb(0x6a8bb5);
    public static readonly Color BorderSelected = MathConvert.Rgb(0xffffff);
    public static readonly Color BorderActive = MathConvert.Rgb(0x3fff3f);

    public SceneNode Node { get; }
    private MeshInstance3D _quad = null!, _border = null!;
    private readonly QuadMesh _quadMesh = new();
    private readonly ArrayMesh _borderMesh = new();
    private StandardMaterial3D _mat = null!, _borderMat = null!;
    private string? _loadedPath;
    private ViewportPanel? _panel;

    public ImagePlaneView(SceneNode node) { Node = node; Name = node.Name; }

    public override void _Ready()
    {
        _mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = Colors.White, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        _quad = new MeshInstance3D { Name = "Quad", Mesh = _quadMesh, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_quad);
        _borderMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = BorderNormal, RenderPriority = 30 };
        _border = new MeshInstance3D { Name = "Border", Mesh = _borderMesh, MaterialOverride = _borderMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_border);
        for (Godot.Node? c = GetParent(); c != null; c = c.GetParent()) if (c is ViewportPanel vp) { _panel = vp; break; }
        Refresh();
    }

    /// <summary>셰이프 속성(이미지·크기·불투명도)을 반영한다. 경로가 바뀌었을 때만 텍스처를 다시 읽는다.</summary>
    public void Refresh()
    {
        var ip = Node.ImagePlane; if (ip == null || _quad == null) return;
        if (_loadedPath != ip.ImagePath)
        {
            _loadedPath = ip.ImagePath;
            _mat.AlbedoTexture = string.IsNullOrEmpty(ip.ImagePath) ? null : MaterialCache.LoadTexture(ip.ImagePath);
        }
        _mat.AlbedoColor = new Color(1, 1, 1, Mathf.Clamp(ip.Opacity, 0f, 1f));
        _quadMesh.Size = new Vector2(MathF.Max(ip.Width, 1e-4f), MathF.Max(ip.Height, 1e-4f));
        // 테두리(사각형 선 4개)
        _borderMesh.ClearSurfaces();
        float hw = ip.Width * 0.5f, hh = ip.Height * 0.5f;
        var v = new[] { new Vector3(-hw, -hh, 0), new Vector3(hw, -hh, 0), new Vector3(hw, -hh, 0), new Vector3(hw, hh, 0), new Vector3(hw, hh, 0), new Vector3(-hw, hh, 0), new Vector3(-hw, hh, 0), new Vector3(-hw, -hh, 0) };
        var arrays = new GArray(); arrays.Resize((int)Mesh.ArrayType.Max); arrays[(int)Mesh.ArrayType.Vertex] = v;
        _borderMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        UpdateViewVisibility();
    }

    /// <summary>OnlyView가 있으면 이 패널의 뷰 라벨과 같을 때만 보인다.</summary>
    private void UpdateViewVisibility()
    {
        var ip = Node.ImagePlane; if (ip == null || _quad == null) return;
        bool show = string.IsNullOrEmpty(ip.OnlyView) || _panel == null || string.Equals(_panel.CameraController.Kind.ToString(), ip.OnlyView, StringComparison.OrdinalIgnoreCase);
        _quad.Visible = show; _border.Visible = show;
    }

    public override void _Process(double delta) { if (Node.ImagePlane?.OnlyView != null) UpdateViewVisibility(); }

    /// <summary>이 패널에서 쿼드가 보이는지(피킹용).</summary>
    public bool ShownHere => _quad != null && _quad.Visible && IsVisibleInTree();

    /// <summary>사각형 네 꼭짓점의 월드 좌표(피킹용).</summary>
    public IEnumerable<Vector3> CornersWorld()
    {
        var ip = Node.ImagePlane; if (ip == null) yield break;
        float hw = ip.Width * 0.5f, hh = ip.Height * 0.5f; var xf = GlobalTransform;
        yield return xf * new Vector3(-hw, -hh, 0); yield return xf * new Vector3(hw, -hh, 0); yield return xf * new Vector3(hw, hh, 0); yield return xf * new Vector3(-hw, hh, 0);
    }

    public void SetColor(Color c) { if (_borderMat != null) _borderMat.AlbedoColor = c; }
}
