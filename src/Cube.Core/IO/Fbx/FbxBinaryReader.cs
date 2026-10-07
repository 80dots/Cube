using System.IO.Compression;
using System.Text;

namespace Cube.Core.IO.Fbx;

/// <summary>바이너리 FBX(7.1~7.4, 32비트 오프셋) 리더. writer 검증·테스트용(가져오기는 Godot FbxDocument가 한다).</summary>
public static class FbxBinaryReader
{
    public static bool IsBinaryFbx(byte[] data) => data.Length > 27 && Encoding.ASCII.GetString(data, 0, 18) == "Kaydara FBX Binary";

    public static (int version, List<FbxNode> nodes) Read(byte[] data)
    {
        if (!IsBinaryFbx(data)) throw new InvalidDataException("not a binary FBX file");
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        ms.Position = 23;
        int version = (int)r.ReadUInt32();
        if (version >= 7500) throw new NotSupportedException("FBX 7.5+ (64-bit offsets) is not supported by this reader");
        var nodes = new List<FbxNode>();
        while (true)
        {
            var n = ReadNode(r);
            if (n == null) break;
            nodes.Add(n);
        }
        return (version, nodes);
    }

    public static (int version, List<FbxNode> nodes) Read(string path) => Read(File.ReadAllBytes(path));

    private static FbxNode? ReadNode(BinaryReader r)
    {
        long end = r.ReadUInt32();
        int propCount = (int)r.ReadUInt32();
        uint propLen = r.ReadUInt32();
        int nameLen = r.ReadByte();
        if (end == 0 && propCount == 0 && propLen == 0 && nameLen == 0) return null; // NULL 레코드
        string name = Encoding.UTF8.GetString(r.ReadBytes(nameLen));
        var node = new FbxNode(name);
        for (int i = 0; i < propCount; i++) node.Props.Add(ReadProp(r));
        while (r.BaseStream.Position < end)
        {
            var child = ReadNode(r);
            if (child == null) break;
            node.Children.Add(child);
        }
        r.BaseStream.Position = end;
        return node;
    }

    private static object ReadProp(BinaryReader r)
    {
        char t = (char)r.ReadByte();
        switch (t)
        {
            case 'Y': return r.ReadInt16();
            case 'C': return r.ReadByte() != 0;
            case 'I': return r.ReadInt32();
            case 'F': return r.ReadSingle();
            case 'D': return r.ReadDouble();
            case 'L': return r.ReadInt64();
            case 'S': { int len = (int)r.ReadUInt32(); return Encoding.UTF8.GetString(r.ReadBytes(len)); }
            case 'R': { int len = (int)r.ReadUInt32(); return r.ReadBytes(len); }
            case 'f': case 'd': case 'l': case 'i': case 'b':
                {
                    int count = (int)r.ReadUInt32();
                    uint encoding = r.ReadUInt32();
                    int packedLen = (int)r.ReadUInt32();
                    var raw = r.ReadBytes(packedLen);
                    int elem = t switch { 'f' => 4, 'd' => 8, 'l' => 8, 'i' => 4, _ => 1 };
                    byte[] bytes;
                    if (encoding == 1)
                    {
                        using var src = new MemoryStream(raw);
                        using var z = new ZLibStream(src, CompressionMode.Decompress);
                        bytes = new byte[count * elem];
                        int got = 0; while (got < bytes.Length) { int n = z.Read(bytes, got, bytes.Length - got); if (n <= 0) break; got += n; }
                        if (got != bytes.Length) throw new InvalidDataException("FBX array decompress size mismatch");
                    }
                    else bytes = raw;
                    switch (t)
                    {
                        case 'f': { var a = new float[count]; Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length); return a; }
                        case 'd': { var a = new double[count]; Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length); return a; }
                        case 'l': { var a = new long[count]; Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length); return a; }
                        case 'i': { var a = new int[count]; Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length); return a; }
                        default: return bytes.Select(b => b != 0).ToArray();
                    }
                }
            default: throw new InvalidDataException($"unknown FBX property type '{t}'");
        }
    }
}
