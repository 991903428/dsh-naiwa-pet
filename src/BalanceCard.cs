using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NaiwaDesktop
{
    // ---------------------------------------------------------------- config

    internal sealed class BalanceConfig
    {
        internal string Provider = "deepseek";
        internal string ApiKey = "";
        internal string BaseUrl = "https://api.deepseek.com";
        internal string Currency = "CNY";
        internal string CustomUrl = "";
        internal string CustomHeader = "Authorization";
        internal string CustomHeaderValue = "Bearer {key}";
        internal string CustomJsonPath = "";
        internal string CustomLabel = "自定义接口";

        internal static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); } }

        internal static BalanceConfig Load()
        {
            BalanceConfig c = new BalanceConfig();
            try
            {
                string env = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
                if (!string.IsNullOrEmpty(env)) c.ApiKey = env.Trim();
                if (!File.Exists(FilePath)) return c;
                string text = File.ReadAllText(FilePath, Encoding.UTF8);
                Dictionary<string, object> root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                Dictionary<string, object> b = Node(root, "balance") as Dictionary<string, object>;
                if (b == null) b = root;
                c.Provider = Str(b, "provider", c.Provider).Trim().ToLowerInvariant();
                c.ApiKey = Str(b, "apiKey", c.ApiKey).Trim();
                c.BaseUrl = Str(b, "baseUrl", c.BaseUrl).Trim();
                c.Currency = Str(b, "currency", c.Currency).Trim();
                c.CustomUrl = Str(b, "customUrl", c.CustomUrl).Trim();
                c.CustomHeader = Str(b, "customHeader", c.CustomHeader).Trim();
                c.CustomHeaderValue = Str(b, "customHeaderValue", c.CustomHeaderValue).Trim();
                c.CustomJsonPath = Str(b, "customJsonPath", c.CustomJsonPath).Trim();
                c.CustomLabel = Str(b, "customLabel", c.CustomLabel).Trim();
                if (string.IsNullOrEmpty(c.ApiKey))
                {
                    string env2 = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
                    if (!string.IsNullOrEmpty(env2)) c.ApiKey = env2.Trim();
                }
            }
            catch { }
            return c;
        }

        /// <summary>
        /// Which credential this card uses. A key in config.json wins, then the account
        /// DeepSeek Harness is already signed in with, then a custom endpoint.
        /// </summary>
        internal string Mode
        {
            get
            {
                if (Provider == "custom" && CustomUrl.Length > 0) return "custom";
                if (ApiKey.Length > 0) return "apikey";
                if (Provider == "custom") return "none";
                return DshAccount.Find().Ready ? "dsh" : "none";
            }
        }

        internal bool Configured { get { return Mode != "none"; } }

        internal string Label
        {
            get
            {
                string mode = Mode;
                if (mode == "custom") return CustomLabel.Length > 0 ? CustomLabel : "自定义";
                if (mode == "dsh") return "DSH 账号";
                return "DeepSeek";
            }
        }

        internal static void EnsureTemplate()
        {
            try
            {
                if (File.Exists(FilePath)) return;
                string[] lines = new string[] {
                    "{",
                    "  \"balance\": {",
                    "    \"provider\": \"auto\",",
                    "    \"apiKey\": \"\",",
                    "",
                    "    \"baseUrl\": \"https://api.deepseek.com\",",
                    "    \"currency\": \"CNY\",",
                    "",
                    "    \"customUrl\": \"\",",
                    "    \"customLabel\": \"自定义接口\",",
                    "    \"customHeader\": \"Authorization\",",
                    "    \"customHeaderValue\": \"Bearer {key}\",",
                    "    \"customJsonPath\": \"\"",
                    "  }",
                    "}",
                    "" };
                string body = string.Join(Environment.NewLine, lines);
                File.WriteAllText(FilePath, body, new UTF8Encoding(false));
            }
            catch { }
        }

        internal static object Node(object root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return root;
            object current = root;
            foreach (string raw in path.Split('.'))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                Dictionary<string, object> map = current as Dictionary<string, object>;
                if (map != null)
                {
                    if (!map.ContainsKey(part)) return null;
                    current = map[part];
                    continue;
                }
                IList<object> list = current as IList<object>;
                if (list != null)
                {
                    int index;
                    if (!int.TryParse(part, out index) || index < 0 || index >= list.Count) return null;
                    current = list[index];
                    continue;
                }
                return null;
            }
            return current;
        }

        internal static string Str(Dictionary<string, object> map, string key, string fallback)
        {
            if (map == null || !map.ContainsKey(key)) return fallback;
            object value = map[key];
            return value == null ? fallback : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        internal static double Num(object value)
        {
            if (value == null) return double.NaN;
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is decimal) return (double)(decimal)value;
            double d;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return double.NaN;
        }
    }

    // ---------------------------------------------------------------- service

    internal sealed class BalanceResult
    {
        internal bool Ok;
        internal bool Pending;
        internal bool NoKey;
        internal string Error = "";
        internal string Currency = "CNY";
        internal double Total;
        internal double Granted;
        internal double ToppedUp;
        internal bool HasBreakdown;
        internal string Account = "";
        internal DateTime At = DateTime.Now;

        internal static BalanceResult Loading() { BalanceResult r = new BalanceResult(); r.Pending = true; return r; }
        internal static BalanceResult MissingKey() { BalanceResult r = new BalanceResult(); r.NoKey = true; return r; }
    }

    internal static class BalanceService
    {
        private static readonly object gate = new object();

        static BalanceService()
        {
            // The platform endpoints reject the TLS 1.0 default of the .NET 4 runtime.
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
            }
            catch (Exception)
            {
            }
        }

        internal static void Fetch(BalanceConfig config, Action<BalanceResult> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                BalanceResult result;
                try { result = FetchSync(config); }
                catch (Exception e) { result = new BalanceResult(); result.Error = Friendly(e); }
                result.At = DateTime.Now;
                try { done(result); } catch { }
            });
        }

        internal static BalanceResult FetchSync(BalanceConfig config)
        {
            string mode = config.Mode;
            if (mode == "none") return BalanceResult.MissingKey();
            if (mode == "dsh") return FetchDsh(config);

            string url;
            string headerName = null, headerValue = null;
            if (mode == "custom")
            {
                url = config.CustomUrl;
                if (config.CustomHeader.Length > 0)
                {
                    headerName = config.CustomHeader;
                    headerValue = config.CustomHeaderValue.Replace("{key}", config.ApiKey);
                }
            }
            else
            {
                string root = config.BaseUrl.TrimEnd('/');
                if (root.Length == 0) root = "https://api.deepseek.com";
                url = root + "/user/balance";
                headerName = "Authorization";
                headerValue = "Bearer " + config.ApiKey;
            }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.UserAgent = "NaiwaPet/1.0";
            request.Accept = "application/json";
            if (!string.IsNullOrEmpty(headerName)) request.Headers[headerName] = headerValue;

            string text;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    text = reader.ReadToEnd();
            }
            catch (WebException e)
            {
                HttpWebResponse response = e.Response as HttpWebResponse;
                if (response != null)
                {
                    string body = "";
                    try { using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) body = reader.ReadToEnd(); }
                    catch { }
                    int code = (int)response.StatusCode;
                    BalanceResult failed = new BalanceResult();
                    failed.Error = code == 401 || code == 403
                        ? "API Key 无效或无权限（HTTP " + code + "）"
                        : "接口返回 HTTP " + code + Detail(body);
                    return failed;
                }
                BalanceResult offline = new BalanceResult();
                offline.Error = e.Status == WebExceptionStatus.Timeout ? "请求超时，请检查网络" : "连不上接口：" + e.Message;
                return offline;
            }

            return Parse(config, text);
        }

        internal static BalanceResult Parse(BalanceConfig config, string text)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object root;
            try { root = serializer.DeserializeObject(text); }
            catch
            {
                BalanceResult bad = new BalanceResult();
                bad.Error = "返回内容不是 JSON：" + Clip(text);
                return bad;
            }

            BalanceResult result = new BalanceResult();
            result.Ok = true;

            if (config.Mode == "dsh") return ParseDsh(config, text);

            if (config.Provider != "custom")
            {
                Dictionary<string, object> map = root as Dictionary<string, object>;
                IList<object> infos = map != null ? map.ContainsKey("balance_infos") ? map["balance_infos"] as IList<object> : null : null;
                if (infos == null || infos.Count == 0)
                {
                    result.Ok = false;
                    result.Error = "接口没有返回余额字段：" + Clip(text);
                    return result;
                }
                Dictionary<string, object> picked = null;
                foreach (object entry in infos)
                {
                    Dictionary<string, object> item = entry as Dictionary<string, object>;
                    if (item == null) continue;
                    if (picked == null) picked = item;
                    if (string.Equals(BalanceConfig.Str(item, "currency", ""), config.Currency, StringComparison.OrdinalIgnoreCase)) { picked = item; break; }
                }
                result.Currency = BalanceConfig.Str(picked, "currency", config.Currency).ToUpperInvariant();
                result.Total = BalanceConfig.Num(BalanceConfig.Node(picked, "total_balance"));
                result.Granted = BalanceConfig.Num(BalanceConfig.Node(picked, "granted_balance"));
                result.ToppedUp = BalanceConfig.Num(BalanceConfig.Node(picked, "topped_up_balance"));
                result.HasBreakdown = !double.IsNaN(result.Granted) || !double.IsNaN(result.ToppedUp);
                if (double.IsNaN(result.Total))
                {
                    result.Ok = false;
                    result.Error = "余额字段无法识别：" + Clip(text);
                }
                return result;
            }

            object node = string.IsNullOrEmpty(config.CustomJsonPath) ? root : BalanceConfig.Node(root, config.CustomJsonPath);
            if (node == null && config.CustomJsonPath.Length > 0)
            {
                result.Ok = false;
                result.Error = "找不到 JSON 路径 " + config.CustomJsonPath;
                return result;
            }
            if (node is Dictionary<string, object> || node is IList<object>)
            {
                // Best effort: look for a numeric field, unwrap a single data/result wrapper first.
                Dictionary<string, object> map = node as Dictionary<string, object>;
                if (map != null)
                {
                    string[] wrappers = { "data", "result", "payload", "balance" };
                    foreach (string name in wrappers)
                        if (map.ContainsKey(name) && (map[name] is Dictionary<string, object>)) { map = map[name] as Dictionary<string, object>; break; }
                    string[] names = { "total_balance", "total", "balance", "amount", "available", "credit", "remain" };
                    foreach (string name in names)
                        if (map.ContainsKey(name)) { node = map[name]; break; }
                }
            }
            double value = BalanceConfig.Num(node);
            if (double.IsNaN(value))
            {
                result.Ok = false;
                result.Error = "无法从返回内容中取出余额：" + Clip(text);
                return result;
            }
            result.Total = value;
            result.Currency = config.Currency;
            return result;
        }

        /// <summary>Balance of the account DeepSeek Harness is already signed in with.</summary>
        private static BalanceResult FetchDsh(BalanceConfig config)
        {
            DshAccount.Invalidate();
            DshCredential credential = DshAccount.Find();
            if (!credential.Ready) return BalanceResult.MissingKey();

            string root = credential.Issuer.TrimEnd('/');
            string text, error;
            int status;
            if (!DshAccount.TryGet(root + "/api/v0/users/get_user_summary", credential.Token, out text, out status, out error))
            {
                BalanceResult failed = new BalanceResult();
                failed.Error = status == 401 || status == 403 || LooksUnauthorized(text)
                    ? "登录已过期，请在 DeepSeek Harness 里重新登录"
                    : error;
                return failed;
            }

            BalanceResult result = ParseDsh(config, text);
            if (result.Ok)
            {
                string profile, profileError;
                int profileStatus;
                if (DshAccount.TryGet(root + "/auth-api/v0/users/current", credential.Token, out profile, out profileStatus, out profileError))
                    result.Account = AccountLabel(profile);
            }
            return result;
        }

        /// <summary>Parse the Platform wallet envelope {code,data:{biz_code,biz_data:{normal_wallets,bonus_wallets}}}.</summary>
        internal static BalanceResult ParseDsh(BalanceConfig config, string text)
        {
            BalanceResult result = new BalanceResult();
            object root;
            try { root = new JavaScriptSerializer().DeserializeObject(text); }
            catch
            {
                result.Error = "返回内容不是 JSON：" + Clip(text);
                return result;
            }

            Dictionary<string, object> map = root as Dictionary<string, object>;
            if (map == null)
            {
                result.Error = "返回内容不是 JSON 对象：" + Clip(text);
                return result;
            }
            if (BalanceConfig.Num(BalanceConfig.Node(map, "code")) == 40003 || LooksUnauthorized(text))
            {
                result.Error = "登录已过期，请在 DeepSeek Harness 里重新登录";
                return result;
            }

            Dictionary<string, object> data = BalanceConfig.Node(map, "data") as Dictionary<string, object>;
            double biz = data == null ? double.NaN : BalanceConfig.Num(BalanceConfig.Node(data, "biz_code"));
            if (!double.IsNaN(biz) && biz != 0)
            {
                result.Error = "接口返回错误 " + biz.ToString("0") + "：" + BalanceConfig.Str(data, "biz_msg", "").Trim();
                return result;
            }
            Dictionary<string, object> body = data == null ? null : BalanceConfig.Node(data, "biz_data") as Dictionary<string, object>;
            if (body == null)
            {
                result.Error = "接口没有返回余额字段：" + Clip(text);
                return result;
            }

            string currency = config.Currency;
            double topped = SumWallets(BalanceConfig.Node(body, "normal_wallets"), ref currency);
            double granted = SumWallets(BalanceConfig.Node(body, "bonus_wallets"), ref currency);
            if (double.IsNaN(topped) && double.IsNaN(granted))
            {
                result.Error = "余额字段无法识别：" + Clip(text);
                return result;
            }

            result.Currency = currency.ToUpperInvariant();
            result.ToppedUp = double.IsNaN(topped) ? 0 : topped;
            result.Granted = double.IsNaN(granted) ? 0 : granted;
            result.Total = result.ToppedUp + result.Granted;
            result.HasBreakdown = true;
            result.Ok = true;
            return result;
        }

        /// <summary>Sum one wallet list, moving the reported currency to the account's own when it differs.</summary>
        private static double SumWallets(object node, ref string currency)
        {
            IList<object> wallets = node as IList<object>;
            if (wallets == null || wallets.Count == 0) return double.NaN;
            double sum = WalletTotal(wallets, currency);
            if (!double.IsNaN(sum)) return sum;
            string fallback = WalletCurrency(wallets);
            if (fallback.Length == 0 || string.Equals(fallback, currency, StringComparison.OrdinalIgnoreCase)) return double.NaN;
            currency = fallback;
            return WalletTotal(wallets, fallback);
        }

        private static double WalletTotal(IList<object> wallets, string currency)
        {
            double sum = double.NaN;
            bool hit = false;
            foreach (object entry in wallets)
            {
                Dictionary<string, object> wallet = entry as Dictionary<string, object>;
                if (wallet == null) continue;
                if (!string.Equals(BalanceConfig.Str(wallet, "currency", "").Trim(), currency, StringComparison.OrdinalIgnoreCase)) continue;
                double amount = BalanceConfig.Num(BalanceConfig.Node(wallet, "balance"));
                if (double.IsNaN(amount)) continue;
                sum = double.IsNaN(sum) ? amount : sum + amount;
                hit = true;
            }
            return hit ? sum : double.NaN;
        }

        private static string WalletCurrency(IList<object> wallets)
        {
            foreach (object entry in wallets)
            {
                Dictionary<string, object> wallet = entry as Dictionary<string, object>;
                if (wallet == null) continue;
                string code = BalanceConfig.Str(wallet, "currency", "").Trim();
                if (code.Length > 0) return code;
            }
            return "";
        }

        /// <summary>Display name for the signed-in account, falling back to its contact.</summary>
        private static string AccountLabel(string text)
        {
            try
            {
                Dictionary<string, object> map = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                Dictionary<string, object> body = BalanceConfig.Node(map, "data.biz_data") as Dictionary<string, object>;
                if (body == null) return "";
                string name = BalanceConfig.Str(BalanceConfig.Node(body, "id_profile") as Dictionary<string, object>, "name", "").Trim();
                if (name.Length > 0) return name;
                return BalanceConfig.Str(body, "email", "").Trim();
            }
            catch { return ""; }
        }

        private static bool LooksUnauthorized(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("\"code\":40003", StringComparison.Ordinal) >= 0
                || text.IndexOf("\"code\": 40003", StringComparison.Ordinal) >= 0;
        }

        private static string Detail(string body)
        {
            string clip = Clip(body);
            return clip.Length == 0 ? "" : "：" + clip;
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 90 ? text.Substring(0, 90) + "…" : text;
        }

        private static string Friendly(Exception e)
        {
            WebException web = e as WebException;
            if (web == null) return e.GetType().Name + "：" + e.Message;
            string text = web.Message;
            if (web.InnerException != null) text = text + " " + web.InnerException.Message;
            if (text.IndexOf("安全包") >= 0 || text.IndexOf("SSL") >= 0 || text.IndexOf("TLS") >= 0 || text.IndexOf("基础连接") >= 0)
                return "TLS 连接被系统拒绝，请检查网络代理或系统时间，或从桌面双击启动程序";
            if (web.Status == WebExceptionStatus.NameResolutionFailure) return "解析不到平台域名，请检查网络";
            if (web.Status == WebExceptionStatus.Timeout) return "连接平台超时，请稍后重试";
            return web.Message;
        }
    }

    // ---------------------------------------------------------------- card

    internal sealed class BalanceCard : Form
    {
        private readonly Assets assets;
        private readonly LayeredSurface surface = new LayeredSurface();
        private readonly float ui;
        private readonly int width, height;
        private BalanceResult result = BalanceResult.Loading();
        private Rectangle refreshRect, closeRect;
        internal Rectangle RefreshButton { get { return refreshRect; } }
        internal Rectangle CloseButton { get { return closeRect; } }
        private bool unloading;

        internal event EventHandler RefreshRequested;
        internal event EventHandler CloseRequested;

        internal BalanceCard(Assets assets)
        {
            this.assets = assets;
            ui = 1f;
            using (Graphics probe = Graphics.FromHwnd(IntPtr.Zero)) ui = Math.Max(1f, probe.DpiX / 96f);
            width = S(384);
            height = S(210);
            Text = Native.Title + ".Balance";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            ClientSize = new Size(width, height);
            Cursor = Cursors.Hand;
        }

        internal LayeredSurface Surface { get { return surface; } }
        internal float Ui { get { return ui; } }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= 0x80000 | 0x80 | 0x08000000;
                p.ClassStyle |= 8;
                return p;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84)
            {
                // WM_NCHITTEST carries screen coordinates; the surface is indexed in client space.
                long value = m.LParam.ToInt64();
                Point screen = new Point((short)(value & 0xffff), (short)((value >> 16) & 0xffff));
                Point client = PointToClient(screen);
                m.Result = surface.Hit(client.X, client.Y) ? (IntPtr)1 : new IntPtr(-1);
                return;
            }
            base.WndProc(ref m);
        }

        internal void SetResult(BalanceResult value)
        {
            result = value;
            Render();
        }

        internal void Render()
        {
            if (!IsHandleCreated || unloading) return;
            using (Bitmap image = Draw())
                surface.Update(Handle, image, Location);
        }

        internal void Reposition() { }

        internal void ShowNear(Rectangle pet)
        {
            Rectangle area = Screen.FromRectangle(pet).WorkingArea;
            int x = pet.Left + pet.Width / 2 - width / 2;
            int y = pet.Top - height - S(10);
            if (y < area.Top + 8) y = pet.Bottom + S(10);
            x = Math.Max(area.Left + 8, Math.Min(x, area.Right - width - 8));
            y = Math.Max(area.Top + 8, Math.Min(y, area.Bottom - height - 8));
            Location = new Point(x, y);
            if (!Visible) Show();
            Render();
        }

        // ---- drawing -------------------------------------------------------

        private int S(float value) { return (int)Math.Round(value * ui); }
        private float F(float value) { return value * ui; }
        private Font font(string name, float size, FontStyle style) { return new Font(name, F(size), style, GraphicsUnit.Point); }

        private static string CurrencySign(string code)
        {
            if (string.IsNullOrEmpty(code)) return "¥";
            code = code.ToUpperInvariant();
            if (code == "CNY" || code == "RMB") return "¥";
            if (code == "USD") return "$";
            if (code == "EUR") return "€";
            if (code == "JPY") return "¥";
            return code + " ";
        }

        internal Bitmap Draw()
        {
            Bitmap canvas = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                Rectangle shadow = new Rectangle(S(3), S(6), width - S(6), height - S(12));
                using (GraphicsPath path = Rounded(shadow, S(16)))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(64, 0, 0, 0)))
                    g.FillPath(brush, path);

                Rectangle card = new Rectangle(S(2), S(2), width - S(4), height - S(9));
                using (GraphicsPath path = Rounded(card, S(14)))
                {
                    using (LinearGradientBrush fill = new LinearGradientBrush(new Rectangle(card.X, card.Y, card.Width, card.Height + 2), Color.FromArgb(246, 32, 36, 43), Color.FromArgb(246, 22, 25, 30), 90f))
                        g.FillPath(fill, path);
                    using (Pen border = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, F(1f))))
                        g.DrawPath(border, path);
                }

                // soft top highlight
                using (GraphicsPath path = Rounded(new Rectangle(S(2), S(2), width - S(4), S(60)), S(14)))
                using (LinearGradientBrush glow = new LinearGradientBrush(new Rectangle(S(2), S(2), width - S(4), S(60)), Color.FromArgb(26, 255, 214, 140), Color.FromArgb(0, 255, 214, 140), 90f))
                    g.FillPath(glow, path);

                // pet thumbnail
                Rectangle icon = new Rectangle(S(18), S(24), S(56), S(72));
                g.DrawImage(assets.Eye(0), icon);

                string title = "奶蛙 · 余额";
                string tag = result != null && result.NoKey ? "未配置" : Label;
                using (Font titleFont = font("Microsoft YaHei UI", 12.5f, FontStyle.Bold))
                using (Font tagFont = font("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
                using (SolidBrush white = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, 170, 180, 195)))
                {
                    g.DrawString(title, titleFont, white, S(88), S(18));
                    SizeF tagSize = g.MeasureString(tag, tagFont);
                    Rectangle tagBox = new Rectangle((int)(width - S(18) - tagSize.Width - S(14)), S(24), (int)(tagSize.Width + S(14)), S(20));
                    using (GraphicsPath path = Rounded(tagBox, S(10)))
                    using (SolidBrush chip = new SolidBrush(Color.FromArgb(38, 255, 214, 140)))
                        g.FillPath(chip, path);
                    using (SolidBrush warm = new SolidBrush(Color.FromArgb(230, 255, 205, 120)))
                        g.DrawString(tag, tagFont, warm, tagBox.X + S(7), tagBox.Y + S(4));
                }

                string headline;
                string detail;
                string status;
                Color headlineColor = Color.FromArgb(255, 255, 255, 255);
                Color statusColor = Color.FromArgb(140, 165, 175, 190);

                if (result == null || result.Pending)
                {
                    headline = "查询中…";
                    detail = "";
                    status = Label + " · 读取中…";
                }
                else if (result.NoKey)
                {
                    headline = "未登录";
                    detail = "在 DeepSeek Harness 里登录 DeepSeek 账号";
                    status = "也可以用 config.json 的 apiKey";
                    headlineColor = Color.FromArgb(255, 230, 190, 120);
                    statusColor = Color.FromArgb(150, 165, 175, 190);
                }
                else if (!result.Ok)
                {
                    headline = "获取失败";
                    detail = result.Error;
                    status = "右键关闭，左键重试";
                    headlineColor = Color.FromArgb(255, 255, 138, 128);
                    statusColor = Color.FromArgb(150, 255, 160, 150);
                }
                else
                {
                    headline = CurrencySign(result.Currency) + result.Total.ToString("0.00");
                    detail = result.HasBreakdown
                        ? "赠送 " + Safe(result.Granted) + "   充值 " + Safe(result.ToppedUp)
                        : "余额同步正常";
                    status = result.Account.Length > 0
                        ? result.Account + " · 更新于 " + result.At.ToString("MM-dd HH:mm")
                        : "更新于 " + result.At.ToString("MM-dd HH:mm:ss");
                }

                using (Font headFont = font("Microsoft YaHei UI", 22f, FontStyle.Bold))
                using (Font detailFont = font("Microsoft YaHei UI", 9f, FontStyle.Regular))
                using (Font statusFont = font("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
                using (SolidBrush headBrush = new SolidBrush(headlineColor))
                using (SolidBrush detailBrush = new SolidBrush(Color.FromArgb(200, 205, 212, 222)))
                using (SolidBrush statusBrush = new SolidBrush(statusColor))
                using (StringFormat oneLine = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                {
                    g.DrawString(headline, headFont, headBrush, S(86), S(38));
                    if (detail.Length > 0)
                        g.DrawString(detail, detailFont, detailBrush, new RectangleF(S(88), S(88), width - S(106), S(20)), oneLine);
                    g.DrawString(status, statusFont, statusBrush, new RectangleF(S(88), S(108), width - S(106), S(18)), oneLine);
                }

                using (Pen line = new Pen(Color.FromArgb(46, 255, 255, 255), Math.Max(1f, F(1f))))
                    g.DrawLine(line, S(18), S(136), width - S(18), S(136));

                // buttons
                closeRect = new Rectangle(width - S(18) - S(58), S(158), S(58), S(28));
                refreshRect = new Rectangle(closeRect.X - S(10) - S(64), S(158), S(64), S(28));
                DrawPill(g, refreshRect, "刷新", result != null && result.Pending);
                DrawPill(g, closeRect, "关闭", false);

                using (Font hintFont = font("Microsoft YaHei UI", 8f, FontStyle.Regular))
                using (SolidBrush hintBrush = new SolidBrush(Color.FromArgb(110, 160, 168, 178)))
                    g.DrawString("左键刷新 · 右键关闭", hintFont, hintBrush, S(20), S(164));
            }
            return canvas;
        }

        private static string Safe(double value)
        {
            return double.IsNaN(value) ? "--" : value.ToString("0.00");
        }

        private void DrawPill(Graphics g, Rectangle box, string text, bool busy)
        {
            using (GraphicsPath path = Rounded(box, box.Height / 2))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(busy ? 26 : 34, 255, 255, 255)))
                g.FillPath(fill, path);
            using (Font textFont = font("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(220, 240, 240, 245)))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, textFont, brush, box, format);
        }

        private static GraphicsPath Rounded(Rectangle box, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (box.Width < d) d = Math.Max(1, box.Width);
            path.AddArc(box.X, box.Y, d, d, 180, 90);
            path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
            path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---- input ---------------------------------------------------------

        private string Label
        {
            get { return ProviderLabel; }
        }
        internal string ProviderLabel = "余额";

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { CloseRequested(this, EventArgs.Empty); return; }
            if (e.Button != MouseButtons.Left) return;
            if (closeRect.Contains(e.Location)) { CloseRequested(this, EventArgs.Empty); return; }
            RefreshRequested(this, EventArgs.Empty);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            unloading = true;
            surface.Dispose();
            base.OnFormClosing(e);
        }

        // ---- headless previews for visual review ---------------------------

        internal static void RenderPreviews(Assets assets, string folder)
        {
            Directory.CreateDirectory(folder);
            using (BalanceCard card = new BalanceCard(assets))
            {
                card.ProviderLabel = "DeepSeek";

                BalanceResult ok = new BalanceResult();
                ok.Ok = true; ok.Total = 110.00; ok.Granted = 10; ok.ToppedUp = 100;
                ok.HasBreakdown = true; ok.Currency = "CNY"; ok.Account = "示例账号";
                card.result = ok;
                using (Bitmap b = card.Draw()) b.Save(Path.Combine(folder, "card-ok.png"), ImageFormat.Png);

                card.ProviderLabel = "DSH 账号";
                using (Bitmap b = card.Draw()) b.Save(Path.Combine(folder, "card-dsh.png"), ImageFormat.Png);
                card.ProviderLabel = "DeepSeek";

                card.result = BalanceResult.Loading();
                using (Bitmap b = card.Draw()) b.Save(Path.Combine(folder, "card-loading.png"), ImageFormat.Png);

                card.result = BalanceResult.MissingKey();
                using (Bitmap b = card.Draw()) b.Save(Path.Combine(folder, "card-nokey.png"), ImageFormat.Png);

                BalanceResult bad = new BalanceResult();
                bad.Error = "API Key 无效或无权限（HTTP 401）";
                card.result = bad;
                using (Bitmap b = card.Draw()) b.Save(Path.Combine(folder, "card-error.png"), ImageFormat.Png);

                BalanceResult custom = new BalanceResult();
                custom.Ok = true; custom.Total = 0.42; custom.Currency = "USD";
                card.ProviderLabel = "自定义接口";
                card.result = custom;
                using (Bitmap b = card.Draw()) b.Save(Path.Combine(folder, "card-custom.png"), ImageFormat.Png);
            }
        }
    }
}
