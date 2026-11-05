using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VSDirector.Rendering;

[HarmonyPatch(typeof(ClientMain), nameof(ClientMain.RenderToDefaultFramebuffer))]
internal static class DirectorWorkspaceInterfaceVisibilityPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var overlayFlag = AccessTools.Field(typeof(ClientMain), nameof(ClientMain.ShouldRender2DOverlays));
        var shouldRender = AccessTools.Method(typeof(DirectorWorkspaceViewportRuntime), nameof(DirectorWorkspaceViewportRuntime.ShouldRenderInterface));
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.LoadsField(overlayFlag))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = shouldRender;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("VS Director: the game's GUI visibility gate has changed.");
    }
}

[HarmonyPatch(typeof(GuiManager), nameof(GuiManager.OnRenderFrameGUI))]
internal static class DirectorWorkspaceGameHudVisibilityPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(GuiDialog), nameof(GuiDialog.ShouldReceiveRenderEvents));
        var shouldRender = AccessTools.Method(typeof(DirectorWorkspaceViewportRuntime), nameof(DirectorWorkspaceViewportRuntime.ShouldRenderDialog));
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = shouldRender;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("VS Director: the game's dialog render loop has changed.");
    }
}

[HarmonyPatch(typeof(SystemRenderPlayerAimAcc), "OnRenderFrame2DOverlay")]
internal static class DirectorWorkspaceAimAccuracyVisibilityPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !DirectorWorkspaceViewportRuntime.HideGameHud;
}
