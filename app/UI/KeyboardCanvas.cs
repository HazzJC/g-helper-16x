using GHelper.USB;
using System.Drawing.Drawing2D;

namespace GHelper.UI
{
    /// <summary>
    /// Top-down picture of the UX7602's keyboard, side lightbars and lid logo. Click or drag to
    /// paint cells with the current colour; right-click to clear. Scales to fit whatever size the
    /// control is given, preserving aspect ratio.
    /// </summary>
    public class KeyboardCanvas : Control
    {
        /// <summary>Slot-indexed colour buffer. Painting a cell writes to every slot it lists.</summary>
        public Color[] Colors { get; } = new Color[Zenbook16X.SLOTS];

        public Color PaintColor { get; set; } = Color.FromArgb(0, 200, 255);

        public event EventHandler? CellsChanged;

        CellDef? hover;
        bool dragging;
        bool dragErases;

        public KeyboardCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Array.Fill(Colors, Color.Black);
        }

        // ------------------------------------------------------------ geometry

        float scale;
        float offsetX, offsetY;

        void ComputeTransform()
        {
            var b = Zenbook16XLayout.Bounds;
            float pad = 8f;
            float availW = Math.Max(1, Width - pad * 2);
            float availH = Math.Max(1, Height - pad * 2);

            scale = Math.Min(availW / b.Width, availH / b.Height);
            offsetX = pad + (availW - b.Width * scale) / 2f - b.X * scale;
            offsetY = pad + (availH - b.Height * scale) / 2f - b.Y * scale;
        }

        RectangleF RectOf(CellDef c) => new(
            offsetX + c.X * scale,
            offsetY + c.Y * scale,
            Math.Max(1, c.W * scale - scale * 0.06f),
            Math.Max(1, c.H * scale - scale * 0.06f));

        CellDef? HitTest(Point p)
        {
            foreach (var c in Zenbook16XLayout.Cells)
                if (RectOf(c).Contains(p)) return c;
            return null;
        }

        // ------------------------------------------------------------ painting

        /// <summary>The colour a cell currently shows: the first slot it owns.</summary>
        public Color ColorOf(CellDef c) => c.Slots.Length > 0 ? Colors[c.Slots[0]] : Color.Black;

        public void SetCell(CellDef c, Color color)
        {
            if (c.Kind == CellKind.Dead) return;

            foreach (int slot in c.Slots)
                if (slot >= 0 && slot < Colors.Length) Colors[slot] = color;

            Invalidate();
            CellsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void FillAll(Color color)
        {
            foreach (var c in Zenbook16XLayout.Cells)
            {
                if (c.Kind == CellKind.Dead) continue;
                foreach (int slot in c.Slots)
                    if (slot >= 0 && slot < Colors.Length) Colors[slot] = color;
            }
            Invalidate();
            CellsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void LoadColors(Color[] source)
        {
            Array.Copy(source, Colors, Math.Min(source.Length, Colors.Length));
            Invalidate();
        }

        // ------------------------------------------------------------ input

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var cell = HitTest(e.Location);
            if (cell is null) return;

            dragging = true;
            dragErases = e.Button == MouseButtons.Right;
            SetCell(cell, dragErases ? Color.Black : PaintColor);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var cell = HitTest(e.Location);

            if (dragging && cell is not null)
            {
                SetCell(cell, dragErases ? Color.Black : PaintColor);
            }

            if (!ReferenceEquals(cell, hover))
            {
                hover = cell;
                Cursor = cell is null || cell.Kind == CellKind.Dead ? Cursors.Default : Cursors.Hand;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover is not null) { hover = null; Invalidate(); }
            dragging = false;
        }

        // ------------------------------------------------------------ drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            bool dark = RForm.formBack.GetBrightness() < 0.5f;
            Color chassis = dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(48, 48, 52);
            g.Clear(RForm.formBack);

            ComputeTransform();

            // Chassis plate behind the keys, so unlit keys read as keys rather than holes.
            var kb = new RectangleF(offsetX - scale * 0.35f, offsetY - scale * 0.35f,
                                    Zenbook16XLayout.WIDTH * scale + scale * 0.7f,
                                    Zenbook16XLayout.HEIGHT * scale + scale * 0.7f);
            using (var plate = new SolidBrush(chassis))
            using (var path = Rounded(kb, scale * 0.25f))
                g.FillPath(plate, path);

            float fontSize = Math.Max(5.5f, scale * 0.20f);
            using var font = new Font("Segoe UI", fontSize, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.FromArgb(235, 235, 235));
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            foreach (var cell in Zenbook16XLayout.Cells)
            {
                var r = RectOf(cell);
                bool isHover = ReferenceEquals(cell, hover) && cell.Kind != CellKind.Dead;

                DrawCell(g, cell, r, ColorOf(cell), isHover, font, textBrush, format);
            }

            var logo = Zenbook16XLayout.Cells.FirstOrDefault(c => c.Kind == CellKind.Logo);
            if (logo is not null)
            {
                var lr = RectOf(logo);
                using var small = new Font("Segoe UI", Math.Max(5.5f, scale * 0.16f), GraphicsUnit.Pixel);
                using var dim = new SolidBrush(Color.FromArgb(150, RForm.foreMain));
                g.DrawString("lid logo", small, dim,
                    new RectangleF(lr.Left - scale, lr.Bottom, lr.Width + scale * 2, scale * 0.5f), format);
            }
        }

        void DrawCell(Graphics g, CellDef cell, RectangleF r, Color lit, bool hovered,
                      Font font, Brush textBrush, StringFormat format)
        {
            float radius = cell.Kind == CellKind.Lightbar ? r.Width * 0.45f : scale * 0.14f;
            using var path = Rounded(r, radius);

            bool isLit = lit.R + lit.G + lit.B > 12;

            // Keycap body.
            Color body = cell.Kind switch
            {
                CellKind.Dead => Color.FromArgb(58, 58, 62),
                CellKind.Lightbar => Color.FromArgb(20, 20, 22),
                _ => Color.FromArgb(46, 46, 50),
            };

            if (isLit)
            {
                // Tint the cap toward its colour so the whole key reads as lit, not just the text.
                int mix = cell.Kind == CellKind.Lightbar ? 100 : 45;
                body = Color.FromArgb(
                    body.R + (lit.R - body.R) * mix / 100,
                    body.G + (lit.G - body.G) * mix / 100,
                    body.B + (lit.B - body.B) * mix / 100);
            }

            using (var brush = new SolidBrush(body)) g.FillPath(brush, path);

            // Glow ring for lit cells.
            if (isLit)
            {
                using var glow = new Pen(Color.FromArgb(200, lit), Math.Max(1.2f, scale * 0.055f));
                g.DrawPath(glow, path);
            }
            else
            {
                using var edge = new Pen(Color.FromArgb(90, 90, 96), 1f);
                g.DrawPath(edge, path);
            }

            if (hovered)
            {
                using var ring = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1.5f, scale * 0.05f));
                g.DrawPath(ring, path);
            }

            if (!string.IsNullOrEmpty(cell.Label) && cell.Kind == CellKind.Key && r.Height > scale * 0.35f)
            {
                var brush = isLit ? new SolidBrush(Brighten(lit)) : (SolidBrush)textBrush;
                g.DrawString(cell.Label, font, brush, r, format);
                if (isLit) brush.Dispose();
            }
        }

        /// <summary>Legends on a lit key read best as a brighter tint of the key's own colour.</summary>
        static Color Brighten(Color c) => Color.FromArgb(
            Math.Min(255, c.R + (255 - c.R) * 6 / 10),
            Math.Min(255, c.G + (255 - c.G) * 6 / 10),
            Math.Min(255, c.B + (255 - c.B) * 6 / 10));

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            radius = Math.Max(0.5f, Math.Min(radius, Math.Min(r.Width, r.Height) / 2f - 0.5f));
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
