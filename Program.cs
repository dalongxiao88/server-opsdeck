using System;
using System.Windows.Forms;

namespace RDPManager
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            StartupSession session;
            if (!StartupSecurity.TryUnlock(AppDomain.CurrentDomain.BaseDirectory, out session))
                return;

            Application.Run(new MainForm(session));
        }
    }
}
