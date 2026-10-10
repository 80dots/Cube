using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Scene;

/// <summary>이미지 플레인(v0.0.71): .cube 왕복, SetImagePlaneCommand Undo, 복제.</summary>
public class ImagePlaneTests
{
    [Fact]
    public void CubeFile_RoundTrips_ImagePlane()
    {
        var doc = new Document();
        var node = new SceneNode { Name = "imagePlane1", Shape = new ImagePlaneShape { ImagePath = "C:/ref/front.png", Width = 2f, Height = 1.5f, Opacity = 0.6f, OnlyView = "front", Locked = true }, Local = new Transform3(new Vector3(0, 0, -10), Vector3.Zero, Vector3.One) };
        doc.AddNode(node);
        string json = CubeFileFormat.Serialize(doc);
        var doc2 = new Document();
        CubeFileFormat.Deserialize(doc2, json);
        var n2 = doc2.Nodes.Values.First(n => n.IsImagePlane);
        var ip = n2.ImagePlane!;
        Assert.Equal("C:/ref/front.png", ip.ImagePath); Assert.Equal(2f, ip.Width); Assert.Equal(1.5f, ip.Height); Assert.Equal(0.6f, ip.Opacity, 5); Assert.Equal("front", ip.OnlyView); Assert.True(ip.Locked);
        Assert.Equal(new Vector3(0, 0, -10), n2.Local.Translation);
    }

    [Fact]
    public void SetImagePlaneCommand_Undoes_AndDuplicateClones()
    {
        var doc = new Document();
        var node = new SceneNode { Name = "imagePlane1", Shape = new ImagePlaneShape { ImagePath = "a.png", Width = 2f, Height = 2f } };
        doc.AddNode(node);
        var after = node.ImagePlane!.Clone(); after.Opacity = 0.3f; after.OnlyView = "top"; after.Width = 4f;
        doc.Undo.Push(new SetImagePlaneCommand(node.Id, after));
        Assert.Equal(0.3f, node.ImagePlane!.Opacity, 5); Assert.Equal("top", node.ImagePlane.OnlyView); Assert.Equal(4f, node.ImagePlane.Width);
        doc.Undo.Undo();
        Assert.Equal(1f, node.ImagePlane.Opacity, 5); Assert.Null(node.ImagePlane.OnlyView); Assert.Equal(2f, node.ImagePlane.Width);
        var copy = NodeDuplicate.CloneTree(doc, node, new HashSet<string>());
        Assert.NotNull(copy.ImagePlane); Assert.NotSame(node.ImagePlane, copy.ImagePlane); Assert.Equal("a.png", copy.ImagePlane!.ImagePath);
    }
}
