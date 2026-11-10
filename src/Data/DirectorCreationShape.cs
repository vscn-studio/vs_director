using System.Text.Json.Nodes;

namespace VSDirector.Data;

/// <summary>Editable native Shape documents. Unrecognized fields survive a round trip.</summary>
public static class DirectorCreationShape
{
    public static (CreationModel Model, List<CreationAnimation> Animations) Import(string json, string name)
    {
        var root = JsonNode.Parse(json, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip }) as JsonObject
            ?? throw new InvalidDataException("模型需要 Vintage Story Shape JSON 对象。");
        if (root["elements"] is not JsonArray elements) throw new InvalidDataException("模型缺少 elements。");
        var model = new CreationModel { Name = name, TextureWidth = (int)N(root, "textureWidth", 16), TextureHeight = (int)N(root, "textureHeight", 16), Extra = (JsonObject)root.DeepClone() };
        foreach (string field in new[] { "elements", "animations", "textures", "textureWidth", "textureHeight" }) model.Extra.Remove(field);
        if (root["textures"] is JsonObject textures)
            foreach (var tex in textures) if (tex.Value is JsonValue v && v.TryGetValue<string>(out var path)) model.Textures[tex.Key] = path;
        void Read(JsonArray list, Guid parent, int depth)
        {
            if (depth > 128) throw new InvalidDataException("模型层级超过 128 层。");
            foreach (var node in list.OfType<JsonObject>())
            {
                var part = new CreationPart { Name = S(node, "name", "Cube"), ParentId = parent, From = V(node["from"]), To = V(node["to"]),
                    Origin = V(node["rotationOrigin"]), Rotation = new(N(node, "rotationX"), N(node, "rotationY"), N(node, "rotationZ")),
                    Scale = new(N(node, "scaleX", 1), N(node, "scaleY", 1), N(node, "scaleZ", 1)),
                    Faces = node["faces"] is JsonObject f ? (JsonObject)f.DeepClone() : new(), Extra = (JsonObject)node.DeepClone() };
                part.Group = node["from"] is null && node["to"] is null;
                foreach (string field in new[] { "name", "from", "to", "rotationOrigin", "rotationX", "rotationY", "rotationZ", "scaleX", "scaleY", "scaleZ", "children", "faces" }) part.Extra.Remove(field);
                model.Parts.Add(part);
                if (node["children"] is JsonArray children) Read(children, part.Id, depth + 1);
            }
        }
        Read(elements, Guid.Empty, 0);
        List<CreationAnimation> animations = new();
        if (root["animations"] is JsonArray animationNodes)
            foreach (var node in animationNodes.OfType<JsonObject>()) animations.Add(ImportAnimation(node, model));
        model.AnimationVersion = animations.Count == 0 ? 0 : (int)N(animations[0].Extra, "version");
        if (model.AnimationVersion is not (0 or 1) || animations.Any(a => (int)N(a.Extra, "version") != model.AnimationVersion))
            throw new InvalidDataException("此模型包含混合版本的动画，请先统一为 Vintage Story 动画版本 0 或 1。");
        return (model, animations);
    }

    public static CreationAnimation ImportAnimation(JsonObject node, CreationModel model)
    {
        var animation = new CreationAnimation { Name = S(node, "name", S(node, "code", "Animation")), ModelId = model.Id,
            Duration = Math.Max(1d / 30, N(node, "quantityframes", 30) / 30), Loop = S(node, "onAnimationEnd") == "Repeat", Extra = (JsonObject)node.DeepClone() };
        animation.Extra.Remove("keyframes");
        if (node["keyframes"] is JsonArray frames)
            foreach (var frame in frames.OfType<JsonObject>())
            {
                if (frame["elements"] is not JsonObject transforms) continue;
                double time = N(frame, "frame") / 30;
                foreach (var element in transforms)
                {
                    var part = model.Parts.Find(p => p.Name == element.Key);
                    if (part is null || element.Value is not JsonObject values) continue;
                    ReadChannel(CreationChannel.Position, "offset", 0, 1d / 16);
                    ReadChannel(CreationChannel.Rotation, "rotation", 0, 1);
                    ReadChannel(CreationChannel.Scale, "stretch", 1, 1);
                    void ReadChannel(CreationChannel channel, string prefix, double fallback, double factor)
                    {
                        if (!new[] { "X", "Y", "Z" }.Any(axis => values.ContainsKey(prefix + axis))) return;
                        var track = animation.Tracks.Find(t => t.PartId == part.Id && t.Channel == channel);
                        if (track is null) { track = new() { PartId = part.Id, Channel = channel }; animation.Tracks.Add(track); }
                        DirectorCreationEdits.SetKey(track, time, new CreationVector(N(values, prefix + "X", fallback), N(values, prefix + "Y", fallback), N(values, prefix + "Z", fallback)) * factor);
                    }
                }
            }
        return animation;
    }

    public static JsonObject Export(CreationModel model, IEnumerable<CreationAnimation>? animations = null, CreationVector offset = default)
    {
        var root = (JsonObject)model.Extra.DeepClone();
        root["textureWidth"] = model.TextureWidth; root["textureHeight"] = model.TextureHeight;
        var textures = new JsonObject(); foreach (var t in model.Textures) textures[t.Key] = t.Value; root["textures"] = textures;
        JsonArray Parts(Guid parent)
        {
            var result = new JsonArray();
            foreach (var p in model.Parts.Where(p => p.ParentId == parent))
            {
                var node = (JsonObject)p.Extra.DeepClone(); node["name"] = p.Name;
                var originOffset = parent == Guid.Empty ? offset : default;
                if (!p.Group) { node["from"] = A(p.From + originOffset - new CreationVector(p.Inflate, p.Inflate, p.Inflate)); node["to"] = A(p.To + originOffset + new CreationVector(p.Inflate, p.Inflate, p.Inflate)); }
                node["rotationOrigin"] = A(p.Origin + originOffset);
                node["rotationX"] = p.Rotation.X; node["rotationY"] = p.Rotation.Y; node["rotationZ"] = p.Rotation.Z;
                node["scaleX"] = p.Scale.X; node["scaleY"] = p.Scale.Y; node["scaleZ"] = p.Scale.Z;
                node["faces"] = p.Visible ? p.Faces.DeepClone() : new JsonObject();
                node["children"] = Parts(p.Id); result.Add(node);
            }
            return result;
        }
        root["elements"] = Parts(Guid.Empty);
        if (animations is not null)
        {
            var list = new JsonArray(); foreach (var a in animations.Where(a => a.ModelId == model.Id)) list.Add(ExportAnimation(a, model)); root["animations"] = list;
        }
        return root;
    }

    public static JsonObject ExportAnimation(CreationAnimation animation, CreationModel model)
    {
        var node = (JsonObject)animation.Extra.DeepClone(); node["name"] = animation.Name;
        node["code"] ??= animation.Name.ToLowerInvariant().Replace(' ', '-');
        node["version"] = model.AnimationVersion; node["quantityframes"] = Math.Max(1, (int)Math.Ceiling(animation.Duration * 30));
        node["onAnimationEnd"] = animation.Loop ? "Repeat" : "Hold";
        var frames = new JsonArray();
        // Native Shape has linear per-frame interpolation. Bake authored curves at its native 30 Hz.
        for (int frame = 0; frame < Math.Ceiling(animation.Duration * 30); frame++)
        {
            var elements = new JsonObject();
            foreach (var tracks in animation.Tracks.Where(t => t.Enabled).GroupBy(t => t.PartId))
            {
                var part = model.Parts.Find(p => p.Id == tracks.Key); if (part is null) continue;
                var values = new JsonObject();
                foreach (var track in tracks)
                {
                    string prefix = track.Channel switch { CreationChannel.Position => "offset", CreationChannel.Rotation => "rotation", CreationChannel.Scale => "stretch", _ => "" };
                    if (prefix.Length == 0) continue;
                    var value = DirectorCreationEvaluator.Sample(track, frame / 30d, track.Channel == CreationChannel.Scale ? CreationVector.One : default);
                    if (track.Channel == CreationChannel.Position) value *= 16;
                    values[prefix + "X"] = value.X; values[prefix + "Y"] = value.Y; values[prefix + "Z"] = value.Z;
                }
                elements[part.Name] = values;
            }
            frames.Add(new JsonObject { ["frame"] = frame, ["elements"] = elements });
        }
        node["keyframes"] = frames; return node;
    }

    public static CreationAnimation BasicAction(CreationModel model, string name, bool walk)
    {
        var animation = new CreationAnimation { Name = name, ModelId = model.Id, Duration = walk ? 1 : 3, Loop = true };
        foreach (var entry in model.BodyMapping)
        {
            if (!walk && entry.Key != "body") continue;
            var track = new CreationTrack { PartId = entry.Value, Channel = CreationChannel.Rotation };
            double amplitude = walk ? (entry.Key.Contains("left", StringComparison.OrdinalIgnoreCase) ? 28 : -28) : 2;
            if (entry.Key is "head" or "body" && walk) amplitude = 3;
            for (int i = 0; i <= 4; i++) DirectorCreationEdits.SetKey(track, animation.Duration * i / 4, new(amplitude * Math.Sin(i * Math.PI / 2), 0, 0)).Interpolation = CreationInterpolation.Smooth;
            animation.Tracks.Add(track);
        }
        return animation;
    }

    public static double Number(JsonNode? node, double fallback = 0)
        => node is JsonValue && double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : fallback;
    public static double N(JsonObject obj, string key, double fallback = 0) => Number(obj[key], fallback);
    public static string S(JsonObject obj, string key, string fallback = "") => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    public static CreationVector V(JsonNode? node) => node is JsonArray a && a.Count >= 3 ? new(Number(a[0]), Number(a[1]), Number(a[2])) : default;
    public static JsonArray A(CreationVector v) => new(v.X, v.Y, v.Z);
}
