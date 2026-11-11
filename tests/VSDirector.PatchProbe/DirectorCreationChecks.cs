using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Config;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCreationWorkspace(Assembly assembly, ICoreClientAPI api, Action<string> capture)
    {
        CheckActorTextures(assembly);
        CheckNativeActors();
        CheckCreationRigMatrices();
        CheckRigEditingWorkflow(assembly, api);
        CheckRigViewportMapping(assembly, api);
        CheckCreationClipboard(assembly);
        CheckCreationActorVisibility(assembly);
        CheckPerformanceInput(assembly);
        CheckCreationExport(assembly);
        CheckCreationCalendar(assembly);
        CheckPerformanceEditing(assembly, api);
        string directory = Path.Combine(Path.GetTempPath(), "vsdirector-creation-probe-" + Guid.NewGuid().ToString("N"));
        var session = new DirectorCreationSession(new(directory));
        var part = new CreationPart { Name = "头部", To = new(16, 16, 16) };
        var model = new CreationModel { Name = "测试演员", Parts = [part] };
        var actor = new CreationSceneObject { Name = "演员一", ModelId = model.Id };
        var camera = new CreationSceneObject { Name = "拍摄相机", Kind = CreationObjectKind.Camera };
        var project = new DirectorCreationProject { Name = "双演员动画", Models = [model], Objects = [actor, camera], CameraId = camera.Id };
        DirectorCreationEdits.SetKey(project, actor.Id, part.Id, CreationChannel.Rotation, 0, default);
        DirectorCreationEdits.SetKey(project, actor.Id, part.Id, CreationChannel.Rotation, 2, new(0, 60, 0));
        session.Open(project); session.SelectedObject = actor.Id;
        Type R(string name) => assembly.GetType(name, true)!;
        var runtime = RuntimeHelpers.GetUninitializedObject(R("VSDirector.Runtime.DirectorCreationRuntime"));
        var objectRenderer = RuntimeHelpers.GetUninitializedObject(R("VSDirector.Rendering.DirectorCreationRenderer"));
        AccessTools.Field(runtime.GetType(), "<Session>k__BackingField").SetValue(runtime, session);
        AccessTools.Field(runtime.GetType(), "<Renderer>k__BackingField").SetValue(runtime, objectRenderer);
        AccessTools.Field(runtime.GetType(), "<Status>k__BackingField").SetValue(runtime, "");
        AccessTools.Field(objectRenderer.GetType(), "<Error>k__BackingField").SetValue(objectRenderer, "");
        var owner = new VSDirectorModSystem(); AccessTools.Field(owner.GetType(), "creation").SetValue(owner, runtime);
        AccessTools.Field(runtime.GetType(), "owner").SetValue(runtime, owner);
        var host = Activator.CreateInstance(R("VSDirector.Gui.DirectorImGuiWorkspace"), api)!;
        var workspace = Activator.CreateInstance(R("VSDirector.Gui.DirectorCreationWorkspace"), BindingFlags.Instance | BindingFlags.NonPublic, null, [api, owner, host], null)!;
        var io = ImGui.GetIO(); Vector2 display = io.DisplaySize; io.DisplaySize = new(960, 640);
        string locale = Lang.CurrentLocale;
        try
        {
            CheckCreationWorkflow(workspace,session);
            foreach (string language in new[] { "zh-cn", "en" })
            {
            LoadCreationProbeLanguage(language);
            foreach (var displayMode in Enum.GetValues<CreationTimeDisplay>())
            {
            project.TimeDisplay = displayMode;
            foreach (float width in new[] { 220f, 320f, 520f })
            {
                for (int frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame();
                    void Panel(string name, Vector2 pos, Vector2 size, string method)
                    {
                        ImGui.SetNextWindowPos(pos); ImGui.SetNextWindowSize(size);
                        ImGui.Begin(name, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
                        if (method == "DrawProperties")
                            foreach (string key in new[] { "creation-recording-channels", "creation-native-actions", "creation-ik" })
                            {
                                ImGui.PushID(Lang.Get("vsdirector:" + key));
                                ImGui.GetStateStorage().SetBool(ImGui.GetID("###header"), true); ImGui.PopID();
                            }
                        if (method == "DrawView")
                            foreach (string key in new[] { "world", "sky", "environment", "particles" })
                            {
                                ImGui.PushID(Lang.Get("vsdirector:replay-visual-group-" + key));
                                ImGui.GetStateStorage().SetBool(ImGui.GetID("###header"), true); ImGui.PopID();
                            }
                        AccessTools.Method(workspace.GetType(), method).Invoke(workspace, method == "DrawProjectSettings" ? [true] : null);
                        // ImGui derives scrollbar widths from the previous frame's content size.
                        if (frame == 2 && ImGui.GetScrollMaxX() > 1) throw new Exception("Creation panel content overflowed horizontally: " + name + " width=" + width + " overflow=" + ImGui.GetScrollMaxX());
                        ImGui.End();
                    }
                    Panel("creation-browser-probe", new(0, 0), new(width, 380), "DrawBrowser");
                    Panel("creation-properties-probe", new(width, 0), new(width, 380), "DrawProperties");
                    Panel("creation-view-probe", new(width, 0), new(width, 380), "DrawView");
                    Panel("creation-bones-probe", new(width, 0), new(width, 380), "DrawBonesPanel");
                    Panel("creation-ik-probe", new(width, 0), new(width, 380), "DrawIkPanel");
                    Panel("creation-actions-probe", new(width, 0), new(width, 380), "DrawActionsPanel");
                    Panel("creation-output-probe", new(width, 0), new(width, 380), "DrawProjectSettings");
                    Panel("creation-timeline-probe", new(0, 380), new(960, 260), "DrawTimeline");
                    ImGui.Render();
                }
                if (ImGui.GetDrawData().TotalVtxCount < 100) throw new Exception("Creation panels produced no real ImGui geometry.");
                capture("creation-" + language + "-" + displayMode + "-" + width);
            }
            Console.WriteLine("PASS creation browser, transform inspector and timeline in real ImGui at 220/320/520px");
            var particle = new CreationSceneObject { Kind = CreationObjectKind.Particle };
            var clip = new CreationClip { Name = "测试剪辑", Duration = 2 };
            project.Objects.Add(particle); project.Clips.Add(clip); session.Changed();
            foreach (float width in new[] { 220f, 320f, 520f })
            foreach (string mode in new[] { "model", "particle", "clip", "key" })
            {
                session.SelectedObject = mode == "particle" ? particle.Id : actor.Id;
                session.SelectedPart = mode == "model" ? part.Id : Guid.Empty;
                AccessTools.Field(workspace.GetType(), "clipId").SetValue(workspace, mode == "clip" ? clip.Id : Guid.Empty);
                AccessTools.Field(workspace.GetType(), "trackId").SetValue(workspace, project.Tracks[0].Id);
                AccessTools.Field(workspace.GetType(), "keyId").SetValue(workspace, mode == "key" ? project.Tracks[0].Keys[0].Id : Guid.Empty);
                for (int frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(width, 600));
                    ImGui.Begin("creation-detail-probe", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
                    string method = mode == "model" ? "DrawModelProperties" : mode == "particle" ? "DrawObjectProperties" : "DrawTimeline";
                    AccessTools.Method(workspace.GetType(), method).Invoke(workspace, null);
                    if (frame == 2 && ImGui.GetScrollMaxX() > 1) throw new Exception($"Creation {mode} overflowed at width {width}: {ImGui.GetScrollMaxX()}");
                    ImGui.End(); ImGui.Render();
                }
            }
            Console.WriteLine("PASS " + language + " creation model/particle inspectors and selected clip/key timeline at 220/320/520px");
            }
            }
            project.TimeDisplay = CreationTimeDisplay.Frames;
            LoadCreationProbeLanguage("zh-cn");
            session.SelectedObject = actor.Id; session.SelectedPart = part.Id;
            AccessTools.Field(workspace.GetType(), "keyId").SetValue(workspace, Guid.Empty);
            AccessTools.Field(workspace.GetType(), "clipId").SetValue(workspace, Guid.Empty);
            Vector2 timelineOrigin = default;
            List<(Vector2 Position, uint Color)> timelineHeader = [], timelineRows = [];
            void TimelineFrame()
            {
                ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(960, 500));
                ImGui.Begin("creation-interaction", ImGuiWindowFlags.NoSavedSettings);
                timelineOrigin = ImGui.GetCursorScreenPos() + new Vector2(0,ImGui.GetFrameHeightWithSpacing());
                AccessTools.Method(workspace.GetType(), "DrawTimeline").Invoke(workspace, null);
                var headerVertices = ImGui.GetWindowDrawList().VtxBuffer;
                timelineHeader = Enumerable.Range(0, headerVertices.Size).Select(i => (headerVertices[i].pos, headerVertices[i].col)).ToList();
                ImGui.BeginChild("timeline-rows");
                var rowVertices = ImGui.GetWindowDrawList().VtxBuffer;
                timelineRows = Enumerable.Range(0, rowVertices.Size).Select(i => (rowVertices[i].pos, rowVertices[i].col)).ToList();
                ImGui.EndChild();
                ImGui.End(); ImGui.Render();
            }
            TimelineFrame(); TimelineFrame();
            foreach (int fps in new[] { 24, 30, 60, 120 })
            {
                project.Output.Fps = fps;
                io.KeyAlt = false;
                double snapped = (double)AccessTools.Method(workspace.GetType(), "Snap").Invoke(workspace, [.049])!;
                if (Math.Abs(snapped - 1d / 30) > 1e-8) throw new Exception("Creation pointer snap followed export FPS");
                io.KeyAlt = true;
                double fine = (double)AccessTools.Method(workspace.GetType(), "Snap").Invoke(workspace, [.049])!;
                if (Math.Abs(fine - .049) > 1e-8) throw new Exception("Creation Alt bypass lost subframe editing");
                io.KeyAlt = false;
                session.Seek(1);
                float scale = (float)AccessTools.Property(R("VSDirector.Gui.DirectorImGuiTheme"), "Scale").GetValue(null)!;
                io.MousePos = timelineOrigin + new Vector2(104 * scale, 14 * scale);
                TimelineFrame(); TimelineFrame(); io.MouseDown[0] = true; TimelineFrame(); io.MouseDown[0] = false; TimelineFrame();
                if (Math.Abs(session.Time - 31d / 30) > 1e-8) throw new Exception("Creation transport next frame followed export FPS or missed its button");
                io.MousePos = timelineOrigin + new Vector2(44 * scale, 14 * scale);
                TimelineFrame(); TimelineFrame(); io.MouseDown[0] = true; TimelineFrame(); io.MouseDown[0] = false; TimelineFrame();
                if (Math.Abs(session.Time - 1) > 1e-8) throw new Exception("Creation transport previous frame followed export FPS or missed its button");
            }
            project.Output.Fps = 30;
            Console.WriteLine("PASS real creation transport and pointer snap at 24/30/60/120 export FPS, with Alt subframe bypass");
            foreach (float x in new[] { 80f, 600f })
            {
                io.MousePos = timelineOrigin + new Vector2(x, 100); TimelineFrame(); TimelineFrame();
                double before = (double)AccessTools.Field(workspace.GetType(), "timelineZoom").GetValue(workspace)!;
                io.AddKeyEvent(ImGuiKey.LeftCtrl, true); io.MouseWheel = -1; TimelineFrame();
                double after = (double)AccessTools.Field(workspace.GetType(), "timelineZoom").GetValue(workspace)!;
                if (after >= before || after >= 1) throw new Exception("Creation Ctrl+wheel failed to zoom out below fit from labels/content.");
                io.MouseWheel = 1; TimelineFrame();
                double restored = (double)AccessTools.Field(workspace.GetType(), "timelineZoom").GetValue(workspace)!;
                if (Math.Abs(restored - before) > .00001) throw new Exception("Creation zoom direction or wheel dispatch is incorrect.");
                io.AddKeyEvent(ImGuiKey.LeftCtrl, false); TimelineFrame();
            }
            AccessTools.Field(workspace.GetType(), "trackId").SetValue(workspace, project.Tracks[0].Id);
            AccessTools.Field(workspace.GetType(), "keyId").SetValue(workspace, project.Tracks[0].Keys[0].Id);
            AccessTools.Field(workspace.GetType(), "timelineEditor").SetValue(workspace, true);
            TimelineFrame(); TimelineFrame(); TimelineFrame(); capture("creation-key-editor");
            AccessTools.Field(workspace.GetType(), "timelineEditor").SetValue(workspace, false);
            TimelineFrame(); TimelineFrame(); capture("creation-timeline-controls");
            Console.WriteLine("PASS real Ctrl+wheel zoom below fit from labels and content, reversible zoom and floating key editor");

            AccessTools.Field(workspace.GetType(), "keyId").SetValue(workspace, Guid.Empty);
            TimelineFrame();
            var glyphs = timelineRows.Where(v => v.Color == 0xffe6e0de).ToArray();
            float rightmost = glyphs.Max(v => v.Position.X);
            var lastKey = glyphs.Where(v => v.Position.X > rightmost - 26).ToArray();
            Vector2 keyCenter = (new Vector2(lastKey.Min(v => v.Position.X), lastKey.Min(v => v.Position.Y))
                + new Vector2(lastKey.Max(v => v.Position.X), lastKey.Max(v => v.Position.Y))) / 2;
            io.MousePos = keyCenter; TimelineFrame(); TimelineFrame();
            io.MouseDown[0] = true; TimelineFrame();
            io.MousePos += new Vector2(65, 0); TimelineFrame();
            double dragOriginal = (double)AccessTools.Field(workspace.GetType(), "timelineDragOrigin").GetValue(workspace)!;
            double dragTarget = (double)AccessTools.Field(workspace.GetType(), "timelineDragTarget").GetValue(workspace)!;
            if (Math.Abs(dragOriginal - 2) > 1e-6 || dragTarget <= dragOriginal
                || !timelineRows.Any(v => v.Color == 0xff181818 && v.Position.Y > 450)
                || timelineHeader.Any(v => v.Color == 0xff181818)
                || !timelineRows.Any(v => v.Color == 0xff55d9ff) || !timelineRows.Any(v => v.Color == 0x88000000))
                throw new Exception("Creation key pointer drag did not show inline time and original/destination guides");
            capture("creation-key-drag-feedback");
            project.TimeDisplay = CreationTimeDisplay.Time; TimelineFrame();
            string label = (string)AccessTools.Method(workspace.GetType(), "FormatTime").Invoke(workspace, [dragTarget])!;
            if (!label.EndsWith(" " + Lang.Get("vsdirector:creation-unit-seconds"))) throw new Exception("Creation drag feedback ignored seconds display mode");
            io.MouseDown[0] = false; TimelineFrame();
            if (timelineRows.Any(v => v.Color is 0xff55d9ff or 0x88000000)) throw new Exception("Creation key drag left stale guide lines");
            session.Undo(); project = session.Project!;
            if (project.Tracks[0].Keys.All(k => Math.Abs(k.Time - 2) > 1e-6)) throw new Exception("Creation key drag feedback broke undo");
            project.TimeDisplay = CreationTimeDisplay.Frames;
            io.MousePos = timelineOrigin + new Vector2(530, 12); TimelineFrame(); TimelineFrame();
            io.MouseDown[0] = true; TimelineFrame();
            double initialScrub = session.Time;
            io.MousePos += new Vector2(65, 0); TimelineFrame();
            if (session.Time <= initialScrub || !timelineRows.Any(v => v.Color == 0xff181818 && v.Position.Y > 450))
                throw new Exception("Creation ruler scrubbing lost inline time label or seeking");
            io.MouseDown[0] = false; TimelineFrame();
            Console.WriteLine("PASS real creation key drag original/destination guides, frame/seconds label, undo, ruler scrubbing and release cleanup");
            AccessTools.Field(workspace.GetType(), "timelineCurves").SetValue(workspace,true);
            AccessTools.Field(workspace.GetType(), "curveAxis").SetValue(workspace,1);
            AccessTools.Field(workspace.GetType(), "keyId").SetValue(workspace,Guid.Empty);
            ((HashSet<Guid>)AccessTools.Field(workspace.GetType(), "selectedTimelineKeys").GetValue(workspace)!).Clear();
            TimelineFrame();TimelineFrame();
            var curveGlyphs=timelineRows.Where(v=>v.Color==0xffe6e0de).ToArray();
            float curveRight=curveGlyphs.Max(v=>v.Position.X);
            var curveKey=curveGlyphs.Where(v=>v.Position.X>curveRight-26).ToArray();
            io.MousePos=(new Vector2(curveKey.Min(v=>v.Position.X),curveKey.Min(v=>v.Position.Y))+new Vector2(curveKey.Max(v=>v.Position.X),curveKey.Max(v=>v.Position.Y)))/2;
            TimelineFrame();TimelineFrame();io.MouseDown[0]=true;TimelineFrame();io.MousePos+=new Vector2(24,24);TimelineFrame();
            if(project.Tracks[0].Keys.Last().Time<=2||project.Tracks[0].Keys.Last().Value.Y>=60)
                throw new Exception("Real curve key drag failed to change both time and value");
            capture("creation-curve-drag");
            io.MouseDown[0]=false;TimelineFrame();session.Undo();project=session.Project!;
            if(project.Tracks[0].Keys.Last().Time!=2||project.Tracks[0].Keys.Last().Value.Y!=60)
                throw new Exception("Curve time/value edit did not undo atomically");
            AccessTools.Field(workspace.GetType(), "timelineCurves").SetValue(workspace,false);
            Console.WriteLine("PASS real curve XY pointer drag edits time/value together and restores both with one undo");
            project.Tracks.Clear(); project.Objects.Clear(); project.Objects.Add(actor); project.Clips.Clear();
            var draggable = new CreationClip { ObjectId = actor.Id, Name = "剪辑1", Start = 1, Duration = 2 };
            project.Clips.Add(draggable); session.SelectedPart = Guid.Empty; session.ClearTransformDrafts(); session.Changed();
            Vector2 ClipCenter()
            {
                ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(960, 500));
                ImGui.Begin("creation-interaction", ImGuiWindowFlags.NoSavedSettings);
                AccessTools.Method(workspace.GetType(), "DrawTimeline").Invoke(workspace, null);
                var vertices = ImGui.GetWindowDrawList().VtxBuffer;
                // Lane geometry belongs to the child draw list.
                ImGui.BeginChild("timeline-rows"); vertices = ImGui.GetWindowDrawList().VtxBuffer;
                Vector2 lo = new(float.PositiveInfinity), hi = new(float.NegativeInfinity);
                for (int i = 0; i < vertices.Size; i++)
                    if (vertices[i].col == 0xff91644a) { lo = Vector2.Min(lo, vertices[i].pos); hi = Vector2.Max(hi, vertices[i].pos); }
                ImGui.EndChild(); ImGui.End(); ImGui.Render();
                if (!float.IsFinite(lo.X)) throw new Exception("Clip did not draw its hit target");
                return (lo + hi) / 2;
            }
            TimelineFrame(); TimelineFrame();
            var center = ClipCenter(); io.MousePos = center; TimelineFrame(); TimelineFrame();
            io.MouseDown[0] = true; TimelineFrame();
            if ((Guid)AccessTools.Field(workspace.GetType(), "clipId").GetValue(workspace)! != draggable.Id) throw new Exception("First click did not select clip");
            io.MousePos += new Vector2(70, 0); TimelineFrame();
            io.MouseDown[0] = false; TimelineFrame();
            if (draggable.Start <= 1) throw new Exception("Clip drag did not move from its first click");
            double moved = draggable.Start; session.Undo();
            if (session.Project!.Clips[0].Start != 1) throw new Exception("Clip pointer drag undo failed");
            session.Redo(); draggable = session.Project.Clips[0];
            if (draggable.Start != moved) throw new Exception("Clip pointer drag redo failed");
            TimelineFrame(); center = ClipCenter(); io.MousePos = center; TimelineFrame();
            io.MouseDown[1] = true; TimelineFrame(); io.MouseDown[1] = false; TimelineFrame();
            if (!(bool)AccessTools.Field(workspace.GetType(), "timelineEditor").GetValue(workspace)!) throw new Exception("Clip right click did not open editor");
            AccessTools.Field(workspace.GetType(), "timelineEditor").SetValue(workspace, false);
            TimelineFrame();
            Console.WriteLine("PASS real clip first-click drag, undo/redo and right-click editor");
        }
        finally { Lang.ChangeLanguage(locale); io.DisplaySize = display; ((IDisposable)host).Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        CheckCreationProjectLifecycle(assembly, api, capture);
        CheckCreationShaderAndState(assembly);
        CheckCreationSelectionContour(assembly);
    }
    private static void CheckPerformanceInput(Assembly assembly)
    {
        var type = assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime", true)!;
        var current = AccessTools.Field(type, "<Current>k__BackingField"); var previous = current.GetValue(null);
        var runtime = RuntimeHelpers.GetUninitializedObject(type);
        var io = ImGui.GetIO(); bool mouse = io.WantCaptureMouse, keyboard = io.WantCaptureKeyboard;
        try
        {
            current.SetValue(null, runtime); io.WantCaptureMouse = io.WantCaptureKeyboard = true;
            var captureType = AccessTools.TypeByName("VSImGui.Controller");
            foreach (string method in new[] { "KeyboardCaptured", "MouseCaptured", "MouseMovesCaptured" })
            {
                var capture = AccessTools.Method(captureType, method);
                AccessTools.Field(type, "performanceRunning").SetValue(runtime, false);
                if (!(bool)capture.Invoke(null, null)!) throw new Exception("Normal workspace input capture changed");
                AccessTools.Field(type, "performanceRunning").SetValue(runtime, true);
                if ((bool)capture.Invoke(null, null)!) throw new Exception("Recording still captures native player input: " + method);
            }
            AccessTools.Field(type, "observingWorldActor").SetValue(runtime, true);
            if (!(bool)AccessTools.Method(captureType, "MouseCaptured").Invoke(null, null)!) throw new Exception("Observing another actor released editor input");
            Console.WriteLine("PASS patched ImGui native mouse/keyboard passthrough only during controlled player recording");
        }
        finally { current.SetValue(null, previous); io.WantCaptureMouse = mouse; io.WantCaptureKeyboard = keyboard; }
    }

    private static void CheckNativeActors()
    {
        string assets = Path.Combine(Path.GetDirectoryName(typeof(Vintagestory.Client.NoObf.ClientMain).Assembly.Location)!, "assets");
        if (!File.Exists(Path.Combine(assets, "game/shapes/entity/humanoid/seraph.json"))) assets = Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? @"E:\vintagestory\Vintagestory", "assets");
        foreach (string relative in new[] { "game/shapes/entity/humanoid/seraph.json", "survival/shapes/entity/animal/mammal/wolf/eurasian-adult.json" })
        {
            var imported = DirectorCreationShape.Import(File.ReadAllText(Path.Combine(assets, relative)), relative);
            var actor = new CreationSceneObject { ModelId = imported.Model.Id };
            var project = new DirectorCreationProject { Models = [imported.Model], Objects = [actor], Animations = imported.Animations };
            project.Validate();
            if (!DirectorCreationIk.SuggestChains(imported.Model).Any()) throw new Exception("Native limb discovery failed: " + relative);
            var rest = DirectorCreationEvaluator.Evaluate(project, 0).Objects[actor.Id];
            foreach (var pair in DirectorCreationIk.SuggestChains(imported.Model))
            {
                var controls = DirectorCreationIk.CreateControls(actor, imported.Model, rest, pair.Upper, pair.Lower);
                project.Objects.Add(controls.Target); project.Objects.Add(controls.Pole); actor.IkChains.Add(controls.Chain);
            }
            var rigged = DirectorCreationEvaluator.Evaluate(project, 0).Objects[actor.Id];
            foreach (var chain in actor.IkChains)
            {
                var end = Vector3.Transform(chain.EndOffset.Float(), rigged.Bones[chain.Lower] * rigged.World);
                var target = project.Objects.First(o => o.Id == chain.Target).Transform.Position.Float();
                if (Vector3.Distance(end, target) > .005f) throw new Exception("Creating native IK moved its end effector: " + relative + " / " + chain.Name);
            }
            actor.IkChains.Clear();
            foreach (var pair in DirectorCreationIk.SuggestChains(imported.Model))
            {
                var endPart = pair.Lower;
                var centre = Vector3.Transform(((endPart.To-endPart.From)*(1d/32)).Float(),rest.Bones[endPart.Id]);
                var result = DirectorCreationPoseGeometry.Pull(imported.Model,rest,endPart.Id,centre+new Vector3(.025f,.015f,.02f));
                if (result.Values.Any(t => !t.Rotation.Finite() || !t.Position.Finite())) throw new Exception("Native actor direct pose pull produced invalid values: " + relative);
            }
            foreach (var animation in imported.Animations)
            {
                var frame = DirectorCreationEvaluator.Evaluate(project, 0, editingAction: animation, actionActor: actor.Id, actionTime: animation.Duration / 2);
                if (frame.Objects[actor.Id].Bones.Values.Any(m => !float.IsFinite(m.M41))) throw new Exception("Native animation produced invalid matrix.");
            }
            Console.WriteLine($"PASS native actor {relative}: {imported.Model.Parts.Count} bones, {imported.Animations.Count} actions");
        }
    }
    private static void CheckActorTextures(Assembly assembly)
    {
        int texture = GL.GenTexture(), previous = GL.GetInteger(GetPName.TextureBinding2D);
        string file = Path.Combine(Path.GetTempPath(), "director-atlas-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            int[] pixels = Enumerable.Range(0, 16).Select(i => unchecked((int)0xff000000) | i * 0x010203).ToArray();
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 4, 4, 0, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
            GL.BindTexture(TextureTarget.Texture2D, previous);
            var position = new TextureAtlasPosition { atlasTextureId = texture, x1 = .25f, y1 = .25f, x2 = .75f, y2 = .75f };
            AccessTools.Method(assembly.GetType("VSDirector.Rendering.DirectorCreationTextureSnapshot", true), "Save").Invoke(null, [position, file]);
            using var stream = File.OpenRead(file);
            using var bitmap = new Vintagestory.API.Common.BitmapExternal(stream, null);
            int[] actual = new int[4]; System.Runtime.InteropServices.Marshal.Copy(bitmap.PixelsPtrAndLock, actual, 0, 4);
            if (bitmap.Width != 2 || bitmap.Height != 2 || !actual.SequenceEqual(new[] { pixels[5], pixels[6], pixels[9], pixels[10] })) throw new Exception("Actor atlas crop changed orientation or channels.");
            if (GL.GetInteger(GetPName.TextureBinding2D) != previous) throw new Exception("Atlas capture leaked texture binding.");
            Console.WriteLine("PASS real GL actor atlas crop and PNG channels/orientation");
        }
        finally { GL.DeleteTexture(texture); GL.BindTexture(TextureTarget.Texture2D, previous); if (File.Exists(file)) File.Delete(file); }
    }

    private static void LoadCreationProbeLanguage(string language)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "assets/vsdirector/lang/" + language + ".json")))!;
        if (!Lang.AvailableLanguages.ContainsKey(language))
        {
            var logger = ProbeDispatchProxy.Create<Vintagestory.API.Common.ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
            Lang.PreLoad(logger, GamePaths.AssetsPath, language);
        }
        Lang.ChangeLanguage(language);
        var translations = Lang.AvailableLanguages[language].GetAllEntries();
        foreach (var entry in entries) translations["vsdirector:" + entry.Key] = entry.Value;
        foreach (var entry in entries.Where(e => e.Key.StartsWith("creation-")))
            if (Lang.Get("vsdirector:" + entry.Key) != entry.Value) throw new Exception("Missing creation translation: " + language + "/" + entry.Key);
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate nint ClipboardRead(nint user);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate void ClipboardWrite(nint user, nint text);
    private static void CheckCreationClipboard(Assembly assembly)
    {
        var io = ImGui.GetIO();
        nint previousRead = io.GetClipboardTextFn, previousWrite = io.SetClipboardTextFn, buffer = 0;
        string clipboard = "", value = "相机 Camera 123"; double number = 0;
        bool numeric = false, focus = true;
        ClipboardRead read = _ =>
        {
            if (buffer != 0) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(buffer);
            return buffer = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(clipboard);
        };
        ClipboardWrite write = (_, text) => clipboard = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text) ?? "";
        io.GetClipboardTextFn = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(read);
        io.SetClipboardTextFn = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(write);
        var queue = AccessTools.Method(assembly.GetType("VSDirector.Gui.DirectorImGuiRuntime", true), "QueueModifierEvents");
        void Frame()
        {
            ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(400, 180));
            ImGui.Begin("creation-clipboard-probe", ImGuiWindowFlags.NoSavedSettings);
            if (focus) { ImGui.SetKeyboardFocusHere(); focus = false; }
            if (numeric) ImGui.InputDouble("number", ref number, 0, 0);
            else ImGui.InputText("text", ref value, 256);
            ImGui.End(); ImGui.Render();
        }
        void Shortcut(ImGuiKey key, ImGuiKey control)
        {
            io.AddKeyEvent(control, true); io.KeyCtrl = true; queue.Invoke(null, null); Frame();
            io.AddKeyEvent(key, true); Frame(); io.AddKeyEvent(key, false); Frame();
            io.AddKeyEvent(control, false); io.KeyCtrl = false; queue.Invoke(null, null); Frame();
        }
        try
        {
            Frame(); Frame();
            foreach (var control in new[] { ImGuiKey.LeftCtrl, ImGuiKey.RightCtrl })
            {
                Shortcut(ImGuiKey.A, control); Shortcut(ImGuiKey.C, control);
                if (clipboard != "相机 Camera 123") throw new Exception("Ctrl+A/C lost text or modifier.");
                Shortcut(ImGuiKey.X, control); if (value.Length != 0) throw new Exception("Ctrl+X failed.");
                Shortcut(ImGuiKey.V, control); if (value != clipboard) throw new Exception("Ctrl+V failed.");
            }
            numeric = focus = true; clipboard = "42.5"; Frame(); Frame();
            Shortcut(ImGuiKey.A, ImGuiKey.LeftCtrl); Shortcut(ImGuiKey.V, ImGuiKey.LeftCtrl);
            if (Math.Abs(number - 42.5) > .0001) throw new Exception("Numeric paste failed.");
            Console.WriteLine("PASS backend modifier events: left/right Ctrl+A/C/X/V for bilingual text and numeric paste");
        }
        finally
        {
            io.GetClipboardTextFn = previousRead; io.SetClipboardTextFn = previousWrite;
            if (buffer != 0) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(buffer);
            GC.KeepAlive(read); GC.KeepAlive(write);
        }
    }

    private static void CheckCreationProjectLifecycle(Assembly assembly, ICoreClientAPI api, Action<string> capture)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vsdirector-creation-flow-" + Guid.NewGuid().ToString("N"));
        var session = new DirectorCreationSession(new(directory));
        Type R(string name) => assembly.GetType(name, true)!;
        object? Call(object obj, string method, params object[] args) => AccessTools.Method(obj.GetType(), method).Invoke(obj, args);
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, _) => m.Name switch
        {
            "get_PerspectiveViewMat" => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, -5, 1 },
            "get_PerspectiveProjectionMat" => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, -1, 0, 0, -.2, 0 },
            "get_FrameWidth" => 1280,
            "get_FrameHeight" => 800,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var viewportApi = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_Render" ? render : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var runtime = RuntimeHelpers.GetUninitializedObject(R("VSDirector.Runtime.DirectorCreationRuntime"));
        var renderer = RuntimeHelpers.GetUninitializedObject(R("VSDirector.Rendering.DirectorCreationRenderer"));
        AccessTools.Field(runtime.GetType(), "<Session>k__BackingField").SetValue(runtime, session);
        AccessTools.Field(runtime.GetType(), "<Renderer>k__BackingField").SetValue(runtime, renderer);
        AccessTools.Field(runtime.GetType(), "<Status>k__BackingField").SetValue(runtime, "");
        AccessTools.Field(renderer.GetType(), "<Error>k__BackingField").SetValue(renderer, "");
        AccessTools.Property(renderer.GetType(), "Visible").SetValue(renderer, true);
        var owner = new VSDirectorModSystem(); AccessTools.Field(owner.GetType(), "creation").SetValue(owner, runtime);
        var host = Activator.CreateInstance(R("VSDirector.Gui.DirectorImGuiWorkspace"), api)!;
        var workspace = Activator.CreateInstance(R("VSDirector.Gui.DirectorCreationWorkspace"), BindingFlags.Instance | BindingFlags.NonPublic, null, [viewportApi, owner, host], null)!;
        Action draw = () => Call(workspace, "Draw"); Call(host, "AddCreation", draw); Call(host, "Open");
        int legacyFrames = 0; Action legacyDraw = () => legacyFrames++; Call(host, "Add", legacyDraw);
        AccessTools.Field(workspace.GetType(), "<Active>k__BackingField").SetValue(workspace, true);
        var io = ImGui.GetIO(); Vector2 display = io.DisplaySize; io.DisplaySize = new(1280, 800);
        string locale = Lang.CurrentLocale;
        var panelTitles = new[] { "###vsdirector-creation-browser", "###vsdirector-creation-properties", "###vsdirector-creation-timeline" };
        void Frame(bool inspect = false)
        {
            ImGui.NewFrame();
            try
            {
                Call(host, "Draw", 1f / 60);
                if (inspect)
                {
                    foreach (string title in panelTitles)
                    {
                        bool visible = ImGui.Begin(title);
                        try
                        {
                            if ((!visible && title != "###vsdirector-creation-properties") || ImGui.GetWindowDockID() == 0 || ImGui.GetWindowSize().X < 100)
                                throw new Exception("New/open project panel is hidden or undocked: " + title);
                            float expectedFont = ImGui.GetFont().FontSize * (float)GuiElement.scaled(1) * io.FontGlobalScale;
                            if (Math.Abs(ImGui.GetFontSize() - expectedFont) > .1f)
                                throw new Exception("Creation panel font differs from legacy workspace scale: " + title);
                        }
                        finally { ImGui.End(); }
                    }
                }
            }
            finally { ImGui.Render(); }
        }
        try
        {
            // Start with the established workspace layout and no open document.
            Frame(); Frame();
            uint legacyDock = (uint)AccessTools.Field(host.GetType(), "dockId").GetValue(host)!;
            uint legacyTimeline = (uint)AccessTools.Property(host.GetType(), "TimelineDockId").GetValue(host)!;
            Call(host, "SetCreationMode", true);
            int legacyCount = legacyFrames;
            foreach (string language in new[] { "zh-cn", "en" })
            {
                LoadCreationProbeLanguage(language);
                // Closing every creation panel must not carry over into a new/open project.
                foreach (string field in new[] { "browser", "properties", "timeline" }) AccessTools.Field(workspace.GetType(), field).SetValue(workspace, false);
                var camera = new CreationSceneObject { Name = "Camera", Kind = CreationObjectKind.Camera };
                var project = new DirectorCreationProject { Name = "New project", Objects = [camera], CameraId = camera.Id };
                session.Open(project);
                Frame(); Frame(true); Frame(true);
                bool toolbarSurface = false;
                var drawData = ImGui.GetDrawData();
                for (int list = 0; list < drawData.CmdListsCount; list++)
                {
                    var vertices = drawData.CmdLists[list].VtxBuffer;
                    for (int vertex = 0; vertex < vertices.Size; vertex++)
                        if (vertices[vertex].col == 0xff242424 && vertices[vertex].pos.Y <= 40) toolbarSurface = true;
                }
                if (!toolbarSurface) throw new Exception("Creation toolbar does not match the legacy RGB 36 backdrop");
                if (session.SelectedObject != camera.Id) throw new Exception("New project did not select its shooting camera.");
                if (((string)AccessTools.Field(workspace.GetType(), "error").GetValue(workspace)!).Length > 0)
                    throw new Exception("Creation full draw failed: " + AccessTools.Field(workspace.GetType(), "error").GetValue(workspace));
                session.Save(); session.Open(session.Store.Load(project.Id));
                Frame(); Frame(true);
                if (session.SelectedObject != camera.Id) throw new Exception("Reopening the same project lost its camera selection.");
                io.DisplaySize = new(960, 640); Frame(); Frame(); Frame();
                capture("creation-project-" + language);
                io.DisplaySize = new(1280, 800);
            }
            if (legacyFrames != legacyCount) throw new Exception("Creation mode submitted legacy windows.");
            float savedScale = RuntimeEnv.GUIScale;
            try
            {
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                { RuntimeEnv.GUIScale = scale; Frame(); Frame(true); }
            }
            finally { RuntimeEnv.GUIScale = savedScale; }
            Frame();
            Console.WriteLine("PASS creation panel fonts match legacy scaling at 100/150/200%");
            Frame();
            ImGui.NewFrame(); Call(host, "Draw", 1f / 60);
            ImGui.Begin("###vsdirector-creation-view");
            uint viewDock = ImGui.GetWindowDockID(); ImGui.End();
            ImGui.Begin("###vsdirector-creation-properties");
            uint propertiesDock = ImGui.GetWindowDockID(); ImGui.End(); ImGui.Render();
            if (viewDock == 0 || viewDock != propertiesDock) throw new Exception("Creation View is not a right-side tab beside properties");
            AccessTools.Field(workspace.GetType(), "view").SetValue(workspace, false); Frame(); Frame();
            AccessTools.Field(workspace.GetType(), "view").SetValue(workspace, true); Frame(); Frame();
            ImGui.NewFrame(); Call(host, "Draw", 1f / 60); ImGui.Begin("###vsdirector-creation-view");
            if (ImGui.GetWindowDockID() != propertiesDock) throw new Exception("Reopening View changed its dock");
            ImGui.End(); ImGui.Render();
            Console.WriteLine("PASS creation View shares the inspector dock and closes/reopens without changing replay layout");
            foreach (var entry in new[] { ("bonesPanel", "bones"), ("ikPanel", "ik"), ("actionsPanel", "actions") })
            {
                AccessTools.Field(workspace.GetType(), entry.Item1).SetValue(workspace, false); Frame();
                AccessTools.Field(workspace.GetType(), entry.Item1).SetValue(workspace, true);
                AccessTools.Field(workspace.GetType(), "focusRigPanel").SetValue(workspace, entry.Item2); Frame(); Frame();
                ImGui.NewFrame(); Call(host, "Draw", 1f / 60); ImGui.Begin("###vsdirector-creation-" + entry.Item2);
                uint rigDock = ImGui.GetWindowDockID(); ImGui.End(); ImGui.Render();
                if (rigDock != propertiesDock) throw new Exception("Rig tab did not reopen beside properties: " + entry.Item2);
            }
            Console.WriteLine("PASS independent Bones/IK/Actions native dock tabs close and reopen beside Properties");
            uint creationDock = (uint)AccessTools.Field(host.GetType(), "creationDock").GetValue(host)!;
            if (creationDock == 0 || creationDock == legacyDock) throw new Exception("Creation shares the legacy dock root.");
            Call(host, "ResetLayout"); Frame(); Frame(true);
            Call(host, "SetCreationMode", false); Frame();
            if (legacyFrames != legacyCount + 1 || (uint)AccessTools.Property(host.GetType(), "TimelineDockId").GetValue(host)! != legacyTimeline)
                throw new Exception("Creation reset changed the legacy layout or callback routing.");
            Call(host, "SetCreationMode", true); Frame(); Frame(true);
            // Recover panel IDs from existing ImGui windows after host recreation.
            foreach (string field in new[] { "creationLeft", "creationRight", "creationBottom", "creationPreview" })
                AccessTools.Field(host.GetType(), field).SetValue(host, (uint)0);
            Frame(); Frame(true);
            if ((uint)AccessTools.Property(host.GetType(), "TimelineDockId").GetValue(host)! == 0)
                throw new Exception("Creation host failed to recover persisted docking IDs.");
            var selected = session.Project!.Objects.Single();
            io.DisplaySize = new(960, 640); Frame(); Frame();
            AccessTools.Field(workspace.GetType(), "requestedMenu").SetValue(workspace, "项目");
            Frame(); Frame(); Frame(); capture("creation-project-menu");
            io.MousePos = new(20, 350); io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame(); Frame();
            var viewportPosition = (Vector2)AccessTools.Property(host.GetType(), "ViewportPosition").GetValue(host)!;
            var viewportSize = (Vector2)AccessTools.Property(host.GetType(), "ViewportSize").GetValue(host)!;
            io.MousePos = viewportPosition + viewportSize * .5f; Frame(); Frame();
            io.MouseDown[1] = true; Frame(); io.MouseDown[1] = false; Frame(); Frame();
            if (!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) throw new Exception("Viewport right click did not open its context menu. capture=" + io.WantCaptureMouse + " hover=" + AccessTools.Field(workspace.GetType(), "gizmoHover").GetValue(workspace) + " error=" + AccessTools.Field(workspace.GetType(), "error").GetValue(workspace));
            capture("creation-viewport-menu");
            io.AddKeyEvent(ImGuiKey.Escape, true); Frame(); io.AddKeyEvent(ImGuiKey.Escape, false); Frame();
            io.MousePos = new(20, 350); io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
            io.DisplaySize = new(1280, 800); Frame();
            session.SetEditTask(CreationEditTask.Animation); session.AutoKey = true; session.Edit("seed key", _ => session.WriteKey(selected.Id, Guid.Empty, CreationChannel.Position, default)); session.AutoKey = false;
            void Draft() { session.SetTransform(selected.Id, Guid.Empty, new() { Position = new(5, 0, 0) }); session.Changed(); }
            bool continued = false;
            Action next = () => continued = true;
            Draft(); Call(workspace, "Continue", next); Frame();
            if (continued) throw new Exception("Pending preview did not block transition.");
            Frame(); Frame();
            ImGui.NewFrame(); Call(host, "Draw", 1f / 60);
            ImGui.Begin("###creation-pending");
            Vector2 promptSize = ImGui.GetWindowSize(), promptPosition = ImGui.GetWindowPos();
            float promptFont = ImGui.GetFontSize();
            ImGui.End(); ImGui.Render();
            Console.WriteLine("Pending transition dialog size: " + promptSize);
            io.MousePos = promptPosition + new Vector2(promptSize.X - ImGui.GetStyle().FramePadding.X - promptFont * .5f, ImGui.GetStyle().FramePadding.Y + promptFont * .5f);
            Frame(); io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame(); Frame();
            if (AccessTools.Field(workspace.GetType(), "pendingTransition").GetValue(workspace) is not null)
                throw new Exception("Closing pending-preview modal with X left the whole workspace blocked.");
            if (continued || !session.HasTransformDraft) throw new Exception("Titlebar close ran transition or lost draft.");
            if (promptSize.X < 350) throw new Exception("Pending-preview dialog collapsed to a narrow strip.");
            try
            {
                foreach (string language in new[] { "zh-cn", "en" })
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                {
                    LoadCreationProbeLanguage(language); RuntimeEnv.GUIScale = scale;
                    foreach (string dismissal in new[] { "close", "escape", "workspace-escape" })
                    {
                        Call(workspace, "Continue", next); Frame(); Frame(); Frame();
                        ImGui.NewFrame(); Call(host, "Draw", 1f / 60);
                        ImGui.Begin("###creation-pending");
                        var size = ImGui.GetWindowSize(); var position = ImGui.GetWindowPos();
                        float font = ImGui.GetFontSize();
                        ImGui.End(); ImGui.Render();
                        if (size.X < 350 || size.X > io.DisplaySize.X || size.Y > io.DisplaySize.Y)
                            throw new Exception($"Pending dialog bounds invalid: {language}/{scale}: {size}");
                        if (dismissal == "close")
                        {
                            io.MousePos = position + new Vector2(size.X - ImGui.GetStyle().FramePadding.X - font * .5f, ImGui.GetStyle().FramePadding.Y + font * .5f);
                            Frame(); io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
                        }
                        else if (dismissal == "escape")
                        { io.AddKeyEvent(ImGuiKey.Escape, true); Frame(); io.AddKeyEvent(ImGuiKey.Escape, false); Frame(); }
                        else Call(workspace, "RequestEscape");
                        Frame(); Frame();
                        if (AccessTools.Field(workspace.GetType(), "pendingTransition").GetValue(workspace) is not null
                            || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
                            throw new Exception($"Dismissal left modal input blocked: {language}/{scale}/{dismissal}");
                        if (continued || !session.HasTransformDraft) throw new Exception("Dismissal ran transition or lost draft.");
                    }
                }
            }
            finally { RuntimeEnv.GUIScale = savedScale; }
            bool left = false;
            AccessTools.Field(workspace.GetType(), "LeaveRequested").SetValue(workspace, (Action)(() => left = true));
            Call(workspace, "RequestLeave"); Frame();
            if (AccessTools.Field(workspace.GetType(), "pendingTransition").GetValue(workspace) is null || left)
                throw new Exception("Workspace exit was blocked or skipped the draft confirmation after dismissal.");
            Call(workspace, "ResolvePendingTransition", new object[] { null! }); Frame();
            Call(workspace, "Continue", next); Frame();
            Call(workspace, "ResolvePendingTransition", new object[] { null! });
            if (continued || !session.HasTransformDraft) throw new Exception("Cancel lost draft or ran transition.");
            Call(workspace, "Continue", next); Call(workspace, "ResolvePendingTransition", false);
            if (!continued || session.HasTransformDraft || session.Project.Tracks[0].Keys[0].Value.X != 0) throw new Exception("Revert transition failed.");
            continued = false; Draft(); Call(workspace, "Continue", next); Call(workspace, "ResolvePendingTransition", true);
            if (!continued || session.HasTransformDraft || session.Project.Tracks[0].Keys[0].Value.X != 5) throw new Exception("Record transition failed.");
            Frame(); Call(workspace, "RequestLeave");
            if (!left) throw new Exception("Workspace exit stayed blocked after resolving draft.");
            Console.WriteLine("PASS pending modal X/Escape/reopen preserves drafts and releases input/exit in Chinese/English at 100/150/200%");
            Console.WriteLine("PASS pending preview record/revert/cancel transitions and persisted creation dock recovery");
            Console.WriteLine("PASS isolated creation dock, legacy callback suspension/restoration and independent layout reset");
            Console.WriteLine("PASS new/open project restores three dock panels, selects camera, runs real ImGuizmo and resolves Chinese/English creation text");
        }
        finally
        {
            Lang.ChangeLanguage(locale); io.DisplaySize = display; ((IDisposable)host).Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void CheckCreationShaderAndState(Assembly assembly)
    {
        // The engine GL namespace needs its own bindings in the hidden core context.
        OpenTK.Graphics.OpenGL.GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
        var stateType = assembly.GetType("VSDirector.Rendering.DirectorCreationRenderState", true)!;
        using var original = (IDisposable)Activator.CreateInstance(stateType, true)!;
        GL.Disable(EnableCap.DepthTest); GL.Enable(EnableCap.CullFace); GL.Disable(EnableCap.Blend); GL.DepthMask(false);
        GL.BlendFuncSeparate(BlendingFactorSrc.One, BlendingFactorDest.Zero, BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);
        using (var saved = (IDisposable)Activator.CreateInstance(stateType, true)!)
        {
            GL.Enable(EnableCap.DepthTest); GL.Disable(EnableCap.CullFace); GL.Enable(EnableCap.Blend); GL.DepthMask(true);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        if (GL.IsEnabled(EnableCap.DepthTest) || !GL.IsEnabled(EnableCap.CullFace) || GL.IsEnabled(EnableCap.Blend)
            || GL.GetInteger(GetPName.DepthWritemask) != 0 || GL.GetInteger(GetPName.BlendSrcRgb) != (int)BlendingFactor.One)
            throw new Exception("Creation renderer leaked GL depth/cull/blend state.");
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/vsdirector/shaders"));
        int vertex = GL.CreateShader(ShaderType.VertexShader), fragment = GL.CreateShader(ShaderType.FragmentShader), program = GL.CreateProgram();
        try
        {
            foreach (var (shader, filename) in new[] { (vertex, "creation.vsh"), (fragment, "creation.fsh") })
            {
                GL.ShaderSource(shader, File.ReadAllText(Path.Combine(root, filename))); GL.CompileShader(shader);
                GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
                if (compiled == 0) throw new Exception("Creation shader failed: " + GL.GetShaderInfoLog(shader));
                GL.AttachShader(program, shader);
            }
            GL.LinkProgram(program); GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) throw new Exception("Creation shader link failed: " + GL.GetProgramInfoLog(program));
            if (GL.GetAttribLocation(program, "vertexPositionIn") != 0 || GL.GetAttribLocation(program, "uvIn") != 1)
                throw new Exception("Creation shader attributes do not match engine mesh layout.");
            Console.WriteLine("PASS creation GLSL 330 compile/link, mesh attribute bindings and GL state restoration");
        }
        finally { GL.DeleteProgram(program); GL.DeleteShader(vertex); GL.DeleteShader(fragment); }
    }
}
