using System.Numerics;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Scene;

/// <summary>
/// 가져온 애니메이션 트랙(<c>NodeTrack</c>)의 시간 평가를 검증한다.
/// 위치는 선형 보간, 회전은 쿼터니언 slerp, 범위 밖 시간은 끝 키로 고정, 키가 없는 채널은 rest 트랜스폼으로 대체되어야 한다.
/// </summary>
public class AnimationTests
{
    /// <summary>테스트 공용 rest 트랜스폼: 이동 (9,9,9), 회전 0, 스케일 2. 키가 없는 채널이 이 값을 쓰는지 구분하기 쉽게 고른 값이다.</summary>
    private static readonly Transform3 Rest = new(new Vector3(9, 9, 9), new Vector3(0, 0, 0), new Vector3(2, 2, 2));

    /// <summary>
    /// 위치 키 3개(0초, 1초, 2초) 사이 시간에서 선형 보간 결과가 맞는지, 첫 키 이전/마지막 키 이후 시간은
    /// 각각 첫 키·마지막 키 값으로 고정(clamp)되는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// Y축 0°→90° 회전 키의 중간(0.5초)이 Y 45°가 되는지 확인해, 회전이 쿼터니언 slerp로 보간되고
    /// 오일러 각으로 올바르게 환산되는지 본다.
    /// </summary>
    [Fact]
    public void Evaluate_SlerpsRotation()
    {
        var tr = new NodeTrack();
        tr.Rotation.Add(new(0f, Quaternion.Identity));
        tr.Rotation.Add(new(1f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)));
        var r = tr.Evaluate(0.5f, Rest).RotationDegrees;
        Assert.Equal(0f, r.X, 3); Assert.Equal(45f, r.Y, 3); Assert.Equal(0f, r.Z, 3);
    }

    /// <summary>
    /// 회전 키만 있는 트랙은 이동·스케일을 rest 값으로 채워야 하고, 키가 하나뿐이면 어느 시간이든 그 키 값을 써야 한다.
    /// 키가 전혀 없는 트랙은 rest 트랜스폼을 그대로 돌려줘야 한다(본 일부만 애니메이션된 클립 대응).
    /// </summary>
    [Fact]
    public void Evaluate_ChannelsWithoutKeys_FallBackToRest()
    {
        var tr = new NodeTrack();
        tr.Rotation.Add(new(0f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)));
        var t = tr.Evaluate(3f, Rest);
        Assert.Equal(9f, t.Translation.X, 4); Assert.Equal(9f, t.Translation.Y, 4); Assert.Equal(9f, t.Translation.Z, 4);
        Assert.Equal(2f, t.Scale.X, 4); Assert.Equal(2f, t.Scale.Z, 4);
        Assert.Equal(90f, t.RotationDegrees.Z, 3);

        // 키가 하나도 없는 트랙: 모든 채널이 rest로 대체되는지 확인.
        var empty = new NodeTrack();
        var e = empty.Evaluate(0.3f, Rest);
        Assert.Equal(Rest.Translation, e.Translation);
        Assert.Equal(2f, e.Scale.Y, 4);
    }
}
