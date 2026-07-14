using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// Unity Test Framework 异步运行监视器。活动 runId 存 SessionState,因此 PlayMode 引发的
    /// domain reload 后仍能继续接收回调;完整快照另由 TestRunStore 落盘。
    /// </summary>
    [InitializeOnLoad]
    internal static class TestRunMonitor
    {
        private const string ActiveRunSessionKey = "AgentBridge.Testing.ActiveRunId.v1";
        private const int CallbackPriority = 1000;
        private const int ProgressPersistInterval = 10;
        private const double ProgressPersistSeconds = 0.5;
        private const double RestoreVerificationGraceSeconds = 15;
        private const double ActiveRunInactiveGraceSeconds = 2;
        private const double TerminalPersistInitialRetrySeconds = 0.25;
        private const double TerminalPersistMaxRetrySeconds = 8;

        private static readonly TestRunTerminalCommitter s_TerminalCommitter =
            new TestRunTerminalCommitter(TestRunStore.Save, OnTerminalCommitted);
        private static TestRunnerApi s_Api;
        private static CallbackReceiver s_Callbacks;
        private static TestRunRecord s_ActiveRecord;
        private static TestRunRecord s_LastRecord;
        private static MethodInfo s_IsRunActiveMethod;
        private static PropertyInfo s_TestJobHolderInstanceProperty;
        private static FieldInfo s_TestRunsField;
        private static Type s_TestJobDataType;
        private static FieldInfo s_TestJobGuidField;
        private static FieldInfo s_TestJobIsRunningField;
        private static bool s_FrameworkHasRunnerRegistry;
        private static string s_InitializationError;
        private static int s_ResultsSincePersist;
        private static double s_LastPersistTime;
        private static bool s_VerifyingRestoredRun;
        private static double s_RestoreVerificationDeadline;
        private static bool s_FrameworkActivityObserved;
        private static double s_FrameworkInactiveSince = -1;
        private static double s_NextTerminalPersistRetry;

        static TestRunMonitor()
        {
            Initialize();
        }

        public static TestRunRecord Start(string mode, TestRunFilterRecord filter, string ifUnsaved)
        {
            EnsureAvailable();
            EnsureEditorCanStart();
            EnsureNoActiveRun();
            var savedScenes = HandleUnsavedScenes(ifUnsaved);
            EnsureEditorCanStart();

            var runId = $"test-run-{Guid.NewGuid():N}";
            var record = new TestRunRecord
            {
                RunId = runId,
                Mode = mode,
                Status = TestRunStatuses.Running,
                StartedAt = UtcNow(),
                Filter = filter ?? new TestRunFilterRecord(),
                SavedScenes = savedScenes
            };

            // 先持久化并设置 SessionState,再交给测试框架。这样 Execute 后即使立刻触发
            // PlayMode/domain reload,新 domain 也能恢复 runId。
            TestRunStore.Save(record);
            try
            {
                s_ActiveRecord = record;
                SessionState.SetString(ActiveRunSessionKey, runId);
                var executionSettings = BuildExecutionSettings(mode, record.Filter);
                var frameworkRunId = s_Api.Execute(executionSettings);
                if (string.IsNullOrEmpty(frameworkRunId))
                {
                    throw new InvalidOperationException("Unity Test Framework 未返回运行 ID");
                }

                record.FrameworkRunId = frameworkRunId;
                TryPersist(record, "记录 Test Framework runId");
                if (record.Status == TestRunStatuses.Running && s_ActiveRecord == record)
                {
                    BeginRestoredRunVerification();
                }
                return record;
            }
            catch (Exception ex)
            {
                ClearActiveState();
                TestRunStore.DeleteBestEffort(runId);
                throw new CommandException(TestErrorCodes.TestRunStartFailed,
                    $"启动 Unity 测试失败:{TestResultLimits.TruncateRunText(ex.Message)}");
            }
        }

        public static TestRunRecord Get(string requestedRunId)
        {
            var useLatest = string.IsNullOrEmpty(requestedRunId);
            var runId = requestedRunId;
            if (string.IsNullOrEmpty(runId))
            {
                runId = s_ActiveRecord?.RunId ?? s_LastRecord?.RunId ?? TestRunStore.FindLatestRunId();
            }
            if (string.IsNullOrEmpty(runId))
            {
                throw new CommandException(TestErrorCodes.TestResultNotFound, "尚无 Unity 测试运行结果");
            }
            if (!TestRunStore.IsValidRunId(runId))
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "runId 必须匹配 [A-Za-z0-9][A-Za-z0-9_-]{0,63}");
            }

            if (s_ActiveRecord != null && s_ActiveRecord.RunId == runId)
            {
                return s_ActiveRecord;
            }
            if (s_LastRecord != null && s_LastRecord.RunId == runId)
            {
                return s_LastRecord;
            }
            if (!TestRunStore.TryLoad(runId, out var record))
            {
                throw new CommandException(TestErrorCodes.TestResultNotFound,
                    $"测试运行不存在:'{runId}'");
            }

            // SessionState 会跨 domain reload 保留,但编辑器重启后清空。若磁盘仍留有
            // running 且本 session 并未恢复它,则上次编辑器进程已中断。
            if (record.Status == TestRunStatuses.Running &&
                SessionState.GetString(ActiveRunSessionKey, "") != runId)
            {
                record.Status = TestRunStatuses.Interrupted;
                record.FinishedAt = UtcNow();
                record.DurationSeconds = ElapsedSeconds(record.StartedAt);
                record.Message = "编辑器会话结束前测试未产生完成回调,运行状态未知";
                TestRunStore.Save(record);
            }

            if (useLatest && record.IsTerminal)
            {
                s_LastRecord = record;
            }
            return record;
        }

        private static void Initialize()
        {
            if (s_Api != null)
            {
                return;
            }

            TestRunnerApi api = null;
            try
            {
                api = ScriptableObject.CreateInstance<TestRunnerApi>();
                api.hideFlags = HideFlags.HideAndDontSave;
                var callbacks = new CallbackReceiver();
                api.RegisterCallbacks(callbacks, CallbackPriority);

                s_Api = api;
                s_Callbacks = callbacks;
                s_IsRunActiveMethod = typeof(TestRunnerApi).GetMethod(
                    "IsRunActive", BindingFlags.Static | BindingFlags.NonPublic);
                InitializeActivityReflection();
                s_InitializationError = null;
                RestoreActiveRecord();
            }
            catch (Exception ex)
            {
                s_InitializationError = $"{ex.GetType().Name}:{ex.Message}";
                if (api != null)
                {
                    UnityEngine.Object.DestroyImmediate(api);
                }
                s_Api = null;
                s_Callbacks = null;
                Debug.LogError($"[AgentBridge] Unity Test Framework 初始化失败:{s_InitializationError}");
            }
        }

        private static void RestoreActiveRecord()
        {
            var runId = SessionState.GetString(ActiveRunSessionKey, "");
            if (string.IsNullOrEmpty(runId))
            {
                return;
            }

            try
            {
                if (!TestRunStore.TryLoad(runId, out var record))
                {
                    Debug.LogError($"[AgentBridge] 活动测试状态文件不存在:'{runId}',保留活动锁等待人工恢复");
                    return;
                }

                if (record.Status == TestRunStatuses.Running)
                {
                    s_ActiveRecord = record;
                    s_LastPersistTime = EditorApplication.timeSinceStartup;
                    BeginRestoredRunVerification();
                    return;
                }

                // 终态已原子写盘、但 domain reload 发生在清理 SessionState 之前。
                // 此时磁盘提交是事实来源，可以安全完成内存发布与锁清理。
                if (record.IsTerminal)
                {
                    OnTerminalCommitted(record);
                }
            }
            catch (CommandException ex)
            {
                // 读取失败时不能假定运行已经结束；清锁会允许第二个 run 与未知 job 并发。
                Debug.LogError($"[AgentBridge] 恢复测试运行失败,保留活动锁:{ex.Message}");
            }
        }

        private static void EnsureAvailable()
        {
            if (s_Api == null)
            {
                Initialize();
            }
            if (s_Api == null)
            {
                throw new CommandException(TestErrorCodes.TestFrameworkUnavailable,
                    $"Unity Test Framework 不可用:{s_InitializationError ?? "初始化失败"}");
            }
        }

        private static void EnsureEditorCanStart()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new CommandException(TestErrorCodes.TestRunRequiresEditMode,
                    "run_tests 只能在完全停止的 EditMode 启动;PlayMode 测试会由 Test Framework 自行进入 PlayMode");
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new CommandException(TestErrorCodes.TestRunEditorBusy,
                    "Unity 正在编译或刷新 AssetDatabase,请空闲后重试 run_tests");
            }
        }

        private static void EnsureNoActiveRun()
        {
            if (s_TerminalCommitter.HasPending)
            {
                throw new CommandException(TestErrorCodes.TestRunActive,
                    $"测试运行 '{s_TerminalCommitter.PendingRunId}' 的终态仍在持久化");
            }

            VerifyRestoredRunIfDue();
            if (s_ActiveRecord != null && s_ActiveRecord.Status == TestRunStatuses.Running)
            {
                throw new CommandException(TestErrorCodes.TestRunActive,
                    $"已有 AgentBridge 测试运行:'{s_ActiveRecord.RunId}'");
            }

            var activeRunId = SessionState.GetString(ActiveRunSessionKey, "");
            if (!string.IsNullOrEmpty(activeRunId))
            {
                throw new CommandException(TestErrorCodes.TestRunActive,
                    $"已有 AgentBridge 测试运行:'{activeRunId}'");
            }

            if (TryIsFrameworkRunActive(null, out var frameworkActive) && frameworkActive)
            {
                throw new CommandException(TestErrorCodes.TestRunActive,
                    "Unity Test Framework 已有其它测试运行");
            }
        }

        private static string[] HandleUnsavedScenes(string action)
        {
            return SceneCommandSupport.HandleUnsavedScenes(
                action,
                SceneUnsavedOperation.RunTests).SavedScenes;
        }

        private static void BeginRestoredRunVerification()
        {
            s_VerifyingRestoredRun = true;
            s_FrameworkActivityObserved = false;
            s_FrameworkInactiveSince = -1;
            s_RestoreVerificationDeadline = EditorApplication.timeSinceStartup +
                                             RestoreVerificationGraceSeconds;
            EditorApplication.update -= VerifyRestoredRunOnUpdate;
            EditorApplication.update += VerifyRestoredRunOnUpdate;
        }

        private static void VerifyRestoredRunOnUpdate()
        {
            VerifyRestoredRunIfDue();
        }

        private static void VerifyRestoredRunIfDue()
        {
            if (!s_VerifyingRestoredRun || s_ActiveRecord == null ||
                s_ActiveRecord.Status != TestRunStatuses.Running)
            {
                StopRestoredRunVerification();
                return;
            }

            // TestJobDataHolder 会在 domain reload 后重新挂载 job。反射不可用时宁可
            // 保留锁,避免把正常 PlayMode reload 误判为中断。
            var now = EditorApplication.timeSinceStartup;
            var activityKnown = TryIsFrameworkRunActive(
                s_ActiveRecord.FrameworkRunId, out var active);
            if (activityKnown && active)
            {
                s_FrameworkActivityObserved = true;
                s_FrameworkInactiveSince = -1;
                return;
            }

            if (now < s_RestoreVerificationDeadline ||
                EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            // 只有在宽限期后仍能成功读取活动状态且得到 false,才考虑 job 丢失。
            // 已观察到活动的正常运行再给予短宽限,让 RunFinished 有机会先落盘。
            if (activityKnown && !active)
            {
                if (!s_FrameworkActivityObserved)
                {
                    InterruptActiveRecord("Unity Test Framework 未建立或恢复活动 job");
                    return;
                }
                if (s_FrameworkInactiveSince < 0)
                {
                    s_FrameworkInactiveSince = now;
                    return;
                }
                if (now - s_FrameworkInactiveSince >= ActiveRunInactiveGraceSeconds)
                {
                    InterruptActiveRecord("Unity Test Framework job 已停止但未产生完成回调");
                }
            }
        }

        private static void StopRestoredRunVerification()
        {
            s_VerifyingRestoredRun = false;
            s_FrameworkActivityObserved = false;
            s_FrameworkInactiveSince = -1;
            EditorApplication.update -= VerifyRestoredRunOnUpdate;
        }

        private static void InitializeActivityReflection()
        {
            var holderType = typeof(TestRunnerApi).Assembly.GetType(
                "UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
            if (holderType == null)
            {
                return;
            }

            s_TestRunsField = holderType.GetField("TestRuns",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_FrameworkHasRunnerRegistry = holderType.GetMethod("GetAllRunners",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;

            for (var cursor = holderType; cursor != null && s_TestJobHolderInstanceProperty == null;
                 cursor = cursor.BaseType)
            {
                s_TestJobHolderInstanceProperty = cursor.GetProperty("instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
            }
        }

        private static bool TryIsFrameworkRunActive(string frameworkRunId, out bool active)
        {
            active = false;
            var dataKnown = TryIsTestJobDataActive(frameworkRunId, out var dataActive);
            if (s_FrameworkHasRunnerRegistry)
            {
                var apiKnown = TryIsApiRunActive(out var apiActive);
                if (dataKnown && apiKnown)
                {
                    active = string.IsNullOrEmpty(frameworkRunId)
                        ? dataActive || apiActive
                        : dataActive && apiActive;
                    return true;
                }
                if (apiKnown)
                {
                    active = apiActive;
                    return true;
                }
            }

            // Test Framework 1.1.x 的 TestRunnerApi.IsRunActive 只覆盖命令行 runner,
            // 程序化 Execute 必须以持久化 TestJobDataHolder.TestRuns 为准。
            if (dataKnown)
            {
                active = dataActive;
                if (string.IsNullOrEmpty(frameworkRunId) &&
                    TryIsApiRunActive(out var commandLineActive))
                {
                    active |= commandLineActive;
                }
                return true;
            }
            return false;
        }

        private static bool TryIsTestJobDataActive(string frameworkRunId, out bool active)
        {
            active = false;
            if (s_TestJobHolderInstanceProperty == null || s_TestRunsField == null)
            {
                return false;
            }

            try
            {
                var holder = s_TestJobHolderInstanceProperty.GetValue(null, null);
                var runs = s_TestRunsField.GetValue(holder) as System.Collections.IEnumerable;
                if (runs == null)
                {
                    return true;
                }

                foreach (var run in runs)
                {
                    if (run == null)
                    {
                        continue;
                    }
                    EnsureTestJobDataReflection(run.GetType());
                    if (s_TestJobIsRunningField == null ||
                        !(bool)s_TestJobIsRunningField.GetValue(run))
                    {
                        continue;
                    }

                    var guid = s_TestJobGuidField?.GetValue(run) as string;
                    if (string.IsNullOrEmpty(frameworkRunId) ||
                        string.Equals(frameworkRunId, guid, StringComparison.Ordinal))
                    {
                        active = true;
                        return true;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void EnsureTestJobDataReflection(Type type)
        {
            if (s_TestJobDataType == type)
            {
                return;
            }
            s_TestJobDataType = type;
            s_TestJobGuidField = type.GetField("guid",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_TestJobIsRunningField = type.GetField("isRunning",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static bool TryIsApiRunActive(out bool active)
        {
            active = false;
            if (s_IsRunActiveMethod == null)
            {
                return false;
            }
            try
            {
                active = (bool)s_IsRunActiveMethod.Invoke(null, null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ExecutionSettings BuildExecutionSettings(string mode, TestRunFilterRecord filter)
        {
            var testMode = mode == "play"
                ? UnityEditor.TestTools.TestRunner.Api.TestMode.PlayMode
                : UnityEditor.TestTools.TestRunner.Api.TestMode.EditMode;
            var frameworkFilter = new Filter
            {
                testMode = testMode,
                testNames = NullIfEmpty(filter.TestNames),
                groupNames = NullIfEmpty(filter.GroupNames),
                categoryNames = NullIfEmpty(filter.CategoryNames),
                assemblyNames = NullIfEmpty(filter.AssemblyNames)
            };
            return new ExecutionSettings(frameworkFilter) { runSynchronously = false };
        }

        private static string[] NullIfEmpty(string[] values)
        {
            return values == null || values.Length == 0 ? null : values;
        }

        private static void OnRunStarted(ITestAdaptor testsToRun)
        {
            if (s_TerminalCommitter.HasPending)
            {
                return;
            }

            var record = GetActiveForCallback();
            if (record == null)
            {
                return;
            }

            record.Status = TestRunStatuses.Running;
            if (testsToRun != null)
            {
                record.TotalCount = Math.Max(record.TotalCount, testsToRun.TestCaseCount);
            }
            TryPersist(record, "记录测试开始状态");
        }

        private static void OnTestFinished(ITestResultAdaptor result)
        {
            if (s_TerminalCommitter.HasPending)
            {
                return;
            }

            var record = GetActiveForCallback();
            if (record == null || result == null || result.Test == null || result.Test.IsSuite)
            {
                return;
            }

            var detail = ToDetail(result);
            record.DetailCount++;
            record.CompletedCount++;
            IncrementStatus(record, detail.Status);
            AddBoundedDetail(record, detail);

            s_ResultsSincePersist++;
            var now = EditorApplication.timeSinceStartup;
            if (!detail.IsPassed || s_ResultsSincePersist >= ProgressPersistInterval ||
                now - s_LastPersistTime >= ProgressPersistSeconds)
            {
                TryPersist(record, "记录测试进度");
            }
        }

        private static void OnRunFinished(ITestResultAdaptor result)
        {
            if (s_TerminalCommitter.HasPending)
            {
                TryCommitPendingTerminal();
                return;
            }

            var activeRecord = GetActiveForCallback();
            if (activeRecord == null)
            {
                return;
            }

            try
            {
                // 不直接修改活动快照。终态只有写盘成功后才替换 running 状态。
                var record = activeRecord.Clone();
                record.Status = TestRunStatuses.Completed;
                record.FinishedAt = UtcNow();
                record.DurationSeconds = result != null ? Math.Max(0, result.Duration) : ElapsedSeconds(record.StartedAt);

                if (result != null)
                {
                    record.PassedCount = Math.Max(0, result.PassCount);
                    record.FailedCount = Math.Max(0, result.FailCount);
                    record.SkippedCount = Math.Max(0, result.SkipCount);
                    record.InconclusiveCount = Math.Max(0, result.InconclusiveCount);
                    record.CompletedCount = record.PassedCount + record.FailedCount +
                                            record.SkippedCount + record.InconclusiveCount;
                    record.TotalCount = Math.Max(record.TotalCount, record.CompletedCount);
                    record.ResultState = TestResultLimits.Truncate(result.ResultState, 256);
                    record.Message = TestResultLimits.TruncateRunText(result.Message);
                    record.StackTrace = TestResultLimits.TruncateRunText(result.StackTrace);
                    record.Output = TestResultLimits.TruncateRunText(result.Output);
                    try
                    {
                        RebuildFinalDetails(record, result);
                    }
                    catch (Exception detailError)
                    {
                        record.DetailsTruncated = true;
                        record.Message = TestResultLimits.TruncateRunText(
                            $"{record.Message}\n收集测试明细失败:{detailError.Message}");
                        Debug.LogError($"[AgentBridge] 收集最终测试明细失败:{detailError}");
                    }
                }
                else
                {
                    record.ResultState = "Unknown";
                    record.Message = "Unity Test Framework 完成回调未提供结果";
                }

                StageTerminal(record, "写入最终测试结果");
            }
            catch (Exception ex)
            {
                var record = activeRecord.Clone();
                record.Status = TestRunStatuses.Interrupted;
                record.FinishedAt = UtcNow();
                record.DurationSeconds = ElapsedSeconds(record.StartedAt);
                record.ResultState = "Interrupted";
                record.Message = TestResultLimits.TruncateRunText(
                    $"序列化最终测试结果失败:{ex.Message}");
                StageTerminal(record, "写入中断测试结果");
                Debug.LogError($"[AgentBridge] 处理测试完成回调失败:{ex}");
            }
        }

        private static void OnRunError(string message)
        {
            if (s_TerminalCommitter.HasPending)
            {
                TryCommitPendingTerminal();
                return;
            }

            var activeRecord = GetActiveForCallback();
            if (activeRecord == null)
            {
                return;
            }

            var record = activeRecord.Clone();
            record.Status = TestRunStatuses.Interrupted;
            record.FinishedAt = UtcNow();
            record.DurationSeconds = ElapsedSeconds(record.StartedAt);
            record.ResultState = "Failed:Error";
            record.Message = TestResultLimits.TruncateRunText(
                string.IsNullOrEmpty(message) ? "Unity Test Framework 运行失败" : message);
            StageTerminal(record, "写入测试框架错误结果");
        }

        private static void InterruptActiveRecord(string reason)
        {
            if (s_TerminalCommitter.HasPending)
            {
                TryCommitPendingTerminal();
                return;
            }

            var record = s_ActiveRecord;
            if (record == null)
            {
                Debug.LogError("[AgentBridge] 无法中断测试:活动锁存在但内存状态不可用,保留活动锁");
                return;
            }

            record = record.Clone();
            record.Status = TestRunStatuses.Interrupted;
            record.FinishedAt = UtcNow();
            record.DurationSeconds = ElapsedSeconds(record.StartedAt);
            record.ResultState = "Interrupted";
            record.Message = TestResultLimits.TruncateRunText(reason);
            StageTerminal(record, "写入失联测试结果");
        }

        private static TestRunRecord GetActiveForCallback()
        {
            if (s_ActiveRecord != null)
            {
                return s_ActiveRecord;
            }

            var runId = SessionState.GetString(ActiveRunSessionKey, "");
            if (string.IsNullOrEmpty(runId))
            {
                return null;
            }
            try
            {
                if (TestRunStore.TryLoad(runId, out var record) &&
                    record.Status == TestRunStatuses.Running)
                {
                    s_ActiveRecord = record;
                    return record;
                }
            }
            catch (CommandException ex)
            {
                Debug.LogError($"[AgentBridge] 读取活动测试状态失败:{ex.Message}");
            }
            return null;
        }

        private static void RebuildFinalDetails(TestRunRecord record, ITestResultAdaptor root)
        {
            var nonPassed = new List<TestCaseResultRecord>();
            var passed = new List<TestCaseResultRecord>();
            var stack = new Stack<ITestResultAdaptor>();
            stack.Push(root);
            var visited = 0;
            var detailCount = 0;
            var traversalTruncated = false;

            while (stack.Count > 0)
            {
                if (++visited > TestResultLimits.MaxTraversalNodes)
                {
                    traversalTruncated = true;
                    break;
                }

                var current = stack.Pop();
                if (current == null)
                {
                    continue;
                }

                if (current.HasChildren)
                {
                    var children = current.Children?.ToArray() ?? Array.Empty<ITestResultAdaptor>();
                    for (var i = children.Length - 1; i >= 0; i--)
                    {
                        stack.Push(children[i]);
                    }
                    continue;
                }

                if (current.Test == null || current.Test.IsSuite)
                {
                    continue;
                }

                detailCount++;
                var detail = ToDetail(current);
                if (detail.IsPassed)
                {
                    if (passed.Count < TestResultLimits.MaxStoredResults)
                    {
                        passed.Add(detail);
                    }
                }
                else if (nonPassed.Count < TestResultLimits.MaxStoredResults)
                {
                    nonPassed.Add(detail);
                }
            }

            var selected = new List<TestCaseResultRecord>(TestResultLimits.MaxStoredResults);
            var storedTextCharacters = 0;
            var budgetTruncated = false;
            foreach (var detail in nonPassed.Concat(passed))
            {
                var detailCharacters = detail.EstimatedTextCharacters;
                if (selected.Count >= TestResultLimits.MaxStoredResults ||
                    storedTextCharacters + detailCharacters > TestResultLimits.MaxStoredTextCharacters)
                {
                    budgetTruncated = true;
                    continue;
                }
                selected.Add(detail);
                storedTextCharacters += detailCharacters;
            }

            if (detailCount == 0 && record.CompletedCount > 0 && record.Results.Count > 0)
            {
                // 某些 Test Framework 版本/失败路径的根 adaptor 不再暴露 children。
                // 保留 TestFinished 已收集的有界快照,而不是用空列表覆盖。
                record.DetailCount = Math.Max(record.DetailCount, record.CompletedCount);
                record.DetailsTruncated = true;
                return;
            }

            record.DetailCount = Math.Max(detailCount, record.CompletedCount);
            record.Results = selected;
            record.StoredTextCharacters = storedTextCharacters;
            record.DetailsTruncated = traversalTruncated || budgetTruncated ||
                                      record.DetailCount > selected.Count;
        }

        private static TestCaseResultRecord ToDetail(ITestResultAdaptor result)
        {
            var test = result.Test;
            var categories = test?.Categories ?? Array.Empty<string>();
            return new TestCaseResultRecord
            {
                Id = TestResultLimits.Truncate(test?.Id, 1024),
                Name = TestResultLimits.Truncate(result.Name ?? test?.Name, 1024),
                FullName = TestResultLimits.Truncate(result.FullName ?? test?.FullName, 2048),
                Mode = TestResultLimits.Truncate(test != null ? test.TestMode.ToString() : "", 64),
                Status = TestResultLimits.Truncate(ClassifyStatus(result.ResultState), 64),
                ResultState = TestResultLimits.Truncate(result.ResultState, 256),
                DurationSeconds = Math.Max(0, result.Duration),
                Message = TestResultLimits.TruncateCaseText(result.Message),
                StackTrace = TestResultLimits.TruncateCaseText(result.StackTrace),
                Output = TestResultLimits.TruncateCaseText(result.Output),
                Categories = categories.Take(32)
                    .Select(value => TestResultLimits.Truncate(value ?? "", 256)).ToArray()
            };
        }

        private static string ClassifyStatus(string resultState)
        {
            if (string.IsNullOrEmpty(resultState))
            {
                return "Unknown";
            }
            if (resultState.StartsWith("Passed", StringComparison.OrdinalIgnoreCase))
            {
                return "Passed";
            }
            if (resultState.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
            {
                return "Failed";
            }
            if (resultState.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase))
            {
                return "Skipped";
            }
            if (resultState.StartsWith("Inconclusive", StringComparison.OrdinalIgnoreCase))
            {
                return "Inconclusive";
            }
            return resultState.Split(':')[0];
        }

        private static void IncrementStatus(TestRunRecord record, string status)
        {
            switch (status)
            {
                case "Passed": record.PassedCount++; break;
                case "Failed": record.FailedCount++; break;
                case "Skipped": record.SkippedCount++; break;
                case "Inconclusive": record.InconclusiveCount++; break;
            }
        }

        private static void AddBoundedDetail(TestRunRecord record, TestCaseResultRecord detail)
        {
            var existingIndex = !string.IsNullOrEmpty(detail.Id)
                ? record.Results.FindIndex(item => item.Id == detail.Id)
                : -1;
            if (existingIndex >= 0)
            {
                record.StoredTextCharacters = Math.Max(0,
                    record.StoredTextCharacters - record.Results[existingIndex].EstimatedTextCharacters);
                record.Results.RemoveAt(existingIndex);
            }

            if (!detail.IsPassed)
            {
                while (!CanStore(record, detail))
                {
                    var passedIndex = record.Results.FindLastIndex(item => item.IsPassed);
                    if (passedIndex < 0)
                    {
                        break;
                    }
                    record.StoredTextCharacters = Math.Max(0,
                        record.StoredTextCharacters - record.Results[passedIndex].EstimatedTextCharacters);
                    record.Results.RemoveAt(passedIndex);
                    record.DetailsTruncated = true;
                }
            }

            if (CanStore(record, detail))
            {
                record.Results.Add(detail);
                record.StoredTextCharacters += detail.EstimatedTextCharacters;
                return;
            }
            record.DetailsTruncated = true;
        }

        private static bool CanStore(TestRunRecord record, TestCaseResultRecord detail)
        {
            return record.Results.Count < TestResultLimits.MaxStoredResults &&
                   record.StoredTextCharacters + detail.EstimatedTextCharacters <=
                   TestResultLimits.MaxStoredTextCharacters;
        }

        private static void StageTerminal(TestRunRecord record, string operation)
        {
            StopRestoredRunVerification();
            if (s_TerminalCommitter.HasPending)
            {
                if (s_TerminalCommitter.PendingRunId != record.RunId)
                {
                    Debug.LogError(
                        $"[AgentBridge] 测试终态提交冲突:等待 '{s_TerminalCommitter.PendingRunId}',拒绝覆盖为 '{record.RunId}'");
                }
                TryCommitPendingTerminal();
                return;
            }

            s_TerminalCommitter.Stage(record, operation);
            TryCommitPendingTerminal();
        }

        private static void TryCommitPendingTerminal()
        {
            if (!s_TerminalCommitter.HasPending)
            {
                StopTerminalPersistRetry();
                return;
            }

            if (s_TerminalCommitter.TryCommit(out var failure))
            {
                return;
            }

            ScheduleTerminalPersistRetry(failure);
        }

        private static void ScheduleTerminalPersistRetry(Exception failure)
        {
            var exponent = Math.Min(5, Math.Max(0, s_TerminalCommitter.FailureCount - 1));
            var delay = Math.Min(TerminalPersistMaxRetrySeconds,
                TerminalPersistInitialRetrySeconds * Math.Pow(2, exponent));
            s_NextTerminalPersistRetry = EditorApplication.timeSinceStartup + delay;
            EditorApplication.update -= RetryTerminalPersistOnUpdate;
            EditorApplication.update += RetryTerminalPersistOnUpdate;

            var detail = failure is CommandException commandError
                ? $"{commandError.Code}:{commandError.Message}"
                : $"{failure?.GetType().Name}:{failure?.Message}";
            Debug.LogError(
                $"[AgentBridge] {s_TerminalCommitter.Operation}失败:{detail};{delay:0.##} 秒后重试,活动测试锁保持");
        }

        private static void RetryTerminalPersistOnUpdate()
        {
            if (!s_TerminalCommitter.HasPending)
            {
                StopTerminalPersistRetry();
                return;
            }
            if (EditorApplication.timeSinceStartup < s_NextTerminalPersistRetry)
            {
                return;
            }
            TryCommitPendingTerminal();
        }

        private static void StopTerminalPersistRetry()
        {
            s_NextTerminalPersistRetry = 0;
            EditorApplication.update -= RetryTerminalPersistOnUpdate;
        }

        private static void OnTerminalCommitted(TestRunRecord committed)
        {
            StopTerminalPersistRetry();
            s_LastRecord = committed;

            var memoryRunId = s_ActiveRecord?.RunId ?? "";
            var sessionRunId = "";
            try
            {
                sessionRunId = SessionState.GetString(ActiveRunSessionKey, "");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentBridge] 读取测试 SessionState 失败:{ex.Message}");
            }

            if ((!string.IsNullOrEmpty(memoryRunId) && memoryRunId != committed.RunId) ||
                (!string.IsNullOrEmpty(sessionRunId) && sessionRunId != committed.RunId))
            {
                Debug.LogError(
                    $"[AgentBridge] 测试终态 '{committed.RunId}' 已写盘,但当前活动锁属于 memory='{memoryRunId}',session='{sessionRunId}',不会清理不相关锁");
                return;
            }

            ClearActiveState();
        }

        private static void TryPersist(TestRunRecord record, string operation)
        {
            // 终态待提交期间禁止进度快照把磁盘重新覆盖为 running。
            if (s_TerminalCommitter.HasPending)
            {
                return;
            }
            try
            {
                TestRunStore.Save(record);
                s_ResultsSincePersist = 0;
                s_LastPersistTime = EditorApplication.timeSinceStartup;
            }
            catch (CommandException ex)
            {
                Debug.LogError($"[AgentBridge] {operation}失败:{ex.Code}:{ex.Message}");
            }
        }

        private static void ClearActiveState()
        {
            StopRestoredRunVerification();
            try
            {
                SessionState.EraseString(ActiveRunSessionKey);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentBridge] 清理测试 SessionState 失败:{ex.Message}");
                try { SessionState.SetString(ActiveRunSessionKey, ""); }
                catch (Exception fallbackError)
                {
                    Debug.LogError($"[AgentBridge] 重置测试 SessionState 失败:{fallbackError.Message}");
                }
            }
            s_ActiveRecord = null;
            s_ResultsSincePersist = 0;
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        private static double ElapsedSeconds(string startedAt)
        {
            if (!DateTime.TryParse(startedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var started))
            {
                return 0;
            }
            return Math.Max(0, (DateTime.UtcNow - started.ToUniversalTime()).TotalSeconds);
        }

        private sealed class CallbackReceiver : IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                try { OnRunStarted(testsToRun); }
                catch (Exception ex) { Debug.LogError($"[AgentBridge] RunStarted callback 失败:{ex}"); }
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                try { OnRunFinished(result); }
                catch (Exception ex) { Debug.LogError($"[AgentBridge] RunFinished callback 失败:{ex}"); }
            }

            public void TestStarted(ITestAdaptor test)
            {
                // 无需逐项记录开始事件;TestFinished 才有稳定状态与输出。
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                try { OnTestFinished(result); }
                catch (Exception ex) { Debug.LogError($"[AgentBridge] TestFinished callback 失败:{ex}"); }
            }

            public void OnError(string message)
            {
                try { OnRunError(message); }
                catch (Exception ex) { Debug.LogError($"[AgentBridge] test error callback 失败:{ex}"); }
            }
        }
    }
}
