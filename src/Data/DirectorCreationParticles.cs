namespace VSDirector.Data;

public readonly record struct CreationParticle(long Index, double Age, CreationVector Position, double Size, double Alpha);

/// <summary>Emission depends only on object ID and absolute time, including after a backwards seek.</summary>
public static class DirectorCreationParticles
{
    public static IEnumerable<CreationParticle> Sample(CreationSceneObject obj, double time)
    {
        double elapsed = time - obj.ParticleStart, life = Math.Max(.05, obj.ParticleLife);
        if (elapsed < 0 || elapsed >= obj.ParticleDuration + life) yield break;
        double rate = Math.Clamp(obj.ParticleCount, 1, 1000);
        long first = Math.Max(0, (long)Math.Ceiling((elapsed - life) * rate));
        long last = Math.Min((long)Math.Floor(elapsed * rate), (long)Math.Ceiling(obj.ParticleDuration * rate) - 1);
        // Bound draw cost while keeping the choice stable across cameras and repeated evaluations.
        long stride = Math.Max(1, (long)Math.Ceiling(rate * life / 2048));
        first = (first + stride - 1) / stride * stride;
        for (long i = first; i <= last; i += stride)
        {
            double age = elapsed - i / rate;
            if (age < 0 || age >= life) continue;
            double x = Noise(obj.Id, i, 0) - .5, z = Noise(obj.Id, i, 1) - .5, speed = Noise(obj.Id, i, 2);
            var velocity = obj.ResourceCode switch
            {
                "smoke" => new CreationVector(x * .25, .3 + speed * .3, z * .25),
                "snow" => new CreationVector(x * .3, -.2 - speed * .2, z * .3),
                "glow" => new CreationVector(x * .25, speed * .2, z * .25),
                _ => new CreationVector(x * 1.6, .8 + speed * .8, z * 1.6)
            };
            double gravity = obj.ResourceCode is "smoke" or "glow" or "snow" ? 0 : -2;
            var position = velocity * age + new CreationVector(0, .5 * gravity * age * age, 0);
            double size = obj.ResourceCode == "smoke" ? .08 + age * .1 : obj.ResourceCode == "glow" ? .05 : .035;
            yield return new(i, age, position, size, Math.Clamp(1 - age / life, 0, 1));
        }
    }

    private static double Noise(Guid id, long index, uint channel)
    {
        Span<byte> bytes = stackalloc byte[16]; id.TryWriteBytes(bytes);
        unchecked
        {
            uint hash = 2166136261;
            foreach (byte b in bytes) hash = (hash ^ b) * 16777619;
            hash ^= (uint)index * 0x9E3779B9 ^ (uint)(index >> 32) ^ channel * 0x85EBCA6B;
            hash ^= hash >> 16; hash *= 0x7FEB352D; hash ^= hash >> 15; hash *= 0x846CA68B; hash ^= hash >> 16;
            return hash / (double)uint.MaxValue;
        }
    }
}
