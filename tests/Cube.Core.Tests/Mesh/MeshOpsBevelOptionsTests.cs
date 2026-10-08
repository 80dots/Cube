using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>Blender식 Bevel 옵션(BevelOptions) 검사.</summary>
public class MeshOpsBevelOptionsTests
{
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;
    private static List<int> EdgesAt(PolyMesh m, int v) { var l = new List<int>(); m.GetVertexEdges(v, l); return l; }

    private static void AssertClosedSound(PolyMesh m)
    {
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive) Assert.False(m.IsBoundaryEdge(e), $"edge {e} is boundary");
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive) Assert.True(MeshNormals.FaceNormalUnnormalized(m, f).Length() > 1e-9f, $"face {f} degenerate");
    }

    /// <summary>큐브 엣지 0(양 끝이 큐브 코너)의 두 정점과 방향.</summary>
    private static (Vector3 a, Vector3 b) EdgeLine(PolyMesh m, int e) { var (x, y) = m.EdgeVertices(e); return (m.Verts[x].Position, m.Verts[y].Position); }

    private static float DistToLine(Vector3 p, Vector3 a, Vector3 b) { var d = Vector3.Normalize(b - a); var r = p - a; return (r - d * Vector3.Dot(r, d)).Length(); }

    [Theory]
    [InlineData(BevelWidthType.Offset, 0.2f, 0.2f)]                        // 면 위 수직 거리 = 0.2
    [InlineData(BevelWidthType.Absolute, 0.2f, 0.2f)]                      // 직각 큐브에서는 Offset과 같다
    [InlineData(BevelWidthType.Percent, 25f, 0.25f)]                       // 인접 엣지 길이(1)의 25%
    [InlineData(BevelWidthType.Width, 0.2f, 0.1414214f)]                  // 베벨 면 폭 0.2 → 오프셋 0.2/√2
    [InlineData(BevelWidthType.Depth, 0.05f, 0.0707107f)]                 // 깊이 0.05 → 오프셋 0.05·√2
    public void WidthTypes_SingleCubeEdge_OffsetDistance(BevelWidthType type, float width, float expectedOffset)
    {
        var m = MeshBuilder.Cube();
        var (a, b) = EdgeLine(m, 0);
        var faces = MeshOps.Bevel(m, new[] { 0 }, new BevelOptions { WidthType = type, Width = width });
        Assert.Single(faces);
        AssertClosedSound(m);
        var ids = new List<int>(); m.GetFaceVertices(faces[0], ids);
        foreach (int v in ids) Assert.Equal(expectedOffset * MathF.Sqrt(2f), DistToLine(m.Verts[v].Position, a, b) * MathF.Sqrt(2f), 3);
        foreach (int v in ids) Assert.Equal(expectedOffset, DistToLine(m.Verts[v].Position, a, b), 3);
    }

    [Fact]
    public void Shape_ControlsProfileBulge()
    {
        // 엣지 하나, 세그먼트 2: 가운데 점과 원래 엣지 사이 거리 — 원호(0.5) < 직선(0.25)보다 가깝고 각(1)이면 거의 0
        float MidDist(float shape)
        {
            var m = MeshBuilder.Cube(); var (a, b) = EdgeLine(m, 0);
            var faces = MeshOps.Bevel(m, new[] { 0 }, new BevelOptions { Width = 0.2f, Segments = 2, Shape = shape });
            AssertClosedSound(m);
            float best = float.MaxValue;
            foreach (int f in faces) { var ids = new List<int>(); m.GetFaceVertices(f, ids); foreach (int v in ids) best = MathF.Min(best, DistToLine(m.Verts[v].Position, a, b)); }
            return best;
        }
        float lin = MidDist(0.25f), circ = MidDist(0.5f), sq = MidDist(0.999f), concave = MidDist(0.1f);
        Assert.Equal(0.2f / MathF.Sqrt(2f), lin, 3);              // 챔퍼 중점
        Assert.Equal(0.2f * (MathF.Sqrt(2f) - 1f), circ, 3);      // 원호 중점: r(√2 − 1)
        Assert.True(sq < 0.01f, $"square profile mid {sq}");
        Assert.True(concave > lin, "concave profile bulges inward");
    }

    [Theory]
    [InlineData(BevelIntersection.GridFill, 2)]
    [InlineData(BevelIntersection.GridFill, 3)]
    [InlineData(BevelIntersection.Cutoff, 2)]
    [InlineData(BevelIntersection.Cutoff, 3)]
    [InlineData(BevelIntersection.NGon, 3)]
    public void Intersection_AllCubeEdges_ClosedMesh(BevelIntersection type, int s)
    {
        var m = MeshBuilder.Cube();
        var faces = MeshOps.Bevel(m, Enumerable.Range(0, m.EdgeCount).ToList(), new BevelOptions { Width = 0.1f, Segments = s, Intersection = type });
        AssertClosedSound(m);
        int strips = 12 * s;
        int corner = type switch
        {
            BevelIntersection.NGon => 1,
            BevelIntersection.Cutoff => 3 + 1,                    // 엣지별 막음 면 3 + 가운데 삼각형
            _ => 3 * s % 2 == 0 ? 3 * s / 2 : 3 * s,               // 짝수 둘레 = 쿼드, 홀수 = 삼각형 부채
        };
        Assert.Equal(strips + 8 * corner, faces.Count);
        // 그리드 채움 가운데 점은 원래 코너 쪽으로 부푼다(캡 평균보다 바깥)
        if (type == BevelIntersection.GridFill)
            Assert.Contains(Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive), v => { var p = m.Verts[v].Position; return MathF.Abs(p.X) > 0.46f && MathF.Abs(p.Y) > 0.46f && MathF.Abs(p.Z) > 0.46f; });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void AffectVertices_CubeCorner(int s)
    {
        var m = MeshBuilder.Cube();
        var faces = MeshOps.Bevel(m, new[] { 2 }, new BevelOptions { Affect = BevelAffect.Vertices, Width = 0.2f, Segments = s });
        AssertClosedSound(m);
        Assert.NotEmpty(faces);
        if (s == 1) { Assert.Equal(7, m.AliveFaceCount); Assert.Equal(10, m.AliveVertexCount); }
        // 새 점은 모두 원래 코너에서 0.2 이내
        foreach (int f in faces) { var ids = new List<int>(); m.GetFaceVertices(f, ids); foreach (int v in ids) Assert.True(Vector3.Distance(m.Verts[v].Position, new Vector3(0.5f, 0.5f, 0.5f)) <= 0.2f + 1e-4f); }
    }

    [Fact]
    public void AffectVertices_PercentAndClamp()
    {
        var m = MeshBuilder.Cube();
        MeshOps.Bevel(m, Enumerable.Range(0, 8).ToList(), new BevelOptions { Affect = BevelAffect.Vertices, WidthType = BevelWidthType.Percent, Width = 90f, ClampOverlap = true });
        AssertClosedSound(m);
        // 90%는 양끝이 모두 베벨이라 50%로 줄어든다 → 모든 정점이 엣지 중점 근처
        for (int v = 0; v < m.VertexCount; v++) if (m.Verts[v].Alive) Assert.True(m.Verts[v].Position.Length() < 0.75f);
    }

    /// <summary>L자 프리즘(윗면에 반사각 코너).</summary>
    private static (PolyMesh m, int e1, int e2) LPrism()
    {
        var pts = new List<Vector3> { new(0, 0, 0), new(0, 0, 2), new(1, 0, 2), new(1, 0, 1), new(2, 0, 1), new(2, 0, 0) };
        var m = MeshBuilder.Polygon(pts, Vector3.UnitY);
        var bottom = Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToList();
        var top = MeshOps.ExtrudeFaces(m, bottom);
        var ids = new List<int>(); var moved = new HashSet<int>();
        foreach (int f in top) { m.GetFaceVertices(f, ids); foreach (int v in ids) if (moved.Add(v)) { var vt = m.Verts[v]; vt.Position += new Vector3(0, 1, 0); m.Verts[v] = vt; } }
        int Find(Vector3 a, Vector3 b)
        {
            for (int e = 0; e < m.EdgeCount; e++)
            {
                if (!m.Edges[e].Alive) continue;
                var (x, y) = m.EdgeVertices(e); var px = m.Verts[x].Position; var py = m.Verts[y].Position;
                if ((Vector3.Distance(px, a) < 1e-4f && Vector3.Distance(py, b) < 1e-4f) || (Vector3.Distance(px, b) < 1e-4f && Vector3.Distance(py, a) < 1e-4f)) return e;
            }
            return -1;
        }
        int e1 = Find(new(1, 1, 2), new(1, 1, 1)), e2 = Find(new(1, 1, 1), new(2, 1, 1));
        Assert.True(e1 >= 0 && e2 >= 0);
        return (m, e1, e2);
    }

    [Theory]
    [InlineData(BevelMiter.Sharp, 1)]
    [InlineData(BevelMiter.Patch, 1)]
    [InlineData(BevelMiter.Arc, 1)]
    [InlineData(BevelMiter.Arc, 3)]
    [InlineData(BevelMiter.Patch, 2)]
    public void OuterMiter_ReflexCorner(BevelMiter miter, int s)
    {
        var (m, e1, e2) = LPrism();
        int before = m.AliveFaceCount;
        var faces = MeshOps.Bevel(m, new[] { e1, e2 }, new BevelOptions { Width = 0.1f, Segments = s, MiterOuter = miter });
        Assert.Empty(MeshValidator.Check(m));
        Assert.True(faces.Count >= 2 * s);
        Assert.True(m.AliveFaceCount > before);
        foreach (int f in faces) Assert.True(MeshNormals.FaceNormalUnnormalized(m, f).Length() > 1e-9f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void InnerMiterArc_TwoEdgesAtCubeCorner(int s)
    {
        var m = MeshBuilder.Cube();
        var two = EdgesAt(m, 2).Take(2).ToList();
        var sharp = MeshBuilder.Cube(); MeshOps.Bevel(sharp, two, new BevelOptions { Width = 0.1f, Segments = s });
        var faces = MeshOps.Bevel(m, two, new BevelOptions { Width = 0.1f, Segments = s, MiterInner = BevelMiter.Arc, Spread = 0.05f });
        AssertClosedSound(m);
        Assert.True(m.AliveVertexCount > sharp.AliveVertexCount, "arc miter adds vertices");
    }

    [Fact]
    public void HardenNormals_LocksStripCornersToNeighborNormals()
    {
        var m = MeshBuilder.Cube();
        var faces = MeshOps.Bevel(m, new[] { 0 }, new BevelOptions { Width = 0.2f, Segments = 3, HardenNormals = true });
        AssertClosedSound(m);
        MeshNormals.Recompute(m); // 고정 코너는 다시 계산해도 그대로
        int locked = 0; var hes = new List<int>();
        foreach (int f in faces)
        {
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes) if (m.Hes[he].NormalLocked) locked++;
        }
        Assert.Equal(faces.Count * 4, locked);
        // 띠 끝 코너의 노멀은 이웃 큐브 면의 축 방향 법선
        int axisAligned = 0;
        foreach (int f in faces) { m.GetFaceHalfEdges(f, hes); foreach (int he in hes) { var n = m.Hes[he].Normal; if (MathF.Max(MathF.Abs(n.X), MathF.Max(MathF.Abs(n.Y), MathF.Abs(n.Z))) > 0.9999f) axisAligned++; } }
        Assert.True(axisAligned >= 4);
    }

    [Theory]
    [InlineData(BevelFaceStrength.New)]
    [InlineData(BevelFaceStrength.Affected)]
    [InlineData(BevelFaceStrength.All)]
    public void FaceStrength_LocksWeightedNormals(BevelFaceStrength mode)
    {
        var m = MeshBuilder.Cube();
        MeshOps.Bevel(m, Enumerable.Range(0, m.EdgeCount).ToList(), new BevelOptions { Width = 0.1f, Segments = 2, FaceStrength = mode });
        AssertClosedSound(m);
        Assert.Contains(Enumerable.Range(0, m.Hes.Count), h => m.Hes[h].Alive && m.Hes[h].NormalLocked);
        if (mode == BevelFaceStrength.Affected)
        {
            // 원래 큐브 면 코너는 그 면 법선(축 방향)으로 고정
            var hes = new List<int>(); int flat = 0;
            for (int f = 0; f < m.FaceCount; f++)
            {
                if (!m.Faces[f].Alive) continue; m.GetFaceVertices(f, hes); if (hes.Count != 4) continue;
                var fn = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f));
                if (MathF.Max(MathF.Abs(fn.X), MathF.Max(MathF.Abs(fn.Y), MathF.Abs(fn.Z))) < 0.9999f) continue;
                m.GetFaceHalfEdges(f, hes);
                if (hes.All(he => Vector3.Dot(m.Hes[he].Normal, fn) > 0.9999f)) flat++;
            }
            Assert.Equal(6, flat);
        }
    }

    [Fact]
    public void MarkSeams_PropagatesBetweenSeamEdgesAtCorner()
    {
        var m = MeshBuilder.Cube();
        var three = EdgesAt(m, 2);
        foreach (int e in three.Take(2)) { var ed = m.Edges[e]; ed.Seam = true; m.Edges[e] = ed; }
        var without = MeshBuilder.Cube(); foreach (int e in EdgesAt(without, 2).Take(2)) { var ed = without.Edges[e]; ed.Seam = true; without.Edges[e] = ed; }
        MeshOps.Bevel(without, EdgesAt(without, 2), new BevelOptions { Width = 0.1f });
        MeshOps.Bevel(m, three, new BevelOptions { Width = 0.1f, MarkSeams = true });
        AssertClosedSound(m);
        int Seams(PolyMesh x) => Enumerable.Range(0, x.EdgeCount).Count(e => x.Edges[e].Alive && x.Edges[e].Seam);
        Assert.True(Seams(m) > Seams(without), $"{Seams(m)} vs {Seams(without)}");
    }

    [Fact]
    public void MarkSharp_HardensBetweenSharpEdges()
    {
        var m = MeshBuilder.Cube();
        for (int e = 0; e < m.EdgeCount; e++) { var ed = m.Edges[e]; ed.Hard = false; m.Edges[e] = ed; }
        var three = EdgesAt(m, 2);
        foreach (int e in three.Take(2)) { var ed = m.Edges[e]; ed.Hard = true; m.Edges[e] = ed; }
        MeshOps.Bevel(m, three, new BevelOptions { Width = 0.1f, MarkSharp = true });
        AssertClosedSound(m);
        Assert.Contains(Enumerable.Range(0, m.EdgeCount), e => m.Edges[e].Alive && m.Edges[e].Hard);
    }

    [Theory]
    [InlineData(BevelProfilePreset.Default, 4, false)]
    [InlineData(BevelProfilePreset.SupportLoops, 4, false)]
    [InlineData(BevelProfilePreset.Steps, 6, false)]
    [InlineData(BevelProfilePreset.CorniceMolding, 7, false)]
    [InlineData(BevelProfilePreset.CrownMolding, 12, true)]
    [InlineData(BevelProfilePreset.CrownMolding, 3, false)]
    public void CustomProfile_Presets(BevelProfilePreset preset, int s, bool even)
    {
        var m = MeshBuilder.Cube();
        var faces = MeshOps.Bevel(m, new[] { 0 }, new BevelOptions { Width = 0.2f, Segments = s, ProfileType = BevelProfileType.Custom, Preset = preset, SampleEvenLengths = even });
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        Assert.True(faces.Count >= s - 1, $"{faces.Count} faces for {s} segments");
        // 프로파일 점은 원래 큐브 안(또는 표면)에 있다
        for (int v = 0; v < m.VertexCount; v++) if (m.Verts[v].Alive) { var p = m.Verts[v].Position; Assert.True(MathF.Abs(p.X) <= 0.5f + 1e-4f && MathF.Abs(p.Y) <= 0.5f + 1e-4f && MathF.Abs(p.Z) <= 0.5f + 1e-4f); }
    }

    [Fact]
    public void ClampOverlap_LimitsLargeWidth()
    {
        var m = MeshBuilder.Cube();
        MeshOps.Bevel(m, Enumerable.Range(0, m.EdgeCount).ToList(), new BevelOptions { Width = 5f, ClampOverlap = true });
        AssertClosedSound(m);
        for (int v = 0; v < m.VertexCount; v++) if (m.Verts[v].Alive) { var p = m.Verts[v].Position; Assert.True(MathF.Abs(p.X) <= 0.5f + 1e-4f && MathF.Abs(p.Y) <= 0.5f + 1e-4f && MathF.Abs(p.Z) <= 0.5f + 1e-4f); }
    }

    [Fact]
    public void MaterialIndex_AssignsNewFaces()
    {
        var m = MeshBuilder.Cube();
        var faces = MeshOps.Bevel(m, Enumerable.Range(0, m.EdgeCount).ToList(), new BevelOptions { Width = 0.1f, Segments = 2, MaterialIndex = 3 });
        Assert.All(faces, f => Assert.Equal(3, m.Faces[f].Material));
        Assert.Equal(6, Enumerable.Range(0, m.FaceCount).Count(f => m.Faces[f].Alive && m.Faces[f].Material == 0));
    }

    [Fact]
    public void LoopSlideOff_NewEdgesPerpendicularOnSlantedFace()
    {
        // 위쪽이 기운 쐐기: 한 쪽 엣지를 베벨하면 Loop Slide 꺼짐에서는 새 점이 베벨 엣지에서 정확히 오프셋 거리
        var m = MeshBuilder.Cube();
        for (int v = 0; v < m.VertexCount; v++) { var vt = m.Verts[v]; if (vt.Position.Y > 0 && vt.Position.X > 0) vt.Position += new Vector3(0.3f, 0, 0); m.Verts[v] = vt; }
        var e = Enumerable.Range(0, m.EdgeCount).First(i => { var (a, b) = EdgeLine(m, i); return a.Y > 0 && b.Y > 0 && a.X < 0 && b.X < 0; });
        var (la, lb) = EdgeLine(m, e);
        var faces = MeshOps.Bevel(m, new[] { e }, new BevelOptions { Width = 0.1f, LoopSlide = false });
        AssertClosedSound(m);
        var ids = new List<int>(); m.GetFaceVertices(faces[0], ids);
        foreach (int v in ids) Assert.Equal(0.1f, DistToLine(m.Verts[v].Position, la, lb), 3);
    }

    [Fact]
    public void SaveLoad_KeepsLockedCornerNormals()
    {
        var doc = new Cube.Core.Scene.Document();
        var m = MeshBuilder.Cube();
        MeshOps.Bevel(m, new[] { 0 }, new BevelOptions { Width = 0.2f, Segments = 2, HardenNormals = true });
        var node = new Cube.Core.Scene.SceneNode { Name = "c", Shape = new Cube.Core.Scene.MeshShape(m) };
        doc.AddNode(node, doc.Root);
        var json = Cube.Core.IO.CubeFileFormat.Serialize(doc);
        Assert.Contains("cornerNormals", json);
        var doc2 = new Cube.Core.Scene.Document();
        Cube.Core.IO.CubeFileFormat.Deserialize(doc2, json);
        var m2 = doc2.MeshNodes().First().Mesh!;
        Assert.Equal(Enumerable.Range(0, m.Hes.Count).Count(h => m.Hes[h].Alive && m.Hes[h].NormalLocked), Enumerable.Range(0, m2.Hes.Count).Count(h => m2.Hes[h].Alive && m2.Hes[h].NormalLocked));
    }
}
