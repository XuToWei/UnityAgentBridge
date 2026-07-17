using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>查询 run_tests 返回的运行句柄;运行中与测试失败均通过正常 result 表达。</summary>
    public sealed class GetTestResultHandler : ICommandHandler
    {
        public string Command => "get_test_result";

        public string Description =>
            "查询异步 Unity 测试结果(runId 可省略取活动/最新;includePassed 默认 false;limit 默认 100);测试失败仍返回 status=ok";

        public string Group => "Testing";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var runId = @params?["runId"]?.Value<string>();
            var includePassed = @params?["includePassed"]?.ToObject<bool?>() ?? false;
            var limit = @params?["limit"]?.ToObject<int?>() ?? TestResultLimits.DefaultReturnedResults;
            if (limit <= 0 || limit > TestResultLimits.MaxReturnedResults)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    $"limit 必须在 1..{TestResultLimits.MaxReturnedResults} 之间");
            }

            var record = TestRunMonitor.Get(runId);
            var matched = record.Results
                .Where(result => includePassed || !result.IsPassed)
                .ToArray();
            var selected = new System.Collections.Generic.List<TestCaseResultRecord>();
            var returnedTextCharacters = 0;
            foreach (var result in matched)
            {
                var textCharacters = result.EstimatedTextCharacters;
                if (selected.Count >= limit ||
                    returnedTextCharacters + textCharacters > TestResultLimits.MaxReturnedTextCharacters)
                {
                    break;
                }
                selected.Add(result);
                returnedTextCharacters += textCharacters;
            }
            var results = selected.ToArray();
            bool? success = record.Status == TestRunStatuses.Completed
                ? record.FailedCount == 0 &&
                  (record.ResultState ?? "").StartsWith("Passed", StringComparison.OrdinalIgnoreCase)
                : (bool?)null;

            return Task.FromResult<object>(new
            {
                runId = record.RunId,
                frameworkRunId = record.FrameworkRunId,
                status = record.Status,
                mode = record.Mode,
                success,
                startedAt = record.StartedAt,
                finishedAt = record.FinishedAt,
                durationSeconds = record.DurationSeconds,
                filter = record.Filter,
                summary = new
                {
                    total = record.TotalCount,
                    completed = record.CompletedCount,
                    passed = record.PassedCount,
                    failed = record.FailedCount,
                    skipped = record.SkippedCount,
                    inconclusive = record.InconclusiveCount,
                    resultState = record.ResultState
                },
                message = record.Message,
                stackTrace = record.StackTrace,
                output = record.Output,
                details = new
                {
                    available = record.DetailCount,
                    stored = record.Results.Count,
                    matchedStored = matched.Length,
                    returned = results.Length,
                    storedTextCharacters = record.StoredTextCharacters,
                    returnedTextCharacters,
                    responseTextBudgetCharacters = TestResultLimits.MaxReturnedTextCharacters,
                    storageTruncated = record.DetailsTruncated,
                    truncated = record.DetailsTruncated || matched.Length > results.Length,
                    includePassed,
                    limit
                },
                results
            });
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""runId"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 64, ""pattern"": ""^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$"", ""description"": ""run_tests 返回的 runId;省略时取活动运行,否则取最新持久化结果。"" },
    ""includePassed"": { ""type"": ""boolean"", ""default"": false, ""description"": ""是否在 results 中包含通过用例;默认只返回非通过用例。"" },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200, ""default"": 100, ""description"": ""本次最多返回的用例明细数;同时受响应总文本预算限制。"" }
  },
  ""additionalProperties"": false
}");
    }
}
