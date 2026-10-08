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
        [JsonPropertyName("seams")] public int[][] Seams { get; set; } = Array.Empty<int[]>();          // [a,b] UV 심
        [JsonPropertyName("creases")] public float[][]? Creases { get; set; }                            // [a,b,crease]
        [JsonPropertyName("lockedNormals")] public float[][]? LockedNormals { get; set; }                // [v,x,y,z]
        [JsonPropertyName("cornerNormals")] public float[][]? CornerNormals { get; set; }                // [face, corner, x,y,z] 고정 코너 노멀
        [JsonPropertyName("pinnedUvs")] public int[][]? PinnedUvs { get; set; }                          // 면별 핀된 코너 인덱스
        [JsonPropertyName("uvSets")] public UvSetDto[]? UvSets { get; set; }
        [JsonPropertyName("currentUvSet")] public int CurrentUvSet { get; set; }
    }

    private sealed class UvSetDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "map1";
        [JsonPropertyName("uvs")] public float[][] Uvs { get; set; } = Array.Empty<float[]>();          // 면별 코너 uv
    }

    private sealed class SkinDto
    {
        [JsonPropertyName("joints")] public int[] Joints { get; set; } = Array.Empty<int>();            // nodes 배열 인덱스
        [JsonPropertyName("bindInverse")] public float[][] BindInverse { get; set; } = Array.Empty<float[]>(); // 16개 행우선(M11..M44)
        [JsonPropertyName("meshBindWorld")] public float[] MeshBindWorld { get; set; } = Array.Empty<float>();
        [JsonPropertyName("weights")] public float[][] Weights { get; set; } = Array.Empty<float[]>();  // 정점별 [j0,w0,j1,w1,...]
    }

    private sealed class NodeDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "node";
        [JsonPropertyName("parent")] public int Parent { get; set; } = -1;   // nodes 배열 인덱스, -1 = 루트
        [JsonPropertyName("translation")] public float[] Translation { get; set; } = { 0, 0, 0 };
        [JsonPropertyName("rotation")] public float[] Rotation { get; set; } = { 0, 0, 0 };
        [JsonPropertyName("scale")] public float[] Scale { get; set; } = { 1, 1, 1 };
        [JsonPropertyName("pivot")] public float[]? Pivot { get; set; }
        [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
        [JsonPropertyName("mesh")] public MeshDto? Mesh { get; set; }
        [JsonPropertyName("jointRadius")] public float? JointRadius { get; set; }   // null이 아니면 조인트
        [JsonPropertyName("light")] public LightDto? Light { get; set; }
        [JsonPropertyName("material")] public int Material { get; set; }            // 0 = 기본
        [JsonPropertyName("skin")] public SkinDto? Skin { get; set; }
    }

    private sealed class LightDto
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "point";
        [JsonPropertyName("color")] public float[] Color { get; set; } = { 1, 1, 1 };
        [JsonPropertyName("intensity")] public float Intensity { get; set; } = 1;
        [JsonPropertyName("range")] public float Range { get; set; } = 10;
        [JsonPropertyName("spotAngle")] public float SpotAngle { get; set; } = 45;
    }

    private sealed class MaterialDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "material";
        [JsonPropertyName("type")] public string Type { get; set; } = "lambert";
        [JsonPropertyName("color")] public float[] Color { get; set; } = { 0.5f, 0.5f, 0.5f };
        [JsonPropertyName("specular")] public float[] Specular { get; set; } = { 0.5f, 0.5f, 0.5f };
        [JsonPropertyName("shininess")] public float Shininess { get; set; } = 32;
        [JsonPropertyName("metallic")] public float Metallic { get; set; }
        [JsonPropertyName("roughness")] public float Roughness { get; set; } = 0.5f;
        [JsonPropertyName("matcap")] public string? Matcap { get; set; }
        [JsonPropertyName("texture")] public string? Texture { get; set; }
    }

    private sealed class AnimationDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "Take";
        [JsonPropertyName("length")] public float Length { get; set; }
        [JsonPropertyName("frameRate")] public float FrameRate { get; set; } = 30f;
        [JsonPropertyName("loop")] public bool Loop { get; set; }
        [JsonPropertyName("tracks")] public TrackDto[] Tracks { get; set; } = Array.Empty<TrackDto>();
    }

    private sealed class TrackDto
    {
        [JsonPropertyName("node")] public int Node { get; set; } = -1;                // nodes 배열 인덱스(-1 = 없음)
        [JsonPropertyName("nodeName")] public string NodeName { get; set; } = "";
        [JsonPropertyName("pt")] public float[]? PosTimes { get; set; }                 // 키 시간(초)
        [JsonPropertyName("pv")] public float[]? PosValues { get; set; }                // xyz 연속
        [JsonPropertyName("rt")] public float[]? RotTimes { get; set; }
        [JsonPropertyName("rv")] public float[]? RotValues { get; set; }                // 쿼터니언 xyzw 연속
        [JsonPropertyName("st")] public float[]? ScaleTimes { get; set; }
        [JsonPropertyName("sv")] public float[]? ScaleValues { get; set; }              // xyz 연속
    }

    private sealed class FileDto
    {
        [JsonPropertyName("format")] public string Format { get; set; } = "cube";
        [JsonPropertyName("version")] public int Version { get; set; } = CubeFileFormat.Version;
        [JsonPropertyName("materials")] public List<MaterialDto> Materials { get; set; } = new();
        [JsonPropertyName("nodes")] public List<NodeDto> Nodes { get; set; } = new();
        [JsonPropertyName("animations")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<AnimationDto>? Animations { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string Serialize(Document doc)
    {
        using var restPose = Cube.Core.Scene.AnimationPose.RestScope(doc); // 재생 포즈가 아니라 rest(바인드) 포즈로 기록
        var dto = new FileDto();
        var index = new Dictionary<SceneNode, int>();
        var order = new List<SceneNode>();
        void Walk(SceneNode n, int parent)
        {
            var nd = new NodeDto
            {
                Name = n.Name, Parent = parent,
                Translation = V(n.Local.Translation), Rotation = V(n.Local.RotationDegrees), Scale = V(n.Local.Scale), Pivot = n.Local.Pivot == Vector3.Zero ? null : V(n.Local.Pivot),
                Visible = n.Visible,
                Mesh = n.Mesh != null ? ToDto(n.Mesh, out _) : null,
                JointRadius = n.Joint?.Radius,
                Light = n.Light is { } lt ? new LightDto { Type = lt.Type.ToString().ToLowerInvariant(), Color = V(lt.Color), Intensity = lt.Intensity, Range = lt.Range, SpotAngle = lt.SpotAngle } : null,
                Material = n.MaterialId,
            };
            index[n] = dto.Nodes.Count;
            dto.Nodes.Add(nd); order.Add(n);
            foreach (var c in n.Children) Walk(c, index[n]);
        }
        foreach (var c in doc.Root.Children) Walk(c, -1);
        foreach (var mt in doc.Materials)
            dto.Materials.Add(new MaterialDto { Id = mt.Id, Name = mt.Name, Type = mt.Type.ToString().ToLowerInvariant(), Color = V(mt.Color), Specular = V(mt.Specular), Shininess = mt.Shininess, Metallic = mt.Metallic, Roughness = mt.Roughness, Matcap = mt.MatcapPath, Texture = mt.TexturePath });
        // 스킨은 노드 인덱스가 모두 정해진 뒤에 기록한다(메시는 Compact 리맵 반영)
        for (int i = 0; i < order.Count; i++)
        {
            var n = order[i];
            if (n.Skin == null || n.Mesh == null) continue;
            ToDto(n.Mesh, out var remap);
            dto.Nodes[i].Skin = SkinToDto(n.Skin, remap, doc, index);
        }
        if (doc.Animations.Count > 0) dto.Animations = doc.Animations.Select(c => AnimToDto(c, doc, index)).ToList();
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
        foreach (var md in dto.Materials)
            doc.AddMaterialWithId(new MaterialDef { Id = md.Id, Name = md.Name, Type = Enum.TryParse<MaterialType>(md.Type, true, out var mt) ? mt : MaterialType.Lambert, Color = V3(md.Color, new Vector3(0.5f)), Specular = V3(md.Specular, new Vector3(0.5f)), Shininess = md.Shininess, Metallic = md.Metallic, Roughness = md.Roughness, MatcapPath = md.Matcap, TexturePath = md.Texture });
        var nodes = new List<SceneNode>(dto.Nodes.Count);
        foreach (var nd in dto.Nodes)
        {
            var n = new SceneNode
            {
                Name = nd.Name,
                Local = new Transform3(V3(nd.Translation), V3(nd.Rotation), V3(nd.Scale, Vector3.One), V3(nd.Pivot)),
                Visible = nd.Visible,
                Shape = nd.Mesh != null ? new MeshShape(FromDto(nd.Mesh)) : nd.JointRadius is { } jr ? new JointShape { Radius = jr }
                    : nd.Light is { } ld ? new LightShape { Type = Enum.TryParse<LightType>(ld.Type, true, out var lt) ? lt : LightType.Point, Color = V3(ld.Color, Vector3.One), Intensity = ld.Intensity, Range = ld.Range, SpotAngle = ld.SpotAngle } : null,
                MaterialId = nd.Material,
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
        for (int i = 0; i < nodes.Count; i++)
            if (dto.Nodes[i].Skin is { } sd && nodes[i].MeshShape is { } ms) { ms.Skin = SkinFromDto(sd, nodes); doc.Notify(new DocChange(ChangeKind.SkinChanged, nodes[i].Id)); }
        if (dto.Animations is { Count: > 0 } anims)
        {
            foreach (var ad in anims) doc.Animations.Add(AnimFromDto(ad, nodes));
            doc.Notify(new DocChange(ChangeKind.AnimationsChanged, NodeId.None));
        }
        doc.Undo.Clear();
        doc.IsDirty = false;
    }

    // ---------------------------------------------------------------- 애니메이션

    private static AnimationDto AnimToDto(AnimationClip c, Document doc, Dictionary<SceneNode, int> index)
    {
        static float[]? Times<T>(List<AnimKey<T>> keys) => keys.Count == 0 ? null : keys.Select(k => k.Time).ToArray();
        static float[]? Vec(List<AnimKey<Vector3>> keys) => keys.Count == 0 ? null : keys.SelectMany(k => new[] { k.Value.X, k.Value.Y, k.Value.Z }).ToArray();
        return new AnimationDto
        {
            Name = c.Name, Length = c.Length, FrameRate = c.FrameRate, Loop = c.Loop,
            Tracks = c.Tracks.Select(t => new TrackDto
            {
                Node = doc.Find(t.Node) is { } n && index.TryGetValue(n, out int ix) ? ix : -1,
                NodeName = t.NodeName,
                PosTimes = Times(t.Position), PosValues = Vec(t.Position),
                RotTimes = Times(t.Rotation), RotValues = t.Rotation.Count == 0 ? null : t.Rotation.SelectMany(k => new[] { k.Value.X, k.Value.Y, k.Value.Z, k.Value.W }).ToArray(),
                ScaleTimes = Times(t.Scale), ScaleValues = Vec(t.Scale),
            }).ToArray(),
        };
    }

    private static AnimationClip AnimFromDto(AnimationDto d, List<SceneNode> nodes)
    {
        var c = new AnimationClip { Name = d.Name, Length = d.Length, FrameRate = d.FrameRate > 0 ? d.FrameRate : 30f, Loop = d.Loop };
        foreach (var td in d.Tracks)
        {
            var t = new NodeTrack { Node = td.Node >= 0 && td.Node < nodes.Count ? nodes[td.Node].Id : NodeId.None, NodeName = td.NodeName };
            if (td.PosTimes != null && td.PosValues != null)
                for (int i = 0; i < td.PosTimes.Length && i * 3 + 2 < td.PosValues.Length; i++)
                    t.Position.Add(new AnimKey<Vector3>(td.PosTimes[i], new Vector3(td.PosValues[i * 3], td.PosValues[i * 3 + 1], td.PosValues[i * 3 + 2])));
            if (td.RotTimes != null && td.RotValues != null)
                for (int i = 0; i < td.RotTimes.Length && i * 4 + 3 < td.RotValues.Length; i++)
                    t.Rotation.Add(new AnimKey<Quaternion>(td.RotTimes[i], new Quaternion(td.RotValues[i * 4], td.RotValues[i * 4 + 1], td.RotValues[i * 4 + 2], td.RotValues[i * 4 + 3])));
            if (td.ScaleTimes != null && td.ScaleValues != null)
                for (int i = 0; i < td.ScaleTimes.Length && i * 3 + 2 < td.ScaleValues.Length; i++)
                    t.Scale.Add(new AnimKey<Vector3>(td.ScaleTimes[i], new Vector3(td.ScaleValues[i * 3], td.ScaleValues[i * 3 + 1], td.ScaleValues[i * 3 + 2])));
            c.Tracks.Add(t);
        }
        return c;
    }

    // ---------------------------------------------------------------- 메시

    private static SkinDto SkinToDto(SkinCluster skin, CompactRemap remap, Document doc, Dictionary<SceneNode, int> index)
    {
        var sd = new SkinDto
        {
            Joints = skin.Joints.Select(id => doc.Find(id) is { } jn && index.TryGetValue(jn, out int ix) ? ix : -1).ToArray(),
            BindInverse = skin.BindInverse.Select(M16).ToArray(),
            MeshBindWorld = M16(skin.MeshBindWorld),
        };
        int nv = remap.Vertices.Count(v => v >= 0);
        var weights = new float[nv][];
        for (int v = 0; v < remap.Vertices.Length; v++)
        {
            int nvId = remap.Vertices[v];
            if (nvId < 0) continue;
            var list = v < skin.Weights.Length ? skin.Weights[v] : null;
            if (list == null) { weights[nvId] = Array.Empty<float>(); continue; }
            var arr = new float[list.Count * 2];
            for (int i = 0; i < list.Count; i++) { arr[i * 2] = list[i].joint; arr[i * 2 + 1] = list[i].weight; }
            weights[nvId] = arr;
        }
        sd.Weights = weights;
        return sd;
    }

    private static SkinCluster SkinFromDto(SkinDto sd, List<SceneNode> nodes)
    {
        var skin = new SkinCluster { MeshBindWorld = sd.MeshBindWorld.Length == 16 ? FromM16(sd.MeshBindWorld) : Matrix4x4.Identity };
        for (int j = 0; j < sd.Joints.Length; j++)
        {
            int ix = sd.Joints[j];
            skin.Joints.Add(ix >= 0 && ix < nodes.Count ? nodes[ix].Id : NodeId.None);
            skin.BindInverse.Add(j < sd.BindInverse.Length && sd.BindInverse[j].Length == 16 ? FromM16(sd.BindInverse[j]) : Matrix4x4.Identity);
        }
        skin.EnsureSize(sd.Weights.Length);
        for (int v = 0; v < sd.Weights.Length; v++)
        {
            var arr = sd.Weights[v];
            if (arr == null || arr.Length < 2) continue;
            var list = new List<(int, float)>();
            for (int i = 0; i + 1 < arr.Length; i += 2) list.Add(((int)arr[i], arr[i + 1]));
            skin.Weights[v] = list;
        }
        return skin;
    }

    private static float[] M16(Matrix4x4 m) => new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
    private static Matrix4x4 FromM16(float[] a) => new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);

    private static MeshDto ToDto(PolyMesh src, out CompactRemap remap)
    {
        var m = src.Clone();
        remap = m.Compact();
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
        var hard = new List<int[]>(); var seams = new List<int[]>();
        for (int e = 0; e < m.EdgeCount; e++)
        {
            var (a, b) = m.EdgeVertices(e);
            if (m.Edges[e].Hard) hard.Add(new[] { a, b });
            if (m.Edges[e].Seam) seams.Add(new[] { a, b });
        }
        dto.HardEdges = hard.ToArray(); dto.Seams = seams.ToArray();
        var creases = new List<float[]>();
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Crease > 0f) { var (a, b) = m.EdgeVertices(e); creases.Add(new[] { a, b, m.Edges[e].Crease }); }
        if (creases.Count > 0) dto.Creases = creases.ToArray();
        if (m.LockedNormals.Count > 0) dto.LockedNormals = m.LockedNormals.Select(kv => new[] { kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Z }).ToArray();
        var cornerNormals = new List<float[]>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            m.GetFaceHalfEdges(f, loop);
            for (int i = 0; i < loop.Count; i++) { var h = m.Hes[loop[i]]; if (h.NormalLocked) cornerNormals.Add(new[] { f, i, h.Normal.X, h.Normal.Y, h.Normal.Z }); }
        }
        if (cornerNormals.Count > 0) dto.CornerNormals = cornerNormals.ToArray();
        // 핀: 면별 코너 인덱스
        var pins = new List<int[]>(); bool anyPin = false;
        for (int f = 0; f < m.FaceCount; f++)
        {
            m.GetFaceHalfEdges(f, loop);
            var p = new List<int>(); for (int i = 0; i < loop.Count; i++) if (m.Hes[loop[i]].PinUv) p.Add(i);
            if (p.Count > 0) anyPin = true;
            pins.Add(p.ToArray());
        }
        if (anyPin) dto.PinnedUvs = pins.ToArray();
        if (m.UvSets.Count > 1)
        {
            m.StoreCurrentUvs();
            dto.CurrentUvSet = m.CurrentUvSet;
            dto.UvSets = m.UvSets.Select(set =>
            {
                var per = new List<float[]>();
                for (int f = 0; f < m.FaceCount; f++)
                {
                    m.GetFaceHalfEdges(f, loop);
                    var uv = new float[loop.Count * 2];
                    for (int i = 0; i < loop.Count; i++) { var v = loop[i] < set.Uvs.Length ? set.Uvs[loop[i]] : Vector2.Zero; uv[i * 2] = v.X; uv[i * 2 + 1] = v.Y; }
                    per.Add(uv);
                }
                return new UvSetDto { Name = set.Name, Uvs = per.ToArray() };
            }).ToArray();
        }
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
        foreach (var pair in dto.Seams)
        {
            if (pair.Length < 2) continue;
            int e = m.FindEdge(pair[0], pair[1]);
            if (e >= 0) { var ed = m.Edges[e]; ed.Seam = true; m.Edges[e] = ed; }
        }
        if (dto.Creases != null)
            foreach (var c in dto.Creases)
            {
                if (c.Length < 3) continue;
                int e = m.FindEdge((int)c[0], (int)c[1]);
                if (e >= 0) { var ed = m.Edges[e]; ed.Crease = c[2]; m.Edges[e] = ed; }
            }
        if (dto.LockedNormals != null)
            foreach (var l in dto.LockedNormals) if (l.Length >= 4) m.LockedNormals[(int)l[0]] = new Vector3(l[1], l[2], l[3]);
        if (dto.CornerNormals != null)
        {
            var cl = new List<int>();
            foreach (var c in dto.CornerNormals)
            {
                if (c.Length < 5) continue;
                int f = (int)c[0], ci = (int)c[1];
                if (f < 0 || f >= m.FaceCount) continue;
                m.GetFaceHalfEdges(f, cl);
                if (ci < 0 || ci >= cl.Count) continue;
                var h = m.Hes[cl[ci]]; h.Normal = new Vector3(c[2], c[3], c[4]); h.NormalLocked = true; m.Hes[cl[ci]] = h;
            }
        }
        if (dto.PinnedUvs != null)
        {
            var loop = new List<int>();
            for (int f = 0; f < m.FaceCount && f < dto.PinnedUvs.Length; f++)
            {
                m.GetFaceHalfEdges(f, loop);
                foreach (int ci in dto.PinnedUvs[f]) if (ci >= 0 && ci < loop.Count) { var h = m.Hes[loop[ci]]; h.PinUv = true; m.Hes[loop[ci]] = h; }
            }
        }
        if (dto.UvSets != null && dto.UvSets.Length > 0)
        {
            var loop = new List<int>();
            foreach (var sd in dto.UvSets)
            {
                var arr = new Vector2[m.HalfEdgeCount];
                for (int f = 0; f < m.FaceCount && f < sd.Uvs.Length; f++)
                {
                    m.GetFaceHalfEdges(f, loop);
                    var uv = sd.Uvs[f];
                    for (int i = 0; i < loop.Count && i * 2 + 1 < uv.Length; i++) arr[loop[i]] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
                }
                m.UvSets.Add(new UvSet { Name = sd.Name, Uvs = arr });
            }
            m.CurrentUvSet = Math.Clamp(dto.CurrentUvSet, 0, m.UvSets.Count - 1);
            // 코너 UV(dto.Uvs)는 현재 세트와 같다
        }
        MeshNormals.Recompute(m);
        m.BumpTopology();
        return m;
    }

    private static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static Vector3 V3(float[]? a, Vector3? fallback = null)
        => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : fallback ?? Vector3.Zero;
}
