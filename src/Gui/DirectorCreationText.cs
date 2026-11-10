using Vintagestory.API.Config;

namespace VSDirector.Gui;

internal static class DirectorCreationText
{
    internal static string L(string key, params object[] args) => Lang.Get("vsdirector:" + key, args);
    internal static string Label(string id) => id switch
    {
        "创作" => L("creation-create"),
        "项目" => L("creation-project"),
        "编辑" => L("creation-edit"),
        "创建" => L("creation-create-5"),
        "模型" => L("creation-model"),
        "动画" => L("creation-animation"),
        "窗口" => L("creation-window"),
        "对象" => L("creation-objects"),
        "资源" => L("creation-resources"),
        "预设" => L("creation-presets"),
        "作品" => L("creation-projects-18"),
        _ => id
    };
    internal static string Message(string message)
    {
        const string invalidList = "作品列表无效：";
        if (message.StartsWith(invalidList, StringComparison.Ordinal))
            return L("creation-data-invalid-project-list") + Message(message[invalidList.Length..]);
        return message switch
        {
            "骨骼基础缩放为零，无法变换。" => L("creation-data-the-bone-s-base-scale-is-zero-it-cannot-be-transformed"),
            "骨骼缩放为零，无法变换。" => L("creation-data-the-bone-s-scale-is-zero-it-cannot-be-transformed"),
            "变换矩阵不可分解。" => L("creation-data-the-transform-matrix-cannot-be-decomposed"),
            "变换缩放为零。" => L("creation-data-the-transform-scale-is-zero"),
            "作品数据缺少必要字段。" => L("creation-data-required-project-fields-are-missing"),
            "Unsupported creation project version." => L("creation-data-unsupported-creation-project-version"),
            "Invalid project identity, origin or duration." => L("creation-data-invalid-project-identity-origin-or-duration"),
            "Invalid timeline range." => L("creation-data-invalid-timeline-range"),
            "Invalid output dimensions or frame rate." => L("creation-data-invalid-output-dimensions-or-frame-rate"),
            "Duplicate or empty creation identity." => L("creation-data-duplicate-or-empty-creation-identity"),
            "Duplicate creation object identity." => L("creation-data-duplicate-creation-object-identity"),
            "Non-finite object transform." => L("creation-data-non-finite-object-transform"),
            "Invalid model." => L("creation-data-invalid-model"),
            "Invalid model part." => L("creation-data-invalid-model-part"),
            "Invalid action duration." => L("creation-data-invalid-action-duration"),
            "Missing keyframe list." => L("creation-data-missing-keyframe-list"),
            "Invalid animation keyframe." => L("creation-data-invalid-animation-keyframe"),
            "Invalid animation clip." => L("creation-data-invalid-animation-clip"),
            "Parent does not exist." => L("creation-data-parent-does-not-exist"),
            "Cyclic parent binding." => L("creation-data-cyclic-parent-binding"),
            "对象或骨骼层级不能超过 128 层。" => L("creation-data-object-or-bone-hierarchies-cannot-exceed-128-levels"),
            "模型需要 Vintage Story Shape JSON 对象。" => L("creation-data-the-model-must-contain-a-vintage-story-shape-json-object"),
            "模型缺少 elements。" => L("creation-data-the-model-is-missing-elements"),
            "模型层级超过 128 层。" => L("creation-data-the-model-hierarchy-exceeds-128-levels"),
            "此模型包含混合版本的动画，请先统一为 Vintage Story 动画版本 0 或 1。" => L("creation-data-this-model-mixes-animation-versions-convert-all-animations-to-vintage-story"),
            "素材路径必须在作品目录内。" => L("creation-data-asset-paths-must-stay-inside-the-project-directory"),
            "作品标识与目录不匹配。" => L("creation-data-the-project-id-does-not-match-its-directory"),
            "作品为空。" => L("creation-data-the-project-is-empty"),
            "找不到素材。" => L("creation-data-asset-not-found"),
            "编辑" => L("creation-data-edit"),
            "父对象缩放为零，无法录制表演。" => L("creation-data-the-parent-s-scale-is-zero-performance-cannot-be-recorded"),
            "录制角色表演" => L("creation-data-record-character-performance"),
            "作品列表无效：" => L("creation-data-invalid-project-list"),
            "对象" => L("creation-data-objects"),
            "模型" => L("creation-data-models"),
            "素材" => L("creation-data-assets"),
            "轨道" => L("creation-data-tracks"),
            "动作" => L("creation-data-animations"),
            "剪辑" => L("creation-data-clips"),
            "预设" => L("creation-data-presets"),
            "文件夹" => L("creation-data-folders"),
            "视频输出设置无效。" => L("creation-data-invalid-video-output-settings"),
            "拍摄相机不存在。" => L("creation-data-the-shooting-camera-does-not-exist"),
            "模型部件" => L("creation-data-model-parts"),
            "模型字段无效。" => L("creation-data-invalid-model-fields"),
            "动作映射指向已删除的骨骼。" => L("creation-data-the-body-mapping-references-a-deleted-bone"),
            "部件字段无效。" => L("creation-data-invalid-part-fields"),
            "模型表面应为 JSON 对象。" => L("creation-data-model-faces-must-be-json-objects"),
            "表面 UV 必须包含四个有限数值。" => L("creation-data-face-uv-must-contain-four-finite-numbers"),
            "对象字段无效。" => L("creation-data-invalid-object-fields"),
            "对象引用的模型不存在。" => L("creation-data-the-object-s-model-does-not-exist"),
            "对象引用的素材不存在。" => L("creation-data-the-object-s-asset-does-not-exist"),
            "目标对象不存在。" => L("creation-data-the-target-object-does-not-exist"),
            "姿势指向不存在的部件。" => L("creation-data-the-pose-references-a-missing-part"),
            "挂接骨骼不存在。" => L("creation-data-the-attachment-bone-does-not-exist"),
            "对象外观或粒子参数无效。" => L("creation-data-invalid-object-appearance-or-particle-parameters"),
            "结构方块" => L("creation-data-structure-blocks"),
            "结构方块数据无效。" => L("creation-data-invalid-structure-block-data"),
            "素材字段无效。" => L("creation-data-invalid-asset-fields"),
            "关键帧" => L("creation-data-keyframes"),
            "轨道属性类型无效。" => L("creation-data-invalid-track-channel"),
            "轨道对象或骨骼不存在。" => L("creation-data-the-track-s-object-or-bone-does-not-exist"),
            "动作骨骼不存在。" => L("creation-data-the-animation-bone-does-not-exist"),
            "关键帧插值类型无效。" => L("creation-data-invalid-keyframe-interpolation"),
            "动作模型不存在。" => L("creation-data-the-animation-model-does-not-exist"),
            "动作轨道" => L("creation-data-animation-tracks"),
            "剪辑引用无效。" => L("creation-data-invalid-clip-reference"),
            "动作与演员的模型不匹配。" => L("creation-data-the-animation-and-actor-use-different-models"),
            "预设数据无效。" => L("creation-data-invalid-preset-data"),
            "预设外观数据无效。" => L("creation-data-invalid-preset-appearance"),
            _ => message
        };
    }
}
