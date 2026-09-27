namespace WikipediaInterestSkill.Statistics;

public static class BlockBootstrap
{
    public static void Resample(ReadOnlySpan<double> source, int blockLength, Random rng, Span<double> destination)
    {
        var n = source.Length;
        if (n == 0) throw new ArgumentException("Cannot resample an empty series.");
        var l = Math.Clamp(blockLength, 1, n);
        var starts = n - l + 1;
        var filled = 0;
        var total = destination.Length;
        while (filled < total)
        {
            var start = rng.Next(starts);
            var take = Math.Min(l, total - filled);
            source.Slice(start, take).CopyTo(destination.Slice(filled, take));
            filled += take;
        }
    }

    public static int StableSeed(params object?[] parts)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var part in parts)
            {
                var text = part?.ToString() ?? "∅";
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(text + "|"))
                {
                    hash ^= b;
                    hash *= 16777619u;
                }
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }
}
