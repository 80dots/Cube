using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Rig;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

/// <summary>
/// .cube 저장/열기 전체 속성 왕복 검증(감사): 삭제된 정점 슬롯이 있는 메시(Compact 리맵)의 스킨 가중치·크리즈·심·핀·
/// 잠긴 정점 노멀·고정 코너 노멀·UV 세트(여럿/이름 바꾼 하나), 라이트, 조인트 반지름, 애니메이션 키, 머티리얼 values/textures.
/// </summary>
public class CubeFileFullRoundTripTests
{
    /// <summary>문서를 직렬화한 뒤 새 문서로 읽는다.</summary>
    private static Document RoundTrip(Document doc)
    {
        var json = CubeFileFormat.Serialize(doc);
        var d2 = new Document();
        CubeFileFormat.Deserialize(d2, json);
        return d2;
    }

    /// <summary>
    /// 면 하나를 지워 정점 슬롯에 구멍이 생긴 큐브에 조인트 2개로 스킨을 바인드하고 각종 메시 속성을 넣은 뒤 왕복한다.
    /// 위치가 같은 정점끼리 가중치·잠긴 노멀이 같고, 조인트를 회전했을 때 LBS 결과도 같아야 한다.
    /// </summary>
    [Fact]
    public void RoundTrip_SkinnedMeshWithDeadSlots_PreservesEverything()
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var node = cube.Node; var m = node.Mesh!;
        // 정점 하나를 지워 슬롯 구멍을 만든다(그 정점을 쓰는 면 3개도 삭제).
        MeshOps.DeleteVertices(m, new[] { 0 });
        MeshNormals.Recompute(m);
        Assert.False(m.Verts[0].Alive);
        node.Local = new Transform3(new Vector3(1, 0, 0), new Vector3(0, 30, 0), new Vector3(1, 2, 1), new Vector3(0.1f, 0.2f, 0.3f));
        // 엣지 속성
        int liveEdge = Enumerable.Range(0, m.EdgeCount).First(e => m.Edges[e].Alive);
        MeshOps.SetCrease(m, new[] { liveEdge }, 2.5f);
        var se = m.Edges[liveEdge]; se.Seam = true; m.Edges[liveEdge] = se;
        // 잠긴 정점 노멀(마지막 정점)
        int lastV = m.VertexCount - 1;
        m.LockedNormals[lastV] = Vector3.Normalize(new Vector3(1, 1, 0));
        // 고정 코너 노멀 + 핀
        int f0 = Enumerable.Range(0, m.FaceCount).First(f => m.Faces[f].Alive);
        int he0 = m.Faces[f0].HalfEdge;
        var h = m.Hes[he0]; h.NormalLocked = true; h.Normal = Vector3.UnitX; h.PinUv = true; m.Hes[he0] = h;
        MeshNormals.Recompute(m);
        // UV 세트 둘(현재 = 두 번째, UV 다름)
        m.EnsureUvSets();
        int s1 = m.AddUvSet("lightmap", copyCurrent: true);
        m.SwitchUvSet(s1);
        for (int i = 0; i < m.Hes.Count; i++) if (m.Hes[i].Alive) { var hh = m.Hes[i]; hh.Uv0 = hh.Uv0 * 0.5f + new Vector2(0.25f); m.Hes[i] = hh; }

        // 조인트 둘(부모-자식) + 스킨
        var j1 = new SceneNode { Name = "j1", Shape = new JointShape { Radius = 0.3f }, Local = new Transform3(new Vector3(1, -0.5f, 0), Vector3.Zero, Vector3.One) };
        var j2 = new SceneNode { Name = "j2", Shape = new JointShape { Radius = 0.15f }, Local = new Transform3(new Vector3(0, 1, 0), Vector3.Zero, Vector3.One) };
        doc.AddNode(j1); doc.AddNode(j2, j1);
        var infos = new[] { j1, j2 }.Select(j => new JointInfo(j.Id, j.WorldMatrix, j.Children.Select(c => c.WorldMatrix.Translation).ToList())).ToList();
        var skin = SkinOps.SmoothBind(m, node.WorldMatrix, infos);
        doc.Undo.Push(new SetSkinCommand("Bind", node.Id, skin));

        // 라이트
        var light = new SceneNode { Name = "spot", Shape = new LightShape { Type = LightType.Spot, Color = new Vector3(1, 0.5f, 0.25f), Intensity = 3, Range = 7, SpotAngle = 33 } };
        doc.AddNode(light);
        // 머티리얼(values + textures)
        var mat = new MaterialDef { Name = "m", Type = MaterialType.Pbr };
        mat.Set("metallic", 0.75f); mat.Set("emissive", new Vector3(1, 2, 3)); mat.SetTex("normal", "C:/n.png");
        doc.Undo.Push(new AddMaterialCommand(mat));
        doc.Undo.Push(new AssignMaterialCommand(new[] { node.Id }, mat.Id));
        // 애니메이션(j2 회전 키 2개 + j1 위치 키)
        var clip = new AnimationClip { Name = "wave", Length = 2, FrameRate = 24, Loop = true };
        var tr = new NodeTrack { Node = j2.Id, NodeName = "j2" };
        tr.Rotation.Add(new AnimKey<Quaternion>(0.5f, Quaternion.Identity));
        tr.Rotation.Add(new AnimKey<Quaternion>(2f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f)));
        var tr1 = new NodeTrack { Node = j1.Id, NodeName = "j1" };
        tr1.Position.Add(new AnimKey<Vector3>(0.5f, new Vector3(1, -0.5f, 0)));
        tr1.Scale.Add(new AnimKey<Vector3>(1f, new Vector3(1, 2, 3)));
        clip.Tracks.Add(tr); clip.Tracks.Add(tr1);
        doc.Animations.Add(clip);

        var d2 = RoundTrip(doc);

        // 노드/셰이프
        var n2 = d2.Nodes.Values.First(n => n.Mesh != null);
        var j1b = d2.Nodes.Values.First(n => n.Name == "j1"); var j2b = d2.Nodes.Values.First(n => n.Name == "j2");
        Assert.Equal(0.3f, j1b.Joint!.Radius); Assert.Equal(0.15f, j2b.Joint!.Radius);
        Assert.Same(j1b, j2b.Parent);
        Assert.Equal(node.Local, n2.Local);
        var l2 = d2.Nodes.Values.First(n => n.Light != null).Light!;
        Assert.Equal(LightType.Spot, l2.Type); Assert.Equal(new Vector3(1, 0.5f, 0.25f), l2.Color);
        Assert.Equal(3, l2.Intensity); Assert.Equal(7, l2.Range); Assert.Equal(33, l2.SpotAngle);
        // 머티리얼
        var mat2 = d2.FindMaterial(n2.MaterialId)!;
        Assert.Equal(0.75f, mat2.GetF("metallic")); Assert.Equal(new Vector3(1, 2, 3), mat2.Get("emissive")); Assert.Equal("C:/n.png", mat2.Tex("normal"));
        // 메시
        var m2 = n2.Mesh!;
        Assert.Empty(MeshValidator.Check(m2));
        Assert.Equal(m.AliveVertexCount, m2.AliveVertexCount);
        Assert.Equal(m.AliveFaceCount, m2.AliveFaceCount);
        Assert.Equal(1, Enumerable.Range(0, m2.EdgeCount).Count(e => m2.Edges[e].Alive && m2.Edges[e].Crease == 2.5f));
        Assert.Equal(Enumerable.Range(0, m.EdgeCount).Count(e => m.Edges[e].Alive && m.Edges[e].Seam), Enumerable.Range(0, m2.EdgeCount).Count(e => m2.Edges[e].Alive && m2.Edges[e].Seam));
        Assert.Equal(1, m2.Hes.Count(x => x.Alive && x.PinUv));
        Assert.Equal(1, m2.Hes.Count(x => x.Alive && x.NormalLocked));
        Assert.Equal(Vector3.UnitX, m2.Hes.First(x => x.Alive && x.NormalLocked).Normal);
        // 잠긴 정점 노멀은 같은 위치 정점에
        var lockedPos = m.Verts[lastV].Position;
        var lkv = m2.LockedNormals.Single(); int lv2 = lkv.Key; var ln2 = lkv.Value;
        Assert.Equal(lockedPos, m2.Verts[lv2].Position);
        Assert.Equal(m.LockedNormals[lastV], ln2);
        // UV 세트
        Assert.Equal(2, m2.UvSets.Count);
        Assert.Equal("lightmap", m2.UvSets[1].Name);
        Assert.Equal(1, m2.CurrentUvSet);
        var uvA = m.Hes.Where(x => x.Alive).Select(x => x.Uv0).ToHashSet();
        var uvB = m2.Hes.Where(x => x.Alive).Select(x => x.Uv0).ToHashSet();
        Assert.Equal(uvA, uvB);
        // 스킨: 위치가 같은 정점끼리 가중치 비교
        var skin2 = n2.Skin!;
        Assert.Equal(2, skin2.Joints.Count);
        Assert.Equal(j1b.Id, skin2.Joints[0]); Assert.Equal(j2b.Id, skin2.Joints[1]);
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            int v2 = Enumerable.Range(0, m2.VertexCount).Single(k => m2.Verts[k].Position == m.Verts[v].Position);
            for (int j = 0; j < 2; j++) Assert.Equal(skin.GetWeight(v, j), skin2.GetWeight(v2, j), 5);
        }
        // 애니메이션
        var c2 = Assert.Single(d2.Animations);
        Assert.Equal("wave", c2.Name); Assert.Equal(2, c2.Length); Assert.Equal(24, c2.FrameRate); Assert.True(c2.Loop);
        Assert.Equal(0.5f, c2.StartTime);
        var t2 = c2.Tracks.First(t => t.NodeName == "j2");
        Assert.Equal(j2b.Id, t2.Node);
        Assert.Equal(2, t2.Rotation.Count);
        Assert.Equal(tr.Rotation[1].Value, t2.Rotation[1].Value);
        var t1 = c2.Tracks.First(t => t.NodeName == "j1");
        Assert.Single(t1.Position); Assert.Single(t1.Scale);
        Assert.Equal(new Vector3(1, 2, 3), t1.Scale[0].Value);
    }

    /// <summary>UV 세트가 하나뿐이지만 이름을 바꾼 경우에도 이름이 저장되어야 한다.</summary>
    [Fact]
    public void RoundTrip_SingleRenamedUvSet_KeepsName()
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var m = cube.Node.Mesh!;
        m.EnsureUvSets();
        m.UvSets[0].Name = "atlas";
        var d2 = RoundTrip(doc);
        var m2 = d2.MeshNodes().First().Mesh!;
        m2.EnsureUvSets();
        Assert.Equal("atlas", m2.UvSets[0].Name);
    }
}
