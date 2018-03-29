﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class P2PSession : SessionBase, ISession
    {
        private readonly ILogger _logger;
        private readonly ISessionManager _sessionManager;
        private bool _sentHello;

        // TODO: initialize with capabilities and version
        public P2PSession(
            ISessionManager sessionManager,
            IMessageSerializationService serializationService,
            IPacketSender packetSender,
            PublicKey localNodeId,
            int listenPort,
            PublicKey remoteNodeId,
            ILogger logger)
        :base(serializationService, packetSender, remoteNodeId, logger)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            LocalNodeId = localNodeId;
            ListenPort = listenPort;
            AgreedCapabilities = new Dictionary<string, int>();
        }

        public int ListenPort { get; }

        public Dictionary<string, int> AgreedCapabilities { get; }

        public PublicKey LocalNodeId { get; }

        public string RemoteClientId { get; private set; }

        public void Init() // TODO: remote node details here?
        {
            _logger.Log($"P2P initiating outbound session");
            SendHello();
        }

        public void Close()
        {
        }

        public string ProtocolCode => Protocol.P2P;

        public int MessageIdSpaceSize => 16;

        public void HandleMessage(Packet msg)
        {
            if (msg.PacketType == P2PMessageCode.Hello)
            {
                HelloMessage helloMessage = Deserialize<HelloMessage>(msg.Data);
                HandleHello(helloMessage);
                
                foreach ((string protocol, int version) in AgreedCapabilities)
                {
                    _sessionManager.Start(protocol, version, PacketSender, RemoteNodeId, RemotePort);    
                } 
            }
            else if (msg.PacketType == P2PMessageCode.Disconnect)
            {
                DisconnectMessage disconnectMessage = Deserialize<DisconnectMessage>(msg.Data);
                _logger.Log($"Received disconnect ({disconnectMessage.Reason}) from {RemoteNodeId}:{RemotePort}");
                Close(disconnectMessage.Reason);
            }
            else if (msg.PacketType == P2PMessageCode.Ping)
            {
                _logger.Log($"Received PING from {RemoteNodeId}:{RemotePort}");
                HandlePing();
            }
            else if (msg.PacketType == P2PMessageCode.Pong)
            {
                _logger.Log($"Received PONG from {RemoteNodeId}:{RemotePort}");
                HandlePong();
            }
            else
            {
                _logger.Error($"Unhandled packet type: {msg.PacketType}");
            }
        }

        public void HandleHello(HelloMessage hello)
        {
            _logger.Log($"P2P received hello from {RemoteNodeId})");
            if (!hello.NodeId.Equals(RemoteNodeId))
            {
                throw new NodeDetailsMismatchException();
            }

            RemotePort = hello.ListenPort;
            RemoteClientId = hello.ClientId;

            if (!_sentHello)
            {
                _logger.Log($"P2P initiating inbound session from {hello.NodeId}:{hello.ListenPort} ({hello.ClientId})");
            }

            // TODO: temp
            if (hello.P2PVersion != NettyP2PHandler.Version)
            {
                Disconnect(DisconnectReason.IncompatibleP2PVersion);
                return;
            }

            if (!hello.Capabilities.ContainsKey(Protocol.Eth))
            {
                Disconnect(DisconnectReason.Other);
                return;
            }

            if (hello.Capabilities[Protocol.Eth] < 62 || hello.Capabilities[Protocol.Eth] > 63)
            {
                Disconnect(DisconnectReason.Other);
                return;
            }
            
            AgreedCapabilities.Add(Protocol.Eth, hello.Capabilities[Protocol.Eth]);
        }

        private void SendHello()
        {
            _logger.Log($"P2P sending hello");
            HelloMessage helloMessage = new HelloMessage
            {
                Capabilities = new Dictionary<string, int>
                {
                    {Protocol.Eth, 62}
                },

                ClientId = ClientVersion.Description,
                NodeId = LocalNodeId,
                ListenPort = ListenPort,
//                P2PVersion = NettyP2PHandler.Version
                P2PVersion = (byte)5
            };

            _sentHello = true;
            Send(helloMessage);
        }

        private void HandlePing()
        {
            _logger.Log($"P2P responding to ping from {RemoteNodeId}:{RemotePort} ({RemoteClientId})");
            Send(PongMessage.Instance);
        }

        
        private void Disconnect(DisconnectReason disconnectReason)
        {
            // TODO: advertise disconnect up the stack so we actually disconnect   
            _logger.Log($"P2P disconnecting from {RemoteNodeId}:{RemotePort} ({RemoteClientId}) [{disconnectReason}]");
            DisconnectMessage message = new DisconnectMessage(disconnectReason);
            Send(message);
        }

        public void Close(DisconnectReason disconnectReason)
        {
            _logger.Log($"P2P received disconnect from {RemoteNodeId}:{RemotePort} ({RemoteClientId}) [{disconnectReason}]");
        }

        private void HandlePong()
        {
            _logger.Log($"P2P pong from {RemoteNodeId}:{RemotePort} ({RemoteClientId})");
        }

        private void Ping()
        {
            _logger.Log($"P2P sending ping to {RemoteNodeId}:{RemotePort} ({RemoteClientId})");
            // TODO: timers
            Send(PingMessage.Instance);
        }
    }
}