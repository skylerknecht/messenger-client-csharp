using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class Program
    {
        // Constants for HTTP and WebSocket routes and user agent
        private const string HTTP_ROUTE = "socketio/?EIO=4&transport=polling";
        private const string WS_ROUTE = "socketio/?EIO=4&transport=websocket";
        private const string USER_AGENT = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:128.0) Gecko/20100101 Firefox/128.0";

        public static async Task Main(string[] args)
        {
            ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(ValidateServerCertificate);

            if (args.Length < 1)
            {
                Console.WriteLine("Usage: Program <URL> [remote_port_forwards...]");
                return;
            }

            string uri = args[0];

            // Handling `remotePortForwards` without slicing
            string[] remotePortForwards;
            if (args.Length > 1)
            {
                remotePortForwards = new string[args.Length - 1];
                Array.Copy(args, 1, remotePortForwards, 0, args.Length - 1);
            }
            else
            {
                remotePortForwards = Array.Empty<string>();
            }

            string[] attempts;

            uri = uri.Trim('/');

            if (uri.Contains("://"))
            {
                string[] urlParts = uri.Split(new[] { "://" }, 2, StringSplitOptions.None);
                attempts = urlParts[0].Split('+');
                uri = urlParts[1];
            }
            else
            {
                attempts = new[] { "ws", "http", "wss", "https" };
            }

            foreach (string attempt in attempts)
            {
                if (attempt.Contains("http"))
                {
                    bool success = await TryHttp($"{attempt}://{uri}/{HTTP_ROUTE}", remotePortForwards);
                    if (success)
                    {
                        await Task.Delay(-1);
                        return;
                    }
                }
                else if (attempt.Contains("ws"))
                {
                    bool success = await TryWs($"{attempt}://{uri}/{WS_ROUTE}", remotePortForwards);
                    if (success)
                    {
                       await Task.Delay(-1);
                       return;
                    }
                }
            }

            Console.WriteLine("All connection attempts failed.");
        }

        private static async Task<bool> TryHttp(string url, string[] remotePortForwards)
        {
            try
            {
                Console.WriteLine($"[HTTP] Trying {url}");
                var httpMessengerClient = new HTTPMessengerClient(url);
                httpMessengerClient.ConnectAsync();
                StartRemotePortForwardsAsync(httpMessengerClient, remotePortForwards);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTP] Failed to connect to {url}: {ex}");
                return false;
            }
        }

        private static async Task<bool> TryWs(string url, string[] remotePortForwards)
        {
            try
            {
                Console.WriteLine($"[WebSocket] Trying {url}");
                var webSocketMessengerClient = new WebSocketMessengerClient(url);
                webSocketMessengerClient.ConnectAsync();
                StartRemotePortForwardsAsync(webSocketMessengerClient, remotePortForwards);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Failed to connect to {url}: {ex}");
                return false;
            }
        }

        private static async Task StartRemotePortForwardsAsync(MessengerClient messengerClient, string[] remotePortForwards)
        {
            foreach (var config in remotePortForwards)
            {
                try
                {
                    var forwarder = new RemotePortForwarder(messengerClient, config);
                    _ = forwarder.StartAsync(); // Fire-and-forget to start each forwarder concurrently
                    Console.WriteLine($"Started RemotePortForwarder with config: {config}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to start RemotePortForwarder with config {config}: {ex.Message}");
                }
            }

            // Keep the main thread alive while the forwarders are running.
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true; // Always accept
        }
    }
}
