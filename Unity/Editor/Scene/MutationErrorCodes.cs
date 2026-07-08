namespace AgentBridge
{
    /// <summary>场景写操作相关错误码。</summary>
    public static class MutationErrorCodes
    {
        public const string PropertyNotFound = "PROPERTY_NOT_FOUND";       // propertyPath 在组件上不存在
        public const string PropertyTypeMismatch = "PROPERTY_TYPE_MISMATCH"; // value 与属性类型不符
        public const string MenuNotFound = "MENU_NOT_FOUND";              // 菜单项不存在或执行失败/被禁用
        public const string CreateFailed = "CREATE_FAILED";              // prefab 路径无效 / 图元类型未知 / 创建失败
    }
}
