using System.Numerics;
using Cube.App.IO;
using Cube.Core.Commands;
using Cube.Core.Rig;
using Cube.Core.Scene;
using Godot;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace Cube.App;

/// <summary>
/// 헤드리스 전체 왕복 스모크(<c>SmokeExport.tscn -- --full</c>): 회전·비균등 스케일·피벗이 있는 부모 메시와 자식 메시, 스킨된 원기둥과
/// 조인트 체인·애니메이션, 스팟 라이트, 텍스처 머티리얼로 된 문서를 .glb/.gltf/.fbx/.obj로 내보내고 다시 가져와
/// 메시 노드별 월드 정점 범위(AABB)·라이트·텍스처·클립 수·애니메이션 포즈(조인트 월드 위치)·스킨 변형 결과가 같은지 확인한다.
/// 선택 내보내기에서 부모와 자식을 함께 골라도 자식이 두 번 나가지 않는지도 본다.
/// </summary>
public static class SmokeRoundTrip
{
    /// <summary>검사 실패 수(0이면 성공).</summary>
    private static int _fails;

    /// <summary>실패를 기록하고 로그를 남긴다.</summary>
    private static void Fail(string msg) { _fails++; GD.PrintErr("[SmokeFull] FAIL " + msg); }

    /// <summary>전체 왕복을 돌려 실패 수를 돌려준다. <paramref name="dir"/>에 결과 파일을 쓴다.</summary>
    public static int Run(string dir)
    {
        _fails = 0;
        System.IO.Directory.CreateDirectory(dir);
        var doc = Build(dir);
        var settings = new Settings();
        foreach (var ext in new[] { ".glb", ".gltf", ".fbx", ".obj" })
        {
            string path = System.IO.Path.Combine(dir, "full" + ext);
            var files = new FileActions(doc, settings, new Node(), s => GD.Print("[SmokeFull] " + s));
            var ex = files.Export(path, selectionOnly: false);
            if (!ex.Ok) { Fail($"{ext} export: {ex.Message}"); continue; }
            var doc2 = new Document();
            var im = new FileActions(doc2, settings, new Node(), s => GD.Print("[SmokeFull] " + s)).Import(path);
            if (!im.Ok) { Fail($"{ext} import: {im.Message}"); continue; }
            Compare(doc, doc2, ext);
        }
        SelectionExport(doc, dir, settings);
        GD.Print($"[SmokeFull] done, {_fails} failure(s)");
        return _fails;
    }

    /// <summary>검사용 문서를 만든다.</summary>
    private static Document Build(string dir)
    {
        var doc = new Document();
        // 텍스처 머티리얼(PNG를 직접 만들어 씀)
        string png = System.IO.Path.Combine(dir, "full_tex.png");
        var img = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8); img.Fill(new Color(0.8f, 0.3f, 0.1f)); img.SavePng(png);
        var mat = new MaterialDef { Name = "texMat", Type = MaterialType.Pbr };
        mat.SetTex("color", png); mat.Color = Vector3.One;
        doc.Undo.Push(new AddMaterialCommand(mat));
        // 부모: 회전·비균등 스케일·피벗
        var a = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(a);
        a.Node.Name = "parentBox";
        a.Node.Local = new Transform3(new Vector3(1, 2, 3), new Vector3(10, 20, 30), new Vector3(1, 2, 0.5f), new Vector3(0.2f, 0.1f, -0.3f));
        doc.Undo.Push(new AssignMaterialCommand(new[] { a.Node.Id }, mat.Id));
        // 자식
        var b = CreatePrimitiveCommand.Sphere(doc); doc.Undo.Push(b);
        b.Node.Name = "childBall";
        doc.Reparent(b.Node, a.Node);
        b.Node.Local = new Transform3(new Vector3(0, 1, 0), new Vector3(0, 45, 0), new Vector3(1.5f, 1.5f, 1.5f));
        // 조인트 체인 + 스킨된 원기둥(높이 2, 원점 y=1)
        var j1 = new SceneNode { Name = "jRoot", Shape = new JointShape(), Local = new Transform3(new Vector3(-3, 0, 0), Vector3.Zero, Vector3.One) };
        var j2 = new SceneNode { Name = "jMid", Shape = new JointShape(), Local = new Transform3(new Vector3(0, 1, 0), Vector3.Zero, Vector3.One) };
        var j3 = new SceneNode { Name = "jTip", Shape = new JointShape(), Local = new Transform3(new Vector3(0, 1, 0), Vector3.Zero, Vector3.One) };
        doc.AddNode(j1); doc.AddNode(j2, j1); doc.AddNode(j3, j2);
        var c = CreatePrimitiveCommand.Cylinder(doc, 0.3f, 2f, 12); doc.Undo.Push(c);
        c.Node.Name = "skinTube";
        c.Node.Local = new Transform3(new Vector3(-3, 1, 0), Vector3.Zero, Vector3.One);
        var joints = new[] { j1, j2, j3 };
        var infos = joints.Select(j => new JointInfo(j.Id, j.WorldMatrix, j.Children.Where(k => k.IsJoint).Select(k => k.WorldMatrix.Translation).ToList())).ToList();
        doc.Undo.Push(new SetSkinCommand("Bind", c.Node.Id, SkinOps.SmoothBind(c.Node.Mesh!, c.Node.WorldMatrix, infos)));
        // 애니메이션: jMid를 Z축으로 0→60° 회전
        var clip = new AnimationClip { Name = "bend", Length = 1, FrameRate = 30 };
        var tr = new NodeTrack { Node = j2.Id, NodeName = "jMid" };
        tr.Rotation.Add(new AnimKey<Quaternion>(0, Quaternion.Identity));
        tr.Rotation.Add(new AnimKey<Quaternion>(1, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3)));
        clip.Tracks.Add(tr);
        // 일반 노드 트랙: 피벗이 있는 부모 아래 자식 구가 Y축으로 돌며 위로 이동(노드 경로 트랙 + 피벗/계층 보정 검사)
        var tb = new NodeTrack { Node = b.Node.Id, NodeName = "childBall" };
        tb.Rotation.Add(new AnimKey<Quaternion>(0, b.Node.Local.Rotation));
        tb.Rotation.Add(new AnimKey<Quaternion>(1, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.75f)));
        tb.Position.Add(new AnimKey<Vector3>(0, new Vector3(0, 1, 0)));
        tb.Position.Add(new AnimKey<Vector3>(1, new Vector3(0.5f, 2, 0)));
        clip.Tracks.Add(tb);
        doc.Undo.Push(new SetAnimationsCommand("anim", new[] { clip }));
        // 스팟 라이트
        var l = new SceneNode { Name = "spotA", Shape = new LightShape { Type = LightType.Spot, Intensity = 2, SpotAngle = 30 }, Local = new Transform3(new Vector3(0, 3, 0), new Vector3(-90, 0, 0), Vector3.One) };
        doc.AddNode(l);
        return doc;
    }

    /// <summary>노드의 월드 정점 범위(스킨 변형 위치를 주면 그것을 쓴다).</summary>
    private static (Vector3 min, Vector3 max) WorldBounds(SceneNode n, Vector3[]? local = null)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        var m = n.Mesh!; var w = n.WorldMatrix;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            var p = Vector3.Transform(local != null ? local[v] : m.Verts[v].Position, w);
            mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
        }
        return (mn, mx);
    }

    /// <summary>스킨이 있는 메시 노드들(같은 이름 접두로 나뉜 경우 포함)의 현재 포즈 변형 월드 범위를 합친다.</summary>
    private static (Vector3 min, Vector3 max) DeformedBounds(Document doc, IEnumerable<SceneNode> nodes)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var n in nodes)
        {
            var b = n.Skin is { } skin ? Deformed(doc, n, skin) : WorldBounds(n);
            mn = Vector3.Min(mn, b.min); mx = Vector3.Max(mx, b.max);
        }
        return (mn, mx);
    }

    /// <summary>LBS로 현재 포즈 변형 위치의 월드 범위.</summary>
    private static (Vector3 min, Vector3 max) Deformed(Document doc, SceneNode n, SkinCluster skin)
    {
        var outp = new Vector3[n.Mesh!.VertexCount];
        Matrix4x4.Invert(n.WorldMatrix, out var inv);
        SkinOps.Deform(n.Mesh, skin, id => doc.Find(id)?.WorldMatrix, inv, outp);
        return WorldBounds(n, outp);
    }

    /// <summary>두 범위가 tol 안에서 같은지.</summary>
    private static bool Same((Vector3 min, Vector3 max) a, (Vector3 min, Vector3 max) b, float tol)
        => Vector3.Distance(a.min, b.min) <= tol && Vector3.Distance(a.max, b.max) <= tol;

    /// <summary>이름이 원본 이름을 포함하는 메시 노드들(가져오기가 머티리얼별로 나누거나 접미를 붙여도 찾도록).</summary>
    private static List<SceneNode> MeshesNamed(Document d, string name) => d.MeshNodes().Where(n => n.Name.Contains(name)).ToList();

    /// <summary>원본과 가져온 문서를 비교한다.</summary>
    private static void Compare(Document src, Document dst, string ext)
    {
        float tol = ext == ".fbx" ? 2e-3f : 1e-3f;
        bool obj = ext == ".obj";
        foreach (var name in new[] { "parentBox", "childBall", "skinTube" })
        {
            var s = MeshesNamed(src, name);
            var d = MeshesNamed(dst, name);
            if (d.Count == 0) { Fail($"{ext}: mesh {name} missing (have {string.Join(",", dst.MeshNodes().Select(n => n.Name))})"); continue; }
            var bs = DeformedBounds(src, s); var bd = DeformedBounds(dst, d);
            if (!Same(bs, bd, tol)) Fail($"{ext}: {name} rest bounds {bs} vs {bd}");
            else GD.Print($"[SmokeFull] {ext} {name} ok {bd.min}..{bd.max}");
        }
        if (obj) return; // OBJ: 메시만(월드 베이크)
        // 라이트
        var lights = dst.LightNodes().ToList();
        if (lights.Count != 1 || lights[0].Light!.Type != LightType.Spot) Fail($"{ext}: lights {lights.Count} {string.Join(",", lights.Select(l => l.Light!.Type))}");
        else
        {
            var dir = Vector3.TransformNormal(-Vector3.UnitZ, lights[0].WorldMatrix);
            if (Vector3.Distance(lights[0].WorldMatrix.Translation, new Vector3(0, 3, 0)) > tol || Vector3.Distance(Vector3.Normalize(dir), -Vector3.UnitY) > 1e-2f) Fail($"{ext}: light transform {lights[0].Local}");
            var ld = lights[0].Light!;
            if (MathF.Abs(ld.Intensity - 2f) > 1e-2f || MathF.Abs(ld.SpotAngle - 30f) > 0.5f) Fail($"{ext}: light intensity {ld.Intensity} angle {ld.SpotAngle}");
            else GD.Print($"[SmokeFull] {ext} light ok");
        }
        // 텍스처 머티리얼
        var box = MeshesNamed(dst, "parentBox").FirstOrDefault();
        var m = box != null ? dst.FindMaterial(box.MaterialId) : null;
        if (m?.Tex("color") == null) Fail($"{ext}: parentBox material texture missing (material {m?.Name})");
        // 조인트·스킨·애니메이션
        if (dst.JointNodes().Count() != 3) Fail($"{ext}: joints {dst.JointNodes().Count()}");
        if (!MeshesNamed(dst, "skinTube").Any(n => n.Skin != null)) Fail($"{ext}: skinTube not skinned");
        if (dst.Animations.Count != 1) { Fail($"{ext}: clips {dst.Animations.Count}"); return; }
        var srcClip = src.Animations[0]; var dstClip = dst.Animations[0];
        foreach (float t in new[] { 0.5f, 1f })
        {
            AnimationPose.Apply(src, srcClip, t); AnimationPose.Apply(dst, dstClip, t);
            var sj = src.JointNodes().First(n => n.Name == "jTip").WorldMatrix.Translation;
            var dj = dst.JointNodes().FirstOrDefault(n => n.Name.Contains("jTip"))?.WorldMatrix.Translation;
            if (dj == null || Vector3.Distance(sj, dj.Value) > tol) Fail($"{ext}: jTip at t={t} {sj} vs {dj}");
            var bs = DeformedBounds(src, MeshesNamed(src, "skinTube")); var bd = DeformedBounds(dst, MeshesNamed(dst, "skinTube"));
            if (!Same(bs, bd, 5e-3f)) Fail($"{ext}: skinTube posed bounds t={t} {bs} vs {bd}");
            // 애니메이션된 일반 노드(자식 구)의 월드 정점 범위
            var cs = WorldBounds(MeshesNamed(src, "childBall")[0]); var cd = WorldBounds(MeshesNamed(dst, "childBall")[0]);
            if (!Same(cs, cd, 5e-3f)) Fail($"{ext}: childBall posed bounds t={t} {cs} vs {cd}");
            else GD.Print($"[SmokeFull] {ext} posed t={t} ok tip={dj}");
        }
        AnimationPose.Clear(src); AnimationPose.Clear(dst);
    }

    /// <summary>부모와 자식을 함께 선택해 내보내도 자식 메시가 한 번만 나가야 한다.</summary>
    private static void SelectionExport(Document doc, string dir, Settings settings)
    {
        var a = doc.MeshNodes().First(n => n.Name == "parentBox"); var b = doc.MeshNodes().First(n => n.Name == "childBall");
        doc.Selection.SelectObjects(new[] { a.Id, b.Id });
        foreach (var ext in new[] { ".glb", ".fbx", ".obj" })
        {
            string path = System.IO.Path.Combine(dir, "sel" + ext);
            var ex = new FileActions(doc, settings, new Node(), s => GD.Print("[SmokeFull] " + s)).Export(path, selectionOnly: true);
            if (!ex.Ok) { Fail($"sel{ext} export {ex.Message}"); continue; }
            var d2 = new Document();
            var im = new FileActions(d2, settings, new Node(), s => GD.Print("[SmokeFull] " + s)).Import(path);
            if (!im.Ok) { Fail($"sel{ext} import {im.Message}"); continue; }
            int n = d2.MeshNodes().Count();
            if (n != 2) Fail($"sel{ext}: {n} meshes ({string.Join(",", d2.MeshNodes().Select(x => x.Name))}), expected 2");
            else if (!Same(WorldBounds(b), WorldBounds(MeshesNamed(d2, "childBall")[0]), ext == ".fbx" ? 2e-3f : 1e-3f)) Fail($"sel{ext}: childBall bounds");
            else GD.Print($"[SmokeFull] sel{ext} ok");
        }
    }
}
