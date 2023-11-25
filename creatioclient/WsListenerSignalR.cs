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
	internal sealed class WsListenerSignalR : IWsListener
	{

		#region Constants: Private

		private const string StartLogBroadcast = "/rest/ATFLogService/StartLogBroadcast";
		private const string StopLogBroadcast = "/rest/ATFLogService/ResetConfiguration";

		#endregion

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

		private static readonly Func<Uri, string, Uri> WsUri = (appUri, connectionToken) => {
			UriBuilder wsUri = new UriBuilder(appUri) {
				Scheme = appUri.Scheme == "https" ? "wss" : "ws",
				Path = appUri.LocalPath == "/" ? "msg" : appUri.LocalPath + "/msg",
				Query = $"id={connectionToken}"
			};
			return wsUri.Uri;
		};

		private static readonly Func<string, ICreatioClient, NegotiateResponse> NegotiateWsConnection
			= (appUrl, creatioClient) => {
				string executeUrl = $"{appUrl}/msg/negotiate?negotiateVersion=1";
				string x = creatioClient.ExecutePostRequest(executeUrl, string.Empty, 10000);
				NegotiateResponse response = JsonConvert.DeserializeObject<NegotiateResponse>(x);
				return response;
			};

		/// <summary>
		/// After opening a connection the client <b>MUST</b> send a HandshakeRequest message to the server as its first message.
		/// The handshake message is always a JSON message and contains the name of the format (protocol) as well as the
		/// version of the protocol that will be used for the duration of the connection.<br/>
		/// <para>
		/// The server will reply with a HandshakeResponse, also always JSON, containing an error if the server does not
		/// support the protocol. If the server does not support the protocol requested by the client or the first message
		/// received from the client is not a HandshakeRequest message the server must close the connection.<br/>
		/// </para>
		/// <para>
		/// Both the HandshakeRequest and HandshakeResponse messages must be terminated by the ASCII character
		/// <b>0x1E</b> (record separator).
		/// </para>
		/// </summary>
		/// See <seealso href="https://github.com/aspnet/SignalR/blob/master/specs/HubProtocol.md#overview">SignalR Hub Protocol</seealso>
		private static readonly Action<ClientWebSocket> SendFirstRequest = client => {
			byte[] buf = Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e");
			client.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Binary, true,
				CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
		};

		private readonly string _appUrl;
		private readonly CreatioClient _creatioClient;
		private readonly CancellationToken _cancellationToken;
		private readonly string _logLevel;
		private readonly string _logPattern;
		private readonly Action<string> _logger;
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
		/// <param name="logLevel"></param>
		/// <param name="logPattern"></param>
		/// <param name="logger"></param>
		public WsListenerSignalR(string appUrl, CreatioClient creatioClient, CancellationToken cancellationToken,
			string logLevel, string logPattern, Action<string> logger){
			_appUrl = appUrl;
			_creatioClient = creatioClient;
			_cancellationToken = cancellationToken;
			_logLevel = logLevel;
			_logPattern = logPattern;
			_logger = logger;
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
			int currentPosition = 0;
			int startPosition = 0;
			while(currentPosition< _buffer.Length) {
				if(_buffer[currentPosition] == 30) {
					string msg = Encoding.UTF8.GetString(_buffer, startPosition, currentPosition-startPosition);
					SignalRWrapper msgObj = JsonConvert.DeserializeObject<SignalRWrapper>(msg);
					if (msgObj.Arguments != null && msgObj.Arguments.Any()) {
						OnMessageReceived(msgObj.Arguments);
					}	
					startPosition = currentPosition+1;
				}
				currentPosition++;
			}
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
			StartLogger();
			NegotiateResponse response = NegotiateWsConnection(_appUrl, _creatioClient);
			Uri wsUri = WsUri(new Uri(_appUrl), response.ConnectionToken);
			_currentPosition = 0;
			Array.Clear(_buffer, 0, _buffer.Length);
			_client?.Dispose();
			_client = CreateClient(_creatioClient.AuthCookie, _appUrl);
			_client.ConnectAsync(wsUri, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
			ConnectionState = _client.State;
			SendFirstRequest(_client);
		}

		private void OnMessageReceived(IEnumerable<WsMessage> messages){
			messages.ToList().ForEach(m => MessageReceived?.Invoke(this, m));
		}

		private void StartLogger(){
			string requestUrl = _appUrl + StartLogBroadcast;
			var payload = new {
				logLevelStr = _logLevel ?? "All",
				bufferSize = 1,
				loggerPattern = _logPattern ?? ""
			};
			string payloadString = JsonConvert.SerializeObject(payload);
			_creatioClient.ExecutePostRequest(requestUrl, payloadString, 10_000, 10, 3);
			_logger("Logger Started");
		}

		private void StopLogger(){
			string requestUrl = _appUrl + StopLogBroadcast;
			_creatioClient.ExecutePostRequest(requestUrl, string.Empty);
			_logger("Logger stopped");
		}

		#endregion

		#region Methods: Public

		public void Dispose(){
			StopLogger();
			Array.Clear(_buffer, 0, _buffer.Length);
			_client?.Dispose();
			ConnectionState = WebSocketState.None;
		}

		public void StartListening(){
			while (!_cancellationToken.IsCancellationRequested) {
				try {
					WebSocketReceiveResult result = _client?.ReceiveAsync(
							new ArraySegment<byte>(_buffer, _currentPosition, _buffer.Length - _currentPosition),
							_cancellationToken)
						.ConfigureAwait(false).GetAwaiter().GetResult();
					HandleWebSocketReceiveResult(result);
				} catch(Exception e) {
					_logger(e.Message);
					ConnectionState = _client?.State ?? WebSocketState.None;
					_currentPosition = 0;
					Array.Clear(_buffer, 0, _buffer.Length);
					Thread.Sleep(TimeSpan.FromSeconds(1));
					InitConnection();
				}
			}
			_logger("Stopping logger");
			StopLogger();

			_client?.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
				.ConfigureAwait(false).GetAwaiter().GetResult();
			_logger("Disconnected from WebSocket");
		}

		#endregion

	}
}