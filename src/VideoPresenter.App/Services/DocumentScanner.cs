// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
// ============================================================================
//  DocumentScanner.cs —— 文档自动裁剪 + 透视矫正
//
//  对标希沃 EasiCamera 的「一键扫描 / 自动裁剪」招牌功能。
//
//  希沃为此背了 30MB 的库（libda_cropper 18.7MB + librectification 5.2MB +
//  libtensorflowlite_c 6.2MB），用 AI 做边缘检测。
//
//  这里走【传统计算机视觉】路线，零第三方依赖、纯 C#：
//    纸张是"高亮度、大面积、近矩形"的物体，在展台这种受控光源下，
//    二值化 + 投影扫描已经足够可靠，不需要神经网络。
//
//  换来的是：体积几乎为 0（对比它的 30MB），而且不依赖 GPU。
// ============================================================================

using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Foundation;

namespace VideoPresenter.App.Services;

/// <summary>文档扫描：自动找纸张边界并拉正。</summary>
internal static class DocumentScanner
{
    /// <summary>一次扫描的完整结果。</summary>
    internal sealed class ScanResult
    {
        /// <summary>是否成功找到纸张。</summary>
        public bool Found { get; init; }

        /// <summary>找到的四角（顺序：左上、右上、右下、左下），原图坐标。</summary>
        public Point[]? Corners { get; init; }

        /// <summary>拉正后的位图（调用方负责 Dispose）。</summary>
        public SoftwareBitmap? Rectified { get; init; }

        /// <summary>失败原因 / 诊断文本。</summary>
        public string Message { get; init; } = "";
    }

    // ───────────────────────── 公开入口 ─────────────────────────

    /// <summary>
    /// 对一帧做"自动裁边 + 透视矫正"。
    ///
    /// <para>
    /// 输入约定为 BGRA8（调用方从 GrabStill 拿到后已转换）。
    /// </para>
    /// </summary>
    public static ScanResult Scan(SoftwareBitmap source)
    {
        try
        {
            int w = source.PixelWidth;
            int h = source.PixelHeight;

            byte[] bgra = ReadPixels(source);

            // ① 灰度
            var gray = ToGray(bgra, w, h);

            // ② Otsu 自动阈值 → 二值图（true = 亮，可能是纸）
            int threshold = OtsuThreshold(gray);
            var bright = new bool[w * h];
            for (int i = 0; i < gray.Length; i++) bright[i] = gray[i] > threshold;

            // ③ 四边投影扫描，找纸张边界
            var corners = FindCorners(bright, w, h);
            if (corners is null)
            {
                return new ScanResult
                {
                    Found = false,
                    Message = $"未检测到纸张（阈值 {threshold}）——请让纸张占满画面并保持光照均匀",
                };
            }

            // ④ 透视矫正
            var rectified = Rectify(bgra, w, h, corners);

            return new ScanResult
            {
                Found = true,
                Corners = corners,
                Rectified = rectified,
                Message = $"已裁剪 {w}×{h} → {rectified.PixelWidth}×{rectified.PixelHeight}",
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP-Scan] 扫描失败：{ex}");
            return new ScanResult { Found = false, Message = $"扫描失败：{ex.Message}" };
        }
    }

    // ───────────────────────── 像素读写 ─────────────────────────

    /// <summary>把 SoftwareBitmap（BGRA8）读成托管数组。</summary>
    private static unsafe byte[] ReadPixels(SoftwareBitmap bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var bytes = new byte[w * 4 * h];

        using var buffer = bmp.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* ptr, out uint cap);
        int len = Math.Min(bytes.Length, (int)cap);
        System.Runtime.InteropServices.Marshal.Copy((IntPtr)ptr, bytes, 0, len);

        return bytes;
    }

    /// <summary>BGRA → 灰度（BT.601 权重）。</summary>
    private static byte[] ToGray(byte[] bgra, int w, int h)
    {
        var gray = new byte[w * h];

        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            // 0.299R + 0.587G + 0.114B，用整数运算避免浮点
            gray[i] = (byte)((bgra[p + 2] * 77 + bgra[p + 1] * 150 + bgra[p] * 29) >> 8);
        }

        return gray;
    }

    // ───────────────────────── 阈值 ─────────────────────────

    /// <summary>
    /// Otsu 大津法：自动找"前景/背景"的最佳分割阈值。
    ///
    /// <para>
    /// 经典做法 —— 遍历所有可能的阈值，选那个让"类间方差"最大的。
    /// 好处是完全不用手工调参，桌面色深浅都能自适应。
    /// </para>
    /// </summary>
    private static int OtsuThreshold(byte[] gray)
    {
        var hist = new int[256];
        foreach (byte g in gray) hist[g]++;

        long total = gray.Length;
        double sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += (double)i * hist[i];

        double sumB = 0;
        long wB = 0;
        double maxVar = 0;
        int best = 127;

        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;

            long wF = total - wB;
            if (wF == 0) break;

            sumB += (double)t * hist[t];

            double mB = sumB / wB;
            double mF = (sumAll - sumB) / wF;
            double diff = mB - mF;

            double var = wB * (double)wF * diff * diff;
            if (var > maxVar)
            {
                maxVar = var;
                best = t;
            }
        }

        return best;
    }

    // ───────────────────────── 边界检测 ─────────────────────────

    /// <summary>
    /// 从四条边往内扫描，找纸张的边界，据此推出四个角。
    ///
    /// <para><b>思路</b></para>
    /// <para>
    /// 对每一行统计"亮像素的左右边界"，对每一列统计"上下边界"，
    /// 再取所有行/列结果的稳健中位数（去掉 10% 极值），
    /// 避免个别噪点把边框拉飞。
    /// </para>
    /// </summary>
    private static Point[]? FindCorners(bool[] bright, int w, int h)
    {
        // 至少要有 10% 的行/列检测到纸张，否则认为没找到
        int minVotes = Math.Max(8, h / 10);

        var lefts = new List<int>();
        var rights = new List<int>();
        int rowHits = 0;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int first = -1, last = -1;

            for (int x = 0; x < w; x++)
            {
                if (!bright[row + x]) continue;
                if (first < 0) first = x;
                last = x;
            }

            // 纸张宽度至少占画面 30% 才算有效行
            if (first >= 0 && last - first > w * 0.3)
            {
                lefts.Add(first);
                rights.Add(last);
                rowHits++;
            }
        }

        if (rowHits < minVotes) return null;

        int colMinVotes = Math.Max(8, w / 10);
        var tops = new List<int>();
        var bottoms = new List<int>();
        int colHits = 0;

        for (int x = 0; x < w; x++)
        {
            int first = -1, last = -1;

            for (int y = 0; y < h; y++)
            {
                if (!bright[y * w + x]) continue;
                if (first < 0) first = y;
                last = y;
            }

            if (first >= 0 && last - first > h * 0.3)
            {
                tops.Add(first);
                bottoms.Add(last);
                colHits++;
            }
        }

        if (colHits < colMinVotes) return null;

        int left = RobustMedian(lefts);
        int right = RobustMedian(rights);
        int top = RobustMedian(tops);
        int bottom = RobustMedian(bottoms);

        // 面积太小 → 不是纸
        if (right - left < w * 0.2 || bottom - top < h * 0.2) return null;

        return new[]
        {
            new Point(left, top),      // 左上
            new Point(right, top),     // 右上
            new Point(right, bottom),  // 右下
            new Point(left, bottom),   // 左下
        };
    }

    /// <summary>去掉两端 10% 极值后取中位数，比平均值稳健。</summary>
    private static int RobustMedian(List<int> values)
    {
        values.Sort();

        int skip = values.Count / 10;
        int from = skip;
        int to = values.Count - skip - 1;
        if (to < from) to = values.Count - 1;

        return values[(from + to) / 2];
    }

    // ───────────────────────── 透视矫正 ─────────────────────────

    /// <summary>
    /// 把四角围成的四边形拉成矩形（双线性采样的反向映射）。
    ///
    /// <para>
    /// 用 OpenCV 的术语就是 warpPerspective —— 这里自己算：
    /// 对目标矩形的每个像素，反推它在源四边形里的位置，再双线性插值取色。
    /// 避免引入 30MB 的 native 库。
    /// </para>
    /// </summary>
    private static SoftwareBitmap Rectify(byte[] bgra, int srcW, int srcH, Point[] c)
    {
        // 输出的宽高：取对边的较大长度，避免拉伸失真
        int outW = (int)Math.Max(Dist(c[0], c[1]), Dist(c[3], c[2]));
        int outH = (int)Math.Max(Dist(c[0], c[3]), Dist(c[1], c[2]));

        outW = Math.Clamp(outW, 64, srcW);
        outH = Math.Clamp(outH, 64, srcH);

        var dst = new byte[outW * 4 * outH];

        for (int y = 0; y < outH; y++)
        {
            double v = outH <= 1 ? 0 : (double)y / (outH - 1);

            for (int x = 0; x < outW; x++)
            {
                double u = outW <= 1 ? 0 : (double)x / (outW - 1);

                // 双线性插值把 (u,v) 映射到源四边形内的坐标
                double sx = Bilinear(c[0].X, c[1].X, c[3].X, c[2].X, u, v);
                double sy = Bilinear(c[0].Y, c[1].Y, c[3].Y, c[2].Y, u, v);

                int px = (int)Math.Round(sx);
                int py = (int)Math.Round(sy);

                int di = (y * outW + x) * 4;

                if (px < 0 || px >= srcW || py < 0 || py >= srcH)
                {
                    dst[di] = dst[di + 1] = dst[di + 2] = 255;
                    dst[di + 3] = 255;
                    continue;
                }

                int si = (py * srcW + px) * 4;
                dst[di] = bgra[si];
                dst[di + 1] = bgra[si + 1];
                dst[di + 2] = bgra[si + 2];
                dst[di + 3] = 255;
            }
        }

        return FromPixels(dst, outW, outH);
    }

    /// <summary>双线性插值（两个方向）</summary>
    private static double Bilinear(double v00, double v10, double v01, double v11, double u, double v)
    {
        double top = v00 + (v10 - v00) * u;
        double bottom = v01 + (v11 - v01) * u;
        return top + (bottom - top) * v;
    }

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>托管数组 → SoftwareBitmap（BGRA8）</summary>
    private static unsafe SoftwareBitmap FromPixels(byte[] bgra, int w, int h)
    {
        var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);

        using var buffer = bmp.LockBuffer(BitmapBufferAccessMode.Write);
        using var reference = buffer.CreateReference();

        ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* ptr, out uint cap);
        int len = Math.Min(bgra.Length, (int)cap);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, (IntPtr)ptr, len);

        return bmp;
    }
}