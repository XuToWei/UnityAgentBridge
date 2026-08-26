namespace AgentBridge
{
    internal static class ScriptErrorCodes
    {
        public const string EditorBusy = "SCRIPT_EDITOR_BUSY";
        public const string SafetyBlocked = "SCRIPT_SAFETY_BLOCKED";
        public const string CompilerUnavailable = "SCRIPT_COMPILER_UNAVAILABLE";
        public const string CompilerTimeout = "SCRIPT_COMPILER_TIMEOUT";
        public const string CompilationFailed = "SCRIPT_COMPILATION_FAILED";
        public const string OutputTooLarge = "SCRIPT_OUTPUT_TOO_LARGE";
        public const string AssemblyLimitReached = "SCRIPT_ASSEMBLY_LIMIT_REACHED";
        public const string AssemblyLoadFailed = "SCRIPT_ASSEMBLY_LOAD_FAILED";
        public const string ContractNotFound = "SCRIPT_CONTRACT_NOT_FOUND";
        public const string ContractAmbiguous = "SCRIPT_CONTRACT_AMBIGUOUS";
        public const string ContractInvalid = "SCRIPT_CONTRACT_INVALID";
        public const string InstantiationFailed = "SCRIPT_INSTANTIATION_FAILED";
        public const string RuntimeException = "SCRIPT_RUNTIME_EXCEPTION";
        public const string ResultUnsupported = "SCRIPT_RESULT_UNSUPPORTED";
        public const string TrackingUnsupportedObject = "SCRIPT_TRACKING_UNSUPPORTED_OBJECT";
        public const string InternalThreadError = "SCRIPT_INTERNAL_THREAD_ERROR";
    }
}