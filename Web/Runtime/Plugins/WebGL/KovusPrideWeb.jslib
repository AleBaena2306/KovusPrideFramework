// Kovu's Pride (Web, copiado de MASSBOUND): puente entre el juego y el navegador.
//  - Firebase con el SDK de JavaScript (Auth anónima + Firestore): el proyecto lo indica la configuración que pasa el juego.
//  - Anuncios bonificados con Google H5 Games Ads (AdSense).
//  - Salir del juego: vuelve a la página del juego en el portfolio.
// El estado se guarda en window.__kpWeb. Ningún error de aquí puede romper el juego: todo responde con ok=0.
var KovusPrideWebLib = {

  // ===================== Firebase =====================

  // cb(ok, uid)
  KP_FbInit: function (configPtr, cb) {
    var W = window.__kpWeb = window.__kpWeb || {};
    var VERSION = '10.14.1';
    var base = 'https://www.gstatic.com/firebasejs/' + VERSION + '/';

    function done(ok, uid) {
      var s = uid || '';
      var n = lengthBytesUTF8(s) + 1, p = _malloc(n);
      stringToUTF8(s, p, n);
      {{{ makeDynCall('vii', 'cb') }}}(ok ? 1 : 0, p);
      _free(p);
    }

    function load(src) {
      return new Promise(function (resolve, reject) {
        var s = document.createElement('script');
        s.src = src;
        s.async = true;
        s.onload = resolve;
        s.onerror = function () { reject(new Error('No se pudo cargar ' + src)); };
        document.head.appendChild(s);
      });
    }

    var config;
    try { config = JSON.parse(UTF8ToString(configPtr)); }
    catch (e) { console.warn('[kpFirebase] Configuración no válida', e); done(false, ''); return; }

    var sdk = (window.firebase && window.firebase.firestore && window.firebase.auth)
      ? Promise.resolve()
      : load(base + 'firebase-app-compat.js')
          .then(function () { return load(base + 'firebase-auth-compat.js'); })
          .then(function () { return load(base + 'firebase-firestore-compat.js'); });

    sdk.then(function () {
      var fb = window.firebase;
      var app = fb.apps.length ? fb.app() : fb.initializeApp(config);
      W.auth = fb.auth(app);
      W.db = fb.firestore(app);
      W.fieldValue = fb.firestore.FieldValue;
      // Usuario anónimo guardado de otra visita, o uno nuevo
      return new Promise(function (resolve) {
        var off = W.auth.onAuthStateChanged(function (user) { off(); resolve(user); });
      });
    }).then(function (user) {
      return user ? user : W.auth.signInAnonymously().then(function (c) { return c.user; });
    }).then(function (user) {
      done(true, user.uid);
    }).catch(function (e) {
      console.warn('[kpFirebase] No se pudo iniciar Firebase', e);
      done(false, '');
    });
  },

  // cb(id, ok, resultJson). Operaciones: getPlayer, registerUsername, deleteAccount, submitScore, top10, localEntry
  KP_FbCall: function (id, opPtr, argsPtr, cb) {
    var W = window.__kpWeb = window.__kpWeb || {};
    var op = UTF8ToString(opPtr);
    var args = {};
    try { args = JSON.parse(UTF8ToString(argsPtr) || '{}'); } catch (e) { }

    function reply(ok, obj) {
      var s = JSON.stringify(obj == null ? {} : obj);
      var n = lengthBytesUTF8(s) + 1, p = _malloc(n);
      stringToUTF8(s, p, n);
      {{{ makeDynCall('viii', 'cb') }}}(id, ok ? 1 : 0, p);
      _free(p);
    }

    var db = W.db, auth = W.auth;
    var user = auth && auth.currentUser;
    if (!db || !user) { reply(false, { error: 'not-ready' }); return; }
    var uid = user.uid;
    var players = db.collection('players'), usernames = db.collection('usernames'), board = db.collection('leaderboard');

    var ops = {
      getPlayer: function () {
        return players.doc(uid).get().then(function (s) {
          return s.exists
            ? { exists: true, username: s.get('username') || '', publicId: s.get('publicId') || '' }
            : { exists: false };
        });
      },

      // Igual que en Android: reserva usernames/{nombre} y actualiza players/{uid} en una transacción
      registerUsername: function (a) {
        var uRef = usernames.doc(a.usernameLower), pRef = players.doc(uid);
        return db.runTransaction(function (t) {
          return Promise.all([t.get(uRef), t.get(pRef)]).then(function (r) {
            var us = r[0], ps = r[1];
            var old = ps.exists ? (ps.get('usernameLower') || null) : null;
            if (us.exists && us.id !== old) return { reserved: false };
            if (ps.exists && old && old !== a.usernameLower) t.delete(usernames.doc(old));
            t.set(uRef, { uid: uid, username: a.username });
            t.set(pRef, { uid: uid, username: a.username, usernameLower: a.usernameLower, publicId: a.publicId }, { merge: true });
            return { reserved: true };
          });
        });
      },

      // Igual que en Android: borra leaderboard, players y usernames, el usuario anónimo, y crea uno nuevo
      deleteAccount: function () {
        var pRef = players.doc(uid);
        return pRef.get().then(function (ps) {
          var lower = ps.exists ? (ps.get('usernameLower') || null) : null;
          var b = db.batch();
          b.delete(board.doc(uid));
          b.delete(pRef);
          if (lower) b.delete(usernames.doc(lower));
          return b.commit();
        }).then(function () {
          return user.delete();
        }).then(function () {
          return auth.signInAnonymously();
        }).then(function (c) {
          return { uid: c.user.uid };
        });
      },

      // Solo se guarda si supera el récord del jugador
      submitScore: function (a) {
        var ref = board.doc(uid);
        return ref.get().then(function (s) {
          if (s.exists && (s.get('score') || 0) >= a.score) return { updated: false };
          return ref.set({ username: a.username, score: a.score, updatedAt: W.fieldValue.serverTimestamp() })
            .then(function () { return { updated: true }; });
        });
      },

      top10: function () {
        return board.orderBy('score', 'desc').limit(10).get().then(function (q) {
          var entries = [];
          q.forEach(function (d) { entries.push({ username: d.get('username') || '', score: d.get('score') || 0 }); });
          return { entries: entries };
        });
      },

      localEntry: function () {
        return board.doc(uid).get().then(function (s) {
          if (!s.exists) return { exists: false };
          var score = s.get('score') || 0, username = s.get('username') || '';
          return board.where('score', '>', score).get().then(function (q) {
            return { exists: true, username: username, score: score, above: q.size };
          });
        });
      }
    };

    var fn = ops[op];
    if (!fn) { reply(false, { error: 'unknown-op ' + op }); return; }
    Promise.resolve().then(function () { return fn(args); })
      .then(function (r) { reply(true, r); })
      .catch(function (e) {
        console.warn('[kpFirebase] ' + op, e);
        reply(false, { error: String((e && e.message) || e) });
      });
  },

  // ===================== Anuncios (H5 Games Ads) =====================

  // cb(ready): 1 cuando el SDK de anuncios está listo, 0 si no se pudo cargar
  KP_AdsInit: function (clientPtr, test, cb) {
    // La página del portfolio manda: así el ID de AdSense vive en un solo sitio (build.js)
    var client = window.KP_ADSENSE_CLIENT || UTF8ToString(clientPtr);
    var notified = false;
    function done(ok) {
      if (notified) return;
      notified = true;
      {{{ makeDynCall('vi', 'cb') }}}(ok ? 1 : 0);
    }
    try {
      window.adsbygoogle = window.adsbygoogle || [];
      if (typeof window.adBreak !== 'function') {
        window.adBreak = window.adConfig = function (o) { window.adsbygoogle.push(o); };
      }
      if (!document.querySelector('script[data-kp-ads]')) {
        var s = document.createElement('script');
        s.async = true;
        s.setAttribute('data-kp-ads', '1');
        s.setAttribute('data-ad-client', client);
        s.setAttribute('data-ad-frequency-hint', '30s');
        if (test) s.setAttribute('data-adbreak-test', 'on');
        s.crossOrigin = 'anonymous';
        s.src = 'https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=' + encodeURIComponent(client);
        s.onerror = function () { console.warn('[kpAds] No se pudo cargar AdSense (¿bloqueador de anuncios?)'); done(false); };
        document.head.appendChild(s);
      }
      window.adConfig({ preloadAdBreaks: 'on', sound: 'on', onReady: function () { done(true); } });
    } catch (e) {
      console.warn('[kpAds]', e);
      done(false);
    }
  },

  // cb(result): 1 = vídeo visto (premio), 0 = cerrado antes de tiempo, -1 = no había anuncio
  KP_AdsShowRewarded: function (namePtr, cb) {
    var name = UTF8ToString(namePtr);
    var viewed = false, shown = false, finished = false;
    function done(r) {
      if (finished) return;
      finished = true;
      {{{ makeDynCall('vi', 'cb') }}}(r);
    }
    try {
      window.adBreak({
        type: 'reward',
        name: name,
        beforeAd: function () { shown = true; },
        afterAd: function () { },
        beforeReward: function (showAdFn) { showAdFn(); },
        adDismissed: function () { viewed = false; },
        adViewed: function () { viewed = true; },
        adBreakDone: function () { done(viewed ? 1 : (shown ? 0 : -1)); }
      });
    } catch (e) {
      console.warn('[kpAds]', e);
      done(-1);
    }
  },

  // ===================== Salir =====================

  // La página puede decidir qué hacer definiendo window.kpExit(); si no, se vuelve a la ficha del juego
  KP_ExitGame: function (fallbackPtr) {
    var fallback = UTF8ToString(fallbackPtr);
    try {
      if (typeof window.kpExit === 'function') { window.kpExit(); return; }
      if (document.fullscreenElement && document.exitFullscreen) document.exitFullscreen();
      var ref = document.referrer;
      if (ref && new URL(ref).origin === window.location.origin) { window.history.back(); return; }
      window.location.href = fallback;
    } catch (e) {
      console.warn('[kpExit]', e);
    }
  }
};

mergeInto(LibraryManager.library, KovusPrideWebLib);
