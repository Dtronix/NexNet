using NexNet.Internals.Pipelines.Threading;
using NUnit.Framework;
using static NexNet.Internals.Pipelines.Threading.MutexSlim;

namespace NexNet.IntegrationTests.Sockets
{
    [TestFixture]
    [NonParallelizable]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    internal class MutexSlimTests
    {
        [SetUp]
        public void SetUp()
        {
#if DEBUG
            _timeoutMux.Logged += Log;
#endif
        }

        [TearDown]
        public void TearDown()
        {
#if DEBUG
            _timeoutMux.Logged -= Log;
#endif
            // Ensure mutex is released before next test
            // Wait briefly for any lingering operations to complete
            Thread.Sleep(50);
            // Try to acquire and immediately release to reset state
            if (_timeoutMux.IsAvailable)
            {
                return;
            }
            // Wait for mutex to become available (max 2 seconds)
            for (int i = 0; i < 20 && !_timeoutMux.IsAvailable; i++)
            {
                Thread.Sleep(100);
            }
        }

        private void Log(string message)
        {
            lock (this)
            {
                TestContext.Out.WriteLine(message);
            }
        }

        private readonly MutexSlim _zeroTimeoutMux = new MutexSlim(0),
            _timeoutMux = new MutexSlim(1000);

        private class DummySyncContext : SynchronizationContext
        {
            public Guid Id { get; }
            public DummySyncContext(Guid guid) => Id = guid;

            public static bool Is(Guid id) => Current is DummySyncContext dsc && dsc.Id == id;

            public override void Post(SendOrPostCallback d, object? state)
                => ThreadPool.QueueUserWorkItem(_ => Send(d, state), null);

            public override void Send(SendOrPostCallback d, object? state)
            {
                var original = Current;
                try
                {
                    SetSynchronizationContext(this);
                    d.Invoke(state);
                }
                finally
                {
                    SetSynchronizationContext(original);
                }
            }
        }

        [Test]
        public void CanObtain()
        {
            // can obtain when not contested (outer)
            // can not obtain when contested, even if re-entrant (inner)
            // can obtain after released (loop)
            for (int i = 0; i < 2; i++)
            {
                Assert.That(_zeroTimeoutMux.IsAvailable, Is.True);
                using var outer = _zeroTimeoutMux.TryWait();
                Assert.That(outer.Success, Is.True);
                Log(outer.ToString());
                Assert.That(_zeroTimeoutMux.IsAvailable, Is.False);
                using var inner = _zeroTimeoutMux.TryWait();
                Log(inner.ToString());
                Assert.That(inner.Success, Is.False);
            }
            Assert.That(_zeroTimeoutMux.IsAvailable, Is.True);

            for (int i = 0; i < 2; i++)
            {
                using var outer = _timeoutMux.TryWait();
                Assert.That(outer.Success, Is.True);
            }
        }

        [Test]
        public void ChangeStatePreservesCounter()
        {
            Assert.That(LockState.ChangeState(0xAAAA, LockState.Timeout), Is.EqualTo(0xAAA8));
            Assert.That(LockState.ChangeState(0xAAAA, LockState.Pending), Is.EqualTo(0xAAA9));
            Assert.That(LockState.ChangeState(0xAAAA, LockState.Success), Is.EqualTo(0xAAAA));
            Assert.That(LockState.ChangeState(0xAAAA, LockState.Canceled), Is.EqualTo(0xAAAB));
        }
        [Test]
        public void NextTokenIncrementsCorrectly()
        {
            // GetNextToken should always reset the 2 LSB (we'll test it with all 4 inputs), and increment the others
            int token = 0;
            token = LockState.GetNextToken(LockState.ChangeState(token, LockState.Timeout));
            Assert.That(token, Is.EqualTo(6)); // 000110
            token = LockState.GetNextToken(LockState.ChangeState(token, LockState.Pending));
            Assert.That(token, Is.EqualTo(10)); // 001010
            token = LockState.GetNextToken(LockState.ChangeState(token, LockState.Success));
            Assert.That(token, Is.EqualTo(14)); // 001110
            token = LockState.GetNextToken(LockState.ChangeState(token, LockState.Canceled));
            Assert.That(token, Is.EqualTo(18)); // 010010

            // and at wraparound, we expect zero again
            token = -1; // anecdotally: a cancelation, but that doesn't matter
            token = LockState.GetNextToken(token);
            Assert.That(token, Is.EqualTo(2)); // 000010
            token = LockState.GetNextToken(token);
            Assert.That(token, Is.EqualTo(6)); // 000110
        }

        [Test]
        public async Task CanObtainAsyncWithoutTimeout()
        {
            // can obtain when not contested (outer)
            // can not obtain when contested, even if re-entrant (inner)
            // can obtain after released (loop)
            // with no timeout: is always completed
            // with timeout: is completed on the success option

            for (int i = 0; i < 2; i++)
            {
                var awaitable1 = _zeroTimeoutMux.TryWaitAsync();
                Assert.That(awaitable1.IsCompleted, Is.True, nameof(awaitable1.IsCompleted));
                //Assert.That(awaitable1.CompletedSynchronously, Is.True, nameof(awaitable1.CompletedSynchronously));
                using var outer = await awaitable1;
                Assert.That(outer.Success, Is.True, nameof(outer.Success));

                var awaitable2 = _zeroTimeoutMux.TryWaitAsync();
                Assert.That(awaitable2.IsCompleted, Is.True, nameof(awaitable2.IsCompleted) + " inner");
                //Assert.That(awaitable2.CompletedSynchronously, Is.True, nameof(awaitable2.CompletedSynchronously) + " inner");
                using var inner = await awaitable2;
                Assert.That(inner.Success, Is.False, nameof(inner.Success) + " inner");
            }
        }

        [Test]
        public async Task CanObtainAsyncWithTimeout()
        {
            for (int i = 0; i < 2; i++)
            {
                var awaitable = _timeoutMux.TryWaitAsync();
                Assert.That(awaitable.IsCompleted, Is.True, nameof(awaitable.IsCompleted));
                //Assert.That(awaitable.CompletedSynchronously, Is.True, nameof(awaitable.CompletedSynchronously));
                using var outer = await awaitable;
                Assert.That(outer.Success, Is.True);
            }
        }

        [Test]
        public void Timeout()
        {
            object allReady = new object(), allDone = new object();
            const int COMPETITORS = 5;
            int active = 0, complete = 0, success = 0;

            Assert.That(_timeoutMux.TimeoutMilliseconds, Is.Not.EqualTo(0));
            lock (allDone)
            {
                using (var token = _timeoutMux.TryWait())
                {
                    lock (allReady)
                    {
                        for (int i = 0; i < COMPETITORS; i++)
                        {
                            Task.Run(() =>
                            {
                                lock (allReady)
                                {
                                    if (++active == COMPETITORS) Monitor.PulseAll(allReady);
                                    else Monitor.Wait(allReady);
                                }
                                using var inner = _timeoutMux.TryWait();
                                lock (allDone)
                                {
                                    if (inner) success++;
                                    if (++complete == COMPETITORS) Monitor.Pulse(allDone);
                                }
                                Thread.Sleep(10);
                            });
                        }
                        Monitor.Wait(allReady);
                    }
                    Thread.Sleep(_timeoutMux.TimeoutMilliseconds * 2);
                }
                Monitor.Wait(allDone);
                Assert.That(complete, Is.EqualTo(COMPETITORS));
                Assert.That(success, Is.EqualTo(0));
            }
        }

        [TestCase(WaitOptions.None)]
        [TestCase(WaitOptions.DisableAsyncContext)]
        public void CompetingCallerAllExecute(WaitOptions waitOptions)
        {
            object allReady = new object(), allDone = new object();
            const int COMPETITORS = 5;
            int active = 0, complete = 0, success = 0;
            lock (allDone)
            {
                using (var token = _timeoutMux.TryWait())
                {
                    lock (allReady)
                    {
                        for (int i = 0; i < COMPETITORS; i++)
                        {
                            Task.Run(() =>
                            {
                                lock (allReady)
                                {
                                    if (++active == COMPETITORS) Monitor.PulseAll(allReady);
                                    else Monitor.Wait(allReady);
                                }
                                using var inner = _timeoutMux.TryWait(waitOptions);
                                lock (allDone)
                                {
                                    if (inner) success++;
                                    if (++complete == COMPETITORS) Monitor.Pulse(allDone);
                                }
                                Thread.Sleep(10);
                            });
                        }
                        Monitor.Wait(allReady);
                    }
                    Thread.Sleep(100);
                }
                Monitor.Wait(allDone);
                Assert.That(complete, Is.EqualTo(COMPETITORS));
                Assert.That(success, Is.EqualTo(COMPETITORS));
            }
        }

        [TestCase(WaitOptions.None)]
        [TestCase(WaitOptions.DisableAsyncContext)]
        public async Task CompetingCallerAllExecuteAsync(WaitOptions waitOptions)
        {
            object allReady = new object(), allDone = new object();
            const int COMPETITORS = 5;
            int active = 0, success = 0, asyncOps = 0;

            var tasks = new Task[COMPETITORS];
            using (var token = await _timeoutMux.TryWaitAsync().ConfigureAwait(false))
            {
                lock (allReady)
                {
                    for (int i = 0; i < tasks.Length; i++)
                    {
                        int j = i;
                        tasks[i] = Task.Run(async () =>
                        {
                            lock (allReady)
                            {
                                if (++active == COMPETITORS)
                                {
                                    Log($"all tasks ready; releasing everyone");
                                    Monitor.PulseAll(allReady);
                                }
                                else
                                {
                                    Monitor.Wait(allReady);
                                }
                            }
                            var awaitable = _timeoutMux.TryWaitAsync(options: waitOptions);
                            if (!awaitable.IsCompleted)
                            {
                                Interlocked.Increment(ref asyncOps);
                            }
                            Log($"task {j} about to await...");
                            using var inner = await awaitable;
                            Log($"task {j} resumed; got lock: {inner.Success}");
                            lock (allDone)
                            {
                                if (inner) success++;
                                //if (!awaitable.CompletedSynchronously) asyncOps++;
                            }
                            await Task.Delay(10).ConfigureAwait(false);
                        });
                    }
                    Log($"outer lock waiting for everyone to be ready");
                    Monitor.Wait(allReady);
                }
                Log("delaying release...");
                await Task.Delay(100).ConfigureAwait(false);
                Log("about to release outer lock");
            }
            Log("outer lock released");
            for (int i = 0; i < tasks.Length; i++)
            {   // deliberately not an await - we want a simple timeout here
                Assert.That(tasks[i].Wait(_timeoutMux.TimeoutMilliseconds), Is.True, $"task {i} completes after {_timeoutMux.TimeoutMilliseconds}ms");
            }

            lock (allDone)
            {
                Assert.That(success, Is.EqualTo(COMPETITORS), "COMPETITORS == success");
                Assert.That(asyncOps, Is.EqualTo(COMPETITORS), "COMPETITORS == asyncOps");
            }
        }

        [Test]
        public async Task TimeoutAsync()
        {
            object allReady = new object(), allDone = new object();
            const int COMPETITORS = 5;
            int active = 0, success = 0, asyncOps = 0;

            var tasks = new Task[COMPETITORS];
            using (var token = await _timeoutMux.TryWaitAsync().ConfigureAwait(false))
            {
                lock (allReady)
                {
                    for (int i = 0; i < tasks.Length; i++)
                    {
                        tasks[i] = Task.Run(async () =>
                        {
                            lock (allReady)
                            {
                                if (++active == COMPETITORS) Monitor.PulseAll(allReady);
                                else Monitor.Wait(allReady);
                            }
                            var awaitable = _timeoutMux.TryWaitAsync();
                            if (!awaitable.IsCompleted)
                            {
                                Interlocked.Increment(ref asyncOps);
                            }
                            using var inner = await awaitable;
                            lock (allDone)
                            {
                                if (inner) success++;
                                //if (!awaitable.CompletedSynchronously) asyncOps++;
                            }
                            await Task.Delay(10).ConfigureAwait(false);
                        });
                    }
                    Monitor.Wait(allReady);
                }
                await Task.Delay(_timeoutMux.TimeoutMilliseconds * 2).ConfigureAwait(false);
            }
            for (int i = 0; i < tasks.Length; i++)
            {   // deliberately not an await - we want a simple timeout here
                Assert.That(tasks[i].Wait(_timeoutMux.TimeoutMilliseconds), Is.True);
            }

            lock (allDone)
            {
                Assert.That(success, Is.EqualTo(0));
                Assert.That(asyncOps, Is.EqualTo(COMPETITORS));
            }
        }

        [Test]
        public async Task PreCanceledReportsCorrectly()
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();

            var ct = _timeoutMux.TryWaitAsync(cancel.Token);
            Assert.That(ct.IsCompleted, Is.True, nameof(ct.IsCompleted));
            Assert.That(ct.IsCanceled, Is.True, nameof(ct.IsCanceled));
            Assert.That(ct.IsCompletedSuccessfully, Is.False, nameof(ct.IsCompletedSuccessfully));

            Assert.Throws<TaskCanceledException>(() => { var _ = ct.Result; });

            Assert.ThrowsAsync<TaskCanceledException>(async () => await ct);
        }

        [Test]
        public async Task DuringCanceledReportsCorrectly()
        {
            using var cancel = new CancellationTokenSource();

            // cancel it *after* issuing incomplete token

            ValueTask<LockToken> ct;
            using (var token = _timeoutMux.TryWait())
            {
                Assert.That(token.Success, Is.True);

                ct = _timeoutMux.TryWaitAsync(cancel.Token);
                Assert.That(ct.IsCompleted, Is.False, nameof(ct.IsCompleted));
                Assert.That(ct.IsCanceled, Is.False, nameof(ct.IsCanceled));
                Assert.That(ct.IsCompletedSuccessfully, Is.False, nameof(ct.IsCompletedSuccessfully));

                cancel.Cancel(); // cancel it *before* release; should be respected
            }
            Assert.That(ct.IsCompleted, Is.True, nameof(ct.IsCompleted));
            Assert.That(ct.IsCanceled, Is.True, nameof(ct.IsCanceled));
            Assert.That(ct.IsCompletedSuccessfully, Is.False, nameof(ct.IsCompletedSuccessfully));

            Assert.Throws<TaskCanceledException>(() => { var _ = ct.Result; });

            Assert.ThrowsAsync<TaskCanceledException>(async () => await ct);
        }

        [Test]
        public async Task PostCanceledReportsCorrectly()
        {
            using var cancel = new CancellationTokenSource();
            // cancel it *after* issuing incomplete token

            ValueTask<LockToken> ct;
            using (var token = _timeoutMux.TryWait())
            {
                Assert.That(token.Success, Is.True);

                ct = _timeoutMux.TryWaitAsync(cancel.Token);
                Assert.That(ct.IsCompleted, Is.False, nameof(ct.IsCompleted) + ":1");
                Assert.That(ct.IsCanceled, Is.False, nameof(ct.IsCanceled) + ":1");
                Assert.That(ct.IsCompletedSuccessfully, Is.False, nameof(ct.IsCompletedSuccessfully) + ":1");
            }
            // cancel it *after* release - should be ignored
            Assert.That(ct.IsCompleted, Is.True, nameof(ct.IsCompleted) + ":2");
            Assert.That(ct.IsCanceled, Is.False, nameof(ct.IsCanceled) + ":2");
            Assert.That(ct.IsCompletedSuccessfully, Is.True, nameof(ct.IsCompletedSuccessfully) + ":2");

            var result = await ct;
            Assert.That(result.Success, Is.True);
        }

        [Test]
        public async Task ManualCanceledReportsCorrectly()
        {
            ValueTask<LockToken> ct;
            using (var token = _timeoutMux.TryWait())
            using (var cancel = new CancellationTokenSource())
            {
                Assert.That(token.Success, Is.True);

                ct = _timeoutMux.TryWaitAsync(cancel.Token);
                Assert.That(ct.IsCompleted, Is.False, nameof(ct.IsCompleted));
                Assert.That(ct.IsCanceled, Is.False, nameof(ct.IsCanceled));
                Assert.That(ct.IsCompletedSuccessfully, Is.False, nameof(ct.IsCompletedSuccessfully));

                cancel.Cancel();
                //Assert.That(ct.TryCancel(), Is.True);
                //Assert.That(ct.TryCancel(), Is.True);
            }
            Assert.That(ct.IsCompleted, Is.True, nameof(ct.IsCompleted));
            Assert.That(ct.IsCanceled, Is.True, nameof(ct.IsCanceled));
            Assert.That(ct.IsCompletedSuccessfully, Is.False, nameof(ct.IsCompletedSuccessfully));

            Assert.Throws<TaskCanceledException>(() => { var _ = ct.Result; });

            Assert.ThrowsAsync<TaskCanceledException>(async () => await ct);
        }

        [Test]
        public async Task ManualCancelAfterAcquisitionDoesNothing()
        {
            using var cancel = new CancellationTokenSource();
            ValueTask<LockToken> ct;
            using (var token = _timeoutMux.TryWait())
            {
                Assert.That(token.Success, Is.True);

                ct = _timeoutMux.TryWaitAsync(cancel.Token);
                Assert.That(ct.IsCompleted, Is.False, nameof(ct.IsCompleted));
                Assert.That(ct.IsCanceled, Is.False, nameof(ct.IsCanceled));
                Assert.That(ct.IsCompletedSuccessfully, Is.False, nameof(ct.IsCompletedSuccessfully));
            }
            cancel.Cancel();
            //Assert.That(ct.TryCancel(), Is.False);
            //Assert.That(ct.TryCancel(), Is.False);

            Assert.That(ct.IsCompleted, Is.True, nameof(ct.IsCompleted));
            Assert.That(ct.IsCanceled, Is.False, nameof(ct.IsCanceled));
            Assert.That(ct.IsCompletedSuccessfully, Is.True, nameof(ct.IsCompletedSuccessfully));

            var result = await ct;
            Assert.That(result.Success, Is.True);
        }

        [Test]
        public async Task ManualCancelOnPreCanceledDoesNothing()
        {
            // cancel it *before* issuing token
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();

            var ct = _timeoutMux.TryWaitAsync(cancel.Token);
            Assert.That(ct.IsCompleted, Is.True, nameof(ct.IsCompleted));
            Assert.That(ct.IsCanceled, Is.True, nameof(ct.IsCanceled));
            Assert.That(ct.IsCompletedSuccessfully, Is.False, nameof(ct.IsCompletedSuccessfully));

            //Assert.That(ct.TryCancel(), Is.True);
            //Assert.That(ct.TryCancel(), Is.True);

            Assert.Throws<TaskCanceledException>(() => { var _ = ct.Result; });

            Assert.ThrowsAsync<TaskCanceledException>(async () => await ct);
        }

        [TestCase(1, 5000000)] // uncontested
        [TestCase(2, 2500000)] // duel
        [TestCase(10, 100000)] // battle royale
        public void DuelingThreadsShouldNotStall(int workerCount, int perWorker)
        {
#if DEBUG
            perWorker /= 1000;
#endif
            Volatile.Write(ref _failCount, 0);
            Volatile.Write(ref _successCount, 0);
            Array.Clear(_buckets, 0, _buckets.Length);
            Thread[] workers = new Thread[workerCount - 1];
#pragma warning disable IDE0039
            ThreadStart work = () => RunAcquireReleaseLoop(perWorker);
#pragma warning restore IDE0039
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(work)
                {
                    Priority = ThreadPriority.AboveNormal,
                    IsBackground = true,
                    Name = nameof(DuelingThreadsShouldNotStall)
                };
            }
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i].Start();
            }
            work(); // we are the final worker
            for (int i = 0; i < workers.Length; i++)
            {
                Assert.That(workers[i].Join(10000), Is.True, "failure to join worker " + i);
            }

            int failCount = Volatile.Read(ref _failCount);
            int successCount = Volatile.Read(ref _successCount);
            int maxTaken = Volatile.Read(ref _maxGetLock);
            Log($"success: {successCount}, failure: {failCount}, max get lock: {_maxGetLock}");
            Assert.That(failCount, Is.EqualTo(0));
            Assert.That(successCount, Is.EqualTo(workerCount * perWorker));
            int endBucket;
            for(endBucket = _buckets.Length - 1; endBucket >= 0; endBucket--)
            {
                if (_buckets[endBucket] != 0) break;
            }
            for(int i = 0; i <= endBucket; i++)
            {
                Log($"{i}ms: {Volatile.Read(ref _buckets[i])}");
            }
        }

        [TestCase(1, 3000000)] // uncontested
        [TestCase(2, 1500000)] // duel
        [TestCase(10, 150000)] // battle royale
        public async Task DuelingThreadsShouldNotStallAsync(int workerCount, int perWorker)
        {
#if DEBUG
            perWorker /= 1000;
#endif
            Volatile.Write(ref _failCount, 0);
            Volatile.Write(ref _successCount, 0);
            Array.Clear(_buckets, 0, _buckets.Length);
            Task[] workers = new Task[workerCount];
#pragma warning disable IDE0039
            Func<Task> work = () => RunAcquireReleaseLoopAsync(perWorker);
#pragma warning restore IDE0039
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(work);
            }
            var allDone = Task.WhenAll(workers);
            Assert.That(allDone.Wait(20000), Is.True, "failure to join");
            await allDone;

            int failCount = Volatile.Read(ref _failCount);
            int successCount = Volatile.Read(ref _successCount);
            int maxTaken = Volatile.Read(ref _maxGetLock);
            Log($"success: {successCount}, failure: {failCount}, max get lock: {_maxGetLock}");
            Assert.That(failCount, Is.EqualTo(0));
            Assert.That(successCount, Is.EqualTo(workerCount * perWorker));
            int endBucket;
            for (endBucket = _buckets.Length - 1; endBucket >= 0; endBucket--)
            {
                if (_buckets[endBucket] != 0) break;
            }
            for (int i = 0; i <= endBucket; i++)
            {
                Log($"{i}ms: {Volatile.Read(ref _buckets[i])}");
            }
        }

        const int BUCKET_COUNT = 50;
        readonly int[] _buckets = new int[BUCKET_COUNT];
        int _failCount, _successCount, _maxGetLock, _attempts;
        void RunAcquireReleaseLoop(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int startedTakingLock = Environment.TickCount;
                var attempt = Interlocked.Increment(ref _attempts);
                using var token = _timeoutMux.TryWait();
                var gotLock = Environment.TickCount;
                int taken = unchecked(gotLock - startedTakingLock), oldMax;
                int aggregate = taken < 0 ? 0 : taken >= BUCKET_COUNT ? (BUCKET_COUNT - 1) : taken;
                Interlocked.Increment(ref _buckets[aggregate]);
                do
                {
                    oldMax = Volatile.Read(ref _maxGetLock);
                } while (taken > oldMax && Interlocked.CompareExchange(ref _maxGetLock, taken, oldMax) != oldMax);

                if (token.Success) Interlocked.Increment(ref _successCount);
                else
                {
                    var nowAttempt = Volatile.Read(ref _attempts);
                    TestContext.Out.WriteLine($"failure: {token}, available: {_timeoutMux.IsAvailable}; attempts before: {attempt}, now: {nowAttempt}");
                    Interlocked.Increment(ref _failCount);
                    return; // give up promptly if we start failing
                }
                GC.KeepAlive(null);
            }
        }
        async Task RunAcquireReleaseLoopAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int startedTakingLock = Environment.TickCount;
                var attempt = Interlocked.Increment(ref _attempts);
                using var token = await _timeoutMux.TryWaitAsync();
                var gotLock = Environment.TickCount;
                int taken = unchecked(gotLock - startedTakingLock), oldMax;
                int aggregate = taken < 0 ? 0 : taken >= BUCKET_COUNT ? (BUCKET_COUNT - 1) : taken;
                Interlocked.Increment(ref _buckets[aggregate]);
                do
                {
                    oldMax = Volatile.Read(ref _maxGetLock);
                } while (taken > oldMax && Interlocked.CompareExchange(ref _maxGetLock, taken, oldMax) != oldMax);

                if (token.Success) Interlocked.Increment(ref _successCount);
                else
                {
                    var nowAttempt = Volatile.Read(ref _attempts);
                    TestContext.Out.WriteLine($"failure: {token}, available: {_timeoutMux.IsAvailable}; attempts before: {attempt}, now: {nowAttempt}");
                    Interlocked.Increment(ref _failCount);
                    return; // give up promptly if we start failing
                }
                GC.KeepAlive(null);
            }
        }
    }
}
