using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.IO;

/// <summary>
/// 네이티브 문서 형식(.cube, JSON). 노드 트리·트랜스폼·폴리곤 메시(n-gon, 코너 UV, 하드 엣지, 머티리얼 인덱스)를 저장한다.
/// 노멀은 저장하지 않고 다시 계산한다.
/// </summary>
public static class CubeFileFormat
{
    public const int Version = 1;
    public const string Extension = ".cube";

    private sealed class MeshDto
    {
        [JsonPropertyName("vertices")] public float[] Vertices { get; set; } = Array.Empty<float>();   // xyz 연속
        [JsonPropertyName("faces")] public int[][] Faces { get; set; } = Array.Empty<int[]>();          // 정점 ID 루프
        [JsonPropertyName("uvs")] public float[][] Uvs { get; set; } = Array.Empty<float[]>();          // 면별 코너 uv (u,v 연속)
        [JsonPropertyName("materials")] public int[] Materials { get; set; } = Array.Empty<int>();      // 면별
        [JsonPropertyName("hardEdges")] public int[][] HardEdges { get; set; } = Array.Empty<int[]>();  // [a,b]
    }

    private sealed class NodeDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "node";
        [JsonPropertyName("parent")] public int Parent { get; set; } = -1;   // nodes 배열 인덱스, -1 = 루트
        [JsonPropertyName("translation")] public float[] Translation { get; set; } = { 0, 0, 0 };
        [JsonPropertyName("rotation")] public float[] Rotation { get; set; } = { 0, 0, 0 };
        [JsonPropertyName("scale")] public float[] Scale { get; set; } = { 1, 1, 1 };
        [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
        [JsonPropertyName("mesh")] public MeshDto? Mesh { get; set; }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("format")] public string Format { get; set; } = "cube";
        [JsonPropertyName("version")] public int Version { get; set; } = CubeFileFormat.Version;
        [JsonPropertyName("nodes")] public List<NodeDto> Nodes { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string Serialize(Document doc)
    {
        var dto = new FileDto();
        var index = new Dictionary<SceneNode, int>();
        void Walk(SceneNode n, int parent)
        {
            var nd = new NodeDto
            {
                Name = n.Name, Parent = parent,
                Translation = V(n.Local.Translation), Rotation = V(n.Local.RotationDegrees), Scale = V(n.Local.Scale),
                Visible = n.Visible,
                Mesh = n.Mesh != null ? ToDto(n.Mesh) : null,
            };
            index[n] = dto.Nodes.Count;
            dto.Nodes.Add(nd);
            foreach (var c in n.Children) Walk(c, index[n]);
        }
        foreach (var c in doc.Root.Children) Walk(c, -1);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    public static void Save(Document doc, string path)
    {
        File.WriteAllText(path, Serialize(doc));
        doc.FilePath = path;
        doc.IsDirty = false;
    }

    /// <summary>문서를 비우고 파일 내용으로 채운다. Undo 이력은 지워진다.</summary>
    public static void Load(Document doc, string path)
    {
        var json = File.ReadAllText(path);
        Deserialize(doc, json);
        doc.FilePath = path;
        doc.IsDirty = false;
    }

    public static void Deserialize(Document doc, string json)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json) ?? throw new InvalidDataException("empty document");
        if (dto.Format != "cube") throw new InvalidDataException("not a .cube document");
        if (dto.Version > Version) throw new InvalidDataException($"document version {dto.Version} is newer than supported {Version}");
        doc.Clear();
        var nodes = new List<SceneNode>(dto.Nodes.Count);
        foreach (var nd in dto.Nodes)
        {
            var n = new SceneNode
            {
                Name = nd.Name,
                Local = new Transform3(V3(nd.Translation), V3(nd.Rotation), V3(nd.Scale, Vector3.One)),
                Visible = nd.Visible,
                Shape = nd.Mesh != null ? new MeshShape(FromDto(nd.Mesh)) : null,
            };
            nodes.Add(n);
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            int p = dto.Nodes[i].Parent;
            if (p >= 0 && p < nodes.Count && p != i) nodes[p].AttachChild(nodes[i]);
        }
        for (int i = 0; i < nodes.Count; i++)
            if (dto.Nodes[i].Parent < 0 || dto.Nodes[i].Parent >= nodes.Count) doc.AddNode(nodes[i]);
        doc.Undo.Clear();
        doc.IsDirty = false;
    }

    // ---------------------------------------------------------------- 메시

    private static MeshDto ToDto(PolyMesh src)
    {
        var m = src.Clone();
        m.Compact();
        var dto = new MeshDto();
        var verts = new float[m.VertexCount * 3];
        for (int v = 0; v < m.VertexCount; v++) { var p = m.Verts[v].Position; verts[v * 3] = p.X; verts[v * 3 + 1] = p.Y; verts[v * 3 + 2] = p.Z; }
        dto.Vertices = verts;
        var faces = new List<int[]>(); var uvs = new List<float[]>(); var mats = new List<int>();
        var loop = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            m.GetFaceHalfEdges(f, loop);
            var ids = new int[loop.Count]; var uv = new float[loop.Count * 2];
            for (int i = 0; i < loop.Count; i++) { var h = m.Hes[loop[i]]; ids[i] = h.Vertex; uv[i * 2] = h.Uv0.X; uv[i * 2 + 1] = h.Uv0.Y; }
            faces.Add(ids); uvs.Add(uv); mats.Add(m.Faces[f].Material);
        }
        dto.Faces = faces.ToArray(); dto.Uvs = uvs.ToArray(); dto.Materials = mats.ToArray();
        var hard = new List<int[]>();
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Hard) { var (a, b) = m.EdgeVertices(e); hard.Add(new[] { a, b }); }
        dto.HardEdges = hard.ToArray();
        return dto;
    }

    private static PolyMesh FromDto(MeshDto dto)
    {
        var m = new PolyMesh();
        for (int i = 0; i + 2 < dto.Vertices.Length; i += 3) m.AddVertex(new Vector3(dto.Vertices[i], dto.Vertices[i + 1], dto.Vertices[i + 2]));
        for (int f = 0; f < dto.Faces.Length; f++)
        {
            int nf = m.AddFace(dto.Faces[f], f < dto.Materials.Length ? dto.Materials[f] : 0);
            if (nf < 0) continue;
            if (f < dto.Uvs.Length)
            {
                var uv = dto.Uvs[f];
                int start = m.Faces[nf].HalfEdge, he = start, i = 0;
                do { var h = m.Hes[he]; if (i * 2 + 1 < uv.Length) h.Uv0 = new Vector2(uv[i * 2], uv[i * 2 + 1]); m.Hes[he] = h; he = h.Next; i++; } while (he != start);
            }
        }
        foreach (var pair in dto.HardEdges)
        {
            if (pair.Length < 2) continue;
            int e = m.FindEdge(pair[0], pair[1]);
            if (e >= 0) { var ed = m.Edges[e]; ed.Hard = true; m.Edges[e] = ed; }
        }
        MeshNormals.Recompute(m);
        m.BumpTopology();
        return m;
    }

    private static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static Vector3 V3(float[]? a, Vector3? fallback = null)
        => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : fallback ?? Vector3.Zero;
}
