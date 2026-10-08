using System.Numerics;

namespace Cube.Core.Scene;

/// <summary>Maya joint. 조인트는 JointShape를 가진 SceneNode이며, 자식 조인트로 본(bone)을 그린다.</summary>
/// <remarks>조인트의 위치·방향은 노드의 Transform3가 정한다. 셰이프는 표시 반지름만 가진다.</remarks>
public sealed class JointShape : Shape
{
    /// <summary>표시 반지름(월드 단위). Maya "Joint Size".</summary>
    public float Radius = 0.08f;
}

/// <summary>
/// Maya skinCluster. 메시 정점마다 조인트 영향(최대 4개)과 가중치를 가진다.
/// 바인드 시점의 조인트 월드 역행렬과 메시 월드 행렬을 저장하고, 표시용 변형은 선형 블렌드 스키닝(LBS)으로 계산한다.
/// </summary>
/// <remarks>
/// 가중치의 조인트 값은 NodeId가 아니라 <see cref="Joints"/> 목록의 인덱스다. 변형식(SkinOps.Deform):
/// v' = Σ w_j · v · MeshBindWorld · BindInverse[j] · JointWorld[j] (행벡터 규약).
/// </remarks>
public sealed class SkinCluster
{
    /// <summary>정점당 최대 영향 조인트 수(glTF/게임 엔진 호환 = 4).</summary>
    public const int MaxInfluences = 4;

    /// <summary>영향 조인트 노드 목록(가중치의 조인트 인덱스가 이 순서를 가리킨다).</summary>
    public readonly List<NodeId> Joints = new();
    /// <summary>바인드 시점 조인트 월드 행렬의 역행렬(Joints와 같은 순서).</summary>
    public readonly List<Matrix4x4> BindInverse = new();
    /// <summary>바인드 시점 메시 월드 행렬.</summary>
    public Matrix4x4 MeshBindWorld = Matrix4x4.Identity;
    /// <summary>정점 ID → (조인트 인덱스, 가중치) 목록. 길이가 정점 수보다 짧거나 null 항목이면 가중치 없음.</summary>
    public List<(int joint, float weight)>?[] Weights = Array.Empty<List<(int, float)>?>();

    /// <summary>조인트 노드의 인덱스(없으면 -1).</summary>
    public int JointIndexOf(NodeId id) => Joints.IndexOf(id);

    /// <summary>Weights 배열을 최소 vertexCount 길이로 늘린다(새 칸은 null = 가중치 없음).</summary>
    public void EnsureSize(int vertexCount)
    {
        if (Weights.Length >= vertexCount) return;
        Array.Resize(ref Weights, vertexCount);
    }

    /// <summary>정점의 특정 조인트(인덱스) 가중치. 없으면 0.</summary>
    public float GetWeight(int vertex, int joint)
    {
        if (vertex < 0 || vertex >= Weights.Length || Weights[vertex] == null) return 0f;
        foreach (var (j, w) in Weights[vertex]!) if (j == joint) return w;
        return 0f;
    }

    /// <summary>가중치를 설정한다(정규화하지 않음). 0이면 항목을 제거한다.</summary>
    public void SetWeight(int vertex, int joint, float weight)
    {
        EnsureSize(vertex + 1);
        var list = Weights[vertex] ??= new List<(int, float)>();
        // 기존 항목이 있으면 갱신 또는 제거.
        for (int i = 0; i < list.Count; i++)
            if (list[i].joint == joint)
            {
                if (weight <= 0f) list.RemoveAt(i); else list[i] = (joint, weight);
                return;
            }
        // 없으면 양수일 때만 추가.
        if (weight > 0f) list.Add((joint, weight));
    }

    /// <summary>정점 가중치 목록의 복사본(없으면 빈 목록). 페인트 Undo 스냅샷용.</summary>
    public List<(int joint, float weight)> CopyWeights(int vertex)
        => vertex < Weights.Length && Weights[vertex] != null ? new List<(int, float)>(Weights[vertex]!) : new List<(int, float)>();

    /// <summary>정점 가중치 목록을 복사해 통째로 교체한다.</summary>
    public void SetWeights(int vertex, List<(int joint, float weight)> weights)
    {
        EnsureSize(vertex + 1);
        Weights[vertex] = new List<(int, float)>(weights);
    }

    /// <summary>조인트·바인드 행렬·가중치(정점별 목록까지)를 복사한 깊은 복사본.</summary>
    public SkinCluster Clone()
    {
        var c = new SkinCluster { MeshBindWorld = MeshBindWorld };
        c.Joints.AddRange(Joints);
        c.BindInverse.AddRange(BindInverse);
        c.Weights = new List<(int, float)>?[Weights.Length];
        for (int i = 0; i < Weights.Length; i++) c.Weights[i] = Weights[i] != null ? new List<(int, float)>(Weights[i]!) : null;
        return c;
    }
}
