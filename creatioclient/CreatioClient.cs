using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Creatio.Client
{

	#region Class: TokenResponse

	public class TokenResponse
	{
		[JsonProperty("access_token")]
		public string AccessToken { get; set; }
		[JsonProperty("expires_in")]
		public int ExpiresIn { get; set; }
		[JsonProperty("token_type")]
		public string TokenType { get; set; }
	}

	#endregion

	#region Class:WebSocketStateEventArgs
	public class WebSocketStateEventArgs: EventArgs
	{
		public WebSocketState State { get; set; }
	}
	#endregion

	#region Class: WebSocketMessageEventArgs
	public class WebSocketMessageEventArgs : EventArgs
	{
		public IWebSocketMessageDto WebSocketMessage { get; set; }
	}
	#endregion

	#region Class: WebSocketMessageDto 
	[JsonObject]
	public class WebSocketMessageDto : IWebSocketMessageDto
	{
		[JsonProperty("Id")]
		public Guid Id { get; set; }

		[JsonProperty("Header")]
		public WebSocketMessageHeaderDto Header { get; set; }

		[JsonProperty("Body")]
		public string Body { get; set; }


		[JsonObject]
		public class WebSocketMessageHeaderDto
		{
			[JsonProperty("Sender")]
			public string Sender { get; set; }

			[JsonProperty("BodyTypeName")]
			public string BodyTypeName { get; set; }
		}
	}
	#endregion

	#region Interface: ICreatioClient

	public interface ICreatioClient
	{
		/// <summary>
		/// Event raised when WebSocket connection state is changed
		/// </summary>
		/// <remarks>
		/// See <seealso cref="WebSocketState"/> for possible values
		/// </remarks>
		event EventHandler<WebSocketStateEventArgs> WebSocketStateChanged;

		/// <summary>
		/// Event raised when new message arrives
		/// </summary>
		event EventHandler<WebSocketMessageEventArgs> WebSocketMessageReceived;

		#region Methods: Public

		void Login();

		string ExecuteGetRequest(string url, int requestTimeout = 10000);

		string ExecutePostRequest(string url, string requestData, int requestTimeout = 10000);

		string UploadFile(string url, string filePath);

		void DownloadFile(string url, string filePath, string requestData);

		string CallConfigurationService(string serviceName, string serviceMethod, string requestData, int requestTimeout = 10000);


		/// <summary>
		/// Start receiving webSocket messages
		/// </summary>
		/// <returns></returns>
		Task Listen();
		#endregion

	}

	#endregion

	#region Interface: IWebSocketMessageDto
	public interface IWebSocketMessageDto
	{
		/// <summary>
		/// Message Body
		/// </summary>
		string Body { get; set; }
		
		/// <summary>
		/// Message Header
		/// </summary>
		WebSocketMessageDto.WebSocketMessageHeaderDto Header { get; set; }
		
		/// <summary>
		/// Message Id
		/// </summary>
		Guid Id { get; set; }
	}
	#endregion

	#region Class: CreatioClient

	public class CreatioClient : ICreatioClient
	{
		#region Events
		public event EventHandler<WebSocketStateEventArgs> WebSocketStateChanged;
		public event EventHandler<WebSocketMessageEventArgs> WebSocketMessageReceived;

		#endregion

		#region Fields: private
		private WebSocketState _webSocketState = WebSocketState.None;
		private readonly string _appUrl;
		private readonly string _userName;
		private readonly string _userPassword;
		private readonly string _worskpaceId = "0";
		private readonly bool _isNetCore;
		private readonly bool _useUntrustedSSL = false;

		private string LoginUrl => _appUrl + @"/ServiceModel/AuthService.svc/Login";
		private string PingUrl => _appUrl + @"/0/ping";
		private CookieContainer _authCookie;
		private string oauthToken;

		private static async Task<string> GetAccessTokenByClientCredentials(string authApp, string clientId, string clientSecret)
		{
			using (HttpClient client = new HttpClient())
			{
				var body = new Dictionary<string, string>()
				{
					{ "client_id", clientId },
					{ "client_secret", clientSecret },
					{ "grant_type", "client_credentials" }
				};
				HttpContent httpContent = new FormUrlEncodedContent(body);
				HttpResponseMessage response = await client.PostAsync(authApp, httpContent).ConfigureAwait(false);
				string content = await response.Content.ReadAsStringAsync();
				TokenResponse token = JsonConvert.DeserializeObject<TokenResponse>(content);
				return token.AccessToken;
			}
		}


		public static CreatioClient CreateOAuth20Client(string app, string authApp, string clientId, string clientSecret, bool isNetCore = false)
		{
			var client = new CreatioClient(app, isNetCore);
			client.oauthToken = GetAccessTokenByClientCredentials(authApp, clientId, clientSecret).Result;
			return client;
		}

		#endregion

		#region Methods: Public

		public CreatioClient(string appUrl, string userName, string userPassword, bool isNetCore = false)
		{
			_appUrl = appUrl;
			_userName = userName;
			_userPassword = userPassword;
			_isNetCore = isNetCore;
		}

		public CreatioClient(string appUrl, string userName, string userPassword, bool UseUntrustedSSL, bool isNetCore = false)
		{
			_appUrl = appUrl;
			_userName = userName;
			_userPassword = userPassword;
			_useUntrustedSSL = UseUntrustedSSL;
			_isNetCore = isNetCore;
		}

		private CreatioClient(string appUrl, bool isNetCore = false)
		{
			_appUrl = appUrl;
			_isNetCore = isNetCore;
		}

		public void Login()
		{
			var authData = @"{
				""UserName"":""" + _userName + @""",
				""UserPassword"":""" + _userPassword + @"""
			}";
			var request = CreateRequest(LoginUrl);
			_authCookie = new CookieContainer();
			request.CookieContainer = _authCookie;
			ApplyRequestData(request, authData);
			using (var response = (HttpWebResponse)request.GetResponse())
			{
				if (response.StatusCode == HttpStatusCode.OK)
				{
					using (var reader = new StreamReader(response.GetResponseStream()))
					{
						var responseMessage = reader.ReadToEnd();
						if (responseMessage.Contains("\"Code\":1"))
						{
							throw new UnauthorizedAccessException($"Unauthorized {_userName} for {_appUrl}");
						}
					}
					var authCookieName = ".ASPXAUTH";
					var authCookieValue = response.Cookies[authCookieName].Value;
					_authCookie.Add(new Uri(_appUrl), new Cookie(authCookieName, authCookieValue));
				}
			}
		}

		public string ExecuteGetRequest(string url, int requestTimeout = 10000)
		{
			HttpWebRequest request = CreateCreatioRequest(url, null, requestTimeout);
			request.Method = "GET";
			return request.GetServiceResponse();
		}

		public string ExecutePostRequest(string url, string requestData, int requestTimeout = 10000)
		{
			if (oauthToken != null)
			{
				using (HttpClient client = new HttpClient())
				{
					client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
					var stringContent = new StringContent(requestData, UnicodeEncoding.UTF8, "application/json");
					HttpResponseMessage response = client.PostAsync(url, stringContent).Result;
					string content = response.Content.ReadAsStringAsync().Result;
					return content;
				}
			}
			else
			{
				HttpWebRequest request = CreateCreatioRequest(url, requestData, requestTimeout);
				return request.GetServiceResponse();
			}
		}

		public string UploadFile(string url, string filePath)
		{
			FileInfo fileInfo = new FileInfo(filePath);
			string fileName = fileInfo.Name;
			string boundary = DateTime.Now.Ticks.ToString("x");
			HttpWebRequest request = CreateCreatioRequest(url);
			request.ContentType = "multipart/form-data; boundary=" + boundary;
			Stream memStream = new MemoryStream();
			var boundarybytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
			var endBoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--");
			string headerTemplate =
				"Content-Disposition: form-data; name=\"{0}\"; filename=\"{1}\"\r\n" +
				"Content-Type: application/octet-stream\r\n\r\n";
			memStream.Write(boundarybytes, 0, boundarybytes.Length);
			var header = string.Format(headerTemplate, "files", fileName);
			var headerbytes = Encoding.UTF8.GetBytes(header);
			memStream.Write(headerbytes, 0, headerbytes.Length);
			using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
			{
				var buffer = new byte[1024];
				var bytesRead = 0;
				while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0)
				{
					memStream.Write(buffer, 0, bytesRead);
				}
			}
			memStream.Write(endBoundaryBytes, 0, endBoundaryBytes.Length);
			request.ContentLength = memStream.Length;
			using (Stream requestStream = request.GetRequestStream())
			{
				memStream.Position = 0;
				byte[] tempBuffer = new byte[memStream.Length];
				memStream.Read(tempBuffer, 0, tempBuffer.Length);
				memStream.Close();
				requestStream.Write(tempBuffer, 0, tempBuffer.Length);
			}
			return request.GetServiceResponse();
		}

		public void DownloadFile(string url, string filePath, string requestData)
		{
			HttpWebRequest request = CreateCreatioRequest(url, requestData);
			request.SaveToFile(filePath);
		}

		public string CallConfigurationService(string serviceName, string serviceMethod, string requestData, int requestTimeout = 10000)
		{
			var executeUrl = CreateConfigurationServiceUrl(serviceName, serviceMethod);
			return ExecutePostRequest(executeUrl, requestData, requestTimeout);
		}
		
		public async Task Listen()
		{
			Login();
			var appUri = new Uri(_appUrl);
			Uri wsUri = (appUri.Scheme == "http") ? new Uri($"ws://{appUri.Host}:{appUri.Port}") : new Uri($"wss://{appUri.Host}:{appUri.Port}");
			Uri websockerUri = (_isNetCore) ? new Uri(wsUri, "/Nui/ViewModule.aspx.ashx") : new Uri(wsUri, "/0/Nui/ViewModule.aspx.ashx");
			do
			{
				ClientWebSocket cws = new ClientWebSocket();
				cws.Options.Cookies = _authCookie;
				await cws.ConnectAsync(websockerUri, CancellationToken.None);
				if (cws.State == WebSocketState.Open)
				{
					_webSocketState = cws.State;
					OnWebSocketStateChanged(new WebSocketStateEventArgs { State = cws.State });
					await SubscribeToMessages(cws);
				}
			}
			while (_webSocketState != WebSocketState.Open);
		}

		#endregion

		#region Methods: Private

		private string CreateConfigurationServiceUrl(string serviceName, string methodName)
		{
			return $"{_appUrl}/{_worskpaceId}/rest/{serviceName}/{methodName}";
		}

		private void AddCsrfToken(HttpWebRequest request)
		{
			var cookie = request.CookieContainer.GetCookies(new Uri(_appUrl))["BPMCSRF"];
			if (cookie != null)
			{
				request.Headers.Add("BPMCSRF", cookie.Value);
			}
		}

		private void PingApp()
		{
			if (_isNetCore)
			{
				return;
			}

			var pingRequest = CreateCreatioRequest(PingUrl);
			pingRequest.Timeout = 60000;
			pingRequest.ContentLength = 0;
			_ = pingRequest.GetServiceResponse();
		}

		private HttpWebRequest CreateCreatioRequest(string url, string requestData = null, int requestTimeout = 100000)
		{
			if (_authCookie == null && string.IsNullOrEmpty(oauthToken))
			{
				Login();
				PingApp();
			}
			var request = CreateRequest(url);
			if (_useUntrustedSSL)
			{
				request.ServerCertificateValidationCallback = (message, cert, chain, errors) => { return true; };
			}
			request.Timeout = requestTimeout;

			if (!string.IsNullOrEmpty(oauthToken))
			{

			}
			else
			{

				request.CookieContainer = _authCookie;
				AddCsrfToken(request);
			}
			ApplyRequestData(request, requestData);
			return request;
		}

		private HttpWebRequest CreateRequest(string url)
		{
			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
			if (_useUntrustedSSL)
			{
				request.ServerCertificateValidationCallback = (message, cert, chain, errors) => { return true; };
			}
			request.ContentType = "application/json";
			request.Method = "POST";
			request.KeepAlive = true;
			return request;
		}

		private void ApplyRequestData(HttpWebRequest request, string requestData = null)
		{
			if (string.IsNullOrEmpty(requestData))
			{
				request.ContentLength = 0;
				return;
			}
			var bytes = Encoding.ASCII.GetBytes(requestData);
			request.ContentLength = bytes.Length;
			using (var dataStream = request.GetRequestStream())
			{
				dataStream.Write(bytes, 0, bytes.Length);
			}
		}

		private async Task SubscribeToMessages(ClientWebSocket cws)
		{
			try
			{
				while (cws.State == WebSocketState.Open)
				{

					byte[] incomingData = new byte[1024 * 8];
					WebSocketReceiveResult result = await cws.ReceiveAsync(new ArraySegment<byte>(incomingData), CancellationToken.None);

					if (result.CloseStatus.HasValue)
					{
						_webSocketState = WebSocketState.Closed;
						OnWebSocketStateChanged(new WebSocketStateEventArgs { State = WebSocketState.Closed });
					}
					else
					{
						var str = Encoding.UTF8.GetString(incomingData, 0, result.Count);
						OnWebSocketMessage(new WebSocketMessageEventArgs {
							WebSocketMessage = JsonConvert.DeserializeObject<WebSocketMessageDto>(str)
						});
					}
				}
			}
			catch (WebSocketException ex)
			{
				_webSocketState = cws.State;
				OnWebSocketStateChanged(new WebSocketStateEventArgs { State = cws.State });
			}
		}

		#endregion

		#region Methods Protected

		protected virtual void OnWebSocketStateChanged(WebSocketStateEventArgs e)
		{
			WebSocketStateChanged?.Invoke(this, e);
		}

		protected virtual void OnWebSocketMessage(WebSocketMessageEventArgs e)
		{
			WebSocketMessageReceived?.Invoke(this, e);
		}

		#endregion
	}
	#endregion
}
