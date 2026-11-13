using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
#if WINDOWS
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#endif
using System.Xml.Linq;

namespace StudyHub.Services
{
    public class PdfConversionItemResult
    {
        public string SourcePath { get; set; } = "";
        public string OriginalName { get; set; } = "";
        public string OutputPdfName { get; set; } = "";
        public string OutputPdfPath { get; set; } = "";
        public long SizeBytes { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; } = "";
    }

    public static class PdfConverterService
    {
        public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
            ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt",
            ".txt", ".md", ".csv"
        };

        public static PdfConversionItemResult ConvertFileToPdf(string inputPath, string targetDir)
        {
            var res = new PdfConversionItemResult
            {
                SourcePath = inputPath,
                OriginalName = Path.GetFileName(inputPath)
            };

            try
            {
                if (!File.Exists(inputPath))
                {
                    res.Error = "Source file does not exist.";
                    return res;
                }

                Directory.CreateDirectory(targetDir);
                string ext = Path.GetExtension(inputPath).ToLowerInvariant();
                string stem = Path.GetFileNameWithoutExtension(inputPath);
                string cleanStem = Regex.Replace(stem, @"[<>:""/\\|?*\x00-\x1f]", "_").Trim(' ', '.');
                if (string.IsNullOrWhiteSpace(cleanStem)) cleanStem = "Converted_Document";

                string pdfName = $"{cleanStem}.pdf";
                string outPath = Path.Combine(targetDir, pdfName);
                int counter = 2;
                while (File.Exists(outPath))
                {
                    pdfName = $"{cleanStem}_{counter}.pdf";
                    outPath = Path.Combine(targetDir, pdfName);
                    counter++;
                }

                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                {
                    ConvertImageToPdfNative(inputPath, outPath);
                }
                else if (ext == ".docx")
                {
                    var lines = ExtractDocxLines(inputPath);
                    RenderLinesToPaginatedPdf(stem, lines, outPath);
                }
                else if (ext == ".pptx")
                {
                    var lines = ExtractPptxLines(inputPath);
                    RenderLinesToPaginatedPdf(stem, lines, outPath);
                }
                else if (ext == ".xlsx")
                {
                    var lines = ExtractXlsxLines(inputPath);
                    RenderLinesToPaginatedPdf(stem, lines, outPath);
                }
                else if (ext is ".doc" or ".xls" or ".ppt")
                {
                    var lines = ExtractBinaryOfficeStrings(inputPath);
                    RenderLinesToPaginatedPdf(stem, lines, outPath);
                }
                else if (ext is ".txt" or ".md" or ".csv")
                {
                    var lines = File.ReadAllLines(inputPath, Encoding.UTF8).ToList();
                    RenderLinesToPaginatedPdf(stem, lines, outPath);
                }
                else
                {
                    res.Error = $"Unsupported file extension: {ext}";
                    return res;
                }

                res.OutputPdfName = pdfName;
                res.OutputPdfPath = outPath;
                res.SizeBytes = new FileInfo(outPath).Length;
                res.Success = true;
                return res;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                return res;
            }
        }

#if WINDOWS
        private static void ConvertImageToPdfNative(string imagePath, string outputPdfPath)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            // Render onto white background in case of PNG transparency
            int w = Math.Max(1, bmp.PixelWidth);
            int h = Math.Max(1, bmp.PixelHeight);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawImage(bmp, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();

            byte[] jpegBytes = EncodeJpeg(rtb, 92);
            bool landscape = w > h * 1.15;
            double pageW = landscape ? 842.0 : 595.0;
            double pageH = landscape ? 595.0 : 842.0;
            double margin = 24.0;
            double availW = pageW - 2 * margin;
            double availH = pageH - 2 * margin;
            double scale = Math.Min(availW / w, availH / h);
            double drawW = w * scale;
            double drawH = h * scale;
            double x0 = (pageW - drawW) / 2.0;
            double y0 = (pageH - drawH) / 2.0;

            WriteJpegPagesToPdf(new List<(byte[] jpeg, int pxW, int pxH, double pageW, double pageH, double x, double y, double w, double h)>
            {
                (jpegBytes, w, h, pageW, pageH, x0, y0, drawW, drawH)
            }, outputPdfPath);
        }

        private static void RenderLinesToPaginatedPdf(string title, List<string> paragraphs, string outputPdfPath)
        {
            if (paragraphs == null || paragraphs.Count == 0)
                paragraphs = new List<string> { "(Empty document)" };

            const int pagePxW = 1240; // ~150 DPI A4 width
            const int pagePxH = 1754; // ~150 DPI A4 height
            const double margin = 72;
            const double contentW = pagePxW - margin * 2;

            var typefaceTitle = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var typefaceBody = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var typefaceHeading = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

            // Paginate paragraphs
            var pages = new List<(byte[] jpeg, int pxW, int pxH, double pageW, double pageH, double x, double y, double w, double h)>();
            int paraIdx = 0;
            int pageNum = 1;

            while (paraIdx < paragraphs.Count || pageNum == 1)
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pagePxW, pagePxH));

                    // Top subtle accent bar
                    var accentBrush = new LinearGradientBrush(
                        Color.FromRgb(124, 92, 255),
                        Color.FromRgb(34, 211, 238),
                        new Point(0, 0), new Point(1, 0));
                    dc.DrawRectangle(accentBrush, null, new Rect(0, 0, pagePxW, 10));

                    double y = margin;
                    if (pageNum == 1)
                    {
                        var ftTitle = new FormattedText(
                            title,
                            CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            typefaceTitle,
                            34,
                            new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                            1.0)
                        {
                            MaxTextWidth = contentW
                        };
                        dc.DrawText(ftTitle, new Point(margin, y));
                        y += ftTitle.Height + 14;
                        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(203, 213, 225)), 1.5),
                            new Point(margin, y), new Point(pagePxW - margin, y));
                        y += 22;
                    }

                    while (paraIdx < paragraphs.Count)
                    {
                        string rawLine = paragraphs[paraIdx] ?? "";
                        bool isHeader = rawLine.StartsWith("## ") || rawLine.StartsWith("# ");
                        string cleanText = isHeader ? rawLine.TrimStart('#', ' ') : rawLine;
                        if (string.IsNullOrWhiteSpace(cleanText))
                        {
                            y += 14;
                            paraIdx++;
                            if (y > pagePxH - margin - 40) break;
                            continue;
                        }

                        var ft = new FormattedText(
                            cleanText,
                            CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            isHeader ? typefaceHeading : typefaceBody,
                            isHeader ? 24 : 18.5,
                            isHeader ? new SolidColorBrush(Color.FromRgb(79, 70, 229)) : new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                            1.0)
                        {
                            MaxTextWidth = contentW
                        };

                        if (y + ft.Height > pagePxH - margin - 36 && y > margin + 80)
                        {
                            break; // Next page
                        }

                        dc.DrawText(ft, new Point(margin, y));
                        y += ft.Height + (isHeader ? 14 : 9);
                        paraIdx++;
                    }

                    // Page footer
                    var ftFooter = new FormattedText(
                        $"Page {pageNum} • Study Hub PDF Converter",
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typefaceBody,
                        14,
                        new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                        1.0);
                    dc.DrawText(ftFooter, new Point(pagePxW - margin - ftFooter.Width, pagePxH - 44));
                }

                var rtb = new RenderTargetBitmap(pagePxW, pagePxH, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();

                byte[] jpg = EncodeJpeg(rtb, 90);
                pages.Add((jpg, pagePxW, pagePxH, 595.0, 842.0, 0, 0, 595.0, 842.0));
                pageNum++;
                if (pageNum > 120) break;
            }

            WriteJpegPagesToPdf(pages, outputPdfPath);
        }
#elif ANDROID
        private static void ConvertImageToPdfNative(string imagePath, string outputPdfPath)
        {
            using var srcBmp = Android.Graphics.BitmapFactory.DecodeFile(imagePath);
            if (srcBmp == null) throw new InvalidOperationException("Failed to decode image.");
            int w = Math.Max(1, srcBmp.Width);
            int h = Math.Max(1, srcBmp.Height);

            using var outBmp = Android.Graphics.Bitmap.CreateBitmap(w, h, Android.Graphics.Bitmap.Config.Argb8888!);
            using (var canvas = new Android.Graphics.Canvas(outBmp))
            {
                canvas.DrawColor(Android.Graphics.Color.White);
                canvas.DrawBitmap(srcBmp, 0, 0, null);
            }

            using var ms = new MemoryStream();
            outBmp.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 92, ms);
            byte[] jpegBytes = ms.ToArray();

            bool landscape = w > h * 1.15;
            double pageW = landscape ? 842.0 : 595.0;
            double pageH = landscape ? 595.0 : 842.0;
            double margin = 24.0;
            double availW = pageW - 2 * margin;
            double availH = pageH - 2 * margin;
            double scale = Math.Min(availW / w, availH / h);
            double drawW = w * scale;
            double drawH = h * scale;
            double x0 = (pageW - drawW) / 2.0;
            double y0 = (pageH - drawH) / 2.0;

            WriteJpegPagesToPdf(new List<(byte[] jpeg, int pxW, int pxH, double pageW, double pageH, double x, double y, double w, double h)>
            {
                (jpegBytes, w, h, pageW, pageH, x0, y0, drawW, drawH)
            }, outputPdfPath);
        }

        private static void RenderLinesToPaginatedPdf(string title, List<string> paragraphs, string outputPdfPath)
        {
            if (paragraphs == null || paragraphs.Count == 0)
                paragraphs = new List<string> { "(Empty document)" };

            const int pagePxW = 1240;
            const int pagePxH = 1754;
            const float margin = 72f;

            var pages = new List<(byte[] jpeg, int pxW, int pxH, double pageW, double pageH, double x, double y, double w, double h)>();
            int paraIdx = 0;
            int pageNum = 1;

            using var paintTitle = new Android.Graphics.Paint(Android.Graphics.PaintFlags.AntiAlias)
            {
                Color = new Android.Graphics.Color(15, 23, 42),
                TextSize = 34f,
                FakeBoldText = true
            };
            using var paintHeading = new Android.Graphics.Paint(Android.Graphics.PaintFlags.AntiAlias)
            {
                Color = new Android.Graphics.Color(79, 70, 229),
                TextSize = 24f,
                FakeBoldText = true
            };
            using var paintBody = new Android.Graphics.Paint(Android.Graphics.PaintFlags.AntiAlias)
            {
                Color = new Android.Graphics.Color(30, 41, 59),
                TextSize = 19f
            };
            using var paintLine = new Android.Graphics.Paint(Android.Graphics.PaintFlags.AntiAlias)
            {
                Color = new Android.Graphics.Color(203, 213, 225),
                StrokeWidth = 2f
            };

            while (paraIdx < paragraphs.Count || pageNum == 1)
            {
                using var bmp = Android.Graphics.Bitmap.CreateBitmap(pagePxW, pagePxH, Android.Graphics.Bitmap.Config.Argb8888!);
                using (var canvas = new Android.Graphics.Canvas(bmp))
                {
                    canvas.DrawColor(Android.Graphics.Color.White);
                    float y = margin + 32f;
                    if (pageNum == 1)
                    {
                        canvas.DrawText(title, margin, y, paintTitle);
                        y += 24f;
                        canvas.DrawLine(margin, y, pagePxW - margin, y, paintLine);
                        y += 36f;
                    }

                    while (paraIdx < paragraphs.Count)
                    {
                        string rawLine = paragraphs[paraIdx] ?? "";
                        bool isHeader = rawLine.StartsWith("## ") || rawLine.StartsWith("# ");
                        string cleanText = isHeader ? rawLine.TrimStart('#', ' ') : rawLine;
                        if (string.IsNullOrWhiteSpace(cleanText))
                        {
                            y += 18f;
                            paraIdx++;
                            if (y > pagePxH - margin - 50f) break;
                            continue;
                        }

                        var p = isHeader ? paintHeading : paintBody;
                        float stepY = isHeader ? 34f : 26f;
                        if (y + stepY > pagePxH - margin - 40f && y > margin + 80f)
                            break;

                        canvas.DrawText(cleanText.Length > 95 ? cleanText.Substring(0, 95) : cleanText, margin, y, p);
                        y += stepY;
                        paraIdx++;
                    }
                }

                using var ms = new MemoryStream();
                bmp.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 90, ms);
                pages.Add((ms.ToArray(), pagePxW, pagePxH, 595.0, 842.0, 0, 0, 595.0, 842.0));
                pageNum++;
                if (pageNum > 120) break;
            }

            WriteJpegPagesToPdf(pages, outputPdfPath);
        }
#endif

        private static List<string> ExtractDocxLines(string docxPath)
        {
            var lines = new List<string>();
            using var zip = ZipFile.OpenRead(docxPath);
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) return lines;

            using var stream = entry.Open();
            var xdoc = XDocument.Load(stream);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

            foreach (var p in xdoc.Descendants(w + "p"))
            {
                string text = string.Concat(p.Descendants(w + "t").Select(t => t.Value)).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    var styleVal = p.Descendants(w + "pStyle").FirstOrDefault()?.Attribute(w + "val")?.Value ?? "";
                    if (styleVal.Contains("Heading", StringComparison.OrdinalIgnoreCase) || styleVal.Contains("Title", StringComparison.OrdinalIgnoreCase))
                        lines.Add("## " + text);
                    else
                        lines.Add(text);
                }
            }
            return lines;
        }

        private static List<string> ExtractPptxLines(string pptxPath)
        {
            var lines = new List<string>();
            using var zip = ZipFile.OpenRead(pptxPath);
            var slideEntries = zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName)
                .ToList();

            XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            int slideNum = 1;
            foreach (var entry in slideEntries)
            {
                lines.Add($"## Slide {slideNum}");
                using var s = entry.Open();
                var xdoc = XDocument.Load(s);
                foreach (var p in xdoc.Descendants(a + "p"))
                {
                    string txt = string.Concat(p.Descendants(a + "t").Select(t => t.Value)).Trim();
                    if (!string.IsNullOrEmpty(txt))
                        lines.Add("• " + txt);
                }
                lines.Add("");
                slideNum++;
            }
            return lines;
        }

        private static List<string> ExtractXlsxLines(string xlsxPath)
        {
            var lines = new List<string>();
            using var zip = ZipFile.OpenRead(xlsxPath);
            var sharedStrings = new List<string>();

            var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
            if (ssEntry != null)
            {
                using var s = ssEntry.Open();
                var xdoc = XDocument.Load(s);
                foreach (var si in xdoc.Root?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    sharedStrings.Add(string.Concat(si.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value)));
                }
            }

            var sheetEntries = zip.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName)
                .ToList();

            int sheetIdx = 1;
            foreach (var entry in sheetEntries)
            {
                lines.Add($"## Worksheet {sheetIdx}");
                using var s = entry.Open();
                var xdoc = XDocument.Load(s);
                foreach (var row in xdoc.Descendants().Where(e => e.Name.LocalName == "row").Take(300))
                {
                    var cells = new List<string>();
                    foreach (var c in row.Elements().Where(e => e.Name.LocalName == "c"))
                    {
                        string type = c.Attribute("t")?.Value ?? "";
                        string val = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                        if (type == "s" && int.TryParse(val, out int sIdx) && sIdx >= 0 && sIdx < sharedStrings.Count)
                            val = sharedStrings[sIdx];
                        if (!string.IsNullOrWhiteSpace(val))
                            cells.Add(val.Trim());
                    }
                    if (cells.Count > 0)
                        lines.Add(string.Join("   |   ", cells));
                }
                lines.Add("");
                sheetIdx++;
            }
            return lines;
        }

        private static List<string> ExtractBinaryOfficeStrings(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            var lines = new List<string>();
            var utf16Matches = Regex.Matches(Encoding.Unicode.GetString(raw), @"[\u0020-\u007E\u0600-\u06FF\r\n\t]{12,}");
            foreach (Match m in utf16Matches)
            {
                string s = m.Value.Trim();
                if (!s.Contains("Word.Document") && !s.Contains("Root Entry") && !s.Contains("Normal.dot"))
                    lines.Add(s);
            }
            return lines;
        }

#if WINDOWS
        private static byte[] EncodeJpeg(BitmapSource bmp, int quality)
        {
            var enc = new JpegBitmapEncoder { QualityLevel = quality };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
#endif

        private static void WriteJpegPagesToPdf(
            List<(byte[] jpeg, int pxW, int pxH, double pageW, double pageH, double x, double y, double w, double h)> pages,
            string outputPdfPath)
        {
            using var fs = new FileStream(outputPdfPath, FileMode.Create, FileAccess.Write);
            var offsets = new Dictionary<int, long>();

            void WriteAscii(string s)
            {
                byte[] b = Encoding.ASCII.GetBytes(s);
                fs.Write(b, 0, b.Length);
            }

            WriteAscii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

            // Object 1: Catalog, Object 2: Pages
            int nextObjId = 3;
            var pageObjIds = new List<int>();

            var pageRecords = new List<(int pageObjId, int contentObjId, int imgObjId, (byte[] jpeg, int pxW, int pxH, double pageW, double pageH, double x, double y, double w, double h) data)>();
            foreach (var p in pages)
            {
                int pId = nextObjId++;
                int cId = nextObjId++;
                int iId = nextObjId++;
                pageObjIds.Add(pId);
                pageRecords.Add((pId, cId, iId, p));
            }

            offsets[1] = fs.Position;
            WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            offsets[2] = fs.Position;
            string kids = string.Join(" ", pageObjIds.Select(id => $"{id} 0 R"));
            WriteAscii($"2 0 obj\n<< /Type /Pages /Kids [ {kids} ] /Count {pageObjIds.Count} >>\nendobj\n");

            foreach (var rec in pageRecords)
            {
                var d = rec.data;
                string contentCmd = string.Format(
                    CultureInfo.InvariantCulture,
                    "q\n{0:F2} 0 0 {1:F2} {2:F2} {3:F2} cm\n/Im0 Do\nQ\n",
                    d.w, d.h, d.x, d.y);
                byte[] contentBytes = Encoding.ASCII.GetBytes(contentCmd);

                offsets[rec.pageObjId] = fs.Position;
                WriteAscii(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {1:F2} {2:F2}] /Resources << /XObject << /Im0 {3} 0 R >> >> /Contents {4} 0 R >>\nendobj\n",
                    rec.pageObjId, d.pageW, d.pageH, rec.imgObjId, rec.contentObjId));

                offsets[rec.contentObjId] = fs.Position;
                WriteAscii($"{rec.contentObjId} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
                fs.Write(contentBytes, 0, contentBytes.Length);
                WriteAscii("\nendstream\nendobj\n");

                offsets[rec.imgObjId] = fs.Position;
                WriteAscii($"{rec.imgObjId} 0 obj\n<< /Type /XObject /Subtype /Image /Width {d.pxW} /Height {d.pxH} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {d.jpeg.Length} >>\nstream\n");
                fs.Write(d.jpeg, 0, d.jpeg.Length);
                WriteAscii("\nendstream\nendobj\n");
            }

            long xrefPos = fs.Position;
            int totalObjs = nextObjId;
            WriteAscii($"xref\n0 {totalObjs}\n0000000000 65535 f \n");
            for (int id = 1; id < totalObjs; id++)
            {
                WriteAscii($"{offsets[id]:D10} 00000 n \n");
            }
            WriteAscii($"trailer\n<< /Size {totalObjs} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        }
    }
}
