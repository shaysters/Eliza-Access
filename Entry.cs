using System;
using System.IO;
using System.Threading;


namespace ElizaAccess
{
    /// <summary>
    /// CLR entry point called by the libvorbisfile.dll proxy via
    /// ICLRRuntimeHost::ExecuteInDefaultAppDomain.
    ///
    /// Signature must be exactly: public static int Initialize(string arg)
    /// The argument is the game directory path (passed from C++ bootstrap).
    /// </summary>
    public static class Entry
    {
        private static Main? _main;

        /// <summary>
        /// Called by ICLRRuntimeHost::ExecuteInDefaultAppDomain.
        /// gamePath = game directory path or exe path, passed from C++ bootstrap.
        /// </summary>
        public static int Initialize(string gamePath)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "My Games", "Eliza", "Accessibility", "Logs");
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

                Log(logPath, $"[Entry] Initialize. gamePath={gamePath}");

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

        private static void Log(string path, string message)
        {
            try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n"); }
            catch { }
        }
    }
}
