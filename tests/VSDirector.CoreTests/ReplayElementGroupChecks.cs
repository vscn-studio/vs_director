using System.Text.Json;
using VSDirector.Data;

internal static class ReplayElementGroupChecks
{
    internal static void Run()
    {
        var timeline = new DirectorReplayViewportTimeline();
        timeline.Visuals.Environment.Season = .7f;
        var group = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.PresetGroup, Name = "Noir" };
        timeline.Tracks.Add(group);
        DirectorReplayElementTrack Child(DirectorReplayElementTrackType type)
        {
            var track = new DirectorReplayElementTrack { TrackType = type, ParentGroupId = group.Id };
            timeline.Tracks.Add(track);
            return track;
        }
        var weather = Child(DirectorReplayElementTrackType.Weather);
        var sky = Child(DirectorReplayElementTrackType.Sky);
        var guide = Child(DirectorReplayElementTrackType.Composition);
        var marker = Child(DirectorReplayElementTrackType.Marker);
        var camera = Child(DirectorReplayElementTrackType.Camera);
        var fov = Child(DirectorReplayElementTrackType.Fov);
        var speed = Child(DirectorReplayElementTrackType.Speed);
        var visuals = timeline.Visuals.DeepClone();
        visuals.Environment.Weather = DirectorReplayWeatherMode.Rain;
        visuals.Sky.Mode = DirectorReplaySkyMode.GreenScreen;
        visuals.Composition.CenterGuide = true;
        foreach (var track in new[] { weather, sky, guide, marker, fov, speed })
        {
            timeline.ElementKeyframes.Add(new() { TrackId = track.Id, TrackType = track.TrackType,
                TimeSeconds = 2, VisualSettings = visuals.DeepClone(), Text = "Highlight", Fov = 55, TickRate = 15,
                InterpolationToNext = DirectorReplayElementInterpolation.Hold });
            timeline.ElementKeyframes.Add(new() { TrackId = track.Id, TrackType = track.TrackType,
                TimeSeconds = 8, EndsElement = true });
        }
        timeline.Clips.Add(new() { TrackId = camera.Id, SourceType = DirectorReplayViewportSourceType.StaticCamera,
            TimelineStartSeconds = 2, DurationSeconds = 6, CameraPose = new CameraPreset { X = 30 } });
        timeline = DirectorReplayViewportTimeline.Normalize(timeline, 20);
        var document = new DirectorReplayDocument { DurationSeconds = 20, ViewportTimeline = timeline };
        var edits = DirectorReplayTimelineEditIO.Create(document);
        var loaded = JsonSerializer.Deserialize<DirectorReplayTimelineEditDocument>(JsonSerializer.Serialize(edits))!;
        DirectorReplayTimelineEditIO.Apply(document, loaded);
        timeline = document.ViewportTimeline;
        group = timeline.Tracks.Single(t => t.Id == group.Id);
        Check.Equal(1, timeline.VisibleTracks(new HashSet<Guid>()).Count());
        Check.Equal(8, timeline.VisibleTracks(new HashSet<Guid> { group.Id }).Count());
        Check.Equal(DirectorReplayWeatherMode.Recorded, DirectorReplayVisualElements.Evaluate(timeline, 1).Environment.Weather);
        var sampled = DirectorReplayVisualElements.Evaluate(timeline, 3);
        Check.Equal(DirectorReplayWeatherMode.Rain, sampled.Environment.Weather);
        Check.Equal(DirectorReplaySkyMode.GreenScreen, sampled.Sky.Mode);
        Check.True(sampled.Composition.CenterGuide);
        Check.Equal(DirectorReplayWeatherMode.Recorded, timeline.Visuals.Environment.Weather);
        Check.Equal(DirectorReplayWeatherMode.Recorded, DirectorReplayVisualElements.Evaluate(timeline, 8).Environment.Weather);
        Check.True(DirectorReplayCameraPriority.ResolveActive(timeline, 3)?.Id == camera.Id);
        Check.NearlyEqual(.5, DirectorReplayElementTimeline.SampleTimingRate(timeline, 3, 1));
        var clone = timeline.DeepClone();
        clone.ElementKeyframes.First(k => k.TrackId == weather.Id).VisualSettings!.Environment.Weather = DirectorReplayWeatherMode.Snow;
        Check.Equal(DirectorReplayWeatherMode.Rain, DirectorReplayVisualElements.Evaluate(timeline, 3).Environment.Weather);
        group.Enabled = false;
        Check.Equal(DirectorReplayWeatherMode.Recorded, DirectorReplayVisualElements.Evaluate(timeline, 3).Environment.Weather);
        Check.Equal(DirectorReplaySkyMode.Recorded, DirectorReplayVisualElements.Evaluate(timeline, 3).Sky.Mode);
        Check.False(DirectorReplayVisualElements.Evaluate(timeline, 3).Composition.CenterGuide);
        Check.True(DirectorReplayCameraPriority.ResolveActive(timeline, 3) is null);
        Check.False(DirectorReplayElementTimeline.TrySample(timeline, fov.TrackType, 3, out _));
        Check.NearlyEqual(1, DirectorReplayElementTimeline.SampleTimingRate(timeline, 3, 1));
        Check.True(timeline.Tracks.Single(t => t.Id == weather.Id).Enabled);
        group.Enabled = true;
        Check.True(DirectorReplayElementTimeline.TrySample(timeline, fov.TrackType, 3, out var lens));
        Check.NearlyEqual(55, lens.Fov);
        var later = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.PresetGroup };
        var laterLens = new DirectorReplayElementTrack { TrackType = fov.TrackType, ParentGroupId = later.Id };
        timeline.Tracks.Add(later); timeline.Tracks.Add(laterLens);
        timeline.ElementKeyframes.Add(new() { TrackId = laterLens.Id, TrackType = laterLens.TrackType, TimeSeconds = 3, Fov = 90 });
        Check.True(DirectorReplayElementTimeline.TrySample(timeline, fov.TrackType, 4, out lens));
        Check.NearlyEqual(90, lens.Fov);
        later.Enabled = false;
        Check.True(DirectorReplayElementTimeline.TrySample(timeline, fov.TrackType, 4, out lens));
        Check.NearlyEqual(55, lens.Fov);
        Check.True(DirectorReplayViewportTimeline.MoveTrack(timeline, group.Id, timeline.Tracks.IndexOf(later)));
        Check.Equal(later.Id, timeline.Tracks[0].Id);
        Check.False(DirectorReplayViewportTimeline.MoveTrack(timeline, weather.Id, 0));
        Check.True(DirectorReplayViewportTimeline.RemoveTrack(timeline, group.Id));
        timeline = DirectorReplayViewportTimeline.Normalize(timeline, 20);
        Check.Equal(2, timeline.Tracks.Count);
        Check.Equal(0, timeline.Clips.Count);
        Check.Equal(1, timeline.ElementKeyframes.Count);
        Check.False(timeline.Tracks.Any(t => t.ParentGroupId == group.Id));
        Check.Equal(DirectorReplayWeatherMode.Recorded, DirectorReplayVisualElements.Evaluate(timeline, 3).Environment.Weather);
    }
}
