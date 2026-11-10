namespace GrandPrixPlayer;

internal static class Program
{
    public static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrandPrixPlayer", "erreurs.log");

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, fatal: true);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(args.FirstOrDefault(File.Exists)));
    }

    private static void Report(Exception? ex, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(fatal ? "FATAL" : "ERREUR")} {ex}\n\n");
        }
        catch { }
        if (!fatal)
            MessageBox.Show($"Erreur inattendue (détails dans {CrashLog}) :\n\n{ex?.Message}", "Grand Prix Player",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
