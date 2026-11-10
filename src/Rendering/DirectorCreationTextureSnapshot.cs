using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VSDirector.Rendering;

internal static class DirectorCreationTextureSnapshot
{
    internal static void Save(TextureAtlasPosition position, string path)
    {
        Type gl = DirectorOpenGlBinding.ApiType;
        Type T(string name) => DirectorOpenGlBinding.NativeType(gl, name);
        var get = DirectorOpenGlBinding.Bind<System.Func<int,int>>(gl, "GetInteger", T("GetPName"));
        var bind = DirectorOpenGlBinding.Bind<Action<int,int>>(gl, "BindTexture", T("TextureTarget"), typeof(int));
        var buffer = DirectorOpenGlBinding.Bind<Action<int,int>>(gl, "BindBuffer", T("BufferTarget"), typeof(int));
        var pixelStore = DirectorOpenGlBinding.Bind<Action<int,int>>(gl, "PixelStore", T("PixelStoreParameter"), typeof(int));
        var read = DirectorOpenGlBinding.Bind<Action<int,int,int,int,IntPtr>>(gl, "GetTexImage", T("TextureTarget"), typeof(int), T("PixelFormat"), T("PixelType"), typeof(IntPtr));
        var size = gl.GetMethod("GetTexLevelParameter", [T("TextureTarget"), typeof(int), T("GetTextureParameter"), typeof(int).MakeByRefType()])!;
        int texture = get(0x8069), packBuffer = get(0x88ED);
        int[] packNames = [0x0D05, 0x0D02, 0x0D03, 0x0D04];
        int[] packing = packNames.Select(get).ToArray();
        try
        {
            bind(0x0DE1, position.atlasTextureId); buffer(0x88EB, 0);
            for (int i = 0; i < packNames.Length; i++) pixelStore(packNames[i], i == 0 ? 4 : 0);
            int Dimension(int name) { object[] values = [Enum.ToObject(T("TextureTarget"), 0x0DE1), 0, Enum.ToObject(T("GetTextureParameter"), name), 0]; size.Invoke(null, values); return (int)values[3]; }
            int width = Dimension(0x1000), height = Dimension(0x1001);
            if (width <= 0 || height <= 0 || (long)width * height > 67108864) throw new InvalidDataException("Invalid actor texture atlas.");
            int[] pixels = new int[width * height]; var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { read(0x0DE1, 0, 0x80E1, 0x1401, pin.AddrOfPinnedObject()); } finally { pin.Free(); }
            int x = Math.Clamp((int)Math.Round(position.x1 * width), 0, width - 1), y = Math.Clamp((int)Math.Round(position.y1 * height), 0, height - 1);
            int w = Math.Clamp((int)Math.Round((position.x2 - position.x1) * width), 1, width - x), h = Math.Clamp((int)Math.Round((position.y2 - position.y1) * height), 1, height - y);
            using var bitmap = new BitmapExternal(w, h);
            for (int row = 0; row < h; row++) Marshal.Copy(pixels, (y + row) * width + x, bitmap.PixelsPtrAndLock + row * w * 4, w);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); bitmap.Save(path);
        }
        finally
        {
            bind(0x0DE1, texture); buffer(0x88EB, packBuffer);
            for (int i = 0; i < packNames.Length; i++) pixelStore(packNames[i], packing[i]);
        }
    }
}
