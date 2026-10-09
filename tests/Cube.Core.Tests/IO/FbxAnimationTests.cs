using System.Numerics;
using Cube.Core.IO;
using Cube.Core.IO.Fbx;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

/// <summary>
/// FBX 애니메이션 내보내기(<c>FbxSceneBuilder.Animation</c>)와 애니메이션의 .cube 왕복을 검증한다.
/// 클립마다 AnimationStack + BaseLayer + AnimationCurveNode(T/R/S) + 축별 AnimationCurve가 올바르게 연결되고,
/// 시간은 KTime(초당 46186158000), 이동은 cm, 회전은 오일러 언랩, 피벗 노드는 베이크 공식으로 기록되어야 한다.
/// </summary>
public class FbxAnimationTests
{
    /// <summary>FBX KTime 단위로 1초에 해당하는 틱 수.</summary>
    private const long Tick = 46186158000L;

    /// <summary>축과 각도(도)로 쿼터니언을 만드는 도우미.</summary>
    private static Quaternion Deg(Vector3 axis, float deg) => Quaternion.CreateFromAxisAngle(axis, deg * MathF.PI / 180f);

    /// <summary>조인트 체인 3개 + 피벗이 있는 일반 노드, 클립 하나.</summary>
    private static (Document doc, SceneNode j0, SceneNode j1, SceneNode j2, SceneNode plain) MakeDoc()
    {
        var doc = new Document();
        var j0 = new SceneNode { Name = "hip", Shape = new JointShape(), Local = new Transform3(new Vector3(0, 1, 0), Vector3.Zero, Vector3.One) };
        var j1 = new SceneNode { Name = "knee", Shape = new JointShape(), Local = new Transform3(new Vector3(0, -0.5f, 0), Vector3.Zero, Vector3.One) };
        var j2 = new SceneNode { Name = "ankle", Shape = new JointShape(), Local = new Transform3(new Vector3(0, -0.5f, 0), Vector3.Zero, Vector3.One) };
        var plain = new SceneNode { Name = "prop", Local = new Transform3(Vector3.Zero, Vector3.Zero, Vector3.One, new Vector3(1, 0, 0)) };
        doc.AddNode(j0, doc.Root); doc.AddNode(j1, j0); doc.AddNode(j2, j1); doc.AddNode(plain, doc.Root);

        var clip = new AnimationClip { Name = "walk", FrameRate = 30f };
        var t0 = new NodeTrack { Node = j0.Id, NodeName = "hip" };
        t0.Position.Add(new(0f, new Vector3(0, 1, 0)));
        t0.Position.Add(new(0.5f, new Vector3(0.25f, 1.5f, -1)));
        t0.Scale.Add(new(0f, Vector3.One));
        t0.Scale.Add(new(1f, new Vector3(2, 2, 2)));
        var t1 = new NodeTrack { Node = j1.Id, NodeName = "knee" };
        t1.Rotation.Add(new(0f, Deg(Vector3.UnitZ, 170)));
        t1.Rotation.Add(new(0.5f, Deg(Vector3.UnitZ, 179)));
        t1.Rotation.Add(new(1f, Deg(Vector3.UnitZ, -179)));
        var tp = new NodeTrack { Node = plain.Id, NodeName = "prop" };
        tp.Position.Add(new(0f, new Vector3(0, 2, 0)));
        tp.Rotation.Add(new(0f, Deg(Vector3.UnitY, 90)));
        clip.Tracks.Add(t0); clip.Tracks.Add(t1); clip.Tracks.Add(tp);
        clip.UpdateLength();
        doc.Animations.Add(clip);
        return (doc, j0, j1, j2, plain);
    }

    /// <summary>
    /// 내보낸 FBX를 다시 읽은 결과와 조회 도우미 묶음: 최상위 노드, Objects 노드, (자식, 부모, 속성) 연결 목록.
    /// </summary>
    private sealed class Parsed
    {
        /// <summary>파일의 최상위 노드 목록(GlobalSettings, Definitions, Takes 등).</summary>
        public required List<FbxNode> Top;
        /// <summary>모든 오브젝트(Model/Geometry/Animation* 등)를 담은 Objects 노드.</summary>
        public required FbxNode Objects;
        /// <summary>Connections의 C 레코드를 (자식 ID, 부모 ID, OP 연결의 속성 이름 또는 null)로 풀어 둔 목록.</summary>
        public required List<(long child, long parent, string? prop)> Conns;
        /// <summary>첫 속성(오브젝트 ID)이 <paramref name="id"/>인 Objects 자식을 찾는다.</summary>
        public FbxNode Obj(long id) => Objects.Children.First(o => o.Props.Count > 0 && o.Props[0] is long l && l == id);
        /// <summary>"Model::이름" 식별자를 가진 Model 노드의 ID를 돌려준다.</summary>
        public long ModelId(string name) => Objects.All("Model").First(m => FbxNode.ReadableId(m.Prop<string>(1)) == "Model::" + name).Prop<long>(0);
        /// <summary>모델에 연결된 커브 노드(속성 이름 → 노드).</summary>
        public Dictionary<string, FbxNode> CurveNodes(string model)
        {
            long mid = ModelId(model);
            return Conns.Where(c => c.parent == mid && c.prop != null && c.prop.StartsWith("Lcl ")).ToDictionary(c => c.prop!, c => Obj(c.child));
        }
        /// <summary>커브 노드에 축 속성(<c>d|X</c>/<c>d|Y</c>/<c>d|Z</c>)으로 연결된 AnimationCurve 노드를 찾는다.</summary>
        public FbxNode Curve(FbxNode curveNode, string axis)
        {
            long cn = curveNode.Prop<long>(0);
            return Obj(Conns.Single(c => c.parent == cn && c.prop == axis).child);
        }
        /// <summary>커브 노드의 지정 축 커브에서 키 값 배열(KeyValueFloat)을 꺼낸다.</summary>
        public float[] Values(FbxNode curveNode, string axis) => Curve(curveNode, axis).Child("KeyValueFloat")!.Prop<float[]>(0);
    }

    /// <summary>
    /// 지정 루트들을 FbxSceneBuilder로 빌드해 바이너리로 쓰고 다시 읽어 <see cref="Parsed"/>로 돌려준다(실제 파일 왕복과 같은 경로).
    /// </summary>
    /// <param name="opt">내보내기 옵션(null이면 기본값).</param>
    private static Parsed BuildAndRead(Document doc, IReadOnlyList<SceneNode> roots, FbxExportOptions? opt = null)
    {
        var top = new FbxSceneBuilder(doc, opt).Build(roots);
        var (_, nodes) = FbxBinaryReader.Read(FbxBinaryWriter.Write(top));
        var conns = nodes.First(n => n.Name == "Connections").All("C")
            .Select(c => (c.Prop<long>(1), c.Prop<long>(2), c.Props.Count > 3 ? c.Prop<string>(3) : null)).ToList();
        return new Parsed { Top = nodes, Objects = nodes.First(n => n.Name == "Objects"), Conns = conns };
    }

    /// <summary>
    /// 클립 "walk"를 내보내 구조 전체를 검증한다: AnimStack 시작/끝 시간(1초), BaseLayer ↔ 스택 연결,
    /// hip의 T/S 커브 노드(회전 키가 없으면 R 노드 없음)와 키 시간·cm 값·키 속성 플래그·Default 값,
    /// knee 회전 170 → 179 → -179가 181로 언랩되는지, 키 없는 ankle엔 커브 노드가 없는지,
    /// Takes·GlobalSettings(30fps → TimeMode 6)·Definitions 개수(커브 = 커브 노드 × 3)까지 확인한다.
    /// </summary>
    [Fact]
    public void Export_WritesStackLayerCurveNodesAndCurves()
    {
        var (doc, j0, _, _, plain) = MakeDoc();
        var p = BuildAndRead(doc, new[] { j0, plain });

        var stack = p.Objects.All("AnimationStack").Single();
        Assert.Equal("AnimStack::walk", FbxNode.ReadableId(stack.Prop<string>(1)));
        var sp = stack.Child("Properties70")!.All("P").ToDictionary(x => x.Prop<string>(0), x => x);
        Assert.Equal(0L, sp["LocalStart"].Prop<long>(4));
        Assert.Equal(Tick, sp["LocalStop"].Prop<long>(4));
        Assert.Equal(Tick, sp["ReferenceStop"].Prop<long>(4));
        var layer = p.Objects.All("AnimationLayer").Single();
        Assert.Equal("AnimLayer::BaseLayer", FbxNode.ReadableId(layer.Prop<string>(1)));
        Assert.Contains((layer.Prop<long>(0), stack.Prop<long>(0), (string?)null), p.Conns);

        // hip: T + S (회전 키 없음)
        var hip = p.CurveNodes("hip");
        Assert.Equal(new[] { "Lcl Scaling", "Lcl Translation" }, hip.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("AnimCurveNode::T", FbxNode.ReadableId(hip["Lcl Translation"].Prop<string>(1)));
        foreach (var cn in hip.Values) Assert.Contains((cn.Prop<long>(0), layer.Prop<long>(0), (string?)null), p.Conns);
        var ty = p.Curve(hip["Lcl Translation"], "d|Y");
        Assert.Equal(new[] { 0L, Tick / 2 }, ty.Child("KeyTime")!.Prop<long[]>(0));
        var tyv = ty.Child("KeyValueFloat")!.Prop<float[]>(0);
        Assert.Equal(100f, tyv[0], 3); Assert.Equal(150f, tyv[1], 3);
        Assert.Equal(-100f, p.Values(hip["Lcl Translation"], "d|Z")[1], 3);
        Assert.Equal(25f, p.Values(hip["Lcl Translation"], "d|X")[1], 3);
        Assert.Equal(4009, ty.Child("KeyVer")!.Prop<int>(0));
        Assert.Equal(new[] { 24836 }, ty.Child("KeyAttrFlags")!.Prop<int[]>(0));
        Assert.Equal(new[] { 2 }, ty.Child("KeyAttrRefCount")!.Prop<int[]>(0));
        Assert.Equal(255790911, BitConverter.SingleToInt32Bits(ty.Child("KeyAttrDataFloat")!.Prop<float[]>(0)[2]));
        Assert.Equal(100.0, ty.Child("Default")!.Prop<double>(0), 3);
        var sx = p.Curve(hip["Lcl Scaling"], "d|X");
        Assert.Equal(new[] { 0L, Tick }, sx.Child("KeyTime")!.Prop<long[]>(0));
        Assert.Equal(new[] { 1f, 2f }, sx.Child("KeyValueFloat")!.Prop<float[]>(0));

        // knee: R만, 170 → 179 → -179가 181로 언랩
        var knee = p.CurveNodes("knee");
        Assert.Equal(new[] { "Lcl Rotation" }, knee.Keys);
        Assert.Equal("AnimCurveNode::R", FbxNode.ReadableId(knee["Lcl Rotation"].Prop<string>(1)));
        var rz = p.Values(knee["Lcl Rotation"], "d|Z");
        Assert.Equal(170f, rz[0], 2); Assert.Equal(179f, rz[1], 2); Assert.Equal(181f, rz[2], 2);
        Assert.Equal(new[] { 0L, Tick / 2, Tick }, p.Curve(knee["Lcl Rotation"], "d|Z").Child("KeyTime")!.Prop<long[]>(0));

        // ankle: 키 없음 → 커브 노드 없음
        Assert.Empty(p.CurveNodes("ankle"));

        // Takes
        var takes = p.Top.First(n => n.Name == "Takes");
        Assert.Equal("walk", takes.Child("Current")!.Prop<string>(0));
        var take = takes.All("Take").Single();
        Assert.Equal("walk", take.Prop<string>(0));
        Assert.Equal("walk.tak", take.Child("FileName")!.Prop<string>(0));
        Assert.Equal(Tick, take.Child("LocalTime")!.Prop<long>(1));
        Assert.Equal(Tick, take.Child("ReferenceTime")!.Prop<long>(1));

        // GlobalSettings: 30fps → TimeMode 6, TimeSpanStop = 1초
        var gs = p.Top.First(n => n.Name == "GlobalSettings").Child("Properties70")!.All("P").ToDictionary(x => x.Prop<string>(0), x => x);
        Assert.Equal(6, gs["TimeMode"].Prop<int>(4));
        Assert.Equal(Tick, gs["TimeSpanStop"].Prop<long>(4));

        // Definitions에 애니메이션 타입 수
        var defs = p.Top.First(n => n.Name == "Definitions").All("ObjectType").ToDictionary(o => o.Prop<string>(0), o => o.Child("Count")!.Prop<int>(0));
        Assert.Equal(1, defs["AnimationStack"]);
        Assert.Equal(1, defs["AnimationLayer"]);
        Assert.Equal(p.Objects.All("AnimationCurveNode").Count(), defs["AnimationCurveNode"]);
        Assert.Equal(p.Objects.All("AnimationCurve").Count(), defs["AnimationCurve"]);
        Assert.Equal(3 * defs["AnimationCurveNode"], defs["AnimationCurve"]);
    }

    /// <summary>
    /// 피벗이 있는 노드의 이동 커브: 기본(BakePivots)은 T = P·R + p 공식으로 베이크되어 (0,200,-100)cm가 되어야 하고,
    /// 베이크하지 않으면 Rotation/ScalingPivot 모델에 맞춘 T = p + P·R − P가 기록되어야 한다.
    /// </summary>
    /// <summary>
    /// Y 45° → 135° 회전 키 둘: 오일러 표현이 (0,45,0) → (180,45,180)으로 바뀌어 축별 선형 보간이 slerp와 다르므로
    /// 중간 키가 추가되고, 키 사이 어디서든 오일러 선형 보간이 slerp와 1° 이내여야 한다(FBX 가져오기에서 중간 포즈가 어긋나던 문제).
    /// </summary>
    [Fact]
    public void Export_RotationKeys_RefinedWhereEulerPathDiverges()
    {
        var doc = new Document();
        var n = new SceneNode { Name = "spin", Local = new Transform3(Vector3.Zero, new Vector3(0, 45, 0), Vector3.One) };
        doc.AddNode(n, doc.Root);
        var clip = new AnimationClip { Name = "spin", FrameRate = 30f };
        var tr = new NodeTrack { Node = n.Id, NodeName = "spin" };
        tr.Rotation.Add(new(0f, Deg(Vector3.UnitY, 45))); tr.Rotation.Add(new(1f, Deg(Vector3.UnitY, 135)));
        clip.Tracks.Add(tr); clip.UpdateLength(); doc.Animations.Add(clip);
        var p = BuildAndRead(doc, new[] { n });
        var r = p.CurveNodes("spin")["Lcl Rotation"];
        var times = p.Curve(r, "d|X").Child("KeyTime")!.Prop<long[]>(0);
        Assert.True(times.Length > 2);
        var x = p.Values(r, "d|X"); var y = p.Values(r, "d|Y"); var z = p.Values(r, "d|Z");
        for (int i = 0; i + 1 < times.Length; i++)
            for (float a = 0.25f; a < 1f; a += 0.25f)
            {
                float t = (times[i] + (times[i + 1] - times[i]) * a) / (float)Tick;
                var e = new Vector3(x[i] + (x[i + 1] - x[i]) * a, y[i] + (y[i + 1] - y[i]) * a, z[i] + (z[i + 1] - z[i]) * a);
                var q = new Transform3(Vector3.Zero, e, Vector3.One).Rotation;
                var want = NodeTrack.Sample(tr.Rotation, t, Quaternion.Identity);
                float ang = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(q, want)))) * 180f / MathF.PI;
                Assert.True(ang < 1f, $"t={t} angle {ang}");
            }
    }

    [Fact]
    public void Export_PivotNode_BakesTranslationFormula()
    {
        var (doc, j0, _, _, plain) = MakeDoc();
        var p = BuildAndRead(doc, new[] { j0, plain });
        var prop = p.CurveNodes("prop");
        // 피벗 P=(1,0,0), R = Y 90°, p = (0,2,0) → T = P·R + p = (0,0,-1) + (0,2,0)
        var t = prop["Lcl Translation"];
        Assert.Equal(0f, p.Values(t, "d|X")[0], 3);
        Assert.Equal(200f, p.Values(t, "d|Y")[0], 3);
        Assert.Equal(-100f, p.Values(t, "d|Z")[0], 3);
        Assert.Equal(90f, p.Values(prop["Lcl Rotation"], "d|Y")[0], 3);

        // 피벗을 베이크하지 않으면 Rotation/ScalingPivot 모델에 맞춘 T = p + P·R − P
        var p2 = BuildAndRead(doc, new[] { j0, plain }, FbxExportOptions.Default with { BakePivots = false });
        var t2 = p2.CurveNodes("prop")["Lcl Translation"];
        Assert.Equal(-100f, p2.Values(t2, "d|X")[0], 3);
        Assert.Equal(-100f, p2.Values(t2, "d|Z")[0], 3);
    }

    /// <summary>
    /// 피벗 베이크로 부모 정점이 −P만큼 옮겨졌으므로, 그 자식의 이동 키에서는 부모 피벗만큼(−1m → −100cm) 빼야 월드 위치가 유지된다.
    /// </summary>
    [Fact]
    public void Export_ChildOfPivotedParent_SubtractsParentShift()
    {
        var doc = new Document();
        var parent = new SceneNode { Name = "parent", Local = new Transform3(Vector3.Zero, Vector3.Zero, Vector3.One, new Vector3(0, 1, 0)) };
        var child = new SceneNode { Name = "child" };
        doc.AddNode(parent, doc.Root); doc.AddNode(child, parent);
        var clip = new AnimationClip { Name = "a" };
        var tr = new NodeTrack { Node = child.Id };
        tr.Position.Add(new(0f, new Vector3(3, 0, 0)));
        clip.Tracks.Add(tr); clip.UpdateLength();
        doc.Animations.Add(clip);
        var p = BuildAndRead(doc, new[] { parent });
        var t = p.CurveNodes("child")["Lcl Translation"];
        Assert.Equal(300f, p.Values(t, "d|X")[0], 3);
        Assert.Equal(-100f, p.Values(t, "d|Y")[0], 3);
    }

    /// <summary>
    /// 부모 없이 자식만 내보내면 자식이 루트가 되므로 이동 키에 부모 월드 변환(Y +5m)이 베이크되어 (100, 500)cm가 되어야 한다.
    /// </summary>
    [Fact]
    public void Export_ChildExportedAlone_BakesParentWorld()
    {
        var doc = new Document();
        var parent = new SceneNode { Name = "parent", Local = new Transform3(new Vector3(0, 5, 0), Vector3.Zero, Vector3.One) };
        var child = new SceneNode { Name = "child" };
        doc.AddNode(parent, doc.Root); doc.AddNode(child, parent);
        var clip = new AnimationClip { Name = "a" };
        var tr = new NodeTrack { Node = child.Id };
        tr.Position.Add(new(0f, new Vector3(1, 0, 0)));
        clip.Tracks.Add(tr); clip.UpdateLength();
        doc.Animations.Add(clip);
        var p = BuildAndRead(doc, new[] { child });
        var t = p.CurveNodes("child")["Lcl Translation"];
        Assert.Equal(100f, p.Values(t, "d|X")[0], 3);
        Assert.Equal(500f, p.Values(t, "d|Y")[0], 3);
    }

    /// <summary>
    /// 내보내는 모델의 트랙만 커브가 된다(prop만 내보내면 T+R 커브 노드 2개).
    /// <c>ExportAnimations = false</c>면 AnimationStack과 Take가 전혀 기록되지 않아야 한다.
    /// </summary>
    [Fact]
    public void Export_OnlyExportedModelsGetCurves_AndOptionDisables()
    {
        var (doc, j0, _, _, plain) = MakeDoc();
        var p = BuildAndRead(doc, new[] { plain });
        Assert.Equal(2, p.Objects.All("AnimationCurveNode").Count()); // prop T + R만
        var off = BuildAndRead(doc, new[] { j0, plain }, FbxExportOptions.Default with { ExportAnimations = false });
        Assert.Empty(off.Objects.All("AnimationStack"));
        Assert.Empty(off.Top.First(n => n.Name == "Takes").All("Take"));
    }

    /// <summary>
    /// 오일러 언랩: 각 성분을 이전 키에 가장 가까운 ±360° 등가각으로 바꿔야 한다(-179 → 181, 350 → -10). 보간 시 반대로 한 바퀴 도는 것을 막는다.
    /// </summary>
    [Fact]
    public void UnwrapEuler_PicksClosestEquivalent()
    {
        var u = FbxSceneBuilder.UnwrapEuler(new Vector3(-179, 10, 350), new Vector3(179, 0, -5));
        Assert.Equal(181f, u.X, 3); Assert.Equal(10f, u.Y, 3); Assert.Equal(-10f, u.Z, 3);
    }

    /// <summary>
    /// 클립(이름·루프·프레임레이트·길이)과 트랙(노드 참조·이름·위치/회전/스케일 키)이 .cube 왕복 후 그대로인지 확인한다.
    /// 애니메이션이 없는 문서는 animations 필드를 쓰지 않고, 그런 파일을 불러오면 기존 클립이 지워져야 한다.
    /// </summary>
    [Fact]
    public void CubeFile_RoundTripsAnimations()
    {
        var (doc, _, _, _, _) = MakeDoc();
        doc.Animations[0].Loop = true;
        var json = CubeFileFormat.Serialize(doc);
        var doc2 = new Document();
        CubeFileFormat.Deserialize(doc2, json);
        var c = Assert.Single(doc2.Animations);
        var src = doc.Animations[0];
        Assert.Equal("walk", c.Name); Assert.True(c.Loop); Assert.Equal(30f, c.FrameRate); Assert.Equal(src.Length, c.Length);
        Assert.Equal(3, c.Tracks.Count);
        var knee = c.Tracks[1];
        Assert.Equal("knee", doc2.Find(knee.Node)!.Name);
        Assert.Equal("knee", knee.NodeName);
        Assert.Equal(src.Tracks[1].Rotation, knee.Rotation);
        Assert.Equal(src.Tracks[0].Position, c.Tracks[0].Position);
        Assert.Equal(src.Tracks[0].Scale, c.Tracks[0].Scale);
        Assert.Empty(knee.Position);

        // 애니메이션 필드가 없는 예전 파일도 열리고, 다시 불러오면 이전 클립은 지워진다
        var plain = new Document();
        plain.AddNode(new SceneNode { Name = "n" }, plain.Root);
        var plainJson = CubeFileFormat.Serialize(plain);
        Assert.DoesNotContain("animations", plainJson);
        CubeFileFormat.Deserialize(doc2, plainJson);
        Assert.Empty(doc2.Animations);
    }
}
