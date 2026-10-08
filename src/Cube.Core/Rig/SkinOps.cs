using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Rig;

/// <summary>바인드에 쓰는 조인트 정보: 월드 행렬과 자식 조인트 월드 위치(본 선분).</summary>
public sealed record JointInfo(NodeId Id, Matrix4x4 World, IReadOnlyList<Vector3> ChildPositions)
{
    public Vector3 Position => World.Translation;
}

public enum PaintMode { Replace, Add, Smooth }

/// <summary>스무스 바인드, LBS 변형, 가중치 페인트/정규화.</summary>
public static class SkinOps
{
    /// <summary>
    /// Maya Smooth Bind(closest distance): 정점마다 각 조인트의 본 선분(자식이 없으면 점)까지의 거리로 1/d² 가중치를 주고
    /// 상위 maxInfluences개만 남겨 정규화한다. 0.01 미만은 버린다.
    /// </summary>
    public static SkinCluster SmoothBind(PolyMesh mesh, Matrix4x4 meshWorld, IReadOnlyList<JointInfo> joints, int maxInfluences = SkinCluster.MaxInfluences, float dropoff = 2f)
    {
        var skin = new SkinCluster { MeshBindWorld = meshWorld };
        foreach (var j in joints)
        {
            skin.Joints.Add(j.Id);
            Matrix4x4.Invert(j.World, out var inv);
            skin.BindInverse.Add(inv);
        }
        skin.EnsureSize(mesh.VertexCount);
        if (joints.Count == 0) return skin;
        var cand = new List<(int joint, float w)>();
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            if (!mesh.Verts[v].Alive) continue;
            var p = Vector3.Transform(mesh.Verts[v].Position, meshWorld);
            cand.Clear();
            for (int j = 0; j < joints.Count; j++)
            {
                float d = DistanceToBones(p, joints[j]);
                cand.Add((j, 1f / MathF.Pow(d + 1e-4f, dropoff)));
            }
            cand.Sort((a, b) => b.w.CompareTo(a.w));
            if (cand.Count > maxInfluences) cand.RemoveRange(maxInfluences, cand.Count - maxInfluences);
            skin.Weights[v] = cand.Select(c => (c.joint, c.w)).ToList();
            Normalize(skin, v);
        }
        return skin;
    }

    private static float DistanceToBones(Vector3 p, JointInfo j)
    {
        float best = Vector3.Distance(p, j.Position);
        foreach (var c in j.ChildPositions) best = MathF.Min(best, DistanceToSegment(p, j.Position, c));
        return best;
    }

    public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a; float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return Vector3.Distance(p, a);
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>정점 가중치를 합 1로 정규화하고 0.01 미만은 버린다(전부 0이면 그대로 둔다).</summary>
    public static void Normalize(SkinCluster skin, int vertex, float prune = 0.01f)
    {
        if (vertex >= skin.Weights.Length || skin.Weights[vertex] == null) return;
        var list = skin.Weights[vertex]!;
        float sum = 0; foreach (var (_, w) in list) sum += w;
        if (sum <= 1e-12f) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            float w = list[i].weight / sum;
            if (w < prune) list.RemoveAt(i); else list[i] = (list[i].joint, w);
        }
        sum = 0; foreach (var (_, w) in list) sum += w;
        if (sum > 1e-12f && MathF.Abs(sum - 1f) > 1e-6f) for (int i = 0; i < list.Count; i++) list[i] = (list[i].joint, list[i].weight / sum);
    }

    /// <summary>
    /// 선형 블렌드 스키닝. 메시 로컬 바인드 위치 → 월드(바인드 메시 행렬) → Σ w·BindInv·JointWorld → 현재 메시 로컬.
    /// 가중치가 없는 정점은 그대로 둔다. out 배열 길이는 mesh.VertexCount.
    /// </summary>
    public static void Deform(PolyMesh mesh, SkinCluster skin, Func<NodeId, Matrix4x4?> jointWorld, Matrix4x4 meshWorldInverse, Vector3[] outPositions)
    {
        var skinMats = new Matrix4x4[skin.Joints.Count];
        var valid = new bool[skin.Joints.Count];
        for (int j = 0; j < skin.Joints.Count; j++)
        {
            var w = jointWorld(skin.Joints[j]);
            if (w == null) continue;
            skinMats[j] = skin.BindInverse[j] * w.Value;
            valid[j] = true;
        }
        var verts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mesh.Verts);
        var weights = skin.Weights;
        int vc = Math.Min(verts.Length, outPositions.Length);
        for (int v = 0; v < vc; v++)
        {
            ref readonly var vert = ref verts[v];
            var local = vert.Position;
            var list = v < weights.Length ? weights[v] : null;
            if (!vert.Alive || list == null || list.Count == 0) { outPositions[v] = local; continue; }
            var pw = Vector3.Transform(local, skin.MeshBindWorld);
            var acc = Vector3.Zero; float sum = 0;
            for (int k = 0; k < list.Count; k++)
            {
                var (j, wgt) = list[k];
                if (j < 0 || j >= skinMats.Length || !valid[j]) continue;
                acc += Vector3.Transform(pw, skinMats[j]) * wgt; sum += wgt;
            }
            if (sum <= 1e-12f) { outPositions[v] = local; continue; }
            if (MathF.Abs(sum - 1f) > 1e-4f) acc += pw * (1f - sum); // 합이 1이 아니면 남은 비율은 바인드 위치
            outPositions[v] = Vector3.Transform(acc, meshWorldInverse);
        }
    }

    /// <summary>
    /// 한 정점에 페인트 적용. Replace: w_j → lerp(w_j, value, amount); Add: w_j += value·amount; Smooth: w_j → lerp(w_j, 이웃 평균, amount).
    /// 다른 조인트의 가중치는 비율을 유지하며 합이 1이 되도록 조정한다(Maya normalize=interactive).
    /// </summary>
    public static void PaintVertex(SkinCluster skin, int vertex, int joint, PaintMode mode, float value, float amount, float neighborAverage = 0f)
    {
        float cur = skin.GetWeight(vertex, joint);
        float target = mode switch
        {
            PaintMode.Replace => cur + (value - cur) * amount,
            PaintMode.Add => cur + value * amount,
            _ => cur + (neighborAverage - cur) * amount,
        };
        target = Math.Clamp(target, 0f, 1f);
        SetWeightNormalized(skin, vertex, joint, target);
    }

    /// <summary>joint의 가중치를 target으로 두고 나머지 조인트를 비율대로 (1-target)에 맞춘다. 나머지가 없으면 joint가 1이 된다.</summary>
    public static void SetWeightNormalized(SkinCluster skin, int vertex, int joint, float target)
    {
        skin.EnsureSize(vertex + 1);
        var list = skin.Weights[vertex] ??= new List<(int, float)>();
        float others = 0; foreach (var (j, w) in list) if (j != joint) others += w;
        if (others <= 1e-9f) target = 1f;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].joint == joint) { list.RemoveAt(i); continue; }
            float w = others > 1e-9f ? list[i].weight / others * (1f - target) : 0f;
            if (w < 1e-4f) list.RemoveAt(i); else list[i] = (list[i].joint, w);
        }
        if (target > 1e-4f) list.Add((joint, target));
        // 영향 수 제한: 가장 작은 것부터 버리고 다시 정규화
        while (list.Count > SkinCluster.MaxInfluences)
        {
            int min = 0; for (int i = 1; i < list.Count; i++) if (list[i].weight < list[min].weight) min = i;
            list.RemoveAt(min);
        }
        Normalize(skin, vertex, prune: 0f);
    }

    /// <summary>이웃 정점들의 joint 가중치 평균(Smooth 모드용).</summary>
    public static float NeighborAverage(PolyMesh mesh, SkinCluster skin, int vertex, int joint)
    {
        float sum = 0; int n = 0;
        foreach (int he in mesh.VertexOutgoing(vertex))
        {
            int other = mesh.Hes[mesh.Hes[he].Next].Vertex;
            sum += skin.GetWeight(other, joint); n++;
            int prev = mesh.Hes[mesh.Hes[he].Prev].Vertex;
            sum += skin.GetWeight(prev, joint); n++;
        }
        return n > 0 ? sum / n : skin.GetWeight(vertex, joint);
    }

    /// <summary>살아있는 모든 정점을 정규화한다.</summary>
    public static void NormalizeAll(PolyMesh mesh, SkinCluster skin)
    {
        for (int v = 0; v < mesh.VertexCount && v < skin.Weights.Length; v++) if (mesh.Verts[v].Alive) Normalize(skin, v);
    }
}
