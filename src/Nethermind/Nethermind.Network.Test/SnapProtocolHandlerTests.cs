//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State.Snap;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class SnapProtocolHandlerTests
{
    private class Context
    {
        public ISession Session { get; set; } = Substitute.For<ISession>();

        private IMessageSerializationService _messageSerializationService;
        public IMessageSerializationService MessageSerializationService
        {
            get
            {
                if (_messageSerializationService == null)
                {
                    _messageSerializationService = new MessageSerializationService();
                    _messageSerializationService.Register(new AccountRangeMessageSerializer());
                }

                return _messageSerializationService;
            }
            set => _messageSerializationService = value;
        }

        public INodeStatsManager NodeStatsManager { get; set; } = Substitute.For<INodeStatsManager>();


        private SnapProtocolHandler _snapProtocolHandler;
        public SnapProtocolHandler SnapProtocolHandler {
            get => _snapProtocolHandler ??= new SnapProtocolHandler(
                Session,
                NodeStatsManager,
                MessageSerializationService,
                LimboLogs.Instance
            );
            set
            {
                _snapProtocolHandler = value;
            }
        }

        public TimeSpan SimulatedLatency { get; set; } = TimeSpan.Zero;

        private List<long> _recordedResponseBytesLength = new();
        public Context WithResponseBytesRecorder {
            get {
                Session
                    .When((ses) => ses.DeliverMessage(Arg.Any<P2PMessage>()))
                    .Do((callInfo) =>
                    {
                        GetAccountRangeMessage accountRangeMessage = (GetAccountRangeMessage)callInfo[0];
                        _recordedResponseBytesLength.Add(accountRangeMessage.ResponseBytes);

                        if (SimulatedLatency > TimeSpan.Zero)
                        {
                            Task.Delay(SimulatedLatency).Wait();
                        }

                        IByteBuffer buffer = MessageSerializationService.ZeroSerialize(new AccountRangeMessage()
                        {
                            PathsWithAccounts = new []{ new PathWithAccount(Keccak.Zero, Account.TotallyEmpty)}
                        });
                        buffer.ReadByte(); // Need to skip adaptive type

                        ZeroPacket packet = new(buffer);

                        packet.PacketType = SnapMessageCode.AccountRange;
                        SnapProtocolHandler.HandleMessage(packet);
                    });
                return this;
            }
        }

        public void RecordedMessageSizesShouldIncrease()
        {
            _recordedResponseBytesLength[^1].Should().BeGreaterThan(_recordedResponseBytesLength[^2]);
        }

        public void RecordedMessageSizesShouldDecrease()
        {
            _recordedResponseBytesLength[^1].Should().BeLessThan(_recordedResponseBytesLength[^2]);
        }

        public void RecordedMessageSizesShouldNotChange()
        {
            _recordedResponseBytesLength[^1].Should().Be(_recordedResponseBytesLength[^2]);
        }
    }

    [Test]
    public async Task Test_response_bytes_adjust_with_latency()
    {
        Context ctx = new Context()
            .WithResponseBytesRecorder;

        SnapProtocolHandler protocolHandler = ctx.SnapProtocolHandler;

        ctx.SimulatedLatency = TimeSpan.Zero;
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldIncrease();

        ctx.SimulatedLatency = SnapProtocolHandler.LowerLatencyThreshold + TimeSpan.FromMilliseconds(1);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldNotChange();

        ctx.SimulatedLatency = SnapProtocolHandler.UpperLatencyThreshold + TimeSpan.FromMilliseconds(1);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldDecrease();
    }

    [Test]
    [Explicit]
    public async Task Test_response_bytes_reset_on_error()
    {
        Context ctx = new Context()
            .WithResponseBytesRecorder;

        SnapProtocolHandler protocolHandler = ctx.SnapProtocolHandler;

        // Just setting baseline
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldIncrease();

        ctx.SimulatedLatency = Timeouts.Eth + TimeSpan.FromSeconds(1);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        ctx.SimulatedLatency = TimeSpan.Zero; // The read value is the request down, but it is adjusted on above request
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero,  Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldDecrease();
    }
}
