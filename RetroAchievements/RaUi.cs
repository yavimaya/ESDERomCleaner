namespace ESDERomCleaner;

// Estilo común de los formularios de RetroAchievements: banda oscura con el logotipo, cuerpo claro, botones planos con acento dorado y tarjetas blancas.
internal static class RaUi
{
    public static readonly Color Dark = Color.FromArgb(28, 30, 38), Gold = Color.FromArgb(222, 168, 32), Body = Color.FromArgb(246, 247, 249),
        Border = Color.FromArgb(218, 221, 228), Muted = Color.FromArgb(104, 110, 124), FooterBack = Color.FromArgb(236, 238, 242), Green = Color.FromArgb(34, 130, 70);

    public static Button MakeButton(string text, bool primary, int width = 100)
    {
        var button = new Button { Text = text, Size = new Size(width, 32), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Gold : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(236, 188, 72) : Color.FromArgb(238, 240, 245);
        button.BackColor = primary ? Gold : Color.White;
        button.ForeColor = Dark;
        if (primary) button.Font = new Font(button.Font, FontStyle.Bold);
        return button;
    }

    // Iconos en pixel art con varios tonos de gris: sprites de 16×16 ampliados en bloques exactos, sin suavizar. El tamaño del bitmap es 16 × scale
    // (con scale 3 son 48 px y se muestran sin reescalar). O contorno, D oscuro, R medio, L claro y W reflejo.
    private static readonly string[] TrashSprite =
    [
        "................",
        ".....OOOOOO.....",
        ".....OWLLLO.....",
        "..OOOOOOOOOOOO..",
        "..OWLLLLLLLLRO..",
        "..OOOOOOOOOOOO..",
        "...OOOOOOOOOO...",
        "...OLRDRDRDRO...",
        "...OLRDRDRDRO...",
        "...OLRDRDRDRO...",
        "...OLRDRDRDRO...",
        "....OLRDRDRO....",
        "....OLRDRDRO....",
        "....OOOOOOOO....",
        "................",
        "................"
    ];

    // Cartucho en gris con un «+» verde superpuesto abajo a la derecha (G relleno, H centro claro, P contorno).
    private static readonly string[] CartridgeBase =
    [
        "................",
        "OOOOOOOOOOOO....",
        "OWLLLLLLLLRO....",
        "OLOOOOOOOORO....",
        "OLORRRRRRDRO....",
        "OLORWWWWRDRO....",
        "OLORRRRRRDRO....",
        "OLOOOOOOOORO....",
        "OLLLLLLLLLRO....",
        "OOOOOOOOOOOO....",
        "..ODODODOD......",
        "..OOOOOOOO......",
        "................",
        "................",
        "................",
        "................"
    ];

    private static readonly string[] PlusSprite =
    [
        "..PPP..",
        "..PGP..",
        "PPPGPPP",
        "PGGHGGP",
        "PPPGPPP",
        "..PGP..",
        "..PPP.."
    ];

    // Dibuja «top» sobre «baseSprite» desde la columna x0 y la fila y0 (el punto no cambia nada).
    private static string[] Overlay(string[] baseSprite, string[] top, int x0, int y0)
    {
        var rows = baseSprite.Select(r => r.ToCharArray()).ToArray();
        for (int y = 0; y < top.Length; y++)
            for (int x = 0; x < top[y].Length; x++)
                if (top[y][x] != '.') rows[y0 + y][x0 + x] = top[y][x];
        return rows.Select(r => new string(r)).ToArray();
    }

    private static readonly string[] CartridgeSprite = Overlay(CartridgeBase, PlusSprite, 9, 8);

    // Papiro medio enrollado (el rollo arriba y la hoja con líneas de texto debajo) con una pluma que escribe en él, en diagonal desde arriba a la derecha.
    private static readonly string[] ScrollBase =
    [
        "................",
        ".OOOOOOOOOOOO...",
        "OWLLLLLLLLLLRO..",
        "OLRRRRRRRRRRDO..",
        ".ODDDDDDDDDDO...",
        "..OLLLLLLLLLO...",
        "..OLDDDDDDDLO...",
        "..OLLLLLLLLLO...",
        "..OLDDDDDLLLO...",
        "..OLLLLLLLLLO...",
        "..OLDDDDDDLLO...",
        "..OLLLLLLLLLO...",
        "..OOOOOOOOOOO...",
        "................",
        "................",
        "................"
    ];

    private static string[] BuildQuill()
    {
        var rows = Enumerable.Range(0, 16).Select(_ => new string('.', 16).ToCharArray()).ToArray();
        for (int i = 0; i <= 8; i++)
        {
            int x = 15 - i, y = i;
            rows[y][x] = 'D';                                 // cálamo
            if (y >= 1) rows[y - 1][x] = 'W';                 // barbas, a un lado y al otro
            if (x + 1 < 16 && y >= 1) rows[y][x + 1] = 'L';
        }
        rows[9][6] = 'O'; rows[10][5] = 'O';                  // punta, apoyada en la hoja
        return rows.Select(r => new string(r)).ToArray();
    }

    private static readonly string[] ScrollSprite = Overlay(ScrollBase, BuildQuill(), 0, 0);

    private static Bitmap PixelIcon(string[] sprite, int scale, Dictionary<char, Color>? palette = null)
    {
        var colors = palette ?? new Dictionary<char, Color>
        {
            ['O'] = Color.FromArgb(78, 78, 86), ['D'] = Color.FromArgb(122, 122, 130), ['R'] = Color.FromArgb(166, 166, 174), ['L'] = Color.FromArgb(210, 210, 218), ['W'] = Color.FromArgb(248, 248, 252),
            ['P'] = Color.FromArgb(26, 104, 46), ['G'] = Color.FromArgb(84, 208, 112), ['H'] = Color.FromArgb(190, 255, 205)
        };
        var bitmap = new Bitmap(16 * scale, 16 * scale);
        using var g = Graphics.FromImage(bitmap);
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        for (int y = 0; y < sprite.Length; y++)
            for (int x = 0; x < sprite[y].Length; x++)
                if (colors.TryGetValue(sprite[y][x], out var color))
                {
                    using var brush = new SolidBrush(color);
                    g.FillRectangle(brush, x * scale, y * scale, scale, scale);
                }
        return bitmap;
    }

    public static Bitmap TrashIcon(int scale = 3) => PixelIcon(TrashSprite, scale);
    public static Bitmap CartridgeIcon(int scale = 3) => PixelIcon(CartridgeSprite, scale);

    // Cuadrícula de 3 × 3 piezas grises con un hueco (la pieza que falta, solo con su contorno punteado), para los huecos de la colección.
    private static string[] GapsSprite()
    {
        var rows = Enumerable.Range(0, 16).Select(_ => new string('.', 16).ToCharArray()).ToArray();
        for (int ty = 0; ty < 3; ty++)
            for (int tx = 0; tx < 3; tx++)
            {
                int x0 = tx * 5, y0 = ty * 5;
                bool missing = tx == 2 && ty == 1;
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        bool edge = x == 0 || y == 0 || x == 3 || y == 3;
                        if (missing) { if (edge && (x + y) % 2 == 0) rows[y0 + y][x0 + x] = 'D'; }
                        else rows[y0 + y][x0 + x] = edge ? (x == 0 || y == 0 ? 'W' : 'R') : 'L';
                    }
            }
        return rows.Select(r => new string(r)).ToArray();
    }

    public static Bitmap GapsIcon(int scale = 3) => PixelIcon(GapsSprite(), scale);

    // Embudo gris (filtro) con una marca verde de «visto» abajo a la derecha, para el curado.
    private static readonly string[] FunnelSprite =
    [
        "................",
        "..OOOOOOOOOOOO..",
        "..OWLLLLLLLLRO..",
        "..OLLLLLLLLLRO..",
        "...OLLLLLLLRO...",
        "....OLLLLLRO....",
        ".....OLLLRO.....",
        "......OLRO......",
        "......ODRO......",
        "......ODRO......",
        "......ODRO......",
        "......OOOO......",
        "................",
        "................",
        "................",
        "................"
    ];

    private static string[] CheckMark()
    {
        var rows = Enumerable.Range(0, 16).Select(_ => new string('.', 16).ToCharArray()).ToArray();
        foreach (var (x, y) in new[] { (9, 12), (10, 13), (11, 12), (12, 11), (13, 10), (14, 9) }) { rows[y][x] = 'G'; rows[y + 1][x] = 'P'; }
        return rows.Select(r => new string(r)).ToArray();
    }

    public static Bitmap FunnelIcon(int scale = 3) => PixelIcon(Overlay(FunnelSprite, CheckMark(), 0, 0), scale);

    // Pala cavando un agujero de donde sale un cofre del tesoro, a todo color: B contorno, C madera, c madera oscura, Y oro, y oro oscuro, R cerradura,
    // G hierba, b tierra, d tierra oscura, H mango, h mango oscuro, S acero, s acero oscuro, W destello.
    private static readonly string[] TreasureBase =
    [
        "..........W...W.",
        ".........WYW.WYW",
        "..........W...W.",
        "......BBBBBBBBB.",
        "......BCCCCCCCB.",
        "......BYYYYYYYB.",
        "......BBBBBBBBB.",
        "GGGGGGBCCCRCCCBG",
        "bbbbbbBCCCCCCCBd",
        "bbdbbbBCCCCCCCBd",
        "bbbbbbBBBBBBBBBd",
        "bbdbbddddddddddd",
        "bbbdbbbbdbbbbdbb",
        "dbbbbdbbbbbdbbbb",
        "bbbbbbbbbbbbbbbb",
        "bbbbbbbbbbbbbbbb"
    ];

    private static readonly string[] ShovelOverlay =
    [
        "HH..............",
        ".HH.............",
        "..H.............",
        "...H............",
        "....h...........",
        "...SSS..........",
        "...SWS..........",
        "...sSs..........",
        "....s...........",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................"
    ];

    private static readonly Dictionary<char, Color> TreasurePalette = new()
    {
        ['B'] = Color.FromArgb(72, 42, 20), ['C'] = Color.FromArgb(184, 104, 42), ['c'] = Color.FromArgb(132, 72, 26), ['Y'] = Color.FromArgb(255, 210, 54), ['y'] = Color.FromArgb(204, 142, 22),
        ['R'] = Color.FromArgb(222, 52, 60), ['G'] = Color.FromArgb(84, 178, 62), ['b'] = Color.FromArgb(150, 98, 54), ['d'] = Color.FromArgb(100, 62, 34),
        ['H'] = Color.FromArgb(214, 164, 92), ['h'] = Color.FromArgb(160, 110, 56), ['S'] = Color.FromArgb(206, 214, 224), ['s'] = Color.FromArgb(128, 140, 156), ['W'] = Color.FromArgb(255, 252, 214)
    };

    public static Bitmap TreasureIcon(int scale = 3) => PixelIcon(Overlay(TreasureBase, ShovelOverlay, 0, 0), scale, TreasurePalette);

    // ---- Pala cavando (descarga masiva): pala corta en diagonal, sin suelo. La empuñadura describe una U (sale casi de la esquina superior izquierda, baja, cruza por
    // abajo y sube hacia el centro) con la pala casi con la misma inclinación, y en cada ciclo saca una imagen esquemática distinta (foto, vídeo, documento).
    private static readonly Dictionary<char, Color> ShovelPalette = new()
    {
        ['s'] = Color.FromArgb(110, 122, 140), ['S'] = Color.FromArgb(206, 214, 224), ['W'] = Color.FromArgb(255, 252, 214),
        ['H'] = Color.FromArgb(214, 164, 92), ['h'] = Color.FromArgb(160, 110, 56),
        ['F'] = Color.FromArgb(240, 240, 245), ['a'] = Color.FromArgb(80, 200, 246), ['Y'] = Color.FromArgb(255, 210, 54), ['G'] = Color.FromArgb(84, 178, 62),
        ['k'] = Color.FromArgb(40, 44, 60), ['R'] = Color.FromArgb(232, 72, 72), ['o'] = Color.FromArgb(130, 136, 150)
    };

    private const double ShovelShaft = 7.0, ShovelBlade = 5.5;

    // Media anchura de la hoja a la distancia u del cuello: hombros, lados rectos y estrechamiento hasta la punta (-1 fuera de la hoja).
    private static double BladeHalfWidth(double u)
    {
        if (u < 0 || u > ShovelBlade) return -1;
        if (u < 0.6) return 1.8 + u;
        if (u < 2.0) return 2.4;
        return 2.4 * Math.Pow(1 - Math.Pow((u - 2.0) / (ShovelBlade - 2.0), 1.5), 0.8);
    }

    // Pala girada thetaDeg grados (0 = horizontal hacia la derecha, 90 = hacia abajo) con la empuñadura en (gx, gy).
    private static char[][] ShovelRaster(double thetaDeg, double gx, double gy)
    {
        double t = thetaDeg * Math.PI / 180, ax = Math.Cos(t), ay = Math.Sin(t), nx = Math.Sin(t), ny = -Math.Cos(t);
        var rows = Enumerable.Range(0, 16).Select(_ => new string('.', 16).ToCharArray()).ToArray();
        (double U, double V) Local(int x, int y) { double rx = x + 0.5 - gx, ry = y + 0.5 - gy; return (rx * ax + ry * ay, rx * nx + ry * ny); }
        bool InBlade(int x, int y) { var (u, v) = Local(x, y); return Math.Abs(v) <= BladeHalfWidth(u - ShovelShaft); }
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                var (u, v) = Local(x, y);
                if (InBlade(x, y))
                {
                    bool edge = !(InBlade(x - 1, y) && InBlade(x + 1, y) && InBlade(x, y - 1) && InBlade(x, y + 1));
                    rows[y][x] = edge ? 's' : v < -0.4 ? 'W' : 'S';
                }
                else if (u >= 0 && u <= ShovelShaft && Math.Abs(v) <= 0.75) rows[y][x] = v < 0.05 ? 'H' : 'h';
                else if (u >= 0 && u <= 1.3 && Math.Abs(v) <= 2.2) rows[y][x] = 'h';   // empuñadura en T
            }
        return rows;
    }

    private static readonly string[][] MediaImages =
    [
        ["FFFFFF", "FaaYaF", "FaaaaF", "FGGGGF", "FFFFFF"],   // foto: cielo, sol y colina
        ["FFFFFF", "FkRkkF", "FkRRkF", "FkRkkF", "FFFFFF"],   // vídeo: pantalla con el símbolo de reproducir
        ["FFFFFF", "FooooF", "FFFFFF", "FoooFF", "FFFFFF"]    // documento: página con líneas de texto
    ];

    public sealed class ShovelAnimation
    {
        public required Bitmap Idle { get; init; }
        public required Bitmap[] Frames { get; init; }
    }

    public static ShovelAnimation ShovelFrames(int scale = 3)
    {
        // (ángulo, empuñadura x, y, imagen: 0 ninguna, 1-3 asomando y subiendo, 4 arriba a la derecha)
        (double Theta, double X, double Y, int Stage)[] path =
        [
            (55, 1.0, 1.0, 0), (55, 1.2, 2.4, 0), (53, 1.8, 4.0, 0), (50, 3.0, 5.2, 0),
            (46, 4.4, 5.6, 1), (42, 5.6, 5.0, 2), (38, 6.4, 4.0, 3), (44, 3.8, 2.2, 4), (52, 1.6, 1.2, 4)
        ];
        (int X, int Y)[] imageAt = [(0, 0), (10, 9), (10, 6), (10, 3), (10, 0)];
        var frames = new List<Bitmap>();
        foreach (var image in MediaImages)
            foreach (var (theta, gx, gy, stage) in path)
            {
                var rows = Enumerable.Range(0, 16).Select(_ => new string('.', 16).ToCharArray()).ToArray();
                if (stage > 0)
                    for (int y = 0; y < image.Length; y++)
                        for (int x = 0; x < image[y].Length; x++)
                        {
                            int px = imageAt[stage].X + x, py = imageAt[stage].Y + y;
                            if (px >= 0 && px < 16 && py >= 0 && py < 16) rows[py][px] = image[y][x];
                        }
                // La pala va por encima de la imagen: parece que la saca de la hoja y no que la empuja.
                var shovel = ShovelRaster(theta, gx, gy);
                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) if (shovel[y][x] != '.') rows[y][x] = shovel[y][x];
                frames.Add(PixelIcon(rows.Select(r => new string(r)).ToArray(), scale, ShovelPalette));
            }
        return new ShovelAnimation { Idle = frames[0], Frames = frames.ToArray() };
    }
    public static Bitmap ScrollIcon(int scale = 3) => PixelIcon(ScrollSprite, scale);

    // Animación del papiro: la pluma va escribiendo, línea a línea, y al terminar la hoja se vacía y vuelve a empezar.
    public static Bitmap[] ScrollWritingFrames(int scale = 3)
    {
        var frames = new List<Bitmap>();
        int[] lineRows = [6, 8, 10];
        const int firstColumn = 4, lineLength = 7;
        string[] blank = ScrollBase.Select((r, y) => lineRows.Contains(y) ? r.Replace('D', 'L') : r).ToArray();
        for (int line = 0; line < lineRows.Length; line++)
            for (int length = 0; length <= lineLength; length++)
            {
                var rows = blank.Select(r => r.ToCharArray()).ToArray();
                for (int done = 0; done <= line; done++)
                    for (int i = 0; i < (done < line ? lineLength : length); i++) rows[lineRows[done]][firstColumn + i] = 'D';
                var paper = rows.Select(r => new string(r)).ToArray();
                // La pluma apoya la punta al final de lo escrito, con un pequeño vaivén.
                int tipX = firstColumn + length + (length % 2), tipY = lineRows[line] + 1;
                frames.Add(PixelIcon(Overlay(paper, QuillAt(tipX, tipY), 0, 0), scale));
            }
        // Hoja ya escrita (una pausa) antes de volver a empezar.
        string[] written = blank.Select(r => r).ToArray();
        var full = written.Select(r => r.ToCharArray()).ToArray();
        foreach (int row in lineRows) for (int i = 0; i < lineLength; i++) full[row][firstColumn + i] = 'D';
        var done2 = full.Select(r => new string(r)).ToArray();
        for (int i = 0; i < 3; i++) frames.Add(PixelIcon(Overlay(done2, QuillAt(firstColumn + lineLength + 2, lineRows[^1] + 1), 0, 0), scale));
        return frames.ToArray();
    }

    // Pluma en diagonal (hacia arriba a la derecha) con la punta en (tipX, tipY).
    private static string[] QuillAt(int tipX, int tipY)
    {
        var rows = Enumerable.Range(0, 16).Select(_ => new string('.', 16).ToCharArray()).ToArray();
        void Put(int x, int y, char c) { if (x >= 0 && x < 16 && y >= 0 && y < 16) rows[y][x] = c; }
        for (int k = 0; k <= 8; k++)
        {
            int x = tipX + 1 + k, y = tipY - 1 - k;
            Put(x, y, 'D');
            Put(x, y - 1, 'W');
            Put(x + 1, y, 'L');
        }
        Put(tipX, tipY, 'O'); Put(tipX - 1, tipY + 1, 'O');
        return rows.Select(r => new string(r)).ToArray();
    }

    // Anima un icono recorriendo sus fotogramas; el temporizador se detiene solo cuando el control se destruye.
    public static void AnimateIcon(PictureBox box, Bitmap[] frames, int intervalMs = 130)
    {
        int index = 0;
        box.Image = frames[0];
        var timer = new System.Windows.Forms.Timer { Interval = intervalMs };
        timer.Tick += (_, _) => { index = (index + 1) % frames.Length; box.Image = frames[index]; };
        box.Disposed += (_, _) => timer.Dispose();
        timer.Start();
    }

    // Aspecto de las tablas de los formularios nuevos: cabecera oscura, filas con fondos alternos, sin bordes y con línea fina entre filas.
    // Con selectionVisible la fila seleccionada se tiñe de dorado claro; sin ella la selección no se nota.
    public static void StyleGrid(DataGridView g, int rowHeight = 28, int headerHeight = 30, bool selectionVisible = true)
    {
        var alternate = Color.FromArgb(243, 245, 249);
        g.BackgroundColor = Color.White; g.BorderStyle = BorderStyle.None; g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; g.GridColor = Color.FromArgb(232, 234, 239);
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; g.ColumnHeadersHeight = headerHeight;
        g.RowTemplate.Height = rowHeight;
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(52, 56, 68); g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = g.ColumnHeadersDefaultCellStyle.BackColor; g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.White;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        g.DefaultCellStyle.BackColor = Color.White; g.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        g.AlternatingRowsDefaultCellStyle.BackColor = alternate;
        var gold = Color.FromArgb(255, 236, 179);
        g.DefaultCellStyle.SelectionBackColor = selectionVisible ? gold : Color.White; g.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        g.AlternatingRowsDefaultCellStyle.SelectionBackColor = selectionVisible ? gold : alternate; g.AlternatingRowsDefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
    }

    // Botón plano de los formularios nuevos (el principal en dorado), sin cambiar su tamaño mínimo salvo la altura.
    public static void StyleButton(Button b, bool primary, Color? accent = null)
    {
        var main = accent ?? Gold;
        b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false; b.Cursor = Cursors.Hand;
        b.MinimumSize = new Size(b.MinimumSize.Width, Math.Max(b.MinimumSize.Height, 32));
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(Math.Min(255, main.R + 18), Math.Min(255, main.G + 18), Math.Min(255, main.B + 18)) : Color.FromArgb(238, 240, 245);
        b.ForeColor = Dark;
        if (primary) b.Font = new Font(b.Font, FontStyle.Bold);
        void Paint() { b.BackColor = !b.Enabled ? Color.FromArgb(233, 235, 239) : primary ? main : Color.White; b.FlatAppearance.BorderColor = !b.Enabled ? Border : primary ? main : Border; }
        b.EnabledChanged += (_, _) => Paint();
        Paint();
    }

    // ---- Clonado: un diskette original (fijo) y su copia. Un diskette apagado viaja cinco veces de izquierda a derecha y la copia se va formando con píxeles
    // dispersos (tramado ordenado); al quinto viaje queda completa y los dos centellean idénticos durante 3 segundos. Lienzo de 24 × 16 (dos diskettes de 11 × 11).
    private static readonly string[] FloppySprite =
    [
        "OOOOOOOOO..",
        "OBSSSSkBBO.",
        "OBSSSSkBBBO",
        "OBSSSSSBBBO",
        "OBBBBBBBBBO",
        "OBwwwwwwwBO",
        "OBwLLLLLwBO",
        "OBwwwwwwwBO",
        "OBwLLLLLwBO",
        "OBwwwwwwwBO",
        "OOOOOOOOOOO"
    ];

    private static readonly int[][] Bayer4 = [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]];
    // Chispas en posiciones relativas al diskette (columna, fila, tamaño: 0 un punto, 1 cruz de cinco píxeles); salen en el mismo sitio de los dos diskettes.
    private static readonly (int X, int Y, int Size)[][] FloppySparkles =
    [
        [(3, 1, 1), (8, 7, 0)], [(2, 6, 0), (8, 2, 1)], [(6, 4, 1), (1, 8, 0)], [(9, 5, 1), (3, 3, 0)], [(5, 8, 1), (1, 3, 0)], [(8, 1, 0), (4, 6, 1)]
    ];

    private static Dictionary<char, Color> FloppyPalette()
    {
        var normal = new Dictionary<char, Color>
        {
            ['O'] = Color.FromArgb(16, 28, 72), ['B'] = Color.FromArgb(40, 70, 160), ['S'] = Color.FromArgb(205, 210, 220), ['k'] = Color.FromArgb(60, 60, 80),
            ['w'] = Color.FromArgb(255, 255, 255), ['L'] = Color.FromArgb(150, 160, 190)
        };
        var palette = new Dictionary<char, Color>(normal)
        {
            ['*'] = Color.White, ['+'] = Color.FromArgb(224, 250, 255), ['-'] = Color.FromArgb(110, 215, 250)
        };
        // El diskette que viaja se ve algo apagado (75 % de opacidad sobre el fondo oscuro de la cabecera): cada color tiene su letra, en minúscula con prefijo.
        var bg = Color.FromArgb(28, 30, 38);
        char ghost = '1';
        foreach (var (letter, color) in normal.ToArray())
        {
            palette[ghost] = Color.FromArgb((int)(bg.R + (color.R - bg.R) * 0.75), (int)(bg.G + (color.G - bg.G) * 0.75), (int)(bg.B + (color.B - bg.B) * 0.75));
            GhostOf[letter] = ghost++;
        }
        return palette;
    }

    private static readonly Dictionary<char, char> GhostOf = [];
    private static readonly Dictionary<char, Color> FloppyColors = FloppyPalette();

    private static Bitmap PixelBitmap(char[][] rows, int scale)
    {
        var bitmap = new Bitmap(rows[0].Length * scale, rows.Length * scale);
        using var g = Graphics.FromImage(bitmap);
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < rows[y].Length; x++)
                if (FloppyColors.TryGetValue(rows[y][x], out var color))
                {
                    using var brush = new SolidBrush(color);
                    g.FillRectangle(brush, x * scale, y * scale, scale, scale);
                }
        return bitmap;
    }

    public sealed class FloppyAnimation
    {
        public required Bitmap Idle { get; init; }
        public required Bitmap[] Frames { get; init; }
        public required int[] Durations { get; init; }
    }

    public static FloppyAnimation FloppyFrames(int scale = 2)
    {
        const int width = 24, height = 16, top = 3, left = 0, right = 13;
        char[][] Canvas() => Enumerable.Range(0, height).Select(_ => new string('.', width).ToCharArray()).ToArray();
        void Draw(char[][] cv, int x0, double coverage, bool ghost = false)
        {
            for (int j = 0; j < FloppySprite.Length; j++)
                for (int i = 0; i < FloppySprite[j].Length; i++)
                {
                    char c = FloppySprite[j][i];
                    if (c == '.' || Bayer4[j % 4][i % 4] >= coverage * 16) continue;   // píxeles de la copia que todavía no han llegado
                    cv[top + j][x0 + i] = ghost ? GhostOf[c] : c;
                }
        }
        void Spark(char[][] cv, int cx, int cy, int size)
        {
            void Put(int x, int y, char c) { if (x >= 0 && x < width && y >= 0 && y < height) cv[y][x] = c; }
            Put(cx, cy, '*');
            for (int d = 1; d <= size; d++) { char c = d == 1 ? '+' : '-'; Put(cx + d, cy, c); Put(cx - d, cy, c); Put(cx, cy + d, c); Put(cx, cy - d, c); }
        }
        var frames = new List<Bitmap>(); var durations = new List<int>();
        int[] steps = [0, 3, 6, 10, 13];
        for (int pass = 1; pass <= 5; pass++)
        {
            foreach (int x in steps)
            {
                var cv = Canvas();
                Draw(cv, right, (pass - 1) / 5.0); Draw(cv, left, 1);
                if (x != left) Draw(cv, x, 1, ghost: true);
                frames.Add(PixelBitmap(cv, scale)); durations.Add(110);
            }
            var done = Canvas(); Draw(done, right, pass / 5.0); Draw(done, left, 1);
            frames.Add(PixelBitmap(done, scale)); durations.Add(260);
        }
        foreach (var sparks in FloppySparkles)
        {
            var cv = Canvas(); Draw(cv, right, 1); Draw(cv, left, 1);
            foreach (var (x, y, size) in sparks) { Spark(cv, left + x, top + y, size); Spark(cv, right + x, top + y, size); }
            frames.Add(PixelBitmap(cv, scale)); durations.Add(500);
        }
        var idle = Canvas(); Draw(idle, right, 1); Draw(idle, left, 1);
        return new FloppyAnimation { Idle = PixelBitmap(idle, scale), Frames = frames.ToArray(), Durations = durations.ToArray() };
    }

    // Barra inferior de botones, con una línea fina arriba.
    public static Panel Footer(int height = 52)
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = height, BackColor = FooterBack };
        panel.Paint += (_, e) => { using var pen = new Pen(Border); e.Graphics.DrawLine(pen, 0, 0, panel.Width, 0); };
        return panel;
    }

    // Tarjeta blanca con borde fino.
    public static Panel Card(Rectangle bounds)
    {
        var panel = new Panel { Bounds = bounds, BackColor = Color.White };
        panel.Paint += (_, e) => { using var pen = new Pen(Border); e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1); };
        return panel;
    }

    // Marco fino alrededor de un control (normalmente una tabla), con el mismo borde que el resto de formularios nuevos.
    public static Panel Frame(Control inner, Rectangle? bounds = null, DockStyle dock = DockStyle.None)
    {
        var panel = new Panel { Padding = new Padding(1), BackColor = Border, Dock = dock };
        if (bounds is { } b) panel.Bounds = b;
        inner.Dock = DockStyle.Fill;
        panel.Controls.Add(inner);
        return panel;
    }

    // Convierte un formulario de controles sueltos al formato nuevo: mete todo lo que tiene en un cuerpo claro y añade la cabecera arriba (la ventana crece lo que mide la cabecera).
    public static Panel WrapWithHeader(Form form, RaHeader banner)
    {
        var items = form.Controls.Cast<Control>().ToArray();
        var body = new Panel { BackColor = Body, Size = form.ClientSize };
        form.Controls.Clear();
        body.Controls.AddRange(items);
        form.BackColor = Body;
        form.ClientSize = new Size(form.ClientSize.Width, form.ClientSize.Height + banner.Height);
        body.Dock = DockStyle.Fill;
        form.Controls.Add(body);
        form.Controls.Add(banner);
        return body;
    }

    private static readonly string[] FolderSprite =
    [
        "",
        "",
        " OOOOOO",
        " ORRRRRO",
        " OWWWWWWOOOOOOO",
        " OWLLLLLLLLLLLRO",
        " OWLLLLLLLLLLLRO",
        " OWLLLLLLLLLLLRO",
        " OWLLLLLLLLLLLRO",
        " OWLLLLLLLLLLLRO",
        " OWLLLLLLLLLLLRO",
        " OWLLLLLLLLLLLRO",
        " ORRRRRRRRRRRRRO",
        " OOOOOOOOOOOOOOO",
    ];

    private static readonly string[] ChartSprite =
    [
        "",
        " O",
        " O          PPP",
        " O          GGP",
        " O          GGP",
        " O     PPP  GGP",
        " O     GGP  GGP",
        " O     GGP  GGP",
        " O PPP GGP  GGP",
        " O GGP GGP  GGP",
        " O GGP GGP  GGP",
        " O GGP GGP  GGP",
        " O GGP GGP  GGP",
        " OOOOOOOOOOOOOO",
    ];

    private static readonly string[] MagnifierSprite =
    [
        "",
        "    OOOOO",
        "   ORRRRRO",
        "  ORWWLLLRO",
        "  ORWLLLLRO",
        "  ORLLLLLRO",
        "  ORLLLLLRO",
        "  ORLLLLLRO",
        "   ORRRRRO",
        "    OOOOOD",
        "          DD",
        "           DDD",
        "            DDD",
        "             DDD",
        "              OO",
    ];

    private static readonly string[] DatabaseSprite =
    [
        "",
        "    OOOOOOOO",
        "   ORRRRRRRRO",
        "   OWLLLLLLRO",
        "    ODDDDDDO",
        "   ORRRRRRRRO",
        "   OLLLLLLLRO",
        "    ODDDDDDO",
        "   ORRRRRRRRO",
        "   OLLLLLLLRO",
        "    ODDDDDDO",
        "   OLLLLLLLRO",
        "    OOOOOOOO",
    ];

    // Círculo gris con una «i» (punto verde y trazo oscuro), para «Acerca de».
    private static readonly string[] InfoSprite =
    [
        "....OOOOOOOO....",
        "..OORRRRRRRROO..",
        ".ORRLLLLLLLLRRO.",
        ".ORLLLLGGLLLLRO.",
        "ORRLLLLGGLLLLRRO",
        "ORLLLLLLLLLLLLRO",
        "ORLLLLOOOOLLLLRO",
        "ORLLLLLOOLLLLLRO",
        "ORLLLLLOOLLLLLRO",
        "ORLLLLLOOLLLLLRO",
        "ORLLLLLOOLLLLLRO",
        "ORLLLLOOOOLLLLRO",
        "ORRLLLLLLLLLLRRO",
        ".ORLLLLLLLLLLRO.",
        "..OODDDDDDDDOO..",
        "....OOOOOOOO....",
    ];

    public static Bitmap InfoIcon(int scale = 3) => PixelIcon(InfoSprite, scale);
    private static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private static double HashUnit(int x, int y, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + salt * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return h / (double)uint.MaxValue;
        }
    }

    // Textura de las cabeceras, adaptada al color de cada una: degradado suave, píxeles que se disuelven por la derecha (tonos del propio color y de un acento) y
    // líneas de barrido de monitor antiguo. Sin «accent» se usa una versión clara del color de fondo. «reserveBottom» deja libre esa franja de abajo (la línea dorada).
    public static void PaintHeaderTexture(Graphics g, Rectangle r, Color back, Color? accent = null, int reserveBottom = 0)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        int scale = Math.Max(1, (int)Math.Round(g.DpiX / 96.0));
        int h = r.Height - reserveBottom * scale;
        if (h <= 0) return;
        var acc = accent ?? Mix(back, Color.White, 0.6);
        using (var gradient = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(r.X, r.Y, r.Width, h), Mix(back, Color.White, 0.05), Mix(back, Color.Black, 0.14), 0f))
            g.FillRectangle(gradient, r.X, r.Y, r.Width, h);
        Color[] tones = [Mix(back, Color.White, 0.10), Mix(back, Color.White, 0.20), Mix(back, acc, 0.35), acc, Mix(back, acc, 0.55)];
        int block = 6 * scale;
        for (int gx = 0; gx < r.Width; gx += block)
        {
            double t = Math.Max(0, (gx - r.Width * 0.45) / (r.Width * 0.55));
            if (t <= 0) continue;
            for (int gy = 0; gy < h; gy += block)
            {
                if (HashUnit(gx / block, gy / block, 1) >= Math.Pow(t, 1.4) * 0.85) continue;
                var color = tones[(int)(HashUnit(gx / block, gy / block, 2) * tones.Length) % tones.Length];
                if (HashUnit(gx / block, gy / block, 3) < 0.55) color = Mix(back, color, 0.55);
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, r.X + gx, r.Y + gy, Math.Min(block, r.Width - gx), Math.Min(block, h - gy));
            }
        }
        using var scan = new SolidBrush(Color.FromArgb(56, 0, 0, 0));
        for (int y = 0; y < h; y += 3 * scale) g.FillRectangle(scan, r.X, r.Y + y, r.Width, scale);
    }

    // ---- Papelera de grises a la que caen, de uno en uno, un cartucho de Mega Drive, una hoja escrita, una imagen y un vídeo (para la confirmación de borrado).
    private static readonly string[] DropCartridge = ["..DDDDDD..", ".DNNNNNND.", "DNNNNNNNND", "DNWWWWWWND", "DNWBBTTWND", "DNWWWWWWND", "DNNNNNNNND", ".DDDDDDDD."];
    private static readonly string[] DropPaper = ["OOOOO.", "OWWWOO", "OWDDWO", "OWWWWO", "OWDDDO", "OOOOOO"];
    private static readonly string[] DropImage = ["OOOOOO", "OBBYBO", "OBBBBO", "OBGBBO", "OGGGGO", "OOOOOO"];
    private static readonly string[] DropVideo = ["OOOOOO", "OKWKKO", "OKWWKO", "OKWKKO", "OKKKKO", "OOOOOO"];

    // Fotogramas de ~55 ms: la tapa se abre, caen los cuatro elementos (cada uno se oculta tras el borde y la papelera da un golpe), se cierra y hay una pausa.
    public static Bitmap[] TrashDropFrames(int scale = 4)
    {
        var palette = new Dictionary<char, Color>
        {
            ['O'] = Color.FromArgb(78, 78, 86), ['D'] = Color.FromArgb(122, 122, 130), ['R'] = Color.FromArgb(166, 166, 174), ['L'] = Color.FromArgb(210, 210, 218),
            ['W'] = Color.FromArgb(248, 248, 252), ['K'] = Color.FromArgb(40, 44, 62), ['N'] = Color.FromArgb(36, 38, 48), ['B'] = Color.FromArgb(110, 170, 230),
            ['G'] = Color.FromArgb(84, 190, 100), ['Y'] = Color.FromArgb(255, 210, 80), ['T'] = Color.FromArgb(226, 60, 60)
        };
        string[][] items = [DropCartridge, DropPaper, DropImage, DropVideo];
        const int shift = 2; // la papelera baja dos filas para dejar sitio arriba

        void Draw(Graphics g, string[] sprite, int ox, int oy, Func<int, bool>? rows = null)
        {
            for (int y = 0; y < sprite.Length; y++)
            {
                if (rows is not null && !rows(y)) continue;
                int yy = y + oy;
                if (yy < 0 || yy >= 16) continue;
                for (int x = 0; x < sprite[y].Length; x++)
                {
                    int xx = x + ox;
                    if (xx < 0 || xx >= 16 || !palette.TryGetValue(sprite[y][x], out var color)) continue;
                    using var brush = new SolidBrush(color);
                    g.FillRectangle(brush, xx * scale, yy * scale, scale, scale);
                }
            }
        }

        Bitmap Frame(bool lidOpen, int item = -1, int itemY = 0, int bump = 0)
        {
            var bitmap = new Bitmap(16 * scale, 16 * scale);
            using var g = Graphics.FromImage(bitmap);
            Draw(g, TrashSprite, lidOpen ? -4 : 0, shift + (lidOpen ? -3 : 0), r => r is >= 1 and <= 5); // asa y tapa, levantadas y a un lado si está abierta
            if (item >= 0) Draw(g, items[item], 8 - items[item][0].Length / 2, itemY);
            Draw(g, TrashSprite, 0, shift + bump, r => r >= 6);                                           // el cuerpo tapa lo que cae
            return bitmap;
        }

        var frames = new List<Bitmap>();
        void Add(Bitmap frame, int ticks = 1) { for (int i = 0; i < ticks; i++) frames.Add(i == 0 ? frame : new Bitmap(frame)); }
        for (int i = 0; i < 5; i++) Add(Frame(false), 2);
        Add(Frame(true), 4);
        for (int k = 0; k < items.Length; k++)
        {
            foreach (int y in new[] { -8, -5, -2, 1, 3, 5, 8 }) Add(Frame(true, k, y));
            Add(Frame(true, bump: 1));
            Add(Frame(true));
        }
        Add(Frame(false), 2);
        Add(Frame(false, bump: 1), 2);
        for (int i = 0; i < 8; i++) Add(Frame(false), 2);
        return frames.ToArray();
    }

    // Chip de la aplicación (el icono de la barra de título) en 16 × 16, con las patillas a igual distancia (filas 3, 6, 9 y 12).
    private static readonly string[] ChipSprite =
    [
        "................",
        "................",
        "...KKKKKKKKKK...",
        ".EEKBBBBBBBBKEE.",
        "...KBSSSSSSBK...",
        "...KBYyBBBBBK...",
        ".EEKBBBBBBBBKEE.",
        "...KBBBBBBBBK...",
        "...KBBBBBBBBK...",
        ".EEKBBBBBBBBKEE.",
        "...KBBBBBBBBK...",
        "...KBBBBBBBBK...",
        ".EEKBBBBBBBBKEE.",
        "...KKKKKKKKKK...",
        "................",
        "................",
    ];

    // Chip con un «+» verde abajo a la derecha que late como un corazón (dos latidos seguidos y una pausa). Un fotograma cada ~75 ms.
    public static Bitmap[] ChipPlusFrames(int scale = 4)
    {
        var palette = new Dictionary<char, Color>
        {
            ['K'] = Color.FromArgb(9, 17, 27), ['B'] = Color.FromArgb(43, 61, 77), ['S'] = Color.FromArgb(74, 96, 112), ['Y'] = Color.FromArgb(255, 207, 89),
            ['y'] = Color.FromArgb(189, 139, 48), ['E'] = Color.FromArgb(224, 234, 241)
        };
        using var chip = PixelIcon(ChipSprite, scale, palette);
        using var plus = PixelIcon(PlusSprite, scale); // el «+» queda en la esquina superior izquierda del lienzo
        double[] beat = [1.0, 1.0, 1.0, 1.25, 1.5, 1.2, 1.0, 1.3, 1.6, 1.3, 1.05, 1.0, 1.0, 1.0, 1.0, 1.0];
        float cx = (9 + 3.5f) * scale, cy = (9 + 3.5f) * scale;
        var frames = new Bitmap[beat.Length];
        for (int i = 0; i < beat.Length; i++)
        {
            var bitmap = new Bitmap(chip);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                float side = (float)(7 * scale * beat[i]);
                g.DrawImage(plus, new RectangleF(cx - side / 2, cy - side / 2, side, side), new RectangleF(0, 0, 7 * scale, 7 * scale), GraphicsUnit.Pixel);
            }
            frames[i] = bitmap;
        }
        return frames;
    }

    private static readonly string[] CrossSprite =
    [
        "", "", "", "", "",
        "    xx    xx",
        "     xx  xx",
        "      xxxx",
        "       xx",
        "      xxxx",
        "     xx  xx",
        "    xx    xx",
    ];

    private static readonly Color[] FlameColors = [Color.FromArgb(255, 236, 120), Color.FromArgb(255, 190, 50), Color.FromArgb(255, 130, 30), Color.FromArgb(226, 70, 20), Color.FromArgb(170, 40, 16)];

    // Carpeta de colección con una X roja que se quema de arriba abajo según el avance del borrado: progress < 0 = intacta, 0..1 = quemándose (la llama baja con el
    // porcentaje y las llamas cambian con «tick») y 1 = solo queda un montón de ceniza en la base.
    public static Bitmap BurningFolderIcon(double progress, int tick, int scale = 4)
    {
        var palette = new Dictionary<char, Color>
        {
            ['O'] = Color.FromArgb(78, 78, 86), ['D'] = Color.FromArgb(122, 122, 130), ['R'] = Color.FromArgb(166, 166, 174), ['L'] = Color.FromArgb(210, 210, 218), ['W'] = Color.FromArgb(248, 248, 252)
        };
        var red = Color.FromArgb(226, 44, 44); var redDark = Color.FromArgb(120, 14, 14);
        var grid = new Color?[16, 16];
        for (int y = 0; y < FolderSprite.Length; y++)
            for (int x = 0; x < FolderSprite[y].Length; x++)
                if (palette.TryGetValue(FolderSprite[y][x], out var c)) grid[y, x] = c;
        var cross = new HashSet<(int X, int Y)>();
        for (int y = 0; y < CrossSprite.Length; y++)
            for (int x = 0; x < CrossSprite[y].Length; x++)
                if (CrossSprite[y][x] == 'x') cross.Add((x, y));
        foreach (var (x, y) in cross) grid[y, x] = red;
        foreach (var (x, y) in cross)
            foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (1, 1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 16 && ny < 16 && !cross.Contains((nx, ny)) && grid[ny, nx] is not null) grid[ny, nx] = redDark;
            }

        var cells = new Dictionary<(int X, int Y), Color>();
        double front = progress < 0 ? -10 : progress >= 1 ? 40 : 1.0 + progress * 13.5; // fila donde está la llama
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                if (grid[y, x] is not { } color || progress >= 1) continue;
                var rnd = new Random(tick * 7919 + x * 131 + y * 17);
                double d = y - front; // < 0: ya quemado (encima de la llama); > 0: sin quemar
                if (d >= 2.5) cells[(x, y)] = color;
                else if (d >= 0.4)
                    cells[(x, y)] = rnd.NextDouble() < 0.78 ? FlameColors[rnd.Next(4)] : Color.FromArgb(color.R * 45 / 100, color.G * 45 / 100, color.B * 45 / 100);
                else if (d >= -1.6)
                {
                    double r = rnd.NextDouble();
                    if (r < 0.4) cells[(x, y)] = rnd.Next(2) == 0 ? Color.FromArgb(64, 60, 60) : Color.FromArgb(88, 84, 86);
                    else if (r < 0.55) cells[(x, y)] = FlameColors[2 + rnd.Next(3)];
                }
            }
        // Lenguas de fuego hacia arriba desde la llama.
        if (progress is >= 0 and < 1)
            for (int x = 1; x < 15; x++)
                for (int y = 0; y < 16; y++)
                    if (cells.TryGetValue((x, y), out var cc) && Array.IndexOf(FlameColors, cc) >= 0)
                    {
                        var rnd = new Random(tick * 104729 + x * 977);
                        for (int k = 1; k < 3; k++)
                            if (y - k >= 0 && !cells.ContainsKey((x, y - k)) && rnd.NextDouble() < 0.4 - 0.15 * k)
                                cells[(x, y - k)] = FlameColors[Math.Min(4, 1 + k + rnd.Next(2))];
                        break;
                    }
        // Montón de ceniza en la base, que crece con el avance (y con alguna brasa que late).
        double h = Math.Clamp(progress, 0, 1) * 3.4;
        for (int x = 2; x < 14; x++)
        {
            int height = (int)(h - Math.Abs(x - 7.5) * 0.45 + 0.5);
            for (int k = 0; k < Math.Clamp(height, 0, 3); k++)
            {
                int y = 15 - k;
                if (cells.ContainsKey((x, y))) continue;
                var rr = new Random(x * 31 + y);
                cells[(x, y)] = rr.NextDouble() < 0.9
                    ? new[] { Color.FromArgb(58, 54, 56), Color.FromArgb(84, 80, 82), Color.FromArgb(110, 106, 108) }[rr.Next(3)]
                    : (tick / 3) % 2 == 0 ? Color.FromArgb(150, 70, 30) : Color.FromArgb(110, 50, 24);
            }
        }
        var bitmap = new Bitmap(16 * scale, 16 * scale);
        using var g = Graphics.FromImage(bitmap);
        foreach (var ((x, y), c) in cells)
        {
            using var brush = new SolidBrush(c);
            g.FillRectangle(brush, x * scale, y * scale, scale, scale);
        }
        return bitmap;
    }

    public static Bitmap FolderIcon(int scale = 3) => PixelIcon(FolderSprite, scale);
    public static Bitmap ChartIcon(int scale = 3) => PixelIcon(ChartSprite, scale);
    public static Bitmap MagnifierIcon(int scale = 3) => PixelIcon(MagnifierSprite, scale);
    public static Bitmap DatabaseIcon(int scale = 3) => PixelIcon(DatabaseSprite, scale);
}

// Banda oscura de la parte superior: icono (por defecto el logotipo de RetroAchievements), título y subtítulo, con una línea dorada abajo.
internal sealed class RaHeader : Panel
{
    private readonly int textLeft;
    public Label Title { get; }
    public Label Subtitle { get; }
    public PictureBox Icon { get; }
    // Espacio libre a la derecha para controles propios del formulario (por ejemplo, el avatar).
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int RightReserve { get; set; } = 16;

    public RaHeader(string title, string subtitle, int height = 68, int iconSize = 44, bool logo = true)
    {
        Dock = DockStyle.Top; Height = height; BackColor = RaUi.Dark; DoubleBuffered = true;
        textLeft = 16 + iconSize + 12;
        Icon = new PictureBox
        {
            Location = new Point(16, (height - iconSize) / 2), Size = new Size(iconSize, iconSize), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent,
            Image = logo ? PixelSystemIcons.Get("★ RetroAchievements", 64) : null
        };
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        Title = new ShadowLabel
        {
            Location = new Point(textLeft, 10), Height = 28, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = title,
            Font = new Font(baseFont.FontFamily, 13F, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent
        };
        Subtitle = new ShadowLabel
        {
            Location = new Point(textLeft, 40), Height = Math.Max(20, height - 46), AutoEllipsis = true, Text = subtitle,
            ForeColor = Color.FromArgb(208, 214, 228), BackColor = Color.Transparent
        };
        Controls.AddRange([Icon, Title, Subtitle]);
        Resized();
    }

    private void Resized()
    {
        if (Title is null || Subtitle is null) return; // el tamaño cambia en el constructor antes de crear las etiquetas
        int width = Math.Max(60, Width - textLeft - RightReserve);
        Title.Width = width; Subtitle.Width = width;
    }

    protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); Resized(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RaUi.PaintHeaderTexture(e.Graphics, ClientRectangle, BackColor, BackColor == RaUi.Dark ? RaUi.Gold : null, reserveBottom: 3);
        using var brush = new SolidBrush(RaUi.Gold);
        e.Graphics.FillRectangle(brush, 0, Height - 3, Width, 3);
    }
}
