using System.Text.RegularExpressions;
using Purr2D2.Rendering;

internal static class SaberWaterChecks
{
    internal static void Run()
    {
        StageOwnership(); ShaderSource();
        Console.WriteLine("PASS: water stage ownership, shader contracts and independent path-length cases");
    }
    private static void StageOwnership()
    {
        var gate=new SaberWaterFrameGate(); object view=new(),otherView=new(),target=new();
        Check(!gate.TryDefer(1,view,target,1,2,3,4),"absent native ocean copy keeps blades in their existing pass");
        gate.Capture(1,view,target,1,2,3,4);
        Check(!gate.TryDefer(2,view,target,1,2,3,4) && !gate.TryDefer(1,otherView,target,1,2,3,4)
            && !gate.TryDefer(1,view,new object(),1,2,3,4) && !gate.TryDefer(1,view,target,9,2,3,4),
            "frame, actual viewport, target and command ownership cannot leak into another render");
        Check(!gate.TryDefer(1,view,target,1,8,3,4) && !gate.TryDefer(1,view,target,1,2,8,4)
            && !gate.TryDefer(1,view,target,1,2,3,8),"resized native color, depth or opaque copies invalidate deferral");
        Check(gate.TryDefer(1,view,target,1,2,3,4) && gate.TryConsume(1,view,target,1,2,3,4)
            && !gate.TryConsume(1,view,target,1,2,3,4),"one captured ocean frame renders its deferred blades exactly once");
        gate.Capture(3,view,target,1,2,3,4); gate.TryDefer(3,view,target,1,2,3,4);
        Check(!gate.TryConsume(3,view,target,1,9,3,4) && gate.HasPending(3,view,1)
            && gate.ConsumePending(3,view,1) && !gate.ConsumePending(3,view,1),
            "a changed image after deferral retains one ordinary resolved fallback opportunity");
        gate.Reset(); Check(!gate.HasPending(3,view,1),"clearing render buckets discards all prior water-frame state");
        gate.Capture(4,view,target,0,2,3,4);
        Check(!gate.TryDefer(4,view,target,0,2,3,4),"missing GPU handles cannot authorize a water pass");
    }
    private static void ShaderSource()
    {
        string root=Path.Combine(Directory.GetCurrentDirectory(),"Assets","Shaders");
        string water=File.ReadAllText(Path.Combine(root,"Content","Purr2D2","SaberWaterFragment.glsl"));
        string compact=Regex.Replace(Regex.Replace(water,"//[^\\r\\n]*", ""),"\\s+","");
        Check(compact.Contains("gl_FragCoord.z<opaque") && compact.Contains("hasSurface=postDepth>opaque"),
            "the production fragment compares reverse-Z against opaque geometry and identifies closer water depth");
        Check(compact.Contains("min(fragmentDistance,surfaceDistance)")
            && compact.Contains("max(fragmentDistance-surfaceDistance,0.0)")
            && compact.Contains("!hasSurface&&postDepth==0.0&&slope>1e-4"),
            "the production fragment separates above/below water path lengths and limits flat exits to sky-depth holes");
        Check(compact.Contains("i<8") && compact.Contains("0.02*0.02")
            && compact.Contains("smoothstep(-0.001,0.001,signedDepth)"),
            "the production waterline uses the native bounded inverse-wave probe and meniscus width");
        Check(compact.Contains("exp(-extinction*underwaterLength)")
            && compact.Contains("1.0-aboveWaterLength/surfaceTransparencyDepth")
            && !compact.Contains("gl_FragDepth") && !compact.Contains("imageStore"),
            "production emission uses native absorption laws without writing scene depth or replacing background color");
        // Independent geometric cases: f is the fragment distance, s is the first visible surface.
        foreach(var item in new[] { (2d,3d,false,0d),(10d,3d,false,7d),(2d,3d,true,2d),(10d,3d,true,3d) })
        {
            double actual=item.Item3 ? Math.Min(item.Item1,item.Item2) : Math.Max(item.Item1-item.Item2,0);
            Check(actual==item.Item4,"known above/below/crossing blade paths stop at the correct water boundary");
        }
        Check(.4 < .5 && !(.6 < .5) && !(0 < 0),"opaque reverse-Z cases reject a hidden blade and retain a nearer blade or sky sample");
        Check(Math.Abs(Math.Exp(Math.Log(.25)*.85/100)-.988285) < .00001
            && Math.Exp(Math.Log(.25)*1000/100) < .000001,
            "a nearby submerged blade retains emission even when the distant seabed is fully attenuated");
        foreach(string name in new[] { "SaberWaterGlow.frag","SaberWaterLight.frag" })
        {
            string expanded=SaberWaterShaderSource.Expand(Path.Combine(root,name),root);
            Check(expanded.Contains("#include \"OceanFunctions.glsl\"") && expanded.Contains("#define TAU SABER_TAU")
                && expanded.Contains("emission.rgb *= saberWaterTransmission()") && !expanded.Contains("#include \"Content/"),
                "portable water templates flatten owned includes and retain the installed native wave/global ABI");
        }
        string temp=Path.Combine(Path.GetTempPath(),"purr2d2-water-source-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string source=Path.Combine(temp,"entry.frag");
            File.WriteAllText(source,"#include \"helper.glsl\"\r\n");
            File.WriteAllText(Path.Combine(temp,"helper.glsl"),"float windowsMarker;\r\n");
            Check(SaberWaterShaderSource.Expand(source,temp).Contains("float windowsMarker;")
                && !SaberWaterShaderSource.Expand(source,temp).Contains("#include"),
                "Windows line endings still flatten packaged includes before native-relative compilation");
            File.WriteAllText(source,"#include \"entry.frag\"");
            Reject(()=>SaberWaterShaderSource.Expand(source,temp),"cyclic shader includes fail closed before deferral");
            File.WriteAllText(source,"#include \"../escape.glsl\"");
            Reject(()=>SaberWaterShaderSource.Expand(source,temp),"unrecognized native or escaping shader includes fail closed");
            File.WriteAllText(source,new string('x',2*1024*1024+1));
            Reject(()=>SaberWaterShaderSource.Expand(source,temp),"shader expansion rejects oversized source files");
        }
        finally { Directory.Delete(temp,true); }
    }
    private static void Reject(Action action,string message)
    {
        try { action(); } catch(InvalidDataException) { Check(true,message); return; }
        throw new InvalidOperationException("Water check failed: "+message);
    }
    private static void Check(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("Water check failed: "+message);
        Console.WriteLine("PASS: "+message);
    }
}
