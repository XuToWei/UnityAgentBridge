using UnityEngine;

namespace AgentBridge.Tests
{
    /// <summary>测试用 ScriptableObject 类型,供 create_asset(kind=scriptableObject)按类型名解析测试。</summary>
    public sealed class TestSettings : ScriptableObject
    {
        public int value;
    }
}
