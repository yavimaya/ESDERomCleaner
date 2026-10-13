using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace ESDERomCleaner;

// Iconos de los sistemas. Los de cada consola son los de «xmb/dot-art» de libretro/retroarch-assets (licencia CC BY 4.0, ver «Acerca de» y Assets\SystemIcons\COPYING),
// incrustados en el programa. Los sistemas que no tienen icono usan uno genérico 8 bit de dibujo propio: consola, portátil, recreativa u ordenador.
internal static class PixelSystemIcons
{
    public enum Kind { Console, Handheld, Arcade, Computer }

    // Carpeta de ES-DE → nombre del icono de libretro (archivo sin extensión).
    private static readonly Dictionary<string, string> IconOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3do"] = "The 3DO Company - 3DO", ["adam"] = "Coleco - ColecoVision ADAM", ["amiga"] = "Commodore - Amiga", ["amiga1200"] = "Commodore - Amiga",
        ["amiga600"] = "Commodore - Amiga", ["amigacd32"] = "Commodore - CD32", ["amstradcpc"] = "Amstrad - CPC", ["android"] = "Google - Android (Misc)",
        ["apple2"] = "Apple - II", ["apple2gs"] = "Apple - IIGS", ["arcade"] = "MAME", ["arcadia"] = "Emerson - Arcadia 2001", ["archimedes"] = "Acorn - Archimedes",
        ["arduboy"] = "Arduboy Inc - Arduboy", ["astrocde"] = "Bally - Astrocade", ["atari2600"] = "Atari - 2600", ["atari5200"] = "Atari - 5200",
        ["atari7800"] = "Atari - 7800", ["atari800"] = "Atari - 8-bit Family", ["atarixe"] = "Atari - XEGS", ["atarijaguar"] = "Atari - Jaguar",
        ["atarijaguarcd"] = "Atari - Jaguar Cd", ["atarilynx"] = "Atari - Lynx", ["atarist"] = "Atari - ST", ["bbcmicro"] = "Acorn - BBC Micro", ["c64"] = "Commodore - 64",
        ["cdimono1"] = "Philips - CD-i", ["cdtv"] = "Commodore - CDTV", ["chailove"] = "ChaiLove", ["channelf"] = "Fairchild - Channel F",
        ["colecovision"] = "Coleco - ColecoVision", ["cps"] = "Capcom - CP System I", ["cps1"] = "Capcom - CP System I", ["cps2"] = "Capcom - CP System II",
        ["cps3"] = "Capcom - CP System III", ["crvision"] = "VTech - CreatiVision", ["doom"] = "DOOM", ["dos"] = "DOS", ["dreamcast"] = "Sega - Dreamcast",
        ["famicom"] = "Nintendo - Nintendo Entertainment System", ["nes"] = "Nintendo - Nintendo Entertainment System", ["fba"] = "FB Alpha - Arcade Games",
        ["fbneo"] = "FBNeo - Arcade Games", ["fds"] = "Nintendo - Family Computer Disk System", ["fm7"] = "Fujitsu - FM-7", ["fmtowns"] = "Fujitsu - FM Towns",
        ["gamate"] = "Bit Corporation - Gamate", ["gameandwatch"] = "Nintendo - Game & Watch", ["gamecom"] = "Tiger - Game.com", ["gamegear"] = "Sega - Game Gear",
        ["gb"] = "Nintendo - Game Boy", ["sgb"] = "Nintendo - Game Boy", ["gba"] = "Nintendo - Game Boy Advance", ["gbc"] = "Nintendo - Game Boy Color",
        ["gc"] = "Nintendo - GameCube", ["genesis"] = "Sega - Mega Drive - Genesis", ["megadrive"] = "Sega - Mega Drive - Genesis", ["megadrivejp"] = "Sega - Mega Drive - Genesis",
        ["gmaster"] = "Hartung - Game Master", ["gx4000"] = "Amstrad - GX4000", ["intellivision"] = "Mattel - Intellivision", ["j2me"] = "Mobile - J2ME",
        ["lutro"] = "Lutro", ["macintosh"] = "Apple - Macintosh", ["mame"] = "MAME", ["mame-advmame"] = "MAME", ["mark3"] = "Sega - Master System - Mark III",
        ["mastersystem"] = "Sega - Master System - Mark III", ["megacd"] = "Sega - Mega-CD - Sega CD", ["megacdjp"] = "Sega - Mega-CD - Sega CD", ["segacd"] = "Sega - Mega-CD - Sega CD",
        ["megaduck"] = "Welback - Mega-Duck", ["moto"] = "Thomson - MOTO", ["to8"] = "Thomson - MOTO", ["msx"] = "Microsoft - MSX", ["msx1"] = "Microsoft - MSX",
        ["msx2"] = "Microsoft - MSX2", ["msxturbor"] = "Microsoft - MSX2", ["n3ds"] = "Nintendo - Nintendo 3DS", ["n64"] = "Nintendo - Nintendo 64",
        ["n64dd"] = "Nintendo - Nintendo 64DD", ["nds"] = "Nintendo - Nintendo DS", ["neogeo"] = "SNK - Neo Geo", ["neogeocd"] = "SNK - Neo Geo CD",
        ["neogeocdjp"] = "SNK - Neo Geo CD", ["ngage"] = "Nokia - N-Gage", ["ngp"] = "SNK - Neo Geo Pocket", ["ngpc"] = "SNK - Neo Geo Pocket Color",
        ["odyssey2"] = "Magnavox - Odyssey2", ["oric"] = "Tangerine Computer Systems - Oric", ["palm"] = "Mobile - Palm OS", ["pc"] = "IBM - PC and Compatibles",
        ["pc88"] = "NEC - PC-88", ["pc98"] = "NEC - PC-98", ["pcengine"] = "NEC - PC Engine - TurboGrafx 16", ["tg16"] = "NEC - PC Engine - TurboGrafx 16",
        ["pcenginecd"] = "NEC - PC Engine CD - TurboGrafx-CD", ["tg-cd"] = "NEC - PC Engine CD - TurboGrafx-CD", ["supergrafx"] = "NEC - PC Engine SuperGrafx",
        ["pcfx"] = "NEC - PC-FX", ["plus4"] = "Commodore - Plus-4", ["pokemini"] = "Nintendo - Pokemon Mini", ["ps2"] = "Sony - PlayStation 2", ["ps3"] = "Sony - PlayStation 3",
        ["ps4"] = "Sony - PlayStation 4", ["psp"] = "Sony - PlayStation Portable", ["psvita"] = "Sony - PlayStation Vita", ["psx"] = "Sony - PlayStation",
        ["quake"] = "Quake", ["satellaview"] = "Nintendo - Satellaview", ["saturn"] = "Sega - Saturn", ["saturnjp"] = "Sega - Saturn", ["scummvm"] = "ScummVM",
        ["scv"] = "Epoch - Super Cassette Vision", ["sega32x"] = "Sega - 32X", ["sega32xjp"] = "Sega - 32X", ["sega32xna"] = "Sega - 32X",
        ["sfc"] = "Nintendo - Super Nintendo Entertainment System", ["snes"] = "Nintendo - Super Nintendo Entertainment System", ["snesna"] = "Nintendo - Super Nintendo Entertainment System",
        ["sg-1000"] = "Sega - SG-1000", ["spectravideo"] = "Spectravideo - SVI-318 - SVI-328", ["sufami"] = "Nintendo - Sufami Turbo", ["supervision"] = "Watara - Supervision",
        ["supracan"] = "Funtech - Super Acan", ["switch"] = "Nintendo - Switch", ["symbian"] = "Mobile - Symbian", ["ti99"] = "Texas Instruments - TI 99 4A", ["tic80"] = "TIC-80",
        ["triforce"] = "Namco, Sega, Nintendo - TriForce (Cartridges)", ["uzebox"] = "Uzebox", ["vectrex"] = "GCE - Vectrex", ["vic20"] = "Commodore - VIC-20",
        ["videopac"] = "Philips - Videopac+", ["virtualboy"] = "Nintendo - Virtual Boy", ["vsmile"] = "VTech - V.Smile", ["wii"] = "Nintendo - Wii", ["wiiu"] = "Nintendo - Wii U",
        ["wonderswan"] = "Bandai - WonderSwan", ["wonderswancolor"] = "Bandai - WonderSwan Color", ["x1"] = "Sharp - X1", ["x68000"] = "Sharp - X68000",
        ["xbox"] = "Microsoft - Xbox", ["xbox360"] = "Microsoft - Xbox 360", ["xboxone"] = "Microsoft - Xbox One", ["zx81"] = "Sinclair - ZX 81", ["zxspectrum"] = "Sinclair - ZX Spectrum"
    };

    private static readonly HashSet<string> Handhelds = new(StringComparer.OrdinalIgnoreCase)
    {
        "gb", "gbc", "gba", "gbh", "nds", "n3ds", "3ds", "gamegear", "gg", "ngp", "ngpc", "psp", "psvita", "atarilynx", "lynx", "wswan", "wswanc", "supervision",
        "gamecom", "gp32", "pokemini", "gameandwatch", "ngage", "gmaster", "megaduck", "virtualboy"
    };

    private static readonly HashSet<string> Arcades = new(StringComparer.OrdinalIgnoreCase)
    {
        "arcade", "mame", "fba", "fbneo", "neogeo", "naomi", "naomi2", "atomiswave", "model2", "model3", "daphne", "cps", "cps1", "cps2", "cps3", "hikaru",
        "triforce", "namco2x6", "mame-libretro", "mame-advmame", "mame-mame4all", "lindbergh", "teknoparrot", "ice", "segastv", "stv", "naomigd"
    };

    private static readonly HashSet<string> Computers = new(StringComparer.OrdinalIgnoreCase)
    {
        "amiga", "amiga500", "amiga1200", "amigacd32", "cdtv", "c64", "c128", "vic20", "plus4", "msx", "msx1", "msx2", "msxturbor", "zxspectrum", "zx81", "atarist", "atari800",
        "atari8bit", "pc", "dos", "amstradcpc", "apple2", "apple2gs", "x68000", "pc98", "pc88", "pc8800", "bbcmicro", "coco", "dragon32", "trs-80", "oric", "thomson", "fm7",
        "fmtowns", "macintosh", "samcoupe", "enterprise", "ti99", "tvc", "pet", "spectravideo", "sharp", "x1", "zmachine", "scummvm", "tic80", "openbor", "lutro", "pico8"
    };

    public static Kind KindOf(string system) =>
        Handhelds.Contains(system) ? Kind.Handheld : Arcades.Contains(system) || system.StartsWith("cps", StringComparison.OrdinalIgnoreCase) ? Kind.Arcade
        : Computers.Contains(system) || system.StartsWith("amiga", StringComparison.OrdinalIgnoreCase) || system.StartsWith("msx", StringComparison.OrdinalIgnoreCase) ? Kind.Computer
        : Kind.Console;

    private static readonly Color Main = Color.FromArgb(96, 110, 190), Dark = Color.FromArgb(30, 32, 60), Accent = Color.FromArgb(240, 90, 70), Light = Color.FromArgb(130, 230, 130);

    private static readonly Dictionary<(string, int), Bitmap> Cache = [];

    // Icono del sistema de «size» píxeles de lado: el de su consola si lo hay y, si no, el genérico de su tipo.
    public static Bitmap Get(string system, int size)
    {
        string key = IconOf.TryGetValue(system, out var name) ? name : "generic-" + KindOf(system);
        lock (Cache)
        {
            if (Cache.TryGetValue((key, size), out var cached)) return cached;
            Bitmap? bitmap = IconOf.ContainsKey(system) ? FromResource(key, size) : null;
            return Cache[(key, size)] = bitmap ?? Generic(KindOf(system), size);
        }
    }

    // Icono genérico en gris y semitransparente, para cuando todavía no hay ningún sistema elegido.
    public static Bitmap Placeholder(int size)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(("placeholder", size), out var cached)) return cached;
            using var color = Generic(Kind.Console, size);
            var gray = new Bitmap(size, size);
            using var g = Graphics.FromImage(gray);
            var matrix = new ColorMatrix(
            [
                [0.30f, 0.30f, 0.30f, 0, 0], [0.59f, 0.59f, 0.59f, 0, 0], [0.11f, 0.11f, 0.11f, 0, 0],
                [0, 0, 0, 0.45f, 0], [0.18f, 0.18f, 0.18f, 0, 1]
            ]);
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);
            g.DrawImage(color, new Rectangle(0, 0, size, size), 0, 0, size, size, GraphicsUnit.Pixel, attributes);
            return Cache[("placeholder", size)] = gray;
        }
    }

    private static Bitmap? FromResource(string name, int size)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"SystemIcons.{name}.png");
            if (stream is null) return null;
            using var original = new Bitmap(stream);
            var result = new Bitmap(size, size);
            using var g = Graphics.FromImage(result);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(original, new Rectangle(0, 0, size, size));
            return result;
        }
        catch { return null; }
    }

    private static Bitmap Generic(Kind kind, int size)
    {
        using var small = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(small))
        {
            g.Clear(Color.Transparent);
            void R(int x, int y, int w, int h, Color c) { using var b = new SolidBrush(c); g.FillRectangle(b, x, y, w, h); }
            switch (kind)
            {
                case Kind.Handheld:
                    R(3, 1, 10, 14, Main); R(5, 3, 6, 5, Dark); R(6, 4, 4, 3, Light);
                    R(5, 10, 3, 1, Dark); R(6, 9, 1, 3, Dark); R(10, 10, 1, 1, Accent); R(9, 11, 1, 1, Accent);
                    break;
                case Kind.Arcade:
                    R(3, 1, 10, 14, Main); R(3, 1, 10, 2, Accent); R(5, 4, 6, 4, Dark); R(6, 5, 4, 2, Light);
                    R(3, 9, 10, 2, Dark); R(5, 8, 1, 2, Accent); R(9, 9, 1, 1, Accent); R(11, 9, 1, 1, Accent); R(4, 12, 8, 3, Dark);
                    break;
                case Kind.Computer:
                    R(2, 2, 12, 8, Main); R(3, 3, 10, 6, Dark); R(4, 4, 4, 1, Light); R(4, 6, 6, 1, Light);
                    R(6, 10, 4, 1, Main); R(2, 12, 12, 3, Main); R(3, 13, 10, 1, Dark);
                    break;
                default:
                    R(1, 5, 14, 7, Main); R(0, 7, 1, 4, Main); R(15, 7, 1, 4, Main);
                    R(3, 7, 3, 1, Dark); R(4, 6, 1, 3, Dark); R(11, 7, 2, 2, Accent); R(9, 9, 2, 2, Accent);
                    R(2, 12, 3, 1, Dark); R(11, 12, 3, 1, Dark);
                    break;
            }
        }
        var big = new Bitmap(size, size);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new Rectangle(0, 0, size, size), 0, 0, 16, 16, GraphicsUnit.Pixel);
        }
        return big;
    }
}

// Columna de icono de sistema para las rejillas que listan sistemas.
internal static class SystemIconColumn
{
    public static DataGridViewImageColumn Create(int width = 30) => new()
    {
        Name = "icon", HeaderText = "", Width = width, ReadOnly = true, ImageLayout = DataGridViewImageCellLayout.Zoom,
        SortMode = DataGridViewColumnSortMode.NotSortable, Resizable = DataGridViewTriState.False
    };

    // Icono del sistema para la celda (de «size» píxeles de lado).
    public static Image Icon(string system, int size) => PixelSystemIcons.Get(system, size);
}
