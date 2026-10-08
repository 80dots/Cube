using Cube.App.Viewport;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// 머티리얼 썸네일(구에 재질을 입혀 렌더). 머티리얼마다 작은 SubViewport(자체 World3D, 카메라, 헤드라이트, SphereMesh)를 두고
/// 한 번만 그린 뒤(UpdateMode.Once) 머티리얼이 바뀔 때 다시 그린다. 텍스처는 ItemList 아이콘으로 그대로 쓴다.
/// </summary>
/// <remarks>
/// 썸네일 SubViewport들은 이 노드의 자식으로 붙어 있어야 렌더링된다(트리에 있어야 그려짐).
/// 머티리얼 ID가 키이며, 삭제된 머티리얼은 <see cref="Prune"/>으로 정리한다.
/// </remarks>
public partial class MaterialThumbnails : Node
{
    /// <summary>머티리얼 ID → (썸네일 SubViewport, 구 MeshInstance3D). 머티리얼당 하나씩 재사용한다.</summary>
    private readonly Dictionary<int, (SubViewport vp, MeshInstance3D mesh)> _views = new();
    /// <summary>썸네일 한 변의 픽셀 크기(새로 만드는 뷰포트에만 적용).</summary>
    public int SizePx { get; set; } = 96;

    /// <summary>
    /// 머티리얼의 썸네일 텍스처를 얻는다. 처음이면 전용 SubViewport(자체 월드·투명 배경·고정 카메라·방향광·구)를 만들고,
    /// 매번 머티리얼을 다시 입힌 뒤 한 번만 다시 그리도록(UpdateMode.Once) 요청한다.
    /// 돌려주는 ViewportTexture는 뷰포트가 다시 그려지면 자동으로 갱신되므로 호출자는 그대로 아이콘으로 쓰면 된다.
    /// </summary>
    public Texture2D Get(MaterialDef def)
    {
        if (!_views.TryGetValue(def.Id, out var v))
        {
            // 메인 씬과 섞이지 않도록 OwnWorld3D + 새 World3D를 쓰고, 배경은 투명(알파 0)으로 둔다.
            var vp = new SubViewport { Name = $"Thumb{def.Id}", Size = new Vector2I(SizePx, SizePx), OwnWorld3D = true, World3D = new World3D(), TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Once, Msaa3D = Godot.Viewport.Msaa.Msaa2X };
            // 주변광은 단색으로 고정해 썸네일 밝기가 렌더 설정(IBL 등)과 무관하게 일정하도록 한다.
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.2f, 0.2f, 0.2f, 0f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.3f, 0.3f, 0.3f), AmbientLightEnergy = 1f };
            vp.AddChild(new WorldEnvironment { Environment = env });
            // 구 전체가 프레임에 들어오도록 좁은 FOV(26°)로 정면 2.7m 거리에서 본다.
            var cam = new Camera3D { Position = new Vector3(0, 0, 2.7f), Fov = 26, Current = true };
            vp.AddChild(cam);
            // 방향광은 카메라의 자식이라 항상 카메라 기준 좌상단에서 비춘다(헤드라이트).
            var light = new DirectionalLight3D { LightEnergy = 1.1f, ShadowEnabled = false };
            light.RotationDegrees = new Vector3(-30, -35, 0);
            cam.AddChild(light);
            // 반지름 0.5m 구(충분히 둥글게 48×24 분할).
            var mesh = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 48, Rings = 24 } };
            vp.AddChild(mesh);
            AddChild(vp);
            v = (vp, mesh);
            _views[def.Id] = v;
        }
        // 머티리얼이 바뀌었을 수 있으므로 매번 최신 셰이더 머티리얼을 입히고 한 프레임 다시 그린다.
        v.mesh.MaterialOverride = MaterialCache.Get(def);
        v.vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        return v.vp.GetTexture();
    }

    /// <param name="aliveIds">문서에 현재 존재하는 머티리얼 ID 목록. 여기에 없는 썸네일 뷰포트는 해제된다.</param>
    /// <summary>더 이상 없는 머티리얼의 뷰포트를 정리한다.</summary>
    public void Prune(IEnumerable<int> aliveIds)
    {
        var alive = new HashSet<int>(aliveIds);
        // 순회 중 사전을 수정하므로 키 목록을 복사해서 돈다.
        foreach (var id in _views.Keys.ToList())
            if (!alive.Contains(id)) { _views[id].vp.QueueFree(); _views.Remove(id); }
    }

    /// <summary>해당 머티리얼의 썸네일을 다음 프레임에 한 번 다시 그리게 한다(머티리얼 값이 바뀌었을 때).</summary>
    public void Invalidate(int id) { if (_views.TryGetValue(id, out var v)) v.vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once; }
}
