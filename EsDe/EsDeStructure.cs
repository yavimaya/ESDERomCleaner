namespace ESDERomCleaner;

// Estructura de carpetas oficial de ES-DE (según su USERGUIDE y su es_systems.xml para Windows).
internal static class EsDeStructure
{
    public static readonly string[] MediaFolders =
        ["3dboxes", "backcovers", "covers", "custom", "fanart", "manuals", "marquees", "miximages", "physicalmedia", "screenshots", "titlescreens", "videos"];

    // Carpetas de datos de ES-DE que se pueden crear de antemano (settings y logs los genera la propia aplicación).
    public static readonly string[] DataFolders =
        [@"ES-DE\gamelists", @"ES-DE\downloaded_media", @"ES-DE\collections", @"ES-DE\custom_systems", @"ES-DE\scripts", @"ES-DE\themes"];

    public static readonly string[] Systems =
    [
        "3do", "adam", "ags", "amiga", "amiga1200", "amiga600", "amigacd32", "amstradcpc", "android", "androidapps", "androidgames", "apple2", "apple2gs",
        "arcade", "arcadia", "archimedes", "arduboy", "astrocde", "atari2600", "atari5200", "atari7800", "atari800", "atarijaguar", "atarijaguarcd",
        "atarilynx", "atarist", "atarixe", "atomiswave", "bbcmicro", "c64", "cdimono1", "cdtv", "chailove", "channelf", "coco", "colecovision",
        "consolearcade", "cps", "cps1", "cps2", "cps3", "crvision", "daphne", "desktop", "doom", "dos", "dragon32", "dreamcast", "easyrpg", "electron",
        "emulators", "epic", "famicom", "fba", "fbneo", "fds", "flash", "fm7", "fmtowns", "fpinball", "gamate", "gameandwatch", "gamecom", "gamegear",
        "gb", "gba", "gbc", "gc", "genesis", "gmaster", "gx4000", "intellivision", "j2me", "kodi", "laserdisc", "lcdgames", "lowresnx", "lutris", "lutro",
        "macintosh", "mame", "mame-advmame", "mark3", "mastersystem", "megacd", "megacdjp", "megadrive", "megadrivejp", "megaduck", "mess", "model2",
        "model3", "moto", "msx", "msx1", "msx2", "msxturbor", "mugen", "multivision", "n3ds", "n64", "n64dd", "naomi", "naomi2", "naomigd", "nds",
        "neogeo", "neogeocd", "neogeocdjp", "nes", "ngage", "ngp", "ngpc", "odyssey2", "openbor", "oric", "palm", "pc", "pc88", "pc98", "pcarcade",
        "pcengine", "pcenginecd", "pcfx", "pico8", "plus4", "pokemini", "ports", "ps2", "ps3", "ps4", "psp", "psvita", "psx", "pv1000", "quake",
        "samcoupe", "satellaview", "saturn", "saturnjp", "scummvm", "scv", "sega32x", "sega32xjp", "sega32xna", "segacd", "sfc", "sg-1000", "sgb", "snes",
        "snesna", "solarus", "spectravideo", "steam", "stv", "sufami", "supergrafx", "supervision", "supracan", "switch", "symbian", "tanodragon", "tg16",
        "tg-cd", "ti99", "tic80", "to8", "triforce", "trs-80", "type-x", "uzebox", "vectrex", "vic20", "videopac", "vircon32", "virtualboy", "vpinball",
        "vsmile", "wasm4", "wii", "wiiu", "windows", "windows3x", "windows9x", "wonderswan", "wonderswancolor", "x1", "x68000", "xbox", "xbox360",
        "xboxone", "zmachine", "zx81", "zxnext", "zxspectrum"
    ];

    // Todas las carpetas de la estructura completa, relativas a la raíz.
    public static IEnumerable<string> AllFolders()
    {
        yield return "Roms";
        foreach (var folder in DataFolders) yield return folder;
        foreach (var system in Systems)
        {
            yield return Path.Combine("Roms", system);
            yield return Path.Combine(@"ES-DE\gamelists", system);
            foreach (var media in MediaFolders) yield return Path.Combine(@"ES-DE\downloaded_media", system, media);
        }
    }
}
