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

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amqp;
using Amqp.Framing;
using Amqp.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test.Amqp
{
    [TestClass]
    public class ProtocolTests
    {
        const int port = 5674;
        TestListener testListener;

        static ProtocolTests()
        {
            Trace.TraceLevel = TraceLevel.Frame;
            Trace.TraceListener = (f, a) => System.Diagnostics.Trace.WriteLine(System.DateTime.Now.ToString("[hh:mm:ss.fff]") + " " + string.Format(f, a));
        }

        [TestInitialize]
        public void Initialize()
        {
            this.testListener = new TestListener(new IPEndPoint(IPAddress.Any, port));
            this.testListener.Open();
        }

        [TestCleanup]
        public void Cleanup()
        {
            this.testListener.Close();
        }

        [TestMethod]
        public void CloseConnectionWithDetachTest()
        {
            this.testListener.RegisterTarget(TestPoint.Close, (stream, channel, fields) =>
                {
                    // send a detach
                    TestListener.FRM(stream, 0x16UL, 0, channel, 0u, true);
                    return TestOutcome.Continue;
                });

            string testName = "CloseConnectionWithDetachTest";
            Address address = new Address("amqp://localhost:" + port);

            Trace.WriteLine(TraceLevel.Information, "sync test");
            {
                Connection connection = new Connection(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                sender.Send(new Message("test") { Properties = new Properties() { MessageId = testName } });
                connection.Close();
                Assert.IsTrue(connection.Error == null, "connection has error!" + connection.Error);
            }

            Trace.WriteLine(TraceLevel.Information, "async test");
            Task.Run(async () =>
            {
                Connection connection = await Connection.Factory.CreateAsync(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                await sender.SendAsync(new Message("test") { Properties = new Properties() { MessageId = testName } });
                await connection.CloseAsync();
                Assert.IsTrue(connection.Error == null, "connection has error!" + connection.Error);

            }).Wait();
        }

        [TestMethod]
        public void CloseConnectionWithEndTest()
        {
            this.testListener.RegisterTarget(TestPoint.Close, (stream, channel, fields) =>
            {
                // send an end
                TestListener.FRM(stream, 0x17UL, 0, channel);
                return TestOutcome.Continue;
            });

            string testName = "CloseConnectionWithEndTest";
            Address address = new Address("amqp://localhost:" + port);

            Trace.WriteLine(TraceLevel.Information, "sync test");
            {
                Connection connection = new Connection(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                sender.Send(new Message("test") { Properties = new Properties() { MessageId = testName } });
                connection.Close();
                Assert.IsTrue(connection.Error == null, "connection has error!" + connection.Error);
            }

            Trace.WriteLine(TraceLevel.Information, "async test");
            Task.Run(async () =>
            {
                Connection connection = await Connection.Factory.CreateAsync(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                await sender.SendAsync(new Message("test") { Properties = new Properties() { MessageId = testName } });
                await connection.CloseAsync();
                Assert.IsTrue(connection.Error == null, "connection has error!" + connection.Error);

            }).Wait();
        }

        [TestMethod]
        public void CloseSessionWithDetachTest()
        {
            this.testListener.RegisterTarget(TestPoint.End, (stream, channel, fields) =>
            {
                // send a detach
                TestListener.FRM(stream, 0x16UL, 0, channel, 0u, true);
                return TestOutcome.Continue;
            });

            string testName = "CloseSessionWithDetachTest";
            Address address = new Address("amqp://localhost:" + port);

            Trace.WriteLine(TraceLevel.Information, "sync test");
            {
                Connection connection = new Connection(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                sender.Send(new Message("test") { Properties = new Properties() { MessageId = testName } });
                session.Close(0);
                connection.Close();
                Assert.IsTrue(connection.Error == null, "connection has error!" + connection.Error);
            }

            Trace.WriteLine(TraceLevel.Information, "async test");
            Task.Run(async () =>
            {
                Connection connection = await Connection.Factory.CreateAsync(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                await sender.SendAsync(new Message("test") { Properties = new Properties() { MessageId = testName } });
                session.Close(0);
                await connection.CloseAsync();
                Assert.IsTrue(connection.Error == null, "connection has error!" + connection.Error);

            }).Wait();
        }

        [TestMethod]
        public void SendWithConnectionResetTest()
        {
            this.testListener.RegisterTarget(TestPoint.Transfer, (stream, channel, fields) =>
            {
                stream.Dispose();
                return TestOutcome.Continue;
            });

            string testName = "SendWithConnectionResetTest";
            Address address = new Address("amqp://localhost:" + port);

            Trace.WriteLine(TraceLevel.Information, "sync test");
            {
                Connection connection = new Connection(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                try
                {
                    sender.Send(new Message("test") { Properties = new Properties() { MessageId = testName } });
                    Assert.IsTrue(false, "Send should throw exception");
                }
                catch (AmqpException exception)
                {
                    Assert.AreEqual(ErrorCode.ConnectionForced, (string)exception.Error.Condition);
                }
                connection.Close();
                Assert.AreEqual(ErrorCode.ConnectionForced, (string)connection.Error.Condition);
            }

            Trace.WriteLine(TraceLevel.Information, "async test");
            Task.Run(async () =>
            {
                Connection connection = await Connection.Factory.CreateAsync(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                try
                {
                    await sender.SendAsync(new Message("test") { Properties = new Properties() { MessageId = testName } });
                    Assert.IsTrue(false, "Send should throw exception");
                }
                catch (AmqpException exception)
                {
                    Assert.AreEqual(ErrorCode.ConnectionForced, (string)exception.Error.Condition);
                }
                await connection.CloseAsync();
                Assert.AreEqual(ErrorCode.ConnectionForced, (string)connection.Error.Condition);
            }).Wait();
        }

        [TestMethod]
        public void ClosedEventOnTransportResetTest()
        {
            this.testListener.RegisterTarget(TestPoint.Begin, (stream, channel, fields) =>
            {
                stream.Dispose();
                return TestOutcome.Continue;
            });

            Address address = new Address("amqp://localhost:" + port);

            Trace.WriteLine(TraceLevel.Information, "sync test");
            {
                ManualResetEvent closed = new ManualResetEvent(false);
                Connection connection = new Connection(address);
                connection.Closed += (o, e) => closed.Set();
                Session session = new Session(connection);
                Assert.IsTrue(closed.WaitOne(5000), "closed event not fired");
                Assert.AreEqual(ErrorCode.ConnectionForced, (string)connection.Error.Condition);
            }

            Trace.WriteLine(TraceLevel.Information, "async test");
            Task.Run(async () =>
            {
                TaskCompletionSource<object> tcs = new TaskCompletionSource<object>();
                Connection connection = await Connection.Factory.CreateAsync(address);
                connection.Closed += (s, a) => tcs.SetResult(null);
                Session session = new Session(connection);
                Task completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                Assert.IsTrue(completed == tcs.Task, "closed event not fired");
                Assert.AreEqual(ErrorCode.ConnectionForced, (string)connection.Error.Condition);
            }).Wait();
        }
        
        [TestMethod]
        public void SendWithInvalidRemoteChannelTest()
        {
            this.testListener.RegisterTarget(TestPoint.Transfer, (stream, channel, fields) =>
            {
                // send an end with invalid channel
                TestListener.FRM(stream, 0x17UL, 0, 33);
                return TestOutcome.Stop;
            });

            string testName = "SendWithProtocolErrorTest";
            Address address = new Address("amqp://localhost:" + port);

            Trace.WriteLine(TraceLevel.Information, "sync test");
            {
                Connection connection = new Connection(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                try
                {
                    sender.Send(new Message("test") { Properties = new Properties() { MessageId = testName } });
                    Assert.IsTrue(false, "Send should throw exception");
                }
                catch (AmqpException exception)
                {
                    Assert.AreEqual(ErrorCode.NotFound, (string)exception.Error.Condition);
                }
                connection.Close();
                Assert.AreEqual(ErrorCode.NotFound, (string)connection.Error.Condition);
            }

            Trace.WriteLine(TraceLevel.Information, "async test");
            Task.Run(async () =>
            {
                Connection connection = await Connection.Factory.CreateAsync(address);
                Session session = new Session(connection);
                SenderLink sender = new SenderLink(session, "sender-" + testName, "any");
                try
                {
                    await sender.SendAsync(new Message("test") { Properties = new Properties() { MessageId = testName } });
                    Assert.IsTrue(false, "Send should throw exception");
                }
                catch (AmqpException exception)
                {
                    Assert.AreEqual(ErrorCode.NotFound, (string)exception.Error.Condition);
                }
                await connection.CloseAsync();
                Assert.AreEqual(ErrorCode.NotFound, (string)connection.Error.Condition);
            }).Wait();
        }

        [TestMethod]
        public void ReceiveWithConnectionResetTest()
        {
            this.testListener.RegisterTarget(TestPoint.Flow, (stream, channel, fields) =>
            {
                stream.Dispose();
                return TestOutcome.Continue;
            });

            string testName = "ReceiveWithConnectionResetTest";
            Address address = new Address("amqp://localhost:" + port);

            Trace.WriteLine(TraceLevel.Information, "sync test");
            {
                Connection connection = new Connection(address);
                Session session = new Session(connection);
                ReceiverLink receiver = new ReceiverLink(session, "receiver-" + testName, "any");
                DateTime start = DateTime.UtcNow;
                Message message = receiver.Receive();
                Assert.IsTrue(message == null);
                Assert.IsTrue(DateTime.UtcNow.Subtract(start).TotalMilliseconds < 1000, "Receive call is not cancelled.");
                connection.Close();
                Assert.AreEqual(ErrorCode.ConnectionForced, (string)connection.Error.Condition);
            }

            Trace.WriteLine(TraceLevel.Information, "async test");
            Task.Run(async () =>
            {
                Connection connection = await Connection.Factory.CreateAsync(address);
                Session session = new Session(connection);
                ReceiverLink receiver = new ReceiverLink(session, "receiver-" + testName, "any");
                DateTime start = DateTime.UtcNow;
                Message message = await receiver.ReceiveAsync();
                Assert.IsTrue(message == null);
                Assert.IsTrue(DateTime.UtcNow.Subtract(start).TotalMilliseconds < 1000, "Receive call is not cancelled.");
                await connection.CloseAsync();
                Assert.AreEqual(ErrorCode.ConnectionForced, (string)connection.Error.Condition);
            }).Wait(); 
        }

        [TestMethod]
        public void ReceiveWithNoCreditTest()
        {
            this.testListener.RegisterTarget(TestPoint.Attach, (stream, channel, fields) =>
            {
                bool role = !(bool)fields[2];
                TestListener.FRM(stream, 0x12UL, 0, channel, fields[0], fields[1], role, fields[3], fields[4], new Source(), new Target());
                TestListener.FRM(stream, 0x14UL, 0, channel, fields[1], 0u, new byte[0], 0u, true, false);  // transfer
                return TestOutcome.Stop;
            });

            string testName = "ReceiveWithNoCreditTest";
            Address address = new Address("amqp://localhost:" + port);

            Trace.WriteLine(TraceLevel.Information, "sync test");
            {
                ManualResetEvent closed = new ManualResetEvent(false);
                Connection connection = new Connection(address);
                connection.Closed += (s, a) => closed.Set();
                Session session = new Session(connection);
                ReceiverLink receiver = new ReceiverLink(session, "receiver-" + testName, "any");
                Assert.IsTrue(closed.WaitOne(5000), "Connection not closed");
                Assert.AreEqual(ErrorCode.TransferLimitExceeded, (string)connection.Error.Condition);
            }

            Trace.WriteLine(TraceLevel.Information, "async test");
            Task.Run(async () =>
            {
                Connection connection = await Connection.Factory.CreateAsync(address);
                TaskCompletionSource<object> tcs = new TaskCompletionSource<object>();
                connection.Closed += (s, a) => tcs.SetResult(null);
                Session session = new Session(connection);
                ReceiverLink receiver = new ReceiverLink(session, "receiver-" + testName, "any");
                Task completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                Assert.IsTrue(completed == tcs.Task, "Connection not closed");
                Assert.AreEqual(ErrorCode.TransferLimitExceeded, (string)connection.Error.Condition);
            }).Wait();
        }

        [TestMethod]
        public void ConnectionEventsOnProtocolError()
        {
            ManualResetEvent closeReceived = null;
            ManualResetEvent closedNotified = null;

            this.testListener.RegisterTarget(TestPoint.Begin, (stream, channel, fields) =>
            {
                // begin with invalid remote channel
                TestListener.FRM(stream, 0x11UL, 0, channel, (ushort)2, 0u, 100u, 100u, 8u);
                return TestOutcome.Stop;
            });

            this.testListener.RegisterTarget(TestPoint.Close, (stream, channel, fields) =>
            {
                closeReceived.Set();
                return TestOutcome.Continue;
            });

            Address address = new Address("amqp://localhost:" + port);

            Trace.WriteLine(TraceLevel.Information, "sync test");
            {
                closeReceived = new ManualResetEvent(false);
                closedNotified = new ManualResetEvent(false);
                Connection connection = new Connection(address);
                connection.Closed += (o, e) => closedNotified.Set();
                Session session = new Session(connection);
                Assert.IsTrue(closeReceived.WaitOne(5000), "Close not received");
                Assert.IsTrue(closedNotified.WaitOne(5000), "Closed event not fired");
                Assert.AreEqual(ErrorCode.NotFound, (string)connection.Error.Condition);
            }

            Trace.WriteLine(TraceLevel.Information, "async test");
            Task.Run(async () =>
            {
                closeReceived = new ManualResetEvent(false);
                closedNotified = new ManualResetEvent(false);
                Connection connection = await Connection.Factory.CreateAsync(address);
                connection.Closed += (o, e) => closedNotified.Set();
                Session session = new Session(connection);
                Assert.IsTrue(closeReceived.WaitOne(5000), "Close not received");
                Assert.IsTrue(closedNotified.WaitOne(5000), "Closed event not fired");
                Assert.AreEqual(ErrorCode.NotFound, (string)connection.Error.Condition);
            }).Wait();
        }
    }
}
