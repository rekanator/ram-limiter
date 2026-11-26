// The tray icons, drawn at whatever size the display scaling asks for.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.Win32;

namespace RamLimiter
{
    static class Icons
    {
        public static readonly string[][] Styles = {
            new[] { "Nap Chip", "nap" }, new[] { "Numbers", "numbers" }, new[] { "Speedometer", "speedometer" },
            new[] { "Memory chip", "chip" }, new[] { "Ring", "ring" }, new[] { "RAM text", "text" },
        };

        /// Yellow from 80% of the limit, orange from 90%, red from 100%.
        public static Color? Warning(double fraction)
        {
            if (fraction >= 1) return Color.FromArgb(255, 59, 48);
            if (fraction >= 0.9) return Color.FromArgb(255, 149, 0);
            if (fraction >= 0.8) return Color.FromArgb(255, 204, 0);
            return null;
        }

        public static bool LightTaskbar()
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0);
            return value is int && (int)value == 1;
        }

        static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static Pen Line(Color color, float width)
        {
            return new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        }

        /// The logo: a white chip with a sleeping moon on a purple tile. It never changes colour.
        /// Same drawing as napChip() in main.swift, on a 100-unit grid with y pointing up.
        static void NapChip(Graphics g, int size)
        {
            g.TranslateTransform(0, size);
            g.ScaleTransform(size / 100f, -size / 100f);
            var purple = Color.FromArgb(0x53, 0x4A, 0xB7);
            using (var fill = new SolidBrush(purple))
            using (var white = new SolidBrush(Color.White))
            using (var chipPen = new Pen(Color.White, 7))
            using (var pinPen = Line(Color.White, 6))
            {
                using (var tile = Rounded(0, 0, 100, 100, 22)) g.FillPath(fill, tile);
                // The moon is a circle with a purple bite. The bite is drawn before the outline.
                g.FillEllipse(white, 33, 33, 34, 34);
                g.FillEllipse(fill, 45, 41, 29, 29);
                using (var chip = Rounded(24, 24, 52, 52, 10)) g.DrawPath(chipPen, chip);
                foreach (var o in new[] { 37f, 50f, 63f })
                {
                    g.DrawLine(pinPen, o, 88, o, 78);
                    g.DrawLine(pinPen, o, 22, o, 12);
                    g.DrawLine(pinPen, 12, o, 22, o);
                    g.DrawLine(pinPen, 78, o, 88, o);
                }
            }
        }

        // Same drawings as gauge() in main.swift (y up), fitted into a square.
        static void Gauge(Graphics g, int size, string style, float f, Color ink)
        {
            float width = style == "ring" ? 17.5f : style == "chip" ? 20.5f : 21f;
            float middle = style == "speedometer" ? 8.25f : 9f;
            g.TranslateTransform(size / 2f, size / 2f);
            g.ScaleTransform(size / width, -size / width);
            g.TranslateTransform(-11, -middle);
            using (var fill = new SolidBrush(ink))
            {
                if (style == "speedometer") // empty is far left, the limit is far right
                {
                    using (var dial = Line(ink, 2)) g.DrawArc(dial, 1.5f, -5, 19, 19, 0, 180);
                    double angle = Math.PI * (1 - f);
                    using (var needle = Line(ink, 1.5f))
                        g.DrawLine(needle, 11, 4.5f, 11 + 7 * (float)Math.Cos(angle), 4.5f + 7 * (float)Math.Sin(angle));
                    g.FillEllipse(fill, 9, 2.5f, 4, 4);
                }
                else if (style == "ring")
                {
                    using (var faint = Line(Color.FromArgb(77, ink), 3)) g.DrawEllipse(faint, 4, 2, 14, 14);
                    if (f > 0)
                        using (var arc = Line(ink, 3)) g.DrawArc(arc, 4, 2, 14, 14, 90, -360 * f);
                }
                else // memory chip: the middle square fills from the bottom
                {
                    using (var pen = Line(ink, 1.2f))
                    {
                        using (var chip = Rounded(4.5f, 2.5f, 13, 13, 2)) g.DrawPath(pen, chip);
                        foreach (var o in new[] { 5.5f, 9f, 12.5f })
                        {
                            g.DrawLine(pen, 1.5f, o, 4, o);
                            g.DrawLine(pen, 18, o, 20.5f, o);
                        }
                    }
                    g.FillRectangle(fill, 6.5f, 4.5f, 9, 9 * f);
                }
            }
        }

        // Text stretched to fill a box, as large as the tiny icon allows.
        static void Fill(Graphics g, string text, Color ink, RectangleF box)
        {
            using (var path = new GraphicsPath())
            using (var family = new FontFamily("Segoe UI"))
            using (var brush = new SolidBrush(ink))
            {
                path.AddString(text, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
                var bounds = path.GetBounds();
                float x = box.Width / bounds.Width, y = box.Height / bounds.Height;
                x = Math.Min(x, y * 1.15f); // don't stretch short text sideways
                using (var move = new Matrix())
                {
                    move.Translate(box.X + (box.Width - bounds.Width * x) / 2, box.Y);
                    move.Scale(x, y);
                    move.Translate(-bounds.X, -bounds.Y);
                    path.Transform(move);
                }
                g.FillPath(brush, path);
            }
        }

        /// fraction: how full the limit is. text: the number shown by the "numbers" style.
        /// design: which of the Numbers and RAM designs to draw ('A' unless previewing).
        public static Bitmap Draw(string style, double fraction, int size, bool light, string text,
                                  bool faded = false, char design = 'A')
        {
            var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            var ink = Warning(fraction) ?? (light ? Color.Black : Color.White);
            var f = (float)Math.Min(Math.Max(fraction, 0), 1);
            using (var g = Graphics.FromImage(image))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (style == "nap") NapChip(g, size);
                else if (style == "numbers" || style == "text")
                {
                    var word = style == "text" ? "RAM" : text;
                    if (design == 'B') // text above a level bar
                    {
                        float bar = Math.Max(2, size / 6);
                        Fill(g, word, ink, new RectangleF(0, size * 0.08f, size, size - bar - size * 0.2f));
                        using (var faint = new SolidBrush(Color.FromArgb(77, ink)))
                        using (var solid = new SolidBrush(ink))
                        {
                            g.FillRectangle(faint, 0, size - bar, size, bar);
                            g.FillRectangle(solid, 0, size - bar, size * f, bar);
                        }
                    }
                    else if (design == 'C') // used above the limit
                    {
                        var parts = text.Split('/');
                        Fill(g, parts[0], ink, new RectangleF(0, 0, size, size * 0.46f));
                        Fill(g, parts.Length > 1 ? parts[1] : "", ink,
                             new RectangleF(0, size * 0.56f, size, size * 0.44f));
                    }
                    else Fill(g, word, ink, new RectangleF(0, size * 0.15f, size, size * 0.7f));
                }
                else Gauge(g, size, style, f, ink);

            }
            if (faded)
            {
                for (int x = 0; x < size; x++)
                    for (int y = 0; y < size; y++)
                    {
                        var c = image.GetPixel(x, y);
                        image.SetPixel(x, y, Color.FromArgb(c.A * 2 / 5, c));
                    }
            }
            return image;
        }

        /// The program's own icon: the logo at every size Windows uses.
        public static void WriteIco(string file)
        {
            var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 256 };
            var pictures = new List<byte[]>();
            foreach (var size in sizes)
                using (var image = Draw("nap", 0, size, false, ""))
                using (var data = new MemoryStream())
                {
                    image.Save(data, ImageFormat.Png);
                    pictures.Add(data.ToArray());
                }
            using (var writer = new BinaryWriter(File.Create(file)))
            {
                writer.Write((short)0);
                writer.Write((short)1);
                writer.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] % 256));
                    writer.Write((byte)(sizes[i] % 256));
                    writer.Write((short)0);
                    writer.Write((short)1);
                    writer.Write((short)32);
                    writer.Write(pictures[i].Length);
                    writer.Write(offset);
                    offset += pictures[i].Length;
                }
                foreach (var picture in pictures) writer.Write(picture);
            }
        }

        /// A picture of the icons at 100%, 150% and 200% scaling, each also enlarged, for checking.
        public static void WriteSheet(string file)
        {
            var rows = new[] {
                new[] { "Nap Chip", "nap", "A" }, new[] { "Speedometer", "speedometer", "A" },
                new[] { "Memory chip", "chip", "A" }, new[] { "Ring", "ring", "A" },
                new[] { "Numbers A: used", "numbers", "A" }, new[] { "Numbers B: used + bar", "numbers", "B" },
                new[] { "Numbers C: used / limit", "numbers", "C" }, new[] { "RAM A: word", "text", "A" },
                new[] { "RAM B: word + bar", "text", "B" },
            };
            var levels = new[] { 0.25, 0.65, 0.85, 0.95, 1.1 };
            using (var sheet = new Bitmap(1180, 40 + rows.Length * 80))
            using (var g = Graphics.FromImage(sheet))
            using (var font = new Font("Segoe UI", 9))
            {
                g.Clear(Color.FromArgb(128, 128, 128));
                g.FillRectangle(Brushes.Black, 180, 0, 500, sheet.Height);
                g.FillRectangle(Brushes.WhiteSmoke, 680, 0, 500, sheet.Height);
                g.DrawString("Dark taskbar: real size 16 / 24 / 32 px at 25-110%", font, Brushes.White, 190, 10);
                g.DrawString("Light taskbar", font, Brushes.Black, 690, 10);
                for (int row = 0; row < rows.Length; row++)
                {
                    int top = 40 + row * 80;
                    g.DrawString(rows[row][0], font, Brushes.White, 8, top + 24);
                    for (int theme = 0; theme < 2; theme++)
                        for (int i = 0; i < levels.Length; i++)
                        {
                            int left = 190 + theme * 500 + i * 98;
                            var text = (levels[i] * 4).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                            if (rows[row][2] == "C") text += "/4";
                            int x = left;
                            foreach (var size in new[] { 16, 24, 32 })
                            {
                                using (var icon = Draw(rows[row][1], levels[i], size, theme == 1, text,
                                                       false, rows[row][2][0]))
                                    g.DrawImageUnscaled(icon, x, top + 4);
                                x += size + 6;
                            }
                            using (var big = Draw(rows[row][1], levels[i], 16, theme == 1, text,
                                                  false, rows[row][2][0]))
                            {
                                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                g.DrawImage(big, left, top + 40, 32, 32);
                                g.PixelOffsetMode = PixelOffsetMode.Default;
                            }
                        }
                }
                sheet.Save(file, ImageFormat.Png);
            }
        }
    }
}
