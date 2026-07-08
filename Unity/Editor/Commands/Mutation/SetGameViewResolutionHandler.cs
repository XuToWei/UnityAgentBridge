using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 设置 Game View 固定分辨率,必要时添加自定义尺寸。
    /// 这会改变编辑器 Game View 的尺寸下拉选项,不修改场景或 PlayerSettings。
    /// </summary>
    public sealed class SetGameViewResolutionHandler : ICommandHandler
    {
        private const int MinSize = 1;
        private const int MaxSize = 16384;
        private const int MaxLabelLength = 128;

        public string Command => "set_game_view_resolution";

        public string Description =>
            "设置 Game View 固定分辨率(width/height),必要时添加自定义尺寸;返回 width/height/label/selectedIndex/created";

        public string Group => "Mutation";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var width = GetRequiredInt(@params, "width");
            var height = GetRequiredInt(@params, "height");
            var label = ResolveLabel(@params, width, height);

            var result = GameViewResolutionUtility.SetFixedResolution(width, height, label);
            return new
            {
                width = result.Width,
                height = result.Height,
                label = result.Label,
                selectedIndex = result.SelectedIndex,
                created = result.Created,
                group = result.Group
            };
        }

        private static int GetRequiredInt(JObject @params, string name)
        {
            var token = @params?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"缺 {name}");
            }
            if (token.Type != JTokenType.Integer)
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"{name} 必须是 integer。");
            }

            long value;
            try
            {
                value = token.Value<long>();
            }
            catch
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"{name} 必须是 integer。");
            }
            if (value < MinSize || value > MaxSize)
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"{name} 必须在 {MinSize}..{MaxSize} 之间。");
            }
            return (int)value;
        }

        private static string ResolveLabel(JObject @params, int width, int height)
        {
            var token = @params?["label"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return $"AgentBridge {width}x{height}";
            }
            if (token.Type != JTokenType.String)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "label 必须是 string。");
            }

            var label = token.Value<string>()?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "label 不能为空白。未指定时会自动生成名称。");
            }
            if (label.Length > MaxLabelLength)
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"label 不能超过 {MaxLabelLength} 个字符。");
            }
            return label;
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""width"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 16384, ""description"": ""Game View 固定分辨率宽度。"" },
    ""height"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 16384, ""description"": ""Game View 固定分辨率高度。"" },
    ""label"": { ""type"": ""string"", ""description"": ""可选自定义尺寸名称;缺省为 AgentBridge {width}x{height}。"" }
  },
  ""required"": [""width"", ""height""]
}");
        }
    }
}
