using System;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>异步启动 Unity Test Framework 的 EditMode 或 PlayMode 测试。</summary>
    public sealed class RunTestsHandler : ICommandHandler
    {
        public string Command => "run_tests";

        public string Description =>
            "异步运行 Unity Test Framework(mode=edit|play,可过滤);dirty 场景默认拒绝、ifUnsaved=save 可非交互保存;返回 runId";

        public string Group => "Testing";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var mode = (@params?["mode"]?.Value<string>() ?? "edit").ToLowerInvariant();
            if (mode != "edit" && mode != "play")
            {
                throw new CommandException(ErrorCodes.InvalidParams, "mode 只能是 edit 或 play");
            }

            var groupNames = ReadFilter(@params, "groupNames");
            ValidateGroupPatterns(groupNames);
            var filter = new TestRunFilterRecord
            {
                TestNames = ReadFilter(@params, "testNames"),
                GroupNames = groupNames,
                CategoryNames = ReadFilter(@params, "categoryNames"),
                AssemblyNames = ReadFilter(@params, "assemblyNames")
            };
            var totalFilterCharacters = filter.TestNames.Concat(filter.GroupNames)
                .Concat(filter.CategoryNames).Concat(filter.AssemblyNames)
                .Sum(value => value.Length);
            if (totalFilterCharacters > TestResultLimits.MaxFilterTotalTextLength)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    $"过滤条件总文本最长 {TestResultLimits.MaxFilterTotalTextLength} 个字符");
            }
            var ifUnsaved = @params?["ifUnsaved"]?.Value<string>() ?? "error";
            var record = TestRunMonitor.Start(mode, filter, ifUnsaved);

            return Task.FromResult<object>(new
            {
                runId = record.RunId,
                frameworkRunId = record.FrameworkRunId,
                status = record.Status,
                mode = record.Mode,
                startedAt = record.StartedAt,
                savedScenes = record.SavedScenes,
                filter = record.Filter
            });
        }

        private static string[] ReadFilter(JObject @params, string propertyName)
        {
            var token = @params?[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return Array.Empty<string>();
            }

            var values = token.ToObject<string[]>() ?? Array.Empty<string>();
            var normalized = values.Select(value => value?.Trim())
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (normalized.Length != values.Length)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    $"{propertyName} 不能包含 null、空白或重复项");
            }
            return normalized;
        }

        private static void ValidateGroupPatterns(string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                try
                {
                    _ = new Regex(pattern, RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException ex)
                {
                    throw new CommandException(ErrorCodes.InvalidParams,
                        $"groupNames 含无效正则表达式:{ex.Message}");
                }
            }
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""enum"": [""edit"", ""play""], ""default"": ""edit"", ""description"": ""运行 EditMode 或 PlayMode 测试;如需两者请分别运行。"" },
    ""ifUnsaved"": { ""type"": ""string"", ""enum"": [""error"", ""save""], ""default"": ""error"", ""description"": ""测试框架启动前如何处理 dirty 场景;默认拒绝,save 会非交互保存全部已命名场景。"" },
    ""testNames"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1024 } },
    ""groupNames"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1024 } },
    ""categoryNames"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1024 } },
    ""assemblyNames"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1024 } }
  },
  ""additionalProperties"": false
}");
    }
}
