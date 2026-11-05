namespace VSDirector.Data;

/// <summary>
/// Owns camera presets for one save and enforces document invariants.
/// Returned presets are always detached copies.
/// </summary>
public sealed class CameraLibrary
{
    private const string DefaultCameraName = "Camera";
    private const string DefaultPathName = "Path";
    private const string DefaultSequenceName = "Sequence";
    private const string DefaultWorkflowName = "Workflow";
    private const string DefaultWorkflowCardName = "Shot";
    private const string DefaultFollowName = "Follow";
    private const string DefaultRecordingTrackName = "Track";
    private const string DefaultActorPerformanceName = "Actor Performance";
    private const string DefaultActionName = "Action";
    private const string DefaultModelName = "Model";
    private const float DefaultFov = 70f;
    private const double KeyframeTimeEpsilon = CameraPathTiming.TimeMatchEpsilon;
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private readonly object gate = new();
    private readonly List<CameraPreset> cameras;
    private readonly List<CameraPath> paths;
    private readonly List<CameraSequence> sequences;
    private readonly List<DirectorWorkflow> workflows;
    private readonly List<CameraFollowRig> followRigs;
    private readonly List<DirectorRecordingTrack> recordingTracks;
    private readonly List<DirectorActorPerformance> actorPerformances;
    private readonly List<DirectorActionAsset> actions;
    private readonly List<DirectorModelAsset> models;

    public CameraLibrary()
        : this(null)
    {
    }

    public CameraLibrary(CameraLibraryDocument? document)
    {
        CameraLibraryDocument normalized = NormalizeDocument(document);
        cameras = normalized.Cameras;
        paths = normalized.Paths;
        sequences = normalized.Sequences;
        workflows = normalized.Workflows;
        followRigs = normalized.FollowRigs;
        recordingTracks = normalized.RecordingTracks;
        actorPerformances = normalized.ActorPerformances;
        actions = normalized.Actions;
        models = normalized.Models;
    }

    /// <summary>
    /// Gets a normalized, stably ordered snapshot of the current document.
    /// </summary>
    public CameraLibraryDocument Document
    {
        get
        {
            lock (gate)
            {
                return CreateDocumentSnapshot();
            }
        }
    }

    /// <summary>
    /// Gets detached camera copies ordered by name using an ordinal,
    /// case-insensitive comparison.
    /// </summary>
    public IReadOnlyList<CameraPreset> Ordered
    {
        get
        {
            lock (gate)
            {
                return OrderedInternal().Select(camera => camera.DeepClone()).ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return cameras.Count;
            }
        }
    }

    public IReadOnlyList<CameraPath> OrderedPaths
    {
        get
        {
            lock (gate)
            {
                return OrderedPathsInternal().Select(path => path.DeepClone()).ToArray();
            }
        }
    }

    public int PathCount
    {
        get
        {
            lock (gate)
            {
                return paths.Count;
            }
        }
    }

    public IReadOnlyList<CameraSequence> OrderedSequences
    {
        get
        {
            lock (gate)
            {
                return OrderedSequencesInternal().Select(sequence => sequence.DeepClone()).ToArray();
            }
        }
    }

    public int SequenceCount
    {
        get
        {
            lock (gate)
            {
                return sequences.Count;
            }
        }
    }

    public IReadOnlyList<DirectorWorkflow> OrderedWorkflows
    {
        get
        {
            lock (gate)
            {
                return OrderedWorkflowsInternal().Select(workflow => workflow.DeepClone()).ToArray();
            }
        }
    }

    public int WorkflowCount
    {
        get
        {
            lock (gate)
            {
                return workflows.Count;
            }
        }
    }

    public IReadOnlyList<CameraFollowRig> OrderedFollowRigs
    {
        get
        {
            lock (gate)
            {
                return OrderedFollowRigsInternal().Select(rig => rig.DeepClone()).ToArray();
            }
        }
    }

    public int FollowRigCount
    {
        get
        {
            lock (gate)
            {
                return followRigs.Count;
            }
        }
    }

    public IReadOnlyList<DirectorRecordingTrack> OrderedRecordingTracks
    {
        get
        {
            lock (gate)
            {
                return OrderedRecordingTracksInternal().Select(track => track.DeepClone()).ToArray();
            }
        }
    }

    public int RecordingTrackCount
    {
        get
        {
            lock (gate)
            {
                return recordingTracks.Count;
            }
        }
    }

    public IReadOnlyList<DirectorActorPerformance> OrderedActorPerformances
    {
        get
        {
            lock (gate)
            {
                return OrderedActorPerformancesInternal()
                    .Select(performance => performance.DeepClone())
                    .ToArray();
            }
        }
    }

    public int ActorPerformanceCount
    {
        get
        {
            lock (gate)
            {
                return actorPerformances.Count;
            }
        }
    }

    public IReadOnlyList<DirectorActionAsset> OrderedActions
    {
        get
        {
            lock (gate)
            {
                return OrderedActionsInternal().Select(action => action.DeepClone()).ToArray();
            }
        }
    }

    public int ActionCount
    {
        get
        {
            lock (gate)
            {
                return actions.Count;
            }
        }
    }

    public IReadOnlyList<DirectorModelAsset> OrderedModels
    {
        get
        {
            lock (gate)
            {
                return OrderedModelsInternal().Select(model => model.DeepClone()).ToArray();
            }
        }
    }

    public DirectorModelAsset? FindModel(Guid id)
    {
        lock (gate)
        {
            return models.FirstOrDefault(model => model.Id == id)?.DeepClone();
        }
    }

    public DirectorModelAsset? FindModel(string modelCode)
    {
        if (string.IsNullOrWhiteSpace(modelCode))
        {
            return null;
        }
        lock (gate)
        {
            return models.FirstOrDefault(model => string.Equals(
                model.ModelCode,
                modelCode.Trim(),
                StringComparison.OrdinalIgnoreCase))?.DeepClone();
        }
    }

    public static CameraLibrary Load(CameraLibraryDocument? document) => new(document);

    /// <summary>
    /// Returns a normalized deep copy without retaining any input references.
    /// </summary>
    public static CameraLibraryDocument Normalize(CameraLibraryDocument? document)
        => new CameraLibrary(document).Document;

    public CameraPreset? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalizedName = name.Trim();
        lock (gate)
        {
            return cameras.FirstOrDefault(camera => NameComparer.Equals(camera.Name, normalizedName))?.DeepClone();
        }
    }

    public CameraPreset? Find(Guid id)
    {
        lock (gate)
        {
            return cameras.FirstOrDefault(camera => camera.Id == id)?.DeepClone();
        }
    }

    public CameraPath? FindPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalizedName = name.Trim();
        lock (gate)
        {
            return paths.FirstOrDefault(path => NameComparer.Equals(path.Name, normalizedName))?.DeepClone();
        }
    }

    public CameraPath? FindPath(Guid id)
    {
        lock (gate)
        {
            return paths.FirstOrDefault(path => path.Id == id)?.DeepClone();
        }
    }

    public CameraSequence? FindSequence(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalizedName = name.Trim();
        lock (gate)
        {
            return sequences
                .FirstOrDefault(sequence => NameComparer.Equals(sequence.Name, normalizedName))
                ?.DeepClone();
        }
    }

    public CameraSequence? FindSequence(Guid id)
    {
        lock (gate)
        {
            return sequences.FirstOrDefault(sequence => sequence.Id == id)?.DeepClone();
        }
    }

    public DirectorWorkflow? FindWorkflow(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalizedName = name.Trim();
        lock (gate)
        {
            return workflows
                .FirstOrDefault(workflow => NameComparer.Equals(workflow.Name, normalizedName))
                ?.DeepClone();
        }
    }

    public DirectorWorkflow? FindWorkflow(Guid id)
    {
        lock (gate)
        {
            return workflows.FirstOrDefault(workflow => workflow.Id == id)?.DeepClone();
        }
    }

    public CameraFollowRig? FindFollowRig(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalizedName = name.Trim();
        lock (gate)
        {
            return followRigs
                .FirstOrDefault(rig => NameComparer.Equals(rig.Name, normalizedName))
                ?.DeepClone();
        }
    }

    public CameraFollowRig? FindFollowRig(Guid id)
    {
        lock (gate)
        {
            return followRigs.FirstOrDefault(rig => rig.Id == id)?.DeepClone();
        }
    }

    public DirectorRecordingTrack? FindRecordingTrack(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalizedName = name.Trim();
        lock (gate)
        {
            return recordingTracks
                .FirstOrDefault(track => NameComparer.Equals(track.Name, normalizedName))
                ?.DeepClone();
        }
    }

    public DirectorRecordingTrack? FindRecordingTrack(Guid id)
    {
        lock (gate)
        {
            return recordingTracks.FirstOrDefault(track => track.Id == id)?.DeepClone();
        }
    }

    public DirectorActorPerformance? FindActorPerformance(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalizedName = name.Trim();
        lock (gate)
        {
            return actorPerformances
                .FirstOrDefault(performance => NameComparer.Equals(performance.Name, normalizedName))
                ?.DeepClone();
        }
    }

    public DirectorActorPerformance? FindActorPerformance(Guid id)
    {
        lock (gate)
        {
            return actorPerformances
                .FirstOrDefault(performance => performance.Id == id)
                ?.DeepClone();
        }
    }

    public DirectorActionAsset? FindAction(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        string normalizedName = name.Trim();
        lock (gate)
        {
            return actions.FirstOrDefault(action => NameComparer.Equals(action.Name, normalizedName))?.DeepClone();
        }
    }

    public DirectorActionAsset? FindAction(Guid id)
    {
        lock (gate)
        {
            return actions.FirstOrDefault(action => action.Id == id)?.DeepClone();
        }
    }

    /// <summary>
    /// Adds a detached, normalized preset. When the requested quick slot is in
    /// use, the new preset takes the slot and the previous owner is unassigned.
    /// </summary>
    public CameraPreset Add(CameraPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HashSet<Guid> usedIds = cameras.Select(camera => camera.Id).ToHashSet();
            HashSet<string> usedNames = cameras.Select(camera => camera.Name).ToHashSet(NameComparer);
            CameraPreset normalized = NormalizePreset(preset, usedIds, usedNames, now);

            if (normalized.QuickSlot is int slot)
            {
                ReleaseSlot(slot);
            }

            cameras.Add(normalized);
            return normalized.DeepClone();
        }
    }

    public bool Remove(Guid id)
    {
        lock (gate)
        {
            int index = cameras.FindIndex(camera => camera.Id == id);
            if (index < 0)
            {
                return false;
            }

            cameras.RemoveAt(index);
            DisableTracksForSource(DirectorRecordingSourceType.StaticCamera, id);
            DisableWorkflowCardsForSource(DirectorRecordingSourceType.StaticCamera, id);
            foreach (CameraSequence sequence in sequences)
            {
                int removed = sequence.Shots.RemoveAll(shot => shot is not null && shot.CameraId == id);
                if (removed > 0)
                {
                    sequence.ModifiedUtc = NextModifiedUtc(sequence.CreatedUtc, sequence.ModifiedUtc);
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Replaces position, dimension, orientation, and field of view while
    /// retaining id, name, tags, description, slot, and creation time.
    /// </summary>
    public bool UpdatePose(Guid id, CameraPreset pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        lock (gate)
        {
            CameraPreset? camera = cameras.FirstOrDefault(candidate => candidate.Id == id);
            if (camera is null)
            {
                return false;
            }

            camera.X = NormalizeCoordinate(pose.X);
            camera.Y = NormalizeCoordinate(pose.Y);
            camera.Z = NormalizeCoordinate(pose.Z);
            camera.Dimension = pose.Dimension;
            camera.Yaw = NormalizeAngle(pose.Yaw);
            camera.Pitch = NormalizeAngle(pose.Pitch);
            camera.Roll = NormalizeAngle(pose.Roll);
            camera.Fov = NormalizeFov(pose.Fov);
            camera.ModifiedUtc = NextModifiedUtc(camera.CreatedUtc, camera.ModifiedUtc);
            return true;
        }
    }

    /// <summary>
    /// Updates user-editable metadata plus roll/FOV. A newly assigned quick
    /// slot takes ownership from any other preset.
    /// </summary>
    public bool UpdateMetadata(
        Guid id,
        string name,
        IEnumerable<string> tags,
        string description,
        float fov,
        float roll,
        int? slot)
    {
        lock (gate)
        {
            CameraPreset? camera = cameras.FirstOrDefault(candidate => candidate.Id == id);
            if (camera is null)
            {
                return false;
            }

            HashSet<string> usedNames = cameras
                .Where(candidate => candidate.Id != id)
                .Select(candidate => candidate.Name)
                .ToHashSet(NameComparer);

            int? normalizedSlot = NormalizeQuickSlot(slot);
            DateTimeOffset now = NextModifiedUtc(camera.CreatedUtc, camera.ModifiedUtc);
            if (normalizedSlot is int quickSlot)
            {
                ReleaseSlot(quickSlot);
            }

            camera.Name = ReserveUniqueName(name, usedNames);
            camera.Tags = NormalizeTags(tags);
            camera.Description = NormalizeDescription(description);
            camera.Fov = NormalizeFov(fov);
            camera.Roll = NormalizeAngle(roll);
            camera.QuickSlot = normalizedSlot;
            camera.ModifiedUtc = now;
            return true;
        }
    }

    public bool UpdateMetadata(
        Guid id,
        string name,
        IEnumerable<string> tags,
        string description,
        double fov,
        double roll,
        int? slot)
    {
        return UpdateMetadata(id, name, tags, description, (float)fov, (float)roll, slot);
    }

    public bool AssignQuickSlot(Guid id, int? slot)
    {
        lock (gate)
        {
            CameraPreset? camera = cameras.FirstOrDefault(candidate => candidate.Id == id);
            if (camera is null)
            {
                return false;
            }

            int? normalizedSlot = NormalizeQuickSlot(slot);
            DateTimeOffset now = NextModifiedUtc(camera.CreatedUtc, camera.ModifiedUtc);
            if (normalizedSlot is int quickSlot)
            {
                ReleaseSlot(quickSlot);
            }

            camera.QuickSlot = normalizedSlot;
            camera.ModifiedUtc = now;
            return true;
        }
    }

    public bool SetCameraPreviewVisible(Guid id, bool visible)
    {
        lock (gate)
        {
            CameraPreset? camera = cameras.FirstOrDefault(candidate => candidate.Id == id);
            if (camera is null)
            {
                return false;
            }

            camera.PreviewVisible = visible;
            camera.ModifiedUtc = NextModifiedUtc(camera.CreatedUtc, camera.ModifiedUtc);
            return true;
        }
    }

    public bool SetPathPreviewVisible(Guid id, bool visible)
    {
        lock (gate)
        {
            CameraPath? path = paths.FirstOrDefault(candidate => candidate.Id == id);
            if (path is null)
            {
                return false;
            }

            path.PreviewVisible = visible;
            path.ModifiedUtc = NextModifiedUtc(path.CreatedUtc, path.ModifiedUtc);
            return true;
        }
    }

    public CameraPath AddPath(CameraPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HashSet<Guid> usedIds = paths.Select(candidate => candidate.Id).ToHashSet();
            HashSet<string> usedNames = paths.Select(candidate => candidate.Name).ToHashSet(NameComparer);
            CameraPath normalized = NormalizePath(path, usedIds, usedNames, now);
            paths.Add(normalized);
            return normalized.DeepClone();
        }
    }

    public bool RemovePath(Guid id)
    {
        lock (gate)
        {
            int index = paths.FindIndex(path => path.Id == id);
            if (index < 0)
            {
                return false;
            }

            paths.RemoveAt(index);
            DisableTracksForSource(DirectorRecordingSourceType.Path, id);
            DisableWorkflowCardsForSource(DirectorRecordingSourceType.Path, id);
            return true;
        }
    }

    public bool UpdatePathSettings(
        Guid id,
        string name,
        CameraPathInterpolation interpolation,
        CameraPathLoopMode loopMode,
        bool previewVisible)
    {
        lock (gate)
        {
            CameraPath? path = paths.FirstOrDefault(candidate => candidate.Id == id);
            if (path is null)
            {
                return false;
            }

            HashSet<string> usedNames = paths
                .Where(candidate => candidate.Id != id)
                .Select(candidate => candidate.Name)
                .ToHashSet(NameComparer);
            path.Name = ReserveUniqueName(name, usedNames, DefaultPathName);
            path.Interpolation = Enum.IsDefined(interpolation)
                ? interpolation
                : CameraPathInterpolation.EaseInOut;
            path.LoopMode = Enum.IsDefined(loopMode) ? loopMode : CameraPathLoopMode.Once;
            path.PreviewVisible = previewVisible;
            path.ModifiedUtc = NextModifiedUtc(path.CreatedUtc, path.ModifiedUtc);
            return true;
        }
    }

    public bool UpdatePath(CameraPath updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        lock (gate)
        {
            CameraPath? path = paths.FirstOrDefault(candidate => candidate.Id == updated.Id);
            if (path is null)
            {
                return false;
            }

            HashSet<string> usedNames = paths
                .Where(candidate => candidate.Id != updated.Id)
                .Select(candidate => candidate.Name)
                .ToHashSet(NameComparer);
            path.Name = ReserveUniqueName(updated.Name, usedNames, DefaultPathName);
            path.Tags = NormalizeTags(updated.Tags);
            path.Description = NormalizeDescription(updated.Description);
            path.Interpolation = Enum.IsDefined(updated.Interpolation)
                ? updated.Interpolation
                : CameraPathInterpolation.EaseInOut;
            path.LoopMode = Enum.IsDefined(updated.LoopMode)
                ? updated.LoopMode
                : CameraPathLoopMode.Once;
            path.PreviewVisible = updated.PreviewVisible;
            path.PlaybackSpeed = CameraPathTiming.NormalizePlaybackSpeed(updated.PlaybackSpeed);
            path.SourceReplayId = updated.SourceReplayId;
            path.SourceActorKey = NormalizeText(updated.SourceActorKey);
            path.ActorYawConvention = NormalizeActorYawConvention(updated.ActorYawConvention);
            path.ActorEyeHeightOffset = NormalizeActorEyeHeightOffset(updated.ActorEyeHeightOffset);
            path.ReplayTimeOffsetSeconds = NormalizeReplayTimeOffset(updated.ReplayTimeOffsetSeconds);
            path.IncludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
                updated.IncludedPreloadColumns);
            path.ExcludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
                updated.ExcludedPreloadColumns);
            path.Keyframes = NormalizeKeyframes(updated.Keyframes);
            path.ElementTracks = DirectorPathElementTimeline.NormalizeTracks(updated.ElementTracks);
            path.ElementKeyframes = DirectorPathElementTimeline.NormalizeKeyframes(
                path.ElementTracks,
                updated.ElementKeyframes);
            path.ModifiedUtc = NextModifiedUtc(path.CreatedUtc, path.ModifiedUtc);
            return true;
        }
    }

    public bool UpsertPathKeyframe(Guid pathId, CameraKeyframe keyframe)
    {
        ArgumentNullException.ThrowIfNull(keyframe);

        lock (gate)
        {
            CameraPath? path = paths.FirstOrDefault(candidate => candidate.Id == pathId);
            if (path is null)
            {
                return false;
            }

            CameraKeyframe normalized = NormalizeKeyframe(keyframe);
            _ = CameraPathTiming.UpsertKeyframe(path, normalized);
            path.ModifiedUtc = NextModifiedUtc(path.CreatedUtc, path.ModifiedUtc);
            return true;
        }
    }

    public bool RemovePathKeyframe(Guid pathId, int index)
    {
        lock (gate)
        {
            CameraPath? path = paths.FirstOrDefault(candidate => candidate.Id == pathId);
            if (path is null || index < 0 || index >= path.Keyframes.Count)
            {
                return false;
            }

            _ = CameraPathTiming.RemoveKeyframe(path, index);
            path.ModifiedUtc = NextModifiedUtc(path.CreatedUtc, path.ModifiedUtc);
            return true;
        }
    }

    public CameraSequence AddSequence(CameraSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HashSet<Guid> usedIds = sequences.Select(candidate => candidate.Id).ToHashSet();
            HashSet<string> usedNames = sequences.Select(candidate => candidate.Name).ToHashSet(NameComparer);
            HashSet<Guid> cameraIds = cameras.Select(camera => camera.Id).ToHashSet();
            CameraSequence normalized = NormalizeSequence(sequence, usedIds, usedNames, cameraIds, now);
            sequences.Add(normalized);
            return normalized.DeepClone();
        }
    }

    public bool UpdateSequence(CameraSequence updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        lock (gate)
        {
            CameraSequence? sequence = sequences.FirstOrDefault(candidate => candidate.Id == updated.Id);
            if (sequence is null)
            {
                return false;
            }

            HashSet<string> usedNames = sequences
                .Where(candidate => candidate.Id != updated.Id)
                .Select(candidate => candidate.Name)
                .ToHashSet(NameComparer);
            HashSet<Guid> cameraIds = cameras.Select(camera => camera.Id).ToHashSet();
            sequence.Name = ReserveUniqueName(updated.Name, usedNames, DefaultSequenceName);
            sequence.LoopMode = Enum.IsDefined(updated.LoopMode)
                ? updated.LoopMode
                : CameraSequenceLoopMode.Once;
            sequence.Shots = NormalizeSequenceShots(updated.Shots, cameraIds);
            sequence.ModifiedUtc = NextModifiedUtc(sequence.CreatedUtc, sequence.ModifiedUtc);
            return true;
        }
    }

    public bool RemoveSequence(Guid id)
    {
        lock (gate)
        {
            int index = sequences.FindIndex(sequence => sequence.Id == id);
            if (index < 0)
            {
                return false;
            }

            sequences.RemoveAt(index);
            DisableTracksForSource(DirectorRecordingSourceType.Sequence, id);
            DisableWorkflowCardsForSource(DirectorRecordingSourceType.Sequence, id);
            return true;
        }
    }

    public DirectorWorkflow AddWorkflow(DirectorWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DirectorWorkflow normalized = NormalizeWorkflow(
                workflow,
                workflows.Select(candidate => candidate.Id).ToHashSet(),
                workflows.Select(candidate => candidate.Name).ToHashSet(NameComparer),
                cameras.Select(camera => camera.Id).ToHashSet(),
                paths.Select(path => path.Id).ToHashSet(),
                sequences.Select(sequence => sequence.Id).ToHashSet(),
                followRigs.Select(rig => rig.Id).ToHashSet(),
                actorPerformances.Select(performance => performance.Id).ToHashSet(),
                actions.Select(action => action.Id).ToHashSet(),
                now);
            if (normalized.QuickSlot is int slot)
            {
                ReleaseSlot(slot);
            }
            workflows.Add(normalized);
            return normalized.DeepClone();
        }
    }

    public bool UpdateWorkflow(DirectorWorkflow updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        lock (gate)
        {
            int index = workflows.FindIndex(candidate => candidate.Id == updated.Id);
            if (index < 0)
            {
                return false;
            }

            DirectorWorkflow existing = workflows[index];
            DirectorWorkflow normalized = NormalizeWorkflow(
                updated,
                new HashSet<Guid> { updated.Id },
                workflows.Where(candidate => candidate.Id != updated.Id)
                    .Select(candidate => candidate.Name)
                    .ToHashSet(NameComparer),
                cameras.Select(camera => camera.Id).ToHashSet(),
                paths.Select(path => path.Id).ToHashSet(),
                sequences.Select(sequence => sequence.Id).ToHashSet(),
                followRigs.Select(rig => rig.Id).ToHashSet(),
                actorPerformances.Select(performance => performance.Id).ToHashSet(),
                actions.Select(action => action.Id).ToHashSet(),
                DateTimeOffset.UtcNow);
            normalized.Id = existing.Id;
            normalized.CreatedUtc = existing.CreatedUtc;
            normalized.ModifiedUtc = NextModifiedUtc(existing.CreatedUtc, existing.ModifiedUtc);
            if (normalized.QuickSlot is int slot)
            {
                ReleaseSlot(slot);
            }
            workflows[index] = normalized;
            return true;
        }
    }

    public bool RemoveWorkflow(Guid id)
    {
        lock (gate)
        {
            int index = workflows.FindIndex(workflow => workflow.Id == id);
            if (index < 0)
            {
                return false;
            }

            workflows.RemoveAt(index);
            DisableTracksForSource(DirectorRecordingSourceType.Workflow, id);
            return true;
        }
    }

    public bool UpsertWorkflowCard(Guid workflowId, DirectorWorkflowCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        lock (gate)
        {
            DirectorWorkflow? workflow = workflows.FirstOrDefault(candidate => candidate.Id == workflowId);
            if (workflow is null)
            {
                return false;
            }
            if (!DirectorWorkflowPlanner.IsCardKindAllowed(DirectorWorkflowType.Recording, card.Kind))
            {
                return false;
            }

            HashSet<Guid> usedIds = workflow.Cards
                .Where(candidate => candidate.Id != card.Id)
                .Select(candidate => candidate.Id)
                .ToHashSet();
            DirectorWorkflowCard normalized = NormalizeWorkflowCard(
                card,
                usedIds,
                cameras.Select(camera => camera.Id).ToHashSet(),
                paths.Select(path => path.Id).ToHashSet(),
                sequences.Select(sequence => sequence.Id).ToHashSet(),
                followRigs.Select(rig => rig.Id).ToHashSet(),
                actorPerformances.Select(performance => performance.Id).ToHashSet(),
                actions.Select(action => action.Id).ToHashSet(),
                workflow.Cards.Count);
            int index = workflow.Cards.FindIndex(candidate => candidate.Id == card.Id && card.Id != Guid.Empty);
            if (index >= 0)
            {
                normalized.Id = workflow.Cards[index].Id;
                workflow.Cards[index] = normalized;
            }
            else
            {
                workflow.Cards.Add(normalized);
            }

            NormalizeWorkflowCardOrder(workflow.Cards);
            workflow.ModifiedUtc = NextModifiedUtc(workflow.CreatedUtc, workflow.ModifiedUtc);
            return true;
        }
    }

    public bool RemoveWorkflowCard(Guid workflowId, Guid cardId)
    {
        lock (gate)
        {
            DirectorWorkflow? workflow = workflows.FirstOrDefault(candidate => candidate.Id == workflowId);
            if (workflow is null || workflow.Cards.RemoveAll(card => card.Id == cardId) == 0)
            {
                return false;
            }

            NormalizeWorkflowCardOrder(workflow.Cards);
            workflow.ModifiedUtc = NextModifiedUtc(workflow.CreatedUtc, workflow.ModifiedUtc);
            return true;
        }
    }

    public CameraFollowRig AddFollowRig(CameraFollowRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);

        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HashSet<Guid> usedIds = followRigs.Select(candidate => candidate.Id).ToHashSet();
            HashSet<string> usedNames = followRigs.Select(candidate => candidate.Name).ToHashSet(NameComparer);
            CameraFollowRig normalized = NormalizeFollowRig(rig, usedIds, usedNames, now);
            if (normalized.QuickSlot is int slot)
            {
                ReleaseSlot(slot);
            }

            followRigs.Add(normalized);
            return normalized.DeepClone();
        }
    }

    public bool UpdateFollowRig(CameraFollowRig updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        lock (gate)
        {
            CameraFollowRig? rig = followRigs.FirstOrDefault(candidate => candidate.Id == updated.Id);
            if (rig is null)
            {
                return false;
            }

            HashSet<string> usedNames = followRigs
                .Where(candidate => candidate.Id != updated.Id)
                .Select(candidate => candidate.Name)
                .ToHashSet(NameComparer);
            int? slot = NormalizeQuickSlot(updated.QuickSlot);
            if (slot is int quickSlot)
            {
                ReleaseSlot(quickSlot);
            }

            rig.Name = ReserveUniqueName(updated.Name, usedNames, DefaultFollowName);
            rig.Tags = NormalizeTags(updated.Tags);
            rig.Description = NormalizeDescription(updated.Description);
            rig.TargetType = Enum.IsDefined(updated.TargetType)
                ? updated.TargetType
                : CameraFollowTargetType.Player;
            rig.TargetPlayerUid = NormalizeText(updated.TargetPlayerUid);
            rig.TargetEntityId = updated.TargetEntityId > 0 ? updated.TargetEntityId : 0;
            rig.TargetDisplayName = NormalizeText(updated.TargetDisplayName);
            rig.ViewMode = Enum.IsDefined(updated.ViewMode)
                ? updated.ViewMode
                : CameraFollowViewMode.ThirdPerson;
            rig.OpenClearView = updated.OpenClearView;
            rig.MotionMode = Enum.IsDefined(updated.MotionMode)
                ? updated.MotionMode
                : CameraFollowMotionMode.Smooth;
            rig.Anchor = Enum.IsDefined(updated.Anchor)
                ? updated.Anchor
                : CameraFollowAnchor.Head;
            rig.OffsetX = CameraFollowMath.NormalizeOffset(updated.OffsetX);
            rig.OffsetY = CameraFollowMath.NormalizeOffset(updated.OffsetY);
            rig.OffsetZ = CameraFollowMath.NormalizeOffset(updated.OffsetZ);
            rig.Distance = CameraFollowMath.NormalizeDistance(updated.Distance);
            rig.Height = CameraFollowMath.NormalizeHeight(updated.Height);
            rig.OrbitAngleDegrees = CameraFollowMath.NormalizeDegrees(updated.OrbitAngleDegrees);
            rig.OrbitSpeedDegreesPerSecond = CameraFollowMath.NormalizeOrbitSpeed(
                updated.OrbitSpeedDegreesPerSecond);
            rig.SmoothTimeSeconds = CameraFollowMath.NormalizeSmoothTime(updated.SmoothTimeSeconds);
            rig.DelaySeconds = CameraFollowMath.NormalizeDelay(updated.DelaySeconds);
            rig.Fov = CameraFollowMath.NormalizeFov(updated.Fov);
            rig.QuickSlot = slot;
            rig.ModifiedUtc = NextModifiedUtc(rig.CreatedUtc, rig.ModifiedUtc);
            return true;
        }
    }

    public bool RemoveFollowRig(Guid id)
    {
        lock (gate)
        {
            int index = followRigs.FindIndex(rig => rig.Id == id);
            if (index < 0)
            {
                return false;
            }

            followRigs.RemoveAt(index);
            DisableTracksForSource(DirectorRecordingSourceType.Follow, id);
            DisableWorkflowCardsForSource(DirectorRecordingSourceType.Follow, id);
            return true;
        }
    }

    public bool AssignFollowQuickSlot(Guid id, int? slot)
    {
        lock (gate)
        {
            CameraFollowRig? rig = followRigs.FirstOrDefault(candidate => candidate.Id == id);
            if (rig is null)
            {
                return false;
            }

            int? normalizedSlot = NormalizeQuickSlot(slot);
            if (normalizedSlot is int quickSlot)
            {
                ReleaseSlot(quickSlot);
            }

            rig.QuickSlot = normalizedSlot;
            rig.ModifiedUtc = NextModifiedUtc(rig.CreatedUtc, rig.ModifiedUtc);
            return true;
        }
    }

    public DirectorRecordingTrack AddRecordingTrack(DirectorRecordingTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HashSet<Guid> usedIds = recordingTracks.Select(candidate => candidate.Id).ToHashSet();
            HashSet<string> usedNames = recordingTracks
                .Select(candidate => candidate.Name)
                .ToHashSet(NameComparer);
            DirectorRecordingTrack normalized = NormalizeRecordingTrack(
                track,
                usedIds,
                usedNames,
                cameras.Select(camera => camera.Id).ToHashSet(),
                paths.Select(path => path.Id).ToHashSet(),
                sequences.Select(sequence => sequence.Id).ToHashSet(),
                followRigs.Select(rig => rig.Id).ToHashSet(),
                workflows.Select(workflow => workflow.Id).ToHashSet(),
                now);
            recordingTracks.Add(normalized);
            return normalized.DeepClone();
        }
    }

    public bool UpdateRecordingTrack(DirectorRecordingTrack updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        lock (gate)
        {
            DirectorRecordingTrack? track = recordingTracks.FirstOrDefault(candidate => candidate.Id == updated.Id);
            if (track is null)
            {
                return false;
            }

            HashSet<string> usedNames = recordingTracks
                .Where(candidate => candidate.Id != updated.Id)
                .Select(candidate => candidate.Name)
                .ToHashSet(NameComparer);
            DirectorRecordingSourceType sourceType = Enum.IsDefined(updated.SourceType)
                ? updated.SourceType
                : DirectorRecordingSourceType.Program;
            Guid sourceId = sourceType == DirectorRecordingSourceType.Program ? Guid.Empty : updated.SourceId;
            track.Name = ReserveUniqueName(updated.Name, usedNames, DefaultRecordingTrackName);
            track.SourceType = sourceType;
            track.SourceId = sourceId;
            track.Enabled = updated.Enabled && IsRecordingSourceValid(sourceType, sourceId);
            track.ModifiedUtc = NextModifiedUtc(track.CreatedUtc, track.ModifiedUtc);
            return true;
        }
    }

    public bool RemoveRecordingTrack(Guid id)
    {
        lock (gate)
        {
            int index = recordingTracks.FindIndex(track => track.Id == id);
            if (index < 0)
            {
                return false;
            }

            recordingTracks.RemoveAt(index);
            return true;
        }
    }

    public DirectorActorPerformance AddActorPerformance(DirectorActorPerformance performance)
    {
        ArgumentNullException.ThrowIfNull(performance);

        lock (gate)
        {
            DirectorActorPerformance normalized = NormalizeActorPerformance(
                performance,
                actorPerformances.Select(candidate => candidate.Id).ToHashSet(),
                actorPerformances.Select(candidate => candidate.Name).ToHashSet(NameComparer),
                DateTimeOffset.UtcNow);
            actorPerformances.Add(normalized);
            return normalized.DeepClone();
        }
    }

    public bool UpdateActorPerformance(DirectorActorPerformance updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        lock (gate)
        {
            int index = actorPerformances.FindIndex(performance => performance.Id == updated.Id);
            if (index < 0)
            {
                return false;
            }

            DirectorActorPerformance previous = actorPerformances[index];
            DirectorActorPerformance source = updated.DeepClone();
            source.Id = previous.Id;
            source.CreatedUtc = previous.CreatedUtc;
            source.ModifiedUtc = NextModifiedUtc(previous.CreatedUtc, previous.ModifiedUtc);
            HashSet<Guid> usedIds = actorPerformances
                .Where(performance => performance.Id != previous.Id)
                .Select(performance => performance.Id)
                .ToHashSet();
            HashSet<string> usedNames = actorPerformances
                .Where(performance => performance.Id != previous.Id)
                .Select(performance => performance.Name)
                .ToHashSet(NameComparer);
            actorPerformances[index] = NormalizeActorPerformance(
                source,
                usedIds,
                usedNames,
                source.ModifiedUtc);
            return true;
        }
    }

    public bool RemoveActorPerformance(Guid id)
    {
        lock (gate)
        {
            int index = actorPerformances.FindIndex(performance => performance.Id == id);
            if (index < 0)
            {
                return false;
            }

            actorPerformances.RemoveAt(index);
            return true;
        }
    }

    public DirectorActionAsset AddAction(DirectorActionAsset action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            DirectorActionAsset normalized = NormalizeAction(
                action,
                actions.Select(candidate => candidate.Id).ToHashSet(),
                actions.Select(candidate => candidate.Name).ToHashSet(NameComparer),
                DateTimeOffset.UtcNow);
            actions.Add(normalized);
            return normalized.DeepClone();
        }
    }

    public bool UpdateAction(DirectorActionAsset updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (gate)
        {
            int index = actions.FindIndex(action => action.Id == updated.Id);
            if (index < 0)
            {
                return false;
            }
            DirectorActionAsset previous = actions[index];
            DirectorActionAsset source = updated.DeepClone();
            source.Id = previous.Id;
            source.CreatedUtc = previous.CreatedUtc;
            source.ModifiedUtc = NextModifiedUtc(previous.CreatedUtc, previous.ModifiedUtc);
            actions[index] = NormalizeAction(
                source,
                actions.Where(action => action.Id != previous.Id).Select(action => action.Id).ToHashSet(),
                actions.Where(action => action.Id != previous.Id).Select(action => action.Name).ToHashSet(NameComparer),
                source.ModifiedUtc);
            return true;
        }
    }

    public bool RemoveAction(Guid id, Guid? replacementId = null)
    {
        lock (gate)
        {
            int index = actions.FindIndex(action => action.Id == id);
            if (index < 0)
            {
                return false;
            }
            actions.RemoveAt(index);
            bool replaceReferences = replacementId is Guid replacement
                && replacement != id
                && actions.Any(action => action.Id == replacement);
            foreach (DirectorWorkflow workflow in workflows)
            {
                bool changed = false;
                foreach (DirectorWorkflowCard card in workflow.Cards.Where(card => card.ActionId == id))
                {
                    if (replaceReferences)
                    {
                        card.ActionId = replacementId!.Value;
                    }
                    else
                    {
                        card.Enabled = false;
                    }
                    changed = true;
                }
                if (changed)
                {
                    workflow.ModifiedUtc = NextModifiedUtc(workflow.CreatedUtc, workflow.ModifiedUtc);
                }
            }
            return true;
        }
    }

    public DirectorModelAsset AddModel(DirectorModelAsset model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (gate)
        {
            DirectorModelAsset normalized = NormalizeModel(
                model,
                models.Select(candidate => candidate.Id).ToHashSet(),
                models.Select(candidate => candidate.Name).ToHashSet(NameComparer),
                DateTimeOffset.UtcNow);
            models.Add(normalized);
            return normalized.DeepClone();
        }
    }

    public bool UpdateModel(DirectorModelAsset updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (gate)
        {
            int index = models.FindIndex(model => model.Id == updated.Id);
            if (index < 0)
            {
                return false;
            }
            DirectorModelAsset previous = models[index];
            DirectorModelAsset source = updated.DeepClone();
            source.Id = previous.Id;
            source.CreatedUtc = previous.CreatedUtc;
            source.ModifiedUtc = NextModifiedUtc(previous.CreatedUtc, previous.ModifiedUtc);
            models[index] = NormalizeModel(
                source,
                new HashSet<Guid>(),
                models.Where(model => model.Id != source.Id).Select(model => model.Name).ToHashSet(NameComparer),
                source.ModifiedUtc);
            return true;
        }
    }

    public bool RemoveModel(Guid id)
    {
        lock (gate)
        {
            return models.RemoveAll(model => model.Id == id) > 0;
        }
    }

    private CameraLibraryDocument CreateDocumentSnapshot()
    {
        return new CameraLibraryDocument
        {
            SchemaVersion = CameraLibraryDocument.CurrentSchemaVersion,
            Cameras = OrderedInternal().Select(camera => camera.DeepClone()).ToList(),
            Paths = OrderedPathsInternal().Select(path => path.DeepClone()).ToList(),
            Sequences = OrderedSequencesInternal().Select(sequence => sequence.DeepClone()).ToList(),
            Workflows = OrderedWorkflowsInternal().Select(workflow => workflow.DeepClone()).ToList(),
            FollowRigs = OrderedFollowRigsInternal().Select(rig => rig.DeepClone()).ToList(),
            RecordingTracks = OrderedRecordingTracksInternal().Select(track => track.DeepClone()).ToList(),
            ActorPerformances = OrderedActorPerformancesInternal()
                .Select(performance => performance.DeepClone())
                .ToList(),
            Actions = OrderedActionsInternal().Select(action => action.DeepClone()).ToList(),
            Models = OrderedModelsInternal().Select(model => model.DeepClone()).ToList()
        };
    }

    private IOrderedEnumerable<CameraPreset> OrderedInternal()
        => cameras.OrderBy(camera => camera.Name, NameComparer);

    private IOrderedEnumerable<CameraPath> OrderedPathsInternal()
        => paths.OrderBy(path => path.Name, NameComparer);

    private IOrderedEnumerable<CameraSequence> OrderedSequencesInternal()
        => sequences.OrderBy(sequence => sequence.Name, NameComparer);

    private IOrderedEnumerable<DirectorWorkflow> OrderedWorkflowsInternal()
        => workflows.OrderBy(workflow => workflow.Name, NameComparer);

    private IOrderedEnumerable<CameraFollowRig> OrderedFollowRigsInternal()
        => followRigs.OrderBy(rig => rig.Name, NameComparer);

    private IOrderedEnumerable<DirectorRecordingTrack> OrderedRecordingTracksInternal()
        => recordingTracks.OrderBy(track => track.Name, NameComparer);

    private IOrderedEnumerable<DirectorActorPerformance> OrderedActorPerformancesInternal()
        => actorPerformances.OrderBy(performance => performance.Name, NameComparer);

    private IOrderedEnumerable<DirectorActionAsset> OrderedActionsInternal()
        => actions.OrderByDescending(action => action.Favorite)
            .ThenBy(action => action.Category)
            .ThenBy(action => action.Name, NameComparer);

    private IOrderedEnumerable<DirectorModelAsset> OrderedModelsInternal()
        => models.OrderByDescending(model => model.Favorite)
            .ThenBy(model => model.Group, NameComparer)
            .ThenBy(model => model.Name, NameComparer);

    private void DisableTracksForSource(DirectorRecordingSourceType sourceType, Guid sourceId)
    {
        foreach (DirectorRecordingTrack track in recordingTracks)
        {
            if (track.SourceType != sourceType || track.SourceId != sourceId || !track.Enabled)
            {
                continue;
            }

            track.Enabled = false;
            track.ModifiedUtc = NextModifiedUtc(track.CreatedUtc, track.ModifiedUtc);
        }
    }

    private void DisableWorkflowCardsForSource(DirectorRecordingSourceType sourceType, Guid sourceId)
    {
        foreach (DirectorWorkflow workflow in workflows)
        {
            bool changed = false;
            foreach (DirectorWorkflowCard card in workflow.Cards)
            {
                if (card.SourceType == sourceType && card.SourceId == sourceId && card.Enabled)
                {
                    card.Enabled = false;
                    changed = true;
                }
            }

            if (changed)
            {
                workflow.ModifiedUtc = NextModifiedUtc(workflow.CreatedUtc, workflow.ModifiedUtc);
            }
        }
    }

    private bool IsRecordingSourceValid(DirectorRecordingSourceType sourceType, Guid sourceId)
    {
        return sourceType switch
        {
            DirectorRecordingSourceType.Program => sourceId == Guid.Empty,
            DirectorRecordingSourceType.StaticCamera => cameras.Any(camera => camera.Id == sourceId),
            DirectorRecordingSourceType.Path => paths.Any(path => path.Id == sourceId),
            DirectorRecordingSourceType.Sequence => sequences.Any(sequence => sequence.Id == sourceId),
            DirectorRecordingSourceType.Follow => followRigs.Any(rig => rig.Id == sourceId),
            DirectorRecordingSourceType.Workflow => workflows.Any(workflow => workflow.Id == sourceId),
            _ => false
        };
    }

    private void ReleaseSlot(int slot)
    {
        foreach (CameraPreset camera in cameras)
        {
            if (camera.QuickSlot != slot)
            {
                continue;
            }

            camera.QuickSlot = null;
            camera.ModifiedUtc = NextModifiedUtc(camera.CreatedUtc, camera.ModifiedUtc);
        }

        foreach (CameraFollowRig rig in followRigs)
        {
            if (rig.QuickSlot != slot)
            {
                continue;
            }

            rig.QuickSlot = null;
            rig.ModifiedUtc = NextModifiedUtc(rig.CreatedUtc, rig.ModifiedUtc);
        }

        foreach (DirectorWorkflow workflow in workflows)
        {
            if (workflow.QuickSlot != slot)
            {
                continue;
            }
            workflow.QuickSlot = null;
            workflow.ModifiedUtc = NextModifiedUtc(workflow.CreatedUtc, workflow.ModifiedUtc);
        }
    }

    private static CameraLibraryDocument NormalizeDocument(CameraLibraryDocument? document)
    {
        if (document is not null && document.SchemaVersion > CameraLibraryDocument.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Camera library schema {document.SchemaVersion} is newer than supported schema {CameraLibraryDocument.CurrentSchemaVersion}.");
        }

        int sourceSchemaVersion = document?.SchemaVersion ?? 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HashSet<Guid> usedIds = new();
        HashSet<string> usedNames = new(NameComparer);
        HashSet<int> usedSlots = new();
        List<CameraPreset> normalized = new();
        HashSet<Guid> usedPathIds = new();
        HashSet<string> usedPathNames = new(NameComparer);
        List<CameraPath> normalizedPaths = new();
        HashSet<Guid> usedSequenceIds = new();
        HashSet<string> usedSequenceNames = new(NameComparer);
        List<CameraSequence> normalizedSequences = new();
        HashSet<Guid> usedWorkflowIds = new();
        HashSet<string> usedWorkflowNames = new(NameComparer);
        List<DirectorWorkflow> normalizedWorkflows = new();
        HashSet<Guid> usedFollowIds = new();
        HashSet<string> usedFollowNames = new(NameComparer);
        List<CameraFollowRig> normalizedFollowRigs = new();
        HashSet<Guid> usedRecordingTrackIds = new();
        HashSet<string> usedRecordingTrackNames = new(NameComparer);
        List<DirectorRecordingTrack> normalizedRecordingTracks = new();
        HashSet<Guid> usedActorPerformanceIds = new();
        HashSet<string> usedActorPerformanceNames = new(NameComparer);
        List<DirectorActorPerformance> normalizedActorPerformances = new();
        HashSet<Guid> usedActionIds = new();
        HashSet<string> usedActionNames = new(NameComparer);
        List<DirectorActionAsset> normalizedActions = new();
        HashSet<Guid> usedModelIds = new();
        HashSet<string> usedModelNames = new(NameComparer);
        List<DirectorModelAsset> normalizedModels = new();

        if (document?.Cameras is not null)
        {
            foreach (CameraPreset? source in document.Cameras)
            {
                if (source is null)
                {
                    continue;
                }

                CameraPreset camera = NormalizePreset(source, usedIds, usedNames, now);
                if (camera.QuickSlot is int slot && !usedSlots.Add(slot))
                {
                    camera.QuickSlot = null;
                }

                normalized.Add(camera);
            }
        }

        if (document?.Paths is not null)
        {
            foreach (CameraPath? source in document.Paths)
            {
                if (source is not null)
                {
                    normalizedPaths.Add(NormalizePath(
                        source,
                        usedPathIds,
                        usedPathNames,
                        now,
                        migrateLegacyActorYaw: sourceSchemaVersion == 14));
                }
            }
        }

        if (document?.FollowRigs is not null)
        {
            foreach (CameraFollowRig? source in document.FollowRigs)
            {
                if (source is null)
                {
                    continue;
                }

                CameraFollowRig rig = NormalizeFollowRig(source, usedFollowIds, usedFollowNames, now);
                if (rig.QuickSlot is int slot && !usedSlots.Add(slot))
                {
                    rig.QuickSlot = null;
                }

                normalizedFollowRigs.Add(rig);
            }
        }

        HashSet<Guid> normalizedCameraIds = normalized.Select(camera => camera.Id).ToHashSet();
        if (document?.Sequences is not null)
        {
            foreach (CameraSequence? source in document.Sequences)
            {
                if (source is not null)
                {
                    normalizedSequences.Add(NormalizeSequence(
                        source,
                        usedSequenceIds,
                        usedSequenceNames,
                        normalizedCameraIds,
                        now));
                }
            }
        }

        if (document?.ActorPerformances is not null)
        {
            foreach (DirectorActorPerformance? source in document.ActorPerformances)
            {
                if (source is not null)
                {
                    normalizedActorPerformances.Add(NormalizeActorPerformance(
                        source,
                        usedActorPerformanceIds,
                        usedActorPerformanceNames,
                        now));
                }
            }
        }

        if (document?.Actions is not null)
        {
            foreach (DirectorActionAsset? source in document.Actions)
            {
                if (source is not null)
                {
                    normalizedActions.Add(NormalizeAction(
                        source,
                        usedActionIds,
                        usedActionNames,
                        now));
                }
            }
        }

        if (document?.Models is not null)
        {
            foreach (DirectorModelAsset? source in document.Models)
            {
                if (source is not null)
                {
                    normalizedModels.Add(NormalizeModel(
                        source,
                        usedModelIds,
                        usedModelNames,
                        now));
                }
            }
        }

        HashSet<Guid> normalizedPathIds = normalizedPaths.Select(path => path.Id).ToHashSet();
        HashSet<Guid> normalizedSequenceIds = normalizedSequences.Select(sequence => sequence.Id).ToHashSet();
        HashSet<Guid> normalizedFollowIds = normalizedFollowRigs.Select(rig => rig.Id).ToHashSet();
        HashSet<Guid> normalizedActorPerformanceIds = normalizedActorPerformances
            .Select(performance => performance.Id)
            .ToHashSet();
        HashSet<Guid> normalizedActionIds = normalizedActions.Select(action => action.Id).ToHashSet();
        if (document?.Workflows is not null)
        {
            foreach (DirectorWorkflow? source in document.Workflows)
            {
                if (source is not null && source.Type != DirectorWorkflowType.Action)
                {
                    normalizedWorkflows.Add(NormalizeWorkflow(
                        source,
                        usedWorkflowIds,
                        usedWorkflowNames,
                        normalizedCameraIds,
                        normalizedPathIds,
                        normalizedSequenceIds,
                        normalizedFollowIds,
                        normalizedActorPerformanceIds,
                        normalizedActionIds,
                        now));
                    DirectorWorkflow workflow = normalizedWorkflows[^1];
                    if (workflow.QuickSlot is int slot && !usedSlots.Add(slot))
                    {
                        workflow.QuickSlot = null;
                    }
                }
            }
        }

        HashSet<Guid> normalizedWorkflowIds = normalizedWorkflows.Select(workflow => workflow.Id).ToHashSet();

        if (document?.RecordingTracks is not null)
        {
            foreach (DirectorRecordingTrack? source in document.RecordingTracks)
            {
                if (source is not null)
                {
                    normalizedRecordingTracks.Add(NormalizeRecordingTrack(
                        source,
                        usedRecordingTrackIds,
                        usedRecordingTrackNames,
                        normalizedCameraIds,
                        normalizedPathIds,
                        normalizedSequenceIds,
                        normalizedFollowIds,
                        normalizedWorkflowIds,
                        now));
                }
            }
        }

        return new CameraLibraryDocument
        {
            SchemaVersion = CameraLibraryDocument.CurrentSchemaVersion,
            Cameras = normalized,
            Paths = normalizedPaths,
            Sequences = normalizedSequences,
            Workflows = normalizedWorkflows,
            FollowRigs = normalizedFollowRigs,
            RecordingTracks = normalizedRecordingTracks,
            ActorPerformances = normalizedActorPerformances,
            Actions = normalizedActions,
            Models = normalizedModels
        };
    }

    private static DirectorWorkflow NormalizeWorkflow(
        DirectorWorkflow source,
        HashSet<Guid> usedIds,
        HashSet<string> usedNames,
        HashSet<Guid> cameraIds,
        HashSet<Guid> pathIds,
        HashSet<Guid> sequenceIds,
        HashSet<Guid> followIds,
        HashSet<Guid> actorPerformanceIds,
        HashSet<Guid> actionIds,
        DateTimeOffset now)
    {
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            }
            while (!usedIds.Add(id));
        }

        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        if (modifiedUtc < createdUtc)
        {
            modifiedUtc = createdUtc;
        }

        HashSet<Guid> usedCardIds = new();
        List<DirectorWorkflowCard> cards = new();
        foreach (DirectorWorkflowCard? card in source.Cards ?? new List<DirectorWorkflowCard>())
        {
            if (card is null
                || cards.Count >= 512
                || !DirectorWorkflowPlanner.IsCardKindAllowed(DirectorWorkflowType.Recording, card.Kind))
            {
                continue;
            }

            cards.Add(NormalizeWorkflowCard(
                card,
                usedCardIds,
                cameraIds,
                pathIds,
                sequenceIds,
                followIds,
                actorPerformanceIds,
                actionIds,
                cards.Count));
        }
        NormalizeWorkflowCardOrder(cards);

        HashSet<Guid> usedAudioIds = new();
        List<DirectorTimelineAudioClip> audioClips = new();
        foreach (DirectorTimelineAudioClip? clip in source.AudioClips ?? new List<DirectorTimelineAudioClip>())
        {
            if (clip is null || audioClips.Count >= 128 || string.IsNullOrWhiteSpace(clip.InputPath))
            {
                continue;
            }

            Guid audioId = clip.Id;
            if (audioId == Guid.Empty || !usedAudioIds.Add(audioId))
            {
                do
                {
                    audioId = Guid.NewGuid();
                }
                while (!usedAudioIds.Add(audioId));
            }

            double timelineStart = double.IsFinite(clip.TimelineStartSeconds)
                ? Math.Clamp(
                    clip.TimelineStartSeconds,
                    0d,
                    DirectorWorkflowPlanner.MaxTimelineSeconds
                        - DirectorWorkflowPlanner.MinCardDurationSeconds)
                : 0d;
            double duration = double.IsFinite(clip.DurationSeconds)
                ? Math.Clamp(
                    clip.DurationSeconds,
                    DirectorWorkflowPlanner.MinCardDurationSeconds,
                    DirectorWorkflowPlanner.MaxTimelineSeconds - timelineStart)
                : DirectorWorkflowPlanner.MinCardDurationSeconds;
            string audioName = string.IsNullOrWhiteSpace(clip.Name)
                ? $"Audio {audioClips.Count + 1}"
                : clip.Name.Trim();
            if (audioName.Length > 96)
            {
                audioName = audioName[..96];
            }

            audioClips.Add(new DirectorTimelineAudioClip
            {
                Id = audioId,
                Name = audioName,
                InputPath = clip.InputPath.Trim(),
                Track = Math.Clamp(clip.Track, 0, 63),
                TimelineStartSeconds = timelineStart,
                SourceStartSeconds = double.IsFinite(clip.SourceStartSeconds)
                    ? Math.Clamp(clip.SourceStartSeconds, 0d, DirectorWorkflowPlanner.MaxTimelineSeconds)
                    : 0d,
                DurationSeconds = duration,
                GainDecibels = double.IsFinite(clip.GainDecibels)
                    ? Math.Clamp(clip.GainDecibels, -96d, 24d)
                    : 0d,
                FadeInSeconds = double.IsFinite(clip.FadeInSeconds)
                    ? Math.Clamp(clip.FadeInSeconds, 0d, duration)
                    : 0d,
                FadeOutSeconds = double.IsFinite(clip.FadeOutSeconds)
                    ? Math.Clamp(clip.FadeOutSeconds, 0d, duration)
                    : 0d,
                PlaybackRate = double.IsFinite(clip.PlaybackRate) && clip.PlaybackRate > 0d
                    ? Math.Clamp(clip.PlaybackRate, 0.01d, 1024d)
                    : 1d,
                Enabled = clip.Enabled
            });
        }

        return new DirectorWorkflow
        {
            Id = id,
            Name = ReserveUniqueName(source.Name, usedNames, DefaultWorkflowName),
            Type = DirectorWorkflowType.Recording,
            QuickSlot = NormalizeQuickSlot(source.QuickSlot),
            Cards = cards,
            AudioClips = audioClips,
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc
        };
    }

    private static DirectorWorkflowCard NormalizeWorkflowCard(
        DirectorWorkflowCard source,
        HashSet<Guid> usedIds,
        HashSet<Guid> cameraIds,
        HashSet<Guid> pathIds,
        HashSet<Guid> sequenceIds,
        HashSet<Guid> followIds,
        HashSet<Guid> actorPerformanceIds,
        HashSet<Guid> actionIds,
        int fallbackOrder)
    {
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            }
            while (!usedIds.Add(id));
        }

        DirectorWorkflowCardKind kind = Enum.IsDefined(source.Kind)
            ? source.Kind
            : DirectorWorkflowCardKind.Shot;
        DirectorRecordingSourceType sourceType = DirectorWorkflowPlanner.IsExportSource(source.SourceType)
            ? source.SourceType
            : DirectorRecordingSourceType.StaticCamera;
        double start = double.IsFinite(source.StartSeconds)
            ? Math.Clamp(source.StartSeconds, 0d, DirectorWorkflowPlanner.MaxTimelineSeconds)
            : 0d;
        double end = double.IsFinite(source.EndSeconds)
            ? Math.Clamp(source.EndSeconds, 0d, DirectorWorkflowPlanner.MaxTimelineSeconds)
            : start + 5d;
        end = Math.Clamp(
            Math.Max(end, start + DirectorWorkflowPlanner.MinCardDurationSeconds),
            DirectorWorkflowPlanner.MinCardDurationSeconds,
            DirectorWorkflowPlanner.MaxTimelineSeconds);
        if (end <= start)
        {
            start = Math.Max(0d, end - DirectorWorkflowPlanner.MinCardDurationSeconds);
        }

        bool sourceValid = kind switch
        {
            DirectorWorkflowCardKind.Shot => sourceType switch
            {
                DirectorRecordingSourceType.StaticCamera => cameraIds.Contains(source.SourceId),
                DirectorRecordingSourceType.Path => pathIds.Contains(source.SourceId),
                DirectorRecordingSourceType.Sequence => sequenceIds.Contains(source.SourceId),
                DirectorRecordingSourceType.Follow => followIds.Contains(source.SourceId),
                _ => false
            },
            DirectorWorkflowCardKind.Action => actionIds.Contains(source.ActionId),
            DirectorWorkflowCardKind.ActorPath => actorPerformanceIds.Contains(source.ActorPerformanceId)
                || pathIds.Contains(source.PathId),
            DirectorWorkflowCardKind.Entity => !string.IsNullOrWhiteSpace(source.ActorKey),
            DirectorWorkflowCardKind.Environment => !string.IsNullOrWhiteSpace(source.EnvironmentCode),
            _ => true
        };
        string name = string.IsNullOrWhiteSpace(source.Name)
            ? $"{DefaultWorkflowCardName} {fallbackOrder + 1}"
            : source.Name.Trim();
        if (name.Length > 96)
        {
            name = name[..96];
        }

        string notes = source.Notes?.Trim() ?? string.Empty;
        if (notes.Length > 2048)
        {
            notes = notes[..2048];
        }

        DirectorVideoTransition? transition = kind != DirectorWorkflowCardKind.Shot
            ? null
            : source.TransitionIn?.DeepClone();
        if (transition is not null)
        {
            transition.Type = Enum.IsDefined(transition.Type)
                ? transition.Type
                : DirectorVideoTransitionType.Cut;
            transition.DurationSeconds = transition.Type == DirectorVideoTransitionType.Cut
                ? 0d
                : double.IsFinite(transition.DurationSeconds)
                    ? Math.Clamp(transition.DurationSeconds, 0d, end - start)
                    : 0d;
        }

        return new DirectorWorkflowCard
        {
            Id = id,
            Name = name,
            Kind = kind,
            SourceType = sourceType,
            SourceId = kind == DirectorWorkflowCardKind.Shot ? source.SourceId : Guid.Empty,
            ActorKey = NormalizeWorkflowText(source.ActorKey, 256),
            EntityCode = NormalizeWorkflowText(source.EntityCode, 128),
            ModelCode = NormalizeWorkflowText(source.ModelCode, 128),
            HideLiveActor = source.HideLiveActor,
            ActionId = kind == DirectorWorkflowCardKind.Action ? source.ActionId : Guid.Empty,
            ActorPerformanceId = kind == DirectorWorkflowCardKind.ActorPath
                ? source.ActorPerformanceId
                : Guid.Empty,
            PathId = kind == DirectorWorkflowCardKind.ActorPath ? source.PathId : Guid.Empty,
            BoundCardId = source.BoundCardId,
            SourceOffsetSeconds = double.IsFinite(source.SourceOffsetSeconds)
                ? Math.Clamp(source.SourceOffsetSeconds, 0d, DirectorWorkflowPlanner.MaxTimelineSeconds)
                : 0d,
            PlaybackSpeed = double.IsFinite(source.PlaybackSpeed)
                ? Math.Clamp(source.PlaybackSpeed, 0.01d, 64d)
                : 1d,
            ActionLoopMode = Enum.IsDefined(source.ActionLoopMode)
                ? source.ActionLoopMode
                : DirectorActionLoopMode.Once,
            ActionTriggerType = kind == DirectorWorkflowCardKind.Action
                && Enum.IsDefined(source.ActionTriggerType)
                    ? source.ActionTriggerType
                    : DirectorActionTriggerType.Manual,
            EnvironmentCode = NormalizeWorkflowText(source.EnvironmentCode, 128),
            EnvironmentData = NormalizeWorkflowText(source.EnvironmentData, 4096),
            StartSeconds = start,
            EndSeconds = end,
            Lane = Math.Clamp(source.Lane, 0, 31),
            Order = source.Order >= 0 ? source.Order : fallbackOrder,
            Enabled = source.Enabled && sourceValid,
            Notes = notes,
            TransitionIn = transition
        };
    }

    private static string NormalizeWorkflowText(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static void NormalizeWorkflowCardOrder(List<DirectorWorkflowCard> cards)
    {
        cards.Sort((left, right) =>
        {
            int comparison = left.Lane.CompareTo(right.Lane);
            if (comparison == 0)
            {
                comparison = left.StartSeconds.CompareTo(right.StartSeconds);
            }
            if (comparison == 0)
            {
                comparison = left.Order.CompareTo(right.Order);
            }
            return comparison == 0
                ? StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name)
                : comparison;
        });
        for (int index = 0; index < cards.Count; index++)
        {
            cards[index].Order = index;
        }
    }

    private static DirectorRecordingTrack NormalizeRecordingTrack(
        DirectorRecordingTrack source,
        HashSet<Guid> usedIds,
        HashSet<string> usedNames,
        HashSet<Guid> cameraIds,
        HashSet<Guid> pathIds,
        HashSet<Guid> sequenceIds,
        HashSet<Guid> followIds,
        HashSet<Guid> workflowIds,
        DateTimeOffset now)
    {
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            }
            while (!usedIds.Add(id));
        }

        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        if (modifiedUtc < createdUtc)
        {
            modifiedUtc = createdUtc;
        }

        DirectorRecordingSourceType sourceType = Enum.IsDefined(source.SourceType)
            ? source.SourceType
            : DirectorRecordingSourceType.Program;
        Guid sourceId = sourceType == DirectorRecordingSourceType.Program ? Guid.Empty : source.SourceId;
        bool valid = sourceType switch
        {
            DirectorRecordingSourceType.Program => true,
            DirectorRecordingSourceType.StaticCamera => cameraIds.Contains(sourceId),
            DirectorRecordingSourceType.Path => pathIds.Contains(sourceId),
            DirectorRecordingSourceType.Sequence => sequenceIds.Contains(sourceId),
            DirectorRecordingSourceType.Follow => followIds.Contains(sourceId),
            DirectorRecordingSourceType.Workflow => workflowIds.Contains(sourceId),
            _ => false
        };
        return new DirectorRecordingTrack
        {
            Id = id,
            Name = ReserveUniqueName(source.Name, usedNames, DefaultRecordingTrackName),
            SourceType = sourceType,
            SourceId = sourceId,
            Enabled = source.Enabled && valid,
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc
        };
    }

    private static DirectorActorPerformance NormalizeActorPerformance(
        DirectorActorPerformance source,
        HashSet<Guid> usedIds,
        HashSet<string> usedNames,
        DateTimeOffset now)
    {
        DirectorActorPerformance normalized = DirectorActorPerformanceNormalizer.Normalize(source);
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            }
            while (!usedIds.Add(id));
        }

        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        if (modifiedUtc < createdUtc)
        {
            modifiedUtc = createdUtc;
        }

        normalized.Id = id;
        normalized.Name = ReserveUniqueName(source.Name, usedNames, DefaultActorPerformanceName);
        normalized.CreatedUtc = createdUtc;
        normalized.ModifiedUtc = modifiedUtc;
        return normalized;
    }

    private static DirectorActionAsset NormalizeAction(
        DirectorActionAsset source,
        HashSet<Guid> usedIds,
        HashSet<string> usedNames,
        DateTimeOffset now)
    {
        DirectorActionAsset normalized = DirectorActionNormalizer.Normalize(source);
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do { id = Guid.NewGuid(); } while (!usedIds.Add(id));
        }
        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        normalized.Id = id;
        normalized.Name = ReserveUniqueName(source.Name, usedNames, DefaultActionName);
        normalized.CreatedUtc = createdUtc;
        normalized.ModifiedUtc = modifiedUtc < createdUtc ? createdUtc : modifiedUtc;
        return normalized;
    }

    private static DirectorModelAsset NormalizeModel(
        DirectorModelAsset source,
        HashSet<Guid> usedIds,
        HashSet<string> usedNames,
        DateTimeOffset now)
    {
        DirectorModelAsset normalized = DirectorModelNormalizer.Normalize(source);
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do { id = Guid.NewGuid(); } while (!usedIds.Add(id));
        }
        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        normalized.Id = id;
        normalized.Name = ReserveUniqueName(source.Name, usedNames, DefaultModelName);
        normalized.CreatedUtc = createdUtc;
        normalized.ModifiedUtc = modifiedUtc < createdUtc ? createdUtc : modifiedUtc;
        return normalized;
    }

    private static CameraFollowRig NormalizeFollowRig(
        CameraFollowRig source,
        HashSet<Guid> usedIds,
        HashSet<string> usedNames,
        DateTimeOffset now)
    {
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            }
            while (!usedIds.Add(id));
        }

        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        if (modifiedUtc < createdUtc)
        {
            modifiedUtc = createdUtc;
        }

        return new CameraFollowRig
        {
            Id = id,
            Name = ReserveUniqueName(source.Name, usedNames, DefaultFollowName),
            Tags = NormalizeTags(source.Tags),
            Description = NormalizeDescription(source.Description),
            TargetType = Enum.IsDefined(source.TargetType)
                ? source.TargetType
                : CameraFollowTargetType.Player,
            TargetPlayerUid = NormalizeText(source.TargetPlayerUid),
            TargetEntityId = source.TargetEntityId > 0 ? source.TargetEntityId : 0,
            TargetDisplayName = NormalizeText(source.TargetDisplayName),
            ViewMode = Enum.IsDefined(source.ViewMode)
                ? source.ViewMode
                : CameraFollowViewMode.ThirdPerson,
            OpenClearView = source.OpenClearView,
            MotionMode = Enum.IsDefined(source.MotionMode)
                ? source.MotionMode
                : CameraFollowMotionMode.Smooth,
            Anchor = Enum.IsDefined(source.Anchor) ? source.Anchor : CameraFollowAnchor.Head,
            OffsetX = CameraFollowMath.NormalizeOffset(source.OffsetX),
            OffsetY = CameraFollowMath.NormalizeOffset(source.OffsetY),
            OffsetZ = CameraFollowMath.NormalizeOffset(source.OffsetZ),
            Distance = CameraFollowMath.NormalizeDistance(source.Distance),
            Height = CameraFollowMath.NormalizeHeight(source.Height),
            OrbitAngleDegrees = CameraFollowMath.NormalizeDegrees(source.OrbitAngleDegrees),
            OrbitSpeedDegreesPerSecond = CameraFollowMath.NormalizeOrbitSpeed(
                source.OrbitSpeedDegreesPerSecond),
            SmoothTimeSeconds = CameraFollowMath.NormalizeSmoothTime(source.SmoothTimeSeconds),
            DelaySeconds = CameraFollowMath.NormalizeDelay(source.DelaySeconds),
            Fov = CameraFollowMath.NormalizeFov(source.Fov),
            QuickSlot = NormalizeQuickSlot(source.QuickSlot),
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc
        };
    }

    private static CameraPreset NormalizePreset(
        CameraPreset source,
        HashSet<Guid> usedIds,
        HashSet<string> usedNames,
        DateTimeOffset now)
    {
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            }
            while (!usedIds.Add(id));
        }

        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        if (modifiedUtc < createdUtc)
        {
            modifiedUtc = createdUtc;
        }

        return new CameraPreset
        {
            Id = id,
            Name = ReserveUniqueName(source.Name, usedNames),
            Tags = NormalizeTags(source.Tags),
            Description = NormalizeDescription(source.Description),
            X = NormalizeCoordinate(source.X),
            Y = NormalizeCoordinate(source.Y),
            Z = NormalizeCoordinate(source.Z),
            Dimension = source.Dimension,
            Yaw = NormalizeAngle(source.Yaw),
            Pitch = NormalizeAngle(source.Pitch),
            Roll = NormalizeAngle(source.Roll),
            Fov = NormalizeFov(source.Fov),
            PreviewVisible = source.PreviewVisible,
            QuickSlot = NormalizeQuickSlot(source.QuickSlot),
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc
        };
    }

    private static CameraPath NormalizePath(
        CameraPath source,
        HashSet<Guid> usedIds,
        HashSet<string> usedNames,
        DateTimeOffset now,
        bool migrateLegacyActorYaw = false)
    {
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            }
            while (!usedIds.Add(id));
        }

        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        if (modifiedUtc < createdUtc)
        {
            modifiedUtc = createdUtc;
        }

        string sourceActorKey = NormalizeText(source.SourceActorKey);
        ActorPathYawConvention actorYawConvention = NormalizeActorYawConvention(source.ActorYawConvention);
        List<CameraKeyframe> keyframes = NormalizeKeyframes(source.Keyframes);
        List<DirectorPathElementTrack> elementTracks =
            DirectorPathElementTimeline.NormalizeTracks(source.ElementTracks);
        List<DirectorPathElementKeyframe> elementKeyframes =
            DirectorPathElementTimeline.NormalizeKeyframes(elementTracks, source.ElementKeyframes);
        if (migrateLegacyActorYaw
            && sourceActorKey.Length > 0
            && actorYawConvention == ActorPathYawConvention.ModelFacing)
        {
            foreach (CameraKeyframe keyframe in keyframes)
            {
                keyframe.Yaw = NormalizeAngle(keyframe.Yaw + 180f);
            }
            actorYawConvention = ActorPathYawConvention.CameraFacing;
        }

        return new CameraPath
        {
            Id = id,
            Name = ReserveUniqueName(source.Name, usedNames, DefaultPathName),
            Tags = NormalizeTags(source.Tags),
            Description = NormalizeDescription(source.Description),
            Interpolation = Enum.IsDefined(source.Interpolation)
                ? source.Interpolation
                : CameraPathInterpolation.EaseInOut,
            LoopMode = Enum.IsDefined(source.LoopMode) ? source.LoopMode : CameraPathLoopMode.Once,
            PreviewVisible = source.PreviewVisible,
            PlaybackSpeed = CameraPathTiming.NormalizePlaybackSpeed(source.PlaybackSpeed),
            SourceReplayId = source.SourceReplayId,
            SourceActorKey = sourceActorKey,
            ActorYawConvention = actorYawConvention,
            ActorEyeHeightOffset = NormalizeActorEyeHeightOffset(source.ActorEyeHeightOffset),
            ReplayTimeOffsetSeconds = NormalizeReplayTimeOffset(source.ReplayTimeOffsetSeconds),
            IncludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
                source.IncludedPreloadColumns),
            ExcludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
                source.ExcludedPreloadColumns),
            Keyframes = keyframes,
            ElementTracks = elementTracks,
            ElementKeyframes = elementKeyframes,
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc
        };
    }

    private static CameraKeyframe NormalizeKeyframe(CameraKeyframe source)
    {
        return new CameraKeyframe
        {
            Name = NormalizeText(source.Name),
            TimeSeconds = double.IsFinite(source.TimeSeconds) ? Math.Max(0d, source.TimeSeconds) : 0d,
            DurationSeconds = double.IsFinite(source.DurationSeconds)
                ? Math.Max(0d, source.DurationSeconds)
                : 0d,
            HoldSeconds = double.IsFinite(source.HoldSeconds)
                ? Math.Max(0d, source.HoldSeconds)
                : 0d,
            InterpolationToNext = source.InterpolationToNext is CameraPathInterpolation interpolation
                && Enum.IsDefined(interpolation)
                    ? interpolation
                    : null,
            SegmentStartSpeed = CameraPathTiming.NormalizeSegmentSpeed(source.SegmentStartSpeed),
            SegmentEndSpeed = CameraPathTiming.NormalizeSegmentSpeed(source.SegmentEndSpeed),
            Marker = NormalizeText(source.Marker),
            BezierHandlesEnabled = source.BezierHandlesEnabled,
            BezierInX = NormalizeCoordinate(source.BezierInX),
            BezierInY = NormalizeCoordinate(source.BezierInY),
            BezierInZ = NormalizeCoordinate(source.BezierInZ),
            BezierOutX = NormalizeCoordinate(source.BezierOutX),
            BezierOutY = NormalizeCoordinate(source.BezierOutY),
            BezierOutZ = NormalizeCoordinate(source.BezierOutZ),
            X = NormalizeCoordinate(source.X),
            Y = NormalizeCoordinate(source.Y),
            Z = NormalizeCoordinate(source.Z),
            Dimension = source.Dimension,
            Yaw = NormalizeAngle(source.Yaw),
            Pitch = NormalizeAngle(source.Pitch),
            Roll = NormalizeAngle(source.Roll),
            Fov = NormalizeFov(source.Fov)
        };
    }

    private static ActorPathYawConvention NormalizeActorYawConvention(ActorPathYawConvention convention)
        => Enum.IsDefined(convention) ? convention : ActorPathYawConvention.ModelFacing;

    private static double? NormalizeActorEyeHeightOffset(double? offset)
        => offset is double value && double.IsFinite(value)
            ? Math.Clamp(value, -64d, 64d)
            : null;

    private static CameraSequence NormalizeSequence(
        CameraSequence source,
        HashSet<Guid> usedIds,
        HashSet<string> usedNames,
        HashSet<Guid> cameraIds,
        DateTimeOffset now)
    {
        Guid id = source.Id;
        if (id == Guid.Empty || !usedIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            }
            while (!usedIds.Add(id));
        }

        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        if (modifiedUtc < createdUtc)
        {
            modifiedUtc = createdUtc;
        }

        return new CameraSequence
        {
            Id = id,
            Name = ReserveUniqueName(source.Name, usedNames, DefaultSequenceName),
            LoopMode = Enum.IsDefined(source.LoopMode)
                ? source.LoopMode
                : CameraSequenceLoopMode.Once,
            Shots = NormalizeSequenceShots(source.Shots, cameraIds),
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc
        };
    }

    private static List<CameraSequenceShot> NormalizeSequenceShots(
        IEnumerable<CameraSequenceShot>? sourceShots,
        HashSet<Guid> cameraIds)
    {
        List<CameraSequenceShot> shots = new();
        if (sourceShots is null)
        {
            return shots;
        }

        foreach (CameraSequenceShot? sourceShot in sourceShots)
        {
            if (sourceShot is null || !cameraIds.Contains(sourceShot.CameraId))
            {
                continue;
            }

            shots.Add(new CameraSequenceShot
            {
                CameraId = sourceShot.CameraId,
                DurationSeconds = CameraSequenceSampler.NormalizeShotDuration(sourceShot.DurationSeconds)
            });
        }

        return shots;
    }

    private static List<CameraKeyframe> NormalizeKeyframes(IEnumerable<CameraKeyframe>? sourceFrames)
    {
        List<CameraKeyframe> keyframes = new();
        if (sourceFrames is null)
        {
            return keyframes;
        }

        foreach (CameraKeyframe? sourceFrame in sourceFrames)
        {
            if (sourceFrame is null)
            {
                continue;
            }

            CameraKeyframe frame = NormalizeKeyframe(sourceFrame);
            int existingIndex = keyframes.FindIndex(
                candidate => Math.Abs(candidate.TimeSeconds - frame.TimeSeconds) <= KeyframeTimeEpsilon);
            if (existingIndex >= 0)
            {
                keyframes[existingIndex] = frame;
            }
            else
            {
                keyframes.Add(frame);
            }
        }

        keyframes.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        CameraPathTiming.NormalizeTimeline(keyframes);
        return keyframes;
    }

    private static string ReserveUniqueName(
        string? requestedName,
        HashSet<string> usedNames,
        string defaultName = DefaultCameraName)
    {
        string baseName = string.IsNullOrWhiteSpace(requestedName)
            ? defaultName
            : requestedName.Trim();

        if (usedNames.Add(baseName))
        {
            return baseName;
        }

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} ({suffix})";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        List<string> normalized = new();
        HashSet<string> seen = new(NameComparer);
        if (tags is null)
        {
            return normalized;
        }

        foreach (string? tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            string trimmed = tag.Trim();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }

    private static string NormalizeDescription(string? description)
        => description?.Trim() ?? string.Empty;

    private static string NormalizeText(string? value)
        => value?.Trim() ?? string.Empty;

    private static double NormalizeReplayTimeOffset(double value)
        => double.IsFinite(value)
            ? Math.Clamp(value, 0d, CameraPathTiming.MaxTimelineSeconds)
            : 0d;

    private static int? NormalizeQuickSlot(int? slot)
        => slot is int value && QuickSlots.IsValid(value) ? value : null;

    private static double NormalizeCoordinate(double value)
        => double.IsFinite(value) ? value : 0d;

    private static float NormalizeAngle(float value)
        => float.IsFinite(value) ? value : 0f;

    private static float NormalizeFov(float value)
        => float.IsFinite(value) && value > 0f ? Math.Clamp(value, 10f, 170f) : DefaultFov;

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value, DateTimeOffset fallback)
        => value == default ? fallback : value.ToUniversalTime();

    private static DateTimeOffset NextModifiedUtc(DateTimeOffset createdUtc, DateTimeOffset previousModifiedUtc)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset floor = previousModifiedUtc > createdUtc ? previousModifiedUtc : createdUtc;
        if (now > floor)
        {
            return now;
        }

        return floor < DateTimeOffset.MaxValue ? floor.AddTicks(1) : floor;
    }
}
