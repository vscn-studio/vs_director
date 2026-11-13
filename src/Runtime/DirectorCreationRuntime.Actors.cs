using static VSDirector.Gui.DirectorCreationText;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSDirector.Rendering;
using System.Text.Json.Nodes;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private GuiDialog? nativeAppearanceDialog;
    internal bool NativeAppearanceEditorOpen => nativeAppearanceDialog?.IsOpened() == true;

    internal void ClosePlayerInventory()
    {
        nativeAppearanceDialog?.TryClose();
        nativeAppearanceDialog?.Dispose();
        nativeAppearanceDialog = null;
    }

    internal void SetActorHand(CreationSceneObject actor, int hand, ItemStack? stack)
    {
        if (Session.Project is not { } project || hand is not (1 or 2)
            || project.Models.Find(m => m.Id == actor.ModelId) is not { } model)
            throw new InvalidOperationException(L("creation-native-unavailable"));
        string anchorName = hand == 1 ? "RightHand" : "LeftHand";
        var anchor = model.Parts.FirstOrDefault(p => p.Name.Equals(anchorName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(L("creation-native-hand-anchor-missing"));
        byte[] data = stack?.ToBytes() ?? [];
        string code = stack?.Collectible.Code?.ToString() ?? "";
        Session.Edit(L("creation-apply-player-hand"), p =>
        {
            var attachment = p.Objects.FirstOrDefault(o => o.ParentId == actor.Id && o.ActorHand == hand);
            if (attachment is null)
            {
                attachment = new CreationSceneObject
                {
                    Name = anchorName, Kind = CreationObjectKind.Item, ParentId = actor.Id,
                    ParentPartId = anchor.Id, ActorHand = hand
                };
                p.Objects.Add(attachment);
            }
            attachment.ResourceCode = code;
            attachment.ItemStackData = data;
            attachment.UseRecordedHandItem = false;
        });
    }

    internal IEnumerable<(string Code, string Name)> ActorResources(string search)
        => api.World.EntityTypes.Where(p => p.Client?.Shape?.Base is not null)
            .Select(p => (Code: p.Code.ToString(), Name: ActorName(p.Code)))
            .Where(p => search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Code.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name);
    private static string ActorName(AssetLocation code)
    {
        string key = code.Domain + ":item-creature-" + code.Path;
        string name = Lang.Get(key);
        return name == key ? code.Path.Replace('-', ' ') : name;
    }

    internal IEnumerable<(long Id, string Name)> WorldActors() => api.World.LoadedEntities.Values
        .Where(e => e.EntityId > 0 && e.Properties?.Client?.Shape?.Base is not null)
        .OrderBy(e => e.Pos.SquareDistanceTo(api.World.Player.Entity.Pos))
        .Select(e => (e.EntityId, e.GetName()));

    internal void ApplyPlayerAppearance(CreationSceneObject actor)
    {
        var player = api.World.Player.Entity;
        string shape = player.Properties.Client.Shape?.Base?.ToString() ?? "";
        if (shape.Length == 0 || actor.ActorSource?.ShapeCode != shape)
            throw new InvalidOperationException(L("creation-player-appearance-incompatible"));
        CaptureNativeActor("", player.EntityId, actor);
    }

    internal CreationSceneObject AddNativeActor(string code, long sourceEntity = 0)
        => CaptureNativeActor(code, sourceEntity);

    private CreationSceneObject CaptureNativeActor(string code, long sourceEntity, CreationSceneObject? update = null, Entity? appearance = null)
    {
        if (Session.Project is not { } project) throw new InvalidOperationException(L("creation-open-a-project-first"));
        var entity = appearance ?? (sourceEntity == 0 ? null : api.World.GetEntityById(sourceEntity));
        var properties = entity?.Properties ?? api.World.GetEntityType(new AssetLocation(code));
        if (properties?.Client?.Shape?.Base is not { } shapeCode) throw new InvalidDataException(L("creation-native-unavailable"));
        var location = shapeCode.Clone(); location.Path = "shapes/" + location.Path + ".json";
        string json = api.Assets.Get(location).ToText();
        var imported = DirectorCreationShape.Import(json, entity?.GetName() ?? ActorName(properties.Code));
        var model = imported.Model;
        if (properties.Client.LoadedShapeForEntity is { } loadedShape)
        {
            var sizes = new JsonObject();
            foreach (var entry in loadedShape.TextureSizes) sizes[entry.Key] = new JsonArray(entry.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            model.Extra["textureSizes"] = sizes;
        }
        var textureAssets = new List<CreationAsset>();
        if ((entity is EntityPlayer ep ? ep.TpAnimManager?.Animator ?? ep.AnimManager?.Animator : entity?.AnimManager?.Animator) is AnimatorBase animator)
        {
            var oldNames = model.Parts.ToDictionary(p => p.Id, p => p.Name);
            model.Parts.Clear();
            void Read(ShapeElement[] elements, Guid parent)
            {
                foreach (var element in elements)
                {
                    static CreationVector V(double[]? a) => a is { Length: >= 3 } ? new(a[0], a[1], a[2]) : default;
                    var part = new CreationPart { ParentId = parent, Name = element.Name ?? "Bone", From = V(element.From), To = V(element.To), Origin = V(element.RotationOrigin),
                        Rotation = new(element.RotationX, element.RotationY, element.RotationZ), Scale = new(element.ScaleX, element.ScaleY, element.ScaleZ), Faces = new(), Group = element.From is null };
                    string[] sides = ["north", "east", "south", "west", "up", "down"];
                    for (int i = 0; i < (element.FacesResolved?.Length ?? 0); i++)
                        if (element.FacesResolved![i] is { Enabled: true } face)
                            part.Faces[sides[i]] = new JsonObject { ["texture"] = "#" + face.Texture.TrimStart('#'), ["uv"] = new JsonArray(face.Uv.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), ["rotation"] = face.Rotation };
                    model.Parts.Add(part);
                    if (element.Children is not null) Read(element.Children, part.Id);
                    foreach (var point in element.AttachmentPoints ?? [])
                        model.Parts.Add(new() { Name = point.Code, ParentId = part.Id, Group = true, From = new(point.PosX, point.PosY, point.PosZ), Origin = new(point.PosX, point.PosY, point.PosZ), Rotation = new(point.RotationX, point.RotationY, point.RotationZ), Faces = new() });
                }
            }
            Read(animator.RootElements, Guid.Empty);
            foreach (var animation in imported.Animations)
            {
                foreach (var track in animation.Tracks) track.PartId = model.Parts.FirstOrDefault(p => p.Name == oldNames.GetValueOrDefault(track.PartId))?.Id ?? Guid.Empty;
                animation.Tracks.RemoveAll(t => t.PartId == Guid.Empty);
            }
        }
        foreach (var part in model.Parts.ToArray())
            if (part.Extra["attachmentpoints"] is JsonArray points)
                foreach (var point in points.OfType<JsonObject>())
                {
                    var position = new CreationVector(DirectorCreationShape.N(point, "posX"), DirectorCreationShape.N(point, "posY"), DirectorCreationShape.N(point, "posZ"));
                    model.Parts.Add(new() { Name = DirectorCreationShape.S(point, "code"), ParentId = part.Id, Group = true, From = position, Origin = position,
                        Rotation = new(DirectorCreationShape.N(point, "rotationX"), DirectorCreationShape.N(point, "rotationY"), DirectorCreationShape.N(point, "rotationZ")), Faces = new() });
                }
        foreach (var texture in properties.Client.Textures)
            if (texture.Value.Base is { } path) model.Textures[texture.Key] = path.ToString();
        if (model.Textures.Count == 1)
        {
            string texture = model.Textures.Values.First();
            foreach (var part in model.Parts) foreach (var face in part.Faces)
                if (face.Value is System.Text.Json.Nodes.JsonObject f) model.Textures.TryAdd(DirectorCreationShape.S(f, "texture").TrimStart('#'), texture);
        }
        if (entity is not null)
        {
            var textures = entity.GetTextureSource();
            foreach (string key in model.Parts.SelectMany(p => p.Faces).Select(f => f.Value is JsonObject face ? DirectorCreationShape.S(face, "texture").TrimStart('#') : "").Where(k => k.Length > 0).Distinct())
            {
                var position = textures[key];
                if (position is null) continue;
                var asset = new CreationAsset { Name = model.Name + " · " + key, Kind = CreationAssetKind.Texture };
                asset.Source = "assets/" + asset.Id.ToString("N") + ".png";
                DirectorCreationTextureSnapshot.Save(position, Session.Store.AssetPath(project.Id, asset.Source));
                textureAssets.Add(asset); model.Textures[key] = asset.Source;
            }
        }
        // Native entity shapes are centered around (8, 0, 8) and face a quarter
        // turn from entity yaw. A parent preserves that basis for every pose and attachment.
        var basis = new CreationPart { Name = "Actor origin", Group = true, From = new(-8, 0, -8), To = new(-8, 0, -8),
            Rotation = new(properties.Client.Shape.rotateX, properties.Client.Shape.rotateY + 90, properties.Client.Shape.rotateZ), Faces = new() };
        foreach (var root in model.Parts.Where(p => p.ParentId == Guid.Empty)) root.ParentId = basis.Id;
        model.Parts.Add(basis);
        var obj = new CreationSceneObject { Name = model.Name, Kind = CreationObjectKind.Model, ModelId = model.Id,
            ActorSource = new() { EntityCode = properties.Code.ToString(), ShapeCode = shapeCode.ToString(), SourceWorld = api.World.SavegameIdentifier, SourceEntityId = sourceEntity, AppearanceData = CaptureAppearanceData(entity) },
            Transform = new() { Position = CursorPosition(), Scale = new(properties.Client.Size, properties.Client.Size, properties.Client.Size) } };
        var attachments = new List<CreationSceneObject>();
        foreach (var (hand, anchorName) in new[] { (1, "RightHand"), (2, "LeftHand") })
        {
            var anchor = model.Parts.FirstOrDefault(p => p.Name.Equals(anchorName, StringComparison.OrdinalIgnoreCase));
            if (anchor is null) continue;
            var stack = entity is EntityAgent agent ? (hand == 1 ? agent.RightHandItemSlot : agent.LeftHandItemSlot)?.Itemstack : null;
            attachments.Add(new CreationSceneObject { Name = anchorName, Kind = CreationObjectKind.Item, ParentId = obj.Id, ParentPartId = anchor.Id,
                ActorHand = hand, ItemStackData = stack?.ToBytes() ?? [], ResourceCode = stack?.Collectible?.Code?.ToString() ?? "" });
        }
        if (update is not null)
        {
            if (update.ActorSource is { } originalSource) obj.ActorSource.SourceWorld = originalSource.SourceWorld;
            Guid editing = Session.AnimationObject == update.Id ? Session.EditingAnimation?.Id ?? Guid.Empty : Guid.Empty;
            double time = Session.EditTime;
            Dictionary<Guid, Guid> actions = new();
            Session.Edit(L("creation-apply-player-appearance"), p =>
            {
                actions = DirectorCreationAppearance.Apply(p, update, model);
                update.ActorSource = obj.ActorSource;
                p.Assets.AddRange(textureAssets);
            });
            if (actions.TryGetValue(editing, out var action))
            {
                Session.EnterAnimation(action, update.Id);
                Session.Seek(time);
            }
            Renderer.ReloadTextures();
            return update;
        }
        Session.Edit(L("creation-native-add"), p => { p.Models.Add(model); p.Animations.AddRange(imported.Animations); p.Assets.AddRange(textureAssets); p.Objects.Add(obj); p.Objects.AddRange(attachments); });
        Renderer.ReloadTextures(); return obj;
    }
}
