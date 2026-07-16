using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>无返回值的轻量命令 task-like 类型。</summary>
    [AsyncMethodBuilder(typeof(CommandTaskMethodBuilder))]
    public readonly struct CommandTask
    {
        private readonly CommandTaskSource m_Source;

        internal CommandTask(CommandTaskSource source)
        {
            m_Source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public bool IsCompleted => m_Source != null && m_Source.IsCompleted;

        public CommandTaskAwaiter GetAwaiter()
        {
            if (m_Source == null)
            {
                throw new InvalidOperationException("CommandTask was not initialized");
            }
            return new CommandTaskAwaiter(m_Source);
        }

        public static CommandTask Delay(int milliseconds)
        {
            if (milliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(milliseconds));
            }

            var source = new CommandTaskSource();
            var dueTime = EditorApplication.timeSinceStartup + milliseconds / 1000.0;

            void Tick()
            {
                if (EditorApplication.timeSinceStartup < dueTime)
                {
                    return;
                }

                EditorApplication.update -= Tick;
                source.SetResult();
            }

            EditorApplication.update += Tick;
            return new CommandTask(source);
        }
    }

    /// <summary>带结果的轻量命令 task-like 类型。</summary>
    [AsyncMethodBuilder(typeof(CommandTaskMethodBuilder<>))]
    public readonly struct CommandTask<T>
    {
        private readonly CommandTaskSource<T> m_Source;

        internal CommandTask(CommandTaskSource<T> source)
        {
            m_Source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public bool IsCompleted => m_Source != null && m_Source.IsCompleted;

        public CommandTaskAwaiter<T> GetAwaiter()
        {
            if (m_Source == null)
            {
                throw new InvalidOperationException("CommandTask was not initialized");
            }
            return new CommandTaskAwaiter<T>(m_Source);
        }
    }

    public readonly struct CommandTaskAwaiter : ICriticalNotifyCompletion
    {
        private readonly CommandTaskSource m_Source;

        internal CommandTaskAwaiter(CommandTaskSource source)
        {
            m_Source = source;
        }

        public bool IsCompleted => m_Source.IsCompleted;

        public void OnCompleted(Action continuation)
        {
            m_Source.OnCompleted(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            m_Source.OnCompleted(continuation);
        }

        public void GetResult()
        {
            m_Source.GetResult();
        }
    }

    public readonly struct CommandTaskAwaiter<T> : ICriticalNotifyCompletion
    {
        private readonly CommandTaskSource<T> m_Source;

        internal CommandTaskAwaiter(CommandTaskSource<T> source)
        {
            m_Source = source;
        }

        public bool IsCompleted => m_Source.IsCompleted;

        public void OnCompleted(Action continuation)
        {
            m_Source.OnCompleted(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            m_Source.OnCompleted(continuation);
        }

        public T GetResult()
        {
            return m_Source.GetResult();
        }
    }

    public struct CommandTaskMethodBuilder<T>
    {
        private CommandTaskSource<T> m_Source;

        public static CommandTaskMethodBuilder<T> Create()
        {
            return new CommandTaskMethodBuilder<T>
            {
                m_Source = new CommandTaskSource<T>()
            };
        }

        public CommandTask<T> Task => new CommandTask<T>(m_Source);

        public void SetResult(T result)
        {
            m_Source.SetResult(result);
        }

        public void SetException(Exception exception)
        {
            m_Source.SetException(exception);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
        }

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter,
            ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            awaiter.OnCompleted(stateMachine.MoveNext);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter,
            ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            awaiter.UnsafeOnCompleted(stateMachine.MoveNext);
        }
    }

    public struct CommandTaskMethodBuilder
    {
        private CommandTaskSource m_Source;

        public static CommandTaskMethodBuilder Create()
        {
            return new CommandTaskMethodBuilder
            {
                m_Source = new CommandTaskSource()
            };
        }

        public CommandTask Task => new CommandTask(m_Source);

        public void SetResult()
        {
            m_Source.SetResult();
        }

        public void SetException(Exception exception)
        {
            m_Source.SetException(exception);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
        }

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter,
            ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            awaiter.OnCompleted(stateMachine.MoveNext);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter,
            ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            awaiter.UnsafeOnCompleted(stateMachine.MoveNext);
        }
    }

    internal abstract class CommandTaskSourceCore
    {
        private readonly object m_Gate = new object();
        private Action m_Continuations;
        private ExceptionDispatchInfo m_Exception;
        private bool m_IsCompleted;

        internal bool IsCompleted
        {
            get
            {
                lock (m_Gate)
                {
                    return m_IsCompleted;
                }
            }
        }

        internal void SetException(Exception exception)
        {
            Complete(ExceptionDispatchInfo.Capture(
                exception ?? throw new ArgumentNullException(nameof(exception))));
        }

        internal void OnCompleted(Action continuation)
        {
            if (continuation == null)
            {
                throw new ArgumentNullException(nameof(continuation));
            }

            var scheduled = CaptureContext(continuation);
            var runNow = false;
            lock (m_Gate)
            {
                if (m_IsCompleted)
                {
                    runNow = true;
                }
                else
                {
                    m_Continuations += scheduled;
                }
            }
            if (runNow)
            {
                scheduled();
            }
        }

        protected void GetResultCore()
        {
            ExceptionDispatchInfo exception;
            lock (m_Gate)
            {
                if (!m_IsCompleted)
                {
                    throw new InvalidOperationException("CommandTask has not completed");
                }
                exception = m_Exception;
            }
            exception?.Throw();
        }

        protected void Complete(ExceptionDispatchInfo exception)
        {
            Action continuations;
            lock (m_Gate)
            {
                if (m_IsCompleted)
                {
                    throw new InvalidOperationException("CommandTask completed more than once");
                }
                m_Exception = exception;
                m_IsCompleted = true;
                continuations = m_Continuations;
                m_Continuations = null;
            }

            continuations?.Invoke();
        }

        private static Action CaptureContext(Action continuation)
        {
            var context = SynchronizationContext.Current;
            return context == null
                ? continuation
                : new Action(() =>
                {
                    if (ReferenceEquals(SynchronizationContext.Current, context))
                    {
                        continuation();
                    }
                    else
                    {
                        context.Post(_ => continuation(), null);
                    }
                });
        }
    }

    internal sealed class CommandTaskSource : CommandTaskSourceCore
    {
        internal void SetResult()
        {
            Complete(null);
        }

        internal void GetResult()
        {
            GetResultCore();
        }
    }

    internal sealed class CommandTaskSource<T> : CommandTaskSourceCore
    {
        private T m_Result;

        internal void SetResult(T result)
        {
            m_Result = result;
            Complete(null);
        }

        internal T GetResult()
        {
            GetResultCore();
            return m_Result;
        }
    }
}
