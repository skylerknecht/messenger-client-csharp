using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using MessengerClient;

public class Program
{
    public static async Task Main(string[] args)
    {
        ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(ValidateServerCertificate);

        if (args.Length < 2)
        {
            Console.WriteLine("URL is required as the first argument, key as second argument");
            return;
        }

        string uri = args[0];
        byte[] key = Crypto.Hash(args[1]);
        string[] attempts;

        uri = uri.Trim('/');

        if (uri.Contains("://"))
        {
            string[] urlParts = uri.Split(new string[] { "://" }, 2, StringSplitOptions.None);
            attempts = urlParts[0].Split('+');
            uri = urlParts[1];
        }
        else
        {
            attempts = new string[] { "ws", "http", "wss", "https" };
        }

        foreach (string attempt in attempts)
        {
            if (attempt.Contains("http"))
            {
                bool success = await TryHttp(attempt + "://" + uri, key);
                if (success)
                {
                    break;
                }
            }
            else if (attempt.Contains("ws"))
            {
                bool success = await TryWs(attempt + "://" + uri, key);
                if (success)
                {
                    break;
                }
            }
        }
    }

    private static async Task<bool> TryHttp(string url, byte[] key)
    {
        try
        {
            HTTPMessengerClient httpMessengerClient = new HTTPMessengerClient(key);
            await httpMessengerClient.Connect($"{url}/socketio/?EIO=4&transport=polling");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Failed to connect to {url}");
            return false;
        }
    }

    private static async Task<bool> TryWs(string url, byte[] key)
    {
        try
        {
            WebSocketMessengerClient webSocketMessengerClient = new WebSocketMessengerClient(key);
            await webSocketMessengerClient.Connect($"{url}/socketio/?EIO=4&transport=websocket");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Failed to connect to {url}");
            return false;
        }
    }

    public static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        return true; // Always accept
    }
}
