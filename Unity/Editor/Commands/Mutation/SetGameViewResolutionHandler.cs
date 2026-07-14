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
        private const int MaxSize = 8192;
        private const long MaxPixels = 33554432; // 8K-class upper bound; RGBA32 is ~128 MiB.
        private const int MaxLabelLength = 128;

        public string Command => "set_game_view_resolution";

        public string Description =>
            "设置 Game View 固定分辨率并返回 restore token;传 restore 可恢复选择并删除本次临时预设";

        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public object Execute(JObject @params)
        {
            if (@params?["restore"] is JObject restore)
            {
                return Restore(restore);
            }

            var width = GetRequiredInt(@params, "width");
            var height = GetRequiredInt(@params, "height");
            var textureLimit = UnityEngine.SystemInfo.maxTextureSize > 0
                ? UnityEngine.SystemInfo.maxTextureSize
                : MaxSize;
            var platformMax = System.Math.Min(MaxSize, textureLimit);
            if (width > platformMax || height > platformMax)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    $"width/height 不能超过当前编辑器纹理上限 {platformMax}");
            }
            if ((long)width * height > MaxPixels)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    $"分辨率总像素不能超过 {MaxPixels},实际为 {(long)width * height}");
            }
            var label = ResolveLabel(@params, width, height);

            var result = GameViewResolutionUtility.SetFixedResolution(width, height, label);
            return new
            {
                width = result.Width,
                height = result.Height,
                label = result.Label,
                selectedIndex = result.SelectedIndex,
                created = result.Created,
                group = result.Group,
                restore = new
                {
                    selectedIndex = result.Restore.SelectedIndex,
                    removeCreated = result.Restore.RemoveCreated,
                    width = result.Restore.Width,
                    height = result.Restore.Height,
                    label = result.Restore.Label,
                    group = result.Restore.Group
                }
            };
        }

        private static object Restore(JObject restore)
        {
            var token = new GameViewResolutionUtility.RestoreToken(
                GetRequiredNonNegativeInt(restore, "selectedIndex"),
                GetRequiredBool(restore, "removeCreated"),
                GetRequiredInt(restore, "width"),
                GetRequiredInt(restore, "height"),
                GetRequiredString(restore, "label"),
                GetRequiredString(restore, "group"));
            var result = GameViewResolutionUtility.RestoreFixedResolution(token);
            return new
            {
                restored = true,
                selectedIndex = result.SelectedIndex,
                width = result.Width,
                height = result.Height,
                label = result.Label,
                removedCreated = result.RemovedCreated,
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

        private static bool GetRequiredBool(JObject @params, string name)
        {
            var token = @params?[name];
            if (token == null || token.Type != JTokenType.Boolean)
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"{name} 必须是 boolean。");
            }
            return token.Value<bool>();
        }

        private static int GetRequiredNonNegativeInt(JObject @params, string name)
        {
            var token = @params?[name];
            if (token == null || token.Type != JTokenType.Integer)
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"{name} 必须是 non-negative integer。");
            }
            try
            {
                var value = token.Value<long>();
                if (value < 0 || value > int.MaxValue)
                {
                    throw new System.OverflowException();
                }
                return (int)value;
            }
            catch (System.Exception)
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"{name} 必须是 non-negative integer。");
            }
        }

        private static string GetRequiredString(JObject @params, string name)
        {
            var token = @params?[name];
            if (token == null || token.Type != JTokenType.String ||
                string.IsNullOrEmpty(token.Value<string>()))
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"{name} 必须是非空 string。");
            }
            return token.Value<string>();
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""width"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192, ""description"": ""Game View 固定分辨率宽度。"" },
    ""height"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192, ""description"": ""Game View 固定分辨率高度;总像素还必须 <= 33554432。"" },
    ""label"": { ""type"": ""string"", ""description"": ""可选自定义尺寸名称;缺省为 AgentBridge {width}x{height}。"" },
    ""restore"": {
      ""type"": ""object"",
      ""description"": ""原样回传上一次 set_game_view_resolution 返回的 restore token。"",
      ""properties"": {
        ""selectedIndex"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 8192 },
        ""removeCreated"": { ""type"": ""boolean"" },
        ""width"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192 },
        ""height"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192 },
        ""label"": { ""type"": ""string"", ""minLength"": 1 },
        ""group"": { ""type"": ""string"", ""minLength"": 1 }
      },
      ""required"": [""selectedIndex"", ""removeCreated"", ""width"", ""height"", ""label"", ""group""],
      ""additionalProperties"": false
    }
  },
  ""oneOf"": [
    { ""required"": [""width"", ""height""] },
    { ""required"": [""restore""] }
  ],
  ""additionalProperties"": false
}");
    }
}
