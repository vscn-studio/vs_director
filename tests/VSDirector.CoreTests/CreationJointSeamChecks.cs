using System.Numerics;
using VSDirector.Data;

internal static class CreationJointSeamChecks
{
    public static void Run()
    {
        foreach (int version in new[] { 0, 1 })
        {
            var parent = new CreationPart { To = new(4, 16, 4) };
            var child = new CreationPart { ParentId = parent.Id, From = new(0, 16, 0), Origin = new(0, 16, 0), To = new(4, 32, 4) };
            var model = new CreationModel { AnimationVersion = version, Parts = [parent, child] };
            var actor = new CreationSceneObject { ModelId = model.Id, Transform = new() { Position = new(3, 7, -5), Rotation = new(10, 140, 20), Scale = new(-2, 1.5, .8) } };
            var project = new DirectorCreationProject { Models = [model], Objects = [actor] };
            var seam = DirectorCreationJointSeams.Find(model, child.Id) ?? throw new Exception("Touching cube faces not detected v" + version);
            model.RigEditing.Seams.Add(seam); project.Validate();
            var rest = DirectorCreationEvaluator.Evaluate(project, 0).Objects[actor.Id];
            foreach (var vertices in DirectorCreationJointSeams.Deformations(model, rest).Values)
                foreach (var (a, b) in vertices) Near(a, b, "Rest seam deformed the model");
            actor.Pose[child.Id] = new() { Rotation = new(0, 0, 70) };
            var posed = DirectorCreationEvaluator.Evaluate(project, 0).Objects[actor.Id];
            var deformed = DirectorCreationJointSeams.Deformations(model, posed);
            var aCorners = DirectorCreationJointSeams.Face(parent, seam.ParentFace);
            var bCorners = DirectorCreationJointSeams.Face(child, seam.ChildFace);
            foreach (var a in aCorners)
            {
                var atRest = Vector3.Transform(a, rest.Bones[parent.Id]);
                var b = bCorners.MinBy(p=>Vector3.DistanceSquared(atRest, Vector3.Transform(p, rest.Bones[child.Id])));
                var worldA = Vector3.Transform(deformed[parent.Id][a], posed.Bones[parent.Id] * posed.World);
                var worldB = Vector3.Transform(deformed[child.Id][b], posed.Bones[child.Id] * posed.World);
                Near(worldA, worldB, "Rotating a joint opened the paired surface v" + version);
            }
            var other = CreationJson.Clone(actor); other.Id = Guid.NewGuid(); other.Pose.Clear(); project.Objects.Add(other);
            var independent = DirectorCreationEvaluator.Evaluate(project, 0).Objects[other.Id];
            foreach (var vertices in DirectorCreationJointSeams.Deformations(model, independent).Values)
                foreach (var (a, b) in vertices) Near(a, b, "Another actor inherited seam deformation");
            var copy = DirectorCreationEdits.Duplicate(project); copy.Validate();
            if (copy.Models[0].RigEditing.Seams[0].Child != copy.Models[0].Parts[1].Id) throw new Exception("Seam copy lost references");
            var animation = new CreationAnimation { ModelId = model.Id };
            try { DirectorCreationShape.ExportAnimation(animation, model); throw new Exception("Unsupported seam export accepted"); }
            catch (InvalidDataException e) when (e.Message.Contains("joint seams")) { }
            model.RigEditing.Seams.Add(CreationJson.Clone(seam));
            try { project.Validate(); throw new Exception("Duplicate seam accepted"); }
            catch (InvalidDataException e) when (e.Message.Contains("joint seam")) { }
            model.RigEditing.Seams.RemoveAt(1);
            child.Inflate = .2;
            if (DirectorCreationJointSeams.Find(model, child.Id) is not null) throw new Exception("Mismatched clothing layer was joined");
            child.Inflate = 0;
            DirectorCreationEdits.DeletePart(project, model, parent.Id); project.Validate();
            if (model.RigEditing.Seams.Count != 0) throw new Exception("Deleting a part retained its seam");
        }
    }
    private static void Near(Vector3 a, Vector3 b, string message)
    { if (Vector3.Distance(a, b) > .001f) throw new Exception(message + $" ({a} / {b})"); }
}
