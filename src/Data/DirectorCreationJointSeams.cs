using System.Numerics;
namespace VSDirector.Data;

public sealed class CreationJointSeam
{
    public Guid Parent { get; set; }
    public Guid Child { get; set; }
    public int ParentFace { get; set; }
    public int ChildFace { get; set; }
}

// Explicit, conservative cube-face seams. Only coincident rest faces are paired;
// unrelated skin/clothing layers and mismatched shapes are never welded by name.
public static class DirectorCreationJointSeams
{
    public static Vector3[] Face(CreationPart part,int face)
    {
        var size=((part.To-part.From)*(1d/16)).Float();float d=(float)part.Inflate/16;
        int axis=face/2,u=(axis+1)%3,v=(axis+2)%3;var result=new Vector3[4];
        for(int i=0;i<4;i++) { var p=Vector3.Zero;p[axis]=(face%2)==0?-d:size[axis]+d;p[u]=(i==1||i==2)?size[u]+d:-d;p[v]=i>=2?size[v]+d:-d;result[i]=p; }
        return result;
    }
    private static int[]? Match(Vector3[] a,Vector3[] b)
    {
        var indices=new int[4];var used=new HashSet<int>();
        for(int i=0;i<4;i++) { int j=Enumerable.Range(0,4).MinBy(k=>Vector3.DistanceSquared(a[i],b[k]));if(Vector3.Distance(a[i],b[j])>.001f||!used.Add(j))return null;indices[i]=j; }
        return indices;
    }
    private static CreationObjectState Rest(CreationModel model)
    { var state=new CreationObjectState { Source=new(),World=Matrix4x4.Identity };DirectorCreationEvaluator.EvaluateBones(model,state);return state; }
    public static CreationJointSeam? Find(CreationModel model,Guid childId)
    {
        var child=model.Parts.Find(p=>p.Id==childId&&!p.Group);var parent=model.Parts.Find(p=>p.Id==child?.ParentId&&!p.Group);if(child is null||parent is null)return null;
        var rest=Rest(model);
        for(int a=0;a<6;a++)for(int b=0;b<6;b++)
        {
            var pa=Face(parent,a).Select(p=>Vector3.Transform(p,rest.Bones[parent.Id])).ToArray();var pb=Face(child,b).Select(p=>Vector3.Transform(p,rest.Bones[child.Id])).ToArray();
            if(Vector3.Cross(pa[1]-pa[0],pa[3]-pa[0]).LengthSquared()<1e-10)continue;
            var ca=Vector3.Transform(((parent.To-parent.From)*(1d/32)).Float(),rest.Bones[parent.Id]);var cb=Vector3.Transform(((child.To-child.From)*(1d/32)).Float(),rest.Bones[child.Id]);
            var normal=Vector3.Normalize(Vector3.Cross(pa[1]-pa[0],pa[3]-pa[0]));
            if(Vector3.Dot(ca-pa[0],normal)*Vector3.Dot(cb-pa[0],normal)>=0)continue;
            if(Match(pa,pb) is not null)return new() { Parent=parent.Id,Child=child.Id,ParentFace=a,ChildFace=b };
        }
        return null;
    }
    public static Dictionary<Guid,Dictionary<Vector3,Vector3>> Deformations(CreationModel model,CreationObjectState state)
    {
        var result=new Dictionary<Guid,Dictionary<Vector3,Vector3>>();if(model.RigEditing.Seams.Count==0)return result;var rest=Rest(model);
        foreach(var seam in model.RigEditing.Seams)
        {
            var parent=model.Parts.Find(p=>p.Id==seam.Parent);var child=model.Parts.Find(p=>p.Id==seam.Child);
            if(parent is null||child is null||state.HiddenParts.Contains(parent.Id)||state.HiddenParts.Contains(child.Id))continue;
            var a=Face(parent,seam.ParentFace);var b=Face(child,seam.ChildFace);
            var matching=Match(a.Select(p=>Vector3.Transform(p,rest.Bones[parent.Id])).ToArray(),b.Select(p=>Vector3.Transform(p,rest.Bones[child.Id])).ToArray());
            if(matching is null||!Matrix4x4.Invert(state.Bones[parent.Id],out var inverseA)||!Matrix4x4.Invert(state.Bones[child.Id],out var inverseB))continue;
            result.TryAdd(parent.Id,new());result.TryAdd(child.Id,new());
            for(int i=0;i<4;i++)
            {
                int j=matching[i];var shared=(Vector3.Transform(a[i],state.Bones[parent.Id])+Vector3.Transform(b[j],state.Bones[child.Id]))*.5f;
                result[parent.Id][a[i]]=Vector3.Transform(shared,inverseA);result[child.Id][b[j]]=Vector3.Transform(shared,inverseB);
            }
        }
        return result;
    }
}
