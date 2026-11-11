// Kovu's Pride · Publicación: adaptador común para los portales web. Lo copia el menú de publicación en el
// WebGL Template de cada plataforma junto a su SDK. Unity habla con window.KPPlatform y no conoce ningún SDK.
//   window.KP_STORE: 'crazygames' | 'playgama' | 'gamedistribution' | 'itch' (lo define el index.html)
//   window.KP_STORAGE_KEYS: claves de guardado que se leen antes de arrancar (Playgama guarda por su SDK)
//   preload()                  Promise: la plantilla la espera ANTES de cargar Unity (Playgama: iniciar el SDK y
//                              leer el guardado). Nunca falla: a los 8 s se resuelve aunque el SDK no responda.
//   init(emit)                 emit(tipo, datos): 1 listo · 2 fin de anuncio · 3 pausa · 4 silencio · 5 empieza anuncio
//   message(name)              loading_stopped · game_ready · gameplay_started · gameplay_stopped · happytime
//   showAd(id, type, placement) type: 'interstitial' | 'rewarded'. Siempre acaba con emit(2, {id, ok})
//   storageGet(key) / storageSet(key, value)   guardado de la plataforma (solo Playgama; null si no hay)
//   setScore(board, score)     clasificación de la plataforma, si la tiene
//   call(id, method, argsJson) llamada asíncrona: responde con emit(6, {id, ok, data}) — data es JSON en texto.
//                              Métodos (Playgama): lb.entries {board} · mp.quick {max, filter} · mp.create {max}
//                              mp.join {room} · mp.findJoin {filter} · mp.close · mp.leave · mp.send {type, data, to}
//                              (CrazyGames) room.update {roomId, isJoinable, inviteParams} · room.left · room.inviteLink {params}
//                              · user.auth (su aviso de inicio de sesión; responde {user}). También Playgama (player.authorize)
//   Eventos de la plataforma: emit(7, {name, data}) — mp.joined, mp.left, mp.message, mp.roomLeft, mp.host, room.join
// Si un SDK falla o no responde, el juego sigue: «listo» sin anuncios a los 8 s y anuncios que caducan a los 60 s.
(function () {
  'use strict';
  var store = window.KP_STORE || 'none';
  var emit = function () {};
  var ready = false;
  var current = null;   // { id, rewarded, earned, timer, seen }
  var info0 = {};       // lo que se sabe del SDK tras preload (idioma, audio)
  var cache = {};       // guardado leído en preload
  var state = { pause: false, mute: false };   // lo último que se le ha dicho a Unity

  // ---------------- Audio del navegador ----------------
  // Unity crea su AudioContext al arrancar. Un anuncio a pantalla completa, cambiar de pestaña o una interrupción del
  // móvil pueden dejarlo «suspended» o «interrupted», y Unity no siempre lo reanuda: el juego se queda sin sonido para
  // el resto de la sesión (jugadores de Playgama, 11/11/2026). Este archivo se carga antes que Unity, así que se
  // apuntan los AudioContext que se crean y se reanudan al acabar un anuncio, al volver a la pestaña y con cada toque o
  // tecla (algunos navegadores solo dejan reanudar tras un gesto). Nunca mientras hay anuncio, pausa o silencio.
  var audioContexts = [];
  (function () {
    var AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    try {
      var Tracked = function () {
        var ctx = new (Function.prototype.bind.apply(AC, [null].concat([].slice.call(arguments))))();
        audioContexts.push(ctx);
        return ctx;
      };
      Tracked.prototype = AC.prototype;
      if (window.AudioContext) window.AudioContext = Tracked;
      if (window.webkitAudioContext) window.webkitAudioContext = Tracked;
    } catch (e) { }
  })();
  function resumeAudio() {
    if (current || state.pause || state.mute) return;
    for (var i = 0; i < audioContexts.length; i++) {
      var c = audioContexts[i];
      try {
        if (c.state !== 'running' && c.state !== 'closed') { var p = c.resume(); if (p && p.catch) p.catch(function () { }); }
      } catch (e) { }
    }
  }
  ['pointerdown', 'touchend', 'mousedown', 'keydown'].forEach(function (t) { window.addEventListener(t, resumeAudio, true); });
  document.addEventListener('visibilitychange', function () { if (!document.hidden) setTimeout(resumeAudio, 50); });

  // Pausa y silencio pedidos por la plataforma: solo se avisa a Unity cuando cambian
  function setPause(on) {
    on = !!on;
    if (on === state.pause) return;
    state.pause = on;
    emit(3, { on: on ? 1 : 0 });
    if (!on) setTimeout(resumeAudio, 0);
  }
  function setMute(on) {
    on = !!on;
    if (on === state.mute) return;
    state.mute = on;
    emit(4, { on: on ? 1 : 0 });
    if (!on) setTimeout(resumeAudio, 0);
  }

  function sendReady(info) {
    if (ready) return;
    ready = true;
    info = info || {};
    emit(1, { store: store, ads: info.ads ? 1 : 0, rewarded: info.rewarded ? 1 : 0, user: info.user || '',
              lang: info.lang || info0.lang || '', lb: info.lb || '', mp: info.mp ? 1 : 0, pid: info.pid || '',
              instant: info.instant ? 1 : 0, invite: info.invite || '', acct: info.acct ? 1 : 0, guest: info.guest ? 1 : 0 });
  }

  function reply(id, ok, data) {
    var text = '';
    try { text = data === undefined || data === null ? '' : (typeof data === 'string' ? data : JSON.stringify(data)); } catch (e) { }
    emit(6, { id: id, ok: ok ? 1 : 0, data: text });
  }
  function platformEvent(name, data) {
    var text = '';
    try { text = data === undefined || data === null ? '' : JSON.stringify(data); } catch (e) { }
    emit(7, { name: name, data: text });
  }

  function adStarted() { if (current) emit(5, { id: current.id }); }

  function adFinished(ok) {
    if (!current) return;
    var c = current; current = null;
    clearTimeout(c.timer);
    emit(2, { id: c.id, ok: ok ? 1 : 0 });
    setTimeout(resumeAudio, 0);
  }

  function beginAd(id, type, noStartMs) {
    if (current) { emit(2, { id: id, ok: 0 }); return false; }
    current = { id: id, rewarded: type === 'rewarded', earned: false, seen: false };
    current.timer = setTimeout(function () { adFinished(false); }, type === 'rewarded' ? 60000 : 40000);
    // Si el SDK decide no enseñar nada (demasiado pronto, sin inventario) puede que no avise: no se espera
    if (noStartMs) {
      var c = current;
      setTimeout(function () { if (current === c && !c.seen) adFinished(false); }, noStartMs);
    }
    return true;
  }

  // Dos fotogramas pintados después: el primer fotograma jugable ya está en pantalla
  function afterPaint(fn) {
    var raf = window.requestAnimationFrame || function (f) { return setTimeout(f, 16); };
    raf(function () { raf(fn); });
  }

  // ---------------- CrazyGames (HTML5 SDK v3) ----------------
  var cg = {
    sdk: function () { return window.CrazyGames && window.CrazyGames.SDK; },
    init: function () {
      var sdk = cg.sdk();
      if (!sdk) { sendReady(); return; }
      sdk.init().then(function () {
        // Anuncios solo en CrazyGames (y en local con su SDK); en Basic Launch el propio SDK responde adsDisabled
        var ads = sdk.environment === 'crazygames' || sdk.environment === 'local';
        try {
          if (sdk.game && sdk.game.settings && sdk.game.settings.muteAudio) setMute(true);
          if (sdk.game && sdk.game.addSettingsChangeListener)
            sdk.game.addSettingsChangeListener(function (s) { setMute(s && s.muteAudio); });
        } catch (e) { }
        // Multijugador de CrazyGames: el juego usa su propio servidor, y el SDK solo sabe en qué sala está cada uno
        // (amigos, invitaciones y la entrada directa desde su página de multijugador)
        var g = sdk.game || {};
        var instant = false, invite = '';
        try { instant = !!g.isInstantMultiplayer; } catch (e) { }
        try { invite = g.inviteParams ? JSON.stringify(g.inviteParams) : ''; } catch (e) { }
        try { if (g.addJoinRoomListener) g.addJoinRoomListener(function (params) { platformEvent('room.join', params || {}); }); } catch (e) { }
        var acct = false;
        try { acct = !!(sdk.user && sdk.user.isUserAccountAvailable); } catch (e) { }
        var finish = function (user) { sendReady({ ads: ads, rewarded: ads, user: user && user.username, instant: instant, invite: invite, acct: acct, guest: !user }); };
        try {
          if (sdk.user && sdk.user.isUserAccountAvailable) sdk.user.getUser().then(finish).catch(function () { finish(null); });
          else finish(null);
        } catch (e) { finish(null); }
      }).catch(function (e) { console.warn('[KPPlatform] CrazyGames init', e); sendReady(); });
    },
    message: function (name) {
      var sdk = cg.sdk(); if (!sdk || !sdk.game) return;
      if (name === 'gameplay_started') sdk.game.gameplayStart();
      else if (name === 'gameplay_stopped') sdk.game.gameplayStop();
      else if (name === 'loading_stopped' && sdk.game.loadingStop) sdk.game.loadingStop();
      else if (name === 'happytime' && sdk.game.happytime) sdk.game.happytime();
    },
    call: function (id, method, args) {
      var sdk = cg.sdk(), g = sdk && sdk.game;
      // Cuenta de CrazyGames: su propio aviso de inicio de sesión (el juego no puede usar cuentas externas)
      if (method === 'user.auth') {
        var u = sdk && sdk.user;
        if (!u || !u.isUserAccountAvailable) { reply(id, false, { error: 'not_available' }); return; }
        try {
          u.showAuthPrompt().then(function (user) { reply(id, !!user, { user: (user && user.username) || '' }); })
            .catch(function (e) { reply(id, false, { error: String((e && (e.code || e.message)) || e) }); });
        } catch (e) { reply(id, false, { error: String((e && e.message) || e) }); }
        return;
      }
      if (!g) { reply(id, false, { error: 'not_ready' }); return; }
      try {
        switch (method) {
          case 'room.update':
            g.updateRoom({ roomId: String(args.roomId || ''), isJoinable: !!args.isJoinable, inviteParams: args.inviteParams || {} });
            reply(id, true, {});
            return;
          case 'room.left':
            if (g.leftRoom) g.leftRoom();
            reply(id, true, {});
            return;
          case 'room.inviteLink':
            reply(id, true, { link: g.inviteLink(args.params || {}) || '' });
            return;
        }
      } catch (e) { reply(id, false, { error: String(e && e.message || e) }); return; }
      reply(id, false, { error: 'unknown_method' });
    },
    showAd: function (id, type) {
      var sdk = cg.sdk();
      if (!sdk || !beginAd(id, type)) { if (!sdk) emit(2, { id: id, ok: 0 }); return; }
      sdk.ad.requestAd(type === 'rewarded' ? 'rewarded' : 'midgame', {
        adStarted: adStarted,
        adFinished: function () { adFinished(true); },
        adError: function (e) { console.log('[KPPlatform] CrazyGames ad', e && e.code); adFinished(false); },
      });
    },
  };

  // ---------------- Playgama Bridge (JS) ----------------
  // Requisitos de Playgama que se cumplen aquí: el SDK se inicia antes que el juego (preload), el guardado va por
  // bridge.storage, se lee platform.language, se respeta isAudioEnabled y los eventos de pausa y audio, game_ready
  // se envía con el primer fotograma jugable y el intervalo entre intersticiales lo decide el SDK.
  var pg = {
    ok: false,
    preload: function () {
      var b = window.bridge;
      if (!b) return Promise.resolve();
      return b.initialize().then(function () {
        pg.ok = true;
        try { info0.lang = b.platform.language || ''; } catch (e) { }
        try { info0.audio = b.platform.isAudioEnabled !== false; } catch (e) { info0.audio = true; }
        var keys = window.KP_STORAGE_KEYS || [];
        if (!keys.length) return;
        return b.storage.get(keys).then(function (data) {
          for (var i = 0; i < keys.length; i++) {
            var v = data && data[i];
            if (v !== null && v !== undefined) cache[keys[i]] = typeof v === 'string' ? v : JSON.stringify(v);
          }
        }).catch(function (e) { console.warn('[KPPlatform] Playgama storage.get', e); });
      }).catch(function (e) { console.warn('[KPPlatform] Playgama init', e); });
    },
    init: function () {
      var b = window.bridge;
      if (!b || !pg.ok) { sendReady(); return; }
      var E = b.EVENT_NAME || {};
      try {
        if (E.INTERSTITIAL_STATE_CHANGED) b.advertisement.on(E.INTERSTITIAL_STATE_CHANGED, function (s) {
          if (!current || current.rewarded) return;
          current.seen = true;
          if (s === 'opened') { current.opened = true; adStarted(); }
          else if (s === 'closed') adFinished(true);
          else if (s === 'failed') adFinished(false);
        });
        if (E.REWARDED_STATE_CHANGED) b.advertisement.on(E.REWARDED_STATE_CHANGED, function (s) {
          if (!current || !current.rewarded) return;
          current.seen = true;
          if (s === 'opened') { current.opened = true; adStarted(); }
          else if (s === 'rewarded') current.earned = true;   // la recompensa solo con «rewarded»
          else if (s === 'closed') adFinished(current.earned);
          else if (s === 'failed') adFinished(false);
        });
        if (E.PAUSE_STATE_CHANGED) b.platform.on(E.PAUSE_STATE_CHANGED, function (p) {
          if (p) pg.pauseFromAd = !!current || pg.adOpen(b);
          setPause(p);
        });
        if (E.AUDIO_STATE_CHANGED) b.platform.on(E.AUDIO_STATE_CHANGED, function (on) { setMute(on === false); });
      } catch (e) { console.warn('[KPPlatform] Playgama listeners', e); }
      var a = b.advertisement || {};
      var lbType = ''; try { lbType = (b.leaderboards && b.leaderboards.type) || ''; } catch (e) { }
      var mpOk = false; try { mpOk = !!(window.KP_OPTIONS && window.KP_OPTIONS.multiplayer && b.multiplayer && b.multiplayer.isSupported); } catch (e) { }
      if (mpOk) pg.listenRooms(b);
      // Cuenta de Playgama: el ranking de Playgama muestra su nombre (invitado o con cuenta), no uno del juego
      var auth = false, guest = true;
      try { auth = !!b.player.isAuthorizationSupported; } catch (e) { }
      try { guest = b.player.isGuest !== undefined ? !!b.player.isGuest : !b.player.isAuthorized; } catch (e) { }
      sendReady({ ads: a.isInterstitialSupported, rewarded: a.isRewardedSupported, user: b.player && b.player.name,
                  lb: lbType, mp: mpOk, pid: (b.player && b.player.id) || '', acct: auth, guest: guest });
      // El estado se lee en vivo, no el de preload: si al arrancar había un anuncio (o el audio estaba apagado) y el
      // aviso de «ya está» llegó antes de registrar los listeners, el juego se quedaba en silencio para siempre.
      if (pg.adOpen(b)) { pg.pauseFromAd = true; setPause(true); }
      pg.sync();
      setInterval(pg.sync, 1500);
    },
    adOpen: function (b) {
      try { var a = b.advertisement || {}; return a.interstitialState === 'opened' || a.rewardedState === 'opened'; } catch (e) { return false; }
    },
    // Red de seguridad cada 1,5 s: el audio de la plataforma, un anuncio que ya se cerró sin avisar con «closed» (en la
    // telemetría salían más «opened» que «closed») y una pausa de anuncio que no se levantó.
    sync: function () {
      var b = window.bridge; if (!b || !pg.ok) return;
      try { if (b.platform.isAudioEnabled === true || b.platform.isAudioEnabled === false) setMute(!b.platform.isAudioEnabled); } catch (e) { }
      if (pg.adOpen(b)) return;
      if (current && current.opened) {
        var st = ''; try { st = current.rewarded ? b.advertisement.rewardedState : b.advertisement.interstitialState; } catch (e) { }
        if (st === 'closed' || st === 'failed') adFinished(current.rewarded ? current.earned : true);
      }
      if (state.pause && pg.pauseFromAd && !current && !document.hidden) { pg.pauseFromAd = false; setPause(false); }
    },
    message: function (name) {
      var b = window.bridge; if (!b || !b.platform || !pg.ok) return;
      if (name === 'game_ready') { afterPaint(function () { try { b.platform.sendMessage('game_ready'); } catch (e) { } }); return; }
      var map = { happytime: 'player_got_achievement' };
      if (map[name]) try { b.platform.sendMessage(map[name]); } catch (e) { }
    },
    showAd: function (id, type, placement) {
      var b = window.bridge;
      if (!b || !pg.ok) { emit(2, { id: id, ok: 0 }); return; }
      if (!beginAd(id, type, type === 'rewarded' ? 4000 : 1500)) return;
      try {
        if (type === 'rewarded') b.advertisement.showRewarded(placement || undefined);
        else b.advertisement.showInterstitial(placement || undefined);
      } catch (e) { adFinished(false); }
    },
    storageGet: function (key) { return Object.prototype.hasOwnProperty.call(cache, key) ? cache[key] : null; },
    storageSet: function (key, value) {
      cache[key] = value;
      var b = window.bridge; if (!b || !pg.ok) return;
      try { b.storage.set([key], [value]).catch(function (e) { console.warn('[KPPlatform] storage.set', e); }); }
      catch (e) { console.warn('[KPPlatform] storage.set', e); }
    },
    // Salas de multijugador: los eventos llegan al juego como emit(7, …)
    listenRooms: function (b) {
      var E = b.EVENT_NAME || {}, m = b.multiplayer;
      var who = function (p) { return p ? { id: p.id, name: p.name || '', host: !!p.isHost } : null; };
      try {
        if (E.MULTIPLAYER_PLAYER_JOINED) m.on(E.MULTIPLAYER_PLAYER_JOINED, function (p) { platformEvent('mp.joined', who(p)); });
        if (E.MULTIPLAYER_PLAYER_LEFT) m.on(E.MULTIPLAYER_PLAYER_LEFT, function (p) { platformEvent('mp.left', who(p)); });
        if (E.MULTIPLAYER_HOST_CHANGED) m.on(E.MULTIPLAYER_HOST_CHANGED, function (p) { platformEvent('mp.host', who(p)); });
        if (E.MULTIPLAYER_ROOM_LEFT) m.on(E.MULTIPLAYER_ROOM_LEFT, function (r) { platformEvent('mp.roomLeft', { reason: String(r || '') }); });
        if (E.MULTIPLAYER_MESSAGE_RECEIVED) m.on(E.MULTIPLAYER_MESSAGE_RECEIVED, function (msg) {
          platformEvent('mp.message', { type: msg && msg.type, from: msg && msg.from, data: msg && msg.data !== undefined ? JSON.stringify(msg.data) : '' });
        });
      } catch (e) { console.warn('[KPPlatform] multiplayer listeners', e); }
    },
    room: function (b) {
      var m = b.multiplayer, list = [];
      try { (m.players || []).forEach(function (p) { list.push({ id: p.id, name: p.name || '', host: !!p.isHost }); }); } catch (e) { }
      return { room: m.roomId || '', host: !!m.isHost, me: m.playerId || '', players: list };
    },
    call: function (id, method, args) {
      var b = window.bridge;
      if (!b || !pg.ok) { reply(id, false, { error: 'not_ready' }); return; }
      var fail = function (e) { reply(id, false, { error: (e && (e.code || e.message)) || String(e || 'error') }); };
      var m = b.multiplayer;
      switch (method) {
        case 'user.auth':
          // Inicio de sesión de Playgama (solo con un toque del jugador); responde el nombre de la cuenta
          if (!b.player || !b.player.isAuthorizationSupported) { reply(id, false, { error: 'not_available' }); return; }
          b.player.authorize({}).then(function () { reply(id, true, { user: b.player.name || '' }); }).catch(fail);
          return;
        case 'lb.entries':
          if (!b.leaderboards || b.leaderboards.type !== 'in_game') { reply(id, false, { error: 'not_available' }); return; }
          b.leaderboards.getEntries(args.board).then(function (entries) {
            var out = [];
            (entries || []).forEach(function (e) { out.push({ id: String(e.id || ''), name: e.name || '', score: Number(e.score) || 0, rank: Number(e.rank) || 0 }); });
            reply(id, true, { entries: out, me: (b.player && b.player.id) || '' });
          }).catch(fail);
          return;
        case 'mp.quick':
          m.joinOrCreateRoom({ maxPlayers: args.max || 2, filter: args.filter || {} }).then(function () { reply(id, true, pg.room(b)); }).catch(fail);
          return;
        case 'mp.create':
          m.createRoom({ maxPlayers: args.max || 2, isVisible: !!args.visible, filter: args.filter || undefined })
            .then(function () { reply(id, true, pg.room(b)); }).catch(fail);
          return;
        case 'mp.findJoin':
          // Sala visible con este filtro exacto (p. ej. el código corto de un duelo privado): se entra en la primera
          m.getRooms({ filter: args.filter || {}, limit: 5 }).then(function (rooms) {
            if (!rooms || !rooms.length) { reply(id, false, { error: 'MULTIPLAYER_ROOM_NOT_FOUND' }); return; }
            return m.joinRoom(rooms[0].id).then(function () { reply(id, true, pg.room(b)); });
          }).catch(fail);
          return;
        case 'mp.join':
          m.joinRoom(String(args.room || '').toUpperCase()).then(function () { reply(id, true, pg.room(b)); }).catch(fail);
          return;
        case 'mp.leave':
          try { m.leaveRoom(); } catch (e) { }
          reply(id, true, {});
          return;
        case 'mp.close':
          try { m.setRoomOpen(false); } catch (e) { }
          reply(id, true, {});
          return;
        case 'mp.send':
          try { m.send(args.type, args.data || {}, args.to ? { to: args.to } : undefined); reply(id, true, {}); } catch (e) { fail(e); }
          return;
      }
      reply(id, false, { error: 'unknown_method' });
    },
    setScore: function (board, score) {
      var b = window.bridge; if (!b || !pg.ok || !b.leaderboards) return;
      try {
        if (b.leaderboards.type === 'not_available') return;
        b.leaderboards.setScore(board, score).catch(function () { });
      } catch (e) { }
    },
  };

  // ---------------- GameDistribution (HTML5 SDK) ----------------
  // El index.html define GD_OPTIONS con onEvent → window.__kpGdEvent (los eventos que llegan antes se guardan)
  var gd = {
    init: function () {
      var queued = window.__kpGdQueue || [];
      window.__kpGdEvent = gd.onEvent;
      queued.forEach(gd.onEvent);
      setTimeout(function () { sendReady({ ads: !!window.gdsdk, rewarded: !!window.gdsdk }); }, 8000);
    },
    onEvent: function (ev) {
      var name = ev && ev.name;
      if (name === 'SDK_READY') sendReady({ ads: true, rewarded: true });
      else if (name === 'SDK_GAME_PAUSE') adStarted();
      else if (name === 'SDK_REWARDED_WATCH_COMPLETE') { if (current) current.earned = true; }
      else if (name === 'SDK_GAME_START') { if (current) adFinished(current.rewarded ? current.earned : true); }
    },
    message: function () { },
    showAd: function (id, type) {
      var sdk = window.gdsdk;
      if (!sdk || typeof sdk.showAd !== 'function' || !beginAd(id, type)) { if (!sdk) emit(2, { id: id, ok: 0 }); return; }
      try {
        var p = type === 'rewarded' ? sdk.showAd('rewarded') : sdk.showAd();
        if (p && typeof p.catch === 'function') p.catch(function () { adFinished(false); });
      } catch (e) { adFinished(false); }
    },
  };

  // ---------------- itch.io: sin SDK ni anuncios ----------------
  var none = { init: function () { sendReady(); }, message: function () { }, showAd: function (id) { emit(2, { id: id, ok: 0 }); } };

  var impl = store === 'crazygames' ? cg : store === 'playgama' ? pg : store === 'gamedistribution' ? gd : none;

  window.KPPlatform = {
    store: store,
    preload: function () {
      var p = impl.preload ? impl.preload() : null;
      if (!p) return Promise.resolve();
      var timeout = new Promise(function (r) { setTimeout(r, 8000); });   // nunca se bloquea la carga del juego
      return Promise.race([p.catch(function () { }), timeout]);
    },
    init: function (e) {
      emit = e || emit;
      setTimeout(function () { sendReady(); }, 8000);   // si el SDK no responde, se juega sin anuncios
      try { impl.init(); } catch (err) { console.warn('[KPPlatform] init', err); sendReady(); }
    },
    message: function (name) { try { impl.message(name); } catch (err) { } },
    showAd: function (id, type, placement) {
      try { impl.showAd(id, type, placement); } catch (err) { adFinished(false); emit(2, { id: id, ok: 0 }); }
    },
    storageGet: function (key) { try { return impl.storageGet ? impl.storageGet(key) : null; } catch (err) { return null; } },
    storageSet: function (key, value) { try { if (impl.storageSet) impl.storageSet(key, value); } catch (err) { } },
    setScore: function (board, score) { try { if (impl.setScore) impl.setScore(board, score); } catch (err) { } },
    call: function (id, method, argsJson) {
      var args = {};
      try { args = argsJson ? JSON.parse(argsJson) : {}; } catch (err) { }
      if (!impl.call) { reply(id, false, { error: 'not_supported' }); return; }
      try { impl.call(id, method, args); } catch (err) { reply(id, false, { error: String(err && err.message || err) }); }
    },
  };
})();
