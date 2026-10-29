// Push the repository, then generate market/index.json against the real commit
// and push the follow-up commit. Nothing placeholder is ever committed: the
// index only exists after the code commit sha is known.
//
//   GH_TOKEN=ghp_xxx node tools/publish.mjs 991903428 dsh-naiwa-pet
//
// The token is read from the environment, never written into the repo or into
// .git/config; the remote URL stays credential-free.
import { execFileSync } from 'node:child_process';
import dns from 'node:dns';
import fs from 'node:fs';
import https from 'node:https';
import path from 'node:path';
import { buildIndex } from './market-index.mjs';

const [owner, repo] = process.argv.slice(2);
const token = process.env.GH_TOKEN;
const proxy = process.env.GITPROXY || 'http://127.0.0.1:7891';

if (!owner || !repo || !token) {
  console.error('usage: GH_TOKEN=... node tools/publish.mjs <owner> <repo>');
  process.exit(2);
}

const root = path.resolve(import.meta.dirname, '..');
const cleanUrl = `https://github.com/${owner}/${repo}.git`;
const rawBase = `https://raw.githubusercontent.com/${owner}/${repo}/main`;
const gitConfig = [
  '-c', `http.proxy=${proxy}`,
  '-c', 'http.sslBackend=openssl',
  '-c', 'credential.helper=',
];

function git(args, { inherit = false } = {}) {
  return execFileSync('git', [...gitConfig, ...args], {
    cwd: root,
    encoding: 'utf8',
    stdio: inherit ? 'inherit' : 'pipe',
    env: { ...process.env, GIT_TERMINAL_PROMPT: '0' },
  });
}

// GitHub credential goes through a throwaway askpass script so it never lands in
// argv, in .git/config or in the shell history.
const askpass = path.join(process.env.TEMP || process.env.TMP || root, 'dsh-naiwa-askpass.cmd');
fs.writeFileSync(askpass, '@echo off\r\necho %GH_TOKEN%\r\n');
const env = { ...process.env, GIT_ASKPASS: askpass, GIT_TERMINAL_PROMPT: '0' };

function gitAuth(args) {
  return execFileSync('git', [...gitConfig, ...args], { cwd: root, encoding: 'utf8', stdio: 'inherit', env });
}

const resolver = new dns.Resolver();
resolver.setServers(['8.8.8.8', '1.1.1.1']);
const ipCache = new Map();
function resolveIp(host) {
  if (ipCache.has(host)) return Promise.resolve(ipCache.get(host));
  return new Promise((resolve, reject) =>
    resolver.resolve4(host, (err, addrs) => {
      if (err || !addrs?.length) return reject(err || new Error(`no A record for ${host}`));
      ipCache.set(host, addrs[0]);
      resolve(addrs[0]);
    }));
}

async function api(pathname) {
  const host = 'api.github.com';
  const ip = await resolveIp(host);
  return new Promise((resolve, reject) => {
    const req = https.request({
      host: ip,
      port: 443,
      path: pathname,
      method: 'GET',
      servername: host,
      headers: {
        Host: host,
        'User-Agent': 'dsh-naiwa-pet-publish',
        Accept: 'application/vnd.github+json',
        Authorization: `Bearer ${token}`,
      },
      timeout: 60000,
    }, (res) => {
      const chunks = [];
      res.on('data', (chunk) => chunks.push(chunk));
      res.on('end', () => resolve({ status: res.statusCode, body: Buffer.concat(chunks).toString('utf8') }));
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
    req.end();
  });
}

try {
  const who = await api('/user');
  if (who.status !== 200) throw new Error(`token rejected: HTTP ${who.status} ${who.body.slice(0, 200)}`);
  const login = JSON.parse(who.body).login;
  console.log(`token ok: ${login}`);

  const repoInfo = await api(`/repos/${owner}/${repo}`);
  if (repoInfo.status !== 200) throw new Error(`repo ${owner}/${repo} not reachable: HTTP ${repoInfo.status}`);

  if (!git(['remote']).split('\n').includes('origin')) {
    git(['remote', 'add', 'origin', cleanUrl]);
  } else {
    git(['remote', 'set-url', 'origin', cleanUrl]);
  }

  console.log('pushing code...');
  gitAuth(['push', '-u', 'origin', 'main']);
  const codeCommit = git(['rev-parse', 'HEAD']).trim();
  console.log('code commit', codeCommit);

  const index = buildIndex({ owner, repo, commit: codeCommit, publishTimestamp: Math.floor(Date.now() / 1000) });
  const indexFile = path.join(root, 'market', 'index.json');
  fs.mkdirSync(path.dirname(indexFile), { recursive: true });
  fs.writeFileSync(indexFile, JSON.stringify(index, null, 2) + '\n');

  execFileSync('python', ['tools/validate-index.py', 'market/index.json'], { cwd: root, stdio: 'inherit' });

  git(['add', 'market/index.json']);
  git(['-c', 'user.name=dsh-naiwa-pet', '-c', 'user.email=naiwa-pet@users.noreply.github.com',
    'commit', '-m', 'Point the market index at the published commit']);
  console.log('pushing index...');
  gitAuth(['push', 'origin', 'main']);

  const indexCommit = git(['rev-parse', 'HEAD']).trim();
  const check = await fetchRaw(`${rawBase}/market/index.json`);
  console.log(`index url: HTTP ${check.status} (${check.length} bytes)`);
  console.log(JSON.stringify({
    owner,
    repo,
    codeCommit,
    indexCommit,
    indexUrl: `${rawBase}/market/index.json`,
    pluginPath: `plugin/dsh-naiwa-pet`,
  }, null, 2));
} finally {
  fs.rmSync(askpass, { force: true });
}

async function fetchRaw(url) {
  const u = new URL(url);
  const ip = await resolveIp(u.hostname);
  return new Promise((resolve, reject) => {
    const req = https.request({
      host: ip,
      port: 443,
      path: u.pathname,
      method: 'GET',
      servername: u.hostname,
      headers: { Host: u.hostname, 'User-Agent': 'dsh-naiwa-pet-publish' },
      timeout: 60000,
    }, (res) => {
      let length = 0;
      res.on('data', (chunk) => { length += chunk.length; });
      res.on('end', () => resolve({ status: res.statusCode, length }));
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
    req.end();
  });
}
