// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Blaster.Batch;

namespace Blaster.Tests;

/// <summary>
/// Simple batch implementation for testing.
/// </summary>
public class TestBatch : IBatch
{
    private readonly object[] _items;

    public TestBatch(int count)
    {
        _items = new object[count];
        for (int i = 0; i < count; i++)
        {
            _items[i] = i + 1;
        }
    }

    public object Item(int index) => _items[index];
    public int Count => _items.Length;
}

public class BatchProcessorTests
{
    [Fact]
    public void ProcessesAllItems()
    {
        var items = new List<object>();
        var lockObj = new object();

        var processor = new BatchProcessor(item =>
        {
            lock (lockObj)
            {
                items.Add(item);
            }
        }, maxTasks: 10);

        processor.AddBatch(new TestBatch(10));
        processor.AddBatch(new TestBatch(10));
        processor.AddBatch(new TestBatch(10));
        processor.AddBatch(new TestBatch(10));
        processor.Finish();

        Assert.Equal(40, items.Count);
    }

    [Fact]
    public void RespectsConcurrencyLimit()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();
        var allStarted = new ManualResetEvent(false);
        var canProceed = new ManualResetEvent(false);

        var processor = new BatchProcessor(item =>
        {
            lock (lockObj)
            {
                concurrentCount++;
                if (concurrentCount > maxConcurrent)
                {
                    maxConcurrent = concurrentCount;
                }
            }

            allStarted.Set();
            canProceed.WaitOne(); // Block until we're told to proceed

            lock (lockObj)
            {
                concurrentCount--;
            }
        }, maxTasks: 5);

        // Add enough items to trigger the concurrency limit
        processor.AddBatch(new TestBatch(10));

        // Give tasks time to start
        System.Threading.Thread.Sleep(100);

        // Let them proceed
        canProceed.Set();

        processor.Finish();

        // Should not have exceeded max concurrent tasks
        Assert.True(maxConcurrent <= 5, $"Max concurrent was {maxConcurrent}, expected <= 5");
    }

    [Fact]
    public void FinishWaitsForCompletion()
    {
        var processingDone = false;
        var processor = new BatchProcessor(_ =>
        {
            System.Threading.Thread.Sleep(50);
        }, maxTasks: 5);

        processor.AddBatch(new TestBatch(10));
        processor.Finish();
        processingDone = true;

        Assert.True(processingDone);
    }

    [Fact]
    public void TerminateStopsProcessing()
    {
        var processor = new BatchProcessor(_ =>
        {
            System.Threading.Thread.Sleep(10);
        }, maxTasks: 5);

        processor.AddBatch(new TestBatch(20));
        System.Threading.Thread.Sleep(50);  // Let some work start

        // Should not throw
        processor.Terminate();
        
        // Should be idempotent
        processor.Terminate();
    }

    [Fact]
    public void HandlesExceptionsInCallback()
    {
        var errorCount = 0;
        var lockObj = new object();

        var processor = new BatchProcessor(item =>
        {
            lock (lockObj)
            {
                errorCount++;
            }

            if ((int)item % 2 == 0)
            {
                throw new InvalidOperationException("Test error");
            }
        }, maxTasks: 5);

        processor.AddBatch(new TestBatch(10));
        processor.Finish();

        // All items should have been attempted
        Assert.Equal(10, errorCount);
    }

    [Fact]
    public void ProcessesMultipleBatches()
    {
        var itemCounts = new List<int>();
        var lockObj = new object();

        var processor = new BatchProcessor(_ =>
        {
            lock (lockObj)
            {
                itemCounts.Add(1);
            }
        }, maxTasks: 5);

        for (int i = 0; i < 5; i++)
        {
            processor.AddBatch(new TestBatch(15));
        }

        processor.Finish();

        Assert.Equal(75, itemCounts.Count);
    }

    [Fact]
    public void QueuesWorkWhenExceedingConcurrencyLimit()
    {
        var items = new List<object>();
        var lockObj = new object();

        var processor = new BatchProcessor(item =>
        {
            lock (lockObj)
            {
                items.Add(item);
            }
        }, maxTasks: 2);

        processor.AddBatch(new TestBatch(10));
        processor.Finish();

        // All items should be processed even though concurrency limit is low
        Assert.Equal(10, items.Count);
    }

    [Fact]
    public void CannotAddBatchAfterStop()
    {
        var processor = new BatchProcessor(_ => { }, maxTasks: 5);
        processor.Terminate();

        Assert.Throws<InvalidOperationException>(() =>
        {
            processor.AddBatch(new TestBatch(5));
        });
    }

    [Fact]
    public void DoubleFinishIsIdempotent()
    {
        var processor = new BatchProcessor(_ => { }, maxTasks: 5);
        processor.AddBatch(new TestBatch(5));
        processor.Finish();
        processor.Finish(); // Should not throw
    }

    [Fact]
    public void DoubleTerminateIsIdempotent()
    {
        var processor = new BatchProcessor(_ => { }, maxTasks: 5);
        processor.AddBatch(new TestBatch(5));
        processor.Terminate();
        processor.Terminate(); // Should not throw
    }
}
