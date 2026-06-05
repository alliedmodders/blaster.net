// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Threading.Channels;

namespace Blaster.Batch;

/// <summary>
/// Represents a collection of items to be processed.
/// </summary>
public interface IBatch
{
    /// <summary>
    /// Gets the item at the specified index.
    /// </summary>
    object Item(int index);

    /// <summary>
    /// Gets the total number of items in this batch.
    /// </summary>
    int Count { get; }
}

/// <summary>
/// Callback function to process a single item.
/// </summary>
public delegate void ProcessCallback(object item);

/// <summary>
/// Processes items from batches concurrently using a configurable worker pool.
/// </summary>
/// <remarks>
/// The BatchProcessor maintains a queue of work items and processes them
/// concurrently using up to maxTasks parallel workers. It supports both graceful
/// and forced shutdown semantics.
/// </remarks>
public class BatchProcessor : IDisposable
{
    private readonly ProcessCallback _callback;
    private readonly int _maxTasks;
    private readonly Channel<IBatch> _batchQueue;
    private readonly Channel<bool> _stopCommand;
    private readonly SemaphoreSlim _finishedSignal;
    private readonly Channel<bool> _taskDone;
    private readonly Lock _stateLock = new();
    
    private bool _stopped;
    private readonly Task? _processorTask;

    public BatchProcessor(ProcessCallback callback, int maxTasks)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _maxTasks = maxTasks;

        _batchQueue = Channel.CreateUnbounded<IBatch>();
        _stopCommand = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) 
        { 
            FullMode = BoundedChannelFullMode.Wait 
        });
        _taskDone = Channel.CreateBounded<bool>(new BoundedChannelOptions(maxTasks)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        _finishedSignal = new SemaphoreSlim(0, 1);
        _stopped = false;

        // Start the worker task
        _processorTask = ProcessBatchesAsync();
    }

    /// <summary>
    /// Adds a batch of items to be processed.
    /// </summary>
    public void AddBatch(IBatch batch)
    {
        if (_stopped)
        {
            throw new InvalidOperationException("BatchProcessor has been stopped");
        }

        try
        {
            _batchQueue.Writer.TryWrite(batch);
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("BatchProcessor has been stopped");
        }
    }

    /// <summary>
    /// Signals graceful shutdown: waits for all pending work to complete.
    /// </summary>
    public void Finish()
    {
        SendStop(terminate: false);
    }

    /// <summary>
    /// Signals forced shutdown: stops accepting work but lets in-flight tasks continue.
    /// </summary>
    public void Terminate()
    {
        SendStop(terminate: true);
    }

    private void SendStop(bool terminate)
    {
        lock (_stateLock)
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
        }

        // Signal the processor to stop
        try
        {
            _stopCommand.Writer.TryWrite(terminate);
        }
        catch (ChannelClosedException) { }

        // Wait for the processor to finish
        _finishedSignal.Wait();
    }

    private async Task ProcessBatchesAsync()
    {
        var worklist = new List<object>();
        int outstanding = 0;
        bool stopped = false;
        bool terminated = false;

        try
        {
            while (true)
            {
                // Check for stop command
                if (_stopCommand.Reader.TryRead(out var terminateFlag))
                {
                    terminated = terminateFlag;
                    stopped = true;

                    if (terminated)
                    {
                        // Forced shutdown: clear worklist and signal immediately
                        worklist.Clear();
                        _finishedSignal.Release();

                        // Exit if no outstanding tasks
                        if (outstanding == 0)
                        {
                            return;
                        }

                        // Otherwise, keep running until all tasks complete
                        continue;
                    }
                }

                // Check for task completion
                if (_taskDone.Reader.TryRead(out _))
                {
                    outstanding--;

                    // If we have queued work, launch a new task
                    if (worklist.Count > 0)
                    {
                        var item = worklist[^1];
                        worklist.RemoveAt(worklist.Count - 1);
                        EnqueueItem(item, ref outstanding);
                        continue;
                    }

                    // If all work is done and we're stopping, signal and exit
                    if (!HasWorkRemaining(worklist, outstanding) && stopped)
                    {
                        if (!terminated)
                        {
                            _finishedSignal.Release();
                        }
                        return;
                    }
                }

                // Check for new batch
                if (_batchQueue.Reader.TryRead(out var batch))
                {
                    EnqueueBatch(batch, worklist, ref outstanding);
                    continue;
                }

                // If nothing is ready, wait for the next event
                // Use a small timeout to periodically check for shutdown
                var readTasks = new List<Task>();

                if (!_batchQueue.Reader.Completion.IsCompleted)
                {
                    readTasks.Add(_batchQueue.Reader.WaitToReadAsync().AsTask());
                }

                if (!_taskDone.Reader.Completion.IsCompleted)
                {
                    readTasks.Add(_taskDone.Reader.WaitToReadAsync().AsTask());
                }

                if (!_stopCommand.Reader.Completion.IsCompleted)
                {
                    readTasks.Add(_stopCommand.Reader.WaitToReadAsync().AsTask());
                }

                if (readTasks.Count > 0)
                {
                    await Task.WhenAny(readTasks);
                }
                else
                {
                    // All channels are closed, we're done
                    break;
                }
            }
        }
        finally
        {
            _batchQueue.Writer.Complete();
            _stopCommand.Writer.Complete();
            _taskDone.Writer.Complete();
        }
    }

    private void EnqueueItem(object item, ref int outstanding)
    {
        outstanding++;

        // Launch task to process this item
        _ = Task.Run(() =>
        {
            try
            {
                _callback(item);
            }
            finally
            {
                try
                {
                    _taskDone.Writer.TryWrite(true);
                }
                catch (ChannelClosedException) { }
            }
        });
    }

    private void EnqueueBatch(IBatch batch, List<object> worklist, ref int outstanding)
    {
        int index = 0;

        // Launch tasks for items up to the max concurrent limit
        while (outstanding < _maxTasks && index < batch.Count)
        {
            EnqueueItem(batch.Item(index), ref outstanding);
            index++;
        }

        // Add remaining items to the worklist
        for (int i = index; i < batch.Count; i++)
        {
            worklist.Add(batch.Item(i));
        }
    }

    private static bool HasWorkRemaining(List<object> worklist, int outstanding)
    {
        return worklist.Count > 0 || outstanding > 0;
    }

    public void Dispose()
    {
        try
        {
            Terminate();
        }
        catch { }

        _finishedSignal?.Dispose();

        if (_processorTask != null)
        {
            try
            {
                _processorTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch { }
        }

        GC.SuppressFinalize(this);
    }
}
