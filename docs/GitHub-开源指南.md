# GitHub 开源指南 —— PEAK Music Player

> 目的：源码透明化（扫码登录 / cookie 只存本地，让玩家自己审查代码），
> 并把仓库地址填进 Thunderstore manifest 的 `website_url`。

## 〇、当前状态（本机已就绪）

| 项目 | 状态 |
|---|---|
| 本地仓库 | ✅ 已 `git init`（分支 `main`） |
| `.gitignore` | ✅ 已配置（bin/obj、日志、二维码、大素材不入库） |
| 敏感信息扫描 | ✅ 无真实 cookie / token（只有用法占位示例） |
| 提交历史 | ✅ `dab7ebf` r43 源码入库；`6849103` r44 取流修复 |
| git 身份 | ✅ `pht5740 <297386195+pht5740@users.noreply.github.com>` |
| 远程仓库 | ⬜ 待在 GitHub 创建（下面步骤一） |
| 关联 + 推送 | ⬜ 待执行（下面步骤二/三） |

## 一、创建远程仓库（二选一）

### 方式 A：网页创建（30 秒，推荐）

1. 打开 https://github.com/new （先登录你的 GitHub 账号）
2. **Repository name** 填 `PEAK-Music-Player`（与显示名一致）
3. 选 **Public**（透明化就是要给别人看代码，必须公开）
4. **不要勾选** "Add a README file" / ".gitignore" / "Choose a license"
   —— 本地已有提交历史，勾了会多出一个空提交还要先合并，直接留空最干净
5. 点 **Create repository**，复制页面上的地址：
   `https://github.com/pht5740/PEAK-Music-Player.git`

### 方式 B：GitHub CLI 命令行

```powershell
winget install --id GitHub.cli          # 装 gh
gh auth login                           # 浏览器授权一次
cd C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\PeakMusic
gh repo create PEAK-Music-Player --public --source=. --push
```

`--source=. --push` 会自动建仓库、加 remote、推当前分支，一步到位。

## 二、关联远程地址

```powershell
cd C:\Users\Admin\WorkBuddy\2026-11-07-20-32-32\PeakMusic
git remote add origin https://github.com/pht5740/PEAK-Music-Player.git
git remote -v        # 核对：fetch/push 都指向上面的地址
```

> 如果远程建好后想改仓库名/地址：`git remote set-url origin <新地址>`

## 三、提交 + 推送

```powershell
# 日常改动流程
git add -A
git commit -m "说明这次改了什么"
git push -u origin main     # -u 只在第一次加，建立 upstream 关联
# 之后只需
git push
```

`-u origin main` 之后，`git push` / `git pull` 不用再带参数。

## 四、凭据配置（推送时会用到）

HTTPS 推送需要向 GitHub 证明身份，三选一：

1. **Git Credential Manager（Windows 自带，推荐）**：第一次 `git push` 时
   自动弹浏览器登录框，登录一次后凭据存在 Windows 凭据管理器，以后免输。
   本机已配好 `credential.helper=manager`，无需额外设置。
2. **Personal Access Token（PAT）**：GitHub → Settings → Developer settings →
   Personal access tokens → Fine-grained token（勾 Contents: Read and write），
   推送提示输密码时粘贴 token。
3. **SSH 密钥**（不想走 HTTPS 才需要）：
   ```powershell
   ssh-keygen -t ed25519 -C "297386195+pht5740@users.noreply.github.com"
   # 把 ~/.ssh/id_ed25519.pub 全文复制到 GitHub → Settings → SSH keys
   git remote set-url origin git@github.com:pht5740/PEAK-Music-Player.git
   ```

## 五、注意事项

1. **敏感信息永远不入库**：cookie、token、账号密码。入库前自查：
   `git grep -n "MUSIC_U=.\{10,\}"` 之类查真实值（用法占位如 `MUSIC_U=xxx` 无碍）。
   `.gitignore` 已排除 `_*.txt`、`*.log`、`login-qr*.png`、`app-qr.png`。
2. **大文件**：测试音频 `testmedia/test-song.mp3` 已被排除；以后若要提交
   大资源（音频包、视频），用 Git LFS，别直接塞 git。
3. **分支与标签**：主分支 `main`；发布新版本时打 tag，方便对照 Thunderstore 版本：
   ```powershell
   git tag v1.0.0 && git push origin v1.0.0
   ```
4. **Thunderstore 关联**：打包上架时把 manifest 的 `website_url` 指向本仓库，
   README 里也放链接——这就是"透明化"入口，玩家点进去自己看登录/cookie 代码。
5. **License（可选）**：开源仓库建议补一个 LICENSE 文件（如 MIT），
   否则别人只有"看"的权利没有"用"的权利。确定后：
   GitHub 创建仓库时顺手选一个，或本地放 LICENSE 文件再提交。
6. **Windows 换行警告**（LF will be replaced by CRLF）无害，可忽略；
   想统一可加 `.gitattributes`：`* text=auto eol=lf`。
