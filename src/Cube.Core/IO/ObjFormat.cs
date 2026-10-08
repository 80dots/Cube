using System.Globalization;
using System.Numerics;
using System.Text;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.IO;

/// <summary>OBJ에서 읽은 오브젝트 하나(<c>o</c>/<c>g</c> 그룹).</summary>
/// <remarks>
/// <see cref="ObjFormat.Read"/>가 그룹마다 하나씩 만든다. 정점 ID는 그 그룹에서 처음 참조된 순서대로 배정된다.
/// </remarks>
public sealed class ObjObject
{
    /// <summary>오브젝트 이름(<c>o</c>/<c>g</c> 줄의 나머지 토큰; 없으면 "default").</summary>
    public string Name = "default";
    /// <summary>읽은 폴리곤 메시(정점은 파일 좌표 그대로, 코너 UV/노멀 포함).</summary>
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
    /// <summary>숫자 서식용 고정 문화권(소수점이 항상 '.'이 되도록).</summary>
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ Write

    /// <summary>노드들을 OBJ 텍스트로 만들어 BOM 없는 UTF-8 파일로 쓴다.</summary>
    /// <param name="path">출력 경로.</param>
    /// <param name="nodes">내보낼 노드(메시가 없는 노드는 건너뜀).</param>
    /// <param name="worldSpace">true면 정점·노멀을 월드 행렬로 베이크한다(브리지/내보내기 기본).</param>
    public static void Write(string path, IEnumerable<SceneNode> nodes, bool worldSpace = true)
    {
        File.WriteAllText(path, WriteToString(nodes, worldSpace), new UTF8Encoding(false));
    }

    /// <summary>
    /// 노드들을 OBJ 텍스트로 직렬화한다. 노드마다: <c>o 이름</c> → <c>v</c>(살아 있는 정점) → <c>vt</c>/<c>vn</c>(중복 제거) → <c>f</c>.
    /// v/vt/vn 인덱스는 파일 전역 1-based이므로 노드마다 앞 노드까지의 개수(vBase 등)를 더한다.
    /// 이름은 공백을 '_'로 바꾸고 중복이면 '_'를 덧붙여 유일하게 만든다(UvTransfer의 이름 짝짓기에 필요).
    /// </summary>
    public static string WriteToString(IEnumerable<SceneNode> nodes, bool worldSpace = true)
    {
        var sb = new StringBuilder();
        sb.Append("# Cube OBJ export\n");
        // 파일 전역 인덱스 오프셋(앞 노드들이 쓴 v/vt/vn 개수)과 재사용 버퍼, 이미 쓴 오브젝트 이름 집합.
        int vBase = 0, vtBase = 0, vnBase = 0;
        var loop = new List<int>();
        var used = new HashSet<string>();
        foreach (var node in nodes)
        {
            var m = node.Mesh;
            if (m == null) continue;
            // 월드 베이크 여부에 따라 정점 변환 행렬과 노멀 변환 행렬(역전치)을 준비한다.
            var world = worldSpace ? node.WorldMatrix : Matrix4x4.Identity;
            Matrix4x4 normalMat = Matrix4x4.Identity;
            if (worldSpace)
            {
                // 행벡터 규약: n' = n · (M^-1)^T
                normalMat = Matrix4x4.Invert(world, out var inv) ? Matrix4x4.Transpose(inv) : world;
            }

            // 오브젝트 이름(공백 제거 + 중복 시 '_' 접미).
            string name = SanitizeName(node.Name);
            while (!used.Add(name)) name += "_";
            sb.Append("o ").Append(name).Append('\n');

            // v: 살아 있는 정점마다 하나
            // vIndex[v] = 정점 슬롯 v의 파일 전역 1-based 인덱스(죽은 정점은 -1).
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
            // heVt/heVn[하프에지] = 그 코너가 참조할 vt/vn 전역 인덱스. 같은 텍스트(같은 값)면 같은 인덱스를 재사용한다.
            // vt/vn 줄은 별도 버퍼에 모았다가 v 다음에 한꺼번에 쓴다.
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
                    // 노멀은 역전치 행렬로 변환한 뒤 다시 정규화한다(비균등 스케일 대응).
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

            // f: 면 루프 순서(CCW) 그대로 v/vt/vn 삼중 인덱스를 쓴다. n각형도 삼각화하지 않는다.
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

            // 다음 노드를 위해 전역 인덱스 오프셋을 전진.
            vBase += nv;
            vtBase += vtMap.Count;
            vnBase += vnMap.Count;
        }
        return sb.ToString();
    }

    /// <summary>
    /// float을 왕복 가능한 최단 표현("R")으로 쓴다. NaN/무한대는 0, -0은 0으로 정규화한다
    /// (vt/vn 중복 제거가 텍스트 기준이라 -0과 0이 다른 값으로 취급되지 않게).
    /// </summary>
    private static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
        if (v == 0) v = 0; // -0 제거
        return v.ToString("R", Inv);
    }

    /// <summary>OBJ <c>o</c> 이름은 공백으로 토큰이 나뉘므로 공백 문자를 '_'로 바꾼다. 빈 이름은 "object".</summary>
    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "object";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsWhiteSpace(c) ? '_' : c);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Read

    /// <summary>OBJ 파일을 읽어 그룹(<c>o</c>/<c>g</c>)별 <see cref="ObjObject"/> 목록을 만든다.</summary>
    public static List<ObjObject> Read(string path) => Parse(File.ReadAllLines(path));

    /// <summary>OBJ 텍스트를 파싱한다(테스트/메모리 데이터용). CR은 각 줄 Trim으로 제거된다.</summary>
    public static List<ObjObject> ReadFromString(string text) => Parse(text.Split('\n'));

    /// <summary>파싱 중인 그룹 하나의 상태(만들고 있는 오브젝트, 전역→로컬 정점 맵, 노멀 유무 플래그).</summary>
    private sealed class Group
    {
        /// <summary>이 그룹이 만드는 결과 오브젝트.</summary>
        public ObjObject Obj = new();
        public readonly Dictionary<int, int> VertexMap = new(); // 전역 v 인덱스(0-based) → 로컬 정점 ID
        /// <summary>노멀 인덱스가 없는 코너가 하나라도 있었는지(있으면 파일 노멀을 버리고 재계산).</summary>
        public bool AnyMissingNormal;
        /// <summary>노멀 인덱스가 있는 코너가 하나라도 있었는지.</summary>
        public bool AnyNormal;
        /// <summary>이 그룹에 추가한 면 수(빈 그룹 판정용).</summary>
        public int FaceCount;
    }

    /// <summary>
    /// 줄 단위 파서. v/vt/vn은 파일 전역 목록에 쌓고, f는 현재 그룹 메시에 면으로 추가한다.
    /// 끝나면 그룹마다 노멀을 마무리한다: 모든 코너에 파일 노멀이 있으면 그대로 두고 면 노멀만 채운 뒤 하드 엣지를 추론,
    /// 아니면 <see cref="MeshNormals.Recompute"/>로 다시 계산한다.
    /// </summary>
    private static List<ObjObject> Parse(IEnumerable<string> lines)
    {
        // 파일 전역 v/vt/vn 목록(OBJ 인덱스는 그룹과 무관하게 파일 전체 기준).
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();
        var groups = new List<Group>();
        Group? cur = null;
        // 면 하나의 코너별 (위치, UV, 노멀) 0-based 인덱스 버퍼(-1 = 없음).
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
                    // 면이 아직 없는 그룹 뒤에 o/g가 또 오면 새 그룹을 만들지 않고 이름만 바꾼다.
                    if (cur != null && cur.FaceCount == 0) { cur.Obj.Name = name; break; } // 빈 그룹 이름만 바꿈(o 뒤의 g 등)
                    cur = new Group { Obj = new ObjObject { Name = name } };
                    groups.Add(cur);
                    break;
                }
                case "f":
                {
                    // 3코너 미만인 면은 무시. 그룹 선언 없이 면이 나오면 "default" 그룹을 만든다.
                    if (parts.Length < 4) break;
                    if (cur == null) { cur = new Group { Obj = new ObjObject { Name = "default" } }; groups.Add(cur); }
                    cornerV.Clear(); cornerT.Clear(); cornerN.Clear();
                    bool bad = false;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        // 코너 토큰 "v", "v/vt", "v//vn", "v/vt/vn"을 '/'로 나눠 각 인덱스를 0-based로 해석한다.
                        // 위치 인덱스가 범위 밖이면 면 전체를 버리고, UV/노멀 인덱스가 범위 밖이면 그 속성만 없는 것으로 본다.
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

        // 그룹마다 결과를 마무리한다(정점도 면도 없는 빈 그룹은 버림).
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
    /// <param name="m">대상 메시(엣지 Hard 플래그가 바뀐다).</param>
    /// <param name="angleDeg">이 각도보다 크게 갈라진 코너 노멀을 하드로 본다(기본 1°: 사실상 값이 다르면 하드).</param>
    /// <returns>하드로 표시한 엣지 수.</returns>
    public static int InferHardEdges(PolyMesh m, float angleDeg = 1f)
    {
        float cosHard = MathF.Cos(angleDeg * MathF.PI / 180f);
        int hard = 0;
        for (int e = 0; e < m.EdgeCount; e++)
        {
            var ed = m.Edges[e];
            // 경계 엣지는 상대 코너가 없으므로 판정하지 않는다.
            if (!ed.Alive || ed.He1 < 0) continue;
            var h0 = m.Hes[ed.He0]; var h1 = m.Hes[ed.He1];
            // 정점 a(h0 시작)에서의 두 코너: h0와 h1.Next
            var na = h0.Normal; var nb = m.Hes[h1.Next].Normal;
            // 정점 b(h0.Next 시작)에서의 두 코너: h0.Next와 h1.
            var nc = m.Hes[h0.Next].Normal; var nd = h1.Normal;
            if (na.LengthSquared() < 1e-12f || nb.LengthSquared() < 1e-12f) continue;
            // 엣지 양끝 중 한 곳에서라도 노멀이 갈라지면 하드.
            bool split = Vector3.Dot(Vector3.Normalize(na), Vector3.Normalize(nb)) < cosHard
                      || (nc.LengthSquared() > 1e-12f && nd.LengthSquared() > 1e-12f && Vector3.Dot(Vector3.Normalize(nc), Vector3.Normalize(nd)) < cosHard);
            if (split) { ed.Hard = true; m.Edges[e] = ed; hard++; }
        }
        return hard;
    }

    /// <summary>
    /// 파싱한 면 하나를 그룹 메시에 추가한다. 전역 정점 인덱스를 그룹 로컬 정점으로 바꾸고(처음 보면 새로 만듦),
    /// <c>AddFace</c>가 비매니폴드로 거부하면 감김을 뒤집어 한 번 더 시도한다. 그래도 실패하면 건너뛴 면으로 센다.
    /// 성공하면 코너마다 UV/노멀을 복사하고 노멀 유무 플래그를 갱신한다.
    /// </summary>
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
        // 이웃 면과 감김이 반대인 파일(일관되지 않은 OBJ)을 살리기 위해 역순으로 재시도한다.
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

        // AddFace는 첫 코너를 Faces[f].HalfEdge로 두므로 루프를 따라가며 코너 속성을 채운다.
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

    /// <summary>i번째 토큰을 float으로 읽는다(없거나 잘못되면 0).</summary>
    private static float P(string[] parts, int i)
        => i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, Inv, out float v) ? v : 0f;
}

/// <summary>File → Export에서 쓰는 OBJ 내보내기(월드 공간 베이크, 선택 노드마다 <c>o</c>).</summary>
/// <remarks>Undo·문서 변경 없이 파일만 쓴다. 메시가 없는 노드(조인트, 라이트)는 제외한다.</remarks>
public sealed class ObjExporter : IExporter
{
    /// <summary>형식 이름.</summary>
    public string Name => "Wavefront OBJ";
    /// <summary>지원 확장자(.obj).</summary>
    public IReadOnlyList<string> Extensions { get; } = new[] { ".obj" };

    /// <summary>메시 노드만 월드 베이크로 기록하고 삼각형 수(n각형은 n-2개)를 세어 결과에 담는다. 예외는 실패 결과로 바꾼다.</summary>
    public ExportResult Export(Document doc, IReadOnlyList<SceneNode> nodes, string path, ExportPreset preset)
    {
        using var restPose = Cube.Core.Scene.AnimationPose.RestScope(doc); // 재생 포즈가 아니라 rest(바인드) 포즈로 기록
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
/// <remarks>노드 이름은 문서에서 유일하게 만들고, 머티리얼은 읽지 않는다(usemtl 무시).</remarks>
public sealed class ObjImporter : IImporter
{
    /// <summary>형식 이름.</summary>
    public string Name => "Wavefront OBJ";
    /// <summary>지원 확장자(.obj).</summary>
    public IReadOnlyList<string> Extensions { get; } = new[] { ".obj" };

    /// <summary>OBJ를 읽어 오브젝트마다 <see cref="MeshShape"/>를 가진 새 <see cref="SceneNode"/>를 만든다(문서에는 넣지 않음).</summary>
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
