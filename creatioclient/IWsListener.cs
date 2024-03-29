﻿using System;
using System.Net.WebSockets;
using Creatio.Client.Dto;

namespace Creatio.Client
{
	internal interface IWsListener: IDisposable
	{

		event EventHandler<WsMessage> MessageReceived;

		event EventHandler<WebSocketState> ConnectionStateChanged;
		
		void StartListening();

	}
}