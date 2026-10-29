/**
 * dsh-naiwa-pet — 把「奶蛙桌宠」装进 DeepSeek Harness。
 *
 * 宿主半边（host half）：负责在 DSH 启动时拉起桌宠、给模型暴露控制与余额查询工具。
 * 桌宠本体是一个独立的 Windows 程序（分层透明窗口 + 素材 + 原版笑声），
 * 放在包的 pet/ 目录里，随插件一起安装，不依赖用户另装。
 *
 * 设计约束：
 * - 只用 node 内置模块，不假设任何 @deepseek-ai/* 依赖可解析；
 * - 工具定义用普通对象注册（name/description/parameters/output/execute）；
 * - 桌宠进程 detached 启动，但盯着 DSH 的心跳文件：DSH 关掉（哪怕强杀）它就自己收走，
 *   不需要用户手动关（stopOnExit 仍可让它立刻退出）；
 * - 每次查询都是「起一个用完即退的探针进程」，不改动正在运行的桌宠状态。
 *
 * @module dsh-naiwa-pet
 */
import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { readFile, rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

export const name = 'naiwa-pet'
export const inject = ['tools', 'webServer']

const here = path.dirname(fileURLToPath(import.meta.url))
const EXE_NAME = '奶蛙桌宠.exe'

/** 桌宠窗口里 State 消息的取值。 */
const STATE_LABEL = {
  0: '站着发呆',
  1: '转头看鼠标',
  2: '正在捧腹大笑',
  3: '笑倒了（躺在地上）',
  4: '全屏应用时藏起来了',
}

const DEFAULTS = {
  autoStart: true,
  exePath: '',
  stopOnExit: true,
}

// 心跳：DSH 活着就每两秒写一次这个文件；DSH 一消失文件就变旧，
// 桌宠盯着它，超过 HEARTBEAT_TIMEOUT 秒没更新就自己收起来。
// 之所以不靠「父进程还在不在」：沙箱令牌下拿不到别的进程句柄，心跳文件谁都能读。
const HEARTBEAT_TIMEOUT = 10
let beatTimer = null

function heartbeatPath() {
  const base = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local')
  return path.join(base, 'NaiwaPetPlus', 'dsh-heartbeat')
}

function beatNow() {
  try {
    const file = heartbeatPath()
    mkdirSync(path.dirname(file), { recursive: true })
    writeFileSync(file, String(Date.now()))
  } catch {}
}

function startHeartbeat() {
  if (beatTimer) return
  beatNow()
  beatTimer = setInterval(beatNow, 2000)
  if (typeof beatTimer.unref === 'function') beatTimer.unref()
}

function stopHeartbeat() {
  if (!beatTimer) return
  clearInterval(beatTimer)
  beatTimer = null
}

function resolveConfig(config) {
  const cfg = { ...DEFAULTS }
  if (config && typeof config === 'object') {
    for (const key of Object.keys(DEFAULTS)) {
      const value = config[key]
      if (value !== undefined && value !== null) cfg[key] = value
    }
  }
  return cfg
}

function petFolder() {
  return path.join(here, 'pet')
}

function exeOf(cfg) {
  const override = typeof cfg.exePath === 'string' ? cfg.exePath.trim() : ''
  return override ? path.resolve(override) : path.join(petFolder(), EXE_NAME)
}

function assertExe(cfg) {
  const exe = exeOf(cfg)
  if (!existsSync(exe)) {
    throw new Error(`找不到奶蛙桌宠程序：${exe}（如果手动移动过，请在插件配置里把 exePath 指向新的位置）`)
  }
  return exe
}

/** 常驻启动：不等待，让桌宠自己活着。 */
function launch(cfg) {
  const exe = assertExe(cfg)
  startHeartbeat()
  // 心跳文件让奶蛙知道 DSH 还活着：DSH 一关（哪怕是被强杀），它就自己收起来。
  const child = spawn(exe, ['--heartbeat', heartbeatPath(), '--heartbeat-timeout', String(HEARTBEAT_TIMEOUT)], {
    cwd: path.dirname(exe),
    detached: true,
    stdio: 'ignore',
    windowsHide: true,
  })
  child.unref()
  return child.pid
}

/**
 * 一次性调用：等程序退出，返回退出码与标准输出。
 *
 * 结果优先走标准输出（`--probe -`）：不落盘就没有 ACL 问题，沙箱里也一样。
 * stdio 用 pipe，桌宠是 GUI 程序但它只在需要时写 stdout，不会卡住。
 */
function runOnce(cfg, args, timeoutMs = 25000) {
  const exe = assertExe(cfg)
  return new Promise(resolve => {
    let settled = false
    let child
    let stdout = ''
    let stderr = ''
    try {
      child = spawn(exe, args, {
        cwd: path.dirname(exe),
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true,
      })
    }
    catch (error) {
      resolve({ code: -1, error: error instanceof Error ? error.message : String(error), stdout: '', stderr: '' })
      return
    }
    const finish = (value) => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      resolve(value)
    }
    const timer = setTimeout(() => {
      try { child.kill() } catch {}
      finish({ code: -1, error: `等待桌宠响应超过 ${Math.round(timeoutMs / 1000)} 秒`, stdout, stderr })
    }, timeoutMs)
    child.stdout.setEncoding('utf8')
    child.stderr.setEncoding('utf8')
    child.stdout.on('data', chunk => { stdout += chunk })
    child.stderr.on('data', chunk => { stderr += chunk })
    child.on('error', error => finish({ code: -1, error: error.message, stdout, stderr }))
    child.on('exit', code => finish({ code: code === null ? -1 : code, stdout, stderr }))
  })
}

/**
 * 兜底：标准输出拿不到时，把结果写进包里 pet/ 目录。
 *
 * 不用 os.tmpdir()——从受限进程建的临时目录桌宠写不进去
 * （UnauthorizedAccessException）；pet/ 是程序自己的目录，沙箱里也可写。
 */
async function withResultFile(cfg, args, timeoutMs = 25000) {
  const file = path.join(path.dirname(exeOf(cfg)), `.ipc-${process.pid}-${Date.now()}.txt`)
  try {
    const run = await runOnce(cfg, [...args, file], timeoutMs)
    if (!existsSync(file)) return { ...run, text: '' }
    const text = await readFile(file, 'utf8')
    return { ...run, text }
  }
  finally {
    await rm(file, { force: true }).catch(() => {})
  }
}

/** 跑一次「命令 + 输出」型子命令，优先 stdout，失败再落文件。 */
async function runReport(cfg, flag, timeoutMs = 25000) {
  const piped = await runOnce(cfg, [flag, '-'], timeoutMs)
  if (piped.stdout && piped.stdout.trim()) {
    return { code: piped.code, error: piped.error, text: piped.stdout, stderr: piped.stderr }
  }
  const filed = await withResultFile(cfg, [flag], timeoutMs)
  return { code: filed.code, error: filed.error, text: filed.text, stderr: filed.stderr, fallback: true }
}

function parseLines(text) {
  const map = {}
  for (const line of String(text).split(/\r?\n/)) {
    const at = line.indexOf('=')
    if (at > 0) map[line.slice(0, at).trim()] = line.slice(at + 1).trim()
  }
  return map
}

/** 读取桌宠当前状态（窗口是否在、可见、位置、动作）。 */
async function probe(cfg) {
  const run = await runReport(cfg, '--probe')
  const text = String(run.text || '').replace(/^\uFEFF/, '')
  if (!text.trim()) {
    return { running: false, missing: true, error: run.error || run.stderr || `桌宠没有响应（退出码 ${run.code}）`, code: run.code }
  }
  const map = parseLines(text)
  const bounds = String(map.Bounds || '').split(',').map(value => Number(value))
  const state = Number(map.State || 0)
  return {
    running: map.Found === 'True',
    visible: map.Visible === 'True',
    bounds,
    state,
    stateLabel: STATE_LABEL[state] || '未知',
  }
}

/** 请求桌宠读取 DSH 登录账号的余额。 */
async function fetchBalance(cfg) {
  const run = await runReport(cfg, '--fetch-balance')
  const raw = String(run.text || '').replace(/^\uFEFF/, '')
  if (!raw.trim()) {
    return { ok: false, error: run.error || run.stderr || `桌宠没有返回结果（退出码 ${run.code}）` }
  }
  const map = parseLines(raw)
  return {
    ok: /^OK\b/.test(raw.trim()),
    mode: map.Mode || '',
    account: map.Account || '',
    currency: map.Currency || 'CNY',
    total: Number(map.Total || 0),
    granted: Number(map.Granted || 0),
    toppedUp: Number(map.ToppedUp || 0),
    error: map.Error || '',
    integrity: map.Integrity || '',
  }
}

function money(currency, amount) {
  const symbol = String(currency || 'CNY').toUpperCase() === 'CNY' ? '¥' : `${currency} `
  return `${symbol}${Number(amount || 0).toFixed(2)}`
}

function describeStatus(info) {
  if (!info || info.missing) {
    return `奶蛙桌宠没有启动${info && info.error ? `（${info.error}）` : ''}。用 naiwa_pet 的 start 可以把它叫出来。`
  }
  if (!info.running) return '奶蛙桌宠当前不在桌面上（进程没在跑，或窗口被关掉了）。'
  const place = Array.isArray(info.bounds) && info.bounds.length === 4
    ? `屏幕位置 ${info.bounds[0]},${info.bounds[1]} – ${info.bounds[2]},${info.bounds[3]}`
    : '位置未知'
  const visible = info.visible ? '可见' : '被全屏应用暂时隐藏'
  return `奶蛙在桌面上：${visible}，${place}，当前动作「${info.stateLabel}」。`
}

async function startPet(cfg) {
  launch(cfg)
  await new Promise(resolve => setTimeout(resolve, 1500))
  return probe(cfg)
}

async function stopPet(cfg) {
  const run = await runOnce(cfg, ['--quit'], 10000)
  if (run.code === 3) return { stopped: false, reason: '本来就没在跑' }
  await new Promise(resolve => setTimeout(resolve, 600))
  return { stopped: true, code: run.code }
}

async function sendAction(cfg, action, done) {
  const before = await probe(cfg)
  if (!before.running) {
    await startPet(cfg)
    await new Promise(resolve => setTimeout(resolve, 400))
  }
  const run = await runOnce(cfg, ['--do', action], 12000)
  if (run.code === 3) {
    return '奶蛙桌宠没有在运行，发送命令失败。先用 start 启动。'
  }
  await new Promise(resolve => setTimeout(resolve, 450))
  return done(run.code)
}

function textTool(name, description, properties, required, run) {
  return {
    name,
    description,
    parameters: {
      type: 'object',
      properties,
      required,
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: { text: { type: 'string' } },
        required: ['text'],
        additionalProperties: false,
      },
      render: (_args, value) => [{ type: 'text', text: String(value && value.text ? value.text : '') }],
    },
    execute: async (args) => ({ text: await run(args && typeof args === 'object' ? args : {}) }),
  }
}

function petTool(cfg) {
  return textTool(
    'naiwa_pet',
    '控制桌面上的奶蛙桌宠：start 叫它出来、stop 取消桌宠、status 看它在哪在做什么、laugh 让它捧腹大笑、poke 戳它肚子、stand 让它从笑倒的状态站起来。奶蛙是 Windows 桌面上的透明小动物，左键点它会笑，右键有菜单。',
    {
      action: {
        type: 'string',
        enum: ['start', 'stop', 'status', 'laugh', 'poke', 'stand'],
        description: '要做的动作。',
      },
    },
    ['action'],
    async (args) => {
      const action = String(args.action || 'status').toLowerCase()
      if (action === 'stop') {
        const result = await stopPet(cfg)
        return result.stopped ? '奶蛙已经从桌面上下来了。' : '奶蛙本来就没在桌面上。'
      }
      if (action === 'status') {
        return describeStatus(await probe(cfg))
      }
      if (action === 'start') {
        const info = await startPet(cfg)
        if (!info.running) {
          return `奶蛙没能启动：${info.error || '程序退出得太快，检查 pet 目录是否完整'}`
        }
        return `奶蛙已经在桌面右下角出现。${describeStatus(info)}`
      }
      if (action === 'laugh') {
        return sendAction(cfg, 'laugh', () => '奶蛙开始捧腹大笑，笑完会自己躺一会儿。')
      }
      if (action === 'poke') {
        return sendAction(cfg, 'poke', () => '戳了一下奶蛙的肚子。')
      }
      if (action === 'stand') {
        return sendAction(cfg, 'stand', () => '奶蛙站起来了（如果它本来就在笑，会先笑完）。')
      }
      return `不认识的动作：${action}`
    },
  )
}

function balanceTool(cfg) {
  return textTool(
    'naiwa_balance',
    '查询当前 DSH 登录的 DeepSeek 账号余额（无需 API Key，直接读本机登录凭据），返回账户名、总余额、赠送与充值明细。可选 showCard 在桌面上把余额卡片也打开。',
    {
      showCard: {
        type: 'boolean',
        description: 'true 时顺便在桌宠头顶弹出余额卡片。',
      },
    },
    [],
    async (args) => {
      if (args.showCard) {
        const info = await probe(cfg)
        if (!info.running) await startPet(cfg)
        await runOnce(cfg, ['--do', 'balance'], 12000)
      }
      const result = await fetchBalance(cfg)
      if (!result.ok) {
        const hint = result.error && result.error.includes('TLS')
          ? '（TLS 被系统拒绝：请从资源管理器或开始菜单正常启动 DSH，不要在受限沙箱里启动）'
          : ''
        return `查不到余额：${result.error || '未知原因'}${hint}`
      }
      const who = result.account ? `账号 ${result.account}` : 'DSH 登录账号'
      const parts = [`${who} 当前余额 ${money(result.currency, result.total)}`]
      if (result.granted > 0 || result.toppedUp > 0) {
        parts.push(`其中赠送 ${money(result.currency, result.granted)}、充值 ${money(result.currency, result.toppedUp)}`)
      }
      if (args.showCard) parts.push('已在桌面上打开余额卡片')
      return parts.join('，') + '。'
    },
  )
}

/**
 * 设置页要用的一组 HTTP 路由。
 *
 * 为什么走 HTTP 而不是 Remote 服务：这一页只服务本机浏览器（同源、无凭据），
 * 而 webServer 是浏览器半端唯一能直接够到宿主的通道；走它就不用把
 * 浏览器半端绑到某个会话，设置页本来也不属于任何会话。
 */
function registerRoutes(ctx, cfg) {
  const json = (res, status, body) => {
    const text = JSON.stringify(body)
    res.writeHead(status, {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': 'no-store',
    })
    res.end(text)
  }

  let iconCache = null

  // 状态查询有个 1.5 秒的小缓存：设置页每 4 秒轮询一次，而每次 probe
  // 都要起一个用完即退的桌宠进程，没必要次次都起。
  let cached = { at: 0, value: null }
  const stateOf = async () => {
    const now = Date.now()
    if (cached.value && now - cached.at < 1500) return cached.value
    const value = await probe(cfg)
    cached = { at: now, value }
    return value
  }

  ctx.effect(() => ctx.webServer.register({
    kind: 'prefix',
    path: '/naiwa-pet/state',
    handler: async (req, res) => {
      if (req.method !== 'GET') {
        json(res, 405, { error: '只支持 GET' })
        return
      }
      try {
        json(res, 200, await stateOf())
      }
      catch (error) {
        json(res, 500, { error: error instanceof Error ? error.message : String(error) })
      }
    },
  }))

  ctx.effect(() => ctx.webServer.register({
    kind: 'prefix',
    path: '/naiwa-pet/action',
    handler: async (req, res) => {
      if (req.method !== 'POST') {
        json(res, 405, { error: '只支持 POST' })
        return
      }
      let body = ''
      try {
        for await (const chunk of req) body += chunk
        const action = String((JSON.parse(body || '{}') || {}).action || '').toLowerCase()
        if (action === 'start') {
          const before = await probe(cfg)
          if (!before.running) {
            launch(cfg)
            await new Promise(resolve => setTimeout(resolve, 1200))
          }
          cached = { at: 0, value: null }
          json(res, 200, { ok: true, state: await probe(cfg) })
          return
        }
        if (action === 'stop') {
          const result = await stopPet(cfg)
          cached = { at: 0, value: null }
          json(res, 200, { ok: true, stopped: result.stopped, state: await probe(cfg) })
          return
        }
        if (action === 'laugh' || action === 'poke' || action === 'stand') {
          const before = await probe(cfg)
          if (!before.running) {
            json(res, 409, { error: '奶蛙不在桌面上' })
            return
          }
          await runOnce(cfg, ['--do', action], 12000)
          json(res, 200, { ok: true })
          return
        }
        json(res, 400, { error: `不认识的动作：${action || '(空)'}` })
      }
      catch (error) {
        json(res, 500, { error: error instanceof Error ? error.message : String(error) })
      }
    },
  }))

  ctx.effect(() => ctx.webServer.register({
    kind: 'prefix',
    path: '/naiwa-pet/icon.png',
    handler: async (req, res) => {
      try {
        if (!iconCache) iconCache = await readFile(path.join(here, 'icon.png'))
        res.writeHead(200, {
          'content-type': 'image/png',
          'content-length': String(iconCache.length),
          'cache-control': 'public, max-age=86400',
        })
        res.end(iconCache)
      }
      catch (error) {
        json(res, 404, { error: error instanceof Error ? error.message : String(error) })
      }
    },
  }))
}

/**
 * 插件入口：注册工具、按需在 DSH 启动时拉起桌宠。
 * @param {object} ctx - 宿主上下文（tools/effect/webServer 等）。
 * @param {object} [config] - cordis.patch.yml 里的 config。
 */
export function apply(ctx, config) {
  const cfg = resolveConfig(config)
  ctx.tools.register(petTool(cfg))
  ctx.tools.register(balanceTool(cfg))
  registerRoutes(ctx, cfg)

  if (cfg.autoStart) {
    ctx.effect(() => {
      const timer = setTimeout(() => {
        try {
          if (!existsSync(exeOf(cfg))) return
          probe(cfg).then((info) => {
            if (!info.running) launch(cfg)
          }, () => {})
        }
        catch {}
      }, 2000)
      return () => {
        clearTimeout(timer)
        stopHeartbeat()
        if (cfg.stopOnExit) {
          runOnce(cfg, ['--quit'], 8000).catch(() => {})
        }
      }
    })
  }
}
