using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetadataMerge.Core;

public static class ImageHeaderTests
{
    public static string DataDir { get { return Path.Combine(Environment.GetEnvironmentVariable("METADATAMERGE_REPO") ?? ".", "tests", "data", "img"); } }
    public static byte[] File_(string name) { return File.ReadAllBytes(Path.Combine(DataDir, name)); }

    private static ImageInfo Ok(byte[] data, int length = -1)
    {
        ImageInfo info;
        var r = ImageHeader.TryParse(data, length < 0 ? data.Length : length, out info);
        Assert.Equal(HeaderResult.Ok, r);
        return info;
    }

    private static void Is(ImageInfo info, int w, int h, ImageFormat f)
    {
        Assert.Equal(w, info.Width); Assert.Equal(h, info.Height); Assert.Equal(f, info.Format);
    }

    public static void Png_is_read_from_the_ihdr_chunk() { Is(Ok(File_("png-120x80.png")), 120, 80, ImageFormat.Png); Is(Ok(File_("png-60x90.png")), 60, 90, ImageFormat.Png); }
    public static void Jpeg_is_read_from_the_start_of_frame() { Is(Ok(File_("jpeg-200x100.jpg")), 200, 100, ImageFormat.Jpeg); }
    public static void Gif_is_read_from_the_logical_screen() { Is(Ok(File_("gif-64x48.gif")), 64, 48, ImageFormat.Gif); }
    public static void Bmp_is_read_from_the_dib_header() { Is(Ok(File_("bmp-40x30.bmp")), 40, 30, ImageFormat.Bmp); }
    public static void Ico_is_read_from_the_directory() { Is(Ok(File_("ico-48x48.ico")), 48, 48, ImageFormat.Ico); }

    public static void Only_the_first_bytes_are_needed()
    {
        // a real PNG and JPEG are read from their first 40 bytes
        Is(Ok(File_("png-120x80.png"), 40), 120, 80, ImageFormat.Png);
        Is(Ok(File_("jpeg-200x100.jpg"), 700), 200, 100, ImageFormat.Jpeg);
    }

    private static byte[] BigProfileJpeg()
    {
        // two 64 KB APP2 profiles (like a colour profile or XMP) before the frame header
        var jpg = File_("jpeg-200x100.jpg");
        var list = new List<byte> { 0xFF, 0xD8 };
        for (int i = 0; i < 2; i++) { list.AddRange(new byte[] { 0xFF, 0xE2, 0xFF, 0xFF }); list.AddRange(new byte[65533]); }
        list.AddRange(jpg.Skip(2));
        return list.ToArray();
    }

    public static void A_jpeg_with_a_big_profile_needs_more_than_the_first_64_kb()
    {
        var data = BigProfileJpeg();
        ImageInfo info;
        Assert.Equal(HeaderResult.NeedMore, ImageHeader.TryParse(data, 64 * 1024, out info));
        Is(Ok(data), 200, 100, ImageFormat.Jpeg);
    }

    public static void Progressive_and_arithmetic_jpegs_are_read_too()
    {
        foreach (byte sof in new byte[] { 0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC9, 0xCA, 0xCF })
        {
            var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xFF, 0xFF, sof, 0x00, 0x11, 0x08, 0x04, 0x38, 0x07, 0x80, 0x03 };
            Is(Ok(data), 1920, 1080, ImageFormat.Jpeg);
        }
    }

    public static void Jpeg_markers_that_are_not_frames_are_skipped()
    {
        // DHT (C4), JPG (C8) and DAC (CC) look like frame markers but are not
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xC4, 0x00, 0x03, 0x00, 0xFF, 0xC8, 0x00, 0x02, 0xFF, 0xCC, 0x00, 0x02, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x20, 0x00, 0x40, 0x03 };
        Is(Ok(data), 64, 32, ImageFormat.Jpeg);
    }

    // real encoder output, 1x1 (the standard feature-detection files of the WebP project and Modernizr)
    private static readonly string[] RealWebP =
    {
        "UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEADsD+JaQAA3AAAAAA",
        "UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==",
        "UklGRkoAAABXRUJQVlA4WAoAAAAQAAAAAAAAAAAAQUxQSAwAAAARBxAR/Q9ERP8DAABWUDggGAAAABQBAJ0BKgEAAQAAAP4AAA3AAP7mtQAAAA=="
    };

    public static void Real_lossy_lossless_and_extended_webp_files_are_read()
    {
        foreach (var b64 in RealWebP) Is(Ok(Convert.FromBase64String(b64)), 1, 1, ImageFormat.WebP);
    }

    public static void The_header_of_a_real_extended_webp_gives_its_canvas_size()
    {
        var data = File_("webp-vp8x-header.bin");
        var info = Ok(data);
        Assert.Equal(ImageFormat.WebP, info.Format);
        Assert.True(info.Width > 100 && info.Height > 100, info.ToString());
    }

    private static byte[] Riff(string chunk, byte[] payload)
    {
        var list = new List<byte>();
        list.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF")); list.AddRange(BitConverter.GetBytes(4 + 8 + payload.Length));
        list.AddRange(System.Text.Encoding.ASCII.GetBytes("WEBP")); list.AddRange(System.Text.Encoding.ASCII.GetBytes(chunk));
        list.AddRange(BitConverter.GetBytes(payload.Length)); list.AddRange(payload);
        return list.ToArray();
    }

    public static void Webp_sizes_beyond_one_byte_are_decoded_for_the_three_layouts()
    {
        // VP8X: canvas width-1 and height-1 on 24 bits
        var vp8x = new byte[18]; int w = 3840 - 1, h = 2160 - 1;
        vp8x[4] = (byte)w; vp8x[5] = (byte)(w >> 8); vp8x[6] = (byte)(w >> 16); vp8x[7] = (byte)h; vp8x[8] = (byte)(h >> 8); vp8x[9] = (byte)(h >> 16);
        Is(Ok(Riff("VP8X", vp8x)), 3840, 2160, ImageFormat.WebP);
        // VP8 (lossy): 3-byte frame tag, start code, 14-bit width and height (the top two bits are scaling)
        var vp8 = new byte[] { 0x10, 0x00, 0x00, 0x9D, 0x01, 0x2A, 0x80, 0x02, 0xE0, 0x01, 0, 0 };
        Is(Ok(Riff("VP8 ", vp8)), 640, 480, ImageFormat.WebP);
        // VP8L (lossless): 0x2F, then width-1 and height-1 on 14 bits each
        uint bits = (uint)(1000 - 1) | ((uint)(1500 - 1) << 14);
        var vp8l = new byte[] { 0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24), 0, 0, 0, 0, 0 };
        Is(Ok(Riff("VP8L", vp8l)), 1000, 1500, ImageFormat.WebP);
    }

    public static void An_ico_with_several_sizes_gives_the_largest()
    {
        var data = new byte[6 + 16 * 3];
        data[2] = 1; data[4] = 3;
        Action<int, int, int> entry = (k, ew, eh) => { data[6 + 16 * k] = (byte)ew; data[7 + 16 * k] = (byte)eh; };
        entry(0, 16, 16); entry(1, 0, 0); entry(2, 48, 48);   // 0 means 256
        Is(Ok(data), 256, 256, ImageFormat.Ico);
    }

    public static void Too_few_bytes_ask_for_more_and_garbage_is_refused()
    {
        ImageInfo info;
        var png = File_("png-120x80.png");
        Assert.Equal(HeaderResult.NeedMore, ImageHeader.TryParse(png, 10, out info));
        Assert.Equal(HeaderResult.NeedMore, ImageHeader.TryParse(png, 2, out info));
        Assert.Equal(HeaderResult.Invalid, ImageHeader.TryParse(System.Text.Encoding.ASCII.GetBytes("<html><body>404 Not Found</body></html>"), 40, out info));
        Assert.Equal(HeaderResult.Invalid, ImageHeader.TryParse(new byte[64], 64, out info));
        Assert.Equal(HeaderResult.Invalid, ImageHeader.TryParse(null, 0, out info));
        Assert.True(info == null);
    }

    public static void A_zero_or_absurd_size_is_refused()
    {
        var png = File_("png-120x80.png").ToArray();
        for (int i = 16; i < 20; i++) png[i] = 0;   // width 0
        ImageInfo info;
        Assert.Equal(HeaderResult.Invalid, ImageHeader.TryParse(png, png.Length, out info));
    }

    public static void No_prefix_of_any_file_and_no_random_bytes_can_make_the_parser_throw()
    {
        var files = Directory.GetFiles(DataDir).Select(File.ReadAllBytes).Concat(RealWebP.Select(Convert.FromBase64String)).Concat(new[] { BigProfileJpeg() }).ToList();
        ImageInfo info;
        foreach (var f in files)
            for (int n = 0; n <= Math.Min(f.Length, 3000); n++)
            {
                var r = ImageHeader.TryParse(f, n, out info);
                Assert.True(r != HeaderResult.Ok || (info != null && info.Width > 0 && info.Height > 0), "prefix " + n);
            }
        var rnd = new Random(12345);
        foreach (var f in files.Take(8))
            for (int round = 0; round < 300; round++)
            {
                var copy = f.Take(200).ToArray();
                for (int k = 0; k < 6; k++) copy[rnd.Next(copy.Length)] = (byte)rnd.Next(256);
                ImageHeader.TryParse(copy, copy.Length, out info);
            }
        for (int round = 0; round < 2000; round++)
        {
            var junk = new byte[rnd.Next(0, 80)]; rnd.NextBytes(junk);
            if (junk.Length > 4 && round % 3 == 0) { junk[0] = 0xFF; junk[1] = 0xD8; }
            if (junk.Length > 12 && round % 3 == 1) { junk[0] = (byte)'R'; junk[1] = (byte)'I'; junk[2] = (byte)'F'; junk[3] = (byte)'F'; junk[8] = (byte)'W'; junk[9] = (byte)'E'; junk[10] = (byte)'B'; junk[11] = (byte)'P'; }
            ImageHeader.TryParse(junk, junk.Length, out info);
        }
    }
}
