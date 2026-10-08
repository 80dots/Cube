using System.Runtime.InteropServices;
using Cube.Core.Mesh;
using Godot;
using GArray = Godot.Collections.Array;

namespace Cube.App.Bridge;

/// <summary>
/// RenderMeshData를 Godot ArrayMesh/MultiMesh로 올린다. 마샬링 비용을 줄이기 위해 Godot 배열 버퍼를 재사용한다.
/// 위치만 바뀌는 경우(스킨 변형 재생, 정점 드래그)는 <see cref="UpdateSurfacePositions"/>/<see cref="UpdateLinePositions"/>/
/// <see cref="UpdatePointPositions"/>가 서피스를 다시 만들지 않고 정점 버퍼 영역(SurfaceUpdateVertexRegion)·MultiMesh 버퍼만 덮어쓴다.
/// 정점 버퍼 레이아웃(위치 float3 × N이 오프셋 0부터 연속)은 처음 한 번 RenderingServer에서 읽어 와 검증하고, 다르면 전체 재생성으로 되돌아간다.
/// </summary>
public sealed class GodotMeshBridge
{
    private Vector3[] _pos = Array.Empty<Vector3>();
    private Vector3[] _nrm = Array.Empty<Vector3>();
    private Vector2[] _uv = Array.Empty<Vector2>();
    private int[] _idx = Array.Empty<int>();
    private Vector3[] _linePos = Array.Empty<Vector3>();
    private Color[] _lineCol = Array.Empty<Color>();
    private Vector3[] _tintPos = Array.Empty<Vector3>();
    private int[] _tintIdx = Array.Empty<int>();
    private int[] _tintCorners = Array.Empty<int>();   // 틴트 정점 k → 렌더 코너 인덱스(위치 갱신용)
    private int _tintVertexCount;

    private Color[] _col = Array.Empty<Color>();

    /// <summary>마지막 전체 업로드의 코너 수(-1 = 없음). 위치만 갱신할 때 같은 위상인지 확인한다.</summary>
    private int _surfaceCorners = -1, _lineVerts = -1;
    private byte[] _bytes = Array.Empty<byte>();

    /// <summary>정점 영역 부분 갱신이 이 Godot 빌드에서 기대한 레이아웃인지(null = 아직 검증 전).</summary>
    private static bool? _vertexRegionOk;
    private static bool? _multimeshBufferOk;
    /// <summary>검증 실패 시 이유(디버그).</summary>
    public static string? FastPathDisabledReason { get; private set; }

    /// <summary>셰이딩 표면(삼각형)을 다시 올린다. cornerColor가 있으면 정점 색(선형)을, bones/weights가 있으면 스키닝 배열(코너당 4개)을 함께 올린다.</summary>
    public void UploadSurface(ArrayMesh mesh, RenderMeshData r, Func<int, Color>? cornerColor = null, int[]? bones4 = null, float[]? weights4 = null)
    {
        mesh.ClearSurfaces();
        _surfaceCorners = -1;
        if (r.IndexCount == 0) return;
        Fit(ref _pos, r.CornerCount); Fit(ref _nrm, r.CornerCount); Fit(ref _uv, r.CornerCount); Fit(ref _idx, r.IndexCount);
        for (int i = 0; i < r.CornerCount; i++)
        {
            _pos[i] = r.Positions[i].ToGodot();
            _nrm[i] = r.Normals[i].ToGodot();
            _uv[i] = new Vector2(r.Uvs[i].X, 1f - r.Uvs[i].Y); // Godot UV는 상단 원점
        }
        // 코어는 반시계(CCW)가 앞면, Godot은 시계(CW)가 앞면 → 삼각형마다 1,2번 인덱스를 바꾼다
        for (int t = 0; t < r.IndexCount; t += 3)
        {
            _idx[t] = r.Indices[t]; _idx[t + 1] = r.Indices[t + 2]; _idx[t + 2] = r.Indices[t + 1];
        }
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _pos;
        arrays[(int)Mesh.ArrayType.Normal] = _nrm;
        arrays[(int)Mesh.ArrayType.TexUV] = _uv;
        arrays[(int)Mesh.ArrayType.Index] = _idx;
        if (cornerColor != null)
        {
            Fit(ref _col, r.CornerCount);
            for (int i = 0; i < r.CornerCount; i++) _col[i] = cornerColor(i);
            arrays[(int)Mesh.ArrayType.Color] = _col;
        }
        if (bones4 != null && weights4 != null)
        {
            arrays[(int)Mesh.ArrayType.Bones] = bones4;
            arrays[(int)Mesh.ArrayType.Weights] = weights4;
        }
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _surfaceCorners = r.CornerCount;
        if (_vertexRegionOk == null) VerifyVertexRegion(mesh, r.CornerCount);
    }

    /// <summary>
    /// 표면 정점 위치만 갱신한다(위상·노멀·UV·인덱스는 그대로). 마지막 <see cref="UploadSurface"/>와 코너 수가 같고 레이아웃 검증을 통과했을 때만 true.
    /// false면 호출자가 전체 업로드로 되돌아가야 한다.
    /// </summary>
    public bool UpdateSurfacePositions(ArrayMesh mesh, RenderMeshData r)
    {
        if (_vertexRegionOk != true || _surfaceCorners < 0 || _surfaceCorners != r.CornerCount || mesh.GetSurfaceCount() == 0) return false;
        mesh.SurfaceUpdateVertexRegion(0, 0, PositionBytes(r.Positions, r.CornerCount));
        return true;
    }

    /// <summary>와이어(선분)를 정점 색과 함께 올린다. colorOf(edgeId)가 선 색을 정한다.</summary>
    public void UploadLines(ArrayMesh mesh, RenderMeshData r, Func<int, Color> colorOf)
    {
        mesh.ClearSurfaces();
        _lineVerts = -1;
        if (r.LineVertexCount == 0) return;
        Fit(ref _linePos, r.LineVertexCount); Fit(ref _lineCol, r.LineVertexCount);
        for (int i = 0; i < r.LineCount; i++)
        {
            // 정점 색은 셰이더에서 선형으로 취급되므로 sRGB 값을 선형으로 바꿔 넘긴다
            var c = colorOf(r.LineToEdge[i]).SrgbToLinear();
            _linePos[i * 2] = r.LinePositions[i * 2].ToGodot();
            _linePos[i * 2 + 1] = r.LinePositions[i * 2 + 1].ToGodot();
            _lineCol[i * 2] = c; _lineCol[i * 2 + 1] = c;
        }
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _linePos;
        arrays[(int)Mesh.ArrayType.Color] = _lineCol;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        _lineVerts = r.LineVertexCount;
        if (_vertexRegionOk == null) VerifyVertexRegion(mesh, r.LineVertexCount);
    }

    /// <summary>와이어 정점 위치만 갱신한다(색 그대로). 마지막 <see cref="UploadLines"/>와 선분 수가 같을 때만 true.</summary>
    public bool UpdateLinePositions(ArrayMesh mesh, RenderMeshData r)
    {
        if (_vertexRegionOk != true || _lineVerts < 0 || _lineVerts != r.LineVertexCount || mesh.GetSurfaceCount() == 0) return false;
        mesh.SurfaceUpdateVertexRegion(0, 0, PositionBytes(r.LinePositions, r.LineVertexCount));
        return true;
    }

    /// <summary>
    /// 점(정점 또는 면 중심)을 MultiMesh 인스턴스로 올린다. buffer는 호출자가 보관하는 인스턴스 버퍼(변환 12 + 색 4 float)로,
    /// 이후 <see cref="UpdatePointPositions"/>가 색을 유지한 채 위치만 바꾼다.
    /// </summary>
    public static void UploadPoints(MultiMesh mm, ReadOnlySpan<System.Numerics.Vector3> positions, Func<int, Color> colorOf, ref float[] buffer)
    {
        int n = positions.Length;
        if (mm.InstanceCount != n) mm.InstanceCount = n;
        if (n == 0) return;
        if (_multimeshBufferOk == false)
        {
            for (int i = 0; i < n; i++)
            {
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, positions[i].ToGodot()));
                mm.SetInstanceColor(i, colorOf(i).SrgbToLinear());
            }
            return;
        }
        const int stride = 16;
        if (buffer.Length != n * stride) buffer = new float[n * stride];
        for (int i = 0; i < n; i++)
        {
            int o = i * stride; var p = positions[i]; var c = colorOf(i).SrgbToLinear();
            buffer[o] = 1; buffer[o + 1] = 0; buffer[o + 2] = 0; buffer[o + 3] = p.X;
            buffer[o + 4] = 0; buffer[o + 5] = 1; buffer[o + 6] = 0; buffer[o + 7] = p.Y;
            buffer[o + 8] = 0; buffer[o + 9] = 0; buffer[o + 10] = 1; buffer[o + 11] = p.Z;
            buffer[o + 12] = c.R; buffer[o + 13] = c.G; buffer[o + 14] = c.B; buffer[o + 15] = c.A;
        }
        RenderingServer.MultimeshSetBuffer(mm.GetRid(), buffer);
        if (_multimeshBufferOk == null)
        {
            // 한 번만: 버퍼 레이아웃(변환 12 + 색 4)이 맞는지 첫 인스턴스로 확인. 다르면 인스턴스별 호출로 되돌아간다
            var t = mm.GetInstanceTransform(0); var col = mm.GetInstanceColor(0); var c0 = colorOf(0).SrgbToLinear();
            bool ok = (t.Origin - positions[0].ToGodot()).Length() < 1e-4f && MathF.Abs(col.R - c0.R) < 1e-2f && MathF.Abs(col.G - c0.G) < 1e-2f && MathF.Abs(col.B - c0.B) < 1e-2f;
            _multimeshBufferOk = ok;
            if (!ok)
            {
                FastPathDisabledReason = $"multimesh buffer layout mismatch: origin={t.Origin} expected={positions[0]} color={col} expected={c0}";
                GD.PushWarning("[Cube] " + FastPathDisabledReason);
                var tmp = buffer; UploadPoints(mm, positions, colorOf, ref tmp);
            }
        }
    }

    /// <summary>MultiMesh 인스턴스의 위치만 바꾼다(색은 buffer에 남아 있는 값). 인스턴스 수가 다르면 false(전체 업로드 필요).</summary>
    public static bool UpdatePointPositions(MultiMesh mm, ReadOnlySpan<System.Numerics.Vector3> positions, float[] buffer)
    {
        int n = positions.Length;
        const int stride = 16;
        if (_multimeshBufferOk != true || n == 0 || mm.InstanceCount != n || buffer.Length != n * stride) return false;
        for (int i = 0; i < n; i++)
        {
            int o = i * stride; var p = positions[i];
            buffer[o + 3] = p.X; buffer[o + 7] = p.Y; buffer[o + 11] = p.Z;
        }
        RenderingServer.MultimeshSetBuffer(mm.GetRid(), buffer);
        return true;
    }

    /// <summary>선택된 면만 모아 반투명 틴트용 삼각형을 올린다.</summary>
    public void UploadFaceSubset(ArrayMesh mesh, RenderMeshData r, Func<int, bool> includeFace)
    {
        mesh.ClearSurfaces();
        _tintVertexCount = 0;
        int tris = 0;
        for (int t = 0; t < r.TriangleCount; t++) if (includeFace(r.TriToFace[t])) tris++;
        if (tris == 0) return;
        Fit(ref _tintPos, tris * 3); Fit(ref _tintIdx, tris * 3); Fit(ref _tintCorners, tris * 3);
        int k = 0;
        for (int t = 0; t < r.TriangleCount; t++)
        {
            if (!includeFace(r.TriToFace[t])) continue;
            // 코어 CCW → Godot CW: 1,2번을 바꾼다
            int c0 = r.Indices[t * 3], c1 = r.Indices[t * 3 + 2], c2 = r.Indices[t * 3 + 1];
            _tintCorners[k] = c0; _tintPos[k] = r.Positions[c0].ToGodot(); _tintIdx[k] = k; k++;
            _tintCorners[k] = c1; _tintPos[k] = r.Positions[c1].ToGodot(); _tintIdx[k] = k; k++;
            _tintCorners[k] = c2; _tintPos[k] = r.Positions[c2].ToGodot(); _tintIdx[k] = k; k++;
        }
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _tintPos;
        arrays[(int)Mesh.ArrayType.Index] = _tintIdx;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _tintVertexCount = k;
    }

    /// <summary>틴트 삼각형의 위치만 갱신한다(같은 면 집합). 틴트가 비어 있거나 검증 전이면 false.</summary>
    public bool UpdateFaceSubsetPositions(ArrayMesh mesh, RenderMeshData r)
    {
        if (_vertexRegionOk != true || _tintVertexCount <= 0 || mesh.GetSurfaceCount() == 0) return false;
        int n = _tintVertexCount;
        if (_bytes.Length < n * 12) _bytes = new byte[n * 12];
        var floats = MemoryMarshal.Cast<byte, float>(_bytes.AsSpan(0, n * 12));
        for (int k = 0; k < n; k++)
        {
            var p = r.Positions[_tintCorners[k]];
            floats[k * 3] = p.X; floats[k * 3 + 1] = p.Y; floats[k * 3 + 2] = p.Z;
        }
        mesh.SurfaceUpdateVertexRegion(0, 0, _bytes.AsSpan(0, n * 12));
        return true;
    }

    /// <summary>System.Numerics 위치 배열을 Godot 정점 버퍼 바이트(float3 연속)로 바꾼다. 둘 다 float3 연속 레이아웃이라 그대로 복사한다.</summary>
    private ReadOnlySpan<byte> PositionBytes(System.Numerics.Vector3[] src, int count)
    {
        int bytes = count * 12;
        if (_bytes.Length < bytes) _bytes = new byte[Math.Max(bytes, 1024)];
        MemoryMarshal.AsBytes(src.AsSpan(0, count)).CopyTo(_bytes);
        return _bytes.AsSpan(0, bytes);
    }

    /// <summary>
    /// 정점 영역 부분 갱신 전제(위치 float3 × N이 버퍼 오프셋 0부터 stride 12로 연속)를 실제 서피스 데이터로 한 번 확인한다.
    /// Godot 4.x는 위치 블록 뒤에 노멀/탄젠트 블록을 두므로 통과해야 정상이며, 다르면 부분 갱신을 끄고 전체 재생성만 쓴다.
    /// </summary>
    private static void VerifyVertexRegion(ArrayMesh mesh, int count)
    {
        try
        {
            var rid = mesh.GetRid();
            var format = (RenderingServer.ArrayFormat)(ulong)mesh.SurfaceGetFormat(0);
            int stride = (int)RenderingServer.MeshSurfaceGetFormatVertexStride(format, count);
            int offset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, count, (int)Mesh.ArrayType.Vertex);
            var data = RenderingServer.MeshGetSurface(rid, 0);
            var bytes = data["vertex_data"].AsByteArray();
            var arrays = mesh.SurfaceGetArrays(0);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            bool ok = stride == 12 && offset == 0 && bytes.Length >= count * 12 && verts.Length == count;
            if (ok)
            {
                var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, count * 12));
                for (int i = 0; i < count && ok; i++)
                    ok = f[i * 3] == verts[i].X && f[i * 3 + 1] == verts[i].Y && f[i * 3 + 2] == verts[i].Z;
            }
            _vertexRegionOk = ok;
            if (!ok)
            {
                FastPathDisabledReason = $"vertex buffer layout mismatch: stride={stride} offset={offset} bytes={bytes.Length} count={count}";
                GD.PushWarning("[Cube] " + FastPathDisabledReason);
            }
        }
        catch (Exception ex)
        {
            _vertexRegionOk = false;
            FastPathDisabledReason = "vertex buffer verify failed: " + ex.Message;
            GD.PushWarning("[Cube] " + FastPathDisabledReason);
        }
    }

    /// <summary>부분 갱신 경로가 켜져 있는지(검증 통과). 디버그 출력용.</summary>
    public static string FastPathState => $"vertexRegion={_vertexRegionOk?.ToString() ?? "unverified"} multimeshBuffer={_multimeshBufferOk?.ToString() ?? "unverified"}";

    private static void Fit<T>(ref T[] arr, int exact)
    {
        // Godot은 배열 길이를 그대로 쓰므로 정확한 길이가 필요하다
        if (arr.Length != exact) arr = new T[exact];
    }
}
