import { copyFile, mkdir, rename, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = path.dirname(fileURLToPath(import.meta.url));
const connector = path.resolve(frontend, '..', '..', 'browser', 'overleaf');
for (const [source, destination] of [
  [['privacy.html'], ['privacy.html']],
  [['popup.css'], ['connector', 'popup.css']],
  [['icons', 'icon-48.png'], ['icons', 'icon-48.png']],
]) {
  const target = path.join(frontend, 'public', ...destination);
  const temporary = target + `.${process.pid}.tmp`;
  await mkdir(path.dirname(target), { recursive: true });
  try {
    await copyFile(path.join(connector, ...source), temporary);
    await rename(temporary, target);
  } finally {
    await rm(temporary, { force: true });
  }
}
