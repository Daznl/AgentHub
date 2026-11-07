// Marks the three live-test folders as trusted in ~/.claude.json (additive; everything else is kept), so Claude Code
// starts in them without its "Quick safety check" prompt. Run: node tests/ui/trust-live-folders.js
const fs = require('fs');
const path = require('path');
const file = path.join(process.env.USERPROFILE, '.claude.json');
const base = path.join(process.env.USERPROFILE, 'OneDrive - IGO Limited', 'Desktop', 'agenthub-live-test').replace(/\\/g, '/');
const config = JSON.parse(fs.readFileSync(file, 'utf8'));
config.projects = config.projects || {};
for (const name of ['alpha', 'beta', 'gamma']) {
  const key = `${base}/${name}`;
  config.projects[key] = Object.assign(
    { allowedTools: [], mcpContextUris: [], mcpServers: {}, enabledMcpjsonServers: [], disabledMcpjsonServers: [], hasClaudeMdExternalIncludesApproved: false, hasClaudeMdExternalIncludesWarningShown: false },
    config.projects[key] || {},
    { hasTrustDialogAccepted: true });
  console.log('trusted', key);
}
fs.copyFileSync(file, file + '.bak-' + new Date().toISOString().replace(/[:.]/g, '-'));
fs.writeFileSync(file, JSON.stringify(config, null, 2));
