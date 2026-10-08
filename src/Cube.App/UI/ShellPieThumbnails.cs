using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI;

/// <summary>파이 메뉴(Assign Material ▸)용 머티리얼 썸네일. Material Editor와 같은 MaterialThumbnails를 셸에 하나 두고 열 때마다 다시 그린다.</summary>
public partial class Shell
{
    private MaterialThumbnails? _pieThumbs;
    /// <summary>lambert1(기본 머티리얼)의 모습: 회색 Lambert.</summary>
    private static readonly MaterialDef DefaultLambert = new() { Id = -1, Name = "lambert1", Type = MaterialType.Lambert };

    /// <summary>머티리얼(null = lambert1)의 구 썸네일. 처음엔 다음 프레임에 그려지며 그 뒤 파이에 바로 나타난다.</summary>
    public Texture2D PieThumbnail(MaterialDef? def)
    {
        if (_pieThumbs == null)
        {
            _pieThumbs = new MaterialThumbnails { Name = "PieThumbnails", SizePx = (int)(48 * CubeApp.Instance.UiScale) };
            AddChild(_pieThumbs);
        }
        _pieThumbs.Prune(Document.Materials.Select(m => m.Id).Append(-1));
        return _pieThumbs.Get(def ?? DefaultLambert);
    }
}
