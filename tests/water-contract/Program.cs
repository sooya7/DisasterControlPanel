using System;
using System.Linq;
using DisasterControlPanel;

static void Equal(float actual, float expected, string name)
{
    if (float.IsNaN(actual) || float.IsInfinity(actual) || Math.Abs(actual - expected) > .0001f) throw new Exception(name + ": " + actual + " != " + expected);
}
static void Check(bool actual, string name) { if (!actual) throw new Exception(name); }

// Timing and terrain regressions exercise the actual production helpers.
Equal(WaterDisasterRules.FloodRamp(100, 900, 6900),0,"future warning stays dry");
Equal(WaterDisasterRules.FloodRamp(900, 900, 6900),0,"impact starts at zero");
Equal(WaterDisasterRules.FloodRamp(1950,0,6000),.5f,"rise spans sixty five percent of selected duration");
Equal(WaterDisasterRules.FloodRamp(3900,0,6000),1,"peak at sixty five percent");
Equal(WaterDisasterRules.HeightOffset(10,10,100,900,6900,1),0,"no forcing before start");
Equal(WaterDisasterRules.HeightOffset(10,10,3900,0,6000,1),90,"high flood peak");
Check(WaterDisasterRules.HeightOffset(10,10,600,0,10800,1)<10,"180 second flood is not already at peak after ten seconds");
Equal(WaterDisasterRules.HeightOffset(11, 10, 300, 0, 6000, 1), -4, "bounded tsunami drawdown");
Equal(WaterDisasterRules.HeightOffset(11, 10, 2250, 0, 6000, 1), 100, "main tsunami crest");
Equal(WaterDisasterRules.HeightOffset(11, 10, 4950, 0, 6000, 1), 25, "smaller trailing wave");
Equal(WaterDisasterRules.HeightOffset(11, 10, 4020, 0, 6000, 1), 0, "gap between main and tail waves");
Equal(WaterDisasterRules.HeightOffset(11, 10, 3000, 0, 6000, 2), 0, "stop leaves no forcing");
Equal(WaterDisasterRules.HeightOffset(11, 10, 6000, 0, 6000, 1), 0, "tail phase never continues spawning waves");
Equal(WaterDisasterRules.HeightOffset(10, 10, 15020000, 15000900, 15020000, 1), 0, "expiry leaves no forcing");
Equal(WaterDisasterRules.HeightOffset(11, 10, 10, 10, 10, 1), 0, "zero duration stays finite and dry");
Equal(WaterDisasterRules.HeightOffset(10, 1, 3900, 0, 6000, 1), 1.5f, "level one flood stays local and mild");
for (int level = 2; level <= 10; level++)
{
    Check(WaterDisasterRules.PeakHeight(10, level) > WaterDisasterRules.PeakHeight(10, level - 1), "increasing flood grades");
    Check(WaterDisasterRules.PeakHeight(11, level) > WaterDisasterRules.PeakHeight(11, level - 1), "increasing tsunami grades");
    Check(WaterDisasterRules.FloodExtent(level) >= WaterDisasterRules.FloodExtent(level - 1), "increasing flood footprint");
    Check(WaterDisasterRules.FrontHalfWidth(level) >= WaterDisasterRules.FrontHalfWidth(level - 1), "increasing wave width");
}
var floodSea = WaterDisasterRules.FloodArea(0, 0, 10, 7161, 383, (x,z) => (300f,83f));
Check(floodSea.Count > 113 && floodSea.Count <= 256 && floodSea.TrueForAll(p => p.x*p.x+p.z*p.z <= 2688f*2688f), "large flood covers a bounded connected sea");
Check(WaterDisasterRules.FloodArea(0, 0, 1, 7161, 383, (x,z) => (300f,83f)).Count == 1, "low flood has one local source");
var riverFlood = WaterDisasterRules.FloodArea(0, 0, 10, 7161, 383, (x,z) => Math.Abs(x)<100 ? (380f,3f) : (390f,0f));
Check(riverFlood.Count >= 20 && riverFlood.TrueForAll(p => Math.Abs(p.x)<100), "sources follow connected river and exclude its banks");
var separateBasins = WaterDisasterRules.FloodArea(0, 0, 10, 7161, 383, (x,z) => Math.Abs(x-224)<40 ? (390f,0f) : (380f,3f));
Check(separateBasins.TrueForAll(p => p.x<=184), "flood never jumps across a dry ridge into isolated water");
var thinBarrier = WaterDisasterRules.FloodArea(0, 0, 10, 7161, 383, (x,z) => Math.Abs(x-105)<6 ? (490f,0f) : (380f,3f));
Check(thinBarrier.TrueForAll(p => p.x<=99), "narrow high ridge blocks connected-source expansion at native water resolution");
var elevatedLake = WaterDisasterRules.FloodArea(0, 0, 10, 7161, 383, (x,z) => x>200 ? (480f,3f) : (380f,3f));
Check(elevatedLake.TrueForAll(p => p.x<=200), "unrelated high lake excluded");
var floodEdge = WaterDisasterRules.FloodArea(7100, 7100, 10, 7161, 383, (x,z) => { Check(Math.Abs(x)<=7161 && Math.Abs(z)<=7161, "flood sampling within map"); return (380f,3f); });
Check(floodEdge.Count>0 && floodEdge.TrueForAll(p => Math.Abs(p.x)<=7161 && Math.Abs(p.z)<=7161), "flood sources within map");
Check(WaterDisasterRules.FloodArea(0, 0, 10, 7161, 383, (x,z) => (390f,0f)).Count==0, "dry origin has no flood sources");
Check(WaterDisasterRules.FloodArea(8000, 0, 10, 7161, 383, (x,z) => (380f,3f)).Count==0, "outside map has no flood sources");
Check(WaterDisasterRules.Upstream(200, -6000, 0, 1) && !WaterDisasterRules.Upstream(200, 6000, 0, 1), "south to north selects south");
Check(WaterDisasterRules.Upstream(-6000, 200, 1, 0) && !WaterDisasterRules.Upstream(6000, 200, 1, 0), "west to east selects west");
Check(WaterDisasterRules.IsSea(1, 5, 6), "existing sea selected");
Check(!WaterDisasterRules.IsSea(13, 0, 6), "land excluded");
Check(!WaterDisasterRules.IsSea(13, 2, 6), "elevated inland water excluded");
Check(!WaterDisasterRules.IsFloodSource(0, -1, 100, 0), "drain excluded");
Check(!WaterDisasterRules.IsFloodSource(0, 2, 100, .1f), "sewage excluded");
Check(WaterDisasterRules.IsFloodSource(2, 2, 100, 0), "clean river included");
var south = WaterDisasterRules.TsunamiFront(1200, -400, 0, 1, 10, 7161, (x,z) => true);
Check(south.Count == 21 && south.TrueForAll(p => p.z == -2192), "front starts near clicked sea, not global map edge");
var moved = WaterDisasterRules.TsunamiFront(2400, 900, 0, 1, 10, 7161, (x,z) => true);
Check(moved[10].x == 2400 && moved[10].z == -892, "changing click moves the wave front");
var east = WaterDisasterRules.TsunamiFront(1200, -400, 1, 0, 10, 7161, (x,z) => true);
Check(east.TrueForAll(p => p.x == -592), "eastbound front starts west of click");
var north = WaterDisasterRules.TsunamiFront(1200, -400, 0, -1, 10, 7161, (x,z) => true);
Check(north.TrueForAll(p => p.z == 1392), "southbound front starts north of click");
var shore = WaterDisasterRules.TsunamiFront(0, 100, 0, 1, 10, 7161, (x,z) => z >= 100);
Check(shore.Count == 21 && shore.TrueForAll(p => p.z == 100), "upstream land falls back to clicked sea");
var narrow = WaterDisasterRules.TsunamiFront(0, 100, 0, 1, 10, 7161, (x,z) => Math.Abs(x) < 50 && Math.Abs(z-100) < 50);
Check(narrow.Count == 1 && narrow[0].x == 0 && narrow[0].z == 100, "source centers stay inside existing sea");
Check(WaterDisasterRules.TsunamiFront(0, 0, 0, 1, 10, 7161, (x,z) => z < -100).Count == 0, "land clicks rejected even with remote sea");
var edge = WaterDisasterRules.TsunamiFront(7000, 0, 0, 1, 10, 7161, (x,z) => true);
Check(edge.Count > 0 && edge.TrueForAll(p => Math.Abs(p.x) <= 7161 && Math.Abs(p.z) <= 7161), "source centers stay inside simulation bounds");
Check(WaterDisasterRules.TsunamiFront(8000, 0, 0, 1, 10, 7161, (x,z) => true).Count == 0, "outside-map click rejected");
var coast = WaterDisasterRules.NearestWater(0,0,7161,true,6,(x,z) => z>=700 ? (1f,5f) : (13f,0f));
Check(coast.HasValue && coast.Value.z >= 700 && coast.Value.z < 800 && coast.Value.x==0, "dry-land click finds nearby sea automatically");
var lake = WaterDisasterRules.NearestWater(0,0,7161,false,6,(x,z) => x>=300 ? (13f,2f) : (13f,0f));
Check(lake.HasValue && lake.Value.surface==15 && lake.Value.x>=300 && lake.Value.x<400, "flood accepts inland water without source entities");
Check(!WaterDisasterRules.NearestWater(0,0,7161,true,6,(x,z) => (13f,2f)).HasValue, "tsunami excludes elevated lake");
Check(!WaterDisasterRules.NearestWater(0,0,7161,false,6,(x,z) => (13f,0f)).HasValue, "dry map reports no water");
Check(!WaterDisasterRules.NearestWater(0,0,7161,false,6,(x,z) => z>4300 ? (1f,5f) : (13f,0f)).HasValue, "remote water search bounded");
var exact = WaterDisasterRules.NearestWater(123,456,7161,true,6,(x,z) => (1f,5f));
Check(exact.Value.x==123 && exact.Value.z==456, "click in water keeps precise position");
Check(!WaterDisasterRules.NearestWater(8000,0,7161,false,6,(x,z) => (1f,5f)).HasValue, "outside-map search rejected");
var bounded = WaterDisasterRules.NearestWater(7150,0,7161,false,6,(x,z) => {Check(Math.Abs(x)<=7161 && Math.Abs(z)<=7161,"samples within map");return (13f,0f);});
Check(!bounded.HasValue,"bounded dry edge");
Equal(WaterDisasterRules.AutoDirection(0,800,0,0,7161,(x,z)=>false),180,"automatic wave toward clicked land");
Equal(WaterDisasterRules.AutoDirection(0,0,0,0,7161,(x,z)=>x>100),90,"water click finds eastern shore");
Equal(WaterDisasterRules.DirectionToward(-1000,10),271,"precise western shore direction");
Equal(WaterDisasterRules.DirectionToward(100,100),45,"diagonal tsunami targets actual heading");
Equal(DisasterVisualRules.MeteorLead(0),3,"zero warning still has fall animation");
Equal(DisasterVisualRules.MeteorLead(30),30,"keep longer warning");
Equal(DisasterVisualRules.Flight(-3,3),0,"flight starts aloft");
Equal(DisasterVisualRules.Flight(0,3),1,"flight meets impact");
Check(DisasterVisualRules.Flight(-1,3)>DisasterVisualRules.Flight(-2,3),"flight progresses");
Equal(DisasterVisualRules.QuakeAge(-1),-1,"quake before impact has no dust");
Equal(DisasterVisualRules.QuakeAge(3,1),0,"second pulse owns its own clock");
Equal(DisasterVisualRules.QuakeAge(3,0),3,"first pulse never resets when second begins");
Equal(DisasterVisualRules.QuakeAge(6,2),0,"third pulse at six seconds");
Equal(DisasterVisualRules.QuakeAge(12,2),6,"no fourth pulse");
Equal(DisasterVisualRules.Fade(-1,12),0,"no pre-impact flash");
Equal(DisasterVisualRules.Fade(12,12),0,"dust expires");
Check(DisasterVisualRules.DebrisHeight(1,60)>0 && DisasterVisualRules.DebrisHeight(10,60)==0,"debris falls back");
var diagonalRiver=WaterDisasterRules.FloodArea(0,0,10,7161,383,(x,z)=>Math.Abs(z-x)<70 ? (380f,3f):(600f,0f));
Check(diagonalRiver.Count>20,"diagonal connected river expands beyond local source");
var slopedRiver=WaterDisasterRules.FloodArea(0,0,10,7161,383,(x,z)=>Math.Abs(x)<50 ? (380f+z*.006f,3f):(600f,0f));
Check(slopedRiver.Count>20,"gentle river gradient does not truncate connected water");
var bentRiver=WaterDisasterRules.FloodArea(0,0,10,7161,383,(x,z)=>Math.Abs(x-180*Math.Sin(z/420))<75 ? (380f,3f):(600f,0f));
Check(bentRiver.Count>15,"curved river expansion follows bends");
float safe=WaterDisasterRules.SafeSourceRadius(0,0,336,7161,(x,z)=>x<70);
Check(safe>7 && safe<70,"entire source disk stops before high dry ridge");
Check(280>safe,"no direct forcing behind the ridge at x280");
Equal(WaterDisasterRules.SafeSourceRadius(0,0,336,7161,(x,z)=>true),336,"open water keeps broad source");
Equal(WaterDisasterRules.SafeSourceRadius(0,0,336,7161,(x,z)=>false),0,"dry source is rejected");
Check(WaterDisasterRules.SafeSourceRadius(7158,0,336,7161,(x,z)=>true)==0,"footprint cannot cross simulation boundary");
for(int ix=-350;ix<=350;ix+=7)for(int iz=-350;iz<=350;iz+=7)
 if(ix*ix+iz*iz<safe*safe)Check(ix<70,"all active disk cells remain water before adding a source");
Check(!WaterDisasterRules.IsThreatened(0,0,473,280,0,400,2000,(x,z)=>x>=70&&x<=210 ? 600 : 380),"native danger prediction respects blocking ridge");
Check(WaterDisasterRules.IsThreatened(0,0,473,280,0,400,2000,(x,z)=>380),"reachable lowland receives native evacuation warning");
Check(!WaterDisasterRules.IsThreatened(0,0,473,280,0,500,2000,(x,z)=>380),"high building is outside forecast water level");
Check(WaterDisasterRules.TravelingOffset(10,2000,0,10000,1,0)>WaterDisasterRules.TravelingOffset(10,2000,0,10000,1,1),"offshore strip rises before coastward strip");
Check(WaterDisasterRules.TravelingOffset(10,5000,0,10000,1,1)>WaterDisasterRules.TravelingOffset(10,5000,0,10000,1,0),"crest phase advances coastward");
Equal(WaterDisasterRules.TravelingOffset(10,5000,0,10000,2,1),0,"stop removes every traveling strip");
Equal(WaterDisasterRules.TravelingOffset(10,10000,0,10000,1,1),0,"no strip outlives forcing end");
Equal(DisasterVisualRules.MeteorLead(0,8),24,"fast simulation retains readable minimum meteor flight");
Check(DisasterVisualRules.DustOpacity(2.99f,9)>0 && DisasterVisualRules.DustOpacity(3,9)>0,"first quake dust survives second pulse");
Equal(DisasterVisualRules.DustOpacity(0,9),0,"new quake cloud fades in without opaque teleport");
Equal(DisasterVisualRules.VisibleTime(3,0,1),3,"presentation freezes when simulation does");
Equal(DisasterVisualRules.VisibleTime(3,8,1),5,"fast simulation presentation capped at two times");
Check(DisasterVisualRules.HailVisible(3709,0,400),"remote camera looking at hail still sees world precipitation");
Check(!DisasterVisualRules.HailVisible(3709,500,400),"unrelated view does not activate near-view hail");
Equal(DisasterVisualRules.FoamOpacity(0,1),0,"foam cannot fake water on dry land");
Check(DisasterVisualRules.FoamOpacity(4,.2f)>0,"measured water slope can display foam");
Check(DisasterVisualRules.HitsGround(101,102,1)&&!DisasterVisualRules.HitsGround(120,102,1),"debris lands on raised terrain");
Check(DisasterVisualRules.KeepTail(false,false,1,1000,900,8),"natural event expiry retains ongoing visual tail");
Check(!DisasterVisualRules.KeepTail(false,true,1,1000,900,8),"explicit stop clears retained visual tail even after expiry");
Check(!DisasterVisualRules.KeepTail(false,false,1,800,900,8),"early external event removal does not resurrect visuals");
Check(!DisasterVisualRules.KeepTail(false,false,1,1000,900,24),"retained visual tail has bounded lifetime");
Check(!WaterDisasterRules.WetSegment(0,0,280,0,(x,z)=>x<70 || x>210),"advancing wave cannot skip a dry island into water behind it");
Check(WaterDisasterRules.WetSegment(0,0,280,280,(x,z)=>true),"diagonal wave path advances through continuous sea");
Console.WriteLine("PASS: water grade/connected-area/main-tail-wave/map-boundary/stop regression and existing visual timing checks (offline only)");

if(args.Length>0)
{
    using var doc=System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(args[0]));var g=doc.RootElement;
    if (g.TryGetProperty("grid", out var nested)) g=nested;
    int cols=g.GetProperty("cols").GetInt32(),rows=g.GetProperty("rows").GetInt32();float minX=g.GetProperty("minX").GetSingle(),minZ=g.GetProperty("minZ").GetSingle(),size=g.GetProperty("cellSize").GetSingle();
    var heights=g.GetProperty("heights");var depths=g.GetProperty("waterDepths");
    (float,float) Sample(float x,float z) {int col=Math.Clamp((int)((x-minX)/size),0,cols-1),row=Math.Clamp((int)((z-minZ)/size),0,rows-1);return (heights[row*cols+col].GetSingle(),depths[row*cols+col].GetSingle());}
    var recorded=WaterDisasterRules.NearestWater(0,0,7161,true,383,Sample);
    Check(recorded.HasValue && recorded.Value.z>0 && Math.Abs(recorded.Value.z)<4096,"recorded city resolves northern nearby sea instead of remote south map edge");
    Console.WriteLine("PASS: recorded live-grid input selects sea x="+recorded.Value.x+" z="+recorded.Value.z+" (recorded input, not live forcing)");
    var floodOrigin=WaterDisasterRules.NearestWater(3714.536f,-2322.158f,7161,false,383,Sample).Value;
    var floodSources=WaterDisasterRules.FloodArea(floodOrigin.x,floodOrigin.z,10,7161,floodOrigin.surface,Sample);
    var waveOrigin=WaterDisasterRules.NearestWater(-1473.988f,-2633.237f,7161,true,383,Sample).Value;
    var waveSources=WaterDisasterRules.TsunamiFront(waveOrigin.x,waveOrigin.z,0,1,10,7161,(x,z)=>{var p=Sample(x,z);return WaterDisasterRules.IsSea(p.Item1,p.Item2,383);});
    Check(floodSources.Count>1 && floodSources.Count<=256,"recorded sea expands flood into bounded connected area");
    Check(floodSources.TrueForAll(p=>Sample(p.x,p.z).Item2>.5f),"recorded flood source centers stay wet");
    Check(waveSources.Count>1 && waveSources.Count<=21,"recorded coast supports broad bounded wave front");
    Check(waveSources.TrueForAll(p=>{var s=Sample(p.x,p.z);return WaterDisasterRules.IsSea(s.Item1,s.Item2,383);}),"recorded tsunami source centers stay at sea");
    var safeFloodSources=floodSources.Select(p=>new {x=p.x,z=p.z,radius=WaterDisasterRules.SafeSourceRadius(p.x,p.z,WaterDisasterRules.SourceRadius(10),7161,(x,z)=>Sample(x,z).Item2>.5f)}).Where(p=>p.radius>=7).ToArray();
    Check(safeFloodSources.Length>1,"recorded water retains multiple safe complete footprints");
    foreach(var source in safeFloodSources)
    {
        for(int z=(int)Math.Floor((source.z-source.radius)/7);z<=(int)Math.Ceiling((source.z+source.radius)/7);z++)
        for(int x=(int)Math.Floor((source.x-source.radius)/7);x<=(int)Math.Ceiling((source.x+source.radius)/7);x++)
        {
            float sx=(x+.5f)*7,sz=(z+.5f)*7;
            if((sx-source.x)*(sx-source.x)+(sz-source.z)*(sz-source.z)<source.radius*source.radius)
                Check(Sample(sx,sz).Item2>.5f,"every active recorded-grid source cell was wet before forcing");
        }
    }
    if (args.Length>1)
    {
        var profile=new System.Collections.Generic.List<object>();
        for(int level=1;level<=10;level++) profile.Add(new {level,floodPeakMetres=WaterDisasterRules.PeakHeight(10,level),tsunamiPeakMetres=WaterDisasterRules.PeakHeight(11,level),floodSourceExtentMetres=WaterDisasterRules.FloodExtent(level),waveFrontMetres=WaterDisasterRules.FrontHalfWidth(level)*2,offshoreMetres=WaterDisasterRules.OffshoreDistance(level)});
        System.IO.File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(new {evidence="Offline execution of actual C# helpers against previously captured water grid; no GPU/animation/propagation verification",floodOrigin,floodSourceCount=floodSources.Count,floodSources,safeFloodSources,waveOrigin,waveSourceCount=waveSources.Count,waveSources,profile},new System.Text.Json.JsonSerializerOptions {WriteIndented=true,IncludeFields=true}));
    }
    Console.WriteLine("PASS: recorded water grid yields "+floodSources.Count+" flood sources and "+waveSources.Count+" tsunami sources (offline only)");
}

// 0.2.1 travelling wave wall
for(int level=1;level<=10;level++)
{
    float lastTravel=-1;
    for(uint f=0;f<6000;f+=15)
    {
        WaterDisasterRules.WaveWall(level,f,0,6000,1,out var mt,out var mh,out var tt,out var th,out var dd);
        if(mt>=0){if(!(mt>=lastTravel-1e-5f&&mt<=1))throw new Exception("crest only moves coastward");lastTravel=mt;}
        if(mh<0||mh>WaterDisasterRules.PeakHeight(11,level)+1e-3f||th>mh+WaterDisasterRules.PeakHeight(11,level)*.5f+1e-3f)throw new Exception("wall height bounded");
        if(dd>0||dd<-4)throw new Exception("drawdown bounded");
    }
    WaterDisasterRules.WaveWall(level,6000,0,6000,1,out var a,out var b,out var c2,out var d2,out var e2);
    if(a!=-1||c2!=-1||b!=0)throw new Exception("no forcing after end");
    WaterDisasterRules.WaveWall(level,3000,0,6000,2,out a,out b,out c2,out d2,out e2);
    if(a!=-1||b!=0)throw new Exception("stop removes wall");
    WaterDisasterRules.WaveWall(level,(uint)(6000*.2),0,6000,1,out a,out b,out c2,out d2,out e2);
    if(!(b>0&&a>0&&a<1))throw new Exception("crest travelling mid-approach");
    if(WaterDisasterRules.CrestDistance(level,0)!=WaterDisasterRules.TsunamiStart(level)||WaterDisasterRules.CrestDistance(level,1)>=0)throw new Exception("crest starts offshore and reaches the coast");
}
var wall=WaterDisasterRules.WallPaths(0,0,0,1,10,7161,(x,z)=>z<300);
if(wall.Count<20||wall.Exists(col=>col.Exists(cell=>cell.z>=300)))throw new Exception("wall stays on sea cells");
if(wall.Exists(col=>col[0].distance<col[col.Count-1].distance))throw new Exception("columns ordered offshore to shore");
var island=WaterDisasterRules.WallPaths(0,0,0,1,10,7161,(x,z)=>z<300 && !(Math.Abs(x)<300&&z>-1500&&z<-1000));
if(WaterDisasterRules.CrestCell(island[island.Count/2],1200)<0)throw new Exception("wall passes islands");
Console.WriteLine("PASS: travelling tsunami wall timing, bounds, sea-only columns (offline only)");
