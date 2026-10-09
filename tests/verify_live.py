# 仅用于 -dcp-test 隔离测试存档；需要 Debug Mod 和既有 CS2MCP 桥。会触发真实灾害，禁止用于正常城市。
import json,time,urllib.request,urllib.error
from pathlib import Path
out=Path(__file__).parent/'live-evidence';out.mkdir(exist_ok=True)
def api(path,body=None,port=8644):
    req=urllib.request.Request(f'http://127.0.0.1:{port}'+path,data=json.dumps(body).encode() if body is not None else None,headers={'Content-Type':'application/json'})
    return json.load(urllib.request.urlopen(req,timeout=12))
def cmd(**kw):return api('/command',kw)
def state():return api('/state')
def wait(check,timeout=12):
    deadline=time.time()+timeout
    while time.time()<deadline:
        s=state()
        if check(s):return s
        time.sleep(.25)
    raise AssertionError(s)
def ui(selector,value=None):
    data={'action':'ui','selector':selector}
    if value is not None:data['value']=str(value)
    return cmd(**data)
def save(name,s): (out/(name+'.json')).write_text(json.dumps(s,ensure_ascii=False,indent=2),encoding='utf-8')
def shot(name): (out/(name+'.png')).write_bytes(urllib.request.urlopen('http://127.0.0.1:8642/screenshot',timeout=20).read())
wait(lambda s:s['inGame'],timeout=180)
api('/sim/control?paused=true',port=8642)
api('/build/place?prefab=ElementarySchool01&x=850&z=650&rotation=0&force=true',port=8642)
assert state()['inGame']
api('/sim/control?paused=true',port=8642)

if not state()['open']:ui('.dcp-launcher')
wait(lambda s:s['open'])
cmd(action='select',id='Tornado');wait(lambda s:s['selected']=='Tornado')
ui('[data-level="8"]');wait(lambda s:s['level']==8)
ui('[data-seconds="120"]');wait(lambda s:s['seconds']==120)
ui('.dcp-target button');wait(lambda s:s['picking'])
ui('.dcp-picking button');wait(lambda s:not s['picking'])
cmd(action='target',x=850,y=133,z=650)
wait(lambda s:s['canSpawn'])
ui('.dcp-spawn')
s=wait(lambda s:len(s['active'])==1)
assert '.L8' in s['active'][0]['variant'] and not s['canSpawn']
assert abs(s['active'][0]['weather']['x']-850)<1
assert abs(s['active'][0]['seconds']-120)<=1
assert api('/inspect?index='+str(s['active'][0]['id'])+'&version='+str(s['active'][0]['version']))['hotspotFrames']==4
save('tornado-created-paused',s);shot('tornado-panel')
ui('.dcp-spawn')
assert len(state()['active'])==1
api('/sim/control?speed=1',port=8642)
time.sleep(2)
save('tornado-simulating',state())
ui('.dcp-stop')
s=wait(lambda s:not s['active'],timeout=18);save('tornado-stopped',s)
print('PASS: native UI binding, tool activation/cancel, level 8, 120s, exact target, native tornado event, duplicate block, native stop',flush=True)
for event,kind in [('Building Fire','building'),('Forest Fire','forest')]:
    api('/sim/control?paused=true',port=8642)
    cmd(action='select',id=event);wait(lambda s:s['selected']==event)
    targets=api('/targets?kind='+kind)
    target=next(t for t in targets if (abs(t['x']-850)<.1 and abs(t['z']-650)<.1) if kind=='building') if kind=='building' else next(t for t in targets if not t['burning'])
    save(kind+'-target-before',target)
    cmd(action='target',**{k:target[k] for k in ['index','version','x','y','z']})
    ui('[data-level="1"]');wait(lambda s:s['level']==1)
    ui('.dcp-spawn');wait(lambda s:len(s['active'])==1)
    api('/sim/control?speed=1',port=8642)
    deadline=time.time()+15
    while time.time()<deadline:
        current=api('/inspect?index='+str(target['index'])+'&version='+str(target['version']))
        if current and current['burning']:break
        time.sleep(.3)
    else:raise AssertionError('native fire did not ignite: '+event)
    save(kind+'-ignited',{'state':state(),'target':current})
    ui('.dcp-stop');s=wait(lambda s:not s['active'],timeout=20)
    assert not api('/inspect?index='+str(target['index'])+'&version='+str(target['version']))['burning']
    save(kind+'-stopped',s)
    print('PASS: '+event+' native OnFire ignition and extinction',flush=True)
api('/sim/control?paused=true',port=8642)
cmd(action='select',id='Hail Storm');wait(lambda s:s['selected']=='Hail Storm')
ui('[data-level="5"]');wait(lambda s:s['level']==5)
cmd(action='target',x=850,y=133,z=650)
ui('.dcp-spawn');s=wait(lambda s:len(s['active'])==1)
save('hail-created',s)
api('/sim/control?speed=1',port=8642)
time.sleep(1)
ui('.dcp-stop');s=wait(lambda s:not s['active'],timeout=15)
api('/sim/control?paused=true',port=8642)
save('final-clean-state',s)
shot('panel-final')
print('PASS: native hail creation/stop, final zero active controlled disasters, simulation paused',flush=True)
