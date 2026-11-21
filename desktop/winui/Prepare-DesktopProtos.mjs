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
for (const manager of ['adminLoadManager', 'gcManager', 'loManager', 'gdmManager']) {
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

// Device sections 0–7 have GetConfig responses. Only resolve a desktop read
// when this connected source receives the requested section from its own node.
const managerFile = join(dist, 'server', 'meshtasticManager.js');
replaceExpected(managerFile, '    localModuleConfigWaiters = new Map();', `    desktopDeviceWaiters = new Map();
    async desktopReadDevice(configType, nodeNum) {
        const field = ['device','position','power','network','display','lora','bluetooth','security'][configType];
        if (!field || this.desktopDeviceWaiters.has(field)) throw new Error('Readback unavailable or already pending.');
        return new Promise((resolve, reject) => {
            const finish = (section) => {
                clearTimeout(timer);
                this.desktopDeviceWaiters.delete(field);
                if (section == null) reject(new Error('No device reply.'));
                else resolve(section);
            };
            const timer = setTimeout(() => finish(null), 7000);
            this.desktopDeviceWaiters.set(field, { nodeNum, finish });
            this.requestConfig(configType).catch(() => {
                if (this.desktopDeviceWaiters.get(field)?.finish === finish) finish(null);
            });
        });
    }
    localModuleConfigWaiters = new Map();`);
replaceExpected(managerFile, '            if (adminMsg.getModuleConfigResponse) {\n                logger.debug(\'⚙️ Received GetModuleConfigResponse from node\'', `            if (localNodeNum && fromNum === localNodeNum && adminMsg.getConfigResponse) {
                for (const [field, pending] of this.desktopDeviceWaiters) {
                    const response = adminMsg.getConfigResponse[field];
                    if (pending.nodeNum === localNodeNum && response != null) pending.finish(response);
                }
            }
            if (adminMsg.getModuleConfigResponse) {
                logger.debug('⚙️ Received GetModuleConfigResponse from node'`);
replaceExpected(routes, "router.post('/local-module-readback',", `router.post('/local-device-readback', requireDeviceSourcePermission('configuration', 'write', 'body'), async (req, res) => {
    if (!req.user?.isAdmin) return res.status(403).json({ error: 'Admin access required.' });
    const { manager } = getDeviceSourceTarget(req);
    const { configType, nodeId } = req.body;
    if (!Number.isInteger(configType) || configType < 0 || configType > 7 || typeof nodeId !== 'string')
        return res.status(400).json({ error: 'Invalid device section or node ID.' });
    const local = manager.getLocalNodeInfo();
    if (!manager.isDeviceConnected() || !local?.nodeNum || local.nodeId !== nodeId)
        return res.status(409).json({ error: 'The selected local radio changed.' });
    try {
        const section = await manager.desktopReadDevice(configType, local.nodeNum);
        if (!manager.isDeviceConnected() || manager.getLocalNodeInfo()?.nodeId !== nodeId)
            return res.status(409).json({ error: 'The selected radio disconnected.' });
        return res.json({ nodeId, section });
    } catch {
        return res.status(409).json({ error: 'The selected radio did not confirm this device section.' });
    }
});
router.post('/local-module-readback',`);

// Device UI uses separate admin messages. Retain the protobuf bytes of every
// untouched field, including fields introduced by newer device firmware.
replaceExpected(service, '    createSingleFieldAdminMessage(field, value, label, sessionPasskey) {', `    desktopUiBytes(payload, changes) {
        const outer = protobuf.Reader.create(payload);
        let raw;
        while (outer.pos < outer.len) {
            const tag = outer.uint32();
            if (tag === (45 << 3 | 2)) raw = outer.bytes();
            else outer.skipType(tag & 7);
        }
        if (!raw) return null;
        if (!changes) return Buffer.from(raw);
        const type = getProtobufRoot().lookupType('meshtastic.DeviceUIConfig');
        const changed = new Set(Object.keys(changes).map(key => type.fields[key].id));
        const reader = protobuf.Reader.create(raw);
        const kept = [];
        while (reader.pos < reader.len) {
            const start = reader.pos;
            const tag = reader.uint32();
            reader.skipType(tag & 7);
            if (!changed.has(tag >>> 3)) kept.push(raw.subarray(start, reader.pos));
        }
        kept.push(type.encode(type.create(changes)).finish());
        return protobuf.Writer.create().uint32(46 << 3 | 2).bytes(Buffer.concat(kept)).finish();
    }
    createSingleFieldAdminMessage(field, value, label, sessionPasskey) {`);
replaceExpected(managerFile, '    async requestConfig(configType) {', `    async desktopUiReply(nodeNum) {
        if (this.desktopUiPending) throw new Error('A UI read is pending.');
        const payload = protobufService.createSingleFieldAdminMessage('getUiConfigRequest', true, 'Read device UI');
        const packet = protobufService.createAdminPacket(payload,nodeNum,nodeNum);
        return new Promise((resolve,reject) => {
            const finish = response => {
                if (this.desktopUiPending?.finish !== finish) return;
                this.desktopUiPending = null;
                clearTimeout(timer);
                if (response) resolve(response); else reject(new Error('Device UI reply unavailable.'));
            };
            const timer = setTimeout(() => finish(null),7000);
            this.desktopUiPending = {nodeNum,finish};
            this.transport.send(packet).catch(() => finish(null));
        });
    }
    async desktopUiSettings(nodeNum, changes, expected) {
        if (this.desktopUiBusy) throw new Error('Device UI is busy.');
        this.desktopUiBusy = true;
        const same = () => this.isDeviceConnected() && this.getLocalNodeInfo()?.nodeNum === nodeNum;
        try {
            if (!same()) throw new Error('Radio disconnected.');
            let reply = await this.desktopUiReply(nodeNum);
            if (changes) {
                if (!same() || Object.keys(changes).some(key => reply.section[key] !== expected[key]))
                    throw new Error('Device UI settings changed.');
                const encoded = protobufService.desktopUiBytes(reply.payload, changes);
                if (!encoded) throw new Error('Device did not return the stored UI.');
                const ack = await this.sendLocalAdminAwaitRejection(encoded,1500);
                if (ack.rejected || !same()) throw new Error('Device UI store was not accepted.');
                reply = await this.desktopUiReply(nodeNum);
            }
            if (!same()) throw new Error('Radio disconnected.');
            const { pinCode, calibrationData, ...visible } = reply.section;
            return visible;
        } finally { this.desktopUiBusy = false; }
    }
    async requestConfig(configType) {`);
replaceExpected(managerFile, '            if (localNodeNum && fromNum === localNodeNum && adminMsg.getConfigResponse) {', `            if (localNodeNum && fromNum === localNodeNum && this.desktopUiPending?.nodeNum === localNodeNum && adminMsg.getUiConfigResponse)
                this.desktopUiPending.finish({section:adminMsg.getUiConfigResponse,payload});
            if (localNodeNum && fromNum === localNodeNum && adminMsg.getConfigResponse) {`);
replaceExpected(routes, "router.post('/local-device-readback',", `router.post('/local-ui', requireDeviceSourcePermission('configuration', 'write', 'body'), async (req,res) => {
    if (!req.user?.isAdmin) return res.status(403).json({error:'Admin access required.'});
    const {manager} = getDeviceSourceTarget(req);
    const {nodeId,changes,expected} = req.body;
    const local = manager.getLocalNodeInfo();
    if (!local?.nodeNum || local.nodeId !== nodeId) return res.status(409).json({error:'The selected radio changed.'});
    if (changes !== undefined) {
        const limits = {screenBrightness:[1,255],screenTimeout:[0,900],theme:[0,2],ringToneId:[0,4294967295],
            language:[0,31],compassMode:[0,2],screenRgbColor:[0,16777215],gpsFormat:[0,6]};
        const flags = ['alertEnabled','bannerEnabled','isClockfaceAnalog'];
        if (!changes || typeof changes !== 'object' || Array.isArray(changes) || !Object.keys(changes).length ||
            !expected || typeof expected !== 'object' || Object.entries(changes).some(([key,value]) =>
                !Object.hasOwn(expected,key) || (flags.includes(key) ? typeof value !== 'boolean' :
                !Object.hasOwn(limits,key) || !Number.isInteger(value) || value < limits[key][0] || value > limits[key][1] ||
                (key==='language' && value>21 && value<30))))
            return res.status(400).json({error:'Unsupported device UI change.'});
    }
    try { return res.json({nodeId,section:await manager.desktopUiSettings(local.nodeNum,changes,expected)}); }
    catch(error) { return res.status(409).json({error:error.message}); }
});
router.post('/local-device-readback',`);


// The health endpoint may be available before the bootstrap restore finishes.
const serverEntry = join(dist, 'server', 'server.js');
replaceExpected(serverEntry, '// This allows createAdminIfNeeded() to proceed\n        systemRestoreService.markRestoreComplete();', `// This allows createAdminIfNeeded() to proceed
        systemRestoreService.markRestoreComplete();
        if (process.env.MESHMONITOR_DESKTOP_RESTORE === '1' && process.env.RESTORE_FROM_BACKUP) {
            const fs = await import('node:fs/promises');
            const path = await import('node:path');
            await fs.writeFile(path.join(process.env.DATA_DIR, '.desktop-restore-finished'), 'done');
        }`);
