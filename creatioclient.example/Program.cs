using System;
using Creatio.Client;

namespace creatioclient.example
{
	class Program
	{
		static void Main(string[] args) {





			var app = " http://tsagent-2-3:88/StudioENU_3627526_1112";
			var authApp = "http://tsagent-2-3:88/studioenu_3627526_is_1112/connect/token";
			var clientId = "3B24D68AD2A710EC244320105D362A99";
			var clientSecret = "AB23B7D15810B4229B028848DFE09581D05F101F35FD71490782C420BF873B52";


			//var client = new CreatioClient(app, "Supervisor", "Supervisor");
			var client = CreatioClient.CreateOAuth20Client(app, authApp, clientId, clientSecret);
			string serviceName = "RightsService";
			string methodName = "GetCanExecuteOperation";
			string requestData = "{\"operation\":\"CanManageSolution\"}";
			string request = client.CallConfigurationService(serviceName, methodName, requestData);
			Console.WriteLine(request);
			Console.ReadLine();
		}
	}
}
