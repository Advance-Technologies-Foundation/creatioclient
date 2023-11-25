using System;
using System.Net.WebSockets;
using Creatio.Client.Dto;

namespace Creatio.Client
{
	internal interface IWsListener : IDisposable
	{

		#region Events: Public

		event EventHandler<WsMessage> MessageReceived;

		event EventHandler<WebSocketState> ConnectionStateChanged;

		#endregion

		#region Methods: Public

		void StartListening();

		#endregion

	}
}