using System.Numerics;

namespace Cube.Core.Scene;

/// <summary>Maya joint. 조인트는 JointShape를 가진 SceneNode이며, 자식 조인트로 본(bone)을 그린다.</summary>
public sealed class JointShape : Shape
{
    /// <summary>표시 반지름(월드 단위). Maya "Joint Size".</summary>
    public float Radius = 0.08f;
}

/// <summary>
/// Maya skinCluster. 메시 정점마다 조인트 영향(최대 4개)과 가중치를 가진다.
/// 바인드 시점의 조인트 월드 역행렬과 메시 월드 행렬을 저장하고, 표시용 변형은 선형 블렌드 스키닝(LBS)으로 계산한다.
/// </summary>
public sealed class SkinCluster
{
    public const int MaxInfluences = 4;

    public readonly List<NodeId> Joints = new();
    /// <summary>바인드 시점 조인트 월드 행렬의 역행렬(Joints와 같은 순서).</summary>
    public readonly List<Matrix4x4> BindInverse = new();
    /// <summary>바인드 시점 메시 월드 행렬.</summary>
    public Matrix4x4 MeshBindWorld = Matrix4x4.Identity;
    /// <summary>정점 ID → (조인트 인덱스, 가중치) 목록. 길이가 정점 수보다 짧거나 null 항목이면 가중치 없음.</summary>
    public List<(int joint, float weight)>?[] Weights = Array.Empty<List<(int, float)>?>();

    public int JointIndexOf(NodeId id) => Joints.IndexOf(id);

    public void EnsureSize(int vertexCount)
    {
        if (Weights.Length >= vertexCount) return;
        Array.Resize(ref Weights, vertexCount);
    }

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
        for (int i = 0; i < list.Count; i++)
            if (list[i].joint == joint)
            {
                if (weight <= 0f) list.RemoveAt(i); else list[i] = (joint, weight);
                return;
            }
        if (weight > 0f) list.Add((joint, weight));
    }

    public List<(int joint, float weight)> CopyWeights(int vertex)
        => vertex < Weights.Length && Weights[vertex] != null ? new List<(int, float)>(Weights[vertex]!) : new List<(int, float)>();

    public void SetWeights(int vertex, List<(int joint, float weight)> weights)
    {
        EnsureSize(vertex + 1);
        Weights[vertex] = new List<(int, float)>(weights);
    }

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
