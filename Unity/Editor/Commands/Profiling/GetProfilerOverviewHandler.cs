using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// get_profiler_overview(只读):快速读取 Profiler Window 当前 capture 的帧时间概览，
    /// 不扫描 CPU hierarchy。
    /// </summary>
    public sealed class GetProfilerOverviewHandler : ICommandHandler
    {
        public string Command => "get_profiler_overview";

        public string Description =>
            "读取当前 Profiler capture 的帧时间概览：P50/P95/P99、最慢帧和基于 P95+MAD 的自动 spike 帧；不扫描 hierarchy";

        public string Group => "Profiling";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var options = new ProfilerOverviewOptions
            {
                EndFrameIndex = @params?["endFrameIndex"]?.ToObject<int?>(),
                FrameCount = @params?["frameCount"]?.ToObject<int?>() ??
                             ProfilerOverviewOptions.DefaultFrameCount,
                SlowestLimit = @params?["slowestLimit"]?.ToObject<int?>() ??
                               ProfilerOverviewOptions.DefaultSlowestLimit,
                SpikeLimit = @params?["spikeLimit"]?.ToObject<int?>() ??
                             ProfilerOverviewOptions.DefaultSpikeLimit,
                IncludeFrames = @params?["includeFrames"]?.ToObject<bool?>() ?? false
            };

            var captureId = @params?["captureId"]?.Value<string>();
            var result = ProfilerSavedCaptureAccess.Read(
                captureId,
                () => ProfilerOverviewReader.Query(options),
                false,
                out var immutable);
            if (result.Capture != null)
            {
                result.Capture.Source =
                    immutable ? "saved" : "current";
                result.Capture.Immutable = immutable;
            }
            return Task.FromResult<object>(result);
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""captureId"": {
      ""type"": ""string"",
      ""pattern"": ""^[0-9a-fA-F]{32}$"",
      ""description"": ""可选 capture_profiler ID；缺省读取当前缓冲。""
    },
    ""endFrameIndex"": {
      ""type"": ""integer"",
      ""minimum"": 0,
      ""maximum"": 2147483647,
      ""description"": ""概览窗口的结束帧。缺省时在命令开始时固定为 Profiler 最新帧。""
    },
    ""frameCount"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 2000,
      ""default"": 120,
      ""description"": ""从结束帧向前选择的实际 Profiler 帧数；通过帧导航读取，不假定索引连续。""
    },
    ""slowestLimit"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 50,
      ""default"": 10,
      ""description"": ""返回的最慢帧数量上限；耗时相同时较新的 frameIndex 优先。""
    },
    ""spikeLimit"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 50,
      ""default"": 10,
      ""description"": ""返回的自动识别 spike 帧数量上限。""
    },
    ""includeFrames"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""是否同时返回窗口内按时间顺序排列的全部帧摘要；默认仅返回聚合、最慢帧和 spike。""
    }
  }
}");
    }
}
