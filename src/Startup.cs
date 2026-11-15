using System.Diagnostics;
using Microsoft.Win32;

namespace CandadoApp
{
    // Inicio con Windows robusto: corrige la ruta en cada arranque y evita que Windows lo deje deshabilitado.
    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(Program.Name) != null;
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled)
                        key.SetValue(Program.Name, Command());
                    else
                        key.DeleteValue(Program.Name, false);
                }
                if (enabled)
                    Enable();
            }
            catch { }
        }

        // Si quedó registrado, reescribe la ruta actual (por si el .exe se movió o se actualizó) y lo deja habilitado.
        public static void Heal()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null || key.GetValue(Program.Name) == null)
                        return;
                    key.SetValue(Program.Name, Command());
                }
                Enable();
            }
            catch { }
        }

        // Borra la marca de StartupApproved para que Windows no lo mantenga deshabilitado.
        private static void Enable()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                {
                    if (key != null && key.GetValue(Program.Name) != null)
                        key.DeleteValue(Program.Name, false);
                }
            }
            catch { }
        }

        private static string Command()
        {
            return "\"" + ExePath() + "\" " + Program.BackgroundArgument;
        }

        private static string ExePath()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch { return System.Windows.Forms.Application.ExecutablePath; }
        }
    }
}
