using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CandadoApp
{
    // Ventana por capas con alfa por píxel: el fondo puede ser transparente sin afectar al texto.
    internal sealed class ClockOverlay : Form
    {
        private readonly Settings settings;
        private readonly Countdown countdown;
        private readonly Timer timer = new Timer();
        private readonly Native.WinEventProc foregroundProc;
        private IntPtr foregroundHook;
        private string lastText;
        private char widestDigit = '\0';

        public bool KeepBelowMenu;

        public event EventHandler OpenRequested;
        public event EventHandler MenuRequested;

        public ClockOverlay(Settings settings, Countdown countdown)
        {
            this.settings = settings;
            this.countdown = countdown;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = Program.Name;

            timer.Interval = 200;
            timer.Tick += delegate
            {
                EnsureTopmost();
                Render(false);
            };

            foregroundProc = delegate { EnsureTopmost(); };
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            ApplyClickThrough();
            timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            if (foregroundHook != IntPtr.Zero)
                Native.UnhookWinEvent(foregroundHook);
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && OpenRequested != null)
                OpenRequested(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right && MenuRequested != null)
                MenuRequested(this, EventArgs.Empty);
        }

        public void ApplySettings()
        {
            widestDigit = '\0';
            ApplyClickThrough();
            Render(true);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                Render(true);
                EnsureTopmost();
            }
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            Render(true);
        }

        private void ApplyClickThrough()
        {
            long style = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
            style = settings.ClickThrough ? style | Native.WS_EX_TRANSPARENT : style & ~(long)Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(style));
        }

        private void EnsureTopmost()
        {
            if (Visible && !KeepBelowMenu)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        private string CurrentText()
        {
            if (countdown.Active && settings.ShowCountdown)
                return countdown.Text;

            string format = settings.Use24Hours
                ? (settings.ShowSeconds ? "HH:mm:ss" : "HH:mm")
                : (settings.ShowSeconds ? "h:mm:ss tt" : "h:mm tt");
            return DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
        }

        private Screen TargetScreen()
        {
            return Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.Monitor) ?? Screen.PrimaryScreen;
        }

        private static FontFamily LoadFamily(string name)
        {
            try
            {
                return new FontFamily(name);
            }
            catch (ArgumentException)
            {
                return new FontFamily(GenericFontFamilies.SansSerif);
            }
        }

        private static FontStyle AvailableStyle(FontFamily family)
        {
            foreach (FontStyle style in new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic })
                if (family.IsStyleAvailable(style))
                    return style;
            return FontStyle.Regular;
        }

        private static RectangleF TextBounds(string text, FontFamily family, FontStyle style, float size)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddString(text, family, (int)style, size, PointF.Empty, StringFormat.GenericTypographic);
                return path.GetBounds();
            }
        }

        // Con fuentes proporcionales el ancho cambiaría cada segundo: se reserva el del dígito más ancho.
        private string StableTemplate(string text, FontFamily family, FontStyle style, float size)
        {
            if (widestDigit == '\0')
                widestDigit = "0123456789".OrderByDescending(d => TextBounds(d.ToString(), family, style, size).Width).First();
            return new string(text.Select(c => char.IsDigit(c) ? widestDigit : c).ToArray());
        }

        private static GraphicsPath RoundedRectangle(int width, int height, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(width, height));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(width - d, 0, d, d, 270, 90);
            path.AddArc(width - d, height - d, d, d, 0, 90);
            path.AddArc(0, height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawText(Graphics g, GraphicsPath path, Color color, float penWidth)
        {
            using (SolidBrush brush = new SolidBrush(color))
                g.FillPath(brush, path);
            if (penWidth > 0)
            {
                using (Pen pen = new Pen(color, penWidth) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
            }
            else if (penWidth < 0)
            {
                // Grosor fino: se erosiona el contorno de la capa de texto.
                g.CompositingMode = CompositingMode.SourceCopy;
                using (Pen pen = new Pen(Color.Transparent, -penWidth) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
                g.CompositingMode = CompositingMode.SourceOver;
            }
        }

        private void Render(bool force)
        {
            if (!Visible)
                return;

            string text = CurrentText();
            if (!force && text == lastText)
                return;
            lastText = text;

            Screen screen = TargetScreen();
            Rectangle area = screen.WorkingArea;
            float scale = Native.DpiScaleAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
            float size = settings.FontSize * scale;
            float penWidth = (settings.Weight - Settings.NormalWeight) * size * (settings.Weight > Settings.NormalWeight ? 0.014f : 0.012f);

            using (FontFamily family = LoadFamily(settings.FontName))
            {
                FontStyle style = AvailableStyle(family);
                RectangleF template = TextBounds(StableTemplate(text, family, style, size), family, style, size);

                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddString(text, family, (int)style, size, PointF.Empty, StringFormat.GenericTypographic);
                    RectangleF actual = path.GetBounds();

                    float paddingX = size * 0.35f + Math.Max(penWidth, 0);
                    float paddingY = size * 0.22f + Math.Max(penWidth, 0);
                    int width = (int)Math.Ceiling(Math.Max(template.Width, actual.Width) + paddingX * 2);
                    int height = (int)Math.Ceiling(template.Height + paddingY * 2);

                    using (Matrix move = new Matrix())
                    {
                        move.Translate((width - actual.Width) / 2 - actual.X, (height - template.Height) / 2 - template.Y);
                        path.Transform(move);
                    }

                    using (Bitmap canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                    using (Bitmap textLayer = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(textLayer))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            DrawText(g, path, Color.FromArgb(255, Color.FromArgb(settings.TextColor)), penWidth);
                        }

                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            int alpha = settings.BackOpacity * 255 / 100;
                            if (alpha == 0 && !settings.ClickThrough)
                                alpha = 1; // alfa 0 dejaría pasar los clics
                            using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, Color.FromArgb(settings.BackColor))))
                            using (GraphicsPath background = RoundedRectangle(width, height, size * 0.2f))
                                g.FillPath(brush, background);
                            g.DrawImageUnscaled(textLayer, 0, 0);
                        }

                        Present(canvas, Placement(area, width, height, (int)Math.Round(settings.Margin * scale)));
                    }
                }
            }
        }

        private Point Placement(Rectangle area, int width, int height, int margin)
        {
            int left = area.Left + margin;
            int right = area.Right - width - margin;
            int top = area.Top + margin;
            int bottom = area.Bottom - height - margin;
            switch (settings.Position)
            {
                case ClockPosition.TopLeft: return new Point(left, top);
                case ClockPosition.TopCenter: return new Point(area.Left + (area.Width - width) / 2, top);
                case ClockPosition.BottomLeft: return new Point(left, bottom);
                case ClockPosition.BottomRight: return new Point(right, bottom);
                default: return new Point(right, top);
            }
        }

        private void Present(Bitmap bitmap, Point location)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr previous = Native.SelectObject(memoryDc, hBitmap);
            try
            {
                Native.POINT destination = new Native.POINT(location.X, location.Y);
                Native.SIZE size = new Native.SIZE(bitmap.Width, bitmap.Height);
                Native.POINT source = new Native.POINT(0, 0);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    SourceConstantAlpha = 255,
                    AlphaFormat = Native.AC_SRC_ALPHA
                };
                Native.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.SelectObject(memoryDc, previous);
                Native.DeleteObject(hBitmap);
                Native.DeleteDC(memoryDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
