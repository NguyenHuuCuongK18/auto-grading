using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Batches UI updates to reduce UI thread contention and improve responsiveness during grading.
    /// 
    /// This service collects update requests and processes them in batches at regular intervals,
    /// significantly reducing the number of dispatcher invocations and improving UI performance
    /// during parallel grading operations.
    /// 
    /// Key features:
    /// - Configurable batch interval (default: 250ms for balanced responsiveness)
    /// - Thread-safe operation
    /// - Automatic deduplication of pending updates
    /// - Flush support for immediate updates when needed
    /// 
    /// Performance impact:
    /// - Reduces UI update frequency from 100s/sec to 4/sec during batch grading
    /// - Maintains responsive UI while processing hundreds of log entries
    /// - Prevents UI freezing during parallel student grading
    /// </summary>
    public class UIUpdateBatcher : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly System.Threading.Timer _batchTimer;
        private readonly object _lock = new object();
        private readonly TimeSpan _batchInterval;
        
        // Pending actions to execute on next batch
        private readonly HashSet<Action> _pendingActions = new HashSet<Action>();
        
        // Separate queue for log updates to preserve order
        private readonly Queue<Action> _pendingLogUpdates = new Queue<Action>();
        
        private bool _disposed;
        
        /// <summary>
        /// Creates a new UI update batcher.
        /// </summary>
        /// <param name="dispatcher">The UI dispatcher to use for updates</param>
        /// <param name="batchIntervalMs">Batch interval in milliseconds (default: 250ms)</param>
        public UIUpdateBatcher(Dispatcher dispatcher, int batchIntervalMs = 250)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _batchInterval = TimeSpan.FromMilliseconds(batchIntervalMs);
            
            // Create timer that fires at the specified interval
            _batchTimer = new System.Threading.Timer(OnBatchTimerTick, null, _batchInterval, _batchInterval);
        }
        
        /// <summary>
        /// Queues a UI update action to be executed in the next batch.
        /// Duplicate actions are automatically deduplicated.
        /// </summary>
        /// <param name="action">Action to execute on UI thread</param>
        public void QueueUpdate(Action action)
        {
            if (_disposed || action == null) return;
            
            lock (_lock)
            {
                _pendingActions.Add(action);
            }
        }
        
        /// <summary>
        /// Queues a log update action to be executed in the next batch.
        /// Log updates maintain their order in the queue.
        /// </summary>
        /// <param name="action">Log update action to execute on UI thread</param>
        public void QueueLogUpdate(Action action)
        {
            if (_disposed || action == null) return;
            
            lock (_lock)
            {
                _pendingLogUpdates.Enqueue(action);
            }
        }
        
        /// <summary>
        /// Forces immediate execution of all pending updates.
        /// Use this when you need to ensure UI is up-to-date (e.g., before closing window).
        /// </summary>
        public void Flush()
        {
            if (_disposed) return;
            
            ProcessPendingUpdates();
        }
        
        /// <summary>
        /// Timer callback that processes batched updates.
        /// </summary>
        private void OnBatchTimerTick(object? state)
        {
            if (_disposed) return;
            
            ProcessPendingUpdates();
        }
        
        /// <summary>
        /// Processes all pending updates by dispatching them to the UI thread.
        /// </summary>
        private void ProcessPendingUpdates()
        {
            List<Action> actionsToExecute;
            List<Action> logUpdatesToExecute;
            
            // Copy pending actions under lock to minimize lock contention
            lock (_lock)
            {
                if (_pendingActions.Count == 0 && _pendingLogUpdates.Count == 0)
                    return;
                
                actionsToExecute = new List<Action>(_pendingActions);
                _pendingActions.Clear();
                
                logUpdatesToExecute = new List<Action>(_pendingLogUpdates);
                _pendingLogUpdates.Clear();
            }
            
            // Execute on UI thread with Render priority for good balance
            // This ensures updates are visible without blocking worker threads
            if (actionsToExecute.Count > 0 || logUpdatesToExecute.Count > 0)
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    // Execute regular updates first (status bar, buttons, etc.)
                    foreach (var action in actionsToExecute)
                    {
                        try
                        {
                            action();
                        }
                        catch
                        {
                            // Ignore exceptions from individual actions
                            // Don't let one failed update break the batch
                        }
                    }
                    
                    // Execute log updates last (less critical)
                    foreach (var action in logUpdatesToExecute)
                    {
                        try
                        {
                            action();
                        }
                        catch
                        {
                            // Ignore exceptions from individual actions
                        }
                    }
                }), DispatcherPriority.Render);
            }
        }
        
        /// <summary>
        /// Disposes the batcher and flushes any pending updates.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            
            // Stop the timer
            _batchTimer?.Dispose();
            
            // Flush any remaining updates
            ProcessPendingUpdates();
        }
    }
}
