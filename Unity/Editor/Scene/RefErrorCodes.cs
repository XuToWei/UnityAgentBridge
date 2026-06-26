namespace AgentBridge
{
    /// <summary>对象/组件引用解析的 handler 自有错误码(file-bridge 4.1 允许 handler 追加自有码)。</summary>
    public static class RefErrorCodes
    {
        public const string InvalidObjectRef = "INVALID_OBJECT_REF";   // path 与 instanceId 都缺
        public const string ObjectNotFound = "OBJECT_NOT_FOUND";       // 解析不到 GameObject
        public const string ComponentNotFound = "COMPONENT_NOT_FOUND"; // 组件类型/序号不存在
    }
}
