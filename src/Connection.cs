//  ------------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation
//  All rights reserved. 
//  
//  Licensed under the Apache License, Version 2.0 (the ""License""); you may not use this 
//  file except in compliance with the License. You may obtain a copy of the License at 
//  http://www.apache.org/licenses/LICENSE-2.0  
//  
//  THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, 
//  EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR 
//  CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR 
//  NON-INFRINGEMENT. 
// 
//  See the Apache Version 2.0 License for specific language governing permissions and 
//  limitations under the License.
//  ------------------------------------------------------------------------------------

namespace Amqp
{
    using Amqp.Framing;
    using Amqp.Sasl;
    using Amqp.Types;
    using System;
    using System.Threading;

    public class Connection : AmqpObject
    {
        enum State
        {
            Start,
            HeaderSent,
            OpenPipe,
            OpenClosePipe,
            HeaderReceived,
            HeaderExchanged,
            OpenSent,
            OpenReceived,
            Opened,
            CloseReceived,
            ClosePipe,
            CloseSent,
            End
        }

        public static bool DisableServerCertValidation;

        internal const uint DefaultMaxFrameSize = 16 * 1024;
        internal const int MaxSessions = 4;
        const uint MaxIdleTimeout = 30 * 60 * 1000;
        static readonly TimerCallback onHeartBeatTimer = OnHeartBeatTimer;
        readonly Address address;
        readonly Session[] localSessions;
        readonly Session[] remoteSessions;
        State state;
        ITransport transport;
        uint maxFrameSize;
        Pump reader;
        Timer heartBeatTimer;

        Connection()
        {
            this.localSessions = new Session[MaxSessions];
            this.remoteSessions = new Session[MaxSessions];
            this.maxFrameSize = DefaultMaxFrameSize;
        }

        public Connection(Address address)
            : this(address, null)
        {
        }

        public Connection(Address address, SaslProfile saslProfile)
            : this()
        {
            this.address = address;
            this.Connect(saslProfile);
        }

#if DOTNET
        internal Connection(ConnectionFactory factory, Address address, IAsyncTransport transport)
            : this()
        {
            this.address = address;
            this.maxFrameSize = (uint)factory.amqpSettings.MaxFrameSize;
            this.transport = transport;
            transport.SetConnection(this);

            // after getting the transport, move state to open pipe before starting the pump
            this.SendHeader();
            this.SendOpen(factory.amqpSettings.ContainerId, factory.amqpSettings.HostName ?? this.address.Host);
            this.state = State.OpenPipe;
        }

        public static ConnectionFactory Factory
        {
            get { return new ConnectionFactory(); }
        }
#endif

        object ThisLock
        {
            get { return this; }
        }

        internal ushort AddSession(Session session)
        {
            lock (this.ThisLock)
            {
                this.ThrowIfClosed("AddSession");
                for (int i = 0; i < this.localSessions.Length; ++i)
                {
                    if (this.localSessions[i] == null)
                    {
                        this.localSessions[i] = session;
                        return (ushort)i;
                    }
                }

                throw new AmqpException(ErrorCode.NotAllowed,
                    Fx.Format(SRAmqp.AmqpHandleExceeded, MaxSessions));
            }
        }

        internal void SendCommand(ushort channel, DescribedList command)
        {
            this.ThrowIfClosed("Send");
            ByteBuffer buffer = Frame.Encode(FrameType.Amqp, channel, command);
            this.transport.Send(buffer);
            Trace.WriteLine(TraceLevel.Frame, "SEND (ch={0}) {1}", channel, command);
        }

        internal void SendCommand(ushort channel, Transfer transfer, ByteBuffer payload)
        {
            this.ThrowIfClosed("Send");
            int payloadSize;
            ByteBuffer buffer = Frame.Encode(FrameType.Amqp, channel, transfer, payload, (int)this.maxFrameSize, out payloadSize);
            this.transport.Send(buffer);
            Trace.WriteLine(TraceLevel.Frame, "SEND (ch={0}) {1} payload {2}", channel, transfer, payloadSize);
        }
       
        protected override bool OnClose(Error error = null)
        {
            lock (this.ThisLock)
            {
                State newState = State.Start;
                if (this.state == State.OpenPipe )
                {
                    newState = State.OpenClosePipe;
                }
                else if (state == State.OpenSent)
                {
                    newState = State.ClosePipe;
                }
                else if (this.state == State.Opened)
                {
                    newState = State.CloseSent;
                }
                else if (this.state == State.CloseReceived)
                {
                    newState = State.End;
                }
                else if (this.state == State.End)
                {
                    return true;
                }
                else
                {
                    throw new AmqpException(ErrorCode.IllegalState,
                        Fx.Format(SRAmqp.AmqpIllegalOperationState, "Close", this.state));
                }

                this.SendClose(error);
                this.state = newState;
                return this.state == State.End;
            }
        }

        static void OnHeartBeatTimer(object state)
        {
            var thisPtr = (Connection)state;
            byte[] frame = new byte[] { 0, 0, 0, 8, 2, 0, 0, 0 };
            thisPtr.transport.Send(new ByteBuffer(frame, 0, frame.Length, frame.Length));
            Trace.WriteLine(TraceLevel.Frame, "SEND (ch=0) empty");
        }

        void Connect(SaslProfile saslProfile)
        {
            ITransport transport;
            TcpTransport tcpTransport = new TcpTransport();
            tcpTransport.Connect(this, this.address, DisableServerCertValidation);
            transport = tcpTransport;

            if (saslProfile != null)
            {
                transport = saslProfile.Open(this.address.Host, transport);
            }
            else if (this.address.User != null)
            {
                transport = new SaslPlainProfile(this.address.User, this.address.Password).Open(this.address.Host, transport);
            }

            this.transport = transport;

            // after getting the transport, move state to open pipe before starting the pump
            this.SendHeader();
            this.SendOpen(Guid.NewGuid().ToString(), this.address.Host);
            this.state = State.OpenPipe;

            this.reader = new Pump(this);
            this.reader.Start();
        }

        void ThrowIfClosed(string operation)
        {
            if (this.state >= State.ClosePipe)
            {
                throw new AmqpException(ErrorCode.IllegalState,
                    Fx.Format(SRAmqp.AmqpIllegalOperationState, operation, this.state));
            }
        }

        void SendHeader()
        {
            byte[] header = new byte[] { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 0, 1, 0, 0 };
            this.transport.Send(new ByteBuffer(header, 0, header.Length, header.Length));
            Trace.WriteLine(TraceLevel.Frame, "SEND AMQP 0 1.0.0");
        }

        void SendOpen(string containerId, string hostName)
        {
            Open open = new Open()
            {
                ContainerId = containerId,
                HostName = hostName,
                MaxFrameSize = this.maxFrameSize,
                ChannelMax = MaxSessions - 1
            };

            this.SendCommand(0, open);
        }

        void SendClose(Error error)
        {
            this.SendCommand(0, new Close() { Error = error });
        }

        void OnOpen(Open open)
        {
            lock (this.ThisLock)
            {
                if (this.state == State.OpenSent)
                {
                    this.state = State.Opened;
                }
                else if (this.state == State.ClosePipe)
                {
                    this.state = State.CloseSent;
                }
                else
                {
                    throw new AmqpException(ErrorCode.IllegalState,
                        Fx.Format(SRAmqp.AmqpIllegalOperationState, "OnOpen", this.state));
                }
            }

            if (open.MaxFrameSize < this.maxFrameSize)
            {
                this.maxFrameSize = open.MaxFrameSize;
            }

            uint idleTimeout = open.IdleTimeOut;
            if (idleTimeout > 0 && idleTimeout < uint.MaxValue)
            {
                idleTimeout -= 3000;
                if (idleTimeout > MaxIdleTimeout)
                {
                    idleTimeout = MaxIdleTimeout;
                }

                this.heartBeatTimer = new Timer(onHeartBeatTimer, this, (int)idleTimeout, (int)idleTimeout);
            }
        }

        void OnClose(Close close)
        {
            lock (this.ThisLock)
            {
                if (this.state == State.Opened)
                {
                    this.SendClose(null);
                }
                else if (this.state == State.CloseSent)
                {
                }
                else
                {
                    throw new AmqpException(ErrorCode.IllegalState,
                        Fx.Format(SRAmqp.AmqpIllegalOperationState, "OnClose", this.state));
                }

                this.state = State.End;
                this.OnEnded(close.Error);
            }
        }

        internal virtual void OnBegin(ushort remoteChannel, Begin begin)
        {
            lock (this.ThisLock)
            {
                Session session = this.GetSession(this.localSessions, begin.RemoteChannel);
                session.OnBegin(remoteChannel, begin);
                this.remoteSessions[remoteChannel] = session;
            }
        }

        void OnEnd(ushort remoteChannel, End end)
        {
            Session session = this.GetSession(this.remoteSessions, remoteChannel);
            if (session.OnEnd(end))
            {
                lock (this.ThisLock)
                {
                    this.localSessions[session.Channel] = null;
                    this.remoteSessions[remoteChannel] = null;
                }
            }
        }

        void OnSessionCommand(ushort remoteChannel, DescribedList command, ByteBuffer buffer)
        {
            this.GetSession(this.remoteSessions, remoteChannel).OnCommand(command, buffer);
        }

        Session GetSession(Session[] sessions, ushort channel)
        {
            lock (this.ThisLock)
            {
                Session session = null;
                if (channel < sessions.Length)
                {
                    session = sessions[channel];
                }

                if (session == null)
                {
                    throw new AmqpException(ErrorCode.NotFound,
                        Fx.Format(SRAmqp.AmqpChannelNotFound, channel));
                }

                return session;
            }
        }

        internal bool OnHeader(ProtocolHeader header)
        {
            Trace.WriteLine(TraceLevel.Frame, "RECV AMQP {0}", header);
            lock (this.ThisLock)
            {
                if (this.state == State.OpenPipe)
                {
                    this.state = State.OpenSent;
                }
                else if (this.state == State.OpenClosePipe)
                {
                    this.state = State.ClosePipe;
                }
                else
                {
                    throw new AmqpException(ErrorCode.IllegalState,
                        Fx.Format(SRAmqp.AmqpIllegalOperationState, "OnHeader", this.state));
                }

                if (header.Major != 1 || header.Minor != 0 || header.Revision != 0)
                {
                    throw new AmqpException(ErrorCode.NotImplemented, header.ToString());
                }
            }

            return true;
        }

        internal bool OnFrame(ByteBuffer buffer)
        {
            bool shouldContinue = true;
            try
            {
                ushort channel;
                DescribedList command;
                Frame.GetFrame(buffer, out channel, out command);
                if (buffer.Length > 0)
                {
                    Trace.WriteLine(TraceLevel.Frame, "RECV (ch={0}) {1} payload {2}", channel, command, buffer.Length);
                }
                else
                {
                    Trace.WriteLine(TraceLevel.Frame, "RECV (ch={0}) {1}", channel, command);
                }

                if (command != null)
                {
                    if (command.Descriptor.Code == Codec.Open.Code)
                    {
                        this.OnOpen((Open)command);
                    }
                    else if (command.Descriptor.Code == Codec.Close.Code)
                    {
                        this.OnClose((Close)command);
                        shouldContinue = false;
                    }
                    else if (command.Descriptor.Code == Codec.Begin.Code)
                    {
                        this.OnBegin(channel, (Begin)command);
                    }
                    else if (command.Descriptor.Code == Codec.End.Code)
                    {
                        this.OnEnd(channel, (End)command);
                    }
                    else
                    {
                        this.OnSessionCommand(channel, command, buffer);
                    }
                }
            }
            catch (Exception exception)
            {
                this.OnException(exception);
                shouldContinue = false;
            }

            return shouldContinue;
        }

        void OnException(Exception exception)
        {
            Trace.WriteLine(TraceLevel.Error, "Exception occurred: {0}", exception.ToString());
            AmqpException amqpException = exception as AmqpException;
            Error error = amqpException != null ?
                amqpException.Error :
                new Error() { Condition = ErrorCode.InternalError, Description = exception.Message };

            if (this.state < State.ClosePipe)
            {
                try
                {
                    this.Close(0, error);
                }
                catch
                {
                    this.state = State.End;
                }
            }
            else
            {
                this.state = State.End;
            }

            if (this.state == State.End)
            {
                this.OnEnded(error);
            }
        }

        internal void OnIoException(Exception exception)
        {
            if (this.state != State.End)
            {
                this.state = State.End;
                this.OnEnded(new Error() { Condition = ErrorCode.ConnectionForced });
            }
        }

        void OnEnded(Error error)
        {
            if (this.heartBeatTimer != null)
            {
                this.heartBeatTimer.Dispose();
            }

            if (this.transport != null)
            {
                this.transport.Close();
            }

            this.NotifyClosed(error);
        }

        sealed class Pump
        {
            readonly Connection connection;

            public Pump(Connection connection)
            {
                this.connection = connection;
            }

            public void Start()
            {
                Fx.StartThread(this.PumpThread);
            }

            void PumpThread()
            {
                try
                {
                    ProtocolHeader header = Reader.ReadHeader(this.connection.transport);
                    this.connection.OnHeader(header);
                }
                catch (Exception exception)
                {
                    this.connection.OnIoException(exception);
                    return;
                }

                byte[] sizeBuffer = new byte[FixedWidth.UInt];
                while (sizeBuffer != null && this.connection.state != State.End)
                {
                    try
                    {
                        ByteBuffer buffer = Reader.ReadFrameBuffer(this.connection.transport, sizeBuffer, this.connection.maxFrameSize);
                        if (buffer != null)
                        {
                            this.connection.OnFrame(buffer);
                        }
                        else
                        {
                            sizeBuffer = null;
                        }
                    }
                    catch (Exception exception)
                    {
                        this.connection.OnIoException(exception);
                    }
                }
            }
        }
    }
}
