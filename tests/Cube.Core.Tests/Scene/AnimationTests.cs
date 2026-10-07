using System.Numerics;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Scene;

public class AnimationTests
{
    private static readonly Transform3 Rest = new(new Vector3(9, 9, 9), new Vector3(0, 0, 0), new Vector3(2, 2, 2));

    [Fact]
    public void Evaluate_LerpsPosition_AndClampsOutsideRange()
    {
        var tr = new NodeTrack();
        tr.Position.Add(new(0f, Vector3.Zero));
        tr.Position.Add(new(1f, new Vector3(10, 0, 0)));
        tr.Position.Add(new(2f, new Vector3(10, 20, 0)));
        Assert.Equal(new Vector3(5, 0, 0), tr.Evaluate(0.5f, Rest).Translation);
        Assert.Equal(new Vector3(10, 10, 0), tr.Evaluate(1.5f, Rest).Translation);
        Assert.Equal(Vector3.Zero, tr.Evaluate(-1f, Rest).Translation);
        Assert.Equal(new Vector3(10, 20, 0), tr.Evaluate(5f, Rest).Translation);
    }

    [Fact]
    public void Evaluate_SlerpsRotation()
    {
        var tr = new NodeTrack();
        tr.Rotation.Add(new(0f, Quaternion.Identity));
        tr.Rotation.Add(new(1f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)));
        var r = tr.Evaluate(0.5f, Rest).RotationDegrees;
        Assert.Equal(0f, r.X, 3); Assert.Equal(45f, r.Y, 3); Assert.Equal(0f, r.Z, 3);
    }

    [Fact]
    public void Evaluate_ChannelsWithoutKeys_FallBackToRest()
    {
        var tr = new NodeTrack();
        tr.Rotation.Add(new(0f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)));
        var t = tr.Evaluate(3f, Rest);
        Assert.Equal(9f, t.Translation.X, 4); Assert.Equal(9f, t.Translation.Y, 4); Assert.Equal(9f, t.Translation.Z, 4);
        Assert.Equal(2f, t.Scale.X, 4); Assert.Equal(2f, t.Scale.Z, 4);
        Assert.Equal(90f, t.RotationDegrees.Z, 3);

        var empty = new NodeTrack();
        var e = empty.Evaluate(0.3f, Rest);
        Assert.Equal(Rest.Translation, e.Translation);
        Assert.Equal(2f, e.Scale.Y, 4);
    }
}
