using Cube.App.IO;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Godot;

namespace Cube.App;

/// <summary>
/// 헤드리스 스모크 테스트: 큐브 생성 → 윗면 압출 → .glb 내보내기 → 다시 가져오기 → 정점/삼각형 수 비교 → 종료 코드.
/// 실행: godot --headless --path . res://scenes/tests/SmokeExport.tscn -- --out=C:/tmp/smoke.glb
/// </summary>
public partial class SmokeExportRunner : Node
{
    public override void _Ready()
    {
        string? outPath = null;
        foreach (var a in OS.GetCmdlineUserArgs()) if (a.StartsWith("--out=")) outPath = a["--out=".Length..];
        outPath ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cube_smoke.glb");
        int code = Run(outPath);
        GD.Print($"[Smoke] exit {code}");
        GetTree().Quit(code);
    }

    private static int Run(string outPath)
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var mesh = cube.Node.Mesh!;
        int top = -1;
        for (int f = 0; f < mesh.FaceCount; f++) if (mesh.Faces[f].Normal.Y > 0.9f) top = f;
        doc.Undo.Push(new ExtrudeFacesCommand(cube.Node.Id, new[] { top }));
        var verts = new List<int>(); mesh.GetFaceVertices(doc.Selection.GetComponents(cube.Node.Id).Faces.First(), verts);
        foreach (int v in verts) { var vt = mesh.Verts[v]; vt.Position += System.Numerics.Vector3.UnitY * 0.5f; mesh.Verts[v] = vt; }
        MeshNormals.Recompute(mesh);
        var sphere = CreatePrimitiveCommand.Sphere(doc); doc.Undo.Push(sphere);
        sphere.Node.Local = new Transform3(new System.Numerics.Vector3(2, 0, 0), new System.Numerics.Vector3(0, 45, 0), System.Numerics.Vector3.One);

        int srcVerts = mesh.AliveVertexCount + sphere.Node.Mesh!.AliveVertexCount;
        int srcFaces = mesh.AliveFaceCount + sphere.Node.Mesh!.AliveFaceCount;
        int srcTris = MeshTessellator.Build(mesh).TriangleCount + MeshTessellator.Build(sphere.Node.Mesh!).TriangleCount;

        var settings = new Settings();
        var files = new FileActions(doc, settings, new Node(), s => GD.Print("[Smoke] " + s));
        var ex = files.Export(outPath, selectionOnly: false);
        if (!ex.Ok) { GD.PrintErr("[Smoke] export failed: " + ex.Message); return 1; }
        if (!System.IO.File.Exists(outPath)) { GD.PrintErr("[Smoke] file missing"); return 1; }
        GD.Print($"[Smoke] wrote {new System.IO.FileInfo(outPath).Length} bytes");

        var doc2 = new Document();
        var files2 = new FileActions(doc2, settings, new Node(), s => GD.Print("[Smoke] " + s));
        var im = files2.Import(outPath);
        if (!im.Ok) { GD.PrintErr("[Smoke] import failed: " + im.Message); return 1; }
        int dstVerts = 0, dstFaces = 0, dstTris = 0, meshes = 0;
        foreach (var n in doc2.MeshNodes())
        {
            meshes++;
            dstVerts += n.Mesh!.AliveVertexCount; dstFaces += n.Mesh.AliveFaceCount; dstTris += MeshTessellator.Build(n.Mesh).TriangleCount;
            var errors = MeshValidator.Check(n.Mesh);
            if (errors.Count > 0) { GD.PrintErr($"[Smoke] invalid mesh {n.Name}: {errors[0]}"); return 1; }
        }
        GD.Print($"[Smoke] src verts={srcVerts} faces={srcFaces} tris={srcTris}  dst meshes={meshes} verts={dstVerts} faces={dstFaces} tris={dstTris}");
        var sphereNode = doc2.MeshNodes().FirstOrDefault(n => n.Name.StartsWith("pSphere"));
        if (sphereNode == null) { GD.PrintErr("[Smoke] sphere node missing"); return 1; }
        var t = sphereNode.Local;
        GD.Print($"[Smoke] sphere transform {t}");
        if (meshes != 2 || dstTris != srcTris || dstVerts != srcVerts || dstFaces != srcFaces) { GD.PrintErr("[Smoke] round-trip mismatch"); return 1; }
        if (System.Numerics.Vector3.Distance(t.Translation, new System.Numerics.Vector3(2, 0, 0)) > 1e-3f || MathF.Abs(t.RotationDegrees.Y - 45) > 0.1f) { GD.PrintErr("[Smoke] transform mismatch"); return 1; }
        return RunFbx(doc, System.IO.Path.ChangeExtension(outPath, ".fbx"), settings, srcVerts, srcFaces, srcTris);
    }

    /// <summary>자체 FBX writer → Godot FbxDocument(ufbx) 재가져오기: 정점/면/삼각형 수와 구의 트랜스폼이 유지되어야 한다.</summary>
    private static int RunFbx(Document doc, string fbxPath, Settings settings, int srcVerts, int srcFaces, int srcTris)
    {
        var files = new FileActions(doc, settings, new Node(), s => GD.Print("[Smoke] " + s));
        var ex = files.Export(fbxPath, selectionOnly: false);
        if (!ex.Ok) { GD.PrintErr("[Smoke] fbx export failed: " + ex.Message); return 1; }
        GD.Print($"[Smoke] wrote {new System.IO.FileInfo(fbxPath).Length} bytes (fbx)");
        var doc2 = new Document();
        var files2 = new FileActions(doc2, settings, new Node(), s => GD.Print("[Smoke] " + s));
        var im = files2.Import(fbxPath);
        if (!im.Ok) { GD.PrintErr("[Smoke] fbx import failed: " + im.Message); return 1; }
        int dstVerts = 0, dstFaces = 0, dstTris = 0, meshes = 0;
        foreach (var n in doc2.MeshNodes())
        {
            meshes++;
            dstVerts += n.Mesh!.AliveVertexCount; dstFaces += n.Mesh.AliveFaceCount; dstTris += MeshTessellator.Build(n.Mesh).TriangleCount;
            var errors = MeshValidator.Check(n.Mesh);
            if (errors.Count > 0) { GD.PrintErr($"[Smoke] invalid fbx mesh {n.Name}: {errors[0]}"); return 1; }
        }
        GD.Print($"[Smoke] fbx src verts={srcVerts} faces={srcFaces} tris={srcTris}  dst meshes={meshes} verts={dstVerts} faces={dstFaces} tris={dstTris}");
        var sphereNode = doc2.MeshNodes().FirstOrDefault(n => n.Name.StartsWith("pSphere"));
        if (sphereNode == null) { GD.PrintErr("[Smoke] fbx sphere node missing"); return 1; }
        var t = sphereNode.Local;
        GD.Print($"[Smoke] fbx sphere transform {t}");
        if (meshes != 2 || dstTris != srcTris || dstVerts != srcVerts || dstFaces != srcFaces) { GD.PrintErr("[Smoke] fbx round-trip mismatch"); return 1; }
        if (System.Numerics.Vector3.Distance(t.Translation, new System.Numerics.Vector3(2, 0, 0)) > 1e-2f || MathF.Abs(t.RotationDegrees.Y - 45) > 0.1f) { GD.PrintErr("[Smoke] fbx transform mismatch"); return 1; }
        return 0;
    }
}
