// ============================================================
//  KeystrokeOverlay - keyboard / click display widget for OBS
//  WinForms host + GDI+ rendering + per-pixel alpha layered window.
//
//  Layout:   CAPSLOCK | A  S  D  F | G
//            LSHIFT   |   SPACE    |
//            (CPS badge on the right)
//
//  Drag anywhere to move. Ctrl + wheel to scale. Close via the
//  taskbar button or the x in the top-right corner.
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeystrokeOverlay
{
    // ---------------------------------------------------------
    //  Win32
    // ---------------------------------------------------------
    static class Native
    {
        public const uint WS_EX_LAYERED = 0x00080000;
        public const uint WS_EX_TOOLWINDOW = 0x00000080;
        public const uint WS_EX_NOACTIVATE = 0x08000000;

        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const uint ULW_ALPHA = 0x00000002;

        public const int WM_NCHITTEST = 0x0084;
        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        public static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        public static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdcDst, int x, int y, int cx, int cy,
            IntPtr hdcSrc, int x1, int y1, uint rop);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; public SIZE(int w, int h) { cx = w; cy = h; } }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        public static int GetExStyle(IntPtr h)
        {
            return IntPtr.Size == 8 ? (int)GetWindowLongPtr64(h, -20).ToInt64()
                                    : GetWindowLong32(h, -20);
        }

        public static void SetExStyle(IntPtr h, int style)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(h, -20, new IntPtr(style));
            else SetWindowLong32(h, -20, style);
        }
    }

    // ---------------------------------------------------------
    //  Key model
    // ---------------------------------------------------------
    sealed class KeyState
    {
        public int Vk;
        public string Caption;
        public bool Held;
        public float Glow;
        /// Rectangle the caption is centred in. Differs from the key rect for the
        /// tall side keys (CAPSLOCK / G) so the label sits at its own row.
        public RectangleF CaptionBox;
        /// CAPS and G are shown but intentionally excluded from the CPS count.
        public bool CountsForCps;

        public KeyState(int vk, string caption, bool countsForCps = true)
        {
            Vk = vk;
            Caption = caption;
            CountsForCps = countsForCps;
        }
    }

    // ---------------------------------------------------------
    //  Input : keyboard only
    // ---------------------------------------------------------
    static class Input
    {
        public const int VK_CAPITAL = 0x14;
        public const int VK_SPACE = 0x20;
        public const int VK_A = 0x41;
        public const int VK_S = 0x53;
        public const int VK_D = 0x44;
        public const int VK_F = 0x46;
        public const int VK_G = 0x47;
        public const int VK_LSHIFT = 0xA0;
        public const int VK_LBUTTON = 0x01;

        public static readonly KeyState[] Keys =
        {
            new KeyState(VK_CAPITAL, "CAPS",  false),   // shown, not counted
            new KeyState(VK_LSHIFT,  "SHIFT"),
            new KeyState(VK_A,       "A"),
            new KeyState(VK_S,       "S"),
            new KeyState(VK_D,       "D"),
            new KeyState(VK_F,       "F"),
            new KeyState(VK_SPACE,   "SPACE"),
            new KeyState(VK_G,       "G",     false),   // shown, not counted
        };

        static Input()
        {
            // CAPSLOCK is full height; put its label on the bottom row so it does not
            // collide with the SHIFT label underneath it.
            int rowH = Board.KeyW;
            int row2 = Board.Top + rowH + Board.RowGap;
            int blockH = Board.BlockH;

            // Full-height side keys (CAPSLOCK / G): centre on the whole block.
            // Row-height keys: centre on their own row.
            // CAPS is full height: put its label on the upper row (aligned with A..F)
            // and SHIFT on the lower row, so the two never share a band.
            Input.Keys[0].CaptionBox = new RectangleF(Board.Caps.X, Board.Top, Board.Caps.W, blockH); // CAPS (centred)
            Input.Keys[1].CaptionBox = new RectangleF(Board.Shift.X, row2, Board.Shift.W, rowH);      // SHIFT
            Input.Keys[2].CaptionBox = new RectangleF(Board.A.X, Board.Top, Board.KeyW, rowH);
            Input.Keys[3].CaptionBox = new RectangleF(Board.S.X, Board.Top, Board.KeyW, rowH);
            Input.Keys[4].CaptionBox = new RectangleF(Board.D.X, Board.Top, Board.KeyW, rowH);
            Input.Keys[5].CaptionBox = new RectangleF(Board.F.X, Board.Top, Board.KeyW, rowH);
            Input.Keys[6].CaptionBox = new RectangleF(Board.Space.X, row2, Board.Space.W, rowH);         // SPACE
            Input.Keys[7].CaptionBox = new RectangleF(Board.G.X, Board.Top, Board.G.W, blockH);           // G
        }

        static bool prevLeft;
        public static float Cps;
        public static int PeakCps;
        static readonly List<double> presses = new List<double>();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static readonly HashSet<int> countedDown = new HashSet<int>();

        /// PEAK is forgotten after this many seconds without any counted key press.
        public const double PeakCooldownSeconds = 5.0;

        /// Time of the most recent counted press (drives the peak cooldown).
        static double lastPressTime = double.MinValue / 2;

        public static bool LeftButtonDown
        {
            get { return (Native.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; }
        }

        /// Samples the keyboard. CPS counts key-down edges only, so holding a key
        /// does not inflate the number; CAPS and G are displayed but not counted.
        public static void Sample()
        {
            double now = clock.Elapsed.TotalSeconds;

            foreach (KeyState k in Keys)
            {
                bool down = (Native.GetAsyncKeyState(k.Vk) & 0x8000) != 0;

                if (down && !countedDown.Contains(k.Vk))
                {
                    countedDown.Add(k.Vk);
                    if (k.CountsForCps)
                    {
                        presses.Add(now);
                        lastPressTime = now;
                    }
                }
                else if (!down)
                {
                    countedDown.Remove(k.Vk);
                }

                k.Held = down;
                k.Glow = down ? Math.Min(1f, k.Glow + 0.35f) : Math.Max(0f, k.Glow - 0.22f);
            }

            // Idle cooldown: drop the historical peak once the user stops typing.
            if (PeakCps > 0 && now - lastPressTime > PeakCooldownSeconds)
                PeakCps = 0;

            Cps = ComputeCps(presses, now, ref PeakCps);
            prevLeft = LeftButtonDown;
        }

        /// Counts presses in the trailing one-second window, tracking the peak.
        public static float ComputeCps(List<double> times, double now, ref int peak)
        {
            times.RemoveAll(t => now - t > 1.0);
            int c = times.Count;
            if (c > peak) peak = c;
            return c;
        }
    }

    // ---------------------------------------------------------
    //  Layout (logical units; everything scales by Render.Scale)
    // ---------------------------------------------------------
    struct KeyRect
    {
        public int X, Y, W, H;
        public KeyRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
    }

    static class Board
    {
        // ---- geometry, derived from a single key size ----
        public const int KeyW  = 60;                 // one letter key
        public const int Gap   = 6;                  // gap between the two letters of a pair
        public const int RowGap = 8;                 // vertical gap between the two rows
        public const int WideW = KeyW * 2 + Gap;     // SHIFT (A+S) and SPACE (D+F) width
        public const int GroupGap = Gap;             // same spacing as between A and S

        public const int CapsW = 64;                 // CAPSLOCK column
        public const int G_W   = 64;                 // G column

        public const int Top   = 42;                 // first row y
        public const int BlockH = KeyW * 2 + RowGap; // CAPSLOCK / G span both rows

        // ---- columns ----
        const int CapX   = 24;                       // CAPSLOCK left
        const int AsdfX  = 128;                      // A left
        const int D_X    = AsdfX + WideW + GroupGap; // D left (SPACE left)

        // SHIFT keeps the A left edge, so it covers exactly A..S.
        // SPACE keeps the D left edge, so it covers exactly D..F.
        const int ShiftX = AsdfX;
        const int SpaceX = D_X;
        const int GX     = D_X + WideW + GroupGap;   // G after F

        public static readonly KeyRect Caps  = new KeyRect(CapX,   Top, CapsW, BlockH);
        public static readonly KeyRect Shift = new KeyRect(ShiftX, Top + KeyW + RowGap, WideW, KeyW);
        public static readonly KeyRect A     = new KeyRect(AsdfX,  Top, KeyW, KeyW);
        public static readonly KeyRect S     = new KeyRect(AsdfX + KeyW + Gap, Top, KeyW, KeyW);
        public static readonly KeyRect D     = new KeyRect(D_X,    Top, KeyW, KeyW);
        public static readonly KeyRect F     = new KeyRect(D_X + KeyW + Gap, Top, KeyW, KeyW);
        public static readonly KeyRect Space = new KeyRect(SpaceX, Top + KeyW + RowGap, WideW, KeyW);
        public static readonly KeyRect G     = new KeyRect(GX,     Top, G_W, BlockH);

        public const int BadgeR  = 60;
        public const int BadgeCY = Top + BlockH / 2;
        public const int BadgeCX = GX + G_W + 84;

        public const int CanvasW = BadgeCX + BadgeR + 28;
        public const int CanvasH = Top + BlockH + 26;

        // ---- chrome ----
        public static readonly Rectangle CloseRect = new Rectangle(CanvasW - 32, 8, 22, 22);
        public static readonly Rectangle MinRect   = new Rectangle(CanvasW - 62, 8, 22, 22);
        public static readonly Rectangle GearRect  = new Rectangle(CanvasW - 92, 8, 22, 22);
        public static readonly Rectangle HandleRect = new Rectangle(CanvasW / 2 - 22, 9, 44, 7);
        public const int PanelH = 168;
        public static readonly Rectangle PanelRect = new Rectangle(14, 14, CanvasW - 28, PanelH);
        public static readonly Rectangle OptRemember =
            new Rectangle(PanelRect.X + 26, PanelRect.Y + 48, 28, 28);
        public static readonly Rectangle OptLock =
            new Rectangle(PanelRect.X + 26, PanelRect.Y + 88, 28, 28);
        public static readonly Rectangle BtnReset =
            new Rectangle(PanelRect.Right - 168, PanelRect.Bottom - 44, 148, 32);
        public static readonly Rectangle BtnClosePanel =
            new Rectangle(PanelRect.Right - 40, PanelRect.Y + 9, 26, 26);

        public static readonly KeyRect[] Rects =
        {
            Caps, Shift, A, S, D, F, Space, G
        };

        /// The panel draws on top of the keys, so the window keeps its size.
        public static int Height(bool panelOpen) { return CanvasH; }
    }
    // ---------------------------------------------------------
    //  Rendering
    // ---------------------------------------------------------
    static class Render
    {
        // Kept translucent so the game shows through, but opaque enough to stay legible
        // over bright backgrounds.
        static readonly Color Accent = Color.FromArgb(190, 176, 150, 255);
        static readonly Color TextDim = Color.FromArgb(255, 255, 252, 255);
        static readonly Color KeyEdge = Color.FromArgb(225, 196, 176, 255);
        static readonly Color KeyFillTop = Color.FromArgb(96, 60, 48, 108);
        static readonly Color KeyFillBot = Color.FromArgb(70, 44, 34, 78);
        static readonly Color KeyPressedTop = Color.FromArgb(168, 116, 92, 206);
        static readonly Color KeyPressedBot = Color.FromArgb(128, 78, 60, 148);

        static Font fontKey, fontCpsLabel, fontCpsValue, fontPeak, fontOption, fontTitle, fontSmall;

        /// Global scale: dpi factor * user zoom.
        public static float Scale = 1f;

        public static void Init()
        {
            fontKey = MakeFont("Segoe UI Semibold", 15f, FontStyle.Regular, "Arial");
            fontCpsLabel = MakeFont("Segoe UI", 11f, FontStyle.Regular, "Arial");
            fontCpsValue = MakeFont("Segoe UI", 44f, FontStyle.Bold, "Arial");
            fontPeak = MakeFont("Segoe UI", 10f, FontStyle.Regular, "Arial");
            fontOption = MakeFont("Segoe UI", 14f, FontStyle.Regular, "Arial");
            fontTitle = MakeFont("Segoe UI Semibold", 19f, FontStyle.Regular, "Arial");
            fontSmall = MakeFont("Segoe UI", 12f, FontStyle.Regular, "Arial");
        }

        static Font MakeFont(string name, float size, FontStyle style, string fallback)
        {
            try
            {
                Font f = new Font(name, size, style, GraphicsUnit.Pixel);
                if (f.Name.IndexOf(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase) >= 0)
                    return f;
                f.Dispose();
            }
            catch { }
            return new Font(fallback, size, style, GraphicsUnit.Pixel);
        }

        /// Draws the whole widget. `scale` maps logical units to device pixels.
        public static bool SolidMode;

        /// Optional frosted plate behind the keys (--glass). Off by default.
        public static bool GlassMode;

        /// Mirrors the layered-window transparency so offscreen previews match the app.
        public static int ConstantAlpha = 255;

        public static void Draw(Graphics gg, float scale, bool hover, bool hoverClose)
        {
            Draw(gg, scale, hover, hoverClose, false, false, false);
        }

        public static void Draw(Graphics gg, float scale, bool hover, bool hoverClose,
                                bool panelOpen, bool optRemember, bool optLock)
        {
            gg.SmoothingMode = SmoothingMode.AntiAlias;
            gg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int logicalH = Board.Height(panelOpen);
            gg.Clear(SolidMode ? Color.FromArgb(22, 16, 34) : Color.Transparent);
            gg.ScaleTransform(scale, scale);

            if (SolidMode)
            {
                // subtle backdrop so the captured image is self-contained
                using (var bg = new LinearGradientBrush(
                    new RectangleF(0, 0, Board.CanvasW, logicalH),
                    Color.FromArgb(30, 22, 46), Color.FromArgb(14, 10, 24), 40f))
                    gg.FillRectangle(bg, new RectangleF(0, 0, Board.CanvasW, logicalH));
            }
            else if (GlassMode)
            {
                // Optional frosted plate (--glass). Off by default: the keys alone are
                // enough and keep the overlay as unobtrusive as possible.
                DrawGlassPlate(gg, logicalH);
            }

            for (int i = 0; i < Input.Keys.Length; i++)
                DrawKey(gg, Input.Keys[i], Board.Rects[i], scale);

            DrawBadge(gg, hover);
            DrawHandle(gg);
            DrawGear(gg, hover);
            DrawMin(gg, hover);
            DrawClose(gg, hover, hover);
        }

        /// Dark translucent "frosted glass" plate behind the keys.
        static void DrawGlassPlate(Graphics gg, int logicalH)
        {
            float inset = 6f;
            var plate = new RectangleF(inset, inset,
                Board.CanvasW - inset * 2, logicalH - inset * 2);

            // Soft outer glow so the plate edge does not cut hard into the footage.
            for (int i = 0; i < 3; i++)
            {
                float d = 3f + i * 4f;
                var outer = new RectangleF(plate.X - d, plate.Y - d,
                    plate.Width + d * 2, plate.Height + d * 2);
                using (var p = RoundPath(outer, 20f + d))
                using (var pen = new Pen(Color.FromArgb(26 - i * 7, 8, 5, 16), 3f + d))
                    gg.DrawPath(pen, p);
            }

            // The plate itself: dark, fairly opaque, slight vertical gradient.
            using (var p = RoundPath(plate, 20f))
            {
                using (var fill = new LinearGradientBrush(plate,
                    Color.FromArgb(168, 16, 12, 26),
                    Color.FromArgb(140, 10, 7, 18), 90f))
                    gg.FillPath(fill, p);

                // glass rim: brighter on the top edge, fading around
                using (var rim = new LinearGradientBrush(plate,
                    Color.FromArgb(70, 255, 255, 255),
                    Color.FromArgb(18, 255, 255, 255), 90f))
                using (var pen = new Pen(rim, 1.6f))
                    gg.DrawPath(pen, p);
            }

            // subtle top sheen
            using (var sheenPath = new GraphicsPath())
            {
                var top = new RectangleF(plate.X + 14, plate.Y + 4, plate.Width - 28, plate.Height * 0.45f);
                using (var fill = new LinearGradientBrush(top,
                    Color.FromArgb(30, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255), 90f))
                    gg.FillRectangle(fill, top);
            }
        }
        static void DrawKey(Graphics gg, KeyState k, KeyRect r, float dpi)
        {
            float glow = k.Glow;
            var rr = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.W - 1f, r.H - 1f);

            // soft glow while held
            if (glow > 0.02f)
            {
                for (int i = 0; i < 4; i++)
                {
                    float d = 1.5f + i * 2.4f;
                    int a = (int)(58 * glow * (1f - i * 0.25f));
                    var outer = new RectangleF(rr.X - d, rr.Y - d, rr.Width + d * 2, rr.Height + d * 2);
                    using (var p = RoundPath(outer, 12f + d))
                    using (var pen = new Pen(Color.FromArgb(a, Accent), 2.2f + d * 0.8f))
                        gg.DrawPath(pen, p);
                }
            }

            using (var path = RoundPath(rr, 12f))
            {
                Color top = Mix(KeyFillTop, KeyPressedTop, glow);
                Color bot = Mix(KeyFillBot, KeyPressedBot, glow);
                using (var fill = new LinearGradientBrush(
                    new RectangleF(rr.X, rr.Y, rr.Width, rr.Height + 1f), top, bot, 90f))
                    gg.FillPath(fill, path);

                Color edge = Mix(KeyEdge, Accent, glow);
                using (var pen = new Pen(edge, 2.4f))
                    gg.DrawPath(pen, path);
            }

            Color txt = Mix(TextDim, Color.White, glow);
            float track = k.Caption.Length <= 1 ? 0f : 2.4f;
            var box = k.CaptionBox;
            track = FitTracking(gg, k.Caption, fontKey, box.Width - 6f, track);
            // drop shadow keeps the label legible over bright game footage
            using (var sh = new SolidBrush(Color.FromArgb(150, 12, 8, 22)))
                DrawSpacedBrush(gg, k.Caption, fontKey, sh, new RectangleF(box.X + 1.2f, box.Y + 1.6f, box.Width, box.Height), track);
            DrawSpaced(gg, k.Caption, fontKey, txt, box, track);
        }

        static void DrawBadge(Graphics gg, bool hover)
        {
            float cx = Board.BadgeCX, cy = Board.BadgeCY, r = Board.BadgeR;
            var circle = new RectangleF(cx - r, cy - r, r * 2, r * 2);

            using (var fill = new SolidBrush(Color.FromArgb(hover ? 132 : 104, 34, 25, 58)))
                gg.FillEllipse(fill, circle);
            using (var ring = new Pen(Color.FromArgb(hover ? 205 : 165, 162, 136, 240), 1.7f))
                gg.DrawEllipse(ring, circle);
            using (var arc = new Pen(Color.FromArgb(hover ? 190 : 140, Accent), 1.7f))
                gg.DrawArc(arc, circle, 205f, 130f);

            // tiny red ornament, as in the reference
            using (var dot = new SolidBrush(Color.FromArgb(205, 226, 96, 140)))
                gg.FillEllipse(dot, cx + r * 0.40f, cy - r * 0.90f, 3.4f, 3.4f);

            DrawSpaced(gg, "CPS", fontCpsLabel, Color.FromArgb(220, 214, 240),
                new RectangleF(cx - r, cy - r * 0.80f, r * 2, 14), 3.4f);

            using (var sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using (var vb = new SolidBrush(TextDim))
                    gg.DrawString(((int)Input.Cps).ToString(), fontCpsValue, vb,
                        new RectangleF(cx - r, cy - r * 0.30f, r * 2, r * 0.95f), sf);
            }

            DrawSpaced(gg, "PEAK " + Input.PeakCps, fontPeak, Color.FromArgb(190, 182, 214),
                new RectangleF(cx - r, cy + r * 0.50f, r * 2, 14), 2.4f);
        }

        static void DrawHandle(Graphics gg)
        {
            RectangleF h = Board.HandleRect;
            using (var b = new SolidBrush(Color.FromArgb(72, 200, 190, 240)))
            {
                float w = 4.5f, gap = 3f;
                for (int i = 0; i < 2; i++)
                {
                    var x = h.X + i * (w + gap);
                    using (var p = RoundPath(new RectangleF(x, h.Y, w, 7.5f), 2.2f))
                        gg.FillPath(b, p);
                }
            }
        }

        static void DrawGear(Graphics gg, bool hover)
        {
            RectangleF r = Board.GearRect;
            using (var fill = new SolidBrush(Color.FromArgb(hover ? 110 : 55, 52, 40, 78)))
            using (var p = RoundPath(r, 10f))
                gg.FillPath(fill, p);

            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            Color col = Color.FromArgb(hover ? 235 : 155, 232, 226, 250);
            using (var pen = new Pen(col, 1.5f))
                gg.DrawEllipse(pen, cx - 3.2f, cy - 3.2f, 6.4f, 6.4f);
            using (var pen = new Pen(col, 1.7f))
            {
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 3.0 * i;
                    float x1 = cx + (float)Math.Cos(a) * 6.2f;
                    float y1 = cy + (float)Math.Sin(a) * 6.2f;
                    float x2 = cx + (float)Math.Cos(a) * 8.4f;
                    float y2 = cy + (float)Math.Sin(a) * 8.4f;
                    gg.DrawLine(pen, x1, y1, x2, y2);
                }
            }
        }

        static void DrawPanel(Graphics gg, bool remember, bool lockPos)
        {
            RectangleF pr = Board.PanelRect;

            // Nearly opaque backdrop: the panel REPLACES the key view, so the layout
            // underneath must not bleed through.
            using (var fill = new SolidBrush(Color.FromArgb(250, 22, 16, 34)))
            using (var path = RoundPath(pr, 14f))
                gg.FillPath(fill, path);
            using (var pen = new Pen(Color.FromArgb(235, 176, 150, 252), 2.4f))
            using (var path = RoundPath(pr, 14f))
                gg.DrawPath(pen, path);

            using (var b = new SolidBrush(Color.FromArgb(255, 246, 242, 255)))
            using (var sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Alignment = StringAlignment.Near;
                gg.DrawString("Settings", fontTitle, b,
                    new RectangleF(pr.X + 26, pr.Y + 12, 220, 30), sf);
            }

            DrawCheckRow(gg, Board.OptRemember, remember, "Remember position");
            DrawCheckRow(gg, Board.OptLock, lockPos, "Lock position (no drag)");


            DrawButton(gg, Board.BtnReset, "Reset position", false);
            DrawButton(gg, Board.BtnClosePanel, "x", true);
        }

        /// Text shown in the panel's hint box (browser-source URL).


        static void DrawCheckRow(Graphics gg, Rectangle box, bool on, string label)
        {
            var boxR = new RectangleF(box.X, box.Y, box.Width, box.Height);
            using (var fill = new SolidBrush(Color.FromArgb(on ? 210 : 40, on ? Accent : Color.FromArgb(120, 110, 150))))
            using (var p = RoundPath(boxR, 6f))
                gg.FillPath(fill, p);
            using (var pen = new Pen(Color.FromArgb(200, 176, 156, 238), 1.4f))
            using (var p = RoundPath(boxR, 6f))
                gg.DrawPath(pen, p);

            if (on)
            {
                using (var tick = new Pen(Color.FromArgb(255, 255, 255, 255), 2.4f))
                {
                    float x = box.X + 6f, y = box.Y + box.Height * 0.55f;
                    gg.DrawLine(tick, x, y, x + 5f, y + 5f);
                    gg.DrawLine(tick, x + 5f, y + 5f, x + 13f, y - 6f);
                }
            }

            using (var sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Alignment = StringAlignment.Near;
                using (var b = new SolidBrush(Color.FromArgb(240, 236, 248)))
                    gg.DrawString(label, fontOption, b,
                        new RectangleF(box.Right + 12, box.Y, 300, box.Height), sf);
            }
        }

        static void DrawButton(Graphics gg, Rectangle r, string text, bool small)
        {
            var rr = new RectangleF(r.X, r.Y, r.Width, r.Height);
            using (var fill = new SolidBrush(Color.FromArgb(70, 70, 54, 100)))
            using (var p = RoundPath(rr, 7f))
                gg.FillPath(fill, p);
            using (var pen = new Pen(Color.FromArgb(170, 160, 186), 1.2f))
            using (var p = RoundPath(rr, 7f))
                gg.DrawPath(pen, p);
            using (var b = new SolidBrush(Color.FromArgb(240, 236, 250)))
            using (var sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                gg.DrawString(text, small ? fontCpsLabel : fontOption, b, rr, sf);
            }
        }

        static void DrawMin(Graphics gg, bool hover)
        {
            RectangleF r = Board.MinRect;
            using (var fill = new SolidBrush(Color.FromArgb(hover ? 110 : 55, 52, 40, 78)))
            using (var p = RoundPath(r, 10f))
                gg.FillPath(fill, p);

            float cx = r.X + r.Width / 2f;
            float y = r.Y + r.Height / 2f + 3f;
            Color col = Color.FromArgb(hover ? 235 : 155, 232, 226, 250);
            using (var pen = new Pen(col, 2.0f))
                gg.DrawLine(pen, cx - 6f, y, cx + 6f, y);
        }

        static void DrawClose(Graphics gg, bool hover, bool windowHover)
        {
            RectangleF r = Board.CloseRect;
            // always faintly present so it can be found, brighter on hover
            int a = hover ? 240 : (windowHover ? 170 : 105);
            using (var fill = new SolidBrush(Color.FromArgb(hover ? 200 : (windowHover ? 110 : 55), 52, 40, 78)))
            using (var p = RoundPath(r, 11f))
                gg.FillPath(fill, p);
            using (var pen = new Pen(Color.FromArgb(a, 236, 230, 252), 1.7f))
            {
                float m = 6.5f;
                gg.DrawLine(pen, r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
                gg.DrawLine(pen, r.Right - m, r.Y + m, r.X + m, r.Bottom - m);
            }
        }

        /// Renders the widget to a PNG (used by --dump, and handy for docs).
        public static void DrawDump(string path, float zoom, float cps, int peak,
                                    bool panel = false, int alpha = 255)
        {
            float dpi;
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
                dpi = pg.DpiX / 96f;
            if (dpi <= 0f) dpi = 1f;

            Input.Cps = cps;
            Input.PeakCps = peak;
            ConstantAlpha = alpha;

            float scale = dpi * zoom;
            int w = (int)Math.Round(Board.CanvasW * scale);
            int h = (int)Math.Round(Board.Height(panel) * scale);

            try
            {
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                        Draw(g, scale, false, false, panel, true, false);
                    // bake the window opacity into the preview so it matches the app
                    if (alpha < 255)
                    {
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                Color c = bmp.GetPixel(x, y);
                                if (c.A == 0) continue;
                                bmp.SetPixel(x, y, Color.FromArgb(
                                    c.A,
                                    c.R * alpha / 255,
                                    c.G * alpha / 255,
                                    c.B * alpha / 255));
                            }
                    }
                    string tmp = path + ".tmp";
                    bmp.Save(tmp, ImageFormat.Png);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    System.IO.File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                string log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keystroke_dump_error.txt");
                System.IO.File.WriteAllText(log, ex.ToString());
                throw;
            }
        }

        // --- helpers ---
        static GraphicsPath RoundPath(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0) return a;
            if (t >= 1) return Color.FromArgb(b.A, b.R, b.G, b.B);
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// Narrows letter spacing until the spaced text fits the given width.
        static float FitTracking(Graphics gg, string text, Font font, float maxWidth, float spacing)
        {
            var near = new StringFormat();
            near.Alignment = StringAlignment.Near;
            near.LineAlignment = StringAlignment.Center;
            for (int guard = 0; guard < 40; guard++)
            {
                float total = 0f;
                for (int i = 0; i < text.Length; i++)
                {
                    total += gg.MeasureString(text[i].ToString(), font, PointF.Empty, near).Width;
                    if (i < text.Length - 1) total += spacing;
                }
                if (total <= maxWidth || spacing <= 0f) return Math.Max(0f, spacing);
                spacing -= 0.2f;
            }
            return 0f;
        }

        static void DrawSpacedBrush(Graphics gg, string text, Font font, Brush brush,
            RectangleF box, float spacing)
        {
            if (string.IsNullOrEmpty(text)) return;
            float[] widths = new float[text.Length];
            float total = 0f;
            var near = new StringFormat();
            near.Alignment = StringAlignment.Near;
            near.LineAlignment = StringAlignment.Center;
            for (int i = 0; i < text.Length; i++)
            {
                widths[i] = gg.MeasureString(text[i].ToString(), font, PointF.Empty, near).Width;
                total += widths[i] + (i < text.Length - 1 ? spacing : 0f);
            }
            float x = box.X + (box.Width - total) * 0.5f;
            for (int i = 0; i < text.Length; i++)
            {
                gg.DrawString(text[i].ToString(), font, brush,
                    new RectangleF(x, box.Y, widths[i] + spacing + 2f, box.Height), near);
                x += widths[i] + spacing;
            }
        }

        static void DrawSpaced(Graphics gg, string text, Font font, Color color,
            RectangleF box, float spacing)
        {
            if (string.IsNullOrEmpty(text)) return;
            float[] widths = new float[text.Length];
            float total = 0f;
            var near = new StringFormat();
            near.Alignment = StringAlignment.Near;
            near.LineAlignment = StringAlignment.Center;
            for (int i = 0; i < text.Length; i++)
            {
                widths[i] = gg.MeasureString(text[i].ToString(), font, PointF.Empty, near).Width;
                total += widths[i] + (i < text.Length - 1 ? spacing : 0f);
            }
            float x = box.X + (box.Width - total) * 0.5f;
            using (var br = new SolidBrush(color))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    gg.DrawString(text[i].ToString(), font, br,
                        new RectangleF(x, box.Y, widths[i] + spacing + 2f, box.Height), near);
                    x += widths[i] + spacing;
                }
            }
        }
    }

    // ---------------------------------------------------------
    //  Layered window surface (premultiplied BGRA)
    // ---------------------------------------------------------
    sealed class Surface : IDisposable
    {
        IntPtr hdc, hbmp, oldBmp;
        public IntPtr Bits;
        public int Width, Height;

        /// When true, the backing bitmap is a plain device bitmap (no alpha), which
        /// makes GDI BitBlt window capture work. UpdateLayeredWindow is not used then.
        public bool Solid;

        /// 255 = fully opaque. Below that the layered window becomes uniformly
        /// translucent through UpdateLayeredWindow's SourceConstantAlpha.
        public int ConstantAlpha = 255;

        public Surface(int w, int h) { Resize(w, h); }

        public Surface(int w, int h, bool solid) { Solid = solid; Resize(w, h); }

        public void Resize(int w, int h)
        {
            if (w == Width && h == Height && hdc != IntPtr.Zero) return;
            Dispose();

            Width = Math.Max(1, w);
            Height = Math.Max(1, h);

            // Keep one Graphics instance so the HDC is handed back to the same object.
            using (Graphics screenG = Graphics.FromHwnd(IntPtr.Zero))
            {
                IntPtr screen = screenG.GetHdc();
                try
                {
                    hdc = Native.CreateCompatibleDC(screen);
                    var bi = new Native.BITMAPINFO();
                    bi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFO));
                    bi.biWidth = Width;
                    bi.biHeight = -Height;      // top-down
                    bi.biPlanes = 1;
                    bi.biBitCount = 32;
                    bi.biCompression = 0;
                    if (Solid)
                    {
                        hbmp = Native.CreateCompatibleBitmap(screen, Width, Height);
                        if (hbmp == IntPtr.Zero) throw new Exception("CreateCompatibleBitmap failed");
                        oldBmp = Native.SelectObject(hdc, hbmp);
                        Bits = IntPtr.Zero;
                    }
                    else
                    {
                        IntPtr bits;
                        hbmp = Native.CreateDIBSection(hdc, ref bi, 0, out bits, IntPtr.Zero, 0);
                        if (hbmp == IntPtr.Zero) throw new Exception("CreateDIBSection failed");
                        oldBmp = Native.SelectObject(hdc, hbmp);
                        Bits = bits;
                    }
                }
                finally
                {
                    screenG.ReleaseHdc(screen);
                }
            }
        }

        public Graphics CreateGraphics()
        {
            Graphics g = Graphics.FromHdc(hdc);
            g.Clear(Color.Transparent);
            return g;
        }

        /// GDI+ writes straight alpha; UpdateLayeredWindow wants premultiplied.
        public unsafe void Premultiply()
        {
            if (Solid || Bits == IntPtr.Zero) return;
            byte* p = (byte*)Bits;
            int n = Width * Height;
            for (int i = 0; i < n; i++)
            {
                byte a = p[i * 4 + 3];
                if (a == 0)
                {
                    p[i * 4 + 0] = 0;
                    p[i * 4 + 1] = 0;
                    p[i * 4 + 2] = 0;
                }
                else if (a != 255)
                {
                    p[i * 4 + 0] = (byte)(p[i * 4 + 0] * a / 255);
                    p[i * 4 + 1] = (byte)(p[i * 4 + 1] * a / 255);
                    p[i * 4 + 2] = (byte)(p[i * 4 + 2] * a / 255);
                }
            }
        }

        /// Alpha at a device pixel, for click-through hit testing.
        public unsafe byte AlphaAt(int x, int y)
        {
            if (Solid) return 255;              // whole window is opaque
            if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
            byte* p = (byte*)Bits;
            return p[((y * Width) + x) * 4 + 3];
        }

        public void Push(IntPtr hwnd, int x, int y)
        {
            if (Solid)
            {
                // plain BitBlt straight to the window DC: capturable by OBS BitBlt
                using (Graphics wg = Graphics.FromHwnd(hwnd))
                {
                    IntPtr wdc = wg.GetHdc();
                    try { Native.BitBlt(wdc, 0, 0, Width, Height, hdc, 0, 0, 0x00CC0020); }
                    finally { wg.ReleaseHdc(wdc); }
                }
                return;
            }
            var dst = new Native.POINT(x, y);
            var src = new Native.POINT(0, 0);
            var size = new Native.SIZE(Width, Height);
            var blend = new Native.BLENDFUNCTION();
            blend.BlendOp = Native.AC_SRC_OVER;
            blend.SourceConstantAlpha = (byte)Math.Max(1, Math.Min(255, ConstantAlpha));
            blend.AlphaFormat = Native.AC_SRC_ALPHA;
            Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, hdc, ref src,
                0, ref blend, Native.ULW_ALPHA);
        }

        public void Dispose()
        {
            if (hdc != IntPtr.Zero)
            {
                if (oldBmp != IntPtr.Zero) Native.SelectObject(hdc, oldBmp);
                Native.DeleteDC(hdc);
                hdc = IntPtr.Zero;
            }
            if (hbmp != IntPtr.Zero) { Native.DeleteObject(hbmp); hbmp = IntPtr.Zero; }
            oldBmp = IntPtr.Zero;
            Bits = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------
    //  Persisted settings
    // ---------------------------------------------------------
    sealed class Settings
    {
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public float Zoom = 1f;
        public bool RememberPosition = true;
        public bool LockPosition = false;
        public int Alpha = 153;          // ~60% of 255

        public static string FilePath
        {
            get
            {
                // Relative to the executable, so the whole folder can be moved anywhere.
                string dir = System.AppDomain.CurrentDomain.BaseDirectory;
                return System.IO.Path.Combine(dir, "settings.txt");
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!System.IO.File.Exists(FilePath)) return s;
                foreach (string raw in System.IO.File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    float f; int i;
                    switch (k)
                    {
                        case "x": if (int.TryParse(v, out i)) s.X = i; break;
                        case "y": if (int.TryParse(v, out i)) s.Y = i; break;
                        case "zoom":
                            if (float.TryParse(v, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out f)) s.Zoom = f;
                            break;
                        case "remember": s.RememberPosition = v == "1"; break;
                        case "lock": s.LockPosition = v == "1"; break;
                        case "alpha":
                            if (int.TryParse(v, out i)) s.Alpha = Math.Max(20, Math.Min(255, i));
                            break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# KeystrokeOverlay settings");
                sb.AppendLine("x=" + X);
                sb.AppendLine("y=" + Y);
                sb.AppendLine("zoom=" + Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine("remember=" + (RememberPosition ? "1" : "0"));
                sb.AppendLine("lock=" + (LockPosition ? "1" : "0"));
                sb.AppendLine("alpha=" + Alpha);
                System.IO.File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }
    }


    // ---------------------------------------------------------
    //  Window
    // ---------------------------------------------------------
    sealed class OverlayForm : Form
    {
        const float ZoomMin = 0.6f;
        const float ZoomMax = 2.5f;

        readonly Timer timer;
        readonly Surface surface;
        readonly Settings settings;
        public static bool SolidMode;

        float dpiScale = 1f;
        float zoom = 1f;
        bool hovering, hoveringClose;
        bool dragging;
        Point dragStartCursor;
        Point dragStartWindow;
        bool movedSinceDown;

        bool panelOpen;
        bool hoverGear, hoverMin, hoverPanelClose, hoverRemember, hoverLock, hoverReset;
        DateTime lastMoveUtc = DateTime.MinValue;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (!SolidMode) cp.ExStyle |= (int)Native.WS_EX_LAYERED;
                cp.ExStyle &= ~(int)Native.WS_EX_TOOLWINDOW;
                cp.ExStyle &= ~(int)Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public OverlayForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            TopMost = true;
            Text = "KeystrokeOverlay";
            BackColor = Color.FromArgb(1, 1, 1);
            try { Icon = LoadAppIcon(); } catch { }

            using (var b = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(b))
                dpiScale = g.DpiX / 96f;
            if (dpiScale <= 0f) dpiScale = 1f;

            settings = Settings.Load();
            zoom = Math.Max(ZoomMin, Math.Min(ZoomMax, settings.Zoom));
            surface = new Surface(1, 1, SolidMode);
            surface.ConstantAlpha = Program.StartAlpha > 0 ? Program.StartAlpha : settings.Alpha;
            Render.ConstantAlpha = surface.ConstantAlpha;

            ApplyScale();

            if (settings.RememberPosition && settings.X != int.MinValue)
            {
                Location = new Point(settings.X, settings.Y);
            }
            else
            {
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(
                    Math.Max(0, (work.Width - ClientSize.Width) / 2),
                    Math.Max(0, work.Height / 6));
            }
            ClampToScreen();

            CreateTrayIcon();

            timer = new Timer();
            timer.Interval = 16;
            timer.Tick += OnTick;
        }

        // ---- tray icon: keeps the tool reachable while hidden ----
        NotifyIcon tray;

        void CreateTrayIcon()
        {
            try
            {
                var menu = new ContextMenuStrip();
                menu.Items.Add("Show widget", null, delegate { RestoreFromTray(); });
                menu.Items.Add("Exit", null, delegate { ExitApp(); });

                tray = new NotifyIcon();
                tray.Icon = LoadAppIcon();
                tray.Text = "KeystrokeOverlay";
                tray.Visible = true;
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { RestoreFromTray(); };
            }
            catch { tray = null; }
        }

        void MinimizeToTray()
        {
            if (tray == null)
            {
                // Never hide without a way back.
                MessageBox.Show("Tray icon unavailable, so the widget cannot be hidden.",
                    "KeystrokeOverlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            tray.ShowBalloonTip(1500, "KeystrokeOverlay",
                "Still running. Double-click the tray icon to bring the widget back.", ToolTipIcon.Info);
            Hide();
        }

        void RestoreFromTray()
        {
            Show();
            TopMost = true;
            RenderFrame();
        }

        void ExitApp()
        {
            Close();
        }

        /// Loads the icon compiled into the exe (csc /win32icon:app.ico).
        /// Falls back to a generic icon only if extraction fails.
        static Icon LoadAppIcon()
        {
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch { }
            return SystemIcons.Application;
        }

        static GraphicsPath RoundedRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void ApplyScale()
        {
            Render.Scale = dpiScale * zoom;
            int w = (int)Math.Round(Board.CanvasW * Render.Scale);
            int h = (int)Math.Round(Board.Height(panelOpen) * Render.Scale);
            if (ClientSize.Width != w || ClientSize.Height != h)
            {
                Size = new Size(w, h);
                surface.Resize(w, h);
            }
        }

        void ClampToScreen()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int x = Math.Max(work.Left, Math.Min(Location.X, work.Right - ClientSize.Width));
            int y = Math.Max(work.Top, Math.Min(Location.Y, work.Bottom - 40));
            if (x != Location.X || y != Location.Y) Location = new Point(x, y);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RenderFrame();
            timer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            PersistPosition();

            // Closing from the tray "Exit" item (or the x button) really quits; hiding
            // must not be mistaken for closing.
            if (tray != null)
            {
                try { tray.Visible = false; tray.Dispose(); } catch { }
                tray = null;
            }
            Program.StopServer();
            base.OnFormClosing(e);
        }

        void PersistPosition()
        {
            settings.Zoom = zoom;
            settings.Alpha = surface.ConstantAlpha;
            if (settings.RememberPosition)
            {
                settings.X = Location.X;
                settings.Y = Location.Y;
            }
            settings.Save();
        }

        void OnTick(object sender, EventArgs e)
        {
            Input.Sample();

            if (dragging && !settings.LockPosition
                && (Native.GetAsyncKeyState(Input.VK_LBUTTON) & 0x8000) != 0)
            {
                Point cur = Cursor.Position;
                var delta = new Size(cur.X - dragStartCursor.X, cur.Y - dragStartCursor.Y);
                if (Math.Abs(delta.Width) > 2 || Math.Abs(delta.Height) > 2)
                {
                    movedSinceDown = true;
                    lastMoveUtc = DateTime.UtcNow;
                }
                Location = new Point(dragStartWindow.X + delta.Width, dragStartWindow.Y + delta.Height);
            }
            else if (dragging)
            {
                dragging = false;
                PersistPosition();
            }

            if (movedSinceDown && !dragging
                && (DateTime.UtcNow - lastMoveUtc).TotalMilliseconds > 400)
            {
                movedSinceDown = false;
                PersistPosition();
            }

            RenderFrame();
        }

        void RenderFrame()
        {
            using (var g = surface.CreateGraphics())
                Render.Draw(g, Render.Scale, hovering, hoveringClose,
                            panelOpen, settings.RememberPosition, settings.LockPosition);
            surface.Premultiply();
            surface.Push(Handle, Location.X, Location.Y);
        }

        // ---------------- mouse ----------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            Point p = e.Location;

            if (panelOpen)
            {
                if (Hit(Board.BtnClosePanel, p)) { panelOpen = false; ApplyScale(); RenderFrame(); return; }
                if (Hit(Board.OptRemember, p))
                {
                    settings.RememberPosition = !settings.RememberPosition;
                    settings.Save(); RenderFrame(); return;
                }
                if (Hit(Board.OptLock, p))
                {
                    settings.LockPosition = !settings.LockPosition;
                    settings.Save(); RenderFrame(); return;
                }
                if (Hit(Board.BtnReset, p))
                {
                    Rectangle work = Screen.PrimaryScreen.WorkingArea;
                    Location = new Point((work.Width - ClientSize.Width) / 2, work.Height / 6);
                    PersistPosition(); RenderFrame(); return;
                }
            }

            if (Hit(Board.GearRect, p))
            {
                panelOpen = !panelOpen;
                ApplyScale();
                RenderFrame();
                return;
            }

            if (Hit(Board.MinRect, p))
            {
                MinimizeToTray();
                return;
            }

            if (Hit(Board.CloseRect, p))
            {
                Close();
                return;
            }

            if (settings.LockPosition) return;

            dragging = true;
            movedSinceDown = false;
            dragStartCursor = Cursor.Position;
            dragStartWindow = Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && dragging)
            {
                dragging = false;
                PersistPosition();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = e.Location;

            bool c = Hit(Board.CloseRect, p);
            bool g = Hit(Board.GearRect, p);
            bool mn = Hit(Board.MinRect, p);
            bool r = panelOpen && Hit(Board.OptRemember, p);
            bool l = panelOpen && Hit(Board.OptLock, p);
            bool rs = panelOpen && Hit(Board.BtnReset, p);
            bool pc = panelOpen && Hit(Board.BtnClosePanel, p);

            if (c != hoveringClose || g != hoverGear || mn != hoverMin || r != hoverRemember ||
                l != hoverLock || rs != hoverReset || pc != hoverPanelClose)
            {
                hoveringClose = c; hoverGear = g; hoverMin = mn; hoverRemember = r;
                hoverLock = l; hoverReset = rs; hoverPanelClose = pc;
                RenderFrame();
            }
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovering = true;
            RenderFrame();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hovering || hoveringClose || hoverGear)
            {
                hovering = false; hoveringClose = false; hoverGear = false;
                RenderFrame();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((Control.ModifierKeys & Keys.Control) == 0) return;

            float factor = e.Delta > 0 ? 1.1f : 1f / 1.1f;
            float next = Math.Max(ZoomMin, Math.Min(ZoomMax, zoom * factor));
            if (Math.Abs(next - zoom) < 0.001f) return;

            zoom = next;
            ApplyScale();
            RenderFrame();
            PersistPosition();
        }

        RectangleF Rect(Rectangle r)
        {
            float s = Render.Scale;
            return new RectangleF(r.X * s, r.Y * s, r.Width * s, r.Height * s);
        }

        bool Hit(Rectangle r, Point p) { return Rect(r).Contains(p); }

        bool HitInteractive(Point client)
        {
            if (Hit(Board.CloseRect, client) || Hit(Board.GearRect, client) || Hit(Board.MinRect, client)) return true;
            if (panelOpen &&
                (Hit(Board.OptRemember, client) || Hit(Board.OptLock, client) ||
                 Hit(Board.BtnReset, client) || Hit(Board.BtnClosePanel, client))) return true;

            float s = Render.Scale;
            foreach (KeyRect kr in Board.Rects)
            {
                var r = new RectangleF(kr.X * s, kr.Y * s, kr.W * s, kr.H * s);
                if (r.Contains(client)) return true;
            }

            float cx = Board.BadgeCX * s, cy = Board.BadgeCY * s, rr = Board.BadgeR * s;
            float dx = client.X - cx, dy = client.Y - cy;
            return dx * dx + dy * dy <= rr * rr;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                Point screen = new Point(unchecked((short)(m.LParam.ToInt64() & 0xFFFF)),
                                         unchecked((short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
                Point client = new Point(screen.X - Location.X, screen.Y - Location.Y);
                bool hit = SolidMode || HitInteractive(client);
                m.Result = (IntPtr)(hit ? Native.HTCLIENT : Native.HTTRANSPARENT);
                return;
            }
            base.WndProc(ref m);
        }

    }

    // ---------------------------------------------------------
    //  Local HTTP server: serves the overlay page for OBS Browser Source
    // ---------------------------------------------------------
    sealed class HttpOverlay : IDisposable
    {
        readonly System.Net.HttpListener listener;
        readonly System.Threading.Thread thread;
        volatile bool running = true;
        public readonly int Port;

        public string Url { get { return "http://127.0.0.1:" + Port + "/"; } }

        public HttpOverlay(int port)
        {
            Port = port;
            listener = new System.Net.HttpListener();
            // Bind both spellings so either URL form works from OBS.
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Prefixes.Add("http://localhost:" + port + "/");
            listener.Start();
            thread = new System.Threading.Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        void Loop()
        {
            while (running)
            {
                try
                {
                    var ctx = listener.GetContext();
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
                }
                catch { if (!running) break; }
            }
        }

        void Handle(System.Net.HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var res = ctx.Response;
                res.Headers.Add("Access-Control-Allow-Origin", "*");
                res.Headers.Add("Cache-Control", "no-store");

                string path = req.Url.AbsolutePath;
                byte[] body;
                string type;

                if (path == "/state")
                {
                    body = System.Text.Encoding.UTF8.GetBytes(BuildState());
                    type = "application/json; charset=utf-8";
                }
                else if (path == "/ping")
                {
                    // Plain-text probe: if OBS shows this, CEF can reach the server and
                    // any blank page is a rendering problem, not a network one.
                    body = System.Text.Encoding.UTF8.GetBytes(
                        "pong\r\nKeystrokeOverlay is serving.\r\nTry: /        (overlay page)\r\n     /state   (live data)\r\n");
                    type = "text/plain; charset=utf-8";
                }
                else if (path == "/" || path == "/index.html")
                {
                    body = System.Text.Encoding.UTF8.GetBytes(Page.Html(Board.CanvasW, Board.CanvasH));
                    type = "text/html; charset=utf-8";
                }
                else
                {
                    res.StatusCode = 404;
                    body = System.Text.Encoding.UTF8.GetBytes("not found");
                    type = "text/plain";
                }

                res.ContentType = type;
                res.ContentLength64 = body.Length;
                res.OutputStream.Write(body, 0, body.Length);
                res.OutputStream.Close();
            }
            catch { }
        }

        static string BuildState()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"keys\":[");
            for (int i = 0; i < Input.Keys.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Input.Keys[i].Held ? "1" : "0");
            }
            sb.Append("],\"glow\":[");
            for (int i = 0; i < Input.Keys.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Input.Keys[i].Glow.ToString("0.00",
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append("],\"cps\":");
            sb.Append(((int)Input.Cps).ToString());
            sb.Append(",\"peak\":");
            sb.Append(Input.PeakCps.ToString());
            sb.Append("}");
            return sb.ToString();
        }

        public void Dispose()
        {
            running = false;
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
    }

    // ---------------------------------------------------------
    //  The browser page (OBS Browser Source)
    // ---------------------------------------------------------
    static class Page
    {
        /// Builds the overlay page. Key elements are emitted server-side so the widget
        /// is visible even if script execution fails (some OBS/CEF setups block it).
        public static string Html(int canvasW, int canvasH)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Board.Rects.Length; i++)
            {
                KeyRect r = Board.Rects[i];
                string cap = Input.Keys[i].Caption;
                bool tall = r.H > Board.KeyW + 4;
                sb.Append("<div class=\"key");
                if (tall) sb.Append(" tall");
                sb.Append("\" id=\"k").Append(i).Append("\" style=\"left:")
                  .Append(r.X).Append("px;top:").Append(r.Y)
                  .Append("px;width:").Append(r.W).Append("px;height:").Append(r.H)
                  .Append("px\"><span>").Append(cap).Append("</span><i class=\"dot\"></i></div>");
            }

            string tpl = Template;
            tpl = tpl.Replace("__W__", canvasW.ToString());
            tpl = tpl.Replace("__H__", canvasH.ToString());
            tpl = tpl.Replace("__KEYS__", sb.ToString());
            tpl = tpl.Replace("__BADGEX__", (Board.BadgeCX - Board.BadgeR).ToString());
            tpl = tpl.Replace("__BADGEY__", (Board.BadgeCY - Board.BadgeR).ToString());
            tpl = tpl.Replace("__BADGED__", (Board.BadgeR * 2).ToString());
            return tpl;
        }

        // ASCII-only on purpose: avoids any encoding surprises inside OBS's CEF.
        const string Template = @"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<title>KeystrokeOverlay</title>
<style>
  html, body { margin:0; padding:0; background:transparent; overflow:hidden; width:100%; height:100%; }
  #wrap { position:absolute; left:0; top:0; width:__W__px; height:__H__px; }
  #glass {
    display:none; position:absolute; left:6px; top:6px; right:6px; bottom:6px;
    border-radius:20px;
    background:linear-gradient(180deg, rgba(16,12,26,0.66), rgba(10,7,18,0.55));
    box-shadow:0 0 18px rgba(8,5,16,0.55), inset 0 1px 0 rgba(255,255,255,0.28);
    border:1.6px solid rgba(255,255,255,0.16);
  }
  .key {
    position:absolute; box-sizing:border-box; border-radius:14px;
    border:2.4px solid rgba(196,176,255,0.90);
    background:linear-gradient(180deg, rgba(62,48,112,0.46), rgba(36,26,64,0.36));
    color:#fffdfa; font-family:'Segoe UI', Arial, sans-serif; font-weight:600;
    font-size:15px; letter-spacing:2.5px;
    display:flex; align-items:center; justify-content:center;
    transition: background 60ms linear, border-color 60ms linear, box-shadow 60ms linear;
    text-shadow: 0 1px 2px rgba(0,0,0,0.75);
  }
  .key > span { position:absolute; left:0; top:0; right:0; bottom:0;
                display:flex; align-items:center; justify-content:center; }
  .key.on {
    background:linear-gradient(180deg, rgba(150,120,240,0.72), rgba(96,70,170,0.58));
    border-color:#b497ff;
    box-shadow:0 0 14px rgba(160,130,255,0.75), 0 0 26px rgba(140,110,240,0.45);
  }
  .key .dot {
    position:absolute; right:9px; top:7px; width:4px; height:4px; border-radius:50%;
    background:rgba(120,110,160,0.55); transition:background 80ms linear;
  }
  .key.on .dot { background:#ff62c8; }
  #badge {
    position:absolute; left:__BADGEX__px; top:__BADGEY__px;
    width:__BADGED__px; height:__BADGED__px; border-radius:50%;
    border:2.4px solid rgba(196,176,255,0.85);
    background:rgba(26,20,44,0.72);
    display:flex; flex-direction:column; align-items:center; justify-content:center;
    color:#fffdfa; font-family:'Segoe UI', Arial, sans-serif;
    text-shadow:0 1px 2px rgba(0,0,0,0.8);
  }
  #badge .label { font-size:12px; letter-spacing:3px; opacity:0.92; margin-top:-6px; }
  #badge .value { font-size:46px; font-weight:700; line-height:1.0; }
  #badge .peak  { font-size:11px; letter-spacing:2px; opacity:0.85; margin-top:2px; }
</style>
</head>
<body>
<div id=""wrap"">
  <div id=""glass""></div>
  __KEYS__
  <div id=""badge"">
    <div class=""label"">CPS</div>
    <div class=""value"" id=""cps"">0</div>
    <div class=""peak"" id=""peak"">PEAK 0</div>
  </div>
</div>
<script>
  // Elements already exist in the HTML; this only adds live highlighting.
  var els = [];
  for (var i = 0; i < 8; i++) els.push(document.getElementById('k' + i));
  if (location.search.indexOf('glass=1') >= 0) {
    document.getElementById('glass').style.display = 'block';
  }
  var cpsEl = document.getElementById('cps');
  var peakEl = document.getElementById('peak');
  function poll() {
    fetch('/state', {cache:'no-store'})
      .then(function(r){ return r.json(); })
      .then(function(s){
        for (var i = 0; i < els.length; i++) {
          if (!els[i]) continue;
          if (s.keys[i]) els[i].classList.add('on');
          else els[i].classList.remove('on');
        }
        if (cpsEl) cpsEl.textContent = s.cps;
        if (peakEl) peakEl.textContent = 'PEAK ' + s.peak;
      })
      .catch(function(){})
      .then(function(){ setTimeout(poll, 40); });
  }
  poll();
</script>
</body>
</html>";
    }

    // ---------------------------------------------------------
    //  Entry
    // ---------------------------------------------------------
    static class Program
    {
        /// Alpha override from --alpha (0 means "use the saved setting").
        public static int StartAlpha;

        /// Browser-overlay HTTP server (created lazily).
        public static HttpOverlay Server;
        public const int DefaultPort = 8765;

        public static int ServerPort
        {
            get { return Server != null ? Server.Port : DefaultPort; }
        }

        public static bool ServerRunning { get { return Server != null; } }

        public static string ServerUrl
        {
            get { return "http://127.0.0.1:" + ServerPort + "/"; }
        }

        /// Starts the browser-source server, trying a few ports if the default is taken.
        public static bool StartServer()
        {
            if (Server != null) return true;
            for (int port = DefaultPort; port < DefaultPort + 10; port++)
            {
                try
                {
                    Server = new HttpOverlay(port);
                    WriteServerInfo();
                    return true;
                }
                catch (Exception ex)
                {
                    try
                    {
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ks_server_error.txt"),
                            "port " + port + ": " + ex.Message + "\r\n");
                    }
                    catch { }
                    Server = null;
                }
            }
            return false;
        }

        /// Writes the live URL next to the exe so it can always be found.
        static void WriteServerInfo()
        {
            try
            {
                string dir = System.AppDomain.CurrentDomain.BaseDirectory;
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "browser_source_url.txt"),
                    ServerUrl + "\r\n");
                // (URL is written to browser_source_url.txt; the window title also shows it)
            }
            catch { }
        }

        public static void StopServer()
        {
            if (Server == null) return;
            try { Server.Dispose(); } catch { }
            Server = null;
        }

        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Render.Init();

            if (args.Length >= 2 && args[0] == "--dump")
            {
                float zoom = 1f;
                bool panel = false;
                float alphaOverride = 0f;
                float cps = 0f;
                int peak = 0;
                for (int ai = 2; ai < args.Length; ai++)
                {
                    if (args[ai] == "--panel") panel = true;
                    else if (args[ai] == "--alpha" && ai + 1 < args.Length)
                    {
                        int a; if (int.TryParse(args[ai + 1], out a)) alphaOverride = a;
                        ai++;
                    }
                    else
                    {
                        float f;
                        if (float.TryParse(args[ai], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out f))
                        {
                            if (zoom == 1f && ai == 2) zoom = f;
                            else if (cps == 0f && f > 0) cps = f;
                        }
                    }
                }
                Render.DrawDump(args[1], zoom, cps, peak, panel,
                                alphaOverride > 0 ? (int)alphaOverride : 255);
                return;
            }

            // --testsave : round-trip the settings file and report PASS/FAIL.
            if (args.Length >= 1 && args[0] == "--testsave")
            {
                int fails = 0;
                Action<string, bool> chk = (name, ok) =>
                {
                    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
                    if (!ok) fails++;
                };

                var src = new Settings();
                src.X = 321; src.Y = 234; src.Zoom = 1.25f;
                src.RememberPosition = true; src.LockPosition = true; src.Alpha = 153;
                src.Save();

                var back = Settings.Load();
                chk("x survives save/load", back.X == 321);
                chk("y survives save/load", back.Y == 234);
                chk("zoom survives save/load", Math.Abs(back.Zoom - 1.25f) < 0.001f);
                chk("remember flag survives", back.RememberPosition);
                chk("lock flag survives", back.LockPosition);
                chk("alpha survives save/load", back.Alpha == 153);

                var src2 = new Settings();
                src2.X = 10; src2.Y = 20; src2.RememberPosition = false;
                src2.Save();
                var back2 = Settings.Load();
                chk("remember=false is written", !back2.RememberPosition);
                chk("coordinates still stored", back2.X == 10 && back2.Y == 20);

                Console.WriteLine(fails == 0 ? "SETTINGS OK" : "SETTINGS FAILED");
                try { System.IO.File.Delete(Settings.FilePath); } catch { }
                return;
            }

            if (args.Length >= 1 && args[0] == "--geom")
            {
                foreach (KeyState k in Input.Keys)
                    Console.WriteLine("{0,-6} box=({1},{2}) {3}x{4}", k.Caption,
                        k.CaptionBox.X, k.CaptionBox.Y, k.CaptionBox.Width, k.CaptionBox.Height);
                Console.WriteLine("Canvas {0}x{1}", Board.CanvasW, Board.CanvasH);
                return;
            }

            if (args.Length >= 1 && args[0] == "--selftest")
            {
                int fails = 0;
                Action<string, bool> check = (name, ok) =>
                {
                    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
                    if (!ok) fails++;
                };

                var t1 = new List<double> { 5.0, 5.2, 5.5 };
                int p1 = 0;
                check("cps counts presses within 1s",
                      Math.Abs(Input.ComputeCps(t1, 5.6, ref p1) - 3) < 0.001 && p1 == 3);

                var t2 = new List<double> { 1.0, 2.0, 9.5 };
                int p2 = 0;
                check("cps drops expired presses",
                      Math.Abs(Input.ComputeCps(t2, 10.0, ref p2) - 1) < 0.001);

                var t3 = new List<double> { 1.0, 1.1, 1.2, 1.3 };
                int p3 = 0;
                Input.ComputeCps(t3, 1.4, ref p3);
                Input.ComputeCps(t3, 5.0, ref p3);
                check("peak keeps historic maximum", p3 == 4);

                var t4 = new List<double>();
                int p4 = 0;
                check("cps is zero when idle", Input.ComputeCps(t4, 10.0, ref p4) == 0);

                // CAPS and G are displayed but must not contribute to the count.
                bool capsCounted = false, gCounted = false;
                int counted = 0;
                foreach (KeyState k in Input.Keys)
                {
                    if (k.Vk == Input.VK_CAPITAL) capsCounted = k.CountsForCps;
                    if (k.Vk == Input.VK_G) gCounted = k.CountsForCps;
                    if (k.CountsForCps) counted++;
                }
                check("CAPS excluded from cps", !capsCounted);
                check("G excluded from cps", !gCounted);
                check("6 keys still counted", counted == 6);
                check("peak cooldown is 5s", Math.Abs(Input.PeakCooldownSeconds - 5.0) < 0.001);

                Console.WriteLine(fails == 0 ? "SELFTEST OK" : "SELFTEST FAILED");
                return;
            }


            bool noServe = false;
            for (int ai = 0; ai < args.Length; ai++)
            {
                if (args[ai] == "--solid") { OverlayForm.SolidMode = true; Render.SolidMode = true; }
                if (args[ai] == "--glass") Render.GlassMode = true;
                // --serve is the default; kept so the old habit still works.
                if (args[ai] == "--noserve") noServe = true;
                if (args[ai] == "--alpha" && ai + 1 < args.Length)
                {
                    int a;
                    if (int.TryParse(args[ai + 1], out a))
                        StartAlpha = Math.Max(20, Math.Min(255, a));
                }
            }

            // The browser-source server starts automatically: OBS needs it and there
            // is no reason to make the user pass a flag.
            if (!noServe) StartServer();

            try
            {
                Application.Run(new OverlayForm());
            }
            catch (Exception ex)
            {
                string log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keystroke_start_error.txt");
                System.IO.File.WriteAllText(log, ex.ToString());
                MessageBox.Show(ex.ToString(), "KeystrokeOverlay");
            }
        }
    }
}
