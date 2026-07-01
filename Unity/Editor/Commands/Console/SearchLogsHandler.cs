using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// search_logs(只读):搜索编辑器 Console 面板当前的日志条目。数据源是 Console 里现有的
    /// 条目(清空 Console 即清空);读取经 ConsoleLogReader 反射内部 LogEntries。
    /// params 全部可选:query(子串,或 regex=true 时按正则)、type(error/warning/log 过滤)、
    /// ignoreCase(默认 true)、limit(返回上限,默认 100,最多 1000)。无 query → 按过滤条件返回最新若干条。
    /// </summary>
    public sealed class SearchLogsHandler : ICommandHandler
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 1000;

        public string Command => "search_logs";

        public string Description =>
            "搜索编辑器 Console 当前日志条目:query(子串,regex=true 则正则)/type(error|warning|log)/ignoreCase/limit;返回 {total,matched,truncated,entries:[{message,type,file,line}]}";

        public string Group => "Console";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var query = @params?["query"]?.Value<string>();
            var useRegex = @params?["regex"]?.ToObject<bool?>() ?? false;
            var ignoreCase = @params?["ignoreCase"]?.ToObject<bool?>() ?? true;
            var typeFilter = @params?["type"]?.Value<string>();
            var limit = @params?["limit"]?.ToObject<int?>() ?? DefaultLimit;
            if (limit <= 0)
            {
                limit = DefaultLimit;
            }
            limit = Math.Min(limit, MaxLimit);

            if (!string.IsNullOrEmpty(typeFilter))
            {
                typeFilter = typeFilter.ToLowerInvariant();
                if (typeFilter != "error" && typeFilter != "warning" && typeFilter != "log")
                {
                    throw new CommandException(ErrorCodes.InvalidParams,
                        "type 只能是 error / warning / log 之一。");
                }
            }

            Regex regex = null;
            if (useRegex && !string.IsNullOrEmpty(query))
            {
                try
                {
                    var options = RegexOptions.CultureInvariant;
                    if (ignoreCase)
                    {
                        options |= RegexOptions.IgnoreCase;
                    }
                    regex = new Regex(query, options);
                }
                catch (ArgumentException ex)
                {
                    throw new CommandException(ErrorCodes.InvalidParams, "正则表达式无效: " + ex.Message);
                }
            }

            var all = ConsoleLogReader.ReadAll();

            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var matched = new List<ConsoleLogReader.Entry>();
            foreach (var e in all)
            {
                if (!string.IsNullOrEmpty(typeFilter) && e.Type != typeFilter)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(query))
                {
                    var message = e.Message ?? "";
                    var hit = regex != null
                        ? regex.IsMatch(message)
                        : message.IndexOf(query, comparison) >= 0;
                    if (!hit)
                    {
                        continue;
                    }
                }
                matched.Add(e);
            }

            var truncated = matched.Count > limit;
            var entries = matched.Take(limit).Select(e => new
            {
                message = e.Message,
                type = e.Type,
                file = e.File,
                line = e.Line
            }).ToArray();

            return new
            {
                total = all.Count,       // Console 里总条目数
                matched = matched.Count, // 命中过滤/搜索的条目数
                truncated,               // 命中数超过 limit,已截断
                entries
            };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""query"": { ""type"": ""string"", ""description"": ""搜索词;默认按子串匹配 message,regex=true 时按正则。留空则只按 type 过滤。"" },
    ""regex"": { ""type"": ""boolean"", ""description"": ""query 是否按正则匹配,默认 false。"" },
    ""ignoreCase"": { ""type"": ""boolean"", ""description"": ""是否忽略大小写,默认 true。"" },
    ""type"": { ""type"": ""string"", ""enum"": [""error"", ""warning"", ""log""], ""description"": ""只返回该类型的条目;缺省不限。"" },
    ""limit"": { ""type"": ""integer"", ""description"": ""返回条目上限,默认 100,最多 1000。"" }
  }
}");
        }
    }
}
