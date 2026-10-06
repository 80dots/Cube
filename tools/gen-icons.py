# assets/icons/*.svg -> src/Cube.App/UI/IconData.cs (아이콘을 코드에 내장; 내보낸 빌드에 .svg가 포함되지 않으므로)
import os, glob

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
entries = []
for p in sorted(glob.glob(os.path.join(root, "assets/icons/*.svg"))):
    name = os.path.splitext(os.path.basename(p))[0]
    svg = open(p, encoding="utf-8").read().strip().replace('"', '""')
    entries.append(f'        ["{name}"] = @"{svg}",')

header = """namespace Cube.App.UI;

/// <summary>
/// 아이콘 SVG를 코드에 내장한다(내보낸 빌드에는 .svg 원본이 포함되지 않으므로).
/// 원본은 assets/icons/*.svg 이며, 바꾼 뒤 tools/gen-icons.py 로 이 파일을 다시 생성한다.
/// </summary>
public static class IconData
{
    public static readonly Dictionary<string, string> Svg = new()
    {
"""
footer = """
    };
}
"""
out = os.path.join(root, "src/Cube.App/UI/IconData.cs")
with open(out, "w", encoding="utf-8", newline="\n") as f:
    f.write(header + "\n".join(entries) + footer)
print(len(entries), "icons embedded ->", out)
