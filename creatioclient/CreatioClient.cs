using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creatio.Client.Dto;
using Newtonsoft.Json;

namespace Creatio.Client
{


	#region Interface: ICreatioClient

	public interface ICreatioClient
	{

		#region Events: Public

		event EventHandler<WsMessage> MessageReceived;

		event EventHandler<WebSocketState> ConnectionStateChanged;

		#endregion

		#region Methods: Public

		string CallConfigurationService(string serviceName, string serviceMethod, string requestData,
			int requestTimeout = 100000);

		void DownloadFile(string url, string filePath, string requestData, int requestTimeout = 100000);

		string ExecuteGetRequest(string url, int requestTimeout = 100000, int retryCount = 1, int delaySec = 1);

		string ExecutePostRequest(string url, string requestData, int requestTimeout = 100000, int retryCount = 1, int delaySec = 1);

		void Login();

		string UploadAlmFile(string url, string filePath);

		string UploadFile(string url, string filePath, int requestTimeout = 100000);

		void StartListening(CancellationToken cancellationToken);
		#endregion

	}

	#endregion

	#region Class: CreatioClient

	public class CreatioClient : ICreatioClient
	{

		#region Constants: Private

		private const string WorkspaceId = "0";

		#endregion

		#region Fields: Private

		private readonly string _appUrl;
		private readonly string _userName;
		private readonly string _userPassword;
		private readonly bool _isNetCore;
		private readonly bool _useUntrustedSsl = true;
		private CookieContainer _authCookie;
		private string _oauthToken;

		#endregion

		#region Constructors: Private

		private CreatioClient(string appUrl, bool isNetCore = false){
			_appUrl = appUrl;
			_isNetCore = isNetCore;
		}

		#endregion

		#region Constructors: Public

		public CreatioClient(string appUrl, string userName, string userPassword, bool isNetCore = false){
			_appUrl = appUrl;
			_userName = userName;
			_userPassword = userPassword;
			_isNetCore = isNetCore;
		}

		public CreatioClient(string appUrl, string userName, string userPassword, bool useUntrustedSsl,
			bool isNetCore = false){
			_appUrl = appUrl;
			_userName = userName;
			_userPassword = userPassword;
			_useUntrustedSsl = useUntrustedSsl;
			_isNetCore = isNetCore;
		}

		#endregion

		#region Properties: Private

		private string LoginUrl => _appUrl + @"/ServiceModel/AuthService.svc/Login";

		private string PingUrl => _appUrl + @"/0/ping";

		#endregion

		#region Properties: Internal

		internal CookieContainer AuthCookie {
			get {
				InitAuthCookie();
				return _authCookie;
			}
		}

		#endregion

		#region Properties: Public

		public bool SkipPing { get; set; }

		#endregion

		#region Events: Public

		public event EventHandler<WsMessage> MessageReceived;

		public event EventHandler<WebSocketState> ConnectionStateChanged;

		#endregion

		#region Methods: Private

		private static async Task<string> GetAccessTokenByClientCredentials(string authApp, string clientId,
			string clientSecret){
			using (HttpClient client = new HttpClient()) {
				Dictionary<string, string> body = new Dictionary<string, string> {
					{"client_id", clientId},
					{"client_secret", clientSecret},
					{"grant_type", "client_credentials"}
				};
				HttpContent httpContent = new FormUrlEncodedContent(body);
				HttpResponseMessage response = await client.PostAsync(authApp, httpContent).ConfigureAwait(false);
				string content = await response.Content.ReadAsStringAsync();
				TokenResponse token = JsonConvert.DeserializeObject<TokenResponse>(content);
				return token.AccessToken;
			}
		}

		private void AddCsrfToken(HttpWebRequest request){
			Cookie cookie = request.CookieContainer.GetCookies(new Uri(_appUrl))["BPMCSRF"];
			if (cookie != null) {
				request.Headers.Add("BPMCSRF", cookie.Value);
			}
		}

		private void AddCsrfToken(HttpClient client){
			Cookie cookie = AuthCookie?.GetCookies(new Uri(_appUrl))["BPMCSRF"];
			if (cookie != null) {
				client.DefaultRequestHeaders.Add("BPMCSRF", cookie.Value);
			}
		}

		private void ApplyRequestData(HttpWebRequest request, string requestData = null){
			if (string.IsNullOrEmpty(requestData)) {
				request.ContentLength = 0;
				return;
			}
			byte[] bytes = Encoding.UTF8.GetBytes(requestData);
			request.ContentLength = bytes.Length;
			using (Stream dataStream = request.GetRequestStream()) {
				dataStream.Write(bytes, 0, bytes.Length);
			}
		}

		private string CreateConfigurationServiceUrl(string serviceName, string methodName){
			return $"{_appUrl}/{WorkspaceId}/rest/{serviceName}/{methodName}";
		}

		private HttpClientHandler CreateCreatioHandler(){
			HttpClientHandler handler = new HttpClientHandler();
			handler.ClientCertificateOptions = ClientCertificateOption.Manual;
			handler.ServerCertificateCustomValidationCallback
				= (httpRequestMessage, cert, certChail, sslPolicyErrors) => { return true; };
			return handler;
		}

		private HttpWebRequest CreateCreatioRequest(string url, string requestData = null, int requestTimeout = 100000){
			HttpWebRequest request = CreateRequest(url);
			if (_useUntrustedSsl) {
				request.ServerCertificateValidationCallback = (message, cert, chain, errors) => { return true; };
			}
			request.Timeout = requestTimeout;
			if (!string.IsNullOrEmpty(_oauthToken)) {
				request.Headers.Add("Authorization", "Bearer " + _oauthToken);
			} else {
				request.CookieContainer = AuthCookie;
				AddCsrfToken(request);
			}
			ApplyRequestData(request, requestData);
			return request;
		}

		private HttpWebRequest CreateRequest(string url){
			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
			if (_useUntrustedSsl) {
				request.ServerCertificateValidationCallback = (message, cert, chain, errors) => { return true; };
			}
			request.ContentType = "application/json; charset=utf-8";
			request.Method = "POST";
			request.KeepAlive = true;
			return request;
		}

		private void InitAuthCookie(int requestTimeout = 100000){
			if (_authCookie == null && string.IsNullOrEmpty(_oauthToken)) {
				if (SkipPing) {
					Login(requestTimeout);
				} else {
					Login(requestTimeout);
					TryPingApp(requestTimeout, 3);
				}
			}
		}

		private void PingApp(int pingTimeout){
			if (_isNetCore) {
				return;
			}
			HttpWebRequest pingRequest = CreateCreatioRequest(PingUrl, null, pingTimeout);
			_ = pingRequest.GetServiceResponse();
		}

		private void StartListeningSignalR(CancellationToken cancellationToken){
			Thread thread = new Thread(() => {
				IWsListener ws = new WsListenerSignalR(_appUrl, this, cancellationToken);
				ws.MessageReceived += (sender, message) => { MessageReceived?.Invoke(sender, message); };
				ws.ConnectionStateChanged += (sender, state) => { ConnectionStateChanged?.Invoke(sender, state); };
				ws.StartListening();
				ws.Dispose();
			});
			thread.Start();
		}

		private void StartListeningNetFrameworkApp(CancellationToken cancellationToken){
			Thread thread = new Thread(() => {
				IWsListener ws = new WsListenerNetFramework(_appUrl, this, cancellationToken);
				ws.MessageReceived += (sender, message) => { MessageReceived?.Invoke(sender, message); };
				ws.ConnectionStateChanged += (sender, state) => { ConnectionStateChanged?.Invoke(sender, state); };
				ws.StartListening();
				ws.Dispose();
			});
			thread.Start();
		}

		private bool TryPingApp(int pingTimeout, int attemtCount){
			for (int i = 0; i < attemtCount; i++) {
				try {
					PingApp(pingTimeout / attemtCount);
					return true;
				} catch {
					Thread.Sleep(1000);
				}
			}
			return false;
		}

		#endregion

		#region Methods: Protected

		protected virtual void OnMessageReceived(IEnumerable<WsMessage> messages){
			messages.ToList().ForEach(m => MessageReceived?.Invoke(this, m));
		}

		#endregion

		#region Methods: Public

		public static CreatioClient CreateOAuth20Client(string app, string authApp, string clientId,
			string clientSecret,
			bool isNetCore = false){
			CreatioClient client = new CreatioClient(app, isNetCore);
			client._oauthToken = GetAccessTokenByClientCredentials(authApp, clientId, clientSecret).Result;
			return client;
		}

		public string CallConfigurationService(string serviceName,
			string serviceMethod,
			string requestData,
			int requestTimeout = 100000){
			string executeUrl = CreateConfigurationServiceUrl(serviceName, serviceMethod);
			return ExecutePostRequest(executeUrl, requestData, requestTimeout);
		}

		public void DownloadFile(string url, string filePath, string requestData, int requestTimeout = 100000){
			HttpWebRequest request = CreateCreatioRequest(url, requestData, requestTimeout);
			request.SaveToFile(filePath);
		}

		public string ExecuteGetRequest(string url, int requestTimeout = 100000, int retryCount = 1, int delaySec = 1) {
			return Retry<string>(() => {
				HttpWebRequest request = CreateCreatioRequest(url, null, requestTimeout);
				request.Method = "GET";
				return request.GetServiceResponse();
			}, retryCount, delaySec);
		}

		public string ExecutePostRequest(string url, string requestData, int requestTimeout = 10000, int retryCount = 1, int delaySec = 1){
			return Retry<string>(() => {
				HttpClientHandler handler = CreateCreatioHandler();
				if (_oauthToken != null) {
					using (HttpClient client = new HttpClient(handler)) {
						client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _oauthToken);
						StringContent stringContent = new StringContent(requestData, Encoding.UTF8, "application/json");
						client.Timeout = new TimeSpan(0, 0, 0, 0, requestTimeout);
						HttpResponseMessage response = client.PostAsync(url, stringContent).Result;
						string content = response.Content.ReadAsStringAsync().Result;
						return content;
					}
				}
				handler.CookieContainer = AuthCookie;
				using (HttpClient client = new HttpClient(handler)) {
					AddCsrfToken(client);
					StringContent stringContent = new StringContent(requestData, Encoding.UTF8, "application/json");
					client.Timeout = new TimeSpan(0, 0, 0, 0, requestTimeout);
					HttpResponseMessage response = client.PostAsync(url, stringContent).Result;
					string content = response.Content.ReadAsStringAsync().Result;
					return content;
				}
			}, retryCount, delaySec);
		}

		static T Retry<T>(Func<T> func, int maxRetries, int delaySeconds) {
			int retries = 0;
			while (retries < maxRetries) {
				try {
					return func();
				} catch (Exception ex) {
					retries++;
					if (retries < maxRetries) {
						Thread.Sleep(delaySeconds * 1000);
					} else {
						throw;					}
				}
			}
			return default(T);
		}

		public void Login(){
			string authData = @"{
				""UserName"":""" + _userName + @""",
				""UserPassword"":""" + _userPassword + @"""
			}";
			HttpWebRequest request = CreateRequest(LoginUrl);
			_authCookie = new CookieContainer();
			request.CookieContainer = _authCookie;
			ApplyRequestData(request, authData);
			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) {
				if (response.StatusCode == HttpStatusCode.OK) {
					using (StreamReader reader = new StreamReader(response.GetResponseStream())) {
						string responseMessage = reader.ReadToEnd();
						if (responseMessage.Contains("\"Code\":1")) {
							throw new UnauthorizedAccessException($"Unauthorized {_userName} for {_appUrl}");
						}
					}
					string authCookieName = ".ASPXAUTH";
					string authCookieValue = response.Cookies[authCookieName].Value;
					_authCookie.Add(new Uri(_appUrl), new Cookie(authCookieName, authCookieValue));
				}
			}
		}

		public void Login(int requestTimeout){
			string authData = @"{
				""UserName"":""" + _userName + @""",
				""UserPassword"":""" + _userPassword + @"""
			}";
			HttpWebRequest request = CreateRequest(LoginUrl);
			request.Timeout = requestTimeout;
			_authCookie = new CookieContainer();
			request.CookieContainer = _authCookie;
			ApplyRequestData(request, authData);
			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) {
				if (response.StatusCode == HttpStatusCode.OK) {
					using (StreamReader reader = new StreamReader(response.GetResponseStream())) {
						string responseMessage = reader.ReadToEnd();
						if (responseMessage.Contains("\"Code\":1")) {
							throw new UnauthorizedAccessException($"Unauthorized {_userName} for {_appUrl}");
						}
					}
					string authCookieName = ".ASPXAUTH";
					string authCookieValue = response.Cookies[authCookieName].Value;
					_authCookie.Add(new Uri(_appUrl), new Cookie(authCookieName, authCookieValue));
				}
			}
		}

		public void StartListening(CancellationToken cancellationToken){
			if (_isNetCore) {
				StartListeningSignalR(cancellationToken);
			} else {
				StartListeningNetFrameworkApp(cancellationToken);
			}
		}

		public string UploadAlmFileByChunk(string url, string filePath) {
			FileInfo fileInfo = new FileInfo(filePath);
			Stream memStream = new MemoryStream();
			string result = "";
			using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read)) {
				int chunkSize = 1024*1024;
				byte[] buffer = new byte[chunkSize];
				int bytesRead = 0;
				var fileLenght = (int)fileInfo.Length;
				var downloadedSize = 0;
				while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0) {
					byte[] readedBytes = new byte[bytesRead];
					Array.Copy(buffer, readedBytes, bytesRead);
					result = UploadChunkAlmFile(url, readedBytes, downloadedSize, fileLenght);
					if (result.ToLower().Contains("\"success\": false")) {
						Console.WriteLine($"Error: {result}");
					};
					downloadedSize += bytesRead ;
					var leftByteSize = fileLenght - downloadedSize;
					chunkSize = leftByteSize < chunkSize ? leftByteSize : chunkSize;
					buffer = new byte[chunkSize];
					Console.WriteLine($"Loaded {downloadedSize} from {fileLenght}");
				}
			}
			return result;
		}

		public string UploadChunkAlmFile(string url, byte[] data, int downloadedSize, int totalSize) {
			HttpWebRequest request = CreateCreatioRequest(url);
			request.ContentType = "Content-Type: application/octet-stream";
			request.ContentLength = data.Length;
			int startByte = downloadedSize == 0 ? 0 : downloadedSize + 1;
			request.Headers.Add("Content-Range", $"bytes {startByte}-{downloadedSize + data.Length}/{totalSize}");
			using (Stream requestStream = request.GetRequestStream()) {
				requestStream.Write(data, 0, data.Length);
			}
			return request.GetServiceResponse();
		}

		public string UploadAlmFile(string url, string filePath){
			FileInfo fileInfo = new FileInfo(filePath);
			Stream memStream = new MemoryStream();
			using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read)) {
				byte[] buffer = new byte[1024];
				int bytesRead = 0;
				while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0) {
					memStream.Write(buffer, 0, bytesRead);
				}
			}
			string fileName = fileInfo.Name;
			HttpWebRequest request = CreateCreatioRequest(url);
			request.ContentType = "Content-Type: application/octet-stream";
			request.ContentLength = memStream.Length;
			using (Stream requestStream = request.GetRequestStream()) {
				memStream.Position = 0;
				byte[] tempBuffer = new byte[memStream.Length];
				memStream.Read(tempBuffer, 0, tempBuffer.Length);
				memStream.Close();
				requestStream.Write(tempBuffer, 0, tempBuffer.Length);
			}
			return request.GetServiceResponse();
		}

		public string UploadFile(string url, string filePath, int defaultTimeout = 100000){
			FileInfo fileInfo = new FileInfo(filePath);
			string fileName = fileInfo.Name;
			string boundary = DateTime.Now.Ticks.ToString("x");
			HttpWebRequest request = CreateCreatioRequest(url);
			request.ContentType = "multipart/form-data; boundary=" + boundary;
			Stream memStream = new MemoryStream();
			byte[] boundarybytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
			byte[] endBoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--");
			string headerTemplate =
				"Content-Disposition: form-data; name=\"{0}\"; filename=\"{1}\"\r\n" +
				"Content-Type: application/octet-stream\r\n\r\n";
			memStream.Write(boundarybytes, 0, boundarybytes.Length);
			string header = string.Format(headerTemplate, "files", fileName);
			byte[] headerbytes = Encoding.UTF8.GetBytes(header);
			memStream.Write(headerbytes, 0, headerbytes.Length);
			using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read)) {
				byte[] buffer = new byte[1024];
				int bytesRead = 0;
				while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0) {
					memStream.Write(buffer, 0, bytesRead);
				}
			}
			memStream.Write(endBoundaryBytes, 0, endBoundaryBytes.Length);
			request.ContentLength = memStream.Length;
			using (Stream requestStream = request.GetRequestStream()) {
				memStream.Position = 0;
				byte[] tempBuffer = new byte[memStream.Length];
				memStream.Read(tempBuffer, 0, tempBuffer.Length);
				memStream.Close();
				requestStream.Write(tempBuffer, 0, tempBuffer.Length);
			}
			return request.GetServiceResponse();
		}

		#endregion

	}

	#endregion
}