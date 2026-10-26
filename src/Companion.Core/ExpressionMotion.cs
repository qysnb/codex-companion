namespace CodexCompanion.Core;

public static class ExpressionMotion
{
    public static int FrameIndex(BubbleKind bubble) => bubble switch { BubbleKind.Greeting => 1, BubbleKind.Quota => 2, _ => 0 };
    public static TimeSpan Duration(bool reduced) => TimeSpan.FromMilliseconds(reduced ? 70 : 140);
    public static double Opacity(TimeSpan age, bool reduced)
    {
        var t = Math.Clamp(age.TotalMilliseconds / Duration(reduced).TotalMilliseconds, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
