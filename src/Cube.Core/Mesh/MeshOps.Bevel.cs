using System.Numerics;

namespace Cube.Core.Mesh;

// Edges = 선택 엣지를 깎아 띠를 만든다(Ctrl+B), Vertices = 선택 정점의 모서리를 깎는다(Shift+Ctrl+B).
/// <summary>Bevel이 영향을 주는 요소(Blender Affect).</summary>
public enum BevelAffect { Edges, Vertices }

/// <summary>Width 값의 의미(Blender Width Type).</summary>
public enum BevelWidthType
{
    /// <summary>베벨 엣지에서 새 엣지까지의 수직 거리(면 위).</summary>
    Offset,
    /// <summary>베벨 면의 폭(새로 생긴 두 엣지 사이 거리).</summary>
    Width,
    /// <summary>원래 엣지에서 베벨 면까지의 수직 깊이.</summary>
    Depth,
    /// <summary>인접 엣지 길이의 백분율.</summary>
    Percent,
    /// <summary>인접 엣지를 따라 잰 거리.</summary>
    Absolute,
}

/// <summary>
/// 두 베벨 엣지가 한 면 코너에서 만날 때 그 코너 처리(Blender Miter).
/// Sharp = 두 오프셋 선의 교점 하나, Patch = 교점을 사이에 둔 꺾인 점 3개, Arc = 두 점 사이를 프로파일 곡선으로 잇는다.
/// </summary>
public enum BevelMiter { Sharp, Patch, Arc }

// GridFill = 가운데를 부풀린 쿼드/삼각 채움, Cutoff = 엣지별 막음 면 + 가운데 면, NGon = 둘레 전체를 다각형 하나로.
/// <summary>베벨 엣지가 3개 이상 모이는 정점의 채움(Blender Intersection Type). NGon은 하나의 다각형(Maya식).</summary>
public enum BevelIntersection { GridFill, Cutoff, NGon }

/// <summary>
/// 프로파일 종류. Superellipse = Shape 값으로 정한 초타원 곡선, Custom = <see cref="BevelProfilePreset"/> 꺾은선.
/// </summary>
public enum BevelProfileType { Superellipse, Custom }

// Default = 원호 근사, SupportLoops = 양끝에 보조 루프가 몰리는 형태, CorniceMolding/CrownMolding = 몰딩 단면, Steps = 계단.
/// <summary>Custom 프로파일 프리셋(Blender Profile Presets).</summary>
public enum BevelProfilePreset { Default, SupportLoops, CorniceMolding, CrownMolding, Steps }

// None = 노멀 고정 안 함, New = 새 면 기준, Affected = 새 면에 닿은 원래 면을 가장 세게, All = 원래 면 모두 가장 세게.
/// <summary>Weighted Normal용 면 세기(Blender Face Strength). Cube에는 Weighted Normal 모디파이어가 없으므로 그 결과(가중 노멀)를 코너 노멀로 바로 고정한다.</summary>
public enum BevelFaceStrength { None, New, Affected, All }

/// <summary>Blender Bevel(Ctrl+B / Shift+Ctrl+B)의 모든 옵션.</summary>
/// <remarks>
/// 불변 record라 옵션 창/이력 파라미터에서 with 식으로 복사해 쓴다. 기본값은 Blender 기본값과 같다.
/// </remarks>
public sealed record BevelOptions
{
    /// <summary>무엇을 베벨할지(엣지/정점). 입력 ID의 의미도 이것에 따라 바뀐다.</summary>
    public BevelAffect Affect { get; init; } = BevelAffect.Edges;
    /// <summary>Width 값을 어떻게 해석할지(<see cref="BevelWidthType"/>).</summary>
    public BevelWidthType WidthType { get; init; } = BevelWidthType.Offset;
    /// <summary>폭(m, Percent면 %). 1e-6 미만은 1e-6으로 올려 쓴다.</summary>
    public float Width { get; init; } = 0.1f;
    /// <summary>프로파일 세그먼트 수(1 = 평평한 면 하나, 내부에서 1..100으로 클램프).</summary>
    public int Segments { get; init; } = 1;
    /// <summary>프로파일 모양 0..1(0.5 = 원호, 0.25 = 직선, 1 = 각진 모서리, 0 = 오목).</summary>
    public float Shape { get; init; } = 0.5f;
    /// <summary>새 면의 머티리얼 슬롯(-1 = 이웃 면을 따름).</summary>
    public int MaterialIndex { get; init; } = -1;
    /// <summary>true면 띠 코너 노멀을 두 이웃 면 법선 사이로 고정하고 원래 면과의 경계를 하드로(<see cref="ApplyHardenNormals"/>).</summary>
    public bool HardenNormals { get; init; }
    /// <summary>true면 모든 이동 길이를 같은 비율로 줄여 이웃 엣지 끝을 넘지 않게 한다(겹침 방지).</summary>
    public bool ClampOverlap { get; init; } = true;
    /// <summary>true면 새 점이 인접 엣지를 따라 미끄러진다. false면 베벨 엣지에 수직으로 오프셋한다(Offset/Width/Depth 종류에서만).</summary>
    public bool LoopSlide { get; init; } = true;
    /// <summary>원래 심(UV seam)이었던 베벨 엣지의 표시를 띠 가장자리와 캡 둘레로 이어 준다.</summary>
    public bool MarkSeams { get; init; }
    /// <summary>원래 하드였던 베벨 엣지의 표시를 띠 가장자리와 캡 둘레로 이어 준다.</summary>
    public bool MarkSharp { get; init; }
    /// <summary>Outer Miter(반사각 코너, 면 바깥으로 꺾이는 쪽)의 처리.</summary>
    public BevelMiter MiterOuter { get; init; } = BevelMiter.Sharp;
    /// <summary>Inner Miter(Sharp/Arc; Patch는 Sharp로 취급).</summary>
    public BevelMiter MiterInner { get; init; } = BevelMiter.Sharp;
    /// <summary>Arc/Patch Inner Miter에서 교점 양쪽으로 벌리는 거리(m, 인접 엣지 길이의 45% 이내).</summary>
    public float Spread { get; init; } = 0.1f;
    /// <summary>베벨 엣지 3개 이상이 모이는 정점 캡의 채움 방식(세그먼트 2+에서만 의미).</summary>
    public BevelIntersection Intersection { get; init; } = BevelIntersection.GridFill;
    /// <summary>Weighted Normal 방식의 코너 노멀 고정 범위(<see cref="ApplyFaceStrength"/>).</summary>
    public BevelFaceStrength FaceStrength { get; init; } = BevelFaceStrength.None;
    /// <summary>프로파일 종류(초타원/Custom 프리셋).</summary>
    public BevelProfileType ProfileType { get; init; } = BevelProfileType.Superellipse;
    /// <summary>ProfileType = Custom일 때 쓸 프리셋.</summary>
    public BevelProfilePreset Preset { get; init; } = BevelProfilePreset.Default;
    /// <summary>Custom 프로파일 샘플링에서 남는 샘플을 구간마다 균등하게 배분(끄면 긴 구간부터).</summary>
    public bool SampleStraightEdges { get; init; }
    /// <summary>Custom 프로파일을 제어점 무시하고 전체 길이 기준 균등 간격으로 샘플링.</summary>
    public bool SampleEvenLengths { get; init; }
    /// <summary>둥근 Bevel 뒤 끝 면에 D자 캡을 합치고 60°로 스무딩(Maya식 마무리). 내부 호환용.</summary>
    /// <remarks>
    /// 실제 동작: true면 기존 <see cref="MeshOps.BevelEdges"/> API 호환 모드 — 전역 Clamp Overlap 대신 각 이동 길이를
    /// 그 엣지 길이의 45%로 따로 자른다(Maya식). 테스트 호환을 위해 internal로 남겨 두었다.
    /// </remarks>
    internal bool LegacyPerEdgeClamp { get; init; }
}

/// <summary>Blender식 Bevel(엣지/정점). 세그먼트·프로파일·폭 종류·마이터·교차 채움·노멀 처리.</summary>
public static partial class MeshOps
{
    /// <summary>
    /// 재구성할 면 루프의 한 항목(코너).
    /// </summary>
    /// <param name="Vertex">새 루프에서 쓸 정점(원래 정점 또는 새로 만든 오프셋 점).</param>
    /// <param name="Uv">코너 UV(원래 면 코너에서 아핀 보간).</param>
    /// <param name="Normal">원래 코너 노멀.</param>
    /// <param name="OriginEdge">이 점이 놓인 원래 엣지 ID. -1 = 원래 정점 그대로, -2 = 면 안쪽 점(교점/마이터).</param>
    /// <param name="OriginVertex">이 점을 만든 원래(베벨) 정점.</param>
    private sealed record BevelEntry(int Vertex, Vector2 Uv, Vector3 Normal, int OriginEdge, int OriginVertex);

    /// <summary>
    /// 기존 API(Maya식): 인접 엣지를 따라 distance만큼(Absolute) 물러나 엣지를 베벨한다. segments 2+는 원호 프로파일,
    /// 엣지가 3개 이상 모이는 정점은 다각형 하나로 채운다.
    /// </summary>
    public static List<int> BevelEdges(PolyMesh m, IEnumerable<int> edgeIds, float distance, int segments = 1)
        => Bevel(m, edgeIds, new BevelOptions { WidthType = BevelWidthType.Absolute, Width = distance, Segments = segments, Intersection = BevelIntersection.NGon, LegacyPerEdgeClamp = true });

    /// <summary>
    /// 면 재구성·D자 캡 병합 뒤 어떤 면에도 쓰이지 않는 정점이 남을 수 있다(반복 Bevel에서 고립 정점) → 죽인다.
    /// 하프에지를 한 번만 훑는다(정점마다 RemoveVertexIfIsolated를 부르면 O(V·H)).
    /// </summary>
    private static void RemoveIsolatedVertices(PolyMesh m)
    {
        var used = new bool[m.VertexCount];
        for (int h = 0; h < m.HalfEdgeCount; h++) { var he = m.Hes[h]; if (he.Alive && he.Vertex >= 0 && he.Vertex < used.Length) used[he.Vertex] = true; }
        bool any = false;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive || used[v]) continue;
            var vert = m.Verts[v]; vert.Alive = false; vert.HalfEdge = -1; m.Verts[v] = vert; any = true;
        }
        if (any) m.BumpTopology();
    }

    /// <summary>Bevel 실행. Affect = Edges면 ids는 엣지, Vertices면 정점 ID. 반환값은 새로 생긴 면 ID들.</summary>
    public static List<int> Bevel(PolyMesh m, IEnumerable<int> ids, BevelOptions o)
    {
        // 폭 0(또는 음수)이면 Blender처럼 아무것도 하지 않는다(전에는 넓이 0인 면이 생겼다, v0.0.57)
        if (!(o.Width > 0f)) return new List<int>();
        var info = new BevelInfo();
        // 1) Affect에 따라 엣지/정점 베벨 핵심 처리
        var result = o.Affect == BevelAffect.Vertices ? BevelVerticesCore(m, ids, o, info) : BevelEdgesCore(m, ids, o, info);
        if (result.Count == 0) { RemoveIsolatedVertices(m); return result; }
        // 2) 세그먼트 2+ 마무리: 엣지는 D자 캡 병합 + 60° 스무딩, 정점은 스무딩만
        if (o.Affect == BevelAffect.Edges && o.Segments >= 2) PostProcessRoundBevel(m, result, info.Strips);
        else if (o.Affect == BevelAffect.Vertices && o.Segments >= 2) SoftenNewFaces(m, result);
        // 병합으로 사라진 면 ID 제거
        result.RemoveAll(f => f < 0 || f >= m.FaceCount || !m.Faces[f].Alive);
        // 3) 노멀 고정 옵션: 기본 노멀을 먼저 계산한 뒤 그 위에 고정 노멀을 덮어쓴다
        if (o.HardenNormals || o.FaceStrength != BevelFaceStrength.None)
        {
            MeshNormals.Recompute(m);
            if (o.HardenNormals) ApplyHardenNormals(m, result, info);
            if (o.FaceStrength != BevelFaceStrength.None) ApplyFaceStrength(m, result, info, o.FaceStrength);
        }
        RemoveIsolatedVertices(m);
        m.BumpTopology();
        return result;
    }

    /// <summary>Bevel 중간 정보(노멀 처리에 씀).</summary>
    private sealed class BevelInfo
    {
        /// <summary>새로 생긴 띠(베벨 쿼드) 면 ID들. 캡 면과 구별해 노멀 처리/스무딩에 쓴다.</summary>
        public readonly HashSet<int> Strips = new();
        /// <summary>띠 면 → (면 A 법선, 면 B 법선, 정점 → 프로파일 위치 0..1).</summary>
        public readonly Dictionary<int, (Vector3 nA, Vector3 nB, Dictionary<int, float> t)> StripProfiles = new();
    }

    // ================================================================ 프로파일

    /// <summary>Shape(0..1) → 초타원 지수 r(0.5 → 2 = 원, 0.25 → 1 = 직선, 1 → ∞ = 각, 0 → 0 = 오목).</summary>
    internal static float SuperellipseExponent(float shape)
    {
        // 0과 1에서는 로그가 발산하므로 살짝 안쪽으로 자른다
        shape = System.Math.Clamp(shape, 0.001f, 0.999f);
        return MathF.Log(0.5f) / MathF.Log(MathF.Sqrt(shape));
    }

    /// <summary>프로파일 정의(단위 정사각 좌표: p0 = (1,0), p1 = (0,1), 모서리 K = (1,1), 안쪽 c = (0,0)).</summary>
    private sealed class ProfileSpec
    {
        /// <summary>초타원 지수 r(|x|^r + |y|^r = 1). 2 = 원호.</summary>
        public float R = 2f;
        /// <summary>Custom 프로파일의 제어점(단위 정사각 좌표, p0=(1,0) → p1=(0,1)). null이면 초타원.</summary>
        public List<Vector2>? Custom;
        /// <summary>Custom 샘플링 옵션(<see cref="BevelOptions.SampleEvenLengths"/>, <see cref="BevelOptions.SampleStraightEdges"/>).</summary>
        public bool EvenLengths, StraightEdges;

        /// <summary>옵션에서 프로파일 정의를 만든다(Custom이면 프리셋 꺾은선을 세그먼트 수에 맞춰 생성).</summary>
        public static ProfileSpec From(BevelOptions o)
        {
            var p = new ProfileSpec { R = SuperellipseExponent(o.Shape), EvenLengths = o.SampleEvenLengths, StraightEdges = o.SampleStraightEdges };
            if (o.ProfileType == BevelProfileType.Custom) p.Custom = PresetPoints(o.Preset, System.Math.Max(1, o.Segments));
            return p;
        }

        // r에서 초타원의 대각선 점 위치(0.5^(1/r))를 구해 -1..1 범위의 부풂 정도로 바꾼다. Custom은 고정 0.4.
        /// <summary>정사각 좌표 그리드 채움용: 대각선 방향으로 얼마나 모서리 쪽으로 부푸는가(-1..1).</summary>
        public float Bulge => Custom != null ? 0.4f : System.Math.Clamp(2f * MathF.Pow(0.5f, 1f / MathF.Max(R, 1e-3f)) - 1f, -0.5f, 1f);
    }

    /// <summary>Blender 프로파일 프리셋(단위 정사각 좌표의 꺾은선). Support Loops와 Steps는 세그먼트 수에 맞춰 만든다.</summary>
    internal static List<Vector2> PresetPoints(BevelProfilePreset preset, int segments)
    {
        var pts = new List<Vector2>();
        switch (preset)
        {
            case BevelProfilePreset.SupportLoops:
                {
                    // 양 끝 면 가까이에 보조 루프가 몰리는 각진 프로파일
                    int n = System.Math.Max(2, segments);
                    float r = SuperellipseExponent(0.85f);
                    for (int i = 0; i <= n; i++)
                    {
                        float t = 0.5f - 0.5f * MathF.Cos(MathF.PI * i / n); // 양 끝 조밀
                        float th = t * MathF.PI / 2;
                        pts.Add(new Vector2(MathF.Pow(MathF.Cos(th), 2f / r), MathF.Pow(MathF.Sin(th), 2f / r)));
                    }
                    pts[0] = new Vector2(1, 0); pts[^1] = new Vector2(0, 1);
                    break;
                }
            case BevelProfilePreset.Steps:
                {
                    // 계단 k단: 위로 올라갔다(면 A 방향) 안쪽으로 들어가기를 반복
                    int k = System.Math.Max(1, segments / 2);
                    pts.Add(new Vector2(1, 0));
                    for (int i = 0; i < k; i++)
                    {
                        float x = 1f - (float)i / k, y = (float)(i + 1) / k;
                        pts.Add(new Vector2(x, y));                 // 면 A 쪽으로 올라감
                        pts.Add(new Vector2(1f - (float)(i + 1) / k, y)); // 안쪽으로 들어감
                    }
                    if (pts[^1] != new Vector2(0, 1)) pts.Add(new Vector2(0, 1));
                    break;
                }
            case BevelProfilePreset.CorniceMolding:
                pts.AddRange(new Vector2[] { new(1, 0), new(1, 0.2f), new(0.82f, 0.3f), new(0.78f, 0.52f), new(0.55f, 0.62f), new(0.4f, 0.88f), new(0.2f, 0.95f), new(0, 1) });
                break;
            case BevelProfilePreset.CrownMolding:
                pts.AddRange(new Vector2[] { new(1, 0), new(0.9f, 0.12f), new(0.96f, 0.35f), new(0.72f, 0.5f), new(0.6f, 0.8f), new(0.32f, 0.88f), new(0.12f, 1), new(0, 1) });
                break;
            default:
                // Default: 원호에 가까운 부드러운 곡선(제어점 9개)
                for (int i = 0; i <= 8; i++) { float th = MathF.PI / 2 * i / 8; pts.Add(new Vector2(MathF.Cos(th), MathF.Sin(th))); }
                break;
        }
        return pts;
    }

    /// <summary>
    /// p0에서 p1까지(모서리 K 쪽으로 부푼) 프로파일의 중간 점 segments-1개. 좌표계: c = p0 + p1 − K, u = p0 − c, w = p1 − c,
    /// 점 = c + u·x + w·y. 초타원(|x|^r + |y|^r = 1)은 호 길이로 고르게, Custom은 프리셋 꺾은선을 샘플링한다. 세 점이 한 직선이면 선형.
    /// </summary>
    private static List<Vector3> ProfileMidPoints(Vector3 p0, Vector3 p1, Vector3 k, int segments, ProfileSpec spec)
    {
        // segments-1개의 중간 점만 돌려준다(양 끝 p0/p1은 호출자가 이미 가진 정점)
        var res = new List<Vector3>(System.Math.Max(0, segments - 1));
        if (segments < 2) return res;
        // c = 안쪽 기준점(p0+p1−K), u/w = c에서 두 끝점으로 가는 축. 축이 퇴화하거나 평행하면 직선 보간.
        var c = p0 + p1 - k; var u = p0 - c; var w = p1 - c;
        float ul = u.Length(), wl = w.Length();
        bool linear = ul < 1e-8f || wl < 1e-8f || Vector3.Cross(u, w).Length() < 1e-4f * ul * wl;
        if (linear)
        {
            for (int i = 1; i < segments; i++) res.Add(Vector3.Lerp(p0, p1, (float)i / segments));
            return res;
        }
        // 단위 정사각 좌표(x, y) → 3D 점
        Vector3 At(Vector2 xy) => c + u * xy.X + w * xy.Y;
        if (spec.Custom == null)
        {
            // 초타원 매개화: θ ∈ [0, π/2] → (cos^e θ, sin^e θ), e = 2/r
            float e = 2f / MathF.Max(spec.R, 1e-3f);
            Vector2 Se(float th) => new(MathF.Pow(MathF.Max(MathF.Cos(th), 0f), e), MathF.Pow(MathF.Max(MathF.Sin(th), 0f), e));
            // 곡선을 N=512 구간으로 촘촘히 샘플해 누적 호 길이 표를 만들고, 균등 호 길이 목표값마다 이분 탐색 + 선형 보간으로 θ를 구한다
            const int N = 512;
            var len = new float[N + 1]; var prev = At(Se(0));
            for (int i = 1; i <= N; i++) { var p = At(Se(MathF.PI / 2 * i / N)); len[i] = len[i - 1] + Vector3.Distance(prev, p); prev = p; }
            for (int s = 1; s < segments; s++)
            {
                float target = len[N] * s / segments;
                int j = System.Array.BinarySearch(len, target); if (j < 0) j = ~j;
                j = System.Math.Clamp(j, 1, N);
                float f = len[j] > len[j - 1] ? (target - len[j - 1]) / (len[j] - len[j - 1]) : 0f;
                res.Add(At(Se(MathF.PI / 2 * (j - 1 + f) / N)));
            }
            return res;
        }
        // Custom: 제어점을 3D로 옮긴 꺾은선을 샘플링하고 양 끝을 뺀 중간 점만 사용
        var cp = spec.Custom.Select(At).ToList();
        foreach (var p in SamplePolyline(cp, segments, spec.EvenLengths, spec.StraightEdges).Skip(1).Take(segments - 1)) res.Add(p);
        return res;
    }

    /// <summary>꺾은선을 segments 구간(segments+1 점)으로 샘플링. even = 전체 길이 균등, 아니면 제어점 우선(남는 샘플은 각 구간에 고르게, StraightEdges가 꺼져 있으면 긴 구간부터).</summary>
    private static List<Vector3> SamplePolyline(List<Vector3> cp, int segments, bool even, bool straightEdges)
    {
        // n = 구간 수. 제어점이 하나뿐이면 같은 점을 반복
        int n = cp.Count - 1;
        var outp = new List<Vector3>();
        if (n <= 0) { for (int i = 0; i <= segments; i++) outp.Add(cp[0]); return outp; }
        // seg[i] = i번째 구간 길이, total = 전체 길이
        var seg = new float[n]; float total = 0;
        for (int i = 0; i < n; i++) { seg[i] = Vector3.Distance(cp[i], cp[i + 1]); total += seg[i]; }
        // 균등 길이 모드: 전체 길이를 segments 등분한 목표 거리마다 해당 구간을 찾아 보간
        if (even || total < 1e-9f)
        {
            for (int s = 0; s <= segments; s++)
            {
                float target = total * s / segments; int i = 0;
                while (i < n - 1 && target > seg[i]) { target -= seg[i]; i++; }
                outp.Add(seg[i] > 1e-9f ? Vector3.Lerp(cp[i], cp[i + 1], System.Math.Clamp(target / seg[i], 0f, 1f)) : cp[i]);
            }
            return outp;
        }
        if (segments <= n)
        {
            // 제어점 일부만 사용(고르게 고름)
            for (int s = 0; s <= segments; s++) outp.Add(cp[(int)MathF.Round((float)s * n / segments)]);
            return outp;
        }
        // 제어점은 모두 쓰고 남는 샘플을 구간에 나눈다
        var count = Enumerable.Repeat(1, n).ToArray();
        int extra = segments - n;
        // StraightEdges: 남는 샘플을 구간에 돌아가며 1개씩. 아니면 (구간 길이 / 현재 샘플 수)가 가장 큰 구간에 하나씩 추가
        if (straightEdges) for (int k = 0; k < extra; k++) count[k % n]++;
        else for (int k = 0; k < extra; k++) { int best = 0; for (int i = 1; i < n; i++) if (seg[i] / count[i] > seg[best] / count[best]) best = i; count[best]++; }
        outp.Add(cp[0]);
        for (int i = 0; i < n; i++) for (int k = 1; k <= count[i]; k++) outp.Add(Vector3.Lerp(cp[i], cp[i + 1], (float)k / count[i]));
        return outp;
    }

    // ================================================================ 엣지 Bevel

    /// <summary>
    /// 엣지 Bevel 핵심. 알고리즘:
    /// ① 선택 엣지(경계 엣지 제외)의 끝점 집합 V와 영향 면(V에 닿은 면)의 하드/심/법선/머티리얼을 기록한다.
    /// ② 폭 종류에 따라 각 정점에서 인접 비베벨 엣지를 따라 물러날 거리(Slide)를 구하고 Clamp Overlap 비율을 적용한다.
    /// ③ 영향 면마다 코너를 새 루프로 바꾼다: 베벨 엣지가 없는 코너 = 양쪽 엣지 위 점 두 개, 한쪽만 베벨 = 비베벨 엣지 위 점 하나,
    ///    양쪽 모두 베벨 = 두 오프셋 선의 교점(Sharp) 또는 마이터 체인. 베벨 엣지 옆 점은 side[(면, 엣지, 정점)]에 기록한다.
    /// ④ 원래 면을 지우고 새 루프로 재생성(하드/심 복원) → 베벨 엣지마다 두 면 쪽 점 사이를 프로파일 점으로 이은 쿼드 띠를 만든다.
    /// ⑤ 정점마다 띠 끝/마이터 체인(capChains)을 고리로 이어 캡(NGon/GridFill/Cutoff)을 만든다.
    /// </summary>
    /// <returns>새로 생긴 면(띠 + 캡) ID.</returns>
    private static List<int> BevelEdgesCore(PolyMesh m, IEnumerable<int> edgeIds, BevelOptions o, BevelInfo info)
    {
        var result = new List<int>();
        // 경계 엣지는 한쪽 면만 있어 띠를 만들 수 없으므로 제외
        var selected = new HashSet<int>(edgeIds.Where(e => e >= 0 && e < m.EdgeCount && m.Edges[e].Alive && !m.IsBoundaryEdge(e)));
        if (selected.Count == 0) return result;
        int segments = System.Math.Clamp(o.Segments, 1, 100);
        var spec = ProfileSpec.From(o);
        float width = MathF.Max(o.Width, 1e-6f);

        // V = 베벨 엣지 끝점, pos = 원래 위치(정점 이동 전 스냅샷), selAt[v] = v에 닿은 선택 엣지들
        var V = new HashSet<int>();
        foreach (int e in selected) { var (a, b) = m.EdgeVertices(e); V.Add(a); V.Add(b); }
        var pos = new Dictionary<int, Vector3>();
        var selAt = new Dictionary<int, List<int>>();
        foreach (int v in V)
        {
            pos[v] = m.Verts[v].Position;
            var es = new List<int>(); m.GetVertexEdges(v, es);
            selAt[v] = es.Where(selected.Contains).ToList();
        }

        // 영향 면과 원래 플래그, 면 법선/머티리얼(제거 전에 기록)
        var affected = new List<int>();
        var tmp = new List<int>();
        foreach (int v in V) { m.GetVertexFaces(v, tmp); foreach (int f in tmp) if (!affected.Contains(f)) affected.Add(f); }
        var hardOf = new Dictionary<int, bool>();
        var seamOf = new Dictionary<int, bool>();
        var edgeVerts = new Dictionary<int, (int a, int b)>();
        var faceNormal = new Dictionary<int, Vector3>();
        var faceMat = new Dictionary<int, int>();
        foreach (int f in affected)
        {
            var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes) { int e = m.Hes[he].Edge; hardOf[e] = m.Edges[e].Hard; seamOf[e] = m.Edges[e].Seam; edgeVerts[e] = m.EdgeVertices(e); }
            var fn = MeshNormals.FaceNormalUnnormalized(m, f);
            faceNormal[f] = fn.LengthSquared() > 1e-18f ? Vector3.Normalize(fn) : Vector3.Zero;
            faceMat[f] = m.Faces[f].Material;
        }
        // selInfo: 선택 엣지마다 (엣지, He0 쪽 면 f0, He1 쪽 면 f1, He0 시작 정점 a, 끝 정점 b)
        var selInfo = new List<(int e, int f0, int f1, int a, int b)>();
        foreach (int e in selected)
        {
            var ed = m.Edges[e];
            selInfo.Add((e, m.Hes[ed.He0].Face, m.Hes[ed.He1].Face, m.Hes[ed.He0].Vertex, m.Hes[m.Hes[ed.He0].Next].Vertex));
        }
        var facesOfSel = selInfo.ToDictionary(x => x.e, x => (x.f0, x.f1));

        // 로컬 헬퍼: v→other 단위 방향, 엣지의 반대쪽 끝, 엣지 길이
        Vector3 Dir(int v, int other) { var d = m.Verts[other].Position - m.Verts[v].Position; float l = d.Length(); return l > 1e-12f ? d / l : Vector3.Zero; }
        int OtherEnd(int e, int v) { var (a, b) = m.EdgeVertices(e); return a == v ? b : a; }
        float EdgeLen(int e) { var (a, b) = m.EdgeVertices(e); return Vector3.Distance(m.Verts[a].Position, m.Verts[b].Position); }

        // 엣지별 면 위 수직 오프셋(Offset/Width/Depth를 오프셋으로 환산; 두 면 사이 내각 α 사용)
        float OffsetOf(int e)
        {
            if (!facesOfSel.TryGetValue(e, out var ff)) return width;
            var n0 = faceNormal[ff.f0]; var n1 = faceNormal[ff.f1];
            float between = MathF.Acos(System.Math.Clamp(Vector3.Dot(n0, n1), -1f, 1f)); // 법선 사이 각
            float alpha = MathF.PI - between;                                               // 면 사이 내각
            return o.WidthType switch
            {
                BevelWidthType.Width => width / MathF.Max(2f * MathF.Sin(alpha / 2f), 0.05f),
                BevelWidthType.Depth => width / MathF.Max(MathF.Cos(alpha / 2f), 0.05f),
                _ => width,
            };
        }
        // Absolute/Percent는 "엣지를 따라 잰 거리"라서 면 위 수직 오프셋 계산을 쓰지 않는다
        bool slideTypes = o.WidthType is BevelWidthType.Absolute or BevelWidthType.Percent;

        // 비베벨 엣지 eu를 따라 v에서 물러나는 길이(클램프 전). 관련 베벨 엣지는 같은 면에서 이웃한 것 우선.
        int RelatedSel(int v, int eu, int f)
        {
            var list = selAt[v];
            if (list.Count == 1) return list[0];
            if (f >= 0)
            {
                var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
                foreach (int he in hes) { int e = m.Hes[he].Edge; if (e != eu && list.Contains(e)) { var (a, b) = m.EdgeVertices(e); if (a == v || b == v) return e; } }
            }
            var du = Dir(v, OtherEnd(eu, v)); int best = list[0]; float bs = -1;
            foreach (int es in list) { float s = Vector3.Cross(du, Dir(v, OtherEnd(es, v))).Length(); if (s > bs) { bs = s; best = es; } }
            return best;
        }
        // 비베벨 엣지 eu를 따라 v에서 물러날 길이: 오프셋 선(베벨 엣지와 평행, 거리 Offset)과 eu의 교점까지 = Offset / sin(두 엣지 사이 각)
        float RawSlide(int v, int eu, int f)
        {
            float len = EdgeLen(eu);
            if (o.WidthType == BevelWidthType.Absolute) return width;
            if (o.WidthType == BevelWidthType.Percent) return len * width / 100f;
            int es = RelatedSel(v, eu, f);
            float sin = Vector3.Cross(Dir(v, OtherEnd(eu, v)), Dir(v, OtherEnd(es, v))).Length();
            // 베벨 엣지와 거의 일직선으로 이어지는 엣지(차수 2 끝점 등): 오프셋 선이 그 엣지와 만나지 않으므로 물러나지 않는다(띠가 끝점에서 뾰족하게 끝남).
            // 예전에는 sin을 0.05로 잘라 이어진 엣지를 거의 끝까지(98%) 미끄러졌다.
            if (sin < 0.05f) return 0f;
            return OffsetOf(es) / sin;
        }

        // Clamp Overlap: 모든 이동 길이를 같은 비율로 줄여 이웃 엣지 끝을 넘지 않게(양끝이 모두 베벨이면 절반까지)
        // clamp = 전역 축소 비율(1 = 그대로). 각 정점의 모든 엣지에 대해 이동 길이 L이 한계(엣지 길이의 98%, 반대쪽도 베벨이면 49%)를 넘으면 줄인다.
        float clamp = 1f;
        if (o.ClampOverlap && !o.LegacyPerEdgeClamp)
        {
            var ev = new List<int>();
            foreach (int v in V)
            {
                m.GetVertexEdges(v, ev);
                foreach (int e in ev)
                {
                    float len = EdgeLen(e); if (len < 1e-9f) continue;
                    bool otherBev = V.Contains(OtherEnd(e, v));
                    float L = selected.Contains(e) ? (slideTypes ? (o.WidthType == BevelWidthType.Percent ? len * width / 100f : width) : OffsetOf(e)) : RawSlide(v, e, -1);
                    if (selected.Contains(e) && selAt[v].Count < 2) continue; // 베벨 엣지 자체를 따라 물러나지 않는 끝
                    float limit = len * (otherBev ? 0.49f : 0.98f);
                    if (L > limit) clamp = MathF.Min(clamp, limit / L);
                }
            }
        }
        // 최종 이동 길이(클램프 적용, Legacy면 엣지별 45% 제한). 0이 되지 않도록 아주 작은 최소값.
        float Slide(int v, int eu, int f)
        {
            float L = RawSlide(v, eu, f) * clamp;
            if (o.LegacyPerEdgeClamp) L = MathF.Min(L, EdgeLen(eu) * 0.45f);
            return MathF.Max(L, 1e-6f);
        }
        // 클램프가 적용된 면 위 오프셋 거리
        float Off(int e) => OffsetOf(e) * clamp;

        // pOnEdge: (정점, 엣지) → 그 엣지 위 새 점(면 둘이 공유). qOnFace: (정점, 면) → 면 안쪽 교점.
        // uvOf: 새 정점의 대표 UV(캡 면 UV용). side: (면, 베벨 엣지, 정점) → 띠 가장자리 점과 UV.
        // capChains: 정점별 캡 둘레 조각(점 체인, 소유 베벨 엣지 또는 -1).
        var pOnEdge = new Dictionary<(int v, int e), int>();
        var qOnFace = new Dictionary<(int v, int f), int>();
        var uvOf = new Dictionary<int, Vector2>();
        var side = new Dictionary<(int f, int e, int v), (int vert, Vector2 uv)>();
        var capChains = new Dictionary<int, List<(List<int> pts, int owner)>>();
        foreach (int v in V) capChains[v] = new List<(List<int>, int)>();

        // 면 코너 (c, prev, next)에서 위치 → UV(두 엣지 방향의 아핀 좌표로 보간)
        // 코너 c 기준 r = a·dp + b·dn 를 최소제곱(2×2 정규방정식)으로 풀어 같은 계수로 UV를 보간한다
        Vector2 UvAt(Vector3 p, MeshOps.Corner c, MeshOps.Corner prev, MeshOps.Corner next)
        {
            var pc = m.Verts[c.Vertex].Position;
            var dp = m.Verts[prev.Vertex].Position - pc; var dn = m.Verts[next.Vertex].Position - pc; var r = p - pc;
            float a11 = Vector3.Dot(dp, dp), a12 = Vector3.Dot(dp, dn), a22 = Vector3.Dot(dn, dn);
            float b1 = Vector3.Dot(r, dp), b2 = Vector3.Dot(r, dn);
            float det = a11 * a22 - a12 * a12;
            if (MathF.Abs(det) < 1e-14f) return c.Uv;
            float x = (b1 * a22 - b2 * a12) / det, y = (a11 * b2 - a12 * b1) / det;
            return c.Uv + (prev.Uv - c.Uv) * x + (next.Uv - c.Uv) * y;
        }

        // v에서 엣지 e(반대쪽 other)를 따라 물러난 새 점을 만들거나 캐시에서 꺼낸다. f는 Loop Slide 끔 계산에 쓰는 면.
        int P(int v, int e, int other, int f)
        {
            if (pOnEdge.TryGetValue((v, e), out int id)) return id;
            var pv = pos[v]; var du = Dir(v, other);
            float L = Slide(v, e, f);
            Vector3 p = pv + du * L;
            if (!o.LoopSlide && !slideTypes && f >= 0)
            {
                // Loop Slide 끔: 새 엣지가 베벨 엣지에 수직(면 위에서 오프셋만큼 수직으로)
                int es = RelatedSel(v, e, f);
                var ds = Dir(v, OtherEnd(es, v));
                var perp = Vector3.Cross(faceNormal[f], ds);
                if (perp.LengthSquared() > 1e-12f)
                {
                    perp = Vector3.Normalize(perp);
                    if (Vector3.Dot(perp, du) < 0) perp = -perp;
                    p = pv + perp * Off(es);
                }
            }
            id = m.AddVertex(p);
            pOnEdge[(v, e)] = id;
            return id;
        }

        // rebuilt: 재구성할 (원래 면, 새 코너 루프, 머티리얼)
        var rebuilt = new List<(int face, List<BevelEntry> loop, int material)>();
        foreach (int f in affected)
        {
            var corners = CaptureCorners(m, f);
            int n = corners.Count;
            var nf = faceNormal[f];
            var loop = new List<BevelEntry>();
            for (int i = 0; i < n; i++)
            {
                var c = corners[i]; var prev = corners[(i + n - 1) % n]; var next = corners[(i + 1) % n];
                // 베벨 정점이 아닌 코너는 그대로
                if (!V.Contains(c.Vertex)) { loop.Add(new BevelEntry(c.Vertex, c.Uv, c.Normal, -1, c.Vertex)); continue; }
                int ePrev = m.FindEdge(prev.Vertex, c.Vertex), eNext = m.FindEdge(c.Vertex, next.Vertex);
                bool sp = selected.Contains(ePrev), sn = selected.Contains(eNext);
                // first: 이 코너에서 추가한 첫 항목 인덱스(side 기록용)
                int first = loop.Count;
                // 경우 1: 이 코너의 두 엣지 모두 베벨 아님(베벨 정점의 다른 면) → 두 엣지 위 점으로 모서리를 잘라 냄, 캡 조각은 p2→p1
                if (!sp && !sn)
                {
                    int p1 = P(c.Vertex, ePrev, prev.Vertex, f), p2 = P(c.Vertex, eNext, next.Vertex, f);
                    var uv1 = UvAt(m.Verts[p1].Position, c, prev, next); var uv2 = UvAt(m.Verts[p2].Position, c, prev, next);
                    loop.Add(new BevelEntry(p1, uv1, c.Normal, ePrev, c.Vertex)); loop.Add(new BevelEntry(p2, uv2, c.Normal, eNext, c.Vertex));
                    uvOf.TryAdd(p1, uv1); uvOf.TryAdd(p2, uv2);
                    capChains[c.Vertex].Add((new List<int> { p2, p1 }, -1));
                }
                // 경우 2: 한쪽만 베벨 → 비베벨 엣지 위 점 하나가 띠 가장자리가 된다
                else if (sp != sn)
                {
                    int eu = sp ? eNext : ePrev; var other = sp ? next : prev;
                    int pp = P(c.Vertex, eu, other.Vertex, f);
                    var uvp = UvAt(m.Verts[pp].Position, c, prev, next);
                    loop.Add(new BevelEntry(pp, uvp, c.Normal, eu, c.Vertex)); uvOf.TryAdd(pp, uvp);
                }
                else
                {
                    // 두 베벨 엣지 사이의 코너: 두 오프셋 선의 교점(Sharp) 또는 마이터
                    var pc = pos[c.Vertex];
                    var dp = m.Verts[prev.Vertex].Position - pc; var dn = m.Verts[next.Vertex].Position - pc;
                    float lp = dp.Length(), ln = dn.Length();
                    var dpH = lp > 1e-12f ? dp / lp : Vector3.Zero; var dnH = ln > 1e-12f ? dn / ln : Vector3.Zero;
                    Vector3 q;
                    // 면 안쪽 법선(왼쪽): prev→c 방향과 c→next 방향 기준
                    var Lp = Vector3.Cross(nf, -dpH); var Ln = Vector3.Cross(nf, dnH);
                    float oP = Off(ePrev), oN = Off(eNext);
                    // Absolute/Percent: 두 엣지를 따라 각각 비율만큼 간 벡터 합(평행사변형 꼭짓점)
                    if (slideTypes)
                    {
                        float fp = o.WidthType == BevelWidthType.Percent ? width / 100f : MathF.Min(width * clamp / MathF.Max(lp, 1e-9f), 1f);
                        float fn2 = o.WidthType == BevelWidthType.Percent ? width / 100f : MathF.Min(width * clamp / MathF.Max(ln, 1e-9f), 1f);
                        if (o.WidthType == BevelWidthType.Percent) { fp *= clamp; fn2 *= clamp; }
                        if (o.LegacyPerEdgeClamp) { fp = MathF.Min(width, lp * 0.45f) / MathF.Max(lp, 1e-9f); fn2 = MathF.Min(width, ln * 0.45f) / MathF.Max(ln, 1e-9f); }
                        q = pc + dp * fp + dn * fn2;
                    }
                    else
                    {
                        // q − c = a·dpH + b·dnH,  (q−c)·Lp = oP,  (q−c)·Ln = oN
                        float m11 = Vector3.Dot(dpH, Lp), m12 = Vector3.Dot(dnH, Lp), m21 = Vector3.Dot(dpH, Ln), m22 = Vector3.Dot(dnH, Ln);
                        float det = m11 * m22 - m12 * m21;
                        if (MathF.Abs(det) > 1e-5f) { float a = (oP * m22 - m12 * oN) / det, b = (m11 * oN - m21 * oP) / det; q = pc + dpH * a + dnH * b; }
                        else q = pc + (Lp * oP + Ln * oN) * 0.5f; // 거의 일직선
                    }
                    // reflex: 면 안에서 이 코너가 반사각(>180°)이면 Outer 마이터, 아니면 Inner 마이터 적용
                    bool reflex = Vector3.Dot(Vector3.Cross(dnH, dpH), nf) < -1e-6f;
                    var miter = reflex ? o.MiterOuter : (o.MiterInner == BevelMiter.Patch ? BevelMiter.Sharp : o.MiterInner);
                    if (miter == BevelMiter.Sharp)
                    {
                        if (!qOnFace.TryGetValue((c.Vertex, f), out int qi)) { qi = m.AddVertex(q); qOnFace[(c.Vertex, f)] = qi; }
                        var uvq = UvAt(q, c, prev, next);
                        loop.Add(new BevelEntry(qi, uvq, c.Normal, -2, c.Vertex)); uvOf.TryAdd(qi, uvq);
                    }
                    else
                    {
                        // 마이터: q1/q2 두 점 사이를 Patch(교점 q 경유) 또는 Arc(프로파일)로 잇고, 체인 역순을 캡 조각으로 추가
                        Vector3 q1, q2;
                        if (!reflex) { float sp2 = MathF.Max(o.Spread, 1e-4f); q1 = q + dpH * MathF.Min(sp2, lp * 0.45f); q2 = q + dnH * MathF.Min(sp2, ln * 0.45f); }
                        else { q1 = pc + Lp * oP; q2 = pc + Ln * oN; }
                        var chain = new List<Vector3> { q1 };
                        if (miter == BevelMiter.Patch) chain.Add(q);
                        else chain.AddRange(ProfileMidPoints(q1, q2, q, System.Math.Max(2, segments), spec));
                        chain.Add(q2);
                        var ids = new List<int>();
                        foreach (var cp in chain)
                        {
                            int id = m.AddVertex(cp); var uv = UvAt(cp, c, prev, next);
                            ids.Add(id); uvOf.TryAdd(id, uv);
                            loop.Add(new BevelEntry(id, uv, c.Normal, -2, c.Vertex));
                        }
                        ids.Reverse();
                        capChains[c.Vertex].Add((ids, -1));
                    }
                }
                // 베벨 엣지 옆 점 기록: prev 쪽 베벨 엣지는 이 코너의 첫 항목, next 쪽은 마지막 항목
                if (sp) side[(f, ePrev, c.Vertex)] = (loop[first].Vertex, loop[first].Uv);
                if (sn) side[(f, eNext, c.Vertex)] = (loop[^1].Vertex, loop[^1].Uv);
            }
            rebuilt.Add((f, loop, m.Faces[f].Material));
        }

        // 원래 면 제거(정점은 프로파일 K 계산에 필요하므로 아직 지우지 않음)
        foreach (var (f, _, _) in rebuilt) m.RemoveFace(f, removeIsolated: false);

        // 새 루프의 두 항목 x→y 사이 엣지가 원래 어느 엣지 위에 놓였는지 추적해 그 엣지의 하드 여부를 돌려준다
        bool HardBetween(BevelEntry x, BevelEntry y)
        {
            if (x.OriginEdge >= 0 && (y.OriginEdge == x.OriginEdge || (y.OriginEdge == -1 && edgeVerts.TryGetValue(x.OriginEdge, out var ev) && (ev.a == y.Vertex || ev.b == y.Vertex)))) return hardOf[x.OriginEdge];
            if (y.OriginEdge >= 0 && x.OriginEdge == -1 && edgeVerts.TryGetValue(y.OriginEdge, out var ev2) && (ev2.a == x.Vertex || ev2.b == x.Vertex)) return hardOf[y.OriginEdge];
            if (x.OriginEdge == -1 && y.OriginEdge == -1)
                foreach (var (e, (a, b)) in edgeVerts) if ((a == x.Vertex && b == y.Vertex) || (a == y.Vertex && b == x.Vertex)) return hardOf[e];
            return false;
        }
        // HardBetween과 같은 규칙으로 심 플래그를 추적
        bool SeamBetween(BevelEntry x, BevelEntry y)
        {
            if (x.OriginEdge >= 0 && (y.OriginEdge == x.OriginEdge || (y.OriginEdge == -1 && edgeVerts.TryGetValue(x.OriginEdge, out var ev) && (ev.a == y.Vertex || ev.b == y.Vertex)))) return seamOf[x.OriginEdge];
            if (y.OriginEdge >= 0 && x.OriginEdge == -1 && edgeVerts.TryGetValue(y.OriginEdge, out var ev2) && (ev2.a == x.Vertex || ev2.b == x.Vertex)) return seamOf[y.OriginEdge];
            if (x.OriginEdge == -1 && y.OriginEdge == -1)
                foreach (var (e, (a, b)) in edgeVerts) if ((a == x.Vertex && b == y.Vertex) || (a == y.Vertex && b == x.Vertex)) return seamOf[e];
            return false;
        }

        // 새 루프로 면 재생성: 연속 중복 정점과 끝-처음 중복 제거 후 3각 이상만
        foreach (var (_, loop, material) in rebuilt)
        {
            var clean = new List<BevelEntry>();
            foreach (var en in loop) if (clean.Count == 0 || clean[^1].Vertex != en.Vertex) clean.Add(en);
            if (clean.Count > 1 && clean[0].Vertex == clean[^1].Vertex) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 3) continue;
            int nf = AddFaceWithCorners(m, clean.Select(e => new Corner(e.Vertex, e.Uv, e.Normal)).ToList(), material);
            if (nf < 0) continue;
            for (int i = 0; i < clean.Count; i++)
            {
                var x = clean[i]; var y = clean[(i + 1) % clean.Count];
                SetHard(m, x.Vertex, y.Vertex, HardBetween(x, y));
                if (SeamBetween(x, y)) SetEdgeSeam(m, x.Vertex, y.Vertex, true);
            }
        }

        // 프로파일 공유: 같은 정점에서 같은 두 끝점을 쓰는 프로파일(엣지 루프)은 한 번만 만든다(틈 면 방지).
        var profCache = new Dictionary<(int v, int lo, int hi), int[]>();
        // 정점 v에서 베벨 엣지 e의 프로파일(segments+1 점): 양 끝 s0/s1 사이 중간 점을 (v, 작은 ID, 큰 ID) 키로 캐시해 이웃 띠와 공유
        (int[] verts, Vector2[] uvs) Profile(int v, int e, (int vert, Vector2 uv) s0, (int vert, Vector2 uv) s1)
        {
            var verts = new int[segments + 1]; var uvs = new Vector2[segments + 1];
            verts[0] = s0.vert; uvs[0] = s0.uv; verts[segments] = s1.vert; uvs[segments] = s1.uv;
            if (segments == 1) return (verts, uvs);
            if (s0.vert == s1.vert) { for (int k = 1; k < segments; k++) { verts[k] = s0.vert; uvs[k] = s0.uv; } return (verts, uvs); }
            // 방향(fwd)에 무관하게 같은 키를 쓰고 꺼낼 때 순서를 맞춘다
            bool fwd = s0.vert < s1.vert;
            var key = (v, fwd ? s0.vert : s1.vert, fwd ? s1.vert : s0.vert);
            if (!profCache.TryGetValue(key, out var mids))
            {
                var plo = m.Verts[key.Item2].Position; var phi = m.Verts[key.Item3].Position;
                // 모서리 K = 두 끝점 중점을 원래 베벨 엣지 직선에 내린 점
                var (ea, eb) = edgeVerts[e];
                var la = m.Verts[ea].Position; var lb = m.Verts[eb].Position; // 원래 정점은 아직 지우지 않았다
                var mid = (plo + phi) * 0.5f; var ld = lb - la; float ll = ld.LengthSquared();
                var k = ll > 1e-18f ? la + ld * (Vector3.Dot(mid - la, ld) / ll) : pos[v];
                var pts = ProfileMidPoints(plo, phi, k, segments, spec);
                mids = new int[segments - 1];
                for (int i = 0; i < segments - 1; i++) mids[i] = m.AddVertex(pts[i]);
                profCache[key] = mids;
            }
            for (int k = 1; k < segments; k++)
            {
                verts[k] = fwd ? mids[k - 1] : mids[segments - 1 - k];
                var uv = Vector2.Lerp(s0.uv, s1.uv, (float)k / segments);
                uvs[k] = uv; uvOf.TryAdd(verts[k], uv);
            }
            return (verts, uvs);
        }

        // 베벨 쿼드 띠
        // 띠 머티리얼: 옵션 지정값, 아니면 f0(없으면 f1) 머티리얼
        int Mat(int f0, int f1) => o.MaterialIndex >= 0 ? o.MaterialIndex : faceMat.GetValueOrDefault(f0, faceMat.GetValueOrDefault(f1));
        foreach (var (e, f0, f1, a, b) in selInfo)
        {
            // 네 모서리(두 면 × 두 끝점)의 띠 가장자리 점이 모두 있어야 띠를 만든다
            if (!side.TryGetValue((f0, e, a), out var a0) || !side.TryGetValue((f0, e, b), out var b0) ||
                !side.TryGetValue((f1, e, a), out var a1) || !side.TryGetValue((f1, e, b), out var b1)) continue;
            // 양 끝이 모두 한 점으로 모이면 띠 넓이가 0이라 건너뜀
            if (a0.vert == a1.vert && b0.vert == b1.vert) continue;
            var (pa, uva) = Profile(a, e, a0, a1);
            var (pb, uvb) = Profile(b, e, b0, b1);
            // 프로파일 k번째와 k+1번째 점을 이어 쿼드를 만든다. t는 프로파일 위치(Harden Normals 보간용).
            for (int k = 0; k < segments; k++)
            {
                var quad = new List<Corner> { new(pb[k], uvb[k], Vector3.Zero), new(pa[k], uva[k], Vector3.Zero), new(pa[k + 1], uva[k + 1], Vector3.Zero), new(pb[k + 1], uvb[k + 1], Vector3.Zero) };
                // 끝점 한쪽이 한 점으로 모이면(차수 2 끝점: 두 면의 옆 정점이 같음) 연속 중복 정점을 빼 삼각형으로 — 그대로 넘기면 면 추가가 거부되어 구멍이 났다
                for (int qi = quad.Count - 1; qi >= 0 && quad.Count > 0; qi--) if (quad[qi].Vertex == quad[(qi + 1) % quad.Count].Vertex) quad.RemoveAt(qi);
                if (quad.Count < 3 || quad.Select(c => c.Vertex).Distinct().Count() < 3) continue;
                int q = AddFaceWithCorners(m, quad, Mat(f0, f1));
                if (q < 0) continue;
                result.Add(q); info.Strips.Add(q);
                var t = new Dictionary<int, float> { [pb[k]] = (float)k / segments, [pa[k]] = (float)k / segments, [pa[k + 1]] = (float)(k + 1) / segments, [pb[k + 1]] = (float)(k + 1) / segments };
                info.StripProfiles[q] = (faceNormal[f0], faceNormal[f1], t);
            }
            // Mark Seams/Sharp: 표시된 엣지는 띠의 면 f0 쪽 가장자리로 이어 간다(경로가 끊기지 않게)
            if (o.MarkSeams && seamOf.GetValueOrDefault(e)) SetEdgeSeam(m, pa[0], pb[0], true);
            if (o.MarkSharp && hardOf.GetValueOrDefault(e)) SetHard(m, pa[0], pb[0], true);
            // 띠의 양 끝 프로파일을 캡 조각으로 등록(정점 a 쪽은 역순으로 해서 캡 고리 방향을 맞춤)
            var ca = new List<int>(); for (int k = segments; k >= 0; k--) ca.Add(pa[k]);
            var cb = new List<int>(); for (int k = 0; k <= segments; k++) cb.Add(pb[k]);
            capChains[a].Add((ca, e)); capChains[b].Add((cb, e));
        }

        // 정점 캡
        foreach (int v in V)
        {
            // 캡 조각들을 끝-시작으로 이어 닫힌 고리를 만든다. 실패(열린 경계 등)면 캡 없음.
            var loopSegs = LinkChains(capChains[v]);
            if (loopSegs == null) continue;
            var loop = new List<int>();
            foreach (var (pts, _) in loopSegs) for (int i = 0; i < pts.Count - 1; i++) loop.Add(pts[i]);
            if (loop.Count < 3 || loop.Distinct().Count() != loop.Count) continue;
            // 캡 머티리얼: 옵션 지정값, 아니면 첫 영향 면 머티리얼
            int mat = o.MaterialIndex >= 0 ? o.MaterialIndex : affected.Where(f => faceMat.ContainsKey(f)).Select(f => faceMat[f]).DefaultIfEmpty(0).First();
            Vector2 Uv(int id) => uvOf.TryGetValue(id, out var uv) ? uv : Vector2.Zero;
            var capFaces = new List<int>();
            int nSel = selAt[v].Count;
            if (nSel >= 3 && segments >= 2 && o.Intersection == BevelIntersection.GridFill)
                capFaces.AddRange(GridFillCap(m, loop, Uv, pos[v], spec.Bulge, mat));
            else if (nSel >= 3 && segments >= 2 && o.Intersection == BevelIntersection.Cutoff)
            {
                // 엣지마다 프로파일을 평평한 면으로 막고, 가운데에 프로파일 끝점만으로 된 면
                var center = new List<int>();
                foreach (var (pts, owner) in loopSegs)
                {
                    if (owner >= 0 && pts.Count >= 3)
                    {
                        int cf = AddFaceWithCorners(m, pts.Select(id => new Corner(id, Uv(id), Vector3.Zero)).ToList(), mat);
                        if (cf >= 0) capFaces.Add(cf);
                        center.Add(pts[0]);
                    }
                    else for (int i = 0; i < pts.Count - 1; i++) center.Add(pts[i]);
                }
                if (center.Count >= 3 && center.Distinct().Count() == center.Count)
                {
                    int cf = AddFaceWithCorners(m, center.Select(id => new Corner(id, Uv(id), Vector3.Zero)).ToList(), mat);
                    if (cf >= 0) capFaces.Add(cf);
                }
            }
            else
            {
                int cf = AddFaceWithCorners(m, loop.Select(id => new Corner(id, Uv(id), Vector3.Zero)).ToList(), mat);
                if (cf >= 0) capFaces.Add(cf);
            }
            result.AddRange(capFaces);
            // Mark Seams / Sharp: 이 정점에서 표시된 베벨 엣지들의 띠 가장자리(면 f0 쪽)를 캡 둘레를 따라 이어 같은 플래그로 표시
            if ((o.MarkSeams || o.MarkSharp) && nSel >= 2)
                foreach (var (flags, isSeam) in new[] { (o.MarkSeams ? seamOf : null, true), (o.MarkSharp ? hardOf : null, false) })
                {
                    if (flags == null) continue;
                    var anchors = new List<int>();
                    foreach (var (e2, f0, _, _, _) in selInfo)
                        if (flags.GetValueOrDefault(e2) && selAt[v].Contains(e2) && side.TryGetValue((f0, e2, v), out var sv)) anchors.Add(sv.vert);
                    if (anchors.Count >= 2) MarkAlongLoop(m, loop, anchors, isSeam);
                }
        }

        // 원래 베벨 정점은 이제 어느 면에도 쓰이지 않으므로 제거
        RemoveIsolatedVertices(m, V);
        m.BumpTopology();
        return result;
    }

    /// <summary>체인(시작 정점 → … → 끝 정점)들을 끝-시작으로 이어 닫힌 고리를 만든다. 실패하면 null.</summary>
    private static List<(List<int> pts, int owner)>? LinkChains(List<(List<int> pts, int owner)> chains)
    {
        // 점이 2개 이상인 체인만, 시작 정점이 겹치면(분기) 실패
        var valid = chains.Where(c => c.pts.Count >= 2).ToList();
        if (valid.Count == 0) return null;
        var byStart = new Dictionary<int, int>();
        for (int i = 0; i < valid.Count; i++) if (!byStart.TryAdd(valid[i].pts[0], i)) return null;
        var order = new List<(List<int>, int)>();
        // 0번 체인부터 끝 정점 = 다음 체인 시작 정점으로 따라가 다시 0번으로 돌아오고 모든 체인을 썼으면 성공
        int cur = 0; var used = new HashSet<int>();
        while (used.Add(cur))
        {
            order.Add(valid[cur]);
            if (!byStart.TryGetValue(valid[cur].pts[^1], out cur)) return null;
        }
        if (cur != 0 || order.Count != valid.Count) return null;
        return order;
    }

    /// <summary>Grid Fill: 캡 둘레 가운데에 원래 정점 쪽으로 부푼 점을 넣고 쿼드(짝수 둘레) 또는 삼각형으로 채운다.</summary>
    private static List<int> GridFillCap(PolyMesh m, List<int> loop, Func<int, Vector2> uv, Vector3 corner, float bulge, int mat)
    {
        var faces = new List<int>();
        // 둘레 평균에서 원래 모서리(corner) 쪽으로 bulge만큼 옮긴 가운데 점
        var avg = Vector3.Zero; var uvAvg = Vector2.Zero;
        foreach (int id in loop) { avg += m.Verts[id].Position; uvAvg += uv(id); }
        avg /= loop.Count; uvAvg /= loop.Count;
        int center = m.AddVertex(avg + (corner - avg) * bulge);
        int n = loop.Count;
        // 짝수 둘레: (i, i+1, i+2, 가운데) 쿼드 n/2개, 홀수: (i, i+1, 가운데) 삼각형 팬
        if (n % 2 == 0 && n >= 4)
            for (int i = 0; i < n; i += 2)
            {
                int a = loop[i], b = loop[(i + 1) % n], c = loop[(i + 2) % n];
                int f = AddFaceWithCorners(m, new List<Corner> { new(a, uv(a), Vector3.Zero), new(b, uv(b), Vector3.Zero), new(c, uv(c), Vector3.Zero), new(center, uvAvg, Vector3.Zero) }, mat);
                if (f >= 0) faces.Add(f);
            }
        else
            for (int i = 0; i < n; i++)
            {
                int a = loop[i], b = loop[(i + 1) % n];
                int f = AddFaceWithCorners(m, new List<Corner> { new(a, uv(a), Vector3.Zero), new(b, uv(b), Vector3.Zero), new(center, uvAvg, Vector3.Zero) }, mat);
                if (f >= 0) faces.Add(f);
            }
        return faces;
    }

    /// <summary>캡 둘레(loop)에서 이웃한 기준 정점끼리(둘이면 짧은 쪽 경로) 사이 엣지를 심 또는 하드로 표시한다.</summary>
    private static void MarkAlongLoop(PolyMesh m, List<int> loop, List<int> anchors, bool seam)
    {
        int n = loop.Count;
        // idx = 기준 정점들의 루프 인덱스(정렬). spans = 이웃 기준 정점 사이 구간(시작, 엣지 수)
        var idx = anchors.Select(a => loop.IndexOf(a)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        if (idx.Count < 2) return;
        var spans = new List<(int from, int count)>();
        for (int k = 0; k < idx.Count; k++) { int a = idx[k], b = idx[(k + 1) % idx.Count]; spans.Add((a, (b - a + n) % n)); }
        if (idx.Count == 2) spans = new List<(int, int)> { spans[0].count <= spans[1].count ? spans[0] : spans[1] };
        else spans.RemoveAt(spans.IndexOf(spans.OrderByDescending(x => x.count).First())); // 셋 이상이면 가장 긴 구간 하나만 빼고 이어 준다
        foreach (var (from, count) in spans)
            for (int t = 0; t < count; t++)
            {
                int a = loop[(from + t) % n], b = loop[(from + t + 1) % n];
                if (seam) SetEdgeSeam(m, a, b, true); else SetHard(m, a, b, true);
            }
    }

    /// <summary>정점 a-b 사이 엣지가 있으면 UV 심 플래그를 설정한다(<see cref="SetHard"/>의 심 버전).</summary>
    private static void SetEdgeSeam(PolyMesh m, int a, int b, bool seam)
    {
        int e = m.FindEdge(a, b);
        if (e < 0) return;
        var ed = m.Edges[e]; ed.Seam = seam; m.Edges[e] = ed;
    }

    // ================================================================ 정점 Bevel (Affect = Vertices)

    /// <summary>
    /// 선택 정점을 베벨한다: 정점에 모인 각 엣지 위에 폭만큼 물러난 점을 만들고, 정점을 둘러싼 각 면의 모서리를 그 점들 사이의 프로파일(세그먼트)로 깎은 뒤
    /// 그 둘레로 캡을 만든다(세그먼트 2+는 가운데를 원래 정점 쪽으로 부풀린 Grid Fill).
    /// </summary>
    /// <returns>새로 생긴 면(캡) ID. 깎인 원래 면은 같은 면이 아니라 재생성되므로 포함하지 않는다.</returns>
    private static List<int> BevelVerticesCore(PolyMesh m, IEnumerable<int> vertIds, BevelOptions o, BevelInfo info)
    {
        var result = new List<int>();
        var V = new HashSet<int>(vertIds.Where(v => v >= 0 && v < m.VertexCount && m.Verts[v].Alive));
        if (V.Count == 0) return result;
        int segments = System.Math.Clamp(o.Segments, 1, 100);
        var spec = ProfileSpec.From(o);
        float width = MathF.Max(o.Width, 1e-6f);
        // pos = 원래 위치 스냅샷, Raw(e) = 엣지 e를 따라 물러날 거리(Percent면 엣지 길이 비율)
        var pos = V.ToDictionary(v => v, v => m.Verts[v].Position);

        float EdgeLen(int e) { var (a, b) = m.EdgeVertices(e); return Vector3.Distance(m.Verts[a].Position, m.Verts[b].Position); }
        int OtherEnd(int e, int v) { var (a, b) = m.EdgeVertices(e); return a == v ? b : a; }
        float Raw(int e) => o.WidthType == BevelWidthType.Percent ? EdgeLen(e) * width / 100f : width;
        // Clamp Overlap: 엣지 Bevel과 같은 전역 비율 축소(반대쪽 끝도 베벨이면 49%, 아니면 98%까지)
        float clamp = 1f;
        if (o.ClampOverlap)
        {
            var ev = new List<int>();
            foreach (int v in V)
            {
                m.GetVertexEdges(v, ev);
                foreach (int e in ev)
                {
                    float len = EdgeLen(e); if (len < 1e-9f) continue;
                    float limit = len * (V.Contains(OtherEnd(e, v)) ? 0.49f : 0.98f);
                    if (Raw(e) > limit) clamp = MathF.Min(clamp, limit / Raw(e));
                }
            }
        }

        // 영향 면과 원래 엣지 하드/심/정점, 면 머티리얼 기록
        var affected = new List<int>(); var tmp = new List<int>();
        foreach (int v in V) { m.GetVertexFaces(v, tmp); foreach (int f in tmp) if (!affected.Contains(f)) affected.Add(f); }
        var hardOf = new Dictionary<int, bool>(); var seamOf = new Dictionary<int, bool>(); var edgeVerts = new Dictionary<int, (int, int)>();
        var faceMat = new Dictionary<int, int>();
        foreach (int f in affected)
        {
            var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes) { int e = m.Hes[he].Edge; hardOf[e] = m.Edges[e].Hard; seamOf[e] = m.Edges[e].Seam; edgeVerts[e] = m.EdgeVertices(e); }
            faceMat[f] = m.Faces[f].Material;
        }

        // pOnEdge: (정점, 엣지) → 물러난 새 점(이웃 면이 공유), uvOf: 새 점 UV, capChains: 정점별 캡 조각
        var pOnEdge = new Dictionary<(int v, int e), int>();
        var uvOf = new Dictionary<int, Vector2>();
        var capChains = V.ToDictionary(v => v, _ => new List<(List<int> pts, int owner)>());
        int P(int v, int e, int other)
        {
            if (pOnEdge.TryGetValue((v, e), out int id)) return id;
            var pv = pos[v]; var po = m.Verts[other].Position;
            float len = Vector3.Distance(pv, po);
            float L = Raw(e) * clamp;
            id = m.AddVertex(len > 1e-9f ? pv + (po - pv) * (L / len) : pv);
            pOnEdge[(v, e)] = id;
            return id;
        }

        // 면마다 베벨 정점 코너를 p1(prev 쪽) → 프로파일 중간 점 → p2(next 쪽)로 바꾼 새 루프. originEdge: -1 원래 정점, -2 중간 점.
        var rebuilt = new List<(int face, List<(int vert, Vector2 uv, int originEdge)> loop, int material)>();
        foreach (int f in affected)
        {
            var corners = CaptureCorners(m, f);
            int n = corners.Count;
            var loop = new List<(int, Vector2, int)>();
            for (int i = 0; i < n; i++)
            {
                var c = corners[i]; var prev = corners[(i + n - 1) % n]; var next = corners[(i + 1) % n];
                if (!V.Contains(c.Vertex)) { loop.Add((c.Vertex, c.Uv, -1)); continue; }
                int ePrev = m.FindEdge(prev.Vertex, c.Vertex), eNext = m.FindEdge(c.Vertex, next.Vertex);
                int p1 = P(c.Vertex, ePrev, prev.Vertex), p2 = P(c.Vertex, eNext, next.Vertex);
                float l1 = Vector3.Distance(pos[c.Vertex], m.Verts[prev.Vertex].Position), l2 = Vector3.Distance(pos[c.Vertex], m.Verts[next.Vertex].Position);
                // UV는 원래 엣지 위 비율로 보간
                float t1 = l1 > 1e-9f ? Vector3.Distance(pos[c.Vertex], m.Verts[p1].Position) / l1 : 0, t2 = l2 > 1e-9f ? Vector3.Distance(pos[c.Vertex], m.Verts[p2].Position) / l2 : 0;
                var uv1 = Vector2.Lerp(c.Uv, prev.Uv, t1); var uv2 = Vector2.Lerp(c.Uv, next.Uv, t2);
                var chain = new List<int> { p1 };
                loop.Add((p1, uv1, ePrev)); uvOf.TryAdd(p1, uv1);
                var mids = ProfileMidPoints(m.Verts[p1].Position, m.Verts[p2].Position, pos[c.Vertex], segments, spec);
                for (int k = 0; k < mids.Count; k++)
                {
                    int id = m.AddVertex(mids[k]);
                    var uv = Vector2.Lerp(uv1, uv2, (float)(k + 1) / segments);
                    loop.Add((id, uv, -2)); uvOf.TryAdd(id, uv); chain.Add(id);
                }
                loop.Add((p2, uv2, eNext)); uvOf.TryAdd(p2, uv2); chain.Add(p2);
                // 캡은 면과 반대 방향으로 감기므로 체인을 뒤집어 저장
                chain.Reverse();
                capChains[c.Vertex].Add((chain, -1));
            }
            rebuilt.Add((f, loop, m.Faces[f].Material));
        }
        foreach (var (f, _, _) in rebuilt) m.RemoveFace(f, removeIsolated: false);
        // 면 재생성 + 원래 엣지 위 구간의 하드/심 복원
        foreach (var (_, loop, material) in rebuilt)
        {
            var clean = new List<(int vert, Vector2 uv, int originEdge)>();
            foreach (var en in loop) if (clean.Count == 0 || clean[^1].vert != en.vert) clean.Add(en);
            if (clean.Count > 1 && clean[0].vert == clean[^1].vert) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 3) continue;
            int nf = AddFaceWithCorners(m, clean.Select(e => new Corner(e.vert, e.uv, Vector3.Zero)).ToList(), material);
            if (nf < 0) continue;
            for (int i = 0; i < clean.Count; i++)
            {
                var x = clean[i]; var y = clean[(i + 1) % clean.Count];
                // 원래 엣지 위 구간은 그 엣지의 하드/심을 잇는다
                int oe = x.originEdge >= 0 && y.originEdge == -1 ? x.originEdge : y.originEdge >= 0 && x.originEdge == -1 ? y.originEdge : -1;
                if (oe >= 0) { SetHard(m, x.vert, y.vert, hardOf.GetValueOrDefault(oe)); if (seamOf.GetValueOrDefault(oe)) SetEdgeSeam(m, x.vert, y.vert, true); }
            }
        }
        // 정점마다 캡 생성: 닫힌 고리(내부 정점)면 Grid Fill(세그먼트 2+, NGon 아님) 또는 다각형, 경계 정점은 열린 경로로 다각형
        foreach (int v in V)
        {
            var chains = capChains[v];
            var linked = LinkChains(chains);
            List<int> loop;
            if (linked != null) { loop = new List<int>(); foreach (var (pts, _) in linked) for (int i = 0; i < pts.Count - 1; i++) loop.Add(pts[i]); }
            else
            {
                // 경계 정점: 열린 사슬을 이어 붙이고 마지막을 직선으로 닫는다
                loop = OpenChainLoop(chains);
                if (loop == null) continue;
            }
            if (loop.Count < 3 || loop.Distinct().Count() != loop.Count) continue;
            int mat = o.MaterialIndex >= 0 ? o.MaterialIndex : faceMat.Values.DefaultIfEmpty(0).First();
            Vector2 Uv(int id) => uvOf.TryGetValue(id, out var uv) ? uv : Vector2.Zero;
            if (segments >= 2 && linked != null && o.Intersection != BevelIntersection.NGon)
                result.AddRange(GridFillCap(m, loop, Uv, pos[v], spec.Bulge, mat));
            else
            {
                int cf = AddFaceWithCorners(m, loop.Select(id => new Corner(id, Uv(id), Vector3.Zero)).ToList(), mat);
                if (cf >= 0) result.Add(cf);
            }
        }
        RemoveIsolatedVertices(m, V);
        m.BumpTopology();
        return result;
    }

    /// <summary>열린 사슬들(경계 정점)을 끝-시작으로 이어 하나의 열린 경로로 만든다(닫는 엣지는 면이 만든다).</summary>
    private static List<int>? OpenChainLoop(List<(List<int> pts, int owner)> chains)
    {
        // 시작 정점이 겹치지 않는 체인들 중 다른 체인의 끝으로 이어지지 않는 체인(열린 경로의 시작)을 찾아 차례로 따라간다
        var valid = chains.Where(c => c.pts.Count >= 2).ToList();
        if (valid.Count == 0) return null;
        var byStart = new Dictionary<int, int>();
        for (int i = 0; i < valid.Count; i++) if (!byStart.TryAdd(valid[i].pts[0], i)) return null;
        var ends = new HashSet<int>(valid.Select(c => c.pts[^1]));
        int start = Enumerable.Range(0, valid.Count).FirstOrDefault(i => !ends.Contains(valid[i].pts[0]), -1);
        if (start < 0) return null;
        var path = new List<int>(); int cur = start; var used = new HashSet<int>();
        while (used.Add(cur))
        {
            var pts = valid[cur].pts;
            for (int i = 0; i < pts.Count - 1; i++) path.Add(pts[i]);
            if (!byStart.TryGetValue(pts[^1], out cur)) { path.Add(pts[^1]); break; }
        }
        return used.Count == valid.Count ? path : null;
    }

    // ================================================================ 마무리 / 노멀

    /// <summary>
    /// 둥근 Bevel(세그먼트 2+) 마무리(Maya와 같게):
    /// ① 끝 정점의 캡이 이웃한 평평한 면과 같은 평면이면(엣지 하나만 Bevel된 모서리) 그 면에 합쳐 원호 정점을 면 테두리로 흡수한다.
    /// ② 스트립 사이/스트립과 이웃 면 사이 엣지를 60° 스무딩 각으로 소프트/하드 처리해 둥근 면이 부드럽게 음영된다.
    /// </summary>
    private static void PostProcessRoundBevel(PolyMesh m, List<int> result, HashSet<int> strips)
    {
        // newSet = 이번 Bevel이 만든 면. 병합이 일어나면 result가 바뀌므로 변화가 없을 때까지 반복한다.
        var newSet = new HashSet<int>(result);
        var hes = new List<int>();
        bool merged = true;
        while (merged)
        {
            merged = false;
            foreach (int f in result.ToArray())
            {
                if (f < 0 || f >= m.FaceCount || !m.Faces[f].Alive) { result.Remove(f); continue; }
                // 띠는 건드리지 않고 캡만 검사
                if (strips.Contains(f)) continue;
                var nf = MeshNormals.FaceNormalUnnormalized(m, f);
                m.GetFaceHalfEdges(f, hes);
                if (nf.LengthSquared() < 1e-14f)
                {
                    // 넓이 0인 캡(직선 프로파일): 이웃한 원래 면에 흡수
                    foreach (int he in hes)
                    {
                        int tw = m.Hes[he].Twin; if (tw < 0) continue;
                        int g = m.Hes[tw].Face;
                        if (g < 0 || newSet.Contains(g) || !m.Faces[g].Alive) continue;
                        var (ok0, _) = MergeFacesAcrossEdgeReturning(m, m.Hes[he].Edge);
                        if (ok0) { result.Remove(f); merged = true; }
                        break;
                    }
                    if (merged) break;
                    continue;
                }
                nf = Vector3.Normalize(nf);
                // curved: 이웃한 새 면(띠) 중 이 캡과 법선이 다른 면이 있으면 둥근 Bevel의 끝 캡(D자)이다
                bool curved = false;
                foreach (int he in hes)
                {
                    int tw = m.Hes[he].Twin; if (tw < 0) continue;
                    int g = m.Hes[tw].Face;
                    if (g < 0 || !newSet.Contains(g) || !m.Faces[g].Alive) continue;
                    var ng = MeshNormals.FaceNormalUnnormalized(m, g);
                    if (ng.LengthSquared() > 1e-20f && Vector3.Dot(nf, Vector3.Normalize(ng)) < 0.9999f) { curved = true; break; }
                }
                if (!curved) continue;
                // D자 캡과 같은 평면인 원래 면을 찾아 합친다(한 번 합치면 face 목록이 바뀌므로 처음부터 다시)
                foreach (int he in hes)
                {
                    int tw = m.Hes[he].Twin; if (tw < 0) continue;
                    int g = m.Hes[tw].Face;
                    if (g < 0 || newSet.Contains(g) || !m.Faces[g].Alive) continue;
                    var ng = MeshNormals.FaceNormalUnnormalized(m, g);
                    // 같은 평면(오목 프로파일이면 캡이 뒤집혀 있으므로 방향 무관)
                    if (ng.LengthSquared() < 1e-20f || MathF.Abs(Vector3.Dot(nf, Vector3.Normalize(ng))) < 0.9999f) continue;
                    var (ok, _) = MergeFacesAcrossEdgeReturning(m, m.Hes[he].Edge);
                    if (ok) { result.Remove(f); merged = true; }
                    break;
                }
                if (merged) break;
            }
        }
        SoftenNewFaces(m, result);
    }

    /// <summary>주어진 면들의 모든 엣지를 60° 기준으로 소프트/하드 처리한다(둥근 Bevel의 부드러운 음영).</summary>
    private static void SoftenNewFaces(PolyMesh m, List<int> faces)
    {
        var hes = new List<int>();
        var edges = new HashSet<int>();
        foreach (int f in faces)
        {
            if (f < 0 || f >= m.FaceCount || !m.Faces[f].Alive) continue;
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes) edges.Add(m.Hes[he].Edge);
        }
        SoftenHardenByAngle(m, edges, 60f);
        m.BumpTopology();
    }

    /// <summary>
    /// Harden Normals: 띠 면의 코너 노멀을 프로파일 위치에 따라 두 이웃 면 법선 사이로 보간해 고정한다(양 끝 = 이웃 면 법선).
    /// 띠와 원래 면 사이 엣지는 하드로 두어 원래 면은 평평하게 보이고, 베벨 면은 이웃과 이어지듯 부드럽게 보인다.
    /// </summary>
    private static void ApplyHardenNormals(PolyMesh m, List<int> result, BevelInfo info)
    {
        var newSet = new HashSet<int>(result);
        var hes = new List<int>();
        // 띠마다: 각 코너의 프로파일 위치 s로 nA→nB slerp한 노멀을 고정(NormalLocked → Recompute가 건너뜀)
        foreach (var (f, (nA, nB, t)) in info.StripProfiles)
        {
            if (f >= m.FaceCount || !m.Faces[f].Alive) continue;
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes)
            {
                var h = m.Hes[he];
                float s = t.TryGetValue(h.Vertex, out var tv) ? tv : 0.5f;
                var n = SlerpNormal(nA, nB, s);
                if (n.LengthSquared() < 1e-12f) continue;
                h.Normal = n; h.NormalLocked = true; m.Hes[he] = h;
                int tw = h.Twin;
                // 띠와 원래 면(새 면 아님) 사이 엣지는 하드: 원래 면은 평평한 노멀 유지
                if (tw >= 0 && !newSet.Contains(m.Hes[tw].Face)) { var ed = m.Edges[h.Edge]; ed.Hard = true; m.Edges[h.Edge] = ed; }
            }
            // 띠 내부 엣지는 부드럽게
            foreach (int he in hes) { int tw = m.Hes[he].Twin; if (tw >= 0 && info.Strips.Contains(m.Hes[tw].Face)) { var ed = m.Edges[m.Hes[he].Edge]; ed.Hard = false; m.Edges[m.Hes[he].Edge] = ed; } }
        }
    }

    /// <summary>두 단위 노멀 사이 구면 선형 보간(각이 아주 작으면 선형 보간 후 정규화, 영벡터는 다른 쪽 반환).</summary>
    private static Vector3 SlerpNormal(Vector3 a, Vector3 b, float t)
    {
        if (a.LengthSquared() < 1e-12f) return b; if (b.LengthSquared() < 1e-12f) return a;
        float d = System.Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        float w = MathF.Acos(d);
        if (w < 1e-4f) return Vector3.Normalize(Vector3.Lerp(a, b, t));
        var r = (a * MathF.Sin((1 - t) * w) + b * MathF.Sin(t * w)) / MathF.Sin(w);
        return r.LengthSquared() > 1e-12f ? Vector3.Normalize(r) : a;
    }

    /// <summary>
    /// Face Strength + Weighted Normal: 새 띠 = Medium, 새 캡 = Weak, 원래 면 = Medium(Affected: 새 면과 이웃한 원래 면 = Strong, All: 원래 면 모두 Strong).
    /// 새 면이 닿는 정점의 각 코너 노멀을, 그 코너의 스무딩 부채꼴에서 가장 센 면들만의 면적 가중 평균으로 고정한다.
    /// </summary>
    private static void ApplyFaceStrength(PolyMesh m, List<int> result, BevelInfo info, BevelFaceStrength mode)
    {
        var newSet = new HashSet<int>(result);
        var hes = new List<int>();
        // 면 세기: 3 = Strong, 2 = Medium, 1 = Weak. 새 띠 = 2, 새 캡 = 1, 원래 면 = 모드에 따라 3 또는 2.
        int Strength(int f)
        {
            if (newSet.Contains(f)) return info.Strips.Contains(f) ? 2 : 1;
            if (mode == BevelFaceStrength.All) return 3;
            if (mode == BevelFaceStrength.Affected)
            {
                m.GetFaceHalfEdges(f, hes);
                foreach (int he in hes) { int tw = m.Hes[he].Twin; if (tw >= 0 && newSet.Contains(m.Hes[tw].Face)) return 3; }
                // 정점만 맞닿아도 영향 면
                foreach (int he in hes.ToArray()) foreach (int o in m.VertexOutgoing(m.Hes[he].Vertex).ToArray()) if (newSet.Contains(m.Hes[o].Face)) return 3;
            }
            return 2;
        }
        // 면 세기 메모이제이션
        var strength = new Dictionary<int, int>();
        int S(int f) { if (!strength.TryGetValue(f, out int s)) { s = Strength(f); strength[f] = s; } return s; }
        // 새 면에 닿은 정점들만 처리
        var verts = new HashSet<int>();
        foreach (int f in result) { if (!m.Faces[f].Alive) continue; m.GetFaceHalfEdges(f, hes); foreach (int he in hes) verts.Add(m.Hes[he].Vertex); }
        foreach (int v in verts)
        {
            foreach (int h in m.VertexOutgoing(v).ToArray())
            {
                // 이 코너의 스무딩 부채꼴에서 가장 센 면들의 (정규화하지 않은 = 면적 가중) 법선 합을 노멀로 고정
                var fan = CornerFan(m, h);
                int best = fan.Max(S);
                var sum = Vector3.Zero;
                foreach (int f in fan) if (S(f) == best) sum += MeshNormals.FaceNormalUnnormalized(m, f);
                if (sum.LengthSquared() < 1e-20f) continue;
                var he = m.Hes[h]; he.Normal = Vector3.Normalize(sum); he.NormalLocked = true; m.Hes[h] = he;
            }
        }
    }

    /// <summary>코너 h(정점의 나가는 하프에지)와 소프트 엣지로 이어진 면들(MeshNormals와 같은 스무딩 규칙).</summary>
    private static List<int> CornerFan(PolyMesh m, int h)
    {
        // 하드 엣지나 경계를 만날 때까지 트윈의 Next로 한 방향 회전, 한 바퀴 돌면 wrapped
        var faces = new List<int> { m.Hes[h].Face };
        bool wrapped = false; int cur = h;
        for (int g = 0; g < 4096; g++)
        {
            var ch = m.Hes[cur];
            if (ch.Twin < 0 || m.Edges[ch.Edge].Hard) break;
            int nxt = m.Hes[ch.Twin].Next;
            if (nxt == h) { wrapped = true; break; }
            faces.Add(m.Hes[nxt].Face); cur = nxt;
        }
        // 한 바퀴가 아니면 반대 방향(Prev의 트윈)으로도 돌아 부채꼴 나머지를 모은다
        if (!wrapped)
        {
            cur = h;
            for (int g = 0; g < 4096; g++)
            {
                var ph = m.Hes[m.Hes[cur].Prev];
                if (ph.Twin < 0 || m.Edges[ph.Edge].Hard) break;
                int nxt = ph.Twin;
                if (nxt == h) break;
                faces.Add(m.Hes[nxt].Face); cur = nxt;
            }
        }
        return faces;
    }
}
