using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 命令处理器接口。实现类只要带公共无参构造并被 Unity 编译,就会被自动注册。
    /// </summary>
    public interface ICommandHandler
    {
        /// <summary>命令名,需全局唯一。</summary>
        string Command { get; }

        /// <summary>命令描述,供 list_commands 展示给 AI。无描述返回 ""。</summary>
        string Description { get; }

        /// <summary>命令所属分组(供管理器界面按组显示),如 Meta/Inspection/Capture/Mutation/Assets/Compilation。</summary>
        string Group { get; }

        /// <summary>是否允许被命令管理器禁用。协议刚需命令(ping/list_commands)返回 false,不可禁用。</summary>
        bool CanDisable { get; }

        /// <summary>
        /// 在 Unity 主线程执行。返回值序列化进 response.result。
        /// 抛 CommandException 用其 code;抛其他异常归 HANDLER_EXCEPTION。
        /// </summary>
        object Execute(JObject @params);

        /// <summary>
        /// params 的 JSON Schema,供 list_commands 返回给 AI。
        /// 无参命令返回空 schema `{}`(new JObject());不要返回 null。
        /// </summary>
        JObject GetParamsSchema();
    }
}
