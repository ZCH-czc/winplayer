'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const LyricsClock = require('../Auralis/wwwroot/lyrics-clock.js');
const fs = require('node:fs');
const vm = require('node:vm');
const snapshot = (seconds, extra = {}) => ({ trackId: 'test', seconds, playing: true, rate: 1, ...extra });
const close = (a, b) => assert.ok(Math.abs(a - b) < 1e-7, `${a} != ${b}`);

test('4 Hz native progress yields continuous 60 Hz presentation without changing native samples', () => {
  const clock = new LyricsClock();
  let previous = 0;
  for (let frame = 0; frame <= 600; frame++) {
    const now = frame * 1000 / 60;
    if (frame % 15 === 0) {
      const sample = Object.freeze(snapshot(frame / 60));
      clock.observe(sample, now);
      assert.equal(sample.seconds, frame / 60);
    }
    const value = clock.read(now);
    close(value, frame / 60);
    if (frame) close(value - previous, 1 / 60);
    previous = value;
  }
});

test('late or quantized heartbeat corrections have no position jump or backward motion', () => {
  for (const rate of [.75, 1, 1.25, 1.5, 2]) {
    const clock = new LyricsClock();
    clock.observe(snapshot(10, { rate }), 0);
    let previous = 10;
    for (let frame = 1; frame < 600; frame++) {
      const now = frame * 1000 / 60;
      if (frame % 15 === 0) {
        const before = clock.read(now);
        const quantized = Math.floor((10 + now / 1000 * rate) * 10) / 10;
        clock.observe(snapshot(quantized, { rate }), now);
        close(clock.read(now), before);
      }
      const value = clock.read(now);
      assert.ok(value >= previous);
      assert.ok(value - previous < .05);
      previous = value;
    }
  }
});

test('duplicate samples and missing heartbeats freeze within 750 ms and stop requesting frames', () => {
  const clock = new LyricsClock();
  clock.observe(snapshot(10), 0);
  for (let now = 250; now <= 5000; now += 250) clock.observe(snapshot(10), now);
  close(clock.read(750), clock.read(5000));
  close(clock.read(5000), 10.75);
  assert.equal(clock.isAdvancing(5000), false);
  clock.observe(snapshot(15), 5100);
  close(clock.read(5100), 15);
  assert.equal(clock.isAdvancing(5100), true);
});

test('pause freezes at native time; resume, rate change and track switch recalibrate', () => {
  const clock = new LyricsClock();
  clock.observe(snapshot(10), 0);
  clock.observe(snapshot(10.2, { playing: false }), 250);
  close(clock.read(9000), 10.2);
  assert.equal(clock.isAdvancing(9000), false);
  clock.observe(snapshot(10.2), 9000);
  close(clock.read(9250), 10.45);
  clock.observe(snapshot(10.45, { rate: 2 }), 9250);
  close(clock.read(9500), 10.95);
  clock.observe(snapshot(0, { trackId: 'next' }), 9500);
  close(clock.read(9500), 0);
});

test('explicit short forward/backward seeks do not blend, large native seeks also reset', () => {
  const clock = new LyricsClock();
  clock.observe(snapshot(10), 0);
  clock.observe(snapshot(10.1), 100, true);
  close(clock.read(100), 10.1);
  clock.observe(snapshot(9.9), 120, true);
  close(clock.read(120), 9.9);
  clock.observe(snapshot(100), 200);
  close(clock.read(200), 100);
  clock.observe(snapshot(1), 220);
  close(clock.read(220), 1);
});

// Exercise the actual app scheduler/line renderer, with a small deterministic DOM and RAF.
function presentation() {
  const source = fs.readFileSync(require.resolve('../Auralis/wwwroot/app.js'), 'utf8');
  const frames = new Map();
  const lines = Array.from({ length: 3 }, () => ({
    classList: { toggle() {} }, style: { setProperty(name, value) { this[name] = value; } },
    offsetTop: 100, offsetHeight: 40
  }));
  const scene = { open: true, reduced: false, now: 0, serial: 0, scrolls: 0 };
  const state = { currentTrackId: 'test', currentTime: 0, duration: 30, playbackRate: 1,
    isPlaying: true, lyricsSynced: true, lyricsOffset: 0, activeLyricIndex: -1,
    lyrics: [0, 10, 20].map(timeSeconds => ({ timeSeconds })) };
  const viewport = { clientHeight: 500, scrollTo() { scene.scrolls++; } };
  const context = vm.createContext({
    state, lyricsClock: new LyricsClock(), mediaHub: null, lyricsAnimationFrame: 0, lyricElements: lines,
    document: { hidden: false }, performance: { now: () => scene.now },
    isMotionReduced: () => scene.reduced,
    $: selector => selector === '#nowPlayingOverlay' ? { classList: { contains: () => scene.open } }
      : selector === '#lyricsViewport' ? viewport : lines[state.activeLyricIndex],
    requestAnimationFrame: cb => { frames.set(++scene.serial, cb); return scene.serial; },
    cancelAnimationFrame: id => frames.delete(id)
  });
  for (const name of ['findActiveLyricIndex', 'observeLyricsClock', 'stopLyricsAnimation',
    'canAnimateLyrics', 'syncLyricsAnimation', 'updateLyricsAtTime']) {
    const start = source.indexOf(`  function ${name}(`);
    assert.ok(start >= 0);
    const end = source.indexOf('\n  function ', start + 1);
    vm.runInContext(source.slice(start, end), context);
  }
  return { context, scene, state, frames, lines,
    call: code => vm.runInContext(code, context),
    frame(now) {
      scene.now = now;
      const callbacks = [...frames.values()]; frames.clear();
      callbacks.forEach(callback => callback(now));
    }
  };
}

test('real lyric renderer advances only the active fill; no repeated line scrolling', () => {
  const p = presentation();
  p.call('observeLyricsClock(); syncLyricsAnimation(); updateLyricsAtTime(true);');
  for (let i = 1; i <= 30; i++) {
    if (i % 15 === 0) {
      p.scene.now = i * 1000 / 60; p.state.currentTime = i / 60;
      p.call('observeLyricsClock(); syncLyricsAnimation(); updateLyricsAtTime();');
    }
    p.frame(i * 1000 / 60);
    assert.ok(Math.abs(parseFloat(p.lines[0].style['--lyric-progress']) - i / 6) < .0001);
    assert.equal(p.lines[1].style['--lyric-progress'], '0%');
    assert.equal(p.frames.size, 1);
  }
  assert.equal(p.scene.scrolls, 1);
  p.state.currentTime = 12;
  p.call('observeLyricsClock(true); updateLyricsAtTime();');
  assert.equal(p.state.activeLyricIndex, 1);
  assert.equal(p.lines[0].style['--lyric-progress'], '100%');
  p.state.lyricsOffset = 3000;
  p.call('updateLyricsAtTime(true);');
  assert.equal(p.state.activeLyricIndex, 0);
});

test('scheduler stops on pause, hidden page, closed overlay, reduced motion and missing lyrics', () => {
  for (const mode of ['paused', 'hidden', 'closed', 'reduced', 'empty']) {
    const p = presentation();
    p.call('observeLyricsClock(); syncLyricsAnimation();');
    assert.equal(p.frames.size, 1);
    if (mode === 'paused') p.state.isPlaying = false;
    if (mode === 'hidden') p.context.document.hidden = true;
    if (mode === 'closed') p.scene.open = false;
    if (mode === 'reduced') p.scene.reduced = true;
    if (mode === 'empty') p.context.lyricElements = [];
    p.call('syncLyricsAnimation();');
    assert.equal(p.frames.size, 0, mode);
    p.frame(100);
    assert.equal(p.frames.size, 0, mode);
  }
});

test('stalled RAF stops, later heartbeat restarts; reopening uses current position', () => {
  const p = presentation();
  p.call('observeLyricsClock(); syncLyricsAnimation();');
  p.frame(750);
  assert.equal(p.frames.size, 0);
  p.scene.now = 1000; p.state.currentTime = 1;
  p.call('observeLyricsClock(); syncLyricsAnimation();');
  assert.equal(p.frames.size, 1);
  p.scene.open = false; p.call('syncLyricsAnimation();');
  p.scene.now = 20000; p.state.currentTime = 20;
  p.call('observeLyricsClock();');
  p.scene.open = true;
  p.call('syncLyricsAnimation(); updateLyricsAtTime(true);');
  assert.equal(p.state.activeLyricIndex, 2);
  assert.equal(p.frames.size, 1);
});
