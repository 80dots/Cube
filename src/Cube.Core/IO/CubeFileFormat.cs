using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.IO;

// 파일 구조(최상위 JSON 객체):
//   { "format": "cube", "version": 1, "materials": [...], "nodes": [...], "animations": [...]? }
// 노드는 깊이 우선(부모가 자식보다 앞) 평탄 배열이며 부모는 배열 인덱스로 가리킨다.
// 메시는 저장 직전 Compact()한 사본을 기록하므로 정점/면 ID가 0부터 빈틈없이 다시 매겨진다.
// 스킨·애니메이션의 노드 참조도 런타임 NodeId가 아니라 nodes 배열 인덱스로 저장한다.
/// <summary>
/// 네이티브 문서 형식(.cube, JSON). 노드 트리·트랜스폼·폴리곤 메시(n-gon, 코너 UV, 하드 엣지, 머티리얼 인덱스)를 저장한다.
/// 노멀은 저장하지 않고 다시 계산한다.
/// </summary>
public static class CubeFileFormat
{
    /// <summary>현재 파일 형식 버전. 읽을 때 이보다 큰 버전은 거부한다(하위 버전은 허용).</summary>
    public const int Version = 1;
    /// <summary>네이티브 문서 확장자.</summary>
    public const string Extension = ".cube";

    /// <summary>
    /// 폴리곤 메시 직렬화 DTO. 정점은 Compact 후 ID 순서, 면은 정점 ID 루프(CCW), 코너 속성은 면별·코너 순서 배열이다.
    /// 엣지 속성(하드/심/크리즈)은 엣지 ID가 아니라 정점 쌍 [a,b]로 저장해 다시 읽을 때 FindEdge로 복원한다.
    /// 선택적 항목(nullable)은 값이 있을 때만 기록된다.
    /// </summary>
    private sealed class MeshDto
    {
        /// <summary>정점 위치(x,y,z 연속; 오브젝트 로컬 공간, m).</summary>
        [JsonPropertyName("vertices")] public float[] Vertices { get; set; } = Array.Empty<float>();   // xyz 연속
        /// <summary>면마다 정점 ID 루프.</summary>
        [JsonPropertyName("faces")] public int[][] Faces { get; set; } = Array.Empty<int[]>();          // 정점 ID 루프
        /// <summary>면마다 코너 UV(u0,v0,u1,v1,...; 하단 원점). 현재 UV 세트의 값이다.</summary>
        [JsonPropertyName("uvs")] public float[][] Uvs { get; set; } = Array.Empty<float[]>();          // 면별 코너 uv (u,v 연속)
        /// <summary>면마다 머티리얼 인덱스.</summary>
        [JsonPropertyName("materials")] public int[] Materials { get; set; } = Array.Empty<int>();      // 면별
        /// <summary>하드 엣지 목록(정점 쌍).</summary>
        [JsonPropertyName("hardEdges")] public int[][] HardEdges { get; set; } = Array.Empty<int[]>();  // [a,b]
        /// <summary>UV 심 엣지 목록(정점 쌍).</summary>
        [JsonPropertyName("seams")] public int[][] Seams { get; set; } = Array.Empty<int[]>();          // [a,b] UV 심
        /// <summary>크리즈 엣지 목록 [a, b, 크리즈 값](값이 0보다 큰 엣지만).</summary>
        [JsonPropertyName("creases")] public float[][]? Creases { get; set; }                            // [a,b,crease]
        /// <summary>정점 단위로 잠근 노멀 [정점 ID, x, y, z](<c>PolyMesh.LockedNormals</c>).</summary>
        [JsonPropertyName("lockedNormals")] public float[][]? LockedNormals { get; set; }                // [v,x,y,z]
        /// <summary>코너 단위로 고정된 노멀(<c>HalfEdge.NormalLocked</c>, Bevel Harden Normals 등).</summary>
        [JsonPropertyName("cornerNormals")] public float[][]? CornerNormals { get; set; }                // [face, corner, x,y,z] 고정 코너 노멀
        /// <summary>면마다 핀(<c>HalfEdge.PinUv</c>)이 켜진 코너 인덱스. 핀이 하나도 없으면 생략.</summary>
        [JsonPropertyName("pinnedUvs")] public int[][]? PinnedUvs { get; set; }                          // 면별 핀된 코너 인덱스
        /// <summary>UV 세트가 2개 이상일 때만 기록하는 모든 세트의 코너 UV.</summary>
        [JsonPropertyName("uvSets")] public UvSetDto[]? UvSets { get; set; }
        /// <summary>현재 UV 세트 인덱스.</summary>
        [JsonPropertyName("currentUvSet")] public int CurrentUvSet { get; set; }
    }

    /// <summary>UV 세트 하나(이름 + 면별 코너 UV).</summary>
    private sealed class UvSetDto
    {
        /// <summary>UV 세트 이름(Maya 기본 "map1").</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "map1";
        [JsonPropertyName("uvs")] public float[][] Uvs { get; set; } = Array.Empty<float[]>();          // 면별 코너 uv
    }

    /// <summary>
    /// 스킨 클러스터 직렬화 DTO. 조인트는 nodes 배열 인덱스, 행렬은 행 우선 16개 float,
    /// 가중치는 Compact 후 정점 ID 순서로 정점마다 (조인트 슬롯, 가중치) 쌍을 평탄화한 배열이다.
    /// </summary>
    private sealed class SkinDto
    {
        [JsonPropertyName("joints")] public int[] Joints { get; set; } = Array.Empty<int>();            // nodes 배열 인덱스
        [JsonPropertyName("bindInverse")] public float[][] BindInverse { get; set; } = Array.Empty<float[]>(); // 16개 행우선(M11..M44)
        /// <summary>바인드 시점 메시의 월드 행렬(16개).</summary>
        [JsonPropertyName("meshBindWorld")] public float[] MeshBindWorld { get; set; } = Array.Empty<float>();
        [JsonPropertyName("weights")] public float[][] Weights { get; set; } = Array.Empty<float[]>();  // 정점별 [j0,w0,j1,w1,...]
    }

    /// <summary>노드(<see cref="SceneNode"/>) 하나의 DTO. 셰이프는 mesh / jointRadius / light 중 하나만 채워진다.</summary>
    private sealed class NodeDto
    {
        /// <summary>노드 이름.</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "node";
        [JsonPropertyName("parent")] public int Parent { get; set; } = -1;   // nodes 배열 인덱스, -1 = 루트
        /// <summary>로컬 이동(m).</summary>
        [JsonPropertyName("translation")] public float[] Translation { get; set; } = { 0, 0, 0 };
        /// <summary>로컬 회전(XYZ 오일러, 도).</summary>
        [JsonPropertyName("rotation")] public float[] Rotation { get; set; } = { 0, 0, 0 };
        /// <summary>로컬 스케일.</summary>
        [JsonPropertyName("scale")] public float[] Scale { get; set; } = { 1, 1, 1 };
        /// <summary>회전/스케일 피벗(오브젝트 공간). 0이면 생략한다.</summary>
        [JsonPropertyName("pivot")] public float[]? Pivot { get; set; }
        /// <summary>표시 여부.</summary>
        [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
        /// <summary>메시 셰이프(메시 노드일 때).</summary>
        [JsonPropertyName("mesh")] public MeshDto? Mesh { get; set; }
        [JsonPropertyName("jointRadius")] public float? JointRadius { get; set; }   // null이 아니면 조인트
        /// <summary>라이트 셰이프(라이트 노드일 때).</summary>
        [JsonPropertyName("light")] public LightDto? Light { get; set; }
        [JsonPropertyName("material")] public int Material { get; set; }            // 0 = 기본
        /// <summary>스킨(메시 노드에 바인드된 경우). 모든 노드 인덱스가 정해진 뒤 채운다.</summary>
        [JsonPropertyName("skin")] public SkinDto? Skin { get; set; }
    }

    /// <summary>라이트 셰이프 DTO. type은 <see cref="LightType"/> 이름의 소문자.</summary>
    private sealed class LightDto
    {
        /// <summary>라이트 종류("directional"/"point"/"spot").</summary>
        [JsonPropertyName("type")] public string Type { get; set; } = "point";
        /// <summary>라이트 색(선형 아님, 편집 값 그대로).</summary>
        [JsonPropertyName("color")] public float[] Color { get; set; } = { 1, 1, 1 };
        /// <summary>세기.</summary>
        [JsonPropertyName("intensity")] public float Intensity { get; set; } = 1;
        /// <summary>영향 범위(m; 포인트/스폿).</summary>
        [JsonPropertyName("range")] public float Range { get; set; } = 10;
        /// <summary>스폿 원뿔 각도(도).</summary>
        [JsonPropertyName("spotAngle")] public float SpotAngle { get; set; } = 45;
    }

    /// <summary>
    /// 머티리얼 DTO. 예전(v0.0.36 이전) 개별 필드와 새 Values/Textures 사전을 함께 기록해 하위 호환을 유지한다.
    /// </summary>
    private sealed class MaterialDto
    {
        /// <summary>머티리얼 ID(노드의 material이 가리키는 값; 0 = 기본 lambert1).</summary>
        [JsonPropertyName("id")] public int Id { get; set; }
        /// <summary>머티리얼 이름.</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "material";
        /// <summary>머티리얼 종류(<see cref="MaterialType"/> 이름의 소문자).</summary>
        [JsonPropertyName("type")] public string Type { get; set; } = "lambert";
        /// <summary>기본 색.</summary>
        [JsonPropertyName("color")] public float[] Color { get; set; } = { 0.5f, 0.5f, 0.5f };
        /// <summary>스펙큘러 색(Blinn-Phong).</summary>
        [JsonPropertyName("specular")] public float[] Specular { get; set; } = { 0.5f, 0.5f, 0.5f };
        /// <summary>광택 지수(Blinn-Phong).</summary>
        [JsonPropertyName("shininess")] public float Shininess { get; set; } = 32;
        /// <summary>메탈릭(PBR).</summary>
        [JsonPropertyName("metallic")] public float Metallic { get; set; }
        /// <summary>러프니스(PBR).</summary>
        [JsonPropertyName("roughness")] public float Roughness { get; set; } = 0.5f;
        /// <summary>Matcap 이미지 경로.</summary>
        [JsonPropertyName("matcap")] public string? Matcap { get; set; }
        /// <summary>컬러 텍스처 경로(예전 필드).</summary>
        [JsonPropertyName("texture")] public string? Texture { get; set; }
        /// <summary>v0.0.36: 모든 파라미터 값(키 → [x,y,z]) — 위의 개별 필드보다 우선.</summary>
        [JsonPropertyName("values")] public Dictionary<string, float[]>? Values { get; set; }
        /// <summary>v0.0.36: 파라미터 텍스처(키 → 이미지 경로).</summary>
        [JsonPropertyName("textures")] public Dictionary<string, string>? Textures { get; set; }
    }

    /// <summary>애니메이션 클립 DTO(<see cref="AnimationClip"/>).</summary>
    private sealed class AnimationDto
    {
        /// <summary>클립 이름.</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "Take";
        /// <summary>길이(초).</summary>
        [JsonPropertyName("length")] public float Length { get; set; }
        /// <summary>프레임 레이트(타임 슬라이더 눈금용).</summary>
        [JsonPropertyName("frameRate")] public float FrameRate { get; set; } = 30f;
        /// <summary>반복 재생 여부.</summary>
        [JsonPropertyName("loop")] public bool Loop { get; set; }
        /// <summary>노드별 트랙.</summary>
        [JsonPropertyName("tracks")] public TrackDto[] Tracks { get; set; } = Array.Empty<TrackDto>();
    }

    /// <summary>노드 하나의 위치/회전/스케일 키 트랙 DTO. 시간 배열과 값 배열을 따로 둔다(값은 성분 연속).</summary>
    private sealed class TrackDto
    {
        [JsonPropertyName("node")] public int Node { get; set; } = -1;                // nodes 배열 인덱스(-1 = 없음)
        /// <summary>대상 노드 이름(참조가 끊겨도 표시용으로 유지).</summary>
        [JsonPropertyName("nodeName")] public string NodeName { get; set; } = "";
        [JsonPropertyName("pt")] public float[]? PosTimes { get; set; }                 // 키 시간(초)
        [JsonPropertyName("pv")] public float[]? PosValues { get; set; }                // xyz 연속
        /// <summary>회전 키 시간(초).</summary>
        [JsonPropertyName("rt")] public float[]? RotTimes { get; set; }
        [JsonPropertyName("rv")] public float[]? RotValues { get; set; }                // 쿼터니언 xyzw 연속
        /// <summary>스케일 키 시간(초).</summary>
        [JsonPropertyName("st")] public float[]? ScaleTimes { get; set; }
        [JsonPropertyName("sv")] public float[]? ScaleValues { get; set; }              // xyz 연속
    }

    /// <summary>파일 최상위 DTO.</summary>
    private sealed class FileDto
    {
        /// <summary>형식 식별자("cube"가 아니면 거부).</summary>
        [JsonPropertyName("format")] public string Format { get; set; } = "cube";
        /// <summary>형식 버전.</summary>
        [JsonPropertyName("version")] public int Version { get; set; } = CubeFileFormat.Version;
        /// <summary>문서 머티리얼 목록.</summary>
        [JsonPropertyName("materials")] public List<MaterialDto> Materials { get; set; } = new();
        /// <summary>노드 평탄 배열(부모가 자식보다 앞).</summary>
        [JsonPropertyName("nodes")] public List<NodeDto> Nodes { get; set; } = new();
        /// <summary>애니메이션 클립(없으면 키 자체를 생략).</summary>
        [JsonPropertyName("animations")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<AnimationDto>? Animations { get; set; }
    }

    /// <summary>직렬화 옵션(공백 없는 압축 JSON).</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// 문서를 .cube JSON 문자열로 만든다. 재생 포즈는 끄고 rest 포즈 기준으로 기록한다.
    /// 순서: ① 노드 트리를 깊이 우선으로 평탄화(노드→인덱스 표 작성) → ② 머티리얼 → ③ 스킨(조인트 인덱스가 필요하므로 마지막) → ④ 애니메이션.
    /// </summary>
    public static string Serialize(Document doc)
    {
        using var restPose = Cube.Core.Scene.AnimationPose.RestScope(doc); // 재생 포즈가 아니라 rest(바인드) 포즈로 기록
        var dto = new FileDto();
        // index = 노드 → nodes 배열 인덱스, order = 같은 순서의 노드 목록(스킨 기록 때 다시 순회).
        var index = new Dictionary<SceneNode, int>();
        var order = new List<SceneNode>();
        // 깊이 우선 순회: 자기 DTO를 추가한 뒤 자식을 자기 인덱스를 부모로 해서 재귀.
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
            dto.Materials.Add(new MaterialDto { Id = mt.Id, Name = mt.Name, Type = mt.Type.ToString().ToLowerInvariant(), Color = V(mt.Color), Specular = V(mt.Specular), Shininess = mt.Shininess, Metallic = mt.Metallic, Roughness = mt.Roughness, Matcap = mt.MatcapPath, Texture = mt.TexturePath,
                Values = mt.Values.Count > 0 ? mt.Values.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.X, kv.Value.Y, kv.Value.Z }) : null,
                Textures = mt.Textures.Count > 0 ? new Dictionary<string, string>(mt.Textures) : null });
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

    /// <summary>문서를 파일로 저장하고 문서 경로를 기록, 변경 표시(dirty)를 지운다.</summary>
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

    /// <summary>
    /// JSON을 읽어 문서를 다시 채운다(기존 내용은 <c>doc.Clear()</c>로 지움).
    /// 순서: 형식/버전 검사 → 머티리얼(ID 유지) → 노드 생성 → 부모-자식 연결 → 루트 노드를 문서에 추가(ID 배정) →
    /// 스킨(노드 ID가 필요) → 애니메이션 → Undo 이력 비움.
    /// </summary>
    /// <exception cref="InvalidDataException">형식이 다르거나 버전이 더 새로울 때.</exception>
    public static void Deserialize(Document doc, string json)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json) ?? throw new InvalidDataException("empty document");
        if (dto.Format != "cube") throw new InvalidDataException("not a .cube document");
        if (dto.Version > Version) throw new InvalidDataException($"document version {dto.Version} is newer than supported {Version}");
        doc.Clear();
        foreach (var md in dto.Materials)
        {
            var mdef = new MaterialDef { Id = md.Id, Name = md.Name, Type = Enum.TryParse<MaterialType>(md.Type, true, out var mt) ? mt : MaterialType.Lambert, Color = V3(md.Color, new Vector3(0.5f)), Specular = V3(md.Specular, new Vector3(0.5f)), Shininess = md.Shininess, Metallic = md.Metallic, Roughness = md.Roughness, MatcapPath = md.Matcap, TexturePath = md.Texture };
            // v0.0.36 전 파일: 컬러 텍스처가 색을 대신했으므로(곱하지 않음) 색을 흰색으로 맞춰 같은 모습을 유지
            if (md.Values == null && !string.IsNullOrEmpty(md.Texture)) mdef.Color = Vector3.One;
            // v0.0.36 이후 파일: 값/텍스처 사전을 그대로 복원한다.
            if (md.Values != null) foreach (var (k, v) in md.Values) mdef.Values[k] = V3(v, Vector3.Zero);
            if (md.Textures != null) foreach (var (k, v) in md.Textures) mdef.SetTex(k, v);
            doc.AddMaterialWithId(mdef);
        }
        // 노드를 배열 순서대로 만든다(아직 ID 없음). 셰이프는 mesh → joint → light 순으로 판별.
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
        // 부모 인덱스로 트리를 다시 잇는다(잘못된 인덱스·자기 자신은 무시).
        for (int i = 0; i < nodes.Count; i++)
        {
            int p = dto.Nodes[i].Parent;
            if (p >= 0 && p < nodes.Count && p != i) nodes[p].AttachChild(nodes[i]);
        }
        // 부모가 없는(또는 잘못된) 노드를 문서 루트에 붙인다. AddNode가 하위 노드까지 ID를 배정한다.
        for (int i = 0; i < nodes.Count; i++)
            if (dto.Nodes[i].Parent < 0 || dto.Nodes[i].Parent >= nodes.Count) doc.AddNode(nodes[i]);
        // 스킨은 조인트를 NodeId로 가리키므로 ID 배정 뒤에 복원하고, 뷰가 스킨을 갱신하도록 통지한다.
        for (int i = 0; i < nodes.Count; i++)
            if (dto.Nodes[i].Skin is { } sd && nodes[i].MeshShape is { } ms) { ms.Skin = SkinFromDto(sd, nodes); doc.Notify(new DocChange(ChangeKind.SkinChanged, nodes[i].Id)); }
        // 애니메이션 트랙도 nodes 인덱스 → NodeId로 바꿔 넣는다.
        if (dto.Animations is { Count: > 0 } anims)
        {
            foreach (var ad in anims) doc.Animations.Add(AnimFromDto(ad, nodes));
            doc.Notify(new DocChange(ChangeKind.AnimationsChanged, NodeId.None));
        }
        // 불러온 상태는 Undo로 되돌릴 대상이 아니다.
        doc.Undo.Clear();
        doc.IsDirty = false;
    }

    // ---------------------------------------------------------------- 애니메이션

    /// <summary>
    /// 클립을 DTO로 바꾼다. 트랙 노드는 문서에서 찾아 nodes 배열 인덱스로 바꾸며(못 찾으면 -1), 키는 시간·값 배열로 펼친다.
    /// </summary>
    private static AnimationDto AnimToDto(AnimationClip c, Document doc, Dictionary<SceneNode, int> index)
    {
        // 키 목록에서 시간만 뽑는다(키가 없으면 null로 생략).
        static float[]? Times<T>(List<AnimKey<T>> keys) => keys.Count == 0 ? null : keys.Select(k => k.Time).ToArray();
        // Vector3 키 값을 x,y,z 연속 배열로 펼친다.
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

    /// <summary>
    /// DTO에서 클립을 복원한다. 트랙 노드 인덱스를 새로 만든 노드의 ID로 바꾸고,
    /// 시간·값 배열 길이가 맞지 않으면 짧은 쪽까지만 키를 만든다. 프레임 레이트가 0 이하면 30으로 보정.
    /// </summary>
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

    /// <summary>
    /// 스킨을 DTO로 바꾼다. 가중치 배열은 <paramref name="remap"/>(Compact의 옛 정점 ID → 새 ID)으로 재배열해
    /// 저장되는 메시 정점 순서와 맞춘다. 죽은 정점의 가중치는 버린다.
    /// </summary>
    private static SkinDto SkinToDto(SkinCluster skin, CompactRemap remap, Document doc, Dictionary<SceneNode, int> index)
    {
        var sd = new SkinDto
        {
            Joints = skin.Joints.Select(id => doc.Find(id) is { } jn && index.TryGetValue(jn, out int ix) ? ix : -1).ToArray(),
            BindInverse = skin.BindInverse.Select(M16).ToArray(),
            MeshBindWorld = M16(skin.MeshBindWorld),
        };
        // 새 정점 수 = 살아남은(리맵 값 ≥ 0) 정점 수.
        int nv = remap.Vertices.Count(v => v >= 0);
        var weights = new float[nv][];
        for (int v = 0; v < remap.Vertices.Length; v++)
        {
            int nvId = remap.Vertices[v];
            if (nvId < 0) continue;
            var list = v < skin.Weights.Length ? skin.Weights[v] : null;
            if (list == null) { weights[nvId] = Array.Empty<float>(); continue; }
            // (조인트 슬롯, 가중치) 쌍을 [j0, w0, j1, w1, ...]로 평탄화한다.
            var arr = new float[list.Count * 2];
            for (int i = 0; i < list.Count; i++) { arr[i * 2] = list[i].joint; arr[i * 2 + 1] = list[i].weight; }
            weights[nvId] = arr;
        }
        sd.Weights = weights;
        return sd;
    }

    /// <summary>
    /// DTO에서 스킨을 복원한다. 조인트 인덱스 → 노드 ID, 행렬 길이가 16이 아니면 단위 행렬로 대체한다.
    /// 가중치는 정점마다 (조인트 슬롯, 가중치) 리스트로 다시 묶는다.
    /// </summary>
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

    /// <summary>행렬을 행 우선 16개 float(M11..M44)로 펼친다.</summary>
    private static float[] M16(Matrix4x4 m) => new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
    /// <summary>행 우선 16개 float에서 행렬을 만든다(<see cref="M16"/>의 역).</summary>
    private static Matrix4x4 FromM16(float[] a) => new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);

    /// <summary>
    /// 메시를 DTO로 바꾼다. 원본을 건드리지 않도록 복제한 뒤 Compact해 ID를 0부터 빈틈없이 만든다.
    /// </summary>
    /// <param name="src">문서의 메시(바뀌지 않는다).</param>
    /// <param name="remap">Compact의 옛 ID → 새 ID 매핑(스킨 가중치 재배열용).</param>
    private static MeshDto ToDto(PolyMesh src, out CompactRemap remap)
    {
        var m = src.Clone();
        remap = m.Compact();
        var dto = new MeshDto();
        // 정점 위치를 평탄 배열로.
        var verts = new float[m.VertexCount * 3];
        for (int v = 0; v < m.VertexCount; v++) { var p = m.Verts[v].Position; verts[v * 3] = p.X; verts[v * 3 + 1] = p.Y; verts[v * 3 + 2] = p.Z; }
        dto.Vertices = verts;
        // 면마다 정점 루프·코너 UV·머티리얼을 기록한다(Compact 후라 모든 면이 살아 있다).
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
        // 하드/심 엣지를 정점 쌍으로 기록.
        var hard = new List<int[]>(); var seams = new List<int[]>();
        for (int e = 0; e < m.EdgeCount; e++)
        {
            var (a, b) = m.EdgeVertices(e);
            if (m.Edges[e].Hard) hard.Add(new[] { a, b });
            if (m.Edges[e].Seam) seams.Add(new[] { a, b });
        }
        dto.HardEdges = hard.ToArray(); dto.Seams = seams.ToArray();
        // 크리즈와 정점 잠금 노멀은 있을 때만 기록한다.
        var creases = new List<float[]>();
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Crease > 0f) { var (a, b) = m.EdgeVertices(e); creases.Add(new[] { a, b, m.Edges[e].Crease }); }
        if (creases.Count > 0) dto.Creases = creases.ToArray();
        if (m.LockedNormals.Count > 0) dto.LockedNormals = m.LockedNormals.Select(kv => new[] { kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Z }).ToArray();
        // 고정 코너 노멀은 (면, 코너 순번)으로 위치를 지정한다(하프에지 ID는 다시 읽을 때 달라질 수 있음).
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
        // UV 세트가 여럿(또는 이름을 바꾼 하나)이면 현재 Uv0을 세트에 먼저 저장(StoreCurrentUvs)한 뒤 세트마다 면별 코너 UV를 기록한다.
        // 세트가 하나뿐이어도 이름을 바꿨으면(UV Set Editor → Rename) 이름을 남기기 위해 기록한다.
        if (m.UvSets.Count > 1 || (m.UvSets.Count == 1 && m.UvSets[0].Name != "map1"))
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

    /// <summary>
    /// DTO에서 메시를 만든다. 정점 → 면(+코너 UV) → 엣지 플래그(정점 쌍으로 FindEdge) → 노멀 잠금 → 핀 → UV 세트 순서로 복원하고
    /// 마지막에 노멀을 다시 계산한다(고정·잠금 노멀은 Recompute가 유지한다).
    /// </summary>
    private static PolyMesh FromDto(MeshDto dto)
    {
        var m = new PolyMesh();
        for (int i = 0; i + 2 < dto.Vertices.Length; i += 3) m.AddVertex(new Vector3(dto.Vertices[i], dto.Vertices[i + 1], dto.Vertices[i + 2]));
        for (int f = 0; f < dto.Faces.Length; f++)
        {
            // 비매니폴드 등으로 면 추가가 실패하면 그 면은 버린다(이후 면 인덱스가 어긋날 수 있으나 정상 파일에서는 생기지 않음).
            int nf = m.AddFace(dto.Faces[f], f < dto.Materials.Length ? dto.Materials[f] : 0);
            if (nf < 0) continue;
            if (f < dto.Uvs.Length)
            {
                var uv = dto.Uvs[f];
                // AddFace는 첫 코너를 Faces[nf].HalfEdge로 두므로 루프를 따라 저장된 순서대로 UV를 넣는다.
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
                // 세트마다 하프에지 ID 크기의 UV 배열을 만들고 (면, 코너) 순서로 채운다.
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

    /// <summary>Vector3를 [x,y,z] 배열로.</summary>
    private static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
    /// <summary>[x,y,z] 배열을 Vector3로(배열이 없거나 짧으면 <paramref name="fallback"/>, 그것도 없으면 0).</summary>
    private static Vector3 V3(float[]? a, Vector3? fallback = null)
        => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : fallback ?? Vector3.Zero;
}
