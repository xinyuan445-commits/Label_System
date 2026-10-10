using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using U8Gateway.Core.U8;

namespace U8Gateway.U8
{
    public sealed class StaOperationQueue : IU8OperationQueue, IDisposable
    {
        private readonly BlockingCollection<IWorkItem> _items;
        private readonly Thread _thread;
        private readonly Mutex _mutex;
        private readonly TimeSpan _mutexTimeout;
        private bool _disposed;

        public StaOperationQueue(
            int capacity = 50,
            int mutexTimeoutSeconds = 120,
            string mutexName = @"Global\U8Gateway.U8Operations")
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            if (mutexTimeoutSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mutexTimeoutSeconds));
            }

            _items = new BlockingCollection<IWorkItem>(capacity);
            _mutex = new Mutex(false, mutexName);
            _mutexTimeout = TimeSpan.FromSeconds(mutexTimeoutSeconds);
            _thread = new Thread(Work)
            {
                IsBackground = true,
                Name = "U8Gateway.U8.STA"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task<T> ExecuteAsync<T>(Func<T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(StaOperationQueue));
            }

            var item = new WorkItem<T>(action);
            if (!_items.TryAdd(item))
            {
                throw new InvalidOperationException("U8 操作队列已满。上层应返回 U8_QUEUE_FULL。");
            }
            return item.Task;
        }

        private void Work()
        {
            foreach (IWorkItem item in _items.GetConsumingEnumerable())
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = _mutex.WaitOne(_mutexTimeout);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }
                    if (!acquired)
                    {
                        item.Fail(new TimeoutException("U8 操作等待超时。"));
                        continue;
                    }
                    item.Run();
                }
                catch (Exception exception)
                {
                    item.Fail(exception);
                }
                finally
                {
                    if (acquired)
                    {
                        try
                        {
                            _mutex.ReleaseMutex();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _items.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(30));
            _items.Dispose();
            _mutex.Dispose();
        }

        private interface IWorkItem
        {
            void Run();
            void Fail(Exception exception);
        }

        private sealed class WorkItem<T> : IWorkItem
        {
            private readonly Func<T> _action;
            private readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>();

            public WorkItem(Func<T> action)
            {
                _action = action;
            }

            public Task<T> Task => _completion.Task;

            public void Run()
            {
                try
                {
                    _completion.TrySetResult(_action());
                }
                catch (Exception exception)
                {
                    _completion.TrySetException(exception);
                }
            }

            public void Fail(Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
