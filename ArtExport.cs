// ArtExport: save in-game photos and paintings to disk at the quality the server stores them.
// Works on Carbon and Oxide/uMod. MIT licence, see LICENSE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.Networking;

namespace Oxide.Plugins
{
    [Info("ArtExport", "ArtExport contributors", "1.0.0")]
    [Description("Export in-game photos and painted signs to image files, with optional download links and Discord posts.")]
    public class ArtExport : RustPlugin
    {
        #region Configuration

        private const string PermUse = "artexport.use";
        private const string PermOthers = "artexport.others";
        private const string PermAdmin = "artexport.admin";

        private Configuration config;

        private class Configuration
        {
            [JsonProperty("RequirePermission (artexport.use)")]
            public bool RequirePermission = true;

            [JsonProperty("AllowOthersArt (anyone may export art owned by someone else)")]
            public bool AllowOthersArt = true;

            [JsonProperty("CooldownSeconds")]
            public float CooldownSeconds = 5f;

            [JsonProperty("LookRangeMeters")]
            public float LookRange = 6f;

            [JsonProperty("MaxImageBytes (larger images are skipped)")]
            public int MaxImageBytes = 16 * 1024 * 1024;

            [JsonProperty("MaxFilesPerPlayer (0 = no limit)")]
            public int MaxFilesPerPlayer = 200;

            [JsonProperty("MaxTotalMegabytes (0 = no limit)")]
            public int MaxTotalMegabytes = 1024;

            [JsonProperty("DataSubfolder")]
            public string DataSubfolder = "ArtExport";

            [JsonProperty("DownloadLinks")]
            public LinkSettings Links = new LinkSettings();

            [JsonProperty("Discord")]
            public DiscordSettings Discord = new DiscordSettings();
        }

        private class LinkSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("ListenPrefix (HttpListener prefix, must end with /)")]
            public string ListenPrefix = "http://*:28099/artexport/";

            [JsonProperty("PublicBaseUrl (what players open; empty = built from the prefix and server.ip)")]
            public string PublicBaseUrl = "";

            [JsonProperty("LinkMinutes")]
            public float LinkMinutes = 30f;

            [JsonProperty("MaxDownloadsPerLink (0 = no limit)")]
            public int MaxDownloads = 5;
        }

        private class DiscordSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("WebhookUrl")]
            public string WebhookUrl = "";

            [JsonProperty("MaxUploadBytes")]
            public int MaxUploadBytes = 8 * 1024 * 1024;

            [JsonProperty("Message ({player}, {steamid}, {kind}, {file})")]
            public string Message = "**{player}** exported {kind}: `{file}`";
        }

        protected override void LoadDefaultConfig()
        {
            config = new Configuration();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception("empty config");
            }
            catch (Exception ex)
            {
                PrintWarning("Config could not be read (" + ex.Message + "); using defaults.");
                config = new Configuration();
            }
            if (config.Links == null) config.Links = new LinkSettings();
            if (config.Discord == null) config.Discord = new DiscordSettings();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        #endregion

        #region Language

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You do not have permission to export art.",
                ["Cooldown"] = "Please wait {0} s before exporting again.",
                ["NothingFound"] = "Hold a photo or look at a photo, sign, canvas or frame within {0} m, then try again.",
                ["NoImage"] = "That has no picture on it yet.",
                ["NotYours"] = "That art belongs to someone else, and this server does not allow exporting it.",
                ["Blocked"] = "Export was blocked by the server.",
                ["Saved"] = "Saved {0} image(s) on the server.",
                ["SavedFile"] = "  {0} ({1} KB)",
                ["Link"] = "Download (valid {0} min): {1}",
                ["LinkConsole"] = "Links are also in your F1 console, where you can copy them.",
                ["Discord"] = "Posted to the server's Discord.",
                ["DiscordFailed"] = "Could not post to Discord.",
                ["TooLarge"] = "Skipped one image: it is larger than the server allows.",
                ["NoPhotos"] = "You have no photos or painted items in your inventory.",
                ["Help"] = "/artexport : export what you hold or look at.\n/artexport all : export every photo and painted item you carry.",
                ["Error"] = "Something went wrong while exporting. The server log has details."
            }, this);
        }

        private string Msg(string key, string userId, params object[] args)
        {
            string text = lang.GetMessage(key, this, userId);
            return args.Length > 0 ? string.Format(text, args) : text;
        }

        #endregion

        #region State

        private readonly Dictionary<ulong, float> lastExport = new Dictionary<ulong, float>();
        private readonly List<Coroutine> coroutines = new List<Coroutine>();
        private readonly List<UnityWebRequest> requests = new List<UnityWebRequest>();
        private string rootDir;
        private bool unloading;

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermOthers, this);
            permission.RegisterPermission(PermAdmin, this);
            rootDir = Path.Combine(Interface.Oxide.DataDirectory, SafeName(config.DataSubfolder, "ArtExport"));
            lookMask = LayerMask.GetMask("Construction", "Deployed", "Default", "World", "Terrain", "Tree", "Vehicle World", "Vehicle Detailed");
        }

        private void OnServerInitialized()
        {
            try { Directory.CreateDirectory(rootDir); }
            catch (Exception ex) { PrintError("Cannot create " + rootDir + ": " + ex.Message); }
            if (config.Links.Enabled) StartListener(config.Links.ListenPrefix);
        }

        private void Unload()
        {
            unloading = true;
            StopListener();
            if (ServerMgr.Instance != null)
            {
                foreach (Coroutine c in coroutines)
                {
                    if (c != null) ServerMgr.Instance.StopCoroutine(c);
                }
            }
            coroutines.Clear();
            foreach (UnityWebRequest r in requests)
            {
                try { r.Abort(); r.Dispose(); } catch { }
            }
            requests.Clear();
        }

        #endregion

        #region Finding images

        // One image found on the server: the stored bytes, untouched.
        private class Frame
        {
            public byte[] Data;
            public string Kind;
            public int Index;
            public ulong Owner;
        }

        private int lookMask;

        // Every image stored for an entity: ISignage textures (signs, frames, pumpkins, neon) or IUGCBrowserEntity content
        // (photos, picked-up sign items, painted items). Parts in FileStorage are the texture index.
        private void CollectFromEntity(BaseEntity entity, string kind, ulong owner, List<Frame> into, ref int skipped)
        {
            if (entity == null || entity.net == null) return;
            uint[] crcs = null;
            NetworkableId id = entity.net.ID;
            var ugc = entity as IUGCBrowserEntity;
            var sign = entity as ISignage;
            if (ugc != null)
            {
                if (ugc.ContentType != UGCType.ImageJpg && ugc.ContentType != UGCType.ImagePng) return;
                crcs = ugc.GetContentCRCs;
            }
            else if (sign != null)
            {
                if (sign.FileType != FileStorage.Type.png && sign.FileType != FileStorage.Type.jpg) return;
                crcs = sign.GetTextureCRCs();
                id = sign.NetworkID;
            }
            if (crcs == null) return;
            for (int i = 0; i < crcs.Length; i++)
            {
                if (crcs[i] == 0) continue;
                byte[] data = FileStorage.server.Get(id, (ulong)i, crcs[i]) ?? FileStorage.server.Get(id, (ulong)i);
                if (data == null || data.Length == 0) continue;
                if (config.MaxImageBytes > 0 && data.Length > config.MaxImageBytes) { skipped++; continue; }
                into.Add(new Frame { Data = data, Kind = kind, Index = i, Owner = owner });
            }
        }

        private static BaseEntity ItemEntity(Item item)
        {
            if (item == null || item.instanceData == null || !item.instanceData.subEntity.IsValid) return null;
            return BaseNetworkable.serverEntities.Find(item.instanceData.subEntity) as BaseEntity;
        }

        private void CollectFromItem(Item item, List<Frame> into, ref int skipped)
        {
            BaseEntity sub = ItemEntity(item);
            if (sub != null) CollectFromEntity(sub, SafeName(item.info.shortname, "item"), 0, into, ref skipped);
        }

        // A world entity: its own images, a photo hung in a photo frame, or the canvas on an easel (a child).
        private void CollectFromWorld(BaseEntity entity, List<Frame> into, ref int skipped)
        {
            string kind = SafeName(entity.ShortPrefabName, "art");
            ulong owner = entity.OwnerID;
            int before = into.Count;
            CollectFromEntity(entity, kind, owner, into, ref skipped);
            var frame = entity as PhotoFrame;
            if (frame != null && frame.inventory != null)
            {
                foreach (Item item in frame.inventory.itemList)
                {
                    BaseEntity sub = ItemEntity(item);
                    if (sub != null) CollectFromEntity(sub, "photo", owner, into, ref skipped);
                }
            }
            if (into.Count == before && entity.children != null)
            {
                foreach (BaseEntity child in entity.children)
                {
                    if (child is ISignage || child is IUGCBrowserEntity)
                        CollectFromEntity(child, SafeName(child.ShortPrefabName, "art"), child.OwnerID != 0 ? child.OwnerID : owner, into, ref skipped);
                }
            }
        }

        private static bool HasImageInterface(BaseEntity e)
        {
            return e is ISignage || e is IUGCBrowserEntity || e is PhotoFrame;
        }

        private BaseEntity LookTarget(BasePlayer player)
        {
            Ray ray = player.eyes.HeadRay();
            RaycastHit[] hits = Physics.RaycastAll(ray, config.LookRange, lookMask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit hit in hits)
            {
                BaseEntity e = hit.GetEntity();
                if (e == null) return null; // terrain or world geometry blocks the view
                if (e == player) continue;
                for (BaseEntity cur = e; cur != null; cur = cur.GetParentEntity())
                {
                    if (HasImageInterface(cur)) return cur;
                    foreach (BaseEntity child in cur.children)
                        if (child is ISignage || child is IUGCBrowserEntity) return cur; // easel holding a canvas
                }
                return null; // the first solid thing hit has no image
            }
            return null;
        }

        private static void AllItems(BasePlayer player, List<Item> into)
        {
            var top = new List<Item>();
            player.inventory.GetAllItems(top);
            var stack = new Stack<Item>(top);
            while (stack.Count > 0)
            {
                Item it = stack.Pop();
                into.Add(it);
                if (it.contents != null)
                    foreach (Item inner in it.contents.itemList) stack.Push(inner);
            }
        }

        #endregion

        #region Saving

        private class Saved
        {
            public string Path;
            public string FileName;
            public string Kind;
            public int Bytes;
            public string ContentType;
        }

        private static string Extension(byte[] d, out string contentType)
        {
            if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) { contentType = "image/png"; return "png"; }
            if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) { contentType = "image/jpeg"; return "jpg"; }
            if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') { contentType = "image/webp"; return "webp"; }
            contentType = "application/octet-stream";
            return "bin";
        }

        private static string SafeName(string s, string fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_') sb.Append(c);
                else if (c == '.' || c == ' ') sb.Append('-');
            }
            string r = sb.ToString().Trim('-');
            if (r.Length > 48) r = r.Substring(0, 48);
            return r.Length == 0 ? fallback : r;
        }

        private string PlayerDir(ulong steamId)
        {
            return Path.Combine(rootDir, steamId.ToString());
        }

        private List<Saved> SaveFrames(ulong steamId, List<Frame> frames)
        {
            var result = new List<Saved>();
            string dir = PlayerDir(steamId);
            Directory.CreateDirectory(dir);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            int n = 0;
            foreach (Frame f in frames)
            {
                n++;
                string contentType;
                string ext = Extension(f.Data, out contentType);
                string baseName = stamp + "-" + f.Kind + "-" + n;
                string name = baseName + "." + ext;
                string path = Path.Combine(dir, name);
                for (int k = 2; File.Exists(path); k++)
                {
                    name = baseName + "-" + k + "." + ext;
                    path = Path.Combine(dir, name);
                }
                File.WriteAllBytes(path, f.Data);
                result.Add(new Saved { Path = path, FileName = name, Kind = f.Kind, Bytes = f.Data.Length, ContentType = contentType });
            }
            Prune(steamId);
            return result;
        }

        // Oldest first: per player, then over everyone.
        private void Prune(ulong steamId)
        {
            try
            {
                if (config.MaxFilesPerPlayer > 0)
                {
                    var files = new DirectoryInfo(PlayerDir(steamId)).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name).ToList();
                    for (int i = 0; i < files.Count - config.MaxFilesPerPlayer; i++) files[i].Delete();
                }
                if (config.MaxTotalMegabytes > 0 && Directory.Exists(rootDir))
                {
                    long cap = (long)config.MaxTotalMegabytes * 1024 * 1024;
                    var all = new DirectoryInfo(rootDir).GetFiles("*", SearchOption.AllDirectories).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name).ToList();
                    long total = all.Sum(f => f.Length);
                    foreach (FileInfo f in all)
                    {
                        if (total <= cap) break;
                        total -= f.Length;
                        f.Delete();
                    }
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Pruning failed: " + ex.Message);
            }
        }

        #endregion

        #region Player commands

        [ChatCommand("artexport")]
        private void CmdArtExport(BasePlayer player, string command, string[] args)
        {
            Export(player, args);
        }

        [ChatCommand("saveart")]
        private void CmdSaveArt(BasePlayer player, string command, string[] args)
        {
            Export(player, args);
        }

        [ConsoleCommand("artexport")]
        private void CcmdArtExport(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                arg.ReplyWith("artexport is for players. Admin commands: artexport.list, artexport.purge, artexport.selftest");
                return;
            }
            // Read through GetString: Args is string[] on older builds and StringView[] on newer ones.
            var list = new List<string>();
            for (int i = 0; arg.HasArgs(i + 1); i++) list.Add(arg.GetString(i));
            Export(player, list.ToArray());
        }

        private void Reply(BasePlayer player, string text)
        {
            player.ChatMessage(text);
            player.ConsoleMessage("[ArtExport] " + text);
        }

        private void Export(BasePlayer player, string[] args)
        {
            string uid = player.UserIDString;
            if (args.Length > 0 && args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Reply(player, Msg("Help", uid));
                return;
            }
            if (config.RequirePermission && !permission.UserHasPermission(uid, PermUse))
            {
                Reply(player, Msg("NoPermission", uid));
                return;
            }
            float last;
            if (config.CooldownSeconds > 0 && lastExport.TryGetValue(player.userID, out last) && Time.realtimeSinceStartup - last < config.CooldownSeconds)
            {
                Reply(player, Msg("Cooldown", uid, Mathf.CeilToInt(config.CooldownSeconds - (Time.realtimeSinceStartup - last))));
                return;
            }

            var frames = new List<Frame>();
            int skipped = 0;
            BaseEntity source = null;
            try
            {
                if (args.Length > 0 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    var items = new List<Item>();
                    AllItems(player, items);
                    foreach (Item it in items) CollectFromItem(it, frames, ref skipped);
                    if (frames.Count == 0 && skipped == 0) { Reply(player, Msg("NoPhotos", uid)); return; }
                }
                else
                {
                    Item held = player.GetActiveItem();
                    if (held != null) CollectFromItem(held, frames, ref skipped);
                    if (frames.Count == 0 && skipped == 0)
                    {
                        source = LookTarget(player);
                        if (source == null) { Reply(player, Msg("NothingFound", uid, config.LookRange)); return; }
                        CollectFromWorld(source, frames, ref skipped);
                        if (frames.Count == 0 && skipped == 0) { Reply(player, Msg("NoImage", uid)); return; }
                    }
                }

                bool othersOk = config.AllowOthersArt || permission.UserHasPermission(uid, PermOthers);
                if (!othersOk && frames.Any(f => f.Owner != 0 && f.Owner != player.userID))
                {
                    Reply(player, Msg("NotYours", uid));
                    return;
                }
                if (Interface.CallHook("CanArtExport", player, source) != null)
                {
                    Reply(player, Msg("Blocked", uid));
                    return;
                }

                lastExport[player.userID] = Time.realtimeSinceStartup;
                if (skipped > 0) Reply(player, Msg("TooLarge", uid));
                if (frames.Count == 0) return;

                List<Saved> saved = SaveFrames(player.userID, frames);
                var lines = new StringBuilder(Msg("Saved", uid, saved.Count));
                foreach (Saved s in saved) lines.Append('\n').Append(Msg("SavedFile", uid, s.FileName, Math.Max(1, s.Bytes / 1024)));
                Reply(player, lines.ToString());

                if (config.Links.Enabled && listener != null)
                {
                    foreach (Saved s in saved)
                    {
                        string url = IssueLink(s, DateTime.UtcNow.AddMinutes(config.Links.LinkMinutes));
                        Reply(player, Msg("Link", uid, config.Links.LinkMinutes, url));
                    }
                    player.ChatMessage(Msg("LinkConsole", uid));
                }
                if (config.Discord.Enabled && !string.IsNullOrEmpty(config.Discord.WebhookUrl))
                {
                    foreach (Saved s in saved) PostToDiscord(player, s, frames[saved.IndexOf(s)].Data);
                }
                Puts(player.displayName + " (" + uid + ") exported " + saved.Count + " image(s): " + string.Join(", ", saved.Select(s => s.FileName).ToArray()));
                Interface.CallHook("OnArtExported", player, saved.Select(s => s.Path).ToList());
            }
            catch (Exception ex)
            {
                PrintError("Export failed for " + uid + ": " + ex);
                Reply(player, Msg("Error", uid));
            }
        }

        #endregion

        #region Admin commands

        private static bool IsAdminArg(ConsoleSystem.Arg arg, ArtExport self)
        {
            if (arg.Connection == null) return true; // server console or RCON
            BasePlayer p = arg.Player();
            return p != null && (p.IsAdmin || self.permission.UserHasPermission(p.UserIDString, PermAdmin));
        }

        [ConsoleCommand("artexport.list")]
        private void CcmdList(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg, this)) { arg.ReplyWith("No permission."); return; }
            if (!Directory.Exists(rootDir)) { arg.ReplyWith("Nothing exported yet."); return; }
            var sb = new StringBuilder();
            string only = arg.GetString(0, "");
            foreach (string dir in Directory.GetDirectories(rootDir).OrderBy(d => d))
            {
                string id = Path.GetFileName(dir);
                if (only.Length > 0 && id != only) continue;
                var files = new DirectoryInfo(dir).GetFiles().OrderBy(f => f.Name).ToList();
                sb.AppendLine(id + ": " + files.Count + " file(s), " + (files.Sum(f => f.Length) / 1024) + " KB");
                if (only.Length > 0)
                    foreach (FileInfo f in files) sb.AppendLine("  " + f.Name + "  " + (f.Length / 1024) + " KB");
            }
            arg.ReplyWith(sb.Length == 0 ? "Nothing exported yet." : sb.ToString().TrimEnd());
        }

        [ConsoleCommand("artexport.purge")]
        private void CcmdPurge(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg, this)) { arg.ReplyWith("No permission."); return; }
            string what = arg.GetString(0, "");
            ulong id;
            if (what == "all")
            {
                int n = 0;
                if (Directory.Exists(rootDir))
                    foreach (string dir in Directory.GetDirectories(rootDir)) { n += Directory.GetFiles(dir).Length; Directory.Delete(dir, true); }
                lock (tokens) tokens.Clear();
                arg.ReplyWith("Deleted " + n + " exported file(s).");
            }
            else if (ulong.TryParse(what, out id))
            {
                string dir = PlayerDir(id);
                int n = Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;
                if (n > 0) Directory.Delete(dir, true);
                arg.ReplyWith("Deleted " + n + " exported file(s) of " + id + ".");
            }
            else arg.ReplyWith("Usage: artexport.purge <steamid|all>");
        }

        #endregion

        #region Download links

        private class LinkToken
        {
            public string Path;
            public string FileName;
            public string ContentType;
            public DateTime Expires;
            public int Uses;
        }

        private readonly Dictionary<string, LinkToken> tokens = new Dictionary<string, LinkToken>();
        private HttpListener listener;
        private string listenPath = "/";

        private static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(32);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private string IssueLink(Saved s, DateTime expires)
        {
            string token = NewToken();
            lock (tokens)
            {
                DateTime now = DateTime.UtcNow;
                foreach (string k in tokens.Where(kv => kv.Value.Expires < now).Select(kv => kv.Key).ToList()) tokens.Remove(k);
                tokens[token] = new LinkToken { Path = s.Path, FileName = s.FileName, ContentType = s.ContentType, Expires = expires };
            }
            return PublicBase() + token + "/" + s.FileName;
        }

        private string PublicBase()
        {
            string b = config.Links.PublicBaseUrl;
            if (string.IsNullOrEmpty(b))
            {
                string host = string.IsNullOrEmpty(ConVar.Server.ip) || ConVar.Server.ip == "0.0.0.0" ? "127.0.0.1" : ConVar.Server.ip;
                b = config.Links.ListenPrefix.Replace("://*", "://" + host).Replace("://+", "://" + host);
            }
            return b.EndsWith("/") ? b : b + "/";
        }

        private bool StartListener(string prefix)
        {
            if (listener != null) return true;
            if (!HttpListener.IsSupported) { PrintError("HttpListener is not supported here; download links are off."); return false; }
            if (string.IsNullOrEmpty(prefix) || !prefix.EndsWith("/")) { PrintError("ListenPrefix must end with '/'."); return false; }
            try
            {
                var uri = new Uri(prefix.Replace("://*", "://localhost").Replace("://+", "://localhost"));
                listenPath = uri.AbsolutePath;
                var l = new HttpListener();
                l.Prefixes.Add(prefix);
                l.Start();
                listener = l;
                l.BeginGetContext(OnContext, l);
                Puts("Download links served on " + prefix);
                return true;
            }
            catch (Exception ex)
            {
                PrintError("Could not start the download listener on " + prefix + ": " + ex.Message);
                listener = null;
                return false;
            }
        }

        private void StopListener()
        {
            HttpListener l = listener;
            listener = null;
            if (l == null) return;
            try { l.Stop(); l.Close(); } catch { }
        }

        // Runs on a thread-pool thread: touches only the token table (locked) and the file named by a token.
        private void OnContext(IAsyncResult ar)
        {
            var l = (HttpListener)ar.AsyncState;
            HttpListenerContext ctx;
            try { ctx = l.EndGetContext(ar); }
            catch { return; }
            try { if (!unloading && listener == l) l.BeginGetContext(OnContext, l); } catch { }
            try { Serve(ctx); }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        private void Serve(HttpListenerContext ctx)
        {
            HttpListenerResponse res = ctx.Response;
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["Referrer-Policy"] = "no-referrer";
            string method = ctx.Request.HttpMethod;
            if (method != "GET" && method != "HEAD") { End(res, 405); return; }

            // The token is the first path segment after the prefix; nothing from the request is used as a path.
            string path = ctx.Request.Url.AbsolutePath;
            string rest = path.StartsWith(listenPath) ? path.Substring(listenPath.Length) : "";
            int slash = rest.IndexOf('/');
            string token = slash >= 0 ? rest.Substring(0, slash) : rest;
            if (token.Length != 32 || !token.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) { End(res, 404); return; }

            LinkToken t;
            lock (tokens)
            {
                if (!tokens.TryGetValue(token, out t) || t.Expires < DateTime.UtcNow) { tokens.Remove(token); End(res, 404); return; }
                if (method == "GET")
                {
                    t.Uses++;
                    if (config.Links.MaxDownloads > 0 && t.Uses >= config.Links.MaxDownloads) tokens.Remove(token);
                }
            }
            if (!File.Exists(t.Path)) { End(res, 404); return; }
            byte[] data = File.ReadAllBytes(t.Path);
            res.StatusCode = 200;
            res.ContentType = t.ContentType;
            res.ContentLength64 = data.Length;
            res.Headers["Content-Disposition"] = "attachment; filename=\"" + t.FileName + "\"";
            if (method == "GET") res.OutputStream.Write(data, 0, data.Length);
            res.OutputStream.Close();
        }

        private static void End(HttpListenerResponse res, int code)
        {
            res.StatusCode = code;
            res.ContentLength64 = 0;
            res.OutputStream.Close();
        }

        #endregion

        #region Discord

        private void PostToDiscord(BasePlayer player, Saved s, byte[] data)
        {
            if (data.Length > config.Discord.MaxUploadBytes)
            {
                Reply(player, Msg("TooLarge", player.UserIDString));
                return;
            }
            string text = config.Discord.Message
                .Replace("{player}", player.displayName.Replace("`", "'").Replace("*", "").Replace("_", "\\_"))
                .Replace("{steamid}", player.UserIDString)
                .Replace("{kind}", s.Kind)
                .Replace("{file}", s.FileName);
            string payload = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["content"] = text,
                ["allowed_mentions"] = new Dictionary<string, object> { ["parse"] = new string[0] }
            });
            ulong userId = player.userID;
            Track(ServerMgr.Instance.StartCoroutine(DiscordUpload(userId, payload, s.FileName, s.ContentType, data)));
        }

        private void Track(Coroutine c)
        {
            coroutines.RemoveAll(x => x == null);
            coroutines.Add(c);
        }

        private IEnumerator DiscordUpload(ulong userId, string payload, string fileName, string contentType, byte[] data)
        {
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("payload_json", Encoding.UTF8.GetBytes(payload), "application/json"),
                new MultipartFormFileSection("files[0]", data, fileName, contentType)
            };
            UnityWebRequest req = UnityWebRequest.Post(config.Discord.WebhookUrl, form);
            req.timeout = 60;
            requests.Add(req);
            yield return req.SendWebRequest();
            if (unloading) yield break;
            requests.Remove(req);
            bool ok = req.result == UnityWebRequest.Result.Success && req.responseCode >= 200 && req.responseCode < 300;
            if (!ok) PrintWarning("Discord upload of " + fileName + " failed: " + req.responseCode + " " + req.error);
            req.Dispose();
            BasePlayer p = BasePlayer.FindByID(userId);
            if (p != null) Reply(p, Msg(ok ? "Discord" : "DiscordFailed", p.UserIDString));
        }

        #endregion

        #region Self test (server console / RCON only)

        private const string SignPrefab = "assets/prefabs/deployable/signs/sign.small.wood.prefab";
        private const string PhotoPrefab = "assets/prefabs/misc/summer_dlc/photo/photo.entity.prefab";

        [ConsoleCommand("artexport.selftest")]
        private void CcmdSelfTest(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { arg.ReplyWith("artexport.selftest runs from the server console or RCON only."); return; }
            bool forceHttp = arg.GetString(0, "") == "http";
            var log = new List<string>();
            bool pass = true;
            Action<bool, string> check = (ok, what) => { log.Add((ok ? "PASS " : "FAIL ") + what); if (!ok) pass = false; };
            const ulong fakeId = 76561190000000042UL;
            BaseEntity sign = null, photo = null;
            Item photoItem = null;
            var saved = new List<Saved>();
            try
            {
                // 1. A sign with a generated PNG as its texture, exported through the world-entity path.
                byte[] png = TestPng(97, 61);
                sign = GameManager.server.CreateEntity(SignPrefab, new Vector3(0f, 950f, 0f));
                sign.OwnerID = fakeId;
                sign.Spawn();
                var signage = (Signage)sign;
                uint crc = FileStorage.server.Store(sign.net.ID, 0UL, FileStorage.Type.png, png);
                signage.RefreshFileCrcs();
                check(signage.GetTextureCRCs()[0] == crc, "sign texture CRC set (" + crc + ")");
                var frames = new List<Frame>();
                int skipped = 0;
                CollectFromWorld(sign, frames, ref skipped);
                check(frames.Count == 1, "sign gives one image (" + frames.Count + ")");
                List<Saved> s1 = SaveFrames(fakeId, frames);
                saved.AddRange(s1);
                check(s1.Count == 1 && s1[0].FileName.EndsWith(".png"), "saved as .png (" + (s1.Count > 0 ? s1[0].FileName : "none") + ")");
                check(s1.Count == 1 && File.ReadAllBytes(s1[0].Path).SequenceEqual(png), "saved PNG bytes identical to stored (" + png.Length + " bytes)");

                // 2. A photo item (JPEG in a PhotoEntity), exported through the held-item path.
                byte[] jpg = TestJpeg();
                photoItem = ItemManager.CreateByName("photo", 1);
                photo = ItemEntity(photoItem);
                if (photo == null)
                {
                    photo = GameManager.server.CreateEntity(PhotoPrefab, Vector3.zero);
                    photo.Spawn();
                    photoItem.instanceData = new ProtoBuf.Item.InstanceData { ShouldPool = false, subEntity = photo.net.ID };
                }
                ((PhotoEntity)photo).SetImageData(fakeId, jpg);
                frames.Clear();
                CollectFromItem(photoItem, frames, ref skipped);
                check(frames.Count == 1, "photo item gives one image (" + frames.Count + ")");
                List<Saved> s2 = SaveFrames(fakeId, frames);
                saved.AddRange(s2);
                check(s2.Count == 1 && s2[0].FileName.EndsWith(".jpg"), "saved as .jpg (" + (s2.Count > 0 ? s2[0].FileName : "none") + ")");
                check(s2.Count == 1 && File.ReadAllBytes(s2[0].Path).SequenceEqual(jpg), "saved JPEG bytes identical to stored (" + jpg.Length + " bytes)");

                // 3. Size limit.
                int oldMax = config.MaxImageBytes;
                config.MaxImageBytes = 100;
                frames.Clear(); skipped = 0;
                CollectFromWorld(sign, frames, ref skipped);
                config.MaxImageBytes = oldMax;
                check(frames.Count == 0 && skipped == 1, "image over MaxImageBytes is skipped");

                // 4. Per-player pruning keeps the newest files.
                int oldCap = config.MaxFilesPerPlayer;
                config.MaxFilesPerPlayer = 1;
                Prune(fakeId);
                config.MaxFilesPerPlayer = oldCap;
                string[] left = Directory.GetFiles(PlayerDir(fakeId));
                check(left.Length == 1, "pruning to 1 file per player leaves " + left.Length);
            }
            catch (Exception ex)
            {
                check(false, "exception: " + ex);
            }
            finally
            {
                if (photoItem != null) { photoItem.Remove(); ItemManager.DoRemoves(); }
                if (photo != null && !photo.IsDestroyed) photo.Kill();
                if (sign != null && !sign.IsDestroyed) sign.Kill();
            }

            // 5. HTTP download links, when the listener is on (or with "artexport.selftest http").
            bool tempListener = false;
            if (listener == null && forceHttp) tempListener = StartListener(config.Links.ListenPrefix);
            Saved target = saved.FirstOrDefault(x => File.Exists(x.Path));
            if (listener != null && target != null)
            {
                Track(ServerMgr.Instance.StartCoroutine(HttpSelfTest(target, log, pass, tempListener, fakeId)));
                arg.ReplyWith("[ArtExport selftest]\n" + string.Join("\n", log.ToArray()) + "\nHTTP checks running; results follow in the server log.");
                return;
            }
            if (listener == null) log.Add("SKIP HTTP (download links off; run 'artexport.selftest http' to test them)");
            Finish(log, pass, fakeId);
            arg.ReplyWith("[ArtExport selftest]\n" + string.Join("\n", log.ToArray()));
        }

        private void Finish(List<string> log, bool pass, ulong fakeId)
        {
            try { if (Directory.Exists(PlayerDir(fakeId))) Directory.Delete(PlayerDir(fakeId), true); } catch { }
            log.Add(pass ? "SELFTEST PASSED" : "SELFTEST FAILED");
            Puts("Self test:\n" + string.Join("\n", log.ToArray()));
        }

        private IEnumerator HttpSelfTest(Saved target, List<string> log, bool pass, bool tempListener, ulong fakeId)
        {
            byte[] expected = File.ReadAllBytes(target.Path);
            string local = config.Links.ListenPrefix.Replace("://*", "://127.0.0.1").Replace("://+", "://127.0.0.1");
            if (!local.EndsWith("/")) local += "/";
            string good = NewToken(), expired = NewToken();
            lock (tokens)
            {
                tokens[good] = new LinkToken { Path = target.Path, FileName = target.FileName, ContentType = target.ContentType, Expires = DateTime.UtcNow.AddMinutes(5) };
                tokens[expired] = new LinkToken { Path = target.Path, FileName = target.FileName, ContentType = target.ContentType, Expires = DateTime.UtcNow.AddSeconds(-1) };
            }
            var cases = new[]
            {
                new { Name = "valid token", Url = local + good + "/" + target.FileName, Code = 200L, Body = true },
                new { Name = "unknown token", Url = local + NewToken() + "/" + target.FileName, Code = 404L, Body = false },
                new { Name = "expired token", Url = local + expired + "/" + target.FileName, Code = 404L, Body = false },
                new { Name = "path traversal (any 4xx)", Url = local + "..%2f..%2fconfig/x", Code = 400L, Body = false },
            };
            foreach (var c in cases)
            {
                UnityWebRequest req = UnityWebRequest.Get(c.Url);
                req.timeout = 10;
                requests.Add(req);
                yield return req.SendWebRequest();
                if (unloading) yield break;
                requests.Remove(req);
                long code = req.responseCode;
                // A traversal attempt passes on any 4xx: the listener may reject it before the plugin sees it.
                bool ok = c.Code == 400L ? code >= 400 && code < 500 : code == c.Code;
                string extra = "";
                if (ok && c.Body)
                {
                    byte[] got = req.downloadHandler.data;
                    ok = got != null && got.SequenceEqual(expected);
                    extra = ", " + (got == null ? 0 : got.Length) + " bytes " + (ok ? "identical" : "DIFFERENT") + ", type " + req.GetResponseHeader("Content-Type");
                }
                log.Add((ok ? "PASS " : "FAIL ") + "HTTP " + c.Name + " -> " + code + extra);
                if (!ok) pass = false;
                req.Dispose();
            }
            lock (tokens) { tokens.Remove(good); tokens.Remove(expired); }
            if (tempListener) StopListener();
            Finish(log, pass, fakeId);
        }

        // A small RGB PNG (stored deflate blocks), so the test needs nothing from Unity's image code.
        private static byte[] TestPng(int w, int h)
        {
            var raw = new byte[(w * 3 + 1) * h];
            int p = 0;
            for (int y = 0; y < h; y++)
            {
                raw[p++] = 0;
                for (int x = 0; x < w; x++) { raw[p++] = (byte)(x * 255 / w); raw[p++] = (byte)(y * 255 / h); raw[p++] = (byte)((x ^ y) * 7); }
            }
            var z = new MemoryStream();
            z.WriteByte(0x78); z.WriteByte(0x01);
            for (int off = 0; off < raw.Length; off += 65535)
            {
                int len = Math.Min(65535, raw.Length - off);
                z.WriteByte((byte)(off + len >= raw.Length ? 1 : 0));
                z.WriteByte((byte)len); z.WriteByte((byte)(len >> 8));
                z.WriteByte((byte)~len); z.WriteByte((byte)(~len >> 8));
                z.Write(raw, off, len);
            }
            uint a = 1, b = 0;
            foreach (byte v in raw) { a = (a + v) % 65521; b = (b + a) % 65521; }
            uint adler = (b << 16) | a;
            z.WriteByte((byte)(adler >> 24)); z.WriteByte((byte)(adler >> 16)); z.WriteByte((byte)(adler >> 8)); z.WriteByte((byte)adler);

            var png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
            ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
            ihdr[8] = 8; ihdr[9] = 2;
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", z.ToArray());
            Chunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            byte[] t = Encoding.ASCII.GetBytes(type);
            s.WriteByte((byte)(data.Length >> 24)); s.WriteByte((byte)(data.Length >> 16)); s.WriteByte((byte)(data.Length >> 8)); s.WriteByte((byte)data.Length);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint c = 0xFFFFFFFF;
            foreach (byte v in t.Concat(data))
            {
                c ^= v;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            c ^= 0xFFFFFFFF;
            s.WriteByte((byte)(c >> 24)); s.WriteByte((byte)(c >> 16)); s.WriteByte((byte)(c >> 8)); s.WriteByte((byte)c);
        }

        // Bytes that start and end like a JPEG. The plugin never decodes images, so this is enough to test the path.
        private static byte[] TestJpeg()
        {
            var d = new byte[4096];
            new System.Random(7).NextBytes(d);
            d[0] = 0xFF; d[1] = 0xD8; d[2] = 0xFF; d[3] = 0xE0;
            d[d.Length - 2] = 0xFF; d[d.Length - 1] = 0xD9;
            return d;
        }

        #endregion
    }
}
