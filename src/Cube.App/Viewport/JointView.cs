using Cube.App.Bridge;
using Cube.Core.Scene;
using Godot;
using GArray = Godot.Collections.Array;

namespace Cube.App.Viewport;

/// <summary>
/// 조인트 하나의 표시(Maya 식): 구 + 자식 조인트로 향하는 팔면체 본. 항상 메시 위에 보이도록 깊이 테스트를 끈다.
/// </summary>
public partial class JointView : Node3D
{
    public static readonly Color JointNormal = MathConvert.Rgb(0x7f9fd0);
    public static readonly Color JointSelected = MathConvert.Rgb(0xffffff);
    public static readonly Color JointActive = MathConvert.Rgb(0x3fff3f);
    public static readonly Color JointHover = MathConvert.Rgb(0xf0f0f0);

    public SceneNode Node { get; }
    private MeshInstance3D _sphere = null!, _bones = null!;
    private readonly ArrayMesh _boneMesh = new();
    private readonly ArrayMesh _axesMesh = new();
    private MeshInstance3D _axes = null!;
    private StandardMaterial3D _mat = null!;
    private static SphereMesh? _sphereMesh;

    public JointView(SceneNode node) { Node = node; Name = node.Name; }

    public override void _Ready()
    {
        _sphereMesh ??= new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 12, Rings = 6 };
        _mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true, AlbedoColor = JointNormal, RenderPriority = 40, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _sphere = new MeshInstance3D { Name = "Sphere", Mesh = _sphereMesh, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_sphere);
        _bones = new MeshInstance3D { Name = "Bones", Mesh = _boneMesh, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_bones);
        var axesMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true, VertexColorUseAsAlbedo = true, RenderPriority = 41 };
        _axes = new MeshInstance3D { Name = "Axes", Mesh = _axesMesh, MaterialOverride = axesMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_axes);
        Refresh();
    }

    public float Radius => Node.Joint?.Radius ?? 0.08f;

    /// <summary>자식 조인트 위치가 바뀌었을 때 본을 다시 만든다. 구 크기도 갱신.</summary>
    public void Refresh()
    {
        if (_sphere == null) return;
        float r = Radius;
        _sphere.Scale = new Vector3(r * 2, r * 2, r * 2);
        // 로컬 회전 축(Display → Joint Local Rotation Axes)
        bool showAxes = CubeApp.Instance.Settings.ShowJointAxes;
        _axes.Visible = showAxes;
        if (showAxes)
        {
            _axesMesh.ClearSurfaces();
            float alen = r * 3.5f;
            var av = new[] { Vector3.Zero, Vector3.Right * alen, Vector3.Zero, Vector3.Up * alen, Vector3.Zero, Vector3.Back * alen };
            var ac = new[] { MathConvert.Rgb(0xff2a2a), MathConvert.Rgb(0xff2a2a), MathConvert.Rgb(0x5aff2a), MathConvert.Rgb(0x5aff2a), MathConvert.Rgb(0x2a6aff), MathConvert.Rgb(0x2a6aff) };
            var aa = new GArray(); aa.Resize((int)Mesh.ArrayType.Max);
            aa[(int)Mesh.ArrayType.Vertex] = av; aa[(int)Mesh.ArrayType.Color] = ac;
            _axesMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, aa);
        }
        _boneMesh.ClearSurfaces();
        var verts = new List<Vector3>();
        foreach (var c in Node.Children)
        {
            if (!c.IsJoint) continue;
            var tip = c.Local.Translation.ToGodot();
            float len = tip.Length();
            if (len < 1e-5f) continue;
            var dir = tip / len;
            // 본 팔면체: 원점 → 링(길이의 15%, 반지름 r·0.8) → 끝
            var up = Mathf.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right;
            var a = dir.Cross(up).Normalized(); var b = dir.Cross(a).Normalized();
            float ringAt = Mathf.Min(len * 0.15f, r * 2f), ringR = Mathf.Min(r * 0.8f, len * 0.1f);
            var ring = new[] { dir * ringAt + a * ringR, dir * ringAt + b * ringR, dir * ringAt - a * ringR, dir * ringAt - b * ringR };
            for (int i = 0; i < 4; i++)
            {
                var p0 = ring[i]; var p1 = ring[(i + 1) % 4];
                verts.Add(Vector3.Zero); verts.Add(p1); verts.Add(p0);
                verts.Add(tip); verts.Add(p0); verts.Add(p1);
            }
        }
        if (verts.Count == 0) return;
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        _boneMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    public void SetColor(Color c) { if (_mat != null) _mat.AlbedoColor = c; }
}
