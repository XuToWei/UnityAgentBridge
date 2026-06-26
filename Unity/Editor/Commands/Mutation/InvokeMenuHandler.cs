using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// invoke_menu(写,逃生舱):执行一个编辑器菜单项。params.path 必填(如 "GameObject/Align With View")。
    /// 经 EditorApplication.ExecuteMenuItem;返回 false(项不存在 / 被禁用 / 执行失败)→ MENU_NOT_FOUND。
    /// 副作用(含可能的保存/资源改动)由被调菜单项决定,不受桥接 dirty/save 纪律约束。
    /// </summary>
    public sealed class InvokeMenuHandler : ICommandHandler
    {
        public string Command => "invoke_menu";
        public string Description => "执行编辑器菜单项(params.path),逃生舱;失败 → MENU_NOT_FOUND";

        public object Execute(JObject @params)
        {
            var path = @params?["path"]?.Value<string>();
            if (string.IsNullOrEmpty(path))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "缺 path");
            }

            if (!EditorApplication.ExecuteMenuItem(path))
            {
                throw new CommandException(MutationErrorCodes.MenuNotFound,
                    $"菜单项 '{path}' 不存在、被禁用或执行失败");
            }

            return new { executed = true };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(
                @"{""type"":""object"",""properties"":{""path"":{""type"":""string""}},""required"":[""path""]}");
        }
    }
}
