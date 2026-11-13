using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

// Uses the game's slot renderer/tooltips with a private selection-only inventory.
// No item transfers, player inventory access or inventory packets are performed.
internal sealed class DirectorActorItemDialog : GuiDialog
{
    private sealed class PickerGrid : GuiElementItemSlotGrid
    {
        internal PickerGrid(ICoreClientAPI api, PickerInventory inventory, int columns, ElementBounds bounds)
            : base(api, inventory, _ => { }, columns, null, bounds) { }
        public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
        {
            if (!Bounds.ParentBounds.PointInside(args.X, args.Y)) return;
            for (int i = 0; i < SlotBounds.Length && i < renderedSlots.Count; i++)
                if (SlotBounds[i].PointInside(args.X, args.Y))
                {
                    SlotClick(api, renderedSlots.GetKeyAtIndex(i), args.Button, false, false, false);
                    args.Handled = true; return;
                }
        }
        // Native grids pause cursor inventory updates while distributing real
        // stacks. A catalog must never enter that drag/transfer lifecycle.
        public override void OnMouseUp(ICoreClientAPI api, MouseEvent args) { }
        public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args) { }
        public override void SlotClick(ICoreClientAPI api, int slotId, EnumMouseButton button, bool shift, bool ctrl, bool alt)
        {
            var op = new ItemStackMoveOperation(api.World, button, 0, EnumMergePriority.AutoMerge);
            inventory.ActivateSlot(slotId, null!, ref op);
        }
    }
    private sealed class PickerInventory : InventoryGeneric
    {
        internal Action<ItemStack>? Pick;
        internal PickerInventory(ICoreClientAPI api, int size = 48) : base(size, "vsdirector-actorpicker-" + Guid.NewGuid().ToString("N"), api) { }
        public override object ActivateSlot(int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op)
        {
            if (op.WheelDir == 0 && op.MouseButton == EnumMouseButton.Left
                && slotId >= 0 && slotId < Count && this[slotId].Itemstack is { } stack) Pick?.Invoke(stack.Clone());
            op.MovedQuantity = 0;
            return null!;
        }
    }
    private readonly PickerInventory inventory;
    private readonly IReadOnlyList<ItemStack> entries;
    private readonly Action<ItemStack> picked;
    private readonly Action<Exception> failed;
    private readonly string title;
    private readonly int columns;
    private bool selectionQueued;
    private string search = "";
    private ItemStack[] filtered = [];
    private int page;
    public override string ToggleKeyCombinationCode => null!;
    internal DirectorActorItemDialog(ICoreClientAPI api, string title, IReadOnlyList<ItemStack> entries,
        Action<ItemStack> picked, Action<Exception> failed) : base(api)
    {
        this.title = title; this.entries = entries; this.picked = picked; this.failed = failed;
        double pitch = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding;
        columns = Math.Clamp((int)((api.Render.FrameWidth / RuntimeEnv.GUIScale - 100) / pitch), 4, 8);
        int rows = Math.Clamp((int)((api.Render.FrameHeight / RuntimeEnv.GUIScale - 210) / pitch), 3, 6);
        inventory = new(api, columns * rows);
        inventory.Pick = stack =>
        {
            if (selectionQueued) return;
            selectionQueued = true;
            // Leave the slot-grid event stack before closing or replacing composers.
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (!IsOpened()) return;
                TryClose();
                try { picked(stack); } catch (Exception e) { failed(e); }
            }, "vsdirector-actor-item");
        };
        Compose(); Filter();
    }
    private void Compose()
    {
        double pitch = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding;
        double width = columns * pitch - GuiElementItemSlotGridBase.unscaledSlotPadding;
        double height = inventory.Count / columns * pitch - GuiElementItemSlotGridBase.unscaledSlotPadding;
        double footer = 80 + height + 12;
        var background = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        SingleComposer = capi.Gui.CreateCompo("vsdirector-actor-items", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar(title, () => TryClose())
            .BeginChildElements(background)
            .AddTextInput(ElementBounds.Fixed(0, 35, width, 30), text => { search = text; page = 0; Filter(); }, key: "search")
            .AddInteractiveElement(new PickerGrid(capi, inventory, columns, ElementBounds.Fixed(0, 80, width, height)), "items")
            .AddSmallButton("◀", () => { if (page > 0) page--; Refresh(); return true; }, ElementBounds.Fixed(0, footer, 40, 28))
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, footer, width - 96, 28), "page")
            .AddSmallButton("▶", () => { if ((page + 1) * inventory.Count < filtered.Length) page++; Refresh(); return true; }, ElementBounds.Fixed(width - 40, footer, 40, 28))
            .EndChildElements().Compose();
        SingleComposer.GetTextInput("search").SetPlaceHolderText(L("creation-search-names"));
        GuiElementItemSlotGridBase.UpdateLastSlotGridFlag(SingleComposer);
    }
    private void Filter()
    {
        filtered = entries.Where(s => search.Length == 0 || s.GetName().Contains(search, StringComparison.OrdinalIgnoreCase)
            || s.Collectible.Code.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        Refresh();
    }
    private void Refresh()
    {
        for (int i = 0; i < inventory.Count; i++)
        {
            inventory[i].Itemstack = filtered.ElementAtOrDefault(page * inventory.Count + i)?.Clone();
            inventory.MarkSlotDirty(i);
        }
        SingleComposer.GetDynamicText("page").SetNewText($"{page + 1} / {Math.Max(1, (filtered.Length + inventory.Count - 1) / inventory.Count)}   ·   {filtered.Length}");
    }
}
