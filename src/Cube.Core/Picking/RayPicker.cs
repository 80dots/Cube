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
