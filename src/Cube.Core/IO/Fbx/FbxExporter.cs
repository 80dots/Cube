using Cube.Core.Scene;

namespace Cube.Core.IO.Fbx;

/// <summary>자체 바이너리 FBX 7.4 writer(M4). Godot 의존 없음. 단위 cm 베이크(Unity "Convert Units"에서 scale 1).</summary>
public sealed class FbxExporter : IExporter
{
    public string Name => "FBX (binary 7.4)";
    public IReadOnlyList<string> Extensions { get; } = new[] { ".fbx" };

    public ExportResult Export(Document doc, IReadOnlyList<SceneNode> nodes, string path, ExportPreset preset)
    {
        using var restPose = Cube.Core.Scene.AnimationPose.RestScope(doc); // 재생 포즈가 아니라 rest(바인드) 포즈로 기록
        if (nodes.Count == 0) return ExportResult.Fail("Nothing to export.");
        try
        {
            var builder = new FbxSceneBuilder(doc, FbxExportOptions.Default with { BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) });
            var top = builder.Build(nodes);
            FbxBinaryWriter.Write(path, top, FbxExportOptions.Default.Compress);
            return new ExportResult(true, $"Exported {builder.NodeCount} node(s), {builder.TriangleCount} triangles to {path} (FBX 7.4, cm)", builder.NodeCount, builder.TriangleCount);
        }
        catch (Exception ex) { return ExportResult.Fail(ex.Message); }
    }
}
