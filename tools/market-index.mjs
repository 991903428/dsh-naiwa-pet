// Builds market/index.json for the DSH plugin market (format 1).
// The committed tree has no index.json: the file is generated here once the real
// commit sha exists, so the repository never carries placeholder values.
export function buildIndex({ owner, repo, commit, publishTimestamp }) {
  const site = `https://github.com/${owner}/${repo}`;
  const rawBase = `https://raw.githubusercontent.com/${owner}/${repo}/main`;
  return {
    format: 1,
    title: {
      en: 'Naiwa Pet',
      zh: '奶蛙桌宠',
    },
    description: {
      en: 'A Naiwa desktop pet for Windows: click it to laugh, right-click for balance and settings.',
      zh: 'Windows 桌面上的奶蛙桌宠：左键点它就笑，右键看 DeepSeek 余额、调大小、取消桌宠。',
    },
    metadata: {
      categories: {
        fun: {
          title: { en: 'Fun', zh: '趣味' },
        },
      },
    },
    plugins: [
      {
        name: 'dsh-naiwa-pet',
        title: { en: 'Naiwa Pet', zh: '奶蛙桌宠' },
        description: {
          en: 'A Naiwa desktop pet that follows your DSH session: left-click to laugh, right-click for DeepSeek balance, poke, size and quit. Adds the naiwa_pet and naiwa_balance tools plus a Pet page in Settings.',
          zh: '跟着 DSH 一起上下班的奶蛙桌宠：左键点它就笑（原版笑声），右键看余额、戳肚子、调大小、取消桌宠；附带 naiwa_pet / naiwa_balance 两个工具和设置页里的「奶蛙」一页。',
        },
        icon: `${rawBase}/plugin/dsh-naiwa-pet/icon.png`,
        author: 'dsh-naiwa-pet contributors',
        homepage: site,
        repository: { url: site },
        metadata: {
          category: 'fun',
          keywords: ['desktop-pet', 'naiwa', 'balance'],
        },
        versions: [
          {
            version: '1.0.0',
            publishTimestamp,
            source: {
              type: 'git',
              url: `${site}.git`,
              commit,
              path: 'plugin/dsh-naiwa-pet',
            },
            engines: { dsh: '>=44.0.0' },
          },
        ],
      },
    ],
  };
}
