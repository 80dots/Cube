using Cube.App.Bridge;
using Cube.Core.Picking;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Viewport.Gizmos;

public enum GizmoPart { None, X, Y, Z, XY, YZ, XZ, Center, Screen }

/// <summary>
/// 조작기 공통: 월드 피벗 + 정규직교 축, 화면 고정 크기(기본 100px), 깊이 무시 렌더링, 호버/활성 하이라이트.
/// 히트 테스트는 CPU 스크린 공간에서 한다. 파생 클래스가 지오메트리와 히트를 구현한다.
/// </summary>
public abstract partial class GizmoBase : Node3D
{
    public static readonly Color ColX = MathConvert.Rgb(0xff2a2a);
    public static readonly Color ColY = MathConvert.Rgb(0x5aff2a);
    public static readonly Color ColZ = MathConvert.Rgb(0x2a6aff);
    public static readonly Color ColHi = MathConvert.Rgb(0xffff00);
    public static readonly Color ColCenter = MathConvert.Rgb(0xd0d0d0);
    public const float ScreenSizePx = 100f;
    public const float HitPx = 8f;

    public NVec3 Pivot;
    public NVec3 AxisX = NVec3.UnitX, AxisY = NVec3.UnitY, AxisZ = NVec3.UnitZ;
    public GizmoPart Hover { get; private set; }
    public GizmoPart Active { get; private set; }
    public float WorldUnit { get; private set; } = 1f; // 화면 100px에 해당하는 월드 길이

    protected ImmediateMesh Mesh = null!;
    protected MeshInstance3D MeshNode = null!;
    protected ViewportPanel Panel = null!;
    private bool _dirty = true;

    public void Setup(ViewportPanel panel)
    {
        Panel = panel;
        Mesh = new ImmediateMesh();
        MeshNode = new MeshInstance3D
        {
            Mesh = Mesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                NoDepthTest = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                RenderPriority = 100,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(MeshNode);
        Visible = false;
    }

    public void SetHover(GizmoPart p) { if (Hover != p) { Hover = p; _dirty = true; } }
    public void SetActive(GizmoPart p) { if (Active != p) { Active = p; _dirty = true; } }
    public void MarkDirty() => _dirty = true;

    protected Color PartColor(GizmoPart part, Color baseColor)
        => Active == part || (Active == GizmoPart.None && Hover == part) ? ColHi : baseColor;

    public NVec3 AxisOf(GizmoPart p) => p switch { GizmoPart.X => AxisX, GizmoPart.Y => AxisY, GizmoPart.Z => AxisZ, _ => NVec3.Zero };

    /// <summary>평면 핸들의 법선.</summary>
    public NVec3 PlaneNormalOf(GizmoPart p) => p switch { GizmoPart.XY => AxisZ, GizmoPart.YZ => AxisX, GizmoPart.XZ => AxisY, _ => NVec3.Zero };

    public override void _Process(double delta)
    {
        if (!Visible) return;
        UpdateScale();
        if (_dirty) { Rebuild(); _dirty = false; }
    }

    private void UpdateScale()
    {
        var proj = Panel.Picker.Projection();
        float depth = proj.IsOrtho ? 1f : MathF.Max(NVec3.Dot(Pivot - proj.Eye, proj.Forward), 0.01f);
        WorldUnit = proj.WorldPerPixel(depth) * ScreenSizePx * CubeApp.Instance.UiScale;
        var basis = new Basis(AxisX.ToGodot() * WorldUnit, AxisY.ToGodot() * WorldUnit, AxisZ.ToGodot() * WorldUnit);
        Transform = new Transform3D(basis, Pivot.ToGodot());
    }

    /// <summary>로컬(축 길이 1) 지오메트리를 ImmediateMesh에 다시 그린다.</summary>
    protected abstract void Rebuild();

    /// <summary>화면 픽셀에서 가장 가까운 파트.</summary>
    public abstract GizmoPart HitTest(NVec2 px, CameraProjection proj);

    // ---------------------------------------------------------------- 그리기 헬퍼 (로컬 단위 공간)

    protected void Line(Vector3 a, Vector3 b, Color c)
    {
        Mesh.SurfaceSetColor(c); Mesh.SurfaceAddVertex(a);
        Mesh.SurfaceSetColor(c); Mesh.SurfaceAddVertex(b);
    }

    protected void Tri(Vector3 a, Vector3 b, Vector3 c, Color col)
    {
        Mesh.SurfaceSetColor(col); Mesh.SurfaceAddVertex(a);
        Mesh.SurfaceSetColor(col); Mesh.SurfaceAddVertex(b);
        Mesh.SurfaceSetColor(col); Mesh.SurfaceAddVertex(c);
    }

    /// <summary>축 방향 원뿔(화살촉). 로컬 공간.</summary>
    protected void Cone(Vector3 axis, float baseAt, float length, float radius, Color col, int segs = 12)
    {
        var tip = axis * (baseAt + length);
        var center = axis * baseAt;
        var (u, v) = Perp(axis);
        for (int i = 0; i < segs; i++)
        {
            float a0 = MathF.Tau * i / segs, a1 = MathF.Tau * (i + 1) / segs;
            var p0 = center + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * radius;
            var p1 = center + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * radius;
            Tri(p0, p1, tip, col);
            Tri(center, p1, p0, col);
        }
    }

    protected void Cube(Vector3 center, float half, Color col)
    {
        var x = Vector3.Right * half; var y = Vector3.Up * half; var z = Vector3.Back * half;
        Vector3[] c = { center - x - y - z, center + x - y - z, center + x + y - z, center - x + y - z, center - x - y + z, center + x - y + z, center + x + y + z, center - x + y + z };
        int[][] f = { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 3, 7, 4 }, new[] { 1, 2, 6, 5 } };
        foreach (var q in f) { Tri(c[q[0]], c[q[1]], c[q[2]], col); Tri(c[q[0]], c[q[2]], c[q[3]], col); }
    }

    protected static (Vector3 u, Vector3 v) Perp(Vector3 axis)
    {
        var helper = MathF.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        var u = helper.Cross(axis).Normalized();
        var v = axis.Cross(u);
        return (u, v);
    }

    protected NVec3 LocalToWorld(Vector3 local) => Pivot + AxisX * (local.X * WorldUnit) + AxisY * (local.Y * WorldUnit) + AxisZ * (local.Z * WorldUnit);

    protected NVec2? Proj(CameraProjection proj, Vector3 local) => proj.Project(LocalToWorld(local), out _);
}
