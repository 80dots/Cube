using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Picking;

/// <summary>피킹 대상 하나: 노드 + 메시 + 테셀레이션 결과 + 월드 행렬.</summary>
public sealed class PickTarget
{
    public required NodeId Id { get; init; }
    public required PolyMesh Mesh { get; init; }
    public required RenderMeshData Render { get; init; }
    public required Matrix4x4 World { get; init; }
    private Matrix4x4? _inv;
    public Matrix4x4 WorldInverse { get { if (_inv == null) { Matrix4x4.Invert(World, out var i); _inv = i; } return _inv.Value; } }
}

public readonly record struct PickHit(NodeId Node, int Component, float Depth, Vector3 WorldPos)
{
    public SelItem ToSelItem() => new(Node, Component);
}

/// <summary>
/// 레이/스크린 공간 피킹과 마키 선택. Maya 규칙: 면은 레이 교차, 엣지/정점은 화면 거리 임계값, 깊이 동률 우선.
/// camera-based 선택 시 가려진 컴포넌트는 제외한다.
/// </summary>
public static class RayPicker
{
    public const float EdgeThresholdPx = 6f;
    public const float VertexThresholdPx = 8f;

    // ------------------------------------------------------------ 레이-삼각형

    /// <summary>Möller–Trumbore. 양면.</summary>
    public static bool RayTriangle(in Ray r, in Vector3 a, in Vector3 b, in Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(r.Direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        var s = r.Origin - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < -1e-6f || u > 1 + 1e-6f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(r.Direction, q) * inv;
        if (v < -1e-6f || u + v > 1 + 1e-6f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 1e-6f;
    }

    /// <summary>슬랩 방식 레이-AABB 교차(t ≥ 0). 방향 성분이 0이면 그 축은 원점이 범위 안일 때만 통과. 박스는 약간 넓혀 경계 삼각형을 놓치지 않는다.</summary>
    public static bool RayIntersectsBox(in Ray r, Vector3 min, Vector3 max)
    {
        float pad = MathF.Max(1e-5f, (max - min).Length() * 1e-4f);
        min -= new Vector3(pad); max += new Vector3(pad);
        float tmin = 0f, tmax = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? r.Origin.X : axis == 1 ? r.Origin.Y : r.Origin.Z;
            float d = axis == 0 ? r.Direction.X : axis == 1 ? r.Direction.Y : r.Direction.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-12f) { if (o < lo || o > hi) return false; continue; }
            float inv = 1f / d;
            float t0 = (lo - o) * inv, t1 = (hi - o) * inv;
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
        var r = tg.Render;
        if (r.PointCount == 0) return false;
        var mn = r.BoundsMin; var mx = r.BoundsMax;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? mn.X : mx.X, (i & 2) == 0 ? mn.Y : mx.Y, (i & 4) == 0 ? mn.Z : mx.Z);
            var sp = cam.Project(Vector3.Transform(c, tg.World), out _);
            if (sp == null) return true;
            minX = MathF.Min(minX, sp.Value.X); maxX = MathF.Max(maxX, sp.Value.X);
            minY = MathF.Min(minY, sp.Value.Y); maxY = MathF.Max(maxY, sp.Value.Y);
        }
        return px.X >= minX - thresholdPx && px.X <= maxX + thresholdPx && px.Y >= minY - thresholdPx && px.Y <= maxY + thresholdPx;
    }

    private static Ray ToLocal(in Ray r, PickTarget tg)
    {
        var o = Vector3.Transform(r.Origin, tg.WorldInverse);
        var d = Vector3.TransformNormal(r.Direction, tg.WorldInverse);
        return new Ray(o, d); // 비정규화: t는 로컬 스케일, 거리 비교는 월드로 변환해서
    }

    /// <summary>레이와 대상의 가장 가까운 삼각형 교차. 월드 거리를 반환.</summary>
    public static bool RaycastTarget(PickTarget tg, in Ray worldRay, out float worldDist, out int tri, out Vector3 worldPos, int excludeFace = -1)
    {
        var lr = ToLocal(worldRay, tg);
        var r = tg.Render;
        worldDist = float.MaxValue; tri = -1; worldPos = default;
        // 레이가 로컬 AABB를 비껴가면 삼각형을 돌지 않는다(메시가 많은 씬의 호버/가림 검사 비용)
        if (r.TriangleCount == 0 || !RayIntersectsBox(lr, r.BoundsMin, r.BoundsMax)) return false;
        float bestT = float.MaxValue;
        for (int i = 0; i < r.TriangleCount; i++)
        {
            if (excludeFace >= 0 && r.TriToFace[i] == excludeFace) continue;
            var a = r.Positions[r.Indices[i * 3]]; var b = r.Positions[r.Indices[i * 3 + 1]]; var c = r.Positions[r.Indices[i * 3 + 2]];
            if (RayTriangle(lr, a, b, c, out float t) && t < bestT) { bestT = t; tri = i; }
        }
        if (tri < 0) return false;
        var localHit = lr.At(bestT);
        worldPos = Vector3.Transform(localHit, tg.World);
        worldDist = Vector3.Distance(worldRay.Origin, worldPos);
        return true;
    }

    /// <summary>월드 점이 다른 지오메트리에 가려졌는지(camera-based 선택용). 자기 자신의 면은 제외할 수 있다.</summary>
    public static bool IsOccluded(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector3 worldPoint, NodeId selfNode, int selfFace = -1)
    {
        Vector3 origin;
        if (cam.IsOrtho) origin = worldPoint - cam.Forward * 1e5f;
        else origin = cam.Eye;
        var dir = worldPoint - origin;
        float dist = dir.Length();
        if (dist < 1e-9f) return false;
        var ray = new Ray(origin, dir / dist);
        float eps = MathF.Max(dist * 1e-3f, 1e-4f);
        foreach (var tg in targets)
        {
            int exclude = tg.Id == selfNode ? selfFace : -1;
            if (RaycastTarget(tg, ray, out float d, out _, out _, exclude) && d < dist - eps) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ 클릭 피킹

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

    public static PickHit? PickVertex(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector2 px, bool cameraBased, float thresholdPx = VertexThresholdPx)
    {
        PickHit? best = null; float bestDist = float.MaxValue;
        foreach (var tg in targets)
        {
            var r = tg.Render;
            if (!ScreenBoundsMayContain(tg, cam, px, thresholdPx)) continue;
            for (int i = 0; i < r.PointCount; i++)
            {
                var w = Vector3.Transform(r.PointPositions[i], tg.World);
                var sp = cam.Project(w, out float depth);
                if (sp == null) continue;
                float d = Vector2.Distance(sp.Value, px);
                if (d > thresholdPx) continue;
                bool better = d < bestDist - 1f || (MathF.Abs(d - bestDist) <= 1f && best != null && depth < best.Value.Depth);
                if (best == null || better)
                {
                    if (cameraBased && IsOccluded(targets, cam, w, tg.Id)) continue;
                    best = new PickHit(tg.Id, r.PointToVertex[i], depth, w); bestDist = d;
                }
            }
        }
        return best;
    }

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

    public static PickHit? PickObject(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector2 px)
    {
        var f = PickFace(targets, cam, px);
        if (f != null) return f.Value with { Component = -1 };
        var e = PickEdge(targets, cam, px, cameraBased: false);
        return e?.ToSelItem() is { } s ? new PickHit(s.Node, -1, e.Value.Depth, e.Value.WorldPos) : null;
    }

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
    public static List<SelItem> Marquee(IReadOnlyList<PickTarget> targets, CameraProjection cam, Vector2 min, Vector2 max, SelectMode mode, bool cameraBased)
    {
        var result = new List<SelItem>();
        foreach (var tg in targets)
        {
            var r = tg.Render;
            switch (mode)
            {
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

    public static float ClosestParam(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a; float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return 0;
        return Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
    }

    public static bool Inside(Vector2 p, Vector2 min, Vector2 max) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;

    public static bool SegmentIntersectsRect(Vector2 a, Vector2 b, Vector2 min, Vector2 max)
    {
        if (Inside(a, min, max) || Inside(b, min, max)) return true;
        // Liang–Barsky
        float t0 = 0, t1 = 1; var d = b - a;
        float[] p = { -d.X, d.X, -d.Y, d.Y };
        float[] q = { a.X - min.X, max.X - a.X, a.Y - min.Y, max.Y - a.Y };
        for (int i = 0; i < 4; i++)
        {
            if (MathF.Abs(p[i]) < 1e-12f) { if (q[i] < 0) return false; continue; }
            float t = q[i] / p[i];
            if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        return true;
    }
}
