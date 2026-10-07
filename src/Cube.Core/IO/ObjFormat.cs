using System.Globalization;
using System.Numerics;
using System.Text;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.IO;

/// <summary>OBJ에서 읽은 오브젝트 하나(<c>o</c>/<c>g</c> 그룹).</summary>
public sealed class ObjObject
{
    public string Name = "default";
    public PolyMesh Mesh = new();
    /// <summary>비매니폴드 등으로 추가하지 못해 건너뛴 면 수.</summary>
    public int SkippedFaces;
    /// <summary>파일에 이 오브젝트의 코너 노멀(vn)이 모두 있었는지. 없으면 <see cref="MeshNormals.Recompute"/>로 계산되어 있다.</summary>
    public bool HadNormals;
}

/// <summary>
/// Wavefront OBJ 읽기/쓰기. 외부 앱(RizomUV 등) 브리지용.
/// 쓰기: 노드마다 <c>o</c> 블록, 정점은 살아 있는 정점마다 <c>v</c> 하나(월드 베이크 선택), <c>vt</c>/<c>vn</c>은 노드 안에서 중복 제거,
/// <c>f</c>는 1-based 전역 인덱스 <c>v/vt/vn</c>이며 n각형을 그대로 쓴다(코너 순서 = 메시 루프 순서, CCW).
/// UV 원점은 OBJ와 코어 모두 좌하단이라 뒤집지 않는다. 좌표계 변환은 없다(내부 = Y-up, m).
/// </summary>
public static class ObjFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ Write

    public static void Write(string path, IEnumerable<SceneNode> nodes, bool worldSpace = true)
    {
        File.WriteAllText(path, WriteToString(nodes, worldSpace), new UTF8Encoding(false));
    }

    public static string WriteToString(IEnumerable<SceneNode> nodes, bool worldSpace = true)
    {
        var sb = new StringBuilder();
        sb.Append("# Cube OBJ export\n");
        int vBase = 0, vtBase = 0, vnBase = 0;
        var loop = new List<int>();
        var used = new HashSet<string>();
        foreach (var node in nodes)
        {
            var m = node.Mesh;
            if (m == null) continue;
            var world = worldSpace ? node.WorldMatrix : Matrix4x4.Identity;
            Matrix4x4 normalMat = Matrix4x4.Identity;
            if (worldSpace)
            {
                // 행벡터 규약: n' = n · (M^-1)^T
                normalMat = Matrix4x4.Invert(world, out var inv) ? Matrix4x4.Transpose(inv) : world;
            }

            string name = SanitizeName(node.Name);
            while (!used.Add(name)) name += "_";
            sb.Append("o ").Append(name).Append('\n');

            // v: 살아 있는 정점마다 하나
            var vIndex = new int[m.VertexCount];
            int nv = 0;
            for (int v = 0; v < m.VertexCount; v++)
            {
                if (!m.Verts[v].Alive) { vIndex[v] = -1; continue; }
                var p = worldSpace ? Vector3.Transform(m.Verts[v].Position, world) : m.Verts[v].Position;
                vIndex[v] = vBase + (++nv);
                sb.Append("v ").Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(' ').Append(F(p.Z)).Append('\n');
            }

            // vt / vn: 노드 내 중복 제거(텍스트 형태 기준)
            var vtMap = new Dictionary<string, int>();
            var vnMap = new Dictionary<string, int>();
            var heVt = new int[m.HalfEdgeCount];
            var heVn = new int[m.HalfEdgeCount];
            var vtLines = new StringBuilder();
            var vnLines = new StringBuilder();
            for (int f = 0; f < m.FaceCount; f++)
            {
                if (!m.Faces[f].Alive) continue;
                m.GetFaceHalfEdges(f, loop);
                foreach (int h in loop)
                {
                    var he = m.Hes[h];
                    string vt = F(he.Uv0.X) + " " + F(he.Uv0.Y);
                    if (!vtMap.TryGetValue(vt, out int vti)) { vti = vtBase + vtMap.Count + 1; vtMap[vt] = vti; vtLines.Append("vt ").Append(vt).Append('\n'); }
                    heVt[h] = vti;
                    var n = he.Normal;
                    if (worldSpace)
                    {
                        n = Vector3.TransformNormal(n, normalMat);
                        float len = n.Length();
                        if (len > 1e-12f) n /= len;
                    }
                    string vn = F(n.X) + " " + F(n.Y) + " " + F(n.Z);
                    if (!vnMap.TryGetValue(vn, out int vni)) { vni = vnBase + vnMap.Count + 1; vnMap[vn] = vni; vnLines.Append("vn ").Append(vn).Append('\n'); }
                    heVn[h] = vni;
                }
            }
            sb.Append(vtLines);
            sb.Append(vnLines);

            for (int f = 0; f < m.FaceCount; f++)
            {
                if (!m.Faces[f].Alive) continue;
                m.GetFaceHalfEdges(f, loop);
                sb.Append('f');
                foreach (int h in loop)
                {
                    sb.Append(' ').Append(vIndex[m.Hes[h].Vertex].ToString(Inv))
                      .Append('/').Append(heVt[h].ToString(Inv))
                      .Append('/').Append(heVn[h].ToString(Inv));
                }
                sb.Append('\n');
            }

            vBase += nv;
            vtBase += vtMap.Count;
            vnBase += vnMap.Count;
        }
        return sb.ToString();
    }

    private static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
        if (v == 0) v = 0; // -0 제거
        return v.ToString("R", Inv);
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "object";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsWhiteSpace(c) ? '_' : c);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Read

    public static List<ObjObject> Read(string path) => Parse(File.ReadAllLines(path));

    public static List<ObjObject> ReadFromString(string text) => Parse(text.Split('\n'));

    private sealed class Group
    {
        public ObjObject Obj = new();
        public readonly Dictionary<int, int> VertexMap = new(); // 전역 v 인덱스(0-based) → 로컬 정점 ID
        public bool AnyMissingNormal;
        public bool AnyNormal;
        public int FaceCount;
    }

    private static List<ObjObject> Parse(IEnumerable<string> lines)
    {
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();
        var groups = new List<Group>();
        Group? cur = null;
        var cornerV = new List<int>(); var cornerT = new List<int>(); var cornerN = new List<int>();

        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            // 줄 연속(\)은 지원하지 않음(실무 파일에서 드묾)
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            switch (parts[0])
            {
                case "v":
                    positions.Add(new Vector3(P(parts, 1), P(parts, 2), P(parts, 3)));
                    break;
                case "vt":
                    uvs.Add(new Vector2(P(parts, 1), P(parts, 2)));
                    break;
                case "vn":
                    normals.Add(new Vector3(P(parts, 1), P(parts, 2), P(parts, 3)));
                    break;
                case "o":
                case "g":
                {
                    string name = parts.Length > 1 ? string.Join(" ", parts, 1, parts.Length - 1) : "default";
                    if (cur != null && cur.FaceCount == 0) { cur.Obj.Name = name; break; } // 빈 그룹 이름만 바꿈(o 뒤의 g 등)
                    cur = new Group { Obj = new ObjObject { Name = name } };
                    groups.Add(cur);
                    break;
                }
                case "f":
                {
                    if (parts.Length < 4) break;
                    if (cur == null) { cur = new Group { Obj = new ObjObject { Name = "default" } }; groups.Add(cur); }
                    cornerV.Clear(); cornerT.Clear(); cornerN.Clear();
                    bool bad = false;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var tok = parts[i].Split('/');
                        int vi = Resolve(tok[0], positions.Count);
                        int ti = tok.Length > 1 ? Resolve(tok[1], uvs.Count) : -1;
                        int ni = tok.Length > 2 ? Resolve(tok[2], normals.Count) : -1;
                        if (vi < 0 || vi >= positions.Count) { bad = true; break; }
                        if (ti >= uvs.Count) ti = -1;
                        if (ni >= normals.Count) ni = -1;
                        cornerV.Add(vi); cornerT.Add(ti); cornerN.Add(ni);
                    }
                    if (bad) { cur.Obj.SkippedFaces++; break; }
                    AddFace(cur, positions, uvs, normals, cornerV, cornerT, cornerN);
                    break;
                }
                default:
                    break; // mtllib/usemtl/s/l/p 등은 무시
            }
        }

        var result = new List<ObjObject>(groups.Count);
        foreach (var g in groups)
        {
            if (g.FaceCount == 0 && g.Obj.Mesh.VertexCount == 0) continue;
            var m = g.Obj.Mesh;
            g.Obj.HadNormals = g.AnyNormal && !g.AnyMissingNormal;
            if (g.Obj.HadNormals)
            {
                // 면 노멀 캐시만 채운다(코너 노멀은 파일 값 유지)
                for (int f = 0; f < m.FaceCount; f++)
                {
                    if (!m.Faces[f].Alive) continue;
                    var n = MeshNormals.FaceNormalUnnormalized(m, f);
                    var face = m.Faces[f];
                    float len = n.Length();
                    face.Normal = len > 1e-12f ? n / len : Vector3.UnitY;
                    m.Faces[f] = face;
                }
                InferHardEdges(m);
            }
            else MeshNormals.Recompute(m);
            result.Add(g.Obj);
        }
        return result;
    }

    /// <summary>파일의 코너 노멀이 엣지 양쪽에서 갈라지면(각도 &gt; angleDeg) Edge.Hard로 표시한다(Blender 등에서 돌아온 OBJ의 샤프 엣지 유지).</summary>
    public static int InferHardEdges(PolyMesh m, float angleDeg = 1f)
    {
        float cosHard = MathF.Cos(angleDeg * MathF.PI / 180f);
        int hard = 0;
        for (int e = 0; e < m.EdgeCount; e++)
        {
            var ed = m.Edges[e];
            if (!ed.Alive || ed.He1 < 0) continue;
            var h0 = m.Hes[ed.He0]; var h1 = m.Hes[ed.He1];
            // 정점 a(h0 시작)에서의 두 코너: h0와 h1.Next
            var na = h0.Normal; var nb = m.Hes[h1.Next].Normal;
            var nc = m.Hes[h0.Next].Normal; var nd = h1.Normal;
            if (na.LengthSquared() < 1e-12f || nb.LengthSquared() < 1e-12f) continue;
            bool split = Vector3.Dot(Vector3.Normalize(na), Vector3.Normalize(nb)) < cosHard
                      || (nc.LengthSquared() > 1e-12f && nd.LengthSquared() > 1e-12f && Vector3.Dot(Vector3.Normalize(nc), Vector3.Normalize(nd)) < cosHard);
            if (split) { ed.Hard = true; m.Edges[e] = ed; hard++; }
        }
        return hard;
    }

    private static void AddFace(Group g, List<Vector3> positions, List<Vector2> uvs, List<Vector3> normals,
        List<int> cornerV, List<int> cornerT, List<int> cornerN)
    {
        var m = g.Obj.Mesh;
        int n = cornerV.Count;
        var ids = new int[n];
        for (int i = 0; i < n; i++)
        {
            int gi = cornerV[i];
            if (!g.VertexMap.TryGetValue(gi, out int local)) { local = m.AddVertex(positions[gi]); g.VertexMap[gi] = local; }
            ids[i] = local;
        }
        bool reversed = false;
        int f = m.AddFace(ids);
        if (f < 0)
        {
            Array.Reverse(ids);
            f = m.AddFace(ids);
            reversed = true;
        }
        if (f < 0) { g.Obj.SkippedFaces++; return; }
        g.FaceCount++;

        int he = m.Faces[f].HalfEdge;
        for (int k = 0; k < n; k++)
        {
            // 뒤집었으면 하프에지 k번째는 원래 코너 (n-1-k)번째 정점에서 출발
            int c = reversed ? n - 1 - k : k;
            var h = m.Hes[he];
            if (cornerT[c] >= 0) h.Uv0 = uvs[cornerT[c]];
            if (cornerN[c] >= 0) { h.Normal = normals[cornerN[c]]; g.AnyNormal = true; }
            else g.AnyMissingNormal = true;
            m.Hes[he] = h;
            he = h.Next;
        }
    }

    /// <summary>1-based(음수는 끝에서부터) 인덱스를 0-based로. 빈 토큰은 -1.</summary>
    private static int Resolve(string tok, int count)
    {
        if (tok.Length == 0) return -1;
        if (!int.TryParse(tok, NumberStyles.Integer, Inv, out int i)) return -1;
        if (i > 0) return i - 1;
        if (i < 0) return count + i;
        return -1;
    }

    private static float P(string[] parts, int i)
        => i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, Inv, out float v) ? v : 0f;
}

/// <summary>File → Export에서 쓰는 OBJ 내보내기(월드 공간 베이크, 선택 노드마다 <c>o</c>).</summary>
public sealed class ObjExporter : IExporter
{
    public string Name => "Wavefront OBJ";
    public IReadOnlyList<string> Extensions { get; } = new[] { ".obj" };

    public ExportResult Export(Document doc, IReadOnlyList<SceneNode> nodes, string path, ExportPreset preset)
    {
        var meshNodes = nodes.Where(n => n.Mesh != null).ToList();
        if (meshNodes.Count == 0) return ExportResult.Fail("Nothing to export (no mesh nodes).");
        try
        {
            ObjFormat.Write(path, meshNodes, worldSpace: true);
            int tris = 0;
            foreach (var n in meshNodes)
            {
                var m = n.Mesh!;
                for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive) tris += Math.Max(0, m.FaceDegree(f) - 2);
            }
            return new ExportResult(true, $"Exported {meshNodes.Count} node(s), {tris} triangles to {path} (OBJ)", meshNodes.Count, tris);
        }
        catch (Exception ex) { return ExportResult.Fail(ex.Message); }
    }
}

/// <summary>OBJ 가져오기: 오브젝트마다 메시 노드 하나(문서에 추가하지는 않음).</summary>
public sealed class ObjImporter : IImporter
{
    public string Name => "Wavefront OBJ";
    public IReadOnlyList<string> Extensions { get; } = new[] { ".obj" };

    public ImportResult Import(string path, Document doc, ImportOptions options)
    {
        try
        {
            var objs = ObjFormat.Read(path);
            if (objs.Count == 0) return ImportResult.Fail("No geometry in OBJ.");
            var nodes = new List<SceneNode>(objs.Count);
            foreach (var o in objs)
                nodes.Add(new SceneNode { Name = doc.UniqueName(o.Name), Shape = new MeshShape(o.Mesh) });
            int skipped = objs.Sum(o => o.SkippedFaces);
            string msg = $"Imported {nodes.Count} object(s) from {path}" + (skipped > 0 ? $" ({skipped} face(s) skipped)" : "");
            return new ImportResult(true, msg, nodes);
        }
        catch (Exception ex) { return ImportResult.Fail(ex.Message); }
    }
}
