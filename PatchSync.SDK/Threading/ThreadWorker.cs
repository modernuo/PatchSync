using System.Collections.Concurrent;

namespace PatchSync.SDK.Threading;

public class ThreadWorker<T>
{
    private readonly CancellationToken? _cancellationToken;
    private readonly Action<T> _action;
    private readonly ConcurrentQueue<T> _entities;
    private readonly AutoResetEvent _startEvent; // Main thread tells the thread to start working
    private readonly AutoResetEvent _stopEvent;  // Main thread waits for the worker finish draining

    private readonly Thread _thread;
    private bool _exit;
    private bool _pause;

    public ThreadWorker(Action<T> action, CancellationToken? cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _action = action;
        _startEvent = new AutoResetEvent(false);
        _stopEvent = new AutoResetEvent(false);
        _entities = new ConcurrentQueue<T>();
        _thread = new Thread(Execute);
        _thread.Start(this);
    }

    public static void MapParallel(
        IEnumerable<T> coll, Action<T> action,
        CancellationToken? cancellationToken = null
    )
    {
        if (cancellationToken?.IsCancellationRequested == true)
        {
            throw new OperationCanceledException("The operation was cancelled before it could start.");
        }

        var workerCount = Math.Max(Environment.ProcessorCount - 1, 1);
        var workers = new ThreadWorker<T>[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            if (cancellationToken?.IsCancellationRequested == true)
            {
                throw new OperationCanceledException("The operation was cancelled.");
            }

            workers[i] = new ThreadWorker<T>(action, cancellationToken);
            workers[i].Wake();
        }

        var index = 0;
        foreach (var t in coll)
        {
            if (cancellationToken?.IsCancellationRequested == true)
            {
                break;
            }

            workers[index++].Push(t);
            if (index >= workerCount)
            {
                index = 0;
            }
        }

        // Pause the workers
        foreach (var worker in workers)
        {
            worker.Exit();
        }

        // We throw after we properly kill off the workers
        if (cancellationToken?.IsCancellationRequested == true)
        {
            throw new OperationCanceledException("The operation was cancelled.");
        }

        Array.Clear(workers);
    }

    public void Wake()
    {
        _startEvent.Set();
    }

    public void Sleep()
    {
        Volatile.Write(ref _pause, true);
        _stopEvent.WaitOne();
    }

    public void Exit()
    {
        _exit = true;
        Wake();
        Sleep();
    }

    public void Push(T entity)
    {
        _entities.Enqueue(entity);
    }

    private static void Execute(object obj)
    {
        var worker = (ThreadWorker<T>)obj;

        var reader = worker._entities;

        while (worker._startEvent.WaitOne())
        {
            while (worker._cancellationToken?.IsCancellationRequested != true)
            {
                var pauseRequested = Volatile.Read(ref worker._pause);
                if (reader.TryDequeue(out var t))
                {
                    worker._action(t);
                }
                else if (pauseRequested) // Break when finished
                {
                    break;
                }
            }

            worker._stopEvent.Set(); // Allow the main thread to continue now that we are finished
            worker._pause = false;

            if (worker._exit)
            {
                return;
            }
        }
    }
}
