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
/// <remarks>
/// tools/smoke-export.ps1이 헤드리스로 실행하고 종료 코드(0 = 성공, 1 = 실패)로 판정한다.
/// glTF 왕복 다음에 같은 문서로 자체 FBX writer 왕복(<see cref="RunFbx"/>)도 검사한다. 셸·뷰포트 없이 Core 문서와 FileActions만 쓴다.
/// </remarks>
public partial class SmokeExportRunner : Node
{
    /// <summary>--out= 인자(없으면 임시 폴더의 cube_smoke.glb)로 테스트를 돌리고 결과 코드로 앱을 종료한다.</summary>
    public override void _Ready()
    {
        string? outPath = null;
        foreach (var a in OS.GetCmdlineUserArgs()) if (a.StartsWith("--out=")) outPath = a["--out=".Length..];
        outPath ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cube_smoke.glb");
        int code = Run(outPath);
        GD.Print($"[Smoke] exit {code}");
        GetTree().Quit(code);
    }

    /// <summary>
    /// 테스트 본문. ① 큐브 생성 후 윗면 Extrude, 새 캡을 Y+0.5 이동 ② 구를 (2,0,0)·Y 45°에 배치 ③ 원본 정점/면/삼각형 수 기록 →
    /// ④ glb 내보내기 → ⑤ 새 문서로 가져오기 → 메시 수·정점·면·삼각형 수 일치, MeshValidator 통과, 구 트랜스폼 유지 확인 → ⑥ FBX 왕복.
    /// </summary>
    /// <returns>0 = 성공, 1 = 실패.</returns>
    private static int Run(string outPath)
    {
        // ① 큐브를 만들고 법선이 +Y인 윗면을 찾는다
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var mesh = cube.Node.Mesh!;
        int top = -1;
        for (int f = 0; f < mesh.FaceCount; f++) if (mesh.Faces[f].Normal.Y > 0.9f) top = f;
        // 윗면 Extrude 후 선택된 새 캡 면의 정점을 위로 0.5 올려 돌출 형태를 만든다
        doc.Undo.Push(new ExtrudeFacesCommand(cube.Node.Id, new[] { top }));
        var verts = new List<int>(); mesh.GetFaceVertices(doc.Selection.GetComponents(cube.Node.Id).Faces.First(), verts);
        foreach (int v in verts) { var vt = mesh.Verts[v]; vt.Position += System.Numerics.Vector3.UnitY * 0.5f; mesh.Verts[v] = vt; }
        MeshNormals.Recompute(mesh);
        // ② 두 번째 오브젝트: 이동·회전이 있는 구(트랜스폼 왕복 검사용)
        var sphere = CreatePrimitiveCommand.Sphere(doc); doc.Undo.Push(sphere);
        sphere.Node.Local = new Transform3(new System.Numerics.Vector3(2, 0, 0), new System.Numerics.Vector3(0, 45, 0), System.Numerics.Vector3.One);

        // ③ 비교 기준 수치
        int srcVerts = mesh.AliveVertexCount + sphere.Node.Mesh!.AliveVertexCount;
        int srcFaces = mesh.AliveFaceCount + sphere.Node.Mesh!.AliveFaceCount;
        int srcTris = MeshTessellator.Build(mesh).TriangleCount + MeshTessellator.Build(sphere.Node.Mesh!).TriangleCount;

        // ④ 문서 전체를 glb로 내보내기(설정은 저장하지 않는 임시 객체, 다이얼로그 소유 노드도 임시)
        var settings = new Settings();
        var files = new FileActions(doc, settings, new Node(), s => GD.Print("[Smoke] " + s));
        var ex = files.Export(outPath, selectionOnly: false);
        if (!ex.Ok) { GD.PrintErr("[Smoke] export failed: " + ex.Message); return 1; }
        if (!System.IO.File.Exists(outPath)) { GD.PrintErr("[Smoke] file missing"); return 1; }
        GD.Print($"[Smoke] wrote {new System.IO.FileInfo(outPath).Length} bytes");

        // ⑤ 새 문서로 다시 가져오기
        var doc2 = new Document();
        var files2 = new FileActions(doc2, settings, new Node(), s => GD.Print("[Smoke] " + s));
        var im = files2.Import(outPath);
        if (!im.Ok) { GD.PrintErr("[Smoke] import failed: " + im.Message); return 1; }
        int dstVerts = 0, dstFaces = 0, dstTris = 0, meshes = 0;
        // 가져온 메시마다 수치를 합산하고 위상 유효성 검사
        foreach (var n in doc2.MeshNodes())
        {
            meshes++;
            dstVerts += n.Mesh!.AliveVertexCount; dstFaces += n.Mesh.AliveFaceCount; dstTris += MeshTessellator.Build(n.Mesh).TriangleCount;
            var errors = MeshValidator.Check(n.Mesh);
            if (errors.Count > 0) { GD.PrintErr($"[Smoke] invalid mesh {n.Name}: {errors[0]}"); return 1; }
        }
        GD.Print($"[Smoke] src verts={srcVerts} faces={srcFaces} tris={srcTris}  dst meshes={meshes} verts={dstVerts} faces={dstFaces} tris={dstTris}");
        // 구 노드(pSphere*)의 트랜스폼이 보존됐는지
        var sphereNode = doc2.MeshNodes().FirstOrDefault(n => n.Name.StartsWith("pSphere"));
        if (sphereNode == null) { GD.PrintErr("[Smoke] sphere node missing"); return 1; }
        var t = sphereNode.Local;
        GD.Print($"[Smoke] sphere transform {t}");
        // 수치·트랜스폼 비교(glTF는 m 단위라 위치 허용 오차 1e-3)
        if (meshes != 2 || dstTris != srcTris || dstVerts != srcVerts || dstFaces != srcFaces) { GD.PrintErr("[Smoke] round-trip mismatch"); return 1; }
        if (System.Numerics.Vector3.Distance(t.Translation, new System.Numerics.Vector3(2, 0, 0)) > 1e-3f || MathF.Abs(t.RotationDegrees.Y - 45) > 0.1f) { GD.PrintErr("[Smoke] transform mismatch"); return 1; }
        // ⑥ 같은 원본 문서로 FBX 왕복
        return RunFbx(doc, System.IO.Path.ChangeExtension(outPath, ".fbx"), settings, srcVerts, srcFaces, srcTris);
    }

    /// <summary>자체 FBX writer → Godot FbxDocument(ufbx) 재가져오기: 정점/면/삼각형 수와 구의 트랜스폼이 유지되어야 한다.</summary>
    private static int RunFbx(Document doc, string fbxPath, Settings settings, int srcVerts, int srcFaces, int srcTris)
    {
        // 원본 문서를 .fbx로 내보내기
        var files = new FileActions(doc, settings, new Node(), s => GD.Print("[Smoke] " + s));
        var ex = files.Export(fbxPath, selectionOnly: false);
        if (!ex.Ok) { GD.PrintErr("[Smoke] fbx export failed: " + ex.Message); return 1; }
        GD.Print($"[Smoke] wrote {new System.IO.FileInfo(fbxPath).Length} bytes (fbx)");
        // 새 문서로 가져오기(Godot FbxDocument)
        var doc2 = new Document();
        var files2 = new FileActions(doc2, settings, new Node(), s => GD.Print("[Smoke] " + s));
        var im = files2.Import(fbxPath);
        if (!im.Ok) { GD.PrintErr("[Smoke] fbx import failed: " + im.Message); return 1; }
        int dstVerts = 0, dstFaces = 0, dstTris = 0, meshes = 0;
        // 메시별 수치 합산과 유효성 검사
        foreach (var n in doc2.MeshNodes())
        {
            meshes++;
            dstVerts += n.Mesh!.AliveVertexCount; dstFaces += n.Mesh.AliveFaceCount; dstTris += MeshTessellator.Build(n.Mesh).TriangleCount;
            var errors = MeshValidator.Check(n.Mesh);
            if (errors.Count > 0) { GD.PrintErr($"[Smoke] invalid fbx mesh {n.Name}: {errors[0]}"); return 1; }
        }
        GD.Print($"[Smoke] fbx src verts={srcVerts} faces={srcFaces} tris={srcTris}  dst meshes={meshes} verts={dstVerts} faces={dstFaces} tris={dstTris}");
        // 구 트랜스폼 확인(FBX는 cm 베이크·재환산이라 위치 허용 오차 1e-2)
        var sphereNode = doc2.MeshNodes().FirstOrDefault(n => n.Name.StartsWith("pSphere"));
        if (sphereNode == null) { GD.PrintErr("[Smoke] fbx sphere node missing"); return 1; }
        var t = sphereNode.Local;
        GD.Print($"[Smoke] fbx sphere transform {t}");
        if (meshes != 2 || dstTris != srcTris || dstVerts != srcVerts || dstFaces != srcFaces) { GD.PrintErr("[Smoke] fbx round-trip mismatch"); return 1; }
        if (System.Numerics.Vector3.Distance(t.Translation, new System.Numerics.Vector3(2, 0, 0)) > 1e-2f || MathF.Abs(t.RotationDegrees.Y - 45) > 0.1f) { GD.PrintErr("[Smoke] fbx transform mismatch"); return 1; }
        return 0;
    }
}
