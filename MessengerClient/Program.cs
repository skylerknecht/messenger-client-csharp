using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class Program
    {

        public static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // Your validation logic here
            return true; // WARNING: Always returning true is insecure for production code
        }
        public static async Task Main(string[] args)
        {

            ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(ValidateServerCertificate);

            //try {
            WebSocketMessengerClient webSocketMessengerClient = new WebSocketMessengerClient();
            await webSocketMessengerClient.Connect($"{args[0]}/socketio/?EIO=4&transport=websocket");
            Console.WriteLine("lol");
            //} catch (Exception ex)
            //{
            //     Console.WriteLine(ex.ToString());
            //   }
            // implement HTTP
        }
    }
}