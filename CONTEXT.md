# Unity Agent Bridge

Unity Agent Bridge exposes Unity Editor work to an external agent through a single-flight file exchange. This context names the protocol concepts shared by the channel, command, scene, and testing modules.

## Language

**Bridge root**:
The `.agentbridge/` directory inside a Unity project. Each Exchange moves through the fixed transient slots `request.json`, `processing.json`, and `response.json`.
_Avoid_: IPC folder, mailbox

**Exchange**:
One request claimed by Unity and its matching terminal response. Exchanges are single-flight.
Exactly one Exchange may be active. The Agent must read the complete `response.json`, wait for Unity to remove `processing.json`, delete the response as an explicit acknowledgement, and only then publish the next request. The response remains present until Claim cleanup completes, closing the reload ambiguity between response publication and processing cleanup.
_Avoid_: call, message pair, transaction

**Claim**:
A request atomically moved from `request.json` to `processing.json`, giving Unity exclusive responsibility for producing its response.
_Avoid_: lock, dequeue

**Command**:
A named Unity Editor operation discovered through `ICommandHandler` and described at runtime by `list_commands`.
_Avoid_: endpoint, action

**Command set**:
The currently registered and enabled commands plus their schemas and batch policies. Its `commandsVersion` changes whenever that visible content changes.
_Avoid_: command catalog, command list cache

**Batch**:
One command containing up to 50 prevalidated child commands that execute sequentially. A Batch is deliberately non-atomic even when its Undo operations are collapsed.
_Avoid_: transaction, bulk request

**Object reference**:
A round-trippable scene object identity combining canonical path with optional instance and scene hints.
_Avoid_: object selector, GameObject id

**Component reference**:
A round-trippable component identity combining an Object reference, runtime type, and type-relative index.
_Avoid_: component selector, component id

**Unsaved-scene policy**:
The explicit `error`, `save`, or context-permitted `discard` decision applied before an operation changes or depends on loaded scenes.
_Avoid_: dirty-scene mode, save prompt policy
