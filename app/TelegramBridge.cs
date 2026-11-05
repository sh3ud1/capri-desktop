using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CapriDesktop
{
    [DataContract]
    internal sealed class TelegramEnvelope
    {
        [DataMember] public bool ok;
        [DataMember] public TelegramUpdate[] result;
    }

    [DataContract]
    internal sealed class TelegramUpdate
    {
        [DataMember] public long update_id;
        [DataMember] public TelegramMessage message;
    }

    [DataContract]
    internal sealed class TelegramMessage
    {
        [DataMember] public long message_id;
        [DataMember] public TelegramUser from;
        [DataMember] public TelegramChat chat;
        [DataMember] public string text;
        [DataMember] public TelegramUser forward_from;
        [DataMember] public TelegramChat sender_chat;
        [DataMember] public TelegramUser via_bot;
        [DataMember] public string forward_date;
        [DataMember] public object forward_origin;
    }

    [DataContract]
    internal sealed class TelegramUser
    {
        [DataMember] public long id;
        [DataMember] public bool is_bot;
    }

    [DataContract]
    internal sealed class TelegramChat
    {
        [DataMember] public long id;
        [DataMember] public string type;
    }

    // The first release deliberately exposes information only. No shell, arbitrary file access, or generic Capri tool dispatch.
    internal sealed class TelegramBridge : IDisposable
    {
        private readonly string token;
        private readonly long ownerId;
        private readonly object requestLock = new object();
        private HttpWebRequest activeRequest;
        private Thread worker;
        private volatile bool stopping;
        private long offset;

        internal TelegramBridge(string botToken, long allowedUserId)
        {
            if (string.IsNullOrEmpty(botToken) || allowedUserId <= 0) throw new ArgumentException("Telegram is not configured.");
            token = botToken;
            ownerId = allowedUserId;
        }

        internal void Start()
        {
            if (worker != null) return;
            stopping = false;
            worker = new Thread(Poll) { IsBackground = true, Name = "Capri Telegram" };
            worker.Start();
        }

        private void Poll()
        {
            try
            {
                // Discard the pending backlog on startup. Old messages must never become fresh remote instructions.
                TelegramEnvelope latest = RequestUpdates(-1, 1, 0);
                if (latest != null && latest.result != null && latest.result.Length > 0)
                    offset = latest.result[latest.result.Length - 1].update_id + 1;
                while (!stopping)
                {
                    TelegramEnvelope batch = RequestUpdates(offset, 20, 20);
                    if (batch == null || batch.result == null) continue;
                    foreach (TelegramUpdate update in batch.result)
                    {
                        if (stopping) break;
                        if (update == null) continue;
                        offset = Math.Max(offset, update.update_id + 1);
                        TelegramMessage message = update.message;
                        if (!Allowed(message)) continue;
                        string command = (message.text ?? "").Trim();
                        if (command == "/start" || command == "/help")
                            Send(message.chat.id, "Capri remote access is connected. Available commands: /status, /help. Remote actions are not enabled yet.");
                        else if (command == "/status")
                            Send(message.chat.id, "Capri is running. Telegram connection is active.");
                    }
                }
            }
            catch (Exception) { /* Fail closed. Never log a URL containing the bot token. */ }
        }

        private bool Allowed(TelegramMessage message)
        {
            return message != null && message.from != null && !message.from.is_bot
                && message.chat != null && message.chat.type == "private"
                && message.chat.id == message.from.id && message.from.id == ownerId
                && message.forward_from == null && message.sender_chat == null
                && message.via_bot == null && message.forward_date == null
                && message.forward_origin == null && !string.IsNullOrEmpty(message.text);
        }

        private TelegramEnvelope RequestUpdates(long next, int limit, int wait)
        {
            string url = "https://api.telegram.org/bot" + token + "/getUpdates?limit=" + limit + "&timeout=" + wait;
            if (next != 0) url += "&offset=" + next;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = (wait + 10) * 1000;
            request.ReadWriteTimeout = (wait + 10) * 1000;
            request.Proxy = WebRequest.DefaultWebProxy;
            lock (requestLock) { if (stopping) return null; activeRequest = request; }
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                    return (TelegramEnvelope)new DataContractJsonSerializer(typeof(TelegramEnvelope)).ReadObject(stream);
            }
            finally { lock (requestLock) { if (activeRequest == request) activeRequest = null; } }
        }

        private void Send(long chatId, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes("chat_id=" + Uri.EscapeDataString(chatId.ToString()) + "&text=" + Uri.EscapeDataString(text));
            string url = "https://api.telegram.org/bot" + token + "/sendMessage";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = body.Length;
            request.Timeout = 15000;
            using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) { }
        }

        public void Dispose()
        {
            stopping = true;
            lock (requestLock) { if (activeRequest != null) try { activeRequest.Abort(); } catch (Exception) { } }
            if (worker != null && worker != Thread.CurrentThread) worker.Join(1500);
            worker = null;
        }
    }

    internal sealed class TelegramSettingsDialog : Form
    {
        private readonly TextBox tokenBox;
        private readonly Label status;
        private readonly Button pair;
        private readonly Button confirm;
        private readonly long existingId;
        private string code;
        private long foundId;
        private Thread pairingThread;
        private volatile bool cancelled;
        private DateTime expires;

        internal string BotToken { get { return tokenBox.Text.Trim(); } }
        internal long UserId { get { return foundId > 0 ? foundId : existingId; } }

        internal TelegramSettingsDialog(string savedToken, long savedId)
        {
            existingId = savedId;
            foundId = savedId;
            Text = "Capri · Telegram";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new System.Drawing.Size(430, 205);

            Label heading = new Label { Left = 16, Top = 14, Width = 395, Height = 22, Text = "Telegram bot token" };
            tokenBox = new TextBox { Left = 16, Top = 40, Width = 395, UseSystemPasswordChar = true, Text = savedToken ?? "" };
            Label note = new Label { Left = 16, Top = 70, Width = 395, Height = 34, Text = "The token is stored locally using Windows DPAPI. Never share it or commit it to GitHub." };
            status = new Label { Left = 16, Top = 111, Width = 395, Height = 34, Text = savedId > 0 ? "Linked Telegram user ID: " + savedId : "Not linked yet." };
            pair = new Button { Left = 16, Top = 153, Width = 150, Height = 30, Text = "Generate pairing code" };
            confirm = new Button { Left = 176, Top = 153, Width = 150, Height = 30, Text = "Confirm account", Enabled = false };
            Button cancel = new Button { Left = 336, Top = 153, Width = 75, Height = 30, Text = "Close", DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { heading, tokenBox, note, status, pair, confirm, cancel });
            pair.Click += PairClick;
            confirm.Click += delegate
            {
                if (foundId <= 0 || string.IsNullOrEmpty(BotToken)) return;
                DialogResult = DialogResult.OK;
                Close();
            };
            AcceptButton = confirm;
            CancelButton = cancel;
            FormClosing += delegate { cancelled = true; };
        }

        private void PairClick(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(BotToken)) { status.Text = "Paste the bot token first."; return; }
            if (pairingThread != null && pairingThread.IsAlive) return;
            code = MakeCode();
            expires = DateTime.UtcNow.AddMinutes(3);
            cancelled = false;
            foundId = 0;
            confirm.Enabled = false;
            pair.Enabled = false;
            status.Text = "Send this code to your bot in a private chat: " + code + " (valid for 3 minutes)";
            pairingThread = new Thread(PairPoll) { IsBackground = true, Name = "Capri Telegram pairing" };
            pairingThread.Start();
        }

        private static string MakeCode()
        {
            byte[] bytes = new byte[4];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return (100000 + (BitConverter.ToUInt32(bytes, 0) % 900000)).ToString();
        }

        private void PairPoll()
        {
            long offset = 0;
            try
            {
                while (!cancelled && DateTime.UtcNow < expires)
                {
                    string url = "https://api.telegram.org/bot" + BotToken + "/getUpdates?timeout=15&limit=20" + (offset > 0 ? "&offset=" + offset : "");
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Timeout = 25000;
                    TelegramEnvelope batch;
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                        batch = (TelegramEnvelope)new DataContractJsonSerializer(typeof(TelegramEnvelope)).ReadObject(stream);
                    if (batch == null || batch.result == null) continue;
                    foreach (TelegramUpdate update in batch.result)
                    {
                        if (update == null) continue;
                        offset = Math.Max(offset, update.update_id + 1);
                        TelegramMessage message = update.message;
                        if (message == null || message.from == null || message.from.is_bot || message.chat == null
                            || message.chat.type != "private" || message.chat.id != message.from.id
                            || message.forward_from != null || message.sender_chat != null || message.via_bot != null
                            || message.forward_date != null || message.forward_origin != null) continue;
                        if (string.Equals((message.text ?? "").Trim(), code, StringComparison.Ordinal))
                        {
                            long id = message.from.id;
                            if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate
                            {
                                if (cancelled || DateTime.UtcNow >= expires) return;
                                foundId = id;
                                status.Text = "Telegram account detected: " + id + ". Confirm on this PC to finish linking.";
                                confirm.Enabled = true;
                                pair.Enabled = true;
                            });
                            return;
                        }
                    }
                }
                if (!cancelled && !IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate { status.Text = "Pairing code expired. Generate a new one."; pair.Enabled = true; });
            }
            catch (Exception)
            {
                if (!cancelled && !IsDisposed && IsHandleCreated) try { BeginInvoke((Action)delegate { status.Text = "Couldn't reach Telegram. Check the token and connection."; pair.Enabled = true; }); } catch (InvalidOperationException) { }
            }
        }
    }
}
