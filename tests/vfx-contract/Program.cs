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
            var c=DisasterVfxRules.MeteorPlume(i,time,level);
            foreach(var e in new[]{g,c})
            {
                Check(float.IsFinite(e.X+e.Y+e.Z+e.Width+e.Height+e.Intensity),"finite emitter state");
                Check(e.Intensity>=0&&e.Intensity<=1,"bounded emission intensity");
                Check(e.Width>0&&e.Height>0,"positive scale");
            }
            if(time<0||time>=8)Check(g.Intensity==0,"no early or endless ground emission");
            if(time<.25f||time>=21)Check(c.Intensity==0,"plume waits for impact and ends before the visual tail");
        }
    }
    var radii=Enumerable.Range(0,24).Select(i=>DisasterVfxRules.MeteorGround(i,2,level))
        .Where(e=>e.Intensity>0).Select(e=>Math.Sqrt(e.X*e.X+e.Z*e.Z)).ToArray();
    Check(radii.Length>=20&&radii.Min()<radii.Max()*.3,"filled radial spread instead of same-radius ring");
    Check(radii.Count(r=>r<radii.Max()*.5)>=5,"several inner dust emitters");
    Check(Enumerable.Range(0,24).Any(i=>DisasterVfxRules.MeteorPlume(i,3,level).Intensity>0),"persistent central plume after impact");
    Check(Enumerable.Range(0,16).Max(i=>DisasterVfxRules.MeteorPlume(i,12,level).Y)>Enumerable.Range(0,16).Max(i=>DisasterVfxRules.MeteorPlume(i,2,level).Y),"plume keeps climbing");
    Check(Enumerable.Range(16,8).Select(i=>DisasterVfxRules.MeteorPlume(i,10,level)).All(e=>e.Y>=Enumerable.Range(0,16).Max(i=>DisasterVfxRules.MeteorPlume(i,10,level).Y)-1),"cap spreads on top of the column");
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
Check(!DisasterVisualRules.KeepTail(false,false,2,3000,2500,DisasterVisualRules.Lifetime(2)),"native graph lifetime bounded");
Check(DisasterVisualRules.KeepTail(false,false,1,3000,2500,60)&&!DisasterVisualRules.KeepTail(false,false,1,3000,2500,75),"fault outlives the event, then goes");
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
}
Check(DisasterVfxRules.EjectaGlow(0)>DisasterVfxRules.EjectaGlow(4)*20,"ejecta cools");
Check(DisasterVfxRules.CraterFire(0)==0&&DisasterVfxRules.CraterFire(2)>0&&DisasterVfxRules.CraterFire(18)==0,"crater fire bounded");
Check(DisasterVfxRules.ImpactSmoke(30)==0&&DisasterVfxRules.ImpactSparks(1)==0,"smoke and sparks bounded");
if(args.Length>0)
{
    var frames=Enumerable.Range(0,241).Select(frame=>new { time=frame*.1f,
        ground=Enumerable.Range(0,24).Select(i=>DisasterVfxRules.MeteorGround(i,frame*.1f,10)).ToArray(),
        plume=Enumerable.Range(0,24).Select(i=>DisasterVfxRules.MeteorPlume(i,frame*.1f,10)).ToArray() });
    File.WriteAllText(args[0],JsonSerializer.Serialize(frames,new JsonSerializerOptions{IncludeFields=true}));
}
// 0.2.2 meteor entry, fireball, shock ring and crater
foreach(float lead in new[]{3f,15f,30f,120f})foreach(float speed in new[]{0f,1f,2f,8f})
{
    float flight=DisasterVfxRules.MeteorFlightTime(lead,speed);
    Check(flight<=lead&&flight>=Math.Min(lead,3),"flight fits inside the warning");
    Check(DisasterVfxRules.MeteorProgress(-flight-.01f,flight)<0&&DisasterVfxRules.MeteorProgress(0,flight)==1,"meteor appears at flight start and lands at impact");
    float last=-1;
    for(float t=-flight;t<0;t+=flight/64){float p=DisasterVfxRules.MeteorProgress(t,flight);Check(p>=last&&p<=1,"meteor only moves forward");last=p;}
    for(int k=0;k<64;k++)
    {
        float along=DisasterVfxRules.TrailPoint(k,64),pass=DisasterVfxRules.MeteorPassTime(along,flight);
        Check(pass>=-flight-1e-3f&&pass<=1e-3f,"trail point is passed during the flight");
        Check(Math.Abs(1-DisasterVfxRules.MeteorProgress(pass,flight)-along)<.01f,"smoke puffs where the head actually was");
    }
}
Check(Math.Abs(DisasterVfxRules.MeteorFlightTime(30,1)-7)<.01f&&Math.Abs(DisasterVfxRules.MeteorFlightTime(60,8)-56)<.01f,"about seven real seconds of fall at any speed");
Check(DisasterVfxRules.TrailSmoke(-.1f)==0&&DisasterVfxRules.TrailSmoke(1)>0&&DisasterVfxRules.TrailSmoke(18)==0,"sky trail lingers then clears");
Check(DisasterVfxRules.TailLength(0,30)==0&&DisasterVfxRules.TailLength(5000,30)==270,"tail grows from the entry point and is capped");
for(int level=1;level<=10;level++)
{
    float crater=25+level*11;
    Check(DisasterVfxRules.FlashRadius(-.01f,crater)==0&&DisasterVfxRules.FlashRadius(.9f,crater)==0,"fireball only right after impact");
    Check(DisasterVfxRules.FlashRadius(.14f,crater)>=crater*1.69f&&DisasterVfxRules.FlashRadius(.5f,crater)<crater*2.1f,"fireball snaps out and stays bounded");
    Check(DisasterVfxRules.FlashEmission(0)>DisasterVfxRules.FlashEmission(.4f)&&DisasterVfxRules.FlashEmission(.89f)<1,"fireball cools before it vanishes");
    float r1=DisasterVfxRules.ShockRadius(.2f,level,crater),r2=DisasterVfxRules.ShockRadius(1,level,crater),r3=DisasterVfxRules.ShockRadius(2.9f,level,crater);
    Check(r1>0&&r2>r1&&r3>r2&&r3<=crater*(3.2f+level*.25f)+1e-3f,"shock ring expands, decelerates and is bounded");
    Check(DisasterVfxRules.ShockRadius(3,level,crater)<0&&DisasterVfxRules.ShockEmission(3)==0,"shock ring ends");
}
Check(DisasterVfxRules.CraterGlow(0)==0&&DisasterVfxRules.CraterGlow(.5f)>DisasterVfxRules.CraterGlow(20)*10,"crater cools from molten to dark");
Check(DisasterVfxRules.CraterSink(30,40)==0&&DisasterVfxRules.CraterSink(39.9f,40)>2.5f,"crater drape sinks out of sight before cleanup");
Check(DisasterVfxRules.CraterFire(17)>0&&DisasterVfxRules.CraterFire(18)==0&&DisasterVfxRules.ImpactSmoke(29)>0&&DisasterVfxRules.ImpactSmoke(30)==0,"crater fire and smoke inside the visual lifetime");

// 0.2.2 fault rupture
for(int level=1;level<=10;level++)foreach(int seed in new[]{1,777,31337,99999})
{
    float radius=200+level*150;
    var fault=DisasterVfxRules.QuakeFault(seed,level,radius);
    float half=DisasterVfxRules.FaultHalfLength(level,radius),reach=DisasterVfxRules.FaultReach(fault);
    var main=fault.Where(f=>f.Main).ToList();
    Check(main.Count>=2&&fault.Count>main.Count-1,"fault has segments and splays");
    Check(fault.All(f=>f.X.Count>=2&&f.X.Count==f.Z.Count&&f.X.Count==f.Reach.Count&&f.X.Count==f.Width.Count),"consistent fissure arrays");
    Check(fault.Sum(f=>f.X.Count)<65536/7,"fault mesh fits 16-bit indices");
    Check(fault.All(f=>f.Width.All(w=>w>=0&&w<=DisasterVfxRules.FissureWidth(level)*1.21f)),"bounded fissure width");
    Check(main.All(f=>f.Reach.All(r=>r>=0&&r<=half+1)),"main trace stays within its half-length");
    Check(fault.All(f=>f.X.Zip(f.Z,(x,z)=>Math.Sqrt(x*x+z*z)).All(d=>d<radius)),"fault stays inside the shaken area");
    // A line, not a star: every main point lies near the fault axis.
    float heading=DisasterVfxRules.Hash(seed,101)*(float)Math.PI;double ax=Math.Cos(heading),az=Math.Sin(heading);
    Check(main.All(f=>f.X.Zip(f.Z,(x,z)=>Math.Abs(-az*x+ax*z)).All(d=>d<20+level*8)),"main rupture follows one fault line");
    double spanPos=main.SelectMany(f=>f.X.Zip(f.Z,(x,z)=>ax*x+az*z)).Max(),spanNeg=main.SelectMany(f=>f.X.Zip(f.Z,(x,z)=>ax*x+az*z)).Min();
    Check(spanPos>half*.9&&spanNeg<-half*.9,"rupture runs both ways from the epicentre");
    Check(main.Any(f=>Math.Abs(f.X[0])<1e-3&&Math.Abs(f.Z[0])<1e-3),"rupture starts at the epicentre");
    float last=0;
    for(float t=0;t<12;t+=.05f){float front=DisasterVfxRules.RuptureFront(t,reach);Check(front>=last-1e-3f,"rupture only extends");last=front;}
    Check(DisasterVfxRules.RuptureFront(0,reach)==0&&DisasterVfxRules.RuptureFront(9,reach)>=reach,"rupture starts closed and finally reaches every point");
    Check(DisasterVfxRules.FissureOpen(DisasterVfxRules.RuptureFront(1,reach),reach,reach)==0,"far tip still closed during the first pulse");
}
Check(DisasterVfxRules.FissureWiden(-.1f)==0&&DisasterVfxRules.FissureWiden(2.9f)<DisasterVfxRules.FissureWiden(5.9f)&&DisasterVfxRules.FissureWiden(5.9f)<DisasterVfxRules.FissureWiden(9),"every pulse widens the fissure");
Check(DisasterVfxRules.FissureWiden(.35f)>DisasterVfxRules.FissureWiden(2.9f)&&DisasterVfxRules.FissureWiden(30)<=1.0001f,"jolt overshoots then settles at full width");
Check(DisasterVfxRules.FissureSink(70,75)==0&&DisasterVfxRules.FissureSink(74.99f,75)>2.4f,"fault sinks under the terrain before cleanup");
Check(DisasterVfxRules.FissureDust(.5f,300,280,1000)>0&&DisasterVfxRules.FissureDust(.5f,300,320,1000)==0,"tear dust right behind the tip only");
Check(DisasterVfxRules.FissureDust(2.5f,550,100,1000)==0&&DisasterVfxRules.FissureDust(3.4f,550,100,1000)>0&&DisasterVfxRules.FissureDust(8,1020,100,1000)==0,"aftershocks shake dust, quiet in between and after");
Console.WriteLine("PASS: 0.2.2 meteor entry/fireball/shock/crater and fault rupture geometry, growth, widening, dust (offline, no GPU rendering)");
Console.WriteLine("PASS: native VFX choreography levels 1-10, non-annular distribution, staging, bounded emissions, pause/stop/tail (offline, no GPU rendering)");
