using System.Text;

namespace DboAssetsStudio;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowStartupError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ShowStartupError(ex);
        };
        try
        {
            using var form = new MainForm();
            form.InitialArguments = args;
            Application.Run(form);
        }
        catch (Exception ex)
        {
            ShowStartupError(ex);
        }
    }

    private static void ShowStartupError(Exception ex)
    {
        try
        {
            string dir = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBO_STUDIO_DATA_ROOT"))
                ? Path.Combine(AppPaths.Root, "startup")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBO Assets Studio", "Table Studio");
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "startup-error.log");
            File.AppendAllText(log, $"[{DateTime.Now:O}] {ex}\r\n\r\n", Encoding.UTF8);
            MessageBox.Show($"Table Studio no pudo iniciar.\n\n{ex.Message}\n\nRegistro: {log}", "DBO DBO Assets Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            MessageBox.Show($"Table Studio no pudo iniciar.\n\n{ex}", "DBO DBO Assets Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
