using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThreeBrookTrans.Services;

public sealed record OcrLanguage(string Tag, string DisplayName);

/// <summary>
/// Windows 自带 OCR（Windows.Media.Ocr），离线、零模型体积。
/// 为避免引入约 25MB 的 Windows SDK 投影库，这里直接按 vtable 调用 WinRT 接口；
/// 各接口 GUID 与方法顺序取自系统 WinMetadata。
/// </summary>
public static unsafe class WindowsOcr
{
    private static readonly Guid IID_IOcrEngineStatics = new("5bffa85a-3384-3540-9940-699120d428a8");
    private static readonly Guid IID_ISoftwareBitmapFactory = new("c99feb69-2d62-4d47-a6b3-4fdb6a07fdf8");
    private static readonly Guid IID_ILanguageFactory = new("9b0252ac-0c27-44f8-b792-9793fb66c63e");
    private static readonly Guid IID_IMemoryBuffer = new("fbc4dd2a-245b-11e4-af98-689423260cf8");
    private static readonly Guid IID_IMemoryBufferByteAccess = new("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");
    private static readonly Guid IID_IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    private const int BitmapPixelFormatBgra8 = 87;
    private const int BitmapBufferAccessWrite = 1;
    private const int AsyncStarted = 0, AsyncCompleted = 1;

    /// <summary>小图放大后再识别，屏幕上的小字号识别率明显更高。</summary>
    private const int UpscaleBelow = 1200;

    /// <summary>两个单词间距小于行高的这个比例时视为同一个单词。</summary>
    private const float WordGapRatio = 0.2f;

    /// <summary>本机已安装的 OCR 语言。</summary>
    public static List<OcrLanguage> GetAvailableLanguages()
    {
        var result = new List<OcrLanguage>();
        var statics = GetFactory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);
        try
        {
            IntPtr list;
            Check(CallOut(statics, 7, &list)); // get_AvailableRecognizerLanguages
            try
            {
                foreach (var language in VectorItems(list))
                {
                    try
                    {
                        result.Add(new OcrLanguage(GetString(language, 6), GetString(language, 7))); // LanguageTag, DisplayName
                    }
                    finally
                    {
                        Marshal.Release(language);
                    }
                }
            }
            finally
            {
                Marshal.Release(list);
            }
        }
        finally
        {
            Marshal.Release(statics);
        }
        return result;
    }

    /// <param name="languageTag">"auto" 使用系统用户语言；否则如 "zh-Hans-CN"、"ja"、"en-US"。</param>
    public static Task<string> RecognizeAsync(BitmapSource image, string languageTag, CancellationToken ct = default)
    {
        var pixels = PreparePixels(image, out var width, out var height);
        return Task.Run(() => Recognize(pixels, width, height, languageTag, ct), ct);
    }

    private static byte[] PreparePixels(BitmapSource image, out int width, out int height)
    {
        BitmapSource source = image;
        if (Math.Max(image.PixelWidth, image.PixelHeight) < UpscaleBelow)
        {
            var scaled = new TransformedBitmap(image, new ScaleTransform(2, 2));
            source = scaled;
        }
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        width = bgra.PixelWidth;
        height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static string Recognize(byte[] pixels, int width, int height, string languageTag, CancellationToken ct)
    {
        var engine = CreateEngine(languageTag);
        var bitmap = IntPtr.Zero;
        var operation = IntPtr.Zero;
        var result = IntPtr.Zero;
        try
        {
            bitmap = CreateSoftwareBitmap(pixels, width, height);
            Check(Call(engine, 6, bitmap, &operation)); // RecognizeAsync
            WaitForCompletion(operation, ct);
            Check(CallOut(operation, 8, &result)); // IAsyncOperation<OcrResult>.GetResults
            return BuildText(result);
        }
        finally
        {
            Release(result);
            Release(operation);
            Release(bitmap);
            Release(engine);
        }
    }

    private static IntPtr CreateEngine(string languageTag)
    {
        var statics = GetFactory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);
        try
        {
            IntPtr engine;
            if (string.IsNullOrWhiteSpace(languageTag) || languageTag == "auto")
            {
                Check(CallOut(statics, 10, &engine)); // TryCreateFromUserProfileLanguages
                if (engine == IntPtr.Zero)
                    throw new InvalidOperationException("系统没有可用的 OCR 语言包，请在 Windows 设置 → 时间和语言 → 语言 中安装。");
                return engine;
            }

            var language = CreateLanguage(languageTag);
            try
            {
                Check(Call(statics, 9, language, &engine)); // TryCreateFromLanguage
            }
            finally
            {
                Marshal.Release(language);
            }
            if (engine == IntPtr.Zero)
                throw new InvalidOperationException($"未安装 {languageTag} 的 OCR 语言包，请在 Windows 设置 → 时间和语言 → 语言 中添加该语言（勾选\"光学字符识别\"）。");
            return engine;
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    private static IntPtr CreateLanguage(string tag)
    {
        var factory = GetFactory("Windows.Globalization.Language", IID_ILanguageFactory);
        var hstring = CreateHString(tag);
        try
        {
            IntPtr language;
            Check(Call(factory, 6, hstring, &language)); // CreateLanguage
            return language;
        }
        finally
        {
            WindowsDeleteString(hstring);
            Marshal.Release(factory);
        }
    }

    private static IntPtr CreateSoftwareBitmap(byte[] pixels, int width, int height)
    {
        var factory = GetFactory("Windows.Graphics.Imaging.SoftwareBitmap", IID_ISoftwareBitmapFactory);
        IntPtr bitmap;
        try
        {
            var vtbl = *(void***)factory;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, IntPtr*, int>)vtbl[6])(factory, BitmapPixelFormatBgra8, width, height, &bitmap)); // Create
        }
        finally
        {
            Marshal.Release(factory);
        }

        IntPtr buffer = IntPtr.Zero, memoryBuffer = IntPtr.Zero, reference = IntPtr.Zero, byteAccess = IntPtr.Zero;
        try
        {
            var vtbl = *(void***)bitmap;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)vtbl[15])(bitmap, BitmapBufferAccessWrite, &buffer)); // LockBuffer

            PlaneDescription plane;
            var bufferVtbl = *(void***)buffer;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, PlaneDescription*, int>)bufferVtbl[7])(buffer, 0, &plane)); // GetPlaneDescription

            memoryBuffer = QueryInterface(buffer, IID_IMemoryBuffer);
            Check(CallOut(memoryBuffer, 6, &reference)); // CreateReference
            byteAccess = QueryInterface(reference, IID_IMemoryBufferByteAccess);

            byte* data;
            uint capacity;
            var accessVtbl = *(void***)byteAccess;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)accessVtbl[3])(byteAccess, &data, &capacity)); // GetBuffer

            fixed (byte* src = pixels)
            {
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + y * width * 4, data + plane.StartIndex + y * plane.Stride, capacity - plane.StartIndex - y * plane.Stride, width * 4);
            }
        }
        catch
        {
            Release(bitmap);
            throw;
        }
        finally
        {
            Release(byteAccess);
            Close(reference);
            Release(reference);
            Release(memoryBuffer);
            Close(buffer);
            Release(buffer);
        }
        return bitmap;
    }

    private static void WaitForCompletion(IntPtr operation, CancellationToken ct)
    {
        var info = QueryInterface(operation, IID_IAsyncInfo);
        try
        {
            while (true)
            {
                int status;
                Check(CallOut(info, 7, &status)); // get_Status
                if (status == AsyncCompleted)
                    return;
                if (status != AsyncStarted)
                {
                    int error;
                    CallOut(info, 8, &error); // get_ErrorCode
                    Marshal.ThrowExceptionForHR(error != 0 ? error : unchecked((int)0x80004004));
                }
                if (ct.IsCancellationRequested)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)info)[9])(info); // Cancel
                    ct.ThrowIfCancellationRequested();
                }
                Thread.Sleep(10);
            }
        }
        finally
        {
            Marshal.Release(info);
        }
    }

    /// <summary>按行拼接单词；中日韩文字之间不加空格，其他单词之间加空格。</summary>
    private static string BuildText(IntPtr ocrResult)
    {
        var text = new StringBuilder();
        IntPtr lines;
        Check(CallOut(ocrResult, 6, &lines)); // get_Lines
        try
        {
            foreach (var line in VectorItems(lines))
            {
                try
                {
                    IntPtr words;
                    Check(CallOut(line, 6, &words)); // get_Words
                    try
                    {
                        var items = new List<(string Text, OcrRect Box)>();
                        foreach (var word in VectorItems(words))
                        {
                            try
                            {
                                OcrRect box;
                                ((delegate* unmanaged[Stdcall]<IntPtr, OcrRect*, int>)(*(void***)word)[6])(word, &box); // get_BoundingRect
                                items.Add((GetString(word, 7), box)); // get_Text
                            }
                            finally
                            {
                                Marshal.Release(word);
                            }
                        }

                        // OCR 有时把一个英文单词切成几段（等宽字体常见），按实际间距决定是否加空格
                        var lineHeight = items.Count > 0 ? items.Max(i => i.Box.Height) : 0;
                        var lineText = new StringBuilder();
                        for (var i = 0; i < items.Count; i++)
                        {
                            var w = items[i].Text;
                            if (i > 0 && w.Length > 0 && lineText.Length > 0 && !TextUtil.IsCjk(lineText[^1]) && !TextUtil.IsCjk(w[0]))
                            {
                                var gap = items[i].Box.X - (items[i - 1].Box.X + items[i - 1].Box.Width);
                                if (gap > lineHeight * WordGapRatio)
                                    lineText.Append(' ');
                            }
                            lineText.Append(w);
                        }
                        if (text.Length > 0)
                            text.Append('\n');
                        text.Append(lineText);
                    }
                    finally
                    {
                        Marshal.Release(words);
                    }
                }
                finally
                {
                    Marshal.Release(line);
                }
            }
        }
        finally
        {
            Marshal.Release(lines);
        }
        return text.ToString();
    }


    // —— WinRT 基础设施 ——

    /// <summary>遍历 IVectorView&lt;T&gt;（GetAt = 6，get_Size = 7），返回的指针由调用方 Release。</summary>
    private static List<IntPtr> VectorItems(IntPtr vector)
    {
        uint size;
        Check(CallOut(vector, 7, &size));
        var items = new List<IntPtr>((int)size);
        for (uint i = 0; i < size; i++)
        {
            IntPtr item;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)vector)[6])(vector, i, &item));
            items.Add(item);
        }
        return items;
    }

    private static int CallOut(IntPtr obj, int slot, IntPtr* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)obj)[slot])(obj, result);

    private static int CallOut(IntPtr obj, int slot, int* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)(*(void***)obj)[slot])(obj, result);

    private static int CallOut(IntPtr obj, int slot, uint* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)(*(void***)obj)[slot])(obj, result);

    private static int Call(IntPtr obj, int slot, IntPtr arg, IntPtr* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(void***)obj)[slot])(obj, arg, result);

    private static string GetString(IntPtr obj, int slot)
    {
        IntPtr hstring;
        Check(CallOut(obj, slot, &hstring));
        try
        {
            uint length;
            var raw = WindowsGetStringRawBuffer(hstring, &length);
            return new string(raw, 0, (int)length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static IntPtr GetFactory(string className, Guid iid)
    {
        var hstring = CreateHString(className);
        try
        {
            Check(RoGetActivationFactory(hstring, ref iid, out var factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static IntPtr CreateHString(string value)
    {
        Check(WindowsCreateString(value, (uint)value.Length, out var hstring));
        return hstring;
    }

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        Check(Marshal.QueryInterface(obj, ref iid, out var result));
        return result;
    }

    private static void Close(IntPtr obj)
    {
        var iid = IID_IClosable;
        if (obj == IntPtr.Zero || Marshal.QueryInterface(obj, ref iid, out var closable) < 0)
            return;
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)closable)[6])(closable);
        Marshal.Release(closable);
    }

    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
            Marshal.Release(obj);
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OcrRect
    {
        public float X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlaneDescription
    {
        public int StartIndex, Width, Height, Stride;
    }

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern char* WindowsGetStringRawBuffer(IntPtr hstring, uint* length);
}
