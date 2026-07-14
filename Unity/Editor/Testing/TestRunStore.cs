using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>把测试运行状态原子持久化到 .agentbridge/test-results。</summary>
    internal static class TestRunStore
    {
        private const string FilePrefix = "test-run-";
        private const string FileExtension = ".json";

        private static string ResultDirectory => Path.Combine(BridgeSettings.RootDir, "test-results");

        public static void Save(TestRunRecord record)
        {
            if (record == null || !IsValidRunId(record.RunId))
            {
                throw new CommandException(TestErrorCodes.TestRunStateCorrupt, "测试运行状态缺少合法 runId");
            }

            try
            {
                Directory.CreateDirectory(ResultDirectory);
                EnforceResultBudget(record);
                var json = JsonConvert.SerializeObject(record, Formatting.Indented);
                AtomicFilePublisher.Publish(GetPath(record.RunId), true,
                    temp => File.WriteAllText(temp, json, new UTF8Encoding(false)));
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex) when (IsIoException(ex))
            {
                throw new CommandException(TestErrorCodes.TestResultIoError,
                    "写入测试结果失败:" + ex.Message);
            }
        }

        public static bool TryLoad(string runId, out TestRunRecord record)
        {
            RequireValidRunId(runId);
            var path = GetPath(runId);
            if (!File.Exists(path))
            {
                record = null;
                return false;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                record = JsonConvert.DeserializeObject<TestRunRecord>(json);
            }
            catch (JsonException ex)
            {
                throw new CommandException(TestErrorCodes.TestRunStateCorrupt,
                    $"测试结果 JSON 损坏:'{runId}':{ex.Message}");
            }
            catch (Exception ex) when (IsIoException(ex))
            {
                throw new CommandException(TestErrorCodes.TestResultIoError,
                    $"读取测试结果失败:'{runId}':{ex.Message}");
            }

            ValidateLoadedRecord(runId, record);
            return true;
        }

        public static string FindLatestRunId()
        {
            try
            {
                if (!Directory.Exists(ResultDirectory))
                {
                    return null;
                }

                var file = Directory.GetFiles(ResultDirectory, FilePrefix + "*" + FileExtension)
                    .Select(path => new { path, written = File.GetLastWriteTimeUtc(path) })
                    .OrderByDescending(item => item.written)
                    .ThenByDescending(item => item.path, StringComparer.Ordinal)
                    .FirstOrDefault();
                return file == null ? null : Path.GetFileNameWithoutExtension(file.path);
            }
            catch (Exception ex) when (IsIoException(ex))
            {
                throw new CommandException(TestErrorCodes.TestResultIoError,
                    "枚举测试结果失败:" + ex.Message);
            }
        }

        public static void DeleteBestEffort(string runId)
        {
            if (!IsValidRunId(runId))
            {
                return;
            }
            AtomicFilePublisher.DeleteBestEffort(GetPath(runId));
        }

        public static bool IsValidRunId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
            {
                return false;
            }
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || (i > 0 && (c == '_' || c == '-')))
                {
                    continue;
                }
                return false;
            }
            return true;
        }

        private static void RequireValidRunId(string runId)
        {
            if (!IsValidRunId(runId))
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "runId 必须匹配 [A-Za-z0-9][A-Za-z0-9_-]{0,63}");
            }
        }

        private static string GetPath(string runId)
        {
            return Path.Combine(ResultDirectory, runId + FileExtension);
        }

        private static void ValidateLoadedRecord(string expectedRunId, TestRunRecord record)
        {
            if (record == null || record.Version != 1 || record.RunId != expectedRunId ||
                !TestRunStatuses.IsKnown(record.Status))
            {
                throw new CommandException(TestErrorCodes.TestRunStateCorrupt,
                    $"测试结果状态无效:'{expectedRunId}'");
            }
            record.Filter = record.Filter ?? new TestRunFilterRecord();
            record.SavedScenes = record.SavedScenes ?? Array.Empty<string>();
            record.Results = record.Results ?? new System.Collections.Generic.List<TestCaseResultRecord>();
            EnforceResultBudget(record);
        }

        private static void EnforceResultBudget(TestRunRecord record)
        {
            var original = record.Results.Where(item => item != null).ToArray();
            var ordered = original.Where(item => !item.IsPassed)
                .Concat(original.Where(item => item.IsPassed));
            var selected = new System.Collections.Generic.List<TestCaseResultRecord>();
            var textCharacters = 0;
            foreach (var detail in ordered)
            {
                var detailCharacters = detail.EstimatedTextCharacters;
                if (selected.Count >= TestResultLimits.MaxStoredResults ||
                    textCharacters + detailCharacters > TestResultLimits.MaxStoredTextCharacters)
                {
                    record.DetailsTruncated = true;
                    continue;
                }
                selected.Add(detail);
                textCharacters += detailCharacters;
            }

            if (selected.Count < original.Length)
            {
                record.DetailsTruncated = true;
            }
            record.Results = selected;
            record.StoredTextCharacters = textCharacters;
        }

        private static bool IsIoException(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException ||
                   ex is ArgumentException || ex is NotSupportedException;
        }

    }
}
