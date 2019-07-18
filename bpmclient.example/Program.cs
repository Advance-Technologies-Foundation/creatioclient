using System;
using Bpmonline.Client;

namespace bpmclient.example
{
	class Program
	{
		static void Main(string[] args) {
			var client = new BpmonlineClient("http://localhost", "UserName", "UserPassword");
			string serviceName = "RightsService";
			string methodName = "GetCanExecuteOperation";
			string requestData = "{\"operation\":\"CanImportFromExcel\"}";
			string request = client.CallConfigurationService(serviceName, methodName, requestData);
			Console.WriteLine(request);
		}
	}
}
