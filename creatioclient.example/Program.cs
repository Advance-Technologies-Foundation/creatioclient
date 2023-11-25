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

	private static void Main(string[] args){
		
		const string username = "Supervisor";
		const string password = "Supervisor";
		const string logFile = "C:\\ws.json";
		
		// const string app = "http://ts1-mrkt-web01.tscrm.com:91/zoominfo_staging";
		// CreatioClient client = new(app, username, password, true, false);
		
		const string app = "http://kkrylovn.tscrm.com:40016";
		CreatioClient client = new(app, username, password, true, true);
		
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
		client.StartListening(CancellationToken.None, "All","", Console.WriteLine);
		Console.ReadLine();
	}

	#endregion

}