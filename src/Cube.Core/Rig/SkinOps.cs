using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Rig;

/// <summary>바인드에 쓰는 조인트 정보: 월드 행렬과 자식 조인트 월드 위치(본 선분).</summary>
/// <remarks>
/// App 측이 조인트 노드에서 만들어 <see cref="SkinOps.SmoothBind"/>에 넘긴다. <c>Id</c> = 조인트 노드 ID,
/// <c>World</c> = 바인드 시점 조인트 월드 행렬, <c>ChildPositions</c> = 자식 조인트 월드 위치(본 선분의 끝점들).
/// </remarks>
public sealed record JointInfo(NodeId Id, Matrix4x4 World, IReadOnlyList<Vector3> ChildPositions)
{
    /// <summary>조인트 월드 위치(본 선분의 시작점).</summary>
    public Vector3 Position => World.Translation;
}

/// <summary>
/// 가중치 페인트 모드. Replace = 값으로 다가감, Add = 값을 더함, Smooth = 이웃 평균으로 다가감(각 정도는 amount).
/// </summary>
public enum PaintMode { Replace, Add, Smooth }

/// <summary>스무스 바인드, LBS 변형, 가중치 페인트/정규화.</summary>
public static class SkinOps
{
    /// <summary>
    /// Maya Smooth Bind(closest distance): 정점마다 각 조인트의 본 선분(자식이 없으면 점)까지의 거리로 1/d² 가중치를 주고
    /// 상위 maxInfluences개만 남겨 정규화한다. 0.01 미만은 버린다.
    /// </summary>
    /// <param name="mesh">바인드할 메시(정점 위치는 메시 로컬).</param>
    /// <param name="meshWorld">바인드 시점 메시 월드 행렬(<c>SkinCluster.MeshBindWorld</c>로 저장).</param>
    /// <param name="joints">영향 조인트 목록(순서가 곧 스킨 조인트 슬롯 번호).</param>
    /// <param name="maxInfluences">정점당 최대 영향 조인트 수.</param>
    /// <param name="dropoff">거리 감쇠 지수(가중치 = 1 / (d + ε)^dropoff, 기본 2).</param>
    /// <returns>새 스킨 클러스터(조인트 바인드 역행렬 포함).</returns>
    public static SkinCluster SmoothBind(PolyMesh mesh, Matrix4x4 meshWorld, IReadOnlyList<JointInfo> joints, int maxInfluences = SkinCluster.MaxInfluences, float dropoff = 2f)
    {
        // 조인트 슬롯과 바인드 역행렬(= 바인드 시점 조인트 월드의 역)을 기록한다.
        var skin = new SkinCluster { MeshBindWorld = meshWorld };
        foreach (var j in joints)
        {
            skin.Joints.Add(j.Id);
            Matrix4x4.Invert(j.World, out var inv);
            skin.BindInverse.Add(inv);
        }
        // 정점별 가중치 배열 크기를 정점 수에 맞춘다.
        skin.EnsureSize(mesh.VertexCount);
        if (joints.Count == 0) return skin;
        var cand = new List<(int joint, float w)>();
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            if (!mesh.Verts[v].Alive) continue;
            // 정점 위치를 월드로 옮겨 각 조인트 본까지의 거리로 후보 가중치를 만든다(ε = 1e-4로 0 나눗셈 방지).
            var p = Vector3.Transform(mesh.Verts[v].Position, meshWorld);
            cand.Clear();
            for (int j = 0; j < joints.Count; j++)
            {
                float d = DistanceToBones(p, joints[j]);
                cand.Add((j, 1f / MathF.Pow(d + 1e-4f, dropoff)));
            }
            // 가중치 큰 순으로 정렬해 상위 maxInfluences개만 남기고 정규화(0.01 미만 제거).
            cand.Sort((a, b) => b.w.CompareTo(a.w));
            if (cand.Count > maxInfluences) cand.RemoveRange(maxInfluences, cand.Count - maxInfluences);
            skin.Weights[v] = cand.Select(c => (c.joint, c.w)).ToList();
            Normalize(skin, v);
        }
        return skin;
    }

    /// <summary>점 p에서 조인트 j의 본들(조인트 → 각 자식 선분)과 조인트 점 자체까지의 최소 거리.</summary>
    private static float DistanceToBones(Vector3 p, JointInfo j)
    {
        float best = Vector3.Distance(p, j.Position);
        foreach (var c in j.ChildPositions) best = MathF.Min(best, DistanceToSegment(p, j.Position, c));
        return best;
    }

    /// <summary>점 p와 선분 ab 사이의 최단 거리(퇴화 선분이면 a까지의 거리). 투영 매개변수 t를 [0,1]로 클램프한다.</summary>
    public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a; float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return Vector3.Distance(p, a);
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>정점 가중치를 합 1로 정규화하고 0.01 미만은 버린다(전부 0이면 그대로 둔다).</summary>
    /// <param name="skin">대상 스킨.</param>
    /// <param name="vertex">정점 ID.</param>
    /// <param name="prune">정규화 후 이 값 미만인 영향은 제거한다(0이면 제거 안 함).</param>
    public static void Normalize(SkinCluster skin, int vertex, float prune = 0.01f)
    {
        if (vertex >= skin.Weights.Length || skin.Weights[vertex] == null) return;
        var list = skin.Weights[vertex]!;
        float sum = 0; foreach (var (_, w) in list) sum += w;
        if (sum <= 1e-12f) return;
        // 1차: 합으로 나누면서 작은 영향 제거(뒤에서부터 지워 인덱스 유지).
        for (int i = list.Count - 1; i >= 0; i--)
        {
            float w = list[i].weight / sum;
            if (w < prune) list.RemoveAt(i); else list[i] = (list[i].joint, w);
        }
        // 2차: 제거로 합이 1에서 벗어났으면 다시 나눈다.
        sum = 0; foreach (var (_, w) in list) sum += w;
        if (sum > 1e-12f && MathF.Abs(sum - 1f) > 1e-6f) for (int i = 0; i < list.Count; i++) list[i] = (list[i].joint, list[i].weight / sum);
    }

    /// <summary>
    /// 선형 블렌드 스키닝. 메시 로컬 바인드 위치 → 월드(바인드 메시 행렬) → Σ w·BindInv·JointWorld → 현재 메시 로컬.
    /// 가중치가 없는 정점은 그대로 둔다. out 배열 길이는 mesh.VertexCount.
    /// </summary>
    /// <param name="mesh">바인드 위치가 들어 있는 메시(정점은 바뀌지 않는다; 표시 전용 변형).</param>
    /// <param name="skin">스킨 클러스터.</param>
    /// <param name="jointWorld">조인트 ID → 현재 월드 행렬(재생 포즈 포함; 없으면 null이고 그 조인트는 무시).</param>
    /// <param name="meshWorldInverse">현재 메시 월드 행렬의 역(결과를 메시 로컬로 되돌림).</param>
    /// <param name="outPositions">결과 정점 위치(메시 로컬).</param>
    public static void Deform(PolyMesh mesh, SkinCluster skin, Func<NodeId, Matrix4x4?> jointWorld, Matrix4x4 meshWorldInverse, Vector3[] outPositions)
    {
        // 조인트마다 스킨 행렬 = BindInverse · 현재 월드(행벡터 규약: 바인드 공간 → 조인트 로컬 → 현재 월드)를 미리 계산.
        var skinMats = new Matrix4x4[skin.Joints.Count];
        var valid = new bool[skin.Joints.Count];
        for (int j = 0; j < skin.Joints.Count; j++)
        {
            var w = jointWorld(skin.Joints[j]);
            if (w == null) continue;
            skinMats[j] = skin.BindInverse[j] * w.Value;
            valid[j] = true;
        }
        // 정점 리스트를 Span으로 직접 읽어 복사 없이 순회한다(매 프레임 호출되는 핫 경로).
        var verts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mesh.Verts);
        var weights = skin.Weights;
        int vc = Math.Min(verts.Length, outPositions.Length);
        for (int v = 0; v < vc; v++)
        {
            ref readonly var vert = ref verts[v];
            var local = vert.Position;
            var list = v < weights.Length ? weights[v] : null;
            if (!vert.Alive || list == null || list.Count == 0) { outPositions[v] = local; continue; }
            // 바인드 시점 월드 위치에 각 스킨 행렬을 적용해 가중 합한다(유효하지 않은 조인트 슬롯은 건너뜀).
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
    /// <param name="skin">대상 스킨.</param>
    /// <param name="vertex">정점 ID.</param>
    /// <param name="joint">칠하는 조인트 슬롯.</param>
    /// <param name="mode">페인트 모드.</param>
    /// <param name="value">브러시 값(Replace 목표값 / Add 증분).</param>
    /// <param name="amount">적용 강도(브러시 감쇠가 곱해진 0..1).</param>
    /// <param name="neighborAverage">Smooth 모드에서 쓸 이웃 평균(<see cref="NeighborAverage"/>).</param>
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
    /// <remarks>
    /// 단계: ① 다른 조인트 합(others) 계산 → ② 다른 조인트를 (1 − target) × 원래 비율로 재분배(1e-4 미만 제거) →
    /// ③ joint를 target으로 추가 → ④ 영향 수가 최대를 넘으면 가장 작은 것부터 제거 → ⑤ 정규화(제거 없음).
    /// </remarks>
    public static void SetWeightNormalized(SkinCluster skin, int vertex, int joint, float target)
    {
        // 정점 가중치 목록이 없으면 새로 만든다.
        skin.EnsureSize(vertex + 1);
        var list = skin.Weights[vertex] ??= new List<(int, float)>();
        // 다른 조인트에 가중치가 없으면 이 조인트가 전부(1)를 가져야 한다.
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
    /// <remarks>
    /// 정점에서 나가는 하프에지마다 다음 정점(Next의 시작)과 이전 정점(Prev의 시작)을 더한다.
    /// 내부 정점에서는 이웃이 두 번씩 세어지지만 평균이므로 결과에 영향이 거의 없다. 이웃이 없으면 자기 값.
    /// </remarks>
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
    /// <remarks>가중치 배열 길이와 정점 수 중 작은 쪽까지 처리하며 기본 prune(0.01)을 적용한다.</remarks>
    public static void NormalizeAll(PolyMesh mesh, SkinCluster skin)
    {
        for (int v = 0; v < mesh.VertexCount && v < skin.Weights.Length; v++) if (mesh.Verts[v].Alive) Normalize(skin, v);
    }
}
