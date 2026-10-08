using Cube.Core.IO;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.IO;

/// <summary>Godot 내장 GltfDocument로 .glb/.gltf를 쓴다. 좌표계 변환은 Godot이 처리(둘 다 오른손 Y-up, m).</summary>
public sealed class GltfExporter : IExporter
{
    public string Name => "glTF 2.0";
    public IReadOnlyList<string> Extensions { get; } = new[] { ".glb", ".gltf" };

    public ExportResult Export(Document doc, IReadOnlyList<SceneNode> nodes, string path, ExportPreset preset)
    {
        using var restPose = Cube.Core.Scene.AnimationPose.RestScope(doc); // 재생 포즈가 아니라 rest(바인드) 포즈로 기록
        if (nodes.Count == 0) return ExportResult.Fail("Nothing to export.");
        string rootName = System.IO.Path.GetFileNameWithoutExtension(path);
        var usedMaterials = new List<MaterialDef>();
        var (root, count, tris) = DocumentToGodotScene.Build(nodes, SanitizeName(rootName), doc.Animations, usedMaterials);
        // 머티리얼 텍스처·PBR 확장은 문서 확장이 기록한다(GltfDocument 확장 등록은 전역이라 이 내보내기 동안만 등록)
        GltfMaterialExtension.Begin(usedMaterials);
        var matExt = new GltfMaterialExtension();
        GltfDocument.RegisterGltfDocumentExtension(matExt);
        try
        {
            var gltf = new GltfDocument();
            var state = new GltfState();
            var err = gltf.AppendFromScene(root, state);
            if (err != Error.Ok) return ExportResult.Fail($"AppendFromScene failed: {err}");
            err = gltf.WriteToFilesystem(state, path);
            if (err != Error.Ok) return ExportResult.Fail($"WriteToFilesystem failed: {err}");
            string warn = GltfMaterialExtension.Warnings.Count > 0 ? " (" + string.Join("; ", GltfMaterialExtension.Warnings.Distinct()) + ")" : "";
            return new ExportResult(true, $"Exported {count} node(s), {tris} triangles to {path}{warn}", count, tris);
        }
        catch (Exception ex) { return ExportResult.Fail(ex.Message); }
        finally { GltfDocument.UnregisterGltfDocumentExtension(matExt); GltfMaterialExtension.End(); root.Free(); }
    }

    private static string SanitizeName(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var r = new string(chars);
        return r.Length == 0 ? "Scene" : r;
    }
}
