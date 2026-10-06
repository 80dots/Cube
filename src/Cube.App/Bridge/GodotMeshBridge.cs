using Cube.Core.Mesh;
using Godot;
using GArray = Godot.Collections.Array;

namespace Cube.App.Bridge;

/// <summary>
/// RenderMeshData를 Godot ArrayMesh/MultiMesh로 올린다. 마샬링 비용을 줄이기 위해 Godot 배열 버퍼를 재사용한다.
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

    /// <summary>셰이딩 표면(삼각형)을 다시 올린다.</summary>
    public void UploadSurface(ArrayMesh mesh, RenderMeshData r)
    {
        mesh.ClearSurfaces();
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
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    /// <summary>와이어(선분)를 정점 색과 함께 올린다. colorOf(edgeId)가 선 색을 정한다.</summary>
    public void UploadLines(ArrayMesh mesh, RenderMeshData r, Func<int, Color> colorOf)
    {
        mesh.ClearSurfaces();
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
    }

    /// <summary>점(정점 또는 면 중심)을 MultiMesh 인스턴스로 올린다.</summary>
    public static void UploadPoints(MultiMesh mm, ReadOnlySpan<System.Numerics.Vector3> positions, Func<int, Color> colorOf)
    {
        int n = positions.Length;
        if (mm.InstanceCount != n) mm.InstanceCount = n;
        for (int i = 0; i < n; i++)
        {
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, positions[i].ToGodot()));
            mm.SetInstanceColor(i, colorOf(i).SrgbToLinear());
        }
    }

    /// <summary>선택된 면만 모아 반투명 틴트용 삼각형을 올린다.</summary>
    public void UploadFaceSubset(ArrayMesh mesh, RenderMeshData r, Func<int, bool> includeFace)
    {
        mesh.ClearSurfaces();
        int tris = 0;
        for (int t = 0; t < r.TriangleCount; t++) if (includeFace(r.TriToFace[t])) tris++;
        if (tris == 0) return;
        Fit(ref _tintPos, tris * 3); Fit(ref _tintIdx, tris * 3);
        int k = 0;
        for (int t = 0; t < r.TriangleCount; t++)
        {
            if (!includeFace(r.TriToFace[t])) continue;
            foreach (int j in new[] { 0, 2, 1 }) { _tintPos[k] = r.Positions[r.Indices[t * 3 + j]].ToGodot(); _tintIdx[k] = k; k++; }
        }
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _tintPos;
        arrays[(int)Mesh.ArrayType.Index] = _tintIdx;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    private static void Fit<T>(ref T[] arr, int exact)
    {
        // Godot은 배열 길이를 그대로 쓰므로 정확한 길이가 필요하다
        if (arr.Length != exact) arr = new T[exact];
    }
}
