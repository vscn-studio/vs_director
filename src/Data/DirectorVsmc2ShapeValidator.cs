using System.Text.Json;

namespace VSDirector.Data;

public enum DirectorShapeDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record DirectorShapeDiagnostic(
    DirectorShapeDiagnosticSeverity Severity,
    string Code,
    string Message,
    string FilePath = "",
    string ElementName = "",
    string AnimationCode = "",
    int? KeyFrame = null);

public sealed class DirectorVsmc2ShapeValidationResult
{
    public int ElementCount { get; init; }

    public int TextureCount { get; init; }

    public IReadOnlyList<string> AnimationCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<DirectorShapeDiagnostic> Diagnostics { get; init; }
        = Array.Empty<DirectorShapeDiagnostic>();

    public bool IsValid => Diagnostics.All(diagnostic => diagnostic.Severity != DirectorShapeDiagnosticSeverity.Error);
}

public static class DirectorVsmc2ShapeValidator
{
    public static DirectorVsmc2ShapeValidationResult Validate(string json, string filePath = "")
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return Validate(document.RootElement, filePath);
    }

    public static DirectorVsmc2ShapeValidationResult Validate(JsonElement root, string filePath = "")
    {
        List<DirectorShapeDiagnostic> diagnostics = new();
        Dictionary<string, ElementState> elements = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> textures = new(StringComparer.OrdinalIgnoreCase);
        List<string> animations = new();

        if (root.ValueKind != JsonValueKind.Object)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.root.object",
                "Shape root must be a JSON object.", filePath);
            return Result(elements.Count, textures.Count, animations, diagnostics);
        }

        ValidateTextures(root, textures, diagnostics, filePath);
        if (!DirectorVsmc2ShapeDocument.TryGetProperty(root, "elements", out JsonElement rootElements))
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.elements.missing",
                "Shape root is missing the elements array.", filePath);
        }
        else if (rootElements.ValueKind != JsonValueKind.Array)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.elements.array",
                "Shape elements must be an array.", filePath);
        }
        else
        {
            int index = 0;
            foreach (JsonElement element in rootElements.EnumerateArray())
            {
                ValidateElement(element, $"elements[{index}]", null, elements, textures, diagnostics, filePath);
                index++;
            }
        }

        ValidateStepParents(elements, diagnostics, filePath);
        ValidateAnimations(root, elements, animations, diagnostics, filePath);
        if (elements.Count > 0 && diagnostics.All(item => item.Severity != DirectorShapeDiagnosticSeverity.Error))
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Info, "shape.valid",
                $"Shape contains {elements.Count} elements, {textures.Count} textures, and {animations.Count} animations.", filePath);
        }
        return Result(elements.Count, textures.Count, animations, diagnostics);
    }

    private static void ValidateTextures(
        JsonElement root,
        HashSet<string> textures,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath)
    {
        if (DirectorVsmc2ShapeDocument.TryGetProperty(root, "textures", out JsonElement textureObject))
        {
            if (textureObject.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.textures.object",
                    "Shape textures must be an object.", filePath);
            }
            else
            {
                foreach (JsonProperty texture in textureObject.EnumerateObject())
                {
                    if (!textures.Add(texture.Name))
                    {
                        Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.texture.duplicate",
                            $"Texture code '{texture.Name}' is duplicated.", filePath);
                    }
                    if (texture.Value.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(texture.Value.GetString()))
                    {
                        Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.texture.location",
                            $"Texture '{texture.Name}' must have a resource location.", filePath);
                    }
                }
            }
        }

        ValidatePositiveInteger(root, "textureWidth", diagnostics, filePath);
        ValidatePositiveInteger(root, "textureHeight", diagnostics, filePath);
        if (DirectorVsmc2ShapeDocument.TryGetProperty(root, "textureSizes", out JsonElement textureSizes))
        {
            if (textureSizes.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.textureSizes.object",
                    "Shape textureSizes must be an object.", filePath);
            }
            else
            {
                foreach (JsonProperty size in textureSizes.EnumerateObject())
                {
                    ValidateNumberArray(size.Value, 2, $"textureSizes.{size.Name}", diagnostics, filePath);
                }
            }
        }
    }

    private static void ValidateElement(
        JsonElement element,
        string jsonPath,
        string? parentName,
        Dictionary<string, ElementState> elements,
        HashSet<string> textures,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.element.object",
                $"Element at {jsonPath} must be an object.", filePath);
            return;
        }

        string name = ReadString(element, "name").Trim();
        string diagnosticName = name.Length == 0 ? jsonPath : name;
        if (name.Length == 0)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.element.name.missing",
                $"Element at {jsonPath} has no name.", filePath, diagnosticName);
        }
        else if (!elements.TryAdd(name, new ElementState(name, parentName, ReadString(element, "stepParentName").Trim())))
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.element.name.duplicate",
                $"Element name '{name}' is duplicated.", filePath, name);
        }

        ValidateRequiredNumberArray(element, "from", 3, diagnostics, filePath, diagnosticName);
        ValidateRequiredNumberArray(element, "to", 3, diagnostics, filePath, diagnosticName);
        ValidateOptionalNumberArray(element, "rotationOrigin", 3, diagnostics, filePath, diagnosticName);
        ValidateOptionalNumberArray(element, "uv", 2, diagnostics, filePath, diagnosticName);
        ValidateScale(element, "scaleX", diagnostics, filePath, diagnosticName);
        ValidateScale(element, "scaleY", diagnostics, filePath, diagnosticName);
        ValidateScale(element, "scaleZ", diagnostics, filePath, diagnosticName);
        ValidateFaces(element, textures, diagnostics, filePath, diagnosticName);
        ValidateAttachmentPoints(element, diagnostics, filePath, diagnosticName);

        if (!DirectorVsmc2ShapeDocument.TryGetProperty(element, "children", out JsonElement children))
        {
            return;
        }
        if (children.ValueKind != JsonValueKind.Array)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.children.array",
                $"Element '{diagnosticName}' children must be an array.", filePath, diagnosticName);
            return;
        }
        int childIndex = 0;
        foreach (JsonElement child in children.EnumerateArray())
        {
            ValidateElement(child, $"{jsonPath}.children[{childIndex}]", name.Length == 0 ? parentName : name,
                elements, textures, diagnostics, filePath);
            childIndex++;
        }
    }

    private static void ValidateFaces(
        JsonElement element,
        HashSet<string> textures,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath,
        string elementName)
    {
        if (!DirectorVsmc2ShapeDocument.TryGetProperty(element, "faces", out JsonElement faces))
        {
            return;
        }
        if (faces.ValueKind != JsonValueKind.Object)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.faces.object",
                $"Element '{elementName}' faces must be an object.", filePath, elementName);
            return;
        }
        foreach (JsonProperty face in faces.EnumerateObject())
        {
            if (face.Value.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.face.object",
                    $"Face '{face.Name}' on element '{elementName}' must be an object.", filePath, elementName);
                continue;
            }
            ValidateOptionalNumberArray(face.Value, "uv", 4, diagnostics, filePath, elementName);
            string texture = ReadString(face.Value, "texture").Trim();
            if (texture.StartsWith('#'))
            {
                string textureCode = texture[1..];
                if (textureCode.Length == 0 || !textures.Contains(textureCode))
                {
                    Add(diagnostics, DirectorShapeDiagnosticSeverity.Warning, "shape.face.texture.missing",
                        $"Face '{face.Name}' on element '{elementName}' references missing texture '{texture}'.",
                        filePath, elementName);
                }
            }
            else if (texture.Length == 0)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Warning, "shape.face.texture.empty",
                    $"Face '{face.Name}' on element '{elementName}' has no texture reference.", filePath, elementName);
            }
        }
    }

    private static void ValidateAttachmentPoints(
        JsonElement element,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath,
        string elementName)
    {
        if (!DirectorVsmc2ShapeDocument.TryGetProperty(element, "attachmentPoints", out JsonElement points))
        {
            return;
        }
        if (points.ValueKind != JsonValueKind.Array)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.attachments.array",
                $"Element '{elementName}' attachmentPoints must be an array.", filePath, elementName);
            return;
        }
        HashSet<string> codes = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.attachment.object",
                    $"Element '{elementName}' contains a non-object attachment point.", filePath, elementName);
                continue;
            }
            string code = ReadString(point, "code").Trim();
            if (code.Length == 0)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.attachment.code.missing",
                    $"Element '{elementName}' contains an attachment point without a code.", filePath, elementName);
            }
            else if (!codes.Add(code))
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.attachment.code.duplicate",
                    $"Attachment point '{code}' is duplicated on element '{elementName}'.", filePath, elementName);
            }
            foreach (string property in new[] { "posX", "posY", "posZ", "rotationX", "rotationY", "rotationZ" })
            {
                ValidateOptionalFiniteNumber(point, property, diagnostics, filePath, elementName);
            }
        }
    }

    private static void ValidateStepParents(
        Dictionary<string, ElementState> elements,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath)
    {
        foreach (ElementState element in elements.Values)
        {
            if (element.StepParentName.Length == 0)
            {
                continue;
            }
            if (!elements.ContainsKey(element.StepParentName))
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.stepParent.missing",
                    $"Element '{element.Name}' references missing step parent '{element.StepParentName}'.",
                    filePath, element.Name);
                continue;
            }
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { element.Name };
            string current = element.StepParentName;
            while (current.Length > 0 && elements.TryGetValue(current, out ElementState? parent))
            {
                if (!visited.Add(current))
                {
                    Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.stepParent.cycle",
                        $"Element '{element.Name}' participates in a step-parent cycle.", filePath, element.Name);
                    break;
                }
                current = parent.StepParentName;
            }
        }
    }

    private static void ValidateAnimations(
        JsonElement root,
        Dictionary<string, ElementState> elements,
        List<string> animations,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath)
    {
        if (!DirectorVsmc2ShapeDocument.TryGetProperty(root, "animations", out JsonElement animationArray))
        {
            return;
        }
        if (animationArray.ValueKind != JsonValueKind.Array)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.animations.array",
                "Shape animations must be an array.", filePath);
            return;
        }
        HashSet<string> codes = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement animation in animationArray.EnumerateArray())
        {
            if (animation.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.animation.object",
                    "Animation entry must be an object.", filePath);
                continue;
            }
            string code = ReadString(animation, "code").Trim();
            if (code.Length == 0)
            {
                code = ReadString(animation, "name").Trim();
            }
            if (code.Length == 0)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.animation.code.missing",
                    "Animation has neither a code nor a name.", filePath);
            }
            else if (!codes.Add(code))
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Warning, "shape.animation.code.duplicate",
                    $"Animation code '{code}' is duplicated.", filePath, animationCode: code);
            }
            else
            {
                animations.Add(code);
            }

            int quantityFrames = ReadInteger(animation, "quantityframes", 0);
            if (quantityFrames <= 0)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.animation.frames.invalid",
                    $"Animation '{code}' quantityframes must be greater than zero.", filePath, animationCode: code);
            }
            if (!DirectorVsmc2ShapeDocument.TryGetProperty(animation, "keyframes", out JsonElement keyframes))
            {
                continue;
            }
            if (keyframes.ValueKind != JsonValueKind.Array)
            {
                Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.keyframes.array",
                    $"Animation '{code}' keyframes must be an array.", filePath, animationCode: code);
                continue;
            }
            foreach (JsonElement keyframe in keyframes.EnumerateArray())
            {
                int frame = ReadInteger(keyframe, "frame", -1);
                if (frame < 0 || quantityFrames <= 0 || frame >= quantityFrames)
                {
                    Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.keyframe.frame.range",
                        $"Animation '{code}' keyframe {frame} is outside 0..{Math.Max(0, quantityFrames - 1)}.",
                        filePath, animationCode: code, keyFrame: frame);
                }
                if (!DirectorVsmc2ShapeDocument.TryGetProperty(keyframe, "elements", out JsonElement keyedElements))
                {
                    continue;
                }
                if (keyedElements.ValueKind != JsonValueKind.Object)
                {
                    Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.keyframe.elements.object",
                        $"Animation '{code}' keyframe {frame} elements must be an object.",
                        filePath, animationCode: code, keyFrame: frame);
                    continue;
                }
                foreach (JsonProperty keyedElement in keyedElements.EnumerateObject())
                {
                    if (!elements.ContainsKey(keyedElement.Name))
                    {
                        Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.keyframe.element.missing",
                            $"Animation '{code}' keyframe {frame} references missing element '{keyedElement.Name}'.",
                            filePath, keyedElement.Name, code, frame);
                    }
                }
            }
        }
        animations.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateRequiredNumberArray(
        JsonElement element,
        string propertyName,
        int expectedLength,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath,
        string elementName)
    {
        if (!DirectorVsmc2ShapeDocument.TryGetProperty(element, propertyName, out JsonElement value))
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, $"shape.element.{propertyName}.missing",
                $"Element '{elementName}' is missing {propertyName}.", filePath, elementName);
            return;
        }
        ValidateNumberArray(value, expectedLength, propertyName, diagnostics, filePath, elementName);
    }

    private static void ValidateOptionalNumberArray(
        JsonElement element,
        string propertyName,
        int expectedLength,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath,
        string elementName)
    {
        if (DirectorVsmc2ShapeDocument.TryGetProperty(element, propertyName, out JsonElement value))
        {
            ValidateNumberArray(value, expectedLength, propertyName, diagnostics, filePath, elementName);
        }
    }

    private static void ValidateNumberArray(
        JsonElement value,
        int expectedLength,
        string propertyName,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath,
        string elementName = "")
    {
        bool valid = value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() == expectedLength
            && value.EnumerateArray().All(IsFiniteNumber);
        if (!valid)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.array.invalid",
                $"{propertyName} must contain exactly {expectedLength} finite numbers.", filePath, elementName);
        }
    }

    private static void ValidateScale(
        JsonElement element,
        string propertyName,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath,
        string elementName)
    {
        if (!DirectorVsmc2ShapeDocument.TryGetProperty(element, propertyName, out JsonElement value))
        {
            return;
        }
        if (!IsFiniteNumber(value) || value.GetDouble() == 0d)
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.scale.invalid",
                $"Element '{elementName}' {propertyName} must be a finite non-zero number.", filePath, elementName);
        }
    }

    private static void ValidateOptionalFiniteNumber(
        JsonElement element,
        string propertyName,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath,
        string elementName)
    {
        if (DirectorVsmc2ShapeDocument.TryGetProperty(element, propertyName, out JsonElement value)
            && !IsFiniteNumber(value))
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.number.invalid",
                $"Element '{elementName}' {propertyName} must be a finite number.", filePath, elementName);
        }
    }

    private static void ValidatePositiveInteger(
        JsonElement element,
        string propertyName,
        List<DirectorShapeDiagnostic> diagnostics,
        string filePath)
    {
        if (DirectorVsmc2ShapeDocument.TryGetProperty(element, propertyName, out JsonElement value)
            && (!value.TryGetInt32(out int number) || number <= 0))
        {
            Add(diagnostics, DirectorShapeDiagnosticSeverity.Error, "shape.integer.positive",
                $"{propertyName} must be a positive integer.", filePath);
        }
    }

    private static string ReadString(JsonElement element, string propertyName)
        => DirectorVsmc2ShapeDocument.TryGetProperty(element, propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

    private static int ReadInteger(JsonElement element, string propertyName, int fallback)
        => DirectorVsmc2ShapeDocument.TryGetProperty(element, propertyName, out JsonElement value)
            && value.TryGetInt32(out int number)
                ? number
                : fallback;

    private static bool IsFiniteNumber(JsonElement value)
        => value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number)
            && double.IsFinite(number);

    private static DirectorVsmc2ShapeValidationResult Result(
        int elementCount,
        int textureCount,
        List<string> animations,
        List<DirectorShapeDiagnostic> diagnostics)
        => new()
        {
            ElementCount = elementCount,
            TextureCount = textureCount,
            AnimationCodes = animations.ToArray(),
            Diagnostics = diagnostics.ToArray()
        };

    private static void Add(
        List<DirectorShapeDiagnostic> diagnostics,
        DirectorShapeDiagnosticSeverity severity,
        string code,
        string message,
        string filePath,
        string elementName = "",
        string animationCode = "",
        int? keyFrame = null)
        => diagnostics.Add(new DirectorShapeDiagnostic(
            severity, code, message, filePath, elementName, animationCode, keyFrame));

    private sealed record ElementState(string Name, string? ParentName, string StepParentName);
}
