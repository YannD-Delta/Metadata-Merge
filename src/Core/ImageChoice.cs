using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace MetadataMerge.Core
{
    /// <summary>What to do with images larger than the maximum size.</summary>
    public enum AboveCapMode { Smallest = 0, Reject = 1, Largest = 2 }

    /// <summary>What to do with an image whose size could not be read.</summary>
    public enum UnknownSizeMode { UseFirst = 0, Reject = 1 }

    /// <summary>The limits and preferences of one image field. Sizes in pixels, 0 = no limit.</summary>
    public class ImageSettings
    {
        /// <summary>Smaller images are ignored (thumbnails).</summary>
        public int MinWidth { get; set; }
        public int MinHeight { get; set; }
        /// <summary>Larger images are only used when nothing else fits: the smallest one is then taken.</summary>
        public int MaxWidth { get; set; }
        public int MaxHeight { get; set; }
        /// <summary>How far the width/height ratio may be from the expected one (cover 2:3, background 16:9, icon 1:1), in percent.</summary>
        public int AspectTolerancePercent { get; set; } = 15;
        /// <summary>Sizes closer than this (percent of pixels) count as comparable: the format preference then decides.</summary>
        public int ComparablePercent { get; set; } = 15;
        /// <summary>Smaller pictures (width x height) are ignored, 0 = no limit.</summary>
        public int MinPixels { get; set; }
        /// <summary>Files larger than this many KB are ignored when their size is known (decode cost and disk space), 0 = no limit.</summary>
        public int MaxFileKb { get; set; }
        /// <summary>The expected width / height as "2:3", "16:9" or "0.67"; empty = the default of the field (cover 2:3, background 16:9, icon 1:1).</summary>
        public string ExpectedRatio { get; set; } = "";
        /// <summary>On: a picture with the wrong proportions is rejected. Off: it is only used when no picture has the right ones.</summary>
        public bool StrictAspect { get; set; } = true;
        public AboveCapMode AboveCap { get; set; } = AboveCapMode.Smallest;
        public UnknownSizeMode UnknownSize { get; set; } = UnknownSizeMode.UseFirst;
        /// <summary>Formats from most to least wanted. JPEG first: it decodes faster than PNG, and much faster than WebP, in WPF.</summary>
        public string FormatPreference { get; set; } = "jpeg,png,webp,gif,ico,bmp";

        public static ImageSettings DefaultsFor(Field f)
        {
            switch (f)
            {
                case Field.CoverImage: return new ImageSettings { MinWidth = 300, MinHeight = 450, MaxWidth = 1000, MaxHeight = 1500 };
                case Field.BackgroundImage: return new ImageSettings { MinWidth = 1280, MinHeight = 720, MaxWidth = 3840, MaxHeight = 2160 };
                case Field.Icon: return new ImageSettings { MinWidth = 64, MinHeight = 64, MaxWidth = 512, MaxHeight = 512 };
            }
            return new ImageSettings();
        }

        public ImageSettings Clone() { return (ImageSettings)MemberwiseClone(); }

        public void Clamp()
        {
            MinWidth = Math.Max(0, Math.Min(20000, MinWidth)); MinHeight = Math.Max(0, Math.Min(20000, MinHeight));
            MaxWidth = Math.Max(0, Math.Min(20000, MaxWidth)); MaxHeight = Math.Max(0, Math.Min(20000, MaxHeight));
            AspectTolerancePercent = Math.Max(0, Math.Min(100, AspectTolerancePercent));
            ComparablePercent = Math.Max(0, Math.Min(100, ComparablePercent));
            if (FormatPreference == null) FormatPreference = "";
            MinPixels = Math.Max(0, MinPixels);
            MaxFileKb = Math.Max(0, Math.Min(1000000, MaxFileKb));
            if (ExpectedRatio == null) ExpectedRatio = "";
        }

        /// <summary>"2:3", "16/9", "1.78" or "0,67" to a number; false when it is not a positive ratio.</summary>
        public static bool TryParseRatio(string text, out double ratio)
        {
            ratio = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim().Replace(',', '.');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = t.Split(new[] { ':', '/', 'x', 'X' });
            double a, b;
            if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out a) && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out b) && a > 0 && b > 0) { ratio = a / b; return true; }
            if (parts.Length == 1 && double.TryParse(t, System.Globalization.NumberStyles.Float, inv, out a) && a > 0) { ratio = a; return true; }
            return false;
        }

        /// <summary>The expected width / height for the field: the setting when it is valid, else the default of the field.</summary>
        public double ExpectedFor(Field f)
        {
            double r;
            return TryParseRatio(ExpectedRatio, out r) ? r : Fields.ExpectedAspect(f);
        }

        /// <summary>The preference as a rank per format (lower = better); formats that are not listed come last.</summary>
        public Dictionary<ImageFormat, int> FormatRanks()
        {
            var ranks = new Dictionary<ImageFormat, int>();
            foreach (string raw in (FormatPreference ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim().ToLowerInvariant();
                ImageFormat f;
                switch (name)
                {
                    case "jpeg": case "jpg": f = ImageFormat.Jpeg; break;
                    case "png": f = ImageFormat.Png; break;
                    case "webp": f = ImageFormat.WebP; break;
                    case "gif": f = ImageFormat.Gif; break;
                    case "ico": f = ImageFormat.Ico; break;
                    case "bmp": f = ImageFormat.Bmp; break;
                    default: continue;
                }
                if (!ranks.ContainsKey(f)) ranks[f] = ranks.Count;
            }
            return ranks;
        }
    }

    /// <summary>One image a source offered, with its measured size when it could be read.</summary>
    public sealed class ImageCandidate
    {
        public string SourceId;
        public string SourceName;
        /// <summary>Position of the source in the priority order (0 = first).</summary>
        public int Rank;
        public Item Item;
        public ImageInfo Info;
        /// <summary>Why the size could not be read.</summary>
        public string Problem;
        public string Verdict = "";

        public string Describe() { return Info != null ? Info.ToString() : "size unknown"; }
    }

    /// <summary>The choice itself: no I/O, candidates in, one winner out.</summary>
    public static class ImageChooser
    {
        public static ImageDecision Choose(Field field, string game, List<ImageCandidate> candidates, ImageSettings s)
        {
            var d = new ImageDecision { Field = field, GameName = game, Candidates = candidates };
            if (candidates.Count == 0) { d.Reason = "no source offered an image"; return d; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double expected = s.ExpectedFor(field);
            double tol = s.AspectTolerancePercent / 100.0;
            var ranks = s.FormatRanks();

            var good = new List<ImageCandidate>();      // acceptable and with the right proportions
            var offAspect = new List<ImageCandidate>(); // acceptable but with other proportions (only when the aspect is not strict)
            foreach (var c in candidates)
            {
                if (c.Info == null) { c.Verdict = "size unknown" + (c.Problem != null ? " (" + c.Problem + ")" : ""); continue; }
                var i = c.Info;
                double ratio = (double)i.Width / i.Height;
                bool wrongAspect = Math.Abs(ratio / expected - 1.0) > tol;
                if (i.Width < s.MinWidth || i.Height < s.MinHeight) c.Verdict = "rejected: smaller than " + s.MinWidth + "x" + s.MinHeight;
                else if (s.MinPixels > 0 && i.Pixels < s.MinPixels) c.Verdict = "rejected: fewer than " + s.MinPixels + " pixels";
                else if (s.MaxFileKb > 0 && i.FileBytes > (long)s.MaxFileKb * 1024) c.Verdict = "rejected: file of " + i.FileBytes / 1024 + " KB, above " + s.MaxFileKb + " KB";
                else if (wrongAspect && s.StrictAspect) c.Verdict = "rejected: ratio " + ratio.ToString("0.00", inv) + " instead of about " + expected.ToString("0.00", inv);
                else if (wrongAspect) { c.Verdict = "acceptable, ratio " + ratio.ToString("0.00", inv) + " is off"; offAspect.Add(c); }
                else { c.Verdict = "acceptable"; good.Add(c); }
            }
            var acceptable = good.Count > 0 ? good : offAspect;
            foreach (var c in offAspect) if (good.Count > 0) c.Verdict = "not chosen: other proportions while some have the right ones";

            if (acceptable.Count == 0)
            {
                var unknown = s.UnknownSize == UnknownSizeMode.UseFirst ? candidates.Where(c => c.Info == null).OrderBy(c => c.Rank).FirstOrDefault() : null;
                if (unknown != null)
                {
                    d.Winner = unknown;
                    d.Reason = candidates.Any(c => c.Info != null) ? "every measured image was rejected: the first one whose size is unknown is used" : "no size could be read: the first image is used";
                    unknown.Verdict = "chosen (size unknown)";
                    return d;
                }
                d.Reason = "every image was rejected";
                return d;
            }

            bool capped = (s.MaxWidth > 0 || s.MaxHeight > 0) && s.AboveCap != AboveCapMode.Largest;
            Func<ImageCandidate, bool> inside = c => (s.MaxWidth <= 0 || c.Info.Width <= s.MaxWidth) && (s.MaxHeight <= 0 || c.Info.Height <= s.MaxHeight);
            var within = capped ? acceptable.Where(inside).ToList() : acceptable;
            Func<ImageCandidate, int> formatRank = c => ranks.ContainsKey(c.Info.Format) ? ranks[c.Info.Format] : int.MaxValue;
            double slack = s.ComparablePercent / 100.0;
            ImageCandidate winner;
            string why;
            if (within.Count > 0)
            {
                long best = within.Max(c => c.Info.Pixels);
                var comparable = within.Where(c => c.Info.Pixels >= best * (1.0 - slack)).ToList();
                winner = comparable.OrderBy(formatRank).ThenByDescending(c => c.Info.Pixels).ThenBy(c => c.Rank).First();
                bool biggest = winner.Info.Pixels == best;
                string scope = capped ? "largest within the cap" : "largest";
                why = comparable.Count > 1 && !biggest ? "comparable size, preferred format"
                    : comparable.Count > 1 ? scope + ", preferred format among comparable sizes"
                    : scope;
                foreach (var c in within) if (c != winner && c.Verdict.StartsWith("acceptable")) c.Verdict = "acceptable, not chosen";
                foreach (var c in acceptable.Except(within)) c.Verdict = "above the cap " + s.MaxWidth + "x" + s.MaxHeight + ", not needed";
            }
            else if (s.AboveCap == AboveCapMode.Reject)
            {
                foreach (var c in acceptable) c.Verdict = "rejected: above the cap " + s.MaxWidth + "x" + s.MaxHeight;
                d.Reason = "every image is above the cap and larger ones are refused";
                return d;
            }
            else
            {
                long smallest = acceptable.Min(c => c.Info.Pixels);
                var comparable = acceptable.Where(c => c.Info.Pixels <= smallest * (1.0 + slack)).ToList();
                winner = comparable.OrderBy(formatRank).ThenBy(c => c.Info.Pixels).ThenBy(c => c.Rank).First();
                why = "everything is above the cap " + s.MaxWidth + "x" + s.MaxHeight + ": the smallest one is used";
                foreach (var c in acceptable) if (c != winner) c.Verdict = "above the cap, not chosen";
            }
            winner.Verdict = "chosen";
            foreach (var c in candidates) if (c.Info == null && c != winner) c.Verdict = "size unknown, not needed";
            d.Winner = winner;
            d.Reason = why;
            return d;
        }
    }

    public interface IImageReader
    {
        /// <summary>The size of the image, or null when it cannot be read. Reads as little as possible; throws on failure or cancellation.</summary>
        ImageInfo Read(Item image, CancellationToken cancel);
    }

    /// <summary>
    /// Measures an image from its first bytes only: the bytes a source already holds, the start of a local file, or the start of a web address
    /// (a Range request for the first 64 KB, more only when the size is further on, and the connection is dropped as soon as the size is known).
    /// </summary>
    public sealed class ImageReader : IImageReader
    {
        private const int MaxRead = 512 * 1024;
        private readonly int firstRange;
        private readonly int timeoutMs;

        /// <param name="firstRangeKb">Bytes asked in the first request, in KB; a second request of 512 KB follows only when the size is further on.</param>
        public ImageReader(int timeoutMs = 20000, int firstRangeKb = 64)
        {
            this.timeoutMs = timeoutMs;
            firstRange = Math.Max(8, Math.Min(512, firstRangeKb)) * 1024;
        }

        /// <summary>Bytes pulled over the network by this reader (for the tests and the log).</summary>
        public long BytesRead;

        public ImageInfo Read(Item image, CancellationToken cancel)
        {
            if (image.Content != null && image.Content.Length > 0)
            {
                var own = Parse(image.Content, image.Content.Length);
                own.FileBytes = image.Content.Length;
                return own;
            }
            string v = (image.Value ?? "").Trim();
            if (v.Length == 0) return null;
            if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return ReadWeb(v, cancel);
            if (v.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) { try { v = new Uri(v).LocalPath; } catch { return null; } }
            return ReadFile(v, cancel);
        }

        private static ImageInfo Parse(byte[] data, int length)
        {
            ImageInfo info;
            var r = ImageHeader.TryParse(data, length, out info);
            if (r == HeaderResult.Ok) return info;
            throw new InvalidDataException(r == HeaderResult.NeedMore ? "the file ends before its size" : "not a PNG, JPEG, GIF, WebP, ICO or BMP file");
        }

        private ImageInfo ReadFile(string path, CancellationToken cancel)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("file not found", path);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var info = ReadProgressive(fs, cancel, MaxRead) ?? Fail(MaxRead);
                info.FileBytes = fs.Length;
                return info;
            }
        }

        private static ImageInfo Fail(int limit) { throw new InvalidDataException("the size was not found in the first " + limit / 1024 + " KB"); }

        /// <summary>The first 64 KB nearly always hold the size; a second request goes further only for a JPEG with a big profile before its frame.</summary>
        private ImageInfo ReadWeb(string url, CancellationToken cancel)
        {
            return ReadWebRange(url, firstRange, cancel) ?? ReadWebRange(url, Math.Max(MaxRead, firstRange), cancel) ?? Fail(MaxRead);
        }

        private ImageInfo ReadWebRange(string url, int limit, CancellationToken cancel)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.UserAgent = "Playnite-MetadataMerge/0.2";
            request.AutomaticDecompression = DecompressionMethods.None;
            request.AddRange(0, limit - 1);
            using (cancel.Register(() => { try { request.Abort(); } catch { } }))
            {
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    {
                        var info = ReadProgressive(stream, cancel, limit);
                        if (info != null) info.FileBytes = TotalBytes(response);
                        // dropped here without reading the rest: the winner is downloaded later, by Playnite, once
                        request.Abort();
                        return info;
                    }
                }
                catch (WebException ex) when (cancel.IsCancellationRequested) { throw new OperationCanceledException("cancelled", ex, cancel); }
            }
        }

        /// <summary>The size of the whole file: the total of a Content-Range ("bytes 0-65535/2256284"), or the length of a full answer.</summary>
        private static long TotalBytes(HttpWebResponse response)
        {
            string range = response.Headers["Content-Range"];
            if (!string.IsNullOrEmpty(range))
            {
                int slash = range.LastIndexOf('/');
                long total;
                if (slash >= 0 && long.TryParse(range.Substring(slash + 1), out total)) return total;
            }
            return response.StatusCode == HttpStatusCode.OK && response.ContentLength > 0 ? response.ContentLength : 0;
        }

        /// <summary>Reads chunk by chunk and stops as soon as the header gives the size; null when the limit is reached without it.</summary>
        private ImageInfo ReadProgressive(Stream stream, CancellationToken cancel, int limit)
        {
            var buffer = new byte[limit];
            int total = 0;
            while (total < limit)
            {
                cancel.ThrowIfCancellationRequested();
                int n = stream.Read(buffer, total, Math.Min(8 * 1024, limit - total));
                if (n <= 0) break;
                total += n;
                Interlocked.Add(ref BytesRead, n);
                ImageInfo info;
                var r = ImageHeader.TryParse(buffer, total, out info);
                if (r == HeaderResult.Ok) return info;
                if (r == HeaderResult.Invalid) throw new InvalidDataException("not a PNG, JPEG, GIF, WebP, ICO or BMP file");
            }
            if (total < limit) throw new InvalidDataException("the file ends before its size");
            return null;
        }
    }
}
