using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace NaiwaDesktop
{
    /// <summary>One DeepSeek Platform grant as stored by DeepSeek Harness.</summary>
    internal sealed class DshCredential
    {
        internal string Token = "";
        internal string Issuer = "https://platform.deepseek.com";
        internal string Source = "";

        internal bool Ready { get { return Token.Length > 0; } }
    }

    /// <summary>
    /// Reads the DeepSeek account that DeepSeek Harness is already signed in with, so the
    /// balance card needs no key of its own. Scope: read one file, send the token to the
    /// issuer only, never write or log it.
    /// </summary>
    internal static class DshAccount
    {
        // Platform accepts these client headers; only x-dsh-auth-token is required.
        internal const string ClientVersion = "1.0.0";
        internal const string Record = "deepseek-account-platform/default";

        private static readonly object gate = new object();
        private static DshCredential cached;
        private static DateTime cachedAt = DateTime.MinValue;
        private static string cachedPath = "";

        /// <summary>Credential store directory: DSH_HOME, else %USERPROFILE%\.dsh.</summary>
        internal static string Home
        {
            get
            {
                try
                {
                    string env = Environment.GetEnvironmentVariable("DSH_HOME");
                    if (!string.IsNullOrEmpty(env) && env.Trim().Length > 0) return env.Trim();
                    string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (profile.Length > 0) return Path.Combine(profile, ".dsh");
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".dsh");
            }
        }

        internal static string CredentialPath { get { return Path.Combine(Home, ".credentials.yaml"); } }

        internal static void Invalidate()
        {
            lock (gate) { cached = null; cachedAt = DateTime.MinValue; }
        }

        /// <summary>Latest stored Platform grant, re-read when the file changes.</summary>
        internal static DshCredential Find()
        {
            lock (gate)
            {
                string path = CredentialPath;
                DateTime modified = DateTime.MinValue;
                try { if (File.Exists(path)) modified = File.GetLastWriteTimeUtc(path); }
                catch { }

                bool fresh = cached != null && path == cachedPath && (DateTime.UtcNow - cachedAt).TotalSeconds < 5;
                if (fresh && (modified == DateTime.MinValue || modified <= cachedAt)) return cached;

                DshCredential found = new DshCredential();
                try
                {
                    if (modified != DateTime.MinValue)
                        found = Parse(File.ReadAllText(path, Encoding.UTF8), path);
                }
                catch { }
                cached = found;
                cachedAt = DateTime.UtcNow;
                cachedPath = path;
                return found;
            }
        }

        /// <summary>Pull the Platform grant out of a Harness credential store document.</summary>
        internal static DshCredential Parse(string text, string source)
        {
            DshCredential credential = new DshCredential();
            credential.Source = source == null ? "" : source;
            if (string.IsNullOrEmpty(text)) return credential;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int start = -1;
            int indent = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0) continue;
                if (string.Equals(trimmed, Record + ":", StringComparison.Ordinal))
                {
                    start = i;
                    indent = 0;
                    while (indent < lines[i].Length && lines[i][indent] == ' ') indent++;
                    break;
                }
            }
            if (start < 0) return credential;

            string token = "";
            string issuer = "";
            for (int i = start + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) continue;
                int depth = 0;
                while (depth < line.Length && line[depth] == ' ') depth++;
                if (depth <= indent) break;

                string body = line.Trim();
                if (body.StartsWith("token:", StringComparison.Ordinal)) token = Value(body.Substring(6));
                else if (body.StartsWith("issuer:", StringComparison.Ordinal)) issuer = Value(body.Substring(7));
            }

            credential.Token = token;
            if (issuer.Length > 0) credential.Issuer = issuer.TrimEnd('/');
            return credential;
        }

        private static string Value(string raw)
        {
            string value = raw.Trim();
            if (value.Length >= 2)
            {
                char first = value[0];
                if ((first == '"' || first == '\'') && value[value.Length - 1] == first)
                    value = value.Substring(1, value.Length - 2);
            }
            return value.Trim();
        }

        // ------------------------------------------------------------ requests

        internal static Dictionary<string, string> Headers(string token)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["x-dsh-auth-token"] = token;
            headers["x-client-platform"] = "desktop-win";
            headers["x-client-version"] = ClientVersion;
            headers["x-client-locale"] = "zh_CN";
            headers["x-client-timezone-offset"] = ((int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalSeconds).ToString(CultureInfo.InvariantCulture);
            headers["x-client-bundle-id"] = "";
            return headers;
        }

        /// <summary>GET a Platform JSON document; false when the transport or status fails.</summary>
        // Turns a transport failure into something a card can print on one line.
        private static string Describe(string url, WebException e)
        {
            string host = new Uri(url).Host;
            string text = e.Message;
            if (e.InnerException != null) text = text + " " + e.InnerException.Message;
            if (text.IndexOf("安全包") >= 0 || text.IndexOf("SSL") >= 0 || text.IndexOf("TLS") >= 0 || text.IndexOf("基础连接") >= 0 || text.IndexOf("凭据") >= 0)
                return "TLS 连接被系统拒绝，请检查网络代理或系统时间，或从桌面双击启动程序";
            if (e.Status == WebExceptionStatus.NameResolutionFailure) return "解析不到 " + host + "，请检查网络";
            if (e.Status == WebExceptionStatus.Timeout) return "连接 " + host + " 超时，请稍后重试";
            if (e.Status == WebExceptionStatus.ConnectFailure) return "连不上 " + host + "，请检查网络";
            return "连不上 " + host + "：" + e.Message;
        }

        internal static bool TryGet(string url, string token, out string text, out int status, out string error)
        {
            text = "";
            status = 0;
            error = "";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.UserAgent = "NaiwaPet/1.0";
            request.Accept = "application/json";
            foreach (KeyValuePair<string, string> header in Headers(token))
            {
                try { request.Headers[header.Key] = header.Value; }
                catch { }
            }
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    status = (int)response.StatusCode;
                    text = reader.ReadToEnd();
                }
                return true;
            }
            catch (WebException e)
            {
                HttpWebResponse response = e.Response as HttpWebResponse;
                if (response == null)
                {
                    error = Describe(url, e);
                    return false;
                }
                status = (int)response.StatusCode;
                try
                {
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        text = reader.ReadToEnd();
                }
                catch { }
                error = status == 401 || status == 403
                    ? "登录已过期，请在 DeepSeek Harness 里重新登录"
                    : "接口返回 HTTP " + status;
                return false;
            }
        }
    }
}
