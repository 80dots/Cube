using Cube.App.Bridge;
using Cube.Core.Scene;
using Godot;
using GArray = Godot.Collections.Array;

namespace Cube.App.Viewport;

/// <summary>
/// 조인트 하나의 표시(Maya 식): 구 + 자식 조인트로 향하는 팔면체 본. 항상 메시 위에 보이도록 깊이 테스트를 끈다.
/// </summary>
/// <remarks>
/// SceneView가 조인트 SceneNode마다 하나 만들며, 노드 자체의 트랜스폼(Evaluated)은 SceneView가 설정하고 여기서는 로컬 공간 지오메트리만 그린다.
/// 본은 자식 조인트마다 원점 → 자식 위치(자식의 로컬 이동)로 향하는 팔면체이며, 구는 반지름 0.5 공유 SphereMesh를 Scale로 키운다.
/// 선택 상태 색은 SceneView가 <see cref="SetColor"/>로 바꾼다.
/// </remarks>
public partial class JointView : Node3D
{
    /// <summary>선택되지 않은 조인트 색(Maya 기본 파랑 계열).</summary>
    public static readonly Color JointNormal = MathConvert.Rgb(0x7f9fd0);
    /// <summary>선택된(비활성) 조인트 색.</summary>
    public static readonly Color JointSelected = MathConvert.Rgb(0xffffff);
    /// <summary>활성(마지막 선택) 조인트 색(초록).</summary>
    public static readonly Color JointActive = MathConvert.Rgb(0x3fff3f);
    /// <summary>호버(프리셀렉션) 조인트 색.</summary>
    public static readonly Color JointHover = MathConvert.Rgb(0xf0f0f0);

    /// <summary>이 뷰가 표시하는 조인트 SceneNode(문서 모델).</summary>
    public SceneNode Node { get; }
    /// <summary>구 표시 노드와 본(팔면체) 표시 노드. 둘 다 같은 머티리얼을 쓴다.</summary>
    private MeshInstance3D _sphere = null!, _bones = null!;
    /// <summary>자식 조인트로 향하는 팔면체 본 삼각형 메시(로컬 공간).</summary>
    private readonly ArrayMesh _boneMesh = new();
    /// <summary>로컬 회전 축(X 빨강/Y 초록/Z 파랑) 선 메시.</summary>
    private readonly ArrayMesh _axesMesh = new();
    /// <summary>로컬 회전 축 표시 노드(Display → Joint Local Rotation Axes일 때만 보임).</summary>
    private MeshInstance3D _axes = null!;
    /// <summary>구·본 공용 무광 X-ray 머티리얼(색 = 선택 상태).</summary>
    private StandardMaterial3D _mat = null!;
    /// <summary>모든 조인트가 공유하는 단위 구 메시(반지름 0.5).</summary>
    private static SphereMesh? _sphereMesh;

    /// <summary>조인트 노드를 받아 뷰를 만든다(Godot 노드 이름 = 조인트 이름).</summary>
    public JointView(SceneNode node) { Node = node; Name = node.Name; }

    /// <summary>
    /// 공유 구 메시·머티리얼과 자식 노드(Sphere/Bones/Axes)를 만든다. 모두 깊이 테스트 없이 RenderPriority 40/41로 메시 위에 그려진다.
    /// 마지막에 <see cref="Refresh"/>로 현재 상태를 반영한다.
    /// </summary>
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

    /// <summary>표시 반지름 = 조인트 반지름 × Display → Joint Size 배율.</summary>
    public float Radius => (Node.Joint?.Radius ?? 0.08f) * Math.Clamp(CubeApp.Instance.Settings.JointDisplayScale, 0.01f, 100f);

    /// <summary>뷰포트에서 집을 수 있는지(노드가 보이고 Display → Joints가 켜져 있음).</summary>
    public bool Pickable => Visible && CubeApp.Instance.Settings.ShowJoints;

    // 마지막으로 만든 본의 입력(반지름/표시/자식 끝점). 같으면 메시를 다시 만들지 않는다(재생 중 매 프레임 호출되지만 회전만 하는 리그는 끝점이 그대로)
    /// <summary>마지막으로 만든 표시 반지름(-1 = 아직 없음).</summary>
    private float _builtRadius = -1;
    /// <summary>마지막으로 만든 시점의 Display → Joints / Joint Local Rotation Axes 상태.</summary>
    private bool _builtShown, _builtAxes;
    /// <summary>마지막으로 본을 만든 자식 끝점들(로컬).</summary>
    private readonly List<Vector3> _builtTips = new();
    /// <summary>이번 호출에서 모은 자식 끝점(재사용 스크래치 리스트).</summary>
    private readonly List<Vector3> _tips = new();

    /// <summary>자식 조인트 위치가 바뀌었을 때 본을 다시 만든다. 구 크기도 갱신. 입력이 지난번과 같으면 아무것도 하지 않는다.</summary>
    public void Refresh()
    {
        // 1) 현재 입력 수집: 반지름, 표시 설정, 자식 조인트의 로컬 위치(포즈 반영 Evaluated)
        if (_sphere == null) return;
        float r = Radius;
        bool shown = CubeApp.Instance.Settings.ShowJoints;
        bool showAxes = shown && CubeApp.Instance.Settings.ShowJointAxes;
        _tips.Clear();
        foreach (var c in Node.Children) if (c.IsJoint) _tips.Add(c.Evaluated.Translation.ToGodot());
        // 2) 입력이 지난번과 완전히 같으면 다시 만들지 않는다
        if (r == _builtRadius && shown == _builtShown && showAxes == _builtAxes && _tips.Count == _builtTips.Count)
        {
            bool same = true;
            for (int i = 0; i < _tips.Count && same; i++) same = _tips[i] == _builtTips[i];
            if (same) return;
        }
        // 3) 입력 기록 후 구 크기(지름 = 2r)와 표시 여부 갱신
        _builtRadius = r; _builtShown = shown; _builtAxes = showAxes;
        _builtTips.Clear(); _builtTips.AddRange(_tips);
        _sphere.Scale = new Vector3(r * 2, r * 2, r * 2);
        // Display → Joints: 노드 표시(Visible)와 별개로 구·본·축만 숨긴다(자식 뷰·스킨 메시는 그대로)
        _sphere.Visible = shown; _bones.Visible = shown;
        // 로컬 회전 축(Display → Joint Local Rotation Axes)
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
        // 4) 본 팔면체 다시 만들기: 자식마다 링 4점과 원점/끝점을 잇는 삼각형 8개
        _boneMesh.ClearSurfaces();
        var verts = new List<Vector3>();
        foreach (var tip in _tips)
        {
            float len = tip.Length();
            if (len < 1e-5f) continue;
            var dir = tip / len;
            // 본 팔면체: 원점 → 링(길이의 15%, 반지름 r·0.8) → 끝
            // dir에 수직인 두 축 a, b(보조 벡터는 dir이 Y에 가까우면 X)
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
        // 자식 조인트가 없으면(끝 조인트) 본 서피스 없음
        if (verts.Count == 0) return;
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        _boneMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    /// <summary>구·본 색을 바꾼다(선택/활성/호버 상태 표시). 아직 _Ready 전이면 무시.</summary>
    public void SetColor(Color c) { if (_mat != null) _mat.AlbedoColor = c; }
}
