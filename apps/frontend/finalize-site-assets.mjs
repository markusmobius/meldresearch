import assert from 'node:assert/strict';
import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = path.dirname(fileURLToPath(import.meta.url));
const output = path.join(frontend, 'dist', 'frontend', 'browser');
const index = await readFile(path.join(output, 'index.html'), 'utf8');
const styles = [...index.matchAll(/<link rel="stylesheet" href="([^"]+)"[^>]*>/g)]
  .map(([, file]) => file).filter(file => /^styles-[\w-]+\.css$/.test(file));
const scripts = [...index.matchAll(/<script src="([^"]+)" type="module"><\/script>/g)]
  .map(([, file]) => file);
assert.equal(styles.length, 1, 'The Angular build must emit one hashed application stylesheet.');
assert.ok(scripts.some(file => /^main-[\w-]+\.js$/.test(file)), 'The Angular entry bundle is missing.');
assert.ok(scripts.every(file => /^[\w-]+\.js$/.test(file)), 'Unexpected Angular script path.');
const assets = [
  '<link rel="icon" type="image/png" href="/favicon.png">',
  ...styles.map(file => `<link rel="stylesheet" href="/${file}">`),
  ...scripts.map(file => `<script src="/${file}" type="module"></script>`),
].join('\n');
await writeFile(path.join(output, 'angular-assets.html'), assets, 'utf8');

for (const name of ['docs', 'team', 'privacy']) {
  const filename = path.join(output, `${name}.html`);
  const html = await readFile(filename, 'utf8');
  const header = /<header class="site-header">[\s\S]*?<\/header>/;
  assert.ok(header.test(html), `${name} must have a public navigation fallback.`);
  assert.ok(html.includes('</head>'), `${name} must be a complete HTML document.`);
  await writeFile(filename, html.replace(header, '<app-meld-toolbar>$&</app-meld-toolbar>')
    .replace('</head>', `${assets}\n</head>`)
    .replace('class="site-footer"', 'class="meld-footer"'), 'utf8');
}

const licenses = path.join(output, 'font-licenses');
await mkdir(licenses, { recursive: true });
for (const font of ['outfit', 'material-icons']) {
  await copyFile(path.join(frontend, 'node_modules', '@fontsource', font, 'LICENSE'),
    path.join(licenses, `${font}.txt`));
}
console.log('Shared Angular Material navigation added to public pages; self-hosted font licenses included.');
