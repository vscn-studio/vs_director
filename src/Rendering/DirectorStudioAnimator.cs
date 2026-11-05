using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Rendering;

/// <summary>Samples native Shape joints at an absolute action time; never ticks a gameplay entity.</summary>
internal sealed class DirectorStudioAnimator(Shape shape)
    : ClientAnimator(() => 1, shape.Animations ?? [], shape.Elements, shape.JointsById)
{
    internal float[] Sample(DirectorActionEvaluation? evaluation)
    {
        activeAnimCount = 0;
        foreach (var layer in evaluation?.Layers ?? [])
        foreach (var state in layer.State.AnimationStates)
        {
            if (activeAnimCount >= MaxConcurrentAnimations || layer.EffectiveWeight <= 0) continue;
            var running = GetAnimationState(state.Code) ?? GetAnimationState(state.StateCode);
            if (running is null) throw new InvalidDataException("Model does not contain animation: " + state.Code);
            if (running.Animation.KeyFrames is not { Length: > 0 } || running.Animation.QuantityFrames <= 0) continue;
            var meta = new AnimationMetaData { Code = running.Animation.Code, Animation = running.Animation.Code,
                AnimationSpeed = 1, Weight = Math.Clamp(state.Weight * layer.EffectiveWeight, 0, 1),
                BlendMode = layer.Layer.BlendMode == DirectorActionBlendMode.Additive ? EnumAnimationBlendMode.Add : EnumAnimationBlendMode.Average,
                ElementWeight = new(state.ElementWeights),
                ElementBlendMode = state.ElementBlendModes.ToDictionary(p => p.Key, p => (EnumAnimationBlendMode)p.Value) };
            foreach (var (bone, weight) in layer.Layer.ElementWeights) meta.ElementWeight[bone] = weight;
            AnimNowActive(running, meta);
            running.CurrentFrame = Math.Clamp(state.RecordedFrame ?? state.Progress * (running.Animation.QuantityFrames - 1), 0, running.Animation.QuantityFrames - 1);
            running.EasingFactor = Math.Clamp(state.EasingFactor, 0, 1);
            CurAnims[activeAnimCount++] = running;
        }
        calculateMatrices(0);
        var matrices = (float[])TransformationMatrices.Clone();
        foreach (var layer in evaluation?.Layers ?? [])
        foreach (var (name, bone) in layer.BoneOverrides)
        {
            var joint = jointsById.Values.FirstOrDefault(j => string.Equals(j.Element.Name, name, StringComparison.OrdinalIgnoreCase));
            if (joint is null) throw new InvalidDataException("Model does not contain bone: " + name);
            float weight = Math.Clamp(layer.EffectiveWeight, 0, 1);
            var adjustment = new Matrixf().Identity().Translate(bone.TranslationX * weight, bone.TranslationY * weight, bone.TranslationZ * weight)
                .RotateX(bone.RotationX * weight * GameMath.DEG2RAD).RotateY(bone.RotationY * weight * GameMath.DEG2RAD)
                .RotateZ(bone.RotationZ * weight * GameMath.DEG2RAD)
                .Scale(1 + (bone.ScaleX - 1) * weight, 1 + (bone.ScaleY - 1) * weight, 1 + (bone.ScaleZ - 1) * weight);
            var current = new float[16]; Array.Copy(matrices, joint.JointId * 16, current, 0, 16);
            var result = Mat4f.Mul(new float[16], current, adjustment.Values);
            Array.Copy(result, 0, matrices, joint.JointId * 16, 16);
        }
        return matrices;
    }

    internal static MeshData Deform(MeshData source, float[] matrices)
    {
        var mesh = source.Clone();
        float[] normal = new float[3];
        for (int v = 0; v < mesh.VerticesCount; v++)
        {
            int offset = (source.CustomInts?.Values[v] ?? 0) * 16;
            if (offset < 0 || offset + 16 > matrices.Length) continue;
            int p = v * 3; float x = source.xyz[p], y = source.xyz[p + 1], z = source.xyz[p + 2];
            mesh.xyz[p] = matrices[offset] * x + matrices[offset + 4] * y + matrices[offset + 8] * z + matrices[offset + 12];
            mesh.xyz[p + 1] = matrices[offset + 1] * x + matrices[offset + 5] * y + matrices[offset + 9] * z + matrices[offset + 13];
            mesh.xyz[p + 2] = matrices[offset + 2] * x + matrices[offset + 6] * y + matrices[offset + 10] * z + matrices[offset + 14];
            if (mesh.Flags is null) continue;
            VertexFlags.UnpackNormal(source.Flags[v], normal);
            // Inverse transpose handles nonuniform bone scales as well as rotation.
            var transform = new System.Numerics.Matrix4x4(matrices[offset], matrices[offset+1], matrices[offset+2], matrices[offset+3],
                matrices[offset+4], matrices[offset+5], matrices[offset+6], matrices[offset+7], matrices[offset+8], matrices[offset+9], matrices[offset+10], matrices[offset+11],
                matrices[offset+12], matrices[offset+13], matrices[offset+14], matrices[offset+15]);
            if (System.Numerics.Matrix4x4.Invert(transform, out var inverse))
            {
                var n = System.Numerics.Vector3.TransformNormal(new(normal[0], normal[1], normal[2]), System.Numerics.Matrix4x4.Transpose(inverse));
                if (n.LengthSquared() > 1e-12f) n = System.Numerics.Vector3.Normalize(n);
                mesh.Flags[v] = (mesh.Flags[v] & VertexFlags.ClearNormalBitMask) | VertexFlags.PackNormal(n.X, n.Y, n.Z);
            }
        }
        mesh.CustomInts = null; // Standard shader receives the already deformed geometry.
        return mesh;
    }
}
