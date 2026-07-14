namespace AgentBridge
{
    /// <summary>对象/组件引用解析相关错误码。</summary>
    public static class RefErrorCodes
    {
        public const string InvalidObjectRef = "INVALID_OBJECT_REF";   // path 与 instanceId 都缺
        public const string ObjectNotFound = "OBJECT_NOT_FOUND";       // 解析不到 GameObject
        public const string ObjectRefAmbiguous = "OBJECT_REF_AMBIGUOUS"; // path 命中多个场景对象
        public const string ObjectRefStale = "OBJECT_REF_STALE"; // instanceId 与 path/scenePath 提示不一致
        public const string PersistentObjectNotAllowed = "PERSISTENT_OBJECT_NOT_ALLOWED"; // Project 资产不是场景对象
        public const string ComponentNotFound = "COMPONENT_NOT_FOUND"; // 组件类型/序号不存在
        public const string ComponentTypeAmbiguous = "COMPONENT_TYPE_AMBIGUOUS"; // 短类型名命中多个组件类型
    }
}
