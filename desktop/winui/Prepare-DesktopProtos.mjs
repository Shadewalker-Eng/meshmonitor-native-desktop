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

replaceExpected(service, 'if (config.transparentMode !== undefined)',
  'if (config.serialHalOnly !== undefined) loraConfigData.serialHalOnly = config.serialHalOnly === true;\n            if (config.transparentMode !== undefined)');

const routes = join(dist, 'server', 'routes', 'configRoutes.js');
replaceExpected(routes, 'export default router;', `router.post('/local-module-readback', requireDeviceSourcePermission('configuration', 'write', 'body'), async (req, res) => {
    const { manager } = getDeviceSourceTarget(req);
    const { configType, nodeId } = req.body;
    if (!Number.isInteger(configType) || typeof nodeId !== 'string' || !nodeId)
        return res.status(400).json({ error: 'A module number and radio identity are required.' });
    const sameRadio = () => manager.isDeviceConnected() && manager.getLocalNodeInfo()?.nodeId === nodeId;
    try {
        if (!sameRadio()) return res.status(409).json({ error: 'Reconnect the selected radio to verify this save.' });
        const section = await manager.readLocalModuleConfig(configType, 5000);
        if (!sameRadio() || section == null)
            return res.status(409).json({ error: 'The selected radio did not confirm its module settings.' });
        return res.json({ nodeId, section });
    } catch {
        return res.status(502).json({ error: 'The local radio readback failed.' });
    }
});
export default router;`);

const adminRoutes = join(dist, 'server', 'routes', 'adminRoutes.js');
replaceExpected(adminRoutes,
  '// Format the response based on config type\n                switch (configType) {',
  `// Preserve the whole struct for the desktop editor's read-modify-write.
                if (req.body.desktopRemoteOnly === true && ['device', 'lora', 'position'].includes(configType))
                    return res.json({ config: remoteConfig });
                // Format the response based on config type
                switch (configType) {`,
  '// Session-passkey acquisition used to run HERE',
  `if (params.desktopRemoteOnly === true && (!localNodeNum || isLocalNode ||
            !Number.isInteger(destinationNodeNum) || destinationNodeNum < 1 || destinationNodeNum >= 0xffffffff))
            return res.status(409).json({ error: 'Select a remote node distinct from the connected local radio.' });
        // Session-passkey acquisition used to run HERE`);
for (const manager of ['adminLoadManager', 'gcManager', 'loManager']) {
  const marker = `const localNodeNum = ${manager}.getLocalNodeInfo()?.nodeNum || 0;`;
  replaceExpected(adminRoutes, marker, `${marker}
        if (req.body.desktopRemoteOnly === true && (!localNodeNum || !Number.isInteger(destinationNodeNum) ||
            destinationNodeNum < 1 || destinationNodeNum >= 0xffffffff || destinationNodeNum === localNodeNum))
            return res.status(409).json({ error: 'Select a remote node distinct from the connected local radio.' });`);
}

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
