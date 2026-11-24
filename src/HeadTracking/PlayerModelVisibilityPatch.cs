// Adapted from VS-6DOF (DF; bjfishman / TGU-HansJack), CC0-1.0.
// See licenses/VS-6DOF-CC0.txt and licenses/THIRD-PARTY.txt.
#nullable disable
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace VSDirector.HeadTracking
{
    [HarmonyPatch(typeof(EntityPlayerShapeRenderer), nameof(EntityPlayerShapeRenderer.Tesselate))]
    internal static class PlayerModelVisibilityPatch
    {
        private static readonly PropertyInfo IsSelfProperty = AccessTools.Property(typeof(EntityPlayerShapeRenderer), "IsSelf");
        private static readonly FieldInfo LoadedField = AccessTools.Field(typeof(EntityShapeRenderer), "loaded");
        private static readonly FieldInfo ShapeFreshField = AccessTools.Field(typeof(EntityShapeRenderer), "shapeFresh");
        private static readonly FieldInfo EntityField = AccessTools.Field(typeof(EntityRenderer), "entity");
        private static readonly FieldInfo FirstPersonMeshRefField = AccessTools.Field(typeof(EntityPlayerShapeRenderer), "firstPersonMeshRef");
        private static readonly FieldInfo ThirdPersonMeshRefField = AccessTools.Field(typeof(EntityPlayerShapeRenderer), "thirdPersonMeshRef");
        private static readonly FieldInfo MeshRefOpaqueField = AccessTools.Field(typeof(EntityShapeRenderer), "meshRefOpaque");
        private static readonly MethodInfo TesselateMethod = AccessTools.Method(typeof(EntityPlayerShapeRenderer), "Tesselate");

        private static EntityPlayerShapeRenderer lastSelfRenderer;

        [HarmonyPrefix]
        public static bool Prefix(EntityPlayerShapeRenderer __instance)
        {
            var config = VsdofModSystem.Config;
            if (config == null || !config.DisablePlayerModel || VsdofModSystem.ExternalMod)
            {
                return true;
            }

            var capi = VsdofModSystem.Capi;
            if (capi == null)
            {
                return true;
            }

            if (!TryGetIsSelf(__instance, capi, out bool isSelf) || !isSelf)
            {
                return true;
            }

            lastSelfRenderer = __instance;

            if (!TryGetLoaded(__instance, out bool loaded) || !loaded)
            {
                return false;
            }

            SetShapeFresh(__instance, true);
            __instance.TesselateShape(meshData => DelegateAction(__instance, meshData, capi));
            return false;
        }

        internal static void RequestRetessellate()
        {
            var capi = VsdofModSystem.Capi;
            if (capi?.Event == null)
            {
                return;
            }

            capi.Event.EnqueueMainThreadTask(() =>
            {
                var renderer = TryFindSelfRenderer(capi);
                if (renderer == null)
                {
                    return;
                }

                try
                {
                    if (TesselateMethod != null)
                    {
                        TesselateMethod.Invoke(renderer, null);
                    }
                    else
                    {
                        renderer.Tesselate();
                    }
                }
                catch
                {
                    // Ignore failures; will retry on next toggle or natural tesselation.
                }
            }, "vsdof-retessellate");
        }

        private static void DelegateAction(EntityPlayerShapeRenderer __instance, MeshData meshData, ICoreClientAPI capi)
        {
            DisposeMeshes(__instance);

            if (capi.IsShuttingDown || meshData.VerticesCount <= 0)
            {
                return;
            }

            ThirdPersonMeshRefField?.SetValue(__instance, capi.Render.UploadMultiTextureMesh(meshData));

            MeshData meshData2 = meshData.EmptyClone();
            if (VsdofModSystem.Config?.DisablePlayerModel ?? false)
            {
                FirstPersonMeshRefField?.SetValue(__instance, capi.Render.UploadMultiTextureMesh(meshData2));
                return;
            }

            var entity = EntityField?.GetValue(__instance) as Entity;
            var animator = entity?.AnimManager?.Animator;

            if (animator == null)
            {
                FirstPersonMeshRefField?.SetValue(__instance, capi.Render.UploadMultiTextureMesh(meshData2));
                return;
            }

            if (capi.Settings.Bool["immersiveFpMode"])
            {
                HashSet<int> skipJointIds = new HashSet<int>();
                var pose = animator.GetPosebyName("Neck");
                if (pose != null)
                {
                    LoadJointIdsRecursive(pose, skipJointIds);
                    meshData2.AddMeshData(meshData, i => !skipJointIds.Contains(meshData.CustomInts.Values[i * 4]));
                }
            }
            else
            {
                HashSet<int> includeJointIds = new HashSet<int>();
                var pose = animator.GetPosebyName("ItemAnchor");
                if (pose != null)
                {
                    LoadJointIdsRecursive(pose, includeJointIds);
                    meshData2.AddMeshData(meshData, i => includeJointIds.Contains(meshData.CustomInts.Values[i * 4]));
                }
            }

            FirstPersonMeshRefField?.SetValue(__instance, capi.Render.UploadMultiTextureMesh(meshData2));
        }

        private static void DisposeMeshes(EntityPlayerShapeRenderer __instance)
        {
            if (FirstPersonMeshRefField?.GetValue(__instance) is MultiTextureMeshRef firstPersonMeshRef)
            {
                firstPersonMeshRef.Dispose();
                FirstPersonMeshRefField.SetValue(__instance, null);
            }

            if (ThirdPersonMeshRefField?.GetValue(__instance) is MultiTextureMeshRef thirdPersonMeshRef)
            {
                thirdPersonMeshRef.Dispose();
                ThirdPersonMeshRefField.SetValue(__instance, null);
            }

            MeshRefOpaqueField?.SetValue(__instance, null);
        }

        private static void LoadJointIdsRecursive(ElementPose elementPose, HashSet<int> outList)
        {
            outList.Add(elementPose.ForElement.JointId);
            foreach (ElementPose childElementPose in elementPose.ChildElementPoses)
            {
                LoadJointIdsRecursive(childElementPose, outList);
            }
        }

        private static bool TryGetIsSelf(EntityPlayerShapeRenderer __instance, ICoreClientAPI capi, out bool isSelf)
        {
            isSelf = false;
            var property = IsSelfProperty ?? AccessTools.Property(__instance.GetType(), "IsSelf");
            if (property != null)
            {
                if (property.GetValue(__instance) is bool value)
                {
                    isSelf = value;
                    return true;
                }
            }

            var entity = EntityField?.GetValue(__instance) as Entity;
            var localPlayer = capi?.World?.Player?.Entity;
            if (entity != null && localPlayer != null)
            {
                isSelf = ReferenceEquals(entity, localPlayer);
                return true;
            }

            return false;
        }

        private static bool TryGetLoaded(EntityPlayerShapeRenderer __instance, out bool loaded)
        {
            loaded = false;
            if (LoadedField == null)
            {
                return false;
            }

            if (LoadedField.GetValue(__instance) is bool value)
            {
                loaded = value;
                return true;
            }

            return false;
        }

        private static void SetShapeFresh(EntityPlayerShapeRenderer __instance, bool value)
        {
            ShapeFreshField?.SetValue(__instance, value);
        }

        private static EntityPlayerShapeRenderer TryFindSelfRenderer(ICoreClientAPI capi)
        {
            var playerEntity = capi?.World?.Player?.Entity;
            if (playerEntity == null)
            {
                return null;
            }

            if (playerEntity.Properties?.Client?.Renderer is EntityPlayerShapeRenderer renderer) return renderer;
            if (lastSelfRenderer != null && ReferenceEquals(EntityField?.GetValue(lastSelfRenderer), playerEntity)) return lastSelfRenderer;

            var type = playerEntity.GetType();
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (typeof(EntityPlayerShapeRenderer).IsAssignableFrom(field.FieldType))
                {
                    return field.GetValue(playerEntity) as EntityPlayerShapeRenderer;
                }
            }

            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!typeof(EntityPlayerShapeRenderer).IsAssignableFrom(prop.PropertyType))
                {
                    continue;
                }

                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                return prop.GetValue(playerEntity) as EntityPlayerShapeRenderer;
            }

            return null;
        }
    }
}
