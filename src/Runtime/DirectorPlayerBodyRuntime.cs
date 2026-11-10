using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Runtime;

// Independent physical dimensions, synchronized by the server. Never change
// the shared EntityProperties or derive collision dimensions from mesh scale.
internal static class DirectorPlayerBodyRuntime
{
    private const string Key = "vsdirector-player-body";
    private sealed record Original(Cuboidf Collision, Cuboidf OriginCollision, Cuboidf Selection, Cuboidf OriginSelection, Vec3d Eye);
    private static readonly ConditionalWeakTable<EntityPlayer, Original> originals = new();

    internal static void InstallServer(Harmony harmony)
    {
        harmony.CreateClassProcessor(typeof(DirectorPlayerBodyEyePatch)).Patch();
        harmony.CreateClassProcessor(typeof(DirectorPlayerBodyStandPatch)).Patch();
    }
    internal static void Set(EntityPlayer player, CreationPlayerBody body)
    {
        if (!body.Enabled || !body.Valid()) { Clear(player); return; }
        var tree = new TreeAttribute();
        tree.SetDouble("width", body.Width); tree.SetDouble("height", body.Height);
        tree.SetDouble("sneak", body.SneakHeight); tree.SetDouble("eye", body.EyeHeight);
        player.WatchedAttributes[Key] = tree; player.WatchedAttributes.MarkPathDirty(Key);
        Prepare(player); Apply(player);
    }
    internal static void Clear(EntityPlayer player)
    {
        player.WatchedAttributes.RemoveAttribute(Key); player.WatchedAttributes.MarkPathDirty(Key);
        Restore(player);
    }
    internal static CreationPlayerBody? Settings(EntityPlayer player)
    {
        if (player.WatchedAttributes.GetTreeAttribute(Key) is not { } tree) return null;
        var body = new CreationPlayerBody { Enabled = true, Width = tree.GetDouble("width"), Height = tree.GetDouble("height"), SneakHeight = tree.GetDouble("sneak"), EyeHeight = tree.GetDouble("eye") };
        return body.Valid() ? body : null;
    }
    internal static void Prepare(EntityPlayer player)
    {
        if (Settings(player) is null) { Restore(player); return; }
        if (player.CollisionBox is null || player.SelectionBox is null || player.OriginCollisionBox is null || player.OriginSelectionBox is null) return;
        originals.GetValue(player, p => new(p.CollisionBox.Clone(), p.OriginCollisionBox.Clone(), p.SelectionBox.Clone(), p.OriginSelectionBox.Clone(), p.LocalEyePos.Clone()));
    }
    private static void Restore(EntityPlayer player)
    {
        if (!originals.TryGetValue(player, out var old)) return;
        player.CollisionBox = old.Collision.Clone(); player.OriginCollisionBox = old.OriginCollision.Clone();
        player.SelectionBox = old.Selection.Clone(); player.OriginSelectionBox = old.OriginSelection.Clone();
        player.LocalEyePos.Set(old.Eye); originals.Remove(player);
    }
    internal static bool CanStand(EntityPlayer player, CreationPlayerBody body)
    {
        if (player.World?.CollisionTester is not { } tester) return true;
        float half = (float)body.Width / 2;
        var box = new Cuboidf(-half, 0, -half, half, (float)body.Height, half);
        return !tester.IsColliding(player.World.BlockAccessor, box, player.Pos.XYZ, false);
    }
    internal static bool TryCanStand(EntityPlayer player, out bool result)
    {
        var body = Settings(player); result = true;
        if (body is null || !player.Alive || player.MountedOn is not null) return false;
        result = CanStand(player, body); return true;
    }
    internal static void Apply(EntityPlayer player)
    {
        var body = Settings(player);
        if (body is null) { Restore(player); return; }
        if (!player.Alive || player.MountedOn is not null || !originals.TryGetValue(player, out _)) return;
        var controls = player.ServerControls;
        bool stand = CanStand(player, body);
        player.PrevFrameCanStandUp = !controls.Sneak && stand;
        bool crouched = (controls.Sneak || !stand) && !controls.IsClimbing && !controls.IsFlying;
        double height = controls.FloorSitting ? body.Height * .55 : crouched ? body.SneakHeight : body.Height;
        double eye = body.EyeHeight * (controls.FloorSitting ? .5 : crouched ? body.SneakHeight / body.Height : 1);
        float half = (float)body.Width / 2;
        void Size(Cuboidf box) { box.X1 = box.Z1 = -half; box.X2 = box.Z2 = half; box.Y1 = 0; box.Y2 = (float)height; }
        Size(player.CollisionBox); Size(player.OriginCollisionBox); Size(player.SelectionBox); Size(player.OriginSelectionBox);
        player.LocalEyePos.Set(0, eye, 0);
    }
}

[HarmonyPatch(typeof(EntityPlayer), "updateEyeHeight")]
internal static class DirectorPlayerBodyEyePatch
{
    [HarmonyPrefix] private static void Prefix(EntityPlayer __instance) => DirectorPlayerBodyRuntime.Prepare(__instance);
    [HarmonyPostfix] private static void Postfix(EntityPlayer __instance) => DirectorPlayerBodyRuntime.Apply(__instance);
}

[HarmonyPatch(typeof(EntityPlayer), "canStandUp")]
internal static class DirectorPlayerBodyStandPatch
{
    [HarmonyPrefix] private static bool Prefix(EntityPlayer __instance, ref bool __result)
    {
        if (!DirectorPlayerBodyRuntime.TryCanStand(__instance, out bool result)) return true;
        __result = result; return false;
    }
}
