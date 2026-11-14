using System.Globalization;

namespace BlueStar.Linux;

/// <summary>
/// UI text for the Linux head, Spanish or English from the system language.
/// shortcut: plain properties instead of the WPF Strings.*.xaml; move both heads to one resx when the screens are shared.
/// </summary>
public static class L
{
    private static readonly bool Es = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";
    private static string T(string es, string en) => Es ? es : en;

    public static string Library => T("Biblioteca", "Library");
    public static string Settings => T("Ajustes", "Settings");
    public static string Requirements => T("Requisitos", "Requirements");
    public static string AddFolder => T("Añadir carpeta de juego", "Add game folder");
    public static string ScanSteam => T("Buscar juegos de Steam", "Find Steam games");
    public static string Import => T("Importar", "Import");
    public static string Play => T("Jugar", "Play");
    public static string Stop => T("Detener", "Stop");
    public static string OpenFolder => T("Abrir carpeta", "Open folder");
    public static string Install => T("Instalar", "Install");
    public static string Uninstall => T("Desinstalar ReFix", "Uninstall ReFix");
    public static string Empty => T("Tu biblioteca está vacía. Añade la carpeta de un juego de Windows o importa uno de Steam.",
                                    "Your library is empty. Add a Windows game folder or import one from Steam.");
    public static string Running => T("En ejecución", "Running");
    public static string Ready => T("Listo", "Ready");
    public static string NoExe => T("No se encontró el .exe", "No .exe found");
    public static string Strategy => T("Cómo iniciar los juegos", "How games start");
    public static string Runner => T("Versión de Proton", "Proton version");
    public static string ExtraEnv => T("Variables de entorno extra (una por línea, CLAVE=valor)", "Extra environment variables (one per line, KEY=value)");
    public static string ExtraDll => T("Overrides de DLL extra (ej. d3d9=n,b)", "Extra DLL overrides (e.g. d3d9=n,b)");
    public static string Save => T("Guardar", "Save");
    public static string Saved => T("Guardado.", "Saved.");
    public static string Recheck => T("Comprobar otra vez", "Check again");
    public static string CompatTitle => T("Compatibilidad (Wine/Proton)", "Compatibility (Wine/Proton)");
    public static string StrategyUmu => T("umu-launcher (recomendado)", "umu-launcher (recommended)");
    public static string StrategyProton => T("Proton directo", "Proton directly");
    public static string StrategySteam => T("A través de Steam", "Through Steam");
    public static string UmuAuto => T("UMU-Proton (automático)", "UMU-Proton (automatic)");
    public static string GeLatest => T("GE-Proton (última)", "GE-Proton (latest)");
    public static string Beta => T("Beta para Linux: biblioteca, lanzamiento con Proton y ReFix. Explorar, descargas y mods siguen solo en Windows por ahora.",
                                   "Linux beta: library, Proton launch and ReFix. Browse, downloads and mods are Windows-only for now.");
    public static string Optional => T("opcional", "optional");
}
