using Cube.App.Bridge;
using Cube.Core.Picking;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Viewport.Gizmos;

/// <summary>
/// 조작기에서 마우스가 가리키거나 드래그 중인 부분.
/// None = 없음, X/Y/Z = 축 화살표·링·스케일 축, XY/YZ/XZ = 두 축 평면 핸들(이동 조작기), Center = 중앙 핸들(화면 평행 이동/균등 스케일),
/// Screen = 회전 조작기의 화면 평행 외곽 링.
/// </summary>
public enum GizmoPart { None, X, Y, Z, XY, YZ, XZ, Center, Screen }

/// <summary>
/// 조작기 공통: 월드 피벗 + 정규직교 축, 화면 고정 크기(기본 100px), 깊이 무시 렌더링, 호버/활성 하이라이트.
/// 히트 테스트는 CPU 스크린 공간에서 한다. 파생 클래스가 지오메트리와 히트를 구현한다.
/// </summary>
/// <remarks>
/// 지오메트리는 "로컬 단위 공간"(축 길이 1 = 화면 <see cref="ScreenSizePx"/> 픽셀)에서 만들고, 매 프레임 <c>UpdateScale</c>이
/// 노드 Transform의 기저를 (축 × <see cref="WorldUnit"/>)으로 잡아 카메라 거리와 무관하게 같은 화면 크기로 보이게 한다.
/// 히트 테스트도 같은 로컬 점을 <see cref="LocalToWorld"/>로 월드에 놓고 카메라로 투영해 화면 픽셀 거리로 판정한다.
/// 툴(TransformToolBase)이 <see cref="Pivot"/>/축을 설정하고 <see cref="SetHover"/>/<see cref="SetActive"/>로 하이라이트를 바꾼다.
/// </remarks>
public abstract partial class GizmoBase : Node3D
{
    /// <summary>X축(빨강) 색.</summary>
    public static readonly Color ColX = MathConvert.Rgb(0xff2a2a);
    /// <summary>Y축(초록) 색.</summary>
    public static readonly Color ColY = MathConvert.Rgb(0x5aff2a);
    /// <summary>Z축(파랑) 색.</summary>
    public static readonly Color ColZ = MathConvert.Rgb(0x2a6aff);
    /// <summary>호버/활성 파트 하이라이트 색(노랑, Maya와 같음).</summary>
    public static readonly Color ColHi = MathConvert.Rgb(0xffff00);
    /// <summary>중앙 핸들·외곽 링 기본 색(밝은 회색).</summary>
    public static readonly Color ColCenter = MathConvert.Rgb(0xd0d0d0);
    /// <summary>축 길이 1이 화면에서 차지하는 픽셀 수(UiScale을 곱하기 전).</summary>
    public const float ScreenSizePx = 100f;
    /// <summary>축 선분 히트 판정 거리(px, UiScale을 곱하기 전).</summary>
    public const float HitPx = 8f;

    /// <summary>조작기 중심의 월드 위치(오브젝트 피벗 또는 선택 컴포넌트 중심).</summary>
    public NVec3 Pivot;
    /// <summary>조작기의 정규직교 축(월드). 축 방향 World/Local/Normal에 따라 툴이 설정한다.</summary>
    public NVec3 AxisX = NVec3.UnitX, AxisY = NVec3.UnitY, AxisZ = NVec3.UnitZ;
    /// <summary>마우스가 올라가 있는 파트(드래그 중이 아닐 때 하이라이트).</summary>
    public GizmoPart Hover { get; private set; }
    /// <summary>드래그 중인 파트. None이 아니면 호버보다 우선해 하이라이트된다.</summary>
    public GizmoPart Active { get; private set; }
    /// <summary>로컬 길이 1에 해당하는 월드 길이. <c>UpdateScale</c>이 투영(원근 깊이/직교)과 UI 배율로 매번 다시 계산한다.</summary>
    public float WorldUnit { get; private set; } = 1f; // 화면 100px에 해당하는 월드 길이

    /// <summary>조작기 선/삼각형을 담는 즉시 모드 메시(로컬 단위 공간).</summary>
    protected ImmediateMesh Mesh = null!;
    /// <summary>메시를 그리는 노드. 깊이 테스트 없음 + 높은 RenderPriority로 항상 위에 그린다.</summary>
    protected MeshInstance3D MeshNode = null!;
    /// <summary>조작기가 붙은 뷰포트 패널(투영 계산에 사용). 4분할 뷰에서 활성 패널이 바뀌면 다시 설정된다.</summary>
    protected ViewportPanel Panel = null!;
    /// <summary>호버/활성/축이 바뀌어 지오메트리를 다시 그려야 하는지.</summary>
    private bool _dirty = true;

    /// <summary>
    /// 패널을 지정하고(처음 한 번은) 메시 노드와 머티리얼을 만든다. 머티리얼은 무광·정점 색·깊이 무시·양면·알파 블렌드이며
    /// 처음에는 숨겨 둔다(툴이 선택이 있을 때 Visible을 켠다).
    /// </summary>
    /// <param name="panel">조작기를 그릴 뷰포트 패널.</param>
    public void Setup(ViewportPanel panel)
    {
        Panel = panel;
        if (MeshNode != null) return; // 패널만 바꾸는 경우(4분할 뷰 전환)
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

    /// <summary>호버 파트를 바꾸고, 달라졌으면 다시 그리도록 표시한다.</summary>
    public void SetHover(GizmoPart p) { if (Hover != p) { Hover = p; _dirty = true; } }
    /// <summary>활성(드래그) 파트를 바꾸고, 달라졌으면 다시 그리도록 표시한다.</summary>
    public void SetActive(GizmoPart p) { if (Active != p) { Active = p; _dirty = true; } }
    /// <summary>다음 갱신 때 지오메트리를 다시 그리게 한다(축·카메라 변경 등 외부 요인).</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>파트의 표시 색: 활성 파트이거나(드래그 없을 때) 호버 파트면 하이라이트 색, 아니면 기본 색.</summary>
    protected Color PartColor(GizmoPart part, Color baseColor)
        => Active == part || (Active == GizmoPart.None && Hover == part) ? ColHi : baseColor;

    /// <summary>축 파트(X/Y/Z)의 월드 축 방향. 다른 파트는 0 벡터.</summary>
    public NVec3 AxisOf(GizmoPart p) => p switch { GizmoPart.X => AxisX, GizmoPart.Y => AxisY, GizmoPart.Z => AxisZ, _ => NVec3.Zero };

    /// <summary>평면 핸들의 법선.</summary>
    public NVec3 PlaneNormalOf(GizmoPart p) => p switch { GizmoPart.XY => AxisZ, GizmoPart.YZ => AxisX, GizmoPart.XZ => AxisY, _ => NVec3.Zero };

    /// <summary>보이는 동안 매 프레임 화면 고정 크기를 다시 맞추고, 필요하면 지오메트리를 다시 그린다.</summary>
    public override void _Process(double delta)
    {
        if (!Visible) return;
        UpdateScale();
        if (_dirty) { Rebuild(); _dirty = false; }
    }

    /// <summary>다음 프레임을 기다리지 않고 크기/지오메트리를 즉시 갱신(히트 테스트가 바로 이어질 때).</summary>
    public void ForceUpdate()
    {
        if (Panel == null || !IsInsideTree()) return;
        UpdateScale();
        if (_dirty) { Rebuild(); _dirty = false; }
    }

    /// <summary>
    /// 피벗의 카메라 깊이(직교면 1)에서 픽셀당 월드 길이를 구해 <see cref="WorldUnit"/>을 정하고,
    /// 노드 Transform = (축 × WorldUnit 기저, 피벗 원점)으로 설정한다. 로컬 단위 지오메트리가 화면에서 항상 같은 크기가 된다.
    /// </summary>
    private void UpdateScale()
    {
        var proj = Panel.Picker.Projection();
        // 원근: 카메라 전방 방향으로의 깊이(너무 가까우면 0.01로 제한). 직교: 깊이와 무관하므로 1
        float depth = proj.IsOrtho ? 1f : MathF.Max(NVec3.Dot(Pivot - proj.Eye, proj.Forward), 0.01f);
        WorldUnit = proj.WorldPerPixel(depth) * ScreenSizePx * CubeApp.Instance.UiScale;
        var basis = new Basis(AxisX.ToGodot() * WorldUnit, AxisY.ToGodot() * WorldUnit, AxisZ.ToGodot() * WorldUnit);
        Transform = new Transform3D(basis, Pivot.ToGodot());
    }

    /// <summary>로컬(축 길이 1) 지오메트리를 ImmediateMesh에 다시 그린다.</summary>
    protected abstract void Rebuild();

    /// <summary>화면 픽셀에서 가장 가까운 파트.</summary>
    /// <param name="px">뷰포트 로컬 픽셀 좌표.</param>
    /// <param name="proj">현재 카메라 투영.</param>
    /// <returns>판정 거리 안에서 가장 가까운(또는 우선순위가 높은) 파트, 없으면 None.</returns>
    public abstract GizmoPart HitTest(NVec2 px, CameraProjection proj);

    // ---------------------------------------------------------------- 그리기 헬퍼 (로컬 단위 공간)

    /// <summary>선분 하나(정점 2개, 같은 색)를 현재 Lines 서피스에 추가한다.</summary>
    protected void Line(Vector3 a, Vector3 b, Color c)
    {
        Mesh.SurfaceSetColor(c); Mesh.SurfaceAddVertex(a);
        Mesh.SurfaceSetColor(c); Mesh.SurfaceAddVertex(b);
    }

    /// <summary>삼각형 하나를 현재 Triangles 서피스에 추가한다(양면 렌더링이라 감기 순서 무관).</summary>
    protected void Tri(Vector3 a, Vector3 b, Vector3 c, Color col)
    {
        Mesh.SurfaceSetColor(col); Mesh.SurfaceAddVertex(a);
        Mesh.SurfaceSetColor(col); Mesh.SurfaceAddVertex(b);
        Mesh.SurfaceSetColor(col); Mesh.SurfaceAddVertex(c);
    }

    /// <summary>축 방향 원뿔(화살촉). 로컬 공간.</summary>
    /// <param name="axis">원뿔 방향(로컬 단위 벡터).</param>
    /// <param name="baseAt">밑면 중심의 축 위치.</param>
    /// <param name="length">밑면에서 꼭짓점까지 길이.</param>
    /// <param name="radius">밑면 반지름.</param>
    /// <param name="col">색.</param>
    /// <param name="segs">둘레 분할 수.</param>
    protected void Cone(Vector3 axis, float baseAt, float length, float radius, Color col, int segs = 12)
    {
        var tip = axis * (baseAt + length);
        var center = axis * baseAt;
        // 축에 수직인 두 단위 벡터로 밑면 원을 만들고, 조각마다 옆면 삼각형과 밑면 삼각형을 추가
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

    /// <summary>축 정렬 큐브(로컬 공간, 반변 길이 half)를 삼각형 12개로 추가한다. 중앙 핸들·스케일 팁에 쓴다.</summary>
    protected void Cube(Vector3 center, float half, Color col)
    {
        // 8개 꼭짓점과 6개 사각 면(각각 삼각형 2개)
        var x = Vector3.Right * half; var y = Vector3.Up * half; var z = Vector3.Back * half;
        Vector3[] c = { center - x - y - z, center + x - y - z, center + x + y - z, center - x + y - z, center - x - y + z, center + x - y + z, center + x + y + z, center - x + y + z };
        int[][] f = { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 3, 7, 4 }, new[] { 1, 2, 6, 5 } };
        foreach (var q in f) { Tri(c[q[0]], c[q[1]], c[q[2]], col); Tri(c[q[0]], c[q[2]], c[q[3]], col); }
    }

    /// <summary>축에 수직인 정규직교 두 벡터 (u, v)를 구한다. 축이 Y에 가까우면 보조 벡터로 X를 써서 외적이 퇴화하지 않게 한다.</summary>
    protected static (Vector3 u, Vector3 v) Perp(Vector3 axis)
    {
        var helper = MathF.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        var u = helper.Cross(axis).Normalized();
        var v = axis.Cross(u);
        return (u, v);
    }

    /// <summary>로컬 단위 공간 점을 월드 좌표로: 피벗 + Σ 축 × (성분 × WorldUnit).</summary>
    protected NVec3 LocalToWorld(Vector3 local) => Pivot + AxisX * (local.X * WorldUnit) + AxisY * (local.Y * WorldUnit) + AxisZ * (local.Z * WorldUnit);

    /// <summary>로컬 단위 공간 점을 화면 픽셀로 투영한다(카메라 뒤면 null).</summary>
    protected NVec2? Proj(CameraProjection proj, Vector3 local) => proj.Project(LocalToWorld(local), out _);
}
