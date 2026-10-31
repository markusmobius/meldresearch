import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { cp, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = path.join(repo, 'browser', 'overleaf');
const sourceManifest = JSON.parse(await readFile(path.join(source, 'manifest.json'), 'utf8'));
const name = `meld-research-overleaf-${sourceManifest.version}.zip`;
const expectedEntries = [
  'manifest.json', 'background.js', 'content.js', 'reader.js', 'popup.html', 'popup.css',
  'popup.js', 'privacy.html', 'icons/icon-16.png', 'icons/icon-32.png', 'icons/icon-48.png',
  'icons/icon-128.png', 'THIRD-PARTY-NOTICES.md',
].sort();

function prepare(root, output) {
  return spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File',
    path.join(root, 'browser', 'prepare-chrome.ps1'), '-OutputDirectory', output], {
    cwd: root, encoding: 'utf8', windowsHide: true, timeout: 60000,
  });
}

function inspectAndExtract(zip, destination) {
  const script = `
    $ErrorActionPreference = 'Stop'
    $zip = [IO.Compression.ZipFile]::OpenRead($env:MELD_PACKAGE_TEST_ZIP)
    try {
      $entries = @($zip.Entries | ForEach-Object {
        if ($_.FullName.StartsWith('/') -or $_.FullName.Contains('..') -or $_.FullName.Contains('\\')) {
          throw 'Unexpected archive path.'
        }
        $stream = $_.Open()
        $memory = [IO.MemoryStream]::new()
        try {
          $stream.CopyTo($memory)
          @{ name = $_.FullName; data = [Convert]::ToBase64String($memory.ToArray()) }
        } finally { $stream.Dispose(); $memory.Dispose() }
      })
      ConvertTo-Json -InputObject $entries -Compress
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($env:MELD_PACKAGE_TEST_ZIP, $env:MELD_PACKAGE_TEST_EXTRACT)
  `;
  const result = execFileSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', script], {
    env: { ...process.env, MELD_PACKAGE_TEST_ZIP: zip, MELD_PACKAGE_TEST_EXTRACT: destination },
    encoding: 'utf8', windowsHide: true, timeout: 30000, maxBuffer: 4 * 1024 * 1024,
  });
  return new Map(JSON.parse(result).map(entry => [entry.name, Buffer.from(entry.data, 'base64')]));
}

function pngSize(bytes) {
  assert.ok(bytes.length >= 33);
  assert.equal(bytes.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
  assert.equal(bytes.subarray(12, 16).toString(), 'IHDR');
  return [bytes.readUInt32BE(16), bytes.readUInt32BE(20)];
}

test('builds and exercises the exact Chrome draft-upload package', { timeout: 120000 }, async t => {
  const temporary = await mkdtemp(path.join(tmpdir(), 'meldresearch-chrome-package-'));
  try {
    const output = path.join(temporary, 'output');
    const built = prepare(repo, output);
    assert.equal(built.status, 0, built.error?.message ?? built.stderr + built.stdout);
    const zip = path.join(output, name);
    const extracted = path.join(temporary, 'extracted');
    const entries = inspectAndExtract(zip, extracted);
    const manifest = JSON.parse(entries.get('manifest.json'));

    await t.test('contains only the extension allowlist and no development identity', async () => {
      assert.deepEqual([...entries.keys()].sort(), expectedEntries);
      assert.equal(entries.size, expectedEntries.length);
      assert.equal(manifest.manifest_version, 3);
      assert.equal(manifest.version, sourceManifest.version);
      assert.equal(manifest.key, undefined);
      assert.equal(manifest.update_url, undefined);
      assert.equal(manifest.incognito, 'not_allowed');
      assert.ok(manifest.description.length > 0 && manifest.description.length <= 132);
      assert.deepEqual(manifest.permissions, ['nativeMessaging', 'scripting', 'storage', 'alarms']);
      assert.deepEqual(manifest.host_permissions, ['https://www.overleaf.com/*']);
      assert.deepEqual(manifest.externally_connectable, { matches: ['http://127.0.0.1/*', 'http://localhost/*'] });
      assert.equal(manifest.content_security_policy.extension_pages, "script-src 'self'; object-src 'none'; base-uri 'none'");
      assert.equal(JSON.parse(await readFile(path.join(source, 'manifest.json'), 'utf8')).key, sourceManifest.key);
      assert.ok(!Buffer.concat([...entries.values()]).includes(Buffer.from(sourceManifest.key)));
    });

    await t.test('ships the actual executable sources and required attribution', async () => {
      for (const file of ['background.js', 'content.js', 'reader.js', 'popup.js', 'popup.html', 'popup.css', 'privacy.html'])
        assert.deepEqual(entries.get(file), await readFile(path.join(source, file)), file);
      assert.deepEqual(entries.get('THIRD-PARTY-NOTICES.md'), await readFile(path.join(repo, 'THIRD-PARTY-NOTICES.md')));
      for (const file of ['background.js', 'content.js', 'reader.js', 'popup.js'])
        assert.doesNotMatch(entries.get(file).toString(), /\beval\s*\(|new\s+Function\s*\(|importScripts\s*\(\s*['"]https?:/);
    });

    await t.test('includes correctly sized real PNG icons and store artwork', async () => {
      for (const size of [16, 32, 48, 128]) {
        const entry = manifest.icons[size];
        assert.equal(entry, `icons/icon-${size}.png`);
        assert.deepEqual(pngSize(entries.get(entry)), [size, size]);
      }
      assert.deepEqual(pngSize(await readFile(path.join(output, 'store-icon-128.png'))), [128, 128]);
      assert.deepEqual(pngSize(await readFile(path.join(output, 'small-promo-440x280.png'))), [440, 280]);
      assert.deepEqual(pngSize(await readFile(path.join(output, 'privacy-screenshot-1280x800.png'))), [1280, 800]);
    });

    await t.test('makes the privacy notice reachable and exports identical static-hosting files', async () => {
      const popup = entries.get('popup.html').toString();
      assert.match(popup, /href="privacy\.html"/);
      assert.match(popup, /Requires the Meld Research desktop application/);
      const policy = entries.get('privacy.html').toString();
      assert.match(policy, /markusmobius@gmail\.com/);
      assert.match(policy, /not encrypted/);
      assert.match(policy, /not TLS/);
      assert.match(policy, /do not delete ZIPs/);
      assert.match(policy, /responsible contact: Markus Mobius/);
      assert.doesNotMatch(policy, /<script\b/i);
      assert.match(entries.get('popup.css').toString(), /\.site-only\s*\{\s*display:\s*none;\s*\}/);
      for (const file of ['privacy.html', 'popup.css', 'icons/icon-48.png'])
        assert.deepEqual(await readFile(path.join(output, 'privacy-site', ...file.split('/'))), entries.get(file));
      for (const file of ['popup.html', 'privacy.html']) {
        // The same policy is hosted by both apps; their navigation is hidden in the connector.
        const standalone = entries.get(file).toString().replace(/<nav\b[^>]*class="[^"]*\bsite-only\b[^"]*"[^>]*>[\s\S]*?<\/nav>/g, '');
        for (const match of standalone.matchAll(/\b(?:src|href)="([^"]+)"/g)) {
          if (!match[1].startsWith('mailto:')) assert.ok(entries.has(match[1]), `${file}: missing ${match[1]}`);
        }
      }
    });

    await t.test('is repeatable and supplies a matching SHA-256 checksum', async () => {
      const first = await readFile(zip);
      const result = prepare(repo, output);
      assert.equal(result.status, 0, result.error?.message ?? result.stderr);
      assert.deepEqual(await readFile(zip), first);
      const checksum = createHash('sha256').update(first).digest('hex');
      assert.equal(await readFile(zip + '.sha256', 'utf8'), `${checksum}  ${name}\n`);
      assert.equal((await readdir(output)).some(file => file.endsWith('.tmp')), false);
    });

    await t.test('passes the existing behavior suite under a browser-assigned, non-development ID', () => {
      const environment = { ...process.env, MELD_EXTENSION_TEST_ROOT: extracted };
      delete environment.NODE_TEST_CONTEXT;
      const result = spawnSync(process.execPath, ['--test', '--test-reporter=tap', path.join(repo, 'tests', 'extension.test.mjs')], {
        cwd: repo, env: environment,
        encoding: 'utf8', windowsHide: true, timeout: 60000,
      });
      assert.equal(result.status, 0, result.error?.message ?? result.stdout + result.stderr);
      assert.match(result.stdout, /pass 39/);
      assert.match(result.stdout, /fail 0/);
    });

    await t.test('excludes unexpected files and refuses newly broadened permissions', async () => {
      const fixture = path.join(temporary, 'fixture');
      await cp(path.join(repo, 'browser'), path.join(fixture, 'browser'), { recursive: true });
      await cp(path.join(repo, 'THIRD-PARTY-NOTICES.md'), path.join(fixture, 'THIRD-PARTY-NOTICES.md'));
      await writeFile(path.join(fixture, 'browser', 'overleaf', '.env'), 'SYNTHETIC-SECRET-DO-NOT-PACKAGE');
      const fixtureOutput = path.join(fixture, 'output');
      const result = prepare(fixture, fixtureOutput);
      assert.equal(result.status, 0, result.error?.message ?? result.stderr);
      assert.deepEqual(await readFile(path.join(fixtureOutput, name)), await readFile(zip));

      const changed = structuredClone(sourceManifest);
      changed.permissions.push('cookies');
      await writeFile(path.join(fixture, 'browser', 'overleaf', 'manifest.json'), JSON.stringify(changed));
      const rejected = prepare(fixture, path.join(fixture, 'rejected'));
      assert.notEqual(rejected.status, 0);
      assert.match(rejected.stderr, /Unexpected permissions/);

      changed.permissions = sourceManifest.permissions;
      changed.description = 'x'.repeat(132);
      await writeFile(path.join(fixture, 'browser', 'overleaf', 'manifest.json'), JSON.stringify(changed));
      assert.equal(prepare(fixture, fixtureOutput).status, 0, 'Chrome accepts a 132-character description.');
      changed.description += 'x';
      await writeFile(path.join(fixture, 'browser', 'overleaf', 'manifest.json'), JSON.stringify(changed));
      const tooLong = prepare(fixture, fixtureOutput);
      assert.notEqual(tooLong.status, 0);
      assert.match(tooLong.stderr, /132 characters/);

      changed.description = sourceManifest.description;
      changed.version = '65536.0';
      await writeFile(path.join(fixture, 'browser', 'overleaf', 'manifest.json'), JSON.stringify(changed));
      const invalidVersion = prepare(fixture, fixtureOutput);
      assert.notEqual(invalidVersion.status, 0);
      assert.match(invalidVersion.stderr, /between 0 and 65535/);

      changed.version = sourceManifest.version;
      changed.icons['256'] = 'icons/not-packaged.png';
      await writeFile(path.join(fixture, 'browser', 'overleaf', 'manifest.json'), JSON.stringify(changed));
      const missingIcon = prepare(fixture, fixtureOutput);
      assert.notEqual(missingIcon.status, 0);
      assert.match(missingIcon.stderr, /Unexpected icon sizes/);
    });
  } finally {
    assert.ok(temporary.startsWith(path.join(tmpdir(), 'meldresearch-chrome-package-')));
    await rm(temporary, { recursive: true });
  }
});
