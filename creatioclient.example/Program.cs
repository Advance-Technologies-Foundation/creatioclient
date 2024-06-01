using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Creatio.Client;

namespace creatioclient.example;

public static class Program
{

	#region Methods: Private

	
	private const string AppUrl = "http://kkrylovn.tscrm.com:40016";
	
	private static void Main(){
		
		const string username = "Supervisor";
		const string password = "Supervisor";
		const string logFile = "C:\\ws.json";
		
		CreatioClient client = new(AppUrl, username, password, true, true);
		
		client.ConnectionStateChanged += (sender, state) => {
			Console.WriteLine($"Connection state changed to: {state}");
		};
		
		JsonSerializerOptions opts = new() {
			WriteIndented = true,
		};
		client.MessageReceived += (sender, message) => {
			var msgObject = new {
				Header = message.Header,
				Id = message.Id,
				Body = JsonSerializer.Deserialize(message.Body, typeof(object), opts)
			};
			System.IO.File.AppendAllText(logFile,JsonSerializer.Serialize(msgObject, opts), Encoding.UTF8);
		};
		client.StartListening(CancellationToken.None);
		Console.ReadLine();
	}
	
	private static void Example2(){
		CreatioClient client = new(AppUrl, true, CredentialCache.DefaultNetworkCredentials);
		const string url = AppUrl+ "/0/ServiceModel/UserInfoService.svc/getCurrentUserInfo";
		string result = client.ExecutePostRequest(url, string.Empty, 100000);
		Console.WriteLine(result);
	}

	#endregion

}