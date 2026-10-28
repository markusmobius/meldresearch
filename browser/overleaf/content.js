(() => {
  if (globalThis.__meldOverleafV1) return;
  globalThis.__meldOverleafV1 = true;

  class TransferError extends Error {
    constructor(code) {
      super(code);
      this.code = code;
    }
  }

  chrome.runtime.onConnect.addListener(port => {
    if (port.name !== 'meld-overleaf-v1' || port.sender?.id !== chrome.runtime.id) return;
    let active = null;
    let pendingAck = null;
    let closed = false;

    function stop() {
      active?.controller.abort();
      pendingAck?.reject(new TransferError('cancelled'));
      pendingAck = null;
    }

    port.onDisconnect.addListener(() => {
      closed = true;
      stop();
    });

    function sendChunk(id, sequence, data, signal) {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => finish(new TransferError('bridge_timeout')), 30000);
        const cancelled = () => finish(new TransferError('cancelled'));
        function finish(error) {
          clearTimeout(timer);
          signal.removeEventListener('abort', cancelled);
          pendingAck = null;
          error ? reject(error) : resolve();
        }
        pendingAck = { sequence, resolve: () => finish(), reject: error => finish(error) };
        signal.addEventListener('abort', cancelled, { once: true });
        if (signal.aborted) {
          cancelled();
          return;
        }
        try {
          port.postMessage({ type: 'chunk', id, sequence, data });
        } catch {
          finish(new TransferError('bridge_timeout'));
        }
      });
    }

    async function execute(message) {
      if (active !== null) {
        port.postMessage({ type: 'error', id: message.id, error: 'browser_busy' });
        return;
      }
      const validId = /^[a-f0-9]{32}$/.test(message.id ?? '');
      const list = message.operation === 'listProjects';
      const download = message.operation === 'downloadProject' && /^[a-f0-9]{24}$/.test(message.projectId ?? '');
      if (!validId || (!list && !download) || location.origin !== 'https://www.overleaf.com') {
        port.postMessage({ type: 'error', id: message.id, error: 'invalid_request' });
        return;
      }

      const controller = new AbortController();
      active = { id: message.id, controller };
      const deadline = setTimeout(() => controller.abort(), 5 * 60 * 1000);
      let reader = null;
      try {
        reader = globalThis.__meldCreateOverleafReaderV1({
          operation: message.operation, projectId: message.projectId, signal: controller.signal,
        });
        let sequence = 0;
        while (true) {
          const part = await reader.next();
          if (part.error) throw new TransferError(part.error);
          if (part.done) break;
          if (part.contentType)
            port.postMessage({ type: 'start', id: message.id, status: 200, contentType: part.contentType });
          else
            await sendChunk(message.id, sequence++, part.data, controller.signal);
        }
        if (!controller.signal.aborted)
          port.postMessage({ type: 'complete', id: message.id });
      } catch (error) {
        if (!closed)
          port.postMessage({ type: 'error', id: message.id, error: error instanceof TransferError ? error.code : 'network_error' });
      } finally {
        clearTimeout(deadline);
        controller.abort();
        if (reader) {
          try {
            await reader.close();
          } catch {
            console.warn('Meld: the Overleaf response stream closed during cleanup.');
          }
        }
        active = null;
      }
    }

    port.onMessage.addListener(message => {
      if (message.type === 'request') {
        void execute(message);
      } else if (active && message.id === active.id) {
        if (message.type === 'cancel') stop();
        else if (message.type === 'ack' && message.sequence === pendingAck?.sequence) pendingAck.resolve();
      }
    });
  });
})();
