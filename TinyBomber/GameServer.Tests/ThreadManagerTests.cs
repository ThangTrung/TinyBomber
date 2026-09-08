using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
using Xunit;

namespace GameServer.Tests
{
    public class ThreadManagerTests : IDisposable
    {
        public ThreadManagerTests()
        {
            ThreadManager.ResetStats();
        }

        public void Dispose()
        {
            ThreadManager.ResetStats();
        }

        [Fact]
        public void ThreadManager_Initialize_ShouldNotThrow()
        {
            ThreadManager.Initialize();
        }

        [Fact]
        public void ThreadManager_EnqueueAndExecute_ActionsExecuteInOrder()
        {
            var results = new List<int>();

            for (int i = 0; i < 10; i++)
            {
                int index = i;
                ThreadManager.EnqueueAction(() => results.Add(index));
            }

            ThreadManager.UpdateMain();

            Assert.Equal(10, results.Count);
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(i, results[i]); 
            }
        }

        [Fact]
        public void ThreadManager_MultipleFrames_AllActionsExecute()
        {
            int executedCount = 0;

            for (int i = 0; i < 5; i++)
            {
                ThreadManager.EnqueueAction(() => Interlocked.Increment(ref executedCount));
            }

            ThreadManager.UpdateMain();

            Assert.Equal(5, executedCount);

            for (int i = 0; i < 3; i++)
            {
                ThreadManager.EnqueueAction(() => Interlocked.Increment(ref executedCount));
            }

            ThreadManager.UpdateMain();

            Assert.Equal(8, executedCount);
        }

        [Fact]
        public void ThreadManager_NoRaceCondition_1000ConcurrentEnqueues()
        {
            const int actionCount = 1000;

            var threads = new List<Thread>();
            for (int i = 0; i < actionCount; i++)
            {
                var t = new Thread(() =>
                {
                    ThreadManager.EnqueueAction(() => { });
                });
                threads.Add(t);
                t.Start();
            }

            foreach (var t in threads)
            {
                t.Join(); 
            }

            ThreadManager.UpdateMain();

            Assert.Equal(actionCount, ThreadManager.GetTotalActionsProcessed());
        }

        [Fact]
        public void ThreadManager_ExceptionHandling_ContinuesOnException()
        {
            int executedAfterException = 0;

            ThreadManager.EnqueueAction(() => throw new InvalidOperationException("Test error"));
            ThreadManager.EnqueueAction(() => Interlocked.Increment(ref executedAfterException));

            ThreadManager.UpdateMain();

            Assert.Equal(1, executedAfterException);
        }

        [Fact]
        public void ThreadManager_FrameRateLimiting_Approximately30FPS()
        {
            int frameCount = 0;

            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < 30; i++)
            {
                ThreadManager.EnqueueAction(() => Interlocked.Increment(ref frameCount));
                ThreadManager.UpdateMain();
            }

            stopwatch.Stop();

            Assert.InRange(stopwatch.ElapsedMilliseconds, 700, 1500);
            Assert.Equal(30, frameCount);
        }

        [Fact]
        public void ThreadManager_PendingActionCount_ReturnsCorrectCount()
        {
            Assert.Equal(0, ThreadManager.GetPendingActionCount());

            ThreadManager.EnqueueAction(() => { });
            Assert.Equal(1, ThreadManager.GetPendingActionCount());

            ThreadManager.EnqueueAction(() => { });
            ThreadManager.EnqueueAction(() => { });
            Assert.Equal(3, ThreadManager.GetPendingActionCount());

            ThreadManager.UpdateMain();
            Assert.Equal(0, ThreadManager.GetPendingActionCount());
        }

        [Fact]
        public void ThreadManager_EnqueueNull_DoesNotThrow()
        {
            ThreadManager.EnqueueAction(null!); 
            ThreadManager.UpdateMain();
        }

        [Fact]
        public void ThreadManager_StressTest_LargeNumberOfActions()
        {
            const int actionCount = 10000;
            int executedCount = 0;

            for (int i = 0; i < actionCount; i++)
            {
                ThreadManager.EnqueueAction(() => Interlocked.Increment(ref executedCount));
            }

            int frameCounter = 0;
            while (ThreadManager.GetPendingActionCount() > 0 && frameCounter < 1000)
            {
                ThreadManager.UpdateMain();
                frameCounter++;
            }

            Assert.Equal(actionCount, executedCount);
            Assert.Equal(actionCount, ThreadManager.GetTotalActionsProcessed());
        }

        [Fact]
        public void ThreadManager_GetFrameCount_IncrementsCorrectly()
        {
            Assert.Equal(0, ThreadManager.GetFrameCount());

            ThreadManager.UpdateMain();
            Assert.Equal(1, ThreadManager.GetFrameCount());

            ThreadManager.UpdateMain();
            Assert.Equal(2, ThreadManager.GetFrameCount());

            ThreadManager.UpdateMain();
            Assert.Equal(3, ThreadManager.GetFrameCount());
        }

        [Fact]
        public void ThreadManager_ResetStats_ResetsCounters()
        {
            for (int i = 0; i < 5; i++)
            {
                ThreadManager.EnqueueAction(() => { });
            }
            ThreadManager.UpdateMain();

            Assert.Equal(5, ThreadManager.GetTotalActionsProcessed());
            Assert.Equal(1, ThreadManager.GetFrameCount());

            ThreadManager.ResetStats();

            Assert.Equal(0, ThreadManager.GetTotalActionsProcessed());
            Assert.Equal(0, ThreadManager.GetFrameCount());
        }
    }
}