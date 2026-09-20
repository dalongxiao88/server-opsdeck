using System;
using System.Windows.Forms;

namespace ServerForge
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            StartupSession session;
            if (!StartupSecurity.TryUnlock(GetExecutableDirectory(), out session))
                return;

            Application.Run(new MainForm(session));
        }

        private static string GetExecutableDirectory()
        {
            string executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
                executablePath = Application.ExecutablePath;

            string directory = System.IO.Path.GetDirectoryName(executablePath);
            return string.IsNullOrWhiteSpace(directory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : directory;
        }
    }
}
