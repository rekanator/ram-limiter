// Runs inside the page. Notes calls and file uploads, so the tab isn't unloaded
// in the middle of one. Nothing is read or sent: it only answers yes or no.
if (!globalThis.ramLimiterBusy) {
  const BIG = 256 * 1024; // smaller requests are ordinary page traffic
  const LINGER = 60_000; // sites that upload in pieces pause between them
  const tracks = new Set(); // camera, microphone and screen
  const calls = new Set();
  let uploading = 0;
  let until = 0;

  const sizeOf = (body) => {
    if (body instanceof Blob) return body.size;
    if (body instanceof FormData) {
      let total = 0;
      for (const value of body.values()) if (value instanceof Blob) total += value.size;
      return total;
    }
    return body?.byteLength ?? 0; // buffers; text and empty bodies count as 0
  };

  // Marks an upload as running and returns what to call when it ends.
  const upload = (body) => {
    try {
      if (!(sizeOf(body) >= BIG)) return null;
    } catch {
      return null;
    }
    uploading++;
    return () => {
      uploading--;
      until = Date.now() + LINGER;
    };
  };

  // Replaces a built-in function with one that behaves the same and is watched.
  const wrap = (owner, name, handler) => {
    if (typeof owner?.[name] === "function") owner[name] = new Proxy(owner[name], handler);
  };

  wrap(globalThis, "fetch", {
    apply(target, self, args) {
      const done = upload(args[1]?.body);
      const result = Reflect.apply(target, self, args);
      if (done) result.then(done, done);
      return result;
    },
  });

  wrap(globalThis.XMLHttpRequest?.prototype, "send", {
    apply(target, self, args) {
      const done = upload(args[0]);
      if (done) self.addEventListener("loadend", done, { once: true });
      try {
        return Reflect.apply(target, self, args);
      } catch (error) {
        if (done) done();
        throw error;
      }
    },
  });

  for (const name of ["getUserMedia", "getDisplayMedia"]) {
    wrap(globalThis.MediaDevices?.prototype, name, {
      apply(target, self, args) {
        const result = Reflect.apply(target, self, args);
        result.then((stream) => { for (const track of stream.getTracks()) tracks.add(track); }, () => {});
        return result;
      },
    });
  }

  for (const name of ["RTCPeerConnection", "webkitRTCPeerConnection"]) {
    wrap(globalThis, name, {
      construct(target, args, newTarget) {
        const call = Reflect.construct(target, args, newTarget);
        for (const old of calls) if (old.connectionState === "closed") calls.delete(old);
        calls.add(call);
        return call;
      },
    });
  }

  // A connection counts as a call only if it carries audio or video.
  const inCall = (call) =>
    call.connectionState === "connected" &&
    [...call.getSenders(), ...call.getReceivers()].some((end) => end.track?.readyState === "live");

  Object.defineProperty(globalThis, "ramLimiterBusy", {
    value: () =>
      uploading > 0 || Date.now() < until ||
      [...tracks].some((track) => track.readyState === "live") || [...calls].some(inCall),
  });
}
