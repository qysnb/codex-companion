namespace CodexCompanion.Core;

public readonly record struct PokePose(double ScaleX, double ScaleY, double OffsetY)
{
    public static PokePose Rest => new(1, 1, 0);
}

public static class PokeMotion
{
    public static TimeSpan Duration(bool reduced) => TimeSpan.FromMilliseconds(reduced ? 180 : 420);

    public static PokePose Sample(TimeSpan age, bool reduced, double displayHeight = 320) => Sample(age, reduced, PokePose.Rest, displayHeight);

    public static PokePose Sample(TimeSpan age, bool reduced, PokePose initial, double displayHeight = 320)
    {
        var progress = age.TotalMilliseconds / Duration(reduced).TotalMilliseconds;
        if (progress <= 0) return initial;
        if (progress >= 1) return PokePose.Rest;
        // Keep the original squash at 180/240/320 DIP, and fit larger sizes
        // within the existing 12 DIP left margin at the right/bottom origin.
        var stretch = Math.Min(reduced ? .012 : .035, 11.5 / displayHeight);
        var peak = new PokePose(1 + stretch, 1 - (reduced ? .03 : .10), reduced ? 0 : -1.5);
        var from = progress < .22 ? initial : peak;
        var to = progress < .22 ? peak : PokePose.Rest;
        var phase = progress < .22 ? progress / .22 : (progress - .22) / .78;
        var amount = phase * phase * (3 - 2 * phase);
        return new(from.ScaleX + (to.ScaleX - from.ScaleX) * amount, from.ScaleY + (to.ScaleY - from.ScaleY) * amount, from.OffsetY + (to.OffsetY - from.OffsetY) * amount);
    }
}
