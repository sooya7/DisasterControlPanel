from pathlib import Path
import json
from playwright.sync_api import sync_playwright

root = Path(__file__).resolve().parents[1]
css = (root / 'ui/panel.css').read_text(encoding='utf-8')
js = (root / 'ui/panel.js').read_text(encoding='utf-8')
fixture = {
    'inGame': True, 'open': True, 'picking': False, 'selected': 'Tornado',
    'level': 5, 'seconds': 60, 'warning': 30, 'citywide': False, 'direction': 0, 'target': None, 'canSpawn': False, 'active': [],
    'message': '选择灾难后，点击地图即可触发。', 'pending': False,
    'types': [
        {'id': 'Building Fire', 'name': '建筑火灾', 'kind': 'building', 'nativeName': 'Building Fire'},
        {'id': 'Forest Fire', 'name': '森林火灾', 'kind': 'forest', 'nativeName': 'Forest Fire'},
        {'id': 'Tornado', 'name': '龙卷风', 'kind': 'weather', 'nativeName': 'Tornado'},
        {'id': 'Earthquake', 'name': '地震', 'kind': 'earthquake', 'nativeName': '自定义 · 地震'},
        {'id': 'Meteor', 'name': '陨石', 'kind': 'meteor', 'nativeName': '自定义 · 陨石'},
        {'id': 'Sinkhole', 'name': '地面塌陷', 'kind': 'sinkhole', 'nativeName': '自定义 · 地面塌陷'},
        {'id': 'Lightning Strike', 'name': '雷击', 'kind': 'weather', 'nativeName': 'Lightning Strike'},
        {'id': 'Flood', 'name': '洪水', 'kind': 'flood', 'nativeName': 'Flood'},
        {'id': 'Tsunami', 'name': '海啸', 'kind': 'tsunami', 'nativeName': 'Tsunami'},
        {'id': 'Hailstorm', 'name': '冰雹', 'kind': 'weather', 'nativeName': 'Hailstorm'},
    ]
}
html = '''<!doctype html><meta charset="utf-8"><title>灾难面板交互验证</title>
<style>body{margin:0;background:#668077;color:white;font-family:sans-serif}aside{padding:32px}small{display:block;margin-top:8px}</style>
<aside>灾难控制面板 · 浏览器交互预览<small>本页使用模拟数据，游戏内实机验收另行记录。</small><button id="mock-map" onclick="simulateMapClick()">地图区域（模拟）</button></aside>
<script>window.calls=[];window.mapRequests=[];window.failTarget=false;window.state=FIXTURE;
window.simulateMapClick=function(){
if(!state.picking||state.pending)return;
if(failTarget){state.message='请选择有效位置。';__dcp.update(state);return;}
mapRequests.push({type:state.selected,level:state.level,seconds:state.seconds,warning:state.warning,direction:state.direction,citywide:state.citywide});
state.picking=false;state.pending=true;state.target=null;state.canSpawn=false;state.message='正在初始化…';__dcp.update(state);};
window.completeSpawn=function(){state.pending=false;state.active.push({name:'龙卷风',seconds:state.seconds});state.message='已触发龙卷风。';__dcp.update(state);};
window['cs2/api']={trigger:function(group,name,value){calls.push({name:name,value:value});
if(name==='toggle'){state.open=!state.open;if(!state.open)state.picking=false;}
if(name==='select'&&!state.pending){state.selected=value;state.target=null;state.canSpawn=false;state.picking=true;state.message='';}
if(name==='level')state.level=value;
if(name==='seconds')state.seconds=value;
if(name==='warning')state.warning=value;
if(name==='direction')state.direction=value;
if(name==='citywide')state.citywide=value;
if(name==='pick'&&!state.pending){state.picking=true;state.message='';}
if(name==='cancel')state.picking=false;
if(name==='spawn'){state.target=null;state.canSpawn=false;state.active=[{name:'龙卷风',seconds:60}];}
if(name==='stop')state.active=[];
if(window.__dcp)window.__dcp.update(state); }};window.__dcpCss=CSS;</script><script>JS</script>
<script>window.__dcp.update(state);</script>'''.replace('FIXTURE', json.dumps(fixture, ensure_ascii=False)).replace('CSS',json.dumps(css)).replace('JS',js)
pagefile = Path(__file__).parent / 'preview.html'
pagefile.write_text(html, encoding='utf-8')
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page(viewport={'width':1280,'height':900},device_scale_factor=1)
    errors=[]
    page.on('pageerror',lambda e:errors.append(str(e)))
    page.goto(pagefile.as_uri()); page.wait_for_load_state('networkidle')
    assert page.get_by_role('button',name='在地图上触发',exact=True).is_enabled()
    page.get_by_role('button',name='建筑火灾',exact=True).click()
    assert page.evaluate('state.picking && mapRequests.length===0')
    assert page.locator('.dcp-picking').is_visible()
    assert page.locator('.dcp-body').is_visible()
    assert not page.locator('.dcp-duration').first.is_visible()
    page.get_by_role('button',name='龙卷风',exact=True).click()
    assert page.locator('.dcp-duration').first.is_visible()
    page.get_by_role('button',name='地震',exact=True).click()
    assert page.locator('.dcp-warning').is_visible()
    assert page.get_by_role('button',name='受灾范围',exact=True).evaluate('(button) => button.classList.contains("dcp-selected")')
    assert '居民由游戏原生机制处理' in page.locator('.dcp-scope').inner_text()
    page.locator('[data-warning="60"]').click()
    assert page.evaluate('state.warning===60')
    page.get_by_role('button',name='受灾范围',exact=True).click()
    assert page.evaluate('state.citywide===false')
    page.get_by_role('button',name='全城避难',exact=True).click()
    assert page.evaluate('state.citywide===true')
    page.get_by_role('button',name='海啸',exact=True).click()
    assert page.locator('.dcp-direction').count()==0
    assert not page.locator('.dcp-warning').is_visible()
    assert not page.locator('.dcp-scope').is_visible()
    assert page.locator('.dcp-duration').first.is_visible()
    assert '附近海面' in page.locator('.dcp-description').first.inner_text()
    page.screenshot(path=str(Path(__file__).parent/'ui-water-preview.png'))
    page.get_by_role('button',name='洪水',exact=True).click()
    assert not page.locator('.dcp-warning').is_visible()
    assert not page.locator('.dcp-scope').is_visible()
    assert '附近水域' in page.locator('.dcp-description').first.inner_text()
    assert not page.evaluate('calls.some(c=>c.name==="direction")')
    page.get_by_role('button',name='发布全城避难',exact=True).click()
    assert page.evaluate('calls.some(c=>c.name==="evacuate")')
    page.get_by_role('button',name='解除全城警报',exact=True).click()
    assert page.evaluate('calls.some(c=>c.name==="release")')
    page.get_by_role('button',name='龙卷风',exact=True).click()
    page.locator('[data-level="10"]').click()
    page.locator('[data-seconds="180"]').click()
    assert page.evaluate('state.level===10 && state.seconds===180')
    assert page.locator('.dcp-picking').is_visible()
    page.evaluate('failTarget=true')
    page.locator('#mock-map').click()
    assert page.evaluate('state.picking && mapRequests.length===0')
    assert '请选择有效位置' in page.locator('.dcp-message').inner_text()
    page.evaluate('failTarget=false')
    page.get_by_role('button',name='取消地图触发',exact=True).click()
    page.locator('#mock-map').click()
    assert page.evaluate('!state.picking && mapRequests.length===0')
    page.get_by_role('button',name='龙卷风',exact=True).click()
    page.screenshot(path=str(Path(__file__).parent/'ui-picking-preview.png'))
    page.locator('#mock-map').click()
    assert page.evaluate('!state.picking && state.pending && mapRequests.length===1 && mapRequests[0].level===10 && mapRequests[0].seconds===180')
    assert page.get_by_role('button',name='正在初始化…',exact=True).is_disabled()
    assert page.get_by_role('button',name='龙卷风',exact=True).is_disabled()
    page.locator('#mock-map').click()
    assert page.evaluate('mapRequests.length===1')
    page.evaluate('completeSpawn()')
    page.get_by_role('button',name='龙卷风',exact=True).click()
    page.keyboard.press('Escape')
    assert page.evaluate('state.open && !state.picking && mapRequests.length===1')
    page.get_by_role('button',name='在地图上触发',exact=True).click()
    page.locator('#mock-map').click()
    assert page.evaluate('mapRequests.length===2 && mapRequests[1].level===10 && mapRequests[1].seconds===180')
    page.evaluate('completeSpawn()')
    page.evaluate('state.active=[{name:"海啸",seconds:1090,custom:{kind:11,phase:1,warningSeconds:0,waveSeconds:60}}];__dcp.update(state)')
    assert '造波 60 秒' in page.locator('.dcp-active').inner_text()
    page.evaluate('state.active[0].custom.phase=2;__dcp.update(state)')
    assert '尾波传播中' in page.locator('.dcp-active').inner_text()
    page.get_by_role('button',name='停止灾害',exact=True).click()
    assert page.get_by_role('button',name='停止灾害',exact=True).is_disabled()
    page.evaluate('state.inGame=false;__dcp.update(state)')
    assert not page.locator('#dcp-root').is_visible()
    page.evaluate('state.inGame=true;state.level=5;state.seconds=60;__dcp.update(state)')
    page.add_script_tag(content=js)
    assert page.locator('#dcp-root').count()==1
    assert page.locator('#dcp-styles').count()==1
    page.screenshot(path=str(Path(__file__).parent/'ui-preview.png'))
    page.set_viewport_size({'width':960,'height':640})
    assert page.locator('.dcp-panel').bounding_box()['y']>=0
    page.get_by_role('button',name='龙卷风',exact=True).click()
    page.screenshot(path=str(Path(__file__).parent/'ui-small-preview.png'))
    page.get_by_role('button',name='关闭面板',exact=True).click()
    assert page.evaluate('!state.open && !state.picking && mapRequests.length===2')
    page.get_by_role('button',name='灾难',exact=True).click()
    page.keyboard.press('Escape')
    assert not page.locator('.dcp-panel').is_visible()
    page.evaluate('__dcp.dispose()')
    assert page.locator('#dcp-root').count()==0
    assert not errors,errors
    browser.close()
print('PASS: select + map triggering, editable/retained parameters, invalid target, cancel/Escape/close, pending/duplicate prevention, stop, menu hiding, remount, 960x640; no JS errors (simulated game input)')
