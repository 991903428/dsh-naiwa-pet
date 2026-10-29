# dsh-naiwa-pet · 奶蛙桌宠（DeepSeek Harness 插件）

把 [yugh6/Naiwa](https://github.com/yugh6/Naiwa) 那只奶蛙搬进 DSH：**DSH 一启动，它就出现在 Windows 桌面上**；左键点它会捧腹大笑（原版笑声，可在菜单里改成戳肚子），右键有菜单——查看余额、左键动作、调大小、总在最上面、取消桌宠。

这个包同时给模型两个工具：

| 工具 | 作用 |
| --- | --- |
| `naiwa_pet` | `start` 叫它出来 / `stop` 取消桌宠 / `status` 看它位置和当前动作 / `laugh` 让它笑 / `poke` 戳肚子 / `stand` 让它站起来 |
| `naiwa_balance` | 查当前 DSH 登录的 DeepSeek 账号余额（账户名、总额、赠送、充值），可选 `showCard` 顺便在桌面弹余额卡片 |

装好以后，**设置 → 奶蛙** 里还多一页：显示它现在在不在桌面上、正在做什么，一个按钮打开 / 关闭它。

## 装

三种都行：

1. **插件市场**：在 DSH 插件市场里添加索引地址 `https://raw.githubusercontent.com/<owner>/<repo>/main/market/index.json`，搜「奶蛙」安装。
2. **本地目录**：`dsh plugin --profile desktop add <repo>/plugin/dsh-naiwa-pet`（或让 agent 用 `plugin_manager` 的 `install_bundle` 指向该目录）。
3. **直接跑**：把 `pet/` 整个拷出来，双击 `奶蛙桌宠.exe`。

装完重启 DSH（或热重载），奶蛙就会自己出来。包内自带素材：`pet/素材.nwp`（动画帧）+ `pet/原版笑声.wav`，不需要额外下载。

## 配

`cordis.patch.yml` 里的三个键：

```yaml
autoStart: true    # 随 DSH 启动自动放出来（已经在桌面就不重复启动）
exePath: ""        # 自定义主程序路径；留空用包内 pet/奶蛙桌宠.exe
stopOnExit: true   # DSH 退出时顺手把桌宠收走（正常退出几秒内）
```

## 桌宠为什么不会在 DSH 关掉后赖着不走

插件每两秒往 `%LOCALAPPDATA%\NaiwaPetPlus\dsh-heartbeat` 写一次时间戳，桌宠启动时带 `--heartbeat <该文件> --heartbeat-timeout 10` 盯着它：心跳文件超过十秒没更新，它就自己关窗走人。

这样即使 DSH 是被强杀的（来不及执行任何清理代码），心跳也会停，桌宠最多十秒自己离开；正常退出时 `stopOnExit` 会直接发一条退出消息，几秒内就收走。

之所以不用「检查父进程还在不在」：在受限令牌里连别的进程的句柄都拿不到，而心跳文件谁都能读。

## 余额为什么不用填 Key

桌宠读 `%USERPROFILE%\.dsh\.credentials.yaml` 里 `deepseek-account-platform/default` 的登录凭据，直接调 `platform.deepseek.com` 的账号接口——和 DSH 界面里显示的余额同源。没登录就显示「未登录」，不需要用户输入任何东西。

想要用别的账号/自建网关时，才去改 `pet/config.json`（`apiKey` 覆盖，或 `provider: custom` + `customUrl`）。

## 设置里那一页

客户端半边（`client.js`）往 `settings.section` 注册一项 `id: naiwa-pet`、`order: 200`、`label: 奶蛙`，页面上只有：状态行（在不在桌面上、屏幕位置、当前动作）+ 一个打开/关闭按钮 + 两句说明。

浏览器够不到宿主，所以按钮走宿主注册的三条同源 HTTP 路由：

| 路由 | 作用 |
| --- | --- |
| `GET /naiwa-pet/state` | 读窗口状态（1.5 秒小缓存，避免每 4 秒轮询都起一个探针进程） |
| `POST /naiwa-pet/action` | `{"action":"start"\|"stop"\|"laugh"\|"poke"\|"stand"}` |
| `GET /naiwa-pet/icon.png` | 页面左上角那只奶蛙 |

## 文件

```
index.js            宿主半边：拉起桌宠 + 注册两个工具 + 设置页的三条路由
client.js           浏览器半边：设置 → 奶蛙 那一页
cordis.patch.yml    插件名单里的一行
pet/                桌宠本体（exe + 26MB 素材 + 原版笑声.wav + 使用说明）
icon.png            图标
LICENCE             原作者许可
```

## 署名与许可

形象、动画、笑声全部来自 [yugh6/Naiwa](https://github.com/yugh6/Naiwa)（作者 yugh6，抖音「握石竹」）。这些素材使用 PolyForm Noncommercial License 1.0.0：**仅限个人非商业使用**，转发请注明作者与原项目地址，不得冒充原创 —— 见 `LICENCE`。

本包新增的代码（`index.js`、`client.js`、`cordis.patch.yml`、`package.json`、`src/`）是 MIT，见仓库根目录 `LICENSE`。
