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
        if (nodes.Count == 0) return ExportResult.Fail("Nothing to export.");
        string rootName = System.IO.Path.GetFileNameWithoutExtension(path);
        var (root, count, tris) = DocumentToGodotScene.Build(nodes, SanitizeName(rootName));
        try
        {
            var gltf = new GltfDocument();
            var state = new GltfState();
            var err = gltf.AppendFromScene(root, state);
            if (err != Error.Ok) return ExportResult.Fail($"AppendFromScene failed: {err}");
            err = gltf.WriteToFilesystem(state, path);
            if (err != Error.Ok) return ExportResult.Fail($"WriteToFilesystem failed: {err}");
            return new ExportResult(true, $"Exported {count} node(s), {tris} triangles to {path}", count, tris);
        }
        catch (Exception ex) { return ExportResult.Fail(ex.Message); }
        finally { root.Free(); }
    }

    private static string SanitizeName(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var r = new string(chars);
        return r.Length == 0 ? "Scene" : r;
    }
}
