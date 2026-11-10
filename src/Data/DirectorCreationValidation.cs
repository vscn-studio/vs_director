using System.Diagnostics.CodeAnalysis;

namespace VSDirector.Data;

public sealed partial class DirectorCreationProject
{
    private void ValidateDocument()
    {
        static void Require([DoesNotReturnIf(false)] bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
        static void List<T>(IEnumerable<T>? items, string field) where T : class
        { Require(items is not null && items.All(v => v is not null), "作品列表无效：" + field); }
        static bool Transform(CreationTransform? value) => value is not null && value.Position.Finite()
            && value.Rotation.Finite() && value.Scale.Finite() && value.Pivot.Finite();
        List(Objects, "对象"); List(Models, "模型"); List(Assets, "素材"); List(Tracks, "轨道");
        List(Animations, "动作"); List(Clips, "剪辑"); List(Presets, "预设"); List(Folders, "文件夹");
        Require(Output is not null && Output.Directory is not null && Output.Encoder is not null
            && Output.Quality is >= 0 and <= 51, "视频输出设置无效。");
        var objects = Objects.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        var models = Models.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var assets = Assets.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var animations = Animations.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        bool Part(Guid model, Guid part) => part == Guid.Empty || models.TryGetValue(model, out var m) && m.Parts?.Any(p => p is not null && p.Id == part) == true;
        Require(CameraId == Guid.Empty || objects.TryGetValue(CameraId, out var camera) && camera.Kind == CreationObjectKind.Camera, "拍摄相机不存在。");
        foreach (var model in Models)
        {
            List(model.Parts, "模型部件");
            Require(model.Name is not null && model.Folder is not null && model.Extra is not null && model.Textures is not null
                && model.Textures.All(t => t.Value is not null) && model.BodyMapping is not null
                && model.AnimationVersion is 0 or 1 && model.TextureWidth is > 0 and <= 8192 && model.TextureHeight is > 0 and <= 8192, "模型字段无效。");
            Require(model.BodyMapping.Values.All(p => Part(model.Id, p)), "动作映射指向已删除的骨骼。");
            foreach (var part in model.Parts)
            {
                Require(part.Name is not null && part.Faces is not null && part.Extra is not null, "部件字段无效。");
                foreach (var face in part.Faces)
                {
                    Require(face.Value is System.Text.Json.Nodes.JsonObject, "模型表面应为 JSON 对象。");
                    if (face.Value!["uv"] is { } uv)
                        Require(uv is System.Text.Json.Nodes.JsonArray a && a.Count == 4
                            && a.All(v => double.TryParse(v?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) && double.IsFinite(n)), "表面 UV 必须包含四个有限数值。");
                }
            }
        }
        foreach (var obj in Objects)
        {
            Require(obj.PlayerBody is not null && obj.PlayerBody.Valid(), "Invalid player collision settings.");
            Require(obj.ActorHand is >= 0 and <= 2 && (obj.RecordingChannels & ~CreationRecordingChannels.All) == 0, "Invalid actor recording options.");
            Require(obj.IkChains is not null && double.IsFinite(obj.IkWeight) && obj.IkWeight is >= 0 and <= 1, "Invalid IK configuration.");
            foreach (var chain in obj.IkChains)
            {
                Require(chain is not null && chain.Id != Guid.Empty && chain.Name is not null && chain.Upper != Guid.Empty && chain.Lower != Guid.Empty
                    && models.TryGetValue(obj.ModelId, out var rig) && rig.Parts.Any(p => p.Id == chain.Lower && p.ParentId == chain.Upper)
                    && Part(obj.ModelId, chain.Upper) && objects.ContainsKey(chain.Target) && objects.ContainsKey(chain.Pole) && chain.EndOffset.Finite(), "Invalid IK chain.");
                foreach (var control in new[] { chain.Target, chain.Pole })
                {
                    var visited = new HashSet<Guid>(); var current = control;
                    while (objects.TryGetValue(current, out var ancestor) && visited.Add(current))
                    {
                        Require(current != obj.Id, "IK controls cannot be parented to the driven actor.");
                        current = ancestor.ParentId;
                    }
                }
            }
            Require(obj.IkChains.Select(c => c.Id).Distinct().Count() == obj.IkChains.Count, "Duplicate IK chain.");
            Require(obj.Name is not null && obj.Text is not null && obj.ResourceCode is not null && obj.ReplacePlayerUid is not null
                && obj.HiddenParts is not null && obj.Pose is not null && Transform(obj.Transform)
                && Enum.IsDefined(obj.Kind) && Enum.IsDefined(obj.CameraMode), "对象字段无效。");
            Require(obj.ModelId == Guid.Empty || models.ContainsKey(obj.ModelId), "对象引用的模型不存在。");
            Require(obj.AssetId == Guid.Empty || assets.ContainsKey(obj.AssetId), "对象引用的素材不存在。");
            Require(obj.TargetId == Guid.Empty || objects.ContainsKey(obj.TargetId), "目标对象不存在。");
            Require(obj.Pose.All(p => Part(obj.ModelId, p.Key) && Transform(p.Value)) && obj.HiddenParts.All(p => Part(obj.ModelId, p)), "姿势指向不存在的部件。");
            Require(obj.ParentPartId == Guid.Empty || objects.TryGetValue(obj.ParentId, out var parent) && Part(parent.ModelId, obj.ParentPartId), "挂接骨骼不存在。");
            Require(double.IsFinite(obj.Glow) && obj.Glow is >= 0 and <= 1 && obj.Opacity is >= 0 and <= 1 && obj.Fov is >= 1 and <= 170
                && obj.ParticleCount is >= 0 and <= 10000 && obj.ParticleLife is > 0 and <= 300
                && obj.ParticleStart >= 0 && obj.ParticleDuration >= 0, "对象外观或粒子参数无效。");
            List(obj.Blocks, "结构方块");
            Require(obj.Blocks.Count <= 4096 && obj.Blocks.All(b => b.Code is not null && b.Position.Finite()), "结构方块数据无效。");
        }
        foreach (var asset in Assets) Require(asset.Name is not null && asset.Folder is not null && asset.Source is not null && Enum.IsDefined(asset.Kind), "素材字段无效。");
        void Track(CreationTrack track, Guid modelId, bool animation)
        {
            List(track.Keys, "关键帧");
            Require(Enum.IsDefined(track.Channel), "轨道属性类型无效。");
            if (!animation) Require(objects.TryGetValue(track.ObjectId, out var obj) && Part(obj.ModelId, track.PartId), "轨道对象或骨骼不存在。");
            else Require(Part(modelId, track.PartId), "动作骨骼不存在。");
            foreach (var key in track.Keys) Require(Enum.IsDefined(key.Interpolation), "关键帧插值类型无效。");
        }
        foreach (var track in Tracks) Track(track, Guid.Empty, false);
        foreach (var animation in Animations)
        {
            Require(animation.Name is not null && animation.Folder is not null && animation.Extra is not null && models.ContainsKey(animation.ModelId), "动作模型不存在。");
            List(animation.Tracks, "动作轨道"); foreach (var track in animation.Tracks) Track(track, animation.ModelId, true);
        }
        foreach (var clip in Clips)
        {
            Require(!clip.BakedPose || clip.AnimationId != Guid.Empty, "A baked pose requires an animation.");
            Require(clip.PerformanceAssetId == Guid.Empty || assets.TryGetValue(clip.PerformanceAssetId, out var performance) && performance.Kind == CreationAssetKind.Performance, "Invalid performance asset reference.");
            Require(clip.Name is not null && clip.Lane >= 0 && (clip.AnimationId == Guid.Empty || animations.ContainsKey(clip.AnimationId))
                && (clip.AudioAssetId == Guid.Empty || assets.TryGetValue(clip.AudioAssetId, out var a) && a.Kind == CreationAssetKind.Audio)
                && (clip.ObjectId == Guid.Empty || objects.ContainsKey(clip.ObjectId)), "剪辑引用无效。");
            if (clip.AnimationId != Guid.Empty) Require(objects.TryGetValue(clip.ObjectId, out var obj)
                && obj.ModelId == animations[clip.AnimationId].ModelId, "动作与演员的模型不匹配。");
        }
        foreach (var preset in Presets)
        {
            Require(preset.Name is not null && preset.Folder is not null && preset.Pose is not null
                && (preset.ModelId == Guid.Empty || models.ContainsKey(preset.ModelId))
                && preset.Pose.All(p => Part(preset.ModelId, p.Key) && Transform(p.Value)), "预设数据无效。");
            if (preset.Appearance is { } appearance)
                Require(Transform(appearance.Transform) && appearance.Pose is not null && appearance.HiddenParts is not null
                    && appearance.Pose.All(p => Part(preset.ModelId, p.Key) && Transform(p.Value))
                    && appearance.HiddenParts.All(p => Part(preset.ModelId, p)), "预设外观数据无效。");
        }
    }
}
