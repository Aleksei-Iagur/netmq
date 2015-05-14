﻿using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetMQ.Monitoring;
using NUnit.Framework;

#if !NET35
using System.Collections.Concurrent;
#endif

namespace NetMQ.Tests
{
    [TestFixture]
    public class NetMQPollerTest
    {
        #region Socket polling tests

        private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(10);

        [Test]
        public void ResponsePoll()
        {
            using (var context = NetMQContext.Create())
            using (var rep = context.CreateResponseSocket())
            using (var req = context.CreateRequestSocket())
            using (var poller = new NetMQPoller(context) { rep })
            {
                poller.PollTimeout = PollTimeout;

                int port = rep.BindRandomPort("tcp://127.0.0.1");

                req.Connect("tcp://127.0.0.1:" + port);

                rep.ReceiveReady += (s, e) =>
                {
                    bool more;
                    Assert.AreEqual("Hello", e.Socket.ReceiveFrameString(out more));
                    Assert.False(more);

                    e.Socket.Send("World");
                };

                poller.StartAsync();

                req.Send("Hello");

                bool more2;
                Assert.AreEqual("World", req.ReceiveFrameString(out more2));
                Assert.IsFalse(more2);

                poller.Stop();
            }
        }

        [Test]
        public void Monitoring()
        {
            var listeningEvent = new ManualResetEvent(false);
            var acceptedEvent = new ManualResetEvent(false);
            var connectedEvent = new ManualResetEvent(false);

            using (var context = NetMQContext.Create())
            using (var rep = context.CreateResponseSocket())
            using (var req = context.CreateRequestSocket())
            using (var poller = new NetMQPoller(context) { PollTimeout = PollTimeout })
            using (var repMonitor = new NetMQMonitor(context, rep, "inproc://rep.inproc", SocketEvent.Accepted | SocketEvent.Listening))
            using (var reqMonitor = new NetMQMonitor(context, req, "inproc://req.inproc", SocketEvent.Connected))
            {
                poller.PollTimeout = PollTimeout;

                repMonitor.Accepted += (s, e) => acceptedEvent.Set();
                repMonitor.Listening += (s, e) => listeningEvent.Set();

                repMonitor.AttachToPoller(poller);

                int port = rep.BindRandomPort("tcp://127.0.0.1");

                reqMonitor.Connected += (s, e) => connectedEvent.Set();

                reqMonitor.AttachToPoller(poller);

                poller.StartAsync();

                req.Connect("tcp://127.0.0.1:" + port);
                req.Send("a");

                rep.SkipFrame();

                rep.Send("b");

                req.SkipFrame();

                Assert.IsTrue(listeningEvent.WaitOne(300));
                Assert.IsTrue(connectedEvent.WaitOne(300));
                Assert.IsTrue(acceptedEvent.WaitOne(300));

                poller.Stop();
            }
        }

        [Test]
        public void AddSocketDuringWork()
        {
            using (var context = NetMQContext.Create())
            using (var router1 = context.CreateRouterSocket())
            using (var router2 = context.CreateRouterSocket())
            using (var dealer1 = context.CreateDealerSocket())
            using (var dealer2 = context.CreateDealerSocket())
            using (var poller = new NetMQPoller(context) { router1 })
            {
                poller.PollTimeout = PollTimeout;

                int port1 = router1.BindRandomPort("tcp://127.0.0.1");
                int port2 = router2.BindRandomPort("tcp://127.0.0.1");

                dealer1.Connect("tcp://127.0.0.1:" + port1);
                dealer2.Connect("tcp://127.0.0.1:" + port2);

                bool router1Arrived = false;
                bool router2Arrived = false;

                var signal1 = new ManualResetEvent(false);
                var signal2 = new ManualResetEvent(false);

                router1.ReceiveReady += (s, e) =>
                {
                    router1.SkipFrame();
                    router1.SkipFrame();
                    router1Arrived = true;
                    poller.Add(router2);
                    signal1.Set();
                };

                router2.ReceiveReady += (s, e) =>
                {
                    router2.SkipFrame();
                    router2.SkipFrame();
                    router2Arrived = true;
                    signal2.Set();
                };

                poller.StartAsync();

                dealer1.Send("1");
                Assert.IsTrue(signal1.WaitOne(300));
                dealer2.Send("2");
                Assert.IsTrue(signal1.WaitOne(300));

                poller.Stop();

                Assert.IsTrue(router1Arrived);
                Assert.IsTrue(router2Arrived);
            }
        }

        [Test]
        public void AddSocketAfterRemoving()
        {
            using (var context = NetMQContext.Create())
            using (var router1 = context.CreateRouterSocket())
            using (var router2 = context.CreateRouterSocket())
            using (var router3 = context.CreateRouterSocket())
            using (var dealer1 = context.CreateDealerSocket())
            using (var dealer2 = context.CreateDealerSocket())
            using (var dealer3 = context.CreateDealerSocket())
            using (var poller = new NetMQPoller(context) { router1, router2 })
            {
                poller.PollTimeout = PollTimeout;

                int port1 = router1.BindRandomPort("tcp://127.0.0.1");
                int port2 = router2.BindRandomPort("tcp://127.0.0.1");
                int port3 = router3.BindRandomPort("tcp://127.0.0.1");

                dealer1.Connect("tcp://127.0.0.1:" + port1);
                dealer2.Connect("tcp://127.0.0.1:" + port2);
                dealer3.Connect("tcp://127.0.0.1:" + port3);

                bool router1Arrived = false;
                bool router2Arrived = false;
                bool router3Arrived = false;

                var signal1 = new ManualResetEvent(false);
                var signal2 = new ManualResetEvent(false);
                var signal3 = new ManualResetEvent(false);

                router1.ReceiveReady += (s, e) =>
                {
                    router1Arrived = true;
                    router1.SkipFrame();
                    router1.SkipFrame();
                    poller.Remove(router1);
                    signal1.Set();
                };

                router2.ReceiveReady += (s, e) =>
                {
                    router2Arrived = true;
                    router2.SkipFrame();
                    router2.SkipFrame();
                    poller.Add(router3);
                    signal2.Set();
                };

                router3.ReceiveReady += (s, e) =>
                {
                    router3Arrived = true;
                    router3.SkipFrame();
                    router3.SkipFrame();
                    signal3.Set();
                };

                poller.StartAsync();

                dealer1.Send("1");
                Assert.IsTrue(signal1.WaitOne(300));
                dealer2.Send("2");
                Assert.IsTrue(signal2.WaitOne(300));
                dealer3.Send("3");
                Assert.IsTrue(signal3.WaitOne(300));

                poller.Stop();

                Assert.IsTrue(router1Arrived);
                Assert.IsTrue(router2Arrived);
                Assert.IsTrue(router3Arrived);
            }
        }

        [Test]
        public void AddTwoSocketAfterRemoving()
        {
            using (var context = NetMQContext.Create())
            using (var router1 = context.CreateRouterSocket())
            using (var router2 = context.CreateRouterSocket())
            using (var router3 = context.CreateRouterSocket())
            using (var router4 = context.CreateRouterSocket())
            using (var dealer1 = context.CreateDealerSocket())
            using (var dealer2 = context.CreateDealerSocket())
            using (var dealer3 = context.CreateDealerSocket())
            using (var dealer4 = context.CreateDealerSocket())
            using (var poller = new NetMQPoller(context) { router1, router2 })
            {
                poller.PollTimeout = PollTimeout;

                int port1 = router1.BindRandomPort("tcp://127.0.0.1");
                int port2 = router2.BindRandomPort("tcp://127.0.0.1");
                int port3 = router3.BindRandomPort("tcp://127.0.0.1");
                int port4 = router4.BindRandomPort("tcp://127.0.0.1");

                dealer1.Connect("tcp://127.0.0.1:" + port1);
                dealer2.Connect("tcp://127.0.0.1:" + port2);
                dealer3.Connect("tcp://127.0.0.1:" + port3);
                dealer4.Connect("tcp://127.0.0.1:" + port4);

                int router1Arrived = 0;
                int router2Arrived = 0;
                bool router3Arrived = false;
                bool router4Arrived = false;

                var signal1 = new ManualResetEvent(false);
                var signal2 = new ManualResetEvent(false);
                var signal3 = new ManualResetEvent(false);
                var signal4 = new ManualResetEvent(false);

                router1.ReceiveReady += (s, e) =>
                {
                    router1Arrived++;
                    router1.SkipFrame(); // identity
                    router1.SkipFrame(); // message
                    poller.Remove(router1);
                    signal1.Set();
                };

                router2.ReceiveReady += (s, e) =>
                {
                    router2Arrived++;
                    router2.SkipFrame(); // identity
                    router2.SkipFrame(); // message

                    if (router2Arrived == 1)
                    {
                        poller.Add(router3);
                        poller.Add(router4);
                        signal2.Set();
                    }
                };

                router3.ReceiveReady += (s, e) =>
                {
                    router3.SkipFrame(); // identity
                    router3.SkipFrame(); // message
                    router3Arrived = true;
                    signal3.Set();
                };

                router4.ReceiveReady += (s, e) =>
                {
                    router4.SkipFrame(); // identity
                    router4.SkipFrame(); // message
                    router4Arrived = true;
                    signal4.Set();
                };

                poller.StartAsync();

                dealer1.Send("1");
                Assert.IsTrue(signal1.WaitOne(300));
                dealer2.Send("2");
                Assert.IsTrue(signal2.WaitOne(300));
                dealer3.Send("3");
                dealer4.Send("4");
                dealer2.Send("2");
                dealer1.Send("1");
                Assert.IsTrue(signal3.WaitOne(300));
                Assert.IsTrue(signal4.WaitOne(300));

                poller.Stop();

                router1.SkipFrame();
                bool more;
                Assert.AreEqual("1", router1.ReceiveFrameString(out more));
                Assert.IsFalse(more);

                Assert.AreEqual(1, router1Arrived);
                Assert.AreEqual(2, router2Arrived);
                Assert.IsTrue(router3Arrived);
                Assert.IsTrue(router4Arrived);
            }
        }


        [Test]
        public void RemoveSocket()
        {
            using (var context = NetMQContext.Create())
            using (var router1 = context.CreateRouterSocket())
            using (var router2 = context.CreateRouterSocket())
            using (var router3 = context.CreateRouterSocket())
            using (var dealer1 = context.CreateDealerSocket())
            using (var dealer2 = context.CreateDealerSocket())
            using (var dealer3 = context.CreateDealerSocket())
            using (var poller = new NetMQPoller(context) { router1, router2, router3 })
            {
                poller.PollTimeout = PollTimeout;

                int port1 = router1.BindRandomPort("tcp://127.0.0.1");
                int port2 = router2.BindRandomPort("tcp://127.0.0.1");
                int port3 = router3.BindRandomPort("tcp://127.0.0.1");

                dealer1.Connect("tcp://127.0.0.1:" + port1);
                dealer2.Connect("tcp://127.0.0.1:" + port2);
                dealer3.Connect("tcp://127.0.0.1:" + port3);

                bool first = true;

                router1.ReceiveReady += (s, e) =>
                {
                    if (!first)
                        Assert.Fail("This should not happen because we cancelled the socket");
                    first = false;

                    // identity
                    e.Socket.SkipFrame();

                    bool more;
                    Assert.AreEqual("Hello", e.Socket.ReceiveFrameString(out more));
                    Assert.False(more);

                    // cancelling the socket
                    poller.Remove(e.Socket); // remove self
                };

                router2.ReceiveReady += (s, e) =>
                {
                    // identity
                    byte[] identity = e.Socket.ReceiveFrameBytes();

                    // message
                    e.Socket.SkipFrame();

                    e.Socket.SendMore(identity);
                    e.Socket.Send("2");
                };

                router3.ReceiveReady += (s, e) =>
                {
                    // identity
                    byte[] identity = e.Socket.ReceiveFrameBytes();

                    // message
                    e.Socket.SkipFrame();

                    e.Socket.SendMore(identity).Send("3");
                };

                Task pollerTask = Task.Factory.StartNew(poller.Start);

                // Send three messages. Only the first will be processed, as then handler removes
                // the socket from the poller.
                dealer1.Send("Hello");
                dealer1.Send("Hello2");
                dealer1.Send("Hello3");

                // making sure the socket defined before the one cancelled still works
                dealer2.Send("1");
                Assert.AreEqual("2", dealer2.ReceiveFrameString());

                // making sure the socket defined after the one cancelled still works
                dealer3.Send("1");
                Assert.AreEqual("3", dealer3.ReceiveFrameString());

                poller.Stop();
                Assert.IsTrue(pollerTask.IsCompleted);
            }
        }

        [Test]
        public void SimpleTimer()
        {
            // TODO it is not really clear what this test is actually testing -- maybe split it into a few smaller tests

            using (var context = NetMQContext.Create())
            using (var router = context.CreateRouterSocket())
            using (var dealer = context.CreateDealerSocket())
            using (var poller = new NetMQPoller(context) { router })
            {
                poller.PollTimeout = PollTimeout;

                int port = router.BindRandomPort("tcp://127.0.0.1");

                dealer.Connect("tcp://127.0.0.1:" + port);

                bool messageArrived = false;

                router.ReceiveReady += (s, e) =>
                {
                    Assert.IsFalse(messageArrived);
                    router.SkipFrame();
                    router.SkipFrame();
                    messageArrived = true;
                };

                bool timerTriggered = false;

                int count = 0;

                const int timerIntervalMillis = 100;

                var timer = new NetMQTimer(TimeSpan.FromMilliseconds(timerIntervalMillis));
                timer.Elapsed += (a, s) =>
                {
                    // the timer should jump before the message
                    Assert.IsFalse(messageArrived);
                    timerTriggered = true;
                    timer.Enable = false;
                    count++;
                };
                poller.Add(timer);

                poller.StartAsync();

                Thread.Sleep(150);

                dealer.Send("hello");

                Thread.Sleep(300);

                poller.Stop();

                Assert.IsTrue(messageArrived);
                Assert.IsTrue(timerTriggered);
                Assert.AreEqual(1, count);
            }
        }

        [Test]
        public void RemoveTimer()
        {
            using (var context = NetMQContext.Create())
            using (var router = context.CreateRouterSocket())
            using (var dealer = context.CreateDealerSocket())
            using (var poller = new NetMQPoller(context) { router })
            {
                poller.PollTimeout = PollTimeout;

                int port = router.BindRandomPort("tcp://127.0.0.1");

                dealer.Connect("tcp://127.0.0.1:" + port);

                bool timerTriggered = false;

                var timer = new NetMQTimer(TimeSpan.FromMilliseconds(100));
                timer.Elapsed += (a, s) => { timerTriggered = true; };

                // The timer will fire after 100ms
                poller.Add(timer);

                bool messageArrived = false;

                router.ReceiveReady += (s, e) =>
                {
                    router.SkipFrame();
                    router.SkipFrame();
                    messageArrived = true;
                    // Remove timer
                    poller.Remove(timer);
                };

                poller.StartAsync();

                Thread.Sleep(20);

                dealer.Send("hello");

                Thread.Sleep(300);

                poller.Stop();

                Assert.IsTrue(messageArrived);
                Assert.IsFalse(timerTriggered);
            }
        }

        [Test]
        public void RunMultipleTimes()
        {
            int count = 0;

            const int timerIntervalMillis = 20;

            var timer = new NetMQTimer(TimeSpan.FromMilliseconds(timerIntervalMillis));
            timer.Elapsed += (a, s) =>
            {
                count++;

                if (count == 3)
                {
                    timer.Enable = false;
                }
            };

            using (var context = NetMQContext.Create())
            using (var poller = new NetMQPoller(context) { timer })
            {
                poller.PollTimeout = PollTimeout;
                poller.StartAsync();

                Thread.Sleep(timerIntervalMillis * 6);

                poller.Stop();

                Assert.AreEqual(3, count);
            }
        }

/*
        NOTE PollOnce hasn't been ported from Poller to NetMQPoller. Is it needed?

        [Test]
        public void PollOnce()
        {
            int count = 0;

            var timer = new NetMQTimer(TimeSpan.FromMilliseconds(50));
            timer.Elapsed += (a, s) =>
            {
                count++;

                if (count == 3)
                {
                    timer.Enable = false;
                }
            };

            // NOTE if the PollTimeout here is less than the timer period, it won't fire during PollOnce -- is this by design?

            using (var context = NetMQContext.Create())
            using (var poller = new NetMQPoller(context) { timer })
            {
                poller.PollTimeout = TimeSpan.FromSeconds(1);

                var stopwatch = Stopwatch.StartNew();

                poller.PollOnce();

                var pollOnceElapsedTime = stopwatch.ElapsedMilliseconds;

                Assert.AreEqual(1, count, "the timer should have fired just once during the call to PollOnce()");
                Assert.Less(pollOnceElapsedTime, 90, "pollonce should return soon after the first timer firing.");
            }
        }
*/

        [Test]
        public void TwoTimers()
        {
            var timer1 = new NetMQTimer(TimeSpan.FromMilliseconds(52));
            var timer2 = new NetMQTimer(TimeSpan.FromMilliseconds(40));

            int count = 0;
            int count2 = 0;

            var signal1 = new ManualResetEvent(false);
            var signal2 = new ManualResetEvent(false);

            timer1.Elapsed += (a, s) =>
            {
                count++;
                timer1.Enable = false;
                timer2.Enable = false;
                signal1.Set();
            };

            timer2.Elapsed += (s, e) =>
            {
                count2++;
                signal2.Set();
            };

            using (var context = NetMQContext.Create())
            using (var poller = new NetMQPoller(context) { timer1, timer2 })
            {
                poller.PollTimeout = PollTimeout;
                poller.StartAsync();

                Assert.IsTrue(signal1.WaitOne(300));
                Assert.IsTrue(signal2.WaitOne(300));

                poller.Stop();
            }

            Assert.AreEqual(1, count);
            Assert.AreEqual(1, count2);
        }

        [Test]
        public void EnableTimer()
        {
            const int timerIntervalMillis = 20;

            var timer1 = new NetMQTimer(TimeSpan.FromMilliseconds(timerIntervalMillis));
            var timer2 = new NetMQTimer(TimeSpan.FromMilliseconds(timerIntervalMillis)) { Enable = false};

            int count = 0;
            int count2 = 0;

            timer1.Elapsed += (a, s) =>
            {
                count++;

                if (count == 1)
                {
                    timer2.Enable = true;
                    timer1.Enable = false;
                }
                else if (count == 2)
                {
                    timer1.Enable = false;
                }
            };

            timer2.Elapsed += (s, e) =>
            {
                timer1.Enable = true;
                timer2.Enable = false;

                count2++;
            };

            using (var context = NetMQContext.Create())
            using (var poller = new NetMQPoller(context) { timer1, timer2 })
            {
                poller.PollTimeout = PollTimeout;
                poller.StartAsync();

                Thread.Sleep(timerIntervalMillis * 6);

                poller.Stop();
            }

            Assert.AreEqual(2, count);
            Assert.AreEqual(1, count2);
        }

        [Test]
        public void ChangeTimerInterval()
        {
            int count = 0;

            const int timerIntervalMillis = 10;

            var timer = new NetMQTimer(TimeSpan.FromMilliseconds(timerIntervalMillis));

            var stopwatch = new Stopwatch();

            long length1 = 0;
            long length2 = 0;

            timer.Elapsed += (a, s) =>
            {
                count++;

                if (count == 1)
                {
                    stopwatch.Start();
                }
                else if (count == 2)
                {
                    length1 = stopwatch.ElapsedMilliseconds;

                    timer.Interval = 20;
                    stopwatch.Restart();
                }
                else if (count == 3)
                {
                    length2 = stopwatch.ElapsedMilliseconds;

                    stopwatch.Stop();

                    timer.Enable = false;
                }
            };

            using (var context = NetMQContext.Create())
            using (var poller = new NetMQPoller(context) { timer })
            {
                poller.PollTimeout = PollTimeout;
                poller.StartAsync();

                Thread.Sleep(timerIntervalMillis * 6);

                poller.Stop();
            }

            Assert.AreEqual(3, count);

            Assert.AreEqual(10.0, length1, 2.0);
            Assert.AreEqual(20.0, length2, 2.0);
        }

        [Test]
        public void TestPollerDispose()
        {
            const int timerIntervalMillis = 10;

            var timer = new NetMQTimer(TimeSpan.FromMilliseconds(timerIntervalMillis));

            var signal = new ManualResetEvent(false);

            var count = 0;

            timer.Elapsed += (a, s) =>
            {
                if (count++ == 5)
                    signal.Set();
            };

            NetMQPoller poller;
            using (var context = NetMQContext.Create())
            using (poller = new NetMQPoller(context) { timer })
            {
                poller.PollTimeout = PollTimeout;
                poller.StartAsync();
                Assert.IsTrue(signal.WaitOne(500));
                Assert.IsTrue(poller.IsStarted);
                Assert.Throws<InvalidOperationException>(() => poller.Start());
            }

            Assert.IsFalse(poller.IsStarted);
            Assert.Throws<ObjectDisposedException>(() => poller.Start());
            Assert.Throws<ObjectDisposedException>(() => poller.Stop());
            Assert.Throws<ObjectDisposedException>(() => poller.Add(timer));
            Assert.Throws<ObjectDisposedException>(() => poller.Remove(timer));
        }

        [Test]
        public void NativeSocket()
        {
            using (var context = NetMQContext.Create())
            using (var streamServer = context.CreateStreamSocket())
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                int port = streamServer.BindRandomPort("tcp://*");

                socket.Connect("127.0.0.1", port);

                var buffer = new byte[] { 1 };
                socket.Send(buffer);

                byte[] identity = streamServer.ReceiveFrameBytes();
                byte[] message = streamServer.ReceiveFrameBytes();

                Assert.AreEqual(buffer[0], message[0]);

                var socketSignal = new ManualResetEvent(false);

                using (var poller = new NetMQPoller(context) { PollTimeout = PollTimeout })
                {
                    poller.Add(socket, s =>
                    {
                        socket.Receive(buffer);

                        socketSignal.Set();

                        // removing the socket
                        poller.Remove(socket);
                    });

                    poller.StartAsync();

                    // no message is waiting for the socket so it should fail
                    Assert.IsFalse(socketSignal.WaitOne(100));

                    // sending a message back to the socket
                    streamServer.SendMore(identity).Send("a");

                    Assert.IsTrue(socketSignal.WaitOne(100));

                    socketSignal.Reset();

                    // sending a message back to the socket
                    streamServer.SendMore(identity).Send("a");

                    // we remove the native socket so it should fail
                    Assert.IsFalse(socketSignal.WaitOne(100));

                    poller.Stop();
                }
            }
        }

        #endregion

        #region Scheduling tests

#if !NET35
        [Test]
        public void OneTask()
        {
            bool triggered = false;

            using (var context = NetMQContext.Create())
            using (var poller = new NetMQPoller(context))
            {
                poller.StartAsync();

                var task = new Task(() => { triggered = true; });
                task.Start(poller);
                task.Wait();

                Assert.IsTrue(triggered);
            }
        }

        [Test]
        public void ContinueWith()
        {
            int threadId1 = 0;
            int threadId2 = 1;

            int runCount1 = 0;
            int runCount2 = 0;

            using (var context = NetMQContext.Create())
            using (var poller = new NetMQPoller(context))
            {
                poller.StartAsync();

                var task = new Task(() =>
                {
                    threadId1 = Thread.CurrentThread.ManagedThreadId;
                    runCount1++;
                });

                var task2 = task.ContinueWith(t =>
                {
                    threadId2 = Thread.CurrentThread.ManagedThreadId;
                    runCount2++;
                }, poller);

                task.Start(poller);
                task.Wait();
                task2.Wait();

                Assert.AreEqual(threadId1, threadId2);
                Assert.AreEqual(1, runCount1);
                Assert.AreEqual(1, runCount2);
            }
        }

        [Test]
        public void TwoThreads()
        {
            int count1 = 0;
            int count2 = 0;

            var allTasks = new ConcurrentBag<Task>();

            using (var context = NetMQContext.Create())
            using (var poller = new NetMQPoller(context))
            {
                poller.StartAsync();

                Task t1 = Task.Factory.StartNew(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        var task = new Task(() => { count1++; });
                        allTasks.Add(task);
                        task.Start(poller);
                    }
                });

                Task t2 = Task.Factory.StartNew(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        var task = new Task(() => { count2++; });
                        allTasks.Add(task);
                        task.Start(poller);
                    }
                });

                t1.Wait(1000);
                t2.Wait(1000);
                Task.WaitAll(allTasks.ToArray(), 1000);

                Assert.AreEqual(100, count1);
                Assert.AreEqual(100, count2);
            }
        }
#endif

        #endregion
    }
}
