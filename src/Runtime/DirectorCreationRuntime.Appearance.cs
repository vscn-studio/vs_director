using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using VSDirector.Data;
using VSDirector.Gui;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private ItemStack[]? actorItemCatalog;
    private readonly Dictionary<string, SkinnablePart[]> actorSkinOptions = new();
    private static byte[] CaptureAppearanceData(Entity? entity)
    {
        if (entity is null) return [];
        var data = new TreeAttribute();
        if (entity.WatchedAttributes.GetTreeAttribute("skinConfig") is { } skin) data["skinConfig"] = skin.Clone();
        if (entity.GetBehavior<EntityBehaviorTexturedClothing>()?.Inventory is { } inventory)
        {
            var clothes = new TreeAttribute(); inventory.ToTreeAttributes(clothes); data["wearablesInv"] = clothes;
        }
        else if (entity.WatchedAttributes.GetTreeAttribute("wearablesInv") is { } clothes) data["wearablesInv"] = clothes.Clone();
        return data.ToBytes();
    }
    private TreeAttribute ActorAppearance(CreationSceneObject actor)
    {
        if (actor.ActorSource?.AppearanceData is { Length: > 0 } bytes) return TreeAttribute.CreateFromBytes(bytes);
        // Legacy projects acquire a snapshot once; subsequent edits use the project snapshot.
        var source = actor.ActorSource?.SourceWorld == api.World.SavegameIdentifier
            ? api.World.GetEntityById(actor.ActorSource.SourceEntityId) : null;
        if (source is null && actor.ActorSource?.EntityCode == api.World.Player.Entity.Code.ToString()) source = api.World.Player.Entity;
        var data = CaptureAppearanceData(source);
        return data.Length == 0 ? new TreeAttribute() : TreeAttribute.CreateFromBytes(data);
    }
    internal bool ActorHasClothing(CreationSceneObject actor)
        => ActorSkinOptions(actor).Count > 0;
    internal IReadOnlyList<SkinnablePart> ActorSkinOptions(CreationSceneObject actor)
    {
        string code = actor.ActorSource?.EntityCode ?? "";
        if (!actorSkinOptions.TryGetValue(code, out var options))
        {
            var properties = code.Length == 0 ? null : api.World.GetEntityType(new AssetLocation(code));
            options = properties?.Attributes?["skinnableParts"].AsObject<SkinnablePart[]>() ?? [];
            if (options.Length > 0) options = api.ModLoader.GetModSystem<ModSystemSkinnableAdditions>().AppendAdditions(options);
            actorSkinOptions[code] = options;
        }
        return options;
    }
    internal string ActorSkinVariant(CreationSceneObject actor, string part)
        => ActorAppearance(actor).GetTreeAttribute("skinConfig")?.GetTreeAttribute("appliedParts")?.GetString(part) ?? "";
    internal string ActorWearableName(CreationSceneObject actor, int slot)
    {
        var stack = ActorAppearance(actor).GetTreeAttribute("wearablesInv")?.GetTreeAttribute("slots")?.GetItemstack(slot.ToString());
        return stack?.ResolveBlockOrItem(api.World) == true ? stack.GetName() : L("creation-none");
    }
    internal string ActorHandName(CreationSceneObject actor, int hand)
    {
        var item = Session.Project?.Objects.FirstOrDefault(o => o.ParentId == actor.Id && o.ActorHand == hand);
        if (item?.ItemStackData is { Length: > 0 } data)
        {
            try { using var reader = new BinaryReader(new MemoryStream(data, false)); return new ItemStack(reader, api.World).GetName(); }
            catch { /* Fall back to the resource code for unavailable old assets. */ }
        }
        var collectible = item is null || item.ResourceCode.Length == 0 ? null
            : (CollectibleObject?)api.World.GetItem(new AssetLocation(item.ResourceCode)) ?? api.World.GetBlock(new AssetLocation(item.ResourceCode));
        return collectible?.GetHeldItemName(new ItemStack(collectible)) ?? L("creation-none");
    }
    internal void PickActorItem(CreationSceneObject actor, int hand = 0, int clothingSlot = -1)
    {
        if (PerformanceActive || Exporting) throw new InvalidOperationException(L("creation-recording-unavailable"));
        ClosePlayerInventory(); Session.Play(false);
        actorItemCatalog ??= api.World.Collectibles.SelectMany(c =>
        {
            try { return c.GetHandBookStacks(api) ?? new List<ItemStack>(); }
            catch (Exception e) { api.Logger.Debug("VS Director: unavailable catalog item {0}: {1}", c.Code, e.Message); return new List<ItemStack>(); }
        }).Where(s => s?.Collectible?.Code is not null).ToArray();
        var entries = clothingSlot < 0 ? actorItemCatalog
            : actorItemCatalog.Where(s => ItemSlotCharacter.IsDressType(s, (EnumCharacterDressType)clothingSlot)).ToArray();
        nativeAppearanceDialog = new DirectorActorItemDialog(api, L(clothingSlot < 0 ? "creation-actor-item-picker" : "creation-actor-clothing-picker"), entries,
            stack =>
            {
                var current = Session.Project?.Objects.Find(o => o.Id == actor.Id);
                if (current is null) return;
                if (clothingSlot < 0) SetActorHand(current, hand, stack); else SetActorWearable(current, clothingSlot, stack);
            }, e => { Status = e.Message; api.Logger.Error(e); });
        if (!nativeAppearanceDialog.TryOpen()) { ClosePlayerInventory(); throw new InvalidOperationException(L("creation-native-inventory-unavailable")); }
    }
    internal void SetActorSkin(CreationSceneObject actor, string part, string variant)
        => RebuildActorAppearance(actor, entity =>
        {
            var skin = entity.GetBehavior<EntityBehaviorExtraSkinnable>() ?? throw new InvalidOperationException(L("creation-player-appearance-incompatible"));
            if (!skin.AvailableSkinPartsByCode.TryGetValue(part, out var available) || !available.VariantsByCode.ContainsKey(variant))
                throw new InvalidOperationException(L("creation-player-appearance-incompatible"));
            skin.selectSkinPart(part, variant, retesselateShape: false, playVoice: false);
        });
    internal void SetActorWearable(CreationSceneObject actor, int slot, ItemStack? stack)
        => RebuildActorAppearance(actor, entity =>
        {
            var inventory = entity.GetBehavior<EntityBehaviorTexturedClothing>()?.Inventory
                ?? throw new InvalidOperationException(L("creation-player-appearance-incompatible"));
            if (slot < 0 || slot >= inventory.Count || stack is not null && !ItemSlotCharacter.IsDressType(stack, (EnumCharacterDressType)slot))
                throw new InvalidOperationException(L("creation-player-appearance-incompatible"));
            inventory[slot].Itemstack = stack?.Clone();
        });
    private void RebuildActorAppearance(CreationSceneObject actor, Action<EntityAgent> change)
    {
        if (PerformanceActive || Exporting) throw new InvalidOperationException(L("creation-recording-unavailable"));
        var source = actor.ActorSource ?? throw new InvalidOperationException(L("creation-native-unavailable"));
        var properties = api.World.GetEntityType(new AssetLocation(source.EntityCode))?.Clone()
            ?? throw new InvalidOperationException(L("creation-native-unavailable"));
        // Skin composition rewrites Baked.TextureSubId. Never share that mutable
        // metadata with the live player or the entity-type texture dictionary.
        foreach (string key in properties.Client.Textures.Keys.ToArray())
            properties.Client.Textures[key] = CloneAppearanceTexture(properties.Client.Textures[key]);
        // Only visual behaviors run on this unregistered actor. Never initialize a player
        // or its inventory behavior: that would resolve the real player's equipment.
        var configs = properties.Client.BehaviorsAsJsonObj.Where(b => b["code"].AsString() == "extraskinnable").ToList();
        configs.Insert(0, JsonObject.FromJson("{\"code\":\"seraphinventory\"}"));
        properties.Client.BehaviorsAsJsonObj = configs.ToArray();
        var entity = new EntityAgent { EntityId = -Math.Abs(DateTime.UtcNow.Ticks), Code = properties.Code };
        EntityShapeRenderer? renderer = null;
        bool textureAllocated = false;
        try
        {
            entity.WatchedAttributes.FromBytes(ActorAppearance(actor).ToBytes());
            if (entity.WatchedAttributes.GetTreeAttribute("wearablesInv") is { } wearables)
                entity.WatchedAttributes["seraphinventory"] = wearables.Clone();
            entity.Pos.SetPos(api.World.Player.Entity.Pos);
            entity.Initialize(properties, api, 0);
            renderer = new EntityShapeRenderer(entity, api);
            properties.Client.Renderer = renderer;
            foreach (var behavior in properties.Client.Behaviors) behavior.OnEntityLoaded();
            entity.GetTextureSource();
            textureAllocated = true;
            change(entity);
            var shape = properties.Client.LoadedShapeForEntity?.Clone()
                ?? throw new InvalidOperationException(L("creation-native-unavailable"));
            entity.OnTesselation(ref shape, source.ShapeCode);
            if (entity.AnimManager.Animator is null) throw new InvalidOperationException(L("creation-native-unavailable"));
            properties.Client.LoadedShapeForEntity = shape;
            CaptureNativeActor(source.EntityCode, source.SourceEntityId, actor, entity);
        }
        finally
        {
            foreach (var behavior in properties.Client.Behaviors)
                if (behavior is not EntityBehaviorTexturedClothing || textureAllocated)
                    behavior.OnEntityDespawn(new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            renderer?.Dispose();
        }
    }
    private static CompositeTexture CloneAppearanceTexture(CompositeTexture source)
    {
        var copy = source.Clone();
        var visited = new Dictionary<BakedCompositeTexture, BakedCompositeTexture>();
        BakedCompositeTexture CloneBaked(BakedCompositeTexture baked)
        {
            if (visited.TryGetValue(baked, out var existing)) return existing;
            var result = new BakedCompositeTexture { TextureSubId = baked.TextureSubId,
                BakedName = baked.BakedName?.Clone(), TextureFilenames = baked.TextureFilenames?.Select(p => p.Clone()).ToArray(), TilesWidth = baked.TilesWidth };
            visited[baked] = result;
            result.BakedVariants = baked.BakedVariants?.Select(CloneBaked).ToArray();
            result.BakedTiles = baked.BakedTiles?.Select(CloneBaked).ToArray();
            return result;
        }
        if (source.Baked is not null) copy.Baked = CloneBaked(source.Baked);
        return copy;
    }
}
