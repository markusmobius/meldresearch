(() => {
  class ReadError extends Error {
    constructor(code) {
      super(code);
      this.code = code;
    }
  }

  function createReader({ operation, projectId, signal }) {
    const origin = 'https://www.overleaf.com';
    const controller = new AbortController();
    const abort = () => controller.abort();
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) controller.abort();
    const deadline = setTimeout(abort, 5 * 60 * 1000);
    let stream = null;

    function* chunks(bytes) {
      for (let offset = 0; offset < bytes.length; offset += 64 * 1024) {
        const chunk = bytes.subarray(offset, offset + 64 * 1024);
        let binary = '';
        for (let index = 0; index < chunk.length; index += 8192)
          binary += String.fromCharCode(...chunk.subarray(index, index + 8192));
        yield { data: btoa(binary) };
      }
    }

    function stop() {
      clearTimeout(deadline);
      signal?.removeEventListener('abort', abort);
      controller.abort();
    }

    async function* read() {
      try {
        const list = operation === 'listProjects';
        const download = operation === 'downloadProject' && /^[a-f0-9]{24}$/.test(projectId ?? '');
        if ((!list && !download) || location.origin !== origin) throw new ReadError('invalid_request');
        const path = list ? '/' : `/project/${projectId}/download/zip`;
        const limit = list ? 8 * 1024 * 1024 : 100 * 1024 * 1024;
        const response = await fetch(new URL(path, origin).href, {
          method: 'GET', credentials: 'include', cache: 'no-store', redirect: 'follow', signal: controller.signal,
        });
        const finalUrl = new URL(response.url);
        if (finalUrl.origin !== origin || /^\/(?:login|register)(?:\/|$)/.test(finalUrl.pathname))
          throw new ReadError('sign_in_required');
        if (response.status !== 200) throw new ReadError(`http_${response.status}`);
        const contentType = response.headers.get('content-type');
        if (!contentType || !response.body) throw new ReadError('invalid_response');
        const declaredSize = response.headers.get('content-length');
        if (declaredSize && Number(declaredSize) > limit) throw new ReadError('response_too_large');

        stream = response.body.getReader();
        const pageChunks = [];
        let total = 0;
        if (!list) yield { contentType };
        while (true) {
          const { done, value } = await stream.read();
          if (done) break;
          total += value.byteLength;
          if (total > limit) throw new ReadError('response_too_large');
          if (list) pageChunks.push(value);
          else yield* chunks(value);
        }
        if (controller.signal.aborted) throw new ReadError('cancelled');
        if (total === 0) throw new ReadError('invalid_response');
        if (list) {
          if (!contentType.toLowerCase().startsWith('text/html')) throw new ReadError('metadata_invalid');
          const pageBytes = new Uint8Array(total);
          let offset = 0;
          for (const chunk of pageChunks) { pageBytes.set(chunk, offset); offset += chunk.length; }
          const page = new DOMParser().parseFromString(new TextDecoder('utf-8', { fatal: true }).decode(pageBytes), 'text/html');
          const blob = page.querySelector('meta[name="ol-prefetchedProjectsBlob"]')?.getAttribute('content');
          if (!blob) throw new ReadError('project_list_unavailable');
          let projects;
          try {
            const data = JSON.parse(blob);
            if (!Array.isArray(data.projects) || data.projects.length > 10000) throw new ReadError('metadata_invalid');
            // Never export the HTML page, CSRF tokens, or unused account metadata.
            projects = data.projects.map(project => ({
              id: project.id, name: project.name, archived: project.archived,
              trashed: project.trashed, lastUpdated: project.lastUpdated,
            }));
          } catch {
            throw new ReadError('metadata_invalid');
          }
          const metadata = new TextEncoder().encode(JSON.stringify({ projects }));
          if (metadata.length > limit) throw new ReadError('response_too_large');
          yield { contentType: 'application/json' };
          yield* chunks(metadata);
        }
      } catch (error) {
        yield { error: error instanceof ReadError ? error.code : 'network_error' };
      } finally {
        stop();
        if (stream) {
          try {
            await stream.cancel();
          } catch {
            console.warn('Meld: the Overleaf response stream closed during cleanup.');
          }
          stream.releaseLock();
        }
      }
    }

    const iterator = read();
    return {
      next: async () => {
        const { value, done } = await iterator.next();
        return value ?? { done };
      },
      close: async () => {
        stop();
        await iterator.return();
      },
    };
  }

  globalThis.__meldCreateOverleafReaderV1 = createReader;
})()
