namespace AirPDF.Services;

/// <summary>
/// Lightweight BGRA preprocessing to improve OCR on scans / low-contrast pages.
/// </summary>
public static class ImagePreprocess
{
    public static byte[] EnhanceForOcr(byte[] bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4 || width < 8 || height < 8)
            return bgra;

        var pixelCount = width * height;
        var luma = new byte[pixelCount];
        var hist = new int[256];

        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var y = (bgra[o + 2] * 30 + bgra[o + 1] * 59 + bgra[o] * 11) / 100;
            luma[i] = (byte)y;
            hist[y]++;
        }

        // Percentile stretch (ignore extremes) then soft binarize around Otsu.
        var lo = Percentile(hist, pixelCount, 0.02);
        var hi = Percentile(hist, pixelCount, 0.98);
        if (hi <= lo + 8)
            return bgra;

        var otsu = OtsuThreshold(hist, pixelCount);
        var output = new byte[bgra.Length];
        var scale = 255.0 / (hi - lo);

        for (var i = 0; i < pixelCount; i++)
        {
            var stretched = (int)Math.Clamp((luma[i] - lo) * scale, 0, 255);
            // Soft threshold: keep gray near edges, crush background/ink for OCR.
            byte v;
            if (stretched < otsu - 18)
                v = 0;
            else if (stretched > otsu + 18)
                v = 255;
            else
                v = (byte)stretched;

            var o = i * 4;
            output[o] = v;
            output[o + 1] = v;
            output[o + 2] = v;
            output[o + 3] = 255;
        }

        return output;
    }

    private static int Percentile(int[] hist, int total, double p)
    {
        var target = (int)(total * p);
        var acc = 0;
        for (var i = 0; i < hist.Length; i++)
        {
            acc += hist[i];
            if (acc >= target)
                return i;
        }

        return 255;
    }

    private static int OtsuThreshold(int[] hist, int total)
    {
        double sum = 0;
        for (var i = 0; i < 256; i++)
            sum += i * hist[i];

        double sumB = 0;
        var wB = 0;
        double max = 0;
        var threshold = 128;

        for (var t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0)
                continue;
            var wF = total - wB;
            if (wF == 0)
                break;

            sumB += t * hist[t];
            var mB = sumB / wB;
            var mF = (sum - sumB) / wF;
            var between = wB * (double)wF * (mB - mF) * (mB - mF);
            if (between > max)
            {
                max = between;
                threshold = t;
            }
        }

        return threshold;
    }
}
