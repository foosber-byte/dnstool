using System;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Режим применения обновления: этот экземпляр запущен старой версией приложения,
            // чтобы заменить файлы установки и перезапустить приложение (см. UpdateApplier).
            if (UpdateApplier.TryParseApplyArgs(args, out var appDir, out var parentProcessId))
            {
                UpdateApplier.Run(appDir, parentProcessId);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FileLogger.EnsureInitialized();
            Application.Run(new MainForm());
        }
    }
}
