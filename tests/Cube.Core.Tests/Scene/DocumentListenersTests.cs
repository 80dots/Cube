using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Scene;

/// <summary>셸을 다시 만들 때 쓰는 <see cref="Document.DetachViewListeners"/>: 바깥 구독자는 모두 떼고 문서의 선택 중계는 유지해야 한다.</summary>
public class DocumentListenersTests
{
    /// <summary>
    /// 옛 구독자(문서·선택·모드·Undo)는 더 이상 호출되지 않고, 새로 붙인 문서 구독자는 선택 변경을 ChangeKind.Selection으로 계속 받는다.
    /// </summary>
    [Fact]
    public void DetachViewListeners_DropsOldSubscribers_KeepsSelectionRelay()
    {
        var doc = new Document();
        int old = 0;
        doc.Changed += _ => old++;
        doc.Selection.Changed += () => old++;
        doc.Selection.ModeChanged += () => old++;
        doc.Undo.Changed += () => old++;
        doc.DetachViewListeners();
        int relayed = 0;
        doc.Changed += c => { if (c.Kind == ChangeKind.Selection) relayed++; };
        doc.Selection.Mode = SelectMode.Face;
        doc.Undo.Clear();
        Assert.Equal(0, old);
        Assert.True(relayed > 0);
    }
}
