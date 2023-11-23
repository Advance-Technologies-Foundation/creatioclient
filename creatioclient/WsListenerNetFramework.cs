using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Creatio.Client.Dto;
using Newtonsoft.Json;

namespace Creatio.Client
{
	
	internal sealed class WsListenerNetFramework : IWsListener
	{

		#region Fields: Private

		private static readonly Func<CookieContainer, string, ClientWebSocket> CreateClient = (cookies, appUrl) => {
			ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
			ClientWebSocket client = new ClientWebSocket();
			client.Options.Cookies = cookies;
			client.Options.SetRequestHeader("Accept-Encoding", "gzip,deflate");
			client.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
			client.Options.SetRequestHeader("Cache-Control", "no-cache");
			client.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
			return client;
		};

		private static readonly Func<Uri, Uri> WsUri = (appUri) => {
			UriBuilder wsUri = new UriBuilder(appUri) {
				Scheme = appUri.Scheme == "https" ? "wss" : "ws",
				Path = appUri.LocalPath+"/0/Nui/ViewModule.aspx.ashx",
			};
			return wsUri.Uri;
		};

		

		private readonly string _appUrl;
		private readonly CreatioClient _creatioClient;
		private readonly CancellationToken _cancellationToken;
		private ClientWebSocket _client;
		private WebSocketState _connectionState;
		private readonly byte[] _buffer = new byte[8192 * 1024];
		private int _currentPosition;

		#endregion

		#region Constructors: Public

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="appUrl"></param>
		/// <param name="creatioClient"></param>
		/// <param name="cancellationToken"></param>
		public WsListenerNetFramework(string appUrl, CreatioClient creatioClient, CancellationToken cancellationToken){
			_appUrl = appUrl;
			_creatioClient = creatioClient;
			_cancellationToken = cancellationToken;
		}

		#endregion

		#region Properties: Private

		private WebSocketState ConnectionState {
			get => _connectionState;
			set {
				if (_connectionState == value) {
					return;
				}
				_connectionState = value;
				ConnectionStateChanged?.Invoke(this, _connectionState);
			}
		}

		#endregion

		#region Events: Public

		public event EventHandler<WsMessage> MessageReceived;

		public event EventHandler<WebSocketState> ConnectionStateChanged;

		#endregion

		#region Methods: Private

		private void HandleBinaryMessage(){
			//TODO: I don't think Creatio sends binary messages
		}

		private void HandleCloseMessage(){
			_client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
				.GetAwaiter().GetResult();
			ConnectionState = _client.State;
			InitConnection();
		}

		private void HandleTextMessage(){
			bool endWithUnreadableSymbol = _buffer[_currentPosition - 1] == 30;
			string message = endWithUnreadableSymbol
				? Encoding.UTF8.GetString(_buffer, 0, _currentPosition - 1)
				: Encoding.UTF8.GetString(_buffer, 0, _currentPosition);
			WsMessage msgObj = JsonConvert.DeserializeObject<WsMessage>(message);
			OnMessageReceived(new []{msgObj});
			
		}

		private void HandleWebSocketReceiveResult(WebSocketReceiveResult result){
			_currentPosition += result.Count;
			if (!result.EndOfMessage) {
				return;
			}
			switch (result.MessageType) {
				case WebSocketMessageType.Text:
					HandleTextMessage();
					break;
				case WebSocketMessageType.Binary:
					HandleBinaryMessage();
					break;
				case WebSocketMessageType.Close:
					HandleCloseMessage();
					break;
			}
			_currentPosition = 0;
			Array.Clear(_buffer, 0, _buffer.Length);
		}

		private void InitConnection(){
			_creatioClient.Login();
			Uri wsUri = WsUri(new Uri(_appUrl));
			_currentPosition = 0;
			Array.Clear(_buffer, 0, _buffer.Length);
			_client?.Dispose();
			_client = CreateClient(_creatioClient.AuthCookie, _appUrl);
			_client.ConnectAsync(wsUri, _cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
			ConnectionState = _client.State;
		}

		private void OnMessageReceived(IEnumerable<WsMessage> messages){
			messages.ToList().ForEach(m => MessageReceived?.Invoke(this, m));
		}

		#endregion

		#region Methods: Public

		public void Dispose(){
			Array.Clear(_buffer, 0, _buffer.Length);
			_client?.Dispose();
			ConnectionState = WebSocketState.None;
		}

		public void StartListening(){
			InitConnection();
			while (!_cancellationToken.IsCancellationRequested) {
				try {
					WebSocketReceiveResult result = _client.ReceiveAsync(
							new ArraySegment<byte>(_buffer, _currentPosition, _buffer.Length - _currentPosition),
							_cancellationToken)
						.ConfigureAwait(false).GetAwaiter().GetResult();
					HandleWebSocketReceiveResult(result);
				} catch {
					ConnectionState = _client.State;
					_currentPosition = 0;
					Array.Clear(_buffer, 0, _buffer.Length);
					Thread.Sleep(TimeSpan.FromSeconds(1));
					InitConnection();
				}
			}
		}

		#endregion

	}
}