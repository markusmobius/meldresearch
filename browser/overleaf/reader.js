(() => {
  class ReadError extends Error {
    constructor(code) {
      super(code);
      this.code = code;
    }
  }

  function createReader({ operation, projectId, path: filePath, signal }) {
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

    // Reads when each file was last edited and who edited the project, from Overleaf's project history. Only paths and
    // times, each editor's name, email and latest edit, and when each editor last edited each file leave the tab; the
    // changes themselves are discarded here.
    async function* history() {
      const paths = {};
      const editors = {};
      const index = {};
      const files = {};
      const added = {};
      let before = null;
      let complete = false;
      for (let page = 0; page < 40 && !complete; page++) {
        const query = before === null ? '?min_count=100' : `?min_count=100&before=${before}`;
        const response = await fetch(new URL(`/project/${projectId}/updates${query}`, origin).href, {
          method: 'GET', credentials: 'include', cache: 'no-store', redirect: 'follow', signal: controller.signal,
          headers: { Accept: 'application/json' },
        });
        const finalUrl = new URL(response.url);
        if (finalUrl.origin !== origin || /^\/(?:login|register)(?:\/|$)/.test(finalUrl.pathname))
          throw new ReadError('sign_in_required');
        if (response.status !== 200) throw new ReadError('history_unavailable');
        const text = await response.text();
        if (text.length > 8 * 1024 * 1024) throw new ReadError('response_too_large');
        let data;
        try { data = JSON.parse(text); } catch { throw new ReadError('history_unavailable'); }
        if (!Array.isArray(data?.updates)) throw new ReadError('history_unavailable');
        // Updates arrive newest first, so the first time a path appears is its latest edit.
        for (const update of data.updates) {
          const time = Number(update?.meta?.end_ts);
          if (!Number.isFinite(time)) continue;
          const names = [...(Array.isArray(update.pathnames) ? update.pathnames : []),
            ...(Array.isArray(update.project_ops) ? update.project_ops.flatMap(op =>
              [op?.add?.pathname, op?.rename?.newPathname]) : [])];
          for (const name of names) {
            if (typeof name !== 'string' || name.length > 1024) continue;
            const path = name.replace(/^\/+/, '');
            if (path && !(path in paths) && Object.keys(paths).length < 5000) paths[path] = time;
          }
          // Who added a file (updates arrive newest first, so the last add seen is the first one): text that was there
          // when it was added (an upload or a copy) has no author of its own in the history.
          const adds = (Array.isArray(update.project_ops) ? update.project_ops : []).map(op => op?.add?.pathname)
            .filter(name => typeof name === 'string' && name.length <= 1024).map(name => name.replace(/^\/+/, ''));
          let adder = null;
          for (const user of Array.isArray(update?.meta?.users) ? update.meta.users : []) {
            const editor = person(user?.first_name, user?.last_name, user?.email);
            const key = editor && (editor.email ?? `name:${editor.name}`);
            if (key && !(key in editors) && Object.keys(editors).length < 200) {
              index[key] = Object.keys(editors).length;
              editors[key] = { ...editor, at: time };
            }
            if (!(key in index)) continue;
            adder ??= index[key];
            // Newest first, so the first time an editor appears for a file is their latest edit of it.
            for (const name of names) {
              if (typeof name !== 'string' || name.length > 1024) continue;
              const path = name.replace(/^\/+/, '');
              if (!(path in paths)) continue;
              const seen = files[path] ??= [];
              if (seen.length < 20 && !seen.some(entry => entry[0] === index[key])) seen.push([index[key], time]);
            }
          }
          if (adder !== null) for (const name of adds) if (Object.keys(added).length < 5000 || name in added) added[name] = adder;
        }
        const next = Number(data.nextBeforeTimestamp);
        if (!Number.isInteger(next) || next <= 0) complete = true;
        else before = next;
      }
      const bytes = new TextEncoder().encode(JSON.stringify({ paths, complete, editors: Object.values(editors), files, added }));
      yield { contentType: 'application/json' };
      yield* chunks(bytes);
    }

    // Who wrote each line of a file now, from Overleaf's history diff of the file over its whole history: each line's
    // characters by author (-1: no author, such as text that was there when the file was added). Lines leave the tab only
    // as short hashes, the text and the deleted text are discarded here.
    async function* fileAuthors() {
      const get = async url => {
        const response = await fetch(new URL(url, origin).href, {
          method: 'GET', credentials: 'include', cache: 'no-store', redirect: 'follow', signal: controller.signal,
          headers: { Accept: 'application/json' },
        });
        const finalUrl = new URL(response.url);
        if (finalUrl.origin !== origin || /^\/(?:login|register)(?:\/|$)/.test(finalUrl.pathname)) throw new ReadError('sign_in_required');
        if (response.status !== 200) throw new ReadError('history_unavailable');
        const text = await response.text();
        if (text.length > 8 * 1024 * 1024) throw new ReadError('response_too_large');
        try { return JSON.parse(text); } catch { throw new ReadError('history_unavailable'); }
      };
      const latest = await get(`/project/${projectId}/updates?min_count=1`);
      const to = Number(latest?.updates?.[0]?.toV);
      if (!Number.isInteger(to) || to < 0) throw new ReadError('history_unavailable');
      const data = await get(`/project/${projectId}/diff?pathname=${encodeURIComponent(filePath)}&from=0&to=${to}`);
      if (!Array.isArray(data?.diff)) throw new ReadError('history_unavailable');
      const users = [];
      const userIndex = {};
      const lines = [];
      let line = '';
      let counts = new Map();
      const author = part => {
        const user = (Array.isArray(part?.meta?.users) ? part.meta.users : []).map(item => person(item?.first_name, item?.last_name, item?.email)).find(Boolean);
        if (!user) return -1;
        const key = user.email ?? `name:${user.name}`;
        if (!(key in userIndex) && users.length < 200) { userIndex[key] = users.length; users.push(user); }
        return userIndex[key] ?? -1;
      };
      const finish = async () => {
        const trimmed = line.trim();
        if (trimmed && lines.length < 20000) {
          const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(trimmed)));
          const hash = [...digest.slice(0, 6)].map(byte => byte.toString(16).padStart(2, '0')).join('');
          lines.push([hash, [...counts].map(([who, chars]) => [who, chars])]);
        }
        line = '';
        counts = new Map();
      };
      for (const part of data.diff) {
        // Deleted text is not in the file now.
        const text = typeof part?.u === 'string' ? part.u : typeof part?.i === 'string' ? part.i : null;
        if (text === null) continue;
        const who = typeof part.i === 'string' ? author(part) : -1;
        for (const piece of text.split(/(\n)/)) {
          if (piece === '\n') { await finish(); continue; }
          if (!piece) continue;
          line += piece;
          const chars = piece.replace(/\s+/g, '').length;
          if (chars) counts.set(who, (counts.get(who) ?? 0) + chars);
        }
      }
      await finish();
      const bytes = new TextEncoder().encode(JSON.stringify({ users, lines }));
      yield { contentType: 'application/json' };
      yield* chunks(bytes);
    }

    // A person's name and email as Overleaf shows them; null when neither is usable.
    function person(first, last, address) {
      const name = [first, last].filter(part => typeof part === 'string' && part.trim()).map(part => part.trim()).join(' ').slice(0, 200) || null;
      const email = typeof address === 'string' && address.length <= 320 && /^[^\s@]+@[^\s@]+$/.test(address.trim())
        ? address.trim().toLowerCase() : null;
      return name || email ? { name, email } : null;
    }

    async function* read() {
      try {
        const list = operation === 'listProjects';
        const download = operation === 'downloadProject' && /^[a-f0-9]{24}$/.test(projectId ?? '');
        const edits = operation === 'projectHistory' && /^[a-f0-9]{24}$/.test(projectId ?? '');
        const authors = operation === 'fileAuthors' && /^[a-f0-9]{24}$/.test(projectId ?? '') && typeof filePath === 'string' &&
          filePath.length > 0 && filePath.length <= 1024 && !filePath.split('/').some(part => part === '' || part === '.' || part === '..');
        if ((!list && !download && !edits && !authors) || location.origin !== origin) throw new ReadError('invalid_request');
        if (edits) {
          yield* history();
          return;
        }
        if (authors) {
          yield* fileAuthors();
          return;
        }
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
            // Never export the HTML page, CSRF tokens, or unused account metadata. The owner (name and email) and whether
            // the user owns the project tell Meld who works on it and which name and email are the user's own.
            projects = data.projects.map(project => ({
              id: project.id, name: project.name, archived: project.archived,
              trashed: project.trashed, lastUpdated: project.lastUpdated,
              owner: person(project.owner?.firstName, project.owner?.lastName, project.owner?.email),
              owned: project.accessLevel === 'owner',
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
