using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace ElizaAccessBootstrap
{
    /// <summary>
    /// Loads the accessibility mod into the stock game process.
    ///
    /// Eliza.exe.config names this class as the process's AppDomainManager,
    /// so the .NET runtime constructs it in the default domain before the
    /// game's Main runs. We start a background thread that loads
    /// ElizaAccessDll.dll from the game folder and calls
    /// <c>ElizaAccess.Entry.Initialize(gameDir)</c> — the same entry point
    /// the old libvorbisfile.dll proxy called. The mod waits for the game
    /// assembly to load on its own, so starting early is fine.
    ///
    /// Everything here swallows exceptions: this class runs before the game
    /// does, and anything escaping it would stop the game from launching.
    /// A broken or missing mod should only mean "no mod".
    /// </summary>
    public sealed class Bootstrap : AppDomainManager
    {
        private const string ModFileName  = "ElizaAccessDll.dll";
        private const string EntryType    = "ElizaAccess.Entry";
        private const string EntryMethod  = "Initialize";

        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            try
            {
                if (!AppDomain.CurrentDomain.IsDefaultAppDomain()) return;
                var thread = new Thread(Start)
                {
                    IsBackground = true,
                    Name = "ElizaAccess-Bootstrap",
                };
                thread.Start();
            }
            catch (Exception ex) { Fail(ex); }
        }

        private static void Start()
        {
            try
            {
                string gameDir = AppDomain.CurrentDomain.BaseDirectory;
                string modPath = Path.Combine(gameDir, ModFileName);
                if (!File.Exists(modPath)) return;   // mod removed; run vanilla

                Assembly mod = Assembly.LoadFrom(modPath);
                MethodInfo? init = mod.GetType(EntryType, throwOnError: true)!
                    .GetMethod(EntryMethod, BindingFlags.Public | BindingFlags.Static,
                               null, new[] { typeof(string) }, null);
                if (init == null) throw new MissingMethodException(EntryType, EntryMethod);
                init.Invoke(null, new object[] { gameDir });
            }
            catch (Exception ex) { Fail(ex); }
        }

        // The mod's own log lives under Documents and is set up by the mod,
        // so a failure to load the mod goes to the temp folder instead.
        private static void Fail(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "eliza-access-bootstrap.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [Bootstrap] {ex}\r\n");
            }
            catch { }
        }
    }
}
