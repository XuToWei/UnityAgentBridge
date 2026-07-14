namespace AgentBridge
{
    /// <summary>Unity Test Framework 命令的稳定错误码。</summary>
    public static class TestErrorCodes
    {
        public const string TestRunActive = "TEST_RUN_ACTIVE";
        public const string TestRunRequiresEditMode = "TEST_RUN_REQUIRES_EDIT_MODE";
        public const string TestRunEditorBusy = "TEST_RUN_EDITOR_BUSY";
        public const string TestFrameworkUnavailable = "TEST_FRAMEWORK_UNAVAILABLE";
        public const string TestRunStartFailed = "TEST_RUN_START_FAILED";
        public const string TestResultNotFound = "TEST_RESULT_NOT_FOUND";
        public const string TestResultIoError = "TEST_RESULT_IO_ERROR";
        public const string TestRunStateCorrupt = "TEST_RUN_STATE_CORRUPT";
    }
}
