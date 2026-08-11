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

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var parsed = new Dictionary<string, string>();
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
                    default:
                        Console.WriteLine($"[!] Could not find argument `{args[i]}`.");
                        break;
                }
            }
            return parsed;
        }

        public static async Task Main(string[] args)
        {
            ServicePointManager.ServerCertificateValidationCallback =
                new RemoteCertificateValidationCallback(ValidateServerCertificate);

            var parsed = ParseArgs(args);

            string serverUrl = parsed.ContainsKey("server-url") && !string.IsNullOrEmpty(parsed["server-url"]) ? parsed["server-url"] : SERVER_URL;
            string encryptionKeyStr = parsed.ContainsKey("encryption-key") && !string.IsNullOrEmpty(parsed["encryption-key"]) ? parsed["encryption-key"] : ENCRYPTION_KEY;
            if (string.IsNullOrEmpty(encryptionKeyStr))
            {
                Console.WriteLine("[!] No encryption key provided, please specify one with --encryption-key.");
                return;
            }
            byte[] encryptionKey = Crypto.Hash(encryptionKeyStr);
            string userAgent = parsed.ContainsKey("user-agent") && !string.IsNullOrEmpty(parsed["user-agent"]) ? parsed["user-agent"] : USER_AGENT;
            string proxyStr = parsed.ContainsKey("proxy") && !string.IsNullOrEmpty(parsed["proxy"]) ? parsed["proxy"] : PROXY;
            double retryDuration = parsed.ContainsKey("retry-duration") ? double.Parse(parsed["retry-duration"]) : RETRY_DURATION;
            int retryAttempts = parsed.ContainsKey("retry-attempts") ? int.Parse(parsed["retry-attempts"]) : RETRY_ATTEMPTS;

            IWebProxy proxy = null;
            if (!string.IsNullOrEmpty(proxyStr))
            {
                proxy = CreateWebProxy(proxyStr);
                Console.WriteLine($"[*] Using proxy: {proxyStr}");
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
                schemes = new[] { "ws", "wss", "http", "https" };
            }

            MessengerClient client = null;

            foreach (string scheme in schemes)
            {
                string candidateUrl = $"{scheme}://{uri}";

                if (scheme.Contains("ws"))
                    client = new WebSocketMessengerClient(candidateUrl, encryptionKey, userAgent, proxy);
                else if (scheme.Contains("http"))
                    client = new HTTPMessengerClient(candidateUrl, encryptionKey, userAgent, proxy);
                else
                {
                    Console.WriteLine($"[!] Unsupported scheme: {scheme}");
                    continue;
                }

                try
                {
                    await client.ConnectAsync();
                    Console.WriteLine($"[+] Connected to {candidateUrl}");
                    break;
                }
                catch (DecryptionException)
                {
                    Console.WriteLine("[!] Decryption failed — the encryption key is likely incorrect. The messenger cannot decrypt server traffic and is stopping.");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Connection failed: {ex.Message}");
                    client = null;
                    continue;
                }
            }

            if (client == null)
            {
                Console.WriteLine("[!] All connection attempts failed.");
                return;
            }

            try
            {
                await client.StartAsync();
            }
            catch (DecryptionException)
            {
                Console.WriteLine("[!] Decryption failed — the encryption key is likely incorrect. The messenger cannot decrypt server traffic and is stopping.");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Disconnected: {ex.Message}");
            }

            if (retryAttempts <= 0)
                return;

            int sleepInterval = (int)((retryDuration / retryAttempts) * 1000);
            int consecutiveFailures = 0;

            while (consecutiveFailures < retryAttempts)
            {
                await Task.Delay(sleepInterval);

                try
                {
                    await client.ConnectAsync();
                    Console.WriteLine("[+] Reconnected");
                    consecutiveFailures = 0;
                    await client.StartAsync();
                }
                catch (DecryptionException)
                {
                    Console.WriteLine("[!] Decryption failed — the encryption key is likely incorrect. The messenger cannot decrypt server traffic and is stopping.");
                    return;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    Console.WriteLine($"[!] Reconnection failed: {ex.Message}");
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
