using Cube.App.Bridge;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.IO;

/// <summary>Godot GltfDocument/FbxDocument가 만든 씬 트리를 Document 노드로 변환한다.</summary>
public abstract class GodotSceneImporterBase : IImporter
{
    public abstract string Name { get; }
    public abstract IReadOnlyList<string> Extensions { get; }

    protected abstract (GltfDocument doc, GltfState state) CreateDocument();

    public ImportResult Import(string path, Document doc, ImportOptions options)
    {
        Node? scene = null;
        try
        {
            var (gltf, state) = CreateDocument();
            var err = gltf.AppendFromFile(path, state);
            if (err != Error.Ok) return ImportResult.Fail($"AppendFromFile failed: {err}");
            scene = gltf.GenerateScene(state);
            if (scene == null) return ImportResult.Fail("GenerateScene returned null.");
            var nodes = new List<SceneNode>();
            int meshes = 0;
            // 루트 자체가 메시를 가지면 루트도 노드로, 아니면 루트의 자식들을 최상위로
            if (scene is Node3D rootN3 && HasMesh(rootN3)) nodes.Add(Convert(rootN3, doc, options, ref meshes));
            else foreach (var child in scene.GetChildren()) if (child is Node3D c3) nodes.Add(Convert(c3, doc, options, ref meshes));
            return new ImportResult(true, $"Imported {meshes} mesh(es) from {System.IO.Path.GetFileName(path)}", nodes);
        }
        catch (Exception ex) { return ImportResult.Fail(ex.Message); }
        finally { scene?.Free(); }
    }

    private static bool HasMesh(Node n) => n is MeshInstance3D { Mesh: not null } || n is ImporterMeshInstance3D { Mesh: not null };

    private static SceneNode Convert(Node3D g, Document doc, ImportOptions options, ref int meshes)
    {
        var node = new SceneNode { Name = doc.UniqueName(SafeName(g.Name)) };
        node.Local = Transform3.FromMatrix(g.Transform.ToNumerics());
        var surfaces = CollectSurfaces(g);
        if (surfaces.Count > 0)
        {
            var mesh = TriangleSoupToPolyMesh.Convert(surfaces, options, out _);
            node.Shape = new MeshShape(mesh);
            meshes++;
        }
        foreach (var child in g.GetChildren())
            if (child is Node3D c3)
            {
                var cn = Convert(c3, doc, options, ref meshes);
                node.AttachChild(cn);
            }
        return node;
    }

    private static string SafeName(string s) => string.IsNullOrWhiteSpace(s) ? "node" : s;

    private static List<TriangleSoupToPolyMesh.Surface> CollectSurfaces(Node3D g)
    {
        var list = new List<TriangleSoupToPolyMesh.Surface>();
        Mesh? mesh = g switch
        {
            MeshInstance3D mi => mi.Mesh,
            ImporterMeshInstance3D imi => imi.Mesh?.GetMesh(),
            _ => null,
        };
        if (mesh == null) return list;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh is ArrayMesh am && am.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles) continue;
            var arrays = mesh.SurfaceGetArrays(s);
            var pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var nrmV = arrays[(int)Mesh.ArrayType.Normal];
            var uvV = arrays[(int)Mesh.ArrayType.TexUV];
            var idxV = arrays[(int)Mesh.ArrayType.Index];
            var nrm = nrmV.VariantType != Variant.Type.Nil ? nrmV.AsVector3Array() : null;
            var uv = uvV.VariantType != Variant.Type.Nil ? uvV.AsVector2Array() : null;
            int[] idx = idxV.VariantType != Variant.Type.Nil ? idxV.AsInt32Array() : Enumerable.Range(0, pos.Length).ToArray();
            // Godot(CW 앞면) → 코어(CCW): 삼각형마다 1,2 교환. UV v 뒤집기(상단 원점 → 하단 원점)
            var indices = new int[idx.Length];
            for (int t = 0; t + 2 < idx.Length; t += 3) { indices[t] = idx[t]; indices[t + 1] = idx[t + 2]; indices[t + 2] = idx[t + 1]; }
            list.Add(new TriangleSoupToPolyMesh.Surface
            {
                Positions = pos.Select(p => p.ToNumerics()).ToArray(),
                Normals = nrm?.Select(n => n.ToNumerics()).ToArray(),
                Uvs = uv?.Select(u => new NVec2(u.X, 1f - u.Y)).ToArray(),
                Indices = indices,
                Material = s,
            });
        }
        return list;
    }
}

public sealed class GltfImporter : GodotSceneImporterBase
{
    public override string Name => "glTF 2.0";
    public override IReadOnlyList<string> Extensions { get; } = new[] { ".glb", ".gltf" };
    protected override (GltfDocument, GltfState) CreateDocument() => (new GltfDocument(), new GltfState());
}

public sealed class FbxImporter : GodotSceneImporterBase
{
    public override string Name => "FBX";
    public override IReadOnlyList<string> Extensions { get; } = new[] { ".fbx" };
    protected override (GltfDocument, GltfState) CreateDocument() => (new FbxDocument(), new FbxState());
}
