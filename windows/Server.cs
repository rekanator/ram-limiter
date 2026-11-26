// Talks to the extension. Loopback only, and only our extension in a supported browser is
// answered: neither web pages nor other extensions can send its origin.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RamLimiter
{
#pragma warning disable 0649 // the fields are filled from the extension's JSON
    // What the extension sends with each check.
    sealed class Report
    {
        public sealed class TabCount { public int loaded; public int total; }
        public sealed class Unload
        {
            public double time;
            public string title;
            public string site;
            public string url;
            public int? tab;
        }
        public TabCount tabs;
        public List<string> sites;
        public List<Unload> unloaded;
        public bool? reloaded; // the extension was just installed or reloaded
    }

#pragma warning restore 0649

    sealed class Server
    {
        public const int Port = 47831;
        // The ID fixed by "key" in the extension's manifest. No other extension is answered.
        const string ExtensionOrigin = "chrome-extension://hoblikokhahjeobemjnpnaholafobgki";

        // Both are called on a background thread; the app takes over the connection.
        public Action<TcpClient, Browser, Report> OnStatus;
        public Action<TcpClient, Browser> OnJump;

        TcpListener listener;

        /// False if the port can't be opened.
        public bool Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.ExclusiveAddressUse = true; // no other program may share the port
                listener.Start();
            }
            catch (SocketException) { return false; }
            new Thread(Accept) { IsBackground = true }.Start();
            return true;
        }

        void Accept()
        {
            while (true)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => Read(client));
                }
                catch (SocketException) { Thread.Sleep(100); }
            }
        }

        void Read(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var request = new MemoryStream();
                var chunk = new byte[65536];
                int bodyStart = -1, length = 0;
                Browser browser = null;
                bool jump = false;
                while (true)
                {
                    var left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    client.ReceiveTimeout = left;
                    int count = stream.Read(chunk, 0, chunk.Length);
                    if (count <= 0) break;
                    request.Write(chunk, 0, count);
                    if (request.Length > 1100000) break;

                    if (bodyStart < 0)
                    {
                        var text = Encoding.ASCII.GetString(request.GetBuffer(), 0, (int)request.Length);
                        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (split < 0) continue;
                        var head = text.Substring(0, split).Split(new[] { "\r\n" }, StringSplitOptions.None);
                        var headers = new Dictionary<string, string>();
                        foreach (var line in head.Skip(1))
                        {
                            int colon = line.IndexOf(':');
                            if (colon > 0)
                                headers[line.Substring(0, colon).ToLowerInvariant()] = line.Substring(colon + 1).Trim();
                        }
                        jump = head[0].StartsWith("POST /jump ", StringComparison.Ordinal);
                        string origin, size;
                        if (!(jump || head[0].StartsWith("POST /status ", StringComparison.Ordinal))
                            || !headers.TryGetValue("origin", out origin) || origin != ExtensionOrigin) break;
                        browser = BrowserBehind(client);
                        if (browser == null || !headers.TryGetValue("content-length", out size)
                            || !int.TryParse(size, System.Globalization.NumberStyles.None, null, out length)
                            || length > 1000000) break;
                        bodyStart = split + 4;
                    }

                    if (request.Length - bodyStart >= length)
                    {
                        if (jump) { OnJump(client, browser); return; }
                        var body = Encoding.UTF8.GetString(request.GetBuffer(), bodyStart, length);
                        var report = new JavaScriptSerializer { MaxJsonLength = 2000000 }.Deserialize<Report>(body);
                        if (report == null || report.tabs == null || report.sites == null || report.unloaded == null
                            || report.sites.Contains(null) || report.unloaded.Contains(null)) break;
                        OnStatus(client, browser, report);
                        return;
                    }
                }
            }
            catch (Exception) { } // a bad or broken request: just close it
            client.Close();
        }

        // Browsers share the extension, so the process on the other end says which one this is.
        static Browser BrowserBehind(TcpClient client)
        {
            var from = (IPEndPoint)client.Client.RemoteEndPoint;
            int pid = Native.ConnectionOwner(from.Port, Port);
            if (pid == 0) return null;
            var path = Native.PathOf(pid);
            return Browser.All.FirstOrDefault(b => b.Matches(path));
        }

        /// Replies and closes. False if the other end has gone.
        public static bool Send(TcpClient client, object value)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value));
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                    + "Cache-Control: no-store\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                client.SendTimeout = 2000;
                client.GetStream().Write(head.Concat(body).ToArray(), 0, head.Length + body.Length);
                return true;
            }
            catch (Exception) { return false; }
            finally { client.Close(); }
        }
    }
}
