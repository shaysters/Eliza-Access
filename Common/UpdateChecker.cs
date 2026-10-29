using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Checks GitHub once per launch for a newer release of the mod, so
    /// the title screen can say so (<see cref="Views.TitleMenuView"/>).
    ///
    /// Anonymous request to the releases API's "latest" endpoint, which
    /// skips drafts and prereleases. Any failure — offline, rate-limited,
    /// or a 404 while the repository is private — is logged and stays
    /// silent: an update notice is a nicety, never an error the player
    /// should hear.
    ///
    /// Runs on its own background thread; the result is a single
    /// volatile string the poll thread reads.
    /// </summary>
    internal static class UpdateChecker
    {
        private const string Repo = "shaysters/Eliza-Access";
        private const string LatestReleaseUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
        private const int TimeoutMs = 10000;

        private static volatile string? _newerVersion;
        private static int _started;

        /// <summary>The newer version's number ("1.0.1"), once the check
        /// has found one; null while running, on failure, or when up to
        /// date.</summary>
        public static string? NewerVersion => _newerVersion;

        /// <summary>The running mod's version ("1.0.0"), without the
        /// "+commit" suffix the build appends.</summary>
        public static string CurrentVersion
        {
            get
            {
                var attrs = typeof(UpdateChecker).Assembly.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                string v = attrs.Length > 0
                    ? ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion
                    : "0";
                int plus = v.IndexOf('+');
                return plus >= 0 ? v.Substring(0, plus) : v;
            }
        }

        /// <summary>Start the check (once per process) unless the player
        /// turned it off.</summary>
        public static void Start(Action<string> log)
        {
            if (!ModConfig.General.CheckForUpdates) return;
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            var thread = new Thread(() => Run(log))
            {
                IsBackground = true,
                Name = "ElizaAccess update check",
            };
            thread.Start();
        }

        /// <summary>Forget a found update once it has been announced, so
        /// returning to the title screen doesn't repeat it.</summary>
        public static void MarkAnnounced() => _newerVersion = null;

        private static void Run(Action<string> log)
        {
            try
            {
                // The game targets an older .NET Framework, whose default
                // protocols don't include the TLS 1.2 GitHub requires.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                var req = (HttpWebRequest)WebRequest.Create(LatestReleaseUrl);
                req.UserAgent = "ElizaAccess/" + CurrentVersion;
                req.Accept = "application/vnd.github+json";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;

                string body;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream()))
                    body = reader.ReadToEnd();

                Match m = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                if (!m.Success)
                {
                    log("[Update] no tag_name in the latest-release response");
                    return;
                }
                string latest = m.Groups[1].Value;
                string current = CurrentVersion;
                if (!Version.TryParse(latest, out Version? latestV) || !Version.TryParse(current, out Version? currentV))
                {
                    log($"[Update] couldn't compare versions (latest={latest}, current={current})");
                    return;
                }
                if (latestV > currentV)
                {
                    _newerVersion = latest;
                    log($"[Update] {latest} is available (running {current})");
                }
                else
                {
                    log($"[Update] up to date (latest={latest}, running {current})");
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r)
            {
                log($"[Update] check failed: HTTP {(int)r.StatusCode}");
            }
            catch (Exception ex)
            {
                log($"[Update] check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
