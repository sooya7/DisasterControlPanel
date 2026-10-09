using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using DisasterControlPanel;

void Check(bool value,string label) { if(!value)throw new Exception(label); }
for(int level=1;level<=10;level++)
{
    for(float time=-3;time<=24;time+=.125f)
    {
        for(int i=0;i<24;i++)
        {
            var g=DisasterVfxRules.MeteorGround(i,time,level);
            var c=DisasterVfxRules.MeteorColumn(i%8,time,level);
            foreach(var e in new[]{g,c})
            {
                Check(float.IsFinite(e.X+e.Y+e.Z+e.Width+e.Height+e.Intensity),"finite emitter state");
                Check(e.Intensity>=0&&e.Intensity<=1,"bounded emission intensity");
                Check(e.Width>0&&e.Height>0,"positive scale");
                if(time<0||time>=8)Check(e.Intensity==0,"no early or endless emission");
            }
        }
    }
    var radii=Enumerable.Range(0,24).Select(i=>DisasterVfxRules.MeteorGround(i,2,level))
        .Where(e=>e.Intensity>0).Select(e=>Math.Sqrt(e.X*e.X+e.Z*e.Z)).ToArray();
    Check(radii.Length>=20&&radii.Min()<radii.Max()*.3,"filled radial spread instead of same-radius ring");
    Check(radii.Count(r=>r<radii.Max()*.5)>=5,"several inner dust emitters");
    Check(Enumerable.Range(0,8).Any(i=>DisasterVfxRules.MeteorColumn(i,3,level).Intensity>0),"persistent central smoke after impact");
    Check(Enumerable.Range(0,24).All(i=>DisasterVfxRules.MeteorGround(i,7,level).Intensity==0),"ground emission stops before tail cleanup");
}
for(int pulse=0;pulse<3;pulse++)
{
    float onset=pulse*3+300f/650;
    Check(DisasterVfxRules.QuakeEmission(onset-.01f,pulse,300,10)==0,"quake waits for propagation");
    Check(DisasterVfxRules.QuakeEmission(onset+.2f,pulse,300,10)>0,"quake emits behind front");
    Check(DisasterVfxRules.QuakeEmission(onset+1,pulse,300,10)==0,"quake pulse stops without looping clouds");
}
Check(DisasterVfxRules.ImpactEmission(-.01f)==0&&DisasterVfxRules.ImpactEmission(0)==1&&DisasterVfxRules.ImpactEmission(1)==0,"one short impact burst");
Check(DisasterVfxRules.TrailEmission(-4,3)==0&&DisasterVfxRules.TrailEmission(-1,3)>0&&DisasterVfxRules.TrailEmission(0,3)==0,"trail emits only during flight");
Check(DisasterVfxRules.LandingEmission(0)>0&&DisasterVfxRules.LandingEmission(1)==0,"debris dust cannot emit forever");
Check(DisasterVisualRules.VisibleTime(2,0,1)==2,"pause freezes choreography");
Check(!DisasterVisualRules.KeepTail(false,true,2,3000,2500,10),"explicit stop removes native tail");
Check(!DisasterVisualRules.KeepTail(false,false,2,3000,2500,24),"native graph lifetime bounded");
// 0.2.1 cinematic pass
for(int level=1;level<=10;level++)
{
    Check(DisasterVfxRules.QuakeShake(-.01f,level)==0&&DisasterVfxRules.QuakeShake(.5f,level)>0,"quake shake starts with first pulse");
    Check(DisasterVfxRules.QuakeShake(.5f,level)<=2.2f,"quake shake bounded");
    Check(DisasterVfxRules.QuakeShake(3.5f,level)>0&&DisasterVfxRules.QuakeShake(6.5f,level)>0,"every aftershock shakes");
    Check(DisasterVfxRules.QuakeShake(12,level)==0,"quake shake ends");
    Check(DisasterVfxRules.QuakeShake(.5f,10)>=DisasterVfxRules.QuakeShake(.5f,level),"stronger level shakes harder");
    Check(DisasterVfxRules.MeteorShake(0,level)>DisasterVfxRules.MeteorShake(1,level)&&DisasterVfxRules.MeteorShake(4.1f,level)==0,"impact kick decays and ends");
    Check(DisasterVfxRules.MeteorShake(-2,level)==0,"no shake early in flight");
    float last=0;
    for(float t=0;t<12;t+=.05f){float g=DisasterVfxRules.CrackGrowth(t);Check(g>=last-1e-5f&&g<=1,"cracks only grow");last=g;}
    Check(Math.Abs(DisasterVfxRules.CrackGrowth(2.9f)-.55f)<.01f&&DisasterVfxRules.CrackGrowth(9)==1,"crack opens per pulse");
    for(int i=0;i<36;i++){DisasterVfxRules.Ejecta(i,level,out var az,out var h,out var v);Check(h>0&&v>h*.6f&&float.IsFinite(az),"ejecta launched upward and outward");}
    Check(DisasterVfxRules.BuildingDust(0,1000,level)==0&&DisasterVfxRules.BuildingDust(1000f/(300+level*35)+.3f,1000,level)>0,"building dust waits for wave");
    Check(DisasterVfxRules.QuakeWaveRadius(-.1f,0,level,500)<0&&DisasterVfxRules.QuakeWaveRadius(20,0,level,500)<0,"surface wave bounded");
}
Check(DisasterVfxRules.EjectaGlow(0)>DisasterVfxRules.EjectaGlow(4)*20,"ejecta cools");
Check(DisasterVfxRules.CraterFire(0)==0&&DisasterVfxRules.CraterFire(2)>0&&DisasterVfxRules.CraterFire(12)==0,"crater fire bounded");
Check(DisasterVfxRules.ImpactSmoke(16)==0&&DisasterVfxRules.ImpactSparks(1)==0,"smoke and sparks bounded");
if(args.Length>0)
{
    var frames=Enumerable.Range(0,241).Select(frame=>new { time=frame*.1f,
        ground=Enumerable.Range(0,24).Select(i=>DisasterVfxRules.MeteorGround(i,frame*.1f,10)).ToArray(),
        column=Enumerable.Range(0,8).Select(i=>DisasterVfxRules.MeteorColumn(i,frame*.1f,10)).ToArray() });
    File.WriteAllText(args[0],JsonSerializer.Serialize(frames,new JsonSerializerOptions{IncludeFields=true}));
}
Console.WriteLine("PASS: native VFX choreography levels 1-10, non-annular distribution, staging, bounded emissions, pause/stop/tail (offline, no GPU rendering)");
