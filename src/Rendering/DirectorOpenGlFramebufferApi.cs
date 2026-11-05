using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Client;

namespace VSDirector.Rendering;

internal sealed class DirectorOpenGlFramebufferApi
{
    private const int ReadFramebuffer = 0x8CA8;
    private const int DrawFramebuffer = 0x8CA9;
    private const int ColorBufferBit = 0x4000;
    private const int Linear = 0x2601;

    private readonly BindFramebufferDelegate bindFramebuffer;
    private readonly BlitFramebufferDelegate blitFramebuffer;

    private DirectorOpenGlFramebufferApi(Type glType)
    {
        Assembly assembly = glType.Assembly;
        Type framebufferTarget = RequiredType(assembly, "OpenTK.Graphics.OpenGL.FramebufferTarget");
        Type clearBufferMask = RequiredType(assembly, "OpenTK.Graphics.OpenGL.ClearBufferMask");
        Type blitFilter = RequiredType(assembly, "OpenTK.Graphics.OpenGL.BlitFramebufferFilter");

        bindFramebuffer = Emit<BindFramebufferDelegate>(RequiredMethod(
            glType,
            "BindFramebuffer",
            framebufferTarget,
            typeof(int)));
        blitFramebuffer = Emit<BlitFramebufferDelegate>(RequiredMethod(
            glType,
            "BlitFramebuffer",
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            clearBufferMask,
            blitFilter));
    }

    internal static bool TryCreate(out DirectorOpenGlFramebufferApi? api, out string error)
    {
        try
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.GetName().Name,
                    "OpenTK.Graphics",
                    StringComparison.Ordinal))
                ?? Assembly.Load("OpenTK.Graphics");
            Type glType = assembly.GetType("OpenTK.Graphics.OpenGL.GL", throwOnError: true)!;
            api = new DirectorOpenGlFramebufferApi(glType);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            api = null;
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    internal void BlitColor(FrameBufferRef source, FrameBufferRef target)
    {
        bindFramebuffer(ReadFramebuffer, source.FboId);
        bindFramebuffer(DrawFramebuffer, target.FboId);
        blitFramebuffer(
            0,
            0,
            source.Width,
            source.Height,
            0,
            0,
            target.Width,
            target.Height,
            ColorBufferBit,
            Linear);
    }

    internal void BlitColorToDefault(
        FrameBufferRef source,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight)
    {
        bindFramebuffer(ReadFramebuffer, source.FboId);
        bindFramebuffer(DrawFramebuffer, 0);
        blitFramebuffer(
            0,
            0,
            source.Width,
            source.Height,
            destinationX,
            destinationY,
            destinationX + destinationWidth,
            destinationY + destinationHeight,
            ColorBufferBit,
            Linear);
    }

    private static Type RequiredType(Assembly assembly, string name)
        => assembly.GetType(name, throwOnError: true)!;

    private static MethodInfo RequiredMethod(Type type, string name, params Type[] parameters)
        => type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: parameters,
            modifiers: null)
            ?? throw new MissingMethodException(type.FullName, name);

    private static TDelegate Emit<TDelegate>(MethodInfo target)
        where TDelegate : Delegate
    {
        MethodInfo invoke = typeof(TDelegate).GetMethod(nameof(Action.Invoke))
            ?? throw new InvalidOperationException("Delegate invoke method was not found.");
        ParameterInfo[] parameters = invoke.GetParameters();
        DynamicMethod method = new(
            "VSDirector_" + target.Name,
            invoke.ReturnType,
            parameters.Select(parameter => parameter.ParameterType).ToArray(),
            target.Module,
            skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        for (int index = 0; index < parameters.Length; index++)
        {
            il.Emit(OpCodes.Ldarg, index);
        }
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<TDelegate>();
    }

    private delegate void BindFramebufferDelegate(int target, int framebuffer);
    private delegate void BlitFramebufferDelegate(
        int sourceX0,
        int sourceY0,
        int sourceX1,
        int sourceY1,
        int destinationX0,
        int destinationY0,
        int destinationX1,
        int destinationY1,
        int mask,
        int filter);
}
