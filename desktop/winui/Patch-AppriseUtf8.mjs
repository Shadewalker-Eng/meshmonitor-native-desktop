import { execFileSync } from 'node:child_process';
import { appendFileSync, copyFileSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const [manifestTool, input, output] = process.argv.slice(2);
if (!manifestTool || !input || !output || resolve(input) === resolve(output)) {
  throw new Error('Usage: node Patch-AppriseUtf8.mjs <mt.exe> <upstream apprise-api.exe> <new apprise-api.exe>');
}

const original = readFileSync(input);
const cookieSize = 88;
const cookie = original.lastIndexOf(Buffer.from('4d45490c0b0a0b0e', 'hex'));
if (cookie < 0 || cookie + cookieSize > original.length) {
  throw new Error('The upstream executable has no PyInstaller archive cookie.');
}
const archiveEnd = cookie + cookieSize;
const archiveStart = archiveEnd - original.readUInt32BE(cookie + 8);
if (archiveStart < 0 || archiveStart >= cookie) throw new Error('Invalid PyInstaller archive bounds.');

const scratch = mkdtempSync(join(dirname(resolve(output)), 'apprise-manifest-'));
const manifestPath = join(scratch, 'apprise.manifest');
const staged = join(scratch, 'apprise.exe');
try {
  copyFileSync(input, staged);
  execFileSync(manifestTool, ['-nologo', `-inputresource:${staged};#1`, `-out:${manifestPath}`]);
  const manifest = readFileSync(manifestPath, 'utf8');
  if (manifest.includes('activeCodePage') || !manifest.includes('</windowsSettings>')) {
    throw new Error('The upstream sidecar manifest already specifies a code page or has changed shape.');
  }
  const codePage = '<activeCodePage xmlns="http://schemas.microsoft.com/SMI/2019/WindowsSettings">UTF-8</activeCodePage>';
  writeFileSync(manifestPath, manifest.replace('</windowsSettings>', `${codePage}</windowsSettings>`));
  execFileSync(manifestTool, ['-nologo', '-manifest', manifestPath, `-outputresource:${staged};#1`]);

  // mt.exe rewrites PE resources and can drop the appended PyInstaller payload.
  const updated = readFileSync(staged);
  const archive = original.subarray(archiveStart, archiveEnd);
  if (!updated.includes(archive)) appendFileSync(staged, archive);
  copyFileSync(staged, output);
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
