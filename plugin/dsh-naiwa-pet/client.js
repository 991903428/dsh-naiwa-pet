/**
 * dsh-naiwa-pet — 浏览器半端（设置左侧栏里的「奶蛙」一页）
 * ============================================================================
 * 自注册脚本，约定同上一个插件生态里的其他客户端插件：
 *
 *     window.__ModuleLoader__.load({ id: 'dsh-naiwa-pet', factory: (require) => ({ inject, apply }) })
 *
 * 这一页只做一件事：**开关奶蛙**。
 *   · 上面一行显示它现在在不在桌面上、在做什么；
 *   · 中间一个按钮，写着当前能做的那个动作（打开 / 关闭）；
 *   · 下面是两句说明，免得用户以为这就是全部功能。
 *
 * 桌宠本体是宿主半边的独立进程，所以这一页的按钮走宿主注册的 HTTP 路由：
 *     GET  /naiwa-pet/state    → { running, visible, state, stateLabel, bounds }
 *     POST /naiwa-pet/action   → { ok, state... } / { ok:false, error }
 * 同源 fetch，不需要凭据，也不经过会话（设置页不属于任何会话）。
 * ============================================================================
 */
window.__ModuleLoader__.load({
  id: 'dsh-naiwa-pet',
  factory: (require) => {
    const React = require('react')
    const h = React.createElement

    const API = '/naiwa-pet'

    const CSS = `
[data-nw-wrap]{display:flex;flex-direction:column;gap:14px;padding:4px 2px 24px;max-width:620px;}
[data-nw-hero]{display:flex;align-items:center;gap:14px;padding:14px 16px;border-radius:12px;background:rgba(127,127,127,.08);}
[data-nw-face]{width:68px;height:68px;border-radius:14px;object-fit:contain;flex:0 0 auto;background:rgba(127,127,127,.10);}
[data-nw-titles]{display:flex;flex-direction:column;gap:4px;min-width:0;flex:1;}
[data-nw-name]{font-size:16px;font-weight:600;line-height:1.2;}
[data-nw-status]{font-size:13px;opacity:.8;line-height:1.5;}
[data-nw-dot]{display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:6px;vertical-align:middle;background:rgba(127,127,127,.5);}
[data-nw-dot][data-nw-on]{background:#3ec46d;box-shadow:0 0 0 3px rgba(62,196,109,.16);}
[data-nw-dot][data-nw-busy]{background:#e0a03c;}
[data-nw-sec]{display:flex;flex-direction:column;gap:10px;padding:14px 16px;border-radius:12px;background:rgba(127,127,127,.06);}
[data-nw-h]{font-size:13px;font-weight:600;opacity:.85;}
[data-nw-note]{font-size:12px;opacity:.62;line-height:1.6;}
[data-nw-row]{display:flex;align-items:center;gap:12px;flex-wrap:wrap;}
[data-nw-btn]{padding:7px 18px;border-radius:8px;border:1px solid rgba(127,127,127,.35);background:transparent;color:inherit;font-size:13px;cursor:pointer;}
[data-nw-btn]:hover:not([disabled]){background:rgba(127,127,127,.12);}
[data-nw-btn][disabled]{opacity:.55;cursor:default;}
[data-nw-btn][data-nw-primary]{border-color:rgba(62,196,109,.55);background:rgba(62,196,109,.14);}
[data-nw-btn][data-nw-primary]:hover:not([disabled]){background:rgba(62,196,109,.22);}
[data-nw-msg]{font-size:12px;opacity:.8;min-height:16px;line-height:1.5;}
[data-nw-kbd]{font-family:inherit;padding:1px 5px;border-radius:4px;background:rgba(127,127,127,.16);font-size:12px;}
`

    const getJson = async (url, init) => {
      const res = await fetch(url, init)
      const text = await res.text()
      let body = null
      try { body = text ? JSON.parse(text) : null } catch { body = null }
      if (!res.ok) {
        const reason = body && body.error ? body.error : 'HTTP ' + res.status
        throw new Error(reason)
      }
      return body
    }

    function describe(state) {
      if (!state) return { on: false, text: '正在读取桌宠状态…' }
      if (!state.running) return { on: false, text: '不在桌面上' }
      const place = Array.isArray(state.bounds) && state.bounds.length === 4
        ? `屏幕 ${state.bounds[0]},${state.bounds[1]} – ${state.bounds[2]},${state.bounds[3]}`
        : ''
      const doing = state.visible === false ? '被全屏应用暂时藏起来了' : `正在「${state.stateLabel || '发呆'}」`
      return { on: true, text: [doing, place].filter(Boolean).join(' · ') }
    }

    function Section() {
      const [state, setState] = React.useState(null)
      const [busy, setBusy] = React.useState('')
      const [msg, setMsg] = React.useState('')

      const load = React.useCallback(async () => {
        try {
          const next = await getJson(API + '/state')
          setState(next)
        }
        catch (error) {
          setState({ running: false })
          setMsg('读不到桌宠状态：' + (error && error.message ? error.message : String(error)))
        }
      }, [])

      React.useEffect(() => {
        let alive = true
        const tick = () => { if (alive) load() }
        tick()
        const timer = setInterval(tick, 4000)
        return () => { alive = false; clearInterval(timer) }
      }, [load])

      const act = async (action) => {
        setBusy(action)
        setMsg(action === 'start' ? '正在把奶蛙放到桌面上…' : '正在把奶蛙收起来…')
        try {
          const next = await getJson(API + '/action', {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ action }),
          })
          if (next && next.state) setState(next.state)
          setMsg(action === 'start' ? '奶蛙已经在桌面上了。' : '奶蛙已经收起来了。')
        }
        catch (error) {
          setMsg('操作失败：' + (error && error.message ? error.message : String(error)))
          await load()
        }
        finally {
          setBusy('')
        }
      }

      const info = describe(state)
      const on = info.on

      return h('div', { 'data-nw-wrap': '' },
        h('div', { 'data-nw-hero': '' },
          h('img', { 'data-nw-face': '', src: API + '/icon.png', alt: '' }),
          h('div', { 'data-nw-titles': '' },
            h('div', { 'data-nw-name': '' }, '奶蛙桌宠'),
            h('div', { 'data-nw-status': '' },
              h('span', { 'data-nw-dot': '', 'data-nw-on': on ? '' : undefined, 'data-nw-busy': busy ? '' : undefined }),
              info.text))),

        h('div', { 'data-nw-sec': '' },
          h('div', { 'data-nw-h': '' }, on ? '它现在在桌面上' : '它现在不在桌面上'),
          h('div', { 'data-nw-row': '' },
            h('button', {
              'data-nw-btn': '',
              'data-nw-primary': on ? undefined : '',
              disabled: busy ? '' : undefined,
              onClick: () => act(on ? 'stop' : 'start'),
            }, busy === 'start' ? '正在打开…' : busy === 'stop' ? '正在关闭…' : on ? '关闭奶蛙' : '打开奶蛙')),
          h('div', { 'data-nw-note': '' },
            on
              ? '关掉以后它就从桌面上消失了。DSH 退出时它也会自己收起来——被强杀的话最多十秒。'
              : '打开以后它会出现在屏幕右下角，贴着任务栏。')),
        h('div', { 'data-nw-msg': '' }, msg),

        h('div', { 'data-nw-sec': '' },
          h('div', { 'data-nw-h': '' }, '在桌面上还能做什么'),
          h('div', { 'data-nw-note': '' },
            '左键点它：按你的设置在「捧腹大笑」或「戳肚子」之间切换（右键菜单 → 左键动作 里改）。'),
          h('div', { 'data-nw-note': '' },
            '右键点它：', h('span', { 'data-nw-kbd': '' }, '查看余额'), '（自动读当前登录的 DeepSeek 账号）、',
            h('span', { 'data-nw-kbd': '' }, '取消桌宠'), '，还有大小、总在最上面这些。'),
          h('div', { 'data-nw-note': '' },
            '在 DSH 里也可以直接说：让奶蛙笑一个 / 看看余额。')),
      )
    }

    return {
      inject: ['slots'],
      apply(ctx) {
        const style = document.createElement('style')
        style.setAttribute('data-plugin-css', 'dsh-naiwa-pet')
        style.textContent = CSS
        document.head.appendChild(style)
        ctx.effect(() => () => style.remove(), 'dsh-naiwa-pet: 设置页样式')

        const slots = (typeof ctx.get === 'function' ? ctx.get('slots') : undefined) || ctx.slots
        if (!slots) return
        slots.inject('settings.section', () => slots.register(
          { name: 'settings.section', id: 'naiwa-pet', order: 200, label: '奶蛙' },
          () => h(Section),
        ))
      },
    }
  },
})
