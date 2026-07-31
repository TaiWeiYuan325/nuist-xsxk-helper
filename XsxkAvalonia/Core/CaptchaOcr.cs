using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace XsxkAvalonia.Core;

/// <summary>
/// ddddocr（common_old.onnx，13.6MB）的 C# 移植：
/// 灰度 → 高度 64 等比缩放（双线性）→ /255 → ONNX → argmax → CTC 解码。
/// 字符集索引 0 为 blank，资产文件 ocr_charset.txt 中索引 i 对应字符 at i-1。
/// </summary>
public static class CaptchaOcr
{
    private static InferenceSession? _session;
    private static string? _charset;
    private static bool[]? _allowed;
    private static string? _inputName;
    private static readonly object _lock = new();

    private static void EnsureLoaded()
    {
        if (_session != null) return;
        lock (_lock)
        {
            if (_session != null) return;
            using (var s = AssetLoader.Open(new Uri("avares://XsxkAvalonia/Assets/ocr_model.onnx")))
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                _session = new InferenceSession(ms.ToArray());
            }
            _inputName = _session.InputMetadata.Keys.First();
            using var sr = new StreamReader(
                AssetLoader.Open(new Uri("avares://XsxkAvalonia/Assets/ocr_charset.txt")));
            _charset = sr.ReadToEnd();
            // 教务验证码只含数字与字母：构建类别掩码，解码时直接屏蔽汉字等类别
            _allowed = new bool[1 + _charset.Length];
            _allowed[0] = true;   // blank
            for (var i = 0; i < _charset.Length; i++)
                _allowed[i + 1] = char.IsAsciiLetterOrDigit(_charset[i]);
        }
    }

    /// <summary>识别验证码图片（PNG/JPG 字节），返回字符序列（可能为空）</summary>
    public static string Recognize(byte[] imageBytes)
    {
        EnsureLoaded();

        // 解码图片 → BGRA 字节
        using var bmp = new Bitmap(new MemoryStream(imageBytes));
        var w = bmp.PixelSize.Width;
        var h = bmp.PixelSize.Height;
        if (w <= 0 || h <= 0) return "";
        var bgra = new byte[w * h * 4];
        unsafe
        {
            fixed (byte* p = bgra)
                bmp.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)p, bgra.Length, w * 4);
        }

        // 灰度（BT.601，/255）
        var gray = new float[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var b = bgra[i * 4];
            var g = bgra[i * 4 + 1];
            var r = bgra[i * 4 + 2];
            gray[i] = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
        }

        // 等比缩放到高 64（双线性插值）
        var tw = Math.Max(1, (int)(w * (64.0 / h)));
        var input = new DenseTensor<float>(new[] { 1, 1, 64, tw });
        for (var y = 0; y < 64; y++)
        {
            var sy = (y + 0.5) * h / 64.0 - 0.5;
            var y0 = (int)Math.Floor(sy);
            var fy = (float)(sy - y0);
            var y0c = Math.Clamp(y0, 0, h - 1);
            var y1c = Math.Clamp(y0 + 1, 0, h - 1);
            for (var x = 0; x < tw; x++)
            {
                var sx = (x + 0.5) * w / (double)tw - 0.5;
                var x0 = (int)Math.Floor(sx);
                var fx = (float)(sx - x0);
                var x0c = Math.Clamp(x0, 0, w - 1);
                var x1c = Math.Clamp(x0 + 1, 0, w - 1);
                var v00 = gray[y0c * w + x0c]; var v01 = gray[y0c * w + x1c];
                var v10 = gray[y1c * w + x0c]; var v11 = gray[y1c * w + x1c];
                input[0, 0, y, x] = (v00 * (1 - fx) + v01 * fx) * (1 - fy)
                                  + (v10 * (1 - fx) + v11 * fx) * fy;
            }
        }

        using var results = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName!, input) });
        var output = results.First().AsTensor<float>();

        // 输出 [1,T,C] 或 [T,1,C]（batch=1）
        int t, c;
        var batchFirst = output.Dimensions.Length != 3 || output.Dimensions[0] == 1;
        if (output.Dimensions.Length == 3)
        {
            t = batchFirst ? output.Dimensions[1] : output.Dimensions[0];
            c = output.Dimensions[2];
        }
        else
        {
            t = output.Dimensions[0];
            c = output.Dimensions[1];
        }

        // CTC 解码：索引层去连续重复 → 跳过 blank(0) → 查字符集
        var sb = new StringBuilder();
        var prev = -1;
        for (var ti = 0; ti < t; ti++)
        {
            var best = 0;
            var bestV = float.MinValue;
            for (var ci = 0; ci < c; ci++)
            {
                if (ci >= _allowed!.Length || !_allowed[ci]) continue;   // 只认数字/字母/blank
                var v = output.Dimensions.Length == 3
                    ? (batchFirst ? output[0, ti, ci] : output[ti, 0, ci])
                    : output[ti, ci];
                if (v > bestV) { bestV = v; best = ci; }
            }
            if (best != prev && best != 0 && best - 1 < _charset!.Length)
                sb.Append(_charset[best - 1]);
            prev = best;
        }
        return sb.ToString();
    }
}
