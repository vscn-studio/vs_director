using VSDirector.Data;

internal static class CreationKeyReductionChecks
{
    public static void Run()
    {
        var position = new CreationTrack { Channel = CreationChannel.Position };
        for (int i = 0; i <= 300; i++)
            position.Keys.Add(new() { Time = i / 30d, Value = new(i / 30d, 2, -3) });
        Guid first = position.Keys[0].Id, last = position.Keys[^1].Id;
        Check(DirectorCreationKeyReduction.Reduce(position) == 299, "Linear motion retained redundant samples");
        Check(position.Keys[0].Id == first && position.Keys[^1].Id == last, "Reduction replaced endpoint identities");
        Near(DirectorCreationEvaluator.Sample(position, 4.125, default).X, 4.125, .000001, "Linear movement changed");

        var scale = new CreationTrack { Channel = CreationChannel.Scale, PartId = Guid.NewGuid() };
        for (int i = 0; i <= 150; i++)
            scale.Keys.Add(new() { Time = i / 30d, Value = CreationVector.One });
        Check(DirectorCreationKeyReduction.Reduce(scale) == 149 && scale.Keys.Count == 2,
            "Constant bone scale was not reduced to its boundaries");

        var rotation = new CreationTrack { Channel = CreationChannel.Rotation, ShortestRotation = true };
        for (int i = 0; i <= 600; i++)
            rotation.Keys.Add(new() { Time = i / 30d, Value = new(0, (359 + i * 1.2) % 360, 0) });
        var original = CreationJson.Clone(rotation);
        Check(DirectorCreationKeyReduction.Reduce(rotation) > 500, "Recorded rotation did not simplify");
        for (int i = 0; i <= 2400; i++)
        {
            double time = i / 120d;
            double expected = DirectorCreationEvaluator.Sample(original, time, default).Y;
            double actual = DirectorCreationEvaluator.Sample(rotation, time, default).Y;
            double delta = ((actual - expected + 180) % 360 + 360) % 360 - 180;
            Near(delta, 0, .100001, "Angular wrap or repeated turns changed");
        }
        Check(original.Keys.Count == 601, "Reduction changed the source copy");

        var corner = new CreationTrack { Channel = CreationChannel.Position };
        for (int i = 0; i <= 60; i++)
            corner.Keys.Add(new() { Time = i / 30d, Value = new(i <= 30 ? i / 30d : 1, i > 30 ? (i - 30) / 30d : 0, 0) });
        DirectorCreationKeyReduction.Reduce(corner);
        Check(corner.Keys.Any(k => Math.Abs(k.Time - 1) < 1e-9), "Motion corner was removed");
        foreach (var interpolation in new[] { CreationInterpolation.Hold, CreationInterpolation.Smooth, CreationInterpolation.Bezier })
        {
            var authored = CreationJson.Clone(original);
            authored.Keys[10].Interpolation = interpolation;
            Check(DirectorCreationKeyReduction.Reduce(authored) == 0 && authored.Keys.Count == original.Keys.Count,
                "Reduction rewrote hand-authored interpolation");
        }
        var duplicateTimes = CreationJson.Clone(original);
        duplicateTimes.Keys[1].Time = duplicateTimes.Keys[0].Time;
        Check(DirectorCreationKeyReduction.Reduce(duplicateTimes) == 0, "Ambiguous same-time keys were discarded");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Near(double actual, double expected, double tolerance, string message)
        => Check(Math.Abs(actual - expected) <= tolerance, message);
}
