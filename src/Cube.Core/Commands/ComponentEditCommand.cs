using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>
/// Component Editor 편집 한 번(셀 입력 또는 가운데 버튼 드래그)의 전/후 스냅샷 명령. 위상은 바뀌지 않으므로 정점 위치, 모든 UV 세트(현재 세트 = Uv0),
/// UV 핀, 코너 노멀·고정, 정점 노멀 잠금, 스킨 가중치를 통째로 저장한다. 즉시형(apply) 또는 Capture → 변경 → Commit 후 alreadyApplied로 넣는다.
/// </summary>
public sealed class ComponentEditCommand : ICommand
{
    private sealed class State
    {
        public Vector3[] Pos = Array.Empty<Vector3>();
        public Vector2[] Uv0 = Array.Empty<Vector2>();
        public bool[] Pin = Array.Empty<bool>();
        public Vector3[] Nrm = Array.Empty<Vector3>();
        public bool[] NrmLock = Array.Empty<bool>();
        public Vector2[][] Sets = Array.Empty<Vector2[]>();
        public Dictionary<int, Vector3> Locked = new();
        public List<(int, float)>?[]? Weights;
    }

    private readonly NodeId _node;
    private readonly Action<PolyMesh, SkinCluster?>? _apply;
    private State? _before, _after;
    public string Name { get; }

    public ComponentEditCommand(string name, NodeId node, Action<PolyMesh, SkinCluster?> apply) { Name = name; _node = node; _apply = apply; }
    public ComponentEditCommand(string name, NodeId node) { Name = name; _node = node; }

    public bool IsNoop => _before != null && _after != null && Same(_before, _after);

    public void Capture(Document doc) => _before = Snap(doc.Get(_node));
    public void Commit(Document doc) => _after = Snap(doc.Get(_node));

    public void Do(Document doc)
    {
        var n = doc.Get(_node);
        if (_after == null) { Capture(doc); _apply?.Invoke(n.Mesh!, n.MeshShape?.Skin); Commit(doc); }
        else Restore(n, _after);
        NotifyAll(doc, _node, n.MeshShape?.Skin != null);
    }

    public void Undo(Document doc)
    {
        var n = doc.Get(_node);
        if (_before != null) Restore(n, _before);
        NotifyAll(doc, _node, n.MeshShape?.Skin != null);
    }

    /// <summary>미리보기(드래그 중)에서도 같은 통지를 쓴다.</summary>
    public static void NotifyAll(Document doc, NodeId node, bool skinned)
    {
        var mesh = doc.Get(node).Mesh!;
        mesh.BumpGeometry();
        doc.Notify(new DocChange(ChangeKind.MeshAttributes, node));
        if (skinned) doc.Notify(new DocChange(ChangeKind.SkinChanged, node));
    }

    private static State Snap(SceneNode n)
    {
        var m = n.Mesh!;
        var s = new State
        {
            Pos = new Vector3[m.VertexCount], Uv0 = new Vector2[m.HalfEdgeCount], Pin = new bool[m.HalfEdgeCount],
            Nrm = new Vector3[m.HalfEdgeCount], NrmLock = new bool[m.HalfEdgeCount],
            Sets = m.UvSets.Select(x => (Vector2[])x.Uvs.Clone()).ToArray(),
            Locked = new Dictionary<int, Vector3>(m.LockedNormals),
        };
        for (int i = 0; i < s.Pos.Length; i++) s.Pos[i] = m.Verts[i].Position;
        for (int i = 0; i < s.Uv0.Length; i++) { var h = m.Hes[i]; s.Uv0[i] = h.Uv0; s.Pin[i] = h.PinUv; s.Nrm[i] = h.Normal; s.NrmLock[i] = h.NormalLocked; }
        if (n.MeshShape?.Skin is { } skin) s.Weights = skin.Weights.Select(w => w == null ? null : new List<(int, float)>(w)).ToArray();
        return s;
    }

    private static void Restore(SceneNode n, State s)
    {
        var m = n.Mesh!;
        for (int i = 0; i < s.Pos.Length && i < m.VertexCount; i++) { var v = m.Verts[i]; v.Position = s.Pos[i]; m.Verts[i] = v; }
        for (int i = 0; i < s.Uv0.Length && i < m.HalfEdgeCount; i++) { var h = m.Hes[i]; h.Uv0 = s.Uv0[i]; h.PinUv = s.Pin[i]; h.Normal = s.Nrm[i]; h.NormalLocked = s.NrmLock[i]; m.Hes[i] = h; }
        for (int k = 0; k < s.Sets.Length && k < m.UvSets.Count; k++) m.UvSets[k].Uvs = (Vector2[])s.Sets[k].Clone();
        m.LockedNormals.Clear(); foreach (var (k, v) in s.Locked) m.LockedNormals[k] = v;
        if (s.Weights != null && n.MeshShape?.Skin is { } skin) skin.Weights = s.Weights.Select(w => w == null ? null : new List<(int, float)>(w)).ToArray();
    }

    private static bool Same(State a, State b)
    {
        if (!a.Pos.AsSpan().SequenceEqual(b.Pos) || !a.Uv0.AsSpan().SequenceEqual(b.Uv0) || !a.Pin.AsSpan().SequenceEqual(b.Pin)
            || !a.Nrm.AsSpan().SequenceEqual(b.Nrm) || !a.NrmLock.AsSpan().SequenceEqual(b.NrmLock) || a.Sets.Length != b.Sets.Length) return false;
        for (int k = 0; k < a.Sets.Length; k++) if (!a.Sets[k].AsSpan().SequenceEqual(b.Sets[k])) return false;
        if (a.Locked.Count != b.Locked.Count || a.Locked.Any(kv => !b.Locked.TryGetValue(kv.Key, out var v) || v != kv.Value)) return false;
        if ((a.Weights == null) != (b.Weights == null)) return false;
        if (a.Weights != null)
            for (int i = 0; i < a.Weights.Length; i++)
            {
                var x = a.Weights[i]; var y = i < b.Weights!.Length ? b.Weights[i] : null;
                if ((x?.Count ?? 0) != (y?.Count ?? 0)) return false;
                if (x != null && y != null && !x.SequenceEqual(y)) return false;
            }
        return true;
    }
}
