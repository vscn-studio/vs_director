namespace VSDirector.Data;

/// Reduces sampled linear tracks without changing their ownership or source recording.
public static class DirectorCreationKeyReduction
{
    public static int Reduce(CreationTrack track)
    {
        if (track.Keys.Count <= 2) return 0;
        var keys = track.Keys.OrderBy(k => k.Time).ThenBy(k => k.Id).ToArray();
        // Hand-authored easing and holds must retain their original timing.
        if (keys.Any(k => k.Interpolation != CreationInterpolation.Linear
            || !double.IsFinite(k.Time) || !k.Value.Finite())) return 0;
        for (int i = 1; i < keys.Length; i++)
            if (keys[i].Time <= keys[i - 1].Time) return 0;

        bool angular = track.Channel == CreationChannel.Rotation && track.ShortestRotation;
        double tolerance = track.Channel switch
        {
            CreationChannel.Rotation => .1,
            CreationChannel.Position when track.PartId == Guid.Empty => .001,
            _ => .0001
        };
        static double Angle(double value)
        {
            double wrapped = (value + 180) % 360;
            return (wrapped < 0 ? wrapped + 360 : wrapped) - 180;
        }
        CreationVector Difference(CreationVector a, CreationVector b)
        {
            var delta = b - a;
            return angular ? new(Angle(delta.X), Angle(delta.Y), Angle(delta.Z)) : delta;
        }
        double Error(CreationVector actual, CreationVector expected)
        {
            var delta = Difference(actual, expected);
            return Math.Max(Math.Abs(delta.X), Math.Max(Math.Abs(delta.Y), Math.Abs(delta.Z)));
        }

        var keep = new bool[keys.Length];
        var segments = new Stack<(int Start, int End)>();
        // Bound worst-case work for long noisy recordings. Chunk boundaries are
        // retained, so this never trades away the error bound to meet a key count.
        for (int start = 0; start < keys.Length - 1; start += 300)
        {
            int end = Math.Min(keys.Length - 1, start + 300);
            keep[start] = keep[end] = true;
            segments.Push((start, end));
        }
        while (segments.TryPop(out var segment))
        {
            if (segment.End - segment.Start <= 1) continue;
            var a = keys[segment.Start];
            var b = keys[segment.End];
            var delta = Difference(a.Value, b.Value);
            double largest = tolerance;
            int split = -1;
            for (int i = segment.Start + 1; i < segment.End; i++)
            {
                double t = (keys[i].Time - a.Time) / (b.Time - a.Time);
                double error = Error(keys[i].Value, a.Value + delta * t);
                if (error > largest) { largest = error; split = i; }
            }
            if (split < 0) continue;
            keep[split] = true;
            segments.Push((segment.Start, split));
            segments.Push((split, segment.End));
        }
        int removed = keys.Length - keep.Count(value => value);
        if (removed > 0) track.Keys = keys.Where((_, index) => keep[index]).ToList();
        return removed;
    }
}
