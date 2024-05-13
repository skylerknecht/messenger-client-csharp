using System.Threading.Tasks;

namespace MessengerClient
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            //try {
            WebSocketMessengerClient webSocketMessengerClient = new WebSocketMessengerClient();
            await webSocketMessengerClient.Connect($"{args[0]}/socketio/?EIO=4&transport=websocket");
            //} catch (Exception ex)
            //{
           //     Console.WriteLine(ex.ToString());
         //   }
            // implement HTTP
        }
    }
}
