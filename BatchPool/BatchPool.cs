﻿using System.Collections.Concurrent;
using BatchPool.Tasks;
using BatchPool.Tasks.BatchTasks;
using BatchPool.Tasks.Callbacks;

namespace BatchPool
{
    /// <summary>
    /// Run tasks in a guaranteed order
    /// </summary>
    public class BatchPool
    {
        // External Config

        /// <summary>
        /// Max number of tasks that will run concurrently.
        /// </summary>
        private int _batchSize;
        /// <summary>
        /// Is the BatchPool enabled from an external consumer perspective.
        /// </summary>
        private bool _isEnabled;
        /// <summary>
        /// If enabled and a running task is added to the BatchPool, an Exception will be thrown.
        /// </summary>
        private readonly bool _inactiveTaskValidation;
        /// <summary>
        /// A Cancellation Token that will permanently cancel the BatchPool.
        /// </summary>
        private readonly CancellationToken _cancellationToken;

        // Helpers

        /// <summary>
        /// Used to limit the number of concurrent tasks based on _batchSize />.
        /// </summary>
        private SemaphoreSlim _batchRateLimitSemaphore;
        /// <summary>
        /// Used to pause all activity in the BatchPool to change the size of _batchSize />.
        /// </summary>
        private readonly SemaphoreSlim _batchUpdateSemaphore;
        /// <summary>
        /// Tracks pending tasks (and running tasks if _inactiveTaskValidation is disabled) that have not be transitioned to the running tasks list _runningTasks />.
        /// </summary>
        private readonly ConcurrentQueue<BatchTask> _unprocessedTasks;
        /// <summary>
        /// Tracks running tasks so they can be awaited or cancelled as required.
        /// </summary>
        private readonly HashSet<BatchTask> _runningTasks;

        // Internal Stateful fields

        /// <summary>
        /// Are tasks actively being processed. The BatchPool controls this field to perform maintenance.
        /// </summary>
        private bool _isRunning;
        /// <summary>
        /// Is the BatchPool halted (waiting for all current tasks to finish in order to update the batch size).
        /// </summary>
        private bool _isUpdatingBatchSize;

        /// <summary>
        /// Create a BatchPool.
        /// </summary>
        /// <param name="batchSize">Max number of tasks that will run concurrently.</param>
        /// <param name="isEnabled">Is the BatchPool enabled by default. If false, the BatchPool must be started manually.</param>
        /// <param name="inactiveTaskValidation">If enabled and a running task is added to the BatchPool, an Exception will be thrown</param>
        /// <param name="cancellationToken">A Cancellation Token that will permanently cancel the BatchPool.</param>
        public BatchPool(int batchSize, bool isEnabled = true, bool inactiveTaskValidation = false, CancellationToken cancellationToken = default)
        {
            // Init config
            _batchSize = batchSize;
            _isEnabled = isEnabled;
            _inactiveTaskValidation = inactiveTaskValidation;
            _cancellationToken = cancellationToken;

            // Init Helpers
            _batchRateLimitSemaphore = new(batchSize, batchSize);
            _batchUpdateSemaphore = new(1, 1);
            _unprocessedTasks = new();
            _runningTasks = new();
        }

        /// <summary>
        /// Resume processing tasks in the background. If already running, nothing will change.
        /// </summary>
        public void ResumeAndForget()
        {
            _isEnabled = true;
            _ = RunBatchPool().ConfigureAwait(false);
        }

        /// <summary>
        /// Resume processing tasks and wait for all tasks that exist in the BatchPool at the time of calling this method. If already running, nothing will change.
        /// </summary>
        /// <returns></returns>
        public async Task ResumeAndWaitForAllAsync()
        {
            _isEnabled = true;
            _ = RunBatchPool()
                .ConfigureAwait(false);
            await WaitForAllAsync();
        }

        /// <summary>
        /// Prevent new tasks being processed. Pending tasks will wait until the BatchPool is resumed.
        /// </summary>
        public void Pause() => _isEnabled = false;

        /// <summary>
        /// Returns the number of pending tasks that have not yet started processing.
        /// </summary>
        public int GetPendingTaskCount() => _unprocessedTasks.Count(a => !a.IsCancelled);

        /// <summary>
        /// Wait for all tasks that exist in the BatchPool at the time of calling this method.
        /// </summary>
        public async Task WaitForAllAsync()
        {
            List<BatchTask> pendingTasks;

            lock (_runningTasks)
            {
                pendingTasks = _runningTasks.ToList();
            }

            lock (_unprocessedTasks)
            {
                pendingTasks.AddRange(_unprocessedTasks.ToArray());
            }

            await WaitForAllAsync(pendingTasks);
        }

        /// <summary>
        /// Wait for all tasks that exist in the BatchPool at the time of calling this method.
        /// </summary>
        /// <param name="timeoutInMilliseconds">Max time to wait for all tasks to finish.</param>
        /// <returns>Returns true if all tasks are completed before the timout occurs, false if the timeout occurs</returns>
        public async Task<bool> WaitForAllAsync(uint timeoutInMilliseconds) => 
            await WaitForAllAsync()
            .AwaitWithTimeout(timeoutInMilliseconds)
            .ConfigureAwait(false);

        /// <summary>
        /// Wait for all tasks that exist in the BatchPool at the time of calling this method.
        /// </summary>
        /// <param name="timeout">Max time to wait for all tasks to finish.</param>
        /// <returns>Returns true if all tasks are completed before the timout occurs, false if the timeout occurs</returns>
        public async Task<bool> WaitForAllAsync(TimeSpan timeout) => 
            await WaitForAllAsync()
            .AwaitWithTimeout(timeout)
            .ConfigureAwait(false);

        /// <summary>
        /// Wait for all tasks that exist in the BatchPool at the time of calling this method.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to cancel waiting for all tasks to complete.</param>
        /// <returns>Returns true if all tasks are completed, false if the cancellation token is cancelled.</returns>
        public async Task<bool> WaitForAllAsync(CancellationToken cancellationToken) => 
            await WaitForAllAsync()
            .AwaitWithTimeout(timeoutInMilliseconds: null, cancellationToken)
            .ConfigureAwait(false);

        /// <summary>
        /// <returns>Returns true if all tasks are completed before the timout occurs, false if the timeout occurs</returns>
        /// </summary>
        /// <param name="timeoutInMilliseconds">Max time to wait for all tasks to finish.</param>
        /// <param name="cancellationToken">Cancellation token to cancel waiting for all tasks to complete.</param>
        /// <returns>Returns true if all tasks are completed before the timout occurs, false if the timeout occurs or the cancellation token is cancelled.</returns>
        public async Task<bool> WaitForAllAsync(uint timeoutInMilliseconds, CancellationToken cancellationToken) => 
            await WaitForAllAsync(cancellationToken)
            .AwaitWithTimeout(timeoutInMilliseconds, cancellationToken)
            .ConfigureAwait(false);

        /// <summary>
        /// Wait for all tasks that exist in the BatchPool at the time of calling this method.
        /// </summary>
        /// <param name="timeout">Max time to wait for all tasks to finish.</param>
        /// <param name="cancellationToken">Cancellation token to cancel waiting for all tasks to complete.</param>
        /// <returns>Returns true if all tasks are completed before the timout occurs, false if the timeout occurs or the cancellation token is cancelled.</returns>
        public async Task<bool> WaitForAllAsync(TimeSpan timeout, CancellationToken cancellationToken) => 
            await WaitForAllAsync(cancellationToken)
            .AwaitWithTimeout(timeout, cancellationToken)
            .ConfigureAwait(false);

        /// <summary>
        /// Wait for all tasks to finish.
        /// </summary>
        /// <param name="tasks">Tasks that will be waited for the finish.</param>
        public static async Task WaitForAllAsync(IEnumerable<BatchTask> tasks)
        {
            foreach (BatchTask pendingTask in tasks)
            {
                // This null check can prevent race conditions in rare scenarios.
                if (pendingTask == null)
                {
                    continue;
                }

                await pendingTask
                    .WaitForTaskAsync()
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Update the size of the BatchPool. Waits for all currently running tasks to finish before updating the BatchPool. Internal logic will prevent running this method concurrently.
        /// </summary>
        /// <param name="newBatchSize">The new batch size, this controls the number of tasks that can be run concurrently. Must be more than 0.</param>
        /// <exception cref="ArgumentException">Throws an ArgumentException if <paramref name="newBatchSize" /> is less than 1.</exception>
        public async Task<bool> UpdateCapacityAsync(int newBatchSize)
        {
            if (newBatchSize < 1)
            {
                throw new ArgumentException($"The provided parameter '{nameof(newBatchSize)}' less than 1");
            }

            bool didObtainSemaphore = false;

            try
            {
                lock (_batchUpdateSemaphore)
                {
                    if (_isUpdatingBatchSize)
                    {
                        return false;
                    }

                    _isUpdatingBatchSize = true;
                }

                await _batchUpdateSemaphore
                    .WaitAsync(_cancellationToken)
                    .ConfigureAwait(false);
                didObtainSemaphore = true;

                int numberOfThreadsObtained = 0;

                // Wait for all threads in the current semaphore to be released.
                while (numberOfThreadsObtained != _batchSize)
                {
                    await _batchRateLimitSemaphore
                        .WaitAsync(_cancellationToken)
                        .ConfigureAwait(false);

                    numberOfThreadsObtained++;
                }

                _batchRateLimitSemaphore = new(newBatchSize, newBatchSize);
                _batchSize = newBatchSize;

                return true;
            }
            finally
            {
                lock (_batchUpdateSemaphore)
                {
                    _isUpdatingBatchSize = false;
                }

                if (didObtainSemaphore)
                {
                    _batchUpdateSemaphore.Release();
                }
            }
        }

        /// <summary>
        /// Update the size of the BatchPool in the background. Waits for all currently running tasks to finish before updating the BatchPool. Internal logic will prevent running this method concurrently.
        /// </summary>
        /// <param name="newBatchSize">The new batch size, this controls the number of tasks that can be run concurrently. Must be more than 0.</param>
        /// <exception cref="ArgumentException">Throws an ArgumentException if <paramref name="newBatchSize" /> is less than 1.</exception>
        public void UpdateCapacityAndForget(int newBatchSize)
        {
            if (newBatchSize < 1)
            {
                throw new ArgumentException($"The provided parameter '{nameof(newBatchSize)}' less than 1");
            }

            _ = UpdateCapacityAsync(newBatchSize);
        }

        /// <summary>
        /// Cancels a task if it has not yet started. Returns true if successfully cancelled, false if could not be cancelled.
        /// </summary>
        public static bool RemoveAndCancel(BatchTask batchTask) => 
            batchTask.Cancel();

        /// <summary>
        /// Cancels task that have not yet started.
        /// </summary>
        public static void RemoveAndCancel(IEnumerable<BatchTask> batchTasks)
        {
            foreach (BatchTask batchTask in batchTasks)
            {
                batchTask.Cancel();
            }
        }

        /// <summary>
        /// Remove all pending tasks. processing tasks will continue processing.
        /// </summary>
        public void RemoveAndCancelPendingTasks()
        {
            lock (_unprocessedTasks)
            {
                while (!_unprocessedTasks.IsEmpty)
                {
                    bool result = _unprocessedTasks.TryDequeue(out BatchTask? batchTask);
                    if (result)
                    {
                        batchTask?.Cancel();
                    }
                }
            }
        }

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Task task) => 
            AddTask(task);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Task task, Task callback, bool waitForCallback = false) => 
            AddTask(task, taskCallback: callback, waitForCallback: waitForCallback);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Task task, Func<Task> callback, bool waitForCallback = false) => 
            AddTask(task, functionCallback: callback, waitForCallback: waitForCallback);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Task task, Action callback, bool waitForCallback = false) => 
            AddTask(task, actionCallback: callback, waitForCallback: waitForCallback);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Func<Task> function) => 
            AddFunc(function);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Func<Task> function, Task callback, bool waitForCallback = false) => 
            AddFunc(function, taskCallback: callback, waitForCallback: waitForCallback);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Func<Task> function, Func<Task> callback, bool waitForCallback = false) => 
            AddFunc(function, functionCallback: callback, waitForCallback: waitForCallback);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Func<Task> function, Action callback, bool waitForCallback = false) =>
            AddFunc(function, actionCallback: callback, waitForCallback: waitForCallback);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Action action) => 
            AddTask(new Task(action));

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Action action, Task callback, bool waitForCallback = false) => 
            AddTask(new Task(action), taskCallback: callback, waitForCallback: waitForCallback);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Action action, Func<Task> callback, bool waitForCallback = false) => 
            AddTask(new Task(action), functionCallback: callback, waitForCallback: waitForCallback);

        /// <summary>
        /// Add a task to the BatchPool. The task will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public BatchTask Add(Action action, Action callback, bool waitForCallback = false) => 
            AddTask(new Task(action), actionCallback: callback, waitForCallback: waitForCallback);
        /// <summary>
        /// Add a tasks to the BatchPool. The tasks will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public List<BatchTask> Add(IEnumerable<Task> tasks)
        {
            List<BatchTask> batchTasks = new();

            foreach (Task task in tasks)
            {
                batchTasks.Add(AddTask(task));
            }

            return batchTasks;
        }

        /// <summary>
        /// Add a tasks to the BatchPool. The tasks will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public List<BatchTask> Add(IEnumerable<Func<Task>> functions)
        {
            List<BatchTask> batchTasks = new();

            foreach (Func<Task> function in functions)
            {
                batchTasks.Add((AddFunc(function)));
            }

            return batchTasks;
        }

        /// <summary>
        /// Add a tasks to the BatchPool. The tasks will automatically start when the it can if the BatchPool is enabled.
        /// </summary>
        public List<BatchTask> Add(IEnumerable<Action> functions)
        {
            List<BatchTask> batchTasks = new();

            foreach (Action function in functions)
            {
                batchTasks.Add((AddTask(new Task(function))));
            }

            return batchTasks;
        }

        /// <summary>
        /// Ready = true if the BatchPool is enabled and is not currently processing new tasks
        /// </summary>
        private bool IsReady() => _isEnabled && !_isRunning;

        private void HandleCancelTokenCancelled()
        {
            RemoveAndCancelPendingTasks();
            _isRunning = false;
            _cancellationToken.ThrowIfCancellationRequested();
        }

        private void ThrowTokenCancelledIfCancelled()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                HandleCancelTokenCancelled();
            }
        }

        /// <summary>
        /// Primary control loop that will not exit unless the BatchPool is cancelled or all pending tasks are finished.
        /// </summary>
        private async Task RunBatchPool()
        {
            if (!IsReady())
            {
                return;
            }

            _isRunning = true;

            while (!_unprocessedTasks.IsEmpty)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    HandleCancelTokenCancelled();
                    return;
                }

                if (_isUpdatingBatchSize)
                {
                    // Wait for a BatchPool size update to finish.
                    await _batchUpdateSemaphore
                        .WaitAsync(_cancellationToken)
                        .ConfigureAwait(false);
                    _batchUpdateSemaphore.Release();
                }

                // Wait for the BatchPool capacity to not be exceeded to process a new task.
                await _batchRateLimitSemaphore
                    .WaitAsync(_cancellationToken)
                    .ConfigureAwait(false);

                if (!_isEnabled)
                {
                    // Graceful exit of the running loop due to the BatchPool being paused.
                    _batchRateLimitSemaphore.Release();
                    _isRunning = false;
                    return;
                }

                bool success = _unprocessedTasks.TryDequeue(out BatchTask? currentTask);
                if (!success
                    || currentTask == null)
                {
                    // A task could not not be retrieved for some reason.
                    continue;
                }

                // Don't wait for the task so more tasks can be started concurrently.
                _ = RunAndWaitForTask(currentTask);
            }

            _isRunning = false;
        }

        private async Task RunAndWaitForTask(BatchTask currentTask)
        {
            try
            {
                await currentTask
                    .StartAndWaitAsync()
                    .ConfigureAwait(false);
            }
            finally
            {
                _batchRateLimitSemaphore.Release();
                lock (_runningTasks)
                {
                    _runningTasks.Remove(currentTask);
                }
            }
        }

        private BatchTask AddTask(Task task, Task? taskCallback = null, Func<Task>? functionCallback = null, Action? actionCallback = null, bool waitForCallback = false)
        {
            ThrowTokenCancelledIfCancelled();
            ThrowArgumentExceptionIfValidationIsEnabledAndFails(task);
            TaskBatchTask batchTask = new(task, GetCallback(taskCallback, functionCallback, actionCallback, waitForCallback));
            return AddTask(batchTask);
        }

        private static ICallback? GetCallback(Task? taskCallback = null, Func<Task>? functionCallback = null, Action? actionCallback = null, bool waitForCallback = false)
        {
            if (taskCallback != null)
            {
                return new TaskCallback(taskCallback, waitForCallback);
            }

            if (functionCallback != null)
            {
                return new FunctionCallback(functionCallback, waitForCallback);
            }

            if (actionCallback != null)
            {
                return new ActionCallback(actionCallback, waitForCallback);
            }

            return null;
        }

        private BatchTask AddFunc(Func<Task> function, Task? taskCallback = null, Func<Task>? functionCallback = null, Action? actionCallback = null, bool waitForCallback = false)
        {
            ThrowTokenCancelledIfCancelled();
            FunctionBatchTask batchTask = new(function, GetCallback(taskCallback, functionCallback, actionCallback, waitForCallback));
            return AddTask(batchTask);
        }

        private BatchTask AddTask(BatchTask batchTask)
        {
            lock (_runningTasks)
            {
                _runningTasks.Add(batchTask);
            }

            _unprocessedTasks.Enqueue(batchTask);

            if (IsReady())
            {
                _ = RunBatchPool()
                    .ConfigureAwait(false);
            }

            return batchTask;
        }

        private void ThrowArgumentExceptionIfValidationIsEnabledAndFails(Task? task)
        {
            if (_inactiveTaskValidation
                && task != null
                && TaskUtil.IsTaskRunningOrCompleted(task))
            {
                throw new ArgumentException("The provided task is already running");
            }
        }
    }
}
