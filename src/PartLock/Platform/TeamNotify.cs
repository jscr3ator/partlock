// PartLock - posts "Sam checked in Wing_Assembly.SLDASM" style messages to a team chat webhook.
//
// The webhook is configured once by the team lead and stored in the vault at
// .partlock/config (key=value lines), so every teammate's PartLock uses it after Get Latest:
//
//   webhook=https://discord.com/api/webhooks/...
//   notify=checkout,checkin,undo,forceunlock
//
// Discord, Slack and Microsoft Teams are detected from the URL; anything else gets a generic
// JSON body with the same text plus structured fields. Sending never throws and never blocks
// the action that triggered it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace PartLock.Platform
{
    public enum TeamEvent { CheckOut, CheckIn, Undo, ForceUnlock, Test }

    /// <summary>Who did it: shown as the message's name and picture (Discord) or in the text (others).</summary>
    public sealed class TeamSender
    {
        public string Name { get; set; }
        public string Login { get; set; }
        public string AvatarUrl { get; set; }

        /// <summary>GitHub name (or username), never empty.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
                if (!string.IsNullOrWhiteSpace(Login)) return Login.Trim();
                return "Someone";
            }
        }

        /// <summary>The GitHub profile picture; falls back to github.com/&lt;login&gt;.png.</summary>
        public string Avatar
        {
            get
            {
                if (!string.IsNullOrEmpty(AvatarUrl)) return AvatarUrl;
                return string.IsNullOrEmpty(Login) ? null : "https://github.com/" + Uri.EscapeDataString(Login) + ".png";
            }
        }

        /// <summary>From the signed-in GitHub account, with the git commit name / lock username as fallbacks.</summary>
        public static TeamSender From(PartLock.Core.GitHubAccount account, string fallbackName, string fallbackLogin)
        {
            var sender = new TeamSender();
            if (account != null)
            {
                sender.Name = account.Name;
                sender.Login = account.Login;
                sender.AvatarUrl = account.AvatarUrl;
            }
            if (string.IsNullOrWhiteSpace(sender.Login)) sender.Login = fallbackLogin;
            if (string.IsNullOrWhiteSpace(sender.Name)) sender.Name = string.IsNullOrWhiteSpace(fallbackName) ? null : fallbackName;
            return sender;
        }
    }

    public sealed class TeamNotifyConfig
    {
        public string Webhook { get; set; }
        public HashSet<TeamEvent> Events { get; } = new HashSet<TeamEvent>
        {
            TeamEvent.CheckOut, TeamEvent.CheckIn, TeamEvent.Undo, TeamEvent.ForceUnlock
        };

        public bool Enabled { get { return !string.IsNullOrWhiteSpace(Webhook); } }
    }

    public static class TeamNotify
    {
        public const string ConfigRelativePath = ".partlock/config";

        private static readonly string[][] EventNames =
        {
            new[] { "checkout", "CheckOut" },
            new[] { "checkin", "CheckIn" },
            new[] { "undo", "Undo" },
            new[] { "forceunlock", "ForceUnlock" }
        };

        public static string ConfigPath(string repoRoot)
        {
            return Path.Combine(repoRoot, ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public static TeamNotifyConfig Load(string repoRoot)
        {
            var config = new TeamNotifyConfig();
            try
            {
                string file = ConfigPath(repoRoot);
                if (!File.Exists(file)) return config;
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (string.Equals(key, "webhook", StringComparison.OrdinalIgnoreCase))
                    {
                        config.Webhook = value;
                    }
                    else if (string.Equals(key, "notify", StringComparison.OrdinalIgnoreCase))
                    {
                        config.Events.Clear();
                        foreach (string part in value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            foreach (string[] e in EventNames)
                                if (string.Equals(part, e[0], StringComparison.OrdinalIgnoreCase))
                                    config.Events.Add((TeamEvent)Enum.Parse(typeof(TeamEvent), e[1]));
                        }
                    }
                }
            }
            catch (Exception) { }
            return config;
        }

        /// <summary>Writes .partlock/config (keeps other keys). Returns its repo-relative path.</summary>
        public static string Save(string repoRoot, TeamNotifyConfig config)
        {
            string file = ConfigPath(repoRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(file));

            var keep = new List<string>();
            if (File.Exists(file))
            {
                foreach (string raw in File.ReadAllLines(file))
                {
                    string key = raw.Split('=')[0].Trim();
                    if (string.Equals(key, "webhook", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "notify", StringComparison.OrdinalIgnoreCase) ||
                        raw.StartsWith("# PartLock team settings", StringComparison.Ordinal)) continue;
                    keep.Add(raw);
                }
            }

            var text = new StringBuilder();
            text.AppendLine("# PartLock team settings (shared with everyone who uses this vault)");
            if (config.Enabled)
            {
                text.AppendLine("webhook=" + config.Webhook.Trim());
                text.AppendLine("notify=" + string.Join(",", EventNames
                    .Where(e => config.Events.Contains((TeamEvent)Enum.Parse(typeof(TeamEvent), e[1])))
                    .Select(e => e[0]).ToArray()));
            }
            foreach (string line in keep) if (line.Trim().Length > 0) text.AppendLine(line);
            File.WriteAllText(file, text.ToString(), new UTF8Encoding(false));
            return ConfigRelativePath;
        }

        /// <summary>Fire-and-forget: posts the event if the vault has a webhook for it.</summary>
        public static void Send(string repoRoot, TeamEvent kind, TeamSender who, IEnumerable<string> paths, string message = null)
        {
            TeamNotifyConfig config = Load(repoRoot);
            if (!config.Enabled || !config.Events.Contains(kind)) return;
            List<string> list = (paths ?? new string[0]).ToList();
            Task.Run(async () =>
            {
                try { await PostAsync(config.Webhook, kind, who, list, message, repoRoot).ConfigureAwait(false); }
                catch (Exception ex) { PartLock.UI.ErrorDialog.LogToFile("Team notification failed.", ex.Message); }
            });
        }

        /// <summary>Sends a test message; throws with a readable message on failure.</summary>
        public static Task SendTestAsync(string webhook, string repoRoot, TeamSender who)
        {
            return PostAsync(webhook, TeamEvent.Test, who, new List<string>(), null, repoRoot);
        }

        /// <summary>
        /// The message text. With <paramref name="includeName"/> false (Discord, where the message
        /// already shows the person's GitHub name and picture) it reads "Just checked out `a`, `b`".
        /// </summary>
        public static string Describe(string repoRoot, TeamEvent kind, TeamSender who, IEnumerable<string> paths,
            string message, bool includeName)
        {
            List<string> list = (paths ?? new string[0]).Where(p => !string.IsNullOrEmpty(p) &&
                !p.StartsWith(".partlock/", StringComparison.OrdinalIgnoreCase)).ToList();
            string files = FileList(list);
            string repo = RepoName(repoRoot);
            string subject = includeName
                ? "**" + who.DisplayName + "**" +
                  (string.IsNullOrEmpty(who.Login) || string.Equals(who.Login, who.DisplayName, StringComparison.OrdinalIgnoreCase)
                      ? string.Empty : " (@" + who.Login + ")") + " just"
                : "Just";
            switch (kind)
            {
                case TeamEvent.CheckOut:
                    return "\U0001F512 " + subject + " checked out " + files + " in **" + repo + "**";
                case TeamEvent.CheckIn:
                    return "✅ " + subject + " checked in " + files + " in **" + repo + "**" +
                           (string.IsNullOrWhiteSpace(message) ? string.Empty : "\n> " + message.Trim().Replace("\n", "\n> "));
                case TeamEvent.Undo:
                    return "↩️ " + subject + " undid their check-out of " + files + " in **" + repo + "** (changes thrown away)";
                case TeamEvent.ForceUnlock:
                    return "⚠️ " + subject + " force-unlocked " + files + " in **" + repo + "**";
                default:
                    return "\U0001F44B " + subject + " connected PartLock to this channel. Check-outs and check-ins in **" +
                           repo + "** will show up here.";
            }
        }

        private static string FileList(List<string> paths)
        {
            if (paths.Count == 0) return "files";
            var names = paths.Select(p => "`" + Path.GetFileName(p.Replace('/', Path.DirectorySeparatorChar)) + "`").ToList();
            if (names.Count <= 6) return string.Join(", ", names.ToArray());
            return string.Join(", ", names.Take(5).ToArray()) + " and " + (names.Count - 5) + " more";
        }

        private static string RepoName(string repoRoot)
        {
            try { return Path.GetFileName(repoRoot.TrimEnd('\\', '/')); }
            catch (Exception) { return "the vault"; }
        }

        private static async Task PostAsync(string webhook, TeamEvent kind, TeamSender who,
            List<string> paths, string message, string repoRoot)
        {
            if (who == null) who = new TeamSender();
            Uri uri;
            if (!Uri.TryCreate((webhook ?? string.Empty).Trim(), UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("The webhook address must start with https://");

            string host = uri.Host.ToLowerInvariant();
            string body;
            string text;
            if (host.EndsWith("discord.com") || host.EndsWith("discordapp.com"))
            {
                // Posts as the person: their GitHub name and profile picture.
                text = Describe(repoRoot, kind, who, paths, message, false);
                if (text.Length > 1900) text = text.Substring(0, 1900) + "…";
                string name = who.DisplayName;
                if (name.Length > 80) name = name.Substring(0, 80);
                if (name.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("clyde", StringComparison.OrdinalIgnoreCase) >= 0) name = "PartLock"; // names Discord rejects
                body = "{\"username\":" + Json(name) +
                       (who.Avatar != null ? ",\"avatar_url\":" + Json(who.Avatar) : string.Empty) +
                       ",\"content\":" + Json(text) + ",\"allowed_mentions\":{\"parse\":[]}}";
            }
            else if (host.EndsWith("slack.com"))
            {
                // Slack uses *bold*, not **bold**. Newer Slack apps ignore username/icon_url, so the name stays in the text.
                text = Describe(repoRoot, kind, who, paths, message, true);
                body = "{\"text\":" + Json(text.Replace("**", "*")) + ",\"username\":" + Json(who.DisplayName) +
                       (who.Avatar != null ? ",\"icon_url\":" + Json(who.Avatar) : string.Empty) + "}";
            }
            else if (host.Contains("office.com") || host.Contains("office365.com") || host.Contains("logic.azure.com"))
            {
                text = Describe(repoRoot, kind, who, paths, message, true);
                body = "{\"text\":" + Json(text) + "}";
            }
            else
            {
                text = Describe(repoRoot, kind, who, paths, message, true);
                var fileJson = paths.Select(Json).ToArray();
                body = "{\"text\":" + Json(text) + ",\"content\":" + Json(text) +
                       ",\"username\":" + Json(who.DisplayName) +
                       (who.Avatar != null ? ",\"avatar_url\":" + Json(who.Avatar) : string.Empty) +
                       ",\"event\":" + Json(kind.ToString().ToLowerInvariant()) +
                       ",\"user\":{\"name\":" + Json(who.DisplayName) + ",\"login\":" + Json(who.Login) + "}" +
                       ",\"repository\":" + Json(RepoName(repoRoot)) +
                       ",\"message\":" + Json(message) +
                       ",\"files\":[" + string.Join(",", fileJson) + "]}";
            }

            byte[] data = Encoding.UTF8.GetBytes(body);
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.UserAgent = "PartLock";
            request.Timeout = 15000;
            request.ContentLength = data.Length;
            using (Stream s = await request.GetRequestStreamAsync().ConfigureAwait(false))
                await s.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            try
            {
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)) { }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                throw new InvalidOperationException(r != null
                    ? "The chat service answered " + (int)r.StatusCode + " " + r.StatusDescription + ". Check the webhook address."
                    : "Couldn't reach the chat service (" + ex.Message + ").");
            }
        }

        private static string Json(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
