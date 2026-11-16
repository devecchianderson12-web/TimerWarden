using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CandadoApp
{
    internal struct BlockedHit
    {
        public string Name;
        public string FileName;
    }

    // Revisa periódicamente los procesos y congela las apps de la lista hasta que se escriba la contraseña.
    // El estado se lleva por app (.exe), no por proceso: una app desbloqueada que se cierra pero sigue viva en
    // segundo plano (Steam, Discord, Epic...) vuelve a pedir la contraseña cuando se abre otra ventana suya.
    internal sealed class Watcher
    {
        private enum State { Pending, Authorized, Dormant }

        private sealed class Family
        {
            public State State;
            public int Since;       // momento del desbloqueo (Environment.TickCount)
            public int LastWindow;  // última vez que se vio una ventana suya
            public bool HadWindow;
        }

        private const int ClosedGraceMs = 3000;    // sin ventanas este tiempo = la app se cerró
        private const int NoWindowGraceMs = 20000; // desbloqueada pero nunca mostró ventana

        private readonly Settings settings;
        private readonly Dictionary<string, Family> families = new Dictionary<string, Family>();
        private readonly Dictionary<int, string> suspended = new Dictionary<int, string>(); // pid -> app
        private readonly uint selfPid = (uint)Process.GetCurrentProcess().Id;

        public Watcher(Settings settings)
        {
            this.settings = settings;
        }

        public List<BlockedHit> Poll()
        {
            List<BlockedHit> hits = new List<BlockedHit>();
            if (!settings.ProtectionEnabled || !settings.HasCredential || settings.Blocked.Count == 0)
                return hits;

            HashSet<string> names = new HashSet<string>(settings.Blocked.Select(b => b.FileName).Where(n => n.Length > 0));
            if (names.Count == 0)
                return hits;

            List<Native.ProcessInfo> all = Native.Snapshot();
            Dictionary<string, List<int>> running = new Dictionary<string, List<int>>();
            foreach (Native.ProcessInfo p in all)
            {
                if (p.Id == 0 || p.Id == selfPid || !names.Contains(p.ExeFile))
                    continue;
                List<int> list;
                if (!running.TryGetValue(p.ExeFile, out list))
                    running[p.ExeFile] = list = new List<int>();
                list.Add((int)p.Id);
            }

            // Se olvidan los procesos y apps que ya no corren (salvo la que tiene la pantalla de bloqueo abierta).
            HashSet<int> alive = new HashSet<int>(running.Values.SelectMany(l => l));
            foreach (int pid in suspended.Keys.Where(id => !alive.Contains(id)).ToList())
                suspended.Remove(pid);
            foreach (string gone in families.Keys.Where(k => !running.ContainsKey(k) && families[k].State != State.Pending).ToList())
                families.Remove(gone);

            HashSet<int> windowed = null;
            int now = Environment.TickCount;
            foreach (KeyValuePair<string, List<int>> entry in running)
            {
                Family family;
                families.TryGetValue(entry.Key, out family);

                if (family != null && family.State != State.Pending)
                {
                    if (windowed == null)
                        windowed = PidsWithWindow(all);
                    bool hasWindow = entry.Value.Any(windowed.Contains);

                    if (family.State == State.Authorized)
                    {
                        if (hasWindow)
                        {
                            family.HadWindow = true;
                            family.LastWindow = now;
                        }
                        else if (family.HadWindow ? now - family.LastWindow > ClosedGraceMs : now - family.Since > NoWindowGraceMs)
                        {
                            family.State = State.Dormant;
                        }
                        continue;
                    }
                    if (!hasWindow)
                        continue; // sigue en segundo plano: se bloquea recién cuando vuelva a mostrar una ventana
                }

                // App nueva o reabierta: se congelan todos sus procesos (también los que aparezcan mientras espera).
                foreach (int pid in entry.Value)
                    Suspend(pid, entry.Key);

                if (family == null || family.State != State.Pending)
                {
                    families[entry.Key] = new Family { State = State.Pending };
                    hits.Add(new BlockedHit { Name = DisplayName(entry.Key), FileName = entry.Key });
                }
            }
            return hits;
        }

        // Una app puede tener varios procesos con el mismo .exe: un desbloqueo (o un cierre) los cubre a todos.
        public void AuthorizeFamily(string fileName)
        {
            families[fileName] = new Family { State = State.Authorized, Since = Environment.TickCount };
            foreach (int pid in suspended.Where(s => s.Value == fileName).Select(s => s.Key).ToList())
            {
                Native.Resume(pid);
                suspended.Remove(pid);
            }
        }

        public void KillFamily(string fileName)
        {
            families.Remove(fileName);
            foreach (Native.ProcessInfo p in Native.Snapshot())
            {
                if (p.Id == 0 || p.Id == selfPid || p.ExeFile != fileName)
                    continue;
                int pid = (int)p.Id;
                if (suspended.Remove(pid))
                    Native.Resume(pid);
                Native.Kill(pid);
            }
            foreach (int pid in suspended.Where(s => s.Value == fileName).Select(s => s.Key).ToList())
                suspended.Remove(pid);
        }

        private void Suspend(int pid, string fileName)
        {
            if (!suspended.ContainsKey(pid) && Native.Suspend(pid))
                suspended[pid] = fileName;
        }

        // Procesos con una ventana principal visible, más sus padres y abuelos: hay apps cuya ventana la dibuja
        // un proceso hijo con otro nombre (p. ej. Steam con steamwebhelper.exe).
        private static HashSet<int> PidsWithWindow(List<Native.ProcessInfo> all)
        {
            Dictionary<uint, uint> parent = new Dictionary<uint, uint>();
            foreach (Native.ProcessInfo p in all)
                parent[p.Id] = p.ParentId;

            HashSet<int> result = new HashSet<int>();
            foreach (uint pid in Native.PidsWithVisibleWindow())
            {
                uint current = pid;
                for (int level = 0; level < 3 && current != 0; level++)
                {
                    result.Add((int)current);
                    uint next;
                    if (!parent.TryGetValue(current, out next) || next == current)
                        break;
                    current = next;
                }
            }
            return result;
        }

        private string DisplayName(string fileName)
        {
            BlockedApp app = settings.Blocked.FirstOrDefault(b => b.FileName == fileName);
            return app != null ? app.Name : fileName;
        }

        private static string TryPath(Process p)
        {
            try { return p.MainModule.FileName; }
            catch { return ""; }
        }

        // Procesos con ventana visible.
        public static List<BlockedApp> RunningWithWindow()
        {
            Dictionary<string, BlockedApp> found = new Dictionary<string, BlockedApp>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || p.MainWindowTitle.Length == 0)
                        continue;
                    string file = (p.ProcessName + ".exe").ToLowerInvariant();
                    if (found.ContainsKey(file))
                        continue;
                    string path = TryPath(p);
                    found[file] = new BlockedApp(path.Length > 0 ? path : file, file);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return found.Values.OrderBy(a => a.Name).ToList();
        }

        // Aplicaciones instaladas (accesos del menú Inicio + rutas del registro) más las abiertas: para el buscador por nombre.
        public static List<BlockedApp> InstalledAndRunning()
        {
            Dictionary<string, BlockedApp> found = new Dictionary<string, BlockedApp>(StringComparer.OrdinalIgnoreCase);

            object shell = CreateShell();
            try
            {
                foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
                {
                    string root = Environment.GetFolderPath(folder);
                    if (root.Length == 0)
                        continue;
                    foreach (string lnk in FindShortcuts(root))
                    {
                        try
                        {
                            string target = ResolveShortcut(shell, lnk);
                            if (target == null || !target.ToLowerInvariant().EndsWith(".exe") || !File.Exists(target))
                                continue;
                            string file = Path.GetFileName(target).ToLowerInvariant();
                            if (file.Length == 0 || found.ContainsKey(file))
                                continue;
                            found[file] = new BlockedApp(target, Path.GetFileNameWithoutExtension(lnk));
                        }
                        catch { }
                    }
                }
            }
            finally
            {
                if (shell != null)
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }

            // Rutas registradas en "App Paths" (programas sin acceso directo en Inicio).
            AddAppPaths(found, Registry.LocalMachine);
            AddAppPaths(found, Registry.CurrentUser);

            // Juegos instalados de Epic y Steam (no aparecen en el menú Inicio).
            AddEpicGames(found);
            AddSteamGames(found);

            foreach (BlockedApp app in RunningWithWindow())
                if (!found.ContainsKey(app.FileName))
                    found[app.FileName] = app;

            return found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // Recorre las carpetas del menú Inicio sin abortar por una subcarpeta sin permisos.
        private static List<string> FindShortcuts(string root)
        {
            List<string> result = new List<string>();
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try { result.AddRange(Directory.GetFiles(dir, "*.lnk")); }
                catch { }
                try { foreach (string sub in Directory.GetDirectories(dir)) stack.Push(sub); }
                catch { }
            }
            return result;
        }

        private static void AddAppPaths(Dictionary<string, BlockedApp> found, RegistryKey hive)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                {
                    if (key == null)
                        return;
                    foreach (string name in key.GetSubKeyNames())
                    {
                        if (!name.ToLowerInvariant().EndsWith(".exe"))
                            continue;
                        try
                        {
                            using (RegistryKey sub = key.OpenSubKey(name))
                            {
                                string path = sub != null ? sub.GetValue(null) as string : null;
                                if (string.IsNullOrEmpty(path))
                                    continue;
                                path = path.Trim('"');
                                if (!File.Exists(path))
                                    continue;
                                string file = Path.GetFileName(path).ToLowerInvariant();
                                if (file.Length == 0 || found.ContainsKey(file))
                                    continue;
                                found[file] = new BlockedApp(path, Path.GetFileNameWithoutExtension(path));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // Juegos de Epic: cada manifiesto .item trae la carpeta, el ejecutable y el nombre visible.
        private static void AddEpicGames(Dictionary<string, BlockedApp> found)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Epic\EpicGamesLauncher\Data\Manifests");
                if (!Directory.Exists(dir))
                    return;
                foreach (string item in Directory.GetFiles(dir, "*.item"))
                {
                    try
                    {
                        string json = File.ReadAllText(item);
                        string install = JsonField(json, "InstallLocation");
                        string exe = JsonField(json, "LaunchExecutable");
                        string name = JsonField(json, "DisplayName");
                        if (install.Length == 0 || exe.Length == 0)
                            continue;
                        string path = Path.Combine(install, exe);
                        if (!File.Exists(path))
                            continue;
                        string file = Path.GetFileName(path).ToLowerInvariant();
                        if (file.Length == 0 || found.ContainsKey(file))
                            continue;
                        found[file] = new BlockedApp(path, name.Length > 0 ? name : Path.GetFileNameWithoutExtension(path));
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Juegos de Steam: se recorren las bibliotecas y se listan los .exe de cada juego.
        private static void AddSteamGames(Dictionary<string, BlockedApp> found)
        {
            try
            {
                string steam = SteamPath();
                if (steam.Length == 0)
                    return;

                List<string> libraries = new List<string> { steam };
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (Directory.Exists(p))
                            libraries.Add(p);
                    }
                }

                foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string common = Path.Combine(library, @"steamapps\common");
                    if (!Directory.Exists(common))
                        continue;
                    string[] games;
                    try { games = Directory.GetDirectories(common); }
                    catch { continue; }

                    foreach (string game in games)
                    {
                        string gameName = Path.GetFileName(game);
                        string norm = Normalize(gameName);

                        List<string> candidates = ShallowExes(game)
                            .Where(e => !IsHelperExe(Path.GetFileName(e).ToLowerInvariant())).ToList();

                        // Preferir el .exe cuyo nombre se parece al del juego; si no hay, los de la carpeta raíz.
                        List<string> matches = candidates.Where(e =>
                        {
                            string n = Normalize(Path.GetFileNameWithoutExtension(e));
                            return n.Length > 2 && (norm.Contains(n) || n.Contains(norm));
                        }).ToList();

                        List<string> chosen = matches.Count > 0
                            ? matches
                            : candidates.Where(e => string.Equals(Path.GetDirectoryName(e), game, StringComparison.OrdinalIgnoreCase)).ToList();

                        foreach (string exe in chosen)
                        {
                            string file = Path.GetFileName(exe).ToLowerInvariant();
                            if (found.ContainsKey(file))
                                continue;
                            found[file] = new BlockedApp(exe, gameName);
                        }
                    }
                }
            }
            catch { }
        }

        // .exe en la carpeta del juego y en sus subcarpetas directas (muchos juegos lo tienen en \bin, \Binaries, etc.).
        private static IEnumerable<string> ShallowExes(string root)
        {
            List<string> list = new List<string>();
            try { list.AddRange(Directory.GetFiles(root, "*.exe")); }
            catch { }
            try
            {
                foreach (string sub in Directory.GetDirectories(root))
                {
                    try { list.AddRange(Directory.GetFiles(sub, "*.exe")); }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private static string Normalize(string text)
        {
            char[] chars = text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }

        private static bool IsHelperExe(string file)
        {
            return file.Contains("unins") || file.Contains("crash") || file.Contains("redist")
                || file.Contains("setup") || file.Contains("vcredist") || file.Contains("dxsetup")
                || file.Contains("install") || file.Contains("launcher_helper") || file.Contains("notification_helper");
        }

        private static string SteamPath()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = k != null ? k.GetValue("SteamPath") as string : null;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                        return p;
                }
            }
            catch { }
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    string p = k != null ? k.GetValue("InstallPath") as string : null;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                        return p;
                }
            }
            catch { }
            return "";
        }

        private static string JsonField(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success)
                return "";
            return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\/", "/");
        }

        private static object CreateShell()
        {
            try
            {
                Type type = Type.GetTypeFromProgID("WScript.Shell");
                return type != null ? Activator.CreateInstance(type) : null;
            }
            catch { return null; }
        }

        // Resuelve el destino de un acceso directo .lnk reutilizando un único objeto WScript.Shell.
        private static string ResolveShortcut(object shell, string lnkPath)
        {
            if (shell == null)
                return null;
            try
            {
                object shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                object target = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
                return target as string;
            }
            catch { return null; }
        }
    }
}
