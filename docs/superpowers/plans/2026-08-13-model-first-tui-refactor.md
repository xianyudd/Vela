# Model-First Terminal.Gui TUI Refactor Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild Vela's interactive TUI as a model-first Terminal.Gui adapter with immutable session state, typed commands, pure reducer, explicit effects, and safety-first worker/DiskPart hardening.

**Architecture:** Terminal.Gui remains the only interactive renderer. A new platform-neutral `Vela.Application` project owns the TUI/application model, reducer, effect contracts, profile state service, and display-safe projections; `Vela.Tui` becomes composition root plus Terminal.Gui/StaticFrame adapters; `Vela.Windows` keeps Windows-native implementations. Security fixes land first: worker administrator verification before request claim, then DiskPart privileged script workspace.

**Tech Stack:** C# / .NET 10, Terminal.Gui, Spectre.Console for redirected/static startup frames only, xUnit, coverlet, Windows ACL/IL native APIs for privileged DiskPart workspace.

**Decision override:** The user selected option 3 (model-first Terminal.Gui) on 2026-08-13. This plan supersedes the implementation choice in section 4.4 of `docs/superpowers/specs/2026-08-13-architecture-remediation-design.md`; that document's security, product, and interaction invariants remain source constraints.

---

## Recommendation: add `Vela.Application`

**Recommendation:** Add `src/Vela.Application/Vela.Application.csproj`.

**Reason:** Model-first TUI needs a compile-time boundary where `TuiSessionState`, `TuiCommand`, `TuiReducer`, and `TuiEffect` stay free of Terminal.Gui, Spectre.Console, Windows registry, DiskPart, file-system ACL APIs, and process launching. Placing these types in `Vela.Core` would mix UI/application orchestration into the domain workflow layer; leaving them inside `Vela.Tui` would make the Terminal.Gui adapter boundary convention-based instead of enforced by references. `Vela.Application` depends only on `Vela.Core`; `Vela.Tui` and `Vela.Windows` may reference `Vela.Application` to compose adapters and implement ports.

Target references after migration:

```text
Vela.Core                         # platform-neutral contracts, models, workflows
    ▲
    │
Vela.Application                  # model-first app/TUI state, reducers, effect ports
    ▲              ▲
    │              │
Vela.Windows ──────┘              # Windows adapters, config/journal stores, DiskPart/UAC
    ▲
    │
Vela.Tui                          # Program, Terminal.Gui adapter, StaticFrameRenderer
```

`Vela.Windows -> Vela.Application` is limited to implementing application ports such as profile state storage. `Vela.Application` must not reference `Vela.Windows`, `Vela.Tui`, `Terminal.Gui`, or `Spectre.Console`.

---

## Current-state findings to preserve or fix

- `src/Vela.Tui/Program.cs` is a large composition root plus workflow controller (~684 lines). It wires preflight, impact estimation, logs, UAC launch, journal polling, and shell events directly.
- `src/Vela.Tui/Views/VelaTerminalShell.cs` is the production Terminal.Gui path and currently owns navigation, target lock, preflight detail, impact preview, confirmation, running/result state, log archive, and display formatting (~2555 lines).
- `src/Vela.Tui/Application/TuiApplication.cs` is an older Spectre-style interactive loop with profile/recent-run page models; it is tested but not used by production startup.
- `src/Vela.Tui/Rendering/FrameRenderer.cs` is still used for redirected/startup static frames and also supports old interactive Spectre frame rendering.
- `src/Vela.Tui/ProgramModes/WorkerMode.cs` currently opens journal and claims the pending request before administrator verification.
- `src/Vela.Windows/DiskPart/DiskPartClient.cs` currently writes scripts to `%TEMP%\Vela` and then launches `diskpart /s <script>`, leaving a privileged boundary TOCTOU window.
- Display-facing records still carry raw paths (`PreflightEvidenceViewModel.FilePath`, `DashboardViewModel.ConfiguredVhdxPath`, `RunProgressViewModel.VhdxPath`, `RunHistoryEntry.VhdxPath`) and shell reads paths for rendering.
- Running targets are visible as attention states in target rows/details; model-first projection must express this as stop impact, not as a preflight blocker.

---

## Multi-agent delivery map

Use fewer, focused agents if the harness supports explicit delegation. If implementing in a single agent, follow the same checkpoints manually.

### Main agent owns locally

- Project graph decisions and `.sln` / `.csproj` edits.
- Security-sensitive code review for worker ordering and DiskPart workspace.
- Final integration, coverage gate, architecture tests, and deletion of old paths.
- Any migration that touches both `Program.cs` and `VelaTerminalShell.cs` in the same commit.

### Safe parallel delegation after Chunk 2 lands

- **Application model agent:** `Vela.Application` reducer/effect model and reducer tests. Depends on Chunks 1-2.
- **Terminal.Gui adapter agent:** view extraction and binder tests. Depends on reducer contracts from Chunk 3.
- **Static renderer agent:** `StaticFrameRenderer` rename/static-only cleanup. Depends on display-safe records from Chunk 4.
- **Coverage/docs agent:** coverage script/CI and XML docs gate for new public APIs. Depends on project graph from Chunk 3.

### Required review checkpoints

- **Architecture review checkpoint A:** after Chunk 2, before adding `Vela.Application`, verify security hotfixes are isolated and green.
- **Architecture review checkpoint B:** after Chunk 3, verify dependencies enforce model-first boundaries.
- **Architecture review checkpoint C:** after Chunk 6, verify Terminal.Gui is an adapter and no workflow state remains in the shell.
- **Reviewer checkpoint before completion:** after Chunk 10, run reviewer/security checklist against full diff before final commit.

---

## Baseline commands

Use Windows PowerShell from repository root for full validation:

```powershell
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Release --no-restore --nologo
dotnet test .\Vela.sln -c Release --no-build --no-restore --nologo --logger "trx;LogFileName=all-tests.trx" --results-directory .\artifacts\test-results
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-build --no-restore --nologo -p:CollectCoverage=true -p:CoverletOutput=.\..\..\artifacts\coverage\coverage -p:CoverletOutputFormat=cobertura -p:Include="[Vela.Core]*%2C[Vela.Windows]*" -p:ExcludeByFile="**/Program.cs"
pwsh -NoProfile -File .\scripts\Verify-Coverage.ps1
```

Use focused test filters inside tasks; run the full baseline at each chunk boundary.

The baseline coverage command intentionally reflects the current two-package gate. Chunk 10 expands it to `Vela.Application` and `Vela.Tui`. Add complete XML documentation whenever a public API is introduced so warnings-as-errors stays green at every intermediate commit; Task 10 audits the result rather than deferring documentation.

---

## Chunk 1: Worker administrator-before-claim hotfix

**Goal:** Non-elevated worker invocations validate arguments/paths and administrator status before opening journal or claiming/moving pending requests. Existing terminal result and exit code semantics stay unchanged.

**Dependencies:** none.

**Rollback point:** current `main`/feature branch HEAD before first security commit.

### Task 1.1: Add RED tests for non-admin and admin-probe failure sequencing

**Files:**
- Modify: `tests/Vela.Tests/Tui/WorkerModeHardeningTests.cs`
- Modify: `tests/Vela.Tests/Tui/WorkerModeTests.cs` if existing parser/order tests fit better.

- [ ] Add test: `RunAsync_WhenNotAdministrator_DoesNotOpenJournalClaimConsumeOrAppend`.
  - Arrange trusted `AppPaths`, a valid run id, a `RecordingStore`, and a `RecordingJournal` whose operation list records `open`, `claim`, `consume`, `append`, and `summary`.
  - Use `new FixedAdministratorProbe(false)`.
  - Assert:
    - `TerminalResult.ValidationFailed`.
    - `ExitCode == 2`.
    - `store.ClaimedRunIds`, `store.ConsumedRunIds`, and `journal.Operations` are empty.
- [ ] Add test: `RunAsync_WhenAdministratorProbeThrows_DoesNotOpenJournalOrClaim`.
  - Update the current `RunAsync_WhenAdministratorProbeThrows_MapsToWorkerInterrupted` expectation.
  - Assert `TerminalResult.WorkerInterrupted`, `ExitCode == 10`, no journal writes, no claim/consume.
- [ ] Add test: `RunAsync_WhenArgumentsInvalid_DoesNotProbeAdministrator`.
  - Add a `RecordingAdministratorProbe` counter.
  - Assert parse failure returns validation failed without probing.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WorkerModeHardeningTests|FullyQualifiedName~WorkerModeTests" --nologo
```

Expected RED: at least the non-admin/admin-probe sequencing assertions fail because current `WorkerMode` opens journal and claims before probing.

### Task 1.2: Reorder `WorkerMode.RunAsync`

**Files:**
- Modify: `src/Vela.Tui/ProgramModes/WorkerMode.cs`

- [ ] Change the order to:

```text
parse --worker --run-id
  -> validate AppPaths trusted root/pending/logs/run directory
  -> administrator probe
  -> open existing journal
  -> claim pending request
  -> validate request/source path
  -> fresh Lxss strict match
  -> execute workflow
  -> append canonical terminal event / summary
  -> consume request
```

- [ ] Add a private `TryVerifyAdministrator` helper only if it reduces nesting; keep functions under 50 lines where practical.
- [ ] Ensure non-admin returns `CreateResult(TerminalResult.ValidationFailed)` directly.
- [ ] Ensure admin probe exception returns `CreateResult(TerminalResult.WorkerInterrupted)` directly.
- [ ] Preserve all existing post-claim failure behavior once a trusted request exists.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WorkerModeHardeningTests|FullyQualifiedName~WorkerModeTests" --nologo
```

Expected GREEN: focused worker tests pass.

### Task 1.3: Validate exit codes and journal schema unchanged

**Files:**
- Modify: `tests/Vela.Tests/Tui/WorkerModeHardeningTests.cs`

- [ ] Add assertion to an existing success/failure test that canonical `WorkerCompleted` / `WorkerFailed` event fields remain:
  - `RunId` equals CLI run id.
  - `TerminalResult` is populated only on terminal event.
  - `ExitCode == WorkerExitCodes.FromTerminalResult(...)`.
  - No new JSON fields or contract changes are required.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WorkerModeHardeningTests" --nologo
```

Expected GREEN.

### Task 1.4: Commit

```bash
git add src/Vela.Tui/ProgramModes/WorkerMode.cs tests/Vela.Tests/Tui/WorkerModeHardeningTests.cs tests/Vela.Tests/Tui/WorkerModeTests.cs
git commit -m "fix: verify worker elevation before claiming requests"
```

---

## Chunk 2: Privileged DiskPart workspace hotfix

**Goal:** DiskPart scripts are created, verified, pinned, and cleaned under `%ProgramData%\Vela\Privileged\DiskPart\<operation-nonce>\<script-nonce>.txt` with protected owner/DACL/high integrity, eliminating the `%TEMP%` replacement window.

**Dependencies:** Chunk 1 complete.

**Rollback point:** commit `fix: verify worker elevation before claiming requests`.

### Architecture review checkpoint A

Before edits:

- [ ] Confirm `IDiskPartClient` remains in `Vela.Core.Contracts`; adding the trusted `Guid runId` parameter does not introduce Windows-specific types.
- [ ] Confirm new privileged ACL/reparse/file identity code lives only in `Vela.Windows`.
- [ ] Confirm `CompactionWorkflow` can continue mapping DiskPart exceptions/failures to existing `TerminalResult.DiskPartPreflightFailed` / `DiskPartCompactFailed`.
- [ ] Confirm no operation request or journal schema changes.

### Task 2.1: Add workspace contract and fakeable lease

**Files:**
- Modify: `src/Vela.Core/Contracts/IDiskPartClient.cs`
- Modify: `src/Vela.Core/Workflows/CompactionWorkflow.cs`
- Create: `src/Vela.Windows/DiskPart/IPrivilegedDiskPartWorkspace.cs`
- Create: `src/Vela.Windows/DiskPart/DiskPartScriptLease.cs`
- Modify: `src/Vela.Windows/DiskPart/DiskPartClient.cs`
- Create: `tests/Vela.Tests/Windows/DiskPartClientTests.cs` (move client tests out of `DiskPartScriptBuilderTests.cs` or keep and rename later).

Contract sketch:

```csharp
namespace Vela.Windows.DiskPart;

public interface IPrivilegedDiskPartWorkspace
{
    Task<IPrivilegedDiskPartScriptLease> CreateScriptAsync(
        Guid runId,
        string script,
        CancellationToken cancellationToken);
}

public interface IPrivilegedDiskPartScriptLease : IAsyncDisposable
{
    string ScriptPath { get; }
    ValueTask VerifyAsync(CancellationToken cancellationToken);
}
```

- [ ] Implement `DiskPartScriptLease` with a private pinned `SafeFileHandle`; expose only `ScriptPath` through `IPrivilegedDiskPartScriptLease`.
- [ ] RED test: `DiskPartClient_KeepsScriptLeaseOpenWhileProcessRuns` with a recording fake lease and fake runner asserting its `Disposed` flag is false during invocation.
- [ ] RED test: `DiskPartClient_DisposesLeaseAfterRunnerCompletes`.
- [ ] RED test: `DiskPartClient_VerifiesLeaseBeforeLaunchAndAfterProcessExit`; if the pre-launch verification fails, the process runner call count stays zero.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DiskPartClientTests" --nologo
```

Expected RED: missing types/constructor.

- [ ] Add a non-empty trusted `runId` parameter to both `IDiskPartClient` operations and pass `OperationRequest.RunId` from `CompactionWorkflow`.
- [ ] Update `DiskPartClient` constructor to accept `IPrivilegedDiskPartWorkspace` instead of raw temp directory.
- [ ] Keep a test-only constructor overload if useful:

```csharp
public DiskPartClient(
    IProcessRunner processRunner,
    NativeToolPaths nativeToolPaths,
    DiskPartScriptBuilder scriptBuilder,
    IPrivilegedDiskPartWorkspace workspace)
```

- [ ] Use `await using var lease = await _workspace.CreateScriptAsync(runId, script, cancellationToken)`, call `VerifyAsync` immediately before launch, invoke `diskpart.exe /s lease.ScriptPath` while the lease is alive, then verify once more after process exit before returning the result.

Run focused tests. Expected GREEN for lease lifecycle.

### Task 2.2: Implement Windows privileged security helpers

Primary API references verified on 2026-08-13:

- Microsoft Learn [`CreateDirectoryW`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createdirectoryw): `lpSecurityAttributes` supplies the security descriptor for a newly created directory; a null value inherits a default descriptor from the parent.
- Microsoft Learn [`CreateFileW`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew): `lpSecurityAttributes` supplies the security descriptor for a newly created file; `CREATE_NEW` fails with `ERROR_FILE_EXISTS` for an existing file; `FILE_FLAG_BACKUP_SEMANTICS` is required for directory handles; `FILE_FLAG_OPEN_REPARSE_POINT` opens the reparse point itself.
- Microsoft Learn [`SACL Access Right`](https://learn.microsoft.com/en-us/windows/win32/secauthz/sacl-access-right) and [`GetSecurityInfo`](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo): reading or writing SACL requires enabling `SE_SECURITY_NAME` with `AdjustTokenPrivileges`; for `GetSecurityInfo` the handle also requests `ACCESS_SYSTEM_SECURITY`; for `GetNamedSecurityInfo` the function internally requests that right; disable the privilege after SACL work.
- Microsoft Learn [`GetNamedSecurityInfoW`](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getnamedsecurityinfow): owner/group/DACL reads require `READ_CONTROL` or ownership; SACL reads require `SE_SECURITY_NAME` enabled; returned descriptor buffers are freed with `LocalFree`.
- Microsoft Learn [`Mandatory Integrity Control`](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control): mandatory label ACEs live in the SACL, and the default mandatory policy is no-write-up.
- Microsoft Learn [`ConvertStringSecurityDescriptorToSecurityDescriptorW`](https://learn.microsoft.com/en-us/windows/win32/api/sddl/nf-sddl-convertstringsecuritydescriptortosecuritydescriptorw): converts SDDL to a self-relative security descriptor and returns a `LocalFree`-owned buffer.
- Microsoft Learn [`GetFinalPathNameByHandleW`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew): retrieves the final path for a file or directory handle; the returned path can show the fully resolved target of a symbolic link.

**Files:**
- Create: `src/Vela.Windows/Security/WindowsSecurityDescriptorFactory.cs`
- Create: `src/Vela.Windows/Security/WindowsObjectSecurityVerifier.cs`
- Create: `src/Vela.Windows/Security/WindowsTokenPrivilegeScope.cs`
- Create: `src/Vela.Windows/Security/NativeSecurityMethods.cs`
- Create: `src/Vela.Windows/Security/FileIdentity.cs`
- Create: `tests/Vela.Tests/Windows/WindowsSecurityDescriptorFactoryTests.cs`
- Create: `tests/Vela.Tests/Windows/WindowsObjectSecurityVerifierTests.cs`

- [ ] RED tests:
  - `CreatesExpectedSddlForAdministratorsSystemOnlyHighIntegrity`.
  - `RejectsDirectoryWithInheritanceOrCurrentUserAce`.
  - `RejectsReparsePoint`.
  - `RejectsIdentityMismatchBetweenCreationHandleAndResolvedPath`.
  - `PrivilegeScope_WhenSecurityPrivilegeIsMissing_FailsBeforeCreatingWorkspace`.
  - `PrivilegeScope_RestoresPreviousTokenStateAfterSuccessAndException`.
- [ ] Implement using Windows APIs rather than shelling out:
  - `ConvertStringSecurityDescriptorToSecurityDescriptorW` for SDDL.
  - `CreateDirectoryW` / `CreateFileW` with `SECURITY_ATTRIBUTES` so objects are visible with the final descriptor.
  - `GetNamedSecurityInfoW` / `GetSecurityInfo` to verify owner, DACL, SACL/integrity label.
  - `GetFileInformationByHandle` to capture file identity.
  - `GetFinalPathNameByHandleW` plus reparse-safe open flags to verify each derived segment without following a junction/symlink silently.
- [ ] Privilege handling for SACL/MIC verification:
  - enable `SE_SECURITY_NAME` only around SACL/integrity-label read/write calls;
  - when using handle-based `GetSecurityInfo`, open the handle with `READ_CONTROL | ACCESS_SYSTEM_SECURITY`;
  - when using path-based `GetNamedSecurityInfoW`, enable `SE_SECURITY_NAME` before the call and let the API request the access right internally;
  - restore the previous privilege state immediately after the SACL operation, including failure paths;
  - keep DACL/owner-only checks on `READ_CONTROL` where SACL data is not needed.
- [ ] Implement a narrow disposable `WindowsTokenPrivilegeScope` around SACL/mandatory-label creation and reads:
  - open the current process token with the least required rights;
  - resolve and enable `SeSecurityPrivilege` with `AdjustTokenPrivileges`;
  - treat `ERROR_NOT_ALL_ASSIGNED` as a closed failure before creating the workspace;
  - restore the exact previous token privilege state in `Dispose`, including exception paths;
  - keep token handles inside safe-handle wrappers.
- [ ] Expected security descriptor properties:
  - owner: `BUILTIN\Administrators`.
  - DACL protected/no inheritance.
  - ACEs: `SYSTEM: FullControl`, `BUILTIN\Administrators: FullControl` only.
  - High mandatory integrity label with no-write-up.
- [ ] Path checks:
  - canonical full path starts with `Environment.SpecialFolder.CommonApplicationData` + fixed segments.
  - treat `CommonApplicationData` as the trusted OS anchor; enforce the protected Vela descriptor only on code-derived descendants.
  - open every derived directory segment by handle using reparse-safe flags, reject reparse points, and compare `GetFinalPathNameByHandleW`/file identity with the expected canonical segment.
- [ ] Put native security calls behind a narrow fakeable adapter. Unit tests use synthetic descriptors and fake native results; ordinary test runs do not create or alter `%ProgramData%` objects.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WindowsSecurityDescriptorFactoryTests|FullyQualifiedName~WindowsObjectSecurityVerifierTests" --nologo
```

Expected GREEN.

### Task 2.3: Implement `PrivilegedDiskPartWorkspace`

**Files:**
- Create: `src/Vela.Windows/DiskPart/PrivilegedDiskPartWorkspace.cs`
- Modify: `src/Vela.Windows/DiskPart/DiskPartClient.cs`
- Create: `tests/Vela.Tests/Windows/PrivilegedDiskPartWorkspaceTests.cs`

- [ ] RED tests:
  - `CreateScriptAsync_UsesProgramDataPrivilegedRoot`.
  - `CreateScriptAsync_WritesAsciiAndFlushesToDisk`.
  - `CreateScriptAsync_UsesCreateNewAndRejectsExistingFile`.
  - `CreateScriptAsync_KeepsCreationHandlePinnedWithFileShareReadOnly`.
  - `CreateScriptAsync_RejectsPreExistingDirectoryWithUnexpectedAcl`.
  - `CreateScriptAsync_RejectsReparseSegment`.
  - `DisposeAsync_BestEffortDeletesScriptAndRunDirectory`.
- [ ] Implement root:

```text
%ProgramData%\Vela\Privileged\DiskPart\<operation-nonce>\vela-diskpart-<script-nonce>.txt
```

Use code-derived fixed segments, a non-empty `Guid` formatted as `D`, and a cryptographically strong random nonce; do not accept a caller-supplied root except through an internal fakeable native/filesystem seam.

- [ ] For each directory segment:
  - create missing with protected descriptor;
  - if existing, verify canonical path, directory type, no reparse, owner, DACL, high integrity;
  - fail closed on any mismatch.
- [ ] For script file:
  - write ASCII bytes only;
  - create once with read/write access, create-new semantics, and `FileShare.Read` only;
  - write and flush through the creation handle;
  - while the creation handle is still open, acquire a read-only pin handle, compare both file identities, then close the writer so no replacement gap exists;
  - verify file identity and security descriptor through the retained read-only handle and its resolved path;
  - verify parent directories again before returning lease;
  - bracket high-integrity SACL reads with `SE_SECURITY_NAME` enable/restore and assert restoration in a fake token-privilege adapter test.
- [ ] Add an elevated, opt-in Windows integration check for the real `%ProgramData%` security descriptor and cleanup path; keep the default automated suite on fake native/filesystem boundaries.

Run focused tests. Expected GREEN.

### Task 2.4: Wire default DiskPart client and workflow regression tests

**Files:**
- Modify: `src/Vela.Windows/DiskPart/DiskPartClient.cs`
- Modify: `src/Vela.Tui/Program.cs` only if constructor call changes.
- Modify: `src/Vela.Core/Contracts/IDiskPartClient.cs`
- Modify: `tests/Vela.Tests/Core/CompactionWorkflowTests.cs`
- Modify: `tests/Vela.Tests/Windows/DiskPartScriptBuilderTests.cs`
- Modify: `tests/Vela.Tests/Windows/DiskPartClientTests.cs`

- [ ] Default `new DiskPartClient()` uses `new PrivilegedDiskPartWorkspace()`.
- [ ] Keep `DiskPartScriptBuilder` path validation tests unchanged.
- [ ] Update old temp-directory cleanup tests to workspace lease tests.
- [ ] Add workflow tests:
  - `ExecuteAsync_WhenDetailWorkspaceValidationThrows_ReturnsDiskPartPreflightFailed`.
  - `ExecuteAsync_WhenCompactWorkspaceValidationThrows_ReturnsDiskPartCompactFailed`.
- [ ] Assert terminal result exit-code mapping unchanged.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DiskPart|FullyQualifiedName~CompactionWorkflowTests" --nologo
```

Expected GREEN.

### Task 2.5: Full security chunk validation and commit

Run:

```powershell
dotnet build .\Vela.sln -c Release --no-restore --nologo
dotnet test .\Vela.sln -c Release --no-build --no-restore --nologo
```

Commit:

```bash
git add src/Vela.Core src/Vela.Windows tests/Vela.Tests src/Vela.Tui/Program.cs
git commit -m "fix: harden privileged diskpart scripts"
```

---

## Chunk 3: Add `Vela.Application` model-first boundary

**Goal:** Introduce a platform-neutral project that owns immutable TUI/application state, commands, reducer, effects, and profile state service contracts.

**Dependencies:** Chunks 1-2 complete.

**Rollback point:** commit `fix: harden privileged diskpart scripts`.

### Architecture review checkpoint B

- [ ] `Vela.Application` references only `Vela.Core`.
- [ ] `Vela.Core` remains platform-neutral and has no reference to application/TUI concepts.
- [ ] `Vela.Tui` references `Vela.Application`, `Vela.Windows`, `Vela.Core`, Terminal.Gui, Spectre.Console.
- [ ] `Vela.Windows` may reference `Vela.Application` only for ports and profile state models.

### Task 3.1: Create project and architecture tests

**Files:**
- Create: `src/Vela.Application/Vela.Application.csproj`
- Modify: `Vela.sln`
- Modify: `src/Vela.Tui/Vela.Tui.csproj`
- Modify: `src/Vela.Windows/Vela.Windows.csproj`
- Modify: `tests/Vela.Tests/Vela.Tests.csproj`
- Modify: `tests/Vela.Tests/Architecture/CoreAssemblyDependencyTests.cs`
- Create: `tests/Vela.Tests/Architecture/ApplicationAssemblyDependencyTests.cs`

`Vela.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\Vela.Core\Vela.Core.csproj" />
  </ItemGroup>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

- [ ] RED test: `ApplicationAssembly_HasNoTerminalGuiSpectreOrWindowsReferences`.
- [ ] RED test: `CoreAssembly_HasNoApplicationReference`.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ApplicationAssemblyDependencyTests|FullyQualifiedName~CoreAssemblyDependencyTests" --nologo
```

Expected RED until project/references are added.

- [ ] Add project to solution.
- [ ] Add references and fix namespaces.

Expected GREEN.

### Task 3.2: Move profile state/service contracts into `Vela.Application`

**Files:**
- Create: `src/Vela.Application/Profiles/ProfileStoreState.cs`
- Create: `src/Vela.Application/Profiles/ProfileDraft.cs`
- Create: `src/Vela.Application/Profiles/IProfileStore.cs`
- Create: `src/Vela.Application/Profiles/IProfileService.cs`
- Create: `src/Vela.Application/Profiles/ProfileService.cs`
- Create: `src/Vela.Application/Profiles/ProfileManagementViewModel.cs`
- Modify: `src/Vela.Windows/Configuration/JsonProfileStore.cs`
- Modify: `src/Vela.Tui/Application/TuiServices.cs`
- Modify: tests currently using `ProfileService`, `ProfileStoreState`, `ProfileDraft`, `ProfileManagementViewModel`.

- [ ] Move pure records/interfaces from `TuiServices.cs` and `JsonProfileStore.cs` without changing persisted JSON shape.
- [ ] Keep `JsonProfileStore` in `Vela.Windows.Configuration` as the Windows file adapter implementing `IProfileStore`.
- [ ] Delete `JsonProfileStoreAdapter`; it becomes redundant once `JsonProfileStore : IProfileStore`.
- [ ] Add XML documentation to all new public types.
- [ ] Preserve tests asserting profile view model excludes raw VHDX path.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TuiServicesTests|FullyQualifiedName~JsonProfileStoreTests|FullyQualifiedName~ApplicationAssemblyDependencyTests" --nologo
```

Expected GREEN.

### Task 3.3: Add immutable TUI model, commands, reducer, and effects

**Files:**
- Create: `src/Vela.Application/Tui/TuiSessionState.cs`
- Create: `src/Vela.Application/Tui/TuiViewState.cs`
- Create: `src/Vela.Application/Tui/TuiViewProjector.cs`
- Create: `src/Vela.Application/Tui/TuiWorkspacePage.cs`
- Create: `src/Vela.Application/Tui/TuiCommand.cs`
- Create: `src/Vela.Application/Tui/TuiEffect.cs`
- Create: `src/Vela.Application/Tui/TuiEffectKey.cs`
- Create: `src/Vela.Application/Tui/TuiReducer.cs`
- Create: `src/Vela.Application/Tui/TuiTransition.cs`
- Create: `src/Vela.Application/Tui/LockedCompactionTarget.cs`
- Create: `src/Vela.Application/Startup/StartupInitializationOutcome.cs`
- Create: `src/Vela.Application/Display/DisplayVhdxSummary.cs`
- Create: `src/Vela.Application/Display/DisplayRunSummary.cs`
- Create: `src/Vela.Application/Display/DisplayMessage.cs`
- Create: `src/Vela.Application/Display/DisplayRunEvent.cs`
- Create: `src/Vela.Application/Display/DisplayTextSanitizer.cs`
- Create: `tests/Vela.Tests/Application/TuiReducerTests.cs`
- Create: `tests/Vela.Tests/Application/TuiViewProjectorTests.cs`

Command/effect shape:

```csharp
public abstract record TuiCommand;
public sealed record AppendStartupConfirmationCharacter(char Value) : TuiCommand;
public sealed record RemoveStartupConfirmationCharacter : TuiCommand;
public sealed record SubmitStartupConfirmation : TuiCommand;
public sealed record StartupInitializationCompleted(
    long Generation,
    StartupInitializationOutcome Outcome) : TuiCommand;
public sealed record NavigateMenu(int Offset) : TuiCommand;
public sealed record SelectTarget(int Offset) : TuiCommand;
public sealed record LockSelectedTarget : TuiCommand;
public sealed record RefreshPreflight : TuiCommand;
public sealed record OpenImpactPreview : TuiCommand;
public sealed record SubmitFirstY : TuiCommand;
public sealed record SubmitSecondY : TuiCommand;
public sealed record CancelOrBack : TuiCommand;
public sealed record OpenLogs : TuiCommand;
public sealed record MoveLogSelection(int Offset) : TuiCommand;
public sealed record OpenSelectedLog : TuiCommand;
public sealed record ExecutionJournalEvent(DisplayRunEvent Event) : TuiCommand;

public abstract record TuiEffect;
public sealed record InitializeDataRootEffect(long Generation) : TuiEffect;
public sealed record StartPreflightEffect(Profile Profile, bool PreserveTargetSelection) : TuiEffect;
public sealed record EstimateImpactEffect(LockedCompactionTarget Target, long Revision) : TuiEffect;
public sealed record StartCompactionEffect(OperationRequest Request) : TuiEffect;
public sealed record ReadRunHistoryEffect(long Revision) : TuiEffect;
public sealed record ReadLogDetailEffect(Guid TrustedRunId, long Revision) : TuiEffect;
public sealed record RequestStopEffect : TuiEffect;
```

- [ ] RED tests:
  - `Reducer_SubmitStartupConfirmation_RequiresExactUppercaseYES` and rejects `yes`, `YES `, and overlength input.
  - `Reducer_StartupInitializationCompletion_RequiresCurrentGeneration`.
  - `Reducer_SelectTarget_UpdatesSelectionWithoutLocking`.
  - `Reducer_LockSelectedTarget_StoresTrustedLockedTargetAndEmitsTargetPreflightWhenNeeded`.
  - `Reducer_OpenImpactPreview_RequiresReadyLockedTarget`.
  - `Reducer_FirstY_ShowsSecondConfirmationWithoutStartingWorker`.
  - `Reducer_SecondY_EmitsStartCompactionOnce`.
  - `Reducer_SecondY_BuildsRequestFromLockedTargetNotCurrentSelection`.
  - `Reducer_StartingNewProfilePreflight_InvalidatesLockedTargetAndConfirmations`.
  - `Reducer_RunningState_IgnoresNavigationAndRefresh`.
  - `Reducer_EscapeFromResult_ReturnsToOverview`.
  - `Reducer_RejectsStaleAsyncEffectByRevision`.
  - `Reducer_OpenLogs_EmitsHistoryReadAndUsesOpaqueSelection`.
  - `Reducer_OpenSelectedLog_EmitsTrustedRunIdOnlyInsideEffect`.
  - `Projector_ExcludesRawPathRunIdNativeOutputAndExceptionText`.
- [ ] Implement reducer as a pure function:

```csharp
public static TuiTransition Reduce(TuiSessionState state, TuiCommand command);
```

- [ ] No Terminal.Gui, Spectre, file I/O, registry, process, or async work in reducer.
- [ ] Add complete XML documentation to every public state, command, effect, projection, and member introduced in this task.
- [ ] Keep `TuiSessionState` as trusted application state; it may hold locked paths and trusted run identifiers needed by effects.
- [ ] Make `TuiViewProjector` the sole mapping from trusted state to immutable `TuiViewState`. The public runtime render callback exposes only `TuiViewState`; Terminal.Gui views never receive `TuiSessionState`, `LockedCompactionTarget`, `OperationRequest`, or `RunEvent`.
- [ ] Map journal data to bounded `DisplayRunEvent` before dispatch; raw native output and exception details go to trusted logs only.
- [ ] Give each async effect kind a `TuiEffectKey` plus monotonically increasing generation so stale completions are ignored independently for startup, preflight, impact, history, log detail, and execution.
- [ ] Treat execution specially: `StartCompactionEffect` is single-flight. The reducer ignores every later start command while execution is launching/running; refresh generations never cancel a launched worker or abandon its journal observer.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TuiReducerTests|FullyQualifiedName~TuiViewProjectorTests" --nologo
```

Expected GREEN.

### Task 3.4: Expand the coverage gate at the new boundary

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `scripts/Verify-Coverage.ps1`
- Modify: `tests/Vela.Tests/Vela.Tests.csproj` if coverlet settings need update.

- [ ] Update the coverage include as soon as `Vela.Application` exists:

```powershell
-p:Include="[Vela.Core]*%2C[Vela.Windows]*%2C[Vela.Application]*%2C[Vela.Tui]*"
```

- [ ] Update the required package list:

```powershell
$required = @( "Vela.Core", "Vela.Windows", "Vela.Application", "Vela.Tui" )
```

- [ ] Add focused tests until all four packages meet 80% line coverage. Keep only the composition-only `Program.cs` exclusion; reducers, effect contracts/runtime, projections, profile services, and Terminal.Gui adapters remain measured.
- [ ] Starting with this task, run the expanded coverage command and `Verify-Coverage.ps1` at every remaining chunk boundary.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-build --no-restore --nologo -p:CollectCoverage=true -p:CoverletOutput=.\..\..\artifacts\coverage\coverage -p:CoverletOutputFormat=cobertura -p:Include="[Vela.Core]*%2C[Vela.Windows]*%2C[Vela.Application]*%2C[Vela.Tui]*" -p:ExcludeByFile="**/Program.cs"
pwsh -NoProfile -File .\scripts\Verify-Coverage.ps1
```

Expected GREEN: each required assembly has at least 80% line coverage.

### Task 3.5: Commit

```bash
git add .github scripts Vela.sln src/Vela.Application src/Vela.Tui src/Vela.Windows tests/Vela.Tests
git commit -m "feat: introduce model-first application boundary"
```

---

## Chunk 4: Display-safe projection and running-target semantics

**Goal:** Views consume display-safe state only; trusted paths/run ids/native output remain in application services/effects and journal. Running target is presented as stop impact, not a blocker.

**Dependencies:** Chunk 3 complete.

**Rollback point:** commit `feat: introduce model-first application boundary`.

### Task 4.1: Add display-safe VHDX and log projections

**Files:**
- Modify: `src/Vela.Application/Display/DisplayVhdxSummary.cs`
- Modify: `src/Vela.Application/Display/DisplayRunSummary.cs`
- Modify: `src/Vela.Application/Display/DisplayMessage.cs`
- Modify: `src/Vela.Application/Display/DisplayTextSanitizer.cs`
- Modify: `src/Vela.Application/Tui/TuiViewProjector.cs`
- Modify: `src/Vela.Tui/Application/DashboardViewModel.cs` or move to `src/Vela.Application/Preflight/DashboardViewModel.cs`
- Modify: `src/Vela.Tui/Application/PreflightOverviewViewModel.cs` or move pure parts to `src/Vela.Application/Preflight/`
- Modify: `src/Vela.Tui/Application/RunProgressViewModel.cs`
- Modify: `src/Vela.Tui/Application/RunLogReader.cs`
- Modify: `src/Vela.Tui/Application/RunLogAnalyzer.cs`
- Modify: `src/Vela.Tui/Views/VelaTerminalShell.cs`
- Test: `tests/Vela.Tests/Application/DisplayProjectionTests.cs`
- Modify: `tests/Vela.Tests/Architecture/ApplicationAssemblyDependencyTests.cs`
- Test: `tests/Vela.Tests/Tui/PreflightOverviewViewModelTests.cs`
- Test: `tests/Vela.Tests/Tui/RunProgressMapperTests.cs`

- [ ] RED tests:
  - serializing display view models does not contain `D:\`, `ext4.vhdx` full parent path, run id, `native output`, or exception text.
  - `RunProgressViewModel` exposes `DisplayVhdxSummary` and target label, not raw `VhdxPath`.
  - `RunHistoryEntry` detail view displays profile/distro/terminal result and not raw path.
  - Terminal.Gui view/binder public APIs do not accept `TuiSessionState`, `LockedCompactionTarget`, `OperationRequest`, or `RunEvent`.
  - display strings are length-bounded and strip control/ANSI/OSC sequences; unknown internal enum values map to a stable Chinese fallback label.
- [ ] Implement `DisplayVhdxSummary` fields:

```csharp
public sealed record DisplayVhdxSummary(
    string FileName,
    string FileType,
    string CurrentSize,
    string MappingStatus,
    string SparseState,
    string HostCapacityStatus);
```

- [ ] Confine trusted `LockedCompactionTarget` to reducer/runtime/effects; it contains distro, profile id, strict resolved/requested path, snapshot size, and shutdown mode.
- [ ] Remove or mark internal any display-facing raw path properties.
- [ ] Centralize boundary cleanup in `DisplayTextSanitizer`; project only explicit allowlisted fields, map status values to Chinese UI copy, normalize whitespace, remove control/ANSI/OSC content, and truncate before constructing display records.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DisplayProjectionTests|FullyQualifiedName~PreflightOverviewViewModelTests|FullyQualifiedName~RunProgressMapperTests|FullyQualifiedName~TuiServicesTests" --nologo
```

Expected GREEN.

### Task 4.2: Reclassify running target as impact

**Files:**
- Modify: `src/Vela.Application/Preflight/PreflightOverviewViewModel.cs` or current `src/Vela.Tui/Application/PreflightOverviewViewModel.cs`
- Modify: `src/Vela.Tui/Views/PreflightHomeView.cs`
- Modify: `src/Vela.Tui/Views/PreflightTargetDetailView.cs`
- Modify: `tests/Vela.Tests/Tui/PreflightOverviewViewModelTests.cs`
- Modify: `tests/Vela.Tests/Tui/VelaTerminalShellTests.cs`

- [ ] RED tests:
  - `CreateTargetDetail_WhenSelectedDistroRunningAndOtherChecksPass_IsReadyAndShowsStopImpact`.
  - `CreateTargetRows_RunningDistro_StatusTextMentionsWillStopNotBlocker`.
  - `Shell_AllowsImpactPreviewForReadyRunningLockedTarget`.
  - `Preflight_ForRunningTarget_DoesNotInvokeStopTerminateDiskPartOrElevationAdapters`.
- [ ] Change target detail checks so running target contributes to `StopScopeSummary` / impact line, not `BlockerCount`.
- [ ] Keep running inventory read failures as failures.
- [ ] Preserve two stop modes:
  - `ShutdownMode.Distro`: only target distro will be terminated.
  - `ShutdownMode.Global`: all WSL instances may stop.

Run focused tests. Expected GREEN.

### Task 4.3: Commit

```bash
git add src/Vela.Application src/Vela.Tui tests/Vela.Tests
git commit -m "fix: project only display-safe tui state"
```

---

## Chunk 5: Explicit effects and runtime orchestration

**Goal:** Move async work out of `Program.cs`/shell event handlers into a testable `TuiEffectRunner` and `TuiRuntime` that dispatch commands back into the reducer.

**Dependencies:** Chunks 3-4 complete.

**Rollback point:** commit `fix: project only display-safe tui state`.

### Task 5.1: Define effect ports

**Files:**
- Create: `src/Vela.Application/Tui/Effects/ITuiEffectRunner.cs`
- Create: `src/Vela.Application/Tui/Effects/IPreflightPort.cs`
- Create: `src/Vela.Application/Tui/Effects/IImpactEstimatePort.cs`
- Create: `src/Vela.Application/Tui/Effects/IElevatedOperationPort.cs`
- Create: `src/Vela.Application/Tui/Effects/IRunJournalPort.cs`
- Create: `src/Vela.Application/Tui/Effects/IRunHistoryPort.cs`
- Create: `src/Vela.Application/Tui/Effects/ILogReaderPort.cs`
- Create: `src/Vela.Application/Tui/Effects/IStartupDataRootPort.cs`
- Create: `src/Vela.Application/Tui/TuiRuntime.cs`
- Test: `tests/Vela.Tests/Application/TuiRuntimeTests.cs`

- [ ] RED tests:
  - `Runtime_SerializesCommandsWhileEffectsCompleteAsynchronously`.
  - `Runtime_ProjectsViewStateBeforeInvokingRenderer`.
  - `Runtime_ExecutesEachEmittedEffectExactlyOnce`.
  - `Runtime_IgnoresStaleEffectCompletionByKeyAndGeneration`.
  - `Runtime_ConvertsEffectExceptionsToSafeStatusCommand`.
  - `Runtime_CancelsInflightPreflightWhenRefreshStartsNewGeneration`.
- [ ] Implement `TuiRuntime.DispatchAsync(TuiCommand command)`:
  - a single mailbox serializes commands and reducer transitions;
  - trusted state is replaced atomically;
  - `TuiViewProjector` creates a new immutable `TuiViewState`;
  - renderer callback receives only `TuiViewState`;
  - supervised effect tasks run outside the mailbox and re-enqueue typed completion commands;
  - a newer effect with the same `TuiEffectKey` cancels the older generation;
  - every effect emitted by an accepted transition starts exactly once.
- [ ] Apply replacement cancellation only to restartable read effects (startup initialization before mutation, preflight, impact, history, and log detail). Profile mutation and execution use the dedicated rules in Chunks 7 and this task.
- [ ] `StartCompactionEffect` accepts cancellation only before UAC launch succeeds. After launch, retain one trusted journal observer until canonical terminal state or the existing observation timeout; later UI commands do not cancel that observer. Release observer resources only after its terminal/timeout completion command has been reduced.
- [ ] Keep Terminal.Gui input responsive while preflight, impact, history, log reads, UAC launch, and journal polling are active.
- [ ] Keep all exception messages display-safe; raw exception goes only to trusted logs where available.
- [ ] Add complete XML documentation to each public runtime/effect-port API; make implementation details internal where cross-project access is unnecessary.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TuiRuntimeTests|FullyQualifiedName~TuiReducerTests" --nologo
```

Expected GREEN.

### Task 5.2: Implement production `TuiEffectRunner`

**Files:**
- Create: `src/Vela.Tui/Application/TuiEffectRunner.cs`
- Create: `src/Vela.Tui/Application/Ports/WorkflowPreflightPort.cs`
- Create: `src/Vela.Tui/Application/Ports/WslImpactEstimatePort.cs`
- Create: `src/Vela.Tui/Application/Ports/ElevatedOperationPort.cs`
- Create: `src/Vela.Tui/Application/Ports/RunJournalPort.cs`
- Create: `src/Vela.Tui/Application/Ports/RunHistoryPort.cs`
- Create: `src/Vela.Tui/Application/Ports/LogReaderPort.cs`
- Create: `src/Vela.Tui/Application/Ports/StartupDataRootPort.cs`
- Modify: `src/Vela.Tui/Application/AutomaticPreflightCoordinator.cs` (migrate or wrap; do not keep duplicate generation logic long term)
- Modify: `src/Vela.Tui/Program.cs` only to instantiate ports.
- Test: `tests/Vela.Tests/Tui/TuiEffectRunnerTests.cs`

- [ ] Move logic currently inside `Program.cs` local functions:
  - `ShowRecentRunsAsync` -> `RunHistoryPort`.
  - `ShowLogsAsync` -> `LogReaderPort`.
  - `ShowCompactionImpactAsync` -> `WslImpactEstimatePort`.
  - `StartCompactionAsync` and journal polling -> `ElevatedOperationPort` / `RunJournalPort`.
- [ ] Map trusted `RunEvent` and history data to bounded `DisplayRunEvent` / `DisplayRunSummary` before dispatching completion commands.
- [ ] Preserve single worker gate (`CompactRunGate`) and UAC launch result mapping.
- [ ] Preserve journal terminal result mapping and visible progress line bounding.
- [ ] RED/GREEN tests with fake ports:
  - UAC cancelled maps to cancelled display state without appending fake worker terminal event.
  - journal timeout maps to timed out/read-failed display state.
  - successful terminal event displays elapsed and reclaimed bytes from history.
  - history and selected-log effects reject stale generations and never place raw run ids/native output in `TuiViewState`.
  - a second execution command during launch/running starts no process, and unrelated generations leave the launched worker's journal observation active.

Run focused tests. Expected GREEN.

### Task 5.3: Commit

```bash
git add src/Vela.Application src/Vela.Tui tests/Vela.Tests
git commit -m "feat: route tui workflows through explicit effects"
```

---

## Chunk 6: Terminal.Gui adapter extraction

**Goal:** `VelaTerminalShell` becomes a view adapter that binds display-safe `TuiViewState` to Terminal.Gui controls and emits typed `TuiCommand`. It no longer owns workflow state, target locking, revisions, profile mutations, log I/O, UAC, or journal polling.

**Dependencies:** Chunk 5 complete.

**Rollback point:** commit `feat: route tui workflows through explicit effects`.

### Architecture review checkpoint C

- [ ] Shell fields are Terminal.Gui controls, layout cache, and binder state only.
- [ ] Target lock, selected target, current page, preflight status, impact estimate, confirmation stage, running state, and log selection all live in `TuiSessionState`.
- [ ] Every key handler emits a `TuiCommand`.
- [ ] Every UI refresh receives only `TuiViewState`; trusted session/effect types never cross into view or binder APIs.

### Task 6.1: Introduce binder and command sink

**Files:**
- Create: `src/Vela.Tui/Views/Shell/ITuiCommandSink.cs`
- Create: `src/Vela.Tui/Views/Shell/VelaShellViewBinder.cs`
- Create: `src/Vela.Tui/Views/Shell/VelaShellInputRouter.cs`
- Modify: `src/Vela.Tui/Views/VelaTerminalShell.cs`
- Test: `tests/Vela.Tests/Tui/VelaShellInputRouterTests.cs`
- Test: `tests/Vela.Tests/Tui/VelaShellViewBinderTests.cs`

- [ ] RED tests:
  - `InputRouter_R_OnOverviewEmitsRefreshPreflightForLowerAndUppercase`.
  - `InputRouter_EnterOnOverviewEmitsLockSelectedTarget`.
  - `InputRouter_RightOnTargetDetailEmitsOpenImpactPreview`.
  - `InputRouter_YOrLowercaseYOnImpactEmitsSubmitFirstY`.
  - `InputRouter_YOrLowercaseYOnConfirmationEmitsSubmitSecondY`.
  - `InputRouter_RunningStateConsumesNavigationWithoutCommand`.
- [ ] Implement router using current Terminal.Gui `Key` handling.
- [ ] Binder maps `TuiViewState` to existing views (`PreflightHomeView`, `PreflightTargetDetailView`, `CompactionImpactView`, `RunProgressView`, `LogArchiveView`).

Run focused tests. Expected GREEN.

### Task 6.2: Extract shell layout/control construction

**Files:**
- Create: `src/Vela.Tui/Views/Shell/VelaShellLayoutController.cs`
- Create: `src/Vela.Tui/Views/Shell/VelaShellNavigationView.cs`
- Create: `src/Vela.Tui/Views/Shell/VelaShellContentHost.cs`
- Move/modify: `src/Vela.Tui/Views/VelaTerminalShell.cs` -> keep public class as root window under 800 lines.
- Test: `tests/Vela.Tests/Tui/VelaTerminalShellTests.cs`
- Test: `tests/Vela.Tests/Tui/VelaLayoutMetricsTests.cs`

- [ ] Preserve responsive sizes: `160×45`, `120×35`, `100×30`, `80×24`, `60×16`.
- [ ] Preserve keyboard affordances: `Enter`, `Esc`, `R/r`, left/right, `Y -> Y`, running input lock.
- [ ] Keep existing screenshots/text expectations updated only for intentional copy changes.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~VelaTerminalShellTests|FullyQualifiedName~VelaLayoutMetricsTests|FullyQualifiedName~VelaShell" --nologo
```

Expected GREEN.

### Task 6.3: Remove shell-owned state machine fields

**Files:**
- Modify: `src/Vela.Tui/Views/VelaTerminalShell.cs`
- Modify: `src/Vela.Tui/Views/Shell/*`
- Modify: `src/Vela.Tui/Program.cs`
- Test: `tests/Vela.Tests/Tui/VelaTerminalShellTests.cs`

- [ ] Remove or replace these shell-owned fields with state binding:
  - `_selectedTargetIndex`.
  - `_targetLocked`.
  - `_lockedTargetName`.
  - `_compactionEstimate`.
  - `_legacySelectedMenuIndex`.
  - `_selectedLogEntry` / `_logSnapshot` as workflow state.
  - `PreflightState` as mutable shell property.
- [ ] Keep only view selection/focus values that Terminal.Gui needs to render controls.
- [ ] Replace `ActionRequested`, `SelectionPreviewRequested`, `ConfirmationSubmitted`, `TargetPreflightRequested` with `CommandSubmitted` or `ITuiCommandSink`.

Run focused tests. Expected GREEN.

### Task 6.4: Commit

```bash
git add src/Vela.Tui tests/Vela.Tests
git commit -m "refactor: make terminal gui shell a state adapter"
```

---

## Chunk 7: Model-first profile management in Terminal.Gui

**Goal:** Restore full profile N/E/D/Enter management in the production Terminal.Gui path through reducer commands/effects, with exact uppercase `YES` confirmation for target-changing edits and deletion, and without leaking raw paths in display projection.

**Dependencies:** Chunks 3, 5, 6 complete.

**Rollback point:** commit `refactor: make terminal gui shell a state adapter`.

### Task 7.1: Add profile commands and reducer tests

**Files:**
- Modify: `src/Vela.Application/Tui/TuiCommand.cs`
- Modify: `src/Vela.Application/Tui/TuiEffect.cs`
- Modify: `src/Vela.Application/Tui/TuiSessionState.cs`
- Modify: `src/Vela.Application/Tui/TuiReducer.cs`
- Test: `tests/Vela.Tests/Application/ProfileReducerTests.cs`

Commands:

```csharp
public sealed record OpenProfileManagement : TuiCommand;
public sealed record MoveProfileSelection(int Offset) : TuiCommand;
public sealed record StartNewProfile : TuiCommand;
public sealed record StartEditProfile : TuiCommand;
public sealed record RequestDeleteProfile : TuiCommand;
public sealed record EditProfileField(ProfileEditField Field, string Value) : TuiCommand;
public sealed record SubmitProfileEditor : TuiCommand;
public sealed record ConfirmProfileMutation(string Response) : TuiCommand;
public sealed record SelectProfile : TuiCommand;
public sealed record ProfileMutationCompleted(long Generation, ProfileStoreState State) : TuiCommand;
public sealed record ProfileMutationFailed(long Generation, DisplayMessage Message) : TuiCommand;
```

Effects:

```csharp
public sealed record CreateProfileEffect(ProfileDraft Draft, long Generation) : TuiEffect;
public sealed record UpdateProfileEffect(Guid ProfileId, ProfileDraft Draft, long Generation) : TuiEffect;
public sealed record DeleteProfileEffect(Guid ProfileId, long Generation) : TuiEffect;
public sealed record SelectProfileEffect(Guid ProfileId, long Generation) : TuiEffect;
```

- [ ] RED tests for uppercase/lowercase N/E/D, Enter, and Esc; target-changing edit and deletion accept exact uppercase `YES` plus Enter only.
- [ ] RED tests preserve timeout boundaries at 4, 5, 300, and 301 seconds and reject empty/oversized distro, path, and profile-name fields through the existing schema/service validation.
- [ ] Keep raw VHDX editor input only in trusted session/effect state. Project the field as write-only/sensitive and keep it out of lists, summaries, errors, and logs.
- [ ] Add complete XML documentation to public profile commands, effects, completion commands, and members as they are introduced.
- [ ] Use a dedicated `TuiEffectKey.ProfileMutation` generation. While one mutation is active, reject additional mutation commands; completion commands with any other generation leave state unchanged.
- [ ] On successful profile selection, target-changing update, or deletion, atomically clear locked target, impact estimate, both confirmation stages, and prior preflight generation before emitting a new read-only preflight effect for the resulting current profile.
- [ ] RED tests prove out-of-order select/update/delete completions do not restore an older current profile, locked target, impact estimate, or confirmation state.

Run focused tests. Expected GREEN after implementation.

### Task 7.2: Add Terminal.Gui profile views

**Files:**
- Create: `src/Vela.Tui/Views/Profiles/ProfileManagementView.cs`
- Create: `src/Vela.Tui/Views/Profiles/ProfileEditorView.cs`
- Create: `src/Vela.Tui/Views/Profiles/ProfileDeleteConfirmationView.cs` or reuse confirmation view.
- Modify: `src/Vela.Tui/Views/Shell/VelaShellContentHost.cs`
- Modify: `src/Vela.Tui/Views/Shell/VelaShellInputRouter.cs`
- Test: `tests/Vela.Tests/Tui/ProfileManagementViewTests.cs`
- Test: `tests/Vela.Tests/Tui/VelaTerminalShellTests.cs`

- [ ] RED tests:
  - pressing `N` opens editor;
  - pressing `E` opens editor for selected profile;
  - pressing `D` opens exact YES confirmation;
  - saving an edit that changes distro, VHDX, or shutdown mode opens exact YES confirmation;
  - saving a non-target-changing edit proceeds without that confirmation;
  - pressing `Enter` on another profile emits `SelectProfile` and refreshes preflight;
  - `Esc` returns to overview without mutation;
  - display list contains `已配置`/`待配置`, never full path.
- [ ] Implement views as controls only; mutation happens through commands/effects.

Run focused tests. Expected GREEN.

### Task 7.3: Implement profile effects

**Files:**
- Modify: `src/Vela.Tui/Application/TuiEffectRunner.cs`
- Modify: `src/Vela.Tui/Application/Ports/ProfilePort.cs` (create if preferred)
- Test: `tests/Vela.Tests/Tui/TuiEffectRunnerTests.cs`
- Test: `tests/Vela.Tests/Tui/TuiServicesTests.cs`

- [ ] Effects call `IProfileService` and dispatch completion commands with new profile state.
- [ ] Selecting/updating current profile marks preflight stale and emits `StartPreflightEffect` as needed.
- [ ] Deleting current/last profile preserves existing validation messages.
- [ ] A failed save/delete leaves the in-memory current profile and trusted target state unchanged.
- [ ] Profile mutation I/O is single-flight and is not cancelled after the store write begins; its generation controls whether the completion is accepted, never whether a partially started write is abandoned.

Run focused tests. Expected GREEN.

### Task 7.4: Commit

```bash
git add src/Vela.Application src/Vela.Tui tests/Vela.Tests
git commit -m "feat: restore terminal gui profile management"
```

---

## Chunk 8: Thin `Program.cs` composition and startup/static flow

**Goal:** `Program.cs` only parses mode, builds services, runs worker or interactive runtime, and delegates startup static frames. Local functions and workflow branching move to focused files.

**Dependencies:** Chunks 5-7 complete.

**Rollback point:** commit `feat: restore terminal gui profile management`.

### Task 8.1: Extract composition root helpers

**Files:**
- Create: `src/Vela.Tui/Composition/VelaCompositionRoot.cs`
- Create: `src/Vela.Tui/ProgramModes/InteractiveMode.cs`
- Create: `src/Vela.Tui/ProgramModes/RedirectedStartupFlow.cs`
- Modify: `src/Vela.Tui/Program.cs`
- Test: `tests/Vela.Tests/Tui/ProgramCompositionTests.cs`
- Test: `tests/Vela.Tests/Tui/StartupGateTests.cs`

- [ ] RED tests:
  - `ProgramComposition_CreatesSingleInteractiveTerminalGuiRuntime`.
  - `InteractiveMode_WhenStartupGateIsRequired_StartsRuntimeOnStartupConfirmationPage`.
  - `RedirectedStartupFlow_WhenInputRedirected_RendersStaticFrameAndReturnsValidationExit`.
  - `ProgramComposition_WorkerModeUsesCompactionWorkflowWithPrivilegedDiskPartClient`.
- [ ] Move creation of `AppPaths`, stores, journals, Windows adapters, ports, runtime, shell, and static renderer to composition helpers.
- [ ] Keep `Program.cs` under ~150 lines.

Run focused tests. Expected GREEN.

### Task 8.2: Remove duplicate local functions and old event wiring

**Files:**
- Modify: `src/Vela.Tui/Program.cs`
- Modify: `src/Vela.Tui/ProgramModes/InteractiveMode.cs`
- Modify: `src/Vela.Tui/Views/VelaTerminalHost.cs` or delete if runtime supersedes it.
- Modify: `src/Vela.Tui/Application/AutomaticPreflightCoordinator.cs` or delete if runtime generation supersedes it.
- Test: `tests/Vela.Tests/Tui/AutomaticPreflightCoordinatorTests.cs` migrated to runtime tests if class deleted.
- Test: `tests/Vela.Tests/Tui/VelaTerminalHostTests.cs` migrated/deleted if class deleted.

- [ ] Delete old event-driven links:
  - `shell.ActionRequested += ...`.
  - `shell.SelectionPreviewRequested += ...`.
  - `shell.ConfirmationSubmitted += ...`.
  - `shell.TargetPreflightRequested += ...`.
- [ ] Interactive mode wires `shell.CommandSubmitted` to `runtime.DispatchAsync`.
- [ ] Runtime state change callback invokes Terminal.Gui dispatcher and binder.
- [ ] Preserve first-run/repair startup confirmation through the same `TuiSessionState`/reducer/runtime using exact uppercase `YES` plus Enter; there is one interactive input owner.
- [ ] When input is redirected, render one deterministic static frame and return the existing validation exit without creating an interactive input loop.

Run focused tests. Expected GREEN.

### Task 8.3: Commit

```bash
git add -A src/Vela.Tui tests/Vela.Tests
git commit -m "refactor: thin tui composition root"
```

---

## Chunk 9: Static frame renderer and old interactive path deletion

**Goal:** Spectre.Console remains only for redirected output and startup failure/confirmation static frames. Old `TuiApplication`, `SpectreTuiInput`, and `SpectreTuiFrameSink` are removed after equivalent reducer/runtime tests exist.

**Dependencies:** Chunk 8 complete.

**Rollback point:** commit `refactor: thin tui composition root`.

### Task 9.1: Rename and narrow `FrameRenderer`

**Files:**
- Move: `src/Vela.Tui/Rendering/FrameRenderer.cs` -> `src/Vela.Tui/Rendering/StaticFrameRenderer.cs`
- Modify: `src/Vela.Tui/Program.cs`
- Modify: tests referencing `FrameRenderer`.
- Test: create/modify `tests/Vela.Tests/Tui/StaticFrameRendererTests.cs`

- [ ] RED tests:
  - `StaticFrameRenderer_RenderRedirected_BuildsDeterministicMarkup`.
  - `StaticFrameRenderer_DoesNotExposeInteractiveInputTypes`.
  - `StartupFailure_UsesStaticRendererWhenInputRedirected`.
- [ ] Rename class to `StaticFrameRenderer`.
- [ ] Keep `BuildMarkup`, `Build`, `RenderRedirected`, and startup `Render` if needed.
- [ ] Remove any dependency on `ITuiInput`, `ITuiFrameSink`, `TuiApplicationContext`, or page controllers.

Run focused tests. Expected GREEN.

### Task 9.2: Delete old `TuiApplication` interactive loop

**Files:**
- Delete: `src/Vela.Tui/Application/TuiApplication.cs`
- Delete or rewrite: `tests/Vela.Tests/Tui/TuiApplicationTests.cs`
- Modify: `src/Vela.Tui/Rendering/StaticFrameRenderer.cs`
- Modify: `src/Vela.Tui/Menu/MainMenu.cs` only if old confirmation/page models were colocated there.
- Modify: `tests/Vela.Tests/Tui/MainMenuTests.cs`

- [ ] Before deletion, verify equivalent behavior is covered by:
  - `TuiReducerTests` and `ProfileReducerTests` for menu/profile/confirmation command semantics.
  - `TuiRuntimeTests` for exception/cancellation safe display.
  - `VelaShellInputRouterTests` for key translation.
- [ ] Move still-needed records (`ConfirmationInputStatus`, `ConfirmationInputResult`, static page view models for renderer) into `Vela.Application` or a static-renderer-only file.
- [ ] Delete Spectre interactive input/sink types.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TuiApplicationTests|FullyQualifiedName~TuiReducerTests|FullyQualifiedName~TuiRuntimeTests|FullyQualifiedName~StaticFrameRendererTests|FullyQualifiedName~MainMenuTests" --nologo
```

Expected GREEN with no `TuiApplicationTests` remaining, or with tests renamed to reducer/runtime tests.

### Task 9.3: Commit

```bash
git add -A src/Vela.Tui tests/Vela.Tests
git commit -m "refactor: keep spectre rendering static only"
```

---

## Chunk 10: Coverage gate, docs, full verification, and final review

**Goal:** Enforce coverage for `Vela.Application`, `Vela.Core`, `Vela.Windows`, and `Vela.Tui`; document architecture changes; complete reviewer/security checkpoints.

**Dependencies:** Chunks 1-9 complete.

**Rollback point:** commit `refactor: keep spectre rendering static only`.

### Task 10.1: Audit the coverage gate and final report

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `scripts/Verify-Coverage.ps1`
- Modify: `tests/Vela.Tests/Vela.Tests.csproj` if coverlet settings need update.

- [ ] Confirm the coverage command introduced in Task 3.4 still includes:

```powershell
-p:Include="[Vela.Core]*%2C[Vela.Windows]*%2C[Vela.Application]*%2C[Vela.Tui]*"
```

- [ ] Confirm the script still requires:

```powershell
$required = @( "Vela.Core", "Vela.Windows", "Vela.Application", "Vela.Tui" )
```

- [ ] Keep `Program.cs` excluded; do not exclude reducers/effect runner/shell adapter.
- [ ] Inspect the Cobertura package list for duplicate or renamed assemblies and verify every required package has measured lines.

Run:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-build --no-restore --nologo -p:CollectCoverage=true -p:CoverletOutput=.\..\..\artifacts\coverage\coverage -p:CoverletOutputFormat=cobertura -p:Include="[Vela.Core]*%2C[Vela.Windows]*%2C[Vela.Application]*%2C[Vela.Tui]*" -p:ExcludeByFile="**/Program.cs"
pwsh -NoProfile -File .\scripts\Verify-Coverage.ps1
```

Expected GREEN: each required assembly >= 80% line coverage.

### Task 10.2: Public API docs gate for new application APIs

**Files:**
- Modify: `src/Vela.Application/Vela.Application.csproj`
- Modify: new public files under `src/Vela.Application/**`
- Optionally modify: `Directory.Build.props` only if all current public APIs have XML docs by this point.

- [ ] Ensure every new public type/member in `Vela.Application` has XML docs.
- [ ] Keep complete-sentence `<summary>`, `<param>`, `<returns>`, `<remarks>` where relevant.
- [ ] If enabling docs globally would surface many unrelated existing warnings, keep the gate scoped to `Vela.Application` now and create a follow-up issue for Core/Windows/Tui.

Run:

```powershell
dotnet build .\src\Vela.Application\Vela.Application.csproj -c Release --no-restore --nologo
```

Expected GREEN.

### Task 10.3: Update architecture docs

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/agent-handoff.md`
- Modify: `docs/testing-and-release.md`
- Optionally create: `docs/tui-model-first.md` if existing docs become too large.

- [ ] Document new project graph.
- [ ] Document TUI runtime flow:

```text
Terminal.Gui key -> TuiCommand -> TuiReducer -> TuiSessionState + TuiEffect[]
  -> TuiEffectRunner -> completion TuiCommand -> reducer
  -> TuiViewProjector -> TuiViewState -> binder -> Terminal.Gui
```

- [ ] Document trusted/display-safe boundary.
- [ ] Document worker sequence and DiskPart privileged workspace.
- [ ] Document unchanged contracts: operation request schema, journal schema, terminal result/exit codes, two `Y/y` confirmation.
- [ ] Document the four-assembly coverage gate and the elevated opt-in DiskPart security integration check in `docs/testing-and-release.md`.

No README screenshot update in this plan unless product visuals materially change; any screenshot update must use real Release TUI captures.

### Task 10.4: Full verification

Run:

```powershell
git status --short
dotnet restore .\Vela.sln -r win-x64 --locked-mode --ignore-failed-sources -p:EnableRuntimePackDownload=false -p:DisableTransitiveFrameworkReferenceDownloads=true
dotnet build .\Vela.sln -c Release --no-restore --nologo
dotnet test .\Vela.sln -c Release --no-build --no-restore --nologo --logger "trx;LogFileName=all-tests.trx" --results-directory .\artifacts\test-results
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj -c Release --no-build --no-restore --nologo -p:CollectCoverage=true -p:CoverletOutput=.\..\..\artifacts\coverage\coverage -p:CoverletOutputFormat=cobertura -p:Include="[Vela.Core]*%2C[Vela.Windows]*%2C[Vela.Application]*%2C[Vela.Tui]*" -p:ExcludeByFile="**/Program.cs"
pwsh -NoProfile -File .\scripts\Verify-Coverage.ps1
git diff --check
git diff --stat
git diff -- src/Vela.Core src/Vela.Application src/Vela.Windows src/Vela.Tui tests .github scripts docs
```

Expected:

- Restore succeeds with locked dependencies.
- Build succeeds with warnings as errors.
- All tests pass.
- Coverage gate passes for Core, Windows, Application, Tui.
- Diff check has no whitespace errors.
- No `artifacts/`, logs, screenshots, or local config files staged.

### Task 10.5: Reviewer/security checkpoint before completion

Use reviewer agent if available; otherwise perform this checklist in the main session.

Reviewer prompt summary:

```text
Review the model-first TUI refactor diff for correctness and security. Focus on:
1. Worker administrator probe occurs before journal open/request claim.
2. DiskPart scripts use protected ProgramData workspace, create-new, ACL/high IL verification, reparse rejection, and pin handle through process exit.
3. Core remains platform-neutral.
4. Terminal.Gui shell emits typed commands and owns no workflow state.
5. Display-facing state has no raw paths, run ids, native output, stack traces, or internal enum names.
6. Running target is an impact line, not a blocker.
7. Two Y/y confirmation, locked target, worker fresh Lxss strict match, journal schema, and exit codes are unchanged.
8. Coverage gate includes Vela.Application and Vela.Tui.
```

- [ ] Address CRITICAL/HIGH findings before final commit.
- [ ] Re-run focused tests for any changed area.
- [ ] Re-run full verification if review changes code.

### Task 10.6: Final commit

```bash
git add .github scripts docs src tests Vela.sln
git commit -m "refactor: complete model-first terminal gui architecture"
```

---

## Final rollback strategy

- Security chunks are intentionally first and isolated. If later model-first work stalls, keep Chunks 1-2 and revert only Chunks 3+.
- For a bad application-boundary migration, revert `feat: introduce model-first application boundary` and all dependent commits, leaving worker/DiskPart fixes intact.
- For shell adapter regressions, revert Chunk 6+ while keeping reducer/effect model if useful; production remains at prior Terminal.Gui path with security hotfixes.
- For profile management regressions, revert Chunk 7 only; model-first preflight/execute/log flow remains intact.
- Before any revert, run `git status --short` and preserve uncommitted work with `git stash push -u -m "pre-rollback <chunk>"`.

---

## Definition of done

- Worker admin probe happens before journal open and request claim; non-admin does not consume/move/write pending request or existing journal.
- DiskPart scripts are created under the protected ProgramData workspace, verified, retained on the creation handle with only read sharing, and cleaned best-effort.
- `Vela.Application` owns immutable `TuiSessionState`, typed `TuiCommand`, pure `TuiReducer`, explicit `TuiEffect`, and effect ports.
- `TuiRuntime` and `TuiEffectRunner` orchestrate async preflight, impact, UAC, journal, history, and logs.
- Terminal.Gui shell binds state and emits commands; it does not own workflow state machine logic.
- `StaticFrameRenderer` handles redirected/startup static output only.
- Old `TuiApplication`, `SpectreTuiInput`, `SpectreTuiFrameSink`, and duplicate interactive state path are deleted.
- Core remains platform-neutral; Windows native capability remains in `Vela.Windows`.
- Preflight remains read-only; locked target flows through impact, two `Y/y`, operation request, worker fresh Lxss strict match, and execution.
- Display state excludes raw paths, RunId, native output, exception stacks, and internal enum names.
- Running selected target is shown as stop impact, not a blocker.
- Operation request schema, journal schema, terminal result semantics, and exit codes are unchanged.
- Tests cover layout sizes `160×45`, `120×35`, `100×30`, `80×24`, `60×16`, keyboard actions, security guards, and execution terminal states.
- Coverage gate passes at >=80% line coverage for `Vela.Core`, `Vela.Windows`, `Vela.Application`, and `Vela.Tui`.
