using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using MessengerClient;

public class Program
{
    public static async Task Main(string[] args)
    {
        ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(ValidateServerCertificate);

        if (args.Length < 1)
        {
            throw new ArgumentException("URL is required as the first argument.");
        }

        string addressPort = args[0];
        bool useSsl = Array.Exists(args, arg => arg == "--ssl");
        bool useWebsockets = Array.Exists(args, arg => arg == "--websockets");
        bool useHttp = Array.Exists(args, arg => arg == "--http");

        string protocolWs = useSsl ? "wss" : "ws";
        string protocolHttp = useSsl ? "https" : "http";

        if (useWebsockets)
        {
            Console.WriteLine("[*] Connecting over WebSockets");
            await TryWsOnly($"{protocolWs}://{addressPort}");
        }
        else if (useHttp)
        {
            Console.WriteLine("[*] Connecting over HTTP");
            await TryHttpOnly($"{protocolHttp}://{addressPort}");
        }
        else
        {
            await TryWsFirstThenHttp($"{protocolWs}://{addressPort}", $"{protocolHttp}://{addressPort}");
        }
    }

    private static async Task TryHttpFirstThenWs(string httpUrl, string wsUrl)
    {
        Console.WriteLine("[*] Connecting over HTTP");
        if (!await TryHttpOnly(httpUrl))
        {
            Console.WriteLine("[*] Connecting over WebSockets");
            await TryWsOnly(wsUrl);
        }
    }

    private static async Task TryWsFirstThenHttp(string wsUrl, string httpUrl)
    {
        if (!await TryWsOnly(wsUrl))
        {
            await TryHttpOnly(httpUrl);
        }
    }

    private static async Task<bool> TryHttpOnly(string url)
    {
        try
        {
            HTTPMessengerClient httpMessengerClient = new HTTPMessengerClient();
            await httpMessengerClient.Connect($"{url}/socketio/?EIO=4&transport=polling");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] HTTP connection failed");
            return false;
        }
    }

    private static async Task<bool> TryWsOnly(string url)
    {
        try
        {
            WebSocketMessengerClient webSocketMessengerClient = new WebSocketMessengerClient();
            await webSocketMessengerClient.Connect($"{url}/socketio/?EIO=4&transport=websocket");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] WebSocket connection failed");
            return false;
        }
    }

    public static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        return true; // Always accept
    }
}