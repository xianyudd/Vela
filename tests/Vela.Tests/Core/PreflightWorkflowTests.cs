using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tests.Fakes;

namespace Vela.Tests.Core;

public sealed class PreflightWorkflowTests
{
    [Fact]
    public async Task ExecuteAsync_WithValidPreflight_RecordsReportAndJournalWithoutActions()
    {
        var trace = new List<string>();
        var installedInventory = CreateInventory("Ubuntu-24.04", WslDistributionState.Stopped);
        var runningInventory = CreateInventory("Ubuntu-24.04", WslDistributionState.Running);
        var resolution = CreateMatchedResolution();
        var inspection = CreateSucceededInspection(isSparse: true);
        var wslClient = new FakeWslClient
        {
            InstalledInventory = installedInventory,
            RunningInventory = runningInventory,
            OnInvoked = trace.Add
        };
        var resolver = new FakeLxssProfileResolver(resolution, trace.Add);
        var inspector = new FakeVhdxInspector(inspection, trace.Add);
        var journal = new FakeRunJournal(trace.Add);
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.True(result.IsSuccessful);
        Assert.Same(installedInventory, result.Preflight.InstalledInventory);
        Assert.Same(runningInventory, result.Preflight.RunningInventory);
        Assert.Same(resolution, result.Preflight.LxssResolution);
        Assert.Same(inspection, result.Preflight.VhdxInspection);
        Assert.Same(inspection.Snapshot, result.Summary.BeforeSnapshot);
        Assert.Null(result.Summary.AfterSnapshot);
        Assert.Empty(result.Diagnostics.Where(static diagnostic => diagnostic.Level == RunEventLevel.Error));
        Assert.Equal(
            ["wsl.installed", "lxss.resolve", "vhdx.inspect", "wsl.running", "journal.create"],
            trace.Take(5));
        Assert.Equal("journal.summary", trace[^1]);
        Assert.Equal(CreateRequest().RunId, Assert.Single(journal.CreatedRunIds));
        Assert.Contains(journal.Events, static @event => @event.Phase == RunPhase.Validation);
        Assert.Contains(journal.Events, static @event => @event.Phase == RunPhase.Inventory);
        Assert.Contains(journal.Events, static @event => @event.Phase == RunPhase.Snapshot);
        Assert.DoesNotContain(journal.Events, static @event => @event.Phase is RunPhase.Completed or RunPhase.Failed);
        Assert.All(journal.Events, @event => Assert.Equal(result.Summary.RunId, @event.RunId));
        Assert.Equal(
            Enumerable.Range(1, journal.Events.Length).Select(static sequence => (long)sequence),
            journal.Events.Select(static @event => @event.Sequence));
        var mappingEvent = Assert.Single(journal.Events.Where(static @event => @event.OperationName == "Lxss profile mapping"));
        Assert.Equal(
            ["D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx", "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx"],
            mappingEvent.Arguments.ToArray());
        Assert.Equal(TerminalResult.Succeeded, Assert.Single(journal.Summaries).TerminalResult);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidProfile_StopsBeforeReadAdapters()
    {
        var wslClient = new FakeWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution()) { ThrowOnCall = true };
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true)) { ThrowOnCall = true };
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest(vhdxPath: "Vela\\ext4.vhdx"));

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.False(result.Preflight.Validation.IsValid);
        Assert.Null(result.Preflight.InstalledInventory);
        Assert.Null(result.Preflight.LxssResolution);
        Assert.Null(result.Preflight.VhdxInspection);
        Assert.Null(result.Preflight.RunningInventory);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.ProfileValidationFailed);
        Assert.Equal(0, wslClient.InstalledInventoryCalls);
        Assert.Equal(0, wslClient.RunningInventoryCalls);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, inspector.CallCount);
        Assert.Equal(TerminalResult.ValidationFailed, Assert.Single(journal.Summaries).TerminalResult);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidRunRequest_StopsBeforeReadersAndDoesNotCreateJournal()
    {
        var wslClient = new FakeWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution()) { ThrowOnCall = true };
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true)) { ThrowOnCall = true };
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(
            CreateRequest(runId: Guid.Empty, intent: (OperationIntent)999));

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Equal(
            2,
            result.Diagnostics.Count(static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.RequestInvalid));
        Assert.Empty(journal.CreatedRunIds);
        Assert.Equal(0, wslClient.InstalledInventoryCalls);
        Assert.Equal(0, wslClient.RunningInventoryCalls);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, inspector.CallCount);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOpeningExistingPreflightRequest_ReturnsRequestInvalidAndWritesFailureSummary()
    {
        var wslClient = new FakeWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution()) { ThrowOnCall = true };
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true)) { ThrowOnCall = true };
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest(), RunJournalAccessMode.OpenExisting);

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.RequestInvalid);
        Assert.Equal(1, journal.OpenExistingRunCalls);
        Assert.Equal(TerminalResult.ValidationFailed, Assert.Single(journal.Summaries).TerminalResult);
        Assert.Equal(0, wslClient.InstalledInventoryCalls);
        Assert.Equal(0, wslClient.RunningInventoryCalls);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, inspector.CallCount);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Theory]
    [InlineData(true, WorkflowDiagnosticCode.InstalledInventoryFailed)]
    [InlineData(false, WorkflowDiagnosticCode.RunningInventoryFailed)]
    public async Task ExecuteAsync_WhenWslInventoryAdapterReturnsNull_FailsClosed(
        bool installedInventoryIsNull,
        WorkflowDiagnosticCode expectedCode)
    {
        var wslClient = new FakeWslClient
        {
            InstalledInventory = installedInventoryIsNull
                ? null!
                : CreateInventory("Ubuntu-24.04", WslDistributionState.Stopped),
            RunningInventory = installedInventoryIsNull
                ? CreateInventory("Ubuntu-24.04", WslDistributionState.Stopped)
                : null!
        };
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution())
        {
            ThrowOnCall = installedInventoryIsNull
        };
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true))
        {
            ThrowOnCall = installedInventoryIsNull
        };
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
        Assert.Equal(installedInventoryIsNull ? 0 : 1, resolver.CallCount);
        Assert.Equal(installedInventoryIsNull ? 0 : 1, inspector.CallCount);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWslInventoriesFail_ReturnsStructuredDiagnosticsAndAvoidsDependentReads()
    {
        var wslClient = new FakeWslClient { ThrowOnRead = true };
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution()) { ThrowOnCall = true };
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true)) { ThrowOnCall = true };
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.InstalledInventoryFailed);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.RunningInventoryFailed);
        Assert.Equal(1, wslClient.InstalledInventoryCalls);
        Assert.Equal(1, wslClient.RunningInventoryCalls);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, inspector.CallCount);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetDistroIsMissing_ReturnsPresentableFailureWithVhdxEvidence()
    {
        var wslClient = new FakeWslClient
        {
            InstalledInventory = CreateInventory("Debian", WslDistributionState.Stopped),
            RunningInventory = CreateInventory("Debian", WslDistributionState.Stopped)
        };
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution()) { ThrowOnCall = true };
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.DistroNotInstalled &&
                                 !string.IsNullOrWhiteSpace(diagnostic.Message));
        Assert.NotNull(result.Preflight.InstalledInventory);
        Assert.NotNull(result.Preflight.RunningInventory);
        Assert.Null(result.Preflight.LxssResolution);
        Assert.NotNull(result.Preflight.VhdxInspection?.Snapshot);
        Assert.Equal(1, wslClient.InstalledInventoryCalls);
        Assert.Equal(1, wslClient.RunningInventoryCalls);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(1, inspector.CallCount);
        Assert.Equal(TerminalResult.ValidationFailed, Assert.Single(journal.Summaries).TerminalResult);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRegistryMappingDiffers_ReturnsMappingFailureAndReadOnlyEvidence()
    {
        var resolution = new LxssProfileResolution(
            LxssResolutionStatus.Mismatched,
            "Ubuntu-24.04",
            "D:\\Registered\\Ubuntu24.04\\ext4.vhdx",
            "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx");
        var inspection = CreateSucceededInspection(isSparse: true);
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(resolution);
        var inspector = new FakeVhdxInspector(inspection);
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Same(resolution, result.Preflight.LxssResolution);
        Assert.Same(inspection, result.Preflight.VhdxInspection);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.LxssMappingMismatch &&
                                 diagnostic.Level == RunEventLevel.Error);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, inspector.CallCount);
        Assert.Equal(TerminalResult.ValidationFailed, Assert.Single(journal.Summaries).TerminalResult);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Theory]
    [InlineData("Ubuntu-24.04", "D:\\Registered\\Ubuntu24.04\\ext4.vhdx")]
    [InlineData("Debian", "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx")]
    public async Task ExecuteAsync_WhenMatchedMappingDoesNotStrictlyMatch_ReturnsValidationFailed(
        string resolvedDistroName,
        string resolvedVhdxPath)
    {
        var resolution = new LxssProfileResolution(
            LxssResolutionStatus.Matched,
            resolvedDistroName,
            resolvedVhdxPath,
            "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx");
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(resolution);
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.LxssMappingMismatch);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Theory]
    [InlineData(LxssResolutionStatus.NotFound, WorkflowDiagnosticCode.LxssResolutionNotFound)]
    [InlineData(LxssResolutionStatus.Failed, WorkflowDiagnosticCode.LxssResolutionFailed)]
    public async Task ExecuteAsync_WhenLxssResolutionCannotBeValidated_ReturnsStructuredFailure(
        LxssResolutionStatus status,
        WorkflowDiagnosticCode expectedCode)
    {
        var resolution = new LxssProfileResolution(status, "Ubuntu-24.04", null, null);
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(resolution);
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, inspector.CallCount);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLxssResolverThrows_ReturnsSafeFailureAndStillCollectsSnapshot()
    {
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution()) { ThrowOnCall = true };
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Equal(LxssResolutionStatus.Failed, result.Preflight.LxssResolution?.Status);
        Assert.NotNull(result.Preflight.VhdxInspection?.Snapshot);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.LxssResolutionFailed);
        Assert.DoesNotContain(
            "configured not to be read",
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)),
            StringComparison.OrdinalIgnoreCase);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenVhdxIsMissing_ReturnsValidationFailed()
    {
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(
            new VhdxInspectionResult(VhdxInspectionStatus.Missing, null));
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Equal(VhdxInspectionStatus.Missing, result.Preflight.VhdxInspection?.Status);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.VhdxMissing &&
                                 diagnostic.Phase == RunPhase.Snapshot);
        Assert.Equal(TerminalResult.ValidationFailed, Assert.Single(journal.Summaries).TerminalResult);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Theory]
    [InlineData(VhdxInspectionStatus.Failed)]
    public async Task ExecuteAsync_WhenVhdxInspectionFails_ReturnsStructuredFailure(VhdxInspectionStatus status)
    {
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(new VhdxInspectionResult(status, null));
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.VhdxInspectionFailed);
        Assert.DoesNotContain(
            "configured not to be read",
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)),
            StringComparison.OrdinalIgnoreCase);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenVhdxInspectorThrows_ReturnsStructuredFailure()
    {
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true)) { ThrowOnCall = true };
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Equal(VhdxInspectionStatus.Failed, result.Preflight.VhdxInspection?.Status);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.VhdxInspectionFailed);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSparseStateIsUnknown_ReturnsWarningAndKeepsSuccessfulSnapshot()
    {
        var inspection = CreateSucceededInspection(isSparse: null);
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(inspection);
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.Null(result.Preflight.VhdxInspection?.Snapshot?.IsSparse);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.SparseStateUnknown &&
                                 diagnostic.Level == RunEventLevel.Warning);
        Assert.Contains(
            journal.Events,
            static @event => @event.Level == RunEventLevel.Warning && @event.Output is not null);
        Assert.Equal(TerminalResult.Succeeded, Assert.Single(journal.Summaries).TerminalResult);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenJournalCreationFails_ReturnsSafeJournalDiagnosticWithoutActions()
    {
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal { ThrowOnCreate = true };
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        var diagnostic = Assert.Single(
            result.Diagnostics.Where(static item => item.Code == WorkflowDiagnosticCode.JournalFailure));
        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Equal(RunEventLevel.Error, diagnostic.Level);
        Assert.DoesNotContain("private journal detail", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.RunDirectory);
        Assert.Empty(journal.Summaries);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenJournalSummaryWriteFails_ReturnsSafeJournalDiagnosticWithoutActions()
    {
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal { ThrowOnWriteSummary = true };
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.JournalFailure &&
                                 diagnostic.Message == "Run diagnostics could not be persisted.");
        Assert.Equal(1, journal.SummaryWriteCalls);
        Assert.Empty(journal.Summaries);
        Assert.DoesNotContain(journal.Events, static @event => @event.Phase is RunPhase.Completed or RunPhase.Failed);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenJournalEventAppendFails_ReturnsJournalFailureAndPersistsFailureSummary()
    {
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal { ThrowOnAppend = true };
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.JournalFailure);
        Assert.Equal(1, journal.AppendCalls);
        Assert.Equal(TerminalResult.ValidationFailed, Assert.Single(journal.Summaries).TerminalResult);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenEvidenceAppendAndSummaryWriteFail_ReturnsFailureWithoutSuccessSummary()
    {
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal { ThrowOnAppend = true, ThrowOnWriteSummary = true };
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(CreateRequest());

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.JournalFailure);
        Assert.Equal(1, journal.AppendCalls);
        Assert.Equal(1, journal.SummaryWriteCalls);
        Assert.Empty(journal.Summaries);
        Assert.DoesNotContain(journal.Events, static @event => @event.Phase is RunPhase.Completed or RunPhase.Failed);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOpeningExistingCompactRun_SuccessOnlyAppendsEvidence()
    {
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(
            CreateRequest(intent: OperationIntent.Compact),
            RunJournalAccessMode.OpenExisting);

        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.Equal(1, journal.OpenExistingRunCalls);
        Assert.Empty(journal.CreatedRunIds);
        Assert.DoesNotContain(journal.Events, static @event => @event.Phase is RunPhase.Completed or RunPhase.Failed);
        Assert.Empty(journal.Summaries);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOpeningExistingCompactRunFailsPreflight_FinalizesValidationFailure()
    {
        var resolution = new LxssProfileResolution(
            LxssResolutionStatus.Mismatched,
            "Ubuntu-24.04",
            "D:\\Registered\\Ubuntu24.04\\ext4.vhdx",
            "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx");
        var wslClient = CreateTargetWslClient();
        var resolver = new FakeLxssProfileResolver(resolution);
        var inspector = new FakeVhdxInspector(CreateSucceededInspection(isSparse: true));
        var journal = new FakeRunJournal();
        var diskPartClient = new FakeDiskPartClient();
        var processRunner = new FakeProcessRunner();
        var workflow = CreateWorkflow(wslClient, resolver, inspector, journal);

        var result = await workflow.ExecuteAsync(
            CreateRequest(intent: OperationIntent.Compact),
            RunJournalAccessMode.OpenExisting);

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Equal(1, journal.OpenExistingRunCalls);
        Assert.Empty(journal.CreatedRunIds);
        Assert.Equal(TerminalResult.ValidationFailed, Assert.Single(journal.Summaries).TerminalResult);
        Assert.DoesNotContain(journal.Events, static @event => @event.Phase is RunPhase.Completed or RunPhase.Failed);
        AssertNoActions(wslClient, diskPartClient, processRunner);
    }

    [Fact]
    public void Constructor_DoesNotAcceptActionPorts()
    {
        var constructor = Assert.Single(typeof(PreflightWorkflow).GetConstructors());
        var parameterTypes = constructor.GetParameters().Select(static parameter => parameter.ParameterType);

        Assert.Contains(typeof(IWslInventoryReader), parameterTypes);
        Assert.DoesNotContain(typeof(IWslClient), parameterTypes);
        Assert.DoesNotContain(typeof(IDiskPartClient), parameterTypes);
        Assert.DoesNotContain(typeof(IProcessRunner), parameterTypes);
    }

    [Fact]
    public void AdapterOutcomeContracts_DoNotExposeDiagnosticMessage()
    {
        Assert.Null(typeof(LxssProfileResolution).GetProperty("DiagnosticMessage"));
        Assert.Null(typeof(VhdxInspectionResult).GetProperty("DiagnosticMessage"));
    }

    private static PreflightWorkflow CreateWorkflow(
        FakeWslClient wslClient,
        FakeLxssProfileResolver resolver,
        FakeVhdxInspector inspector,
        FakeRunJournal journal) => new(
        wslClient,
        resolver,
        inspector,
        journal,
        new FixedClock());

    private static FakeWslClient CreateTargetWslClient() => new()
    {
        InstalledInventory = CreateInventory("Ubuntu-24.04", WslDistributionState.Stopped),
        RunningInventory = CreateInventory("Ubuntu-24.04", WslDistributionState.Running)
    };

    private static OperationRequest CreateRequest(
        string? vhdxPath = null,
        Guid? runId = null,
        OperationIntent intent = OperationIntent.Preflight) => new(
        runId ?? Guid.Parse("58d25bb8-b714-4fa8-bc8c-11233c05c173"),
        new Profile(
            Guid.Parse("b5798574-bc95-4bf6-a09a-994934e58e8d"),
            "Ubuntu 24.04",
            "Ubuntu-24.04",
            vhdxPath ?? "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx",
            ShutdownMode.Global,
            TimeSpan.FromSeconds(45)),
        intent);

    private static WslInventory CreateInventory(string distroName, WslDistributionState state) => new(
        DateTimeOffset.UnixEpoch,
        ImmutableArray.Create(new WslDistribution(distroName, state, 2, true)));

    private static LxssProfileResolution CreateMatchedResolution() => new(
        LxssResolutionStatus.Matched,
        "Ubuntu-24.04",
        "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx",
        "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx");

    private static VhdxInspectionResult CreateSucceededInspection(bool? isSparse) => new(
        VhdxInspectionStatus.Succeeded,
        new VhdxSnapshot(
            DateTimeOffset.UnixEpoch,
            "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx",
            10_000L,
            DateTimeOffset.UnixEpoch,
            isSparse,
            new DriveSnapshot("D:\\", 1_000_000L, 500_000L)));

    private static void AssertNoActions(
        FakeWslClient wslClient,
        FakeDiskPartClient diskPartClient,
        FakeProcessRunner processRunner)
    {
        Assert.Equal(0, wslClient.ShutdownAllCalls);
        Assert.Equal(0, wslClient.TerminateDistroCalls);
        Assert.Equal(0, diskPartClient.TotalCalls);
        Assert.Equal(0, processRunner.InvocationCount);
    }

}
