using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThreebrookInterpret.Services;

public enum StitchResult
{
    /// <summary>第一帧，作为起点。</summary>
    Started,
    /// <summary>画面没变（没滚动或滚到底了）。</summary>
    Unchanged,
    /// <summary>找到重叠，追加了新内容。</summary>
    Appended,
    /// <summary>找不到可靠的重叠：滚得太快、往上滚或画面整体变了，这一帧丢弃。</summary>
    NoOverlap,
    /// <summary>已达到最大高度。</summary>
    Full,
}

/// <summary>
/// 长截图拼接：每次传入同一区域的新一帧（BGRX 32 位像素），与上一帧逐行比较，算出向下滚动了多少行，只追加新出现的部分。
/// 顶部 / 底部固定不动的区域（网页导航栏、底部工具栏）在第一次成功拼接时识别出来，长图里只保留一份。
/// 行比较用每行像素的哈希；纯色行（空白）区分度低，计分时降低权重，避免空白区域被误配。
/// </summary>
public sealed class ScrollStitcher
{
    /// <summary>长图最大高度（像素），防止无限滚动的页面占满内存。</summary>
    public const int MaxHeight = 30000;

    private const double MinScore = 0.92;
    private const int MinOverlapRows = 24;
    private const double UniformRowWeight = 0.05;

    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;

    private byte[]? _previous;
    private ulong[] _previousHashes = Array.Empty<ulong>();

    private int _header = -1; // 顶部固定行数，-1 表示还未确定
    private int _footer;
    private byte[] _headerRows = Array.Empty<byte>();
    private byte[] _footerRows = Array.Empty<byte>();
    private readonly List<byte[]> _body = new();
    private int _bodyHeight;

    public ScrollStitcher(int width, int height)
    {
        _width = width;
        _height = height;
        _stride = width * 4;
    }

    /// <summary>当前拼接结果的高度（像素）。</summary>
    public int TotalHeight => _previous == null ? 0 : _header < 0 ? _height : _header + _bodyHeight + _footer;

    public int Width => _width;

    public StitchResult Add(byte[] frame)
    {
        if (frame.Length != _stride * _height)
            throw new ArgumentException("帧尺寸与区域不一致");

        var hashes = HashRows(frame);
        if (_previous == null)
        {
            Accept(frame, hashes);
            return StitchResult.Started;
        }
        if (TotalHeight >= MaxHeight)
            return StitchResult.Full;

        if (SameRows(hashes, _previousHashes, 0, _height))
            return StitchResult.Unchanged;

        // 固定区域：第一次成功拼接时确定，之后沿用
        int header = _header, footer = _footer;
        if (_header < 0)
        {
            header = 0;
            while (header < _height && hashes[header] == _previousHashes[header])
                header++;
            footer = 0;
            while (footer < _height - header && hashes[_height - 1 - footer] == _previousHashes[_height - 1 - footer])
                footer++;
            // 全是空白行的"固定区"只是恰好两帧都空白，不是真的导航栏，否则这段会被冻住
            if (AllUniform(hashes, 0, header))
                header = 0;
            if (AllUniform(hashes, _height - footer, footer))
                footer = 0;
            // 固定区域过大说明不是"内容在滚"，交给下面的匹配判定失败
            if (header + footer > _height * 3 / 4)
            {
                header = 0;
                footer = 0;
            }
        }

        var contentHeight = _height - header - footer;
        var shift = FindShift(hashes, header, contentHeight, out var uniform);
        if (shift == 0)
            return uniform ? StitchResult.Unchanged : StitchResult.NoOverlap;

        if (_header < 0)
        {
            // 第一次拼接：把第一帧拆成 固定顶部 + 内容 + 固定底部
            _header = header;
            _footer = footer;
            _headerRows = Slice(_previous, 0, header);
            _body.Add(Slice(_previous, header, contentHeight));
            _bodyHeight = contentHeight;
        }

        // 追加新一帧内容区最下面新出现的 shift 行；底部固定区换成最新一帧的
        var room = MaxHeight - TotalHeight;
        var take = Math.Min(shift, room);
        _body.Add(Slice(frame, header + contentHeight - shift, take));
        _bodyHeight += take;
        _footerRows = Slice(frame, _height - footer, footer);
        Accept(frame, hashes);
        return take < shift ? StitchResult.Full : StitchResult.Appended;
    }

    /// <summary>
    /// 在内容区里找滚动距离 s：新帧第 i 行应等于上一帧第 i+s 行。
    /// 返回得分最高且达到阈值的 s（相同得分取较小值）；全是纯色行时 uniform 为 true。
    /// </summary>
    private int FindShift(ulong[] current, int top, int contentHeight, out bool uniform)
    {
        uniform = true;
        for (var i = top; i < top + contentHeight; i++)
        {
            if (!IsUniform(current[i]))
            {
                uniform = false;
                break;
            }
        }
        if (uniform)
            return 0;

        var bestShift = 0;
        var bestScore = 0.0;
        for (var s = 1; s <= contentHeight - MinOverlapRows; s++)
        {
            var overlap = contentHeight - s;
            double matched = 0, total = 0;
            for (var i = 0; i < overlap; i++)
            {
                var h = current[top + i];
                var weight = IsUniform(h) ? UniformRowWeight : 1.0;
                total += weight;
                if (h == _previousHashes[top + i + s])
                    matched += weight;
            }
            var score = total > 0 ? matched / total : 0;
            if (score > bestScore + 1e-9)
            {
                bestScore = score;
                bestShift = s;
            }
        }
        return bestScore >= MinScore ? bestShift : 0;
    }

    private void Accept(byte[] frame, ulong[] hashes)
    {
        _previous = frame;
        _previousHashes = hashes;
    }

    /// <summary>拼出最终长图：固定顶部 + 内容 + 固定底部。</summary>
    public BitmapSource ToBitmap()
    {
        if (_previous == null)
            throw new InvalidOperationException("还没有截到内容");

        byte[] pixels;
        int height;
        if (_header < 0)
        {
            pixels = _previous;
            height = _height;
        }
        else
        {
            height = TotalHeight;
            pixels = new byte[height * _stride];
            var offset = 0;
            Buffer.BlockCopy(_headerRows, 0, pixels, offset, _headerRows.Length);
            offset += _headerRows.Length;
            foreach (var part in _body)
            {
                Buffer.BlockCopy(part, 0, pixels, offset, part.Length);
                offset += part.Length;
            }
            Buffer.BlockCopy(_footerRows, 0, pixels, offset, _footerRows.Length);
        }

        var bitmap = BitmapSource.Create(_width, height, 96, 96, PixelFormats.Bgr32, null, pixels, _stride);
        bitmap.Freeze();
        return bitmap;
    }

    private byte[] Slice(byte[] frame, int row, int count)
    {
        var part = new byte[count * _stride];
        Buffer.BlockCopy(frame, row * _stride, part, 0, part.Length);
        return part;
    }

    private static bool AllUniform(ulong[] hashes, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (!IsUniform(hashes[i]))
                return false;
        }
        return true;
    }

    private static bool SameRows(ulong[] a, ulong[] b, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    // 行哈希：FNV-1a（忽略未使用的第 4 字节）；最高位标记"纯色行"
    private const ulong UniformFlag = 1UL << 63;

    private static bool IsUniform(ulong hash) => (hash & UniformFlag) != 0;

    private ulong[] HashRows(byte[] frame)
    {
        var hashes = new ulong[_height];
        for (var y = 0; y < _height; y++)
        {
            var offset = y * _stride;
            var hash = 14695981039346656037UL;
            var first = BitConverter.ToUInt32(frame, offset) & 0xFFFFFF;
            var uniformRow = true;
            for (var x = 0; x < _width; x++)
            {
                var p = offset + x * 4;
                var pixel = (uint)(frame[p] | frame[p + 1] << 8 | frame[p + 2] << 16);
                if (pixel != first)
                    uniformRow = false;
                hash = (hash ^ pixel) * 1099511628211UL;
            }
            hashes[y] = (hash & ~UniformFlag) | (uniformRow ? UniformFlag : 0);
        }
        return hashes;
    }
}
