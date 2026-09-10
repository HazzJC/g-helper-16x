namespace GHelper.USB
{
    public enum CellKind
    {
        Key,
        Lightbar,
        Logo,
        /// <summary>Drawn for realism but carries no LED (the power button).</summary>
        Dead,
    }

    /// <summary>
    /// One addressable element of the UX7602's lighting, positioned in key units for drawing.
    /// X/Y/W/H are in "one standard key = 1.0" units with the origin at the keyboard's top-left.
    /// </summary>
    /// <param name="Slots">
    /// Every LED slot this element writes. Wide keys list the whole span they physically cover -
    /// only one slot in each span turns out to be live, but writing the dead neighbours is a
    /// harmless no-op and keeps the table robust if the live one differs between units.
    /// </param>
    public record CellDef(string Label, float X, float Y, float W, float H, int[] Slots, CellKind Kind = CellKind.Key);

    /// <summary>
    /// Physical layout of the ZenBook Pro 16X OLED (UX7602) keyboard, ISO/UK.
    ///
    /// Every slot number below was confirmed live by lighting ranges and single slots and reading
    /// the result off the physical keyboard. Notably this does NOT match the inherited Strix
    /// `packetMap` in Aura.cs, which puts gaps at slots 22/27/32 and F10-F12 at 34-36; on this
    /// machine the function row is dense and 34/35/36 are PrtSc/Insert/Delete.
    ///
    /// Dead slots (no physical LED): row 0 (0-20), columns 17-20 of every row, 59-62, 77-78,
    /// 80-83, 101-104, 122-125, 130-131, 133-134, 138, 140-146, 148-158, 162, 164-175.
    /// </summary>
    public static class Zenbook16XLayout
    {
        public const float WIDTH = 16f;
        public const float HEIGHT = 6.05f;

        const float FN_Y = 0f, FN_H = 0.75f;
        const float NUM_Y = 0.85f;
        const float QWE_Y = 1.90f;
        const float ASD_Y = 2.95f;
        const float ZXC_Y = 4.00f;
        const float BOT_Y = 5.05f;

        static CellDef K(string label, float x, float y, float w, int slot, float h = 1f)
            => new(label, x, y, w, h, new[] { slot });

        static CellDef K(string label, float x, float y, float w, int[] slots, float h = 1f)
            => new(label, x, y, w, h, slots);

        public static readonly CellDef[] Cells = BuildCells();

        static CellDef[] BuildCells()
        {
            var cells = new List<CellDef>();

            // ---- Function row: dense, Esc + F1-F12 + PrtSc + Insert + Delete, then Power.
            string[] fnLabels = { "Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "PrtSc", "Insert", "Delete" };
            const float fnW = WIDTH / 17f;
            for (int i = 0; i < fnLabels.Length; i++)
                cells.Add(K(fnLabels[i], i * fnW, FN_Y, fnW, 21 + i, FN_H));
            cells.Add(new CellDef("⏻", 16 * fnW, FN_Y, fnW, FN_H, Array.Empty<int>(), CellKind.Dead));

            // ---- Number row.
            string[] numLabels = { "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" };
            for (int i = 0; i < numLabels.Length; i++)
                cells.Add(K(numLabels[i], i, NUM_Y, 1f, 42 + i));
            cells.Add(K("Backspace", 13f, NUM_Y, 2f, new[] { 55, 56, 57 }));
            cells.Add(K("Home", 15f, NUM_Y, 1f, 58));

            // ---- QWERTY row.
            cells.Add(K("Tab", 0f, QWE_Y, 1.5f, 63));
            string[] qwe = { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]" };
            for (int i = 0; i < qwe.Length; i++)
                cells.Add(K(qwe[i], 1.5f + i, QWE_Y, 1f, 64 + i));
            cells.Add(K("#", 13.5f, QWE_Y, 1.5f, 76));
            cells.Add(K("PgUp", 15f, QWE_Y, 1f, 79));

            // ---- Home row.
            cells.Add(K("Caps Lock", 0f, ASD_Y, 1.75f, 84));
            string[] asd = { "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'" };
            for (int i = 0; i < asd.Length; i++)
                cells.Add(K(asd[i], 1.75f + i, ASD_Y, 1f, 85 + i));
            cells.Add(K("Enter", 12.75f, ASD_Y, 2.25f, new[] { 96, 97, 98, 99 }));
            cells.Add(K("PgDn", 15f, ASD_Y, 1f, 100));

            // ---- Shift row.
            cells.Add(K("Shift", 0f, ZXC_Y, 1.25f, 105));
            string[] zxc = { "\\", "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/" };
            for (int i = 0; i < zxc.Length; i++)
                cells.Add(K(zxc[i], 1.25f + i, ZXC_Y, 1f, 106 + i));
            cells.Add(K("Shift", 12.25f, ZXC_Y, 2.75f, new[] { 117, 118, 119, 120 }));
            cells.Add(K("End", 15f, ZXC_Y, 1f, 121));

            // ---- Bottom row. Only 132 under the spacebar is live; the span is listed anyway.
            cells.Add(K("Ctrl", 0f, BOT_Y, 1.2f, 126));
            cells.Add(K("Fn", 1.2f, BOT_Y, 1.2f, 127));
            cells.Add(K("Win", 2.4f, BOT_Y, 1.2f, 128));
            cells.Add(K("Alt", 3.6f, BOT_Y, 1.2f, 129));
            cells.Add(K("", 4.8f, BOT_Y, 5.2f, new[] { 130, 131, 132, 133, 134 }));
            cells.Add(K("Alt Gr", 10.0f, BOT_Y, 1.2f, 135));
            cells.Add(K("☰", 11.2f, BOT_Y, 1.0f, 136));
            cells.Add(K("Ctrl", 12.2f, BOT_Y, 1.2f, 137));

            // Arrow cluster: up is on row 6, left/down/right on row 7, matching the physical
            // half-height inverted-T.
            cells.Add(K("◀", 13.4f, BOT_Y, 0.87f, 159));
            cells.Add(K("▲", 14.27f, BOT_Y, 0.86f, 139, 0.5f));
            cells.Add(K("▼", 14.27f, BOT_Y + 0.5f, 0.86f, 160, 0.5f));
            cells.Add(K("▶", 15.13f, BOT_Y, 0.87f, 161));

            // ---- Side lightbars: one addressable LED each (confirmed - 148-158, 162, 164-167
            // are all dead, so the bars cannot be given a gradient).
            cells.Add(new CellDef("Left bar", -1.15f, FN_Y, 0.55f, HEIGHT, new[] { Zenbook16X.SLOT_LIGHTBAR_LEFT }, CellKind.Lightbar));
            cells.Add(new CellDef("Right bar", WIDTH + 0.6f, FN_Y, 0.55f, HEIGHT, new[] { Zenbook16X.SLOT_LIGHTBAR_RIGHT }, CellKind.Lightbar));

            // ---- Lid logo: slot 0, full RGB, on this same chunk stream (confirmed live).
            // The rest of row 0 (slots 1-20) is dead. This is NOT the ACPI MonogramLogo call the
            // app used to make for this model - that is a boolean on a different channel and
            // returns failure on this firmware, which is why the logo appeared uncontrollable.
            cells.Add(new CellDef("Lid logo", 7.2f, -1.75f, 1.6f, 1.4f, new[] { 0 }, CellKind.Logo));

            return cells.ToArray();
        }

        /// <summary>Full drawing extent including the lightbars and the lid logo.</summary>
        public static RectangleF Bounds
        {
            get
            {
                float minX = Cells.Min(c => c.X), maxX = Cells.Max(c => c.X + c.W);
                float minY = Cells.Min(c => c.Y), maxY = Cells.Max(c => c.Y + c.H);
                return new RectangleF(minX, minY, maxX - minX, maxY - minY);
            }
        }
    }
}
