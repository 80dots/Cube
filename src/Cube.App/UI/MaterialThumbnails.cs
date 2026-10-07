using Cube.App.Viewport;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// 머티리얼 썸네일(구에 재질을 입혀 렌더). 머티리얼마다 작은 SubViewport(자체 World3D, 카메라, 헤드라이트, SphereMesh)를 두고
/// 한 번만 그린 뒤(UpdateMode.Once) 머티리얼이 바뀔 때 다시 그린다. 텍스처는 ItemList 아이콘으로 그대로 쓴다.
/// </summary>
public partial class MaterialThumbnails : Node
{
    private readonly Dictionary<int, (SubViewport vp, MeshInstance3D mesh)> _views = new();
    public int SizePx { get; set; } = 96;

    public Texture2D Get(MaterialDef def)
    {
        if (!_views.TryGetValue(def.Id, out var v))
        {
            var vp = new SubViewport { Name = $"Thumb{def.Id}", Size = new Vector2I(SizePx, SizePx), OwnWorld3D = true, World3D = new World3D(), TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Once, Msaa3D = Godot.Viewport.Msaa.Msaa2X };
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.2f, 0.2f, 0.2f, 0f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.3f, 0.3f, 0.3f), AmbientLightEnergy = 1f };
            vp.AddChild(new WorldEnvironment { Environment = env });
            var cam = new Camera3D { Position = new Vector3(0, 0, 2.7f), Fov = 26, Current = true };
            vp.AddChild(cam);
            var light = new DirectionalLight3D { LightEnergy = 1.1f, ShadowEnabled = false };
            light.RotationDegrees = new Vector3(-30, -35, 0);
            cam.AddChild(light);
            var mesh = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 48, Rings = 24 } };
            vp.AddChild(mesh);
            AddChild(vp);
            v = (vp, mesh);
            _views[def.Id] = v;
        }
        v.mesh.MaterialOverride = MaterialCache.Get(def);
        v.vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        return v.vp.GetTexture();
    }

    /// <summary>더 이상 없는 머티리얼의 뷰포트를 정리한다.</summary>
    public void Prune(IEnumerable<int> aliveIds)
    {
        var alive = new HashSet<int>(aliveIds);
        foreach (var id in _views.Keys.ToList())
            if (!alive.Contains(id)) { _views[id].vp.QueueFree(); _views.Remove(id); }
    }

    public void Invalidate(int id) { if (_views.TryGetValue(id, out var v)) v.vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once; }
}
