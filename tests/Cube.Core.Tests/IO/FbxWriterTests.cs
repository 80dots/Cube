using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO.Fbx;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

/// <summary>
/// 자체 바이너리 FBX 7.4 writer(M4)를 검증한다: <c>FbxBinaryWriter</c>의 모든 속성 타입·배열 압축 왕복(<c>FbxBinaryReader</c>로 다시 읽기),
/// <c>FbxSceneBuilder</c>가 만드는 Geometry/Model/Material/Texture/연결/정의/전역 설정, 피벗 베이크, PBR 텍스처 슬롯 매핑, 스킨 클러스터·바인드 포즈.
/// </summary>
public class FbxWriterTests
{
    /// <summary>
    /// short/bool/int/float/double/long/string/raw 바이트와 float/double/long/int/bool 배열, 속성 없는 노드, 식별자 문자열(name\0\x01class),
    /// 중첩 자식을 써서 다시 읽으면 값이 모두 같아야 한다. 헤더 버전 7400과 Blender와 같은 푸터 바이트도 확인한다.
    /// </summary>
    [Fact]
    public void Writer_RoundTrips_AllPropertyTypes_ThroughReader()
    {
        var root = new FbxNode("Root", (short)7, true, 42, 1.5f, 2.25, 123456789012L, "hello", new byte[] { 1, 2, 3 });
        var arrays = root.Add("Arrays", new float[] { 1, 2, 3 }, new double[200].Select((_, i) => i * 0.5).ToArray(), new long[] { -1, 2 }, Enumerable.Range(0, 100).ToArray(), new[] { true, false, true });
        root.Add("Empty");
        var leaf = root.Add("Leaf", FbxNode.Id("Model", "cube"));
        leaf.Add("Deep", 1);

        // 실행: 최상위 노드 두 개를 바이너리로 쓰고 리더로 다시 파싱한다.
        var bytes = FbxBinaryWriter.Write(new[] { root, new FbxNode("Second", 9) });
        Assert.True(FbxBinaryReader.IsBinaryFbx(bytes));
        Assert.Equal(7400u, BitConverter.ToUInt32(bytes, 23));
        var (version, nodes) = FbxBinaryReader.Read(bytes);
        Assert.Equal(7400, version);
        Assert.Equal(2, nodes.Count);
        var r = nodes[0];
        Assert.Equal("Root", r.Name);
        Assert.Equal((short)7, r.Prop<short>(0));
        Assert.True(r.Prop<bool>(1));
        Assert.Equal(42, r.Prop<int>(2));
        Assert.Equal(1.5f, r.Prop<float>(3));
        Assert.Equal(2.25, r.Prop<double>(4));
        Assert.Equal(123456789012L, r.Prop<long>(5));
        Assert.Equal("hello", r.Prop<string>(6));
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Prop<byte[]>(7));
        var a = r.Child("Arrays")!;
        Assert.Equal(new float[] { 1, 2, 3 }, a.Prop<float[]>(0));
        Assert.Equal(200, a.Prop<double[]>(1).Length);
        Assert.Equal(99.5, a.Prop<double[]>(1)[199]);
        Assert.Equal(new long[] { -1, 2 }, a.Prop<long[]>(2));
        Assert.Equal(Enumerable.Range(0, 100).ToArray(), a.Prop<int[]>(3));
        Assert.Equal(new[] { true, false, true }, a.Prop<bool[]>(4));
        Assert.NotNull(r.Child("Empty"));
        Assert.Equal("Model::cube", FbxNode.ReadableId(r.Child("Leaf")!.Prop<string>(0)));
        Assert.Equal(1, r.Child("Leaf")!.Child("Deep")!.Prop<int>(0));
        Assert.Equal(9, nodes[1].Prop<int>(0));
        // 푸터: 16바이트 ID + 4 zero + 정렬 패딩 + 버전 + 120 zero + 16바이트 ID2
        Assert.Equal(0x0b, bytes[^1]);
        Assert.Equal(0xf8, bytes[^16]);
    }

    /// <summary>
    /// 큰 double 배열을 zlib 압축/비압축으로 각각 써서 압축본이 더 작고, 두 경우 모두 원래 값으로 정확히 복원되는지 확인한다.
    /// </summary>
    [Fact]
    public void Writer_CompressedArrays_DecodeToSameValues()
    {
        var data = Enumerable.Range(0, 5000).Select(i => Math.Sin(i)).ToArray();
        var n = new FbxNode("A", data);
        var compressed = FbxBinaryWriter.Write(new[] { n }, compress: true);
        var raw = FbxBinaryWriter.Write(new[] { n }, compress: false);
        Assert.True(compressed.Length < raw.Length);
        Assert.Equal(data, FbxBinaryReader.Read(compressed).nodes[0].Prop<double[]>(0));
        Assert.Equal(data, FbxBinaryReader.Read(raw).nodes[0].Prop<double[]>(0));
    }

    /// <summary>
    /// 트랜스폼(피벗 포함)과 텍스처가 있는 Phong 머티리얼이 할당된 큐브를 내보낸다.
    /// BakePivots=false: 정점 cm 단위(0.5m → 50), PolygonVertexIndex의 면 끝 ~i 표기, 노멀/UV 레이어 크기, Lcl T/R/S·RotationPivot/ScalingPivot 속성.
    /// 기본(BakePivots): 피벗 속성 없이 정점 −P, Lcl Translation = P+T이며 월드 결과가 같아야 한다.
    /// 이어서 Material/Texture/Video 노드, OO/OP 연결(Texture → DiffuseColor), Definitions 개수, GlobalSettings(Y-up, 단위 1)를 확인한다.
    /// </summary>
    [Fact]
    public void SceneBuilder_Cube_WritesGeometryModelMaterialAndConnections()
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        cube.Node.Local = new Transform3(new Vector3(1, 2, 3), new Vector3(0, 90, 0), new Vector3(2, 2, 2), new Vector3(0.5f, 0, 0));
        var mat = new MaterialDef { Name = "wood", Type = MaterialType.BlinnPhong, Color = new Vector3(0.8f, 0.6f, 0.4f), TexturePath = "C:/tex/wood.png" };
        doc.Undo.Push(new AddMaterialCommand(mat));
        doc.Undo.Push(new AssignMaterialCommand(new[] { cube.Node.Id }, mat.Id));

        // 1단계: 피벗을 베이크하지 않는 옵션으로 내보내 원래 피벗 속성이 기록되는지 본다.
        var builder = new FbxSceneBuilder(doc, FbxExportOptions.Default with { BakePivots = false });
        var top = builder.Build(new[] { cube.Node });
        Assert.Equal(1, builder.NodeCount);
        Assert.Equal(12, builder.TriangleCount);
        var bytes = FbxBinaryWriter.Write(top);
        var (_, nodes) = FbxBinaryReader.Read(bytes);
        var objects = nodes.First(n => n.Name == "Objects");
        var geom = objects.All("Geometry").Single();
        Assert.Equal("Geometry::pCube1", FbxNode.ReadableId(geom.Prop<string>(1)));
        var verts = geom.Child("Vertices")!.Prop<double[]>(0);
        Assert.Equal(24, verts.Length);
        Assert.Equal(50.0, verts.Max(), 3); // 0.5 m → 50 cm
        var poly = geom.Child("PolygonVertexIndex")!.Prop<int[]>(0);
        Assert.Equal(24, poly.Length);
        Assert.Equal(6, poly.Count(i => i < 0)); // 면마다 마지막 인덱스는 ~i
        Assert.All(poly, i => Assert.InRange(i < 0 ? ~i : i, 0, 7));
        Assert.Equal(72, geom.Child("LayerElementNormal")!.Child("Normals")!.Prop<double[]>(0).Length);
        Assert.Equal(24, geom.Child("LayerElementUV")!.Child("UVIndex")!.Prop<int[]>(0).Length);

        var model = objects.All("Model").Single();
        Assert.Equal("Mesh", model.Prop<string>(2));
        var props = model.Child("Properties70")!.All("P").ToDictionary(p => p.Prop<string>(0), p => p);
        Assert.Equal(100.0, props["Lcl Translation"].Prop<double>(4), 3);
        Assert.Equal(90.0, props["Lcl Rotation"].Prop<double>(5), 3);
        Assert.Equal(2.0, props["Lcl Scaling"].Prop<double>(4), 3);
        Assert.Equal(50.0, props["RotationPivot"].Prop<double>(4), 3);
        Assert.Equal(50.0, props["ScalingPivot"].Prop<double>(4), 3);

        // 기본(BakePivots): 피벗 속성 없이 정점을 −P 옮기고 Lcl Translation = P+T. 월드 위치는 같아야 한다.
        var baked = new FbxSceneBuilder(doc);
        var (_, nodes2) = FbxBinaryReader.Read(FbxBinaryWriter.Write(baked.Build(new[] { cube.Node })));
        var objects2 = nodes2.First(n => n.Name == "Objects");
        var props2 = objects2.All("Model").Single().Child("Properties70")!.All("P").ToDictionary(p => p.Prop<string>(0), p => p);
        Assert.False(props2.ContainsKey("RotationPivot"));
        Assert.Equal(150.0, props2["Lcl Translation"].Prop<double>(4), 3); // (0.5 + 1) m
        var verts2 = objects2.All("Geometry").Single().Child("Vertices")!.Prop<double[]>(0);
        Assert.Equal(-100.0, verts2.Where((_, i) => i % 3 == 0).Min(), 3); // x: −0.5 − 0.5 = −1 m
        Assert.Equal(0.0, verts2.Where((_, i) => i % 3 == 0).Max(), 3);
        // 월드 검증: 베이크된 정점에 S·R·T(P+T)를 적용한 결과 = 원래 정점에 ToMatrix 적용
        var tr = cube.Node.Local; var bakedT = new Transform3(tr.Translation + tr.Pivot, tr.RotationDegrees, tr.Scale, Vector3.Zero);
        var p0 = cube.Node.Mesh!.Verts[0].Position;
        var w1 = Vector3.Transform(p0, tr.ToMatrix());
        var w2 = Vector3.Transform(p0 - tr.Pivot, bakedT.ToMatrix());
        Assert.True((w1 - w2).Length() < 1e-4f);

        // 3단계: 머티리얼·텍스처 노드와 오브젝트 간 연결(Connections) 검증(1단계 결과 objects/nodes를 다시 사용).
        var material = objects.All("Material").Single();
        Assert.Equal("Material::wood", FbxNode.ReadableId(material.Prop<string>(1)));
        Assert.Equal("Phong", material.Child("ShadingModel")!.Prop<string>(0));
        var tex = objects.All("Texture").Single();
        Assert.Equal("C:/tex/wood.png", tex.Child("FileName")!.Prop<string>(0));
        Assert.Single(objects.All("Video"));

        long modelId = model.Prop<long>(0), geomId = geom.Prop<long>(0), matId = material.Prop<long>(0), texId = tex.Prop<long>(0);
        var conns = nodes.First(n => n.Name == "Connections").All("C").ToList();
        Assert.Contains(conns, c => c.Prop<string>(0) == "OO" && c.Prop<long>(1) == modelId && c.Prop<long>(2) == 0L);
        Assert.Contains(conns, c => c.Prop<string>(0) == "OO" && c.Prop<long>(1) == geomId && c.Prop<long>(2) == modelId);
        Assert.Contains(conns, c => c.Prop<string>(0) == "OO" && c.Prop<long>(1) == matId && c.Prop<long>(2) == modelId);
        Assert.Contains(conns, c => c.Prop<string>(0) == "OP" && c.Prop<long>(1) == texId && c.Prop<long>(2) == matId && c.Prop<string>(3) == "DiffuseColor");

        // 4단계: Definitions의 타입별 개수와 GlobalSettings 축/단위.
        var defs = nodes.First(n => n.Name == "Definitions");
        var counts = defs.All("ObjectType").ToDictionary(o => o.Prop<string>(0), o => o.Child("Count")!.Prop<int>(0));
        Assert.Equal(1, counts["Model"]); Assert.Equal(1, counts["Geometry"]); Assert.Equal(1, counts["Material"]); Assert.Equal(1, counts["Texture"]); Assert.Equal(1, counts["Video"]);
        Assert.Equal(counts.Values.Sum(), defs.Child("Count")!.Prop<int>(0)); // GlobalSettings 포함
        var gs = nodes.First(n => n.Name == "GlobalSettings").Child("Properties70")!.All("P").ToDictionary(p => p.Prop<string>(0), p => p);
        Assert.Equal(1, gs["UpAxis"].Prop<int>(4));
        Assert.Equal(1.0, gs["UnitScaleFactor"].Prop<double>(4));
    }

    /// <summary>
    /// PBR 파라미터 텍스처가 FBX 표준 슬롯에 연결되는지 확인한다: color → DiffuseColor, normal → NormalMap,
    /// roughness → ShininessExponent, metallic → ReflectionFactor, emissive → EmissiveColor, occlusion → AmbientColor.
    /// 같은 이미지(base.png)는 Texture 노드 하나를 공유하고, emissiveStrength는 EmissiveFactor 값으로 기록되어야 한다.
    /// </summary>
    [Fact]
    public void SceneBuilder_MaterialParameterTextures_ConnectToStandardSlots()
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var mat = new MaterialDef { Name = "pbr", Type = MaterialType.Pbr };
        mat.SetTex("color", "C:/t/base.png"); mat.SetTex("normal", "C:/t/n.png"); mat.SetTex("roughness", "C:/t/r.png");
        mat.SetTex("metallic", "C:/t/m.png"); mat.SetTex("emissive", "C:/t/e.png"); mat.SetTex("occlusion", "C:/t/base.png");
        mat.Set("emissive", Vector3.One); mat.Set("emissiveStrength", 3f);
        doc.Undo.Push(new AddMaterialCommand(mat));
        doc.Undo.Push(new AssignMaterialCommand(new[] { cube.Node.Id }, doc.Materials[0].Id));
        var (_, nodes) = FbxBinaryReader.Read(FbxBinaryWriter.Write(new FbxSceneBuilder(doc).Build(new[] { cube.Node })));
        var objects = nodes.First(n => n.Name == "Objects");
        var texByFile = objects.All("Texture").ToDictionary(t => t.Prop<long>(0), t => t.Child("FileName")!.Prop<string>(0));
        Assert.Equal(5, texByFile.Count); // base.png는 Color와 Occlusion이 공유
        long matId = objects.All("Material").Single().Prop<long>(0);
        var conns = nodes.First(n => n.Name == "Connections").All("C").Where(c => c.Prop<string>(0) == "OP" && c.Prop<long>(2) == matId)
            .ToDictionary(c => c.Prop<string>(3), c => texByFile[c.Prop<long>(1)]);
        Assert.Equal("C:/t/base.png", conns["DiffuseColor"]);
        Assert.Equal("C:/t/n.png", conns["NormalMap"]);
        Assert.Equal("C:/t/r.png", conns["ShininessExponent"]);
        Assert.Equal("C:/t/m.png", conns["ReflectionFactor"]);
        Assert.Equal("C:/t/e.png", conns["EmissiveColor"]);
        Assert.Equal("C:/t/base.png", conns["AmbientColor"]);
        var props = objects.All("Material").Single().Child("Properties70")!.All("P").Where(x => x.Prop<string>(0) == "EmissiveFactor").Single();
        Assert.Equal(3.0, props.Prop<double>(4), 3);
    }

    /// <summary>
    /// FBX 색 속성은 선형이다(Maya·Blender·Unity·ufbx가 선형으로 읽음): 문서 sRGB 색 0.5 → DiffuseColor ≈ 0.214, 발광 1 → 1.
    /// </summary>
    [Fact]
    public void SceneBuilder_MaterialColors_AreWrittenLinear()
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var mat = new MaterialDef { Name = "m", Type = MaterialType.Lambert, Color = new Vector3(0.5f, 1f, 0f) };
        mat.Set("emissive", new Vector3(1f, 0.5f, 0f));
        doc.Undo.Push(new AddMaterialCommand(mat));
        doc.Undo.Push(new AssignMaterialCommand(new[] { cube.Node.Id }, doc.Materials[0].Id));
        var (_, nodes) = FbxBinaryReader.Read(FbxBinaryWriter.Write(new FbxSceneBuilder(doc).Build(new[] { cube.Node })));
        var ps = nodes.First(n => n.Name == "Objects").All("Material").Single().Child("Properties70")!.All("P").ToList();
        var diffuse = ps.Single(x => x.Prop<string>(0) == "DiffuseColor");
        Assert.Equal(0.2140, diffuse.Prop<double>(4), 3);
        Assert.Equal(1.0, diffuse.Prop<double>(5), 6);
        Assert.Equal(0.0, diffuse.Prop<double>(6), 6);
        var emis = ps.Single(x => x.Prop<string>(0) == "EmissiveColor");
        Assert.Equal(1.0, emis.Prop<double>(4), 6);
        Assert.Equal(0.2140, emis.Prop<double>(5), 3);
    }

    /// <summary>
    /// 조인트 두 개에 스킨된 원기둥에서 메시만 골라 내보내도 스킨이 참조하는 조인트 체인이 자동 포함되어야 한다(Model 3, LimbNode 2).
    /// Skin 디포머 1개·Cluster 2개(Indexes/Weights 길이 일치, TransformLink 4x4), joint2의 TransformLink 이동 성분 100cm,
    /// BindPose 노드 수 3, Skeleton NodeAttribute 2개를 확인한다.
    /// </summary>
    [Fact]
    public void SceneBuilder_SkinnedMesh_WritesSkinClustersAndBindPose()
    {
        var doc = new Document();
        var cyl = CreatePrimitiveCommand.Cylinder(doc); doc.Undo.Push(cyl);
        var j0 = new SceneNode { Name = "joint1", Shape = new JointShape(), Local = new Transform3(new Vector3(0, -1, 0), Vector3.Zero, Vector3.One) };
        var j1 = new SceneNode { Name = "joint2", Shape = new JointShape(), Local = new Transform3(new Vector3(0, 2, 0), Vector3.Zero, Vector3.One) };
        doc.AddNode(j0, doc.Root); doc.AddNode(j1, j0);
        // 준비: 위쪽 정점은 joint2에 100%, 아래쪽은 joint1 0.75 + joint2 0.25로 묶은 스킨을 직접 구성한다.
        var mesh = cyl.Node.Mesh!;
        var skin = new SkinCluster { MeshBindWorld = cyl.Node.WorldMatrix };
        foreach (var j in new[] { j0, j1 }) { skin.Joints.Add(j.Id); Matrix4x4.Invert(j.WorldMatrix, out var inv); skin.BindInverse.Add(inv); }
        skin.EnsureSize(mesh.VertexCount);
        for (int v = 0; v < mesh.VertexCount; v++) skin.Weights[v] = mesh.Verts[v].Position.Y > 0 ? new List<(int, float)> { (1, 1f) } : new List<(int, float)> { (0, 0.75f), (1, 0.25f) };
        cyl.Node.MeshShape!.Skin = skin;

        // 메시만 선택해도 스킨이 참조하는 조인트 체인이 함께 나간다
        var builder = new FbxSceneBuilder(doc);
        var top = builder.Build(new[] { cyl.Node });
        var (_, nodes) = FbxBinaryReader.Read(FbxBinaryWriter.Write(top));
        var objects = nodes.First(n => n.Name == "Objects");
        var models = objects.All("Model").ToList();
        Assert.Equal(3, models.Count);
        Assert.Equal(2, models.Count(m => m.Prop<string>(2) == "LimbNode"));
        var deformers = objects.All("Deformer").ToList();
        Assert.Single(deformers.Where(d => d.Prop<string>(2) == "Skin"));
        var clusters = deformers.Where(d => d.Prop<string>(2) == "Cluster").ToList();
        Assert.Equal(2, clusters.Count);
        foreach (var c in clusters)
        {
            Assert.Equal(c.Child("Indexes")!.Prop<int[]>(0).Length, c.Child("Weights")!.Prop<double[]>(0).Length);
            Assert.Equal(16, c.Child("TransformLink")!.Prop<double[]>(0).Length);
        }
        // joint2 월드 y = 1 m → TransformLink 이동 성분 100 cm
        var c2 = clusters.First(c => FbxNode.ReadableId(c.Prop<string>(1)).EndsWith("joint2"));
        Assert.Equal(100.0, c2.Child("TransformLink")!.Prop<double[]>(0)[13], 3);
        var pose = objects.All("Pose").Single();
        Assert.Equal(3, pose.Child("NbPoseNodes")!.Prop<int>(0));
        Assert.Equal(2, objects.All("NodeAttribute").Count(a => a.Child("TypeFlags")!.Prop<string>(0) == "Skeleton"));
    }
}
