using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DfoServer.Infrastructure
{
    /// 网关准入模式下 LOGIN 必须消费一次性 ticket，失败则关闭连接。
    public static class GatewayAdmission
    {
        private static HttpClient Http = CreateHttpClient("");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static bool Enabled { get; private set; }

        private static string _introspectUrl = "";
        private static string _internalKeyFile = "";

        public static void ConfigureFromEnvironment()
        {
            Enabled = ResolveMode(
                Environment.GetEnvironmentVariable("DFO_GATEWAY_MODE"),
                Environment.GetEnvironmentVariable(
                    "DFO_GATEWAY_ALLOW_LEGACY_LOGIN"));
            if (!Enabled)
            {
                FileLogger.Log(
                    "[GatewayAdmission] WARNING legacy LOGIN explicitly enabled");
                return;
            }

            _introspectUrl = (Environment.GetEnvironmentVariable("DFO_GATEWAY_INTROSPECT_URL") ?? "").Trim();
            var keyFile = (Environment.GetEnvironmentVariable(
                "DFO_GATEWAY_INTERNAL_KEY_FILE") ?? "").Trim();
            var legacyInlineKey = (Environment.GetEnvironmentVariable(
                "DFO_GATEWAY_INTERNAL_KEY") ?? "").Trim();
            var caFile = (Environment.GetEnvironmentVariable(
                "DFO_GATEWAY_INTROSPECT_CA_FILE") ?? "").Trim();
            if (!IsSafeIntrospectUrl(_introspectUrl)
                || string.IsNullOrEmpty(keyFile)
                || !string.IsNullOrEmpty(legacyInlineKey))
            {
                throw new InvalidOperationException(
                    "DFO_GATEWAY_MODE=1 requires a safe DFO_GATEWAY_INTROSPECT_URL " +
                    "and DFO_GATEWAY_INTERNAL_KEY_FILE. Inline keys are not accepted. " +
                    "Plain HTTP is allowed only for a loopback endpoint.");
            }

            _internalKeyFile = Path.GetFullPath(keyFile);
            _ = ReadInternalKeyFile(_internalKeyFile);
            if (!Uri.TryCreate(_introspectUrl, UriKind.Absolute, out var uri))
                throw new InvalidOperationException("Invalid gateway introspection URL.");
            if (uri.Scheme != Uri.UriSchemeHttps && !string.IsNullOrEmpty(caFile))
            {
                throw new InvalidOperationException(
                    "DFO_GATEWAY_INTROSPECT_CA_FILE requires an HTTPS introspection URL.");
            }
            Http = CreateHttpClient(caFile);

            FileLogger.Log(
                $"[GatewayAdmission] enabled introspect={_introspectUrl} " +
                $"keyFile={_internalKeyFile}");
        }

        private static bool ResolveMode(
            string gatewayModeValue,
            string allowLegacyValue)
        {
            var gatewayMode = ParseSwitch(
                "DFO_GATEWAY_MODE",
                gatewayModeValue);
            var allowLegacy = ParseSwitch(
                "DFO_GATEWAY_ALLOW_LEGACY_LOGIN",
                allowLegacyValue);
            if (gatewayMode == true && allowLegacy == true)
            {
                throw new InvalidOperationException(
                    "DFO_GATEWAY_MODE and DFO_GATEWAY_ALLOW_LEGACY_LOGIN cannot both be enabled.");
            }
            if (gatewayMode != true && allowLegacy != true)
            {
                throw new InvalidOperationException(
                    "Explicit login admission mode required: set DFO_GATEWAY_MODE=1, " +
                    "or set DFO_GATEWAY_ALLOW_LEGACY_LOGIN=1 for local development only.");
            }

            return gatewayMode == true;
        }

        public static async Task<(bool ok, string mid)> TryConsumeTicketAsync(string mid, string ticket)
        {
            mid = (mid ?? "").Trim();
            ticket = (ticket ?? "").Trim();
            if (!Enabled || mid.Length == 0 || ticket.Length == 0)
                return (false, "");

            try
            {
                var internalKey = ReadInternalKeyFile(_internalKeyFile);
                var payload = JsonSerializer.Serialize(
                    new IntrospectRequest { Mid = mid, Ticket = ticket },
                    JsonOptions);
                using var req = new HttpRequestMessage(HttpMethod.Post, _introspectUrl);
                req.Headers.TryAddWithoutValidation("X-Internal-Key", internalKey);
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

        private static bool IsSafeIntrospectUrl(string value)
        {
            if (!Uri.TryCreate(
                    (value ?? "").Trim(),
                    UriKind.Absolute,
                    out var uri)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment)
                || (uri.Scheme != Uri.UriSchemeHttp
                    && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttps)
                return true;

            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(uri.Host, out var address)
                && IPAddress.IsLoopback(address);
        }

        internal static bool IsClientAddressAllowed(
            bool gatewayEnabled,
            IPAddress remoteAddress)
        {
            if (gatewayEnabled || remoteAddress == null)
                return gatewayEnabled;

            if (remoteAddress.IsIPv4MappedToIPv6)
                remoteAddress = remoteAddress.MapToIPv4();
            return IPAddress.IsLoopback(remoteAddress);
        }

        private static string ReadInternalKeyFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Gateway internal key file is required.");

            var fullPath = Path.GetFullPath(path.Trim());
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Gateway internal key file must not be a symbolic link.");
            }

            var key = File.ReadAllText(fullPath).Trim().ToLowerInvariant();
            if (key.Length != 64)
            {
                throw new InvalidOperationException(
                    "Gateway internal key file must contain a 256-bit hex key.");
            }
            try
            {
                _ = Convert.FromHexString(key);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "Gateway internal key file format is invalid.",
                    ex);
            }
            return key;
        }

        private static HttpClient CreateHttpClient(string caFile)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            };
            caFile = (caFile ?? "").Trim();
            if (!string.IsNullOrEmpty(caFile))
            {
                var roots = new X509Certificate2Collection();
                roots.ImportFromPemFile(Path.GetFullPath(caFile));
                if (roots.Count == 0)
                    throw new InvalidOperationException("Gateway CA file has no certificates.");

                handler.SslOptions.RemoteCertificateValidationCallback =
                    (_, certificate, serverChain, errors) =>
                    {
                        if (certificate == null
                            || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0
                            || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
                        {
                            return false;
                        }

                        using var chain = new X509Chain();
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                        if (serverChain != null)
                        {
                            for (var i = 1; i < serverChain.ChainElements.Count; i++)
                            {
                                chain.ChainPolicy.ExtraStore.Add(
                                    serverChain.ChainElements[i].Certificate);
                            }
                        }

                        using var leaf = X509CertificateLoader.LoadCertificate(
                            certificate.GetRawCertData());
                        return chain.Build(leaf);
                    };
            }

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
        }

        private static bool? ParseSwitch(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                    return false;
                default:
                    throw new InvalidOperationException(
                        $"{name} must be a boolean switch.");
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
