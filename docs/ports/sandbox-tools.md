# Port: sandbox tools (`text_editor`, `bash_session`, JSON-RPC over the injected `inspect-sandbox-tools` binary)

Reuses the published `inspect-sandbox-tools` binary exactly as Python does: the same S3 bucket, the same
version pin (v29) and vendored `SHA256SUMS`, the same install path in the container
(`/var/tmp/.da7be258e003d428/inspect-sandbox-tools`), and the same JSON-RPC 2.0 exchange over
`ISandboxEnvironment.ExecAsync([cli, "exec"], input: request)`. `SandboxTools.Bash` and `Python` are unchanged.

## What was ported

| Python (`src/inspect_ai/...`) | C# (`src/InspectAzureAI.Eval/...`) |
|---|---|
| `_util/_json_rpc.py` | `Tools/Support/JsonRpc.cs`: `JsonRpc` (request/response, `ExecScalarRequestAsync<T>`, `ExecModelRequestAsync<T>`, `ExecNotificationAsync`), `IJsonRpcTransport`, `JsonRpcCallOptions`, `JsonRpcErrorMapper`, `GenericJsonRpcErrorMapper`, `JsonRpcError` |
| `util/_sandbox/_json_rpc_transport.py` | `Tools/Support/SandboxJsonRpcTransport.cs` (exec transport, `INSPECT_SANDBOX_JSON_RPC_RESPONSE_MAX_BYTES`, chunked-response reassembly and release) |
| `util/_sandbox/_cli.py`; `util/_sandbox/context.py` (`sandbox_with_injection`, `sandbox_file_detector`, `_get_injection_target`); `tool/_sandbox_tools_utils/sandbox.py` (`sandbox_with_injected_tools`, `_inject_container_tools_code`, `_extract_tools_tree`, `SandboxInjectionError`) | `Tools/Support/SandboxToolSupport.cs`: `SandboxToolSupport`, `InjectedSandbox`, `SandboxInjectionException` |
| `tool/_sandbox_tools_utils/sandbox.py` (artifact resolution, S3 download), `_build_config.py`, `_digests.py`, `SHA256SUMS`, `sandbox_tools_version.txt` | `Tools/Support/SandboxToolsBinary.cs`: `SandboxToolsBinary : ISandboxToolsBinarySource`, `SandboxToolsArtifact`, `SandboxToolsBuildConfig` |
| `util/_sandbox/recon.py` | `Tools/Support/SandboxRecon.cs` (`DetectSandboxOsAsync`, `SandboxOsInfo`) |
| `tool/_sandbox_tools_utils/_error_mapper.py` | `Tools/Support/SandboxToolsErrorMapper.cs` |
| `tool/_tools/_text_editor.py` | `Tools/TextEditor.cs` (`TextEditor.Create(timeout, user, support)`) |
| `tool/_tools/_bash_session.py` | `Tools/BashSession.cs` (`BashSession.Create(timeout, waitForOutput, user, instance, support)`) |
| argument validation done by Python's signature check | `Tools/Support/ToolArguments.cs` (internal typed readers; wrong JSON types are `ToolParsingError`) |

Request/response shapes were checked against `src/inspect_sandbox_tools/.../_text_editor/tool_types.py`,
`_bash_session/tool_types.py` and `_version/json_rpc_methods.py`. `FakeSandboxEnvironment` gained one additive
member, `OnExecCall`, so tests can script by the whole call (stdin included).

## Behaviour kept from Python

- Requests are `json.dumps`-formatted (`PythonJson.Dumps`), ids count from 666, `params` is omitted when empty and
  null members are stripped recursively; `rpc_call_description` messages are byte-for-byte Python's.
- Error codes: -32099 (`ToolException` in the container) is a `ToolError`, -32098 (unexpected exception) and other
  server errors are `InvalidOperationException` (Python `RuntimeError`, fatal to the sample), -32602 is a
  `ToolParsingError`, -32603 a `ToolError`; -32600/-32601/-32700 are a coding error with Python's message.
- Injection order: detector (`test -r`, falling back to reading the file), recon (`uname -s`, `uname -m`, musl
  probe, `/etc/os-release`), `mkdir -p` as root (any failure or exception selects the rootless install), stage
  the gzipped onedir tar with `WriteFileAsync` and `tar xzf` (falling back to an uncompressed tar for a `tar`
  without gzip), `chmod 700` when root, `start-server` as the tools user. The detector runs once while choosing
  the target and again under the per-sandbox inject lock, as in `sandbox_with_injection`.
- `text_editor` parameters travel in the order of Python's `locals()` sweep, `insert_text` is rewired to `new_str`
  for `insert`, and `user` goes through the reserved `_run_as_user` parameter; the default timeout is 180 s.
- `bash_session`: session name in the sample store under `BashSessionStore[:{instance}]:session_id` (and
  `:instance`, materialised like `StoreModel`), `bash_session_new_session` with a 180 s transport timeout and
  Python's "Timed out creating new session" error, per-action parameters (`wait_for_output`, `idle_timeout` 0.5,
  `max_output_bytes` = `SandboxLimits.MaxExecOutputSize`, `input` with `\n` for `type_submit` and `\u0003` (ETX) for
  `interrupt`, `restart: true`), `timeout` defaulting to `wait_for_output + 180` and rejected when smaller.
- Schemas and descriptions are byte-for-byte what `ToolDef(text_editor()).parameters` / `.description` dump in the
  venv (fixtures under `tests/InspectAzureAI.Eval.Tests/fixtures/`).

## Deviations

1. **Version verification.** Python's `sandbox_with_injection` only re-runs the file detector after injecting;
   the brief asks for a version check, so after a fresh injection `SandboxToolSupport` also sends the `version`
   RPC (60 s timeout, the same request the launcher's own healthcheck uses) and fails with
   `SandboxInjectionException` when the launcher does not answer or reports an empty version. The reported
   package version is exposed as `InjectedVersion(sandbox)`.
2. **Artifact source.** No binaries are bundled: every artifact is downloaded from the same bucket and cached in
   `INSPECT_SANDBOX_TOOLS_BINARIES_DIR` or `~/.cache/inspect-azureai/sandbox-tools` (a pre-placed file is served
   without network). Digest verification is always strict (Python only warns unless
   `INSPECT_SANDBOX_TOOLS_STRICT_DIGESTS` is set): a mismatch is a `PrerequisiteError`, a missing `SHA256SUMS`
   entry an `InvalidOperationException`, and unverified bytes are never injected. The local PyInstaller build
   fallback, the interactive prompt, `-dev` artifacts and install-state detection are not ported; a 403/404 is a
   `PrerequisiteError` naming the build command.
3. `ISandboxEnvironment.ExecAsync` has no `timeout_retry` / `concurrency` arguments, so the transport cannot pass
   them; the chunk-release call still uses the 5 s budget.
4. `bash_session(waitForOutput)` is a `TimeSpan` validated to be a positive whole number of seconds (Python takes
   an `int`); the JSON still carries an integer. Construction errors are `ArgumentException` (Python `ValueError`).
5. `text_editor(timeout)` treats `TimeSpan.Zero` as unset (Python's `timeout or 180`).
6. Exception types: `RuntimeError` is `InvalidOperationException`, a result of the wrong JSON type is
   `InvalidOperationException` with Python's `Expected <class 'str'> result, got <class 'int'>` text,
   `NotImplementedError` is `PlatformNotSupportedException`, and the no-sandbox `ProcessLookupError` is an
   `InvalidOperationException` with Python's message. Logging goes through `ProviderLogger`.
7. The end-to-end Docker test is gated by `SandboxToolsDockerFact`: Docker must answer and the host-architecture
   artifact must be cached or `INSPECT_SWE_NETWORK_TESTS=1` set, since injecting requires the ~15 MB download
   (the download itself is covered by a `NetworkFact`).
8. `bash_session(action="type_submit")` without `input` sends `"\n"` (just the return key). Python builds
   `f"{input}\n"` from `input=None` and so types the literal text `None` before the return; that is an artefact of
   the f-string rather than intended behaviour (the schema documents `input` as optional for `type_submit`), so it
   is not mirrored. Transcripts of the two runtimes differ on that action's shell input.

## Not ported

The legacy `inspect-tool-support` image path (`_legacy_helpers.py`, web browser), `exec_remote` and MCP over the
sandbox tools server, `build_within_container.py` and friends, and Python's per-file `environments_with` cache
of `sandbox_with()` (which `sandbox_with_injection` does not use either).

## Tests

`tests/InspectAzureAI.Eval.Tests/SandboxToolSupportTests.cs`: `JsonRpcTests`, `SandboxJsonRpcTransportTests`
(port of `tests/util/sandbox/test_json_rpc_transport.py`), `SandboxToolSupportInjectionTests` (root, rootless,
gzip-less tar, musl, recon, wrapped failures, cancellation, target selection), `SandboxToolsBinaryTests` (fake
bucket: verified download, mismatch, corrupt cache, 404, tar cache; `[NetworkFact]` real download against the
pinned digest), `TextEditorToolTests` and `BashSessionToolTests` (scripted JSON-RPC exchanges: success, RPC
errors, timeouts, argument errors, store keys, instances), `SandboxToolsDockerTests` (real container: view,
create, str_replace, undo_edit, missing file, bash session with restart).
