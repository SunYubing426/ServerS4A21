using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DfoServer.Infrastructure
{
    /// 网关准入模式下 LOGIN 必须消费一次性 ticket，失败则关闭连接。
    public static class GatewayAdmission
    {
        private static readonly HttpClient Http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static bool Enabled { get; private set; }

        private static string _introspectUrl = "";
        private static string _internalKey = "";

        public static void ConfigureFromEnvironment()
        {
            Enabled = ReadBool("DFO_GATEWAY_MODE");
            if (!Enabled)
                return;

            _introspectUrl = (Environment.GetEnvironmentVariable("DFO_GATEWAY_INTROSPECT_URL") ?? "").Trim();
            _internalKey = (Environment.GetEnvironmentVariable("DFO_GATEWAY_INTERNAL_KEY") ?? "").Trim();
            if (string.IsNullOrEmpty(_introspectUrl) || string.IsNullOrEmpty(_internalKey))
            {
                throw new InvalidOperationException(
                    "DFO_GATEWAY_MODE=1 requires DFO_GATEWAY_INTROSPECT_URL and DFO_GATEWAY_INTERNAL_KEY.");
            }

            FileLogger.Log(
                $"[GatewayAdmission] enabled introspect={_introspectUrl}");
        }

        public static async Task<(bool ok, string mid)> TryConsumeTicketAsync(string mid, string ticket)
        {
            mid = (mid ?? "").Trim();
            ticket = (ticket ?? "").Trim();
            if (!Enabled || mid.Length == 0 || ticket.Length == 0)
                return (false, "");

            try
            {
                var payload = JsonSerializer.Serialize(
                    new IntrospectRequest { Mid = mid, Ticket = ticket },
                    JsonOptions);
                using var req = new HttpRequestMessage(HttpMethod.Post, _introspectUrl);
                req.Headers.TryAddWithoutValidation("X-Internal-Key", _internalKey);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    FileLogger.Log(
                        $"[GatewayAdmission] introspect http={(int)resp.StatusCode} mid={mid}");
                    return (false, "");
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var body = JsonSerializer.Deserialize<IntrospectResponse>(json, JsonOptions);
                var bound = (body?.Mid ?? "").Trim();
                var ok = body != null && body.Ok && bound.Length > 0
                    && string.Equals(bound, mid, StringComparison.Ordinal);
                FileLogger.Log(
                    $"[GatewayAdmission] introspect ok={ok} mid={mid}");
                return ok ? (true, bound) : (false, "");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GatewayAdmission] introspect FAILED mid={mid} err={ex.Message}");
                return (false, "");
            }
        }

        private static bool ReadBool(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
                default:
                    return false;
            }
        }

        private sealed class IntrospectRequest
        {
            public string Mid { get; set; }
            public string Ticket { get; set; }
        }

        private sealed class IntrospectResponse
        {
            public bool Ok { get; set; }
            public string Mid { get; set; }
        }
    }
}
