using System;

namespace MetadataMerge.Core
{
    public enum ImageFormat { Unknown, Jpeg, Png, Gif, WebP, Ico, Bmp }

    public sealed class ImageInfo
    {
        public int Width;
        public int Height;
        public ImageFormat Format;
        /// <summary>Size of the whole file in bytes when it is known (Content-Range of the partial answer, file length, bytes in memory), else 0.</summary>
        public long FileBytes;
        public long Pixels { get { return (long)Width * Height; } }
        public override string ToString() { return Width + "x" + Height + " " + Format.ToString().ToUpperInvariant(); }
    }

    public enum HeaderResult { Ok, NeedMore, Invalid }

    /// <summary>
    /// Reads the pixel size of an image from its first bytes (PNG IHDR, GIF logical screen, JPEG start-of-frame, WebP VP8/VP8L/VP8X, ICO directory,
    /// BMP header), so a picture never has to be downloaded whole to be measured.
    /// </summary>
    public static class ImageHeader
    {
        /// <summary>NeedMore: the bytes so far are a valid start of the format but the size is further on (JPEG with a big profile before the frame).</summary>
        public static HeaderResult TryParse(byte[] d, int length, out ImageInfo info)
        {
            info = null;
            if (d == null) return HeaderResult.Invalid;
            if (length < 4) return HeaderResult.NeedMore;
            if (length > d.Length) length = d.Length;
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return Png(d, length, out info);
            if (d[0] == 0xFF && d[1] == 0xD8) return Jpeg(d, length, out info);
            if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return Gif(d, length, out info);
            if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F') return WebP(d, length, out info);
            if (d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0) return Ico(d, length, out info);
            if (d[0] == 'B' && d[1] == 'M') return Bmp(d, length, out info);
            return HeaderResult.Invalid;
        }

        private static HeaderResult Make(int w, int h, ImageFormat f, out ImageInfo info)
        {
            info = null;
            if (w <= 0 || h <= 0 || w > 100000 || h > 100000) return HeaderResult.Invalid;
            info = new ImageInfo { Width = w, Height = h, Format = f };
            return HeaderResult.Ok;
        }

        private static int Be32(byte[] d, int i) { return (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]; }
        private static int Be16(byte[] d, int i) { return (d[i] << 8) | d[i + 1]; }
        private static int Le16(byte[] d, int i) { return d[i] | (d[i + 1] << 8); }
        private static int Le24(byte[] d, int i) { return d[i] | (d[i + 1] << 8) | (d[i + 2] << 16); }
        private static int Le32(byte[] d, int i) { return d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24); }

        private static HeaderResult Png(byte[] d, int n, out ImageInfo info)
        {
            info = null;
            if (n < 24) return HeaderResult.NeedMore;
            // signature, then the IHDR chunk must come first: length(4) "IHDR" width(4) height(4)
            if (d[4] != 0x0D || d[5] != 0x0A || d[6] != 0x1A || d[7] != 0x0A || d[12] != 'I' || d[13] != 'H' || d[14] != 'D' || d[15] != 'R') return HeaderResult.Invalid;
            return Make(Be32(d, 16), Be32(d, 20), ImageFormat.Png, out info);
        }

        private static HeaderResult Gif(byte[] d, int n, out ImageInfo info)
        {
            info = null;
            if (n < 10) return HeaderResult.NeedMore;
            if (d[3] != '8' || (d[4] != '7' && d[4] != '9') || d[5] != 'a') return HeaderResult.Invalid;
            return Make(Le16(d, 6), Le16(d, 8), ImageFormat.Gif, out info);
        }

        private static HeaderResult Bmp(byte[] d, int n, out ImageInfo info)
        {
            info = null;
            if (n < 26) return HeaderResult.NeedMore;
            int dib = Le32(d, 14);
            if (dib == 12) return Make(Le16(d, 18), Le16(d, 20), ImageFormat.Bmp, out info);
            return Make(Le32(d, 18), Math.Abs(Le32(d, 22)), ImageFormat.Bmp, out info);
        }

        private static HeaderResult Jpeg(byte[] d, int n, out ImageInfo info)
        {
            info = null;
            int i = 2;
            while (true)
            {
                if (i + 4 > n) return HeaderResult.NeedMore;
                if (d[i] != 0xFF) return HeaderResult.Invalid;
                while (i + 1 < n && d[i + 1] == 0xFF) i++;   // fill bytes
                if (i + 4 > n) return HeaderResult.NeedMore;
                int marker = d[i + 1];
                i += 2;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8)) continue;   // markers without a length
                if (marker == 0xD9 || marker == 0xDA) return HeaderResult.Invalid;   // end of image or scan before any frame header
                int len = Be16(d, i);
                if (len < 2) return HeaderResult.Invalid;
                bool frame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (frame)
                {
                    if (i + 7 > n) return HeaderResult.NeedMore;   // length(2) precision(1) height(2) width(2)
                    return Make(Be16(d, i + 5), Be16(d, i + 3), ImageFormat.Jpeg, out info);
                }
                i += len;
            }
        }

        private static HeaderResult WebP(byte[] d, int n, out ImageInfo info)
        {
            info = null;
            if (n < 30) return HeaderResult.NeedMore;
            if (d[8] != 'W' || d[9] != 'E' || d[10] != 'B' || d[11] != 'P') return HeaderResult.Invalid;
            string chunk = "" + (char)d[12] + (char)d[13] + (char)d[14] + (char)d[15];
            if (chunk == "VP8X") return Make(Le24(d, 24) + 1, Le24(d, 27) + 1, ImageFormat.WebP, out info);
            if (chunk == "VP8 ")
            {
                // frame tag(3) start code 9D 01 2A, then 14-bit width and 14-bit height
                if (d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A) return HeaderResult.Invalid;
                return Make(Le16(d, 26) & 0x3FFF, Le16(d, 28) & 0x3FFF, ImageFormat.WebP, out info);
            }
            if (chunk == "VP8L")
            {
                if (d[20] != 0x2F) return HeaderResult.Invalid;
                // signature byte, then width-1 (14 bits) and height-1 (14 bits), little-endian bit order
                uint bits = (uint)(d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24));
                return Make((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, ImageFormat.WebP, out info);
            }
            return HeaderResult.Invalid;
        }

        private static HeaderResult Ico(byte[] d, int n, out ImageInfo info)
        {
            info = null;
            if (n < 6) return HeaderResult.NeedMore;
            int count = Le16(d, 4);
            if (count < 1 || count > 256) return HeaderResult.Invalid;
            if (n < 6 + 16 * count) return HeaderResult.NeedMore;
            int bestW = 0, bestH = 0;
            for (int k = 0; k < count; k++)
            {
                int w = d[6 + 16 * k]; if (w == 0) w = 256;
                int h = d[7 + 16 * k]; if (h == 0) h = 256;
                if ((long)w * h > (long)bestW * bestH) { bestW = w; bestH = h; }
            }
            return Make(bestW, bestH, ImageFormat.Ico, out info);
        }
    }
}
