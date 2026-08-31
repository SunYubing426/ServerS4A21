using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Session;
using DfoServer.Network;

namespace DfoServer.Infrastructure
{
    /// 踢人与停服：关闭匹配连接后写档。
    public static class GatewayAdmin
    {
        public const int DefaultPort = 61004;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static TcpListener _listener;
        private static CancellationTokenSource _cts;
        private static MultiStructureTcpServer _server;

        public static void Start(MultiStructureTcpServer server)
        {
            if (server == null)
                throw new ArgumentNullException(nameof(server));
            _server = server;

            var prefix = (Environment.GetEnvironmentVariable("DFO_GATEWAY_ADMIN_LISTEN") ?? "").Trim();
            if (prefix.Length == 0)
                prefix = "http://127.0.0.1:" + DefaultPort + "/";
            if (!TryLoopbackPort(prefix, out var port))
            {
                throw new InvalidOperationException(
                    "DFO_GATEWAY_ADMIN_LISTEN must be a loopback HTTP URL.");
            }

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = AcceptLoop(_cts.Token);
            FileLogger.Log($"[GatewayAdmin] listen=127.0.0.1:{port}");
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); }
            catch { }
            try { _listener?.Stop(); }
            catch { }
            _cts = null;
            _listener = null;
            _server = null;
        }

        public static int CloseMatching(IEnumerable<EnhancedClientSession> clients, string mid, bool all)
        {
            mid = (mid ?? "").Trim();
            var n = 0;
            foreach (var session in clients)
            {
                if (!all && !MidEquals(session.Account?.MId, mid))
                    continue;
                try
                {
                    session.Close();
                    n++;
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[GatewayAdmin] close failed: {ex.Message}");
                }
            }
            return n;
        }

        internal static bool MidEquals(string bound, string mid)
        {
            bound = (bound ?? "").Trim();
            mid = (mid ?? "").Trim();
            return bound.Length > 0 && mid.Length > 0
                && string.Equals(bound, mid, StringComparison.Ordinal);
        }

        private static async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    if (token.IsCancellationRequested)
                        return;
                    continue;
                }

                _ = Task.Run(() => Serve(client));
            }
        }

        private static void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 3000;
                    client.SendTimeout = 3000;
                    var stream = client.GetStream();
                    if (!TryReadRequest(stream, out var method, out var path, out var key, out var body))
                    {
                        Write(stream, 400, "{\"ok\":false}");
                        return;
                    }

                    var want = ReadAdminKey();
                    if (want.Length == 0 || !FixedEquals(key, want))
                    {
                        Write(stream, 401, "{\"ok\":false}");
                        return;
                    }

                    if ((method == "GET" || method == "POST") && path == "/internal/v1/sessions")
                    {
                        Write(stream, 200, SessionListJSON());
                        return;
                    }

                    if (method == "POST" && path == "/internal/v1/sessions/kick")
                    {
                        var req = JsonSerializer.Deserialize<KickRequest>(body ?? "{}", JsonOptions)
                            ?? new KickRequest();
                        var n = CloseMatching(_server.SnapshotClients(), req.Mid, req.All);
                        FileLogger.Log($"[GatewayAdmin] kick all={req.All} mid={req.Mid} n={n}");
                        Write(stream, 200, "{\"ok\":true,\"kicked\":" + n + "}");
                        return;
                    }

                    if (method == "POST" && path == "/internal/v1/shutdown")
                    {
                        var n = CloseMatching(_server.SnapshotClients(), "", true);
                        FileLogger.Log($"[GatewayAdmin] shutdown kicked={n}");
                        Write(stream, 200, "{\"ok\":true,\"kicked\":" + n + "}");
                        WaitUntilDrained(TimeSpan.FromSeconds(8));
                        try
                        {
                            DfoServer.Game.Inventory.InventoryPersistenceService.SaveAllDirty();
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log($"[GatewayAdmin] shutdown save failed: {ex.Message}");
                        }
                        Environment.Exit(0);
                        return;
                    }

                    Write(stream, 404, "{\"ok\":false}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[GatewayAdmin] request failed: {ex.Message}");
                }
            }
        }

        private static bool TryReadRequest(
            NetworkStream stream,
            out string method,
            out string path,
            out string key,
            out string body)
        {
            method = "";
            path = "";
            key = "";
            body = "";
            var raw = new MemoryStream();
            var buf = new byte[1024];
            var headerEnd = -1;
            while (raw.Length < 16384)
            {
                var n = stream.Read(buf, 0, buf.Length);
                if (n <= 0)
                    return false;
                raw.Write(buf, 0, n);
                headerEnd = IndexOfHeaderEnd(raw.GetBuffer(), (int)raw.Length);
                if (headerEnd >= 0)
                    break;
            }
            if (headerEnd < 0)
                return false;

            var header = Encoding.ASCII.GetString(raw.GetBuffer(), 0, headerEnd);
            var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                return false;
            var parts = lines[0].Split(' ');
            if (parts.Length < 2)
                return false;
            method = parts[0].Trim().ToUpperInvariant();
            path = parts[1].Trim();
            var q = path.IndexOf('?');
            if (q >= 0)
                path = path.Substring(0, q);

            var contentLength = 0;
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                if (name.Equals("X-Internal-Key", StringComparison.OrdinalIgnoreCase))
                    key = value;
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out contentLength);
            }
            if (contentLength < 0 || contentLength > 1 << 20)
                return false;

            var have = (int)raw.Length - headerEnd;
            while (have < contentLength)
            {
                var n = stream.Read(buf, 0, Math.Min(buf.Length, contentLength - have));
                if (n <= 0)
                    return false;
                raw.Write(buf, 0, n);
                have += n;
            }
            if (contentLength > 0)
                body = Encoding.UTF8.GetString(raw.GetBuffer(), headerEnd, contentLength);
            return true;
        }

        private static int IndexOfHeaderEnd(byte[] data, int length)
        {
            for (var i = 0; i + 3 < length; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i + 4;
            }
            return -1;
        }

        private static void Write(NetworkStream stream, int status, string json)
        {
            var reason = status == 200 ? "OK" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : "Bad Request";
            var payload = Encoding.UTF8.GetBytes(json);
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: " + payload.Length + "\r\n" +
                "Connection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static bool TryLoopbackPort(string value, out int port)
        {
            port = 0;
            if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttp
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }
            if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                && !(IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip)))
            {
                return false;
            }
            port = uri.IsDefaultPort ? 80 : uri.Port;
            return port > 0 && port < 65536;
        }

        private static string SessionListJSON()
        {
            var mids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<SessionRow>();
            var clients = _server != null
                ? _server.SnapshotClients()
                : (IReadOnlyList<EnhancedClientSession>)new EnhancedClientSession[0];
            foreach (var session in clients)
            {
                var row = Describe(session);
                if (row == null)
                    continue;
                rows.Add(row);
                if (seen.Add(row.Mid))
                    mids.Add(row.Mid);
            }
            return JsonSerializer.Serialize(new SessionListReply
            {
                Ok = true,
                Mids = mids,
                Connections = rows.Count,
                Sessions = rows
            }, JsonOptions);
        }

        private static SessionRow Describe(EnhancedClientSession session)
        {
            var mid = (session?.Account?.MId ?? "").Trim();
            if (mid.Length == 0)
                return null;

            var player = session.Player;
            var run = player?.CurrentRun;
            var channel = 0;
            if (GameNetworkConfig.TryResolveGameChannel(session.ListenerPort, out var ch))
                channel = ch.ChannelId;

            var place = "login";
            var dungeonId = 0;
            var difficulty = 0;
            if (run != null && run.DungeonId != 0)
            {
                place = "dungeon";
                dungeonId = run.DungeonId;
                difficulty = run.Difficulty;
            }
            else if (player != null && player.UserState == 0x02)
            {
                place = "pvp";
            }
            else if (player != null && player.CharacterId > 0)
            {
                place = "town";
            }

            return new SessionRow
            {
                Mid = mid,
                CharacterId = player?.CharacterId ?? 0,
                Character = ReadName(player),
                Level = player?.Level ?? 0,
                Job = player?.Job ?? 0,
                Channel = channel,
                Place = place,
                TownId = player?.CurTownId ?? 0,
                AreaId = player?.CurAreaId ?? 0,
                DungeonId = dungeonId,
                Difficulty = difficulty
            };
        }

        private static string ReadName(PlayerContext player)
        {
            if (player?.Name == null || player.Name.Length == 0)
                return "";
            try
            {
                return ClientTextEncoding.GetString(player.Name).Trim();
            }
            catch
            {
                return "";
            }
        }

        private static void WaitUntilDrained(TimeSpan limit)
        {
            var deadline = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < deadline)
            {
                if (_server.SnapshotClients().Count == 0)
                    return;
                Thread.Sleep(50);
            }
            FileLogger.Log(
                $"[GatewayAdmin] shutdown wait timeout remaining={_server.SnapshotClients().Count}");
        }

        private static string ReadAdminKey()
        {
            var path = (Environment.GetEnvironmentVariable("DFO_GATEWAY_INTERNAL_KEY_FILE") ?? "").Trim();
            if (path.Length == 0)
                return "";
            try
            {
                return File.ReadAllText(Path.GetFullPath(path)).Trim().ToLowerInvariant();
            }
            catch
            {
                return "";
            }
        }

        private static bool FixedEquals(string a, string b)
        {
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (a.Length != b.Length)
                return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private sealed class KickRequest
        {
            public string Mid { get; set; }
            public bool All { get; set; }
        }

        private sealed class SessionListReply
        {
            public bool Ok { get; set; }
            public List<string> Mids { get; set; }
            public int Connections { get; set; }
            public List<SessionRow> Sessions { get; set; }
        }

        private sealed class SessionRow
        {
            public string Mid { get; set; }
            public int CharacterId { get; set; }
            public string Character { get; set; }
            public int Level { get; set; }
            public int Job { get; set; }
            public int Channel { get; set; }
            public string Place { get; set; }
            public int TownId { get; set; }
            public int AreaId { get; set; }
            public int DungeonId { get; set; }
            public int Difficulty { get; set; }
        }
    }
}
