namespace CodexCompanion.Core;

public enum BubbleKind { None, Quota, Greeting }
public sealed class CompanionController
{
    private long clicks;
    public long Version { get; private set; }
    public string Character { get; private set; } = "cute";
    public string Language { get; private set; } = "zh-CN";
    public BubbleKind Bubble { get; private set; }
    public BubbleKind Click() { Version++; return Bubble = ++clicks % 2 == 1 ? BubbleKind.Quota : BubbleKind.Greeting; }
    public void CloseBubble() { Version++; Bubble = BubbleKind.None; }
    public bool IsCurrent(long version) => version == Version;
    public void SetCharacter(string value) { if (!CompanionSettings.Characters.Contains(value)) throw new ArgumentException("Unknown character"); Character = value; }
    public void SetLanguage(string value) { if (!CompanionSettings.Languages.Contains(value)) throw new ArgumentException("Unknown language"); Language = value; }
}

public sealed class ShuffleBag
{
    private readonly string[] source;
    private readonly Random random;
    private readonly Queue<string> queue = new();
    private string? previous;
    public ShuffleBag(IEnumerable<string> lines, Random? random = null)
    {
        source = lines.Distinct().ToArray();
        if (source.Length == 0) throw new ArgumentException("At least one line is required");
        this.random = random ?? Random.Shared;
    }
    public string Next()
    {
        if (queue.Count == 0)
        {
            var items = (string[])source.Clone();
            for (var i = items.Length - 1; i > 0; i--) { var j = random.Next(i + 1); (items[i], items[j]) = (items[j], items[i]); }
            if (items.Length > 1 && items[0] == previous) (items[0], items[1]) = (items[1], items[0]);
            foreach (var item in items) queue.Enqueue(item);
        }
        return previous = queue.Dequeue();
    }
}
