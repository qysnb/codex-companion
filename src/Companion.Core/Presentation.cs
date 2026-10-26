namespace CodexCompanion.Core;

public static class CharacterNames
{
    public static string Get(string id) => id switch { "elegant" => "Astra", "mischievous" => "Sol", "cute" => "Luna", "fable" => "Fable", "pro" => "Pro", "flash" => "Flash", "haiku" => "Haiku", _ => throw new ArgumentException("Unknown character", nameof(id)) };
}

public static class BubblePlacement
{
    public static PixelRect Place(PixelRect character, PixelRect work, double width, double height, double scale)
    {
        var gap = 6 * scale;
        var x = character.X + character.Width - width;
        var y = character.Y - height - gap;
        if (y < work.Y)
        {
            var below = character.Y + character.Height + gap;
            if (below + height <= work.Y + work.Height)
                y = below;
            else if (height <= work.Height)
            {
                var left = character.X - width - gap;
                var right = character.X + character.Width + gap;
                if (left >= work.X)
                { x = left; y = character.Y + character.Height - height; }
                else if (right + width <= work.X + work.Width)
                { x = right; y = character.Y + character.Height - height; }
            }
        }
        return new(Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width)),
            Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height)), width, height);
    }
}

public sealed class LongPressDrag(TimeSpan pressedAt)
{
    bool beyondSlop, active;
    public bool Update(TimeSpan now, double dx, double dy)
    {
        beyondSlop |= Math.Abs(dx) >= 5 || Math.Abs(dy) >= 5;
        if (beyondSlop && now - pressedAt >= TimeSpan.FromMilliseconds(350)) active = true;
        return active;
    }
    public bool IsClick(TimeSpan now) => !beyondSlop && !active && now - pressedAt < TimeSpan.FromMilliseconds(350);
}
