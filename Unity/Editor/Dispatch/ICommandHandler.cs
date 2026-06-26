using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 命令处理器接口(M3,扩展核心)。对应 file-bridge roadmap 4.3。
    /// 写一个实现(含公共无参构造),放进任意被编译的程序集即自动注册,无需改框架、无需特性。
    /// </summary>
    public interface ICommandHandler
    {
        /// <summary>命令名,需全局唯一。</summary>
        string Command { get; }

        /// <summary>命令描述,供 list_commands 展示给 AI(4.7 自描述)。无描述返回 ""。</summary>
        string Description { get; }

        /// <summary>
        /// 在 Unity 主线程执行。返回值序列化进 response.result。
        /// 抛 CommandException 用其 code;抛其他异常归 HANDLER_EXCEPTION。
        /// </summary>
        object Execute(JObject @params);

        /// <summary>
        /// params 的 JSON Schema,供 list_commands 返回给 AI(4.7 自描述)。
        /// 无参命令返回空 schema `{}`(new JObject());不要返回 null。
        /// </summary>
        JObject GetParamsSchema();
    }
}
