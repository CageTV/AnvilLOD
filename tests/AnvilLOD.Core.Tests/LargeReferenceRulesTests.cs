using System.Numerics;
using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Tests;

public class LargeReferenceRulesTests
{
    private static (Vector3 Min, Vector3 Max) Box(float diagonal) => (Vector3.Zero, new Vector3(diagonal, 0, 0));

    [Theory]
    [InlineData(1023f, 1f, false)]
    [InlineData(1024f, 1f, false)]    // strictly more than fLargeRefMinSize
    [InlineData(1025f, 1f, true)]
    [InlineData(600f, 1f, false)]
    [InlineData(600f, 2f, true)]      // the reference scale counts
    [InlineData(5000f, 0.2f, false)]
    [InlineData(5000f, 0f, true)]     // an unset or zero scale is treated as 1
    public void The_size_rule_uses_the_half_diagonal_times_scale_against_512(float diagonal, float scale, bool expected)
    {
        var (min, max) = Box(diagonal);
        Assert.Equal(expected, LargeReferenceRules.IsLargeEnough(min, max, scale));
    }

    [Fact]
    public void The_magnitude_is_the_half_diagonal_of_the_box()
    {
        Assert.Equal(1.5f * 100f, LargeReferenceRules.Magnitude(Vector3.Zero, new Vector3(0, 300, 0), 1f) / 1f);
        Assert.Equal(25f, LargeReferenceRules.Magnitude(new Vector3(-30, -40, 0), new Vector3(30, 40, 0), 0.5f), 3);
    }

    [Fact]
    public void Moveable_statics_need_flag_4_and_disabled_references_are_recognised()
    {
        Assert.True(LargeReferenceRules.MoveableStaticQualifies(0x4));
        Assert.True(LargeReferenceRules.MoveableStaticQualifies(0x8004));
        Assert.False(LargeReferenceRules.MoveableStaticQualifies(0x8000));
        Assert.True(LargeReferenceRules.StartsDisabled(0x800));
        Assert.True(LargeReferenceRules.StartsDisabled(0x10c00));
        Assert.False(LargeReferenceRules.StartsDisabled(0x400));
        Assert.Equal(512f, LargeReferenceRules.DefaultMinSize);
    }
}
