import { writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';

const [runtime, destination] = process.argv.slice(2).map(value => resolve(value));
process.chdir(runtime);
const load = file => import(pathToFileURL(join(runtime, 'server', file)));
const { loadProtobufDefinitions } = await load('protobufLoader.js');
const { CONFIG_TYPES } = await load('constants/configTypes.js');
const root = await loadProtobufDefinitions();
const device = root.lookupType('meshtastic.Config');
const modules = root.lookupType('meshtastic.ModuleConfig');
const names = {
  lora: 'LoRa', mqtt: 'MQTT', neighborinfo: 'Neighbor info', deviceui: 'Device UI',
  extnotif: 'External notification', storeforward: 'Store and forward', rangetest: 'Range test',
  cannedmsg: 'Canned messages', remotehardware: 'Remote hardware', ambientlighting: 'Ambient lighting',
  detectionsensor: 'Detection sensor', statusmessage: 'Status message', trafficmanagement: 'Traffic management',
  meshbeacon: 'Mesh beacon', tak: 'TAK', paxcounter: 'PAX counter'
};
const label = name => name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/^./, c => c.toUpperCase());
const sections = CONFIG_TYPES.filter(section => section.id !== 'sessionkey').map(section => {
  const group = section.kind === 'module' ? modules : device;
  const field = group.fields[section.field] ?? (section.id === 'deviceui' ? group.fields.deviceUi : undefined);
  if (!field) throw new Error(`Missing protobuf section ${section.id}`);
  const fields = field.resolve().resolvedType.fieldsArray.map(raw => {
    const proto = raw.resolve();
    const values = proto.resolvedType?.values;
    const kind = proto.repeated || proto.map || (proto.resolvedType && !values)
      ? 'json' : values ? 'enum' : proto.type;
    return { key: proto.name, label: label(proto.name), kind,
      ...(values ? { options: values } : {}),
      ...(proto.repeated ? { array: true } : {}) };
  });
  return { ...section, field: field.name, label: names[section.id] ?? label(section.id), fields };
});
writeFileSync(destination, JSON.stringify(sections, null, 2) + '\n');
