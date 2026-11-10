using System.Text;
using System.Text.Json;
using VSDirector;
using VSDirector.Data;
using VSDirector.Networking;
using VSDirector.Recording;
using VSDirector.Rendering;

return await SmokeTests.RunAsync();

internal static class SmokeTests
{
    private static readonly (string Name, Func<Task> Run)[] Cases =
    {
        ("creation two-bone IK, unreachable targets, isolation and reference remapping", RunSync(CreationActorChecks.Ik)),
        ("creation performance migration, reload, deterministic seeking and duplication", RunSync(CreationActorChecks.PerformanceStorage)),
        ("creation clip gesture allocation, undo isolation, recording beyond ten seconds and world coordinates", RunSync(CreationChecks.InteractionAndRecording)),
        ("creation editing workflow, auto key, drafts and isolated action clock", RunSync(CreationChecks.EditingWorkflow)),
        ("creation transform matrices and native bone pose inverses", RunSync(CreationChecks.Transforms)),
        ("creation independent actors, bone bindings, interpolation and clip boundaries", RunSync(CreationChecks.ActorsAndTimeline)),
        ("creation native Shape preserves unknown fields and animation units", RunSync(CreationChecks.ShapeRoundTrip)),
        ("creation atomic saves, recovery, undo, gestures and asset isolation", RunSync(CreationChecks.StoreAndSession)),
        ("creation continuous particles remain deterministic across seeks", RunSync(CreationChecks.Particles)),
        ("editor camera pivot, world rotation, precision and pointer intersections", RunSync(CameraManipulationChecks.Run)),
        ("observation cardinal views, orbit, zoom, large coordinates and image orientation", RunSync(CameraObservationChecks.Run)),
        ("timeline row geometry and indexed voice waveforms preserve clipping, amplitude and cancellation", RunSync(TimelineMediaChecks.Run)),
        ("paged playback opens metadata only, seeks both ways and evicts payloads", RunSync(ReplayPagingChecks.Run)),
        ("continuous voice preserves packets at low FPS and handles pause, seek and silence", RunSync(ReplayVoiceStreamChecks.Run)),
        ("server indexes seek checkpoints and resolve cross-segment shared chunks losslessly", RunSync(ServerIndexChecks.Run)),
        ("legacy coordinated replay metadata remains readable", RunSync(ServerRecordingChecks.Run)),
        ("server companion observations merge into client replay witnesses", RunSync(ServerReplayCompanionChecks.Run)),
        ("load normalization and deep copies", RunSync(LoadNormalizationAndDeepCopies)),
        ("recording actor slots are released without deleting history", RunSync(ReplayRecordingReleasesHistoricalActorSlots)),
        ("unique names and stable ordering", RunSync(UniqueNamesAndStableOrdering)),
        ("quick slots are mutually exclusive", RunSync(QuickSlotsAreMutuallyExclusive)),
        ("quick slots map to number-row digits", RunSync(QuickSlotsMapToNumberRowDigits)),
        ("follow rigs normalize and share quick slots", RunSync(FollowRigsNormalizeAndShareQuickSlots)),
        ("legacy action workflows are removed and release quick slots", RunSync(LegacyActionWorkflowsAreRemoved)),
        ("first and third person follow math", RunSync(FollowCameraMathWorks)),
        ("entity view yaw conversion", RunSync(EntityViewYawConversionWorks)),
        ("follow smoothing and delay history", RunSync(FollowSmoothingAndDelayHistoryWork)),
        ("camera paths normalize and edit", RunSync(CameraPathsNormalizeAndEdit)),
        ("camera path authoring append and insertion times", RunSync(CameraPathAuthoringTimesWork)),
        ("camera path interpolation", RunSync(CameraPathInterpolationWorks)),
        ("camera path element tracks normalize sample and persist", RunSync(CameraPathElementTracksWork)),
        ("camera path element insertion samples and upserts", RunSync(CameraPathElementInsertionWorks)),
        ("camera path step hold bezier handles and JSON", RunSync(CameraPathAdvancedInterpolationAndJsonWorks)),
        ("camera path playback modes", RunSync(CameraPathPlaybackModesWork)),
        ("replay camera state keeps active mode separate from source selection", RunSync(ReplayCameraStateIsExplicit)),
        ("replay paths stay aligned to the replay timeline", RunSync(ReplayPathsStayAlignedToTimeline)),
        ("camera path segment timing, insertion, and speed", RunSync(CameraPathRetimingAndSpeedWork)),
        ("camera path scissors keep the exact left side", RunSync(CameraPathScissorsKeepLeft)),
        ("camera path segments split delete stretch and mark", RunSync(CameraPathSegmentEditingWorks)),
        ("camera path edit history undoes and branches", RunSync(CameraPathEditHistoryWorks)),
        ("hundreds of cameras and sequences normalize and edit", RunSync(CameraSequencesNormalizeAndEdit)),
        ("camera sequence playback timing", RunSync(CameraSequencePlaybackTimingWorks)),
        ("workflow cards persist and overlapping shots split outputs", RunSync(WorkflowCardsPersistAndSplitOutputs)),
        ("workflow countdown cards persist without entering video passes", RunSync(WorkflowCountdownCardsStayOutsideVideoPasses)),
        ("workflow types expose only their supported card kinds", RunSync(WorkflowTypesRestrictCardKinds)),
        ("live workflow runner starts on the first rendered frame", RunSync(LiveWorkflowRunnerUsesRenderClock)),
        ("workflow multi-view resolves all passes from one wall clock", RunSync(WorkflowMultiViewUsesOneWallClock)),
        ("workflow multi-view offscreen requests share capture clock", RunSync(WorkflowMultiViewOffscreenRequestsShareCaptureClock)),
        ("workflow multi-view manifests round trip atomically", RunSync(WorkflowMultiViewManifestRoundTripsAtomically)),
        ("workflow multi-view holds gaps and reports missing resources", RunSync(WorkflowMultiViewHoldsGapsAndReportsErrors)),
        ("recording tracks normalize and validate sources", RunSync(RecordingTracksNormalizeAndValidateSources)),
        ("recording timecode, bounded scheduling, and ffmpeg arguments", RunSync(RecordingTimecodeAndFfmpegArgumentsWork)),
        ("ffmpeg locator distinguishes missing executables from missing encoders", RunSync(FfmpegLocatorReportsMissingExecutable)),
        ("ffmpeg inputs wait for writer release with a bounded failure", FfmpegInputsWaitForWriterRelease),
        ("DirectShow audio device listing parses stable display names", RunSync(DirectShowAudioDevicesParse)),
        ("recording completion callbacks isolate failures", RunSync(RecordingCompletionCallbacksIsolateFailures)),
        ("offscreen resource budget includes framebuffer and PBO storage", RunSync(OffscreenResourceBudgetIncludesAllTargets)),
        ("multi-track transitions and audio produce executable ffmpeg plan", RunSync(MultiTrackTimelineProducesFfmpegPlan)),
        ("rendered workflow materials preserve scaled edit timing", RunSync(RenderedWorkflowMaterialsPreserveScaledTiming)),
        ("workflow audio editor validates CRUD selectors", RunSync(WorkflowAudioEditorValidatesCrudSelectors)),
        ("client configuration normalizes and reloads in place", RunSync(ClientConfigurationNormalizesAndCopies)),
        ("voice PCM resampling and replay frames normalize safely", RunSync(VoicePcmAndReplayFramesWork)),
        ("OBS websocket authentication vector", RunSync(ObsAuthenticationVectorWorks)),
        ("director permissions default narrowly and reserve full access for admin", RunSync(DirectorPermissionsAreGranular)),
        ("live offscreen sources accept saved and transient current-view selectors", RunSync(LiveOffscreenSourcesValidateSelectors)),
        ("display coordinates, fixed frame schedule, and player animations", RunSync(CoordinatesFramesAndPlayerAnimationsNormalize)),
        ("replay remote chunk anchors prefer visible recorded players", RunSync(ReplayRemoteChunkAnchorSelectionWorks)),
        ("replay remote chunk windows require every visible column", RunSync(ReplayRemoteChunkWindowReadinessWorks)),
        ("replay preload planner builds a continuous preferred-player corridor", RunSync(ReplayPreloadPlannerBuildsContinuousCorridor)),
        ("replay particles deduplicate and bound playback backlog", RunSync(ReplayParticleDeduplicationAndBacklogPolicyWork)),
        ("replay sounds preserve spatial semantics and complete due-event windows", RunSync(ReplaySoundPlaybackPolicyWorks)),
        ("replay interpolation and angle wrapping", RunSync(ReplayInterpolationAndAngleWrappingWork)),
        ("replay third-person poses migrate independently", RunSync(ReplayThirdPersonPoseMigrationWorks)),
        ("replay animation gaps crossfade without stopping", RunSync(ReplayAnimationGapsCrossfadeWithoutStopping)),
        ("replay markers and speed keys persist in lightweight sidecars", RunSync(ReplayTimelineEditsPersistAndSample)),
        ("replay viewport clips normalize clone and persist", RunSync(ReplayViewportClipsNormalizeCloneAndPersist)),
        ("replay visual settings normalize clone migrate and persist", RunSync(ReplayVisualSettingsWork)),
        ("replay curves preserve shape, local control, clock bounds and camera capture", RunSync(ReplayElementCurveChecks.Run)),
        ("Replay elements normalize sample and persist", RunSync(ReplayElementsNormalizeSampleAndPersist)),
        ("replay entity follow intervals sample and normalize", RunSync(ReplayEntityFollowIntervalsWork)),
        ("replay camera orbit and entity tracking math", RunSync(ReplayCameraElementMathWorks)),
        ("timelapse pairs keep independent ranges and roles", RunSync(TimelapsePairsKeepIndependentRanges)),
        ("Replay freeze holds replay content until an unfreeze keyframe", RunSync(ReplayFreezeReachesUnfreezeKeyframe)),
        ("replay export timing applies enabled speed timelapse and freeze elements", RunSync(ReplayExportTimingAppliesElements)),
        ("replay speed keys calculate adjusted playback duration", RunSync(ReplayAdjustedDurationCalculates)),
        ("actor path playback resolves replay time", RunSync(ActorPathPlaybackResolvesReplayTime)),
        ("actor workflow paths restore eye height and convert camera angles", RunSync(ActorWorkflowPathsRestoreActorPose)),
        ("actor performances normalize clone and persist", RunSync(ActorPerformancesNormalizeCloneAndPersist)),
        ("actor performance state interpolates deterministically", RunSync(ActorPerformanceStateInterpolatesDeterministically)),
        ("actor performance import and cue cursor seek deterministically", RunSync(ActorPerformanceImportAndCueCursorWork)),
        ("action assets normalize serialize and package", RunSync(ActionAssetsNormalizeSerializeAndPackage)),
        ("action replay import detects motion and composes layers", RunSync(ActionReplayImportDetectsAndComposesLayers)),
        ("action evaluator loops trims and appends deterministically", RunSync(ActionEvaluatorLoopsTrimsAndAppends)),
        ("imported actions animate without overriding actor facing", RunSync(ImportedActionsAnimateWithoutOverridingFacing)),
        ("legacy action workflow cards are removed", RunSync(LegacyActionWorkflowCardsAreRemoved)),
        ("model assets normalize clone and persist", RunSync(ModelAssetsNormalizeCloneAndPersist)),
        ("VSMC2 Shape classification and validation", RunSync(Vsmc2ShapeClassificationAndValidation)),
        ("replay world state normalization and chunk RLE", RunSync(ReplayWorldStateAndChunkRleWork)),
        ("replay pause resync compares final block state", RunSync(ReplayPauseResyncComparesFinalBlockState)),
        ("quick replay policy skips world reconstruction", RunSync(QuickReplayPolicySkipsWorldReconstruction)),
        ("replay recording reserves capacity for transient actors", RunSync(ReplayRecordingReservesTransientActors)),
        ("replay actor animation forcing respects current view", RunSync(ReplayActorAnimationForcingRespectsCurrentView)),
        ("replay actor scheduling uses distance intervals", RunSync(ReplayActorSchedulingUsesDistanceIntervals)),
        ("replay actor scheduling forces first visible updates", RunSync(ReplayActorSchedulingForcesFirstVisibleUpdates)),
        ("replay actor scheduling resolves mode budgets", RunSync(ReplayActorSchedulingResolvesModeBudgets)),
        ("replay actor scheduling adapts to render time", RunSync(ReplayActorSchedulingAdaptsToRenderTime)),
        ("replay actor scheduling keeps a fair cursor", RunSync(ReplayActorSchedulingKeepsFairCursor)),
        ("replay actor warmup remains bounded", RunSync(ReplayActorWarmupRemainsBounded)),
        ("replay playback delta stays bounded after a slow frame", RunSync(ReplayPlaybackDeltaStaysBounded)),
        ("replay playback restarts after reaching the end", RunSync(ReplayPlaybackRestartsAfterReachingEnd)),
        ("timeline subpixel zoom keeps mouse and playhead aligned", RunSync(TimelineSubpixelZoomStaysAligned)),
        ("automatic replay handoff waits only for take replay sources", RunSync(AutomaticReplayHandoffWaitPolicy)),
        ("replay compression profiles round trip", RunSync(ReplayCompressionProfilesRoundTrip)),
        ("gzip replay round trip", RunSync(GzipReplayRoundTripWorks)),
        ("retryable asynchronous replay save preserves frozen input", RetryableAsyncReplaySavePreservesFrozenInput),
        ("async replay load reports progress", AsyncReplayLoadReportsProgress),
        ("workspace layout normalizes and clones", RunSync(WorkspaceLayoutNormalizesAndClones)),
        ("replay export plan is explicit and deterministic", RunSync(ReplayExportPlanIsExplicitAndDeterministic)),
        ("replay export settles after every seek", RunSync(ReplayExportSettlesAfterEverySeek)),
        ("OBS replay export waits for a stable loaded frame", RunSync(ObsReplayExportWaitsForStableLoadedFrame)),
        ("pose updates retain metadata", RunSync(PoseUpdatesRetainMetadata)),
        ("metadata updates retain pose", RunSync(MetadataUpdatesRetainPose)),
        ("portable preset packages remap ids and references", RunSync(PortablePresetPackagesRemapIdsAndReferences)),
        ("UTF-8 JSON round trip and temp cleanup", JsonRoundTripAndTempCleanupAsync),
        ("invalid and future JSON are rejected without mutation", InvalidDocumentsAreRejectedAsync)
    };

    public static async Task<int> RunAsync()
    {
        int failed = 0;
        foreach ((string name, Func<Task> run) in Cases)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{Cases.Length - failed}/{Cases.Length} smoke tests passed.");
        return failed == 0 ? 0 : 1;
    }

    private static Func<Task> RunSync(Action action)
    {
        return () =>
        {
            action();
            return Task.CompletedTask;
        };
    }

    private static void LoadNormalizationAndDeepCopies()
    {
        Guid duplicateId = Guid.NewGuid();
        DateTimeOffset localTime = new(2025, 2, 3, 12, 30, 0, TimeSpan.FromHours(8));
        CameraPreset source = new()
        {
            Id = duplicateId,
            Name = "  Main Base  ",
            Tags = new List<string> { " dawn ", "DAWN", "", " wide " },
            Description = "  Establishing shot  ",
            X = double.NaN,
            Y = double.PositiveInfinity,
            Z = 4.5,
            Fov = 0,
            PreviewVisible = true,
            QuickSlot = 3,
            CreatedUtc = localTime,
            ModifiedUtc = localTime.AddMinutes(-1)
        };

        CameraLibrary library = new(new CameraLibraryDocument
        {
            SchemaVersion = 0,
            Cameras = new List<CameraPreset>
            {
                source,
                new()
                {
                    Id = duplicateId,
                    Name = "main base",
                    QuickSlot = 3,
                    Fov = 80
                },
                new()
                {
                    Name = "   ",
                    QuickSlot = 99,
                    Fov = float.NaN
                }
            }
        });

        CameraLibraryDocument snapshot = library.Document;
        Check.Equal(CameraLibraryDocument.CurrentSchemaVersion, snapshot.SchemaVersion);
        Check.Equal(3, snapshot.Cameras.Count);
        Check.Equal(3, snapshot.Cameras.Select(camera => camera.Id).Distinct().Count());
        Check.True(snapshot.Cameras.All(camera => camera.Id != Guid.Empty));
        Check.Equal(3, snapshot.Cameras.Count(camera => camera.CreatedUtc.Offset == TimeSpan.Zero));

        CameraPreset main = Check.NotNull(library.Find("main base"));
        Check.Equal("Main Base", main.Name);
        Check.SequenceEqual(new[] { "dawn", "wide" }, main.Tags);
        Check.Equal("Establishing shot", main.Description);
        Check.Equal(0d, main.X);
        Check.Equal(0d, main.Y);
        Check.Equal(4.5d, main.Z);
        Check.Equal(70f, main.Fov);
        Check.True(main.PreviewVisible);
        Check.Equal(localTime.ToUniversalTime(), main.CreatedUtc);
        Check.Equal(main.CreatedUtc, main.ModifiedUtc);

        CameraPreset suffixed = Check.NotNull(library.Find("main base (2)"));
        Check.Null(suffixed.QuickSlot);
        Check.Equal(3, main.QuickSlot);
        CameraPreset fallback = Check.NotNull(library.Find("Camera"));
        Check.Null(fallback.QuickSlot);
        Check.Equal(70f, fallback.Fov);

        source.Name = "Changed outside";
        source.Tags.Add("outside");
        snapshot.Cameras[0].Name = "Changed snapshot";
        snapshot.Cameras[0].Tags.Add("snapshot");

        CameraPreset unchanged = Check.NotNull(library.Find("Main Base"));
        Check.Equal("Main Base", unchanged.Name);
        Check.SequenceEqual(new[] { "dawn", "wide" }, unchanged.Tags);

        CameraPreset clone = unchanged.DeepClone();
        clone.PreviewVisible = false;
        clone.Tags.Add("clone only");
        Check.Equal(2, unchanged.Tags.Count);
        Check.True(unchanged.PreviewVisible);
    }

    private static void CoordinatesFramesAndPlayerAnimationsNormalize()
    {
        Check.Equal(592d, DirectorCoordinateSpace.ToDisplayHorizontal(512592d, 1_024_000));
        Check.Equal(-512_000d, DirectorCoordinateSpace.ToDisplayHorizontal(0d, 1_024_000));
        Check.Equal(12.5d, DirectorCoordinateSpace.ToDisplayHorizontal(12.5d, 0));
        Check.Equal(65_659d, DirectorChunkWindow.ToAbsoluteY(123d, 2));

        (double x, double y, double z) = DirectorCoordinateSpace.ToDisplay(
            512592d,
            123d,
            513155d,
            1_024_000,
            1_024_000);
        Check.Equal(592d, x);
        Check.Equal(123d, y);
        Check.Equal(1155d, z);

        (double absoluteX, double absoluteY, double absoluteZ) = DirectorCoordinateSpace.ToAbsolute(
            x,
            y,
            z,
            1_024_000,
            1_024_000);
        Check.Equal(512592d, absoluteX);
        Check.Equal(123d, absoluteY);
        Check.Equal(513155d, absoluteZ);

        Check.Equal(900L, DirectorFrameSchedule.TargetFrameCount(TimeSpan.FromSeconds(15d), 60));
        Check.Equal(0L, DirectorFrameSchedule.TargetFrameCount(TimeSpan.Zero, 60));
        Check.Equal(8, DirectorFrameSchedule.FramesDue(900L, 100L, 8));
        Check.Equal(800, DirectorFrameSchedule.FramesDue(900L, 100L, int.MaxValue));
        Check.Equal(0, DirectorFrameSchedule.FramesDue(100L, 100L, 8));

        DirectorRealtimeFrameScheduler scheduler = new(60);
        DirectorRealtimeFrameDecision first = scheduler.AdvanceTo(1L);
        Check.True(first.CaptureFrame);
        Check.Equal(0L, first.TimelineFrame);
        Check.Equal(0L, first.DroppedFramesBeforeCapture);
        DirectorRealtimeFrameDecision stalled = scheduler.AdvanceTo(9L);
        Check.True(stalled.CaptureFrame);
        Check.Equal(8L, stalled.TimelineFrame);
        Check.Equal(7L, stalled.DroppedFramesBeforeCapture);
        Check.Equal(8L, stalled.BacklogFrames);
        Check.Equal(2L, scheduler.CapturedFrames);
        Check.Equal(7L, scheduler.DroppedFrames);
        Check.Equal(8L, scheduler.PeakBacklogFrames);
        DirectorRealtimeFrameDecision sameSlot = scheduler.AdvanceTo(9L);
        Check.False(sameSlot.CaptureFrame);
        Check.Equal(3L, scheduler.CompleteTo(12L));
        Check.Equal(10L, scheduler.DroppedFrames);
        Check.Equal(12L, scheduler.TargetFrameCount);

        Check.SequenceEqual(
            new[] { "creativefly", "holdbothhandslarge", "interactstatic", "walk" },
            DirectorReplayMath.NormalizePlayerAnimations(new[]
            {
                "walk-fp", "WALK", " creativefly-fp ", "holdbothhandslarge-fp",
                "interactstatic-ifp", ""
            }));
    }

    private static void UniqueNamesAndStableOrdering()
    {
        CameraLibrary library = new();
        CameraPreset source = new() { Name = "  Zebra  ", Tags = new List<string> { "one" }, Fov = 70 };
        CameraPreset zebra = library.Add(source);
        source.Name = "Mutated";
        source.Tags.Add("two");

        CameraPreset alpha = library.Add(new CameraPreset { Name = "alpha", Fov = 70 });
        CameraPreset duplicate = library.Add(new CameraPreset { Name = "ALPHA", Fov = 70 });

        Check.Equal("ALPHA (2)", duplicate.Name);
        Check.SequenceEqual(new[] { "alpha", "ALPHA (2)", "Zebra" }, library.Ordered.Select(camera => camera.Name));
        Check.Equal("Zebra", Check.NotNull(library.Find(zebra.Id)).Name);
        Check.SequenceEqual(new[] { "one" }, Check.NotNull(library.Find("zebra")).Tags);

        Check.True(library.UpdateMetadata(duplicate.Id, " alpha ", Array.Empty<string>(), "", 75f, 1f, null));
        Check.Equal("alpha (2)", Check.NotNull(library.Find(duplicate.Id)).Name);
        Check.False(library.Remove(Guid.NewGuid()));
        Check.True(library.Remove(alpha.Id));
        Check.Equal(2, library.Count);
    }

    private static void QuickSlotsAreMutuallyExclusive()
    {
        CameraLibrary library = new();
        DateTimeOffset futureModifiedUtc = DateTimeOffset.UtcNow.AddDays(1);
        CameraPreset first = library.Add(new CameraPreset
        {
            Name = "First",
            QuickSlot = 1,
            Fov = 70,
            ModifiedUtc = futureModifiedUtc
        });
        CameraPreset second = library.Add(new CameraPreset { Name = "Second", QuickSlot = 2, Fov = 70 });

        Check.True(library.AssignQuickSlot(second.Id, 1));
        CameraPreset displaced = Check.NotNull(library.Find(first.Id));
        Check.Null(displaced.QuickSlot);
        Check.True(displaced.ModifiedUtc > first.ModifiedUtc);
        Check.Equal(1, Check.NotNull(library.Find(second.Id)).QuickSlot);

        Check.True(library.AssignQuickSlot(second.Id, QuickSlots.Last));
        Check.Equal(QuickSlots.Last, Check.NotNull(library.Find(second.Id)).QuickSlot);

        Check.True(library.AssignQuickSlot(second.Id, QuickSlots.Last + 1));
        Check.Null(Check.NotNull(library.Find(second.Id)).QuickSlot);

        CameraPreset third = library.Add(new CameraPreset { Name = "Third", QuickSlot = 1, Fov = 70 });
        Check.Equal(1, Check.NotNull(library.Find(third.Id)).QuickSlot);
        Check.Equal(1, library.Document.Cameras.Count(camera => camera.QuickSlot == 1));
    }

    private static void QuickSlotsMapToNumberRowDigits()
    {
        for (int slot = QuickSlots.First; slot < QuickSlots.Last; slot++)
        {
            Check.Equal(slot, QuickSlots.KeyboardDigit(slot));
        }

        Check.Equal(0, QuickSlots.KeyboardDigit(QuickSlots.Last));
        Check.Throws<ArgumentOutOfRangeException>(() => QuickSlots.KeyboardDigit(QuickSlots.First - 1));
        Check.Throws<ArgumentOutOfRangeException>(() => QuickSlots.KeyboardDigit(QuickSlots.Last + 1));
    }

    private static void FollowRigsNormalizeAndShareQuickSlots()
    {
        CameraLibrary migrated = new(new CameraLibraryDocument
        {
            SchemaVersion = 4,
            Cameras = new List<CameraPreset>
            {
                new() { Name = "Fixed", QuickSlot = 2, Fov = 70 }
            },
            FollowRigs = new List<CameraFollowRig>
            {
                new()
                {
                    Name = "  Player One  ",
                    Tags = new List<string> { " actor ", "ACTOR", "cinematic" },
                    Description = "  Lead actor follow  ",
                    TargetType = (CameraFollowTargetType)999,
                    TargetPlayerUid = "  uid-one  ",
                    TargetDisplayName = "  Player One  ",
                    ViewMode = (CameraFollowViewMode)999,
                    MotionMode = (CameraFollowMotionMode)999,
                    Anchor = (CameraFollowAnchor)999,
                    OpenClearView = true,
                    OffsetX = double.NaN,
                    Distance = double.PositiveInfinity,
                    Height = double.NegativeInfinity,
                    OrbitAngleDegrees = 540,
                    OrbitSpeedDegreesPerSecond = 9999,
                    SmoothTimeSeconds = 0,
                    DelaySeconds = 999,
                    Fov = float.NaN,
                    QuickSlot = 2
                }
            }
        });

        CameraFollowRig normalized = Check.NotNull(migrated.FindFollowRig("player one"));
        Check.Equal("Player One", normalized.Name);
        Check.SequenceEqual(new[] { "actor", "cinematic" }, normalized.Tags);
        Check.Equal("Lead actor follow", normalized.Description);
        Check.Equal(CameraFollowTargetType.Player, normalized.TargetType);
        Check.Equal("uid-one", normalized.TargetPlayerUid);
        Check.Equal(CameraFollowViewMode.ThirdPerson, normalized.ViewMode);
        Check.Equal(CameraFollowMotionMode.Smooth, normalized.MotionMode);
        Check.Equal(CameraFollowAnchor.Head, normalized.Anchor);
        Check.True(normalized.OpenClearView);
        Check.Equal(0d, normalized.OffsetX);
        Check.Equal(CameraFollowMath.DefaultDistance, normalized.Distance);
        Check.Equal(CameraFollowMath.DefaultHeight, normalized.Height);
        Check.Equal(-180d, normalized.OrbitAngleDegrees);
        Check.Equal(CameraFollowMath.MaxOrbitSpeed, normalized.OrbitSpeedDegreesPerSecond);
        Check.Equal(CameraFollowMath.MinSmoothTimeSeconds, normalized.SmoothTimeSeconds);
        Check.Equal(CameraFollowMath.MaxDelaySeconds, normalized.DelaySeconds);
        Check.Equal(CameraFollowMath.DefaultFov, normalized.Fov);
        Check.Null(normalized.QuickSlot);

        CameraFollowRig follow = migrated.AddFollowRig(new CameraFollowRig
        {
            Name = "Entity Follow",
            TargetType = CameraFollowTargetType.Entity,
            TargetEntityId = 42,
            QuickSlot = 2
        });
        Check.Null(Check.NotNull(migrated.Find("Fixed")).QuickSlot);
        Check.Equal(2, Check.NotNull(migrated.FindFollowRig(follow.Id)).QuickSlot);

        CameraPreset fixedCamera = Check.NotNull(migrated.Find("Fixed"));
        Check.True(migrated.AssignQuickSlot(fixedCamera.Id, 2));
        Check.Equal(2, Check.NotNull(migrated.Find(fixedCamera.Id)).QuickSlot);
        Check.Null(Check.NotNull(migrated.FindFollowRig(follow.Id)).QuickSlot);

        follow.Name = "Renamed Follow";
        follow.Tags = new List<string> { " close ", "CLOSE", "actor" };
        follow.Description = "  Tight follow shot  ";
        follow.ViewMode = CameraFollowViewMode.FirstPerson;
        follow.MotionMode = CameraFollowMotionMode.Delayed;
        follow.OpenClearView = true;
        follow.QuickSlot = QuickSlots.Last;
        Check.True(migrated.UpdateFollowRig(follow));
        CameraFollowRig updated = Check.NotNull(migrated.FindFollowRig(follow.Id));
        Check.Equal("Renamed Follow", updated.Name);
        Check.SequenceEqual(new[] { "close", "actor" }, updated.Tags);
        Check.Equal("Tight follow shot", updated.Description);
        Check.Equal(CameraFollowViewMode.FirstPerson, updated.ViewMode);
        Check.Equal(CameraFollowMotionMode.Delayed, updated.MotionMode);
        Check.True(updated.OpenClearView);
        Check.Equal(QuickSlots.Last, updated.QuickSlot);
        follow.Name = "Changed outside";
        Check.Equal("Renamed Follow", Check.NotNull(migrated.FindFollowRig(updated.Id)).Name);
        Check.True(migrated.RemoveFollowRig(updated.Id));
    }

    private static void LegacyActionWorkflowsAreRemoved()
    {
        Guid cameraId = Guid.NewGuid();
        Guid removedWorkflowId = Guid.NewGuid();
        Guid recordingWorkflowId = Guid.NewGuid();
        CameraLibrary library = new(new CameraLibraryDocument
        {
            Cameras = new List<CameraPreset>
            {
                new() { Id = cameraId, Name = "Camera Slot", Fov = 70f, QuickSlot = 4 }
            },
            Workflows = new List<DirectorWorkflow>
            {
                new()
                {
                    Id = removedWorkflowId,
                    Name = "Legacy action",
                    Type = DirectorWorkflowType.Action,
                    QuickSlot = 4
                },
                new()
                {
                    Id = recordingWorkflowId,
                    Name = "Recording",
                    Type = DirectorWorkflowType.Recording,
                    QuickSlot = 5
                }
            },
            RecordingTracks = new List<DirectorRecordingTrack>
            {
                new()
                {
                    Name = "Legacy action track",
                    SourceType = DirectorRecordingSourceType.Workflow,
                    SourceId = removedWorkflowId,
                    Enabled = true
                }
            }
        });

        Check.True(library.FindWorkflow(removedWorkflowId) is null);
        Check.Equal(4, Check.NotNull(library.Find(cameraId)).QuickSlot);
        Check.Equal(5, Check.NotNull(library.FindWorkflow(recordingWorkflowId)).QuickSlot);
        DirectorRecordingTrack removedTrack = Check.NotNull(library.OrderedRecordingTracks.Single());
        Check.False(removedTrack.Enabled);

        DirectorWorkflow normalized = library.AddWorkflow(new DirectorWorkflow
        {
            Name = "Legacy action input",
            Type = DirectorWorkflowType.Action,
            Cards = new List<DirectorWorkflowCard>
            {
                new() { Name = "Legacy action card", Kind = DirectorWorkflowCardKind.Action }
            }
        });
        Check.Equal(DirectorWorkflowType.Recording, normalized.Type);
        Check.Equal(0, normalized.Cards.Count);
    }

    private static void FollowCameraMathWorks()
    {
        CameraFollowTargetPose target = new(10, 20, 30, 2, 0, 0);
        CameraFollowRig firstPerson = new()
        {
            ViewMode = CameraFollowViewMode.FirstPerson,
            OffsetX = 1,
            OffsetY = 2,
            OffsetZ = 3,
            Fov = 80
        };
        CameraFollowPose first = CameraFollowMath.Evaluate(firstPerson, target);
        Check.NearlyEqual(11d, first.X);
        Check.NearlyEqual(22d, first.Y);
        Check.NearlyEqual(27d, first.Z);
        Check.Equal(2, first.Dimension);
        Check.NearlyEqual(0d, first.YawDegrees);
        Check.NearlyEqual(0d, first.PitchDegrees);
        Check.NearlyEqual(80d, first.Fov);

        CameraFollowRig thirdPerson = new()
        {
            ViewMode = CameraFollowViewMode.ThirdPerson,
            Distance = 5,
            Height = 2,
            Fov = 70
        };
        CameraFollowPose behind = CameraFollowMath.Evaluate(thirdPerson, target);
        Check.NearlyEqual(10d, behind.X);
        Check.NearlyEqual(22d, behind.Y);
        Check.NearlyEqual(35d, behind.Z);
        Check.NearlyEqual(0d, behind.YawDegrees);
        Check.NearlyEqual(-21.801409d, behind.PitchDegrees);

        thirdPerson.OrbitAngleDegrees = 90;
        CameraFollowPose side = CameraFollowMath.Evaluate(thirdPerson, target);
        Check.NearlyEqual(15d, side.X);
        Check.NearlyEqual(30d, side.Z);
        Check.NearlyEqual(90d, side.YawDegrees);

        CameraFollowPose runtimeOrbit = CameraFollowMath.Evaluate(thirdPerson, target, 90);
        Check.NearlyEqual(10d, runtimeOrbit.X);
        Check.NearlyEqual(25d, runtimeOrbit.Z);
        Check.NearlyEqual(-180d, runtimeOrbit.YawDegrees);

        CameraFollowPose elevated = CameraFollowMath.Evaluate(thirdPerson, target, 0d, 30d);
        Check.NearlyEqual(24.5d, elevated.Y);
        Check.NearlyEqual(14.330127d, elevated.X);
        Check.NearlyEqual(30d, elevated.Z);
    }

    private static void EntityViewYawConversionWorks()
    {
        Check.NearlyEqual(Math.PI, DirectorEntityViewMath.ToCameraYaw(0d), 0.0000001d);
        Check.NearlyEqual(
            0.5d + 0.25d + Math.PI,
            DirectorEntityViewMath.ToCameraYaw(0.5d, 0.25d),
            0.0000001d);
        Check.NearlyEqual(
            0.5d + 0.25d + Math.PI + Math.PI / 2d,
            DirectorEntityViewMath.ToCameraYaw(0.5d, 0.25d, Math.PI / 2d),
            0.0000001d);
        Check.NearlyEqual(
            Math.PI / 2d,
            DirectorEntityViewMath.EntityShapeYawOffset("game:armorstand"),
            0.0000001d);
        Check.NearlyEqual(
            Math.PI / 2d,
            DirectorEntityViewMath.EntityShapeYawOffset("game:armorstand-aged"),
            0.0000001d);
        Check.NearlyEqual(
            Math.PI / 2d,
            DirectorEntityViewMath.EntityShapeYawOffset("strawdummy"),
            0.0000001d);
        Check.NearlyEqual(
            0d,
            DirectorEntityViewMath.EntityShapeYawOffset("game:horse"),
            0.0000001d);
        Check.NearlyEqual(
            0.25d,
            DirectorEntityViewMath.ToPlayerRelativePitch(Math.PI - 0.25d),
            0.0000001d);
        Check.NearlyEqual(
            -0.25d,
            DirectorEntityViewMath.ToPlayerRelativePitch(Math.PI + 0.25d),
            0.0000001d);
        Check.True(double.IsNaN(DirectorEntityViewMath.ToCameraYaw(double.NaN)));
        Check.True(double.IsNaN(DirectorEntityViewMath.ToCameraYaw(0d, double.PositiveInfinity)));
        Check.True(double.IsNaN(DirectorEntityViewMath.ToCameraYaw(0d, 0d, double.NaN)));
        Check.True(double.IsNaN(DirectorEntityViewMath.ToPlayerRelativePitch(double.NaN)));
    }

    private static void FollowSmoothingAndDelayHistoryWork()
    {
        CameraFollowPose from = new(0, 0, 0, 0, 170, 0, 0, 60);
        CameraFollowPose to = new(10, 20, 30, 0, -170, 20, 10, 80);
        CameraFollowPose halfway = CameraFollowMath.Interpolate(from, to, 0.5);
        Check.NearlyEqual(5d, halfway.X);
        Check.NearlyEqual(10d, halfway.Y);
        Check.NearlyEqual(15d, halfway.Z);
        Check.NearlyEqual(-180d, halfway.YawDegrees);
        Check.NearlyEqual(10d, halfway.PitchDegrees);
        Check.NearlyEqual(70d, halfway.Fov);

        CameraFollowPose smoothed = CameraFollowMath.Smooth(from, to, 0.5, 0.5);
        Check.True(smoothed.X > 6d && smoothed.X < 7d);
        Check.True(smoothed.PitchDegrees > 12d && smoothed.PitchDegrees < 13d);

        CameraFollowPoseHistory history = new();
        history.Add(0, from, 2);
        history.Add(2, to, 2);
        Check.True(history.TrySample(1, out CameraFollowPose delayed));
        Check.NearlyEqual(5d, delayed.X);
        Check.NearlyEqual(-180d, delayed.YawDegrees);
        Check.True(history.TrySample(-1, out CameraFollowPose oldest));
        Check.NearlyEqual(0d, oldest.X);
        Check.True(history.TrySample(3, out CameraFollowPose latest));
        Check.NearlyEqual(10d, latest.X);
        history.Clear();
        Check.False(history.TrySample(0, out _));
    }

    private static void CameraPathsNormalizeAndEdit()
    {
        CameraLibrary library = new(new CameraLibraryDocument
        {
            SchemaVersion = 1,
            Paths = new List<CameraPath>
            {
                new()
                {
                    Name = "  Flyover  ",
                    Tags = new List<string> { "  aerial ", "AERIAL", "path" },
                    Description = "  wide establishing move  ",
                    Interpolation = (CameraPathInterpolation)999,
                    LoopMode = (CameraPathLoopMode)999,
                    Keyframes = new List<CameraKeyframe>
                    {
                        new() { TimeSeconds = 4, X = 4, Fov = 80 },
                        new() { TimeSeconds = double.NaN, X = double.NaN, Fov = float.NaN },
                        new() { TimeSeconds = 4, X = 5, Fov = 90 }
                    }
                }
            }
        });

        CameraPath path = Check.NotNull(library.FindPath("flyover"));
        Check.Equal("Flyover", path.Name);
        Check.Equal(2, path.Tags.Count);
        Check.Equal("aerial", path.Tags[0]);
        Check.Equal("path", path.Tags[1]);
        Check.Equal("wide establishing move", path.Description);
        Check.Equal(CameraPathInterpolation.EaseInOut, path.Interpolation);
        Check.Equal(CameraPathLoopMode.Once, path.LoopMode);
        Check.Equal(2, path.Keyframes.Count);
        Check.Equal(0d, path.Keyframes[0].TimeSeconds);
        Check.Equal(0d, path.Keyframes[0].X);
        Check.Equal(70f, path.Keyframes[0].Fov);
        Check.Equal(5d, path.Keyframes[1].X);
        Check.Equal(4d, path.Keyframes[0].DurationSeconds);
        Check.Equal(0d, path.Keyframes[1].DurationSeconds);
        Check.Equal(4d, path.DurationSeconds);

        CameraPath duplicate = library.AddPath(new CameraPath { Name = "FLYOVER" });
        Check.Equal("FLYOVER (2)", duplicate.Name);
        Check.True(library.UpsertPathKeyframe(duplicate.Id, new CameraKeyframe { TimeSeconds = 2, X = 1, Fov = 65 }));
        Check.True(library.UpsertPathKeyframe(duplicate.Id, new CameraKeyframe { TimeSeconds = 2, X = 9, Fov = 75 }));
        Check.Equal(1, Check.NotNull(library.FindPath(duplicate.Id)).Keyframes.Count);
        Check.Equal(9d, Check.NotNull(library.FindPath(duplicate.Id)).Keyframes[0].X);

        Check.True(library.UpdatePathSettings(
            duplicate.Id,
            "Orbit",
            CameraPathInterpolation.Bezier,
            CameraPathLoopMode.PingPong,
            previewVisible: false));
        CameraPath updated = Check.NotNull(library.FindPath(duplicate.Id));
        Check.Equal("Orbit", updated.Name);
        Check.Equal(CameraPathInterpolation.Bezier, updated.Interpolation);
        Check.Equal(CameraPathLoopMode.PingPong, updated.LoopMode);
        Check.False(updated.PreviewVisible);
        updated.Keyframes = new List<CameraKeyframe>
        {
            new()
            {
                TimeSeconds = 0d,
                DurationSeconds = 1d,
                Yaw = CameraPathOrientation.PreserveDegrees(243.75d, 0f),
                Pitch = CameraPathOrientation.PreserveDegrees(180d, 0f),
                Roll = CameraPathOrientation.PreserveDegrees(269.14d, 0f),
                Fov = 70f
            },
            new()
            {
                TimeSeconds = 1d,
                Yaw = CameraPathOrientation.PreserveDegrees(-271.5d, 0f),
                Pitch = CameraPathOrientation.PreserveDegrees(269.14d, 0f),
                Roll = CameraPathOrientation.PreserveDegrees(-225d, 0f),
                Fov = 70f
            }
        };
        updated.Tags = new List<string> { "orbit", "ORBIT" };
        updated.Description = " updated path ";
        Check.Equal(17f, CameraPathOrientation.PreserveDegrees(double.NaN, 17f));
        Check.True(library.UpdatePath(updated));
        CameraPath orientationSaved = Check.NotNull(library.FindPath(duplicate.Id));
        Check.SequenceEqual(new[] { "orbit" }, orientationSaved.Tags);
        Check.Equal("updated path", orientationSaved.Description);
        Check.Equal(243.75f, orientationSaved.Keyframes[0].Yaw);
        Check.Equal(180f, orientationSaved.Keyframes[0].Pitch);
        Check.Equal(269.14f, orientationSaved.Keyframes[0].Roll);
        Check.Equal(-271.5f, orientationSaved.Keyframes[1].Yaw);
        Check.Equal(269.14f, orientationSaved.Keyframes[1].Pitch);
        Check.Equal(-225f, orientationSaved.Keyframes[1].Roll);
        Check.True(library.SetPathPreviewVisible(duplicate.Id, true));
        Check.True(Check.NotNull(library.FindPath(duplicate.Id)).PreviewVisible);
        Check.False(library.SetPathPreviewVisible(Guid.NewGuid(), true));
        Check.True(library.RemovePathKeyframe(duplicate.Id, 0));
        Check.Equal(1, Check.NotNull(library.FindPath(duplicate.Id)).Keyframes.Count);
        Check.True(library.RemovePath(duplicate.Id));
        Check.Equal(1, library.PathCount);
    }

    private static void CameraPathAuthoringTimesWork()
    {
        CameraPath path = new();
        Check.NearlyEqual(0d, CameraPathAuthoring.ResolveCaptureTime(
            path, 12d, playheadWasPositioned: true));
        Check.NearlyEqual(0d, CameraPathAuthoring.ResolveCaptureTime(
            path, 12d, playheadWasPositioned: false));

        int first = CameraPathTiming.UpsertKeyframe(path, new CameraKeyframe
        {
            TimeSeconds = 0d,
            DurationSeconds = CameraPathTiming.DefaultKeyframeDurationSeconds,
            X = 1d,
            Fov = 70f
        });
        Check.Equal(0, first);
        Check.NearlyEqual(1d, CameraPathAuthoring.NextAppendTime(path));
        Check.NearlyEqual(1d, CameraPathAuthoring.ResolveCaptureTime(
            path, 0d, playheadWasPositioned: false));

        int second = CameraPathTiming.UpsertKeyframe(path, new CameraKeyframe
        {
            TimeSeconds = CameraPathAuthoring.ResolveCaptureTime(
                path, 0d, playheadWasPositioned: false),
            DurationSeconds = CameraPathTiming.DefaultKeyframeDurationSeconds,
            X = 2d,
            Fov = 70f
        });
        Check.Equal(1, second);
        Check.NearlyEqual(1d, path.Keyframes[second].TimeSeconds);
        Check.NearlyEqual(2d, CameraPathAuthoring.NextAppendTime(path));

        double insertedTime = CameraPathAuthoring.ResolveCaptureTime(
            path, 0.516d, playheadWasPositioned: true);
        Check.NearlyEqual(0.5d, insertedTime);
        int inserted = CameraPathTiming.UpsertKeyframe(path, new CameraKeyframe
        {
            TimeSeconds = insertedTime,
            DurationSeconds = CameraPathTiming.DefaultKeyframeDurationSeconds,
            X = 1.5d,
            Fov = 70f
        });
        Check.Equal(1, inserted);
        Check.Equal(3, path.Keyframes.Count);
        Check.NearlyEqual(0.5d, path.Keyframes[inserted].TimeSeconds);

        double sameTick = CameraPathAuthoring.ResolveCaptureTime(
            path, 0.501d, playheadWasPositioned: true);
        int recaptured = CameraPathTiming.UpsertKeyframe(path, new CameraKeyframe
        {
            TimeSeconds = sameTick,
            DurationSeconds = CameraPathTiming.DefaultKeyframeDurationSeconds,
            X = 1.6d,
            Fov = 70f
        });
        Check.Equal(inserted, recaptured);
        Check.Equal(3, path.Keyframes.Count);

        path.Keyframes[^1].DurationSeconds = CameraPathTiming.MinKeyframeGapSeconds;
        Check.NearlyEqual(2d, CameraPathAuthoring.NextAppendTime(path));
        path.Keyframes[^1].DurationSeconds = 0.5d;
        Check.NearlyEqual(1.5d, CameraPathAuthoring.NextAppendTime(path));

        CameraPath legacyCollapsed = new()
        {
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, DurationSeconds = 0.001d, X = 1d, Fov = 70f },
                new() { TimeSeconds = 0.001d, DurationSeconds = 0.001d, X = 2d, Fov = 70f },
                new() { TimeSeconds = 0.002d, DurationSeconds = 1d, X = 3d, Fov = 70f }
            }
        };
        Check.Equal(2, CameraPathAuthoring.ExpandCollapsedKeyframes(legacyCollapsed));
        Check.NearlyEqual(0d, legacyCollapsed.Keyframes[0].TimeSeconds);
        Check.NearlyEqual(1d, legacyCollapsed.Keyframes[1].TimeSeconds);
        Check.NearlyEqual(2d, legacyCollapsed.Keyframes[2].TimeSeconds);
        Check.NearlyEqual(1d, legacyCollapsed.Keyframes[0].DurationSeconds);
        Check.NearlyEqual(1d, legacyCollapsed.Keyframes[1].DurationSeconds);

        CameraPath intentionalSubsecond = new()
        {
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, DurationSeconds = 0.25d, Fov = 70f },
                new() { TimeSeconds = 0.25d, DurationSeconds = 0.5d, Fov = 70f }
            }
        };
        Check.Equal(0, CameraPathAuthoring.ExpandCollapsedKeyframes(intentionalSubsecond));
        Check.NearlyEqual(0.25d, intentionalSubsecond.Keyframes[1].TimeSeconds);
    }

    private static void CameraPathInterpolationWorks()
    {
        CameraPath path = new()
        {
            Interpolation = CameraPathInterpolation.Linear,
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0, X = 0, Yaw = 170, Fov = 60 },
                new() { TimeSeconds = 10, X = 10, Yaw = -170, Fov = 80 }
            }
        };

        Check.True(CameraPathSampler.TryEvaluate(path, 5, out CameraKeyframe linear));
        Check.NearlyEqual(5d, linear.X);
        Check.NearlyEqual(180d, linear.Yaw);
        Check.NearlyEqual(70d, linear.Fov);

        path.Interpolation = CameraPathInterpolation.EaseInOut;
        Check.True(CameraPathSampler.TryEvaluate(path, 2.5, out CameraKeyframe eased));
        Check.NearlyEqual(1.5625d, eased.X);

        path.Interpolation = CameraPathInterpolation.Bezier;
        path.Keyframes = new List<CameraKeyframe>
        {
            new() { TimeSeconds = 0, X = 0, Fov = 70 },
            new() { TimeSeconds = 1, X = 10, Fov = 70 },
            new() { TimeSeconds = 2, X = 20, Fov = 70 },
            new() { TimeSeconds = 3, X = 30, Fov = 70 }
        };
        Check.True(CameraPathSampler.TryEvaluate(path, 1.5, out CameraKeyframe bezier));
        Check.NearlyEqual(15d, bezier.X);

        CameraPath mixed = new()
        {
            Interpolation = CameraPathInterpolation.EaseInOut,
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0, X = 0, Fov = 70, InterpolationToNext = CameraPathInterpolation.Linear },
                new() { TimeSeconds = 10, X = 10, Fov = 70, InterpolationToNext = CameraPathInterpolation.Bezier },
                new() { TimeSeconds = 20, X = 0, Fov = 70 },
                new() { TimeSeconds = 30, X = 30, Fov = 70 }
            }
        };
        Check.True(CameraPathSampler.TryEvaluate(mixed, 5, out CameraKeyframe mixedLinear));
        Check.NearlyEqual(5d, mixedLinear.X);
        Check.True(CameraPathSampler.TryEvaluate(mixed, 15, out CameraKeyframe mixedBezier));
        Check.NearlyEqual(3.75d, mixedBezier.X);
        Check.True(CameraPathSampler.TryEvaluate(mixed, 22.5, out CameraKeyframe mixedInherited));
        Check.NearlyEqual(4.6875d, mixedInherited.X);
        Check.Equal(CameraPathInterpolation.Bezier, mixed.DeepClone().Keyframes[1].InterpolationToNext);
    }

    private static void CameraPathElementTracksWork()
    {
        DirectorPathElementTrack camera = new()
        {
            TrackType = DirectorPathElementTrackType.CameraPath
        };
        DirectorPathElementTrack duplicateCamera = new()
        {
            TrackType = DirectorPathElementTrackType.CameraPath
        };
        DirectorPathElementTrack fov = new()
        {
            TrackType = DirectorPathElementTrackType.Fov
        };
        DirectorPathElementTrack speed = new()
        {
            TrackType = DirectorPathElementTrackType.Speed
        };
        CameraPath path = new()
        {
            PlaybackSpeed = 2d,
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, Fov = 60f },
                new() { TimeSeconds = 2d, Fov = 80f }
            },
            ElementTracks = new List<DirectorPathElementTrack>
            {
                camera,
                duplicateCamera,
                fov,
                speed
            },
            ElementKeyframes = new List<DirectorPathElementKeyframe>
            {
                new()
                {
                    TrackId = fov.Id,
                    TrackType = DirectorPathElementTrackType.Fov,
                    TimeSeconds = 0d,
                    Fov = 70f
                },
                new()
                {
                    TrackId = fov.Id,
                    TrackType = DirectorPathElementTrackType.Fov,
                    TimeSeconds = 2d,
                    Fov = 100f,
                    InterpolationToNext = DirectorPathElementInterpolation.EaseInOut
                },
                new()
                {
                    TrackId = speed.Id,
                    TrackType = DirectorPathElementTrackType.Speed,
                    TimeSeconds = 0d,
                    PlaybackRate = 0.5d
                }
            }
        };

        DirectorPathElementTimeline.Normalize(path);
        Check.Equal(1, path.ElementTracks.Count(track =>
            track.TrackType == DirectorPathElementTrackType.CameraPath));
        Check.True(DirectorPathElementTimeline.TrySample(
            path,
            DirectorPathElementTrackType.Fov,
            1d,
            out DirectorPathElementKeyframe sampledFov));
        Check.NearlyEqual(85d, sampledFov.Fov);
        Check.NearlyEqual(0.5d, DirectorPathElementTimeline.SamplePlaybackRate(path, 1d, 2d));

        DirectorPathElementTrack normalizedSpeed = path.ElementTracks.Single(track =>
            track.TrackType == DirectorPathElementTrackType.Speed);
        normalizedSpeed.Enabled = false;
        Check.NearlyEqual(2d, DirectorPathElementTimeline.SamplePlaybackRate(path, 1d, 2d));
        normalizedSpeed.Enabled = true;
        CameraPath clone = path.DeepClone();
        clone.ElementKeyframes[0].Fov = 20f;
        Check.NearlyEqual(70d, path.ElementKeyframes.First(frame =>
            frame.TrackId == fov.Id).Fov);

        CameraPath imported = CameraPathJsonIO.Import(CameraPathJsonIO.Export(path));
        Check.Equal(path.ElementTracks.Count, imported.ElementTracks.Count);
        Check.Equal(path.ElementKeyframes.Count, imported.ElementKeyframes.Count);
        Check.NearlyEqual(0.5d,
            DirectorPathElementTimeline.SamplePlaybackRate(imported, 1d, 2d));
    }

    private static void CameraPathElementInsertionWorks()
    {
        DirectorPathElementTrack speed = new()
        {
            TrackType = DirectorPathElementTrackType.Speed
        };
        DirectorPathElementTrack shake = new()
        {
            TrackType = DirectorPathElementTrackType.CameraShake
        };
        DirectorPathElementTrack lookAt = new()
        {
            TrackType = DirectorPathElementTrackType.LookAt
        };
        DirectorPathElementTrack marker = new()
        {
            TrackType = DirectorPathElementTrackType.Marker
        };
        DirectorPathElementTrack fov = new()
        {
            TrackType = DirectorPathElementTrackType.Fov
        };
        Guid markerId = Guid.NewGuid();
        CameraPath path = new()
        {
            PlaybackSpeed = 2.5d,
            ElementTracks = new List<DirectorPathElementTrack>
            {
                speed,
                shake,
                lookAt,
                marker,
                fov
            },
            ElementKeyframes = new List<DirectorPathElementKeyframe>
            {
                new()
                {
                    TrackId = shake.Id,
                    TrackType = DirectorPathElementTrackType.CameraShake,
                    TimeSeconds = 0d,
                    FrequencyX = 2f,
                    AmplitudeX = 4f,
                    FrequencyY = 3f,
                    AmplitudeY = 5f,
                    InterpolationToNext = DirectorPathElementInterpolation.EaseInOut
                },
                new()
                {
                    TrackId = shake.Id,
                    TrackType = DirectorPathElementTrackType.CameraShake,
                    TimeSeconds = 2d,
                    FrequencyX = 6f,
                    AmplitudeX = 8f,
                    FrequencyY = 7f,
                    AmplitudeY = 9f
                },
                new()
                {
                    TrackId = lookAt.Id,
                    TrackType = DirectorPathElementTrackType.LookAt,
                    TimeSeconds = 0d,
                    TargetEntityId = 42L,
                    BodyPart = "root",
                    OffsetX = 1d,
                    X = 10d,
                    Dimension = 3,
                    InterpolationToNext = DirectorPathElementInterpolation.EaseInOut
                },
                new()
                {
                    TrackId = lookAt.Id,
                    TrackType = DirectorPathElementTrackType.LookAt,
                    TimeSeconds = 2d,
                    TargetEntityId = 42L,
                    BodyPart = "root",
                    OffsetX = 3d,
                    X = 14d,
                    Dimension = 3
                },
                new()
                {
                    Id = markerId,
                    TrackId = marker.Id,
                    TrackType = DirectorPathElementTrackType.Marker,
                    TimeSeconds = 1d,
                    Label = "Existing cue",
                    InterpolationToNext = DirectorPathElementInterpolation.Hold
                }
            }
        };
        DirectorPathElementTimeline.Normalize(path);

        Check.True(DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
            path, speed.Id, 1d, null, "Marker", out DirectorPathElementKeyframe speedFrame));
        Check.NearlyEqual(2.5d, speedFrame.PlaybackRate);

        Check.True(DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
            path, shake.Id, 1d, null, "Marker", out DirectorPathElementKeyframe shakeFrame));
        Check.NearlyEqual(4d, shakeFrame.FrequencyX);
        Check.NearlyEqual(6d, shakeFrame.AmplitudeX);
        Check.NearlyEqual(5d, shakeFrame.FrequencyY);
        Check.NearlyEqual(7d, shakeFrame.AmplitudeY);
        Check.Equal(DirectorPathElementInterpolation.EaseInOut,
            shakeFrame.InterpolationToNext);

        Check.True(DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
            path, lookAt.Id, 1d, null, "Marker", out DirectorPathElementKeyframe lookAtFrame));
        Check.Equal(42L, lookAtFrame.TargetEntityId);
        Check.Equal("root", lookAtFrame.BodyPart);
        Check.NearlyEqual(2d, lookAtFrame.OffsetX);
        Check.NearlyEqual(12d, lookAtFrame.X);
        Check.Equal(3, lookAtFrame.Dimension);
        Check.Equal(DirectorPathElementInterpolation.EaseInOut,
            lookAtFrame.InterpolationToNext);

        int markerCount = path.ElementKeyframes.Count(frame => frame.TrackId == marker.Id);
        Check.True(DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
            path, marker.Id, 1d, null, "New cue", out DirectorPathElementKeyframe existingMarker));
        Check.Equal(markerId, existingMarker.Id);
        Check.Equal("Existing cue", existingMarker.Label);
        Check.Equal(markerCount, path.ElementKeyframes.Count(frame => frame.TrackId == marker.Id));
        Check.True(DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
            path, marker.Id, 2d, null, "New cue", out DirectorPathElementKeyframe newMarker));
        Check.Equal("New cue", newMarker.Label);

        int fovCount = path.ElementKeyframes.Count(frame => frame.TrackId == fov.Id);
        Check.False(DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
            path, fov.Id, 1d, null, "Marker", out _));
        Check.Equal(fovCount, path.ElementKeyframes.Count(frame => frame.TrackId == fov.Id));
        CameraKeyframe captured = new() { Fov = 83f, Roll = 12f };
        Check.True(DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
            path, fov.Id, 1d, captured, "Marker", out DirectorPathElementKeyframe fovFrame));
        Check.NearlyEqual(83d, fovFrame.Fov);
        fovFrame.InterpolationToNext = DirectorPathElementInterpolation.EaseInOut;
        Check.False(DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
            path, fov.Id, 1d, null, "Marker", out DirectorPathElementKeyframe sameFov));
        captured.Fov = 91f;
        Check.True(DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
            path, fov.Id, 1d, captured, "Marker", out sameFov));
        Check.Equal(fovFrame.Id, sameFov.Id);
        Check.NearlyEqual(91d, sameFov.Fov);
        Check.Equal(DirectorPathElementInterpolation.EaseInOut,
            sameFov.InterpolationToNext);
        Check.Equal(fovCount + 1, path.ElementKeyframes.Count(frame => frame.TrackId == fov.Id));
    }

    private static void CameraPathAdvancedInterpolationAndJsonWorks()
    {
        CameraPath path = new()
        {
            Name = "Advanced",
            Interpolation = CameraPathInterpolation.Linear,
            Keyframes = new List<CameraKeyframe>
            {
                new()
                {
                    Name = "Opening",
                    TimeSeconds = 0d,
                    DurationSeconds = 10d,
                    HoldSeconds = 4d,
                    X = 0d,
                    Fov = 70f
                },
                new() { TimeSeconds = 10d, X = 10d, Fov = 80f }
            }
        };

        Check.True(CameraPathSampler.TryEvaluate(path, 2d, out CameraKeyframe held));
        Check.NearlyEqual(0d, held.X);
        Check.True(CameraPathSampler.TryEvaluate(path, 7d, out CameraKeyframe afterHold));
        Check.NearlyEqual(5d, afterHold.X);
        Check.True(CameraPathTiming.TrySetKeyframeHold(path, 0, 20d, out double clampedHold));
        Check.NearlyEqual(10d, clampedHold);

        path.Keyframes[0].HoldSeconds = 0d;
        path.Keyframes[0].InterpolationToNext = CameraPathInterpolation.Step;
        Check.True(CameraPathSampler.TryEvaluate(path, 9d, out CameraKeyframe stepped));
        Check.NearlyEqual(0d, stepped.X);
        Check.True(CameraPathSampler.TryEvaluate(path, 10d, out CameraKeyframe stepEnd));
        Check.NearlyEqual(10d, stepEnd.X);

        path.Keyframes[0].InterpolationToNext = CameraPathInterpolation.Bezier;
        path.Keyframes[0].BezierHandlesEnabled = true;
        path.Keyframes[0].BezierOutX = 10d;
        path.Keyframes[1].BezierHandlesEnabled = true;
        path.Keyframes[1].BezierInX = 0d;
        Check.True(CameraPathSampler.TryEvaluate(path, 5d, out CameraKeyframe customBezier));
        Check.NearlyEqual(8.75d, customBezier.X);

        string json = CameraPathJsonIO.Export(path);
        CameraPath imported = CameraPathJsonIO.Import(json);
        Check.Equal("Opening", imported.Keyframes[0].Name);
        Check.Equal(CameraPathInterpolation.Bezier, imported.Keyframes[0].InterpolationToNext);
        Check.True(imported.Keyframes[0].BezierHandlesEnabled);
        Check.NearlyEqual(10d, imported.Keyframes[0].BezierOutX);
        Check.True(imported.Keyframes[1].BezierHandlesEnabled);
        Check.True(CameraPathTiming.TryScaleTimeline(imported, 20d));
        Check.NearlyEqual(20d, imported.DurationSeconds);
        Check.NearlyEqual(20d, imported.Keyframes[1].TimeSeconds);
    }

    private static void CameraPathPlaybackModesWork()
    {
        CameraPathTime once = CameraPathSampler.ResolvePlaybackTime(10, 3, CameraPathLoopMode.Once, reverse: false);
        Check.NearlyEqual(3d, once.TimeSeconds);
        Check.False(once.Completed);
        CameraPathTime onceReverse = CameraPathSampler.ResolvePlaybackTime(10, 12, CameraPathLoopMode.Once, reverse: true);
        Check.NearlyEqual(0d, onceReverse.TimeSeconds);
        Check.True(onceReverse.Completed);
        CameraPathTime onceAtEnd = CameraPathSampler.ResolvePlaybackTime(10, 10, CameraPathLoopMode.Once, reverse: false);
        Check.NearlyEqual(10d, onceAtEnd.TimeSeconds);
        Check.True(onceAtEnd.Completed);

        CameraPathTime loop = CameraPathSampler.ResolvePlaybackTime(10, 12, CameraPathLoopMode.Loop, reverse: false);
        Check.NearlyEqual(2d, loop.TimeSeconds);
        CameraPathTime loopAtEnd = CameraPathSampler.ResolvePlaybackTime(10, 10, CameraPathLoopMode.Loop, reverse: false);
        Check.NearlyEqual(0d, loopAtEnd.TimeSeconds);
        CameraPathTime loopReverse = CameraPathSampler.ResolvePlaybackTime(10, 12, CameraPathLoopMode.Loop, reverse: true);
        Check.NearlyEqual(8d, loopReverse.TimeSeconds);
        CameraPathTime loopAtStartReverse = CameraPathSampler.ResolvePlaybackTime(10, 0, CameraPathLoopMode.Loop, reverse: true);
        Check.NearlyEqual(10d, loopAtStartReverse.TimeSeconds);

        CameraPathTime pingPong = CameraPathSampler.ResolvePlaybackTime(10, 12, CameraPathLoopMode.PingPong, reverse: false);
        Check.NearlyEqual(8d, pingPong.TimeSeconds);
        CameraPathTime pingPongReverse = CameraPathSampler.ResolvePlaybackTime(10, 12, CameraPathLoopMode.PingPong, reverse: true);
        Check.NearlyEqual(2d, pingPongReverse.TimeSeconds);
        CameraPathTime pingPongAtTurn = CameraPathSampler.ResolvePlaybackTime(10, 10, CameraPathLoopMode.PingPong, reverse: false);
        Check.NearlyEqual(10d, pingPongAtTurn.TimeSeconds);
        CameraPathTime pingPongReverseAtStart = CameraPathSampler.ResolvePlaybackTime(10, 0, CameraPathLoopMode.PingPong, reverse: true);
        Check.NearlyEqual(10d, pingPongReverseAtStart.TimeSeconds);

        CameraPathTime invalid = CameraPathSampler.ResolvePlaybackTime(double.NaN, double.PositiveInfinity, CameraPathLoopMode.Loop, reverse: false);
        Check.NearlyEqual(0d, invalid.TimeSeconds);
        Check.True(invalid.Completed);

        CameraPath invalidPath = new()
        {
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0, X = 1, Fov = 70 }
            }
        };
        Check.False(CameraPathSampler.TryEvaluate(invalidPath, double.NaN, out _));
    }

    private static void ReplayCameraStateIsExplicit()
    {
        Guid pathId = Guid.NewGuid();
        DirectorReplayCameraState inactive = DirectorReplayCameraState.Inactive;
        Check.Equal(DirectorReplayCameraMode.None, inactive.Mode);
        Check.Equal(Guid.Empty, inactive.SourceId);

        DirectorReplayCameraState synchronizedPath = DirectorReplayCameraState.FromSource(
            DirectorReplayCameraMode.SyncedPath,
            pathId);
        Check.True(synchronizedPath.Is(DirectorReplayCameraMode.SyncedPath, pathId));
        Check.False(synchronizedPath.Is(DirectorReplayCameraMode.Path, pathId));

        DirectorReplayCameraState free = DirectorReplayCameraState.FromSource(
            DirectorReplayCameraMode.Free,
            pathId);
        Check.Equal(DirectorReplayCameraMode.Free, free.Mode);
        Check.Equal(Guid.Empty, free.SourceId);

        DirectorReplayCameraState fixedView = DirectorReplayCameraState.FromSource(
            DirectorReplayCameraMode.Fixed,
            Guid.NewGuid());
        Check.Equal(DirectorReplayCameraMode.Fixed, fixedView.Mode);
        Check.False(fixedView.SourceId == Guid.Empty);
    }

    private static void ReplayPathsStayAlignedToTimeline()
    {
        Guid replayId = Guid.NewGuid();
        CameraPath path = new()
        {
            PlaybackSpeed = 2d,
            LoopMode = CameraPathLoopMode.Once,
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, X = 0d, Fov = 70f },
                new() { TimeSeconds = 10d, X = 10d, Fov = 70f }
            }
        };

        Check.NearlyEqual(0d, DirectorReplayPathTimeline.StartTimeSeconds(path, replayId));
        Check.NearlyEqual(6d, DirectorReplayPathTimeline.ResolvePathTime(path, replayId, 3d));
        Check.NearlyEqual(2d, DirectorReplayPathTimeline.ResolveReplayTime(path, replayId, 4d));

        path.SourceReplayId = replayId;
        path.ReplayTimeOffsetSeconds = 2d;
        Check.NearlyEqual(2d, DirectorReplayPathTimeline.StartTimeSeconds(path, replayId));
        Check.NearlyEqual(3d, DirectorReplayPathTimeline.ResolvePathTime(path, replayId, 5d));
        Check.NearlyEqual(5d, DirectorReplayPathTimeline.ResolveReplayTime(path, replayId, 3d));

        Guid otherReplayId = Guid.NewGuid();
        Check.NearlyEqual(0d, DirectorReplayPathTimeline.StartTimeSeconds(path, otherReplayId));
        Check.NearlyEqual(6d, DirectorReplayPathTimeline.ResolvePathTime(path, otherReplayId, 3d));
    }

    private static void CameraPathRetimingAndSpeedWork()
    {
        CameraPath accelerated = new()
        {
            Interpolation = CameraPathInterpolation.Linear,
            Keyframes = new List<CameraKeyframe>
            {
                new()
                {
                    TimeSeconds = 0d,
                    X = 0d,
                    Fov = 70f,
                    SegmentStartSpeed = 0d,
                    SegmentEndSpeed = 2d
                },
                new() { TimeSeconds = 10d, X = 10d, Fov = 70f }
            }
        };
        Check.True(CameraPathSampler.TryEvaluate(accelerated, 5d, out CameraKeyframe acceleratedMid));
        Check.NearlyEqual(2.5d, acceleratedMid.X);
        Check.True(CameraPathTiming.TrySetSegmentSpeeds(
            accelerated, 0, 2d, 0d, out double startSpeed, out double endSpeed));
        Check.NearlyEqual(2d, startSpeed);
        Check.NearlyEqual(0d, endSpeed);
        Check.True(CameraPathSampler.TryEvaluate(accelerated, 5d, out CameraKeyframe deceleratedMid));
        Check.NearlyEqual(7.5d, deceleratedMid.X);
        Check.False(CameraPathTiming.TrySetSegmentSpeeds(accelerated, 0, 0d, 0d, out _, out _));
        Check.False(CameraPathTiming.TrySetSegmentSpeeds(accelerated, 0, -1d, 1d, out _, out _));
        Check.False(CameraPathTiming.TrySetSegmentSpeeds(accelerated, 0, 1d, 9d, out _, out _));

        CameraPath path = new()
        {
            PlaybackSpeed = 2d,
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0, X = 0, Fov = 70 },
                new() { TimeSeconds = 5, X = 5, Fov = 70 },
                new() { TimeSeconds = 10, X = 10, Fov = 70 }
            }
        };

        Check.NearlyEqual(5d, path.PlaybackDurationSeconds);
        Check.True(CameraPathTiming.TrySetKeyframeTime(path, 1, 7.5, out double moved));
        Check.NearlyEqual(7.5d, moved);
        Check.NearlyEqual(12.5d, path.Keyframes[2].TimeSeconds);
        Check.NearlyEqual(5d, path.Keyframes[1].DurationSeconds);
        Check.True(CameraPathTiming.TrySetKeyframeDuration(path, 1, 4d, out double duration));
        Check.NearlyEqual(4d, duration);
        Check.NearlyEqual(11.5d, path.Keyframes[2].TimeSeconds);

        int segment = CameraPathTiming.InsertKeyframe(
            path,
            2,
            new CameraKeyframe { X = 42, Fov = 80 },
            3d);
        Check.Equal(2, segment);
        Check.Equal(4, path.Keyframes.Count);
        Check.NearlyEqual(11.5d, path.Keyframes[segment].TimeSeconds);
        Check.NearlyEqual(3d, path.Keyframes[segment].DurationSeconds);
        Check.NearlyEqual(14.5d, path.Keyframes[3].TimeSeconds);
        Check.True(CameraPathTiming.RemoveKeyframe(path, segment));
        Check.Equal(3, path.Keyframes.Count);
        Check.NearlyEqual(11.5d, path.Keyframes[2].TimeSeconds);

        CameraPath selectedDelete = new()
        {
            Keyframes = Enumerable.Range(0, 5)
                .Select(index => new CameraKeyframe
                {
                    Name = $"key-{index}",
                    TimeSeconds = index * 2d,
                    X = index,
                    Fov = 70f
                })
                .ToList()
        };
        Check.Equal(2, CameraPathTiming.RemoveKeyframesPreservingTimeline(
            selectedDelete, new[] { 1, 3 }));
        Check.Equal(3, selectedDelete.Keyframes.Count);
        Check.Equal("key-0", selectedDelete.Keyframes[0].Name);
        Check.Equal("key-2", selectedDelete.Keyframes[1].Name);
        Check.Equal("key-4", selectedDelete.Keyframes[2].Name);
        Check.NearlyEqual(0d, selectedDelete.Keyframes[0].TimeSeconds);
        Check.NearlyEqual(4d, selectedDelete.Keyframes[1].TimeSeconds);
        Check.NearlyEqual(8d, selectedDelete.Keyframes[2].TimeSeconds);

        CameraPath absoluteInsert = new()
        {
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, X = 0d, Fov = 70f },
                new() { TimeSeconds = 10d, X = 10d, Fov = 70f }
            }
        };
        int absoluteIndex = CameraPathTiming.InsertKeyframeAtTime(
            absoluteInsert,
            new CameraKeyframe { X = 4d, Fov = 75f },
            4d,
            2d);
        Check.Equal(1, absoluteIndex);
        Check.NearlyEqual(4d, absoluteInsert.Keyframes[absoluteIndex].TimeSeconds);
        Check.NearlyEqual(10d, absoluteInsert.Keyframes[2].TimeSeconds);

        Check.True(CameraPathTiming.TrySetKeyframeDuration(path, 2, 2d, out _));
        Check.NearlyEqual(13.5d, path.DurationSeconds);
        Check.NearlyEqual(6.75d, path.PlaybackDurationSeconds);

        int inserted = CameraPathTiming.UpsertKeyframe(
            path,
            new CameraKeyframe { TimeSeconds = 5, X = 42, Fov = 80 });
        Check.Equal(1, inserted);
        Check.Equal(4, path.Keyframes.Count);
        Check.NearlyEqual(42d, path.Keyframes[inserted].X);
        path.Keyframes[inserted].InterpolationToNext = CameraPathInterpolation.Bezier;
        int recaptured = CameraPathTiming.UpsertKeyframe(
            path,
            new CameraKeyframe { TimeSeconds = 5, X = 43, Fov = 81 });
        Check.Equal(inserted, recaptured);
        Check.Equal(CameraPathInterpolation.Bezier, path.Keyframes[recaptured].InterpolationToNext);
        path.Keyframes[recaptured].Marker = "Action";
        recaptured = CameraPathTiming.UpsertKeyframe(
            path,
            new CameraKeyframe { TimeSeconds = 5, X = 44, Fov = 82 });
        Check.Equal("Action", path.Keyframes[recaptured].Marker);
        path.Keyframes[recaptured].SegmentStartSpeed = 0.5d;
        path.Keyframes[recaptured].SegmentEndSpeed = 2.5d;
        recaptured = CameraPathTiming.UpsertKeyframe(
            path,
            new CameraKeyframe { TimeSeconds = 5, X = 45, Fov = 83 });
        Check.NearlyEqual(0.5d, path.Keyframes[recaptured].SegmentStartSpeed);
        Check.NearlyEqual(2.5d, path.Keyframes[recaptured].SegmentEndSpeed);

        CameraLibrary library = new(new CameraLibraryDocument
        {
            SchemaVersion = 2,
            Paths = new List<CameraPath>
            {
                new() { Name = "Migrated", PlaybackSpeed = double.NaN }
            }
        });
        CameraPath migrated = Check.NotNull(library.FindPath("Migrated"));
        Check.NearlyEqual(CameraPathTiming.DefaultPlaybackSpeed, migrated.PlaybackSpeed);

        path.Id = migrated.Id;
        path.Name = "Retimed";
        path.PlaybackSpeed = 3.5d;
        Check.True(library.UpdatePath(path));
        CameraPath updated = Check.NotNull(library.FindPath("Retimed"));
        Check.NearlyEqual(3.5d, updated.PlaybackSpeed);
        Check.NearlyEqual(0.5d, updated.Keyframes[recaptured].SegmentStartSpeed);
        Check.NearlyEqual(2.5d, updated.Keyframes[recaptured].SegmentEndSpeed);
        Check.NearlyEqual(13.5d, updated.DurationSeconds);
        Check.Equal(4, updated.Keyframes.Count);
        Check.NearlyEqual(
            updated.Keyframes[1].TimeSeconds - updated.Keyframes[0].TimeSeconds,
            updated.Keyframes[0].DurationSeconds);
    }

    private static void CameraPathScissorsKeepLeft()
    {
        CameraPath path = new()
        {
            Interpolation = CameraPathInterpolation.Linear,
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, X = 0d, Fov = 70f },
                new() { TimeSeconds = 5d, X = 10d, Fov = 80f },
                new() { TimeSeconds = 10d, DurationSeconds = 3d, X = 20d, Fov = 90f }
            }
        };

        Check.True(CameraPathTiming.TryCutKeepLeft(path, 7.5d, out int endpoint));
        Check.Equal(3, path.Keyframes.Count);
        Check.Equal(2, endpoint);
        Check.NearlyEqual(7.5d, path.Keyframes[endpoint].TimeSeconds);
        Check.NearlyEqual(15d, path.Keyframes[endpoint].X);
        Check.NearlyEqual(0d, path.Keyframes[endpoint].DurationSeconds);
        Check.NearlyEqual(7.5d, path.DurationSeconds);

        Check.True(CameraPathTiming.TryCutKeepLeft(path, 5d, out endpoint));
        Check.Equal(2, path.Keyframes.Count);
        Check.Equal(1, endpoint);
        Check.NearlyEqual(5d, path.DurationSeconds);
        Check.False(CameraPathTiming.TryCutKeepLeft(path, 0d, out _));
        Check.False(CameraPathTiming.TryCutKeepLeft(path, path.DurationSeconds, out _));

        CameraPath heldEnd = new()
        {
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, X = 0d, Fov = 70f },
                new() { TimeSeconds = 4d, DurationSeconds = 4d, X = 4d, Fov = 70f }
            }
        };
        Check.True(CameraPathTiming.TryCutKeepLeft(heldEnd, 6d, out endpoint));
        Check.Equal(1, endpoint);
        Check.NearlyEqual(2d, heldEnd.Keyframes[endpoint].DurationSeconds);
        Check.NearlyEqual(6d, heldEnd.DurationSeconds);
    }

    private static void CameraPathSegmentEditingWorks()
    {
        CameraPath path = new()
        {
            Interpolation = CameraPathInterpolation.Linear,
            Keyframes = new List<CameraKeyframe>
            {
                new()
                {
                    TimeSeconds = 0d,
                    X = 0d,
                    Fov = 70f,
                    Marker = "Opening",
                    SegmentStartSpeed = 0.5d,
                    SegmentEndSpeed = 2.5d
                },
                new() { TimeSeconds = 4d, X = 8d, Fov = 70f },
                new() { TimeSeconds = 8d, DurationSeconds = 2d, X = 16d, Fov = 70f }
            }
        };

        Check.True(CameraPathTiming.TrySplitSegment(path, 2d, out int splitIndex));
        Check.Equal(1, splitIndex);
        Check.Equal(4, path.Keyframes.Count);
        Check.NearlyEqual(8d / 3d, path.Keyframes[splitIndex].X);
        Check.NearlyEqual(2d, path.Keyframes[splitIndex].DurationSeconds);
        Check.Equal(string.Empty, path.Keyframes[splitIndex].Marker);
        Check.NearlyEqual(1.5d, path.Keyframes[0].SegmentEndSpeed);
        Check.NearlyEqual(1.5d, path.Keyframes[splitIndex].SegmentStartSpeed);
        Check.NearlyEqual(2.5d, path.Keyframes[splitIndex].SegmentEndSpeed);

        Check.True(CameraPathTiming.TrySetKeyframeDuration(path, splitIndex, 3d, out _));
        Check.NearlyEqual(5d, path.Keyframes[2].TimeSeconds);
        Check.NearlyEqual(9d, path.Keyframes[3].TimeSeconds);
        Check.True(CameraPathTiming.TryDeleteSegment(path, splitIndex));
        Check.Equal(3, path.Keyframes.Count);
        Check.NearlyEqual(2d, path.Keyframes[1].TimeSeconds);
        Check.NearlyEqual(6d, path.Keyframes[2].TimeSeconds);
        Check.Equal("Opening", path.Keyframes[0].Marker);

        Check.True(CameraPathTiming.TryDeleteSegment(path, 2));
        Check.NearlyEqual(6d, path.DurationSeconds);
        Check.False(CameraPathTiming.TryDeleteSegment(path, 2));
        Check.False(CameraPathTiming.TryDeleteSegment(
            new CameraPath
            {
                Keyframes = new List<CameraKeyframe>
                {
                    new() { TimeSeconds = 0d },
                    new() { TimeSeconds = 1d }
                }
            },
            0));
    }

    private static void CameraPathEditHistoryWorks()
    {
        CameraPath path = new()
        {
            Id = Guid.NewGuid(),
            Name = "History",
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, DurationSeconds = 1d, Fov = 70f },
                new() { TimeSeconds = 1d, Fov = 80f }
            }
        };
        CameraPathEditHistory history = new(capacity: 2);

        CameraPath before = path.DeepClone();
        path.Keyframes[0].Fov = 120f;
        Check.True(history.RecordChange(before, path));
        Check.True(history.CanUndo);
        Check.False(history.RecordChange(path.DeepClone(), path));

        Check.True(history.TryUndo(path, out CameraPath undone));
        Check.Equal(70f, undone.Keyframes[0].Fov);
        Check.True(history.CanRedo);
        Check.True(history.TryRedo(undone, out CameraPath redone));
        Check.Equal(120f, redone.Keyframes[0].Fov);

        CameraPath branchBefore = undone.DeepClone();
        undone.Keyframes[1].Yaw = 45f;
        Check.True(history.RecordChange(branchBefore, undone));
        Check.False(history.CanRedo);
        undone.Keyframes[0].Fov = 10f;
        Check.Equal(70f, branchBefore.Keyframes[0].Fov);

        CameraPathEditHistory merged = new();
        CameraPath speedPath = path.DeepClone();
        CameraPath speedBefore = speedPath.DeepClone();
        speedPath.PlaybackSpeed = 1.5d;
        Check.True(merged.RecordChange(speedBefore, speedPath, "speed"));
        CameraPath secondSpeedBefore = speedPath.DeepClone();
        speedPath.PlaybackSpeed = 2d;
        Check.True(merged.RecordChange(secondSpeedBefore, speedPath, "speed"));
        Check.True(merged.TryUndo(speedPath, out CameraPath speedUndone));
        Check.NearlyEqual(speedBefore.PlaybackSpeed, speedUndone.PlaybackSpeed);
        Check.False(merged.TryUndo(speedUndone, out _));
    }

    private static void CameraSequencesNormalizeAndEdit()
    {
        CameraLibrary library = new();
        List<CameraPreset> cameras = new();
        for (int index = 0; index < 150; index++)
        {
            cameras.Add(library.Add(new CameraPreset
            {
                Name = $"Camera {index:000}",
                X = index,
                Fov = 70
            }));
        }

        Check.Equal(150, library.Count);
        CameraSequence sequence = library.AddSequence(new CameraSequence
        {
            Name = "  Opening  ",
            LoopMode = (CameraSequenceLoopMode)999,
            Shots = new List<CameraSequenceShot>
            {
                new() { CameraId = cameras[0].Id, DurationSeconds = double.NaN },
                new() { CameraId = Guid.NewGuid(), DurationSeconds = 10 },
                new() { CameraId = cameras[1].Id, DurationSeconds = 0 }
            }
        });

        Check.Equal("Opening", sequence.Name);
        Check.Equal(CameraSequenceLoopMode.Once, sequence.LoopMode);
        Check.Equal(2, sequence.Shots.Count);
        Check.NearlyEqual(CameraSequenceSampler.DefaultShotDurationSeconds, sequence.Shots[0].DurationSeconds);
        Check.NearlyEqual(CameraSequenceSampler.MinShotDurationSeconds, sequence.Shots[1].DurationSeconds);

        CameraSequence duplicate = library.AddSequence(new CameraSequence { Name = "OPENING" });
        Check.Equal("OPENING (2)", duplicate.Name);
        sequence.Name = "Program";
        sequence.LoopMode = CameraSequenceLoopMode.Loop;
        sequence.Shots.Add(new CameraSequenceShot { CameraId = cameras[0].Id, DurationSeconds = 2.5 });
        Check.True(library.UpdateSequence(sequence));
        CameraSequence updated = Check.NotNull(library.FindSequence("program"));
        Check.Equal(CameraSequenceLoopMode.Loop, updated.LoopMode);
        Check.Equal(3, updated.Shots.Count);
        Check.NearlyEqual(7.55d, updated.DurationSeconds);

        updated.Shots.Clear();
        Check.Equal(3, Check.NotNull(library.FindSequence(sequence.Id)).Shots.Count);
        Check.True(library.Remove(cameras[0].Id));
        Check.Equal(1, Check.NotNull(library.FindSequence(sequence.Id)).Shots.Count);
        Check.True(library.RemoveSequence(duplicate.Id));
        Check.Equal(1, library.SequenceCount);
    }

    private static void CameraSequencePlaybackTimingWorks()
    {
        CameraSequence sequence = new()
        {
            Shots = new List<CameraSequenceShot>
            {
                new() { CameraId = Guid.NewGuid(), DurationSeconds = 2 },
                new() { CameraId = Guid.NewGuid(), DurationSeconds = 3 }
            }
        };

        Check.True(CameraSequenceSampler.TryResolve(sequence, 0, out CameraSequenceTime start));
        Check.Equal(0, start.ShotIndex);
        Check.NearlyEqual(0d, start.ShotElapsedSeconds);
        Check.False(start.Completed);

        Check.True(CameraSequenceSampler.TryResolve(sequence, 2, out CameraSequenceTime second));
        Check.Equal(1, second.ShotIndex);
        Check.NearlyEqual(0d, second.ShotElapsedSeconds);
        Check.True(CameraSequenceSampler.TryResolve(sequence, 4.5, out CameraSequenceTime nearEnd));
        Check.Equal(1, nearEnd.ShotIndex);
        Check.NearlyEqual(2.5d, nearEnd.ShotElapsedSeconds);

        Check.True(CameraSequenceSampler.TryResolve(sequence, 5, out CameraSequenceTime completed));
        Check.Equal(1, completed.ShotIndex);
        Check.NearlyEqual(3d, completed.ShotElapsedSeconds);
        Check.True(completed.Completed);

        sequence.LoopMode = CameraSequenceLoopMode.Loop;
        Check.True(CameraSequenceSampler.TryResolve(sequence, 5, out CameraSequenceTime looped));
        Check.Equal(0, looped.ShotIndex);
        Check.NearlyEqual(0d, looped.SequenceElapsedSeconds);
        Check.False(looped.Completed);
        Check.True(CameraSequenceSampler.TryResolve(sequence, 7, out CameraSequenceTime loopedSecond));
        Check.Equal(1, loopedSecond.ShotIndex);
        Check.NearlyEqual(0d, loopedSecond.ShotElapsedSeconds);
        Check.True(CameraSequenceSampler.TryResolve(
            sequence,
            5d,
            CameraSequenceLoopMode.Once,
            out CameraSequenceTime forcedOnce));
        Check.True(forcedOnce.Completed);
        Check.Equal(1, forcedOnce.ShotIndex);
        Check.True(CameraSequenceSampler.TryResolve(
            sequence,
            5d,
            CameraSequenceLoopMode.Loop,
            out CameraSequenceTime forcedLoop));
        Check.False(forcedLoop.Completed);
        Check.Equal(0, forcedLoop.ShotIndex);

        Check.False(CameraSequenceSampler.TryResolve(sequence, -1, out _));
        Check.False(CameraSequenceSampler.TryResolve(new CameraSequence(), 0, out _));
    }

    private static void WorkflowCardsPersistAndSplitOutputs()
    {
        CameraLibrary library = new();
        CameraPreset first = library.Add(new CameraPreset { Name = "Wide", Fov = 70f });
        CameraPreset second = library.Add(new CameraPreset { Name = "Close", Fov = 70f });
        CameraPreset third = library.Add(new CameraPreset { Name = "Roof", Fov = 70f });
        DirectorWorkflow workflow = library.AddWorkflow(new DirectorWorkflow
        {
            Name = "Build",
            Cards = new List<DirectorWorkflowCard>
            {
                new()
                {
                    Name = "Foundation A",
                    SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = first.Id,
                    StartSeconds = 0d,
                    EndSeconds = 10d,
                    Lane = 0,
                    TransitionIn = new DirectorVideoTransition
                    {
                        Type = DirectorVideoTransitionType.FadeFromBlack,
                        DurationSeconds = 0.75d
                    }
                },
                new()
                {
                    Name = "Foundation B",
                    SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = second.Id,
                    StartSeconds = 5d,
                    EndSeconds = 8d,
                    Lane = 1
                },
                new()
                {
                    Name = "Roof",
                    SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = third.Id,
                    StartSeconds = 12d,
                    EndSeconds = 18d,
                    Lane = 0
                }
            },
            AudioClips = new List<DirectorTimelineAudioClip>
            {
                new()
                {
                    Name = "Guide",
                    InputPath = " guide.wav ",
                    TimelineStartSeconds = 1d,
                    DurationSeconds = 8d,
                    PlaybackRate = 1d
                }
            }
        });

        Check.Equal(1, library.WorkflowCount);
        Check.Equal(CameraLibraryDocument.CurrentSchemaVersion, library.Document.SchemaVersion);
        Check.Equal(3, workflow.Cards.Count);
        Check.Equal(1, workflow.AudioClips.Count);
        Check.Equal("guide.wav", workflow.AudioClips[0].InputPath);
        Check.NearlyEqual(0.75d, Check.NotNull(workflow.Cards
            .Single(card => card.Name == "Foundation A").TransitionIn).DurationSeconds);
        Check.Equal(2, DirectorWorkflowPlanner.FindAvailableLane(workflow, 5d, 8d));
        Check.Equal(0, DirectorWorkflowPlanner.FindAvailableLane(workflow, 18d, 20d));
        IReadOnlyList<DirectorWorkflowExportPass> passes = DirectorWorkflowPlanner.Create(workflow);
        Check.Equal(3, passes.Count);
        Check.Equal(2, passes.Count(pass => pass.SplitForOverlap));
        DirectorWorkflowExportPass continuous = Check.NotNull(passes.Single(pass => !pass.SplitForOverlap));
        Check.Equal(1, continuous.Segments.Count);
        Check.Equal("Roof", Check.NotNull(continuous.Resolve(14d)).CardName);

        DirectorWorkflow nonOverlapping = new()
        {
            Id = Guid.NewGuid(),
            Name = "Continuous",
            Cards = new List<DirectorWorkflowCard>
            {
                new()
                {
                    Id = Guid.NewGuid(), Name = "One", SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = first.Id, StartSeconds = 0d, EndSeconds = 5d
                },
                new()
                {
                    Id = Guid.NewGuid(), Name = "Two", SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = second.Id, StartSeconds = 5d, EndSeconds = 9d
                }
            }
        };
        IReadOnlyList<DirectorWorkflowExportPass> continuousPasses = DirectorWorkflowPlanner.Create(nonOverlapping);
        Check.Equal(1, continuousPasses.Count);
        Check.Equal(2, continuousPasses[0].Segments.Count);
        Check.Equal("One", Check.NotNull(continuousPasses[0].Resolve(4.999d)).CardName);
        Check.Equal("Two", Check.NotNull(continuousPasses[0].Resolve(5d)).CardName);
        Check.NearlyEqual(4d, DirectorWorkflowPlanner.SegmentLocalTime(
            continuousPasses[0].Segments[1],
            20d));
        Check.NearlyEqual(0d, DirectorWorkflowPlanner.SegmentLocalTime(
            continuousPasses[0].Segments[1],
            2d));

        Check.True(library.Remove(first.Id));
        DirectorWorkflow disabled = Check.NotNull(library.FindWorkflow(workflow.Id));
        Check.False(disabled.Cards.Single(card => card.Name == "Foundation A").Enabled);
        disabled.Cards.Clear();
        Check.Equal(3, Check.NotNull(library.FindWorkflow(workflow.Id)).Cards.Count);
    }

    private static void WorkflowCountdownCardsStayOutsideVideoPasses()
    {
        CameraLibrary library = new();
        CameraPreset camera = library.Add(new CameraPreset { Name = "Wide", Fov = 70f });
        DirectorWorkflow saved = library.AddWorkflow(new DirectorWorkflow
        {
            Name = "Countdown",
            Cards = new List<DirectorWorkflowCard>
            {
                new()
                {
                    Name = "Later countdown",
                    Kind = DirectorWorkflowCardKind.Countdown,
                    SourceType = DirectorRecordingSourceType.Follow,
                    SourceId = Guid.NewGuid(),
                    StartSeconds = 4d,
                    EndSeconds = 7d,
                    Lane = 1,
                    TransitionIn = new DirectorVideoTransition
                    {
                        Type = DirectorVideoTransitionType.FadeFromBlack,
                        DurationSeconds = 1d
                    }
                },
                new()
                {
                    Name = "Effective countdown",
                    Kind = DirectorWorkflowCardKind.Countdown,
                    StartSeconds = 0d,
                    EndSeconds = 5d,
                    Lane = 2
                },
                new()
                {
                    Name = "Opening",
                    SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = camera.Id,
                    StartSeconds = 10d,
                    EndSeconds = 20d
                }
            }
        });

        DirectorWorkflowCard[] countdowns = saved.Cards
            .Where(card => card.Kind == DirectorWorkflowCardKind.Countdown)
            .ToArray();
        Check.Equal(2, countdowns.Length);
        Check.True(countdowns.All(card => card.Enabled));
        Check.True(countdowns.All(card => card.SourceId == Guid.Empty));
        Check.True(countdowns.All(card => card.TransitionIn is null));
        Check.NearlyEqual(5d, DirectorWorkflowPlanner.RecordingCountdownSeconds(saved));

        IReadOnlyList<DirectorWorkflowExportPass> passes = DirectorWorkflowPlanner.Create(saved);
        Check.Equal(1, passes.Count);
        Check.NearlyEqual(10d, passes[0].StartSeconds);
        Check.NearlyEqual(20d, passes[0].EndSeconds);
        Check.Equal(1, passes[0].Segments.Count);
        Check.Equal("Opening", passes[0].Segments[0].CardName);

        DirectorWorkflow clone = saved.DeepClone();
        clone.Cards.Single(card => card.Name == "Effective countdown").EndSeconds = 9d;
        Check.NearlyEqual(5d, DirectorWorkflowPlanner.RecordingCountdownSeconds(saved));
        DirectorWorkflow reloaded = Check.NotNull(new CameraLibrary(library.Document).FindWorkflow(saved.Id));
        Check.Equal(DirectorWorkflowCardKind.Countdown,
            reloaded.Cards.Single(card => card.Name == "Effective countdown").Kind);

        DirectorWorkflow countdownOnly = new()
        {
            Cards = countdowns.Select(card => card.DeepClone()).ToList()
        };
        Check.Equal(0, DirectorWorkflowPlanner.Create(countdownOnly).Count);
    }

    private static void WorkflowTypesRestrictCardKinds()
    {
        Check.True(new DirectorReplayActorTrack { ForceGhost = true }.DeepClone().ForceGhost);
        Check.True(DirectorWorkflowPlanner.IsCardKindAllowed(
            DirectorWorkflowType.Recording,
            DirectorWorkflowCardKind.Shot));
        Check.True(DirectorWorkflowPlanner.IsCardKindAllowed(
            DirectorWorkflowType.Recording,
            DirectorWorkflowCardKind.Countdown));
        foreach (DirectorWorkflowCardKind kind in Enum.GetValues<DirectorWorkflowCardKind>())
        {
            Check.Equal(
                kind is DirectorWorkflowCardKind.Shot or DirectorWorkflowCardKind.Countdown,
                DirectorWorkflowPlanner.IsCardKindAllowed(DirectorWorkflowType.Action, kind));
        }
        foreach (DirectorWorkflowCardKind kind in new[]
                 {
                     DirectorWorkflowCardKind.Entity,
                     DirectorWorkflowCardKind.ActorPath,
                     DirectorWorkflowCardKind.Action,
                     DirectorWorkflowCardKind.Environment
                 })
        {
            Check.False(DirectorWorkflowPlanner.IsCardKindAllowed(DirectorWorkflowType.Recording, kind));
        }
    }

    private static void LiveWorkflowRunnerUsesRenderClock()
    {
        Guid cameraId = Guid.NewGuid();
        DirectorWorkflow workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "Live",
            Cards = new List<DirectorWorkflowCard>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Opening",
                    SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = cameraId,
                    StartSeconds = 0d,
                    EndSeconds = 2d
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Closing",
                    SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = cameraId,
                    StartSeconds = 2d,
                    EndSeconds = 4d
                }
            }
        };
        IReadOnlyList<DirectorWorkflowExportPass> passes = DirectorWorkflowPlanner.Create(workflow);
        long timestamp = 100L;
        List<double> appliedTimes = new();
        DirectorLiveWorkflowRunner runner = new(
            (track, time) =>
            {
                Check.Equal(DirectorRecordingSourceType.Workflow, track.SourceType);
                Check.Equal(2, track.WorkflowSegments.Count);
                appliedTimes.Add(time);
                return (true, string.Empty);
            },
            () => timestamp,
            timestampFrequency: 1000d);

        Check.True(runner.Prepare(workflow, passes, out string error), error);
        Check.True(runner.Start(workflow.Id, out error), error);
        timestamp = 900L;
        runner.PrepareFrame();
        Check.NearlyEqual(0d, appliedTimes[^1]);
        timestamp = 2900L;
        runner.PrepareFrame();
        Check.NearlyEqual(2d, appliedTimes[^1]);
        Check.False(runner.TryTakeCompletion());
        timestamp = 4900L;
        runner.PrepareFrame();
        Check.NearlyEqual(4d, appliedTimes[^1]);
        Check.True(runner.TryTakeCompletion());
        Check.False(runner.TryTakeCompletion());

        DirectorWorkflow overlapping = workflow.DeepClone();
        overlapping.Cards[1].StartSeconds = 1d;
        Check.False(runner.Prepare(
            overlapping,
            DirectorWorkflowPlanner.Create(overlapping),
            out error));
        Check.Equal("workflow-live-overlap", error);

        bool failFrame = false;
        DirectorLiveWorkflowRunner failing = new(
            (_, _) => failFrame ? (false, "camera-lost") : (true, string.Empty),
            () => timestamp,
            timestampFrequency: 1000d);
        Check.True(failing.Prepare(workflow, passes, out error), error);
        Check.True(failing.Start(workflow.Id, out error), error);
        failing.PrepareFrame();
        failFrame = true;
        timestamp += 100L;
        failing.PrepareFrame();
        Check.True(failing.TryTakeFailure(out error));
        Check.Equal("camera-lost", error);
        Check.False(failing.TryTakeCompletion());

        DirectorWorkflow shifted = workflow.DeepClone();
        foreach (DirectorWorkflowCard card in shifted.Cards)
        {
            card.StartSeconds += 20d;
            card.EndSeconds += 20d;
        }
        List<double> shiftedTimes = new();
        DirectorLiveWorkflowRunner shiftedRunner = new(
            (_, time) =>
            {
                shiftedTimes.Add(time);
                return (true, string.Empty);
            },
            () => timestamp,
            timestampFrequency: 1000d);
        Check.True(shiftedRunner.Prepare(
            shifted,
            DirectorWorkflowPlanner.Create(shifted),
            out error), error);
        Check.NearlyEqual(20d, shiftedTimes[^1]);
        Check.True(shiftedRunner.Start(shifted.Id, out error), error);
        shiftedRunner.PrepareFrame();
        timestamp += 4000L;
        shiftedRunner.PrepareFrame();
        Check.NearlyEqual(24d, shiftedTimes[^1]);
        Check.True(shiftedRunner.TryTakeCompletion());

        double replayTime = 20d;
        List<double> replayDrivenTimes = new();
        DirectorLiveWorkflowRunner replayDrivenRunner = new((_, time) =>
        {
            replayDrivenTimes.Add(time);
            return (true, string.Empty);
        });
        Check.True(replayDrivenRunner.Prepare(
            shifted,
            DirectorWorkflowPlanner.Create(shifted),
            out error), error);
        Check.True(replayDrivenRunner.Start(shifted.Id, () => replayTime, out error), error);
        replayTime = 22.5d;
        replayDrivenRunner.PrepareFrame();
        Check.NearlyEqual(22.5d, replayDrivenTimes[^1]);
        replayTime = 24d;
        replayDrivenRunner.PrepareFrame();
        Check.True(replayDrivenRunner.TryTakeCompletion());
    }

    private static void WorkflowMultiViewUsesOneWallClock()
    {
        Guid staticId = Guid.NewGuid();
        Guid sequenceFirstId = Guid.NewGuid();
        Guid sequenceSecondId = Guid.NewGuid();
        Guid pathId = Guid.NewGuid();
        Guid sequenceId = Guid.NewGuid();
        Guid followId = Guid.NewGuid();
        Dictionary<Guid, CameraPreset> cameras = new()
        {
            [staticId] = new CameraPreset
            {
                Id = staticId, Name = "Static", X = 10d, Dimension = 0, Fov = 70f
            },
            [sequenceFirstId] = new CameraPreset
            {
                Id = sequenceFirstId, Name = "Sequence A", X = 20d, Dimension = 0, Fov = 75f
            },
            [sequenceSecondId] = new CameraPreset
            {
                Id = sequenceSecondId, Name = "Sequence B", X = 30d, Dimension = 1, Fov = 80f
            }
        };
        CameraPath path = new()
        {
            Id = pathId,
            Name = "Dimension cut",
            Interpolation = CameraPathInterpolation.Linear,
            LoopMode = CameraPathLoopMode.Once,
            PlaybackSpeed = 1d,
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, X = 100d, Dimension = 0, Fov = 70f },
                new() { TimeSeconds = 2d, X = 200d, Dimension = 2, Fov = 90f }
            }
        };
        CameraSequence sequence = new()
        {
            Id = sequenceId,
            Name = "Cuts",
            LoopMode = CameraSequenceLoopMode.Loop,
            Shots = new List<CameraSequenceShot>
            {
                new() { CameraId = sequenceFirstId, DurationSeconds = 1d },
                new() { CameraId = sequenceSecondId, DurationSeconds = 1d }
            }
        };
        CameraFollowRig follow = new() { Id = followId, Name = "Actor follow", Fov = 65f };
        DirectorWorkflow workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "Four cameras",
            Cards = new List<DirectorWorkflowCard>
            {
                WorkflowCard("Static", DirectorRecordingSourceType.StaticCamera, staticId),
                WorkflowCard("Path", DirectorRecordingSourceType.Path, pathId),
                WorkflowCard("Sequence", DirectorRecordingSourceType.Sequence, sequenceId),
                WorkflowCard("Follow", DirectorRecordingSourceType.Follow, followId)
            }
        };
        IReadOnlyList<DirectorWorkflowExportPass> passes = DirectorWorkflowPlanner.Create(workflow);
        Check.Equal(4, passes.Count);
        Check.True(passes.All(pass => pass.SplitForOverlap));

        long timestamp = 500L;
        int clockReads = 0;
        List<DirectorWorkflowFollowPoseContext> followSamples = new();
        DirectorWorkflowMultiViewScheduler scheduler = new(
            id => cameras.TryGetValue(id, out CameraPreset? camera) ? camera : null,
            id => id == pathId ? path : null,
            id => id == sequenceId ? sequence : null,
            id => id == followId ? follow : null,
            context =>
            {
                followSamples.Add(context);
                return new DirectorWorkflowCameraPose(
                    400d + context.SourceTimeSeconds,
                    50d,
                    60d,
                    3,
                    10f,
                    20f,
                    0f,
                    context.Rig.Fov);
            },
            () =>
            {
                clockReads++;
                return timestamp;
            },
            timestampFrequency: 1000d);
        Check.True(scheduler.Prepare(
            workflow,
            passes,
            new DirectorWorkflowMultiViewSettings
            {
                Width = 1280,
                Height = 720,
                RenderLocalPlayer = false
            },
            out IReadOnlyList<DirectorWorkflowMultiViewError> prepareErrors));
        Check.Empty(prepareErrors);
        Check.Equal(4, scheduler.Passes.Count);
        Check.True(scheduler.Passes.All(pass => pass.StartSeconds == 0d && pass.EndSeconds == 4d));
        Check.True(scheduler.Start(out DirectorWorkflowMultiViewError startError), startError.Detail);

        DirectorWorkflowMultiViewFrame first = scheduler.EvaluateFrame();
        Check.Equal(1, clockReads);
        Check.NearlyEqual(0d, first.TimelineSeconds);
        Check.Equal(4, first.Views.Count);
        Check.Empty(first.Errors);
        Check.True(first.Views.All(view => view.Width == 1280 && view.Height == 720));
        Dictionary<DirectorRecordingSourceType, Guid> stableIds = first.Views
            .ToDictionary(view => view.SourceType, view => view.ViewId);

        timestamp = 1500L;
        DirectorWorkflowMultiViewFrame second = scheduler.EvaluateFrame();
        Check.Equal(2, clockReads);
        Check.NearlyEqual(1d, second.TimelineSeconds);
        Check.Equal(4, second.Views.Count);
        Check.True(second.Views.All(view => stableIds[view.SourceType] == view.ViewId));
        Check.NearlyEqual(10d, second.Views.Single(
            view => view.SourceType == DirectorRecordingSourceType.StaticCamera).Pose.X);
        Check.Equal(0, second.Views.Single(
            view => view.SourceType == DirectorRecordingSourceType.Path).Pose.Dimension);
        Check.NearlyEqual(100d, second.Views.Single(
            view => view.SourceType == DirectorRecordingSourceType.Path).Pose.X);
        Check.Equal(1, second.Views.Single(
            view => view.SourceType == DirectorRecordingSourceType.Sequence).Pose.Dimension);
        Check.NearlyEqual(401d, second.Views.Single(
            view => view.SourceType == DirectorRecordingSourceType.Follow).Pose.X);
        Check.NearlyEqual(1d, followSamples[^1].TimelineSeconds);
        Check.NearlyEqual(1d, followSamples[^1].SourceTimeSeconds);

        timestamp = 1000L;
        DirectorWorkflowMultiViewFrame nonRewinding = scheduler.EvaluateFrame();
        Check.Equal(3, clockReads);
        Check.NearlyEqual(1d, nonRewinding.TimelineSeconds);

        DirectorWorkflowMultiViewFrame dimensionCut = scheduler.EvaluateAt(2d);
        Check.Equal(2, dimensionCut.Views.Single(
            view => view.SourceType == DirectorRecordingSourceType.Path).Pose.Dimension);
        Check.NearlyEqual(200d, dimensionCut.Views.Single(
            view => view.SourceType == DirectorRecordingSourceType.Path).Pose.X);

        timestamp = 4500L;
        DirectorWorkflowMultiViewFrame completed = scheduler.EvaluateFrame();
        Check.Equal(4, clockReads);
        Check.True(completed.Completed);
        Check.NearlyEqual(4d, completed.TimelineSeconds);
        Check.Equal(4, completed.Views.Count);
        Check.False(scheduler.IsRunning);
        DirectorWorkflowMultiViewFrame stopped = scheduler.EvaluateFrame();
        Check.Equal(4, clockReads);
        Check.Equal(DirectorWorkflowMultiViewErrorCode.NotRunning, stopped.Errors.Single().Code);

        static DirectorWorkflowCard WorkflowCard(
            string name,
            DirectorRecordingSourceType sourceType,
            Guid sourceId)
            => new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                SourceType = sourceType,
                SourceId = sourceId,
                StartSeconds = 0d,
                EndSeconds = 4d
            };
    }

    private static void WorkflowMultiViewOffscreenRequestsShareCaptureClock()
    {
        Guid firstViewId = Guid.NewGuid();
        Guid secondViewId = Guid.NewGuid();
        DirectorWorkflowMultiViewFrame frame = new()
        {
            TimelineSeconds = 12.3456d,
            Views = new[]
            {
                new DirectorWorkflowViewPlan
                {
                    ViewId = firstViewId,
                    PassName = "Wide",
                    Width = 3840,
                    Height = 2160,
                    Pose = new DirectorWorkflowCameraPose(
                        10.25d,
                        20.5d,
                        -30.75d,
                        2,
                        45.5f,
                        -12.25f,
                        3.75f,
                        68.5f),
                    RenderTerrain = true,
                    RenderEntities = false,
                    RenderLocalPlayer = true,
                    RenderOpaqueParticles = false
                },
                new DirectorWorkflowViewPlan
                {
                    ViewId = secondViewId,
                    PassName = "Close",
                    Width = 1920,
                    Height = 1080,
                    Pose = new DirectorWorkflowCameraPose(
                        -100.125d,
                        80.875d,
                        200.625d,
                        -1,
                        -170.25f,
                        32.5f,
                        -8.125f,
                        92.25f),
                    RenderTerrain = false,
                    RenderEntities = true,
                    RenderLocalPlayer = false,
                    RenderOpaqueParticles = true
                }
            }
        };

        IReadOnlyList<DirectorOffscreenViewRequest> requests = frame.ToOffscreenViewRequests();

        Check.Equal(2, requests.Count);
        long captureMilliseconds = (long)Math.Round(frame.TimelineSeconds * 1000d);
        Check.True(requests.All(request => request.CaptureClockMilliseconds == captureMilliseconds));
        AssertMapped(frame.Views[0], requests.Single(request => request.ViewId == firstViewId));
        AssertMapped(frame.Views[1], requests.Single(request => request.ViewId == secondViewId));

        static void AssertMapped(DirectorWorkflowViewPlan view, DirectorOffscreenViewRequest request)
        {
            Check.Equal(view.ViewId, request.ViewId);
            Check.Equal(view.Pose.X, request.Pose.X);
            Check.Equal(view.Pose.Y, request.Pose.Y);
            Check.Equal(view.Pose.Z, request.Pose.Z);
            Check.Equal(view.Pose.Dimension, request.Pose.Dimension);
            Check.Equal(view.Pose.Yaw, request.Pose.Yaw);
            Check.Equal(view.Pose.Pitch, request.Pose.Pitch);
            Check.Equal(view.Pose.Roll, request.Pose.Roll);
            Check.Equal(view.Pose.Fov, request.Pose.Fov);
        }
    }

    private static void WorkflowMultiViewHoldsGapsAndReportsErrors()
    {
        Guid cameraId = Guid.NewGuid();
        Guid pathId = Guid.NewGuid();
        Guid missingPathId = Guid.NewGuid();
        CameraPreset camera = new()
        {
            Id = cameraId,
            Name = "Hold",
            X = 11d,
            Dimension = 0,
            Fov = 70f
        };
        CameraPath path = new()
        {
            Id = pathId,
            Name = "Hard cut",
            Interpolation = CameraPathInterpolation.Bezier,
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, X = 20d, Dimension = 0, Fov = 70f },
                new() { TimeSeconds = 1d, X = 40d, Dimension = 3, Fov = 70f }
            }
        };
        DirectorWorkflow workflow = new() { Id = Guid.NewGuid(), Name = "Gaps" };
        DirectorWorkflowExportPass continuous = new()
        {
            Id = Guid.NewGuid(),
            Name = "Continuous",
            StartSeconds = 0d,
            EndSeconds = 5d,
            Segments = new List<DirectorWorkflowExportSegment>
            {
                new()
                {
                    CardId = Guid.NewGuid(), CardName = "Opening",
                    SourceType = DirectorRecordingSourceType.StaticCamera, SourceId = cameraId,
                    StartSeconds = 0d, EndSeconds = 1d
                },
                new()
                {
                    CardId = Guid.NewGuid(), CardName = "Portal",
                    SourceType = DirectorRecordingSourceType.Path, SourceId = pathId,
                    StartSeconds = 3d, EndSeconds = 5d
                }
            }
        };
        DirectorWorkflowExportPass missing = new()
        {
            Id = Guid.NewGuid(),
            Name = "Missing path",
            StartSeconds = 1d,
            EndSeconds = 4d,
            SplitForOverlap = true,
            Segments = new List<DirectorWorkflowExportSegment>
            {
                new()
                {
                    CardId = Guid.NewGuid(), CardName = "Unavailable",
                    SourceType = DirectorRecordingSourceType.Path, SourceId = missingPathId,
                    StartSeconds = 1d, EndSeconds = 4d
                }
            }
        };

        DirectorWorkflowMultiViewScheduler scheduler = new(
            id => id == cameraId ? camera : null,
            id => id == pathId ? path : null,
            _ => null,
            _ => null,
            _ => null);
        Check.True(scheduler.Prepare(
            workflow,
            new[] { continuous, missing },
            null,
            out IReadOnlyList<DirectorWorkflowMultiViewError> errors));
        Check.Empty(errors);

        DirectorWorkflowMultiViewFrame gap = scheduler.EvaluateAt(2d);
        Check.Equal(1, gap.Views.Count);
        Check.Equal("Opening", gap.Views[0].CardName);
        Check.NearlyEqual(1d, gap.Views[0].SourceTimeSeconds);
        Check.NearlyEqual(11d, gap.Views[0].Pose.X);
        DirectorWorkflowMultiViewError missingError = gap.Errors.Single();
        Check.Equal(DirectorWorkflowMultiViewErrorCode.MissingPath, missingError.Code);
        Check.Equal(missing.Id, missingError.PassId);
        Check.Equal(missingPathId, missingError.SourceId);

        DirectorWorkflowMultiViewFrame beforeCut = scheduler.EvaluateAt(3.999d);
        DirectorWorkflowViewPlan beforeCutPath = beforeCut.Views.Single();
        Check.Equal("Portal", beforeCutPath.CardName);
        Check.Equal(0, beforeCutPath.Pose.Dimension);
        Check.NearlyEqual(20d, beforeCutPath.Pose.X);
        DirectorWorkflowMultiViewFrame atCut = scheduler.EvaluateAt(4d);
        DirectorWorkflowViewPlan atCutPath = atCut.Views.Single();
        Check.Equal(3, atCutPath.Pose.Dimension);
        Check.NearlyEqual(40d, atCutPath.Pose.X);

        DirectorWorkflowMultiViewScheduler duplicate = new(
            _ => null,
            _ => null,
            _ => null,
            _ => null,
            _ => null);
        Check.False(duplicate.Prepare(
            workflow,
            new[] { continuous, continuous.DeepClone() },
            null,
            out errors));
        Check.Equal(DirectorWorkflowMultiViewErrorCode.DuplicateViewId, errors.Single().Code);
        Check.False(duplicate.IsPrepared);
    }

    private static void RecordingTracksNormalizeAndValidateSources()
    {
        Check.Equal(5, (int)DirectorRecordingSourceType.Workflow);
        Guid cameraId = Guid.NewGuid();
        Guid pathId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid duplicateTrackId = Guid.NewGuid();
        CameraLibrary library = new(new CameraLibraryDocument
        {
            SchemaVersion = 5,
            Cameras = new List<CameraPreset> { new() { Id = cameraId, Name = "Wide", Fov = 70f } },
            Paths = new List<CameraPath> { new() { Id = pathId, Name = "Flyby" } },
            Workflows = new List<DirectorWorkflow> { new() { Id = workflowId, Name = "Live shoot" } },
            RecordingTracks = new List<DirectorRecordingTrack>
            {
                new()
                {
                    Id = duplicateTrackId,
                    Name = " Program ",
                    SourceType = DirectorRecordingSourceType.Program,
                    SourceId = Guid.NewGuid(),
                    Enabled = true
                },
                new()
                {
                    Id = duplicateTrackId,
                    Name = "program",
                    SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = cameraId,
                    Enabled = true
                },
                new()
                {
                    Name = "Missing",
                    SourceType = DirectorRecordingSourceType.Path,
                    SourceId = Guid.NewGuid(),
                    Enabled = true
                },
                new()
                {
                    Name = "Workflow master",
                    SourceType = DirectorRecordingSourceType.Workflow,
                    SourceId = workflowId,
                    Enabled = true
                },
                new()
                {
                    Name = "Missing workflow",
                    SourceType = DirectorRecordingSourceType.Workflow,
                    SourceId = Guid.NewGuid(),
                    Enabled = true
                }
            }
        });

        CameraLibraryDocument document = library.Document;
        Check.Equal(CameraLibraryDocument.CurrentSchemaVersion, document.SchemaVersion);
        Check.Equal(5, document.RecordingTracks.Count);
        Check.Equal(5, document.RecordingTracks.Select(track => track.Id).Distinct().Count());
        DirectorRecordingTrack program = Check.NotNull(library.FindRecordingTrack("Program"));
        Check.Equal(Guid.Empty, program.SourceId);
        Check.True(program.Enabled);
        DirectorRecordingTrack cameraTrack = Check.NotNull(library.FindRecordingTrack("program (2)"));
        Check.Equal(DirectorRecordingSourceType.StaticCamera, cameraTrack.SourceType);
        Check.Equal(cameraId, cameraTrack.SourceId);
        Check.True(cameraTrack.Enabled);
        Check.False(Check.NotNull(library.FindRecordingTrack("Missing")).Enabled);
        DirectorRecordingTrack workflowTrack = Check.NotNull(library.FindRecordingTrack("Workflow master"));
        Check.Equal(DirectorRecordingSourceType.Workflow, workflowTrack.SourceType);
        Check.Equal(workflowId, workflowTrack.SourceId);
        Check.True(workflowTrack.Enabled);
        Check.False(Check.NotNull(library.FindRecordingTrack("Missing workflow")).Enabled);
        Check.True(DirectorRecordingTrack.RequiresSource(DirectorRecordingSourceType.Workflow));

        workflowTrack.Enabled = true;
        Check.True(library.UpdateRecordingTrack(workflowTrack));
        Check.True(Check.NotNull(library.FindRecordingTrack(workflowTrack.Id)).Enabled);

        DirectorRecordingTrack added = library.AddRecordingTrack(new DirectorRecordingTrack
        {
            Name = "Path master",
            SourceType = DirectorRecordingSourceType.Path,
            SourceId = pathId,
            Enabled = true
        });
        Check.True(added.Enabled);
        DirectorRecordingTrack detached = Check.NotNull(library.FindRecordingTrack(added.Id));
        detached.Name = "outside";
        Check.Equal("Path master", Check.NotNull(library.FindRecordingTrack(added.Id)).Name);
        Check.True(library.RemovePath(pathId));
        Check.False(Check.NotNull(library.FindRecordingTrack(added.Id)).Enabled);
        Check.True(library.RemoveWorkflow(workflowId));
        Check.False(Check.NotNull(library.FindRecordingTrack(workflowTrack.Id)).Enabled);
        Check.True(library.RemoveRecordingTrack(added.Id));
        Check.False(library.RemoveRecordingTrack(added.Id));
    }

    private static void RecordingTimecodeAndFfmpegArgumentsWork()
    {
        DateTimeOffset utc = new(2026, 7, 14, 1, 2, 3, 500, TimeSpan.Zero);
        DirectorTimecode timecode = DirectorTimecode.FromUtc(utc, 30);
        Check.Equal("01:02:03:15", timecode.ToString());
        Check.Equal("01:02:05:00", timecode.AtElapsed(1.5d).ToString());
        Check.Equal(240, DirectorTimecode.NormalizeFrameRate(999));

        IReadOnlyList<string> arguments = DirectorFfmpegArguments.BuildMp4(
            @"C:\Video Source\take 01.mp4",
            @"C:\Video Output\take 01.mp4",
            30,
            timecode);
        Check.True(arguments.Contains(@"C:\Video Source\take 01.mp4"));
        Check.True(arguments.Contains(@"C:\Video Output\take 01.mp4"));
        Check.True(arguments.Contains("-an"));
        int timecodeIndex = arguments.ToList().IndexOf("-timecode");
        Check.True(timecodeIndex >= 0);
        Check.Equal("01:02:03:15", arguments[timecodeIndex + 1]);

        IReadOnlyList<string> audioArguments = DirectorFfmpegArguments.BuildMp4(
            @"C:\Video Source\take 01.mp4",
            @"C:\Video Output\take 01.mp4",
            30,
            timecode,
            @"C:\Audio Source\take 01.audio.mka",
            videoCrf: 20,
            videoPreset: "slow",
            audioBitrateKbps: 256);
        Check.True(audioArguments.Contains(@"C:\Audio Source\take 01.audio.mka"));
        Check.False(audioArguments.Contains("-an"));
        Check.True(audioArguments.Contains("-c:a"));
        Check.True(audioArguments.Contains("256k"));
        Check.True(audioArguments.Contains("slow"));
        Check.True(audioArguments.Contains("20"));

        IReadOnlyList<string> remuxArguments = DirectorFfmpegArguments.BuildMp4Remux(
            "video.mp4", "output.mp4", timecode, "audio.mka", 256);
        int videoCodecIndex = remuxArguments.ToList().IndexOf("-c:v");
        Check.Equal("copy", remuxArguments[videoCodecIndex + 1]);
        Check.True(remuxArguments.Contains("audio.mka"));
        Check.True(remuxArguments.Contains("256k"));
        Check.True(remuxArguments.Contains("-shortest"));
    }

    private static void FfmpegLocatorReportsMissingExecutable()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "vsdirector-ffmpeg-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string managedDirectory = Path.Combine(root, "managed");
            DirectorFfmpegLocator.ConfigureManagedDirectory(managedDirectory);
            Directory.CreateDirectory(managedDirectory);
            string managedExecutable = Path.Combine(
                managedDirectory,
                OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            Check.Equal(Path.GetFullPath(managedDirectory), DirectorFfmpegLocator.ManagedDirectory);
            Check.Equal(
                Path.GetFullPath(managedExecutable),
                DirectorFfmpegLocator.PreferredConfiguredPath("ffmpeg"));
            Check.Equal("8.0.x", DirectorFfmpegLocator.RecommendedVersion);
            Check.True(DirectorFfmpegLocator.OfficialDownloadUrl.StartsWith("https://", StringComparison.Ordinal));
            Check.True(DirectorFfmpegLocator.CurrentDownload.DownloadPageUrl.StartsWith(
                "https://",
                StringComparison.Ordinal));
            Check.True(DirectorFfmpegLocator.CurrentDownload.DirectArchiveUrl.StartsWith(
                "https://",
                StringComparison.Ordinal));

            string missing = Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            Check.Throws<FileNotFoundException>(() => DirectorFfmpegLocator.ResolveRequired(missing));
            DirectorFfmpegInspection inspection = DirectorFfmpegLocator.Inspect(missing);
            Check.False(inspection.Available);
            Check.True(inspection.Error.Contains("was not found", StringComparison.OrdinalIgnoreCase));

            File.WriteAllBytes(missing, new byte[] { 0 });
            Check.Equal(Path.GetFullPath(missing), DirectorFfmpegLocator.ResolveRequired(missing));

            File.WriteAllBytes(managedExecutable, new byte[] { 0 });
            Check.Equal(
                Path.GetFullPath(managedExecutable),
                DirectorFfmpegLocator.ResolveRequired("ffmpeg"));
        }
        finally
        {
            DirectorFfmpegLocator.ConfigureManagedDirectory(null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task FfmpegInputsWaitForWriterRelease()
    {
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-ffmpeg-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string readyPath = Path.Combine(root, "ready.mp4");
            string lockedPath = Path.Combine(root, "locked.mp4");
            await File.WriteAllBytesAsync(readyPath, new byte[] { 1, 2, 3 });
            await File.WriteAllBytesAsync(lockedPath, new byte[] { 4, 5, 6 });

            FileStream writer = new(
                lockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            try
            {
                Task<DirectorFfmpegInputReadinessResult> pending =
                    DirectorFfmpegInputReadiness.WaitAsync(new[] { readyPath, lockedPath });
                await Task.Delay(50);
                writer.Dispose();
                DirectorFfmpegInputReadinessResult released = await pending;
                Check.True(released.Ready, released.Error);
            }
            finally
            {
                writer.Dispose();
            }

            int transientAttempts = 0;
            int stableAttempts = 0;
            DirectorFfmpegInputReadinessResult retried = await DirectorFfmpegInputReadiness.WaitAsync(
                new[] { lockedPath, readyPath, lockedPath },
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                path =>
                {
                    if (string.Equals(path, lockedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (++transientAttempts < 3)
                        {
                            throw new IOException("sharing violation");
                        }
                        return;
                    }

                    stableAttempts++;
                });
            Check.True(retried.Ready, retried.Error);
            Check.Equal(3, transientAttempts);
            Check.Equal(1, stableAttempts);

            DirectorFfmpegInputReadinessResult blocked = await DirectorFfmpegInputReadiness.WaitAsync(
                new[] { lockedPath },
                TimeSpan.FromMilliseconds(15),
                TimeSpan.FromMilliseconds(1),
                _ => throw new UnauthorizedAccessException("Permission denied"));
            Check.False(blocked.Ready);
            Check.True(blocked.Error.Contains("source files were retained", StringComparison.Ordinal));
            Check.True(blocked.Error.Contains("Permission denied", StringComparison.Ordinal));
            Check.True(blocked.Error.Contains(Path.GetFullPath(lockedPath), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RecordingCompletionCallbacksIsolateFailures()
    {
        InvalidOperationException successException = new("success callback failed");
        IOException failureException = new("failure callback failed");
        Exception? observedFailure = null;
        List<Exception> logged = new();

        DirectorRecordingCallbackGuard.InvokeSuccess(
            () => throw successException,
            exception =>
            {
                observedFailure = exception;
                throw failureException;
            },
            logged.Add);

        Check.True(ReferenceEquals(successException, observedFailure));
        Check.Equal(2, logged.Count);
        Check.True(ReferenceEquals(successException, logged[0]));
        Check.True(ReferenceEquals(failureException, logged[1]));

        bool failureCalled = false;
        DirectorRecordingCallbackGuard.InvokeSuccess(
            () => throw successException,
            _ => failureCalled = true,
            _ => throw new InvalidOperationException("logger failed"));
        Check.True(failureCalled);

        logged.Clear();
        DirectorRecordingCallbackGuard.InvokeFailure(
            _ => throw failureException,
            successException,
            logged.Add);
        Check.Equal(1, logged.Count);
        Check.True(ReferenceEquals(failureException, logged[0]));
    }

    private static void OffscreenResourceBudgetIncludesAllTargets()
    {
        long expected = 8L * 1920L * 1080L * (4L + 4L + 4L * 4L);
        Check.Equal(expected, DirectorOffscreenResourceBudget.EstimateGpuBytes(8, 1920, 1080, 4));
        Check.True(DirectorOffscreenResourceBudget.FitsDefaultBudget(
            8, 3840, 2160, 4, out long fourKBytes));
        Check.Equal(8L * 3840L * 2160L * 24L, fourKBytes);
        Check.False(DirectorOffscreenResourceBudget.FitsDefaultBudget(
            2, 8192, 8192, 4, out long eightKBytes));
        Check.True(eightKBytes > DirectorOffscreenResourceBudget.MaximumEstimatedGpuBytes);
        Check.Throws<ArgumentOutOfRangeException>(() =>
            DirectorOffscreenResourceBudget.EstimateGpuBytes(-1, 1920, 1080, 4));
    }

    private static void MultiTrackTimelineProducesFfmpegPlan()
    {
        Guid firstCardId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid secondCardId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        DirectorWorkflow workflow = new()
        {
            Name = "Program edit",
            Cards = new List<DirectorWorkflowCard>
            {
                new()
                {
                    Id = firstCardId,
                    Name = "Wide",
                    SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = Guid.NewGuid(),
                    StartSeconds = 0d,
                    EndSeconds = 4d,
                    Lane = 1
                },
                new()
                {
                    Id = secondCardId,
                    Name = "Close",
                    SourceType = DirectorRecordingSourceType.Path,
                    SourceId = Guid.NewGuid(),
                    StartSeconds = 3d,
                    EndSeconds = 7d,
                    Lane = 0
                }
            }
        };
        DirectorTimelineAudioClip dialogue = new()
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Name = "Dialogue",
            InputPath = @"C:\Audio\dialogue.wav",
            Track = 1,
            TimelineStartSeconds = 1.25d,
            SourceStartSeconds = 2d,
            DurationSeconds = 4d,
            GainDecibels = -3d,
            FadeInSeconds = 0.25d,
            FadeOutSeconds = 0.5d
        };
        DirectorTimelineAudioClip ambience = new()
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Name = "Ambience",
            InputPath = @"C:\Audio\ambience.flac",
            Track = 0,
            DurationSeconds = 7d,
            GainDecibels = -9d
        };

        DirectorTimelineProject project = DirectorTimelineFfmpegPlanner.FromWorkflow(
            workflow,
            card => card.Id == firstCardId ? @"C:\Video\wide.mp4" : @"C:\Video\close.mp4",
            new[] { dialogue, ambience },
            defaultCrossDissolveSeconds: 0.75d,
            startTimecode: new DirectorTimecode(30L * 60L * 60L, 30),
            resolveTransition: card => card.Id switch
            {
                _ when card.Id == firstCardId => new DirectorVideoTransition
                {
                    Type = DirectorVideoTransitionType.FadeFromBlack,
                    DurationSeconds = 0.4d
                },
                _ when card.Id == secondCardId => new DirectorVideoTransition
                {
                    Type = DirectorVideoTransitionType.CrossDissolve,
                    DurationSeconds = 1d
                },
                _ => null
            },
            resolveSourceStart: card => card.Id == secondCardId ? 1.5d : 0d);

        DirectorTimelineExportPlan plan = DirectorTimelineFfmpegPlanner.Build(
            project,
            @"C:\Output\program.mp4",
            new DirectorTimelineExportSettings
            {
                Width = 1920,
                Height = 1080,
                FramesPerSecond = 60,
                AudioSampleRate = 48000,
                VideoCrf = 20,
                VideoPreset = "slow",
                AudioBitrateKbps = 256
            });

        Check.NearlyEqual(7d, plan.DurationSeconds);
        Check.Equal("01:00:00:00", plan.StartTimecode.ToString());
        Check.Equal(4, plan.Inputs.Count);
        DirectorTimelineInputPlan closeInput = plan.Inputs.Single(input => input.ClipId == secondCardId);
        Check.Equal(0, closeInput.Track);
        Check.NearlyEqual(1.5d, closeInput.SourceStartSeconds);
        Check.Equal("01:00:03:00", closeInput.TimelineTimecode.ToString());
        Check.True(plan.FilterGraph.Contains("fade=t=in:st=0:d=0.4", StringComparison.Ordinal));
        Check.True(plan.FilterGraph.Contains("fade=t=in:st=0:d=1:alpha=1", StringComparison.Ordinal));
        Check.True(plan.FilterGraph.Contains(
            "overlay=eof_action=repeat:repeatlast=1:shortest=0",
            StringComparison.Ordinal));
        Check.True(plan.FilterGraph.Contains("adelay=60000S:all=1", StringComparison.Ordinal));
        Check.True(plan.FilterGraph.Contains("volume=-3dB", StringComparison.Ordinal));
        Check.True(plan.FilterGraph.Contains("afade=t=out:st=3.5:d=0.5", StringComparison.Ordinal));
        Check.True(plan.FilterGraph.Contains("amix=inputs=2", StringComparison.Ordinal));
        Check.True(plan.Arguments.Contains("[vout]"));
        Check.True(plan.Arguments.Contains("[aout]"));
        Check.True(plan.Arguments.Contains("256k"));
        Check.True(plan.Arguments.Contains("slow"));
        Check.Equal(@"C:\Output\program.mp4", plan.Arguments[^1]);

        DirectorTimelineExportPlan repeat = DirectorTimelineFfmpegPlanner.Build(
            project,
            @"C:\Output\program.mp4",
            new DirectorTimelineExportSettings { FramesPerSecond = 60 });
        Check.Equal(plan.FilterGraph, repeat.FilterGraph);
        DirectorWorkflow invalidTransition = workflow.DeepClone();
        invalidTransition.Cards.Single(card => card.Id == secondCardId).TransitionIn =
            new DirectorVideoTransition
            {
                Type = DirectorVideoTransitionType.CrossDissolve,
                DurationSeconds = 2d
            };
        DirectorWorkflowTransitionIssue issue = Check.NotNull(
            DirectorTimelineFfmpegPlanner.FindUnsupportedCrossDissolve(invalidTransition));
        Check.NearlyEqual(2d, issue.RequestedDurationSeconds);
        Check.NearlyEqual(1d, issue.AvailableOverlapSeconds);
        Check.Throws<InvalidOperationException>(() => DirectorTimelineFfmpegPlanner.FromWorkflow(
            workflow,
            card => card.Id == firstCardId ? @"C:\Video\wide.mp4" : @"C:\Video\close.mp4",
            resolveTransition: card => card.Id == secondCardId
                ? new DirectorVideoTransition
                {
                    Type = DirectorVideoTransitionType.CrossDissolve,
                    DurationSeconds = 2d
                }
                : null));
        Check.Throws<InvalidOperationException>(() => DirectorTimelineFfmpegPlanner.FromWorkflow(
            workflow,
            _ => null));
    }

    private static void RenderedWorkflowMaterialsPreserveScaledTiming()
    {
        Guid firstCardId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid secondCardId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        DirectorWorkflow workflow = new()
        {
            Name = "Scaled program",
            Cards = new List<DirectorWorkflowCard>
            {
                new()
                {
                    Id = firstCardId,
                    Name = "First",
                    SourceType = DirectorRecordingSourceType.StaticCamera,
                    SourceId = Guid.NewGuid(),
                    StartSeconds = 2d,
                    EndSeconds = 5d,
                    TransitionIn = new DirectorVideoTransition
                    {
                        Type = DirectorVideoTransitionType.FadeFromBlack,
                        DurationSeconds = 1d
                    }
                },
                new()
                {
                    Id = secondCardId,
                    Name = "Second",
                    SourceType = DirectorRecordingSourceType.Path,
                    SourceId = Guid.NewGuid(),
                    StartSeconds = 7d,
                    EndSeconds = 11d,
                    TransitionIn = new DirectorVideoTransition
                    {
                        Type = DirectorVideoTransitionType.Cut
                    }
                }
            },
            AudioClips = new List<DirectorTimelineAudioClip>
            {
                new()
                {
                    Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    Name = "Production mix",
                    InputPath = @"C:\Audio\mix.wav",
                    TimelineStartSeconds = 2.5d,
                    SourceStartSeconds = 7d,
                    DurationSeconds = 4d,
                    FadeInSeconds = 0.5d,
                    FadeOutSeconds = 1d
                }
            }
        };

        DirectorTimelineProject project = DirectorTimelineFfmpegPlanner.FromRenderedWorkflow(
            workflow,
            card => new DirectorRenderedWorkflowSource(@"C:\Video\continuous.mp4", 2d),
            timeScale: 2d,
            startTimecode: new DirectorTimecode(0L, 30));
        DirectorTimelineVideoClip first = project.VideoClips.Single(clip => clip.Id == firstCardId);
        DirectorTimelineVideoClip second = project.VideoClips.Single(clip => clip.Id == secondCardId);
        Check.NearlyEqual(1d, first.TimelineStartSeconds);
        Check.NearlyEqual(1.5d, first.DurationSeconds);
        Check.NearlyEqual(0d, first.SourceStartSeconds);
        Check.NearlyEqual(0.5d, first.TransitionIn.DurationSeconds);
        Check.NearlyEqual(3.5d, second.TimelineStartSeconds);
        Check.NearlyEqual(2d, second.DurationSeconds);
        Check.NearlyEqual(2.5d, second.SourceStartSeconds);
        Check.Equal(DirectorVideoTransitionType.Cut, second.TransitionIn.Type);
        Check.NearlyEqual(0d, second.TransitionIn.DurationSeconds);
        Check.NearlyEqual(5.5d, project.DurationSeconds);

        DirectorTimelineAudioClip audio = project.AudioClips.Single();
        Check.NearlyEqual(1.25d, audio.TimelineStartSeconds);
        Check.NearlyEqual(7d, audio.SourceStartSeconds);
        Check.NearlyEqual(2d, audio.DurationSeconds);
        Check.NearlyEqual(0.25d, audio.FadeInSeconds);
        Check.NearlyEqual(0.5d, audio.FadeOutSeconds);
        Check.NearlyEqual(2d, audio.PlaybackRate);

        DirectorTimelineExportPlan plan = DirectorTimelineFfmpegPlanner.Build(
            project,
            @"C:\Output\scaled.mp4",
            new DirectorTimelineExportSettings { FramesPerSecond = 30 });
        Check.NearlyEqual(5.5d, plan.DurationSeconds);
        Check.True(plan.FilterGraph.Contains("trim=start=2.5:duration=2", StringComparison.Ordinal));
        Check.True(plan.FilterGraph.Contains("atrim=start=7:duration=4", StringComparison.Ordinal));
        Check.True(plan.FilterGraph.Contains("atempo=2", StringComparison.Ordinal));
        Check.True(plan.FilterGraph.Contains("adelay=60000S:all=1", StringComparison.Ordinal));
        DirectorWorkflow unsupported = workflow.DeepClone();
        unsupported.Cards.Single(card => card.Id == secondCardId).TransitionIn =
            new DirectorVideoTransition
            {
                Type = DirectorVideoTransitionType.CrossDissolve,
                DurationSeconds = 1d
            };
        DirectorWorkflowTransitionIssue unsupportedIssue = Check.NotNull(
            DirectorTimelineFfmpegPlanner.FindUnsupportedCrossDissolve(unsupported));
        Check.NearlyEqual(0d, unsupportedIssue.AvailableOverlapSeconds);
        Check.Throws<InvalidOperationException>(() =>
            DirectorTimelineFfmpegPlanner.FromRenderedWorkflow(
                unsupported,
                card => new DirectorRenderedWorkflowSource(@"C:\Video\continuous.mp4", 2d),
                timeScale: 2d));
        Check.Throws<ArgumentOutOfRangeException>(() =>
            DirectorTimelineFfmpegPlanner.FromRenderedWorkflow(workflow, _ => null, 0d));
    }

    private static void WorkflowAudioEditorValidatesCrudSelectors()
    {
        Check.True(DirectorWorkflowAudioEditor.TryCreate(
            @"C:\Audio Files\guide.wav",
            timelineStartSeconds: 1.5d,
            sourceStartSeconds: 2d,
            durationSeconds: 4d,
            track: 3,
            gainDecibels: -6d,
            fadeInSeconds: 0.25d,
            fadeOutSeconds: 0.5d,
            playbackRate: 1.25d,
            out DirectorTimelineAudioClip clip,
            out string error), error);
        Check.Equal("guide", clip.Name);
        Check.Equal(3, clip.Track);
        Check.NearlyEqual(1.25d, clip.PlaybackRate);

        DirectorWorkflow workflow = new()
        {
            AudioClips = new List<DirectorTimelineAudioClip> { clip }
        };
        Check.Equal(clip.Id, Check.NotNull(DirectorWorkflowAudioEditor.Resolve(workflow, "1")).Id);
        Check.Equal(clip.Id, Check.NotNull(DirectorWorkflowAudioEditor.Resolve(
            workflow, clip.Id.ToString("D"))).Id);
        Check.Equal(clip.Id, Check.NotNull(DirectorWorkflowAudioEditor.Resolve(workflow, "GUIDE")).Id);

        string originalPath = clip.InputPath;
        Check.False(DirectorWorkflowAudioEditor.TryUpdate(
            clip,
            string.Empty,
            0d,
            0d,
            1d,
            0,
            0d,
            0d,
            0d,
            1d,
            out error));
        Check.Equal("workflow-audio-path", error);
        Check.Equal(originalPath, clip.InputPath);

        Guid originalId = clip.Id;
        Check.True(DirectorWorkflowAudioEditor.TryUpdate(
            clip,
            @"C:\Audio\final.flac",
            3d,
            1d,
            2d,
            1,
            -3d,
            0.1d,
            0.2d,
            0.75d,
            out error), error);
        Check.Equal(originalId, clip.Id);
        Check.Equal("final", clip.Name);
        Check.NearlyEqual(3d, clip.TimelineStartSeconds);
        Check.NearlyEqual(0.75d, clip.PlaybackRate);

        Check.False(DirectorWorkflowAudioEditor.TryCreate(
            "audio.wav", 0d, 0d, 1d, 64, 0d, 0d, 0d, 1d, out _, out error));
        Check.Equal("workflow-audio-range", error);
        workflow.AudioClips.Remove(clip);
        Check.True(DirectorWorkflowAudioEditor.Resolve(workflow, "1") is null);
    }

    private static void ClientConfigurationNormalizesAndCopies()
    {
        DirectorConfig loaded = new()
        {
            HideHudOnCamera = false,
            ShowSwitchNotifications = false,
            ShowWorkflowEditors = true,
            ShowRemoteChunkPreload = false,
            ShowCameraSequences = true,
            ShowPackageImportExport = true,
            EnableVsmc2Integration = true,
            Vsmc2ExecutablePath = "  C:\\Tools\\VSMC2.exe  ",
            Vsmc2AutoReload = false,
            Vsmc2FileWatchDebounceMs = 99999,
            EnableModelOutlines = true,
            ModelOutlineColor = "  #0f8  ",
            ModelOutlineThickness = 99f,
            ModelOutlineThroughObstacles = true,
            ModelOutlineEntityCodes = new List<string>
            {
                " game:wolf ",
                "GAME:WOLF",
                "game:player",
                ""
            },
            EnableGreenScreenSupport = true,
            EnableGreenScreenSky = true,
            GreenScreenSkyHideClouds = false,
            EnableGreenScreenModels = true,
            RecordingBackend = (DirectorRecordingBackend)999,
            RecordingContainer = (DirectorRecordingContainer)999,
            RecordingFrameRate = 999,
            RecordingOutputDirectory = "  C:\\videos  ",
            FfmpegPath = "   ",
            FfmpegVideoCrf = 999,
            FfmpegVideoPreset = " impossible ",
            FfmpegAudioBitrateKbps = 1,
            EnableExperimentalOffscreenMultiView = true,
            LiveOffscreenBackend = DirectorLiveOffscreenBackend.Ffmpeg,
            OffscreenOutputDirectory = "  C:\\offscreen  ",
            OffscreenWidth = 17,
            OffscreenHeight = 99999,
            OffscreenFrameRate = 999,
            OffscreenEncoder = (DirectorOffscreenEncoder)999,
            OffscreenVideoQuality = 999,
            OffscreenVideoBitrateKbps = -1,
            OffscreenCaptureDirectorAudio = false,
            OffscreenMuteOtherObsAudio = false,
            OffscreenUseIndependentPlayerRenderer = true,
            NativeCaptureGameAudio = true,
            NativeGameAudioDevice = "  Stereo Mix  ",
            NativeGameAudioVolume = double.NaN,
            NativeCaptureMicrophone = true,
            NativeMicrophoneDevice = "  Microphone  ",
            NativeMicrophoneVolume = 99d,
            NativeAudioSampleRate = 1,
            NativeAudioChannels = 9,
            ObsWebSocketUrl = "   ",
            ObsWebSocketPassword = null!,
            ReplaySampleRate = double.PositiveInfinity,
            ReplayCompression = (DirectorReplayCompressionProfile)999,
            ReplayQuickMode = true,
            ReplayUseDedicatedRenderer = true,
            ReplayMaxActors = 99999,
            ReplaySnapshotRadiusChunks = 999,
            ReplayVoiceCaptureRadiusChunks = 999,
            ReplayEntityStateRate = double.PositiveInfinity,
            ReplayMaxWorldEvents = 1,
            ReplayWorldOperationsPerFrame = 999999,
            ReplayFreeCameraSpeed = 0,
            ReplayFreeCameraMouseSensitivity = 99,
            ReplayExportWarmupFrames = 99,
            RecordReplayWithVideo = false
        };
        loaded.Normalize();

        Check.Equal(DirectorRecordingBackend.ObsWebSocket, loaded.RecordingBackend);
        Check.Equal(DirectorRecordingContainer.Mp4, loaded.RecordingContainer);
        Check.Equal(240, loaded.RecordingFrameRate);
        Check.Equal("C:\\videos", loaded.RecordingOutputDirectory);
        Check.Equal("ffmpeg", loaded.FfmpegPath);
        Check.Equal(51, loaded.FfmpegVideoCrf);
        Check.Equal("medium", loaded.FfmpegVideoPreset);
        Check.Equal(64, loaded.FfmpegAudioBitrateKbps);
        Check.False(loaded.EnableExperimentalOffscreenMultiView);
        Check.Equal(DirectorLiveOffscreenBackend.Ffmpeg, loaded.LiveOffscreenBackend);
        Check.Equal("C:\\offscreen", loaded.OffscreenOutputDirectory);
        Check.Equal(16, loaded.OffscreenWidth);
        Check.Equal(1080, loaded.OffscreenHeight);
        Check.Equal(240, loaded.OffscreenFrameRate);
        Check.Equal(DirectorOffscreenEncoder.Auto, loaded.OffscreenEncoder);
        Check.Equal(51, loaded.OffscreenVideoQuality);
        Check.Equal(0, loaded.OffscreenVideoBitrateKbps);
        Check.False(loaded.OffscreenCaptureDirectorAudio);
        Check.False(loaded.OffscreenMuteOtherObsAudio);
        Check.True(loaded.OffscreenUseIndependentPlayerRenderer);
        Check.False(loaded.NativeCaptureGameAudio);
        Check.Equal("Stereo Mix", loaded.NativeGameAudioDevice);
        Check.Equal(1d, loaded.NativeGameAudioVolume);
        Check.Equal("Microphone", loaded.NativeMicrophoneDevice);
        Check.Equal(2d, loaded.NativeMicrophoneVolume);
        Check.False(loaded.NativeCaptureMicrophone);
        Check.Equal(8000, loaded.NativeAudioSampleRate);
        Check.Equal(2, loaded.NativeAudioChannels);
        Check.Equal("ws://127.0.0.1:4455", loaded.ObsWebSocketUrl);
        Check.Equal(string.Empty, loaded.ObsWebSocketPassword);
        Check.Equal(30d, loaded.ReplaySampleRate);
        Check.Equal(DirectorReplayCompressionProfile.Balanced, loaded.ReplayCompression);
        Check.True(loaded.ReplayQuickMode);
        Check.True(loaded.ReplayUseDedicatedRenderer);
        Check.False(loaded.AutoRecordReplay);
        Check.Equal(2048, loaded.ReplayMaxActors);
        Check.Equal(16, loaded.ReplaySnapshotRadiusChunks);
        Check.Equal(16, loaded.ReplayVoiceCaptureRadiusChunks);
        Check.Equal(2d, loaded.ReplayEntityStateRate);
        Check.Equal(1000, loaded.ReplayMaxWorldEvents);
        Check.Equal(262144, loaded.ReplayWorldOperationsPerFrame);
        Check.Equal(0.1d, loaded.ReplayFreeCameraSpeed);
        Check.Equal(2d, loaded.ReplayFreeCameraMouseSensitivity);
        Check.Equal(8, loaded.ReplayExportWarmupFrames);
        Check.False(loaded.RecordReplayWithVideo);
        Check.True(loaded.EnableModelOutlines);
        Check.Equal("#00FF88", loaded.ModelOutlineColor);
        Check.Equal(16f, loaded.ModelOutlineThickness);
        Check.True(loaded.ModelOutlineThroughObstacles);
        Check.Equal(2, loaded.ModelOutlineEntityCodes.Count);
        Check.Equal("game:player", loaded.ModelOutlineEntityCodes[0]);
        Check.Equal("game:wolf", loaded.ModelOutlineEntityCodes[1]);
        Check.True(loaded.EnableGreenScreenSupport);
        Check.True(loaded.EnableGreenScreenSky);
        Check.False(loaded.GreenScreenSkyHideClouds);
        Check.False(loaded.EnableGreenScreenModels);
        Check.False(loaded.EnableVsmc2Integration);
        Check.Equal("C:\\Tools\\VSMC2.exe", loaded.Vsmc2ExecutablePath);
        Check.False(loaded.Vsmc2AutoReload);
        Check.Equal(5000, loaded.Vsmc2FileWatchDebounceMs);

        DirectorConfig active = new();
        active.CopyFrom(loaded);
        Check.False(active.HideHudOnCamera);
        Check.False(active.ShowSwitchNotifications);
        Check.False(new DirectorConfig().ReplayPauseWorld);
        Check.True(active.ShowWorkflowEditors);
        Check.True(active.ShowRemoteChunkPreload);
        Check.True(active.ShowCameraSequences);
        Check.True(active.ShowPackageImportExport);
        Check.False(active.EnableVsmc2Integration);
        Check.Equal("C:\\Tools\\VSMC2.exe", active.Vsmc2ExecutablePath);
        Check.False(active.Vsmc2AutoReload);
        Check.Equal(5000, active.Vsmc2FileWatchDebounceMs);
        Check.True(active.EnableModelOutlines);
        Check.Equal("#00FF88", active.ModelOutlineColor);
        Check.Equal(16f, active.ModelOutlineThickness);
        Check.True(active.ModelOutlineThroughObstacles);
        Check.Equal(2, active.ModelOutlineEntityCodes.Count);
        Check.True(active.EnableGreenScreenSupport);
        Check.True(active.EnableGreenScreenSky);
        Check.False(active.GreenScreenSkyHideClouds);
        Check.False(active.EnableGreenScreenModels);
        Check.Equal("C:\\videos", active.RecordingOutputDirectory);
        Check.Equal(2048, active.ReplayMaxActors);
        Check.Equal(DirectorReplayCompressionProfile.Balanced, active.ReplayCompression);
        Check.True(active.ReplayQuickMode);
        Check.True(active.ReplayUseDedicatedRenderer);
        Check.False(active.AutoRecordReplay);
        Check.Equal(16, active.ReplaySnapshotRadiusChunks);
        Check.Equal(16, active.ReplayVoiceCaptureRadiusChunks);
        Check.Equal(1000, active.ReplayMaxWorldEvents);
        Check.Equal(262144, active.ReplayWorldOperationsPerFrame);
        Check.Equal(8, active.ReplayExportWarmupFrames);
        Check.False(active.RecordReplayWithVideo);
        Check.Equal(51, active.FfmpegVideoCrf);
        Check.Equal("Stereo Mix", active.NativeGameAudioDevice);
        Check.Equal(2d, active.NativeMicrophoneVolume);
        Check.False(active.EnableExperimentalOffscreenMultiView);
        Check.Equal(DirectorLiveOffscreenBackend.Ffmpeg, active.LiveOffscreenBackend);
        Check.Equal("C:\\offscreen", active.OffscreenOutputDirectory);
        Check.Equal(16, active.OffscreenWidth);
        Check.Equal(1080, active.OffscreenHeight);
        Check.Equal(DirectorOffscreenEncoder.Auto, active.OffscreenEncoder);
        Check.False(active.OffscreenCaptureDirectorAudio);
        Check.False(active.OffscreenMuteOtherObsAudio);
        Check.True(active.OffscreenUseIndependentPlayerRenderer);
        loaded.ModelOutlineEntityCodes.Clear();
        Check.Equal(2, active.ModelOutlineEntityCodes.Count);
        Check.False(new DirectorConfig().OffscreenUseIndependentPlayerRenderer);
        Check.False(new DirectorConfig().ReplayUseDedicatedRenderer);
        Check.False(new DirectorConfig().ShowWorkflowEditors);
        Check.True(new DirectorConfig().ShowRemoteChunkPreload);
        Check.False(new DirectorConfig().ShowCameraSequences);
        Check.False(new DirectorConfig().ShowPackageImportExport);
        Check.False(new DirectorConfig().EnableModelOutlines);
        Check.Equal("#00FF00", new DirectorConfig().ModelOutlineColor);
        Check.Equal(2f, new DirectorConfig().ModelOutlineThickness);
        Check.False(new DirectorConfig().ModelOutlineThroughObstacles);
        Check.Equal(0, new DirectorConfig().ModelOutlineEntityCodes.Count);
        Check.False(new DirectorConfig().EnableGreenScreenSupport);
        Check.False(new DirectorConfig().EnableGreenScreenSky);
        Check.True(new DirectorConfig().GreenScreenSkyHideClouds);
        Check.False(new DirectorConfig().EnableGreenScreenModels);
        Check.False(new DirectorConfig().EnableExperimentalOffscreenMultiView);
        Check.Equal(
            DirectorLiveOffscreenBackend.Obs,
            new DirectorConfig().LiveOffscreenBackend);

        DirectorConfig lowReplayBudget = new() { ReplayWorldOperationsPerFrame = 1 };
        lowReplayBudget.Normalize();
        Check.Equal(4096, lowReplayBudget.ReplayWorldOperationsPerFrame);

        DirectorConfig invalidLiveBackend = new()
        {
            LiveOffscreenBackend = (DirectorLiveOffscreenBackend)999
        };
        invalidLiveBackend.Normalize();
        Check.Equal(DirectorLiveOffscreenBackend.Obs, invalidLiveBackend.LiveOffscreenBackend);
    }

    private static void ObsAuthenticationVectorWorks()
    {
        Check.Equal(
            "bnR2CtDVbmdFHjtDiVOcEgOv6jGdHViYJY7yE2ne82o=",
            DirectorObsAuthentication.Compute("supersecret", "salt", "challenge"));
    }

    private static void LiveOffscreenSourcesValidateSelectors()
    {
        Guid id = Guid.Parse("50edb928-d851-4aa2-a72e-d7a20b740dc1");
        foreach (DirectorRecordingSourceType type in new[]
        {
            DirectorRecordingSourceType.StaticCamera,
            DirectorRecordingSourceType.Path,
            DirectorRecordingSourceType.Sequence,
            DirectorRecordingSourceType.Follow,
            DirectorRecordingSourceType.Workflow
        })
        {
            DirectorLiveOffscreenSource expected = new(type, id);
            Check.True(expected.IsSupported);
            Check.True(DirectorLiveOffscreenSource.TryParse(expected.Serialize(), out var parsed));
            Check.Equal(expected, parsed);
        }

        Check.False(DirectorLiveOffscreenSource.TryParse(null, out _));
        Check.False(DirectorLiveOffscreenSource.TryParse("not-a-source", out _));
        Check.False(DirectorLiveOffscreenSource.TryParse(
            ((int)DirectorRecordingSourceType.Program) + "|" + id.ToString("D"),
            out _));
        Check.False(DirectorLiveOffscreenSource.TryParse(
            ((int)DirectorRecordingSourceType.StaticCamera) + "|" + Guid.Empty.ToString("D"),
            out _));
        DirectorLiveOffscreenSource currentView = new(
            DirectorRecordingSourceType.Program,
            Guid.Empty);
        Check.True(currentView.IsSupported);
        Check.True(DirectorLiveOffscreenSource.TryParse(currentView.Serialize(), out var parsedCurrentView));
        Check.Equal(currentView, parsedCurrentView);
    }

    private static void DirectorPermissionsAreGranular()
    {
        Check.Equal(
            DirectorPermission.OffscreenCurrentView,
            DirectorPermissions.DefaultPlayerPermissions);
        Check.True(DirectorPermissions.IsAdminRole("admin"));
        Check.True(DirectorPermissions.IsAdminRole("ADMIN"));
        Check.False(DirectorPermissions.IsAdminRole("director"));
        Check.False(DirectorPermissions.IsAdminRole("vsdirector"));
        Check.Equal(
            DirectorPermission.All,
            DirectorPermissions.Sanitize(long.MaxValue));
        Check.Equal(
            DirectorPermissions.Definitions.Count,
            DirectorPermissions.Definitions.Select(definition => definition.Permission).Distinct().Count());
        Check.Equal(
            DirectorPermissions.Definitions.Count,
            DirectorPermissions.Definitions.Select(definition => definition.Code).Distinct().Count());
        Check.Equal(
            DirectorPermission.All,
            DirectorPermissions.Definitions.Aggregate(
                DirectorPermission.None,
                (mask, definition) => mask | definition.Permission));
        Check.False(DirectorPermissions.Definitions.Any(definition =>
            definition.Code is "vsdirector.recording-sync" or "vsdirector.monitor"));
        Check.False(DirectorPermissions.Definitions.Any(definition =>
            definition.Code == "vsdirector.actions"));
        Check.False(DirectorPermissions.Definitions.Any(definition =>
            definition.Permission is DirectorPermission.CameraSequences
                or DirectorPermission.Workflows
                or DirectorPermission.PresetPackages));
    }

    private static void DirectShowAudioDevicesParse()
    {
        const string diagnostics = """
            [dshow @ 000001] "Stereo Mix (Realtek Audio)" (audio)
            [dshow @ 000001]   Alternative name "@device_cm_{abc}\\wave_{one}"
            [dshow @ 000001] "Microphone (USB Audio Device)" (audio)
            [dshow @ 000001] "Camera" (video)
            [dshow @ 000001] "stereo mix (realtek audio)" (audio)
            """;

        Check.SequenceEqual(
            new[] { "Stereo Mix (Realtek Audio)", "Microphone (USB Audio Device)" },
            DirectorFfmpegAudioDevices.ParseDirectShowAudioDevices(diagnostics));
    }

    private static void ReplayRemoteChunkAnchorSelectionWorks()
    {
        DirectorReplayActorTrack animal = new()
        {
            ActorKey = "animal",
            Samples = new List<DirectorReplayActorSample>
            {
                new() { TimeSeconds = 0d, X = 10d, Y = 2d, Z = 11d, Dimension = 0 },
                new() { TimeSeconds = 10d, X = 15d, Y = 2d, Z = 16d, Dimension = 0 }
            }
        };
        DirectorReplayActorTrack otherPlayer = new()
        {
            ActorKey = "other-player",
            PlayerUid = "other",
            Samples = new List<DirectorReplayActorSample>
            {
                new() { TimeSeconds = 0d, X = 100d, Y = 4d, Z = 110d, Dimension = 0 },
                new() { TimeSeconds = 10d, X = 110d, Y = 4d, Z = 120d, Dimension = 0 }
            }
        };
        DirectorReplayActorTrack preferredPlayer = new()
        {
            ActorKey = "preferred-player",
            PlayerUid = "preferred",
            Samples = new List<DirectorReplayActorSample>
            {
                new() { TimeSeconds = 0d, X = 200d, Y = 6d, Z = 210d, Dimension = 0 },
                new() { TimeSeconds = 10d, X = 220d, Y = 6d, Z = 230d, Dimension = 0 }
            }
        };
        DirectorReplayDocument document = new()
        {
            Actors = new List<DirectorReplayActorTrack> { animal, otherPlayer, preferredPlayer }
        };

        Check.True(DirectorReplayAnchorSelector.TrySelect(document, 5d, "preferred", out DirectorReplayAnchorPose pose));
        Check.NearlyEqual(210d, pose.X);
        Check.NearlyEqual(220d, pose.Z);

        preferredPlayer.Lifetimes.Add(new DirectorReplayActorLifetime
        {
            StartSeconds = 0d,
            EndSeconds = 2d,
            EndedByDespawn = true
        });
        Check.True(DirectorReplayAnchorSelector.TrySelect(document, 5d, "preferred", out pose));
        Check.NearlyEqual(105d, pose.X);
        Check.NearlyEqual(115d, pose.Z);

        document.Actors.Clear();
        document.WorldSnapshot = new DirectorReplayWorldSnapshot
        {
            CenterChunkX = 20,
            CenterChunkZ = 30,
            Dimension = 2,
            Chunks = new List<DirectorReplayChunkSnapshot> { new() }
        };
        Check.True(DirectorReplayAnchorSelector.TrySelect(document, 0d, "preferred", out pose));
        Check.NearlyEqual(656d, pose.X);
        Check.NearlyEqual(976d, pose.Z);
        Check.Equal(2, pose.Dimension);
    }

    private static void ReplayRemoteChunkWindowReadinessWorks()
    {
        HashSet<(int X, int Y, int Z)> available = new();
        int dimensionOffset = 2 * DirectorChunkWindow.DimensionHeight / 32;
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                if (offsetX * offsetX + offsetZ * offsetZ > 1)
                {
                    continue;
                }

                for (int chunkY = 0; chunkY < 8; chunkY++)
                {
                    available.Add((100 + offsetX, dimensionOffset + chunkY, 200 + offsetZ));
                }
            }
        }

        Check.True(DirectorChunkWindow.IsReady(
            100,
            200,
            dimension: 2,
            radiusChunks: 1,
            mapSizeY: 256,
            chunkSize: 32,
            (x, y, z) => available.Contains((x, y, z))));

        available.Remove((101, dimensionOffset + 4, 200));
        Check.False(DirectorChunkWindow.IsReady(
            100,
            200,
            dimension: 2,
            radiusChunks: 1,
            mapSizeY: 256,
            chunkSize: 32,
            (x, y, z) => available.Contains((x, y, z))));
    }

    private static void ReplayPreloadPlannerBuildsContinuousCorridor()
    {
        DirectorReplayDocument document = new()
        {
            Actors = new List<DirectorReplayActorTrack>
            {
                new()
                {
                    ActorKey = "animal",
                    Samples = new List<DirectorReplayActorSample>
                    {
                        new() { X = 640d, Z = 640d, Dimension = 0 }
                    }
                },
                new()
                {
                    ActorKey = "preferred",
                    PlayerUid = "preferred",
                    Samples = new List<DirectorReplayActorSample>
                    {
                        new() { X = 0d, Z = 0d, Dimension = 0 },
                        new() { X = 96d, Z = 64d, Dimension = 0 }
                    }
                }
            },
            WorldSnapshot = new DirectorReplayWorldSnapshot
            {
                Dimension = 0,
                Chunks = Enumerable.Range(100, 9)
                    .SelectMany(x => Enumerable.Range(100, 9).Select(z => new DirectorReplayChunkSnapshot
                    {
                        ChunkX = x,
                        ChunkY = 0,
                        ChunkZ = z
                    }))
                    .ToList()
            }
        };

        DirectorReplayPreloadPlan plan = DirectorReplayPreloadPlanner.Build(
            document,
            "preferred",
            includeWorldSnapshotColumns: false,
            radiusChunks: 1,
            maxSourceColumns: 32);
        Check.SequenceEqual(
            new[]
            {
                new DirectorReplayPreloadColumn(0, 0, 0),
                new DirectorReplayPreloadColumn(1, 1, 0),
                new DirectorReplayPreloadColumn(2, 1, 0),
                new DirectorReplayPreloadColumn(3, 2, 0)
            },
            plan.SourceColumns);
        Check.True(plan.ExpandedColumnSet.Contains(new DirectorReplayPreloadColumn(-1, 0, 0)));
        Check.True(plan.ExpandedColumnSet.Contains(new DirectorReplayPreloadColumn(4, 2, 0)));
        Check.False(plan.AutomaticColumnSet.Contains(new DirectorReplayPreloadColumn(100, 100, 0)));
        Check.False(plan.Truncated);

        DirectorReplayPreloadPlan snapshotPlan = DirectorReplayPreloadPlanner.Build(
            document,
            "preferred",
            includeWorldSnapshotColumns: true,
            radiusChunks: 1,
            maxSourceColumns: 32);
        Check.True(Enumerable.Range(100, 9).All(x => Enumerable.Range(100, 9).All(z =>
            snapshotPlan.AutomaticColumnSet.Contains(new DirectorReplayPreloadColumn(x, z, 0)))));

        DirectorReplayPreloadPlan limited = DirectorReplayPreloadPlanner.Build(
            document,
            "preferred",
            includeWorldSnapshotColumns: false,
            radiusChunks: 1,
            maxSourceColumns: 2);
        Check.Equal(2, limited.SourceColumns.Count);
        Check.True(limited.Truncated);

        DirectorReplayPreloadColumn automaticExcluded = new(1, 1, 0);
        DirectorReplayPreloadColumn manualVista = new(40, 36, 0);
        document.IncludedPreloadColumns = new List<DirectorReplayPreloadColumn>
        {
            manualVista,
            manualVista,
            new(-1, 2, 0)
        };
        document.ExcludedPreloadColumns = new List<DirectorReplayPreloadColumn>
        {
            automaticExcluded
        };
        DirectorReplayPreloadPlan overridden = DirectorReplayPreloadPlanner.Build(
            document,
            "preferred",
            includeWorldSnapshotColumns: false);
        Check.True(overridden.AutomaticColumnSet.Contains(automaticExcluded));
        Check.False(overridden.ExpandedColumnSet.Contains(automaticExcluded));
        Check.True(overridden.ExpandedColumnSet.Contains(manualVista));
        Check.SequenceEqual(
            new[] { manualVista },
            DirectorReplayPreloadPlanner.NormalizeOverrideColumns(document.IncludedPreloadColumns));

        CameraPath path = new()
        {
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, X = 320d, Z = 320d, Fov = 70f },
                new() { TimeSeconds = 2d, X = 448d, Z = 384d, Fov = 70f }
            },
            IncludedPreloadColumns = new List<DirectorReplayPreloadColumn> { new(80, 75, 0) },
            ExcludedPreloadColumns = new List<DirectorReplayPreloadColumn> { new(10, 10, 0) }
        };
        DirectorReplayPreloadPlan pathPlan = DirectorReplayPreloadPlanner.Build(path);
        Check.True(pathPlan.SourceColumns.Count >= 5);
        Check.False(pathPlan.ExpandedColumnSet.Contains(new DirectorReplayPreloadColumn(10, 10, 0)));
        Check.True(pathPlan.ExpandedColumnSet.Contains(new DirectorReplayPreloadColumn(80, 75, 0)));
    }

    private static void ReplayParticleDeduplicationAndBacklogPolicyWork()
    {
        static DirectorReplayParticleEvent Provider(double time, byte value) => new()
        {
            TimeSeconds = time,
            Kind = DirectorReplayParticleKind.Provider,
            ProviderType = "test.Provider",
            ProviderData = new[] { value }
        };

        static DirectorReplayParticleEvent Simple(double time, float quantity) => new()
        {
            TimeSeconds = time,
            Kind = DirectorReplayParticleKind.Simple,
            Quantity = quantity,
            Color = 0x123456,
            MinX = 1d,
            MinY = 2d,
            MinZ = 3d,
            MaxX = 4d,
            MaxY = 5d,
            MaxZ = 6d,
            MinVelocityX = 0.1f,
            MinVelocityY = 0.2f,
            MinVelocityZ = 0.3f,
            MaxVelocityX = 0.4f,
            MaxVelocityY = 0.5f,
            MaxVelocityZ = 0.6f,
            LifeLength = 1.5f,
            GravityEffect = 0.25f,
            Scale = 0.75f,
            ParticleModel = 2
        };

        DirectorReplayDocument normalized = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            ParticleEvents = new List<DirectorReplayParticleEvent>
            {
                Provider(1d, 1),
                Provider(1d, 2),
                Provider(1d, 1),
                Provider(2d, 1),
                Simple(3d, 4f),
                Simple(3d, 4f),
                Simple(3d, 5f)
            }
        });
        Check.Equal(5, normalized.ParticleEvents.Count);
        Check.SequenceEqual(
            new byte[] { 1, 2 },
            normalized.ParticleEvents.Take(2).Select(item => item.ProviderData[0]));
        Check.Equal(1, normalized.ParticleEvents[2].ProviderData[0]);
        Check.Equal(4f, normalized.ParticleEvents[3].Quantity);
        Check.Equal(5f, normalized.ParticleEvents[4].Quantity);

        List<DirectorReplayParticleEvent> small = new()
        {
            Provider(0.1d, 1),
            Provider(0.2d, 2),
            Provider(0.3d, 3),
            Provider(0.4d, 4)
        };
        DirectorReplayParticlePlaybackWindow smallWindow = DirectorReplayPlaybackPolicy.SelectParticleWindow(
            small,
            nextEvent: 0,
            playbackTimeSeconds: 0.3d,
            maxEventAgeSeconds: 1d,
            maxEvents: 128);
        Check.Equal(0, smallWindow.StartIndex);
        Check.Equal(3, smallWindow.EndIndex);

        List<DirectorReplayParticleEvent> sameTimestamp = Enumerable.Range(0, 1966)
            .Select(index => Provider(2d, (byte)(index % 251)))
            .ToList();
        DirectorReplayParticlePlaybackWindow bounded = DirectorReplayPlaybackPolicy.SelectParticleWindow(
            sameTimestamp,
            nextEvent: 0,
            playbackTimeSeconds: 2d,
            maxEventAgeSeconds: 0.25d,
            maxEvents: 128);
        Check.Equal(1966 - 128, bounded.StartIndex);
        Check.Equal(1966, bounded.EndIndex);

        List<DirectorReplayParticleEvent> aged = new()
        {
            Provider(0d, 1),
            Provider(0.5d, 2),
            Provider(0.8d, 3),
            Provider(0.9d, 4),
            Provider(1d, 5),
            Provider(1.1d, 6)
        };
        DirectorReplayParticlePlaybackWindow agedWindow = DirectorReplayPlaybackPolicy.SelectParticleWindow(
            aged,
            nextEvent: 3,
            playbackTimeSeconds: 1d,
            maxEventAgeSeconds: 0.25d,
            maxEvents: 128);
        Check.Equal(3, agedWindow.StartIndex);
        Check.Equal(5, agedWindow.EndIndex);

        Check.Equal(
            0,
            DirectorReplayPlaybackPolicy.AvailableParticleSlots(
                aliveCount: 1536,
                maximumAlive: 2048,
                reservedPrioritySlots: 512,
                priority: false));
        Check.Equal(
            512,
            DirectorReplayPlaybackPolicy.AvailableParticleSlots(
                aliveCount: 1536,
                maximumAlive: 2048,
                reservedPrioritySlots: 512,
                priority: true));
        Check.Equal(
            2,
            DirectorReplayPlaybackPolicy.AvailableParticleSlots(
                aliveCount: 4,
                maximumAlive: 8,
                reservedPrioritySlots: 2,
                priority: false));
        DirectorReplayParticleFrameClock<object> frameClock = new();
        object firstPool = new();
        object secondPool = new();
        Check.Equal(0.75f, frameClock.Read(0.75f).DeltaSeconds);
        frameClock.Publish(usesReplayClock: true, frameDeltaSeconds: 0.25f);
        Check.Equal(0.25f, frameClock.Read(0.75f).DeltaSeconds);
        Check.Equal(0.25f, frameClock.Consume(firstPool, 0.75f).DeltaSeconds);
        Check.Equal(0f, frameClock.Consume(firstPool, 0.75f).DeltaSeconds);
        Check.Equal(0.25f, frameClock.Consume(secondPool, 0.75f).DeltaSeconds);
        Check.Equal(0.25f, frameClock.Read(0.75f).DeltaSeconds);
        frameClock.Publish(usesReplayClock: true, frameDeltaSeconds: 0.5f);
        Check.Equal(0.5f, frameClock.Consume(firstPool, 0.75f).DeltaSeconds);
        frameClock.Reset();
        DirectorReplayParticleFrameDelta liveDelta = frameClock.Consume(firstPool, 0.75f);
        Check.False(liveDelta.UsesReplayClock);
        Check.Equal(0.75f, liveDelta.DeltaSeconds);

        float exportSeekDelta = DirectorReplayPlaybackPolicy.ResolvePendingParticleDelta(
            replayFrameDeltaSeconds: 1f / 30f,
            pendingDeltaSeconds: 0f,
            resetTimeline: false);
        Check.Equal(1f / 30f, exportSeekDelta);
        Check.Equal(
            exportSeekDelta,
            DirectorReplayPlaybackPolicy.ResolvePendingParticleDelta(
                replayFrameDeltaSeconds: 0f,
                pendingDeltaSeconds: exportSeekDelta,
                resetTimeline: false));
        Check.Equal(
            0f,
            DirectorReplayPlaybackPolicy.ResolvePendingParticleDelta(
                replayFrameDeltaSeconds: 0f,
                pendingDeltaSeconds: exportSeekDelta,
                resetTimeline: true));
        Check.Equal(
            0f,
            DirectorReplayPlaybackPolicy.ResolvePendingParticleDelta(
                replayFrameDeltaSeconds: 0f,
                pendingDeltaSeconds: 0f,
                resetTimeline: false));
        Check.True(DirectorReplayPlaybackPolicy.ShouldAdvanceParticleClock(
            playbackPaused: false,
            exportActive: false));
        Check.True(DirectorReplayPlaybackPolicy.ShouldAdvanceParticleClock(
            playbackPaused: true,
            exportActive: true));
        Check.False(DirectorReplayPlaybackPolicy.ShouldAdvanceParticleClock(
            playbackPaused: true,
            exportActive: false));
        Check.False(DirectorReplayPlaybackPolicy.ShouldPauseReplaySounds(
            playbackPaused: true,
            gamePaused: false,
            obsExportRecording: true));
        Check.True(DirectorReplayPlaybackPolicy.ShouldPauseReplaySounds(
            playbackPaused: true,
            gamePaused: false,
            obsExportRecording: false));
        Check.True(DirectorReplayPlaybackPolicy.ShouldPauseReplaySounds(
            playbackPaused: false,
            gamePaused: true,
            obsExportRecording: true));
    }

    private static void ReplaySoundPlaybackPolicyWorks()
    {
        Check.True(DirectorReplayPlaybackPolicy.IsValidSoundPosition(0d, 0d, 0d));
        Check.True(DirectorReplayPlaybackPolicy.IsValidSoundPosition(1d, 0d, 0d));
        Check.False(DirectorReplayPlaybackPolicy.IsValidSoundPosition(double.NaN, 2d, 3d));
        Check.False(DirectorReplayPlaybackPolicy.IsValidSoundPosition(1d, double.PositiveInfinity, 3d));

        List<DirectorReplaySoundEvent> burst = Enumerable.Range(0, 40)
            .Select(index => new DirectorReplaySoundEvent { TimeSeconds = 0.1d + index * 0.01d })
            .ToList();
        DirectorReplaySoundPlaybackWindow wholeBurst =
            DirectorReplayPlaybackPolicy.SelectSoundWindow(burst, nextEvent: 0, playbackTimeSeconds: 1d);
        Check.Equal(0, wholeBurst.StartIndex);
        Check.Equal(40, wholeBurst.EndIndex);
        DirectorReplaySoundPlaybackWindow remainingBurst =
            DirectorReplayPlaybackPolicy.SelectSoundWindow(burst, nextEvent: 17, playbackTimeSeconds: 1d);
        Check.Equal(17, remainingBurst.StartIndex);
        Check.Equal(40, remainingBurst.EndIndex);

        List<DirectorReplaySoundEvent> loopTimeline = new()
        {
            new()
            {
                TimeSeconds = 0d,
                InstanceId = 10L,
                Action = DirectorReplaySoundAction.Play,
                ShouldLoop = true,
                Volume = 0.4f
            },
            new()
            {
                TimeSeconds = 0.5d,
                InstanceId = 10L,
                Action = DirectorReplaySoundAction.Update,
                ShouldLoop = true,
                Volume = 0.7f
            },
            new()
            {
                TimeSeconds = 0.75d,
                InstanceId = 20L,
                Action = DirectorReplaySoundAction.Play,
                ShouldLoop = true
            },
            new()
            {
                TimeSeconds = 1d,
                InstanceId = 10L,
                Action = DirectorReplaySoundAction.Pause,
                ShouldLoop = true
            },
            new()
            {
                TimeSeconds = 1.25d,
                InstanceId = 10L,
                Action = DirectorReplaySoundAction.Play,
                ShouldLoop = true,
                PlaybackPosition = 0.25f
            },
            new()
            {
                TimeSeconds = 1.5d,
                InstanceId = 20L,
                Action = DirectorReplaySoundAction.Stop,
                ShouldLoop = true
            },
            new()
            {
                TimeSeconds = 2d,
                InstanceId = 30L,
                Action = DirectorReplaySoundAction.Play,
                ShouldLoop = false,
                PlaybackPosition = 0.25f,
                SoundLengthSeconds = 2f,
                Pitch = 1f
            }
        };
        IReadOnlyList<DirectorReplaySoundEvent> loopsAtOneSecond =
            DirectorReplayPlaybackPolicy.SelectActiveSounds(loopTimeline, 1d);
        Check.Equal(1, loopsAtOneSecond.Count);
        Check.Equal(20L, loopsAtOneSecond[0].InstanceId);
        IReadOnlyList<DirectorReplaySoundEvent> loopsAtOnePointFour =
            DirectorReplayPlaybackPolicy.SelectActiveSounds(loopTimeline, 1.4d);
        Check.Equal(2, loopsAtOnePointFour.Count);
        Check.Equal(10L, loopsAtOnePointFour[0].InstanceId);
        Check.NearlyEqual(0.25d, loopsAtOnePointFour[0].PlaybackPosition);
        IReadOnlyList<DirectorReplaySoundEvent> loopsAtTwoSeconds =
            DirectorReplayPlaybackPolicy.SelectActiveSounds(loopTimeline, 2d);
        Check.Equal(2, loopsAtTwoSeconds.Count);
        Check.Equal(10L, loopsAtTwoSeconds[0].InstanceId);
        Check.Equal(30L, loopsAtTwoSeconds[1].InstanceId);
        IReadOnlyList<DirectorReplaySoundEvent> soundsAtFourSeconds =
            DirectorReplayPlaybackPolicy.SelectActiveSounds(loopTimeline, 4d);
        Check.Equal(1, soundsAtFourSeconds.Count);
        Check.Equal(10L, soundsAtFourSeconds[0].InstanceId);

        DirectorReplayDocument legacy = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            SchemaVersion = 16,
            SoundEvents = new List<DirectorReplaySoundEvent>
            {
                new() { Location = "game:sounds/block/rock-break", X = 0d, Y = 0d, Z = 0d }
            }
        });
        Check.True(legacy.SoundEvents[0].RelativePosition);

        DirectorReplayDocument current = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            SchemaVersion = DirectorReplayDocument.CurrentSchemaVersion,
            SoundEvents = new List<DirectorReplaySoundEvent>
            {
                new()
                {
                    Location = "game:sounds/block/rock-break",
                    X = 0d,
                    Y = 0d,
                    Z = 0d,
                    RelativePosition = false
                }
            }
        });
        Check.False(current.SoundEvents[0].RelativePosition);
    }

    private static void ReplayInterpolationAndAngleWrappingWork()
    {
        float degreesToRadians = MathF.PI / 180f;
        DirectorReplayActorTrack actor = new()
        {
            ActorKey = "player:test",
            Samples = new List<DirectorReplayActorSample>
            {
                new()
                {
                    TimeSeconds = 0d,
                    X = 0d,
                    Yaw = 170f * degreesToRadians,
                    BodyYaw = 20f * degreesToRadians,
                    Animations = new List<string> { "idle" }
                },
                new()
                {
                    TimeSeconds = 2d,
                    X = 10d,
                    Yaw = -170f * degreesToRadians,
                    BodyYaw = 40f * degreesToRadians,
                    Animations = new List<string> { "walk" }
                }
            }
        };

        Check.True(DirectorReplayMath.TrySample(actor, 1d, out DirectorReplayActorSample middle));
        Check.NearlyEqual(5d, middle.X);
        Check.NearlyEqual(Math.PI, Math.Abs(middle.Yaw), 0.0001d);
        Check.NearlyEqual(30d * degreesToRadians, middle.BodyYaw, 0.0001d);
        Check.NearlyEqual(5d, middle.MotionX);
        Check.SequenceEqual(new[] { "walk" }, middle.Animations);

        DirectorReplayActorTrack nativePlayerPitch = new()
        {
            Samples = new List<DirectorReplayActorSample>
            {
                new()
                {
                    TimeSeconds = 0d,
                    HasPlayerState = true,
                    Pitch = MathF.PI - 0.4f
                },
                new()
                {
                    TimeSeconds = 2d,
                    HasPlayerState = true,
                    Pitch = MathF.PI + 0.4f
                }
            }
        };
        Check.True(DirectorReplayMath.TrySample(
            nativePlayerPitch,
            1d,
            out DirectorReplayActorSample nativePitchMiddle));
        Check.NearlyEqual(Math.PI, nativePitchMiddle.Pitch, 0.0001d);

        DirectorReplayActorSample stepped = new();
        Check.True(DirectorReplayMath.TrySample(
            actor,
            1d,
            stepped,
            interpolate: false));
        Check.NearlyEqual(0d, stepped.TimeSeconds);
        Check.NearlyEqual(0d, stepped.X);
        Check.NearlyEqual(170d * degreesToRadians, stepped.Yaw, 0.0001d);
        Check.NearlyEqual(20d * degreesToRadians, stepped.BodyYaw, 0.0001d);
        Check.SequenceEqual(new[] { "idle" }, stepped.Animations);
        Check.True(DirectorReplayMath.TrySamplePosition(
            actor,
            1d,
            interpolate: false,
            out DirectorReplayActorPosition steppedPosition));
        Check.NearlyEqual(0d, steppedPosition.X);
        Check.NearlyEqual(0d, steppedPosition.Y);
        Check.NearlyEqual(0d, steppedPosition.Z);
        Check.Equal(0, steppedPosition.Dimension);

        foreach (double time in new[] { double.NaN, 0d, 1d, 2d })
        {
            Check.True(DirectorReplayMath.TrySample(actor, time, out DirectorReplayActorSample full));
            Check.True(DirectorReplayMath.TrySamplePosition(actor, time, out DirectorReplayActorPosition position));
            Check.NearlyEqual(full.X, position.X);
            Check.NearlyEqual(full.Y, position.Y);
            Check.NearlyEqual(full.Z, position.Z);
            Check.Equal(full.Dimension, position.Dimension);
        }

        DirectorReplayActorTrack dimensionJump = new()
        {
            Samples = new List<DirectorReplayActorSample>
            {
                new() { TimeSeconds = 0d, X = 1d, Y = 2d, Z = 3d, Dimension = 0 },
                new() { TimeSeconds = 2d, X = 9d, Y = 8d, Z = 7d, Dimension = 2 }
            }
        };
        foreach (double time in new[] { 0.9d, 1.1d })
        {
            Check.True(DirectorReplayMath.TrySample(dimensionJump, time, out DirectorReplayActorSample full));
            Check.True(DirectorReplayMath.TrySamplePosition(
                dimensionJump,
                time,
                out DirectorReplayActorPosition position));
            Check.NearlyEqual(full.X, position.X);
            Check.NearlyEqual(full.Y, position.Y);
            Check.NearlyEqual(full.Z, position.Z);
            Check.Equal(full.Dimension, position.Dimension);
        }

        DirectorReplayActorTrack animated = new()
        {
            PlayerUid = "animated",
            Samples = new List<DirectorReplayActorSample>
            {
                new()
                {
                    TimeSeconds = 0d,
                    HasMotion = true,
                    MotionX = 2d,
                    HasRenderState = true,
                    OnGround = false,
                    FeetInLiquid = false,
                    Swimming = false,
                    ClimbingOnFace = "north",
                    LocalEyePosY = 1.45d,
                    SelectionBoxY2 = 1.5f,
                    CollisionBoxY2 = 1.5f,
                    HasItemRenderState = true,
                    ItemRenderElapsedMilliseconds = 1_000L,
                    ItemTouchGroundAgeMilliseconds = 200L,
                    ItemScaleRandom = -0.02f,
                    ItemYawRandom = 170f * degreesToRadians,
                    ItemRenderXAngle = 10f,
                    ItemRenderYAngle = 20f,
                    ItemRenderZAngle = 30f,
                    ItemRenderAccumulator = 0.01f,
                    HasAgentState = true,
                    BodyYawServer = 10f * degreesToRadians,
                    HasPlayerState = true,
                    HasPlayerPostureState = true,
                    CanStandUp = false,
                    Controls = 32,
                    ServerControls = 32,
                    HandUse = 1,
                    ServerHandUse = 1,
                    WalkYaw = 5f * degreesToRadians,
                    WalkPitch = 10f * degreesToRadians,
                    RightHandItem = new byte[] { 1, 2 },
                    EquipmentItems = new List<byte[]> { new byte[] { 5 }, Array.Empty<byte>() },
                    BackpackItems = new List<byte[]> { new byte[] { 6 } },
                    Animations = new List<string> { "walk" },
                    AnimationStates = new List<DirectorReplayAnimationSample>
                    {
                        new()
                        {
                            Code = "walk",
                            StateCode = "walk-fp",
                            Progress = 0.9f,
                            Iterations = 0,
                            EasingFactor = 0.8f
                        }
                    }
                },
                new()
                {
                    TimeSeconds = 2d,
                    X = 4d,
                    HasMotion = true,
                    MotionX = 4d,
                    HasRenderState = true,
                    OnGround = true,
                    FeetInLiquid = true,
                    Swimming = true,
                    ClimbingOnFace = "east",
                    LocalEyePosY = 0.95d,
                    SelectionBoxY2 = 1.1f,
                    CollisionBoxY2 = 1.1f,
                    HasItemRenderState = true,
                    ItemRenderElapsedMilliseconds = 3_000L,
                    ItemTouchGroundAgeMilliseconds = 600L,
                    ItemScaleRandom = 0.02f,
                    ItemYawRandom = -170f * degreesToRadians,
                    ItemRenderXAngle = 30f,
                    ItemRenderYAngle = 40f,
                    ItemRenderZAngle = 50f,
                    ItemRenderAccumulator = 0.03f,
                    HasAgentState = true,
                    BodyYawServer = 30f * degreesToRadians,
                    HasPlayerState = true,
                    HasPlayerPostureState = true,
                    CanStandUp = true,
                    Controls = 64,
                    ServerControls = 64,
                    HandUse = 2,
                    ServerHandUse = 2,
                    WalkYaw = 25f * degreesToRadians,
                    WalkPitch = 30f * degreesToRadians,
                    RightHandItem = new byte[] { 3, 4 },
                    EquipmentItems = new List<byte[]> { new byte[] { 7 }, new byte[] { 8 } },
                    BackpackItems = new List<byte[]> { Array.Empty<byte>() },
                    Animations = new List<string> { "walk" },
                    AnimationStates = new List<DirectorReplayAnimationSample>
                    {
                        new()
                        {
                            Code = "walk",
                            StateCode = "walk",
                            Progress = 0.1f,
                            Iterations = 1,
                            EasingFactor = 1f
                        }
                    }
                }
            }
        };
        DirectorReplayActorSample reusable = new();
        Check.True(DirectorReplayMath.TrySample(animated, 1d, reusable));
        Check.NearlyEqual(3d, reusable.MotionX);
        Check.Equal(1, reusable.AnimationStates.Count);
        Check.NearlyEqual(0d, reusable.AnimationStates[0].Progress, 0.0001d);
        Check.NearlyEqual(0.9d, reusable.AnimationStates[0].EasingFactor, 0.0001d);
        Check.True(reusable.HasRenderState);
        Check.True(reusable.OnGround);
        Check.True(reusable.FeetInLiquid);
        Check.True(reusable.Swimming);
        Check.Equal("east", reusable.ClimbingOnFace);
        Check.NearlyEqual(1.2d, reusable.LocalEyePosY, 0.0001d);
        Check.NearlyEqual(1.3d, reusable.SelectionBoxY2, 0.0001d);
        Check.True(reusable.HasItemRenderState);
        Check.Equal(2_000L, reusable.ItemRenderElapsedMilliseconds);
        Check.Equal(400L, reusable.ItemTouchGroundAgeMilliseconds);
        Check.NearlyEqual(0d, reusable.ItemScaleRandom, 0.0001d);
        Check.NearlyEqual(Math.PI, Math.Abs(reusable.ItemYawRandom), 0.0001d);
        Check.NearlyEqual(20d, reusable.ItemRenderXAngle, 0.0001d);
        Check.NearlyEqual(30d, reusable.ItemRenderYAngle, 0.0001d);
        Check.NearlyEqual(40d, reusable.ItemRenderZAngle, 0.0001d);
        Check.NearlyEqual(0.02d, reusable.ItemRenderAccumulator, 0.0001d);
        Check.True(reusable.HasAgentState);
        Check.NearlyEqual(20d * degreesToRadians, reusable.BodyYawServer, 0.0001d);
        Check.True(reusable.HasPlayerState);
        Check.True(reusable.HasPlayerPostureState);
        Check.True(reusable.CanStandUp);
        Check.Equal(64, reusable.Controls);
        Check.Equal(2, reusable.HandUse);
        Check.NearlyEqual(15d * degreesToRadians, reusable.WalkYaw, 0.0001d);
        Check.NearlyEqual(20d * degreesToRadians, reusable.WalkPitch, 0.0001d);
        Check.SequenceEqual(new byte[] { 3, 4 }, reusable.RightHandItem);
        Check.SequenceEqual(new byte[] { 7 }, reusable.EquipmentItems[0]);
        Check.SequenceEqual(new byte[] { 8 }, reusable.EquipmentItems[1]);
        Check.Empty(reusable.BackpackItems[0]);
        Check.Equal("walk", reusable.AnimationStates[0].StateCode);
        List<DirectorReplayAnimationSample> reusedStates = reusable.AnimationStates;
        DirectorReplayAnimationSample reusedState = reusable.AnimationStates[0];
        Dictionary<string, float> reusedElementWeights = reusedState.ElementWeights;
        Dictionary<string, int> reusedElementBlendModes = reusedState.ElementBlendModes;
        Check.True(DirectorReplayMath.TrySample(animated, 1.5d, reusable));
        Check.True(ReferenceEquals(reusedStates, reusable.AnimationStates));
        Check.True(ReferenceEquals(reusedState, reusable.AnimationStates[0]));
        Check.True(ReferenceEquals(reusedElementWeights, reusable.AnimationStates[0].ElementWeights));
        Check.True(ReferenceEquals(reusedElementBlendModes, reusable.AnimationStates[0].ElementBlendModes));

        DirectorReplayDocument normalized = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            SchemaVersion = 0,
            Name = "  Take  ",
            SampleRate = 999d,
            ParticleCaptureVersion = 99,
            Actors = new List<DirectorReplayActorTrack> { actor, animated }
        });
        Check.Equal(DirectorReplayDocument.CurrentSchemaVersion, normalized.SchemaVersion);
        Check.Equal("Take", normalized.Name);
        Check.Equal(60d, normalized.SampleRate);
        Check.Equal(DirectorReplayDocument.CurrentParticleCaptureVersion, normalized.ParticleCaptureVersion);
        Check.Equal(2d, normalized.DurationSeconds);
        Check.Equal(DirectorReplayDocument.ClientActorCaptureScope, normalized.CaptureScope);
        Check.False(normalized.IncludesWorldSnapshot);
        Check.Equal(1, normalized.Actors[1].Samples[0].AnimationStates.Count);
        Check.True(normalized.Actors[1].Samples[0].HasMotion);
        Check.True(normalized.Actors[1].Samples[0].HasRenderState);
        Check.True(normalized.Actors[1].Samples[0].HasAgentState);
        Check.True(normalized.Actors[1].Samples[0].HasPlayerState);
        Check.True(normalized.Actors[1].Samples[0].HasPlayerPostureState);
        Check.False(normalized.Actors[1].Samples[0].CanStandUp);
        Check.Equal(32, normalized.Actors[1].Samples[0].Controls);
        Check.NearlyEqual(10d * degreesToRadians, normalized.Actors[1].Samples[0].BodyYawServer, 0.0001d);
        Check.NearlyEqual(10d * degreesToRadians, normalized.Actors[1].Samples[0].WalkPitch, 0.0001d);
        Check.SequenceEqual(new byte[] { 1, 2 }, normalized.Actors[1].Samples[0].RightHandItem);
        Check.Equal("walk", normalized.Actors[1].Samples[0].AnimationStates[0].StateCode);

        DirectorReplayDocument layeredAnimations = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Actors = new List<DirectorReplayActorTrack>
            {
                new()
                {
                    ActorKey = "layered",
                    Samples = new List<DirectorReplayActorSample>
                    {
                        new()
                        {
                            AnimationStates = new List<DirectorReplayAnimationSample>
                            {
                                new() { Code = "modpose", StateCode = "upper-body" },
                                new() { Code = "modpose", StateCode = "facial-layer" }
                            }
                        }
                    }
                }
            }
        });
        Check.Equal(2, layeredAnimations.Actors[0].Samples[0].AnimationStates.Count);
    }

    private static void ReplayThirdPersonPoseMigrationWorks()
    {
        DirectorReplayDocument migrated = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            SchemaVersion = 12,
            Actors = new List<DirectorReplayActorTrack>
            {
                new()
                {
                    ActorKey = "player:first-person",
                    PlayerUid = "first-person",
                    EntityCode = "player",
                    Samples = new List<DirectorReplayActorSample>
                    {
                        new()
                        {
                            TimeSeconds = 0d,
                            Yaw = 0f,
                            Pitch = MathF.PI,
                            BodyYaw = 0f,
                            HeadYaw = 0f,
                            HeadPitch = 0f,
                            HasPlayerState = true,
                            WalkYaw = 0f
                        },
                        new()
                        {
                            TimeSeconds = 0.5d,
                            Yaw = 1f,
                            Pitch = MathF.PI,
                            BodyYaw = 1f,
                            HeadYaw = 0f,
                            HeadPitch = 0f,
                            HasPlayerState = true,
                            WalkYaw = 0f
                        }
                    }
                }
            }
        });

        Check.Equal(DirectorReplayDocument.CurrentSchemaVersion, migrated.SchemaVersion);
        DirectorReplayActorSample migratedTurn = migrated.Actors[0].Samples[1];
        Check.True(migratedTurn.HasThirdPersonPose);
        Check.True(migratedTurn.ThirdPersonBodyYaw < 0.1f);
        Check.True(migratedTurn.ThirdPersonHeadYaw > 0.5f);
        Check.True(DirectorReplayMath.TrySample(
            migrated.Actors[0],
            0.25d,
            out DirectorReplayActorSample interpolated));
        Check.True(interpolated.HasThirdPersonPose);
        Check.True(interpolated.ThirdPersonHeadYaw > 0.2f);

        List<DirectorReplayActorSample> falselyExplicitFirstPersonSamples = new();
        for (int index = 0; index < 120; index++)
        {
            float yaw = index * 0.02f;
            bool transientBodyLag = index is >= 40 and <= 42;
            float bodyYaw = transientBodyLag ? yaw - 0.16f : yaw;
            falselyExplicitFirstPersonSamples.Add(new DirectorReplayActorSample
            {
                TimeSeconds = index / 60d,
                Yaw = yaw,
                Pitch = MathF.PI,
                BodyYaw = bodyYaw,
                HeadYaw = 0f,
                HasThirdPersonPose = true,
                ThirdPersonBodyYaw = bodyYaw,
                ThirdPersonHeadYaw = 0f,
                HasPlayerState = true,
                WalkYaw = 0f
            });
        }
        DirectorReplayDocument repairedFalsePositive = DirectorReplayMath.Normalize(
            new DirectorReplayDocument
            {
                SchemaVersion = 15,
                Actors = new List<DirectorReplayActorTrack>
                {
                    new()
                    {
                        PlayerUid = "flattened-first-person",
                        EntityCode = "player",
                        Samples = falselyExplicitFirstPersonSamples
                    }
                }
            });
        DirectorReplayActorSample repairedTurn = repairedFalsePositive.Actors[0].Samples[30];
        Check.True(repairedTurn.HasThirdPersonPose);
        Check.True(Math.Abs(repairedTurn.ThirdPersonHeadYaw) > 0.2f);
        Check.True(Math.Abs(repairedTurn.ThirdPersonBodyYaw - repairedTurn.Yaw) > 0.2f);

        DirectorReplayDocument repairedAgain = DirectorReplayMath.Normalize(repairedFalsePositive);
        Check.Equal(
            repairedFalsePositive.Actors[0].Samples.Count,
            repairedAgain.Actors[0].Samples.Count);
        for (int index = 0; index < repairedFalsePositive.Actors[0].Samples.Count; index++)
        {
            DirectorReplayActorSample firstPass = repairedFalsePositive.Actors[0].Samples[index];
            DirectorReplayActorSample secondPass = repairedAgain.Actors[0].Samples[index];
            Check.True(secondPass.HasThirdPersonPose);
            Check.NearlyEqual(firstPass.ThirdPersonBodyYaw, secondPass.ThirdPersonBodyYaw, 0.000001d);
            Check.NearlyEqual(firstPass.ThirdPersonHeadYaw, secondPass.ThirdPersonHeadYaw, 0.000001d);
            Check.NearlyEqual(firstPass.ThirdPersonHeadPitch, secondPass.ThirdPersonHeadPitch, 0.000001d);
        }

        foreach (int sampleRate in new[] { 10, 20, 60 })
        {
            List<DirectorReplayActorSample> resampledTransient = new();
            for (int index = 0; index <= sampleRate * 4; index++)
            {
                double time = index / (double)sampleRate;
                float yaw = (float)(time * 0.35d);
                bool transientBodyLag = time >= 1d && time <= 1.12d;
                float bodyYaw = transientBodyLag ? yaw - 0.16f : yaw;
                resampledTransient.Add(new DirectorReplayActorSample
                {
                    TimeSeconds = time,
                    Yaw = yaw,
                    Pitch = MathF.PI,
                    BodyYaw = bodyYaw,
                    HasThirdPersonPose = true,
                    ThirdPersonBodyYaw = bodyYaw,
                    HasPlayerState = true,
                    WalkYaw = 0f
                });
            }
            DirectorReplayDocument resampledRepair = DirectorReplayMath.Normalize(
                new DirectorReplayDocument
                {
                    SchemaVersion = 15,
                    SampleRate = sampleRate,
                    Actors = new List<DirectorReplayActorTrack>
                    {
                        new()
                        {
                            PlayerUid = $"flattened-{sampleRate}hz",
                            EntityCode = "player",
                            Samples = resampledTransient
                        }
                    }
                });
            DirectorReplayActorSample resampledTurn =
                resampledRepair.Actors[0].Samples[sampleRate * 2];
            Check.True(Math.Abs(resampledTurn.ThirdPersonHeadYaw) > 0.2f);
            Check.True(Math.Abs(
                resampledTurn.ThirdPersonBodyYaw
                - resampledTurn.Yaw) > 0.2f);
        }

        List<DirectorReplayActorSample> smallFirstPersonTurn = new();
        for (int index = 0; index <= 60; index++)
        {
            float yaw = index / 60f * 0.2f;
            float bodyYaw = index == 20 ? yaw - 0.16f : yaw;
            smallFirstPersonTurn.Add(new DirectorReplayActorSample
            {
                TimeSeconds = index / 30d,
                Yaw = yaw,
                Pitch = MathF.PI,
                BodyYaw = bodyYaw,
                HasThirdPersonPose = true,
                ThirdPersonBodyYaw = bodyYaw,
                HasPlayerState = true,
                WalkYaw = 0f
            });
        }
        DirectorReplayDocument repairedSmallTurn = DirectorReplayMath.Normalize(
            new DirectorReplayDocument
            {
                SchemaVersion = 15,
                SampleRate = 30d,
                Actors = new List<DirectorReplayActorTrack>
                {
                    new()
                    {
                        PlayerUid = "flattened-small-turn",
                        EntityCode = "player",
                        Samples = smallFirstPersonTurn
                    }
                }
            });
        Check.True(Math.Abs(
            repairedSmallTurn.Actors[0].Samples[^1].ThirdPersonHeadYaw) > 0.1f);

        List<DirectorReplayActorSample> quickThirdPersonTurn = new();
        for (int index = 0; index <= 240; index++)
        {
            float yaw = index / 240f * 0.5f;
            bool detachedTurn = index is >= 60 and <= 66;
            quickThirdPersonTurn.Add(new DirectorReplayActorSample
            {
                TimeSeconds = index / 60d,
                Yaw = yaw,
                BodyYaw = detachedTurn ? yaw - 0.3f : yaw,
                HeadYaw = detachedTurn ? 0.3f : 0f,
                HasThirdPersonPose = true,
                ThirdPersonBodyYaw = detachedTurn ? yaw - 0.3f : yaw,
                ThirdPersonHeadYaw = detachedTurn ? 0.3f : 0f,
                HasPlayerState = true,
                WalkYaw = yaw
            });
        }
        DirectorReplayDocument preservedQuickThirdPerson = DirectorReplayMath.Normalize(
            new DirectorReplayDocument
            {
                SchemaVersion = 15,
                SampleRate = 60d,
                Actors = new List<DirectorReplayActorTrack>
                {
                    new()
                    {
                        PlayerUid = "quick-third-person-turn",
                        EntityCode = "player",
                        Samples = quickThirdPersonTurn
                    }
                }
            });
        Check.NearlyEqual(
            quickThirdPersonTurn[63].ThirdPersonBodyYaw,
            preservedQuickThirdPerson.Actors[0].Samples[63].ThirdPersonBodyYaw,
            0.000001d);
        Check.NearlyEqual(
            0.3d,
            preservedQuickThirdPerson.Actors[0].Samples[63].ThirdPersonHeadYaw,
            0.000001d);

        List<DirectorReplayActorSample> schema16FirstPersonSamples = new();
        for (int index = 0; index < 30; index++)
        {
            float yaw = 0.6f + index * 0.03f;
            schema16FirstPersonSamples.Add(new DirectorReplayActorSample
            {
                TimeSeconds = index / 30d,
                Yaw = yaw,
                Pitch = MathF.PI,
                BodyYaw = yaw - 0.5f,
                HeadYaw = 0.35f,
                HasThirdPersonPose = false,
                HasPlayerState = true,
                WalkYaw = 0f
            });
        }
        DirectorReplayDocument explicitFirstPersonSource = DirectorReplayMath.Normalize(
            new DirectorReplayDocument
            {
                SchemaVersion = 16,
                Actors = new List<DirectorReplayActorTrack>
                {
                    new()
                    {
                        PlayerUid = "schema16-first-person",
                        EntityCode = "player",
                        Samples = schema16FirstPersonSamples
                    }
                }
            });
        DirectorReplayActorSample synthesizedFirstPose = explicitFirstPersonSource.Actors[0].Samples[0];
        Check.True(synthesizedFirstPose.HasThirdPersonPose);
        Check.NearlyEqual(0d, synthesizedFirstPose.ThirdPersonBodyYaw, 0.0001d);
        Check.True(Math.Abs(
            synthesizedFirstPose.ThirdPersonBodyYaw
            - schema16FirstPersonSamples[0].BodyYaw) > 0.05f);

        List<DirectorReplayActorSample> sustainedThirdPersonSamples = new();
        for (int index = 0; index < 120; index++)
        {
            float yaw = index * 0.02f;
            sustainedThirdPersonSamples.Add(new DirectorReplayActorSample
            {
                TimeSeconds = index / 60d,
                Yaw = yaw,
                BodyYaw = yaw - 0.3f,
                HeadYaw = 0.3f,
                HasThirdPersonPose = true,
                ThirdPersonBodyYaw = yaw - 0.3f,
                ThirdPersonHeadYaw = 0.3f,
                HasPlayerState = true,
                WalkYaw = yaw - 0.3f
            });
        }
        DirectorReplayDocument legacyThirdPerson = DirectorReplayMath.Normalize(
            new DirectorReplayDocument
            {
                SchemaVersion = 15,
                Actors = new List<DirectorReplayActorTrack>
                {
                    new()
                    {
                        PlayerUid = "legacy-third-person",
                        EntityCode = "player",
                        Samples = sustainedThirdPersonSamples
                    }
                }
            });
        for (int index = 0; index < sustainedThirdPersonSamples.Count; index++)
        {
            Check.NearlyEqual(
                sustainedThirdPersonSamples[index].ThirdPersonBodyYaw,
                legacyThirdPerson.Actors[0].Samples[index].ThirdPersonBodyYaw,
                0.000001d);
            Check.NearlyEqual(
                sustainedThirdPersonSamples[index].ThirdPersonHeadYaw,
                legacyThirdPerson.Actors[0].Samples[index].ThirdPersonHeadYaw,
                0.000001d);
        }

        DirectorReplayDocument recordedThirdPerson = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Actors = new List<DirectorReplayActorTrack>
            {
                new()
                {
                    PlayerUid = "third-person",
                    EntityCode = "player",
                    Samples = new List<DirectorReplayActorSample>
                    {
                        new()
                        {
                            TimeSeconds = 0d,
                            Yaw = 0.6f,
                            BodyYaw = 0.2f,
                            HeadYaw = 0.4f,
                            HeadPitch = -0.15f,
                            HasThirdPersonPose = true,
                            ThirdPersonBodyYaw = 0.2f,
                            ThirdPersonHeadYaw = 0.4f,
                            ThirdPersonHeadPitch = -0.15f,
                            HasPlayerState = true,
                            WalkYaw = 0.2f,
                            AnimationStates = new List<DirectorReplayAnimationSample>
                            {
                                new() { Code = "modpose", StateCode = "upper-body" },
                                new() { Code = "modpose", StateCode = "facial-layer" }
                            }
                        }
                    }
                }
            }
        });
        DirectorReplayActorSample preserved = recordedThirdPerson.Actors[0].Samples[0];
        Check.True(preserved.HasThirdPersonPose);
        Check.NearlyEqual(0.2d, preserved.ThirdPersonBodyYaw, 0.0001d);
        Check.NearlyEqual(0.4d, preserved.ThirdPersonHeadYaw, 0.0001d);
        Check.NearlyEqual(-0.15d, preserved.ThirdPersonHeadPitch, 0.0001d);
        Check.Equal(2, preserved.AnimationStates.Count);

        Check.Equal(
            0f,
            DirectorReplayPlaybackPolicy.ResolveAnimatorPoseDelta(
                hasExactRecordedFrames: true,
                replayDeltaSeconds: 0.05f));
        Check.Equal(
            0.05f,
            DirectorReplayPlaybackPolicy.ResolveAnimatorPoseDelta(
                hasExactRecordedFrames: false,
                replayDeltaSeconds: 0.05f));
        Check.Equal(
            0f,
            DirectorReplayPlaybackPolicy.ResolveAnimatorPoseDelta(
                hasExactRecordedFrames: false,
                replayDeltaSeconds: float.NaN));
    }

    private static void ReplayAnimationGapsCrossfadeWithoutStopping()
    {
        DirectorReplayDocument normalized = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Actors = new List<DirectorReplayActorTrack>
            {
                new()
                {
                    ActorKey = "entity:transition",
                    EntityCode = "bowtorn-surface",
                    Samples = new List<DirectorReplayActorSample>
                    {
                        new()
                        {
                            TimeSeconds = 0d,
                            Animations = new List<string> { "walk" },
                            AnimationStates = new List<DirectorReplayAnimationSample>
                            {
                                new()
                                {
                                    Code = "walk",
                                    StateCode = "walk",
                                    Progress = 0.5f,
                                    EasingFactor = 1f,
                                    BlendedWeight = 1f,
                                    Iterations = 1,
                                    IsActive = false,
                                    IsRunning = true,
                                    ShouldPlayTillEnd = true
                                }
                            }
                        },
                        new() { TimeSeconds = 0.05d },
                        new()
                        {
                            TimeSeconds = 0.1d,
                            Animations = new List<string> { "idle" },
                            AnimationStates = new List<DirectorReplayAnimationSample>
                            {
                                new()
                                {
                                    Code = "idle",
                                    StateCode = "idle",
                                    Progress = 0.1f,
                                    EasingFactor = 1f,
                                    BlendedWeight = 1f
                                }
                            }
                        }
                    }
                },
                new()
                {
                    ActorKey = "entity:spawn",
                    EntityCode = "drifter-normal",
                    Samples = new List<DirectorReplayActorSample>
                    {
                        new() { TimeSeconds = 0d },
                        new()
                        {
                            TimeSeconds = 0.05d,
                            Animations = new List<string> { "idle" },
                            AnimationStates = new List<DirectorReplayAnimationSample>
                            {
                                new()
                                {
                                    Code = "idle",
                                    StateCode = "idle",
                                    EasingFactor = 1f,
                                    BlendedWeight = 1f
                                }
                            }
                        }
                    }
                }
            }
        });

        DirectorReplayActorSample repaired = normalized.Actors[0].Samples[1];
        Check.Equal(2, repaired.AnimationStates.Count);
        Check.True(repaired.AnimationStates.Any(state => state.Code == "walk"));
        Check.True(repaired.AnimationStates.Any(state => state.Code == "idle"));
        Check.True(repaired.AnimationStates.All(state => state.EasingFactor > 0f));
        Check.True(repaired.AnimationStates.All(state => state.BlendedWeight > 0f));
        DirectorReplayAnimationSample repairedOneShot = repaired.AnimationStates.Single(
            state => state.Code == "walk");
        Check.False(repairedOneShot.IsActive);
        Check.True(repairedOneShot.IsRunning);
        Check.True(repairedOneShot.ShouldPlayTillEnd);
        Check.Equal(1, repairedOneShot.Iterations);
        Check.Equal(1, normalized.Actors[1].Samples[0].AnimationStates.Count);
        Check.Equal("idle", normalized.Actors[1].Samples[0].AnimationStates[0].Code);

        DirectorReplayActorTrack directTransition = new()
        {
            Samples = new List<DirectorReplayActorSample>
            {
                normalized.Actors[0].Samples[0].DeepClone(),
                normalized.Actors[0].Samples[2].DeepClone()
            }
        };
        directTransition.Samples[0].TimeSeconds = 0d;
        directTransition.Samples[1].TimeSeconds = 1d;
        Check.True(DirectorReplayMath.TrySample(
            directTransition,
            0.5d,
            out DirectorReplayActorSample transition));
        Check.Equal(2, transition.AnimationStates.Count);
        Check.True(transition.AnimationStates.Any(state => state.Code == "walk"));
        Check.True(transition.AnimationStates.Any(state => state.Code == "idle"));
        DirectorReplayAnimationSample transitioningOneShot = transition.AnimationStates.Single(
            state => state.Code == "walk");
        Check.False(transitioningOneShot.IsActive);
        Check.True(transitioningOneShot.IsRunning);
        Check.True(transitioningOneShot.ShouldPlayTillEnd);
    }

    private static void ActorPathPlaybackResolvesReplayTime()
    {
        CameraPath path = new()
        {
            SourceReplayId = Guid.NewGuid(),
            ReplayTimeOffsetSeconds = 2d
        };
        Check.NearlyEqual(3d, DirectorActorPathPlayback.ResolvePlaybackTime(
            path,
            path.SourceReplayId,
            replayTimeSeconds: 5d,
            localTimeSeconds: 1d));
        Check.NearlyEqual(1d, DirectorActorPathPlayback.ResolvePlaybackTime(
            path,
            Guid.NewGuid(),
            replayTimeSeconds: 5d,
            localTimeSeconds: 1d));
    }

    private static void ActorWorkflowPathsRestoreActorPose()
    {
        CameraPath path = new()
        {
            SourceActorKey = "player:lead",
            ActorYawConvention = ActorPathYawConvention.CameraFacing,
            ActorEyeHeightOffset = 1.62d
        };
        CameraKeyframe frame = new()
        {
            X = 10d,
            Y = 21.62d,
            Z = 30d,
            Yaw = 180f,
            Pitch = 30f,
            Roll = 10f
        };
        CameraKeyframe next = frame.DeepClone();
        next.X = 10.3d;
        next.Y = 21.72d;
        DirectorReplayActorSample applied = DirectorActorPathPlayback.Apply(
            new DirectorReplayActorSample
            {
                HasPlayerState = true,
                HasAgentState = true
            },
            path,
            frame,
            next,
            0.1d,
            playerActor: true);
        Check.NearlyEqual(10d, applied.X);
        Check.NearlyEqual(20d, applied.Y);
        Check.NearlyEqual(30d, applied.Z);
        Check.NearlyEqual(0d, applied.Yaw);
        Check.NearlyEqual(0d, applied.BodyYaw);
        Check.NearlyEqual(0d, applied.Pitch);
        Check.NearlyEqual(Math.PI / 6d, applied.HeadPitch);
        Check.True(applied.HasThirdPersonPose);
        Check.NearlyEqual(0d, applied.ThirdPersonBodyYaw);
        Check.NearlyEqual(0d, applied.ThirdPersonHeadYaw);
        Check.NearlyEqual(Math.PI / 6d, applied.ThirdPersonHeadPitch);
        Check.NearlyEqual(Math.PI / 18d, applied.Roll);
        Check.NearlyEqual(3d, applied.MotionX);
        Check.NearlyEqual(1d, applied.MotionY);
        Check.True(applied.HasMotion);

        path.ActorYawConvention = ActorPathYawConvention.ModelFacing;
        frame.Yaw = 90f;
        applied = DirectorActorPathPlayback.Apply(
            applied,
            path,
            frame,
            null,
            0d,
            playerActor: false);
        Check.NearlyEqual(Math.PI / 2d, applied.BodyYaw);
        Check.NearlyEqual(Math.PI / 6d, applied.Pitch);
        Check.False(applied.HasMotion);
    }

    private static void ActorPerformancesNormalizeCloneAndPersist()
    {
        Guid duplicateId = Guid.NewGuid();
        Guid duplicateCueId = Guid.NewGuid();
        DateTimeOffset localTime = new(2026, 6, 1, 12, 0, 0, TimeSpan.FromHours(8));
        DirectorActorPerformance source = new()
        {
            Id = duplicateId,
            Name = "  Scene Actor  ",
            Interpolation = (DirectorActorTransformInterpolation)999,
            SourceActorKey = "  player:a  ",
            SourcePlayerUid = "  a  ",
            SourceEntityCode = "  game:player  ",
            ReplayTimeOffsetSeconds = double.NaN,
            InitialState = new byte[] { 1, 2, 3 },
            CreatedUtc = localTime,
            ModifiedUtc = localTime.AddMinutes(-1),
            TransformKeyframes = new List<DirectorActorTransformKeyframe>
            {
                new() { TimeSeconds = 2d, X = 2d, Yaw = 0.5f },
                new()
                {
                    TimeSeconds = double.NaN,
                    X = double.PositiveInfinity,
                    Yaw = float.NaN,
                    InterpolationToNext = (DirectorActorTransformInterpolation)999
                },
                new() { TimeSeconds = 2d, X = 8d, Yaw = 1f }
            },
            Cues = new List<DirectorActorCue>
            {
                new()
                {
                    Id = duplicateCueId,
                    TimeSeconds = double.NaN,
                    Order = 5,
                    Type = DirectorActorCueType.Visibility,
                    Visible = false
                },
                new()
                {
                    Id = duplicateCueId,
                    TimeSeconds = 1d,
                    Type = DirectorActorCueType.AnimationSet,
                    Animations = new List<string> { " walk ", "WALK", string.Empty }
                },
                new() { TimeSeconds = 2d, Type = (DirectorActorCueType)999 },
                new() { TimeSeconds = 2d, Type = DirectorActorCueType.LookAt },
                new()
                {
                    TimeSeconds = 3d,
                    Type = DirectorActorCueType.AnimationSet,
                    Animations = new List<string>()
                }
            }
        };

        CameraLibrary library = new(new CameraLibraryDocument
        {
            SchemaVersion = 12,
            ActorPerformances = new List<DirectorActorPerformance>
            {
                source,
                new() { Id = duplicateId, Name = "scene actor" }
            }
        });

        Check.Equal(2, library.ActorPerformanceCount);
        Check.Equal(2, library.OrderedActorPerformances.Select(performance => performance.Id).Distinct().Count());
        DirectorActorPerformance normalized = Check.NotNull(library.FindActorPerformance("scene actor"));
        Check.Equal("Scene Actor", normalized.Name);
        Check.Equal(DirectorActorTransformInterpolation.Linear, normalized.Interpolation);
        Check.Equal("player:a", normalized.SourceActorKey);
        Check.Equal("a", normalized.SourcePlayerUid);
        Check.Equal("game:player", normalized.SourceEntityCode);
        Check.Equal(0d, normalized.ReplayTimeOffsetSeconds);
        Check.Equal(localTime.ToUniversalTime(), normalized.CreatedUtc);
        Check.Equal(normalized.CreatedUtc, normalized.ModifiedUtc);
        Check.SequenceEqual(new byte[] { 1, 2, 3 }, normalized.InitialState);
        Check.Equal(2, normalized.TransformKeyframes.Count);
        Check.Equal(0d, normalized.TransformKeyframes[0].TimeSeconds);
        Check.Equal(0d, normalized.TransformKeyframes[0].X);
        Check.Equal(0f, normalized.TransformKeyframes[0].Yaw);
        Check.Equal(8d, normalized.TransformKeyframes[1].X);
        Check.Equal(1f, normalized.TransformKeyframes[1].Yaw);
        Check.True(normalized.TransformKeyframes[0].InterpolationToNext is null);
        Check.Equal(3, normalized.Cues.Count);
        Check.SequenceEqual(
            new[]
            {
                DirectorActorCueType.Visibility,
                DirectorActorCueType.AnimationSet,
                DirectorActorCueType.AnimationStop
            },
            normalized.Cues.Select(cue => cue.Type));
        Check.SequenceEqual(new[] { 0, 1, 2 }, normalized.Cues.Select(cue => cue.Order));
        Check.Equal(3, normalized.Cues.Select(cue => cue.Id).Distinct().Count());
        Check.SequenceEqual(new[] { "walk" }, normalized.Cues[1].Animations);
        Check.Equal("scene actor (2)", Check.NotNull(library.FindActorPerformance("scene actor (2)")).Name);

        source.Name = "Changed outside";
        source.InitialState[0] = 9;
        source.TransformKeyframes[0].X = 99d;
        source.Cues[0].Visible = true;
        normalized.InitialState[1] = 9;
        normalized.TransformKeyframes[0].X = 88d;
        normalized.Cues[0].Visible = true;
        DirectorActorPerformance unchanged = Check.NotNull(library.FindActorPerformance("Scene Actor"));
        Check.SequenceEqual(new byte[] { 1, 2, 3 }, unchanged.InitialState);
        Check.Equal(0d, unchanged.TransformKeyframes[0].X);
        Check.False(unchanged.Cues[0].Visible);

        unchanged.Name = "Renamed Actor";
        unchanged.Cues[1].Animations.Add("run");
        Check.True(library.UpdateActorPerformance(unchanged));
        DirectorActorPerformance updated = Check.NotNull(library.FindActorPerformance("Renamed Actor"));
        Check.SequenceEqual(new[] { "run", "walk" }, updated.Cues[1].Animations);

        string root = Path.Combine(Path.GetTempPath(), "vsdirector-actor-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "cameras.json");
            CameraDocumentIO.Save(path, library.Document);
            CameraLibrary loaded = new(CameraDocumentIO.Load(path));
            DirectorActorPerformance persisted = Check.NotNull(
                loaded.FindActorPerformance("Renamed Actor"));
            Check.SequenceEqual(new[] { "run", "walk" }, persisted.Cues[1].Animations);
            Check.SequenceEqual(new byte[] { 1, 2, 3 }, persisted.InitialState);
            Check.Empty(Directory.GetFiles(root, ".cameras.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Guid duplicatePerformanceId = Check.NotNull(
            library.FindActorPerformance("scene actor (2)")).Id;
        Check.True(library.RemoveActorPerformance(duplicatePerformanceId));
        Check.Equal(1, library.ActorPerformanceCount);
    }

    private static void ActorPerformanceStateInterpolatesDeterministically()
    {
        float degreesToRadians = MathF.PI / 180f;
        DirectorActorPerformance performance = new()
        {
            Name = "Blocking",
            Interpolation = DirectorActorTransformInterpolation.EaseInOut,
            TransformKeyframes = new List<DirectorActorTransformKeyframe>
            {
                new()
                {
                    TimeSeconds = 0d,
                    X = 0d,
                    Yaw = 170f * degreesToRadians,
                    BodyYaw = 170f * degreesToRadians
                },
                new()
                {
                    TimeSeconds = 10d,
                    X = 10d,
                    Yaw = -170f * degreesToRadians,
                    BodyYaw = -170f * degreesToRadians
                },
                new() { TimeSeconds = 20d, X = 30d, Dimension = 1 }
            },
            Cues = new List<DirectorActorCue>
            {
                new()
                {
                    TimeSeconds = 0d,
                    Type = DirectorActorCueType.AnimationSet,
                    Animations = new List<string> { "idle" }
                },
                new() { TimeSeconds = 2d, Type = DirectorActorCueType.Visibility, Visible = false },
                new()
                {
                    TimeSeconds = 3d,
                    Type = DirectorActorCueType.LookAt,
                    LookAt = new DirectorActorLookAtTarget { X = 4d, Y = 5d, Z = 6d, Dimension = 0 }
                },
                new()
                {
                    TimeSeconds = 4d,
                    Type = DirectorActorCueType.AnimationSet,
                    Animations = new List<string> { "walk" }
                },
                new() { TimeSeconds = 6d, Type = DirectorActorCueType.AnimationStop },
                new() { TimeSeconds = 8d, Type = DirectorActorCueType.Visibility, Visible = true }
            }
        };

        DirectorActorPerformanceEvaluator evaluator = new(performance);
        DirectorActorPerformanceState beforeVisibility = evaluator.Sample(1.999d);
        Check.True(beforeVisibility.Visible);
        Check.SequenceEqual(new[] { "idle" }, beforeVisibility.Animations);
        Check.True(beforeVisibility.LookAt is null);

        DirectorActorPerformanceState eased = evaluator.Sample(2.5d);
        DirectorActorTransformKeyframe easedTransform = Check.NotNull(eased.Transform);
        Check.NearlyEqual(1.5625d, easedTransform.X);
        Check.False(eased.Visible);
        Check.NearlyEqual(173.125d * degreesToRadians, easedTransform.Yaw, 0.0001d);

        DirectorActorPerformanceState directed = evaluator.Sample(5d);
        DirectorActorTransformKeyframe directedTransform = Check.NotNull(directed.Transform);
        Check.NearlyEqual(5d, directedTransform.X);
        Check.NearlyEqual(Math.PI, Math.Abs(directedTransform.Yaw), 0.0001d);
        Check.SequenceEqual(new[] { "walk" }, directed.Animations);
        Check.False(directed.Visible);
        DirectorActorLookAtTarget directedLookAt = Check.NotNull(directed.LookAt);
        Check.Equal(4d, directedLookAt.X);
        directedLookAt.X = 99d;
        Check.Equal(4d, Check.NotNull(evaluator.Sample(5d).LookAt).X);

        DirectorActorPerformanceState stopped = evaluator.Sample(7d);
        Check.Empty(stopped.Animations);
        Check.False(stopped.Visible);
        Check.True(evaluator.Sample(8d).Visible);

        DirectorActorPerformanceState heldAcrossDimension = evaluator.Sample(15d);
        DirectorActorTransformKeyframe heldTransform = Check.NotNull(heldAcrossDimension.Transform);
        Check.Equal(0, heldTransform.Dimension);
        Check.NearlyEqual(10d, heldTransform.X);
        DirectorActorPerformanceState dimensionCut = evaluator.Sample(20d);
        DirectorActorTransformKeyframe cutTransform = Check.NotNull(dimensionCut.Transform);
        Check.Equal(1, cutTransform.Dimension);
        Check.NearlyEqual(30d, cutTransform.X);

        DirectorActorPerformance linearSource = performance.DeepClone();
        linearSource.TransformKeyframes[0].InterpolationToNext = DirectorActorTransformInterpolation.Linear;
        DirectorActorPerformanceEvaluator linearEvaluator = new(linearSource);
        Check.NearlyEqual(2.5d, Check.NotNull(linearEvaluator.Sample(2.5d).Transform).X);

        performance.TransformKeyframes[1].X = 1000d;
        performance.Cues[0].Animations[0] = "changed";
        Check.NearlyEqual(5d, Check.NotNull(evaluator.Sample(5d).Transform).X);
        Check.SequenceEqual(new[] { "idle" }, evaluator.Sample(1d).Animations);
    }

    private static void ActorPerformanceImportAndCueCursorWork()
    {
        Guid replayId = Guid.NewGuid();
        DirectorReplayActorTrack actor = new()
        {
            ActorKey = "player:lead",
            EntityId = 44,
            PlayerUid = "lead",
            EntityCode = "game:player",
            DisplayName = "Lead Actor",
            InitialState = new byte[] { 4, 5, 6 },
            Samples = new List<DirectorReplayActorSample>
            {
                new()
                {
                    TimeSeconds = 2d,
                    X = 0d,
                    Animations = new List<string> { " idle " }
                },
                new()
                {
                    TimeSeconds = 3d,
                    X = 2d,
                    Animations = new List<string> { "IDLE" }
                },
                new()
                {
                    TimeSeconds = 4d,
                    X = 4d,
                    Animations = new List<string> { "walk", "attack" }
                },
                new() { TimeSeconds = 5d, X = 6d, Animations = new List<string>() }
            }
        };

        Check.True(DirectorActorPerformanceImporter.TryCreate(
            actor,
            "Lead Blocking",
            out DirectorActorPerformance performance,
            out string error,
            replayId));
        Check.Equal(string.Empty, error);
        Check.Equal("Lead Blocking", performance.Name);
        Check.Equal(replayId, performance.SourceReplayId);
        Check.Equal("player:lead", performance.SourceActorKey);
        Check.Equal(44L, performance.SourceEntityId);
        Check.Equal("lead", performance.SourcePlayerUid);
        Check.Equal("game:player", performance.SourceEntityCode);
        Check.NearlyEqual(2d, performance.ReplayTimeOffsetSeconds);
        Check.SequenceEqual(new byte[] { 4, 5, 6 }, performance.InitialState);
        Check.Equal(4, performance.TransformKeyframes.Count);
        Check.SequenceEqual(new[] { 0d, 1d, 2d, 3d },
            performance.TransformKeyframes.Select(frame => frame.TimeSeconds));
        Check.Equal(3, performance.Cues.Count);
        Check.SequenceEqual(
            new[]
            {
                DirectorActorCueType.AnimationSet,
                DirectorActorCueType.AnimationSet,
                DirectorActorCueType.AnimationStop
            },
            performance.Cues.Select(cue => cue.Type));
        Check.SequenceEqual(new[] { "idle" }, performance.Cues[0].Animations);
        Check.SequenceEqual(new[] { "attack", "walk" }, performance.Cues[1].Animations);
        Check.SequenceEqual(new[] { 0d, 2d, 3d }, performance.Cues.Select(cue => cue.TimeSeconds));

        actor.InitialState[0] = 99;
        actor.Samples[0].Animations[0] = "changed";
        Check.SequenceEqual(new byte[] { 4, 5, 6 }, performance.InitialState);
        Check.SequenceEqual(new[] { "idle" }, performance.Cues[0].Animations);

        DirectorActorPerformanceEvaluator evaluator = new(performance);
        DirectorActorPerformanceState halfway = evaluator.Sample(1.5d);
        Check.NearlyEqual(3d, Check.NotNull(halfway.Transform).X);
        Check.SequenceEqual(new[] { "idle" }, halfway.Animations);

        DirectorActorPerformanceCursor cursor = evaluator.CreateCursor();
        DirectorActorPerformanceEvaluation atStart = cursor.Advance(0d);
        Check.Equal(1, atStart.CrossedCues.Count);
        Check.Equal(DirectorActorCueType.AnimationSet, atStart.CrossedCues[0].Type);
        Check.Empty(cursor.Advance(0d).CrossedCues);
        Check.Empty(cursor.Advance(1.5d).CrossedCues);
        DirectorActorPerformanceEvaluation atChange = cursor.Advance(2d);
        Check.Equal(1, atChange.CrossedCues.Count);
        Check.SequenceEqual(new[] { "attack", "walk" }, atChange.State.Animations);
        Check.Empty(cursor.Advance(2d).CrossedCues);

        DirectorActorPerformanceEvaluation seeked = cursor.Seek(3d);
        Check.Empty(seeked.CrossedCues);
        Check.Empty(seeked.State.Animations);
        Check.Empty(cursor.Seek(3d).CrossedCues);

        DirectorActorPerformanceEvaluation rewound = cursor.Advance(1d);
        Check.Empty(rewound.CrossedCues);
        Check.SequenceEqual(new[] { "idle" }, rewound.State.Animations);
        Check.Equal(1, cursor.Advance(2d).CrossedCues.Count);

        Check.Empty(cursor.Seek(1d).CrossedCues);
        DirectorActorPerformanceEvaluation crossedTwo = cursor.Advance(3d);
        Check.SequenceEqual(
            new[] { DirectorActorCueType.AnimationSet, DirectorActorCueType.AnimationStop },
            crossedTwo.CrossedCues.Select(cue => cue.Type));
        Check.Empty(crossedTwo.State.Animations);

        cursor.Reset();
        Check.False(cursor.IsInitialized);
        Check.Equal(3, cursor.Advance(3d).CrossedCues.Count);
        Check.True(cursor.IsInitialized);

        Check.False(DirectorActorPerformanceImporter.TryCreate(
            new DirectorReplayActorTrack(),
            "Empty",
            out _,
            out string emptyError));
        Check.Equal("actor-performance-no-samples", emptyError);
    }

    private static void ReplayTimelineEditsPersistAndSample()
    {
        Guid replayId = Guid.NewGuid();
        DirectorReplayDocument replay = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Id = replayId,
            Name = "Timeline Edit",
            DurationSeconds = 10d,
            Markers = new List<DirectorReplayMarker>
            {
                new() { TimeSeconds = 8d, Label = "  Finish  " },
                new() { TimeSeconds = -4d, Label = string.Empty }
            },
            SpeedKeyframes = new List<DirectorReplaySpeedKeyframe>
            {
                new()
                {
                    TimeSeconds = 2d,
                    Speed = 0.5d,
                    InterpolationToNext = DirectorReplaySpeedInterpolation.Linear
                },
                new()
                {
                    TimeSeconds = 6d,
                    Speed = 2.5d,
                    InterpolationToNext = DirectorReplaySpeedInterpolation.Hold
                }
            },
            IncludedPreloadColumns = new List<DirectorReplayPreloadColumn>
            {
                new(120, 130, 0)
            },
            ExcludedPreloadColumns = new List<DirectorReplayPreloadColumn>
            {
                new(121, 130, 0)
            }
        });

        Check.Equal(2, replay.Markers.Count);
        Check.Equal("Marker 2", replay.Markers[0].Label);
        Check.Equal("Finish", replay.Markers[1].Label);
        Check.NearlyEqual(1d, DirectorReplayTimeline.SampleSpeed(replay.SpeedKeyframes, 1d, 1d));
        Check.NearlyEqual(0.5d, DirectorReplayTimeline.SampleSpeed(replay.SpeedKeyframes, 2d, 1d));
        Check.NearlyEqual(1.5d, DirectorReplayTimeline.SampleSpeed(replay.SpeedKeyframes, 4d, 1d));
        Check.NearlyEqual(2.5d, DirectorReplayTimeline.SampleSpeed(replay.SpeedKeyframes, 8d, 1d));

        replay.SpeedKeyframes[0].InterpolationToNext = DirectorReplaySpeedInterpolation.Hold;
        Check.NearlyEqual(0.5d, DirectorReplayTimeline.SampleSpeed(replay.SpeedKeyframes, 4d, 1d));
        replay.SpeedKeyframes[0].InterpolationToNext = DirectorReplaySpeedInterpolation.EaseInOut;
        Check.NearlyEqual(0.8125d, DirectorReplayTimeline.SampleSpeed(replay.SpeedKeyframes, 3d, 1d));

        DirectorReplayDocument clone = replay.DeepClone();
        clone.Markers[0].Label = "Changed";
        clone.SpeedKeyframes[0].Speed = 4d;
        Check.Equal("Marker 2", replay.Markers[0].Label);
        Check.NearlyEqual(0.5d, replay.SpeedKeyframes[0].Speed);

        string root = Path.Combine(Path.GetTempPath(), "vsdirector-replay-timeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string replayPath = Path.Combine(root, "take.vsdreplay.gz");
            string sidecarPath = replayPath + ".timeline.json";
            DirectorReplayIO.Save(replayPath, replay);
            FileInfo before = new(replayPath);
            long sourceLength = before.Length;
            long sourceWriteTicks = before.LastWriteTimeUtc.Ticks;
            Check.True(DirectorReplayTimelineEditIO.Load(sidecarPath) is null);

            DirectorReplayTimelineEditIO.Save(sidecarPath, DirectorReplayTimelineEditIO.Create(replay));
            FileInfo after = new(replayPath);
            Check.Equal(sourceLength, after.Length);
            Check.Equal(sourceWriteTicks, after.LastWriteTimeUtc.Ticks);

            DirectorReplayDocument loaded = DirectorReplayIO.Load(replayPath);
            loaded.Markers.Clear();
            loaded.SpeedKeyframes.Clear();
            loaded.IncludedPreloadColumns.Clear();
            loaded.ExcludedPreloadColumns.Clear();
            DirectorReplayTimelineEditDocument edits = Check.NotNull(
                DirectorReplayTimelineEditIO.Load(sidecarPath));
            DirectorReplayTimelineEditIO.Apply(loaded, edits);
            Check.Equal(replayId, edits.ReplayId);
            Check.Equal(2, loaded.Markers.Count);
            Check.Equal("Finish", loaded.Markers[1].Label);
            Check.Equal(2, loaded.SpeedKeyframes.Count);
            Check.Equal(DirectorReplaySpeedInterpolation.EaseInOut,
                loaded.SpeedKeyframes[0].InterpolationToNext);
            Check.SequenceEqual(
                new[] { new DirectorReplayPreloadColumn(120, 130, 0) },
                loaded.IncludedPreloadColumns);
            Check.SequenceEqual(
                new[] { new DirectorReplayPreloadColumn(121, 130, 0) },
                loaded.ExcludedPreloadColumns);
            Check.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReplayViewportClipsNormalizeCloneAndPersist()
    {
        Guid replayId = Guid.NewGuid();
        Guid cameraId = Guid.NewGuid();
        Guid duplicateId = Guid.NewGuid();
        DirectorReplayDocument replay = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Id = replayId,
            DurationSeconds = 10d,
            ViewportTimeline = new DirectorReplayViewportTimeline
            {
                SelectedClipId = duplicateId,
                DurationSeconds = 999d,
                ElementTracks = new List<DirectorReplayElementTrackType>
                {
                    DirectorReplayElementTrackType.Marker,
                    DirectorReplayElementTrackType.Camera,
                    DirectorReplayElementTrackType.Marker
                },
                Clips = new List<DirectorReplayViewportClip>
                {
                    new()
                    {
                        Id = duplicateId,
                        Name = "  Establishing  ",
                        SourceType = DirectorReplayViewportSourceType.StaticCamera,
                        SourceId = cameraId,
                        CameraPose = new CameraPreset
                        {
                            Id = Guid.NewGuid(),
                            Name = "Embedded pose",
                            X = 12.5d,
                            Y = 90d,
                            Z = -4d,
                            Yaw = 45f,
                            Pitch = -12f,
                            Roll = 3f,
                            Fov = 82f
                        },
                        CameraInterpolationToNext = CameraPathInterpolation.Step,
                        TimelineStartSeconds = double.NaN,
                        DurationSeconds = double.PositiveInfinity,
                        SourceStartSeconds = -4d,
                        PlaybackRate = 0d,
                        TransitionIn = new DirectorVideoTransition
                        {
                            Type = DirectorVideoTransitionType.CrossDissolve,
                            DurationSeconds = 99d
                        }
                    },
                    new()
                    {
                        Id = duplicateId,
                        Name = " ",
                        SourceType = DirectorReplayViewportSourceType.Path,
                        SourceId = Guid.NewGuid(),
                        TimelineStartSeconds = 9d,
                        DurationSeconds = 5d,
                        SourceStartSeconds = 2d,
                        PlaybackRate = 2d,
                        Loop = true
                    },
                    new()
                    {
                        SourceType = (DirectorReplayViewportSourceType)999,
                        DurationSeconds = 1d
                    }
                }
            }
        });

        Check.Equal(2, replay.ViewportTimeline.Clips.Count);
        Check.Equal(10d, replay.ViewportTimeline.DurationSeconds);
        Check.Equal(2, replay.ViewportTimeline.Clips.Select(clip => clip.Id).Distinct().Count());
        DirectorReplayViewportClip first = replay.ViewportTimeline.Clips[0];
        DirectorReplayViewportClip second = replay.ViewportTimeline.Clips[1];
        Check.Equal("Establishing", first.Name);
        Check.Equal(cameraId, first.SourceId);
        Check.NearlyEqual(12.5d, Check.NotNull(first.CameraPose).X);
        Check.Equal(Guid.Empty, Check.NotNull(first.CameraPose).Id);
        Check.Equal(CameraPathInterpolation.Step, first.CameraInterpolationToNext);
        Check.SequenceEqual(
            new[]
            {
                DirectorReplayElementTrackType.Camera
            },
            replay.ViewportTimeline.ElementTracks);
        Check.True(first.TrackId != Guid.Empty);
        Check.Equal(DirectorReplayElementTrackType.Camera,
            replay.ViewportTimeline.Tracks.Single().TrackType);
        Check.Equal(0, first.TrackOrder);
        Check.NearlyEqual(0d, first.TimelineStartSeconds);
        Check.NearlyEqual(5d, first.DurationSeconds);
        Check.NearlyEqual(0d, first.SourceStartSeconds);
        Check.NearlyEqual(0.05d, first.PlaybackRate);
        Check.NearlyEqual(5d, Check.NotNull(first.TransitionIn).DurationSeconds);
        Check.Equal("Path", second.Name);
        Check.Equal(1, second.TrackOrder);
        Check.NearlyEqual(9d, second.TimelineStartSeconds);
        Check.NearlyEqual(1d, second.DurationSeconds);
        Check.True(second.Loop);
        Check.Equal(first.Id, replay.ViewportTimeline.SelectedClipId);

        DirectorReplayDocument clone = replay.DeepClone();
        clone.ViewportTimeline.Clips[0].Name = "Changed";
        Check.NotNull(clone.ViewportTimeline.Clips[0].CameraPose).X = 999d;
        Check.NotNull(clone.ViewportTimeline.Clips[0].TransitionIn).DurationSeconds = 0.25d;
        Check.Equal("Establishing", replay.ViewportTimeline.Clips[0].Name);
        Check.NearlyEqual(12.5d, Check.NotNull(replay.ViewportTimeline.Clips[0].CameraPose).X);
        Check.NearlyEqual(5d, Check.NotNull(replay.ViewportTimeline.Clips[0].TransitionIn).DurationSeconds);

        Guid lowerTime = Guid.NewGuid();
        Guid upperTime = Guid.NewGuid();
        DirectorReplayViewportTimeline orderedTracks = DirectorReplayViewportTimeline.Normalize(
            new DirectorReplayViewportTimeline
            {
                Clips = new List<DirectorReplayViewportClip>
                {
                    new()
                    {
                        Id = lowerTime,
                        Name = "Lower time",
                        SourceType = DirectorReplayViewportSourceType.Path,
                        TimelineStartSeconds = 0d,
                        DurationSeconds = 2d,
                        TrackOrder = 1
                    },
                    new()
                    {
                        Id = upperTime,
                        Name = "Upper time",
                        SourceType = DirectorReplayViewportSourceType.StaticCamera,
                        TimelineStartSeconds = 8d,
                        DurationSeconds = 2d,
                        TrackOrder = 0
                    }
                }
            },
            10d);
        Check.Equal(upperTime, orderedTracks.Clips[0].Id);
        Check.Equal(lowerTime, orderedTracks.Clips[1].Id);
        Check.Equal(0, orderedTracks.Clips[0].TrackOrder);
        Check.Equal(1, orderedTracks.Clips[1].TrackOrder);

        string root = Path.Combine(
            Path.GetTempPath(),
            "vsdirector-replay-viewports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "take.timeline.json");
            DirectorReplayTimelineEditIO.Save(path, DirectorReplayTimelineEditIO.Create(replay));
            DirectorReplayTimelineEditDocument edits = Check.NotNull(
                DirectorReplayTimelineEditIO.Load(path));
            Check.Equal(DirectorReplayTimelineEditDocument.CurrentSchemaVersion, edits.SchemaVersion);
            Check.Equal(2, edits.ViewportTimeline.Clips.Count);
            Check.NearlyEqual(12.5d,
                Check.NotNull(edits.ViewportTimeline.Clips[0].CameraPose).X);
            Check.True(edits.ViewportTimeline.ElementTracks.Contains(
                DirectorReplayElementTrackType.Camera));

            DirectorReplayDocument target = new()
            {
                Id = replayId,
                DurationSeconds = 10d
            };
            DirectorReplayTimelineEditIO.Apply(target, edits);
            Check.Equal(2, target.ViewportTimeline.Clips.Count);
            Check.Equal("Establishing", target.ViewportTimeline.Clips[0].Name);
            Check.NearlyEqual(2d, target.ViewportTimeline.Clips[1].PlaybackRate);
            Check.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        DirectorReplayElementTrack firstFovTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Fov
        };
        DirectorReplayElementTrack secondFovTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Fov
        };
        DirectorReplayViewportTimeline duplicateTracks = new()
        {
            Tracks = new List<DirectorReplayElementTrack> { firstFovTrack, secondFovTrack },
            ElementKeyframes = new List<DirectorReplayElementKeyframe>
            {
                new()
                {
                    TrackId = firstFovTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Fov,
                    Fov = 60f
                },
                new()
                {
                    TrackId = secondFovTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Fov,
                    Fov = 90f
                }
            }
        };
        Check.True(DirectorReplayElementTimeline.TrySample(
            duplicateTracks,
            DirectorReplayElementTrackType.Fov,
            0d,
            out DirectorReplayElementKeyframe topFov));
        Check.Equal(60f, topFov.Fov);
        secondFovTrack.Enabled = false;
        Check.True(DirectorReplayElementTimeline.TrySample(
            duplicateTracks,
            DirectorReplayElementTrackType.Fov,
            0d,
            out DirectorReplayElementKeyframe visibleFov));
        Check.Equal(60f, visibleFov.Fov);

        Check.True(DirectorReplayViewportTimeline.RemoveTrack(
            duplicateTracks,
            firstFovTrack.Id));
        Check.Equal(1, duplicateTracks.Tracks.Count);
        Check.Equal(1, duplicateTracks.ElementKeyframes.Count);
        Check.SequenceEqual(
            new[] { DirectorReplayElementTrackType.Fov },
            duplicateTracks.ElementTracks);
        Check.SequenceEqual(
            new[] { DirectorReplayElementTrackType.Fov },
            duplicateTracks.DisabledElementTracks);
        Check.True(DirectorReplayViewportTimeline.RemoveTrack(
            duplicateTracks,
            secondFovTrack.Id));
        Check.Empty(duplicateTracks.Tracks);
        Check.Empty(duplicateTracks.ElementKeyframes);
        Check.Empty(duplicateTracks.ElementTracks);
        Check.Empty(duplicateTracks.DisabledElementTracks);
        Check.Empty(DirectorReplayViewportTimeline.Normalize(
            duplicateTracks,
            5d).Tracks);

        DirectorReplayElementTrack sceneCamera = new()
        {
            TrackType = DirectorReplayElementTrackType.Camera,
            Name = "Camera"
        };
        DirectorReplayElementTrack sceneFov = new()
        {
            TrackType = DirectorReplayElementTrackType.Fov,
            Name = "FOV"
        };
        DirectorReplayViewportTimeline scenes = DirectorReplayViewportTimeline.Normalize(
            new DirectorReplayViewportTimeline
            {
                Tracks = new List<DirectorReplayElementTrack> { sceneCamera, sceneFov }
            },
            10d);
        Check.Equal(1, scenes.Scenes.Count);
        Guid firstSceneId = scenes.ActiveSceneId;
        Check.True(firstSceneId != Guid.Empty);
        Check.Equal(2, scenes.Tracks.Count);
        Check.True(DirectorReplayViewportTimeline.MoveTrack(scenes, sceneFov.Id, 0));
        Check.Equal(sceneFov.Id, scenes.Tracks[0].Id);
        Check.Equal(sceneFov.Id, Check.NotNull(scenes.ActiveScene).Tracks[0].Id);
        scenes.ExportStartSeconds = 1.25d;
        scenes.ExportEndSeconds = 8.75d;
        scenes.SyncActiveSceneFromTimeline();

        DirectorReplayElementScene alternate = scenes.AddScene("Alternate");
        Check.Equal(alternate.Id, scenes.ActiveSceneId);
        Check.Empty(scenes.Tracks);
        DirectorReplayElementTrack alternateVoice = new()
        {
            TrackType = DirectorReplayElementTrackType.VoiceAudio,
            SpeakerId = "speaker-a",
            Name = "Speaker A"
        };
        scenes.Tracks.Add(alternateVoice);
        scenes.SyncActiveSceneFromTimeline();
        Check.True(scenes.ActivateScene(firstSceneId));
        Check.Equal(2, scenes.Tracks.Count);
        Check.Equal(sceneFov.Id, scenes.Tracks[0].Id);
        Check.NearlyEqual(1.25d, scenes.ExportStartSeconds ?? -1d);
        Check.NearlyEqual(8.75d, scenes.ExportEndSeconds ?? -1d);
        Check.True(scenes.ActivateScene(alternate.Id));
        Check.Equal(1, scenes.Tracks.Count);
        Check.Equal(alternateVoice.Id, scenes.Tracks[0].Id);
        Check.True(scenes.ExportStartSeconds is null);
        Check.True(scenes.ExportEndSeconds is null);

        DirectorReplayViewportTimeline normalizedScenes = DirectorReplayViewportTimeline.Normalize(
            scenes,
            10d);
        Check.Equal(2, normalizedScenes.Scenes.Count);
        Check.Equal(alternate.Id, normalizedScenes.ActiveSceneId);
        Check.Equal(alternateVoice.Id, normalizedScenes.Tracks.Single().Id);
        Check.True(normalizedScenes.ActivateScene(firstSceneId));
        Check.Equal(sceneFov.Id, normalizedScenes.Tracks[0].Id);
        Check.NearlyEqual(1.25d, normalizedScenes.ExportStartSeconds ?? -1d);
        Check.NearlyEqual(8.75d, normalizedScenes.ExportEndSeconds ?? -1d);
        Check.True(normalizedScenes.RemoveScene(firstSceneId));
        Check.Equal(1, normalizedScenes.Scenes.Count);
        Check.False(normalizedScenes.RemoveScene(normalizedScenes.ActiveSceneId));
    }

    private static void ReplayVisualSettingsWork()
    {
        DirectorReplayVisualSettings custom = DirectorReplayVisualSettings.Normalize(new());
        Check.False(custom.Camera.CameraCollision);
        Check.False(custom.Camera.OverrideNearClip);
        Check.True(custom.Audio.PreservePitchWithReplaySpeed);
        Check.Equal(
            44,
            typeof(DirectorReplayWorldVisuals).GetProperties().Length
            + typeof(DirectorReplaySkyVisuals).GetProperties().Length
            + typeof(DirectorReplayEnvironmentVisuals).GetProperties().Length
            + typeof(DirectorReplayCameraVisuals).GetProperties().Length
            + typeof(DirectorReplayParticleVisuals).GetProperties().Length
            + typeof(DirectorReplayAudioVisuals).GetProperties().Length
            + typeof(DirectorReplayCompositionVisuals).GetProperties().Length);

        DirectorReplayVisualSettings normalized = DirectorReplayVisualSettings.Normalize(new()
        {
            Sky = new DirectorReplaySkyVisuals
            {
                Mode = (DirectorReplaySkyMode)999,
                BackgroundColor = "bad",
                CloudDensity = float.PositiveInfinity
            },
            Environment = new DirectorReplayEnvironmentVisuals
            {
                TimeOfDayTick = -1,
                Weather = (DirectorReplayWeatherMode)999,
                FogColor = "#abc",
                FogDensity = float.PositiveInfinity
            },
            Camera = new DirectorReplayCameraVisuals
            {
                Fov = 999f,
                Roll = -999f,
                NearClip = 0f
            },
            Audio = new DirectorReplayAudioVisuals
            {
                MasterVolume = 99f
            }
        });

        Check.Equal(DirectorReplaySkyMode.Recorded, normalized.Sky.Mode);
        Check.Equal("#000000", normalized.Sky.BackgroundColor);
        Check.NearlyEqual(1d, normalized.Sky.CloudDensity);
        Check.Equal(0, normalized.Environment.TimeOfDayTick);
        Check.Equal(DirectorReplayWeatherMode.Recorded, normalized.Environment.Weather);
        Check.Equal("#C8D8E8", normalized.Environment.FogColor);
        Check.NearlyEqual(0d, normalized.Environment.FogDensity);
        Check.NearlyEqual(
            0.02d,
            DirectorReplayVisualSettings.Normalize(new()
            {
                Environment = new DirectorReplayEnvironmentVisuals { FogDensity = 1f }
            }).Environment.FogDensity);
        Check.NearlyEqual(170d, normalized.Camera.Fov);
        Check.NearlyEqual(-180d, normalized.Camera.Roll);
        Check.NearlyEqual(0.001d, normalized.Camera.NearClip);
        Check.NearlyEqual(2d, normalized.Audio.MasterVolume);

        normalized.World.ShowTerrain = false;
        normalized.Sky.BackgroundColor = "#102030";
        DirectorReplayVisualSettings clone = normalized.DeepClone();
        clone.World.ShowTerrain = true;
        clone.Sky.BackgroundColor = "#405060";
        clone.Environment.FogColor = "#112233";
        Check.False(normalized.World.ShowTerrain);
        Check.Equal("#102030", normalized.Sky.BackgroundColor);
        Check.Equal("#C8D8E8", normalized.Environment.FogColor);

        DirectorReplayViewportTimeline timeline = DirectorReplayViewportTimeline.Normalize(
            new DirectorReplayViewportTimeline(),
            12d);
        Guid firstSceneId = timeline.ActiveSceneId;
        timeline.Visuals = DirectorReplayVisualSettings.Normalize(timeline.Visuals);
        timeline.Visuals.Camera.OverrideFov = true;
        timeline.Visuals.Camera.Fov = 52f;
        timeline.Visuals.Sky.Mode = DirectorReplaySkyMode.GreenScreen;
        timeline.SyncActiveSceneFromTimeline();
        DirectorReplayElementScene second = timeline.AddScene("Night");
        timeline.Visuals = DirectorReplayVisualSettings.Normalize(timeline.Visuals);
        timeline.Visuals.Camera.OverrideFov = true;
        timeline.Visuals.Camera.Fov = 92f;
        timeline.Visuals.Sky.Mode = DirectorReplaySkyMode.SolidColor;
        timeline.Visuals.Sky.BackgroundColor = "#101820";
        timeline.SyncActiveSceneFromTimeline();
        Check.True(timeline.ActivateScene(firstSceneId));
        Check.NearlyEqual(52d, timeline.Visuals.Camera.Fov);
        Check.Equal(DirectorReplaySkyMode.GreenScreen, timeline.Visuals.Sky.Mode);
        Check.True(timeline.ActivateScene(second.Id));
        Check.NearlyEqual(92d, timeline.Visuals.Camera.Fov);
        Check.Equal("#101820", timeline.Visuals.Sky.BackgroundColor);

        DirectorReplayDocument migrated = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            SchemaVersion = 19,
            DurationSeconds = 12d,
            ViewportTimeline = timeline
        });
        Check.Equal(DirectorReplayDocument.CurrentSchemaVersion, migrated.SchemaVersion);
        Check.Equal(22, migrated.SchemaVersion);
        Check.NearlyEqual(92d, migrated.ViewportTimeline.Visuals.Camera.Fov);
        Check.Equal("#101820", migrated.ViewportTimeline.Visuals.Sky.BackgroundColor);

        string root = Path.Combine(
            Path.GetTempPath(),
            "vsdirector-replay-visuals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sidecar = Path.Combine(root, "take.timeline.json");
            DirectorReplayTimelineEditIO.Save(
                sidecar,
                DirectorReplayTimelineEditIO.Create(migrated));
            string serializedVisuals = File.ReadAllText(sidecar);
            foreach (string removedKey in new[]
                     {
                         "PostProcessing", "Liquids", "Animations", "Advanced",
                         "replayVisualPreset", "UseRecordedVisuals", "AspectMode", "MasterPitch",
                         "EntityCodeFilter"
                     })
            {
                Check.False(
                    serializedVisuals.Contains(removedKey, StringComparison.Ordinal),
                    $"Serialized replay visuals still contain removed key {removedKey}.");
            }
            DirectorReplayTimelineEditDocument edits = Check.NotNull(
                DirectorReplayTimelineEditIO.Load(sidecar));
            Check.NearlyEqual(92d, edits.ViewportTimeline.Visuals.Camera.Fov);
            Check.Equal("#101820", edits.ViewportTimeline.Visuals.Sky.BackgroundColor);
            Check.Equal(2, edits.ViewportTimeline.Scenes.Count);

            DirectorReplayDocument target = new()
            {
                Id = migrated.Id,
                DurationSeconds = 12d
            };
            DirectorReplayTimelineEditIO.Apply(target, edits);
            Check.NearlyEqual(92d, target.ViewportTimeline.Visuals.Camera.Fov);
            Check.Equal("#101820", target.ViewportTimeline.Visuals.Sky.BackgroundColor);
            Check.True(target.ViewportTimeline.ActivateScene(firstSceneId));
            Check.NearlyEqual(52d, target.ViewportTimeline.Visuals.Camera.Fov);
            Check.Equal(DirectorReplaySkyMode.GreenScreen, target.ViewportTimeline.Visuals.Sky.Mode);
            Check.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReplayEntityFollowIntervalsWork()
    {
        Check.Equal(5, (int)DirectorReplayElementTrackType.CameraShake);
        Check.Equal(12, (int)DirectorReplayElementTrackType.VoiceAudio);
        Check.Equal(13, (int)DirectorReplayElementTrackType.FollowEntity);

        Guid trackId = Guid.NewGuid();
        DirectorReplayViewportTimeline timeline = DirectorReplayViewportTimeline.Normalize(
            new DirectorReplayViewportTimeline
            {
                Tracks = new List<DirectorReplayElementTrack>
                {
                    new() { Id = trackId, TrackType = DirectorReplayElementTrackType.FollowEntity }
                },
                ElementKeyframes = new List<DirectorReplayElementKeyframe>
                {
                    new()
                    {
                        TrackId = trackId,
                        TrackType = DirectorReplayElementTrackType.FollowEntity,
                        TimeSeconds = 1d,
                        FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.Start,
                        EntityActorKey = " actor:1 ",
                        FollowOpenClearView = true,
                        OpenClearView = true,
                        FollowDistance = double.PositiveInfinity,
                        FollowHeight = double.NegativeInfinity,
                        FollowOrbitSpeedDegreesPerSecond = 9999d,
                        FollowSmoothTimeSeconds = 0d,
                        FollowDelaySeconds = 999d,
                        FollowFov = 999f
                    },
                    new()
                    {
                        TrackId = trackId,
                        TrackType = DirectorReplayElementTrackType.FollowEntity,
                        TimeSeconds = 3d,
                        FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.End
                    },
                    new()
                    {
                        TrackId = trackId,
                        TrackType = DirectorReplayElementTrackType.FollowEntity,
                        TimeSeconds = 4d,
                        FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.Start,
                        EntityActorKey = "actor:2"
                    },
                    new()
                    {
                        TrackId = trackId,
                        TrackType = DirectorReplayElementTrackType.FollowEntity,
                        TimeSeconds = 6d,
                        FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.End
                    },
                    new()
                    {
                        TrackType = DirectorReplayElementTrackType.TrackEntity,
                        TimeSeconds = 0d,
                        EntityActorKey = "actor:tracked",
                        OpenClearView = true,
                        FollowOpenClearView = true
                    }
                }
            },
            8d);

        DirectorReplayElementKeyframe normalizedStart = timeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.FollowEntity
            && item.FollowEntityRole == DirectorReplayFollowEntityKeyframeRole.Start);
        Check.Equal("actor:1", normalizedStart.EntityActorKey);
        Check.True(normalizedStart.FollowOpenClearView);
        Check.False(normalizedStart.OpenClearView);
        Check.Equal(CameraFollowMath.DefaultDistance, normalizedStart.FollowDistance);
        Check.Equal(CameraFollowMath.DefaultHeight, normalizedStart.FollowHeight);
        Check.Equal(CameraFollowMath.MaxOrbitSpeed, normalizedStart.FollowOrbitSpeedDegreesPerSecond);
        Check.Equal(CameraFollowMath.MinSmoothTimeSeconds, normalizedStart.FollowSmoothTimeSeconds);
        Check.Equal(CameraFollowMath.MaxDelaySeconds, normalizedStart.FollowDelaySeconds);
        Check.Equal(170f, normalizedStart.FollowFov);
        DirectorReplayElementKeyframe tracked = timeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.TrackEntity);
        Check.True(tracked.OpenClearView);
        Check.False(tracked.FollowOpenClearView);

        Check.False(DirectorReplayElementTimeline.TrySampleFollowEntityInterval(
            timeline, trackId, 0.999d, out _, out _));
        Check.True(DirectorReplayElementTimeline.TrySampleFollowEntityInterval(
            timeline, trackId, 1d, out DirectorReplayElementKeyframe first, out DirectorReplayElementKeyframe firstEnd));
        Check.Equal("actor:1", first.EntityActorKey);
        Check.NearlyEqual(3d, firstEnd.TimeSeconds);
        Check.True(DirectorReplayElementTimeline.TrySampleFollowEntityInterval(
            timeline, trackId, 2.999d, out _, out _));
        Check.False(DirectorReplayElementTimeline.TrySampleFollowEntityInterval(
            timeline, trackId, 3d, out _, out _));
        Check.False(DirectorReplayElementTimeline.TrySampleFollowEntityInterval(
            timeline, trackId, 3.5d, out _, out _));
        Check.True(DirectorReplayElementTimeline.TrySampleFollowEntityInterval(
            timeline, trackId, 4d, out DirectorReplayElementKeyframe second, out _));
        Check.Equal("actor:2", second.EntityActorKey);
    }

    private static void ReplayCameraElementMathWorks()
    {
        DirectorReplayElementKeyframe orbit = new()
        {
            X = 10d,
            Y = 20d,
            Z = 30d,
            Distance = 8d,
            Yaw = 0f,
            Pitch = 0f,
            Dimension = 3
        };
        CameraPreset orbitPose = DirectorReplayCameraElementMath.EvaluateOrbit(orbit, 72f);
        Check.NearlyEqual(10d, orbitPose.X);
        Check.NearlyEqual(20d, orbitPose.Y);
        Check.NearlyEqual(38d, orbitPose.Z);
        Check.Equal(3, orbitPose.Dimension);
        Check.NearlyEqual(0d, orbitPose.Yaw);
        Check.NearlyEqual(0d, orbitPose.Pitch);

        orbit.Yaw = 90f;
        orbitPose = DirectorReplayCameraElementMath.EvaluateOrbit(orbit, 72f);
        Check.NearlyEqual(18d, orbitPose.X);
        Check.NearlyEqual(20d, orbitPose.Y);
        Check.NearlyEqual(30d, orbitPose.Z);
        Check.NearlyEqual(90d, orbitPose.Yaw);

        DirectorReplayTrackedTargetPose target = new(
            100d,
            40d,
            -20d,
            7,
            0d,
            0d);
        DirectorReplayElementKeyframe tracking = new()
        {
            ViewOffsetX = 1d,
            ViewOffsetY = 2d,
            ViewOffsetZ = 3d
        };
        CameraPreset trackedPose = DirectorReplayCameraElementMath.EvaluateTrack(
            tracking,
            target,
            80f);
        Check.NearlyEqual(101d, trackedPose.X);
        Check.NearlyEqual(42d, trackedPose.Y);
        Check.NearlyEqual(-17d, trackedPose.Z);
        Check.NearlyEqual(0d, trackedPose.Yaw);
        Check.NearlyEqual(0d, trackedPose.Pitch);
        Check.Equal(7, trackedPose.Dimension);

        target = target with { YawDegrees = 0d, PitchDegrees = 30d };
        tracking = new DirectorReplayElementKeyframe
        {
            ViewOffsetZ = 3d,
            Yaw = 90f,
            Pitch = 4f
        };
        trackedPose = DirectorReplayCameraElementMath.EvaluateTrack(tracking, target, 80f);
        // Target pitch and the user pitch adjustment compose additively.
        Check.NearlyEqual(90d, trackedPose.Yaw);
        Check.NearlyEqual(34d, trackedPose.Pitch);
        Check.NearlyEqual(100d + 3d * Math.Cos(34d * Math.PI / 180d), trackedPose.X);
        Check.NearlyEqual(-20d, trackedPose.Z);

        CameraPreset captured = new()
        {
            X = 106.5d,
            Y = 43.25d,
            Z = -24.5d,
            Yaw = 131f,
            Pitch = 11f,
            Roll = 6f
        };
        DirectorReplayElementKeyframe configured = new();
        DirectorReplayCameraElementMath.ConfigureTrackFromCamera(
            configured,
            target,
            captured);
        Check.True(Math.Abs(configured.ViewOffsetX) > 0.000001d);
        Check.True(Math.Abs(configured.ViewOffsetY) > 0.000001d);
        Check.True(Math.Abs(configured.ViewOffsetZ) > 0.000001d);
        CameraPreset reconstructed = DirectorReplayCameraElementMath.EvaluateTrack(
            configured,
            target,
            80f);
        Check.NearlyEqual(captured.X, reconstructed.X);
        Check.NearlyEqual(captured.Y, reconstructed.Y);
        Check.NearlyEqual(captured.Z, reconstructed.Z);
        Check.NearlyEqual(captured.Yaw, reconstructed.Yaw);
        Check.NearlyEqual(captured.Pitch, reconstructed.Pitch);
        Check.NearlyEqual(captured.Roll, reconstructed.Roll);

        Guid orbitTrackId = Guid.NewGuid();
        DirectorReplayViewportTimeline timeline = new()
        {
            Tracks = new List<DirectorReplayElementTrack>
            {
                new()
                {
                    Id = orbitTrackId,
                    TrackType = DirectorReplayElementTrackType.CameraOrbit
                }
            },
            ElementKeyframes = new List<DirectorReplayElementKeyframe>
            {
                new()
                {
                    TrackId = orbitTrackId,
                    TrackType = DirectorReplayElementTrackType.CameraOrbit,
                    TimeSeconds = 0d,
                    Distance = 10d,
                    Yaw = 0f,
                    Dimension = 2
                },
                new()
                {
                    TrackId = orbitTrackId,
                    TrackType = DirectorReplayElementTrackType.CameraOrbit,
                    TimeSeconds = 2d,
                    Distance = 10d,
                    Yaw = 180f,
                    Dimension = 2
                }
            }
        };
        DirectorReplayOrbitGuide orbitGuide = DirectorReplayCameraGuide.BuildOrbit(
            timeline,
            orbitTrackId,
            time => (float)(60d + time),
            samplesPerSegment: 2);
        Check.Equal(3, orbitGuide.Samples.Count);
        Check.Equal(2, orbitGuide.Keyframes.Count);
        DirectorReplayOrbitGuideSample orbitMiddle = orbitGuide.Samples[1];
        Check.NearlyEqual(1d, orbitMiddle.TimeSeconds);
        Check.NearlyEqual(10d, orbitMiddle.Camera.X);
        Check.NearlyEqual(0d, orbitMiddle.Camera.Z);
        Check.NearlyEqual(61d, orbitMiddle.Camera.Fov);
        Check.Equal(2, orbitMiddle.Camera.Dimension);
        Check.NearlyEqual(0d, orbitGuide.Keyframes[0].CenterX);
        Check.NearlyEqual(0d, orbitGuide.Keyframes[0].CenterY);
        Check.NearlyEqual(0d, orbitGuide.Keyframes[0].CenterZ);

        timeline.ElementKeyframes[0].InterpolationToNext = DirectorReplayElementInterpolation.Hold;
        DirectorReplayOrbitGuide heldGuide = DirectorReplayCameraGuide.BuildOrbit(
            timeline,
            orbitTrackId,
            samplesPerSegment: 2);
        Check.NearlyEqual(0d, heldGuide.Samples[1].Camera.X);
        Check.NearlyEqual(10d, heldGuide.Samples[1].Camera.Z);
        Check.NearlyEqual(0d, heldGuide.Samples[1].Camera.Yaw);
        Check.NearlyEqual(180d, heldGuide.Samples[2].Camera.Yaw);
    }

    private static void ReplayElementsNormalizeSampleAndPersist()
    {
        Guid replayId = Guid.NewGuid();
        DirectorReplayElementTrack legacySpeedTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Speed
        };
        DirectorReplayElementTrack legacyTimelapseTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Timelapse
        };
        DirectorReplayElementTrack legacyFreezeTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Freeze
        };
        DirectorReplayDocument migratedTwentyTickReplay = DirectorReplayMath.Normalize(
            new DirectorReplayDocument
            {
                SchemaVersion = 18,
                SampleRate = 20d,
                DurationSeconds = 2d,
                ViewportTimeline = new DirectorReplayViewportTimeline
                {
                    Tracks = new List<DirectorReplayElementTrack>
                    {
                        legacySpeedTrack,
                        legacyTimelapseTrack,
                        legacyFreezeTrack
                    },
                    ElementKeyframes = new List<DirectorReplayElementKeyframe>
                    {
                        new()
                        {
                            TrackId = legacySpeedTrack.Id,
                            TrackType = DirectorReplayElementTrackType.Speed,
                            TickRate = 20f
                        },
                        new()
                        {
                            TrackId = legacyTimelapseTrack.Id,
                            TrackType = DirectorReplayElementTrackType.Timelapse,
                            TimeSeconds = 0d,
                            TimelapseTick = 0
                        },
                        new()
                        {
                            TrackId = legacyTimelapseTrack.Id,
                            TrackType = DirectorReplayElementTrackType.Timelapse,
                            TimeSeconds = 1d,
                            TimelapseTick = 40
                        },
                        new()
                        {
                            TrackId = legacyFreezeTrack.Id,
                            TrackType = DirectorReplayElementTrackType.Freeze,
                            FreezeDelayTicks = 10
                        }
                    }
                }
            });
        Check.Equal(20d, migratedTwentyTickReplay.SampleRate);
        Check.Equal(30f, migratedTwentyTickReplay.ViewportTimeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.Speed).TickRate);
        Check.Equal(60, migratedTwentyTickReplay.ViewportTimeline.ElementKeyframes.Last(item =>
            item.TrackType == DirectorReplayElementTrackType.Timelapse).TimelapseTick);
        Check.Equal(15, migratedTwentyTickReplay.ViewportTimeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.Freeze).FreezeDelayTicks);
        Check.NearlyEqual(1d, DirectorReplayElementTimeline.SampleTimingRate(
            migratedTwentyTickReplay.ViewportTimeline, 0.5d, 1d));

        Guid duplicateId = Guid.NewGuid();
        List<DirectorReplayElementKeyframe> keyframes = new()
        {
            new()
            {
                Id = duplicateId,
                TrackType = DirectorReplayElementTrackType.CameraOrbit,
                TimeSeconds = 0d,
                X = 0d,
                Distance = 8d,
                EntityId = 99,
                EntityActorKey = "entity:99"
            },
            new()
            {
                Id = duplicateId,
                TrackType = DirectorReplayElementTrackType.CameraOrbit,
                TimeSeconds = 2d,
                X = 10d,
                Distance = double.PositiveInfinity
            },
            new()
            {
                TrackType = DirectorReplayElementTrackType.TrackEntity,
                EntityId = 42,
                EntityActorKey = " actor:42 ",
                BodyPart = "unsupported"
            },
            new()
            {
                TrackType = DirectorReplayElementTrackType.CameraShake,
                FrequencyX = 50f,
                AmplitudeX = -2f
            },
            new() { TrackType = DirectorReplayElementTrackType.Fov, Fov = 999f },
            new()
            {
                TrackType = DirectorReplayElementTrackType.Timelapse,
                TimeSeconds = 0d,
                TimelapseTick = 0
            },
            new()
            {
                TrackType = DirectorReplayElementTrackType.Timelapse,
                TimeSeconds = 1d,
                TimelapseTick = 60
            },
            new()
            {
                TrackType = DirectorReplayElementTrackType.TimeOfDay,
                TimeOfDayHours = 30d
            },
            new()
            {
                TrackType = DirectorReplayElementTrackType.Freeze,
                TimeSeconds = 1d,
                Frozen = true,
                FreezeDelayTicks = 5,
                InterpolationToNext = DirectorReplayElementInterpolation.Hold
            },
            new()
            {
                TrackType = DirectorReplayElementTrackType.BlockOverride,
                BlockCode = " game:stone "
            },
            new()
            {
                TrackType = DirectorReplayElementTrackType.Audio,
                AudioAsset = " game:sounds/test "
            },
            new() { TrackType = DirectorReplayElementTrackType.Camera },
            new() { TrackType = (DirectorReplayElementTrackType)999 }
        };
        DirectorReplayDocument replay = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Id = replayId,
            DurationSeconds = 5d,
            ViewportTimeline = new DirectorReplayViewportTimeline
            {
                ElementTracks = Enum.GetValues<DirectorReplayElementTrackType>().ToList(),
                DisabledElementTracks = new List<DirectorReplayElementTrackType>
                {
                    DirectorReplayElementTrackType.Audio,
                    (DirectorReplayElementTrackType)999
                },
                ElementKeyframes = keyframes
            }
        });

        Check.Equal(10, replay.ViewportTimeline.ElementKeyframes.Count);
        Check.Equal(10, replay.ViewportTimeline.ElementKeyframes.Select(item => item.Id).Distinct().Count());
        Check.Empty(replay.ViewportTimeline.DisabledElementTracks);
        Check.False(replay.ViewportTimeline.Tracks.Any(track =>
            track.TrackType == DirectorReplayElementTrackType.Audio));
        Check.True(replay.ViewportTimeline.ElementTracks.Contains(
            DirectorReplayElementTrackType.BlockOverride));
        Check.Equal("game:stone", replay.ViewportTimeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.BlockOverride).BlockCode);
        Check.Equal(150f, replay.ViewportTimeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.Fov).Fov);
        Check.Equal(10f, replay.ViewportTimeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.CameraShake).FrequencyX);
        DirectorReplayElementKeyframe normalizedTrack = replay.ViewportTimeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.TrackEntity);
        Check.Equal("actor:42", normalizedTrack.EntityActorKey);
        Check.Equal("head", normalizedTrack.BodyPart);
        DirectorReplayElementKeyframe normalizedOrbit = replay.ViewportTimeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.CameraOrbit);
        Check.Equal(0L, normalizedOrbit.EntityId);
        Check.Empty(normalizedOrbit.EntityActorKey);

        Check.True(DirectorReplayElementTimeline.TrySample(
            replay.ViewportTimeline.ElementKeyframes,
            DirectorReplayElementTrackType.CameraOrbit,
            1d,
            out DirectorReplayElementKeyframe orbit));
        Check.NearlyEqual(5d, orbit.X);
        Check.NearlyEqual(8d, orbit.Distance);
        Check.NearlyEqual(0.5d, DirectorReplayElementTimeline.SamplePlaybackRate(
            replay.ViewportTimeline.ElementKeyframes, 0.5d));
        Check.False(DirectorReplayElementTimeline.IsFrozen(
            replay.ViewportTimeline.ElementKeyframes, 1.15d));
        Check.True(DirectorReplayElementTimeline.IsFrozen(
            replay.ViewportTimeline.ElementKeyframes, 1.2d));
        Check.Equal(24000, replay.ViewportTimeline.ElementKeyframes.First(item =>
            item.TrackType == DirectorReplayElementTrackType.TimeOfDay).TimeOfDayTick);
        Check.True(replay.ViewportTimeline.ElementKeyframes.All(keyframe =>
            keyframe.TrackId != Guid.Empty));

        DirectorReplayDocument clone = replay.DeepClone();
        clone.ViewportTimeline.ElementKeyframes[0].X = 999d;
        clone.ViewportTimeline.DisabledElementTracks.Clear();
        Check.True(replay.ViewportTimeline.ElementKeyframes[0].X != 999d);
        Check.Empty(replay.ViewportTimeline.DisabledElementTracks);

        string root = Path.Combine(Path.GetTempPath(),
            "vsdirector-replay-elements-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "take.timeline.json");
            DirectorReplayTimelineEditIO.Save(path, DirectorReplayTimelineEditIO.Create(replay));
            DirectorReplayTimelineEditDocument edits = Check.NotNull(
                DirectorReplayTimelineEditIO.Load(path));
            Check.Equal(10, edits.ViewportTimeline.ElementKeyframes.Count);
            Check.Empty(edits.ViewportTimeline.DisabledElementTracks);

            DirectorReplayDocument target = new() { Id = replayId, DurationSeconds = 5d };
            DirectorReplayTimelineEditIO.Apply(target, edits);
            Check.False(target.ViewportTimeline.ElementKeyframes.Any(item =>
                item.TrackType == DirectorReplayElementTrackType.Audio));

            DirectorReplayElementTrack legacySpeed = new()
            {
                TrackType = DirectorReplayElementTrackType.Speed
            };
            DirectorReplayElementTrack legacyTimelapse = new()
            {
                TrackType = DirectorReplayElementTrackType.Timelapse
            };
            DirectorReplayElementTrack legacyFreeze = new()
            {
                TrackType = DirectorReplayElementTrackType.Freeze
            };
            string legacyPath = Path.Combine(root, "legacy.timeline.json");
            DirectorReplayTimelineEditIO.Save(legacyPath, new DirectorReplayTimelineEditDocument
            {
                SchemaVersion = 5,
                ReplayId = replayId,
                ViewportTimeline = new DirectorReplayViewportTimeline
                {
                    Tracks = new List<DirectorReplayElementTrack>
                    {
                        legacySpeed,
                        legacyTimelapse,
                        legacyFreeze
                    },
                    ElementKeyframes = new List<DirectorReplayElementKeyframe>
                    {
                        new()
                        {
                            TrackId = legacySpeed.Id,
                            TrackType = DirectorReplayElementTrackType.Speed,
                            TickRate = 20f
                        },
                        new()
                        {
                            TrackId = legacyTimelapse.Id,
                            TrackType = DirectorReplayElementTrackType.Timelapse,
                            TimelapseTick = 40
                        },
                        new()
                        {
                            TrackId = legacyFreeze.Id,
                            TrackType = DirectorReplayElementTrackType.Freeze,
                            FreezeDelayTicks = 10
                        }
                    }
                }
            });
            DirectorReplayTimelineEditDocument migratedEdits = Check.NotNull(
                DirectorReplayTimelineEditIO.Load(legacyPath));
            Check.Equal(DirectorReplayTimelineEditDocument.CurrentSchemaVersion,
                migratedEdits.SchemaVersion);
            Check.Equal(30f, migratedEdits.ViewportTimeline.ElementKeyframes.First(item =>
                item.TrackType == DirectorReplayElementTrackType.Speed).TickRate);
            Check.Equal(60, migratedEdits.ViewportTimeline.ElementKeyframes.First(item =>
                item.TrackType == DirectorReplayElementTrackType.Timelapse).TimelapseTick);
            Check.Equal(15, migratedEdits.ViewportTimeline.ElementKeyframes.First(item =>
                item.TrackType == DirectorReplayElementTrackType.Freeze).FreezeDelayTicks);
            Check.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TimelapsePairsKeepIndependentRanges()
    {
        DirectorReplayElementTrack track = new()
        {
            TrackType = DirectorReplayElementTrackType.Timelapse,
            TimelapseDurationSeconds = 9d
        };
        Guid firstPairId = Guid.NewGuid();
        Guid secondPairId = Guid.NewGuid();
        DirectorReplayViewportTimeline source = new()
        {
            Tracks = new List<DirectorReplayElementTrack> { track },
            ElementKeyframes = new List<DirectorReplayElementKeyframe>
            {
                new()
                {
                    TrackId = track.Id,
                    TrackType = DirectorReplayElementTrackType.Timelapse,
                    TimeSeconds = 0d,
                    TimelapsePairId = firstPairId,
                    TimelapseRole = DirectorReplayTimelapseKeyframeRole.Start,
                    TimelapseDurationSeconds = 8d
                },
                new()
                {
                    TrackId = track.Id,
                    TrackType = DirectorReplayElementTrackType.Timelapse,
                    TimeSeconds = 2d,
                    TimelapsePairId = firstPairId,
                    TimelapseRole = DirectorReplayTimelapseKeyframeRole.End,
                    TimelapseDurationSeconds = 8d
                },
                new()
                {
                    TrackId = track.Id,
                    TrackType = DirectorReplayElementTrackType.Timelapse,
                    TimeSeconds = 4d,
                    TimelapsePairId = secondPairId,
                    TimelapseRole = DirectorReplayTimelapseKeyframeRole.Start,
                    TimelapseDurationSeconds = 4d
                },
                new()
                {
                    TrackId = track.Id,
                    TrackType = DirectorReplayElementTrackType.Timelapse,
                    TimeSeconds = 6d,
                    TimelapsePairId = secondPairId,
                    TimelapseRole = DirectorReplayTimelapseKeyframeRole.End,
                    TimelapseDurationSeconds = 4d
                }
            }
        };
        DirectorReplayViewportTimeline timeline = DirectorReplayViewportTimeline.Normalize(source, 8d);
        List<DirectorReplayTimelapsePair> pairs = DirectorReplayTimelapse.FindPairs(
            timeline.ElementKeyframes,
            track.Id);
        Check.Equal(2, pairs.Count);
        Check.True(pairs.All(pair => pair.Start.TimelapseRole
            == DirectorReplayTimelapseKeyframeRole.Start));
        Check.True(pairs.All(pair => pair.End.TimelapseRole
            == DirectorReplayTimelapseKeyframeRole.End));
        Check.True(pairs.All(pair => pair.Start.TimelapsePairId == pair.End.TimelapsePairId));
        Check.NearlyEqual(0.25d, DirectorReplayElementTimeline.SampleTimingRate(
            timeline,
            1d,
            1d));
        Check.NearlyEqual(1d, DirectorReplayElementTimeline.SampleTimingRate(
            timeline,
            3d,
            1d));
        Check.NearlyEqual(0.5d, DirectorReplayElementTimeline.SampleTimingRate(
            timeline,
            5d,
            1d));
        Check.NearlyEqual(1d, DirectorReplayElementTimeline.SampleTimingRate(
            timeline,
            7d,
            1d));

        DirectorReplayViewportTimeline legacy = DirectorReplayViewportTimeline.Normalize(
            new DirectorReplayViewportTimeline
            {
                Tracks = new List<DirectorReplayElementTrack>
                {
                    new()
                    {
                        Id = track.Id,
                        TrackType = DirectorReplayElementTrackType.Timelapse
                    }
                },
                ElementKeyframes = new List<DirectorReplayElementKeyframe>
                {
                    new()
                    {
                        TrackId = track.Id,
                        TrackType = DirectorReplayElementTrackType.Timelapse,
                        TimeSeconds = 1d
                    },
                    new()
                    {
                        TrackId = track.Id,
                        TrackType = DirectorReplayElementTrackType.Timelapse,
                        TimeSeconds = 3d
                    }
                }
            },
            4d);
        List<DirectorReplayTimelapsePair> migratedPairs = DirectorReplayTimelapse.FindPairs(
            legacy.ElementKeyframes,
            track.Id);
        Check.Equal(1, migratedPairs.Count);
        DirectorReplayTimelapsePair migrated = migratedPairs[0];
        Check.Equal(DirectorReplayTimelapseKeyframeRole.Start, migrated.Start.TimelapseRole);
        Check.Equal(DirectorReplayTimelapseKeyframeRole.End, migrated.End.TimelapseRole);
        Check.Equal(migrated.Start.TimelapsePairId, migrated.End.TimelapsePairId);
    }

    private static void ReplayExportTimingAppliesElements()
    {
        DirectorReplayViewportTimeline baseline = new();
        long baselineFrames = DirectorReplayElementExportClock.EstimateFrameCount(
            baseline,
            0d,
            2d,
            1d,
            20);

        DirectorReplayElementTrack speedTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Speed
        };
        DirectorReplayViewportTimeline speed = new()
        {
            Tracks = new List<DirectorReplayElementTrack> { speedTrack },
            ElementKeyframes = new List<DirectorReplayElementKeyframe>
            {
                new()
                {
                    TrackId = speedTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Speed,
                    TimeSeconds = 0d,
                    TickRate = 60f
                }
            }
        };
        Check.True(DirectorReplayElementExportClock.EstimateFrameCount(
            speed, 0d, 2d, 1d, 20) < baselineFrames);

        speedTrack.Enabled = false;
        Check.Equal(baselineFrames, DirectorReplayElementExportClock.EstimateFrameCount(
            speed, 0d, 2d, 1d, 20));

        DirectorReplayElementTrack timelapseTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Timelapse
        };
        DirectorReplayViewportTimeline timelapse = new()
        {
            Tracks = new List<DirectorReplayElementTrack> { timelapseTrack },
            ElementKeyframes = new List<DirectorReplayElementKeyframe>
            {
                new()
                {
                    TrackId = timelapseTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Timelapse,
                    TimeSeconds = 0d,
                    TimelapseTick = 0
                },
                new()
                {
                    TrackId = timelapseTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Timelapse,
                    TimeSeconds = 2d,
                    TimelapseTick = 15
                }
            }
        };
        Check.True(DirectorReplayElementExportClock.EstimateFrameCount(
            timelapse, 0d, 2d, 1d, 20) < baselineFrames);
        timelapseTrack.TimelapseDurationSeconds = 8d;
        Check.NearlyEqual(0.25d, DirectorReplayElementTimeline.SampleTimingRate(
            timelapse, 0.5d, 1d));

        DirectorReplayElementTrack prioritySpeedTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Speed
        };
        DirectorReplayElementTrack priorityTimelapseTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Timelapse
        };
        DirectorReplayViewportTimeline priority = new()
        {
            Tracks = new List<DirectorReplayElementTrack>
            {
                prioritySpeedTrack,
                priorityTimelapseTrack
            },
            ElementKeyframes = new List<DirectorReplayElementKeyframe>
            {
                new()
                {
                    TrackId = prioritySpeedTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Speed,
                    TimeSeconds = 0d,
                    TickRate = 60f
                },
                new()
                {
                    TrackId = priorityTimelapseTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Timelapse,
                    TimeSeconds = 0d,
                    TimelapseTick = 0
                },
                new()
                {
                    TrackId = priorityTimelapseTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Timelapse,
                    TimeSeconds = 2d,
                    TimelapseTick = 15
                }
            }
        };
        Check.NearlyEqual(2d, DirectorReplayElementTimeline.SampleTimingRate(
            priority, 0.5d, 1d));
        priority.Tracks.Reverse();
        Check.NearlyEqual(4d, DirectorReplayElementTimeline.SampleTimingRate(
            priority, 0.5d, 1d));
        priorityTimelapseTrack.Enabled = false;
        Check.NearlyEqual(2d, DirectorReplayElementTimeline.SampleTimingRate(
            priority, 0.5d, 1d));
        priorityTimelapseTrack.Enabled = true;
        priority.ElementKeyframes.First(keyframe =>
            keyframe.TrackId == priorityTimelapseTrack.Id).TimelapseTick = 20;
        Check.NearlyEqual(2d, DirectorReplayElementTimeline.SampleTimingRate(
            priority, 0.5d, 1d));
        DirectorReplayElementKeyframe firstTimelapse = priority.ElementKeyframes.First(keyframe =>
            keyframe.TrackId == priorityTimelapseTrack.Id);
        firstTimelapse.TimeSeconds = 1d;
        firstTimelapse.TimelapseTick = 0;
        Check.NearlyEqual(2d, DirectorReplayElementTimeline.SampleTimingRate(
            priority, 0.5d, 1d));
        priority.ElementKeyframes.First(keyframe =>
            keyframe.TrackId == prioritySpeedTrack.Id).TimeSeconds = 1d;
        Check.NearlyEqual(1d, DirectorReplayElementTimeline.SampleTimingRate(
            priority, 0.5d, 1d));

        DirectorReplayElementTrack freezeTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Freeze
        };
        DirectorReplayViewportTimeline freeze = new()
        {
            Tracks = new List<DirectorReplayElementTrack> { freezeTrack },
            ElementKeyframes = new List<DirectorReplayElementKeyframe>
            {
                new()
                {
                    TrackId = freezeTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Freeze,
                    TimeSeconds = 0.5d,
                    Frozen = true,
                    FreezeDelayTicks = 15
                }
            }
        };
        Check.Equal(baselineFrames, DirectorReplayElementExportClock.EstimateFrameCount(
            freeze, 0d, 2d, 1d, 20));

        Check.Equal(
            DirectorReplayAudioExportPolicy.SampleRate,
            DirectorReplayAudioExportPolicy.SamplesThroughFrame(30L, 30));
        Check.Equal(1600, DirectorReplayAudioExportPolicy.SamplesInFrame(0L, 30));
        Check.Equal(
            DirectorReplayAudioExportPolicy.SampleRate,
            Enumerable.Range(0, 29)
                .Sum(index => DirectorReplayAudioExportPolicy.SamplesInFrame(index, 29)));
        Check.NearlyEqual(
            2d,
            DirectorReplayAudioExportPolicy.ResolveReplayRate(1d, 1.1d, 20, 1d));
        Check.NearlyEqual(
            0d,
            DirectorReplayAudioExportPolicy.ResolveReplayRate(1d, 1d, 20, 1d));

        DirectorReplayExportOptions audioOptions = new();
        Check.True(audioOptions.IncludeAudio);
        Check.Equal(192, audioOptions.AudioBitrateKbps);
        DirectorReplayExportOptions audioClone = audioOptions.DeepClone();
        audioClone.IncludeAudio = false;
        audioClone.AudioBitrateKbps = 320;
        Check.True(audioOptions.IncludeAudio);
        Check.Equal(192, audioOptions.AudioBitrateKbps);
    }

    private static void ReplayFreezeReachesUnfreezeKeyframe()
    {
        DirectorReplayElementTrack freezeTrack = new()
        {
            TrackType = DirectorReplayElementTrackType.Freeze
        };
        DirectorReplayViewportTimeline timeline = new()
        {
            Tracks = new List<DirectorReplayElementTrack> { freezeTrack },
            ElementKeyframes = new List<DirectorReplayElementKeyframe>
            {
                new()
                {
                    TrackId = freezeTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Freeze,
                    TimeSeconds = 1d,
                    Frozen = true,
                    FreezeDelayTicks = 6,
                    InterpolationToNext = DirectorReplayElementInterpolation.Hold
                },
                new()
                {
                    TrackId = freezeTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Freeze,
                    TimeSeconds = 2d,
                    Frozen = true,
                    FreezeDelayTicks = 15,
                    InterpolationToNext = DirectorReplayElementInterpolation.Hold
                },
                new()
                {
                    TrackId = freezeTrack.Id,
                    TrackType = DirectorReplayElementTrackType.Freeze,
                    TimeSeconds = 3d,
                    Frozen = false,
                    InterpolationToNext = DirectorReplayElementInterpolation.Hold
                }
            }
        };

        DirectorReplayElementFreezeState before = DirectorReplayElementTimeline.SampleFreeze(
            timeline,
            0.5d);
        Check.False(before.Frozen);
        Check.NearlyEqual(0.5d, before.ReplayTimeSeconds);

        DirectorReplayElementFreezeState slowing = DirectorReplayElementTimeline.SampleFreeze(
            timeline,
            1.1d);
        Check.True(slowing.Frozen);
        Check.False(slowing.FullyFrozen);
        Check.True(slowing.ReplayTimeSeconds > 1d);
        Check.True(slowing.ReplayTimeSeconds < 1.1d);

        DirectorReplayElementFreezeState held = DirectorReplayElementTimeline.SampleFreeze(
            timeline,
            1.5d);
        Check.True(held.FullyFrozen);
        Check.NearlyEqual(
            1d + 2d / DirectorReplayElementTimeline.TicksPerSecond,
            held.ReplayTimeSeconds);
        Check.NearlyEqual(
            held.ReplayTimeSeconds,
            DirectorReplayElementTimeline.SampleFreeze(timeline, 2.5d).ReplayTimeSeconds);
        Check.True(DirectorReplayElementTimeline.IsFrozen(timeline, 2.5d));

        DirectorReplayElementFreezeState resumed = DirectorReplayElementTimeline.SampleFreeze(
            timeline,
            3d);
        Check.False(resumed.Frozen);
        Check.False(resumed.FullyFrozen);
        Check.NearlyEqual(3d, resumed.ReplayTimeSeconds);
        Check.False(DirectorReplayElementTimeline.IsFrozen(timeline, 3d));

        DirectorReplayElementExportClock clock = new(timeline, 0d, 4d, 1d, 30);
        while (!clock.IsComplete)
        {
            clock.Advance();
        }
        Check.NearlyEqual(4d, clock.CurrentReplaySeconds);
    }

    private static void ReplayAdjustedDurationCalculates()
    {
        Check.NearlyEqual(10d, DirectorReplayTimeline.AdjustedDurationSeconds(10d, null, 1d));
        Check.NearlyEqual(5d, DirectorReplayTimeline.AdjustedDurationSeconds(10d, null, 2d));
        Check.NearlyEqual(2d, DirectorReplayTimeline.AdjustedElapsedSeconds(4d, 10d, null, 2d));

        List<DirectorReplaySpeedKeyframe> hold = new()
        {
            new()
            {
                TimeSeconds = 2d,
                Speed = 0.5d,
                InterpolationToNext = DirectorReplaySpeedInterpolation.Hold
            },
            new()
            {
                TimeSeconds = 6d,
                Speed = 2d,
                InterpolationToNext = DirectorReplaySpeedInterpolation.Hold
            }
        };
        Check.NearlyEqual(12d, DirectorReplayTimeline.AdjustedDurationSeconds(10d, hold, 1d));
        Check.NearlyEqual(1d, DirectorReplayTimeline.AdjustedElapsedSeconds(1d, 10d, hold, 1d));
        Check.NearlyEqual(6d, DirectorReplayTimeline.AdjustedElapsedSeconds(4d, 10d, hold, 1d));
        Check.NearlyEqual(11d, DirectorReplayTimeline.AdjustedElapsedSeconds(8d, 10d, hold, 1d));

        List<DirectorReplaySpeedKeyframe> linear = new()
        {
            new()
            {
                TimeSeconds = 0d,
                Speed = 1d,
                InterpolationToNext = DirectorReplaySpeedInterpolation.Linear
            },
            new()
            {
                TimeSeconds = 10d,
                Speed = 2d,
                InterpolationToNext = DirectorReplaySpeedInterpolation.Hold
            }
        };
        Check.NearlyEqual(10d * Math.Log(2d),
            DirectorReplayTimeline.AdjustedDurationSeconds(10d, linear, 1d), 0.000001d);
        Check.NearlyEqual(10d * Math.Log(1.5d),
            DirectorReplayTimeline.AdjustedElapsedSeconds(5d, 10d, linear, 1d), 0.000001d);

        linear[0].InterpolationToNext = DirectorReplaySpeedInterpolation.EaseInOut;
        double eased = DirectorReplayTimeline.AdjustedDurationSeconds(10d, linear, 1d);
        Check.True(eased > 5d && eased < 10d);
        Check.NearlyEqual(4.279446633229175d,
            DirectorReplayTimeline.AdjustedElapsedSeconds(5d, 10d, linear, 1d), 0.000001d);
        Check.NearlyEqual(eased,
            DirectorReplayTimeline.AdjustedElapsedSeconds(10d, 10d, linear, 1d), 0.000001d);
        Check.NearlyEqual(0d,
            DirectorReplayTimeline.AdjustedDurationSeconds(double.NaN, linear, 1d));
    }

    private static void ActionAssetsNormalizeSerializeAndPackage()
    {
        Guid actionId = Guid.NewGuid();
        Guid layerId = Guid.NewGuid();
        DirectorActionAsset source = new()
        {
            Id = actionId,
            Name = "  Sword Combo  ",
            Author = "Director",
            Favorite = true,
            Tags = new List<string> { "Attack", "attack", " cinema " },
            CompatibleEntityCodes = new List<string> { "player", " player " },
            CompatibleModelCodes = new List<string> { "playermodellib:seraph" },
            PreviewModelCode = "playermodellib:seraph",
            PreviewEntityCode = "player",
            PreviewEntityName = "Lead model",
            PreviewEntityState = new byte[] { 7, 8, 9 },
            LoopMode = DirectorActionLoopMode.Loop,
            LoopCount = 0,
            Layers = new List<DirectorActionLayer>
            {
                new()
                {
                    Id = layerId,
                    Name = "Upper",
                    Kind = DirectorActionLayerKind.UpperBody,
                    Frames = new List<DirectorActionFrame>
                    {
                        ActionFrame(1d, "hit", 0.5f),
                        ActionFrame(0d, "idle", 0f),
                        ActionFrame(1d, "swordhit", 0.75f)
                    }
                },
                new() { Id = layerId, Name = "Expression", Kind = DirectorActionLayerKind.Expression }
            },
            Events = new List<DirectorActionEvent>
            {
                new() { TimeSeconds = 0.5d, Type = DirectorActionEventType.Sound, Code = "game:sounds/player/strike" }
            }
        };

        DirectorActionAsset normalized = DirectorActionNormalizer.Normalize(source);
        DirectorActionLayer normalizedUpper = normalized.Layers.Single(layer => layer.Name == "Upper");
        Check.Equal("Sword Combo", normalized.Name);
        Check.Equal(2, normalized.Tags.Count);
        Check.Equal(2, normalized.Layers.Count);
        Check.Equal(2, normalizedUpper.Frames.Count);
        Check.Equal(2, normalized.Layers.Select(layer => layer.Id).Distinct().Count());
        Check.Equal("swordhit", normalizedUpper.Frames[1].State.AnimationStates[0].Code);
        Check.Equal("playermodellib:seraph", normalized.PreviewModelCode);
        Check.Equal("player", normalized.PreviewEntityCode);
        Check.Equal("Lead model", normalized.PreviewEntityName);
        Check.SequenceEqual(new byte[] { 7, 8, 9 }, normalized.PreviewEntityState);

        string json = DirectorActionIO.ExportJson(normalized);
        DirectorActionAsset jsonAction = DirectorActionIO.ImportJson(json);
        Check.Equal(actionId, jsonAction.Id);
        Check.Equal("Sword Combo", jsonAction.Name);
        Check.Equal(1, jsonAction.Events.Count);
        Check.SequenceEqual(new byte[] { 7, 8, 9 }, jsonAction.PreviewEntityState);
        DirectorActionLayer jsonUpper = jsonAction.Layers.Single(layer => layer.Name == "Upper");
        Check.NearlyEqual(0.75d, jsonUpper.Frames[1].State.AnimationStates[0].Progress);

        string root = Path.Combine(Path.GetTempPath(), "vsdirector-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string binary = Path.Combine(root, "combo.vsaction");
            DirectorActionIO.Write(binary, normalized);
            DirectorActionAsset binaryAction = DirectorActionIO.Read(binary);
            Check.Equal(actionId, binaryAction.Id);
            string package = Path.Combine(root, "pack.vsactionpack");
            DirectorActionIO.WritePackage(package, new[] { normalized, DirectorActionImporter.CreateAnimationAction("Wave", "wave") }, "Pack");
            IReadOnlyList<DirectorActionAsset> packageActions = DirectorActionIO.ReadPackage(package);
            Check.Equal(2, packageActions.Count);
            Check.True(packageActions.Any(action => action.Name == "Wave"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        CameraLibrary library = new(new CameraLibraryDocument { Actions = new List<DirectorActionAsset> { normalized } });
        Check.Equal(1, library.ActionCount);
        Check.True(Check.NotNull(library.FindAction(actionId)).Favorite);
    }

    private static void ActionReplayImportDetectsAndComposesLayers()
    {
        DirectorReplayActorTrack actor = new()
        {
            ActorKey = "player:lead",
            PlayerUid = "lead",
            EntityCode = "player",
            Samples = new List<DirectorReplayActorSample>
            {
                ReplayActionSample(2d, "sprint", 0f, swimming: false),
                ReplayActionSample(3d, "swim", 0.4f, swimming: true),
                ReplayActionSample(4d, "swordhit", 0.8f, swimming: false)
            }
        };
        Check.True(DirectorActionImporter.TryCreateFromReplay(
            actor,
            2.25d,
            3.75d,
            "Lead Action",
            out DirectorActionAsset action,
            out string error,
            Guid.NewGuid()));
        Check.Equal(string.Empty, error);
        Check.True(action.Tags.Contains("sprinting"));
        Check.True(action.Tags.Contains("swimming"));
        Check.True(action.Tags.Contains("attack"));
        Check.NearlyEqual(0d, action.Layers[0].Frames[0].TimeSeconds);
        Check.NearlyEqual(1.5d, action.DurationSeconds);

        DirectorActionLayer upper = action.Layers[0].DeepClone();
        upper.Id = Guid.NewGuid();
        upper.Name = "Upper";
        upper.Kind = DirectorActionLayerKind.UpperBody;
        upper.BlendMode = DirectorActionBlendMode.Additive;
        upper.Weight = 0.5f;
        action.Layers.Add(upper);
        DirectorActionEvaluation evaluation = new DirectorActionEvaluator(action).Sample(0.75d);
        DirectorReplayActorSample composed = DirectorActionComposer.Apply(
            ReplayActionSample(10d, "walk", 0.25f, swimming: false),
            evaluation);
        Check.True(composed.AnimationStates.Count > 0);
        DirectorReplayAnimationSample upperState = Check.NotNull(composed.AnimationStates.LastOrDefault());
        Check.True(upperState.ElementWeights.TryGetValue("UpperArmR", out float upperWeight));
        Check.True(upperWeight > 0f);
        Check.True(upperState.ElementWeights.TryGetValue("UpperFootR", out float lowerWeight));
        Check.NearlyEqual(0d, lowerWeight);
    }

    private static void ActionEvaluatorLoopsTrimsAndAppends()
    {
        DirectorActionAsset action = DirectorActionImporter.CreateAnimationAction("Loop", "wave");
        action.PlaybackSpeed = 2d;
        action.LoopMode = DirectorActionLoopMode.PingPong;
        action.LoopCount = 2;
        DirectorActionEvaluator evaluator = new(action);
        Check.NearlyEqual(0.5d, evaluator.Sample(0.25d).SourceSeconds);
        Check.NearlyEqual(0.5d, evaluator.Sample(0.75d).SourceSeconds);
        Check.True(evaluator.Sample(2d).Completed);

        DirectorActionAsset finite = DirectorActionImporter.CreateAnimationAction("Finite", "walk");
        finite.LoopMode = DirectorActionLoopMode.Once;
        finite.LoopCount = 1;
        DirectorActionAsset scheduled = DirectorActionCardPlayback.Configure(
            finite,
            DirectorActionLoopMode.Loop);
        DirectorActionEvaluation repeated = new DirectorActionEvaluator(scheduled).Sample(1.25d);
        Check.Equal(DirectorActionLoopMode.Loop, scheduled.LoopMode);
        Check.Equal(0, scheduled.LoopCount);
        Check.NearlyEqual(0.25d, repeated.SourceSeconds);
        Check.False(repeated.Completed);
        Check.Equal(DirectorActionLoopMode.Once, finite.LoopMode);
        Check.Equal(1, finite.LoopCount);

        DirectorActionAsset recorded = new()
        {
            Name = "Long",
            Layers = new List<DirectorActionLayer>
            {
                new()
                {
                    Name = "Base",
                    Frames = new List<DirectorActionFrame>
                    {
                        ActionFrame(0d, "idle", 0f),
                        ActionFrame(1d, "walk", 0.25f),
                        ActionFrame(2d, "run", 0.5f),
                        ActionFrame(3d, "jump", 0.75f)
                    }
                }
            }
        };
        DirectorActionAsset trimmed = DirectorActionEditor.Trim(recorded, 0.5d, 2.5d);
        Check.NearlyEqual(2d, trimmed.DurationSeconds);
        Check.NearlyEqual(0d, trimmed.Layers[0].Frames[0].TimeSeconds);
        DirectorActionAsset appended = DirectorActionEditor.Append(trimmed, action, 0.25d);
        Check.True(appended.DurationSeconds > trimmed.DurationSeconds);
        Check.True(appended.Tags.Contains("emote"));

        DirectorActionEditHistory history = new();
        Check.True(history.RecordChange(recorded, trimmed));
        Check.True(history.TryUndo(trimmed, out DirectorActionAsset undone));
        Check.NearlyEqual(recorded.DurationSeconds, undone.DurationSeconds);
        Check.True(history.TryRedo(undone, out DirectorActionAsset redone));
        Check.NearlyEqual(trimmed.DurationSeconds, redone.DurationSeconds);
        Check.True(DirectorActionTriggerDetector.Matches(
            ReplayActionSample(0d, "swordhit", 0.2f, swimming: false),
            DirectorActionTriggerType.Attack));
        Check.True(DirectorActionTriggerDetector.Matches(
            ReplayActionSample(0d, "custompose", 0.2f, swimming: false),
            DirectorActionTriggerType.Custom,
            "custompose"));
        Check.False(DirectorActionTriggerDetector.Matches(
            ReplayActionSample(0d, "walk", 0.2f, swimming: false),
            DirectorActionTriggerType.Sprinting));
    }

    private static void ImportedActionsAnimateWithoutOverridingFacing()
    {
        DirectorActionAsset action = DirectorActionImporter.CreateAnimationAction(
            "Wave",
            "wave",
            sourceModId: "emotes",
            sourceCode: "wave",
            durationSeconds: 2d,
            animationSpeed: 0.5f,
            easeInSpeed: 2f,
            easeOutSpeed: 3f);
        Check.Equal(DirectorActionLayerKind.Additive, action.Layers[0].Kind);
        Check.NearlyEqual(0d, action.Layers[0].Frames[0].State.AnimationStates[0].Progress);
        Check.NearlyEqual(1d, action.Layers[0].Frames[^1].State.AnimationStates[0].Progress);
        DirectorActionEvaluation halfway = new DirectorActionEvaluator(action).Sample(1d);
        Check.NearlyEqual(0.5d, halfway.Layers[0].State.AnimationStates[0].Progress);
        DirectorReplayActorSample facing = ReplayActionSample(0d, "idle", 0f, swimming: false);
        facing.BodyYaw = 1.25f;
        facing.BodyYawServer = 1.25f;
        DirectorReplayActorSample composed = DirectorActionComposer.Apply(facing, halfway);
        Check.NearlyEqual(1.25d, composed.BodyYaw);

        DirectorActionAsset broken = action.DeepClone();
        broken.Layers[0].Kind = DirectorActionLayerKind.FullBody;
        foreach (DirectorActionFrame frame in broken.Layers[0].Frames)
        {
            frame.State.AnimationStates[0].Progress = 0f;
        }
        Check.True(DirectorActionImporter.TryRepairGeneratedAnimationProgress(
            broken,
            out DirectorActionAsset repaired));
        Check.Equal(DirectorActionLayerKind.Additive, repaired.Layers[0].Kind);
        Check.NearlyEqual(1d, repaired.Layers[0].Frames[^1].State.AnimationStates[0].Progress);
    }

    private static void LegacyActionWorkflowCardsAreRemoved()
    {
        Guid cameraId = Guid.NewGuid();
        CameraLibrary library = new(new CameraLibraryDocument
        {
            Cameras = new List<CameraPreset> { new() { Id = cameraId, Name = "Camera" } },
            Workflows = new List<DirectorWorkflow>
            {
                new()
                {
                    Name = "Recording",
                    Type = DirectorWorkflowType.Recording,
                    Cards = new List<DirectorWorkflowCard>
                    {
                        new()
                        {
                            Name = "Shot",
                            Kind = DirectorWorkflowCardKind.Shot,
                            SourceType = DirectorRecordingSourceType.StaticCamera,
                            SourceId = cameraId,
                            EndSeconds = 5d
                        },
                        new() { Name = "Countdown", Kind = DirectorWorkflowCardKind.Countdown, EndSeconds = 3d },
                        new() { Name = "Entity", Kind = DirectorWorkflowCardKind.Entity, EndSeconds = 5d },
                        new() { Name = "Actor path", Kind = DirectorWorkflowCardKind.ActorPath, EndSeconds = 5d },
                        new() { Name = "Action", Kind = DirectorWorkflowCardKind.Action, EndSeconds = 5d },
                        new() { Name = "Environment", Kind = DirectorWorkflowCardKind.Environment, EndSeconds = 5d }
                    }
                }
            }
        });
        DirectorWorkflow persisted = Check.NotNull(library.FindWorkflow("Recording"));
        Check.Equal(DirectorWorkflowType.Recording, persisted.Type);
        Check.Equal(2, persisted.Cards.Count);
        Check.True(persisted.Cards.All(card =>
            card.Kind is DirectorWorkflowCardKind.Shot or DirectorWorkflowCardKind.Countdown));
        Check.False(library.UpsertWorkflowCard(
            persisted.Id,
            new DirectorWorkflowCard { Kind = DirectorWorkflowCardKind.Action }));
    }

    private static void ModelAssetsNormalizeCloneAndPersist()
    {
        DirectorModelAsset source = new()
        {
            Name = "  Lead model  ",
            ModelCode = "  playermodellib:lead  ",
            EntityCode = "  game:player  ",
            Group = "  Cast  ",
            Description = "  Main actor  ",
            SourceModId = "  playermodellib  ",
            Size = 0.01f,
            Favorite = true,
            ShapeAssetLocation = "  cast:shapes/entity/lead.json  ",
            ShapeSourcePath = "  C:\\Models\\lead.json  ",
            ShapeSourceSha256 = new string('A', 64),
            ShapeSourceModifiedUtc = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.FromHours(8)),
            ShapeFormat = "wrong-format",
            ShapeAnimationCodes = new List<string> { " walk ", "WALK", " idle " }
        };
        CameraLibrary library = new(new CameraLibraryDocument
        {
            SchemaVersion = 20,
            Models = new List<DirectorModelAsset> { source }
        });

        DirectorModelAsset normalized = Check.NotNull(library.FindModel("playermodellib:lead"));
        Check.Equal(CameraLibraryDocument.CurrentSchemaVersion, library.Document.SchemaVersion);
        Check.Equal("Lead model", normalized.Name);
        Check.Equal("playermodellib:lead", normalized.ModelCode);
        Check.Equal("game:player", normalized.EntityCode);
        Check.Equal("Cast", normalized.Group);
        Check.Equal("Main actor", normalized.Description);
        Check.Equal("playermodellib", normalized.SourceModId);
        Check.Equal(0.1f, normalized.Size);
        Check.True(normalized.Favorite);
        Check.Equal("cast:shapes/entity/lead.json", normalized.ShapeAssetLocation);
        Check.Equal("C:\\Models\\lead.json", normalized.ShapeSourcePath);
        Check.Equal(new string('a', 64), normalized.ShapeSourceSha256);
        Check.Equal("vintagestory-shape", normalized.ShapeFormat);
        Check.SequenceEqual(new[] { "idle", "walk" }, normalized.ShapeAnimationCodes);
        Check.Equal(TimeSpan.Zero, normalized.ShapeSourceModifiedUtc!.Value.Offset);

        source.Name = "Changed source";
        CameraLibraryDocument snapshot = library.Document;
        snapshot.Models[0].Name = "Changed snapshot";
        normalized.Name = "Edited clone";
        normalized.ShapeAnimationCodes.Add("clone-only");
        Check.Equal("Lead model", Check.NotNull(library.FindModel("playermodellib:lead")).Name);
        Check.Equal(2, Check.NotNull(library.FindModel("playermodellib:lead")).ShapeAnimationCodes.Count);

        DirectorModelAsset update = Check.NotNull(library.FindModel("playermodellib:lead"));
        update.Name = "Hero model";
        update.Size = 100f;
        Check.True(library.UpdateModel(update));
        DirectorModelAsset updated = Check.NotNull(library.FindModel(update.Id));
        Check.Equal("Hero model", updated.Name);
        Check.Equal(10f, updated.Size);

        string json = JsonSerializer.Serialize(library.Document);
        CameraLibraryDocument? decoded = JsonSerializer.Deserialize<CameraLibraryDocument>(json);
        CameraLibrary reloaded = new(Check.NotNull(decoded));
        DirectorModelAsset persisted = Check.NotNull(reloaded.FindModel("playermodellib:lead"));
        Check.Equal("Hero model", persisted.Name);
        Check.Equal("game:player", persisted.EntityCode);
        Check.Equal(10f, persisted.Size);
        Check.True(persisted.Favorite);
        Check.Equal("C:\\Models\\lead.json", persisted.ShapeSourcePath);
        Check.SequenceEqual(new[] { "idle", "walk" }, persisted.ShapeAnimationCodes);
        Check.True(reloaded.RemoveModel(persisted.Id));
        Check.True(reloaded.FindModel(persisted.Id) is null);

        string root = Path.Combine(Path.GetTempPath(), $"vsdirector-model-tests-{Guid.NewGuid():N}");
        try
        {
            string modelPath = Path.Combine(root, "hero.vsmodel");
            DirectorModelAssetIO.Write(modelPath, persisted);
            DirectorModelAsset single = Check.NotNull(DirectorModelAssetIO.Read(modelPath).SingleOrDefault());
            Check.Equal(persisted.Id, single.Id);
            Check.Equal("playermodellib:lead", single.ModelCode);
            Check.Equal("game:player", single.EntityCode);
            Check.Equal("C:\\Models\\lead.json", single.ShapeSourcePath);
            Check.Equal(new string('a', 64), single.ShapeSourceSha256);

            string packagePath = Path.Combine(root, "cast.json");
            DirectorModelAssetIO.WritePackage(packagePath, new[]
            {
                persisted,
                new DirectorModelAsset
                {
                    Name = "Bighorn",
                    ModelCode = "entity/game:bighornsheep-male-adult"
                }
            }, "Cast");
            IReadOnlyList<DirectorModelAsset> package = DirectorModelAssetIO.Read(packagePath);
            Check.Equal(2, package.Count);
            DirectorModelAsset portableLead = Check.NotNull(package.SingleOrDefault(model =>
                model.ModelCode == "playermodellib:lead"));
            Check.Equal(string.Empty, portableLead.ShapeSourcePath);
            Check.Equal(string.Empty, portableLead.ShapeSourceSha256);
            Check.Null(portableLead.ShapeSourceModifiedUtc);
            Check.Equal("cast:shapes/entity/lead.json", portableLead.ShapeAssetLocation);
            DirectorModelAsset creature = Check.NotNull(package.SingleOrDefault(model =>
                model.ModelCode == "entity/game:bighornsheep-male-adult"));
            Check.Equal("game:bighornsheep-male-adult", creature.EntityCode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void Vsmc2ShapeClassificationAndValidation()
    {
        const string validShape = """
        {
          "editor": { "futureOption": true },
          "textureWidth": 64,
          "textureHeight": 64,
          "textures": { "body": "entity/actor/body" },
          "elements": [
            {
              "name": "Root",
              "from": [0, 0, 0],
              "to": [4, 8, 4],
              "rotationOrigin": [2, 4, 2],
              "uv": [0, 0],
              "faces": {
                "north": { "texture": "#body", "uv": [0, 0, 4, 8] }
              },
              "attachmentPoints": [
                { "code": "hand", "posX": 1, "posY": 2, "posZ": 3 }
              ],
              "children": [
                {
                  "name": "Hand",
                  "from": [0, 0, 0],
                  "to": [1, 2, 1],
                  "stepParentName": "Root"
                }
              ]
            }
          ],
          "animations": [
            {
              "name": "Walk",
              "code": "walk",
              "quantityframes": 10,
              "keyframes": [
                { "frame": 0, "elements": { "Root": {}, "Hand": {} } }
              ]
            }
          ],
          "unknownRootField": { "mustRemainUntouched": true }
        }
        """;

        Check.Equal(DirectorModelFileKind.VintagestoryShape,
            DirectorVsmc2ShapeDocument.DetectFileKind(validShape));
        Check.Equal(DirectorModelFileKind.DirectorPackage,
            DirectorVsmc2ShapeDocument.DetectFileKind("{\"schemaVersion\":1,\"models\":[]}"));
        Check.Equal(DirectorModelFileKind.DirectorModel,
            DirectorVsmc2ShapeDocument.DetectFileKind("{\"name\":\"Actor\",\"modelCode\":\"actor:test\"}"));
        Check.Equal(DirectorModelFileKind.Unknown,
            DirectorVsmc2ShapeDocument.DetectFileKind("{\"name\":\"not enough\"}"));

        DirectorVsmc2ShapeDocument shape = DirectorVsmc2ShapeDocument.Parse(validShape, "actor.json");
        Check.True(shape.IsValid);
        Check.Equal(2, shape.ElementCount);
        Check.Equal(1, shape.TextureCount);
        Check.SequenceEqual(new[] { "walk" }, shape.AnimationCodes);
        Check.Equal(64, shape.Sha256.Length);

        const string invalidShape = """
        {
          "textures": { "body": "entity/body" },
          "elements": [
            {
              "name": "Root",
              "from": [0, 0],
              "to": [1, 1, 1],
              "stepParentName": "Missing",
              "faces": { "north": { "texture": "#missing", "uv": [0, 1] } },
              "attachmentPoints": [{ "code": "hand" }, { "code": "HAND" }],
              "children": [{ "name": "Root", "from": [0, 0, 0], "to": [1, 1, 1] }]
            }
          ],
          "animations": [
            {
              "code": "broken",
              "quantityframes": 0,
              "keyframes": [{ "frame": 2, "elements": { "Ghost": {} } }]
            }
          ]
        }
        """;
        DirectorVsmc2ShapeValidationResult invalid = DirectorVsmc2ShapeValidator.Validate(invalidShape, "bad.json");
        Check.False(invalid.IsValid);
        HashSet<string> codes = invalid.Diagnostics.Select(diagnostic => diagnostic.Code).ToHashSet();
        Check.True(codes.Contains("shape.array.invalid"));
        Check.True(codes.Contains("shape.element.name.duplicate"));
        Check.True(codes.Contains("shape.stepParent.missing"));
        Check.True(codes.Contains("shape.face.texture.missing"));
        Check.True(codes.Contains("shape.attachment.code.duplicate"));
        Check.True(codes.Contains("shape.animation.frames.invalid"));
        Check.True(codes.Contains("shape.keyframe.frame.range"));
        Check.True(codes.Contains("shape.keyframe.element.missing"));

        string root = Path.Combine(Path.GetTempPath(), $"vsdirector-shape-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            string shapePath = Path.Combine(root, "actor.json");
            File.WriteAllText(shapePath, validShape, Encoding.UTF8);
            Check.Throws<DirectorShapeFileException>(() => DirectorModelAssetIO.Read(shapePath));

            string legacyPath = Path.Combine(root, "legacy.vsmodel");
            File.WriteAllText(legacyPath,
                "{\"name\":\"Legacy\",\"modelCode\":\"legacy:model\"}", Encoding.UTF8);
            DirectorModelAsset legacy = DirectorModelAssetIO.Read(legacyPath).Single();
            Check.Equal(string.Empty, legacy.ShapeSourcePath);
            Check.Empty(legacy.ShapeAnimationCodes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static DirectorActionFrame ActionFrame(double time, string animation, float progress)
        => new()
        {
            TimeSeconds = time,
            State = ReplayActionSample(time, animation, progress, swimming: false)
        };

    private static DirectorReplayActorSample ReplayActionSample(
        double time,
        string animation,
        float progress,
        bool swimming)
        => new()
        {
            TimeSeconds = time,
            HasMotion = true,
            MotionX = 1d,
            MotionY = animation.Contains("jump", StringComparison.OrdinalIgnoreCase) ? 0.2d : 0d,
            HasRenderState = true,
            OnGround = !swimming,
            FeetInLiquid = swimming,
            Swimming = swimming,
            HasAgentState = true,
            HasPlayerState = true,
            HasPlayerPostureState = true,
            Animations = new List<string> { animation },
            AnimationStates = new List<DirectorReplayAnimationSample>
            {
                new()
                {
                    Code = animation,
                    StateCode = animation,
                    Progress = progress,
                    Weight = 1f,
                    BlendedWeight = 1f,
                    EasingFactor = 1f,
                    ElementWeights = new Dictionary<string, float> { ["UpperArmR"] = 100f }
                }
            }
        };

    private static void ReplayPauseResyncComparesFinalBlockState()
    {
        Check.True(DirectorReplayBlockStateComparison.IsEquivalent(
            101,
            0,
            expectedBlockEntityKnown: true,
            string.Empty,
            Array.Empty<byte>(),
            101,
            0,
            string.Empty,
            Array.Empty<byte>()));
        Check.False(DirectorReplayBlockStateComparison.IsEquivalent(
            101,
            0,
            expectedBlockEntityKnown: true,
            string.Empty,
            Array.Empty<byte>(),
            0,
            0,
            string.Empty,
            Array.Empty<byte>()));
        Check.False(DirectorReplayBlockStateComparison.IsEquivalent(
            101,
            0,
            expectedBlockEntityKnown: true,
            "chest",
            new byte[] { 1, 2 },
            101,
            0,
            "chest",
            new byte[] { 1, 3 }));
        Check.False(DirectorReplayBlockStateComparison.IsEquivalent(
            101,
            0,
            expectedBlockEntityKnown: false,
            string.Empty,
            Array.Empty<byte>(),
            101,
            0,
            string.Empty,
            Array.Empty<byte>()));
    }

    private static void QuickReplayPolicySkipsWorldReconstruction()
    {
        DirectorReplayDocument document = new()
        {
            WorldSnapshot = new DirectorReplayWorldSnapshot
            {
                Chunks = new List<DirectorReplayChunkSnapshot>
                {
                    new() { ChunkX = 80, ChunkY = 0, ChunkZ = 90 }
                }
            },
            BlockEvents = new List<DirectorReplayBlockEvent> { new() }
        };
        Check.True(DirectorReplayPlaybackPolicy.ShouldRebuildWorld(document, quickMode: false, applyWorldSnapshot: true));
        Check.False(DirectorReplayPlaybackPolicy.ShouldRebuildWorld(document, quickMode: true, applyWorldSnapshot: true));
        Check.False(DirectorReplayPlaybackPolicy.ShouldRebuildWorld(document, quickMode: false, applyWorldSnapshot: false));
        Check.True(DirectorReplayPlaybackPolicy.HasNonActorContent(document, quickMode: false));
        Check.False(DirectorReplayPlaybackPolicy.HasNonActorContent(document, quickMode: true));

        DirectorReplayPreloadColumn snapshotColumn = new(80, 90, 0);
        DirectorReplayPreloadPlan normalPlan = DirectorReplayPreloadPlanner.Build(
            document,
            preferredPlayerUid: null,
            includeWorldSnapshotColumns: DirectorReplayPlaybackPolicy.ShouldRebuildWorld(
                document,
                quickMode: false,
                applyWorldSnapshot: true));
        DirectorReplayPreloadPlan quickPlan = DirectorReplayPreloadPlanner.Build(
            document,
            preferredPlayerUid: null,
            includeWorldSnapshotColumns: DirectorReplayPlaybackPolicy.ShouldRebuildWorld(
                document,
                quickMode: true,
                applyWorldSnapshot: true));
        DirectorReplayPreloadPlan disabledPlan = DirectorReplayPreloadPlanner.Build(
            document,
            preferredPlayerUid: null,
            includeWorldSnapshotColumns: DirectorReplayPlaybackPolicy.ShouldRebuildWorld(
                document,
                quickMode: false,
                applyWorldSnapshot: false));
        Check.True(normalPlan.AutomaticColumnSet.Contains(snapshotColumn));
        Check.False(quickPlan.AutomaticColumnSet.Contains(snapshotColumn));
        Check.False(disabledPlan.AutomaticColumnSet.Contains(snapshotColumn));

        document.ParticleEvents.Add(new DirectorReplayParticleEvent());
        Check.True(DirectorReplayPlaybackPolicy.HasNonActorContent(document, quickMode: true));
    }

    private static void ReplayActorAnimationForcingRespectsCurrentView()
    {
        Check.False(DirectorReplayPlaybackPolicy.ShouldForceActorAnimations(
            backwards: false,
            becameVisible: false,
            animatorReplaced: false,
            inCurrentView: false,
            wasInCurrentView: false));
        Check.True(DirectorReplayPlaybackPolicy.ShouldForceActorAnimations(
            backwards: false,
            becameVisible: false,
            animatorReplaced: false,
            inCurrentView: true,
            wasInCurrentView: false));
        Check.False(DirectorReplayPlaybackPolicy.ShouldForceActorAnimations(
            backwards: false,
            becameVisible: false,
            animatorReplaced: false,
            inCurrentView: true,
            wasInCurrentView: true));
        Check.False(DirectorReplayPlaybackPolicy.ShouldForceActorAnimations(
            backwards: false,
            becameVisible: false,
            animatorReplaced: false,
            inCurrentView: false,
            wasInCurrentView: true));
        Check.True(DirectorReplayPlaybackPolicy.ShouldForceActorAnimations(
            backwards: true,
            becameVisible: false,
            animatorReplaced: false,
            inCurrentView: false,
            wasInCurrentView: false));
        Check.True(DirectorReplayPlaybackPolicy.ShouldForceActorAnimations(
            backwards: false,
            becameVisible: true,
            animatorReplaced: false,
            inCurrentView: false,
            wasInCurrentView: false));
        Check.True(DirectorReplayPlaybackPolicy.ShouldForceActorAnimations(
            backwards: false,
            becameVisible: false,
            animatorReplaced: true,
            inCurrentView: false,
            wasInCurrentView: false));

        Check.False(DirectorReplayPlaybackPolicy.ShouldResetActorAnimationManager(
            forceAnimations: false,
            hasExactRecordedStates: false));
        Check.False(DirectorReplayPlaybackPolicy.ShouldResetActorAnimationManager(
            forceAnimations: true,
            hasExactRecordedStates: true));
        Check.True(DirectorReplayPlaybackPolicy.ShouldResetActorAnimationManager(
            forceAnimations: true,
            hasExactRecordedStates: false));
    }

    private static void ReplayRecordingReservesTransientActors()
    {
        Check.Equal(
            0,
            DirectorReplayActorSchedulingPolicy.ResolveTransientActorReserve(1));
        Check.Equal(
            1,
            DirectorReplayActorSchedulingPolicy.ResolveTransientActorReserve(2));
        Check.Equal(
            16,
            DirectorReplayActorSchedulingPolicy.ResolveTransientActorReserve(64));
        Check.Equal(
            DirectorReplayActorSchedulingPolicy.MaximumReservedTransientActors,
            DirectorReplayActorSchedulingPolicy.ResolveTransientActorReserve(2048));

        Check.True(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
            existingActors: 191,
            existingTransientActors: 0,
            maximumActors: 256,
            canUseTransientReserve: false));
        Check.False(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
            existingActors: 192,
            existingTransientActors: 0,
            maximumActors: 256,
            canUseTransientReserve: false));
        Check.True(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
            existingActors: 200,
            existingTransientActors: 20,
            maximumActors: 256,
            canUseTransientReserve: false));
        Check.True(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
            existingActors: 255,
            existingTransientActors: 64,
            maximumActors: 256,
            canUseTransientReserve: true));
        Check.False(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
            existingActors: 256,
            existingTransientActors: 64,
            maximumActors: 256,
            canUseTransientReserve: true));
    }

    private static void ReplayRecordingReleasesHistoricalActorSlots()
    {
        HashSet<string> active = new(StringComparer.Ordinal);
        Dictionary<string, string> transientTypes = new(StringComparer.Ordinal);
        Dictionary<string, DirectorReplayActorTrack> history = new(StringComparer.Ordinal);

        // Travel through successive areas, then spawn animals. Historical
        // actors must not exhaust either the stable or the transient budget.
        for (int wave = 0; wave < 12; wave++)
        {
            for (int index = 0; index < 256; index++)
            {
                bool transient = index >= 192;
                if (index == 192)
                {
                    Check.False(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
                        active, transientTypes, 256, canUseTransientReserve: false));
                }
                Check.True(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
                    active, transientTypes, 256, canUseTransientReserve: transient));
                string key = $"entity:{wave * 256 + index}";
                active.Add(key);
                if (transient) transientTypes.Add(key, "EntityItem");
                history.Add(key, new DirectorReplayActorTrack
                {
                    ActorKey = key,
                    EntityCode = transient ? "item" : "deer-caribou-baby-female",
                    Samples = new() { new() { TimeSeconds = wave, X = index } }
                });
            }
            Check.False(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
                active, transientTypes, 256, canUseTransientReserve: true));
            // Despawn/unload releases slots, while the old tracks and type
            // registry remain available for playback and transient reuse.
            active.Clear();
        }

        Check.Equal(3072, history.Count);
        Check.Equal(768, transientTypes.Count);
        Check.NearlyEqual(0d, history["entity:0"].Samples[0].TimeSeconds);
        Check.NearlyEqual(11d, history["entity:3000"].Samples[0].TimeSeconds);

        // Old transient tracks must not subtract from today's stable count
        // and accidentally let ordinary creatures consume the reserve.
        foreach (string key in history.Keys.Take(192)) active.Add(key);
        Check.False(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
            active, transientTypes, 256, canUseTransientReserve: false));
        active.Remove("entity:0");
        Check.True(DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
            active, transientTypes, 256, canUseTransientReserve: false));
    }

    private static void ReplayActorSchedulingUsesDistanceIntervals()
    {
        Check.Equal(
            1,
            DirectorReplayActorSchedulingPolicy.DistanceUpdateInterval(0d));
        Check.Equal(
            1,
            DirectorReplayActorSchedulingPolicy.DistanceUpdateInterval(16d * 16d));
        Check.Equal(
            2,
            DirectorReplayActorSchedulingPolicy.DistanceUpdateInterval(16d * 16d + 0.001d));
        Check.Equal(
            2,
            DirectorReplayActorSchedulingPolicy.DistanceUpdateInterval(32d * 32d));
        Check.Equal(
            4,
            DirectorReplayActorSchedulingPolicy.DistanceUpdateInterval(32d * 32d + 0.001d));
        Check.Equal(
            1,
            DirectorReplayActorSchedulingPolicy.DistanceUpdateInterval(-10d));
        Check.Equal(
            4,
            DirectorReplayActorSchedulingPolicy.DistanceUpdateInterval(double.NaN));

        Check.True(DirectorReplayActorSchedulingPolicy.IsAnimationDue(
            frame: 10L,
            lastAnimationUpdateFrame: 9L,
            distanceSquared: 0d));
        Check.False(DirectorReplayActorSchedulingPolicy.IsAnimationDue(
            frame: 10L,
            lastAnimationUpdateFrame: 9L,
            distanceSquared: 10_000d));
        Check.True(DirectorReplayActorSchedulingPolicy.IsAnimationDue(
            frame: 10L,
            lastAnimationUpdateFrame: 12L,
            distanceSquared: 10_000d));
    }

    private static void ReplayActorSchedulingForcesFirstVisibleUpdates()
    {
        bool force = DirectorReplayActorSchedulingPolicy.ShouldForceAnimation(
            backwards: false,
            becameVisible: false,
            animatorReplaced: false,
            inCurrentView: true,
            wasInCurrentView: false);
        Check.True(force);

        DirectorReplayActorSchedulingRequest[] requests =
        {
            new(
                Eligible: true,
                Force: false,
                LastAnimationUpdateFrame: 0L,
                DistanceSquared: 10_000d),
            new(
                Eligible: true,
                Force: force,
                LastAnimationUpdateFrame: 9_999L,
                DistanceSquared: 10_000d),
            new(
                Eligible: true,
                Force: true,
                LastAnimationUpdateFrame: -1L,
                DistanceSquared: 0d),
            new(
                Eligible: false,
                Force: true,
                LastAnimationUpdateFrame: -1L,
                DistanceSquared: 0d)
        };

        DirectorReplayActorSchedulingResult result =
            DirectorReplayActorSchedulingPolicy.SelectAnimationUpdates(
                requests,
                frame: 10L,
                cursor: 0,
                mode: DirectorReplayActorSchedulingMode.Preview,
                budgetOverride: 1);
        Check.SequenceEqual(new[] { 1 }, result.SelectedIndices);
        Check.Equal(1, result.NextCursor);
    }

    private static void ReplayActorSchedulingResolvesModeBudgets()
    {
        Check.Equal(
            DirectorReplayActorSchedulingPolicy.PreviewAnimationBudget,
            DirectorReplayActorSchedulingPolicy.ResolveAnimationBudget(
                DirectorReplayActorSchedulingMode.Preview,
                actorCount: 100));
        Check.Equal(
            DirectorReplayActorSchedulingPolicy.DedicatedAnimationBudget,
            DirectorReplayActorSchedulingPolicy.ResolveAnimationBudget(
                DirectorReplayActorSchedulingMode.Dedicated,
                actorCount: 100));
        Check.Equal(
            5,
            DirectorReplayActorSchedulingPolicy.ResolveAnimationBudget(
                DirectorReplayActorSchedulingMode.Dedicated,
                actorCount: 5));
        Check.Equal(
            0,
            DirectorReplayActorSchedulingPolicy.ResolveAnimationBudget(
                DirectorReplayActorSchedulingMode.Preview,
                actorCount: -1));
        Check.Equal(
            7,
            DirectorReplayActorSchedulingPolicy.ResolveAnimationBudget(
                DirectorReplayActorSchedulingMode.Preview,
                actorCount: 20,
                previewBudget: 7,
                dedicatedBudget: 11));
        Check.Equal(
            11,
            DirectorReplayActorSchedulingPolicy.ResolveAnimationBudget(
                DirectorReplayActorSchedulingMode.Dedicated,
                actorCount: 20,
                previewBudget: 7,
                dedicatedBudget: 11));

        DirectorReplayActorSchedulingRequest[] requests = Enumerable
            .Repeat(
                new DirectorReplayActorSchedulingRequest(
                    Eligible: true,
                    Force: false,
                    LastAnimationUpdateFrame: -1L,
                    DistanceSquared: 0d),
                40)
            .ToArray();
        DirectorReplayActorSchedulingResult preview =
            DirectorReplayActorSchedulingPolicy.SelectAnimationUpdates(
                requests,
                frame: 0L,
                cursor: 0,
                mode: DirectorReplayActorSchedulingMode.Preview);
        DirectorReplayActorSchedulingResult dedicated =
            DirectorReplayActorSchedulingPolicy.SelectAnimationUpdates(
                requests,
                frame: 0L,
                cursor: 0,
                mode: DirectorReplayActorSchedulingMode.Dedicated);
        Check.Equal(16, preview.SelectedIndices.Count);
        Check.Equal(32, dedicated.SelectedIndices.Count);
    }

    private static void ReplayActorSchedulingAdaptsToRenderTime()
    {
        Check.Equal(
            28,
            DirectorReplayActorSchedulingPolicy.AdaptAnimationBudget(
                previousBudget: 32,
                elapsedMilliseconds: 8d,
                updatedCount: 32));
        Check.Equal(
            36,
            DirectorReplayActorSchedulingPolicy.AdaptAnimationBudget(
                previousBudget: 32,
                elapsedMilliseconds: 1d,
                updatedCount: 32));
        Check.Equal(
            DirectorReplayActorSchedulingPolicy.DedicatedMinimumAnimationBudget,
            DirectorReplayActorSchedulingPolicy.AdaptAnimationBudget(
                previousBudget: 16,
                elapsedMilliseconds: 100d,
                updatedCount: 16));
        Check.Equal(
            DirectorReplayActorSchedulingPolicy.DedicatedMaximumAnimationBudget,
            DirectorReplayActorSchedulingPolicy.AdaptAnimationBudget(
                previousBudget: 64,
                elapsedMilliseconds: 0.01d,
                updatedCount: 64));
        Check.Equal(
            32,
            DirectorReplayActorSchedulingPolicy.AdaptAnimationBudget(
                previousBudget: 32,
                elapsedMilliseconds: double.NaN,
                updatedCount: 32));
        Check.Equal(
            32,
            DirectorReplayActorSchedulingPolicy.AdaptAnimationBudget(
                previousBudget: 32,
                elapsedMilliseconds: 1d,
                updatedCount: 1));
        Check.Equal(
            32,
            DirectorReplayActorSchedulingPolicy.AdaptAnimationBudget(
                previousBudget: 32,
                elapsedMilliseconds: 1d,
                updatedCount: 0));

        DirectorReplayActorSchedulingRequest[] mixedRequests =
            Enumerable.Repeat(
                new DirectorReplayActorSchedulingRequest(
                    Eligible: true,
                    Force: true,
                    LastAnimationUpdateFrame: -1L,
                    DistanceSquared: 0d),
                8)
            .Concat(Enumerable.Repeat(
                new DirectorReplayActorSchedulingRequest(
                    Eligible: true,
                    Force: false,
                    LastAnimationUpdateFrame: -1L,
                    DistanceSquared: 0d),
                8))
            .ToArray();
        DirectorReplayActorSchedulingResult mixed =
            DirectorReplayActorSchedulingPolicy.SelectAnimationUpdates(
                mixedRequests,
                frame: 0L,
                cursor: 0,
                mode: DirectorReplayActorSchedulingMode.Preview,
                budgetOverride: 8);
        Check.Equal(6, mixed.SelectedIndices.Count(index => index < 8));
        Check.Equal(2, mixed.SelectedIndices.Count(index => index >= 8));
    }

    private static void ReplayActorSchedulingKeepsFairCursor()
    {
        DirectorReplayActorSchedulingRequest[] requests = Enumerable
            .Repeat(
                new DirectorReplayActorSchedulingRequest(
                    Eligible: true,
                    Force: false,
                    LastAnimationUpdateFrame: -1L,
                    DistanceSquared: 0d),
                5)
            .ToArray();

        DirectorReplayActorSchedulingResult first =
            DirectorReplayActorSchedulingPolicy.SelectAnimationUpdates(
                requests,
                frame: 0L,
                cursor: 0,
                mode: DirectorReplayActorSchedulingMode.Preview,
                budgetOverride: 2);
        Check.SequenceEqual(new[] { 0, 1 }, first.SelectedIndices);
        Check.Equal(2, first.NextCursor);

        DirectorReplayActorSchedulingResult second =
            DirectorReplayActorSchedulingPolicy.SelectAnimationUpdates(
                requests,
                frame: 0L,
                cursor: first.NextCursor,
                mode: DirectorReplayActorSchedulingMode.Preview,
                budgetOverride: 2);
        Check.SequenceEqual(new[] { 2, 3 }, second.SelectedIndices);
        Check.Equal(4, second.NextCursor);

        DirectorReplayActorSchedulingResult third =
            DirectorReplayActorSchedulingPolicy.SelectAnimationUpdates(
                requests,
                frame: 0L,
                cursor: second.NextCursor,
                mode: DirectorReplayActorSchedulingMode.Preview,
                budgetOverride: 2);
        Check.SequenceEqual(new[] { 4, 0 }, third.SelectedIndices);
        Check.Equal(1, third.NextCursor);

        DirectorReplayActorSchedulingResult fourth =
            DirectorReplayActorSchedulingPolicy.SelectAnimationUpdates(
                requests,
                frame: 0L,
                cursor: third.NextCursor,
                mode: DirectorReplayActorSchedulingMode.Preview,
                budgetOverride: 2);
        Check.SequenceEqual(new[] { 1, 2 }, fourth.SelectedIndices);
    }

    private static void ReplayActorWarmupRemainsBounded()
    {
        Check.Equal(
            0,
            DirectorReplayActorSchedulingPolicy.ResolveWarmupBudget(0));
        Check.Equal(
            0,
            DirectorReplayActorSchedulingPolicy.ResolveWarmupBudget(-4));
        Check.Equal(
            4,
            DirectorReplayActorSchedulingPolicy.ResolveWarmupBudget(16));
        Check.Equal(
            8,
            DirectorReplayActorSchedulingPolicy.ResolveWarmupBudget(32));
        Check.Equal(
            8,
            DirectorReplayActorSchedulingPolicy.ResolveWarmupBudget(64));

        Check.Equal(
            1L,
            DirectorReplayActorSchedulingPolicy.WarmupRetryDelayFrames(1));
        Check.Equal(
            2L,
            DirectorReplayActorSchedulingPolicy.WarmupRetryDelayFrames(2));
        Check.Equal(
            4L,
            DirectorReplayActorSchedulingPolicy.WarmupRetryDelayFrames(3));
        Check.Equal(
            8L,
            DirectorReplayActorSchedulingPolicy.WarmupRetryDelayFrames(4));
        Check.Equal(
            8L,
            DirectorReplayActorSchedulingPolicy.WarmupRetryDelayFrames(20));
        Check.Equal(
            0L,
            DirectorReplayActorSchedulingPolicy.WarmupRetryDelayFrames(0));

        Check.False(
            DirectorReplayActorSchedulingPolicy.ShouldFallbackAfterWarmupFailure(2));
        Check.False(
            DirectorReplayActorSchedulingPolicy.ShouldFallbackAfterWarmupFailure(5));
        Check.True(
            DirectorReplayActorSchedulingPolicy.ShouldFallbackAfterWarmupFailure(6));
        Check.Equal(
            30L,
            DirectorReplayActorSchedulingPolicy.DedicatedMaximumWarmupRetryDelayFrames);
        Check.Equal(
            2d,
            DirectorReplayActorSchedulingPolicy.DedicatedWarmupWorkBudgetMilliseconds);
    }

    private static void ReplayPlaybackDeltaStaysBounded()
    {
        Check.NearlyEqual(
            1d / 60d,
            DirectorReplayPlaybackPolicy.ClampPlaybackDelta(1d / 60d, 0.1d),
            0.0000001d);
        Check.NearlyEqual(
            0.1d,
            DirectorReplayPlaybackPolicy.ClampPlaybackDelta(1d, 0.1d),
            0.0000001d);
        Check.Equal(
            0d,
            DirectorReplayPlaybackPolicy.ClampPlaybackDelta(double.NaN, 0.1d));
        Check.Equal(
            0d,
            DirectorReplayPlaybackPolicy.ClampPlaybackDelta(-1d, 0.1d));
    }

    private static void ReplayPlaybackRestartsAfterReachingEnd()
    {
        Check.True(DirectorReplayPlaybackPolicy.ShouldRestartFromEnd(true, 10d, 10d));
        Check.True(DirectorReplayPlaybackPolicy.ShouldRestartFromEnd(true, 10.0001d, 10d));
        Check.False(DirectorReplayPlaybackPolicy.ShouldRestartFromEnd(false, 10d, 10d));
        Check.False(DirectorReplayPlaybackPolicy.ShouldRestartFromEnd(true, 9.99d, 10d));
        Check.False(DirectorReplayPlaybackPolicy.ShouldRestartFromEnd(true, double.NaN, 10d));
        Check.False(DirectorReplayPlaybackPolicy.ShouldRestartFromEnd(true, 10d, double.NaN));
    }

    private static void TimelineSubpixelZoomStaysAligned()
    {
        const double pixelsPerTick = 2d / 3d;
        const double contentLeft = 280d;
        const double scrollTick = 450d;
        const double expectedTick = 1830d;
        double scale = DirectorTimelineCoordinateMath.PositivePixelsPerTick(pixelsPerTick);
        double playheadX = contentLeft + (expectedTick - scrollTick) * scale;
        double mouseTick = scrollTick + (playheadX - contentLeft) / scale;

        Check.NearlyEqual(pixelsPerTick, scale);
        Check.NearlyEqual(expectedTick, mouseTick);
        Check.True(DirectorTimelineCoordinateMath.PositivePixelsPerTick(0d) > 0d);
        Check.True(DirectorTimelineCoordinateMath.PositivePixelsPerTick(double.NaN) > 0d);
    }

    private static void AutomaticReplayHandoffWaitPolicy()
    {
        Check.True(DirectorReplayHandoffPolicy.MustWaitForReplaySave(
            recordReplayWithVideo: true,
            isLiveWorkflow: false));
        Check.False(DirectorReplayHandoffPolicy.MustWaitForReplaySave(
            recordReplayWithVideo: false,
            isLiveWorkflow: false));
        Check.False(DirectorReplayHandoffPolicy.MustWaitForReplaySave(
            recordReplayWithVideo: true,
            isLiveWorkflow: true));
    }

    private static void ReplayCompressionProfilesRoundTrip()
    {
        Check.Equal(System.IO.Compression.CompressionLevel.Fastest,
            DirectorReplayIO.ResolveCompressionLevel(DirectorReplayCompressionProfile.Fastest));
        Check.Equal(System.IO.Compression.CompressionLevel.Optimal,
            DirectorReplayIO.ResolveCompressionLevel(DirectorReplayCompressionProfile.Balanced));
        Check.Equal(System.IO.Compression.CompressionLevel.SmallestSize,
            DirectorReplayIO.ResolveCompressionLevel(DirectorReplayCompressionProfile.Smallest));
        Check.Equal(System.IO.Compression.CompressionLevel.Optimal,
            DirectorReplayIO.ResolveCompressionLevel((DirectorReplayCompressionProfile)999));

        string root = Path.Combine(Path.GetTempPath(), "vsdirector-replay-compression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DirectorReplayDocument source = new()
            {
                Id = Guid.NewGuid(),
                Name = "Compression",
                WorldId = "world-compression",
                DurationSeconds = 2d,
                Actors = new List<DirectorReplayActorTrack>
                {
                    new()
                    {
                        ActorKey = "entity:1",
                        EntityId = 1,
                        Samples = new List<DirectorReplayActorSample>
                        {
                            new() { TimeSeconds = 0d },
                            new() { TimeSeconds = 2d, X = 4d }
                        }
                    }
                }
            };
            foreach (DirectorReplayCompressionProfile profile in Enum.GetValues<DirectorReplayCompressionProfile>())
            {
                string path = Path.Combine(root, profile + ".vsdreplay.gz");
                DirectorReplayIO.Save(path, source, profile);
                DirectorReplayDocument loaded = DirectorReplayIO.Load(path);
                Check.Equal(source.Id, loaded.Id);
                Check.NearlyEqual(2d, loaded.DurationSeconds);
            }
            Check.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VoicePcmAndReplayFramesWork()
    {
        short[] source = Enumerable.Range(0, 240).Select(index => (short)(index * 100)).ToArray();
        short[] resampled = DirectorVoicePcm.ResampleTo48Khz(source, 24000);
        Check.Equal(480, resampled.Length);
        byte[] bytes = DirectorVoicePcm.ToLittleEndianBytes(resampled);
        Check.SequenceEqual(resampled, DirectorVoicePcm.FromLittleEndianBytes(bytes));

        DirectorReplayDocument normalized = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            VoiceFrames = new List<DirectorReplayVoiceFrame>
            {
                new()
                {
                    TimeSeconds = -1d,
                    SpeakerId = "speaker-1",
                    SpeakerName = "Speaker One",
                    X = double.NaN,
                    Volume = 8f,
                    Spatialized = false,
                    Pcm16 = bytes
                }
            }
        });
        Check.Equal(1, normalized.VoiceFrames.Count);
        Check.Equal(0d, normalized.VoiceFrames[0].TimeSeconds);
        Check.Equal(4f, normalized.VoiceFrames[0].Volume);
        Check.False(normalized.VoiceFrames[0].Spatialized);
        DirectorReplayElementTrack voiceTrack = normalized.ViewportTimeline.Tracks.Single(track =>
            track.TrackType == DirectorReplayElementTrackType.VoiceAudio);
        Check.Equal("speaker-1", voiceTrack.SpeakerId);
        Check.Equal("Speaker One", voiceTrack.Name);
        normalized.ViewportTimeline.ElementKeyframes.Add(new DirectorReplayElementKeyframe
        {
            TrackId = voiceTrack.Id,
            TrackType = DirectorReplayElementTrackType.VoiceAudio,
            TimeSeconds = 1d,
            Volume = 0.5f,
            InterpolationToNext = DirectorReplayElementInterpolation.Hold
        });
        Check.True(DirectorReplayVoiceTrackPolicy.TryResolveGain(
            normalized.ViewportTimeline,
            normalized.VoiceFrames[0],
            0.5d,
            out float initialVoiceGain));
        Check.Equal(1f, initialVoiceGain);
        Check.True(DirectorReplayVoiceTrackPolicy.TryResolveGain(
            normalized.ViewportTimeline,
            normalized.VoiceFrames[0],
            1d,
            out float keyedVoiceGain));
        Check.Equal(0.5f, keyedVoiceGain);
        Check.False(DirectorReplayViewportTimeline.RemoveTrack(
            normalized.ViewportTimeline,
            voiceTrack.Id));
        voiceTrack.Enabled = false;
        Check.False(DirectorReplayVoiceTrackPolicy.TryResolveGain(
            normalized.ViewportTimeline,
            normalized.VoiceFrames[0],
            1d,
            out _));
        DirectorReplayDocument clone = normalized.DeepClone();
        clone.VoiceFrames[0].Pcm16[0] = 99;
        Check.True(normalized.VoiceFrames[0].Pcm16[0] != 99);

        DirectorConfig config = new()
        {
            ReplayCaptureVoiceModAudio = true
        };
        DirectorVoiceBridge bridge = new();
        DirectorVoicePcmFrame? accepted = null;
        bridge.Configure(
            () => config,
            () => true,
            (frame, _, _) => accepted = frame);
        using DirectorVoiceSource speaker = bridge.Register("test", "speaker", "Speaker Name");
        DirectorVoiceSpatialization spatialization = new(
            new DirectorVoicePosition(4d, 5d, 6d, 2),
            18f,
            3f,
            1.6f);
        Check.Equal(
            DirectorVoiceSubmitResult.Accepted,
            speaker.SubmitPcm16(source, 24000, spatialization, 1234L));
        bridge.Drain();
        Check.True(accepted is not null);
        Check.Equal(1234L, accepted!.TimestampMilliseconds);
        Check.Equal("Speaker Name", accepted.SpeakerName);
        Check.Equal(spatialization, accepted.Spatialization);
        Check.Equal(480, accepted.Samples.Length);

        DirectorReplayVoiceTimestampTimeline timestamps = new();
        Guid replayId = Guid.NewGuid();
        Check.NearlyEqual(0.1d, timestamps.Resolve(replayId, "speaker", 0.1d, 960));
        Check.NearlyEqual(0.12d, timestamps.Resolve(replayId, "speaker", 0.1d, 960));
        Check.NearlyEqual(0.14d, timestamps.Resolve(replayId, "speaker", 0.119d, 960));
        Check.NearlyEqual(0.1d, timestamps.Resolve(replayId, "another-speaker", 0.1d, 960));
        Check.NearlyEqual(0d, timestamps.Resolve(Guid.NewGuid(), "speaker", 0d, 960));
        Guid variablePackets = Guid.NewGuid();
        Check.NearlyEqual(0d, timestamps.Resolve(variablePackets, "speaker", 0d, 960));
        Check.NearlyEqual(.02d, timestamps.Resolve(variablePackets, "speaker", 0d, 1920));
        Check.NearlyEqual(.06d, timestamps.Resolve(variablePackets, "speaker", 0d, 480));

        DirectorVoiceSpatialization direct = DirectorVoiceSpatialization.Direct(
            new DirectorVoicePosition(7d, 8d, 9d, 2));
        Check.False(direct.IsSpatialized);
        Check.Equal(1f, direct.ResolveGain(10_000d));

        DirectorVoicePosition captureCenter = new(16d, 0d, 16d, 0);
        Check.True(DirectorVoiceCaptureRegion.Contains(
            new DirectorVoicePosition(159d, 20d, 159d, 0),
            captureCenter,
            4));
        Check.False(DirectorVoiceCaptureRegion.Contains(
            new DirectorVoicePosition(160d, 20d, 160d, 0),
            captureCenter,
            4));
        Check.False(DirectorVoiceCaptureRegion.Contains(
            new DirectorVoicePosition(16d, 20d, 16d, 1),
            captureCenter,
            4));
    }

    private static void GzipReplayRoundTripWorks()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vsdirector-replay-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "take.vsdreplay.gz");
            DirectorReplayDocument source = new()
            {
                Name = "演员测试",
                WorldId = "world-alpha",
                ParticleCaptureVersion = DirectorReplayDocument.CurrentParticleCaptureVersion,
                ClockSamples = new List<DirectorReplayClockSample>
                {
                    new() { TimeSeconds = 0d, TotalHours = 48d, CalendarSpeedMul = 0.5f }
                },
                BlockEvents = new List<DirectorReplayBlockEvent>
                {
                    new() { TimeSeconds = 0.5d, X = 1, Y = 2, Z = 3, SolidBlockId = 4, BlockEntityCaptured = true }
                },
                ParticleEvents = new List<DirectorReplayParticleEvent>
                {
                    new() { TimeSeconds = 0.75d, Kind = DirectorReplayParticleKind.Provider, ProviderType = "test", ProviderData = new byte[] { 3, 4 } },
                    new() { TimeSeconds = 0.75d, Kind = DirectorReplayParticleKind.Provider, ProviderType = "test", ProviderData = new byte[] { 3, 4 } }
                },
                SoundEvents = new List<DirectorReplaySoundEvent>
                {
                    new()
                    {
                        TimeSeconds = 1d,
                        Location = "game:sounds/test",
                        InstanceId = 42L,
                        Action = DirectorReplaySoundAction.Update,
                        RelativePosition = true,
                        ShouldLoop = true,
                        PlaybackPosition = 0.25f,
                        SoundLengthSeconds = 3.5f,
                        Volume = 0.5f,
                        Pitch = 1f,
                        PitchOffset = -0.1f,
                        Range = 16f,
                        ReferenceDistance = 2f,
                        LowPassFilter = 0.7f,
                        ReverbDecayTime = 0.4f
                    }
                },
                Actors = new List<DirectorReplayActorTrack>
                {
                    new()
                    {
                        ActorKey = "player:actor",
                        EntityId = 42,
                        PlayerUid = "actor",
                        EntityCode = "game:player",
                        InitialState = new byte[] { 1, 2 },
                        Samples = new List<DirectorReplayActorSample>
                        {
                            new()
                            {
                                TimeSeconds = 1.25d,
                                X = 4d,
                                Y = 5d,
                                Z = 6d,
                                HasMotion = true,
                                MotionX = 1.5d,
                                HasItemRenderState = true,
                                ItemRenderElapsedMilliseconds = 123_456L,
                                ItemTouchGroundAgeMilliseconds = 789L,
                                ItemScaleRandom = 0.015f,
                                ItemYawRandom = 0.75f,
                                ItemRenderXAngle = 12f,
                                ItemRenderYAngle = 34f,
                                ItemRenderZAngle = 56f,
                                ItemRenderAccumulator = 0.02f,
                                HasPlayerState = true,
                                Controls = 32,
                                ServerControls = 64,
                                HandUse = 1,
                                ServerHandUse = 2,
                                RightHandItem = new byte[] { 8, 9 },
                                LeftHandItem = new byte[] { 10, 11 },
                                EquipmentItems = new List<byte[]>
                                {
                                    new byte[] { 12, 13 },
                                    Array.Empty<byte>()
                                },
                                BackpackItems = new List<byte[]> { new byte[] { 14, 15 } },
                                Animations = new List<string> { "walk" },
                                AnimationStates = new List<DirectorReplayAnimationSample>
                                {
                                    new()
                                    {
                                        Code = "walk",
                                        StateCode = "walk-animation",
                                        Progress = 0.625f,
                                        AnimationSpeed = 1.25f
                                    }
                                }
                            }
                        }
                    }
                }
            };
            DirectorReplayIO.Save(path, source);
            byte[] header = File.ReadAllBytes(path)[..2];
            Check.SequenceEqual(new byte[] { 0x1f, 0x8b }, header);
            DirectorReplayDocument loaded = DirectorReplayIO.Load(path);
            Check.Equal("演员测试", loaded.Name);
            Check.Equal("world-alpha", loaded.WorldId);
            Check.Equal(
                DirectorReplayDocument.CurrentParticleCaptureVersion,
                loaded.ParticleCaptureVersion);
            Check.Equal(DirectorReplayDocument.ClientActorCaptureScope, loaded.CaptureScope);
            Check.False(loaded.IncludesWorldSnapshot);
            Check.Equal(1, loaded.Actors.Count);
            Check.Equal(42L, loaded.Actors[0].EntityId);
            Check.SequenceEqual(new byte[] { 1, 2 }, loaded.Actors[0].InitialState);
            Check.True(loaded.Actors[0].Samples[0].HasMotion);
            Check.NearlyEqual(1.5d, loaded.Actors[0].Samples[0].MotionX);
            Check.True(loaded.Actors[0].Samples[0].HasItemRenderState);
            Check.Equal(123_456L, loaded.Actors[0].Samples[0].ItemRenderElapsedMilliseconds);
            Check.Equal(789L, loaded.Actors[0].Samples[0].ItemTouchGroundAgeMilliseconds);
            Check.NearlyEqual(0.015d, loaded.Actors[0].Samples[0].ItemScaleRandom, 0.0001d);
            Check.NearlyEqual(0.75d, loaded.Actors[0].Samples[0].ItemYawRandom, 0.0001d);
            Check.NearlyEqual(12d, loaded.Actors[0].Samples[0].ItemRenderXAngle, 0.0001d);
            Check.NearlyEqual(34d, loaded.Actors[0].Samples[0].ItemRenderYAngle, 0.0001d);
            Check.NearlyEqual(56d, loaded.Actors[0].Samples[0].ItemRenderZAngle, 0.0001d);
            Check.True(loaded.Actors[0].Samples[0].HasPlayerState);
            Check.Equal(32, loaded.Actors[0].Samples[0].Controls);
            Check.Equal(2, loaded.Actors[0].Samples[0].ServerHandUse);
            Check.SequenceEqual(new byte[] { 8, 9 }, loaded.Actors[0].Samples[0].RightHandItem);
            Check.SequenceEqual(new byte[] { 10, 11 }, loaded.Actors[0].Samples[0].LeftHandItem);
            Check.SequenceEqual(new byte[] { 12, 13 }, loaded.Actors[0].Samples[0].EquipmentItems[0]);
            Check.Empty(loaded.Actors[0].Samples[0].EquipmentItems[1]);
            Check.SequenceEqual(new byte[] { 14, 15 }, loaded.Actors[0].Samples[0].BackpackItems[0]);
            Check.NearlyEqual(0.625d, loaded.Actors[0].Samples[0].AnimationStates[0].Progress);
            Check.Equal("walk-animation", loaded.Actors[0].Samples[0].AnimationStates[0].StateCode);
            Check.True(loaded.BlockEvents[0].BlockEntityCaptured);
            Check.Equal(1, loaded.ParticleEvents.Count);
            Check.SequenceEqual(new byte[] { 3, 4 }, loaded.ParticleEvents[0].ProviderData);
            Check.Equal("game:sounds/test", loaded.SoundEvents[0].Location);
            Check.Equal(42L, loaded.SoundEvents[0].InstanceId);
            Check.Equal(DirectorReplaySoundAction.Update, loaded.SoundEvents[0].Action);
            Check.True(loaded.SoundEvents[0].RelativePosition);
            Check.True(loaded.SoundEvents[0].ShouldLoop);
            Check.NearlyEqual(0.25d, loaded.SoundEvents[0].PlaybackPosition);
            Check.NearlyEqual(3.5d, loaded.SoundEvents[0].SoundLengthSeconds);
            Check.NearlyEqual(-0.1d, loaded.SoundEvents[0].PitchOffset, 0.0001d);
            Check.NearlyEqual(2d, loaded.SoundEvents[0].ReferenceDistance);
            Check.NearlyEqual(0.7d, loaded.SoundEvents[0].LowPassFilter, 0.0001d);
            Check.NearlyEqual(0.4d, loaded.SoundEvents[0].ReverbDecayTime, 0.0001d);
            Check.Equal(48d, loaded.ClockSamples[0].TotalHours);
            Check.NearlyEqual(1.25d, loaded.DurationSeconds);
            Check.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AsyncReplayLoadReportsProgress()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vsdirector-replay-async-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "take.vsdreplay.gz");
        Directory.CreateDirectory(directory);
        try
        {
            DirectorReplayDocument source = new()
            {
                Id = Guid.NewGuid(),
                Name = "Async Take",
                WorldId = "world-async",
                CreatedUtc = DateTimeOffset.UtcNow,
                DurationSeconds = 4d,
                Actors = new List<DirectorReplayActorTrack>
                {
                    new()
                    {
                        ActorKey = "player:a",
                        PlayerUid = "a",
                        EntityCode = "game:player",
                        ReplayEntityId = -10,
                        Samples = new List<DirectorReplayActorSample>
                        {
                            new() { TimeSeconds = 0d },
                            new() { TimeSeconds = 4d, X = 5d }
                        }
                    }
                }
            };
            DirectorReplayIO.Save(path, source);
            CaptureProgress progress = new();
            DirectorReplayDocument loaded = await DirectorReplayIO.LoadAsync(path, progress);
            Check.Equal(source.Id, loaded.Id);
            Check.Equal("Async Take", loaded.Name);
            Check.True(progress.Values.Count > 0);
            Check.NearlyEqual(1d, progress.Values[^1]);
            Check.True(progress.Values.All(value => value is >= 0d and <= 1d));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RetryableAsyncReplaySavePreservesFrozenInput()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "vsdirector-replay-save-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string blockedPath = Path.Combine(directory, "blocked.vsdreplay.gz");
        string outputPath = Path.Combine(directory, "retry.vsdreplay.gz");
        Directory.CreateDirectory(blockedPath);
        try
        {
            DirectorReplayDocument frozen = new()
            {
                Id = Guid.NewGuid(),
                Name = "Frozen retry",
                WorldId = "world-retry",
                DurationSeconds = 3.5d,
                Actors = new List<DirectorReplayActorTrack>
                {
                    new()
                    {
                        ActorKey = "player:retry",
                        PlayerUid = "retry",
                        Samples = new List<DirectorReplayActorSample>
                        {
                            new() { TimeSeconds = 0d },
                            new() { TimeSeconds = 3.5d, X = 7d }
                        }
                    }
                }
            };
            int attempts = 0;
            DirectorRetryableAsyncSave<string> save = new(async () =>
            {
                attempts++;
                string target = attempts == 1 ? blockedPath : outputPath;
                await DirectorReplayIO.SaveAsync(target, frozen);
                return target;
            });

            save.Start();
            string? result = null;
            Exception? failure = null;
            while (!save.TryTakeCompletion(out result, out failure))
            {
                await Task.Delay(1);
            }

            Check.True(result is null);
            Check.NotNull(failure);
            Check.True(save.HasFailed);
            Check.True(save.Error.Length > 0);
            Check.Equal(1, attempts);
            Check.NearlyEqual(3.5d, frozen.DurationSeconds);
            Check.Equal(2, frozen.Actors[0].Samples.Count);
            Check.Empty(Directory.GetFiles(directory, ".blocked.vsdreplay.gz.*.tmp"));

            save.Start();
            while (!save.TryTakeCompletion(out result, out failure))
            {
                await Task.Delay(1);
            }

            Check.True(failure is null);
            Check.Equal(outputPath, result);
            Check.False(save.HasFailed);
            Check.Equal(2, attempts);
            DirectorReplayDocument loaded = DirectorReplayIO.Load(outputPath);
            Check.Equal(frozen.Id, loaded.Id);
            Check.Equal("Frozen retry", loaded.Name);
            Check.NearlyEqual(3.5d, loaded.DurationSeconds);
            Check.Equal(2, loaded.Actors[0].Samples.Count);
            Check.Empty(Directory.GetFiles(directory, ".retry.vsdreplay.gz.*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WorkspaceLayoutNormalizesAndClones()
    {
        DirectorWorkspaceDocument normalized = DirectorWorkspaceDocument.Normalize(new DirectorWorkspaceDocument
        {
            SchemaVersion = -5,
            ActiveSection = (DirectorWorkspaceSection)999,
            ReplayResourceSplitRatio = double.NaN,
            ReplayInspectorSplitRatio = 4d,
            Panels = new List<DirectorWorkspacePanelLayout>
            {
                new()
                {
                    Id = " resources ",
                    X = -10d,
                    Y = double.NaN,
                    Width = 2d,
                    Height = double.PositiveInfinity,
                    Scale = 9d,
                    Dock = (DirectorWorkspaceDock)999
                },
                new() { Id = "RESOURCES", Width = 900d },
                new() { Id = "toolbar", Width = double.NaN, Height = 900d, Collapsed = true },
                new() { Id = string.Empty }
            }
        });

        Check.Equal(DirectorWorkspaceDocument.CurrentSchemaVersion, normalized.SchemaVersion);
        Check.Equal(DirectorWorkspaceSection.Cameras, normalized.ActiveSection);
        Check.Equal(6, normalized.Panels.Count);
        DirectorWorkspacePanelLayout resources = normalized.Panel("resources");
        Check.Equal(0d, resources.X);
        Check.Equal(DirectorWorkspacePanelLayout.ToolbarHeight, resources.Y);
        Check.Equal(220d, resources.Width);
        Check.Equal(420d, resources.Height);
        Check.Equal(1d, resources.Scale);
        Check.Equal(DirectorWorkspaceDock.Left, resources.Dock);
        DirectorWorkspacePanelLayout toolbar = normalized.Panel("toolbar");
        Check.Equal(DirectorWorkspacePanelLayout.ToolbarHeight, toolbar.Height);
        Check.Equal(1020d, toolbar.Width);
        Check.False(toolbar.Collapsed);
        Check.Equal(DirectorWorkspaceDock.Top, toolbar.Dock);
        DirectorWorkspacePanelLayout workflow = normalized.Panel("workflow");
        Check.Equal(DirectorWorkspaceDock.Floating, workflow.Dock);
        Check.Equal(DirectorWorkspaceDock.Bottom, normalized.Panel("timeline").Dock);
        Check.Equal(DirectorWorkspaceDock.Right, normalized.Panel("inspector").Dock);
        DirectorWorkspacePanelLayout preview = normalized.Panel("preview");
        Check.Equal(DirectorWorkspaceDock.Floating, preview.Dock);
        Check.Equal(0d, preview.X);
        Check.Equal(DirectorWorkspacePanelLayout.ToolbarHeight, preview.Y);
        Check.False(preview.Collapsed);
        Check.False(normalized.Panels.Any(panel => panel.Dock == DirectorWorkspaceDock.Center));
        Check.Equal(1d, normalized.WorkflowZoom);
        Check.Equal(0d, normalized.WorkflowPanSeconds);
        Check.Equal(0, normalized.WorkflowLaneOffset);
        Check.NearlyEqual(0.5d, normalized.ReplayResourceSplitRatio);
        Check.NearlyEqual(0.82d, normalized.ReplayInspectorSplitRatio);

        DirectorWorkspaceDocument sequencePage = DirectorWorkspaceDocument.Normalize(
            new DirectorWorkspaceDocument
            {
                SchemaVersion = DirectorWorkspaceDocument.CurrentSchemaVersion,
                ActiveSection = DirectorWorkspaceSection.SequenceEditor
            });
        Check.Equal(DirectorWorkspaceSection.Cameras, sequencePage.ActiveSection);
        DirectorWorkspaceDocument presetPage = DirectorWorkspaceDocument.Normalize(
            new DirectorWorkspaceDocument
            {
                SchemaVersion = DirectorWorkspaceDocument.CurrentSchemaVersion,
                ActiveSection = DirectorWorkspaceSection.Presets
            });
        Check.Equal(DirectorWorkspaceSection.Cameras, presetPage.ActiveSection);
        DirectorWorkspaceDocument legacyActionPage = DirectorWorkspaceDocument.Normalize(
            new DirectorWorkspaceDocument
            {
                SchemaVersion = 14,
                ActiveSection = DirectorWorkspaceSection.Actions
            });
        Check.Equal(DirectorWorkspaceSection.Cameras, legacyActionPage.ActiveSection);

        DirectorWorkspaceDocument clone = normalized.DeepClone();
        clone.Panel("resources").Width = 700d;
        clone.Panel("preview").Width = 700d;
        clone.WorkflowLaneOffset = 4;
        clone.ReplayResourceSplitRatio = 0.2d;
        Check.Equal(220d, normalized.Panel("resources").Width);
        Check.Equal(960d, normalized.Panel("preview").Width);
        Check.Equal(0, normalized.WorkflowLaneOffset);
        Check.NearlyEqual(0.5d, normalized.ReplayResourceSplitRatio);
    }

    private static void ReplayExportPlanIsExplicitAndDeterministic()
    {
        CameraPath first = new()
        {
            Id = Guid.NewGuid(),
            Name = "Wide/Shot",
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, Fov = 70f },
                new() { TimeSeconds = 10d, X = 10d, Fov = 70f }
            }
        };
        CameraPath second = new()
        {
            Id = Guid.NewGuid(),
            Name = "Close",
            Keyframes = new List<CameraKeyframe>
            {
                new() { TimeSeconds = 0d, Fov = 50f },
                new() { TimeSeconds = 10d, X = 2d, Fov = 50f }
            }
        };
        DirectorReplayExportPlan plan = DirectorReplayExportPlan.Create(
            Guid.NewGuid(),
            "Take 7",
            10d,
            new[] { first, second },
            30,
            DirectorRecordingContainer.Mp4,
            "exports");
        plan.TimeScale = 2d;
        plan.Tracks[1].Enabled = false;
        plan.RefreshFileStems();

        Check.True(plan.IsReady);
        Check.Equal(1, plan.EnabledTracks.Count);
        Check.Equal(first.Id, plan.EnabledTracks[0].TrackId);
        Check.Equal(first.Id, plan.EnabledTracks[0].SourceId);
        Check.Equal(DirectorRecordingSourceType.Path, plan.EnabledTracks[0].SourceType);
        Check.Equal("01_Wide_Shot", plan.Tracks[0].FileStem);
        Check.Equal("--_Close", plan.Tracks[1].FileStem);
        Check.Equal(150L, plan.FramesPerTrack);
        Check.Equal(150L, plan.TotalFrames);

        DirectorReplayExportPlan clone = plan.DeepClone();
        clone.Tracks[0].Enabled = false;
        Check.True(plan.Tracks[0].Enabled);
        Check.False(clone.IsReady);

        DirectorRecordingTrack[] mixedTracks =
        {
            new() { Id = Guid.NewGuid(), Name = "Fixed wide", SourceType = DirectorRecordingSourceType.StaticCamera, SourceId = Guid.NewGuid() },
            new() { Id = Guid.NewGuid(), Name = "Path move", SourceType = DirectorRecordingSourceType.Path, SourceId = Guid.NewGuid() },
            new() { Id = Guid.NewGuid(), Name = "Sequence cut", SourceType = DirectorRecordingSourceType.Sequence, SourceId = Guid.NewGuid() },
            new() { Id = Guid.NewGuid(), Name = "Follow actor", SourceType = DirectorRecordingSourceType.Follow, SourceId = Guid.NewGuid() },
            new() { Id = Guid.NewGuid(), Name = "Live workflow", SourceType = DirectorRecordingSourceType.Workflow, SourceId = Guid.NewGuid() },
            new() { Id = Guid.NewGuid(), Name = "Program", SourceType = DirectorRecordingSourceType.Program, Enabled = true }
        };
        DirectorReplayExportPlan mixedPlan = DirectorReplayExportPlan.Create(
            Guid.NewGuid(),
            "Unified",
            12d,
            mixedTracks,
            track => track.SourceType.ToString(),
            60,
            DirectorRecordingContainer.Mp4,
            "exports");
        Check.Equal(4, mixedPlan.EnabledTracks.Count);
        Check.SequenceEqual(
            new[]
            {
                DirectorRecordingSourceType.StaticCamera,
                DirectorRecordingSourceType.Path,
                DirectorRecordingSourceType.Sequence,
                DirectorRecordingSourceType.Follow
            },
            mixedPlan.EnabledTracks.Select(track => track.SourceType));
    }

    private static void ReplayExportSettlesAfterEverySeek()
    {
        DirectorReplayExportFrameGate gate = new(settleFramesPerSeek: 2);
        gate.BeginSeek();
        Check.Equal(DirectorReplayExportFrameAction.WaitForReplay, gate.Next(replayReady: false));
        Check.Equal(DirectorReplayExportFrameAction.WaitForReplay, gate.Next(replayReady: false));
        Check.Equal(DirectorReplayExportFrameAction.ApplyCamera, gate.Next(replayReady: true));
        Check.Equal(DirectorReplayExportFrameAction.Settle, gate.Next(replayReady: true));
        Check.Equal(DirectorReplayExportFrameAction.Settle, gate.Next(replayReady: true));
        Check.Equal(DirectorReplayExportFrameAction.Capture, gate.Next(replayReady: true));

        gate.BeginSeek();
        Check.Equal(DirectorReplayExportFrameAction.ApplyCamera, gate.Next(replayReady: true));
        Check.Equal(DirectorReplayExportFrameAction.Settle, gate.Next(replayReady: true));
        Check.Equal(DirectorReplayExportFrameAction.WaitForReplay, gate.Next(replayReady: false));
        Check.Equal(DirectorReplayExportFrameAction.ApplyCamera, gate.Next(replayReady: true));
        Check.Equal(DirectorReplayExportFrameAction.Settle, gate.Next(replayReady: true));
        Check.Equal(DirectorReplayExportFrameAction.Settle, gate.Next(replayReady: true));
        Check.Equal(DirectorReplayExportFrameAction.Capture, gate.Next(replayReady: true));

        DirectorReplayExportFrameGate noSettle = new(settleFramesPerSeek: 0);
        noSettle.BeginSeek();
        Check.Equal(DirectorReplayExportFrameAction.ApplyCamera, noSettle.Next(replayReady: true));
        Check.Equal(DirectorReplayExportFrameAction.Capture, noSettle.Next(replayReady: true));
    }

    private static void ObsReplayExportWaitsForStableLoadedFrame()
    {
        DirectorReplayObsExportStartGate gate = new(settleFrames: 2);
        Check.False(gate.TryBeginRecording(replayReady: true));
        Check.False(gate.CanAdvanceReplayClock);

        gate.MarkConnected();
        Check.False(gate.TryBeginRecording(replayReady: false));
        Check.False(gate.TryBeginRecording(replayReady: true));
        Check.False(gate.TryBeginRecording(replayReady: true));
        Check.False(gate.TryBeginRecording(replayReady: false));

        Check.False(gate.TryBeginRecording(replayReady: true));
        Check.False(gate.TryBeginRecording(replayReady: true));
        Check.False(gate.TryBeginRecording(replayReady: true));
        Check.True(gate.TryBeginRecording(replayReady: true));
        Check.False(gate.TryBeginRecording(replayReady: true));
        Check.False(gate.CanAdvanceReplayClock);

        gate.CompleteRecordingStart();
        Check.True(gate.RecordingStarted);
        Check.True(gate.CanAdvanceReplayClock);
        Check.False(gate.TryBeginRecording(replayReady: true));

        DirectorReplayObsExportStartGate canceled = new(settleFrames: 0);
        canceled.MarkConnected();
        Check.False(canceled.TryBeginRecording(replayReady: true));
        Check.True(canceled.TryBeginRecording(replayReady: true));
        canceled.FailRecordingStart();
        Check.False(canceled.RecordingStartPending);
        Check.False(canceled.CanAdvanceReplayClock);
    }

    private static void ReplayWorldStateAndChunkRleWork()
    {
        int[] sourceBlocks = { -5, 0, 0, 4, 4, 2 };
        List<DirectorReplayBlockRun> runs = DirectorReplayChunkCodec.Encode(sourceBlocks);
        Check.Equal(3, runs.Count);
        Check.Equal(0, runs[0].BlockId);
        Check.Equal(3, runs[0].Length);
        Check.SequenceEqual(new[] { 0, 0, 0, 4, 4, 2 }, DirectorReplayChunkCodec.Decode(runs, 6));
        int[] pooledDestination = Enumerable.Repeat(99, 8).ToArray();
        DirectorReplayChunkCodec.DecodeInto(runs, pooledDestination);
        Check.SequenceEqual(new[] { 0, 0, 0, 4, 4, 2, 0, 0 }, pooledDestination);

        int chunkBlockCount = 32 * 32 * 32;
        int[] solid = new int[chunkBlockCount];
        solid[1] = 7;
        DirectorReplayDocument normalized = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Name = " World take ",
            DurationSeconds = 5d,
            WorldSnapshot = new DirectorReplayWorldSnapshot
            {
                Dimension = 2,
                RadiusChunks = 3,
                Chunks = new List<DirectorReplayChunkSnapshot>
                {
                    new()
                    {
                        ChunkX = 10,
                        ChunkY = 1,
                        ChunkZ = -4,
                        SolidRuns = DirectorReplayChunkCodec.Encode(solid),
                        FluidRuns = DirectorReplayChunkCodec.Encode(new int[chunkBlockCount])
                    }
                },
                BlockEntities = new List<DirectorReplayBlockEntitySnapshot>
                {
                    new() { X = 321, Y = 40, Z = -120, Dimension = 2, ClassName = "chest", Data = new byte[] { 1, 2 } }
                }
            },
            BlockEvents = new List<DirectorReplayBlockEvent>
            {
                new()
                {
                    TimeSeconds = 2d,
                    X = 322,
                    Y = 40,
                    Z = -120,
                    Dimension = 2,
                    SolidBlockId = 8,
                    BlockEntityCaptured = true
                }
            },
            ParticleEvents = new List<DirectorReplayParticleEvent>
            {
                new() { TimeSeconds = 3d, Kind = DirectorReplayParticleKind.Simple, Quantity = 4f, Scale = 1f }
            },
            SoundEvents = new List<DirectorReplaySoundEvent>
            {
                new() { TimeSeconds = 4d, Location = "game:sounds/test", Volume = 1f, Pitch = 1f, Range = 32f }
            },
            ClockSamples = new List<DirectorReplayClockSample>
            {
                new() { TimeSeconds = 0d, TotalHours = 100d, CalendarSpeedMul = 0.5f },
                new() { TimeSeconds = 4d, TotalHours = 124d, CalendarSpeedMul = 1f, SeasonOverride = 0.75f }
            },
            Actors = new List<DirectorReplayActorTrack>
            {
                new()
                {
                    ActorKey = "entity:5",
                    EntityId = 5,
                    EntityCode = "game:drifter-normal",
                    InitialState = new byte[] { 9 },
                    Lifetimes = new List<DirectorReplayActorLifetime>
                    {
                        new() { StartSeconds = 1d, EndSeconds = 4d, EndedByDespawn = true }
                    },
                    States = new List<DirectorReplayActorState>
                    {
                        new() { TimeSeconds = 1d, Data = new byte[] { 9 } },
                        new() { TimeSeconds = 3d, Data = new byte[] { 10 } }
                    },
                    Samples = new List<DirectorReplayActorSample>
                    {
                        new() { TimeSeconds = 1d },
                        new() { TimeSeconds = 4d, X = 3d }
                    }
                }
            }
        });

        Check.Equal(DirectorReplayDocument.CurrentSchemaVersion, normalized.SchemaVersion);
        Check.True(normalized.IncludesWorldSnapshot);
        Check.Equal(DirectorReplayDocument.ClientWorldCaptureScope, normalized.CaptureScope);
        Check.Equal(1, normalized.WorldSnapshot.Chunks.Count);
        Check.Equal(7, DirectorReplayChunkCodec.Decode(
            normalized.WorldSnapshot.Chunks[0].SolidRuns,
            chunkBlockCount)[1]);
        Check.True(normalized.BlockEvents[0].BlockEntityCaptured);
        Check.Equal(string.Empty, normalized.BlockEvents[0].BlockEntityClass);
        Check.Equal(2, normalized.Actors[0].States.Count);
        Check.True(normalized.Actors[0].ReplayEntityId < 0);
        Check.False(DirectorReplayMath.IsActorVisible(normalized.Actors[0], 0.999d));
        Check.True(DirectorReplayMath.IsActorVisible(normalized.Actors[0], 1d));
        Check.True(DirectorReplayMath.IsActorVisible(normalized.Actors[0], 2d));
        Check.True(DirectorReplayMath.IsActorVisible(normalized.Actors[0], 3.999d));
        Check.False(DirectorReplayMath.IsActorVisible(normalized.Actors[0], 4d));
        Check.False(DirectorReplayMath.IsActorVisible(normalized.Actors[0], 4.5d));
        Check.Equal(10, Check.NotNull(DirectorReplayMath.FindActorState(normalized.Actors[0], 3.5d)).Data[0]);
        Check.True(DirectorReplayMath.TrySampleClock(normalized.ClockSamples, 2d, out DirectorReplayClockSample clock));
        Check.NearlyEqual(112d, clock.TotalHours);
        Check.NearlyEqual(0.75d, clock.CalendarSpeedMul);
        Check.NearlyEqual(0.75d, clock.SeasonOverride ?? -1f);
        Check.Equal(5d, normalized.DurationSeconds);

        DirectorReplayDocument clone = normalized.DeepClone();
        clone.WorldSnapshot.Chunks[0].SolidRuns[0].BlockId = 99;
        clone.Actors[0].States[0].Data[0] = 55;
        Check.Equal(0, normalized.WorldSnapshot.Chunks[0].SolidRuns[0].BlockId);
        Check.Equal(9, normalized.Actors[0].States[0].Data[0]);
    }

    private static void PoseUpdatesRetainMetadata()
    {
        DateTimeOffset createdUtc = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        CameraLibrary library = new();
        CameraPreset original = library.Add(new CameraPreset
        {
            Name = "Metadata",
            Tags = new List<string> { "tag" },
            Description = "keep this",
            QuickSlot = 7,
            X = 1,
            Y = 2,
            Z = 3,
            Fov = 60,
            CreatedUtc = createdUtc,
            ModifiedUtc = createdUtc
        });

        bool updated = library.UpdatePose(original.Id, new CameraPreset
        {
            Name = "must not replace",
            Tags = new List<string> { "must not replace" },
            Description = "must not replace",
            QuickSlot = 9,
            X = 10.5,
            Y = 20.5,
            Z = 30.5,
            Dimension = 2,
            Yaw = 45,
            Pitch = -20,
            Roll = 12,
            Fov = 92
        });

        Check.True(updated);
        CameraPreset result = Check.NotNull(library.Find(original.Id));
        Check.Equal("Metadata", result.Name);
        Check.SequenceEqual(new[] { "tag" }, result.Tags);
        Check.Equal("keep this", result.Description);
        Check.Equal(7, result.QuickSlot);
        Check.Equal(createdUtc, result.CreatedUtc);
        Check.True(result.ModifiedUtc > original.ModifiedUtc);
        Check.Equal(10.5d, result.X);
        Check.Equal(20.5d, result.Y);
        Check.Equal(30.5d, result.Z);
        Check.Equal(2, result.Dimension);
        Check.Equal(45f, result.Yaw);
        Check.Equal(-20f, result.Pitch);
        Check.Equal(12f, result.Roll);
        Check.Equal(92f, result.Fov);
    }

    private static void MetadataUpdatesRetainPose()
    {
        CameraLibrary library = new();
        CameraPreset original = library.Add(new CameraPreset
        {
            Name = "Original",
            X = 4,
            Y = 5,
            Z = 6,
            Dimension = -1,
            Yaw = 15,
            Pitch = 25,
            Roll = 35,
            Fov = 65
        });

        Check.True(library.UpdateMetadata(
            original.Id,
            " Renamed ",
            new[] { " close ", "CLOSE", "night" },
            " description ",
            88f,
            9f,
            QuickSlots.Last));

        CameraPreset result = Check.NotNull(library.Find(original.Id));
        Check.Equal("Renamed", result.Name);
        Check.SequenceEqual(new[] { "close", "night" }, result.Tags);
        Check.Equal("description", result.Description);
        Check.Equal(88f, result.Fov);
        Check.Equal(9f, result.Roll);
        Check.Equal(QuickSlots.Last, result.QuickSlot);
        Check.Equal(4d, result.X);
        Check.Equal(5d, result.Y);
        Check.Equal(6d, result.Z);
        Check.Equal(-1, result.Dimension);
        Check.Equal(15f, result.Yaw);
        Check.Equal(25f, result.Pitch);
    }

    private static void WorkflowMultiViewManifestRoundTripsAtomically()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vsdirector-manifest-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            Guid workflowId = Guid.NewGuid();
            Guid passId = Guid.NewGuid();
            Guid viewId = Guid.NewGuid();
            Guid cardId = Guid.NewGuid();
            Guid sourceId = Guid.NewGuid();
            DateTimeOffset createdUtc = new(2026, 7, 15, 6, 30, 0, TimeSpan.Zero);
            string path = Path.Combine(root, "nested", "workflow-multiview.manifest.json");
            DirectorWorkflowMultiViewManifest manifest = new()
            {
                Status = "completed",
                WorkflowId = workflowId,
                WorkflowName = "Opening sequence",
                CreatedUtc = createdUtc,
                StartedUtc = createdUtc.AddSeconds(1),
                CompletedUtc = createdUtc.AddSeconds(13),
                TimelineStartSeconds = 2.5d,
                TimelineEndSeconds = 14.5d,
                LastTimelineSeconds = 14.5d,
                Width = 1920,
                Height = 1080,
                FramesPerSecond = 60,
                ReplayStartedByCapture = false,
                AdvancesWorldTicks = true,
                RenderLocalPlayer = true,
                LocalPlayerRenderMode = "detached-third-person",
                Capabilities = new List<string> { "same-physical-frame", "independent-view-poses" },
                Limitations = new List<string> { "one-observation-anchor" },
                Passes = new List<DirectorWorkflowMultiViewManifestPass>
                {
                    new()
                    {
                        PassId = passId,
                        ViewId = viewId,
                        Name = "Path view",
                        StartSeconds = 2.5d,
                        EndSeconds = 14.5d,
                        SplitForOverlap = true,
                        FileName = "path-view.mp4",
                        Encoder = "Nvenc",
                        Codec = "h264_nvenc",
                        EncodedFrames = 720,
                        DroppedFrames = 3,
                        Success = true,
                        Segments = new List<DirectorWorkflowMultiViewManifestSegment>
                        {
                            new()
                            {
                                CardId = cardId,
                                CardName = "Dolly in",
                                SourceType = DirectorRecordingSourceType.Path,
                                SourceId = sourceId,
                                StartSeconds = 2.5d,
                                EndSeconds = 14.5d
                            }
                        }
                    }
                },
                Errors = new List<DirectorWorkflowMultiViewManifestError>
                {
                    new()
                    {
                        Code = "SampleWarning",
                        PassId = passId,
                        PassName = "Path view",
                        CardId = cardId,
                        SourceType = DirectorRecordingSourceType.Path,
                        SourceId = sourceId,
                        Detail = "Diagnostic detail"
                    }
                }
            };

            DirectorWorkflowMultiViewManifestIO.SaveAtomic(path, manifest);
            string directory = Path.GetDirectoryName(path)!;
            Check.Empty(Directory.GetFiles(directory, "workflow-multiview.manifest.json.tmp-*"));

            DirectorWorkflowMultiViewManifest loaded = DirectorWorkflowMultiViewManifestIO.Load(path);
            Check.Equal(DirectorWorkflowMultiViewManifest.CurrentSchemaVersion, loaded.SchemaVersion);
            Check.Equal("live-world-offscreen-multiview", loaded.CaptureMode);
            Check.Equal("completed", loaded.Status);
            Check.Equal(workflowId, loaded.WorkflowId);
            Check.Equal("Opening sequence", loaded.WorkflowName);
            Check.Equal(createdUtc, loaded.CreatedUtc);
            Check.Equal<DateTimeOffset?>(createdUtc.AddSeconds(1), loaded.StartedUtc);
            Check.Equal<DateTimeOffset?>(createdUtc.AddSeconds(13), loaded.CompletedUtc);
            Check.Equal(14.5d, loaded.LastTimelineSeconds);
            Check.Equal(1920, loaded.Width);
            Check.Equal(1080, loaded.Height);
            Check.Equal(60, loaded.FramesPerSecond);
            Check.True(loaded.AdvancesWorldTicks);
            Check.True(loaded.RenderLocalPlayer);
            Check.SequenceEqual(manifest.Capabilities, loaded.Capabilities);
            Check.SequenceEqual(manifest.Limitations, loaded.Limitations);
            Check.Equal(1, loaded.Passes.Count);
            DirectorWorkflowMultiViewManifestPass loadedPass = loaded.Passes[0];
            Check.Equal(passId, loadedPass.PassId);
            Check.Equal(viewId, loadedPass.ViewId);
            Check.Equal<bool?>(true, loadedPass.Success);
            Check.Equal(720L, loadedPass.EncodedFrames);
            Check.Equal(3L, loadedPass.DroppedFrames);
            Check.Equal(DirectorRecordingSourceType.Path, loadedPass.Segments.Single().SourceType);
            Check.Equal(sourceId, loadedPass.Segments.Single().SourceId);
            Check.Equal("SampleWarning", loaded.Errors.Single().Code);

            manifest.Status = "failed";
            manifest.Passes[0].Success = false;
            DirectorWorkflowMultiViewManifestIO.SaveAtomic(path, manifest);
            Check.Equal("failed", DirectorWorkflowMultiViewManifestIO.Load(path).Status);
            Check.Empty(Directory.GetFiles(directory, "workflow-multiview.manifest.json.tmp-*"));

            string failingTarget = Path.Combine(root, "cannot-replace.json");
            Directory.CreateDirectory(failingTarget);
            Check.ThrowsAny<IOException, UnauthorizedAccessException>(
                () => DirectorWorkflowMultiViewManifestIO.SaveAtomic(failingTarget, manifest));
            Check.Empty(Directory.GetFiles(root, "cannot-replace.json.tmp-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task JsonRoundTripAndTempCleanupAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vsdirector-core-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string path = Path.Combine(root, "nested", "cameras.json");
            CameraLibrary library = new();
            library.Add(new CameraPreset
            {
                Name = "主基地远景",
                Tags = new List<string> { "清晨", "wide" },
                Description = "UTF-8 描述",
                X = 123.25,
                Y = 87.5,
                Z = -40.75,
                Dimension = 1,
                Yaw = 179.5f,
                Pitch = -12.25f,
                Roll = 2.5f,
                Fov = 68.5f,
                QuickSlot = 4,
                CreatedUtc = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
                ModifiedUtc = new DateTimeOffset(2025, 1, 2, 3, 5, 6, TimeSpan.Zero)
            });
            CameraPath savedPath = library.AddPath(new CameraPath
            {
                Name = "环绕路径",
                Interpolation = CameraPathInterpolation.Bezier,
                LoopMode = CameraPathLoopMode.Loop,
                PlaybackSpeed = 1.75d,
                IncludedPreloadColumns = new List<DirectorReplayPreloadColumn>
                {
                    new(210, 220, 1)
                },
                ExcludedPreloadColumns = new List<DirectorReplayPreloadColumn>
                {
                    new(211, 220, 1)
                }
            });
            Check.True(library.UpsertPathKeyframe(savedPath.Id, new CameraKeyframe
            {
                TimeSeconds = 3.5,
                X = 10,
                Y = 20,
                Z = 30,
                Dimension = 1,
                Yaw = 45,
                Fov = 75
            }));
            CameraPreset sequenceCamera = Check.NotNull(library.Find("主基地远景"));
            Check.True(library.SetCameraPreviewVisible(sequenceCamera.Id, true));
            library.AddSequence(new CameraSequence
            {
                Name = "开场多机位",
                LoopMode = CameraSequenceLoopMode.Loop,
                Shots = new List<CameraSequenceShot>
                {
                    new() { CameraId = sequenceCamera.Id, DurationSeconds = 4.25 }
                }
            });
            CameraFollowRig savedFollow = library.AddFollowRig(new CameraFollowRig
            {
                Name = "跟随演员甲",
                Tags = new List<string> { "演员", "近景" },
                Description = "跟随主角移动",
                TargetType = CameraFollowTargetType.Player,
                TargetPlayerUid = "player-alpha",
                TargetDisplayName = "演员甲",
                ViewMode = CameraFollowViewMode.ThirdPerson,
                MotionMode = CameraFollowMotionMode.Delayed,
                Anchor = CameraFollowAnchor.Head,
                OffsetX = 1.25,
                Distance = 8,
                Height = 2.5,
                OrbitAngleDegrees = 30,
                OrbitSpeedDegreesPerSecond = 12,
                DelaySeconds = 1.5,
                Fov = 55,
                QuickSlot = 6
            });
            library.AddRecordingTrack(new DirectorRecordingTrack
            {
                Name = "演员甲轨道",
                SourceType = DirectorRecordingSourceType.Follow,
                SourceId = savedFollow.Id,
                Enabled = true
            });

            await CameraDocumentIO.SaveAsync(path, library.Document);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            Check.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            string json = Encoding.UTF8.GetString(bytes);
            Check.True(json.Contains("主基地远景", StringComparison.Ordinal));
            Check.False(json.Contains("RecordingTracks", StringComparison.Ordinal));
            Check.False(json.Contains("演员甲轨道", StringComparison.Ordinal));

            CameraLibraryDocument loadedDocument = await CameraDocumentIO.LoadAsync(path);
            CameraPreset loaded = Check.NotNull(new CameraLibrary(loadedDocument).Find("主基地远景"));
            Check.Equal(123.25d, loaded.X);
            Check.Equal(87.5d, loaded.Y);
            Check.Equal(-40.75d, loaded.Z);
            Check.Equal(1, loaded.Dimension);
            Check.Equal(179.5f, loaded.Yaw);
            Check.Equal(-12.25f, loaded.Pitch);
            Check.Equal(2.5f, loaded.Roll);
            Check.Equal(68.5f, loaded.Fov);
            Check.Equal(4, loaded.QuickSlot);
            Check.True(loaded.PreviewVisible);
            Check.SequenceEqual(new[] { "清晨", "wide" }, loaded.Tags);
            CameraPath loadedPath = Check.NotNull(new CameraLibrary(loadedDocument).FindPath("环绕路径"));
            Check.Equal(CameraPathInterpolation.Bezier, loadedPath.Interpolation);
            Check.Equal(CameraPathLoopMode.Loop, loadedPath.LoopMode);
            Check.NearlyEqual(1.75d, loadedPath.PlaybackSpeed);
            Check.SequenceEqual(
                new[] { new DirectorReplayPreloadColumn(210, 220, 1) },
                loadedPath.IncludedPreloadColumns);
            Check.SequenceEqual(
                new[] { new DirectorReplayPreloadColumn(211, 220, 1) },
                loadedPath.ExcludedPreloadColumns);
            Check.Equal(1, loadedPath.Keyframes.Count);
            Check.Equal(3.5d, loadedPath.Keyframes[0].TimeSeconds);
            CameraSequence loadedSequence = Check.NotNull(new CameraLibrary(loadedDocument).FindSequence("开场多机位"));
            Check.Equal(CameraSequenceLoopMode.Loop, loadedSequence.LoopMode);
            Check.Equal(1, loadedSequence.Shots.Count);
            Check.Equal(sequenceCamera.Id, loadedSequence.Shots[0].CameraId);
            Check.NearlyEqual(4.25d, loadedSequence.Shots[0].DurationSeconds);
            CameraFollowRig loadedFollow = Check.NotNull(
                new CameraLibrary(loadedDocument).FindFollowRig("跟随演员甲"));
            Check.SequenceEqual(new[] { "演员", "近景" }, loadedFollow.Tags);
            Check.Equal("跟随主角移动", loadedFollow.Description);
            Check.Equal("player-alpha", loadedFollow.TargetPlayerUid);
            Check.Equal(CameraFollowViewMode.ThirdPerson, loadedFollow.ViewMode);
            Check.Equal(CameraFollowMotionMode.Delayed, loadedFollow.MotionMode);
            Check.NearlyEqual(1.25d, loadedFollow.OffsetX);
            Check.NearlyEqual(8d, loadedFollow.Distance);
            Check.NearlyEqual(2.5d, loadedFollow.Height);
            Check.NearlyEqual(30d, loadedFollow.OrbitAngleDegrees);
            Check.NearlyEqual(12d, loadedFollow.OrbitSpeedDegreesPerSecond);
            Check.NearlyEqual(1.5d, loadedFollow.DelaySeconds);
            Check.Equal(6, loadedFollow.QuickSlot);
            Check.True(new CameraLibrary(loadedDocument).FindRecordingTrack("演员甲轨道") is null);

            string directory = Path.GetDirectoryName(path)!;
            Check.Empty(Directory.GetFiles(directory, ".cameras.json.*.tmp"));

            CameraDocumentIO.Save(path, library.Document);
            Check.Empty(Directory.GetFiles(directory, ".cameras.json.*.tmp"));

            string failingTarget = Path.Combine(root, "cannot-replace.json");
            Directory.CreateDirectory(failingTarget);
            Check.ThrowsAny<IOException, UnauthorizedAccessException>(
                () => CameraDocumentIO.Save(failingTarget, library.Document));
            Check.Empty(Directory.GetFiles(root, ".cannot-replace.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task InvalidDocumentsAreRejectedAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vsdirector-invalid-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string corruptPath = Path.Combine(root, "corrupt.json");
            const string corruptJson = "{ this is not valid json";
            await File.WriteAllTextAsync(corruptPath, corruptJson, new UTF8Encoding(false));
            await Check.ThrowsAsync<System.Text.Json.JsonException>(() => CameraDocumentIO.LoadAsync(corruptPath));
            Check.Equal(corruptJson, await File.ReadAllTextAsync(corruptPath));

            string futurePath = Path.Combine(root, "future.json");
            const string futureJson = "{\"schemaVersion\":999,\"cameras\":[]}";
            await File.WriteAllTextAsync(futurePath, futureJson, new UTF8Encoding(false));
            await Check.ThrowsAsync<NotSupportedException>(() => CameraDocumentIO.LoadAsync(futurePath));
            Check.Equal(futureJson, await File.ReadAllTextAsync(futurePath));
            Check.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PortablePresetPackagesRemapIdsAndReferences()
    {
        Guid sourceCameraId = Guid.NewGuid();
        Guid sourcePathId = Guid.NewGuid();
        Guid sourceSequenceId = Guid.NewGuid();
        Guid sourceFollowId = Guid.NewGuid();
        CameraLibrary destination = new();
        CameraPreset existing = destination.Add(new CameraPreset
        {
            Name = "Shared",
            QuickSlot = 1,
            Fov = 70f
        });
        DirectorPresetPackage source = new()
        {
            Name = "Portable",
            Cameras = new List<CameraPreset>
            {
                new()
                {
                    Id = sourceCameraId,
                    Name = "Shared",
                    QuickSlot = 2,
                    X = 12d,
                    Fov = 80f
                }
            },
            Paths = new List<CameraPath>
            {
                new()
                {
                    Id = sourcePathId,
                    Name = "Flyover",
                    Keyframes = new List<CameraKeyframe>
                    {
                        new() { TimeSeconds = 0d, Fov = 70f },
                        new() { TimeSeconds = 2d, X = 4d, Fov = 75f }
                    }
                }
            },
            Sequences = new List<CameraSequence>
            {
                new()
                {
                    Id = sourceSequenceId,
                    Name = "Cut",
                    Shots = new List<CameraSequenceShot>
                    {
                        new() { CameraId = sourceCameraId, DurationSeconds = 3d }
                    }
                }
            },
            FollowRigs = new List<CameraFollowRig>
            {
                new()
                {
                    Id = sourceFollowId,
                    Name = "Actor",
                    TargetType = CameraFollowTargetType.Player,
                    TargetPlayerUid = "player-a",
                    TargetDisplayName = "Actor A",
                    QuickSlot = 3
                }
            },
            Workflows = new List<DirectorWorkflow>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Edit",
                    Cards = new List<DirectorWorkflowCard>
                    {
                        new()
                        {
                            Id = Guid.NewGuid(), Name = "Camera",
                            SourceType = DirectorRecordingSourceType.StaticCamera,
                            SourceId = sourceCameraId, StartSeconds = 0d, EndSeconds = 3d
                        },
                        new()
                        {
                            Id = Guid.NewGuid(), Name = "Follow",
                            SourceType = DirectorRecordingSourceType.Follow,
                            SourceId = sourceFollowId, StartSeconds = 3d, EndSeconds = 6d
                        }
                    }
                }
            }
        };

        string text = System.Text.Json.JsonSerializer.Serialize(source);
        DirectorPresetPackage parsed = DirectorPresetPackageIO.ParseText(text);
        DirectorPresetImportResult result = DirectorPresetPackageIO.Merge(destination.Document, parsed);
        Check.Equal(1, result.Cameras);
        Check.Equal(1, result.Paths);
        Check.Equal(1, result.Sequences);
        Check.Equal(1, result.FollowRigs);
        Check.Equal(1, result.Workflows);

        CameraLibrary merged = new(result.Document);
        CameraPreset imported = Check.NotNull(merged.Find("Shared (2)"));
        Check.True(imported.Id != sourceCameraId);
        Check.Null(imported.QuickSlot);
        Check.Equal(1, Check.NotNull(merged.Find(existing.Id)).QuickSlot);
        CameraPath importedPath = Check.NotNull(merged.FindPath("Flyover"));
        Check.True(importedPath.Id != sourcePathId);
        CameraSequence importedSequence = Check.NotNull(merged.FindSequence("Cut"));
        Check.True(importedSequence.Id != sourceSequenceId);
        Check.Equal(imported.Id, importedSequence.Shots.Single().CameraId);
        Check.Null(Check.NotNull(merged.FindFollowRig("Actor")).QuickSlot);
        DirectorWorkflow importedWorkflow = Check.NotNull(merged.FindWorkflow("Edit"));
        Check.Equal(imported.Id, importedWorkflow.Cards.Single(card => card.Name == "Camera").SourceId);
        Check.Equal(Check.NotNull(merged.FindFollowRig("Actor")).Id,
            importedWorkflow.Cards.Single(card => card.Name == "Follow").SourceId);

        string exported = DirectorPresetPackageIO.ExportText(merged.Document, "Round trip");
        DirectorPresetPackage roundTrip = DirectorPresetPackageIO.ParseText(exported);
        Check.Equal("Round trip", roundTrip.Name);
        Check.Equal(2, roundTrip.Cameras.Count);
        Check.Throws<InvalidDataException>(() => DirectorPresetPackageIO.Merge(
            destination.Document,
            new DirectorPresetPackage { SchemaVersion = 999 }));
    }
}

internal static class Check
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected true, but was false.");
        }
    }

    public static void False(bool condition, string? message = null) => True(!condition, message ?? "Expected false, but was true.");

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}>, but was <{actual}>.");
        }
    }

    public static void NearlyEqual(double expected, double actual, double tolerance = 0.00001d)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"Expected <{expected}> +/- {tolerance}, but was <{actual}>.");
        }
    }

    public static void Null<T>(T? value)
        where T : struct
    {
        if (value.HasValue)
        {
            throw new InvalidOperationException($"Expected null, but was <{value.Value}>.");
        }
    }

    public static T NotNull<T>(T? value)
        where T : class
    {
        return value ?? throw new InvalidOperationException("Expected a non-null value.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        T[] expectedArray = expected.ToArray();
        T[] actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidOperationException(
                $"Expected sequence <{string.Join(", ", expectedArray)}>, but was <{string.Join(", ", actualArray)}>.");
        }
    }

    public static void Empty<T>(IEnumerable<T> values)
    {
        if (values.Any())
        {
            throw new InvalidOperationException("Expected an empty sequence.");
        }
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    public static void ThrowsAny<TFirst, TSecond>(Action action)
        where TFirst : Exception
        where TSecond : Exception
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is TFirst or TSecond)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TFirst).Name} or {typeof(TSecond).Name} to be thrown.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }
}

internal sealed class CaptureProgress : IProgress<double>
{
    public List<double> Values { get; } = new();

    public void Report(double value) => Values.Add(value);
}
