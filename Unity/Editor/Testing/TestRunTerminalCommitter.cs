using System;

namespace AgentBridge
{
    /// <summary>
    /// 两阶段提交测试终态：只有持久化成功后才向监视器发布终态并释放活动锁。
    /// 写入失败时保留同一份待提交快照，供 Editor update 重试。
    /// </summary>
    internal sealed class TestRunTerminalCommitter
    {
        private readonly Action<TestRunRecord> _save;
        private readonly Action<TestRunRecord> _publishCommitted;
        private TestRunRecord _pending;

        public TestRunTerminalCommitter(
            Action<TestRunRecord> save,
            Action<TestRunRecord> publishCommitted)
        {
            _save = save ?? throw new ArgumentNullException(nameof(save));
            _publishCommitted = publishCommitted ??
                                throw new ArgumentNullException(nameof(publishCommitted));
        }

        public bool HasPending => _pending != null;
        public string PendingRunId => _pending?.RunId ?? "";
        public string Operation { get; private set; } = "";
        public int FailureCount { get; private set; }

        public void Stage(TestRunRecord record, string operation)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }
            if (!record.IsTerminal)
            {
                throw new ArgumentException("只能提交 completed/interrupted 测试状态", nameof(record));
            }
            if (_pending != null)
            {
                throw new InvalidOperationException(
                    $"测试终态 '{_pending.RunId}' 仍在等待持久化");
            }

            _pending = record;
            Operation = string.IsNullOrEmpty(operation) ? "写入最终测试结果" : operation;
            FailureCount = 0;
        }

        public bool TryCommit(out Exception failure)
        {
            failure = null;
            if (_pending == null)
            {
                return false;
            }

            try
            {
                _save(_pending);
                // 发布可能包含 SessionState 清锁。即使磁盘已成功，发布失败也必须
                // 保留 pending；下一次重复 Save 是原子且幂等的，然后再次发布。
                _publishCommitted(_pending);
            }
            catch (Exception ex)
            {
                FailureCount++;
                failure = ex;
                return false;
            }

            _pending = null;
            Operation = "";
            FailureCount = 0;
            return true;
        }
    }
}
