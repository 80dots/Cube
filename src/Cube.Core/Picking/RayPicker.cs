using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Picking;

/// <summary>피킹 대상 하나: 노드 + 메시 + 테셀레이션 결과 + 월드 행렬.</summary>
/// <remarks>
/// App의 Picker가 화면에 보이는 메시 노드마다 하나씩 만든다. Render는 <c>MeshTessellator</c> 결과(로컬 좌표의 삼각형·선·점·면 중심과
/// TriToFace/LineToEdge/PointToVertex 매핑)이므로 피킹 결과를 하프에지 메시의 면/엣지/정점 ID로 되돌릴 수 있다.
/// </remarks>
public sealed class PickTarget
{
    /// <summary>피킹 대상 노드 ID.</summary>
    public required NodeId Id { get; init; }
    /// <summary>원본 폴리 메시(컴포넌트 ID의 소유자).</summary>
    public required PolyMesh Mesh { get; init; }
    /// <summary>테셀레이션 결과(로컬 좌표). 스무스 프리뷰/스킨 변형이 있으면 표시 중인 형태일 수 있다.</summary>
    public required RenderMeshData Render { get; init; }
    /// <summary>노드의 월드 행렬(로컬 → 월드, 행벡터 규약).</summary>
    public required Matrix4x4 World { get; init; }
    /// <summary>WorldInverse 지연 계산 캐시.</summary>
    private Matrix4x4? _inv;
    /// <summary>월드 → 로컬 역행렬. 처음 요청할 때 한 번만 계산해 캐시한다(광선을 로컬로 옮길 때 사용).</summary>
    public Matrix4x4 WorldInverse { get { if (_inv == null) { Matrix4x4.Invert(World, out var i); _inv = i; } return _inv.Value; } }
}

/// <summary>
/// 피킹 결과 하나. Component는 모드에 따른 컴포넌트 ID(정점/엣지/면; 오브젝트 피킹이면 −1),
/// Depth는 정렬용 깊이(면 = 광선 원점에서의 월드 거리, 정점/엣지 = 카메라 전방 깊이), WorldPos는 맞은 월드 위치.
/// </summary>
public readonly record struct PickHit(NodeId Node, int Component, float Depth, Vector3 WorldPos)
{
    /// <summary>선택 상태에 넣을 (노드, 컴포넌트) 쌍으로 변환.</summary>
    public SelItem ToSelItem() => new(Node, Component);
}

/// <summary>
/// 레이/스크린 공간 피킹과 마키 선택. Maya 규칙: 면은 레이 교차, 엣지/정점은 화면 거리 임계값, 깊이 동률 우선.
/// camera-based 선택 시 가려진 컴포넌트는 제외한다.
/// </summary>
/// <remarks>
/// 모든 함수는 상태 없는 정적 함수다. 정점·엣지는 각 점/선을 화면에 투영해 커서와의 픽셀 거리로 고르고,
/// 거리 차가 1px 이내면 더 가까운(깊이가 작은) 것을 우선한다. cameraBased가 켜지면 후보 점에서 카메라로 광선을 쏴 가림 여부를 확인한다.
/// </remarks>
public static class RayPicker
{
    /// <summary>엣지 클릭 허용 반경(픽셀).</summary>
    public const float EdgeThresholdPx = 6f;
    /// <summary>정점 클릭 허용 반경(픽셀).</summary>
    public const float VertexThresholdPx = 8f;

    // ------------------------------------------------------------ 레이-삼각형

    /// <summary>Möller–Trumbore. 양면.</summary>
    /// <remarks>
    /// 무게중심 좌표 (u, v)와 광선 파라미터 t를 동시에 푼다: P = D × e2, det = e1·P.
    /// det의 부호를 따지지 않으므로 앞/뒷면 모두 맞는다(det ≈ 0이면 평행). u, v에 1e-6 여유를 둬 공유 엣지 위의 광선이 틈으로 빠지지 않게 한다.
    /// </remarks>
    /// <param name="t">교차 시 광선 파라미터(방향 길이 단위). 1e-6 이하(원점 뒤/위)는 무효.</param>
    /// <returns>삼각형 내부를 앞쪽에서 맞으면 true.</returns>
    public static bool RayTriangle(in Ray r, in Vector3 a, in Vector3 b, in Vector3 c, out float t)
    {
        t = 0;
        // 삼각형 두 변
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(r.Direction, e2);
        float det = Vector3.Dot(e1, p);
        // 광선이 삼각형 평면과 평행
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        // 첫 번째 무게중심 좌표 u 검사
        var s = r.Origin - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < -1e-6f || u > 1 + 1e-6f) return false;
        // 두 번째 무게중심 좌표 v와 u+v ≤ 1 검사
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(r.Direction, q) * inv;
        if (v < -1e-6f || u + v > 1 + 1e-6f) return false;
        // 교점까지의 광선 파라미터
        t = Vector3.Dot(e2, q) * inv;
        return t > 1e-6f;
    }

    /// <summary>슬랩 방식 레이-AABB 교차(t ≥ 0). 방향 성분이 0이면 그 축은 원점이 범위 안일 때만 통과. 박스는 약간 넓혀 경계 삼각형을 놓치지 않는다.</summary>
    public static bool RayIntersectsBox(in Ray r, Vector3 min, Vector3 max)
    {
        // 경계 패딩: 박스 대각선의 1e-4(최소 1e-5). 납작한(두께 0) 메시도 교차되도록
        float pad = MathF.Max(1e-5f, (max - min).Length() * 1e-4f);
        min -= new Vector3(pad); max += new Vector3(pad);
        // 슬랩 교차 구간 [tmin, tmax]를 축마다 좁혀 간다. 광선 뒤쪽은 보지 않으므로 tmin은 0에서 시작
        float tmin = 0f, tmax = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? r.Origin.X : axis == 1 ? r.Origin.Y : r.Origin.Z;
            float d = axis == 0 ? r.Direction.X : axis == 1 ? r.Direction.Y : r.Direction.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            // 이 축과 평행한 광선: 원점이 슬랩 밖이면 절대 교차하지 않음
            if (MathF.Abs(d) < 1e-12f) { if (o < lo || o > hi) return false; continue; }
            float inv = 1f / d;
            float t0 = (lo - o) * inv, t1 = (hi - o) * inv;
            // 방향이 음수면 진입/진출 순서가 뒤바뀌므로 교환
            if (t0 > t1) (t0, t1) = (t1, t0);
            tmin = MathF.Max(tmin, t0); tmax = MathF.Min(tmax, t1);
            if (tmin > tmax) return false;
        }
        return true;
    }

    /// <summary>
    /// 대상의 로컬 AABB를 화면에 투영한 사각형(여유 thresholdPx)에 픽셀이 들어가는지. 코너 하나라도 카메라 뒤면 보수적으로 true.
    /// 정점/엣지 피킹이 메시마다 모든 점·선을 투영하기 전에 대상을 걸러내는 용도.
    /// </summary>
    public static bool ScreenBoundsMayContain(PickTarget tg, CameraProjection cam, Vector2 px, float thresholdPx)
    {
        // 렌더 데이터가 비어 있으면 피킹할 것이 없음
        var r = tg.Render;
        if (r.PointCount == 0) return false;
        var mn = r.BoundsMin; var mx = r.BoundsMax;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        // 로컬 AABB의 8개 코너를 월드 → 화면으로 투영해 화면상 경계 사각형을 만든다
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? mn.X : mx.X, (i & 2) == 0 ? mn.Y : mx.Y, (i & 4) == 0 ? mn.Z : mx.Z);
            var sp = cam.Project(Vector3.Transform(c, tg.World), out _);
            // 원근 카메라 뒤 코너가 있으면 화면 범위를 알 수 없으므로 통과시킨다
            if (sp == null) return true;
            minX = MathF.Min(minX, sp.Value.X); maxX = MathF.Max(maxX, sp.Value.X);
            minY = MathF.Min(minY, sp.Value.Y); maxY = MathF.Max(maxY, sp.Value.Y);
        }
        return px.X >= minX - thresholdPx && px.X <= maxX + thresholdPx && px.Y >= minY - thresholdPx && px.Y <= maxY + thresholdPx;
    }

    /// <summary>
    /// 월드 광선을 대상의 로컬 공간으로 옮긴다. 방향은 정규화하지 않으므로 로컬 t는 스케일이 섞인 값이며,
    /// 실제 거리 비교는 교점을 월드로 되돌려 계산한다.
    /// </summary>
    private static Ray ToLocal(in Ray r, PickTarget tg)
    {
        var o = Vector3.Transform(r.Origin, tg.WorldInverse);
        var d = Vector3.TransformNormal(r.Direction, tg.WorldInverse);
        return new Ray(o, d); // 비정규화: t는 로컬 스케일, 거리 비교는 월드로 변환해서
    }

    /// <summary>레이와 대상의 가장 가까운 삼각형 교차. 월드 거리를 반환.</summary>
    /// <remarks>
    /// 광선을 로컬로 옮겨 AABB로 먼저 거른 뒤 모든 삼각형을 Möller–Trumbore로 검사해 가장 가까운(t 최소) 삼각형을 고른다.
    /// </remarks>
    /// <param name="worldDist">광선 원점에서 교점까지의 월드 거리.</param>
    /// <param name="tri">맞은 삼각형 번호(Render 기준; TriToFace로 면 ID를 얻는다).</param>
    /// <param name="worldPos">교점 월드 위치.</param>
    /// <param name="excludeFace">이 면 ID에 속한 삼각형은 무시한다(가림 검사에서 자기 면 제외). −1이면 제외 없음.</param>
    public static bool RaycastTarget(PickTarget tg, in Ray worldRay, out float worldDist, out int tri, out Vector3 worldPos, int excludeFace = -1)
    {
        var lr = ToLocal(worldRay, tg);
        var r = tg.Render;
        worldDist = float.MaxValue; tri = -1; worldPos = default;
        // 레이가 로컬 AABB를 비껴가면 삼각형을 돌지 않는다(메시가 많은 씬의 호버/가림 검사 비용)
        if (r.TriangleCount == 0 || !RayIntersectsBox(lr, r.BoundsMin, r.BoundsMax)) return false;
        // 로컬 t 기준으로 가장 가까운 삼각형을 찾는다(같은 광선 안에서는 t 순서 = 거리 순서)
        float bestT = float.MaxValue;
        for (int i = 0; i < r.TriangleCount; i++)
        {
            if (excludeFace >= 0 && r.TriToFace[i] == excludeFace) continue;
            var a = r.Positions[r.Indices[i * 3]]; var b = r.Positions[r.Indices[i * 3 + 1]]; var c = r.Positions[r.Indices[i * 3 + 2]];
            if (RayTriangle(lr, a, b, c, out float t) && t < bestT) { bestT = t; tri = i; }
        }
        // 교점을 월드로 되돌려 진짜 거리를 계산
        if (tri < 0) return false;
        var localHit = lr.At(bestT);
        worldPos = Vector3.Transform(localHit, tg.World);
        worldDist = Vector3.Distance(worldRay.Origin, worldPos);
        return true;
    }

    /// <summary>월드 점이 다른 지오메트리에 가려졌는지(camera-based 선택용). 자기 자신의 면은 제외할 수 있다.</summary>
    /// <remarks>
    /// 점에서 카메라 쪽으로 향하는 광선을 만들고(원근 = 눈에서 점으로, 직교 = 시선 반대 방향으로 멀리 물러난 점에서 점으로)
    /// 점보다 eps 이상 앞에서 다른 삼각형에 맞으면 가려진 것으로 본다. eps(거리의 0.1%)는 점 자신이 놓인 면에 맞는 자기 교차를 흡수한다.
    /// </remarks>
    /// <param name="selfFace">이 노드의 이 면은 검사에서 제외(면 중심이 자기 면에 가려졌다고 판정하지 않도록).</param>
    public static bool IsOccluded(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector3 worldPoint, NodeId selfNode, int selfFace = -1)
    {
        // 광선 원점: 직교는 전방 반대로 1e5m 물러난 점(평행 광선), 원근은 눈
        Vector3 origin;
        if (cam.IsOrtho) origin = worldPoint - cam.Forward * 1e5f;
        else origin = cam.Eye;
        var dir = worldPoint - origin;
        float dist = dir.Length();
        if (dist < 1e-9f) return false;
        var ray = new Ray(origin, dir / dist);
        // 자기 교차 허용 오차
        float eps = MathF.Max(dist * 1e-3f, 1e-4f);
        foreach (var tg in targets)
        {
            int exclude = tg.Id == selfNode ? selfFace : -1;
            if (RaycastTarget(tg, ray, out float d, out _, out _, exclude) && d < dist - eps) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ 클릭 피킹

    /// <summary>
    /// 면 모드 클릭: 커서 광선과 가장 가까이 교차하는 삼각형의 면을 고른다(모든 대상 중 월드 거리 최소).
    /// 면은 광선이 실제로 맞아야 하므로 가림 검사가 따로 필요 없다.
    /// </summary>
    public static PickHit? PickFace(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector2 px)
    {
        var ray = cam.Unproject(px);
        PickHit? best = null;
        foreach (var tg in targets)
        {
            if (RaycastTarget(tg, ray, out float d, out int tri, out var pos) && (best == null || d < best.Value.Depth))
                best = new PickHit(tg.Id, tg.Render.TriToFace[tri], d, pos);
        }
        return best;
    }

    /// <summary>
    /// 정점 모드 클릭: 모든 정점을 화면에 투영해 커서에서 thresholdPx 이내 중 가장 가까운 것을 고른다.
    /// 픽셀 거리가 1px 이내로 비슷하면 깊이가 얕은(앞쪽) 정점을 우선한다. cameraBased면 가려진 정점은 건너뛴다.
    /// </summary>
    public static PickHit? PickVertex(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector2 px, bool cameraBased, float thresholdPx = VertexThresholdPx)
    {
        PickHit? best = null; float bestDist = float.MaxValue;
        foreach (var tg in targets)
        {
            var r = tg.Render;
            // 화면 경계 사각형으로 멀리 있는 메시를 먼저 거른다
            if (!ScreenBoundsMayContain(tg, cam, px, thresholdPx)) continue;
            for (int i = 0; i < r.PointCount; i++)
            {
                var w = Vector3.Transform(r.PointPositions[i], tg.World);
                var sp = cam.Project(w, out float depth);
                if (sp == null) continue;
                float d = Vector2.Distance(sp.Value, px);
                if (d > thresholdPx) continue;
                // 더 가까운 후보인지: 1px 이상 가깝거나, 거의 같은 거리면서 더 앞쪽
                bool better = d < bestDist - 1f || (MathF.Abs(d - bestDist) <= 1f && best != null && depth < best.Value.Depth);
                if (best == null || better)
                {
                    // 가림 검사는 비싸므로 후보가 될 때만 한다
                    if (cameraBased && IsOccluded(targets, cam, w, tg.Id)) continue;
                    best = new PickHit(tg.Id, r.PointToVertex[i], depth, w); bestDist = d;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// 엣지 모드 클릭: 각 선분을 화면에 투영해 커서와의 선분 거리가 thresholdPx 이내 중 가장 가까운 엣지를 고른다.
    /// 깊이는 화면 최근접점 비율 u로 양 끝 깊이를 보간한다. cameraBased면 그 최근접 월드 점의 가림을 검사한다.
    /// </summary>
    public static PickHit? PickEdge(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector2 px, bool cameraBased, float thresholdPx = EdgeThresholdPx)
    {
        PickHit? best = null; float bestDist = float.MaxValue;
        foreach (var tg in targets)
        {
            var r = tg.Render;
            if (!ScreenBoundsMayContain(tg, cam, px, thresholdPx)) continue;
            for (int i = 0; i < r.LineCount; i++)
            {
                var wa = Vector3.Transform(r.LinePositions[i * 2], tg.World);
                var wb = Vector3.Transform(r.LinePositions[i * 2 + 1], tg.World);
                var sa = cam.Project(wa, out float da); var sb = cam.Project(wb, out float db);
                if (sa == null || sb == null) continue;
                // 화면 선분 위 커서에 가장 가까운 비율 u
                float u = ClosestParam(sa.Value, sb.Value, px);
                var closest = Vector2.Lerp(sa.Value, sb.Value, u);
                float d = Vector2.Distance(closest, px);
                if (d > thresholdPx) continue;
                float depth = da + (db - da) * u;
                bool better = d < bestDist - 1f || (MathF.Abs(d - bestDist) <= 1f && best != null && depth < best.Value.Depth);
                if (best == null || better)
                {
                    var wp = Vector3.Lerp(wa, wb, u);
                    if (cameraBased && IsOccluded(targets, cam, wp, tg.Id)) continue;
                    best = new PickHit(tg.Id, r.LineToEdge[i], depth, wp); bestDist = d;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// 오브젝트 모드 클릭: 먼저 면 광선 피킹, 실패하면 와이어(엣지) 근처 클릭도 그 오브젝트로 본다. Component는 −1.
    /// </summary>
    public static PickHit? PickObject(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector2 px)
    {
        // 표면을 맞으면 그 노드
        var f = PickFace(targets, cam, px);
        if (f != null) return f.Value with { Component = -1 };
        // 표면을 빗나가도 와이어프레임 선 근처면 집히게(가림 무시)
        var e = PickEdge(targets, cam, px, cameraBased: false);
        return e?.ToSelItem() is { } s ? new PickHit(s.Node, -1, e.Value.Depth, e.Value.WorldPos) : null;
    }

    /// <summary>현재 선택 모드에 맞는 클릭 피킹으로 분기한다. UV 모드 등 그 밖의 모드는 null(App이 따로 처리).</summary>
    public static PickHit? Pick(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector2 px, SelectMode mode, bool cameraBased)
        => mode switch
        {
            SelectMode.Object => PickObject(targets, cam, px),
            SelectMode.Vertex => PickVertex(targets, cam, px, cameraBased),
            SelectMode.Edge => PickEdge(targets, cam, px, cameraBased),
            SelectMode.Face => PickFace(targets, cam, px),
            _ => null,
        };

    // ------------------------------------------------------------ 마키

    /// <summary>사각형(픽셀, min/max) 안의 컴포넌트. 정점: 투영점 포함, 엣지: 끝점 포함 또는 변 교차, 면: 중심 포함, 오브젝트: 정점/엣지 조건.</summary>
    /// <remarks>
    /// 마키 드래그 사각형으로 대상마다 컴포넌트를 모은다. cameraBased(App에서는 Marquee Select Through가 꺼진 경우)면 가려진 컴포넌트는 뺀다.
    /// 오브젝트 모드는 정점 하나라도 사각형 안이거나 엣지 하나라도 사각형을 지나면 그 노드를 선택한다(가림 무시).
    /// </remarks>
    public static List<SelItem> Marquee(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector2 min, Vector2 max, SelectMode mode, bool cameraBased)
    {
        var result = new List<SelItem>();
        foreach (var tg in targets)
        {
            var r = tg.Render;
            switch (mode)
            {
                // 정점: 투영 점이 사각형 안
                case SelectMode.Vertex:
                    for (int i = 0; i < r.PointCount; i++)
                    {
                        var w = Vector3.Transform(r.PointPositions[i], tg.World);
                        var sp = cam.Project(w, out _);
                        if (sp == null || !Inside(sp.Value, min, max)) continue;
                        if (cameraBased && IsOccluded(targets, cam, w, tg.Id)) continue;
                        result.Add(new SelItem(tg.Id, r.PointToVertex[i]));
                    }
                    break;
                // 엣지: 한쪽 끝이 안이거나 선분이 사각형을 가로지름. 가림은 중점으로 검사
                case SelectMode.Edge:
                    for (int i = 0; i < r.LineCount; i++)
                    {
                        var wa = Vector3.Transform(r.LinePositions[i * 2], tg.World);
                        var wb = Vector3.Transform(r.LinePositions[i * 2 + 1], tg.World);
                        var sa = cam.Project(wa, out _); var sb = cam.Project(wb, out _);
                        if (sa == null || sb == null) continue;
                        if (!(Inside(sa.Value, min, max) || Inside(sb.Value, min, max) || SegmentIntersectsRect(sa.Value, sb.Value, min, max))) continue;
                        if (cameraBased && IsOccluded(targets, cam, (wa + wb) * 0.5f, tg.Id)) continue;
                        result.Add(new SelItem(tg.Id, r.LineToEdge[i]));
                    }
                    break;
                // 면: 면 중심 투영이 사각형 안. 가림 검사에서 자기 면은 제외
                case SelectMode.Face:
                    for (int i = 0; i < r.FaceCenterCount; i++)
                    {
                        var w = Vector3.Transform(r.FaceCenters[i], tg.World);
                        var sp = cam.Project(w, out _);
                        if (sp == null || !Inside(sp.Value, min, max)) continue;
                        int f = r.FaceCenterToFace[i];
                        if (cameraBased && IsOccluded(targets, cam, w, tg.Id, f)) continue;
                        result.Add(new SelItem(tg.Id, f));
                    }
                    break;
                // 오브젝트: 정점 포함 → 엣지 교차 순으로 하나라도 맞으면 즉시 중단
                case SelectMode.Object:
                    {
                        bool hit = false;
                        for (int i = 0; i < r.PointCount && !hit; i++)
                        {
                            var sp = cam.Project(Vector3.Transform(r.PointPositions[i], tg.World), out _);
                            if (sp != null && Inside(sp.Value, min, max)) hit = true;
                        }
                        for (int i = 0; i < r.LineCount && !hit; i++)
                        {
                            var sa = cam.Project(Vector3.Transform(r.LinePositions[i * 2], tg.World), out _);
                            var sb = cam.Project(Vector3.Transform(r.LinePositions[i * 2 + 1], tg.World), out _);
                            if (sa != null && sb != null && SegmentIntersectsRect(sa.Value, sb.Value, min, max)) hit = true;
                        }
                        if (hit) result.Add(new SelItem(tg.Id, -1));
                        break;
                    }
            }
        }
        return result;
    }

    // ------------------------------------------------------------ 2D 헬퍼

    /// <summary>
    /// 2D 선분 a→b 위에서 점 p에 가장 가까운 점의 비율 u(0~1로 클램프). 선분 길이가 0이면 0.
    /// </summary>
    public static float ClosestParam(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a; float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return 0;
        return Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
    }

    /// <summary>점 p가 축 정렬 사각형 [min, max] 안(경계 포함)인지.</summary>
    public static bool Inside(Vector2 p, Vector2 min, Vector2 max) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;

    /// <summary>
    /// 2D 선분이 축 정렬 사각형과 겹치는지. 끝점이 안이면 바로 true, 아니면 Liang–Barsky 클리핑으로
    /// 선분 파라미터 구간 [t0, t1]이 사각형 네 경계에 의해 비지 않는지 확인한다.
    /// </summary>
    public static bool SegmentIntersectsRect(Vector2 a, Vector2 b, Vector2 min, Vector2 max)
    {
        if (Inside(a, min, max) || Inside(b, min, max)) return true;
        // Liang–Barsky
        float t0 = 0, t1 = 1; var d = b - a;
        // p[i]·t ≤ q[i] 형태의 네 부등식(왼쪽/오른쪽/아래/위 경계)
        float[] p = { -d.X, d.X, -d.Y, d.Y };
        float[] q = { a.X - min.X, max.X - a.X, a.Y - min.Y, max.Y - a.Y };
        for (int i = 0; i < 4; i++)
        {
            // 경계와 평행: 바깥쪽이면 교차 불가
            if (MathF.Abs(p[i]) < 1e-12f) { if (q[i] < 0) return false; continue; }
            float t = q[i] / p[i];
            // 진입 경계(p &lt; 0)는 t0을, 진출 경계(p &gt; 0)는 t1을 좁힌다. 구간이 뒤집히면 교차 없음
            if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        return true;
    }
}
