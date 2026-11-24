using System.Numerics;
using VSDirector.Data;

internal static class CreationDragParticleChecks
{
    internal static void Run()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        // Off-centre perspective, camera roll, wide/tall viewports and local axes.
        foreach(float yaw in new[]{.25f,1.1f,2.4f,4.2f})
        foreach(float pitch in new[]{-.8f,.3f,1.1f})
        foreach(float roll in new[]{0f,.5f})
        foreach(var size in new[]{new Vector2(1100,700),new Vector2(500,1000)})
        {
            var eye=new Vector3(MathF.Sin(yaw)*MathF.Cos(pitch),MathF.Sin(pitch),MathF.Cos(yaw)*MathF.Cos(pitch))*8;
            var view=Matrix4x4.CreateLookAt(eye,Vector3.Zero,Vector3.UnitY)*Matrix4x4.CreateRotationZ(roll);
            var projection=Matrix4x4.CreatePerspectiveFieldOfView(1.2f,size.X/size.Y,.1f,100);
            var vp=view*projection;var origin=new Vector2(100,70);var pivot=new Vector3(1.2f,.7f,-.6f);
            Vector2 Screen(Vector3 point){var p=Vector4.Transform(new Vector4(point,1),vp);return origin+new Vector2((p.X/p.W+1)*size.X/2,(1-p.Y/p.W)*size.Y/2);}
            foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ,Vector3.Normalize(new Vector3(1,2,3))})
            {
                var start=Screen(pivot+axis*.5f);
                var drag=DirectorCreationDrag.Begin(vp,origin,size,start,pivot,axis,false);
                if(drag is null)continue;
                Check(drag.TryOffset(Screen(pivot+axis*.9f),out var delta),"Axis drag failed");
                Check(Vector3.Distance(delta,axis*.4f)<.002f,"Perspective drag slipped off its axis or jumped");
                Check(drag.TryOffset(start,out var reset)&&reset.Length()<.001f,"Returning to drag start changed position");
                var tangent=Vector3.Normalize(Vector3.Cross(axis,Math.Abs(axis.Y)<.9?Vector3.UnitY:Vector3.UnitX));
                var plane=DirectorCreationDrag.Begin(vp,origin,size,Screen(pivot),pivot,axis,true);
                if(plane is not null&&plane.TryOffset(Screen(pivot+tangent*.25f),out var offset))
                    Check(Vector3.Distance(offset,tangent*.25f)<.002f,"Plane drag changed its normal component");
            }
        }
        // Overlay handles inside the render near plane are still in front of
        // the eye. They must begin and complete axis/plane drags without jumps.
        foreach (float distance in new[] { .002f, .02f, .05f, .1f, 4f })
        foreach (bool orthographic in new[] { false, true })
        {
            var view = Matrix4x4.CreateTranslation(0, 0, -distance);
            var projection = orthographic ? Matrix4x4.CreateOrthographic(2, 2, .001f, 100)
                : Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1, .1f, 100);
            var vp = view * projection; var size = new Vector2(800);
            Vector2 Screen(Vector3 point) { var p = Vector4.Transform(new Vector4(point, 1), vp); return new((p.X/p.W+1)*400, (1-p.Y/p.W)*400); }
            foreach (bool plane in new[] { false, true })
            {
                var axis = plane ? Vector3.UnitZ : Vector3.UnitX;
                var start = Screen(Vector3.Zero);
                var drag = DirectorCreationDrag.Begin(vp, Vector2.Zero, size, start, Vector3.Zero, axis, plane);
                Check(drag is not null, "Visible near-plane handle could not start dragging");
                var expected = new Vector3(distance * .1f, plane ? distance * .05f : 0, 0);
                Check(drag!.TryOffset(Screen(expected), out var offset) && Vector3.Distance(offset, expected) < 1e-5f,
                    "Close-up drag jumped or lost its axis/plane constraint");
                Check(drag.TryOffset(start, out offset) && offset.Length() < 1e-5f, "Close-up drag could not return to its origin");
            }
            if (!orthographic)
                Check(DirectorCreationDrag.Begin(vp, Vector2.Zero, size, new(400), new(0,0,distance*2), Vector3.UnitX, false) is null,
                    "A pivot behind the eye became draggable");
        }
        var emitter=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParticleCount=4,ParticleLife=2,ParticleDuration=1,
            ParticleSettings=new(){Burst=true,Spread=default,Velocity=new(1,2,0),VelocitySpread=default,Acceleration=new(0,-2,0),
                StartSize=.2,EndSize=.6,SizeSpread=0,StartOpacity=1,EndOpacity=0}};
        var a=DirectorCreationParticles.Sample(emitter,1).ToArray();
        Check(a.Length==4&&a.All(p=>p.Position==new CreationVector(1,1,0)&&Math.Abs(p.Size-.4)<1e-6&&p.Alpha==.5),"Particle velocity/gravity/size/fade incorrect");
        Check(!DirectorCreationParticles.Sample(emitter,2).Any(),"Burst particles never expired");
        emitter.ParticleCount=0;Check(!DirectorCreationParticles.Sample(emitter,.5).Any(),"Zero rate still emitted particles");
        emitter.ParticleCount=10000;emitter.ParticleLife=300;emitter.ParticleSettings.Burst=false;emitter.ParticleDuration=600;
        Check(DirectorCreationParticles.Sample(emitter,300).Count()<=2048,"Particle budget unbounded");
        emitter.ParticleCount=10;emitter.ParticleLife=2;emitter.ParticleSettings.Spread=new(1,1,1);
        var saved=CreationJson.Clone(emitter);
        Check(DirectorCreationParticles.Sample(emitter,.8).SequenceEqual(DirectorCreationParticles.Sample(saved,.8)),"Saved particles changed random sample");
        string root=Path.Combine(Path.GetTempPath(),"director-particle-check-"+Guid.NewGuid().ToString("N"));
        try
        {
        var project=new DirectorCreationProject{Objects=[emitter]};var session=new DirectorCreationSession(new(root));
        session.Open(project);session.Edit("size",_=>emitter.ParticleSettings.StartSize=.7);session.Undo();
        Check(session.Project!.Objects[0].ParticleSettings!.StartSize==.2,"Particle undo lost settings");
        var preset=CreationJson.Clone(new CreationParticlePreset{Settings=saved.ParticleSettings!});preset.Validate();
        preset.Settings.StartSize=double.NaN;bool rejected=false;try{preset.Validate();}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Invalid particle preset accepted");
        emitter.ParticleSettings.Texture="assets/smoke.png";
        Check(DirectorCreationDependencies.Collect(project).Any(d=>d.Source=="assets/smoke.png"),"Export validation ignored particle texture");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
