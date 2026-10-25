using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ThreebrookInterpret.Services;

/// <summary>读识别结果：data 是输出张量的数据（只在回调里有效），dims 是各维大小。</summary>
public delegate T OnnxOutputReader<T>(ReadOnlySpan<float> data, long[] dims);

/// <summary>
/// 最小的 ONNX Runtime 调用：直接用 C API（OrtApi 函数表），只做"加载模型、输入一个 float 张量、读第一个输出"。
/// 不引用微软的托管包装（它连同 System.Numerics.Tensors 会让安装包多出约 0.7 MB）。
/// 函数表序号取自 onnxruntime_c_api.h（1.31，API 只追加不改序），onnxruntime.dll 由识别组件提供。
/// </summary>
public sealed unsafe class OnnxSession : IDisposable
{
    // OrtApi 里用到的函数序号
    private const int GetErrorMessage = 2, CreateEnv = 3, CreateSession = 7, RunModel = 9, CreateSessionOptions = 10;
    private const int SetSessionGraphOptimizationLevel = 23, SetIntraOpNumThreads = 24, SessionGetInputName = 36, SessionGetOutputName = 37;
    private const int CreateTensorWithDataAsOrtValue = 49, GetTensorMutableData = 51, GetDimensionsCount = 61, GetDimensions = 62;
    private const int GetTensorTypeAndShape = 65, CreateCpuMemoryInfo = 69, AllocatorFree = 76, GetAllocatorWithDefaultOptions = 78;
    private const int ReleaseStatus = 93, ReleaseSession = 95, ReleaseValue = 96, ReleaseTensorTypeAndShapeInfo = 99, ReleaseSessionOptions = 100;

    /// <summary>用到的函数 1.17 起就都有了，按这个版本取函数表，新版的 dll 也能用。</summary>
    private const uint ApiVersion = 17;
    private const int LoggingLevelError = 3, OptimizeAll = 99, ArenaAllocator = 1, MemTypeDefault = 0, ElementFloat = 1;

    private static readonly object LoadLock = new();
    private static IntPtr* _api;
    private static IntPtr _env, _cpuMemory;

    private IntPtr _session;
    private readonly byte[] _inputName, _outputName;

    /// <summary>加载 onnxruntime.dll（整个进程只加载一次，之后不能卸载）。</summary>
    public static void Initialize(string dllPath)
    {
        lock (LoadLock)
        {
            if (_api != null)
                return;
            var library = NativeLibrary.Load(dllPath);
            var getApiBase = (delegate* unmanaged[Stdcall]<IntPtr*>)NativeLibrary.GetExport(library, "OrtGetApiBase");
            var apiBase = getApiBase(); // OrtApiBase { GetApi, GetVersionString }
            var api = ((delegate* unmanaged[Stdcall]<uint, IntPtr*>)apiBase[0])(ApiVersion);
            if (api == null)
                throw new InvalidOperationException("onnxruntime.dll does not support API version " + ApiVersion);
            _api = api;

            IntPtr env, memory;
            fixed (byte* logId = "ThreebrookInterpret\0"u8) // ONNX Runtime 会复制这个名字
                Check(((delegate* unmanaged[Stdcall]<int, byte*, IntPtr*, IntPtr>)_api[CreateEnv])(LoggingLevelError, logId, &env));
            Check(((delegate* unmanaged[Stdcall]<int, int, IntPtr*, IntPtr>)_api[CreateCpuMemoryInfo])(ArenaAllocator, MemTypeDefault, &memory));
            _env = env;
            _cpuMemory = memory;
        }
    }

    public OnnxSession(string modelPath, int threads)
    {
        if (_api == null)
            throw new InvalidOperationException("OnnxSession.Initialize was not called");
        IntPtr options;
        Check(((delegate* unmanaged[Stdcall]<IntPtr*, IntPtr>)_api[CreateSessionOptions])(&options));
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr>)_api[SetSessionGraphOptimizationLevel])(options, OptimizeAll));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr>)_api[SetIntraOpNumThreads])(options, threads));
            IntPtr session;
            fixed (char* path = modelPath) // Windows 上模型路径是 wchar_t*
                Check(((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, IntPtr*, IntPtr>)_api[CreateSession])(_env, path, options, &session));
            _session = session;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, void>)_api[ReleaseSessionOptions])(options);
        }
        _inputName = GetName(SessionGetInputName);
        _outputName = GetName(SessionGetOutputName);
    }

    /// <summary>输入一个 float 张量，读第一个输出（输出缓冲区不复制，直接交给 reader）。</summary>
    public T Run<T>(float[] input, long[] shape, OnnxOutputReader<T> reader)
    {
        IntPtr value = IntPtr.Zero, output = IntPtr.Zero;
        try
        {
            fixed (float* data = input)
            fixed (long* dims = shape)
            fixed (byte* inputName = _inputName)
            fixed (byte* outputName = _outputName)
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, void*, nuint, long*, nuint, int, IntPtr*, IntPtr>)_api[CreateTensorWithDataAsOrtValue])(
                    _cpuMemory, data, (nuint)(input.Length * sizeof(float)), dims, (nuint)shape.Length, ElementFloat, &value));
                var inputNames = stackalloc byte*[] { inputName };
                var outputNames = stackalloc byte*[] { outputName };
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte**, IntPtr*, nuint, byte**, nuint, IntPtr*, IntPtr>)_api[RunModel])(
                    _session, IntPtr.Zero, inputNames, &value, 1, outputNames, 1, &output));
            }

            IntPtr info;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, IntPtr>)_api[GetTensorTypeAndShape])(output, &info));
            long[] outDims;
            try
            {
                nuint count;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, nuint*, IntPtr>)_api[GetDimensionsCount])(info, &count));
                outDims = new long[(int)count];
                fixed (long* p = outDims)
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, long*, nuint, IntPtr>)_api[GetDimensions])(info, p, count));
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, void>)_api[ReleaseTensorTypeAndShapeInfo])(info);
            }

            float* result;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, float**, IntPtr>)_api[GetTensorMutableData])(output, &result));
            long length = 1;
            foreach (var d in outDims)
                length *= d;
            return reader(new ReadOnlySpan<float>(result, checked((int)length)), outDims);
        }
        finally
        {
            if (output != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<IntPtr, void>)_api[ReleaseValue])(output);
            if (value != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<IntPtr, void>)_api[ReleaseValue])(value);
        }
    }

    public void Dispose()
    {
        if (_session == IntPtr.Zero)
            return;
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)_api[ReleaseSession])(_session);
        _session = IntPtr.Zero;
    }

    /// <summary>第一个输入 / 输出的名字（UTF-8，带结尾 0），Run 时要用。</summary>
    private byte[] GetName(int slot)
    {
        IntPtr allocator;
        byte* name;
        Check(((delegate* unmanaged[Stdcall]<IntPtr*, IntPtr>)_api[GetAllocatorWithDefaultOptions])(&allocator));
        Check(((delegate* unmanaged[Stdcall]<IntPtr, nuint, IntPtr, byte**, IntPtr>)_api[slot])(_session, 0, allocator, &name));
        try
        {
            return Encoding.UTF8.GetBytes(Marshal.PtrToStringUTF8((IntPtr)name) + "\0");
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, void*, IntPtr>)_api[AllocatorFree])(allocator, name);
        }
    }

    /// <summary>OrtStatus* 不为空就是出错：取出错误信息后释放，抛出异常。</summary>
    private static void Check(IntPtr status)
    {
        if (status == IntPtr.Zero)
            return;
        var message = Marshal.PtrToStringUTF8(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)_api[GetErrorMessage])(status));
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)_api[ReleaseStatus])(status);
        throw new InvalidOperationException("ONNX Runtime: " + message);
    }
}
