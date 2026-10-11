using System.Windows;
using System.Windows.Media.Animation;

namespace AnvilLOD.App;

/// <summary>Animates a grid row height or column width (a <see cref="GridLength"/>), in pixels or in stars.</summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public GridLength From { get; set; }
    public GridLength To { get; set; }
    public IEasingFunction? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        double t = animationClock.CurrentProgress ?? 0;
        if (EasingFunction is not null) t = EasingFunction.Ease(t);
        return new GridLength(From.Value + (To.Value - From.Value) * t, To.GridUnitType);
    }
}
