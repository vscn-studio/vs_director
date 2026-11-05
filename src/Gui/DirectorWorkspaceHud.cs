using System.Globalization;
using System.Net;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Networking;
using VSDirector.Replay;
using VSDirector.Rendering;
using VSDirector.Runtime;
using VSDirector.Storage;

namespace VSDirector.Gui;

/// <summary>
/// The director workspace remains a HUD so the game view stays alive while the
/// user edits a shot. Large editors use a draggable floating window above the
/// core edge-docked panes.
/// </summary>
internal sealed partial class DirectorWorkspaceHud : GuiDialog
{
    private enum AutoSaveTarget
    {
        None,
        Camera,
        Follow,
        Sequence,
        ActionMetadata,
        ModelMetadata,
        ReplayMetadata,
        WorkflowCard,
        Path,
        PathKeyframe,
        PathMetadata
    }

    private enum AutoSaveState
    {
        Saved,
        Pending,
        Saving,
        Invalid
    }

    private const string ToolbarKey = "vsdirector-workspace-toolbar";
    private const string PreviewKey = "vsdirector-workspace-preview";
    private const string ResourceMenuKey = "vsdirector-workspace-resource-menu";
    private const string InspectorKey = "vsdirector-workspace-inspector";
    private const string TimelineKey = "vsdirector-workspace-timeline";
    private const string WorkflowCanvasKey = "vsdirector-workspace-workflow";
    private const int TimelineSteps = 10000;
    private const int ResourceRows = 8;
    private const int ResourceRowHeight = 58;
    private const int SequenceRowsPerPage = 7;
    private const int MinPreloadGridRadius = 3;
    private const int MaxPreloadGridRadius = 30;
    private const int MaxPreloadBrushRadius = 8;
    private const double ReplayBlockHeaderHeight = 30d;
    private const double ReplaySplitDividerHeight = 8d;
    private const double ReplayBlockMinimumContentHeight = 54d;
    private const double ReplayTimelineHeaderHeight = 36d;
    private const double ReplayTimelineRulerHeight = 36d;
    private const double ReplayTimelineTrackHeight = DirectorTimelineRows.DefaultHeight;
    private const double ReplayTimelineTicksPerSecond = DirectorReplayElementTimeline.TicksPerSecond;
    private const double DefaultTimelapseDurationSeconds = 5d;
    private const double MaxTimelapseDurationSeconds = 86400d;
    private const double ReplayTimelineBasePixelsPerTick = 20d;
    private static readonly int[] ReplayTimelineTickScaleLevels = { 30, 15, 10, 5, 0 };
    private const double ReplayTimelineLabelColumnExpansion = 24d;
    private const double ReplayTimelineLabelColumnBaseMinimum = 220d;
    private const double ReplayTimelineLabelColumnBaseMaximum = 280d;
    private const double ReplayPropertyPopupWidth = 400d;
    private const double ReplayPropertyLabelWidth = 126d;
    private const double ReplayPropertyControlX = 142d;
    private const double ReplayPropertyTextWidth = 226d;
    private const double ReplayPropertyNumberWidth = 150d;
    private const double ReplayPropertyDropDownWidth = 190d;
    private static readonly string[] ReplayElementNumericFields =
    {
        "time", "x", "y", "z", "dimension", "distance", "yaw", "pitch", "roll", "fov",
        "offsetX", "offsetY", "offsetZ", "viewOffsetX", "viewOffsetY", "viewOffsetZ",
        "followOffsetX", "followOffsetY", "followOffsetZ", "followDistance", "followHeight",
        "followOrbitAngle", "followOrbitSpeed", "followSmooth", "followDelay", "followFov",
        "frequencyX", "amplitudeX", "frequencyY", "amplitudeY", "timelapseTick",
        "timelapseDuration", "timeOfDay", "speed", "freezeDelay", "blockX", "blockY", "blockZ",
        "volume", "audioPitch"
    };
    private const double PreviewBorderThickness = 0d;
    private const double MinimumPreviewContentWidth = 320d;
    private const double MinimumPreviewContentHeight = 180d;
    private const double MinimumInspectorWidth = 300d;
    private const double MinimumBottomPanelHeight = 220d;
    private const double FloatingEditorHeaderHeight = 36d;
    private const float AutoSaveDelaySeconds = 0.7f;

    private readonly VSDirectorModSystem owner;
    private readonly DirectorWorkspaceStore store;
    private readonly DirectorSolidTextureRenderer solidTextureRenderer;
    private readonly DirectorTimelineMarkerRenderer timelineMarkerRenderer;
    private readonly DirectorWorkspaceFloatingEditorDialog floatingEditorDialog;
    private DirectorImGuiWindow? rmlInspectorWindow, rmlViewWindow;
    private readonly DirectorReplayExportDialog exportDialog;
    private DirectorWorkspaceDocument layout;
    private DirectorWorkspaceSection section;
    private Guid? selectedId;
    private DirectorWorkspaceSection? resourceMenuSection;
    private int resourceScrollOffset;
    private int outputResourceScrollOffset;
    private string search = string.Empty;
    private string newName = string.Empty;
    private string selectedPresetFile = string.Empty;
    private CameraPreset? cameraEdit;
    private CameraPath? pathEdit;
    private CameraSequence? sequenceEdit;
    private DirectorWorkflow? workflowEdit;
    private DirectorActionAsset? actionEdit;
    private Guid selectedActionLayerId;
    private string actionCategoryFilter = "all";
    private string actionTagsText = string.Empty;
    private string actionAuthorText = string.Empty;
    private string actionVersionText = "1.0.0";
    private string actionDescriptionText = string.Empty;
    private string actionPreviewActorKey = string.Empty;
    private string actionPreviewModelCode = string.Empty;
    private string actionPreviewSpeedText = "1";
    private string actionPreviewLoopCode = "loop";
    private string actionPreviewLoopCountText = "0";
    private string actionRecordStartText = "0";
    private string actionRecordEndText = "5";
    private string actionTrimStartText = "0";
    private string actionTrimEndText = "5";
    private string actionImportPathText = string.Empty;
    private string actionLayerWeightText = "1";
    private string actionLayerFadeInText = "0";
    private string actionLayerFadeOutText = "0";
    private string actionEventTimeText = "0";
    private string actionEventCodeText = string.Empty;
    private string actionEventTypeCode = "sound";
    private double actionTimelineZoom = 1d;
    private double actionTimelinePanSeconds;
    private double actionPlayhead;
    private ElementBounds? actionTimelineBounds;
    private readonly HashSet<ActionFrameSelection> selectedActionFrames = new();
    private readonly List<ActionFrameClipboardItem> actionFrameClipboard = new();
    private readonly DirectorActionEditHistory actionEditHistory = new();
    private string actionLibraryMode = "actions";
    private string actionInspectorSection = "basic";
    private string inspectorSection = "basic";
    private string actionTimelineToolSection = "playback";
    private string actionLibrarySearch = string.Empty;
    private string actionResourceSearch = string.Empty;
    private string actionLibrarySelectedModelCode = string.Empty;
    private int actionLibraryPageIndex;
    private DirectorModelAsset? modelEdit;
    private string modelNameText = string.Empty;
    private string modelCodeText = string.Empty;
    private string modelEntityCodeText = string.Empty;
    private string modelGroupText = string.Empty;
    private string modelDescriptionText = string.Empty;
    private string modelSizeText = "1";
    private string modelImportPathText = string.Empty;
    private string modelShapeAssetText = string.Empty;
    private string modelShapeSourceText = string.Empty;
    private bool workflowAudioEditorExpanded;
    private Guid selectedWorkflowAudioClipId;
    private Guid workflowAudioFieldsId;
    private bool workflowAudioCreating;
    private string workflowAudioPath = string.Empty;
    private string workflowAudioTimelineStart = "0";
    private string workflowAudioSourceStart = "0";
    private string workflowAudioDuration = "5";
    private string workflowAudioTrack = "0";
    private string workflowAudioGain = "0";
    private string workflowAudioFadeIn = "0";
    private string workflowAudioFadeOut = "0";
    private string workflowAudioRate = "1";
    private bool workflowAudioEnabled = true;
    private Guid selectedWorkflowCardId;
    private Guid contextWorkflowCardId;
    private bool consumeWorkflowContextMouseUp;
    private bool resourceMenuPointerCapture;
    private bool draggingWorkflowCard;
    private WorkflowCardDragMode workflowCardDragMode;
    private WorkflowScrollbarDragMode workflowScrollbarDragMode;
    private double workflowDragStartX;
    private double workflowDragStartY;
    private double workflowDragCardStart;
    private double workflowDragCardEnd;
    private int workflowDragLane;
    private WorkflowView workflowDragView;
    private double workflowSnapTime = double.NaN;
    private double workflowContextX;
    private double workflowContextY;
    private bool panningWorkflow;
    private double workflowPanStartX;
    private double workflowPanStartSeconds;
    private CameraFollowRig? followEdit;
    private bool suppressFollowPreview;
    private int selectedPathKeyframeIndex = -1;
    private string pathFrameStartText = "0";
    private string pathFrameDurationText = "1";
    private string pathSegmentStartSpeedText = "1";
    private string pathSegmentEndSpeedText = "1";
    private string pathSegmentMarkerText = string.Empty;
    private string pathTagsText = string.Empty;
    private string pathDescriptionText = string.Empty;
    private string pathInsertStartText = "0";
    private string pathInsertDurationText = "1";
    private Guid selectedSequenceCameraId;
    private int sequencePageIndex;
    private string replayRecordingName = string.Empty;
    private Guid replayMetadataFieldsId;
    private string replayMetadataName = string.Empty;
    private string replayMetadataTags = string.Empty;
    private string replayMetadataDescription = string.Empty;
    private Guid selectedReplayMarkerId;
    private Guid selectedReplaySpeedKeyframeId;
    private Guid selectedReplayElementKeyframeId;
    private Guid replayElementFieldsId;
    private readonly Dictionary<string, string> replayElementFieldValues = new(StringComparer.Ordinal);
    private ReplayTimelineElementKind? selectedReplayTimelineElementTrack;
    private Guid selectedReplayElementTrackId;
    private ReplayAuxiliaryKeyframeDragKind replayAuxiliaryKeyframeDragKind;
    private Guid replayAuxiliaryKeyframeDragId;
    private Guid replayMarkerFieldsId;
    private Guid replaySpeedFieldsId;
    private string replayMarkerLabelText = string.Empty;
    private string replayMarkerTimeText = "0";
    private string replaySpeedTimeText = "0";
    private string replaySpeedValueText = "1";
    private string replayBaseSpeedText = "1";
    private string replaySpeedInterpolationCode = "linear";
    private string cameraRightTab = "visuals";
    private string pathRightTab = "visuals";
    private string followRightTab = "visuals";
    private string replayRightTab = "visuals";
    private bool pathTotalDurationDirty;
    private Guid selectedReplayViewportClipId;
    private Guid replayViewportFieldsId;
    private string replayViewportNameText = string.Empty;
    private string replayViewportStartText = "0";
    private string replayViewportXText = "0";
    private string replayViewportYText = "0";
    private string replayViewportZText = "0";
    private string replayViewportDimensionText = "0";
    private string replayViewportYawText = "0";
    private string replayViewportPitchText = "0";
    private string replayViewportRollText = "0";
    private string replayViewportInterpolationCode = "ease";
    private string replayElementTypeCode = "element-camera";
    private ReplayFloatingEditorKind replayFloatingEditorKind;
    private Guid replayFloatingTrackId;
    private string replayFloatingTrackName = string.Empty;
    private string replayFloatingSceneName = string.Empty;
    private double replayFloatingX = 180d;
    private double replayFloatingY = 120d;
    private bool replayElementTrackReordering;
    private bool replayViewportFreeCameraPointerActive;
    private Guid replayElementTrackReorderId;
    private int replayElementTrackReorderIndex = -1;
    private bool replayAuxiliaryKeyframeDragMoved;
    private bool replayViewportDragMoved;
    private ReplayViewportDragMode replayViewportDragMode;
    private Guid replayViewportDragClipId;
    private double replayViewportDragStartMouseX;
    private double replayViewportDragOriginalStart;
    private double replayViewportDragOriginalDuration;
    private double replayViewportDragOriginalSourceStart;
    private DirectorReplayViewportTimeline? replayViewportDraftTimeline;
    private string selectedReplayActor = string.Empty;
    private Guid replayActorReplayId;
    private string replayViewMode = "free";
    private Guid replayViewSourceId;
    private double replayViewStartSeconds;
    private string replayInspectorSection = "info";
    private string replayViewInspectorSection = "basic";
    private int replayViewResourceScrollOffset;
    private int replayResourceRows = 1;
    private int replayViewResourceRows = 1;
    private int replaySequenceRowsPerPage = SequenceRowsPerPage;
    private InspectorSectionTarget composingInspectorSectionTarget;
    private Guid replayCapturePathId;
    private bool replayViewConfigured;
    private bool replayViewApplyQueued;
    private Guid replayViewAppliedReplayId;
    private string recordingTrackName = string.Empty;
    private string selectedRecordingSource = string.Empty;

    private Guid selectedRecordingTrackId;
    private Guid? pendingDeleteId;
    private bool recordingConfirmationPending;
    private bool scrubbingReplay;
    private double replayTimelineScrollTicks;
    private int replayTimelineTickScaleIndex;
    private int replayTimelineFirstVisibleTrack;
    private bool replayTimelinePanning;
    private int replayTimelinePanStartMouseX;
    private double replayTimelinePanStartTicks;
    private ReplayExportRangeDragMode replayExportRangeDragMode;
    private DirectorReplayViewportTimeline? replayExportRangeDraftTimeline;
    private double replayExportRangeStart;
    private double replayExportRangeEnd;
    private double replayKeyframeDragTime = double.NaN;
    private readonly HashSet<int> selectedPathKeyframes = new();
    private readonly List<CameraKeyframe> pathKeyframeClipboard = new();
    private readonly CameraPathEditHistory pathEditHistory = new();
    private string pathCurveChannelCode = "x";
    private string pathBatchScopeCode = "selected";
    private string pathBatchPropertyCode = "fov";
    private string pathBatchInterpolationCode = "default";
    private string pathBatchValueText = "120";
    private PathWorkspaceTool pathWorkspaceTool;
    private bool draggingPathEditorKeyframes;
    private bool pathEditorDragValuesOnly;
    private bool selectingPathEditorRange;
    private bool pathEditorRangeAdditive;
    private bool pathEditorRangeMoved;
    private bool panningPathEditorTimeline;
    private bool draggingPathEditorPlayhead;
    private bool draggingWorldPathKeyframe;
    private bool worldPathVerticalDrag;
    private bool rotatingActionPreview;
    private int actionPreviewDragMouseX;
    private PathWorldDragTarget pathWorldDragTarget;
    private int stretchingPathEditorSegment = -1;
    private int pathEditorDragStartMouseX;
    private int pathEditorDragStartMouseY;
    private double pathEditorDragStartView;
    private double pathEditorDragValueMinimum;
    private double pathEditorDragValueMaximum;
    private double pathEditorBoxStartTime;
    private double pathEditorBoxEndTime;
    private int pathEditorBoxEndMouseY;
    private double pathEditorPlayhead;
    private bool pathEditorPlayheadWasPositioned;
    private double pathEditorHoverTime = double.NaN;
    private double pathWorldDragStartY;
    private readonly HashSet<int> pathEditorRangeBaseSelection = new();
    private Dictionary<int, double> pathEditorDragTimes = new();
    private Dictionary<int, double> pathEditorDragValues = new();
    private CameraPath? pendingPathEditHistory;
    private double previewReplayTime;
    private double previewPathTime;
    private double preloadReplayTime;
    private int preloadGridRadius = 6;
    private int preloadBrushRadius;
    private bool preloadEraseMode;
    private bool preloadEditorExpanded;
    private bool workflowEditorWindowOpen;
    private DirectorPreloadRmlWindow? preloadRmlWindow;
    private bool selectingPreloadGrid;
    private bool panningPreloadGrid;
    private bool preloadGridChanged;
    private int preloadSelectionStartX;
    private int preloadSelectionStartZ;
    private int preloadSelectionEndX;
    private int preloadSelectionEndZ;
    private int preloadPanChunkX;
    private int preloadPanChunkZ;
    private int preloadPanStartMouseX;
    private int preloadPanStartMouseY;
    private int preloadPanStartChunkX;
    private int preloadPanStartChunkZ;
    private Guid preloadDraftReplayId;
    private HashSet<DirectorReplayPreloadColumn> preloadDraftIncluded = new();
    private HashSet<DirectorReplayPreloadColumn> preloadDraftExcluded = new();
    private float dynamicAccumulator;
    private float layoutSaveAccumulator;
    private AutoSaveTarget pendingAutoSaveTarget;
    private AutoSaveState autoSaveState = AutoSaveState.Saved;
    private float autoSaveAccumulator;
    private Guid pendingAutoSaveResourceId;
    private Guid pendingAutoSaveChildId;
    private int pendingAutoSaveIndex = -1;
    private AutoSaveTarget autoSaveStatusTarget;
    private Guid autoSaveStatusResourceId;
    private Guid autoSaveStatusChildId;
    private int autoSaveStatusIndex = -1;
    private string autoSaveError = string.Empty;
    private bool suppressAutoSave;
    private bool paneSplitDragging;
    private ReplaySplitDragTarget replaySplitDragTarget;
    private float replaySplitRecomposeAccumulator;
    private bool replaySplitRecomposePending;
    private DragState? drag;
    private SnapPreview? snapPreview;
    private ElementBounds? replayTimelineBounds;
    private ElementBounds? replaySequenceTimelineBounds;
    private ElementBounds? replayPathViewTimelineBounds;
    private ElementBounds? replayResourceSplitBounds;
    private ElementBounds? replayInspectorSplitBounds;
    private ElementBounds? replayResourceTopBounds;
    private ElementBounds? replayResourceViewBounds;
    private ElementBounds? replayInspectorTopBounds;
    private ElementBounds? replayInspectorViewBounds;
    private double replayInspectorBlockBottom = double.PositiveInfinity;
    private ElementBounds? pathTimelineBounds;
    private ElementBounds? pathGlobalTimelineBounds;
    private ElementBounds? pathEditorTimelineBounds;
    private ElementBounds? workflowBoardBounds;
    private ElementBounds? preloadGridBounds;
    private DirectorRmlConfirmWindow? invalidWorkflowDialog;
    private bool routingFloatingEditorInput;
    private bool disposed;
    private Guid synchronizedActiveReplayId;
    private readonly DirectorImGuiWorkspace imGuiWorkspace;

    public DirectorWorkspaceHud(ICoreClientAPI capi, VSDirectorModSystem owner, string worldId)
        : base(capi)
    {
        this.owner = owner;
        imGuiWorkspace = new DirectorImGuiWorkspace(capi);
        floatingEditorDialog = new DirectorWorkspaceFloatingEditorDialog(capi, this);
        exportDialog = new DirectorReplayExportDialog(capi, owner, this);
        solidTextureRenderer = new DirectorSolidTextureRenderer(capi);
        timelineMarkerRenderer = new DirectorTimelineMarkerRenderer(capi);
        store = new DirectorWorkspaceStore(capi, worldId);
        DirectorWorkspaceDocument? storedLayout = store.Load();
        bool initializePreview = storedLayout is null
            || storedLayout.SchemaVersion < DirectorWorkspaceDocument.CurrentSchemaVersion
            || storedLayout.Panels?.Any(panel => string.Equals(
                panel.Id,
                "preview",
                StringComparison.OrdinalIgnoreCase)) != true;
        layout = DirectorWorkspaceDocument.Normalize(storedLayout);
        imGuiWorkspace.BoundsChanged += SynchronizeNativePanel;
        if (initializePreview)
        {
            LayoutPreviewAtTopLeft();
        }
        NormalizeLayoutToViewport();
        section = IsSectionAvailable(layout.ActiveSection)
            ? layout.ActiveSection
            : DirectorWorkspaceSection.Cameras;
        workflowEditorWindowOpen = section == DirectorWorkspaceSection.Workflows;
        layout.ActiveSection = section;
        selectedRecordingTrackId = owner.AssignedRecordingTrackId
            ?? owner.RecordingTracks.FirstOrDefault()?.Id
            ?? Guid.Empty;
        previewReplayTime = owner.ReplayTimeSeconds;
        SynchronizeReplaySelectionWithActivePlayback(force: true);
        ComposeAll();
    }

    public override string? ToggleKeyCombinationCode => null;

    public override EnumDialogType DialogType => EnumDialogType.HUD;

    public override double DrawOrder => 0.34d;

    public override bool UnregisterOnClose => true;

    public override bool PrefersUngrabbedMouse => !layout.NavigationMode;

    public override bool DisableMouseGrab => !layout.NavigationMode;

    public override bool ShouldReceiveKeyboardEvents() => IsOpened() && !layout.NavigationMode;

    public override bool ShouldReceiveMouseEvents() => IsOpened() && !layout.NavigationMode;

    internal bool ShouldRouteFloatingEditorInput
        => !disposed && IsOpened() && !layout.NavigationMode;

    internal void RouteFloatingEditorMouseDown(MouseEvent args)
        => RouteFloatingEditorInput(() => OnMouseDown(args));

    internal void RouteFloatingEditorMouseMove(MouseEvent args)
        => RouteFloatingEditorInput(() => OnMouseMove(args));

    internal void RouteFloatingEditorMouseUp(MouseEvent args)
        => RouteFloatingEditorInput(() => OnMouseUp(args));

    internal void RouteFloatingEditorMouseWheel(MouseWheelEventArgs args)
        => RouteFloatingEditorInput(() => OnMouseWheel(args));

    private void RouteFloatingEditorInput(Action route)
    {
        if (disposed || !IsOpened())
        {
            return;
        }

        bool previous = routingFloatingEditorInput;
        routingFloatingEditorInput = true;
        try
        {
            route();
        }
        finally
        {
            routingFloatingEditorInput = previous;
        }
    }

    private bool ShouldHandleWorkspacePointerInteraction
        => !routingFloatingEditorInput || HasActiveFloatingPointerInteraction();

    public bool IsShowing(DirectorWorkspaceSection target) => section == target;

    public bool ToggleInteractionMode()
    {
        if (!IsOpened())
        {
            return false;
        }

        return ToggleNavigationMode();
    }

    internal bool EnterReplayNavigationMode()
    {
        if (!IsOpened())
        {
            return false;
        }

        return EnterNavigationMode();
    }

    internal bool KeepReplayWorkspaceVisible()
    {
        if (!IsOpened())
        {
            return false;
        }

        if (layout.NavigationMode)
        {
            layout.NavigationMode = false;
            SynchronizeNavigationInput();
            UpdateGameViewportRuntime();
            SaveLayout();
            ComposeToolbar();
        }
        owner.SetWorkspaceInputOverride(true);
        return true;
    }

    public void ToggleOpen()
    {
        if (IsOpened())
        {
            TryClose();
            return;
        }

        TryOpen(withFocus: false);
    }

    public void ShowSection(DirectorWorkspaceSection target)
    {
        if (!IsSectionAvailable(target))
        {
            return;
        }
        if (section == target && IsOpened())
        {
            TryClose();
            return;
        }

        PreserveInputs();
        PersistWorkflowDraft();
        section = target;
        if (target == DirectorWorkspaceSection.Replay)
        {
            replayViewMode = "free";
            replayViewSourceId = Guid.Empty;
            replayViewConfigured = false;
            replayViewAppliedReplayId = Guid.Empty;
        }
        inspectorSection = DefaultInspectorSection(target);
        layout.ActiveSection = target;
        resourceScrollOffset = 0;
        outputResourceScrollOffset = 0;
        selectedId = null;
        ClearEditors();
        SynchronizeReplaySelectionWithActivePlayback(force: true);
        workflowEditorWindowOpen = target == DirectorWorkspaceSection.Workflows;
        ComposeAll();
        if (!IsOpened())
        {
            TryOpen(withFocus: false);
        }
    }

    public void ShowPath(Guid pathId)
    {
        if (section != DirectorWorkspaceSection.Paths)
        {
            PreserveInputs();
            PersistWorkflowDraft();
            section = DirectorWorkspaceSection.Paths;
            inspectorSection = DefaultInspectorSection(section);
            layout.ActiveSection = section;
            resourceScrollOffset = 0;
            selectedId = null;
            ClearEditors();
        }
        SelectPath(pathId);
        if (!IsOpened())
        {
            TryOpen(withFocus: false);
        }
    }

    public void Refresh()
    {
        // The native ImGui layer can dispose the inspector before our LeaveWorld handler. Pending edits
        // still save on close, but their callbacks must not rebuild destroyed documents.
        if (disposed || owner.IsShuttingDown || rmlInspectorWindow?.Document?.IsDisposed == true) return;
        PreserveInputs();
        SynchronizeReplaySelectionWithActivePlayback(force: false);
        ComposeAll();
    }

    internal void RefreshVsmc2Model(DirectorModelAsset model)
    {
        if (modelEdit?.Id != model.Id)
        {
            return;
        }
        modelEdit = model.DeepClone();
        modelShapeAssetText = modelEdit.ShapeAssetLocation;
        modelShapeSourceText = modelEdit.ShapeSourcePath;
        ComposeInspector();
        ComposeWorkflowCanvas();
    }

    public void CloseAll() => TryClose();

    public override bool OnEscapePressed()
    {
        if (DirectorToolWindows.HasOpenTool) { DirectorToolWindows.Close(); return true; }
        if (resourceMenuSection is not null) { CloseResourceMenu(); return true; }
        return TryClose();
    }

    public override void OnGuiOpened()
    {
        imGuiWorkspace.Open();
        DirectorToolWindows.CloseWorkspaceMenu = CloseResourceMenu;
        if (SynchronizeReplaySelectionWithActivePlayback(force: true))
        {
            ComposeAll();
        }
        owner.SetWorkspaceInputOverride(!layout.NavigationMode);
        base.OnGuiOpened();
        floatingEditorDialog.OpenForWorkspace();
        ComposeToolbar();
        ComposeResources();
        ComposeInspector();
        UpdateGameViewportRuntime();
        ComposeTimeline();
    }

    public override void OnGuiClosed()
    {
        imGuiWorkspace.Close();
        waveformCancellation?.Cancel();
        waveformKey = "";
        rmlTimeline?.Close();
        DisposeKeyframeRml();
        preloadRmlWindow?.Dispose(); preloadRmlWindow = null;
        rmlInspectorWindow?.Close();
        rmlViewWindow?.Close();
        rmlToolbarWindow?.Close();
        rmlResourceWindow?.Close();
        sceneBrowser?.Close();
        DirectorToolWindows.Close();
        DirectorToolWindows.CloseWorkspaceMenu = null;
        EndReplayViewportFreeCameraPointerInput();
        ResetWorkspacePointerInteraction();
        FlushPendingAutoSave();
        PreserveInputs();
        PersistWorkflowDraft();
        resourceMenuPointerCapture = false;
        resourceMenuSection = null;
        invalidWorkflowDialog?.TryClose();
        floatingEditorDialog.TryClose();
        exportDialog.TryClose();
        owner.StopActionPreview();
        layout.NavigationMode = false;
        owner.SetWorkspaceInputOverride(false);
        DirectorWorkspaceViewportRuntime.Deactivate(this);
        SaveLayout();
        base.OnGuiClosed();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        CheckPaneSplitRelease();
        UpdateRmlTimelineFrame();
        UpdateInspectorBounds();
        UpdateGameViewportRuntime();
        var previewPanel = layout.Panel("preview");
        imGuiWorkspace.SetPreview(previewPanel.X, previewPanel.Y, previewPanel.Width, previewPanel.Height);
        DirectorWorkspaceViewportRuntime.UseFullScreenUi(capi);
        float elapsed = float.IsFinite(deltaTime) && deltaTime > 0f ? deltaTime : 0f;
        dynamicAccumulator += elapsed;
        layoutSaveAccumulator += elapsed;
        if (pendingAutoSaveTarget != AutoSaveTarget.None)
        {
            autoSaveAccumulator += elapsed;
            if (autoSaveAccumulator >= AutoSaveDelaySeconds)
            {
                FlushPendingAutoSave();
            }
        }
        // Dock bounds and retained timeline nodes update in place while resizing.
        // Rebuilding all documents here used to interrupt capture and stall the pointer.
        if (replaySplitDragTarget != ReplaySplitDragTarget.None && replaySplitRecomposePending)
        {
            replaySplitRecomposeAccumulator += float.IsFinite(deltaTime) && deltaTime > 0f ? deltaTime : 0f;
            if (replaySplitRecomposeAccumulator >= 0.04f)
            {
                replaySplitRecomposeAccumulator = 0f;
                replaySplitRecomposePending = false;
                if (replaySplitDragTarget == ReplaySplitDragTarget.Resources)
                {
                    ComposeResources();
                }
                else
                {
                    ComposeInspector();
                }
            }
        }
        if (dynamicAccumulator >= 0.1f)
        {
            dynamicAccumulator = 0f;
            UpdateRuntimeUi();
        }

        if (layoutSaveAccumulator >= 0.5f)
        {
            layoutSaveAccumulator = 0f;
            SaveLayout();
        }

        RenderWorkspaceBackdrop();
        base.OnRenderGUI(deltaTime);
        RenderPaneIntersectionHandle();
        // Vanilla overlays with a higher draw order (escape menu, held-item
        // information and hover text) still belong to the game viewport.
        DirectorWorkspaceViewportRuntime.UseGameHudViewport();
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (args.Handled)
        {
            return;
        }

        if (!routingFloatingEditorInput && IsWorkspaceDropDownOpen())
        {
            base.OnMouseDown(args);
            return;
        }

        if (!routingFloatingEditorInput
            && resourceMenuSection is not null
            && !PointInsideResourceMenu(args.X, args.Y)
            && HoveredToolbarResourceSection(args.X, args.Y) is null)
        {
            CloseResourceMenu();
        }

        if (!routingFloatingEditorInput && RouteResourceMouseDown(args))
        {
            return;
        }

        // The reorder handle is rendered as a chromeless button, but its
        // press starts a row drag rather than a button click. Handle this
        // canvas interaction before routing the timeline composer; otherwise
        // the button capture layer consumes the press and the reorder state
        // is never entered.
        if (!routingFloatingEditorInput
            && args.Button == EnumMouseButton.Left
            && TryBeginReplayElementTrackReorder(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        // The docked timeline has custom canvas hit testing below. Give its
        // actual controls first chance to consume the press; otherwise a
        // canvas hit can swallow transport and tool buttons before the
        // GuiComposer sees them.
        if (!routingFloatingEditorInput && RouteTimelineMouseDown(args))
        {
            return;
        }

        if (contextWorkflowCardId != Guid.Empty)
        {
            if (WorkflowContextMenuContains(args.X, args.Y))
            {
                if (!routingFloatingEditorInput)
                {
                    base.OnMouseDown(args);
                }
                args.Handled = true;
                return;
            }

            contextWorkflowCardId = Guid.Empty;
            consumeWorkflowContextMouseUp = true;
            ComposeWorkflowCanvas();
            args.Handled = true;
            return;
        }

        bool insideFloatingEditor = PointInsideFloatingEditor(args.X, args.Y);
        if (routingFloatingEditorInput && !insideFloatingEditor)
        {
            return;
        }

        if (!insideFloatingEditor
            && args.Button == EnumMouseButton.Left
            && TryBeginReplaySplitDrag(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        if (!insideFloatingEditor
            && args.Button == EnumMouseButton.Left
            && TryBeginPaneSplitDrag(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        if (preloadGridBounds?.PointInside(args.X, args.Y) == true)
        {
            if (args.Button == EnumMouseButton.Left)
            {
                BeginPreloadGridSelection(args.X, args.Y);
                args.Handled = true;
                return;
            }
            if (args.Button is EnumMouseButton.Middle or EnumMouseButton.Right)
            {
                BeginPreloadGridPan(args.X, args.Y);
                args.Handled = true;
                return;
            }
        }

        if (section == DirectorWorkspaceSection.Actions
            && actionEdit is not null
            && actionTimelineBounds?.PointInside(args.X, args.Y) == true
            && args.Button == EnumMouseButton.Left)
        {
            double localX = args.X - actionTimelineBounds.renderX;
            double localY = args.Y - actionTimelineBounds.renderY;
            double labelWidth = Math.Min(GuiElement.scaled(130d), actionTimelineBounds.OuterWidth * 0.24d);
            double rulerHeight = GuiElement.scaled(28d);
            DirectorActionLayer? clickedLayer = null;
            if (localY >= rulerHeight && actionEdit.Layers.Count > 0)
            {
                double laneHeight = Math.Max(
                    GuiElement.scaled(34d),
                    (actionTimelineBounds.OuterHeight - rulerHeight) / actionEdit.Layers.Count);
                int layerIndex = Math.Clamp((int)((localY - rulerHeight) / laneHeight), 0, actionEdit.Layers.Count - 1);
                clickedLayer = actionEdit.Layers[layerIndex];
                selectedActionLayerId = clickedLayer.Id;
                SyncSelectedActionLayerInputs();
            }
            if (localX >= labelWidth)
            {
                double fraction = Math.Clamp(
                    (localX - labelWidth) / Math.Max(1d, actionTimelineBounds.OuterWidth - labelWidth),
                    0d,
                    1d);
                actionPlayhead = Math.Clamp(
                    actionTimelinePanSeconds + ActionTimelineVisibleDuration * fraction,
                    0d,
                    actionEdit.DurationSeconds);
                actionEventTimeText = actionPlayhead.ToString("0.###", CultureInfo.InvariantCulture);
                ActionFrameSelection? hit = clickedLayer is null
                    ? null
                    : FindActionFrameAt(clickedLayer, args.X, actionTimelineBounds);
                if (!IsShiftDown())
                {
                    selectedActionFrames.Clear();
                }
                if (hit is ActionFrameSelection selection)
                {
                    if (!selectedActionFrames.Add(selection) && IsShiftDown())
                    {
                        selectedActionFrames.Remove(selection);
                    }
                }
            }
            ComposeInspector();
            ComposeTimeline();
            args.Handled = true;
            return;
        }

        if (section == DirectorWorkspaceSection.Paths
            && pathEdit is not null
            && pathEditorTimelineBounds?.PointInside(args.X, args.Y) == true)
        {
            if (args.Button is EnumMouseButton.Middle or EnumMouseButton.Right)
            {
                panningPathEditorTimeline = true;
                pathEditorDragStartMouseX = args.X;
                pathEditorDragStartView = PathEditorViewStart;
                args.Handled = true;
                return;
            }
            if (args.Button == EnumMouseButton.Left)
            {
                BeginPathEditorTimelineInteraction(args.X, args.Y, IsShiftDown());
                args.Handled = true;
                return;
            }
        }

        if (section == DirectorWorkspaceSection.Paths
            && pathEdit is not null
            && pathGlobalTimelineBounds?.PointInside(args.X, args.Y) == true
            && args.Button == EnumMouseButton.Left)
        {
            draggingPathEditorPlayhead = true;
            SetPathEditorPlayhead(PathGlobalTimeAt(args.X), centerView: true);
            args.Handled = true;
            return;
        }

        if (section == DirectorWorkspaceSection.Workflows
            && workflowBoardBounds?.PointInside(args.X, args.Y) == true)
        {
            DirectorWorkflowCard? card = WorkflowCardAt(args.X, args.Y);
            DirectorTimelineAudioClip? audioClip = WorkflowAudioClipAt(args.X, args.Y);
            if (args.Button == EnumMouseButton.Right)
            {
                contextWorkflowCardId = card?.Id ?? Guid.Empty;
                double guiScale = Math.Max(0.001d, GuiElement.scaled(1d));
                workflowContextX = Math.Clamp(
                    (args.X - workflowBoardBounds.absX) / guiScale,
                    6d,
                    Math.Max(6d, workflowBoardBounds.OuterWidth / guiScale - 146d));
                workflowContextY = Math.Clamp(
                    (args.Y - workflowBoardBounds.absY) / guiScale,
                    6d,
                    Math.Max(6d, workflowBoardBounds.OuterHeight / guiScale - 42d));
                ComposeWorkflowCanvas();
                args.Handled = true;
                return;
            }

            if (args.Button == EnumMouseButton.Middle)
            {
                contextWorkflowCardId = Guid.Empty;
                panningWorkflow = true;
                workflowPanStartX = args.X;
                workflowPanStartSeconds = layout.WorkflowPanSeconds;
                args.Handled = true;
                return;
            }

            if (args.Button == EnumMouseButton.Left && TryBeginWorkflowScrollbarDrag(args.X, args.Y))
            {
                contextWorkflowCardId = Guid.Empty;
                args.Handled = true;
                return;
            }

            if (args.Button == EnumMouseButton.Left && card is not null)
            {
                FlushPendingAutoSave(AutoSaveTarget.WorkflowCard);
                selectedWorkflowCardId = card.Id;
                inspectorSection = "card";
                contextWorkflowCardId = Guid.Empty;
                draggingWorkflowCard = true;
                workflowDragStartX = args.X;
                workflowDragStartY = args.Y;
                workflowDragCardStart = card.StartSeconds;
                workflowDragCardEnd = card.EndSeconds;
                workflowDragLane = card.Lane;
                workflowDragView = CurrentWorkflowView(workflowEdit!);
                workflowCardDragMode = WorkflowCardDragModeAt(card, args.X);
                workflowSnapTime = double.NaN;
                ComposeInspector();
                ComposeWorkflowCanvas();
                args.Handled = true;
                return;
            }

            if (args.Button == EnumMouseButton.Left && audioClip is not null)
            {
                selectedWorkflowAudioClipId = audioClip.Id;
                workflowAudioFieldsId = Guid.Empty;
                workflowAudioCreating = false;
                workflowAudioEditorExpanded = true;
                inspectorSection = "audio";
                SyncWorkflowAudioSelection();
                ComposeInspector();
                ComposeWorkflowCanvas();
                args.Handled = true;
                return;
            }

            if (args.Button == EnumMouseButton.Left)
            {
                contextWorkflowCardId = Guid.Empty;
                ComposeWorkflowCanvas();
                args.Handled = true;
                return;
            }
        }

        if (insideFloatingEditor)
        {
            args.Handled = true;
            return;
        }

        if (args.Button == EnumMouseButton.Right
            && TryOpenReplayEntityPopup(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        if (args.Button == EnumMouseButton.Left
            && section == DirectorWorkspaceSection.Actions
            && owner.HasActionPreviewVisual
            && PointInsideGameViewport(args.X, args.Y))
        {
            rotatingActionPreview = true;
            actionPreviewDragMouseX = args.X;
            args.Handled = true;
            return;
        }

        if (args.Button == EnumMouseButton.Left
            && section == DirectorWorkspaceSection.Replay
            && owner.IsReplayPlaying
            && PointInsideGameViewport(args.X, args.Y)
            && owner.TryEnableReplayFreeCamera(out _))
        {
            replayViewportFreeCameraPointerActive = true;
            owner.SetReplayViewportFreeCameraPointerActive(true);
            args.Handled = true;
            return;
        }

        if (args.Button == EnumMouseButton.Middle
            && section == DirectorWorkspaceSection.Replay
            && ReplayTimelineInteractionContains(args.X, args.Y)
            && !ReplayTimelineAddRowContains(args.Y))
        {
            replayTimelinePanning = true;
            replayTimelinePanStartMouseX = args.X;
            replayTimelinePanStartTicks = replayTimelineScrollTicks;
            args.Handled = true;
            return;
        }

        if (section == DirectorWorkspaceSection.Paths
            && TryBeginPathTimelineMouseDown(args))
        {
            args.Handled = true;
            return;
        }

        if (args.Button != EnumMouseButton.Left)
        {
            base.OnMouseDown(args);
            return;
        }

        if (replaySequenceTimelineBounds?.PointInside(args.X, args.Y) == true)
        {
            SelectReplaySequenceShotAt(args.X);
            args.Handled = true;
            return;
        }

        if (replayPathViewTimelineBounds?.PointInside(args.X, args.Y) == true)
        {
            int keyframeIndex = FindReplayPathViewKeyframeAt(args.X);
            if (keyframeIndex >= 0)
            {
                JumpReplayToPathKeyframe(keyframeIndex);
            }
            args.Handled = true;
            return;
        }

        if (args.Button == EnumMouseButton.Left
            && TrySelectReplayViewportTrack(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        if (ReplayTimelineInteractionContains(args.X, args.Y)
            && !ReplayTimelineAddRowContains(args.Y))
        {
            if (TryBeginReplayExportRangeDrag(args.X, args.Y))
            {
                args.Handled = true;
                return;
            }
            if (TryBeginReplayViewportDrag(args.X, args.Y))
            {
                args.Handled = true;
                return;
            }
            if (FindReplayElementKeyframeAt(args.X, args.Y) is Guid elementKeyframeId)
            {
                BeginReplayAuxiliaryKeyframeDrag(
                    ReplayAuxiliaryKeyframeDragKind.Element,
                    elementKeyframeId);
                args.Handled = true;
                return;
            }
            if (!owner.IsReplayPlaying || !ReplayTimelineRulerContains(args.X, args.Y))
            {
                args.Handled = true;
                return;
            }

            owner.PauseReplayForTimelineScrub();
            scrubbingReplay = true;
            previewReplayTime = ReplayTimeAt(args.X);
            args.Handled = true;
            return;
        }

        if (section == DirectorWorkspaceSection.Paths && pathEdit is not null)
        {
            CameraPath before = pathEdit.DeepClone();
            if (TryBeginWorldPathKeyframeDrag(args.X, args.Y, IsShiftDown()))
            {
                pendingPathEditHistory = before;
                args.Handled = true;
                return;
            }
        }

        base.OnMouseDown(args);
    }

    public override void OnMouseMove(MouseEvent args)
    {
        CheckPaneSplitRelease();
        if (DirectorSliderInputCapture.Captured is GuiElementDirectorSlider slider)
        {
            slider.OnMouseMove(capi, args);
            args.Handled = true;
            return;
        }

        if (args.Handled)
        {
            return;
        }

        if (routingFloatingEditorInput
            && !HasActiveFloatingPointerInteraction()
            && !PointInsideFloatingEditor(args.X, args.Y))
        {
            return;
        }

        if (!routingFloatingEditorInput && IsWorkspaceDropDownOpen())
        {
            base.OnMouseMove(args);
            return;
        }

        if (!routingFloatingEditorInput && RouteResourceMouseMove(args))
        {
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replaySplitDragTarget != ReplaySplitDragTarget.None)
        {
            UpdateReplaySplitDrag(args.Y);
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction && rotatingActionPreview)
        {
            int deltaX = args.X - actionPreviewDragMouseX;
            actionPreviewDragMouseX = args.X;
            if (deltaX != 0)
            {
                owner.RotateActionPreview(deltaX * 0.0125d);
            }
            args.Handled = true;
            return;
        }

        if (selectingPreloadGrid)
        {
            UpdatePreloadGridSelection(args.X, args.Y);
            args.Handled = true;
            return;
        }

        if (panningPreloadGrid)
        {
            UpdatePreloadGridPan(args.X, args.Y);
            args.Handled = true;
            return;
        }

        if (draggingWorldPathKeyframe)
        {
            UpdateWorldPathKeyframeDrag(args.X, args.Y, IsShiftDown() || worldPathVerticalDrag);
            args.Handled = true;
            return;
        }

        if (panningPathEditorTimeline)
        {
            UpdatePathEditorTimelinePan(args.X);
            args.Handled = true;
            return;
        }

        if (draggingPathEditorPlayhead)
        {
            SetPathEditorPlayhead(PathGlobalTimeAt(args.X), centerView: false);
            args.Handled = true;
            return;
        }

        if (draggingPathEditorKeyframes)
        {
            UpdatePathEditorKeyframeDrag(args.X, args.Y);
            args.Handled = true;
            return;
        }

        if (selectingPathEditorRange)
        {
            pathEditorBoxEndTime = PathEditorTimeAt(args.X);
            pathEditorBoxEndMouseY = args.Y;
            pathEditorRangeMoved |= Math.Abs(args.X - pathEditorDragStartMouseX)
                >= GuiElement.scaled(4d)
                || Math.Abs(args.Y - pathEditorDragStartMouseY) >= GuiElement.scaled(4d);
            if (pathEditorRangeMoved)
            {
                PreviewPathEditorRangeSelection();
            }
            args.Handled = true;
            return;
        }

        if (stretchingPathEditorSegment >= 0)
        {
            UpdatePathEditorSegmentStretch(args.X);
            args.Handled = true;
            return;
        }

        if (workflowScrollbarDragMode != WorkflowScrollbarDragMode.None)
        {
            UpdateWorkflowScrollbarDrag(args.X, args.Y);
            args.Handled = true;
            return;
        }

        if (draggingWorkflowCard)
        {
            UpdateWorkflowCardDrag(args);
            args.Handled = true;
            return;
        }

        if (panningWorkflow)
        {
            UpdateWorkflowPan(args.X);
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction && section == DirectorWorkspaceSection.Paths)
        {
            UpdatePathElementTimelineMouseMove(args);
            if (args.Handled)
            {
                return;
            }
        }

        if (ShouldHandleWorkspacePointerInteraction && paneSplitDragging)
        {
            UpdatePaneSplitDrag(args.X, args.Y);
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction && replayElementTrackReordering)
        {
            UpdateReplayElementTrackReorder(args.Y);
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction && replayTimelinePanning)
        {
            double pixelsPerTick = GuiElement.scaled(ReplayTimelinePixelsPerTick);
            double deltaTicks = (args.X - replayTimelinePanStartMouseX)
                / DirectorTimelineCoordinateMath.PositivePixelsPerTick(pixelsPerTick);
            replayTimelineScrollTicks = replayTimelinePanStartTicks - deltaTicks;
            ScrollReplayTimeline(0d);
            ComposeTimeline();
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replayExportRangeDragMode != ReplayExportRangeDragMode.None)
        {
            UpdateReplayExportRangeDrag(args.X, IsShiftDown());
            ComposeTimeline();
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replayAuxiliaryKeyframeDragKind != ReplayAuxiliaryKeyframeDragKind.None)
        {
            DirectorReplayElementKeyframe? draggedElement = replayAuxiliaryKeyframeDragKind
                    == ReplayAuxiliaryKeyframeDragKind.Element
                ? CurrentReplayViewportTimeline().ElementKeyframes.FirstOrDefault(keyframe =>
                    keyframe.Id == replayAuxiliaryKeyframeDragId)
                : null;
            if (draggedElement is not null)
            {
                double nextTime = ReplayTimeAt(args.X);
                if (draggedElement.TrackType == DirectorReplayElementTrackType.Timelapse
                    && DirectorReplayTimelapse.TryFindPair(
                        CurrentReplayViewportTimeline().ElementKeyframes,
                        draggedElement.TrackId,
                        draggedElement.Id,
                        out DirectorReplayTimelapsePair timelapsePair))
                {
                    double minimumGap = 1d / ReplayTimelineTicksPerSecond;
                    nextTime = draggedElement.TimelapseRole
                        == DirectorReplayTimelapseKeyframeRole.Start
                        ? Math.Min(nextTime, timelapsePair.End.TimeSeconds - minimumGap)
                        : Math.Max(nextTime, timelapsePair.Start.TimeSeconds + minimumGap);
                    nextTime = Math.Clamp(nextTime, 0d, owner.ReplayDurationSeconds);
                }
                replayAuxiliaryKeyframeDragMoved |= ReplayTimelineTickAt(nextTime)
                    != ReplayTimelineTickAt(replayKeyframeDragTime);
                replayKeyframeDragTime = nextTime;
            }
            ComposeTimeline();
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replayViewportDragMode != ReplayViewportDragMode.None)
        {
            UpdateReplayViewportDrag(args.X);
            args.Handled = true;
            return;
        }

        if (scrubbingReplay)
        {
            previewReplayTime = ReplayTimeAt(args.X);
            args.Handled = true;
            return;
        }

        if (pathEdit is not null
            && pathEditorTimelineBounds?.PointInside(args.X, args.Y) == true)
        {
            double hoverTime = Math.Round(
                PathEditorTimeAt(args.X) * ReplayTimelineTicksPerSecond,
                MidpointRounding.AwayFromZero) / ReplayTimelineTicksPerSecond;
            if (!double.IsFinite(pathEditorHoverTime)
                || Math.Abs(pathEditorHoverTime - hoverTime)
                    >= 0.5d / ReplayTimelineTicksPerSecond)
            {
                pathEditorHoverTime = hoverTime;
                owner.SetPathEditPreview(
                    pathEdit,
                    SelectedPathKeyframeIndex(),
                    hoverTime,
                    selectedPathKeyframes);
            }
            args.Handled = true;
            return;
        }
        if (double.IsFinite(pathEditorHoverTime))
        {
            pathEditorHoverTime = double.NaN;
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
        }

        if (PointInsideFloatingEditor(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        if (!routingFloatingEditorInput)
        {
            base.OnMouseMove(args);
        }
    }

    public override void OnMouseUp(MouseEvent args)
    {
        // Release before other controls' Handled flags or focus routing can consume the event.
        if (paneSplitDragging && args.Button == EnumMouseButton.Left)
        {
            FinishPaneSplitDrag();
            args.Handled = true;
            return;
        }
        if (DirectorButtonInputCapture.TryRelease(args))
        {
            args.Handled = true;
            return;
        }

        if (DirectorSliderInputCapture.Captured is GuiElementDirectorSlider slider)
        {
            slider.OnMouseUp(capi, args);
            args.Handled = true;
            return;
        }

        bool endedReplayViewportInput = args.Button == EnumMouseButton.Left
            && replayViewportFreeCameraPointerActive;
        if (endedReplayViewportInput)
        {
            EndReplayViewportFreeCameraPointerInput();
        }
        if (args.Handled)
        {
            return;
        }
        if (endedReplayViewportInput)
        {
            args.Handled = true;
            return;
        }

        if (routingFloatingEditorInput
            && !HasActiveFloatingPointerInteraction()
            && !PointInsideFloatingEditor(args.X, args.Y))
        {
            return;
        }

        if (!routingFloatingEditorInput && IsWorkspaceDropDownOpen())
        {
            resourceMenuPointerCapture = false;
            base.OnMouseUp(args);
            return;
        }

        if (!routingFloatingEditorInput && RouteResourceMouseUp(args))
        {
            return;
        }

        if (consumeWorkflowContextMouseUp)
        {
            consumeWorkflowContextMouseUp = false;
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replaySplitDragTarget != ReplaySplitDragTarget.None
            && args.Button == EnumMouseButton.Left)
        {
            ReplaySplitDragTarget completed = replaySplitDragTarget;
            replaySplitDragTarget = ReplaySplitDragTarget.None;
            replaySplitRecomposeAccumulator = 0f;
            replaySplitRecomposePending = false;
            if (completed == ReplaySplitDragTarget.Resources)
            {
                ComposeResources();
            }
            else
            {
                ComposeInspector();
            }
            SaveLayout();
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && rotatingActionPreview
            && args.Button == EnumMouseButton.Left)
        {
            rotatingActionPreview = false;
            args.Handled = true;
            return;
        }

        if (selectingPreloadGrid)
        {
            UpdatePreloadGridSelection(args.X, args.Y);
            selectingPreloadGrid = false;
            ApplyPreloadGridSelection();
            if (preloadGridChanged)
            {
                RecordPathEdit(pendingPathEditHistory);
                SavePreloadGridChanges();
            }
            pendingPathEditHistory = null;
            args.Handled = true;
            return;
        }

        if (panningPreloadGrid)
        {
            panningPreloadGrid = false;
            args.Handled = true;
            return;
        }

        if (draggingWorldPathKeyframe)
        {
            draggingWorldPathKeyframe = false;
            worldPathVerticalDrag = false;
            RecordPathEdit(pendingPathEditHistory);
            pendingPathEditHistory = null;
            FinishPathEditorInteraction(recomposeResources: true);
            args.Handled = true;
            return;
        }

        if (panningPathEditorTimeline)
        {
            panningPathEditorTimeline = false;
            SaveLayout();
            args.Handled = true;
            return;
        }

        if (draggingPathEditorPlayhead)
        {
            draggingPathEditorPlayhead = false;
            args.Handled = true;
            return;
        }

        if (draggingPathEditorKeyframes)
        {
            draggingPathEditorKeyframes = false;
            pathEditorDragValuesOnly = false;
            pathEditorDragTimes.Clear();
            pathEditorDragValues.Clear();
            RecordPathEdit(pendingPathEditHistory);
            pendingPathEditHistory = null;
            FinishPathEditorInteraction(recomposeResources: true);
            args.Handled = true;
            return;
        }

        if (selectingPathEditorRange)
        {
            selectingPathEditorRange = false;
            pathEditorBoxEndTime = PathEditorTimeAt(args.X);
            pathEditorBoxEndMouseY = args.Y;
            pathEditorRangeMoved |= Math.Abs(args.X - pathEditorDragStartMouseX)
                >= GuiElement.scaled(4d)
                || Math.Abs(args.Y - pathEditorDragStartMouseY) >= GuiElement.scaled(4d);
            if (pathEditorRangeMoved)
            {
                ApplyPathEditorRangeSelection(pathEditorRangeAdditive);
                pathEditorRangeBaseSelection.Clear();
                RecordPathEdit(pendingPathEditHistory);
                pendingPathEditHistory = null;
                FinishPathEditorInteraction(recomposeResources: true);
            }
            else if (!pathEditorRangeAdditive)
            {
                pathEditorRangeBaseSelection.Clear();
                AddPathKeyframeAtTime(pathEditorBoxStartTime);
            }
            else
            {
                pathEditorRangeBaseSelection.Clear();
                RecordPathEdit(pendingPathEditHistory);
                pendingPathEditHistory = null;
                FinishPathEditorInteraction(recomposeResources: true);
            }
            args.Handled = true;
            return;
        }

        if (stretchingPathEditorSegment >= 0)
        {
            stretchingPathEditorSegment = -1;
            RecordPathEdit(pendingPathEditHistory);
            pendingPathEditHistory = null;
            FinishPathEditorInteraction(recomposeResources: true);
            args.Handled = true;
            return;
        }

        if (workflowScrollbarDragMode != WorkflowScrollbarDragMode.None)
        {
            workflowScrollbarDragMode = WorkflowScrollbarDragMode.None;
            SaveLayout();
            ComposeWorkflowCanvas();
            args.Handled = true;
            return;
        }

        if (draggingWorkflowCard)
        {
            draggingWorkflowCard = false;
            workflowCardDragMode = WorkflowCardDragMode.None;
            workflowSnapTime = double.NaN;
            SaveWorkflowEditor(showMessage: false, captureInputs: false);
            args.Handled = true;
            return;
        }

        if (panningWorkflow)
        {
            panningWorkflow = false;
            SaveLayout();
            ComposeWorkflowCanvas();
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && section == DirectorWorkspaceSection.Paths
            && FinishPathElementTimelineMouseUp(args))
        {
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replayElementTrackReordering
            && args.Button == EnumMouseButton.Left)
        {
            FinishReplayElementTrackReorder();
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replayTimelinePanning
            && args.Button == EnumMouseButton.Middle)
        {
            replayTimelinePanning = false;
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replayAuxiliaryKeyframeDragKind != ReplayAuxiliaryKeyframeDragKind.None
            && args.Button == EnumMouseButton.Left)
        {
            FinishReplayAuxiliaryKeyframeDrag();
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replayExportRangeDragMode != ReplayExportRangeDragMode.None
            && args.Button == EnumMouseButton.Left)
        {
            FinishReplayExportRangeDrag();
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction && scrubbingReplay)
        {
            scrubbingReplay = false;
            previewReplayTime = Math.Clamp(previewReplayTime, 0d, owner.ReplayDurationSeconds);
            owner.SeekReplayFromPanel(previewReplayTime);
            args.Handled = true;
            return;
        }

        if (ShouldHandleWorkspacePointerInteraction
            && replayViewportDragMode != ReplayViewportDragMode.None
            && args.Button == EnumMouseButton.Left)
        {
            FinishReplayViewportDrag();
            args.Handled = true;
            return;
        }

        if (PointInsideFloatingEditor(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        if (!routingFloatingEditorInput)
        {
            base.OnMouseUp(args);
        }
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (args.IsHandled)
        {
            return;
        }

        // The native timeline reads the same physical wheel through ImGui.
        // Consume the game event here so zoom/pan runs exactly once.
        if (!routingFloatingEditorInput && rmlTimeline?.IsOpened == true
            && !PointInsideFloatingEditor(capi.Input.MouseX, capi.Input.MouseY)
            && layout.Panel("timeline") is { } timelinePanel
            && capi.Input.MouseX >= GuiElement.scaled(timelinePanel.X)
            && capi.Input.MouseX < GuiElement.scaled(timelinePanel.X + timelinePanel.Width)
            && capi.Input.MouseY >= GuiElement.scaled(timelinePanel.Y)
            && capi.Input.MouseY < GuiElement.scaled(timelinePanel.Y + timelinePanel.Height))
        {
            args.SetHandled();
            return;
        }

        if (routingFloatingEditorInput
            && !PointInsideFloatingEditor(capi.Input.MouseX, capi.Input.MouseY))
        {
            return;
        }

        if (!routingFloatingEditorInput && IsWorkspaceDropDownOpen())
        {
            base.OnMouseWheel(args);
            return;
        }

        if (!routingFloatingEditorInput && RouteResourceMouseWheel(args))
        {
            return;
        }

        // Give controls in the docked inspector first chance to consume the
        // wheel event. In particular, sliders must win over the replay panel's
        // own scrolling handler when the pointer is over their track.
        if (!routingFloatingEditorInput && RouteInspectorWheelToControl(args))
        {
            return;
        }

        if (!routingFloatingEditorInput
            && !args.IsHandled
            && section == DirectorWorkspaceSection.Replay
            && ReplayVisualSettingsContain(capi.Input.MouseX, capi.Input.MouseY))
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (Math.Abs(direction) > double.Epsilon)
            {
                ScrollReplayVisualSettings(direction);
                args.SetHandled();
                return;
            }
        }

        if (!args.IsHandled
            && section == DirectorWorkspaceSection.Replay
            && ReplayTimelineTrackLabelsContain(capi.Input.MouseX, capi.Input.MouseY))
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (Math.Abs(direction) > double.Epsilon)
            {
                ScrollReplayTimelineTracks(direction > 0d ? -1 : 1);
                ComposeTimeline();
                args.SetHandled();
                return;
            }
        }

        if (!args.IsHandled
            && section == DirectorWorkspaceSection.Paths
            && PathTimelineTrackLabelsContain(capi.Input.MouseX, capi.Input.MouseY))
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (Math.Abs(direction) > double.Epsilon)
            {
                ScrollPathTimelineTracks(direction > 0d ? -1 : 1);
                ComposeTimeline();
                args.SetHandled();
                return;
            }
        }

        if (!args.IsHandled
            && section == DirectorWorkspaceSection.Paths
            && PathTimelineContentContains(capi.Input.MouseX, capi.Input.MouseY))
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (Math.Abs(direction) > double.Epsilon)
            {
                if (IsControlDown())
                {
                    ZoomPathElementTimeline(direction, capi.Input.MouseX);
                }
                else
                {
                    ScrollPathTimeline(-Math.Sign(direction) * PathTimelineLabelTickStep * 2d);
                }
                ComposeTimeline();
                args.SetHandled();
                return;
            }
        }

        if (!args.IsHandled
            && section == DirectorWorkspaceSection.Replay
            && ReplayTimelineInteractionContains(capi.Input.MouseX, capi.Input.MouseY))
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (Math.Abs(direction) > double.Epsilon)
            {
                if (IsControlDown())
                {
                    ZoomReplayTimeline(direction, capi.Input.MouseX);
                }
                else
                {
                    ScrollReplayTimeline(-Math.Sign(direction) * ReplayTimelineLabelTickStep * 2d);
                }
                ComposeTimeline();
                args.SetHandled();
                return;
            }
        }

        if (!args.IsHandled
            && pathEditorTimelineBounds?.PointInside(capi.Input.MouseX, capi.Input.MouseY) == true)
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (IsShiftDown())
            {
                PanPathEditorTimeline(-Math.Sign(direction) * PathEditorViewDuration * 0.12d);
            }
            else
            {
                ZoomPathEditorTimeline(
                    direction > 0d ? 1.25d : 1d / 1.25d,
                    PathEditorTimeAt(capi.Input.MouseX));
            }
            args.SetHandled();
            return;
        }

        if (!routingFloatingEditorInput
            && !args.IsHandled
            && actionTimelineBounds?.PointInside(capi.Input.MouseX, capi.Input.MouseY) == true)
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (IsShiftDown())
            {
                actionTimelinePanSeconds = Math.Clamp(
                    actionTimelinePanSeconds - Math.Sign(direction) * ActionTimelineVisibleDuration * 0.12d,
                    0d,
                    Math.Max(0d, (actionEdit?.DurationSeconds ?? 0d) - ActionTimelineVisibleDuration));
                ComposeWorkflowCanvas();
            }
            else
            {
                ZoomActionTimeline(direction > 0d ? 1.25d : 1d / 1.25d);
            }
            args.SetHandled();
            return;
        }

        if (!args.IsHandled
            && preloadGridBounds?.PointInside(capi.Input.MouseX, capi.Input.MouseY) == true)
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            preloadGridRadius = Math.Clamp(preloadGridRadius + (direction > 0d ? -1 : 1),
                MinPreloadGridRadius, MaxPreloadGridRadius);
            ComposeWorkflowCanvas();
            args.SetHandled();
            return;
        }

        if (!args.IsHandled
            && section == DirectorWorkspaceSection.Workflows
            && workflowBoardBounds?.PointInside(capi.Input.MouseX, capi.Input.MouseY) == true)
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (Math.Abs(direction) <= double.Epsilon)
            {
                base.OnMouseWheel(args);
                return;
            }
            ZoomWorkflow(direction > 0d ? 1.2d : 1d / 1.2d, capi.Input.MouseX);
            args.SetHandled();
            return;
        }

        if (!args.IsHandled
            && section == DirectorWorkspaceSection.Actions
            && floatingEditorDialog.Composer?.Bounds.PointInside(
                capi.Input.MouseX,
                capi.Input.MouseY) == true)
        {
            double libraryDirection = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (libraryDirection != 0d)
            {
                ChangeActionLibraryPage(libraryDirection > 0d ? -1 : 1);
                args.SetHandled();
                return;
            }
        }

        if (!routingFloatingEditorInput
            && !args.IsHandled
            && (section == DirectorWorkspaceSection.SequenceEditor && inspectorSection == "shots"
                || section == DirectorWorkspaceSection.Replay
                    && replayViewMode == "sequence"
                    && replayViewInspectorSection == "shots")
            && sequenceEdit is not null
            && Composers[InspectorKey] is GuiComposer sequenceInspector
            && sequenceInspector.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY)
            && (section != DirectorWorkspaceSection.Replay
                ? capi.Input.MouseY >= sequenceInspector.Bounds.absY + GuiElement.scaled(150d)
                : replayInspectorViewBounds?.PointInside(capi.Input.MouseX, capi.Input.MouseY) == true))
        {
            double sequenceDirection = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (sequenceDirection != 0d && !ShouldRouteSequenceWheelToControl(sequenceInspector))
            {
                ChangeSequencePage(sequenceDirection > 0d ? -1 : 1);
                args.SetHandled();
                return;
            }
        }

        if (!routingFloatingEditorInput
            && !args.IsHandled
            && section == DirectorWorkspaceSection.Actions
            && owner.HasActionPreviewVisual
            && PointInsideGameViewport(capi.Input.MouseX, capi.Input.MouseY))
        {
            double previewDirection = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (previewDirection != 0d)
            {
                owner.ScaleActionPreviewDistance(previewDirection > 0d ? 0.85d : 1d / 0.85d);
                args.SetHandled();
                return;
            }
        }

        if (!args.IsHandled
            && PointInsideFloatingEditor(capi.Input.MouseX, capi.Input.MouseY))
        {
            args.SetHandled();
            return;
        }

        if (!routingFloatingEditorInput)
        {
            base.OnMouseWheel(args);
        }
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (!args.Handled && args.KeyCode == (int)GlKeys.Escape)
        {
            args.Handled = OnEscapePressed();
            return;
        }
        if (replayViewportFreeCameraPointerActive
            && args.KeyCode == (int)GlKeys.Space)
        {
            // Free-camera movement reads the held key state directly. Do not
            // let the same key reach the focused workspace control.
            args.Handled = true;
            return;
        }

        if (!args.Handled
            && args.KeyCode is (int)GlKeys.Enter or (int)GlKeys.KeypadEnter
            && (TrySubmitFocusedResourceInput()
                || TrySubmitFocusedReplayVisualTimeInput()))
        {
            args.Handled = true;
            return;
        }

        if (!args.Handled
            && section == DirectorWorkspaceSection.Actions
            && actionEdit is not null
            && !layout.NavigationMode
            && args.KeyCode == (int)GlKeys.Escape
            && selectedActionFrames.Count > 0)
        {
            ClearActionFrameSelection();
            args.Handled = true;
            return;
        }
        if (!args.Handled
            && section == DirectorWorkspaceSection.Paths
            && pathEdit is not null
            && !layout.NavigationMode
            && args.KeyCode == (int)GlKeys.Escape
            && selectedPathKeyframes.Count > 0)
        {
            ClearPathKeyframeSelection();
            args.Handled = true;
            return;
        }

        if (!args.Handled
            && section == DirectorWorkspaceSection.Replay
            && args.CtrlPressed
            && args.KeyCode is (int)GlKeys.Left or (int)GlKeys.Right)
        {
            StepReplayTimelineTick(args.KeyCode == (int)GlKeys.Left ? -1 : 1);
            args.Handled = true;
            return;
        }

        if (!args.Handled
            && section == DirectorWorkspaceSection.Paths
            && pathEdit is not null
            && args.CtrlPressed
            && args.KeyCode is (int)GlKeys.Left or (int)GlKeys.Right)
        {
            StepPathTimelineTick(args.KeyCode == (int)GlKeys.Left ? -1 : 1);
            args.Handled = true;
            return;
        }

        base.OnKeyDown(args);
        if (!args.Handled
            && section == DirectorWorkspaceSection.Actions
            && actionEdit is not null
            && !layout.NavigationMode)
        {
            if (args.CtrlPressed && args.KeyCode == (int)GlKeys.Z)
            {
                if (args.ShiftPressed) RedoActionEdit(); else UndoActionEdit();
                args.Handled = true;
            }
            else if (args.CtrlPressed && args.KeyCode == (int)GlKeys.Y)
            {
                RedoActionEdit();
                args.Handled = true;
            }
            else if (args.KeyCode == (int)GlKeys.Delete)
            {
                DeleteSelectedActionFrames();
                args.Handled = true;
            }
            else if (args.CtrlPressed && args.KeyCode == (int)GlKeys.C)
            {
                CopySelectedActionFrames();
                args.Handled = true;
            }
            else if (args.CtrlPressed && args.KeyCode == (int)GlKeys.V)
            {
                PasteActionFramesAtPlayhead();
                args.Handled = true;
            }
            else if (args.CtrlPressed && args.KeyCode == (int)GlKeys.A)
            {
                SelectAllActionFrames();
                args.Handled = true;
            }
            return;
        }
        if (args.Handled
            || section != DirectorWorkspaceSection.Paths
            || pathEdit is null
            || layout.NavigationMode)
        {
            return;
        }

        if (args.CtrlPressed && args.KeyCode == (int)GlKeys.Z)
        {
            if (args.ShiftPressed)
            {
                RedoPathEdit();
            }
            else
            {
                UndoPathEdit();
            }
            args.Handled = true;
        }
        else if (args.CtrlPressed && args.KeyCode == (int)GlKeys.Y)
        {
            RedoPathEdit();
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.K && !args.CtrlPressed && !args.AltPressed)
        {
            AddPathKeyframeAtPlayhead();
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.Delete)
        {
            DeleteSelectedPathKeyframes();
            args.Handled = true;
        }
        else if (args.CtrlPressed && args.KeyCode == (int)GlKeys.C)
        {
            CopySelectedPathKeyframe();
            args.Handled = true;
        }
        else if (args.CtrlPressed && args.KeyCode == (int)GlKeys.V)
        {
            PastePathKeyframeAtPlayhead();
            args.Handled = true;
        }
        else if (args.CtrlPressed && args.KeyCode == (int)GlKeys.A)
        {
            SelectAllPathKeyframes();
            args.Handled = true;
        }
    }

    public override void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DisposeTimelineMedia();
        owner.SetWorkspaceInputOverride(false);
        rmlTimeline?.Dispose(); rmlTimeline = null;
        rmlInspectorWindow?.Close();
        preloadRmlWindow?.Dispose(); preloadRmlWindow = null;
        rmlToolbarWindow?.Dispose(); rmlToolbarWindow = null;
        rmlResourceWindow?.Dispose(); rmlResourceWindow = null;
        sceneBrowser?.Dispose(); sceneBrowser = null;
        rmlInspectorWindow?.Dispose();
        rmlViewWindow?.Dispose(); rmlViewWindow = null;
        rmlInspectorWindow = null;
        EndReplayViewportFreeCameraPointerInput();
        ResetWorkspacePointerInteraction();
        DirectorWorkspaceViewportRuntime.Deactivate(this);
        SaveLayout();
        store.Dispose();
        owner.StopActionPreview();
        invalidWorkflowDialog?.Dispose();
        invalidWorkflowDialog = null;
        floatingEditorDialog.Dispose();
        DisposeKeyframeRml();
        exportDialog.Dispose();
        timelineMarkerRenderer.Dispose();
        solidTextureRenderer.Dispose();
        imGuiWorkspace.Dispose();
        base.Dispose();
    }

    private void ComposeAll()
    {
        if (disposed || owner.IsShuttingDown || rmlInspectorWindow?.Document?.IsDisposed == true) return;
        PreserveInputs();
        NormalizeFeatureVisibility();
        NormalizeLayoutToViewport();
        EnsureReplayViewEditor(reset: false);
        ComposeToolbar();
        ComposePreview();
        ComposeInspector();
        ComposeTimeline();
        // Hover menus render last so they stay above the docked editor panels.
        ComposeResources();
    }

    private void ComposeToolbar() => ComposeRmlToolbar();

    private bool OpenReplayExport()
        => exportDialog.OpenForCurrentScene();

    private void ComposePreview()
    {
        const string key = PreviewKey;
        Composers[key]?.Dispose();
        DirectorWorkspacePanelLayout panel = layout.Panel("preview");
        ElementBounds root = PanelRoot(panel);
        GuiComposer composer = capi.Gui.CreateCompo(key, root);
        Composers[key] = composer.Compose(focusFirstElement: false);
        UpdateGameViewportRuntime();
    }

    private void ComposeResources() => ComposeRmlResources();



    private void ComposeTimeline()
    {
        ComposeWorkflowCanvas();
        const string key = TimelineKey;
        DirectorGuiFocusSnapshot? focus = DirectorGuiFocus.Capture(Composers[key]);
        Composers[key]?.Dispose();
        replaySequenceTimelineBounds = null;
        replayPathViewTimelineBounds = null;
        // Keep the same native bottom dock when switching between scene and replay editors.
        if (section is DirectorWorkspaceSection.Cameras or DirectorWorkspaceSection.Follows
            or DirectorWorkspaceSection.Paths or DirectorWorkspaceSection.Replay)
        {
            Composers.Remove(key);
            ComposeRmlTimeline();
            return;
        }
        rmlTimeline?.Close();
        if (section == DirectorWorkspaceSection.LiveOffscreen)
        {
            Composers.Remove(key);
            replayTimelineBounds = null;
            pathTimelineBounds = null;
            return;
        }
        if (section == DirectorWorkspaceSection.Actions)
        {
            ComposeActionTimelinePanel(key, focus);
            return;
        }

        DirectorWorkspacePanelLayout panel = layout.Panel("timeline");
        ElementBounds root = PanelRoot(panel);
        ElementBounds content = ElementBounds.Fixed(0, 0, panel.Width, panel.Height);
        ElementBounds background = ElementBounds.Fill;
        background.WithChildren(content);
        CairoFont detail = DirectorFont.WhiteDetailText().WithLineHeightMultiplier(1.16d);
        GuiComposer composer = capi.Gui.CreateCompo(key, root)
            .AddDirectorOpaqueDockedSurface(background, DirectorSurfaceEdges.Top)
            .BeginChildElements(content);

        replayTimelineBounds = null;
        replaySequenceTimelineBounds = null;
        replayPathViewTimelineBounds = null;
        pathTimelineBounds = null;
        if (section == DirectorWorkspaceSection.Replay)
        {
            double timelineY = 6d;
            double timelineHeight = Math.Max(96d, panel.Height - 12d);
            replayTimelineBounds = ElementBounds.Fixed(0d, timelineY, panel.Width, timelineHeight);
            composer.AddCustomRender(replayTimelineBounds, RenderReplayTimeline);

            double labelColumnWidth = ReplayTimelineLabelColumnWidth(panel.Width);
            double controlY = timelineY + 3d;
            const double playbackButtonWidth = 26d;
            const double playbackButtonStep = 30d;
            composer
                .AddDirectorTransportButton(DirectorIcon.SkipBack,
                    Lang.Get("vsdirector:replay-skip-backward"),
                    SkipReplayBackward, ElementBounds.Fixed(14d, controlY,
                        playbackButtonWidth, 26d), "replaySkipBack")
                .AddDirectorTransportButton(DirectorIcon.Reverse,
                    Lang.Get("vsdirector:replay-slow-down",
                        FormatReplayTransportSpeed(owner.ReplayBasePlaybackSpeed)),
                    SlowDownReplay, ElementBounds.Fixed(14d + playbackButtonStep, controlY,
                        playbackButtonWidth, 26d), "replaySlowDown")
                .AddDirectorTransportButton(
                    owner.IsReplayPaused ? DirectorIcon.Play : DirectorIcon.Pause,
                    Lang.Get(owner.IsReplayPaused
                        ? owner.HasReplayReachedEnd
                            ? "vsdirector:replay-restart"
                            : "vsdirector:replay-play"
                        : "vsdirector:replay-pause"),
                    ToggleReplayPause, ElementBounds.Fixed(14d + playbackButtonStep * 2d,
                        controlY, playbackButtonWidth, 26d), "pause")
                .AddDirectorTransportButton(DirectorIcon.FastForward,
                    Lang.Get("vsdirector:replay-speed-up",
                        FormatReplayTransportSpeed(owner.ReplayBasePlaybackSpeed)),
                    SpeedUpReplay, ElementBounds.Fixed(14d + playbackButtonStep * 3d,
                        controlY, playbackButtonWidth, 26d), "replaySpeedUp")
                .AddDirectorTransportButton(DirectorIcon.SkipForward,
                    Lang.Get("vsdirector:replay-skip-forward"),
                    SkipReplayForward, ElementBounds.Fixed(14d + playbackButtonStep * 4d,
                        controlY, playbackButtonWidth, 26d), "replaySkipForward")
                .AddDirectorIconButton(DirectorIcon.Stop, Lang.Get("vsdirector:replay-exit"),
                    StopReplay, ElementBounds.Fixed(14d + playbackButtonStep * 5d,
                        controlY, playbackButtonWidth, 26d),
                    EnumButtonStyle.Small, "exit");

            CairoFont rulerFont = DirectorFont.WhiteDetailText().WithLineHeightMultiplier(1d);
            double rulerTextTop = timelineY + 1d;
            double rulerTextHeight = 17d;
            double scaleContentWidth = Math.Max(1d, panel.Width - labelColumnWidth);
            double scaleContentLeft = labelColumnWidth;
            double visibleTicks = scaleContentWidth / ReplayTimelinePixelsPerTick;
            int durationTicks = ReplayTimelineDurationTicks();
            replayTimelineScrollTicks = Math.Clamp(
                replayTimelineScrollTicks,
                0d,
                Math.Max(0d, durationTicks - visibleTicks));
            int firstVisibleTick = Math.Max(0, (int)Math.Floor(replayTimelineScrollTicks));
            int lastVisibleTick = Math.Min(
                durationTicks,
                (int)Math.Ceiling(replayTimelineScrollTicks + visibleTicks));
            int firstLabelTick = ((firstVisibleTick + ReplayTimelineLabelTickStep - 1)
                / ReplayTimelineLabelTickStep) * ReplayTimelineLabelTickStep;
            int? dragTick = double.IsFinite(replayKeyframeDragTime)
                ? ReplayTimelineTickAt(replayKeyframeDragTime)
                : null;
            const double rulerLabelWidth = 78d;
            int rulerLabelIndex = 0;
            for (int tick = firstLabelTick; tick <= lastVisibleTick;
                 tick += ReplayTimelineLabelTickStep)
            {
                double tickX = scaleContentLeft
                    + (tick - replayTimelineScrollTicks) * ReplayTimelinePixelsPerTick;
                double labelWidth = Math.Min(rulerLabelWidth, Math.Max(1d, scaleContentWidth - 4d));
                double labelX = tickX + 3d;
                if (labelX + labelWidth > scaleContentLeft + scaleContentWidth
                    || dragTick is int activeTick
                        && Math.Abs(activeTick - tick) * ReplayTimelinePixelsPerTick < labelWidth)
                {
                    continue;
                }
                composer.AddDirectorSingleLineText(
                    FormatReplayTimelineTick(tick),
                    rulerFont,
                    ElementBounds.Fixed(labelX, rulerTextTop, labelWidth, rulerTextHeight),
                    "replayRulerLabel" + rulerLabelIndex.ToString(CultureInfo.InvariantCulture));
                rulerLabelIndex++;
            }
            if (dragTick is int preciseTick)
            {
                double tickX = scaleContentLeft
                    + (preciseTick - replayTimelineScrollTicks) * ReplayTimelinePixelsPerTick;
                double labelX = Math.Clamp(
                    tickX + 3d,
                    scaleContentLeft + 2d,
                    Math.Max(scaleContentLeft + 2d,
                        scaleContentLeft + scaleContentWidth - rulerLabelWidth - 2d));
                composer.AddDirectorSingleLineText(
                    FormatReplayTimelineTick(preciseTick),
                    rulerFont,
                    ElementBounds.Fixed(labelX, rulerTextTop, rulerLabelWidth, rulerTextHeight),
                    "replayDraggedKeyframeTime");
            }

            DirectorReplayViewportTimeline replayTimeline = CurrentReplayViewportTimeline();
            IReadOnlyList<DirectorReplayElementTrack> elementTracks = ActiveReplayTimelineElementTracks();
            string[] sceneValues = new[]
                {
                    "scene-command-new",
                    "scene-command-rename",
                    "scene-command-delete"
                }
                .Concat(replayTimeline.Scenes.Select(scene => scene.Id.ToString("D")))
                .ToArray();
            string[] sceneNames = new[]
                {
                    Lang.Get("vsdirector:replay-scene-new"),
                    Lang.Get("vsdirector:replay-scene-rename"),
                    Lang.Get("vsdirector:replay-scene-delete")
                }
                .Concat(replayTimeline.Scenes.Select(scene => scene.Name))
                .ToArray();
            int selectedScene = 3 + Math.Max(0, replayTimeline.Scenes.FindIndex(scene =>
                scene.Id == replayTimeline.ActiveSceneId));
            int visibleTrackCount = Math.Max(1, (int)Math.Floor(
                (timelineHeight - ReplayTimelineHeaderHeight)
                / ReplayTimelineTrackHeight));
            replayTimelineFirstVisibleTrack = Math.Clamp(
                replayTimelineFirstVisibleTrack,
                0,
                Math.Max(0, elementTracks.Count + 1 - visibleTrackCount));
            for (int visibleTrackIndex = 0;
                 visibleTrackIndex < visibleTrackCount;
                 visibleTrackIndex++)
            {
                int trackIndex = replayTimelineFirstVisibleTrack + visibleTrackIndex;
                if (trackIndex > elementTracks.Count)
                {
                    break;
                }
                double rowY = timelineY
                    + ReplayTimelineHeaderHeight
                    + visibleTrackIndex * ReplayTimelineTrackHeight;
                if (rowY + ReplayTimelineTrackHeight > timelineY + timelineHeight)
                {
                    break;
                }
                if (trackIndex == elementTracks.Count)
                {
                    double addButtonWidth = Math.Clamp(labelColumnWidth * 0.38d, 56d, 88d);
                    const double controlGap = 6d;
                    double sceneX = 8d + addButtonWidth + controlGap;
                    double sceneWidth = Math.Max(34d, labelColumnWidth - sceneX - 8d);
                    composer
                        .AddDirectorButton(
                            Lang.Get("vsdirector:replay-element-add"),
                            OpenReplayElementMenu,
                            ElementBounds.Fixed(8d, rowY + 2d, addButtonWidth, 26d),
                            EnumButtonStyle.Small,
                            "replayAddElement")
                        .AddDropDown(sceneValues, sceneNames, selectedScene,
                            OnReplaySceneMenuChanged,
                            ElementBounds.Fixed(sceneX, rowY + 2d, sceneWidth, 26d),
                            "replaySceneSelector");
                    continue;
                }
                DirectorReplayElementTrack track = elementTracks[trackIndex];
                ReplayTimelineElementKind kind = ReplayTimelineElementKindFor(track.TrackType);
                string trackKey = track.Id.ToString("N");
                // Keep the controls at their previous positions. The expanded
                // label column becomes a real safety area before the 0s marker.
                double addKeyframeX = Math.Max(
                    190d,
                    labelColumnWidth - ReplayTimelineLabelColumnExpansion - 30d);
                double visibilityX = addKeyframeX - 28d;
                double menuX = visibilityX - 28d;
                double nameX = 60d;
                double nameRight = menuX;
                double nameWidth = Math.Max(26d, nameRight - nameX - 4d);
                composer.AddDirectorChromelessIconButton(DirectorIcon.Drag,
                    Lang.Get("vsdirector:replay-element-track-reorder"),
                    () => SelectReplayTimelineElementTrack(track.Id),
                    ElementBounds.Fixed(8d, rowY + 2d, 28d, 26d),
                    "replayTrackDrag" + trackKey);
                composer.AddDirectorChromelessIconButton(DirectorIcon.Bars,
                    Lang.Get("vsdirector:replay-element-track-options"),
                    () => OpenReplayTrackEditor(track.Id),
                    ElementBounds.Fixed(menuX, rowY + 2d, 28d, 26d),
                    "replayTrackMenu" + trackKey);
                bool enabled = track.Enabled;
                composer.AddDirectorChromelessIconButton(enabled ? DirectorIcon.Eye : DirectorIcon.EyeOff,
                    enabled
                        ? Lang.Get("vsdirector:replay-viewport-hide")
                        : Lang.Get("vsdirector:replay-viewport-show"),
                    () => ToggleReplayElementTrack(track.Id),
                    ElementBounds.Fixed(visibilityX, rowY + 2d, 28d, 26d),
                    "replayTrackVisible" + trackKey);
                composer.AddDirectorChromelessIconButton(DirectorIcon.CirclePlus,
                    Lang.Get("vsdirector:replay-element-keyframe-add"),
                    () => AddReplayKeyframeForTrack(track.Id),
                    ElementBounds.Fixed(addKeyframeX, rowY + 2d, 28d, 26d),
                    "replayTrackAddKeyframe" + trackKey);
                composer.AddDirectorSingleLineText(
                    Ellipsize(track.Name.Length == 0
                            ? ReplayTimelineElementTrackName(kind)
                            : track.Name,
                        Math.Max(6, (int)(nameWidth / 7d))),
                    detail,
                    ElementBounds.Fixed(nameX, rowY + 4d, nameWidth, 22d));
            }
        }
        else if (section == DirectorWorkspaceSection.Paths && pathEdit is not null)
        {
            ComposePathElementTimeline(composer, panel, detail);
        }
        composer.EndChildElements();
        Composers[key] = composer.Compose(focusFirstElement: false);
        UpdateReplayTimelineButtonStates();
        if (section == DirectorWorkspaceSection.Paths)
        {
            SetButtonEnabledIfPresent(composer, "pathTimelinePrevious", pathEdit?.Keyframes.Count > 0);
            SetButtonEnabledIfPresent(composer, "pathTimelineNext", pathEdit?.Keyframes.Count > 0);
            SetButtonEnabledIfPresent(composer, "pathTimelinePlay", pathEdit?.Keyframes.Count >= 2);
            SetButtonEnabledIfPresent(composer, "pathTimelineReverse", pathEdit?.Keyframes.Count >= 2);
            SetButtonEnabledIfPresent(composer, "pathTimelineStop", owner.IsPathPlaying);
        }
        DirectorGuiFocus.Restore(Composers[key], focus);
    }

    private void ComposeWorkflowCanvas()
    {
        if (!preloadEditorExpanded || !CanManageChunkPreload || !HasPreloadEditorTarget()
            || (section == DirectorWorkspaceSection.Replay && replayFloatingEditorKind != ReplayFloatingEditorKind.None))
        { preloadRmlWindow?.Dispose(); preloadRmlWindow = null; }
        const string key = WorkflowCanvasKey;
        if (section == DirectorWorkspaceSection.Replay
            && replayFloatingEditorKind != ReplayFloatingEditorKind.None)
        {
            ComposeReplayFloatingEditor(key);
            return;
        }
        if (CanManageChunkPreload
            && preloadEditorExpanded
            && HasPreloadEditorTarget())
        {
            ComposePreloadCanvas(key);
            return;
        }
        if (section == DirectorWorkspaceSection.Paths
            && pathFloatingEditorKind != PathFloatingEditorKind.None)
        {
            ComposePathFloatingEditor(key);
            return;
        }
        if (section == DirectorWorkspaceSection.Paths && pathEdit is not null)
        {
            RemoveFloatingEditorComposer();
            return;
        }
        if (section == DirectorWorkspaceSection.Actions)
        {
            RemoveFloatingEditorComposer();
            ComposeResources();
            return;
        }
        if (section != DirectorWorkspaceSection.Workflows || !workflowEditorWindowOpen)
        {
            RemoveFloatingEditorComposer();
            contextWorkflowCardId = Guid.Empty;
            return;
        }

        DirectorWorkspacePanelLayout panel = layout.Panel("workflow");
        panel.Dock = DirectorWorkspaceDock.Floating;
        ElementBounds root = PanelRoot(panel);
        double contentHeight = Math.Max(1d, panel.Height - FloatingEditorHeaderHeight);
        ElementBounds content = ElementBounds.Fixed(
            0,
            FloatingEditorHeaderHeight,
            panel.Width,
            contentHeight);
        ElementBounds background = ElementBounds.Fill;
        background.WithChildren(content);
        CairoFont detail = DirectorFont.WhiteDetailText().WithLineHeightMultiplier(1.16d);
        workflowBoardBounds = ElementBounds.Fixed(
            10,
            44,
            panel.Width - 20,
            Math.Max(250d, contentHeight - 110d));

        GuiComposer composer = capi.Gui.CreateCompo(key, root)
            .AddDirectorOpaqueWindowSurface(background)
            .AddDirectorDialogHeader(
                Lang.Get("vsdirector:workspace-workflow-canvas"),
                CloseFloatingEditorWindow,
                panel.Width,
                "floatingEditorHeader",
                UpdateFloatingEditorPosition)
            .BeginChildElements(content)
            .AddDirectorIconButton(DirectorIcon.Add, Lang.Get("vsdirector:workflow-add-card"),
                NewWorkflowCard,
                ElementBounds.Fixed(10, 6, 30, 30), EnumButtonStyle.Normal, "workflowAdd")
            .AddDirectorIconButton(DirectorIcon.ZoomOut, Lang.Get("vsdirector:workspace-zoom-out"),
                () => ZoomWorkflow(1d / 1.25d),
                ElementBounds.Fixed(46, 6, 30, 30), EnumButtonStyle.Small, "workflowZoomOut")
            .AddDirectorIconButton(DirectorIcon.ZoomIn, Lang.Get("vsdirector:workspace-zoom-in"),
                () => ZoomWorkflow(1.25d),
                ElementBounds.Fixed(82, 6, 30, 30), EnumButtonStyle.Small, "workflowZoomIn")
            .AddDirectorIconButton(DirectorIcon.Fit, Lang.Get("vsdirector:workspace-zoom-fit"),
                FitWorkflowView,
                ElementBounds.Fixed(118, 6, 30, 30), EnumButtonStyle.Small, "workflowFit")
            .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:workflow-preview"),
                PreviewWorkflow,
                ElementBounds.Fixed(154, 6, 30, 30), EnumButtonStyle.Normal, "workflowPreview")
            .AddDirectorIconButton(DirectorIcon.Record, Lang.Get("vsdirector:workflow-live-record"),
                RecordWorkflowLive,
                ElementBounds.Fixed(190, 6, 30, 30), EnumButtonStyle.Normal, "workflowRecord")
            .AddDynamicText(WorkflowCanvasToolbarText(), detail,
                ElementBounds.Fixed(232, 8, panel.Width - 242, 26), "workflowToolbar")
            .AddCustomRender(workflowBoardBounds, RenderWorkflowBoard)
            .AddDynamicText(WorkflowCanvasFooterText(), detail,
                ElementBounds.Fixed(10, contentHeight - 58, panel.Width - 20, 48), "workflowStatus");

        if (workflowEdit is not null)
        {
            AddWorkflowCanvasLabels(composer, detail, workflowBoardBounds);
            AddWorkflowRulerLabels(composer, detail, workflowBoardBounds);
        }

        if (contextWorkflowCardId != Guid.Empty && workflowBoardBounds is not null)
        {
            double menuX = 10d + workflowContextX;
            double menuY = 44d + workflowContextY;
            composer
                .AddDirectorIconButton(DirectorIcon.Select, Lang.Get("vsdirector:workspace-select"),
                    SelectWorkflowContextCard,
                    ElementBounds.Fixed(menuX + 4d, menuY + 4d, 30d, 30d), EnumButtonStyle.Normal,
                    "workflowContextSelect")
                .AddDirectorIconButton(DirectorIcon.Edit, Lang.Get("vsdirector:edit"),
                    EditWorkflowContextCard,
                    ElementBounds.Fixed(menuX + 38d, menuY + 4d, 30d, 30d), EnumButtonStyle.Small,
                    "workflowContextEdit")
                .AddDirectorIconButton(DirectorIcon.Duplicate, Lang.Get("vsdirector:workflow-duplicate-card"),
                    DuplicateWorkflowCard,
                    ElementBounds.Fixed(menuX + 72d, menuY + 4d, 30d, 30d), EnumButtonStyle.Small,
                    "workflowContextDuplicate")
                .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete"),
                    DeleteWorkflowCard,
                    ElementBounds.Fixed(menuX + 106d, menuY + 4d, 30d, 30d), EnumButtonStyle.Small,
                    "workflowContextDelete");
        }

        composer.EndChildElements();
        composer.zDepth = 35f;
        GuiComposer composed = composer.Compose(focusFirstElement: false);
        floatingEditorDialog.SetComposer(composed);
        composed.GetButton("workflowRecord").Enabled = workflowEdit is not null;
    }

    private void ComposeReplayFloatingEditor(string key)
    {
        if (replayFloatingEditorKind != ReplayFloatingEditorKind.None)
        {
            ComposeKeyframeRml(replay: true);
            return;
        }
        DisposeKeyframeRml();
        double width = ReplayPropertyPopupWidth;
        double height = ReplayFloatingEditorHeight();
        ClampReplayFloatingEditorPosition(width, height);
        ElementBounds root = ElementBounds.Fixed(
            replayFloatingX,
            replayFloatingY,
            width,
            height);
        ElementBounds content = ElementBounds.Fixed(
            0d,
            FloatingEditorHeaderHeight,
            width,
            Math.Max(1d, height - FloatingEditorHeaderHeight));
        ElementBounds background = ElementBounds.Fill;
        background.WithChildren(content);
        CairoFont small = DirectorFont.WhiteSmallText();
        CairoFont detail = DirectorFont.WhiteDetailText().WithLineHeightMultiplier(1.16d);
        GuiComposer composer = capi.Gui.CreateCompo(key, root)
            .AddDirectorOpaqueWindowSurface(background)
            .AddDirectorDialogHeader(
                ReplayFloatingEditorTitle(),
                CloseReplayFloatingEditor,
                width,
                "floatingEditorHeader",
                UpdateFloatingEditorPosition)
            .BeginChildElements(content);

        int y = 10;
        switch (replayFloatingEditorKind)
        {
            case ReplayFloatingEditorKind.AddElement:
                ComposeReplayElementMenu(composer);
                break;
            case ReplayFloatingEditorKind.Track:
                ComposeReplayTrackEditor(composer, small, ref y);
                break;
            case ReplayFloatingEditorKind.CameraKeyframe:
                if (SelectedReplayViewportClip() is DirectorReplayViewportClip clip)
                {
                    ComposeReplayViewportInspector(composer, small, detail, clip, ref y);
                }
                break;
            case ReplayFloatingEditorKind.ElementKeyframe:
                if (SelectedReplayElementKeyframe() is DirectorReplayElementKeyframe keyframe)
                {
                    ComposeReplayElementKeyframeInspector(composer, small, keyframe, ref y);
                }
                break;
            case ReplayFloatingEditorKind.EntityPopup:
                ComposeReplayEntityPopup(composer, small, detail, ref y);
                break;
            case ReplayFloatingEditorKind.NewScene:
            case ReplayFloatingEditorKind.RenameScene:
                ComposeReplaySceneNameEditor(composer, small, ref y);
                break;
        }

        composer.EndChildElements();
        composer.zDepth = 35f;
        GuiComposer composed = composer.Compose(focusFirstElement: false);
        floatingEditorDialog.SetComposer(composed);
        RestoreReplayFloatingEditorInputs(composed);
        floatingEditorDialog.OpenForWorkspace();
    }

    private double ReplayFloatingEditorHeight()
        => replayFloatingEditorKind switch
        {
            ReplayFloatingEditorKind.AddElement => 84d
                + Math.Max(0, (ReplayViewportSourceOptions().Count + 1) / 2 - 1) * 38d,
            ReplayFloatingEditorKind.Track => 118d,
            ReplayFloatingEditorKind.NewScene or ReplayFloatingEditorKind.RenameScene => 122d,
            ReplayFloatingEditorKind.CameraKeyframe => 452d,
            ReplayFloatingEditorKind.ElementKeyframe => 112d
                + ReplayElementPropertyRowCount(SelectedReplayElementKeyframe()) * 34d,
            ReplayFloatingEditorKind.EntityPopup => 414d,
            _ => 122d
        };

    private static int ReplayElementPropertyRowCount(DirectorReplayElementKeyframe? keyframe)
    {
        if (keyframe is null)
        {
            return 0;
        }
        int commonRows = keyframe.TrackType == DirectorReplayElementTrackType.Timelapse ? 0 : 1;
        if (keyframe.TrackType is not (DirectorReplayElementTrackType.Freeze
            or DirectorReplayElementTrackType.BlockOverride
            or DirectorReplayElementTrackType.VoiceAudio
            or DirectorReplayElementTrackType.Timelapse))
        {
            commonRows++;
        }
        return commonRows + (keyframe.TrackType switch
        {
            DirectorReplayElementTrackType.CameraOrbit => 8,
            DirectorReplayElementTrackType.TrackEntity => 13,
            DirectorReplayElementTrackType.FollowEntity => keyframe.FollowEntityRole
                == DirectorReplayFollowEntityKeyframeRole.Start ? 17 : 1,
            DirectorReplayElementTrackType.CameraShake => 5,
            DirectorReplayElementTrackType.Fov => 1,
            DirectorReplayElementTrackType.Speed => 1,
            DirectorReplayElementTrackType.Timelapse => 1,
            DirectorReplayElementTrackType.TimeOfDay => 1,
            DirectorReplayElementTrackType.Freeze => 2,
            DirectorReplayElementTrackType.BlockOverride => 5,
            DirectorReplayElementTrackType.VoiceAudio => 1,
            _ => 0
        });
    }

    private string ReplayFloatingEditorTitle()
        => replayFloatingEditorKind switch
        {
            ReplayFloatingEditorKind.AddElement => Lang.Get("vsdirector:replay-element-add"),
            ReplayFloatingEditorKind.Track => Lang.Get("vsdirector:replay-element-track-options"),
            ReplayFloatingEditorKind.CameraKeyframe or ReplayFloatingEditorKind.ElementKeyframe
                => Lang.Get("vsdirector:replay-element-properties"),
            ReplayFloatingEditorKind.EntityPopup => Lang.Get("vsdirector:replay-entity-popup-title"),
            ReplayFloatingEditorKind.NewScene => Lang.Get("vsdirector:replay-scene-new"),
            ReplayFloatingEditorKind.RenameScene => Lang.Get("vsdirector:replay-scene-rename"),
            _ => string.Empty
        };

    private void ComposeReplayElementMenu(GuiComposer composer)
    {
        IReadOnlyList<ReplayViewportSourceOption> options = ReplayViewportSourceOptions();
        const double gap = 8d;
        double buttonWidth = (ReplayPropertyPopupWidth - 28d) / 2d;
        for (int index = 0; index < options.Count; index++)
        {
            ReplayViewportSourceOption option = options[index];
            int column = index % 2;
            int row = index / 2;
            composer.AddDirectorButton(
                option.Name,
                () => AddReplayElementTrack(option.Value),
                ElementBounds.Fixed(
                    10d + column * (buttonWidth + gap),
                    10d + row * 38d,
                    buttonWidth,
                    30d),
                EnumButtonStyle.Small,
                "replayElementMenu" + index.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void ComposeReplayTrackEditor(
        GuiComposer composer,
        CairoFont small,
        ref int y)
    {
        DirectorReplayElementTrack? track = CurrentReplayViewportTimeline().Tracks
            .FirstOrDefault(item => item.Id == replayFloatingTrackId);
        if (track is null)
        {
            return;
        }
        if (track.TrackType == DirectorReplayElementTrackType.VoiceAudio)
        {
            composer.AddDirectorSingleLineText(
                Ellipsize(track.Name, 40),
                small,
                ElementBounds.Fixed(10d, y + 6d, ReplayPropertyPopupWidth - 20d, 22d));
            y += 34;
            composer.AddDirectorButton(Lang.Get("vsdirector:replay-element-track-clear"),
                ClearReplayTrackKeyframes,
                ElementBounds.Fixed(10d, y, 150d, 30d), EnumButtonStyle.Small,
                "replayVoiceTrackClear");
            return;
        }
        AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:name"),
            "replayTrackName", replayFloatingTrackName, ref y);
        composer
            .AddDirectorButton(Lang.Get("vsdirector:apply"), ApplyReplayTrackEditor,
                ElementBounds.Fixed(10d, y, 108d, 30d), EnumButtonStyle.Normal,
                "replayTrackApply")
            .AddDirectorButton(Lang.Get("vsdirector:replay-element-track-clear"),
                ClearReplayTrackKeyframes,
                ElementBounds.Fixed(126d, y, 120d, 30d), EnumButtonStyle.Small,
                "replayTrackClear")
            .AddDirectorDangerButton(Lang.Get("vsdirector:delete"),
                DeleteReplayTrackFromEditor,
                ElementBounds.Fixed(254d, y, 112d, 30d),
                "replayTrackDelete");
    }

    private void ComposeReplaySceneNameEditor(
        GuiComposer composer,
        CairoFont small,
        ref int y)
    {
        AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:name"),
            "replaySceneName", replayFloatingSceneName, ref y);
        composer.AddDirectorButton(
            Lang.Get("vsdirector:apply"),
            SaveReplaySceneName,
            ElementBounds.Fixed(10d, y + 4d, 108d, 30d),
            EnumButtonStyle.Normal,
            "replaySceneNameApply");
    }

    private void RestoreReplayFloatingEditorInputs(GuiComposer composer)
    {
        SetInput(composer, "replayTrackName", replayFloatingTrackName);
        SetInput(composer, "replaySceneName", replayFloatingSceneName);
        SetInput(composer, "replayViewportName", replayViewportNameText);
        SetNumber(composer, "replayViewportStart", ParseDouble(replayViewportStartText, 0d));
        SetNumber(composer, "replayViewportX", ParseDouble(replayViewportXText, 0d));
        SetNumber(composer, "replayViewportY", ParseDouble(replayViewportYText, 0d));
        SetNumber(composer, "replayViewportZ", ParseDouble(replayViewportZText, 0d));
        SetNumber(composer, "replayViewportDimension", ParseDouble(replayViewportDimensionText, 0d));
        SetNumber(composer, "replayViewportYaw", ParseDouble(replayViewportYawText, 0d));
        SetNumber(composer, "replayViewportPitch", ParseDouble(replayViewportPitchText, 0d));
        SetNumber(composer, "replayViewportRoll", ParseDouble(replayViewportRollText, 0d));
        foreach (string field in ReplayElementNumericFields)
        {
            RestoreReplayPropertyValue(
                composer,
                "replayElement" + UpperFirst(field),
                ReplayElementNumber(field));
        }
        SetInput(composer, "replayElementEntityId", ReplayElementField("entityId"));
        SetInput(composer, "replayElementEntityActorKey", ReplayElementField("entityActorKey"));
        SetInput(composer, "replayElementBlockCode", ReplayElementField("blockCode"));
        SetInput(composer, "replayElementAudioAsset", ReplayElementField("audioAsset"));
        composer.GetSwitch("replayElementSplit")?.SetValue(
            ReplayElementField("split") == "true");
        composer.GetSwitch("replayElementFrozen")?.SetValue(
            ReplayElementField("frozen") == "true");
        composer.GetSwitch("replayElementClearView")?.SetValue(
            ReplayElementField("clearView") == "true");
        composer.GetSwitch("replayElementFollowClearView")?.SetValue(
            ReplayElementField("followClearView") == "true");
    }

    private void ClampReplayFloatingEditorPosition(double width, double height)
    {
        double screenWidth = capi.Render.FrameWidth / Math.Max(0.001d, GuiElement.scaled(1d));
        double screenHeight = capi.Render.FrameHeight / Math.Max(0.001d, GuiElement.scaled(1d));
        replayFloatingX = Math.Clamp(replayFloatingX, 0d, Math.Max(0d, screenWidth - width));
        replayFloatingY = Math.Clamp(replayFloatingY,
            DirectorWorkspacePanelLayout.ToolbarHeight,
            Math.Max(DirectorWorkspacePanelLayout.ToolbarHeight, screenHeight - height));
    }

    private void ComposeActionTimelinePanel(string key, DirectorGuiFocusSnapshot? focus = null)
    {
        DirectorWorkspacePanelLayout panel = layout.Panel("timeline");
        ElementBounds root = PanelRoot(panel);
        ElementBounds content = ElementBounds.Fixed(0, 0, panel.Width, panel.Height);
        ElementBounds background = ElementBounds.Fill;
        background.WithChildren(content);
        CairoFont detail = DirectorFont.WhiteDetailText().WithLineHeightMultiplier(1.16d);
        actionTimelineBounds = ElementBounds.Fixed(10, 48, panel.Width - 20, Math.Max(72d, panel.Height - 98d));
        GuiComposer composer = capi.Gui.CreateCompo(key, root)
            .AddDirectorOpaqueDockedSurface(background, DirectorSurfaceEdges.Top)
            .BeginChildElements(content)
            .AddSmallButton(Lang.Get("vsdirector:action-tools-playback"),
                () => SetActionTimelineToolSection("playback"), ElementBounds.Fixed(10, 6, 82, 30),
                actionTimelineToolSection == "playback" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "actionToolsPlayback")
            .AddSmallButton(Lang.Get("vsdirector:action-tools-view"),
                () => SetActionTimelineToolSection("view"), ElementBounds.Fixed(98, 6, 82, 30),
                actionTimelineToolSection == "view" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "actionToolsView");

        double toolX = 192d;
        if (actionTimelineToolSection == "playback")
        {
            composer
                .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:action-preview"),
                    PreviewSelectedAction, ElementBounds.Fixed(toolX, 6, 30, 30),
                    EnumButtonStyle.Normal, "actionCanvasPlay")
                .AddDirectorIconButton(DirectorIcon.Stop, Lang.Get("vsdirector:action-preview-stop"),
                    StopActionPreview, ElementBounds.Fixed(toolX + 36d, 6, 30, 30),
                    EnumButtonStyle.Small, "actionCanvasStop");
            toolX += 78d;
        }
        else if (actionTimelineToolSection == "view")
        {
            composer
                .AddDirectorIconButton(DirectorIcon.ZoomOut, Lang.Get("vsdirector:workspace-zoom-out"),
                    () => ZoomActionTimeline(1d / 1.25d), ElementBounds.Fixed(toolX, 6, 30, 30),
                    EnumButtonStyle.Small, "actionCanvasZoomOut")
                .AddDirectorIconButton(DirectorIcon.ZoomIn, Lang.Get("vsdirector:workspace-zoom-in"),
                    () => ZoomActionTimeline(1.25d), ElementBounds.Fixed(toolX + 36d, 6, 30, 30),
                    EnumButtonStyle.Small, "actionCanvasZoomIn")
                .AddDirectorIconButton(DirectorIcon.Fit, Lang.Get("vsdirector:workspace-zoom-fit"),
                    FitActionTimeline, ElementBounds.Fixed(toolX + 72d, 6, 30, 30),
                    EnumButtonStyle.Small, "actionCanvasFit");
            toolX += 114d;
        }
        double statusWidth = panel.Width - toolX - 18d;
        if (statusWidth >= 80d)
        {
            composer.AddDynamicText(
                ActionCanvasStatus(),
                detail,
                ElementBounds.Fixed(toolX + 8d, 8, statusWidth, 26),
                "actionCanvasStatus");
        }
        composer
            .AddCustomRender(actionTimelineBounds, RenderActionTimeline)
            .AddDynamicText(ActionCanvasFooter(), detail,
                ElementBounds.Fixed(10, panel.Height - 42, panel.Width - 20, 32), "actionCanvasFooter")
            .EndChildElements();
        composer.zDepth = 35f;
        Composers[key] = composer.Compose(focusFirstElement: false);
        SetButtonEnabledIfPresent(composer, "actionCanvasUndo", actionEditHistory.CanUndo);
        SetButtonEnabledIfPresent(composer, "actionCanvasRedo", actionEditHistory.CanRedo);
        SetButtonEnabledIfPresent(composer, "actionCanvasCopy", selectedActionFrames.Count > 0);
        SetButtonEnabledIfPresent(composer, "actionCanvasDelete", selectedActionFrames.Count > 0);
        SetButtonEnabledIfPresent(composer, "actionCanvasClear", selectedActionFrames.Count > 0);
        SetButtonEnabledIfPresent(composer, "actionCanvasPaste", actionFrameClipboard.Count > 0);
        DirectorGuiFocus.Restore(Composers[key], focus);
    }

    private void ComposeActionLibraryCanvas(string key)
    {
        DirectorGuiFocusSnapshot? focus = DirectorGuiFocus.Capture(Composers[key]);
        DirectorWorkspacePanelLayout panel = layout.Panel("workflow");
        ElementBounds root = PanelRoot(panel);
        ElementBounds content = ElementBounds.Fixed(0, 0, panel.Width, panel.Height);
        ElementBounds background = ElementBounds.Fill;
        background.WithChildren(content);
        CairoFont detail = DirectorFont.WhiteDetailText().WithLineHeightMultiplier(1.12d);
        double searchX = actionLibraryMode == "models" ? 382d : 346d;
        double searchWidth = Math.Clamp(panel.Width - searchX - 46d, 80d, 420d);
        double searchButtonX = searchX + searchWidth + 6d;
        GuiComposer composer = capi.Gui.CreateCompo(key, root)
            .AddDirectorOpaqueDockedSurface(background, DirectorSurfaceEdges.Top)
            .BeginChildElements(content)
            .AddSmallButton(Lang.Get("vsdirector:action-library-actions"),
                () => SetActionLibraryMode("actions"), ElementBounds.Fixed(10, 6, 104, 30),
                actionLibraryMode == "actions" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "actionLibraryActions")
            .AddSmallButton(Lang.Get("vsdirector:action-library-expressions"),
                () => SetActionLibraryMode("expressions"), ElementBounds.Fixed(120, 6, 104, 30),
                actionLibraryMode == "expressions" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "actionLibraryExpressions")
            .AddSmallButton(Lang.Get("vsdirector:action-library-models"),
                () => SetActionLibraryMode("models"), ElementBounds.Fixed(230, 6, 104, 30),
                actionLibraryMode == "models" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "actionLibraryModels");
        if (actionLibraryMode == "models")
        {
            composer.AddDirectorIconButton(
                DirectorIcon.Add,
                Lang.Get("vsdirector:model-new"),
                NewModelEditor,
                ElementBounds.Fixed(340, 7, 30, 28),
                EnumButtonStyle.Normal,
                "modelNew");
        }
        composer.AddTextInput(
            ElementBounds.Fixed(searchX, 7, searchWidth, 28),
            null,
            DirectorFont.TextInput(),
            "actionLibrarySearch")
            .AddDirectorIconButton(
                DirectorIcon.Search,
                Lang.Get("vsdirector:search"),
                ApplyActionLibrarySearch,
                ElementBounds.Fixed(searchButtonX, 7, 30d, 28),
                EnumButtonStyle.Small,
                "actionLibrarySearchApply");

        if (actionLibraryMode == "models")
        {
            bool showModelExport = owner.Config.ShowPackageImportExport;
            double importWidth = Math.Clamp(panel.Width - (showModelExport ? 96d : 60d), 180d, 596d);
            double importButtonX = 16d + importWidth;
            composer
                .AddTextInput(ElementBounds.Fixed(10, 42, importWidth, 28),
                    null, DirectorFont.TextInput(), "modelLibraryImportPath")
                .AddDirectorIconButton(DirectorIcon.Import, Lang.Get("vsdirector:import"),
                    ImportModelFileFromLibrary,
                    ElementBounds.Fixed(importButtonX, 42, 30d, 28), EnumButtonStyle.Small,
                    "modelLibraryImport");
            if (showModelExport)
            {
                composer.AddDirectorIconButton(DirectorIcon.Export, Lang.Get("vsdirector:export"),
                    ExportSelectedModelFromLibrary,
                    ElementBounds.Fixed(importButtonX + 36d, 42, 30d, 28), EnumButtonStyle.Small,
                    "modelLibraryExport");
            }
        }
        else
        {
            double newNameX = 10d;
            if (actionLibraryMode == "actions")
            {
                string[] categoryCodes = { "all", "vanilla", "player", "npc", "creature", "effect", "favorite" };
                string[] categoryNames = categoryCodes.Select(code => Lang.Get(
                    code == "all" ? "vsdirector:action-category-all" : "vsdirector:action-category-" + code)).ToArray();
                composer.AddDropDown(
                    categoryCodes,
                    categoryNames,
                    Math.Max(0, Array.IndexOf(categoryCodes, actionCategoryFilter)),
                    OnActionCategoryFilterChanged,
                    ElementBounds.Fixed(10, 42, 130d, 28),
                    "actionLibraryCategory");
                newNameX = 146d;
            }
            double reservedButtons = owner.Config.ShowPackageImportExport ? 160d : 52d;
            double createNameWidth = Math.Clamp(panel.Width - newNameX - reservedButtons, 100d, 468d);
            double createButtonX = newNameX + createNameWidth + 6d;
            composer
                .AddTextInput(ElementBounds.Fixed(newNameX, 42, createNameWidth, 28), null,
                    DirectorFont.TextInput(), "actionLibraryNewName")
                .AddDirectorIconButton(DirectorIcon.Add, Lang.Get("vsdirector:create"),
                    CreateActionFromLibrary,
                    ElementBounds.Fixed(createButtonX, 42, 30d, 28), EnumButtonStyle.Normal,
                    "actionLibraryCreate");
            if (owner.Config.ShowPackageImportExport)
            {
                double actionImportWidth = Math.Clamp(panel.Width - 58d, 180d, 560d);
                composer
                    .AddDirectorIconButton(DirectorIcon.Import, Lang.Get("vsdirector:action-import-mods"),
                        ImportExternalActions,
                        ElementBounds.Fixed(createButtonX + 36d, 42, 30d, 28), EnumButtonStyle.Small,
                        "actionLibraryImportMods")
                    .AddDirectorIconButton(DirectorIcon.Export, Lang.Get("vsdirector:export"),
                        ExportActionJson,
                        ElementBounds.Fixed(createButtonX + 72d, 42, 30d, 28), EnumButtonStyle.Small,
                        "actionLibraryExport")
                    .AddTextInput(ElementBounds.Fixed(10, 76, actionImportWidth, 28),
                        null, DirectorFont.TextInput(), "actionLibraryImportPath")
                    .AddDirectorIconButton(DirectorIcon.Import, Lang.Get("vsdirector:import"),
                        ImportActionFileFromLibrary,
                        ElementBounds.Fixed(16d + actionImportWidth, 76, 30d, 28), EnumButtonStyle.Small,
                        "actionLibraryImport");
            }
        }

        IReadOnlyList<ActionLibraryCardEntry> entries = BuildActionLibraryEntries();
        const double gap = 10d;
        double top = actionLibraryMode == "models"
            ? 82d
            : owner.Config.ShowPackageImportExport ? 116d : 82d;
        const double pagerHeight = 42d;
        double availableWidth = Math.Max(180d, panel.Width - 20d);
        int columns = Math.Clamp((int)Math.Floor((availableWidth + gap) / 200d), 1, 4);
        double cardWidth = (availableWidth - gap * (columns - 1)) / columns;
        const double cardHeight = 96d;
        int rows = Math.Max(1, (int)Math.Floor((panel.Height - top - pagerHeight) / (cardHeight + gap)));
        int pageSize = Math.Max(1, columns * rows);
        int pages = Math.Max(1, (entries.Count + pageSize - 1) / pageSize);
        actionLibraryPageIndex = Math.Clamp(actionLibraryPageIndex, 0, pages - 1);
        int first = actionLibraryPageIndex * pageSize;
        int count = Math.Min(pageSize, entries.Count - first);

        for (int visibleIndex = 0; visibleIndex < count; visibleIndex++)
        {
            ActionLibraryCardEntry entry = entries[first + visibleIndex];
            int column = visibleIndex % columns;
            int row = visibleIndex / columns;
            double x = 10d + column * (cardWidth + gap);
            double y = top + row * (cardHeight + gap);
            ElementBounds cardBounds = ElementBounds.Fixed(x, y, cardWidth, cardHeight);
            string keySuffix = visibleIndex.ToString(CultureInfo.InvariantCulture);
            composer
                .AddDirectorCard(cardBounds, "actionLibraryCard-" + keySuffix)
                .AddStaticText(entry.Badge, detail,
                    ElementBounds.Fixed(x + 12d, y + 10d, cardWidth - 24d, 20))
                .AddDirectorSingleLineText(entry.Detail, detail,
                    ElementBounds.Fixed(x + 12d, y + 34d, cardWidth - 24d, 18d))
                .AddSmallButton(Ellipsize(entry.Name, Math.Max(12, (int)(cardWidth / 8d))),
                    () => EditActionLibraryCard(entry),
                    ElementBounds.Fixed(x + 8d, y + 60d, cardWidth - 16d, 28d),
                    entry.Selected ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                    "actionLibraryEdit-" + keySuffix);
        }

        double pagerY = panel.Height - 34d;
        composer
            .AddDirectorIconButton(DirectorIcon.Previous, Lang.Get("vsdirector:previous"),
                () => ChangeActionLibraryPage(-1), ElementBounds.Fixed(panel.Width / 2d - 76d, pagerY, 30, 26),
                EnumButtonStyle.Small, "actionLibraryPrev")
            .AddStaticText($"{actionLibraryPageIndex + 1}/{pages}",
                detail.WithOrientation(EnumTextOrientation.Center),
                ElementBounds.Fixed(panel.Width / 2d - 40d, pagerY + 4d, 80d, 20d))
            .AddDirectorIconButton(DirectorIcon.Next, Lang.Get("vsdirector:next"),
                () => ChangeActionLibraryPage(1), ElementBounds.Fixed(panel.Width / 2d + 46d, pagerY, 30, 26),
                EnumButtonStyle.Small, "actionLibraryNext")
            .EndChildElements();

        composer.zDepth = 35f;
        Composers[key] = composer.Compose(focusFirstElement: false);
        Composers[key].GetTextInput("actionLibrarySearch")?.SetValue(actionLibrarySearch);
        Composers[key].GetTextInput("actionLibrarySearch")?.SetPlaceHolderText(
            Lang.Get("vsdirector:search-placeholder"));
        Composers[key].GetTextInput("actionLibraryNewName")?.SetValue(newName);
        Composers[key].GetTextInput("actionLibraryNewName")?.SetPlaceHolderText(
            Lang.Get(actionLibraryMode == "expressions"
                ? "vsdirector:expression-name-placeholder"
                : "vsdirector:action-name-placeholder"));
        Composers[key].GetTextInput("actionLibraryImportPath")?.SetValue(actionImportPathText);
        Composers[key].GetTextInput("actionLibraryImportPath")?.SetPlaceHolderText(
            Lang.Get("vsdirector:action-import-path-placeholder"));
        Composers[key].GetTextInput("modelLibraryImportPath")?.SetValue(modelImportPathText);
        Composers[key].GetTextInput("modelLibraryImportPath")?.SetPlaceHolderText(
            Lang.Get("vsdirector:model-import-path-placeholder"));
        DirectorGuiFocus.Restore(Composers[key], focus);
    }

    private bool ZoomActionTimeline(double factor)
    {
        actionTimelineZoom = Math.Clamp(actionTimelineZoom * factor, 1d, 64d);
        actionTimelinePanSeconds = Math.Clamp(
            actionTimelinePanSeconds,
            0d,
            Math.Max(0d, (actionEdit?.DurationSeconds ?? 0d) - ActionTimelineVisibleDuration));
        ComposeTimeline();
        return true;
    }

    private bool FitActionTimeline()
    {
        actionTimelineZoom = 1d;
        actionTimelinePanSeconds = 0d;
        ComposeTimeline();
        return true;
    }

    private ActionFrameSelection? FindActionFrameAt(
        DirectorActionLayer layer,
        double mouseX,
        ElementBounds bounds)
    {
        double labelWidth = Math.Min(GuiElement.scaled(130d), bounds.OuterWidth * 0.24d);
        double timelineWidth = Math.Max(1d, bounds.OuterWidth - labelWidth);
        double threshold = GuiElement.scaled(8d);
        DirectorActionFrame? nearest = layer.Frames
            .Where(frame => frame.TimeSeconds >= actionTimelinePanSeconds - DirectorActionNormalizer.TimeEpsilon
                && frame.TimeSeconds <= actionTimelinePanSeconds + ActionTimelineVisibleDuration
                    + DirectorActionNormalizer.TimeEpsilon)
            .OrderBy(frame => Math.Abs(
                bounds.renderX + labelWidth
                    + (frame.TimeSeconds - actionTimelinePanSeconds) / ActionTimelineVisibleDuration * timelineWidth
                    - mouseX))
            .FirstOrDefault();
        if (nearest is null)
        {
            return null;
        }
        double frameX = bounds.renderX + labelWidth
            + (nearest.TimeSeconds - actionTimelinePanSeconds) / ActionTimelineVisibleDuration * timelineWidth;
        return Math.Abs(frameX - mouseX) <= threshold
            ? new ActionFrameSelection(layer.Id, nearest.TimeSeconds)
            : null;
    }

    private bool SelectAllActionFrames()
    {
        selectedActionFrames.Clear();
        if (actionEdit is not null)
        {
            foreach (DirectorActionLayer layer in actionEdit.Layers)
            {
                foreach (DirectorActionFrame frame in layer.Frames)
                {
                    selectedActionFrames.Add(new ActionFrameSelection(layer.Id, frame.TimeSeconds));
                }
            }
        }
        ComposeTimeline();
        return true;
    }

    private bool ClearActionFrameSelection()
    {
        selectedActionFrames.Clear();
        ComposeTimeline();
        return true;
    }

    private bool CopySelectedActionFrames()
    {
        if (actionEdit is null || selectedActionFrames.Count == 0)
        {
            return true;
        }
        actionFrameClipboard.Clear();
        double origin = selectedActionFrames.Min(selection => selection.TimeSeconds);
        foreach (DirectorActionLayer layer in actionEdit.Layers)
        {
            foreach (DirectorActionFrame frame in layer.Frames.Where(frame => selectedActionFrames.Contains(
                         new ActionFrameSelection(layer.Id, frame.TimeSeconds))))
            {
                actionFrameClipboard.Add(new ActionFrameClipboardItem(
                    layer.Id,
                    layer.Name,
                    layer.Kind,
                    layer.BlendMode,
                    frame.TimeSeconds - origin,
                    frame.DeepClone()));
            }
        }
        ComposeTimeline();
        return true;
    }

    private bool PasteActionFramesAtPlayhead()
    {
        if (actionEdit is null || actionFrameClipboard.Count == 0)
        {
            return true;
        }
        DirectorActionAsset before = actionEdit.DeepClone();
        selectedActionFrames.Clear();
        foreach (IGrouping<Guid, ActionFrameClipboardItem> group in actionFrameClipboard.GroupBy(item => item.LayerId))
        {
            ActionFrameClipboardItem template = group.First();
            DirectorActionLayer? target = actionEdit.Layers.FirstOrDefault(layer => layer.Id == group.Key)
                ?? actionEdit.Layers.FirstOrDefault(layer => layer.Kind == template.Kind
                    && string.Equals(layer.Name, template.LayerName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                target = new DirectorActionLayer
                {
                    Id = Guid.NewGuid(),
                    Name = template.LayerName,
                    Kind = template.Kind,
                    BlendMode = template.BlendMode,
                    Order = actionEdit.Layers.Count
                };
                actionEdit.Layers.Add(target);
            }
            foreach (ActionFrameClipboardItem item in group)
            {
                DirectorActionFrame frame = item.Frame.DeepClone();
                frame.TimeSeconds = Math.Max(0d, actionPlayhead + item.OffsetSeconds);
                frame.State.TimeSeconds = frame.TimeSeconds;
                target.Frames.Add(frame);
                selectedActionFrames.Add(new ActionFrameSelection(target.Id, frame.TimeSeconds));
            }
        }
        actionEdit = DirectorActionNormalizer.Normalize(actionEdit);
        selectedActionLayerId = selectedActionFrames.FirstOrDefault().LayerId;
        actionEditHistory.RecordChange(before, actionEdit);
        SyncSelectedActionLayerInputs();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private bool DeleteSelectedActionFrames()
    {
        if (actionEdit is null || selectedActionFrames.Count == 0)
        {
            return true;
        }
        DirectorActionAsset before = actionEdit.DeepClone();
        foreach (DirectorActionLayer layer in actionEdit.Layers)
        {
            layer.Frames.RemoveAll(frame => selectedActionFrames.Contains(
                new ActionFrameSelection(layer.Id, frame.TimeSeconds)));
        }
        selectedActionFrames.Clear();
        actionEdit = DirectorActionNormalizer.Normalize(actionEdit);
        actionPlayhead = Math.Min(actionPlayhead, actionEdit.DurationSeconds);
        actionEditHistory.RecordChange(before, actionEdit);
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private bool UndoActionEdit()
    {
        if (actionEdit is not null && actionEditHistory.TryUndo(actionEdit, out DirectorActionAsset restored))
        {
            RestoreActionEdit(restored);
        }
        return true;
    }

    private bool RedoActionEdit()
    {
        if (actionEdit is not null && actionEditHistory.TryRedo(actionEdit, out DirectorActionAsset restored))
        {
            RestoreActionEdit(restored);
        }
        return true;
    }

    private void RestoreActionEdit(DirectorActionAsset restored)
    {
        actionEdit = restored;
        selectedActionFrames.Clear();
        selectedActionLayerId = actionEdit.Layers.FirstOrDefault()?.Id ?? Guid.Empty;
        actionPlayhead = Math.Min(actionPlayhead, actionEdit.DurationSeconds);
        SyncSelectedActionLayerInputs();
        ComposeInspector();
        ComposeTimeline();
    }

    private double ActionTimelineVisibleDuration
        => Math.Max(0.05d, Math.Max(1d, actionEdit?.DurationSeconds ?? 1d) / actionTimelineZoom);

    private string ActionCanvasStatus()
        => actionEdit is null
            ? string.Empty
            : Lang.Get(
                "vsdirector:action-editor-status",
                actionEdit.Layers.Count,
                actionEdit.FrameCount,
                actionTimelineZoom.ToString("0.##", CultureInfo.InvariantCulture),
                actionPlayhead.ToString("0.###", CultureInfo.InvariantCulture))
                + (selectedActionFrames.Count == 0
                    ? string.Empty
                    : " | " + Lang.Get("vsdirector:action-selected-frames", selectedActionFrames.Count));

    private string ActionCanvasFooter()
        => actionEdit is null
            ? string.Empty
            : Lang.Get(
                "vsdirector:action-editor-footer",
                actionEdit.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                actionEdit.Events.Count,
                actionEdit.Tags.Count == 0 ? Lang.Get("vsdirector:none") : string.Join(", ", actionEdit.Tags));

    private IReadOnlyList<ActionLibraryCardEntry> BuildActionLibraryEntries()
    {
        if (actionLibraryMode == "models")
        {
            Dictionary<string, DirectorExternalModelInfo> external = owner.ActionModels
                .ToDictionary(model => model.Code, StringComparer.OrdinalIgnoreCase);
            List<ActionLibraryCardEntry> entries = new();
            foreach (DirectorExternalModelInfo model in external.Values)
            {
                DirectorModelAsset? saved = owner.Models.FirstOrDefault(candidate => string.Equals(
                    candidate.ModelCode,
                    model.Code,
                    StringComparison.OrdinalIgnoreCase));
                string name = saved?.Name ?? model.Name;
                string group = saved?.Group.Length > 0 ? saved.Group : model.Group;
                if (!ActionLibrarySearchMatch(name, model.Code, group, saved?.Description))
                {
                    continue;
                }
                DirectorModelAsset cardModel = saved?.DeepClone() ?? new DirectorModelAsset
                {
                    Name = model.Name,
                    ModelCode = model.Code,
                    EntityCode = model.EntityCode,
                    Group = model.Group,
                    SourceModId = string.Equals(
                            model.Code,
                            VSDirectorModSystem.VanillaPlayerModelCode,
                            StringComparison.OrdinalIgnoreCase)
                        || model.Code.StartsWith("entity/", StringComparison.OrdinalIgnoreCase)
                            ? model.EntityCode.Split(':', 2)[0]
                            : model.PreviewOnly ? "vsdirector" : "playermodellib",
                    Size = 1f
                };
                if (cardModel.EntityCode.Length == 0)
                {
                    cardModel.EntityCode = model.EntityCode;
                }
                entries.Add(new ActionLibraryCardEntry(
                    Guid.Empty,
                    model.Code,
                    name,
                    saved is not null
                        && (saved.ShapeSourcePath.Length > 0 || saved.ShapeAssetLocation.Length > 0)
                            ? owner.Vsmc2ShapeStatus(saved)
                            : model.PreviewOnly
                                ? Lang.Get("vsdirector:model-green-screen-preview-only")
                                : Lang.Get("vsdirector:model-card-detail", model.Animations.Count, model.Expressions.Count),
                    string.IsNullOrWhiteSpace(group) ? Lang.Get("vsdirector:model-library-source") : group,
                    true,
                    string.Equals(actionLibrarySelectedModelCode, model.Code, StringComparison.OrdinalIgnoreCase),
                    cardModel));
            }
            foreach (DirectorModelAsset saved in owner.Models.Where(saved => !external.ContainsKey(saved.ModelCode)))
            {
                if (!ActionLibrarySearchMatch(saved.Name, saved.ModelCode, saved.Group, saved.Description))
                {
                    continue;
                }
                entries.Add(new ActionLibraryCardEntry(
                    Guid.Empty,
                    saved.ModelCode,
                    saved.Name,
                    saved.ShapeSourcePath.Length > 0 || saved.ShapeAssetLocation.Length > 0
                        ? owner.Vsmc2ShapeStatus(saved)
                        : Lang.Get("vsdirector:model-card-unavailable"),
                    saved.Group.Length == 0 ? Lang.Get("vsdirector:model-library-saved") : saved.Group,
                    true,
                    string.Equals(actionLibrarySelectedModelCode, saved.ModelCode, StringComparison.OrdinalIgnoreCase),
                    saved.DeepClone()));
            }
            return entries
                .OrderByDescending(entry => entry.Model?.Favorite == true)
                .ThenByDescending(entry => string.Equals(
                    entry.ModelCode,
                    VSDirectorModSystem.VanillaPlayerModelCode,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(entry => entry.Badge, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        bool expressions = actionLibraryMode == "expressions";
        return owner.Actions
            .Where(action => (action.Category == DirectorActionCategory.Expression) == expressions)
            .Where(action => expressions || ActionCategoryMatches(action))
            .Where(action => ActionLibrarySearchMatch(
                action.Name,
                action.Description,
                action.Author,
                string.Join(" ", action.Tags)))
            .Select(action => new ActionLibraryCardEntry(
                action.Id,
                string.Empty,
                action.Name,
                Lang.Get(
                    "vsdirector:action-card-detail",
                    action.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                    action.Layers.Count),
                ActionCategoryLabel(action.Category),
                false,
                actionEdit?.Id == action.Id,
                null))
            .ToArray();
    }

    private bool ActionLibrarySearchMatch(params string?[] values)
    {
        string query = actionLibrarySearch.Trim();
        return query.Length == 0 || values.Any(value => value?.Contains(
            query,
            StringComparison.OrdinalIgnoreCase) == true);
    }

    private bool SetActionLibraryMode(string mode)
    {
        CaptureActionLibrarySearch();
        actionLibraryMode = mode;
        if (mode == "actions" && actionCategoryFilter == "expression")
        {
            actionCategoryFilter = "all";
        }
        actionLibraryPageIndex = 0;
        owner.StopActionPreview();
        if (mode != "models")
        {
            actionLibrarySelectedModelCode = string.Empty;
            actionPreviewModelCode = string.Empty;
            modelEdit = null;
            owner.WatchVsmc2Shape(null);
        }
        else
        {
            actionLibrarySelectedModelCode = string.Empty;
            actionPreviewModelCode = string.Empty;
            modelEdit = null;
            ComposeInspector();
            ComposeResources();
            return true;
        }
        ActionLibraryCardEntry? entry = BuildActionLibraryEntries().FirstOrDefault();
        if (entry is not null)
        {
            ActivateActionLibraryCard(entry);
        }
        else
        {
            ComposeInspector();
            ComposeWorkflowCanvas();
        }
        ComposeResources();
        return true;
    }

    private bool SetActionInspectorSection(string sectionCode)
    {
        CaptureActionInputs();
        PreserveInputs();
        actionInspectorSection = sectionCode;
        ComposeInspector();
        return true;
    }

    private bool SetInspectorSection(
        string sectionCode,
        InspectorSectionTarget target = InspectorSectionTarget.Normal)
    {
        if (!CaptureInspectorDraft())
        {
            return true;
        }
        PreserveInputs();
        if (target == InspectorSectionTarget.Replay)
        {
            replayInspectorSection = sectionCode;
        }
        else if (target == InspectorSectionTarget.ReplayView)
        {
            replayViewInspectorSection = sectionCode;
        }
        else
        {
            inspectorSection = sectionCode;
        }
        workflowAudioEditorExpanded = target == InspectorSectionTarget.Normal
            && section == DirectorWorkspaceSection.Workflows
            && sectionCode == "audio";
        if (workflowAudioEditorExpanded)
        {
            workflowAudioCreating = false;
            workflowAudioFieldsId = Guid.Empty;
            SyncWorkflowAudioSelection();
        }
        ComposeInspector();
        return true;
    }

    private bool SetActionTimelineToolSection(string sectionCode)
    {
        actionTimelineToolSection = sectionCode;
        DirectorGuiFocusSnapshot? focus = DirectorGuiFocus.Capture(Composers[TimelineKey]);
        Composers[TimelineKey]?.Dispose();
        ComposeActionTimelinePanel(TimelineKey, focus);
        return true;
    }

    private void EnsureActionLibrarySelection(bool restartCurrent)
    {
        if (section != DirectorWorkspaceSection.Actions)
        {
            return;
        }
        if (actionLibraryMode == "models" && modelEdit is not null)
        {
            if (restartCurrent)
            {
                PreviewModelEditor();
            }
            return;
        }
        if (actionLibraryMode == "models")
        {
            return;
        }
        bool expectsExpression = actionLibraryMode == "expressions";
        if (actionLibraryMode != "models"
            && actionEdit is not null
            && (actionEdit.Category == DirectorActionCategory.Expression) == expectsExpression)
        {
            if (restartCurrent)
            {
                PreviewSelectedAction();
            }
            return;
        }
        ActionLibraryCardEntry? entry = BuildActionLibraryEntries().FirstOrDefault();
        if (entry is not null)
        {
            ActivateActionLibraryCard(entry);
        }
    }

    private bool ChangeActionLibraryPage(int delta)
    {
        CaptureActionLibrarySearch();
        actionLibraryPageIndex = Math.Max(0, actionLibraryPageIndex + delta);
        ComposeWorkflowCanvas();
        return true;
    }

    private void CaptureActionLibrarySearch()
    {
        GuiComposer? composer = floatingEditorDialog.Composer;
        GuiComposer? resources = Composers[ResourceMenuKey];
        actionLibrarySearch = resources?.GetTextInput("actionResourceSearch")?.GetText()
            ?? composer?.GetTextInput("actionLibrarySearch")?.GetText()
            ?? actionLibrarySearch;
        actionResourceSearch = actionLibrarySearch;
        newName = resources?.GetTextInput("actionLibraryNewName")?.GetText()
            ?? composer?.GetTextInput("actionLibraryNewName")?.GetText()
            ?? newName;
        actionImportPathText = resources?.GetTextInput("actionLibraryImportPath")?.GetText()
            ?? composer?.GetTextInput("actionLibraryImportPath")?.GetText()
            ?? actionImportPathText;
        modelImportPathText = resources?.GetTextInput("modelLibraryImportPath")?.GetText()
            ?? composer?.GetTextInput("modelLibraryImportPath")?.GetText()
            ?? modelImportPathText;
    }

    private bool CreateActionFromLibrary()
    {
        CaptureActionLibrarySearch();
        if (!owner.TryCreateAction(newName, out DirectorActionAsset? created, out _) || created is null)
        {
            return true;
        }
        if (actionLibraryMode == "expressions")
        {
            created.Category = DirectorActionCategory.Expression;
            owner.TrySaveAction(created, out _);
        }
        newName = string.Empty;
        SelectAndPreviewActionLibraryCard(created.Id);
        return true;
    }

    private bool ImportActionFileFromLibrary()
    {
        CaptureActionLibrarySearch();
        if (actionImportPathText.Length > 0
            && owner.TryImportActionFile(actionImportPathText, out _, out _))
        {
            ComposeInspector();
            ComposeWorkflowCanvas();
        }
        return true;
    }

    private bool ApplyActionLibrarySearch()
    {
        CaptureActionLibrarySearch();
        actionLibraryPageIndex = 0;
        ComposeWorkflowCanvas();
        return true;
    }

    private bool ApplyActionResourceSearch()
    {
        if (Composers[ResourceMenuKey] is GuiComposer composer)
        {
            actionResourceSearch = composer.GetTextInput("actionResourceSearch")?.GetText()
                ?? actionResourceSearch;
        }
        actionLibrarySearch = actionResourceSearch;
        resourceScrollOffset = 0;
        ComposeResources();
        return true;
    }

    private bool ActivateActionLibraryCard(ActionLibraryCardEntry entry)
    {
        return entry.IsModel
            ? SelectModelLibraryCard(entry)
            : SelectAndPreviewActionLibraryCard(entry.ActionId);
    }

    private bool EditActionLibraryCard(ActionLibraryCardEntry entry)
    {
        return entry.IsModel ? SelectModelLibraryCard(entry) : SelectAndPreviewActionLibraryCard(entry.ActionId);
    }

    private bool SelectAndPreviewActionLibraryCard(Guid id)
    {
        SelectAction(id);
        modelEdit = null;
        return PreviewSelectedAction();
    }

    private bool SelectModelLibraryCard(ActionLibraryCardEntry entry)
    {
        FlushPendingAutoSave();
        CaptureActionLibrarySearch();
        if (string.Equals(
                actionLibrarySelectedModelCode,
                entry.ModelCode,
                StringComparison.OrdinalIgnoreCase))
        {
            owner.StopActionPreview();
            actionLibrarySelectedModelCode = string.Empty;
            actionPreviewModelCode = string.Empty;
            modelEdit = null;
            resourceScrollOffset = 0;
            ComposeInspector();
            ComposeResources();
            ComposeWorkflowCanvas();
            return true;
        }
        actionLibrarySelectedModelCode = entry.ModelCode;
        actionPreviewModelCode = entry.ModelCode;
        modelEdit = entry.Model?.DeepClone();
        modelNameText = modelEdit?.Name ?? entry.Name;
        modelCodeText = modelEdit?.ModelCode ?? entry.ModelCode;
        modelEntityCodeText = string.IsNullOrWhiteSpace(modelEdit?.EntityCode)
            ? VSDirectorModSystem.VanillaPlayerModelCode
            : modelEdit.EntityCode;
        modelGroupText = modelEdit?.Group ?? string.Empty;
        modelDescriptionText = modelEdit?.Description ?? string.Empty;
        modelSizeText = (modelEdit?.Size ?? 1f).ToString(CultureInfo.InvariantCulture);
        modelShapeAssetText = modelEdit?.ShapeAssetLocation ?? string.Empty;
        modelShapeSourceText = modelEdit?.ShapeSourcePath ?? string.Empty;
        owner.WatchVsmc2Shape(modelEdit);
        resourceScrollOffset = 0;
        ComposeInspector();
        ComposeResources();
        return PreviewModelEditor();
    }

    private bool CloseModelResourceSelection()
    {
        owner.StopActionPreview();
        actionLibrarySelectedModelCode = string.Empty;
        actionPreviewModelCode = string.Empty;
        actionLibrarySearch = string.Empty;
        actionResourceSearch = string.Empty;
        modelEdit = null;
        owner.WatchVsmc2Shape(null);
        resourceScrollOffset = 0;
        ComposeInspector();
        ComposeResources();
        return true;
    }

    private void ComposePathCanvas(string key)
    {
        DirectorWorkspacePanelLayout panel = layout.Panel("workflow");
        panel.Dock = DirectorWorkspaceDock.Floating;
        ElementBounds root = PanelRoot(panel);
        double contentHeight = Math.Max(1d, panel.Height - FloatingEditorHeaderHeight);
        ElementBounds content = ElementBounds.Fixed(
            0,
            FloatingEditorHeaderHeight,
            panel.Width,
            contentHeight);
        ElementBounds background = ElementBounds.Fill;
        background.WithChildren(content);
        CairoFont small = DirectorFont.WhiteSmallText();
        CairoFont detail = DirectorFont.WhiteDetailText().WithLineHeightMultiplier(1.16d);
        const int pathToolbarButtonCount = 18;
        int toolbarColumns = Math.Clamp((int)Math.Floor((panel.Width - 20d) / 36d), 4, pathToolbarButtonCount);
        int toolbarRows = (pathToolbarButtonCount + toolbarColumns - 1) / toolbarColumns;
        ElementBounds ToolbarButton(int index)
            => ElementBounds.Fixed(10 + index % toolbarColumns * 36d,
                6 + index / toolbarColumns * 36d, 30, 30);
        double controlsY = 6d + toolbarRows * 36d;
        double batchY = controlsY + 34d;
        bool compactBatchLayout = panel.Width < 700d;
        double batchHeight = compactBatchLayout ? 102d : 34d;
        double globalLabelY = batchY + batchHeight;
        double globalTimelineY = globalLabelY + 16d;
        double localLabelY = globalTimelineY + 52d;
        double editorTop = localLabelY + 16d;
        pathGlobalTimelineBounds = ElementBounds.Fixed(10, globalTimelineY, panel.Width - 20, 48);
        pathEditorTimelineBounds = ElementBounds.Fixed(
            10,
            editorTop,
            panel.Width - 20,
            Math.Max(150d, contentHeight - editorTop - 48d));

        string[] channelCodes = { "x", "y", "z", "yaw", "pitch", "roll", "fov" };
        string[] channelNames = { "X", "Y", "Z", "Yaw", "Pitch", "Roll", "FOV" };
        string[] batchScopeCodes = { "selected", "all" };
        string[] batchScopeNames =
        {
            Lang.Get("vsdirector:path-batch-selected"),
            Lang.Get("vsdirector:path-batch-all")
        };
        string[] batchPropertyCodes = { "fov", "yaw", "pitch", "roll", "hold", "startspeed", "endspeed" };
        string[] batchPropertyNames =
        {
            "FOV", "Yaw", "Pitch", "Roll",
            Lang.Get("vsdirector:keyframe-hold"),
            Lang.Get("vsdirector:segment-start-speed"),
            Lang.Get("vsdirector:segment-end-speed")
        };
        string[] batchInterpolationCodes = { "default", "linear", "ease", "bezier", "step" };
        string[] batchInterpolationNames =
        {
            Lang.Get("vsdirector:segment-interpolation-default"),
            Lang.Get("vsdirector:interpolation-linear"),
            Lang.Get("vsdirector:interpolation-easeinout"),
            Lang.Get("vsdirector:interpolation-bezier"),
            Lang.Get("vsdirector:interpolation-step")
        };
        ElementBounds batchScopeBounds;
        ElementBounds batchPropertyBounds;
        ElementBounds batchValueBounds;
        ElementBounds batchApplyBounds;
        ElementBounds batchInterpolationBounds;
        ElementBounds batchInterpolationApplyBounds;
        if (compactBatchLayout)
        {
            double half = Math.Max(82d, (panel.Width - 26d) / 2d);
            batchScopeBounds = ElementBounds.Fixed(10, batchY, half, 28);
            batchPropertyBounds = ElementBounds.Fixed(16 + half, batchY, half, 28);
            batchValueBounds = ElementBounds.Fixed(10, batchY + 34d, half, 28);
            batchApplyBounds = ElementBounds.Fixed(16 + half, batchY + 34d, half, 28);
            batchInterpolationBounds = ElementBounds.Fixed(10, batchY + 68d, half, 28);
            batchInterpolationApplyBounds = ElementBounds.Fixed(16 + half, batchY + 68d, half, 28);
        }
        else
        {
            double gap = 6d;
            double usable = Math.Max(600d, panel.Width - 20d - gap * 5d);
            double scopeWidth = usable * 0.18d;
            double propertyWidth = usable * 0.18d;
            double valueWidth = usable * 0.13d;
            double applyWidth = usable * 0.13d;
            double interpolationWidth = usable * 0.2d;
            double interpolationApplyWidth = usable - scopeWidth - propertyWidth - valueWidth
                - applyWidth - interpolationWidth;
            double x = 10d;
            batchScopeBounds = ElementBounds.Fixed(x, batchY, scopeWidth, 28);
            x += scopeWidth + gap;
            batchPropertyBounds = ElementBounds.Fixed(x, batchY, propertyWidth, 28);
            x += propertyWidth + gap;
            batchValueBounds = ElementBounds.Fixed(x, batchY, valueWidth, 28);
            x += valueWidth + gap;
            batchApplyBounds = ElementBounds.Fixed(x, batchY, applyWidth, 28);
            x += applyWidth + gap;
            batchInterpolationBounds = ElementBounds.Fixed(x, batchY, interpolationWidth, 28);
            x += interpolationWidth + gap;
            batchInterpolationApplyBounds = ElementBounds.Fixed(x, batchY, interpolationApplyWidth, 28);
        }
        GuiComposer composer = capi.Gui.CreateCompo(key, root)
            .AddDirectorOpaqueWindowSurface(background)
            .AddDirectorDialogHeader(
                Lang.Get("vsdirector:paths"),
                CloseFloatingEditorWindow,
                panel.Width,
                "floatingEditorHeader",
                UpdateFloatingEditorPosition)
            .BeginChildElements(content)
            .AddDirectorIconButton(DirectorIcon.Add, Lang.Get("vsdirector:capture-keyframe"),
                AddPathKeyframeAtPlayhead,
                ToolbarButton(0), EnumButtonStyle.Normal, "pathAddKey")
            .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete-selected-keyframes"),
                DeleteSelectedPathKeyframes,
                ToolbarButton(1), EnumButtonStyle.Small, "pathDeleteKeys")
            .AddDirectorIconButton(DirectorIcon.Duplicate, Lang.Get("vsdirector:path-copy-keyframes"),
                CopySelectedPathKeyframe,
                ToolbarButton(2), EnumButtonStyle.Small, "pathCopyKey")
            .AddDirectorIconButton(DirectorIcon.Paste, Lang.Get("vsdirector:path-paste-keyframes"),
                PastePathKeyframeAtPlayhead,
                ToolbarButton(3), EnumButtonStyle.Small, "pathPasteKey")
            .AddDirectorIconButton(DirectorIcon.Select, Lang.Get("vsdirector:path-tool-select"),
                () => SetPathWorkspaceTool(PathWorkspaceTool.Select),
                ToolbarButton(4),
                pathWorkspaceTool == PathWorkspaceTool.Select ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "pathSelectTool")
            .AddDirectorIconButton(DirectorIcon.Scissors, Lang.Get("vsdirector:path-tool-razor"),
                () => SetPathWorkspaceTool(PathWorkspaceTool.Razor),
                ToolbarButton(5),
                pathWorkspaceTool == PathWorkspaceTool.Razor ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "pathRazorTool")
            .AddDirectorIconButton(DirectorIcon.Marker, Lang.Get("vsdirector:path-segment-marker"),
                ToggleSelectedPathMarker,
                ToolbarButton(6), EnumButtonStyle.Small, "pathMarker")
            .AddDirectorIconButton(DirectorIcon.ZoomOut, Lang.Get("vsdirector:workspace-zoom-out"),
                () => ZoomPathEditorTimeline(1d / 1.25d),
                ToolbarButton(7), EnumButtonStyle.Small, "pathZoomOut")
            .AddDirectorIconButton(DirectorIcon.ZoomIn, Lang.Get("vsdirector:workspace-zoom-in"),
                () => ZoomPathEditorTimeline(1.25d),
                ToolbarButton(8), EnumButtonStyle.Small, "pathZoomIn")
            .AddDirectorIconButton(DirectorIcon.Fit, Lang.Get("vsdirector:workspace-zoom-fit"),
                FitPathEditorTimeline,
                ToolbarButton(9), EnumButtonStyle.Small, "pathZoomFit")
            .AddDirectorIconButton(DirectorIcon.Export, Lang.Get("vsdirector:path-json-export"),
                ExportPathJsonToClipboard,
                ToolbarButton(10), EnumButtonStyle.Small, "pathJsonExport")
            .AddDirectorIconButton(DirectorIcon.Import, Lang.Get("vsdirector:path-json-import"),
                ImportPathJsonFromClipboard,
                ToolbarButton(11), EnumButtonStyle.Small, "pathJsonImport")
            .AddDirectorIconButton(
                pathEdit?.PreviewVisible == true ? DirectorIcon.EyeOff : DirectorIcon.Eye,
                Lang.Get(pathEdit?.PreviewVisible == true
                    ? "vsdirector:camera-model-hide"
                    : "vsdirector:camera-model-show"),
                TogglePathWorkspacePreview,
                ToolbarButton(12), EnumButtonStyle.Small, "pathPreview")
            .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:play"),
                () => PlayEditedPath(false),
                ToolbarButton(13), EnumButtonStyle.Normal, "pathPlay")
            .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:save"), SavePathEditor,
                ToolbarButton(14), EnumButtonStyle.Normal, "pathSave")
            .AddDirectorIconButton(DirectorIcon.Undo, Lang.Get("vsdirector:undo"), UndoPathEdit,
                ToolbarButton(15), EnumButtonStyle.Small, "pathUndo")
            .AddDirectorIconButton(DirectorIcon.Redo, Lang.Get("vsdirector:redo"), RedoPathEdit,
                ToolbarButton(16), EnumButtonStyle.Small, "pathRedo")
            .AddDirectorIconButton(DirectorIcon.Close, Lang.Get("vsdirector:path-clear-selection"),
                ClearPathKeyframeSelection,
                ToolbarButton(17), EnumButtonStyle.Small, "pathClearSelection")
            .AddDropDown(channelCodes, channelNames,
                Math.Max(0, Array.IndexOf(channelCodes, pathCurveChannelCode)),
                OnPathCurveChannelChanged,
                ElementBounds.Fixed(10, controlsY, 94, 28), "pathCurveChannel")
            .AddSwitch(value => { layout.PathTimelineSnap = value; },
                ElementBounds.Fixed(114, controlsY + 1, 25, 25), "pathSnap", 25, 3)
            .AddStaticText(Lang.Get("vsdirector:path-snap"), small,
                ElementBounds.Fixed(148, controlsY + 4, Math.Max(40d, panel.Width - 158d), 22))
            .AddDropDown(batchScopeCodes, batchScopeNames,
                Math.Max(0, Array.IndexOf(batchScopeCodes, pathBatchScopeCode)),
                OnPathBatchScopeChanged, batchScopeBounds, "pathBatchScope")
            .AddDropDown(batchPropertyCodes, batchPropertyNames,
                Math.Max(0, Array.IndexOf(batchPropertyCodes, pathBatchPropertyCode)),
                OnPathBatchPropertyChanged, batchPropertyBounds, "pathBatchProperty")
            .AddNumberInput(batchValueBounds, value => pathBatchValueText = value,
                DirectorFont.TextInput(), "pathBatchValue")
            .AddDirectorButton(Lang.Get("vsdirector:path-batch-apply"), ApplyPathBatchValue,
                batchApplyBounds, EnumButtonStyle.Normal, "pathBatchApply")
            .AddDropDown(batchInterpolationCodes, batchInterpolationNames,
                Math.Max(0, Array.IndexOf(batchInterpolationCodes, pathBatchInterpolationCode)),
                OnPathBatchInterpolationChanged, batchInterpolationBounds, "pathBatchInterpolation")
            .AddDirectorButton(Lang.Get("vsdirector:path-batch-apply-interpolation"),
                ApplyPathBatchInterpolation, batchInterpolationApplyBounds,
                EnumButtonStyle.Small, "pathBatchInterpolationApply")
            .AddStaticText(Lang.Get("vsdirector:path-global-timeline"), detail,
                ElementBounds.Fixed(14, globalLabelY, 120, 16))
            .AddCustomRender(pathGlobalTimelineBounds, RenderPathGlobalTimeline)
            .AddStaticText(Lang.Get("vsdirector:path-local-timeline"), detail,
                ElementBounds.Fixed(14, localLabelY, 120, 16))
            .AddCustomRender(pathEditorTimelineBounds, RenderPathEditorTimeline)
            .AddDynamicText(PathWorkspaceStatusText(), detail,
                ElementBounds.Fixed(10, contentHeight - 38, panel.Width - 20, 28), "pathWorkspaceStatus")
            .EndChildElements();

        composer.zDepth = 35f;
        GuiComposer composed = composer.Compose(focusFirstElement: false);
        floatingEditorDialog.SetComposer(composed);
        composed.GetSwitch("pathSnap")?.SetValue(layout.PathTimelineSnap);
        SetNumber(composed, "pathBatchValue", ParseDouble(pathBatchValueText, 120d));
        SetButtonEnabledIfPresent(composer, "pathDeleteKeys", selectedPathKeyframes.Count > 0);
        SetButtonEnabledIfPresent(composer, "pathCopyKey", selectedPathKeyframes.Count > 0);
        SetButtonEnabledIfPresent(composer, "pathClearSelection", selectedPathKeyframes.Count > 0);
        SetButtonEnabledIfPresent(composer, "pathPasteKey", pathKeyframeClipboard.Count > 0);
        SetButtonEnabledIfPresent(composer, "pathUndo", pathEditHistory.CanUndo);
        SetButtonEnabledIfPresent(composer, "pathRedo", pathEditHistory.CanRedo);
        SetButtonEnabledIfPresent(composer, "pathBatchApply", PathBatchTargetIndices().Count > 0);
        SetButtonEnabledIfPresent(composer, "pathBatchInterpolationApply", PathBatchTargetIndices().Count > 0);
    }

    private bool CanManageChunkPreload
        => owner.HasPermission(DirectorPermission.RemoteChunkAccess);

    private bool HasPreloadEditorTarget()
        => section == DirectorWorkspaceSection.Paths && pathEdit is { Keyframes.Count: > 0 }
            || section == DirectorWorkspaceSection.Replay && PreloadEditorReplayInfo() is not null;

    private void ComposePreloadCanvas(string key)
    {
        RemoveFloatingEditorComposer();
        if (preloadRmlWindow is null)
        {
            var replay = section == DirectorWorkspaceSection.Replay ? PreloadEditorReplayInfo() : null;
            preloadRmlWindow = new DirectorPreloadRmlWindow(capi, () =>
            {
                if (!TryGetPreloadGridState(out var state)) return null;
                return new DirectorPreloadMapState(state.CenterChunkX, state.CenterChunkZ, state.TargetChunkX, state.TargetChunkZ,
                    state.Dimension, state.Automatic, state.Included, state.Excluded, state.Final);
            }, (minX, minZ, maxX, maxZ, erase) =>
            {
                preloadSelectionStartX = minX; preloadSelectionStartZ = minZ;
                preloadSelectionEndX = maxX; preloadSelectionEndZ = maxZ;
                preloadBrushRadius = 0; preloadEraseMode = erase; preloadGridChanged = false;
                ApplyPreloadGridSelection(); if (preloadGridChanged) SavePreloadGridChanges();
            }, (x, z) => { preloadPanChunkX = x; preloadPanChunkZ = z; },
                time => preloadReplayTime = time, replay is not null, replay?.DurationSeconds ?? 0, preloadReplayTime);
            preloadRmlWindow.Closed += () => { preloadEditorExpanded = false; };
        }
        preloadRmlWindow.SetEmptyMessage(PreloadGridSummary());
        if (IsOpened() && !preloadRmlWindow.IsOpened) preloadRmlWindow.Open();
        preloadRmlWindow.Refresh();
    }

    private void ComposeCameraResources(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        IReadOnlyList<CameraPreset> cameras = owner.Cameras
            .Where(camera => SearchMatch(camera.Name, camera.Description, camera.Tags))
            .ToArray();
        AddResourceCount(composer, detail, cameras.Count, ref y);
        int rows = VisibleResourceRows(y);
        resourceScrollOffset = Math.Clamp(resourceScrollOffset, 0, MaxScrollOffset(cameras.Count, rows));
        int first = resourceScrollOffset;
        for (int index = first; index < Math.Min(first + rows, cameras.Count); index++)
        {
            CameraPreset camera = cameras[index];
            Guid id = camera.Id;
            (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
                camera.X,
                camera.Y,
                camera.Z);
            AddResourceRow(composer, small, detail, y, camera.Name,
                $"{displayX:0.##}, {displayY:0.##}, {displayZ:0.##}  FOV {camera.Fov:0.#}",
                () => SelectCamera(id), () => ActivateCameraFromWorkspace(id), ref y);
        }
    }

    private void ComposePathResources(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        IReadOnlyList<CameraPath> paths = owner.Paths
            .Where(path => SearchMatch(path.Name, path.Description, path.Tags))
            .ToArray();
        AddResourceCount(composer, detail, paths.Count, ref y);
        int rows = VisibleResourceRows(y);
        resourceScrollOffset = Math.Clamp(resourceScrollOffset, 0, MaxScrollOffset(paths.Count, rows));
        int first = resourceScrollOffset;
        for (int index = first; index < Math.Min(first + rows, paths.Count); index++)
        {
            CameraPath path = paths[index];
            Guid id = path.Id;
            AddResourceRow(composer, small, detail, y, path.Name,
                PathResourceDetails(path),
                () => SelectPath(id), () => PlayPathFromWorkspace(id, reverse: false), ref y);
        }
    }

    private void ComposeActionResources(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        if (actionLibraryMode == "models"
            && owner.ActionModels.FirstOrDefault(model => string.Equals(
                model.Code,
                actionLibrarySelectedModelCode,
                StringComparison.OrdinalIgnoreCase)) is DirectorExternalModelInfo selectedModel)
        {
            double panelWidth = layout.Panel("resources").Width;
            composer
                .AddDirectorIconButton(DirectorIcon.Previous, Lang.Get("vsdirector:previous"),
                    CloseModelResourceSelection,
                    ElementBounds.Fixed(10, y, 30, 28), EnumButtonStyle.Small, "modelLibraryBack")
                .AddDirectorSingleLineText(
                    selectedModel.Name,
                    small,
                    ElementBounds.Fixed(48, y + 5, Math.Max(80d, panelWidth - 58d), 22));
            y += 34;
            IReadOnlyList<string> animations = selectedModel.Animations
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Where(code => ActionResourceSearchMatch(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AddResourceCount(composer, detail, animations.Count, ref y);
            int animationRows = VisibleActionResourceRows(y);
            resourceScrollOffset = Math.Clamp(
                resourceScrollOffset,
                0,
                MaxScrollOffset(animations.Count, animationRows));
            int animationFirst = resourceScrollOffset;
            for (int index = animationFirst;
                 index < Math.Min(animationFirst + animationRows, animations.Count);
                 index++)
            {
                string animation = animations[index];
                AddActionPreviewResourceRow(
                    composer,
                    detail,
                    animation,
                    () => PreviewSelectedModelAnimation(animation),
                    ref y);
            }
            return;
        }

        IReadOnlyList<ActionLibraryCardEntry> entries = BuildActionLibraryEntries();
        AddResourceCount(composer, detail, entries.Count, ref y);
        int rows = VisibleResourceRows(y);
        resourceScrollOffset = Math.Clamp(resourceScrollOffset, 0, MaxScrollOffset(entries.Count, rows));
        int first = resourceScrollOffset;
        for (int index = first; index < Math.Min(first + rows, entries.Count); index++)
        {
            ActionLibraryCardEntry entry = entries[index];
            string title = (entry.Selected ? "[*] " : string.Empty) + entry.Name;
            string rowDetails = entry.Badge + " | " + entry.Detail;
            if (entry.IsModel)
            {
                AddResourceRow(
                    composer,
                    small,
                    detail,
                    y,
                    title,
                    rowDetails,
                    () => { _ = SelectModelLibraryCard(entry); },
                    () =>
                    {
                        if (!string.Equals(actionLibrarySelectedModelCode, entry.ModelCode,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            _ = SelectModelLibraryCard(entry);
                        }
                        else
                        {
                            _ = PreviewModelEditor();
                        }
                    },
                    ref y,
                    Lang.Get("vsdirector:edit"));
                continue;
            }

            Guid id = entry.ActionId;
            AddResourceRow(
                composer,
                small,
                detail,
                y,
                title,
                rowDetails,
                () => SelectAction(id),
                () => { _ = PreviewAction(id); },
                ref y,
                Lang.Get("vsdirector:action-preview"));
        }
    }

    private void AddActionPreviewResourceRow(
        GuiComposer composer,
        CairoFont font,
        string title,
        Action preview,
        ref int y)
    {
        int row = y;
        double panelWidth = layout.Panel("resources").Width;
        composer
            .AddDirectorSingleLineText(
                Ellipsize(title, Math.Max(16, (int)((panelWidth - 62d) / 8d))),
                font,
                ElementBounds.Fixed(12, row + 5, Math.Max(80d, panelWidth - 64d), 22))
            .AddDirectorIconButton(
                DirectorIcon.Play,
                Lang.Get("vsdirector:play"),
                () => { preview(); return true; },
                ElementBounds.Fixed(panelWidth - 42d, row, 30, 28),
                EnumButtonStyle.Normal,
                "actionPreview-" + row);
        y = row + 36;
    }

    private void ComposeSequenceResources(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        IReadOnlyList<CameraSequence> sequences = owner.Sequences
            .Where(sequence => SearchMatch(sequence.Name))
            .ToArray();
        AddResourceCount(composer, detail, sequences.Count, ref y);
        int rows = VisibleResourceRows(y);
        resourceScrollOffset = Math.Clamp(
            resourceScrollOffset,
            0,
            MaxScrollOffset(sequences.Count, rows));
        int first = resourceScrollOffset;
        for (int index = first; index < Math.Min(first + rows, sequences.Count); index++)
        {
            CameraSequence sequence = sequences[index];
            Guid id = sequence.Id;
            AddResourceRow(composer, small, detail, y, sequence.Name,
                Lang.Get("vsdirector:sequence-summary",
                    sequence.Shots.Count,
                    sequence.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)),
                () => SelectSequence(id), () => PlaySequenceFromWorkspace(id), ref y);
        }
    }

    private void ComposeWorkflowResources(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        IReadOnlyList<DirectorWorkflow> workflows = owner.Workflows
            .Where(workflow => SearchMatch(workflow.Name))
            .ToArray();
        AddResourceCount(composer, detail, workflows.Count, ref y);
        int rows = VisibleResourceRows(y);
        resourceScrollOffset = Math.Clamp(
            resourceScrollOffset,
            0,
            MaxScrollOffset(workflows.Count, rows));
        int first = resourceScrollOffset;
        for (int index = first; index < Math.Min(first + rows, workflows.Count); index++)
        {
            DirectorWorkflow workflow = workflows[index];
            Guid id = workflow.Id;
            string invalidReason = WorkflowInvalidReason(workflow);
            bool invalid = invalidReason.Length > 0;
            IReadOnlyList<DirectorWorkflowExportPass> passes = owner.PlanWorkflow(id);
            AddResourceRow(composer, small, detail, y, workflow.Name,
                Lang.Get("vsdirector:workflow-detail", workflow.Cards.Count,
                    workflow.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    passes.Count),
                () => SelectWorkflow(id),
                invalid
                    ? () => PromptDeleteInvalidWorkflow(id, invalidReason)
                    : () => SelectWorkflow(id),
                ref y,
                invalid ? Lang.Get("vsdirector:invalid") : null,
                invalid);
        }
    }

    private string WorkflowInvalidReason(DirectorWorkflow workflow)
    {
        foreach (DirectorWorkflowCard card in (workflow.Cards ?? new List<DirectorWorkflowCard>())
            .Where(card => card is not null && card.Enabled))
        {
            bool validTime = double.IsFinite(card.StartSeconds)
                && double.IsFinite(card.EndSeconds)
                && card.StartSeconds >= 0d
                && card.EndSeconds - card.StartSeconds >= DirectorWorkflowPlanner.MinCardDurationSeconds;
            if (!validTime || !DirectorWorkflowPlanner.IsCardKindAllowed(workflow.Type, card.Kind))
            {
                return Lang.Get("vsdirector:workflow-invalid-card", card.Name);
            }
            if (card.Kind != DirectorWorkflowCardKind.Shot)
            {
                continue;
            }
            if (card.SourceType == DirectorRecordingSourceType.Sequence)
            {
                return Lang.Get("vsdirector:workflow-invalid-sequence", card.Name);
            }
            if (!DirectorWorkflowPlanner.IsExportSource(card.SourceType)
                || !owner.WorkflowSourceExists(card.SourceType, card.SourceId))
            {
                return Lang.Get("vsdirector:workflow-invalid-source", card.Name);
            }
        }

        return string.Empty;
    }

    private void PromptDeleteInvalidWorkflow(Guid workflowId, string reason)
    {
        DirectorWorkflow? workflow = owner.FindWorkflow(workflowId);
        if (workflow is null)
        {
            Refresh();
            return;
        }

        invalidWorkflowDialog?.TryClose();
        invalidWorkflowDialog?.Dispose();
        invalidWorkflowDialog = new DirectorRmlConfirmWindow(
            capi,
            Lang.Get("vsdirector:workflow-invalid-dialog-title"),
            Lang.Get("vsdirector:workflow-invalid-dialog-message", workflow.Name, reason),
            () =>
            {
                if (!owner.TryDeleteWorkflow(workflowId, out _))
                {
                    return;
                }
                if (workflowEdit?.Id == workflowId)
                {
                    selectedId = null;
                    ClearEditors();
                }
                Refresh();
            });
        invalidWorkflowDialog.TryOpen();
    }

    private void ComposeFollowResources(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        IReadOnlyList<CameraFollowRig> rigs = owner.FollowRigs
            .Where(rig => SearchMatch(rig.Name, rig.Description, rig.Tags))
            .ToArray();
        AddResourceCount(composer, detail, rigs.Count, ref y);
        int rows = VisibleResourceRows(y);
        resourceScrollOffset = Math.Clamp(resourceScrollOffset, 0, MaxScrollOffset(rigs.Count, rows));
        int first = resourceScrollOffset;
        for (int index = first; index < Math.Min(first + rows, rigs.Count); index++)
        {
            CameraFollowRig rig = rigs[index];
            Guid id = rig.Id;
            AddResourceRow(composer, small, detail, y, rig.Name,
                FollowResourceDetails(rig),
                () => SelectFollow(id), () => ActivateFollowFromWorkspace(id), ref y);
        }
    }

    private void ComposeReplayResources(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        double panelWidth = layout.Panel("resources").Width;
        double nameWidth = Math.Clamp(panelWidth - 138d, 180d, 420d);
        double commandX = 16d + nameWidth;
        composer
            .AddTextInput(ElementBounds.Fixed(10, y, nameWidth, 28),
                null, DirectorFont.TextInput(), "replayName")
            .AddDirectorIconButton(DirectorIcon.Record, Lang.Get("vsdirector:replay-record"),
                StartReplayRecording, ElementBounds.Fixed(commandX, y, 30, 28),
                EnumButtonStyle.Normal, "record")
            .AddDirectorIconButton(DirectorIcon.Stop, Lang.Get("vsdirector:replay-stop-record"),
                StopReplayRecording, ElementBounds.Fixed(commandX + 36d, y, 30, 28),
                EnumButtonStyle.Small, "stopRecord")
            .AddDirectorIconButton(DirectorIcon.Refresh, Lang.Get("vsdirector:refresh"),
                RefreshReplayIndex, ElementBounds.Fixed(commandX + 72d, y, 30, 28),
                EnumButtonStyle.Small, "refresh");
        composer.GetButton("record").Enabled = !owner.IsReplayRecording && !owner.HasPendingReplaySave;
        composer.GetButton("stopRecord").Enabled = owner.IsReplayRecording || owner.HasFailedReplaySave;
        y += 36;
        if (owner.IsReplayIndexing)
        {
            composer.AddDynamicText(
                Lang.Get("vsdirector:workspace-replay-indexing", owner.ReplayIndexProgress * 100d),
                detail,
                ElementBounds.Fixed(10, y, 470, 24),
                "indexing");
            y += 30;
        }

        IReadOnlyList<DirectorReplayInfo> replays = owner.Replays
            .Where(replay => SearchMatch(replay.Name, replay.Description, replay.Tags))
            .ToArray();
        AddResourceCount(composer, detail, replays.Count, ref y);
        int rows = VisibleResourceRows(y);
        resourceScrollOffset = Math.Clamp(resourceScrollOffset, 0, MaxScrollOffset(replays.Count, rows));
        int first = resourceScrollOffset;
        for (int index = first; index < Math.Min(first + rows, replays.Count); index++)
        {
            DirectorReplayInfo replay = replays[index];
            Guid id = replay.Id;
            AddResourceRow(composer, small, detail, y, replay.Name,
                Lang.Get("vsdirector:replay-row-detail",
                    replay.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                    replay.ActorCount,
                    replay.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)),
                () => SelectReplay(id), () => PlayReplayFromWorkspace(id), ref y,
                Lang.Get("vsdirector:play"));
        }
    }

    private void ComposeMergedReplayResources(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        DirectorWorkspacePanelLayout panel = layout.Panel("resources");
        ReplaySplitLayout split = CalculateReplaySplit(
            y,
            panel.Height - 8d,
            layout.ReplayResourceSplitRatio);
        replayResourceTopBounds = ElementBounds.Fixed(4d, split.TopY, panel.Width - 8d, split.TopHeight);
        replayResourceViewBounds = ElementBounds.Fixed(4d, split.BottomY, panel.Width - 8d, split.BottomHeight);
        ElementBounds divider = ElementBounds.Fixed(
            10d,
            split.DividerY,
            panel.Width - 20d,
            ReplaySplitDividerHeight);
        replayResourceSplitBounds = divider;
        composer
            .AddCustomRender(replayResourceTopBounds, RenderReplayBlockBackground)
            .AddCustomRender(replayResourceViewBounds, RenderReplayBlockBackground)
            .AddCustomRender(divider, RenderReplaySplitDivider);
        AddReplayBlockHeader(
            composer,
            small,
            Lang.Get("vsdirector:replay-block-replays"),
            panel.Width,
            split.TopY);
        int replayContentY = (int)Math.Ceiling(split.TopContentY + 4d);
        ComposeReplayResourceBlock(
            composer,
            small,
            detail,
            ref replayContentY,
            split.TopEnd - 4d);

        AddReplayBlockHeader(
            composer,
            small,
            ReplayViewResourceBlockTitle(),
            panel.Width,
            split.BottomY);
        int viewContentY = (int)Math.Ceiling(split.BottomContentY + 4d);
        ComposeReplayViewResourceBlock(
            composer,
            small,
            detail,
            ref viewContentY,
            split.BottomEnd - 4d);
        y = (int)Math.Ceiling(split.BottomEnd);
    }

    private void ComposeReplayResourceBlock(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y,
        double bottom)
    {
        double panelWidth = layout.Panel("resources").Width;
        double searchWidth = Math.Clamp(panelWidth - 58d, 180d, 620d);
        composer
            .AddTextInput(ElementBounds.Fixed(10, y, searchWidth, 28), null,
                DirectorFont.TextInput(), "search")
            .AddDirectorIconButton(DirectorIcon.Search, Lang.Get("vsdirector:search"),
                ApplyResourceSearch,
                ElementBounds.Fixed(16d + searchWidth, y, 30, 28), EnumButtonStyle.Small,
                "resourceSearchApply");
        y += 34;

        double nameWidth = Math.Clamp(panelWidth - 138d, 180d, 620d);
        double commandX = 16d + nameWidth;
        composer
            .AddTextInput(ElementBounds.Fixed(10, y, nameWidth, 28),
                null, DirectorFont.TextInput(), "replayName")
            .AddDirectorIconButton(DirectorIcon.Record, Lang.Get("vsdirector:replay-record"),
                StartReplayRecording, ElementBounds.Fixed(commandX, y, 30, 28),
                EnumButtonStyle.Normal, "record")
            .AddDirectorIconButton(DirectorIcon.Stop, Lang.Get("vsdirector:replay-stop-record"),
                StopReplayRecording, ElementBounds.Fixed(commandX + 36d, y, 30, 28),
                EnumButtonStyle.Small, "stopRecord")
            .AddDirectorIconButton(DirectorIcon.Refresh, Lang.Get("vsdirector:refresh"),
                RefreshReplayIndex, ElementBounds.Fixed(commandX + 72d, y, 30, 28),
                EnumButtonStyle.Small, "refresh");
        composer.GetButton("record").Enabled = !owner.IsReplayRecording && !owner.HasPendingReplaySave;
        composer.GetButton("stopRecord").Enabled = owner.IsReplayRecording || owner.HasFailedReplaySave;
        y += 34;
        if (owner.IsReplayIndexing && y + 26d <= bottom)
        {
            composer.AddDynamicText(
                Lang.Get("vsdirector:workspace-replay-indexing", owner.ReplayIndexProgress * 100d),
                detail,
                ElementBounds.Fixed(10, y, panelWidth - 20d, 22),
                "indexing");
            y += 26;
        }

        IReadOnlyList<DirectorReplayInfo> replays = owner.Replays
            .Where(replay => SearchMatch(replay.Name, replay.Description, replay.Tags))
            .ToArray();
        AddResourceCount(composer, detail, replays.Count, ref y);
        int rows = Math.Max(0, (int)Math.Floor((bottom - y) / ResourceRowHeight));
        replayResourceRows = Math.Max(1, Math.Min(ResourceRows, rows));
        resourceScrollOffset = Math.Clamp(
            resourceScrollOffset,
            0,
            MaxScrollOffset(replays.Count, replayResourceRows));
        int first = resourceScrollOffset;
        for (int index = first;
             rows > 0 && index < Math.Min(first + replayResourceRows, replays.Count);
             index++)
        {
            DirectorReplayInfo replay = replays[index];
            Guid id = replay.Id;
            AddResourceRow(composer, small, detail, y, replay.Name,
                Lang.Get("vsdirector:replay-row-detail",
                    replay.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                    replay.ActorCount,
                    replay.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)),
                () => SelectReplay(id), () => PlayReplayFromWorkspace(id), ref y,
                Lang.Get("vsdirector:play"));
        }
    }

    private void ComposeReplayViewResourceBlock(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y,
        double bottom)
    {
        if (replayViewMode == "path")
        {
            ComposeReplayPathResourceBlock(composer, small, detail, ref y, bottom);
            return;
        }

        IReadOnlyList<ReplayViewOption> options = ReplayViewOptions();
        AddResourceCount(composer, detail, options.Count, ref y);
        const int rowHeight = 50;
        int rows = Math.Max(0, (int)Math.Floor((bottom - y) / rowHeight));
        replayViewResourceRows = Math.Max(1, rows);
        replayViewResourceScrollOffset = Math.Clamp(
            replayViewResourceScrollOffset,
            0,
            MaxScrollOffset(options.Count, replayViewResourceRows));
        int first = replayViewResourceScrollOffset;
        for (int index = first;
             rows > 0 && index < Math.Min(first + replayViewResourceRows, options.Count);
             index++)
        {
            ReplayViewOption option = options[index];
            AddReplayViewResourceRow(
                composer,
                small,
                detail,
                y,
                option,
                ReplayViewResourceDetails(option.Id),
                "replayViewResource-" + y);
            y += rowHeight;
        }
    }

    private void ComposeReplayPathResourceBlock(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y,
        double bottom)
    {
        IReadOnlyList<CameraPath> paths = owner.Paths;
        string[] values = paths.Select(path => path.Id.ToString("D")).ToArray();
        string[] names = paths.Select(path => path.Name).ToArray();
        if (values.Length == 0)
        {
            values = new[] { Guid.Empty.ToString("D") };
            names = new[] { string.Empty };
        }
        double panelWidth = layout.Panel("resources").Width;
        composer
            .AddDropDown(values, names,
                Math.Max(0, Array.IndexOf(values, replayViewSourceId.ToString("D"))),
                OnReplayViewSourceChanged,
                ElementBounds.Fixed(10, y, panelWidth - 58d, 28),
                "replayEmbeddedPathSource")
            .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:replay-view-activate"),
                ActivateReplayView,
                ElementBounds.Fixed(panelWidth - 42d, y, 30, 28),
                EnumButtonStyle.Normal,
                "activateEmbeddedPath");
        y += 34;
        CameraPath? path = pathEdit;
        if (path is null)
        {
            return;
        }

        composer.AddDirectorSingleLineText(
            Lang.Get("vsdirector:path-keyframe-list-count", path.Keyframes.Count),
            detail,
            ElementBounds.Fixed(10, y, panelWidth - 20d, 22));
        y += 26;
        const int rowHeight = 38;
        int rows = Math.Max(0, (int)Math.Floor((bottom - y) / rowHeight));
        replayViewResourceRows = Math.Max(1, rows);
        replayViewResourceScrollOffset = Math.Clamp(
            replayViewResourceScrollOffset,
            0,
            MaxScrollOffset(path.Keyframes.Count, replayViewResourceRows));
        int first = replayViewResourceScrollOffset;
        for (int index = first;
             rows > 0 && index < Math.Min(first + replayViewResourceRows, path.Keyframes.Count);
             index++)
        {
            int captured = index;
            CameraKeyframe frame = path.Keyframes[index];
            bool selected = selectedPathKeyframes.Contains(index);
            string name = string.IsNullOrWhiteSpace(frame.Name)
                ? Lang.Get("vsdirector:path-keyframe-default-name", index + 1)
                : frame.Name;
            composer
                .AddSmallButton(
                    Ellipsize($"{index + 1:00}  {frame.TimeSeconds:0.###}s  {name}",
                        Math.Max(24, (int)((panelWidth - 68d) / 8d))),
                    () => SelectReplayPathKeyframeFromList(captured),
                    ElementBounds.Fixed(10, y, panelWidth - 58d, 28),
                    selected ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                    "replayPathKey-" + index)
                .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:replay-view-jump-keyframe"),
                    () => JumpReplayToPathKeyframe(captured),
                    ElementBounds.Fixed(panelWidth - 42d, y, 30, 28),
                    EnumButtonStyle.Small,
                    "replayPathJump-" + index);
            y += rowHeight;
        }
    }

    private void AddReplayViewResourceRow(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        int y,
        ReplayViewOption option,
        string details,
        string keyPrefix)
    {
        double panelWidth = layout.Panel("resources").Width;
        double textWidth = Math.Max(180d, panelWidth - 58d);
        bool selected = option.Id == replayViewSourceId;
        composer
            .AddSmallButton(Ellipsize(option.Name, Math.Max(24, (int)(textWidth / 8d))),
                () => SelectReplayViewSource(option.Id, activate: false),
                ElementBounds.Fixed(10, y, textWidth, 28),
                selected ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                keyPrefix + "-select")
            .AddDirectorSingleLineText(details, detail,
                ElementBounds.Fixed(14, y + 29, textWidth - 4d, 18))
            .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:replay-view-activate"),
                () => SelectReplayViewSource(option.Id, activate: true),
                ElementBounds.Fixed(panelWidth - 42d, y, 30, 28),
                EnumButtonStyle.Normal,
                keyPrefix + "-activate");
        _ = small;
    }

    private void ComposeOutputResources(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        if (!owner.IsRecordingActive)
        {
            composer.AddStaticText(
                Lang.Get("vsdirector:record-monitor-title"),
                small,
                ElementBounds.Fixed(10, y, 470, 24));
            y += 28;
            composer.AddDynamicText(
                owner.RecordingMonitorStatusText(),
                detail,
                ElementBounds.Fixed(14, y, 470, 150),
                "recordMonitorSummary");
            y += 156;
            return;
        }

        IReadOnlyList<RecordingSourceOption> sources = BuildRecordingSources();
        if (sources.Count > 0 && !sources.Any(source => source.Value == selectedRecordingSource))
        {
            selectedRecordingSource = sources[0].Value;
        }
        composer
            .AddTextInput(ElementBounds.Fixed(10, y, 180, 28), null, DirectorFont.TextInput(), "recordTrackName")
            .AddDropDown(sources.Select(source => source.Value).ToArray(),
                sources.Select(source => Ellipsize(source.Label, 28)).ToArray(),
                Math.Max(0, sources.ToList().FindIndex(source => source.Value == selectedRecordingSource)),
                OnRecordingSourceChanged,
                ElementBounds.Fixed(196, y, 210, 28), "recordSource")
            .AddSmallButton(Lang.Get("vsdirector:create"), CreateRecordingTrack,
                ElementBounds.Fixed(412, y, 78, 28), EnumButtonStyle.Normal, "createTrack");
        y += 36;
        IReadOnlyList<DirectorRecordingTrack> recordingTracks = owner.RecordingTracks;
        composer.AddStaticText(
            Lang.Get("vsdirector:workspace-live-tracks", recordingTracks.Count),
            small,
            ElementBounds.Fixed(10, y, 470, 24));
        y += 28;
        int rows = VisibleResourceRows(y);
        outputResourceScrollOffset = Math.Clamp(
            outputResourceScrollOffset,
            0,
            MaxScrollOffset(recordingTracks.Count, rows));
        int first = outputResourceScrollOffset;
        for (int index = first; index < Math.Min(first + rows, recordingTracks.Count); index++)
        {
            DirectorRecordingTrack track = recordingTracks[index];
            Guid id = track.Id;
            bool assigned = owner.AssignedRecordingTrackId == id;
            AddResourceRow(
                composer,
                small,
                detail,
                y,
                assigned
                    ? track.Name + "  [" + Lang.Get("vsdirector:record-worker-marker") + "]"
                    : track.Name,
                RecordingTrackDetails(track),
                () => selectedRecordingTrackId = id,
                () => owner.TryAssignRecordingWorker(id, out _),
                ref y,
                Lang.Get("vsdirector:sync-recording-assign"));
        }
    }



    private void ComposePresetResources(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        IReadOnlyList<string> presets = owner.PresetFiles
            .Where(file => SearchMatch(file))
            .ToArray();
        AddResourceCount(composer, detail, presets.Count, ref y);
        int rows = VisibleResourceRows(y);
        resourceScrollOffset = Math.Clamp(resourceScrollOffset, 0, MaxScrollOffset(presets.Count, rows));
        int first = resourceScrollOffset;
        for (int index = first; index < Math.Min(first + rows, presets.Count); index++)
        {
            string file = presets[index];
            AddResourceRow(composer, small, detail, y, file,
                Lang.Get("vsdirector:preset-package"),
                () => SelectPreset(file), () => ImportPreset(file), ref y);
        }
    }

    private void ComposeCameraInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        CameraPreset? camera = cameraEdit;
        if (camera is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-camera"), ref y);
            return;
        }

        string viewLabel = Lang.Get("vsdirector:replay-right-view");
        string propertiesLabel = Lang.Get("vsdirector:replay-right-inspector");
        double viewWidth = ToolbarButtonWidth(viewLabel);
        double propertiesWidth = ToolbarButtonWidth(propertiesLabel);
        composer
            .AddDirectorToolbarButton(viewLabel,
                () => SetCameraRightTab("visuals"),
                ElementBounds.Fixed(0d, y, viewWidth, 28d),
                cameraRightTab == "visuals" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "cameraRightVisuals")
            .AddDirectorToolbarButton(propertiesLabel,
                () => SetCameraRightTab("properties"),
                ElementBounds.Fixed(viewWidth, y, propertiesWidth, 28d),
                cameraRightTab == "properties" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "cameraRightProperties");
        y += 30;

        if (cameraRightTab == "properties")
        {
            AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "cameraName", camera.Name, ref y,
                AutoSaveTarget.Camera);
            AddLabeledInput(composer, small, Lang.Get("vsdirector:tags"), "cameraTags",
                string.Join(", ", camera.Tags), ref y, AutoSaveTarget.Camera);
            AddLabeledInput(composer, small, Lang.Get("vsdirector:description"),
                "cameraDescription", camera.Description, ref y, AutoSaveTarget.Camera);
            return;
        }

        AddAutoSavePathInspectorNumberRow(composer, small, ref y, AutoSaveTarget.Camera,
            ("X", "cameraPosX"), ("Y", "cameraPosY"), ("Z", "cameraPosZ"));
        AddAutoSavePathInspectorNumberRow(composer, small, ref y, AutoSaveTarget.Camera,
            ("Yaw", "cameraYaw"), ("Pitch", "cameraPitch"), ("Roll", "cameraRoll"));
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:camera-dimension"),
            "cameraDimension", camera.Dimension, ref y,
            autoSaveTarget: AutoSaveTarget.Camera);
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:fov"), "cameraFov", camera.Fov, ref y,
            autoSaveTarget: AutoSaveTarget.Camera);
        string[] slotValues = new[] { "none" }
            .Concat(Enumerable.Range(QuickSlots.First, QuickSlots.Count)
                .Select(slot => slot.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        string[] slotNames = new[] { Lang.Get("vsdirector:none") }
            .Concat(Enumerable.Range(QuickSlots.First, QuickSlots.Count).Select(owner.QuickSlotHotkeyLabel))
            .ToArray();
        composer
            .AddStaticText(Lang.Get("vsdirector:quick-slot"), small,
                ElementBounds.Fixed(10, y + 6, 100, 22))
            .AddDropDown(slotValues, slotNames, camera.QuickSlot ?? 0, OnCameraSlotChanged,
                ElementBounds.Fixed(114, y, 160, 28), "cameraSlot");
        y += 34;
        composer
            .AddDirectorIconButton(DirectorIcon.Capture, Lang.Get("vsdirector:recapture"), RecaptureCameraEditor,
                ElementBounds.Fixed(10, y, 30, 28), EnumButtonStyle.Small, "recaptureCamera")
            .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:activate"), ActivateCameraEditor,
                ElementBounds.Fixed(46, y, 30, 28), EnumButtonStyle.Small, "activateCamera")
            .AddDirectorIconButton(DirectorIcon.Delete, DeleteLabel(camera.Id), DeleteCameraEditor,
                ElementBounds.Fixed(82, y, 30, 28), EnumButtonStyle.Small, "deleteCamera")
            .AddDirectorIconButton(
                camera.PreviewVisible ? DirectorIcon.EyeOff : DirectorIcon.Eye,
                Lang.Get(camera.PreviewVisible
                    ? "vsdirector:camera-model-hide"
                    : "vsdirector:camera-model-show"),
                ToggleCameraModel,
                ElementBounds.Fixed(118, y, 30, 28),
                camera.PreviewVisible ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "cameraModel");
        y += 36;
    }

    private bool SetCameraRightTab(string tab)
    {
        string nextTab = tab == "properties" ? "properties" : "visuals";
        if (cameraRightTab == nextTab)
        {
            return true;
        }

        FlushPendingAutoSave(AutoSaveTarget.Camera);
        cameraRightTab = nextTab;
        ComposeInspector();
        return true;
    }

    private void ComposeActionInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        DirectorActionAsset? action = actionEdit;
        if (action is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-action"), ref y);
            ComposeActionRecordRow(composer, small, ref y);
            return;
        }

        double panelWidth = layout.Panel("inspector").Width;
        AddInspectorHeader(composer, small, action.Name, ref y);
        ComposeActionInspectorSections(composer, ref y);
        switch (actionInspectorSection)
        {
            case "preview":
                ComposeActionPreviewInspector(composer, small, detail, action, panelWidth, ref y);
                break;
            case "record":
                ComposeActionRecordRow(composer, small, ref y);
                break;
            case "layers":
                ComposeActionLayerInspector(composer, small, action, panelWidth, ref y);
                break;
            case "events":
                ComposeActionEventInspector(composer, small, panelWidth, ref y);
                break;
            default:
                ComposeActionBasicInspector(composer, small, action, panelWidth, ref y);
                break;
        }
    }

    private void ComposeActionInspectorSections(GuiComposer composer, ref int y)
    {
        string[] sections = { "basic", "preview", "record", "layers", "events" };
        if (Array.IndexOf(sections, actionInspectorSection) < 0)
        {
            actionInspectorSection = sections[0];
        }
        double panelWidth = layout.Panel("inspector").Width;
        const double gap = 4d;
        double tabWidth = Math.Clamp(
            (panelWidth - 20d - gap * (sections.Length - 1)) / sections.Length,
            62d,
            124d);
        for (int index = 0; index < sections.Length; index++)
        {
            string sectionCode = sections[index];
            composer.AddSmallButton(
                Lang.Get("vsdirector:action-inspector-" + sectionCode),
                () => SetActionInspectorSection(sectionCode),
                ElementBounds.Fixed(10d + index * (tabWidth + gap), y, tabWidth, 28),
                actionInspectorSection == sectionCode ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "actionInspector-" + sectionCode);
        }
        y += 38;
    }

    private void ComposeInspectorSections(GuiComposer composer, ref int y, params string[] sectionCodes)
    {
        if (sectionCodes.Length == 0)
        {
            return;
        }
        if (Array.IndexOf(sectionCodes, inspectorSection) < 0)
        {
            inspectorSection = sectionCodes[0];
        }

        double panelWidth = layout.Panel("inspector").Width;
        const double gap = 4d;
        double tabWidth = Math.Clamp(
            (panelWidth - 20d - gap * (sectionCodes.Length - 1)) / sectionCodes.Length,
            72d,
            160d);
        InspectorSectionTarget target = composingInspectorSectionTarget;
        string keyPrefix = target == InspectorSectionTarget.ReplayView
            ? "replayViewInspector-"
            : "inspector-";
        for (int index = 0; index < sectionCodes.Length; index++)
        {
            string sectionCode = sectionCodes[index];
            composer.AddSmallButton(
                Lang.Get("vsdirector:inspector-" + sectionCode),
                () => SetInspectorSection(sectionCode, target),
                ElementBounds.Fixed(10d + index * (tabWidth + gap), y, tabWidth, 28),
                inspectorSection == sectionCode ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                keyPrefix + sectionCode);
        }
        y += 38;
    }

    private void ComposeActionBasicInspector(
        GuiComposer composer,
        CairoFont small,
        DirectorActionAsset action,
        double panelWidth,
        ref int y)
    {
        double contentWidth = panelWidth - 20d;
        AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "actionName", action.Name, ref y,
            AutoSaveTarget.ActionMetadata);

        string[] categoryCodes = { "player", "npc", "creature", "expression", "effect" };
        string[] categoryNames =
        {
            Lang.Get("vsdirector:action-category-player"),
            Lang.Get("vsdirector:action-category-npc"),
            Lang.Get("vsdirector:action-category-creature"),
            Lang.Get("vsdirector:action-category-expression"),
            Lang.Get("vsdirector:action-category-effect")
        };
        composer
            .AddStaticText(Lang.Get("vsdirector:action-category"), small,
                ElementBounds.Fixed(10, y + 6, 90, 22))
            .AddDropDown(categoryCodes, categoryNames,
                Math.Max(0, Array.IndexOf(categoryCodes, ActionCategoryCode(action.Category))),
                OnActionCategoryChanged,
                ElementBounds.Fixed(104, y, Math.Max(110d, contentWidth * 0.38d), 28), "actionCategory")
            .AddStaticText(Lang.Get("vsdirector:action-version"), small,
                ElementBounds.Fixed(116 + contentWidth * 0.38d, y + 6, 60, 22))
            .AddTextInput(ElementBounds.Fixed(180 + contentWidth * 0.38d, y,
                    Math.Max(70d, contentWidth - 170d - contentWidth * 0.38d), 28),
                AutoSaveOnChanged(AutoSaveTarget.ActionMetadata),
                DirectorFont.TextInput(),
                "actionVersion",
                AutoSaveOnFocusLost(AutoSaveTarget.ActionMetadata));
        y += 34;
        AddLabeledInput(composer, small, Lang.Get("vsdirector:action-tags"), "actionTags", actionTagsText, ref y,
            AutoSaveTarget.ActionMetadata);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:action-author"), "actionAuthor", actionAuthorText, ref y,
            AutoSaveTarget.ActionMetadata);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:description"), "actionDescription", actionDescriptionText, ref y,
            AutoSaveTarget.ActionMetadata);
        composer
            .AddSmallButton(action.Favorite
                    ? Lang.Get("vsdirector:action-unfavorite")
                    : Lang.Get("vsdirector:action-favorite"),
                ToggleActionFavorite, ElementBounds.Fixed(10, y, 104, 28), EnumButtonStyle.Small, "actionFavorite")
            .AddDirectorIconButton(DirectorIcon.Delete, DeleteLabel(action.Id),
                DeleteActionEditor, ElementBounds.Fixed(panelWidth - 40, y, 30, 28),
                EnumButtonStyle.Small, "actionDelete");
        y += 36;
    }

    private void ComposeActionPreviewInspector(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        DirectorActionAsset action,
        double panelWidth,
        ref int y)
    {
        string[] loopCodes = { "once", "loop", "pingpong" };
        string[] loopNames =
        {
            Lang.Get("vsdirector:loop-once"),
            Lang.Get("vsdirector:loop-loop"),
            Lang.Get("vsdirector:loop-pingpong")
        };
        composer
            .AddStaticText(Lang.Get("vsdirector:loop-mode"), small, ElementBounds.Fixed(10, y + 6, 72, 22))
            .AddDropDown(loopCodes, loopNames, Math.Max(0, Array.IndexOf(loopCodes, actionPreviewLoopCode)),
                OnActionLoopChanged, ElementBounds.Fixed(86, y, 116, 28), "actionLoop")
            .AddStaticText(Lang.Get("vsdirector:action-speed"), small, ElementBounds.Fixed(210, y + 6, 50, 22))
            .AddNumberInput(ElementBounds.Fixed(264, y, 68, 28), null, DirectorFont.TextInput(), "actionSpeed")
            .AddStaticText(Lang.Get("vsdirector:action-loops"), small, ElementBounds.Fixed(340, y + 6, 48, 22))
            .AddNumberInput(ElementBounds.Fixed(392, y, Math.Clamp(panelWidth - 402d, 60d, 90d), 28),
                null, DirectorFont.TextInput(), "actionLoopCount");
        y += 34;

        IReadOnlyList<DirectorReplayActorPathSource> actors = owner.ActionActorSources;
        if (!actors.Any(actor => actor.ActorKey == actionPreviewActorKey))
        {
            actionPreviewActorKey = actors.FirstOrDefault().ActorKey ?? string.Empty;
        }
        string[] actorValues = actors.Count == 0 ? new[] { string.Empty } : actors.Select(actor => actor.ActorKey).ToArray();
        string[] actorNames = actors.Count == 0
            ? new[] { string.Empty }
            : actors.Select(actor => actor.DisplayName).ToArray();
        IReadOnlyList<DirectorExternalModelInfo> models = owner.ActionModels
            .Where(model => !model.PreviewOnly)
            .ToArray();
        if (actionPreviewModelCode.Length == 0)
        {
            actionPreviewModelCode = action.PreviewModelCode;
        }
        List<string> modelValues = new() { string.Empty };
        List<string> modelNames = new() { Lang.Get("vsdirector:action-model-recorded") };
        foreach (DirectorExternalModelInfo model in models)
        {
            modelValues.Add(model.Code);
            modelNames.Add(model.Name);
        }
        if (actionPreviewModelCode.Length > 0
            && !modelValues.Contains(actionPreviewModelCode, StringComparer.OrdinalIgnoreCase))
        {
            modelValues.Insert(1, actionPreviewModelCode);
            modelNames.Insert(1, Lang.Get("vsdirector:action-model-unavailable", actionPreviewModelCode));
        }
        composer
            .AddStaticText(Lang.Get("vsdirector:action-preview-actor"), small,
                ElementBounds.Fixed(10, y + 6, 72, 22))
            .AddDropDown(actorValues, actorNames,
                Math.Max(0, Array.IndexOf(actorValues, actionPreviewActorKey)),
                OnActionActorChanged, ElementBounds.Fixed(86, y, 168, 28), "actionActor")
            .AddStaticText(Lang.Get("vsdirector:action-preview-model"), small,
                ElementBounds.Fixed(262, y + 6, 64, 22))
            .AddDropDown(modelValues.ToArray(), modelNames.ToArray(),
                Math.Max(0, modelValues.FindIndex(code => string.Equals(
                    code, actionPreviewModelCode, StringComparison.OrdinalIgnoreCase))),
                OnActionModelChanged, ElementBounds.Fixed(330, y,
                    Math.Clamp(panelWidth - 340d, 100d, 280d), 28), "actionModel");
        y += 34;
        composer
            .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:action-preview"),
                PreviewSelectedAction, ElementBounds.Fixed(10, y, 30, 28), EnumButtonStyle.Normal, "actionPreview")
            .AddDirectorIconButton(DirectorIcon.Stop, Lang.Get("vsdirector:action-preview-stop"),
                StopActionPreview, ElementBounds.Fixed(46, y, 30, 28), EnumButtonStyle.Small, "actionStop")
            .AddDirectorIconButton(DirectorIcon.Previous, Lang.Get("vsdirector:action-preview-rotate-left"),
                RotateActionPreviewLeft, ElementBounds.Fixed(82, y, 30, 28), EnumButtonStyle.Small, "actionRotateLeft")
            .AddDirectorIconButton(DirectorIcon.Next, Lang.Get("vsdirector:action-preview-rotate-right"),
                RotateActionPreviewRight, ElementBounds.Fixed(118, y, 30, 28), EnumButtonStyle.Small, "actionRotateRight")
            .AddDirectorIconButton(DirectorIcon.ZoomIn, Lang.Get("vsdirector:action-preview-zoom-in"),
                ZoomActionPreviewIn, ElementBounds.Fixed(154, y, 30, 28), EnumButtonStyle.Small, "actionZoomIn")
            .AddDirectorIconButton(DirectorIcon.ZoomOut, Lang.Get("vsdirector:action-preview-zoom-out"),
                ZoomActionPreviewOut, ElementBounds.Fixed(190, y, 30, 28), EnumButtonStyle.Small, "actionZoomOut")
            .AddDirectorIconButton(DirectorIcon.Fit, Lang.Get("vsdirector:action-preview-reset-view"),
                ResetActionPreviewView, ElementBounds.Fixed(226, y, 30, 28), EnumButtonStyle.Small, "actionResetView");
        y += 36;
        string storedPreviewModel = action.PreviewEntityName.Length > 0
            ? action.PreviewEntityName
            : Lang.Get("vsdirector:action-preview-model-none");
        composer
            .AddSmallButton(Lang.Get("vsdirector:action-preview-model-store"), StoreActionPreviewModel,
                ElementBounds.Fixed(10, y, 142, 28), EnumButtonStyle.Small, "actionStoreModel")
            .AddSmallButton(Lang.Get("vsdirector:action-preview-model-clear"), ClearActionPreviewModel,
                ElementBounds.Fixed(158, y, 120, 28), EnumButtonStyle.Small, "actionClearModel")
            .AddStaticText(Ellipsize(storedPreviewModel, 34), detail,
                ElementBounds.Fixed(286, y + 6, Math.Max(80d, panelWidth - 296d), 22));
        y += 34;
    }

    private void ComposeActionLayerInspector(
        GuiComposer composer,
        CairoFont small,
        DirectorActionAsset action,
        double panelWidth,
        ref int y)
    {
        DirectorActionLayer? layer = SelectedActionLayer();
        string[] layerValues = action.Layers.Select(item => item.Id.ToString("D")).ToArray();
        string[] layerNames = action.Layers.Select(item => item.Name + " | " + ActionLayerKindLabel(item.Kind)).ToArray();
        if (layerValues.Length == 0)
        {
            layerValues = new[] { Guid.Empty.ToString("D") };
            layerNames = new[] { string.Empty };
        }
        composer
            .AddStaticText(Lang.Get("vsdirector:action-layer"), small, ElementBounds.Fixed(10, y + 6, 56, 22))
            .AddDropDown(layerValues, layerNames,
                Math.Max(0, Array.IndexOf(layerValues, selectedActionLayerId.ToString("D"))),
                OnActionLayerChanged, ElementBounds.Fixed(70, y,
                    Math.Clamp(panelWidth - 190d, 160d, 360d), 28), "actionLayer")
            .AddDirectorIconButton(DirectorIcon.Add, Lang.Get("vsdirector:action-layer-add"),
                AddActionLayer, ElementBounds.Fixed(panelWidth - 112, y, 30, 28), EnumButtonStyle.Small, "actionLayerAdd")
            .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:action-layer-delete"),
                DeleteActionLayer, ElementBounds.Fixed(panelWidth - 76, y, 30, 28), EnumButtonStyle.Small, "actionLayerDelete");
        y += 34;
        string[] kindCodes = { "full", "upper", "lower", "head", "hands", "expression", "additive" };
        string[] kindNames = kindCodes.Select(code => Lang.Get("vsdirector:action-layer-" + code)).ToArray();
        composer
            .AddDropDown(kindCodes, kindNames,
                Math.Max(0, Array.IndexOf(kindCodes, layer is null ? "full" : ActionLayerKindCode(layer.Kind))),
                OnActionLayerKindChanged, ElementBounds.Fixed(10, y, 112, 28), "actionLayerKind")
            .AddStaticText(Lang.Get("vsdirector:action-weight"), small, ElementBounds.Fixed(130, y + 6, 48, 22))
            .AddNumberInput(ElementBounds.Fixed(182, y, 64, 28), null, DirectorFont.TextInput(), "actionLayerWeight")
            .AddStaticText(Lang.Get("vsdirector:action-fade-in"), small, ElementBounds.Fixed(254, y + 6, 44, 22))
            .AddNumberInput(ElementBounds.Fixed(302, y, 64, 28), null, DirectorFont.TextInput(), "actionLayerFadeIn")
            .AddStaticText(Lang.Get("vsdirector:action-fade-out"), small, ElementBounds.Fixed(374, y + 6, 44, 22))
            .AddNumberInput(ElementBounds.Fixed(422, y, Math.Clamp(panelWidth - 432d, 50d, 70d), 28),
                null, DirectorFont.TextInput(), "actionLayerFadeOut");
        y += 34;

        composer
            .AddStaticText(Lang.Get("vsdirector:action-trim"), small, ElementBounds.Fixed(10, y + 6, 52, 22))
            .AddNumberInput(ElementBounds.Fixed(66, y, 72, 28), null, DirectorFont.TextInput(), "actionTrimStart")
            .AddStaticText("-", small, ElementBounds.Fixed(144, y + 6, 12, 22))
            .AddNumberInput(ElementBounds.Fixed(160, y, 72, 28), null, DirectorFont.TextInput(), "actionTrimEnd")
            .AddSmallButton(Lang.Get("vsdirector:action-trim-apply"), TrimAction,
                ElementBounds.Fixed(240, y, Math.Max(92d, panelWidth - 250d), 28),
                EnumButtonStyle.Small, "actionTrim");
        y += 34;
    }

    private void ComposeActionEventInspector(
        GuiComposer composer,
        CairoFont small,
        double panelWidth,
        ref int y)
    {
        string[] eventCodes = { "sound", "particle", "marker" };
        string[] eventNames =
        {
            Lang.Get("vsdirector:action-event-sound"),
            Lang.Get("vsdirector:action-event-particle"),
            Lang.Get("vsdirector:action-event-marker")
        };
        composer
            .AddDropDown(eventCodes, eventNames, Math.Max(0, Array.IndexOf(eventCodes, actionEventTypeCode)),
                OnActionEventTypeChanged, ElementBounds.Fixed(10, y, 92, 28), "actionEventType")
            .AddNumberInput(ElementBounds.Fixed(110, y, 66, 28), null, DirectorFont.TextInput(), "actionEventTime")
            .AddTextInput(ElementBounds.Fixed(184, y, Math.Clamp(panelWidth - 306d, 120d, 300d), 28),
                null, DirectorFont.TextInput(), "actionEventCode")
            .AddDirectorIconButton(DirectorIcon.Add, Lang.Get("vsdirector:action-event-add"),
                AddActionEvent, ElementBounds.Fixed(panelWidth - 40, y, 30, 28), EnumButtonStyle.Small, "actionEventAdd");
        y += 38;
        if (owner.Config.ShowPackageImportExport)
        {
            double actionWidth = Math.Max(76d, (panelWidth - 32d) / 3d);
            composer
                .AddSmallButton(Lang.Get("vsdirector:export-json"), ExportActionJson,
                    ElementBounds.Fixed(10, y, actionWidth, 28), EnumButtonStyle.Small, "actionJson")
                .AddSmallButton(".vsaction", ExportActionBinary,
                    ElementBounds.Fixed(16 + actionWidth, y, actionWidth, 28),
                    EnumButtonStyle.Small, "actionBinary")
                .AddSmallButton(Lang.Get("vsdirector:action-pack"), ExportActionPack,
                    ElementBounds.Fixed(22 + actionWidth * 2d, y, actionWidth, 28),
                    EnumButtonStyle.Small, "actionPack");
            y += 34;
        }
    }

    private void ComposeModelInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        DirectorModelAsset? model = modelEdit;
        if (model is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:model-library-select"), ref y);
            return;
        }
        AddInspectorHeader(composer, small, model.Name, ref y);
        double panelWidth = layout.Panel("inspector").Width;
        bool previewOnly = owner.ActionModels.FirstOrDefault(candidate => string.Equals(
            candidate.Code,
            model.ModelCode,
            StringComparison.OrdinalIgnoreCase))?.PreviewOnly == true;
        if (previewOnly)
        {
            inspectorSection = "preview";
        }
        else
        {
            ComposeInspectorSections(composer, ref y, "basic", "shape", "preview");
        }
        if (inspectorSection == "shape")
        {
            AddLabeledInput(composer, small, Lang.Get("vsdirector:model-shape-asset"),
                "modelShapeAsset", modelShapeAssetText, ref y);
            AddLabeledInput(composer, small, Lang.Get("vsdirector:model-shape-source"),
                "modelShapeSource", modelShapeSourceText, ref y);
            composer.AddRichtext(
                WebUtility.HtmlEncode(owner.Vsmc2ShapeStatus(model)),
                detail,
                ElementBounds.Fixed(10, y, Math.Max(180d, panelWidth - 20d), 42),
                "modelShapeStatus");
            y += 46;
            composer
                .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:vsmc2-open"),
                    OpenModelShapeInVsmc2, ElementBounds.Fixed(10, y, 30, 28),
                    EnumButtonStyle.Small, "modelShapeOpen")
                .AddDirectorIconButton(DirectorIcon.Export, Lang.Get("vsdirector:model-shape-folder"),
                    OpenModelShapeFolder, ElementBounds.Fixed(46, y, 30, 28),
                    EnumButtonStyle.Small, "modelShapeFolder")
                .AddDirectorIconButton(DirectorIcon.Refresh, Lang.Get("vsdirector:model-shape-rescan"),
                    RescanModelShape, ElementBounds.Fixed(82, y, 30, 28),
                    EnumButtonStyle.Small, "modelShapeRescan")
                .AddDirectorIconButton(DirectorIcon.Close, Lang.Get("vsdirector:model-shape-ignore"),
                    IgnoreModelShapeChange, ElementBounds.Fixed(118, y, 30, 28),
                    EnumButtonStyle.Small, "modelShapeIgnore")
                .AddDirectorIconButton(DirectorIcon.Capture, Lang.Get("vsdirector:model-shape-auto-reload"),
                    EnableModelShapeAutoReload, ElementBounds.Fixed(154, y, 30, 28),
                    EnumButtonStyle.Small, "modelShapeAutoReload");
            bool hasSourcePath = modelShapeSourceText.Length > 0;
            composer.GetButton("modelShapeOpen").Enabled = owner.Config.EnableVsmc2Integration
                && File.Exists(owner.Config.Vsmc2ExecutablePath)
                && File.Exists(modelShapeSourceText);
            composer.GetButton("modelShapeFolder").Enabled = hasSourcePath
                && ShapeDirectoryExists(modelShapeSourceText);
            composer.GetButton("modelShapeRescan").Enabled = owner.Config.EnableVsmc2Integration
                && hasSourcePath;
            composer.GetButton("modelShapeIgnore").Enabled = owner.HasPendingVsmc2ShapeChange(model.Id);
            composer.GetButton("modelShapeAutoReload").Enabled = owner.Config.EnableVsmc2Integration
                && !owner.Config.Vsmc2AutoReload;
            y += 38;
        }
        else if (inspectorSection == "preview")
        {
            composer
                .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:action-preview"),
                    PreviewModelEditor, ElementBounds.Fixed(10, y, 30, 28), EnumButtonStyle.Normal, "modelPreview")
                .AddDirectorIconButton(DirectorIcon.Stop, Lang.Get("vsdirector:action-preview-stop"),
                    StopActionPreview, ElementBounds.Fixed(46, y, 30, 28), EnumButtonStyle.Small, "modelStop")
                .AddDirectorIconButton(DirectorIcon.Previous, Lang.Get("vsdirector:action-preview-rotate-left"),
                    RotateActionPreviewLeft, ElementBounds.Fixed(82, y, 30, 28), EnumButtonStyle.Small, "modelRotateLeft")
                .AddDirectorIconButton(DirectorIcon.Next, Lang.Get("vsdirector:action-preview-rotate-right"),
                    RotateActionPreviewRight, ElementBounds.Fixed(118, y, 30, 28), EnumButtonStyle.Small, "modelRotateRight")
                .AddDirectorIconButton(DirectorIcon.ZoomIn, Lang.Get("vsdirector:action-preview-zoom-in"),
                    ZoomActionPreviewIn, ElementBounds.Fixed(154, y, 30, 28), EnumButtonStyle.Small, "modelZoomIn")
                .AddDirectorIconButton(DirectorIcon.ZoomOut, Lang.Get("vsdirector:action-preview-zoom-out"),
                    ZoomActionPreviewOut, ElementBounds.Fixed(190, y, 30, 28), EnumButtonStyle.Small, "modelZoomOut")
                .AddDirectorIconButton(DirectorIcon.Fit, Lang.Get("vsdirector:action-preview-reset-view"),
                    ResetActionPreviewView, ElementBounds.Fixed(226, y, 30, 28), EnumButtonStyle.Small, "modelResetView");
            y += 38;
            if (previewOnly)
            {
                composer.AddRichtext(
                    WebUtility.HtmlEncode(Lang.Get("vsdirector:model-green-screen-preview-note")),
                    detail,
                    ElementBounds.Fixed(10, y, Math.Max(160d, panelWidth - 20d), 58),
                    "greenScreenPreviewNote");
                y += 62;
                return;
            }
        }
        else
        {
            AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "modelName", modelNameText, ref y,
                AutoSaveTarget.ModelMetadata);
            AddLabeledInput(composer, small, Lang.Get("vsdirector:model-code"), "modelCode", modelCodeText, ref y,
                AutoSaveTarget.ModelMetadata);
            AddLabeledInput(composer, small, Lang.Get("vsdirector:model-entity-code"),
                "modelEntityCode", modelEntityCodeText, ref y, AutoSaveTarget.ModelMetadata);
            AddLabeledInput(composer, small, Lang.Get("vsdirector:model-group"), "modelGroup", modelGroupText, ref y,
                AutoSaveTarget.ModelMetadata);
            AddLabeledInput(composer, small, Lang.Get("vsdirector:description"),
                "modelDescription", modelDescriptionText, ref y, AutoSaveTarget.ModelMetadata);
            AddLabeledNumber(composer, small, Lang.Get("vsdirector:model-size"),
                "modelSize", ParseDouble(modelSizeText, 1d), ref y,
                autoSaveTarget: AutoSaveTarget.ModelMetadata);
            composer.AddStaticText(
                Lang.Get("vsdirector:model-library-metadata", model.SourceModId.Length == 0
                    ? Lang.Get("vsdirector:model-library-saved")
                    : model.SourceModId),
                detail,
                ElementBounds.Fixed(10, y, Math.Max(160d, panelWidth - 20d), 40));
            y += 44;
        }
        composer
            .AddSmallButton(model.Favorite
                    ? Lang.Get("vsdirector:action-unfavorite")
                    : Lang.Get("vsdirector:action-favorite"),
                ToggleModelFavorite, ElementBounds.Fixed(10, y, 100, 28), EnumButtonStyle.Small, "modelFavorite")
            .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete"),
                DeleteModelEditor, ElementBounds.Fixed(panelWidth - 46, y, 30, 28),
                EnumButtonStyle.Small, "modelDelete");
        y += 38;
    }

    private void ComposeActionRecordRow(GuiComposer composer, CairoFont small, ref int y)
    {
        double panelWidth = layout.Panel("inspector").Width;
        composer
            .AddStaticText(Lang.Get("vsdirector:action-record-range"), small,
                ElementBounds.Fixed(10, y + 6, 92, 22))
            .AddNumberInput(ElementBounds.Fixed(106, y, 72, 28), null, DirectorFont.TextInput(), "actionRecordStart")
            .AddStaticText("-", small, ElementBounds.Fixed(184, y + 6, 12, 22))
            .AddNumberInput(ElementBounds.Fixed(200, y, 72, 28), null, DirectorFont.TextInput(), "actionRecordEnd")
            .AddSmallButton(Lang.Get("vsdirector:action-record-from-replay"), RecordActionFromReplay,
                ElementBounds.Fixed(280, y, Math.Clamp(panelWidth - 290d, 120d, 220d), 28),
                EnumButtonStyle.Normal, "actionRecordReplay");
        y += 34;
        composer
            .AddStaticText(Lang.Get("vsdirector:action-live-record"), small,
                ElementBounds.Fixed(10, y + 6, 92, 22))
            .AddSmallButton(
                owner.IsLiveActionRecording
                    ? Lang.Get("vsdirector:action-live-stop", owner.LiveActionRecordingSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                    : Lang.Get("vsdirector:action-live-start"),
                ToggleLiveActionRecording,
                ElementBounds.Fixed(106, y, Math.Clamp(panelWidth - 116d, 180d, 260d), 28),
                owner.IsLiveActionRecording ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "actionLiveRecord");
        y += 34;
    }

    private void ComposeActionImportRow(GuiComposer composer, CairoFont small, ref int y)
    {
        double panelWidth = layout.Panel("inspector").Width;
        double inputWidth = Math.Clamp(panelWidth - 202d, 180d, 360d);
        composer
            .AddStaticText(Lang.Get("vsdirector:action-import-file"), small,
                ElementBounds.Fixed(10, y + 6, 82, 22))
            .AddTextInput(ElementBounds.Fixed(96, y, inputWidth, 28),
                null, DirectorFont.TextInput(), "actionImportPath")
            .AddSmallButton(Lang.Get("vsdirector:import"), ImportActionFile,
                ElementBounds.Fixed(102d + inputWidth, y, 88, 28), EnumButtonStyle.Small, "actionImportFile");
        y += 34;
    }

    private void ComposeCategorizedPathInspector(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        if (pathEdit is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-path"), ref y);
            return;
        }

        string viewLabel = Lang.Get("vsdirector:replay-right-view");
        string propertiesLabel = Lang.Get("vsdirector:replay-right-inspector");
        double viewWidth = ToolbarButtonWidth(viewLabel);
        double propertiesWidth = ToolbarButtonWidth(propertiesLabel);
        composer
            .AddDirectorToolbarButton(viewLabel,
                () => SetPathRightTab("visuals"),
                ElementBounds.Fixed(0d, y, viewWidth, 28d),
                pathRightTab == "visuals" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "pathRightVisuals")
            .AddDirectorToolbarButton(propertiesLabel,
                () => SetPathRightTab("properties"),
                ElementBounds.Fixed(viewWidth, y, propertiesWidth, 28d),
                pathRightTab == "properties" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "pathRightProperties");
        y += 30;

        if (pathRightTab == "properties")
        {
            ComposePathPropertiesPanel(composer, small, ref y);
            return;
        }

        ComposePathVisualsPanel(composer, small, ref y);
    }

    private bool SetPathRightTab(string tab)
    {
        string nextTab = tab == "properties" ? "properties" : "visuals";
        if (pathRightTab == nextTab)
        {
            return true;
        }

        FlushPendingAutoSave(AutoSaveTarget.Path);
        FlushPendingAutoSave(AutoSaveTarget.PathKeyframe);
        FlushPendingAutoSave(AutoSaveTarget.PathMetadata);
        pathRightTab = nextTab;
        ComposeInspector();
        return true;
    }

    private void FlushPendingPathAutoSave()
    {
        if (section != DirectorWorkspaceSection.Paths)
        {
            FlushPendingAutoSave(AutoSaveTarget.PathKeyframe);
            return;
        }

        FlushPendingAutoSave(pathRightTab == "properties"
            ? AutoSaveTarget.PathMetadata
            : AutoSaveTarget.Path);
    }

    private void ComposePathVisualsPanel(GuiComposer composer, CairoFont small, ref int y)
    {
        double panelWidth = layout.Panel("inspector").Width;
        double contentWidth = Math.Max(200d, panelWidth - 20d);
        CameraKeyframe? frame = SelectedPathFrame();
        composer
            .AddDirectorSingleLineText(
                Lang.Get("vsdirector:bezier-custom-handles"),
                small,
                ElementBounds.Fixed(10d, y + 5d, Math.Max(120d, contentWidth - 40d), 22d))
            .AddSwitch(SetSelectedBezierHandlesEnabled,
                ElementBounds.Fixed(panelWidth - 36d, y + 2d, 25d, 25d),
                "pathBezierEnabled", 25d, 3d);
        y += 32;

        if (frame is not null)
        {
            AddAutoSavePathInspectorNumberRow(composer, small, ref y, AutoSaveTarget.Path,
                (Lang.Get("vsdirector:bezier-in-x"), "pathBezierInX"),
                (Lang.Get("vsdirector:bezier-in-y"), "pathBezierInY"),
                (Lang.Get("vsdirector:bezier-in-z"), "pathBezierInZ"));
            AddAutoSavePathInspectorNumberRow(composer, small, ref y, AutoSaveTarget.Path,
                (Lang.Get("vsdirector:bezier-out-x"), "pathBezierOutX"),
                (Lang.Get("vsdirector:bezier-out-y"), "pathBezierOutY"),
                (Lang.Get("vsdirector:bezier-out-z"), "pathBezierOutZ"));
        }

        if (CanManageChunkPreload)
        {
            double labelWidth = Math.Clamp(panelWidth * 0.47d, 118d, 220d);
            double controlX = 16d + labelWidth;
            composer
                .AddDirectorSingleLineText(
                    Lang.Get("vsdirector:replay-chunk-preload"),
                    small,
                    ElementBounds.Fixed(10d, y + 6d, labelWidth, 22d))
                .AddDirectorButton(
                    Lang.Get("vsdirector:edit"),
                    OpenPathPreloadEditor,
                    ElementBounds.Fixed(controlX, y,
                        Math.Max(74d, panelWidth - controlX - 12d), 28d),
                    EnumButtonStyle.Small,
                    "pathChunkPreloadEdit");
            composer.GetButton("pathChunkPreloadEdit").Enabled = pathEdit is { Keyframes.Count: > 0 };
            y += 34;
        }

        CameraPath path = pathEdit!;
        composer.AddDirectorIconButton(
            DirectorIcon.Delete,
            DeleteLabel(path.Id, "vsdirector:delete-path"),
            DeletePathEditor,
            ElementBounds.Fixed(10, y, 30, 28),
            EnumButtonStyle.Small,
            "deletePath");
        y += 34;
    }

    private bool OpenPathPreloadEditor()
    {
        if (!CanManageChunkPreload || pathEdit is not { Keyframes.Count: > 0 })
        {
            return true;
        }

        preloadEditorExpanded = true;
        selectingPreloadGrid = false;
        panningPreloadGrid = false;
        preloadPanChunkX = 0;
        preloadPanChunkZ = 0;
        ComposeWorkflowCanvas();
        return true;
    }

    private void ComposePathPropertiesPanel(GuiComposer composer, CairoFont small, ref int y)
    {
        CameraPath path = pathEdit!;
        AddLabeledInput(composer, small, Lang.Get("vsdirector:name"),
            "pathName", path.Name, ref y, AutoSaveTarget.PathMetadata);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:tags"),
            "pathTags", string.Join(", ", path.Tags), ref y, AutoSaveTarget.PathMetadata);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:description"),
            "pathDescription", path.Description, ref y, AutoSaveTarget.PathMetadata);
    }

    private void ComposePathInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        CameraPath? path = pathEdit;
        if (path is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-path"), ref y);
            return;
        }

        double panelWidth = layout.Panel("inspector").Width;
        double contentWidth = Math.Max(200d, panelWidth - 20d);
        AddInspectorHeader(composer, small, path.Name, ref y);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "pathName", path.Name, ref y);

        string[] interpolationCodes = { "linear", "ease", "bezier", "step" };
        string[] interpolationNames =
        {
            Lang.Get("vsdirector:interpolation-linear"),
            Lang.Get("vsdirector:interpolation-easeinout"),
            Lang.Get("vsdirector:interpolation-bezier"),
            Lang.Get("vsdirector:interpolation-step")
        };
        string[] loopCodes = { "once", "loop", "pingpong" };
        string[] loopNames =
        {
            Lang.Get("vsdirector:loop-once"),
            Lang.Get("vsdirector:loop-loop"),
            Lang.Get("vsdirector:loop-pingpong")
        };
        double half = (contentWidth - 8d) / 2d;
        composer
            .AddDropDown(interpolationCodes, interpolationNames,
                Math.Max(0, Array.IndexOf(interpolationCodes, PathInterpolationCode(path))),
                OnPathInterpolationChanged, ElementBounds.Fixed(10, y, half, 28), "pathInterpolation")
            .AddDropDown(loopCodes, loopNames,
                Math.Max(0, Array.IndexOf(loopCodes, PathLoopCode(path))),
                OnPathLoopChanged, ElementBounds.Fixed(18 + half, y, half, 28), "pathLoop");
        y += 34;
        composer
            .AddStaticText(Lang.Get("vsdirector:path-speed"), small,
                ElementBounds.Fixed(10, y + 6, 80, 22))
            .AddDirectorSlider(OnPathSpeedChanged,
                ElementBounds.Fixed(92, y + 1, Math.Max(100d, contentWidth - 218d), 26), "pathSpeed")
            .AddSwitch(SetPathPreviewVisible,
                ElementBounds.Fixed(panelWidth - 112, y + 2, 25, 25), "pathPreview", 25, 3)
            .AddStaticText(Lang.Get("vsdirector:path-preview"), small,
                ElementBounds.Fixed(panelWidth - 78, y + 5, 68, 22));
        y += 34;
        composer
            .AddStaticText(Lang.Get("vsdirector:path-duration"), small,
                ElementBounds.Fixed(10, y + 6, 112, 22))
            .AddNumberInput(ElementBounds.Fixed(128, y,
                    Math.Clamp(contentWidth - 170d, 80d, 160d), 28),
                null, DirectorFont.TextInput(), "pathTotalDuration")
            .AddDirectorIconButton(DirectorIcon.Select, Lang.Get("vsdirector:path-duration-apply"),
                ApplyPathTotalDuration,
                ElementBounds.Fixed(panelWidth - 46, y, 30, 28), EnumButtonStyle.Small,
                "applyPathDuration");
        y += 34;

        CameraKeyframe? frame = SelectedPathFrame();
        composer.AddStaticText(
            frame is null
                ? string.Empty
                : Lang.Get("vsdirector:path-selected-keyframe", SelectedPathKeyframeIndex() + 1,
                    selectedPathKeyframes.Count),
            small,
            ElementBounds.Fixed(10, y, contentWidth, 22));
        y += 24;
        composer
            .AddStaticText(Lang.Get("vsdirector:keyframe-name"), small,
                ElementBounds.Fixed(10, y + 6, 112, 22))
            .AddTextInput(ElementBounds.Fixed(128, y,
                    Math.Clamp(contentWidth - 118d, 140d, 420d), 28),
                null, DirectorFont.TextInput(), "pathKeyframeName");
        y += 34;

        AddPathInspectorNumberRow(composer, small, ref y,
            (Lang.Get("vsdirector:keyframe-time"), "pathFrameStart"),
            (Lang.Get("vsdirector:keyframe-duration"), "pathFrameDuration"),
            (Lang.Get("vsdirector:keyframe-hold"), "pathFrameHold"));
        AddPathInspectorNumberRow(composer, small, ref y,
            ("X", "pathPosX"), ("Y", "pathPosY"), ("Z", "pathPosZ"));
        AddPathInspectorNumberRow(composer, small, ref y,
            ("Yaw", "pathYaw"), ("Pitch", "pathPitch"));

        string[] segmentCodes = { "default", "linear", "ease", "bezier", "step" };
        string[] segmentNames =
        {
            Lang.Get("vsdirector:segment-interpolation-default"),
            Lang.Get("vsdirector:interpolation-linear"),
            Lang.Get("vsdirector:interpolation-easeinout"),
            Lang.Get("vsdirector:interpolation-bezier"),
            Lang.Get("vsdirector:interpolation-step")
        };
        composer
            .AddStaticText(Lang.Get("vsdirector:segment-interpolation"), small,
                ElementBounds.Fixed(10, y + 6, 116, 22))
            .AddDropDown(segmentCodes, segmentNames,
                Math.Max(0, Array.IndexOf(segmentCodes, SelectedSegmentInterpolationCode(path))),
                OnSegmentInterpolationChanged,
                ElementBounds.Fixed(132, y,
                    Math.Clamp(contentWidth - 122d, 160d, 420d), 28), "segmentInterpolation");
        y += 34;
        double speedWidth = Math.Max(58d, contentWidth * 0.16d);
        composer
            .AddStaticText(Lang.Get("vsdirector:segment-start-speed"), small,
                ElementBounds.Fixed(10, y + 6, 62, 22))
            .AddNumberInput(ElementBounds.Fixed(74, y, speedWidth, 28), null,
                DirectorFont.TextInput(), "pathSegmentStartSpeed")
            .AddStaticText(Lang.Get("vsdirector:segment-end-speed"), small,
                ElementBounds.Fixed(82 + speedWidth, y + 6, 58, 22))
            .AddNumberInput(ElementBounds.Fixed(142 + speedWidth, y, speedWidth, 28), null,
                DirectorFont.TextInput(), "pathSegmentEndSpeed")
            .AddTextInput(ElementBounds.Fixed(150 + speedWidth * 2d, y,
                    Math.Clamp(contentWidth - 140d - speedWidth * 2d, 60d, 240d), 28),
                null, DirectorFont.TextInput(), "pathSegmentMarker");
        y += 34;

        composer
            .AddSwitch(SetSelectedBezierHandlesEnabled,
                ElementBounds.Fixed(10, y + 2, 25, 25), "pathBezierEnabled", 25, 3)
            .AddStaticText(Lang.Get("vsdirector:bezier-custom-handles"), small,
                ElementBounds.Fixed(44, y + 5, contentWidth - 34, 22));
        y += 32;
        AddPathInspectorNumberRow(composer, small, ref y,
            (Lang.Get("vsdirector:bezier-in-x"), "pathBezierInX"),
            (Lang.Get("vsdirector:bezier-in-y"), "pathBezierInY"),
            (Lang.Get("vsdirector:bezier-in-z"), "pathBezierInZ"));
        AddPathInspectorNumberRow(composer, small, ref y,
            (Lang.Get("vsdirector:bezier-out-x"), "pathBezierOutX"),
            (Lang.Get("vsdirector:bezier-out-y"), "pathBezierOutY"),
            (Lang.Get("vsdirector:bezier-out-z"), "pathBezierOutZ"));

        composer
            .AddDirectorButton(Lang.Get("vsdirector:apply"), ApplyPathKeyframeProperties,
                ElementBounds.Fixed(10, y, 104, 30), EnumButtonStyle.Normal, "applyPathProperties")
            .AddDirectorIconButton(DirectorIcon.Capture, Lang.Get("vsdirector:recapture-keyframe"),
                RecaptureSelectedPathKeyframe,
                ElementBounds.Fixed(122, y, 30, 30), EnumButtonStyle.Small, "recapturePathKey")
            .AddDirectorIconButton(DirectorIcon.Previous, Lang.Get("vsdirector:previous-keyframe"),
                SelectPreviousPathKeyframe,
                ElementBounds.Fixed(158, y, 30, 30), EnumButtonStyle.Small, "previousPathKey")
            .AddDirectorIconButton(DirectorIcon.Next, Lang.Get("vsdirector:next-keyframe"),
                SelectNextPathKeyframe,
                ElementBounds.Fixed(194, y, 30, 30), EnumButtonStyle.Small, "nextPathKey")
            .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:save-path"), SavePathEditor,
                ElementBounds.Fixed(panelWidth - 118, y, 30, 30), EnumButtonStyle.Normal, "savePath")
            .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:play-path"),
                () => PlayEditedPath(false),
                ElementBounds.Fixed(panelWidth - 82, y, 30, 30), EnumButtonStyle.Small, "playPath")
            .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete-selected-keyframes"),
                DeleteSelectedPathKeyframes,
                ElementBounds.Fixed(panelWidth - 46, y, 30, 30), EnumButtonStyle.Small, "deletePathKey");
        y += 36;
        ComposePreloadEditorEntry(composer, ref y);
    }

    private void ComposeLegacyPathInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        CameraPath? path = pathEdit;
        if (path is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-path"), ref y);
            return;
        }

        AddInspectorHeader(composer, small, path.Name, ref y);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "pathName", path.Name, ref y);
        string[] interpolationCodes = { "linear", "ease", "bezier" };
        string[] interpolationNames =
        {
            Lang.Get("vsdirector:interpolation-linear"),
            Lang.Get("vsdirector:interpolation-easeinout"),
            Lang.Get("vsdirector:interpolation-bezier")
        };
        string[] loopCodes = { "once", "loop", "pingpong" };
        string[] loopNames =
        {
            Lang.Get("vsdirector:loop-once"),
            Lang.Get("vsdirector:loop-loop"),
            Lang.Get("vsdirector:loop-pingpong")
        };
        composer
            .AddStaticText(Lang.Get("vsdirector:interpolation"), small, ElementBounds.Fixed(10, y + 6, 100, 22))
            .AddDropDown(interpolationCodes, interpolationNames, Array.IndexOf(interpolationCodes, PathInterpolationCode(path)),
                OnPathInterpolationChanged, ElementBounds.Fixed(116, y, 150, 28), "pathInterpolation")
            .AddStaticText(Lang.Get("vsdirector:loop-mode"), small, ElementBounds.Fixed(274, y + 6, 80, 22))
            .AddDropDown(loopCodes, loopNames, Array.IndexOf(loopCodes, PathLoopCode(path)),
                OnPathLoopChanged, ElementBounds.Fixed(360, y, 140, 28), "pathLoop");
        y += 36;
        composer
            .AddStaticText(Lang.Get("vsdirector:path-speed"), small, ElementBounds.Fixed(10, y + 6, 90, 22))
            .AddDirectorSlider(OnPathSpeedChanged, ElementBounds.Fixed(96, y + 1, 180, 26), "pathSpeed")
            .AddSwitch(SetPathPreviewVisible,
                ElementBounds.Fixed(300, y + 2, 25, 25), "pathPreview", 25, 3)
            .AddStaticText(Lang.Get("vsdirector:path-preview"), small,
                ElementBounds.Fixed(334, y + 5, 156, 22));
        y += 36;
        composer
            .AddStaticText(Lang.Get("vsdirector:keyframe-start"), small,
                ElementBounds.Fixed(10, y + 6, 76, 22))
            .AddNumberInput(ElementBounds.Fixed(88, y, 86, 28), null, DirectorFont.TextInput(), "pathFrameStart")
            .AddStaticText(Lang.Get("vsdirector:keyframe-duration"), small,
                ElementBounds.Fixed(184, y + 6, 72, 22))
            .AddNumberInput(ElementBounds.Fixed(258, y, 86, 28), null, DirectorFont.TextInput(), "pathFrameDuration")
            .AddDirectorIconButton(DirectorIcon.Select, Lang.Get("vsdirector:apply-keyframe-timing"),
                ApplyPathKeyframeTiming, ElementBounds.Fixed(354, y, 30, 28),
                EnumButtonStyle.Normal, "applyPathTiming")
            .AddDirectorIconButton(DirectorIcon.Previous, Lang.Get("vsdirector:previous-keyframe"),
                SelectPreviousPathKeyframe, ElementBounds.Fixed(390, y, 30, 28),
                EnumButtonStyle.Small, "previousPathKey")
            .AddDirectorIconButton(DirectorIcon.Next, Lang.Get("vsdirector:next-keyframe"),
                SelectNextPathKeyframe, ElementBounds.Fixed(426, y, 30, 28),
                EnumButtonStyle.Small, "nextPathKey");
        y += 36;
        string[] segmentCodes = { "default", "linear", "ease", "bezier" };
        string[] segmentNames =
        {
            Lang.Get("vsdirector:segment-interpolation-default"),
            Lang.Get("vsdirector:interpolation-linear"),
            Lang.Get("vsdirector:interpolation-easeinout"),
            Lang.Get("vsdirector:interpolation-bezier")
        };
        composer
            .AddStaticText(Lang.Get("vsdirector:segment-interpolation"), small,
                ElementBounds.Fixed(10, y + 6, 100, 22))
            .AddDropDown(segmentCodes, segmentNames,
                Array.IndexOf(segmentCodes, SelectedSegmentInterpolationCode(path)),
                OnSegmentInterpolationChanged,
                ElementBounds.Fixed(116, y, 180, 28), "segmentInterpolation");
        y += 36;
        composer
            .AddStaticText(Lang.Get("vsdirector:segment-start-speed"), small,
                ElementBounds.Fixed(10, y + 6, 58, 22))
            .AddNumberInput(ElementBounds.Fixed(70, y, 64, 28), null,
                DirectorFont.TextInput(), "pathSegmentStartSpeed")
            .AddStaticText(Lang.Get("vsdirector:segment-end-speed"), small,
                ElementBounds.Fixed(142, y + 6, 52, 22))
            .AddNumberInput(ElementBounds.Fixed(198, y, 64, 28), null,
                DirectorFont.TextInput(), "pathSegmentEndSpeed")
            .AddStaticText(Lang.Get("vsdirector:path-segment-marker-short"), small,
                ElementBounds.Fixed(270, y + 6, 48, 22))
            .AddTextInput(ElementBounds.Fixed(322, y, 132, 28), null,
                DirectorFont.TextInput(), "pathSegmentMarker")
            .AddDirectorIconButton(DirectorIcon.Select, Lang.Get("vsdirector:apply-segment-metadata"),
                ApplyPathSegmentMetadata, ElementBounds.Fixed(462, y, 30, 28),
                EnumButtonStyle.Small, "applyPathSegmentMetadata");
        y += 36;
        composer
            .AddStaticText(Lang.Get("vsdirector:insert-start"), small,
                ElementBounds.Fixed(10, y + 6, 78, 22))
            .AddNumberInput(ElementBounds.Fixed(90, y, 90, 28), null, DirectorFont.TextInput(), "pathInsertStart")
            .AddStaticText(Lang.Get("vsdirector:insert-duration"), small,
                ElementBounds.Fixed(190, y + 6, 88, 22))
            .AddNumberInput(ElementBounds.Fixed(280, y, 90, 28), null, DirectorFont.TextInput(), "pathInsertDuration")
            .AddDirectorIconButton(DirectorIcon.Add, Lang.Get("vsdirector:insert-keyframe"),
                InsertPathKeyframe, ElementBounds.Fixed(378, y, 30, 28),
                EnumButtonStyle.Normal, "insertPathKey");
        y += 36;
        composer
            .AddDirectorIconButton(DirectorIcon.Capture, Lang.Get("vsdirector:recapture-keyframe"),
                RecaptureSelectedPathKeyframe, ElementBounds.Fixed(10, y, 30, 28),
                EnumButtonStyle.Normal, "recapturePathKey")
            .AddDirectorIconButton(DirectorIcon.Duplicate, Lang.Get("vsdirector:duplicate-keyframe"),
                DuplicatePathKeyframe, ElementBounds.Fixed(46, y, 30, 28),
                EnumButtonStyle.Small, "duplicatePathKey")
            .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete-selected-keyframes"),
                DeletePathKeyframe, ElementBounds.Fixed(82, y, 30, 28),
                EnumButtonStyle.Small, "deletePathKey")
            .AddDynamicText(PathSelectedKeyframeText(), detail,
                ElementBounds.Fixed(124, y, 366, 66), "pathKeyDetails");
        y += 70;
        composer
            .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:save-path"), SavePathEditor,
                ElementBounds.Fixed(10, y, 30, 28), EnumButtonStyle.Normal, "savePath")
            .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:play-path"),
                () => PlayEditedPath(false), ElementBounds.Fixed(46, y, 30, 28),
                EnumButtonStyle.Small, "playPath")
            .AddDirectorIconButton(DirectorIcon.Reverse, Lang.Get("vsdirector:reverse"),
                () => PlayEditedPath(true), ElementBounds.Fixed(82, y, 30, 28),
                EnumButtonStyle.Small, "reversePath")
            .AddDirectorIconButton(DirectorIcon.Delete, DeleteLabel(path.Id, "vsdirector:delete-path"),
                DeletePathEditor,
                ElementBounds.Fixed(118, y, 30, 28), EnumButtonStyle.Small, "deletePath");
        y += 34;
        ComposePreloadEditorEntry(composer, ref y);
        int pathDetailsHeight = (int)Math.Floor(layout.Panel("inspector").Height - y - 10d);
        if (pathDetailsHeight >= 24)
        {
            composer.AddDynamicText(PathEditorDetails(path), detail,
                ElementBounds.Fixed(10, y, 480, pathDetailsHeight), "pathDetails");
        }
    }

    private void ComposeCategorizedSequenceInspector(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        CameraSequence? sequence = sequenceEdit;
        if (sequence is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-sequence"), ref y);
            return;
        }

        double panelWidth = layout.Panel("inspector").Width;
        AddInspectorHeader(composer, small, sequence.Name, ref y);
        ComposeInspectorSections(composer, ref y, "basic", "shots");
        int firstShot = 0;
        int lastShot = 0;
        if (inspectorSection == "shots")
        {
            IReadOnlyList<CameraPreset> cameras = owner.Cameras;
            string[] cameraValues = cameras.Select(camera => camera.Id.ToString("D")).ToArray();
            string[] cameraNames = cameras.Select(camera => camera.Name).ToArray();
            if (cameras.Count > 0)
            {
                composer
                    .AddStaticText(Lang.Get("vsdirector:sequence-camera"), small,
                        ElementBounds.Fixed(10, y + 6, 100, 22))
                    .AddDropDown(cameraValues, cameraNames,
                        Math.Max(0, Array.IndexOf(cameraValues, SelectedSequenceCameraId().ToString("D"))),
                        OnSequenceCameraChanged,
                        ElementBounds.Fixed(114, y, Math.Max(130d, panelWidth - 124d), 28), "sequenceCamera");
                y += 34;
            }
            composer
                .AddNumberInput(ElementBounds.Fixed(10, y, 105, 28), null,
                    DirectorFont.TextInput(), "shotDuration")
                .AddDirectorIconButton(DirectorIcon.Add, Lang.Get("vsdirector:add-shot"), AddSequenceShot,
                    ElementBounds.Fixed(121, y, 30, 28), EnumButtonStyle.Normal, "addShot")
                .AddStaticText(Lang.Get("vsdirector:sequence-summary", sequence.Shots.Count,
                    sequence.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)), detail,
                    ElementBounds.Fixed(160, y + 5, Math.Max(120d, panelWidth - 170d), 22));
            y += 36;
            int rowsPerPage = CurrentSequenceRowsPerPage;
            int sequencePages = PageCount(sequence.Shots.Count, rowsPerPage);
            sequencePageIndex = Math.Clamp(sequencePageIndex, 0, sequencePages - 1);
            firstShot = sequencePageIndex * rowsPerPage;
            lastShot = Math.Min(firstShot + rowsPerPage, sequence.Shots.Count);
            for (int index = firstShot; index < lastShot; index++)
            {
                CameraSequenceShot shot = sequence.Shots[index];
                int captured = index;
                string cameraName = owner.FindCamera(shot.CameraId)?.Name ?? Lang.Get("vsdirector:missing-camera");
                composer
                    .AddStaticText($"{index + 1:00}  {Ellipsize(cameraName, 19)}", small,
                        ElementBounds.Fixed(10, y + 5, 170, 22))
                    .AddNumberInput(
                        ElementBounds.Fixed(184, y, 74, 28),
                        AutoSaveOnChanged(AutoSaveTarget.Sequence),
                        DirectorFont.TextInput(),
                        "shot-" + index,
                        AutoSaveOnFocusLost(AutoSaveTarget.Sequence))
                    .AddDirectorIconButton(DirectorIcon.Previous, Lang.Get("vsdirector:previous"),
                        () => MoveSequenceShot(captured, -1), ElementBounds.Fixed(264, y, 28, 28),
                        EnumButtonStyle.Small, "up-" + index)
                    .AddDirectorIconButton(DirectorIcon.Next, Lang.Get("vsdirector:next"),
                        () => MoveSequenceShot(captured, 1), ElementBounds.Fixed(298, y, 28, 28),
                        EnumButtonStyle.Small, "down-" + index)
                    .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete"),
                        () => DeleteSequenceShot(captured), ElementBounds.Fixed(332, y, 28, 28),
                        EnumButtonStyle.Small, "delete-" + index);
                y += 34;
            }
            if (sequencePages > 1)
            {
                composer
                    .AddDirectorIconButton(DirectorIcon.Previous, Lang.Get("vsdirector:previous"),
                        () => ChangeSequencePage(-1), ElementBounds.Fixed(10, y, 30, 28),
                        EnumButtonStyle.Small, "sequencePreviousPage")
                    .AddStaticText(Lang.Get("vsdirector:page", sequencePageIndex + 1, sequencePages), detail,
                        ElementBounds.Fixed(46, y + 5, 124, 22))
                    .AddDirectorIconButton(DirectorIcon.Next, Lang.Get("vsdirector:next"),
                        () => ChangeSequencePage(1), ElementBounds.Fixed(176, y, 30, 28),
                        EnumButtonStyle.Small, "sequenceNextPage");
                y += 34;
            }
        }
        else
        {
            AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "sequenceName", sequence.Name, ref y,
                AutoSaveTarget.Sequence);
            string[] loopCodes = { "once", "loop" };
            string[] loopNames =
            {
                Lang.Get("vsdirector:sequence-loop-once"),
                Lang.Get("vsdirector:sequence-loop-loop")
            };
            composer
                .AddStaticText(Lang.Get("vsdirector:loop-mode"), small,
                    ElementBounds.Fixed(10, y + 6, 90, 22))
                .AddDropDown(loopCodes, loopNames, sequence.LoopMode == CameraSequenceLoopMode.Loop ? 1 : 0,
                    OnSequenceLoopChanged, ElementBounds.Fixed(105, y, 150, 28), "sequenceLoop")
                .AddStaticText(Lang.Get("vsdirector:sequence-summary", sequence.Shots.Count,
                    sequence.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)), detail,
                    ElementBounds.Fixed(270, y + 5, Math.Max(120d, panelWidth - 280d), 22));
            y += 36;
        }

        composer.AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:play-sequence"), PlaySequenceEditor,
            ElementBounds.Fixed(10, y + 4, 30, 28), EnumButtonStyle.Small, "playSequence");
        if (inspectorSection == "basic")
        {
            composer.AddDirectorIconButton(DirectorIcon.Delete,
                DeleteLabel(sequence.Id, "vsdirector:delete-sequence"), DeleteSequenceEditor,
                ElementBounds.Fixed(46, y + 4, 30, 28), EnumButtonStyle.Small, "deleteSequence");
        }
        composer.GetNumberInput("shotDuration")?.SetValue(
            DirectorNumberFormatting.Format(CameraSequenceSampler.DefaultShotDurationSeconds));
        for (int index = firstShot; index < lastShot; index++)
        {
            composer.GetNumberInput("shot-" + index)?.SetValue(
                DirectorNumberFormatting.Format(sequence.Shots[index].DurationSeconds));
        }
    }

    private void ComposeSequenceInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        CameraSequence? sequence = sequenceEdit;
        if (sequence is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-sequence"), ref y);
            return;
        }

        AddInspectorHeader(composer, small, sequence.Name, ref y);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "sequenceName", sequence.Name, ref y,
            AutoSaveTarget.Sequence);
        string[] loopCodes = { "once", "loop" };
        string[] loopNames = { Lang.Get("vsdirector:sequence-loop-once"), Lang.Get("vsdirector:sequence-loop-loop") };
        composer
            .AddStaticText(Lang.Get("vsdirector:loop-mode"), small, ElementBounds.Fixed(10, y + 6, 90, 22))
            .AddDropDown(loopCodes, loopNames, sequence.LoopMode == CameraSequenceLoopMode.Loop ? 1 : 0,
                OnSequenceLoopChanged, ElementBounds.Fixed(105, y, 150, 28), "sequenceLoop")
            .AddStaticText(Lang.Get("vsdirector:sequence-camera"), small, ElementBounds.Fixed(270, y + 6, 100, 22));
        IReadOnlyList<CameraPreset> cameras = owner.Cameras;
        string[] cameraValues = cameras.Select(camera => camera.Id.ToString("D")).ToArray();
        string[] cameraNames = cameras.Select(camera => camera.Name).ToArray();
        if (cameras.Count > 0)
        {
            composer.AddDropDown(cameraValues, cameraNames,
                Math.Max(0, Array.IndexOf(cameraValues, SelectedSequenceCameraId().ToString("D"))),
                OnSequenceCameraChanged, ElementBounds.Fixed(372, y, 128, 28), "sequenceCamera");
        }
        y += 36;
        composer
            .AddNumberInput(ElementBounds.Fixed(10, y, 105, 28), null, DirectorFont.TextInput(), "shotDuration")
            .AddSmallButton(Lang.Get("vsdirector:add-shot"), AddSequenceShot,
                ElementBounds.Fixed(121, y, 105, 28), EnumButtonStyle.Normal, "addShot")
            .AddStaticText(Lang.Get("vsdirector:sequence-summary", sequence.Shots.Count,
                sequence.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)), detail,
                ElementBounds.Fixed(240, y + 5, 260, 22));
        y += 36;
        int rowsPerPage = CurrentSequenceRowsPerPage;
        int sequencePages = PageCount(sequence.Shots.Count, rowsPerPage);
        sequencePageIndex = Math.Clamp(sequencePageIndex, 0, sequencePages - 1);
        int firstShot = sequencePageIndex * rowsPerPage;
        int lastShot = Math.Min(firstShot + rowsPerPage, sequence.Shots.Count);
        for (int index = firstShot; index < lastShot; index++)
        {
            CameraSequenceShot shot = sequence.Shots[index];
            int captured = index;
            string cameraName = owner.FindCamera(shot.CameraId)?.Name ?? Lang.Get("vsdirector:missing-camera");
            composer
                .AddStaticText($"{index + 1:00}  {Ellipsize(cameraName, 19)}", small,
                    ElementBounds.Fixed(10, y + 5, 170, 22))
                .AddNumberInput(
                    ElementBounds.Fixed(184, y, 74, 28),
                    AutoSaveOnChanged(AutoSaveTarget.Sequence),
                    DirectorFont.TextInput(),
                    "shot-" + index,
                    AutoSaveOnFocusLost(AutoSaveTarget.Sequence))
                .AddSmallButton("<", () => MoveSequenceShot(captured, -1),
                    ElementBounds.Fixed(264, y, 28, 28), EnumButtonStyle.Small, "up-" + index)
                .AddSmallButton(">", () => MoveSequenceShot(captured, 1),
                    ElementBounds.Fixed(296, y, 28, 28), EnumButtonStyle.Small, "down-" + index)
                .AddSmallButton(Lang.Get("vsdirector:delete"), () => DeleteSequenceShot(captured),
                    ElementBounds.Fixed(328, y, 80, 28), EnumButtonStyle.Small, "delete-" + index);
            y += 34;
        }
        if (sequencePages > 1)
        {
            composer
                .AddSmallButton("<", () => ChangeSequencePage(-1),
                    ElementBounds.Fixed(10, y, 30, 28), EnumButtonStyle.Small, "sequencePreviousPage")
                .AddStaticText(Lang.Get("vsdirector:page", sequencePageIndex + 1, sequencePages), detail,
                    ElementBounds.Fixed(46, y + 5, 124, 22))
                .AddSmallButton(">", () => ChangeSequencePage(1),
                    ElementBounds.Fixed(176, y, 30, 28), EnumButtonStyle.Small, "sequenceNextPage");
            y += 34;
        }
        composer
            .AddSmallButton(Lang.Get("vsdirector:play"), PlaySequenceEditor,
                ElementBounds.Fixed(10, y + 4, 82, 28), EnumButtonStyle.Small, "playSequence")
            .AddSmallButton(DeleteLabel(sequence.Id), DeleteSequenceEditor,
                ElementBounds.Fixed(98, y + 4, 92, 28), EnumButtonStyle.Small, "deleteSequence");
        composer.GetNumberInput("shotDuration")?.SetValue(
            DirectorNumberFormatting.Format(CameraSequenceSampler.DefaultShotDurationSeconds));
        for (int index = firstShot; index < lastShot; index++)
        {
            composer.GetNumberInput("shot-" + index)?.SetValue(
                DirectorNumberFormatting.Format(sequence.Shots[index].DurationSeconds));
        }
    }

    private void ComposeWorkflowInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        DirectorWorkflow? workflow = workflowEdit;
        if (workflow is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-workflow"), ref y);
            return;
        }

        AddInspectorHeader(composer, small, workflow.Name, ref y);
        if (workflow.Type == DirectorWorkflowType.Recording)
        {
            ComposeInspectorSections(composer, ref y, "basic", "card", "audio");
        }
        else
        {
            workflowAudioEditorExpanded = false;
            ComposeInspectorSections(composer, ref y, "basic", "card");
        }
        if (inspectorSection == "audio")
        {
            workflowAudioEditorExpanded = true;
            ComposeWorkflowAudioEditor(composer, small, detail, workflow, ref y);
            return;
        }

        if (inspectorSection == "basic")
        {
            AddLabeledInput(composer, small, Lang.Get("vsdirector:name"),
                "workflowName", workflow.Name, ref y);
            string[] workflowSlotValues = new[] { "none" }
                .Concat(Enumerable.Range(QuickSlots.First, QuickSlots.Count)
                    .Select(slot => slot.ToString(CultureInfo.InvariantCulture)))
                .ToArray();
            string[] workflowSlotNames = new[] { Lang.Get("vsdirector:none") }
                .Concat(Enumerable.Range(QuickSlots.First, QuickSlots.Count)
                    .Select(owner.QuickSlotHotkeyLabel))
                .ToArray();
            composer
                .AddStaticText(Lang.Get("vsdirector:quick-slot"), small,
                    ElementBounds.Fixed(10, y + 6, 100, 22))
                .AddDropDown(workflowSlotValues, workflowSlotNames, workflow.QuickSlot ?? 0,
                    OnWorkflowSlotChanged, ElementBounds.Fixed(114, y, 220, 28), "workflowSlot");
            y += 34;
            double audioSummaryWidth = Math.Max(180d, layout.Panel("inspector").Width - 30d);
            composer.AddStaticText(
                Ellipsize(
                    WorkflowAudioSummary(workflow),
                    Math.Max(24, (int)(audioSummaryWidth / 8d))),
                detail,
                ElementBounds.Fixed(10, y + 2, audioSummaryWidth, 24));
            y += 28;
            composer
                .AddDirectorButton(Lang.Get("vsdirector:workflow-add-card"), NewWorkflowCard,
                    ElementBounds.Fixed(10, y, 100, 30), EnumButtonStyle.Normal, "newWorkflowCard")
                .AddDirectorButton(Lang.Get("vsdirector:workflow-add-countdown"), NewWorkflowCountdown,
                    ElementBounds.Fixed(116, y, 104, 30), EnumButtonStyle.Small, "newWorkflowCountdown")
                .AddDirectorButton(Lang.Get("vsdirector:workflow-audio-editor"), ToggleWorkflowAudioEditor,
                    ElementBounds.Fixed(226, y, 70, 30), EnumButtonStyle.Small,
                    "workflowAudioEditor")
                .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:save"),
                    () => SaveWorkflowEditor(), ElementBounds.Fixed(302, y, 34, 30),
                    EnumButtonStyle.Normal, "saveWorkflow")
                .AddDirectorIconButton(DirectorIcon.Delete, DeleteLabel(workflow.Id),
                    DeleteWorkflowEditor, ElementBounds.Fixed(342, y, 34, 30),
                    EnumButtonStyle.Small, "deleteWorkflow");
            y += 38;
            return;
        }

        DirectorWorkflowCard? card = SelectedWorkflowCard();
        if (card is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workflow-select-card"), ref y);
            return;
        }

        AddInspectorHeader(composer, small, card.Name, ref y);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "workflowCardName", card.Name, ref y,
            AutoSaveTarget.WorkflowCard);
        DirectorWorkflowCardKind[] availableKinds = Enum.GetValues<DirectorWorkflowCardKind>()
            .Where(kind => DirectorWorkflowPlanner.IsCardKindAllowed(workflow.Type, kind))
            .ToArray();
        string[] kindCodes = availableKinds.Select(WorkflowCardKindCode).ToArray();
        string[] kindNames = availableKinds.Select(kind => Lang.Get(kind switch
        {
            DirectorWorkflowCardKind.Countdown => "vsdirector:workflow-card-kind-countdown",
            _ => "vsdirector:workflow-card-kind-shot"
        })).ToArray();
        composer
            .AddStaticText(Lang.Get("vsdirector:workflow-card-kind"), small,
                ElementBounds.Fixed(10, y + 6, 100, 22))
            .AddDropDown(
                kindCodes,
                kindNames,
                Math.Max(0, Array.IndexOf(kindCodes, WorkflowCardKindCode(card.Kind))),
                OnWorkflowCardKindChanged,
                ElementBounds.Fixed(114, y, 286, 28),
                "workflowCardKind")
            .AddSwitch(value =>
                {
                    card.Enabled = value;
                    QueueAutoSave(AutoSaveTarget.WorkflowCard);
                },
                ElementBounds.Fixed(414, y + 2, 25, 25), "workflowCardEnabled", 25, 3)
            .AddStaticText(Lang.Get("vsdirector:enabled"), small,
                ElementBounds.Fixed(445, y + 5, 55, 22));
        y += 34;
        IReadOnlyList<RecordingSourceOption> sources = BuildRecordingSources()
            .Where(source => DirectorWorkflowPlanner.IsExportSource(source.Type))
            .ToArray();
        string selectedSource = ((int)card.SourceType).ToString(CultureInfo.InvariantCulture)
            + "|" + card.SourceId.ToString("D");
        if (card.Kind == DirectorWorkflowCardKind.Shot)
        {
            composer.AddStaticText(Lang.Get("vsdirector:workflow-source"), small,
                ElementBounds.Fixed(10, y + 6, 100, 22));
            if (sources.Count > 0)
            {
                string[] values = sources.Select(source => source.Value).ToArray();
                composer.AddDropDown(
                    values,
                    sources.Select(source => source.Label).ToArray(),
                    Math.Max(0, Array.IndexOf(values, selectedSource)),
                    OnWorkflowSourceChanged,
                    ElementBounds.Fixed(114, y, 386, 28),
                    "workflowSource");
            }
            y += 34;
        }
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:workflow-start"),
            "workflowStart", card.StartSeconds, ref y,
            autoSaveTarget: AutoSaveTarget.WorkflowCard);
        if (card.Kind == DirectorWorkflowCardKind.Countdown)
        {
            AddLabeledNumber(composer, small, Lang.Get("vsdirector:workflow-countdown-duration"),
                "workflowCountdownDuration", card.DurationSeconds, ref y,
                autoSaveTarget: AutoSaveTarget.WorkflowCard);
        }
        else
        {
            AddLabeledNumber(composer, small, Lang.Get("vsdirector:workflow-end"),
                "workflowEnd", card.EndSeconds, ref y,
                autoSaveTarget: AutoSaveTarget.WorkflowCard);
        }
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:workflow-lane"),
            "workflowLane", card.Lane, ref y,
            autoSaveTarget: AutoSaveTarget.WorkflowCard);
        if (card.Kind == DirectorWorkflowCardKind.Shot)
        {
            string[] transitionCodes = { "auto", "cut", "cross", "fade" };
            string[] transitionNames =
            {
                Lang.Get("vsdirector:workflow-transition-auto"),
                Lang.Get("vsdirector:workflow-transition-cut"),
                Lang.Get("vsdirector:workflow-transition-cross"),
                Lang.Get("vsdirector:workflow-transition-fade")
            };
            string selectedTransition = WorkflowTransitionCode(card);
            composer
                .AddStaticText(Lang.Get("vsdirector:workflow-transition"), small,
                    ElementBounds.Fixed(10, y + 6, 100, 22))
                .AddDropDown(
                    transitionCodes,
                    transitionNames,
                    Math.Max(0, Array.IndexOf(transitionCodes, selectedTransition)),
                    OnWorkflowTransitionChanged,
                    ElementBounds.Fixed(114, y, 386, 28),
                    "workflowTransition");
            y += 34;
            AddLabeledNumber(composer, small, Lang.Get("vsdirector:workflow-transition-duration"),
                "workflowTransitionDuration", WorkflowTransitionDuration(card), ref y,
                autoSaveTarget: AutoSaveTarget.WorkflowCard);
        }
        composer
            .AddStaticText(Lang.Get("vsdirector:description"), small,
                ElementBounds.Fixed(10, y + 6, 100, 22))
            .AddTextArea(
                ElementBounds.Fixed(114, y, 386, 58),
                AutoSaveOnChanged(AutoSaveTarget.WorkflowCard),
                DirectorFont.TextInput(),
                "workflowNotes",
                AutoSaveOnFocusLost(AutoSaveTarget.WorkflowCard));
        y += 66;
        composer
            .AddDirectorButton(Lang.Get("vsdirector:workflow-duplicate-card"), DuplicateWorkflowCard,
                ElementBounds.Fixed(10, y, 118, 30), EnumButtonStyle.Small, "duplicateWorkflowCard")
            .AddDirectorButton(Lang.Get("vsdirector:delete"), DeleteWorkflowCard,
                ElementBounds.Fixed(134, y, 90, 30), EnumButtonStyle.Small, "deleteWorkflowCard");
        y += 38;
        IReadOnlyList<DirectorWorkflowExportPass> passes = owner.PlanWorkflow(workflow.Id);
        composer.AddDynamicText(
            WorkflowPlanSummary(workflow, passes),
            detail,
            ElementBounds.Fixed(10, y, 490, 74),
            "workflowSummary");
    }

    private void ComposeWorkflowAudioEditor(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        DirectorWorkflow workflow,
        ref int y)
    {
        SyncWorkflowAudioSelection();
        IReadOnlyList<DirectorTimelineAudioClip> clips = workflow.AudioClips;
        composer
            .AddDirectorButton(Lang.Get("vsdirector:workflow-audio-new"), NewWorkflowAudioClip,
                ElementBounds.Fixed(10, y, 104, 28), EnumButtonStyle.Normal, "newWorkflowAudio");
        if (clips.Count > 0)
        {
            string[] values = clips.Select(clip => clip.Id.ToString("D")).ToArray();
            string[] names = clips.Select(clip => Ellipsize(
                $"A{clip.Track}  {clip.TimelineStartSeconds:0.###}s  {clip.Name}", 42)).ToArray();
            composer.AddDropDown(values, names,
                Math.Max(0, Array.IndexOf(values, selectedWorkflowAudioClipId.ToString("D"))),
                OnWorkflowAudioSelectionChanged,
                ElementBounds.Fixed(122, y, 378, 28), "workflowAudioSelection");
        }
        y += 36;

        AddLabeledInput(composer, small, Lang.Get("vsdirector:workflow-audio-path"),
            "workflowAudioPath", workflowAudioPath, ref y);
        composer
            .AddStaticText(Lang.Get("vsdirector:workflow-audio-timeline-start"), small,
                ElementBounds.Fixed(10, y + 6, 82, 22))
            .AddNumberInput(ElementBounds.Fixed(94, y, 68, 28), null,
                DirectorFont.TextInput(), "workflowAudioTimelineStart")
            .AddStaticText(Lang.Get("vsdirector:workflow-audio-source-start"), small,
                ElementBounds.Fixed(170, y + 6, 76, 22))
            .AddNumberInput(ElementBounds.Fixed(248, y, 68, 28), null,
                DirectorFont.TextInput(), "workflowAudioSourceStart")
            .AddStaticText(Lang.Get("vsdirector:workflow-audio-duration"), small,
                ElementBounds.Fixed(324, y + 6, 62, 22))
            .AddNumberInput(ElementBounds.Fixed(388, y, 74, 28), null,
                DirectorFont.TextInput(), "workflowAudioDuration");
        y += 34;
        composer
            .AddStaticText(Lang.Get("vsdirector:workflow-audio-track"), small,
                ElementBounds.Fixed(10, y + 6, 54, 22))
            .AddNumberInput(ElementBounds.Fixed(66, y, 56, 28), null,
                DirectorFont.TextInput(), "workflowAudioTrack")
            .AddStaticText(Lang.Get("vsdirector:workflow-audio-gain"), small,
                ElementBounds.Fixed(132, y + 6, 64, 22))
            .AddNumberInput(ElementBounds.Fixed(198, y, 64, 28), null,
                DirectorFont.TextInput(), "workflowAudioGain")
            .AddStaticText(Lang.Get("vsdirector:workflow-audio-rate"), small,
                ElementBounds.Fixed(272, y + 6, 64, 22))
            .AddNumberInput(ElementBounds.Fixed(338, y, 64, 28), null,
                DirectorFont.TextInput(), "workflowAudioRate")
            .AddSwitch(OnWorkflowAudioEnabledChanged,
                ElementBounds.Fixed(416, y + 2, 25, 25), "workflowAudioEnabled", 25, 3)
            .AddStaticText(Lang.Get("vsdirector:enabled"), small,
                ElementBounds.Fixed(447, y + 5, 52, 22));
        y += 34;
        composer
            .AddStaticText(Lang.Get("vsdirector:workflow-audio-fade-in"), small,
                ElementBounds.Fixed(10, y + 6, 72, 22))
            .AddNumberInput(ElementBounds.Fixed(84, y, 72, 28), null,
                DirectorFont.TextInput(), "workflowAudioFadeIn")
            .AddStaticText(Lang.Get("vsdirector:workflow-audio-fade-out"), small,
                ElementBounds.Fixed(166, y + 6, 72, 22))
            .AddNumberInput(ElementBounds.Fixed(240, y, 72, 28), null,
                DirectorFont.TextInput(), "workflowAudioFadeOut");
        y += 34;
        composer
            .AddDirectorButton(Lang.Get("vsdirector:workflow-audio-apply"), ApplyWorkflowAudioClip,
                ElementBounds.Fixed(10, y, 116, 30), EnumButtonStyle.Normal, "applyWorkflowAudio")
            .AddDirectorButton(Lang.Get("vsdirector:delete"), DeleteWorkflowAudioClip,
                ElementBounds.Fixed(132, y, 88, 30), EnumButtonStyle.Small, "deleteWorkflowAudio");
        y += 38;
    }

    private void ComposeCategorizedFollowInspector(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        CameraFollowRig? rig = followEdit;
        if (rig is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-follow"), ref y);
            return;
        }

        double panelWidth = layout.Panel("inspector").Width;
        string viewLabel = Lang.Get("vsdirector:replay-right-view");
        string propertiesLabel = Lang.Get("vsdirector:replay-right-inspector");
        double viewWidth = ToolbarButtonWidth(viewLabel);
        double propertiesWidth = ToolbarButtonWidth(propertiesLabel);
        composer
            .AddDirectorToolbarButton(viewLabel,
                () => SetFollowRightTab("visuals"),
                ElementBounds.Fixed(0d, y, viewWidth, 28d),
                followRightTab == "visuals" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "followRightVisuals")
            .AddDirectorToolbarButton(propertiesLabel,
                () => SetFollowRightTab("properties"),
                ElementBounds.Fixed(viewWidth, y, propertiesWidth, 28d),
                followRightTab == "properties" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "followRightProperties");
        y += 30;

        if (followRightTab == "properties")
        {
            AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "followName", rig.Name, ref y,
                AutoSaveTarget.Follow);
            AddLabeledInput(composer, small, Lang.Get("vsdirector:tags"), "followTags",
                string.Join(", ", rig.Tags), ref y, AutoSaveTarget.Follow);
            AddLabeledInput(composer, small, Lang.Get("vsdirector:description"), "followDescription",
                rig.Description, ref y, AutoSaveTarget.Follow);
            return;
        }

        IReadOnlyList<DirectorFollowTarget> targets = owner.GetFollowTargets();
        string[] targetValues = targets.Select(target => target.Selector).ToArray();
        string[] targetNames = targets.Select(target => target.DisplayName).ToArray();
        if (targets.Count > 0)
        {
            composer
                .AddStaticText(Lang.Get("vsdirector:follow-target"), small,
                    ElementBounds.Fixed(10, y + 6, 95, 22))
                .AddDropDown(targetValues, targetNames,
                    Math.Max(0, Array.IndexOf(targetValues, FollowTargetSelector(rig))),
                    OnFollowTargetChanged,
                    ElementBounds.Fixed(110, y, Math.Clamp(panelWidth - 120d, 160d, 420d), 28),
                    "followTarget");
            y += 36;
        }

        AddFollowDropDowns(composer, small, rig, ref y);
        string[] followSlotValues = new[] { "none" }
            .Concat(Enumerable.Range(QuickSlots.First, QuickSlots.Count)
                .Select(slot => slot.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        string[] followSlotNames = new[] { Lang.Get("vsdirector:none") }
            .Concat(Enumerable.Range(QuickSlots.First, QuickSlots.Count)
                .Select(owner.QuickSlotHotkeyLabel))
            .ToArray();
        composer
            .AddStaticText(Lang.Get("vsdirector:quick-slot"), small,
                ElementBounds.Fixed(10, y + 6, 100, 22))
            .AddDropDown(followSlotValues, followSlotNames, rig.QuickSlot ?? 0,
                OnFollowSlotChanged,
                ElementBounds.Fixed(114, y, Math.Clamp(panelWidth - 124d, 120d, 190d), 28),
                "followSlot");
        y += 34;

        if (panelWidth >= 430d)
        {
            composer
                .AddCustomRender(ElementBounds.Fixed(10, y, 180, 88), RenderFollowRigPreview)
                .AddStaticText(Lang.Get("vsdirector:follow-orbit-angle"), small,
                    ElementBounds.Fixed(202, y, 82, 24))
                .AddDirectorSlider(OnFollowOrbitSlider,
                    ElementBounds.Fixed(288, y, panelWidth - 298d, 24), "followOrbitVisual")
                .AddStaticText(Lang.Get("vsdirector:follow-distance"), small,
                    ElementBounds.Fixed(202, y + 30, 82, 24))
                .AddDirectorSlider(OnFollowDistanceSlider,
                    ElementBounds.Fixed(288, y + 30, panelWidth - 298d, 24), "followDistanceVisual")
                .AddStaticText(Lang.Get("vsdirector:follow-height"), small,
                    ElementBounds.Fixed(202, y + 60, 82, 24))
                .AddDirectorSlider(OnFollowHeightSlider,
                    ElementBounds.Fixed(288, y + 60, panelWidth - 298d, 24), "followHeightVisual");
            y += 96;
        }
        else
        {
            double labelWidth = Math.Clamp(panelWidth * 0.35d, 100d, 150d);
            double sliderX = 16d + labelWidth;
            double sliderWidth = Math.Max(80d, panelWidth - sliderX - 12d);
            composer.AddCustomRender(
                ElementBounds.Fixed(10, y, Math.Max(180d, panelWidth - 20d), 88),
                RenderFollowRigPreview);
            y += 94;
            composer
                .AddStaticText(Lang.Get("vsdirector:follow-orbit-angle"), small,
                    ElementBounds.Fixed(10, y, labelWidth, 24))
                .AddDirectorSlider(OnFollowOrbitSlider,
                    ElementBounds.Fixed(sliderX, y, sliderWidth, 24), "followOrbitVisual")
                .AddStaticText(Lang.Get("vsdirector:follow-distance"), small,
                    ElementBounds.Fixed(10, y + 30, labelWidth, 24))
                .AddDirectorSlider(OnFollowDistanceSlider,
                    ElementBounds.Fixed(sliderX, y + 30, sliderWidth, 24), "followDistanceVisual")
                .AddStaticText(Lang.Get("vsdirector:follow-height"), small,
                    ElementBounds.Fixed(10, y + 60, labelWidth, 24))
                .AddDirectorSlider(OnFollowHeightSlider,
                    ElementBounds.Fixed(sliderX, y + 60, sliderWidth, 24), "followHeightVisual");
            y += 90;
        }
        AddCompactNumberRow(composer, small, ref y, OnFollowPropertyInputChanged,
            AutoSaveTarget.Follow,
            (Lang.Get("vsdirector:follow-offset-x"), "followOffsetX"),
            (Lang.Get("vsdirector:follow-offset-y"), "followOffsetY"),
            (Lang.Get("vsdirector:follow-offset-z"), "followOffsetZ"));
        AddCompactNumberRow(composer, small, ref y, OnFollowPropertyInputChanged,
            AutoSaveTarget.Follow,
            (Lang.Get("vsdirector:follow-fov"), "followFov"),
            (Lang.Get("vsdirector:follow-orbit-speed"), "followOrbitSpeed"),
            (Lang.Get("vsdirector:follow-smooth-time"), "followSmooth"),
            (Lang.Get("vsdirector:follow-delay"), "followDelay"));

        composer
            .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:activate"), ActivateFollowEditor,
                ElementBounds.Fixed(10, y, 30, 28), EnumButtonStyle.Small, "activateFollow")
            .AddDirectorIconButton(DirectorIcon.Delete, DeleteLabel(rig.Id), DeleteFollowEditor,
                ElementBounds.Fixed(46, y, 30, 28), EnumButtonStyle.Small, "deleteFollow");
        y += 36;
    }

    private bool SetFollowRightTab(string tab)
    {
        string nextTab = tab == "properties" ? "properties" : "visuals";
        if (followRightTab == nextTab)
        {
            return true;
        }

        FlushPendingAutoSave(AutoSaveTarget.Follow);
        followRightTab = nextTab;
        ComposeInspector();
        return true;
    }

    private void ComposeFollowInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        CameraFollowRig? rig = followEdit;
        if (rig is null)
        {
            AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:workspace-select-follow"), ref y);
            return;
        }

        AddInspectorHeader(composer, small, rig.Name, ref y);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:name"), "followName", rig.Name, ref y,
            AutoSaveTarget.Follow);
        IReadOnlyList<DirectorFollowTarget> targets = owner.GetFollowTargets();
        string[] targetValues = targets.Select(target => target.Selector).ToArray();
        string[] targetNames = targets.Select(target => target.DisplayName).ToArray();
        if (targets.Count > 0)
        {
            composer
                .AddStaticText(Lang.Get("vsdirector:follow-target"), small, ElementBounds.Fixed(10, y + 6, 95, 22))
                .AddDropDown(targetValues, targetNames,
                    Math.Max(0, Array.IndexOf(targetValues, FollowTargetSelector(rig))),
                    OnFollowTargetChanged, ElementBounds.Fixed(110, y, 390, 28), "followTarget");
            y += 36;
        }

        AddFollowDropDowns(composer, small, rig, ref y);
        string[] followSlotValues = new[] { "none" }
            .Concat(Enumerable.Range(QuickSlots.First, QuickSlots.Count)
                .Select(slot => slot.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        string[] followSlotNames = new[] { Lang.Get("vsdirector:none") }
            .Concat(Enumerable.Range(QuickSlots.First, QuickSlots.Count)
                .Select(owner.QuickSlotHotkeyLabel))
            .ToArray();
        composer
            .AddStaticText(Lang.Get("vsdirector:quick-slot"), small,
                ElementBounds.Fixed(10, y + 6, 100, 22))
            .AddDropDown(followSlotValues, followSlotNames, rig.QuickSlot ?? 0,
                OnFollowSlotChanged, ElementBounds.Fixed(114, y, 190, 28), "followSlot");
        y += 34;
        composer
            .AddCustomRender(ElementBounds.Fixed(10, y, 180, 88), RenderFollowRigPreview)
            .AddStaticText(Lang.Get("vsdirector:follow-orbit-angle"), small,
                ElementBounds.Fixed(202, y, 82, 24))
            .AddDirectorSlider(OnFollowOrbitSlider,
                ElementBounds.Fixed(288, y, 212, 24), "followOrbitVisual")
            .AddStaticText(Lang.Get("vsdirector:follow-distance"), small,
                ElementBounds.Fixed(202, y + 30, 82, 24))
            .AddDirectorSlider(OnFollowDistanceSlider,
                ElementBounds.Fixed(288, y + 30, 212, 24), "followDistanceVisual")
            .AddStaticText(Lang.Get("vsdirector:follow-height"), small,
                ElementBounds.Fixed(202, y + 60, 82, 24))
            .AddDirectorSlider(OnFollowHeightSlider,
                ElementBounds.Fixed(288, y + 60, 212, 24), "followHeightVisual");
        y += 96;
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:follow-offset-x"), "followOffsetX", rig.OffsetX, ref y, OnFollowPropertyInputChanged, AutoSaveTarget.Follow);
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:follow-offset-y"), "followOffsetY", rig.OffsetY, ref y, OnFollowPropertyInputChanged, AutoSaveTarget.Follow);
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:follow-offset-z"), "followOffsetZ", rig.OffsetZ, ref y, OnFollowPropertyInputChanged, AutoSaveTarget.Follow);
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:follow-fov"), "followFov", rig.Fov, ref y, OnFollowPropertyInputChanged, AutoSaveTarget.Follow);
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:follow-orbit-speed"), "followOrbitSpeed", rig.OrbitSpeedDegreesPerSecond, ref y, OnFollowPropertyInputChanged, AutoSaveTarget.Follow);
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:follow-smooth-time"), "followSmooth", rig.SmoothTimeSeconds, ref y, OnFollowPropertyInputChanged, AutoSaveTarget.Follow);
        AddLabeledNumber(composer, small, Lang.Get("vsdirector:follow-delay"), "followDelay", rig.DelaySeconds, ref y, OnFollowPropertyInputChanged, AutoSaveTarget.Follow);
        composer
            .AddSmallButton(Lang.Get("vsdirector:activate"), ActivateFollowEditor,
                ElementBounds.Fixed(10, y, 92, 28), EnumButtonStyle.Small, "activateFollow")
            .AddSmallButton(DeleteLabel(rig.Id), DeleteFollowEditor,
                ElementBounds.Fixed(108, y, 92, 28), EnumButtonStyle.Small, "deleteFollow");
        y += 36;
        composer.AddDynamicText(owner.FollowVisualStatusText(), detail,
            ElementBounds.Fixed(10, y, 490, 48), "followVisualStatus");
    }

    private void ComposeMergedReplayInspector(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        DirectorWorkspacePanelLayout panel = layout.Panel("inspector");
        ReplaySplitLayout split = CalculateReplaySplit(
            y,
            panel.Height - 8d,
            layout.ReplayInspectorSplitRatio);
        replayInspectorTopBounds = ElementBounds.Fixed(4d, split.TopY, panel.Width - 8d, split.TopHeight);
        replayInspectorViewBounds = ElementBounds.Fixed(4d, split.BottomY, panel.Width - 8d, split.BottomHeight);
        ElementBounds divider = ElementBounds.Fixed(
            10d,
            split.DividerY,
            panel.Width - 20d,
            ReplaySplitDividerHeight);
        replayInspectorSplitBounds = divider;
        composer
            .AddCustomRender(replayInspectorTopBounds, RenderReplayBlockBackground)
            .AddCustomRender(replayInspectorViewBounds, RenderReplayBlockBackground)
            .AddCustomRender(divider, RenderReplaySplitDivider);
        AddReplayBlockHeader(
            composer,
            small,
            Lang.Get("vsdirector:replay-block-properties"),
            panel.Width,
            split.TopY);
        int replayContentY = (int)Math.Ceiling(split.TopContentY + 4d);
        string previousReplaySection = inspectorSection;
        InspectorSectionTarget previousReplayTarget = composingInspectorSectionTarget;
        composingInspectorSectionTarget = InspectorSectionTarget.Replay;
        inspectorSection = replayInspectorSection;
        replayInspectorBlockBottom = split.TopEnd - 4d;
        ComposeCategorizedReplayInspector(composer, small, detail, ref replayContentY);
        replayInspectorSection = inspectorSection;
        inspectorSection = previousReplaySection;
        composingInspectorSectionTarget = previousReplayTarget;

        AddReplayBlockHeader(
            composer,
            small,
            ReplayViewInspectorBlockTitle(),
            panel.Width,
            split.BottomY);
        int viewContentY = (int)Math.Ceiling(split.BottomContentY + 4d);
        string previousViewSection = inspectorSection;
        InspectorSectionTarget previousViewTarget = composingInspectorSectionTarget;
        composingInspectorSectionTarget = InspectorSectionTarget.ReplayView;
        inspectorSection = replayViewInspectorSection;
        replayInspectorBlockBottom = split.BottomEnd - 4d;
        replaySequenceRowsPerPage = Math.Clamp(
            (int)Math.Floor((replayInspectorBlockBottom - viewContentY - 178d) / 34d),
            1,
            SequenceRowsPerPage);
        switch (replayViewMode)
        {
            case "camera":
                ComposeCameraInspector(composer, small, detail, ref viewContentY);
                break;
            case "path":
                ComposeCategorizedPathInspector(composer, small, detail, ref viewContentY);
                break;
            case "sequence":
                ComposeCategorizedSequenceInspector(composer, small, detail, ref viewContentY);
                break;
            case "follow":
                ComposeCategorizedFollowInspector(composer, small, detail, ref viewContentY);
                break;
        }
        replayViewInspectorSection = inspectorSection;
        inspectorSection = previousViewSection;
        composingInspectorSectionTarget = previousViewTarget;
        replayInspectorBlockBottom = double.PositiveInfinity;
        y = (int)Math.Ceiling(split.BottomEnd);
    }

    private void ComposeCategorizedReplayInspector(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        DirectorReplayInfo? selected = SelectedReplayInfo();
        double panelWidth = layout.Panel("inspector").Width;
        AddInspectorHeader(composer, small,
            selected?.Name ?? owner.ActiveReplayName ?? Lang.Get("vsdirector:inactive"), ref y);
        List<string> replaySections = new() { "info", "timeline" };
        if (CanManageChunkPreload)
        {
            replaySections.Add("preload");
        }
        replaySections.Add("actors");
        ComposeInspectorSections(composer, ref y, replaySections.ToArray());
        if (inspectorSection == "timeline")
        {
            ComposeReplayTimelineEditor(composer, small, detail, ref y);
            return;
        }
        if (inspectorSection == "preload")
        {
            if (PreloadEditorReplayInfo() is null)
            {
                AddInspectorEmpty(composer, detail, Lang.Get("vsdirector:replay-list-empty"), ref y);
            }
            else
            {
                ComposePreloadEditorEntry(composer, ref y);
            }
            return;
        }
        if (inspectorSection == "actors")
        {
            ComposeReplayActorControls(composer, small, detail, ref y);
            return;
        }

        if (selected is not null)
        {
            ComposeReplayMetadataFields(composer, small, selected, ref y);
            composer.AddStaticText(
                Lang.Get("vsdirector:replay-row-detail",
                    selected.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                    selected.ActorCount,
                    selected.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)),
                detail,
                ElementBounds.Fixed(10, y, Math.Max(180d, panelWidth - 20d), 30));
            y += 38;
            composer
                .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:save"),
                    SaveReplayMetadata,
                    ElementBounds.Fixed(10, y, 30, 28), EnumButtonStyle.Normal, "saveReplayMetadata")
                .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:play"),
                    () => owner.TryPlayReplay(selected.Id.ToString("D"), out _, pauseAfterLoad: true),
                    ElementBounds.Fixed(46, y, 30, 28), EnumButtonStyle.Normal, "playReplay")
                .AddDirectorIconButton(DirectorIcon.Stop, Lang.Get("vsdirector:replay-exit"),
                    StopReplay, ElementBounds.Fixed(82, y, 30, 28), EnumButtonStyle.Small, "stopReplay")
                .AddDirectorIconButton(DirectorIcon.Delete, DeleteLabel(selected.Id),
                    DeleteSelectedReplay, ElementBounds.Fixed(118, y, 30, 28),
                    EnumButtonStyle.Small, "deleteReplay");
            y += 36;
        }
        composer.AddDynamicText(ReplayLoadingText(), detail,
            ElementBounds.Fixed(10, y, Math.Max(180d, panelWidth - 20d), 52), "replayLoading");
        y += 58;
        double boundaryBottom = Math.Min(
            layout.Panel("inspector").Height - 10d,
            replayInspectorBlockBottom);
        int boundaryHeight = (int)Math.Floor(boundaryBottom - y);
        if (boundaryHeight >= 40)
        {
            composer.AddRichtext(ToVtml(Lang.Get("vsdirector:replay-capture-boundary")), detail,
                ElementBounds.Fixed(10, y, Math.Max(180d, panelWidth - 20d), boundaryHeight),
                "replayBoundary");
        }
    }

    private void ComposeReplayInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        DirectorReplayInfo? selected = SelectedReplayInfo();
        AddInspectorHeader(composer, small, selected?.Name ?? owner.ActiveReplayName ?? Lang.Get("vsdirector:inactive"), ref y);
        if (selected is not null)
        {
            ComposeReplayMetadataFields(composer, small, selected, ref y);
            composer.AddStaticText(
                Lang.Get("vsdirector:replay-row-detail", selected.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture), selected.ActorCount,
                    selected.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)),
                detail,
                ElementBounds.Fixed(10, y, 390, 30));
            y += 38;
            composer
                .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:save"),
                    SaveReplayMetadata, ElementBounds.Fixed(10, y, 30, 28),
                    EnumButtonStyle.Normal, "saveReplayMetadata")
                .AddDirectorIconButton(DirectorIcon.Play, Lang.Get("vsdirector:play"),
                    () => owner.TryPlayReplay(selected.Id.ToString("D"), out _, pauseAfterLoad: true),
                    ElementBounds.Fixed(46, y, 30, 28), EnumButtonStyle.Normal, "playReplay")
                .AddDirectorIconButton(DirectorIcon.Stop, Lang.Get("vsdirector:replay-exit"),
                    StopReplay, ElementBounds.Fixed(82, y, 30, 28), EnumButtonStyle.Small, "stopReplay")
                .AddDirectorIconButton(DirectorIcon.Delete, DeleteLabel(selected.Id),
                    DeleteSelectedReplay, ElementBounds.Fixed(118, y, 30, 28),
                    EnumButtonStyle.Small, "deleteReplay");
            y += 36;
        }

        composer.AddDynamicText(ReplayLoadingText(), detail,
            ElementBounds.Fixed(10, y, 390, 52), "replayLoading");
        y += 58;
        ComposeReplayTimelineEditor(composer, small, detail, ref y);
        if (PreloadEditorReplayInfo() is not null)
        {
            ComposePreloadEditorEntry(composer, ref y);
        }
        ComposeReplayActorControls(composer, small, detail, ref y);
        int boundaryHeight = (int)Math.Floor(layout.Panel("inspector").Height - y - 10d);
        if (boundaryHeight >= 40)
        {
            composer.AddRichtext(ToVtml(Lang.Get("vsdirector:replay-capture-boundary")), detail,
                ElementBounds.Fixed(10, y, 390, boundaryHeight),
                "replayBoundary");
        }
    }

    private void ComposeReplayMetadataFields(
        GuiComposer composer,
        CairoFont small,
        DirectorReplayInfo replay,
        ref int y,
        AutoSaveTarget autoSaveTarget = AutoSaveTarget.None)
    {
        SyncReplayMetadataFields(replay);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:name"),
            "replayMetadataName", replayMetadataName, ref y, autoSaveTarget);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:tags"),
            "replayMetadataTags", replayMetadataTags, ref y, autoSaveTarget);
        AddLabeledInput(composer, small, Lang.Get("vsdirector:description"),
            "replayMetadataDescription", replayMetadataDescription, ref y, autoSaveTarget);
    }

    private void SyncReplayMetadataFields(DirectorReplayInfo replay)
    {
        if (replayMetadataFieldsId == replay.Id)
        {
            return;
        }

        replayMetadataFieldsId = replay.Id;
        replayMetadataName = replay.Name;
        replayMetadataTags = string.Join(", ", replay.Tags);
        replayMetadataDescription = replay.Description;
    }

    private void ComposeReplayTimelineEditor(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        SyncReplayTimelineSelection();
        composer.AddStaticText(Lang.Get("vsdirector:replay-timeline-editor"), small,
            ElementBounds.Fixed(10, y + 4, 490, 22));
        y += 28;
        composer.AddStaticText(Lang.Get("vsdirector:replay-base-speed"), small,
            ElementBounds.Fixed(10, y + 6, 118, 22));
        ReplayPropertyRange baseSpeedRange = new(
            DirectorReplayTimeline.MinSpeed,
            DirectorReplayTimeline.MaxSpeed,
            100d,
            1,
            "0.00");
        composer.AddDirectorSlider(
            value =>
            {
                replayBaseSpeedText = baseSpeedRange.FromSlider(value)
                    .ToString(baseSpeedRange.Format, CultureInfo.InvariantCulture);
                return true;
            },
            ElementBounds.Fixed(132, y, 88, 28),
            "replayBaseSpeed");
        GuiElementDirectorSlider? baseSpeedSlider = composer.GetDirectorSlider("replayBaseSpeed");
        baseSpeedSlider?.SetValues(
            baseSpeedRange.ToSlider(ParseDouble(replayBaseSpeedText, owner.ReplayBasePlaybackSpeed)),
            baseSpeedRange.Minimum,
            baseSpeedRange.Maximum,
            baseSpeedRange.Step);
        if (baseSpeedSlider is not null)
        {
            baseSpeedSlider.OnSliderTooltip = value => baseSpeedRange.FromSlider(value)
                .ToString(baseSpeedRange.Format, CultureInfo.InvariantCulture);
        }
        composer.AddDirectorButton(Lang.Get("vsdirector:apply"), ApplyReplayBaseSpeed,
            ElementBounds.Fixed(228, y, 82, 28), EnumButtonStyle.Small, "applyReplayBaseSpeed");
        y += 34;

        IReadOnlyList<DirectorReplayMarker> markers = owner.ReplayMarkers;
        if (selectedReplayMarkerId != Guid.Empty
            && markers.Any(marker => marker.Id == selectedReplayMarkerId))
        {
            composer
                .AddTextInput(ElementBounds.Fixed(10, y, 236, 28), null,
                    DirectorFont.TextInput(), "replayMarkerLabel")
                .AddNumberInput(ElementBounds.Fixed(252, y, 88, 28), null,
                    DirectorFont.TextInput(), "replayMarkerTime")
                .AddDirectorIconButton(DirectorIcon.Select, Lang.Get("vsdirector:apply"),
                    ApplySelectedReplayMarker, ElementBounds.Fixed(348, y, 30, 28),
                    EnumButtonStyle.Normal, "applyReplayMarker")
                .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete"),
                    DeleteSelectedReplayMarker, ElementBounds.Fixed(384, y, 30, 28),
                    EnumButtonStyle.Small, "deleteReplayMarker");
            y += 36;
        }

        IReadOnlyList<DirectorReplaySpeedKeyframe> speedKeys = owner.ReplaySpeedKeyframes;
        if (selectedReplaySpeedKeyframeId != Guid.Empty
            && speedKeys.Any(keyframe => keyframe.Id == selectedReplaySpeedKeyframeId))
        {
            string[] interpolationCodes = { "hold", "linear", "ease" };
            string[] interpolationNames =
            {
                Lang.Get("vsdirector:replay-speed-interpolation-hold"),
                Lang.Get("vsdirector:replay-speed-interpolation-linear"),
                Lang.Get("vsdirector:replay-speed-interpolation-ease")
            };
            composer.AddNumberInput(ElementBounds.Fixed(10, y, 88, 28), null,
                DirectorFont.TextInput(), "replaySpeedTime");
            ReplayPropertyRange speedRange = new(
                DirectorReplayTimeline.MinSpeed,
                DirectorReplayTimeline.MaxSpeed,
                100d,
                1,
                "0.00");
            composer.AddDirectorSlider(
                    value =>
                    {
                        replaySpeedValueText = speedRange.FromSlider(value)
                            .ToString(speedRange.Format, CultureInfo.InvariantCulture);
                        return true;
                    },
                    ElementBounds.Fixed(104, y, 78, 28),
                    "replaySpeedValue");
            GuiElementDirectorSlider? speedSlider = composer.GetDirectorSlider("replaySpeedValue");
            speedSlider?.SetValues(
                speedRange.ToSlider(ParseDouble(replaySpeedValueText, 1d)),
                speedRange.Minimum,
                speedRange.Maximum,
                speedRange.Step);
            if (speedSlider is not null)
            {
                speedSlider.OnSliderTooltip = value => speedRange.FromSlider(value)
                    .ToString(speedRange.Format, CultureInfo.InvariantCulture);
            }
            composer.AddDropDown(interpolationCodes, interpolationNames,
                    Math.Max(0, Array.IndexOf(interpolationCodes, replaySpeedInterpolationCode)),
                    OnReplaySpeedInterpolationChanged,
                    ElementBounds.Fixed(188, y, 154, 28), "replaySpeedInterpolation")
                .AddDirectorIconButton(DirectorIcon.Select, Lang.Get("vsdirector:apply"),
                    ApplySelectedReplaySpeedKeyframe, ElementBounds.Fixed(348, y, 30, 28),
                    EnumButtonStyle.Normal, "applyReplaySpeed")
                .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete"),
                    DeleteSelectedReplaySpeedKeyframe, ElementBounds.Fixed(384, y, 30, 28),
                    EnumButtonStyle.Small, "deleteReplaySpeed");
            y += 36;
        }
    }

    private void ComposePreloadEditorEntry(GuiComposer composer, ref int y)
    {
        if (!CanManageChunkPreload)
        {
            return;
        }
        double panelWidth = layout.Panel("inspector").Width;
        string key = composingInspectorSectionTarget == InspectorSectionTarget.ReplayView
            ? "openReplayViewPreloadEditor"
            : "openPreloadEditor";
        composer.AddDirectorButton(
            Lang.Get(preloadEditorExpanded
                ? "vsdirector:preload-editor-hide"
                : "vsdirector:preload-editor-open"),
            TogglePreloadEditor,
            ElementBounds.Fixed(10, y, Math.Clamp(panelWidth - 20d, 180d, 340d), 30),
            preloadEditorExpanded ? EnumButtonStyle.Small : EnumButtonStyle.Normal,
            key);
        y += 38;
    }

    private void ComposeReplayActorControls(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        IReadOnlyList<DirectorReplayActorPathSource> actors = owner.ReplayActorPathSources;
        if (!owner.IsReplayPlaying)
        {
            return;
        }

        if (replayActorReplayId != owner.ActiveReplayId)
        {
            replayActorReplayId = owner.ActiveReplayId;
            selectedReplayActor = string.Empty;
        }

        if (actors.Count == 0)
        {
            return;
        }

        composer.AddStaticText(Lang.Get("vsdirector:replay-actor-controls-title"), small,
            ElementBounds.Fixed(10, y + 5, 390, 22));
        y += 28;

        if (!actors.Any(actor => actor.ActorKey == selectedReplayActor))
        {
            selectedReplayActor = actors[0].ActorKey;
        }

        string[] values = actors.Select(actor => actor.ActorKey).ToArray();
        string[] names = actors.Select(actor => Ellipsize(
            Lang.Get("vsdirector:replay-actor-option", actor.DisplayName, actor.SampleCount),
            42)).ToArray();
        composer
            .AddDropDown(values, names, Math.Max(0, Array.IndexOf(values, selectedReplayActor)),
                OnReplayActorChanged, ElementBounds.Fixed(10, y, 390, 28), "replayActor")
            .AddSmallButton(Lang.Get("vsdirector:replay-attach-first"),
                () => AttachSelectedReplayActor(CameraFollowViewMode.FirstPerson),
                ElementBounds.Fixed(10, y + 36, 116, 28), EnumButtonStyle.Normal, "attachFirst")
            .AddSmallButton(Lang.Get("vsdirector:replay-attach-third"),
                () => AttachSelectedReplayActor(CameraFollowViewMode.ThirdPerson),
                ElementBounds.Fixed(132, y + 36, 116, 28), EnumButtonStyle.Normal, "attachThird")
            .AddSmallButton(Lang.Get("vsdirector:replay-attach-detach"),
                DetachReplayActor,
                ElementBounds.Fixed(254, y + 36, 100, 28), EnumButtonStyle.Small, "detachActor")
            .AddDynamicText(owner.ReplayAttachmentStatusText(), detail,
                ElementBounds.Fixed(10, y + 70, 390, 22), "actorAttachStatus");
        y += 98;
    }

    private void ComposeCategorizedOutputInspector(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        if (!owner.IsRecordingActive)
        {
            double monitorWidth = layout.Panel("inspector").Width;
            AddInspectorHeader(composer, small, Lang.Get("vsdirector:workspace-live-plan"), ref y);
            composer.AddDynamicText(
                owner.RecordingMonitorStatusText(),
                detail,
                ElementBounds.Fixed(10, y, Math.Max(180d, monitorWidth - 20d), 150),
                "liveStatus");
            y += 156;
            return;
        }

        DirectorRecordingTrack? liveTrack = owner.RecordingTracks.FirstOrDefault(
            track => track.Id == selectedRecordingTrackId)
            ?? owner.RecordingTracks.FirstOrDefault(track => track.Id == owner.AssignedRecordingTrackId)
            ?? owner.RecordingTracks.FirstOrDefault();
        if (liveTrack is not null)
        {
            selectedRecordingTrackId = liveTrack.Id;
        }
        double panelWidth = layout.Panel("inspector").Width;
        AddInspectorHeader(composer, small, Lang.Get("vsdirector:workspace-live-plan"), ref y);
        ComposeInspectorSections(composer, ref y, "plan", "control");
        if (inspectorSection == "control")
        {
            if (recordingConfirmationPending)
            {
                composer
                    .AddDirectorIconButton(DirectorIcon.Select, Lang.Get("vsdirector:confirm"),
                        ConfirmRecordingPlan, ElementBounds.Fixed(10, y, 30, 30),
                        EnumButtonStyle.Normal, "confirmRecording")
                    .AddDirectorIconButton(DirectorIcon.Close, Lang.Get("vsdirector:cancel"),
                        CancelRecordingPlan, ElementBounds.Fixed(46, y, 30, 30),
                        EnumButtonStyle.Small, "cancelRecording");
            }
            else
            {
                composer
                    .AddDirectorIconButton(DirectorIcon.Record, Lang.Get("vsdirector:sync-recording-start"),
                        ArmRecordingPlan, ElementBounds.Fixed(10, y, 30, 30),
                        EnumButtonStyle.Normal, "armRecording")
                    .AddDirectorIconButton(DirectorIcon.Stop, Lang.Get("vsdirector:sync-recording-stop"),
                        StopRecordingPlan, ElementBounds.Fixed(46, y, 30, 30),
                        EnumButtonStyle.Small, "stopRecording")
                    .AddDirectorIconButton(DirectorIcon.Close, Lang.Get("vsdirector:sync-recording-unassign"),
                        UnassignRecordingPlan, ElementBounds.Fixed(82, y, 30, 30),
                        EnumButtonStyle.Small, "unassignRecording")
                    .AddDirectorIconButton(DirectorIcon.Delete,
                        liveTrack is null ? Lang.Get("vsdirector:delete") : DeleteLabel(liveTrack.Id),
                        DeleteRecordingTrack, ElementBounds.Fixed(118, y, 30, 30),
                        EnumButtonStyle.Small, "deleteRecording");
            }
            y += 38;
            composer.AddDynamicText(owner.RecordingStatusText(includeBoundary: false), detail,
                ElementBounds.Fixed(10, y, Math.Max(180d, panelWidth - 20d), 96), "liveStatus");
            return;
        }

        composer.AddRichtext(ToVtml(LiveOutputPlanText(liveTrack)), detail,
            ElementBounds.Fixed(10, y, Math.Max(180d, panelWidth - 20d), 124), "livePlan");
        y += 130;
        composer
            .AddSwitch(value =>
            {
                owner.SetRecordReplayWithVideo(value);
                ComposeInspector();
            }, ElementBounds.Fixed(10, y + 2, 25, 25), "takeReplay", 25, 3)
            .AddStaticText(Lang.Get("vsdirector:record-replay-with-video"), small,
                ElementBounds.Fixed(44, y + 5, Math.Max(140d, panelWidth - 54d), 22));
    }

    private void ComposeOutputInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        DirectorRecordingTrack? liveTrack = owner.RecordingTracks.FirstOrDefault(
            track => track.Id == selectedRecordingTrackId)
            ?? owner.RecordingTracks.FirstOrDefault(track => track.Id == owner.AssignedRecordingTrackId)
            ?? owner.RecordingTracks.FirstOrDefault();
        if (liveTrack is not null)
        {
            selectedRecordingTrackId = liveTrack.Id;
        }
        AddInspectorHeader(composer, small, Lang.Get("vsdirector:workspace-live-plan"), ref y);
        composer.AddRichtext(ToVtml(LiveOutputPlanText(liveTrack)), detail,
            ElementBounds.Fixed(10, y, 490, 124), "livePlan");
        y += 130;
        composer
            .AddSwitch(value =>
            {
                owner.SetRecordReplayWithVideo(value);
                ComposeInspector();
            }, ElementBounds.Fixed(10, y + 2, 25, 25), "takeReplay", 25, 3)
            .AddStaticText(Lang.Get("vsdirector:record-replay-with-video"), small,
                ElementBounds.Fixed(44, y + 5, 430, 22));
        y += 34;
        if (recordingConfirmationPending)
        {
            composer
                .AddSmallButton(Lang.Get("vsdirector:confirm"), ConfirmRecordingPlan,
                    ElementBounds.Fixed(10, y, 110, 30), EnumButtonStyle.Normal, "confirmRecording")
                .AddSmallButton(Lang.Get("vsdirector:cancel"), CancelRecordingPlan,
                    ElementBounds.Fixed(126, y, 100, 30), EnumButtonStyle.Small, "cancelRecording");
        }
        else
        {
            composer
                .AddSmallButton(Lang.Get("vsdirector:record-start"), ArmRecordingPlan,
                    ElementBounds.Fixed(10, y, 110, 30), EnumButtonStyle.Normal, "armRecording")
                .AddSmallButton(Lang.Get("vsdirector:record-stop"), StopRecordingPlan,
                    ElementBounds.Fixed(126, y, 100, 30), EnumButtonStyle.Small, "stopRecording")
                .AddSmallButton(Lang.Get("vsdirector:record-unassign"), UnassignRecordingPlan,
                    ElementBounds.Fixed(232, y, 120, 30), EnumButtonStyle.Small, "unassignRecording")
                .AddSmallButton(liveTrack is null ? Lang.Get("vsdirector:delete") : DeleteLabel(liveTrack.Id),
                    DeleteRecordingTrack,
                    ElementBounds.Fixed(358, y, 110, 30), EnumButtonStyle.Small, "deleteRecording");
        }
        y += 38;
        composer.AddDynamicText(owner.RecordingStatusText(includeBoundary: false), detail,
            ElementBounds.Fixed(10, y, 490, 72), "liveStatus");
        y += 78;
    }

    private void ComposePresetInspector(GuiComposer composer, CairoFont small, CairoFont detail, ref int y)
    {
        AddInspectorHeader(composer, small, Lang.Get("vsdirector:preset-package"), ref y);
        composer.AddStaticText(
            selectedPresetFile.Length == 0
                ? string.Empty
                : selectedPresetFile,
            detail,
            ElementBounds.Fixed(10, y, 490, 28));
        y += 34;
        composer
            .AddDirectorIconButton(DirectorIcon.Import, Lang.Get("vsdirector:import"), ImportSelectedPreset,
                ElementBounds.Fixed(10, y, 34, 30), EnumButtonStyle.Normal, "importPreset")
            .AddDirectorIconButton(DirectorIcon.Refresh, Lang.Get("vsdirector:refresh"), RefreshPresetList,
                ElementBounds.Fixed(50, y, 34, 30), EnumButtonStyle.Small, "refreshPresets");
        y += 38;
        composer.AddStaticText(owner.PresetDirectory, detail,
            ElementBounds.Fixed(10, y, 490, 52));
    }

    private void AddFollowDropDowns(GuiComposer composer, CairoFont small, CameraFollowRig rig, ref int y)
    {
        string[] viewCodes = { "first", "third" };
        string[] viewNames = { Lang.Get("vsdirector:follow-view-first"), Lang.Get("vsdirector:follow-view-third") };
        string[] motionCodes = { "immediate", "smooth", "delayed" };
        string[] motionNames =
        {
            Lang.Get("vsdirector:follow-motion-immediate"),
            Lang.Get("vsdirector:follow-motion-smooth"),
            Lang.Get("vsdirector:follow-motion-delayed")
        };
        string[] anchorCodes = { "head", "body" };
        string[] anchorNames = { Lang.Get("vsdirector:follow-anchor-head"), Lang.Get("vsdirector:follow-anchor-body") };
        double panelWidth = layout.Panel("inspector").Width;
        double labelWidth = Math.Clamp(panelWidth * 0.32d, 90d, 150d);
        double inputX = 16d + labelWidth;
        double inputWidth = Math.Max(120d, panelWidth - inputX - 12d);
        int motionIndex = rig.MotionMode switch
        {
            CameraFollowMotionMode.Immediate => 0,
            CameraFollowMotionMode.Delayed => 2,
            _ => 1
        };
        if (panelWidth >= 520d)
        {
            composer
                .AddStaticText(Lang.Get("vsdirector:follow-view-mode"), small,
                    ElementBounds.Fixed(10, y + 6, 90, 22))
                .AddDropDown(viewCodes, viewNames,
                    rig.ViewMode == CameraFollowViewMode.ThirdPerson ? 1 : 0,
                    OnFollowViewChanged, ElementBounds.Fixed(105, y, 120, 28), "followView")
                .AddStaticText(Lang.Get("vsdirector:follow-motion-mode"), small,
                    ElementBounds.Fixed(235, y + 6, 100, 22))
                .AddDropDown(motionCodes, motionNames, motionIndex,
                    OnFollowMotionChanged,
                    ElementBounds.Fixed(340, y, panelWidth - 350d, 28), "followMotion");
            y += 36;
        }
        else
        {
            composer
                .AddStaticText(Lang.Get("vsdirector:follow-view-mode"), small,
                    ElementBounds.Fixed(10, y + 6, labelWidth, 22))
                .AddDropDown(viewCodes, viewNames,
                    rig.ViewMode == CameraFollowViewMode.ThirdPerson ? 1 : 0,
                    OnFollowViewChanged,
                    ElementBounds.Fixed(inputX, y, inputWidth, 28), "followView");
            y += 36;
            composer
                .AddStaticText(Lang.Get("vsdirector:follow-motion-mode"), small,
                    ElementBounds.Fixed(10, y + 6, labelWidth, 22))
                .AddDropDown(motionCodes, motionNames, motionIndex,
                    OnFollowMotionChanged,
                    ElementBounds.Fixed(inputX, y, inputWidth, 28), "followMotion");
            y += 36;
        }
        composer
            .AddStaticText(Lang.Get("vsdirector:follow-anchor"), small,
                ElementBounds.Fixed(10, y + 6, labelWidth, 22))
            .AddDropDown(anchorCodes, anchorNames, rig.Anchor == CameraFollowAnchor.Body ? 1 : 0,
                OnFollowAnchorChanged,
                ElementBounds.Fixed(inputX, y, inputWidth, 28), "followAnchor");
        y += 36;
        composer
            .AddSwitch(OnFollowClearViewChanged,
                ElementBounds.Fixed(10, y + 2, 25, 25), "followClearView", 25, 3)
            .AddStaticText(Lang.Get("vsdirector:follow-clear-view"), small,
                ElementBounds.Fixed(42, y + 5, Math.Max(120d, panelWidth - 54d), 22));
        y += 34;
    }

    private bool HasReplayViewEditor
        => section == DirectorWorkspaceSection.Replay
            && replayViewMode is "camera" or "path" or "sequence" or "follow";

    private int CurrentSequenceRowsPerPage
        => section == DirectorWorkspaceSection.Replay && replayViewMode == "sequence"
            ? Math.Max(1, replaySequenceRowsPerPage)
            : SequenceRowsPerPage;

    private string ReplayViewResourceBlockTitle()
        => Lang.Get(replayViewMode switch
        {
            "camera" => "vsdirector:replay-block-camera-resources",
            "path" => "vsdirector:replay-block-path-resources",
            "sequence" => "vsdirector:replay-block-sequence-resources",
            "follow" => "vsdirector:replay-block-follow-resources",
            _ => "vsdirector:replays"
        });

    private string ReplayViewInspectorBlockTitle()
        => Lang.Get(replayViewMode switch
        {
            "camera" => "vsdirector:replay-block-camera-editor",
            "path" => "vsdirector:replay-block-path-editor",
            "sequence" => "vsdirector:replay-block-sequence-editor",
            "follow" => "vsdirector:replay-block-follow-editor",
            _ => "vsdirector:workspace-inspector"
        });

    private string ReplayViewResourceDetails(Guid id)
        => replayViewMode switch
        {
            "camera" when owner.FindCamera(id) is CameraPreset camera
                => CameraPoseText(camera).Replace('\n', ' '),
            "path" when owner.FindPath(id) is CameraPath path
                => PathResourceDetails(path),
            "sequence" when owner.FindSequence(id) is CameraSequence sequence
                => Lang.Get("vsdirector:sequence-summary",
                    sequence.Shots.Count,
                    sequence.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)),
            "follow" when owner.FindFollowRig(id) is CameraFollowRig rig
                => FollowResourceDetails(rig),
            _ => Lang.Get("vsdirector:none")
        };

    private void AddReplayBlockHeader(
        GuiComposer composer,
        CairoFont font,
        string title,
        double panelWidth,
        double y)
    {
        composer.AddStaticText(
            Ellipsize(title, Math.Max(24, (int)((panelWidth - 24d) / 8d))),
            font,
            ElementBounds.Fixed(12d, y + 5d, panelWidth - 24d, 22d));
    }

    private void RenderReplayBlockBackground(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        double left = Math.Round(bounds.renderX);
        double right = Math.Round(bounds.renderX + bounds.OuterWidth);
        double top = Math.Round(bounds.renderY);
        double bottom = Math.Round(bounds.renderY + bounds.OuterHeight);
        double width = Math.Max(1d, right - left);
        solidTextureRenderer.Render(left, top, width, Math.Max(1d, bottom - top), 99.5f,
            ColorUtil.ToRgba(74, 12, 18, 22));
        double headerDividerY = Math.Round(
            bounds.renderY + GuiElement.scaled(ReplayBlockHeaderHeight - 1d));
        solidTextureRenderer.Render(left, headerDividerY, width, 1d, 99.6f,
            ColorUtil.ToRgba(90, 79, 113, 121));
    }

    private void RenderReplaySplitDivider(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        // Bounds are already measured in physical pixels.  The draggable
        // hit area remains tall, but only one snapped physical row is drawn.
        double left = Math.Round(bounds.renderX);
        double right = Math.Round(bounds.renderX + bounds.OuterWidth);
        double top = Math.Round(bounds.renderY + bounds.OuterHeight * 0.5d);
        double width = Math.Max(1d, right - left);
        int edge = ColorUtil.ToRgba(185, 92, 139, 148);
        solidTextureRenderer.Render(left, top, width, 1d, 100.8f, edge);
    }

    private static ReplaySplitLayout CalculateReplaySplit(
        double start,
        double end,
        double ratio)
    {
        double total = Math.Max(ReplayBlockHeaderHeight * 2d + ReplaySplitDividerHeight, end - start);
        double usable = Math.Max(ReplayBlockHeaderHeight * 2d, total - ReplaySplitDividerHeight);
        double minimum = ReplayBlockHeaderHeight + ReplayBlockMinimumContentHeight;
        double maximumTop = Math.Max(minimum, usable - minimum);
        double topHeight = Math.Clamp(
            usable * Math.Clamp(ratio, 0.18d, 0.82d),
            minimum,
            maximumTop);
        double bottomHeight = usable - topHeight;

        double dividerY = start + topHeight;
        double bottomY = dividerY + ReplaySplitDividerHeight;
        return new ReplaySplitLayout(start, topHeight, dividerY, bottomY, bottomHeight);
    }

    private bool CaptureInspectorDraft()
    {
        if (Composers[InspectorKey] is not GuiComposer composer)
        {
            return true;
        }

        if (section == DirectorWorkspaceSection.Replay)
        {
            CaptureReplayMetadataInputs(composer);
        }

        if (cameraEdit is not null)
        {
            CaptureCameraInputs(composer);
            return true;
        }
        if (pathEdit is not null)
        {
            if (section == DirectorWorkspaceSection.Paths && pathRightTab == "properties")
            {
                pathEdit.Name = composer.GetTextInput("pathName")?.GetText() ?? pathEdit.Name;
                pathTagsText = composer.GetTextInput("pathTags")?.GetText() ?? pathTagsText;
                pathDescriptionText = composer.GetTextInput("pathDescription")?.GetText()
                    ?? pathDescriptionText;
                pathEdit.Tags = ParseTags(pathTagsText);
                pathEdit.Description = pathDescriptionText.Trim();
                return true;
            }

            CameraPath before = pathEdit.DeepClone();
            pathEdit.Name = composer.GetTextInput("pathName")?.GetText() ?? pathEdit.Name;
            if (!CaptureCurrentPathInspectorInputs())
            {
                return false;
            }
            RecordPathEdit(before);
            return true;
        }
        if (sequenceEdit is not null)
        {
            sequenceEdit.Name = composer.GetTextInput("sequenceName")?.GetText()
                ?? sequenceEdit.Name;
            return CaptureSequenceDurations();
        }
        if (followEdit is not null)
        {
            CaptureFollowInputs(composer);
        }
        return true;
    }

    private void AddResourceCount(GuiComposer composer, CairoFont font, int count, ref int y)
    {
        composer.AddStaticText(Lang.Get("vsdirector:workspace-resource-count", count), font,
            ElementBounds.Fixed(10, y, 400, 22));
        y += 26;
    }

    private void AddResourceRow(
        GuiComposer composer,
        CairoFont titleFont,
        CairoFont detailFont,
        int y,
        string title,
        string details,
        Action select,
        Action activate,
        ref int nextY,
        string? actionLabel = null,
        bool danger = false)
    {
        int row = y;
        DirectorWorkspaceSection? menuTarget = resourceMenuSection;
        double panelWidth = layout.Panel("resources").Width;
        double actionWidth = 92d;
        double textWidth = Math.Max(180d, panelWidth - actionWidth - 32d);
        double actionX = panelWidth - actionWidth - 10d;
        composer
            .AddSmallButton(Ellipsize(title, Math.Max(23, (int)(textWidth / 8d))),
                () =>
                {
                    ActivateResourceMenuSection(menuTarget);
                    select();
                    Refresh();
                    return true;
                },
                ElementBounds.Fixed(10, row, textWidth, 28), EnumButtonStyle.Small, "select-" + row)
            .AddDirectorSingleLineText(details, detailFont,
                ElementBounds.Fixed(10, row + 32, textWidth - 10d, 20));
        ActionConsumable activateCommand = () =>
        {
            ActivateResourceMenuSection(menuTarget);
            activate();
            Refresh();
            return true;
        };
        string label = actionLabel ?? Lang.Get("vsdirector:activate");
        if (danger)
        {
            composer.AddDirectorDangerButton(label, activateCommand,
                ElementBounds.Fixed(actionX, row, actionWidth, 28), "activate-" + row);
        }
        else
        {
            composer.AddSmallButton(label, activateCommand,
                ElementBounds.Fixed(actionX, row, actionWidth, 28), EnumButtonStyle.Normal, "activate-" + row);
        }
        nextY = row + ResourceRowHeight;
    }

    private bool ShouldRouteResourceWheelToControl()
    {
        GuiComposer? composer = Composers[ResourceMenuKey];
        if (composer is null)
        {
            return false;
        }
        foreach (string key in new[]
        {
            "recordSource", "liveOffscreenSource", "actionCategoryFilter", "replayEmbeddedPathSource"
        })
        {
            if (composer.GetElement(key) is GuiElementDirectorDropDown dropDown
                && dropDown.HasFocus
                && (dropDown.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY)
                    || dropDown.listMenu.IsOpened))
            {
                return true;
            }
        }
        return false;
    }

    private bool ShouldRouteSequenceWheelToControl(GuiComposer composer)
    {
        IEnumerable<string> keys = new[] { "sequenceCamera", "shotDuration" }
            .Concat(Enumerable.Range(
                sequencePageIndex * CurrentSequenceRowsPerPage,
                Math.Min(CurrentSequenceRowsPerPage, Math.Max(0,
                    (sequenceEdit?.Shots.Count ?? 0) - sequencePageIndex * CurrentSequenceRowsPerPage)))
                .Select(index => "shot-" + index));
        foreach (string key in keys)
        {
            if (composer.GetElement(key) is GuiElement element
                && element.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY))
            {
                return true;
            }
        }
        return false;
    }

    private bool TryScrollResourceList(int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        PreserveInputs();
        DirectorWorkspaceSection menuSection = resourceMenuSection ?? section;
        if (menuSection == DirectorWorkspaceSection.Output)
        {
            ScrollResourceOffset(
                ref outputResourceScrollOffset,
                delta,
                owner.RecordingTracks.Count,
                VisibleResourceRows(130));
            return true;
        }
        if (menuSection == DirectorWorkspaceSection.Replay && HasReplayViewEditor)
        {
            bool viewBlock = replayResourceViewBounds?.PointInside(
                capi.Input.MouseX,
                capi.Input.MouseY) == true;
            if (viewBlock)
            {
                int viewCount = replayViewMode == "path"
                    ? pathEdit?.Keyframes.Count ?? 0
                    : ReplayViewOptions().Count;
                ScrollResourceOffset(
                    ref replayViewResourceScrollOffset,
                    delta,
                    viewCount,
                    replayViewResourceRows);
            }
            else
            {
                ScrollResourceOffset(
                    ref resourceScrollOffset,
                    delta,
                    owner.Replays.Count(replay =>
                        SearchMatch(replay.Name, replay.Description, replay.Tags)),
                    replayResourceRows);
            }
            return true;
        }

        int contentY;
        int count;
        int rows;
        if (menuSection == DirectorWorkspaceSection.Paths
            && section == DirectorWorkspaceSection.Paths
            && pathEdit is not null)
        {
            contentY = 70 + 36 + 34 + 28;
            count = pathEdit.Keyframes.Count;
        }
        else if (menuSection == DirectorWorkspaceSection.Replay)
        {
            contentY = 70 + 36 + (owner.IsReplayIndexing ? 30 : 0) + 26;
            count = owner.Replays.Count(replay =>
                SearchMatch(replay.Name, replay.Description, replay.Tags));
        }
        else if (menuSection == DirectorWorkspaceSection.Actions)
        {
            int baseY = owner.Config.ShowPackageImportExport ? 172 : 138;
            if (actionLibraryMode == "models" && actionLibrarySelectedModelCode.Length > 0)
            {
                DirectorExternalModelInfo? model = owner.ActionModels.FirstOrDefault(candidate =>
                    string.Equals(candidate.Code, actionLibrarySelectedModelCode,
                        StringComparison.OrdinalIgnoreCase));
                contentY = baseY + 34 + 26;
                count = model?.Animations
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Where(code => ActionResourceSearchMatch(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() ?? 0;
                rows = VisibleActionResourceRows(contentY);
                ScrollResourceOffset(ref resourceScrollOffset, delta, count, rows);
                return true;
            }

            contentY = baseY + 26;
            count = BuildActionLibraryEntries().Count;
            rows = VisibleResourceRows(contentY);
            ScrollResourceOffset(ref resourceScrollOffset, delta, count, rows);
            return true;
        }
        else
        {
            contentY = 70 + 36 + 26;
            count = menuSection switch
            {
                DirectorWorkspaceSection.Cameras => owner.Cameras.Count(camera =>
                    SearchMatch(camera.Name, camera.Description, camera.Tags)),
                DirectorWorkspaceSection.Paths => owner.Paths.Count(path =>
                    SearchMatch(path.Name, path.Description, path.Tags)),
                DirectorWorkspaceSection.Actions => owner.Actions.Count(action => SearchMatch(action.Name)),
                DirectorWorkspaceSection.SequenceEditor => owner.Sequences.Count(sequence => SearchMatch(sequence.Name)),
                DirectorWorkspaceSection.Workflows => owner.Workflows.Count(workflow => SearchMatch(workflow.Name)),
                DirectorWorkspaceSection.Follows => owner.FollowRigs.Count(rig =>
                    SearchMatch(rig.Name, rig.Description, rig.Tags)),
                DirectorWorkspaceSection.Presets => owner.PresetFiles.Count(file => SearchMatch(file)),
                _ => 0
            };
        }

        rows = VisibleResourceRows(contentY);
        ScrollResourceOffset(ref resourceScrollOffset, delta, count, rows);
        return true;
    }

    private void ScrollResourceOffset(ref int offset, int delta, int count, int rows)
    {
        int target = Math.Clamp(offset + delta, 0, MaxScrollOffset(count, rows));
        if (target == offset)
        {
            return;
        }
        offset = target;
        ComposeResources();
    }

    private static int MaxScrollOffset(int count, int rows)
        => Math.Max(0, Math.Max(0, count) - Math.Max(1, rows));

    private static int PageCount(int count, int rows)
        => Math.Max(1, (Math.Max(0, count) + rows - 1) / rows);

    private int VisibleResourceRows(int contentY)
    {
        double available = layout.Panel("resources").Height - contentY - 8d;
        return Math.Clamp((int)Math.Floor(available / ResourceRowHeight), 1, ResourceRows);
    }

    private int VisibleActionResourceRows(int contentY)
    {
        double available = layout.Panel("resources").Height - contentY - 8d;
        return Math.Max(1, (int)Math.Floor(available / 36d));
    }

    private void AddInspectorHeader(GuiComposer composer, CairoFont font, string title, ref int y)
    {
        double width = Math.Max(180d, layout.Panel("inspector").Width - 30d);
        composer.AddStaticText(Ellipsize(title, Math.Max(38, (int)(width / 8d))), font,
            ElementBounds.Fixed(10, y, width, 26));
        y += 30;
    }

    private void AddInspectorEmpty(GuiComposer composer, CairoFont font, string message, ref int y)
    {
        _ = composer;
        _ = font;
        _ = message;
        _ = y;
    }

    private void AddLabeledInput(
        GuiComposer composer,
        CairoFont font,
        string label,
        string key,
        string value,
        ref int y,
        AutoSaveTarget autoSaveTarget = AutoSaveTarget.None)
    {
        double panelWidth = layout.Panel("inspector").Width;
        double labelWidth = Math.Clamp(panelWidth * 0.32d, 140d, 210d);
        double inputX = 16d + labelWidth;
        double inputWidth = Math.Clamp(panelWidth - inputX - 16d, 140d, 420d);
        composer
            .AddDirectorSingleLineText(Ellipsize(label, 30), font,
                ElementBounds.Fixed(10, y + 6, labelWidth, 22))
            .AddTextInput(
                ElementBounds.Fixed(inputX, y, inputWidth, 28),
                autoSaveTarget == AutoSaveTarget.None ? null : AutoSaveOnChanged(autoSaveTarget),
                DirectorFont.TextInput(),
                key,
                autoSaveTarget == AutoSaveTarget.None ? null : AutoSaveOnFocusLost(autoSaveTarget));
        y += 34;
    }

    private void AddLabeledNumber(
        GuiComposer composer,
        CairoFont font,
        string label,
        string key,
        double value,
        ref int y,
        Action<string>? onTextChanged = null,
        AutoSaveTarget autoSaveTarget = AutoSaveTarget.None)
    {
        double panelWidth = layout.Panel("inspector").Width;
        double labelWidth = Math.Clamp(panelWidth * 0.32d, 140d, 210d);
        double inputX = 16d + labelWidth;
        double inputWidth = Math.Clamp(panelWidth - inputX - 16d, 120d, 190d);
        Action<string>? changed = autoSaveTarget == AutoSaveTarget.None
            ? onTextChanged
            : valueText =>
            {
                onTextChanged?.Invoke(valueText);
                QueueAutoSave(autoSaveTarget);
            };
        composer
            .AddDirectorSingleLineText(Ellipsize(label, 30), font,
                ElementBounds.Fixed(10, y + 6, labelWidth, 22))
            .AddNumberInput(
                ElementBounds.Fixed(inputX, y, inputWidth, 28),
                changed,
                DirectorFont.TextInput(),
                key,
                autoSaveTarget == AutoSaveTarget.None ? null : AutoSaveOnFocusLost(autoSaveTarget));
        y += 34;
    }

    private void AddAutoSavePathInspectorNumberRow(
        GuiComposer composer,
        CairoFont font,
        ref int y,
        AutoSaveTarget autoSaveTarget,
        params (string Label, string Key)[] fields)
    {
        double contentWidth = Math.Max(200d, layout.Panel("inspector").Width - 20d);
        double slotWidth = contentWidth / Math.Max(1, fields.Length);
        for (int index = 0; index < fields.Length; index++)
        {
            double slotX = 10d + slotWidth * index;
            double labelWidth = Math.Clamp(slotWidth * 0.42d, 26d, 72d);
            composer
                .AddStaticText(Ellipsize(fields[index].Label, Math.Max(3, (int)(labelWidth / 7d))),
                    font, ElementBounds.Fixed(slotX, y + 6, labelWidth, 22))
                .AddNumberInput(
                    ElementBounds.Fixed(
                        slotX + labelWidth + 2d,
                        y,
                        Math.Max(38d, slotWidth - labelWidth - 8d),
                        28),
                    AutoSaveOnChanged(autoSaveTarget),
                    DirectorFont.TextInput(),
                    fields[index].Key,
                    AutoSaveOnFocusLost(autoSaveTarget));
        }
        y += 34;
    }

    private void AddPathInspectorNumberRow(
        GuiComposer composer,
        CairoFont font,
        ref int y,
        params (string Label, string Key)[] fields)
    {
        double contentWidth = Math.Max(200d, layout.Panel("inspector").Width - 20d);
        double slotWidth = contentWidth / Math.Max(1, fields.Length);
        for (int index = 0; index < fields.Length; index++)
        {
            double slotX = 10d + slotWidth * index;
            double labelWidth = Math.Clamp(slotWidth * 0.42d, 26d, 72d);
            composer
                .AddStaticText(Ellipsize(fields[index].Label, Math.Max(3, (int)(labelWidth / 7d))),
                    font, ElementBounds.Fixed(slotX, y + 6, labelWidth, 22))
                .AddNumberInput(ElementBounds.Fixed(
                        slotX + labelWidth + 2d,
                        y,
                        Math.Max(38d, slotWidth - labelWidth - 8d),
                        28),
                    null,
                    DirectorFont.TextInput(),
                    fields[index].Key);
        }
        y += 34;
    }

    private void AddCompactNumberRow(
        GuiComposer composer,
        CairoFont font,
        ref int y,
        Action<string>? onTextChanged,
        AutoSaveTarget autoSaveTarget,
        params (string Label, string Key)[] fields)
    {
        double contentWidth = Math.Max(200d, layout.Panel("inspector").Width - 20d);
        double slotWidth = contentWidth / Math.Max(1, fields.Length);
        for (int index = 0; index < fields.Length; index++)
        {
            double slotX = 10d + slotWidth * index;
            double labelWidth = Math.Clamp(slotWidth * 0.43d, 30d, 78d);
            composer
                .AddStaticText(
                    Ellipsize(fields[index].Label, Math.Max(3, (int)(labelWidth / 7d))),
                    font,
                    ElementBounds.Fixed(slotX, y + 6, labelWidth, 22))
                .AddNumberInput(
                    ElementBounds.Fixed(
                        slotX + labelWidth + 2d,
                        y,
                        Math.Max(38d, slotWidth - labelWidth - 8d),
                        28),
                    value =>
                    {
                        onTextChanged?.Invoke(value);
                        QueueAutoSave(autoSaveTarget);
                    },
                    DirectorFont.TextInput(),
                    fields[index].Key,
                    AutoSaveOnFocusLost(autoSaveTarget));
        }
        y += 34;
    }

    private static ElementBounds PanelRoot(DirectorWorkspacePanelLayout panel)
    {
        return ElementBounds.Fixed(EnumDialogArea.LeftTop, 0, 0, panel.Width, panel.Height)
            .WithFixedAlignmentOffset(panel.X, panel.Y);
    }

    private IReadOnlyList<DirectorWorkspaceSection> VisibleToolbarSections()
    {
        DirectorWorkspaceSection[] ordered =
        {
            DirectorWorkspaceSection.Cameras,
            DirectorWorkspaceSection.Paths,
            DirectorWorkspaceSection.Follows,
            DirectorWorkspaceSection.Replay
        };
        return ordered.Where(IsSectionAvailable).ToArray();
    }

    private bool IsSectionAvailable(DirectorWorkspaceSection target)
        => target switch
        {
            DirectorWorkspaceSection.Cameras => owner.HasPermission(DirectorPermission.CameraLibrary),
            DirectorWorkspaceSection.Paths => owner.HasPermission(DirectorPermission.PathLibrary),
            DirectorWorkspaceSection.Actions => false,
            DirectorWorkspaceSection.SequenceEditor => false,
            DirectorWorkspaceSection.Workflows => false,
            DirectorWorkspaceSection.Follows => owner.HasPermission(DirectorPermission.FollowCameras),
            DirectorWorkspaceSection.Replay => owner.HasPermission(DirectorPermission.ReplayCapture)
                || owner.HasPermission(DirectorPermission.ReplayPlayback),
            DirectorWorkspaceSection.Output => false,
            DirectorWorkspaceSection.LiveOffscreen => false,
            DirectorWorkspaceSection.Presets => false,
            _ => false
        };

    private void NormalizeFeatureVisibility()
    {
        if (resourceMenuSection is DirectorWorkspaceSection menuSection
            && !IsSectionAvailable(menuSection))
        {
            resourceMenuSection = null;
            resourceMenuPointerCapture = false;
        }

        if (!IsSectionAvailable(section))
        {
            PersistWorkflowDraft();
            section = VisibleToolbarSections().FirstOrDefault();
            inspectorSection = DefaultInspectorSection(section);
            layout.ActiveSection = section;
            resourceScrollOffset = 0;
            outputResourceScrollOffset = 0;
            selectedId = null;
            ClearEditors();
        }

        if (!CanManageChunkPreload
            && (preloadEditorExpanded || preloadGridBounds is not null))
        {
            preloadEditorExpanded = false;
            preloadGridBounds = null;
        }

        if (replayViewMode is "sequence" or "path" or "camera")
        {
            replayViewMode = "free";
            replayViewSourceId = Guid.Empty;
            replayViewInspectorSection = "basic";
            replayViewAppliedReplayId = Guid.Empty;
        }
    }

    private static string ToolbarTabKey(DirectorWorkspaceSection target)
        => target switch
        {
            DirectorWorkspaceSection.Cameras => "tab-cameras",
            DirectorWorkspaceSection.Paths => "tab-paths",
            DirectorWorkspaceSection.Actions => "tab-actions",
            DirectorWorkspaceSection.SequenceEditor => "tab-sequences",
            DirectorWorkspaceSection.Workflows => "tab-workflows",
            DirectorWorkspaceSection.Follows => "tab-follows",
            DirectorWorkspaceSection.Replay => "tab-replay",
            DirectorWorkspaceSection.Output => "tab-output",
            DirectorWorkspaceSection.LiveOffscreen => "tab-live-offscreen",
            _ => "tab-presets"
        };

    private bool SelectSection(DirectorWorkspaceSection target)
    {
        if (!IsSectionAvailable(target))
        {
            return true;
        }
        DirectorToolWindows.Close();
        PreserveInputs();
        PersistWorkflowDraft();
        section = target;
        inspectorSection = DefaultInspectorSection(target);
        layout.ActiveSection = target;
        resourceScrollOffset = 0;
        outputResourceScrollOffset = 0;
        selectedId = null;
        ClearEditors();
        workflowEditorWindowOpen = target == DirectorWorkspaceSection.Workflows;
        ComposeAll();
        return true;
    }

    private void PersistWorkflowDraft()
    {
        if (workflowEdit is not null)
        {
            owner.TrySaveWorkflow(workflowEdit, out _, showNotification: false);
        }
    }

    private EnumButtonStyle ToolbarMenuStyle(DirectorWorkspaceSection target)
        => section == target || resourceMenuSection == target
            ? EnumButtonStyle.Normal
            : EnumButtonStyle.Small;

    private void ActivateResourceMenuSection(DirectorWorkspaceSection? target)
    {
        if (target is not DirectorWorkspaceSection resolved || resolved == section)
        {
            return;
        }

        PersistWorkflowDraft();
        section = resolved;
        inspectorSection = DefaultInspectorSection(resolved);
        layout.ActiveSection = resolved;
        resourceScrollOffset = 0;
        outputResourceScrollOffset = 0;
        selectedId = null;
        ClearEditors();
        workflowEditorWindowOpen = resolved == DirectorWorkspaceSection.Workflows;
    }

    private static string DefaultInspectorSection(DirectorWorkspaceSection target)
        => target switch
        {
            DirectorWorkspaceSection.Follows => "target",
            DirectorWorkspaceSection.Replay => "info",
            DirectorWorkspaceSection.Output => "plan",
            _ => "basic"
        };

    private string SectionLabel(DirectorWorkspaceSection target)
        => target switch
        {
            DirectorWorkspaceSection.Cameras => Lang.Get("vsdirector:cameras"),
            DirectorWorkspaceSection.Paths => Lang.Get("vsdirector:paths"),
            DirectorWorkspaceSection.Actions => Lang.Get("vsdirector:actions"),
            DirectorWorkspaceSection.SequenceEditor => Lang.Get("vsdirector:workspace-tab-sequences"),
            DirectorWorkspaceSection.Workflows => Lang.Get("vsdirector:workspace-tab-workflows"),
            DirectorWorkspaceSection.Follows => Lang.Get("vsdirector:follows"),
            DirectorWorkspaceSection.Replay => Lang.Get("vsdirector:replays"),
            DirectorWorkspaceSection.LiveOffscreen => Lang.Get("vsdirector:workspace-tab-live-offscreen"),
            DirectorWorkspaceSection.Presets => Lang.Get("vsdirector:workspace-tab-presets"),
            _ => Lang.Get("vsdirector:workspace-output")
        };

    private string ResourceCreatePlaceholder()
        => Lang.Get((resourceMenuSection ?? section) switch
        {
            DirectorWorkspaceSection.Cameras => "vsdirector:new-camera-placeholder",
            DirectorWorkspaceSection.Paths => "vsdirector:path-name-placeholder",
            DirectorWorkspaceSection.Actions => "vsdirector:action-name-placeholder",
            DirectorWorkspaceSection.SequenceEditor => "vsdirector:sequence-name-placeholder",
            DirectorWorkspaceSection.Workflows => "vsdirector:workflow-name-placeholder",
            DirectorWorkspaceSection.Follows => "vsdirector:follow-name-placeholder",
            DirectorWorkspaceSection.Presets => "vsdirector:preset-name-placeholder",
            _ => "vsdirector:resource-name-placeholder"
        });

    private bool TrySubmitFocusedResourceInput()
    {
        if (Composers[ResourceMenuKey] is GuiComposer resources)
        {
            if (resources.GetTextInput("actionResourceSearch")?.HasFocus == true)
            {
                return ApplyActionResourceSearch();
            }
            if (resources.GetTextInput("actionLibraryNewName")?.HasFocus == true)
            {
                return CreateActionFromLibrary();
            }
            if (resources.GetTextInput("search")?.HasFocus == true)
            {
                return ApplyResourceSearch();
            }
            if (resources.GetTextInput("newName")?.HasFocus == true)
            {
                return OnCreateResource();
            }
        }

        if (floatingEditorDialog.Composer is GuiComposer canvas)
        {
            if (canvas.GetTextInput("actionLibrarySearch")?.HasFocus == true)
            {
                return ApplyActionLibrarySearch();
            }
            if (canvas.GetTextInput("actionLibraryNewName")?.HasFocus == true)
            {
                return CreateActionFromLibrary();
            }
        }

        return false;
    }

    private bool ApplyResourceSearch()
    {
        if (Composers[ResourceMenuKey] is GuiComposer composer)
        {
            search = composer.GetTextInput("search")?.GetText() ?? search;
        }
        resourceScrollOffset = 0;
        ComposeResources();
        return true;
    }

    private bool OnCreateResource()
    {
        PreserveInputs();
        ActivateResourceMenuSection(resourceMenuSection);
        bool created = section switch
        {
            DirectorWorkspaceSection.Cameras => owner.TryAddCamera(newName, out _),
            DirectorWorkspaceSection.Paths => owner.TryAddPath(newName, out _),
            DirectorWorkspaceSection.Actions => owner.TryCreateAction(newName, out _, out _),
            DirectorWorkspaceSection.SequenceEditor => owner.TryAddSequence(newName, out _),
            DirectorWorkspaceSection.Workflows => owner.TryAddWorkflow(newName, out _, out _),
            DirectorWorkspaceSection.Follows => TryCreateFollow(),
            DirectorWorkspaceSection.Presets => owner.TryExportPresetPackage(newName, out _),
            _ => false
        };
        if (created)
        {
            if (section == DirectorWorkspaceSection.Presets)
            {
                selectedPresetFile = owner.PresetFiles.FirstOrDefault() ?? string.Empty;
            }
            newName = string.Empty;
            Refresh();
        }

        return true;
    }

    private bool TryCreateFollow()
    {
        DirectorFollowTarget? target = owner.GetFollowTargets().FirstOrDefault();
        return target is DirectorFollowTarget resolved && owner.TryAddFollowRig(newName, resolved, out _);
    }

    private void SelectCamera(Guid id)
    {
        FlushPendingAutoSave();
        selectedId = id;
        cameraEdit = owner.FindCamera(id);
        pathEdit = null;
        sequenceEdit = null;
        workflowEdit = null;
        actionEdit = null;
        modelEdit = null;
        followEdit = null;
        ComposeInspector();
        ComposeTimeline();
    }

    private void SelectPath(Guid id)
    {
        FlushPendingAutoSave();
        selectedId = id;
        preloadPanChunkX = 0;
        preloadPanChunkZ = 0;
        pathEdit = owner.FindPath(id);
        pathTagsText = pathEdit is null ? string.Empty : string.Join(", ", pathEdit.Tags);
        pathDescriptionText = pathEdit?.Description ?? string.Empty;
        pathTotalDurationDirty = false;
        if (pathEdit is not null)
        {
            CameraPathAuthoring.ExpandCollapsedKeyframes(pathEdit);
        }
        pathEditHistory.Clear();
        pendingPathEditHistory = null;
        selectedPathKeyframeIndex = pathEdit?.Keyframes.Count > 0 ? 0 : -1;
        selectedPathKeyframes.Clear();
        if (selectedPathKeyframeIndex >= 0)
        {
            selectedPathKeyframes.Add(selectedPathKeyframeIndex);
        }
        pathEditorPlayhead = pathEdit is null
            ? 0d
            : CameraPathAuthoring.NextAppendTime(pathEdit);
        pathEditorPlayheadWasPositioned = false;
        previewPathTime = pathEditorPlayhead;
        SyncPathTimingText();
        cameraEdit = null;
        sequenceEdit = null;
        workflowEdit = null;
        followEdit = null;
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            previewPathTime,
            selectedKeyframes: selectedPathKeyframes);
        ComposeResources();
        ComposeInspector();
        ComposeTimeline();
    }

    private void SelectSequence(Guid id)
    {
        FlushPendingAutoSave();
        selectedId = id;
        sequenceEdit = owner.FindSequence(id);
        sequencePageIndex = 0;
        selectedSequenceCameraId = sequenceEdit?.Shots.FirstOrDefault()?.CameraId
            ?? owner.Cameras.FirstOrDefault()?.Id
            ?? Guid.Empty;
        cameraEdit = null;
        pathEdit = null;
        pathEditHistory.Clear();
        pendingPathEditHistory = null;
        workflowEdit = null;
        followEdit = null;
        ComposeInspector();
        ComposeTimeline();
    }

    private void SelectAction(Guid id)
    {
        FlushPendingAutoSave();
        selectedId = id;
        actionEdit = owner.Actions.FirstOrDefault(action => action.Id == id)?.DeepClone();
        modelEdit = null;
        if (actionEdit is not null)
        {
            actionLibraryMode = actionEdit.Category == DirectorActionCategory.Expression
                ? "expressions"
                : "actions";
        }
        selectedActionLayerId = actionEdit?.Layers.FirstOrDefault()?.Id ?? Guid.Empty;
        actionTagsText = actionEdit is null ? string.Empty : string.Join(", ", actionEdit.Tags);
        actionAuthorText = actionEdit?.Author ?? string.Empty;
        actionVersionText = actionEdit?.Version ?? "1.0.0";
        actionDescriptionText = actionEdit?.Description ?? string.Empty;
        actionPreviewSpeedText = actionEdit?.PlaybackSpeed.ToString(CultureInfo.InvariantCulture) ?? "1";
        actionPreviewLoopCode = actionEdit is null ? "loop" : ActionLoopCode(actionEdit.LoopMode);
        actionPreviewLoopCountText = actionEdit?.LoopCount.ToString(CultureInfo.InvariantCulture) ?? "0";
        actionPreviewModelCode = string.Empty;
        actionTrimStartText = "0";
        actionTrimEndText = actionEdit?.DurationSeconds.ToString(CultureInfo.InvariantCulture) ?? "0";
        actionPlayhead = 0d;
        selectedActionFrames.Clear();
        actionEditHistory.Clear();
        cameraEdit = null;
        pathEdit = null;
        pathTotalDurationDirty = false;
        sequenceEdit = null;
        workflowEdit = null;
        followEdit = null;
        SyncSelectedActionLayerInputs();
        ComposeInspector();
        ComposeTimeline();
    }

    private void SelectWorkflow(Guid id)
    {
        FlushPendingAutoSave();
        selectedId = id;
        workflowEdit = owner.FindWorkflow(id);
        workflowEditorWindowOpen = workflowEdit is not null;
        selectedWorkflowCardId = workflowEdit?.Cards.FirstOrDefault()?.Id ?? Guid.Empty;
        selectedWorkflowAudioClipId = workflowEdit?.AudioClips.FirstOrDefault()?.Id ?? Guid.Empty;
        workflowAudioFieldsId = Guid.Empty;
        workflowAudioCreating = false;
        contextWorkflowCardId = Guid.Empty;
        cameraEdit = null;
        pathEdit = null;
        sequenceEdit = null;
        followEdit = null;
        ComposeInspector();
        ComposeTimeline();
    }

    private void SelectFollow(Guid id)
    {
        FlushPendingAutoSave();
        selectedId = id;
        followEdit = owner.FindFollowRig(id);
        cameraEdit = null;
        pathEdit = null;
        sequenceEdit = null;
        workflowEdit = null;
        actionEdit = null;
        ComposeInspector();
        ComposeTimeline();
    }

    private void SelectReplay(Guid id)
    {
        SetReplaySelection(id);
        ComposeInspector();
        ComposeTimeline();
    }

    private void SetReplaySelection(Guid id)
    {
        selectedId = id;
        replayMetadataFieldsId = Guid.Empty;
        preloadPanChunkX = 0;
        preloadPanChunkZ = 0;
        preloadReplayTime = 0d;
        preloadDraftReplayId = Guid.Empty;
        preloadDraftIncluded.Clear();
        preloadDraftExcluded.Clear();
        if (CanManageChunkPreload)
        {
            owner.RequestReplayPreloadEditor(id);
        }
        workflowEdit = null;
        if (HasReplayViewEditor)
        {
            EnsureReplayViewEditor(reset: false);
        }
        else
        {
            cameraEdit = null;
            pathEdit = null;
            sequenceEdit = null;
            followEdit = null;
        }
    }

    private void PlayReplayFromWorkspace(Guid id)
    {
        SelectReplay(id);
        owner.TryPlayReplay(id.ToString("D"), out _, pauseAfterLoad: true);
    }

    private bool SynchronizeReplaySelectionWithActivePlayback(bool force)
    {
        Guid activeReplayId = owner.ActiveReplayId;
        if (section != DirectorWorkspaceSection.Replay)
        {
            return false;
        }
        if (activeReplayId == Guid.Empty)
        {
            synchronizedActiveReplayId = Guid.Empty;
            return false;
        }
        if (owner.Replays.All(replay => replay.Id != activeReplayId))
        {
            return false;
        }

        bool activeReplayChanged = activeReplayId != synchronizedActiveReplayId;
        synchronizedActiveReplayId = activeReplayId;
        if ((!force && !activeReplayChanged) || selectedId == activeReplayId)
        {
            return false;
        }

        SetReplaySelection(activeReplayId);
        return true;
    }

    private void SelectPreset(string file)
    {
        selectedPresetFile = file;
        ComposeInspector();
    }

    private void ImportPreset(string file)
    {
        selectedPresetFile = file;
        if (owner.TryImportPresetPackage(file, out _))
        {
            Refresh();
        }
    }

    private bool ImportSelectedPreset()
    {
        if (selectedPresetFile.Length > 0)
        {
            ImportPreset(selectedPresetFile);
        }
        return true;
    }

    private bool RefreshPresetList()
    {
        if (!owner.PresetFiles.Contains(selectedPresetFile, StringComparer.OrdinalIgnoreCase))
        {
            selectedPresetFile = owner.PresetFiles.FirstOrDefault() ?? string.Empty;
        }
        ComposeResources();
        ComposeInspector();
        return true;
    }

    private void ClearEditors()
    {
        preloadRmlWindow?.Dispose(); preloadRmlWindow = null;
        replayFloatingEditorKind = ReplayFloatingEditorKind.None;
        replayEntityPopupEntity = null;
        replayFloatingTrackId = Guid.Empty;
        cameraEdit = null;
        pathEdit = null;
        pathTotalDurationDirty = false;
        sequenceEdit = null;
        workflowEdit = null;
        actionEdit = null;
        modelEdit = null;
        followEdit = null;
        pathTagsText = string.Empty;
        pathDescriptionText = string.Empty;
        replayMetadataFieldsId = Guid.Empty;
        selectedPathKeyframes.Clear();
        pathEditHistory.Clear();
        pendingPathEditHistory = null;
        ResetWorkspacePointerInteraction();
        ResetPathEditorInteraction();
        preloadEditorExpanded = false;
        workflowEditorWindowOpen = false;
        preloadGridBounds = null;
        selectingPreloadGrid = false;
        panningPreloadGrid = false;
        owner.SetPathEditPreview(null);
        owner.StopActionPreview();
    }

    private void ResetWorkspacePointerInteraction()
    {
        resizingTimelineTrack = Guid.Empty;
        consumeWorkflowContextMouseUp = false;
        selectingPreloadGrid = false;
        panningPreloadGrid = false;
        draggingWorldPathKeyframe = false;
        panningPathEditorTimeline = false;
        draggingPathEditorPlayhead = false;
        draggingPathEditorKeyframes = false;
        selectingPathEditorRange = false;
        stretchingPathEditorSegment = -1;

        pathTimelinePanning = false;
        pathTimelineScrubbing = false;
        pathTimelineDragKind = PathTimelineDragKind.None;
        pathTimelineCameraDragIndex = -1;
        pathTimelineElementDragId = Guid.Empty;
        pathTimelineDragTime = double.NaN;
        pathTimelineDragMoved = false;
        pathTimelineDragBefore = null;
        pathElementTrackReordering = false;
        pathElementTrackReorderId = Guid.Empty;
        pathElementTrackReorderIndex = -1;
        pathElementTrackReorderBefore = null;

        replayElementTrackReordering = false;
        replayElementTrackReorderId = Guid.Empty;
        replayElementTrackReorderIndex = -1;
        replayViewportDraftTimeline = null;
        replayTimelinePanning = false;
        replayExportRangeDragMode = ReplayExportRangeDragMode.None;
        replayExportRangeDraftTimeline = null;
        replayAuxiliaryKeyframeDragKind = ReplayAuxiliaryKeyframeDragKind.None;
        replayAuxiliaryKeyframeDragId = Guid.Empty;
        replayAuxiliaryKeyframeDragMoved = false;
        replayViewportDragMode = ReplayViewportDragMode.None;
        replayViewportDragClipId = Guid.Empty;
        replayViewportDragMoved = false;
        replayViewportDragStartMouseX = 0d;
        replayViewportDragOriginalStart = 0d;
        replayViewportDragOriginalDuration = 0d;
        replayViewportDragOriginalSourceStart = 0d;
        replayKeyframeDragTime = double.NaN;
        scrubbingReplay = false;

        replaySplitDragTarget = ReplaySplitDragTarget.None;
        replaySplitRecomposeAccumulator = 0f;
        replaySplitRecomposePending = false;
        paneSplitDragging = false;
        rotatingActionPreview = false;

        workflowScrollbarDragMode = WorkflowScrollbarDragMode.None;
        draggingWorkflowCard = false;
        workflowCardDragMode = WorkflowCardDragMode.None;
        panningWorkflow = false;
    }

    private bool ActionCategoryMatches(DirectorActionAsset action)
        => actionCategoryFilter switch
        {
            "vanilla" => string.Equals(action.SourceModId, "game", StringComparison.OrdinalIgnoreCase)
                && action.SourceCode.StartsWith("vanilla-animation:", StringComparison.OrdinalIgnoreCase),
            "player" => action.Category == DirectorActionCategory.Player,
            "npc" => action.Category == DirectorActionCategory.Npc,
            "creature" => action.Category == DirectorActionCategory.Creature,
            "expression" => action.Category == DirectorActionCategory.Expression,
            "effect" => action.Category == DirectorActionCategory.SpecialEffect,
            "favorite" => action.Favorite,
            _ => true
        };

    private void OnActionCategoryFilterChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }
        actionCategoryFilter = value;
        actionLibraryPageIndex = 0;
        ComposeWorkflowCanvas();
    }

    private bool ImportExternalActions()
    {
        owner.TryImportExternalActionLibraries(out _, out _);
        Refresh();
        return true;
    }

    private bool PreviewAction(Guid id)
    {
        if (actionEdit?.Id != id)
        {
            SelectAction(id);
        }
        return PreviewSelectedAction();
    }

    private bool PreviewSelectedAction()
    {
        if (actionEdit is null)
        {
            return true;
        }
        CaptureActionInputs();
        if (section == DirectorWorkspaceSection.Actions)
        {
            owner.TryPreviewLibraryAction(
                actionEdit.Id,
                actionPreviewActorKey,
                actionPreviewModelCode,
                ParseDouble(actionPreviewSpeedText, 1d),
                ParseActionLoopMode(actionPreviewLoopCode),
                Math.Max(0, (int)ParseDouble(actionPreviewLoopCountText, 0d)),
                ResolveActionPreviewModelSize(),
                out _);
        }
        else
        {
            owner.TryPreviewAction(
                actionEdit.Id,
                actionPreviewActorKey,
                actionPreviewModelCode,
                ParseDouble(actionPreviewSpeedText, 1d),
                ParseActionLoopMode(actionPreviewLoopCode),
                Math.Max(0, (int)ParseDouble(actionPreviewLoopCountText, 0d)),
                out _);
        }
        ComposeInspector();
        ComposeWorkflowCanvas();
        return true;
    }

    private bool StopActionPreview()
    {
        owner.StopActionPreview();
        ComposeInspector();
        return true;
    }

    private bool RotateActionPreviewLeft()
    {
        owner.RotateActionPreview(-Math.PI / 12d);
        return true;
    }

    private bool RotateActionPreviewRight()
    {
        owner.RotateActionPreview(Math.PI / 12d);
        return true;
    }

    private bool ZoomActionPreviewIn()
    {
        owner.ScaleActionPreviewDistance(0.85d);
        return true;
    }

    private bool ZoomActionPreviewOut()
    {
        owner.ScaleActionPreviewDistance(1d / 0.85d);
        return true;
    }

    private bool ResetActionPreviewView()
    {
        owner.ResetActionPreviewTransform();
        return true;
    }

    private void CaptureModelInputs()
    {
        if (modelEdit is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return;
        }
        modelNameText = composer.GetTextInput("modelName")?.GetText() ?? modelNameText;
        modelCodeText = composer.GetTextInput("modelCode")?.GetText() ?? modelCodeText;
        modelEntityCodeText = composer.GetTextInput("modelEntityCode")?.GetText() ?? modelEntityCodeText;
        modelGroupText = composer.GetTextInput("modelGroup")?.GetText() ?? modelGroupText;
        modelDescriptionText = composer.GetTextInput("modelDescription")?.GetText() ?? modelDescriptionText;
        modelSizeText = composer.GetNumberInput("modelSize")?.GetText() ?? modelSizeText;
        modelShapeAssetText = composer.GetTextInput("modelShapeAsset")?.GetText() ?? modelShapeAssetText;
        modelShapeSourceText = composer.GetTextInput("modelShapeSource")?.GetText() ?? modelShapeSourceText;
        modelEdit.Name = modelNameText;
        modelEdit.ModelCode = modelCodeText;
        modelEdit.EntityCode = modelEntityCodeText;
        modelEdit.Group = modelGroupText;
        modelEdit.Description = modelDescriptionText;
        modelEdit.Size = ParseFloat(modelSizeText, modelEdit.Size);
        modelEdit.ShapeAssetLocation = modelShapeAssetText;
        modelEdit.ShapeSourcePath = modelShapeSourceText;
    }

    private float ResolveActionPreviewModelSize()
    {
        if (modelEdit is not null
            && string.Equals(modelEdit.ModelCode, actionPreviewModelCode, StringComparison.OrdinalIgnoreCase))
        {
            return modelEdit.Size;
        }
        return owner.Models.FirstOrDefault(model => string.Equals(
            model.ModelCode,
            actionPreviewModelCode,
            StringComparison.OrdinalIgnoreCase))?.Size ?? 1f;
    }

    private bool NewModelEditor()
    {
        CaptureActionLibrarySearch();
        actionLibrarySelectedModelCode = string.Empty;
        modelEdit = new DirectorModelAsset
        {
            Name = Lang.Get("vsdirector:model-new-default-name"),
            EntityCode = VSDirectorModSystem.VanillaPlayerModelCode,
            Size = 1f
        };
        modelNameText = modelEdit.Name;
        modelCodeText = string.Empty;
        modelEntityCodeText = VSDirectorModSystem.VanillaPlayerModelCode;
        modelGroupText = string.Empty;
        modelDescriptionText = string.Empty;
        modelSizeText = "1";
        modelShapeAssetText = string.Empty;
        modelShapeSourceText = string.Empty;
        owner.WatchVsmc2Shape(null);
        ComposeInspector();
        ComposeWorkflowCanvas();
        return true;
    }

    private bool SaveModelEditor()
    {
        if (modelEdit is null)
        {
            return true;
        }
        CaptureModelInputs();
        if (owner.TrySaveModel(modelEdit, out DirectorModelAsset? saved, out _) && saved is not null)
        {
            modelEdit = saved;
            modelShapeAssetText = saved.ShapeAssetLocation;
            modelShapeSourceText = saved.ShapeSourcePath;
            owner.WatchVsmc2Shape(saved);
            actionLibrarySelectedModelCode = saved.ModelCode;
            actionPreviewModelCode = saved.ModelCode;
            ComposeInspector();
            ComposeWorkflowCanvas();
        }
        return true;
    }

    private bool RescanModelShape()
    {
        if (modelEdit is null)
        {
            return true;
        }
        CaptureModelInputs();
        if (owner.TryRescanVsmc2Shape(modelEdit, out DirectorModelAsset? saved, out _)
            && saved is not null)
        {
            modelEdit = saved;
            modelShapeAssetText = saved.ShapeAssetLocation;
            modelShapeSourceText = saved.ShapeSourcePath;
            ComposeInspector();
            ComposeWorkflowCanvas();
        }
        return true;
    }

    private bool OpenModelShapeInVsmc2()
    {
        if (modelEdit is not null)
        {
            CaptureModelInputs();
            owner.TryOpenVsmc2Shape(modelEdit, out _);
        }
        return true;
    }

    private bool OpenModelShapeFolder()
    {
        if (modelEdit is not null)
        {
            CaptureModelInputs();
            owner.TryOpenVsmc2ShapeDirectory(modelEdit, out _);
        }
        return true;
    }

    private static bool ShapeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
        }
        catch
        {
            return false;
        }
    }

    private bool IgnoreModelShapeChange()
    {
        if (modelEdit is not null)
        {
            owner.TryIgnoreVsmc2ShapeChange(modelEdit.Id, out _);
        }
        return true;
    }

    private bool EnableModelShapeAutoReload()
    {
        if (modelEdit is not null)
        {
            owner.TryEnableVsmc2AutoReload(modelEdit.Id, out _);
        }
        return true;
    }

    private bool ImportModelFileFromLibrary()
    {
        CaptureActionLibrarySearch();
        if (modelImportPathText.Length > 0
            && owner.TryImportModelFile(modelImportPathText, out _, out _))
        {
            modelEdit = null;
            ComposeInspector();
            ComposeWorkflowCanvas();
            EnsureActionLibrarySelection(restartCurrent: false);
        }
        return true;
    }

    private bool ExportSelectedModelFromLibrary()
    {
        if (modelEdit is null)
        {
            return true;
        }
        CaptureModelInputs();
        if (modelEdit.Id == Guid.Empty)
        {
            SaveModelEditor();
        }
        if (modelEdit?.Id is Guid id && id != Guid.Empty)
        {
            owner.TryExportModel(id, out _, out _);
        }
        return true;
    }

    private bool PreviewSelectedModelAnimation(string animationCode)
    {
        DirectorExternalModelInfo? discovered = owner.ActionModels.FirstOrDefault(model => string.Equals(
            model.Code,
            actionLibrarySelectedModelCode,
            StringComparison.OrdinalIgnoreCase));
        if (discovered is null)
        {
            return true;
        }
        CaptureModelInputs();
        string entityCode = modelEdit is not null
                && string.Equals(modelEdit.ModelCode, discovered.Code, StringComparison.OrdinalIgnoreCase)
            ? modelEdit.EntityCode
            : discovered.EntityCode;
        float modelSize = modelEdit is not null
                && string.Equals(modelEdit.ModelCode, discovered.Code, StringComparison.OrdinalIgnoreCase)
            ? modelEdit.Size
            : 1f;
        owner.TryPreviewLibraryModelAnimation(
            actionPreviewActorKey,
            discovered.Code,
            entityCode,
            animationCode,
            modelSize,
            out _);
        return true;
    }

    private bool PreviewModelEditor()
    {
        if (modelEdit is null)
        {
            return true;
        }
        CaptureModelInputs();
        actionLibrarySelectedModelCode = modelEdit.ModelCode;
        actionPreviewModelCode = modelEdit.ModelCode;
        owner.TryPreviewLibraryModel(
            actionPreviewActorKey,
            modelEdit.ModelCode,
            modelEdit.EntityCode,
            modelEdit.Size,
            out _);
        ComposeWorkflowCanvas();
        return true;
    }

    private bool ToggleModelFavorite()
    {
        if (modelEdit is not null)
        {
            modelEdit.Favorite = !modelEdit.Favorite;
            SaveModelEditor();
        }
        return true;
    }

    private bool DeleteModelEditor()
    {
        if (modelEdit?.Id is not Guid id || id == Guid.Empty)
        {
            return true;
        }
        if (owner.TryDeleteModel(id, out _))
        {
            modelEdit = null;
            actionLibrarySelectedModelCode = string.Empty;
            ComposeInspector();
            ComposeWorkflowCanvas();
        }
        return true;
    }

    private bool StoreActionPreviewModel()
    {
        if (actionEdit is null)
        {
            return true;
        }
        Guid id = actionEdit.Id;
        if (owner.TryStoreActionPreviewModel(id, actionPreviewActorKey, out _))
        {
            SelectAction(id);
            ComposeResources();
            ComposeInspector();
        }
        return true;
    }

    private bool ClearActionPreviewModel()
    {
        if (actionEdit is null)
        {
            return true;
        }
        Guid id = actionEdit.Id;
        if (owner.TryClearActionPreviewModel(id, out _))
        {
            SelectAction(id);
            ComposeResources();
            ComposeInspector();
        }
        return true;
    }

    private bool SaveActionEditor()
    {
        if (actionEdit is null)
        {
            return true;
        }
        CaptureActionInputs();
        if (owner.TrySaveAction(actionEdit, out _))
        {
            SelectAction(actionEdit.Id);
            ComposeResources();
        }
        return true;
    }

    private bool DeleteActionEditor()
    {
        if (actionEdit is null)
        {
            return true;
        }
        Guid id = actionEdit.Id;
        return ConfirmDelete(id, () => owner.TryDeleteAction(id, out _));
    }

    private bool ToggleActionFavorite()
    {
        if (actionEdit is not null
            && owner.TrySetActionFavorite(actionEdit.Id, !actionEdit.Favorite, out _))
        {
            SelectAction(actionEdit.Id);
            ComposeResources();
        }
        return true;
    }

    private bool RecordActionFromReplay()
    {
        PreserveInputs();
        string name = newName.Trim();
        if (name.Length == 0)
        {
            string baseName = actionEdit?.Name ?? Lang.Get("vsdirector:action-default-name");
            name = baseName + " " + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        }
        if (owner.TryRecordReplayAction(
                actionPreviewActorKey,
                ParseDouble(actionRecordStartText, 0d),
                ParseDouble(actionRecordEndText, owner.ReplayDurationSeconds),
                name,
                out DirectorActionAsset? created,
                out _)
            && created is not null)
        {
            newName = string.Empty;
            SelectAction(created.Id);
            ComposeResources();
        }
        return true;
    }

    private bool ToggleLiveActionRecording()
    {
        PreserveInputs();
        if (owner.IsLiveActionRecording)
        {
            if (owner.TryStopLiveActionRecording(out DirectorActionAsset? created, out _)
                && created is not null)
            {
                newName = string.Empty;
                SelectAction(created.Id);
                ComposeResources();
            }
            ComposeInspector();
            return true;
        }

        string name = newName.Trim();
        if (name.Length == 0)
        {
            string baseName = actionEdit?.Name ?? Lang.Get("vsdirector:action-default-name");
            name = baseName + " " + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        }
        string actorKey = actionPreviewActorKey;
        if (actorKey.Length == 0)
        {
            actorKey = owner.ActionActorSources.FirstOrDefault().ActorKey ?? string.Empty;
        }
        owner.TryStartLiveActionRecording(actorKey, name, out _);
        ComposeInspector();
        return true;
    }

    private bool TrimAction()
    {
        if (actionEdit is null)
        {
            return true;
        }
        CaptureActionInputs();
        DirectorActionAsset before = actionEdit.DeepClone();
        actionEdit = DirectorActionEditor.Trim(
            actionEdit,
            ParseDouble(actionTrimStartText, 0d),
            ParseDouble(actionTrimEndText, actionEdit.DurationSeconds));
        actionTrimStartText = "0";
        actionTrimEndText = actionEdit.DurationSeconds.ToString(CultureInfo.InvariantCulture);
        selectedActionLayerId = actionEdit.Layers.FirstOrDefault()?.Id ?? Guid.Empty;
        selectedActionFrames.Clear();
        actionEditHistory.RecordChange(before, actionEdit);
        SyncSelectedActionLayerInputs();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private bool AddActionLayer()
    {
        if (actionEdit is null)
        {
            return true;
        }
        CaptureActionInputs();
        DirectorActionAsset before = actionEdit.DeepClone();
        DirectorActionLayer? source = SelectedActionLayer() ?? actionEdit.Layers.FirstOrDefault();
        DirectorActionLayer layer = new()
        {
            Id = Guid.NewGuid(),
            Name = Lang.Get("vsdirector:action-layer-default", actionEdit.Layers.Count + 1),
            Kind = DirectorActionLayerKind.UpperBody,
            BlendMode = DirectorActionBlendMode.Additive,
            Order = actionEdit.Layers.Count,
            Frames = source?.Frames.Select(frame => frame.DeepClone()).ToList()
                ?? new List<DirectorActionFrame>()
        };
        actionEdit.Layers.Add(layer);
        actionEditHistory.RecordChange(before, actionEdit);
        selectedActionLayerId = layer.Id;
        SyncSelectedActionLayerInputs();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private bool DeleteActionLayer()
    {
        if (actionEdit is null || selectedActionLayerId == Guid.Empty)
        {
            return true;
        }
        DirectorActionAsset before = actionEdit.DeepClone();
        Guid deletedLayerId = selectedActionLayerId;
        actionEdit.Layers.RemoveAll(layer => layer.Id == selectedActionLayerId);
        selectedActionFrames.RemoveWhere(selection => selection.LayerId == deletedLayerId);
        actionEditHistory.RecordChange(before, actionEdit);
        selectedActionLayerId = actionEdit.Layers.FirstOrDefault()?.Id ?? Guid.Empty;
        SyncSelectedActionLayerInputs();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private DirectorActionLayer? SelectedActionLayer()
        => actionEdit?.Layers.FirstOrDefault(layer => layer.Id == selectedActionLayerId)
            ?? actionEdit?.Layers.FirstOrDefault();

    private void OnActionLayerChanged(string value, bool selected)
    {
        if (selected && Guid.TryParse(value, out Guid id))
        {
            CaptureActionLayerInputs();
            selectedActionLayerId = id;
            SyncSelectedActionLayerInputs();
            ComposeInspector();
        }
    }

    private void OnActionLayerKindChanged(string value, bool selected)
    {
        if (selected && SelectedActionLayer() is { } layer)
        {
            layer.Kind = ParseActionLayerKind(value);
            layer.BlendMode = layer.Kind == DirectorActionLayerKind.FullBody
                ? DirectorActionBlendMode.Override
                : DirectorActionBlendMode.Additive;
        }
    }

    private void SyncSelectedActionLayerInputs()
    {
        DirectorActionLayer? layer = SelectedActionLayer();
        if (layer is null)
        {
            return;
        }
        selectedActionLayerId = layer.Id;
        actionLayerWeightText = layer.Weight.ToString(CultureInfo.InvariantCulture);
        actionLayerFadeInText = layer.FadeInSeconds.ToString(CultureInfo.InvariantCulture);
        actionLayerFadeOutText = layer.FadeOutSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private void CaptureActionLayerInputs()
    {
        if (SelectedActionLayer() is not { } layer || Composers[InspectorKey] is not GuiComposer composer)
        {
            return;
        }
        actionLayerWeightText = composer.GetNumberInput("actionLayerWeight")?.GetText() ?? actionLayerWeightText;
        actionLayerFadeInText = composer.GetNumberInput("actionLayerFadeIn")?.GetText() ?? actionLayerFadeInText;
        actionLayerFadeOutText = composer.GetNumberInput("actionLayerFadeOut")?.GetText() ?? actionLayerFadeOutText;
        layer.Weight = ParseFloat(actionLayerWeightText, layer.Weight);
        layer.FadeInSeconds = ParseDouble(actionLayerFadeInText, layer.FadeInSeconds);
        layer.FadeOutSeconds = ParseDouble(actionLayerFadeOutText, layer.FadeOutSeconds);
    }

    private void CaptureActionInputs()
    {
        if (actionEdit is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return;
        }
        actionEdit.Name = composer.GetTextInput("actionName")?.GetText()?.Trim() ?? actionEdit.Name;
        actionTagsText = composer.GetTextInput("actionTags")?.GetText() ?? actionTagsText;
        actionAuthorText = composer.GetTextInput("actionAuthor")?.GetText() ?? actionAuthorText;
        actionVersionText = composer.GetTextInput("actionVersion")?.GetText() ?? actionVersionText;
        actionDescriptionText = composer.GetTextInput("actionDescription")?.GetText() ?? actionDescriptionText;
        actionPreviewSpeedText = composer.GetNumberInput("actionSpeed")?.GetText() ?? actionPreviewSpeedText;
        actionPreviewLoopCountText = composer.GetNumberInput("actionLoopCount")?.GetText() ?? actionPreviewLoopCountText;
        actionEdit.Tags = actionTagsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        actionEdit.Author = actionAuthorText.Trim();
        actionEdit.Version = actionVersionText.Trim();
        actionEdit.Description = actionDescriptionText.Trim();
        actionEdit.PlaybackSpeed = ParseDouble(actionPreviewSpeedText, actionEdit.PlaybackSpeed);
        actionEdit.LoopMode = ParseActionLoopMode(actionPreviewLoopCode);
        actionEdit.LoopCount = Math.Max(0, (int)ParseDouble(actionPreviewLoopCountText, actionEdit.LoopCount));
        CaptureActionLayerInputs();
    }

    private void OnActionCategoryChanged(string value, bool selected)
    {
        if (selected && actionEdit is not null)
        {
            actionEdit.Category = ParseActionCategory(value);
            QueueAutoSave(AutoSaveTarget.ActionMetadata);
        }
    }

    private void OnActionLoopChanged(string value, bool selected)
    {
        if (selected)
        {
            actionPreviewLoopCode = value;
        }
    }

    private void OnActionActorChanged(string value, bool selected)
    {
        if (selected)
        {
            actionPreviewActorKey = value;
        }
    }

    private void OnActionModelChanged(string value, bool selected)
    {
        if (selected)
        {
            actionPreviewModelCode = value;
        }
    }

    private void OnActionEventTypeChanged(string value, bool selected)
    {
        if (selected)
        {
            actionEventTypeCode = value;
        }
    }

    private bool AddActionEvent()
    {
        if (actionEdit is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return true;
        }
        DirectorActionAsset before = actionEdit.DeepClone();
        actionEventTimeText = composer.GetNumberInput("actionEventTime")?.GetText() ?? actionEventTimeText;
        actionEventCodeText = composer.GetTextInput("actionEventCode")?.GetText()?.Trim() ?? actionEventCodeText;
        actionEdit.Events.Add(new DirectorActionEvent
        {
            Id = Guid.NewGuid(),
            TimeSeconds = ParseDouble(actionEventTimeText, actionPlayhead),
            Type = actionEventTypeCode switch
            {
                "particle" => DirectorActionEventType.Particle,
                "marker" => DirectorActionEventType.Marker,
                _ => DirectorActionEventType.Sound
            },
            Code = actionEventCodeText
        });
        actionEditHistory.RecordChange(before, actionEdit);
        actionEventCodeText = string.Empty;
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private bool ExportActionJson()
    {
        if (actionEdit is not null)
        {
            owner.TryExportAction(actionEdit.Id, binary: false, out _, out _);
        }
        return true;
    }

    private bool ExportActionBinary()
    {
        if (actionEdit is not null)
        {
            owner.TryExportAction(actionEdit.Id, binary: true, out _, out _);
        }
        return true;
    }

    private bool ExportActionPack()
    {
        if (actionEdit is not null)
        {
            Guid[] ids = owner.Actions
                .Where(ActionCategoryMatches)
                .Where(action => SearchMatch(action.Name)
                    || action.Tags.Any(tag => SearchMatch(tag))
                    || SearchMatch(action.Description))
                .Select(action => action.Id)
                .ToArray();
            owner.TryExportActionPack(ids.Length == 0 ? new[] { actionEdit.Id } : ids,
                actionEdit.Name, out _, out _);
        }
        return true;
    }

    private bool ImportActionFile()
    {
        PreserveInputs();
        if (actionImportPathText.Length > 0
            && owner.TryImportActionFile(actionImportPathText, out _, out _))
        {
            Refresh();
        }
        return true;
    }

    private static string ActionCategoryCode(DirectorActionCategory category)
        => category switch
        {
            DirectorActionCategory.Npc => "npc",
            DirectorActionCategory.Creature => "creature",
            DirectorActionCategory.Expression => "expression",
            DirectorActionCategory.SpecialEffect => "effect",
            _ => "player"
        };

    private static DirectorActionCategory ParseActionCategory(string code)
        => code switch
        {
            "npc" => DirectorActionCategory.Npc,
            "creature" => DirectorActionCategory.Creature,
            "expression" => DirectorActionCategory.Expression,
            "effect" => DirectorActionCategory.SpecialEffect,
            _ => DirectorActionCategory.Player
        };

    private static string ActionCategoryLabel(DirectorActionCategory category)
        => Lang.Get("vsdirector:action-category-" + ActionCategoryCode(category));

    private static string ActionLoopCode(DirectorActionLoopMode mode)
        => mode switch
        {
            DirectorActionLoopMode.Loop => "loop",
            DirectorActionLoopMode.PingPong => "pingpong",
            _ => "once"
        };

    private static DirectorActionLoopMode ParseActionLoopMode(string code)
        => code switch
        {
            "loop" => DirectorActionLoopMode.Loop,
            "pingpong" => DirectorActionLoopMode.PingPong,
            _ => DirectorActionLoopMode.Once
        };

    private static string ActionTriggerCode(DirectorActionTriggerType type)
        => type switch
        {
            DirectorActionTriggerType.Attack => "attack",
            DirectorActionTriggerType.Mining => "mining",
            DirectorActionTriggerType.Swimming => "swimming",
            DirectorActionTriggerType.Sprinting => "sprinting",
            DirectorActionTriggerType.Jumping => "jumping",
            DirectorActionTriggerType.Hurt => "hurt",
            DirectorActionTriggerType.Custom => "custom",
            _ => "manual"
        };

    private static DirectorActionTriggerType ParseActionTriggerType(string code)
        => code switch
        {
            "attack" => DirectorActionTriggerType.Attack,
            "mining" => DirectorActionTriggerType.Mining,
            "swimming" => DirectorActionTriggerType.Swimming,
            "sprinting" => DirectorActionTriggerType.Sprinting,
            "jumping" => DirectorActionTriggerType.Jumping,
            "hurt" => DirectorActionTriggerType.Hurt,
            "custom" => DirectorActionTriggerType.Custom,
            _ => DirectorActionTriggerType.Manual
        };

    private static string ActionLayerKindCode(DirectorActionLayerKind kind)
        => kind switch
        {
            DirectorActionLayerKind.UpperBody => "upper",
            DirectorActionLayerKind.LowerBody => "lower",
            DirectorActionLayerKind.Head => "head",
            DirectorActionLayerKind.Hands => "hands",
            DirectorActionLayerKind.Expression => "expression",
            DirectorActionLayerKind.Additive => "additive",
            _ => "full"
        };

    private static DirectorActionLayerKind ParseActionLayerKind(string code)
        => code switch
        {
            "upper" => DirectorActionLayerKind.UpperBody,
            "lower" => DirectorActionLayerKind.LowerBody,
            "head" => DirectorActionLayerKind.Head,
            "hands" => DirectorActionLayerKind.Hands,
            "expression" => DirectorActionLayerKind.Expression,
            "additive" => DirectorActionLayerKind.Additive,
            _ => DirectorActionLayerKind.FullBody
        };

    private static string ActionLayerKindLabel(DirectorActionLayerKind kind)
        => Lang.Get("vsdirector:action-layer-" + ActionLayerKindCode(kind));

    private bool SaveCameraEditor()
    {
        if (TryCommitCameraEditor())
        {
            Refresh();
        }
        return true;
    }

    private bool TryCommitCameraEditor()
    {
        if (cameraEdit is null)
        {
            return false;
        }

        GuiComposer composer = Composers[InspectorKey];
        CaptureCameraInputs(composer);
        if (!owner.TryUpdateCamera(cameraEdit, out _))
        {
            return false;
        }
        cameraEdit = owner.FindCamera(cameraEdit.Id) ?? cameraEdit;
        RestoreInspectorInputs();
        return true;
    }

    private void CaptureCameraInputs(GuiComposer composer)
    {
        if (cameraEdit is null)
        {
            return;
        }

        cameraEdit.Name = composer.GetTextInput("cameraName")?.GetText() ?? cameraEdit.Name;
        if (composer.GetTextInput("cameraTags") is { } tagsInput)
        {
            cameraEdit.Tags = tagsInput.GetText()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        cameraEdit.Description = composer.GetTextInput("cameraDescription")?.GetText()
            ?? cameraEdit.Description;
        cameraEdit.Fov = CameraPathOrientation.PreserveDegrees(
            ReadNumber(composer, "cameraFov", cameraEdit.Fov), cameraEdit.Fov);
        cameraEdit.Roll = CameraPathOrientation.PreserveDegrees(
            ReadNumber(composer, "cameraRoll", cameraEdit.Roll), cameraEdit.Roll);

        (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
            cameraEdit.X, cameraEdit.Y, cameraEdit.Z);
        double requestedX = ReadNumber(composer, "cameraPosX", displayX);
        double requestedY = ReadNumber(composer, "cameraPosY", displayY);
        double requestedZ = ReadNumber(composer, "cameraPosZ", displayZ);
        cameraEdit.X += requestedX - displayX;
        cameraEdit.Y += requestedY - displayY;
        cameraEdit.Z += requestedZ - displayZ;
        cameraEdit.Yaw = CameraPathOrientation.PreserveDegrees(
            ReadNumber(composer, "cameraYaw", cameraEdit.Yaw), cameraEdit.Yaw);
        cameraEdit.Pitch = CameraPathOrientation.PreserveDegrees(
            ReadNumber(composer, "cameraPitch", cameraEdit.Pitch), cameraEdit.Pitch);
        if (composer.GetNumberInput("cameraDimension") is { } dimensionInput
            && int.TryParse(dimensionInput.GetText(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int dimension))
        {
            cameraEdit.Dimension = dimension;
        }
    }

    private bool RecaptureCameraEditor()
    {
        if (cameraEdit is not null && TryCommitCameraEditor())
        {
            owner.TryRecaptureCamera(cameraEdit.Id, out _);
            cameraEdit = owner.FindCamera(cameraEdit.Id);
            RestoreInspectorInputs();
            Refresh();
        }
        return true;
    }

    private bool ActivateCameraEditor()
    {
        if (cameraEdit is not null && TryCommitCameraEditor())
        {
            if (section == DirectorWorkspaceSection.Replay)
            {
                replayViewMode = "camera";
                replayViewSourceId = cameraEdit.Id;
                _ = ApplyReplayView();
            }
            else
            {
                ActivateCameraFromWorkspace(cameraEdit.Id);
            }
        }
        return true;
    }

    private bool ToggleCameraModel()
    {
        if (cameraEdit is null)
        {
            return true;
        }

        if (!TryCommitCameraEditor())
        {
            return true;
        }
        owner.TrySetCameraPreviewVisible(cameraEdit.Id, !cameraEdit.PreviewVisible, out _);
        cameraEdit = owner.FindCamera(cameraEdit.Id);
        Refresh();
        return true;
    }

    private void OnCameraSlotChanged(string value, bool selected)
    {
        if (selected && cameraEdit is not null)
        {
            cameraEdit.QuickSlot = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)
                ? slot
                : null;
            QueueAutoSave(AutoSaveTarget.Camera);
        }
    }

    private bool DeleteCameraEditor()
        => cameraEdit is null || ConfirmDelete(cameraEdit.Id, () => owner.TryDeleteCamera(cameraEdit.Id, out _));

    private bool SavePathEditor() => SavePathEditor(captureTiming: true);

    private bool SavePathEditor(bool captureTiming)
    {
        _ = TryCommitPathEditor(captureTiming);
        return true;
    }

    private bool TryCommitPathEditor(bool captureTiming)
    {
        if (pathEdit is null)
        {
            return false;
        }

        CameraPath before = pathEdit.DeepClone();
        GuiComposer composer = Composers[InspectorKey];
        pathEdit.Name = composer.GetTextInput("pathName")?.GetText() ?? pathEdit.Name;
        if (captureTiming && SelectedPathFrame() is not null)
        {
            if (!CapturePathSegmentMetadataInputs())
            {
                return false;
            }
            CapturePathKeyframePropertyInputs();
            CapturePathTimingInputs();
        }
        RecordPathEdit(before);
        Guid pathId = pathEdit.Id;
        if (!owner.TrySavePath(pathEdit, out _))
        {
            return false;
        }
        pathEdit = owner.FindPath(pathId) ?? pathEdit;
        SyncPathTimingText();
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            pathEditorPlayhead,
            selectedKeyframes: selectedPathKeyframes);
        ComposeAll();
        return true;
    }

    private bool ApplyPathKeyframeTiming()
    {
        if (pathEdit is null || SelectedPathFrame() is null)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        if (!CapturePathSegmentMetadataInputs())
        {
            return true;
        }
        _ = CapturePathTimingInputs();
        RecordPathEdit(before);

        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
        SyncPathTimingText();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private bool ApplyPathKeyframeProperties()
    {
        if (pathEdit is null || SelectedPathFrame() is null)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        CapturePathKeyframePropertyInputs();
        _ = CapturePathTimingInputs();
        if (!CapturePathSegmentMetadataInputs())
        {
            return true;
        }
        RecordPathEdit(before);
        FinishPathEditorInteraction(recomposeResources: true);
        return true;
    }

    private bool ApplyPathTotalDuration()
    {
        if (pathEdit is null)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        double requested = ReadNumber(
            Composers[InspectorKey],
            "pathTotalDuration",
            pathEdit.DurationSeconds);
        if (CameraPathTiming.TryScaleTimeline(pathEdit, requested))
        {
            RecordPathEdit(before);
            pathEditorPlayhead = Math.Min(pathEditorPlayhead, pathEdit.DurationSeconds);
            FinishPathEditorInteraction(recomposeResources: true);
        }
        return true;
    }

    private void CapturePathKeyframePropertyInputs()
    {
        CameraKeyframe? frame = SelectedPathFrame();
        if (frame is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return;
        }

        (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
            frame.X, frame.Y, frame.Z);
        double requestedX = ReadNumber(composer, "pathPosX", displayX);
        double requestedY = ReadNumber(composer, "pathPosY", displayY);
        double requestedZ = ReadNumber(composer, "pathPosZ", displayZ);
        frame.X += requestedX - displayX;
        frame.Y += requestedY - displayY;
        frame.Z += requestedZ - displayZ;
        frame.Yaw = CameraPathOrientation.PreserveDegrees(
            ReadNumber(composer, "pathYaw", frame.Yaw), frame.Yaw);
        frame.Pitch = CameraPathOrientation.PreserveDegrees(
            ReadNumber(composer, "pathPitch", frame.Pitch), frame.Pitch);
        frame.Name = composer.GetTextInput("pathKeyframeName")?.GetText()?.Trim()
            ?? frame.Name;
        frame.BezierHandlesEnabled = composer.GetSwitch("pathBezierEnabled")?.On
            ?? frame.BezierHandlesEnabled;
        frame.BezierInX = ReadNumber(composer, "pathBezierInX", frame.BezierInX);
        frame.BezierInY = ReadNumber(composer, "pathBezierInY", frame.BezierInY);
        frame.BezierInZ = ReadNumber(composer, "pathBezierInZ", frame.BezierInZ);
        frame.BezierOutX = ReadNumber(composer, "pathBezierOutX", frame.BezierOutX);
        frame.BezierOutY = ReadNumber(composer, "pathBezierOutY", frame.BezierOutY);
        frame.BezierOutZ = ReadNumber(composer, "pathBezierOutZ", frame.BezierOutZ);
        _ = CameraPathTiming.TrySetKeyframeHold(
            pathEdit!,
            SelectedPathKeyframeIndex(),
            ReadNumber(composer, "pathFrameHold", frame.HoldSeconds),
            out _);
    }

    private void SetSelectedBezierHandlesEnabled(bool enabled)
    {
        CameraKeyframe? frame = SelectedPathFrame();
        if (frame is null || pathEdit is null)
        {
            return;
        }
        CameraPath before = pathEdit.DeepClone();
        frame.BezierHandlesEnabled = enabled;
        if (enabled
            && Math.Abs(frame.BezierInX) + Math.Abs(frame.BezierInY) + Math.Abs(frame.BezierInZ)
                + Math.Abs(frame.BezierOutX) + Math.Abs(frame.BezierOutY) + Math.Abs(frame.BezierOutZ)
                <= 0.000001d)
        {
            int index = SelectedPathKeyframeIndex();
            if (index > 0)
            {
                CameraKeyframe previous = pathEdit.Keyframes[index - 1];
                frame.BezierInX = (previous.X - frame.X) / 3d;
                frame.BezierInY = (previous.Y - frame.Y) / 3d;
                frame.BezierInZ = (previous.Z - frame.Z) / 3d;
            }
            if (index + 1 < pathEdit.Keyframes.Count)
            {
                CameraKeyframe next = pathEdit.Keyframes[index + 1];
                frame.BezierOutX = (next.X - frame.X) / 3d;
                frame.BezierOutY = (next.Y - frame.Y) / 3d;
                frame.BezierOutZ = (next.Z - frame.Z) / 3d;
            }
        }
        RecordPathEdit(before);
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
        QueueAutoSave(section == DirectorWorkspaceSection.Paths
            ? AutoSaveTarget.Path
            : AutoSaveTarget.PathKeyframe);
    }

    private bool CaptureCurrentPathInspectorInputs()
    {
        if (SelectedPathFrame() is null)
        {
            return true;
        }

        if (section == DirectorWorkspaceSection.Paths)
        {
            CapturePathKeyframePropertyInputs();
            if (pathRightTab == "properties")
            {
                _ = CapturePathTimingInputs();
                return CapturePathSegmentMetadataInputs();
            }
            return true;
        }

        string activeSection = replayViewInspectorSection;
        if (activeSection == "keyframe")
        {
            CapturePathKeyframePropertyInputs();
            _ = CapturePathTimingInputs();
        }
        else if (activeSection == "segment")
        {
            CapturePathKeyframePropertyInputs();
            return CapturePathSegmentMetadataInputs();
        }

        return true;
    }

    private CameraKeyframe? SelectedPathFrame()
    {
        int index = SelectedPathKeyframeIndex();
        return pathEdit is not null && index >= 0 && index < pathEdit.Keyframes.Count
            ? pathEdit.Keyframes[index]
            : null;
    }

    private bool CapturePathTimingInputs()
    {
        int index = SelectedPathKeyframeIndex();
        if (pathEdit is null || index < 0 || index >= pathEdit.Keyframes.Count)
        {
            return false;
        }

        GuiComposer composer = Composers[InspectorKey];
        VSDirector.Data.CameraKeyframe frame = pathEdit.Keyframes[index];
        double requestedStart = ParseDouble(
            composer.GetNumberInput("pathFrameStart")?.GetText(),
            frame.TimeSeconds);
        double requestedDuration = ParseDouble(
            composer.GetNumberInput("pathFrameDuration")?.GetText(),
            frame.DurationSeconds);
        bool changed = CameraPathTiming.TrySetKeyframeTime(
            pathEdit,
            index,
            requestedStart,
            out _);
        changed |= CameraPathTiming.TrySetKeyframeDuration(
            pathEdit,
            index,
            requestedDuration,
            out _);
        if (changed)
        {
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
        }
        return changed;
    }

    private bool ApplyPathSegmentMetadata()
    {
        CameraPath? before = pathEdit?.DeepClone();
        if (!CapturePathSegmentMetadataInputs())
        {
            return true;
        }

        RecordPathEdit(before);
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
        SyncPathTimingText();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private bool CapturePathSegmentMetadataInputs()
    {
        int index = SelectedPathKeyframeIndex();
        if (pathEdit is null || index < 0 || index >= pathEdit.Keyframes.Count)
        {
            return false;
        }

        GuiComposer composer = Composers[InspectorKey];
        CameraKeyframe frame = pathEdit.Keyframes[index];
        string marker = composer.GetTextInput("pathSegmentMarker")?.GetText()?.Trim()
            ?? frame.Marker;
        GuiElementNumberInput? startInput = composer.GetNumberInput("pathSegmentStartSpeed");
        GuiElementNumberInput? endInput = composer.GetNumberInput("pathSegmentEndSpeed");
        if (index < pathEdit.Keyframes.Count - 1 && (startInput is not null || endInput is not null))
        {
            double startSpeed = startInput is null
                ? frame.SegmentStartSpeed
                : ParseDouble(startInput.GetText(), double.NaN);
            double endSpeed = endInput is null
                ? frame.SegmentEndSpeed
                : ParseDouble(endInput.GetText(), double.NaN);
            if (!CameraPathTiming.TrySetSegmentSpeeds(
                pathEdit,
                index,
                startSpeed,
                endSpeed,
                out _,
                out _))
            {
                capi.ShowChatMessage(Lang.Get("vsdirector:invalid-segment-speed"));
                return false;
            }
        }

        frame.Marker = marker;
        return true;
    }

    private bool InsertPathKeyframe()
    {
        if (pathEdit is null)
        {
            return true;
        }

        CameraPath before = pathEdit.DeepClone();
        GuiComposer composer = Composers[InspectorKey];
        double start = ParseDouble(
            composer.GetNumberInput("pathInsertStart")?.GetText(),
            previewPathTime);
        double duration = ParseDouble(
            composer.GetNumberInput("pathInsertDuration")?.GetText(),
            CameraPathTiming.DefaultKeyframeDurationSeconds);
        if (SelectedPathFrame() is not null && !CapturePathSegmentMetadataInputs())
        {
            return true;
        }
        if (SelectedPathFrame() is not null)
        {
            _ = CapturePathTimingInputs();
        }
        if (owner.TryCapturePathKeyframePose(
            pathEdit,
            start,
            out VSDirector.Data.CameraKeyframe frame,
            out _,
            preferBasePathPreview: true))
        {
            selectedPathKeyframeIndex = CameraPathTiming.InsertKeyframeAtTime(
                pathEdit,
                frame,
                start,
                duration);
            previewPathTime = pathEdit.Keyframes[selectedPathKeyframeIndex].TimeSeconds;
            pathInsertStartText = previewPathTime.ToString("0.###", CultureInfo.InvariantCulture);
            pathInsertDurationText = Math.Max(
                CameraPathTiming.MinKeyframeGapSeconds,
                duration).ToString("0.###", CultureInfo.InvariantCulture);
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
            SyncPathTimingText();
            if (section == DirectorWorkspaceSection.Replay)
            {
                ComposeResources();
            }
            ComposeInspector();
            ComposeTimeline();
        }
        RecordPathEdit(before);
        return true;
    }

    private bool RecaptureSelectedPathKeyframe()
    {
        int index = SelectedPathKeyframeIndex();
        if (pathEdit is null || index < 0 || index >= pathEdit.Keyframes.Count)
        {
            return true;
        }

        CameraPath before = pathEdit.DeepClone();
        if (!CapturePathSegmentMetadataInputs())
        {
            return true;
        }
        _ = CapturePathTimingInputs();
        VSDirector.Data.CameraKeyframe existing = pathEdit.Keyframes[index];
        if (owner.TryCapturePathKeyframePose(
            pathEdit,
            existing.TimeSeconds,
            out VSDirector.Data.CameraKeyframe replacement,
            out _,
            preferBasePathPreview: true))
        {
            replacement.DurationSeconds = existing.DurationSeconds;
            replacement.Name = existing.Name;
            replacement.HoldSeconds = existing.HoldSeconds;
            replacement.InterpolationToNext = existing.InterpolationToNext;
            replacement.SegmentStartSpeed = existing.SegmentStartSpeed;
            replacement.SegmentEndSpeed = existing.SegmentEndSpeed;
            replacement.Marker = existing.Marker;
            replacement.BezierHandlesEnabled = existing.BezierHandlesEnabled;
            replacement.BezierInX = existing.BezierInX;
            replacement.BezierInY = existing.BezierInY;
            replacement.BezierInZ = existing.BezierInZ;
            replacement.BezierOutX = existing.BezierOutX;
            replacement.BezierOutY = existing.BezierOutY;
            replacement.BezierOutZ = existing.BezierOutZ;
            pathEdit.Keyframes[index] = replacement;
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
            SyncPathTimingText();
            if (section == DirectorWorkspaceSection.Replay)
            {
                ComposeResources();
            }
            ComposeInspector();
            ComposeTimeline();
        }
        RecordPathEdit(before);
        return true;
    }

    private bool DeletePathKeyframe()
    {
        if (pathEdit is null || pathEdit.Keyframes.Count == 0)
        {
            return true;
        }

        int index = SelectedPathKeyframeIndex();
        if (index >= 0 && index < pathEdit.Keyframes.Count)
        {
            CameraPath before = pathEdit.DeepClone();
            _ = CameraPathTiming.RemoveKeyframesPreservingTimeline(pathEdit, new[] { index });
            RecordPathEdit(before);
            selectedPathKeyframeIndex = Math.Clamp(
                index,
                0,
                Math.Max(0, pathEdit.Keyframes.Count - 1));
            selectedPathKeyframes.Clear();
            if (pathEdit.Keyframes.Count > 0)
            {
                selectedPathKeyframes.Add(selectedPathKeyframeIndex);
            }
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
            SyncPathTimingText();
            if (section == DirectorWorkspaceSection.Replay)
            {
                ComposeResources();
            }
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool SelectPreviousPathKeyframe()
    {
        FlushPendingPathAutoSave();
        if (pathEdit is not null && pathEdit.Keyframes.Count > 0)
        {
            CameraPath before = pathEdit.DeepClone();
            if (!CaptureCurrentPathInspectorInputs())
            {
                return true;
            }
            RecordPathEdit(before);
            SelectOnlyPathKeyframe(Math.Max(0, SelectedPathKeyframeIndex() - 1));
            SetPathAuthoringPlayhead(
                pathEdit.Keyframes[selectedPathKeyframeIndex].TimeSeconds,
                refreshPathMarker: true);
            SyncPathTimingText();
            ComposeResources();
            ComposeInspector();
            ComposeTimeline();
        }

        return true;
    }

    private bool SelectNextPathKeyframe()
    {
        FlushPendingPathAutoSave();
        if (pathEdit is not null && pathEdit.Keyframes.Count > 0)
        {
            CameraPath before = pathEdit.DeepClone();
            if (!CaptureCurrentPathInspectorInputs())
            {
                return true;
            }
            RecordPathEdit(before);
            SelectOnlyPathKeyframe(Math.Min(
                pathEdit.Keyframes.Count - 1,
                SelectedPathKeyframeIndex() + 1));
            SetPathAuthoringPlayhead(
                pathEdit.Keyframes[selectedPathKeyframeIndex].TimeSeconds,
                refreshPathMarker: true);
            SyncPathTimingText();
            ComposeResources();
            ComposeInspector();
            ComposeTimeline();
        }

        return true;
    }

    private bool DuplicatePathKeyframe()
    {
        int index = SelectedPathKeyframeIndex();
        if (pathEdit is null || index < 0 || index >= pathEdit.Keyframes.Count)
        {
            return true;
        }

        CameraPath before = pathEdit.DeepClone();
        if (!CapturePathSegmentMetadataInputs())
        {
            return true;
        }
        _ = CapturePathTimingInputs();
        VSDirector.Data.CameraKeyframe duplicate = pathEdit.Keyframes[index].DeepClone();
        double duration = Math.Max(
            CameraPathTiming.MinKeyframeGapSeconds,
            duplicate.DurationSeconds > 0d
                ? duplicate.DurationSeconds
                : CameraPathTiming.DefaultKeyframeDurationSeconds);
        selectedPathKeyframeIndex = CameraPathTiming.InsertKeyframe(
            pathEdit,
            index + 1,
            duplicate,
            duration);
        RecordPathEdit(before);
        SelectOnlyPathKeyframe(selectedPathKeyframeIndex);
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
        SyncPathTimingText();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private bool ClosePathEditorSelection()
    {
        FlushPendingPathAutoSave();
        PreserveInputs();
        pathEdit = null;
        selectedId = null;
        selectedPathKeyframeIndex = -1;
        selectedPathKeyframes.Clear();
        pathEditHistory.Clear();
        pendingPathEditHistory = null;
        ResetPathEditorInteraction();
        owner.SetPathEditPreview(null);
        resourceScrollOffset = 0;
        ComposeAll();
        return true;
    }

    private bool SelectPathKeyframeFromList(int index)
    {
        FlushPendingPathAutoSave();
        CameraPath? before = pathEdit?.DeepClone();
        if (!CaptureCurrentPathInspectorInputs())
        {
            return true;
        }
        RecordPathEdit(before);
        SelectOnlyPathKeyframe(index);
        if (pathEdit?.Keyframes.ElementAtOrDefault(index) is CameraKeyframe selected)
        {
            SetPathAuthoringPlayhead(selected.TimeSeconds, refreshPathMarker: true);
        }
        SyncPathTimingText();
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            pathEditorPlayhead,
            selectedKeyframes: selectedPathKeyframes);
        ComposeResources();
        ComposeInspector();
        ComposeWorkflowCanvas();
        return true;
    }

    private bool AddPathKeyframeAtPlayhead()
    {
        if (pathEdit is not null)
        {
            pendingPathEditHistory = pathEdit.DeepClone();
            if (!CaptureCurrentPathInspectorInputs())
            {
                pendingPathEditHistory = null;
                return true;
            }
        }
        return AddPathKeyframeAtTime(pathEditorPlayhead, useAuthoringCursor: true);
    }

    private bool AddSimplePathKeyframe()
    {
        if (pathEdit is null)
        {
            return true;
        }
        pendingPathEditHistory = pathEdit.DeepClone();
        if (!CaptureCurrentPathInspectorInputs())
        {
            pendingPathEditHistory = null;
            return true;
        }
        return AddPathKeyframeAtTime(pathEditorPlayhead, useAuthoringCursor: true);
    }

    private bool StartSimplePathFreeCamera()
    {
        if (pathEdit is not null
            && owner.TryStartPathFreeCamera(pathEdit, pathEditorPlayhead, out _))
        {
            EnterNavigationMode();
        }
        return true;
    }

    private void SetPathAuthoringPlayhead(
        double timeSeconds,
        bool refreshPathMarker,
        bool explicitlyPositioned = true)
    {
        if (pathEdit is null)
        {
            return;
        }

        pathEditorPlayhead = CameraPathAuthoring.SnapTime(timeSeconds);
        previewPathTime = pathEditorPlayhead;
        pathInsertStartText = pathEditorPlayhead.ToString("0.###", CultureInfo.InvariantCulture);
        pathEditorPlayheadWasPositioned = explicitlyPositioned;
        if (refreshPathMarker)
        {
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                pathEditorPlayhead,
                selectedPathKeyframes);
        }
        Composers[TimelineKey]?.GetDynamicText("pathStatus")?.SetNewText(
            PathSimpleTimelineStatusText(), false, true, false);
    }

    private bool AddPathKeyframeAtTime(double timeSeconds, bool useAuthoringCursor = false)
    {
        if (pathEdit is null)
        {
            return true;
        }

        CameraPath before = pendingPathEditHistory ?? pathEdit.DeepClone();
        pendingPathEditHistory = null;
        double time = useAuthoringCursor
            ? CameraPathAuthoring.ResolveCaptureTime(
                pathEdit,
                timeSeconds,
                pathEditorPlayheadWasPositioned)
            : CameraPathAuthoring.SnapTime(timeSeconds);
        if (owner.TryCapturePathKeyframePose(pathEdit, time,
                out CameraKeyframe frame, out _, preferBasePathPreview: true))
        {
            int index = CameraPathTiming.UpsertKeyframe(pathEdit, frame);
            SelectOnlyPathKeyframe(index);
            SetPathAuthoringPlayhead(
                CameraPathAuthoring.NextAppendTime(pathEdit),
                refreshPathMarker: false,
                explicitlyPositioned: false);
            RecordPathEdit(before);
            FinishPathEditorInteraction(recomposeResources: true);
            return true;
        }
        RecordPathEdit(before);
        return true;
    }

    private bool DeleteSelectedPathKeyframes()
    {
        FlushPendingPathAutoSave();
        if (pathEdit is null || selectedPathKeyframes.Count == 0)
        {
            return true;
        }

        CameraPath before = pathEdit.DeepClone();
        int[] indices = selectedPathKeyframes
            .Where(index => index >= 0 && index < pathEdit.Keyframes.Count)
            .ToArray();
        if (indices.Length == 0)
        {
            selectedPathKeyframes.Clear();
            return true;
        }

        int fallback = indices.Min();
        _ = CameraPathTiming.RemoveKeyframesPreservingTimeline(pathEdit, indices);
        selectedPathKeyframes.Clear();
        if (pathEdit.Keyframes.Count > 0)
        {
            SelectOnlyPathKeyframe(Math.Clamp(fallback, 0, pathEdit.Keyframes.Count - 1));
            pathEditorPlayhead = pathEdit.Keyframes[selectedPathKeyframeIndex].TimeSeconds;
        }
        else
        {
            selectedPathKeyframeIndex = -1;
        }
        RecordPathEdit(before);
        FinishPathEditorInteraction(recomposeResources: true);
        return true;
    }

    private bool DeleteSelectedPathSegment()
    {
        FlushPendingPathAutoSave();
        int index = SelectedPathKeyframeIndex();
        if (pathEdit is null
            || index < 0
            || index >= pathEdit.Keyframes.Count)
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:path-segment-delete-invalid"));
            return true;
        }

        CameraPath before = pathEdit.DeepClone();
        if (!CameraPathTiming.TryDeleteSegment(pathEdit, index))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:path-segment-delete-invalid"));
            return true;
        }

        selectedPathKeyframes.Clear();
        if (pathEdit.Keyframes.Count > 0)
        {
            SelectOnlyPathKeyframe(Math.Min(index, pathEdit.Keyframes.Count - 1));
            pathEditorPlayhead = pathEdit.Keyframes[selectedPathKeyframeIndex].TimeSeconds;
        }
        else
        {
            selectedPathKeyframeIndex = -1;
        }
        RecordPathEdit(before);
        FinishPathEditorInteraction(recomposeResources: true);
        return true;
    }

    private bool CopySelectedPathKeyframe()
    {
        if (pathEdit is null || selectedPathKeyframes.Count == 0)
        {
            return true;
        }
        int[] indices = selectedPathKeyframes
            .Where(index => index >= 0 && index < pathEdit.Keyframes.Count)
            .OrderBy(index => pathEdit.Keyframes[index].TimeSeconds)
            .ToArray();
        pathKeyframeClipboard.Clear();
        if (indices.Length > 0)
        {
            double firstTime = pathEdit.Keyframes[indices[0]].TimeSeconds;
            foreach (int index in indices)
            {
                CameraKeyframe copy = pathEdit.Keyframes[index].DeepClone();
                copy.TimeSeconds -= firstTime;
                pathKeyframeClipboard.Add(copy);
            }
        }
        ComposeWorkflowCanvas();
        return true;
    }

    private bool PastePathKeyframeAtPlayhead()
    {
        if (pathEdit is null || pathKeyframeClipboard.Count == 0)
        {
            return true;
        }

        CameraPath before = pathEdit.DeepClone();
        double pasteStart = SnapPathEditorTime(pathEditorPlayhead, excludeSelection: false);
        selectedPathKeyframes.Clear();
        foreach (CameraKeyframe source in pathKeyframeClipboard)
        {
            CameraKeyframe pasted = source.DeepClone();
            double target = Math.Min(
                CameraPathTiming.MaxTimelineSeconds,
                pasteStart + Math.Max(0d, source.TimeSeconds));
            int index = CameraPathTiming.InsertKeyframeAtTime(
                pathEdit,
                pasted,
                target,
                Math.Max(CameraPathTiming.MinKeyframeGapSeconds, pasted.DurationSeconds));
            selectedPathKeyframes.Add(index);
        }
        selectedPathKeyframeIndex = selectedPathKeyframes.Min();
        pathEditorPlayhead = pathEdit.Keyframes[selectedPathKeyframeIndex].TimeSeconds;
        RecordPathEdit(before);
        FinishPathEditorInteraction(recomposeResources: true);
        return true;
    }

    private bool SelectAllPathKeyframes()
    {
        selectedPathKeyframes.Clear();
        if (pathEdit is not null)
        {
            for (int index = 0; index < pathEdit.Keyframes.Count; index++)
            {
                selectedPathKeyframes.Add(index);
            }
            selectedPathKeyframeIndex = pathEdit.Keyframes.Count > 0 ? 0 : -1;
        }
        FinishPathEditorInteraction(recomposeResources: true);
        return true;
    }

    private bool ClearPathKeyframeSelection()
    {
        if (selectedPathKeyframes.Count == 0 && selectedPathKeyframeIndex < 0)
        {
            return true;
        }

        selectedPathKeyframes.Clear();
        selectedPathKeyframeIndex = -1;
        pathEditorRangeBaseSelection.Clear();
        pathEditorHoverTime = double.NaN;
        FinishPathEditorInteraction(recomposeResources: true);
        return true;
    }

    private bool SetPathWorkspaceTool(PathWorkspaceTool tool)
    {
        pathWorkspaceTool = tool;
        ComposeWorkflowCanvas();
        return true;
    }

    private bool ToggleSelectedPathMarker()
    {
        if (pathEdit is null || selectedPathKeyframes.Count == 0)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        foreach (int index in selectedPathKeyframes.Where(index => index < pathEdit.Keyframes.Count))
        {
            CameraKeyframe frame = pathEdit.Keyframes[index];
            frame.Marker = string.IsNullOrWhiteSpace(frame.Marker)
                ? Lang.Get("vsdirector:path-segment-marker-default", index + 1)
                : string.Empty;
        }
        RecordPathEdit(before);
        FinishPathEditorInteraction(recomposeResources: true);
        return true;
    }

    private bool TogglePathWorkspacePreview()
    {
        if (pathEdit is not null)
        {
            SetPathPreviewVisible(!pathEdit.PreviewVisible);
            ComposeWorkflowCanvas();
        }
        return true;
    }

    private void SetPathPreviewVisible(bool visible)
    {
        if (pathEdit is null || pathEdit.PreviewVisible == visible)
        {
            return;
        }
        if (!owner.TrySetPathPreviewVisible(pathEdit.Id, visible, out _))
        {
            return;
        }
        pathEdit.PreviewVisible = visible;
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            pathEditorPlayhead,
            selectedKeyframes: selectedPathKeyframes);
    }

    private void OnPathCurveChannelChanged(string value, bool selected)
    {
        if (selected)
        {
            pathCurveChannelCode = value;
        }
    }

    private void OnPathBatchScopeChanged(string value, bool selected)
    {
        if (selected)
        {
            pathBatchScopeCode = value;
            if (floatingEditorDialog.Composer is GuiComposer composer)
            {
                bool enabled = PathBatchTargetIndices().Count > 0;
                SetButtonEnabledIfPresent(composer, "pathBatchApply", enabled);
                SetButtonEnabledIfPresent(composer, "pathBatchInterpolationApply", enabled);
            }
        }
    }

    private void OnPathBatchPropertyChanged(string value, bool selected)
    {
        if (selected)
        {
            pathBatchPropertyCode = value;
        }
    }

    private void OnPathBatchInterpolationChanged(string value, bool selected)
    {
        if (selected)
        {
            pathBatchInterpolationCode = value;
        }
    }

    private bool ApplyPathBatchValue()
    {
        if (pathEdit is null || floatingEditorDialog.Composer is not GuiComposer composer)
        {
            return true;
        }

        pathBatchValueText = composer.GetNumberInput("pathBatchValue")?.GetText()
            ?? pathBatchValueText;
        if (!double.TryParse(pathBatchValueText, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double value)
            || !double.IsFinite(value))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:path-batch-invalid-value"));
            return true;
        }

        List<int> targets = PathBatchTargetIndices();
        if (targets.Count == 0 || !ValidatePathBatchValue(targets, value))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:path-batch-invalid-value"));
            return true;
        }

        CameraPath before = pathEdit.DeepClone();
        foreach (int index in targets)
        {
            CameraKeyframe frame = pathEdit.Keyframes[index];
            switch (pathBatchPropertyCode)
            {
                case "yaw":
                    frame.Yaw = CameraPathOrientation.PreserveDegrees(value, frame.Yaw);
                    break;
                case "pitch":
                    frame.Pitch = CameraPathOrientation.PreserveDegrees(value, frame.Pitch);
                    break;
                case "roll":
                    frame.Roll = CameraPathOrientation.PreserveDegrees(value, frame.Roll);
                    break;
                case "hold":
                    _ = CameraPathTiming.TrySetKeyframeHold(pathEdit, index, value, out _);
                    break;
                case "startspeed":
                    _ = CameraPathTiming.TrySetSegmentSpeeds(
                        pathEdit, index, value, frame.SegmentEndSpeed, out _, out _);
                    break;
                case "endspeed":
                    _ = CameraPathTiming.TrySetSegmentSpeeds(
                        pathEdit, index, frame.SegmentStartSpeed, value, out _, out _);
                    break;
                default:
                    frame.Fov = (float)value;
                    break;
            }
        }
        RecordPathEdit(before);
        FinishPathEditorInteraction(recomposeResources: true);
        return true;
    }

    private bool ApplyPathBatchInterpolation()
    {
        if (pathEdit is null)
        {
            return true;
        }
        List<int> targets = PathBatchTargetIndices();
        if (targets.Count == 0)
        {
            return true;
        }

        CameraPathInterpolation? interpolation = pathBatchInterpolationCode switch
        {
            "linear" => CameraPathInterpolation.Linear,
            "ease" => CameraPathInterpolation.EaseInOut,
            "bezier" => CameraPathInterpolation.Bezier,
            "step" => CameraPathInterpolation.Step,
            _ => null
        };
        CameraPath before = pathEdit.DeepClone();
        foreach (int index in targets)
        {
            pathEdit.Keyframes[index].InterpolationToNext = interpolation;
        }
        RecordPathEdit(before);
        FinishPathEditorInteraction(recomposeResources: true);
        return true;
    }

    private bool ValidatePathBatchValue(IReadOnlyList<int> targets, double value)
    {
        if (pathEdit is null)
        {
            return false;
        }
        return pathBatchPropertyCode switch
        {
            "fov" => value >= 10d && value <= 170d,
            "hold" => value >= 0d && value <= CameraPathTiming.MaxTimelineSeconds,
            "startspeed" => value >= CameraPathTiming.MinSegmentSpeed
                && value <= CameraPathTiming.MaxSegmentSpeed
                && targets.All(index => index >= pathEdit.Keyframes.Count - 1
                    || value + pathEdit.Keyframes[index].SegmentEndSpeed
                        > CameraPathTiming.TimeMatchEpsilon),
            "endspeed" => value >= CameraPathTiming.MinSegmentSpeed
                && value <= CameraPathTiming.MaxSegmentSpeed
                && targets.All(index => index >= pathEdit.Keyframes.Count - 1
                    || pathEdit.Keyframes[index].SegmentStartSpeed + value
                        > CameraPathTiming.TimeMatchEpsilon),
            _ => value >= -float.MaxValue && value <= float.MaxValue
        };
    }

    private List<int> PathBatchTargetIndices()
    {
        if (pathEdit is null)
        {
            return new List<int>();
        }
        return pathBatchScopeCode == "all"
            ? Enumerable.Range(0, pathEdit.Keyframes.Count).ToList()
            : selectedPathKeyframes
                .Where(index => index >= 0 && index < pathEdit.Keyframes.Count)
                .OrderBy(index => index)
                .ToList();
    }

    private bool UndoPathEdit()
    {
        if (pathEdit is not null && pathEditHistory.TryUndo(pathEdit, out CameraPath restored))
        {
            RestorePathHistory(restored);
        }
        return true;
    }

    private bool RedoPathEdit()
    {
        if (pathEdit is not null && pathEditHistory.TryRedo(pathEdit, out CameraPath restored))
        {
            RestorePathHistory(restored);
        }
        return true;
    }

    private void RestorePathHistory(CameraPath restored)
    {
        pathEdit = restored;
        pendingPathEditHistory = null;
        selectedPathKeyframes.RemoveWhere(index => index < 0 || index >= pathEdit.Keyframes.Count);
        if (selectedPathKeyframes.Count == 0 && pathEdit.Keyframes.Count > 0)
        {
            selectedPathKeyframeIndex = Math.Clamp(
                selectedPathKeyframeIndex, 0, pathEdit.Keyframes.Count - 1);
            selectedPathKeyframes.Add(selectedPathKeyframeIndex);
        }
        else
        {
            selectedPathKeyframeIndex = selectedPathKeyframes.Count > 0
                ? selectedPathKeyframes.Min()
                : -1;
        }
        pathEditorPlayhead = SelectedPathFrame()?.TimeSeconds
            ?? Math.Min(pathEditorPlayhead, pathEdit.DurationSeconds);
        SyncPathTimingText();
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
        ComposeResources();
        ComposeInspector();
        ComposeTimeline();
    }

    private void RecordPathEdit(CameraPath? before, string? mergeKey = null)
    {
        if (before is not null && pathEdit is not null)
        {
            _ = pathEditHistory.RecordChange(before, pathEdit, mergeKey);
            if (floatingEditorDialog.Composer is GuiComposer composer)
            {
                SetButtonEnabledIfPresent(composer, "pathUndo", pathEditHistory.CanUndo);
                SetButtonEnabledIfPresent(composer, "pathRedo", pathEditHistory.CanRedo);
            }
        }
    }

    private static bool IsWorkspaceDropDownOpen()
        => DirectorDropdownInputCapture.OpenPopup is not null;

    private bool ExportPathJsonToClipboard()
    {
        if (pathEdit is not null
            && owner.TryExportPathJson(pathEdit, out string json, out _))
        {
            capi.Input.ClipboardText = json;
        }
        return true;
    }

    private bool ImportPathJsonFromClipboard()
    {
        if (owner.TryImportPathJson(capi.Input.ClipboardText, out Guid importedId, out _))
        {
            SelectPath(importedId);
            Refresh();
        }
        return true;
    }

    private bool PlayEditedPath(bool reverse)
    {
        if (pathEdit is not null && TryCommitPathEditor(captureTiming: true))
        {
            if (section == DirectorWorkspaceSection.Replay)
            {
                replayViewMode = "path";
                replayViewSourceId = pathEdit.Id;
                _ = ApplyReplayView();
            }
            else
            {
                PlayPathFromWorkspace(pathEdit.Id, reverse);
            }
        }
        return true;
    }

    private bool DeletePathEditor()
        => pathEdit is null || ConfirmDelete(pathEdit.Id, () => owner.TryDeletePath(pathEdit.Id, out _));

    private void OnPathInterpolationChanged(string value, bool selected)
    {
        if (selected && pathEdit is not null)
        {
            CameraPath before = pathEdit.DeepClone();
            pathEdit.Interpolation = value switch
            {
                "ease" => CameraPathInterpolation.EaseInOut,
                "bezier" => CameraPathInterpolation.Bezier,
                "step" => CameraPathInterpolation.Step,
                _ => CameraPathInterpolation.Linear
            };
            RecordPathEdit(before);
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
            QueueAutoSave(section == DirectorWorkspaceSection.Paths
                ? AutoSaveTarget.Path
                : AutoSaveTarget.PathKeyframe);
        }
    }

    private void OnSegmentInterpolationChanged(string value, bool selected)
    {
        int index = SelectedPathKeyframeIndex();
        if (!selected
            || pathEdit is null
            || index < 0
            || index >= pathEdit.Keyframes.Count)
        {
            return;
        }

        CameraPath before = pathEdit.DeepClone();
        pathEdit.Keyframes[index].InterpolationToNext = value switch
        {
            "linear" => CameraPathInterpolation.Linear,
            "ease" => CameraPathInterpolation.EaseInOut,
            "bezier" => CameraPathInterpolation.Bezier,
            "step" => CameraPathInterpolation.Step,
            _ => null
        };
        RecordPathEdit(before);
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
        QueueAutoSave(section == DirectorWorkspaceSection.Paths
            ? AutoSaveTarget.Path
            : AutoSaveTarget.PathKeyframe);
        ComposeTimeline();
    }

    private void OnPathLoopChanged(string value, bool selected)
    {
        if (selected && pathEdit is not null)
        {
            CameraPath before = pathEdit.DeepClone();
            pathEdit.LoopMode = value switch
            {
                "loop" => CameraPathLoopMode.Loop,
                "pingpong" => CameraPathLoopMode.PingPong,
                _ => CameraPathLoopMode.Once
            };
            RecordPathEdit(before);
            QueueAutoSave(section == DirectorWorkspaceSection.Paths
                ? AutoSaveTarget.Path
                : AutoSaveTarget.PathKeyframe);
        }
    }

    private bool OnPathSpeedChanged(int value)
    {
        if (pathEdit is not null)
        {
            double next = CameraPathTiming.NormalizePlaybackSpeed(value / 100d);
            if (Math.Abs(next - pathEdit.PlaybackSpeed) <= 0.000001d)
            {
                return true;
            }
            CameraPath before = pathEdit.DeepClone();
            pathEdit.PlaybackSpeed = next;
            RecordPathEdit(before, "path-speed");
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
            QueueAutoSave(section == DirectorWorkspaceSection.Paths
                ? AutoSaveTarget.Path
                : AutoSaveTarget.PathKeyframe);
        }
        return true;
    }

    private void OnPathTotalDurationChanged(string value)
    {
        _ = value;
        if (suppressAutoSave)
        {
            return;
        }

        pathTotalDurationDirty = true;
        QueueAutoSave(AutoSaveTarget.Path);
    }

    private bool SaveSequenceEditor()
    {
        if (TryCommitSequenceEditor())
        {
            Refresh();
        }
        return true;
    }

    private bool TryCommitSequenceEditor()
    {
        if (sequenceEdit is null)
        {
            return false;
        }

        GuiComposer composer = Composers[InspectorKey];
        sequenceEdit.Name = composer.GetTextInput("sequenceName")?.GetText() ?? sequenceEdit.Name;
        if (!CaptureSequenceDurations())
        {
            return false;
        }
        if (!owner.TrySaveSequence(sequenceEdit, out _))
        {
            return false;
        }
        sequenceEdit = owner.FindSequence(sequenceEdit.Id) ?? sequenceEdit;
        return true;
    }

    private bool AddSequenceShot()
    {
        if (sequenceEdit is null)
        {
            return true;
        }

        if (!CaptureSequenceDurations())
        {
            return true;
        }
        GuiComposer composer = Composers[InspectorKey];
        Guid cameraId = SelectedSequenceCameraId();
        double duration = ParseDouble(composer.GetNumberInput("shotDuration")?.GetText(), double.NaN);
        if (!IsValidSequenceDuration(duration))
        {
            ShowInvalidSequenceDuration();
            return true;
        }
        if (cameraId != Guid.Empty)
        {
            sequenceEdit.Shots.Add(new CameraSequenceShot { CameraId = cameraId, DurationSeconds = duration });
            PersistSequenceStructureChange();
            sequencePageIndex = PageCount(sequenceEdit.Shots.Count, CurrentSequenceRowsPerPage) - 1;
            ComposeInspector();
            if (section == DirectorWorkspaceSection.Replay)
            {
                ComposeTimeline();
            }
        }
        return true;
    }

    private bool MoveSequenceShot(int index, int delta)
    {
        if (sequenceEdit is null || index < 0 || index >= sequenceEdit.Shots.Count)
        {
            return true;
        }

        if (!CaptureSequenceDurations())
        {
            return true;
        }
        int target = index + delta;
        if (target < 0 || target >= sequenceEdit.Shots.Count)
        {
            return true;
        }
        CameraSequenceShot shot = sequenceEdit.Shots[index];
        sequenceEdit.Shots.RemoveAt(index);
        sequenceEdit.Shots.Insert(target, shot);
        PersistSequenceStructureChange();
        sequencePageIndex = target / CurrentSequenceRowsPerPage;
        ComposeInspector();
        if (section == DirectorWorkspaceSection.Replay)
        {
            ComposeTimeline();
        }
        return true;
    }

    private bool DeleteSequenceShot(int index)
    {
        if (sequenceEdit is not null && index >= 0 && index < sequenceEdit.Shots.Count)
        {
            if (!CaptureSequenceDurations())
            {
                return true;
            }
            sequenceEdit.Shots.RemoveAt(index);
            PersistSequenceStructureChange();
            sequencePageIndex = Math.Clamp(
                sequencePageIndex,
                0,
                PageCount(sequenceEdit.Shots.Count, CurrentSequenceRowsPerPage) - 1);
            ComposeInspector();
            if (section == DirectorWorkspaceSection.Replay)
            {
                ComposeTimeline();
            }
        }
        return true;
    }

    private void PersistSequenceStructureChange()
    {
        if (sequenceEdit is null)
        {
            return;
        }

        sequenceEdit.Name = Composers[InspectorKey].GetTextInput("sequenceName")?.GetText()
            ?? sequenceEdit.Name;
        pendingAutoSaveTarget = AutoSaveTarget.None;
        autoSaveAccumulator = 0f;
        autoSaveState = AutoSaveState.Saving;
        UpdateAutoSaveStatusText();
        Guid sequenceId = sequenceEdit.Id;
        bool saved = owner.TrySaveSequence(sequenceEdit, out autoSaveError, showNotification: false);
        autoSaveState = saved ? AutoSaveState.Saved : AutoSaveState.Invalid;
        if (saved)
        {
            autoSaveError = string.Empty;
            sequenceEdit = owner.FindSequence(sequenceId) ?? sequenceEdit;
        }
        UpdateAutoSaveStatusText();
    }

    private void OnSequenceLoopChanged(string value, bool selected)
    {
        if (selected && sequenceEdit is not null)
        {
            sequenceEdit.LoopMode = value == "loop" ? CameraSequenceLoopMode.Loop : CameraSequenceLoopMode.Once;
            QueueAutoSave(AutoSaveTarget.Sequence);
        }
    }

    private void OnSequenceCameraChanged(string value, bool selected)
    {
        if (selected && sequenceEdit is not null && Guid.TryParse(value, out Guid id))
        {
            selectedSequenceCameraId = id;
        }
    }

    private bool PlaySequenceEditor()
    {
        if (sequenceEdit is not null && TryCommitSequenceEditor())
        {
            if (section == DirectorWorkspaceSection.Replay)
            {
                replayViewMode = "sequence";
                replayViewSourceId = sequenceEdit.Id;
                _ = ApplyReplayView();
            }
            else if (owner.TryPlaySequence(sequenceEdit.Id))
            {
                EnterNavigationMode();
            }
        }
        return true;
    }

    private bool ChangeSequencePage(int delta)
    {
        if (sequenceEdit is null)
        {
            return true;
        }

        if (!CaptureSequenceDurations())
        {
            return true;
        }
        int pages = PageCount(sequenceEdit.Shots.Count, CurrentSequenceRowsPerPage);
        sequencePageIndex = Math.Clamp(sequencePageIndex + delta, 0, pages - 1);
        ComposeInspector();
        return true;
    }

    private bool DeleteSequenceEditor()
        => sequenceEdit is null || ConfirmDelete(sequenceEdit.Id,
            () => owner.TryDeleteSequence(sequenceEdit.Id, out _));

    private bool CaptureSequenceDurations()
    {
        if (sequenceEdit is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return false;
        }

        int first = sequencePageIndex * CurrentSequenceRowsPerPage;
        int last = Math.Min(first + CurrentSequenceRowsPerPage, sequenceEdit.Shots.Count);
        for (int index = first; index < last; index++)
        {
            if (composer.GetNumberInput("shot-" + index) is not { } input)
            {
                continue;
            }
            double duration = ParseDouble(
                input.GetText(),
                double.NaN);
            if (!IsValidSequenceDuration(duration))
            {
                ShowInvalidSequenceDuration();
                return false;
            }
            sequenceEdit.Shots[index].DurationSeconds = duration;
        }
        return true;
    }

    private static bool IsValidSequenceDuration(double duration)
        => double.IsFinite(duration)
            && duration >= CameraSequenceSampler.MinShotDurationSeconds
            && duration <= CameraSequenceSampler.MaxShotDurationSeconds;

    private void ShowInvalidSequenceDuration()
        => capi.ShowChatMessage(Lang.Get(
            "vsdirector:invalid-shot-duration",
            CameraSequenceSampler.MinShotDurationSeconds,
            CameraSequenceSampler.MaxShotDurationSeconds));

    private DirectorWorkflowCard? SelectedWorkflowCard()
    {
        if (workflowEdit is null)
        {
            return null;
        }

        DirectorWorkflowCard? selected = workflowEdit.Cards.FirstOrDefault(card => card.Id == selectedWorkflowCardId);
        if (selected is not null)
        {
            return selected;
        }

        selected = workflowEdit.Cards.FirstOrDefault();
        selectedWorkflowCardId = selected?.Id ?? Guid.Empty;
        return selected;
    }

    private bool SelectWorkflowContextCard()
    {
        FlushPendingAutoSave(AutoSaveTarget.WorkflowCard);
        if (workflowEdit is not null
            && contextWorkflowCardId != Guid.Empty
            && workflowEdit.Cards.Any(card => card.Id == contextWorkflowCardId))
        {
            selectedWorkflowCardId = contextWorkflowCardId;
            inspectorSection = "card";
        }
        contextWorkflowCardId = Guid.Empty;
        ComposeInspector();
        ComposeWorkflowCanvas();
        return true;
    }

    private bool EditWorkflowContextCard()
    {
        FlushPendingAutoSave(AutoSaveTarget.WorkflowCard);
        if (workflowEdit is not null
            && contextWorkflowCardId != Guid.Empty
            && workflowEdit.Cards.Any(card => card.Id == contextWorkflowCardId))
        {
            selectedWorkflowCardId = contextWorkflowCardId;
            inspectorSection = "card";
        }
        contextWorkflowCardId = Guid.Empty;
        layout.Panel("inspector").Collapsed = false;
        ComposeInspector();
        ComposeWorkflowCanvas();
        SaveLayout();
        return true;
    }

    private bool WorkflowContextMenuContains(double mouseX, double mouseY)
    {
        if (contextWorkflowCardId == Guid.Empty || workflowBoardBounds is null)
        {
            return false;
        }

        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        double left = workflowBoardBounds.absX + workflowContextX * scale;
        double top = workflowBoardBounds.absY + workflowContextY * scale;
        return mouseX >= left && mouseX <= left + 140d * scale
            && mouseY >= top && mouseY <= top + 38d * scale;
    }

    private bool ZoomWorkflow(double factor)
        => ZoomWorkflow(factor, null);

    private bool ZoomWorkflow(double factor, double? mouseX)
    {
        if (workflowEdit is null || !double.IsFinite(factor) || factor <= 0d)
        {
            return true;
        }

        WorkflowView before = CurrentWorkflowView(workflowEdit);
        double anchorFraction = 0.5d;
        if (mouseX is double pointerX && workflowBoardBounds is not null)
        {
            double left = workflowBoardBounds.absX + GuiElement.scaled(30d);
            double width = Math.Max(1d, workflowBoardBounds.OuterWidth - GuiElement.scaled(60d));
            anchorFraction = Math.Clamp((pointerX - left) / width, 0d, 1d);
        }
        double anchorTime = before.StartSeconds + before.DurationSeconds * anchorFraction;
        layout.WorkflowZoom = Math.Clamp(before.Zoom * factor, 0.5d, 12d);
        double newDuration = before.TimelineSeconds / layout.WorkflowZoom;
        layout.WorkflowPanSeconds = Math.Max(0d, anchorTime - newDuration * anchorFraction);
        _ = CurrentWorkflowView(workflowEdit);
        contextWorkflowCardId = Guid.Empty;
        SaveLayout();
        ComposeWorkflowCanvas();
        return true;
    }

    private bool FitWorkflowView()
    {
        layout.WorkflowZoom = 1d;
        layout.WorkflowPanSeconds = 0d;
        layout.WorkflowLaneOffset = 0;
        contextWorkflowCardId = Guid.Empty;
        SaveLayout();
        ComposeWorkflowCanvas();
        return true;
    }

    private void UpdateWorkflowPan(double mouseX)
    {
        if (workflowEdit is null || workflowBoardBounds is null)
        {
            return;
        }

        WorkflowView view = CurrentWorkflowView(workflowEdit);
        double usableWidth = Math.Max(1d, workflowBoardBounds.OuterWidth - GuiElement.scaled(60d));
        double deltaSeconds = (mouseX - workflowPanStartX) / usableWidth * view.DurationSeconds;
        layout.WorkflowPanSeconds = Math.Clamp(
            workflowPanStartSeconds - deltaSeconds,
            0d,
            Math.Max(0d, view.TimelineSeconds - view.DurationSeconds));
    }

    private bool NewWorkflowCard()
    {
        FlushPendingAutoSave(AutoSaveTarget.WorkflowCard);
        if (workflowEdit is null)
        {
            return true;
        }
        RecordingSourceOption? source = BuildRecordingSources()
            .FirstOrDefault(option => DirectorWorkflowPlanner.IsExportSource(option.Type));
        if (source is null || !Guid.TryParse(source.Selector, out Guid sourceId))
        {
            return true;
        }

        double start = workflowEdit.Cards
            .Where(card => card.Kind == DirectorWorkflowCardKind.Shot)
            .Select(card => card.EndSeconds)
            .DefaultIfEmpty(0d)
            .Max();
        DirectorWorkflowCard card = new()
        {
            Id = Guid.NewGuid(),
            Name = Lang.Get("vsdirector:workflow-default-card", workflowEdit.Cards.Count + 1),
            SourceType = source.Type,
            SourceId = sourceId,
            StartSeconds = start,
            EndSeconds = start + 5d,
            Lane = FindAvailableWorkflowLane(start, start + 5d),
            Order = workflowEdit.Cards.Count
        };
        workflowEdit.Cards.Add(card);
        selectedWorkflowCardId = card.Id;
        inspectorSection = "card";
        contextWorkflowCardId = Guid.Empty;
        return SaveWorkflowEditor(showMessage: false, captureInputs: false);
    }

    private bool NewWorkflowCountdown()
    {
        FlushPendingAutoSave(AutoSaveTarget.WorkflowCard);
        if (workflowEdit is null)
        {
            return true;
        }
        const double start = 0d;
        const double duration = 3d;
        DirectorWorkflowCard card = new()
        {
            Id = Guid.NewGuid(),
            Name = Lang.Get("vsdirector:workflow-default-countdown", workflowEdit.Cards.Count + 1),
            Kind = DirectorWorkflowCardKind.Countdown,
            SourceId = Guid.Empty,
            StartSeconds = start,
            EndSeconds = start + duration,
            Lane = FindAvailableWorkflowLane(start, start + duration),
            Order = workflowEdit.Cards.Count
        };
        workflowEdit.Cards.Add(card);
        selectedWorkflowCardId = card.Id;
        inspectorSection = "card";
        contextWorkflowCardId = Guid.Empty;
        return SaveWorkflowEditor(showMessage: false, captureInputs: false);
    }

    private bool ApplyWorkflowCard()
    {
        CaptureWorkflowInputs();
        return SaveWorkflowEditor(captureInputs: false);
    }

    private bool DuplicateWorkflowCard()
    {
        FlushPendingAutoSave(AutoSaveTarget.WorkflowCard);
        DirectorWorkflowCard? source = workflowEdit?.Cards.FirstOrDefault(card =>
            card.Id == (contextWorkflowCardId != Guid.Empty ? contextWorkflowCardId : selectedWorkflowCardId));
        source ??= SelectedWorkflowCard();
        if (workflowEdit is null || source is null)
        {
            return true;
        }

        CaptureWorkflowInputs();
        DirectorWorkflowCard duplicate = source.DeepClone();
        duplicate.Id = Guid.NewGuid();
        duplicate.Name = source.Name + " Copy";
        duplicate.StartSeconds = source.StartSeconds;
        duplicate.EndSeconds = source.EndSeconds;
        duplicate.Lane = FindAvailableWorkflowLane(
            duplicate.StartSeconds,
            duplicate.EndSeconds);
        duplicate.Order = workflowEdit.Cards.Count;
        workflowEdit.Cards.Add(duplicate);
        selectedWorkflowCardId = duplicate.Id;
        inspectorSection = "card";
        contextWorkflowCardId = Guid.Empty;
        return SaveWorkflowEditor(captureInputs: false);
    }

    private int FindAvailableWorkflowLane(double startSeconds, double endSeconds, Guid? ignoredCardId = null)
        => workflowEdit is null
            ? 0
            : DirectorWorkflowPlanner.FindAvailableLane(
                workflowEdit,
                startSeconds,
                endSeconds,
                ignoredCardId);

    private bool DeleteWorkflowCard()
    {
        FlushPendingAutoSave(AutoSaveTarget.WorkflowCard);
        if (workflowEdit is null)
        {
            return true;
        }

        Guid id = contextWorkflowCardId != Guid.Empty
            ? contextWorkflowCardId
            : selectedWorkflowCardId;
        workflowEdit.Cards.RemoveAll(card => card.Id == id);
        selectedWorkflowCardId = workflowEdit.Cards.FirstOrDefault()?.Id ?? Guid.Empty;
        contextWorkflowCardId = Guid.Empty;
        return SaveWorkflowEditor(captureInputs: false);
    }

    private bool SaveWorkflowEditor(bool showMessage = true, bool captureInputs = true)
    {
        if (workflowEdit is null)
        {
            return true;
        }

        if (captureInputs)
        {
            CaptureWorkflowInputs();
        }
        Guid workflowId = workflowEdit.Id;
        Guid cardId = selectedWorkflowCardId;
        if (owner.TrySaveWorkflow(workflowEdit, out _, showMessage))
        {
            workflowEdit = owner.FindWorkflow(workflowId);
            selectedWorkflowCardId = workflowEdit?.Cards.Any(card => card.Id == cardId) == true
                ? cardId
                : workflowEdit?.Cards.FirstOrDefault()?.Id ?? Guid.Empty;
        }
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private bool ToggleWorkflowAudioEditor()
    {
        PreserveInputs();
        workflowAudioEditorExpanded = true;
        inspectorSection = "audio";
        workflowAudioCreating = false;
        workflowAudioFieldsId = Guid.Empty;
        SyncWorkflowAudioSelection();
        ComposeInspector();
        return true;
    }

    private bool NewWorkflowAudioClip()
    {
        workflowAudioCreating = true;
        selectedWorkflowAudioClipId = Guid.Empty;
        workflowAudioFieldsId = Guid.Empty;
        workflowAudioPath = string.Empty;
        workflowAudioTimelineStart = "0";
        workflowAudioSourceStart = "0";
        workflowAudioDuration = "5";
        workflowAudioTrack = "0";
        workflowAudioGain = "0";
        workflowAudioFadeIn = "0";
        workflowAudioFadeOut = "0";
        workflowAudioRate = "1";
        workflowAudioEnabled = true;
        ComposeInspector();
        return true;
    }

    private void OnWorkflowAudioSelectionChanged(string value, bool selected)
    {
        if (selected && Guid.TryParse(value, out Guid id))
        {
            workflowAudioCreating = false;
            selectedWorkflowAudioClipId = id;
            workflowAudioFieldsId = Guid.Empty;
            SyncWorkflowAudioSelection();
            ComposeInspector();
        }
    }

    private void OnWorkflowAudioEnabledChanged(bool enabled)
        => workflowAudioEnabled = enabled;

    private void SyncWorkflowAudioSelection()
    {
        if (workflowEdit is null || workflowAudioCreating)
        {
            return;
        }

        DirectorTimelineAudioClip? clip = workflowEdit.AudioClips
            .FirstOrDefault(item => item.Id == selectedWorkflowAudioClipId)
            ?? workflowEdit.AudioClips.FirstOrDefault();
        selectedWorkflowAudioClipId = clip?.Id ?? Guid.Empty;
        if (clip is null || workflowAudioFieldsId == clip.Id)
        {
            return;
        }

        workflowAudioPath = clip.InputPath;
        workflowAudioTimelineStart = clip.TimelineStartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        workflowAudioSourceStart = clip.SourceStartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        workflowAudioDuration = clip.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        workflowAudioTrack = clip.Track.ToString(CultureInfo.InvariantCulture);
        workflowAudioGain = clip.GainDecibels.ToString("0.###", CultureInfo.InvariantCulture);
        workflowAudioFadeIn = clip.FadeInSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        workflowAudioFadeOut = clip.FadeOutSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        workflowAudioRate = clip.PlaybackRate.ToString("0.###", CultureInfo.InvariantCulture);
        workflowAudioEnabled = clip.Enabled;
        workflowAudioFieldsId = clip.Id;
    }

    private bool ApplyWorkflowAudioClip()
    {
        if (workflowEdit is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return true;
        }

        CaptureWorkflowAudioInputs(composer);
        if (!TryParseWorkflowAudioValues(
            out double timelineStart,
            out double sourceStart,
            out double duration,
            out int track,
            out double gain,
            out double fadeIn,
            out double fadeOut,
            out double rate))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:workflow-audio-range-invalid"));
            return true;
        }

        DirectorTimelineAudioClip? existing = workflowEdit.AudioClips
            .FirstOrDefault(clip => clip.Id == selectedWorkflowAudioClipId);
        string error;
        if (workflowAudioCreating || existing is null)
        {
            if (!DirectorWorkflowAudioEditor.TryCreate(
                workflowAudioPath,
                timelineStart,
                sourceStart,
                duration,
                track,
                gain,
                fadeIn,
                fadeOut,
                rate,
                out DirectorTimelineAudioClip created,
                out error))
            {
                ShowWorkflowAudioError(error);
                return true;
            }
            created.Enabled = workflowAudioEnabled;
            workflowEdit.AudioClips.Add(created);
            selectedWorkflowAudioClipId = created.Id;
        }
        else
        {
            if (!DirectorWorkflowAudioEditor.TryUpdate(
                existing,
                workflowAudioPath,
                timelineStart,
                sourceStart,
                duration,
                track,
                gain,
                fadeIn,
                fadeOut,
                rate,
                out error))
            {
                ShowWorkflowAudioError(error);
                return true;
            }
            existing.Enabled = workflowAudioEnabled;
        }

        workflowEdit.Name = composer.GetTextInput("workflowName")?.GetText() ?? workflowEdit.Name;
        workflowAudioCreating = false;
        workflowAudioFieldsId = Guid.Empty;
        Guid workflowId = workflowEdit.Id;
        if (owner.TrySaveWorkflow(workflowEdit, out _, showNotification: false))
        {
            workflowEdit = owner.FindWorkflow(workflowId) ?? workflowEdit;
            SyncWorkflowAudioSelection();
            ComposeInspector();
            ComposeWorkflowCanvas();
        }
        return true;
    }

    private bool DeleteWorkflowAudioClip()
    {
        if (workflowEdit is null || selectedWorkflowAudioClipId == Guid.Empty)
        {
            return true;
        }

        workflowEdit.AudioClips.RemoveAll(clip => clip.Id == selectedWorkflowAudioClipId);
        selectedWorkflowAudioClipId = Guid.Empty;
        workflowAudioFieldsId = Guid.Empty;
        workflowAudioCreating = false;
        Guid workflowId = workflowEdit.Id;
        if (owner.TrySaveWorkflow(workflowEdit, out _, showNotification: false))
        {
            workflowEdit = owner.FindWorkflow(workflowId) ?? workflowEdit;
            SyncWorkflowAudioSelection();
            ComposeInspector();
            ComposeWorkflowCanvas();
        }
        return true;
    }

    private void CaptureWorkflowAudioInputs(GuiComposer composer)
    {
        workflowAudioPath = composer.GetTextInput("workflowAudioPath")?.GetText()
            ?? workflowAudioPath;
        workflowAudioTimelineStart = composer.GetNumberInput("workflowAudioTimelineStart")?.GetText()
            ?? workflowAudioTimelineStart;
        workflowAudioSourceStart = composer.GetNumberInput("workflowAudioSourceStart")?.GetText()
            ?? workflowAudioSourceStart;
        workflowAudioDuration = composer.GetNumberInput("workflowAudioDuration")?.GetText()
            ?? workflowAudioDuration;
        workflowAudioTrack = composer.GetNumberInput("workflowAudioTrack")?.GetText()
            ?? workflowAudioTrack;
        workflowAudioGain = composer.GetNumberInput("workflowAudioGain")?.GetText()
            ?? workflowAudioGain;
        workflowAudioFadeIn = composer.GetNumberInput("workflowAudioFadeIn")?.GetText()
            ?? workflowAudioFadeIn;
        workflowAudioFadeOut = composer.GetNumberInput("workflowAudioFadeOut")?.GetText()
            ?? workflowAudioFadeOut;
        workflowAudioRate = composer.GetNumberInput("workflowAudioRate")?.GetText()
            ?? workflowAudioRate;
    }

    private bool TryParseWorkflowAudioValues(
        out double timelineStart,
        out double sourceStart,
        out double duration,
        out int track,
        out double gain,
        out double fadeIn,
        out double fadeOut,
        out double rate)
    {
        bool valid = double.TryParse(workflowAudioTimelineStart, NumberStyles.Float,
            CultureInfo.InvariantCulture, out timelineStart);
        valid &= double.TryParse(workflowAudioSourceStart, NumberStyles.Float,
            CultureInfo.InvariantCulture, out sourceStart);
        valid &= double.TryParse(workflowAudioDuration, NumberStyles.Float,
            CultureInfo.InvariantCulture, out duration);
        valid &= int.TryParse(workflowAudioTrack, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out track);
        valid &= double.TryParse(workflowAudioGain, NumberStyles.Float,
            CultureInfo.InvariantCulture, out gain);
        valid &= double.TryParse(workflowAudioFadeIn, NumberStyles.Float,
            CultureInfo.InvariantCulture, out fadeIn);
        valid &= double.TryParse(workflowAudioFadeOut, NumberStyles.Float,
            CultureInfo.InvariantCulture, out fadeOut);
        valid &= double.TryParse(workflowAudioRate, NumberStyles.Float,
            CultureInfo.InvariantCulture, out rate);
        return valid;
    }

    private void ShowWorkflowAudioError(string error)
        => capi.ShowChatMessage(Lang.Get(error == "workflow-audio-path"
            ? "vsdirector:workflow-audio-path-invalid"
            : "vsdirector:workflow-audio-range-invalid"));

    private bool DeleteWorkflowEditor()
        => workflowEdit is null || ConfirmDelete(
            workflowEdit.Id,
            () => owner.TryDeleteWorkflow(workflowEdit.Id, out _));

    private bool RecordWorkflowLive() => false;



    private bool PreviewWorkflow()
    {
        if (workflowEdit is not null)
        {
            CaptureWorkflowInputs();
            if (owner.TrySaveWorkflow(workflowEdit, out _, showNotification: false))
            {
                owner.TryPreviewWorkflow(workflowEdit.Id, out _);
            }
        }
        return true;
    }

    private void CaptureWorkflowInputs()
    {
        if (workflowEdit is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return;
        }

        workflowEdit.Name = composer.GetTextInput("workflowName")?.GetText() ?? workflowEdit.Name;
        string workflowSlot = composer.GetDropDown("workflowSlot")?.SelectedValue ?? "none";
        workflowEdit.QuickSlot = int.TryParse(
            workflowSlot,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsedWorkflowSlot)
                ? parsedWorkflowSlot
                : null;
        DirectorWorkflowCard? card = SelectedWorkflowCard();
        if (card is null)
        {
            return;
        }

        card.Name = composer.GetTextInput("workflowCardName")?.GetText() ?? card.Name;
        card.StartSeconds = ReadNumber(composer, "workflowStart", card.StartSeconds);
        card.EndSeconds = card.Kind == DirectorWorkflowCardKind.Countdown
            ? card.StartSeconds + Math.Max(
                DirectorWorkflowPlanner.MinCardDurationSeconds,
                ReadNumber(composer, "workflowCountdownDuration", card.DurationSeconds))
            : ReadNumber(composer, "workflowEnd", card.EndSeconds);
        card.Lane = (int)Math.Round(ReadNumber(composer, "workflowLane", card.Lane));
        if (card.TransitionIn is not null)
        {
            card.TransitionIn.DurationSeconds = card.TransitionIn.Type == DirectorVideoTransitionType.Cut
                ? 0d
                : Math.Max(0d, ReadNumber(
                    composer,
                    "workflowTransitionDuration",
                    card.TransitionIn.DurationSeconds));
        }
        card.Notes = composer.GetTextArea("workflowNotes")?.GetText() ?? card.Notes;
    }

    private void OnWorkflowCardKindChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }

        CaptureWorkflowInputs();
        DirectorWorkflowCard? card = SelectedWorkflowCard();
        if (card is null)
        {
            return;
        }

        DirectorWorkflowCardKind kind = value switch
        {
            "countdown" => DirectorWorkflowCardKind.Countdown,
            _ => DirectorWorkflowCardKind.Shot
        };
        if (workflowEdit is null || !DirectorWorkflowPlanner.IsCardKindAllowed(workflowEdit.Type, kind))
        {
            return;
        }
        if (card.Kind == kind)
        {
            return;
        }

        card.Kind = kind;
        if (kind != DirectorWorkflowCardKind.Shot)
        {
            card.SourceId = Guid.Empty;
            card.TransitionIn = null;
        }
        else
        {
            RecordingSourceOption? source = BuildRecordingSources()
                .FirstOrDefault(option => DirectorWorkflowPlanner.IsExportSource(option.Type));
            if (source is not null && Guid.TryParse(source.Selector, out Guid sourceId))
            {
                card.SourceType = source.Type;
                card.SourceId = sourceId;
            }
        }
        QueueAutoSave(AutoSaveTarget.WorkflowCard);
        ComposeInspector();
        ComposeWorkflowCanvas();
    }

    private void OnWorkflowSlotChanged(string value, bool selected)
    {
        if (selected && workflowEdit is not null)
        {
            workflowEdit.QuickSlot = int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int slot)
                    ? slot
                    : null;
        }
    }

    private static string WorkflowCardKindCode(DirectorWorkflowCardKind kind)
        => kind == DirectorWorkflowCardKind.Countdown ? "countdown" : "shot";

    private void OnWorkflowSourceChanged(string value, bool selected)
    {
        DirectorWorkflowCard? card = SelectedWorkflowCard();
        if (!selected || card is null)
        {
            return;
        }

        string[] parts = value.Split('|', 2);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type)
            && Enum.IsDefined((DirectorRecordingSourceType)type)
            && DirectorWorkflowPlanner.IsExportSource((DirectorRecordingSourceType)type)
            && Guid.TryParse(parts[1], out Guid sourceId))
        {
            card.SourceType = (DirectorRecordingSourceType)type;
            card.SourceId = sourceId;
            QueueAutoSave(AutoSaveTarget.WorkflowCard);
        }
    }

    private void OnWorkflowTransitionChanged(string value, bool selected)
    {
        DirectorWorkflowCard? card = SelectedWorkflowCard();
        if (!selected || card is null)
        {
            return;
        }

        double duration = Composers[InspectorKey] is GuiComposer composer
            ? Math.Max(0d, ReadNumber(
                composer,
                "workflowTransitionDuration",
                WorkflowTransitionDuration(card)))
            : WorkflowTransitionDuration(card);
        card.TransitionIn = value switch
        {
            "cut" => new DirectorVideoTransition { Type = DirectorVideoTransitionType.Cut },
            "cross" => new DirectorVideoTransition
            {
                Type = DirectorVideoTransitionType.CrossDissolve,
                DurationSeconds = duration
            },
            "fade" => new DirectorVideoTransition
            {
                Type = DirectorVideoTransitionType.FadeFromBlack,
                DurationSeconds = duration
            },
            _ => null
        };
        QueueAutoSave(AutoSaveTarget.WorkflowCard);
    }

    private bool SaveFollowEditor()
    {
        if (TryCommitFollowEditor())
        {
            Refresh();
        }
        return true;
    }

    private bool TryCommitFollowEditor()
    {
        if (followEdit is null)
        {
            return false;
        }

        GuiComposer composer = Composers[InspectorKey];
        CaptureFollowInputs(composer);
        if (!owner.TrySaveFollowRig(followEdit, out _))
        {
            return false;
        }
        followEdit = owner.FindFollowRig(followEdit.Id) ?? followEdit;
        return true;
    }

    private bool ActivateFollowEditor()
    {
        if (followEdit is not null && TryCommitFollowEditor())
        {
            if (section == DirectorWorkspaceSection.Replay)
            {
                replayViewMode = "follow";
                replayViewSourceId = followEdit.Id;
                _ = ApplyReplayView();
            }
            else
            {
                ActivateFollowFromWorkspace(followEdit.Id);
            }
        }
        return true;
    }

    private bool DeleteFollowEditor()
        => followEdit is null || ConfirmDelete(followEdit.Id,
            () => owner.TryDeleteFollowRig(followEdit.Id, out _));

    private void OnFollowTargetChanged(string value, bool selected)
    {
        if (!suppressFollowPreview && selected && followEdit is not null)
        {
            DirectorFollowTarget? target = owner.FindFollowTarget(value);
            if (target is DirectorFollowTarget resolved)
            {
                followEdit.TargetType = resolved.TargetType;
                followEdit.TargetPlayerUid = resolved.PlayerUid;
                followEdit.TargetEntityId = resolved.EntityId;
                followEdit.TargetDisplayName = resolved.DisplayName;
                owner.TryPreviewFollowRig(followEdit);
                QueueAutoSave(AutoSaveTarget.Follow);
            }
        }
    }

    private void OnFollowViewChanged(string value, bool selected)
    {
        if (!suppressFollowPreview && selected && followEdit is not null)
        {
            followEdit.ViewMode = value == "third" ? CameraFollowViewMode.ThirdPerson : CameraFollowViewMode.FirstPerson;
            owner.TryPreviewFollowRig(followEdit);
            QueueAutoSave(AutoSaveTarget.Follow);
        }
    }

    private void OnFollowMotionChanged(string value, bool selected)
    {
        if (!suppressFollowPreview && selected && followEdit is not null)
        {
            followEdit.MotionMode = value switch
            {
                "immediate" => CameraFollowMotionMode.Immediate,
                "delayed" => CameraFollowMotionMode.Delayed,
                _ => CameraFollowMotionMode.Smooth
            };
            owner.TryPreviewFollowRig(followEdit);
            QueueAutoSave(AutoSaveTarget.Follow);
        }
    }

    private void OnFollowAnchorChanged(string value, bool selected)
    {
        if (!suppressFollowPreview && selected && followEdit is not null)
        {
            followEdit.Anchor = value == "body" ? CameraFollowAnchor.Body : CameraFollowAnchor.Head;
            owner.TryPreviewFollowRig(followEdit);
            QueueAutoSave(AutoSaveTarget.Follow);
        }
    }

    private void OnFollowClearViewChanged(bool value)
    {
        if (suppressFollowPreview || followEdit is null)
        {
            return;
        }

        if (value && !owner.HasClearView)
        {
            followEdit.OpenClearView = false;
            Composers[InspectorKey].GetSwitch("followClearView")?.SetValue(false);
            owner.NotifyClearViewUnavailable();
            return;
        }

        followEdit.OpenClearView = value;
        owner.TryPreviewFollowRig(followEdit);
        QueueAutoSave(AutoSaveTarget.Follow);
    }

    private void OnFollowSlotChanged(string value, bool selected)
    {
        if (!suppressFollowPreview && selected && followEdit is not null)
        {
            followEdit.QuickSlot = int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int slot)
                    ? slot
                    : null;
            QueueAutoSave(AutoSaveTarget.Follow);
        }
    }

    private bool OnFollowOrbitSlider(int value)
    {
        if (!suppressFollowPreview && followEdit is not null)
        {
            followEdit.OrbitAngleDegrees = value;
            owner.TryPreviewFollowRig(followEdit);
            QueueAutoSave(AutoSaveTarget.Follow);
        }
        return true;
    }

    private bool OnFollowDistanceSlider(int value)
    {
        if (!suppressFollowPreview && followEdit is not null)
        {
            followEdit.Distance = value / 100d;
            owner.TryPreviewFollowRig(followEdit);
            QueueAutoSave(AutoSaveTarget.Follow);
        }
        return true;
    }

    private bool OnFollowHeightSlider(int value)
    {
        if (!suppressFollowPreview && followEdit is not null)
        {
            followEdit.Height = value / 100d;
            owner.TryPreviewFollowRig(followEdit);
            QueueAutoSave(AutoSaveTarget.Follow);
        }
        return true;
    }

    private void OnFollowPropertyInputChanged(string value)
    {
        _ = value;
        if (suppressFollowPreview
            || followEdit is null
            || Composers[InspectorKey] is not GuiComposer composer)
        {
            return;
        }

        bool changed = false;
        changed |= TryApplyFollowNumber(composer, "followOffsetX",
            -CameraFollowMath.MaxOffset, CameraFollowMath.MaxOffset,
            current => followEdit.OffsetX = current, followEdit.OffsetX);
        changed |= TryApplyFollowNumber(composer, "followOffsetY",
            -CameraFollowMath.MaxOffset, CameraFollowMath.MaxOffset,
            current => followEdit.OffsetY = current, followEdit.OffsetY);
        changed |= TryApplyFollowNumber(composer, "followOffsetZ",
            -CameraFollowMath.MaxOffset, CameraFollowMath.MaxOffset,
            current => followEdit.OffsetZ = current, followEdit.OffsetZ);
        changed |= TryApplyFollowNumber(composer, "followFov", 10d, 170d,
            current => followEdit.Fov = (float)current, followEdit.Fov);
        changed |= TryApplyFollowNumber(composer, "followOrbitSpeed",
            -CameraFollowMath.MaxOrbitSpeed, CameraFollowMath.MaxOrbitSpeed,
            current => followEdit.OrbitSpeedDegreesPerSecond = current,
            followEdit.OrbitSpeedDegreesPerSecond);
        changed |= TryApplyFollowNumber(composer, "followSmooth",
            CameraFollowMath.MinSmoothTimeSeconds, CameraFollowMath.MaxSmoothTimeSeconds,
            current => followEdit.SmoothTimeSeconds = current, followEdit.SmoothTimeSeconds);
        changed |= TryApplyFollowNumber(composer, "followDelay", 0d, CameraFollowMath.MaxDelaySeconds,
            current => followEdit.DelaySeconds = current, followEdit.DelaySeconds);
        if (changed)
        {
            owner.TryPreviewFollowRig(followEdit);
        }
    }

    private static bool TryApplyFollowNumber(
        GuiComposer composer,
        string key,
        double minimum,
        double maximum,
        Action<double> apply,
        double current)
    {
        string? text = composer.GetNumberInput(key)?.GetText();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed)
            || parsed < minimum
            || parsed > maximum
            || parsed == current)
        {
            return false;
        }

        apply(parsed);
        return true;
    }

    private bool StartReplayRecording()
    {
        PreserveInputs();
        owner.TryStartReplayRecording(replayRecordingName, out _);
        Refresh();
        return true;
    }

    private bool StopReplayRecording()
    {
        owner.TryStopReplayRecording(out _);
        Refresh();
        return true;
    }

    private bool RefreshReplayIndex()
    {
        owner.RefreshReplayIndex();
        Refresh();
        return true;
    }

    private void OnReplayActorChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }

        selectedReplayActor = value;
    }

    private bool AttachSelectedReplayActor(CameraFollowViewMode viewMode)
    {
        if (selectedReplayActor.Length > 0)
        {
            owner.TryAttachReplayActor(selectedReplayActor, viewMode, out _);
        }
        return true;
    }

    private bool DetachReplayActor()
    {
        owner.TryDetachReplayActor(out _);
        return true;
    }

    private bool ToggleReplayPause()
    {
        owner.TryToggleReplayPause(out _);
        UpdateReplayTimelineButtonStates();
        return true;
    }

    private bool SkipReplayBackward()
    {
        if (!owner.IsReplayPlaying)
        {
            return true;
        }
        double current = owner.ReplayTimeSeconds;
        double target = owner.ReplayMarkers
            .Where(marker => marker.TimeSeconds < current - 0.000001d)
            .Select(marker => marker.TimeSeconds)
            .DefaultIfEmpty(0d)
            .Max();
        owner.SeekReplayFromPanel(target);
        ComposeTimeline();
        return true;
    }

    private bool SkipReplayForward()
    {
        if (!owner.IsReplayPlaying)
        {
            return true;
        }
        double current = owner.ReplayTimeSeconds;
        double target = owner.ReplayMarkers
            .Where(marker => marker.TimeSeconds > current + 0.000001d)
            .Select(marker => marker.TimeSeconds)
            .DefaultIfEmpty(owner.ReplayDurationSeconds)
            .Min();
        owner.SeekReplayFromPanel(target);
        ComposeTimeline();
        return true;
    }

    private static readonly double[] ReplayManualSpeeds =
        { 0.05d, 0.1d, 0.2d, 0.5d, 1d, 2d, 5d, 10d, 20d };

    private static string FormatReplayTransportSpeed(double speed)
        => speed.ToString("0.##", CultureInfo.InvariantCulture);

    private bool SlowDownReplay() => AdjustReplayManualSpeed(-1);

    private bool SpeedUpReplay() => AdjustReplayManualSpeed(1);

    private bool AdjustReplayManualSpeed(int direction)
    {
        if (!owner.IsReplayPlaying)
        {
            return true;
        }
        double current = owner.ReplayBasePlaybackSpeed;
        double next = direction < 0
            ? ReplayManualSpeeds.Where(speed => speed < current - 0.000001d)
                .DefaultIfEmpty(ReplayManualSpeeds[0]).Max()
            : ReplayManualSpeeds.Where(speed => speed > current + 0.000001d)
                .DefaultIfEmpty(ReplayManualSpeeds[^1]).Min();
        if (owner.SetReplaySpeedFromPanel(next))
        {
            ComposeTimeline();
        }
        return true;
    }

    private bool AddReplayMarkerAtHead()
    {
        string label = Lang.Get("vsdirector:replay-marker-default", owner.ReplayMarkers.Count + 1);
        HashSet<Guid> before = owner.ReplayMarkers.Select(marker => marker.Id).ToHashSet();
        double time = ReplayTimelineTickAt(owner.ReplayTimeSeconds)
            / ReplayTimelineTicksPerSecond;
        if (owner.TryAddReplayMarker(label, time, out _))
        {
            EnsureReplayElementTrackStored(DirectorReplayElementTrackType.Marker);
            selectedReplayTimelineElementTrack = ReplayTimelineElementKind.Marker;
            selectedReplayViewportClipId = Guid.Empty;
            selectedReplaySpeedKeyframeId = Guid.Empty;
            selectedReplayElementKeyframeId = Guid.Empty;
            selectedReplayMarkerId = owner.ReplayMarkers
                .FirstOrDefault(marker => !before.Contains(marker.Id))?.Id
                ?? owner.ReplayMarkers
                    .OrderBy(marker => Math.Abs(marker.TimeSeconds - owner.ReplayTimeSeconds))
                    .FirstOrDefault()?.Id
                ?? Guid.Empty;
            replayMarkerFieldsId = Guid.Empty;
            SyncReplayTimelineSelection();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool AddReplaySpeedKeyframeAtHead()
    {
        if (owner.TryAddReplaySpeedKeyframe(
            owner.ReplayPlaybackSpeed,
            ReplayTimelineTickAt(owner.ReplayTimeSeconds) / ReplayTimelineTicksPerSecond,
            DirectorReplaySpeedInterpolation.Linear,
            out _))
        {
            EnsureReplayElementTrackStored(DirectorReplayElementTrackType.Speed);
            selectedReplayTimelineElementTrack = ReplayTimelineElementKind.Speed;
            selectedReplayViewportClipId = Guid.Empty;
            selectedReplayMarkerId = Guid.Empty;
            selectedReplayElementKeyframeId = Guid.Empty;
            selectedReplaySpeedKeyframeId = owner.ReplaySpeedKeyframes
                .OrderBy(keyframe => Math.Abs(keyframe.TimeSeconds - owner.ReplayTimeSeconds))
                .FirstOrDefault()?.Id
                ?? Guid.Empty;
            replaySpeedFieldsId = Guid.Empty;
            SyncReplayTimelineSelection();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private void OnReplaySpeedInterpolationChanged(string value, bool selected)
    {
        if (selected)
        {
            replaySpeedInterpolationCode = value;
        }
    }

    private bool ApplySelectedReplayMarker()
    {
        PreserveInputs();
        if (selectedReplayMarkerId == Guid.Empty)
        {
            return true;
        }
        if (!double.TryParse(replayMarkerTimeText, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double time))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-marker-invalid-time"));
            return true;
        }
        if (owner.TryUpdateReplayMarker(
                selectedReplayMarkerId,
                replayMarkerLabelText,
                ReplayTimelineTickAt(time) / ReplayTimelineTicksPerSecond,
                out _))
        {
            replayMarkerFieldsId = Guid.Empty;
            SyncReplayTimelineSelection();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool ApplyReplayBaseSpeed()
    {
        PreserveInputs();
        if (!double.TryParse(replayBaseSpeedText, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double speed))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-invalid-speed"));
            return true;
        }
        if (owner.TrySetReplaySpeed(speed, out _))
        {
            replayBaseSpeedText = owner.ReplayBasePlaybackSpeed
                .ToString("0.###", CultureInfo.InvariantCulture);
            RestoreInspectorInputs();
        }
        return true;
    }

    private bool DeleteSelectedReplayMarker()
    {
        if (selectedReplayMarkerId != Guid.Empty
            && owner.TryDeleteReplayMarker(selectedReplayMarkerId.ToString("D"), out _))
        {
            selectedReplayMarkerId = Guid.Empty;
            replayMarkerFieldsId = Guid.Empty;
            SyncReplayTimelineSelection();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool ApplySelectedReplaySpeedKeyframe()
    {
        PreserveInputs();
        DirectorReplaySpeedInterpolation interpolation = replaySpeedInterpolationCode switch
        {
            "hold" => DirectorReplaySpeedInterpolation.Hold,
            "ease" => DirectorReplaySpeedInterpolation.EaseInOut,
            _ => DirectorReplaySpeedInterpolation.Linear
        };
        if (selectedReplaySpeedKeyframeId == Guid.Empty)
        {
            return true;
        }
        if (!double.TryParse(replaySpeedTimeText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double time)
            || !double.TryParse(replaySpeedValueText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double speed))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-invalid-speed"));
            return true;
        }
        if (owner.TryUpdateReplaySpeedKeyframe(
                selectedReplaySpeedKeyframeId,
                ReplayTimelineTickAt(time) / ReplayTimelineTicksPerSecond,
                speed,
                interpolation,
                out _))
        {
            replaySpeedFieldsId = Guid.Empty;
            SyncReplayTimelineSelection();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool DeleteSelectedReplaySpeedKeyframe()
    {
        if (selectedReplaySpeedKeyframeId != Guid.Empty
            && owner.TryDeleteReplaySpeedKeyframe(
                selectedReplaySpeedKeyframeId.ToString("D"), out _))
        {
            selectedReplaySpeedKeyframeId = Guid.Empty;
            replaySpeedFieldsId = Guid.Empty;
            SyncReplayTimelineSelection();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private void SyncReplayTimelineSelection()
    {
        DirectorReplayViewportTimeline timeline = CurrentReplayViewportTimeline();
        DirectorReplayElementTrack? selectedTrack = timeline.Tracks.FirstOrDefault(track =>
            track.Id == selectedReplayElementTrackId);
        if (selectedTrack is null)
        {
            selectedReplayElementTrackId = Guid.Empty;
            selectedReplayTimelineElementTrack = null;
        }
        else
        {
            selectedReplayTimelineElementTrack = ReplayTimelineElementKindFor(selectedTrack.TrackType);
        }
        IReadOnlyList<DirectorReplayMarker> markers = owner.ReplayMarkers;
        DirectorReplayMarker? marker = selectedReplayMarkerId == Guid.Empty
            ? null
            : markers.FirstOrDefault(item => item.Id == selectedReplayMarkerId);
        selectedReplayMarkerId = marker?.Id ?? Guid.Empty;
        if (marker is not null && replayMarkerFieldsId != marker.Id)
        {
            replayMarkerLabelText = marker.Label;
            replayMarkerTimeText = marker.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            replayMarkerFieldsId = marker.Id;
        }

        IReadOnlyList<DirectorReplaySpeedKeyframe> speedKeys = owner.ReplaySpeedKeyframes;
        DirectorReplaySpeedKeyframe? speedKey = selectedReplaySpeedKeyframeId == Guid.Empty
            ? null
            : speedKeys.FirstOrDefault(item => item.Id == selectedReplaySpeedKeyframeId);
        selectedReplaySpeedKeyframeId = speedKey?.Id ?? Guid.Empty;
        if (speedKey is not null && replaySpeedFieldsId != speedKey.Id)
        {
            replaySpeedTimeText = speedKey.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            replaySpeedValueText = speedKey.Speed.ToString("0.###", CultureInfo.InvariantCulture);
            replaySpeedInterpolationCode = speedKey.InterpolationToNext switch
            {
                DirectorReplaySpeedInterpolation.Hold => "hold",
                DirectorReplaySpeedInterpolation.EaseInOut => "ease",
                _ => "linear"
            };
            replaySpeedFieldsId = speedKey.Id;
        }

        DirectorReplayElementKeyframe? elementKeyframe = selectedReplayElementKeyframeId == Guid.Empty
            ? null
            : timeline.ElementKeyframes.FirstOrDefault(item =>
                item.Id == selectedReplayElementKeyframeId
                && (selectedReplayElementTrackId == Guid.Empty
                    || item.TrackId == selectedReplayElementTrackId));
        selectedReplayElementKeyframeId = elementKeyframe?.Id ?? Guid.Empty;
        if (elementKeyframe is not null && replayElementFieldsId != elementKeyframe.Id)
        {
            SyncReplayElementFields(elementKeyframe);
        }
    }

    private DirectorReplaySpeedKeyframe? SelectedReplaySpeedKeyframe()
        => owner.ReplaySpeedKeyframes.FirstOrDefault(
            keyframe => keyframe.Id == selectedReplaySpeedKeyframeId);

    private IReadOnlyList<ReplayViewOption> ReplayViewOptions()
    {
        return replayViewMode switch
        {
            "camera" => owner.Cameras
                .Select(camera => new ReplayViewOption(camera.Id, camera.Name))
                .ToArray(),
            "path" => owner.Paths
                .Select(path => new ReplayViewOption(path.Id, path.Name))
                .ToArray(),
            "sequence" => owner.Sequences
                .Select(sequence => new ReplayViewOption(sequence.Id, sequence.Name))
                .ToArray(),
            "follow" => owner.FollowRigs
                .Select(rig => new ReplayViewOption(rig.Id, rig.Name))
                .ToArray(),
            _ => new[]
            {
                new ReplayViewOption(Guid.Empty, Lang.Get("vsdirector:replay-view-free-active"))
            }
        };
    }

    private void OnReplayViewModeChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }
        string previousMode = replayViewMode;
        if (previousMode == "free" && value != "free")
        {
            replayInspectorSection = inspectorSection;
        }
        else if (previousMode != "free" && value == "free")
        {
            inspectorSection = replayInspectorSection;
        }
        replayViewMode = value;
        replayViewSourceId = Guid.Empty;
        replayViewResourceScrollOffset = 0;
        replayViewInspectorSection = DefaultReplayViewInspectorSection(value);
        replayViewStartSeconds = ReplayViewTimelineStartSeconds();
        replayViewConfigured = true;
        replayViewAppliedReplayId = Guid.Empty;
        EnsureReplayViewEditor(reset: true);
        if (replayViewMode == "path" && replayViewSourceId != Guid.Empty)
        {
            replayCapturePathId = replayViewSourceId;
        }
        ComposeResources();
        ComposeInspector();
        ComposeTimeline();
    }

    private void OnReplayViewSourceChanged(string value, bool selected)
    {
        if (selected && Guid.TryParse(value, out Guid id))
        {
            replayViewSourceId = id;
            replayViewResourceScrollOffset = 0;
            replayViewStartSeconds = ReplayViewTimelineStartSeconds();
            replayViewConfigured = true;
            replayViewAppliedReplayId = Guid.Empty;
            EnsureReplayViewEditor(reset: true);
            if (replayViewMode == "path")
            {
                replayCapturePathId = replayViewSourceId;
            }
            ComposeResources();
            ComposeInspector();
            ComposeTimeline();
        }
    }

    private void OnReplayCapturePathChanged(string value, bool selected)
    {
        if (selected && Guid.TryParse(value, out Guid id))
        {
            replayCapturePathId = id;
        }
    }

    private bool ActivateReplayView()
    {
        _ = ApplyReplayView();
        return true;
    }

    private bool ApplyReplayView()
    {
        bool activated = replayViewMode switch
        {
            "camera" when replayViewSourceId != Guid.Empty
                => owner.TryActivateReplayCamera(replayViewSourceId),
            "path" when replayViewSourceId != Guid.Empty
                => owner.TryActivateReplayPath(replayViewSourceId),
            "sequence" when replayViewSourceId != Guid.Empty
                => owner.TryActivateReplaySequence(replayViewSourceId),
            "follow" when replayViewSourceId != Guid.Empty
                => owner.TryActivateReplayFollow(replayViewSourceId),
            "free" => owner.TryEnableReplayFreeCamera(out _),
            _ => false
        };
        if (activated)
        {
            CameraPath? activePath = replayViewMode == "path"
                ? owner.FindPath(replayViewSourceId)
                : null;
            replayViewStartSeconds = ReplayViewTimelineStartSeconds(activePath);
            replayViewConfigured = true;
            replayViewAppliedReplayId = owner.ActiveReplayId;
            EnsureReplayViewEditor(reset: false);
            Refresh();
        }
        return activated;
    }

    private bool PreviousReplayView() => StepReplayView(-1);

    private bool NextReplayView() => StepReplayView(1);

    private bool StepReplayView(int delta)
    {
        IReadOnlyList<ReplayViewOption> options = ReplayViewOptions();
        if (options.Count == 0)
        {
            return true;
        }
        int current = options.ToList().FindIndex(option => option.Id == replayViewSourceId);
        current = current < 0 ? 0 : (current + delta + options.Count) % options.Count;
        replayViewSourceId = options[current].Id;
        if (replayViewMode == "path")
        {
            replayCapturePathId = replayViewSourceId;
        }
        replayViewStartSeconds = ReplayViewTimelineStartSeconds();
        EnsureReplayViewEditor(reset: true);
        ActivateReplayView();
        ComposeResources();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private void CaptureFollowInputs(GuiComposer composer)
    {
        if (followEdit is null)
        {
            return;
        }
        followEdit.Name = composer.GetTextInput("followName")?.GetText() ?? followEdit.Name;
        if (composer.GetTextInput("followTags") is { } tagsInput)
        {
            followEdit.Tags = ParseTags(tagsInput.GetText());
        }
        followEdit.Description = composer.GetTextInput("followDescription")?.GetText()
            ?? followEdit.Description;
        followEdit.OffsetX = ReadNumber(composer, "followOffsetX", followEdit.OffsetX);
        followEdit.OffsetY = ReadNumber(composer, "followOffsetY", followEdit.OffsetY);
        followEdit.OffsetZ = ReadNumber(composer, "followOffsetZ", followEdit.OffsetZ);
        followEdit.Distance = ReadNumber(composer, "followDistance", followEdit.Distance);
        followEdit.Height = ReadNumber(composer, "followHeight", followEdit.Height);
        followEdit.Fov = ParseFloat(composer.GetNumberInput("followFov")?.GetText(), followEdit.Fov);
        followEdit.OrbitAngleDegrees = ReadNumber(
            composer, "followOrbitAngle", followEdit.OrbitAngleDegrees);
        followEdit.OrbitSpeedDegreesPerSecond = ReadNumber(
            composer, "followOrbitSpeed", followEdit.OrbitSpeedDegreesPerSecond);
        followEdit.SmoothTimeSeconds = ReadNumber(
            composer, "followSmooth", followEdit.SmoothTimeSeconds);
        followEdit.DelaySeconds = ReadNumber(composer, "followDelay", followEdit.DelaySeconds);
        followEdit.OpenClearView = composer.GetSwitch("followClearView")?.On
            ?? followEdit.OpenClearView;
    }

    private bool SelectReplayViewSource(Guid id, bool activate)
    {
        replayViewSourceId = id;
        replayViewResourceScrollOffset = 0;
        replayViewStartSeconds = ReplayViewTimelineStartSeconds();
        replayViewConfigured = true;
        replayViewAppliedReplayId = Guid.Empty;
        EnsureReplayViewEditor(reset: true);
        if (replayViewMode == "path")
        {
            replayCapturePathId = replayViewSourceId;
        }
        if (activate)
        {
            _ = ApplyReplayView();
        }
        else
        {
            ComposeResources();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private void EnsureReplayViewEditor(bool reset)
    {
        if (!HasReplayViewEditor)
        {
            if (section == DirectorWorkspaceSection.Replay && replayViewMode == "free")
            {
                cameraEdit = null;
                pathEdit = null;
                sequenceEdit = null;
                followEdit = null;
                selectedPathKeyframes.Clear();
                owner.SetPathEditPreview(null);
            }
            return;
        }

        IReadOnlyList<ReplayViewOption> options = ReplayViewOptions();
        if (!options.Any(option => option.Id == replayViewSourceId))
        {
            replayViewSourceId = options.FirstOrDefault().Id;
            reset = true;
        }

        switch (replayViewMode)
        {
            case "camera":
                if (reset || cameraEdit?.Id != replayViewSourceId)
                {
                    cameraEdit = owner.FindCamera(replayViewSourceId);
                }
                pathEdit = null;
                sequenceEdit = null;
                followEdit = null;
                owner.SetPathEditPreview(null);
                break;
            case "path":
                if (reset || pathEdit?.Id != replayViewSourceId)
                {
                    pathEdit = owner.FindPath(replayViewSourceId);
                    pathEditHistory.Clear();
                    pendingPathEditHistory = null;
                    selectedPathKeyframeIndex = pathEdit?.Keyframes.Count > 0 ? 0 : -1;
                    selectedPathKeyframes.Clear();
                    if (selectedPathKeyframeIndex >= 0)
                    {
                        selectedPathKeyframes.Add(selectedPathKeyframeIndex);
                        pathEditorPlayhead = pathEdit!.Keyframes[selectedPathKeyframeIndex].TimeSeconds;
                    }
                    SyncPathTimingText();
                }
                cameraEdit = null;
                sequenceEdit = null;
                followEdit = null;
                owner.SetPathEditPreview(
                    pathEdit,
                    SelectedPathKeyframeIndex(),
                    selectedKeyframes: selectedPathKeyframes);
                break;
            case "sequence":
                if (reset || sequenceEdit?.Id != replayViewSourceId)
                {
                    sequenceEdit = owner.FindSequence(replayViewSourceId);
                    sequencePageIndex = 0;
                    selectedSequenceCameraId = sequenceEdit?.Shots.FirstOrDefault()?.CameraId
                        ?? owner.Cameras.FirstOrDefault()?.Id
                        ?? Guid.Empty;
                }
                cameraEdit = null;
                pathEdit = null;
                followEdit = null;
                owner.SetPathEditPreview(null);
                break;
            case "follow":
                if (reset || followEdit?.Id != replayViewSourceId)
                {
                    followEdit = owner.FindFollowRig(replayViewSourceId);
                }
                cameraEdit = null;
                pathEdit = null;
                sequenceEdit = null;
                owner.SetPathEditPreview(null);
                break;
        }
    }

    private static string DefaultReplayViewInspectorSection(string mode)
        => mode == "follow" ? "target" : "basic";

    private bool SelectReplayPathKeyframeFromList(int index)
    {
        if (pathEdit is null || index < 0 || index >= pathEdit.Keyframes.Count)
        {
            return true;
        }

        if (Composers[InspectorKey] is GuiComposer composer)
        {
            CaptureReplayMetadataInputs(composer);
        }

        // Replay keyframe markers are navigation controls. Capturing the
        // inspector here can retime the path with stale fields during a seek.
        SelectOnlyPathKeyframe(index);
        pathEditorPlayhead = pathEdit.Keyframes[index].TimeSeconds;
        replayViewInspectorSection = "keyframe";
        SyncPathTimingText();
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
        ComposeResources();
        ComposeInspector();
        ComposeWorkflowCanvas();
        return true;
    }

    private bool JumpReplayToPathKeyframe(int index)
    {
        if (pathEdit is null || index < 0 || index >= pathEdit.Keyframes.Count)
        {
            return true;
        }

        double replayTime = ReplayTimeForPathKeyframe(pathEdit, pathEdit.Keyframes[index]);
        SelectReplayPathKeyframeFromList(index);
        if (owner.IsReplayPlaying)
        {
            owner.SeekReplayFromPanel(replayTime);
        }
        return true;
    }

    private bool CaptureReplayFreeCamera()
    {
        if (!owner.IsReplayPlaying)
        {
            return true;
        }
        string name = UniqueResourceName(
            Lang.Get("vsdirector:replay-camera-default-name"),
            owner.Cameras.Select(camera => camera.Name));
        if (owner.TryAddCamera(name, out _))
        {
            Refresh();
        }
        return true;
    }

    private bool CreateReplayPathFromView()
    {
        if (!owner.IsReplayPlaying)
        {
            return true;
        }
        string name = UniqueResourceName(
            Lang.Get("vsdirector:replay-path-default-name"),
            owner.Paths.Select(path => path.Name));
        if (owner.TryCreateReplayPath(name, out Guid pathId, out _))
        {
            replayCapturePathId = pathId;
            replayViewMode = "path";
            replayViewSourceId = pathId;
            Refresh();
        }
        return true;
    }

    private bool CaptureReplayPathKeyframe()
    {
        if (replayCapturePathId != Guid.Empty)
        {
            if (owner.TryCaptureReplayKeyframe(replayCapturePathId.ToString("D"), out _)
                && replayViewMode == "path"
                && replayViewSourceId == replayCapturePathId)
            {
                EnsureReplayViewEditor(reset: true);
            }
            Refresh();
        }
        return true;
    }

    private static string UniqueResourceName(string baseName, IEnumerable<string> existingNames)
    {
        HashSet<string> names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
        {
            return baseName;
        }
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private bool StopReplay()
    {
        owner.StopReplay();
        Refresh();
        return true;
    }

    private void CaptureReplayMetadataInputs(GuiComposer composer)
    {
        DirectorReplayInfo? selected = SelectedReplayInfo();
        if (selected is null || replayMetadataFieldsId != selected.Id)
        {
            return;
        }

        replayMetadataName = composer.GetTextInput("replayMetadataName")?.GetText()
            ?? replayMetadataName;
        replayMetadataTags = composer.GetTextInput("replayMetadataTags")?.GetText()
            ?? replayMetadataTags;
        replayMetadataDescription = composer.GetTextInput("replayMetadataDescription")?.GetText()
            ?? replayMetadataDescription;
    }

    private bool SaveReplayMetadata()
    {
        DirectorReplayInfo? selected = SelectedReplayInfo();
        if (selected is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return true;
        }

        CaptureReplayMetadataInputs(composer);
        Guid previousFieldsId = replayMetadataFieldsId;
        replayMetadataFieldsId = Guid.Empty;
        if (!owner.TryUpdateReplayMetadata(
                selected.Id,
                replayMetadataName,
                replayMetadataTags,
                replayMetadataDescription,
                out _))
        {
            replayMetadataFieldsId = previousFieldsId;
        }
        return true;
    }

    private bool DeleteSelectedReplay()
    {
        DirectorReplayInfo? selected = SelectedReplayInfo();
        return selected is null || ConfirmDelete(
            selected.Id,
            () => owner.TryDeleteReplay(selected.Id, out _));
    }

    private IReadOnlyList<RecordingSourceOption> BuildRecordingSources()
    {
        List<RecordingSourceOption> sources = new()
        {
            new RecordingSourceOption(
                ((int)DirectorRecordingSourceType.Program).ToString(CultureInfo.InvariantCulture) + "|",
                DirectorRecordingSourceType.Program,
                string.Empty,
                Lang.Get("vsdirector:record-source-program"))
        };
        sources.AddRange(owner.Cameras.Select(camera => RecordingSource(
            DirectorRecordingSourceType.StaticCamera, camera.Id, camera.Name, "vsdirector:record-source-static")));
        sources.AddRange(owner.Paths.Select(path => RecordingSource(
            DirectorRecordingSourceType.Path, path.Id, path.Name, "vsdirector:record-source-path")));
        sources.AddRange(owner.FollowRigs.Select(rig => RecordingSource(
            DirectorRecordingSourceType.Follow, rig.Id, rig.Name, "vsdirector:record-source-follow")));
        return sources;
    }



    private static RecordingSourceOption RecordingSource(
        DirectorRecordingSourceType type,
        Guid id,
        string name,
        string langKey)
        => new(
            ((int)type).ToString(CultureInfo.InvariantCulture) + "|" + id.ToString("D"),
            type,
            id.ToString("D"),
            Lang.Get(langKey) + ": " + name);

    private void OnRecordingSourceChanged(string value, bool selected)
    {
        if (selected)
        {
            selectedRecordingSource = value;
        }
    }



    private bool CreateRecordingTrack()
    {
        PreserveInputs();
        RecordingSourceOption? source = BuildRecordingSources().FirstOrDefault(
            option => option.Value == selectedRecordingSource);
        if (source is not null
            && owner.TryAddRecordingTrack(source.Type, source.Selector, recordingTrackName, out _))
        {
            recordingTrackName = string.Empty;
            DirectorRecordingTrack? created = owner.RecordingTracks.LastOrDefault();
            selectedRecordingTrackId = created?.Id ?? selectedRecordingTrackId;
            Refresh();
        }
        return true;
    }

    private bool ArmRecordingPlan()
    {
        recordingConfirmationPending = selectedRecordingTrackId != Guid.Empty
            && owner.RecordingTracks.Any(track => track.Id == selectedRecordingTrackId && track.Enabled);
        ComposeInspector();
        return true;
    }

    private bool ConfirmRecordingPlan()
    {
        recordingConfirmationPending = false;
        if (selectedRecordingTrackId != Guid.Empty
            && owner.AssignedRecordingTrackId != selectedRecordingTrackId
            && !owner.TryAssignRecordingWorker(selectedRecordingTrackId, out _))
        {
            ComposeInspector();
            return true;
        }

        owner.TryStartRecording(out _);
        ComposeInspector();
        return true;
    }

    private bool CancelRecordingPlan()
    {
        recordingConfirmationPending = false;
        ComposeInspector();
        return true;
    }

    private bool StopRecordingPlan()
    {
        owner.TryStopRecording(out _);
        ComposeInspector();
        return true;
    }

    private bool UnassignRecordingPlan()
    {
        owner.UnassignRecordingWorker();
        recordingConfirmationPending = false;
        ComposeInspector();
        ComposeResources();
        return true;
    }

    private bool DeleteRecordingTrack()
    {
        DirectorRecordingTrack? track = owner.RecordingTracks.FirstOrDefault(
            candidate => candidate.Id == selectedRecordingTrackId);
        return track is null || ConfirmDelete(track.Id, () => owner.TryDeleteRecordingTrack(track.Id, out _));
    }

    private bool ConfirmDelete(Guid id, Func<bool> delete)
    {
        if (pendingDeleteId != id)
        {
            pendingDeleteId = id;
            ComposeInspector();
            return true;
        }

        bool deletingReplayViewResource = HasReplayViewEditor && replayViewSourceId == id;
        bool deleted = delete();
        pendingDeleteId = null;
        if (deleted)
        {
            if (deletingReplayViewResource)
            {
                replayViewSourceId = Guid.Empty;
                replayViewResourceScrollOffset = 0;
                replayViewAppliedReplayId = Guid.Empty;
                EnsureReplayViewEditor(reset: true);
            }
            else
            {
                selectedId = null;
                selectedRecordingTrackId = owner.AssignedRecordingTrackId
                    ?? owner.RecordingTracks.FirstOrDefault()?.Id
                    ?? Guid.Empty;
                ClearEditors();
            }
        }
        Refresh();
        return true;
    }

    private string DeleteLabel(Guid id)
        => pendingDeleteId == id ? Lang.Get("vsdirector:confirm-delete") : Lang.Get("vsdirector:delete");

    private string DeleteLabel(Guid id, string deleteLangCode)
        => pendingDeleteId == id ? Lang.Get("vsdirector:confirm-delete") : Lang.Get(deleteLangCode);

    private string RecordingTrackDetails(DirectorRecordingTrack track)
        => Lang.Get("vsdirector:record-track-detail",
            RecordingSourceTypeLabel(track.SourceType),
            RecordingSourceName(track),
            track.Enabled ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"));

    private string RecordingSourceName(DirectorRecordingTrack track)
        => track.SourceType switch
        {
            DirectorRecordingSourceType.StaticCamera => owner.FindCamera(track.SourceId)?.Name,
            DirectorRecordingSourceType.Path => owner.FindPath(track.SourceId)?.Name,
            DirectorRecordingSourceType.Sequence => owner.FindSequence(track.SourceId)?.Name,
            DirectorRecordingSourceType.Follow => owner.FindFollowRig(track.SourceId)?.Name,
            DirectorRecordingSourceType.Workflow => owner.FindWorkflow(track.SourceId)?.Name,
            _ => Lang.Get("vsdirector:record-source-program")
        } ?? Lang.Get("vsdirector:record-source-missing");

    private static string RecordingSourceTypeLabel(DirectorRecordingSourceType sourceType)
        => Lang.Get(sourceType switch
        {
            DirectorRecordingSourceType.StaticCamera => "vsdirector:record-source-static",
            DirectorRecordingSourceType.Path => "vsdirector:record-source-path",
            DirectorRecordingSourceType.Sequence => "vsdirector:record-source-sequence",
            DirectorRecordingSourceType.Follow => "vsdirector:record-source-follow",
            DirectorRecordingSourceType.Workflow => "vsdirector:record-source-workflow",
            _ => "vsdirector:record-source-program"
        });

    private static string PathResourceDetails(CameraPath path)
        => Lang.Get("vsdirector:path-detail", path.Keyframes.Count,
            path.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            Lang.Get("vsdirector:interpolation-" + path.Interpolation.ToString().ToLowerInvariant()),
            Lang.Get("vsdirector:loop-" + path.LoopMode.ToString().ToLowerInvariant()),
            path.PlaybackSpeed.ToString("0.##", CultureInfo.InvariantCulture) + "x",
            path.PreviewVisible ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"));

    private string FollowResourceDetails(CameraFollowRig rig)
        => Lang.Get("vsdirector:follow-detail", rig.TargetDisplayName,
            Lang.Get(rig.ViewMode == CameraFollowViewMode.ThirdPerson
                ? "vsdirector:follow-view-third"
                : "vsdirector:follow-view-first"),
            Lang.Get(rig.MotionMode switch
            {
                CameraFollowMotionMode.Immediate => "vsdirector:follow-motion-immediate",
                CameraFollowMotionMode.Delayed => "vsdirector:follow-motion-delayed",
                _ => "vsdirector:follow-motion-smooth"
            }),
            Lang.Get(rig.Anchor == CameraFollowAnchor.Body
                ? "vsdirector:follow-anchor-body"
                : "vsdirector:follow-anchor-head"),
            rig.QuickSlot is int slot && QuickSlots.IsValid(slot)
                ? owner.QuickSlotHotkeyLabel(slot)
                : Lang.Get("vsdirector:no-slot"));

    private string LiveOutputPlanText(DirectorRecordingTrack? track)
    {
        string source = track is null
            ? string.Empty
            : $"{track.Name} -> {RecordingSourceTypeLabel(track.SourceType)}: {RecordingSourceName(track)}";
        string backend = Lang.Get("vsdirector:record-backend-obs");
        string format = "OBS";
        return Lang.Get(
            "vsdirector:workspace-live-plan-detail",
            source,
            backend,
            format,
            owner.Config.RecordingFrameRate,
            owner.Config.RecordingOutputDirectory.Length == 0
                ? Lang.Get("vsdirector:config-default")
                : owner.Config.RecordingOutputDirectory)
            + "\n"
            + Lang.Get(owner.RecordReplayWithVideo
                ? "vsdirector:workspace-live-take-replay-on"
                : "vsdirector:workspace-live-take-replay-off")
            + (owner.TakeOutputStatusText().Length == 0
                ? string.Empty
                : "\n" + owner.TakeOutputStatusText())
            + "\n"
            + Lang.Get("vsdirector:workspace-live-framebuffer-boundary");
    }

    private void PreserveInputs()
    {
        CaptureActionLibrarySearch();
        if (section == DirectorWorkspaceSection.Workflows)
        {
            CaptureWorkflowInputs();
            if (workflowAudioEditorExpanded && Composers[InspectorKey] is GuiComposer workflowInspector)
            {
                CaptureWorkflowAudioInputs(workflowInspector);
            }
        }

        GuiComposer? resources = Composers[ResourceMenuKey];
        if (resources is not null)
        {
            search = resources.GetTextInput("search")?.GetText() ?? search;
            actionResourceSearch = resources.GetTextInput("actionResourceSearch")?.GetText()
                ?? actionResourceSearch;
            newName = resources.GetTextInput("newName")?.GetText() ?? newName;
            replayRecordingName = resources.GetTextInput("replayName")?.GetText() ?? replayRecordingName;
            recordingTrackName = resources.GetTextInput("recordTrackName")?.GetText() ?? recordingTrackName;
        }

        GuiComposer? inspector = Composers[InspectorKey];
        if (inspector is not null)
        {
            CaptureReplayMetadataInputs(inspector);
            CaptureCameraInputs(inspector);
            pathTagsText = inspector.GetTextInput("pathTags")?.GetText()
                ?? pathTagsText;
            pathDescriptionText = inspector.GetTextInput("pathDescription")?.GetText()
                ?? pathDescriptionText;
            pathFrameStartText = inspector.GetNumberInput("pathFrameStart")?.GetText()
                ?? pathFrameStartText;
            pathFrameDurationText = inspector.GetNumberInput("pathFrameDuration")?.GetText()
                ?? pathFrameDurationText;
            pathSegmentStartSpeedText = inspector.GetNumberInput("pathSegmentStartSpeed")?.GetText()
                ?? pathSegmentStartSpeedText;
            pathSegmentEndSpeedText = inspector.GetNumberInput("pathSegmentEndSpeed")?.GetText()
                ?? pathSegmentEndSpeedText;
            pathSegmentMarkerText = inspector.GetTextInput("pathSegmentMarker")?.GetText()
                ?? pathSegmentMarkerText;
            pathInsertDurationText = inspector.GetNumberInput("pathInsertDuration")?.GetText()
                ?? pathInsertDurationText;
            pathInsertStartText = inspector.GetNumberInput("pathInsertStart")?.GetText()
                ?? pathInsertStartText;
            replayMarkerLabelText = inspector.GetTextInput("replayMarkerLabel")?.GetText()
                ?? replayMarkerLabelText;
            replayMarkerTimeText = inspector.GetNumberInput("replayMarkerTime")?.GetText()
                ?? replayMarkerTimeText;
            replaySpeedTimeText = inspector.GetNumberInput("replaySpeedTime")?.GetText()
                ?? replaySpeedTimeText;
            replaySpeedValueText = ReadReplayTimelineValue(
                inspector,
                "replaySpeedValue",
                replaySpeedValueText);
            replayBaseSpeedText = ReadReplayTimelineValue(
                inspector,
                "replayBaseSpeed",
                replayBaseSpeedText);
            replayViewportNameText = inspector.GetTextInput("replayViewportName")?.GetText()
                ?? replayViewportNameText;
            replayViewportStartText = inspector.GetNumberInput("replayViewportStart")?.GetText()
                ?? replayViewportStartText;
            replayViewportXText = inspector.GetNumberInput("replayViewportX")?.GetText()
                ?? replayViewportXText;
            replayViewportYText = inspector.GetNumberInput("replayViewportY")?.GetText()
                ?? replayViewportYText;
            replayViewportZText = inspector.GetNumberInput("replayViewportZ")?.GetText()
                ?? replayViewportZText;
            replayViewportDimensionText = inspector.GetNumberInput("replayViewportDimension")?.GetText()
                ?? replayViewportDimensionText;
            replayViewportYawText = inspector.GetNumberInput("replayViewportYaw")?.GetText()
                ?? replayViewportYawText;
            replayViewportPitchText = inspector.GetNumberInput("replayViewportPitch")?.GetText()
                ?? replayViewportPitchText;
            replayViewportRollText = inspector.GetNumberInput("replayViewportRoll")?.GetText()
                ?? replayViewportRollText;
            foreach (string field in ReplayElementNumericFields)
            {
                string key = "replayElement" + UpperFirst(field);
                replayElementFieldValues[field] = ReadReplayPropertyValue(
                    inspector,
                    key,
                    ReplayElementField(field));
            }
            replayElementFieldValues["entityId"] = inspector.GetTextInput("replayElementEntityId")
                ?.GetText() ?? ReplayElementField("entityId");
            replayElementFieldValues["entityActorKey"] = inspector.GetTextInput("replayElementEntityActorKey")
                ?.GetText() ?? ReplayElementField("entityActorKey");
            replayElementFieldValues["blockCode"] = inspector.GetTextInput("replayElementBlockCode")
                ?.GetText() ?? ReplayElementField("blockCode");
            replayElementFieldValues["audioAsset"] = inspector.GetTextInput("replayElementAudioAsset")
                ?.GetText() ?? ReplayElementField("audioAsset");
            actionPreviewSpeedText = inspector.GetNumberInput("actionSpeed")?.GetText()
                ?? actionPreviewSpeedText;
            actionPreviewLoopCountText = inspector.GetNumberInput("actionLoopCount")?.GetText()
                ?? actionPreviewLoopCountText;
            actionRecordStartText = inspector.GetNumberInput("actionRecordStart")?.GetText()
                ?? actionRecordStartText;
            actionRecordEndText = inspector.GetNumberInput("actionRecordEnd")?.GetText()
                ?? actionRecordEndText;
            actionTrimStartText = inspector.GetNumberInput("actionTrimStart")?.GetText()
                ?? actionTrimStartText;
            actionTrimEndText = inspector.GetNumberInput("actionTrimEnd")?.GetText()
                ?? actionTrimEndText;
            actionImportPathText = inspector.GetTextInput("actionImportPath")?.GetText()
                ?? actionImportPathText;
            actionEventTimeText = inspector.GetNumberInput("actionEventTime")?.GetText()
                ?? actionEventTimeText;
            actionEventCodeText = inspector.GetTextInput("actionEventCode")?.GetText()
                ?? actionEventCodeText;
            modelNameText = inspector.GetTextInput("modelName")?.GetText() ?? modelNameText;
            modelCodeText = inspector.GetTextInput("modelCode")?.GetText() ?? modelCodeText;
            modelGroupText = inspector.GetTextInput("modelGroup")?.GetText() ?? modelGroupText;
            modelDescriptionText = inspector.GetTextInput("modelDescription")?.GetText()
                ?? modelDescriptionText;
            modelSizeText = inspector.GetNumberInput("modelSize")?.GetText() ?? modelSizeText;
            modelShapeAssetText = inspector.GetTextInput("modelShapeAsset")?.GetText() ?? modelShapeAssetText;
            modelShapeSourceText = inspector.GetTextInput("modelShapeSource")?.GetText() ?? modelShapeSourceText;
        }
        if (section == DirectorWorkspaceSection.Replay
            && replayFloatingEditorKind != ReplayFloatingEditorKind.None)
        {
            CaptureReplayFloatingEditorInputs();
        }
    }

    private void CaptureReplayPropertyInputs(GuiComposer composer)
    {
        replayViewportNameText = composer.GetTextInput("replayViewportName")?.GetText()
            ?? replayViewportNameText;
        replayViewportStartText = composer.GetNumberInput("replayViewportStart")?.GetText()
            ?? replayViewportStartText;
        replayViewportXText = composer.GetNumberInput("replayViewportX")?.GetText()
            ?? replayViewportXText;
        replayViewportYText = composer.GetNumberInput("replayViewportY")?.GetText()
            ?? replayViewportYText;
        replayViewportZText = composer.GetNumberInput("replayViewportZ")?.GetText()
            ?? replayViewportZText;
        replayViewportDimensionText = composer.GetNumberInput("replayViewportDimension")?.GetText()
            ?? replayViewportDimensionText;
        replayViewportYawText = composer.GetNumberInput("replayViewportYaw")?.GetText()
            ?? replayViewportYawText;
        replayViewportPitchText = composer.GetNumberInput("replayViewportPitch")?.GetText()
            ?? replayViewportPitchText;
        replayViewportRollText = composer.GetNumberInput("replayViewportRoll")?.GetText()
            ?? replayViewportRollText;
        foreach (string field in ReplayElementNumericFields)
        {
            string key = "replayElement" + UpperFirst(field);
            replayElementFieldValues[field] = ReadReplayPropertyValue(
                composer,
                key,
                ReplayElementField(field));
        }
        replayElementFieldValues["entityId"] = composer.GetTextInput("replayElementEntityId")
            ?.GetText() ?? ReplayElementField("entityId");
        replayElementFieldValues["entityActorKey"] = composer.GetTextInput("replayElementEntityActorKey")
            ?.GetText() ?? ReplayElementField("entityActorKey");
        replayElementFieldValues["blockCode"] = composer.GetTextInput("replayElementBlockCode")
            ?.GetText() ?? ReplayElementField("blockCode");
        replayElementFieldValues["audioAsset"] = composer.GetTextInput("replayElementAudioAsset")
            ?.GetText() ?? ReplayElementField("audioAsset");
    }

    private void RestoreInspectorInputs()
    {
        suppressAutoSave = true;
        try
        {
            RestoreInspectorInputsCore();
        }
        finally
        {
            suppressAutoSave = false;
        }
    }

    private void RestoreInspectorInputsCore()
    {
        GuiComposer? composer = Composers[InspectorKey];
        if (composer is null)
        {
            return;
        }

        SetInput(composer, "cameraName", cameraEdit?.Name);
        SetInput(composer, "cameraTags", cameraEdit is null ? null : string.Join(", ", cameraEdit.Tags));
        SetInput(composer, "cameraDescription", cameraEdit?.Description);
        SetPlaceholder(composer, "cameraTags", "vsdirector:tags-placeholder");
        SetPlaceholder(composer, "cameraDescription", "vsdirector:description-placeholder");
        SetInput(composer, "replayMetadataName", replayMetadataName);
        SetInput(composer, "replayMetadataTags", replayMetadataTags);
        SetInput(composer, "replayMetadataDescription", replayMetadataDescription);
        SetPlaceholder(composer, "replayMetadataTags", "vsdirector:tags-placeholder");
        SetPlaceholder(composer, "replayMetadataDescription", "vsdirector:description-placeholder");
        if (cameraEdit is not null)
        {
            (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
                cameraEdit.X, cameraEdit.Y, cameraEdit.Z);
            SetNumber(composer, "cameraPosX", displayX);
            SetNumber(composer, "cameraPosY", displayY);
            SetNumber(composer, "cameraPosZ", displayZ);
            SetNumber(composer, "cameraYaw", cameraEdit.Yaw);
            SetNumber(composer, "cameraPitch", cameraEdit.Pitch);
            SetNumber(composer, "cameraDimension", cameraEdit.Dimension);
        }
        SetInput(composer, "cameraFov", cameraEdit is null
            ? null
            : DirectorNumberFormatting.Format(cameraEdit.Fov));
        SetInput(composer, "cameraRoll", cameraEdit is null
            ? null
            : DirectorNumberFormatting.Format(cameraEdit.Roll));
        SetInput(composer, "pathName", pathEdit?.Name);
        SetInput(composer, "pathTags", pathEdit is null ? pathTagsText : string.Join(", ", pathEdit.Tags));
        SetInput(composer, "pathDescription", pathEdit?.Description ?? pathDescriptionText);
        SetPlaceholder(composer, "pathTags", "vsdirector:tags-placeholder");
        SetPlaceholder(composer, "pathDescription", "vsdirector:description-placeholder");
        SetInput(composer, "sequenceName", sequenceEdit?.Name);
        SetInput(composer, "workflowName", workflowEdit?.Name);
        SetInput(composer, "actionName", actionEdit?.Name);
        SetInput(composer, "actionTags", actionTagsText);
        SetInput(composer, "actionAuthor", actionAuthorText);
        SetInput(composer, "actionVersion", actionVersionText);
        SetInput(composer, "actionDescription", actionDescriptionText);
        SetPlaceholder(composer, "actionTags", "vsdirector:tags-placeholder");
        SetPlaceholder(composer, "actionAuthor", "vsdirector:author-placeholder");
        SetPlaceholder(composer, "actionDescription", "vsdirector:description-placeholder");
        SetInput(composer, "actionImportPath", actionImportPathText);
        SetPlaceholder(composer, "actionImportPath", "vsdirector:action-import-path-placeholder");
        SetInput(composer, "actionEventCode", actionEventCodeText);
        SetPlaceholder(composer, "actionEventCode", "vsdirector:action-event-code-placeholder");
        SetInput(composer, "modelName", modelNameText);
        SetInput(composer, "modelCode", modelCodeText);
        SetInput(composer, "modelEntityCode", modelEntityCodeText);
        SetInput(composer, "modelGroup", modelGroupText);
        SetInput(composer, "modelDescription", modelDescriptionText);
        SetPlaceholder(composer, "modelName", "vsdirector:model-name-placeholder");
        SetPlaceholder(composer, "modelCode", "vsdirector:model-code-placeholder");
        SetPlaceholder(composer, "modelEntityCode", "vsdirector:entity-code-placeholder");
        SetPlaceholder(composer, "modelGroup", "vsdirector:model-group-placeholder");
        SetPlaceholder(composer, "modelDescription", "vsdirector:description-placeholder");
        SetInput(composer, "modelShapeAsset", modelShapeAssetText);
        SetInput(composer, "modelShapeSource", modelShapeSourceText);
        SetPlaceholder(composer, "modelShapeAsset", "vsdirector:model-shape-asset-placeholder");
        SetPlaceholder(composer, "modelShapeSource", "vsdirector:model-shape-source-placeholder");
        SetNumber(composer, "modelSize", ParseDouble(modelSizeText, 1d));
        SetNumber(composer, "actionSpeed", ParseDouble(actionPreviewSpeedText, 1d));
        SetNumber(composer, "actionLoopCount", ParseDouble(actionPreviewLoopCountText, 0d));
        SetNumber(composer, "actionRecordStart", ParseDouble(actionRecordStartText, 0d));
        SetNumber(composer, "actionRecordEnd", ParseDouble(actionRecordEndText, owner.ReplayDurationSeconds));
        SetNumber(composer, "actionTrimStart", ParseDouble(actionTrimStartText, 0d));
        SetNumber(composer, "actionTrimEnd", ParseDouble(actionTrimEndText, actionEdit?.DurationSeconds ?? 0d));
        SetNumber(composer, "actionLayerWeight", ParseDouble(actionLayerWeightText, 1d));
        SetNumber(composer, "actionLayerFadeIn", ParseDouble(actionLayerFadeInText, 0d));
        SetNumber(composer, "actionLayerFadeOut", ParseDouble(actionLayerFadeOutText, 0d));
        SetNumber(composer, "actionEventTime", ParseDouble(actionEventTimeText, actionPlayhead));
        SetInput(composer, "workflowAudioPath", workflowAudioPath);
        SetPlaceholder(composer, "workflowAudioPath", "vsdirector:audio-path-placeholder");
        DirectorWorkflowCard? workflowCard = SelectedWorkflowCard();
        SetInput(composer, "workflowCardName", workflowCard?.Name);
        if (workflowCard is not null)
        {
            composer.GetTextArea("workflowNotes")?.SetValue(workflowCard.Notes);
        }
        SetInput(composer, "followName", followEdit?.Name);
        SetInput(composer, "followTags", followEdit is null ? null : string.Join(", ", followEdit.Tags));
        SetInput(composer, "followDescription", followEdit?.Description);
        SetPlaceholder(composer, "followTags", "vsdirector:tags-placeholder");
        SetPlaceholder(composer, "followDescription", "vsdirector:description-placeholder");
        SetInput(composer, "replayMarkerLabel", replayMarkerLabelText);
        SetPlaceholder(composer, "replayMarkerLabel", "vsdirector:marker-label-placeholder");
        SetNumber(composer, "replayMarkerTime", ParseDouble(replayMarkerTimeText, 0d));
        SetNumber(composer, "replaySpeedTime", ParseDouble(replaySpeedTimeText, 0d));
        RestoreReplayTimelineValue(
            composer,
            "replaySpeedValue",
            ParseDouble(replaySpeedValueText, 1d));
        RestoreReplayTimelineValue(
            composer,
            "replayBaseSpeed",
            ParseDouble(replayBaseSpeedText, owner.ReplayBasePlaybackSpeed));
        SetInput(composer, "replayViewportName", replayViewportNameText);
        SetNumber(composer, "replayViewportStart", ParseDouble(replayViewportStartText, 0d));
        SetNumber(composer, "replayViewportX", ParseDouble(replayViewportXText, 0d));
        SetNumber(composer, "replayViewportY", ParseDouble(replayViewportYText, 0d));
        SetNumber(composer, "replayViewportZ", ParseDouble(replayViewportZText, 0d));
        SetNumber(composer, "replayViewportDimension", ParseDouble(replayViewportDimensionText, 0d));
        SetNumber(composer, "replayViewportYaw", ParseDouble(replayViewportYawText, 0d));
        SetNumber(composer, "replayViewportPitch", ParseDouble(replayViewportPitchText, 0d));
        SetNumber(composer, "replayViewportRoll", ParseDouble(replayViewportRollText, 0d));
        foreach (string field in ReplayElementNumericFields)
        {
            RestoreReplayPropertyValue(
                composer,
                "replayElement" + UpperFirst(field),
                ReplayElementNumber(field));
        }
        SetInput(composer, "replayElementEntityId", ReplayElementField("entityId"));
        SetInput(composer, "replayElementBlockCode", ReplayElementField("blockCode"));
        SetInput(composer, "replayElementAudioAsset", ReplayElementField("audioAsset"));
        composer.GetSwitch("replayElementSplit")?.SetValue(ReplayElementField("split") == "true");
        composer.GetSwitch("replayElementFrozen")?.SetValue(ReplayElementField("frozen") == "true");
        DirectorReplayViewportClip? replayViewportClip = SelectedReplayViewportClip();
        composer.GetSwitch("replayViewportLoop")?.SetValue(replayViewportClip?.Loop ?? false);
        composer.GetSwitch("takeReplay")?.SetValue(owner.RecordReplayWithVideo);
        RestoreReplayVisualInputs(composer);
        SetNumber(composer, "cameraFov", cameraEdit?.Fov);
        SetNumber(composer, "cameraRoll", cameraEdit?.Roll);
        SetNumber(composer, "pathFrameStart", ParseDouble(pathFrameStartText, 0d));
        SetNumber(composer, "pathFrameDuration", ParseDouble(
            pathFrameDurationText,
            CameraPathTiming.DefaultKeyframeDurationSeconds));
        SetNumber(composer, "pathSegmentStartSpeed", ParseDouble(
            pathSegmentStartSpeedText,
            CameraPathTiming.DefaultSegmentSpeed));
        SetNumber(composer, "pathSegmentEndSpeed", ParseDouble(
            pathSegmentEndSpeedText,
            CameraPathTiming.DefaultSegmentSpeed));
        SetInput(composer, "pathSegmentMarker", pathSegmentMarkerText);
        SetPlaceholder(composer, "pathSegmentMarker", "vsdirector:segment-marker-placeholder");
        SetNumber(composer, "pathInsertDuration", ParseDouble(
            pathInsertDurationText,
            CameraPathTiming.DefaultKeyframeDurationSeconds));
        SetNumber(composer, "pathInsertStart", ParseDouble(pathInsertStartText, previewPathTime));
        if (pathEdit is not null)
        {
            GuiElementDirectorSlider? pathSpeed = composer.GetDirectorSlider("pathSpeed");
            pathSpeed?.SetValues(
                (int)Math.Round(pathEdit.PlaybackSpeed * 100d),
                5,
                400,
                5);
            if (pathSpeed is not null)
            {
                pathSpeed.OnSliderTooltip = value =>
                    (value / 100d).ToString("0.##", CultureInfo.InvariantCulture) + "x";
            }
            composer.GetSwitch("pathPreview")?.SetValue(pathEdit.PreviewVisible);
            SetNumber(composer, "pathTotalDuration", pathEdit.DurationSeconds);
            CameraKeyframe? selectedFrame = SelectedPathFrame();
            if (selectedFrame is not null)
            {
                (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
                    selectedFrame.X, selectedFrame.Y, selectedFrame.Z);
                SetNumber(composer, "pathFrameHold", selectedFrame.HoldSeconds);
                SetInput(composer, "pathKeyframeName", selectedFrame.Name);
                SetPlaceholder(composer, "pathKeyframeName", "vsdirector:keyframe-name-placeholder");
                SetNumber(composer, "pathPosX", displayX);
                SetNumber(composer, "pathPosY", displayY);
                SetNumber(composer, "pathPosZ", displayZ);
                SetNumber(composer, "pathYaw", selectedFrame.Yaw);
                SetNumber(composer, "pathPitch", selectedFrame.Pitch);
                composer.GetSwitch("pathBezierEnabled")?.SetValue(selectedFrame.BezierHandlesEnabled);
                SetNumber(composer, "pathBezierInX", selectedFrame.BezierInX);
                SetNumber(composer, "pathBezierInY", selectedFrame.BezierInY);
                SetNumber(composer, "pathBezierInZ", selectedFrame.BezierInZ);
                SetNumber(composer, "pathBezierOutX", selectedFrame.BezierOutX);
                SetNumber(composer, "pathBezierOutY", selectedFrame.BezierOutY);
                SetNumber(composer, "pathBezierOutZ", selectedFrame.BezierOutZ);
            }
            bool hasSegment = SelectedPathKeyframeIndex() >= 0
                && SelectedPathKeyframeIndex() < pathEdit.Keyframes.Count - 1;
            if (composer.GetNumberInput("pathSegmentStartSpeed") is { } segmentStart)
            {
                segmentStart.Enabled = hasSegment;
            }
            if (composer.GetNumberInput("pathSegmentEndSpeed") is { } segmentEnd)
            {
                segmentEnd.Enabled = hasSegment;
            }
        }
        SetNumber(composer, "shotDuration", sequenceEdit is null
            ? null
            : CameraSequenceSampler.DefaultShotDurationSeconds);
        if (sequenceEdit is not null)
        {
            int first = sequencePageIndex * CurrentSequenceRowsPerPage;
            int last = Math.Min(first + CurrentSequenceRowsPerPage, sequenceEdit.Shots.Count);
            for (int index = first; index < last; index++)
            {
                SetNumber(composer, "shot-" + index, sequenceEdit.Shots[index].DurationSeconds);
            }
        }
        SetNumber(composer, "workflowStart", workflowCard?.StartSeconds);
        SetNumber(composer, "workflowEnd", workflowCard?.EndSeconds);
        SetNumber(composer, "workflowLane", workflowCard?.Lane);
        SetInput(composer, "workflowEntityCode", workflowCard?.EntityCode);
        SetPlaceholder(composer, "workflowEntityCode", "vsdirector:workflow-entity-code-placeholder");
        SetInput(composer, "workflowEnvironmentCode", workflowCard?.EnvironmentCode);
        SetInput(composer, "workflowEnvironmentData", workflowCard?.EnvironmentData);
        SetPlaceholder(composer, "workflowEnvironmentData", "vsdirector:environment-data-placeholder");
        SetNumber(composer, "workflowSourceOffset", workflowCard?.SourceOffsetSeconds);
        SetNumber(composer, "workflowPlaybackSpeed", workflowCard?.PlaybackSpeed);
        SetNumber(composer, "workflowTransitionDuration",
            workflowCard is null ? null : WorkflowTransitionDuration(workflowCard));
        SetNumber(composer, "workflowAudioTimelineStart", ParseDouble(workflowAudioTimelineStart, 0d));
        SetNumber(composer, "workflowAudioSourceStart", ParseDouble(workflowAudioSourceStart, 0d));
        SetNumber(composer, "workflowAudioDuration", ParseDouble(workflowAudioDuration, 5d));
        SetNumber(composer, "workflowAudioTrack", ParseDouble(workflowAudioTrack, 0d));
        SetNumber(composer, "workflowAudioGain", ParseDouble(workflowAudioGain, 0d));
        SetNumber(composer, "workflowAudioFadeIn", ParseDouble(workflowAudioFadeIn, 0d));
        SetNumber(composer, "workflowAudioFadeOut", ParseDouble(workflowAudioFadeOut, 0d));
        SetNumber(composer, "workflowAudioRate", ParseDouble(workflowAudioRate, 1d));
        composer.GetSwitch("workflowAudioEnabled")?.SetValue(workflowAudioEnabled);
        composer.GetSwitch("workflowCardEnabled")?.SetValue(workflowCard?.Enabled ?? false);
        composer.GetSwitch("workflowHideLiveActor")?.SetValue(workflowCard?.HideLiveActor ?? true);
        SetButtonEnabledIfPresent(composer, "applyReplayBaseSpeed", owner.IsReplayPlaying);
        suppressFollowPreview = true;
        try
        {
            SetNumber(composer, "followOffsetX", followEdit?.OffsetX);
            SetNumber(composer, "followOffsetY", followEdit?.OffsetY);
            SetNumber(composer, "followOffsetZ", followEdit?.OffsetZ);
            SetNumber(composer, "followDistance", followEdit?.Distance);
            SetNumber(composer, "followHeight", followEdit?.Height);
            SetNumber(composer, "followFov", followEdit?.Fov);
            SetNumber(composer, "followOrbitAngle", followEdit?.OrbitAngleDegrees);
            SetNumber(composer, "followOrbitSpeed", followEdit?.OrbitSpeedDegreesPerSecond);
            SetNumber(composer, "followSmooth", followEdit?.SmoothTimeSeconds);
            SetNumber(composer, "followDelay", followEdit?.DelaySeconds);
            composer.GetSwitch("followClearView")?.SetValue(followEdit?.OpenClearView ?? false);
            if (composer.GetSwitch("followClearView") is { } clearViewSwitch)
            {
                clearViewSwitch.Enabled = owner.HasClearView;
            }
            if (followEdit is not null)
            {
                SetDirectorSlider(composer, "followOrbitVisual",
                    (int)Math.Round(followEdit.OrbitAngleDegrees), -180, 180, 1,
                    value => value.ToString(CultureInfo.InvariantCulture) + " deg");
                SetDirectorSlider(composer, "followDistanceVisual",
                    (int)Math.Round(Math.Clamp(followEdit.Distance, 0d, 100d) * 100d), 0, 10000, 5,
                    value => (value / 100d).ToString("0.00", CultureInfo.InvariantCulture));
                SetDirectorSlider(composer, "followHeightVisual",
                    (int)Math.Round(Math.Clamp(followEdit.Height, -50d, 50d) * 100d), -5000, 5000, 5,
                    value => (value / 100d).ToString("0.00", CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            suppressFollowPreview = false;
        }
    }

    private static void SetDirectorSlider(
        GuiComposer composer,
        string key,
        int value,
        int minimum,
        int maximum,
        int step,
        SliderTooltipDelegate tooltip)
    {
        if (composer.GetElement(key) is not GuiElementDirectorSlider slider)
        {
            return;
        }
        slider.OnSliderTooltip = tooltip;
        slider.SetValues(value, minimum, maximum, step);
    }

    private static void SetInput(GuiComposer composer, string key, string? value)
    {
        if (value is not null && composer.GetElement(key) is GuiElementTextInput input)
        {
            input.SetValue(value);
        }
    }

    private static void SetPlaceholder(GuiComposer composer, string key, string langKey)
    {
        if (composer.GetElement(key) is GuiElementTextInput input)
        {
            input.SetPlaceHolderText(Lang.Get(langKey));
        }
    }

    private static void SetNumber(GuiComposer composer, string key, double? value)
    {
        if (value is double number
            && double.IsFinite(number)
            && composer.GetElement(key) is GuiElementNumberInput input)
        {
            input.SetValue(DirectorNumberFormatting.Format(number));
        }
    }

    private Action<string> AutoSaveOnChanged(AutoSaveTarget target)
        => _ => QueueAutoSave(target);

    private Action AutoSaveOnFocusLost(AutoSaveTarget target)
        => () => FlushPendingAutoSave(target);

    private void QueueAutoSave(AutoSaveTarget target)
    {
        if (suppressAutoSave || target == AutoSaveTarget.None || !TryGetAutoSaveIdentity(
            target,
            out Guid resourceId,
            out Guid childId,
            out int index))
        {
            return;
        }

        pendingAutoSaveTarget = target;
        pendingAutoSaveResourceId = resourceId;
        pendingAutoSaveChildId = childId;
        pendingAutoSaveIndex = index;
        autoSaveStatusTarget = target;
        autoSaveStatusResourceId = resourceId;
        autoSaveStatusChildId = childId;
        autoSaveStatusIndex = index;
        autoSaveAccumulator = 0f;
        autoSaveState = AutoSaveState.Pending;
        autoSaveError = string.Empty;
        UpdateAutoSaveStatusText();
    }

    private void FlushPendingAutoSave(AutoSaveTarget expectedTarget = AutoSaveTarget.None)
    {
        AutoSaveTarget target = pendingAutoSaveTarget;
        if (target == AutoSaveTarget.None
            || expectedTarget != AutoSaveTarget.None && expectedTarget != target)
        {
            return;
        }

        pendingAutoSaveTarget = AutoSaveTarget.None;
        autoSaveAccumulator = 0f;
        if (!AutoSaveIdentityMatches(target))
        {
            autoSaveState = AutoSaveState.Saved;
            autoSaveError = string.Empty;
            UpdateAutoSaveStatusText();
            return;
        }

        autoSaveState = AutoSaveState.Saving;
        UpdateAutoSaveStatusText();
        bool saved = target switch
        {
            AutoSaveTarget.Camera => TryAutoSaveCamera(out autoSaveError),
            AutoSaveTarget.Follow => TryAutoSaveFollow(out autoSaveError),
            AutoSaveTarget.Sequence => TryAutoSaveSequence(out autoSaveError),
            AutoSaveTarget.ActionMetadata => TryAutoSaveActionMetadata(out autoSaveError),
            AutoSaveTarget.ModelMetadata => TryAutoSaveModelMetadata(out autoSaveError),
            AutoSaveTarget.ReplayMetadata => TryAutoSaveReplayMetadata(out autoSaveError),
            AutoSaveTarget.WorkflowCard => TryAutoSaveWorkflowCard(out autoSaveError),
            AutoSaveTarget.Path => TryAutoSavePath(out autoSaveError),
            AutoSaveTarget.PathKeyframe => TryAutoSavePathKeyframe(out autoSaveError),
            AutoSaveTarget.PathMetadata => TryAutoSavePathMetadata(out autoSaveError),
            _ => true
        };
        autoSaveState = saved ? AutoSaveState.Saved : AutoSaveState.Invalid;
        if (saved)
        {
            autoSaveError = string.Empty;
        }
        UpdateAutoSaveStatusText();
    }

    private bool TryGetAutoSaveIdentity(
        AutoSaveTarget target,
        out Guid resourceId,
        out Guid childId,
        out int index)
    {
        childId = Guid.Empty;
        index = -1;
        resourceId = target switch
        {
            AutoSaveTarget.Camera => cameraEdit?.Id ?? Guid.Empty,
            AutoSaveTarget.Follow => followEdit?.Id ?? Guid.Empty,
            AutoSaveTarget.Sequence => sequenceEdit?.Id ?? Guid.Empty,
            AutoSaveTarget.ActionMetadata => actionEdit?.Id ?? Guid.Empty,
            AutoSaveTarget.ModelMetadata => modelEdit?.Id ?? Guid.Empty,
            AutoSaveTarget.ReplayMetadata => SelectedReplayInfo()?.Id ?? Guid.Empty,
            AutoSaveTarget.WorkflowCard => workflowEdit?.Id ?? Guid.Empty,
            AutoSaveTarget.Path => pathEdit?.Id ?? Guid.Empty,
            AutoSaveTarget.PathKeyframe => pathEdit?.Id ?? Guid.Empty,
            AutoSaveTarget.PathMetadata => pathEdit?.Id ?? Guid.Empty,
            _ => Guid.Empty
        };
        if (target == AutoSaveTarget.WorkflowCard)
        {
            childId = SelectedWorkflowCard()?.Id ?? Guid.Empty;
        }
        else if (target == AutoSaveTarget.PathKeyframe)
        {
            index = SelectedPathKeyframeIndex();
        }

        return target == AutoSaveTarget.ModelMetadata && modelEdit is not null
            || resourceId != Guid.Empty
                && (target != AutoSaveTarget.WorkflowCard || childId != Guid.Empty)
                && (target != AutoSaveTarget.PathKeyframe || index >= 0);
    }

    private bool AutoSaveIdentityMatches(AutoSaveTarget target)
        => TryGetAutoSaveIdentity(target, out Guid resourceId, out Guid childId, out int index)
            && resourceId == pendingAutoSaveResourceId
            && childId == pendingAutoSaveChildId
            && index == pendingAutoSaveIndex;

    private AutoSaveTarget CurrentAutoSaveTargetForInspector()
        => section switch
        {
            DirectorWorkspaceSection.Cameras when cameraEdit is not null => AutoSaveTarget.Camera,
            DirectorWorkspaceSection.Follows when followEdit is not null => AutoSaveTarget.Follow,
            DirectorWorkspaceSection.SequenceEditor when sequenceEdit is not null => AutoSaveTarget.Sequence,
            DirectorWorkspaceSection.Actions => actionLibraryMode == "models"
                ? modelEdit is not null && inspectorSection == "basic"
                    ? AutoSaveTarget.ModelMetadata
                    : AutoSaveTarget.None
                : actionEdit is not null && actionInspectorSection == "basic"
                    ? AutoSaveTarget.ActionMetadata
                    : AutoSaveTarget.None,
            DirectorWorkspaceSection.Replay when replayRightTab == "inspector"
                && SelectedReplayInfo() is not null => AutoSaveTarget.ReplayMetadata,
            DirectorWorkspaceSection.Workflows when workflowEdit is not null
                && inspectorSection == "card"
                && SelectedWorkflowCard() is not null => AutoSaveTarget.WorkflowCard,
            DirectorWorkspaceSection.Paths when pathEdit is not null
                && pathRightTab == "properties" => AutoSaveTarget.PathMetadata,
            DirectorWorkspaceSection.Paths when pathEdit is not null => AutoSaveTarget.Path,
            _ => AutoSaveTarget.None
        };

    private bool HasAutoSaveFieldsForCurrentInspector()
    {
        AutoSaveTarget target = CurrentAutoSaveTargetForInspector();
        return target is not (AutoSaveTarget.None
            or AutoSaveTarget.Camera
            or AutoSaveTarget.Follow
            or AutoSaveTarget.ReplayMetadata
            or AutoSaveTarget.Path
            or AutoSaveTarget.PathMetadata)
            && TryGetAutoSaveIdentity(target, out _, out _, out _);
    }

    private void PrepareAutoSaveStatusForCurrentInspector()
    {
        AutoSaveTarget target = CurrentAutoSaveTargetForInspector();
        if (target == AutoSaveTarget.None
            || !TryGetAutoSaveIdentity(target, out Guid resourceId, out Guid childId, out int index)
            || target == autoSaveStatusTarget
                && resourceId == autoSaveStatusResourceId
                && childId == autoSaveStatusChildId
                && index == autoSaveStatusIndex)
        {
            return;
        }

        autoSaveStatusTarget = target;
        autoSaveStatusResourceId = resourceId;
        autoSaveStatusChildId = childId;
        autoSaveStatusIndex = index;
        autoSaveState = AutoSaveState.Saved;
        autoSaveError = string.Empty;
    }

    private string AutoSaveStatusText()
        => autoSaveState switch
        {
            AutoSaveState.Pending => Lang.Get("vsdirector:auto-save-pending"),
            AutoSaveState.Saving => Lang.Get("vsdirector:auto-save-saving"),
            AutoSaveState.Invalid => Lang.Get(
                "vsdirector:auto-save-invalid",
                string.IsNullOrWhiteSpace(autoSaveError)
                    ? Lang.Get("vsdirector:auto-save-invalid-input")
                    : autoSaveError),
            _ => Lang.Get("vsdirector:auto-save-saved")
        };

    private void UpdateAutoSaveStatusText()
        => Composers[InspectorKey]?.GetDynamicText("autoSaveStatus")?.SetNewText(
            AutoSaveStatusText(), false, true, false);

    private bool TryAutoSaveCamera(out string error)
    {
        error = string.Empty;
        if (cameraEdit is null || Composers[InspectorKey] is not GuiComposer composer
            || !ValidateNumberInputs(composer, out error,
                "cameraPosX", "cameraPosY", "cameraPosZ", "cameraYaw", "cameraPitch",
                "cameraRoll", "cameraFov", "cameraDimension"))
        {
            return false;
        }
        CaptureCameraInputs(composer);
        if (!owner.TryUpdateCamera(cameraEdit, out error, showNotification: false))
        {
            return false;
        }
        cameraEdit = owner.FindCamera(cameraEdit.Id) ?? cameraEdit;
        return true;
    }

    private bool TryAutoSaveFollow(out string error)
    {
        error = string.Empty;
        if (followEdit is null || Composers[InspectorKey] is not GuiComposer composer
            || !ValidateNumberInputs(composer, out error,
                "followOffsetX", "followOffsetY", "followOffsetZ", "followDistance",
                "followHeight", "followFov", "followOrbitAngle", "followOrbitSpeed",
                "followSmooth", "followDelay"))
        {
            return false;
        }
        CaptureFollowInputs(composer);
        if (!owner.TrySaveFollowRig(followEdit, out error, showNotification: false))
        {
            return false;
        }
        followEdit = owner.FindFollowRig(followEdit.Id) ?? followEdit;
        return true;
    }

    private bool TryAutoSaveSequence(out string error)
    {
        error = string.Empty;
        if (sequenceEdit is null || !CaptureSequenceDurations())
        {
            error = Lang.Get("vsdirector:auto-save-invalid-input");
            return false;
        }
        sequenceEdit.Name = Composers[InspectorKey].GetTextInput("sequenceName")?.GetText()
            ?? sequenceEdit.Name;
        if (!owner.TrySaveSequence(sequenceEdit, out error, showNotification: false))
        {
            return false;
        }
        sequenceEdit = owner.FindSequence(sequenceEdit.Id) ?? sequenceEdit;
        return true;
    }

    private bool TryAutoSaveActionMetadata(out string error)
    {
        error = string.Empty;
        if (actionEdit is null)
        {
            return false;
        }
        CaptureActionInputs();
        if (!owner.TrySaveAction(actionEdit, out error, showNotification: false))
        {
            return false;
        }
        actionEdit = owner.Actions.FirstOrDefault(action => action.Id == actionEdit.Id)?.DeepClone()
            ?? actionEdit;
        return true;
    }

    private bool TryAutoSaveModelMetadata(out string error)
    {
        error = string.Empty;
        if (modelEdit is null || Composers[InspectorKey] is not GuiComposer composer
            || !ValidateNumberInputs(composer, out error, "modelSize"))
        {
            return false;
        }
        CaptureModelInputs();
        if (!owner.TrySaveModel(
            modelEdit,
            out DirectorModelAsset? saved,
            out error,
            showNotification: false) || saved is null)
        {
            return false;
        }
        modelEdit = saved;
        actionLibrarySelectedModelCode = saved.ModelCode;
        actionPreviewModelCode = saved.ModelCode;
        return true;
    }

    private bool TryAutoSaveReplayMetadata(out string error)
    {
        error = string.Empty;
        DirectorReplayInfo? selected = SelectedReplayInfo();
        if (selected is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return false;
        }

        CaptureReplayMetadataInputs(composer);
        if (!owner.TryUpdateReplayMetadata(
                selected.Id,
                replayMetadataName,
                replayMetadataTags,
                replayMetadataDescription,
                out error,
                showNotification: false,
                refreshWorkspace: false))
        {
            return false;
        }

        replayMetadataFieldsId = Guid.Empty;
        if (SelectedReplayInfo() is DirectorReplayInfo updated)
        {
            SyncReplayMetadataFields(updated);
        }
        ComposeResources();
        return true;
    }

    private bool TryAutoSaveWorkflowCard(out string error)
    {
        error = string.Empty;
        if (workflowEdit is null || SelectedWorkflowCard() is null
            || Composers[InspectorKey] is not GuiComposer composer
            || !ValidateNumberInputs(composer, out error,
                "workflowStart", "workflowEnd", "workflowCountdownDuration", "workflowLane",
                "workflowSourceOffset", "workflowPlaybackSpeed", "workflowTransitionDuration"))
        {
            return false;
        }
        CaptureWorkflowInputs();
        Guid workflowId = workflowEdit.Id;
        Guid cardId = selectedWorkflowCardId;
        if (!owner.TrySaveWorkflow(workflowEdit, out error, showNotification: false))
        {
            return false;
        }
        workflowEdit = owner.FindWorkflow(workflowId) ?? workflowEdit;
        selectedWorkflowCardId = workflowEdit.Cards.Any(card => card.Id == cardId)
            ? cardId
            : workflowEdit.Cards.FirstOrDefault()?.Id ?? Guid.Empty;
        return true;
    }

    private bool TryAutoSavePath(out string error)
    {
        error = string.Empty;
        if (pathEdit is null || Composers[InspectorKey] is not GuiComposer composer
            || !ValidateNumberInputs(composer, out error,
                "pathTotalDuration", "pathFrameStart", "pathFrameDuration", "pathFrameHold",
                "pathSegmentStartSpeed", "pathSegmentEndSpeed", "pathPosX", "pathPosY",
                "pathPosZ", "pathYaw", "pathPitch",
                "pathBezierInX", "pathBezierInY", "pathBezierInZ",
                "pathBezierOutX", "pathBezierOutY", "pathBezierOutZ"))
        {
            return false;
        }
        if (SelectedPathFrame() is not null && !ValidatePathAutoSaveInputs(composer, out error))
        {
            return false;
        }

        CameraPath before = pathEdit.DeepClone();
        pathEdit.Name = composer.GetTextInput("pathName")?.GetText()?.Trim() ?? pathEdit.Name;
        if (SelectedPathFrame() is not null)
        {
            if (!CapturePathSegmentMetadataInputs())
            {
                error = Lang.Get("vsdirector:invalid-segment-speed");
                return false;
            }
            CapturePathKeyframePropertyInputs();
            _ = CapturePathTimingInputs();
        }
        if (pathTotalDurationDirty)
        {
            double requested = ReadNumber(composer, "pathTotalDuration", double.NaN);
            if (!CameraPathTiming.TryScaleTimeline(pathEdit, requested))
            {
                error = Lang.Get("vsdirector:auto-save-invalid-input");
                return false;
            }
            pathEditorPlayhead = Math.Min(pathEditorPlayhead, pathEdit.DurationSeconds);
        }

        RecordPathEdit(before);
        Guid pathId = pathEdit.Id;
        if (!owner.TrySavePath(pathEdit, out error, showNotification: false))
        {
            return false;
        }

        pathTotalDurationDirty = false;
        pathEdit = owner.FindPath(pathId) ?? pathEdit;
        SyncPathTimingText();
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            pathEditorPlayhead,
            selectedKeyframes: selectedPathKeyframes);
        ComposeResources();
        ComposeTimeline();
        return true;
    }

    private bool TryAutoSavePathMetadata(out string error)
    {
        error = string.Empty;
        if (pathEdit is null || Composers[InspectorKey] is not GuiComposer composer)
        {
            return false;
        }

        string name = composer.GetTextInput("pathName")?.GetText()?.Trim() ?? pathEdit.Name;
        if (name.Length == 0)
        {
            error = Lang.Get("vsdirector:path-name-required");
            return false;
        }

        pathTagsText = composer.GetTextInput("pathTags")?.GetText() ?? pathTagsText;
        pathDescriptionText = composer.GetTextInput("pathDescription")?.GetText()
            ?? pathDescriptionText;
        CameraPath copy = pathEdit.DeepClone();
        copy.Name = name;
        copy.Tags = ParseTags(pathTagsText);
        copy.Description = pathDescriptionText.Trim();
        Guid pathId = copy.Id;
        if (!owner.TrySavePath(copy, out error, showNotification: false))
        {
            return false;
        }

        pathEdit = owner.FindPath(pathId) ?? copy;
        pathTagsText = string.Join(", ", pathEdit.Tags);
        pathDescriptionText = pathEdit.Description;
        ComposeResources();
        return true;
    }

    private bool TryAutoSavePathKeyframe(out string error)
    {
        error = string.Empty;
        if (pathEdit is null || SelectedPathFrame() is null
            || Composers[InspectorKey] is not GuiComposer composer
            || !ValidateNumberInputs(composer, out error,
                "pathFrameStart", "pathFrameDuration", "pathFrameHold",
                "pathSegmentStartSpeed", "pathSegmentEndSpeed", "pathPosX", "pathPosY",
                "pathPosZ", "pathYaw", "pathPitch",
                "pathBezierInX", "pathBezierInY", "pathBezierInZ",
                "pathBezierOutX", "pathBezierOutY", "pathBezierOutZ"))
        {
            return false;
        }
        if (!ValidatePathAutoSaveInputs(composer, out error))
        {
            return false;
        }

        CameraPath before = pathEdit.DeepClone();
        if (!CapturePathSegmentMetadataInputs())
        {
            error = Lang.Get("vsdirector:invalid-segment-speed");
            return false;
        }
        CapturePathKeyframePropertyInputs();
        _ = CapturePathTimingInputs();
        RecordPathEdit(before);
        Guid pathId = pathEdit.Id;
        if (!owner.TrySavePath(pathEdit, out error, showNotification: false))
        {
            return false;
        }
        pathEdit = owner.FindPath(pathId) ?? pathEdit;
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
        return true;
    }

    private bool ValidatePathAutoSaveInputs(GuiComposer composer, out string error)
    {
        int index = SelectedPathKeyframeIndex();
        double minimumDuration = pathEdit is not null && index == pathEdit.Keyframes.Count - 1
            ? 0d
            : CameraPathTiming.MinKeyframeGapSeconds;
        if (!ValidateNumberRange(composer, "pathFrameStart", 0d, CameraPathTiming.MaxTimelineSeconds)
            || !ValidateNumberRange(composer, "pathFrameDuration", minimumDuration,
                CameraPathTiming.MaxTimelineSeconds)
            || !ValidateNumberRange(composer, "pathFrameHold", 0d,
                CameraPathTiming.MaxTimelineSeconds))
        {
            error = Lang.Get("vsdirector:auto-save-invalid-input");
            return false;
        }

        if (pathEdit is not null && index >= 0 && index < pathEdit.Keyframes.Count - 1
            && composer.GetNumberInput("pathSegmentStartSpeed") is { } startInput
            && composer.GetNumberInput("pathSegmentEndSpeed") is { } endInput)
        {
            double startSpeed = ParseDouble(startInput.GetText(), double.NaN);
            double endSpeed = ParseDouble(endInput.GetText(), double.NaN);
            if (startSpeed < CameraPathTiming.MinSegmentSpeed
                || startSpeed > CameraPathTiming.MaxSegmentSpeed
                || endSpeed < CameraPathTiming.MinSegmentSpeed
                || endSpeed > CameraPathTiming.MaxSegmentSpeed
                || startSpeed + endSpeed <= CameraPathTiming.TimeMatchEpsilon)
            {
                error = Lang.Get("vsdirector:invalid-segment-speed");
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateNumberRange(
        GuiComposer composer,
        string key,
        double minimum,
        double maximum)
    {
        if (composer.GetNumberInput(key) is not { } input)
        {
            return true;
        }
        double value = ParseDouble(input.GetText(), double.NaN);
        return double.IsFinite(value) && value >= minimum && value <= maximum;
    }

    private static bool ValidateNumberInputs(
        GuiComposer composer,
        out string error,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (composer.GetNumberInput(key) is not { } input)
            {
                continue;
            }
            if (!double.TryParse(
                input.GetText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value) || !double.IsFinite(value))
            {
                error = Lang.Get("vsdirector:auto-save-invalid-input");
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private bool SearchMatch(params string[] values)
        => values.Any(value => value?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    private bool ActionResourceSearchMatch(params string?[] values)
    {
        string query = actionResourceSearch.Trim();
        return query.Length == 0 || values.Any(value => value?.Contains(
            query,
            StringComparison.OrdinalIgnoreCase) == true);
    }

    private bool SearchMatch(string name, string description, IReadOnlyList<string> tags)
        => SearchMatch(new[] { name, description }.Concat(tags ?? Array.Empty<string>()).ToArray());

    private static List<string> ParseTags(string? value)
        => (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void UpdateRuntimeUi()
    {
        if (replayFloatingEditorKind == ReplayFloatingEditorKind.EntityPopup
            && !owner.IsReplayPlaying)
        {
            CloseReplayFloatingEditor();
            ComposeWorkflowCanvas();
        }
        QueueConfiguredReplayView();
        Composers[InspectorKey]?.GetDynamicText("autoSaveStatus")?.SetNewText(
            AutoSaveStatusText(), false, true, false);
        UpdateRmlMenuStatus();
        if (section != DirectorWorkspaceSection.Replay)
        {
            Composers[TimelineKey]?.GetDynamicText("timelineText")?.SetNewText(
                PathTimelineText(), false, true, false);
        }
        Composers[TimelineKey]?.GetDynamicText("pathStatus")?.SetNewText(
            PathSimpleTimelineStatusText(), false, true, false);
        Composers[TimelineKey]?.GetDynamicText("replayViewStatus")?.SetNewText(
            owner.ReplayActiveCameraStatusText(),
            false,
            true,
            false);
        Composers[InspectorKey]?.GetDynamicText("replayLoading")?.SetNewText(ReplayLoadingText(), false, true, false);
        Composers[InspectorKey]?.GetDynamicText("liveStatus")?.SetNewText(
            owner.RecordingStatusText(includeBoundary: false), false, true, false);
        Composers[InspectorKey]?.GetDynamicText("followVisualStatus")?.SetNewText(
            owner.FollowVisualStatusText(), false, true, false);
        Composers[InspectorKey]?.GetDynamicText("actorAttachStatus")?.SetNewText(
            owner.ReplayAttachmentStatusText(), false, true, false);
        Composers[InspectorKey]?.GetDynamicText("preloadSummary")?.SetNewText(
            PreloadGridSummary(), false, true, false);

        floatingEditorDialog.Composer?.GetDynamicText("preloadSummary")?.SetNewText(
            PreloadGridSummary(), false, true, false);
        floatingEditorDialog.Composer?.GetDynamicText("preloadToolbar")?.SetNewText(
            PreloadMapToolbarText(), false, true, false);
        floatingEditorDialog.Composer?.GetDynamicText("pathWorkspaceStatus")?.SetNewText(
            PathWorkspaceStatusText(), false, true, false);
        UpdateReplayTimelineButtonStates();
    }

    private void QueueConfiguredReplayView()
    {
        if (!owner.IsReplayPlaying || owner.IsReplayLoading)
        {
            replayViewAppliedReplayId = Guid.Empty;
            return;
        }
        Guid replayId = owner.ActiveReplayId;
        if (!replayViewConfigured
            || replayId == Guid.Empty
            || replayViewAppliedReplayId == replayId
            || replayViewApplyQueued)
        {
            return;
        }

        replayViewApplyQueued = true;
        capi.Event.EnqueueMainThreadTask(() =>
        {
            replayViewApplyQueued = false;
            if (owner.IsReplayPlaying && owner.ActiveReplayId == replayId)
            {
                if (!ApplyReplayView())
                {
                    replayViewAppliedReplayId = replayId;
                }
            }
        }, "vsdirector-replay-view-auto-apply");
    }

    private void UpdateReplayTimelineButtonStates()
    {
        if (rmlTimeline is not null && section is DirectorWorkspaceSection.Paths or DirectorWorkspaceSection.Replay)
        {
            ComposeRmlTimeline();
            return;
        }
        GuiComposer? composer = Composers[TimelineKey];
        if (section != DirectorWorkspaceSection.Replay || composer is null)
        {
            return;
        }

        bool playing = owner.IsReplayPlaying;
        bool active = playing || owner.IsReplayRecording;
        composer.TryGetButton("pause")?.SetIcon(owner.IsReplayPaused
            ? DirectorIcon.Play
            : DirectorIcon.Pause);
        double speed = owner.ReplayBasePlaybackSpeed;
        composer.TryGetButton("replaySlowDown")?.SetIconTint(
            speed < 1d - 0.000001d ? 126 : 235,
            speed < 1d - 0.000001d ? 178 : 247,
            speed < 1d - 0.000001d ? 255 : 250);
        composer.TryGetButton("replaySpeedUp")?.SetIconTint(
            speed > 1d + 0.000001d ? 112 : 235,
            speed > 1d + 0.000001d ? 224 : 247,
            speed > 1d + 0.000001d ? 150 : 250);
        SetButtonEnabledIfPresent(composer, "pause", active);
        SetButtonEnabledIfPresent(composer, "replaySkipBack", playing);
        SetButtonEnabledIfPresent(composer, "replaySlowDown", playing);
        SetButtonEnabledIfPresent(composer, "replaySpeedUp", playing);
        SetButtonEnabledIfPresent(composer, "replaySkipForward", playing);
        SetButtonEnabledIfPresent(composer, "exit", playing);
        SetButtonEnabledIfPresent(composer, "replayAddElement", playing);
    }

    private static void SetButtonEnabledIfPresent(
        GuiComposer composer,
        string key,
        bool enabled)
    {
        GuiElementDirectorButton? button = composer.TryGetButton(key);
        if (button is not null)
        {
            button.Enabled = enabled;
        }
    }

    private void ComposeReplayRightPanel(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        _ = detail;
        string viewLabel = Lang.Get("vsdirector:replay-right-view");
        string inspectorLabel = Lang.Get("vsdirector:replay-right-inspector");
        double viewWidth = ToolbarButtonWidth(viewLabel);
        double inspectorWidth = ToolbarButtonWidth(inspectorLabel);
        composer
            .AddDirectorToolbarButton(viewLabel,
                () => SetReplayRightTab("visuals"),
                ElementBounds.Fixed(0d, y, viewWidth, 28d),
                replayRightTab == "visuals" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "replayRightVisuals")
            .AddDirectorToolbarButton(inspectorLabel,
                () => SetReplayRightTab("inspector"),
                ElementBounds.Fixed(viewWidth, y, inspectorWidth, 28d),
                replayRightTab == "inspector" ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                "replayRightInspector");
        y += 30;

        if (replayRightTab == "inspector")
        {
            ComposeReplayPropertiesPanel(composer, small, ref y);
            return;
        }

        ComposeReplayVisualsPanel(composer, small, ref y);
    }

    private bool SetReplayRightTab(string tab)
    {
        string nextTab = tab == "inspector" ? "inspector" : "visuals";
        if (replayRightTab == nextTab)
        {
            return true;
        }

        PreserveInputs();
        FlushPendingAutoSave(AutoSaveTarget.ReplayMetadata);
        replayRightTab = nextTab;
        ComposeInspector();
        return true;
    }

    private void ComposeReplayPropertiesPanel(
        GuiComposer composer,
        CairoFont small,
        ref int y)
    {
        if (SelectedReplayInfo() is DirectorReplayInfo selected)
        {
            ComposeReplayMetadataFields(
                composer,
                small,
                selected,
                ref y,
                AutoSaveTarget.ReplayMetadata);
        }
    }

    private void ComposeReplayVisualsPanel(
        GuiComposer composer,
        CairoFont small,
        ref int y)
    {
        ComposeReplayVisualSettings(composer, small, ref y);
    }

    private void ComposeReplayViewportInspector(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        DirectorReplayViewportClip clip,
        ref int y)
    {
        _ = detail;
        SyncReplayViewportFields(clip);
        AddReplayPropertyHeader(composer, small,
            Lang.Get("vsdirector:replay-element-properties"), ref y);
        AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:name"),
            "replayViewportName", replayViewportNameText, ref y);

        AddReplayPropertyNumber(composer, small, Lang.Get("vsdirector:replay-element-time-seconds"),
            "replayViewportStart", ParseDouble(replayViewportStartText, clip.TimelineStartSeconds), ref y);

        string[] interpolationCodes = { "step", "linear", "ease", "bezier" };
        string[] interpolationNames =
        {
            Lang.Get("vsdirector:replay-camera-interpolation-step"),
            Lang.Get("vsdirector:replay-camera-interpolation-linear"),
            Lang.Get("vsdirector:replay-camera-interpolation-ease"),
            Lang.Get("vsdirector:replay-camera-interpolation-bezier")
        };
        composer
            .AddDirectorSingleLineText(Lang.Get("vsdirector:replay-camera-interpolation"), small,
                ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d))
            .AddDropDown(interpolationCodes, interpolationNames,
                Math.Max(0, Array.IndexOf(interpolationCodes, replayViewportInterpolationCode)),
                OnReplayCameraInterpolationChanged,
                ElementBounds.Fixed(ReplayPropertyControlX, y,
                    ReplayPropertyDropDownWidth, 28d),
                "replayViewportInterpolation");
        y += 34;

        AddReplayPropertyNumber(composer, small, "X", "replayViewportX",
            ParseDouble(replayViewportXText, 0d), ref y);
        AddReplayPropertyNumber(composer, small, "Y", "replayViewportY",
            ParseDouble(replayViewportYText, 0d), ref y);
        AddReplayPropertyNumber(composer, small, "Z", "replayViewportZ",
            ParseDouble(replayViewportZText, 0d), ref y);
        AddReplayPropertyNumber(composer, small, Lang.Get("vsdirector:camera-dimension"),
            "replayViewportDimension", ParseDouble(replayViewportDimensionText, 0d), ref y);
        AddReplayPropertyNumber(composer, small, "Yaw", "replayViewportYaw",
            ParseDouble(replayViewportYawText, 0d), ref y);
        AddReplayPropertyNumber(composer, small, "Pitch", "replayViewportPitch",
            ParseDouble(replayViewportPitchText, 0d), ref y);
        AddReplayPropertyNumber(composer, small, Lang.Get("vsdirector:roll"),
            "replayViewportRoll", ParseDouble(replayViewportRollText, 0d), ref y);

        composer
            .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:apply"),
                ApplyReplayViewport, ElementBounds.Fixed(10d, y, 30d, 28d),
                EnumButtonStyle.Normal, "applyReplayViewport")
            .AddDirectorIconButton(DirectorIcon.Capture,
                Lang.Get("vsdirector:replay-camera-recapture"),
                RecaptureSelectedReplayCameraKeyframe,
                ElementBounds.Fixed(46d, y, 30d, 28d),
                EnumButtonStyle.Small, "recaptureReplayViewport")
            .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete"),
                DeleteReplayViewport, ElementBounds.Fixed(82d, y, 30d, 28d),
                EnumButtonStyle.Small, "deleteReplayViewport");
    }

    private void ComposeReplayElementKeyframeInspector(
        GuiComposer composer,
        CairoFont small,
        DirectorReplayElementKeyframe keyframe,
        ref int y)
    {
        SyncReplayElementFields(keyframe);
        DirectorReplayElementTrack? keyframeTrack = CurrentReplayViewportTimeline().Tracks
            .FirstOrDefault(track => track.Id == keyframe.TrackId);
        string? keyframeTrackName = keyframeTrack?.Name;
        AddReplayPropertyHeader(composer, small, string.IsNullOrWhiteSpace(keyframeTrackName)
            ? ReplayTimelineElementTrackName(ReplayTimelineElementKindFor(keyframe.TrackType))
            : keyframeTrackName, ref y);
        if (keyframe.TrackType != DirectorReplayElementTrackType.Timelapse)
        {
            AddReplayPropertyNumber(composer, small, Lang.Get("vsdirector:replay-element-time-seconds"),
                "replayElementTime", ReplayElementNumber("time", keyframe.TimeSeconds), ref y);
        }

        if (keyframe.TrackType is not (DirectorReplayElementTrackType.Freeze
            or DirectorReplayElementTrackType.BlockOverride
            or DirectorReplayElementTrackType.VoiceAudio
            or DirectorReplayElementTrackType.Timelapse
            or DirectorReplayElementTrackType.FollowEntity))
        {
            string[] codes = { "smooth", "linear", "ease-in", "ease-out", "ease-in-out", "hold", "hermite" };
            string[] names =
            {
                Lang.Get("vsdirector:replay-element-interpolation-smooth"),
                Lang.Get("vsdirector:replay-speed-interpolation-linear"),
                Lang.Get("vsdirector:replay-element-interpolation-ease-in"),
                Lang.Get("vsdirector:replay-element-interpolation-ease-out"),
                Lang.Get("vsdirector:replay-element-interpolation-ease-in-out"),
                Lang.Get("vsdirector:replay-speed-interpolation-hold"),
                Lang.Get("vsdirector:replay-element-interpolation-hermite")
            };
            composer
                .AddDirectorSingleLineText(Lang.Get("vsdirector:replay-element-interpolation"), small,
                    ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d))
                .AddDropDown(codes, names,
                    Math.Max(0, Array.IndexOf(codes, ReplayElementField("interpolation", "smooth"))),
                    OnReplayElementInterpolationChanged,
                    ElementBounds.Fixed(ReplayPropertyControlX, y,
                        ReplayPropertyDropDownWidth, 28d),
                    "replayElementInterpolation");
            y += 34;
        }

        switch (keyframe.TrackType)
        {
            case DirectorReplayElementTrackType.CameraOrbit:
                AddReplayElementNumber(composer, small, "X", "x", ref y);
                AddReplayElementNumber(composer, small, "Y", "y", ref y);
                AddReplayElementNumber(composer, small, "Z", "z", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:camera-dimension"),
                    "dimension", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-distance"),
                    "distance", ref y);
                AddReplayElementNumber(composer, small, "Yaw", "yaw", ref y);
                AddReplayElementNumber(composer, small, "Pitch", "pitch", ref y);
                break;
            case DirectorReplayElementTrackType.TrackEntity:
                AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:replay-element-actor-key"),
                    "replayElementEntityActorKey", ReplayElementField("entityActorKey"), ref y);
                AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:replay-element-entity-id"),
                    "replayElementEntityId", ReplayElementField("entityId", "0"), ref y);
                string[] partCodes = { "head", "body", "root" };
                string[] partNames =
                {
                    Lang.Get("vsdirector:replay-element-body-head"),
                    Lang.Get("vsdirector:replay-element-body-center"),
                    Lang.Get("vsdirector:replay-element-body-root")
                };
                composer
                    .AddDirectorSingleLineText(Lang.Get("vsdirector:replay-element-body-part"), small,
                        ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d))
                    .AddDropDown(partCodes, partNames,
                        Math.Max(0, Array.IndexOf(partCodes, ReplayElementField("bodyPart", "head"))),
                        OnReplayElementBodyPartChanged,
                        ElementBounds.Fixed(ReplayPropertyControlX, y,
                            ReplayPropertyDropDownWidth, 28d),
                        "replayElementBodyPart");
                y += 34;
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-offset-x"),
                    "offsetX", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-offset-y"),
                    "offsetY", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-offset-z"),
                    "offsetZ", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-view-x"),
                    "viewOffsetX", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-view-y"),
                    "viewOffsetY", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-view-z"),
                    "viewOffsetZ", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-yaw-offset"),
                    "yaw", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-pitch-offset"),
                    "pitch", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:roll"), "roll", ref y);
                AddReplayElementSwitch(composer, small, Lang.Get("vsdirector:follow-clear-view"),
                    "clearView", ref y, requiresClearView: true);
                break;
            case DirectorReplayElementTrackType.FollowEntity:
                AddReplayFollowEntityFields(composer, small, keyframe, ref y);
                break;
            case DirectorReplayElementTrackType.CameraShake:
                AddReplayElementSwitch(composer, small, Lang.Get("vsdirector:replay-element-split-axes"),
                    "split", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-frequency-x"),
                    "frequencyX", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-amplitude-x"),
                    "amplitudeX", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-frequency-y"),
                    "frequencyY", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-amplitude-y"),
                    "amplitudeY", ref y);
                break;
            case DirectorReplayElementTrackType.Fov:
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:fov"), "fov", ref y);
                break;
            case DirectorReplayElementTrackType.Speed:
                AddReplayElementNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-speed"), "speed", ref y);
                break;
            case DirectorReplayElementTrackType.Timelapse:
                AddReplayElementNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-duration-seconds"),
                    "timelapseDuration", ref y);
                break;
            case DirectorReplayElementTrackType.TimeOfDay:
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-time-tick"),
                    "timeOfDay", ref y);
                break;
            case DirectorReplayElementTrackType.Freeze:
                AddReplayElementSwitch(composer, small, Lang.Get("vsdirector:replay-element-frozen"),
                    "frozen", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-freeze-delay"),
                    "freezeDelay", ref y);
                break;
            case DirectorReplayElementTrackType.BlockOverride:
                AddReplayElementNumber(composer, small, "X", "blockX", ref y);
                AddReplayElementNumber(composer, small, "Y", "blockY", ref y);
                AddReplayElementNumber(composer, small, "Z", "blockZ", ref y);
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:camera-dimension"),
                    "dimension", ref y);
                AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:replay-element-block-code"),
                    "replayElementBlockCode", ReplayElementField("blockCode"), ref y);
                break;
            case DirectorReplayElementTrackType.VoiceAudio:
                AddReplayElementNumber(composer, small, Lang.Get("vsdirector:replay-element-volume"),
                    "volume", ref y);
                break;
        }

        composer
            .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:apply"),
                ApplySelectedReplayElementKeyframe,
                ElementBounds.Fixed(10d, y, 30d, 28d),
                EnumButtonStyle.Normal, "applyReplayElementKeyframe")
            .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete"),
                DeleteSelectedReplayElementKeyframe,
                ElementBounds.Fixed(46d, y, 30d, 28d),
                EnumButtonStyle.Small, "deleteReplayElementKeyframe");
    }

    private void AddReplayElementNumber(
        GuiComposer composer,
        CairoFont small,
        string label,
        string field,
        ref int y)
        => AddReplayPropertyNumber(composer, small, label, "replayElement" + UpperFirst(field),
            ReplayElementNumber(field), ref y);

    private static void AddReplayPropertyHeader(
        GuiComposer composer,
        CairoFont font,
        string title,
        ref int y)
    {
        composer.AddStaticText(
            Ellipsize(title, 40),
            font,
            ElementBounds.Fixed(10d, y, ReplayPropertyPopupWidth - 20d, 26d));
        y += 30;
    }

    private static void AddReplayPropertyInput(
        GuiComposer composer,
        CairoFont font,
        string label,
        string key,
        string value,
        ref int y)
    {
        _ = value;
        composer
            .AddDirectorSingleLineText(Ellipsize(label, 24), font,
                ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d))
            .AddTextInput(
                ElementBounds.Fixed(ReplayPropertyControlX, y,
                    ReplayPropertyTextWidth, 28d),
                null,
                DirectorFont.TextInput(),
                key);
        y += 34;
    }

    private void AddReplayPropertyNumber(
        GuiComposer composer,
        CairoFont font,
        string label,
        string key,
        double value,
        ref int y)
    {
        ReplayPropertyRange? range = ReplayPropertyRangeFor(key);
        composer.AddDirectorSingleLineText(Ellipsize(label, 24), font,
            ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d));
        if (range?.TimeInput == true)
        {
            composer.AddTextInput(
                ElementBounds.Fixed(ReplayPropertyControlX, y,
                    ReplayPropertyNumberWidth, 28d),
                text =>
                {
                    if (DirectorTimeText.TryParseTicks(text, out int ticks))
                    {
                        replayElementFieldValues["timeOfDay"] = ticks.ToString(
                            CultureInfo.InvariantCulture);
                    }
                },
                DirectorFont.TextInput(),
                key,
                () => RestoreReplayPropertyValue(composer, key, ReplayElementNumber("timeOfDay")));
        }
        else if (range is not null)
        {
            composer.AddDirectorSlider(
                next =>
                {
                    SetReplayElementSliderValue(key, range, next);
                    return true;
                },
                ElementBounds.Fixed(ReplayPropertyControlX, y,
                    ReplayPropertyNumberWidth, 28d),
                key);
            GuiElementDirectorSlider? slider = composer.GetDirectorSlider(key);
            slider?.SetValues(range.ToSlider(value), range.Minimum, range.Maximum, range.Step);
            if (slider is not null)
            {
                slider.OnSliderTooltip = next => range.FromSlider(next)
                    .ToString(range.Format, CultureInfo.InvariantCulture);
            }
        }
        else
        {
            composer.AddNumberInput(
                ElementBounds.Fixed(ReplayPropertyControlX, y,
                    ReplayPropertyNumberWidth, 28d),
                null,
                DirectorFont.TextInput(),
                key);
        }
        y += 34;
    }

    private void AddReplayElementSwitch(
        GuiComposer composer,
        CairoFont small,
        string label,
        string field,
        ref int y,
        bool requiresClearView = false)
    {
        composer
            .AddSwitch(value => SetReplayElementSwitch(field, value),
                ElementBounds.Fixed(10d, y + 2d, 25d, 25d),
                "replayElement" + UpperFirst(field), 25d, 3d)
            .AddDirectorSingleLineText(label, small,
                ElementBounds.Fixed(41d, y + 5d,
                    ReplayPropertyPopupWidth - 57d, 22d));
        if (requiresClearView && composer.GetSwitch("replayElement" + UpperFirst(field)) is { } clearViewSwitch)
        {
            clearViewSwitch.Enabled = owner.HasClearView;
        }
        y += 34;
    }

    private static string UpperFirst(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private bool SetReplayElementSwitch(string field, bool value)
    {
        if (value && field is "clearView" or "followClearView" && !owner.HasClearView)
        {
            replayElementFieldValues[field] = "false";
            owner.NotifyClearViewUnavailable();
            return true;
        }
        replayElementFieldValues[field] = value ? "true" : "false";
        return true;
    }

    private void OnReplayElementInterpolationChanged(string value, bool selected)
    {
        if (selected && value is "smooth" or "linear" or "ease-in" or "ease-out"
            or "ease-in-out" or "hold" or "hermite")
        {
            replayElementFieldValues["interpolation"] = value;
        }
    }

    private void OnReplayElementBodyPartChanged(string value, bool selected)
    {
        if (selected && value is "head" or "body" or "root")
        {
            replayElementFieldValues["bodyPart"] = value;
        }
    }

    private void AddReplayFollowEntityFields(
        GuiComposer composer,
        CairoFont small,
        DirectorReplayElementKeyframe keyframe,
        ref int y)
    {
        AddReplayElementDropDown(composer, small,
            Lang.Get("vsdirector:replay-follow-role"),
            "followRole",
            new[] { "start", "end" },
            new[]
            {
                Lang.Get("vsdirector:replay-follow-start"),
                Lang.Get("vsdirector:replay-follow-end")
            },
            ReplayElementField("followRole", keyframe.FollowEntityRole
                == DirectorReplayFollowEntityKeyframeRole.End ? "end" : "start"), ref y);
        if (keyframe.FollowEntityRole == DirectorReplayFollowEntityKeyframeRole.End)
        {
            return;
        }

        AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:replay-element-actor-key"),
            "replayElementEntityActorKey", ReplayElementField("entityActorKey"), ref y);
        AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:replay-element-entity-id"),
            "replayElementEntityId", ReplayElementField("entityId", "0"), ref y);
        AddReplayElementDropDown(composer, small,
            Lang.Get("vsdirector:follow-view-mode"),
            "followView",
            new[] { "first", "third" },
            new[]
            {
                Lang.Get("vsdirector:follow-view-first"),
                Lang.Get("vsdirector:follow-view-third")
            },
            ReplayElementField("followView", "third"), ref y);
        AddReplayElementDropDown(composer, small,
            Lang.Get("vsdirector:follow-motion-mode"),
            "followMotion",
            new[] { "immediate", "smooth", "delayed" },
            new[]
            {
                Lang.Get("vsdirector:follow-motion-immediate"),
                Lang.Get("vsdirector:follow-motion-smooth"),
                Lang.Get("vsdirector:follow-motion-delayed")
            },
            ReplayElementField("followMotion", "smooth"), ref y);
        AddReplayElementDropDown(composer, small,
            Lang.Get("vsdirector:follow-anchor"),
            "followAnchor",
            new[] { "head", "body" },
            new[]
            {
                Lang.Get("vsdirector:follow-anchor-head"),
                Lang.Get("vsdirector:follow-anchor-body")
            },
            ReplayElementField("followAnchor", "head"), ref y);
        AddReplayElementSwitch(composer, small, Lang.Get("vsdirector:follow-clear-view"),
            "followClearView", ref y, requiresClearView: true);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-offset-x"),
            "followOffsetX", ref y);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-offset-y"),
            "followOffsetY", ref y);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-offset-z"),
            "followOffsetZ", ref y);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-distance"),
            "followDistance", ref y);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-height"),
            "followHeight", ref y);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-orbit-angle"),
            "followOrbitAngle", ref y);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-orbit-speed"),
            "followOrbitSpeed", ref y);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-smooth-time"),
            "followSmooth", ref y);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-delay"),
            "followDelay", ref y);
        AddReplayElementNumber(composer, small, Lang.Get("vsdirector:follow-fov"),
            "followFov", ref y);

    }

    private void AddReplayElementDropDown(
        GuiComposer composer,
        CairoFont small,
        string label,
        string field,
        string[] codes,
        string[] names,
        string selected,
        ref int y)
    {
        composer
            .AddDirectorSingleLineText(label, small,
                ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d))
            .AddDropDown(
                codes,
                names,
                Math.Max(0, Array.IndexOf(codes, selected)),
                (value, isSelected) =>
                {
                    if (isSelected && codes.Contains(value))
                    {
                        replayElementFieldValues[field] = value;
                    }
                },
                ElementBounds.Fixed(ReplayPropertyControlX, y,
                    ReplayPropertyDropDownWidth, 28d),
                "replayElement" + UpperFirst(field));
        y += 34;
    }

    private DirectorReplayElementKeyframe? SelectedReplayElementKeyframe()
        => selectedReplayElementKeyframeId == Guid.Empty
            ? null
            : CurrentReplayViewportTimeline().ElementKeyframes.FirstOrDefault(keyframe =>
                keyframe.Id == selectedReplayElementKeyframeId
                && (selectedReplayElementTrackId == Guid.Empty
                    || keyframe.TrackId == selectedReplayElementTrackId));

    private void SyncReplayElementFields(DirectorReplayElementKeyframe keyframe)
    {
        if (replayElementFieldsId == keyframe.Id)
        {
            return;
        }

        replayElementFieldsId = keyframe.Id;
        replayElementFieldValues.Clear();
        void Number(string key, double value)
            => replayElementFieldValues[key] = value.ToString("0.###", CultureInfo.InvariantCulture);
        Number("time", keyframe.TimeSeconds);
        replayElementFieldValues["interpolation"] = keyframe.InterpolationToNext switch
        {
            DirectorReplayElementInterpolation.Hold => "hold",
            DirectorReplayElementInterpolation.Smooth => "smooth",
            DirectorReplayElementInterpolation.EaseIn => "ease-in",
            DirectorReplayElementInterpolation.EaseOut => "ease-out",
            DirectorReplayElementInterpolation.EaseInOut => "ease-in-out",
            DirectorReplayElementInterpolation.Hermite => "hermite",
            _ => "linear"
        };
        (double orbitDisplayX, double orbitDisplayY, double orbitDisplayZ) = keyframe.TrackType
            == DirectorReplayElementTrackType.CameraOrbit
            ? owner.DisplayPosition(keyframe.X, keyframe.Y, keyframe.Z)
            : (keyframe.X, keyframe.Y, keyframe.Z);
        Number("x", orbitDisplayX);
        Number("y", orbitDisplayY);
        Number("z", orbitDisplayZ);
        Number("dimension", keyframe.Dimension);
        Number("distance", keyframe.Distance);
        Number("yaw", keyframe.Yaw);
        Number("pitch", keyframe.Pitch);
        Number("roll", keyframe.Roll);
        Number("fov", keyframe.Fov);
        Number("speed", keyframe.TickRate / DirectorReplayElementTimeline.TicksPerSecond);
        replayElementFieldValues["entityId"] = keyframe.EntityId.ToString(CultureInfo.InvariantCulture);
        replayElementFieldValues["entityActorKey"] = keyframe.EntityActorKey;
        replayElementFieldValues["bodyPart"] = keyframe.BodyPart;
        replayElementFieldValues["clearView"] = keyframe.OpenClearView ? "true" : "false";
        replayElementFieldValues["followRole"] = keyframe.FollowEntityRole
            == DirectorReplayFollowEntityKeyframeRole.End ? "end" : "start";
        replayElementFieldValues["followView"] = keyframe.FollowViewMode
            == CameraFollowViewMode.FirstPerson ? "first" : "third";
        replayElementFieldValues["followMotion"] = keyframe.FollowMotionMode switch
        {
            CameraFollowMotionMode.Immediate => "immediate",
            CameraFollowMotionMode.Delayed => "delayed",
            _ => "smooth"
        };
        replayElementFieldValues["followAnchor"] = keyframe.FollowAnchor
            == CameraFollowAnchor.Body ? "body" : "head";
        replayElementFieldValues["followClearView"] = keyframe.FollowOpenClearView ? "true" : "false";
        Number("followOffsetX", keyframe.FollowOffsetX);
        Number("followOffsetY", keyframe.FollowOffsetY);
        Number("followOffsetZ", keyframe.FollowOffsetZ);
        Number("followDistance", keyframe.FollowDistance);
        Number("followHeight", keyframe.FollowHeight);
        Number("followOrbitAngle", keyframe.FollowOrbitAngleDegrees);
        Number("followOrbitSpeed", keyframe.FollowOrbitSpeedDegreesPerSecond);
        Number("followSmooth", keyframe.FollowSmoothTimeSeconds);
        Number("followDelay", keyframe.FollowDelaySeconds);
        Number("followFov", keyframe.FollowFov);
        Number("offsetX", keyframe.OffsetX);
        Number("offsetY", keyframe.OffsetY);
        Number("offsetZ", keyframe.OffsetZ);
        Number("viewOffsetX", keyframe.ViewOffsetX);
        Number("viewOffsetY", keyframe.ViewOffsetY);
        Number("viewOffsetZ", keyframe.ViewOffsetZ);
        Number("frequencyX", keyframe.FrequencyX);
        Number("amplitudeX", keyframe.AmplitudeX);
        Number("frequencyY", keyframe.FrequencyY);
        Number("amplitudeY", keyframe.AmplitudeY);
        replayElementFieldValues["split"] = keyframe.SplitParameters ? "true" : "false";
        Number("timelapseTick", keyframe.TimelapseTick);
        DirectorReplayElementTrack? selectedTrack = CurrentReplayViewportTimeline().Tracks
            .FirstOrDefault(track => track.Id == keyframe.TrackId
                && track.TrackType == DirectorReplayElementTrackType.Timelapse);
        double timelapseDuration = keyframe.TimelapseDurationSeconds;
        if (timelapseDuration <= 0d)
        {
            timelapseDuration = selectedTrack?.TimelapseDurationSeconds ?? 0d;
        }
        Number("timelapseDuration", timelapseDuration > 0d
            ? timelapseDuration
            : DefaultTimelapseDurationSeconds);
        Number("timeOfDay", keyframe.TimeOfDayTick);
        replayElementFieldValues["frozen"] = keyframe.Frozen ? "true" : "false";
        Number("freezeDelay", keyframe.FreezeDelayTicks);
        (double blockDisplayX, double blockDisplayY, double blockDisplayZ) = keyframe.TrackType
            == DirectorReplayElementTrackType.BlockOverride
            ? owner.DisplayPosition(keyframe.BlockX, keyframe.BlockY, keyframe.BlockZ)
            : (keyframe.BlockX, keyframe.BlockY, keyframe.BlockZ);
        Number("blockX", blockDisplayX);
        Number("blockY", blockDisplayY);
        Number("blockZ", blockDisplayZ);
        replayElementFieldValues["blockCode"] = keyframe.BlockCode;
        replayElementFieldValues["audioAsset"] = keyframe.AudioAsset;
        Number("volume", keyframe.TrackType == DirectorReplayElementTrackType.VoiceAudio
            ? keyframe.Volume * 100f
            : keyframe.Volume);
        Number("audioPitch", keyframe.AudioPitch);
    }

    private string ReplayElementField(string field, string fallback = "")
        => replayElementFieldValues.TryGetValue(field, out string? value) ? value : fallback;

    private double ReplayElementNumber(string field, double fallback = 0d)
        => ParseDouble(ReplayElementField(field), fallback);

    private bool ApplySelectedReplayElementKeyframe()
    {
        PreserveInputs();
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayElementKeyframe? keyframe = timeline.ElementKeyframes.FirstOrDefault(item =>
            item.Id == selectedReplayElementKeyframeId);
        if (keyframe is null)
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-element-invalid"));
            return true;
        }

        if (keyframe.TrackType != DirectorReplayElementTrackType.Timelapse)
        {
            if (!TryReplayElementDouble("time", out double time))
            {
                capi.ShowChatMessage(Lang.Get("vsdirector:replay-element-invalid"));
                return true;
            }
            keyframe.TimeSeconds = ReplayTimelineTickAt(time) / ReplayTimelineTicksPerSecond;
        }
        keyframe.InterpolationToNext = ReplayElementField("interpolation", "smooth") switch
        {
            "hold" => DirectorReplayElementInterpolation.Hold,
            "smooth" => DirectorReplayElementInterpolation.Smooth,
            "ease-in" => DirectorReplayElementInterpolation.EaseIn,
            "ease-out" => DirectorReplayElementInterpolation.EaseOut,
            "ease-in-out" or "ease" => DirectorReplayElementInterpolation.EaseInOut,
            "hermite" => DirectorReplayElementInterpolation.Hermite,
            _ => DirectorReplayElementInterpolation.Linear
        };
        bool valid = keyframe.TrackType switch
        {
            DirectorReplayElementTrackType.CameraOrbit => TryApplyReplayOrbitFields(keyframe),
            DirectorReplayElementTrackType.TrackEntity => TryApplyReplayTrackingFields(keyframe),
            DirectorReplayElementTrackType.FollowEntity => TryApplyReplayFollowEntityFields(keyframe),
            DirectorReplayElementTrackType.CameraShake => TryApplyReplayShakeFields(keyframe),
            DirectorReplayElementTrackType.Fov => TryApplyReplayFovFields(keyframe),
            DirectorReplayElementTrackType.Speed => TryApplyReplaySpeedFields(keyframe),
            DirectorReplayElementTrackType.Timelapse => TryApplyReplayTimelapseFields(
                timeline,
                keyframe),
            DirectorReplayElementTrackType.TimeOfDay => TryApplyReplayTimeOfDayFields(keyframe),
            DirectorReplayElementTrackType.Freeze => TryApplyReplayFreezeFields(keyframe),
            DirectorReplayElementTrackType.BlockOverride => TryApplyReplayBlockFields(keyframe),
            DirectorReplayElementTrackType.VoiceAudio => TryApplyReplayVoiceAudioFields(keyframe),
            _ => false
        };
        if (!valid)
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-element-invalid"));
            return true;
        }

        replayElementFieldsId = Guid.Empty;
        if (CommitReplayViewportTimeline(timeline))
        {
            CloseReplayFloatingEditor();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool TryApplyReplayOrbitFields(DirectorReplayElementKeyframe keyframe)
    {
        if (!TryReplayElementDouble("x", out double x)
            || !TryReplayElementDouble("y", out double y)
            || !TryReplayElementDouble("z", out double z)
            || !int.TryParse(ReplayElementField("dimension"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int dimension)
            || !TryReplayElementDouble("distance", out double distance)
            || !TryReplayElementFloat("yaw", out float yaw)
            || !TryReplayElementFloat("pitch", out float pitch)
            || distance <= 0d)
        {
            return false;
        }
        (keyframe.X, keyframe.Y, keyframe.Z) = owner.AbsolutePosition(x, y, z);
        keyframe.Dimension = dimension;
        keyframe.Distance = distance;
        keyframe.Yaw = yaw;
        keyframe.Pitch = pitch;
        keyframe.Roll = 0f;
        return true;
    }

    private bool TryApplyReplayTrackingFields(DirectorReplayElementKeyframe keyframe)
    {
        if (!long.TryParse(ReplayElementField("entityId"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long entityId)
            || !TryReplayElementDouble("offsetX", out double offsetX)
            || !TryReplayElementDouble("offsetY", out double offsetY)
            || !TryReplayElementDouble("offsetZ", out double offsetZ)
            || !TryReplayElementDouble("viewOffsetX", out double viewOffsetX)
            || !TryReplayElementDouble("viewOffsetY", out double viewOffsetY)
            || !TryReplayElementDouble("viewOffsetZ", out double viewOffsetZ)
            || !TryReplayElementFloat("yaw", out float yaw)
            || !TryReplayElementFloat("pitch", out float pitch)
            || !TryReplayElementFloat("roll", out float roll))
        {
            return false;
        }
        string bodyPart = ReplayElementField("bodyPart", "head").Trim().ToLowerInvariant();
        if (bodyPart is not ("head" or "body" or "root"))
        {
            return false;
        }
        keyframe.EntityId = entityId;
        keyframe.EntityActorKey = ReplayElementField("entityActorKey").Trim();
        keyframe.BodyPart = bodyPart;
        keyframe.OffsetX = offsetX;
        keyframe.OffsetY = offsetY;
        keyframe.OffsetZ = offsetZ;
        keyframe.ViewOffsetX = viewOffsetX;
        keyframe.ViewOffsetY = viewOffsetY;
        keyframe.ViewOffsetZ = viewOffsetZ;
        keyframe.Yaw = yaw;
        keyframe.Pitch = pitch;
        keyframe.Roll = roll;
        keyframe.OpenClearView = string.Equals(
            ReplayElementField("clearView"), "true", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private bool TryApplyReplayFollowEntityFields(DirectorReplayElementKeyframe keyframe)
    {
        keyframe.InterpolationToNext = DirectorReplayElementInterpolation.Hold;
        keyframe.FollowEntityRole = ReplayElementField("followRole", "start") == "end"
            ? DirectorReplayFollowEntityKeyframeRole.End
            : DirectorReplayFollowEntityKeyframeRole.Start;
        if (keyframe.FollowEntityRole == DirectorReplayFollowEntityKeyframeRole.End)
        {
            return true;
        }

        if (!long.TryParse(ReplayElementField("entityId"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long entityId)
            || !TryReplayElementDouble("followOffsetX", out double offsetX)
            || !TryReplayElementDouble("followOffsetY", out double offsetY)
            || !TryReplayElementDouble("followOffsetZ", out double offsetZ)
            || !TryReplayElementDouble("followDistance", out double distance)
            || !TryReplayElementDouble("followHeight", out double height)
            || !TryReplayElementDouble("followOrbitAngle", out double orbitAngle)
            || !TryReplayElementDouble("followOrbitSpeed", out double orbitSpeed)
            || !TryReplayElementDouble("followSmooth", out double smooth)
            || !TryReplayElementDouble("followDelay", out double delay)
            || !TryReplayElementFloat("followFov", out float fov)
            || Math.Abs(offsetX) > CameraFollowMath.MaxOffset
            || Math.Abs(offsetY) > CameraFollowMath.MaxOffset
            || Math.Abs(offsetZ) > CameraFollowMath.MaxOffset
            || distance < 0d || distance > CameraFollowMath.MaxDistance
            || Math.Abs(height) > CameraFollowMath.MaxHeight
            || Math.Abs(orbitSpeed) > CameraFollowMath.MaxOrbitSpeed
            || smooth < CameraFollowMath.MinSmoothTimeSeconds
            || smooth > CameraFollowMath.MaxSmoothTimeSeconds
            || delay < 0d || delay > CameraFollowMath.MaxDelaySeconds
            || fov < 10f || fov > 170f)
        {
            return false;
        }

        keyframe.EntityId = entityId;
        keyframe.EntityActorKey = ReplayElementField("entityActorKey").Trim();
        keyframe.FollowViewMode = ReplayElementField("followView", "third") == "first"
            ? CameraFollowViewMode.FirstPerson
            : CameraFollowViewMode.ThirdPerson;
        keyframe.FollowMotionMode = ReplayElementField("followMotion", "smooth") switch
        {
            "immediate" => CameraFollowMotionMode.Immediate,
            "delayed" => CameraFollowMotionMode.Delayed,
            _ => CameraFollowMotionMode.Smooth
        };
        keyframe.FollowAnchor = ReplayElementField("followAnchor", "head") == "body"
            ? CameraFollowAnchor.Body
            : CameraFollowAnchor.Head;
        keyframe.FollowOpenClearView = ReplayElementField("followClearView") == "true";
        keyframe.FollowOffsetX = offsetX;
        keyframe.FollowOffsetY = offsetY;
        keyframe.FollowOffsetZ = offsetZ;
        keyframe.FollowDistance = distance;
        keyframe.FollowHeight = height;
        keyframe.FollowOrbitAngleDegrees = orbitAngle;
        keyframe.FollowOrbitSpeedDegreesPerSecond = orbitSpeed;
        keyframe.FollowSmoothTimeSeconds = smooth;
        keyframe.FollowDelaySeconds = delay;
        keyframe.FollowFov = fov;
        return true;
    }

    private bool TryApplyReplayShakeFields(DirectorReplayElementKeyframe keyframe)
    {
        if (!TryReplayElementFloat("frequencyX", out float frequencyX)
            || !TryReplayElementFloat("amplitudeX", out float amplitudeX)
            || !TryReplayElementFloat("frequencyY", out float frequencyY)
            || !TryReplayElementFloat("amplitudeY", out float amplitudeY)
            || frequencyX is < 0.1f or > 10f
            || frequencyY is < 0.1f or > 10f
            || amplitudeX is < 0f or > 10f
            || amplitudeY is < 0f or > 10f)
        {
            return false;
        }
        keyframe.FrequencyX = frequencyX;
        keyframe.AmplitudeX = amplitudeX;
        keyframe.FrequencyY = frequencyY;
        keyframe.AmplitudeY = amplitudeY;
        keyframe.SplitParameters = ReplayElementField("split") == "true";
        if (!keyframe.SplitParameters)
        {
            keyframe.FrequencyY = keyframe.FrequencyX;
            keyframe.AmplitudeY = keyframe.AmplitudeX;
        }
        return true;
    }

    private bool TryApplyReplayFovFields(DirectorReplayElementKeyframe keyframe)
    {
        if (!TryReplayElementFloat("fov", out float fov)
            || fov < DirectorReplayElementTimeline.MinFovDegrees
            || fov > DirectorReplayElementTimeline.MaxFovDegrees)
        {
            return false;
        }
        keyframe.Fov = fov;
        return true;
    }

    private bool TryApplyReplaySpeedFields(DirectorReplayElementKeyframe keyframe)
    {
        if (!TryReplayElementFloat("speed", out float speed) || speed is < 0.1f or > 10f)
        {
            return false;
        }
        keyframe.TickRate = (float)(speed * DirectorReplayElementTimeline.TicksPerSecond);
        return true;
    }

    private bool TryApplyReplayTimelapseFields(
        DirectorReplayViewportTimeline timeline,
        DirectorReplayElementKeyframe keyframe)
    {
        if (DirectorReplayTimelapse.IsEnd(keyframe))
        {
            return false;
        }
        if (!TryReplayElementDouble("timelapseDuration", out double duration)
            || !double.IsFinite(duration)
            || duration <= 0d
            || duration > MaxTimelapseDurationSeconds)
        {
            return false;
        }

        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item =>
            item.Id == keyframe.TrackId
            && item.TrackType == DirectorReplayElementTrackType.Timelapse);
        if (track is null)
        {
            return false;
        }

        if (!DirectorReplayTimelapse.TryFindPair(
                timeline.ElementKeyframes,
                track.Id,
                keyframe.Id,
                out DirectorReplayTimelapsePair pair))
        {
            return false;
        }

        duration = Math.Min(duration, MaxTimelapseDurationSeconds);

        pair.Start.TimelapseDurationSeconds = duration;
        pair.End.TimelapseDurationSeconds = duration;
        // Keep the track-level value in sync for older sidecars and as the
        // default for newly added segments. Each pair still owns its value.
        track.TimelapseDurationSeconds = duration;
        return true;
    }

    private bool TryApplyReplayTimeOfDayFields(DirectorReplayElementKeyframe keyframe)
    {
        if (!TryReplayElementInt("timeOfDay", out int tick) || tick is < 0 or > 24000)
        {
            return false;
        }
        keyframe.TimeOfDayTick = tick;
        keyframe.TimeOfDayHours = null;
        return true;
    }

    private bool TryApplyReplayFreezeFields(DirectorReplayElementKeyframe keyframe)
    {
        if (!TryReplayElementInt("freezeDelay", out int delay)
            || delay < 0
            || delay > DirectorReplayElementTimeline.MaxFreezeDelayTicks)
        {
            return false;
        }
        keyframe.Frozen = ReplayElementField("frozen") == "true";
        keyframe.FreezeDelayTicks = delay;
        keyframe.InterpolationToNext = DirectorReplayElementInterpolation.Hold;
        return true;
    }

    private bool TryApplyReplayBlockFields(DirectorReplayElementKeyframe keyframe)
    {
        if (!TryReplayElementInt("blockX", out int x)
            || !TryReplayElementInt("blockY", out int y)
            || !TryReplayElementInt("blockZ", out int z)
            || !TryReplayElementInt("dimension", out int dimension)
            || string.IsNullOrWhiteSpace(ReplayElementField("blockCode")))
        {
            return false;
        }
        (double absoluteX, double absoluteY, double absoluteZ) = owner.AbsolutePosition(x, y, z);
        keyframe.BlockX = (int)Math.Round(absoluteX, MidpointRounding.AwayFromZero);
        keyframe.BlockY = (int)Math.Round(absoluteY, MidpointRounding.AwayFromZero);
        keyframe.BlockZ = (int)Math.Round(absoluteZ, MidpointRounding.AwayFromZero);
        keyframe.Dimension = dimension;
        keyframe.BlockCode = ReplayElementField("blockCode").Trim();
        keyframe.InterpolationToNext = DirectorReplayElementInterpolation.Hold;
        return true;
    }

    private bool TryApplyReplayVoiceAudioFields(DirectorReplayElementKeyframe keyframe)
    {
        if (!TryReplayElementFloat("volume", out float volumePercent)
            || volumePercent is < 0f or > 200f)
        {
            return false;
        }
        keyframe.Volume = volumePercent / 100f;
        keyframe.InterpolationToNext = DirectorReplayElementInterpolation.Hold;
        return true;
    }

    private bool TryReplayElementDouble(string field, out double value)
        => double.TryParse(ReplayElementField(field), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);

    private bool TryReplayElementFloat(string field, out float value)
        => float.TryParse(ReplayElementField(field), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value)
            && float.IsFinite(value);

    private bool TryReplayElementInt(string field, out int value)
        => int.TryParse(ReplayElementField(field), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value);

    private bool DeleteSelectedReplayElementKeyframe()
    {
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayElementKeyframe? selected = timeline.ElementKeyframes.FirstOrDefault(keyframe =>
            keyframe.Id == selectedReplayElementKeyframeId);
        if (selected is null)
        {
            return true;
        }
        HashSet<Guid> ids = new() { selected.Id };
        if (selected.TrackType == DirectorReplayElementTrackType.Timelapse
            && DirectorReplayTimelapse.TryFindPair(
                timeline.ElementKeyframes,
                selected.TrackId,
                selected.Id,
                out DirectorReplayTimelapsePair pair))
        {
            ids.Add(pair.Start.Id);
            ids.Add(pair.End.Id);
        }
        if (timeline.ElementKeyframes.RemoveAll(keyframe => ids.Contains(keyframe.Id)) == 0)
        {
            return true;
        }
        selectedReplayElementKeyframeId = Guid.Empty;
        replayElementFieldsId = Guid.Empty;
        if (CommitReplayViewportTimeline(timeline))
        {
            ComposeInspector();
            ComposeTimeline();
            CloseReplayFloatingEditor();
        }
        return true;
    }

    private void SyncReplayViewportFields(DirectorReplayViewportClip clip)
    {
        if (replayViewportFieldsId == clip.Id)
        {
            return;
        }

        replayViewportFieldsId = clip.Id;
        replayViewportNameText = clip.Name;
        replayViewportStartText = clip.TimelineStartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        CameraPreset? pose = ReplayCameraElementPose(clip);
        (double displayX, double displayY, double displayZ) = pose is null
            ? (0d, 0d, 0d)
            : owner.DisplayPosition(pose.X, pose.Y, pose.Z);
        replayViewportXText = DirectorNumberFormatting.Format(displayX);
        replayViewportYText = DirectorNumberFormatting.Format(displayY);
        replayViewportZText = DirectorNumberFormatting.Format(displayZ);
        replayViewportDimensionText = (pose?.Dimension ?? 0).ToString(CultureInfo.InvariantCulture);
        replayViewportYawText = (pose?.Yaw ?? 0f).ToString("0.###", CultureInfo.InvariantCulture);
        replayViewportPitchText = (pose?.Pitch ?? 0f).ToString("0.###", CultureInfo.InvariantCulture);
        replayViewportRollText = (pose?.Roll ?? 0f).ToString("0.###", CultureInfo.InvariantCulture);
        replayViewportInterpolationCode = (clip.CameraInterpolationToNext
                ?? CameraPathInterpolation.EaseInOut) switch
        {
            CameraPathInterpolation.Step => "step",
            CameraPathInterpolation.Linear => "linear",
            CameraPathInterpolation.Bezier => "bezier",
            _ => "ease"
        };
    }

    private DirectorReplayViewportClip? SelectedReplayViewportClip()
    {
        if (selectedReplayTimelineElementTrack is ReplayTimelineElementKind selectedKind
            && selectedKind != ReplayTimelineElementKind.Camera)
        {
            return null;
        }

        DirectorReplayViewportTimeline timeline = CurrentReplayViewportTimeline();
        DirectorReplayViewportClip? clip = timeline.Clips.FirstOrDefault(
            item => item.Id == selectedReplayViewportClipId
                && (selectedReplayElementTrackId == Guid.Empty
                    || item.TrackId == selectedReplayElementTrackId)
                && item.SourceType == DirectorReplayViewportSourceType.StaticCamera);
        if (clip is null && timeline.SelectedClipId != Guid.Empty)
        {
            clip = timeline.Clips.FirstOrDefault(item =>
                item.Id == timeline.SelectedClipId
                && item.SourceType == DirectorReplayViewportSourceType.StaticCamera);
        }
        clip ??= timeline.Clips.FirstOrDefault(item =>
            (selectedReplayElementTrackId == Guid.Empty
                || item.TrackId == selectedReplayElementTrackId)
            &&
            item.SourceType == DirectorReplayViewportSourceType.StaticCamera);
        selectedReplayViewportClipId = clip?.Id ?? Guid.Empty;
        if (clip is not null && selectedReplayTimelineElementTrack is null)
        {
            selectedReplayElementTrackId = clip.TrackId;
            selectedReplayTimelineElementTrack = ReplayTimelineElementKind.Camera;
        }
        return clip;
    }

    private CameraPreset? ReplayCameraElementPose(DirectorReplayViewportClip clip)
        => clip.CameraPose?.DeepClone()
            ?? (clip.SourceType == DirectorReplayViewportSourceType.StaticCamera
                ? owner.FindCamera(clip.SourceId)?.DeepClone()
                : null);

    private DirectorReplayViewportTimeline CurrentReplayViewportTimeline()
        => replayViewportDraftTimeline?.DeepClone() ?? owner.ReplayViewportTimeline;

    private bool CommitReplayViewportTimeline(DirectorReplayViewportTimeline timeline)
    {
        timeline.SelectedClipId = selectedReplayViewportClipId;
        replayViewportDraftTimeline = null;
        if (!owner.TryReplaceReplayViewportTimeline(timeline, out _, notify: false))
        {
            return false;
        }

        DirectorReplayViewportClip? selected = owner.ReplayViewportClips.FirstOrDefault(
            clip => clip.Id == selectedReplayViewportClipId
                && clip.SourceType == DirectorReplayViewportSourceType.StaticCamera);
        if ((selectedReplayTimelineElementTrack is null or ReplayTimelineElementKind.Camera)
            && selected is null)
        {
            selectedReplayViewportClipId = owner.ReplayViewportClips.FirstOrDefault(clip =>
                (selectedReplayElementTrackId == Guid.Empty
                    || clip.TrackId == selectedReplayElementTrackId)
                &&
                clip.SourceType == DirectorReplayViewportSourceType.StaticCamera)?.Id ?? Guid.Empty;
        }
        else if (selectedReplayTimelineElementTrack != ReplayTimelineElementKind.Camera)
        {
            selectedReplayViewportClipId = Guid.Empty;
        }
        replayViewportFieldsId = Guid.Empty;
        return true;
    }

    private IReadOnlyList<ReplayViewportSourceOption> ReplayViewportSourceOptions()
        => new[]
        {
            new ReplayViewportSourceOption("element-camera",
                Lang.Get("vsdirector:replay-element-camera")),
            new ReplayViewportSourceOption("element-camera-orbit",
                Lang.Get("vsdirector:replay-element-camera-orbit")),
            new ReplayViewportSourceOption("element-track-entity",
                Lang.Get("vsdirector:replay-element-track-entity")),
            new ReplayViewportSourceOption("element-follow-entity",
                Lang.Get("vsdirector:replay-element-follow-entity")),
            new ReplayViewportSourceOption("element-camera-shake",
                Lang.Get("vsdirector:replay-element-camera-shake")),
            new ReplayViewportSourceOption("element-fov",
                Lang.Get("vsdirector:replay-element-fov")),
            new ReplayViewportSourceOption("element-speed",
                Lang.Get("vsdirector:replay-element-speed")),
            new ReplayViewportSourceOption("element-timelapse",
                Lang.Get("vsdirector:replay-element-timelapse")),
            new ReplayViewportSourceOption("element-time-of-day",
                Lang.Get("vsdirector:replay-element-time-of-day")),
            new ReplayViewportSourceOption("element-freeze",
                Lang.Get("vsdirector:replay-element-freeze"))
        };

    private IReadOnlyList<DirectorReplayElementTrack> ActiveReplayTimelineElementTracks()
        => CurrentReplayViewportTimeline().Tracks
            .Where(track => track.TrackType != DirectorReplayElementTrackType.Marker)
            .ToArray();

    private static string ReplayTimelineElementTrackName(ReplayTimelineElementKind kind)
        => Lang.Get(kind switch
        {
            ReplayTimelineElementKind.Camera => "vsdirector:replay-element-camera",
            ReplayTimelineElementKind.Marker => "vsdirector:replay-element-marker",
            ReplayTimelineElementKind.Speed => "vsdirector:replay-element-speed",
            ReplayTimelineElementKind.CameraOrbit => "vsdirector:replay-element-camera-orbit",
            ReplayTimelineElementKind.TrackEntity => "vsdirector:replay-element-track-entity",
            ReplayTimelineElementKind.FollowEntity => "vsdirector:replay-element-follow-entity",
            ReplayTimelineElementKind.CameraShake => "vsdirector:replay-element-camera-shake",
            ReplayTimelineElementKind.Fov => "vsdirector:replay-element-fov",
            ReplayTimelineElementKind.Timelapse => "vsdirector:replay-element-timelapse",
            ReplayTimelineElementKind.TimeOfDay => "vsdirector:replay-element-time-of-day",
            ReplayTimelineElementKind.Freeze => "vsdirector:replay-element-freeze",
            ReplayTimelineElementKind.BlockOverride => "vsdirector:replay-element-block-override",
            ReplayTimelineElementKind.VoiceAudio => "vsdirector:replay-element-voice-audio",
            _ => "vsdirector:replay-element-add"
        });

    private static ReplayTimelineElementKind ReplayTimelineElementKindFor(
        DirectorReplayElementTrackType kind)
        => kind switch
        {
            DirectorReplayElementTrackType.Camera => ReplayTimelineElementKind.Camera,
            DirectorReplayElementTrackType.Marker => ReplayTimelineElementKind.Marker,
            DirectorReplayElementTrackType.Speed => ReplayTimelineElementKind.Speed,
            DirectorReplayElementTrackType.CameraOrbit => ReplayTimelineElementKind.CameraOrbit,
            DirectorReplayElementTrackType.TrackEntity => ReplayTimelineElementKind.TrackEntity,
            DirectorReplayElementTrackType.FollowEntity => ReplayTimelineElementKind.FollowEntity,
            DirectorReplayElementTrackType.CameraShake => ReplayTimelineElementKind.CameraShake,
            DirectorReplayElementTrackType.Fov => ReplayTimelineElementKind.Fov,
            DirectorReplayElementTrackType.Timelapse => ReplayTimelineElementKind.Timelapse,
            DirectorReplayElementTrackType.TimeOfDay => ReplayTimelineElementKind.TimeOfDay,
            DirectorReplayElementTrackType.Freeze => ReplayTimelineElementKind.Freeze,
            DirectorReplayElementTrackType.BlockOverride => ReplayTimelineElementKind.BlockOverride,
            DirectorReplayElementTrackType.VoiceAudio => ReplayTimelineElementKind.VoiceAudio,
            _ => ReplayTimelineElementKind.Camera
        };

    private static DirectorReplayElementTrackType StoredReplayTimelineElementKind(
        ReplayTimelineElementKind kind)
        => kind switch
        {
            ReplayTimelineElementKind.Camera => DirectorReplayElementTrackType.Camera,
            ReplayTimelineElementKind.Marker => DirectorReplayElementTrackType.Marker,
            ReplayTimelineElementKind.Speed => DirectorReplayElementTrackType.Speed,
            ReplayTimelineElementKind.CameraOrbit => DirectorReplayElementTrackType.CameraOrbit,
            ReplayTimelineElementKind.TrackEntity => DirectorReplayElementTrackType.TrackEntity,
            ReplayTimelineElementKind.FollowEntity => DirectorReplayElementTrackType.FollowEntity,
            ReplayTimelineElementKind.CameraShake => DirectorReplayElementTrackType.CameraShake,
            ReplayTimelineElementKind.Fov => DirectorReplayElementTrackType.Fov,
            ReplayTimelineElementKind.Timelapse => DirectorReplayElementTrackType.Timelapse,
            ReplayTimelineElementKind.TimeOfDay => DirectorReplayElementTrackType.TimeOfDay,
            ReplayTimelineElementKind.Freeze => DirectorReplayElementTrackType.Freeze,
            ReplayTimelineElementKind.BlockOverride => DirectorReplayElementTrackType.BlockOverride,
            ReplayTimelineElementKind.VoiceAudio => DirectorReplayElementTrackType.VoiceAudio,
            _ => DirectorReplayElementTrackType.Camera
        };

    private bool IsReplayTimelineElementTrackSelected(DirectorReplayElementTrack track)
        => selectedReplayElementTrackId == track.Id;

    private bool SelectReplayTimelineElementTrack(Guid trackId)
    {
        DirectorReplayViewportTimeline timeline = CurrentReplayViewportTimeline();
        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item => item.Id == trackId);
        if (track is null)
        {
            return true;
        }
        ReplayTimelineElementKind kind = ReplayTimelineElementKindFor(track.TrackType);
        selectedReplayElementTrackId = track.Id;
        selectedReplayTimelineElementTrack = kind;
        selectedReplayViewportClipId = kind == ReplayTimelineElementKind.Camera
            ? timeline.Clips.FirstOrDefault(clip =>
                clip.TrackId == track.Id
                && clip.SourceType == DirectorReplayViewportSourceType.StaticCamera)?.Id ?? Guid.Empty
            : Guid.Empty;
        selectedReplayMarkerId = Guid.Empty;
        selectedReplaySpeedKeyframeId = Guid.Empty;
        selectedReplayElementKeyframeId = kind != ReplayTimelineElementKind.Camera
            ? timeline.ElementKeyframes.FirstOrDefault(keyframe =>
                keyframe.TrackId == track.Id)?.Id ?? Guid.Empty
            : Guid.Empty;
        EnsureReplayTimelineElementTrackVisible(track.Id);
        SyncReplayTimelineSelection();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private void EnsureReplayTimelineElementTrackVisible(Guid trackId)
    {
        IReadOnlyList<DirectorReplayElementTrack> tracks = ActiveReplayTimelineElementTracks();
        int index = tracks.ToList().FindIndex(track => track.Id == trackId);
        if (index < 0 || replayTimelineBounds is null)
        {
            return;
        }
        var rows = ReplayRows();
        double available = replayTimelineBounds.OuterHeight / GuiElement.scaled(1) - ReplayTimelineHeaderHeight;
        if (index < replayTimelineFirstVisibleTrack)
        {
            replayTimelineFirstVisibleTrack = index;
        }
        else if (rows.Top(index + 1) - rows.Top(replayTimelineFirstVisibleTrack) > available)
        {
            replayTimelineFirstVisibleTrack = index;
        }
    }

    private bool ToggleReplayElementTrack(Guid trackId)
    {
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item => item.Id == trackId);
        if (track is null)
        {
            return true;
        }
        track.Enabled = !track.Enabled;
        CommitReplayViewportTimeline(timeline);
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private void EnsureReplayElementTrackStored(DirectorReplayElementTrackType kind)
    {
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        if (!timeline.Tracks.Any(track => track.TrackType == kind))
        {
            timeline.Tracks.Add(new DirectorReplayElementTrack { TrackType = kind });
            CommitReplayViewportTimeline(timeline);
        }
    }

    private void OnReplayElementTypeChanged(string value, bool selected)
    {
        if (selected && ReplayViewportSourceOptions().Any(option => option.Value == value))
        {
            replayElementTypeCode = value;
        }
    }

    private bool OpenReplayElementMenu()
    {
        ShowReplayFloatingEditor(ReplayFloatingEditorKind.AddElement);
        return true;
    }

    private bool AddReplayElementTrack(string code)
    {
        replayElementTypeCode = code;
        CloseReplayFloatingEditor();
        return AddSelectedReplayElementTrack();
    }

    private bool OpenReplayTrackEditor(Guid trackId)
    {
        DirectorReplayElementTrack? track = CurrentReplayViewportTimeline().Tracks
            .FirstOrDefault(item => item.Id == trackId);
        if (track is null)
        {
            return true;
        }
        selectedReplayElementTrackId = track.Id;
        selectedReplayTimelineElementTrack = ReplayTimelineElementKindFor(track.TrackType);
        replayFloatingTrackId = track.Id;
        replayFloatingTrackName = track.Name.Length == 0
            ? ReplayTimelineElementTrackName(ReplayTimelineElementKindFor(track.TrackType))
            : track.Name;
        ShowReplayFloatingEditor(ReplayFloatingEditorKind.Track);
        return true;
    }

    private bool ApplyReplayTrackEditor()
    {
        CaptureReplayFloatingEditorInputs();
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item =>
            item.Id == replayFloatingTrackId);
        if (track is null)
        {
            CloseReplayFloatingEditor();
            return true;
        }
        if (track.TrackType != DirectorReplayElementTrackType.VoiceAudio)
        {
            track.Name = replayFloatingTrackName.Trim();
        }
        if (CommitReplayViewportTimeline(timeline))
        {
            ComposeTimeline();
            CloseReplayFloatingEditor();
        }
        return true;
    }

    private bool ClearReplayTrackKeyframes()
    {
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        timeline.Clips.RemoveAll(clip => clip.TrackId == replayFloatingTrackId);
        timeline.ElementKeyframes.RemoveAll(keyframe => keyframe.TrackId == replayFloatingTrackId);
        if (selectedReplayElementTrackId == replayFloatingTrackId)
        {
            selectedReplayViewportClipId = Guid.Empty;
            selectedReplayElementKeyframeId = Guid.Empty;
            replayViewportFieldsId = Guid.Empty;
            replayElementFieldsId = Guid.Empty;
        }
        if (CommitReplayViewportTimeline(timeline))
        {
            ComposeTimeline();
            CloseReplayFloatingEditor();
        }
        return true;
    }

    private bool DeleteReplayTrackFromEditor()
    {
        Guid trackId = replayFloatingTrackId;
        CloseReplayFloatingEditor();
        return DeleteReplayElementTrack(trackId);
    }

    private void OnReplaySceneMenuChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }
        if (value == "scene-command-new")
        {
            ComposeTimeline();
            OpenNewReplaySceneEditor();
            return;
        }
        if (value == "scene-command-rename")
        {
            ComposeTimeline();
            OpenRenameReplaySceneEditor();
            return;
        }
        if (value == "scene-command-delete")
        {
            DeleteActiveReplayScene();
            return;
        }
        if (!Guid.TryParse(value, out Guid sceneId))
        {
            return;
        }
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        if (!timeline.ActivateScene(sceneId))
        {
            return;
        }
        ResetReplayTimelineSelection();
        replayTimelineFirstVisibleTrack = 0;
        if (CommitReplayViewportTimeline(timeline))
        {
            CloseReplayFloatingEditor();
            ComposeInspector();
            ComposeTimeline();
        }
    }

    private bool OpenNewReplaySceneEditor()
    {
        replayFloatingSceneName = string.Empty;
        ShowReplayFloatingEditor(ReplayFloatingEditorKind.NewScene);
        return true;
    }

    private bool OpenRenameReplaySceneEditor()
    {
        replayFloatingSceneName = owner.ReplayViewportTimeline.ActiveScene?.Name ?? string.Empty;
        ShowReplayFloatingEditor(ReplayFloatingEditorKind.RenameScene);
        return true;
    }

    private bool SaveReplaySceneName()
    {
        CaptureReplayFloatingEditorInputs();
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        string name = replayFloatingSceneName.Trim();
        if (replayFloatingEditorKind == ReplayFloatingEditorKind.NewScene)
        {
            timeline.AddScene(name, activate: true);
            ResetReplayTimelineSelection();
            replayTimelineFirstVisibleTrack = 0;
        }
        else if (timeline.ActiveScene is DirectorReplayElementScene active)
        {
            active.Name = string.IsNullOrWhiteSpace(name) ? active.Name : name;
        }
        if (CommitReplayViewportTimeline(timeline))
        {
            CloseReplayFloatingEditor();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool DeleteActiveReplayScene()
    {
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        if (!timeline.RemoveScene(timeline.ActiveSceneId))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-scene-last-delete"));
            ComposeTimeline();
            return true;
        }
        ResetReplayTimelineSelection();
        replayTimelineFirstVisibleTrack = 0;
        if (CommitReplayViewportTimeline(timeline))
        {
            CloseReplayFloatingEditor();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private void ResetReplayTimelineSelection()
    {
        selectedReplayElementTrackId = Guid.Empty;
        selectedReplayTimelineElementTrack = null;
        selectedReplayViewportClipId = Guid.Empty;
        selectedReplayElementKeyframeId = Guid.Empty;
        selectedReplayMarkerId = Guid.Empty;
        selectedReplaySpeedKeyframeId = Guid.Empty;
        replayViewportFieldsId = Guid.Empty;
        replayElementFieldsId = Guid.Empty;
    }

    private void ShowReplayFloatingEditor(ReplayFloatingEditorKind kind)
    {
        CaptureKeyframeAnchor();
        replayFloatingEditorKind = kind;
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        replayFloatingX = capi.Input.MouseX / scale + 14d;
        replayFloatingY = capi.Input.MouseY / scale - 18d;
        ComposeWorkflowCanvas();
    }

    private void OpenSelectedReplayCameraKeyframeEditor()
    {
        if (SelectedReplayViewportClip() is null)
        {
            return;
        }
        bool alreadyOpen = replayFloatingEditorKind == ReplayFloatingEditorKind.CameraKeyframe;
        replayFloatingEditorKind = ReplayFloatingEditorKind.CameraKeyframe;
        if (alreadyOpen)
        {
            ComposeWorkflowCanvas();
        }
        else
        {
            ShowReplayFloatingEditor(ReplayFloatingEditorKind.CameraKeyframe);
        }
    }

    private void OpenSelectedReplayElementKeyframeEditor()
    {
        if (SelectedReplayElementKeyframe() is null)
        {
            return;
        }
        bool alreadyOpen = replayFloatingEditorKind == ReplayFloatingEditorKind.ElementKeyframe;
        replayFloatingEditorKind = ReplayFloatingEditorKind.ElementKeyframe;
        if (alreadyOpen)
        {
            ComposeWorkflowCanvas();
        }
        else
        {
            ShowReplayFloatingEditor(ReplayFloatingEditorKind.ElementKeyframe);
        }
    }

    private void CloseReplayFloatingEditor()
    {
        replayFloatingEditorKind = ReplayFloatingEditorKind.None;
        replayFloatingTrackId = Guid.Empty;
        replayEntityPopupEntity = null;
        RemoveFloatingEditorComposer();
    }

    private void CaptureReplayFloatingEditorInputs()
    {
        if (floatingEditorDialog.Composer is not GuiComposer composer)
        {
            return;
        }
        replayFloatingTrackName = composer.GetTextInput("replayTrackName")?.GetText()
            ?? replayFloatingTrackName;
        replayFloatingSceneName = composer.GetTextInput("replaySceneName")?.GetText()
            ?? replayFloatingSceneName;
        CaptureReplayPropertyInputs(composer);
    }

    private bool AddSelectedReplayElementTrack()
    {
        DirectorReplayElementTrackType type = replayElementTypeCode switch
        {
            "element-speed" => DirectorReplayElementTrackType.Speed,
            "element-camera-orbit" => DirectorReplayElementTrackType.CameraOrbit,
            "element-track-entity" => DirectorReplayElementTrackType.TrackEntity,
            "element-follow-entity" => DirectorReplayElementTrackType.FollowEntity,
            "element-camera-shake" => DirectorReplayElementTrackType.CameraShake,
            "element-fov" => DirectorReplayElementTrackType.Fov,
            "element-timelapse" => DirectorReplayElementTrackType.Timelapse,
            "element-time-of-day" => DirectorReplayElementTrackType.TimeOfDay,
            "element-freeze" => DirectorReplayElementTrackType.Freeze,
            _ => DirectorReplayElementTrackType.Camera
        };
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayElementTrack track = new() { TrackType = type };
        timeline.Tracks.Add(track);
        if (!CommitReplayViewportTimeline(timeline))
        {
            return true;
        }
        if (type == DirectorReplayElementTrackType.Timelapse)
        {
            return AddTimelapseSegment(track);
        }
        if (type == DirectorReplayElementTrackType.FollowEntity)
        {
            return AddFollowEntitySegment(track);
        }
        return SelectReplayTimelineElementTrack(track.Id);
    }

    private bool AddReplayKeyframeForTrack(Guid trackId)
    {
        DirectorReplayElementTrack? track = CurrentReplayViewportTimeline().Tracks.FirstOrDefault(
            item => item.Id == trackId);
        if (track is null)
        {
            return true;
        }
        selectedReplayElementTrackId = track.Id;
        selectedReplayTimelineElementTrack = ReplayTimelineElementKindFor(track.TrackType);
        return track.TrackType == DirectorReplayElementTrackType.Camera
            ? CaptureReplayViewport()
            : track.TrackType == DirectorReplayElementTrackType.Timelapse
                ? AddTimelapseSegment(track)
            : track.TrackType == DirectorReplayElementTrackType.FollowEntity
                ? AddFollowEntitySegment(track)
            : AddReplayElementKeyframeAtHead(track);
    }

    private bool AddTimelapseSegment(DirectorReplayElementTrack track)
    {
        DirectorReplayViewportTimeline timeline = CurrentReplayViewportTimeline();
        if (owner.ReplayDurationSeconds <= 1e-9d)
        {
            return true;
        }

        double replayDuration = Math.Max(0d, owner.ReplayDurationSeconds);
        double start = ReplayTimelineTickAt(Math.Clamp(
            owner.ReplayTimeSeconds,
            0d,
            replayDuration)) / ReplayTimelineTicksPerSecond;
        double end = replayDuration;
        double minimumGap = 1d / ReplayTimelineTicksPerSecond;
        if (end - start < minimumGap)
        {
            double defaultSpan = Math.Min(
                replayDuration,
                Math.Max(DefaultTimelapseDurationSeconds, minimumGap));
            start = Math.Max(0d, replayDuration - defaultSpan);
            start = ReplayTimelineTickAt(start) / ReplayTimelineTicksPerSecond;
        }
        double duration = track.TimelapseDurationSeconds > 0d
            ? track.TimelapseDurationSeconds
            : DefaultTimelapseDurationSeconds;
        duration = Math.Clamp(duration, 0.001d, MaxTimelapseDurationSeconds);
        Guid pairId = Guid.NewGuid();
        int startTick = ReplayTimelineTickAt(start);
        DirectorReplayElementKeyframe first = new()
        {
            TrackId = track.Id,
            TrackType = DirectorReplayElementTrackType.Timelapse,
            TimeSeconds = start,
            TimelapseTick = startTick,
            TimelapseRole = DirectorReplayTimelapseKeyframeRole.Start,
            TimelapsePairId = pairId,
            TimelapseDurationSeconds = duration,
            InterpolationToNext = DirectorReplayElementInterpolation.Linear
        };
        DirectorReplayElementKeyframe second = new()
        {
            TrackId = track.Id,
            TrackType = DirectorReplayElementTrackType.Timelapse,
            TimeSeconds = end,
            TimelapseTick = startTick + Math.Max(1,
                (int)Math.Round(duration * ReplayTimelineTicksPerSecond)),
            TimelapseRole = DirectorReplayTimelapseKeyframeRole.End,
            TimelapsePairId = pairId,
            TimelapseDurationSeconds = duration,
            InterpolationToNext = DirectorReplayElementInterpolation.Linear
        };
        timeline.ElementKeyframes.Add(first);
        timeline.ElementKeyframes.Add(second);
        track.TimelapseDurationSeconds = duration;
        DirectorReplayTimelapse.NormalizePairs(timeline.ElementKeyframes, track.Id);

        selectedReplayElementTrackId = track.Id;
        selectedReplayTimelineElementTrack = ReplayTimelineElementKind.Timelapse;
        selectedReplayElementKeyframeId = first.Id;
        selectedReplayViewportClipId = Guid.Empty;
        selectedReplayMarkerId = Guid.Empty;
        selectedReplaySpeedKeyframeId = Guid.Empty;
        replayElementFieldsId = Guid.Empty;
        if (CommitReplayViewportTimeline(timeline))
        {
            ComposeInspector();
            ComposeTimeline();
            OpenSelectedReplayElementKeyframeEditor();
        }
        return true;
    }

    private bool AddFollowEntitySegment(
        DirectorReplayElementTrack track,
        string? actorKey = null,
        long entityId = 0L)
    {
        DirectorReplayViewportTimeline timeline = CurrentReplayViewportTimeline();
        if (owner.ReplayDurationSeconds <= 1e-9d)
        {
            return true;
        }

        double replayDuration = Math.Max(0d, owner.ReplayDurationSeconds);
        double minimumGap = 1d / ReplayTimelineTicksPerSecond;
        double start = ReplayTimelineTickAt(Math.Clamp(
            owner.ReplayTimeSeconds,
            0d,
            replayDuration)) / ReplayTimelineTicksPerSecond;
        double end = Math.Min(replayDuration, start + 5d);
        if (end - start < minimumGap)
        {
            start = Math.Max(0d, replayDuration - Math.Min(5d, replayDuration));
            start = ReplayTimelineTickAt(start) / ReplayTimelineTicksPerSecond;
            end = replayDuration;
        }

        if (string.IsNullOrWhiteSpace(actorKey))
        {
            DirectorReplayActorPathSource? source = owner.ReplayActorPathSources
                .Select(item => (DirectorReplayActorPathSource?)item)
                .FirstOrDefault();
            if (source is DirectorReplayActorPathSource selected)
            {
                actorKey = selected.ActorKey;
                entityId = selected.EntityId;
            }
        }

        DirectorReplayElementKeyframe first = new()
        {
            TrackId = track.Id,
            TrackType = DirectorReplayElementTrackType.FollowEntity,
            TimeSeconds = start,
            InterpolationToNext = DirectorReplayElementInterpolation.Hold,
            FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.Start,
            EntityActorKey = actorKey?.Trim() ?? string.Empty,
            EntityId = entityId,
            FollowViewMode = CameraFollowViewMode.ThirdPerson,
            FollowMotionMode = CameraFollowMotionMode.Smooth,
            FollowAnchor = CameraFollowAnchor.Head,
            FollowDistance = CameraFollowMath.DefaultDistance,
            FollowHeight = CameraFollowMath.DefaultHeight,
            FollowSmoothTimeSeconds = CameraFollowMath.DefaultSmoothTimeSeconds,
            FollowDelaySeconds = CameraFollowMath.DefaultDelaySeconds,
            FollowFov = CameraFollowMath.DefaultFov
        };
        DirectorReplayElementKeyframe second = first.DeepClone();
        second.Id = Guid.NewGuid();
        second.TimeSeconds = end;
        second.FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.End;
        timeline.ElementKeyframes.Add(first);
        timeline.ElementKeyframes.Add(second);

        selectedReplayElementTrackId = track.Id;
        selectedReplayTimelineElementTrack = ReplayTimelineElementKind.FollowEntity;
        selectedReplayElementKeyframeId = first.Id;
        selectedReplayViewportClipId = Guid.Empty;
        selectedReplayMarkerId = Guid.Empty;
        selectedReplaySpeedKeyframeId = Guid.Empty;
        replayElementFieldsId = Guid.Empty;
        if (CommitReplayViewportTimeline(timeline))
        {
            ComposeInspector();
            ComposeTimeline();
            OpenSelectedReplayElementKeyframeEditor();
        }
        return true;
    }

    private bool AddReplayElementKeyframeAtHead(DirectorReplayElementTrack track)
    {
        DirectorReplayElementTrackType trackType = track.TrackType;
        if (trackType is DirectorReplayElementTrackType.Camera
            or DirectorReplayElementTrackType.Marker)
        {
            return true;
        }
        ReplayTimelineElementKind kind = ReplayTimelineElementKindFor(trackType);

        double replayDuration = owner.ReplayDurationSeconds;
        double time = ReplayTimelineTickAt(Math.Clamp(owner.ReplayTimeSeconds, 0d, replayDuration))
            / ReplayTimelineTicksPerSecond;
        DirectorReplayElementKeyframe keyframe = new()
        {
            TrackId = track.Id,
            TrackType = trackType,
            TimeSeconds = time,
            InterpolationToNext = trackType is DirectorReplayElementTrackType.Freeze
                or DirectorReplayElementTrackType.BlockOverride
                or DirectorReplayElementTrackType.VoiceAudio
                or DirectorReplayElementTrackType.FollowEntity
                    ? DirectorReplayElementInterpolation.Hold
                    : DirectorReplayElementInterpolation.Smooth
        };
        if (owner.TryCaptureReplayCameraKeyframe(
                ReplayTimelineElementTrackName(kind),
                out CameraPreset pose,
                out _))
        {
            keyframe.X = pose.X;
            keyframe.Y = pose.Y;
            keyframe.Z = pose.Z;
            keyframe.Dimension = pose.Dimension;
            keyframe.Yaw = pose.Yaw;
            keyframe.Pitch = pose.Pitch;
            keyframe.Roll = trackType == DirectorReplayElementTrackType.CameraOrbit
                ? 0f
                : pose.Roll;
            if (trackType == DirectorReplayElementTrackType.Fov)
            {
                keyframe.Fov = Math.Clamp(
                    pose.Fov,
                    DirectorReplayElementTimeline.MinFovDegrees,
                    DirectorReplayElementTimeline.MaxFovDegrees);
            }
            else if (trackType == DirectorReplayElementTrackType.CameraOrbit)
            {
                // Seed an orbit from the captured camera view. Storing the
                // camera position as the orbit center would move the shot as
                // soon as playback evaluates the orbit radius.
                double yaw = pose.Yaw * Math.PI / 180d;
                double pitch = pose.Pitch * Math.PI / 180d;
                double distance = Math.Max(0.01d, keyframe.Distance);
                double horizontal = Math.Cos(pitch) * distance;
                keyframe.X = pose.X - Math.Sin(yaw) * horizontal;
                keyframe.Y = pose.Y + Math.Sin(pitch) * distance;
                keyframe.Z = pose.Z - Math.Cos(yaw) * horizontal;
            }
        }
        if (trackType == DirectorReplayElementTrackType.TrackEntity)
        {
            IReadOnlyList<DirectorReplayActorPathSource> sources = owner.ReplayActorPathSources;
            if (sources.Count > 0)
            {
                DirectorReplayActorPathSource source = sources[0];
                keyframe.EntityId = source.EntityId;
                keyframe.EntityActorKey = source.ActorKey;
                keyframe.BodyPart = "head";
                if (owner.TryResolveReplayActor(
                        source.ActorKey,
                        out _,
                        out Entity? target)
                    && target is not null
                    && owner.TryGetReplayTrackingTargetPose(
                        target,
                        keyframe.BodyPart,
                        out DirectorReplayTrackedTargetPose targetPose))
                {
                    DirectorReplayCameraElementMath.ConfigureTrackFromCamera(
                        keyframe,
                        targetPose,
                        pose);
                }
                else
                {
                    // The actor may not be loaded at edit time. Keep a valid
                    // Flashback-style default; playback will resolve its
                    // stable actor key once the binding is available.
                    keyframe.OffsetX = 0d;
                    keyframe.OffsetY = 0d;
                    keyframe.OffsetZ = 0d;
                    keyframe.ViewOffsetX = 0d;
                    keyframe.ViewOffsetY = 0d;
                    keyframe.ViewOffsetZ = 3d;
                    keyframe.Yaw = 0f;
                    keyframe.Pitch = 0f;
                    keyframe.Roll = pose.Roll;
                }
            }
            else
            {
                keyframe.OffsetX = 0d;
                keyframe.OffsetY = 0d;
                keyframe.OffsetZ = 0d;
                keyframe.ViewOffsetX = 0d;
                keyframe.ViewOffsetY = 0d;
                keyframe.ViewOffsetZ = 3d;
                keyframe.Yaw = 0f;
                keyframe.Pitch = 0f;
            }
        }
        else if (trackType == DirectorReplayElementTrackType.Timelapse)
        {
            keyframe.TimelapseTick = ReplayTimelineTickAt(time);
        }
        else if (trackType == DirectorReplayElementTrackType.TimeOfDay)
        {
            double hoursPerDay = Math.Max(1d, capi.World.Calendar.HoursPerDay);
            keyframe.TimeOfDayTick = (int)Math.Round(
                (capi.World.Calendar.TotalHours % hoursPerDay) / hoursPerDay * 24000d);
        }
        else if (trackType == DirectorReplayElementTrackType.Speed)
        {
            keyframe.TickRate = (float)Math.Clamp(
                owner.ReplayBasePlaybackSpeed * DirectorReplayElementTimeline.TicksPerSecond,
                1d,
                400d);
        }

        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayElementKeyframe? existing = timeline.ElementKeyframes.FirstOrDefault(item =>
            item.TrackId == track.Id
            && ReplayTimelineTickAt(item.TimeSeconds) == ReplayTimelineTickAt(time));
        if (existing is not null)
        {
            keyframe.Id = existing.Id;
            timeline.ElementKeyframes.Remove(existing);
        }
        timeline.ElementKeyframes.Add(keyframe);
        selectedReplayElementTrackId = track.Id;
        selectedReplayTimelineElementTrack = kind;
        selectedReplayElementKeyframeId = keyframe.Id;
        selectedReplayViewportClipId = Guid.Empty;
        selectedReplayMarkerId = Guid.Empty;
        selectedReplaySpeedKeyframeId = Guid.Empty;
        replayElementFieldsId = Guid.Empty;
        if (CommitReplayViewportTimeline(timeline))
        {
            ComposeInspector();
            ComposeTimeline();
            OpenSelectedReplayElementKeyframeEditor();
        }
        return true;
    }

    private bool DeleteReplayElementTrack(Guid trackId)
    {
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        if (!DirectorReplayViewportTimeline.RemoveTrack(timeline, trackId))
        {
            return true;
        }

        if (selectedReplayElementTrackId == trackId)
        {
            selectedReplayElementTrackId = Guid.Empty;
            selectedReplayTimelineElementTrack = null;
            selectedReplayViewportClipId = Guid.Empty;
            selectedReplayMarkerId = Guid.Empty;
            selectedReplaySpeedKeyframeId = Guid.Empty;
            selectedReplayElementKeyframeId = Guid.Empty;
            replayElementFieldsId = Guid.Empty;
        }
        if (CommitReplayViewportTimeline(timeline))
        {
            CloseReplayFloatingEditor();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool CaptureReplayViewport()
    {
        if (!owner.IsReplayPlaying)
        {
            return true;
        }

        string name = UniqueResourceName(
            Lang.Get("vsdirector:replay-camera-default-name"),
            owner.ReplayViewportClips
                .Where(clip => clip.SourceType == DirectorReplayViewportSourceType.StaticCamera)
                .Select(clip => clip.Name));
        if (!owner.TryCaptureReplayCameraKeyframe(name, out CameraPreset pose, out _))
        {
            return true;
        }

        AddReplayCameraKeyframe(pose);
        return true;
    }

    private void AddReplayCameraKeyframe(CameraPreset pose)
    {
        double replayDuration = owner.ReplayDurationSeconds;
        if (!owner.IsReplayPlaying || replayDuration < 0.05d)
        {
            return;
        }

        double start = ReplayTimelineTickAt(Math.Clamp(
            owner.ReplayTimeSeconds,
            0d,
            replayDuration)) / ReplayTimelineTicksPerSecond;
        double duration = Math.Clamp(replayDuration - start, 0.05d, 5d);
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayElementTrack? cameraTrack = timeline.Tracks.FirstOrDefault(track =>
            track.Id == selectedReplayElementTrackId
            && track.TrackType == DirectorReplayElementTrackType.Camera);
        cameraTrack ??= timeline.Tracks.FirstOrDefault(track =>
            track.TrackType == DirectorReplayElementTrackType.Camera);
        if (cameraTrack is null)
        {
            cameraTrack = new DirectorReplayElementTrack
            {
                TrackType = DirectorReplayElementTrackType.Camera
            };
            timeline.Tracks.Add(cameraTrack);
        }
        selectedReplayElementTrackId = cameraTrack.Id;
        DirectorReplayViewportClip? existing = timeline.Clips.FirstOrDefault(clip =>
            clip.SourceType == DirectorReplayViewportSourceType.StaticCamera
            && clip.TrackId == cameraTrack.Id
            && ReplayTimelineTickAt(clip.TimelineStartSeconds) == ReplayTimelineTickAt(start));
        if (existing is not null)
        {
            existing.Name = pose.Name;
            existing.SourceId = Guid.Empty;
            existing.CameraPose = pose.DeepClone();
            existing.Enabled = true;
            selectedReplayTimelineElementTrack = ReplayTimelineElementKind.Camera;
            selectedReplayViewportClipId = existing.Id;
            selectedReplayMarkerId = Guid.Empty;
            selectedReplaySpeedKeyframeId = Guid.Empty;
            selectedReplayElementKeyframeId = Guid.Empty;
            if (CommitReplayViewportTimeline(timeline))
            {
                ComposeInspector();
                ComposeTimeline();
                OpenSelectedReplayCameraKeyframeEditor();
            }
            return;
        }
        DirectorReplayViewportClip clip = new()
        {
            Name = pose.Name,
            SourceType = DirectorReplayViewportSourceType.StaticCamera,
            SourceId = Guid.Empty,
            CameraPose = pose.DeepClone(),
            CameraInterpolationToNext = CameraPathInterpolation.EaseInOut,
            TrackId = cameraTrack.Id,
            TimelineStartSeconds = start,
            DurationSeconds = duration,
            PlaybackRate = 1d,
            TrackOrder = timeline.Clips.Count,
            Enabled = true
        };
        timeline.Clips.Add(clip);
        selectedReplayTimelineElementTrack = ReplayTimelineElementKind.Camera;
        selectedReplayViewportClipId = clip.Id;
        selectedReplayMarkerId = Guid.Empty;
        selectedReplaySpeedKeyframeId = Guid.Empty;
        selectedReplayElementKeyframeId = Guid.Empty;
        if (CommitReplayViewportTimeline(timeline))
        {
            ComposeInspector();
            ComposeTimeline();
            OpenSelectedReplayCameraKeyframeEditor();
        }
    }

    private void OnReplayCameraInterpolationChanged(string value, bool selected)
    {
        if (selected && value is "step" or "linear" or "ease" or "bezier")
        {
            replayViewportInterpolationCode = value;
        }
    }

    private bool ApplyReplayViewport()
    {
        PreserveInputs();
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayViewportClip? clip = timeline.Clips.FirstOrDefault(
            item => item.Id == selectedReplayViewportClipId);
        if (clip is null
            || !double.TryParse(replayViewportStartText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double start)
            || !double.IsFinite(start)
            || !TryParseReplayCameraPose(clip, out CameraPreset pose))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-viewport-invalid"));
            return true;
        }

        clip.Name = string.IsNullOrWhiteSpace(replayViewportNameText)
            ? DirectorReplayViewportTimeline.DefaultName(clip.SourceType)
            : replayViewportNameText.Trim();
        clip.TimelineStartSeconds = ReplayTimelineTickAt(start) / ReplayTimelineTicksPerSecond;
        pose.Name = clip.Name;
        pose.Id = Guid.Empty;
        clip.CameraPose = pose;
        clip.SourceId = Guid.Empty;
        clip.CameraInterpolationToNext = replayViewportInterpolationCode switch
        {
            "step" => CameraPathInterpolation.Step,
            "linear" => CameraPathInterpolation.Linear,
            "bezier" => CameraPathInterpolation.Bezier,
            _ => CameraPathInterpolation.EaseInOut
        };
        if (CommitReplayViewportTimeline(timeline))
        {
            CloseReplayFloatingEditor();
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool TryParseReplayCameraPose(
        DirectorReplayViewportClip clip,
        out CameraPreset pose)
    {
        CameraPreset fallback = ReplayCameraElementPose(clip)?.DeepClone() ?? new CameraPreset
        {
            Fov = 70f
        };
        pose = fallback;
        if (!double.TryParse(replayViewportXText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(replayViewportYText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double y)
            || !double.TryParse(replayViewportZText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double z)
            || !int.TryParse(replayViewportDimensionText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int dimension)
            || !float.TryParse(replayViewportYawText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out float yaw)
            || !float.TryParse(replayViewportPitchText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out float pitch)
            || !float.TryParse(replayViewportRollText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out float roll)
            || !double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(z)
            || !float.IsFinite(yaw)
            || !float.IsFinite(pitch)
            || !float.IsFinite(roll))
        {
            return false;
        }

        (pose.X, pose.Y, pose.Z) = owner.AbsolutePosition(x, y, z);
        pose.Dimension = dimension;
        pose.Yaw = yaw;
        pose.Pitch = pitch;
        pose.Roll = roll;
        return true;
    }

    private bool RecaptureSelectedReplayCameraKeyframe()
    {
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayViewportClip? clip = timeline.Clips.FirstOrDefault(item =>
            item.Id == selectedReplayViewportClipId
            && item.SourceType == DirectorReplayViewportSourceType.StaticCamera);
        if (clip is null
            || !owner.TryCaptureReplayCameraKeyframe(clip.Name, out CameraPreset pose, out _))
        {
            return true;
        }

        clip.CameraPose = pose;
        clip.SourceId = Guid.Empty;
        if (CommitReplayViewportTimeline(timeline))
        {
            replayViewportFieldsId = Guid.Empty;
            ComposeInspector();
            ComposeTimeline();
        }
        return true;
    }

    private bool DeleteReplayViewport()
    {
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        if (timeline.Clips.RemoveAll(clip => clip.Id == selectedReplayViewportClipId) > 0)
        {
            selectedReplayViewportClipId = timeline.Clips.FirstOrDefault(clip =>
                clip.TrackId == selectedReplayElementTrackId
                &&
                clip.SourceType == DirectorReplayViewportSourceType.StaticCamera)?.Id ?? Guid.Empty;
            if (CommitReplayViewportTimeline(timeline))
            {
                ComposeInspector();
                ComposeTimeline();
                CloseReplayFloatingEditor();
            }
        }
        return true;
    }

    private bool SetPreloadEraseMode(bool erase)
    {
        preloadEraseMode = erase;
        ComposeWorkflowCanvas();
        return true;
    }

    private bool TogglePreloadEditor()
    {
        preloadEditorExpanded = !preloadEditorExpanded;
        selectingPreloadGrid = false;
        panningPreloadGrid = false;
        preloadPanChunkX = 0;
        preloadPanChunkZ = 0;
        ComposeInspector();
        ComposeWorkflowCanvas();
        return true;
    }

    private bool ZoomPreloadMap(int delta)
    {
        preloadGridRadius = Math.Clamp(
            preloadGridRadius + delta,
            MinPreloadGridRadius,
            MaxPreloadGridRadius);
        ComposeWorkflowCanvas();
        return true;
    }

    private bool FitPreloadMap()
    {
        preloadGridRadius = 6;
        preloadPanChunkX = 0;
        preloadPanChunkZ = 0;
        ComposeWorkflowCanvas();
        return true;
    }


    private string PreloadMapToolbarText()
        => Lang.Get(
            "vsdirector:preload-map-toolbar",
            preloadGridRadius,
            preloadPanChunkX,
            preloadPanChunkZ);

    private bool OnPreloadGridRadiusChanged(int value)
    {
        preloadGridRadius = Math.Clamp(value, MinPreloadGridRadius, MaxPreloadGridRadius);
        return true;
    }

    private bool OnPreloadBrushRadiusChanged(int value)
    {
        preloadBrushRadius = Math.Clamp(value, 0, MaxPreloadBrushRadius);
        return true;
    }

    private bool OnPreloadReplayTimeChanged(int value)
    {
        DirectorReplayInfo? selected = PreloadEditorReplayInfo();
        preloadReplayTime = selected is null
            ? 0d
            : selected.DurationSeconds * Math.Clamp(value, 0, 1000) / 1000d;
        return true;
    }


    private bool TryGetPreloadGridState(out PreloadGridState state)
    {
        if (section == DirectorWorkspaceSection.Paths
            && pathEdit is { Keyframes.Count: > 0 } path)
        {
            int index = SelectedPathKeyframeIndex();
            if (index < 0 || index >= path.Keyframes.Count)
            {
                state = default!;
                return false;
            }
            VSDirector.Data.CameraKeyframe frame = path.Keyframes[index];
            DirectorReplayPreloadPlan plan = DirectorReplayPreloadPlanner.Build(path);
            HashSet<DirectorReplayPreloadColumn> included = path.IncludedPreloadColumns.ToHashSet();
            HashSet<DirectorReplayPreloadColumn> excluded = path.ExcludedPreloadColumns.ToHashSet();
            HashSet<DirectorReplayPreloadColumn> final = DirectorReplayPreloadPlanner.ApplyOverrides(
                plan.AutomaticColumns,
                included,
                excluded,
                out _).ToHashSet();
            state = new PreloadGridState(
                path.Id,
                IsReplay: false,
                (int)Math.Floor(frame.X / 32d) + preloadPanChunkX,
                (int)Math.Floor(frame.Z / 32d) + preloadPanChunkZ,
                (int)Math.Floor(frame.X / 32d),
                (int)Math.Floor(frame.Z / 32d),
                frame.Dimension,
                plan.AutomaticColumnSet,
                final,
                included,
                excluded);
            return true;
        }

        DirectorReplayInfo? replayInfo = section == DirectorWorkspaceSection.Replay
            ? PreloadEditorReplayInfo()
            : null;
        if (replayInfo is null)
        {
            state = null!;
            return false;
        }

        owner.RequestReplayPreloadEditor(replayInfo.Id);
        if (!owner.TryGetReplayPreloadEditorState(
                replayInfo.Id,
                preloadReplayTime,
                out DirectorReplayPreloadEditorState replayState))
        {
            state = null!;
            return false;
        }

        if (preloadDraftReplayId != replayInfo.Id)
        {
            preloadDraftReplayId = replayInfo.Id;
            preloadDraftIncluded = replayState.IncludedColumns.ToHashSet();
            preloadDraftExcluded = replayState.ExcludedColumns.ToHashSet();
        }
        HashSet<DirectorReplayPreloadColumn> replayFinal = DirectorReplayPreloadPlanner.ApplyOverrides(
            replayState.Plan.AutomaticColumns,
            preloadDraftIncluded,
            preloadDraftExcluded,
            out _).ToHashSet();
        state = new PreloadGridState(
            replayInfo.Id,
            IsReplay: true,
            replayState.CenterChunkX + preloadPanChunkX,
            replayState.CenterChunkZ + preloadPanChunkZ,
            replayState.CenterChunkX,
            replayState.CenterChunkZ,
            replayState.Dimension,
            replayState.Plan.AutomaticColumnSet,
            replayFinal,
            preloadDraftIncluded,
            preloadDraftExcluded);
        return true;
    }

    private string PreloadGridSummary()
    {
        if (!TryGetPreloadGridState(out PreloadGridState state))
        {
            DirectorReplayInfo? selected = PreloadEditorReplayInfo();
            return selected is not null && owner.IsReplayPreloadEditorLoading(selected.Id)
                ? Lang.Get("vsdirector:preload-loading")
                : Lang.Get("vsdirector:preload-empty");
        }

        return Lang.Get(
            "vsdirector:preload-summary",
            state.Automatic.Count,
            state.Included.Count,
            state.Excluded.Count,
            state.Final.Count,
            state.CenterChunkX,
            state.CenterChunkZ);
    }

    private void RenderPreloadGrid(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        float x = (float)bounds.renderX;
        float y = (float)bounds.renderY;
        float width = (float)bounds.OuterWidth;
        float height = (float)bounds.OuterHeight;
        capi.Render.RenderRectangle(x, y, 100f, width, height, ColorUtil.ToRgba(245, 10, 14, 17));
        if (!TryGetPreloadGridState(out PreloadGridState state))
        {
            return;
        }

        PreloadGridMetrics metrics = GetPreloadGridMetrics(bounds);
        float gridX = x + (float)metrics.OffsetX;
        float gridY = y + (float)metrics.OffsetY;
        float cellSize = (float)metrics.CellSize;
        int unloadedColor = ColorUtil.ToRgba(205, 106, 39, 46);
        int excludedColor = ColorUtil.ToRgba(245, 204, 56, 66);
        int automaticColor = ColorUtil.ToRgba(230, 42, 139, 91);
        int includedColor = ColorUtil.ToRgba(245, 65, 205, 118);
        int selectionMinX = Math.Min(preloadSelectionStartX, preloadSelectionEndX) - preloadBrushRadius;
        int selectionMaxX = Math.Max(preloadSelectionStartX, preloadSelectionEndX) + preloadBrushRadius;
        int selectionMinZ = Math.Min(preloadSelectionStartZ, preloadSelectionEndZ) - preloadBrushRadius;
        int selectionMaxZ = Math.Max(preloadSelectionStartZ, preloadSelectionEndZ) + preloadBrushRadius;
        for (int row = 0; row < metrics.CellsZ; row++)
        {
            for (int columnIndex = 0; columnIndex < metrics.CellsX; columnIndex++)
            {
                DirectorReplayPreloadColumn column = new(
                    state.CenterChunkX + columnIndex - metrics.HalfX,
                    state.CenterChunkZ + row - metrics.HalfZ,
                    state.Dimension);
                int color = state.Excluded.Contains(column)
                    ? excludedColor
                    : state.Included.Contains(column)
                        ? includedColor
                        : state.Final.Contains(column)
                            ? automaticColor
                            : unloadedColor;
                capi.Render.RenderRectangle(
                    gridX + columnIndex * cellSize + 0.7f,
                    gridY + row * cellSize + 0.7f,
                    100.2f,
                    Math.Max(1f, cellSize - 1.4f),
                    Math.Max(1f, cellSize - 1.4f),
                    color);
                if (selectingPreloadGrid
                    && column.X >= selectionMinX
                    && column.X <= selectionMaxX
                    && column.Z >= selectionMinZ
                    && column.Z <= selectionMaxZ)
                {
                    capi.Render.RenderRectangle(
                        gridX + columnIndex * cellSize + 1.5f,
                        gridY + row * cellSize + 1.5f,
                        100.4f,
                        Math.Max(1f, cellSize - 3f),
                        Math.Max(1f, cellSize - 3f),
                        ColorUtil.ToRgba(210, 247, 191, 65));
                }
            }
        }

        float centerX = gridX + (state.TargetChunkX - state.CenterChunkX + metrics.HalfX) * cellSize;
        float centerY = gridY + (state.TargetChunkZ - state.CenterChunkZ + metrics.HalfZ) * cellSize;
        int centerColor = ColorUtil.ToRgba(255, 247, 191, 65);
        if (centerX + cellSize >= gridX
            && centerX <= gridX + metrics.CellsX * cellSize
            && centerY + cellSize >= gridY
            && centerY <= gridY + metrics.CellsZ * cellSize)
        {
            capi.Render.RenderRectangle(centerX, centerY, 100.5f, cellSize, 1.5f, centerColor);
            capi.Render.RenderRectangle(centerX, centerY + cellSize - 1.5f, 100.5f, cellSize, 1.5f, centerColor);
            capi.Render.RenderRectangle(centerX, centerY, 100.5f, 1.5f, cellSize, centerColor);
            capi.Render.RenderRectangle(centerX + cellSize - 1.5f, centerY, 100.5f, 1.5f, cellSize, centerColor);
        }
    }

    private void BeginPreloadGridSelection(int mouseX, int mouseY)
    {
        if (!TryGetPreloadGridColumn(mouseX, mouseY, out int chunkX, out int chunkZ))
        {
            return;
        }
        selectingPreloadGrid = true;
        pendingPathEditHistory = section == DirectorWorkspaceSection.Paths
            ? pathEdit?.DeepClone()
            : null;
        preloadGridChanged = false;
        preloadSelectionStartX = preloadSelectionEndX = chunkX;
        preloadSelectionStartZ = preloadSelectionEndZ = chunkZ;
    }

    private void UpdatePreloadGridSelection(int mouseX, int mouseY)
    {
        if (TryGetPreloadGridColumn(mouseX, mouseY, out int chunkX, out int chunkZ))
        {
            preloadSelectionEndX = chunkX;
            preloadSelectionEndZ = chunkZ;
        }
    }

    private void ApplyPreloadGridSelection()
    {
        if (!TryGetPreloadGridState(out PreloadGridState state))
        {
            return;
        }

        int minX = Math.Min(preloadSelectionStartX, preloadSelectionEndX) - preloadBrushRadius;
        int maxX = Math.Max(preloadSelectionStartX, preloadSelectionEndX) + preloadBrushRadius;
        int minZ = Math.Min(preloadSelectionStartZ, preloadSelectionEndZ) - preloadBrushRadius;
        int maxZ = Math.Max(preloadSelectionStartZ, preloadSelectionEndZ) + preloadBrushRadius;
        bool changed = false;
        for (int chunkX = minX; chunkX <= maxX; chunkX++)
        {
            for (int chunkZ = minZ; chunkZ <= maxZ; chunkZ++)
            {
                DirectorReplayPreloadColumn column = new(chunkX, chunkZ, state.Dimension);
                if (column.X < 0 || column.Z < 0)
                {
                    continue;
                }

                if (preloadEraseMode)
                {
                    changed |= state.Included.Remove(column);
                    if (state.Automatic.Contains(column))
                    {
                        changed |= state.Excluded.Add(column);
                    }
                }
                else
                {
                    changed |= state.Excluded.Remove(column);
                    if (!state.Automatic.Contains(column))
                    {
                        changed |= state.Included.Add(column);
                    }
                }
            }
        }

        if (!changed)
        {
            return;
        }

        preloadGridChanged = true;
        if (!state.IsReplay && pathEdit?.Id == state.TargetId)
        {
            pathEdit.IncludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(state.Included);
            pathEdit.ExcludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(state.Excluded);
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
        }
    }

    private void BeginPreloadGridPan(int mouseX, int mouseY)
    {
        panningPreloadGrid = true;
        preloadPanStartMouseX = mouseX;
        preloadPanStartMouseY = mouseY;
        preloadPanStartChunkX = preloadPanChunkX;
        preloadPanStartChunkZ = preloadPanChunkZ;
    }

    private void UpdatePreloadGridPan(int mouseX, int mouseY)
    {
        if (preloadGridBounds is null)
        {
            return;
        }
        PreloadGridMetrics metrics = GetPreloadGridMetrics(preloadGridBounds);
        preloadPanChunkX = preloadPanStartChunkX
            + (int)Math.Round((preloadPanStartMouseX - mouseX) / metrics.CellSize);
        preloadPanChunkZ = preloadPanStartChunkZ
            + (int)Math.Round((preloadPanStartMouseY - mouseY) / metrics.CellSize);
    }

    private bool TryGetPreloadGridColumn(int mouseX, int mouseY, out int chunkX, out int chunkZ)
    {
        chunkX = 0;
        chunkZ = 0;
        if (preloadGridBounds is null || !TryGetPreloadGridState(out PreloadGridState state))
        {
            return false;
        }

        PreloadGridMetrics metrics = GetPreloadGridMetrics(preloadGridBounds);
        double localX = mouseX - preloadGridBounds.absX - metrics.OffsetX;
        double localY = mouseY - preloadGridBounds.absY - metrics.OffsetY;
        int cellX = Math.Clamp((int)Math.Floor(localX / metrics.CellSize), 0, metrics.CellsX - 1);
        int cellZ = Math.Clamp((int)Math.Floor(localY / metrics.CellSize), 0, metrics.CellsZ - 1);
        chunkX = state.CenterChunkX + cellX - metrics.HalfX;
        chunkZ = state.CenterChunkZ + cellZ - metrics.HalfZ;
        return true;
    }

    private PreloadGridMetrics GetPreloadGridMetrics(ElementBounds bounds)
    {
        int minimumCells = preloadGridRadius * 2 + 1;
        double cellSize = Math.Max(1d, Math.Min(
            bounds.OuterWidth / minimumCells,
            bounds.OuterHeight / minimumCells));
        int cellsX = Math.Max(minimumCells, (int)Math.Floor(bounds.OuterWidth / cellSize));
        int cellsZ = Math.Max(minimumCells, (int)Math.Floor(bounds.OuterHeight / cellSize));
        if (cellsX % 2 == 0)
        {
            cellsX--;
        }
        if (cellsZ % 2 == 0)
        {
            cellsZ--;
        }
        double gridWidth = cellsX * cellSize;
        double gridHeight = cellsZ * cellSize;
        return new PreloadGridMetrics(
            (bounds.OuterWidth - gridWidth) / 2d,
            (bounds.OuterHeight - gridHeight) / 2d,
            cellSize,
            cellsX,
            cellsZ,
            cellsX / 2,
            cellsZ / 2);
    }

    private void SavePreloadGridChanges()
    {
        if (section == DirectorWorkspaceSection.Paths && pathEdit is not null)
        {
            Guid pathId = pathEdit.Id;
            owner.TrySavePath(pathEdit, out _);
            pathEdit = owner.FindPath(pathId) ?? pathEdit;
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
        }
        else if (section == DirectorWorkspaceSection.Replay && preloadDraftReplayId != Guid.Empty)
        {
            owner.TrySaveReplayPreloadOverrides(
                preloadDraftReplayId,
                preloadDraftIncluded,
                preloadDraftExcluded,
                out _);
        }

        ComposeWorkflowCanvas();
    }

    private void RenderReplayTimeline(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        double time = scrubbingReplay ? previewReplayTime : owner.ReplayTimeSeconds;
        double labelWidth = ReplayTimelineLabelColumnWidth(bounds);
        double rulerHeight = GuiElement.scaled(ReplayTimelineRulerHeight);
        double headerHeight = GuiElement.scaled(ReplayTimelineHeaderHeight);
        double rowHeight = GuiElement.scaled(ReplayTimelineTrackHeight);
        double contentLeft = labelWidth;
        double contentWidth = Math.Max(
            GuiElement.scaled(1d),
            bounds.OuterWidth - contentLeft);
        double rulerTop = 0d;
        double trackTop = headerHeight;
        double trackBottom = bounds.OuterHeight;
        double timelineLineWidth = GuiElement.scaled(1d);
        double separatorLeft = contentLeft - timelineLineWidth * 0.5d;
        int separatorColor = ColorUtil.ToRgba(255, 255, 255, 255);
        const float timelineBackgroundZ = 20f;
        double pixelsPerTick = GuiElement.scaled(ReplayTimelinePixelsPerTick);
        int durationTicks = ReplayTimelineDurationTicks();
        double visibleTicks = contentWidth
            / DirectorTimelineCoordinateMath.PositivePixelsPerTick(pixelsPerTick);
        replayTimelineScrollTicks = Math.Clamp(
            replayTimelineScrollTicks,
            0d,
            Math.Max(0d, durationTicks - visibleTicks));
        double TimelineX(double seconds)
            => contentLeft
                + (seconds * ReplayTimelineTicksPerSecond - replayTimelineScrollTicks)
                * pixelsPerTick;

        RenderSolidWorkflowRect(bounds, 0d, 0d, bounds.OuterWidth, bounds.OuterHeight,
            timelineBackgroundZ, ColorUtil.ToRgba(232, 13, 18, 22));
        RenderSolidWorkflowRect(bounds, 0d, 0d, labelWidth, headerHeight,
            timelineBackgroundZ, ColorUtil.ToRgba(238, 24, 31, 38));
        RenderSolidWorkflowRect(bounds, contentLeft, 0d, contentWidth, headerHeight,
            timelineBackgroundZ, ColorUtil.ToRgba(238, 20, 27, 34));
        RenderSolidWorkflowRect(bounds, contentLeft, rulerTop, contentWidth, rulerHeight,
            timelineBackgroundZ, ColorUtil.ToRgba(226, 16, 23, 30));
        RenderSolidWorkflowRect(bounds, separatorLeft, 0d, timelineLineWidth,
            Math.Max(1d, bounds.OuterHeight), 100.25f, separatorColor);

        double rulerBaseline = rulerTop + rulerHeight - timelineLineWidth;
        RenderSolidWorkflowRect(bounds, 0d, rulerBaseline,
            bounds.OuterWidth,
            timelineLineWidth, 100.3f, separatorColor);

        (double exportStart, double exportEnd) = CurrentReplayExportRange();
        double exportLeft = Math.Clamp(TimelineX(exportStart), contentLeft,
            contentLeft + contentWidth);
        double exportRight = Math.Clamp(TimelineX(exportEnd), contentLeft,
            contentLeft + contentWidth);
        if (exportRight > exportLeft)
        {
            int exportFill = ColorUtil.ToRgba(92, 242, 142, 43);
            int exportEdge = ColorUtil.ToRgba(255, 255, 154, 55);
            double edgeWidth = GuiElement.scaled(3d);
            RenderSolidWorkflowRect(bounds, exportLeft, rulerTop,
                exportRight - exportLeft, rulerHeight - timelineLineWidth,
                100.31f, exportFill);
            RenderSolidWorkflowRect(bounds, exportLeft - edgeWidth * 0.5d, rulerTop,
                edgeWidth, rulerHeight - timelineLineWidth, 100.46f, exportEdge);
            RenderSolidWorkflowRect(bounds, exportRight - edgeWidth * 0.5d, rulerTop,
                edgeWidth, rulerHeight - timelineLineWidth, 100.46f, exportEdge);
        }
        int firstVisibleTick = Math.Max(0,
            (int)Math.Floor(replayTimelineScrollTicks / ReplayTimelineMinorTickStep)
                * ReplayTimelineMinorTickStep);
        int lastVisibleTick = Math.Min(
            durationTicks,
            (int)Math.Ceiling(replayTimelineScrollTicks + visibleTicks));
        for (int tick = firstVisibleTick; tick <= lastVisibleTick;
             tick += ReplayTimelineMinorTickStep)
        {
            double x = contentLeft + (tick - replayTimelineScrollTicks) * pixelsPerTick;
            bool major = tick % ReplayTimelineLabelTickStep == 0;
            double tickHeight = GuiElement.scaled(major ? 13d : 6d);
            RenderSolidWorkflowRect(bounds, x - timelineLineWidth * 0.5d,
                rulerBaseline - tickHeight,
                timelineLineWidth, tickHeight,
                major ? 100.4f : 100.35f,
                separatorColor);
        }
        IReadOnlyList<DirectorReplayElementTrack> elementTracks = ActiveReplayTimelineElementTracks();
        int visibleTrackCount = Math.Max(1, (int)Math.Floor(
            (trackBottom - trackTop) / Math.Max(1d, rowHeight)));
        replayTimelineFirstVisibleTrack = Math.Clamp(
            replayTimelineFirstVisibleTrack,
            0,
            Math.Max(0, elementTracks.Count + 1 - visibleTrackCount));
        for (int visibleTrackIndex = 0;
             visibleTrackIndex < visibleTrackCount;
             visibleTrackIndex++)
        {
            int trackIndex = replayTimelineFirstVisibleTrack + visibleTrackIndex;
            if (trackIndex > elementTracks.Count)
            {
                break;
            }
            double rowY = trackTop + visibleTrackIndex * rowHeight;
            if (rowY + rowHeight > trackBottom)
            {
                break;
            }

            bool addRow = trackIndex == elementTracks.Count;
            DirectorReplayElementTrack? track = addRow ? null : elementTracks[trackIndex];
            bool trackSelected = track is not null && IsReplayTimelineElementTrackSelected(track);
            int rowBackground = addRow
                ? ColorUtil.ToRgba(238, 16, 23, 30)
                : trackSelected
                    ? ColorUtil.ToRgba(64, 60, 53, 58)
                    : trackIndex % 2 == 0
                    ? ColorUtil.ToRgba(46, 22, 29, 35)
                    : ColorUtil.ToRgba(34, 20, 26, 32);
            RenderSolidWorkflowRect(bounds, 0d, rowY, labelWidth, rowHeight - timelineLineWidth,
                timelineBackgroundZ, rowBackground);
            if (!addRow)
            {
                RenderSolidWorkflowRect(bounds, contentLeft, rowY, contentWidth,
                    rowHeight - timelineLineWidth,
                    timelineBackgroundZ, rowBackground);
            }
            RenderSolidWorkflowRect(bounds, 0d, rowY + rowHeight - timelineLineWidth,
                addRow ? labelWidth : bounds.OuterWidth, timelineLineWidth, 100.34f,
                separatorColor);
            if (track is null)
            {
                continue;
            }

            ReplayTimelineElementKind kind = ReplayTimelineElementKindFor(track.TrackType);
            double centerY = rowY + rowHeight * 0.5d;
            double markerSize = rowHeight * 0.9d;
            timelineMarkerRenderer.RenderTrackIcon(
                track.TrackType,
                bounds.renderX + GuiElement.scaled(46d),
                bounds.renderY + centerY,
                rowHeight * 0.8d,
                100.52f,
                ColorUtil.ToRgba(255, 255, 255, 255));
            void RenderKeyframe(double keyframeTime, bool selected)
            {
                double markerX = TimelineX(keyframeTime);
                if (markerX < contentLeft || markerX > contentLeft + contentWidth)
                {
                    return;
                }
                timelineMarkerRenderer.Render(
                    bounds.renderX + markerX,
                    bounds.renderY + centerY,
                    markerSize,
                    100.55f,
                    selected
                        ? ColorUtil.ToRgba(255, 255, 213, 76)
                        : ColorUtil.ToRgba(255, 255, 255, 255));
            }

            if (kind == ReplayTimelineElementKind.Camera)
            {
                foreach (DirectorReplayViewportClip clip in CurrentReplayViewportTimeline().Clips
                    .Where(clip => clip.TrackId == track.Id
                        && clip.SourceType == DirectorReplayViewportSourceType.StaticCamera))
                {
                    RenderKeyframe(clip.TimelineStartSeconds,
                        clip.Id == selectedReplayViewportClipId);
                }
            }
            else if (kind == ReplayTimelineElementKind.Timelapse)
            {
                DirectorReplayViewportTimeline currentTimeline = CurrentReplayViewportTimeline();
                IReadOnlyList<DirectorReplayTimelapsePair> pairs =
                    DirectorReplayTimelapse.FindPairs(currentTimeline.ElementKeyframes, track.Id);
                HashSet<Guid> pairedKeyframes = new();
                foreach (DirectorReplayTimelapsePair pair in pairs)
                {
                    pairedKeyframes.Add(pair.Start.Id);
                    pairedKeyframes.Add(pair.End.Id);
                    double startTime = replayAuxiliaryKeyframeDragKind
                            == ReplayAuxiliaryKeyframeDragKind.Element
                        && pair.Start.Id == replayAuxiliaryKeyframeDragId
                            ? replayKeyframeDragTime
                            : pair.Start.TimeSeconds;
                    double endTime = replayAuxiliaryKeyframeDragKind
                            == ReplayAuxiliaryKeyframeDragKind.Element
                        && pair.End.Id == replayAuxiliaryKeyframeDragId
                            ? replayKeyframeDragTime
                            : pair.End.TimeSeconds;
                    double startX = TimelineX(startTime);
                    double endX = TimelineX(endTime);
                    double left = Math.Min(startX, endX);
                    double right = Math.Max(startX, endX);
                    double visibleLeft = Math.Max(contentLeft, left);
                    double visibleRight = Math.Min(contentLeft + contentWidth, right);
                    if (visibleRight > visibleLeft)
                    {
                        double dash = GuiElement.scaled(4d);
                        double gap = GuiElement.scaled(3d);
                        double line = Math.Max(GuiElement.scaled(1d), timelineLineWidth);
                        int connectorColor = pair.Start.Id == selectedReplayElementKeyframeId
                            ? ColorUtil.ToRgba(255, 255, 213, 155)
                            : ColorUtil.ToRgba(255, 255, 255, 125);
                        for (double x = visibleLeft; x < visibleRight; x += dash + gap)
                        {
                            RenderSolidWorkflowRect(bounds, x, centerY - line * 0.5d,
                                Math.Min(dash, visibleRight - x), line, 100.53f,
                                connectorColor);
                        }
                        double duration = pair.Start.TimelapseDurationSeconds > 0d
                            ? pair.Start.TimelapseDurationSeconds
                            : pair.End.TimelapseDurationSeconds > 0d
                                ? pair.End.TimelapseDurationSeconds
                                : track.TimelapseDurationSeconds;
                        if (duration <= 0d
                            && pair.End.TimelapseTick > pair.Start.TimelapseTick)
                        {
                            duration = (pair.End.TimelapseTick - pair.Start.TimelapseTick)
                                / ReplayTimelineTicksPerSecond;
                        }
                        if (duration <= 0d)
                        {
                            duration = Math.Max(0d, pair.End.TimeSeconds - pair.Start.TimeSeconds);
                        }
                        string label = string.Format(
                            CultureInfo.InvariantCulture,
                            "Time {0:0.##}s",
                            Math.Max(0d, duration));
                        // Keep the annotation readable even when the two
                        // keyframes are close together on a zoomed timeline.
                        double timelapseLabelWidth = GuiElement.scaled(156d);
                        timelineMarkerRenderer.RenderTimelapseLabel(
                            label,
                            (startX + endX) * 0.5d + bounds.renderX,
                            bounds.renderY + centerY,
                            timelapseLabelWidth,
                            GuiElement.scaled(26d),
                            100.54f,
                            ColorUtil.ToRgba(255, 255, 255, 255));
                    }
                    if (startX >= contentLeft - markerSize
                        && startX <= contentLeft + contentWidth + markerSize)
                    {
                        timelineMarkerRenderer.RenderTimelapseKeyframe(
                            DirectorReplayTimelapseKeyframeRole.Start,
                            bounds.renderX + startX,
                            bounds.renderY + centerY,
                            markerSize,
                            100.55f,
                            pair.Start.Id == selectedReplayElementKeyframeId
                                ? ColorUtil.ToRgba(255, 255, 213, 76)
                                : ColorUtil.ToRgba(255, 255, 255, 255));
                    }
                    if (endX >= contentLeft - markerSize
                        && endX <= contentLeft + contentWidth + markerSize)
                    {
                        timelineMarkerRenderer.RenderTimelapseKeyframe(
                            DirectorReplayTimelapseKeyframeRole.End,
                            bounds.renderX + endX,
                            bounds.renderY + centerY,
                            markerSize,
                            100.56f,
                            pair.End.Id == selectedReplayElementKeyframeId
                                ? ColorUtil.ToRgba(255, 255, 213, 76)
                                : ColorUtil.ToRgba(255, 255, 255, 255));
                    }
                }
                foreach (DirectorReplayElementKeyframe keyframe in currentTimeline.ElementKeyframes
                    .Where(item => item.TrackId == track.Id
                        && !pairedKeyframes.Contains(item.Id)))
                {
                    double keyframeTime = replayAuxiliaryKeyframeDragKind
                            == ReplayAuxiliaryKeyframeDragKind.Element
                        && keyframe.Id == replayAuxiliaryKeyframeDragId
                            ? replayKeyframeDragTime
                        : keyframe.TimeSeconds;
                    double keyframeX = TimelineX(keyframeTime);
                    if (keyframeX >= contentLeft - markerSize
                        && keyframeX <= contentLeft + contentWidth + markerSize)
                    {
                        timelineMarkerRenderer.RenderTimelapseKeyframe(
                            keyframe.TimelapseRole,
                            bounds.renderX + keyframeX,
                            bounds.renderY + centerY,
                            markerSize,
                            100.55f,
                            keyframe.Id == selectedReplayElementKeyframeId
                                ? ColorUtil.ToRgba(255, 255, 213, 76)
                                : ColorUtil.ToRgba(255, 255, 255, 255));
                    }
                }
            }
            else if (track.TrackType == DirectorReplayElementTrackType.FollowEntity)
            {
                foreach (DirectorReplayElementKeyframe keyframe in CurrentReplayViewportTimeline()
                    .ElementKeyframes.Where(item => item.TrackId == track.Id))
                {
                    double keyframeTime = replayAuxiliaryKeyframeDragKind
                            == ReplayAuxiliaryKeyframeDragKind.Element
                        && keyframe.Id == replayAuxiliaryKeyframeDragId
                            ? replayKeyframeDragTime
                            : keyframe.TimeSeconds;
                    double keyframeX = TimelineX(keyframeTime);
                    if (keyframeX >= contentLeft - markerSize
                        && keyframeX <= contentLeft + contentWidth + markerSize)
                    {
                        timelineMarkerRenderer.RenderFollowEntityKeyframe(
                            keyframe.FollowEntityRole,
                            bounds.renderX + keyframeX,
                            bounds.renderY + centerY,
                            markerSize,
                            100.55f,
                            keyframe.Id == selectedReplayElementKeyframeId
                                ? ColorUtil.ToRgba(255, 255, 213, 76)
                                : ColorUtil.ToRgba(255, 255, 255, 255));
                    }
                }
            }
            else
            {
                foreach (DirectorReplayElementKeyframe keyframe in CurrentReplayViewportTimeline()
                    .ElementKeyframes.Where(item => item.TrackId == track.Id))
                {
                    double keyframeTime = replayAuxiliaryKeyframeDragKind
                            == ReplayAuxiliaryKeyframeDragKind.Element
                        && keyframe.Id == replayAuxiliaryKeyframeDragId
                            ? replayKeyframeDragTime
                            : keyframe.TimeSeconds;
                    RenderKeyframe(keyframeTime,
                        keyframe.Id == selectedReplayElementKeyframeId);
                }
            }
        }
        if (replayElementTrackReordering && replayElementTrackReorderIndex >= 0)
        {
            int visibleIndex = replayElementTrackReorderIndex - replayTimelineFirstVisibleTrack;
            if (visibleIndex >= 0 && visibleIndex <= visibleTrackCount)
            {
                double lineY = trackTop + visibleIndex * rowHeight;
                RenderSolidWorkflowRect(bounds, 0d, lineY - GuiElement.scaled(1d),
                    bounds.OuterWidth, GuiElement.scaled(2d), 100.92f,
                    ColorUtil.ToRgba(255, 255, 213, 76));
            }
        }
        double playheadX = TimelineX(time);
        if (playheadX >= contentLeft && playheadX <= contentLeft + contentWidth)
        {
            RenderSolidWorkflowRect(bounds, playheadX - timelineLineWidth * 0.5d,
                rulerBaseline,
                timelineLineWidth, Math.Max(GuiElement.scaled(12d),
                    bounds.OuterHeight - rulerBaseline - GuiElement.scaled(3d)),
                100.8f, ColorUtil.ToRgba(255, 255, 255, 255));
            timelineMarkerRenderer.RenderPlayhead(
                bounds.renderX + playheadX,
                bounds.renderY + rulerBaseline - GuiElement.scaled(5d),
                GuiElement.scaled(22d),
                100.86f,
                ColorUtil.ToRgba(255, 255, 255, 255));
        }
    }

    private static double ReplayTimelineLabelColumnWidth(double availableWidth)
    {
        double preferred = ReplayTimelineLabelColumnExpansion + Math.Clamp(
            availableWidth * 0.34d,
            ReplayTimelineLabelColumnBaseMinimum,
            ReplayTimelineLabelColumnBaseMaximum);
        double maximumWithTimelineVisible = Math.Max(120d, availableWidth - 180d);
        return Math.Min(preferred, maximumWithTimelineVisible);
    }

    private static double ReplayTimelineLabelColumnWidth(ElementBounds bounds)
        => GuiElement.scaled(ReplayTimelineLabelColumnWidth(
            bounds.OuterWidth / Math.Max(0.001d, GuiElement.scaled(1d))));

    private IReadOnlyList<ReplayAuxiliaryTimelineKind> ActiveReplayAuxiliaryTimelines()
    {
        List<ReplayAuxiliaryTimelineKind> timelines = new(2);
        if (replayViewMode == "sequence" && sequenceEdit is not null)
        {
            timelines.Add(ReplayAuxiliaryTimelineKind.Sequence);
        }
        if (replayViewMode == "path" && pathEdit is not null)
        {
            timelines.Add(ReplayAuxiliaryTimelineKind.Path);
        }
        return timelines;
    }

    private void RenderReplaySequenceTimeline(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        RenderReplayAuxiliaryBase(bounds, ColorUtil.ToRgba(255, 66, 154, 201));
        CameraSequence? sequence = sequenceEdit;
        if (sequence is null || sequence.Shots.Count == 0)
        {
            return;
        }

        double duration = Math.Max(CameraSequenceSampler.MinShotDurationSeconds, sequence.DurationSeconds);
        double left = GuiElement.scaled(16d);
        double width = Math.Max(1d, bounds.OuterWidth - left * 2d);
        double elapsed = Math.Max(0d, owner.ReplayTimeSeconds - replayViewStartSeconds);
        int activeShot = CameraSequenceSampler.TryResolve(sequence, elapsed, out CameraSequenceTime sequenceTime)
            ? sequenceTime.ShotIndex
            : -1;
        double start = 0d;
        for (int index = 0; index < sequence.Shots.Count; index++)
        {
            CameraSequenceShot shot = sequence.Shots[index];
            double x = left + width * Math.Clamp(start / duration, 0d, 1d);
            double shotWidth = Math.Max(
                GuiElement.scaled(3d),
                width * Math.Clamp(shot.DurationSeconds / duration, 0d, 1d));
            int color = index == activeShot
                ? ColorUtil.ToRgba(235, 74, 207, 139)
                : index % 2 == 0
                    ? ColorUtil.ToRgba(205, 47, 118, 157)
                    : ColorUtil.ToRgba(205, 44, 94, 132);
            RenderSolidWorkflowRect(bounds, x, GuiElement.scaled(4d), shotWidth - GuiElement.scaled(1d),
                Math.Max(GuiElement.scaled(8d), bounds.OuterHeight - GuiElement.scaled(8d)),
                100.4f, color);
            start += shot.DurationSeconds;
        }
    }

    private void RenderReplayPathViewTimeline(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        RenderReplayAuxiliaryBase(bounds, ColorUtil.ToRgba(255, 205, 133, 66));
        CameraPath? path = pathEdit;
        if (path is null || path.Keyframes.Count == 0)
        {
            return;
        }
        double duration = Math.Max(0.001d, path.DurationSeconds);
        double left = GuiElement.scaled(16d);
        double width = Math.Max(1d, bounds.OuterWidth - left * 2d);
        foreach ((CameraKeyframe frame, int index) in path.Keyframes.Select((frame, index) => (frame, index)))
        {
            double x = left + width * Math.Clamp(frame.TimeSeconds / duration, 0d, 1d);
            bool selected = selectedPathKeyframes.Contains(index);
            RenderSolidWorkflowRect(bounds, x - GuiElement.scaled(selected ? 4d : 3d), GuiElement.scaled(5d),
                GuiElement.scaled(selected ? 8d : 6d),
                Math.Max(GuiElement.scaled(8d), bounds.OuterHeight - GuiElement.scaled(10d)),
                100.5f,
                selected
                    ? ColorUtil.ToRgba(255, 94, 232, 151)
                    : ColorUtil.ToRgba(255, 236, 166, 83));
        }
    }

    private void RenderReplayAuxiliaryBase(ElementBounds bounds, int accent)
    {
        RenderWorkflowRect(bounds, 0d, 0d, bounds.OuterWidth, bounds.OuterHeight, 100f,
            ColorUtil.ToRgba(232, 13, 18, 22));
        RenderWorkflowRect(bounds, 0d, 0d, GuiElement.scaled(5d), bounds.OuterHeight, 100.2f, accent);
        RenderWorkflowRect(bounds, 0d, bounds.OuterHeight - GuiElement.scaled(1d),
            bounds.OuterWidth, GuiElement.scaled(1d), 100.2f,
            ColorUtil.ToRgba(115, 85, 117, 124));
    }

    private void ComposeReplayTimelineLinks(
        Cairo.Context context,
        Cairo.ImageSurface surface,
        ElementBounds bounds)
    {
        _ = surface;
        _ = bounds;
        if (replayTimelineBounds is null)
        {
            return;
        }

        replayTimelineBounds.CalcWorldBounds();
        replaySequenceTimelineBounds?.CalcWorldBounds();
        replayPathViewTimelineBounds?.CalcWorldBounds();
        context.Save();
        context.Antialias = Cairo.Antialias.Best;
        context.LineWidth = Math.Max(2d, GuiElement.scaled(2.25d));
        context.LineCap = Cairo.LineCap.Round;
        context.LineJoin = Cairo.LineJoin.Round;

        double replayDuration = Math.Max(0.001d, owner.ReplayDurationSeconds);
        if (replaySequenceTimelineBounds is not null && sequenceEdit is not null)
        {
            double sourceX = ReplayTimelineInnerDrawX(replaySequenceTimelineBounds, 0d);
            double sourceY = replaySequenceTimelineBounds.drawY
                + replaySequenceTimelineBounds.OuterHeight - GuiElement.scaled(2d);
            double targetX = ReplayTimelineInnerDrawX(
                replayTimelineBounds,
                Math.Clamp(replayViewStartSeconds / replayDuration, 0d, 1d));
            double targetY = replayTimelineBounds.drawY + GuiElement.scaled(3d);
            DrawReplayTimelineLink(
                context,
                sourceX,
                sourceY,
                targetX,
                targetY,
                83d / 255d,
                181d / 255d,
                224d / 255d,
                215d / 255d);
        }

        if (replayPathViewTimelineBounds is not null && pathEdit is not null)
        {
            double pathDuration = Math.Max(0.001d, pathEdit.DurationSeconds);
            foreach (CameraKeyframe frame in pathEdit.Keyframes)
            {
                double sourceX = ReplayTimelineInnerDrawX(
                    replayPathViewTimelineBounds,
                    Math.Clamp(frame.TimeSeconds / pathDuration, 0d, 1d));
                double sourceY = replayPathViewTimelineBounds.drawY
                    + replayPathViewTimelineBounds.OuterHeight - GuiElement.scaled(2d);
                double targetX = ReplayTimelineInnerDrawX(
                    replayTimelineBounds,
                    Math.Clamp(ReplayTimeForPathKeyframe(pathEdit, frame) / replayDuration, 0d, 1d));
                double targetY = replayTimelineBounds.drawY + GuiElement.scaled(3d);
                DrawReplayTimelineLink(
                    context,
                    sourceX,
                    sourceY,
                    targetX,
                    targetY,
                    237d / 255d,
                    174d / 255d,
                    92d / 255d,
                    175d / 255d);
            }
        }

        context.Restore();
    }

    private void AddReplaySequenceTimelineLabels(
        GuiComposer composer,
        CairoFont detail,
        ElementBounds bounds)
    {
        CameraSequence? sequence = sequenceEdit;
        if (sequence is null || sequence.Shots.Count == 0)
        {
            return;
        }
        double duration = Math.Max(CameraSequenceSampler.MinShotDurationSeconds, sequence.DurationSeconds);
        double innerLeft = 26d;
        double innerWidth = Math.Max(1d, layout.Panel("timeline").Width - 52d);
        double start = 0d;
        for (int index = 0; index < sequence.Shots.Count; index++)
        {
            CameraSequenceShot shot = sequence.Shots[index];
            double x = innerLeft + innerWidth * start / duration;
            double width = innerWidth * shot.DurationSeconds / duration;
            if (width >= 44d)
            {
                string cameraName = owner.FindCamera(shot.CameraId)?.Name
                    ?? Lang.Get("vsdirector:missing-camera");
                composer.AddDirectorSingleLineText(
                    Ellipsize($"{index + 1} {cameraName}", Math.Max(4, (int)(width / 8d))),
                    detail,
                    ElementBounds.Fixed(x, bounds.fixedY + 7d, Math.Max(10d, width - 4d), 18d));
            }
            start += shot.DurationSeconds;
        }
    }

    private void AddReplayPathTimelineLabels(
        GuiComposer composer,
        CairoFont detail,
        ElementBounds bounds)
    {
        CameraPath? path = pathEdit;
        if (path is null || path.Keyframes.Count == 0)
        {
            return;
        }
        double duration = Math.Max(0.001d, path.DurationSeconds);
        double innerLeft = 26d;
        double innerWidth = Math.Max(1d, layout.Panel("timeline").Width - 52d);
        for (int index = 0; index < path.Keyframes.Count; index++)
        {
            double x = innerLeft + innerWidth * path.Keyframes[index].TimeSeconds / duration;
            composer.AddDirectorSingleLineText(
                (index + 1).ToString(CultureInfo.InvariantCulture),
                detail,
                ElementBounds.Fixed(x + 4d, bounds.fixedY + 7d, 24d, 18d));
        }
    }

    private static double ReplayTimelineInnerDrawX(ElementBounds bounds, double fraction)
    {
        double left = GuiElement.scaled(16d);
        double width = Math.Max(1d, bounds.OuterWidth - left * 2d);
        return bounds.drawX + left + width * Math.Clamp(fraction, 0d, 1d);
    }

    private static void DrawReplayTimelineLink(
        Cairo.Context context,
        double sourceX,
        double sourceY,
        double targetX,
        double targetY,
        double red,
        double green,
        double blue,
        double alpha)
    {
        context.SetSourceRGBA(red, green, blue, alpha);
        context.MoveTo(sourceX, sourceY);
        context.LineTo(targetX, targetY);
        context.Stroke();
    }

    private double ReplayTimeForPathKeyframe(CameraPath path, CameraKeyframe frame)
    {
        double replayTime = DirectorReplayPathTimeline.ResolveReplayTime(
            path,
            owner.ActiveReplayId,
            frame.TimeSeconds);
        return Math.Clamp(replayTime, 0d, Math.Max(0d, owner.ReplayDurationSeconds));
    }

    private double ReplayViewTimelineStartSeconds(CameraPath? path = null)
    {
        if (replayViewMode != "path")
        {
            return owner.ReplayTimeSeconds;
        }

        path ??= replayViewSourceId == Guid.Empty ? null : owner.FindPath(replayViewSourceId);
        return path is null
            ? 0d
            : DirectorReplayPathTimeline.StartTimeSeconds(path, owner.ActiveReplayId);
    }

    private void RenderFollowRigPreview(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        CameraFollowRig? rig = followEdit;
        float x = (float)bounds.renderX;
        float y = (float)bounds.renderY;
        float width = (float)bounds.OuterWidth;
        float height = (float)bounds.OuterHeight;
        capi.Render.RenderRectangle(x, y, 100f, width, height,
            ColorUtil.ToRgba(238, 14, 20, 23));
        capi.Render.RenderRectangle(x, y, 100.1f, width, 1f,
            ColorUtil.ToRgba(150, 73, 102, 108));
        capi.Render.RenderRectangle(x, y + height - 1f, 100.1f, width, 1f,
            ColorUtil.ToRgba(150, 73, 102, 108));
        if (rig is null)
        {
            return;
        }

        float targetX = x + width * 0.5f;
        float targetY = y + height * 0.52f;
        float radius = (float)GuiElement.scaled(
            18d + Math.Clamp(rig.Distance, 0d, 30d) / 30d * 22d);
        double angle = (rig.OrbitAngleDegrees - 90d) * Math.PI / 180d;
        float cameraX = targetX + (float)Math.Cos(angle) * radius;
        float cameraY = targetY + (float)Math.Sin(angle) * radius;
        int guide = ColorUtil.ToRgba(180, 77, 112, 118);
        int accent = ColorUtil.ToRgba(255, 46, 174, 179);
        capi.Render.RenderRectangle(targetX - radius, targetY, 100.2f, radius * 2f, 1f, guide);
        capi.Render.RenderRectangle(targetX, targetY - radius, 100.2f, 1f, radius * 2f, guide);
        DrawPreviewLine(targetX, targetY, cameraX, cameraY, 100.3f, accent);
        capi.Render.RenderRectangle(targetX - 5f, targetY - 5f, 100.4f, 10f, 10f,
            ColorUtil.ToRgba(255, 238, 198, 84));
        capi.Render.RenderRectangle(cameraX - 7f, cameraY - 4f, 100.5f, 14f, 8f, accent);
        float heightBar = (float)Math.Clamp(rig.Height, -10d, 10d) / 10f * (height * 0.36f);
        capi.Render.RenderRectangle(x + width - 14f, targetY - heightBar, 100.4f, 3f,
            Math.Max(2f, Math.Abs(heightBar)), ColorUtil.ToRgba(255, 225, 102, 112));
    }

    private void DrawPreviewLine(float x1, float y1, float x2, float y2, float z, int color)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / 3f));
        for (int step = 0; step <= steps; step++)
        {
            float amount = (float)step / steps;
            capi.Render.RenderRectangle(x1 + dx * amount, y1 + dy * amount, z, 2f, 2f, color);
        }
    }

    private void RenderActionTimeline(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        float originX = (float)bounds.renderX;
        float originY = (float)bounds.renderY;
        float width = (float)bounds.OuterWidth;
        float height = (float)bounds.OuterHeight;
        capi.Render.RenderRectangle(originX, originY, 100f, width, height,
            ColorUtil.ToRgba(238, 13, 18, 22));
        if (actionEdit is null)
        {
            return;
        }

        double labelWidth = Math.Min(GuiElement.scaled(130d), bounds.OuterWidth * 0.24d);
        double rulerHeight = GuiElement.scaled(28d);
        double timelineWidth = Math.Max(1d, bounds.OuterWidth - labelWidth);
        double visibleDuration = ActionTimelineVisibleDuration;
        double viewStart = actionTimelinePanSeconds;
        double viewEnd = viewStart + visibleDuration;
        int layerCount = Math.Max(1, actionEdit.Layers.Count);
        double laneHeight = Math.Max(GuiElement.scaled(34d), (bounds.OuterHeight - rulerHeight) / layerCount);

        capi.Render.RenderRectangle(originX, originY, 100.1f, (float)labelWidth, height,
            ColorUtil.ToRgba(230, 23, 30, 36));
        capi.Render.RenderRectangle(originX + (float)labelWidth, originY, 100.1f,
            (float)timelineWidth, (float)rulerHeight, ColorUtil.ToRgba(235, 20, 27, 32));

        double gridStep = visibleDuration <= 5d ? 0.25d : visibleDuration <= 15d ? 1d : 5d;
        double firstGrid = Math.Ceiling(viewStart / gridStep) * gridStep;
        for (double time = firstGrid; time <= viewEnd + DirectorActionNormalizer.TimeEpsilon; time += gridStep)
        {
            double x = labelWidth + (time - viewStart) / visibleDuration * timelineWidth;
            int color = Math.Abs(time - Math.Round(time)) <= 0.0001d
                ? ColorUtil.ToRgba(95, 128, 151, 163)
                : ColorUtil.ToRgba(45, 128, 151, 163);
            RenderWorkflowRect(bounds, x, 0d, GuiElement.scaled(1d), bounds.OuterHeight, 100.2f, color);
        }

        for (int layerIndex = 0; layerIndex < actionEdit.Layers.Count; layerIndex++)
        {
            DirectorActionLayer layer = actionEdit.Layers[layerIndex];
            double laneY = rulerHeight + layerIndex * laneHeight;
            bool selected = layer.Id == selectedActionLayerId;
            RenderWorkflowRect(bounds, 0d, laneY, labelWidth, laneHeight - 1d, 100.25f,
                selected ? ColorUtil.ToRgba(210, 36, 79, 70) : ColorUtil.ToRgba(160, 30, 38, 44));
            RenderWorkflowRect(bounds, labelWidth, laneY, timelineWidth, laneHeight - 1d, 100.2f,
                layerIndex % 2 == 0
                    ? ColorUtil.ToRgba(88, 29, 37, 42)
                    : ColorUtil.ToRgba(88, 23, 31, 36));
            int accent = selected
                ? ColorUtil.ToRgba(255, 74, 207, 139)
                : ActionLayerColor(layer.Kind);
            IReadOnlyList<DirectorActionFrame> frames = layer.Frames;
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                DirectorActionFrame frame = frames[frameIndex];
                if (frame.TimeSeconds < viewStart - DirectorActionNormalizer.TimeEpsilon
                    || frame.TimeSeconds > viewEnd + DirectorActionNormalizer.TimeEpsilon)
                {
                    continue;
                }
                double x = labelWidth + (frame.TimeSeconds - viewStart) / visibleDuration * timelineWidth;
                double nextTime = frameIndex + 1 < frames.Count
                    ? frames[frameIndex + 1].TimeSeconds
                    : frame.TimeSeconds;
                double nextX = labelWidth + (Math.Min(viewEnd, nextTime) - viewStart) / visibleDuration * timelineWidth;
                if (nextX > x + GuiElement.scaled(3d))
                {
                    RenderSolidWorkflowRect(bounds, x, laneY + laneHeight * 0.34d,
                        nextX - x, laneHeight * 0.32d, 100.35f,
                         selected ? ColorUtil.ToRgba(145, 74, 207, 139) : ColorUtil.ToRgba(115, 76, 151, 178));
                }
                bool frameSelected = selectedActionFrames.Contains(
                    new ActionFrameSelection(layer.Id, frame.TimeSeconds));
                RenderSolidWorkflowRect(bounds, x - GuiElement.scaled(3d), laneY + laneHeight * 0.25d,
                    GuiElement.scaled(frameSelected ? 8d : 6d), laneHeight * 0.5d, 100.5f,
                    frameSelected ? ColorUtil.ToRgba(255, 94, 232, 151) : accent);
            }
        }

        foreach (DirectorActionEvent actionEvent in actionEdit.Events)
        {
            if (actionEvent.TimeSeconds < viewStart || actionEvent.TimeSeconds > viewEnd)
            {
                continue;
            }
            double x = labelWidth + (actionEvent.TimeSeconds - viewStart) / visibleDuration * timelineWidth;
            RenderSolidWorkflowRect(bounds, x - GuiElement.scaled(2d), rulerHeight - GuiElement.scaled(8d),
                GuiElement.scaled(4d), GuiElement.scaled(8d), 100.7f,
                ColorUtil.ToRgba(255, 238, 184, 75));
        }

        if (actionPlayhead >= viewStart && actionPlayhead <= viewEnd)
        {
            double x = labelWidth + (actionPlayhead - viewStart) / visibleDuration * timelineWidth;
            RenderWorkflowRect(bounds, x - GuiElement.scaled(1d), 0d, GuiElement.scaled(2d),
                bounds.OuterHeight, 100.9f, ColorUtil.ToRgba(255, 239, 92, 89));
        }
    }

    private static int ActionLayerColor(DirectorActionLayerKind kind)
        => kind switch
        {
            DirectorActionLayerKind.UpperBody => ColorUtil.ToRgba(255, 73, 174, 214),
            DirectorActionLayerKind.LowerBody => ColorUtil.ToRgba(255, 226, 151, 71),
            DirectorActionLayerKind.Head => ColorUtil.ToRgba(255, 197, 112, 211),
            DirectorActionLayerKind.Hands => ColorUtil.ToRgba(255, 224, 105, 131),
            DirectorActionLayerKind.Expression => ColorUtil.ToRgba(255, 106, 194, 142),
            DirectorActionLayerKind.Additive => ColorUtil.ToRgba(255, 151, 140, 222),
            _ => ColorUtil.ToRgba(255, 83, 154, 186)
        };

    private void RenderPathTimeline(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        CameraPath? path = pathEdit;
        double duration = Math.Max(1d, path?.DurationSeconds ?? 1d);
        double displayTime = owner.IsPathPlaying
            ? owner.ActivePathTimeSeconds
            : pathEditorPlayhead;
        RenderTimelineBase(bounds, displayTime / duration,
            ColorUtil.ToRgba(255, 200, 84, 218));
        if (path is null)
        {
            return;
        }

        float originX = (float)bounds.renderX;
        float originY = (float)bounds.renderY;
        double left = GuiElement.scaled(16d);
        double width = Math.Max(1d, bounds.OuterWidth - left * 2d);
        for (int index = 0; index + 1 < path.Keyframes.Count; index++)
        {
            VSDirector.Data.CameraKeyframe from = path.Keyframes[index];
            VSDirector.Data.CameraKeyframe to = path.Keyframes[index + 1];
            double fromX = left + width * Math.Clamp(from.TimeSeconds / duration, 0d, 1d);
            double toX = left + width * Math.Clamp(to.TimeSeconds / duration, 0d, 1d);
            CameraPathInterpolation interpolation = from.InterpolationToNext ?? path.Interpolation;
            int segmentColor = interpolation switch
            {
                CameraPathInterpolation.Linear => ColorUtil.ToRgba(235, 72, 218, 224),
                CameraPathInterpolation.Bezier => ColorUtil.ToRgba(235, 226, 105, 214),
                _ => ColorUtil.ToRgba(235, 245, 174, 66)
            };
            solidTextureRenderer.Render(
                originX + fromX,
                originY + bounds.OuterHeight / 2d - 3d,
                Math.Max(1f, (float)(toX - fromX)),
                6d,
                101f,
                segmentColor);
        }

        for (int index = 0; index < path.Keyframes.Count; index++)
        {
            double x = left + width * Math.Clamp(path.Keyframes[index].TimeSeconds / duration, 0d, 1d);
            int color = selectedPathKeyframes.Contains(index)
                ? ColorUtil.ToRgba(255, 250, 190, 45)
                : ColorUtil.ToRgba(255, 72, 218, 224);
            solidTextureRenderer.Render(originX + x - 5d, originY + bounds.OuterHeight / 2d - 5d,
                10d, 10d, 102f, color);
        }
    }

    private void RenderPathGlobalTimeline(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        float originX = (float)bounds.renderX;
        float originY = (float)bounds.renderY;
        float width = (float)bounds.OuterWidth;
        float height = (float)bounds.OuterHeight;
        capi.Render.RenderRectangle(originX, originY, 100f, width, height,
            ColorUtil.ToRgba(242, 14, 19, 23));
        if (pathEdit is null)
        {
            return;
        }

        double left = GuiElement.scaled(12d);
        double trackWidth = Math.Max(1d, bounds.OuterWidth - left * 2d);
        double globalDuration = PathGlobalDuration;
        double pathEnd = Math.Clamp(pathEdit.DurationSeconds / globalDuration, 0d, 1d);
        solidTextureRenderer.Render(
            originX + left,
            originY + GuiElement.scaled(18d),
            Math.Max(2f, (float)(trackWidth * pathEnd)),
            GuiElement.scaled(12d),
            100.2f,
            ColorUtil.ToRgba(205, 57, 136, 151));

        double viewStart = PathEditorViewStart / globalDuration;
        double viewEnd = (PathEditorViewStart + PathEditorViewDuration) / globalDuration;
        float viewX = originX + (float)(left + trackWidth * Math.Clamp(viewStart, 0d, 1d));
        float viewWidth = Math.Max(2f,
            (float)(trackWidth * Math.Clamp(viewEnd - viewStart, 0d, 1d)));
        solidTextureRenderer.Render(viewX, originY + 5f, viewWidth, height - 10f, 100.35f,
            ColorUtil.ToRgba(112, 246, 190, 72));
        solidTextureRenderer.Render(viewX, originY + 5f, viewWidth, 2f, 100.4f,
            ColorUtil.ToRgba(255, 246, 190, 72));
        solidTextureRenderer.Render(viewX, originY + height - 7f, viewWidth, 2f, 100.4f,
            ColorUtil.ToRgba(255, 246, 190, 72));
        solidTextureRenderer.Render(viewX, originY + 5f, 2f, height - 10f, 100.4f,
            ColorUtil.ToRgba(255, 246, 190, 72));
        solidTextureRenderer.Render(viewX + viewWidth - 2f, originY + 5f, 2f, height - 10f, 100.4f,
            ColorUtil.ToRgba(255, 246, 190, 72));

        float playheadX = originX + (float)(left + trackWidth
            * Math.Clamp(pathEditorPlayhead / globalDuration, 0d, 1d));
        capi.Render.RenderRectangle(playheadX, originY, 100.7f, 2f, height,
            ColorUtil.ToRgba(245, 92, 203, 224));
    }

    private void RenderPathEditorTimeline(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        float originX = (float)bounds.renderX;
        float originY = (float)bounds.renderY;
        float width = (float)bounds.OuterWidth;
        float height = (float)bounds.OuterHeight;
        capi.Render.RenderRectangle(originX, originY, 100f, width, height,
            ColorUtil.ToRgba(246, 13, 18, 22));
        CameraPath? path = pathEdit;
        if (path is null)
        {
            return;
        }

        double left = GuiElement.scaled(18d);
        double right = Math.Max(left + 1d, bounds.OuterWidth - left);
        double curveTop = GuiElement.scaled(12d);
        double laneHeight = GuiElement.scaled(25d);
        double curveBottom = Math.Max(curveTop + 1d, bounds.OuterHeight - laneHeight - GuiElement.scaled(8d));
        double viewStart = PathEditorViewStart;
        double viewDuration = PathEditorViewDuration;
        (double minimum, double maximum) = PathCurveValueRange();
        int[] selectedIndices = selectedPathKeyframes
            .Where(index => index >= 0 && index < path.Keyframes.Count)
            .ToArray();
        bool hasSelectedRange = selectedIndices.Length > 1;
        double selectedRangeStart = hasSelectedRange
            ? selectedIndices.Min(index => path.Keyframes[index].TimeSeconds)
            : 0d;
        double selectedRangeEnd = hasSelectedRange
            ? selectedIndices.Max(index => path.Keyframes[index].TimeSeconds)
            : 0d;
        (double Start, double End)[] selectedSegments = Enumerable.Range(
                0,
                Math.Max(0, path.Keyframes.Count - 1))
            .Where(index => selectedPathKeyframes.Contains(index)
                && selectedPathKeyframes.Contains(index + 1))
            .Select(index => (
                Start: path.Keyframes[index].TimeSeconds,
                End: path.Keyframes[index + 1].TimeSeconds))
            .ToArray();

        for (int band = 0; band < 4; band++)
        {
            double bandY = curveTop + (curveBottom - curveTop) * band / 4d;
            double bandBottom = curveTop + (curveBottom - curveTop) * (band + 1) / 4d;
            capi.Render.RenderRectangle(
                originX + (float)left,
                originY + (float)bandY,
                100.1f,
                (float)(right - left),
                (float)Math.Max(1d, bandBottom - bandY - 1d),
                ColorUtil.ToRgba(band % 2 == 0 ? 30 : 18, 68, 82, 89));
        }

        for (int tick = 0; tick <= 10; tick++)
        {
            double x = left + (right - left) * tick / 10d;
            capi.Render.RenderRectangle(originX + (float)x, originY + (float)curveTop,
                100.2f, 1f, (float)(bounds.OuterHeight - curveTop),
                ColorUtil.ToRgba(tick % 5 == 0 ? 84 : 34, 134, 151, 158));
        }

        float selectedRangeX = 0f;
        float selectedRangeY = 0f;
        float selectedRangeWidth = 0f;
        float selectedRangeHeight = 0f;
        if (hasSelectedRange
            && selectedRangeEnd >= viewStart
            && selectedRangeStart <= viewStart + viewDuration)
        {
            double clippedStart = Math.Max(selectedRangeStart, viewStart);
            double clippedEnd = Math.Min(selectedRangeEnd, viewStart + viewDuration);
            selectedRangeX = originX + (float)PathEditorTimeToLocalX(clippedStart, bounds);
            selectedRangeWidth = Math.Max(2f,
                (float)(PathEditorTimeToLocalX(clippedEnd, bounds)
                    - PathEditorTimeToLocalX(clippedStart, bounds)));
            double selectedMarkerTop = selectedIndices.Min(index => PathCurveValueToY(
                PathCurveValue(path.Keyframes[index]), minimum, maximum, curveTop, curveBottom));
            double selectedMarkerBottom = selectedIndices.Max(index => PathCurveValueToY(
                PathCurveValue(path.Keyframes[index]), minimum, maximum, curveTop, curveBottom));
            double rangePadding = GuiElement.scaled(8d);
            double rangeTop = Math.Clamp(selectedMarkerTop - rangePadding, 0d, bounds.OuterHeight);
            double rangeBottom = Math.Clamp(selectedMarkerBottom + rangePadding, 0d, bounds.OuterHeight);
            selectedRangeY = originY + (float)rangeTop;
            selectedRangeHeight = Math.Max(2f, (float)(rangeBottom - rangeTop));
            solidTextureRenderer.Render(selectedRangeX, selectedRangeY,
                selectedRangeWidth, selectedRangeHeight, 100.3f,
                ColorUtil.ToRgba(112, 72, 224, 132));
        }

        int samples = Math.Clamp((int)Math.Round(right - left), 64, 320);
        for (int sampleIndex = 0; sampleIndex <= samples; sampleIndex++)
        {
            double time = viewStart + viewDuration * sampleIndex / samples;
            if (!CameraPathSampler.TryEvaluate(path, time, out CameraKeyframe sample))
            {
                continue;
            }
            float sampleX = originX + (float)(left + (right - left) * sampleIndex / samples);
            float sampleY = originY + (float)PathCurveValueToY(
                PathCurveValue(sample), minimum, maximum, curveTop, curveBottom);
            bool selectedSample = selectedSegments.Any(segment =>
                time >= segment.Start - CameraPathTiming.TimeMatchEpsilon
                && time <= segment.End + CameraPathTiming.TimeMatchEpsilon);
            solidTextureRenderer.Render(sampleX - 1f, sampleY - 1f, 3f, 3f, 100.35f,
                selectedSample
                    ? ColorUtil.ToRgba(255, 72, 224, 132)
                    : ColorUtil.ToRgba(255, 66, 194, 221));
        }

        float laneY = originY + height - (float)laneHeight;
        for (int index = 0; index < path.Keyframes.Count; index++)
        {
            CameraKeyframe frame = path.Keyframes[index];
            double segmentEnd = index + 1 < path.Keyframes.Count
                ? path.Keyframes[index + 1].TimeSeconds
                : path.DurationSeconds;
            if (segmentEnd < viewStart || frame.TimeSeconds > viewStart + viewDuration)
            {
                continue;
            }
            float startX = originX + (float)PathEditorTimeToLocalX(
                Math.Max(frame.TimeSeconds, viewStart), bounds);
            float endX = originX + (float)PathEditorTimeToLocalX(
                Math.Min(segmentEnd, viewStart + viewDuration), bounds);
            CameraPathInterpolation interpolation = frame.InterpolationToNext ?? path.Interpolation;
            int color = interpolation switch
            {
                CameraPathInterpolation.Linear => ColorUtil.ToRgba(232, 71, 174, 199),
                CameraPathInterpolation.Bezier => ColorUtil.ToRgba(232, 185, 111, 199),
                CameraPathInterpolation.Step => ColorUtil.ToRgba(232, 213, 86, 92),
                _ => ColorUtil.ToRgba(232, 220, 168, 73)
            };
            solidTextureRenderer.Render(startX, laneY + 3f,
                Math.Max(2f, endX - startX), (float)laneHeight - 6f, 100.45f, color);
            if (frame.HoldSeconds > CameraPathTiming.TimeMatchEpsilon)
            {
                double holdEnd = Math.Min(segmentEnd, frame.TimeSeconds + frame.HoldSeconds);
                float holdX = originX + (float)PathEditorTimeToLocalX(holdEnd, bounds);
                solidTextureRenderer.Render(startX, laneY + 3f,
                    Math.Max(1f, holdX - startX), 3f, 100.5f,
                    ColorUtil.ToRgba(255, 244, 206, 111));
            }
            if (index + 1 < path.Keyframes.Count
                && selectedPathKeyframes.Contains(index)
                && selectedPathKeyframes.Contains(index + 1))
            {
                float highlightStartX = originX + (float)PathEditorTimeToLocalX(
                    Math.Max(frame.TimeSeconds, viewStart), bounds);
                float highlightEndX = originX + (float)PathEditorTimeToLocalX(
                    Math.Min(segmentEnd, viewStart + viewDuration), bounds);
                solidTextureRenderer.Render(highlightStartX, laneY + 3f,
                    Math.Max(2f, highlightEndX - highlightStartX), 3f, 100.55f,
                    ColorUtil.ToRgba(255, 72, 224, 132));
            }
            else if (!hasSelectedRange && selectedPathKeyframes.Contains(index))
            {
                solidTextureRenderer.Render(startX, laneY + 3f,
                    Math.Max(2f, endX - startX), 2f, 100.55f,
                    ColorUtil.ToRgba(255, 250, 190, 45));
            }
        }

        for (int index = 0; index < path.Keyframes.Count; index++)
        {
            CameraKeyframe frame = path.Keyframes[index];
            if (frame.TimeSeconds < viewStart - CameraPathTiming.TimeMatchEpsilon
                || frame.TimeSeconds > viewStart + viewDuration + CameraPathTiming.TimeMatchEpsilon)
            {
                continue;
            }
            float markerX = originX + (float)PathEditorTimeToLocalX(frame.TimeSeconds, bounds);
            float markerY = originY + (float)PathCurveValueToY(
                PathCurveValue(frame), minimum, maximum, curveTop, curveBottom);
            bool selected = selectedPathKeyframes.Contains(index);
            bool rangeSelected = hasSelectedRange && selected;
            float size = selected ? 11f : 8f;
            solidTextureRenderer.Render(markerX - size / 2f, markerY - size / 2f,
                size, size, 100.65f,
                selected
                    ? rangeSelected
                        ? ColorUtil.ToRgba(255, 72, 224, 132)
                        : ColorUtil.ToRgba(255, 250, 190, 45)
                    : ColorUtil.ToRgba(255, 72, 218, 224));
            if (!string.IsNullOrWhiteSpace(frame.Marker))
            {
                solidTextureRenderer.Render(markerX - 2f, laneY, 4f, 5f, 100.7f,
                    ColorUtil.ToRgba(255, 238, 92, 92));
            }
        }

        if (selectingPathEditorRange)
        {
            double selectionStart = PathEditorTimeToLocalX(
                Math.Clamp(Math.Min(pathEditorBoxStartTime, pathEditorBoxEndTime),
                    viewStart, viewStart + viewDuration), bounds);
            double selectionEnd = PathEditorTimeToLocalX(
                Math.Clamp(Math.Max(pathEditorBoxStartTime, pathEditorBoxEndTime),
                    viewStart, viewStart + viewDuration), bounds);
            float selectionX = originX + (float)selectionStart;
            float selectionWidth = Math.Max(1f, (float)(selectionEnd - selectionStart));
            double localSelectionTop = Math.Clamp(
                Math.Min(pathEditorDragStartMouseY, pathEditorBoxEndMouseY) - bounds.absY,
                0d,
                bounds.OuterHeight);
            double localSelectionBottom = Math.Clamp(
                Math.Max(pathEditorDragStartMouseY, pathEditorBoxEndMouseY) - bounds.absY,
                0d,
                bounds.OuterHeight);
            float selectionY = originY + (float)localSelectionTop;
            float selectionHeight = Math.Max(1f,
                (float)(localSelectionBottom - localSelectionTop));
            solidTextureRenderer.Render(selectionX, selectionY,
                selectionWidth, selectionHeight, 100.8f,
                ColorUtil.ToRgba(118, 72, 224, 132));
        }

        if (double.IsFinite(pathEditorHoverTime)
            && pathEditorHoverTime >= viewStart
            && pathEditorHoverTime <= viewStart + viewDuration)
        {
            float hoverX = originX + (float)PathEditorTimeToLocalX(pathEditorHoverTime, bounds);
            capi.Render.RenderRectangle(hoverX, originY, 100.85f, 1f, height,
                ColorUtil.ToRgba(210, 190, 122, 212));
        }

        if (pathEditorPlayhead >= viewStart && pathEditorPlayhead <= viewStart + viewDuration)
        {
            float playheadX = originX + (float)PathEditorTimeToLocalX(pathEditorPlayhead, bounds);
            capi.Render.RenderRectangle(playheadX, originY, 100.9f, 2f, height,
                ColorUtil.ToRgba(245, 92, 203, 224));
        }
    }

    private void RenderWorkflowBoard(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        float originX = (float)bounds.renderX;
        float originY = (float)bounds.renderY;
        float width = (float)bounds.OuterWidth;
        float height = (float)bounds.OuterHeight;
        capi.Render.RenderRectangle(originX, originY, 100f, width, height,
            ColorUtil.ToRgba(248, 17, 23, 28));
        capi.Render.RenderRectangle(originX, originY, 100.1f, width, (float)GuiElement.scaled(28d),
            ColorUtil.ToRgba(238, 29, 38, 45));
        capi.Render.RenderRectangle(originX, originY + height - (float)GuiElement.scaled(30d), 100.1f,
            width, (float)GuiElement.scaled(30d), ColorUtil.ToRgba(238, 29, 38, 45));

        DirectorWorkflow? workflow = workflowEdit;
        if (workflow is null)
        {
            return;
        }

        WorkflowView view = WorkflowViewForDisplay(workflow);
        double laneHeight = WorkflowLaneHeight(bounds);
        double left = GuiElement.scaled(30d);
        double usableWidth = Math.Max(1d, width - left * 2d);
        int visibleLanes = WorkflowVisibleLaneCount(bounds, laneHeight);
        for (int lane = 0; lane < visibleLanes; lane++)
        {
            double laneY = GuiElement.scaled(34d) + lane * laneHeight + GuiElement.scaled(3d);
            RenderWorkflowRect(bounds, left, laneY, usableWidth,
                Math.Max(GuiElement.scaled(36d), laneHeight - GuiElement.scaled(7d)),
                100.2f, ColorUtil.ToRgba(lane % 2 == 0 ? 34 : 22, 73, 89, 96));
        }


        if (draggingWorkflowCard && workflowCardDragMode == WorkflowCardDragMode.Move)
        {
            double laneY = GuiElement.scaled(36d)
                + (workflowDragLane - layout.WorkflowLaneOffset) * laneHeight;
            RenderWorkflowRect(bounds, left, laneY, usableWidth,
                Math.Max(GuiElement.scaled(44d), laneHeight - GuiElement.scaled(10d)),
                100.25f, ColorUtil.ToRgba(54, 105, 176, 190));
        }

        IReadOnlyList<DirectorWorkflowCard> ordered = workflow.Cards
            .Where(candidate => candidate.Enabled)
            .OrderBy(candidate => candidate.StartSeconds)
            .ThenBy(candidate => candidate.Lane)
            .ThenBy(candidate => candidate.Order)
            .ToArray();
        IReadOnlyList<DirectorWorkflowCard> shots = ordered
            .Where(card => card.Kind == DirectorWorkflowCardKind.Shot)
            .ToArray();
        for (int index = 0; index + 1 < shots.Count; index++)
        {
            WorkflowCardRectangle(shots[index], bounds, view, laneHeight,
                out double fromX, out double fromY, out double fromWidth, out double fromHeight);
            WorkflowCardRectangle(shots[index + 1], bounds, view, laneHeight,
                out double toX, out double toY, out _, out double toHeight);
            double fromRight = fromX + fromWidth;
            double fromCenter = fromY + fromHeight / 2d;
            double toCenter = toY + toHeight / 2d;
            double bendX = fromRight <= toX
                ? (fromRight + toX) / 2d
                : Math.Max(fromRight, toX) + GuiElement.scaled(18d);
            int lineColor = ColorUtil.ToRgba(112, 105, 166, 176);
            RenderWorkflowLine(bounds, fromRight, fromCenter, bendX, fromCenter, lineColor);
            RenderWorkflowLine(bounds, bendX, fromCenter, bendX, toCenter, lineColor);
            RenderWorkflowLine(bounds, bendX, toCenter, toX, toCenter, lineColor);
            RenderWorkflowRect(bounds, toX - GuiElement.scaled(3d), toCenter - GuiElement.scaled(3d),
                GuiElement.scaled(6d), GuiElement.scaled(6d), 100.35f,
                ColorUtil.ToRgba(230, 111, 190, 199));
        }

        foreach (DirectorWorkflowCard card in ordered.OrderBy(candidate => candidate.Lane))
        {
            WorkflowCardRectangle(card, bounds, view, laneHeight,
                out double x, out double y, out double cardWidth, out double cardHeight);
            if (x >= width || x + cardWidth <= 0d || y >= height || y + cardHeight <= 0d)
            {
                continue;
            }

            bool selected = card.Id == selectedWorkflowCardId;
            int color = WorkflowCardBackground(card, selected);
            int accent = WorkflowCardAccent(card, selected ? 255 : 226);
            RenderSolidWorkflowRect(bounds, x, y, cardWidth, cardHeight, 100.4f, color);
            RenderSolidWorkflowRect(bounds, x, y, cardWidth, GuiElement.scaled(selected ? 4d : 3d),
                100.45f, accent);
            RenderSolidWorkflowRect(bounds, x, y + GuiElement.scaled(7d), GuiElement.scaled(5d),
                Math.Max(1d, cardHeight - GuiElement.scaled(14d)), 100.5f, accent);
            RenderSolidWorkflowRect(bounds, x - GuiElement.scaled(4d), y + cardHeight / 2d - GuiElement.scaled(4d),
                GuiElement.scaled(8d), GuiElement.scaled(8d), 100.55f, accent);
            RenderSolidWorkflowRect(bounds, x + cardWidth - GuiElement.scaled(4d),
                y + cardHeight / 2d - GuiElement.scaled(4d), GuiElement.scaled(8d), GuiElement.scaled(8d),
                100.55f, accent);
        }

        foreach (DirectorTimelineAudioClip clip in workflow.AudioClips
            .Where(candidate => candidate.Enabled)
            .OrderBy(candidate => candidate.Track)
            .ThenBy(candidate => candidate.TimelineStartSeconds))
        {
            WorkflowAudioClipRectangle(clip, workflow, bounds, view, laneHeight,
                out double x, out double y, out double clipWidth, out double clipHeight);
            if (x >= width || x + clipWidth <= 0d || y >= height || y + clipHeight <= 0d)
            {
                continue;
            }

            bool selected = clip.Id == selectedWorkflowAudioClipId && workflowAudioEditorExpanded;
            int background = ColorUtil.ToRgba(255,
                selected ? 39 : 30, selected ? 137 : 106, selected ? 108 : 82);
            int accent = ColorUtil.ToRgba(255, 74, 214, 170);
            RenderSolidWorkflowRect(bounds, x, y, clipWidth, clipHeight, 100.4f, background);
            RenderSolidWorkflowRect(bounds, x, y, clipWidth, GuiElement.scaled(selected ? 4d : 3d),
                100.45f, accent);
            RenderSolidWorkflowRect(bounds, x, y, GuiElement.scaled(5d), clipHeight, 100.5f, accent);
        }

        double tick = WorkflowTickInterval(view.DurationSeconds, usableWidth / GuiElement.scaled(1d));
        double firstTick = Math.Ceiling(view.StartSeconds / tick) * tick;
        for (double time = firstTick; time <= view.EndSeconds + DirectorWorkflowPlanner.TimeEpsilon; time += tick)
        {
            double x = left + usableWidth * ((time - view.StartSeconds) / view.DurationSeconds);
            RenderWorkflowRect(bounds, x, 0d, GuiElement.scaled(1d), GuiElement.scaled(8d), 100.6f,
                ColorUtil.ToRgba(180, 161, 179, 184));
            RenderWorkflowRect(bounds, x, height - GuiElement.scaled(9d), GuiElement.scaled(1d),
                GuiElement.scaled(9d), 100.6f, ColorUtil.ToRgba(180, 161, 179, 184));
        }

        if (owner.ActiveReplayId != Guid.Empty
            && owner.ReplayTimeSeconds >= view.StartSeconds
            && owner.ReplayTimeSeconds <= view.EndSeconds)
        {
            double playheadX = left + usableWidth
                * ((owner.ReplayTimeSeconds - view.StartSeconds) / view.DurationSeconds);
            RenderWorkflowRect(bounds, playheadX - GuiElement.scaled(1d), 0d, GuiElement.scaled(2d), height,
                100.7f, ColorUtil.ToRgba(235, 229, 92, 74));
        }

        if (double.IsFinite(workflowSnapTime)
            && workflowSnapTime >= view.StartSeconds - DirectorWorkflowPlanner.TimeEpsilon
            && workflowSnapTime <= view.EndSeconds + DirectorWorkflowPlanner.TimeEpsilon)
        {
            double snapX = left + usableWidth
                * ((workflowSnapTime - view.StartSeconds) / view.DurationSeconds);
            int snapColor = ColorUtil.ToRgba(248, 80, 205, 224);
            RenderWorkflowRect(bounds, snapX - GuiElement.scaled(1d), GuiElement.scaled(22d),
                GuiElement.scaled(2d), height - GuiElement.scaled(44d), 100.8f, snapColor);
            RenderWorkflowRect(bounds, snapX - GuiElement.scaled(5d), GuiElement.scaled(22d),
                GuiElement.scaled(10d), GuiElement.scaled(4d), 100.81f, snapColor);
        }

        RenderWorkflowScrollbars(bounds, workflow, view, visibleLanes);

        if (contextWorkflowCardId != Guid.Empty)
        {
            RenderWorkflowRect(
                bounds,
                GuiElement.scaled(workflowContextX),
                GuiElement.scaled(workflowContextY),
                GuiElement.scaled(140d),
                GuiElement.scaled(38d),
                100.9f,
                ColorUtil.ToRgba(252, 12, 17, 21));
        }
    }

    private DirectorWorkflowCard? WorkflowCardAt(double mouseX, double mouseY)
    {
        if (workflowEdit is null || workflowBoardBounds is null)
        {
            return null;
        }

        WorkflowView view = CurrentWorkflowView(workflowEdit);
        double laneHeight = WorkflowLaneHeight(workflowBoardBounds);
        double relativeX = mouseX - workflowBoardBounds.absX;
        double relativeY = mouseY - workflowBoardBounds.absY;
        List<(DirectorWorkflowCard Card, double Distance)> expanded = new();
        foreach (DirectorWorkflowCard card in workflowEdit.Cards
            .Where(candidate => candidate.Enabled)
            .OrderByDescending(candidate => candidate.Id == selectedWorkflowCardId)
            .ThenByDescending(candidate => candidate.Order))
        {
            WorkflowCardRectangle(card, workflowBoardBounds, view, laneHeight,
                out double x, out double y, out double width, out double height);
            if (relativeX >= x && relativeX <= x + width
                && relativeY >= y && relativeY <= y + height)
            {
                return card;
            }

            double hitWidth = Math.Max(width, GuiElement.scaled(24d));
            double hitX = x - (hitWidth - width) / 2d;
            if (relativeX >= hitX && relativeX <= hitX + hitWidth
                && relativeY >= y && relativeY <= y + height)
            {
                double distance = relativeX < x
                    ? x - relativeX
                    : relativeX > x + width
                        ? relativeX - (x + width)
                        : 0d;
                expanded.Add((card, distance));
            }
        }

        return expanded
            .OrderBy(candidate => candidate.Distance)
            .ThenByDescending(candidate => candidate.Card.Order)
            .Select(candidate => candidate.Card)
            .FirstOrDefault();
    }

    private DirectorTimelineAudioClip? WorkflowAudioClipAt(double mouseX, double mouseY)
    {
        if (workflowEdit is null || workflowBoardBounds is null)
        {
            return null;
        }

        WorkflowView view = CurrentWorkflowView(workflowEdit);
        double laneHeight = WorkflowLaneHeight(workflowBoardBounds);
        double relativeX = mouseX - workflowBoardBounds.absX;
        double relativeY = mouseY - workflowBoardBounds.absY;
        return workflowEdit.AudioClips
            .Where(clip => clip.Enabled)
            .OrderByDescending(clip => clip.Id == selectedWorkflowAudioClipId)
            .FirstOrDefault(clip =>
            {
                WorkflowAudioClipRectangle(clip, workflowEdit, workflowBoardBounds, view, laneHeight,
                    out double x, out double y, out double width, out double height);
                return relativeX >= x && relativeX <= x + Math.Max(width, GuiElement.scaled(16d))
                    && relativeY >= y && relativeY <= y + height;
            });
    }

    private WorkflowCardDragMode WorkflowCardDragModeAt(DirectorWorkflowCard card, double mouseX)
    {
        if (workflowBoardBounds is null || workflowEdit is null)
        {
            return WorkflowCardDragMode.Move;
        }

        WorkflowView view = draggingWorkflowCard
            ? workflowDragView
            : CurrentWorkflowView(workflowEdit);
        double laneHeight = WorkflowLaneHeight(workflowBoardBounds);
        WorkflowCardRectangle(card, workflowBoardBounds, view, laneHeight,
            out double x, out _, out double width, out _);
        double relativeX = mouseX - workflowBoardBounds.absX;
        double hitWidth = Math.Max(width, GuiElement.scaled(24d));
        double hitX = x - (hitWidth - width) / 2d;
        if (width < GuiElement.scaled(24d))
        {
            double moveWidth = Math.Min(hitWidth, Math.Max(width, GuiElement.scaled(8d)));
            double moveLeft = x + width / 2d - moveWidth / 2d;
            if (relativeX < moveLeft)
            {
                return WorkflowCardDragMode.ResizeStart;
            }
            if (relativeX > moveLeft + moveWidth)
            {
                return WorkflowCardDragMode.ResizeEnd;
            }
            return WorkflowCardDragMode.Move;
        }

        double handleWidth = Math.Min(hitWidth / 3d, GuiElement.scaled(10d));
        if (relativeX <= hitX + handleWidth)
        {
            return WorkflowCardDragMode.ResizeStart;
        }
        if (relativeX >= hitX + hitWidth - handleWidth)
        {
            return WorkflowCardDragMode.ResizeEnd;
        }
        return WorkflowCardDragMode.Move;
    }

    private void UpdateWorkflowCardDrag(MouseEvent args)
    {
        DirectorWorkflowCard? card = SelectedWorkflowCard();
        if (card is null || workflowBoardBounds is null)
        {
            return;
        }

        WorkflowView view = workflowDragView;
        double left = GuiElement.scaled(30d);
        double usableWidth = Math.Max(1d, workflowBoardBounds.OuterWidth - left * 2d);
        double deltaSeconds = (args.X - workflowDragStartX) / usableWidth * view.DurationSeconds;
        double minimum = DirectorWorkflowPlanner.MinCardDurationSeconds;
        workflowSnapTime = double.NaN;
        switch (workflowCardDragMode)
        {
            case WorkflowCardDragMode.ResizeStart:
            {
                double requested = Math.Clamp(workflowDragCardStart + deltaSeconds,
                    0d, Math.Max(0d, workflowDragCardEnd - minimum));
                if (TrySnapWorkflowBoundary(card.Id, requested, view, usableWidth, out double snapped))
                {
                    requested = snapped;
                    workflowSnapTime = snapped;
                }
                card.StartSeconds = Math.Clamp(requested, 0d, workflowDragCardEnd - minimum);
                card.EndSeconds = workflowDragCardEnd;
                break;
            }
            case WorkflowCardDragMode.ResizeEnd:
            {
                double requested = Math.Clamp(workflowDragCardEnd + deltaSeconds,
                    workflowDragCardStart + minimum, DirectorWorkflowPlanner.MaxTimelineSeconds);
                if (TrySnapWorkflowBoundary(card.Id, requested, view, usableWidth, out double snapped))
                {
                    requested = snapped;
                    workflowSnapTime = snapped;
                }
                card.StartSeconds = workflowDragCardStart;
                card.EndSeconds = Math.Clamp(requested,
                    workflowDragCardStart + minimum, DirectorWorkflowPlanner.MaxTimelineSeconds);
                break;
            }
            default:
            {
                double duration = Math.Max(minimum, workflowDragCardEnd - workflowDragCardStart);
                double requested = Math.Clamp(workflowDragCardStart + deltaSeconds,
                    0d, Math.Max(0d, DirectorWorkflowPlanner.MaxTimelineSeconds - duration));
                double requestedEnd = requested + duration;
                bool startSnaps = TrySnapWorkflowBoundary(
                    card.Id, requested, view, usableWidth, out double snappedStart);
                bool endSnaps = TrySnapWorkflowBoundary(
                    card.Id, requestedEnd, view, usableWidth, out double snappedEnd);
                if (startSnaps || endSnaps)
                {
                    bool useStart = startSnaps && (!endSnaps
                        || Math.Abs(snappedStart - requested) <= Math.Abs(snappedEnd - requestedEnd));
                    workflowSnapTime = useStart ? snappedStart : snappedEnd;
                    requested += useStart
                        ? snappedStart - requested
                        : snappedEnd - requestedEnd;
                }
                card.StartSeconds = Math.Clamp(requested,
                    0d, Math.Max(0d, DirectorWorkflowPlanner.MaxTimelineSeconds - duration));
                card.EndSeconds = card.StartSeconds + duration;

                double laneHeight = WorkflowLaneHeight(workflowBoardBounds);
                double relativeY = args.Y - workflowBoardBounds.absY - GuiElement.scaled(34d);
                int visibleLanes = WorkflowVisibleLaneCount(workflowBoardBounds, laneHeight);
                workflowDragLane = Math.Clamp(
                    layout.WorkflowLaneOffset + (int)Math.Floor(relativeY / laneHeight),
                    layout.WorkflowLaneOffset,
                    Math.Min(31, layout.WorkflowLaneOffset + visibleLanes - 1));
                card.Lane = workflowDragLane;
                break;
            }
        }
    }

    private bool TrySnapWorkflowBoundary(
        Guid cardId,
        double requested,
        WorkflowView view,
        double usableWidth,
        out double snapped)
    {
        snapped = requested;
        if (workflowEdit is null)
        {
            return false;
        }

        double threshold = Math.Max(DirectorWorkflowPlanner.TimeEpsilon,
            view.DurationSeconds * GuiElement.scaled(10d) / Math.Max(1d, usableWidth));
        double tick = WorkflowTickInterval(
            view.DurationSeconds,
            usableWidth / Math.Max(0.001d, GuiElement.scaled(1d)));
        List<double> candidates = new()
        {
            0d,
            Math.Round(requested / tick, MidpointRounding.AwayFromZero) * tick
        };
        if (owner.ActiveReplayId != Guid.Empty)
        {
            candidates.Add(owner.ReplayTimeSeconds);
        }
        foreach (DirectorWorkflowCard other in workflowEdit.Cards)
        {
            if (other.Id == cardId || !other.Enabled)
            {
                continue;
            }
            candidates.Add(other.StartSeconds);
            candidates.Add(other.EndSeconds);
        }
        foreach (DirectorTimelineAudioClip clip in workflowEdit.AudioClips.Where(clip => clip.Enabled))
        {
            candidates.Add(clip.TimelineStartSeconds);
            candidates.Add(clip.TimelineStartSeconds + clip.DurationSeconds);
        }

        double bestDistance = double.MaxValue;
        foreach (double candidate in candidates.Where(double.IsFinite))
        {
            double clamped = Math.Clamp(candidate, 0d, DirectorWorkflowPlanner.MaxTimelineSeconds);
            double distance = Math.Abs(clamped - requested);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                snapped = clamped;
            }
        }
        return bestDistance <= threshold;
    }

    private bool TryBeginWorkflowScrollbarDrag(double mouseX, double mouseY)
    {
        if (workflowEdit is null || workflowBoardBounds is null)
        {
            return false;
        }

        double localX = mouseX - workflowBoardBounds.absX;
        double localY = mouseY - workflowBoardBounds.absY;
        double hit = GuiElement.scaled(15d);
        if (localY >= workflowBoardBounds.OuterHeight - hit
            && localX >= GuiElement.scaled(24d)
            && localX <= workflowBoardBounds.OuterWidth - GuiElement.scaled(24d))
        {
            workflowScrollbarDragMode = WorkflowScrollbarDragMode.Horizontal;
            UpdateWorkflowScrollbarDrag(mouseX, mouseY);
            return true;
        }
        if (localX >= workflowBoardBounds.OuterWidth - hit
            && localY >= GuiElement.scaled(28d)
            && localY <= workflowBoardBounds.OuterHeight - GuiElement.scaled(28d))
        {
            workflowScrollbarDragMode = WorkflowScrollbarDragMode.Vertical;
            UpdateWorkflowScrollbarDrag(mouseX, mouseY);
            return true;
        }
        return false;
    }

    private void UpdateWorkflowScrollbarDrag(double mouseX, double mouseY)
    {
        if (workflowEdit is null || workflowBoardBounds is null)
        {
            return;
        }

        WorkflowView view = CurrentWorkflowView(workflowEdit);
        if (workflowScrollbarDragMode == WorkflowScrollbarDragMode.Horizontal)
        {
            double trackLeft = workflowBoardBounds.absX + GuiElement.scaled(30d);
            double trackWidth = Math.Max(1d,
                workflowBoardBounds.OuterWidth - GuiElement.scaled(60d));
            double thumbWidth = WorkflowHorizontalThumbWidth(trackWidth, view);
            double available = Math.Max(1d, trackWidth - thumbWidth);
            double fraction = Math.Clamp((mouseX - trackLeft - thumbWidth / 2d) / available, 0d, 1d);
            layout.WorkflowPanSeconds = fraction
                * Math.Max(0d, view.TimelineSeconds - view.DurationSeconds);
            return;
        }

        if (workflowScrollbarDragMode == WorkflowScrollbarDragMode.Vertical)
        {
            double laneHeight = WorkflowLaneHeight(workflowBoardBounds);
            int visibleLanes = WorkflowVisibleLaneCount(workflowBoardBounds, laneHeight);
            int totalLanes = WorkflowTotalLaneCount(workflowEdit, visibleLanes);
            int maxOffset = Math.Max(0, totalLanes - visibleLanes);
            double trackTop = workflowBoardBounds.absY + GuiElement.scaled(34d);
            double trackHeight = Math.Max(1d,
                workflowBoardBounds.OuterHeight - GuiElement.scaled(68d));
            double thumbHeight = WorkflowVerticalThumbHeight(trackHeight, visibleLanes, totalLanes);
            double available = Math.Max(1d, trackHeight - thumbHeight);
            double fraction = Math.Clamp((mouseY - trackTop - thumbHeight / 2d) / available, 0d, 1d);
            layout.WorkflowLaneOffset = Math.Clamp(
                (int)Math.Round(fraction * maxOffset, MidpointRounding.AwayFromZero),
                0,
                maxOffset);
        }
    }

    private static double WorkflowTimelineSeconds(DirectorWorkflow workflow)
        => Math.Max(10d, Math.Min(
            DirectorWorkflowPlanner.MaxTimelineSeconds,
            Math.Max(workflow.DurationSeconds, workflow.Cards.Select(card => card.EndSeconds).DefaultIfEmpty(10d).Max())));

    private static int WorkflowVisibleLaneCount(ElementBounds bounds, double laneHeight)
        => Math.Max(1, (int)Math.Floor(
            (bounds.OuterHeight - GuiElement.scaled(68d)) / Math.Max(1d, laneHeight)));

    private static int WorkflowTotalLaneCount(DirectorWorkflow workflow, int visibleLanes)
    {
        int videoLanes = WorkflowVideoLaneCount(workflow);
        int audioLanes = workflow.AudioClips
            .Where(clip => clip.Enabled)
            .Select(clip => videoLanes + Math.Clamp(clip.Track, 0, DirectorWorkflowAudioEditor.MaximumTrack) + 1)
            .DefaultIfEmpty(videoLanes)
            .Max();
        return Math.Max(visibleLanes, Math.Max(videoLanes, audioLanes));
    }

    private static int WorkflowVideoLaneCount(DirectorWorkflow workflow)
        => workflow.Cards
            .Where(card => card.Enabled)
            .Select(card => Math.Clamp(card.Lane, 0, 31) + 1)
            .DefaultIfEmpty(1)
            .Max();

    private static double WorkflowHorizontalThumbWidth(double trackWidth, WorkflowView view)
        => Math.Clamp(
            trackWidth * Math.Clamp(view.DurationSeconds / Math.Max(view.DurationSeconds, view.TimelineSeconds), 0d, 1d),
            Math.Min(trackWidth, GuiElement.scaled(28d)),
            trackWidth);

    private static double WorkflowVerticalThumbHeight(double trackHeight, int visibleLanes, int totalLanes)
        => Math.Clamp(
            trackHeight * Math.Clamp(visibleLanes / (double)Math.Max(visibleLanes, totalLanes), 0d, 1d),
            Math.Min(trackHeight, GuiElement.scaled(28d)),
            trackHeight);

    private void WorkflowCardRectangle(
        DirectorWorkflowCard card,
        ElementBounds bounds,
        WorkflowView view,
        double laneHeight,
        out double x,
        out double y,
        out double width,
        out double height)
    {
        double left = GuiElement.scaled(30d);
        double usableWidth = Math.Max(1d, bounds.OuterWidth - left * 2d);
        x = left + usableWidth * ((card.StartSeconds - view.StartSeconds) / view.DurationSeconds);
        width = Math.Max(GuiElement.scaled(3d), usableWidth * (card.DurationSeconds / view.DurationSeconds));
        y = GuiElement.scaled(36d)
            + (Math.Clamp(card.Lane, 0, 31) - layout.WorkflowLaneOffset) * laneHeight;
        height = Math.Max(GuiElement.scaled(44d), laneHeight - GuiElement.scaled(10d));
    }

    private void WorkflowAudioClipRectangle(
        DirectorTimelineAudioClip clip,
        DirectorWorkflow workflow,
        ElementBounds bounds,
        WorkflowView view,
        double laneHeight,
        out double x,
        out double y,
        out double width,
        out double height)
    {
        double left = GuiElement.scaled(30d);
        double usableWidth = Math.Max(1d, bounds.OuterWidth - left * 2d);
        x = left + usableWidth * ((clip.TimelineStartSeconds - view.StartSeconds) / view.DurationSeconds);
        width = Math.Max(GuiElement.scaled(3d), usableWidth * (clip.DurationSeconds / view.DurationSeconds));
        int lane = WorkflowVideoLaneCount(workflow)
            + Math.Clamp(clip.Track, 0, DirectorWorkflowAudioEditor.MaximumTrack);
        y = GuiElement.scaled(36d) + (lane - layout.WorkflowLaneOffset) * laneHeight;
        height = Math.Max(GuiElement.scaled(44d), laneHeight - GuiElement.scaled(10d));
    }

    private void AddWorkflowCanvasLabels(
        GuiComposer composer,
        CairoFont detail,
        ElementBounds bounds)
    {
        DirectorWorkflow? workflow = workflowEdit;
        if (workflow is null)
        {
            return;
        }

        double boardWidth = Math.Max(1d, layout.Panel("workflow").Width - 20d);
        double boardHeight = Math.Max(1d, layout.Panel("workflow").Height - 142d);
        WorkflowView view = WorkflowViewForDisplay(workflow);
        double laneHeight = WorkflowLaneHeightLogical(boardHeight);
        int visibleLanes = Math.Max(1, (int)Math.Floor(
            (boardHeight - 68d) / Math.Max(1d, laneHeight)));
        int totalLanes = WorkflowTotalLaneCount(workflow, visibleLanes);
        layout.WorkflowLaneOffset = Math.Clamp(
            layout.WorkflowLaneOffset,
            0,
            Math.Max(0, totalLanes - visibleLanes));
        foreach (DirectorWorkflowCard card in workflow.Cards
            .Where(candidate => candidate.Enabled
                && (!draggingWorkflowCard || candidate.Id != selectedWorkflowCardId))
            .OrderBy(candidate => candidate.Lane)
            .ThenBy(candidate => candidate.StartSeconds)
            .Take(96))
        {
            WorkflowCardRectangleLogical(card, boardWidth, view, laneHeight, layout.WorkflowLaneOffset,
                out double x, out double y, out double width, out double height);
            double clippedLeft = Math.Max(0d, x);
            double clippedRight = Math.Min(boardWidth, x + width);
            if (clippedRight - clippedLeft < 46d
                || y < 28d
                || y + height > boardHeight - 28d)
            {
                continue;
            }

            double labelX = 10d + clippedLeft + 12d;
            double labelWidth = Math.Max(28d, clippedRight - clippedLeft - 20d);
            int maxName = Math.Max(5, Math.Min(42, (int)(labelWidth / 7d)));
            composer
                .AddDynamicText(Ellipsize(card.Name, maxName), detail,
                    ElementBounds.Fixed(labelX, 76d + y + 6d, labelWidth, 18d),
                    "workflowCardName-" + card.Id.ToString("N"))
                .AddDynamicText(
                    WorkflowCardDetailLabel(card),
                    detail,
                    ElementBounds.Fixed(labelX, 76d + y + 25d, labelWidth, 18d),
                    "workflowCardTime-" + card.Id.ToString("N"));
        }


        double usableWidth = Math.Max(1d, boardWidth - 60d);
        int videoLanes = WorkflowVideoLaneCount(workflow);
        foreach (DirectorTimelineAudioClip clip in workflow.AudioClips
            .Where(candidate => candidate.Enabled)
            .OrderBy(candidate => candidate.Track)
            .ThenBy(candidate => candidate.TimelineStartSeconds)
            .Take(96))
        {
            double x = 30d + usableWidth
                * ((clip.TimelineStartSeconds - view.StartSeconds) / view.DurationSeconds);
            double width = Math.Max(3d, usableWidth * (clip.DurationSeconds / view.DurationSeconds));
            int lane = videoLanes + Math.Clamp(clip.Track, 0, DirectorWorkflowAudioEditor.MaximumTrack);
            double y = 36d + (lane - layout.WorkflowLaneOffset) * laneHeight;
            double height = Math.Max(44d, laneHeight - 10d);
            double clippedLeft = Math.Max(0d, x);
            double clippedRight = Math.Min(boardWidth, x + width);
            if (clippedRight - clippedLeft < 46d
                || y < 28d
                || y + height > boardHeight - 28d)
            {
                continue;
            }

            double labelX = 10d + clippedLeft + 12d;
            double labelWidth = Math.Max(28d, clippedRight - clippedLeft - 20d);
            int maxName = Math.Max(5, Math.Min(42, (int)(labelWidth / 7d)));
            composer
                .AddDynamicText(Ellipsize(clip.Name, maxName), detail,
                    ElementBounds.Fixed(labelX, 76d + y + 6d, labelWidth, 18d),
                    "workflowAudioName-" + clip.Id.ToString("N"))
                .AddDynamicText(
                    $"A{clip.Track}  {clip.TimelineStartSeconds:0.###}-{clip.TimelineStartSeconds + clip.DurationSeconds:0.###}s",
                    detail,
                    ElementBounds.Fixed(labelX, 76d + y + 25d, labelWidth, 18d),
                    "workflowAudioTime-" + clip.Id.ToString("N"));
        }
    }

    private string WorkflowCardDetailLabel(DirectorWorkflowCard card)
        => card.Kind switch
        {
            DirectorWorkflowCardKind.Countdown => Lang.Get(
                "vsdirector:workflow-card-countdown-label",
                card.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)),
            _ => $"{RecordingSourceTypeLabel(card.SourceType)}  {card.StartSeconds:0.###}-{card.EndSeconds:0.###}s"
        };

    private void AddWorkflowRulerLabels(
        GuiComposer composer,
        CairoFont detail,
        ElementBounds bounds)
    {
        _ = bounds;
        DirectorWorkflow? workflow = workflowEdit;
        if (workflow is null)
        {
            return;
        }

        DirectorWorkspacePanelLayout panel = layout.Panel("workflow");
        double boardWidth = Math.Max(1d, panel.Width - 20d);
        double boardHeight = Math.Max(1d, panel.Height - 142d);
        double usableWidth = Math.Max(1d, boardWidth - 60d);
        WorkflowView view = WorkflowViewForDisplay(workflow);
        double tick = WorkflowTickInterval(view.DurationSeconds, usableWidth);
        double firstTick = Math.Ceiling(view.StartSeconds / tick) * tick;
        int index = 0;
        for (double time = firstTick;
             time <= view.EndSeconds + DirectorWorkflowPlanner.TimeEpsilon && index < 32;
             time += tick, index++)
        {
            double x = 30d + usableWidth * ((time - view.StartSeconds) / view.DurationSeconds);
            string label = time.ToString(time >= 100d ? "0" : "0.##", CultureInfo.InvariantCulture) + "s";
            composer
                .AddDynamicText(label, detail,
                    ElementBounds.Fixed(10d + x - 22d, 78d, 44d, 18d),
                    "workflowRulerTop-" + index)
                .AddDynamicText(label, detail,
                    ElementBounds.Fixed(10d + x - 22d, 76d + boardHeight - 28d, 44d, 14d),
                    "workflowRulerBottom-" + index);
        }
    }

    private WorkflowView CurrentWorkflowView(DirectorWorkflow workflow)
    {
        double timeline = WorkflowTimelineSeconds(workflow);
        double zoom = Math.Clamp(layout.WorkflowZoom, 0.5d, 12d);
        double duration = Math.Max(0.25d, timeline / zoom);
        double maxStart = Math.Max(0d, timeline - duration);
        double start = Math.Clamp(layout.WorkflowPanSeconds, 0d, maxStart);
        layout.WorkflowPanSeconds = start;
        return new WorkflowView(start, duration, timeline, zoom);
    }

    private WorkflowView WorkflowViewForDisplay(DirectorWorkflow workflow)
        => draggingWorkflowCard ? workflowDragView : CurrentWorkflowView(workflow);

    private static double WorkflowLaneHeight(ElementBounds bounds)
        => GuiElement.scaled(WorkflowLaneHeightLogical(
            bounds.OuterHeight / Math.Max(0.001d, GuiElement.scaled(1d))));

    private static double WorkflowLaneHeightLogical(double boardHeight)
        => Math.Clamp((boardHeight - 70d) / 6d, 58d, 86d);

    private static void WorkflowCardRectangleLogical(
        DirectorWorkflowCard card,
        double boardWidth,
        WorkflowView view,
        double laneHeight,
        int laneOffset,
        out double x,
        out double y,
        out double width,
        out double height)
    {
        double usableWidth = Math.Max(1d, boardWidth - 60d);
        x = 30d + usableWidth * ((card.StartSeconds - view.StartSeconds) / view.DurationSeconds);
        width = Math.Max(3d, usableWidth * (card.DurationSeconds / view.DurationSeconds));
        y = 36d + (Math.Clamp(card.Lane, 0, 31) - laneOffset) * laneHeight;
        height = Math.Max(44d, laneHeight - 10d);
    }

    private static double WorkflowTickInterval(double duration, double usableWidth)
    {
        double targetTicks = Math.Clamp(usableWidth / 95d, 4d, 16d);
        double raw = Math.Max(0.001d, duration / targetTicks);
        double power = Math.Pow(10d, Math.Floor(Math.Log10(raw)));
        double normalized = raw / power;
        double step = normalized <= 1d ? 1d : normalized <= 2d ? 2d : normalized <= 5d ? 5d : 10d;
        return step * power;
    }

    private static int WorkflowCardAccent(DirectorWorkflowCard card, int alpha)
        => card.Kind switch
        {
            DirectorWorkflowCardKind.Countdown => ColorUtil.ToRgba(alpha, 236, 154, 68),
            _ => card.SourceType switch
            {
                DirectorRecordingSourceType.Path => ColorUtil.ToRgba(alpha, 225, 176, 73),
                DirectorRecordingSourceType.Sequence => ColorUtil.ToRgba(alpha, 192, 117, 204),
                DirectorRecordingSourceType.Follow => ColorUtil.ToRgba(alpha, 103, 189, 128),
                _ => ColorUtil.ToRgba(alpha, 72, 176, 200)
            }
        };

    private static int WorkflowCardBackground(DirectorWorkflowCard card, bool selected)
    {
        const int alpha = 255;
        if (card.Kind == DirectorWorkflowCardKind.Countdown)
        {
            return ColorUtil.ToRgba(alpha, selected ? 174 : 132, selected ? 111 : 82, selected ? 45 : 34);
        }
        return card.SourceType switch
        {
            DirectorRecordingSourceType.Path => ColorUtil.ToRgba(alpha,
                selected ? 171 : 132, selected ? 132 : 101, selected ? 46 : 35),
            DirectorRecordingSourceType.Sequence => ColorUtil.ToRgba(alpha,
                selected ? 142 : 108, selected ? 87 : 66, selected ? 162 : 124),
            DirectorRecordingSourceType.Follow => ColorUtil.ToRgba(alpha,
                selected ? 70 : 54, selected ? 143 : 112, selected ? 96 : 75),
            _ => ColorUtil.ToRgba(alpha,
                selected ? 54 : 41, selected ? 128 : 100, selected ? 151 : 119)
        };
    }

    private void RenderWorkflowScrollbars(
        ElementBounds bounds,
        DirectorWorkflow workflow,
        WorkflowView view,
        int visibleLanes)
    {
        double horizontalLeft = GuiElement.scaled(30d);
        double horizontalWidth = Math.Max(1d, bounds.OuterWidth - GuiElement.scaled(60d));
        double horizontalY = bounds.OuterHeight - GuiElement.scaled(8d);
        double horizontalHeight = GuiElement.scaled(5d);
        int trackColor = ColorUtil.ToRgba(210, 58, 68, 74);
        int thumbColor = ColorUtil.ToRgba(245, 126, 166, 176);
        RenderWorkflowRect(bounds, horizontalLeft, horizontalY, horizontalWidth,
            horizontalHeight, 100.82f, trackColor);
        double horizontalThumb = WorkflowHorizontalThumbWidth(horizontalWidth, view);
        double horizontalAvailable = Math.Max(0d, horizontalWidth - horizontalThumb);
        double horizontalFraction = view.TimelineSeconds <= view.DurationSeconds
            ? 0d
            : view.StartSeconds / Math.Max(
                DirectorWorkflowPlanner.TimeEpsilon,
                view.TimelineSeconds - view.DurationSeconds);
        RenderWorkflowRect(bounds,
            horizontalLeft + horizontalAvailable * Math.Clamp(horizontalFraction, 0d, 1d),
            horizontalY,
            horizontalThumb,
            horizontalHeight,
            100.84f,
            workflowScrollbarDragMode == WorkflowScrollbarDragMode.Horizontal
                ? ColorUtil.ToRgba(255, 86, 205, 224)
                : thumbColor);

        int totalLanes = WorkflowTotalLaneCount(workflow, visibleLanes);
        int maxOffset = Math.Max(0, totalLanes - visibleLanes);
        layout.WorkflowLaneOffset = Math.Clamp(layout.WorkflowLaneOffset, 0, maxOffset);
        double verticalX = bounds.OuterWidth - GuiElement.scaled(8d);
        double verticalTop = GuiElement.scaled(34d);
        double verticalWidth = GuiElement.scaled(5d);
        double verticalHeight = Math.Max(1d, bounds.OuterHeight - GuiElement.scaled(68d));
        RenderWorkflowRect(bounds, verticalX, verticalTop, verticalWidth,
            verticalHeight, 100.82f, trackColor);
        double verticalThumb = WorkflowVerticalThumbHeight(verticalHeight, visibleLanes, totalLanes);
        double verticalAvailable = Math.Max(0d, verticalHeight - verticalThumb);
        double verticalFraction = maxOffset == 0 ? 0d : layout.WorkflowLaneOffset / (double)maxOffset;
        RenderWorkflowRect(bounds,
            verticalX,
            verticalTop + verticalAvailable * Math.Clamp(verticalFraction, 0d, 1d),
            verticalWidth,
            verticalThumb,
            100.84f,
            workflowScrollbarDragMode == WorkflowScrollbarDragMode.Vertical
                ? ColorUtil.ToRgba(255, 86, 205, 224)
                : thumbColor);
    }

    private void RenderWorkflowRect(
        ElementBounds bounds,
        double x,
        double y,
        double width,
        double height,
        float z,
        int color)
    {
        double left = Math.Max(0d, x);
        double top = Math.Max(0d, y);
        double right = Math.Min(bounds.OuterWidth, x + width);
        double bottom = Math.Min(bounds.OuterHeight, y + height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        capi.Render.RenderRectangle(
            (float)bounds.renderX + (float)left,
            (float)bounds.renderY + (float)top,
            z,
            (float)(right - left),
            (float)(bottom - top),
            color);
    }

    private void RenderSolidWorkflowRect(
        ElementBounds bounds,
        double x,
        double y,
        double width,
        double height,
        float z,
        int color)
    {
        double left = Math.Max(0d, x);
        double top = Math.Max(0d, y);
        double right = Math.Min(bounds.OuterWidth, x + width);
        double bottom = Math.Min(bounds.OuterHeight, y + height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        solidTextureRenderer.Render(
            bounds.renderX + left,
            bounds.renderY + top,
            right - left,
            bottom - top,
            z,
            color);
    }

    private void RenderWorkflowLine(
        ElementBounds bounds,
        double fromX,
        double fromY,
        double toX,
        double toY,
        int color)
    {
        double thickness = GuiElement.scaled(2d);
        if (Math.Abs(toY - fromY) <= 0.01d)
        {
            RenderWorkflowRect(bounds, Math.Min(fromX, toX), fromY - thickness / 2d,
                Math.Abs(toX - fromX) + thickness, thickness, 100.3f, color);
            return;
        }

        RenderWorkflowRect(bounds, fromX - thickness / 2d, Math.Min(fromY, toY),
            thickness, Math.Abs(toY - fromY) + thickness, 100.3f, color);
    }

    private string WorkflowCanvasToolbarText()
    {
        if (workflowEdit is null)
        {
            return string.Empty;
        }

        WorkflowView view = CurrentWorkflowView(workflowEdit);
        return Lang.Get(
            "vsdirector:workflow-view-status",
            (view.Zoom * 100d).ToString("0", CultureInfo.InvariantCulture),
            view.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            view.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            workflowEdit.Cards.Count);
    }

    private string WorkflowCanvasFooterText()
        => workflowEdit is null
            ? string.Empty
            : WorkflowPlanSummary(workflowEdit, owner.PlanWorkflow(workflowEdit.Id));

    private string WorkflowPlanSummary(
        DirectorWorkflow workflow,
        IReadOnlyList<DirectorWorkflowExportPass> passes)
        => Lang.Get("vsdirector:workflow-plan-summary", workflow.Cards.Count,
            passes.Count,
            passes.Count(pass => pass.SplitForOverlap),
            workflow.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));

    private static string WorkflowTransitionCode(DirectorWorkflowCard card)
        => card.TransitionIn?.Type switch
        {
            DirectorVideoTransitionType.Cut => "cut",
            DirectorVideoTransitionType.CrossDissolve => "cross",
            DirectorVideoTransitionType.FadeFromBlack => "fade",
            _ => "auto"
        };

    private static double WorkflowTransitionDuration(DirectorWorkflowCard card)
        => card.TransitionIn is { Type: not DirectorVideoTransitionType.Cut } transition
            ? Math.Max(0d, transition.DurationSeconds)
            : 0.5d;

    private static string WorkflowAudioSummary(DirectorWorkflow workflow)
    {
        DirectorTimelineAudioClip[] clips = (workflow.AudioClips ?? new List<DirectorTimelineAudioClip>())
            .Where(clip => clip.Enabled)
            .ToArray();
        if (clips.Length == 0)
        {
            return string.Empty;
        }

        return Lang.Get(
            "vsdirector:workflow-audio-summary",
            clips.Length,
            clips.Select(clip => clip.Track).Distinct().Count(),
            string.Join(", ", clips.Take(3).Select(clip => clip.Name)));
    }

    private void RenderTimelineBase(ElementBounds bounds, double fraction, int color)
    {
        float originX = (float)bounds.renderX;
        float originY = (float)bounds.renderY;
        float width = (float)bounds.OuterWidth;
        float height = (float)bounds.OuterHeight;
        capi.Render.RenderRectangle(originX, originY, 100f, width, height, ColorUtil.ToRgba(220, 13, 18, 22));
        capi.Render.RenderRectangle(originX + 16f, originY + height / 2f - 2f, 100.1f,
            Math.Max(1f, width - 32f), 4f, color);
        float markerX = originX + 16f + (width - 32f) * (float)Math.Clamp(fraction, 0d, 1d);
        solidTextureRenderer.Render(markerX - 2f, originY + 8f, 4f, height - 16f, 100.2f,
            ColorUtil.ToRgba(255, 255, 255, 255));
    }

    private int ReplayTimelineTickScale
        => ReplayTimelineTickScaleLevels[Math.Clamp(
            replayTimelineTickScaleIndex,
            0,
            ReplayTimelineTickScaleLevels.Length - 1)];

    private int ReplayTimelineMinorTickStep
        => ReplayTimelineTickScale switch
        {
            30 => 1,
            15 => 2,
            10 => 3,
            5 => 6,
            _ => 30
        };

    private int ReplayTimelineLabelTickStep
        => ReplayTimelineTickScale switch
        {
            30 => 5,
            15 => 10,
            10 => 15,
            5 => 30,
            _ => 150
        };

    private double ReplayTimelinePixelsPerTick
        => ReplayTimelineBasePixelsPerTick / ReplayTimelineMinorTickStep;

    private void ZoomReplayTimeline(double direction, double mouseX)
    {
        if (replayTimelineBounds is null || Math.Abs(direction) <= double.Epsilon)
        {
            return;
        }
        replayTimelineBounds.CalcWorldBounds();
        double left = ReplayTimelineContentLeft(replayTimelineBounds);
        double oldPixelsPerTick = GuiElement.scaled(ReplayTimelinePixelsPerTick);
        double anchorTick = replayTimelineScrollTicks
            + (mouseX - left)
                / DirectorTimelineCoordinateMath.PositivePixelsPerTick(oldPixelsPerTick);
        replayTimelineTickScaleIndex = Math.Clamp(
            replayTimelineTickScaleIndex + (direction > 0d ? -1 : 1),
            0,
            ReplayTimelineTickScaleLevels.Length - 1);
        double newPixelsPerTick = GuiElement.scaled(ReplayTimelinePixelsPerTick);
        replayTimelineScrollTicks = anchorTick
            - (mouseX - left)
                / DirectorTimelineCoordinateMath.PositivePixelsPerTick(newPixelsPerTick);
        ScrollReplayTimeline(0d);
    }

    private double ReplayTimeAt(double mouseX)
    {
        if (replayTimelineBounds is null)
        {
            return owner.ReplayTimeSeconds;
        }
        double left = ReplayTimelineContentLeft(replayTimelineBounds);
        double pixelsPerTick = GuiElement.scaled(ReplayTimelinePixelsPerTick);
        double tick = replayTimelineScrollTicks
            + (mouseX - left)
                / DirectorTimelineCoordinateMath.PositivePixelsPerTick(pixelsPerTick);
        int snappedTick = Math.Clamp(
            (int)Math.Round(tick, MidpointRounding.AwayFromZero),
            0,
            ReplayTimelineDurationTicks());
        return snappedTick / ReplayTimelineTicksPerSecond;
    }

    private (double Start, double End) CurrentReplayExportRange()
    {
        double duration = Math.Max(0d, owner.ReplayDurationSeconds);
        if (replayExportRangeDragMode != ReplayExportRangeDragMode.None)
        {
            return (
                Math.Clamp(replayExportRangeStart, 0d, duration),
                Math.Clamp(replayExportRangeEnd, 0d, duration));
        }
        DirectorReplayViewportTimeline timeline = CurrentReplayViewportTimeline();
        double start = timeline.ExportStartSeconds is double configuredStart
            && double.IsFinite(configuredStart)
                ? Math.Clamp(configuredStart, 0d, duration)
                : 0d;
        double end = timeline.ExportEndSeconds is double configuredEnd
            && double.IsFinite(configuredEnd)
                ? Math.Clamp(configuredEnd, 0d, duration)
                : duration;
        return end > start ? (start, end) : (0d, duration);
    }

    private bool TryBeginReplayExportRangeDrag(double mouseX, double mouseY)
    {
        if (!owner.IsReplayPlaying
            || replayTimelineBounds is null
            || !ReplayTimelineRulerContains(mouseX, mouseY))
        {
            return false;
        }
        (double start, double end) = CurrentReplayExportRange();
        double startX = ReplayTimelineXForSeconds(replayTimelineBounds, start);
        double endX = ReplayTimelineXForSeconds(replayTimelineBounds, end);
        double hitRadius = GuiElement.scaled(8d);
        double startDistance = Math.Abs(mouseX - startX);
        double endDistance = Math.Abs(mouseX - endX);
        if (Math.Min(startDistance, endDistance) > hitRadius)
        {
            return false;
        }

        replayExportRangeDraftTimeline = owner.ReplayViewportTimeline;
        replayExportRangeStart = start;
        replayExportRangeEnd = end;
        replayExportRangeDragMode = startDistance <= endDistance
            ? ReplayExportRangeDragMode.Start
            : ReplayExportRangeDragMode.End;
        replayKeyframeDragTime = replayExportRangeDragMode == ReplayExportRangeDragMode.Start
            ? start
            : end;
        return true;
    }

    private void UpdateReplayExportRangeDrag(double mouseX, bool snapToKeyframe)
    {
        if (replayExportRangeDragMode == ReplayExportRangeDragMode.None)
        {
            return;
        }
        double time = ReplayTimeAt(mouseX);
        if (snapToKeyframe)
        {
            time = NearestReplayElementKeyframeTime(time);
        }
        double minimumDuration = 1d / ReplayTimelineTicksPerSecond;
        if (replayExportRangeDragMode == ReplayExportRangeDragMode.Start)
        {
            replayExportRangeStart = Math.Clamp(
                time,
                0d,
                Math.Max(0d, replayExportRangeEnd - minimumDuration));
            replayKeyframeDragTime = replayExportRangeStart;
        }
        else
        {
            replayExportRangeEnd = Math.Clamp(
                time,
                Math.Min(owner.ReplayDurationSeconds,
                    replayExportRangeStart + minimumDuration),
                owner.ReplayDurationSeconds);
            replayKeyframeDragTime = replayExportRangeEnd;
        }
    }

    private double NearestReplayElementKeyframeTime(double time)
    {
        DirectorReplayViewportTimeline timeline = CurrentReplayViewportTimeline();
        double[] candidates = timeline.Clips
            .Select(clip => clip.TimelineStartSeconds)
            .Concat(timeline.ElementKeyframes.Select(keyframe => keyframe.TimeSeconds))
            .ToArray();
        return candidates.Length == 0
            ? time
            : candidates.OrderBy(candidate => Math.Abs(candidate - time)).First();
    }

    private void FinishReplayExportRangeDrag()
    {
        DirectorReplayViewportTimeline? timeline = replayExportRangeDraftTimeline;
        replayExportRangeDraftTimeline = null;
        replayExportRangeDragMode = ReplayExportRangeDragMode.None;
        replayKeyframeDragTime = double.NaN;
        if (timeline is not null)
        {
            timeline.ExportStartSeconds = replayExportRangeStart;
            timeline.ExportEndSeconds = replayExportRangeEnd;
            timeline.SyncActiveSceneFromTimeline();
            CommitReplayViewportTimeline(timeline);
        }
        ComposeTimeline();
    }

    internal bool TrySetReplayExportRange(double startSeconds, double endSeconds)
    {
        double duration = owner.ReplayDurationSeconds;
        if (!double.IsFinite(startSeconds)
            || !double.IsFinite(endSeconds)
            || startSeconds < 0d
            || endSeconds > duration + 0.000001d
            || endSeconds - startSeconds < 1d / ReplayTimelineTicksPerSecond)
        {
            return false;
        }
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        timeline.ExportStartSeconds = ReplayTimelineTickAt(startSeconds)
            / ReplayTimelineTicksPerSecond;
        timeline.ExportEndSeconds = ReplayTimelineTickAt(endSeconds)
            / ReplayTimelineTicksPerSecond;
        timeline.SyncActiveSceneFromTimeline();
        bool saved = CommitReplayViewportTimeline(timeline);
        if (saved)
        {
            ComposeTimeline();
        }
        return saved;
    }

    private int ReplayTimelineDurationTicks()
        => Math.Max(1, (int)Math.Ceiling(
            Math.Max(0d, owner.ReplayDurationSeconds) * ReplayTimelineTicksPerSecond));

    private static int ReplayTimelineTickAt(double seconds)
        => Math.Max(0, (int)Math.Round(
            Math.Max(0d, seconds) * ReplayTimelineTicksPerSecond,
            MidpointRounding.AwayFromZero));

    private static string FormatReplayTimelineTick(int tick)
    {
        int safeTick = Math.Max(0, tick);
        int wholeSeconds = safeTick / (int)ReplayTimelineTicksPerSecond;
        int minutes = wholeSeconds / 60;
        int hours = minutes / 60;
        string timestamp = hours == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}",
                minutes, wholeSeconds % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                hours, minutes % 60, wholeSeconds % 60);
        return timestamp + "/" + (safeTick % (int)ReplayTimelineTicksPerSecond)
            .ToString(CultureInfo.InvariantCulture);
    }

    private double ReplayTimelineXForSeconds(ElementBounds bounds, double seconds)
        => ReplayTimelineContentLeft(bounds)
            + (seconds * ReplayTimelineTicksPerSecond - replayTimelineScrollTicks)
            * GuiElement.scaled(ReplayTimelinePixelsPerTick);

    private void ScrollReplayTimeline(double deltaTicks)
    {
        if (replayTimelineBounds is null)
        {
            return;
        }

        double visibleTicks = ReplayTimelineContentWidth(replayTimelineBounds)
            / DirectorTimelineCoordinateMath.PositivePixelsPerTick(
                GuiElement.scaled(ReplayTimelinePixelsPerTick));
        replayTimelineScrollTicks = Math.Clamp(
            replayTimelineScrollTicks + deltaTicks,
            0d,
            Math.Max(0d, ReplayTimelineDurationTicks() - visibleTicks));
    }

    private void StepReplayTimelineTick(int direction)
    {
        if (!owner.IsReplayPlaying || replayTimelineBounds is null)
        {
            return;
        }

        int targetTick = Math.Clamp(
            ReplayTimelineTickAt(owner.ReplayTimeSeconds) + Math.Sign(direction),
            0,
            ReplayTimelineDurationTicks());
        owner.SeekReplayFromPanel(targetTick / ReplayTimelineTicksPerSecond);

        double visibleTicks = ReplayTimelineContentWidth(replayTimelineBounds)
            / DirectorTimelineCoordinateMath.PositivePixelsPerTick(
                GuiElement.scaled(ReplayTimelinePixelsPerTick));
        if (targetTick < replayTimelineScrollTicks)
        {
            replayTimelineScrollTicks = targetTick;
        }
        else if (targetTick > replayTimelineScrollTicks + visibleTicks)
        {
            replayTimelineScrollTicks = Math.Max(0d, targetTick - visibleTicks);
        }
        ScrollReplayTimeline(0d);
        ComposeTimeline();
    }

    private bool ReplayTimelineInteractionContains(double mouseX, double mouseY)
    {
        if (replayTimelineBounds is null)
        {
            return false;
        }

        replayTimelineBounds.CalcWorldBounds();
        double contentLeft = ReplayTimelineContentLeft(replayTimelineBounds);
        double contentRight = replayTimelineBounds.renderX + replayTimelineBounds.OuterWidth;
        double top = replayTimelineBounds.renderY;
        double bottom = replayTimelineBounds.renderY + replayTimelineBounds.OuterHeight;
        return mouseX >= contentLeft
            && mouseX <= contentRight
            && mouseY >= top
            && mouseY <= bottom;
    }

    private bool ReplayTimelineRulerContains(double mouseX, double mouseY)
    {
        if (!ReplayTimelineInteractionContains(mouseX, mouseY) || replayTimelineBounds is null)
        {
            return false;
        }

        replayTimelineBounds.CalcWorldBounds();
        double top = replayTimelineBounds.renderY;
        double bottom = top + GuiElement.scaled(ReplayTimelineRulerHeight);
        return mouseY >= top && mouseY < bottom;
    }

    private static double ReplayTimelineContentLeft(ElementBounds bounds)
    {
        bounds.CalcWorldBounds();
        return bounds.renderX + ReplayTimelineLabelColumnWidth(bounds);
    }

    private static double ReplayTimelineContentWidth(ElementBounds bounds)
        => Math.Max(
            GuiElement.scaled(1d),
            bounds.OuterWidth - ReplayTimelineLabelColumnWidth(bounds));

    private bool TryBeginReplayViewportDrag(int mouseX, int mouseY)
    {
        DirectorReplayViewportClip? clip = ReplayViewportClipAt(mouseX, mouseY);
        if (clip is null || replayTimelineBounds is null)
        {
            return false;
        }

        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayViewportClip? draftClip = timeline.Clips.FirstOrDefault(item => item.Id == clip.Id);
        if (draftClip is null)
        {
            return false;
        }

        selectedReplayViewportClipId = draftClip.Id;
        selectedReplayElementTrackId = draftClip.TrackId;
        selectedReplayTimelineElementTrack = ReplayTimelineElementKind.Camera;
        selectedReplayMarkerId = Guid.Empty;
        selectedReplaySpeedKeyframeId = Guid.Empty;
        selectedReplayElementKeyframeId = Guid.Empty;
        timeline.SelectedClipId = draftClip.Id;
        replayViewportDraftTimeline = timeline;
        replayViewportDragClipId = draftClip.Id;
        replayViewportDragStartMouseX = mouseX;
        replayViewportDragOriginalStart = draftClip.TimelineStartSeconds;
        replayViewportDragOriginalDuration = draftClip.DurationSeconds;
        replayViewportDragOriginalSourceStart = draftClip.SourceStartSeconds;
        replayViewportDragMoved = false;

        replayViewportDragMode = ReplayViewportDragMode.Move;
        replayKeyframeDragTime = draftClip.TimelineStartSeconds;
        replayViewportFieldsId = Guid.Empty;
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private DirectorReplayViewportClip? ReplayViewportClipAt(double mouseX, double mouseY)
    {
        DirectorReplayElementTrack? track = ReplayTimelineElementTrackAt(mouseY);
        if (replayTimelineBounds is null
            || track?.TrackType != DirectorReplayElementTrackType.Camera)
        {
            return null;
        }

        DirectorReplayViewportClip? clip = CurrentReplayViewportTimeline().Clips
            .Where(item => item.TrackId == track.Id
                && item.SourceType == DirectorReplayViewportSourceType.StaticCamera)
            .OrderBy(item => Math.Abs(
                ReplayTimelineXForSeconds(replayTimelineBounds, item.TimelineStartSeconds) - mouseX))
            .FirstOrDefault();
        if (clip is null)
        {
            return null;
        }
        double markerX = ReplayTimelineXForSeconds(
            replayTimelineBounds,
            clip.TimelineStartSeconds);
        double hitRadius = GuiElement.scaled(ReplayTimelineTrackHeight * 0.5d);
        return Math.Abs(mouseX - markerX) <= hitRadius ? clip : null;
    }

    private DirectorReplayElementTrack? ReplayTimelineElementTrackAt(double mouseY)
    {
        if (replayTimelineBounds is null)
        {
            return null;
        }

        replayTimelineBounds.CalcWorldBounds();
        double localY = mouseY - replayTimelineBounds.renderY;
        double trackTop = GuiElement.scaled(ReplayTimelineHeaderHeight);
        double rowHeight = GuiElement.scaled(ReplayTimelineTrackHeight);
        if (localY < trackTop || localY >= replayTimelineBounds.OuterHeight)
        {
            return null;
        }

        int trackIndex = TimelineRowAt(mouseY, true);
        IReadOnlyList<DirectorReplayElementTrack> tracks = ActiveReplayTimelineElementTracks();
        return trackIndex >= 0 && trackIndex < tracks.Count ? tracks[trackIndex] : null;
    }

    private bool ReplayTimelineTrackLabelsContain(double mouseX, double mouseY)
    {
        if (replayTimelineBounds is null)
        {
            return false;
        }
        replayTimelineBounds.CalcWorldBounds();
        double localY = mouseY - replayTimelineBounds.renderY;
        return mouseX >= replayTimelineBounds.renderX
            && mouseX < ReplayTimelineContentLeft(replayTimelineBounds)
            && localY >= GuiElement.scaled(ReplayTimelineHeaderHeight)
            && localY < replayTimelineBounds.OuterHeight;
    }

    private void ScrollReplayTimelineTracks(int delta)
    {
        if (replayTimelineBounds is null)
        {
            return;
        }
        var rows = new DirectorTimelineRows(ActiveReplayTimelineElementTracks().Select(t => TrackHeight(t.Id)).Append(40));
        replayTimelineFirstVisibleTrack = Math.Clamp(
            replayTimelineFirstVisibleTrack + delta,
            0,
            rows.MaxFirst(replayTimelineBounds.OuterHeight / GuiElement.scaled(1) - ReplayTimelineHeaderHeight));
    }

    private bool TrySelectReplayViewportTrack(double mouseX, double mouseY)
    {
        if (replayTimelineBounds is null)
        {
            return false;
        }
        replayTimelineBounds.CalcWorldBounds();
        double labelLeft = replayTimelineBounds.renderX + GuiElement.scaled(58d);
        double labelRight = ReplayTimelineContentLeft(replayTimelineBounds) - GuiElement.scaled(90d);
        DirectorReplayElementTrack? track = ReplayTimelineElementTrackAt(mouseY);
        return track is not null
            && mouseX >= labelLeft
            && mouseX <= labelRight
            && SelectReplayTimelineElementTrack(track.Id);
    }

    private bool ReplayTimelineAddRowContains(double mouseY)
    {
        if (replayTimelineBounds is null)
        {
            return false;
        }
        replayTimelineBounds.CalcWorldBounds();
        double localY = mouseY - replayTimelineBounds.renderY;
        double trackTop = GuiElement.scaled(ReplayTimelineHeaderHeight);
        double rowHeight = GuiElement.scaled(ReplayTimelineTrackHeight);
        if (localY < trackTop || localY >= replayTimelineBounds.OuterHeight)
        {
            return false;
        }
        var rows = ReplayRows();
        double addTop = rows.Top(rows.Count) - rows.Top(replayTimelineFirstVisibleTrack);
        double y = TimelineLocalY(mouseY, true);
        return y >= addTop && y < addTop + 40;
    }

    private bool TryBeginReplayElementTrackReorder(double mouseX, double mouseY)
    {
        if (section != DirectorWorkspaceSection.Replay || replayTimelineBounds is null)
        {
            return false;
        }
        replayTimelineBounds.CalcWorldBounds();
        double handleLeft = replayTimelineBounds.renderX + GuiElement.scaled(6d);
        double handleRight = replayTimelineBounds.renderX + GuiElement.scaled(38d);
        DirectorReplayElementTrack? track = ReplayTimelineElementTrackAt(mouseY);
        if (track is null || mouseX < handleLeft || mouseX > handleRight)
        {
            return false;
        }

        // A lost release from a previous button interaction must not steal
        // the mouse-up that completes this row drag.
        DirectorButtonInputCapture.Cancel();
        replayViewportDraftTimeline = owner.ReplayViewportTimeline;
        replayElementTrackReordering = true;
        replayElementTrackReorderId = track.Id;
        replayElementTrackReorderIndex = ActiveReplayTimelineElementTracks()
            .ToList()
            .FindIndex(item => item.Id == track.Id);
        selectedReplayElementTrackId = track.Id;
        selectedReplayTimelineElementTrack = ReplayTimelineElementKindFor(track.TrackType);
        CloseReplayFloatingEditor();
        ComposeTimeline();
        return true;
    }

    private void UpdateReplayElementTrackReorder(double mouseY)
    {
        if (!replayElementTrackReordering
            || replayViewportDraftTimeline is null
            || replayTimelineBounds is null)
        {
            return;
        }
        replayTimelineBounds.CalcWorldBounds();
        int targetIndex = TimelineRowAt(mouseY, true);
        List<DirectorReplayElementTrack> visible = replayViewportDraftTimeline.Tracks
            .Where(track => track.TrackType != DirectorReplayElementTrackType.Marker)
            .ToList();
        if (visible.Count < 2)
        {
            return;
        }
        targetIndex = Math.Clamp(targetIndex, 0, visible.Count - 1);
        int sourceIndex = visible.FindIndex(track => track.Id == replayElementTrackReorderId);
        if (sourceIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }
        DirectorReplayElementTrack moved = visible[sourceIndex];
        visible.RemoveAt(sourceIndex);
        visible.Insert(targetIndex, moved);
        int visibleIndex = 0;
        replayViewportDraftTimeline.Tracks = replayViewportDraftTimeline.Tracks
            .Select(track => track.TrackType == DirectorReplayElementTrackType.Marker
                ? track
                : visible[visibleIndex++])
            .ToList();
        replayViewportDraftTimeline.SyncActiveSceneFromTimeline();
        replayElementTrackReorderIndex = targetIndex;
        ComposeTimeline();
    }

    private void FinishReplayElementTrackReorder()
    {
        DirectorReplayViewportTimeline? timeline = replayViewportDraftTimeline;
        replayElementTrackReordering = false;
        replayElementTrackReorderId = Guid.Empty;
        replayElementTrackReorderIndex = -1;
        replayViewportDraftTimeline = null;
        if (timeline is not null)
        {
            CommitReplayViewportTimeline(timeline);
        }
        ComposeInspector();
        ComposeTimeline();
    }

    private void UpdateReplayViewportDrag(double mouseX)
    {
        if (replayViewportDraftTimeline is null || replayTimelineBounds is null)
        {
            return;
        }

        DirectorReplayViewportClip? clip = replayViewportDraftTimeline.Clips.FirstOrDefault(
            item => item.Id == replayViewportDragClipId);
        if (clip is null)
        {
            return;
        }

        double replayDuration = Math.Max(0.05d, owner.ReplayDurationSeconds);
        double pixelsPerTick = GuiElement.scaled(ReplayTimelinePixelsPerTick);
        replayViewportDragMoved |= Math.Abs(mouseX - replayViewportDragStartMouseX)
            >= GuiElement.scaled(2d);
        double delta = (mouseX - replayViewportDragStartMouseX)
            / DirectorTimelineCoordinateMath.PositivePixelsPerTick(pixelsPerTick)
            / ReplayTimelineTicksPerSecond;
        switch (replayViewportDragMode)
        {
            case ReplayViewportDragMode.Move:
                clip.TimelineStartSeconds = Math.Round(
                    Math.Clamp(
                        replayViewportDragOriginalStart + delta,
                        0d,
                        replayDuration)
                    * ReplayTimelineTicksPerSecond,
                    MidpointRounding.AwayFromZero) / ReplayTimelineTicksPerSecond;
                break;
            case ReplayViewportDragMode.ResizeStart:
                double originalEnd = replayViewportDragOriginalStart + replayViewportDragOriginalDuration;
                double newStart = Math.Clamp(
                    replayViewportDragOriginalStart + delta,
                    0d,
                    Math.Max(0d, originalEnd - 0.05d));
                clip.TimelineStartSeconds = newStart;
                clip.DurationSeconds = originalEnd - newStart;
                clip.SourceStartSeconds = Math.Max(0d,
                    replayViewportDragOriginalSourceStart
                    + (newStart - replayViewportDragOriginalStart) * clip.PlaybackRate);
                break;
            case ReplayViewportDragMode.ResizeEnd:
                clip.DurationSeconds = Math.Clamp(
                    replayViewportDragOriginalDuration + delta,
                    0.05d,
                    Math.Max(0.05d, replayDuration - replayViewportDragOriginalStart));
                break;
        }
        replayKeyframeDragTime = clip.TimelineStartSeconds;
        ComposeTimeline();
    }

    private void FinishReplayViewportDrag()
    {
        DirectorReplayViewportTimeline? timeline = replayViewportDraftTimeline;
        bool openEditor = !replayViewportDragMoved;
        replayViewportDragMode = ReplayViewportDragMode.None;
        replayViewportDragClipId = Guid.Empty;
        replayViewportDraftTimeline = null;
        replayKeyframeDragTime = double.NaN;
        replayViewportDragMoved = false;
        if (timeline is not null)
        {
            CommitReplayViewportTimeline(timeline);
        }
        replayViewportFieldsId = Guid.Empty;
        ComposeInspector();
        ComposeTimeline();
        if (openEditor)
        {
            OpenSelectedReplayCameraKeyframeEditor();
        }
    }

    private void SelectReplaySequenceShotAt(double mouseX)
    {
        if (sequenceEdit is null
            || sequenceEdit.Shots.Count == 0
            || replaySequenceTimelineBounds is null)
        {
            return;
        }
        if (!CaptureInspectorDraft())
        {
            return;
        }
        double left = replaySequenceTimelineBounds.absX + GuiElement.scaled(16d);
        double width = Math.Max(1d, replaySequenceTimelineBounds.OuterWidth - GuiElement.scaled(32d));
        double time = Math.Clamp((mouseX - left) / width, 0d, 1d) * sequenceEdit.DurationSeconds;
        double start = 0d;
        int selectedShot = sequenceEdit.Shots.Count - 1;
        for (int index = 0; index < sequenceEdit.Shots.Count; index++)
        {
            double end = start + sequenceEdit.Shots[index].DurationSeconds;
            if (time < end || index == sequenceEdit.Shots.Count - 1)
            {
                selectedShot = index;
                break;
            }
            start = end;
        }
        replayViewInspectorSection = "shots";
        sequencePageIndex = selectedShot / CurrentSequenceRowsPerPage;
        if (owner.IsReplayPlaying)
        {
            owner.SeekReplayFromPanel(Math.Clamp(
                replayViewStartSeconds + start,
                0d,
                owner.ReplayDurationSeconds));
        }
        ComposeInspector();
        ComposeTimeline();
    }

    private int FindReplayPathViewKeyframeAt(double mouseX)
    {
        if (pathEdit is null
            || pathEdit.Keyframes.Count == 0
            || replayPathViewTimelineBounds is null)
        {
            return -1;
        }
        double left = replayPathViewTimelineBounds.absX + GuiElement.scaled(16d);
        double width = Math.Max(1d, replayPathViewTimelineBounds.OuterWidth - GuiElement.scaled(32d));
        double duration = Math.Max(0.001d, pathEdit.DurationSeconds);
        (CameraKeyframe frame, int index, double distance) nearest = pathEdit.Keyframes
            .Select((frame, index) => (
                frame,
                index,
                distance: Math.Abs(left + width * frame.TimeSeconds / duration - mouseX)))
            .OrderBy(item => item.distance)
            .First();
        return nearest.distance <= GuiElement.scaled(12d) ? nearest.index : -1;
    }

    private Guid? FindReplayMarkerAt(double mouseX, double mouseY)
    {
        if (replayTimelineBounds is null
            || owner.ReplayMarkers.Count == 0
            || ReplayTimelineElementTrackAt(mouseY)?.TrackType
                != DirectorReplayElementTrackType.Marker)
        {
            return null;
        }

        DirectorReplayMarker nearest = owner.ReplayMarkers
            .OrderBy(marker => Math.Abs(
                ReplayTimelineXForSeconds(replayTimelineBounds, marker.TimeSeconds) - mouseX))
            .First();
        double distance = Math.Abs(
            ReplayTimelineXForSeconds(replayTimelineBounds, nearest.TimeSeconds) - mouseX);
        return distance <= GuiElement.scaled(ReplayTimelineTrackHeight * 0.5d)
            ? nearest.Id
            : null;
    }

    private void BeginReplayAuxiliaryKeyframeDrag(
        ReplayAuxiliaryKeyframeDragKind kind,
        Guid id)
    {
        double time = kind switch
        {
            ReplayAuxiliaryKeyframeDragKind.Marker => owner.ReplayMarkers
                .FirstOrDefault(marker => marker.Id == id)?.TimeSeconds ?? double.NaN,
            ReplayAuxiliaryKeyframeDragKind.Speed => owner.ReplaySpeedKeyframes
                .FirstOrDefault(keyframe => keyframe.Id == id)?.TimeSeconds ?? double.NaN,
            ReplayAuxiliaryKeyframeDragKind.Element => CurrentReplayViewportTimeline().ElementKeyframes
                .FirstOrDefault(keyframe => keyframe.Id == id)?.TimeSeconds ?? double.NaN,
            _ => double.NaN
        };
        if (!double.IsFinite(time))
        {
            return;
        }

        replayAuxiliaryKeyframeDragKind = kind;
        replayAuxiliaryKeyframeDragMoved = false;
        DirectorReplayElementKeyframe? element = kind == ReplayAuxiliaryKeyframeDragKind.Element
            ? CurrentReplayViewportTimeline().ElementKeyframes.FirstOrDefault(keyframe => keyframe.Id == id)
            : null;
        bool timelapseEnd = element is not null
            && DirectorReplayTimelapse.IsEnd(element);
        selectedReplayElementTrackId = element?.TrackId ?? Guid.Empty;
        selectedReplayTimelineElementTrack = kind switch
        {
            ReplayAuxiliaryKeyframeDragKind.Marker => ReplayTimelineElementKind.Marker,
            ReplayAuxiliaryKeyframeDragKind.Speed => ReplayTimelineElementKind.Speed,
            ReplayAuxiliaryKeyframeDragKind.Element when element is not null
                => ReplayTimelineElementKindFor(element.TrackType),
            _ => selectedReplayTimelineElementTrack
        };
        replayAuxiliaryKeyframeDragId = id;
        replayKeyframeDragTime = time;
        selectedReplayViewportClipId = Guid.Empty;
        selectedReplayMarkerId = kind == ReplayAuxiliaryKeyframeDragKind.Marker ? id : Guid.Empty;
        selectedReplaySpeedKeyframeId = kind == ReplayAuxiliaryKeyframeDragKind.Speed ? id : Guid.Empty;
        if (kind == ReplayAuxiliaryKeyframeDragKind.Element)
        {
            if (!timelapseEnd)
            {
                selectedReplayElementKeyframeId = id;
            }
            else if (!DirectorReplayTimelapse.TryFindPair(
                    CurrentReplayViewportTimeline().ElementKeyframes,
                    element!.TrackId,
                    id,
                    out DirectorReplayTimelapsePair pair))
            {
                selectedReplayElementKeyframeId = Guid.Empty;
            }
            else
            {
                selectedReplayElementKeyframeId = pair.Start.Id;
            }
        }
        else
        {
            selectedReplayElementKeyframeId = Guid.Empty;
        }
        replayElementFieldsId = Guid.Empty;
        SyncReplayTimelineSelection();
        ComposeInspector();
        ComposeTimeline();
    }

    private void FinishReplayAuxiliaryKeyframeDrag()
    {
        ReplayAuxiliaryKeyframeDragKind kind = replayAuxiliaryKeyframeDragKind;
        Guid id = replayAuxiliaryKeyframeDragId;
        DirectorReplayElementKeyframe? draggedElement = kind == ReplayAuxiliaryKeyframeDragKind.Element
            ? owner.ReplayViewportTimeline.ElementKeyframes.FirstOrDefault(item => item.Id == id)
            : null;
        bool timelapseEnd = draggedElement is not null
            && DirectorReplayTimelapse.IsEnd(draggedElement);
        bool moved = replayAuxiliaryKeyframeDragMoved;
        bool openEditor = kind == ReplayAuxiliaryKeyframeDragKind.Element
            && !timelapseEnd
            && !moved;
        double time = Math.Clamp(replayKeyframeDragTime, 0d, owner.ReplayDurationSeconds);
        replayAuxiliaryKeyframeDragKind = ReplayAuxiliaryKeyframeDragKind.None;
        replayAuxiliaryKeyframeDragId = Guid.Empty;
        replayKeyframeDragTime = double.NaN;
        replayAuxiliaryKeyframeDragMoved = false;

        if (kind == ReplayAuxiliaryKeyframeDragKind.Marker)
        {
            DirectorReplayMarker? marker = owner.ReplayMarkers.FirstOrDefault(item => item.Id == id);
            if (marker is not null)
            {
                owner.TryUpdateReplayMarker(id, marker.Label, time, out _);
            }
            replayMarkerFieldsId = Guid.Empty;
        }
        else if (kind == ReplayAuxiliaryKeyframeDragKind.Speed)
        {
            DirectorReplaySpeedKeyframe? keyframe = owner.ReplaySpeedKeyframes
                .FirstOrDefault(item => item.Id == id);
            if (keyframe is not null)
            {
                owner.TryUpdateReplaySpeedKeyframe(
                    id,
                    time,
                    keyframe.Speed,
                    keyframe.InterpolationToNext,
                    out _);
            }
            replaySpeedFieldsId = Guid.Empty;
        }
        else if (kind == ReplayAuxiliaryKeyframeDragKind.Element)
        {
            DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
            DirectorReplayElementKeyframe? keyframe = timeline.ElementKeyframes.FirstOrDefault(
                item => item.Id == id);
            if (keyframe is not null && (moved || !timelapseEnd))
            {
                keyframe.TimeSeconds = ReplayTimelineTickAt(time) / ReplayTimelineTicksPerSecond;
                CommitReplayViewportTimeline(timeline);
            }
            replayElementFieldsId = Guid.Empty;
        }

        SyncReplayTimelineSelection();
        ComposeInspector();
        ComposeTimeline();
        if (openEditor)
        {
            OpenSelectedReplayElementKeyframeEditor();
        }
    }

    private Guid? FindReplaySpeedKeyframeAt(double mouseX, double mouseY)
    {
        if (replayTimelineBounds is null
            || owner.ReplaySpeedKeyframes.Count == 0
            || ReplayTimelineElementTrackAt(mouseY)?.TrackType
                != DirectorReplayElementTrackType.Speed)
        {
            return null;
        }

        DirectorReplaySpeedKeyframe nearest = owner.ReplaySpeedKeyframes
            .OrderBy(keyframe => Math.Abs(
                ReplayTimelineXForSeconds(replayTimelineBounds, keyframe.TimeSeconds) - mouseX))
            .First();
        double distance = Math.Abs(
            ReplayTimelineXForSeconds(replayTimelineBounds, nearest.TimeSeconds) - mouseX);
        return distance <= GuiElement.scaled(ReplayTimelineTrackHeight * 0.5d)
            ? nearest.Id
            : null;
    }

    private Guid? FindReplayElementKeyframeAt(double mouseX, double mouseY)
    {
        DirectorReplayElementTrack? track = ReplayTimelineElementTrackAt(mouseY);
        if (replayTimelineBounds is null
            || track is null
            || track.TrackType is DirectorReplayElementTrackType.Camera
                or DirectorReplayElementTrackType.Marker)
        {
            return null;
        }
        List<DirectorReplayElementKeyframe> keyframes = CurrentReplayViewportTimeline().ElementKeyframes
            .Where(keyframe => keyframe.TrackId == track.Id)
            .ToList();
        if (keyframes.Count == 0)
        {
            return null;
        }
        DirectorReplayElementKeyframe nearest = keyframes
            .OrderBy(keyframe => Math.Abs(
                ReplayTimelineXForSeconds(replayTimelineBounds, keyframe.TimeSeconds) - mouseX))
            .ThenBy(keyframe => keyframe.TrackType == DirectorReplayElementTrackType.Timelapse
                && keyframe.TimelapseRole == DirectorReplayTimelapseKeyframeRole.End
                    ? 1
                    : 0)
            .First();
        double distance = Math.Abs(
            ReplayTimelineXForSeconds(replayTimelineBounds, nearest.TimeSeconds) - mouseX);
        return distance <= GuiElement.scaled(ReplayTimelineTrackHeight * 0.5d)
            ? nearest.Id
            : null;
    }

    private double PathFullTimelineDuration => Math.Max(1d, pathEdit?.DurationSeconds ?? 1d);

    private double PathGlobalDuration => Math.Max(
        PathFullTimelineDuration,
        owner.IsReplayPlaying ? Math.Max(0d, owner.ReplayDurationSeconds) : 0d);

    private double PathEditorViewDuration
        => Math.Clamp(
            PathFullTimelineDuration / Math.Clamp(layout.PathTimelineZoom, 1d, 64d),
            Math.Min(0.05d, PathFullTimelineDuration),
            PathFullTimelineDuration);

    private double PathEditorViewStart
        => Math.Clamp(
            layout.PathTimelinePanSeconds,
            0d,
            Math.Max(0d, PathFullTimelineDuration - PathEditorViewDuration));

    private void BeginPathEditorTimelineInteraction(int mouseX, int mouseY, bool shift)
    {
        if (pathEdit is null || pathEditorTimelineBounds is null)
        {
            return;
        }
        pendingPathEditHistory = pathEdit.DeepClone();
        if (!CaptureCurrentPathInspectorInputs())
        {
            pendingPathEditHistory = null;
            return;
        }

        double time = PathEditorTimeAt(mouseX);
        pathEditorPlayhead = time;
        pathEditorPlayheadWasPositioned = true;
        if (pathWorkspaceTool == PathWorkspaceTool.Razor)
        {
            if (CameraPathTiming.TrySplitSegment(pathEdit, time, out int insertedIndex))
            {
                SelectOnlyPathKeyframe(insertedIndex);
                pathEditorPlayhead = pathEdit.Keyframes[insertedIndex].TimeSeconds;
                RecordPathEdit(pendingPathEditHistory);
                pendingPathEditHistory = null;
                FinishPathEditorInteraction(recomposeResources: true);
            }
            else
            {
                RecordPathEdit(pendingPathEditHistory);
                pendingPathEditHistory = null;
            }
            return;
        }

        int resizeIndex = FindPathEditorSegmentResizeHandleAt(mouseX, mouseY);
        if (resizeIndex >= 0)
        {
            SelectOnlyPathKeyframe(resizeIndex);
            stretchingPathEditorSegment = resizeIndex;
            return;
        }

        int hit = FindPathEditorKeyframeAt(mouseX, mouseY);
        if (hit >= 0)
        {
            if (shift)
            {
                if (!selectedPathKeyframes.Add(hit))
                {
                    selectedPathKeyframes.Remove(hit);
                }
                if (selectedPathKeyframes.Count == 0)
                {
                    selectedPathKeyframes.Add(hit);
                }
            }
            else if (!selectedPathKeyframes.Contains(hit))
            {
                selectedPathKeyframes.Clear();
                selectedPathKeyframes.Add(hit);
            }
            selectedPathKeyframeIndex = selectedPathKeyframes.Contains(hit)
                ? hit
                : selectedPathKeyframes.Min();
            pathEditorPlayhead = pathEdit.Keyframes[selectedPathKeyframeIndex].TimeSeconds;
            BeginPathEditorKeyframeDrag(mouseX, mouseY, valuesOnly: false);
            SyncPathTimingText();
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
            return;
        }

        if (TryBeginSelectedPathRangeValueDrag(mouseX, mouseY))
        {
            SyncPathTimingText();
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
            return;
        }

        int segment = FindPathEditorSegmentAt(mouseY, time);
        if (segment >= 0)
        {
            SelectOnlyPathKeyframe(segment);
            RecordPathEdit(pendingPathEditHistory);
            pendingPathEditHistory = null;
            SyncPathTimingText();
            owner.SetPathEditPreview(
                pathEdit,
                SelectedPathKeyframeIndex(),
                selectedKeyframes: selectedPathKeyframes);
            return;
        }

        selectingPathEditorRange = true;
        pathEditorRangeAdditive = shift;
        pathEditorRangeMoved = false;
        pathEditorRangeBaseSelection.Clear();
        pathEditorRangeBaseSelection.UnionWith(selectedPathKeyframes);
        if (!shift)
        {
            selectedPathKeyframes.Clear();
            selectedPathKeyframeIndex = -1;
            owner.SetPathEditPreview(pathEdit, selectedKeyframes: selectedPathKeyframes);
        }
        pathEditorDragStartMouseX = mouseX;
        pathEditorDragStartMouseY = mouseY;
        pathEditorBoxStartTime = time;
        pathEditorBoxEndTime = time;
        pathEditorBoxEndMouseY = mouseY;
    }

    private void BeginPathEditorKeyframeDrag(int mouseX, int mouseY, bool valuesOnly)
    {
        if (pathEdit is null)
        {
            return;
        }

        pathEditorDragStartMouseX = mouseX;
        pathEditorDragStartMouseY = mouseY;
        pathEditorDragValuesOnly = valuesOnly;
        (pathEditorDragValueMinimum, pathEditorDragValueMaximum) = PathCurveValueRange();
        pathEditorDragTimes = selectedPathKeyframes
            .Where(index => index >= 0 && index < pathEdit.Keyframes.Count)
            .ToDictionary(index => index, index => pathEdit.Keyframes[index].TimeSeconds);
        pathEditorDragValues = selectedPathKeyframes
            .Where(index => index >= 0 && index < pathEdit.Keyframes.Count)
            .ToDictionary(index => index, index => PathCurveValue(pathEdit.Keyframes[index]));
        draggingPathEditorKeyframes = pathEditorDragValues.Count > 0;
    }

    private bool TryBeginSelectedPathRangeValueDrag(int mouseX, int mouseY)
    {
        if (pathEdit is null
            || pathEditorTimelineBounds is null
            || selectedPathKeyframes.Count < 2)
        {
            return false;
        }

        int[] indices = selectedPathKeyframes
            .Where(index => index >= 0 && index < pathEdit.Keyframes.Count)
            .ToArray();
        if (indices.Length < 2)
        {
            return false;
        }

        double localY = mouseY - pathEditorTimelineBounds.absY;
        double curveTop = GuiElement.scaled(12d);
        double curveBottom = pathEditorTimelineBounds.OuterHeight
            - GuiElement.scaled(33d);
        double time = PathEditorTimeAt(mouseX);
        double minimumTime = indices.Min(index => pathEdit.Keyframes[index].TimeSeconds);
        double maximumTime = indices.Max(index => pathEdit.Keyframes[index].TimeSeconds);
        if (localY < curveTop
            || localY > curveBottom
            || time < minimumTime - CameraPathTiming.TimeMatchEpsilon
            || time > maximumTime + CameraPathTiming.TimeMatchEpsilon)
        {
            return false;
        }

        BeginPathEditorKeyframeDrag(mouseX, mouseY, valuesOnly: true);
        return draggingPathEditorKeyframes;
    }

    private void UpdatePathEditorTimelinePan(int mouseX)
    {
        if (pathEditorTimelineBounds is null)
        {
            return;
        }
        double width = Math.Max(1d,
            pathEditorTimelineBounds.OuterWidth - GuiElement.scaled(36d));
        double delta = (mouseX - pathEditorDragStartMouseX) / width * PathEditorViewDuration;
        layout.PathTimelinePanSeconds = Math.Clamp(
            pathEditorDragStartView - delta,
            0d,
            Math.Max(0d, PathFullTimelineDuration - PathEditorViewDuration));
    }

    private void UpdatePathEditorKeyframeDrag(int mouseX, int mouseY)
    {
        if (pathEdit is null || pathEditorTimelineBounds is null || pathEditorDragTimes.Count == 0)
        {
            return;
        }

        int primary = SelectedPathKeyframeIndex();
        if (!pathEditorDragValuesOnly)
        {
            double width = Math.Max(1d,
                pathEditorTimelineBounds.OuterWidth - GuiElement.scaled(36d));
            double delta = (mouseX - pathEditorDragStartMouseX) / width * PathEditorViewDuration;
            if (pathEditorDragTimes.TryGetValue(primary, out double primaryStart))
            {
                double snapped = SnapPathEditorTime(primaryStart + delta, excludeSelection: true);
                delta = snapped - primaryStart;
            }

            double minimumDelta = double.NegativeInfinity;
            double maximumDelta = double.PositiveInfinity;
            foreach ((int index, double original) in pathEditorDragTimes)
            {
                minimumDelta = Math.Max(minimumDelta, -original);
                if (index > 0 && !pathEditorDragTimes.ContainsKey(index - 1))
                {
                    minimumDelta = Math.Max(minimumDelta,
                        pathEdit.Keyframes[index - 1].TimeSeconds
                        + CameraPathTiming.MinKeyframeGapSeconds - original);
                }
                if (index + 1 < pathEdit.Keyframes.Count && !pathEditorDragTimes.ContainsKey(index + 1))
                {
                    maximumDelta = Math.Min(maximumDelta,
                        pathEdit.Keyframes[index + 1].TimeSeconds
                        - CameraPathTiming.MinKeyframeGapSeconds - original);
                }
                maximumDelta = Math.Min(maximumDelta,
                    CameraPathTiming.MaxTimelineSeconds - original);
            }
            delta = Math.Clamp(delta, minimumDelta, maximumDelta);
            foreach ((int index, double original) in pathEditorDragTimes)
            {
                pathEdit.Keyframes[index].TimeSeconds = original + delta;
            }
        }

        double curveHeight = Math.Max(1d,
            pathEditorTimelineBounds.OuterHeight - GuiElement.scaled(45d));
        double valueDelta = -(mouseY - pathEditorDragStartMouseY)
            / curveHeight * (pathEditorDragValueMaximum - pathEditorDragValueMinimum);
        foreach ((int index, double original) in pathEditorDragValues)
        {
            SetPathCurveValue(pathEdit.Keyframes[index], original + valueDelta);
        }
        CameraPathTiming.NormalizeTimeline(pathEdit.Keyframes);
        if (primary >= 0 && primary < pathEdit.Keyframes.Count)
        {
            pathEditorPlayhead = pathEdit.Keyframes[primary].TimeSeconds;
        }
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            pathEditorPlayhead,
            selectedKeyframes: selectedPathKeyframes);
    }

    private void UpdatePathEditorSegmentStretch(int mouseX)
    {
        if (pathEdit is null
            || stretchingPathEditorSegment < 0
            || stretchingPathEditorSegment >= pathEdit.Keyframes.Count)
        {
            return;
        }

        int index = stretchingPathEditorSegment;
        double targetEnd = SnapPathEditorTime(PathEditorTimeAt(mouseX), excludeSelection: true);
        double duration = Math.Max(
            index + 1 < pathEdit.Keyframes.Count ? CameraPathTiming.MinKeyframeGapSeconds : 0d,
            targetEnd - pathEdit.Keyframes[index].TimeSeconds);
        _ = CameraPathTiming.TrySetKeyframeDuration(pathEdit, index, duration, out _);
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
    }

    private void ApplyPathEditorRangeSelection(bool additive)
    {
        if (pathEdit is null)
        {
            return;
        }

        HashSet<int> next = BuildPathEditorRangeSelection(additive);
        selectedPathKeyframes.Clear();
        selectedPathKeyframes.UnionWith(next);
        selectedPathKeyframeIndex = selectedPathKeyframes.Count > 0
            ? selectedPathKeyframes.Min()
            : -1;
    }

    private void PreviewPathEditorRangeSelection()
    {
        if (pathEdit is null)
        {
            return;
        }

        HashSet<int> next = BuildPathEditorRangeSelection(pathEditorRangeAdditive);
        if (selectedPathKeyframes.SetEquals(next))
        {
            return;
        }

        selectedPathKeyframes.Clear();
        selectedPathKeyframes.UnionWith(next);
        selectedPathKeyframeIndex = selectedPathKeyframes.Count > 0
            ? selectedPathKeyframes.Min()
            : -1;
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            selectedKeyframes: selectedPathKeyframes);
    }

    private HashSet<int> BuildPathEditorRangeSelection(bool additive)
    {
        HashSet<int> selected = additive
            ? new HashSet<int>(pathEditorRangeBaseSelection)
            : new HashSet<int>();
        if (pathEdit is null)
        {
            return selected;
        }
        selected.RemoveWhere(index => index < 0 || index >= pathEdit.Keyframes.Count);

        double minimum = Math.Min(pathEditorBoxStartTime, pathEditorBoxEndTime);
        double maximum = Math.Max(pathEditorBoxStartTime, pathEditorBoxEndTime);
        if (pathEditorTimelineBounds is null)
        {
            return selected;
        }

        double selectionTop = Math.Min(pathEditorDragStartMouseY, pathEditorBoxEndMouseY)
            - pathEditorTimelineBounds.absY;
        double selectionBottom = Math.Max(pathEditorDragStartMouseY, pathEditorBoxEndMouseY)
            - pathEditorTimelineBounds.absY;
        double curveTop = GuiElement.scaled(12d);
        double curveBottom = pathEditorTimelineBounds.OuterHeight - GuiElement.scaled(33d);
        (double curveMinimum, double curveMaximum) = PathCurveValueRange();
        double markerMargin = GuiElement.scaled(6d);
        for (int index = 0; index < pathEdit.Keyframes.Count; index++)
        {
            CameraKeyframe frame = pathEdit.Keyframes[index];
            double time = frame.TimeSeconds;
            double markerY = PathCurveValueToY(
                PathCurveValue(frame),
                curveMinimum,
                curveMaximum,
                curveTop,
                curveBottom);
            if (time >= minimum - CameraPathTiming.TimeMatchEpsilon
                && time <= maximum + CameraPathTiming.TimeMatchEpsilon
                && markerY >= selectionTop - markerMargin
                && markerY <= selectionBottom + markerMargin)
            {
                selected.Add(index);
            }
        }
        return selected;
    }

    private int FindPathEditorKeyframeAt(int mouseX, int mouseY)
    {
        if (pathEdit is null || pathEditorTimelineBounds is null || pathEdit.Keyframes.Count == 0)
        {
            return -1;
        }

        double localX = mouseX - pathEditorTimelineBounds.absX;
        double localY = mouseY - pathEditorTimelineBounds.absY;
        double top = GuiElement.scaled(12d);
        double bottom = pathEditorTimelineBounds.OuterHeight - GuiElement.scaled(33d);
        (double minimum, double maximum) = PathCurveValueRange();
        int nearest = -1;
        double nearestDistance = double.MaxValue;
        for (int index = 0; index < pathEdit.Keyframes.Count; index++)
        {
            CameraKeyframe frame = pathEdit.Keyframes[index];
            if (frame.TimeSeconds < PathEditorViewStart
                || frame.TimeSeconds > PathEditorViewStart + PathEditorViewDuration)
            {
                continue;
            }
            double x = PathEditorTimeToLocalX(frame.TimeSeconds, pathEditorTimelineBounds);
            double y = PathCurveValueToY(PathCurveValue(frame), minimum, maximum, top, bottom);
            double distance = Math.Sqrt((x - localX) * (x - localX) + (y - localY) * (y - localY));
            if (distance <= GuiElement.scaled(14d) && distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    private int FindPathEditorSegmentAt(int mouseY, double time)
    {
        if (pathEdit is null || pathEditorTimelineBounds is null)
        {
            return -1;
        }
        double localY = mouseY - pathEditorTimelineBounds.absY;
        if (localY < pathEditorTimelineBounds.OuterHeight - GuiElement.scaled(25d))
        {
            return -1;
        }
        for (int index = pathEdit.Keyframes.Count - 1; index >= 0; index--)
        {
            if (time >= pathEdit.Keyframes[index].TimeSeconds - CameraPathTiming.TimeMatchEpsilon)
            {
                return index;
            }
        }
        return -1;
    }

    private int FindPathEditorSegmentResizeHandleAt(int mouseX, int mouseY)
    {
        if (pathEdit is null || pathEditorTimelineBounds is null
            || FindPathEditorSegmentAt(mouseY, PathEditorTimeAt(mouseX)) < 0)
        {
            return -1;
        }
        double localX = mouseX - pathEditorTimelineBounds.absX;
        int nearest = -1;
        double nearestDistance = double.MaxValue;
        for (int index = 0; index < pathEdit.Keyframes.Count; index++)
        {
            double end = index + 1 < pathEdit.Keyframes.Count
                ? pathEdit.Keyframes[index + 1].TimeSeconds
                : pathEdit.DurationSeconds;
            if (end < PathEditorViewStart || end > PathEditorViewStart + PathEditorViewDuration)
            {
                continue;
            }
            double x = PathEditorTimeToLocalX(end, pathEditorTimelineBounds);
            double distance = Math.Abs(x - localX);
            if (distance <= GuiElement.scaled(7d) && distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    private double PathEditorTimeAt(double mouseX)
    {
        if (pathEditorTimelineBounds is null)
        {
            return 0d;
        }
        double left = pathEditorTimelineBounds.absX + GuiElement.scaled(18d);
        double width = Math.Max(1d, pathEditorTimelineBounds.OuterWidth - GuiElement.scaled(36d));
        return PathEditorViewStart + Math.Clamp((mouseX - left) / width, 0d, 1d)
            * PathEditorViewDuration;
    }

    private double PathGlobalTimeAt(double mouseX)
    {
        if (pathGlobalTimelineBounds is null)
        {
            return 0d;
        }
        double left = pathGlobalTimelineBounds.absX + GuiElement.scaled(12d);
        double width = Math.Max(1d, pathGlobalTimelineBounds.OuterWidth - GuiElement.scaled(24d));
        return Math.Clamp((mouseX - left) / width, 0d, 1d) * PathGlobalDuration;
    }

    private double PathEditorTimeToLocalX(double time, ElementBounds bounds)
    {
        double left = GuiElement.scaled(18d);
        double width = Math.Max(1d, bounds.OuterWidth - left * 2d);
        return left + width * Math.Clamp(
            (time - PathEditorViewStart) / Math.Max(0.05d, PathEditorViewDuration),
            0d,
            1d);
    }

    private void SetPathEditorPlayhead(double time, bool centerView)
    {
        pathEditorPlayhead = Math.Clamp(time, 0d, PathGlobalDuration);
        if (centerView && pathEditorPlayhead <= PathFullTimelineDuration)
        {
            layout.PathTimelinePanSeconds = Math.Clamp(
                pathEditorPlayhead - PathEditorViewDuration / 2d,
                0d,
                Math.Max(0d, PathFullTimelineDuration - PathEditorViewDuration));
        }
        if (owner.IsReplayPlaying)
        {
            owner.SeekReplayFromPanel(Math.Min(pathEditorPlayhead, owner.ReplayDurationSeconds));
        }
    }

    private bool ZoomPathEditorTimeline(double factor)
        => ZoomPathEditorTimeline(factor, pathEditorPlayhead);

    private bool ZoomPathEditorTimeline(double factor, double anchorTime)
    {
        double oldDuration = PathEditorViewDuration;
        double oldStart = PathEditorViewStart;
        double anchorFraction = oldDuration <= 0d
            ? 0.5d
            : Math.Clamp((anchorTime - oldStart) / oldDuration, 0d, 1d);
        layout.PathTimelineZoom = Math.Clamp(layout.PathTimelineZoom * factor, 1d, 64d);
        double newDuration = PathEditorViewDuration;
        layout.PathTimelinePanSeconds = Math.Clamp(
            anchorTime - anchorFraction * newDuration,
            0d,
            Math.Max(0d, PathFullTimelineDuration - newDuration));
        ComposeWorkflowCanvas();
        return true;
    }

    private bool FitPathEditorTimeline()
    {
        layout.PathTimelineZoom = 1d;
        layout.PathTimelinePanSeconds = 0d;
        ComposeWorkflowCanvas();
        return true;
    }

    private void PanPathEditorTimeline(double deltaSeconds)
    {
        layout.PathTimelinePanSeconds = Math.Clamp(
            PathEditorViewStart + deltaSeconds,
            0d,
            Math.Max(0d, PathFullTimelineDuration - PathEditorViewDuration));
        ComposeWorkflowCanvas();
    }

    private double SnapPathEditorTime(double requested, bool excludeSelection)
    {
        double clamped = Math.Clamp(requested, 0d, CameraPathTiming.MaxTimelineSeconds);
        if (!layout.PathTimelineSnap || pathEdit is null)
        {
            return Math.Round(clamped, 3, MidpointRounding.AwayFromZero);
        }

        double tolerance = PathEditorViewDuration / Math.Max(
            1d,
            (pathEditorTimelineBounds?.OuterWidth ?? 400d) - GuiElement.scaled(36d))
            * GuiElement.scaled(10d);
        List<double> candidates = new() { Math.Round(clamped) };
        candidates.AddRange(pathEdit.Keyframes
            .Select((frame, index) => (frame, index))
            .Where(item => !excludeSelection || !selectedPathKeyframes.Contains(item.index))
            .Select(item => item.frame.TimeSeconds));
        if (owner.IsReplayPlaying)
        {
            candidates.AddRange(owner.ReplayMarkers.Select(marker => marker.TimeSeconds));
        }
        double nearest = candidates.OrderBy(candidate => Math.Abs(candidate - clamped)).First();
        return Math.Abs(nearest - clamped) <= tolerance
            ? nearest
            : Math.Round(clamped, 3, MidpointRounding.AwayFromZero);
    }

    private (double Minimum, double Maximum) PathCurveValueRange()
    {
        if (pathCurveChannelCode == "fov")
        {
            return (10d, 170d);
        }
        if (pathEdit is null || pathEdit.Keyframes.Count == 0)
        {
            return (-1d, 1d);
        }
        double minimum = pathEdit.Keyframes.Min(PathCurveValue);
        double maximum = pathEdit.Keyframes.Max(PathCurveValue);
        if (pathCurveChannelCode is "yaw" or "roll")
        {
            minimum = Math.Min(minimum, -180d);
            maximum = Math.Max(maximum, 180d);
        }
        else if (pathCurveChannelCode == "pitch")
        {
            minimum = Math.Min(minimum, -90d);
            maximum = Math.Max(maximum, 270d);
        }
        double span = maximum - minimum;
        double padding = span > 0.000001d
            ? Math.Max(1d, span * 0.12d)
            : Math.Max(5d, Math.Abs(minimum) * 0.05d);
        return (minimum - padding, maximum + padding);
    }

    private double PathCurveValue(CameraKeyframe frame)
    {
        (double displayX, double displayY, double displayZ) = owner.DisplayPosition(frame.X, frame.Y, frame.Z);
        return pathCurveChannelCode switch
        {
            "y" => displayY,
            "z" => displayZ,
            "yaw" => frame.Yaw,
            "pitch" => frame.Pitch,
            "roll" => frame.Roll,
            "fov" => frame.Fov,
            _ => displayX
        };
    }

    private void SetPathCurveValue(CameraKeyframe frame, double value)
    {
        if (!double.IsFinite(value))
        {
            return;
        }
        (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
            frame.X, frame.Y, frame.Z);
        switch (pathCurveChannelCode)
        {
            case "y":
                frame.Y += value - displayY;
                break;
            case "z":
                frame.Z += value - displayZ;
                break;
            case "yaw":
                frame.Yaw = CameraPathOrientation.PreserveDegrees(value, frame.Yaw);
                break;
            case "pitch":
                frame.Pitch = CameraPathOrientation.PreserveDegrees(value, frame.Pitch);
                break;
            case "roll":
                frame.Roll = CameraPathOrientation.PreserveDegrees(value, frame.Roll);
                break;
            case "fov":
                frame.Fov = (float)Math.Clamp(value, 10d, 170d);
                break;
            default:
                frame.X += value - displayX;
                break;
        }
    }

    private static double PathCurveValueToY(
        double value,
        double minimum,
        double maximum,
        double top,
        double bottom)
    {
        double fraction = Math.Clamp((value - minimum) / Math.Max(0.000001d, maximum - minimum), 0d, 1d);
        return bottom - fraction * (bottom - top);
    }

    private void SelectOnlyPathKeyframe(int index)
    {
        selectedPathKeyframes.Clear();
        if (pathEdit is not null && index >= 0 && index < pathEdit.Keyframes.Count)
        {
            selectedPathKeyframes.Add(index);
            selectedPathKeyframeIndex = index;
        }
        else
        {
            selectedPathKeyframeIndex = -1;
        }
    }

    private void FinishPathEditorInteraction(bool recomposeResources)
    {
        pathEditorHoverTime = double.NaN;
        if (pathEdit is not null)
        {
            CameraPathTiming.NormalizeTimeline(pathEdit.Keyframes);
        }
        SyncPathTimingText();
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            pathEditorPlayhead,
            selectedKeyframes: selectedPathKeyframes);
        if (recomposeResources)
        {
            ComposeResources();
        }
        ComposeInspector();
        ComposeTimeline();
    }

    private void ResetPathEditorInteraction()
    {
        draggingPathEditorKeyframes = false;
        pathEditorDragValuesOnly = false;
        selectingPathEditorRange = false;
        pathEditorRangeAdditive = false;
        pathEditorRangeMoved = false;
        pathEditorRangeBaseSelection.Clear();
        panningPathEditorTimeline = false;
        draggingPathEditorPlayhead = false;
        draggingWorldPathKeyframe = false;
        stretchingPathEditorSegment = -1;
        pathEditorHoverTime = double.NaN;
        pathEditorDragTimes.Clear();
        pathEditorDragValues.Clear();
        pendingPathEditHistory = null;
        pathEditorPlayheadWasPositioned = false;
        pathGlobalTimelineBounds = null;
        pathEditorTimelineBounds = null;
    }

    private bool TryBeginWorldPathKeyframeDrag(int mouseX, int mouseY, bool vertical)
    {
        if (pathEdit is null || !pathEdit.PreviewVisible || !PointInsideGameViewport(mouseX, mouseY))
        {
            return false;
        }

        int nearest = -1;
        PathWorldDragTarget nearestTarget = PathWorldDragTarget.Keyframe;
        double nearestDistance = double.MaxValue;
        void Consider(int index, PathWorldDragTarget target, double x, double y, double z)
        {
            Vec3d projected = MatrixToolsd.Project(
                new Vec3d(x, y, z),
                capi.Render.PerspectiveProjectionMat,
                capi.Render.PerspectiveViewMat,
                capi.Render.FrameWidth,
                capi.Render.FrameHeight);
            double gameScreenY = capi.Render.FrameHeight - projected.Y;
            if (!DirectorWorkspaceViewportRuntime.TryMapGamePointToPhysical(
                    projected.X,
                    gameScreenY,
                    out double screenX,
                    out double screenY))
            {
                return;
            }
            double distance = Math.Sqrt(
                (screenX - mouseX) * (screenX - mouseX)
                + (screenY - mouseY) * (screenY - mouseY));
            if (distance <= GuiElement.scaled(18d) && distance < nearestDistance)
            {
                nearest = index;
                nearestTarget = target;
                nearestDistance = distance;
            }
        }

        for (int index = 0; index < pathEdit.Keyframes.Count; index++)
        {
            CameraKeyframe frame = pathEdit.Keyframes[index];
            Consider(index, PathWorldDragTarget.Keyframe,
                frame.X,
                frame.Y + frame.Dimension * 32768d,
                frame.Z);
        }
        CameraKeyframe? selected = SelectedPathFrame();
        if (selected is not null && selected.BezierHandlesEnabled)
        {
            int selectedIndex = SelectedPathKeyframeIndex();
            Consider(selectedIndex, PathWorldDragTarget.BezierIn,
                selected.X + selected.BezierInX,
                selected.Y + selected.BezierInY + selected.Dimension * 32768d,
                selected.Z + selected.BezierInZ);
            Consider(selectedIndex, PathWorldDragTarget.BezierOut,
                selected.X + selected.BezierOutX,
                selected.Y + selected.BezierOutY + selected.Dimension * 32768d,
                selected.Z + selected.BezierOutZ);
        }
        if (nearest < 0)
        {
            return false;
        }

        if (nearestTarget == PathWorldDragTarget.Keyframe)
        {
            SelectOnlyPathKeyframe(nearest);
        }
        else
        {
            selectedPathKeyframeIndex = nearest;
            selectedPathKeyframes.Add(nearest);
        }
        draggingWorldPathKeyframe = true;
        worldPathVerticalDrag = vertical;
        pathWorldDragTarget = nearestTarget;
        pathEditorDragStartMouseX = mouseX;
        pathEditorDragStartMouseY = mouseY;
        CameraKeyframe dragFrame = pathEdit.Keyframes[nearest];
        pathWorldDragStartY = nearestTarget switch
        {
            PathWorldDragTarget.BezierIn => dragFrame.Y + dragFrame.BezierInY,
            PathWorldDragTarget.BezierOut => dragFrame.Y + dragFrame.BezierOutY,
            _ => dragFrame.Y
        };
        pathEditorPlayhead = pathEdit.Keyframes[nearest].TimeSeconds;
        owner.SetPathEditPreview(
            pathEdit,
            nearest,
            selectedKeyframes: selectedPathKeyframes);
        ComposeResources();
        ComposeInspector();
        ComposeWorkflowCanvas();
        return true;
    }

    private void UpdateWorldPathKeyframeDrag(int mouseX, int mouseY, bool vertical)
    {
        int index = SelectedPathKeyframeIndex();
        if (pathEdit is null || index < 0)
        {
            return;
        }
        CameraKeyframe frame = pathEdit.Keyframes[index];
        if (vertical)
        {
            double y = pathWorldDragStartY + (pathEditorDragStartMouseY - mouseY) * 0.03d;
            if (pathWorldDragTarget == PathWorldDragTarget.BezierIn)
            {
                frame.BezierInY = y - frame.Y;
            }
            else if (pathWorldDragTarget == PathWorldDragTarget.BezierOut)
            {
                frame.BezierOutY = y - frame.Y;
            }
            else
            {
                frame.Y = y;
            }
        }
        else if (TryGetWorldRay(mouseX, mouseY, out Vec3d rayStart, out Vec3d rayEnd))
        {
            double handleY = pathWorldDragTarget switch
            {
                PathWorldDragTarget.BezierIn => frame.BezierInY,
                PathWorldDragTarget.BezierOut => frame.BezierOutY,
                _ => 0d
            };
            double planeY = frame.Y + handleY + frame.Dimension * 32768d;
            double directionY = rayEnd.Y - rayStart.Y;
            if (Math.Abs(directionY) > 0.000001d)
            {
                double amount = (planeY - rayStart.Y) / directionY;
                if (amount > 0d)
                {
                    double x = rayStart.X + (rayEnd.X - rayStart.X) * amount;
                    double z = rayStart.Z + (rayEnd.Z - rayStart.Z) * amount;
                    if (pathWorldDragTarget == PathWorldDragTarget.BezierIn)
                    {
                        frame.BezierInX = x - frame.X;
                        frame.BezierInZ = z - frame.Z;
                    }
                    else if (pathWorldDragTarget == PathWorldDragTarget.BezierOut)
                    {
                        frame.BezierOutX = x - frame.X;
                        frame.BezierOutZ = z - frame.Z;
                    }
                    else
                    {
                        frame.X = x;
                        frame.Z = z;
                    }
                }
            }
        }
        owner.SetPathEditPreview(
            pathEdit,
            index,
            selectedKeyframes: selectedPathKeyframes);
    }

    private bool TryGetWorldRay(int mouseX, int mouseY, out Vec3d start, out Vec3d end)
    {
        if (!DirectorWorkspaceViewportRuntime.TryMapPhysicalGamePoint(
                mouseX,
                mouseY,
                out double gameX,
                out double gameY))
        {
            start = new Vec3d();
            end = new Vec3d();
            return false;
        }
        double[] viewport = { 0d, 0d, capi.Render.FrameWidth, capi.Render.FrameHeight };
        double[] near = new double[4];
        double[] far = new double[4];
        Vintagestory.Client.NoObf.Unproject unproject = new();
        bool nearOk = unproject.UnProject(
            (int)Math.Round(gameX),
            (int)Math.Round(capi.Render.FrameHeight - gameY),
            0,
            capi.Render.PerspectiveViewMat,
            capi.Render.PerspectiveProjectionMat,
            viewport,
            near);
        bool farOk = unproject.UnProject(
            (int)Math.Round(gameX),
            (int)Math.Round(capi.Render.FrameHeight - gameY),
            1,
            capi.Render.PerspectiveViewMat,
            capi.Render.PerspectiveProjectionMat,
            viewport,
            far);
        start = new Vec3d(near[0], near[1], near[2]);
        end = new Vec3d(far[0], far[1], far[2]);
        return nearOk && farOk;
    }

    private static bool PointInsideGameViewport(int x, int y)
        => DirectorWorkspaceViewportRuntime.ContainsPhysicalGamePoint(x, y);

    private void EndReplayViewportFreeCameraPointerInput()
    {
        if (!replayViewportFreeCameraPointerActive)
        {
            return;
        }
        replayViewportFreeCameraPointerActive = false;
        owner.SetReplayViewportFreeCameraPointerActive(false);
    }

    private bool IsShiftDown()
    {
        bool[] keys = capi.Input.KeyboardKeyStateRaw;
        int left = (int)GlKeys.LShift;
        int right = (int)GlKeys.RShift;
        return (uint)left < (uint)keys.Length && keys[left]
            || (uint)right < (uint)keys.Length && keys[right];
    }

    private double PathTimeAt(double mouseX)
    {
        if (pathTimelineBounds is null || pathEdit is null)
        {
            return 0d;
        }
        double left = pathTimelineBounds.absX + GuiElement.scaled(16d);
        double width = Math.Max(1d, pathTimelineBounds.OuterWidth - GuiElement.scaled(32d));
        return Math.Clamp((mouseX - left) / width, 0d, 1d) * Math.Max(1d, pathEdit.DurationSeconds);
    }

    private int FindPathKeyframeAt(double mouseX)
    {
        if (pathEdit is null || pathTimelineBounds is null || pathEdit.Keyframes.Count == 0)
        {
            return -1;
        }

        double time = PathTimeAt(mouseX);
        var nearest = pathEdit.Keyframes
            .Select((frame, index) => (frame, index, distance: Math.Abs(frame.TimeSeconds - time)))
            .OrderBy(item => item.distance)
            .First();
        double secondsPerPixel = Math.Max(1d, pathEdit.DurationSeconds)
            / Math.Max(1d, pathTimelineBounds.OuterWidth - GuiElement.scaled(32d));
        return nearest.distance <= secondsPerPixel * GuiElement.scaled(14d)
            ? nearest.index
            : -1;
    }

    private bool TryBeginReplaySplitDrag(int x, int y)
    {
        if (!HasReplayViewEditor)
        {
            return false;
        }
        if (replayResourceSplitBounds?.PointInside(x, y) == true)
        {
            replaySplitDragTarget = ReplaySplitDragTarget.Resources;
        }
        else if (replayInspectorSplitBounds?.PointInside(x, y) == true)
        {
            replaySplitDragTarget = ReplaySplitDragTarget.Inspector;
        }
        else
        {
            return false;
        }

        if (!CaptureInspectorDraft())
        {
            replaySplitDragTarget = ReplaySplitDragTarget.None;
            return false;
        }
        PreserveInputs();
        replaySplitRecomposeAccumulator = 0f;
        replaySplitRecomposePending = false;
        return true;
    }

    private void UpdateReplaySplitDrag(int mouseY)
    {
        string panelId = replaySplitDragTarget == ReplaySplitDragTarget.Resources
            ? "resources"
            : "inspector";
        GuiComposer? composer = PanelComposer(panelId);
        if (composer is null)
        {
            return;
        }
        DirectorWorkspacePanelLayout panel = layout.Panel(panelId);
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        double localY = (mouseY - composer.Bounds.absY) / scale;
        const double start = 38d;
        double usable = Math.Max(1d, panel.Height - 8d - start - ReplaySplitDividerHeight);
        double ratio = Math.Clamp((localY - start) / usable, 0.18d, 0.82d);
        if (replaySplitDragTarget == ReplaySplitDragTarget.Resources)
        {
            layout.ReplayResourceSplitRatio = ratio;
        }
        else
        {
            layout.ReplayInspectorSplitRatio = ratio;
        }
        replaySplitRecomposePending = true;
        layoutSaveAccumulator = 0f;
    }

    private double paneSplitPointerOffsetY;

    private void CheckPaneSplitRelease()
    {
        if (paneSplitDragging && !capi.Input.MouseButton.Left) FinishPaneSplitDrag();
    }

    private void FinishPaneSplitDrag()
    {
        paneSplitDragging = false;
        if (section is DirectorWorkspaceSection.Paths or DirectorWorkspaceSection.Replay)
        {
            UpdateInspectorBounds();
            ComposeRmlTimeline();
        }
        else ComposeAll();
        SaveLayout();
    }

    private bool TryBeginPaneSplitDrag(int x, int y)
    {
        if (imGuiWorkspace.HasDockLayout || section == DirectorWorkspaceSection.LiveOffscreen) return false;

        if (!IsPaneIntersectionPoint(x, y))
        {
            return false;
        }

        if (!CaptureInspectorDraft())
        {
            return false;
        }
        PreserveInputs();
        var preview = layout.Panel("preview");
        paneSplitPointerOffsetY = y / Math.Max(.001, GuiElement.scaled(1d)) - (preview.Y + preview.Height);
        paneSplitDragging = true;
        return true;
    }

    private void UpdatePaneSplitDrag(int mouseX, int mouseY)
    {
        if (!paneSplitDragging) return;
        if (!capi.Input.MouseButton.Left) { FinishPaneSplitDrag(); return; }
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        double screenWidth = capi.Render.FrameWidth / scale;
        double screenHeight = capi.Render.FrameHeight / scale;
        DirectorWorkspacePanelLayout preview = layout.Panel("preview");
        preview.Height = mouseY / scale - paneSplitPointerOffsetY - DirectorWorkspacePanelLayout.ToolbarHeight;
        ApplySynchronizedPaneLayout(screenWidth, screenHeight);
        foreach (string id in new[] { "preview", "inspector", "timeline", "workflow" })
        {
            MovePanelComposer(layout.Panel(id));
        }
        UpdateGameViewportRuntime();
        UpdateInspectorBounds();
        rmlTimelineDirty = true;
        layoutSaveAccumulator = 0f;
    }

    private void RenderPaneIntersectionHandle()
    {
        if (imGuiWorkspace.HasDockLayout) return;
        // The retained timeline owns the permanent Tabler corner handle for these editors.
        if (section is DirectorWorkspaceSection.Paths or DirectorWorkspaceSection.Replay) return;
        if (section == DirectorWorkspaceSection.LiveOffscreen)
        {
            return;
        }
        if (PointInsideFloatingEditor(capi.Input.MouseX, capi.Input.MouseY))
        {
            return;
        }

        DirectorWorkspacePanelLayout preview = layout.Panel("preview");
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        double centerX = (preview.X + preview.Width) * scale;
        double centerY = (preview.Y + preview.Height) * scale;
        bool active = paneSplitDragging || IsPaneIntersectionPoint(capi.Input.MouseX, capi.Input.MouseY);
        int color = active ? ColorUtil.ToRgba(255, 138, 203, 255) : ColorUtil.ToRgba(255, 183, 188, 194);
        int background = active ? ColorUtil.ToRgba(255, 62, 67, 73) : ColorUtil.ToRgba(255, 36, 36, 36);
        double size = 20d * scale;
        // Match the timeline's permanent Tabler handle before any replay is opened.
        solidTextureRenderer.Render(centerX - size, centerY - size, size, size, 902f, background);
        timelineMarkerRenderer.RenderResizeHandle(centerX, centerY, size, 903f, color);
    }


    private bool IsPaneIntersectionPoint(double x, double y)
    {
        if (imGuiWorkspace.HasDockLayout) return false;
        DirectorWorkspacePanelLayout preview = layout.Panel("preview");
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        double splitX = (preview.X + preview.Width) * scale;
        double splitY = (preview.Y + preview.Height) * scale;
        double handleSize = GuiElement.scaled(20d);
        return x >= splitX - handleSize && x <= splitX && y >= splitY - handleSize && y <= splitY;
    }


    private bool TryBeginPanelDrag(int x, int y)
    {
        foreach (string id in new[] { "resources" })
        {
            DirectorWorkspacePanelLayout panel = layout.Panel(id);
            if (panel.Locked)
            {
                continue;
            }

            GuiComposer? composer = PanelComposer(id);
            if (composer is null || !composer.Bounds.PointInside(x, y))
            {
                continue;
            }

            double scale = GuiElement.scaled(1d);
            double resizeMargin = GuiElement.scaled(10d);
            double left = composer.Bounds.absX;
            double top = composer.Bounds.absY;
            double right = left + composer.Bounds.OuterWidth;
            double bottom = top + composer.Bounds.OuterHeight;
            PanelResizeEdge edges = PanelResizeEdge.None;
            if (x <= left + resizeMargin)
            {
                edges |= PanelResizeEdge.Left;
            }
            else if (x >= right - resizeMargin)
            {
                edges |= PanelResizeEdge.Right;
            }
            if (y <= top + resizeMargin)
            {
                edges |= PanelResizeEdge.Top;
            }
            else if (y >= bottom - resizeMargin)
            {
                edges |= PanelResizeEdge.Bottom;
            }
            edges = panel.Dock switch
            {
                DirectorWorkspaceDock.Left => edges & PanelResizeEdge.Right,
                DirectorWorkspaceDock.Right => edges & PanelResizeEdge.Left,
                DirectorWorkspaceDock.Bottom or DirectorWorkspaceDock.Center => edges & PanelResizeEdge.Top,
                DirectorWorkspaceDock.Top => PanelResizeEdge.None,
                _ when string.Equals(id, "toolbar", StringComparison.OrdinalIgnoreCase)
                    => edges & (PanelResizeEdge.Left | PanelResizeEdge.Right),
                _ => edges
            };
            if (string.Equals(id, "toolbar", StringComparison.OrdinalIgnoreCase)
                && panel.Dock == DirectorWorkspaceDock.Floating)
            {
                edges &= PanelResizeEdge.Left | PanelResizeEdge.Right;
            }
            bool resize = edges != PanelResizeEdge.None;
            bool header = panel.Dock == DirectorWorkspaceDock.Floating
                && IsPanelHeaderDragArea(id, x, y, composer.Bounds);
            if (!resize && !header)
            {
                continue;
            }

            if (!CaptureInspectorDraft())
            {
                return false;
            }
            PreserveInputs();

            drag = new DragState(id, x, y, panel.X, panel.Y, panel.Width, panel.Height, edges, scale);
            snapPreview = null;
            return true;
        }

        return false;
    }

    private static bool IsPanelHeaderDragArea(string id, double x, double y, ElementBounds bounds)
    {
        double right = bounds.absX + bounds.OuterWidth;
        if (string.Equals(id, "toolbar", StringComparison.OrdinalIgnoreCase))
        {
            return y >= bounds.absY + GuiElement.scaled(36d)
                && y <= bounds.absY + bounds.OuterHeight;
        }

        return y <= bounds.absY + GuiElement.scaled(32d)
            && x <= Math.Min(
                right - GuiElement.scaled(142d),
                bounds.absX + GuiElement.scaled(260d));
    }

    private void UpdatePanelDrag(MouseEvent args)
    {
        DragState? current = drag;
        if (current is null)
        {
            return;
        }

        DirectorWorkspacePanelLayout panel = layout.Panel(current.PanelId);
        DirectorWorkspaceDock originalDock = panel.Dock;
        double dx = (args.X - current.StartX) / current.Scale;
        double dy = (args.Y - current.StartY) / current.Scale;
        if (current.Resize)
        {
            double x = current.StartPanelX;
            double y = current.StartPanelY;
            double width = current.StartWidth;
            double height = current.StartHeight;
            if ((current.Edges & PanelResizeEdge.Left) != 0)
            {
                width = current.StartWidth - dx;
                x = current.StartPanelX + dx;
            }
            if ((current.Edges & PanelResizeEdge.Right) != 0)
            {
                width = current.StartWidth + dx;
            }
            if ((current.Edges & PanelResizeEdge.Top) != 0)
            {
                height = current.StartHeight - dy;
                y = current.StartPanelY + dy;
            }
            if ((current.Edges & PanelResizeEdge.Bottom) != 0)
            {
                height = current.StartHeight + dy;
            }

            double screenWidth = capi.Render.FrameWidth / GuiElement.scaled(1d);
            double screenHeight = capi.Render.FrameHeight / GuiElement.scaled(1d);
            double availableWidth = Math.Max(1d, screenWidth - 16d);
            double availableHeight = Math.Max(1d, screenHeight - 16d);
            double minimumWidth = Math.Min(PanelMinimumWidth(current.PanelId), availableWidth);
            double minimumHeight = Math.Min(PanelMinimumHeight(current.PanelId), availableHeight);
            double maximumWidth = Math.Max(minimumWidth,
                Math.Min(PanelMaximumWidth(current.PanelId), availableWidth));
            double maximumHeight = ViewportMaximumPanelHeight(
                current.PanelId,
                minimumHeight,
                availableHeight);
            double clampedWidth = Math.Clamp(width, minimumWidth, maximumWidth);
            double clampedHeight = Math.Clamp(height, minimumHeight, maximumHeight);
            if ((current.Edges & PanelResizeEdge.Left) != 0)
            {
                x = current.StartPanelX + current.StartWidth - clampedWidth;
            }
            if ((current.Edges & PanelResizeEdge.Top) != 0)
            {
                y = current.StartPanelY + current.StartHeight - clampedHeight;
            }

            panel.Width = clampedWidth;
            panel.Height = clampedHeight;
            if (originalDock == DirectorWorkspaceDock.Floating)
            {
                double topBoundary = string.Equals(
                    current.PanelId,
                    "toolbar",
                    StringComparison.OrdinalIgnoreCase)
                    ? 0d
                    : DirectorWorkspacePanelLayout.ToolbarHeight;
                panel.X = Math.Clamp(x, 0d, Math.Max(0d, screenWidth - panel.Width));
                panel.Y = Math.Clamp(
                    y,
                    topBoundary,
                    Math.Max(topBoundary, screenHeight - panel.Height));
            }
            else
            {
                panel.Dock = originalDock;
                ApplyDock(panel);
            }
            snapPreview = null;
        }
        else
        {
            double screenWidth = capi.Render.FrameWidth / GuiElement.scaled(1d);
            double screenHeight = capi.Render.FrameHeight / GuiElement.scaled(1d);
            double visibleHeight = panel.Height;
            double requestedX = Math.Clamp(
                current.StartPanelX + dx,
                0d,
                Math.Max(0d, screenWidth - panel.Width));
            double topBoundary = string.Equals(
                current.PanelId,
                "toolbar",
                StringComparison.OrdinalIgnoreCase)
                ? 0d
                : DirectorWorkspacePanelLayout.ToolbarHeight;
            double requestedY = Math.Clamp(
                current.StartPanelY + dy,
                topBoundary,
                Math.Max(topBoundary, screenHeight - visibleHeight));
            ApplyAutomaticSnap(panel, requestedX, requestedY, visibleHeight, screenWidth, screenHeight);
            panel.Dock = DirectorWorkspaceDock.Floating;
            MovePanelComposer(panel);
        }

        if (string.Equals(current.PanelId, "preview", StringComparison.OrdinalIgnoreCase))
        {
            UpdateGameViewportRuntime();
        }
        layoutSaveAccumulator = 0f;
    }

    private void ApplyAutomaticSnap(
        DirectorWorkspacePanelLayout panel,
        double requestedX,
        double requestedY,
        double visibleHeight,
        double screenWidth,
        double screenHeight)
    {
        const double threshold = 14d;
        double topBoundary = string.Equals(panel.Id, "toolbar", StringComparison.OrdinalIgnoreCase)
            ? 0d
            : DirectorWorkspacePanelLayout.ToolbarHeight;
        List<SnapCandidate> horizontal = new()
        {
            new(0d, 0d),
            new((screenWidth - panel.Width) / 2d, screenWidth / 2d),
            new(Math.Max(0d, screenWidth - panel.Width), screenWidth)
        };
        List<SnapCandidate> vertical = new()
        {
            new(topBoundary, topBoundary),
            new(topBoundary + (screenHeight - topBoundary - visibleHeight) / 2d,
                topBoundary + (screenHeight - topBoundary) / 2d),
            new(Math.Max(0d, screenHeight - visibleHeight), screenHeight)
        };

        foreach (DirectorWorkspacePanelLayout other in layout.Panels)
        {
            if (ReferenceEquals(other, panel)
                || !other.Visible
                || PanelComposer(other.Id) is not GuiComposer otherComposer)
            {
                continue;
            }

            double otherHeight = other.Height;
            horizontal.Add(new SnapCandidate(other.X, other.X));
            horizontal.Add(new SnapCandidate(other.X + other.Width - panel.Width, other.X + other.Width));
            horizontal.Add(new SnapCandidate(other.X + other.Width, other.X + other.Width));
            horizontal.Add(new SnapCandidate(other.X - panel.Width, other.X));
            horizontal.Add(new SnapCandidate(
                other.X + (other.Width - panel.Width) / 2d,
                other.X + other.Width / 2d));

            vertical.Add(new SnapCandidate(other.Y, other.Y));
            vertical.Add(new SnapCandidate(other.Y + otherHeight - visibleHeight, other.Y + otherHeight));
            vertical.Add(new SnapCandidate(other.Y + otherHeight, other.Y + otherHeight));
            vertical.Add(new SnapCandidate(other.Y - visibleHeight, other.Y));
            vertical.Add(new SnapCandidate(
                other.Y + (otherHeight - visibleHeight) / 2d,
                other.Y + otherHeight / 2d));
            _ = otherComposer;
        }

        SnapCandidate? snapX = horizontal
            .Where(candidate => candidate.Position >= 0d
                && candidate.Position <= Math.Max(0d, screenWidth - panel.Width))
            .OrderBy(candidate => Math.Abs(candidate.Position - requestedX))
            .FirstOrDefault();
        SnapCandidate? snapY = vertical
            .Where(candidate => candidate.Position >= topBoundary
                && candidate.Position <= Math.Max(0d, screenHeight - visibleHeight))
            .OrderBy(candidate => Math.Abs(candidate.Position - requestedY))
            .FirstOrDefault();

        bool useX = snapX is not null && Math.Abs(snapX.Position - requestedX) <= threshold;
        bool useY = snapY is not null && Math.Abs(snapY.Position - requestedY) <= threshold;
        panel.X = useX ? snapX!.Position : requestedX;
        panel.Y = useY ? snapY!.Position : requestedY;
        snapPreview = useX || useY
            ? new SnapPreview(
                panel.X,
                panel.Y,
                panel.Width,
                visibleHeight,
                useX ? snapX!.Guide : null,
                useY ? snapY!.Guide : null)
            : null;
    }

    private void RenderSnapPreview()
    {
        SnapPreview? preview = snapPreview;
        if (preview is null || drag is null || drag.Resize)
        {
            return;
        }

        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        float x = (float)(preview.X * scale);
        float y = (float)(preview.Y * scale);
        float width = (float)(preview.Width * scale);
        float height = (float)(preview.Height * scale);
        int fill = ColorUtil.ToRgba(42, 74, 190, 207);
        int edge = ColorUtil.ToRgba(225, 102, 221, 229);
        capi.Render.RenderRectangle(x, y, 900f, width, height, fill);
        capi.Render.RenderRectangle(x, y, 901f, width, 2f, edge);
        capi.Render.RenderRectangle(x, y + height - 2f, 901f, width, 2f, edge);
        capi.Render.RenderRectangle(x, y, 901f, 2f, height, edge);
        capi.Render.RenderRectangle(x + width - 2f, y, 901f, 2f, height, edge);
        if (preview.VerticalGuide is double vertical)
        {
            capi.Render.RenderRectangle((float)(vertical * scale), 0f, 899f, 1f,
                capi.Render.FrameHeight, edge);
        }
        if (preview.HorizontalGuide is double horizontal)
        {
            capi.Render.RenderRectangle(0f, (float)(horizontal * scale), 899f,
                capi.Render.FrameWidth, 1f, edge);
        }
    }

    private void MovePanelComposer(DirectorWorkspacePanelLayout panel)
    {
        GuiComposer? composer = PanelComposer(panel.Id);
        if (composer is null)
        {
            return;
        }

        ElementBounds root = composer.Bounds;
        root.WithFixedAlignmentOffset(panel.X, panel.Y);
        root.MarkDirtyRecursive();
        root.CalcWorldBounds();
        if (string.Equals(panel.Id, "preview", StringComparison.OrdinalIgnoreCase))
        {
            UpdateGameViewportRuntime();
        }
    }

    private void ApplyDock(DirectorWorkspacePanelLayout panel)
    {
        double screenWidth = capi.Render.FrameWidth / GuiElement.scaled(1d);
        double screenHeight = capi.Render.FrameHeight / GuiElement.scaled(1d);
        switch (panel.Dock)
        {
            case DirectorWorkspaceDock.Top:
                panel.X = 0d;
                panel.Y = 0d;
                panel.Width = screenWidth;
                panel.Height = DirectorWorkspacePanelLayout.ToolbarHeight;
                break;
            case DirectorWorkspaceDock.Left:
                panel.X = 0d;
                panel.Y = DirectorWorkspacePanelLayout.ToolbarHeight;
                panel.Height = Math.Max(90d,
                    screenHeight - DirectorWorkspacePanelLayout.ToolbarHeight);
                break;
            case DirectorWorkspaceDock.Right:
                panel.X = Math.Max(0d, screenWidth - panel.Width);
                panel.Y = DirectorWorkspacePanelLayout.ToolbarHeight;
                panel.Height = Math.Max(90d,
                    screenHeight - DirectorWorkspacePanelLayout.ToolbarHeight);
                break;
            case DirectorWorkspaceDock.Bottom:
            case DirectorWorkspaceDock.Center:
                panel.X = 0d;
                panel.Width = Math.Max(1d, screenWidth);
                panel.Y = Math.Max(
                    DirectorWorkspacePanelLayout.ToolbarHeight,
                    screenHeight - panel.Height);
                break;
        }
    }

    private void CloseFloatingEditorWindow()
    {
        if (pathFloatingEditorKind != PathFloatingEditorKind.None)
        {
            ClosePathFloatingEditor();
            return;
        }
        if (replayFloatingEditorKind != ReplayFloatingEditorKind.None)
        {
            CloseReplayFloatingEditor();
            return;
        }
        if (preloadEditorExpanded)
        {
            preloadEditorExpanded = false;
            selectingPreloadGrid = false;
            panningPreloadGrid = false;
            ComposeInspector();
        }
        else if (section == DirectorWorkspaceSection.Paths)
        {
            ResetPathEditorInteraction();
        }
        else if (section == DirectorWorkspaceSection.Workflows)
        {
            workflowEditorWindowOpen = false;
            contextWorkflowCardId = Guid.Empty;
        }

        ComposeWorkflowCanvas();
        SaveLayout();
    }

    private void UpdateFloatingEditorPosition(double x, double y)
    {
        if (pathFloatingEditorKind != PathFloatingEditorKind.None)
        {
            pathFloatingX = x;
            pathFloatingY = y;
            return;
        }
        if (replayFloatingEditorKind != ReplayFloatingEditorKind.None)
        {
            replayFloatingX = x;
            replayFloatingY = y;
            return;
        }
        DirectorWorkspacePanelLayout panel = layout.Panel("workflow");
        panel.Dock = DirectorWorkspaceDock.Floating;
        panel.X = x;
        panel.Y = y;
        layoutSaveAccumulator = 0f;
    }

    private bool PointInsideFloatingEditor(int x, int y)
        => floatingEditorDialog.PointInside(x, y);

    internal bool HasActiveFloatingPointerInteraction()
        => consumeWorkflowContextMouseUp
            || selectingPreloadGrid
            || panningPreloadGrid
            || draggingWorldPathKeyframe
            || pathTimelinePanning
            || pathTimelineScrubbing
            || pathTimelineDragKind != PathTimelineDragKind.None
            || pathElementTrackReordering
            || panningPathEditorTimeline
            || draggingPathEditorPlayhead
            || draggingPathEditorKeyframes
            || selectingPathEditorRange
            || stretchingPathEditorSegment >= 0
            || rotatingActionPreview
            || paneSplitDragging
            || replaySplitDragTarget != ReplaySplitDragTarget.None
            || replayElementTrackReordering
            || replayTimelinePanning
            || replayExportRangeDragMode != ReplayExportRangeDragMode.None
            || replayAuxiliaryKeyframeDragKind != ReplayAuxiliaryKeyframeDragKind.None
            || scrubbingReplay
            || replayViewportDragMode != ReplayViewportDragMode.None
            || workflowScrollbarDragMode != WorkflowScrollbarDragMode.None
            || draggingWorkflowCard
            || panningWorkflow;

    private DirectorWorkspaceSection? HoveredToolbarResourceSection(int x, int y)
        => RmlToolbarSectionAt(x, y);

    private void CloseResourceMenu()
    {
        if (resourceMenuSection is null)
        {
            return;
        }

        PreserveInputs();
        resourceMenuSection = null;
        resourceMenuPointerCapture = false;
        rmlResourceWindow?.Close();
        if (rmlToolbarWindow?.Document is { IsDisposed: false } menu)
            foreach (var button in menu.Root.QuerySelectorAll(".menu-command.selected")) button.SetClass("selected", false);
    }

    private bool PointInsideResourceMenu(int x, int y)
        => resourceMenuSection is not null
            && RmlResourceContains(x, y);

    private bool RouteResourceMouseDown(MouseEvent args)
    {
        if (rmlResourceWindow?.IsOpened == true) return false;
        if (!PointInsideResourceMenu(args.X, args.Y))
        {
            return false;
        }

        resourceMenuPointerCapture = true;
        Composers[ResourceMenuKey]?.OnMouseDown(args);
        args.Handled = true;
        return true;
    }

    private bool RouteTimelineMouseDown(MouseEvent args)
    {
        if (Composers[TimelineKey] is not GuiComposer composer)
        {
            return false;
        }

        // GuiComposer performs per-element bounds checks. Do not gate this
        // dispatch on the panel root: a rebuilt/docked root can briefly have
        // stale world bounds while its buttons already render at the new
        // position.
        composer.OnMouseDown(args);
        return args.Handled;
    }

    private bool RouteResourceMouseMove(MouseEvent args)
    {
        if (rmlResourceWindow?.IsOpened == true) return false;
        bool inside = PointInsideResourceMenu(args.X, args.Y);
        if (!resourceMenuPointerCapture && !inside)
        {
            return false;
        }

        Composers[ResourceMenuKey]?.OnMouseMove(args);
        args.Handled = true;
        return true;
    }

    private bool RouteResourceMouseUp(MouseEvent args)
    {
        if (rmlResourceWindow?.IsOpened == true) return false;
        bool captured = resourceMenuPointerCapture;
        bool inside = PointInsideResourceMenu(args.X, args.Y);
        if (!captured && !inside)
        {
            return false;
        }

        resourceMenuPointerCapture = false;
        Composers[ResourceMenuKey]?.OnMouseUp(args);
        args.Handled = true;
        return true;
    }

    private bool RouteInspectorWheelToControl(MouseWheelEventArgs args)
    {
        if (args.IsHandled
            || Composers[InspectorKey] is not GuiComposer composer
            || !composer.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY))
        {
            return false;
        }

        composer.OnMouseWheel(args);
        return args.IsHandled;
    }

    private bool RouteResourceMouseWheel(MouseWheelEventArgs args)
    {
        if (rmlResourceWindow?.IsOpened == true) return false;
        if (args.IsHandled
            || !PointInsideResourceMenu(capi.Input.MouseX, capi.Input.MouseY)
            || Composers[ResourceMenuKey] is not GuiComposer composer)
        {
            return false;
        }

        if (ShouldRouteResourceWheelToControl())
        {
            composer.OnMouseWheel(args);
            args.SetHandled();
            return true;
        }

        double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
        if (direction != 0d && TryScrollResourceList(direction > 0d ? -1 : 1))
        {
            args.SetHandled();
            return true;
        }

        composer.OnMouseWheel(args);
        args.SetHandled();
        return true;
    }

    private void RemoveFloatingEditorComposer()
    {
        DisposeKeyframeRml();
        floatingEditorDialog.RemoveComposer();
        workflowBoardBounds = null;
        pathGlobalTimelineBounds = null;
        pathEditorTimelineBounds = null;
        preloadGridBounds = null;
    }

    private void NormalizeLayoutToViewport()
    {
        double screenWidth = capi.Render.FrameWidth / GuiElement.scaled(1d);
        double screenHeight = capi.Render.FrameHeight / GuiElement.scaled(1d);
        foreach (DirectorWorkspacePanelLayout panel in layout.Panels)
        {
            if (imGuiWorkspace.HasDockLayout && panel.Id is "preview" or "inspector" or "timeline") continue;
            double availableWidth = Math.Max(1d, screenWidth);
            double availableHeight = Math.Max(1d,
                screenHeight - DirectorWorkspacePanelLayout.ToolbarHeight);
            double minimumWidth = Math.Min(PanelMinimumWidth(panel.Id), availableWidth);
            double minimumHeight = Math.Min(PanelMinimumHeight(panel.Id), availableHeight);
            double maximumWidth = Math.Max(minimumWidth,
                Math.Min(PanelMaximumWidth(panel.Id), availableWidth));
            double maximumHeight = ViewportMaximumPanelHeight(
                panel.Id,
                minimumHeight,
                availableHeight);
            panel.Width = Math.Clamp(panel.Width, minimumWidth, maximumWidth);
            panel.Height = Math.Clamp(panel.Height, minimumHeight, maximumHeight);
            if (panel.Dock != DirectorWorkspaceDock.Floating)
            {
                ApplyDock(panel);
            }
            else
            {
                double topBoundary = string.Equals(panel.Id, "toolbar", StringComparison.OrdinalIgnoreCase)
                    ? 0d
                    : DirectorWorkspacePanelLayout.ToolbarHeight;
                panel.X = Math.Clamp(panel.X, 0d, Math.Max(0d, screenWidth - panel.Width));
                panel.Y = Math.Clamp(
                    panel.Y,
                    topBoundary,
                    Math.Max(
                        topBoundary,
                        screenHeight - panel.Height));
            }
        }
        ApplySynchronizedPaneLayout(screenWidth, screenHeight);
    }

    private void ApplySynchronizedPaneLayout(double screenWidth, double screenHeight)
    {
        if (imGuiWorkspace.HasDockLayout) return;
        double toolbarHeight = DirectorWorkspacePanelLayout.ToolbarHeight;
        DirectorWorkspacePanelLayout preview = layout.Panel("preview");
        DirectorWorkspacePanelLayout inspector = layout.Panel("inspector");
        double inspectorWidth = Math.Min(MinimumInspectorWidth, screenWidth);
        double previewAreaWidth = Math.Max(1d, screenWidth - inspectorWidth);
        CalculateSynchronizedPreviewSize(previewAreaWidth, screenHeight, preview.Width, preview.Height,
            out double splitX, out double topHeight);
        double splitY = toolbarHeight + topHeight;

        // The game viewport is intentionally anchored to the left edge. The
        // inspector owns the remaining right-hand area and never overlaps it.
        preview.X = 0d;
        preview.Y = toolbarHeight;
        preview.Width = splitX;
        preview.Height = topHeight;
        preview.Dock = DirectorWorkspaceDock.Floating;

        inspector.X = preview.X + preview.Width;
        inspector.Y = toolbarHeight;
        inspector.Width = Math.Max(1d, screenWidth - inspector.X);
        inspector.Height = topHeight;
        inspector.Dock = DirectorWorkspaceDock.Floating;

        DirectorWorkspacePanelLayout bottom = layout.Panel("timeline");
        bottom.X = 0d;
        bottom.Y = splitY;
        bottom.Width = screenWidth;
        bottom.Height = Math.Max(1d, screenHeight - splitY);
        bottom.Dock = DirectorWorkspaceDock.Bottom;
    }

    private void CalculateSynchronizedPreviewSize(
        double screenWidth,
        double screenHeight,
        double requestedWidth,
        double requestedHeight,
        out double previewWidth,
        out double previewHeight)
    {
        double borderTotal = PreviewBorderThickness * 2d;
        double aspect = capi.Render.FrameWidth / (double)Math.Max(1, capi.Render.FrameHeight);
        double availableHeight = Math.Max(
            1d,
            screenHeight - DirectorWorkspacePanelLayout.ToolbarHeight);
        double minimumBottomHeight = Math.Min(
            MinimumBottomPanelHeight,
            Math.Max(1d, availableHeight - MinimumPreviewContentHeight - borderTotal));
        double maximumContentWidth = Math.Max(
            1d,
            screenWidth - borderTotal);
        double maximumContentHeight = Math.Max(
            1d,
            availableHeight - minimumBottomHeight - borderTotal);
        double maximumScale = Math.Min(maximumContentHeight, maximumContentWidth / aspect);
        double minimumScale = Math.Min(
            maximumScale,
            Math.Max(MinimumPreviewContentHeight, MinimumPreviewContentWidth / aspect));

        // The corner grip controls height; retain the game's aspect ratio
        // while keeping the viewport anchored to the left.
        double requestedContentHeight = Math.Max(1d, requestedHeight - borderTotal);
        double contentHeight = Math.Clamp(requestedContentHeight, minimumScale, maximumScale);
        previewWidth = contentHeight * aspect + borderTotal;
        previewHeight = contentHeight + borderTotal;
    }

    private static double PanelMinimumWidth(string id)
        => id switch
        {
            "toolbar" => DirectorWorkspacePanelLayout.ToolbarMinimumWidth,
            "preview" => MinimumPreviewContentWidth + PreviewBorderThickness * 2d,
            "resources" => 360d,
            "inspector" => MinimumInspectorWidth,
            "timeline" => 320d,
            "workflow" => 680d,
            _ => 320d
        };

    private static double PanelMinimumHeight(string id)
        => id switch
        {
            "toolbar" => DirectorWorkspacePanelLayout.ToolbarHeight,
            "preview" => MinimumPreviewContentHeight + PreviewBorderThickness * 2d,
            "timeline" => MinimumBottomPanelHeight,
            "workflow" => 420d,
            _ => 300d
        };

    private static double PanelMaximumWidth(string id)
        => id switch
        {
            "toolbar" => 4096d,
            "preview" => 4096d,
            "resources" => 560d,
            "inspector" => 640d,
            _ => 4096d
        };

    private static double PanelMaximumHeight(string id)
        => id switch
        {
            "toolbar" => DirectorWorkspacePanelLayout.ToolbarHeight,
            "preview" => 2160d,
            "timeline" => 720d,
            "resources" or "inspector" => 1600d,
            _ => 1000d
        };

    private static double ViewportMaximumPanelHeight(
        string id,
        double minimumHeight,
        double availableHeight)
    {
        return Math.Max(
            minimumHeight,
            Math.Min(PanelMaximumHeight(id), availableHeight));
    }

    private static double ToolbarButtonWidth(string label)
    {
        CairoFont font = DirectorFont.ToolbarButtonText();
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        double textWidth = font.GetTextExtents(label ?? string.Empty).Width / scale;
        return Math.Clamp(textWidth + 20d, 44d, 220d);
    }

    private void ConfigureResourceMenuPanel(
        DirectorWorkspacePanelLayout panel,
        DirectorWorkspaceSection menuSection)
    {
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        double screenWidth = capi.Render.FrameWidth / scale;
        double screenHeight = capi.Render.FrameHeight / scale;
        double anchorX = RmlToolbarAnchor(menuSection);
        double anchorY = DirectorWorkspacePanelLayout.ToolbarHeight;
        panel.Dock = DirectorWorkspaceDock.Floating;
        panel.Width = Math.Min(440d, Math.Max(1d, screenWidth - 8d));
        panel.Height = Math.Min(620d, Math.Max(90d, screenHeight - anchorY - 4d));
        panel.X = Math.Clamp(anchorX, 4d, Math.Max(4d, screenWidth - panel.Width - 4d));
        panel.Y = anchorY;
    }

    private void LayoutPreviewAtTopLeft()
    {
        double screenWidth = capi.Render.FrameWidth / Math.Max(0.001d, GuiElement.scaled(1d));
        double screenHeight = capi.Render.FrameHeight / Math.Max(0.001d, GuiElement.scaled(1d));
        DirectorWorkspacePanelLayout preview = layout.Panel("preview");
        preview.Dock = DirectorWorkspaceDock.Floating;
        preview.X = 0d;
        preview.Y = DirectorWorkspacePanelLayout.ToolbarHeight;
        preview.Width = Math.Max(1d, screenWidth - MinimumInspectorWidth);
        preview.Height = Math.Max(
            1d,
            screenHeight
                - DirectorWorkspacePanelLayout.ToolbarHeight
                - MinimumBottomPanelHeight);
        CalculateSynchronizedPreviewSize(
            screenWidth,
            screenHeight,
            preview.Width,
            preview.Height,
            out double fittedWidth,
            out double fittedHeight);
        preview.Width = fittedWidth;
        preview.Height = fittedHeight;
        preview.Collapsed = false;
        preview.Locked = false;
    }

    private void SynchronizeNativePanel(string id, System.Numerics.Vector2 position, System.Numerics.Vector2 size)
    {
        var panel = layout.Panel(id);
        if (Math.Abs(panel.X - position.X) < .1 && Math.Abs(panel.Y - position.Y) < .1
            && Math.Abs(panel.Width - size.X) < .1 && Math.Abs(panel.Height - size.Y) < .1) return;
        panel.X = position.X; panel.Y = position.Y;
        panel.Width = size.X; panel.Height = size.Y;
        if (id == "timeline" && section is DirectorWorkspaceSection.Cameras or DirectorWorkspaceSection.Follows
            or DirectorWorkspaceSection.Paths or DirectorWorkspaceSection.Replay)
            ComposeRmlTimeline();
        if (id == "preview") UpdateGameViewportRuntime();
    }

    private void UpdateGameViewportRuntime()
    {
        if (!IsOpened())
        {
            return;
        }

        DirectorWorkspacePanelLayout preview = layout.Panel("preview");
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        double border = PreviewBorderThickness;
        int x = (int)Math.Round((preview.X + border) * scale);
        int y = (int)Math.Round((preview.Y + border) * scale);
        int width = Math.Max(1, (int)Math.Round((preview.Width - border * 2d) * scale));
        int height = Math.Max(
            1,
            (int)Math.Round((preview.Height - border * 2d) * scale));
        DirectorWorkspaceViewportRuntime.Configure(
            this,
            capi,
            x,
            y,
            width,
            height,
            mapGameInput: layout.NavigationMode);
    }

    private void RenderWorkspaceBackdrop()
    {
        DirectorWorkspacePanelLayout preview = layout.Panel("preview");
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        float frameWidth = capi.Render.FrameWidth;
        float frameHeight = capi.Render.FrameHeight;
        double border = PreviewBorderThickness;
        float left = Math.Clamp((float)((preview.X + border) * scale), 0f, frameWidth);
        float top = Math.Clamp(
            (float)((preview.Y + border) * scale),
            0f,
            frameHeight);
        float right = Math.Clamp(
            (float)((preview.X + preview.Width - border) * scale),
            left,
            frameWidth);
        float bottom = Math.Clamp(
            (float)((preview.Y + preview.Height - border) * scale),
            top,
            frameHeight);
        int color = ColorUtil.ToRgba(255, 36, 36, 36);
        if (top > 0f)
        {
            solidTextureRenderer.Render(0d, 0d, frameWidth, top, 1f, color);
        }
        if (bottom < frameHeight)
        {
            solidTextureRenderer.Render(
                0f,
                bottom,
                frameWidth,
                frameHeight - bottom,
                1f,
                color);
        }
        if (left > 0f && bottom > top)
        {
            solidTextureRenderer.Render(0f, top, left, bottom - top, 1f, color);
        }
        if (right < frameWidth && bottom > top)
        {
            solidTextureRenderer.Render(
                right,
                top,
                frameWidth - right,
                bottom - top,
                1f,
                color);
        }
    }

    private bool ToggleNavigationMode()
    {
        layout.NavigationMode = !layout.NavigationMode;
        SynchronizeNavigationInput();
        owner.SetWorkspaceInputOverride(!layout.NavigationMode);
        UpdateGameViewportRuntime();
        SaveLayout();
        ComposeToolbar();
        return true;
    }

    private bool EnterNavigationMode()
    {
        if (!layout.NavigationMode)
        {
            layout.NavigationMode = true;
            SynchronizeNavigationInput();
            owner.SetWorkspaceInputOverride(false);
            UpdateGameViewportRuntime();
            SaveLayout();
            ComposeToolbar();
        }
        return true;
    }

    private bool ActivateCameraFromWorkspace(Guid id)
    {
        bool activated = owner.TryActivateCamera(id);
        if (activated)
        {
            EnterNavigationMode();
        }
        return activated;
    }

    private bool PlayPathFromWorkspace(Guid id, bool reverse)
    {
        bool playing = owner.TryPlayPath(id, reverse);
        if (playing)
        {
            EnterNavigationMode();
        }
        return playing;
    }

    private bool ActivateFollowFromWorkspace(Guid id)
    {
        bool activated = owner.TryActivateFollowRig(id);
        if (activated)
        {
            EnterNavigationMode();
        }
        return activated;
    }

    private bool ToggleCameraHud()
    {
        owner.SetHudHidden(!owner.Config.HideHudOnCamera);
        ComposeToolbar();
        return true;
    }

    private bool PlaySequenceFromWorkspace(Guid id)
    {
        if (owner.TryPlaySequence(id))
        {
            EnterNavigationMode();
        }
        return true;
    }

    private void SaveLayout()
    {
        layout.ActiveSection = section;
        store.Save(layout);
    }

    private string PanelKey(string id)
        => id switch
        {
            "toolbar" => ToolbarKey,
            "preview" => PreviewKey,
            "resources" => ResourceMenuKey,
            "inspector" => InspectorKey,
            "timeline" => TimelineKey,
            "workflow" => WorkflowCanvasKey,
            _ => TimelineKey
        };

    private GuiComposer? PanelComposer(string id)
        => string.Equals(id, "workflow", StringComparison.OrdinalIgnoreCase)
            ? floatingEditorDialog.Composer
            : Composers[PanelKey(id)];

    private string PathTimelineText()
        => pathEdit is null
            ? string.Empty
            : Lang.Get("vsdirector:timeline-summary", pathEdit.Keyframes.Count,
                pathEdit.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                pathEdit.Interpolation, pathEdit.LoopMode);

    private string PathSimpleTimelineStatusText()
        => Lang.Get(
            "vsdirector:path-basic-timeline-status",
            pathEditorPlayhead.ToString("0.###", CultureInfo.InvariantCulture),
            SelectedPathKeyframeIndex() >= 0
                ? (SelectedPathKeyframeIndex() + 1).ToString(CultureInfo.InvariantCulture)
                : Lang.Get("vsdirector:none"));

    private string PathWorkspaceStatusText()
        => Lang.Get(
            "vsdirector:path-workspace-status",
            pathEditorPlayhead.ToString("0.###", CultureInfo.InvariantCulture),
            PathEditorViewStart.ToString("0.###", CultureInfo.InvariantCulture),
            (PathEditorViewStart + PathEditorViewDuration).ToString("0.###", CultureInfo.InvariantCulture),
            pathEdit?.Keyframes.Count ?? 0,
            selectedPathKeyframes.Count,
            Lang.Get(layout.PathTimelineSnap
                ? "vsdirector:path-snap-on"
                : "vsdirector:path-snap-off"));

    private string ReplayLoadingText()
        => owner.IsReplayLoadOverlayVisible
            ? Lang.Get("vsdirector:workspace-replay-loading",
                Lang.Get("vsdirector:workspace-replay-phase-" + owner.LoadingPhase),
                (owner.ReplayLoadingProgress * 100d).ToString("0.0", CultureInfo.InvariantCulture))
            : owner.PlaybackLoadError.Length > 0
                ? owner.PlaybackLoadError
                : owner.IsReplayPlaying
                    ? Lang.Get(
                        "vsdirector:workspace-replay-playback-status",
                        owner.ActiveReplayName ?? Lang.Get("vsdirector:inactive"),
                        owner.ReplayTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                        owner.ReplayDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                        Lang.Get(owner.HasReplayReachedEnd
                            ? "vsdirector:replay-ended-short"
                            : owner.IsReplayPaused
                                ? "vsdirector:replay-paused-short"
                                : "vsdirector:replay-playing-short"))
                    : Lang.Get("vsdirector:workspace-replay-none-loaded");

    private string PathSelectedKeyframeText()
    {
        int index = SelectedPathKeyframeIndex();
        if (pathEdit is null || index < 0 || index >= pathEdit.Keyframes.Count)
        {
            return string.Empty;
        }

        VSDirector.Data.CameraKeyframe frame = pathEdit.Keyframes[index];
        (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
            frame.X,
            frame.Y,
            frame.Z);
        string detail = Lang.Get("vsdirector:selected-keyframe-detail", index + 1, pathEdit.Keyframes.Count,
            frame.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            displayX.ToString("0.##", CultureInfo.InvariantCulture), displayY.ToString("0.##", CultureInfo.InvariantCulture),
            displayZ.ToString("0.##", CultureInfo.InvariantCulture), frame.Yaw.ToString("0.#", CultureInfo.InvariantCulture),
            frame.Pitch.ToString("0.#", CultureInfo.InvariantCulture), frame.Roll.ToString("0.#", CultureInfo.InvariantCulture),
            frame.Fov.ToString("0.#", CultureInfo.InvariantCulture));
        return Lang.Get(
            "vsdirector:selected-keyframe-time-range",
            frame.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            frame.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture))
            + " | "
            + Lang.Get("vsdirector:segment-speed-range",
                frame.SegmentStartSpeed.ToString("0.##", CultureInfo.InvariantCulture),
                frame.SegmentEndSpeed.ToString("0.##", CultureInfo.InvariantCulture))
            + "\n" + detail;
    }

    private string PathEditorDetails(CameraPath path)
        => string.Join("\n", path.Keyframes.Select((frame, index) =>
        {
            (double x, double y, double z) = owner.DisplayPosition(frame.X, frame.Y, frame.Z);
            return $"{index + 1:00}  {frame.TimeSeconds:0.###}-{frame.TimeSeconds + frame.DurationSeconds:0.###}s  "
                + $"({x:0.##}, {y:0.##}, {z:0.##})  -> "
                + SegmentInterpolationLabel(frame.InterpolationToNext ?? path.Interpolation)
                + $"  {frame.SegmentStartSpeed:0.##}x->{frame.SegmentEndSpeed:0.##}x"
                + (string.IsNullOrWhiteSpace(frame.Marker) ? string.Empty : $"  [{frame.Marker}]");
        })) ?? string.Empty;

    private string CameraPoseText(CameraPreset camera)
    {
        (double x, double y, double z) = owner.DisplayPosition(camera.X, camera.Y, camera.Z);
        return $"X {x:0.###}  Y {y:0.###}  Z {z:0.###}\nYaw {camera.Yaw:0.#}  Pitch {camera.Pitch:0.#}  Roll {camera.Roll:0.#}\nFOV {camera.Fov:0.#}  Dimension {camera.Dimension}";
    }

    private int SelectedPathKeyframeIndex()
        => pathEdit is null
            || pathEdit.Keyframes.Count == 0
            || selectedPathKeyframeIndex < 0
            ? -1
            : Math.Clamp(selectedPathKeyframeIndex, 0, pathEdit.Keyframes.Count - 1);

    private Guid SelectedSequenceCameraId()
        => selectedSequenceCameraId != Guid.Empty
            ? selectedSequenceCameraId
            : sequenceEdit?.Shots.FirstOrDefault()?.CameraId ?? owner.Cameras.FirstOrDefault()?.Id ?? Guid.Empty;

    private DirectorReplayInfo? SelectedReplayInfo()
        => selectedId is Guid id ? owner.Replays.FirstOrDefault(replay => replay.Id == id) : null;

    private DirectorReplayInfo? PreloadEditorReplayInfo()
        => ResolvePreloadEditorReplayInfo(SelectedReplayInfo(), owner.ActiveReplayId, owner.Replays);

    private static DirectorReplayInfo? ResolvePreloadEditorReplayInfo(
        DirectorReplayInfo? selected,
        Guid activeReplayId,
        IReadOnlyList<DirectorReplayInfo> replays)
        => selected ?? (activeReplayId == Guid.Empty
            ? null
            : replays.FirstOrDefault(replay => replay.Id == activeReplayId));

    private static string FollowTargetSelector(CameraFollowRig rig)
        => rig.TargetType == CameraFollowTargetType.Player
            ? "player:" + rig.TargetPlayerUid
            : "entity:" + rig.TargetEntityId;

    private static string PathInterpolationCode(CameraPath path)
        => path.Interpolation switch
        {
            CameraPathInterpolation.EaseInOut => "ease",
            CameraPathInterpolation.Bezier => "bezier",
            CameraPathInterpolation.Step => "step",
            _ => "linear"
        };

    private string SelectedSegmentInterpolationCode(CameraPath path)
    {
        int index = SelectedPathKeyframeIndex();
        return index < 0 || index >= path.Keyframes.Count
            ? "default"
            : path.Keyframes[index].InterpolationToNext switch
            {
                CameraPathInterpolation.Linear => "linear",
                CameraPathInterpolation.EaseInOut => "ease",
                CameraPathInterpolation.Bezier => "bezier",
                CameraPathInterpolation.Step => "step",
                _ => "default"
            };
    }

    private static string SegmentInterpolationLabel(CameraPathInterpolation interpolation)
        => Lang.Get(interpolation switch
        {
            CameraPathInterpolation.Linear => "vsdirector:interpolation-linear",
            CameraPathInterpolation.Bezier => "vsdirector:interpolation-bezier",
            CameraPathInterpolation.Step => "vsdirector:interpolation-step",
            _ => "vsdirector:interpolation-easeinout"
        });

    private static string PathLoopCode(CameraPath path)
        => path.LoopMode switch
        {
            CameraPathLoopMode.Loop => "loop",
            CameraPathLoopMode.PingPong => "pingpong",
            _ => "once"
        };

    private void SyncPathTimingText()
    {
        int index = SelectedPathKeyframeIndex();
        if (pathEdit is null || index < 0 || index >= pathEdit.Keyframes.Count)
        {
            pathFrameStartText = "0";
            pathFrameDurationText = CameraPathTiming.DefaultKeyframeDurationSeconds
                .ToString(CultureInfo.InvariantCulture);
            pathSegmentStartSpeedText = CameraPathTiming.DefaultSegmentSpeed
                .ToString(CultureInfo.InvariantCulture);
            pathSegmentEndSpeedText = CameraPathTiming.DefaultSegmentSpeed
                .ToString(CultureInfo.InvariantCulture);
            pathSegmentMarkerText = string.Empty;
            return;
        }

        VSDirector.Data.CameraKeyframe frame = pathEdit.Keyframes[index];
        pathFrameStartText = frame.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        pathFrameDurationText = frame.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        pathSegmentStartSpeedText = frame.SegmentStartSpeed.ToString("0.###", CultureInfo.InvariantCulture);
        pathSegmentEndSpeedText = frame.SegmentEndSpeed.ToString("0.###", CultureInfo.InvariantCulture);
        pathSegmentMarkerText = frame.Marker;
    }

    private static double NextPathCaptureTime(CameraPath path)
    {
        if (path.Keyframes.Count == 0)
        {
            return 0d;
        }
        CameraKeyframe last = path.Keyframes[^1];
        return last.DurationSeconds >= CameraPathTiming.MinKeyframeGapSeconds
            ? path.DurationSeconds
            : last.TimeSeconds + CameraPathTiming.DefaultKeyframeDurationSeconds;
    }

    private static double ParseDouble(string? value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed) ? parsed : fallback;

    private static float ParseFloat(string? value, float fallback)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            && float.IsFinite(parsed) ? parsed : fallback;

    private static double ReadNumber(GuiComposer composer, string key, double fallback)
        => ParseDouble(composer.GetNumberInput(key)?.GetText(), fallback);

    private static string Ellipsize(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..Math.Max(1, maxLength - 3)] + "...";

    private static string ToVtml(string value)
    {
        string[] lines = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        return string.Join("<br>", lines.Select(WebUtility.HtmlEncode));
    }

    private sealed record PreloadGridState(
        Guid TargetId,
        bool IsReplay,
        int CenterChunkX,
        int CenterChunkZ,
        int TargetChunkX,
        int TargetChunkZ,
        int Dimension,
        IReadOnlySet<DirectorReplayPreloadColumn> Automatic,
        IReadOnlySet<DirectorReplayPreloadColumn> Final,
        HashSet<DirectorReplayPreloadColumn> Included,
        HashSet<DirectorReplayPreloadColumn> Excluded);

    private readonly record struct PreloadGridMetrics(
        double OffsetX,
        double OffsetY,
        double CellSize,
        int CellsX,
        int CellsZ,
        int HalfX,
        int HalfZ);

    private readonly record struct ActionFrameSelection(Guid LayerId, double TimeSeconds);

    private sealed record ActionLibraryCardEntry(
        Guid ActionId,
        string ModelCode,
        string Name,
        string Detail,
        string Badge,
        bool IsModel,
        bool Selected,
        DirectorModelAsset? Model);

    private sealed record ActionFrameClipboardItem(
        Guid LayerId,
        string LayerName,
        DirectorActionLayerKind Kind,
        DirectorActionBlendMode BlendMode,
        double OffsetSeconds,
        DirectorActionFrame Frame);

    [Flags]
    private enum PanelResizeEdge
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8
    }

    private enum WorkflowCardDragMode
    {
        None,
        Move,
        ResizeStart,
        ResizeEnd
    }

    private enum WorkflowScrollbarDragMode
    {
        None,
        Horizontal,
        Vertical
    }

    private enum PathWorkspaceTool
    {
        Select,
        Razor
    }

    private enum PathWorldDragTarget
    {
        Keyframe,
        BezierIn,
        BezierOut
    }

    private enum ReplaySplitDragTarget
    {
        None,
        Resources,
        Inspector
    }

    private bool IsControlDown()
    {
        bool[] keys = capi.Input.KeyboardKeyStateRaw;
        int left = (int)GlKeys.LControl;
        int right = (int)GlKeys.RControl;
        return (uint)left < (uint)keys.Length && keys[left]
            || (uint)right < (uint)keys.Length && keys[right];
    }

    private enum ReplayViewportDragMode
    {
        None,
        Move,
        ResizeStart,
        ResizeEnd
    }

    private enum ReplayExportRangeDragMode
    {
        None,
        Start,
        End
    }

    private enum ReplayAuxiliaryKeyframeDragKind
    {
        None,
        Marker,
        Speed,
        Element
    }

    private enum ReplayFloatingEditorKind
    {
        None,
        AddElement,
        Track,
        CameraKeyframe,
        ElementKeyframe,
        EntityPopup,
        NewScene,
        RenameScene
    }

    private enum ReplayTimelineElementKind
    {
        Camera,
        Marker,
        Speed,
        CameraOrbit,
        TrackEntity,
        FollowEntity,
        CameraShake,
        Fov,
        Timelapse,
        TimeOfDay,
        Freeze,
        BlockOverride,
        VoiceAudio
    }

    private enum InspectorSectionTarget
    {
        Normal,
        Replay,
        ReplayView
    }

    private enum ReplayAuxiliaryTimelineKind
    {
        Sequence,
        Path
    }

    private sealed record DragState(
        string PanelId,
        int StartX,
        int StartY,
        double StartPanelX,
        double StartPanelY,
        double StartWidth,
        double StartHeight,
        PanelResizeEdge Edges,
        double Scale)
    {
        public bool Resize => Edges != PanelResizeEdge.None;
    }

    private readonly record struct ReplayViewOption(Guid Id, string Name);

    private readonly record struct ReplayViewportSourceOption(string Value, string Name);

    private readonly record struct ReplaySplitLayout(
        double TopY,
        double TopHeight,
        double DividerY,
        double BottomY,
        double BottomHeight)
    {
        public double TopContentY => TopY + ReplayBlockHeaderHeight;

        public double TopEnd => TopY + TopHeight;

        public double BottomContentY => BottomY + ReplayBlockHeaderHeight;

        public double BottomEnd => BottomY + BottomHeight;
    }

    private readonly record struct WorkflowView(
        double StartSeconds,
        double DurationSeconds,
        double TimelineSeconds,
        double Zoom)
    {
        public double EndSeconds => StartSeconds + DurationSeconds;
    }

    private sealed record SnapCandidate(double Position, double Guide);

    private sealed record SnapPreview(
        double X,
        double Y,
        double Width,
        double Height,
        double? VerticalGuide,
        double? HorizontalGuide);

    private sealed record RecordingSourceOption(
        string Value,
        DirectorRecordingSourceType Type,
        string Selector,
        string Label);
}
