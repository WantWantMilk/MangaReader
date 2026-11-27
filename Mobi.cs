// Mobi.cs - PalmDB / MOBI / AZW3 (KF8) parsing and page extraction.
// Part of Manga Reader.  C# 5 / .NET Framework 4.8, no external dependencies.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MangaReaderApp
{
    /// <summary>One rendered page (image) of a book.</summary>
    public class PageEntry
    {
        public int Index;          // global page number, 0 based
        public int ChapterIndex;   // which source file this page came from
        public string FilePath;    // extracted image on disk
        public int Width;          // natural pixel size
        public int Height;
        public double Aspect { get { return Width > 0 ? (double)Height / Width : 1.4; } }
    }

    /// <summary>One opened file; several files can be chained into one book.</summary>
    public class ChapterInfo
    {
        public string Title;
        public string SourcePath;
        public int FirstPage;
        public int PageCount;
    }

    public class Book
    {
        public string Title = "";
        public List<PageEntry> Pages = new List<PageEntry>();
        public List<ChapterInfo> Chapters = new List<ChapterInfo>();
    }

    // =====================================================================
    //  MOBI container
    // =====================================================================
    public class MobiFile : IDisposable
    {
        private FileStream _fs;
        private long _fileLen;
        private int _numRecords;
        private long[] _offsets;
        private long[] _lengths;

        public string Title = "";
        public string Author = "";
        public int Version = 6;
        public int TextEncoding = 65001;
        public int EncryptionType;

        /// <summary>Record numbers that hold a page image, in reading order.</summary>
        public List<int> ImageRecords = new List<int>();
        public Dictionary<int, string> ImageExt = new Dictionary<int, string>();
        public int CoverOrdinal = -1;

        public MobiFile(string path)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { Parse(); }
            catch { Dispose(); throw; }
        }

        // ---------------- low level helpers ----------------
        private static int BE16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }

        private static long BE32(byte[] b, int o)
        {
            return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
        }

        private static long LE32(byte[] b, int o)
        {
            return ((long)b[o + 3] << 24) | ((long)b[o + 2] << 16) | ((long)b[o + 1] << 8) | b[o];
        }

        private static string Ascii(byte[] b, int o, int n)
        {
            if (b == null || o < 0 || o + n > b.Length) return "";
            char[] c = new char[n];
            for (int i = 0; i < n; i++) c[i] = (char)b[o + i];
            return new string(c);
        }

        private void ReadAt(long pos, byte[] buf, int count)
        {
            if (count <= 0) return;
            _fs.Position = pos;
            int done = 0;
            while (done < count)
            {
                int n = _fs.Read(buf, done, count - done);
                if (n <= 0) break;
                done += n;
            }
            if (done < count) Array.Clear(buf, done, count - done);
        }

        public byte[] ReadRecord(int i)
        {
            if (i < 0 || i >= _numRecords) return new byte[0];
            long len = _lengths[i];
            if (len <= 0) return new byte[0];
            if (len > 512L * 1024 * 1024) len = 512L * 1024 * 1024;
            byte[] buf = new byte[(int)len];
            ReadAt(_offsets[i], buf, buf.Length);
            return buf;
        }

        private byte[] ReadRecordHead(int i, int n)
        {
            byte[] buf = new byte[n];
            if (i < 0 || i >= _numRecords) return buf;
            long len = Math.Min((long)n, _lengths[i]);
            ReadAt(_offsets[i], buf, (int)len);
            return buf;
        }

        // ---------------- header parsing ----------------
        private void Parse()
        {
            _fileLen = _fs.Length;
            if (_fileLen < 100) throw new InvalidDataException("文件太小，不是有效的 MOBI 文件。");

            byte[] hdr = new byte[78];
            ReadAt(0, hdr, 78);
            _numRecords = BE16(hdr, 76);
            if (_numRecords <= 0 || _numRecords > 200000)
                throw new InvalidDataException("MOBI 记录数异常（" + _numRecords + "）。");

            byte[] ri = new byte[_numRecords * 8];
            ReadAt(78, ri, ri.Length);
            _offsets = new long[_numRecords];
            _lengths = new long[_numRecords];
            for (int i = 0; i < _numRecords; i++) _offsets[i] = BE32(ri, i * 8);
            for (int i = 0; i < _numRecords; i++)
            {
                long s = _offsets[i];
                long e = (i + 1 < _numRecords) ? _offsets[i + 1] : _fileLen;
                if (e > _fileLen) e = _fileLen;
                if (e < s) e = s;
                _lengths[i] = e - s;
            }

            byte[] r0 = ReadRecord(0);
            if (r0.Length < 120) throw new InvalidDataException("MOBI 记录 0 损坏。");
            if (Ascii(r0, 16, 4) != "MOBI")
                throw new InvalidDataException("这不是 MOBI / AZW3 文件（缺少 MOBI 头）。");

            int mobiLen = (int)BE32(r0, 20);
            if (mobiLen < 24) mobiLen = 24;
            if (16 + mobiLen > r0.Length) mobiLen = r0.Length - 16;

            Version = (int)BE32(r0, 36);
            TextEncoding = (int)BE32(r0, 28);
            EncryptionType = BE16(r0, 12);
            int firstImage = (int)BE32(r0, 16 + 0x5C);
            int nameOff = (int)BE32(r0, 16 + 0x44);
            int nameLen = (int)BE32(r0, 16 + 0x48);

            Dictionary<int, byte[]> exth = ReadExth(r0, 16 + mobiLen);

            if (nameOff > 16 && nameLen > 0 && nameOff + nameLen <= r0.Length)
                Title = DecodeText(r0, nameOff, nameLen);
            byte[] v;
            if (exth.TryGetValue(503, out v)) Title = Utf8(v);
            if (exth.TryGetValue(100, out v)) Author = Utf8(v);

            int boundary = -1;
            if (exth.TryGetValue(121, out v) && v.Length >= 4) boundary = (int)BE32(v, 0);

            bool usedKf8 = false;
            int scanEnd = _numRecords;
            if (boundary > 0 && boundary < _numRecords)
            {
                byte[] rb = ReadRecord(boundary);
                if (rb.Length > 120 && Ascii(rb, 16, 4) == "MOBI")
                {
                    int fi8 = (int)BE32(rb, 16 + 0x5C);
                    int v8 = (int)BE32(rb, 36);
                    if (v8 >= 8 && fi8 >= boundary && fi8 < _numRecords)
                    {
                        firstImage = fi8;
                        usedKf8 = true;
                    }
                }
                // Older MOBI part and KF8 part each own their images: never
                // walk across the boundary or every page would appear twice.
                if (!usedKf8 && firstImage < boundary) scanEnd = boundary;
            }

            CollectImages(firstImage, scanEnd);

            // Fall back to the KF8 section when the first section had no images.
            if (ImageRecords.Count == 0 && boundary > 0 && boundary < _numRecords)
            {
                byte[] rb = ReadRecord(boundary);
                if (rb.Length > 120 && Ascii(rb, 16, 4) == "MOBI")
                {
                    int fi8 = (int)BE32(rb, 16 + 0x5C);
                    CollectImages(fi8, _numRecords);
                }
            }

            // Last resort: a few tools leave the "first image index" field at
            // zero, so sweep the whole file for image records.
            if (ImageRecords.Count == 0) CollectImages(1, _numRecords);

            // Cover pages are addressed relative to the first image record.
            if (exth.TryGetValue(201, out v) && v.Length >= 4)
            {
                int coverRecord = firstImage + (int)BE32(v, 0);
                int ord = ImageRecords.IndexOf(coverRecord);
                if (ord >= 0) CoverOrdinal = ord;
            }
        }

        private void CollectImages(int from, int to)
        {
            if (from < 1) from = 1;
            if (to > _numRecords) to = _numRecords;
            for (int i = from; i < to; i++)
            {
                byte[] head = ReadRecordHead(i, 16);
                string ext = DetectImageExt(head);
                if (ext == null) continue;
                ImageRecords.Add(i);
                ImageExt[i] = ext;
            }
        }

        private static Dictionary<int, byte[]> ReadExth(byte[] r0, int off)
        {
            Dictionary<int, byte[]> map = new Dictionary<int, byte[]>();
            if (off < 0 || off + 12 > r0.Length) return map;
            if (Ascii(r0, off, 4) != "EXTH") return map;
            long exthLen = BE32(r0, off + 4);
            int count = (int)BE32(r0, off + 8);
            long limit = off + Math.Max(exthLen, 12L);
            if (limit > r0.Length) limit = r0.Length;
            int p = off + 12;
            for (int i = 0; i < count && p + 8 <= limit; i++)
            {
                int type = (int)BE32(r0, p);
                int rl = (int)BE32(r0, p + 4);
                if (rl < 8 || p + rl > limit) break;
                byte[] data = new byte[rl - 8];
                Array.Copy(r0, p + 8, data, 0, rl - 8);
                if (!map.ContainsKey(type)) map[type] = data;
                // The record length already covers any 4 byte padding, so the
                // next record starts exactly at p + rl (as in mobiunpack).
                p += rl;
            }
            return map;
        }

        private string DecodeText(byte[] b, int off, int len)
        {
            try
            {
                Encoding enc = (TextEncoding == 65001)
                    ? new UTF8Encoding(false)
                    : Encoding.GetEncoding(TextEncoding == 0 ? 1252 : TextEncoding);
                return enc.GetString(b, off, len).Trim('\0', ' ', '\r', '\n');
            }
            catch { return ""; }
        }

        private static string Utf8(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            return new UTF8Encoding(false).GetString(b).Trim('\0', ' ', '\r', '\n');
        }

        public static string DetectImageExt(byte[] h)
        {
            if (h == null || h.Length < 4) return null;
            if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "jpg";
            if (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return "png";
            if (h[0] == 0x47 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x38) return "gif";
            if (h[0] == 0x42 && h[1] == 0x4D) return "bmp";
            if (h.Length >= 12 && h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46
                && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50) return "webp";
            return null;
        }

        public void Dispose()
        {
            if (_fs != null) { try { _fs.Dispose(); } catch { } _fs = null; }
        }
    }

    // =====================================================================
    //  Image helpers
    // =====================================================================
    public static class ImageGeometry
    {
        public static int BE16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }

        public static long BE32(byte[] b, int o)
        {
            return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
        }

        public static long LE32(byte[] b, int o)
        {
            return ((long)b[o + 3] << 24) | ((long)b[o + 2] << 16) | ((long)b[o + 1] << 8) | b[o];
        }

        /// <summary>Cut the padding that a MOBI image record may carry.</summary>
        public static byte[] Trim(byte[] d, string ext)
        {
            if (d == null) return new byte[0];
            if (ext == "jpg")
            {
                for (int i = d.Length - 2; i >= 2; i--)
                    if (d[i] == 0xFF && d[i + 1] == 0xD9) return Slice(d, 0, i + 2);
            }
            else if (ext == "png")
            {
                byte[] iend = { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };
                for (int i = d.Length - 8; i >= 0; i--)
                {
                    bool ok = true;
                    for (int k = 0; k < 8; k++) if (d[i + k] != iend[k]) { ok = false; break; }
                    if (ok) return Slice(d, 0, i + 8);
                }
            }
            else if (ext == "gif")
            {
                for (int i = d.Length - 1; i >= 0; i--) if (d[i] == 0x3B) return Slice(d, 0, i + 1);
            }
            else if (ext == "bmp")
            {
                if (d.Length >= 6)
                {
                    long size = LE32(d, 2);
                    if (size > 0 && size <= d.Length) return Slice(d, 0, (int)size);
                }
            }
            return d;
        }

        private static byte[] Slice(byte[] d, int off, int len)
        {
            byte[] r = new byte[len];
            Array.Copy(d, off, r, 0, len);
            return r;
        }

        /// <summary>Read pixel dimensions straight from the file header.</summary>
        public static void GetSize(byte[] d, string ext, out int w, out int h)
        {
            w = 0; h = 0;
            if (d == null || d.Length < 16) return;
            if (ext == "jpg")
            {
                int p = 2;
                while (p + 9 < d.Length)
                {
                    if (d[p] != 0xFF) { p++; continue; }
                    int m = d[p + 1];
                    if (m == 0xFF) { p++; continue; }
                    if (m == 0x01 || (m >= 0xD0 && m <= 0xD9)) { p += 2; continue; }
                    int sl = (d[p + 2] << 8) | d[p + 3];
                    if (sl < 2) break;
                    if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                    {
                        h = (d[p + 5] << 8) | d[p + 6];
                        w = (d[p + 7] << 8) | d[p + 8];
                        return;
                    }
                    p += 2 + sl;
                }
            }
            else if (ext == "png" && d.Length >= 24)
            {
                w = (int)BE32(d, 16); h = (int)BE32(d, 20);
            }
            else if (ext == "gif" && d.Length >= 10)
            {
                w = d[6] | (d[7] << 8); h = d[8] | (d[9] << 8);
            }
            else if (ext == "bmp" && d.Length >= 26)
            {
                int hs = (int)LE32(d, 14);
                if (hs == 12) { w = d[18] | (d[19] << 8); h = d[20] | (d[21] << 8); }
                else { w = (int)LE32(d, 18); h = (int)LE32(d, 22); if (h < 0) h = -h; }
            }
            if (w < 0) w = 0;
            if (h < 0) h = 0;
            if (w == 0 || h == 0) { w = 1000; h = 1400; }   // unknown: assume a page
        }
    }

    // =====================================================================
    //  Book loading (parse + extract every page to a reusable cache)
    // =====================================================================
    public static class BookLoader
    {
        public static string DefaultCacheRoot
        {
            get
            {
                string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(Path.Combine(b, "MangaReader"), "cache");
            }
        }

        public static Book Load(string[] paths, string cacheRoot, Action<double, string> report)
        {
            Book book = new Book();
            for (int i = 0; i < paths.Length; i++)
            {
                int idx = i;
                string p = paths[i];
                if (report != null) report((double)i / paths.Length, "正在解析 " + Path.GetFileName(p) + " …");

                ChapterInfo ci = new ChapterInfo();
                ci.SourcePath = p;
                ci.Title = Path.GetFileNameWithoutExtension(p);
                ci.FirstPage = book.Pages.Count;

                Action<double, string> sub = delegate(double frac, string msg)
                {
                    if (report != null) report((idx + frac) / paths.Length, msg);
                };
                List<PageEntry> pages = LoadOne(p, cacheRoot, sub);

                foreach (PageEntry pe in pages)
                {
                    pe.Index = book.Pages.Count;
                    pe.ChapterIndex = idx;
                    book.Pages.Add(pe);
                }
                ci.PageCount = pages.Count;
                book.Chapters.Add(ci);
            }

            if (book.Chapters.Count == 1) book.Title = book.Chapters[0].Title;
            else if (book.Chapters.Count > 1) book.Title = book.Chapters[0].Title + " 等 " + book.Chapters.Count + " 个文件";
            return book;
        }

        private static List<PageEntry> LoadOne(string path, string cacheRoot, Action<double, string> report)
        {
            string sig = Signature(path);
            string dir = Path.Combine(cacheRoot, sig);
            string index = Path.Combine(dir, "index.txt");

            List<PageEntry> cached = TryReadCache(index);
            if (cached != null)
            {
                if (report != null) report(1.0, "已从缓存载入 " + Path.GetFileName(path));
                return cached;
            }

            Directory.CreateDirectory(cacheRoot);
            string tmp = dir + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);

            List<PageEntry> pages = new List<PageEntry>();
            using (MobiFile mf = new MobiFile(path))
            {
                int total = mf.ImageRecords.Count;
                if (total == 0)
                {
                    string why = mf.EncryptionType != 0
                        ? "这个文件带有 DRM 加密，Manga Reader 无法读取其中的图片。"
                        : "Manga Reader 只能显示图片型（漫画）MOBI 文件，纯文字的电子书无法阅读。";
                    throw new InvalidDataException(
                        "《" + Path.GetFileName(path) + "》里没有找到任何图片页面。" +
                        Environment.NewLine + why);
                }

                for (int i = 0; i < total; i++)
                {
                    int rec = mf.ImageRecords[i];
                    string ext = mf.ImageExt[rec];
                    byte[] raw = mf.ReadRecord(rec);
                    byte[] img = ImageGeometry.Trim(raw, ext);
                    if (img.Length < 32) continue;

                    string name = string.Format(CultureInfo.InvariantCulture, "{0:0000}.{1}", i, ext);
                    string full = Path.Combine(tmp, name);
                    File.WriteAllBytes(full, img);

                    PageEntry pe = new PageEntry();
                    pe.FilePath = full;
                    int w, h;
                    ImageGeometry.GetSize(img, ext, out w, out h);
                    pe.Width = w;
                    pe.Height = h;
                    pages.Add(pe);

                    if (report != null)
                        report(total == 0 ? 1.0 : (double)(i + 1) / total,
                               "正在解压图片 " + (i + 1) + " / " + total);
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(path);
                FileInfo fi = new FileInfo(path);
                sb.AppendLine(fi.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                              fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine(mf.Title.Replace('\r', ' ').Replace('\n', ' '));
                sb.AppendLine(pages.Count.ToString(CultureInfo.InvariantCulture));
                foreach (PageEntry pe in pages)
                    sb.AppendLine(Path.GetFileName(pe.FilePath) + "|" +
                                  pe.Width.ToString(CultureInfo.InvariantCulture) + "|" +
                                  pe.Height.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(Path.Combine(tmp, "index.txt"), sb.ToString(), new UTF8Encoding(false));
            }

            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);

            for (int i = 0; i < pages.Count; i++) pages[i].FilePath = Path.Combine(dir, Path.GetFileName(pages[i].FilePath));
            return pages;
        }

        private static List<PageEntry> TryReadCache(string indexFile)
        {
            try
            {
                if (!File.Exists(indexFile)) return null;
                string[] lines = File.ReadAllLines(indexFile);
                if (lines.Length < 4) return null;
                string dir = Path.GetDirectoryName(indexFile);
                int count = int.Parse(lines[3], CultureInfo.InvariantCulture);
                if (lines.Length < 4 + count) return null;
                List<PageEntry> pages = new List<PageEntry>();
                for (int i = 0; i < count; i++)
                {
                    string[] parts = lines[4 + i].Split('|');
                    if (parts.Length < 3) return null;
                    string full = Path.Combine(dir, parts[0]);
                    if (!File.Exists(full)) return null;
                    PageEntry pe = new PageEntry();
                    pe.FilePath = full;
                    pe.Width = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    pe.Height = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    pages.Add(pe);
                }
                return pages.Count > 0 ? pages : null;
            }
            catch { return null; }
        }

        private static string Signature(string path)
        {
            FileInfo fi = new FileInfo(path);
            string s = fi.FullName.ToLowerInvariant() + "|" +
                       fi.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                       fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            MD5 md5 = MD5.Create();
            byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
