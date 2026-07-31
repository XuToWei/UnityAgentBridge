using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// get_profiler_data(只读):读取 Profiler Window 当前缓冲或加载的 CPU capture，
    /// 返回实际帧摘要和按 category + 完整调用路径聚合的热点。
    /// </summary>
    public sealed class GetProfilerDataHandler : ICommandHandler
    {
        public string Command => "get_profiler_data";

        public string Description =>
            "读取当前 Profiler CPU capture 的帧摘要与路径热点；支持单/多线程选择、Category/阈值过滤和按需 P95/P99/慢点/趋势详情";

        public string Group => "Profiling";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var threadSelector = ParseThreadSelector(
                @params?["threadSelector"] as JObject);
            var options = new ProfilerQueryOptions
            {
                EndFrameIndex = @params?["endFrameIndex"]?.ToObject<int?>(),
                FrameCount = @params?["frameCount"]?.ToObject<int?>() ??
                             ProfilerQueryOptions.DefaultFrameCount,
                ThreadIndex = @params?["threadIndex"]?.ToObject<int?>() ?? 0,
                ThreadSelector = threadSelector,
                Query = @params?["query"]?.Value<string>(),
                Categories = (@params?["categories"] as JArray)?
                                 .Values<string>()
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToArray() ??
                             Array.Empty<string>(),
                MinSelfTimeSumMs =
                    @params?["minSelfTimeSumMs"]?.ToObject<double?>() ?? 0,
                MinGcAllocSumBytes =
                    @params?["minGcAllocSumBytes"]?.ToObject<long?>() ?? 0,
                MinCallCount =
                    @params?["minCallCount"]?.ToObject<long?>() ?? 0,
                SortBy = @params?["sortBy"]?.Value<string>() ??
                         ProfilerQueryOptions.DefaultSortBy,
                MaxDepth = @params?["maxDepth"]?.ToObject<int?>() ??
                           ProfilerQueryOptions.DefaultMaxDepth,
                IncludeEditorOnly = @params?["includeEditorOnly"]?.ToObject<bool?>() ?? false,
                Limit = @params?["limit"]?.ToObject<int?>() ??
                        (threadSelector == null
                            ? ProfilerQueryOptions.DefaultLimit
                            : 25),
                HotspotDetails = ParseHotspotDetails(
                    @params?["hotspotDetails"] as JObject)
            };

            var captureId = @params?["captureId"]?.Value<string>();
            var result = ProfilerSavedCaptureAccess.Read(
                captureId,
                () => ProfilerDataReader.Query(options),
                false,
                out var immutable);
            ProfilerDataReader.MarkCaptureSource(
                result,
                immutable ? "saved" : "current",
                immutable);
            return Task.FromResult(result);
        }

        internal static ProfilerThreadSelector ParseThreadSelector(JObject value)
        {
            if (value == null)
            {
                return null;
            }

            var selector = new ProfilerThreadSelector
            {
                Mode = value["mode"]?.Value<string>(),
                Index = value["index"]?.ToObject<int?>() ?? 0,
                Name = value["name"]?.Value<string>(),
                Group = value["group"]?.Value<string>(),
                Offset = value["offset"]?.ToObject<int?>() ?? 0,
                MaxThreads = value["maxThreads"]?.ToObject<int?>() ??
                             ProfilerThreadSelector.DefaultMaxThreads
            };
            if (value["id"] != null)
            {
                if (!ulong.TryParse(
                        value["id"].Value<string>(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var id))
                {
                    throw new CommandException(
                        ErrorCodes.InvalidParams,
                        "threadSelector.id 必须是 UInt64 十进制字符串");
                }
                selector.Id = id;
            }
            return selector;
        }

        internal static ProfilerHotspotDetailsOptions ParseHotspotDetails(
            JObject value)
        {
            if (value == null)
            {
                return null;
            }
            return new ProfilerHotspotDetailsOptions
            {
                Metric = value["metric"]?.Value<string>() ?? "selfTimeMs",
                SlowestLimit = value["slowestLimit"]?.ToObject<int?>() ??
                               ProfilerHotspotDetailsOptions.DefaultSlowestLimit,
                TrendFrameCount = value["trendFrameCount"]?.ToObject<int?>() ??
                                  ProfilerHotspotDetailsOptions.DefaultTrendFrameCount,
                HotspotLimit = value["hotspotLimit"]?.ToObject<int?>() ??
                               ProfilerHotspotDetailsOptions.DefaultHotspotLimit
            };
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""not"": { ""required"": [""threadIndex"", ""threadSelector""] },
  ""properties"": {
    ""captureId"": {
      ""type"": ""string"",
      ""pattern"": ""^[0-9a-fA-F]{32}$"",
      ""description"": ""可选 capture_profiler ID；缺省读取当前缓冲。已保存 capture 会在查询后自动恢复原 Profiler 缓冲。""
    },
    ""endFrameIndex"": {
      ""type"": ""integer"",
      ""minimum"": 0,
      ""maximum"": 2147483647,
      ""description"": ""查询窗口的结束帧。缺省时在命令开始时固定为 Profiler 最新帧。""
    },
    ""frameCount"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 120,
      ""default"": 30,
      ""description"": ""从结束帧向前选择的实际 Profiler 帧数；通过帧导航读取，不假定索引连续。""
    },
    ""threadIndex"": {
      ""type"": ""integer"",
      ""minimum"": 0,
      ""maximum"": 1023,
      ""default"": 0,
      ""description"": ""结束帧中的 CPU 线程索引，默认 0；跨帧按持久 threadId 追踪同一线程。""
    },
    ""threadSelector"": {
      ""description"": ""显式线程选择；与 legacy threadIndex 互斥。传入后响应使用 threadSelection/threadResults，多线程逐线程返回而不合并热点。"",
      ""oneOf"": [
        {
          ""type"": ""object"",
          ""additionalProperties"": false,
          ""required"": [""mode"", ""index""],
          ""properties"": {
            ""mode"": { ""const"": ""index"" },
            ""index"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 1023 }
          }
        },
        {
          ""type"": ""object"",
          ""additionalProperties"": false,
          ""required"": [""mode"", ""id""],
          ""properties"": {
            ""mode"": { ""const"": ""id"" },
            ""id"": { ""type"": ""string"", ""pattern"": ""^(0|[1-9][0-9]{0,19})$"" }
          }
        },
        {
          ""type"": ""object"",
          ""additionalProperties"": false,
          ""required"": [""mode"", ""name""],
          ""properties"": {
            ""mode"": { ""const"": ""name"" },
            ""name"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 128 },
            ""group"": { ""type"": ""string"", ""maxLength"": 64 },
            ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 1023, ""default"": 0 },
            ""maxThreads"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 16, ""default"": 4 }
          }
        },
        {
          ""type"": ""object"",
          ""additionalProperties"": false,
          ""required"": [""mode"", ""group""],
          ""properties"": {
            ""mode"": { ""const"": ""group"" },
            ""group"": { ""type"": ""string"", ""maxLength"": 64 },
            ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 1023, ""default"": 0 },
            ""maxThreads"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 16, ""default"": 4 }
          }
        },
        {
          ""type"": ""object"",
          ""additionalProperties"": false,
          ""required"": [""mode""],
          ""properties"": {
            ""mode"": { ""const"": ""all"" },
            ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 1023, ""default"": 0 },
            ""maxThreads"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 16, ""default"": 4 }
          }
        }
      ]
    },
    ""query"": {
      ""type"": ""string"",
      ""maxLength"": 256,
      ""description"": ""对热点 name 或完整 path 做忽略大小写的子串过滤。""
    },
    ""categories"": {
      ""type"": ""array"",
      ""maxItems"": 16,
      ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 64 },
      ""description"": ""Category 忽略大小写精确匹配；多项为 OR，缺省为全部。""
    },
    ""minSelfTimeSumMs"": {
      ""type"": ""number"",
      ""minimum"": 0,
      ""maximum"": 1000000000000,
      ""default"": 0,
      ""description"": ""窗口 self time 总和下限；与其他阈值按 AND 组合。""
    },
    ""minGcAllocSumBytes"": {
      ""type"": ""integer"",
      ""minimum"": 0,
      ""maximum"": 9223372036854775807,
      ""default"": 0,
      ""description"": ""窗口 GC Alloc 总字节下限。""
    },
    ""minCallCount"": {
      ""type"": ""integer"",
      ""minimum"": 0,
      ""maximum"": 9223372036854775807,
      ""default"": 0,
      ""description"": ""窗口调用总次数下限。""
    },
    ""sortBy"": {
      ""type"": ""string"",
      ""enum"": [""selfTimeSumMs"", ""totalTimeSumMs"", ""maxSelfTimeMs"", ""gcAllocSumBytes""],
      ""default"": ""selfTimeSumMs"",
      ""description"": ""热点全局降序排序指标；并列时按 path/category 稳定排序。""
    },
    ""maxDepth"": {
      ""type"": ""integer"",
      ""minimum"": 0,
      ""maximum"": 128,
      ""default"": 64,
      ""description"": ""相对线程根节点扫描的最大层级；0 只读取根节点的直接子项。""
    },
    ""includeEditorOnly"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""是否保留 EditorOnly 样本；默认隐藏以减少 Editor 自身开销干扰。""
    },
    ""limit"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 100,
      ""default"": 50,
      ""description"": ""单线程返回热点上限默认 50；显式 threadSelector 时缺省 25，且 maxThreads × limit 不得超过 100。""
    },
    ""hotspotDetails"": {
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""description"": ""按需为排序后的前 N 个热点计算逐帧分布；缺省时不保留逐帧序列。多线程查询的总趋势点上限为 600。"",
      ""properties"": {
        ""metric"": {
          ""type"": ""string"",
          ""enum"": [""selfTimeMs"", ""totalTimeMs"", ""gcAllocBytes"", ""calls""],
          ""default"": ""selfTimeMs""
        },
        ""slowestLimit"": {
          ""type"": ""integer"",
          ""minimum"": 0,
          ""maximum"": 5,
          ""default"": 3
        },
        ""trendFrameCount"": {
          ""type"": ""integer"",
          ""minimum"": 0,
          ""maximum"": 120,
          ""default"": 30,
          ""description"": ""返回最近 N 个实际帧的趋势；线程或 marker 缺席时该帧值为 0。""
        },
        ""hotspotLimit"": {
          ""type"": ""integer"",
          ""minimum"": 1,
          ""maximum"": 10,
          ""default"": 5
        }
      }
    }
  }
}");
    }
}
