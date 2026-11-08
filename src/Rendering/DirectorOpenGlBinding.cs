using System.Linq.Expressions;
using System.Reflection;

namespace VSDirector.Rendering;

/// <summary>Adapts the game's enum-based GL entry points to primitive delegates.</summary>
internal static class DirectorOpenGlBinding
{
    internal static Type ApiType => Type.GetType(
        "OpenTK.Graphics.OpenGL.GL, OpenTK.Graphics", throwOnError: true)!;

    internal static T Bind<T>(Type api, string method, params Type[] signature) where T : Delegate
    {
        MethodInfo target = api.GetMethod(method, signature)
            ?? throw new MissingMethodException(api.FullName, method);
        MethodInfo contract = typeof(T).GetMethod("Invoke")!;
        ParameterExpression[] arguments = contract.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
        ParameterInfo[] nativeParameters = target.GetParameters();
        if (arguments.Length != nativeParameters.Length)
            throw new InvalidOperationException($"Incompatible GL entry point: {method}");
        Expression[] converted = arguments.Select((argument, index) =>
            (Expression)Expression.Convert(argument, nativeParameters[index].ParameterType)).ToArray();
        Expression call = Expression.Call(target, converted);
        Expression body = contract.ReturnType == typeof(void)
            ? call : Expression.Convert(call, contract.ReturnType);
        return Expression.Lambda<T>(body, arguments).Compile();
    }

    internal static Type NativeType(Type api, string name)
        => api.Assembly.GetType("OpenTK.Graphics.OpenGL." + name, throwOnError: true)!;
}
