using System.Numerics;
using Cube.Core.Rig;
using Cube.Core.Scene;

namespace Cube.App.Viewport;

/// <summary>
/// 스킨 변형(CPU LBS) 결과를 문서 통지 일련번호(<see cref="Document.ChangeSerial"/>)로 캐시해 뷰포트 패널 4개가 같은 포즈를 각자 계산하지 않게 한다.
/// 같은 일련번호 안에서는 조인트 월드 행렬도 메모해 메시마다 조인트 체인을 다시 곱하지 않는다.
/// <see cref="Update"/>는 여러 메시의 변형을 병렬로 계산한다(순수 C#; 행렬 메모는 먼저 순차로 채운다).
/// 반환 배열은 공유되므로 읽기만 할 것(MeshView.Deformed가 그대로 참조한다).
/// </summary>
public static class SkinDeformCache
{
    private sealed class Entry { public int Serial = -1; public Vector3[] Buf = Array.Empty<Vector3>(); }
    private static readonly Dictionary<NodeId, Entry> _entries = new();
    private static readonly Dictionary<NodeId, Matrix4x4> _worlds = new();
    private static int _worldSerial = -1;
    private static Document? _doc;
    private static readonly List<SceneNode> _stale = new();

    /// <summary>주어진 스킨 노드들의 변형을 이 통지 기준으로 최신화한다(이미 계산된 노드는 건너뜀, 나머지는 병렬).</summary>
    public static void Update(Document doc, IReadOnlyList<SceneNode> skinned)
    {
        if (!ReferenceEquals(_doc, doc)) { _entries.Clear(); _worlds.Clear(); _doc = doc; _worldSerial = -1; }
        int serial = doc.ChangeSerial;
        if (_worldSerial != serial) { _worlds.Clear(); _worldSerial = serial; }
        _stale.Clear();
        foreach (var n in skinned)
        {
            if (n.Mesh == null || n.Skin == null) continue;
            if (!_entries.TryGetValue(n.Id, out var e)) _entries[n.Id] = e = new Entry();
            int vc = n.Mesh.VertexCount;
            if (e.Serial == serial && e.Buf.Length == vc) continue;
            if (e.Buf.Length != vc) e.Buf = new Vector3[vc];
            // 월드 행렬 메모는 순차로(사전 쓰기) — 병렬 단계에서는 읽기만 한다
            World(n);
            foreach (var jid in n.Skin.Joints) if (doc.Find(jid) is { } j) World(j);
            _stale.Add(n);
        }
        if (_stale.Count == 0) return;
        if (_stale.Count == 1) DeformOne(_stale[0], serial);
        else Parallel.For(0, _stale.Count, i => DeformOne(_stale[i], serial));
    }

    private static void DeformOne(SceneNode n, int serial)
    {
        var e = _entries[n.Id];
        Matrix4x4.Invert(_worlds[n.Id], out var inv);
        SkinOps.Deform(n.Mesh!, n.Skin!, jid => _worlds.TryGetValue(jid, out var w) ? w : null, inv, e.Buf);
        e.Serial = serial;
    }

    /// <summary>노드의 현재 변형 위치(정점 ID 순, 길이 = VertexCount). 이 통지에서 아직 계산하지 않았으면 지금 계산한다.</summary>
    public static Vector3[] Get(Document doc, SceneNode n)
    {
        if (!ReferenceEquals(_doc, doc) || !_entries.TryGetValue(n.Id, out var e) || e.Serial != doc.ChangeSerial || e.Buf.Length != n.Mesh!.VertexCount)
        {
            _one[0] = n;
            Update(doc, _one);
            e = _entries[n.Id];
        }
        return e.Buf;
    }
    private static readonly SceneNode[] _one = new SceneNode[1];

    private static Matrix4x4 World(SceneNode n)
    {
        if (_worlds.TryGetValue(n.Id, out var m)) return m;
        m = n.WorldMatrix;
        _worlds[n.Id] = m;
        return m;
    }

    /// <summary>노드가 지워졌거나 스킨이 떨어졌을 때 버퍼를 버린다.</summary>
    public static void Forget(NodeId id) => _entries.Remove(id);

    public static void Clear() { _entries.Clear(); _worlds.Clear(); _worldSerial = -1; }
}
