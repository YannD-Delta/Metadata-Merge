using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MetadataMerge.Core;

/// <summary>The reader against a real local web server: it must measure a picture from its first bytes and never pull the whole file.</summary>
public static class ImageReaderTests
{
    private sealed class Server : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        public readonly string Base;
        public readonly List<string> Requests = new List<string>();
        public long BytesSent;
        public Func<string, byte[]> Body;
        public Func<string, bool> HonoursRange = path => true;
        public Func<string, int> DelayMs = path => 0;

        public Server()
        {
            int port = new Random().Next(20000, 40000);
            Base = "http://localhost:" + port + "/";
            listener.Prefixes.Add(Base);
            listener.Start();
            Task.Run(() => Loop());
        }

        private void Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { return; }
                var c = ctx;
                Task.Run(() => Handle(c));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            try
            {
                string range = ctx.Request.Headers["Range"];
                lock (Requests) Requests.Add(path + (range != null ? " [" + range + "]" : ""));
                int delay = DelayMs(path);
                if (delay > 0) Thread.Sleep(delay);
                var body = Body(path);
                if (body == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
                int from = 0, to = body.Length - 1;
                if (range != null && HonoursRange(path))
                {
                    var parts = range.Replace("bytes=", "").Split('-');
                    from = int.Parse(parts[0]); to = Math.Min(body.Length - 1, int.Parse(parts[1]));
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", "bytes " + from + "-" + to + "/" + body.Length);
                }
                int count = to - from + 1;
                ctx.Response.ContentLength64 = count;
                // written in small steps so a client that stops reading stops the transfer
                int sent = 0;
                while (sent < count)
                {
                    int n = Math.Min(16 * 1024, count - sent);
                    ctx.Response.OutputStream.Write(body, from + sent, n);
                    Interlocked.Add(ref BytesSent, n);
                    sent += n;
                }
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
    }

    private static byte[] BigPng(int w, int h)
    {
        // a valid PNG start (signature + IHDR) followed by 3 MB that a client must never need
        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0 };
        var data = new byte[3 * 1024 * 1024];
        Array.Copy(header, data, header.Length);
        return data;
    }

    private static byte[] Bytes(string name) { return ImageHeaderTests.File_(name); }

    public static void A_big_picture_is_measured_from_its_first_bytes_only()
    {
        using (var server = new Server { Body = p => BigPng(3840, 2160) })
        {
            var reader = new ImageReader(5000);
            var info = reader.Read(Item.Image(server.Base + "huge.png"), CancellationToken.None);
            Assert.Equal(3840, info.Width); Assert.Equal(2160, info.Height); Assert.Equal(ImageFormat.Png, info.Format);
            Assert.True(reader.BytesRead <= 16 * 1024, "read " + reader.BytesRead + " bytes");
            Thread.Sleep(300);
            Assert.True(server.BytesSent < 200 * 1024, "the server sent " + server.BytesSent + " bytes of a 3 MB picture");
            Assert.True(server.Requests.Single().Contains("bytes=0-65535"), server.Requests.Single());
        }
    }

    public static void A_server_that_ignores_range_is_still_not_read_to_the_end()
    {
        using (var server = new Server { Body = p => BigPng(1500, 2250), HonoursRange = p => false })
        {
            var reader = new ImageReader(5000);
            var clock = Stopwatch.StartNew();
            var info = reader.Read(Item.Image(server.Base + "x.png"), CancellationToken.None);
            Assert.Equal(1500, info.Width);
            Assert.True(reader.BytesRead <= 16 * 1024, "read " + reader.BytesRead + " bytes");
            Assert.True(clock.ElapsedMilliseconds < 3000, "took " + clock.ElapsedMilliseconds);
        }
    }

    public static void A_jpeg_with_a_big_profile_takes_a_second_wider_request()
    {
        var jpg = new List<byte> { 0xFF, 0xD8 };
        for (int i = 0; i < 2; i++) { jpg.AddRange(new byte[] { 0xFF, 0xE2, 0xFF, 0xFF }); jpg.AddRange(new byte[65533]); }
        jpg.AddRange(Bytes("jpeg-200x100.jpg").Skip(2));
        var body = jpg.ToArray();
        using (var server = new Server { Body = p => body })
        {
            var reader = new ImageReader(5000);
            var info = reader.Read(Item.Image(server.Base + "profile.jpg"), CancellationToken.None);
            Assert.Equal(200, info.Width); Assert.Equal(100, info.Height);
            Assert.Equal(2, server.Requests.Count);
            Assert.True(server.Requests[1].Contains("bytes=0-524287"), server.Requests[1]);
        }
    }

    public static void Small_files_and_all_the_formats_are_measured_over_http()
    {
        var files = new Dictionary<string, byte[]>
        {
            { "/a.png", Bytes("png-120x80.png") }, { "/b.jpg", Bytes("jpeg-200x100.jpg") }, { "/c.gif", Bytes("gif-64x48.gif") }, { "/d.ico", Bytes("ico-48x48.ico") }
        };
        using (var server = new Server { Body = p => files.ContainsKey(p) ? files[p] : null })
        {
            var reader = new ImageReader(5000);
            Assert.Equal(120, reader.Read(Item.Image(server.Base + "a.png"), CancellationToken.None).Width);
            Assert.Equal(200, reader.Read(Item.Image(server.Base + "b.jpg"), CancellationToken.None).Width);
            Assert.Equal(64, reader.Read(Item.Image(server.Base + "c.gif"), CancellationToken.None).Width);
            Assert.Equal(48, reader.Read(Item.Image(server.Base + "d.ico"), CancellationToken.None).Width);
        }
    }

    public static void A_404_a_web_page_and_a_dead_address_are_errors_not_sizes()
    {
        using (var server = new Server { Body = p => p == "/page" ? System.Text.Encoding.ASCII.GetBytes("<html><body>Not an image at all, just a long enough page</body></html>") : null })
        {
            var reader = new ImageReader(3000);
            Assert.Throws<WebException>(() => reader.Read(Item.Image(server.Base + "missing.png"), CancellationToken.None));
            Assert.Throws<InvalidDataException>(() => reader.Read(Item.Image(server.Base + "page"), CancellationToken.None));
        }
        Assert.Throws<WebException>(() => new ImageReader(2000).Read(Item.Image("http://localhost:1/nothing.png"), CancellationToken.None));
    }

    public static void A_slow_server_is_cut_by_the_cancellation()
    {
        using (var server = new Server { Body = p => BigPng(100, 100), DelayMs = p => 10000 })
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(300);
            var clock = Stopwatch.StartNew();
            Assert.Throws<OperationCanceledException>(() => new ImageReader(20000).Read(Item.Image(server.Base + "slow.png"), cts.Token));
            Assert.True(clock.ElapsedMilliseconds < 3000, "took " + clock.ElapsedMilliseconds);
        }
    }

    public static void Local_files_file_addresses_and_bytes_are_measured_without_any_request()
    {
        string path = Path.Combine(ImageHeaderTests.DataDir, "png-120x80.png");
        var reader = new ImageReader(1000);
        Assert.Equal(120, reader.Read(Item.Image(path), CancellationToken.None).Width);
        Assert.Equal(120, reader.Read(Item.Image(new Uri(path).AbsoluteUri), CancellationToken.None).Width);
        Assert.Equal(60, reader.Read(Item.Image(null, Bytes("png-60x90.png"), "cover.png"), CancellationToken.None).Width);
        Assert.Throws<FileNotFoundException>(() => reader.Read(Item.Image(Path.Combine(ImageHeaderTests.DataDir, "nope.png")), CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => reader.Read(Item.Image(null, new byte[100], "x"), CancellationToken.None));
        Assert.True(reader.Read(Item.Image(""), CancellationToken.None) == null);
    }
}
