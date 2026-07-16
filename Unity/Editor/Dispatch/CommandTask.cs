using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace AgentBridge
{
    /// <summary>无返回值的命令 task-like 类型。</summary>
    [AsyncMethodBuilder(typeof(CommandTaskMethodBuilder))]
    public readonly struct CommandTask
    {
        private readonly Task m_Task;

        internal CommandTask(Task task)
        {
            m_Task = task ?? throw new ArgumentNullException(nameof(task));
        }

        public bool IsCompleted => m_Task != null && m_Task.IsCompleted;

        public TaskAwaiter GetAwaiter()
        {
            if (m_Task == null)
            {
                throw new InvalidOperationException("CommandTask was not initialized");
            }
            return m_Task.GetAwaiter();
        }
    }

    /// <summary>
    /// 命令专用的 task-like 返回类型。执行、状态机和异常传播由系统 Task 承载，
    /// 对 ICommandHandler 暴露独立类型并支持原生 async/await。
    /// </summary>
    [AsyncMethodBuilder(typeof(CommandTaskMethodBuilder<>))]
    public readonly struct CommandTask<T>
    {
        private readonly Task<T> m_Task;

        internal CommandTask(Task<T> task)
        {
            m_Task = task ?? throw new ArgumentNullException(nameof(task));
        }

        public bool IsCompleted => m_Task != null && m_Task.IsCompleted;

        public TaskAwaiter<T> GetAwaiter()
        {
            if (m_Task == null)
            {
                throw new InvalidOperationException("CommandTask was not initialized");
            }
            return m_Task.GetAwaiter();
        }
    }

    public struct CommandTaskMethodBuilder<T>
    {
        private AsyncTaskMethodBuilder<T> m_Builder;

        public static CommandTaskMethodBuilder<T> Create()
        {
            return new CommandTaskMethodBuilder<T>
            {
                m_Builder = AsyncTaskMethodBuilder<T>.Create()
            };
        }

        public CommandTask<T> Task => new CommandTask<T>(m_Builder.Task);

        public void SetResult(T result)
        {
            m_Builder.SetResult(result);
        }

        public void SetException(Exception exception)
        {
            m_Builder.SetException(exception);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            m_Builder.SetStateMachine(stateMachine);
        }

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            m_Builder.Start(ref stateMachine);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter,
            ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            m_Builder.AwaitOnCompleted(ref awaiter, ref stateMachine);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter,
            ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            m_Builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
        }
    }

    public struct CommandTaskMethodBuilder
    {
        private AsyncTaskMethodBuilder m_Builder;

        public static CommandTaskMethodBuilder Create()
        {
            return new CommandTaskMethodBuilder
            {
                m_Builder = AsyncTaskMethodBuilder.Create()
            };
        }

        public CommandTask Task => new CommandTask(m_Builder.Task);

        public void SetResult()
        {
            m_Builder.SetResult();
        }

        public void SetException(Exception exception)
        {
            m_Builder.SetException(exception);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            m_Builder.SetStateMachine(stateMachine);
        }

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            m_Builder.Start(ref stateMachine);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter,
            ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            m_Builder.AwaitOnCompleted(ref awaiter, ref stateMachine);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter,
            ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            m_Builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
        }
    }
}
