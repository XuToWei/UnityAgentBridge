using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace AgentBridge
{
    internal static class TestRunStatuses
    {
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Interrupted = "interrupted";

        public static bool IsKnown(string value)
        {
            return value == Running || value == Completed || value == Interrupted;
        }
    }

    internal static class TestResultLimits
    {
        public const int MaxStoredResults = 500;
        public const int MaxReturnedResults = 200;
        public const int DefaultReturnedResults = 100;
        public const int MaxFilterItems = 256;
        public const int MaxFilterTextLength = 1024;
        public const int MaxFilterTotalTextLength = 65536;
        public const int MaxCaseTextLength = 4096;
        public const int MaxRunTextLength = 8192;
        public const int MaxTraversalNodes = 100000;
        public const int MaxStoredTextCharacters = 524288;
        public const int MaxReturnedTextCharacters = 131072;

        private const string TruncatedSuffix = "\n...[truncated]";

        public static string TruncateCaseText(string value)
        {
            return Truncate(value, MaxCaseTextLength);
        }

        public static string TruncateRunText(string value)
        {
            return Truncate(value, MaxRunTextLength);
        }

        public static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? "";
            }

            var prefixLength = Math.Max(0, maxLength - TruncatedSuffix.Length);
            return value.Substring(0, prefixLength) + TruncatedSuffix;
        }
    }

    [Serializable]
    internal sealed class TestRunFilterRecord
    {
        [JsonProperty("testNames")] public string[] TestNames { get; set; } = Array.Empty<string>();
        [JsonProperty("groupNames")] public string[] GroupNames { get; set; } = Array.Empty<string>();
        [JsonProperty("categoryNames")] public string[] CategoryNames { get; set; } = Array.Empty<string>();
        [JsonProperty("assemblyNames")] public string[] AssemblyNames { get; set; } = Array.Empty<string>();

        public TestRunFilterRecord Clone()
        {
            return new TestRunFilterRecord
            {
                TestNames = TestNames?.ToArray() ?? Array.Empty<string>(),
                GroupNames = GroupNames?.ToArray() ?? Array.Empty<string>(),
                CategoryNames = CategoryNames?.ToArray() ?? Array.Empty<string>(),
                AssemblyNames = AssemblyNames?.ToArray() ?? Array.Empty<string>()
            };
        }
    }

    [Serializable]
    internal sealed class TestCaseResultRecord
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("fullName")] public string FullName { get; set; } = "";
        [JsonProperty("mode")] public string Mode { get; set; } = "";
        [JsonProperty("status")] public string Status { get; set; } = "";
        [JsonProperty("resultState")] public string ResultState { get; set; } = "";
        [JsonProperty("durationSeconds")] public double DurationSeconds { get; set; }
        [JsonProperty("message")] public string Message { get; set; } = "";
        [JsonProperty("stackTrace")] public string StackTrace { get; set; } = "";
        [JsonProperty("output")] public string Output { get; set; } = "";
        [JsonProperty("categories")] public string[] Categories { get; set; } = Array.Empty<string>();

        [JsonIgnore]
        public bool IsPassed => string.Equals(Status, "Passed", StringComparison.OrdinalIgnoreCase) ||
                                (ResultState ?? "").StartsWith("Passed", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public int EstimatedTextCharacters => Length(Id) + Length(Name) + Length(FullName) + Length(Mode) +
                                              Length(Status) + Length(ResultState) + Length(Message) +
                                              Length(StackTrace) + Length(Output) +
                                              (Categories?.Sum(Length) ?? 0);

        private static int Length(string value)
        {
            return value?.Length ?? 0;
        }

        public TestCaseResultRecord Clone()
        {
            return new TestCaseResultRecord
            {
                Id = Id,
                Name = Name,
                FullName = FullName,
                Mode = Mode,
                Status = Status,
                ResultState = ResultState,
                DurationSeconds = DurationSeconds,
                Message = Message,
                StackTrace = StackTrace,
                Output = Output,
                Categories = Categories?.ToArray() ?? Array.Empty<string>()
            };
        }
    }

    [Serializable]
    internal sealed class TestRunRecord
    {
        [JsonProperty("v")] public int Version { get; set; } = 1;
        [JsonProperty("runId")] public string RunId { get; set; } = "";
        [JsonProperty("frameworkRunId")] public string FrameworkRunId { get; set; } = "";
        [JsonProperty("mode")] public string Mode { get; set; } = "edit";
        [JsonProperty("status")] public string Status { get; set; } = TestRunStatuses.Running;
        [JsonProperty("startedAt")] public string StartedAt { get; set; } = "";
        [JsonProperty("finishedAt")] public string FinishedAt { get; set; } = "";
        [JsonProperty("durationSeconds")] public double DurationSeconds { get; set; }
        [JsonProperty("filter")] public TestRunFilterRecord Filter { get; set; } = new TestRunFilterRecord();
        [JsonProperty("savedScenes")] public string[] SavedScenes { get; set; } = Array.Empty<string>();
        [JsonProperty("totalCount")] public int TotalCount { get; set; }
        [JsonProperty("completedCount")] public int CompletedCount { get; set; }
        [JsonProperty("passedCount")] public int PassedCount { get; set; }
        [JsonProperty("failedCount")] public int FailedCount { get; set; }
        [JsonProperty("skippedCount")] public int SkippedCount { get; set; }
        [JsonProperty("inconclusiveCount")] public int InconclusiveCount { get; set; }
        [JsonProperty("resultState")] public string ResultState { get; set; } = "";
        [JsonProperty("message")] public string Message { get; set; } = "";
        [JsonProperty("stackTrace")] public string StackTrace { get; set; } = "";
        [JsonProperty("output")] public string Output { get; set; } = "";
        [JsonProperty("detailCount")] public int DetailCount { get; set; }
        [JsonProperty("detailsTruncated")] public bool DetailsTruncated { get; set; }
        [JsonProperty("storedTextCharacters")] public int StoredTextCharacters { get; set; }
        [JsonProperty("results")] public List<TestCaseResultRecord> Results { get; set; } = new List<TestCaseResultRecord>();

        [JsonIgnore]
        public bool IsTerminal => Status == TestRunStatuses.Completed || Status == TestRunStatuses.Interrupted;

        public TestRunRecord Clone()
        {
            return new TestRunRecord
            {
                Version = Version,
                RunId = RunId,
                FrameworkRunId = FrameworkRunId,
                Mode = Mode,
                Status = Status,
                StartedAt = StartedAt,
                FinishedAt = FinishedAt,
                DurationSeconds = DurationSeconds,
                Filter = Filter?.Clone() ?? new TestRunFilterRecord(),
                SavedScenes = SavedScenes?.ToArray() ?? Array.Empty<string>(),
                TotalCount = TotalCount,
                CompletedCount = CompletedCount,
                PassedCount = PassedCount,
                FailedCount = FailedCount,
                SkippedCount = SkippedCount,
                InconclusiveCount = InconclusiveCount,
                ResultState = ResultState,
                Message = Message,
                StackTrace = StackTrace,
                Output = Output,
                DetailCount = DetailCount,
                DetailsTruncated = DetailsTruncated,
                StoredTextCharacters = StoredTextCharacters,
                Results = Results?.Where(item => item != null).Select(item => item.Clone()).ToList() ??
                          new List<TestCaseResultRecord>()
            };
        }
    }
}
