using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using VSDirector;
using VSDirector.Data;

internal static class DirectorActorEquipmentChecks
{
    internal static void Run()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        var runtimeType = assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime", true)!;
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var item = new Item { ItemId = 42, Code = new AssetLocation("game:equipment-probe") };
        var stack = new ItemStack(item, 3); stack.Attributes.SetString("variant", "preserve-me");
        var pickerType = assembly.GetType("VSDirector.Gui.DirectorActorItemDialog", true)!
            .GetNestedType("PickerInventory", BindingFlags.NonPublic)!;
        var inventory = (InventoryBase)Activator.CreateInstance(pickerType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object?[] { null, 48 }, null)!;
        inventory[0].Itemstack = stack;
        var cursor = new DummySlot(new ItemStack(item, 8));
        ItemStack? selected = null; int selections = 0;
        AccessTools.Field(pickerType, "Pick").SetValue(inventory, (Action<ItemStack>)(s => { selected = s; selections++; }));
        var op = new ItemStackMoveOperation(null!, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 1);
        object packet = inventory.ActivateSlot(0, cursor, ref op);
        Check(packet is null && op.MovedQuantity == 0 && cursor.StackSize == 8 && inventory[0].StackSize == 3,
            "Actor picker moved live items or emitted an inventory packet");
        Check(selected is not null && selected.Attributes.GetString("variant") == "preserve-me" && !ReferenceEquals(selected, stack),
            "Actor picker lost stack attributes or returned a shared stack");
        selected!.Attributes.SetString("variant", "changed");
        Check(stack.Attributes.GetString("variant") == "preserve-me", "Actor selection mutated catalog data");
        op.WheelDir = 1; inventory.ActivateSlot(0, cursor, ref op);
        Check(selections == 1, "Scrolling a catalog item changed the actor equipment");
        var gridType = assembly.GetType("VSDirector.Gui.DirectorActorItemDialog", true)!
            .GetNestedType("PickerGrid", BindingFlags.NonPublic)!;
        var grid = (GuiElementItemSlotGrid)RuntimeHelpers.GetUninitializedObject(gridType);
        AccessTools.Field(typeof(GuiElementItemSlotGridBase), "inventory").SetValue(grid, inventory);
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name == "get_World" ? null
            : throw new Exception("Catalog grid touched live API: " + method.Name));
        grid.SlotClick(api, 0, EnumMouseButton.Left, true, true, false);
        grid.OnMouseUp(api, null!);
        Check(selections == 2 && cursor.StackSize == 8, "Catalog grid bypassed isolated selection");

        var baseTexture = new CompositeTexture(new AssetLocation("game:skin"))
        { Baked = new BakedCompositeTexture { TextureSubId = 17, BakedName = new AssetLocation("game:skin"), TextureFilenames = [new AssetLocation("game:skin")] } };
        baseTexture.Baked.BakedVariants = [baseTexture.Baked];
        var copied = (CompositeTexture)AccessTools.Method(runtimeType, "CloneAppearanceTexture").Invoke(null, [baseTexture])!;
        copied.Baked.TextureSubId = 99; copied.Baked.BakedName.Path = "private-skin";
        Check(baseTexture.Baked.TextureSubId == 17 && baseTexture.Baked.BakedName.Path == "skin"
            && ReferenceEquals(copied.Baked.BakedVariants[0], copied.Baked), "Skin editing changed live atlas metadata or broke variant references");

        string directory = Path.Combine(Path.GetTempPath(), "director-equipment-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new DirectorCreationSession(new(directory));
            var hand = new CreationPart { Name = "RightHand", Group = true };
            var model = new CreationModel { Parts = [hand] };
            var skin = new TreeAttribute(); skin.SetString("skin", "chosen");
            var actor = new CreationSceneObject { ModelId = model.Id, ActorSource = new() { EntityCode = "game:player", AppearanceData = skin.ToBytes() } };
            var other = new CreationSceneObject { ModelId = model.Id };
            var project = new DirectorCreationProject { Models = [model], Objects = [actor, other] };
            session.Open(project);
            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            AccessTools.Field(runtimeType, "<Session>k__BackingField").SetValue(runtime, session);
            var setHand = AccessTools.Method(runtimeType, "SetActorHand");
            setHand.Invoke(runtime, [actor, 1, stack]);
            var attachment = session.Project!.Objects.Single(o => o.ParentId == actor.Id);
            Check(attachment.ParentPartId == hand.Id && !attachment.UseRecordedHandItem && attachment.ItemStackData.SequenceEqual(stack.ToBytes())
                && !session.Project.Objects.Any(o => o.ParentId == other.Id), "Equipment did not attach exclusively to the selected actor");
            session.Undo(); Check(session.Project!.Objects.Count == 2, "Undo retained added hand equipment");
            session.Redo();
            session.Save(); session.Open(session.Store.Load(project.Id));
            actor = session.Project!.Objects.Single(o => o.Id == actor.Id);
            Check(actor.ActorSource!.AppearanceData.SequenceEqual(skin.ToBytes())
                && session.Project.Objects.Single(o => o.ParentId == actor.Id).ItemStackData.SequenceEqual(stack.ToBytes()),
                "Actor appearance/equipment was lost on save and reopen");
            setHand.Invoke(runtime, [actor, 1, null]);
            attachment = session.Project.Objects.Single(o => o.ParentId == actor.Id);
            Check(attachment.ResourceCode.Length == 0 && attachment.ItemStackData.Length == 0 && !attachment.UseRecordedHandItem,
                "Clearing a hand restored recorded equipment");
            session.Undo();
            Check(session.Project!.Objects.Single(o => o.ParentId == actor.Id).ItemStackData.SequenceEqual(stack.ToBytes()),
                "Undo did not restore full item stack attributes");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS actor-only catalog selection, scroll/cursor isolation, full-stack save/reopen/undo, hand clearing and private skin atlas metadata");
    }
}
