[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [ValidateSet("Baseline", "Mutating", "Full")]
    [string]$Suite = "Baseline",

    [int]$TimeoutSeconds = 30,

    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$bridgeRoot = Join-Path $project ".agentbridge"
$requestTempPath = Join-Path $bridgeRoot "request.json.tmp"
$requestPath = Join-Path $bridgeRoot "request.json"
$processingPath = Join-Path $bridgeRoot "processing.json"
$responsePath = Join-Path $bridgeRoot "response.json"

foreach ($requiredPath in @(
    (Join-Path $project "Assets"),
    $bridgeRoot
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "AgentBridge project structure is incomplete: $requiredPath"
    }
}

$script:Results = [System.Collections.Generic.List[object]]::new()
$script:ExercisedCommands = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$script:CommandsVersion = $null
$script:CommandMap = @{}
$script:SampleObjectRef = $null
$script:OriginalSelectionRefs = @()

function New-RequestId {
    param([string]$Prefix)

    $safePrefix = ($Prefix -replace '[^A-Za-z0-9_-]', '-')
    if ($safePrefix.Length -gt 36) {
        $safePrefix = $safePrefix.Substring(0, 36)
    }
    return "$safePrefix-$([Guid]::NewGuid().ToString('N').Substring(0, 16))"
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param(
        $Actual,
        $Expected,
        [string]$Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message (expected=$Expected actual=$Actual)"
    }
}

function Assert-Near {
    param(
        [double]$Actual,
        [double]$Expected,
        [double]$Tolerance,
        [string]$Message
    )

    if ([math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw "$Message (expected=$Expected actual=$Actual tolerance=$Tolerance)"
    }
}

function Wait-ResponseFile {
    param(
        [string]$Path,
        [string]$RequestId
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "timeout after ${TimeoutSeconds}s waiting for response id=$RequestId"
}

function Wait-ProcessingReleased {
    param(
        [string]$Path,
        [string]$RequestId
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Test-Path -LiteralPath $Path)) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "timeout after ${TimeoutSeconds}s waiting for processing cleanup id=$RequestId"
}

function Read-ResponseText {
    param([string]$Path)

    # Atomic rename guarantees complete content, but antivirus/indexers can briefly retain a
    # Windows share lock after the file becomes visible. Retry the open, never the command.
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    do {
        try {
            return [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 25
        }
        catch [UnauthorizedAccessException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 25
        }
    } while ($true)
}

function Assert-ResponseEnvelope {
    param(
        [object]$Response,
        [string]$RequestId
    )

    Assert-Equal $Response.v 1 "response.v must be 1"
    Assert-Equal $Response.id $RequestId "response.id must match request id"
    Assert-True ($Response.status -eq "ok" -or $Response.status -eq "error") "response.status must be ok or error"
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$Response.commandsVersion)) "commandsVersion must be non-empty"
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$Response.timestamp)) "timestamp must be non-empty"

    if ($Response.status -eq "ok") {
        Assert-True ($null -eq $Response.error) "ok response must have error=null"
    }
    else {
        Assert-True ($null -eq $Response.result) "error response must have result=null"
        Assert-True ($null -ne $Response.error) "error response must contain error"
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$Response.error.code)) "error.code must be non-empty"
    }
}

function Invoke-RawBridgeRequest {
    param(
        [AllowEmptyString()]
        [string]$ExpectedResponseId,
        [string]$RawJson
    )

    foreach ($activePath in @($requestPath, $processingPath, $responsePath)) {
        if (Test-Path -LiteralPath $activePath) {
            throw "bridge is not idle; active fixed slot: $activePath"
        }
    }

    $started = [Diagnostics.Stopwatch]::StartNew()

    [IO.File]::WriteAllText($requestTempPath, $RawJson, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($requestTempPath, $requestPath)
    Wait-ResponseFile $responsePath $ExpectedResponseId

    $responseAccepted = $false
    $processingReleased = $false
    try {
        $rawResponse = Read-ResponseText $responsePath
        $response = $rawResponse | ConvertFrom-Json
        Assert-ResponseEnvelope $response $ExpectedResponseId
        $responseAccepted = $true
        Wait-ProcessingReleased $processingPath $ExpectedResponseId
        $processingReleased = $true
        $started.Stop()

        return [pscustomobject]@{
            Response = $response
            ElapsedMs = [math]::Round($started.Elapsed.TotalMilliseconds, 1)
        }
    }
    finally {
        if ($responseAccepted -and $processingReleased -and (Test-Path -LiteralPath $responsePath)) {
            Remove-Item -LiteralPath $responsePath
        }
    }
}

function Invoke-BridgeRequest {
    param(
        [string]$Command,
        $Params = @{},
        [string]$IdPrefix = "case"
    )

    $null = $script:ExercisedCommands.Add($Command)
    $requestId = New-RequestId $IdPrefix
    $request = [ordered]@{
        v = 1
        id = $requestId
        command = $Command
        params = $Params
    }
    $json = $request | ConvertTo-Json -Depth 100 -Compress
    $exchange = Invoke-RawBridgeRequest $requestId $json

    if ($null -ne $script:CommandsVersion -and
        $exchange.Response.commandsVersion -ne $script:CommandsVersion) {
        throw "commandsVersion changed unexpectedly: cached=$script:CommandsVersion actual=$($exchange.Response.commandsVersion)"
    }
    return $exchange
}

function Assert-Ok {
    param([object]$Exchange)
    if ($Exchange.Response.status -ne "ok") {
        throw "expected ok response; actual error=$($Exchange.Response.error.code): $($Exchange.Response.error.message)"
    }
}

function Assert-Error {
    param(
        [object]$Exchange,
        [string]$Code
    )

    if ($Exchange.Response.status -ne "error") {
        $actualResult = $Exchange.Response.result | ConvertTo-Json -Depth 20 -Compress
        throw "expected error response; actual result=$actualResult"
    }
    if (-not [string]::IsNullOrEmpty($Code)) {
        if ($Exchange.Response.error.code -ne $Code) {
            throw "unexpected error code; expected=$Code actual=$($Exchange.Response.error.code) message=$($Exchange.Response.error.message)"
        }
    }
}

function Wait-PlayModeState {
    param(
        [bool]$IsPlaying,
        [int]$WaitSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        $exchange = Invoke-BridgeRequest "ping" @{} "wait-play-state"
        Assert-Ok $exchange
        # Unity keeps isPlayingOrWillChangePlaymode=true for the whole Play session,
        # not just during the transition. Entered = isPlaying; fully stopped = both false.
        if ([bool]$exchange.Response.result.isPlaying -eq $IsPlaying -and
            ($IsPlaying -or -not [bool]$exchange.Response.result.isPlayingOrWillChangePlaymode)) {
            return $exchange
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Unity did not reach isPlaying=$IsPlaying within ${WaitSeconds}s"
}

function Wait-CompilationIdle {
    param([int]$WaitSeconds = 90)

    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        $exchange = Invoke-BridgeRequest "get_compile_result" @{} "wait-compile"
        if ($exchange.Response.status -eq "error" -and $exchange.Response.error.code -eq "INTERRUPTED") {
            Start-Sleep -Milliseconds 250
            continue
        }
        Assert-Ok $exchange
        if (-not [bool]$exchange.Response.result.compiling) {
            $ping = Invoke-BridgeRequest "ping" @{} "wait-compile-ping"
            Assert-Ok $ping
            if (-not [bool]$ping.Response.result.isCompiling) {
                return $exchange
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Unity compilation did not become idle within ${WaitSeconds}s"
}

function Wait-TestRunResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RunId,
        [int]$WaitSeconds = 120
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        $exchange = Invoke-BridgeRequest "get_test_result" @{
            runId = $RunId
            includePassed = $true
            limit = 100
        } "wait-test-result"
        if ($exchange.Response.status -eq "error" -and
            $exchange.Response.error.code -eq "INTERRUPTED") {
            Start-Sleep -Milliseconds 250
            continue
        }
        Assert-Ok $exchange
        $status = [string]$exchange.Response.result.status
        if ($status -eq "completed" -or $status -eq "interrupted") {
            return $exchange
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Unity test run $RunId did not finish within ${WaitSeconds}s"
}

function Invoke-TestRunWhenIdle {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Params,
        [Parameter(Mandatory = $true)]
        [string]$IdPrefix,
        [int]$WaitSeconds = 10
    )

    # RunFinished can publish a completed result a few frames before Unity Test Framework
    # removes the finished job from its global runner registry. Treat that short window as
    # framework cleanup, while preserving TEST_RUN_ACTIVE as an error after the deadline.
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    $attempt = 0
    do {
        $attempt++
        $exchange = Invoke-BridgeRequest "run_tests" $Params "$IdPrefix-$attempt"
        if ($exchange.Response.status -ne "error" -or
            $exchange.Response.error.code -ne "TEST_RUN_ACTIVE") {
            return $exchange
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            return $exchange
        }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

function Invoke-TestCase {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    $startedAt = [DateTime]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $passed = $false
    $errorMessage = $null
    $status = $null
    $errorCode = $null
    $command = $null

    try {
        $exchange = & $Action
        if ($null -ne $exchange -and $null -ne $exchange.Response) {
            $status = $exchange.Response.status
            if ($null -ne $exchange.Response.error) {
                $errorCode = $exchange.Response.error.code
            }
        }
        $passed = $true
    }
    catch {
        $errorMessage = $_.Exception.Message
    }
    finally {
        $stopwatch.Stop()
    }

    $script:Results.Add([pscustomobject]@{
        caseId = $Name
        suite = $Suite
        passed = $passed
        status = $status
        errorCode = $errorCode
        error = $errorMessage
        elapsedMs = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
        startedAt = $startedAt.ToString("o")
    })

    $marker = if ($passed) { "PASS" } else { "FAIL" }
    $detail = if ($passed) { "" } else { " :: $errorMessage" }
    Write-Host "[$marker] $Name$detail"
}

function Invoke-BridgeErrorCases {
    param([object[]]$Cases)

    foreach ($definition in @($Cases)) {
        $case = $definition
        Invoke-TestCase ([string]$case.CaseId) {
            $params = if ($null -eq $case.Params) { @{} } else { $case.Params }
            $exchange = Invoke-BridgeRequest ([string]$case.Command) $params ([string]$case.IdPrefix)
            Assert-Error $exchange ([string]$case.ExpectedCode)
            return $exchange
        }
    }
}

function Find-FirstHierarchyObjectRef {
    param([object]$HierarchyResult)

    foreach ($scene in @($HierarchyResult.scenes)) {
        foreach ($root in @($scene.roots)) {
            if ($null -ne $root) {
                return [ordered]@{
                    path = [string]$root.path
                    instanceId = [int]$root.instanceId
                }
            }
        }
    }
    return $null
}

function ConvertTo-StableObjectRef {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }
    $reference = [ordered]@{}
    if (-not [string]::IsNullOrEmpty([string]$Value.path)) {
        $reference.path = [string]$Value.path
    }
    if ($null -ne $Value.instanceId) {
        $reference.instanceId = [int]$Value.instanceId
    }
    if ($null -ne $Value.scenePath) {
        $reference.scenePath = [string]$Value.scenePath
    }
    return $reference
}

# Discovery is deliberately the first exchange in every run.
Invoke-TestCase "meta.list_commands" {
    $exchange = Invoke-BridgeRequest "list_commands" @{} "list"
    Assert-Ok $exchange
    $commands = @($exchange.Response.result.commands)
    Assert-True ($commands.Count -gt 0) "list_commands returned no commands"

    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($info in $commands) {
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$info.command)) "command name must be non-empty"
        Assert-True ($seen.Add([string]$info.command)) "duplicate command in list_commands: $($info.command)"
        Assert-True ($null -ne $info.paramsSchema) "paramsSchema must not be null: $($info.command)"
        Assert-True ($null -ne $info.PSObject.Properties['batchAllowed']) "batchAllowed missing: $($info.command)"
        Assert-True ($null -ne $info.PSObject.Properties['supportsUndoCollapse']) "supportsUndoCollapse missing: $($info.command)"
        $script:CommandMap[[string]$info.command] = $info
    }

    $script:CommandsVersion = [string]$exchange.Response.commandsVersion
    Assert-Equal $exchange.Response.result.commandsVersion $script:CommandsVersion "nested commandsVersion mismatch"
    return $exchange
}

Invoke-TestCase "meta.ping" {
    $exchange = Invoke-BridgeRequest "ping" @{} "ping"
    Assert-Ok $exchange
    Assert-Equal $exchange.Response.result.message "pong" "ping message mismatch"
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$exchange.Response.result.unityVersion)) "ping unityVersion missing"
    Assert-True ($null -ne $exchange.Response.result.isPlaying) "ping isPlaying missing"
    Assert-True ($null -ne $exchange.Response.result.isPlayingOrWillChangePlaymode) "ping transition state missing"
    Assert-True ($null -ne $exchange.Response.result.isCompiling) "ping isCompiling missing"
    Assert-True ($null -ne $exchange.Response.result.isUpdating) "ping isUpdating missing"
    return $exchange
}

Invoke-TestCase "protocol.invalid_json" {
    $exchange = Invoke-RawBridgeRequest "" "{"
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "protocol.id_empty" {
    $json = '{"v":1,"id":"","command":"ping","params":{}}'
    $exchange = Invoke-RawBridgeRequest "" $json
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "protocol.id_too_long" {
    $id = "x" * 65
    $json = "{`"v`":1,`"id`":`"$id`",`"command`":`"ping`",`"params`":{}}"
    $exchange = Invoke-RawBridgeRequest "" $json
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "protocol.v_wrong_type" {
    $id = New-RequestId "bad-v-type"
    $json = "{`"v`":`"1`",`"id`":`"$id`",`"command`":`"ping`",`"params`":{}}"
    $exchange = Invoke-RawBridgeRequest $id $json
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "protocol.v_integer_overflow" {
    $id = New-RequestId "bad-v-overflow"
    $json = "{`"v`":999999999999999999999999999999999999999999999999999999,`"id`":`"$id`",`"command`":`"ping`",`"params`":{}}"
    $exchange = Invoke-RawBridgeRequest $id $json
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "protocol.id_wrong_type" {
    $json = '{"v":1,"id":123,"command":"ping","params":{}}'
    $exchange = Invoke-RawBridgeRequest "" $json
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "protocol.command_wrong_type" {
    $id = New-RequestId "bad-command-type"
    $json = "{`"v`":1,`"id`":`"$id`",`"command`":456,`"params`":{}}"
    $exchange = Invoke-RawBridgeRequest $id $json
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "protocol.duplicate_field" {
    $id = New-RequestId "duplicate-field"
    $json = "{`"v`":1,`"v`":2,`"id`":`"$id`",`"command`":`"ping`",`"params`":{}}"
    $exchange = Invoke-RawBridgeRequest "" $json
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "protocol.request_too_large" {
    $id = New-RequestId "too-large"
    $padding = "x" * (1024 * 1024)
    $json = "{`"v`":1,`"id`":`"$id`",`"command`":`"ping`",`"params`":{},`"padding`":`"$padding`"}"
    $exchange = Invoke-RawBridgeRequest "" $json
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "protocol.params_wrong_shape" {
    $id = New-RequestId "bad-params"
    $json = "{`"v`":1,`"id`":`"$id`",`"command`":`"ping`",`"params`":[]}"
    $exchange = Invoke-RawBridgeRequest $id $json
    Assert-Error $exchange "INVALID_REQUEST"
    return $exchange
}

Invoke-TestCase "meta.unknown_command" {
    $exchange = Invoke-BridgeRequest "__agentbridge_missing_command__" @{} "unknown"
    Assert-Error $exchange "UNKNOWN_COMMAND"
    return $exchange
}

Invoke-TestCase "inspection.get_hierarchy.default" {
    $exchange = Invoke-BridgeRequest "get_hierarchy" @{} "hierarchy"
    Assert-Ok $exchange
    Assert-True ($null -ne $exchange.Response.result.scenes) "hierarchy scenes missing"
    $script:SampleObjectRef = Find-FirstHierarchyObjectRef $exchange.Response.result
    return $exchange
}

Invoke-TestCase "inspection.get_hierarchy.depth_zero" {
    $exchange = Invoke-BridgeRequest "get_hierarchy" @{ maxDepth = 0 } "hierarchy-depth"
    Assert-Ok $exchange
    return $exchange
}

Invoke-BridgeErrorCases @(
    @{ CaseId = "inspection.get_hierarchy.invalid_depth"; Command = "get_hierarchy"; Params = @{ maxDepth = -2 }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "hierarchy-bad" }
    @{ CaseId = "inspection.get_hierarchy.invalid_limit"; Command = "get_hierarchy"; Params = @{ limit = 50001 }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "hierarchy-limit-bad" }
    @{ CaseId = "inspection.get_hierarchy.depth_overflow"; Command = "get_hierarchy"; Params = @{ maxDepth = [decimal]2147483648 }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "hierarchy-depth-overflow" }
    @{ CaseId = "inspection.get_hierarchy.depth_wrong_type"; Command = "get_hierarchy"; Params = @{ maxDepth = "0" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "hierarchy-type" }
    @{ CaseId = "inspection.get_hierarchy.missing_root"; Command = "get_hierarchy"; Params = @{ root = @{ instanceId = 2147483647 } }; ExpectedCode = "OBJECT_NOT_FOUND"; IdPrefix = "hierarchy-root" }
)

Invoke-TestCase "inspection.get_selection" {
    $exchange = Invoke-BridgeRequest "get_selection" @{} "selection"
    Assert-Ok $exchange
    Assert-True ($null -ne $exchange.Response.result.selection) "selection result missing"
    $script:OriginalSelectionRefs = @($exchange.Response.result.selection | ForEach-Object {
        ConvertTo-StableObjectRef $_
    })
    return $exchange
}

Invoke-TestCase "scenes.list_scenes" {
    $exchange = Invoke-BridgeRequest "list_scenes" @{} "list-scenes"
    Assert-Ok $exchange
    Assert-Equal ([int]$exchange.Response.result.count) @($exchange.Response.result.scenes).Count "scene count mismatch"
    Assert-True (@($exchange.Response.result.scenes | Where-Object active).Count -le 1) "more than one active scene returned"
    foreach ($scene in @($exchange.Response.result.scenes)) {
        Assert-True ($null -ne $scene.handle) "scene handle missing"
        Assert-True ($null -ne $scene.loaded) "scene loaded flag missing"
        Assert-True ($null -ne $scene.dirty) "scene dirty flag missing"
    }
    return $exchange
}

Invoke-TestCase "inspection.get_object.sample" {
    if ($null -eq $script:SampleObjectRef) {
        throw "no scene root was available for get_object"
    }
    $exchange = Invoke-BridgeRequest "get_object" @{ object = $script:SampleObjectRef } "object"
    Assert-Ok $exchange
    Assert-True (@($exchange.Response.result.components).Count -gt 0) "sample object has no serialized components"
    return $exchange
}

Invoke-BridgeErrorCases @(
    @{ CaseId = "inspection.get_object.missing"; Command = "get_object"; Params = @{ object = @{ instanceId = 2147483647 } }; ExpectedCode = "OBJECT_NOT_FOUND"; IdPrefix = "object-missing" }
    @{ CaseId = "inspection.get_object.instance_id_overflow"; Command = "get_object"; Params = @{ object = @{ instanceId = [decimal]2147483648 } }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "object-id-overflow" }
    @{ CaseId = "inspection.get_object.empty_ref"; Command = "get_object"; Params = @{ object = @{} }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "object-empty-ref" }
)

Invoke-TestCase "inspection.list_assets.limit" {
    $exchange = Invoke-BridgeRequest "list_assets" @{ limit = 1 } "assets"
    Assert-Ok $exchange
    Assert-True (@($exchange.Response.result.assets).Count -le 1) "list_assets ignored limit"
    return $exchange
}

Invoke-BridgeErrorCases @(
    @{ CaseId = "inspection.list_assets.invalid_limit"; Command = "list_assets"; Params = @{ limit = 0 }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "assets-bad" }
    @{ CaseId = "inspection.list_assets.limit_wrong_type"; Command = "list_assets"; Params = @{ limit = "1" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "assets-type" }
)

Invoke-TestCase "inspection.get_asset.assets_root" {
    $exchange = Invoke-BridgeRequest "get_asset" @{ path = "Assets" } "get-asset-root"
    Assert-Ok $exchange
    Assert-Equal $exchange.Response.result.asset.path "Assets" "get_asset root path mismatch"
    Assert-Equal $exchange.Response.result.asset.folder $true "Assets should be reported as a folder"
    return $exchange
}

Invoke-TestCase "inspection.get_asset.missing" {
    $exchange = Invoke-BridgeRequest "get_asset" @{
        path = "Assets/__AgentBridgeMissing__/missing.asset"
    } "get-asset-missing"
    Assert-Error $exchange "ASSET_NOT_FOUND"
    return $exchange
}

Invoke-TestCase "inspection.get_asset_dependencies.package_file" {
    $exchange = Invoke-BridgeRequest "get_asset_dependencies" @{
        path = "Packages/me.xw.unityagentbridge/Editor/Commands/PingHandler.cs"
        recursive = $false
        limit = 10
    } "get-dependencies-package"
    Assert-Ok $exchange
    Assert-Equal $exchange.Response.result.source.path "Packages/me.xw.unityagentbridge/Editor/Commands/PingHandler.cs" "dependency source mismatch"
    Assert-True ([int]$exchange.Response.result.total -ge 0) "dependency total must be non-negative"
    return $exchange
}

Invoke-TestCase "inspection.get_asset_dependencies.reject_folder" {
    $exchange = Invoke-BridgeRequest "get_asset_dependencies" @{ path = "Assets" } "get-dependencies-folder"
    Assert-Error $exchange "INVALID_PARAMS"
    return $exchange
}

Invoke-TestCase "console.search_logs.default" {
    $exchange = Invoke-BridgeRequest "search_logs" @{ limit = 5 } "logs"
    Assert-Ok $exchange
    Assert-True (@($exchange.Response.result.entries).Count -le 5) "search_logs ignored limit"
    return $exchange
}

Invoke-BridgeErrorCases @(
    @{ CaseId = "console.search_logs.invalid_regex"; Command = "search_logs"; Params = @{ query = "["; regex = $true }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "logs-regex" }
    @{ CaseId = "console.search_logs.regex_wrong_type"; Command = "search_logs"; Params = @{ query = "AgentBridge"; regex = "false" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "logs-regex-type" }
    @{ CaseId = "console.search_logs.invalid_type"; Command = "search_logs"; Params = @{ type = "fatal" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "logs-type" }
    @{ CaseId = "console.search_logs.query_too_long"; Command = "search_logs"; Params = @{ query = ("x" * 4097) }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "logs-query-long" }
)

Invoke-TestCase "compilation.get_compile_result" {
    $exchange = Invoke-BridgeRequest "get_compile_result" @{} "compile-result"
    Assert-Ok $exchange
    Assert-True ($null -ne $exchange.Response.result.compiling) "compile result missing compiling"
    return $exchange
}

Invoke-TestCase "testing.run_tests.invalid_mode" {
    $exchange = Invoke-BridgeRequest "run_tests" @{ mode = "invalid" } "tests-invalid-mode"
    Assert-Error $exchange "INVALID_PARAMS"
    return $exchange
}

Invoke-TestCase "testing.get_test_result.missing" {
    $missingRunId = "test-run-missing-$([Guid]::NewGuid().ToString("N").Substring(0, 12))"
    $exchange = Invoke-BridgeRequest "get_test_result" @{ runId = $missingRunId } "tests-missing-result"
    Assert-Error $exchange "TEST_RESULT_NOT_FOUND"
    return $exchange
}

# Write-capable commands are exercised only through inputs that must fail before mutation.
$missingTransform = @{ object = @{ instanceId = 2147483647 }; type = "UnityEngine.Transform"; index = 0 }
Invoke-BridgeErrorCases @(
    @{ CaseId = "capture.invalid_file_name"; Command = "capture_game_view"; Params = @{ fileName = "../escape.png" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "capture-bad" }
    @{ CaseId = "capture.scene_view.invalid_file_name"; Command = "capture_scene_view"; Params = @{ fileName = "../escape.png" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "scene-capture-bad" }
    @{ CaseId = "mutation.create_object.invalid_kind"; Command = "create_object"; Params = @{ kind = "invalid" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "create-object-bad" }
    @{ CaseId = "mutation.create_object.invalid_primitive_numeric"; Command = "create_object"; Params = @{ kind = "primitive"; primitive = "999" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "create-primitive-bad" }
    @{ CaseId = "mutation.delete_object.missing"; Command = "delete_object"; Params = @{ object = @{ instanceId = 2147483647 } }; ExpectedCode = "OBJECT_NOT_FOUND"; IdPrefix = "delete-object-bad" }
    @{ CaseId = "mutation.set_property.missing_component"; Command = "set_property"; Params = @{ component = $missingTransform; propertyPath = "m_LocalPosition"; value = @{ x = 0; y = 0; z = 0 } }; ExpectedCode = "OBJECT_NOT_FOUND"; IdPrefix = "set-property-bad" }
    @{ CaseId = "mutation.set_property.missing_value"; Command = "set_property"; Params = @{ component = $missingTransform; propertyPath = "m_LocalPosition" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "set-property-no-value" }
    @{ CaseId = "mutation.add_component.missing_object"; Command = "add_component"; Params = @{ object = @{ instanceId = 2147483647 }; type = "UnityEngine.AudioSource" }; ExpectedCode = "OBJECT_NOT_FOUND"; IdPrefix = "add-component-missing" }
    @{ CaseId = "mutation.remove_component.missing_object"; Command = "remove_component"; Params = @{ component = @{ object = @{ instanceId = 2147483647 }; type = "UnityEngine.AudioSource"; index = 0 } }; ExpectedCode = "OBJECT_NOT_FOUND"; IdPrefix = "remove-component-missing" }
    @{ CaseId = "mutation.update_object.requires_change"; Command = "update_object"; Params = @{ object = @{ instanceId = 2147483647 } }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "update-object-empty" }
    @{ CaseId = "mutation.prefab.missing_object"; Command = "prefab"; Params = @{ action = "status"; object = @{ instanceId = 2147483647 } }; ExpectedCode = "OBJECT_NOT_FOUND"; IdPrefix = "prefab-missing" }
    @{ CaseId = "mutation.set_selection.invalid_active"; Command = "set_selection"; Params = @{ objects = @(); active = @{ instanceId = 2147483647 } }; ExpectedCode = "OBJECT_NOT_FOUND"; IdPrefix = "selection-invalid-active" }
    @{ CaseId = "mutation.frame_object.missing"; Command = "frame_object"; Params = @{ object = @{ instanceId = 2147483647 } }; ExpectedCode = "OBJECT_NOT_FOUND"; IdPrefix = "frame-missing" }
)

Invoke-TestCase "mutation.batch.success" {
    $exchange = Invoke-BridgeRequest "batch" @{
        steps = @(
            @{ command = "ping"; params = @{} },
            @{ command = "list_scenes"; params = @{} }
        )
    } "batch-success"
    Assert-Ok $exchange
    Assert-Equal $exchange.Response.result.success $true "batch should succeed"
    Assert-Equal ([int]$exchange.Response.result.executed) 2 "batch executed count mismatch"
    Assert-Equal @($exchange.Response.result.results | Where-Object status -eq "ok").Count 2 "batch child status mismatch"
    return $exchange
}

Invoke-TestCase "mutation.batch.prevalidation_is_side_effect_free" {
    $exchange = Invoke-BridgeRequest "batch" @{
        steps = @(
            @{ command = "ping"; params = @{} },
            @{ command = "get_object"; params = @{} }
        )
    } "batch-prevalidation"
    Assert-Error $exchange "INVALID_PARAMS"
    return $exchange
}

Invoke-TestCase "mutation.batch.stop_on_runtime_error" {
    $exchange = Invoke-BridgeRequest "batch" @{
        steps = @(
            @{ command = "ping"; params = @{} },
            @{ command = "get_object"; params = @{ object = @{ instanceId = 2147483647 } } },
            @{ command = "ping"; params = @{} }
        )
        stopOnError = $true
    } "batch-stop"
    Assert-Ok $exchange
    Assert-Equal $exchange.Response.result.success $false "batch runtime failure was not reported"
    Assert-Equal ([int]$exchange.Response.result.executed) 2 "batch did not stop at failed child"
    Assert-Equal $exchange.Response.result.stopped $true "batch stopped flag mismatch"
    Assert-Equal $exchange.Response.result.results[1].error.code "OBJECT_NOT_FOUND" "batch child error mismatch"
    return $exchange
}

Invoke-TestCase "mutation.batch.continue_on_runtime_error" {
    $exchange = Invoke-BridgeRequest "batch" @{
        steps = @(
            @{ command = "ping"; params = @{} },
            @{ command = "get_object"; params = @{ object = @{ instanceId = 2147483647 } } },
            @{ command = "ping"; params = @{} }
        )
        stopOnError = $false
    } "batch-continue"
    Assert-Ok $exchange
    Assert-Equal $exchange.Response.result.success $false "batch should retain failure status"
    Assert-Equal ([int]$exchange.Response.result.executed) 3 "batch did not continue after failure"
    Assert-Equal $exchange.Response.result.stopped $false "batch stopped unexpectedly"
    return $exchange
}

Invoke-BridgeErrorCases @(
    @{ CaseId = "mutation.batch.reject_forbidden"; Command = "batch"; Params = @{ steps = @(@{ command = "batch"; params = @{ steps = @() } }) }; ExpectedCode = "BATCH_COMMAND_NOT_ALLOWED"; IdPrefix = "batch-forbidden" }
    @{ CaseId = "mutation.invoke_menu.missing"; Command = "invoke_menu"; Params = @{ path = "AgentBridge/__DefinitelyMissing__" }; ExpectedCode = "MENU_NOT_FOUND"; IdPrefix = "menu-bad" }
    @{ CaseId = "mutation.set_game_view_resolution.invalid"; Command = "set_game_view_resolution"; Params = @{ width = 0; height = 480 }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "resolution-bad" }
    @{ CaseId = "mutation.set_game_view_resolution.pixel_limit"; Command = "set_game_view_resolution"; Params = @{ width = 8192; height = 8192 }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "resolution-pixels-bad" }
    @{ CaseId = "play_mode.invalid_target_pair"; Command = "play_scene"; Params = @{ scenePath = "Assets/Launcher.unity"; buildIndex = 0 }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "play-bad" }
    @{ CaseId = "play_mode.build_index_overflow"; Command = "play_scene"; Params = @{ buildIndex = [decimal]2147483648 }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "play-index-overflow" }
    @{ CaseId = "play_mode.pause.requires_play_mode"; Command = "pause"; Params = @{}; ExpectedCode = "PLAY_MODE_NOT_ACTIVE"; IdPrefix = "pause-stopped" }
    @{ CaseId = "play_mode.resume.requires_play_mode"; Command = "resume"; Params = @{}; ExpectedCode = "PLAY_MODE_NOT_ACTIVE"; IdPrefix = "resume-stopped" }
    @{ CaseId = "play_mode.step.requires_play_mode"; Command = "step"; Params = @{}; ExpectedCode = "PLAY_MODE_NOT_ACTIVE"; IdPrefix = "step-stopped" }
    @{ CaseId = "scenes.save_scene.missing_handle"; Command = "save_scene"; Params = @{ sceneHandle = 2147483647 }; ExpectedCode = "SCENE_NOT_LOADED"; IdPrefix = "save-scene-missing" }
    @{ CaseId = "scenes.open_scene.missing_asset"; Command = "open_scene"; Params = @{ scenePath = "Assets/__AgentBridgeMissing__/missing.unity"; mode = "additive" }; ExpectedCode = "SCENE_NOT_FOUND"; IdPrefix = "open-scene-missing" }
    @{ CaseId = "scenes.set_active_scene.missing_handle"; Command = "set_active_scene"; Params = @{ sceneHandle = 2147483647 }; ExpectedCode = "SCENE_NOT_LOADED"; IdPrefix = "active-scene-missing" }
    @{ CaseId = "assets.create_asset.invalid_kind"; Command = "create_asset"; Params = @{ kind = "invalid"; path = "Assets/__AgentBridgeInvalid__" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "asset-create-bad" }
    @{ CaseId = "assets.create_asset.text_missing_content"; Command = "create_asset"; Params = @{ kind = "text"; path = "Assets/__AgentBridgeMissingContent__.txt" }; ExpectedCode = "INVALID_PARAMS"; IdPrefix = "asset-content-bad" }
    @{ CaseId = "assets.set_importer_property.missing_asset"; Command = "set_importer_property"; Params = @{ path = "Assets/__AgentBridgeMissing__/missing.txt"; propertyPath = "m_UserData"; value = "blocked" }; ExpectedCode = "ASSET_NOT_FOUND"; IdPrefix = "importer-missing" }
    @{ CaseId = "assets.import_asset.missing_source"; Command = "import_asset"; Params = @{ source = (Join-Path $bridgeRoot "missing-source.txt"); destination = "Assets/__AgentBridgeInvalid__/missing.txt" }; ExpectedCode = "ASSET_SOURCE_NOT_FOUND"; IdPrefix = "asset-import-bad" }
    @{ CaseId = "assets.move_asset.missing_source"; Command = "move_asset"; Params = @{ from = "Assets/__AgentBridgeMissing__/from.txt"; to = "Assets/__AgentBridgeMissing__/to.txt" }; ExpectedCode = "ASSET_MOVE_FAILED"; IdPrefix = "asset-move-bad" }
    @{ CaseId = "assets.move_asset.reject_root"; Command = "move_asset"; Params = @{ from = "Assets"; to = "AssetsRenamed" }; ExpectedCode = "INVALID_ASSET_PATH"; IdPrefix = "asset-move-root" }
    @{ CaseId = "assets.delete_asset.missing"; Command = "delete_asset"; Params = @{ path = "Assets/__AgentBridgeMissing__/missing.asset" }; ExpectedCode = "ASSET_NOT_FOUND"; IdPrefix = "asset-delete-bad" }
    @{ CaseId = "assets.delete_asset.reject_root"; Command = "delete_asset"; Params = @{ path = "Assets" }; ExpectedCode = "INVALID_ASSET_PATH"; IdPrefix = "asset-delete-root" }
    @{ CaseId = "assets.create_asset.reject_reserved_name"; Command = "create_asset"; Params = @{ kind = "text"; path = "Assets/CON.txt"; content = "blocked" }; ExpectedCode = "INVALID_ASSET_PATH"; IdPrefix = "asset-reserved-name" }
    @{ CaseId = "assets.create_asset.reject_trailing_dot"; Command = "create_asset"; Params = @{ kind = "text"; path = "Assets/blocked."; content = "blocked" }; ExpectedCode = "INVALID_ASSET_PATH"; IdPrefix = "asset-trailing-dot" }
    @{ CaseId = "assets.create_asset.reject_outer_whitespace"; Command = "create_asset"; Params = @{ kind = "text"; path = " Assets/blocked.txt "; content = "blocked" }; ExpectedCode = "INVALID_ASSET_PATH"; IdPrefix = "asset-path-whitespace" }
)

if ($script:CommandMap.ContainsKey("codebind")) {
    Invoke-TestCase "extension.codebind.invalid_action" {
        $exchange = Invoke-BridgeRequest "codebind" @{ action = "invalid"; assetPath = "Assets/__AgentBridgeMissing__.prefab" } "codebind-bad"
        Assert-Error $exchange "INVALID_PARAMS"
        return $exchange
    }
}

if ($script:CommandMap.ContainsKey("statecontroller")) {
    Invoke-TestCase "extension.statecontroller.invalid_action" {
        $exchange = Invoke-BridgeRequest "statecontroller" @{ action = "invalid"; assetPath = "Assets/__AgentBridgeMissing__.prefab" } "state-bad"
        Assert-Error $exchange "INVALID_PARAMS"
        return $exchange
    }
}

if ($script:CommandMap.ContainsKey("uxtool")) {
    Invoke-TestCase "extension.uxtool.invalid_action" {
        $exchange = Invoke-BridgeRequest "uxtool" @{ action = "invalid" } "uxtool-bad"
        Assert-Error $exchange "INVALID_PARAMS"
        return $exchange
    }
}

if ($Suite -ne "Baseline") {
    $fixtureId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
    $fixtureName = "__AgentBridgeValidation_$fixtureId"
    $script:SceneFixtureRef = $null
    $script:SceneComponentRef = $null
    $script:AssetRoot = "Assets/$fixtureName"
    $script:ScreenshotPath = $null
    $script:SceneViewScreenshotPath = $null
    $script:DirtyScenesBeforePlay = @()
    $script:UxBackgroundRef = $null
    $script:EditScenePath = $null
    $script:EditSceneHashBefore = $null
    $script:SceneSourcePath = $null
    $script:SceneFixturePath = "$($script:AssetRoot)/scene-fixture.unity"
    $script:SceneFixtureSecondPath = "$($script:AssetRoot)/scene-fixture-second.unity"
    $script:SceneFixtureSecondSavedAsPath = "$($script:AssetRoot)/scene-fixture-second-saved-as.unity"
    $script:OriginalActiveSceneSelector = $null
    $script:EditFixtureRootRef = $null
    $script:EditFixtureOtherRef = $null
    $script:EditFixtureChildRef = $null
    $script:PrefabSourceRef = $null
    $script:PrefabInstanceRef = $null
    $script:PrefabAssetPath = "$($script:AssetRoot)/agentbridge-prefab.prefab"
    $script:RuntimeAudioComponentRef = $null
    $script:RuntimeChildRef = $null
    $script:TextAssetGuid = $null
    $script:ImportedAssetGuid = $null
    $script:GameViewRestore = $null
    $uxImagePath = "Assets/Res/Editor/UI/UXTool/Tools/Res/Icon/QuickBackgroundDefault.png"
    $uxPrefabPath = "Assets/Res/Editor/UI/UXTool/Tools/Res/UXQuickBackground.prefab"
    $uxDataFullPath = Join-Path $project "Assets/Res/Editor/UI/UXTool/Tools/UserDatas/QuickBackgroundData.json"
    $uxDataHashBefore = if (Test-Path -LiteralPath $uxDataFullPath) {
        (Get-FileHash -Algorithm SHA256 -LiteralPath $uxDataFullPath).Hash
    } else { $null }

    Invoke-TestCase "console.clear_logs" {
        $exchange = Invoke-BridgeRequest "clear_logs" @{} "clear-logs"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.cleared $true "clear_logs did not report success"
        Assert-True ([int]$exchange.Response.result.clearedCount -ge 0) "clearedCount must be non-negative"
        return $exchange
    }

    Invoke-TestCase "console.clear_logs.idempotent" {
        $exchange = Invoke-BridgeRequest "clear_logs" @{} "clear-logs-again"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.cleared $true "repeated clear_logs did not report success"
        Assert-True ([int]$exchange.Response.result.clearedCount -ge 0) "repeated clearedCount must be non-negative"
        return $exchange
    }

    Invoke-TestCase "play_mode.play_current_scene" {
        $exchange = Invoke-BridgeRequest "play_scene" @{} "play-current"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.playRequested $true "play_scene did not request Play Mode"
        $script:DirtyScenesBeforePlay = @($exchange.Response.result.unsaved.dirtyScenes)
        return $exchange
    }

    Invoke-TestCase "play_mode.wait_until_entered" {
        $exchange = Wait-PlayModeState $true
        return $exchange
    }

    Invoke-TestCase "play_mode.resume.ensure_running" {
        $exchange = Invoke-BridgeRequest "resume" @{} "resume-running"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.paused $false "resume left PlayMode paused"
        return $exchange
    }

    Invoke-TestCase "play_mode.pause" {
        $exchange = Invoke-BridgeRequest "pause" @{} "pause-running"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.paused $true "pause did not pause PlayMode"
        Assert-Equal $exchange.Response.result.changed $true "pause did not report its state change"
        return $exchange
    }

    Invoke-TestCase "play_mode.pause.idempotent" {
        $exchange = Invoke-BridgeRequest "pause" @{} "pause-again"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.changed $false "second pause should be idempotent"
        return $exchange
    }

    Invoke-TestCase "play_mode.step" {
        $exchange = Invoke-BridgeRequest "step" @{} "step-paused"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.stepRequested $true "step request was not reported"
        Assert-Equal $exchange.Response.result.paused $true "step should leave PlayMode paused"
        return $exchange
    }

    Invoke-TestCase "play_mode.resume" {
        $exchange = Invoke-BridgeRequest "resume" @{} "resume-paused"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.paused $false "resume did not unpause PlayMode"
        Assert-Equal $exchange.Response.result.changed $true "resume did not report its state change"
        return $exchange
    }

    Invoke-TestCase "play_mode.resume.idempotent" {
        $exchange = Invoke-BridgeRequest "resume" @{} "resume-again"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.paused $false "repeated resume left PlayMode paused"
        Assert-Equal $exchange.Response.result.changed $false "second resume should be idempotent"
        return $exchange
    }

    Invoke-TestCase "play_mode.step.reject_unpaused" {
        $exchange = Invoke-BridgeRequest "step" @{} "step-unpaused"
        Assert-Error $exchange "PLAY_MODE_NOT_PAUSED"
        return $exchange
    }

    Invoke-TestCase "mutation.undo.reject_play_mode" {
        $exchange = Invoke-BridgeRequest "undo" @{} "undo-playing"
        Assert-Error $exchange "EDIT_MODE_REQUIRED"
        return $exchange
    }

    Invoke-TestCase "mutation.redo.reject_play_mode" {
        $exchange = Invoke-BridgeRequest "redo" @{} "redo-playing"
        Assert-Error $exchange "EDIT_MODE_REQUIRED"
        return $exchange
    }


    Invoke-TestCase "assets.refresh.reject_play_mode" {
        $exchange = Invoke-BridgeRequest "refresh" @{} "refresh-playing"
        Assert-Error $exchange "REFRESH_NOT_ALLOWED_IN_PLAY_MODE"
        return $exchange
    }

    if ($script:CommandMap.ContainsKey("uxtool") -and
        (Test-Path -LiteralPath (Join-Path $project $uxImagePath))) {
        Invoke-TestCase "extension.uxtool.background_absent_before" {
            $exchange = Invoke-BridgeRequest "get_object" @{ object = @{ path = "UXQuickBackground" } } "uxtool-before"
            Assert-Error $exchange "OBJECT_NOT_FOUND"
            return $exchange
        }

        Invoke-TestCase "extension.uxtool.add_background" {
            $exchange = Invoke-BridgeRequest "uxtool" @{
                action = "add_background"
                designImage = $uxImagePath
                color = "0.25,0.5,0.75,0.8"
            } "uxtool-add"
            Assert-Ok $exchange
            Assert-Equal $exchange.Response.result.designImage $uxImagePath "uxtool design image mismatch"
            Assert-Equal ([int]$exchange.Response.result.size.width) 1920 "uxtool background width mismatch"
            Assert-Equal ([int]$exchange.Response.result.size.height) 1080 "uxtool background height mismatch"
            return $exchange
        }

        Invoke-TestCase "extension.uxtool.verify_background" {
            $exchange = Invoke-BridgeRequest "get_object" @{
                object = @{ path = "UXQuickBackground/UXQuickBackgroundImage" }
                componentTypes = @("UnityEngine.UI.Image")
            } "uxtool-verify"
            Assert-Ok $exchange
            $image = @($exchange.Response.result.components) | Select-Object -First 1
            Assert-True ($null -ne $image) "uxtool background Image component missing"
            Assert-Near ([double]$image.properties.m_Color.r) 0.25 0.001 "uxtool color.r mismatch"
            Assert-Near ([double]$image.properties.m_Color.g) 0.5 0.001 "uxtool color.g mismatch"
            Assert-Near ([double]$image.properties.m_Color.b) 0.75 0.001 "uxtool color.b mismatch"
            Assert-Near ([double]$image.properties.m_Color.a) 0.8 0.001 "uxtool color.a mismatch"
            Assert-Equal $image.properties.m_Sprite.assetPath $uxImagePath "uxtool sprite reference mismatch"
            $script:UxBackgroundRef = [ordered]@{
                path = "UXQuickBackground"
                scenePath = [string]$exchange.Response.result.object.scenePath
            }
            return $exchange
        }

        Invoke-TestCase "extension.uxtool.cleanup_background" {
            if ($null -eq $script:UxBackgroundRef) { throw "uxtool background reference unavailable" }
            $exchange = Invoke-BridgeRequest "delete_object" @{ object = $script:UxBackgroundRef } "uxtool-delete"
            Assert-Ok $exchange
            Assert-Equal $exchange.Response.result.persistent $false "uxtool PlayMode cleanup must be transient"
            return $exchange
        }
    }

    if (Test-Path -LiteralPath (Join-Path $project $uxPrefabPath)) {
        Invoke-TestCase "mutation.create_object.prefab" {
            $created = Invoke-BridgeRequest "create_object" @{
                kind = "prefab"
                prefabPath = $uxPrefabPath
                name = "${fixtureName}_Prefab"
            } "create-prefab"
            Assert-Ok $created
            Assert-Equal $created.Response.result.persistent $false "PlayMode prefab instance must be transient"
            $ref = @{
                instanceId = [int]$created.Response.result.object.instanceId
                scenePath = [string]$created.Response.result.object.scenePath
            }
            $deleted = Invoke-BridgeRequest "delete_object" @{ object = $ref } "delete-prefab"
            Assert-Ok $deleted
            return $created
        }
    }

    Invoke-TestCase "mutation.create_object.empty" {
        $params = @{
            kind = "empty"
            name = $fixtureName
            position = @{ x = 1.25; y = -2.5; z = 3.75 }
            rotation = @{ x = 5; y = 10; z = 15 }
            scale = @{ x = 1; y = 1.5; z = 2 }
            active = $false
        }
        $exchange = Invoke-BridgeRequest "create_object" $params "create-empty"
        Assert-Ok $exchange
        $created = $exchange.Response.result.object
        Assert-Equal $created.name $fixtureName "created object name mismatch"
        Assert-Equal $created.active $false "created object active state mismatch"
        $script:SceneFixtureRef = [ordered]@{
            path = [string]$created.path
            instanceId = [int]$created.instanceId
            scenePath = [string]$created.scenePath
        }
        Assert-Equal $exchange.Response.result.persistent $false "PlayMode create_object must report persistent=false"
        return $exchange
    }

    Invoke-TestCase "mutation.create_object.reject_non_finite_vector" {
        $exchange = Invoke-BridgeRequest "create_object" @{
            kind = "empty"
            name = "${fixtureName}_InvalidVector"
            position = @{ x = 1e100; y = 0; z = 0 }
        } "create-invalid-vector"
        Assert-Error $exchange "INVALID_PARAMS"
        return $exchange
    }

    Invoke-TestCase "mutation.get_object.created" {
        if ($null -eq $script:SceneFixtureRef) { throw "scene fixture was not created" }
        $exchange = Invoke-BridgeRequest "get_object" @{ object = $script:SceneFixtureRef } "get-created"
        Assert-Ok $exchange
        $transform = @($exchange.Response.result.components | Where-Object type -eq "UnityEngine.Transform") | Select-Object -First 1
        Assert-True ($null -ne $transform) "created object Transform was not returned"
        Assert-Equal $transform.exactType $true "get_object component ref must declare exactType=true"
        Assert-Equal ([math]::Round([double]$transform.properties.m_LocalPosition.x, 2)) 1.25 "initial position.x mismatch"
        $script:SceneComponentRef = [ordered]@{
            object = $script:SceneFixtureRef
            type = "UnityEngine.Transform"
            index = 0
            exactType = $true
        }
        return $exchange
    }

    Invoke-TestCase "mutation.set_property.vector3" {
        if ($null -eq $script:SceneComponentRef) { throw "scene component fixture was not resolved" }
        $value = @{ x = 12.5; y = 0.25; z = -4.75 }
        $exchange = Invoke-BridgeRequest "set_property" @{
            component = $script:SceneComponentRef
            propertyPath = "m_LocalPosition"
            value = $value
        } "set-vector3"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.applied $true "set_property did not report applied"
        Assert-Equal $exchange.Response.result.persistent $false "PlayMode set_property must report persistent=false"
        return $exchange
    }

    Invoke-TestCase "mutation.set_property.reject_non_finite_vector" {
        $exchange = Invoke-BridgeRequest "set_property" @{
            component = $script:SceneComponentRef
            propertyPath = "m_LocalPosition"
            value = @{ x = 1e100; y = 0; z = 0 }
        } "set-invalid-vector"
        Assert-Error $exchange "PROPERTY_TYPE_MISMATCH"
        return $exchange
    }

    Invoke-TestCase "mutation.get_object.verify_property" {
        $exchange = Invoke-BridgeRequest "get_object" @{ object = $script:SceneFixtureRef } "verify-property"
        Assert-Ok $exchange
        $transform = @($exchange.Response.result.components | Where-Object type -eq "UnityEngine.Transform") | Select-Object -First 1
        Assert-Equal ([math]::Round([double]$transform.properties.m_LocalPosition.x, 2)) 12.5 "set_property position.x was not persisted"
        Assert-Equal ([math]::Round([double]$transform.properties.m_LocalPosition.z, 2)) -4.75 "set_property position.z was not persisted"
        return $exchange
    }

    Invoke-TestCase "mutation.add_component.play_mode" {
        $exchange = Invoke-BridgeRequest "add_component" @{
            object = $script:SceneFixtureRef
            type = "UnityEngine.AudioSource"
        } "add-audio-runtime"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.added $true "add_component did not report success"
        Assert-Equal $exchange.Response.result.persistent $false "PlayMode component must be transient"
        $script:RuntimeAudioComponentRef = $exchange.Response.result.component
        return $exchange
    }

    Invoke-TestCase "mutation.add_component.reject_transform" {
        $exchange = Invoke-BridgeRequest "add_component" @{
            object = $script:SceneFixtureRef
            type = "UnityEngine.Transform"
        } "add-transform-runtime"
        Assert-Error $exchange "COMPONENT_TYPE_NOT_ADDABLE"
        return $exchange
    }

    Invoke-TestCase "mutation.remove_component.reject_transform" {
        $exchange = Invoke-BridgeRequest "remove_component" @{
            component = $script:SceneComponentRef
        } "remove-transform-runtime"
        Assert-Error $exchange "COMPONENT_REMOVE_NOT_ALLOWED"
        return $exchange
    }

    Invoke-TestCase "mutation.remove_component.play_mode" {
        if ($null -eq $script:RuntimeAudioComponentRef) { throw "runtime AudioSource was not added" }
        $exchange = Invoke-BridgeRequest "remove_component" @{
            component = $script:RuntimeAudioComponentRef
        } "remove-audio-runtime"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.removed $true "remove_component did not report success"
        Assert-Equal $exchange.Response.result.persistent $false "PlayMode removal must be transient"
        return $exchange
    }

    Invoke-TestCase "mutation.remove_component.repeated_is_missing" {
        $exchange = Invoke-BridgeRequest "remove_component" @{
            component = $script:RuntimeAudioComponentRef
        } "remove-audio-runtime-again"
        Assert-Error $exchange "COMPONENT_NOT_FOUND"
        return $exchange
    }

    Invoke-TestCase "mutation.update_object.reject_static_in_play_mode" {
        $exchange = Invoke-BridgeRequest "update_object" @{
            object = $script:SceneFixtureRef
            isStatic = $true
        } "update-static-runtime"
        Assert-Error $exchange "STATIC_EDIT_MODE_REQUIRED"
        return $exchange
    }

    Invoke-TestCase "mutation.update_object.reject_invalid_tag" {
        $exchange = Invoke-BridgeRequest "update_object" @{
            object = $script:SceneFixtureRef
            tag = "__AgentBridgeUndefinedTag__"
        } "update-invalid-tag-runtime"
        Assert-Error $exchange "INVALID_TAG"
        return $exchange
    }

    Invoke-TestCase "mutation.update_object.play_mode" {
        $updatedName = "${fixtureName}_RuntimeUpdated"
        $exchange = Invoke-BridgeRequest "update_object" @{
            object = $script:SceneFixtureRef
            name = $updatedName
            active = $true
            tag = "Untagged"
            layer = "Default"
            position = @{ x = -3.25; y = 2.5; z = 7.75 }
            rotation = @{ x = 10; y = 20; z = 30 }
            scale = @{ x = 1.25; y = 0.75; z = 2 }
        } "update-runtime"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.persistent $false "PlayMode update must be transient"
        Assert-Equal $exchange.Response.result.object.name $updatedName "updated name mismatch"
        Assert-Near ([double]$exchange.Response.result.state.position.x) -3.25 0.001 "updated position.x mismatch"
        $script:SceneFixtureRef = ConvertTo-StableObjectRef $exchange.Response.result.object
        return $exchange
    }

    Invoke-TestCase "mutation.update_object.idempotent_values" {
        $exchange = Invoke-BridgeRequest "update_object" @{
            object = $script:SceneFixtureRef
            name = "${fixtureName}_RuntimeUpdated"
            active = $true
            tag = "Untagged"
            layer = 0
        } "update-runtime-again"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.object.name "${fixtureName}_RuntimeUpdated" "idempotent update changed name"
        return $exchange
    }

    Invoke-TestCase "mutation.create_object.child" {
        $childName = "${fixtureName}_Child"
        $exchange = Invoke-BridgeRequest "create_object" @{
            kind = "empty"
            name = $childName
            parent = $script:SceneFixtureRef
            position = @{ x = 1; y = 2; z = 3 }
        } "create-child"
        Assert-Ok $exchange
        Assert-True ([string]$exchange.Response.result.object.path -like "$($script:SceneFixtureRef.path)/*") "child path does not include parent"
        $script:RuntimeChildRef = ConvertTo-StableObjectRef $exchange.Response.result.object
        return $exchange
    }

    Invoke-TestCase "mutation.update_object.reject_parent_cycle" {
        $exchange = Invoke-BridgeRequest "update_object" @{
            object = $script:SceneFixtureRef
            parent = $script:RuntimeChildRef
        } "update-cycle-runtime"
        Assert-Error $exchange "PARENT_CYCLE"
        return $exchange
    }

    Invoke-TestCase "mutation.batch.play_mode_mutations" {
        $exchange = Invoke-BridgeRequest "batch" @{
            steps = @(
                @{ command = "update_object"; params = @{ object = $script:SceneFixtureRef; active = $false } },
                @{ command = "update_object"; params = @{ object = $script:SceneFixtureRef; active = $true } }
            )
            collapseUndo = $true
        } "batch-runtime-mutations"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.success $true "PlayMode mutation batch failed"
        Assert-Equal $exchange.Response.result.collapsedUndo $false "PlayMode batch must not claim Undo collapsing"
        return $exchange
    }

    Invoke-TestCase "mutation.set_selection.lifecycle" {
        $before = Invoke-BridgeRequest "get_selection" @{} "selection-before-runtime"
        Assert-Ok $before
        $beforeRefs = @($before.Response.result.selection | ForEach-Object { ConvertTo-StableObjectRef $_ })
        try {
            $set = Invoke-BridgeRequest "set_selection" @{
                objects = @($script:SceneFixtureRef, $script:RuntimeChildRef, $script:SceneFixtureRef)
                active = $script:RuntimeChildRef
            } "selection-set-runtime"
            Assert-Ok $set
            Assert-Equal ([int]$set.Response.result.count) 2 "set_selection did not de-duplicate objects"
            Assert-Equal $set.Response.result.active.instanceId $script:RuntimeChildRef.instanceId "active selection mismatch"

            $invalid = Invoke-BridgeRequest "set_selection" @{
                objects = @($script:SceneFixtureRef)
                active = $script:RuntimeChildRef
            } "selection-active-not-member"
            Assert-Error $invalid "INVALID_PARAMS"

            $clear = Invoke-BridgeRequest "set_selection" @{ objects = @() } "selection-clear-runtime"
            Assert-Ok $clear
            Assert-Equal ([int]$clear.Response.result.count) 0 "set_selection did not clear selection"
            $clearAgain = Invoke-BridgeRequest "set_selection" @{ objects = @() } "selection-clear-runtime-again"
            Assert-Ok $clearAgain
            Assert-Equal ([int]$clearAgain.Response.result.count) 0 "clearing selection was not idempotent"
        }
        finally {
            $restoreParams = @{ objects = $beforeRefs }
            if ($beforeRefs.Count -gt 0) { $restoreParams.active = $beforeRefs[0] }
            $restored = Invoke-BridgeRequest "set_selection" $restoreParams "selection-restore-runtime"
            Assert-Ok $restored
        }
        return $set
    }

    Invoke-TestCase "mutation.frame_object.play_mode" {
        $exchange = Invoke-BridgeRequest "frame_object" @{
            object = $script:SceneFixtureRef
            instant = $true
        } "frame-runtime"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.framed $true "frame_object did not frame fixture"
        Assert-True ([double]$exchange.Response.result.bounds.size.x -gt 0) "framed bounds must be non-empty"
        $again = Invoke-BridgeRequest "frame_object" @{
            object = $script:SceneFixtureRef
            instant = $true
        } "frame-runtime-again"
        Assert-Ok $again
        Assert-Equal $again.Response.result.framed $true "repeated frame_object should remain valid"
        return $again
    }

    Invoke-TestCase "mutation.get_hierarchy.created_subtree" {
        $exchange = Invoke-BridgeRequest "get_hierarchy" @{ root = $script:SceneFixtureRef; maxDepth = 2 } "created-tree"
        Assert-Ok $exchange
        $roots = @($exchange.Response.result.scenes[0].roots)
        Assert-Equal $roots.Count 1 "fixture subtree should contain one root"
        Assert-Equal @($roots[0].children).Count 1 "fixture subtree should contain one child"
        return $exchange
    }


    Invoke-TestCase "mutation.get_hierarchy.limit_truncates" {
        $exchange = Invoke-BridgeRequest "get_hierarchy" @{ root = $script:SceneFixtureRef; maxDepth = -1; limit = 1 } "created-tree-limit"
        Assert-Ok $exchange
        Assert-Equal ([int]$exchange.Response.result.count) 1 "hierarchy count mismatch at limit=1"
        Assert-Equal $exchange.Response.result.truncated $true "hierarchy should report truncated at limit=1"
        return $exchange
    }

    Invoke-TestCase "mutation.delete_object.created_tree" {
        $exchange = Invoke-BridgeRequest "delete_object" @{ object = $script:SceneFixtureRef } "delete-tree"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.persistent $false "PlayMode delete_object must report persistent=false"
        Assert-Equal $exchange.Response.result.deleted $true "delete_object did not report deleted"
        return $exchange
    }

    Invoke-TestCase "mutation.deleted_object_is_missing" {
        $exchange = Invoke-BridgeRequest "get_object" @{ object = $script:SceneFixtureRef } "deleted-missing"
        Assert-Error $exchange "OBJECT_NOT_FOUND"
        return $exchange
    }

    Invoke-TestCase "mutation.create_object.primitive" {
        $name = "${fixtureName}_Cube"
        $created = Invoke-BridgeRequest "create_object" @{ kind = "primitive"; primitive = "Cube"; name = $name } "create-primitive"
        Assert-Ok $created
        $ref = @{ path = [string]$created.Response.result.object.path; instanceId = [int]$created.Response.result.object.instanceId }
        $deleted = Invoke-BridgeRequest "delete_object" @{ object = $ref } "delete-primitive"
        Assert-Ok $deleted
        $missing = Invoke-BridgeRequest "get_object" @{ object = $ref } "primitive-missing"
        Assert-Error $missing "OBJECT_NOT_FOUND"
        return $created
    }


    Invoke-TestCase "mutation.object_ref.reject_ambiguous_path" {
        $duplicateName = "${fixtureName}_Duplicate"
        $first = Invoke-BridgeRequest "create_object" @{ kind = "empty"; name = $duplicateName } "create-duplicate-a"
        Assert-Ok $first
        $second = Invoke-BridgeRequest "create_object" @{ kind = "empty"; name = $duplicateName } "create-duplicate-b"
        Assert-Ok $second
        $firstRef = @{ instanceId = [int]$first.Response.result.object.instanceId }
        $secondRef = @{ instanceId = [int]$second.Response.result.object.instanceId }
        try {
            $ambiguous = Invoke-BridgeRequest "get_object" @{ object = @{ path = $duplicateName } } "ambiguous-path"
            Assert-Error $ambiguous "OBJECT_REF_AMBIGUOUS"
            $byId = Invoke-BridgeRequest "get_object" @{ object = $firstRef } "ambiguous-by-id"
            Assert-Ok $byId
        }
        finally {
            $deleteFirst = Invoke-BridgeRequest "delete_object" @{ object = $firstRef } "delete-duplicate-a"
            Assert-Ok $deleteFirst
            $deleteSecond = Invoke-BridgeRequest "delete_object" @{ object = $secondRef } "delete-duplicate-b"
            Assert-Ok $deleteSecond
        }
        return $first
    }

    Invoke-TestCase "mutation.object_ref.escaped_path_round_trip" {
        $objectName = "${fixtureName}/Slash~Name"
        $created = Invoke-BridgeRequest "create_object" @{ kind = "empty"; name = $objectName } "create-escaped-name"
        Assert-Ok $created
        $ref = [ordered]@{
            path = [string]$created.Response.result.object.path
            scenePath = [string]$created.Response.result.object.scenePath
        }
        Assert-True ($ref.path -like "*~1Slash~0Name") "escaped object path did not encode slash/tilde"
        try {
            $resolved = Invoke-BridgeRequest "get_object" @{ object = $ref } "get-escaped-name"
            Assert-Ok $resolved
            Assert-Equal $resolved.Response.result.object.name $objectName "escaped path resolved wrong object"
        }
        finally {
            $deleted = Invoke-BridgeRequest "delete_object" @{
                object = @{ instanceId = [int]$created.Response.result.object.instanceId }
            } "delete-escaped-name"
            Assert-Ok $deleted
        }
        return $created
    }

    Invoke-TestCase "mutation.object_ref.reject_stale_hints" {
        $created = Invoke-BridgeRequest "create_object" @{
            kind = "empty"; name = "${fixtureName}_StaleRef"
        } "create-stale-ref"
        Assert-Ok $created
        $instanceId = [int]$created.Response.result.object.instanceId
        try {
            $exchange = Invoke-BridgeRequest "get_object" @{
                object = @{
                    instanceId = $instanceId
                    scenePath = "Assets/__AgentBridgeWrongScene__.unity"
                }
            } "stale-object-ref"
            Assert-Error $exchange "OBJECT_REF_STALE"
            return $exchange
        }
        finally {
            $deleted = Invoke-BridgeRequest "delete_object" @{ object = @{ instanceId = $instanceId } } "delete-stale-ref"
            Assert-Ok $deleted
        }
    }

    Invoke-TestCase "play_mode.stop" {
        $exchange = Invoke-BridgeRequest "play_scene" @{ stop = $true } "play-stop"
        Assert-Ok $exchange
        return $exchange
    }

    Invoke-TestCase "play_mode.wait_until_stopped" {
        $exchange = Wait-PlayModeState $false
        return $exchange
    }

    if ($null -ne $uxDataHashBefore) {
        Invoke-TestCase "extension.uxtool.user_data_unchanged" {
            Assert-True (Test-Path -LiteralPath $uxDataFullPath) "uxtool user data disappeared"
            $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $uxDataFullPath).Hash
            Assert-Equal $actual $uxDataHashBefore "uxtool changed persistent user data"
            return $null
        }
    }

    if ($Suite -eq "Full" -and @($script:DirtyScenesBeforePlay).Count -eq 0) {
        Invoke-TestCase "mutation.edit_mode.undo_redo_lifecycle" {
            $editName = "${fixtureName}_EditUndo"
            $created = Invoke-BridgeRequest "create_object" @{
                kind = "empty"
                name = $editName
                position = @{ x = 0; y = 0; z = 0 }
            } "edit-create"
            Assert-Ok $created
            Assert-Equal $created.Response.result.persistent $true "EditMode create_object must be persistent"
            $objectRef = [ordered]@{
                path = [string]$created.Response.result.object.path
                instanceId = [int]$created.Response.result.object.instanceId
                scenePath = [string]$created.Response.result.object.scenePath
            }
            $pathRef = [ordered]@{
                path = [string]$objectRef.path
                scenePath = [string]$objectRef.scenePath
            }
            $script:EditScenePath = [string]$objectRef.scenePath
            if (-not [string]::IsNullOrWhiteSpace($script:EditScenePath)) {
                $sceneFullPath = Join-Path $project $script:EditScenePath
                if (Test-Path -LiteralPath $sceneFullPath) {
                    $script:EditSceneHashBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $sceneFullPath).Hash
                }
            }

            try {
                $component = [ordered]@{
                    object = $objectRef
                    type = "UnityEngine.Transform"
                    index = 0
                    exactType = $true
                }
                $set = Invoke-BridgeRequest "set_property" @{
                    component = $component
                    propertyPath = "m_LocalPosition"
                    value = @{ x = 7.5; y = -2; z = 3 }
                } "edit-set"
                Assert-Ok $set
                Assert-Equal $set.Response.result.persistent $true "EditMode set_property must be persistent"
                Assert-Equal $set.Response.result.object.scenePath $script:EditScenePath "set_property did not return a round-trippable scenePath"

                $changed = Invoke-BridgeRequest "get_object" @{ object = $pathRef } "edit-get-changed"
                Assert-Ok $changed
                $transform = @($changed.Response.result.components | Where-Object type -eq "UnityEngine.Transform") | Select-Object -First 1
                Assert-Near ([double]$transform.properties.m_LocalPosition.x) 7.5 0.001 "EditMode property was not applied"

                $undoSet = Invoke-BridgeRequest "undo" @{} "edit-undo-set"
                Assert-Ok $undoSet
                Assert-Equal $undoSet.Response.result.eventObserved $true "undo callback was not observed"
                $restored = Invoke-BridgeRequest "get_object" @{ object = $pathRef } "edit-get-restored"
                Assert-Ok $restored
                $transform = @($restored.Response.result.components | Where-Object type -eq "UnityEngine.Transform") | Select-Object -First 1
                Assert-Near ([double]$transform.properties.m_LocalPosition.x) 0 0.001 "Undo did not restore EditMode property"

                $deleted = Invoke-BridgeRequest "delete_object" @{ object = $pathRef } "edit-delete"
                Assert-Ok $deleted
                Assert-Equal $deleted.Response.result.persistent $true "EditMode delete_object must be persistent"
                $missing = Invoke-BridgeRequest "get_object" @{ object = $pathRef } "edit-deleted-missing"
                Assert-Error $missing "OBJECT_NOT_FOUND"

                $undoDelete = Invoke-BridgeRequest "undo" @{} "edit-undo-delete"
                Assert-Ok $undoDelete
                $restoredObject = Invoke-BridgeRequest "get_object" @{ object = $pathRef } "edit-delete-restored"
                Assert-Ok $restoredObject

                $undoCreate = Invoke-BridgeRequest "undo" @{} "edit-undo-create"
                Assert-Ok $undoCreate
                $missingAfterUndo = Invoke-BridgeRequest "get_object" @{ object = $pathRef } "edit-create-undone"
                Assert-Error $missingAfterUndo "OBJECT_NOT_FOUND"

                $redoCreate = Invoke-BridgeRequest "redo" @{} "edit-redo-create"
                Assert-Ok $redoCreate
                Assert-Equal $redoCreate.Response.result.eventObserved $true "redo callback was not observed"
                $redoneObject = Invoke-BridgeRequest "get_object" @{ object = $pathRef } "edit-create-redone"
                Assert-Ok $redoneObject

                $finalUndo = Invoke-BridgeRequest "undo" @{} "edit-final-undo"
                Assert-Ok $finalUndo
                $finalMissing = Invoke-BridgeRequest "get_object" @{ object = $pathRef } "edit-final-missing"
                Assert-Error $finalMissing "OBJECT_NOT_FOUND"
            }
            finally {
                $probe = Invoke-BridgeRequest "get_object" @{ object = $pathRef } "edit-cleanup-probe"
                if ($probe.Response.status -eq "ok") {
                    $cleanup = Invoke-BridgeRequest "delete_object" @{ object = $pathRef } "edit-cleanup-delete"
                    Assert-Ok $cleanup
                }
                elseif ($probe.Response.error.code -ne "OBJECT_NOT_FOUND") {
                    throw "EditMode fixture cleanup probe failed: $($probe.Response.error.code)"
                }
            }
            return $created
        }
    }

    Invoke-TestCase "mutation.invoke_menu.window" {
        $exchange = Invoke-BridgeRequest "invoke_menu" @{ path = "Window/Agent Bridge" } "menu-window"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.executed $true "Agent Bridge window menu was not executed"
        return $exchange
    }

    Invoke-TestCase "assets.create_folder" {
        $exchange = Invoke-BridgeRequest "create_asset" @{ kind = "folder"; path = $script:AssetRoot } "create-folder"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.path $script:AssetRoot "created folder path mismatch"
        return $exchange
    }


    Invoke-TestCase "assets.create_folder.idempotent" {
        $exchange = Invoke-BridgeRequest "create_asset" @{ kind = "folder"; path = $script:AssetRoot } "create-folder-again"
        Assert-Ok $exchange
        return $exchange
    }

    Invoke-TestCase "assets.delete_folder.requires_permanent" {
        $exchange = Invoke-BridgeRequest "delete_asset" @{ path = $script:AssetRoot } "delete-folder-safe-default"
        Assert-Error $exchange "ASSET_DIRECTORY_DELETE_REQUIRES_PERMANENT"
        return $exchange
    }

    Invoke-TestCase "scenes.fixture.snapshot_user_setup" {
        $exchange = Invoke-BridgeRequest "list_scenes" @{} "scene-snapshot"
        Assert-Ok $exchange
        $active = @($exchange.Response.result.scenes | Where-Object active) | Select-Object -First 1
        if ($null -eq $active) { throw "no active scene is available for fixture isolation" }
        $script:OriginalActiveSceneSelector = if (-not [string]::IsNullOrEmpty([string]$active.path)) {
            @{ scenePath = [string]$active.path }
        } else {
            @{ sceneHandle = [int]$active.handle }
        }
        $script:UserDirtySceneCount = @($exchange.Response.result.scenes | Where-Object dirty).Count
        return $exchange
    }

    Invoke-TestCase "scenes.fixture.find_source" {
        $exchange = Invoke-BridgeRequest "list_assets" @{
            type = "SceneAsset"
            folder = "Assets"
            limit = 1000
        } "scene-source"
        Assert-Ok $exchange
        $candidate = @($exchange.Response.result.assets | Where-Object {
            -not ([string]$_.path).StartsWith("$($script:AssetRoot)/", [StringComparison]::Ordinal)
        }) | Select-Object -First 1
        if ($null -eq $candidate) { throw "project has no saved SceneAsset to copy as an isolated fixture" }
        $script:SceneSourcePath = [string]$candidate.path
        Assert-True (Test-Path -LiteralPath (Join-Path $project $script:SceneSourcePath)) "scene fixture source file is unavailable"
        return $exchange
    }

    Invoke-TestCase "scenes.fixture.import_primary" {
        if ([string]::IsNullOrWhiteSpace($script:SceneSourcePath)) { throw "scene fixture source was not resolved" }
        $exchange = Invoke-BridgeRequest "import_asset" @{
            source = (Join-Path $project $script:SceneSourcePath)
            destination = $script:SceneFixturePath
        } "scene-import-primary"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.path $script:SceneFixturePath "primary scene fixture path mismatch"
        return $exchange
    }

    Invoke-TestCase "scenes.fixture.import_secondary" {
        if ([string]::IsNullOrWhiteSpace($script:SceneSourcePath)) { throw "scene fixture source was not resolved" }
        $exchange = Invoke-BridgeRequest "import_asset" @{
            source = (Join-Path $project $script:SceneSourcePath)
            destination = $script:SceneFixtureSecondPath
        } "scene-import-secondary"
        Assert-Ok $exchange
        return $exchange
    }

    Invoke-TestCase "scenes.open_scene.additive" {
        $exchange = Invoke-BridgeRequest "open_scene" @{
            scenePath = $script:SceneFixturePath
            mode = "additive"
            setActive = $false
        } "scene-open-primary"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.opened $true "primary scene was not opened"
        Assert-Equal $exchange.Response.result.alreadyLoaded $false "new scene reported already loaded"
        return $exchange
    }

    Invoke-TestCase "scenes.open_scene.additive_idempotent" {
        $exchange = Invoke-BridgeRequest "open_scene" @{
            scenePath = $script:SceneFixturePath
            mode = "additive"
            setActive = $false
        } "scene-open-primary-again"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.opened $false "repeated additive open should be idempotent"
        Assert-Equal $exchange.Response.result.alreadyLoaded $true "repeated additive open did not report alreadyLoaded"
        return $exchange
    }

    Invoke-TestCase "scenes.open_scene.secondary" {
        $exchange = Invoke-BridgeRequest "open_scene" @{
            scenePath = $script:SceneFixtureSecondPath
            mode = "additive"
        } "scene-open-secondary"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.opened $true "secondary scene was not opened"
        return $exchange
    }

    Invoke-TestCase "scenes.set_active_scene.primary" {
        $exchange = Invoke-BridgeRequest "set_active_scene" @{ scenePath = $script:SceneFixturePath } "scene-active-primary"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.changed $true "primary scene should change active scene"
        Assert-Equal $exchange.Response.result.scene.path $script:SceneFixturePath "wrong scene became active"
        return $exchange
    }

    Invoke-TestCase "scenes.set_active_scene.idempotent" {
        $exchange = Invoke-BridgeRequest "set_active_scene" @{ scenePath = $script:SceneFixturePath } "scene-active-primary-again"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.changed $false "setting the same active scene should be idempotent"
        return $exchange
    }

    Invoke-TestCase "scenes.list_scenes.includes_fixtures" {
        $exchange = Invoke-BridgeRequest "list_scenes" @{} "scene-list-fixtures"
        Assert-Ok $exchange
        Assert-Equal @($exchange.Response.result.scenes | Where-Object path -eq $script:SceneFixturePath).Count 1 "primary scene missing from list_scenes"
        Assert-Equal @($exchange.Response.result.scenes | Where-Object path -eq $script:SceneFixtureSecondPath).Count 1 "secondary scene missing from list_scenes"
        return $exchange
    }

    Invoke-TestCase "mutation.edit_fixture.create_objects" {
        $root = Invoke-BridgeRequest "create_object" @{
            kind = "empty"; name = "${fixtureName}_EditRoot"; position = @{ x = 0; y = 0; z = 0 }
        } "edit-fixture-root"
        Assert-Ok $root
        Assert-Equal $root.Response.result.object.scenePath $script:SceneFixturePath "fixture root was created in a user scene"
        $script:EditFixtureRootRef = ConvertTo-StableObjectRef $root.Response.result.object

        $other = Invoke-BridgeRequest "create_object" @{
            kind = "empty"; name = "${fixtureName}_EditOther"
        } "edit-fixture-other"
        Assert-Ok $other
        $script:EditFixtureOtherRef = ConvertTo-StableObjectRef $other.Response.result.object

        $child = Invoke-BridgeRequest "create_object" @{
            kind = "empty"; name = "${fixtureName}_EditChild"; parent = $script:EditFixtureRootRef
        } "edit-fixture-child"
        Assert-Ok $child
        $script:EditFixtureChildRef = ConvertTo-StableObjectRef $child.Response.result.object
        return $root
    }

    Invoke-TestCase "mutation.add_remove_component.edit_mode_undo_redo" {
        if ($null -eq $script:EditFixtureRootRef) { throw "EditMode fixture root is unavailable" }
        $added = Invoke-BridgeRequest "add_component" @{
            object = $script:EditFixtureRootRef
            type = "AudioSource"
        } "edit-add-audio"
        Assert-Ok $added
        Assert-Equal $added.Response.result.persistent $true "EditMode add_component must be persistent"

        $removed = Invoke-BridgeRequest "remove_component" @{
            component = $added.Response.result.component
        } "edit-remove-audio"
        Assert-Ok $removed
        Assert-Equal $removed.Response.result.persistent $true "EditMode remove_component must be persistent"

        $undoRemove = Invoke-BridgeRequest "undo" @{} "edit-undo-remove-component"
        Assert-Ok $undoRemove
        Assert-Equal $undoRemove.Response.result.eventObserved $true "component Undo event was not observed"
        $restored = Invoke-BridgeRequest "get_object" @{
            object = $script:EditFixtureRootRef
            componentTypes = @("UnityEngine.AudioSource")
        } "edit-audio-restored"
        Assert-Ok $restored
        Assert-Equal @($restored.Response.result.components).Count 1 "Undo did not restore AudioSource"

        $redoRemove = Invoke-BridgeRequest "redo" @{} "edit-redo-remove-component"
        Assert-Ok $redoRemove
        Assert-Equal $redoRemove.Response.result.eventObserved $true "component Redo event was not observed"
        $removedAgain = Invoke-BridgeRequest "get_object" @{
            object = $script:EditFixtureRootRef
            componentTypes = @("UnityEngine.AudioSource")
        } "edit-audio-removed-again"
        Assert-Ok $removedAgain
        Assert-Equal @($removedAgain.Response.result.components).Count 0 "Redo did not remove AudioSource"
        return $removed
    }

    Invoke-TestCase "mutation.remove_component.reject_transform_edit_mode" {
        $exchange = Invoke-BridgeRequest "remove_component" @{
            component = @{
                object = $script:EditFixtureRootRef
                type = "UnityEngine.Transform"
                index = 0
            }
        } "edit-remove-transform"
        Assert-Error $exchange "COMPONENT_REMOVE_NOT_ALLOWED"
        return $exchange
    }

    Invoke-TestCase "mutation.update_object.edit_mode" {
        $exchange = Invoke-BridgeRequest "update_object" @{
            object = $script:EditFixtureChildRef
            name = "${fixtureName}_ReparentedChild"
            parent = $script:EditFixtureOtherRef
            siblingIndex = 0
            active = $false
            tag = "Untagged"
            layer = 0
            isStatic = $false
            position = @{ x = 2; y = 3; z = 4 }
            rotation = @{ x = 5; y = 10; z = 15 }
            scale = @{ x = 1.5; y = 1.25; z = 0.75 }
        } "edit-update-child"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.persistent $true "EditMode update_object must be persistent"
        Assert-Equal $exchange.Response.result.state.parent.instanceId $script:EditFixtureOtherRef.instanceId "child was not reparented"
        Assert-Equal $exchange.Response.result.state.active $false "updated active state mismatch"
        Assert-Near ([double]$exchange.Response.result.state.position.z) 4 0.001 "updated local position mismatch"
        $script:EditFixtureChildRef = ConvertTo-StableObjectRef $exchange.Response.result.object
        return $exchange
    }

    Invoke-TestCase "mutation.update_object.reject_cycle_and_sibling" {
        $cycle = Invoke-BridgeRequest "update_object" @{
            object = $script:EditFixtureOtherRef
            parent = $script:EditFixtureChildRef
        } "edit-update-cycle"
        Assert-Error $cycle "PARENT_CYCLE"

        $sibling = Invoke-BridgeRequest "update_object" @{
            object = $script:EditFixtureChildRef
            siblingIndex = 2147483647
        } "edit-update-sibling"
        Assert-Error $sibling "SIBLING_INDEX_OUT_OF_RANGE"
        return $cycle
    }

    Invoke-TestCase "mutation.update_object.cross_fixture_scenes" {
        $toSecond = Invoke-BridgeRequest "update_object" @{
            object = $script:EditFixtureOtherRef
            targetScenePath = $script:SceneFixtureSecondPath
        } "edit-move-second-scene"
        Assert-Ok $toSecond
        Assert-Equal $toSecond.Response.result.object.scenePath $script:SceneFixtureSecondPath "object did not move to secondary fixture scene"
        $script:EditFixtureOtherRef = ConvertTo-StableObjectRef $toSecond.Response.result.object

        $back = Invoke-BridgeRequest "update_object" @{
            object = $script:EditFixtureOtherRef
            targetScenePath = $script:SceneFixturePath
        } "edit-move-primary-scene"
        Assert-Ok $back
        Assert-Equal $back.Response.result.object.scenePath $script:SceneFixturePath "object did not return to primary fixture scene"
        $script:EditFixtureOtherRef = ConvertTo-StableObjectRef $back.Response.result.object
        return $back
    }

    Invoke-TestCase "scenes.save_scene.save_as_and_overwrite_guard" {
        $blocked = Invoke-BridgeRequest "save_scene" @{
            scenePath = $script:SceneFixtureSecondPath
            saveAs = $script:SceneFixturePath
        } "scene-save-as-existing"
        Assert-Error $blocked "ASSET_ALREADY_EXISTS"

        $savedAsPath = $script:SceneFixtureSecondSavedAsPath
        $previousPath = $script:SceneFixtureSecondPath
        $exchange = Invoke-BridgeRequest "save_scene" @{
            scenePath = $script:SceneFixtureSecondPath
            saveAs = $savedAsPath
        } "scene-save-as"
        Assert-Ok $exchange
        $script:SceneFixtureSecondPath = $savedAsPath
        Assert-Equal $exchange.Response.result.previousPath $previousPath "saveAs previous path mismatch"
        Assert-Equal $exchange.Response.result.scene.path $savedAsPath "saveAs did not update loaded scene path"
        return $exchange
    }

    Invoke-TestCase "mutation.batch.collapsed_undo_redo" {
        $exchange = Invoke-BridgeRequest "batch" @{
            steps = @(
                @{ command = "update_object"; params = @{ object = $script:EditFixtureRootRef; position = @{ x = 10; y = 0; z = 0 } } },
                @{ command = "update_object"; params = @{ object = $script:EditFixtureRootRef; position = @{ x = 20; y = 0; z = 0 } } }
            )
            collapseUndo = $true
        } "edit-batch-collapse"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.success $true "EditMode mutation batch failed"
        Assert-Equal $exchange.Response.result.collapsedUndo $true "eligible batch was not collapsed into one Undo step"

        $undoBatch = Invoke-BridgeRequest "undo" @{} "edit-batch-undo"
        Assert-Ok $undoBatch
        $afterUndo = Invoke-BridgeRequest "get_object" @{
            object = $script:EditFixtureRootRef
            componentTypes = @("UnityEngine.Transform")
        } "edit-batch-after-undo"
        Assert-Ok $afterUndo
        $transform = @($afterUndo.Response.result.components) | Select-Object -First 1
        Assert-Near ([double]$transform.properties.m_LocalPosition.x) 0 0.001 "one Undo did not revert the whole batch"

        $redoBatch = Invoke-BridgeRequest "redo" @{} "edit-batch-redo"
        Assert-Ok $redoBatch
        $afterRedo = Invoke-BridgeRequest "get_object" @{
            object = $script:EditFixtureRootRef
            componentTypes = @("UnityEngine.Transform")
        } "edit-batch-after-redo"
        Assert-Ok $afterRedo
        $transform = @($afterRedo.Response.result.components) | Select-Object -First 1
        Assert-Near ([double]$transform.properties.m_LocalPosition.x) 20 0.001 "Redo did not restore the collapsed batch"
        return $exchange
    }

    Invoke-TestCase "scenes.save_scene.primary" {
        $exchange = Invoke-BridgeRequest "save_scene" @{ scenePath = $script:SceneFixturePath } "scene-save-primary"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.saved $true "save_scene did not report success"
        Assert-Equal $exchange.Response.result.scene.path $script:SceneFixturePath "save_scene returned wrong scene"
        return $exchange
    }

    Invoke-TestCase "scenes.save_scene.clean_idempotent" {
        $exchange = Invoke-BridgeRequest "save_scene" @{ scenePath = $script:SceneFixturePath } "scene-save-primary-again"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.wasDirty $false "scene should be clean before repeated save"
        return $exchange
    }

    Invoke-TestCase "scenes.save_scene.reject_conflicting_all" {
        $exchange = Invoke-BridgeRequest "save_scene" @{
            all = $true
            scenePath = $script:SceneFixturePath
        } "scene-save-conflict"
        Assert-Error $exchange "INVALID_PARAMS"
        return $exchange
    }

    Invoke-TestCase "scenes.open_and_close.reject_dirty" {
        $dirty = Invoke-BridgeRequest "update_object" @{
            object = $script:EditFixtureRootRef
            active = $false
        } "scene-dirty-primary"
        Assert-Ok $dirty

        $open = Invoke-BridgeRequest "open_scene" @{
            scenePath = $script:SceneSourcePath
            mode = "single"
            ifUnsaved = "error"
        } "scene-single-dirty"
        Assert-Error $open "UNSAVED_SCENES"

        $close = Invoke-BridgeRequest "close_scene" @{
            scenePath = $script:SceneFixturePath
            ifUnsaved = "error"
        } "scene-close-dirty"
        Assert-Error $close "UNSAVED_SCENES"
        return $close
    }

    Invoke-TestCase "scenes.close_scene.secondary_discard" {
        $exchange = Invoke-BridgeRequest "close_scene" @{
            scenePath = $script:SceneFixtureSecondPath
            ifUnsaved = "discard"
        } "scene-close-secondary"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.closed $true "secondary fixture scene was not closed"
        return $exchange
    }

    Invoke-TestCase "scenes.close_scene.missing_handle" {
        $exchange = Invoke-BridgeRequest "close_scene" @{
            sceneHandle = 2147483647
            ifUnsaved = "discard"
        } "scene-close-missing"
        Assert-Error $exchange "SCENE_NOT_LOADED"
        return $exchange
    }

    Invoke-TestCase "prefab.fixture.create_source" {
        $root = Invoke-BridgeRequest "create_object" @{
            kind = "empty"
            name = "${fixtureName}_PrefabSource"
        } "prefab-source-root"
        Assert-Ok $root
        Assert-Equal $root.Response.result.object.scenePath $script:SceneFixturePath "Prefab source was created outside fixture scene"
        $script:PrefabSourceRef = ConvertTo-StableObjectRef $root.Response.result.object

        $child = Invoke-BridgeRequest "create_object" @{
            kind = "empty"
            name = "${fixtureName}_PrefabChild"
            parent = $script:PrefabSourceRef
        } "prefab-source-child"
        Assert-Ok $child
        return $root
    }

    Invoke-TestCase "prefab.status.plain_object_idempotent" {
        $first = Invoke-BridgeRequest "prefab" @{
            action = "status"
            object = $script:PrefabSourceRef
        } "prefab-status-plain"
        Assert-Ok $first
        Assert-Equal $first.Response.result.status.isInstance $false "plain fixture unexpectedly reported as Prefab instance"
        $second = Invoke-BridgeRequest "prefab" @{
            action = "status"
            object = $script:PrefabSourceRef
        } "prefab-status-plain-again"
        Assert-Ok $second
        Assert-Equal $second.Response.result.status.isInstance $false "repeated status changed plain object state"
        return $second
    }

    Invoke-TestCase "prefab.apply.reject_plain_object" {
        $exchange = Invoke-BridgeRequest "prefab" @{
            action = "apply"
            object = $script:PrefabSourceRef
        } "prefab-apply-plain"
        Assert-Error $exchange "NOT_PREFAB_INSTANCE"
        return $exchange
    }

    Invoke-TestCase "prefab.create" {
        $exchange = Invoke-BridgeRequest "prefab" @{
            action = "create"
            object = $script:PrefabSourceRef
            assetPath = $script:PrefabAssetPath
            connect = $false
        } "prefab-create"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.created $true "prefab create did not report a new asset"
        Assert-Equal $exchange.Response.result.connected $false "connect=false unexpectedly connected source"
        Assert-Equal $exchange.Response.result.undoable $false "automated Prefab create must not claim Undo support"
        Assert-Equal $exchange.Response.result.asset.path $script:PrefabAssetPath "created Prefab path mismatch"
        return $exchange
    }

    Invoke-TestCase "prefab.create.reject_duplicate" {
        $exchange = Invoke-BridgeRequest "prefab" @{
            action = "create"
            object = $script:PrefabSourceRef
            assetPath = $script:PrefabAssetPath
        } "prefab-create-duplicate"
        Assert-Error $exchange "ASSET_ALREADY_EXISTS"
        return $exchange
    }

    Invoke-TestCase "prefab.create.explicit_overwrite" {
        $exchange = Invoke-BridgeRequest "prefab" @{
            action = "create"
            object = $script:PrefabSourceRef
            assetPath = $script:PrefabAssetPath
            overwrite = $true
        } "prefab-create-overwrite"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.overwritten $true "Prefab overwrite was not reported"
        return $exchange
    }

    Invoke-TestCase "prefab.fixture.instantiate" {
        $exchange = Invoke-BridgeRequest "create_object" @{
            kind = "prefab"
            prefabPath = $script:PrefabAssetPath
            name = "${fixtureName}_PrefabInstance"
        } "prefab-instance"
        Assert-Ok $exchange
        $script:PrefabInstanceRef = ConvertTo-StableObjectRef $exchange.Response.result.object
        return $exchange
    }

    Invoke-TestCase "prefab.status.instance" {
        $exchange = Invoke-BridgeRequest "prefab" @{
            action = "status"
            object = $script:PrefabInstanceRef
        } "prefab-status-instance"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.status.isInstance $true "Prefab instance status was not detected"
        Assert-Equal $exchange.Response.result.status.assetPath $script:PrefabAssetPath "Prefab instance asset path mismatch"
        Assert-Equal $exchange.Response.result.status.isOutermostRoot $true "fixture instance should be the outermost root"
        return $exchange
    }

    Invoke-TestCase "prefab.create.reject_instance_child" {
        $hierarchy = Invoke-BridgeRequest "get_hierarchy" @{
            root = $script:PrefabInstanceRef
            maxDepth = 1
        } "prefab-instance-tree"
        Assert-Ok $hierarchy
        $child = @($hierarchy.Response.result.scenes[0].roots[0].children) | Select-Object -First 1
        if ($null -eq $child) { throw "Prefab fixture instance has no child" }
        $childRef = ConvertTo-StableObjectRef $child
        $exchange = Invoke-BridgeRequest "prefab" @{
            action = "create"
            object = $childRef
            assetPath = "$($script:AssetRoot)/invalid-child.prefab"
        } "prefab-create-child"
        Assert-Error $exchange "PREFAB_SOURCE_NOT_ROOT"
        return $exchange
    }

    Invoke-TestCase "prefab.apply_added_component_override" {
        $added = Invoke-BridgeRequest "add_component" @{
            object = $script:PrefabInstanceRef
            type = "UnityEngine.AudioSource"
        } "prefab-add-audio-override"
        Assert-Ok $added
        $before = Invoke-BridgeRequest "prefab" @{
            action = "status"
            object = $script:PrefabInstanceRef
        } "prefab-status-before-apply"
        Assert-Ok $before
        Assert-Equal $before.Response.result.status.hasOverrides $true "added component was not reported as a Prefab override"

        $exchange = Invoke-BridgeRequest "prefab" @{
            action = "apply"
            object = $script:PrefabInstanceRef
        } "prefab-apply"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.applied $true "Prefab apply did not report success"
        Assert-Equal $exchange.Response.result.undoable $false "automated Prefab apply must not claim Undo support"
        $script:PrefabInstanceRef = ConvertTo-StableObjectRef $exchange.Response.result.root
        return $exchange
    }

    Invoke-TestCase "prefab.revert_added_component_override" {
        $added = Invoke-BridgeRequest "add_component" @{
            object = $script:PrefabInstanceRef
            type = "UnityEngine.BoxCollider"
        } "prefab-add-collider-override"
        Assert-Ok $added

        $exchange = Invoke-BridgeRequest "prefab" @{
            action = "revert"
            object = $script:PrefabInstanceRef
        } "prefab-revert"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.reverted $true "Prefab revert did not report success"
        $script:PrefabInstanceRef = ConvertTo-StableObjectRef $exchange.Response.result.root

        $object = Invoke-BridgeRequest "get_object" @{
            object = $script:PrefabInstanceRef
            componentTypes = @("UnityEngine.BoxCollider")
        } "prefab-collider-after-revert"
        Assert-Ok $object
        Assert-Equal @($object.Response.result.components).Count 0 "Prefab revert did not remove added collider override"
        return $exchange
    }

    Invoke-TestCase "prefab.unpack_complete" {
        $exchange = Invoke-BridgeRequest "prefab" @{
            action = "unpack"
            object = $script:PrefabInstanceRef
            unpackMode = "complete"
        } "prefab-unpack"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.unpacked $true "Prefab unpack did not report success"
        Assert-Equal $exchange.Response.result.status.isInstance $false "unpacked object still reports as Prefab instance"
        $script:PrefabInstanceRef = ConvertTo-StableObjectRef $exchange.Response.result.object
        return $exchange
    }

    Invoke-TestCase "prefab.fixture.cleanup_scene_objects" {
        foreach ($reference in @($script:PrefabInstanceRef, $script:PrefabSourceRef)) {
            if ($null -eq $reference) { continue }
            $probe = Invoke-BridgeRequest "get_object" @{ object = $reference } "prefab-cleanup-probe"
            if ($probe.Response.status -eq "ok") {
                $deleted = Invoke-BridgeRequest "delete_object" @{ object = $reference } "prefab-cleanup-delete"
                Assert-Ok $deleted
            }
            elseif ($probe.Response.error.code -ne "OBJECT_NOT_FOUND") {
                throw "Prefab fixture cleanup probe failed: $($probe.Response.error.code)"
            }
        }
        return $null
    }

    # Restore the user's active scene and close every temporary scene before the
    # remaining asset-only fixtures run. This keeps later failures from leaving a
    # temporary scene loaded or selected.
    Invoke-TestCase "scenes.fixture.restore_user_active_scene" {
        if ($null -eq $script:OriginalActiveSceneSelector) { throw "original active scene selector is unavailable" }
        $exchange = Invoke-BridgeRequest "set_active_scene" $script:OriginalActiveSceneSelector "scene-restore-active"
        Assert-Ok $exchange
        return $exchange
    }

    Invoke-TestCase "scenes.fixture.close_remaining_scenes" {
        $listed = Invoke-BridgeRequest "list_scenes" @{} "scene-cleanup-list"
        Assert-Ok $listed
        foreach ($fixturePath in @(
            $script:SceneFixtureSecondSavedAsPath,
            $script:SceneFixtureSecondPath,
            $script:SceneFixturePath
        ) | Select-Object -Unique) {
            if (@($listed.Response.result.scenes | Where-Object path -eq $fixturePath).Count -eq 0) {
                continue
            }
            $closed = Invoke-BridgeRequest "close_scene" @{
                scenePath = $fixturePath
                ifUnsaved = "discard"
            } "scene-cleanup-close"
            Assert-Ok $closed
        }
        $verified = Invoke-BridgeRequest "list_scenes" @{} "scene-cleanup-verify"
        Assert-Ok $verified
        Assert-Equal @($verified.Response.result.scenes | Where-Object {
            -not [string]::IsNullOrEmpty([string]$_.path) -and
            ([string]$_.path).StartsWith("$($script:AssetRoot)/", [StringComparison]::Ordinal)
        }).Count 0 "fixture scene remained loaded after cleanup"
        return $verified
    }

    $textPath = "$($script:AssetRoot)/fixture.txt"
    $movedTextPath = "$($script:AssetRoot)/fixture-moved.txt"
    Invoke-TestCase "assets.create_text" {
        $exchange = Invoke-BridgeRequest "create_asset" @{ kind = "text"; path = $textPath; content = "agentbridge-before" } "create-text"
        Assert-Ok $exchange
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$exchange.Response.result.guid)) "created text guid missing"
        $script:TextAssetGuid = [string]$exchange.Response.result.guid
        return $exchange
    }

    Invoke-TestCase "assets.create_text.reject_duplicate" {
        $exchange = Invoke-BridgeRequest "create_asset" @{ kind = "text"; path = $textPath; content = "agentbridge-after" } "create-text-duplicate"
        Assert-Error $exchange "ASSET_ALREADY_EXISTS"
        return $exchange
    }


    Invoke-TestCase "assets.create_text.explicit_overwrite" {
        $exchange = Invoke-BridgeRequest "create_asset" @{
            kind = "text"; path = $textPath; content = "agentbridge-after"; overwrite = $true
        } "create-text-overwrite"
        Assert-Ok $exchange
        $fullPath = Join-Path $project $textPath
        Assert-Equal ([IO.File]::ReadAllText($fullPath)) "agentbridge-after" "text overwrite content mismatch"
        Assert-Equal ([string]$exchange.Response.result.guid) $script:TextAssetGuid "text overwrite changed GUID"
        return $exchange
    }

    Invoke-TestCase "inspection.get_asset.path_and_guid" {
        $byPath = Invoke-BridgeRequest "get_asset" @{
            path = $textPath
            includeProperties = $true
            subAssetLimit = 10
        } "get-text-asset"
        Assert-Ok $byPath
        Assert-Equal $byPath.Response.result.asset.path $textPath "get_asset returned wrong text path"
        Assert-Equal $byPath.Response.result.asset.folder $false "text asset reported as folder"
        Assert-True ($null -ne $byPath.Response.result.importer) "text asset importer missing"
        $script:TextAssetGuid = [string]$byPath.Response.result.asset.guid
        Assert-True (-not [string]::IsNullOrWhiteSpace($script:TextAssetGuid)) "text asset guid missing"

        $byGuid = Invoke-BridgeRequest "get_asset" @{ guid = $script:TextAssetGuid } "get-text-by-guid"
        Assert-Ok $byGuid
        Assert-Equal $byGuid.Response.result.asset.path $textPath "guid lookup returned wrong asset"
        Assert-Equal $byGuid.Response.result.asset.guid $script:TextAssetGuid "guid lookup changed guid"
        return $byGuid
    }

    Invoke-TestCase "inspection.get_asset.reject_stale_path_guid" {
        $folder = Invoke-BridgeRequest "get_asset" @{ path = $script:AssetRoot } "get-fixture-folder"
        Assert-Ok $folder
        $exchange = Invoke-BridgeRequest "get_asset" @{
            path = $textPath
            guid = [string]$folder.Response.result.asset.guid
        } "get-asset-stale"
        Assert-Error $exchange "ASSET_REF_STALE"
        return $exchange
    }

    Invoke-TestCase "inspection.get_asset_dependencies.direct_and_recursive" {
        $direct = Invoke-BridgeRequest "get_asset_dependencies" @{
            path = $textPath
            recursive = $false
            limit = 100
        } "text-dependencies-direct"
        Assert-Ok $direct
        Assert-Equal $direct.Response.result.source.path $textPath "direct dependency source mismatch"

        $recursive = Invoke-BridgeRequest "get_asset_dependencies" @{
            guid = $script:TextAssetGuid
            recursive = $true
            limit = 100
        } "text-dependencies-recursive"
        Assert-Ok $recursive
        Assert-Equal $recursive.Response.result.source.path $textPath "recursive dependency source mismatch"
        Assert-True ([int]$recursive.Response.result.total -ge @($recursive.Response.result.dependencies).Count) "dependency total is smaller than returned entries"
        return $recursive
    }

    Invoke-TestCase "assets.set_importer_property.write_without_reimport" {
        $exchange = Invoke-BridgeRequest "set_importer_property" @{
            path = $textPath
            propertyPath = "m_UserData"
            value = "agentbridge-$fixtureId"
            reimport = $false
        } "importer-user-data"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.path $textPath "importer mutation path mismatch"
        Assert-Equal $exchange.Response.result.changed $true "first importer mutation should change the fixture"
        Assert-Equal $exchange.Response.result.reimported $false "reimport=false unexpectedly reimported"
        return $exchange
    }

    Invoke-TestCase "assets.set_importer_property.idempotent" {
        $exchange = Invoke-BridgeRequest "set_importer_property" @{
            path = $textPath
            propertyPath = "m_UserData"
            value = "agentbridge-$fixtureId"
            reimport = $false
        } "importer-user-data-again"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.changed $false "reapplying importer value should be idempotent"
        Assert-Equal $exchange.Response.result.reimported $false "idempotent importer write should not reimport"
        return $exchange
    }

    Invoke-TestCase "assets.set_importer_property.save_and_reimport" {
        $exchange = Invoke-BridgeRequest "set_importer_property" @{
            path = $textPath
            propertyPath = "m_UserData"
            value = "agentbridge-reimport-$fixtureId"
            reimport = $true
        } "importer-reimport"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.changed $true "changed importer value was not applied"
        Assert-Equal $exchange.Response.result.reimported $true "SaveAndReimport was not reported"
        return $exchange
    }

    Invoke-TestCase "assets.set_importer_property.missing_property" {
        $exchange = Invoke-BridgeRequest "set_importer_property" @{
            path = $textPath
            propertyPath = "m_AgentBridgeDefinitelyMissing"
            value = 1
        } "importer-property-missing"
        Assert-Error $exchange "PROPERTY_NOT_FOUND"
        return $exchange
    }

    Invoke-TestCase "assets.list_fixture" {
        $exchange = Invoke-BridgeRequest "list_assets" @{ folder = $script:AssetRoot; limit = 20 } "list-fixture"
        Assert-Ok $exchange
        Assert-True (@($exchange.Response.result.assets | Where-Object path -eq $textPath).Count -eq 1) "created text was not listed"
        return $exchange
    }

    Invoke-TestCase "assets.move_text" {
        $exchange = Invoke-BridgeRequest "move_asset" @{ from = $textPath; to = $movedTextPath } "move-text"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.to $movedTextPath "move destination mismatch"
        return $exchange
    }

    Invoke-TestCase "assets.delete_text" {
        $exchange = Invoke-BridgeRequest "delete_asset" @{ path = $movedTextPath } "delete-text"
        Assert-Ok $exchange
        return $exchange
    }

    $doubleDotPath = "$($script:AssetRoot)/name..withdots.txt"
    Invoke-TestCase "assets.create_text.double_dot_name" {
        $exchange = Invoke-BridgeRequest "create_asset" @{ kind = "text"; path = $doubleDotPath; content = "valid filename" } "create-double-dot"
        Assert-Ok $exchange
        return $exchange
    }

    Invoke-TestCase "assets.delete_text.double_dot_name" {
        $exchange = Invoke-BridgeRequest "delete_asset" @{ path = $doubleDotPath } "delete-double-dot"
        Assert-Ok $exchange
        return $exchange
    }


    Invoke-TestCase "assets.reject_meta_path" {
        $metaPath = "$($script:AssetRoot)/blocked.meta"
        $exchange = Invoke-BridgeRequest "create_asset" @{ kind = "text"; path = $metaPath; content = "blocked" } "create-meta-bad"
        Assert-Error $exchange "INVALID_ASSET_PATH"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $project $metaPath))) "forbidden .meta file was created"
        return $exchange
    }

    Invoke-TestCase "assets.reject_parent_segment" {
        $escapePath = "$($script:AssetRoot)/../escaped.txt"
        $exchange = Invoke-BridgeRequest "create_asset" @{ kind = "text"; path = $escapePath; content = "blocked" } "create-parent-bad"
        Assert-Error $exchange "INVALID_ASSET_PATH"
        return $exchange
    }

    $scriptablePath = "$($script:AssetRoot)/panel-settings.asset"
    Invoke-TestCase "assets.create_scriptable_object" {
        $exchange = Invoke-BridgeRequest "create_asset" @{
            kind = "scriptableObject"
            path = $scriptablePath
            type = "VolumeProfile"
        } "create-scriptable"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.type "UnityEngine.Rendering.VolumeProfile" "ScriptableObject type mismatch"
        $script:ScriptableGuidBeforeOverwrite = [string]$exchange.Response.result.guid
        return $exchange
    }


    Invoke-TestCase "assets.create_scriptable_object.reject_duplicate" {
        $exchange = Invoke-BridgeRequest "create_asset" @{
            kind = "scriptableObject"
            path = $scriptablePath
            type = "UnityEngine.Rendering.VolumeProfile"
        } "create-scriptable-duplicate"
        Assert-Error $exchange "ASSET_ALREADY_EXISTS"
        return $exchange
    }

    Invoke-TestCase "assets.create_scriptable_object.explicit_overwrite" {
        $exchange = Invoke-BridgeRequest "create_asset" @{
            kind = "scriptableObject"
            path = $scriptablePath
            type = "UnityEngine.Rendering.VolumeProfile"
            overwrite = $true
        } "create-scriptable-overwrite"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.guid $script:ScriptableGuidBeforeOverwrite "ScriptableObject overwrite changed GUID"
        return $exchange
    }

    Invoke-TestCase "assets.delete_scriptable_object" {
        $exchange = Invoke-BridgeRequest "delete_asset" @{ path = $scriptablePath } "delete-scriptable"
        Assert-Ok $exchange
        return $exchange
    }

    $fixtureSourceDir = Join-Path $bridgeRoot "test-fixtures"
    $fixtureSource = Join-Path $fixtureSourceDir "$fixtureId.txt"
    $importPath = "$($script:AssetRoot)/imported.txt"

    Invoke-TestCase "assets.prepare_external_import_fixture" {
        [IO.Directory]::CreateDirectory($fixtureSourceDir) | Out-Null
        [IO.File]::WriteAllText($fixtureSource, "import-before", [Text.UTF8Encoding]::new($false))
        Assert-True (Test-Path -LiteralPath $fixtureSource) "external import fixture was not created"
        return $null
    }

    Invoke-TestCase "assets.import_text" {
        $exchange = Invoke-BridgeRequest "import_asset" @{ source = $fixtureSource; destination = $importPath } "import-text"
        Assert-Ok $exchange
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$exchange.Response.result.guid)) "imported asset guid missing"
        $script:ImportedAssetGuid = [string]$exchange.Response.result.guid
        return $exchange
    }

    Invoke-TestCase "assets.update_external_import_fixture" {
        [IO.File]::WriteAllText($fixtureSource, "import-after", [Text.UTF8Encoding]::new($false))
        Assert-Equal ([IO.File]::ReadAllText($fixtureSource)) "import-after" "external import fixture update mismatch"
        return $null
    }
    Invoke-TestCase "assets.import_text.reject_duplicate" {
        $exchange = Invoke-BridgeRequest "import_asset" @{ source = $fixtureSource; destination = $importPath } "import-duplicate"
        Assert-Error $exchange "ASSET_ALREADY_EXISTS"
        return $exchange
    }


    Invoke-TestCase "assets.import_text.explicit_overwrite" {
        $exchange = Invoke-BridgeRequest "import_asset" @{
            source = $fixtureSource; destination = $importPath; overwrite = $true
        } "import-overwrite"
        Assert-Ok $exchange
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $project $importPath))) "import-after" "import overwrite content mismatch"
        Assert-Equal ([string]$exchange.Response.result.guid) $script:ImportedAssetGuid "import overwrite changed GUID"
        return $exchange
    }

    Invoke-TestCase "assets.import_text.reject_same_file" {
        $fullImportPath = Join-Path $project $importPath
        $exchange = Invoke-BridgeRequest "import_asset" @{
            source = $fullImportPath; destination = $importPath; overwrite = $true
        } "import-same-file"
        Assert-Error $exchange "INVALID_PARAMS"
        return $exchange
    }

    Invoke-TestCase "assets.delete_imported_text" {
        $exchange = Invoke-BridgeRequest "delete_asset" @{ path = $importPath } "delete-import"
        Assert-Ok $exchange
        return $exchange
    }

    $codeBindSource = Join-Path $project "Assets/Res/UI/UIForm/Demo/UIHelp.prefab"
    $codeBindPath = "$($script:AssetRoot)/uihelp-codebind.prefab"
    if ($script:CommandMap.ContainsKey("codebind") -and (Test-Path -LiteralPath $codeBindSource)) {
        Invoke-TestCase "extension.codebind.import_fixture" {
            $exchange = Invoke-BridgeRequest "import_asset" @{
                source = $codeBindSource
                destination = $codeBindPath
            } "codebind-import"
            Assert-Ok $exchange
            return $exchange
        }

        Invoke-TestCase "extension.codebind.set_serialization" {
            $exchange = Invoke-BridgeRequest "codebind" @{
                action = "set_serialization"
                assetPath = $codeBindPath
            } "codebind-serialize"
            Assert-Ok $exchange
            Assert-Equal $exchange.Response.result.serializationDeferred $false "codebind serialization was deferred"
            Assert-True (@($exchange.Response.result.serialized) -contains "MonoUIFormHelp") "codebind did not serialize MonoUIFormHelp"
            return $exchange
        }

        Invoke-TestCase "extension.codebind.rename_node" {
            $exchange = Invoke-BridgeRequest "codebind" @{
                action = "rename_node"
                assetPath = $codeBindPath
                nodePath = "Desc_Text"
                bindName = "AuditDesc"
                arrayIndex = 2
                separator = "_"
            } "codebind-rename"
            Assert-Ok $exchange
            Assert-Equal $exchange.Response.result.oldName "Desc_Text" "codebind old node name mismatch"
            Assert-Equal $exchange.Response.result.newName "AuditDesc_* (2)" "codebind renamed node unexpectedly"
            Assert-Equal $exchange.Response.result.isArray $true "codebind array rename flag mismatch"
            return $exchange
        }
    }

    $stateSource = Join-Path $project "Assets/Res/UI/RuntimeInspector/RuntimeInspectorForm.prefab"
    $statePath = "$($script:AssetRoot)/runtime-inspector-state.prefab"
    $stateDataName = "ABAudit_$fixtureId"
    if ($script:CommandMap.ContainsKey("statecontroller") -and (Test-Path -LiteralPath $stateSource)) {
        Invoke-TestCase "extension.statecontroller.import_fixture" {
            $exchange = Invoke-BridgeRequest "import_asset" @{
                source = $stateSource
                destination = $statePath
            } "state-import"
            Assert-Ok $exchange
            return $exchange
        }

        Invoke-TestCase "extension.statecontroller.add_data" {
            $exchange = Invoke-BridgeRequest "statecontroller" @{
                action = "add_data"
                assetPath = $statePath
                controllerPath = "Root_StateControllerMono"
                dataName = $stateDataName
                states = "Idle,Active"
            } "state-add-data"
            Assert-Ok $exchange
            Assert-Equal (@($exchange.Response.result.states) -join ",") "Idle,Active" "statecontroller initial states mismatch"
            return $exchange
        }

        Invoke-TestCase "extension.statecontroller.list" {
            $exchange = Invoke-BridgeRequest "statecontroller" @{ action = "list"; assetPath = $statePath } "state-list"
            Assert-Ok $exchange
            $datas = @($exchange.Response.result.controllers | ForEach-Object { @($_.datas) })
            Assert-True (@($datas | Where-Object name -eq $stateDataName).Count -eq 1) "statecontroller data not listed"
            return $exchange
        }

        Invoke-TestCase "extension.statecontroller.set_state" {
            $exchange = Invoke-BridgeRequest "statecontroller" @{
                action = "set_state"
                assetPath = $statePath
                controllerPath = "Root_StateControllerMono"
                dataName = $stateDataName
                stateName = "Active"
            } "state-set"
            Assert-Ok $exchange
            Assert-Equal $exchange.Response.result.stateIndex 1 "statecontroller selected index mismatch"
            return $exchange
        }

        Invoke-TestCase "extension.statecontroller.add_state_node" {
            $exchange = Invoke-BridgeRequest "statecontroller" @{
                action = "add_state_node"
                assetPath = $statePath
                nodePath = "Root_StateControllerMono/Close_Button"
                stateType = "StateGameObjectForActive"
                dataName = $stateDataName
            } "state-add-node"
            Assert-Ok $exchange
            Assert-Equal $exchange.Response.result.boundField "m_DataName1" "statecontroller bound field mismatch"
            Assert-Equal (@($exchange.Response.result.stateValueSlots) -join ",") "Idle,Active" "statecontroller slots mismatch"
            return $exchange
        }

        Invoke-TestCase "extension.statecontroller.add_state" {
            $exchange = Invoke-BridgeRequest "statecontroller" @{
                action = "add_state"
                assetPath = $statePath
                controllerPath = "Root_StateControllerMono"
                dataName = $stateDataName
                stateName = "Disabled"
            } "state-add-state"
            Assert-Ok $exchange
            Assert-Equal (@($exchange.Response.result.states) -join ",") "Idle,Active,Disabled" "statecontroller expanded states mismatch"
            Assert-Equal ([int]$exchange.Response.result.alignedEffectNodes) 1 "statecontroller did not align effect node"
            return $exchange
        }

        Invoke-TestCase "extension.statecontroller.list_nodes" {
            $exchange = Invoke-BridgeRequest "statecontroller" @{
                action = "list_nodes"
                assetPath = $statePath
                dataName = $stateDataName
            } "state-list-nodes"
            Assert-Ok $exchange
            Assert-Equal ([int]$exchange.Response.result.count) 1 "statecontroller node count mismatch"
            $node = @($exchange.Response.result.nodes) | Select-Object -First 1
            Assert-Equal $node.mode "boolean" "statecontroller node mode mismatch"
            Assert-Equal @($node.bindings[0].stateValues).Count 3 "statecontroller node state slots were not aligned"
            return $exchange
        }
    }

    Invoke-TestCase "assets.cleanup_fixture_folder" {
        $scenes = Invoke-BridgeRequest "list_scenes" @{} "asset-cleanup-scene-guard"
        Assert-Ok $scenes
        Assert-Equal @($scenes.Response.result.scenes | Where-Object {
            -not [string]::IsNullOrEmpty([string]$_.path) -and
            ([string]$_.path).StartsWith("$($script:AssetRoot)/", [StringComparison]::Ordinal)
        }).Count 0 "refusing to delete AssetRoot while one of its scenes is still loaded"
        $exchange = Invoke-BridgeRequest "delete_asset" @{
            path = $script:AssetRoot
            permanent = $true
        } "delete-fixture-folder"
        Assert-Ok $exchange
        return $exchange
    }
    Invoke-TestCase "assets.cleanup_external_import_fixture" {
        if (Test-Path -LiteralPath $fixtureSource) {
            [IO.File]::Delete($fixtureSource)
        }
        Assert-True (-not (Test-Path -LiteralPath $fixtureSource)) "external import fixture cleanup failed"
        return $null
    }

    Invoke-TestCase "mutation.set_game_view_resolution" {
        $exchange = Invoke-BridgeRequest "set_game_view_resolution" @{
            width = 1234
            height = 567
            label = "AgentBridge Validation 1234x567"
        } "set-resolution"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.width 1234 "Game View width mismatch"
        Assert-Equal $exchange.Response.result.height 567 "Game View height mismatch"
        Assert-True ($null -ne $exchange.Response.result.restore) "Game View restore token missing"
        $script:GameViewRestore = $exchange.Response.result.restore
        return $exchange
    }

    $screenshotName = "agentbridge_validation_$fixtureId.png"
    Invoke-TestCase "capture.game_view" {
        $exchange = Invoke-BridgeRequest "capture_game_view" @{ fileName = $screenshotName } "capture"
        Assert-Ok $exchange
        $script:ScreenshotPath = [string]$exchange.Response.result.path
        Assert-True (Test-Path -LiteralPath $script:ScreenshotPath) "screenshot file was not created"
        $bytes = [IO.File]::ReadAllBytes($script:ScreenshotPath)
        Assert-True ($bytes.Length -gt 24) "screenshot is too small to be a PNG"
        $pngSignature = @(137, 80, 78, 71, 13, 10, 26, 10)
        for ($i = 0; $i -lt $pngSignature.Count; $i++) {
            Assert-Equal $bytes[$i] $pngSignature[$i] "invalid PNG signature byte $i"
        }
        return $exchange
    }

    Invoke-TestCase "capture.reject_duplicate" {
        $exchange = Invoke-BridgeRequest "capture_game_view" @{ fileName = $screenshotName } "capture-duplicate"
        Assert-Error $exchange "SCREENSHOT_ALREADY_EXISTS"
        return $exchange
    }

    Invoke-TestCase "capture.overwrite" {
        $exchange = Invoke-BridgeRequest "capture_game_view" @{ fileName = $screenshotName; overwrite = $true } "capture-overwrite"
        Assert-Ok $exchange
        Assert-True ([long]$exchange.Response.result.bytes -gt 0) "overwritten screenshot is empty"
        return $exchange
    }

    Invoke-TestCase "capture.cleanup" {
        $path = if ([string]::IsNullOrWhiteSpace($script:ScreenshotPath)) {
            Join-Path (Join-Path $bridgeRoot "screenshots") $screenshotName
        } else { $script:ScreenshotPath }
        if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
        Assert-True (-not (Test-Path -LiteralPath $path)) "screenshot cleanup failed"
        return $null
    }

    Invoke-TestCase "mutation.restore_game_view_resolution" {
        if ($null -eq $script:GameViewRestore) { throw "Game View restore token unavailable" }
        $exchange = Invoke-BridgeRequest "set_game_view_resolution" @{
            restore = $script:GameViewRestore
        } "restore-resolution"
        Assert-Ok $exchange
        Assert-Equal $exchange.Response.result.restored $true "Game View resolution was not restored"
        Assert-Equal ([int]$exchange.Response.result.selectedIndex) ([int]$script:GameViewRestore.selectedIndex) "Game View selected index was not restored"
        Assert-Equal ([bool]$exchange.Response.result.removedCreated) ([bool]$script:GameViewRestore.removeCreated) "temporary Game View preset cleanup mismatch"
        return $exchange
    }

    $sceneViewScreenshotName = "agentbridge_scene_view_$fixtureId.png"
    Invoke-TestCase "capture.scene_view" {
        $exchange = Invoke-BridgeRequest "capture_scene_view" @{
            fileName = $sceneViewScreenshotName
            width = 128
            height = 96
        } "capture-scene-view"
        Assert-Ok $exchange
        Assert-Equal ([int]$exchange.Response.result.width) 128 "SceneView capture width mismatch"
        Assert-Equal ([int]$exchange.Response.result.height) 96 "SceneView capture height mismatch"
        $script:SceneViewScreenshotPath = [string]$exchange.Response.result.path
        Assert-True (Test-Path -LiteralPath $script:SceneViewScreenshotPath) "SceneView screenshot file was not created"
        $bytes = [IO.File]::ReadAllBytes($script:SceneViewScreenshotPath)
        Assert-True ($bytes.Length -gt 24) "SceneView screenshot is too small to be a PNG"
        $pngSignature = @(137, 80, 78, 71, 13, 10, 26, 10)
        for ($i = 0; $i -lt $pngSignature.Count; $i++) {
            Assert-Equal $bytes[$i] $pngSignature[$i] "invalid SceneView PNG signature byte $i"
        }
        return $exchange
    }

    Invoke-TestCase "capture.scene_view.reject_duplicate" {
        $exchange = Invoke-BridgeRequest "capture_scene_view" @{
            fileName = $sceneViewScreenshotName
            width = 128
            height = 96
        } "capture-scene-view-duplicate"
        Assert-Error $exchange "SCREENSHOT_ALREADY_EXISTS"
        return $exchange
    }

    Invoke-TestCase "capture.scene_view.overwrite" {
        $exchange = Invoke-BridgeRequest "capture_scene_view" @{
            fileName = $sceneViewScreenshotName
            width = 64
            height = 64
            overwrite = $true
        } "capture-scene-view-overwrite"
        Assert-Ok $exchange
        Assert-Equal ([int]$exchange.Response.result.width) 64 "overwritten SceneView capture width mismatch"
        Assert-True ([long]$exchange.Response.result.bytes -gt 0) "overwritten SceneView screenshot is empty"
        return $exchange
    }

    Invoke-TestCase "capture.scene_view.reject_pixel_limit" {
        $exchange = Invoke-BridgeRequest "capture_scene_view" @{
            fileName = "agentbridge_scene_view_too_large_$fixtureId.png"
            width = 8192
            height = 8192
        } "capture-scene-view-large"
        Assert-Error $exchange "INVALID_PARAMS"
        return $exchange
    }

    Invoke-TestCase "capture.scene_view.cleanup" {
        $path = if ([string]::IsNullOrWhiteSpace($script:SceneViewScreenshotPath)) {
            Join-Path (Join-Path $bridgeRoot "screenshots") $sceneViewScreenshotName
        } else { $script:SceneViewScreenshotPath }
        if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
        Assert-True (-not (Test-Path -LiteralPath $path)) "SceneView screenshot cleanup failed"
        return $null
    }

    if ($Suite -eq "Full") {
        Invoke-TestCase "assets.refresh" {
            if (@($script:DirtyScenesBeforePlay).Count -gt 0) {
                throw "refresh skipped because scenes were already dirty: $($script:DirtyScenesBeforePlay -join ', ')"
            }
            $exchange = Invoke-BridgeRequest "refresh" @{} "refresh"
            Assert-Ok $exchange
            Assert-Equal $exchange.Response.result.saved $true "refresh did not report saved"
            Assert-Equal $exchange.Response.result.refreshed $true "refresh did not report refreshed"
            return $exchange
        }

        if ($null -ne $script:EditSceneHashBefore) {
            Invoke-TestCase "mutation.edit_mode.scene_file_unchanged" {
                $sceneFullPath = Join-Path $project $script:EditScenePath
                Assert-True (Test-Path -LiteralPath $sceneFullPath) "EditMode validation scene file disappeared"
                $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sceneFullPath).Hash
                Assert-Equal $actualHash $script:EditSceneHashBefore "EditMode Undo/Redo lifecycle changed the saved scene"
                return $null
            }
        }


        Invoke-TestCase "compilation.wait_after_refresh" {
            return Wait-CompilationIdle 90
        }

        $script:TestRunnerAssembly = "AgentBridge.Tests"
        $script:TestRunnerEditPassName = "AgentBridge.Tests.EditMode.TestCommandFixtures.PassCase"
        $script:TestRunnerEditFailName = "AgentBridge.Tests.EditMode.TestCommandFixtures.ExpectedFailureCase"
        $script:TestRunnerEditSlowName = "AgentBridge.Tests.EditMode.TestCommandFixtures.SlowCase"
        $script:TestRunnerSlowRunId = $null
        $script:TestRunnerPassRunId = $null
        $script:TestRunnerFailRunId = $null

        Invoke-TestCase "testing.run_tests.active_guard" {
            $started = Invoke-TestRunWhenIdle @{
                mode = "edit"
                testNames = @($script:TestRunnerEditSlowName)
                assemblyNames = @($script:TestRunnerAssembly)
            } "tests-run-slow"
            Assert-Ok $started
            $script:TestRunnerSlowRunId = [string]$started.Response.result.runId
            Assert-True (-not [string]::IsNullOrWhiteSpace($script:TestRunnerSlowRunId)) "slow test runId missing"

            $duplicate = Invoke-BridgeRequest "run_tests" @{
                mode = "edit"
                testNames = @($script:TestRunnerEditSlowName)
                assemblyNames = @($script:TestRunnerAssembly)
            } "tests-run-duplicate"
            Assert-Error $duplicate "TEST_RUN_ACTIVE"
            return $started
        }

        Invoke-TestCase "testing.get_test_result.slow_success" {
            $exchange = Wait-TestRunResult $script:TestRunnerSlowRunId 120
            Assert-Equal $exchange.Response.result.status "completed" "slow EditMode test did not complete"
            Assert-Equal $exchange.Response.result.success $true "slow EditMode test should pass"
            Assert-Equal ([int]$exchange.Response.result.summary.failed) 0 "slow EditMode test reported failures"
            return $exchange
        }

        Invoke-TestCase "testing.run_tests.edit_pass" {
            $exchange = Invoke-TestRunWhenIdle @{
                mode = "edit"
                testNames = @($script:TestRunnerEditPassName)
                assemblyNames = @($script:TestRunnerAssembly)
            } "tests-run-edit-pass"
            Assert-Ok $exchange
            $script:TestRunnerPassRunId = [string]$exchange.Response.result.runId
            return $exchange
        }

        Invoke-TestCase "testing.get_test_result.edit_pass" {
            $exchange = Wait-TestRunResult $script:TestRunnerPassRunId 120
            Assert-Equal $exchange.Response.result.status "completed" "passing EditMode test did not complete"
            Assert-Equal $exchange.Response.result.success $true "passing EditMode test reported failure"
            Assert-True ([int]$exchange.Response.result.summary.passed -ge 1) "passing EditMode result count missing"
            Assert-Equal ([int]$exchange.Response.result.summary.failed) 0 "passing EditMode test reported failures"
            return $exchange
        }

        Invoke-TestCase "testing.run_tests.edit_expected_failure" {
            $exchange = Invoke-TestRunWhenIdle @{
                mode = "edit"
                testNames = @($script:TestRunnerEditFailName)
                assemblyNames = @($script:TestRunnerAssembly)
            } "tests-run-edit-fail"
            Assert-Ok $exchange
            $script:TestRunnerFailRunId = [string]$exchange.Response.result.runId
            return $exchange
        }

        Invoke-TestCase "testing.get_test_result.edit_expected_failure" {
            $exchange = Wait-TestRunResult $script:TestRunnerFailRunId 120
            Assert-Equal $exchange.Response.result.status "completed" "failing EditMode test did not complete"
            Assert-Equal $exchange.Response.result.success $false "expected test failure was not reported"
            Assert-True ([int]$exchange.Response.result.summary.failed -ge 1) "failed test count missing"
            $failedDetail = @($exchange.Response.result.results | Where-Object status -eq "Failed") | Select-Object -First 1
            Assert-True ($null -ne $failedDetail) "failed test detail missing"
            Assert-True ([string]$failedDetail.message -like "*AgentBridge expected failure*") "failed test message mismatch"
            return $exchange
        }

        Invoke-TestCase "testing.get_test_result.explicit_old_does_not_replace_latest" {
            $old = Invoke-BridgeRequest "get_test_result" @{
                runId = $script:TestRunnerPassRunId
                includePassed = $true
            } "tests-get-old-explicit"
            Assert-Ok $old
            Assert-Equal $old.Response.result.runId $script:TestRunnerPassRunId "explicit old run lookup returned wrong run"

            $latest = Invoke-BridgeRequest "get_test_result" @{} "tests-get-latest-after-old"
            Assert-Ok $latest
            Assert-Equal $latest.Response.result.runId $script:TestRunnerFailRunId "explicit old lookup replaced latest test result"
            return $latest
        }

        Invoke-TestCase "compilation.recompile" {
            $exchange = Invoke-BridgeRequest "recompile" @{} "recompile"
            Assert-Ok $exchange
            Assert-Equal $exchange.Response.result.requested $true "recompile did not report requested"
            Assert-True ([int]$exchange.Response.result.generation -gt 0) "recompile generation missing"
            $script:RequestedCompileGeneration = [int]$exchange.Response.result.requestedGeneration
            return $exchange
        }


        Invoke-TestCase "compilation.recompile_result" {
            $deadline = [DateTime]::UtcNow.AddSeconds(90)
            do {
                $exchange = Invoke-BridgeRequest "get_compile_result" @{} "recompile-result"
                if ($exchange.Response.status -eq "error" -and $exchange.Response.error.code -eq "INTERRUPTED") {
                    Start-Sleep -Milliseconds 250
                    continue
                }
                Assert-Ok $exchange
                Assert-True ([int]$exchange.Response.result.generation -ge $script:RequestedCompileGeneration) "compile generation is older than requested generation"
                if (-not [bool]$exchange.Response.result.compiling) {
                    Assert-Equal ([int]$exchange.Response.result.errorCount) 0 "recompile produced errors"
                    Assert-Equal $exchange.Response.result.requestFailed $false "recompile request failed"
                    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$exchange.Response.result.compiledAt)) "compiledAt missing"
                    return $exchange
                }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $deadline)
            throw "recompile generation $script:RequestedCompileGeneration did not finish within 90s"
        }
    }
}

$allCommands = @($script:CommandMap.Keys | Sort-Object)
$exercised = @($script:ExercisedCommands | Sort-Object)
$notExercised = @($allCommands | Where-Object { -not $script:ExercisedCommands.Contains($_) })
$passedCount = @($script:Results | Where-Object passed).Count
$failedCount = $script:Results.Count - $passedCount

$report = [ordered]@{
    generatedAt = [DateTime]::UtcNow.ToString("o")
    projectPath = $project
    suite = $Suite
    commandsVersion = $script:CommandsVersion
    discoveredCommands = $allCommands
    exercisedCommands = $exercised
    notExercisedCommands = $notExercised
    passed = $passedCount
    failed = $failedCount
    cases = $script:Results
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportDir = Join-Path $bridgeRoot "test-results"
    [IO.Directory]::CreateDirectory($reportDir) | Out-Null
    $ReportPath = Join-Path $reportDir "agentbridge-$($Suite.ToLowerInvariant())-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).json"
}
else {
    $parent = Split-Path -Parent $ReportPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }
}

$reportJson = $report | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($ReportPath, $reportJson, [Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "AgentBridge suite=$Suite passed=$passedCount failed=$failedCount"
Write-Host "commands discovered=$($allCommands.Count) exercised=$($exercised.Count) notExercised=$($notExercised.Count)"
if ($notExercised.Count -gt 0) {
    Write-Host "not exercised: $($notExercised -join ', ')"
}
Write-Host "report: $ReportPath"

if ($failedCount -gt 0) {
    exit 1
}
