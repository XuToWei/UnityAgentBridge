using System;

namespace AgentBridge
{
    /// <summary>
    /// 显式允许外部 Agent 调用一个无参静态方法。
    /// 方法 ID 由声明类型 FullName 与方法名确定，特性仅保存供发现命令展示的说明。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class AgentCallableAttribute : Attribute
    {
        public AgentCallableAttribute(string description)
        {
            Description = description;
        }

        public string Description { get; }
    }
}
