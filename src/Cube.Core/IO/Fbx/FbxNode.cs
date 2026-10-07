using System.Text;

namespace Cube.Core.IO.Fbx;

/// <summary>
/// FBX 노드 레코드(이름 + 속성 목록 + 자식). 속성 값 타입은 바이너리 FBX의 타입 코드와 1:1로 대응한다:
/// short(Y) bool(C) int(I) float(F) double(D) long(L) string(S) byte[](R) float[](f) double[](d) long[](l) int[](i) bool[](b).
/// </summary>
public sealed class FbxNode
{
    public string Name { get; }
    public readonly List<object> Props = new();
    public readonly List<FbxNode> Children = new();

    public FbxNode(string name, params object[] props)
    {
        Name = name;
        foreach (var p in props) Props.Add(Check(p));
    }

    /// <summary>자식 노드를 추가하고 그 자식을 돌려준다(체이닝용).</summary>
    public FbxNode Add(string name, params object[] props)
    {
        var n = new FbxNode(name, props);
        Children.Add(n);
        return n;
    }

    public FbxNode Add(FbxNode child) { Children.Add(child); return child; }

    public FbxNode? Child(string name) => Children.FirstOrDefault(c => c.Name == name);
    public IEnumerable<FbxNode> All(string name) => Children.Where(c => c.Name == name);

    public T Prop<T>(int i) => (T)Props[i];

    private static object Check(object p) => p switch
    {
        short or bool or int or float or double or long or string or byte[] or float[] or double[] or long[] or int[] or bool[] => p,
        _ => throw new ArgumentException($"unsupported FBX property type {p.GetType().Name}"),
    };

    /// <summary>"Class::Name" 식별 문자열의 바이너리 표현: 이름 + \0\x01 + 클래스(파일에는 이 순서로 저장된다).</summary>
    public static string Id(string cls, string name) => name + "\0\u0001" + cls;

    /// <summary>바이너리 표현을 사람이 읽는 "Class::Name"으로.</summary>
    public static string ReadableId(string s)
    {
        int i = s.IndexOf("\0\u0001", StringComparison.Ordinal);
        return i < 0 ? s : s[(i + 2)..] + "::" + s[..i];
    }

    public override string ToString()
    {
        var sb = new StringBuilder(Name).Append(':');
        foreach (var p in Props) sb.Append(' ').Append(p is string s ? '"' + ReadableId(s) + '"' : p is Array a ? $"[{a.Length}]" : p);
        return sb.ToString();
    }
}
