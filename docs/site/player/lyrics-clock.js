// Presentation only: never writes playback state or sends seek commands to Native.
((root) => {
  'use strict';
  class LyricsClock {
    constructor() { this.anchor = null; }

    observe({ trackId, seconds, playing, rate = 1 }, now, reset = false) {
      seconds = Math.max(0, Number(seconds) || 0);
      rate = Math.max(.1, Number(rate) || 1);
      const previous = this.anchor;
      if (!reset && previous && previous.trackId === trackId &&
          previous.seconds === seconds && previous.playing === playing && previous.rate === rate) return;
      const projected = this.read(now);
      const discontinuity = reset || !previous || previous.trackId !== trackId ||
        previous.playing !== playing || previous.rate !== rate || Math.abs(projected - seconds) > .75;
      this.anchor = { trackId, seconds, playing, rate, now,
        correction: discontinuity || !playing ? 0 : projected - seconds };
    }

    read(now) {
      const a = this.anchor;
      if (!a) return 0;
      if (!a.playing) return a.seconds;
      // Duplicate timestamps must not allow lyrics to run indefinitely after a stall.
      const elapsed = Math.min(.75, Math.max(0, (now - a.now) / 1000));
      const correctionDuration = Math.max(1, Math.abs(a.correction) * 2 / a.rate);
      return Math.max(0, a.seconds + elapsed * a.rate +
        a.correction * (1 - elapsed / correctionDuration));
    }

    isAdvancing(now) {
      return !!this.anchor?.playing && now - this.anchor.now < 750;
    }
  }
  if (typeof module === 'object' && module.exports) module.exports = LyricsClock;
  else root.AuralisLyricsClock = LyricsClock;
})(typeof globalThis === 'object' ? globalThis : this);
