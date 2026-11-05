using System.Text;
using Vintagestory.API.Common;

namespace VSDirector.Runtime;

internal static class DirectorVsmc2ShapeLoader
{
    public static bool TryRead(string path, out Shape? shape, out string error)
    {
        shape = null;
        error = string.Empty;
        try
        {
            string json = File.ReadAllText(Path.GetFullPath(path), Encoding.UTF8);
            shape = JsonUtil.FromString<Shape>(json)
                ?? throw new InvalidDataException("Vintage Story returned an empty Shape.");
            if (shape.Elements is null)
            {
                throw new InvalidDataException("Vintage Story Shape has no elements array.");
            }
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            shape = null;
            return false;
        }
    }
}
