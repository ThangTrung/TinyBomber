using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace GameServer
{
    public static class ThreadManager
    {
        // ===== CONSTANTS =====
        private const int TICKS_PER_SECOND = 30;
        private const int MS_PER_TICK = 1000 / TICKS_PER_SECOND; // ~33ms

        // ===== FIELDS =====
        private static List<Action> actionQueue = new List<Action>();   
        private static List<Action> nextQueue = new List<Action>();        
        private static object queueLock = new object();                     

        private static bool isInitialized = false;
        private static int totalActionsProcessed = 0;
        private static long frameCount = 0;
        internal static ActionBatchMetrics LastBatchMetrics { get; private set; }

        // ===== THREAD-SAFE INITIALIZATION =====
        public static void Initialize()
        {
            if (isInitialized)
            {
                Console.WriteLine("[WARNING] ThreadManager da khoi dong roi!");
                return;
            }

            isInitialized = true;
            totalActionsProcessed = 0;
            frameCount = 0;

            Console.WriteLine($"[INFO] ThreadManager initialized - {TICKS_PER_SECOND} ticks/sec ({MS_PER_TICK}ms per frame)");
        }

        // ===== PUBLIC METHODS =====
        public static void EnqueueAction(Action action)
        {
            if (!isInitialized)
            {
                Console.WriteLine("[ERROR] ThreadManager chua duoc khoi dong! Goi Initialize() truoc.");
                return;
            }

            if (action == null)
            {
                Console.WriteLine("[WARNING] Trying to enqueue null Action!");
                return;
            }

            lock (queueLock)
            {
                nextQueue.Add(action);
            }
        }

        public static void UpdateMain()
        {
            long actionStart = Stopwatch.GetTimestamp();
            long tickStartTime = GetCurrentTimeMs();

            lock (queueLock)
            {
                var temp = actionQueue;
                actionQueue = nextQueue;
                nextQueue = temp;
            }

            int actionsThisFrame = actionQueue.Count;
            int succeeded = 0;
            int failed = 0;

            foreach (var action in actionQueue)
            {
                try
                {
                    action?.Invoke();
                    totalActionsProcessed++;
                    succeeded++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"[ERROR] ThreadManager Exception: {ex.Message}");
                    Console.WriteLine($"[ERROR] StackTrace: {ex.StackTrace}");
                }
            }

            actionQueue.Clear();
            double actionMs = Stopwatch.GetElapsedTime(actionStart).TotalMilliseconds;
            double sleepMs = 0;

            long tickElapsedMs = GetCurrentTimeMs() - tickStartTime;
            int sleepTimeMs = (int)(MS_PER_TICK - tickElapsedMs);

            if (sleepTimeMs > 0)
            {
                long sleepStart = Stopwatch.GetTimestamp();
                Thread.Sleep(sleepTimeMs);
                sleepMs = Stopwatch.GetElapsedTime(sleepStart).TotalMilliseconds;
            }
            else if (sleepTimeMs < -5)
            {
                Console.WriteLine($"[WARNING] Frame {frameCount} exceeded budget: {tickElapsedMs}ms (target: {MS_PER_TICK}ms)");
            }

            frameCount++;
            LastBatchMetrics = new ActionBatchMetrics(actionMs, sleepMs, actionsThisFrame, succeeded, failed);

            if (frameCount % TICKS_PER_SECOND == 0 && frameCount > 0)
            {
                // Console.WriteLine($"[DEBUG] Frame {frameCount}: {actionsThisFrame} actions processed. Total: {totalActionsProcessed}");
            }
        }

        public static int GetPendingActionCount()
        {
            lock (queueLock)
            {
                return nextQueue.Count + actionQueue.Count;
            }
        }

        internal static int GetWaitingActionCount()
        {
            lock (queueLock) return nextQueue.Count;
        }

        public static int GetTotalActionsProcessed()
        {
            return totalActionsProcessed;
        }

        public static long GetFrameCount()
        {
            return frameCount;
        }

        public static void ResetStats()
        {
            lock (queueLock)
            {
                actionQueue.Clear();
                nextQueue.Clear();
            }

            totalActionsProcessed = 0;
            frameCount = 0;
        }

        private static long GetCurrentTimeMs()
        {
            return DateTime.Now.Ticks / 10000L;
        }
    }
}