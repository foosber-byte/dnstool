using System;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FileLogger.EnsureInitialized();
            Application.Run(new MainForm());
        }
    }
}

