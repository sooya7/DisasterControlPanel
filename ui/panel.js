(function () {
  'use strict';
  if (!document.body) return;
  function trigger(name, value) {
    var api = window['cs2/api'];
    if (api) {
      if (value === undefined) api.trigger('dcp', name);
      else api.trigger('dcp', name, value);
    }
  }
  if (window.__dcp) { trigger('ready'); return; }
  var css = document.createElement('style');
  css.id = 'dcp-styles'; css.textContent = window.__dcpCss || '';
  document.head.appendChild(css);
  var root = document.createElement('div');
  root.id = 'dcp-root'; root.style.display = 'none';
  document.body.appendChild(root);
  var s = {}, signature = '', buttons = [];
  function element(tag, cls, text, parent) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    if (parent) parent.appendChild(e);
    return e;
  }
  function button(cls, text, parent, callback) {
    var b = element('button', cls, text, parent);
    b.type = 'button'; b.addEventListener('click', function (e) { if (!b.disabled) callback(e); });
    return b;
  }
  function disabled(b, value) {
    b.disabled = value;
    b.classList.toggle('dcp-disabled', value);
    b.setAttribute('aria-disabled', value ? 'true' : 'false');
  }
  function icon(kind) {
    var ns = 'http://www.w3.org/2000/svg';
    var svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 32 32'); svg.setAttribute('aria-hidden', 'true');
    var path = document.createElementNS(ns, 'path');
    var paths = {
      building: 'M8 28V14h7v14M18 28V6h7v22M11 17v2m0 3v2m10-14v3m0 4v3M4 28h24M7 9c0-5 7-7 6-2 6 2 3 7 0 7',
      forest: 'M8 28V18m-6 0 6-9 6 9H2m16 10V16m-6 0 6-11 6 11H12m13 11c-5-4 2-6 0-12 6 4 5 10 0 12',
      tornado: 'M4 5h24M6 9h20M8 13h16M11 17h12M15 21h7M18 25h4M21 29h3',
      hail: 'M6 17a6 6 0 0 1 0-12 7 7 0 0 1 13 0 5 5 0 0 1 6 9H6m1 7v2m7-5v2m7 0v2m-11 2v2m8-1v2',
      thunder: 'M6 15a5 5 0 0 1 0-10 7 7 0 0 1 13 0 5 5 0 0 1 6 9H6m10-3-5 9h6l-3 9 10-13h-7l3-5',
      earthquake: 'M3 9h26M3 24h8l5-8-5-5 7-8m-2 13 5 5-2 7m-7-4H3m20 0h6',
      meteor: 'M4 4l9 9M12 3l8 8M3 12l8 8M20 14a7 7 0 1 1-7 7 7 7 0 0 1 7-7m-1 5 3 3',
      sinkhole: 'M3 13l6 6 7 4 7-4 6-6M3 13l5-4 8 3 8-3 5 4m-20 6 2 7h10l2-7',
      flood: 'M3 15l8-7 8 7v9H5v-9m2 0v-5m-4 16c5-5 8 5 13 0s8 5 13 0',
      tsunami: 'M3 24c6 0 1-9 7-15 8-9 19-3 13 3-5-6-9 0-5 6 2 4 6 5 11 6M3 29h26'
    };
    path.setAttribute('d', paths[kind] || paths.hail);
    path.setAttribute('fill', 'none'); path.setAttribute('stroke', 'currentColor');
    path.setAttribute('stroke-width', '2'); path.setAttribute('stroke-linecap', 'round'); path.setAttribute('stroke-linejoin', 'round');
    svg.appendChild(path); return svg;
  }
  var launcher = button('dcp-launcher', '灾难', root, function () { trigger('toggle'); });
  launcher.title = '灾难控制面板（F9）';
  launcher.insertBefore(icon('tornado'), launcher.firstChild);
  var panel = element('section', 'dcp-panel', undefined, root);
  panel.setAttribute('aria-label', '灾难控制面板');
  var head = element('div', 'dcp-head', undefined, panel);
  element('h2', '', '灾难控制面板', head);
  var close = button('dcp-close', '×', head, function () { trigger('toggle'); });
  close.setAttribute('aria-label', '关闭面板');
  var body = element('div', 'dcp-body', undefined, panel);
  var choices = element('div', 'dcp-choices', undefined, body);
  var description = element('p', 'dcp-description', '选择灾难类型。', body);
  var strength = element('div', 'dcp-control', undefined, body);
  var strengthLabel = element('div', 'dcp-control-label', undefined, strength);
  element('label', '', '灾难强度', strengthLabel);
  var levelValue = element('strong', '', '中', strengthLabel);
  var levels = element('div', 'dcp-levels', undefined, strength);
  levels.setAttribute('role', 'group'); levels.setAttribute('aria-label', '灾难强度');
  var tiers = [[3, '小'], [6, '中'], [10, '大']], levelButtons = [];
  tiers.forEach(function (tier) {
    var b = button('dcp-level', tier[1], levels, function () { trigger('level', tier[0]); });
    b.setAttribute('data-level', String(tier[0])); levelButtons.push(b);
  });
  function tierName(level) { return level >= 9 ? '大' : level >= 5 ? '中' : '小'; }
  var ends = element('div', 'dcp-range-ends', undefined, strength);
  element('span', '', '局部', ends); element('span', '', '街区', ends); element('span', '', '大片城区', ends);
  var warningBox = element('div', 'dcp-warning dcp-duration', undefined, body);
  element('label', '', '预警时间（模拟秒）', warningBox);
  var warningRow = element('div', 'dcp-times', undefined, warningBox), warningButtons = [];
  [0, 15, 30, 60, 120].forEach(function (v) { var b = button('dcp-time', String(v), warningRow, function () { trigger('warning', v); }); b.setAttribute('data-warning', String(v)); warningButtons.push(b); });
  var scopeBox = element('div', 'dcp-scope dcp-duration', undefined, body);
  element('label', '', '警报范围（居民由游戏原生机制处理）', scopeBox);
  var scopeRow = element('div', 'dcp-times', undefined, scopeBox);
  var regional = button('dcp-time', '受灾范围', scopeRow, function () { trigger('citywide', false); });
  var citywide = button('dcp-time', '全城避难', scopeRow, function () { trigger('citywide', true); });
  element('p', 'dcp-description', '默认按受灾范围发出警报；庇护所选择、出行和疏散车辆由游戏处理。冰雹、雷击沿用原生留在室内规则。', scopeBox);
  var triggerBox = element('div', 'dcp-trigger', undefined, body);
  body.insertBefore(triggerBox, choices);
  var picking = element('div', 'dcp-picking', undefined, triggerBox);
  var pickHint = element('div', '', undefined, picking);
  var pickTitle = element('strong', '', '左键点击地图，立即触发', pickHint);
  element('p', '', '右键或 Esc 取消；参数可在下方调整。', pickHint);
  var cancel = button('dcp-secondary', '取消', picking, function () { trigger('cancel'); });
  cancel.setAttribute('aria-label', '取消地图触发');
  var spawn = button('dcp-spawn', '在地图上触发', triggerBox, function () {
    if (!spawn.disabled) { disabled(spawn, true); trigger('pick'); }
  });
  var repeatToggle = button('dcp-repeat', '连续放置：关', triggerBox, function () { trigger('repeat', !s.repeat); });
  var message = element('p', 'dcp-message', '', triggerBox); message.setAttribute('role', 'status');
  element('p', 'dcp-footnote', '灾害会真实损伤城市，建议先另存。', body);
  var evacuation = element('div', 'dcp-evacuation', undefined, body);
  var alertRow = element('div', 'dcp-times', undefined, evacuation);
  button('dcp-secondary dcp-evacuate', '发布全城避难', alertRow, function () { trigger('evacuate'); });
  button('dcp-secondary dcp-release', '解除全城警报', alertRow, function () { trigger('release'); });
  element('p', 'dcp-footnote', '避难沿用原版容量、道路与巴士规则。收容人数可在原版灾害信息页查看。', evacuation);
  var ongoing = element('div', 'dcp-ongoing', undefined, body);
  var ongoingHead = element('div', 'dcp-control-label', undefined, ongoing);
  var count = element('strong', '', '本面板触发的灾害', ongoingHead);
  var stop = button('dcp-stop', '停止灾害', ongoingHead, function () { disabled(stop, true); trigger('stop'); });
  var active = element('div', 'dcp-active', undefined, ongoing), rows = {};
  var empty = element('p', 'dcp-empty', '当前没有进行中的灾害。', active);
  element('p', 'dcp-footnote', '停止不会修复已造成的破坏。', ongoing);
  root.addEventListener('mouseenter', function () { trigger('pointer', true); });
  root.addEventListener('mouseleave', function () { trigger('pointer', false); });
  function key(e) {
    // Esc only leaves map picking; the game keeps Esc for its own menu. Close with F9 or ×.
    if (e.key === 'Escape' && s.open && s.picking) { trigger('cancel'); e.stopPropagation(); }
  }
  document.addEventListener('keydown', key);
  function update(state) {
    s = state;
    root.style.display = s.inGame ? 'block' : 'none';
    panel.style.display = s.open ? 'block' : 'none';
    launcher.setAttribute('aria-expanded', s.open ? 'true' : 'false');
    launcher.className = 'dcp-launcher' + (s.open ? ' dcp-selected' : '');
    if (!s.open) return;
    picking.style.display = s.picking ? 'flex' : 'none';
    spawn.style.display = s.picking ? 'none' : 'block';
    var types = s.types || [];
    var nextSignature = JSON.stringify(types);
    if (nextSignature !== signature) {
      signature = nextSignature; choices.textContent = ''; buttons = [];
      types.forEach(function (t) {
        var b = button('dcp-choice', '', choices, function () { trigger('select', t.id); });
        var n = t.nativeName.toLowerCase();
        b.appendChild(icon(t.kind === 'weather' ? (n.indexOf('tornado') >= 0 ? 'tornado' : n.indexOf('lightning') >= 0 || n.indexOf('thunder') >= 0 ? 'thunder' : 'hail') : t.kind));
        element('span', '', t.name, b); b.setAttribute('title', t.nativeName); buttons.push({ id: t.id, button: b });
      });
      if (!types.length) element('p', 'dcp-message', '正在读取可用灾难…', choices);
    }
    buttons.forEach(function (b) {
      var selected = b.id === s.selected;
      b.button.className = 'dcp-choice' + (selected ? ' dcp-selected' : '');
      b.button.setAttribute('aria-pressed', selected ? 'true' : 'false');
    });
    var type = types.filter(function (t) { return t.id === s.selected; })[0];
    pickTitle.textContent = type ? '点击地图触发“' + type.name + '”' : '左键点击地图，立即触发';
    spawn.textContent = s.pending ? '正在初始化…' : '在地图上触发';
    var descriptions = { building: '点燃指定建筑，由原生消防系统处理。', forest: '点燃指定野生树木，火势可向周边蔓延。', weather: '在选定位置形成原生天气事件，随后受风向影响移动。', earthquake: '一条断层从震中向两侧撕开，三轮震动逐次加宽并抬起断坎，裂口与建筑扬尘；强震可造成建筑倒塌。', meteor: '火球拖着长烟迹从高空斜落，撞击时火球闪光、冲击尘环和蘑菇状烟柱，熔融坑冷却，并留下真实地形坑。', sinkhole: '预警后地形下陷，坑内建筑倒塌。停止不会填平坑洞。', flood: '点想淹的位置，附近水域逐渐上涨，水边一步步漫进相连的低洼街区。高等级涨得更高更远，堤坝和高地挡住的地方不进水。', tsunami: '点想冲击的海岸，海水先退，远海涌起一道浪墙越推越高，冲上岸后继续向内陆卷去，随后还有一道尾浪。等级越高浪墙越宽越高、冲得越远，高地仍可幸免。' };
    description.textContent = type ? descriptions[type.kind] || type.name : '选择一种灾难，再在地图上指定发生位置。';
    levelValue.textContent = tierName(s.level);
    levelButtons.forEach(function (b) { var on = tierName(Number(b.getAttribute('data-level'))) === tierName(s.level); b.className = 'dcp-level' + (on ? ' dcp-selected' : ''); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
    repeatToggle.textContent = '连续放置：' + (s.repeat ? '开' : '关');
    repeatToggle.className = 'dcp-repeat' + (s.repeat ? ' dcp-selected' : '');
    var timed = type && type.kind !== 'building' && type.kind !== 'forest';
    var custom = type && ['earthquake', 'meteor', 'sinkhole'].indexOf(type.kind) >= 0;
    var waterDisaster = type && (type.kind === 'flood' || type.kind === 'tsunami');
    warningBox.style.display = custom || (type && type.kind === 'tsunami') ? 'block' : 'none';
    scopeBox.style.display = timed && !waterDisaster ? 'block' : 'none';
    warningButtons.forEach(function (b) { b.className = 'dcp-time' + (Number(b.getAttribute('data-warning')) === s.warning ? ' dcp-selected' : ''); });
    regional.className = 'dcp-time' + (!s.citywide ? ' dcp-selected' : ''); citywide.className = 'dcp-time' + (s.citywide ? ' dcp-selected' : '');
    message.textContent = s.message || '';
    message.style.display = s.message ? 'block' : 'none';
    disabled(spawn, !type || s.pending);
    buttons.forEach(function (b) { disabled(b.button, s.pending); });
    var events = s.active || [];
    count.textContent = '本面板触发的灾害（' + events.length + '）';
    disabled(stop, !events.length && !s.pending);
    var seen = {};
    empty.style.display = events.length ? 'none' : 'block';
    events.forEach(function (event) {
      var id = event.id + ':' + event.version; seen[id] = true;
      var row = rows[id];
      if (!row) {
        var el = element('div', 'dcp-event', undefined, active);
        row = rows[id] = { el: el, name: element('span', 'dcp-event-name', '', el), status: element('span', 'dcp-event-status', '', el) };
        var tools = element('span', 'dcp-event-tools', undefined, el);
        button('dcp-mini', '定位', tools, function () { trigger('focus', id); });
        button('dcp-mini', '停止', tools, function () { trigger('stopOne', id); });
      }
      var name = event.name + (event.level ? ' · ' + tierName(event.level) : '');
      var status = event.custom && event.custom.warningSeconds > 0 ? '预警 ' + event.custom.warningSeconds + ' 秒' : event.custom && event.custom.kind === 11 ? (event.custom.phase === 2 ? '余波退去中' : '浪墙 ' + event.custom.waveSeconds + ' 秒') : event.seconds >= 0 ? event.seconds + ' 秒' : '消防处理中';
      if (row.name.textContent !== name) row.name.textContent = name;
      if (row.status.textContent !== status) row.status.textContent = status;
    });
    Object.keys(rows).forEach(function (id) { if (!seen[id]) { rows[id].el.remove(); delete rows[id]; } });
  }
  window.__dcp = {
    update: update,
    dispose: function () {
      document.removeEventListener('keydown', key); root.remove(); css.remove(); delete window.__dcp;
    }
  };
  trigger('ready');
}());
