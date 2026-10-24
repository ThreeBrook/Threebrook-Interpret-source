using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ThreebrookInterpret.Setup;

/// <summary>
/// 安装包 = 程序本身（ThreebrookInterpret.exe）+ 附带文件的 zip（许可文件等）+ 尾标，由 publish.ps1 -Package 拼出来。
/// 尾标是最后 32 字节：程序长度、zip 长度、保留、魔数 "TBINTP01"（各 8 字节，小端）。
/// 只有安装包末尾有尾标，所以同一个 exe 装好以后、绿色版里都不会进入安装模式。
/// </summary>
internal sealed class SetupPayload
{
    public const string Magic = "TBINTP01";
    public const int TrailerSize = 32;

    private readonly string _path;
    private readonly byte[] _zip;

    private SetupPayload(string path, long appLength, byte[] zip)
    {
        _path = path;
        AppLength = appLength;
        _zip = zip;
        using var archive = new ZipArchive(new MemoryStream(_zip), ZipArchiveMode.Read);
        Files = new[] { new PayloadFile(SetupEngine.ExeName, appLength) }
            .Concat(archive.Entries.Where(e => !e.FullName.EndsWith('/'))
                .Select(e => new PayloadFile(e.FullName.Replace('/', '\\'), e.Length)))
            .ToList();
    }

    public long AppLength { get; }

    /// <summary>要装的文件（相对安装目录的路径）。第一个是程序本身。</summary>
    public IReadOnlyList<PayloadFile> Files { get; }

    public long TotalSize => Files.Sum(f => f.Length);

    /// <summary>不是安装包（没有尾标）时返回 null。</summary>
    public static SetupPayload? TryOpen(string? path)
    {
        if (path == null || !File.Exists(path))
            return null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < TrailerSize)
                return null;
            var trailer = new byte[TrailerSize];
            file.Seek(-TrailerSize, SeekOrigin.End);
            file.ReadExactly(trailer);
            if (Encoding.ASCII.GetString(trailer, 24, 8) != Magic)
                return null;
            var appLength = BitConverter.ToInt64(trailer, 0);
            var zipLength = BitConverter.ToInt64(trailer, 8);
            if (appLength <= 0 || zipLength < 0 || appLength + zipLength + TrailerSize != file.Length)
                throw new InvalidDataException(Services.Loc.T("Setup.Corrupt"));
            var zip = new byte[zipLength];
            file.Seek(appLength, SeekOrigin.Begin);
            file.ReadExactly(zip);
            return new SetupPayload(path, appLength, zip);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException(Services.Loc.T("Setup.Corrupt"));
        }
    }

    /// <summary>打开一个要装的文件的内容。</summary>
    public Stream Open(PayloadFile file)
    {
        if (file.Path == SetupEngine.ExeName)
        {
            var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new BoundedStream(stream, AppLength);
        }
        var archive = new ZipArchive(new MemoryStream(_zip), ZipArchiveMode.Read);
        var entry = archive.GetEntry(file.Path.Replace('\\', '/')) ?? throw new FileNotFoundException(file.Path);
        return entry.Open();
    }

    /// <summary>只读文件开头一段（安装包前面的程序部分）。</summary>
    private sealed class BoundedStream(Stream inner, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, length - _position));
            _position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

internal sealed record PayloadFile(string Path, long Length);
