using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class Program
    {
        private const string SERVER_URL = "{{ server_url }}";
        private const string ENCRYPTION_KEY = "{{ encryption_key }}";
        private const string USER_AGENT = "{{ user_agent }}";
        private const string PROXY = "{{ proxy }}";
        private const double RETRY_DURATION = {{ retry_duration }};
        private const int RETRY_ATTEMPTS = {{ retry_attempts }};
        private static readonly string[] REMOTE_PORT_FORWARDS = new string[]
        {
            {% for rpf in remote_port_forwards %}
            "{{ rpf }}",
            {% endfor %}
        };

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var parsed = new Dictionary<string, string>();
            var rpfs = new List<string>();
            bool rpfSeen = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--server-url":
                        parsed["server-url"] = args[++i];
                        break;
                    case "--encryption-key":
                        parsed["encryption-key"] = args[++i];
                        break;
                    case "--user-agent":
                        parsed["user-agent"] = args[++i];
                        break;
                    case "--proxy":
                        parsed["proxy"] = args[++i];
                        break;
                    case "--retry-duration":
                        parsed["retry-duration"] = args[++i];
                        break;
                    case "--retry-attempts":
                        parsed["retry-attempts"] = args[++i];
                        break;
                    case "--remote-port-forwards":
                        rpfSeen = true;
                        while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                            rpfs.Add(args[++i]);
                        break;
                    default:
                        Console.WriteLine($"[!] Could not find argument `{args[i]}`.");
                        break;
                }
            }
            if (rpfSeen)
                parsed["remote-port-forwards"] = string.Join("|", rpfs);
            return parsed;
        }

        public static async Task Main(string[] args)
        {
            ServicePointManager.ServerCertificateValidationCallback =
                new RemoteCertificateValidationCallback(ValidateServerCertificate);

            var parsed = ParseArgs(args);

            string serverUrl = parsed.ContainsKey("server-url") ? parsed["server-url"] : SERVER_URL;
            string encryptionKeyStr = parsed.ContainsKey("encryption-key") ? parsed["encryption-key"] : ENCRYPTION_KEY;
            if (string.IsNullOrEmpty(encryptionKeyStr))
            {
                Console.WriteLine("[!] No encryption key provided, please specify one with --encryption-key.");
                return;
            }
            byte[] encryptionKey = Crypto.Hash(encryptionKeyStr);
            string userAgent = parsed.ContainsKey("user-agent") ? parsed["user-agent"] : USER_AGENT;
            string proxyStr = parsed.ContainsKey("proxy") ? parsed["proxy"] : PROXY;
            double retryDuration = parsed.ContainsKey("retry-duration") ? double.Parse(parsed["retry-duration"]) : RETRY_DURATION;
            int retryAttempts = parsed.ContainsKey("retry-attempts") ? int.Parse(parsed["retry-attempts"]) : RETRY_ATTEMPTS;
            string[] remotePortForwards = parsed.ContainsKey("remote-port-forwards")
                ? parsed["remote-port-forwards"].Split('|')
                : REMOTE_PORT_FORWARDS;

            IWebProxy proxy = null;
            if (!string.IsNullOrEmpty(proxyStr))
            {
                proxy = CreateWebProxy(proxyStr);
                Console.WriteLine($"Using proxy: {proxyStr}");
            }

            string uri = serverUrl.Trim('/');
            string[] schemes;

            if (uri.Contains("://"))
            {
                string[] urlParts = uri.Split(new[] { "://" }, 2, StringSplitOptions.None);
                schemes = urlParts[0].Split('+');
                uri = urlParts[1];
            }
            else
            {
                schemes = new[] { "ws", "http", "wss", "https" };
            }

            foreach (string scheme in schemes)
            {
                bool success = false;
                if (scheme.Contains("http"))
                    success = await TryHttp($"{scheme}://{uri}", encryptionKey, userAgent, retryDuration, retryAttempts, remotePortForwards, proxy);
                else if (scheme.Contains("ws"))
                    success = await TryWs($"{scheme}://{uri}", encryptionKey, userAgent, retryDuration, retryAttempts, remotePortForwards, proxy);

                if (success)
                    return;
            }

            Console.WriteLine("All connection attempts failed.");
        }

        private static async Task<bool> TryHttp(string url, byte[] encryptionKey, string userAgent, double retryDuration, int retryAttempts, string[] remotePortForwards, IWebProxy proxy)
        {
            try
            {
                Console.WriteLine($"[HTTP] Trying {url}");
                var client = new HTTPMessengerClient(url, encryptionKey, userAgent, retryDuration, retryAttempts, proxy);
                client.OnConnected = () => StartRemotePortForwards(client, remotePortForwards);
                await client.ConnectAsync();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTP] Failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> TryWs(string url, byte[] encryptionKey, string userAgent, double retryDuration, int retryAttempts, string[] remotePortForwards, IWebProxy proxy)
        {
            try
            {
                Console.WriteLine($"[WebSocket] Trying {url}");
                var client = new WebSocketMessengerClient(url, encryptionKey, userAgent, retryDuration, retryAttempts, proxy);
                client.OnConnected = () => StartRemotePortForwards(client, remotePortForwards);
                await client.ConnectAsync();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Failed: {ex.Message}");
                return false;
            }
        }

        private static void StartRemotePortForwards(MessengerClient client, string[] remotePortForwards)
        {
            foreach (var config in remotePortForwards)
            {
                try
                {
                    var forwarder = new RemotePortForwarder(client, config);
                    _ = forwarder.StartAsync();
                    Console.WriteLine($"Started RemotePortForwarder: {config}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed RemotePortForwarder {config}: {ex.Message}");
                }
            }
        }

        private static IWebProxy CreateWebProxy(string proxyConfig)
        {
            var proxyUri = new Uri(proxyConfig);
            var webProxy = new WebProxy(proxyUri);

            if (!string.IsNullOrEmpty(proxyUri.UserInfo))
            {
                string[] userInfo = proxyUri.UserInfo.Split(':');
                if (userInfo.Length == 2)
                    webProxy.Credentials = new NetworkCredential(userInfo[0], userInfo[1]);
            }

            return webProxy;
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }
    }
}
