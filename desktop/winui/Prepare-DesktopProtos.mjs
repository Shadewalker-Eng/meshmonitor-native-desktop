import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const dist = process.argv[2];
if (!dist) throw new Error('Usage: node Prepare-DesktopProtos.mjs <resources/dist>');

// protobufjs deduplicates imported .proto files by resolved path string.
// The Windows package must use the same separator for entrypoints and imports.
const loader = join(dist, 'server', 'protobufLoader.js');
replaceExpected(loader, 'return path.join(protoRoot, target);',
  'return protobuf.util.path.normalize(path.join(protoRoot, target));',
  'return path.join(PROTOBUFJS_DIR, target);',
  'return protobuf.util.path.normalize(path.join(PROTOBUFJS_DIR, target));');

// The container path in 4.18.0 is not available in the desktop payload.
const service = join(dist, 'server', 'protobufService.js');
replaceExpected(service, "const protoDir = '/app/protobufs';",
  "const protoDir = path.join(process.cwd(), 'protobufs');");

function replaceExpected(path, ...pairs) {
  let contents = readFileSync(path, 'utf8');
  for (let index = 0; index < pairs.length; index += 2) {
    const [before, after] = pairs.slice(index, index + 2);
    if (contents.includes(after)) continue;
    if (contents.split(before).length !== 2) throw new Error(`Unexpected upstream source in ${path}.`);
    contents = contents.replace(before, after);
  }
  writeFileSync(path, contents);
}
