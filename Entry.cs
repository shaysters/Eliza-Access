using System;
using System.IO;
using System.Threading;
using ElizaAccess.Common;


namespace ElizaAccess
{
    /// <summary>
    /// Mod entry point. Called by the AppDomainManager in
    /// ElizaAccessBootstrap.dll (wired up by Eliza.exe.config), and — for
    /// players still on the old manual install — by the libvorbisfile.dll
    /// proxy via ICLRRuntimeHost::ExecuteInDefaultAppDomain.
    ///
    /// Signature must be exactly: public static int Initialize(string arg)
    /// The argument is the game directory path.
    /// </summary>
    public static class Entry
    {
        private static Main? _main;

        // Set on the AppDomain (not a static) because the two loaders load
        // this assembly in different load contexts, giving each its own
        // copy of every static. The AppDomain object is shared, so it is
        // both the flag store and the lock.
        private const string StartedKey = "ElizaAccess.Started";

        /// <summary>
        /// gamePath = game directory path (or exe path from the old proxy).
        /// Only the first call in a process starts the mod; later ones
        /// return immediately.
        /// </summary>
        public static int Initialize(string gamePath)
        {
            AppDomain domain = AppDomain.CurrentDomain;
            lock (domain)
            {
                if (domain.GetData(StartedKey) != null) return 0;
                domain.SetData(StartedKey, true);
            }

            try
            {
                // %LocalAppData%\ElizaAccess — never redirected or synced
                // by OneDrive, unlike Documents.
                string accessibilityDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ElizaAccess");
                MigrateLegacySettings(accessibilityDir);
                string logDir = Path.Combine(accessibilityDir, "Logs");
                try { Directory.CreateDirectory(logDir); } catch { }
                string logPath = Path.Combine(logDir, "eliza-access.log");

                // Rotate: keep one previous log so last session is always available.
                try
                {
                    if (File.Exists(logPath))
                    {
                        string prev = Path.Combine(logDir, "eliza-access.prev.log");
                        if (File.Exists(prev)) File.Delete(prev);
                        File.Move(logPath, prev);
                    }
                }
                catch { }

                string version = typeof(Entry).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    is System.Reflection.AssemblyInformationalVersionAttribute[] v && v.Length > 0
                    ? v[0].InformationalVersion : "?";
                Log(logPath, $"[Entry] Initialize. version={version} gamePath={gamePath}");

                // Load user mod settings before Main spins up so any
                // observer that reads ModConfig at construction time
                // sees the persisted values, not the bare defaults.
                ModConfig.Init(accessibilityDir, msg => Log(logPath, msg));

                _main = new Main(logPath);

                // STA thread required for JAWS COM late-binding.
                var thread = new Thread(() =>
                {
                    try   { _main.Run(); }
                    catch (Exception ex)
                        { Log(logPath, $"[Entry] Main loop crashed: {ex}"); }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Name = "ElizaAccess-Main";
                thread.Start();

                Log(logPath, "[Entry] STA thread started.");
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(Path.GetTempPath(), "eliza-access.log"),
                        $"[Entry] FATAL: {ex}\r\n");
                }
                catch { }
                return -1;
            }
        }

        // Older versions kept settings in Documents\My Games\Eliza\
        // Accessibility. Carry config.json over once, the first time the
        // new folder has none; logs and dumps are left behind.
        private static void MigrateLegacySettings(string accessibilityDir)
        {
            try
            {
                string newConfig = Path.Combine(accessibilityDir, "config.json");
                string oldConfig = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "My Games", "Eliza", "Accessibility", "config.json");
                if (File.Exists(newConfig) || !File.Exists(oldConfig)) return;
                Directory.CreateDirectory(accessibilityDir);
                File.Copy(oldConfig, newConfig);
            }
            catch { }
        }

        private static void Log(string path, string message)
        {
            try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n"); }
            catch { }
        }
    }
}
