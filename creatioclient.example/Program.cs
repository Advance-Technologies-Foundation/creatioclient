using System;
using System.Threading.Tasks;
using Creatio.Client;

namespace creatioclient.example
{
	static class Program
	{
		static async Task Main() {

			const string app = "http://localhost:8060";
			const string userName = "Supervisor";
			const string userPassword = "Supervisor";
			var authApp = "{authApp}";
			var clientId = "3B24D68AD2A710EC244320105D362A99";
			var clientSecret = "AB23B7D15810B4229B028848DFE09581D05F101F35FD71490782C420BF873B52";


			ICreatioClient client = new CreatioClient(app, userName, userPassword, false);
			client.WebSocketStateChanged += Client_WebSocketStateChanged;
			client.WebSocketMessageReceived += Client_WebSocketMessageReceived;

			await client.Listen();
			
			//var client = new CreatioClient(app, "Supervisor", "Supervisor");
			//var client = CreatioClient.CreateOAuth20Client(app, authApp, clientId, clientSecret);
			//string serviceName = "RightsService";
			//string methodName = "GetCanExecuteOperation";
			//string requestData = "{\"operation\":\"CanManageSolution\"}";
			//string request = client.CallConfigurationService(serviceName, methodName, requestData);
			//Console.WriteLine(request);
			Console.ReadLine();
		}

		private static void Client_WebSocketMessageReceived(object sender, WebSocketMessageEventArgs e)
		{
			Console.WriteLine("Message Id: {0}",e.WebSocketMessage.Id);
			Console.WriteLine("Message Sender: {0}",e.WebSocketMessage.Header.Sender);
			Console.WriteLine("Message BodyTypeName: {0}", e.WebSocketMessage.Header.BodyTypeName);
			Console.WriteLine("Message Body: {0}", e.WebSocketMessage.Body);
		}

		private static void Client_WebSocketStateChanged(object sender, WebSocketStateEventArgs e)
		{
			Console.WriteLine("Connection state: {0}",e.State);
		}
	}
}
